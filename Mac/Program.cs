using MultiDisplayVCPServer.Services;
using System.Globalization;

namespace MultiDisplayVCPServer.Mac
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  MultiDisplayVCP Server v2.0.0 (macOS Edition)   ");
            Console.WriteLine("==================================================");

            string? configPath = null;
            int? overridePort = null;
            int? overrideGrpcPort = null;
            string? overridePassword = null;
            bool scanOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    return 0;
                }
                if (arg.Equals("--scan", StringComparison.OrdinalIgnoreCase))
                {
                    scanOnly = true;
                }
                else if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    configPath = args[++i];
                }
                else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                        overridePort = p;
                }
                else if (arg.Equals("--grpc-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gp))
                        overrideGrpcPort = gp;
                }
                else if (arg.Equals("--password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    overridePassword = args[++i];
                }
            }

            var config = new MacServerConfig(configPath);
            if (overridePort.HasValue) config.Port = overridePort.Value;
            if (overrideGrpcPort.HasValue) config.GrpcPort = overrideGrpcPort.Value;
            if (!string.IsNullOrWhiteSpace(overridePassword)) config.Password = overridePassword;

            var monitorController = new MacMonitorController();

            if (scanOnly)
            {
                Console.WriteLine("Scanning attached DDC/CI monitors on macOS...");
                await monitorController.WarmCacheAsync();
                var caps = monitorController.GetCachedCapabilities();
                Console.WriteLine($"Status: {caps.Message}");
                foreach (var mon in caps.Monitors)
                {
                    Console.WriteLine($"\nMonitor: {mon.DeviceID} - {mon.Description}");
                    foreach (var f in mon.Capabilities)
                    {
                        Console.WriteLine($"  - [0x{f.Code:X2}] {f.Name}: {f.CurrentValue}/{f.MaximumValue} (RW: {f.ReadWrite})");
                    }
                }
                return 0;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                Console.WriteLine("\nShutdown signal received. Stopping server...");
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            var engine = new VcpServerEngine(config, monitorController, logMsg =>
            {
                Console.WriteLine(logMsg);
            });

            try
            {
                await engine.StartAsync(cts.Token);
                Console.WriteLine("MultiDisplayVCP Server daemon active. Press Ctrl+C to terminate.");
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Graceful termination
            }
            finally
            {
                await engine.StopAsync();
                Console.WriteLine("MultiDisplayVCP Server stopped.");
            }

            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
Usage: MultiDisplayVCPServer.Mac [options]

Options:
  --port <number>        TCP port for legacy client connections (default: 21000)
  --grpc-port <number>   HTTP/2 port for MagicOnion gRPC connections (default: 5002)
  --password <secret>    Shared secret password for HMAC authentication
  --config <path>        Path to custom JSON configuration file
  --scan                 Perform a one-time monitor discovery scan and exit
  -h, --help             Show this help information

Requirements:
  macOS uses 'ddcctl' for DDC/CI monitor communication:
    brew install ddcctl
");
        }
    }
}
