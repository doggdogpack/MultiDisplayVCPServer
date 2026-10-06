#!/usr/bin/env bash
set -e

echo "=== MultiDisplayVCP Server Linux Uninstaller ==="

if [ "$EUID" -ne 0 ]; then
  echo "Error: Please run as root or with sudo:"
  echo "  sudo ./uninstall.sh"
  exit 1
fi

echo "-> Stopping and disabling multidisplayvcp service..."
systemctl stop multidisplayvcp.service 2>/dev/null || true
systemctl disable multidisplayvcp.service 2>/dev/null || true
rm -f /etc/systemd/system/multidisplayvcp.service
systemctl daemon-reload 2>/dev/null || true

echo "-> Removing binary..."
rm -f /usr/local/bin/MultiDisplayVCPServer.Linux

echo ""
echo "MultiDisplayVCP Server has been completely uninstalled."
