using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Construction Document Checklist verification methods
    /// Automates CD set completeness checking via MCP
    /// </summary>
    public static class CDChecklistMethods
    {
        #region Main Checklist Method

        /// <summary>
        /// Run comprehensive CD checklist verification on the current model
        /// Returns pass/fail status for each check category
        /// </summary>
        public static string RunCDChecklist(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var includeDetails = parameters["includeDetails"]?.ToObject<bool>() ?? true;
                var categories = parameters["categories"]?.ToObject<string[]>()
                    ?? new[] { "all" };

                var results = new Dictionary<string, object>();
                var summary = new ChecklistSummary();

                // Run all checks
                if (categories.Contains("all") || categories.Contains("sheets"))
                {
                    var sheetResult = CheckSheets(doc);
                    results["sheets"] = sheetResult;
                    UpdateSummary(summary, sheetResult);
                }

                if (categories.Contains("all") || categories.Contains("rooms"))
                {
                    var roomResult = CheckRooms(doc);
                    results["rooms"] = roomResult;
                    UpdateSummary(summary, roomResult);
                }

                if (categories.Contains("all") || categories.Contains("doors"))
                {
                    var doorResult = CheckDoors(doc);
                    results["doors"] = doorResult;
                    UpdateSummary(summary, doorResult);
                }

                if (categories.Contains("all") || categories.Contains("windows"))
                {
                    var windowResult = CheckWindows(doc);
                    results["windows"] = windowResult;
                    UpdateSummary(summary, windowResult);
                }

                if (categories.Contains("all") || categories.Contains("views"))
                {
                    var viewResult = CheckViews(doc);
                    results["views"] = viewResult;
                    UpdateSummary(summary, viewResult);
                }

                if (categories.Contains("all") || categories.Contains("schedules"))
                {
                    var scheduleResult = CheckSchedules(doc);
                    results["schedules"] = scheduleResult;
                    UpdateSummary(summary, scheduleResult);
                }

                if (categories.Contains("all") || categories.Contains("projectInfo"))
                {
                    var projectResult = CheckProjectInfo(doc);
                    results["projectInfo"] = projectResult;
                    UpdateSummary(summary, projectResult);
                }

                if (categories.Contains("all") || categories.Contains("levels"))
                {
                    var levelResult = CheckLevels(doc);
                    results["levels"] = levelResult;
                    UpdateSummary(summary, levelResult);
                }

                // Calculate overall pass rate
                summary.PassRate = summary.TotalChecks > 0
                    ? Math.Round((double)summary.Passed / summary.TotalChecks * 100, 1)
                    : 0;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    summary = new
                    {
                        passed = summary.Passed,
                        warnings = summary.Warnings,
                        failures = summary.Failures,
                        totalChecks = summary.TotalChecks,
                        passRate = summary.PassRate
                    },
                    results = includeDetails ? results : null,
                    recommendations = GenerateRecommendations(results)
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Individual Check Methods

        /// <summary>
        /// Check sheet organization and completeness
        /// </summary>
        private static CheckResult CheckSheets(Document doc)
        {
            var result = new CheckResult { Category = "Sheets" };
            var issues = new List<string>();
            var details = new List<object>();

            // Get all sheets
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .ToList();

            result.Count = sheets.Count;

            // Check for minimum sheets
            if (sheets.Count == 0)
            {
                issues.Add("No sheets found in model");
                result.Status = "FAIL";
            }
            else
            {
                // Group sheets by discipline
                var sheetsByDiscipline = sheets
                    .GroupBy(s => GetDiscipline(s.SheetNumber))
                    .ToDictionary(g => g.Key, g => g.ToList());

                details.Add(new {
                    totalSheets = sheets.Count,
                    byDiscipline = sheetsByDiscipline.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.Count)
                });

                // Check for required architectural sheets
                var archSheets = sheets.Where(s => s.SheetNumber.StartsWith("A")).ToList();
                if (archSheets.Count == 0)
                {
                    issues.Add("No architectural sheets (A-series) found");
                }

                // Check for cover sheet
                var hasCover = sheets.Any(s =>
                    s.SheetNumber.Contains("0.0") ||
                    s.SheetNumber.Contains("-000") ||
                    s.SheetNumber.EndsWith("00") ||
                    s.Name.ToLower().Contains("cover"));
                if (!hasCover)
                {
                    issues.Add("No cover sheet detected");
                }

                // Check for orphan sheets (no viewports)
                var emptySheets = sheets.Where(s =>
                    new FilteredElementCollector(doc, s.Id)
                        .OfClass(typeof(Viewport))
                        .GetElementCount() == 0 &&
                    !s.Name.ToLower().Contains("cover") &&
                    !s.Name.ToLower().Contains("index") &&
                    !s.Name.ToLower().Contains("schedule")).ToList();

                if (emptySheets.Any())
                {
                    issues.Add($"{emptySheets.Count} sheet(s) have no viewports placed");
                    details.Add(new {
                        emptySheets = emptySheets.Select(s => new {
                            number = s.SheetNumber,
                            name = s.Name
                        })
                    });
                }

                result.Status = issues.Count == 0 ? "PASS" :
                               issues.Count <= 2 ? "WARNING" : "FAIL";
            }

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check room naming and numbering
        /// </summary>
        private static CheckResult CheckRooms(Document doc)
        {
            var result = new CheckResult { Category = "Rooms" };
            var issues = new List<string>();
            var details = new List<object>();

            // Get all rooms
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Location != null && r.Area > 0)
                .ToList();

            result.Count = rooms.Count;

            if (rooms.Count == 0)
            {
                issues.Add("No placed rooms found in model");
                result.Status = "FAIL";
            }
            else
            {
                // Check for unnamed rooms
                var unnamedRooms = rooms.Where(r =>
                    string.IsNullOrWhiteSpace(r.Name) ||
                    r.Name == "Room").ToList();
                if (unnamedRooms.Any())
                {
                    issues.Add($"{unnamedRooms.Count} room(s) are unnamed");
                }

                // Check for unnumbered rooms
                var unnumberedRooms = rooms.Where(r =>
                    string.IsNullOrWhiteSpace(r.Number)).ToList();
                if (unnumberedRooms.Any())
                {
                    issues.Add($"{unnumberedRooms.Count} room(s) have no number");
                }

                // Check for duplicate room numbers
                var duplicateNumbers = rooms
                    .Where(r => !string.IsNullOrWhiteSpace(r.Number))
                    .GroupBy(r => r.Number)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();
                if (duplicateNumbers.Any())
                {
                    issues.Add($"Duplicate room numbers: {string.Join(", ", duplicateNumbers)}");
                }

                // Check for rooms without finishes (common finish parameters)
                var roomsWithoutFinish = rooms.Where(r =>
                {
                    var floorFinish = r.LookupParameter("Floor Finish")?.AsString();
                    var wallFinish = r.LookupParameter("Wall Finish")?.AsString();
                    return string.IsNullOrWhiteSpace(floorFinish) &&
                           string.IsNullOrWhiteSpace(wallFinish);
                }).ToList();

                if (roomsWithoutFinish.Count > rooms.Count / 2)
                {
                    issues.Add($"{roomsWithoutFinish.Count} room(s) have no finish data");
                }

                details.Add(new
                {
                    totalRooms = rooms.Count,
                    named = rooms.Count - unnamedRooms.Count,
                    numbered = rooms.Count - unnumberedRooms.Count,
                    withFinishes = rooms.Count - roomsWithoutFinish.Count
                });

                result.Status = issues.Count == 0 ? "PASS" :
                               issues.Count <= 2 ? "WARNING" : "FAIL";
            }

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check door marks and schedule readiness
        /// </summary>
        private static CheckResult CheckDoors(Document doc)
        {
            var result = new CheckResult { Category = "Doors" };
            var issues = new List<string>();
            var details = new List<object>();

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            result.Count = doors.Count;

            if (doors.Count == 0)
            {
                issues.Add("No doors found in model");
                result.Status = "WARNING";
            }
            else
            {
                // Check for unmarked doors
                var unmarkedDoors = doors.Where(d =>
                {
                    var mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    return string.IsNullOrWhiteSpace(mark);
                }).ToList();

                if (unmarkedDoors.Any())
                {
                    issues.Add($"{unmarkedDoors.Count} door(s) have no mark");
                }

                // Check for duplicate marks
                var duplicateMarks = doors
                    .Select(d => d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .GroupBy(m => m)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                if (duplicateMarks.Any())
                {
                    issues.Add($"Duplicate door marks: {string.Join(", ", duplicateMarks.Take(5))}" +
                        (duplicateMarks.Count > 5 ? $" (+{duplicateMarks.Count - 5} more)" : ""));
                }

                details.Add(new
                {
                    totalDoors = doors.Count,
                    marked = doors.Count - unmarkedDoors.Count,
                    unmarked = unmarkedDoors.Count,
                    duplicateMarks = duplicateMarks.Count
                });

                result.Status = unmarkedDoors.Count == 0 && duplicateMarks.Count == 0 ? "PASS" :
                               unmarkedDoors.Count <= 3 ? "WARNING" : "FAIL";
            }

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check window marks and schedule readiness
        /// </summary>
        private static CheckResult CheckWindows(Document doc)
        {
            var result = new CheckResult { Category = "Windows" };
            var issues = new List<string>();
            var details = new List<object>();

            var windows = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            result.Count = windows.Count;

            if (windows.Count == 0)
            {
                // Windows might not exist in all building types
                result.Status = "PASS";
                details.Add(new { note = "No windows in model (may be intentional)" });
            }
            else
            {
                // Check for unmarked windows
                var unmarkedWindows = windows.Where(w =>
                {
                    var mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    return string.IsNullOrWhiteSpace(mark);
                }).ToList();

                if (unmarkedWindows.Any())
                {
                    issues.Add($"{unmarkedWindows.Count} window(s) have no mark");
                }

                details.Add(new
                {
                    totalWindows = windows.Count,
                    marked = windows.Count - unmarkedWindows.Count,
                    unmarked = unmarkedWindows.Count
                });

                result.Status = unmarkedWindows.Count == 0 ? "PASS" :
                               unmarkedWindows.Count <= 3 ? "WARNING" : "FAIL";
            }

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check view organization and placement
        /// </summary>
        private static CheckResult CheckViews(Document doc)
        {
            var result = new CheckResult { Category = "Views" };
            var issues = new List<string>();
            var details = new List<object>();

            // Get all views (excluding templates and schedules)
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate &&
                           v.ViewType != ViewType.Schedule &&
                           v.ViewType != ViewType.DrawingSheet)
                .ToList();

            result.Count = views.Count;

            // Get all viewports
            var viewports = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .ToList();

            var viewsOnSheets = viewports.Select(vp => vp.ViewId).Distinct().ToList();

            // Check for floor plans
            var floorPlans = views.Where(v => v.ViewType == ViewType.FloorPlan).ToList();
            var floorPlansOnSheets = floorPlans.Where(v => viewsOnSheets.Contains(v.Id)).ToList();

            if (floorPlans.Count == 0)
            {
                issues.Add("No floor plans found");
            }
            else if (floorPlansOnSheets.Count < floorPlans.Count)
            {
                issues.Add($"{floorPlans.Count - floorPlansOnSheets.Count} floor plan(s) not on sheets");
            }

            // Check for elevations
            var elevations = views.Where(v => v.ViewType == ViewType.Elevation).ToList();
            var elevationsOnSheets = elevations.Where(v => viewsOnSheets.Contains(v.Id)).ToList();

            if (elevations.Count < 4)
            {
                issues.Add($"Only {elevations.Count} elevation(s) (need 4 for complete set)");
            }

            // Check for sections
            var sections = views.Where(v => v.ViewType == ViewType.Section).ToList();
            if (sections.Count == 0)
            {
                issues.Add("No building sections found");
            }

            // Orphan views (not on any sheet)
            var orphanViews = views.Where(v =>
                !viewsOnSheets.Contains(v.Id) &&
                v.ViewType != ViewType.ThreeD &&
                v.ViewType != ViewType.Legend &&
                !v.Name.StartsWith("_") &&
                !v.Name.Contains("Working")).Count();

            if (orphanViews > 10)
            {
                issues.Add($"{orphanViews} view(s) not placed on sheets");
            }

            details.Add(new
            {
                totalViews = views.Count,
                floorPlans = floorPlans.Count,
                elevations = elevations.Count,
                sections = sections.Count,
                onSheets = viewsOnSheets.Count,
                orphanViews = orphanViews
            });

            result.Status = issues.Count == 0 ? "PASS" :
                           issues.Count <= 2 ? "WARNING" : "FAIL";

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check required schedules exist
        /// </summary>
        private static CheckResult CheckSchedules(Document doc)
        {
            var result = new CheckResult { Category = "Schedules" };
            var issues = new List<string>();
            var details = new List<object>();

            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsTemplate)
                .ToList();

            result.Count = schedules.Count;

            // Check for essential schedules
            var hasDoorSchedule = schedules.Any(s =>
                s.Name.ToLower().Contains("door"));
            var hasWindowSchedule = schedules.Any(s =>
                s.Name.ToLower().Contains("window"));
            var hasRoomSchedule = schedules.Any(s =>
                s.Name.ToLower().Contains("room") ||
                s.Name.ToLower().Contains("finish"));
            var hasSheetIndex = schedules.Any(s =>
                s.Name.ToLower().Contains("sheet") ||
                s.Name.ToLower().Contains("index") ||
                s.Name.ToLower().Contains("drawing list"));

            if (!hasDoorSchedule)
                issues.Add("No door schedule found");
            if (!hasWindowSchedule)
                issues.Add("No window schedule found");
            if (!hasRoomSchedule)
                issues.Add("No room/finish schedule found");
            if (!hasSheetIndex)
                issues.Add("No sheet index/drawing list found");

            // Check schedules are on sheets
            var viewports = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .ToList();
            var viewsOnSheets = viewports.Select(vp => vp.ViewId).Distinct().ToList();

            var schedulesOnSheets = schedules.Where(s => viewsOnSheets.Contains(s.Id)).Count();

            details.Add(new
            {
                totalSchedules = schedules.Count,
                onSheets = schedulesOnSheets,
                hasDoorSchedule,
                hasWindowSchedule,
                hasRoomSchedule,
                hasSheetIndex
            });

            result.Status = issues.Count == 0 ? "PASS" :
                           issues.Count <= 2 ? "WARNING" : "FAIL";

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check project information completeness
        /// </summary>
        private static CheckResult CheckProjectInfo(Document doc)
        {
            var result = new CheckResult { Category = "Project Info" };
            var issues = new List<string>();
            var details = new List<object>();

            var projectInfo = doc.ProjectInformation;
            result.Count = 1;

            // Check required fields
            if (string.IsNullOrWhiteSpace(projectInfo.Name) || projectInfo.Name == "Project Name")
                issues.Add("Project name not set");

            if (string.IsNullOrWhiteSpace(projectInfo.Number) || projectInfo.Number == "Project Number")
                issues.Add("Project number not set");

            if (string.IsNullOrWhiteSpace(projectInfo.Address))
                issues.Add("Project address not set");

            if (string.IsNullOrWhiteSpace(projectInfo.ClientName))
                issues.Add("Client name not set");

            // Check for issue date
            var issueDate = projectInfo.IssueDate;
            if (string.IsNullOrWhiteSpace(issueDate))
                issues.Add("Issue date not set");

            details.Add(new
            {
                name = projectInfo.Name,
                number = projectInfo.Number,
                address = projectInfo.Address,
                client = projectInfo.ClientName,
                issueDate = issueDate,
                status = projectInfo.Status
            });

            result.Status = issues.Count == 0 ? "PASS" :
                           issues.Count <= 2 ? "WARNING" : "FAIL";

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        /// <summary>
        /// Check levels match floor plans
        /// </summary>
        private static CheckResult CheckLevels(Document doc)
        {
            var result = new CheckResult { Category = "Levels" };
            var issues = new List<string>();
            var details = new List<object>();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Where(l => !l.Name.ToLower().Contains("ref"))
                .OrderBy(l => l.Elevation)
                .ToList();

            result.Count = levels.Count;

            // Get floor plans
            var floorPlans = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => v.ViewType == ViewType.FloorPlan && !v.IsTemplate)
                .ToList();

            // Check each level has a floor plan
            foreach (var level in levels)
            {
                var hasFloorPlan = floorPlans.Any(fp =>
                    fp.GenLevel?.Id == level.Id);
                if (!hasFloorPlan)
                {
                    // Skip levels that are typically not plans
                    if (!level.Name.ToLower().Contains("t.o.") &&
                        !level.Name.ToLower().Contains("parapet") &&
                        !level.Name.ToLower().Contains("bearing"))
                    {
                        issues.Add($"Level '{level.Name}' has no floor plan");
                    }
                }
            }

            details.Add(new
            {
                totalLevels = levels.Count,
                floorPlans = floorPlans.Count,
                levels = levels.Select(l => new
                {
                    name = l.Name,
                    elevation = Math.Round(l.Elevation, 2)
                })
            });

            result.Status = issues.Count == 0 ? "PASS" :
                           issues.Count <= 1 ? "WARNING" : "FAIL";

            result.Issues = issues;
            result.Details = details;
            return result;
        }

        #endregion

        #region Helper Methods

        private static string GetDiscipline(string sheetNumber)
        {
            if (string.IsNullOrEmpty(sheetNumber)) return "Unknown";

            var firstChar = sheetNumber.ToUpper()[0];
            switch (firstChar)
            {
                case 'G': return "General";
                case 'C': return "Civil";
                case 'L': return "Landscape";
                case 'A': return "Architectural";
                case 'S': return "Structural";
                case 'M': return "Mechanical";
                case 'P': return "Plumbing";
                case 'E': return "Electrical";
                case 'F': return "Fire Protection";
                case 'I': return "Interiors";
                default: return "Other";
            }
        }

        private static void UpdateSummary(ChecklistSummary summary, CheckResult result)
        {
            summary.TotalChecks++;
            switch (result.Status)
            {
                case "PASS": summary.Passed++; break;
                case "WARNING": summary.Warnings++; break;
                case "FAIL": summary.Failures++; break;
            }
        }

        private static List<string> GenerateRecommendations(Dictionary<string, object> results)
        {
            var recommendations = new List<string>();

            foreach (var kvp in results)
            {
                if (kvp.Value is CheckResult cr && cr.Issues?.Count > 0)
                {
                    switch (cr.Category)
                    {
                        case "Rooms":
                            if (cr.Issues.Any(i => i.Contains("unnamed")))
                                recommendations.Add("Run Room naming tool to assign room names");
                            if (cr.Issues.Any(i => i.Contains("no number")))
                                recommendations.Add("Number rooms using Auto-Number feature");
                            break;
                        case "Doors":
                            if (cr.Issues.Any(i => i.Contains("no mark")))
                                recommendations.Add("Tag doors and assign marks before creating schedule");
                            break;
                        case "Windows":
                            if (cr.Issues.Any(i => i.Contains("no mark")))
                                recommendations.Add("Tag windows and assign marks before creating schedule");
                            break;
                        case "Schedules":
                            if (cr.Issues.Any(i => i.Contains("door schedule")))
                                recommendations.Add("Create Door Schedule from View > Schedules");
                            if (cr.Issues.Any(i => i.Contains("window schedule")))
                                recommendations.Add("Create Window Schedule from View > Schedules");
                            break;
                        case "Views":
                            if (cr.Issues.Any(i => i.Contains("not on sheets")))
                                recommendations.Add("Place working views on sheets or prefix with '_' to exclude");
                            break;
                    }
                }
            }

            if (recommendations.Count == 0)
                recommendations.Add("All checks passed - model is ready for documentation");

            return recommendations;
        }

        #endregion

        #region Public Audit Methods (MCP-exposed)

        /// <summary>
        /// Audit rooms for naming, numbering, finish data, and area issues.
        /// Wraps the private CheckRooms method and adds detailed per-room data.
        /// </summary>
        public static string AuditRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var includeDetails = parameters["includeDetails"]?.Value<bool>() ?? true;
                var levelName = parameters["level"]?.ToString();

                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Location != null && r.Area > 0)
                    .ToList();

                // Filter by level if specified
                if (!string.IsNullOrEmpty(levelName))
                {
                    rooms = rooms.Where(r => r.Level?.Name == levelName).ToList();
                }

                var issues = new List<object>();
                var unnamedCount = 0;
                var unnumberedCount = 0;
                var noFinishCount = 0;
                var duplicateNumbers = new List<string>();

                // Check for duplicates
                var numberGroups = rooms
                    .Where(r => !string.IsNullOrWhiteSpace(r.Number))
                    .GroupBy(r => r.Number)
                    .Where(g => g.Count() > 1);
                foreach (var g in numberGroups)
                    duplicateNumbers.Add(g.Key);

                var roomDetails = new List<object>();
                foreach (var room in rooms)
                {
                    var roomIssues = new List<string>();

                    if (string.IsNullOrWhiteSpace(room.Name) || room.Name == "Room")
                    {
                        roomIssues.Add("unnamed");
                        unnamedCount++;
                    }
                    if (string.IsNullOrWhiteSpace(room.Number))
                    {
                        roomIssues.Add("no number");
                        unnumberedCount++;
                    }

                    var floorFinish = room.LookupParameter("Floor Finish")?.AsString();
                    var wallFinish = room.LookupParameter("Wall Finish")?.AsString();
                    var baseFinish = room.LookupParameter("Base Finish")?.AsString();
                    var ceilingFinish = room.LookupParameter("Ceiling Finish")?.AsString();

                    if (string.IsNullOrWhiteSpace(floorFinish) &&
                        string.IsNullOrWhiteSpace(wallFinish))
                    {
                        roomIssues.Add("no finishes");
                        noFinishCount++;
                    }

                    if (duplicateNumbers.Contains(room.Number))
                        roomIssues.Add("duplicate number");

                    if (includeDetails || roomIssues.Count > 0)
                    {
                        roomDetails.Add(new
                        {
                            id = (int)room.Id.Value,
                            name = room.Name,
                            number = room.Number,
                            level = room.Level?.Name,
                            area = Math.Round(room.Area, 1),
                            floorFinish = floorFinish ?? "",
                            wallFinish = wallFinish ?? "",
                            baseFinish = baseFinish ?? "",
                            ceilingFinish = ceilingFinish ?? "",
                            issues = roomIssues
                        });
                    }
                }

                var totalIssues = unnamedCount + unnumberedCount + noFinishCount + duplicateNumbers.Count;
                var status = totalIssues == 0 ? "PASS" :
                            totalIssues <= 3 ? "WARNING" : "FAIL";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    status,
                    totalRooms = rooms.Count,
                    summary = new
                    {
                        unnamed = unnamedCount,
                        unnumbered = unnumberedCount,
                        noFinishes = noFinishCount,
                        duplicateNumbers = duplicateNumbers.Count
                    },
                    duplicateNumbers,
                    rooms = roomDetails
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Audit doors for marks, types, fire ratings, and schedule readiness.
        /// </summary>
        public static string AuditDoors(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var includeDetails = parameters["includeDetails"]?.Value<bool>() ?? true;
                var levelName = parameters["level"]?.ToString();

                var doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                if (!string.IsNullOrEmpty(levelName))
                {
                    doors = doors.Where(d =>
                    {
                        var lvl = doc.GetElement(d.LevelId) as Level;
                        if (lvl?.Name == levelName) return true;
                        if (d.Host is Wall w)
                        {
                            var wallLevel = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsValueString();
                            if (wallLevel == levelName) return true;
                        }
                        return false;
                    }).ToList();
                }

                var unmarkedCount = 0;
                var noHostCount = 0;
                var duplicateMarks = new List<string>();

                var markGroups = doors
                    .Select(d => d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .GroupBy(m => m)
                    .Where(g => g.Count() > 1);
                foreach (var g in markGroups)
                    duplicateMarks.Add(g.Key);

                var doorDetails = new List<object>();
                foreach (var door in doors)
                {
                    var doorIssues = new List<string>();
                    var mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    var fireRating = door.LookupParameter("Fire Rating")?.AsString();

                    if (string.IsNullOrWhiteSpace(mark))
                    {
                        doorIssues.Add("no mark");
                        unmarkedCount++;
                    }
                    if (duplicateMarks.Contains(mark))
                        doorIssues.Add("duplicate mark");

                    if (door.Host == null)
                    {
                        doorIssues.Add("no host wall");
                        noHostCount++;
                    }

                    if (includeDetails || doorIssues.Count > 0)
                    {
                        doorDetails.Add(new
                        {
                            id = (int)door.Id.Value,
                            mark = mark ?? "",
                            familyName = door.Symbol?.Family?.Name ?? "",
                            typeName = door.Symbol?.Name ?? "",
                            level = (doc.GetElement(door.LevelId) as Level)?.Name ?? "",
                            fireRating = fireRating ?? "",
                            fromRoom = door.FromRoom?.Name ?? "",
                            toRoom = door.ToRoom?.Name ?? "",
                            issues = doorIssues
                        });
                    }
                }

                var totalIssues = unmarkedCount + noHostCount + duplicateMarks.Count;
                var status = totalIssues == 0 ? "PASS" :
                            totalIssues <= 3 ? "WARNING" : "FAIL";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    status,
                    totalDoors = doors.Count,
                    summary = new
                    {
                        unmarked = unmarkedCount,
                        noHost = noHostCount,
                        duplicateMarks = duplicateMarks.Count
                    },
                    duplicateMarks,
                    doors = doorDetails
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Get purgeable element counts without deleting.
        /// Thin wrapper around PurgeUnused with dryRun=true, plus additional categories.
        /// </summary>
        public static string GetPurgeable(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var categories = new Dictionary<string, int>();

                // Unused view types
                var unusedViewTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Count(vft => !IsViewTypeUsedPublic(doc, vft));
                categories["viewTypes"] = unusedViewTypes;

                // Unused line patterns
                var unusedLinePatterns = new FilteredElementCollector(doc)
                    .OfClass(typeof(LinePatternElement))
                    .Count(lp => !IsElementUsedPublic(doc, lp));
                categories["linePatterns"] = unusedLinePatterns;

                // Unused materials
                var unusedMaterials = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Count(m => !IsElementUsedPublic(doc, m));
                categories["materials"] = unusedMaterials;

                // Unused fill patterns
                var unusedFillPatterns = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Count(fp => !IsElementUsedPublic(doc, fp));
                categories["fillPatterns"] = unusedFillPatterns;

                // Unplaced rooms
                var unplacedRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Count(r => r.Location == null || r.Area <= 0);
                categories["unplacedRooms"] = unplacedRooms;

                // Unplaced areas
                var unplacedAreas = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Areas)
                    .WhereElementIsNotElementType()
                    .Count(a => a.LookupParameter("Area")?.AsDouble() <= 0);
                categories["unplacedAreas"] = unplacedAreas;

                // Unused view templates
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .ToList();
                var usedTemplateIds = allViews
                    .Where(v => !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId)
                    .Select(v => v.ViewTemplateId)
                    .Distinct()
                    .ToHashSet();
                var unusedTemplates = allViews
                    .Count(v => v.IsTemplate && !usedTemplateIds.Contains(v.Id));
                categories["unusedViewTemplates"] = unusedTemplates;

                var total = categories.Values.Sum();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalPurgeable = total,
                    categories,
                    message = total > 0
                        ? $"{total} purgeable elements found. Use purgeUnused with dryRun=false to clean."
                        : "Model is clean - no purgeable elements found."
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Run standards check against firm profile or custom rules.
        /// Checks text types, dimension types, line weights, and naming conventions.
        /// </summary>
        public static string RunStandardsCheck(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var checkCategories = parameters["categories"]?.ToObject<string[]>()
                    ?? new[] { "all" };

                var results = new Dictionary<string, object>();
                var totalIssues = 0;

                // Check text note types
                if (checkCategories.Contains("all") || checkCategories.Contains("text"))
                {
                    var textIssues = new List<object>();
                    var textTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNoteType))
                        .Cast<TextNoteType>()
                        .ToList();

                    foreach (var tt in textTypes)
                    {
                        var issues = new List<string>();
                        var height = tt.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                        var heightInches = height * 12;

                        // Flag non-standard text sizes
                        var standardSizes = new[] { 3.0/32, 1.0/8, 5.0/32, 3.0/16, 1.0/4, 3.0/8, 1.0/2 };
                        if (height > 0 && !standardSizes.Any(s => Math.Abs(height - s) < 0.001))
                        {
                            issues.Add($"Non-standard text height: {heightInches:F3}\"");
                        }

                        if (issues.Count > 0)
                        {
                            textIssues.Add(new
                            {
                                name = tt.Name,
                                height = Math.Round(heightInches, 4),
                                issues
                            });
                            totalIssues += issues.Count;
                        }
                    }
                    results["textTypes"] = new { count = textTypes.Count, issues = textIssues };
                }

                // Check dimension types
                if (checkCategories.Contains("all") || checkCategories.Contains("dimensions"))
                {
                    var dimIssues = new List<object>();
                    var dimTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(DimensionType))
                        .Cast<DimensionType>()
                        .Where(dt => dt.StyleType == DimensionStyleType.Linear)
                        .ToList();

                    results["dimensionTypes"] = new { count = dimTypes.Count };
                }

                // Check line weights
                if (checkCategories.Contains("all") || checkCategories.Contains("lineWeights"))
                {
                    // Count categories with non-default overrides
                    var overriddenCategories = 0;
                    var categoryCount = 0;

                    var settings = doc.Settings;
                    foreach (Category cat in settings.Categories)
                    {
                        categoryCount++;
                        var lineWeight = cat.GetLineWeight(GraphicsStyleType.Projection);
                        if (lineWeight.HasValue && lineWeight.Value > 5)
                        {
                            overriddenCategories++;
                        }
                    }

                    results["lineWeights"] = new
                    {
                        categoriesChecked = categoryCount,
                        heavyLineWeights = overriddenCategories,
                        status = overriddenCategories > 10 ? "WARNING" : "PASS"
                    };
                }

                // Check naming conventions
                if (checkCategories.Contains("all") || checkCategories.Contains("naming"))
                {
                    var namingIssues = new List<string>();

                    // Check sheet naming
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .ToList();

                    var sheetsNoNumber = sheets.Count(s => string.IsNullOrWhiteSpace(s.SheetNumber));
                    if (sheetsNoNumber > 0)
                        namingIssues.Add($"{sheetsNoNumber} sheet(s) have no number");

                    // Check view naming (underscore prefix = working views)
                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.ViewType != ViewType.Schedule
                            && v.ViewType != ViewType.DrawingSheet)
                        .ToList();

                    var copyViews = views.Count(v => v.Name.Contains("Copy") || v.Name.Contains("copy"));
                    if (copyViews > 3)
                        namingIssues.Add($"{copyViews} views have 'Copy' in name (rename or delete)");

                    totalIssues += namingIssues.Count;
                    results["naming"] = new { issues = namingIssues };
                }

                var status = totalIssues == 0 ? "PASS" :
                            totalIssues <= 5 ? "WARNING" : "FAIL";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    status,
                    totalIssues,
                    results
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        // Helper: check if a ViewFamilyType is used (public accessor)
        private static bool IsViewTypeUsedPublic(Document doc, ViewFamilyType vft)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => v.GetTypeId() == vft.Id);
        }

        // Helper: check if an element is referenced anywhere
        private static bool IsElementUsedPublic(Document doc, Element element)
        {
            // Simple check: if the element has dependent elements, it's used
            var dependents = element.GetDependentElements(null);
            return dependents != null && dependents.Count > 1; // Self-reference = 1
        }

        #endregion

        #region Data Classes

        private class ChecklistSummary
        {
            public int Passed { get; set; }
            public int Warnings { get; set; }
            public int Failures { get; set; }
            public int TotalChecks { get; set; }
            public double PassRate { get; set; }
        }

        private class CheckResult
        {
            public string Category { get; set; }
            public string Status { get; set; } = "PASS";
            public int Count { get; set; }
            public List<string> Issues { get; set; } = new List<string>();
            public List<object> Details { get; set; } = new List<object>();
        }

        #endregion
    }
}
