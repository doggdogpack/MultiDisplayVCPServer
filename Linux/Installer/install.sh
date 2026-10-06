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

CONFIG_DIR="/etc/multidisplayvcp"
CONFIG_FILE="${CONFIG_DIR}/server.json"
echo "-> Setting up configuration at ${CONFIG_FILE}..."
mkdir -p "${CONFIG_DIR}"
if [ ! -f "${CONFIG_FILE}" ]; then
  if [ -f "${SCRIPT_DIR}/server.json" ]; then
    cp -f "${SCRIPT_DIR}/server.json" "${CONFIG_FILE}"
  else
    cat << 'EOF' > "${CONFIG_FILE}"
{
  "port": 5001,
  "grpcPort": 5002,
  "password": "changeme"
}
EOF
  fi
  chmod 644 "${CONFIG_FILE}"
  echo "   Created default config at: ${CONFIG_FILE}"
else
  echo "   Existing config preserved at: ${CONFIG_FILE}"
fi

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
echo "=========================================================="
echo "  MultiDisplayVCP Server installed and active!"
echo "=========================================================="
echo "Configuration: ${CONFIG_FILE}"
echo "Default password: changeme"
echo ""
echo "To set your password or change ports:"
echo "  1. Edit '${CONFIG_FILE}'"
echo "  2. Run: sudo systemctl restart multidisplayvcp"
echo ""
echo "Check status:  sudo systemctl status multidisplayvcp"
echo "View logs:     sudo journalctl -u multidisplayvcp -f"
echo "=========================================================="
