using MagicOnion;
using MemoryPack;

namespace MultiDisplayVCPServer.Shared
{
    /// <summary>
    /// The MagicOnion gRPC service contract for the VCP server.
    /// Implemented by VcpService on the server; called by VcpGrpcClient on the client.
    /// </summary>
    public interface IVcpService : IService<IVcpService>
    {
        /// <summary>Validates the connection and password.</summary>
        UnaryResult<PingResponse> PingAsync(string passwordHash, long timestamp);

        /// <summary>Returns full monitor capabilities for all attached DDC/CI monitors.</summary>
        UnaryResult<CapabilitiesResponse> GetCapabilitiesAsync(string passwordHash, long timestamp);

        /// <summary>Sets a VCP feature value on a specific monitor.</summary>
        UnaryResult<SetVcpResponse> SetVcpAsync(string passwordHash, long timestamp, string monitorPnpId, byte vcpCode, uint value);
    }

    [MemoryPackable]
    public partial class PingResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    [MemoryPackable]
    public partial class CapabilitiesResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<MonitorInfoDto> Monitors { get; set; } = [];
    }

    [MemoryPackable]
    public partial class MonitorInfoDto
    {
        public string DeviceID { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<VcpFeatureDto> Capabilities { get; set; } = [];
    }

    [MemoryPackable]
    public partial class VcpFeatureDto
    {
        public byte Code { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool ReadWrite { get; set; }
        public uint CurrentValue { get; set; }
        public uint MaximumValue { get; set; }
        public Dictionary<uint, string> NonContinuousValues { get; set; } = [];
    }

    [MemoryPackable]
    public partial class SetVcpResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
