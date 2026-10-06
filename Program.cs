using MagicOnion.Server;
using MessagePipe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MultiDisplayVCPServer.Messaging;
using MultiDisplayVCPServer.Properties;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiDisplayVCPServer
{
    /// <summary>
    /// Main application class. Handles server lifecycle, client connections, and command execution.
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// A unique GUID to ensure only one instance of the application can run.
        /// </summary>
        private const string AppGuid = "MultiDisplayVCPServer.0D77BA1D-F890-48F0-975F-5E8A91F2681E";

        /// <summary>
        /// Mutex to enforce the single-instance-only rule.
        /// </summary>
        private static Mutex appMutex = null;

        /// <summary>
        /// The main TCP listener for incoming client connections.
        /// </summary>
        private static TcpListener listener;

        /// <summary>
        /// CancellationTokenSource to gracefully shut down the server.
        /// </summary>
        private static CancellationTokenSource cts;

        /// <summary>
        /// The background task that runs the server's listener loop.
        /// </summary>
        private static Task listenerTask;

        /// <summary>
        /// Event that fires when the server's state (Stopped, Running, Restarting) changes.
        /// </summary>
        public static event EventHandler<int> ServerStateChanged;

        /// <summary>
        /// Event that fires to send a log message to the UI.
        /// </summary>
        public static event EventHandler<string> LogMessageReceived;

        /// <summary>
        /// Logs a message to the console and to the main UI form if it's open.
        /// </summary>
        /// <param name="message">The message to log.</param>
        private static void Log(string message)
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Debug.WriteLine(logEntry);
            if (Application.OpenForms.Cast<Form>().Any(f => f is MainForm))
            {
                LogMessageReceived?.Invoke(null, logEntry);
            }
        }

        private static ServerStatus _monitorCache = new();
        private static volatile bool _cacheWarming = true;
        private static Task? _cacheWarmTask;
        private static readonly object _cacheLock = new();
        private static readonly char[] _pipeDelimiter = new[] { '|' };
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>
        /// Indicates whether the initial background monitor capability scan is still running.
        /// </summary>
        public static bool IsCacheWarming => _cacheWarming;

        /// <summary>
        /// Waits for the initial monitor capability scan to complete, up to timeoutMs.
        /// </summary>
        public static async Task EnsureCacheWarmedAsync(int timeoutMs = 15000)
        {
            var task = _cacheWarmTask;
            if (task != null && !task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(timeoutMs));
            }
        }

        /// <summary>
        /// The ASP.NET Core WebApplication hosting the MagicOnion gRPC endpoint.
        /// Runs alongside the existing TCP listener (dual-mode).
        /// </summary>
        private static WebApplication? _grpcApp;

        /// <summary>
        /// Returns a snapshot of the current monitor cache.
        /// Returns a "warming up" status if the initial background scan has not yet completed.
        /// Used by MonitorController.GetCachedCapabilities() for the gRPC path.
        /// </summary>
        public static ServerStatus GetCachedStatus()
        {
            if (_cacheWarming)
                return new ServerStatus { Message = "WARMING: Monitor cache is still initializing. Please retry shortly." };

            lock (_cacheLock)
            {
                return _monitorCache;
            }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase) || a.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
            {
                GetMonitorCapabilities();
                return;
            }

            Log("Main() started.");
            bool createdNew = false;
            try
            {
                // Ensure only one instance of the app is running
                Log("Checking for existing application instance (Mutex)...");
                appMutex = new Mutex(true, $"Global\\{AppGuid}", out createdNew);
            }
            catch (Exception)
            {
                createdNew = false;
            }

            if (!createdNew)
            {
                Log("Another instance is already running. Showing message box.");
                MessageBox.Show("Another instance of Multi-Connection Monitor Server is already running.", "Application Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Log("Main() exiting.");
                return;
            }

            Log("Mutex check passed. Starting application.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // --- THIS IS THE NEW LOGIC ---
            // 1. Run the SplashForm.
            //    This makes the SplashForm the main UI thread.
            Log("Running SplashForm.");
            Application.Run(new SplashForm());
            // --- END NEW LOGIC ---

            Log("SplashForm closed. Releasing mutex.");
            appMutex.ReleaseMutex();
            Log("Main() finished.");
        }

        /// <summary>
        /// Starts the TCP listener background task.
        /// </summary>
        public static void StartServerLoop()
        {
            Log("StartServerLoop() started.");
            if (listenerTask != null && !listenerTask.IsCompleted)
            {
                Log("Attempted to start server, but it is already running or busy.");
                return;
            }
            Log("Creating new CancellationTokenSource and starting ListenerLoopAsync task.");
            cts = new CancellationTokenSource();
            listenerTask = Task.Run(() => ListenerLoopAsync(cts.Token));
            Log("Server startup initiated...");

            // Also start the MagicOnion gRPC host in dual-mode alongside the TCP server
            int grpcPort = Settings.Default.GrpcPort;
            Log($"Starting gRPC host on port {grpcPort}...");
            _ = StartGrpcHostAsync(grpcPort, cts.Token);
        }

        /// <summary>
        /// Signals the TCP listener to stop and cancels the background task.
        /// </summary>
        public static void ShutdownServer()
        {
            Log("ShutdownServer() started.");
            cts?.Cancel();
            Log("CancellationTokenSource cancel requested.");
            listener?.Stop();
            Log("TCP listener stop requested.");

            // Also stop the MagicOnion gRPC host
            Log("Requesting gRPC host stop...");
            _ = StopGrpcHostAsync();
            Log("ShutdownServer() finished.");
        }

        /// <summary>
        /// Shuts down the server, waits, and then starts it again.
        /// </summary>
        public static void RestartServer()
        {
            Log("RestartServer() started.");
            Settings.Default.ServerState = 2; // Set state to "Restarting"
            Settings.Default.Save();
            ServerStateChanged?.Invoke(null, 2);
            Log("Server state set to 2 (Restarting).");

            Log("Calling ShutdownServer().");
            ShutdownServer();

            Log("Waiting for 150ms...");
            Thread.Sleep(150);

            Log("Calling StartServerLoop().");
            StartServerLoop();
            Log("Server restart sequence completed.");
        }

        /// <summary>
        /// The main server loop that runs on a background thread.
        /// </summary>
        /// <param name="token">The cancellation token to signal shutdown.</param>
        private static async Task ListenerLoopAsync(CancellationToken token)
        {
            Log("ListenerLoopAsync() started on background thread.");
            bool serverStartedSuccessfully = false;
            try
            {
                SetServerState(2); // "Busy/Restarting"

                int port = Settings.Default.Port;
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                serverStartedSuccessfully = true;

                // --- THIS LOG IS MOVED UP ---
                Log($"Server listener started on port {port}.");

                // Mark the server as Running immediately so the splash screen closes
                // and the UI is responsive. The monitor cache builds in the background.
                Log("TCP listener bound. Setting server state to 1 (Running).");
                SetServerState(1);

                // Build the cache in the background — GET_CAPS will return a
                // "warming up" message until the first scan completes.
                Log("Starting background monitor cache build...");
                _cacheWarmTask = BuildMonitorCacheAsync();

                while (!token.IsCancellationRequested)
                {
                    // ...
                    TcpClient client = await listener.AcceptTcpClientAsync(token);
                    if (client != null)
                    {
                        Log($"Client connected: {client.Client.RemoteEndPoint}");
                    }

                    // --- FIX for CS4014: Assign task to discard '_' ---
                    // This tells the compiler we are *intentionally* not awaiting
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Server listener stopped gracefully via token cancellation.");
            }
            catch (SocketException ex) when (ex.ErrorCode == 10004)
            {
                Log("Server listener stopped gracefully (SocketException 10004).");
            }
            catch (Exception ex)
            {
                Log($"Server failed to start or crashed: {ex.Message}");
                if (!serverStartedSuccessfully)
                {
                    Log("Server failed during initial startup.");
                    throw;
                }
            }
            finally
            {
                Log("ListenerLoopAsync() cleaning up...");
                listener?.Stop();
                Settings.Default.ServerState = 0; // "Stopped"
                Settings.Default.Save();
                Log("Server state set to 0 (Stopped).");
                if (!token.IsCancellationRequested)
                {
                    Log("Firing ServerStateChanged event (0).");
                    ServerStateChanged?.Invoke(null, 0);
                }
                Log("ListenerLoopAsync() finished.");
            }
        }

        /// <summary>
        /// Runs the slow, brute-force scan to discover all monitors and capabilities
        /// and stores the result in the static _monitorCache.
        /// </summary>
        public static async Task BuildMonitorCacheAsync()
        {
            Log("BuildMonitorCacheAsync() started.");
            Log("Starting monitor capability scan...");
            var sw = Stopwatch.StartNew();

            // Run the synchronous, slow scan on a background thread
            Log("Awaiting Task.Run(GetMonitorCapabilities)...");
            var newStatus = await Task.Run(() => GetMonitorCapabilities());
            Log("GetMonitorCapabilities task completed.");
            sw.Stop();

            // Safely replace the old cache with the new one and clear the warming flag
            Log("Safely replacing old cache with new cache...");
            lock (_cacheLock)
            {
                Log("Cache lock acquired.");
                _monitorCache = newStatus;
                Log("Cache lock released.");
            }
            _cacheWarming = false;
            Log($"Monitor scan complete in {sw.ElapsedMilliseconds}ms. Found {_monitorCache.Monitors.Count} monitors.");
            Log("BuildMonitorCacheAsync() finished.");
        }

        /// <summary>
        /// Updates a single value in the cache after a successful SET command.
        /// </summary>
        public static void UpdateCache(string pnpId, byte vcpCode, uint newValue)
        {
            Log($"UpdateCache() started for {pnpId}: 0x{vcpCode:X2} = {newValue}");
            lock (_cacheLock)
            {
                Log("Cache lock acquired.");
                var monitor = _monitorCache.Monitors.FirstOrDefault(m =>
                    m.DeviceID.Equals(pnpId, StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Equals(pnpId, StringComparison.OrdinalIgnoreCase) ||
                    m.DeviceID.Contains(pnpId, StringComparison.OrdinalIgnoreCase));
                if (monitor != null)
                {
                    var feature = monitor.Capabilities.FirstOrDefault(f => f.Code == vcpCode);
                    if (feature != null)
                    {
                        feature.CurrentValue = newValue;
                        Log($"Cache updated for {pnpId}: 0x{vcpCode:X2} set to {newValue}");
                    }
                    else
                    {
                        Log($"Cache update warning: Feature 0x{vcpCode:X2} not found for monitor {pnpId}.");
                    }
                }
                else
                {
                    Log($"Cache update warning: Monitor {pnpId} not found in cache.");
                }
            }
            Log("Cache lock released.");
            Log("UpdateCache() finished.");
        }

        /// <summary>
        /// Handles a single connected client.
        /// </summary>
        /// <param name="client">The connected TcpClient.</param>
        /// <param name="token">The cancellation token.</param>
        private static async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            string remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown Client";
            Log($"HandleClientAsync() started for {remoteEndPoint}.");
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    Log($"Reading from stream for {remoteEndPoint}...");
                    byte[] buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    string receivedData = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                    // Log the command part of the request, which is safer than logging the whole string
                    string loggableData = receivedData;
                    if (receivedData.Contains('|'))
                    {
                        // --- FIX for CA1861: Use the cached delimiter ---
                        var partsForLog = receivedData.Split(_pipeDelimiter, 3);
                        loggableData = partsForLog.Length == 3 ? partsForLog[2] : receivedData;
                    }
                    Log($"Received request from {remoteEndPoint}: '{loggableData}'");

                    if (string.IsNullOrEmpty(receivedData))
                    {
                        Log($"Empty request from {remoteEndPoint}. Closing connection.");
                        return;
                    }

                    // Protocol: timestamp|hash_base64|command
                    string[] parts = receivedData.Split(_pipeDelimiter, 3);
                    string responseMessage;
                    bool isJson = false;

                    if (parts.Length == 3)
                    {
                        Log($"Request from {remoteEndPoint} has 3 parts. Validating hash...");
                        string timestampStr = parts[0];
                        string hashBase64 = parts[1];
                        string command = parts[2];
                        string requiredPassword = Settings.Default.Password;

                        if (ValidateHash(timestampStr, hashBase64, command, requiredPassword))
                        {
                            Log("Hash validated. Executing command.");
                            responseMessage = ExecuteDdcCiCommand(command, out isJson);
                        }
                        else
                        {
                            responseMessage = "ERROR: Invalid Hash.";
                            Log($"Authentication failed for {remoteEndPoint}. Hash mismatch or stale timestamp.");
                        }
                    }
                    else
                    {
                        responseMessage = "ERROR: Invalid request format.";
                        Log($"Authentication failed for {remoteEndPoint}. Invalid format (expected 3 parts, got {parts.Length}).");
                    }

                    Log($"Sending response to {remoteEndPoint}...");
                    await SendResponseAsync(stream, responseMessage, isJson, token);
                }
                catch (OperationCanceledException) { }
                catch (IOException ex) when (ex.InnerException is SocketException se && se.ErrorCode == 10054)
                {
                    Log($"Client {remoteEndPoint} disconnected abruptly (SocketError 10054).");
                }
                catch (Exception ex)
                {
                    Log($"Client handling error for {remoteEndPoint}: {ex.Message}");
                    if (stream.CanWrite)
                    {
                        Log("Attempting to send server error message to client.");
                        await SendResponseAsync(stream, "SERVER ERROR: An unexpected server error occurred.", false, token);
                    }
                }
                finally
                {
                    Log($"Client connection closed: {remoteEndPoint}.");
                    Log($"HandleClientAsync() finished for {remoteEndPoint}.");
                }
            }
        }

        /// <summary>
        /// Validates an incoming request's HMAC hash and timestamp.
        /// </summary>
        /// <param name="timestampStr">The client-provided Unix timestamp string.</param>
        /// <param name="hashBase64">The client-provided HMAC-SHA256 hash.</param>
        /// <param name="command">The command being executed.</param>
        /// <param name="password">The shared secret (password) used to generate the hash.</param>
        /// <returns>True if the hash and timestamp are valid, otherwise false.</returns>
        private static bool ValidateHash(string timestampStr, string hashBase64, string command, string password)
        {
            Log("ValidateHash() started.");
            try
            {
                // 1. Check timestamp
                Log("Checking timestamp...");
                if (!long.TryParse(timestampStr, NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp))
                {
                    Log("Timestamp parse failed.");
                    return false;
                }

                var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                var now = DateTime.UtcNow;

                // Allow a 30-second window
                if (requestTime < now.AddSeconds(-30) || requestTime > now.AddSeconds(30))
                {
                    Log($"Hash validation failed: Stale timestamp. Request: {requestTime}, Server: {now}");
                    return false;
                }
                Log("Timestamp is valid.");

                // 2. Check hash
                Log("Checking hash...");
                using (var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(password)))
                {
                    string messageToHash = command + timestampStr;
                    byte[] computedHashBytes = hmac.ComputeHash(Encoding.ASCII.GetBytes(messageToHash));
                    string computedHashBase64 = Convert.ToBase64String(computedHashBytes);

                    if (computedHashBase64 != hashBase64)
                    {
                        Log($"Hash validation failed: Hash mismatch. Expected: {computedHashBase64}, Got: {hashBase64}");
                        return false;
                    }
                }
                Log("Hash is valid.");
                Log("ValidateHash() finished successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error during hash validation: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Parses and executes a validated command from a client.
        /// </summary>
        /// <param name="command">The command to execute (e.g., "GET_CAPS" or "SET:ID:CODE:VALUE").</param>
        /// <param name="isJson">Output parameter; set to true if the response is JSON.</param>
        /// <returns>A string response for the client.</returns>
        private static string ExecuteDdcCiCommand(string command, out bool isJson)
        {
            Log($"ExecuteDdcCiCommand() started. Command: {command}");
            isJson = false;

            if (command.Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                Log("Command is PING. Returning OK.");
                Log("ExecuteDdcCiCommand() finished.");
                return "OK: PONG";
            }
            else if (command.Equals("GET_CAPS", StringComparison.OrdinalIgnoreCase))
            {
                isJson = true;

                if (_cacheWarming)
                {
                    try
                    {
                        EnsureCacheWarmedAsync(10000).GetAwaiter().GetResult();
                    }
                    catch { }
                }

                Log("Executing GET_CAPS from cache.");
                lock (_cacheLock)
                {
                    Log("Cache lock acquired for GET_CAPS.");

                    // --- FIX for CA1869: Use the cached JSON options ---
                    string jsonResponse = JsonSerializer.Serialize(_monitorCache, _jsonOptions);

                    Log("Cache lock released for GET_CAPS.");
                    // ...
                    return jsonResponse;
                }
            }
            else if (command.StartsWith("SET:", StringComparison.OrdinalIgnoreCase))
            {
                Log("Command is SET. Parsing...");
                string[] parts = command.Split(':');

                if (parts.Length == 4 &&
                    uint.TryParse(parts[2], out uint vcpCode) &&
                    uint.TryParse(parts[3], out uint vcpValue))
                {
                    string targetPnP_ID = parts[1]; // This is the stable Model ID
                    Log($"Parsed SET: ID={targetPnP_ID}, Code={vcpCode}, Value={vcpValue}");

                    Log($"Finding handle for PnP_ID: {targetPnP_ID}");
                    IntPtr targetHandle = FindMonitorHandle(targetPnP_ID);

                    if (targetHandle != (IntPtr)(-1))
                    {
                        Log($"Found handle {targetHandle}. Attempting to set VCP feature...");
                        try
                        {
                            bool success = MonitorController.SetVCPFeature(targetHandle, (byte)vcpCode, vcpValue);
                            Log($"SetVCPFeature returned: {success}");

                            if (success)
                            {
                                Log("SET successful. Updating cache.");
                                UpdateCache(targetPnP_ID, (byte)vcpCode, vcpValue);
                            }

                            string response = success
                                ? $"OK: VCP Code 0x{vcpCode:X2} set to {vcpValue} on {targetPnP_ID}."
                                : "ERROR: DDC/CI command failed.";
                            Log($"ExecuteDdcCiCommand() finished. Response: {response}");
                            return response;
                        }
                        finally
                        {
                            Log($"Destroying handle {targetHandle}.");
                            MonitorController.DestroyPhysicalMonitor(targetHandle);
                        }
                    }
                    else
                    {
                        Log($"Error: Monitor ID {targetPnP_ID} not found.");
                        Log("ExecuteDdcCiCommand() finished.");
                        return $"ERROR: Monitor ID {targetPnP_ID} not found.";
                    }
                }
                else
                {
                    Log("Error: Invalid SET command format.");
                    Log("ExecuteDdcCiCommand() finished.");
                    return "ERROR: Invalid SET command format. Use SET:ID:CODE:VALUE.";
                }
            }
            else if (command.Equals("REFRESH_CACHE", StringComparison.OrdinalIgnoreCase))
            {
                Log("Manual REFRESH_CACHE command received. Rebuilding cache in background.");
                _ = BuildMonitorCacheAsync();
                Log("ExecuteDdcCiCommand() finished.");
                return "OK: Cache refresh initiated.";
            }
            else
            {
                Log($"Error: Invalid command '{command}'.");
                Log("ExecuteDdcCiCommand() finished.");
                return "ERROR: Invalid Command. Send GET_CAPS or SET:ID:CODE:VALUE.";
            }
        }

        #region Monitor_DDC/CI_Helpers

        /// <summary>
        /// Finds the DDC/CI handle (hMonitor) for a monitor based on its stable PnP Model ID.
        /// </summary>
        /// <param name="targetPnP_ID">The stable Model ID (e.g., "ACR0D1D").</param>
        /// <returns>A handle to the physical monitor, or -1 if not found.</returns>
        static IntPtr FindMonitorHandle(string targetPnP_ID)
        {
            Log($"FindMonitorHandle() started for PnP_ID: {targetPnP_ID}");
            if (string.IsNullOrEmpty(targetPnP_ID))
            {
                Log("Error: targetPnP_ID is null or empty.");
                return (IntPtr)(-1);
            }

            var pnpMap = MonitorWmiHelper.GetPnPMonitorMap();
            pnpMap.TryGetValue(targetPnP_ID, out string? targetDescription);

            // Enumerate all physical monitors with native PnP ID detection
            Log("Enumerating all physical monitors...");
            var monitors = MonitorController.EnumeratePhysicalMonitors(MonitorWmiHelper.GetMonitorPnPMap());
            Log($"Found {monitors.Count} physical monitors.");
            IntPtr foundHandle = (IntPtr)(-1);

            // Match monitor directly by PnP ID, DeviceID, or Description
            Log("Searching for monitor matching PnP ID...");
            foreach (var monitor in monitors)
            {
                bool matches = monitor.PnP_ID.Equals(targetPnP_ID, StringComparison.OrdinalIgnoreCase)
                            || monitor.DeviceID.Equals(targetPnP_ID, StringComparison.OrdinalIgnoreCase)
                            || monitor.PnP_ID.Contains(targetPnP_ID, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(targetDescription) && monitor.Description.Equals(targetDescription, StringComparison.OrdinalIgnoreCase));

                if (matches)
                {
                    foundHandle = monitor.Handle;
                    Log($"Found matching handle {foundHandle} for target {targetPnP_ID} ({monitor.Description})");
                    break;
                }
            }

            // Clean up other handles
            Log("Cleaning up other monitor handles...");
            foreach (var monitor in monitors)
            {
                if (monitor.Handle != foundHandle)
                {
                    MonitorController.DestroyPhysicalMonitor(monitor.Handle);
                }
            }
            Log($"FindMonitorHandle() finished. Returning handle: {foundHandle}");
            return foundHandle;
        }

        /// <summary>
        /// Gathers capabilities from all DDC/CI-compliant monitors, builds the status cache,
        /// and dumps a full diagnostic breakdown to monitor_dump.json.
        /// </summary>
        /// <returns>A ServerStatus object representing all discovered monitors.</returns>
        static ServerStatus GetMonitorCapabilities()
        {
            Log("GetMonitorCapabilities() started.");
            var sw = Stopwatch.StartNew();

            // 1. Get the map of DDC/CI Description -> PnP Model ID
            Log("Getting WMI map (from cache or new query)...");
            var pnpMap = MonitorWmiHelper.GetMonitorPnPMap();
            Log($"Found {pnpMap.Count} monitors in WMI.");

            // 2. Enumerate all physical monitors
            Log("Enumerating all physical monitors...");
            var physicalMonitors = MonitorController.EnumeratePhysicalMonitors(pnpMap);
            Log($"Found {physicalMonitors.Count} physical monitor handles.");

            var monitorList = new List<MonitorInfo>();
            var monitorDiags = new List<Dictionary<string, object?>>();

            // Sequential enumeration avoids I2C bus contention across display adapter ports
            foreach (var pMon in physicalMonitors)
            {
                Log($"Processing monitor: {pMon.Description} (PnP: {pMon.PnP_ID}, Device: {pMon.DeviceID})");
                IntPtr hMonitor = pMon.Handle;
                var monDiag = new Dictionary<string, object?>
                {
                    ["PnP_ID"] = pMon.PnP_ID,
                    ["Description"] = pMon.Description,
                    ["DeviceID"] = pMon.DeviceID,
                    ["Handle"] = "0x" + hMonitor.ToString("X")
                };

                try
                {
                    // 3. Check for stable PnP_ID
                    if (string.IsNullOrEmpty(pMon.PnP_ID))
                    {
                        Log($"Skipping monitor {pMon.Description}: Could not find matching PnP_ID.");
                        monDiag["Status"] = "Skipped";
                        monDiag["Reason"] = "Missing PnP_ID";
                        monitorDiags.Add(monDiag);
                        continue;
                    }

                    // 4. Get capabilities string length
                    Log($"Getting capabilities string length for {pMon.Description}...");
                    uint length = 0;
                    bool okLen = MonitorController.GetCapabilitiesStringLength(hMonitor, ref length);
                    int lastError = Marshal.GetLastWin32Error();
                    monDiag["CapabilitiesLengthSuccess"] = okLen;
                    monDiag["CapabilitiesLength"] = length;
                    monDiag["Win32Error"] = lastError;

                    if (!okLen || length == 0)
                    {
                        Log($"Skipping monitor {pMon.Description}: Failed to get capabilities string length (Win32 Error: {lastError}).");
                        monDiag["Status"] = "Skipped";
                        monDiag["Reason"] = $"GetCapabilitiesStringLength failed or returned 0 (Win32 Error {lastError})";
                        monitorDiags.Add(monDiag);
                        continue;
                    }

                    Log($"Capabilities string length: {length}");
                    StringBuilder sb = new StringBuilder((int)length);
                    bool okReply = MonitorController.CapabilitiesRequestAndCapabilitiesReply(hMonitor, sb, length);
                    monDiag["CapabilitiesReplySuccess"] = okReply;
                    monDiag["CapabilitiesString"] = sb.ToString();

                    if (!okReply)
                    {
                        Log($"Skipping monitor {pMon.Description}: Failed to get capabilities string.");
                        monDiag["Status"] = "Skipped";
                        monDiag["Reason"] = "CapabilitiesRequestAndCapabilitiesReply failed";
                        monitorDiags.Add(monDiag);
                        continue;
                    }

                    Log($"Capabilities string: {sb}");

                    // 5. Discover all VCP features
                    Log($"Discovering VCP features for {pMon.Description}...");
                    List<VcpFeature> features = DiscoverAllVcpFeatures(hMonitor, sb.ToString());
                    monDiag["FeaturesCount"] = features.Count;

                    // The Dummy Filter: If it has less than 10 features, it's a capture card.
                    if (features.Count < 10)
                    {
                        Log($"Skipping {pMon.Description}: Only found {features.Count} features. Likely a capture card.");
                        monDiag["Status"] = "Skipped";
                        monDiag["Reason"] = $"Only found {features.Count} features (< 10). Likely a capture card.";
                        monitorDiags.Add(monDiag);
                        continue;
                    }

                    Log($"Discovered {features.Count} features for {pMon.Description}. Adding to cache.");
                    monDiag["Status"] = "Included";
                    monDiag["Features"] = features.Select(f => new
                    {
                        Code = $"0x{f.Code:X2}",
                        f.Name,
                        f.Type,
                        f.CurrentValue,
                        f.MaximumValue,
                        NonContinuousValues = f.NonContinuousValues
                    }).ToList();
                    monitorDiags.Add(monDiag);

                    monitorList.Add(new MonitorInfo
                    {
                        DeviceID = pMon.PnP_ID,
                        Description = pMon.Description,
                        Capabilities = features
                    });
                }
                catch (Exception ex)
                {
                    Log($"Exception processing monitor {pMon.Description}: {ex.Message}");
                    monDiag["Status"] = "Error";
                    monDiag["Exception"] = ex.ToString();
                    monitorDiags.Add(monDiag);
                }
                finally
                {
                    Log($"Destroying handle for {pMon.Description}.");
                    MonitorController.DestroyPhysicalMonitor(hMonitor);
                }
            }

            monitorList = monitorList.OrderBy(m => m.Description).ToList();
            sw.Stop();

            ServerStatus status = new()
            {
                Monitors = monitorList,
                Message = $"OK: Found {monitorList.Count} DDC/CI compliant monitors."
            };

            // Write full diagnostic dump to monitor_dump.json
            try
            {
                var fullDump = new
                {
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    ScanDurationMs = sw.ElapsedMilliseconds,
                    WmiCount = pnpMap.Count,
                    WmiPnpMap = pnpMap,
                    PhysicalMonitorsEnumeratedCount = physicalMonitors.Count,
                    EnumeratedMonitors = monitorDiags,
                    FinalStatus = status
                };

                string dumpJson = JsonSerializer.Serialize(fullDump, _jsonOptions);

                // 1. Next to the server executable
                string appDirDump = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "monitor_dump.json");
                File.WriteAllText(appDirDump, dumpJson);
                Log($"Diagnostic dump written to {appDirDump}");

                // 2. In LocalAppData
                string localAppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiDisplayVCPServer");
                Directory.CreateDirectory(localAppDir);
                string localAppDump = Path.Combine(localAppDir, "monitor_dump.json");
                File.WriteAllText(localAppDump, dumpJson);
                Log($"Diagnostic dump written to {localAppDump}");
            }
            catch (Exception ex)
            {
                Log($"Error writing diagnostic dump: {ex.Message}");
            }

            Log($"GetMonitorCapabilities() finished in {sw.ElapsedMilliseconds}ms. Found {monitorList.Count} monitor(s).");
            return status;
        }

        /// <summary>
        /// Helper to parse a hex string to a uint.
        /// </summary>
        static bool TryParseVcpHex(string hex, out uint result)
        {
            // --- Skipped logging for this small, noisy utility ---
            return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out result);
        }


        [GeneratedRegex(@"vcp\((.*?)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex VcpCapabilityRegex();

        /// <summary>
        /// Iterates through all possible VCP codes (0-255) to discover a monitor's features.
        /// </summary>
        /// <param name="hMonitor">The monitor's handle.</param>
        /// <param name="capString">The monitor's capabilities string (for parsing non-continuous values).</param>
        /// <returns>A list of VcpFeature objects.</returns>
        static List<VcpFeature> DiscoverAllVcpFeatures(IntPtr hMonitor, string capString)
        {
            Log($"DiscoverAllVcpFeatures() started for handle {hMonitor}.");
            List<VcpFeature> features = new();

            // List of VCP codes to ignore (reserved, table, or buggy)
            var exclusionList = new HashSet<byte>
            {
                0x04, 0x05, 0x08, 0xAC, 0xAE, 0xB6, 0xC0, 0xC8, 0xC9, 0xDF, 0x02, 0x52, 0x82,
                0xB2, 0xC6, 0xCA, 0xCC, 0xDC,
            };
            for (int i = 224; i <= 255; i++) { exclusionList.Add((byte)i); } // 0xE0-0xFF

            // Known non-continuous VCP codes
            var nonContinuousList = new HashSet<byte> { 0x14, 0x60, 0x8D, 0xD6, };

            // Parse non-continuous value maps from the capability string (e.g., "60(01 02 0F)")
            Log("Parsing capability string for non-continuous values...");
            Dictionary<byte, string> nonContinuousMap = new Dictionary<byte, string>();
            Match vcpMatch = VcpCapabilityRegex().Match(capString);
            if (vcpMatch.Success)
            {
                string vcpContent = vcpMatch.Groups[1].Value.Trim();
                string[] vcpTokens = vcpContent.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string token in vcpTokens)
                {
                    int nonContStart = token.IndexOf('(');
                    if (nonContStart != -1)
                    {
                        if (TryParseVcpHex(token.Substring(0, nonContStart), out uint vcpCode))
                        {
                            nonContinuousMap[(byte)vcpCode] = token.Substring(nonContStart + 1).TrimEnd(')');
                        }
                    }
                }
            }
            Log($"Found {nonContinuousMap.Count} non-continuous value maps.");

            // Brute-force check all 256 VCP codes
            Log("Starting brute-force scan for VCP codes 0-255...");
            for (int vcpCode = 0; vcpCode <= 255; vcpCode++)
            {
                uint currentValue = 0;
                uint maximumValue = 0;
                MonitorController.MONITOR_CAPABILITIES_REQUEST_TYPE type = 0;

                if (MonitorController.GetVCPFeatureAndVCPFeatureReply(hMonitor, (byte)vcpCode, ref type, ref currentValue, ref maximumValue))
                {
                    if (exclusionList.Contains((byte)vcpCode))
                    {
                        continue;
                    }

                    VcpFeature feature = new VcpFeature
                    {
                        Code = (byte)vcpCode,
                        Name = GetVcpFeatureName((byte)vcpCode),
                        ReadWrite = true, // We assume Read/Write if GetVCPFeature succeeded
                        CurrentValue = currentValue,
                        MaximumValue = maximumValue,
                    };

                    if (nonContinuousList.Contains((byte)vcpCode))
                    {
                        feature.Type = "Non-Continuous";
                        if (nonContinuousMap.TryGetValue((byte)vcpCode, out string valuesStr))
                        {
                            string[] values = valuesStr.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string valHex in values)
                            {
                                if (TryParseVcpHex(valHex, out uint val))
                                {
                                    if (feature.NonContinuousValues == null) feature.NonContinuousValues = new Dictionary<uint, string>();
                                    feature.NonContinuousValues[val] = val.ToString(); // TODO: Add friendly names if available
                                }
                            }
                        }
                    }
                    else
                    {
                        feature.Type = "Continuous";
                    }
                    features.Add(feature);
                }
            }
            Log($"Brute-force scan complete. Found {features.Count} features.");
            Log("DiscoverAllVcpFeatures() finished.");
            return features;
        }

        /// <summary>
        /// Provides a friendly name for a known VCP code.
        /// </summary>
        static string GetVcpFeatureName(byte code)
        {
            // --- Skipped logging for this simple, high-frequency utility ---
            return code switch
            {
                0x02 => "New Control Value",
                0x04 => "Restore Factory Defaults",
                0x05 => "Restore Factory Luminance/Contrast",
                0x08 => "Restore Factory Color Defaults",
                0x0B => "Color Temperature Increment",
                0x0C => "Color Temperature Request",
                0x10 => "Brightness",
                0x12 => "Contrast",
                0x14 => "Select Color Preset",
                0x16 => "Video Gain (Drive): Red",
                0x18 => "Video Gain (Drive): Green",
                0x1A => "Video Gain (Drive): Blue",
                0x59 => "6 Axis Saturation Control: Red",
                0x5A => "6 Axis Saturation Control: Yellow",
                0x5B => "6 Axis Saturation Control: Green",
                0x5C => "6 Axis Saturation Control: Cyan",
                0x5D => "6 Axis Saturation Control: Blue",
                0x5E => "6 Axis Saturation Control: Magenta",
                0x60 => "Input Select",
                0x62 => "Audio: Speaker Volume",
                0x6C => "Video Black Level: Red",
                0x6E => "Video Black Level: Green",
                0x70 => "Video Black Level: Blue",
                0x8D => "Audio Mute / Screen Blank",
                0x9B => "6 Axis Color Control: Red",
                0x9C => "6 Axis Color Control: Yellow",
                0x9D => "6 Axis Color Control: Green",
                0x9E => "6 Axis Color Control: Cyan",
                0x9F => "6 Axis Color Control: Blue",
                0xA0 => "6 Axis Color Control: Magenta",
                0xAC => "Horizontal Frequency",
                0xAE => "Vertical Frequency",
                0xB6 => "Display Technology Type",
                0xC0 => "Display Usage Time",
                0xC6 => "Application Enable Key",
                0xC8 => "Display Controller ID",
                0xC9 => "Display Firmware Level",
                0xCC => "OSD Language",
                0xD6 => "Power Mode",
                0xDF => "VCP Version",
                _ => $"VCP Code 0x{code:X2}",
            };
        }
        #endregion

        /// <summary>
        /// Sends a response back to the client over the network stream.
        /// </summary>
        private static async Task SendResponseAsync(NetworkStream stream, string message, bool isJson, CancellationToken token)
        {
            string logMessage = isJson ? "JSON Data" : message;
            Log($"SendResponseAsync() started. Sending: {logMessage}");
            byte[] response = Encoding.ASCII.GetBytes(message);
            await stream.WriteAsync(response, 0, response.Length, token);
            Log("SendResponseAsync() finished.");
        }

        /// <summary>
        /// Saves the current application settings to disk.
        /// </summary>
        public static void SaveSettings()
        {
            Log("SaveSettings() started.");
            try
            {
                Settings.Default.Save();
                Log("Application settings saved successfully.");
            }
            catch (Exception ex)
            {
                Log($"Error saving settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables or disables the application's automatic startup via the Windows Registry.
        /// </summary>
        /// <param name="enable">If true, adds to startup; otherwise, removes it.</param>
        public static void SetStartup(bool enable)
        {
            Log($"SetStartup() started. Enable: {enable}");
            const string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
            const string appName = "MultiDisplayVCPServer";

            try
            {
                Log("Opening registry key HKCU\\" + runKey);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (key == null)
                    {
                        Log("Error: Registry key not found.");
                        return;
                    }

                    if (enable)
                    {
                        string executablePath = Environment.ProcessPath;

                        if (string.IsNullOrEmpty(executablePath))
                        {
                            Log("Error: Could not determine executable path for startup.");
                            return;
                        }

                        string registryValue = $"\"{executablePath}\"";
                        Log($"Setting registry value '{appName}' to '{registryValue}'");
                        key.SetValue(appName, registryValue);
                        Log("Application added to Windows startup.");
                    }
                    else
                    {
                        if (key.GetValue(appName) != null)
                        {
                            Log($"Deleting registry value '{appName}'.");
                            key.DeleteValue(appName);
                            Log("Application removed from Windows startup.");
                        }
                        else
                        {
                            Log("Registry value not found, no action needed.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error modifying registry for startup: {ex.Message}");
            }
            Log("SetStartup() finished.");
        }

        /// <summary>
        /// Starts the MagicOnion gRPC host on the configured gRPC port (default 5002).
        /// Runs alongside the TCP server (dual-mode). MessagePipe is registered for
        /// inter-service messaging via IPublisher/ISubscriber.
        /// </summary>
        /// <param name="port">The HTTP/2 port to listen on.</param>
        /// <param name="ct">Cancellation token for graceful shutdown.</param>
        public static async Task StartGrpcHostAsync(int port, CancellationToken ct = default)
        {
            Log($"StartGrpcHostAsync() starting on port {port}...");
            try
            {
                var builder = WebApplication.CreateBuilder();

                // Bind to any IP — Macro Deck runs on a separate machine on the network.
                // Windows Firewall will prompt once on first run; clicking Allow makes it permanent.
                builder.WebHost.ConfigureKestrel(opts =>
                {
                    opts.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
                });

                // Suppress Kestrel's verbose logging — we use our own Log().
                builder.Logging.ClearProviders();

                builder.Services.AddGrpc();
                builder.Services.AddMagicOnion();
                builder.Services.AddMessagePipe();
                builder.Services.AddSingleton<Shared.IServerConfig, Windows.WindowsServerConfig>();
                builder.Services.AddSingleton<Shared.IMonitorController, Windows.WindowsMonitorController>();

                var app = builder.Build();
                app.MapMagicOnionService();

                _grpcApp = app;
                await app.StartAsync(ct);
                Log($"gRPC host started on port {port} (all interfaces).");
            }
            catch (Exception ex)
            {
                Log($"Failed to start gRPC host: {ex.Message}");
            }
        }

        /// <summary>
        /// Gracefully stops and disposes the MagicOnion gRPC host.
        /// Called from ShutdownServer() to ensure dual-mode teardown.
        /// </summary>
        public static async Task StopGrpcHostAsync()
        {
            Log("StopGrpcHostAsync() called.");
            if (_grpcApp != null)
            {
                try
                {
                    await _grpcApp.StopAsync();
                    await _grpcApp.DisposeAsync();
                    Log("gRPC host stopped and disposed.");
                }
                catch (Exception ex)
                {
                    Log($"Error stopping gRPC host: {ex.Message}");
                }
                finally
                {
                    _grpcApp = null;
                }
            }
            else
            {
                Log("StopGrpcHostAsync(): _grpcApp was null, nothing to stop.");
            }
        }

        /// <summary>
        /// Allows external classes (like MainForm) to report a change in the server's busy state.
        /// </summary>
        /// <param name="state">The new state (0=Stopped, 1=Running, 2=Busy/Restarting).</param>
        public static void SetServerState(int state)
        {
            Log($"SetServerState() called. New state: {state}");
            // Only update if the state is actually changing
            if (Settings.Default.ServerState != state)
            {
                Log($"State is different from current ({Settings.Default.ServerState}). Updating...");
                Settings.Default.ServerState = state;
                Settings.Default.Save();
                Log($"Server state manually set to {state} and saved.");
                ServerStateChanged?.Invoke(null, state);
                Log("ServerStateChanged event fired.");
            }
            else
            {
                Log("State is the same as current. No change.");
            }
            Log("SetServerState() finished.");
        }
    }
}