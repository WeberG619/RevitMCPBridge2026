#!/usr/bin/env python3
"""
RevitMCPBridge - MCP Wrapper for Claude
Bridges Claude (stdio MCP) to Revit (Windows named pipes).

Usage:
  python revit_mcp_wrapper.py                    # defaults to RevitMCPBridge2026
  REVIT_PIPE_NAME=RevitMCPBridge2025 python revit_mcp_wrapper.py

This script is the MCP server that Claude connects to. It translates
Claude's MCP tool calls into named pipe messages that Revit understands.
"""
import subprocess
import json
import sys
import os
from mcp.server.fastmcp import FastMCP

# Configurable pipe name - matches the Revit version you're running
PIPE_NAME = os.environ.get("REVIT_PIPE_NAME", "RevitMCPBridge2026")
SERVER_NAME = f"revit-bridge-{PIPE_NAME.replace('RevitMCPBridge', '')}"

mcp = FastMCP(SERVER_NAME)


def call_revit(method: str, params: dict = None) -> dict:
    """Send a command to Revit via Windows named pipe through PowerShell."""
    if params is None:
        params = {}

    request = json.dumps({
        "jsonrpc": "2.0",
        "method": method,
        "params": params,
        "id": 1
    })

    ps_script = f'''
$ErrorActionPreference = "Stop"
$pipeName = "{PIPE_NAME}"
try {{
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(5000)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $reader = New-Object System.IO.StreamReader($pipe)
    $writer.AutoFlush = $true

    $request = @'
{request}
'@

    $writer.WriteLine($request)
    $response = $reader.ReadLine()
    Write-Output $response
    $pipe.Close()
}} catch {{
    $errorResult = @{{ success = $false; error = $_.Exception.Message }} | ConvertTo-Json -Compress
    Write-Output $errorResult
}}
'''

    try:
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command", ps_script],
            capture_output=True,
            text=True,
            timeout=30
        )

        output = result.stdout.strip()
        # Filter out any system messages
        lines = [l for l in output.split('\n')
                 if l.strip() and not l.startswith('Drop Zone') and not l.startswith('Claude Code')]
        output = '\n'.join(lines).strip()

        if not output:
            return {"success": False, "error": f"No response from Revit. Is Revit running with MCP Bridge loaded? (Pipe: {PIPE_NAME})"}

        return json.loads(output)
    except subprocess.TimeoutExpired:
        return {"success": False, "error": f"Timeout connecting to Revit. Make sure Revit is open and MCP Bridge is loaded. (Pipe: {PIPE_NAME})"}
    except json.JSONDecodeError:
        return {"success": False, "error": f"Invalid response from Revit: {output[:200]}"}
    except FileNotFoundError:
        return {"success": False, "error": "PowerShell not found. This wrapper requires Windows with PowerShell."}
    except Exception as e:
        return {"success": False, "error": str(e)}


@mcp.tool()
async def revit_ping() -> dict:
    """Test connection to Revit. Returns Revit version and project info if connected."""
    return call_revit("ping")


@mcp.tool()
async def revit_get_levels() -> dict:
    """Get all levels in the current Revit document."""
    return call_revit("getLevels")


@mcp.tool()
async def revit_get_active_view() -> dict:
    """Get the currently active view in Revit."""
    return call_revit("getActiveView")


@mcp.tool()
async def revit_get_document_info() -> dict:
    """Get information about the active Revit document (name, path, phase, etc.)."""
    return call_revit("getDocumentInfo")


@mcp.tool()
async def revit_get_wall_types() -> dict:
    """Get all available wall types in the document."""
    return call_revit("getWallTypes")


@mcp.tool()
async def revit_get_sheets() -> dict:
    """Get all sheets in the document."""
    return call_revit("getSheets")


@mcp.tool()
async def revit_create_wall(
    start_x: float,
    start_y: float,
    end_x: float,
    end_y: float,
    height: float = 10.0,
    wall_type_id: int = None,
    level_id: int = None
) -> dict:
    """Create a wall in Revit between two points."""
    params = {
        "startPoint": {"x": start_x, "y": start_y, "z": 0},
        "endPoint": {"x": end_x, "y": end_y, "z": 0},
        "height": height
    }
    if wall_type_id:
        params["wallTypeId"] = wall_type_id
    if level_id:
        params["levelId"] = level_id
    return call_revit("createWall", params)


@mcp.tool()
async def revit_execute(method_name: str, parameters: str = "{}") -> dict:
    """
    Execute ANY RevitMCPBridge method by name. This is the universal tool
    that gives you access to all 1,114 methods (2026) or 437 methods (2025).

    Args:
        method_name: The method to call (e.g., 'getRooms', 'placeDoor', 'createSheet',
                     'getElements', 'setParameter', 'createSchedule', etc.)
        parameters: JSON string of parameters to pass to the method.

    Examples:
        revit_execute("getRooms", "{}")
        revit_execute("placeDoor", '{"wallId": 12345, "location": {"x": 10, "y": 5}}')
        revit_execute("createSheet", '{"number": "A101", "name": "Floor Plan"}')

    Returns:
        Result dict from Revit with 'success' flag and data or error.
    """
    try:
        params = json.loads(parameters) if parameters else {}
    except json.JSONDecodeError:
        return {"success": False, "error": f"Invalid JSON parameters: {parameters[:200]}"}
    return call_revit(method_name, params)


if __name__ == "__main__":
    mcp.run()
