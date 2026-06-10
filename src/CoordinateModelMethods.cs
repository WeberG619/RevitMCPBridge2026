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
    /// Whole-model coordination/completeness audit. Walks each relevant category, checks the
    /// key fields that matter for that category, and reports counts + what's missing. Categories
    /// with no elements are skipped (graceful). Read-only. Powers the agent's "coordinate
    /// everything and tell me what's missing" request.
    /// </summary>
    public static class CoordinateModelMethods
    {
        private class CatSpec
        {
            public string Label;
            public BuiltInCategory Cat;
            public string[] Fields;
            public bool IsRoom;
        }

        [MCPMethod("coordinateModel", Category = "Coordination",
            Description = "Coordinate the whole model: per-category completeness audit across doors, windows, rooms, equipment, plumbing/electrical/lighting fixtures and furniture. Reports element counts and which key fields (Mark, Manufacturer, Model, Fire Rating, Number...) are missing. Skips categories with no elements. Read-only.")]
        public static string CoordinateModel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var specs = new List<CatSpec>
                {
                    new CatSpec { Label = "Doors",                Cat = BuiltInCategory.OST_Doors,                Fields = new[] { "Mark", "Fire Rating" } },
                    new CatSpec { Label = "Windows",              Cat = BuiltInCategory.OST_Windows,              Fields = new[] { "Mark" } },
                    new CatSpec { Label = "Rooms",                Cat = BuiltInCategory.OST_Rooms,                Fields = new[] { "Number" }, IsRoom = true },
                    new CatSpec { Label = "Specialty Equipment",  Cat = BuiltInCategory.OST_SpecialityEquipment,  Fields = new[] { "Mark", "Manufacturer", "Model" } },
                    new CatSpec { Label = "Mechanical Equipment", Cat = BuiltInCategory.OST_MechanicalEquipment,  Fields = new[] { "Mark", "Manufacturer", "Model" } },
                    new CatSpec { Label = "Electrical Equipment", Cat = BuiltInCategory.OST_ElectricalEquipment,  Fields = new[] { "Mark", "Manufacturer", "Model" } },
                    new CatSpec { Label = "Plumbing Fixtures",    Cat = BuiltInCategory.OST_PlumbingFixtures,     Fields = new[] { "Mark", "Manufacturer", "Model" } },
                    new CatSpec { Label = "Electrical Fixtures",  Cat = BuiltInCategory.OST_ElectricalFixtures,   Fields = new[] { "Mark" } },
                    new CatSpec { Label = "Lighting Fixtures",    Cat = BuiltInCategory.OST_LightingFixtures,     Fields = new[] { "Mark", "Manufacturer", "Model" } },
                    new CatSpec { Label = "Furniture",            Cat = BuiltInCategory.OST_Furniture,            Fields = new[] { "Mark" } },
                };

                var categories = new List<object>();
                int totalIssues = 0;

                foreach (var s in specs)
                {
                    List<Element> els;
                    try { els = new FilteredElementCollector(doc).OfCategory(s.Cat).WhereElementIsNotElementType().ToElements().ToList(); }
                    catch { continue; }
                    if (els.Count == 0) continue;   // not in this model -> skip silently

                    var missing = new Dictionary<string, int>();
                    int unplaced = 0;

                    foreach (var el in els)
                    {
                        if (s.IsRoom && el is Autodesk.Revit.DB.Architecture.Room rm && rm.Area <= 0) unplaced++;
                        var type = doc.GetElement(el.GetTypeId());
                        foreach (var f in s.Fields)
                        {
                            string v = ReadParam(el, f) ?? ReadParam(type, f);
                            if (string.IsNullOrWhiteSpace(v))
                                missing[f] = (missing.TryGetValue(f, out var c) ? c : 0) + 1;
                        }
                    }

                    totalIssues += missing.Values.Sum() + unplaced;

                    var catObj = new Dictionary<string, object>
                    {
                        ["category"] = s.Label,
                        ["count"] = els.Count,
                        ["missing"] = missing.Count > 0 ? missing.ToDictionary(k => k.Key + " missing", k => k.Value) : null
                    };
                    if (unplaced > 0) catObj["unplaced or unbounded"] = unplaced;
                    categories.Add(catObj);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = categories.Count == 0 ? "No coordinatable categories found in this model." : "Whole-model coordination audit complete.",
                    categoriesPresent = categories.Count,
                    totalIssues,
                    categories
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        [MCPMethod("findDuplicates", Category = "Coordination",
            Description = "Find overlapping / duplicate elements within a category (e.g. stacked walls at the same spot). Params: category (name, e.g. 'Walls','Doors'), tolerance (optional feet, default 0.01). Read-only.")]
        public static string FindDuplicates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string category = parameters["category"]?.ToString();
                var bic = ElementTypeMethods.CatFromName(category);
                if (bic == null) return JsonConvert.SerializeObject(new { success = false, error = "unknown category '" + category + "'" });
                var p = new JObject { ["categoryId"] = (int)bic.Value, ["tolerance"] = parameters["tolerance"] ?? 0.01 };
                return CoordinationMethods.FindOverlappingElements(uiApp, p);
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static string ReadParam(Element el, string name)
        {
            if (el == null) return null;
            var p = el.LookupParameter(name);
            if (p == null) return null;
            string v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }
}
