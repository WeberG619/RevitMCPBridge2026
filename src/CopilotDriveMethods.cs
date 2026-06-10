using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Scripted access to the Model Copilot panel over the pipe — deterministic driving for
    /// tests/automation (replaces brittle UI automation of the chat box).
    /// </summary>
    public static class CopilotDriveMethods
    {
        [MCPMethod("copilotAsk", Category = "Copilot",
            Description = "Submit a question/command to the Model Copilot panel chat, exactly as if typed. Params: question. Returns immediately — poll copilotStatus for the reply. The panel must be open (MCP Bridge ribbon).")]
        public static string CopilotAsk(UIApplication uiApp, JObject parameters)
        {
            string q = parameters?["question"]?.ToString();
            if (string.IsNullOrWhiteSpace(q)) return "{\"success\":false,\"error\":\"question is required\"}";
            return ElementInfoPanel.AskFromBridge(q);
        }

        [MCPMethod("copilotOpen", Category = "Copilot",
            Description = "Open the Model Copilot panel (same as the ribbon button) — whole-model chat when nothing is selected. Requires an active document. No params. Returns copilotStatus.")]
        public static string CopilotOpen(UIApplication uiApp, JObject parameters)
        {
            if (uiApp?.ActiveUIDocument?.Document == null)
                return "{\"success\":false,\"error\":\"no active document - open a project first\"}";
            ElementInfoPanel.OpenStandalone(uiApp);
            return ElementInfoPanel.StatusFromBridge();
        }

        [MCPMethod("copilotStatus", Category = "Copilot",
            Description = "Model Copilot panel state: panelOpen, busy, lastQuestion, lastAnswer (null while busy). No params.")]
        public static string CopilotStatus(UIApplication uiApp, JObject parameters)
        {
            return ElementInfoPanel.StatusFromBridge();
        }
    }
}
