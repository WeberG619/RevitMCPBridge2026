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
    /// Place a door on a wall — and AUTO-LOAD a default door family if none is loaded (the gap that
    /// made "add a door" flail in a blank template). Read-write.
    /// </summary>
    public static class AddDoorMethods
    {
        [MCPMethod("addDoor", Category = "Door",
            Description = "Place a door on a wall. Params: wallId; position (0-1 along the wall, default 0.5 = middle); doorTypeName (optional). Auto-loads a default door family if none is loaded. Returns the door id.")]
        public static string AddDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["wallId"] == null) return JsonConvert.SerializeObject(new { success = false, error = "wallId is required" });
                var wall = doc.GetElement(new ElementId(int.Parse(parameters["wallId"].ToString()))) as Wall;
                if (wall == null) return JsonConvert.SerializeObject(new { success = false, error = "wall not found for that wallId" });
                double position = parameters["position"]?.ToObject<double>() ?? 0.5;
                if (position < 0 || position > 1) position = 0.5;
                string typeName = parameters["doorTypeName"]?.ToString();

                var lc = wall.Location as LocationCurve;
                if (lc == null) return JsonConvert.SerializeObject(new { success = false, error = "wall has no location curve" });

                string usedType = null; int doorId = 0;
                using (var t = new Transaction(doc, "Add door"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }

                    FamilySymbol sym = FindDoorSymbol(doc, typeName);
                    if (sym == null) sym = LoadDefaultDoor(doc);   // auto-load when nothing is in the project
                    if (sym == null) { t.RollBack(); return JsonConvert.SerializeObject(new { success = false, error = "no door family is loaded and a default could not be found — load a door family first (load_family 'door')" }); }
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                    var pt = lc.Curve.Evaluate(position, true);
                    var level = doc.GetElement(wall.LevelId) as Level;
                    var inst = doc.Create.NewFamilyInstance(pt, sym, wall, level, StructuralType.NonStructural);
                    usedType = sym.Family?.Name + " : " + sym.Name;
                    doorId = (int)inst.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, doorId, type = usedType, position, message = "Placed a door (" + usedType + ") on wall " + wall.Id.Value + " at " + (int)(position * 100) + "% along it." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static bool IsCurtainDoor(FamilySymbol s)
        {
            var fn = ((s.Family?.Name ?? "") + " " + s.Name).ToLowerInvariant();
            return fn.Contains("curtain") || fn.Contains("store front") || fn.Contains("storefront");
        }

        private static FamilySymbol FindDoorSymbol(Document doc, string name)
        {
            var syms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            // curtain-wall / storefront doors won't host on a basic wall — exclude them so the door lands IN the wall
            var normal = syms.Where(s => !IsCurtainDoor(s)).ToList();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var hit = normal.FirstOrDefault(s => ((s.Family?.Name ?? "") + " " + s.Name).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }
            // prefer a plain single door, then any non-curtain door; return null if only curtain doors exist (caller loads a real one)
            return normal.FirstOrDefault(s => ((s.Family?.Name ?? "") + " " + s.Name).IndexOf("single", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? normal.FirstOrDefault();
        }

        private static FamilySymbol LoadDefaultDoor(Document doc)
        {
            var candidates = new List<string>
            {
                "D:\\003 - RESOURCES\\_FAMILY_CLOUD\\doors\\DR-SINGLE.rfa",
                "D:\\003 - RESOURCES\\_FAMILY_CLOUD\\doors\\DR-DOUBLE.rfa"
            };
            try
            {
                string lib = "C:\\ProgramData\\Autodesk\\RVT 2025\\Libraries";
                if (System.IO.Directory.Exists(lib))
                {
                    var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
                    var libDoor = System.IO.Directory.EnumerateFiles(lib, "*.rfa", opts).FirstOrDefault(f => System.IO.Path.GetFileName(f).IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (libDoor != null) candidates.Add(libDoor);
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
