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
    /// Two production helpers: assign UNIQUE sequential Marks (avoids the duplicate-Mark warning), and
    /// create a schedule that actually has FIELDS in it (not an empty one). Read-write.
    /// </summary>
    public static class ScheduleAndMarkMethods
    {
        private static BuiltInCategory CategoryFromName(string name)
        {
            var n = (name ?? "").Trim().ToLowerInvariant().TrimEnd('s');
            switch (n)
            {
                case "door": return BuiltInCategory.OST_Doors;
                case "window": return BuiltInCategory.OST_Windows;
                case "room": return BuiltInCategory.OST_Rooms;
                case "wall": return BuiltInCategory.OST_Walls;
                case "floor": return BuiltInCategory.OST_Floors;
                case "ceiling": return BuiltInCategory.OST_Ceilings;
                case "furniture": return BuiltInCategory.OST_Furniture;
                case "casework": return BuiltInCategory.OST_Casework;
                case "plumbing fixture": return BuiltInCategory.OST_PlumbingFixtures;
                case "lighting fixture": return BuiltInCategory.OST_LightingFixtures;
                case "sheet": return BuiltInCategory.OST_Sheets;
                default: return BuiltInCategory.OST_GenericModel;
            }
        }

        private static string[] DefaultFields(string category)
        {
            var n = (category ?? "").Trim().ToLowerInvariant().TrimEnd('s');
            switch (n)
            {
                case "door":
                case "window": return new[] { "Mark", "Family and Type", "Width", "Height", "Level", "Comments" };
                case "room": return new[] { "Number", "Name", "Area", "Level", "Comments" };
                case "wall": return new[] { "Mark", "Family and Type", "Length", "Area", "Comments" };
                default: return new[] { "Mark", "Family and Type", "Level", "Comments" };
            }
        }

        [MCPMethod("setSequentialMarks", Category = "Parameters",
            Description = "Assign UNIQUE sequential Mark values to elements (so they don't collide). Params: prefix (e.g. 'MC-TEST-'); elementIds (array) OR category (e.g. 'Doors'); start (default 1); padding (digits, default 3); comments (optional, set on all). Returns count + first/last mark.")]
        public static string SetSequentialMarks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string prefix = parameters["prefix"]?.ToString() ?? "";
                int start = parameters["start"]?.ToObject<int>() ?? 1;
                int pad = parameters["padding"]?.ToObject<int>() ?? 3;
                string comments = parameters["comments"]?.ToString();

                List<Element> els = new List<Element>();
                if (parameters["elementIds"] is JArray arr && arr.Count > 0)
                {
                    foreach (var t in arr) { var e = doc.GetElement(new ElementId(int.Parse(t.ToString()))); if (e != null) els.Add(e); }
                }
                else if (parameters["category"] != null)
                {
                    var bic = CategoryFromName(parameters["category"].ToString());
                    els = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();
                }
                else return JsonConvert.SerializeObject(new { success = false, error = "provide elementIds or category" });

                if (els.Count == 0) return JsonConvert.SerializeObject(new { success = false, error = "no elements found to mark" });

                int n = start; string first = null, last = null; int done = 0;
                using (var t = new Transaction(doc, "Set sequential marks"))
                {
                    t.Start();
                    try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                    foreach (var el in els)
                    {
                        string mark = prefix + n.ToString().PadLeft(pad, '0');
                        var mp = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        if (mp != null && !mp.IsReadOnly) { mp.Set(mark); if (first == null) first = mark; last = mark; done++; n++; }   // only consume a number when actually applied (no gaps)
                        if (!string.IsNullOrEmpty(comments)) { var cp = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS); if (cp != null && !cp.IsReadOnly) cp.Set(comments); }
                    }
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, count = done, firstMark = first, lastMark = last, message = "Set " + done + " unique marks (" + first + " … " + last + ")" + (comments != null ? " and Comments." : ".") });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static string DefaultMarkPrefix(string category)
        {
            var n = (category ?? "").Trim().ToLowerInvariant().TrimEnd('s');
            switch (n) { case "door": return "D-"; case "window": return "W-"; case "wall": return "WL-"; case "room": return "R-"; default: return "M-"; }
        }

        [MCPMethod("fixDuplicateMarks", Category = "Parameters",
            Description = "Find elements with DUPLICATE or BLANK Mark values and renumber the offenders to UNIQUE marks (the first occurrence of each mark is kept). Params: category (optional — Doors/Windows/Walls/Rooms/…; OMIT to sweep ALL common categories in the model); prefix (optional, default derived from category). Returns per-category counts of duplicates/blanks found and fixed.")]
        public static string FixDuplicateMarks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string category = parameters["category"]?.ToString();
                string prefixParam = parameters["prefix"]?.ToString();
                // no category = sweep the whole model ("fix any duplicate/blank marks" must not stop at one category)
                var cats = !string.IsNullOrWhiteSpace(category)
                    ? new[] { category }
                    : new[] { "Walls", "Doors", "Windows", "Rooms", "Furniture", "Plumbing Fixtures", "Lighting Fixtures", "Specialty Equipment", "Casework", "Columns" };

                var perCat = new List<object>();
                int totalFixed = 0, totalDups = 0, totalBlanks = 0;
                foreach (var cat in cats)
                {
                    BuiltInCategory bic;
                    try { bic = CategoryFromName(cat); } catch { continue; }
                    List<Element> els;
                    try { els = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList(); } catch { continue; }
                    if (els.Count == 0) continue;
                    string prefix = string.IsNullOrWhiteSpace(prefixParam) ? DefaultMarkPrefix(cat) : prefixParam;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var toFix = new List<Element>();
                    int blanks = 0, dups = 0;
                    var dupExamples = new List<string>();
                    foreach (var el in els)
                    {
                        var mp = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        string mark = mp?.AsString();
                        if (mp == null) continue;                                  // category has no Mark param
                        if (string.IsNullOrWhiteSpace(mark)) { toFix.Add(el); blanks++; continue; }
                        if (!seen.Add(mark)) { toFix.Add(el); dups++; if (dupExamples.Count < 5) dupExamples.Add(mark); }  // duplicate of an already-seen mark
                    }

                    int n = 1, fixedCount = 0; string firstNew = null, lastNew = null;
                    if (toFix.Count > 0)
                    {
                        using (var t = new Transaction(doc, "Fix duplicate marks (" + cat + ")"))
                        {
                            t.Start();
                            try { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(fo); } catch { }
                            foreach (var el in toFix)
                            {
                                var mp = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                                if (mp == null || mp.IsReadOnly) continue;
                                string nm;
                                do { nm = prefix + n.ToString(); n++; } while (seen.Contains(nm));   // never collide with an existing mark
                                seen.Add(nm); mp.Set(nm);
                                if (firstNew == null) firstNew = nm; lastNew = nm; fixedCount++;
                            }
                            t.Commit();
                        }
                    }
                    totalFixed += fixedCount; totalDups += dups; totalBlanks += blanks;
                    perCat.Add(new
                    {
                        category = cat, totalElements = els.Count,
                        duplicatesFound = dups, blanksFound = blanks, fixedCount,
                        duplicateExamples = dupExamples.Count > 0 ? dupExamples : null,
                        newRange = firstNew != null ? (firstNew + " … " + lastNew) : null
                    });
                }
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    scope = string.IsNullOrWhiteSpace(category) ? "whole model" : category,
                    duplicatesFound = totalDups, blanksFound = totalBlanks, fixedCount = totalFixed,
                    categories = perCat,
                    message = totalFixed == 0
                        ? "No duplicate or blank marks found" + (string.IsNullOrWhiteSpace(category) ? " in any category." : " in " + category + ".")
                        : ("Fixed " + totalFixed + " marks (" + totalDups + " duplicate, " + totalBlanks + " blank) across " + perCat.Count + " categor" + (perCat.Count == 1 ? "y" : "ies") + ".")
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        [MCPMethod("createScheduleWithFields", Category = "Schedule",
            Description = "Create a schedule WITH fields (not empty). Params: category (Doors/Windows/Rooms/Walls/…); scheduleName; fields (optional array of field names — defaults to a sensible set for the category). Returns scheduleId + the fields added.")]
        public static string CreateScheduleWithFields(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string category = parameters["category"]?.ToString();
                if (string.IsNullOrWhiteSpace(category)) return JsonConvert.SerializeObject(new { success = false, error = "category is required" });
                string name = parameters["scheduleName"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = category + " Schedule";
                string[] want = (parameters["fields"] is JArray fa && fa.Count > 0) ? fa.Select(x => x.ToString()).ToArray() : DefaultFields(category);

                var bic = CategoryFromName(category);
                var added = new List<string>(); var missed = new List<string>();
                int schedId = 0;
                using (var t = new Transaction(doc, "Create schedule with fields"))
                {
                    t.Start();
                    var sched = ViewSchedule.CreateSchedule(doc, new ElementId(bic));
                    try { sched.Name = name; } catch { }
                    var def = sched.Definition;
                    var schedulable = def.GetSchedulableFields();
                    foreach (var fname in want)
                    {
                        var sf = schedulable.FirstOrDefault(f => { try { return f.GetName(doc).Equals(fname, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                              ?? schedulable.FirstOrDefault(f => { try { return f.GetName(doc).IndexOf(fname, StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } });
                        if (sf != null) { try { def.AddField(sf); added.Add(fname); } catch { missed.Add(fname); } }
                        else missed.Add(fname);
                    }
                    schedId = (int)sched.Id.Value;
                    t.Commit();
                }
                return JsonConvert.SerializeObject(new { success = true, scheduleId = schedId, name, fieldsAdded = added, fieldsNotFound = missed, message = "Created schedule '" + name + "' with " + added.Count + " fields: " + string.Join(", ", added) + (missed.Count > 0 ? " (not available: " + string.Join(", ", missed) + ")" : "") });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
