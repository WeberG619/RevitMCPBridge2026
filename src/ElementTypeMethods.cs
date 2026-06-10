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
    /// Change an element's type, and find type ids by category/name — so the agent can do
    /// "change this door to a 3'-0 x 7'-0" (find_types -> change_type).
    /// </summary>
    public static class ElementTypeMethods
    {
        [MCPMethod("changeElementType", Category = "Element",
            Description = "Change an element's type. Params: elementId (int), typeId (int). The typeId comes from findTypes.")]
        public static string ChangeElementType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["elementId"] == null || parameters["typeId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId and typeId are required" });

                int elId = parameters["elementId"].ToObject<int>();
                int tyId = parameters["typeId"].ToObject<int>();
                var el = doc.GetElement(new ElementId(elId));
                if (el == null) return JsonConvert.SerializeObject(new { success = false, error = "element not found: " + elId });
                var nt = doc.GetElement(new ElementId(tyId));
                if (nt == null) return JsonConvert.SerializeObject(new { success = false, error = "type not found: " + tyId });

                using (var t = new Transaction(doc, "Change Element Type"))
                {
                    t.Start();
                    if (nt is FamilySymbol fs && !fs.IsActive) fs.Activate();
                    el.ChangeTypeId(new ElementId(tyId));
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, elementId = elId, newTypeId = tyId, newType = (nt as ElementType)?.Name ?? nt.Name });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("changeTypeByCategory", Category = "Element",
            Description = "Change EVERY element of a category to a type ('change all the doors to 36x80'). Params: category (e.g. 'Doors'), typeId (from findTypes). Skips elements already on that type. Returns counts.")]
        public static string ChangeTypeByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string category = parameters["category"]?.ToString() ?? "";
                if (parameters["typeId"] == null) return JsonConvert.SerializeObject(new { success = false, error = "typeId is required (use findTypes first)" });
                int tyId = parameters["typeId"].ToObject<int>();
                var bic = CatFromName(category);
                if (bic == null) return JsonConvert.SerializeObject(new { success = false, error = "unknown category '" + category + "'" });
                var nt = doc.GetElement(new ElementId(tyId));
                if (nt == null) return JsonConvert.SerializeObject(new { success = false, error = "type not found: " + tyId });

                var els = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToList();
                int changed = 0, skipped = 0, failed = 0;
                using (var t = new Transaction(doc, "Change type by category"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    if (nt is FamilySymbol fs && !fs.IsActive) fs.Activate();
                    foreach (var el in els)
                    {
                        try
                        {
                            if (el.GetTypeId().Value == tyId) { skipped++; continue; }
                            el.ChangeTypeId(new ElementId(tyId));
                            changed++;
                        }
                        catch { failed++; }
                    }
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new
                {
                    success = true, category, newType = (nt as ElementType)?.Name ?? nt.Name,
                    totalElements = els.Count, changed, alreadyThatType = skipped, failed,
                    message = "Changed " + changed + " of " + els.Count + " " + category + " to '" + ((nt as ElementType)?.Name ?? nt.Name) + "'" + (skipped > 0 ? " (" + skipped + " already were)" : "") + "."
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("findTypes", Category = "Element",
            Description = "Find element types by category, optionally filtered by a name substring. Params: category (e.g. 'Doors','Windows','Walls','Lighting Fixtures'), contains (optional). Returns matching {id, name}.")]
        public static string FindTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string category = parameters["category"]?.ToString() ?? "";
                string contains = parameters["contains"]?.ToString();
                var bic = CatFromName(category);
                if (bic == null) return JsonConvert.SerializeObject(new { success = false, error = "unknown category '" + category + "'. Try Doors/Windows/Walls/Rooms/Furniture/Plumbing Fixtures/Lighting Fixtures/Specialty Equipment/Mechanical Equipment/Electrical Equipment/Casework." });

                // Normalized matching — type names carry inch marks ("36\" x 80\"") and family prefixes
                // the user never types ('Single-Flush 36 x 80' must still find '36" x 80"').
                static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                string needle = Norm(contains);
                var all = new FilteredElementCollector(doc)
                    .OfCategory(bic.Value).WhereElementIsElementType().ToElements()
                    .Select(e => new { id = (int)e.Id.Value, name = (e as ElementType)?.Name ?? e.Name, family = (e as ElementType)?.FamilyName ?? "" })
                    .Where(x => !string.IsNullOrEmpty(x.name)).ToList();
                var types = all
                    .Where(x => needle.Length == 0
                        || Norm(x.name).Contains(needle)
                        || Norm(x.family + " " + x.name).Contains(needle)
                        || needle.Contains(Norm(x.name)))   // user string wraps the type name (e.g. family prefix + name)
                    .OrderBy(x => x.name).Take(40).ToList();

                return JsonConvert.SerializeObject(new { success = true, category, count = types.Count, types,
                    note = (types.Count == 0 && all.Count > 0) ? ("no name matched '" + contains + "' — the " + all.Count + " available types are: " + string.Join(", ", all.Take(15).Select(x => x.name))) : null });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        public static BuiltInCategory? CatFromName(string n)
        {
            n = (n ?? "").ToLowerInvariant();
            if (n.Contains("door")) return BuiltInCategory.OST_Doors;
            if (n.Contains("window")) return BuiltInCategory.OST_Windows;
            if (n.Contains("wall")) return BuiltInCategory.OST_Walls;
            if (n.Contains("floor")) return BuiltInCategory.OST_Floors;
            if (n.Contains("ceiling")) return BuiltInCategory.OST_Ceilings;
            if (n.Contains("roof")) return BuiltInCategory.OST_Roofs;
            if (n.Contains("furniture")) return BuiltInCategory.OST_Furniture;
            if (n.Contains("casework")) return BuiltInCategory.OST_Casework;
            if (n.Contains("plumb")) return BuiltInCategory.OST_PlumbingFixtures;
            if (n.Contains("light")) return BuiltInCategory.OST_LightingFixtures;
            if (n.Contains("special")) return BuiltInCategory.OST_SpecialityEquipment;
            if (n.Contains("mech")) return BuiltInCategory.OST_MechanicalEquipment;
            if (n.Contains("elect") && n.Contains("equip")) return BuiltInCategory.OST_ElectricalEquipment;
            if (n.Contains("elect")) return BuiltInCategory.OST_ElectricalFixtures;
            if (n.Contains("equip")) return BuiltInCategory.OST_SpecialityEquipment;
            if (n.Contains("room")) return BuiltInCategory.OST_Rooms;
            return null;
        }
    }
}
