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
    /// Batch edits across a whole category — "set Manufacturer to X on all lighting fixtures",
    /// "set Comments to reviewed on all doors". Sets the parameter on each element; if the
    /// parameter lives on the TYPE (e.g. Manufacturer/Model), sets it on the type (deduped).
    /// Pairs with coordinateModel: find the gaps, then fix them in one shot. Read-write.
    /// </summary>
    public static class BatchEditMethods
    {
        [MCPMethod("setParameterByCategory", Category = "Parameter",
            Description = "Set a parameter to a value on EVERY element of a category. Params: category (e.g. 'Doors','Lighting Fixtures'), parameterName (e.g. 'Mark','Manufacturer','Comments','Fire Rating'), value. Sets type params (Manufacturer/Model) on the type. Returns how many were updated.")]
        public static string SetParameterByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string category = parameters["category"]?.ToString();
                string paramName = parameters["parameterName"]?.ToString();
                string value = parameters["value"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(paramName))
                    return JsonConvert.SerializeObject(new { success = false, error = "category and parameterName are required" });

                var bic = ElementTypeMethods.CatFromName(category);
                if (bic == null) return JsonConvert.SerializeObject(new { success = false, error = "unknown category '" + category + "'" });

                var els = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements();
                if (els.Count == 0) return JsonConvert.SerializeObject(new { success = true, category, elementsAffected = 0, message = "no elements of that category in the model" });

                int updated = 0, skipped = 0;
                var doneTypes = new HashSet<int>();

                using (var t = new Transaction(doc, "Set " + paramName + " on all " + category))
                {
                    t.Start();
                    foreach (var el in els)
                    {
                        var p = el.LookupParameter(paramName);
                        if (p != null && !p.IsReadOnly) { if (SetVal(p, value)) updated++; else skipped++; continue; }

                        // fall back to the type parameter (Manufacturer/Model live on the type)
                        var type = doc.GetElement(el.GetTypeId());
                        if (type == null) { skipped++; continue; }
                        if (doneTypes.Contains((int)type.Id.Value)) continue;   // already set this type
                        var tp = type.LookupParameter(paramName);
                        if (tp != null && !tp.IsReadOnly) { if (SetVal(tp, value)) { updated++; doneTypes.Add((int)type.Id.Value); } else skipped++; }
                        else skipped++;
                    }
                    t.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category,
                    parameterName = paramName,
                    value,
                    elementsAffected = updated,
                    skipped,
                    message = "Set " + paramName + " = '" + value + "' on " + updated + " " + category + (skipped > 0 ? " (" + skipped + " skipped — no writable '" + paramName + "')" : "")
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("autoMarkByCategory", Category = "Parameter",
            Description = "Assign sequential Mark values to elements of a category — e.g. 'number all the doors D-1, D-2...'. Params: category, prefix (optional, e.g. 'D-'), start (optional int, default 1), onlyMissing (optional bool, default true = only fill blank Marks). Returns how many were marked.")]
        public static string AutoMarkByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string category = parameters["category"]?.ToString();
                string prefix = parameters["prefix"]?.ToString() ?? "";
                int start = parameters["start"]?.ToObject<int>() ?? 1;
                bool onlyMissing = parameters["onlyMissing"]?.ToObject<bool>() ?? true;
                if (string.IsNullOrWhiteSpace(category))
                    return JsonConvert.SerializeObject(new { success = false, error = "category is required" });

                var bic = ElementTypeMethods.CatFromName(category);
                if (bic == null) return JsonConvert.SerializeObject(new { success = false, error = "unknown category '" + category + "'" });

                var els = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType()
                    .ToElements().OrderBy(e => (int)e.Id.Value).ToList();
                if (els.Count == 0) return JsonConvert.SerializeObject(new { success = true, category, marked = 0, message = "no elements of that category" });

                int n = start, marked = 0, skipped = 0;
                using (var t = new Transaction(doc, "Auto-mark " + category))
                {
                    t.Start();
                    foreach (var el in els)
                    {
                        var p = el.LookupParameter("Mark");
                        if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) { skipped++; continue; }
                        if (onlyMissing && !string.IsNullOrWhiteSpace(p.AsString())) continue;   // leave existing marks
                        p.Set(prefix + n.ToString());
                        n++; marked++;
                    }
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, category, marked, prefix, startedAt = start, message = "Marked " + marked + " " + category + " as " + prefix + start + " … " + prefix + (n - 1) });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static bool SetVal(Parameter p, string value)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.Set(value ?? "");
                    case StorageType.Integer:
                        if (int.TryParse(value, out int iv)) return p.Set(iv);
                        // yes/no params
                        if (value != null && (value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase))) return p.Set(1);
                        if (value != null && (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase))) return p.Set(0);
                        return false;
                    case StorageType.Double:
                        if (double.TryParse(value, out double d)) return p.Set(d);
                        return false;
                    default: return false;
                }
            }
            catch { return false; }
        }
    }
}
