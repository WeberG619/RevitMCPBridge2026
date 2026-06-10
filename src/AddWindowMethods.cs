using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Place a window on a wall — and AUTO-LOAD a default window family if none is loaded (parallels addDoor).
    /// </summary>
    public static class AddWindowMethods
    {
        [MCPMethod("addWindow", Category = "Window",
            Description = "Place a window on a wall. Params: wallId; position (0-1 along the wall, default 0.5); sillHeight (feet, optional); windowTypeName (optional). Auto-loads a default window family if none is loaded. Returns the window id.")]
        public static string AddWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["wallId"] == null) return JsonConvert.SerializeObject(new { success = false, error = "wallId is required" });
                var wall = doc.GetElement(new ElementId(int.Parse(parameters["wallId"].ToString()))) as Wall;
                if (wall == null) return JsonConvert.SerializeObject(new { success = false, error = "wall not found for that wallId" });
                double position = parameters["position"]?.ToObject<double>() ?? 0.5;
                if (position < 0 || position > 1) position = 0.5;
                double? sill = parameters["sillHeight"]?.ToObject<double>();
                string typeName = parameters["windowTypeName"]?.ToString();

                var lc = wall.Location as LocationCurve;
                if (lc == null) return JsonConvert.SerializeObject(new { success = false, error = "wall has no location curve" });

                string usedType = null; int winId = 0;
                using (var t = new Transaction(doc, "Add window"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }

                    FamilySymbol sym = FindWindowSymbol(doc, typeName) ?? LoadDefaultWindow(doc);
                    if (sym == null) { t.RollBack(); return JsonConvert.SerializeObject(new { success = false, error = "no window family is loaded and a default could not be found — load a window family first (load_family 'window')" }); }
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                    var pt = lc.Curve.Evaluate(position, true);
                    var level = doc.GetElement(wall.LevelId) as Level;
                    var inst = doc.Create.NewFamilyInstance(pt, sym, wall, level, StructuralType.NonStructural);
                    if (sill.HasValue) { var sp = inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM); if (sp != null && !sp.IsReadOnly) sp.Set(sill.Value); }
                    usedType = sym.Family?.Name + " : " + sym.Name;
                    winId = (int)inst.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, windowId = winId, type = usedType, position, message = "Placed a window (" + usedType + ") on wall " + wall.Id.Value + " at " + (int)(position * 100) + "% along it." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static FamilySymbol FindWindowSymbol(Document doc, string name)
        {
            var syms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var hit = syms.FirstOrDefault(s => ((s.Family?.Name ?? "") + " " + s.Name).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }
            return syms.FirstOrDefault(s => ((s.Family?.Name ?? "") + " " + s.Name).IndexOf("fixed", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? syms.FirstOrDefault();
        }

        private static FamilySymbol LoadDefaultWindow(Document doc)
        {
            var candidates = new List<string>
            {
                "D:\\003 - RESOURCES\\_FAMILY_CLOUD\\windows\\Fixed-window.rfa",
                "D:\\003 - RESOURCES\\_FAMILY_CLOUD\\windows\\Fixed.rfa"
            };
            try
            {
                string lib = "C:\\ProgramData\\Autodesk\\RVT 2025\\Libraries";
                if (System.IO.Directory.Exists(lib))
                {
                    var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
                    var libWin = System.IO.Directory.EnumerateFiles(lib, "*.rfa", opts).FirstOrDefault(f => System.IO.Path.GetFileName(f).IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0 || System.IO.Path.GetFileName(f).IndexOf("fixed", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (libWin != null) candidates.Add(libWin);
                }
            }
            catch { }
            foreach (var path in candidates)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) continue;
                    if (doc.LoadFamily(path, out Family fam) && fam != null)
                    {
                        var sid = fam.GetFamilySymbolIds().FirstOrDefault();
                        if (sid != null && sid != ElementId.InvalidElementId) return doc.GetElement(sid) as FamilySymbol;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
