using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Structural (columns, beams, stairs) and MEP (ducts, pipes, fixtures) build wrappers.
    /// Each auto-resolves the active level + a default type/system, and auto-loads a family from the
    /// Revit library if the category has none loaded. NEEDS LIVE VERIFICATION on deploy (MEP needs an
    /// MEP-enabled project; structural needs structural families available).
    /// </summary>
    public static class StructuralMepMethods
    {
        private static ElementId ActiveLevelId(Document doc)
        {
            var lid = (doc.ActiveView as ViewPlan)?.GenLevel?.Id;
            if (lid != null && lid != ElementId.InvalidElementId) return lid;
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            return lvl?.Id;
        }

        private static Level NextLevelAbove(Document doc, Level baseLevel)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.Elevation > baseLevel.Elevation + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault();
        }

        /// <summary>Find a loaded FamilySymbol of a category, else load one from the Revit library by keyword.</summary>
        private static FamilySymbol FindOrLoadSymbol(Document doc, BuiltInCategory bic, string libKeyword)
        {
            var sym = new FilteredElementCollector(doc).OfCategory(bic).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault();
            if (sym != null) return sym;
            try
            {
                foreach (var lib in new[] { "C:\\ProgramData\\Autodesk\\RVT 2025\\Libraries", "D:\\003 - RESOURCES\\_FAMILY_CLOUD" })
                {
                    if (!System.IO.Directory.Exists(lib)) continue;
                    var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
                    var hit = System.IO.Directory.EnumerateFiles(lib, "*.rfa", opts).FirstOrDefault(f => System.IO.Path.GetFileName(f).IndexOf(libKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit != null && doc.LoadFamily(hit, out Family fam) && fam != null)
                    {
                        var sid = fam.GetFamilySymbolIds().FirstOrDefault();
                        if (sid != null && sid != ElementId.InvalidElementId) return doc.GetElement(sid) as FamilySymbol;
                    }
                }
            }
            catch { }
            return null;
        }

        [MCPMethod("placeColumnAuto", Category = "Structural", Description = "Place a structural column at a point on the active level. Params: x, y (feet). Auto-loads a column family if none is loaded.")]
        public static string PlaceColumnAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double x = parameters["x"]?.ToObject<double>() ?? 0, y = parameters["y"]?.ToObject<double>() ?? 0;
                var levelId = ActiveLevelId(doc); if (levelId == null) return Err("no level found");
                var level = doc.GetElement(levelId) as Level;
                int cid = 0; string used = null;
                using (var t = new Transaction(doc, "Place column"))
                {
                    t.Start(); Swallow(t);
                    var sym = FindOrLoadSymbol(doc, BuiltInCategory.OST_StructuralColumns, "column");
                    if (sym == null) { t.RollBack(); return Err("no structural column family available — load one first (load_family 'column')"); }
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                    var inst = doc.Create.NewFamilyInstance(new XYZ(x, y, level.Elevation), sym, level, StructuralType.Column);
                    used = sym.Family?.Name + " : " + sym.Name; cid = (int)inst.Id.Value;
                    t.Commit();
                }
                return Ok(new { columnId = cid, type = used, message = "Placed column (" + used + ") on " + level?.Name + "." });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        [MCPMethod("createBeamAuto", Category = "Structural", Description = "Create a structural beam along a line on the active level. Params: x1, y1, x2, y2 (feet). Auto-loads a framing family if none is loaded.")]
        public static string CreateBeamAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double x1 = parameters["x1"]?.ToObject<double>() ?? 0, y1 = parameters["y1"]?.ToObject<double>() ?? 0;
                double x2 = parameters["x2"]?.ToObject<double>() ?? 0, y2 = parameters["y2"]?.ToObject<double>() ?? 0;
                var levelId = ActiveLevelId(doc); if (levelId == null) return Err("no level found");
                var level = doc.GetElement(levelId) as Level;
                int bid = 0; string used = null;
                using (var t = new Transaction(doc, "Create beam"))
                {
                    t.Start(); Swallow(t);
                    var sym = FindOrLoadSymbol(doc, BuiltInCategory.OST_StructuralFraming, "beam") ?? FindOrLoadSymbol(doc, BuiltInCategory.OST_StructuralFraming, "framing");
                    if (sym == null) { t.RollBack(); return Err("no structural framing family available — load one first (load_family 'beam')"); }
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                    var line = Line.CreateBound(new XYZ(x1, y1, level.Elevation), new XYZ(x2, y2, level.Elevation));
                    var inst = doc.Create.NewFamilyInstance(line, sym, level, StructuralType.Beam);
                    used = sym.Family?.Name + " : " + sym.Name; bid = (int)inst.Id.Value;
                    t.Commit();
                }
                return Ok(new { beamId = bid, type = used, message = "Created beam (" + used + ") on " + level?.Name + "." });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        [MCPMethod("createStairAuto", Category = "Structural", Description = "Create a straight-run stair from the active level up to the next level. Params: x, y (run start, feet); direction (north/south/east/west, default north); width (feet, default 3.5); length (run length, feet, default = level-to-level dist).")]
        public static string CreateStairAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double x = parameters["x"]?.ToObject<double>() ?? 0, y = parameters["y"]?.ToObject<double>() ?? 0;
                double width = parameters["width"]?.ToObject<double>() ?? 3.5;
                string dir = (parameters["direction"]?.ToString() ?? "north").ToLowerInvariant();
                double dx = 0, dy = 1; if (dir.StartsWith("s")) { dy = -1; } else if (dir.StartsWith("e")) { dx = 1; dy = 0; } else if (dir.StartsWith("w")) { dx = -1; dy = 0; }
                var baseId = ActiveLevelId(doc); if (baseId == null) return Err("no level found");
                var baseLevel = doc.GetElement(baseId) as Level;
                var topLevel = NextLevelAbove(doc, baseLevel);
                if (topLevel == null) return Err("no level above the active one — a stair needs a level to reach (create_level first)");
                double rise = topLevel.Elevation - baseLevel.Elevation;
                double length = parameters["length"]?.ToObject<double>() ?? Math.Max(rise * 1.5, 8.0);
                var stairTypeId = new FilteredElementCollector(doc).OfClass(typeof(StairsType)).FirstElementId();
                if (stairTypeId == null || stairTypeId == ElementId.InvalidElementId) return Err("no stair type available");

                ElementId stairId;
                using (var scope = new StairsEditScope(doc, "Create stair"))
                {
                    stairId = scope.Start(baseId, topLevel.Id);   // StairsEditScope.Start takes only bottom/top level ids
                    using (var st = new Transaction(doc, "Add stair run"))     // SDK pattern: a REGULAR Transaction inside the edit scope (SubTransaction needs an open Transaction and fails here)
                    {
                        st.Start();
                        var p1 = new XYZ(x, y, baseLevel.Elevation);
                        var p2 = new XYZ(x + dx * length, y + dy * length, baseLevel.Elevation);
                        var runLine = Line.CreateBound(p1, p2);
                        StairsRun.CreateStraightRun(doc, stairId, runLine, StairsRunJustification.Center);
                        st.Commit();
                    }
                    scope.Commit(new WarningSwallower());
                }
                return Ok(new { stairId = (int)stairId.Value, message = "Created a stair from " + baseLevel?.Name + " to " + topLevel?.Name + "." });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        [MCPMethod("createDuctAuto", Category = "MEP", Description = "Create a duct between two 3D points (default mechanical system + duct type). Params: x1,y1,z1,x2,y2,z2 (feet). Needs an MEP-enabled project.")]
        public static string CreateDuctAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var p1 = Pt(parameters, "x1", "y1", "z1"); var p2 = Pt(parameters, "x2", "y2", "z2");
                var levelId = ActiveLevelId(doc); if (levelId == null) return Err("no level found");
                var systemType = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
                var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).FirstElementId();
                if (systemType == ElementId.InvalidElementId || ductType == ElementId.InvalidElementId || systemType == null || ductType == null)
                    return Err("this project has no duct/mechanical-system types — open an MEP/mechanical project");
                int did = 0;
                using (var t = new Transaction(doc, "Create duct"))
                {
                    t.Start(); Swallow(t);
                    var duct = Duct.Create(doc, systemType, ductType, levelId, p1, p2);
                    did = (int)duct.Id.Value;
                    t.Commit();
                }
                return Ok(new { ductId = did, message = "Created a duct." });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        [MCPMethod("createPipeAuto", Category = "MEP", Description = "Create a pipe between two 3D points (default piping system + pipe type). Params: x1,y1,z1,x2,y2,z2 (feet). Needs an MEP-enabled project.")]
        public static string CreatePipeAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var p1 = Pt(parameters, "x1", "y1", "z1"); var p2 = Pt(parameters, "x2", "y2", "z2");
                var levelId = ActiveLevelId(doc); if (levelId == null) return Err("no level found");
                var systemType = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
                var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                if (systemType == ElementId.InvalidElementId || pipeType == ElementId.InvalidElementId || systemType == null || pipeType == null)
                    return Err("this project has no pipe/piping-system types — open an MEP/plumbing project");
                int pid = 0;
                using (var t = new Transaction(doc, "Create pipe"))
                {
                    t.Start(); Swallow(t);
                    var pipe = Pipe.Create(doc, systemType, pipeType, levelId, p1, p2);
                    pid = (int)pipe.Id.Value;
                    t.Commit();
                }
                return Ok(new { pipeId = pid, message = "Created a pipe." });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        [MCPMethod("placeFixtureAuto", Category = "MEP", Description = "Place a plumbing or lighting fixture at a point on the active level. Params: x, y (feet); kind ('plumbing' or 'lighting', default plumbing). Auto-loads a fixture family if none is loaded.")]
        public static string PlaceFixtureAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double x = parameters["x"]?.ToObject<double>() ?? 0, y = parameters["y"]?.ToObject<double>() ?? 0;
                string kind = (parameters["kind"]?.ToString() ?? "plumbing").ToLowerInvariant();
                bool light = kind.StartsWith("light");
                var bic = light ? BuiltInCategory.OST_LightingFixtures : BuiltInCategory.OST_PlumbingFixtures;
                var levelId = ActiveLevelId(doc); if (levelId == null) return Err("no level found");
                var level = doc.GetElement(levelId) as Level;
                int fid = 0; string used = null;
                using (var t = new Transaction(doc, "Place fixture"))
                {
                    t.Start(); Swallow(t);
                    var sym = FindOrLoadSymbol(doc, bic, light ? "light" : "toilet") ?? FindOrLoadSymbol(doc, bic, light ? "ceiling light" : "sink");
                    if (sym == null) { t.RollBack(); return Err("no " + (light ? "lighting" : "plumbing") + " fixture family available — load one first (load_family)"); }
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                    var inst = doc.Create.NewFamilyInstance(new XYZ(x, y, level.Elevation), sym, level, StructuralType.NonStructural);
                    used = sym.Family?.Name + " : " + sym.Name; fid = (int)inst.Id.Value;
                    t.Commit();
                }
                return Ok(new { fixtureId = fid, type = used, message = "Placed " + (light ? "lighting" : "plumbing") + " fixture (" + used + ")." });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        // helpers
        private static XYZ Pt(JObject p, string kx, string ky, string kz) => new XYZ(p[kx]?.ToObject<double>() ?? 0, p[ky]?.ToObject<double>() ?? 0, p[kz]?.ToObject<double>() ?? 0);
        private static void Swallow(Transaction t) { try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { } }
        private static string Ok(object o) { var jo = JObject.FromObject(o); jo["success"] = true; return jo.ToString(Formatting.None); }
        private static string Err(string m) => JsonConvert.SerializeObject(new { success = false, error = m });
    }
}
