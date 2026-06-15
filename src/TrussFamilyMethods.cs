using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// Authors NATIVE Structural Truss families (.rfa) programmatically from the
    /// "Structural Trusses.rft" template, then loads them as real TrussTypes — so
    /// createTruss has genuine truss geometry to instantiate without any cloud download.
    ///
    /// Truss members are classified GEOMETRICALLY by Revit on placement (horizontal bottom =
    /// bottom chord, sloped/flat top = top chord, interior verticals/diagonals = webs). We also
    /// tag each model curve with the matching truss SUBCATEGORY (Top Chord / Bottom Chord / Web).
    /// The truss family template BLOCKS SketchPlane.Create, so we reuse an existing work plane
    /// and map the 2D truss profile (u = span, v = height) into it.
    /// </summary>
    public static class TrussFamilyMethods
    {
        private static string Err(string m) => JsonConvert.SerializeObject(new { success = false, error = m });

        private static GraphicsStyle TrussStyle(Document fdoc, string subNameContains)
        {
            try
            {
                var cat = fdoc.OwnerFamily?.FamilyCategory;
                if (cat == null) return null;
                foreach (Category c in cat.SubCategories)
                {
                    if (c.Name != null && c.Name.IndexOf(subNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return c.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
            }
            catch { }
            return null;
        }

        [MCPMethod("createTrussFamily", Category = "Structural",
            Description = "Authors a NATIVE Structural Truss family from the Structural Trusses template and loads it as a TrussType. Params: span (ft), height (ft); optional shape ('gable'|'flat'|'mono', default gable), panels (int default 6), familyName, savePath, templatePath. Returns savedPath + trussTypeIds (use with createTruss).")]
        public static string CreateTrussFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;
                var projectDoc = uiApp.ActiveUIDocument.Document;

                string template = parameters["templatePath"]?.ToObject<string>()
                    ?? @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\English-Imperial\Structural Trusses.rft";
                if (!System.IO.File.Exists(template))
                    return Err("Truss template not found: " + template);

                double L = parameters["span"]?.ToObject<double>() ?? 24.0;
                double H = parameters["height"]?.ToObject<double>() ?? 6.0;
                int panels = Math.Max(2, parameters["panels"]?.ToObject<int>() ?? 6);
                // pitchSlope (rise/run) drives the 'hip' (trapezoid step-down) side slopes so they
                // lie IN the roof side-planes. Default 5:12. Ignored by gable/flat/mono.
                double pitchSlope = parameters["pitchSlope"]?.ToObject<double>() ?? (5.0 / 12.0);
                string shape = (parameters["shape"]?.ToObject<string>() ?? "gable").Trim().ToLowerInvariant();
                string famName = parameters["familyName"]?.ToObject<string>()
                    ?? ($"GenTruss_{shape}_{(int)Math.Round(L)}x{H:0.##}").Replace(".", "_");

                string saveDir = @"D:\_CLAUDE-TOOLS\generated_trusses";
                System.IO.Directory.CreateDirectory(saveDir);
                string savePath = parameters["savePath"]?.ToObject<string>()
                    ?? System.IO.Path.Combine(saveDir, famName + ".rfa");

                if (L < app.ShortCurveTolerance || H <= 0) return Err("span/height too small");

                Func<double, double> topZ = x =>
                {
                    if (shape == "flat") return H;
                    if (shape == "mono") return H * (x / L);
                    // hip = trapezoid step-down: slope up at pitchSlope from each eave, clip flat at H.
                    if (shape == "hip") return Math.Min(Math.Min(pitchSlope * x, pitchSlope * (L - x)), H);
                    return x <= L / 2.0 ? (2.0 * H * x / L) : (2.0 * H * (L - x) / L); // gable
                };

                Document fdoc = app.NewFamilyDocument(template);
                int lineCount = 0;
                string planeInfo = "";
                try
                {
                    using (var t = new Transaction(fdoc, "Build Truss Layout"))
                    {
                        t.Start();

                        // Reuse an existing work plane (creation is blocked in truss families).
                        SketchPlane sp = new FilteredElementCollector(fdoc)
                            .OfClass(typeof(SketchPlane)).Cast<SketchPlane>().FirstOrDefault();
                        if (sp == null)
                        {
                            foreach (View vv in new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>())
                            {
                                if (vv.SketchPlane != null) { sp = vv.SketchPlane; break; }
                            }
                        }
                        if (sp == null)
                        {
                            int spc = new FilteredElementCollector(fdoc).OfClass(typeof(SketchPlane)).GetElementCount();
                            int rpc = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).GetElementCount();
                            return Err($"No usable work plane in truss family (sketchPlanes={spc}, refPlanes={rpc}); SketchPlane.Create is blocked.");
                        }

                        Plane pl = sp.GetPlane();
                        XYZ O = pl.Origin, U = pl.XVec, V = pl.YVec;

                        // The bottom chord MUST sit on the truss template's "Bottom" (bearing) reference
                        // plane, or the placed truss floats above the wall (proven 6/14: a free-drawn
                        // bottom chord at the family origin landed ~4' above the bearing line, and the
                        // unconstrained layout distorted on any Truss Height edit). Shift the whole
                        // profile so v=0 maps onto the Bottom ref plane.
                        double vBottom = 0.0;
                        ReferencePlane rpBottom = new FilteredElementCollector(fdoc)
                            .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                            .FirstOrDefault(r => string.Equals(r.Name, "Bottom", StringComparison.OrdinalIgnoreCase));
                        if (rpBottom != null) vBottom = (rpBottom.BubbleEnd - O).DotProduct(V);
                        planeInfo = $"O={O} N={pl.Normal} vBottom={vBottom:0.###} bearingRefFound={rpBottom != null}";
                        Func<double, double, XYZ> P = (u, v) => O + (u * U) + ((v + vBottom) * V);

                        var styBottom = TrussStyle(fdoc, "Bottom Chord");
                        var styTop = TrussStyle(fdoc, "Top Chord");
                        var styWeb = TrussStyle(fdoc, "Web");

                        // THE critical step (was missing -> 0 members): each model curve must be
                        // classified with its ModelCurve.TrussCurveType so Truss.Create generates a
                        // structural member along it. A LineStyle/subcategory alone does NOT classify
                        // it. Per Revit SDK CreateTruss.cs, set TrussCurveType then Regenerate.
                        Action<double, double, double, double, GraphicsStyle, TrussCurveType> add =
                            (u1, v1, u2, v2, sty, tct) =>
                        {
                            XYZ a = P(u1, v1), b = P(u2, v2);
                            if (a.DistanceTo(b) < app.ShortCurveTolerance) return;
                            var mc = fdoc.FamilyCreate.NewModelCurve(Line.CreateBound(a, b), sp);
                            try { mc.TrussCurveType = tct; } catch { }
                            if (sty != null) { try { mc.LineStyle = sty; } catch { } }
                            fdoc.Regenerate();   // SDK: regenerate after each tagged truss curve
                            lineCount++;
                        };

                        // bottom chord
                        add(0, 0, L, 0, styBottom, TrussCurveType.BottomChord);

                        // top chord
                        if (shape == "gable")
                        {
                            add(0, 0, L / 2, H, styTop, TrussCurveType.TopChord);
                            add(L / 2, H, L, 0, styTop, TrussCurveType.TopChord);
                        }
                        else if (shape == "flat")
                        {
                            // Top chord as TWO collinear HORIZONTAL segments meeting at a center
                            // vertex. A single full-span flat top chord has no apex/vertex to pin it,
                            // so Truss.Create's parametric flex compresses it (proven 6/14: chords
                            // truncated to ~11'). Splitting at center mimics the gable's stabilizing
                            // apex vertex while keeping the top HORIZONTAL (required for AttachChord).
                            add(0, H, L / 2, H, styTop, TrussCurveType.TopChord);
                            add(L / 2, H, L, H, styTop, TrussCurveType.TopChord);
                            // end verticals
                            add(0, 0, 0, H, styWeb, TrussCurveType.Web);
                            add(L, 0, L, H, styWeb, TrussCurveType.Web);
                        }
                        else if (shape == "hip")
                        {
                            // TRAPEZOID step-down truss: top slopes up from each eave at pitchSlope
                            // (so it lies IN the roof side-planes -> no poking), clipped FLAT at H
                            // (the hip-end-plane height at this truss's setback). H >= pitchSlope*L/2
                            // self-degenerates to a full gable common.
                            // RIGID construction (proven 6/14): drawing the flat as a few BIG segments
                            // with mid-span webs is a parallelogram MECHANISM -> the solver collapses
                            // the wide flat (stray diagonal, truncated top). Instead draw the top chord
                            // PANEL-BY-PANEL with a node at every panel vertex + a vertical + a diagonal
                            // each panel, so every flat vertex is pinned and triangulated. This branch
                            // emits ALL its own webs (the generic web loop below is skipped for hip).
                            double prevTop = 0.0;
                            for (int i = 1; i <= panels; i++)
                            {
                                double xi = L * i / panels;
                                double xp = L * (i - 1) / panels;
                                double ti = Math.Min(Math.Min(pitchSlope * xi, pitchSlope * (L - xi)), H);
                                add(xp, prevTop, xi, ti, styTop, TrussCurveType.TopChord); // top chord segment
                                add(xi, 0, xi, ti, styWeb, TrussCurveType.Web);            // vertical at xi
                                add(xp, 0, xi, ti, styWeb, TrussCurveType.Web);            // diagonal -> triangulate
                                prevTop = ti;
                            }
                        }
                        else // mono
                        {
                            // Top chord as TWO collinear segments meeting at a CENTER VERTEX
                            // (mirrors the flat-top fix). A SINGLE full-span sloped top-chord line
                            // has no interior vertex to pin it, so Truss.Create's parametric solver
                            // flexes and generates 0 members above ~6' span (proven 6/14: mono span=6
                            // -> 13 members, span=7.667 & 8.0 -> 0 members; gable/flat unaffected
                            // because they already split at center). Splitting the sloped top chord at
                            // its midpoint (L/2, H/2) gives the solver the stabilizing vertex.
                            add(0, 0, L / 2.0, H / 2.0, styTop, TrussCurveType.TopChord);
                            add(L / 2.0, H / 2.0, L, H, styTop, TrussCurveType.TopChord);
                            add(L, 0, L, H, styWeb, TrussCurveType.Web);            // tall-end vertical
                            add(L / 2.0, 0, L / 2.0, H / 2.0, styWeb, TrussCurveType.Web); // center web to vertex
                        }

                        // webs: interior verticals + zig diagonals (hip emits its own webs above)
                        double prevBx = 0;
                        for (int i = 1; i < panels && shape != "hip"; i++)
                        {
                            double x = L * i / panels;
                            double tz = topZ(x);
                            add(x, 0, x, tz, styWeb, TrussCurveType.Web);
                            add(prevBx, 0, x, tz, styWeb, TrussCurveType.Web);
                            prevBx = x;
                        }

                        t.Commit();
                    }

                    fdoc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = true });
                }
                finally
                {
                    fdoc.Close(false);
                }

                var trussTypeIds = new List<long>();
                string loadedFamName = null;
                using (var t = new Transaction(projectDoc, "Load Truss Family"))
                {
                    t.Start();
                    Family fam;
                    projectDoc.LoadFamily(savePath, out fam);
                    if (fam != null)
                    {
                        loadedFamName = fam.Name;
                        foreach (ElementId sid in fam.GetFamilySymbolIds())
                            trussTypeIds.Add(sid.Value);
                    }
                    t.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    savedPath = savePath,
                    familyName = loadedFamName ?? famName,
                    shape,
                    span = L,
                    height = H,
                    lineCount,
                    planeInfo,
                    trussTypeIds
                });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }
    }
}
