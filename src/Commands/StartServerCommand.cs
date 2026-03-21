using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Serilog;

namespace RevitMCPBridge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StartServerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var server = RevitMCPBridgeApp.GetServer();
                
                if (server == null)
                {
                    server = new MCPServer();
                    RevitMCPBridgeApp.SetServer(server);
                }
                
                if (server.IsRunning)
                {
                    TaskDialog.Show("", "Server is already running.");
                    return Result.Succeeded;
                }

                server.Start();

                var dialog = new TaskDialog("");
                dialog.MainContent = "BIM Monkey server started. You can now run Claude Code.";
                dialog.MainIcon = TaskDialogIcon.TaskDialogIconInformation;
                dialog.Show();
                
                Log.Information("MCP Server started via UI command");
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start MCP Server");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
    
    public class ServerStoppedAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            var server = RevitMCPBridgeApp.GetServer();
            return server == null || !server.IsRunning;
        }
    }
}