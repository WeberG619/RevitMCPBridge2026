; RevitMCPBridge Installer - Inno Setup Script
; Installs RevitMCPBridge for Revit 2024, 2025, and/or 2026
; Includes Python MCP wrapper for Claude integration

#define MyAppName "RevitMCPBridge"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "BIM Ops Studio"
#define MyAppURL "https://github.com/WeberG619/RevitMCPBridge2026"

[Setup]
AppId={{8B8B6F55-9C7A-4F5E-8D8A-1B2C3D4E5F00}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=RevitMCPBridge-{#MyAppVersion}-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\icon.ico
SetupIconFile=assets\icon.ico
WizardImageFile=assets\wizard.bmp
WizardSmallImageFile=assets\wizard-small.bmp

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation (Revit 2024 + 2025 + 2026 + MCP Wrapper)"
Name: "revit2026"; Description: "Revit 2026 only"
Name: "revit2025"; Description: "Revit 2025 only"
Name: "revit2024"; Description: "Revit 2024 only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "revit2026"; Description: "RevitMCPBridge for Revit 2026 (1,114 methods)"; Types: full revit2026 custom
Name: "revit2025"; Description: "RevitMCPBridge for Revit 2025 (437 methods)"; Types: full revit2025 custom
Name: "revit2024"; Description: "RevitMCPBridge for Revit 2024 (437 methods)"; Types: full revit2024 custom
Name: "mcpwrapper"; Description: "Python MCP Wrapper (required for Claude integration)"; Types: full revit2026 revit2025 revit2024 custom; Flags: fixed

[Files]
; === Revit 2026 files ===
Source: "files\2026\RevitMCPBridge2026.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "files\2026\appsettings.json"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion
Source: "files\2026\RevitMCPBridge2026.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit2026; Flags: ignoreversion

; === Revit 2025 files ===
Source: "files\2025\RevitMCPBridge2025.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "files\2025\RevitMCPBridge2025.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "files\2025\appsettings.json"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "files\2025\Newtonsoft.Json.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "files\2025\Serilog.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion
Source: "files\2025\Serilog.Sinks.File.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit2025; Flags: ignoreversion

; === Revit 2024 files ===
Source: "files\2024\RevitMCPBridge2024.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "files\2024\RevitMCPBridge2024.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "files\2024\appsettings.json"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "files\2024\Newtonsoft.Json.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "files\2024\Serilog.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion
Source: "files\2024\Serilog.Sinks.File.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: revit2024; Flags: ignoreversion

; === MCP Wrapper (Python bridge for Claude) ===
Source: "files\wrapper\revit_mcp_wrapper.py"; DestDir: "{app}\wrapper"; Components: mcpwrapper; Flags: ignoreversion
Source: "files\wrapper\requirements.txt"; DestDir: "{app}\wrapper"; Components: mcpwrapper; Flags: ignoreversion
Source: "files\wrapper\setup_claude.py"; DestDir: "{app}\wrapper"; Components: mcpwrapper; Flags: ignoreversion

; === Documentation ===
Source: "files\README.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Tasks]
Name: "configureclaude"; Description: "Auto-configure Claude Desktop / Claude Code MCP settings"; GroupDescription: "Claude Integration:"

[Run]
; Install Python dependencies for the MCP wrapper
Filename: "python"; Parameters: "-m pip install -r ""{app}\wrapper\requirements.txt"""; StatusMsg: "Installing Python MCP dependencies..."; Flags: runhidden nowait skipifdoesntexist
; Configure Claude MCP settings
Filename: "python"; Parameters: """{app}\wrapper\setup_claude.py"" --install-dir ""{app}"""; StatusMsg: "Configuring Claude MCP settings..."; Tasks: configureclaude; Flags: runhidden nowait skipifdoesntexist

[UninstallDelete]
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitMCPBridge2026.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitMCPBridge2026.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\appsettings.json"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitMCPBridge2025.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitMCPBridge2025.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\appsettings.json"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\Newtonsoft.Json.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\Serilog.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\Serilog.Sinks.File.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitMCPBridge2024.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitMCPBridge2024.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\appsettings.json"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\Newtonsoft.Json.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\Serilog.dll"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\Serilog.Sinks.File.dll"

[Code]
// Check if Revit addins folders exist to auto-detect installed versions
function InitializeSetup(): Boolean;
var
  Revit2024Path, Revit2025Path, Revit2026Path: String;
begin
  Result := True;

  Revit2026Path := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2026');
  Revit2025Path := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2025');
  Revit2024Path := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2024');

  if not DirExists(Revit2024Path) and not DirExists(Revit2025Path) and not DirExists(Revit2026Path) then
  begin
    if MsgBox('No Revit 2024, 2025, or 2026 installation detected.' + #13#10 + #13#10 +
              'The installer could not find Revit addins folders.' + #13#10 + #13#10 +
              'Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Create addins directories if they don't exist
    if WizardIsComponentSelected('revit2026') then
      ForceDirectories(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2026'));
    if WizardIsComponentSelected('revit2025') then
      ForceDirectories(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2025'));
    if WizardIsComponentSelected('revit2024') then
      ForceDirectories(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2024'));
  end;
end;
