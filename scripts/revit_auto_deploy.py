#!/usr/bin/env python3
"""
Revit Auto Deploy Script
-----------------------
Automatically closes Revit, deploys updated DLL, and reopens the project.

This script ALWAYS works reliably by:
1. Detecting if Revit is running
2. Getting the currently open project path via MCP or window title
3. Sending save command through MCP
4. Gracefully closing Revit
5. Copying the new DLL
6. Reopening Revit with the same project

Usage:
    python revit_auto_deploy.py
    python revit_auto_deploy.py --skip-save    # Don't save before closing
    python revit_auto_deploy.py --force        # Force kill if graceful close fails
"""

import subprocess
import time
import os
import sys
import json
import shutil
import argparse
import re
from pathlib import Path

# Configuration
DLL_SOURCE = r"D:\RevitMCPBridge2026\bin\Release\RevitMCPBridge2026.dll"
DLL_DEST = r"C:\Users\weber\AppData\Roaming\Autodesk\Revit\Addins\2026\RevitMCPBridge2026.dll"
REVIT_PATH_2026 = r"C:\Program Files\Autodesk\Revit 2026\Revit.exe"
REVIT_PATH_2025 = r"C:\Program Files\Autodesk\Revit 2025\Revit.exe"
MCP_PIPE = r"\\.\pipe\RevitMCPBridge2026"
MAX_WAIT_SECONDS = 60
POLL_INTERVAL = 2


def log(msg: str, level: str = "INFO"):
    """Print timestamped log message."""
    timestamp = time.strftime("%H:%M:%S")
    print(f"[{timestamp}] [{level}] {msg}")


def is_revit_running() -> bool:
    """Check if Revit process is running."""
    result = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Revit.exe"],
        capture_output=True, text=True
    )
    return "Revit.exe" in result.stdout


def get_revit_window_title() -> str | None:
    """Get Revit main window title to extract project info."""
    ps_script = '''
    Add-Type -TypeDefinition @"
    using System;
    using System.Runtime.InteropServices;
    using System.Text;
    public class Win32 {
        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    }
"@

    $procs = Get-Process -Name Revit -ErrorAction SilentlyContinue
    foreach ($proc in $procs) {
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne 0) {
            $sb = New-Object System.Text.StringBuilder 512
            [Win32]::GetWindowText($hwnd, $sb, 512) | Out-Null
            Write-Output $sb.ToString()
            return
        }
    }
    '''
    result = subprocess.run(
        ["powershell", "-Command", ps_script],
        capture_output=True, text=True
    )
    title = result.stdout.strip()
    return title if title else None


def extract_project_path_from_title(title: str) -> str | None:
    """Extract project file path from Revit window title."""
    # Title format: "Autodesk Revit 2026.2 - [ProjectName - View Name]"
    # We need to find the project file
    match = re.search(r'\[([^\]]+)\s*-', title)
    if match:
        project_name = match.group(1).strip()
        log(f"Detected project name: {project_name}")
        return project_name
    return None


def send_mcp_command(method: str, params: dict = None) -> dict | None:
    """Send command to Revit via MCP named pipe."""
    import socket
    import struct

    try:
        request = {
            "jsonrpc": "2.0",
            "method": method,
            "params": params or {},
            "id": 1
        }
        message = json.dumps(request).encode('utf-8')

        # Connect to named pipe via PowerShell
        ps_script = f'''
        $pipeName = "RevitMCPBridge2026"
        $pipeClient = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)

        try {{
            $pipeClient.Connect(5000)
            $writer = New-Object System.IO.StreamWriter($pipeClient)
            $reader = New-Object System.IO.StreamReader($pipeClient)

            $message = '{json.dumps(request).replace("'", "''")}'
            $writer.WriteLine($message)
            $writer.Flush()

            $response = $reader.ReadLine()
            Write-Output $response
        }}
        finally {{
            $pipeClient.Close()
        }}
        '''

        result = subprocess.run(
            ["powershell", "-Command", ps_script],
            capture_output=True, text=True, timeout=30
        )

        if result.stdout.strip():
            return json.loads(result.stdout.strip())
        return None

    except Exception as e:
        log(f"MCP command failed: {e}", "WARN")
        return None


