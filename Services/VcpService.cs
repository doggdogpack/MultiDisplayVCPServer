using MagicOnion;
using MagicOnion.Server;
using MessagePipe;
using Microsoft.Extensions.Logging;
using MultiDisplayVCPServer.Messaging;
using MultiDisplayVCPServer.Shared;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MultiDisplayVCPServer.Services
{
    /// <summary>
    /// MagicOnion gRPC service implementation for the VCP server.
    /// Runs alongside the legacy TCP server (dual-mode).
    /// Each method validates the HMAC-SHA256 password hash using a
    /// 30-second sliding window.
    /// </summary>
    public class VcpService : ServiceBase<IVcpService>, IVcpService
    {
        private readonly ISubscriber<ServerStateMessage>? _stateSubscriber;
        private readonly ILogger<VcpService> _logger;
        private readonly IMonitorController? _monitorController;
        private readonly IServerConfig? _config;

        /// <summary>
        /// Initializes a new instance of <see cref="VcpService"/>.
        /// Injected via DI by Kestrel in VcpServerEngine / StartGrpcHostAsync.
        /// </summary>
        public VcpService(
            ILogger<VcpService> logger,
            ISubscriber<ServerStateMessage>? stateSubscriber = null,
            IMonitorController? monitorController = null,
            IServerConfig? config = null)
        {
            _logger = logger;
            _stateSubscriber = stateSubscriber;
            _monitorController = monitorController;
            _config = config;
        }

        private string Password => _config?.Password ?? string.Empty;

        /// <summary>
        /// Validates the connection and HMAC-SHA256 password hash.
        /// Returns <see cref="PingResponse.Success"/> = true if the hash is valid.
        /// </summary>
        public UnaryResult<PingResponse> PingAsync(string passwordHash, long timestamp)
        {
            _logger.LogDebug("PingAsync called. Timestamp={Timestamp}", timestamp);

            if (!ValidateHmac(passwordHash, timestamp, "PING", Password))
            {
                _logger.LogWarning("PingAsync: HMAC validation failed.");
                return UnaryResult.FromResult(new PingResponse { Success = false, Message = "ERROR: Invalid hash or stale timestamp." });
            }

            _logger.LogInformation("PingAsync: Authenticated successfully.");
            return UnaryResult.FromResult(new PingResponse { Success = true, Message = "OK" });
        }

        /// <summary>
        /// Returns full monitor capabilities for all attached DDC/CI monitors.
        /// Reads from the in-memory cache built by the active IMonitorController.
        /// </summary>
        public async UnaryResult<CapabilitiesResponse> GetCapabilitiesAsync(string passwordHash, long timestamp)
        {
            _logger.LogDebug("GetCapabilitiesAsync called. Timestamp={Timestamp}", timestamp);

            if (!ValidateHmac(passwordHash, timestamp, "GET_CAPS", Password))
            {
                _logger.LogWarning("GetCapabilitiesAsync: HMAC validation failed.");
                return new CapabilitiesResponse
                {
                    Success = false,
                    Message = "ERROR: Invalid hash or stale timestamp."
                };
            }

            _logger.LogInformation("GetCapabilitiesAsync: Authenticated. Ensuring cache is warmed...");
            if (_monitorController != null)
            {
                if (_monitorController.IsCacheWarming)
                {
                    await _monitorController.WarmCacheAsync();
                }
                return _monitorController.GetCachedCapabilities();
            }

            return new CapabilitiesResponse
            {
                Success = false,
                Message = "ERROR: Monitor controller not registered."
            };
        }

        /// <summary>
        /// Sets a VCP feature value on a specific monitor identified by its PnP ID.
        /// </summary>
        public UnaryResult<SetVcpResponse> SetVcpAsync(string passwordHash, long timestamp, string monitorPnpId, byte vcpCode, uint value)
        {
            _logger.LogDebug("SetVcpAsync called. Monitor={MonitorId}, Code=0x{Code:X2}, Value={Value}", monitorPnpId, vcpCode, value);

            if (!ValidateHmac(passwordHash, timestamp, "SET_VCP", Password) &&
                !ValidateHmac(passwordHash, timestamp, "SET", Password))
            {
                _logger.LogWarning("SetVcpAsync: HMAC validation failed.");
                return UnaryResult.FromResult(new SetVcpResponse
                {
                    Success = false,
                    Message = "ERROR: Invalid hash or stale timestamp."
                });
            }

            _logger.LogInformation("SetVcpAsync: Authenticated. Setting VCP 0x{Code:X2}={Value} on {MonitorId}.", vcpCode, value, monitorPnpId);
            if (_monitorController != null)
            {
                var response = _monitorController.SetVcp(monitorPnpId, vcpCode, value);
                return UnaryResult.FromResult(response);
            }

            return UnaryResult.FromResult(new SetVcpResponse
            {
                Success = false,
                Message = "ERROR: Monitor controller not registered."
            });
        }

        /// <summary>
        /// Validates an HMAC-SHA256 password hash from a gRPC request.
        /// Rejects requests outside a 30-second window to prevent replay attacks.
        /// </summary>
        private static bool ValidateHmac(string passwordHash, long timestamp, string command, string sharedSecret)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - timestamp) > 30) return false;

            string messageToHash = command + timestamp.ToString(CultureInfo.InvariantCulture);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret));
            byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(messageToHash));
            string expectedBase64 = Convert.ToBase64String(expected);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(passwordHash),
                Encoding.UTF8.GetBytes(expectedBase64));
        }
    }
}
