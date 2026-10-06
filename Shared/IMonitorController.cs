namespace MultiDisplayVCPServer.Shared
{
    /// <summary>
    /// OS-agnostic interface for querying and controlling physical monitor VCP features.
    /// Implemented by WindowsMonitorController, LinuxMonitorController, and MacMonitorController.
    /// </summary>
    public interface IMonitorController
    {
        /// <summary>Returns the current cached capabilities of attached monitors.</summary>
        CapabilitiesResponse GetCachedCapabilities();

        /// <summary>Sets a VCP feature value on the specified monitor.</summary>
        SetVcpResponse SetVcp(string monitorPnpId, byte vcpCode, uint value);

        /// <summary>Asynchronously performs or refreshes the monitor capability scan.</summary>
        Task WarmCacheAsync(CancellationToken cancellationToken = default);

        /// <summary>True while the initial or ongoing capability scan is executing.</summary>
        bool IsCacheWarming { get; }
    }
}
