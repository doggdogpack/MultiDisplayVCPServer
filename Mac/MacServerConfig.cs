using MultiDisplayVCPServer.Shared;
using System.Text.Json;

namespace MultiDisplayVCPServer.Mac
{
    /// <summary>
    /// macOS implementation of IServerConfig with JSON configuration persistence
    /// in ~/Library/Application Support/MultiDisplayVCP.
    /// </summary>
    public class MacServerConfig : IServerConfig
    {
        public int Port { get; set; } = 5001;
        public int GrpcPort { get; set; } = 5002;
        public string Password { get; set; } = "changeme";
        public int ServerState { get; set; } = 0;

        private readonly string _configFilePath;

        public MacServerConfig(string? customConfigPath = null)
        {
            string configDir = customConfigPath != null
                ? Path.GetDirectoryName(customConfigPath) ?? "."
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MultiDisplayVCP");

            _configFilePath = customConfigPath ?? Path.Combine(configDir, "server.json");

            Load();

            if (int.TryParse(Environment.GetEnvironmentVariable("VCP_PORT"), out int envPort))
                Port = envPort;
            if (int.TryParse(Environment.GetEnvironmentVariable("VCP_GRPC_PORT"), out int envGrpcPort))
                GrpcPort = envGrpcPort;
            string? envPass = Environment.GetEnvironmentVariable("VCP_PASSWORD");
            if (!string.IsNullOrWhiteSpace(envPass))
                Password = envPass;
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    if (doc.TryGetProperty("port", out var p) && p.TryGetInt32(out int port))
                        Port = port;
                    if (doc.TryGetProperty("grpcPort", out var gp) && gp.TryGetInt32(out int grpcPort))
                        GrpcPort = grpcPort;
                    if (doc.TryGetProperty("password", out var pass) && pass.GetString() is string password)
                        Password = password;
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var data = new
                {
                    port = Port,
                    grpcPort = GrpcPort,
                    password = Password
                };

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, json);
            }
            catch { }
        }
    }
}
