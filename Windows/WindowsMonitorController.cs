using MultiDisplayVCPServer.Shared;

namespace MultiDisplayVCPServer.Windows
{
    /// <summary>
    /// Windows implementation of IMonitorController using DXVA2 DDC/CI and WMI.
    /// Delegates to MonitorController and Program cache methods.
    /// </summary>
    public class WindowsMonitorController : IMonitorController
    {
        public bool IsCacheWarming => Program.IsCacheWarming;

        public CapabilitiesResponse GetCachedCapabilities()
        {
            return MonitorController.GetCachedCapabilities();
        }

        public SetVcpResponse SetVcp(string monitorPnpId, byte vcpCode, uint value)
        {
            return MonitorController.SetVcp(monitorPnpId, vcpCode, value);
        }

        public async Task WarmCacheAsync(CancellationToken cancellationToken = default)
        {
            await Program.EnsureCacheWarmedAsync(15000);
        }
    }
}