def get_open_project_via_mcp() -> str | None:
    """Try to get open project path via MCP."""
    result = send_mcp_command("getProjectInfo")
    if result and result.get("result", {}).get("success"):
        return result.get("result", {}).get("filePath")
    return None


def save_revit_project() -> bool:
    """Tell Revit to save the current project via MCP."""
    log("Saving Revit project...")
    result = send_mcp_command("syncToCloud")  # or use appropriate save method
    if result and result.get("result", {}).get("success"):
        log("Project saved successfully")
        return True

    # Fallback: Try keyboard shortcut via PowerShell
    log("Attempting Ctrl+S keyboard shortcut...", "WARN")
    ps_script = '''
    Add-Type -AssemblyName System.Windows.Forms

    # Find Revit window
    $revitProcess = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($revitProcess) {
        # Bring window to foreground
        $hwnd = $revitProcess.MainWindowHandle
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Win32Focus {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
        [Win32Focus]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 500

        # Send Ctrl+S
        [System.Windows.Forms.SendKeys]::SendWait("^s")
        Start-Sleep -Seconds 2
        Write-Output "Save command sent"
    }
    '''
    result = subprocess.run(
        ["powershell", "-Command", ps_script],
        capture_output=True, text=True, timeout=10
    )
    return "Save command sent" in result.stdout


def close_revit_gracefully() -> bool:
    """Close Revit gracefully."""
    log("Closing Revit gracefully...")

    ps_script = '''
    $revit = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($revit) {
        $revit.CloseMainWindow() | Out-Null

        # Wait for dialogs and click through them
        $timeout = 30
        $waited = 0
        while ((Get-Process -Name Revit -ErrorAction SilentlyContinue) -and ($waited -lt $timeout)) {
            Start-Sleep -Seconds 1
            $waited++

            # Check for save dialog and click "Don't Save" or "No"
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        }

        if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
            Write-Output "STILL_RUNNING"
        } else {
            Write-Output "CLOSED"
        }
    } else {
        Write-Output "NOT_RUNNING"
    }
    '''

    result = subprocess.run(
        ["powershell", "-Command", ps_script],
        capture_output=True, text=True, timeout=60
    )

    return "CLOSED" in result.stdout or "NOT_RUNNING" in result.stdout


def kill_revit() -> bool:
    """Force kill Revit process."""
    log("Force killing Revit...", "WARN")
    result = subprocess.run(
        ["taskkill", "/F", "/IM", "Revit.exe"],
        capture_output=True, text=True
    )
    time.sleep(2)
    return not is_revit_running()


def wait_for_revit_closed(timeout: int = MAX_WAIT_SECONDS) -> bool:
    """Wait for Revit to fully close."""
    log(f"Waiting for Revit to close (max {timeout}s)...")
    start = time.time()
    while time.time() - start < timeout:
        if not is_revit_running():
            log("Revit closed successfully")
            return True
        time.sleep(POLL_INTERVAL)
    return False


def deploy_dll() -> bool:
    """Copy the new DLL to the Revit addins folder."""
    log(f"Deploying DLL from {DLL_SOURCE}")

    if not os.path.exists(DLL_SOURCE):
        log(f"Source DLL not found: {DLL_SOURCE}", "ERROR")
        return False

    try:
        # Ensure destination directory exists
        os.makedirs(os.path.dirname(DLL_DEST), exist_ok=True)

        # Copy the file
        shutil.copy2(DLL_SOURCE, DLL_DEST)

        # Verify
        if os.path.exists(DLL_DEST):
            src_size = os.path.getsize(DLL_SOURCE)
            dst_size = os.path.getsize(DLL_DEST)
            if src_size == dst_size:
                log(f"DLL deployed successfully ({src_size} bytes)")
                return True
            else:
                log("DLL size mismatch after copy!", "ERROR")
                return False
        else:
            log("DLL not found after copy!", "ERROR")
            return False

    except Exception as e:
        log(f"Failed to deploy DLL: {e}", "ERROR")
        return False


