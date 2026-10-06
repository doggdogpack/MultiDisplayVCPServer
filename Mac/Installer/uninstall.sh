#!/usr/bin/env bash
set -e

echo "=== MultiDisplayVCP Server macOS Uninstaller ==="

PLIST_NAME="com.multidisplayvcp.server.plist"
USER_LAUNCH_DIR="${HOME}/Library/LaunchAgents"

echo "-> Unloading launchd service..."
launchctl unload -w "${USER_LAUNCH_DIR}/${PLIST_NAME}" 2>/dev/null || true
rm -f "${USER_LAUNCH_DIR}/${PLIST_NAME}"

echo "-> Removing binary..."
sudo rm -f "/usr/local/bin/MultiDisplayVCPServer.Mac"

echo ""
echo "MultiDisplayVCP Server has been uninstalled."
