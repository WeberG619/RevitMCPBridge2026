RevitMCPBridge - AI Integration for Autodesk Revit
===================================================

Thank you for installing RevitMCPBridge!

QUICK START
-----------
1. Open Revit 2025 or 2026
   - MCP Bridge loads automatically (check the Add-Ins tab)

2. Open Claude Desktop or Claude Code
   - If you chose "Auto-configure Claude" during install, you're ready
   - Otherwise, see MANUAL SETUP below

3. Ask Claude: "Can you ping Revit?"
   - If it works, you have full AI access to your Revit model

WHAT CAN CLAUDE DO WITH REVIT?
-------------------------------
- Create and modify walls, doors, windows, rooms
- Generate construction document sheets
- Place annotations, dimensions, tags
- Create and populate schedules
- Run code compliance checks
- Extract model data to JSON/Excel
- Manage views, filters, worksets
- And 1,000+ more operations

MANUAL SETUP (if auto-configure wasn't selected)
-------------------------------------------------
Add this to your Claude Desktop config:
  %APPDATA%\Claude\claude_desktop_config.json

{
  "mcpServers": {
    "revit-bridge-2026": {
      "command": "python",
      "args": ["C:\\Program Files\\RevitMCPBridge\\wrapper\\revit_mcp_wrapper.py"],
      "env": { "REVIT_PIPE_NAME": "RevitMCPBridge2026" }
    }
  }
}

For Revit 2025, change the pipe name to "RevitMCPBridge2025".

REQUIREMENTS
------------
- Autodesk Revit 2025 and/or 2026
- Python 3.10+ (for the MCP wrapper)
- Claude Desktop or Claude Code

TROUBLESHOOTING
---------------
- "No response from Revit" = Revit isn't running or MCP Bridge didn't load
  -> Check Add-Ins tab in Revit for "MCP Bridge"
  -> Restart Revit if needed

- "PowerShell not found" = Running from non-Windows environment
  -> The wrapper must run on Windows (or WSL with powershell.exe access)

- "Timeout connecting" = Revit is busy or has a dialog open
  -> Close any open dialogs in Revit
  -> Click in the drawing area, then retry

SUPPORT
-------
GitHub: https://github.com/WeberG619/RevitMCPBridge2026
Issues: https://github.com/WeberG619/RevitMCPBridge2026/issues

Free and open source (MIT License)
Built by BIM Ops Studio - weber@bimopsstudio.com
