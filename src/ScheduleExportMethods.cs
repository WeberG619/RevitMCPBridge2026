using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// One-shot "export the door schedule to Excel" — finds a schedule by name, exports it to
    /// a CSV on the Desktop (opens in Excel), and opens it. Wraps the proven ExportScheduleToCSV.
    /// </summary>
    public static class ScheduleExportMethods
    {
        [MCPMethod("exportSchedule", Category = "Schedule",
            Description = "Export a schedule to a CSV (opens in Excel) on the Desktop. Params: scheduleName (matched loosely, e.g. 'Door','Room Finish','Window'). Returns the file path; if no match, lists the available schedule names.")]
        public static string ExportSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string name = parameters["scheduleName"]?.ToString() ?? "";

                var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>().Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule).ToList();

                var match = schedules.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                         ?? schedules.FirstOrDefault(s => s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "No schedule matching '" + name + "'.", available = schedules.Select(s => s.Name).OrderBy(x => x).ToList() });

                string safe = match.Name;
                foreach (var ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    safe + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");

                var p = new JObject { ["scheduleId"] = (int)match.Id.Value, ["filePath"] = filePath };
                var res = RevitMCPBridge2026.ScheduleMethods.ExportScheduleToCSV(uiApp, p);

                if (!File.Exists(filePath))
                    return JsonConvert.SerializeObject(new { success = false, error = "export failed", schedule = match.Name, detail = res });

                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = filePath, UseShellExecute = true }); } catch { }
                return JsonConvert.SerializeObject(new { success = true, schedule = match.Name, path = filePath, message = "Exported '" + match.Name + "' to " + filePath });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
