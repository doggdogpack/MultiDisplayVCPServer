# MultiDisplayVCP Server v2.0

A high-performance, cross-platform DDC/CI hardware display control daemon. Enables remote control of monitor hardware settings (Brightness, Contrast, Input Source, Audio Volume, Power Mode, and custom VCP codes) across your local network.

> [!NOTE]
> **Prerequisite**: Your physical displays must support **DDC/CI** and have it enabled in their On-Screen Display (OSD) menu settings.

---

## 🌟 Key Features

- **Cross-Platform**: Native support for **Windows** (WinForms GUI / System Tray), **Linux** (headless daemon / systemd service), and **macOS** (headless daemon / launchd service).
- **Dual-Mode Networking**:
  - **MagicOnion gRPC / HTTP/2 (Primary)**: High-throughput, zero-allocation protocol paired with Macro Deck 3 clients.
  - **Raw TCP Socket Listener (Legacy / Universal)**: Fast, text pipe-delimited protocol ensuring backward compatibility with Macro Deck 2 and open integrations (Bitfocus Companion, Stream Deck, Home Assistant, custom scripts).
- **Security**: HMAC-SHA256 challenge authentication with a sliding timestamp window to prevent replay attacks.
- **Hardware Integration**:
  - **Windows**: Windows Monitor Configuration API (`dxva2.dll` / Physical Monitors).
  - **Linux**: Kernel I2C interface (`ddcutil` / `/dev/i2c`).
  - **macOS**: I/O Kit Display Services (`ddcctl`).

---

## 🖥️ Client Compatibility

| Client | Protocol | Supported Server Versions |
| :--- | :--- | :--- |
| **[Macro Deck 3 Client](https://github.com/doggdogpack/MultiDisplayVCPClient/tree/Macro-Deck-3)** (v3.0.0+) | MagicOnion gRPC (HTTP/2) + TCP fallback | Server v2.0+ |
| **[Macro Deck 2 Client](https://github.com/doggdogpack/MultiDisplayVCPClient/tree/Macro-Deck-2)** (v2.0.0) | Raw TCP socket | Server v1.x and v2.0+ |
| **Bitfocus Companion / Stream Deck / Custom** | Raw TCP socket or gRPC | Server v2.0+ |

---

## 📦 Downloads & Installation

Pre-built packages and installers are available in the [Releases](https://github.com/doggdogpack/MultiDisplayVCPServer/releases) section:

### Windows
* **Recommended Setup Installer**: Download and run **`MultiDisplayVCPServer_Windows_v2.0.0_Setup.exe`**.  
  *(Sets up Start Menu & Desktop shortcuts, optional system startup run entry, and registers an uninstaller).*
* **Portable Archive**: Download and extract **`MultiDisplayVCPServer_Windows_v2.0.0.zip`**, then launch `MultiDisplayVCPServer.exe`.

### Linux (x64)
1. Install prerequisites:
   ```bash
   sudo apt install ddcutil i2c-tools
   sudo modprobe i2c-dev
   ```
2. Download and extract **`MultiDisplayVCPServer_Linux_x64_v2.0.0.tar.gz`**:
   ```bash
   tar -xzf MultiDisplayVCPServer_Linux_x64_v2.0.0.tar.gz
   ```
3. Run the automated installer:
   ```bash
   sudo ./install.sh
   ```
   *(Installs binary to `/usr/local/bin`, configures I2C group permissions, and installs/starts the `multidisplayvcp.service` systemd unit).*

### macOS (Apple Silicon & Intel)
1. Install prerequisite:
   ```bash
   brew install ddcctl
   ```
2. Download and extract **`MultiDisplayVCPServer_macOS_<arch>_v2.0.0.tar.gz`** (choose `arm64` for M1/M2/M3/M4 or `x64` for Intel):
   ```bash
   tar -xzf MultiDisplayVCPServer_macOS_<arch>_v2.0.0.tar.gz
   ```
3. Run the automated installer:
   ```bash
   ./install.sh
   ```
   *(Installs binary to `/usr/local/bin` and registers/loads the `com.multidisplayvcp.server` background LaunchAgent).*

---

## ⚙️ Configuration (macOS & Linux)

The Windows edition provides a system tray and graphical window to configure settings. Because macOS and Linux run as headless background daemons, their settings are stored in a standard `server.json` file.

### Configuration Locations
* **macOS**: `~/Library/Application Support/MultiDisplayVCP/server.json`
* **Linux**: `/etc/multidisplayvcp/server.json`

### Configuration Format
```json
{
  "port": 5001,
  "grpcPort": 5002,
  "password": "changeme"
}
```
*(Default password is `changeme` if unconfigured).*

### Applying Changes
After editing `server.json`, reload the background service:

* **macOS**:
  ```bash
  launchctl unload ~/Library/LaunchAgents/com.multidisplayvcp.server.plist
  launchctl load -w ~/Library/LaunchAgents/com.multidisplayvcp.server.plist
  ```

* **Linux**:
  ```bash
  sudo systemctl restart multidisplayvcp
  ```

### CLI Overrides & Environment Variables
You can also customize settings without a file:
* **Command Line Flags**: `--password <secret>`, `--port <num>`, `--grpc-port <num>`, `--config <path>`
* **Environment Variables**: `VCP_PASSWORD`, `VCP_PORT`, `VCP_GRPC_PORT`

---

## 🔌 Open Protocol Specification

Any client can control displays via the TCP port using simple HMAC-SHA256 authenticated commands:

* **Ping**: `PING|<timestamp>|<base64_hmac>`
* **Get Capabilities**: `GET_CAPS|<timestamp>|<base64_hmac>`
* **Set VCP Feature**: `SET_VCP|<timestamp>|<base64_hmac>|<monitor_id>|<vcp_hex_code>|<value>`

Where `<base64_hmac>` is `HMAC-SHA256(command + timestamp, password)`.

---

## 🛠️ Building from Source

Requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/):

```bash
# Build Windows version
dotnet publish MultiDisplayVCPServer.csproj -c Release

# Build Linux version
dotnet publish Linux/MultiDisplayVCPServer.Linux.csproj -c Release -r linux-x64

# Build macOS version
dotnet publish Mac/MultiDisplayVCPServer.Mac.csproj -c Release -r osx-arm64
```

---

## 📄 License

MIT License. See [LICENSE.txt](LICENSE.txt) for details.
