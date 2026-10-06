#!/usr/bin/env bash
set -e

echo "=== MultiDisplayVCP Server Linux Installer ==="

# Check for root / sudo
if [ "$EUID" -ne 0 ]; then
  echo "Error: Please run as root or with sudo:"
  echo "  sudo ./install.sh"
  exit 1
fi

INSTALL_DIR="/usr/local/bin"
SERVICE_DIR="/etc/systemd/system"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Find binary (either in same folder or parent directory)
BIN_PATH=""
if [ -f "${SCRIPT_DIR}/MultiDisplayVCPServer.Linux" ]; then
  BIN_PATH="${SCRIPT_DIR}/MultiDisplayVCPServer.Linux"
elif [ -f "${SCRIPT_DIR}/../MultiDisplayVCPServer.Linux" ]; then
  BIN_PATH="${SCRIPT_DIR}/../MultiDisplayVCPServer.Linux"
fi

if [ -z "$BIN_PATH" ]; then
  echo "Error: MultiDisplayVCPServer.Linux binary not found in ${SCRIPT_DIR}"
  exit 1
fi

echo "-> Installing binary to ${INSTALL_DIR}..."
cp -f "${BIN_PATH}" "${INSTALL_DIR}/MultiDisplayVCPServer.Linux"
chmod +x "${INSTALL_DIR}/MultiDisplayVCPServer.Linux"

echo "-> Configuring i2c permissions (ddcutil requirement)..."
if command -v usermod &> /dev/null && [ -n "$SUDO_USER" ]; then
  usermod -aG i2c "$SUDO_USER" 2>/dev/null || true
fi
modprobe i2c-dev 2>/dev/null || true

# Install systemd service
if [ -f "${SCRIPT_DIR}/multidisplayvcp.service" ]; then
  echo "-> Installing systemd service..."
  cp -f "${SCRIPT_DIR}/multidisplayvcp.service" "${SERVICE_DIR}/multidisplayvcp.service"
  systemctl daemon-reload
  systemctl enable multidisplayvcp.service
  systemctl restart multidisplayvcp.service
  echo "-> Service multidisplayvcp started and enabled on boot."
fi

echo ""
echo "MultiDisplayVCP Server installed successfully!"
echo "Check status:  sudo systemctl status multidisplayvcp"
echo "View logs:     sudo journalctl -u multidisplayvcp -f"
