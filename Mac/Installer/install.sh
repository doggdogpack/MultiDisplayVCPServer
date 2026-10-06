#!/usr/bin/env bash
set -e

echo "=== MultiDisplayVCP Server macOS Installer ==="

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="/usr/local/bin"
PLIST_NAME="com.multidisplayvcp.server.plist"
USER_LAUNCH_DIR="${HOME}/Library/LaunchAgents"

# Find binary (either in current dir or parent)
BIN_PATH=""
if [ -f "${SCRIPT_DIR}/MultiDisplayVCPServer.Mac" ]; then
  BIN_PATH="${SCRIPT_DIR}/MultiDisplayVCPServer.Mac"
elif [ -f "${SCRIPT_DIR}/../MultiDisplayVCPServer.Mac" ]; then
  BIN_PATH="${SCRIPT_DIR}/../MultiDisplayVCPServer.Mac"
fi

if [ -z "$BIN_PATH" ]; then
  echo "Error: MultiDisplayVCPServer.Mac binary not found in ${SCRIPT_DIR}"
  exit 1
fi

echo "-> Installing binary to ${BIN_DIR}..."
sudo mkdir -p "${BIN_DIR}"
sudo cp -f "${BIN_PATH}" "${BIN_DIR}/MultiDisplayVCPServer.Mac"
sudo chmod +x "${BIN_DIR}/MultiDisplayVCPServer.Mac"

echo "-> Setting up launchd service..."
mkdir -p "${USER_LAUNCH_DIR}"
cp -f "${SCRIPT_DIR}/${PLIST_NAME}" "${USER_LAUNCH_DIR}/${PLIST_NAME}"

# Unload previous instance if running
launchctl unload "${USER_LAUNCH_DIR}/${PLIST_NAME}" 2>/dev/null || true
# Load new instance
launchctl load -w "${USER_LAUNCH_DIR}/${PLIST_NAME}"

echo ""
echo "MultiDisplayVCP Server installed and running in background!"
echo "Logs:   /tmp/multidisplayvcp.log"
echo "Errors: /tmp/multidisplayvcp.err"
