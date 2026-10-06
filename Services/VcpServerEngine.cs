using MagicOnion.Server;
using MessagePipe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiDisplayVCPServer.Messaging;
using MultiDisplayVCPServer.Shared;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiDisplayVCPServer.Services
{
    /// <summary>
    /// Cross-platform server engine that hosts both the MagicOnion gRPC endpoint (HTTP/2)
    /// and the legacy TCP listener in dual-mode. Used by Windows, Linux, and macOS servers.
    /// </summary>
    public class VcpServerEngine
    {
        private readonly IServerConfig _config;
        private readonly IMonitorController _monitorController;
        private readonly Action<string>? _logAction;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly char[] _pipeDelimiter = ['|'];

        private WebApplication? _grpcApp;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cts;
        private Task? _tcpListenerTask;

        public bool IsRunning { get; private set; }

        public VcpServerEngine(
            IServerConfig config,
            IMonitorController monitorController,
            Action<string>? logAction = null)
        {
            _config = config;
            _monitorController = monitorController;
            _logAction = logAction;
        }

        private void Log(string message)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logAction?.Invoke(entry);
        }

        /// <summary>
        /// Starts both gRPC and TCP server listeners and initiates the background monitor capability scan.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (IsRunning) return;

            Log("Starting VCP Server Engine (Dual-Mode)...");
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 1. Kick off background monitor discovery scan
            _ = Task.Run(() => _monitorController.WarmCacheAsync(_cts.Token), _cts.Token);

            // 2. Start MagicOnion gRPC host
            await StartGrpcHostAsync(_config.GrpcPort, _cts.Token);

            // 3. Start Legacy TCP listener
            StartTcpListener(_config.Port, _cts.Token);

            IsRunning = true;
            _config.ServerState = 1;
            Log($"VCP Server Engine running: gRPC on port {_config.GrpcPort}, TCP on port {_config.Port}.");
        }

        /// <summary>
        /// Stops both gRPC and TCP listeners cleanly.
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning) return;

            Log("Stopping VCP Server Engine...");
            _cts?.Cancel();

            // Stop TCP listener
            try
            {
                _tcpListener?.Stop();
            }
            catch { }

            // Stop gRPC host
            if (_grpcApp != null)
            {
                try
                {
                    await _grpcApp.StopAsync();
                    await _grpcApp.DisposeAsync();
                }
                catch { }
                _grpcApp = null;
            }

            IsRunning = false;
            _config.ServerState = 0;
            Log("VCP Server Engine stopped.");
        }

        private async Task StartGrpcHostAsync(int port, CancellationToken ct)
        {
            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.ConfigureKestrel(opts =>
                {
                    opts.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
                });

                builder.Logging.ClearProviders();

                builder.Services.AddGrpc();
                builder.Services.AddMagicOnion();
                builder.Services.AddMessagePipe();

                // Register abstractions for VcpService DI injection
                builder.Services.AddSingleton(_config);
                builder.Services.AddSingleton(_monitorController);

                var app = builder.Build();
                app.MapMagicOnionService();

                _grpcApp = app;
                await app.StartAsync(ct);
                Log($"gRPC server initialized on port {port}.");
            }
            catch (Exception ex)
            {
                Log($"Error starting gRPC host on port {port}: {ex.Message}");
            }
        }

        private void StartTcpListener(int port, CancellationToken ct)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, port);
                _tcpListener.Start();
                Log($"TCP listener initialized on port {port}.");

                _tcpListenerTask = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            var client = await _tcpListener.AcceptTcpClientAsync(ct);
                            _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (!ct.IsCancellationRequested)
                            {
                                Log($"TCP accept error: {ex.Message}");
                            }
                        }
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                Log($"Error starting TCP listener on port {port}: {ex.Message}");
            }
        }

        private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
        {
            string remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            using (client)
            await using (var stream = client.GetStream())
            {
                try
                {
                    byte[] buffer = new byte[2048];
                    int bytesRead = await stream.ReadAsync(buffer, ct);
                    if (bytesRead <= 0) return;

                    string receivedData = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                    string[] parts = receivedData.Split(_pipeDelimiter, 3);
                    string responseMessage;

                    if (parts.Length == 3)
                    {
                        string timestampStr = parts[0];
                        string hashBase64 = parts[1];
                        string command = parts[2];

                        if (ValidateHash(timestampStr, hashBase64, command, _config.Password))
                        {
                            responseMessage = ExecuteCommand(command);
                        }
                        else
                        {
                            responseMessage = "ERROR: Invalid Hash.";
                            Log($"Authentication failed for {remoteEndPoint} (invalid hash or stale timestamp).");
                        }
                    }
                    else
                    {
                        responseMessage = "ERROR: Invalid request format.";
                    }

                    byte[] responseBytes = Encoding.UTF8.GetBytes(responseMessage);
                    await stream.WriteAsync(responseBytes, ct);
                }
                catch (Exception ex)
                {
                    Log($"Error processing client {remoteEndPoint}: {ex.Message}");
                }
            }
        }

        private bool ValidateHash(string timestampStr, string hashBase64, string command, string password)
        {
            try
            {
                if (!long.TryParse(timestampStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long clientTimestamp))
                {
                    return false;
                }

                long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (Math.Abs(currentTimestamp - clientTimestamp) > 30)
                {
                    return false;
                }

                using var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(password));
                string messageToHash = command + timestampStr;
                byte[] computedHashBytes = hmac.ComputeHash(Encoding.ASCII.GetBytes(messageToHash));
                string computedHashBase64 = Convert.ToBase64String(computedHashBytes);

                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(computedHashBase64),
                    Encoding.UTF8.GetBytes(hashBase64));
            }
            catch
            {
                return false;
            }
        }

        private string ExecuteCommand(string command)
        {
            if (command.Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                return "OK: PONG";
            }

            if (command.Equals("GET_CAPS", StringComparison.OrdinalIgnoreCase))
            {
                var caps = _monitorController.GetCachedCapabilities();
                return JsonSerializer.Serialize(caps, _jsonOptions);
            }

            if (command.StartsWith("SET:", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = command.Split(':');
                if (parts.Length == 4 &&
                    byte.TryParse(parts[2], out byte vcpCode) &&
                    uint.TryParse(parts[3], out uint value))
                {
                    string monitorId = parts[1];
                    var result = _monitorController.SetVcp(monitorId, vcpCode, value);
                    return result.Message;
                }
                return "ERROR: Invalid SET format. Expected SET:ID:CODE:VALUE.";
            }

            return $"ERROR: Unknown command '{command}'.";
        }
    }
}