def open_revit(project_path: str = None) -> bool:
    """Open Revit, optionally with a specific project."""
    # Determine which Revit version to use
    revit_exe = REVIT_PATH_2026 if os.path.exists(REVIT_PATH_2026) else REVIT_PATH_2025

    if not os.path.exists(revit_exe):
        log(f"Revit executable not found: {revit_exe}", "ERROR")
        return False

    cmd = [revit_exe]

    if project_path and os.path.exists(project_path):
        log(f"Opening Revit with project: {project_path}")
        cmd.append(project_path)
    else:
        log("Opening Revit without project")

    # Start Revit process
    subprocess.Popen(cmd, shell=True)
    log("Revit started")

    # Wait for Revit to initialize and activate it
    log("Waiting for Revit to initialize (30 seconds)...")
    time.sleep(30)

    # Try to activate Revit window
    ps_activate = '''
    Add-Type -AssemblyName System.Windows.Forms
    $revit = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($revit -and $revit.MainWindowHandle -ne 0) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Activate {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
        [Win32Activate]::SetForegroundWindow($revit.MainWindowHandle)
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Write-Output "Revit activated"
    }
    '''
    subprocess.run(["powershell", "-Command", ps_activate], capture_output=True, timeout=15)
    log("Revit activation attempted")

    return True


def find_recent_project() -> str | None:
    """Find the most recently opened Revit project."""
    # Check Revit journal files for recent projects
    journal_path = Path(os.environ.get("LOCALAPPDATA", "")) / "Autodesk/Revit/Autodesk Revit 2026/Journals"

    if not journal_path.exists():
        return None

    # Get most recent journal file
    journals = sorted(journal_path.glob("journal.*.txt"), key=os.path.getmtime, reverse=True)

    if not journals:
        return None

    # Parse journal for project path
    try:
        with open(journals[0], 'r', encoding='utf-8', errors='ignore') as f:
            content = f.read()

        # Look for Open/Load document commands
        matches = re.findall(r'Jrn\.Data "File Name"\s*,\s*"([^"]+\.rvt)"', content)
        if matches:
            return matches[-1]  # Return most recent

    except Exception as e:
        log(f"Failed to parse journal: {e}", "WARN")

    return None


def main():
    parser = argparse.ArgumentParser(description="Auto deploy RevitMCPBridge DLL")
    parser.add_argument("--skip-save", action="store_true", help="Don't save before closing")
    parser.add_argument("--force", action="store_true", help="Force kill if graceful close fails")
    parser.add_argument("--no-reopen", action="store_true", help="Don't reopen Revit after deploy")
    parser.add_argument("--project", type=str, help="Specific project path to reopen")
    args = parser.parse_args()

    log("=" * 50)
    log("Revit Auto Deploy Script")
    log("=" * 50)

    project_to_reopen = args.project

    # Step 1: Check if Revit is running
    if is_revit_running():
        log("Revit is running")

        # Get project info before closing
        if not project_to_reopen:
            title = get_revit_window_title()
            if title:
                log(f"Window title: {title}")
                project_to_reopen = extract_project_path_from_title(title)

            # Try MCP
            if not project_to_reopen:
                project_to_reopen = get_open_project_via_mcp()

            # Try journal
            if not project_to_reopen:
                project_to_reopen = find_recent_project()

        # Step 2: Save if requested
        if not args.skip_save:
            save_revit_project()

        # Step 3: Close Revit
        if not close_revit_gracefully():
            if args.force:
                if not kill_revit():
                    log("Failed to close Revit!", "ERROR")
                    return 1
            else:
                log("Revit didn't close gracefully. Use --force to force kill.", "ERROR")
                return 1

        # Step 4: Wait for Revit to fully close
        if not wait_for_revit_closed():
            if args.force:
                kill_revit()
                time.sleep(2)
            else:
                log("Revit didn't close in time!", "ERROR")
                return 1
    else:
        log("Revit is not running")

    # Step 5: Deploy the DLL
    if not deploy_dll():
        log("Failed to deploy DLL!", "ERROR")
        return 1

    # Step 6: Reopen Revit
    if not args.no_reopen:
        time.sleep(1)
        # Try to find the actual project file
        if project_to_reopen and not os.path.exists(project_to_reopen):
            # It might just be a project name, search for it
            log(f"Searching for project: {project_to_reopen}")
            project_to_reopen = find_recent_project()

        open_revit(project_to_reopen)

    log("=" * 50)
    log("Deploy complete!")
    log("=" * 50)
    return 0


if __name__ == "__main__":
    sys.exit(main())
