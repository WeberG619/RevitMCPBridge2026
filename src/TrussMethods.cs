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
    /// MCP methods for structural roof TRUSSES.
    /// A Revit truss family is only a structural *diagram*; its members are project
    /// structural-framing (beam) types assigned per chord. These methods place a truss
    /// along a bearing line at a level, assign top/bottom/web beam types, size it, and
    /// duplicate types for hip step-downs / different sizes.
    ///
    /// Workflow for a hip-roof truss system:
    ///   1. Load a Structural Truss family (loadFamily) + wood beam types (already have them).
    ///   2. getTrussTypes -> pick a common-truss type.
    ///   3. createTrussType (optional) -> step-down / girder variants with set Truss Height.
    ///   4. createTruss along each bearing line (common field, hip girder, step-downs, jacks),
    ///      passing topChordTypeId / bottomChordTypeId / webTypeId (wood 2x members).
    ///   5. (next phase) attach top chord to roof + roof compound-structure sheeting layers.
    /// </summary>
    public static class TrussMethods
    {
        /// <summary>Captures Revit failure messages (so AttachChord's rollback reason is reported,
        /// not swallowed). Deletes warnings; lets errors proceed to rollback but records their text.</summary>
        private class AttachFailureCapture : IFailuresPreprocessor
        {
            public List<string> Messages = new List<string>();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages())
                {
                    try { Messages.Add(f.GetSeverity().ToString() + ": " + f.GetDescriptionText()); } catch { }
                    if (f.GetSeverity() == FailureSeverity.Warning) { try { fa.DeleteWarning(f); } catch { } }
                }
                return FailureProcessingResult.Continue;
            }
        }

        // ------------------------------------------------------------------ helpers
        private static string Err(string msg) =>
            JsonConvert.SerializeObject(new { success = false, error = msg });

        private static XYZ PtFromJson(JToken t, double z)
        {
            double x = t["x"].ToObject<double>();
            double y = t["y"].ToObject<double>();
            return new XYZ(x, y, z);
        }

        private static double? TryGetParamDouble(Element e, string name)
        {
            try
            {
                var p = e.LookupParameter(name);
                return (p != null && p.StorageType == StorageType.Double) ? (double?)p.AsDouble() : null;
            }
            catch { return null; }
        }

        /// <summary>Resolve which beam type a truss member should use, by its Structural Usage.</summary>
        private static int? PickMemberType(FamilyInstance fi, int? single, int? top, int? bottom, int? web)
        {
            if (!top.HasValue && !bottom.HasValue && !web.HasValue) return single;

            // PRIMARY signal: the truss member's Structural Usage INTEGER. Revit classifies a
            // generated member from its source curve's TrussCurveType -> chord = 11, web = 12.
            // The value STRING does NOT reliably contain "Top"/"Bottom"/"Web" (proven 6/14:
            // string-only routing put EVERY member on the web type / 2x4). So route by the
            // integer first; use the string only to split top vs bottom chord when present.
            int usage = -1;
            try { var up = fi.get_Parameter(BuiltInParameter.INSTANCE_STRUCT_USAGE_PARAM); if (up != null) usage = up.AsInteger(); } catch { }
            string role = "";
            try { var p = fi.LookupParameter("Structural Usage"); role = p?.AsValueString() ?? ""; } catch { }

            bool isWeb = usage == 12 || role.IndexOf("Web", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isWeb) return web ?? single;

            // chord (usage 11): prefer an explicit Top/Bottom string, else treat as a chord
            if (role.IndexOf("Bottom", StringComparison.OrdinalIgnoreCase) >= 0) return bottom ?? top ?? single;
            if (role.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0) return top ?? bottom ?? single;
            if (usage == 11 || role.IndexOf("Chord", StringComparison.OrdinalIgnoreCase) >= 0)
                return top ?? bottom ?? single;   // chord, top/bottom indistinguishable -> chord size

            return single ?? web;   // unknown
        }

        /// <summary>Assign a dimension-lumber FamilySymbol to one of the 4 truss framing-type ROLE
        /// params on the TrussType (per FRAMING_BUILD §2) so generated members are the right 2x size.</summary>
        private static void SetFramingTypeParam(Document doc, TrussType tt, BuiltInParameter bip, int? symbolId)
        {
            if (!symbolId.HasValue) return;
            var sym = doc.GetElement(new ElementId(symbolId.Value)) as FamilySymbol;
            if (sym == null) return;
            if (!sym.IsActive) sym.Activate();
            try
            {
                var p = tt.get_Parameter(bip);
                if (p != null && !p.IsReadOnly) p.Set(sym.Id);
            }
            catch { }
        }

        private static int ApplyMemberTypes(Document doc, Truss truss, int? single, int? top, int? bottom, int? web, List<long> memberIds)
        {
            int changed = 0;
            foreach (ElementId mid in truss.Members)
            {
                memberIds?.Add(mid.Value);
                var fi = doc.GetElement(mid) as FamilyInstance;
                if (fi == null) continue;
                int? want = PickMemberType(fi, single, top, bottom, web);
                if (!want.HasValue) continue;
                var sym = doc.GetElement(new ElementId(want.Value)) as FamilySymbol;
                if (sym == null) continue;
                if (!sym.IsActive) sym.Activate();
                try { fi.Symbol = sym; changed++; } catch { }
            }
            return changed;
        }

        // ------------------------------------------------------------------ getTrussTypes
        [MCPMethod("getTrussTypes", Category = "Structural",
            Description = "Lists all loaded truss family types (TrussType): id, name, family, trussHeight (ft). Load a Structural Truss family first if empty.")]
        public static string GetTrussTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                // NOTE: TrussType is an API type that is NOT in Revit's native object model,
                // so FilteredElementCollector.OfClass(typeof(TrussType)) throws. Collect all
                // element types (a native filter) and narrow to TrussType in memory instead.
                var types = new FilteredElementCollector(doc)
                    .WhereElementIsElementType()
                    .OfType<TrussType>()
                    .Select(tt => new
                    {
                        id = tt.Id.Value,
                        name = tt.Name,
                        family = tt.FamilyName ?? "",
                        trussHeight = TryGetParamDouble(tt, "Truss Height")
                    })
                    .OrderBy(t => t.family).ThenBy(t => t.name)
                    .ToList();
                return JsonConvert.SerializeObject(new { success = true, count = types.Count, trussTypes = types });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ getTrussInstances
        [MCPMethod("getTrussInstances", Category = "Structural",
            Description = "Lists all PLACED truss instances (category Structural Trusses): id, typeName, mark, memberCount. Use to enumerate trusses for editing/deleting (selectByCategory does NOT support trusses).")]
        public static string GetTrussInstances(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var trusses = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralTruss)
                    .WhereElementIsNotElementType()
                    .OfType<Truss>()
                    .Select(t => new
                    {
                        id = t.Id.Value,
                        typeName = (doc.GetElement(t.GetTypeId()) as ElementType)?.Name ?? "",
                        mark = t.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                        memberCount = t.Members?.Count ?? 0
                    })
                    .ToList();
                return JsonConvert.SerializeObject(new { success = true, count = trusses.Count, trusses });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ deleteAllTrusses
        [MCPMethod("deleteAllTrusses", Category = "Structural",
            Description = "Deletes ALL placed truss instances (category Structural Trusses) and their members in ONE transaction. Optional markPrefix (string) to delete only trusses whose Mark starts with it. Returns deletedCount. Use for a clean-slate roof-framing rebuild.")]
        public static string DeleteAllTrusses(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string markPrefix = parameters["markPrefix"]?.ToObject<string>();
                var trusses = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralTruss)
                    .WhereElementIsNotElementType()
                    .OfType<Truss>();
                var ids = (string.IsNullOrEmpty(markPrefix)
                        ? trusses
                        : trusses.Where(t => (t.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                                                .StartsWith(markPrefix, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => t.Id).ToList();
                int deleted = 0;
                if (ids.Count > 0)
                {
                    using (var trans = new Transaction(doc, "Delete All Trusses"))
                    {
                        trans.Start();
                        var fo = trans.GetFailureHandlingOptions();
                        fo.SetFailuresPreprocessor(new AttachFailureCapture());
                        trans.SetFailureHandlingOptions(fo);
                        var result = doc.Delete(ids);
                        deleted = result?.Count ?? 0;
                        trans.Commit();
                    }
                }
                return JsonConvert.SerializeObject(new { success = true, trussesTargeted = ids.Count, deletedCount = deleted });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ createTruss
        [MCPMethod("createTruss", Category = "Structural",
            Description = "Creates a structural truss along a bearing line at a level. Params: trussTypeId (int), levelId (int), startPoint{x,y}, endPoint{x,y}; optional bearingOffset (ft above level, default 0), trussHeight (ft), and beam types chordTypeId (all members) or topChordTypeId/bottomChordTypeId/webTypeId.")]
        public static string CreateTruss(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["trussTypeId"] == null) return Err("trussTypeId is required");
                if (parameters["levelId"] == null) return Err("levelId is required");
                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                    return Err("startPoint and endPoint are required");

                var trussType = doc.GetElement(new ElementId(parameters["trussTypeId"].ToObject<int>())) as TrussType;
                if (trussType == null)
                    return Err("trussTypeId is not a TrussType. Call getTrussTypes (load a Structural Truss family first).");

                var level = doc.GetElement(new ElementId(parameters["levelId"].ToObject<int>())) as Level;
                if (level == null) return Err("Level not found");

                double bearingOffset = parameters["bearingOffset"]?.ToObject<double>() ?? 0.0;
                double z = level.Elevation + bearingOffset;

                XYZ p1 = PtFromJson(parameters["startPoint"], z);
                XYZ p2 = PtFromJson(parameters["endPoint"], z);
                if (p1.DistanceTo(p2) < doc.Application.ShortCurveTolerance)
                    return Err("Bearing line too short (start and end nearly identical).");

                int? single = parameters["chordTypeId"]?.ToObject<int>();
                int? topId = parameters["topChordTypeId"]?.ToObject<int>();
                int? botId = parameters["bottomChordTypeId"]?.ToObject<int>();
                int? webId = parameters["webTypeId"]?.ToObject<int>();

                Truss truss;
                int memberCount;
                var memberIds = new List<long>();

                using (var trans = new Transaction(doc, "Create Truss"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);

                    // Assign the 4 framing-type ROLE params on the TrussType BEFORE Truss.Create so
                    // generated members come out as the right 2x size (2x6 chords / 2x4 webs).
                    // (Exact BuiltInParameter names verified against RevitAPI.dll 2026.)
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_TOP_CHORD_STRUCTURAL_TYPES_PARAM, topId ?? single);
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_BOTTOM_CHORD_STRUCTURAL_TYPES_PARAM, botId ?? single);
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_VERT_WEB_STRUCTURAL_TYPES_PARAM, webId ?? single);
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_DIAG_WEB_STRUCTURAL_TYPES_PARAM, webId ?? single);

                    Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
                    SketchPlane sp = SketchPlane.Create(doc, plane);
                    Line line = Line.CreateBound(p1, p2);

                    truss = Truss.Create(doc, trussType.Id, sp.Id, line);

                    if (parameters["trussHeight"] != null)
                    {
                        var hp = trussType.LookupParameter("Truss Height");
                        if (hp != null && !hp.IsReadOnly) hp.Set(parameters["trussHeight"].ToObject<double>());
                    }

                    doc.Regenerate();
                    ApplyMemberTypes(doc, truss, single, topId, botId, webId, memberIds);
                    memberCount = truss.Members.Count;

                    trans.CommitAndCheck();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    trussId = truss.Id.Value,
                    trussType = trussType.Name,
                    level = level.Name,
                    bearingElevation = z,
                    length = p1.DistanceTo(p2),
                    memberCount,
                    memberIds
                });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ setTrussChords
        [MCPMethod("setTrussChords", Category = "Structural",
            Description = "Assigns beam types to an existing truss's members. Params: trussId (int); chordTypeId (all members) or topChordTypeId/bottomChordTypeId/webTypeId.")]
        public static string SetTrussChords(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["trussId"] == null) return Err("trussId is required");
                var truss = doc.GetElement(new ElementId(parameters["trussId"].ToObject<int>())) as Truss;
                if (truss == null) return Err("Truss not found");

                int? single = parameters["chordTypeId"]?.ToObject<int>();
                int? topId = parameters["topChordTypeId"]?.ToObject<int>();
                int? botId = parameters["bottomChordTypeId"]?.ToObject<int>();
                int? webId = parameters["webTypeId"]?.ToObject<int>();
                int changed;

                using (var trans = new Transaction(doc, "Set Truss Chords"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);
                    changed = ApplyMemberTypes(doc, truss, single, topId, botId, webId, null);
                    trans.CommitAndCheck();
                }
                return JsonConvert.SerializeObject(new { success = true, trussId = truss.Id.Value, membersChanged = changed });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ createTrussType
        [MCPMethod("createTrussType", Category = "Structural",
            Description = "Duplicates a truss type with a new name and optional Truss Height (ft) — for hip step-downs / girders / different sizes. Params: baseTrussTypeId (int), newName (string); optional trussHeight (ft).")]
        public static string CreateTrussType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["baseTrussTypeId"] == null) return Err("baseTrussTypeId is required");
                if (parameters["newName"] == null) return Err("newName is required");

                var baseType = doc.GetElement(new ElementId(parameters["baseTrussTypeId"].ToObject<int>())) as TrussType;
                if (baseType == null) return Err("baseTrussTypeId is not a TrussType");
                string newName = parameters["newName"].ToObject<string>();

                ElementId newId = null;
                using (var trans = new Transaction(doc, "Create Truss Type"))
                {
                    trans.Start();
                    var nt = baseType.Duplicate(newName) as TrussType;
                    if (nt != null && parameters["trussHeight"] != null)
                    {
                        var hp = nt.LookupParameter("Truss Height");
                        if (hp != null && !hp.IsReadOnly) hp.Set(parameters["trussHeight"].ToObject<double>());
                    }
                    newId = nt?.Id;
                    trans.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, trussTypeId = newId?.Value, name = newName });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ createTrussRow
        [MCPMethod("createTrussRow", Category = "Structural",
            Description = "Places a ROW of identical trusses (one TrussType) along parallel bearing lines in ONE transaction. Params: trussTypeId, levelId, positions (array of coords along the array axis), spanStart, spanEnd (extent along the span axis); optional spanAxis ('Y' default | 'X'), bearingOffset (ft, default 0), chordTypeId or topChordTypeId/bottomChordTypeId/webTypeId. Returns trusses[] with id + memberCount.")]
        public static string CreateTrussRow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["trussTypeId"] == null) return Err("trussTypeId is required");
                if (parameters["levelId"] == null) return Err("levelId is required");
                if (parameters["positions"] == null) return Err("positions array is required");
                if (parameters["spanStart"] == null || parameters["spanEnd"] == null)
                    return Err("spanStart and spanEnd are required");

                var trussType = doc.GetElement(new ElementId(parameters["trussTypeId"].ToObject<int>())) as TrussType;
                if (trussType == null) return Err("trussTypeId is not a TrussType");
                var level = doc.GetElement(new ElementId(parameters["levelId"].ToObject<int>())) as Level;
                if (level == null) return Err("Level not found");

                double bearingOffset = parameters["bearingOffset"]?.ToObject<double>() ?? 0.0;
                double z = level.Elevation + bearingOffset;
                string spanAxis = (parameters["spanAxis"]?.ToObject<string>() ?? "Y").Trim().ToUpperInvariant();
                double spanStart = parameters["spanStart"].ToObject<double>();
                double spanEnd = parameters["spanEnd"].ToObject<double>();

                int? single = parameters["chordTypeId"]?.ToObject<int>();
                int? topId = parameters["topChordTypeId"]?.ToObject<int>();
                int? botId = parameters["bottomChordTypeId"]?.ToObject<int>();
                int? webId = parameters["webTypeId"]?.ToObject<int>();

                var positions = parameters["positions"].Select(t => t.ToObject<double>()).ToList();
                var results = new List<object>();

                using (var trans = new Transaction(doc, "Create Truss Row"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);

                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_TOP_CHORD_STRUCTURAL_TYPES_PARAM, topId ?? single);
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_BOTTOM_CHORD_STRUCTURAL_TYPES_PARAM, botId ?? single);
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_VERT_WEB_STRUCTURAL_TYPES_PARAM, webId ?? single);
                    SetFramingTypeParam(doc, trussType, BuiltInParameter.TRUSS_FAMILY_DIAG_WEB_STRUCTURAL_TYPES_PARAM, webId ?? single);

                    Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
                    SketchPlane sp = SketchPlane.Create(doc, plane);

                    foreach (double pos in positions)
                    {
                        XYZ p1, p2;
                        if (spanAxis == "X") { p1 = new XYZ(spanStart, pos, z); p2 = new XYZ(spanEnd, pos, z); }
                        else { p1 = new XYZ(pos, spanStart, z); p2 = new XYZ(pos, spanEnd, z); }
                        if (p1.DistanceTo(p2) < doc.Application.ShortCurveTolerance)
                        { results.Add(new { position = pos, error = "span too short" }); continue; }
                        try
                        {
                            var truss = Truss.Create(doc, trussType.Id, sp.Id, Line.CreateBound(p1, p2));
                            doc.Regenerate();
                            ApplyMemberTypes(doc, truss, single, topId, botId, webId, null);
                            results.Add(new { trussId = truss.Id.Value, position = pos, memberCount = truss.Members.Count });
                        }
                        catch (Exception ex) { results.Add(new { position = pos, error = ex.Message }); }
                    }
                    trans.CommitAndCheck();
                }
                return JsonConvert.SerializeObject(new { success = true, count = results.Count, trusses = results });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ groupTrusses
        [MCPMethod("groupTrusses", Category = "Structural",
            Description = "Tags a set of trusses as a role group via their Mark (schedulable set) and optionally creates a Model Group. Params: trussIds (array), groupName (string); optional createModelGroup (bool, default false). Returns marked count + grouped bool.")]
        public static string GroupTrusses(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["trussIds"] == null) return Err("trussIds array is required");
                if (parameters["groupName"] == null) return Err("groupName is required");
                string groupName = parameters["groupName"].ToObject<string>();
                bool createMG = parameters["createModelGroup"]?.ToObject<bool>() ?? false;
                var ids = parameters["trussIds"].Select(t => new ElementId(t.ToObject<int>())).ToList();

                int marked = 0;
                using (var trans = new Transaction(doc, "Mark Truss Group"))
                {
                    trans.Start();
                    foreach (var id in ids)
                    {
                        var e = doc.GetElement(id);
                        if (e == null) continue;
                        var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        if (p != null && !p.IsReadOnly) { try { p.Set(groupName); marked++; } catch { } }
                    }
                    trans.Commit();
                }

                bool grouped = false; string groupErr = null;
                if (createMG)
                {
                    using (var trans = new Transaction(doc, "Group Trusses"))
                    {
                        trans.Start();
                        try { var g = doc.Create.NewGroup(ids); grouped = g != null; trans.Commit(); }
                        catch (Exception gex) { groupErr = gex.Message; trans.RollBack(); }
                    }
                }
                return JsonConvert.SerializeObject(new { success = true, groupName, marked, grouped, groupErr });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }

        // ------------------------------------------------------------------ attachTrussChords
        [MCPMethod("attachTrussChords", Category = "Structural",
            Description = "Attaches the top (or bottom) chord of one or more trusses to a roof/floor so they parametrically follow its slope. Params: trussIds (array), attachToElementId (roof/floor id); optional location ('Top' default | 'Bottom'), forceRemoveSketch (bool default true). Returns per-truss attached/failed.")]
        public static string AttachTrussChords(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["trussIds"] == null) return Err("trussIds array is required");
                if (parameters["attachToElementId"] == null) return Err("attachToElementId is required");

                var attachTo = doc.GetElement(new ElementId(parameters["attachToElementId"].ToObject<int>()));
                if (attachTo == null) return Err("attachToElement not found");

                string loc = (parameters["location"]?.ToObject<string>() ?? "Top").Trim();
                TrussChordLocation chordLoc = loc.IndexOf("Bottom", StringComparison.OrdinalIgnoreCase) >= 0
                    ? TrussChordLocation.Bottom : TrussChordLocation.Top;
                bool forceRemoveSketch = parameters["forceRemoveSketch"]?.ToObject<bool>() ?? true;

                var ids = parameters["trussIds"].Select(t => t.ToObject<int>()).ToList();
                var results = new List<object>();
                int attached = 0;
                var capture = new AttachFailureCapture();
                bool committed = false;

                using (var trans = new Transaction(doc, "Attach Truss Chords"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(capture);
                    fo.SetClearAfterRollback(true);
                    trans.SetFailureHandlingOptions(fo);

                    foreach (int tid in ids)
                    {
                        var truss = doc.GetElement(new ElementId(tid)) as Truss;
                        if (truss == null) { results.Add(new { trussId = tid, error = "not a Truss" }); continue; }
                        try
                        {
                            truss.AttachChord(attachTo, chordLoc, forceRemoveSketch);
                            attached++;
                            results.Add(new { trussId = tid, attached = true });
                        }
                        catch (Exception ex) { results.Add(new { trussId = tid, error = ex.Message }); }
                    }
                    doc.Regenerate();
                    committed = (trans.Commit() == TransactionStatus.Committed);
                }
                return JsonConvert.SerializeObject(new { success = committed, committed, attachedCount = attached, total = ids.Count, failures = capture.Messages, results });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }
    }
}
