using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Auto-resolving build/edit wrappers so the agent doesn't have to hunt level ids:
    /// place a room, a rectangular floor, renumber rooms, select a whole category. Read-write.
    /// </summary>
    public static class MoreBuildMethods
    {
        private static ElementId ActiveLevelId(Document doc)
        {
            var lid = (doc.ActiveView as ViewPlan)?.GenLevel?.Id;
            if (lid != null && lid != ElementId.InvalidElementId) return lid;
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            return lvl?.Id;
        }

        private static BuiltInCategory CatFromName(string name)
        {
            var n = (name ?? "").Trim().ToLowerInvariant().TrimEnd('s');
            switch (n)
            {
                case "door": return BuiltInCategory.OST_Doors;
                case "window": return BuiltInCategory.OST_Windows;
                case "room": return BuiltInCategory.OST_Rooms;
                case "wall": return BuiltInCategory.OST_Walls;
                case "floor": return BuiltInCategory.OST_Floors;
                case "ceiling": return BuiltInCategory.OST_Ceilings;
                case "furniture": return BuiltInCategory.OST_Furniture;
                case "casework": return BuiltInCategory.OST_Casework;
                case "plumbing fixture": return BuiltInCategory.OST_PlumbingFixtures;
                case "lighting fixture": return BuiltInCategory.OST_LightingFixtures;
                case "grid": return BuiltInCategory.OST_Grids;
                case "level": return BuiltInCategory.OST_Levels;
                default: return BuiltInCategory.OST_GenericModel;
            }
        }

        [MCPMethod("createRoomAuto", Category = "Room",
            Description = "Place a room at a point on the active level (auto-resolves the level). Params: x, y (feet); name (optional); number (optional). The point should be inside an enclosed area for a bounded room.")]
        public static string CreateRoomAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double x = parameters["x"]?.ToObject<double>() ?? 0;
                double y = parameters["y"]?.ToObject<double>() ?? 0;
                var levelId = ActiveLevelId(doc);
                if (levelId == null) return JsonConvert.SerializeObject(new { success = false, error = "no level found" });
                var level = doc.GetElement(levelId) as Level;
                int rid = 0; string nm = null, num = null, phName = null; double area = 0;
                using (var t = new Transaction(doc, "Create room"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }

                    // NewRoom(level, uv) drops the room in the project's LAST phase (e.g. "Project
                    // Completion"), which won't enclose against walls modeled in the working phase.
                    // So: create the room UNPLACED in the ACTIVE VIEW's phase, then place it into the
                    // plan-topology circuit that contains the point.
                    Autodesk.Revit.DB.Architecture.Room room = null;
                    Phase phase = null;
                    try
                    {
                        var phId = doc.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                        if (phId != null && phId != ElementId.InvalidElementId) phase = doc.GetElement(phId) as Phase;
                    }
                    catch { }
                    if (phase != null)
                    {
                        var unplaced = doc.Create.NewRoom(phase);
                        var probe = new XYZ(x, y, (level?.Elevation ?? 0) + 0.1);
                        bool placed = false;
                        try
                        {
                            var topo = doc.get_PlanTopology(level, phase);
                            foreach (PlanCircuit c in topo.Circuits)
                            {
                                if (c.IsRoomLocated) continue;
                                var pr = doc.Create.NewRoom(unplaced, c);
                                doc.Regenerate();
                                if (pr != null && pr.IsPointInRoom(probe)) { room = pr; placed = true; break; }
                                try { pr?.Unplace(); } catch { }
                            }
                        }
                        catch { }
                        if (!placed) { try { doc.Delete(unplaced.Id); } catch { } room = null; }
                    }
                    if (room == null) room = doc.Create.NewRoom(level, new UV(x, y));   // fallback: point placement (project's last phase)
                    if (room == null) { t.RollBack(); return JsonConvert.SerializeObject(new { success = false, error = "could not create the room (is the point inside an enclosed area?)" }); }
                    var name = parameters["name"]?.ToString();
                    var number = parameters["number"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) room.Name = name;
                    if (!string.IsNullOrWhiteSpace(number)) room.Number = number;
                    doc.Regenerate();
                    rid = (int)room.Id.Value; nm = room.Name; num = room.Number; area = room.Area;
                    try { phName = (doc.GetElement(room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase)?.Name; } catch { }
                    t.Commit();
                }
                string warn = area <= 0 ? " WARNING: the room is NOT enclosed (area 0) — the point may not be inside walls in phase '" + (phName ?? "?") + "'." : "";
                return JsonConvert.SerializeObject(new { success = true, roomId = rid, name = nm, number = num, areaSf = Math.Round(area, 2), phase = phName, message = "Placed room " + num + " '" + nm + "' on " + level?.Name + " (phase " + (phName ?? "?") + ", " + Math.Round(area) + " sf)." + warn });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("createFloorRect", Category = "Floor",
            Description = "Create a rectangular floor on the active level. Params: width, depth (feet); x, y (origin corner, default 0).")]
        public static string CreateFloorRect(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double width = parameters["width"]?.ToObject<double>() ?? 0;
                double depth = parameters["depth"]?.ToObject<double>() ?? 0;
                if (width <= 0 || depth <= 0) return JsonConvert.SerializeObject(new { success = false, error = "width and depth (feet) are required" });
                double x = parameters["x"]?.ToObject<double>() ?? 0;
                double y = parameters["y"]?.ToObject<double>() ?? 0;
                var levelId = ActiveLevelId(doc);
                if (levelId == null) return JsonConvert.SerializeObject(new { success = false, error = "no level found" });
                var ftId = doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType);
                if (ftId == null || ftId == ElementId.InvalidElementId)
                    ftId = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).FirstElementId();
                if (ftId == null || ftId == ElementId.InvalidElementId) return JsonConvert.SerializeObject(new { success = false, error = "no floor type available" });

                var pts = new[] { new XYZ(x, y, 0), new XYZ(x + width, y, 0), new XYZ(x + width, y + depth, 0), new XYZ(x, y + depth, 0) };
                var loop = new CurveLoop();
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                int fid = 0;
                using (var t = new Transaction(doc, "Create floor"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    var floor = Floor.Create(doc, new List<CurveLoop> { loop }, ftId, levelId);
                    fid = (int)floor.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, floorId = fid, message = "Created a " + width + "' x " + depth + "' floor on " + (doc.GetElement(levelId) as Level)?.Name + "." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("renumberRoomsAuto", Category = "Room",
            Description = "Renumber ALL rooms in the model sequentially. Params: prefix (optional, e.g. '1'); start (default 1).")]
        public static string RenumberRoomsAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string prefix = parameters["prefix"]?.ToString() ?? "";
                int start = parameters["start"]?.ToObject<int>() ?? 1;
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
                    .Where(r => r.Area > 0).OrderBy(r => { var l = r.Level?.Elevation ?? 0; return l; }).ToList();
                if (rooms.Count == 0) return JsonConvert.SerializeObject(new { success = false, error = "no placed rooms to renumber" });
                int n = start, done = 0; string first = null, last = null;
                using (var t = new Transaction(doc, "Renumber rooms"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    // two-pass to avoid collisions: stamp temp, then final
                    foreach (var r in rooms) { try { r.Number = "TMP_" + r.Id.Value; } catch { } }
                    foreach (var r in rooms) { string num = prefix + n.ToString(); try { r.Number = num; if (first == null) first = num; last = num; done++; } catch { } n++; }
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, count = done, range = first + " … " + last, message = "Renumbered " + done + " rooms (" + first + " … " + last + ")." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("selectByCategory", Category = "Selection",
            Description = "Select all elements of a category in the model. Params: category (Doors/Windows/Walls/Rooms/Furniture/…).")]
        public static string SelectByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                var doc = uidoc.Document;
                string category = parameters["category"]?.ToString();
                if (string.IsNullOrWhiteSpace(category)) return JsonConvert.SerializeObject(new { success = false, error = "category is required" });
                var bic = CatFromName(category);
                var ids = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElementIds().ToList();
                uidoc.Selection.SetElementIds(ids);
                try { if (ids.Count > 0) uidoc.ShowElements(ids); } catch { }
                return JsonConvert.SerializeObject(new { success = true, category, count = ids.Count, message = "Selected " + ids.Count + " " + category + "." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("createCurtainWallAuto", Category = "Wall",
            Description = "Create a curtain wall along a line on the active level. Params: x1, y1, x2, y2 (endpoints, feet); height (feet, default 10).")]
        public static string CreateCurtainWallAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double x1 = parameters["x1"]?.ToObject<double>() ?? 0, y1 = parameters["y1"]?.ToObject<double>() ?? 0;
                double x2 = parameters["x2"]?.ToObject<double>() ?? 0, y2 = parameters["y2"]?.ToObject<double>() ?? 0;
                double height = parameters["height"]?.ToObject<double>() ?? 10.0;
                if (Math.Abs(x2 - x1) < 1e-6 && Math.Abs(y2 - y1) < 1e-6) return JsonConvert.SerializeObject(new { success = false, error = "start and end points must differ" });
                var levelId = ActiveLevelId(doc);
                if (levelId == null) return JsonConvert.SerializeObject(new { success = false, error = "no level found" });
                var cwt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(t => t.Kind == WallKind.Curtain)
                    ?? new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name.IndexOf("curtain", StringComparison.OrdinalIgnoreCase) >= 0);
                if (cwt == null) return JsonConvert.SerializeObject(new { success = false, error = "no curtain wall type available in this project" });
                int wid = 0;
                using (var t = new Transaction(doc, "Create curtain wall"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    var line = Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0));
                    var wall = Wall.Create(doc, line, cwt.Id, levelId, height, 0, false, false);
                    wid = (int)wall.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, wallId = wid, message = "Created a curtain wall (" + cwt.Name + ") on " + (doc.GetElement(levelId) as Level)?.Name + "." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static List<CurveLoop> RectLoop(double x, double y, double width, double depth)
        {
            var pts = new[] { new XYZ(x, y, 0), new XYZ(x + width, y, 0), new XYZ(x + width, y + depth, 0), new XYZ(x, y + depth, 0) };
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            return new List<CurveLoop> { loop };
        }

        [MCPMethod("createCeilingRect", Category = "Ceiling",
            Description = "Create a rectangular ceiling on the active level. Params: width, depth (feet); x, y (origin, default 0); height (offset above level, feet, default 9).")]
        public static string CreateCeilingRect(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double width = parameters["width"]?.ToObject<double>() ?? 0;
                double depth = parameters["depth"]?.ToObject<double>() ?? 0;
                if (width <= 0 || depth <= 0) return JsonConvert.SerializeObject(new { success = false, error = "width and depth (feet) are required" });
                double x = parameters["x"]?.ToObject<double>() ?? 0, y = parameters["y"]?.ToObject<double>() ?? 0;
                double height = parameters["height"]?.ToObject<double>() ?? 9.0;
                var levelId = ActiveLevelId(doc);
                if (levelId == null) return JsonConvert.SerializeObject(new { success = false, error = "no level found" });
                var ctId = doc.GetDefaultElementTypeId(ElementTypeGroup.CeilingType);
                if (ctId == null || ctId == ElementId.InvalidElementId) ctId = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).FirstElementId();
                if (ctId == null || ctId == ElementId.InvalidElementId) return JsonConvert.SerializeObject(new { success = false, error = "no ceiling type available" });
                int cid = 0;
                using (var t = new Transaction(doc, "Create ceiling"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    var ceiling = Ceiling.Create(doc, RectLoop(x, y, width, depth), ctId, levelId);
                    var hp = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (hp != null && !hp.IsReadOnly) hp.Set(height);
                    cid = (int)ceiling.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, ceilingId = cid, message = "Created a " + width + "' x " + depth + "' ceiling at " + height + "' on " + (doc.GetElement(levelId) as Level)?.Name + "." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("createRoofRect", Category = "Roof",
            Description = "Create a rectangular (flat footprint) roof on the active level. Params: width, depth (feet); x, y (origin, default 0).")]
        public static string CreateRoofRect(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double width = parameters["width"]?.ToObject<double>() ?? 0;
                double depth = parameters["depth"]?.ToObject<double>() ?? 0;
                if (width <= 0 || depth <= 0) return JsonConvert.SerializeObject(new { success = false, error = "width and depth (feet) are required" });
                double x = parameters["x"]?.ToObject<double>() ?? 0, y = parameters["y"]?.ToObject<double>() ?? 0;
                var levelId = ActiveLevelId(doc);
                if (levelId == null) return JsonConvert.SerializeObject(new { success = false, error = "no level found" });
                var level = doc.GetElement(levelId) as Level;
                var rtId = doc.GetDefaultElementTypeId(ElementTypeGroup.RoofType);
                if (rtId == null || rtId == ElementId.InvalidElementId) rtId = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).FirstElementId();
                var roofType = doc.GetElement(rtId) as RoofType;
                if (roofType != null && roofType.Name.IndexOf("glazing", StringComparison.OrdinalIgnoreCase) >= 0)   // sloped glazing can't be made as a plain footprint roof
                    roofType = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault(rt => rt.Name.IndexOf("glazing", StringComparison.OrdinalIgnoreCase) < 0) ?? roofType;
                if (roofType == null) return JsonConvert.SerializeObject(new { success = false, error = "no roof type available" });
                var pts = new[] { new XYZ(x, y, 0), new XYZ(x + width, y, 0), new XYZ(x + width, y + depth, 0), new XYZ(x, y + depth, 0) };
                var fp = new CurveArray();
                for (int i = 0; i < 4; i++) fp.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                int rid = 0;
                using (var t = new Transaction(doc, "Create roof"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    var mca = new ModelCurveArray();   // MUST be pre-initialized — Revit null-checks the out array (ArgumentNullException otherwise)
                    var roof = doc.Create.NewFootPrintRoof(fp, level, roofType, out mca);
                    // sit the roof on TOP of the walls, not slicing through them at floor level
                    double offset = parameters["offset"]?.ToObject<double>() ?? 10.0;
                    try { roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(offset); } catch { }
                    rid = (int)roof.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, roofId = rid, message = "Created a " + width + "' x " + depth + "' roof on " + level?.Name + "." });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.GetType().Name + ": " + ex.Message.Replace("\r", " ").Replace("\n", " ") }); }
        }
    }
}
