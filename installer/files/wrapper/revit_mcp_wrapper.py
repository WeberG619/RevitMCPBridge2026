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
import time
import logging
import urllib.request
import urllib.error
from mcp.server.fastmcp import FastMCP

# Redirect all logging to stderr so stdout stays clean for MCP JSON-RPC
logging.basicConfig(stream=sys.stderr, level=logging.WARNING)

# Configurable pipe name - matches the Revit version you're running
PIPE_NAME = os.environ.get("REVIT_PIPE_NAME", "RevitMCPBridge2026")
SERVER_NAME = f"revit-bridge-{PIPE_NAME.replace('RevitMCPBridge', '')}"
BIM_MONKEY_API_KEY = os.environ.get("BIM_MONKEY_API_KEY", "")
BIM_MONKEY_API_URL = "https://bimmonkey-production.up.railway.app"

mcp = FastMCP(SERVER_NAME)

SHEET_PLACEABLE_TYPES = {"FloorPlan", "CeilingPlan", "Elevation", "Section",
                          "Detail", "DraftingView", "Legend", "Schedule",
                          "EngineeringPlan", "AreaPlan"}


def _get_revit_array(container, array_key):
    """Extract an array from a Revit response object or direct list."""
    if isinstance(container, list):
        return container
    if isinstance(container, dict):
        v = container.get(array_key)
        if isinstance(v, list):
            return v
    return []


def build_model_summary(raw_model):
    """
    Transform raw Revit method responses into a lean, structured model summary.

    Revit methods return nested objects like {"success": true, "views": [...]}.
    The backend expects flat top-level arrays: existingViews, existingSheets, levels, etc.
    This function reshapes AND prunes to only the fields the backend AI actually needs.

    A 500-view model goes from ~500KB to ~20KB after this transform.
    """
    # Views — filter to sheet-placeable types, keep only id/name/viewType
    raw_views = _get_revit_array(raw_model.get("views", {}), "views")
    existing_views = [
        {"id": v["id"], "name": v["name"], "viewType": v["viewType"]}
        for v in raw_views
        if v.get("viewType") in SHEET_PLACEABLE_TYPES and v.get("id") and v.get("name")
    ]
    existing_drafting_views = [
        {"id": v["id"], "name": v["name"]}
        for v in raw_views
        if v.get("viewType") == "DraftingView" and v.get("id") and v.get("name")
    ]

    # Sheets — keep only sheetNumber and sheetName
    raw_sheets = _get_revit_array(raw_model.get("sheets", {}), "sheets")
    existing_sheets = [
        {"sheetNumber": s.get("sheetNumber"), "sheetName": s.get("sheetName") or s.get("name")}
        for s in raw_sheets
        if s.get("sheetNumber")
    ]

    # Levels — keep id, name, elevation
    raw_levels = _get_revit_array(raw_model.get("levels", {}), "levels")
    levels = [
        {"id": l.get("id"), "name": l.get("name"), "elevation": round(float(l.get("elevation", 0) or 0), 2)}
        for l in raw_levels
        if l.get("name")
    ]

    # Rooms — keep id, name, number, area, levelName
    raw_rooms = _get_revit_array(raw_model.get("rooms", {}), "rooms")
    rooms = [
        {
            "id": r.get("id"),
            "name": r.get("name"),
            "number": r.get("number"),
            "area": round(float(r.get("area", 0) or 0), 1),
            "levelName": r.get("levelName") or r.get("level"),
        }
        for r in raw_rooms
        if r.get("name")
    ]

    # Wall types — keep id, name, width, function (drop all layer details)
    raw_wall_types = _get_revit_array(raw_model.get("wallTypes", {}), "wallTypes")
    wall_types = [
        {
            "id": wt.get("id"),
            "name": wt.get("name"),
            "width": round(float(wt.get("width", 0) or 0), 3),
            "function": wt.get("function"),
        }
        for wt in raw_wall_types
        if wt.get("name")
    ]

    # Project info — essentials only
    doc = raw_model.get("documentInfo", {})
    if not isinstance(doc, dict):
        doc = {}
    pi = doc.get("projectInfo") or {}
    if not isinstance(pi, dict):
        pi = {}
    project_info = {k: v for k, v in {
        "name": pi.get("name"),
        "address": pi.get("address"),
        "number": pi.get("number"),
    }.items() if v}

    project_name = (
        raw_model.get("projectName") or
        project_info.get("name") or
        doc.get("title") or
        "Unnamed Project"
    )

    return {
        "projectName": project_name,
        "projectInfo": project_info,
        "buildingType": raw_model.get("buildingType", "residential"),
        "conditions": raw_model.get("conditions", []),
        "levels": levels,
        "rooms": rooms,
        "existingSheets": existing_sheets,
        "wallTypes": wall_types,
        "existingViews": existing_views,
        "existingDraftingViews": existing_drafting_views,
    }


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

    for attempt in range(2):
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
                if attempt == 0:
                    time.sleep(0.5)
                    continue
                return {"success": False, "error": f"No response from Revit. Is Revit running with MCP Bridge loaded? (Pipe: {PIPE_NAME})"}

            response = json.loads(output)

            # Retry once on broken pipe (race condition at startup)
            if attempt == 0 and not response.get("success", True) and "Pipe is broken" in response.get("error", ""):
                time.sleep(0.5)
                continue

            return response
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


