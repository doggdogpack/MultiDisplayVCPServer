using Cysharp.Text;
using MultiDisplayVCPServer.Shared;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ZLinq;

namespace MultiDisplayVCPServer.Linux
{
    /// <summary>
    /// Linux DDC/CI monitor controller implementation using the Linux i2c subsystem via ddcutil.
    /// Supports dynamic discovery, capabilities querying, and VCP feature execution.
    /// </summary>
    public class LinuxMonitorController : IMonitorController
    {
        private readonly List<LinuxMonitorInfo> _monitors = [];
        private CapabilitiesResponse _cachedCapabilities = new() { Success = true, Message = "No monitors discovered yet." };
        private readonly object _lock = new();
        private volatile bool _isCacheWarming = true;

        public bool IsCacheWarming => _isCacheWarming;

        private record LinuxMonitorInfo(
            int DisplayNumber,
            string DeviceId,
            string Description,
            string I2cBus,
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
            LinuxMonitorInfo? target;
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
                    Message = ZString.Format("ERROR: Monitor '{0}' not found on Linux host.", monitorPnpId)
                };
            }

            try
            {
                string cmdArgs = ZString.Format("setvcp 0x{0:X2} {1} --display {2} --noverify", vcpCode, value, target.DisplayNumber);
                var (exitCode, stdout, stderr) = RunDdcUtil(cmdArgs);

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
                    Message = ZString.Format("ERROR: ddcutil failed ({0}): {1}", exitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)
                };
            }
            catch (Exception ex)
            {
                return new SetVcpResponse
                {
                    Success = false,
                    Message = ZString.Format("ERROR executing ddcutil: {0}", ex.Message)
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
            var discovered = new List<LinuxMonitorInfo>();

            try
            {
                var (exitCode, stdout, _) = RunDdcUtil("detect --terse");
                if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    // Fallback to standard detect
                    var (fallbackCode, fallbackOut, _) = RunDdcUtil("detect");
                    stdout = fallbackOut;
                }

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    discovered = ParseDdcUtilDetect(stdout);
                }

                // Query capabilities for each discovered monitor
                foreach (var mon in discovered)
                {
                    PopulateCapabilities(mon);
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _cachedCapabilities = new CapabilitiesResponse
                    {
                        Success = false,
                        Message = ZString.Format("ddcutil discovery failed: {0}. Ensure 'ddcutil' is installed and i2c-dev module is loaded (sudo modprobe i2c-dev).", ex.Message),
                        Monitors = []
                    };
                }
                return;
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
                    Message = ZString.Format("OK: Found {0} monitor(s) on Linux.", dtoList.Count),
                    Monitors = dtoList
                };
            }
        }

        private List<LinuxMonitorInfo> ParseDdcUtilDetect(string output)
        {
            var list = new List<LinuxMonitorInfo>();
            var displayBlocks = Regex.Split(output, @"(?:^|\n)(?=Display\s+\d+)");

            foreach (var block in displayBlocks)
            {
                if (string.IsNullOrWhiteSpace(block)) continue;

                var dispMatch = Regex.Match(block, @"Display\s+(\d+)");
                if (!dispMatch.Success) continue;
                int dispNum = int.Parse(dispMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                string i2cBus = Regex.Match(block, @"I2C bus:\s*(.+?)(?:\r?\n|$)").Groups[1].Value.Trim();
                string model = Regex.Match(block, @"Model:\s*(.+?)(?:\r?\n|$)").Groups[1].Value.Trim();
                string mfg = Regex.Match(block, @"Mfg id:\s*(.+?)(?:\r?\n|$)").Groups[1].Value.Trim();
                string serial = Regex.Match(block, @"Serial number:\s*(.+?)(?:\r?\n|$)").Groups[1].Value.Trim();

                string pnpId = !string.IsNullOrEmpty(model) ? Slugify(model) : $"DISPLAY{dispNum}";
                string desc = !string.IsNullOrEmpty(model) ? $"{mfg} {model}".Trim() : $"Display {dispNum} ({i2cBus})";

                list.Add(new LinuxMonitorInfo(dispNum, pnpId, desc, i2cBus, []));
            }

            return list;
        }

        private void PopulateCapabilities(LinuxMonitorInfo mon)
        {
            // Standard common VCP features to probe
            var standardFeatures = new (byte code, string name, string type)[]
            {
                (0x10, "Brightness", "Continuous"),
                (0x12, "Contrast", "Continuous"),
                (0x60, "Input Select", "NonContinuous"),
                (0x62, "Audio Speaker Volume", "Continuous"),
                (0xD6, "Power Mode", "NonContinuous"),
                (0x14, "Select Color Preset", "NonContinuous"),
                (0x16, "Red Video Gain", "Continuous"),
                (0x18, "Green Video Gain", "Continuous"),
                (0x1A, "Blue Video Gain", "Continuous")
            };

            foreach (var sf in standardFeatures)
            {
                var (exitCode, stdout, _) = RunDdcUtil(ZString.Format("getvcp 0x{0:X2} --display {1} --terse", sf.code, mon.DisplayNumber));
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    // Format of 'getvcp 0x10 --terse': VCP 10 C 60 100
                    var parts = stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    uint curVal = 50;
                    uint maxVal = 100;

                    if (parts.Length >= 4 && uint.TryParse(parts[3], out uint parsedCur))
                    {
                        curVal = parsedCur;
                    }
                    if (parts.Length >= 5 && uint.TryParse(parts[4], out uint parsedMax))
                    {
                        maxVal = parsedMax;
                    }

                    mon.Features.Add(new VcpFeatureDto
                    {
                        Code = sf.code,
                        Name = sf.name,
                        Type = sf.type,
                        ReadWrite = true,
                        CurrentValue = curVal,
                        MaximumValue = maxVal
                    });
                }
            }
        }

        private static (int exitCode, string stdout, string stderr) RunDdcUtil(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ddcutil",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "", "Failed to start ddcutil");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            return (proc.ExitCode, stdout, stderr);
        }

        private static string Slugify(string text)
        {
            return Regex.Replace(text.Trim().ToUpperInvariant(), @"[^A-Z0-9]+", "_");
        }
    }
}
