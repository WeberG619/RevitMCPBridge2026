using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// PROPER molding workflow: create a profile FAMILY from a cross-section, then run it as a native
    /// Wall Sweep (projecting molding: cornice/base/belt) or Wall Reveal (recessed groove) on a real
    /// wall — real Revit elements Revit accounts for, not DirectShape masses.
    /// </summary>
    public static class WallSweepMethods
    {
        private class ProfileLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }

        private static string FindProfileTemplate(UIApplication uiApp, bool reveal)
        {
            string want = reveal ? "Profile-Reveal.rft" : "Profile.rft";
            var roots = new List<string>();
            try { roots.Add(uiApp.Application.FamilyTemplatePath); } catch { }
            roots.Add(@"C:\ProgramData\Autodesk\RVT 2026\Family Templates");
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
            {
                var hit = Directory.GetFiles(root, want, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
                // fall back to plain Profile.rft if the reveal-specific one isn't there
                hit = Directory.GetFiles(root, "Profile.rft", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        private static SketchPlane FamilySketchPlane(Document fdoc)
        {
            var sp = new FilteredElementCollector(fdoc).OfClass(typeof(SketchPlane)).Cast<SketchPlane>().FirstOrDefault();
            if (sp != null) return sp;
            foreach (View vv in new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>())
                if (vv.SketchPlane != null) return vv.SketchPlane;
            return null;
        }

        /// <summary>Create a PROFILE family from a [[u,v],...] closed cross-section and load it. u = projection
        /// out from the wall, v = vertical. Returns the profile family symbol id (use as profileId for a sweep).</summary>
        [MCPMethod("createProfileFamily", Category = "Detail", Description = "Create + load a molding profile family from a [[u,v],...] cross-section (for wall sweeps/reveals)")]
        public static string CreateProfileFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["name"] == null || parameters["points"] == null)
                    return ResponseBuilder.Error("name and points [[u,v],...] required", "VALIDATION_ERROR").Build();
                string name = parameters["name"].ToString();
                var pts = parameters["points"].ToObject<double[][]>();
                if (pts.Length < 3) return ResponseBuilder.Error("profile needs >= 3 points", "VALIDATION_ERROR").Build();

                string template = FindProfileTemplate(uiApp, false);
                if (template == null) return ResponseBuilder.Error("Profile.rft template not found", "ERROR").Build();

                Document fdoc = app.NewFamilyDocument(template);
                string tempPath = Path.Combine(Path.GetTempPath(), name + ".rfa");
                Family fam = null;
                try
                {
                    using (var t = new Transaction(fdoc, "Draw Profile"))
                    {
                        t.Start();
                        // profile is drawn in the XY plane; create that sketch plane explicitly
                        SketchPlane sp;
                        try { sp = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero)); }
                        catch { sp = FamilySketchPlane(fdoc); }
                        if (sp == null) { t.RollBack(); return ResponseBuilder.Error("no work plane in profile family", "ERROR").Build(); }
                        Plane pl = sp.GetPlane(); XYZ O = pl.Origin, U = pl.XVec, V = pl.YVec;
                        string diag = null;
                        for (int i = 0; i < pts.Length; i++)
                        {
                            XYZ a = O + pts[i][0] * U + pts[i][1] * V;
                            XYZ b = O + pts[(i + 1) % pts.Length][0] * U + pts[(i + 1) % pts.Length][1] * V;
                            if (a.DistanceTo(b) <= app.ShortCurveTolerance) continue;
                            var ln = Line.CreateBound(a, b);
                            try { fdoc.FamilyCreate.NewModelCurve(ln, sp); }
                            catch (Exception e1)
                            {
                                try { fdoc.FamilyCreate.NewSymbolicCurve(ln, sp); }
                                catch (Exception e2) { diag = "model:" + e1.Message + " | symbolic:" + e2.Message; }
                            }
                        }
                        if (diag != null) { t.RollBack(); return ResponseBuilder.Error("profile curves failed", "ERROR").With("detail", diag).Build(); }
                        t.CommitAndCheck();
                    }
                    fdoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                    fdoc.Close(false);
                    using (var t = new Transaction(doc, "Load Profile Family"))
                    {
                        t.Start();
                        doc.LoadFamily(tempPath, new ProfileLoadOptions(), out fam);
                        t.CommitAndCheck();
                    }
                }
                finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }

                if (fam == null) return ResponseBuilder.Error("profile family failed to load", "ERROR").Build();
                var symId = fam.GetFamilySymbolIds().FirstOrDefault();
                return ResponseBuilder.Success()
                    .With("familyId", fam.Id.Value)
                    .With("profileId", symId != null ? symId.Value : -1)
                    .With("name", fam.Name)
                    .With("note", "profile family loaded — pass profileId to createWallSweep")
                    .Build();
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        /// <summary>Apply a native Wall Sweep (projecting molding) or Wall Reveal (recess) to a wall using a
        /// loaded profile. params: wallId, profileId, heightFt (distance from base), reveal(bool), exterior(bool), vertical(bool).</summary>
        [MCPMethod("createWallSweep", Category = "Detail", Description = "Native Wall Sweep / Wall Reveal on a wall from a profile family (proper moldings)")]
        public static string CreateWallSweep(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["wallId"] == null || parameters["profileId"] == null)
                    return ResponseBuilder.Error("wallId and profileId required", "VALIDATION_ERROR").Build();
                var wall = doc.GetElement(new ElementId(parameters["wallId"].ToObject<long>())) as Wall;
                if (wall == null) return ResponseBuilder.Error("wall not found", "ELEMENT_NOT_FOUND").Build();
                var profileId = new ElementId(parameters["profileId"].ToObject<long>());
                if (!(doc.GetElement(profileId) is FamilySymbol)) return ResponseBuilder.Error("profileId is not a profile symbol", "VALIDATION_ERROR").Build();

                bool reveal = parameters["reveal"]?.ToObject<bool>() ?? false;
                bool vertical = parameters["vertical"]?.ToObject<bool>() ?? false;
                bool exterior = parameters["exterior"]?.ToObject<bool>() ?? true;
                double heightFt = parameters["heightFt"]?.ToObject<double>() ?? 0.0;

                var info = new WallSweepInfo(reveal ? WallSweepType.Reveal : WallSweepType.Sweep, vertical);
                info.ProfileId = profileId;
                info.WallSide = exterior ? WallSide.Exterior : WallSide.Interior;
                info.Distance = heightFt;
                try { info.DistanceMeasuredFrom = DistanceMeasuredFrom.Base; } catch { }
                if (parameters["materialId"] != null)
                    try { info.MaterialId = new ElementId(parameters["materialId"].ToObject<long>()); } catch { }

                using (var trans = new Transaction(doc, reveal ? "Wall Reveal" : "Wall Sweep"))
                {
                    trans.Start();
                    var ws = WallSweep.Create(wall, profileId, info);
                    trans.CommitAndCheck();
                    return ResponseBuilder.Success()
                        .With("wallSweepId", ws.Id.Value)
                        .With("kind", reveal ? "reveal" : "sweep")
                        .With("wallId", wall.Id.Value)
                        .With("heightFt", heightFt)
                        .Build();
                }
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }
    }
}