@mcp.tool()
async def bim_monkey_generate(model_data: str) -> dict:
    """
    Submit Revit model data to the BIM Monkey backend and receive a complete
    CD plan trained on this firm's drawing standards.

    THIS MUST BE CALLED BEFORE CREATING ANY SHEETS OR VIEWS.
    Never improvise a CD plan — always use this tool to get the plan from the backend.

    Args:
        model_data: JSON string of the full Revit model snapshot. Build this by
                    calling revit_execute for: getDocumentInfo, getLevels, getRooms,
                    getSheets, getWallTypes, getViews — then combine into one object.

    Returns:
        {
          "success": true,
          "generationId": 42,          # save this — pass to bim_monkey_mark_executed when done
          "plan": { ... }              # the full CD plan to execute in Revit
        }
    """
    if not BIM_MONKEY_API_KEY:
        return {"success": False, "error": "BIM_MONKEY_API_KEY not set in environment."}

    try:
        raw_model = json.loads(model_data) if isinstance(model_data, str) else model_data
    except json.JSONDecodeError as e:
        return {"success": False, "error": f"Invalid model_data JSON: {e}"}

    # Reshape raw Revit responses into lean, structured model summary.
    # This fixes the backend's existingViews/existingSheets/levels extraction
    # (which expects flat top-level arrays, not nested Revit response objects)
    # and prunes all fields the AI doesn't need — reducing a 500-view model
    # from ~500KB to ~20KB.
    model_obj = build_model_summary(raw_model)

    payload = json.dumps(model_obj).encode("utf-8")
    req = urllib.request.Request(
        f"{BIM_MONKEY_API_URL}/api/generate",
        data=payload,
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {BIM_MONKEY_API_KEY}",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=300) as resp:
            raw = b""
            while True:
                chunk = resp.read(8192)
                if not chunk:
                    break
                raw += chunk
            # Strip leading keep-alive newlines the server sends during generation
            data = json.loads(raw.decode("utf-8").strip())
            if not data.get("success"):
                return {"success": False, "error": data.get("error", "Backend returned failure")}
            return {
                "success": True,
                "generationId": data.get("generationId"),
                "plan": data.get("plan"),
                "examplesUsed": data.get("examplesUsed", 0),
            }
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")[:500]
        return {"success": False, "error": f"HTTP {e.code}: {body}"}
    except urllib.error.URLError as e:
        return {"success": False, "error": f"Network error: {e.reason}"}
    except Exception as e:
        return {"success": False, "error": str(e)}


@mcp.tool()
async def bim_monkey_report_issues(generation_id: int, issues: list) -> dict:
    """
    Report issues encountered during a Revit execution run to the BIM Monkey dashboard.
    Call this AFTER completing all Revit operations, before bim_monkey_mark_executed.

    Each issue should be a dict with:
      - type: short category (e.g. "placement_error", "method_fallback", "duplicate_view", "schedule_skipped")
      - message: one-sentence description of what happened
      - item: optional — which sheet/view/detail was affected
      - recommendation: optional — what should change in the next run

    Args:
        generation_id: The generationId returned by bim_monkey_generate.
        issues: List of issue dicts encountered during execution.

    Returns:
        {"success": true} on success.
    """
    if not BIM_MONKEY_API_KEY:
        return {"success": False, "error": "BIM_MONKEY_API_KEY not set in environment."}

    payload = json.dumps({"issues": issues}).encode("utf-8")
    req = urllib.request.Request(
        f"{BIM_MONKEY_API_URL}/api/generation/{generation_id}/system-report",
        data=payload,
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {BIM_MONKEY_API_KEY}",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            return {"success": data.get("success", True)}
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")[:300]
        return {"success": False, "error": f"HTTP {e.code}: {body}"}
    except Exception as e:
        return {"success": False, "error": str(e)}


@mcp.tool()
async def bim_monkey_mark_executed(generation_id: int) -> dict:
    """
    Mark a generation as executed in Revit. Call this after all sheets, views,
    and details from the CD plan have been created in Revit.

    Args:
        generation_id: The generationId returned by bim_monkey_generate.

    Returns:
        {"success": true} on success.
    """
    if not BIM_MONKEY_API_KEY:
        return {"success": False, "error": "BIM_MONKEY_API_KEY not set in environment."}

    req = urllib.request.Request(
        f"{BIM_MONKEY_API_URL}/api/generation/{generation_id}/mark-executed",
        data=b"{}",
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {BIM_MONKEY_API_KEY}",
        },
        method="PATCH",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            return {"success": True, "revit_executed_at": data.get("revit_executed_at")}
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")[:300]
        return {"success": False, "error": f"HTTP {e.code}: {body}"}
    except Exception as e:
        return {"success": False, "error": str(e)}


if __name__ == "__main__":
    mcp.run()
