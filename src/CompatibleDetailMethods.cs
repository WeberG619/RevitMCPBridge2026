using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Version-AWARE detail search. Revit can open older .rvt files (and upgrade them) but never
    /// newer ones — so when searching the firm's detail libraries we read each file's saved Revit
    /// version (BasicFileInfo, no open) and only surface details COMPATIBLE with the running version.
    /// </summary>
    public static class CompatibleDetailMethods
    {
        // The firm's detail libraries, in preference order (compatible ones first).
        private static readonly string[] Libraries = new[]
        {
            @"D:\BDArchitect-DetailLibrary-2025",
            @"D:\003 - RESOURCES\01 - REVIT LIBRARY\REVIT STANDARD DETAILS LIBRARY",
            @"D:\003 - RESOURCES\01 - REVIT LIBRARY\Revit Details",
            @"D:\Revit Detail Libraries\Revit Details"
        };

        /// <summary>Read a .rvt's saved Revit version without opening it. Returns the year (e.g. 2025) or 0 if unknown.</summary>
        public static int RvtVersion(string path)
        {
            try
            {
                var bfi = BasicFileInfo.Extract(path);
                string s = bfi?.Format;
                if (!string.IsNullOrEmpty(s))
                {
                    var m = Regex.Match(s, @"20\d\d");
                    if (m.Success && int.TryParse(m.Value, out int y)) return y;
                }
            }
            catch { }
            return 0;
        }

        [MCPMethod("getRvtVersion", Category = "Detail",
            Description = "Read the Revit version a .rvt file was saved in (without opening it). Params: path. Returns the year + whether it's compatible with the running Revit.")]
        public static string GetRvtVersion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string path = parameters["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    return JsonConvert.SerializeObject(new { success = false, error = "file not found: " + path });
                int ver = RvtVersion(path);
                int.TryParse(uiApp.Application.VersionNumber, out int running);
                return JsonConvert.SerializeObject(new { success = true, version = ver, running, compatible = ver == 0 || ver <= running });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("findCompatibleDetails", Category = "Detail",
            Description = "Search the firm's detail libraries for a detail and return only versions COMPATIBLE with the running Revit (saved version <= current). Params: searchTerm. Returns name, path, version, library.")]
        public static string FindCompatibleDetails(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string term = parameters["searchTerm"]?.ToString();
                if (string.IsNullOrWhiteSpace(term))
                    return JsonConvert.SerializeObject(new { success = false, error = "searchTerm is required" });
                int.TryParse(uiApp.Application.VersionNumber, out int running);

                var compatible = new List<object>();
                var incompatibleCount = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };

                foreach (var lib in Libraries)
                {
                    if (!System.IO.Directory.Exists(lib)) continue;
                    IEnumerable<string> files;
                    try { files = System.IO.Directory.EnumerateFiles(lib, "*.rvt", opts); }
                    catch { continue; }
                    foreach (var f in files)
                    {
                        if (compatible.Count >= 15) break;
                        // skip folders the user already culled / non-detail working folders
                        if (Regex.IsMatch(f, @"[\\/](to_delete|removed_\w+|needs_review|_backup|backup|archive|old|captures|previews|exports|family_exports|legend_exports|schedule_exports|templates|pipelines|project_configs|checklists)[\\/]", RegexOptions.IgnoreCase)) continue;
                        string nm = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (nm.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (Regex.IsMatch(nm, @"\.\d{4}$")) continue;                 // skip Revit backup copies (.0001 etc.)
                        string key = Regex.Replace(nm, @"(\.\d{4})+$", "").Trim();
                        if (!seen.Add(key)) continue;
                        int ver = RvtVersion(f);
                        if (ver != 0 && ver > running) { incompatibleCount++; continue; }   // too new for this Revit
                        compatible.Add(new { name = nm, path = f, version = ver == 0 ? "unknown" : ver.ToString(), library = System.IO.Path.GetFileName(lib.TrimEnd('\\')) });
                    }
                    if (compatible.Count >= 15) break;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    running,
                    compatibleCount = compatible.Count,
                    skippedNewer = incompatibleCount,
                    results = compatible,
                    note = incompatibleCount > 0 ? (incompatibleCount + " newer-version matches were skipped (can't open in Revit " + running + ").") : null
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
