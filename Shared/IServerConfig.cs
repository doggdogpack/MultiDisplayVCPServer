namespace MultiDisplayVCPServer.Shared
{
    /// <summary>
    /// Configuration contract for server ports, authentication secrets, and runtime state.
    /// Implemented by WindowsServerConfig (via Properties.Settings), LinuxServerConfig, and MacServerConfig.
    /// </summary>
    public interface IServerConfig
    {
        int Port { get; }
        int GrpcPort { get; }
        string Password { get; }
        int ServerState { get; set; }
        void Save();
    }
}
