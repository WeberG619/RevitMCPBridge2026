using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// One-shot "build a rectangular room of walls" — resolves the active level + default wall type
    /// itself, so the agent only needs width/depth (no level-id hunting). Read-write.
    /// </summary>
    public static class CreateRoomWallsMethods
    {
        [MCPMethod("createRoomWalls", Category = "Wall",
            Description = "Create 4 walls forming a rectangular room on the active level. Params: width, depth (feet); x, y (origin corner, default 0,0); height (feet, optional); wallTypeName (optional). Returns the new wall ids.")]
        public static string CreateRoomWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                double width = parameters["width"]?.ToObject<double>() ?? 0;
                double depth = parameters["depth"]?.ToObject<double>() ?? 0;
                if (width <= 0 || depth <= 0) return JsonConvert.SerializeObject(new { success = false, error = "width and depth (feet) are required and must be > 0" });
                double x = parameters["x"]?.ToObject<double>() ?? 0;
                double y = parameters["y"]?.ToObject<double>() ?? 0;
                double? height = parameters["height"]?.ToObject<double>();

                // active level (from the plan view) or the lowest level
                ElementId levelId = (doc.ActiveView as ViewPlan)?.GenLevel?.Id;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                {
                    var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
                    if (lvl == null) return JsonConvert.SerializeObject(new { success = false, error = "no level found in the document" });
                    levelId = lvl.Id;
                }

                // wall type: by name if given, else the document default
                WallType wt = null;
                string wtName = parameters["wallTypeName"]?.ToString();
                var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
                if (!string.IsNullOrWhiteSpace(wtName))
                    wt = wallTypes.FirstOrDefault(t => t.Name.IndexOf(wtName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (wt == null) { var def = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType); wt = doc.GetElement(def) as WallType; }
                if (wt == null) wt = wallTypes.FirstOrDefault();
                if (wt == null) return JsonConvert.SerializeObject(new { success = false, error = "no wall type available" });

                var pts = new[]
                {
                    new XYZ(x, y, 0), new XYZ(x + width, y, 0),
                    new XYZ(x + width, y + depth, 0), new XYZ(x, y + depth, 0)
                };

                var ids = new List<int>();
                var skippedExisting = new List<int>();
                using (var t = new Transaction(doc, "Create room walls"))
                {
                    t.Start();
                    var fo = t.GetFailureHandlingOptions();
                    try { fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    var newWalls = new List<Wall>();
                    double lvlElev = (doc.GetElement(levelId) as Level)?.Elevation ?? 0;
                    bool allowDuplicate = parameters["allowDuplicate"]?.ToObject<bool>() ?? false;
                    for (int i = 0; i < 4; i++)
                    {
                        // DUPLICATE GUARD: don't re-draw a side that already has a wall on it —
                        // reuse the earlier work instead of stacking a new wall on top.
                        if (!allowDuplicate)
                        {
                            var dup = WallMethods.FindWallOnSegment(doc, pts[i], pts[(i + 1) % 4], lvlElev);
                            if (dup != null) { skippedExisting.Add((int)dup.Id.Value); continue; }
                        }
                        var line = Line.CreateBound(pts[i], pts[(i + 1) % 4]);
                        var wall = Wall.Create(doc, line, wt.Id, levelId, height ?? 10.0, 0, false, false);
                        if (height.HasValue) { var hp = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM); if (hp != null && !hp.IsReadOnly) hp.Set(height.Value); }
                        ids.Add((int)wall.Id.Value);
                        newWalls.Add(wall);
                    }
                    // HARD RULE: the exterior face of every enclosure wall faces OUT of the room,
                    // never into it — enforced in code, independent of draw direction.
                    doc.Regenerate();
                    var centroid = new XYZ(x + width / 2.0, y + depth / 2.0, 0);
                    foreach (var w in newWalls) FlipIfFacingCentroid(w, centroid);
                    t.Commit();
                }
                string skippedNote = skippedExisting.Count == 0 ? "" :
                    " " + skippedExisting.Count + " side(s) already had a wall there (ids " + string.Join(", ", skippedExisting) + ") — reused, not drawn on top.";
                return JsonConvert.SerializeObject(new { success = true, wallIds = ids, reusedWallIds = skippedExisting, message = "Created a " + width + "' x " + depth + "' room (" + ids.Count + " walls) on " + (doc.GetElement(levelId) as Level)?.Name + " with type '" + wt.Name + "'." + skippedNote });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        /// <summary>Flip a wall when its exterior-face normal (Wall.Orientation) points toward
        /// the enclosure centroid — i.e. the exterior finish is facing INTO the room.</summary>
        internal static bool FlipIfFacingCentroid(Wall wall, XYZ centroid)
        {
            try
            {
                var curve = (wall.Location as LocationCurve)?.Curve;
                if (curve == null) return false;
                var mid = curve.Evaluate(0.5, true);
                var toCentroid = new XYZ(centroid.X - mid.X, centroid.Y - mid.Y, 0);
                var n = wall.Orientation;
                if (n.X * toCentroid.X + n.Y * toCentroid.Y > 1e-9) { wall.Flip(); return true; }
            }
            catch { }
            return false;
        }

        [MCPMethod("orientWallsExteriorOut", Category = "Wall",
            Description = "Enforce exterior-face-out on a set of walls forming an enclosure: any wall whose exterior face points toward the enclosure centroid is flipped. Params: wallIds (array, optional — default ALL walls in the model); centroidX, centroidY (feet, optional — default mean of the walls' midpoints). Returns flipped ids.")]
        public static string OrientWallsExteriorOut(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null) return JsonConvert.SerializeObject(new { success = false, error = "No active document" });

                var walls = new List<Wall>();
                if (parameters?["wallIds"] is JArray idsArr && idsArr.Count > 0)
                {
                    foreach (var idTok in idsArr)
                        if (doc.GetElement(new ElementId(idTok.ToObject<int>())) is Wall w) walls.Add(w);
                }
                else
                {
                    walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
                }
                if (walls.Count == 0) return JsonConvert.SerializeObject(new { success = false, error = "no walls found" });

                XYZ centroid;
                if (parameters?["centroidX"] != null && parameters?["centroidY"] != null)
                {
                    centroid = new XYZ(parameters["centroidX"].ToObject<double>(), parameters["centroidY"].ToObject<double>(), 0);
                }
                else
                {
                    double sx = 0, sy = 0; int nMid = 0;
                    foreach (var w in walls)
                    {
                        var c = (w.Location as LocationCurve)?.Curve;
                        if (c == null) continue;
                        var m = c.Evaluate(0.5, true); sx += m.X; sy += m.Y; nMid++;
                    }
                    if (nMid == 0) return JsonConvert.SerializeObject(new { success = false, error = "walls have no location curves" });
                    centroid = new XYZ(sx / nMid, sy / nMid, 0);
                }

                var flipped = new List<int>();
                using (var t = new Transaction(doc, "Orient walls exterior-out"))
                {
                    t.Start();
                    foreach (var w in walls)
                        if (FlipIfFacingCentroid(w, centroid)) flipped.Add((int)w.Id.Value);
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new
                {
                    success = true, checkedCount = walls.Count, flippedCount = flipped.Count, flippedIds = flipped,
                    centroid = new { x = centroid.X, y = centroid.Y },
                    message = flipped.Count + " of " + walls.Count + " walls flipped so the exterior face points out of the enclosure."
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
