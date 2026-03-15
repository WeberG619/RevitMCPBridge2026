"""
Auto-configure Claude Desktop and Claude Code to use RevitMCPBridge.

This script modifies the MCP settings files so Claude can talk to Revit
immediately after installation — no manual JSON editing required.

Supports:
  - Claude Desktop: %APPDATA%\Claude\claude_desktop_config.json
  - Claude Code:    ~/.claude/settings.local.json (WSL or native)
"""
import json
import os
import sys
import shutil
from pathlib import Path


def get_wrapper_path(install_dir: str) -> str:
    """Get the path to the MCP wrapper script."""
    return os.path.join(install_dir, "wrapper", "revit_mcp_wrapper.py")


def configure_claude_desktop(wrapper_path: str) -> bool:
    """Add RevitMCPBridge to Claude Desktop's MCP config."""
    config_dir = os.path.join(os.environ.get("APPDATA", ""), "Claude")
    config_file = os.path.join(config_dir, "claude_desktop_config.json")

    if not os.path.isdir(config_dir):
        print(f"  Claude Desktop config dir not found: {config_dir}")
        return False

    # Read existing config or start fresh
    config = {}
    if os.path.exists(config_file):
        backup = config_file + ".backup"
        shutil.copy2(config_file, backup)
        print(f"  Backed up existing config to: {backup}")
        with open(config_file, "r") as f:
            try:
                config = json.load(f)
            except json.JSONDecodeError:
                config = {}

    if "mcpServers" not in config:
        config["mcpServers"] = {}

    # Add Revit 2026 server
    config["mcpServers"]["revit-bridge-2026"] = {
        "command": "python",
        "args": [wrapper_path],
        "env": {
            "REVIT_PIPE_NAME": "RevitMCPBridge2026"
        }
    }

    # Add Revit 2025 server
    config["mcpServers"]["revit-bridge-2025"] = {
        "command": "python",
        "args": [wrapper_path],
        "env": {
            "REVIT_PIPE_NAME": "RevitMCPBridge2025"
        }
    }

    os.makedirs(config_dir, exist_ok=True)
    with open(config_file, "w") as f:
        json.dump(config, f, indent=2)

    print(f"  Configured Claude Desktop: {config_file}")
    return True


def configure_claude_code(wrapper_path: str) -> bool:
    """Add RevitMCPBridge to Claude Code's MCP config."""
    # Claude Code settings in user home
    home = Path.home()
    config_file = home / ".claude" / "settings.local.json"

    config = {}
    if config_file.exists():
        backup = str(config_file) + ".backup"
        shutil.copy2(config_file, backup)
        print(f"  Backed up existing config to: {backup}")
        with open(config_file, "r") as f:
            try:
                config = json.load(f)
            except json.JSONDecodeError:
                config = {}

    if "mcpServers" not in config:
        config["mcpServers"] = {}

    # Convert Windows path for WSL if needed
    wsl_wrapper_path = wrapper_path
    if wrapper_path.startswith("C:") or wrapper_path.startswith("D:"):
        drive = wrapper_path[0].lower()
        wsl_wrapper_path = f"/mnt/{drive}" + wrapper_path[2:].replace("\\", "/")

    # Add Revit 2026 server
    config["mcpServers"]["revit-bridge-2026"] = {
        "type": "stdio",
        "command": "python",
        "args": [wrapper_path],
        "env": {
            "REVIT_PIPE_NAME": "RevitMCPBridge2026"
        }
    }

    # Add Revit 2025 server
    config["mcpServers"]["revit-bridge-2025"] = {
        "type": "stdio",
        "command": "python",
        "args": [wrapper_path],
        "env": {
            "REVIT_PIPE_NAME": "RevitMCPBridge2025"
        }
    }

    config_file.parent.mkdir(parents=True, exist_ok=True)
    with open(config_file, "w") as f:
        json.dump(config, f, indent=2)

    print(f"  Configured Claude Code: {config_file}")
    return True


def main():
    install_dir = None
    for i, arg in enumerate(sys.argv):
        if arg == "--install-dir" and i + 1 < len(sys.argv):
            install_dir = sys.argv[i + 1]

    if not install_dir:
        print("Usage: setup_claude.py --install-dir <path>")
        sys.exit(1)

    wrapper_path = get_wrapper_path(install_dir)
    print(f"RevitMCPBridge MCP Setup")
    print(f"Wrapper: {wrapper_path}")
    print()

    success = False

    print("Configuring Claude Desktop...")
    if configure_claude_desktop(wrapper_path):
        success = True
    else:
        print("  Skipped (not installed)")

    print()
    print("Configuring Claude Code...")
    if configure_claude_code(wrapper_path):
        success = True
    else:
        print("  Skipped (not installed)")

    if success:
        print()
        print("Done! Restart Claude to connect to Revit.")
        print()
        print("Quick test: Open Revit with MCP Bridge loaded, then ask Claude:")
        print('  "Can you ping Revit?"')
    else:
        print()
        print("Warning: Neither Claude Desktop nor Claude Code was found.")
        print("You can manually configure MCP settings later.")

    sys.exit(0)


if __name__ == "__main__":
    main()
