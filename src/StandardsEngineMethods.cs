using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Standards Engine Methods for Predictive Intelligence System
    /// Phase 2: Compare projects against standards and learn from existing projects
    /// </summary>
    public static class StandardsEngineMethods
    {
        // Default standards folder path
        private static readonly string StandardsFolder = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            "..", "standards"
        );

        #region Get Available Standards

        /// <summary>
        /// List all available project standards
        /// </summary>
        public static string GetAvailableStandards(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var standardsPath = parameters?["standardsPath"]?.ToString() ?? StandardsFolder;

                // Also check the project folder
                var projectStandardsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitMCPBridge", "standards"
                );

                var standards = new List<object>();

                // Load from default standards folder
                if (Directory.Exists(standardsPath))
                {
                    foreach (var file in Directory.GetFiles(standardsPath, "*.json"))
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            var std = JObject.Parse(json);
                            standards.Add(new
                            {
                                standardId = std["standardId"]?.ToString(),
                                standardName = std["standardName"]?.ToString(),
                                version = std["version"]?.ToString(),
                                description = std["description"]?.ToString(),
                                filePath = file,
                                source = "default"
                            });
                        }
                        catch { }
                    }
                }

                // Load from project-specific standards
                if (Directory.Exists(projectStandardsPath))
                {
                    foreach (var file in Directory.GetFiles(projectStandardsPath, "*.json"))
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            var std = JObject.Parse(json);
                            standards.Add(new
                            {
                                standardId = std["standardId"]?.ToString(),
                                standardName = std["standardName"]?.ToString(),
                                version = std["version"]?.ToString(),
                                description = std["description"]?.ToString(),
                                filePath = file,
                                source = "custom"
                            });
                        }
                        catch { }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    standardCount = standards.Count,
                    defaultPath = standardsPath,
                    customPath = projectStandardsPath,
                    standards = standards
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Compare to Standards

        /// <summary>
        /// Compare current project against a standard and return compliance report
        /// </summary>
        public static string CompareToStandard(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;
                var standardId = parameters?["standardId"]?.ToString() ?? "residential-single-family";
                var standardPath = parameters?["standardPath"]?.ToString();

                // Load the standard
                JObject standard = null;

                if (!string.IsNullOrEmpty(standardPath) && File.Exists(standardPath))
                {
                    standard = JObject.Parse(File.ReadAllText(standardPath));
                }
                else
                {
                    // Find standard by ID
                    var searchPaths = new[] { StandardsFolder,
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitMCPBridge", "standards") };

                    foreach (var path in searchPaths)
                    {
                        if (!Directory.Exists(path)) continue;
                        foreach (var file in Directory.GetFiles(path, "*.json"))
                        {
                            try
                            {
                                var json = JObject.Parse(File.ReadAllText(file));
                                if (json["standardId"]?.ToString() == standardId)
                                {
                                    standard = json;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (standard != null) break;
                    }
                }

                if (standard == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Standard '{standardId}' not found"
                    });
                }

                // Gather project data
                var projectData = GatherProjectData(doc);

                // Compare against standard
                var complianceReport = CompareProjectToStandard(projectData, standard);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    standardId = standard["standardId"]?.ToString(),
                    standardName = standard["standardName"]?.ToString(),
                    complianceScore = complianceReport.ComplianceScore,
                    totalChecks = complianceReport.TotalChecks,
                    passedChecks = complianceReport.PassedChecks,
                    failedChecks = complianceReport.FailedChecks,
                    summary = new
                    {
                        sheetsCompliance = complianceReport.SheetsCompliance,
                        viewsCompliance = complianceReport.ViewsCompliance,
                        schedulesCompliance = complianceReport.SchedulesCompliance,
                        namingCompliance = complianceReport.NamingCompliance
                    },
                    issues = complianceReport.Issues,
                    recommendations = complianceReport.Recommendations
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Learn From Project

        /// <summary>
        /// Analyze current project and generate a custom standard based on its patterns
        /// </summary>
        public static string LearnFromProject(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;
                var standardName = parameters?["standardName"]?.ToString() ?? $"Learned from {doc.Title}";
                var standardId = parameters?["standardId"]?.ToString() ??
                    $"custom-{DateTime.Now:yyyyMMdd-HHmmss}";
                var saveToFile = parameters?["saveToFile"]?.ToObject<bool>() ?? false;

                // Gather comprehensive project data
                var projectData = GatherProjectData(doc);

                // Generate standard from project patterns
                var learnedStandard = GenerateStandardFromProject(projectData, standardId, standardName, doc.Title);

                // Optionally save to file
                string savedPath = null;
                if (saveToFile)
                {
                    var customStandardsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "RevitMCPBridge", "standards"
                    );
                    Directory.CreateDirectory(customStandardsPath);
                    savedPath = Path.Combine(customStandardsPath, $"{standardId}.json");
                    File.WriteAllText(savedPath, JsonConvert.SerializeObject(learnedStandard, Formatting.Indented));
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Standard learned from project",
                    standardId = standardId,
                    standardName = standardName,
                    savedToFile = saveToFile,
                    filePath = savedPath,
                    learnedPatterns = new
                    {
                        sheetNumberingPattern = learnedStandard["namingConventions"]?["sheets"]?["pattern"],
                        sheetCount = projectData.Sheets.Count,
                        viewCount = projectData.Views.Count,
                        levelCount = projectData.Levels.Count,
                        scheduleCount = projectData.Schedules.Count,
                        detectedDisciplines = learnedStandard["namingConventions"]?["sheets"]?["disciplines"]
                    },
                    standard = learnedStandard
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Predict Next Steps

        /// <summary>
        /// Based on current project state and standards, predict what should be done next
        /// </summary>
        public static string PredictNextSteps(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;
                var standardId = parameters?["standardId"]?.ToString() ?? "residential-single-family";
                var maxSteps = parameters?["maxSteps"]?.ToObject<int>() ?? 10;

                // Get project data
                var projectData = GatherProjectData(doc);

                // Load standard (or use defaults if not found)
                JObject standard = LoadStandard(standardId);

                // Generate predictions
                var predictions = GeneratePredictions(projectData, standard, maxSteps);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    standardUsed = standard?["standardId"]?.ToString() ?? "default",
                    predictionCount = predictions.Count,
                    predictions = predictions
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Classes

        private class ProjectData
        {
            public List<SheetInfo> Sheets { get; set; } = new List<SheetInfo>();
            public List<ViewInfo> Views { get; set; } = new List<ViewInfo>();
            public List<LevelInfo> Levels { get; set; } = new List<LevelInfo>();
            public List<ScheduleInfo> Schedules { get; set; } = new List<ScheduleInfo>();
            public HashSet<int> PlacedViewIds { get; set; } = new HashSet<int>();
        }

        private class SheetInfo
        {
            public int Id { get; set; }
            public string Number { get; set; }
            public string Name { get; set; }
            public List<int> ViewIds { get; set; } = new List<int>();
            public bool IsEmpty => ViewIds.Count == 0;
        }

        private class ViewInfo
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public ViewType Type { get; set; }
            public string TypeName { get; set; }
            public int? LevelId { get; set; }
            public string LevelName { get; set; }
            public bool IsPlaced { get; set; }
        }

        private class LevelInfo
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public double Elevation { get; set; }
            public bool HasFloorPlan { get; set; }
            public bool HasCeilingPlan { get; set; }
        }

        private class ScheduleInfo
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Category { get; set; }
            public bool IsPlaced { get; set; }
        }

        private class ComplianceReport
        {
            public double ComplianceScore { get; set; }
            public int TotalChecks { get; set; }
            public int PassedChecks { get; set; }
            public int FailedChecks { get; set; }
            public double SheetsCompliance { get; set; }
            public double ViewsCompliance { get; set; }
            public double SchedulesCompliance { get; set; }
            public double NamingCompliance { get; set; }
            public List<object> Issues { get; set; } = new List<object>();
            public List<object> Recommendations { get; set; } = new List<object>();
        }

        #endregion

        #region Helper Methods

        private static ProjectData GatherProjectData(Document doc)
        {
            var data = new ProjectData();

            // Get all viewports to track placed views
            var viewports = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .ToList();

            foreach (var vp in viewports)
            {
                data.PlacedViewIds.Add((int)vp.ViewId.Value);
            }

            // Get schedule instances
            var scheduleInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Select(si => (int)si.ScheduleId.Value)
                .ToHashSet();

            // Get sheets
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber)
                .ToList();

            foreach (var sheet in sheets)
            {
                var viewIds = sheet.GetAllViewports()
                    .Select(vpId => doc.GetElement(vpId) as Viewport)
                    .Where(vp => vp != null)
                    .Select(vp => (int)vp.ViewId.Value)
                    .ToList();

                data.Sheets.Add(new SheetInfo
                {
                    Id = (int)sheet.Id.Value,
                    Number = sheet.SheetNumber,
                    Name = sheet.Name,
                    ViewIds = viewIds
                });
            }

            // Get views
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet)
                .ToList();

            foreach (var view in views)
            {
                data.Views.Add(new ViewInfo
                {
                    Id = (int)view.Id.Value,
                    Name = view.Name,
                    Type = view.ViewType,
                    TypeName = view.ViewType.ToString(),
                    LevelId = view.GenLevel != null ? (int?)view.GenLevel.Id.Value : null,
                    LevelName = view.GenLevel?.Name,
                    IsPlaced = data.PlacedViewIds.Contains((int)view.Id.Value)
                });
            }

            // Get levels
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var level in levels)
            {
                var levelViews = data.Views.Where(v => v.LevelId == (int)level.Id.Value).ToList();
                data.Levels.Add(new LevelInfo
                {
                    Id = (int)level.Id.Value,
                    Name = level.Name,
                    Elevation = level.Elevation,
                    HasFloorPlan = levelViews.Any(v => v.Type == ViewType.FloorPlan),
                    HasCeilingPlan = levelViews.Any(v => v.Type == ViewType.CeilingPlan)
                });
            }

            // Get schedules
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .ToList();

            foreach (var schedule in schedules)
            {
                string category = "";
                try
                {
                    var catId = schedule.Definition.CategoryId;
                    if (catId != ElementId.InvalidElementId)
                    {
                        var cat = Category.GetCategory(doc, catId);
                        category = cat?.Name ?? "";
                    }
                }
                catch { }

                data.Schedules.Add(new ScheduleInfo
                {
                    Id = (int)schedule.Id.Value,
                    Name = schedule.Name,
                    Category = category,
                    IsPlaced = scheduleInstances.Contains((int)schedule.Id.Value)
                });
            }

            return data;
        }

        private static ComplianceReport CompareProjectToStandard(ProjectData project, JObject standard)
        {
            var report = new ComplianceReport();
            int totalChecks = 0;
            int passedChecks = 0;

            // Check schedules
            var requiredSchedules = standard["scheduleRequirements"]?["required"]?.ToObject<JArray>() ?? new JArray();
            foreach (var reqSchedule in requiredSchedules)
            {
                totalChecks++;
                var scheduleType = reqSchedule["type"]?.ToString() ?? "";
                var scheduleName = reqSchedule["name"]?.ToString() ?? "";
                var mustBePlaced = reqSchedule["mustBePlaced"]?.ToObject<bool>() ?? false;

                // Find matching schedule
                var found = project.Schedules.FirstOrDefault(s =>
                    s.Name.IndexOf(scheduleName.Replace(" Schedule", ""), StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Name.IndexOf(scheduleType.Replace("_", " "), StringComparison.OrdinalIgnoreCase) >= 0);

                if (found != null)
                {
                    if (!mustBePlaced || found.IsPlaced)
                    {
                        passedChecks++;
                    }
                    else
                    {
                        report.Issues.Add(new
                        {
                            type = "schedule_not_placed",
                            severity = "high",
                            description = $"Schedule '{found.Name}' exists but is not placed on any sheet",
                            scheduleId = found.Id,
                            recommendation = $"Place '{found.Name}' on appropriate schedule sheet"
                        });
                    }
                }
                else
                {
                    report.Issues.Add(new
                    {
                        type = "missing_schedule",
                        severity = "high",
                        description = $"Required schedule '{scheduleName}' not found",
                        recommendation = $"Create {scheduleName}"
                    });
                }
            }

            // Check elevations
            var requiredDirections = new[] { "North", "South", "East", "West" };
            var elevationViews = project.Views.Where(v => v.Type == ViewType.Elevation).ToList();

            foreach (var dir in requiredDirections)
            {
                totalChecks++;
                if (elevationViews.Any(e => e.Name.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    passedChecks++;
                }
                else
                {
                    report.Issues.Add(new
                    {
                        type = "missing_elevation",
                        severity = "high",
                        description = $"{dir} Elevation view not found",
                        recommendation = $"Create {dir} Elevation view"
                    });
                }
            }

            // Check for empty sheets
            foreach (var sheet in project.Sheets.Where(s => s.IsEmpty))
            {
                totalChecks++;
                report.Issues.Add(new
                {
                    type = "empty_sheet",
                    severity = "medium",
                    description = $"Sheet {sheet.Number} '{sheet.Name}' has no views",
                    sheetId = sheet.Id,
                    recommendation = "Add views or delete empty sheet"
                });
            }

            // Check level coverage
            var viewRequirements = standard["viewRequirements"];
            foreach (var level in project.Levels)
            {
                // Skip utility levels
                if (level.Name.Contains("T.O.") || level.Name.Contains("Parapet") ||
                    level.Name.Contains("NOT USED") || level.Name.Contains("GRADE"))
                    continue;

                totalChecks++;
                if (level.HasFloorPlan)
                {
                    passedChecks++;
                }
                else
                {
                    report.Issues.Add(new
                    {
                        type = "missing_floor_plan",
                        severity = "high",
                        description = $"Level '{level.Name}' has no floor plan",
                        levelId = level.Id,
                        recommendation = $"Create floor plan for {level.Name}"
                    });
                }

                totalChecks++;
                if (level.HasCeilingPlan)
                {
                    passedChecks++;
                }
                else
                {
                    report.Issues.Add(new
                    {
                        type = "missing_ceiling_plan",
                        severity = "medium",
                        description = $"Level '{level.Name}' has no ceiling plan (RCP)",
                        levelId = level.Id,
                        recommendation = $"Create RCP for {level.Name}"
                    });
                }
            }

            // Calculate scores
            report.TotalChecks = totalChecks;
            report.PassedChecks = passedChecks;
            report.FailedChecks = totalChecks - passedChecks;
            report.ComplianceScore = totalChecks > 0 ? Math.Round((double)passedChecks / totalChecks * 100, 1) : 100;

            // Component scores (simplified)
            report.SheetsCompliance = project.Sheets.Count(s => !s.IsEmpty) * 100.0 / Math.Max(1, project.Sheets.Count);
            report.ViewsCompliance = project.Views.Count(v => v.IsPlaced) * 100.0 / Math.Max(1, project.Views.Count(v =>
                v.Type == ViewType.FloorPlan || v.Type == ViewType.Elevation || v.Type == ViewType.Section));
            report.SchedulesCompliance = project.Schedules.Count(s => s.IsPlaced) * 100.0 / Math.Max(1, project.Schedules.Count);
            report.NamingCompliance = 85; // Placeholder - would need pattern matching

            // Generate recommendations
            report.Recommendations = report.Issues
                .Cast<dynamic>()
                .OrderBy(i => i.severity == "high" ? 0 : 1)
                .Take(5)
                .Select(i => (object)new { priority = i.severity, action = i.recommendation })
                .ToList();

            return report;
        }

        private static JObject GenerateStandardFromProject(ProjectData project, string standardId, string standardName, string projectTitle)
        {
            // Detect sheet numbering pattern
            var sheetNumbers = project.Sheets.Select(s => s.Number).ToList();
            var sheetPattern = DetectSheetPattern(sheetNumbers);
            var disciplines = DetectDisciplines(sheetNumbers);

            // Detect view naming patterns
            var floorPlanNames = project.Views.Where(v => v.Type == ViewType.FloorPlan).Select(v => v.Name).ToList();
            var elevationNames = project.Views.Where(v => v.Type == ViewType.Elevation).Select(v => v.Name).ToList();

            var standard = new JObject
            {
                ["$schema"] = "project-standard-schema.json",
                ["standardId"] = standardId,
                ["standardName"] = standardName,
                ["version"] = "1.0.0",
                ["description"] = $"Standard learned from {projectTitle}",
                ["learnedFrom"] = new JObject
                {
                    ["projectName"] = projectTitle,
                    ["learnedAt"] = DateTime.Now.ToString("o"),
                    ["sheetCount"] = project.Sheets.Count,
                    ["viewCount"] = project.Views.Count,
                    ["levelCount"] = project.Levels.Count
                },
                ["sheetRequirements"] = GenerateSheetRequirements(project),
                ["viewRequirements"] = GenerateViewRequirements(project),
                ["scheduleRequirements"] = GenerateScheduleRequirements(project),
                ["namingConventions"] = new JObject
                {
                    ["sheets"] = new JObject
                    {
                        ["pattern"] = sheetPattern,
                        ["disciplines"] = disciplines,
                        ["examples"] = new JArray(sheetNumbers.Take(5))
                    },
                    ["views"] = new JObject
                    {
                        ["floorPlan"] = DetectCommonPattern(floorPlanNames),
                        ["elevation"] = DetectCommonPattern(elevationNames)
                    }
                },
                ["metadata"] = new JObject
                {
                    ["createdBy"] = "RevitMCPBridge Predictive Intelligence",
                    ["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd"),
                    ["source"] = "learned"
                }
            };

            return standard;
        }

        private static JObject GenerateSheetRequirements(ProjectData project)
        {
            var requirements = new JObject();

            // Group sheets by discipline prefix
            var sheetGroups = project.Sheets
                .GroupBy(s => Regex.Match(s.Number, @"^[A-Z]+").Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var group in sheetGroups)
            {
                var sheets = new JArray();
                foreach (var sheet in group.Value)
                {
                    sheets.Add(new JObject
                    {
                        ["numberPattern"] = sheet.Number,
                        ["name"] = sheet.Name,
                        ["viewCount"] = sheet.ViewIds.Count
                    });
                }

                requirements[group.Key.ToLower() + "_sheets"] = new JObject
                {
                    ["required"] = true,
                    ["sheets"] = sheets
                };
            }

            return requirements;
        }

        private static JObject GenerateViewRequirements(ProjectData project)
        {
            return new JObject
            {
                ["floorPlans"] = new JObject
                {
                    ["required"] = true,
                    ["count"] = project.Views.Count(v => v.Type == ViewType.FloorPlan),
                    ["perLevel"] = true
                },
                ["ceilingPlans"] = new JObject
                {
                    ["required"] = true,
                    ["count"] = project.Views.Count(v => v.Type == ViewType.CeilingPlan)
                },
                ["elevations"] = new JObject
                {
                    ["required"] = true,
                    ["count"] = project.Views.Count(v => v.Type == ViewType.Elevation)
                },
                ["sections"] = new JObject
                {
                    ["required"] = true,
                    ["count"] = project.Views.Count(v => v.Type == ViewType.Section)
                }
            };
        }

        private static JObject GenerateScheduleRequirements(ProjectData project)
        {
            var required = new JArray();
            var optional = new JArray();

            foreach (var schedule in project.Schedules)
            {
                var scheduleObj = new JObject
                {
                    ["name"] = schedule.Name,
                    ["category"] = schedule.Category,
                    ["mustBePlaced"] = schedule.IsPlaced
                };

                // Important schedules go to required
                var name = schedule.Name.ToLower();
                if (name.Contains("door") || name.Contains("window") || name.Contains("room") || name.Contains("finish"))
                {
                    required.Add(scheduleObj);
                }
                else
                {
                    optional.Add(scheduleObj);
                }
            }

            return new JObject
            {
                ["required"] = required,
                ["optional"] = optional
            };
        }

        private static string DetectSheetPattern(List<string> sheetNumbers)
        {
            if (sheetNumbers.Count == 0) return "unknown";

            var hasDot = sheetNumbers.Any(s => s.Contains("."));
            var hasDash = sheetNumbers.Any(s => s.Contains("-"));

            if (hasDot) return "DISCIPLINE#.#";
            if (hasDash) return "DISCIPLINE-#.#";
            return "DISCIPLINE###";
        }

        private static JObject DetectDisciplines(List<string> sheetNumbers)
        {
            var disciplines = new JObject();
            var prefixes = sheetNumbers
                .Select(s => Regex.Match(s, @"^[A-Z]+").Value)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct();

            var disciplineMap = new Dictionary<string, string>
            {
                { "A", "Architectural" },
                { "S", "Structural" },
                { "M", "Mechanical" },
                { "E", "Electrical" },
                { "P", "Plumbing" },
                { "G", "General" },
                { "C", "Civil" },
                { "L", "Landscape" }
            };

            foreach (var prefix in prefixes)
            {
                disciplines[prefix] = disciplineMap.ContainsKey(prefix) ? disciplineMap[prefix] : "Unknown";
            }

            return disciplines;
        }

        private static string DetectCommonPattern(List<string> names)
        {
            if (names.Count == 0) return "none";
            if (names.Count == 1) return names[0];

            // Find common substrings
            var words = names.SelectMany(n => n.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                .GroupBy(w => w.ToLower())
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key);

            return string.Join(" + ", words);
        }

        private static JObject LoadStandard(string standardId)
        {
            var searchPaths = new[] { StandardsFolder,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitMCPBridge", "standards") };

            foreach (var path in searchPaths)
            {
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.GetFiles(path, "*.json"))
                {
                    try
                    {
                        var json = JObject.Parse(File.ReadAllText(file));
                        if (json["standardId"]?.ToString() == standardId)
                        {
                            return json;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private static List<object> GeneratePredictions(ProjectData project, JObject standard, int maxSteps)
        {
            var predictions = new List<object>();
            int priority = 1;

            // Predict: Place unplaced schedules
            var importantSchedules = project.Schedules
                .Where(s => !s.IsPlaced)
                .Where(s => s.Name.ToLower().Contains("door") || s.Name.ToLower().Contains("window") ||
                           s.Name.ToLower().Contains("room") || s.Name.ToLower().Contains("finish"))
                .ToList();

            foreach (var schedule in importantSchedules.Take(3))
            {
                predictions.Add(new
                {
                    priority = priority++,
                    action = "place_schedule",
                    description = $"Place '{schedule.Name}' on schedule sheet",
                    confidence = 0.95,
                    elementId = schedule.Id,
                    reasoning = "Required schedule exists but is not placed on any sheet",
                    canAutoExecute = true
                });
            }

            // Predict: Create missing elevations
            var elevations = project.Views.Where(v => v.Type == ViewType.Elevation).ToList();
            var directions = new[] { "North", "South", "East", "West" };
            foreach (var dir in directions)
            {
                if (!elevations.Any(e => e.Name.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    predictions.Add(new
                    {
                        priority = priority++,
                        action = "create_elevation",
                        description = $"Create {dir} Elevation view",
                        confidence = 0.90,
                        reasoning = $"3 of 4 cardinal elevations exist, {dir} is missing",
                        canAutoExecute = false,
                        parameters = new { direction = dir }
                    });
                }
            }

            // Predict: Fill empty sheets or delete them
            foreach (var sheet in project.Sheets.Where(s => s.IsEmpty).Take(2))
            {
                predictions.Add(new
                {
                    priority = priority++,
                    action = "resolve_empty_sheet",
                    description = $"Sheet {sheet.Number} '{sheet.Name}' is empty - add views or delete",
                    confidence = 0.85,
                    elementId = sheet.Id,
                    reasoning = "Empty sheets indicate incomplete documentation",
                    canAutoExecute = false,
                    options = new[] { "add_views", "delete_sheet" }
                });
            }

            // Predict: Place important unplaced views
            var unplacedImportant = project.Views
                .Where(v => !v.IsPlaced)
                .Where(v => v.Type == ViewType.FloorPlan || v.Type == ViewType.Elevation || v.Type == ViewType.Section)
                .Take(3);

            foreach (var view in unplacedImportant)
            {
                predictions.Add(new
                {
                    priority = priority++,
                    action = "place_view",
                    description = $"Place '{view.Name}' ({view.TypeName}) on appropriate sheet",
                    confidence = 0.80,
                    elementId = view.Id,
                    reasoning = $"{view.TypeName} should typically be included in documentation",
                    canAutoExecute = true
                });
            }

            return predictions.Take(maxSteps).ToList();
        }

        #endregion
    }
}
