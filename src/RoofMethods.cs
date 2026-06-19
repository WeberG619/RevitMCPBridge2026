using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Extended roof query and manipulation methods for MCP Bridge.
    /// Does NOT duplicate methods in FloorCeilingRoofMethods.cs.
    /// </summary>
    public static class RoofMethods
    {
        #region Query Methods

        /// <summary>
        /// Get all roofs in the document.
        /// Returns roofId, typeName, area, levelName, roofType (footprint/extrusion).
        /// </summary>
        [MCPMethod("getRoofs", Category = "Roof", Description = "Get all roofs in the document with type, area, level, and roof style")]
        public static string GetRoofs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofs = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .Select(r =>
                    {
                        var level = doc.GetElement(r.LevelId) as Level;
                        string roofStyle = "unknown";
                        if (r is FootPrintRoof) roofStyle = "footprint";
                        else if (r is ExtrusionRoof) roofStyle = "extrusion";

                        var areaParam = r.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        return new
                        {
                            roofId = (int)r.Id.Value,
                            typeName = r.Name,
                            area = area,
                            levelName = level?.Name ?? "Unknown",
                            levelId = level != null ? (int)level.Id.Value : -1,
                            roofType = roofStyle
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofCount = roofs.Count,
                    roofs = roofs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detailed info for one roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofInfo", Category = "Roof", Description = "Get detailed information for a specific roof including type, slope, area, boundary, thickness, and material")]
        public static string GetRoofInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var level = doc.GetElement(roof.LevelId) as Level;
                var roofType = doc.GetElement(roof.GetTypeId()) as RoofType;

                string roofStyle = "unknown";
                if (roof is FootPrintRoof) roofStyle = "footprint";
                else if (roof is ExtrusionRoof) roofStyle = "extrusion";

                // Area
                var areaParam = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double area = areaParam != null ? areaParam.AsDouble() : 0;

                // Slope
                var slopeParam = roof.get_Parameter(BuiltInParameter.ROOF_SLOPE);
                double slope = slopeParam != null ? slopeParam.AsDouble() : 0;

                // Offset
                var offsetParam = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                double offset = offsetParam != null ? offsetParam.AsDouble() : 0;

                // Thickness from type
                double thickness = 0;
                if (roofType != null)
                {
                    var cs = roofType.GetCompoundStructure();
                    if (cs != null)
                        thickness = cs.GetWidth();
                }

                // Material from first structural layer
                string materialName = "None";
                if (roofType != null)
                {
                    var cs = roofType.GetCompoundStructure();
                    if (cs != null)
                    {
                        var layers = cs.GetLayers();
                        foreach (var layer in layers)
                        {
                            if (layer.Function == MaterialFunctionAssignment.Structure)
                            {
                                var mat = doc.GetElement(layer.MaterialId) as Material;
                                if (mat != null)
                                {
                                    materialName = mat.Name;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Boundary points (footprint roofs)
                var boundaryPoints = new List<object>();
                if (roof is FootPrintRoof fpr)
                {
                    var modelCurveArray = fpr.GetProfiles();
                    if (modelCurveArray != null)
                    {
                        foreach (ModelCurveArray profile in modelCurveArray)
                        {
                            foreach (ModelCurve mc in profile)
                            {
                                var curve = mc.GeometryCurve;
                                var start = curve.GetEndPoint(0);
                                boundaryPoints.Add(new { x = start.X, y = start.Y, z = start.Z });
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    typeName = roofType?.Name ?? "Unknown",
                    roofStyle = roofStyle,
                    slope = slope,
                    area = area,
                    offset = offset,
                    thickness = thickness,
                    material = materialName,
                    levelName = level?.Name ?? "Unknown",
                    levelId = level != null ? (int)level.Id.Value : -1,
                    boundaryPoints = boundaryPoints
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get slope info per edge of a footprint roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofSlope", Category = "Roof", Description = "Get slope info per edge of a footprint roof including defining/non-defining status")]
        public static string GetRoofSlope(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as FootPrintRoof;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found or is not a footprint roof", "NOT_FOUND").Build();

                var edgeInfos = new List<object>();
                var profiles = roof.GetProfiles();
                int edgeIndex = 0;

                if (profiles != null)
                {
                    foreach (ModelCurveArray profile in profiles)
                    {
                        foreach (ModelCurve mc in profile)
                        {
                            bool isDefining = roof.get_DefinesSlope(mc);
                            double slopeAngle = roof.get_SlopeAngle(mc);
                            double overhang = roof.get_Overhang(mc);
                            bool isExtended = roof.get_ExtendIntoWall(mc);

                            var curve = mc.GeometryCurve;
                            var start = curve.GetEndPoint(0);
                            var end = curve.GetEndPoint(1);

                            edgeInfos.Add(new
                            {
                                edgeIndex = edgeIndex,
                                definesSlope = isDefining,
                                slopeAngle = slopeAngle,
                                overhang = overhang,
                                extendIntoWall = isExtended,
                                startPoint = new { x = start.X, y = start.Y, z = start.Z },
                                endPoint = new { x = end.X, y = end.Y, z = end.Z }
                            });
                            edgeIndex++;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    edgeCount = edgeInfos.Count,
                    edges = edgeInfos
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Roof framing interpreter. Reads the roof's actual solid geometry and
        /// classifies it into the lines a framer needs: ridges, hips, valleys,
        /// eaves and rakes, plus every sloped top face with its slope and eave.
        /// Geometry-driven (not footprint-driven) so hips, gables and intersecting
        /// roofs with valleys all resolve through the same code.
        /// Read-only — no transaction.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("analyzeRoofFraming", Category = "Roof", Description = "Interpret a roof's solid geometry into framing lines: ridges, hips, valleys, eaves, rakes, and sloped top faces with slope/eave. Keystone for rafter/truss layout.")]
        public static string AnalyzeRoofFraming(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                const double NZ_EPS = 0.001;   // min vertical component for a "top" face
                const double Z_TOL = 0.02;     // ft, horizontal-edge / fold tolerance
                const double SAMPLE = 0.5;      // ft, inward sampling step for fold test

                // ---- collect solids (handle nested geometry instances) ----
                var opts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var ge = roof.get_Geometry(opts);
                var solids = new List<Solid>();
                CollectSolids(ge, solids);
                if (solids.Count == 0)
                    return ResponseBuilder.Error("No solid geometry on roof", "NO_GEOMETRY").Build();

                // ---- first pass: gather sloped top faces ----
                var topFaces = new List<PlanarFace>();
                foreach (var solid in solids)
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.Z > NZ_EPS)
                            topFaces.Add(pf);

                var faceOut = new List<object>();
                for (int i = 0; i < topFaces.Count; i++)
                {
                    var pf = topFaces[i];
                    var n = pf.FaceNormal;
                    double slopeRad = Math.Acos(Math.Min(1.0, n.Z));
                    double riseRun = n.Z > NZ_EPS ? Math.Sqrt(n.X * n.X + n.Y * n.Y) / n.Z : 0;
                    var eave = LowestHorizontalEdge(pf, Z_TOL);
                    var cen = FaceCentroid(pf);
                    faceOut.Add(new
                    {
                        faceIndex = i,
                        normal = new { x = n.X, y = n.Y, z = n.Z },
                        slopeDegrees = slopeRad * 180.0 / Math.PI,
                        slopeRiseRun = riseRun,
                        slopeRisePer12 = riseRun * 12.0,
                        area = pf.Area,
                        centroid = new { x = cen.X, y = cen.Y, z = cen.Z },
                        eaveLine = eave
                    });
                }

                // ---- second pass: classify every edge ----
                var ridges = new List<object>();
                var hips = new List<object>();
                var valleys = new List<object>();
                var eaves = new List<object>();
                var rakes = new List<object>();

                foreach (var solid in solids)
                {
                    foreach (Edge e in solid.Edges)
                    {
                        var f0 = e.GetFace(0);
                        var f1 = e.GetFace(1);
                        bool t0 = f0 is PlanarFace p0 && p0.FaceNormal.Z > NZ_EPS;
                        bool t1 = f1 is PlanarFace p1 && p1.FaceNormal.Z > NZ_EPS;

                        var curve = e.AsCurve();
                        var a = curve.GetEndPoint(0);
                        var b = curve.GetEndPoint(1);
                        double len = curve.Length;
                        var mid = (a + b) * 0.5;
                        var tan = (b - a);
                        if (tan.IsZeroLength()) continue;
                        tan = tan.Normalize();
                        bool horizontal = Math.Abs(b.Z - a.Z) < Z_TOL;
                        var seg = new
                        {
                            start = new { x = a.X, y = a.Y, z = a.Z },
                            end = new { x = b.X, y = b.Y, z = b.Z },
                            length = len
                        };

                        if (t0 && t1)
                        {
                            // interior crease between two sloped faces — fold test
                            double z0 = SampleZInward((PlanarFace)f0, mid, tan, SAMPLE);
                            double z1 = SampleZInward((PlanarFace)f1, mid, tan, SAMPLE);
                            int ia = IndexOfFace(topFaces, f0);
                            int ib = IndexOfFace(topFaces, f1);
                            if (z0 < mid.Z - Z_TOL && z1 < mid.Z - Z_TOL)
                            {
                                // mountain fold
                                if (horizontal)
                                    ridges.Add(seg);
                                else
                                    hips.Add(new { seg.start, seg.end, seg.length, faceA = ia, faceB = ib });
                            }
                            else if (z0 > mid.Z + Z_TOL && z1 > mid.Z + Z_TOL)
                            {
                                valleys.Add(new { seg.start, seg.end, seg.length, faceA = ia, faceB = ib });
                            }
                            // else: coplanar / ambiguous seam — ignore
                        }
                        else if (t0 ^ t1)
                        {
                            // boundary: one top face + a side/bottom face
                            int fi = IndexOfFace(topFaces, t0 ? f0 : f1);
                            if (horizontal)
                                eaves.Add(new { seg.start, seg.end, seg.length, faceIndex = fi });
                            else
                                rakes.Add(new { seg.start, seg.end, seg.length, faceIndex = fi });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    topFaceCount = topFaces.Count,
                    counts = new
                    {
                        ridges = ridges.Count,
                        hips = hips.Count,
                        valleys = valleys.Count,
                        eaves = eaves.Count,
                        rakes = rakes.Count
                    },
                    topFaces = faceOut,
                    ridges = ridges,
                    hips = hips,
                    valleys = valleys,
                    eaves = eaves,
                    rakes = rakes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Lay common + jack rafters on every sloped face of a roof, at a spacing o.c.
        /// Built on the same geometry analyzeRoofFraming reads: for each top face it marches
        /// along the eave and shoots an up-slope ray, clipping it to the face boundary — a
        /// station that reaches the ridge gives a common rafter, one that dies into a hip/valley
        /// gives a jack, automatically. Members placed with DisallowJoinAtEnd (square-cut, stay
        /// exactly on the line — the wood-truss technique).
        /// Parameters:
        /// - roofId: roof element id (required)
        /// - spacing: rafter spacing o.c. in INCHES (default 24)
        /// - framingTypeId: (optional) structural framing FamilySymbol id; default = first loaded
        /// </summary>
        [MCPMethod("layoutRoofRafters", Category = "Roof", Description = "Lay common+jack rafters on every sloped roof face at a spacing o.c. (built on analyzeRoofFraming geometry). Returns per-face rafter counts + member ids.")]
        public static string LayoutRoofRafters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                double spacingIn = parameters["spacing"]?.ToObject<double>() ?? 24.0;
                double spacing = spacingIn / 12.0;
                if (spacing < 0.1) spacing = 2.0;

                // framing symbol: explicit, else first loaded structural framing family
                FamilySymbol sym = null;
                if (parameters["framingTypeId"] != null)
                    sym = doc.GetElement(new ElementId(parameters["framingTypeId"].ToObject<int>())) as FamilySymbol;
                if (sym == null)
                    sym = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralFraming)
                        .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault();
                if (sym == null)
                    return ResponseBuilder.Error("No structural framing family loaded — load a rafter/lumber family first (revit_load_autodesk_family).", "NO_FRAMING_TYPE").Build();

                var level = doc.GetElement(roof.LevelId) as Level
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                const double NZ_EPS = 0.001, Z_TOL = 0.02, EPS = 0.05, BIG = 1000.0;

                var opts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var solids = new List<Solid>();
                CollectSolids(roof.get_Geometry(opts), solids);
                if (solids.Count == 0)
                    return ResponseBuilder.Error("No solid geometry on roof", "NO_GEOMETRY").Build();

                var topFaces = new List<PlanarFace>();
                foreach (var solid in solids)
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.Z > NZ_EPS)
                            topFaces.Add(pf);

                var faceResults = new List<object>();
                var allMemberIds = new List<int>();
                int total = 0;

                using (var trans = new Transaction(doc, "Layout Roof Rafters"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);
                    if (!sym.IsActive) sym.Activate();

                    for (int fi = 0; fi < topFaces.Count; fi++)
                    {
                        var pf = topFaces[fi];
                        var n = pf.FaceNormal;

                        // boundary line segments + lowest horizontal edge (the eave)
                        var segs = new List<Line>();
                        XYZ eaveA = null, eaveB = null;
                        double eaveZ = double.MaxValue;
                        foreach (CurveLoop loop in pf.GetEdgesAsCurveLoops())
                            foreach (Curve c in loop)
                            {
                                if (c is Line ln) segs.Add(ln);
                                var s = c.GetEndPoint(0); var e = c.GetEndPoint(1);
                                if (Math.Abs(s.Z - e.Z) < Z_TOL)
                                {
                                    double z = (s.Z + e.Z) * 0.5;
                                    if (z < eaveZ) { eaveZ = z; eaveA = s; eaveB = e; }
                                }
                            }
                        if (eaveA == null)
                        {
                            faceResults.Add(new { faceIndex = fi, rafterCount = 0, note = "no horizontal eave" });
                            continue;
                        }

                        var eaveVec = eaveB - eaveA;
                        double eaveLen = eaveVec.GetLength();
                        if (eaveLen < EPS) continue;
                        var eaveDir = eaveVec.Normalize();

                        // up-slope, in-plane, perpendicular to eave, oriented into the face (uphill)
                        var vDir = n.CrossProduct(eaveDir);
                        if (vDir.IsZeroLength()) continue;
                        vDir = vDir.Normalize();
                        var eaveMid = (eaveA + eaveB) * 0.5;
                        if (vDir.DotProduct(FaceCentroid(pf) - eaveMid) < 0) vDir = vDir.Negate();

                        int count = 0;
                        double firstLen = 0, lastLen = 0;
                        for (double sdist = spacing * 0.5; sdist < eaveLen - EPS; sdist += spacing)
                        {
                            var P = eaveA + eaveDir * sdist;
                            var ray = Line.CreateBound(P, P + vDir * BIG);

                            XYZ hit = null;
                            double hitDist = double.MaxValue;
                            foreach (var seg in segs)
                            {
                                IntersectionResultArray res = null;
                                if (ray.Intersect(seg, out res) == SetComparisonResult.Overlap && res != null)
                                    foreach (IntersectionResult ir in res)
                                    {
                                        double d = P.DistanceTo(ir.XYZPoint);
                                        if (d > EPS && d < hitDist) { hitDist = d; hit = ir.XYZPoint; }
                                    }
                            }
                            if (hit == null) continue;

                            var inst = doc.Create.NewFamilyInstance(Line.CreateBound(P, hit), sym, level, StructuralType.Beam);
                            try
                            {
                                StructuralFramingUtils.DisallowJoinAtEnd(inst, 0);
                                StructuralFramingUtils.DisallowJoinAtEnd(inst, 1);
                            }
                            catch { }
                            allMemberIds.Add((int)inst.Id.Value);
                            if (count == 0) firstLen = hitDist;
                            lastLen = hitDist;
                            count++;
                        }

                        total += count;
                        faceResults.Add(new
                        {
                            faceIndex = fi,
                            rafterCount = count,
                            eaveLength = eaveLen,
                            firstRafterLength = firstLen,
                            lastRafterLength = lastLen,
                            slopeDegrees = Math.Acos(Math.Min(1.0, n.Z)) * 180.0 / Math.PI
                        });
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    framingType = sym.Name,
                    spacingInches = spacingIn,
                    totalRafters = total,
                    faceCount = topFaces.Count,
                    faces = faceResults,
                    memberIds = allMemberIds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Lay horizontal ceiling joists (rafter ties) across a roof's bearing span,
        /// at a spacing o.c. Finds the two longest PARALLEL eaves (the bearing walls)
        /// and spans joists between them at plate level. Pairs with layoutRoofRafters
        /// to complete a stick-framed roof. Targets the main rectangular span.
        /// Parameters:
        /// - roofId: roof element id (required)
        /// - spacing: joist spacing o.c. in INCHES (default 16)
        /// - framingTypeId: (optional) structural framing FamilySymbol id; default = first loaded
        /// </summary>
        [MCPMethod("layoutCeilingJoists", Category = "Roof", Description = "Lay horizontal ceiling joists across the bearing span (between the two parallel bearing eaves) at a spacing o.c. Pairs with layoutRoofRafters. Returns joist count + member ids.")]
        public static string LayoutCeilingJoists(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                double spacingIn = parameters["spacing"]?.ToObject<double>() ?? 16.0;
                double spacing = spacingIn / 12.0;
                if (spacing < 0.1) spacing = 1.333;

                FamilySymbol sym = null;
                if (parameters["framingTypeId"] != null)
                    sym = doc.GetElement(new ElementId(parameters["framingTypeId"].ToObject<int>())) as FamilySymbol;
                if (sym == null)
                    sym = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralFraming)
                        .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault();
                if (sym == null)
                    return ResponseBuilder.Error("No structural framing family loaded.", "NO_FRAMING_TYPE").Build();

                var level = doc.GetElement(roof.LevelId) as Level
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                const double NZ_EPS = 0.001, Z_TOL = 0.02, EPS = 0.05;
                var opts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var solids = new List<Solid>();
                CollectSolids(roof.get_Geometry(opts), solids);
                if (solids.Count == 0)
                    return ResponseBuilder.Error("No solid geometry on roof", "NO_GEOMETRY").Build();

                // gather each top face's lowest horizontal edge (= an eave) as a segment
                var eaves = new List<(XYZ a, XYZ b, double len)>();
                foreach (var solid in solids)
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.Z > NZ_EPS)
                        {
                            XYZ ea = null, eb = null; double ez = double.MaxValue;
                            foreach (CurveLoop loop in pf.GetEdgesAsCurveLoops())
                                foreach (Curve c in loop)
                                {
                                    var s = c.GetEndPoint(0); var e = c.GetEndPoint(1);
                                    if (Math.Abs(s.Z - e.Z) < Z_TOL)
                                    {
                                        double z = (s.Z + e.Z) * 0.5;
                                        if (z < ez) { ez = z; ea = s; eb = e; }
                                    }
                                }
                            if (ea != null && eb.DistanceTo(ea) > EPS)
                                eaves.Add((ea, eb, eb.DistanceTo(ea)));
                        }
                if (eaves.Count < 2)
                    return ResponseBuilder.Error("Need at least two eaves to span joists", "NO_SPAN").Build();

                // pick the best PARALLEL, SEPARATED eave pair, maximizing min(length)
                (XYZ a, XYZ b, double len) e1 = eaves[0], e2 = eaves[0];
                double bestScore = -1;
                for (int i = 0; i < eaves.Count; i++)
                    for (int j = i + 1; j < eaves.Count; j++)
                    {
                        var di = (eaves[i].b - eaves[i].a).Normalize();
                        var dj = (eaves[j].b - eaves[j].a).Normalize();
                        if (Math.Abs(di.DotProduct(dj)) < 0.98) continue;   // not parallel
                        var mid_i = (eaves[i].a + eaves[i].b) * 0.5;
                        var mid_j = (eaves[j].a + eaves[j].b) * 0.5;
                        if (mid_i.DistanceTo(mid_j) < EPS) continue;        // same line
                        double score = Math.Min(eaves[i].len, eaves[j].len);
                        if (score > bestScore) { bestScore = score; e1 = eaves[i]; e2 = eaves[j]; }
                    }
                if (bestScore < 0)
                    return ResponseBuilder.Error("No parallel bearing eave pair found", "NO_SPAN").Build();

                // march along the longer eave; project each station onto the other eave line
                if (e2.len > e1.len) { var t = e1; e1 = e2; e2 = t; }
                var a0 = e1.a; var dirA = (e1.b - e1.a).Normalize(); double lenA = e1.len;
                var b0 = e2.a; var dirB = (e2.b - e2.a).Normalize();
                double plateZ = Math.Min(a0.Z, b0.Z);

                var memberIds = new List<int>();
                int count = 0; double span0 = 0;
                using (var trans = new Transaction(doc, "Layout Ceiling Joists"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);
                    if (!sym.IsActive) sym.Activate();

                    for (double s = spacing * 0.5; s < lenA - EPS; s += spacing)
                    {
                        var Pa = a0 + dirA * s;
                        var Pb = b0 + dirB * (Pa - b0).DotProduct(dirB);   // project onto eave-B line
                        Pa = new XYZ(Pa.X, Pa.Y, plateZ);
                        Pb = new XYZ(Pb.X, Pb.Y, plateZ);
                        if (Pa.DistanceTo(Pb) < EPS) continue;
                        var inst = doc.Create.NewFamilyInstance(Line.CreateBound(Pa, Pb), sym, level, StructuralType.Beam);
                        try { StructuralFramingUtils.DisallowJoinAtEnd(inst, 0); } catch { }
                        try { StructuralFramingUtils.DisallowJoinAtEnd(inst, 1); } catch { }
                        memberIds.Add((int)inst.Id.Value);
                        if (count == 0) span0 = Pa.DistanceTo(Pb);
                        count++;
                    }
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    framingType = sym.Name,
                    spacingInches = spacingIn,
                    joistCount = count,
                    bearingRun = lenA,
                    spanLength = span0,
                    plateElevation = plateZ,
                    memberIds = memberIds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Lay a SET of common wood trusses across a roof's bearing span (the FL
        /// default roof system). Finds the two parallel bearing eaves, computes the
        /// ridge height from the roof geometry, and steps gable trusses between them
        /// at spacing o.c. — each built by the native createWoodTruss. Targets the
        /// main rectangular span; hip step-down trusses are a future refinement.
        /// Parameters:
        /// - roofId: roof element id (required)
        /// - spacing: truss spacing o.c. in INCHES (default 24)
        /// - chordTypeId / webTypeId: (optional) structural framing FamilySymbol ids; default = first loaded
        /// - panels: web panels per truss (default 8)
        /// - shape: 'gable' (default) | 'flat' | 'mono'
        /// </summary>
        [MCPMethod("layoutRoofTrusses", Category = "Roof", Description = "Lay a set of common wood trusses across the bearing span at spacing o.c. (FL-default roof). Builds each via native createWoodTruss. Returns truss count + height/span.")]
        public static string LayoutRoofTrusses(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                double spacingIn = parameters["spacing"]?.ToObject<double>() ?? 24.0;
                double spacing = spacingIn / 12.0;
                if (spacing < 0.1) spacing = 2.0;
                int panels = parameters["panels"]?.ToObject<int>() ?? 8;
                string shape = parameters["shape"]?.ToObject<string>() ?? "gable";

                // chord/web types: explicit or first loaded structural framing
                FamilySymbol firstSF() => new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault();
                var chord = parameters["chordTypeId"] != null
                    ? doc.GetElement(new ElementId(parameters["chordTypeId"].ToObject<int>())) as FamilySymbol : firstSF();
                var web = parameters["webTypeId"] != null
                    ? doc.GetElement(new ElementId(parameters["webTypeId"].ToObject<int>())) as FamilySymbol : firstSF();
                if (chord == null || web == null)
                    return ResponseBuilder.Error("No structural framing family loaded for chords/webs.", "NO_FRAMING_TYPE").Build();

                var level = doc.GetElement(roof.LevelId) as Level
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                const double NZ_EPS = 0.001, Z_TOL = 0.02, EPS = 0.05;
                var opts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var solids = new List<Solid>();
                CollectSolids(roof.get_Geometry(opts), solids);
                if (solids.Count == 0)
                    return ResponseBuilder.Error("No solid geometry on roof", "NO_GEOMETRY").Build();

                // eaves (lowest horizontal edge per top face) + ridge Z (max top-face vertex Z)
                var eaves = new List<(XYZ a, XYZ b, double len)>();
                double ridgeZ = double.MinValue;
                foreach (var solid in solids)
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.Z > NZ_EPS)
                        {
                            XYZ ea = null, eb = null; double ez = double.MaxValue;
                            foreach (CurveLoop loop in pf.GetEdgesAsCurveLoops())
                                foreach (Curve c in loop)
                                {
                                    var s = c.GetEndPoint(0); var e = c.GetEndPoint(1);
                                    if (s.Z > ridgeZ) ridgeZ = s.Z;
                                    if (e.Z > ridgeZ) ridgeZ = e.Z;
                                    if (Math.Abs(s.Z - e.Z) < Z_TOL)
                                    {
                                        double z = (s.Z + e.Z) * 0.5;
                                        if (z < ez) { ez = z; ea = s; eb = e; }
                                    }
                                }
                            if (ea != null && eb.DistanceTo(ea) > EPS)
                                eaves.Add((ea, eb, eb.DistanceTo(ea)));
                        }
                if (eaves.Count < 2)
                    return ResponseBuilder.Error("Need at least two eaves to span trusses", "NO_SPAN").Build();

                // best parallel, separated eave pair
                (XYZ a, XYZ b, double len) e1 = eaves[0], e2 = eaves[0];
                double bestScore = -1;
                for (int i = 0; i < eaves.Count; i++)
                    for (int j = i + 1; j < eaves.Count; j++)
                    {
                        var di = (eaves[i].b - eaves[i].a).Normalize();
                        var dj = (eaves[j].b - eaves[j].a).Normalize();
                        if (Math.Abs(di.DotProduct(dj)) < 0.98) continue;
                        if (((eaves[i].a + eaves[i].b) * 0.5).DistanceTo((eaves[j].a + eaves[j].b) * 0.5) < EPS) continue;
                        double score = Math.Min(eaves[i].len, eaves[j].len);
                        if (score > bestScore) { bestScore = score; e1 = eaves[i]; e2 = eaves[j]; }
                    }
                if (bestScore < 0)
                    return ResponseBuilder.Error("No parallel bearing eave pair found", "NO_SPAN").Build();

                if (e2.len > e1.len) { var t = e1; e1 = e2; e2 = t; }
                var a0 = e1.a; var dirA = (e1.b - e1.a).Normalize(); double lenA = e1.len;
                var b0 = e2.a; var dirB = (e2.b - e2.a).Normalize();
                double plateZ = Math.Min(a0.Z, b0.Z);
                double height = ridgeZ - plateZ;
                // true cross-span = perpendicular distance between the two bearing eaves
                var mid1 = (e1.a + e1.b) * 0.5;
                var toB = mid1 - b0;
                double crossSpan = (toB - dirB * toB.DotProduct(dirB)).GetLength();
                double bearingOffset = plateZ - level.Elevation;
                if (height < EPS)
                    return ResponseBuilder.Error("Computed truss height ~0; not a sloped roof?", "FLAT").Build();

                int trussCount = 0, memberTotal = 0;
                var failures = new List<string>();
                for (double s = spacing * 0.5; s < lenA - EPS; s += spacing)
                {
                    var Pa = a0 + dirA * s;
                    var Pb = b0 + dirB * (Pa - b0).DotProduct(dirB);
                    var jo = new JObject
                    {
                        ["startPoint"] = new JObject { ["x"] = Pa.X, ["y"] = Pa.Y },
                        ["endPoint"] = new JObject { ["x"] = Pb.X, ["y"] = Pb.Y },
                        ["height"] = height,
                        ["chordTypeId"] = (int)chord.Id.Value,
                        ["webTypeId"] = (int)web.Id.Value,
                        ["levelId"] = (int)level.Id.Value,
                        ["bearingOffset"] = bearingOffset,
                        ["shape"] = shape,
                        ["panels"] = panels
                    };
                    var res = RevitMCPBridge2026.TrussBeamMethods.CreateWoodTruss(uiApp, jo);
                    try
                    {
                        var jr = JObject.Parse(res);
                        if (jr["success"]?.ToObject<bool>() == true)
                        {
                            trussCount++;
                            var mids = jr["memberIds"] as JArray;
                            if (mids != null) memberTotal += mids.Count;
                        }
                        else if (failures.Count < 3)
                            failures.Add(jr["error"]?.ToString() ?? "unknown");
                    }
                    catch { if (failures.Count < 3) failures.Add("unparseable truss result"); }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    chordType = chord.Name,
                    webType = web.Name,
                    spacingInches = spacingIn,
                    trussCount = trussCount,
                    memberTotal = memberTotal,
                    trussHeight = height,
                    span = crossSpan,
                    bearingRun = lenA,
                    plateElevation = plateZ,
                    shape = shape,
                    failuresSample = failures
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ---- roof framing interpreter helpers ----

        private static void CollectSolids(GeometryElement ge, List<Solid> solids)
        {
            if (ge == null) return;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid s && s.Faces.Size > 0 && s.Volume > 1e-6)
                    solids.Add(s);
                else if (go is GeometryInstance gi)
                    CollectSolids(gi.GetInstanceGeometry(), solids);
            }
        }

        private static XYZ FaceCentroid(Face f)
        {
            var mesh = f.Triangulate();
            if (mesh == null || mesh.Vertices.Count == 0)
                return XYZ.Zero;
            var sum = XYZ.Zero;
            foreach (var v in mesh.Vertices) sum += v;
            return sum * (1.0 / mesh.Vertices.Count);
        }

        // Z of a point stepped a short distance off the edge, into the face plane.
        // Used to tell a mountain fold (ridge/hip) from a valley fold.
        private static double SampleZInward(PlanarFace pf, XYZ mid, XYZ edgeTan, double step)
        {
            var inPlane = pf.FaceNormal.CrossProduct(edgeTan);  // in face, perp to edge
            if (inPlane.IsZeroLength()) return mid.Z;
            inPlane = inPlane.Normalize();
            var cen = FaceCentroid(pf);
            if (inPlane.DotProduct(cen - mid) < 0) inPlane = inPlane.Negate();
            return (mid + inPlane * step).Z;
        }

        private static int IndexOfFace(List<PlanarFace> faces, Face target)
        {
            for (int i = 0; i < faces.Count; i++)
                if (ReferenceEquals(faces[i], target)) return i;
            // fallback: match by centroid proximity (different managed instance)
            if (target is PlanarFace tpf)
            {
                var tc = FaceCentroid(tpf);
                for (int i = 0; i < faces.Count; i++)
                    if (FaceCentroid(faces[i]).DistanceTo(tc) < 0.01) return i;
            }
            return -1;
        }

        private static object LowestHorizontalEdge(PlanarFace pf, double zTol)
        {
            Curve best = null;
            double bestZ = double.MaxValue;
            foreach (CurveLoop loop in pf.GetEdgesAsCurveLoops())
            {
                foreach (Curve c in loop)
                {
                    var s = c.GetEndPoint(0);
                    var e = c.GetEndPoint(1);
                    if (Math.Abs(s.Z - e.Z) >= zTol) continue;  // not horizontal
                    double z = (s.Z + e.Z) * 0.5;
                    if (z < bestZ)
                    {
                        bestZ = z;
                        best = c;
                    }
                }
            }
            if (best == null) return null;
            var a = best.GetEndPoint(0);
            var b = best.GetEndPoint(1);
            return new
            {
                start = new { x = a.X, y = a.Y, z = a.Z },
                end = new { x = b.X, y = b.Y, z = b.Z },
                length = best.Length
            };
        }

        /// <summary>
        /// Get precise roof area including actual surface area with slopes.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofArea", Category = "Roof", Description = "Get precise roof area including actual surface area accounting for slopes")]
        public static string GetRoofArea(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                // Computed area from Revit (accounts for slope)
                var areaParam = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double computedArea = areaParam != null ? areaParam.AsDouble() : 0;

                // Try to get projected area from geometry
                double surfaceArea = 0;
                var options = new Options();
                options.ComputeReferences = true;
                var geom = roof.get_Geometry(options);
                if (geom != null)
                {
                    foreach (GeometryObject gObj in geom)
                    {
                        if (gObj is Solid solid)
                        {
                            foreach (Face face in solid.Faces)
                            {
                                // Top faces contribute to roof area
                                surfaceArea += face.Area;
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    computedArea = computedArea,
                    totalSurfaceArea = surfaceArea
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all roofs on a specific level.
        /// Parameters:
        /// - levelId: ID of the level
        /// </summary>
        [MCPMethod("getRoofsOnLevel", Category = "Roof", Description = "Get all roofs on a specific level")]
        public static string GetRoofsOnLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["levelId"] == null)
                    return ResponseBuilder.Error("levelId is required", "MISSING_PARAM").Build();

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                    return ResponseBuilder.Error("Level not found", "NOT_FOUND").Build();

                var roofs = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .Where(r => r.LevelId == levelId)
                    .Select(r =>
                    {
                        string roofStyle = "unknown";
                        if (r is FootPrintRoof) roofStyle = "footprint";
                        else if (r is ExtrusionRoof) roofStyle = "extrusion";

                        var areaParam = r.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        return new
                        {
                            roofId = (int)r.Id.Value,
                            typeName = r.Name,
                            area = area,
                            roofType = roofStyle
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelName = level.Name,
                    roofCount = roofs.Count,
                    roofs = roofs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all sub-elements (fascias, gutters, soffits) of a roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofSubElements", Category = "Roof", Description = "Get all sub-elements (fascias, gutters, soffits) associated with a roof")]
        public static string GetRoofSubElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                // Find fascias hosted on this roof
                var fascias = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Fascia)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Where(e =>
                    {
                        var hostParam = e.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM);
                        if (hostParam != null) return true;
                        // Check via dependency
                        var depIds = e.GetDependentElements(null);
                        return depIds != null && depIds.Contains(roofId);
                    })
                    .Select(e => new
                    {
                        elementId = (int)e.Id.Value,
                        typeName = e.Name,
                        category = "Fascia"
                    })
                    .ToList();

                // Find gutters
                var gutters = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Gutter)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Select(e => new
                    {
                        elementId = (int)e.Id.Value,
                        typeName = e.Name,
                        category = "Gutter"
                    })
                    .ToList();

                // Find soffits
                var soffits = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RoofSoffit)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Select(e => new
                    {
                        elementId = (int)e.Id.Value,
                        typeName = e.Name,
                        category = "Soffit"
                    })
                    .ToList();

                var allSubElements = new List<object>();
                allSubElements.AddRange(fascias.Cast<object>());
                allSubElements.AddRange(gutters.Cast<object>());
                allSubElements.AddRange(soffits.Cast<object>());

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    fasciaCount = fascias.Count,
                    gutterCount = gutters.Count,
                    soffitCount = soffits.Count,
                    totalSubElements = allSubElements.Count,
                    subElements = allSubElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Modification Methods

        /// <summary>
        /// Change roof type.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - newTypeId: ID of the new roof type
        /// </summary>
        [MCPMethod("modifyRoofType", Category = "Roof", Description = "Change the type of an existing roof")]
        public static string ModifyRoofType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["newTypeId"] == null)
                    return ResponseBuilder.Error("newTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var newTypeId = new ElementId(int.Parse(parameters["newTypeId"].ToString()));
                var newType = doc.GetElement(newTypeId) as RoofType;
                if (newType == null)
                    return ResponseBuilder.Error("Roof type not found", "NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Modify Roof Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    roof.ChangeTypeId(newTypeId);

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        newTypeName = newType.Name,
                        message = "Roof type changed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("deleteRoof", Category = "Roof", Description = "Delete a roof element from the document")]
        public static string DeleteRoof(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                string typeName = roof.Name;

                using (var trans = new Transaction(doc, "Delete Roof"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(roofId);

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedRoofId = (int)roofId.Value,
                        typeName = typeName,
                        message = "Roof deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set roof offset from level.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - offset: offset value in feet
        /// </summary>
        [MCPMethod("setRoofOffset", Category = "Roof", Description = "Set the roof offset from its associated level")]
        public static string SetRoofOffset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["offset"] == null)
                    return ResponseBuilder.Error("offset is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                double offset = double.Parse(parameters["offset"].ToString());

                using (var trans = new Transaction(doc, "Set Roof Offset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var offsetParam = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                    if (offsetParam != null && !offsetParam.IsReadOnly)
                    {
                        offsetParam.Set(offset);
                    }
                    else
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Cannot set offset on this roof type", "INVALID_OPERATION").Build();
                    }

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        offset = offset,
                        message = "Roof offset set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set slope for a specific edge of a footprint roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - edgeIndex: index of the edge (0-based)
        /// - slopeAngle: slope angle in degrees (e.g. 30)
        /// - definesSlope: (optional) whether this edge defines slope (default true)
        /// </summary>
        [MCPMethod("setRoofSlopeByEdge", Category = "Roof", Description = "Set the slope angle for a specific edge of a footprint roof")]
        public static string SetRoofSlopeByEdge(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["edgeIndex"] == null)
                    return ResponseBuilder.Error("edgeIndex is required", "MISSING_PARAM").Build();
                if (parameters["slopeAngle"] == null)
                    return ResponseBuilder.Error("slopeAngle is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as FootPrintRoof;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found or is not a footprint roof", "NOT_FOUND").Build();

                int edgeIndex = int.Parse(parameters["edgeIndex"].ToString());
                double slopeAngle = double.Parse(parameters["slopeAngle"].ToString());
                bool definesSlope = parameters["definesSlope"]?.ToObject<bool>() ?? true;

                using (var trans = new Transaction(doc, "Set Roof Slope By Edge"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var profiles = roof.GetProfiles();
                    int currentIndex = 0;
                    bool found = false;

                    foreach (ModelCurveArray profile in profiles)
                    {
                        foreach (ModelCurve mc in profile)
                        {
                            if (currentIndex == edgeIndex)
                            {
                                roof.set_DefinesSlope(mc, definesSlope);
                                if (definesSlope)
                                {
                                    // slopeAngle is degrees; set_SlopeAngle takes a
                                    // rise/run ratio despite its name (see
                                    // createRoof/modifyRoofSlope)
                                    roof.set_SlopeAngle(mc, Math.Tan(slopeAngle * Math.PI / 180.0));
                                }
                                found = true;
                                break;
                            }
                            currentIndex++;
                        }
                        if (found) break;
                    }

                    if (!found)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Edge index {edgeIndex} not found. Roof has {currentIndex} edges.", "INVALID_INDEX").Build();
                    }

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        edgeIndex = edgeIndex,
                        slopeAngle = slopeAngle,
                        definesSlope = definesSlope,
                        message = "Roof edge slope set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create fascia on roof edge.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - fasciaTypeId: ID of the fascia type
        /// - edgeIndex: (optional) index of edge to place fascia on, defaults to all edges
        /// </summary>
        [MCPMethod("createFascia", Category = "Roof", Description = "Create fascia on roof edges")]
        public static string CreateFascia(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["fasciaTypeId"] == null)
                    return ResponseBuilder.Error("fasciaTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var fasciaTypeId = new ElementId(int.Parse(parameters["fasciaTypeId"].ToString()));
                var fasciaType = doc.GetElement(fasciaTypeId);
                if (fasciaType == null)
                    return ResponseBuilder.Error("Fascia type not found", "NOT_FOUND").Build();

                int? targetEdgeIndex = parameters["edgeIndex"]?.ToObject<int>();

                using (var trans = new Transaction(doc, "Create Fascia"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var createdIds = new List<int>();

                    // Get roof edge references from geometry
                    var options = new Options();
                    options.ComputeReferences = true;
                    var geom = roof.get_Geometry(options);

                    var edgeRefs = new List<Reference>();
                    if (geom != null)
                    {
                        foreach (GeometryObject gObj in geom)
                        {
                            if (gObj is Solid solid)
                            {
                                foreach (Edge edge in solid.Edges)
                                {
                                    var edgeRef = edge.Reference;
                                    if (edgeRef != null)
                                        edgeRefs.Add(edgeRef);
                                }
                            }
                        }
                    }

                    if (edgeRefs.Count == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("No roof edges found for fascia placement", "NO_EDGES").Build();
                    }

                    // Place fascia on specified edge or first edge
                    int edgeIdx = targetEdgeIndex ?? 0;
                    if (edgeIdx >= edgeRefs.Count)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Edge index {edgeIdx} out of range. Roof has {edgeRefs.Count} edges.", "INVALID_INDEX").Build();
                    }

                    var referenceArray = new ReferenceArray();
                    referenceArray.Append(edgeRefs[edgeIdx]);

                    var fascia = doc.Create.NewFascia(fasciaType as FasciaType, referenceArray);
                    if (fascia != null)
                        createdIds.Add((int)fascia.Id.Value);

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        createdFasciaIds = createdIds,
                        message = $"Fascia created on {createdIds.Count} edge(s)"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create gutter on roof edge.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - gutterTypeId: ID of the gutter type
        /// - edgeIndex: (optional) index of edge, defaults to 0
        /// </summary>
        [MCPMethod("createGutter", Category = "Roof", Description = "Create gutter on roof edges")]
        public static string CreateGutter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["gutterTypeId"] == null)
                    return ResponseBuilder.Error("gutterTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var gutterTypeId = new ElementId(int.Parse(parameters["gutterTypeId"].ToString()));
                var gutterType = doc.GetElement(gutterTypeId);
                if (gutterType == null)
                    return ResponseBuilder.Error("Gutter type not found", "NOT_FOUND").Build();

                int? targetEdgeIndex = parameters["edgeIndex"]?.ToObject<int>();

                using (var trans = new Transaction(doc, "Create Gutter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get roof edge references
                    var options = new Options();
                    options.ComputeReferences = true;
                    var geom = roof.get_Geometry(options);

                    var edgeRefs = new List<Reference>();
                    if (geom != null)
                    {
                        foreach (GeometryObject gObj in geom)
                        {
                            if (gObj is Solid solid)
                            {
                                foreach (Edge edge in solid.Edges)
                                {
                                    var edgeRef = edge.Reference;
                                    if (edgeRef != null)
                                        edgeRefs.Add(edgeRef);
                                }
                            }
                        }
                    }

                    if (edgeRefs.Count == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("No roof edges found for gutter placement", "NO_EDGES").Build();
                    }

                    int edgeIdx = targetEdgeIndex ?? 0;
                    if (edgeIdx >= edgeRefs.Count)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Edge index {edgeIdx} out of range. Roof has {edgeRefs.Count} edges.", "INVALID_INDEX").Build();
                    }

                    var referenceArray = new ReferenceArray();
                    referenceArray.Append(edgeRefs[edgeIdx]);

                    var gutter = doc.Create.NewGutter(gutterType as GutterType, referenceArray);

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        gutterId = gutter != null ? (int)gutter.Id.Value : -1,
                        message = "Gutter created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create soffit under roof overhang.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - soffitTypeId: ID of the soffit type
        /// </summary>
        [MCPMethod("createRoofSoffit", Category = "Roof", Description = "Create soffit under roof overhang")]
        public static string CreateRoofSoffit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["soffitTypeId"] == null)
                    return ResponseBuilder.Error("soffitTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var soffitTypeId = new ElementId(int.Parse(parameters["soffitTypeId"].ToString()));
                var soffitType = doc.GetElement(soffitTypeId);
                if (soffitType == null)
                    return ResponseBuilder.Error("Soffit type not found", "NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Create Roof Soffit"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get bottom face references for soffit
                    var options = new Options();
                    options.ComputeReferences = true;
                    var geom = roof.get_Geometry(options);

                    var bottomFaceRefs = new List<Reference>();
                    if (geom != null)
                    {
                        foreach (GeometryObject gObj in geom)
                        {
                            if (gObj is Solid solid)
                            {
                                foreach (Face face in solid.Faces)
                                {
                                    // Check if face normal points down (bottom face)
                                    if (face is PlanarFace planarFace)
                                    {
                                        if (planarFace.FaceNormal.Z < -0.5)
                                        {
                                            var faceRef = face.Reference;
                                            if (faceRef != null)
                                                bottomFaceRefs.Add(faceRef);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (bottomFaceRefs.Count == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("No bottom faces found for soffit placement. Ensure roof has overhang.", "NO_FACES").Build();
                    }

                    // Create soffit on first bottom face reference
                    var referenceArray = new ReferenceArray();
                    referenceArray.Append(bottomFaceRefs[0]);

                    // Use the first bottom face edge references for soffit boundary
                    var edgeRefs = new ReferenceArray();
                    var face0 = roof.GetGeometryObjectFromReference(bottomFaceRefs[0]) as Face;
                    if (face0 != null)
                    {
                        var edgeLoops = face0.EdgeLoops;
                        if (edgeLoops.Size > 0)
                        {
                            foreach (Edge edge in edgeLoops.get_Item(0))
                            {
                                edgeRefs.Append(edge.Reference);
                            }
                        }
                    }

                    // Note: Soffit creation in Revit API requires specific face/edge setup
                    // The doc.Create.NewSlab method or manual floor creation at soffit elevation
                    // is often the practical approach for soffits
                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        bottomFacesFound = bottomFaceRefs.Count,
                        message = "Roof soffit geometry identified. Use floor/ceiling creation at soffit elevation for physical soffit."
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a roof type with a new name.
        /// Parameters:
        /// - sourceTypeId: ID of the source roof type to duplicate
        /// - newName: name for the duplicated type
        /// </summary>
        [MCPMethod("duplicateRoofType", Category = "Roof", Description = "Duplicate an existing roof type with a new name")]
        public static string DuplicateRoofType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceTypeId"] == null)
                    return ResponseBuilder.Error("sourceTypeId is required", "MISSING_PARAM").Build();
                if (parameters["newName"] == null)
                    return ResponseBuilder.Error("newName is required", "MISSING_PARAM").Build();

                var sourceTypeId = new ElementId(int.Parse(parameters["sourceTypeId"].ToString()));
                var sourceType = doc.GetElement(sourceTypeId) as RoofType;
                if (sourceType == null)
                    return ResponseBuilder.Error("Source roof type not found", "NOT_FOUND").Build();

                string newName = parameters["newName"].ToString();

                // Check if name already exists
                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofType))
                    .Cast<RoofType>()
                    .FirstOrDefault(rt => rt.Name == newName);

                if (existing != null)
                    return ResponseBuilder.Error($"A roof type named '{newName}' already exists", "DUPLICATE_NAME").Build();

                using (var trans = new Transaction(doc, "Duplicate Roof Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var newType = sourceType.Duplicate(newName) as RoofType;

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sourceTypeId = (int)sourceType.Id.Value,
                        sourceTypeName = sourceType.Name,
                        newTypeId = (int)newType.Id.Value,
                        newTypeName = newType.Name,
                        message = "Roof type duplicated successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
