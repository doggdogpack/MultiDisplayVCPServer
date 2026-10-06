using MultiDisplayVCPServer.Shared;

namespace MultiDisplayVCPServer.Windows
{
    /// <summary>
    /// Windows implementation of IServerConfig backed by Windows Forms application settings.
    /// </summary>
    public class WindowsServerConfig : IServerConfig
    {
        public int Port => Properties.Settings.Default.Port;
        public int GrpcPort => Properties.Settings.Default.GrpcPort;
        public string Password => Properties.Settings.Default.Password;

        public int ServerState
        {
            get => Properties.Settings.Default.ServerState;
            set => Properties.Settings.Default.ServerState = value;
        }

        public void Save()
        {
            Properties.Settings.Default.Save();
        }
    }
}
