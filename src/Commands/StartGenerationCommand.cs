using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Serilog;

namespace RevitMCPBridge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StartGenerationCommand : IExternalCommand
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const string GenerationPrompt =
            "Generate a full Construction Document set for this model.";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (GenerationState.IsRunning)
                {
                    TaskDialog.Show("BIM Monkey", "Generation is already running.");
                    return Result.Succeeded;
                }

                var workingDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "BIM Monkey");

                if (!Directory.Exists(workingDir))
                    Directory.CreateDirectory(workingDir);

                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/K cd /D \"{workingDir}\" && claude \"{GenerationPrompt}\"",
                    UseShellExecute = true,
                    WorkingDirectory = workingDir,
                    WindowStyle = ProcessWindowStyle.Normal,
                });

                GenerationState.ActiveProcess = process;

                // Keep Revit in front
                var revitHandle = commandData.Application.MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                    SetForegroundWindow(revitHandle);

                Log.Information("Generation started via ribbon button");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start generation");
                TaskDialog.Show("BIM Monkey", $"Could not start generation: {ex.Message}\n\nMake sure Claude Code is installed.");
                return Result.Succeeded;
            }
        }
    }

    public class GenerationNotRunningAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
            => !GenerationState.IsRunning;
    }
}
