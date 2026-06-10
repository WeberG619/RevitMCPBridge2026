using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Export sheets to PDF on the Desktop using Revit's native PDF export (no printer needed).
    /// "export the cover sheet to PDF", "export all the sheets". One combined PDF, auto-opened.
    /// </summary>
    public static class ExportSheetsPdfMethods
    {
        [MCPMethod("exportSheetsPdf", Category = "Document",
            Description = "Export sheets to a single PDF on the Desktop. Params: sheets ('all', or a sheet number/name keyword to match, e.g. 'Cover','A-101'). Returns the PDF path; if no match, lists available sheets.")]
        public static string ExportSheetsPdf(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string which = parameters["sheets"]?.ToString() ?? "all";

                var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();

                if (!which.Equals("all", StringComparison.OrdinalIgnoreCase))
                    sheets = sheets.Where(s => (s.SheetNumber + " " + s.Name).IndexOf(which, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (sheets.Count == 0)
                {
                    var avail = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder).Select(s => s.SheetNumber + " - " + s.Name).OrderBy(x => x).ToList();
                    return JsonConvert.SerializeObject(new { success = false, error = "No sheets matching '" + which + "'.", available = avail });
                }

                string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string baseName = (sheets.Count == 1 ? sheets[0].SheetNumber + " " + sheets[0].Name : "Sheets") + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                foreach (var ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');

                var opts = new PDFExportOptions { Combine = true, FileName = baseName };
                var ids = sheets.OrderBy(s => s.SheetNumber).Select(s => s.Id).ToList();
                bool ok = doc.Export(folder, ids, opts);

                string path = Path.Combine(folder, baseName + ".pdf");
                if (ok && File.Exists(path))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
                    return JsonConvert.SerializeObject(new { success = true, sheetCount = sheets.Count, path, message = "Exported " + sheets.Count + " sheet(s) to " + path });
                }
                return JsonConvert.SerializeObject(new { success = false, error = "PDF export did not produce a file", sheetCount = sheets.Count, expected = path });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
