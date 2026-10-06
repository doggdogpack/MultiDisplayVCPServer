using Cysharp.Text;
using MultiDisplayVCPServer.Shared;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiDisplayVCPServer.Mac
{
    /// <summary>
    /// macOS DDC/CI monitor controller implementation using ddcctl and system_profiler.
    /// Supports Apple Silicon (arm64) and Intel (x86_64) Macs.
    /// </summary>
    public class MacMonitorController : IMonitorController
    {
        private readonly List<MacMonitorInfo> _monitors = [];
        private CapabilitiesResponse _cachedCapabilities = new() { Success = true, Message = "No monitors discovered yet." };
        private readonly object _lock = new();
        private volatile bool _isCacheWarming = true;

        public bool IsCacheWarming => _isCacheWarming;

        private record MacMonitorInfo(
            int DisplayNumber,
            string DeviceId,
            string Description,
            List<VcpFeatureDto> Features);

        public CapabilitiesResponse GetCachedCapabilities()
        {
            lock (_lock)
            {
                return _cachedCapabilities;
            }
        }

        public SetVcpResponse SetVcp(string monitorPnpId, byte vcpCode, uint value)
        {
            MacMonitorInfo? target;
            lock (_lock)
            {
                target = _monitors.FirstOrDefault(m =>
                    m.DeviceId.Equals(monitorPnpId, StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Equals(monitorPnpId, StringComparison.OrdinalIgnoreCase));
            }

            if (target == null)
            {
                return new SetVcpResponse
                {
                    Success = false,
                    Message = ZString.Format("ERROR: Monitor '{0}' not found on macOS host.", monitorPnpId)
                };
            }

            // Map standard VCP codes to ddcctl flags:
            // 0x10 -> -b (brightness)
            // 0x12 -> -c (contrast)
            // Other codes can be set via -v <hex> if supported
            string flag = vcpCode switch
            {
                0x10 => "-b",
                0x12 => "-c",
                _ => ZString.Format("-v 0x{0:X2}", vcpCode)
            };

            try
            {
                string cmdArgs = ZString.Format("-d {0} {1} {2}", target.DisplayNumber, flag, value);
                var (exitCode, stdout, stderr) = RunProcess("ddcctl", cmdArgs);

                if (exitCode == 0)
                {
                    lock (_lock)
                    {
                        var feat = target.Features.FirstOrDefault(f => f.Code == vcpCode);
                        if (feat != null)
                        {
                            feat.CurrentValue = value;
                        }
                    }

                    return new SetVcpResponse
                    {
                        Success = true,
                        Message = ZString.Format("OK: Set 0x{0:X2} = {1} on monitor {2}.", vcpCode, value, monitorPnpId)
                    };
                }

                return new SetVcpResponse
                {
                    Success = false,
                    Message = ZString.Format("ERROR: ddcctl failed ({0}): {1}", exitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)
                };
            }
            catch (Exception ex)
            {
                return new SetVcpResponse
                {
                    Success = false,
                    Message = ZString.Format("ERROR executing ddcctl on macOS: {0}", ex.Message)
                };
            }
        }

        public async Task WarmCacheAsync(CancellationToken cancellationToken = default)
        {
            _isCacheWarming = true;
            try
            {
                await Task.Run(() => ScanMonitors(), cancellationToken);
            }
            finally
            {
                _isCacheWarming = false;
            }
        }

        private void ScanMonitors()
        {
            var discovered = new List<MacMonitorInfo>();

            try
            {
                // Query system_profiler for displays
                var (exitCode, stdout, _) = RunProcess("system_profiler", "SPDisplaysDataType");
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    discovered = ParseSystemProfiler(stdout);
                }
                else
                {
                    // Fallback to default display 1
                    discovered.Add(CreateDefaultMacDisplay(1, "Display 1"));
                }
            }
            catch
            {
                discovered.Add(CreateDefaultMacDisplay(1, "Display 1"));
            }

            lock (_lock)
            {
                _monitors.Clear();
                _monitors.AddRange(discovered);

                var dtoList = _monitors.Select(m => new MonitorInfoDto
                {
                    DeviceID = m.DeviceId,
                    Description = m.Description,
                    Capabilities = m.Features
                }).ToList();

                _cachedCapabilities = new CapabilitiesResponse
                {
                    Success = true,
                    Message = ZString.Format("OK: Found {0} monitor(s) on macOS.", dtoList.Count),
                    Monitors = dtoList
                };
            }
        }

        private List<MacMonitorInfo> ParseSystemProfiler(string output)
        {
            var list = new List<MacMonitorInfo>();
            int displayIndex = 1;

            var matches = Regex.Matches(output, @"^\s{4}([A-Za-z0-9\s\-_]+):\s*$", RegexOptions.Multiline);
            foreach (Match match in matches)
            {
                string rawName = match.Groups[1].Value.Trim();
                if (rawName.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string pnpId = Regex.Replace(rawName.ToUpperInvariant(), @"[^A-Z0-9]+", "_");
                if (string.IsNullOrWhiteSpace(pnpId)) pnpId = $"DISPLAY_{displayIndex}";

                list.Add(CreateDefaultMacDisplay(displayIndex, rawName, pnpId));
                displayIndex++;
            }

            if (list.Count == 0)
            {
                list.Add(CreateDefaultMacDisplay(1, "Main Display", "DISPLAY_1"));
            }

            return list;
        }

        private static MacMonitorInfo CreateDefaultMacDisplay(int dispNum, string desc, string? pnpId = null)
        {
            string id = pnpId ?? $"DISPLAY_{dispNum}";
            var features = new List<VcpFeatureDto>
            {
                new() { Code = 0x10, Name = "Brightness", Type = "Continuous", ReadWrite = true, CurrentValue = 50, MaximumValue = 100 },
                new() { Code = 0x12, Name = "Contrast", Type = "Continuous", ReadWrite = true, CurrentValue = 50, MaximumValue = 100 },
                new() { Code = 0x60, Name = "Input Select", Type = "NonContinuous", ReadWrite = true, CurrentValue = 1, MaximumValue = 10 },
                new() { Code = 0x62, Name = "Audio Speaker Volume", Type = "Continuous", ReadWrite = true, CurrentValue = 50, MaximumValue = 100 },
                new() { Code = 0xD6, Name = "Power Mode", Type = "NonContinuous", ReadWrite = true, CurrentValue = 1, MaximumValue = 5 }
            };

            return new MacMonitorInfo(dispNum, id, desc, features);
        }

        private static (int exitCode, string stdout, string stderr) RunProcess(string command, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "", $"Failed to start {command}");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            return (proc.ExitCode, stdout, stderr);
        }
    }
}
