using System;
using System.Collections.Generic;
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
    /// Builds CLEAN wood roof trusses as assemblies of real structural-framing beams along a
    /// bearing line. Members lie flat in the truss plane; the web pattern shares work points with
    /// no member collinear with a chord.
    ///
    /// END CONDITIONS:
    ///   * CHORDS keep their auto-join, so they miter cleanly at the ridge and heels.
    ///   * WEB ends have the framing auto-join DISALLOWED (StructuralFramingUtils.DisallowJoinAtEnd)
    ///     so they are square-cut with no flailing auto-extension, and each end is drawn to the
    ///     chord's NEAR FACE so the web butts flush against the chord — a clean truss joint.
    ///
    /// NOTE: solid-cut coping (SolidSolidCutUtils.AddCutBetweenSolids) and FamilyInstance.AddCoping
    /// are NOT usable here — wood loadable Structural Framing fails IsAllowedForSolidCut (confirmed
    /// live: "must be ... a GenericForm, GeomCombination, or a FamilyInstance"), so we butt to the
    /// face instead. A square web end vs. a sloped chord can leave a tiny triangular gap on steep
    /// diagonals — standard for most real truss models.
    ///
    /// Supports gable / flat / mono profiles.
    /// </summary>
    public static class TrussBeamMethods
    {
        private static string Err(string m) => JsonConvert.SerializeObject(new { success = false, error = m });

        [MCPMethod("createWoodTruss", Category = "Structural",
            Description = "Builds a CLEAN truss from real structural-framing beams along a bearing line. Chords auto-miter at peak/heels; web ends are DisallowJoinAtEnd square-cut and butted flush to each chord's near face (no solid cut — wood framing is ineligible for SolidSolidCutUtils/AddCoping). Params: startPoint{x,y}, endPoint{x,y}, height (ft), chordTypeId (int), webTypeId (int), levelId (int); optional bearingOffset (ft, default 0), shape ('gable'|'flat'|'mono', default gable), panels (even int default 8), verticalRotationDeg. Returns memberIds.")]
        public static string CreateWoodTruss(UIApplication uiApp, JObject p)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (p["startPoint"] == null || p["endPoint"] == null) return Err("startPoint and endPoint are required");
                if (p["height"] == null) return Err("height is required");
                if (p["chordTypeId"] == null || p["webTypeId"] == null) return Err("chordTypeId and webTypeId are required");
                if (p["levelId"] == null) return Err("levelId is required");

                var level = doc.GetElement(new ElementId(p["levelId"].ToObject<int>())) as Level;
                if (level == null) return Err("Level not found");
                var chordSym = doc.GetElement(new ElementId(p["chordTypeId"].ToObject<int>())) as FamilySymbol;
                var webSym = doc.GetElement(new ElementId(p["webTypeId"].ToObject<int>())) as FamilySymbol;
                if (chordSym == null || webSym == null) return Err("chordTypeId or webTypeId is not a structural-framing type");

                double H = p["height"].ToObject<double>();
                double bz = level.Elevation + (p["bearingOffset"]?.ToObject<double>() ?? 0.0);
                int n = Math.Max(2, p["panels"]?.ToObject<int>() ?? 8);
                if (n % 2 != 0) n++;
                string shape = (p["shape"]?.ToObject<string>() ?? "gable").Trim().ToLowerInvariant();
                double? vertOverride = p["verticalRotationDeg"] != null ? (double?)(p["verticalRotationDeg"].ToObject<double>() * Math.PI / 180.0) : null;

                XYZ A = new XYZ(p["startPoint"]["x"].ToObject<double>(), p["startPoint"]["y"].ToObject<double>(), bz);
                XYZ B = new XYZ(p["endPoint"]["x"].ToObject<double>(), p["endPoint"]["y"].ToObject<double>(), bz);
                double tol = doc.Application.ShortCurveTolerance;
                if (A.DistanceTo(B) < tol) return Err("bearing line too short");

                XYZ u = new XYZ((B - A).X, (B - A).Y, 0);
                u = u.GetLength() < 1e-9 ? XYZ.BasisY : u.Normalize();
                XYZ Nrm = new XYZ(-u.Y, u.X, 0).Normalize();

                Func<double, double> hRel = f =>
                    shape == "flat" ? H : (shape == "mono" ? H * f : (f <= 0.5 ? 2.0 * H * f : 2.0 * H * (1.0 - f)));
                Func<double, XYZ> Bot = f => A + (B - A) * f;
                Func<double, XYZ> Top = f => Bot(f) + new XYZ(0, 0, hRel(f));

                // cross-section rotation so each member's strong axis lies in the truss plane
                Func<XYZ, XYZ, double> RotFor = (a, b) =>
                {
                    XYZ d = (b - a).Normalize();
                    bool vertical = Math.Abs(d.Z) > 0.95;
                    if (vertical && vertOverride.HasValue) return vertOverride.Value;
                    XYZ S = d.CrossProduct(Nrm);
                    if (S.GetLength() < 1e-6) return 0.0;
                    S = S.Normalize(); if (S.Z < 0) S = S.Negate();
                    XYZ defUp = XYZ.BasisZ - d * XYZ.BasisZ.DotProduct(d);
                    if (defUp.GetLength() < 1e-6)
                    {
                        defUp = XYZ.BasisX - d * XYZ.BasisX.DotProduct(d);
                        if (defUp.GetLength() < 1e-6) defUp = XYZ.BasisY;
                    }
                    defUp = defUp.Normalize();
                    double dot = defUp.DotProduct(S);
                    double det = d.DotProduct(defUp.CrossProduct(S));
                    return Math.Atan2(det, dot);
                };

                var memberIds = new List<long>();
                ElementId botId = null, topLId = null, topRId = null, topFlatId = null, topMonoId = null;

                using (var t = new Transaction(doc, "Create Wood Truss"))
                {
                    t.Start();
                    var fo = t.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(fo);
                    if (!chordSym.IsActive) chordSym.Activate();
                    if (!webSym.IsActive) webSym.Activate();
                    doc.Regenerate();

                    // disallow == true → kill the framing auto-extension so the web sits at its work
                    //   point (penetrating the chord by ~half its depth) ready to be coped (webs).
                    // disallow == false → keep auto-join so the member miters to its neighbours (chords).
                    Func<XYZ, XYZ, FamilySymbol, bool, ElementId> beam = (a, b, sym, disallow) =>
                    {
                        if (a.DistanceTo(b) < tol) return null;
                        var fi = doc.Create.NewFamilyInstance(Line.CreateBound(a, b), sym, level, StructuralType.Beam);
                        if (fi == null) return null;
                        memberIds.Add(fi.Id.Value);
                        var rp = fi.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE);
                        if (rp == null || rp.IsReadOnly) rp = fi.LookupParameter("Cross-Section Rotation");
                        if (rp != null && !rp.IsReadOnly) { try { rp.Set(RotFor(a, b)); } catch { } }
                        if (disallow)
                        {
                            try { StructuralFramingUtils.DisallowJoinAtEnd(fi, 0); } catch { }
                            try { StructuralFramingUtils.DisallowJoinAtEnd(fi, 1); } catch { }
                            try { fi.get_Parameter(BuiltInParameter.START_EXTENSION)?.Set(0.0); } catch { }
                            try { fi.get_Parameter(BuiltInParameter.END_EXTENSION)?.Set(0.0); } catch { }
                        }
                        return fi.Id;
                    };

                    // chord half-depth → how far to push web ends INTO the chord so the solids
                    // overlap (a solid cut only removes material where solids physically intersect).
                    // Read the chord section depth; default to a 2x6 (5.5") if the param is absent.
                    double chordHalfDepth = 0.4583 / 2.0; // half a 2x6 (5.5") default
                    foreach (var pn in new[] { "d", "Depth", "Height", "h" })
                    {
                        var dp = chordSym.LookupParameter(pn);
                        if (dp != null && dp.StorageType == StorageType.Double && dp.AsDouble() > 0.01)
                        { chordHalfDepth = dp.AsDouble() / 2.0; break; }
                    }

                    // Face normal: unit vector perpendicular to a chord's axis, in the truss plane,
                    // pointing toward `toward` (the side the web approaches from).
                    Func<XYZ, XYZ, XYZ> faceNormal = (chordAxis, toward) =>
                    {
                        XYZ perp = XYZ.BasisZ - chordAxis.Multiply(XYZ.BasisZ.DotProduct(chordAxis));
                        if (perp.GetLength() < 1e-6) perp = XYZ.BasisZ;
                        perp = perp.Normalize();
                        if (perp.DotProduct(toward) < 0) perp = perp.Negate();
                        return perp;
                    };

                    // Build a web that BUTTS square against each chord's near FACE — no solid cut
                    // (wood Structural Framing is ineligible for SolidSolidCutUtils/AddCoping). Each
                    // end is slid along the web axis to where it crosses the chord's near-face plane,
                    // then DisallowJoinAtEnd (in beam()) holds it square there. botAxis = bearing line;
                    // topAxis = the top chord this web lands on.
                    Action<XYZ, XYZ, XYZ> mkWeb = (botPt, topPt, topAxis) =>
                    {
                        XYZ axis = topPt - botPt;
                        if (axis.GetLength() < tol) return;
                        axis = axis.Normalize();
                        XYZ nb = faceNormal(u, axis);                 // bottom chord; web heads up (+axis)
                        XYZ nt = faceNormal(topAxis, axis.Negate());  // top chord; web heads down (-axis)
                        double db = axis.DotProduct(nb); if (db < 0.2) db = 0.2;
                        double dt = axis.Negate().DotProduct(nt); if (dt < 0.2) dt = 0.2;
                        XYZ nbPt = botPt + (chordHalfDepth / db) * axis;  // up to bottom chord's top face
                        XYZ ntPt = topPt - (chordHalfDepth / dt) * axis;  // down to top chord's under face
                        if (nbPt.DistanceTo(ntPt) < tol) return;
                        beam(nbPt, ntPt, webSym, true);
                    };

                    // chords (auto-join → miter at ridge/heels)
                    XYZ ctMono = XYZ.BasisZ;
                    botId = beam(A, B, chordSym, false);
                    if (shape == "gable")
                    {
                        topLId = beam(A, Top(0.5), chordSym, false);
                        topRId = beam(Top(0.5), B, chordSym, false);
                    }
                    else if (shape == "flat")
                    {
                        topFlatId = beam(Top(0.0), Top(1.0), chordSym, false);
                        mkWeb(A, Top(0.0), u); // end posts (flat top chord is horizontal)
                        mkWeb(B, Top(1.0), u);
                    }
                    else // mono
                    {
                        topMonoId = beam(A, Top(1.0), chordSym, false);
                        ctMono = (Top(1.0) - A).Normalize();
                        mkWeb(B, Top(1.0), ctMono); // vertical end post
                    }

                    // top chord AXIS covering fraction f
                    XYZ ctL = shape == "gable" ? (Top(0.5) - A).Normalize() : u;
                    XYZ ctR = shape == "gable" ? (B - Top(0.5)).Normalize() : u;
                    Func<double, XYZ> topAxisFor = f =>
                        shape == "flat" ? u : (shape == "mono" ? ctMono : (f <= 0.5 ? ctL : ctR));

                    // webs: interior verticals + zig diagonals, butted square to the chord faces
                    for (int i = 1; i <= n - 1; i++)
                    {
                        double f = (double)i / n;
                        mkWeb(Bot(f), Top(f), topAxisFor(f));
                    }
                    for (int i = 1; i <= n - 2; i++)
                    {
                        double f = (double)i / n;
                        mkWeb(Bot((double)(i + 1) / n), Top(f), topAxisFor(f));
                    }

                    t.CommitAndCheck();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    memberCount = memberIds.Count,
                    jointMethod = "face-butt",
                    bearingElevation = bz,
                    shape,
                    memberIds
                });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }
    }
}
