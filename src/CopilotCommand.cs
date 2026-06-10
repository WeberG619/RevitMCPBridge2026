using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPBridge.Commands
{
    /// <summary>Ribbon command: open the Model Copilot panel anytime (no selection needed).</summary>
    [Transaction(TransactionMode.Manual)]
    public class OpenCopilotCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { ElementInfoPanel.OpenStandalone(commandData.Application); return Result.Succeeded; }
            catch (System.Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }
}
