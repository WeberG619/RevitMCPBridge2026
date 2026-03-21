using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Serilog;

namespace RevitMCPBridge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StopGenerationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!GenerationState.IsRunning)
                {
                    TaskDialog.Show("BIM Monkey", "No generation is currently running.");
                    return Result.Succeeded;
                }

                GenerationState.ActiveProcess.Kill();
                GenerationState.ActiveProcess = null;

                Log.Information("Generation stopped via ribbon button");
                TaskDialog.Show("BIM Monkey", "Generation stopped.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to stop generation");
                TaskDialog.Show("BIM Monkey", $"Could not stop generation: {ex.Message}");
                return Result.Succeeded;
            }
        }
    }

    public class GenerationRunningAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
            => GenerationState.IsRunning;
    }
}
