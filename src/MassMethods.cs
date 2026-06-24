using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Conceptual-massing methods: build loose volumes (SketchUp-style) and convert
    /// their faces into native Revit elements. This is the "loose model -> smart element"
    /// bridge: createMassBox drops a solid, createFaceWall turns its vertical faces into walls.
    /// </summary>
    public static class MassMethods
    {
        /// <summary>
        /// Drop a solid box "mass" (DirectShape, category Mass) into the active project.
        /// This is the loose, SketchUp-style volume that createFaceWall later converts.
        /// </summary>
        [MCPMethod("createMassBox", Category = "Mass", Description = "Create a solid box mass (DirectShape) from an origin + width/depth/height — the loose volume for Wall-by-Face")]
        public static string CreateMassBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // origin defaults to project origin; dims default to a 20x30x10 box
                var origin = parameters["origin"] != null
                    ? parameters["origin"].ToObject<double[]>()
                    : new double[] { 0, 0, 0 };
                double width  = parameters["width"]?.ToObject<double>()  ?? 20.0;  // X
                double depth  = parameters["depth"]?.ToObject<double>()  ?? 30.0;  // Y
                double height = parameters["height"]?.ToObject<double>() ?? 10.0;  // Z
                string name   = parameters["name"]?.ToString() ?? "MassBox";

                if (width <= 0 || depth <= 0 || height <= 0)
                    return ResponseBuilder.Error("width, depth and height must be positive", "VALIDATION_ERROR").Build();

                double ox = origin[0];
                double oy = origin[1];
                double oz = origin.Length > 2 ? origin[2] : 0.0;

                // Footprint rectangle, CCW, then extrude up Z
                var p0 = new XYZ(ox,         oy,         oz);
                var p1 = new XYZ(ox + width, oy,         oz);
                var p2 = new XYZ(ox + width, oy + depth, oz);
                var p3 = new XYZ(ox,         oy + depth, oz);

                var curves = new List<Curve>
                {
                    Line.CreateBound(p0, p1),
                    Line.CreateBound(p1, p2),
                    Line.CreateBound(p2, p3),
                    Line.CreateBound(p3, p0),
                };
                var loop = CurveLoop.Create(curves);
                var solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, height);

                using (var trans = new Transaction(doc, "Create Mass Box"))
                {
                    trans.Start();

                    // Category Mass so the faces read as a conceptual mass downstream.
                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Mass));
                    ds.SetShape(new GeometryObject[] { solid });
                    ds.Name = name;

                    trans.CommitAndCheck();

                    return ResponseBuilder.Success()
                        .With("massId", ds.Id.Value)
                        .With("name", name)
                        .With("origin", new { x = ox, y = oy, z = oz })
                        .With("width", width)
                        .With("depth", depth)
                        .With("height", height)
                        .With("note", "Loose mass placed. Call createFaceWall with this massId to convert its vertical faces to native walls.")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extrude an arbitrary closed 2D profile (in a u-v plane) along a direction — the proper way to
        /// model pediments/gables, cornices & moldings (a molding profile swept along a length), beams, etc.
        /// profile = [[u,v],...] in the plane (origin + u*uAxis + v*vAxis); extruded along `direction` (default uAxis×vAxis).
        /// </summary>
        [MCPMethod("createExtrudedProfile", Category = "Mass", Description = "Extrude a closed 2D profile along a direction (pediments, cornices, moldings, gables)")]
        public static string CreateExtrudedProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["origin"] == null || parameters["uAxis"] == null || parameters["vAxis"] == null
                    || parameters["profile"] == null || parameters["length"] == null)
                    return ResponseBuilder.Error("origin, uAxis, vAxis, profile [[u,v],...], length required", "VALIDATION_ERROR").Build();
                var o3 = parameters["origin"].ToObject<double[]>();
                var ua = parameters["uAxis"].ToObject<double[]>();
                var va = parameters["vAxis"].ToObject<double[]>();
                var prof = parameters["profile"].ToObject<double[][]>();
                double length = parameters["length"].ToObject<double>();
                if (prof.Length < 3) return ResponseBuilder.Error("profile needs >= 3 points", "VALIDATION_ERROR").Build();

                XYZ o = new XYZ(o3[0], o3[1], o3[2]);
                XYZ u = new XYZ(ua[0], ua[1], ua[2]).Normalize();
                XYZ v = new XYZ(va[0], va[1], va[2]).Normalize();
                XYZ dir = parameters["direction"] != null
                    ? new XYZ(parameters["direction"][0].ToObject<double>(), parameters["direction"][1].ToObject<double>(), parameters["direction"][2].ToObject<double>()).Normalize()
                    : u.CrossProduct(v).Normalize();

                var pts = new List<XYZ>();
                foreach (var p in prof) pts.Add(o + p[0] * u + p[1] * v);
                var loop = new CurveLoop();
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    if (a.DistanceTo(b) > 1e-7) loop.Append(Line.CreateBound(a, b));
                }
                var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, dir, length);

                using (var trans = new Transaction(doc, "Extruded Profile"))
                {
                    trans.Start();
                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Mass));
                    ds.SetShape(new GeometryObject[] { solid });
                    ds.Name = parameters["name"]?.ToString() ?? "Extruded";
                    trans.CommitAndCheck();
                    return ResponseBuilder.Success().With("massId", ds.Id.Value)
                        .With("profilePoints", prof.Length).With("length", length)
                        .With("note", "extruded profile").Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Revolve an ARBITRARY [radius, z] profile 360° about the vertical axis — the proper way to
        /// model turned classical elements: columns (base+shaft+capital), balusters, urns, moldings.
        /// The profile is the OUTER silhouette (bottom→top); it is auto-closed along the axis.
        /// </summary>
        [MCPMethod("createRevolvedProfile", Category = "Mass", Description = "Revolve an arbitrary [radius,z] silhouette into a turned solid (columns, balusters, capitals, urns)")]
        public static string CreateRevolvedProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["center"] == null || parameters["profile"] == null)
                    return ResponseBuilder.Error("center [x,y,z] and profile [[r,z],...] required", "VALIDATION_ERROR").Build();
                var c = parameters["center"].ToObject<double[]>();
                var prof = parameters["profile"].ToObject<double[][]>();
                if (prof.Length < 2)
                    return ResponseBuilder.Error("profile needs >= 2 points", "VALIDATION_ERROR").Build();
                double cx = c[0], cy = c[1], bz = c.Length > 2 ? c[2] : 0.0;
                XYZ origin = new XYZ(cx, cy, bz);
                XYZ rad = XYZ.BasisX, up = XYZ.BasisZ;
                var frame = new Frame(origin, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);

                var pts = new List<XYZ>();
                foreach (var p in prof) pts.Add(origin + Math.Max(0.0, p[0]) * rad + p[1] * up);

                var loop = new CurveLoop();
                for (int i = 0; i < pts.Count - 1; i++)
                    if (pts[i].DistanceTo(pts[i + 1]) > 1e-7) loop.Append(Line.CreateBound(pts[i], pts[i + 1]));
                XYZ axisTop = origin + prof[prof.Length - 1][1] * up;
                XYZ axisBot = origin + prof[0][1] * up;
                if (pts[pts.Count - 1].DistanceTo(axisTop) > 1e-7) loop.Append(Line.CreateBound(pts[pts.Count - 1], axisTop));
                loop.Append(Line.CreateBound(axisTop, axisBot));
                if (axisBot.DistanceTo(pts[0]) > 1e-7) loop.Append(Line.CreateBound(axisBot, pts[0]));

                var solid = GeometryCreationUtilities.CreateRevolvedGeometry(
                    frame, new List<CurveLoop> { loop }, 0.0, 2.0 * Math.PI);

                using (var trans = new Transaction(doc, "Revolved Profile"))
                {
                    trans.Start();
                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Mass));
                    ds.SetShape(new GeometryObject[] { solid });
                    ds.Name = parameters["name"]?.ToString() ?? "Turned";
                    trans.CommitAndCheck();
                    return ResponseBuilder.Success()
                        .With("massId", ds.Id.Value)
                        .With("profilePoints", prof.Length)
                        .With("note", "turned solid of revolution")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a SMOOTH dome as a true solid of revolution (the proper way — not stacked slices).
        /// Revolves a quarter-ellipse profile 360° about the vertical axis through the center.
        /// </summary>
        [MCPMethod("createRevolvedDome", Category = "Mass", Description = "Smooth dome via revolved geometry (radius + dome height); proper modeling, not stacked massing")]
        public static string CreateRevolvedDome(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["center"] == null || parameters["radius"] == null)
                    return ResponseBuilder.Error("center [x,y,z] and radius are required", "VALIDATION_ERROR").Build();
                var c = parameters["center"].ToObject<double[]>();
                double R = parameters["radius"].ToObject<double>();
                double H = parameters["height"]?.ToObject<double>() ?? R;   // dome rise; default hemisphere
                bool solid = parameters["solidBase"]?.ToObject<bool>() ?? true;
                double cx = c[0], cy = c[1], bz = c.Length > 2 ? c[2] : 0.0;

                XYZ origin = new XYZ(cx, cy, bz);
                // Revolution axis = frame.BasisZ (vertical). Profile must lie in a plane CONTAINING that
                // axis — here the X-Z plane (radial = BasisX, vertical = BasisZ).
                XYZ rad = XYZ.BasisX, up = XYZ.BasisZ;
                var frame = new Frame(origin, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);

                XYZ p1 = origin + R * rad;     // base edge (R, 0)
                XYZ apex = origin + H * up;    // top on axis (0, H)
                XYZ p0 = origin;               // axis base (0, 0)

                Curve domeCurve = (Math.Abs(H - R) < 1e-6)
                    ? Arc.Create(origin, R, 0.0, Math.PI / 2.0, rad, up)               // hemisphere
                    : Ellipse.CreateCurve(origin, R, H, rad, up, 0.0, Math.PI / 2.0);  // shallow/tall dome

                var profile = new CurveLoop();
                profile.Append(domeCurve);                  // dome surface: p1 -> apex
                profile.Append(Line.CreateBound(apex, p0)); // down the axis
                profile.Append(Line.CreateBound(p0, p1));   // base radius

                var dome = GeometryCreationUtilities.CreateRevolvedGeometry(
                    frame, new List<CurveLoop> { profile }, 0.0, 2.0 * Math.PI);

                using (var trans = new Transaction(doc, "Revolved Dome"))
                {
                    trans.Start();
                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Mass));
                    ds.SetShape(new GeometryObject[] { dome });
                    ds.Name = parameters["name"]?.ToString() ?? "Dome";
                    trans.CommitAndCheck();
                    return ResponseBuilder.Success()
                        .With("massId", ds.Id.Value)
                        .With("center", new { x = cx, y = cy, z = bz })
                        .With("radius", R).With("domeHeight", H)
                        .With("note", "smooth solid of revolution")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Convert the vertical faces of a mass (DirectShape) into native walls.
        /// Prefers the true FaceWall.Create API; for any face the API rejects, falls back
        /// to a Wall.Create along that face's base edge. Reports which path each face took.
        /// </summary>
        [MCPMethod("createFaceWall", Category = "Mass", Description = "Convert a mass's vertical faces into native walls — true FaceWall where valid, Wall-by-base-edge fallback otherwise")]
        public static string CreateFaceWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["massId"] == null)
                    return ResponseBuilder.Error("massId is required", "VALIDATION_ERROR").Build();

                var mass = doc.GetElement(new ElementId(parameters["massId"].ToObject<long>()));
                if (mass == null)
                    return ResponseBuilder.Error("Mass element not found for massId", "VALIDATION_ERROR").Build();

                // Resolve a basic wall type (FaceWall + Wall.Create both need a Basic wall type)
                WallType wallType = ResolveBasicWallType(doc, parameters);
                if (wallType == null)
                    return ResponseBuilder.Error("No Basic wall type available", "VALIDATION_ERROR").Build();

                // Level for the Wall.Create fallback — explicit, else lowest level
                Level level = ResolveLevel(doc, parameters);
                if (level == null)
                    return ResponseBuilder.Error("No level available for fallback walls", "VALIDATION_ERROR").Build();

                // Pull geometry WITH references so FaceWall can host on the faces
                var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
                var geom = mass.get_Geometry(opt);
                if (geom == null)
                    return ResponseBuilder.Error("Mass has no readable geometry", "GEOMETRY_ERROR").Build();

                var verticalFaces = new List<PlanarFace>();
                CollectVerticalPlanarFaces(geom, verticalFaces);

                if (verticalFaces.Count == 0)
                    return ResponseBuilder.Error("No vertical planar faces found on the mass", "GEOMETRY_ERROR").Build();

                var perFace = new List<object>();
                var faceWallIds = new List<long>();
                var wallIds = new List<long>();

                using (var trans = new Transaction(doc, "Walls by Face"))
                {
                    trans.Start();

                    int idx = 0;
                    foreach (var face in verticalFaces)
                    {
                        idx++;
                        string method = null;
                        long createdId = 0;
                        string err = null;

                        // 1) Prefer the true FaceWall API
                        try
                        {
                            var faceRef = face.Reference;
                            if (faceRef != null && FaceWall.IsValidFaceReferenceForFaceWall(doc, faceRef))
                            {
                                var fw = FaceWall.Create(doc, wallType.Id, WallLocationLine.CoreExterior, faceRef);
                                if (fw != null)
                                {
                                    method = "FaceWall";
                                    createdId = fw.Id.Value;
                                    faceWallIds.Add(createdId);
                                }
                            }
                        }
                        catch (Exception fwEx)
                        {
                            err = "FaceWall: " + fwEx.Message;
                        }

                        // 2) Fallback: native wall along the face's base edge
                        if (createdId == 0)
                        {
                            try
                            {
                                var baseLine = BaseEdgeOf(face, out double faceHeight);
                                if (baseLine != null && faceHeight > 1e-3)
                                {
                                    var flat = Line.CreateBound(
                                        new XYZ(baseLine.GetEndPoint(0).X, baseLine.GetEndPoint(0).Y, level.Elevation),
                                        new XYZ(baseLine.GetEndPoint(1).X, baseLine.GetEndPoint(1).Y, level.Elevation));
                                    var w = Wall.Create(doc, flat, wallType.Id, level.Id, faceHeight, 0.0, false, false);
                                    if (w != null)
                                    {
                                        method = "WallByBaseEdge";
                                        createdId = w.Id.Value;
                                        wallIds.Add(createdId);
                                    }
                                }
                                else
                                {
                                    err = (err == null ? "" : err + "; ") + "no usable base edge";
                                }
                            }
                            catch (Exception wEx)
                            {
                                err = (err == null ? "" : err + "; ") + "Wall: " + wEx.Message;
                            }
                        }

                        perFace.Add(new
                        {
                            faceIndex = idx,
                            method = method ?? "failed",
                            elementId = createdId,
                            normal = new { x = face.FaceNormal.X, y = face.FaceNormal.Y, z = face.FaceNormal.Z },
                            error = err
                        });
                    }

                    trans.CommitAndCheck();
                }

                return ResponseBuilder.Success()
                    .With("massId", parameters["massId"].ToObject<long>())
                    .With("verticalFaces", verticalFaces.Count)
                    .With("faceWallCount", faceWallIds.Count)
                    .With("fallbackWallCount", wallIds.Count)
                    .With("wallTypeUsed", wallType.Name)
                    .With("levelUsed", level.Name)
                    .With("faceWallIds", faceWallIds)
                    .With("wallIds", wallIds)
                    .With("perFace", perFace)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Drop a solid mass (DirectShape) from an ARBITRARY footprint polygon, extruded up.
        /// Generalizes createMassBox beyond rectangles — the loose form for intricate buildings.
        /// </summary>
        [MCPMethod("createMassFromFootprint", Category = "Mass", Description = "Create a solid mass (DirectShape) from an arbitrary footprint polygon + height — loose form for Wall/Floor/Roof by face")]
        public static string CreateMassFromFootprint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["footprint"] == null)
                    return ResponseBuilder.Error("footprint (array of [x,y] points) is required", "VALIDATION_ERROR").Build();

                var pts = parameters["footprint"].ToObject<double[][]>();
                if (pts == null || pts.Length < 3)
                    return ResponseBuilder.Error("footprint needs at least 3 points", "VALIDATION_ERROR").Build();

                double baseZ  = parameters["baseElevation"]?.ToObject<double>() ?? 0.0;
                double height = parameters["height"]?.ToObject<double>() ?? 12.0;
                string name   = parameters["name"]?.ToString() ?? "Mass";
                if (height <= 0)
                    return ResponseBuilder.Error("height must be positive", "VALIDATION_ERROR").Build();

                var curves = new List<Curve>();
                for (int i = 0; i < pts.Length; i++)
                {
                    var a = new XYZ(pts[i][0], pts[i][1], baseZ);
                    var b = new XYZ(pts[(i + 1) % pts.Length][0], pts[(i + 1) % pts.Length][1], baseZ);
                    if (a.DistanceTo(b) > 1e-6) curves.Add(Line.CreateBound(a, b));
                }
                var loop = CurveLoop.Create(curves);
                var solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, height);

                using (var trans = new Transaction(doc, "Create Mass From Footprint"))
                {
                    trans.Start();
                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Mass));
                    ds.SetShape(new GeometryObject[] { solid });
                    ds.Name = name;
                    trans.CommitAndCheck();

                    return ResponseBuilder.Success()
                        .With("massId", ds.Id.Value)
                        .With("name", name)
                        .With("footprintPoints", pts.Length)
                        .With("baseElevation", baseZ)
                        .With("height", height)
                        .With("note", "Loose mass placed. Call createBuildingShell with this massId to convert it to walls + floors + roof.")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Convert a mass into a complete native building shell: per-story walls (multi-story
        /// aware via levels that fall within the mass height), a floor at each story, and a
        /// flat roof at the top. Vertical faces -> walls, bottom loop -> floors, top loop -> roof.
        /// Resilient: each piece reports its own success/error; partial shells still return.
        /// </summary>
        [MCPMethod("createBuildingShell", Category = "Mass", Description = "Convert a mass into native walls + floors + flat roof (multi-story aware) — the full building primitive")]
        public static string CreateBuildingShell(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["massId"] == null)
                    return ResponseBuilder.Error("massId is required", "VALIDATION_ERROR").Build();

                var mass = doc.GetElement(new ElementId(parameters["massId"].ToObject<long>()));
                if (mass == null)
                    return ResponseBuilder.Error("Mass element not found for massId", "VALIDATION_ERROR").Build();

                bool makeFloor = parameters["makeFloor"]?.ToObject<bool>() ?? true;
                bool makeRoof  = parameters["makeRoof"]?.ToObject<bool>() ?? true;

                var wallType  = ResolveBasicWallType(doc, parameters);
                if (wallType == null)
                    return ResponseBuilder.Error("No Basic wall type available", "VALIDATION_ERROR").Build();
                var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault();
                var roofType  = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();

                var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
                var geom = mass.get_Geometry(opt);
                if (geom == null)
                    return ResponseBuilder.Error("Mass has no readable geometry", "GEOMETRY_ERROR").Build();

                PlanarFace bottom = null;
                var allFaces = new List<PlanarFace>();
                CollectAllPlanarFaces(geom, allFaces);
                foreach (var f in allFaces)
                {
                    if (f.FaceNormal.Z < -0.99 && (bottom == null || f.Origin.Z < bottom.Origin.Z))
                        bottom = f;
                }
                if (bottom == null)
                    return ResponseBuilder.Error("No bottom (horizontal) face found on the mass", "GEOMETRY_ERROR").Build();

                double baseZ = bottom.Origin.Z;
                double topZ  = allFaces.Where(f => f.FaceNormal.Z > 0.99).Select(f => f.Origin.Z)
                                       .DefaultIfEmpty(baseZ).Max();
                if (topZ <= baseZ + 1e-3)
                    return ResponseBuilder.Error("Mass has no vertical extent", "GEOMETRY_ERROR").Build();

                // Outer footprint loop from the bottom face
                var bottomLoops = bottom.GetEdgesAsCurveLoops();
                if (bottomLoops == null || bottomLoops.Count == 0)
                    return ResponseBuilder.Error("Could not extract footprint loop from mass", "GEOMETRY_ERROR").Build();
                var outerLoop = bottomLoops.OrderByDescending(LoopLength).First();

                // Story boundaries: levels that fall within [baseZ, topZ)
                var levels = ResolveCandidateLevels(doc, parameters, baseZ, topZ);
                if (levels.Count == 0)
                {
                    var lv = NearestLevelAtOrBelow(doc, baseZ) ?? ResolveLevel(doc, null);
                    if (lv != null) levels.Add(lv);
                }
                if (levels.Count == 0)
                    return ResponseBuilder.Error("No level available to host walls", "VALIDATION_ERROR").Build();

                var stories = new List<object>();
                var wallIds = new List<long>();
                var floorIds = new List<long>();
                long roofId = 0;
                string roofErr = null;
                string roofKind = parameters["roofType"]?.ToString()?.ToLower() ?? "flat";
                double roofSlopeDeg = parameters["roofSlope"]?.ToObject<double>() ?? 26.57; // 6:12
                double roofSlopeTan = Math.Tan(roofSlopeDeg * Math.PI / 180.0);

                using (var trans = new Transaction(doc, "Build Shell From Mass"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);

                    for (int i = 0; i < levels.Count; i++)
                    {
                        var lvl = levels[i];
                        double z0 = lvl.Elevation;
                        double z1 = (i + 1 < levels.Count) ? levels[i + 1].Elevation : topZ;
                        double h = z1 - z0;
                        if (h <= 1e-3) continue;

                        int storyWalls = 0;
                        foreach (var c in outerLoop)
                        {
                            var moved = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z0 - baseZ)));
                            try
                            {
                                var w = Wall.Create(doc, moved, wallType.Id, lvl.Id, h, 0.0, false, false);
                                if (w != null) { wallIds.Add(w.Id.Value); storyWalls++; }
                            }
                            catch { /* skip degenerate segment */ }
                        }

                        long floorId = 0;
                        if (makeFloor && floorType != null)
                        {
                            try
                            {
                                var floorLoop = TranslatedLoop(outerLoop, z0 - baseZ);
                                var fl = Floor.Create(doc, new List<CurveLoop> { floorLoop }, floorType.Id, lvl.Id);
                                if (fl != null) { floorId = fl.Id.Value; floorIds.Add(floorId); }
                            }
                            catch { /* floor optional */ }
                        }

                        stories.Add(new { level = lvl.Name, baseZ = z0, height = h, walls = storyWalls, floorId });
                    }

                    // Roof at the top — flat (default), hip, or gable
                    if (makeRoof && roofType != null)
                    {
                        try
                        {
                            var roofLevel = NearestLevelAtOrBelow(doc, topZ) ?? levels.Last();
                            var arr = new CurveArray();
                            foreach (var c in TranslatedLoop(outerLoop, roofLevel.Elevation - baseZ))
                                arr.Append(c);
                            ModelCurveArray mca = new ModelCurveArray();
                            var roof = doc.Create.NewFootPrintRoof(arr, roofLevel, roofType, out mca);

                            var edges = mca.Cast<ModelCurve>().ToList();
                            if (roofKind == "hip")
                            {
                                foreach (var mc in edges)
                                { roof.set_DefinesSlope(mc, true); roof.set_SlopeAngle(mc, roofSlopeTan); }
                            }
                            else if (roofKind == "gable" && edges.Count == 4)
                            {
                                // slope the LONG pair -> gable ends form on the short walls (Weber rule)
                                var ranked = edges.Select((mc, i) => new { mc, len = mc.GeometryCurve.Length })
                                                  .OrderByDescending(x => x.len).ToList();
                                var longPair = new HashSet<ModelCurve> { ranked[0].mc, ranked[1].mc };
                                foreach (var mc in edges)
                                {
                                    bool slope = longPair.Contains(mc);
                                    roof.set_DefinesSlope(mc, slope);
                                    if (slope) roof.set_SlopeAngle(mc, roofSlopeTan);
                                }
                            }
                            else // flat (or gable on a non-quad footprint -> flat fallback)
                            {
                                foreach (var mc in edges) roof.set_DefinesSlope(mc, false);
                                if (roofKind == "gable") roofKind = "flat(gable-needs-quad)";
                            }

                            var off = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                            if (off != null && !off.IsReadOnly) off.Set(topZ - roofLevel.Elevation);
                            roofId = roof.Id.Value;
                        }
                        catch (Exception rEx) { roofErr = rEx.Message; }
                    }

                    trans.CommitAndCheck();
                }

                return ResponseBuilder.Success()
                    .With("massId", parameters["massId"].ToObject<long>())
                    .With("baseElevation", baseZ)
                    .With("topElevation", topZ)
                    .With("storyCount", stories.Count)
                    .With("wallCount", wallIds.Count)
                    .With("floorCount", floorIds.Count)
                    .With("roofId", roofId)
                    .With("roofKind", roofKind)
                    .With("roofError", roofErr)
                    .With("wallTypeUsed", wallType.Name)
                    .With("floorTypeUsed", floorType?.Name)
                    .With("roofTypeUsed", roofType?.Name)
                    .With("stories", stories)
                    .With("wallIds", wallIds)
                    .With("floorIds", floorIds)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ---- helpers ---------------------------------------------------------

        private static void CollectVerticalPlanarFaces(GeometryElement geom, List<PlanarFace> sink)
        {
            foreach (var g in geom)
            {
                if (g is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face f in solid.Faces)
                    {
                        if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) < 1e-3)
                            sink.Add(pf);
                    }
                }
                else if (g is GeometryInstance gi)
                {
                    CollectVerticalPlanarFaces(gi.GetInstanceGeometry(), sink);
                }
            }
        }

        /// <summary>Lowest horizontal edge of a vertical planar face, plus the face's vertical extent.</summary>
        private static Line BaseEdgeOf(PlanarFace face, out double faceHeight)
        {
            faceHeight = 0;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            Line best = null;
            double bestZ = double.MaxValue;

            foreach (EdgeArray loop in face.EdgeLoops)
            {
                foreach (Edge e in loop)
                {
                    var c = e.AsCurve();
                    var a = c.GetEndPoint(0);
                    var b = c.GetEndPoint(1);
                    minZ = Math.Min(minZ, Math.Min(a.Z, b.Z));
                    maxZ = Math.Max(maxZ, Math.Max(a.Z, b.Z));

                    // horizontal edge candidate (both ends same Z)
                    if (Math.Abs(a.Z - b.Z) < 1e-4)
                    {
                        double z = a.Z;
                        if (z < bestZ && (c is Line))
                        {
                            bestZ = z;
                            best = (Line)c;
                        }
                    }
                }
            }

            if (minZ < double.MaxValue && maxZ > double.MinValue)
                faceHeight = maxZ - minZ;
            return best;
        }

        private static void CollectAllPlanarFaces(GeometryElement geom, List<PlanarFace> sink)
        {
            foreach (var g in geom)
            {
                if (g is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf) sink.Add(pf);
                }
                else if (g is GeometryInstance gi)
                {
                    CollectAllPlanarFaces(gi.GetInstanceGeometry(), sink);
                }
            }
        }

        private static double LoopLength(CurveLoop loop)
        {
            double s = 0;
            foreach (var c in loop) s += c.Length;
            return s;
        }

        private static CurveLoop TranslatedLoop(CurveLoop loop, double dz)
        {
            var t = Transform.CreateTranslation(new XYZ(0, 0, dz));
            var outLoop = new CurveLoop();
            foreach (var c in loop) outLoop.Append(c.CreateTransformed(t));
            return outLoop;
        }

        private static List<Level> ResolveCandidateLevels(Document doc, JObject parameters, double baseZ, double topZ)
        {
            IEnumerable<Level> source;
            if (parameters["levelIds"] != null)
            {
                var ids = parameters["levelIds"].ToObject<long[]>();
                source = ids.Select(id => doc.GetElement(new ElementId(id)) as Level).Where(l => l != null);
            }
            else
            {
                source = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            }
            // A level seeds a story only if it leaves room for a wall band below the top.
            return source
                .Where(l => l.Elevation >= baseZ - 0.05 && l.Elevation <= topZ - 0.5)
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        private static Level NearestLevelAtOrBelow(Document doc, double z)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.Elevation <= z + 0.05)
                .OrderByDescending(l => l.Elevation)
                .FirstOrDefault();
        }

        private static WallType ResolveBasicWallType(Document doc, JObject parameters)
        {
            var basics = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Basic)
                .ToList();
            if (basics.Count == 0) return null;

            if (parameters["wallTypeId"] != null)
            {
                var byId = doc.GetElement(new ElementId(parameters["wallTypeId"].ToObject<long>())) as WallType;
                if (byId != null && byId.Kind == WallKind.Basic) return byId;
            }
            if (parameters["wallTypeName"] != null)
            {
                string n = parameters["wallTypeName"].ToString();
                var byName = basics.FirstOrDefault(wt => wt.Name == n);
                if (byName != null) return byName;
            }
            return basics.First();
        }

        private static Level ResolveLevel(Document doc, JObject parameters)
        {
            if (parameters["levelId"] != null)
            {
                var lv = doc.GetElement(new ElementId(parameters["levelId"].ToObject<long>())) as Level;
                if (lv != null) return lv;
            }
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();
        }
    }
}
