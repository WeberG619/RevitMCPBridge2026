using System;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Serilog;

namespace RevitMCPBridge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ServerStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var server = RevitMCPBridgeApp.GetServer();
                var status = new StringBuilder();
                
                status.AppendLine("BIM Monkey Server Status");
                status.AppendLine("========================");
                status.AppendLine();

                if (server == null)
                {
                    status.AppendLine("Status: Not Initialized");
                    status.AppendLine("The BIM Monkey server has not been created yet.");
                }
                else
                {
                    status.AppendLine($"Status: {(server.IsRunning ? "Running" : "Stopped")}");

                    if (server.IsRunning)
                    {
                        status.AppendLine();
                        status.AppendLine("The BIM Monkey server is running and ready.");
                    }
                    else
                    {
                        status.AppendLine();
                        status.AppendLine("Click 'Start Server' to start the BIM Monkey server.");
                    }
                }

                status.AppendLine();
                status.AppendLine("Log Location:");
                var logPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins", "2026", "Logs");
                status.AppendLine(logPath);

                var dialog = new TaskDialog("BIM Monkey");
                dialog.MainContent = status.ToString();
                dialog.MainIcon = TaskDialogIcon.TaskDialogIconInformation;
                dialog.Show();
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to get server status");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}