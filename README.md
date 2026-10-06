# MultiDisplayVCP Server v2.0

A high-performance, cross-platform DDC/CI hardware display control daemon. Enables remote control of monitor hardware settings (Brightness, Contrast, Input Source, Audio Volume, Power Mode, and arbitrary VCP codes) across your local network.

---

## 🌟 Key Features

- **Cross-Platform**: Native support for **Windows** (WinForms GUI / System Tray), **Linux** (headless daemon / systemd), and **macOS** (headless daemon / launchd).
- **Dual-Mode Networking**:
  - **MagicOnion gRPC / HTTP/2 (Primary)**: High-throughput, zero-allocation protocol paired with Macro Deck 3 clients.
  - **Raw TCP Socket Listener (Legacy / Universal)**: Fast, text pipe-delimited protocol ensuring backward compatibility with Macro Deck 2 and open integrations (Bitfocus Companion, Stream Deck, Home Assistant, etc.).
- **Security**: HMAC-SHA256 challenge authentication with a sliding timestamp window to prevent replay attacks.
- **Hardware Integration**:
  - **Windows**: Windows Monitor Configuration API (`dxva2.dll` / Physical Monitors).
  - **Linux**: Kernel I2C interface (`ddcutil` / `/dev/i2c`).
  - **macOS**: I/O Kit Display Services (`ddcctl`).

---

## 🖥️ Compatibility

| Client | Protocol | Supported Server Versions |
| :--- | :--- | :--- |
| **[Macro Deck 3 Client](https://github.com/doggdogpack/MultiDisplayVCPClient/tree/Macro-Deck-3)** (v3.0+) | MagicOnion gRPC (HTTP/2) + TCP fallback | Server v2.0+ |
| **[Macro Deck 2 Client](https://github.com/doggdogpack/MultiDisplayVCPClient/tree/Macro-Deck-2)** (v2.0) | Raw TCP socket | Server v1.x and v2.0+ |
| **Bitfocus Companion / Stream Deck / Custom** | Raw TCP socket or gRPC | Server v2.0+ |

---

## 📦 Downloads & Installation

Pre-built binaries are available in the [Releases](https://github.com/doggdogpack/MultiDisplayVCPServer/releases) section:

### Windows
1. Download `MultiDisplayVCPServer_Windows_v2.0.0.zip`.
2. Extract and run `MultiDisplayVCPServer.exe`.
3. Set your desired port, password, and minimize to system tray.
4. Ensure your displays have **DDC/CI enabled** in their On-Screen Display (OSD) menus.

### Linux
1. Download `MultiDisplayVCPServer_Linux_x64_v2.0.0.zip`.
2. Ensure `ddcutil` and `i2c-dev` are installed:
   ```bash
   sudo apt install ddcutil i2c-tools
   sudo modprobe i2c-dev
   ```
3. Run the daemon:
   ```bash
   ./MultiDisplayVCPServer.Linux --port 5001 --grpc-port 5002 --password "yourpassword"
   ```

### macOS
1. Download `MultiDisplayVCPServer_macOS_arm64_v2.0.0.zip` (Apple Silicon) or `MultiDisplayVCPServer_macOS_x64_v2.0.0.zip` (Intel).
2. Ensure `ddcctl` is installed (`brew install ddcctl`).
3. Run the daemon:
   ```bash
   ./MultiDisplayVCPServer.Mac --port 5001 --grpc-port 5002 --password "yourpassword"
   ```

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
