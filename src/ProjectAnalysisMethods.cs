using System;
using System.Collections.Generic;
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
    /// Project Analysis Methods for Predictive Intelligence System
    /// Phase 1: Deep project scanning and state analysis
    /// </summary>
    public static class ProjectAnalysisMethods
    {
        #region Master Analysis Method

        /// <summary>
        /// Comprehensive project state analysis - returns full project model
        /// This is the master method that provides complete project overview
        /// </summary>
        [MCPMethod("analyzeProjectState", Category = "ProjectAnalysis", Description = "Comprehensive project state analysis")]
        public static string AnalyzeProjectState(UIApplication uiApp, JObject parameters)
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
                var includeDetails = parameters?["includeDetails"]?.ToObject<bool>() ?? true;

                // Gather all project data
                var sheets = GetAllSheetsData(doc);
                var views = GetAllViewsData(doc);
                var levels = GetAllLevelsData(doc);
                var schedules = GetAllSchedulesData(doc);
                var titleblocks = GetTitleblockTypes(doc);

                // Calculate statistics
                var placedViewIds = sheets.SelectMany(s => s.PlacedViewIds).Distinct().ToList();
                var unplacedViews = views.Where(v => !placedViewIds.Contains(v.ViewId) && v.CanBePlaced).ToList();
                var emptySheets = sheets.Where(s => s.ViewportCount == 0).ToList();

                // Build level coverage analysis
                var levelCoverage = AnalyzeLevelCoverage(levels, views);

                // Detect naming patterns
                var namingPatterns = DetectNamingPatternsInternal(sheets, views);

                // Build the comprehensive state object
                var projectState = new
                {
                    success = true,
                    analyzedAt = DateTime.Now.ToString("o"),
                    projectInfo = new
                    {
                        name = doc.Title,
                        path = doc.PathName,
                        isWorkshared = doc.IsWorkshared
                    },
                    summary = new
                    {
                        sheetCount = sheets.Count,
                        emptySheetCount = emptySheets.Count,
                        viewCount = views.Count,
                        placedViewCount = placedViewIds.Count,
                        unplacedViewCount = unplacedViews.Count,
                        levelCount = levels.Count,
                        scheduleCount = schedules.Count,
                        placedScheduleCount = schedules.Count(s => s.IsPlaced),
                        titleblockCount = titleblocks.Count
                    },
                    sheets = includeDetails ? sheets.Select(s => new
                    {
                        s.SheetId,
                        s.SheetNumber,
                        s.SheetName,
                        s.TitleblockId,
                        s.TitleblockName,
                        s.ViewportCount,
                        s.PlacedViewIds,
                        s.PlacedViewNames,
                        isEmpty = s.ViewportCount == 0
                    }) : null,
                    views = includeDetails ? views.Select(v => new
                    {
                        v.ViewId,
                        v.ViewName,
                        v.ViewType,
                        v.ViewTypeName,
                        v.AssociatedLevelId,
                        v.AssociatedLevelName,
                        v.IsPlaced,
                        v.PlacedOnSheetId,
                        v.PlacedOnSheetNumber,
                        v.CanBePlaced,
                        v.IsTemplate,
                        v.Scale
                    }) : null,
                    levels = levels.Select(l => new
                    {
                        l.LevelId,
                        l.LevelName,
                        l.Elevation,
                        l.HasFloorPlan,
                        l.HasCeilingPlan,
                        l.FloorPlanViewId,
                        l.CeilingPlanViewId,
                        l.AssociatedViewCount
                    }),
                    schedules = schedules.Select(s => new
                    {
                        s.ScheduleId,
                        s.ScheduleName,
                        s.ScheduleCategory,
                        s.IsPlaced,
                        s.PlacedOnSheetId,
                        s.PlacedOnSheetNumber
                    }),
                    unplacedViews = unplacedViews.Select(v => new
                    {
                        v.ViewId,
                        v.ViewName,
                        v.ViewType,
                        v.ViewTypeName
                    }),
                    emptySheets = emptySheets.Select(s => new
                    {
                        s.SheetId,
                        s.SheetNumber,
                        s.SheetName
                    }),
                    levelCoverage = levelCoverage,
                    namingPatterns = namingPatterns,
                    titleblocks = titleblocks.Select(t => new
                    {
                        t.TypeId,
                        t.TypeName,
                        t.FamilyName,
                        t.UsageCount
                    })
                };

                return JsonConvert.SerializeObject(projectState, Formatting.None);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Sheet-View Matrix

        /// <summary>
        /// Get the complete sheet-to-view relationship matrix
        /// Shows which views are on which sheets
        /// </summary>
        [MCPMethod("getSheetViewMatrix", Category = "ProjectAnalysis", Description = "Get the complete sheet-to-view relationship matrix")]
        public static string GetSheetViewMatrix(UIApplication uiApp, JObject parameters)
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

                // Get all sheets with their viewports
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .OrderBy(s => s.SheetNumber)
                    .ToList();

                var matrix = new List<object>();

                foreach (var sheet in sheets)
                {
                    var viewportIds = sheet.GetAllViewports();
                    var viewsOnSheet = new List<object>();

                    foreach (var vpId in viewportIds)
                    {
                        var viewport = doc.GetElement(vpId) as Viewport;
                        if (viewport != null)
                        {
                            var view = doc.GetElement(viewport.ViewId) as View;
                            var location = viewport.GetBoxCenter();

                            viewsOnSheet.Add(new
                            {
                                viewportId = (int)viewport.Id.Value,
                                viewId = (int)viewport.ViewId.Value,
                                viewName = view?.Name ?? "",
                                viewType = view?.ViewType.ToString() ?? "",
                                location = new { x = location.X, y = location.Y },
                                scale = view?.Scale ?? 0
                            });
                        }
                    }

                    matrix.Add(new
                    {
                        sheetId = (int)sheet.Id.Value,
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        viewCount = viewsOnSheet.Count,
                        views = viewsOnSheet
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetCount = sheets.Count,
                    matrix = matrix
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region View Placement Status

        /// <summary>
        /// Get detailed view placement status - which views are placed and where
        /// </summary>
        [MCPMethod("getViewPlacementStatus", Category = "ProjectAnalysis", Description = "Get detailed view placement status")]
        public static string GetViewPlacementStatus(UIApplication uiApp, JObject parameters)
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
                var filterType = parameters?["viewType"]?.ToString(); // Optional filter

                // Get all viewports to find placed views
                var viewports = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .ToList();

                var placedViewIds = new Dictionary<int, List<object>>();
                foreach (var vp in viewports)
                {
                    var viewId = (int)vp.ViewId.Value;
                    var sheet = doc.GetElement(vp.SheetId) as ViewSheet;

                    if (!placedViewIds.ContainsKey(viewId))
                        placedViewIds[viewId] = new List<object>();

                    placedViewIds[viewId].Add(new
                    {
                        sheetId = (int)vp.SheetId.Value,
                        sheetNumber = sheet?.SheetNumber ?? "",
                        viewportId = (int)vp.Id.Value
                    });
                }

                // Get all views
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet)
                    .ToList();

                if (!string.IsNullOrEmpty(filterType))
                {
                    if (Enum.TryParse<ViewType>(filterType, true, out var vt))
                    {
                        views = views.Where(v => v.ViewType == vt).ToList();
                    }
                }

                var placed = new List<object>();
                var unplaced = new List<object>();

                foreach (var view in views)
                {
                    var viewId = (int)view.Id.Value;
                    var canPlace = Viewport.CanAddViewToSheet(doc, ElementId.InvalidElementId, view.Id);

                    var viewInfo = new
                    {
                        viewId = viewId,
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        scale = view.Scale,
                        canBePlaced = canPlace
                    };

                    if (placedViewIds.ContainsKey(viewId))
                    {
                        placed.Add(new
                        {
                            view = viewInfo,
                            placements = placedViewIds[viewId]
                        });
                    }
                    else if (canPlace)
                    {
                        unplaced.Add(viewInfo);
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalViews = views.Count,
                    placedCount = placed.Count,
                    unplacedCount = unplaced.Count,
                    placed = placed,
                    unplaced = unplaced
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Level View Coverage

        /// <summary>
        /// Analyze what views exist for each level
        /// Critical for detecting missing floor plans, RCPs, etc.
        /// </summary>
        [MCPMethod("getLevelViewCoverage", Category = "ProjectAnalysis", Description = "Analyze what views exist for each level")]
        public static string GetLevelViewCoverage(UIApplication uiApp, JObject parameters)
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

                // Get all levels
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                // Get all views
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                var coverage = new List<object>();

                foreach (var level in levels)
                {
                    var levelId = (int)level.Id.Value;
                    var levelViews = views.Where(v => v.GenLevel?.Id.Value == level.Id.Value).ToList();

                    var floorPlans = levelViews.Where(v => v.ViewType == ViewType.FloorPlan).ToList();
                    var ceilingPlans = levelViews.Where(v => v.ViewType == ViewType.CeilingPlan).ToList();
                    var sections = views.Where(v => v.ViewType == ViewType.Section).ToList(); // Sections aren't level-specific
                    var structuralPlans = levelViews.Where(v => v.ViewType == ViewType.EngineeringPlan).ToList();
                    var areaPlans = levelViews.Where(v => v.ViewType == ViewType.AreaPlan).ToList();

                    coverage.Add(new
                    {
                        levelId = levelId,
                        levelName = level.Name,
                        elevation = level.Elevation,
                        totalViews = levelViews.Count,
                        floorPlans = new
                        {
                            count = floorPlans.Count,
                            views = floorPlans.Select(v => new { viewId = (int)v.Id.Value, name = v.Name })
                        },
                        ceilingPlans = new
                        {
                            count = ceilingPlans.Count,
                            views = ceilingPlans.Select(v => new { viewId = (int)v.Id.Value, name = v.Name })
                        },
                        structuralPlans = new
                        {
                            count = structuralPlans.Count,
                            views = structuralPlans.Select(v => new { viewId = (int)v.Id.Value, name = v.Name })
                        },
                        areaPlans = new
                        {
                            count = areaPlans.Count,
                            views = areaPlans.Select(v => new { viewId = (int)v.Id.Value, name = v.Name })
                        },
                        gaps = new
                        {
                            missingFloorPlan = floorPlans.Count == 0,
                            missingCeilingPlan = ceilingPlans.Count == 0
                        }
                    });
                }

                // Also check for elevations (not level-specific but important)
                var elevations = views.Where(v => v.ViewType == ViewType.Elevation).ToList();
                var elevationDirections = new[] { "North", "South", "East", "West" };
                var foundDirections = elevationDirections.Where(dir =>
                    elevations.Any(e => e.Name.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                var missingDirections = elevationDirections.Except(foundDirections).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelCount = levels.Count,
                    coverage = coverage,
                    elevationStatus = new
                    {
                        totalElevations = elevations.Count,
                        foundDirections = foundDirections,
                        missingDirections = missingDirections,
                        elevations = elevations.Select(e => new { viewId = (int)e.Id.Value, name = e.Name })
                    },
                    sectionStatus = new
                    {
                        totalSections = views.Count(v => v.ViewType == ViewType.Section),
                        sections = views.Where(v => v.ViewType == ViewType.Section)
                            .Select(s => new { viewId = (int)s.Id.Value, name = s.Name })
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Naming Pattern Detection

        /// <summary>
        /// Detect naming patterns used in the project
        /// Helps understand conventions for consistent suggestions
        /// </summary>
        [MCPMethod("detectNamingPatterns", Category = "ProjectAnalysis", Description = "Detect naming patterns used in the project")]
        public static string DetectNamingPatterns(UIApplication uiApp, JObject parameters)
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

                var sheets = GetAllSheetsData(doc);
                var views = GetAllViewsData(doc);

                var patterns = DetectNamingPatternsInternal(sheets, views);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    patterns = patterns
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Gap Analysis

        /// <summary>
        /// Smart gap analysis focused on CD SET COMPLETENESS, not just unplaced items.
        /// Compares project against Construction Document requirements.
        /// Working views and draft schedules are intentionally ignored - only flags
        /// REQUIRED elements that are missing or not placed on sheets.
        /// </summary>
        [MCPMethod("analyzeProjectGaps", Category = "ProjectAnalysis", Description = "Analyze project gaps for CD set completeness")]
        public static string AnalyzeProjectGaps(UIApplication uiApp, JObject parameters)
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
                var gaps = new List<object>();
                var completedChecks = new List<string>();
                int priority = 1;

                // Non-habitable level names to exclude from FLOOR PLAN coverage checks
                // These levels don't need floor plans on sheets
                var excludedLevelPatterns = new[] {
                    "T.O.F.", "T.O. FOOTING", "TOP OF FOOTING", "PARAPET",
                    "T.O. ROOF", "ROOF STRUCTURE", "NOT USED", "GRADE",
                    "T.O. PLATE", "T.O. WALL", "FOUNDATION", "ROOF"
                };

                // Get all project data
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .ToList();

                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet)
                    .ToList();

                var placedViewIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Select(vp => vp.ViewId.Value)
                    .ToHashSet();

                var schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                    .ToList();

                var placedScheduleIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .Select(si => si.ScheduleId.Value)
                    .ToHashSet();

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                // Filter to habitable levels only
                var habitableLevels = levels.Where(l =>
                    !excludedLevelPatterns.Any(p =>
                        l.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                // ============================================================
                // CD SET COMPLETENESS CHECKS - What a complete set REQUIRES
                // ============================================================

                // 1. COVER SHEET CHECK - Required for any CD set
                var hasCoverSheet = sheets.Any(s =>
                    s.SheetNumber.Contains("0.0") ||
                    s.SheetNumber.Contains("001") ||
                    s.Name.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!hasCoverSheet)
                {
                    gaps.Add(CreateGap(ref priority, "missing_cover_sheet", "high",
                        "No Cover Sheet found in project",
                        null, "create_sheet",
                        new { requirement = "CD sets require a cover sheet (typically A0.0 or G001)" }));
                }
                else
                {
                    completedChecks.Add("Cover sheet exists");
                }

                // 2. ELEVATION REQUIREMENT - All 4 cardinal directions required
                var elevations = views.Where(v => v.ViewType == ViewType.Elevation).ToList();
                var directions = new[] { "North", "South", "East", "West" };
                var missingDirections = new List<string>();
                var placedElevations = new List<string>();

                foreach (var dir in directions)
                {
                    var dirElevation = elevations.FirstOrDefault(e =>
                        e.Name.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (dirElevation == null)
                    {
                        missingDirections.Add(dir);
                    }
                    else if (!placedViewIds.Contains(dirElevation.Id.Value))
                    {
                        // Elevation exists but not placed - this IS a gap (required view)
                        gaps.Add(CreateGap(ref priority, "elevation_not_placed", "high",
                            $"{dir} Elevation exists but is not placed on any sheet",
                            (int)dirElevation.Id.Value, "place_on_sheet",
                            new { direction = dir, viewName = dirElevation.Name }));
                    }
                    else
                    {
                        placedElevations.Add(dir);
                    }
                }

                if (missingDirections.Count > 0)
                {
                    gaps.Add(CreateGap(ref priority, "missing_elevations", "high",
                        $"Missing elevation views: {string.Join(", ", missingDirections)}",
                        null, "create_elevations",
                        new { missingDirections = missingDirections, requirement = "CD sets require all 4 cardinal elevations" }));
                }

                if (placedElevations.Count == 4)
                    completedChecks.Add("All 4 elevations placed");

                // 3. FLOOR PLAN COVERAGE - One per habitable level, must be placed
                foreach (var level in habitableLevels)
                {
                    var levelFloorPlans = views.Where(v =>
                        v.ViewType == ViewType.FloorPlan &&
                        v.GenLevel?.Id.Value == level.Id.Value).ToList();

                    if (levelFloorPlans.Count == 0)
                    {
                        gaps.Add(CreateGap(ref priority, "missing_floor_plan", "high",
                            $"Level '{level.Name}' has no floor plan view",
                            (int)level.Id.Value, "create_floor_plan",
                            new { levelName = level.Name, requirement = "Each habitable level needs a floor plan" }));
                    }
                    else
                    {
                        // Check if at least ONE floor plan for this level is placed
                        var hasPlacedPlan = levelFloorPlans.Any(v => placedViewIds.Contains(v.Id.Value));
                        if (!hasPlacedPlan)
                        {
                            gaps.Add(CreateGap(ref priority, "floor_plan_not_placed", "high",
                                $"Level '{level.Name}' has floor plan(s) but none are placed on sheets",
                                (int)levelFloorPlans.First().Id.Value, "place_on_sheet",
                                new { levelName = level.Name, availablePlans = levelFloorPlans.Select(v => v.Name).ToList() }));
                        }
                    }
                }

                // 3b. ROOF PLAN CHECK - CD sets need a roof plan (separate from floor plans)
                var roofPlanViews = views.Where(v =>
                    v.Name.IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (v.Name.IndexOf("plan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan))
                    .ToList();

                if (roofPlanViews.Count == 0)
                {
                    gaps.Add(CreateGap(ref priority, "missing_roof_plan", "high",
                        "No Roof Plan view found in project",
                        null, "create_roof_plan",
                        new { requirement = "CD sets require a roof plan view" }));
                }
                else
                {
                    var hasPlacedRoofPlan = roofPlanViews.Any(v => placedViewIds.Contains(v.Id.Value));
                    if (!hasPlacedRoofPlan)
                    {
                        gaps.Add(CreateGap(ref priority, "roof_plan_not_placed", "medium",
                            "Roof Plan exists but is not placed on any sheet",
                            (int)roofPlanViews.First().Id.Value, "place_on_sheet",
                            new { availableRoofPlans = roofPlanViews.Select(v => v.Name).ToList() }));
                    }
                    else
                    {
                        completedChecks.Add("Roof plan placed");
                    }
                }

                // 4. BUILDING SECTIONS - Minimum 2 required
                var sections = views.Where(v => v.ViewType == ViewType.Section).ToList();
                var placedSections = sections.Where(s => placedViewIds.Contains(s.Id.Value)).ToList();

                if (sections.Count < 2)
                {
                    gaps.Add(CreateGap(ref priority, "insufficient_sections", "high",
                        $"Only {sections.Count} building section(s) exist. Minimum 2 required.",
                        null, "create_sections",
                        new { existingCount = sections.Count, requirement = "CD sets require at least 2 building sections" }));
                }
                else if (placedSections.Count < 2)
                {
                    gaps.Add(CreateGap(ref priority, "sections_not_placed", "medium",
                        $"{sections.Count} sections exist but only {placedSections.Count} placed on sheets",
                        null, "place_sections",
                        new { existingCount = sections.Count, placedCount = placedSections.Count }));
                }
                else
                {
                    completedChecks.Add($"{placedSections.Count} building sections placed");
                }

                // 5. REQUIRED SCHEDULES - Check for existence AND placement
                var requiredScheduleTypes = new Dictionary<string, string[]>
                {
                    { "Door Schedule", new[] { "door schedule", "door sched" } },
                    { "Window Schedule", new[] { "window schedule", "window sched" } },
                    { "Room Finish Schedule", new[] { "room finish", "finish schedule", "room schedule" } }
                };

                foreach (var reqSchedule in requiredScheduleTypes)
                {
                    var matchingSchedules = schedules.Where(s =>
                        reqSchedule.Value.Any(pattern =>
                            s.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
                        .ToList();

                    if (matchingSchedules.Count == 0)
                    {
                        gaps.Add(CreateGap(ref priority, "missing_schedule", "high",
                            $"No {reqSchedule.Key} found in project",
                            null, "create_schedule",
                            new { scheduleType = reqSchedule.Key, requirement = $"{reqSchedule.Key} is required for CD sets" }));
                    }
                    else
                    {
                        // Check if at least one is placed
                        var hasPlaced = matchingSchedules.Any(s => placedScheduleIds.Contains(s.Id.Value));
                        if (!hasPlaced)
                        {
                            gaps.Add(CreateGap(ref priority, "schedule_not_placed", "medium",
                                $"{reqSchedule.Key} exists but is not placed on any sheet",
                                (int)matchingSchedules.First().Id.Value, "place_on_sheet",
                                new { scheduleType = reqSchedule.Key, availableSchedules = matchingSchedules.Select(s => s.Name).ToList() }));
                        }
                        else
                        {
                            completedChecks.Add($"{reqSchedule.Key} placed");
                        }
                    }
                }

                // 6. CONSULTANT COORDINATION CHECK - Are there sheets for other disciplines?
                var sheetNumbers = sheets.Select(s => s.SheetNumber.ToUpper()).ToList();
                var consultantStatus = new Dictionary<string, object>();

                // Structural
                var hasStructural = sheetNumbers.Any(n => n.StartsWith("S"));
                consultantStatus["structural"] = new { hasSheets = hasStructural, prefix = "S" };

                // Mechanical
                var hasMechanical = sheetNumbers.Any(n => n.StartsWith("M"));
                consultantStatus["mechanical"] = new { hasSheets = hasMechanical, prefix = "M" };

                // Electrical
                var hasElectrical = sheetNumbers.Any(n => n.StartsWith("E"));
                consultantStatus["electrical"] = new { hasSheets = hasElectrical, prefix = "E" };

                // Plumbing
                var hasPlumbing = sheetNumbers.Any(n => n.StartsWith("P"));
                consultantStatus["plumbing"] = new { hasSheets = hasPlumbing, prefix = "P" };

                // Civil (C-series or has site plan)
                var hasCivil = sheetNumbers.Any(n => n.StartsWith("C")) ||
                               sheets.Any(s => s.Name.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0);
                consultantStatus["civil"] = new { hasSheets = hasCivil, prefix = "C" };

                // 7. SHEET ORGANIZATION CHECK - Do we have logical sheet groupings?
                var hasGeneralSheets = sheetNumbers.Any(n => n.StartsWith("G") || n.Contains("0.0") || n.Contains("0.1"));
                var hasPlanSheets = sheetNumbers.Any(n => n.Contains("1.") || n.Contains("2."));
                var hasElevationSheets = sheetNumbers.Any(n => n.Contains("4.") || n.Contains("5."));
                var hasDetailSheets = sheetNumbers.Any(n => n.Contains("9.") || n.Contains("8."));

                // Sort gaps by severity
                var sortedGaps = gaps.Cast<dynamic>()
                    .OrderBy(g => g.severity == "high" ? 0 : g.severity == "medium" ? 1 : 2)
                    .ThenBy(g => g.priority)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    analysisType = "CD_SET_COMPLETENESS",
                    philosophy = "Checking what a complete Construction Document set REQUIRES, not flagging all unplaced items",
                    projectName = doc.Title,
                    gapCount = gaps.Count,
                    highPriority = gaps.Cast<dynamic>().Count(g => g.severity == "high"),
                    mediumPriority = gaps.Cast<dynamic>().Count(g => g.severity == "medium"),
                    lowPriority = gaps.Cast<dynamic>().Count(g => g.severity == "low"),
                    completedChecks = completedChecks,
                    gaps = sortedGaps,
                    levelInfo = new
                    {
                        totalLevels = levels.Count,
                        habitableLevels = habitableLevels.Count,
                        excludedLevels = levels.Where(l =>
                            excludedLevelPatterns.Any(p =>
                                l.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                            .Select(l => l.Name).ToList()
                    },
                    consultantCoordination = consultantStatus,
                    sheetOrganization = new
                    {
                        hasGeneralSheets,
                        hasPlanSheets,
                        hasElevationSheets,
                        hasDetailSheets,
                        totalSheets = sheets.Count,
                        disciplines = sheetNumbers.Select(n => n.Length > 0 ? n[0].ToString() : "?")
                            .Distinct().OrderBy(d => d).ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Helper to create standardized gap objects
        /// </summary>
        private static object CreateGap(ref int priority, string type, string severity,
            string description, int? elementId, string suggestedAction, object context)
        {
            return new
            {
                id = $"gap_{priority}",
                type = type,
                severity = severity,
                priority = priority++,
                description = description,
                elementId = elementId,
                suggestedAction = suggestedAction,
                context = context
            };
        }

        private class SheetData
        {
            public int SheetId { get; set; }
            public string SheetNumber { get; set; }
            public string SheetName { get; set; }
            public int TitleblockId { get; set; }
            public string TitleblockName { get; set; }
            public int ViewportCount { get; set; }
            public List<int> PlacedViewIds { get; set; }
            public List<string> PlacedViewNames { get; set; }
        }

        private class ViewData
        {
            public int ViewId { get; set; }
            public string ViewName { get; set; }
            public ViewType ViewType { get; set; }
            public string ViewTypeName { get; set; }
            public int? AssociatedLevelId { get; set; }
            public string AssociatedLevelName { get; set; }
            public bool IsPlaced { get; set; }
            public int? PlacedOnSheetId { get; set; }
            public string PlacedOnSheetNumber { get; set; }
            public bool CanBePlaced { get; set; }
            public bool IsTemplate { get; set; }
            public int Scale { get; set; }
        }

        private class LevelData
        {
            public int LevelId { get; set; }
            public string LevelName { get; set; }
            public double Elevation { get; set; }
            public bool HasFloorPlan { get; set; }
            public bool HasCeilingPlan { get; set; }
            public int? FloorPlanViewId { get; set; }
            public int? CeilingPlanViewId { get; set; }
            public int AssociatedViewCount { get; set; }
        }

        private class ScheduleData
        {
            public int ScheduleId { get; set; }
            public string ScheduleName { get; set; }
            public string ScheduleCategory { get; set; }
            public bool IsPlaced { get; set; }
            public int? PlacedOnSheetId { get; set; }
            public string PlacedOnSheetNumber { get; set; }
        }

        private class TitleblockData
        {
            public int TypeId { get; set; }
            public string TypeName { get; set; }
            public string FamilyName { get; set; }
            public int UsageCount { get; set; }
        }

        private static List<SheetData> GetAllSheetsData(Document doc)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber)
                .ToList();

            var result = new List<SheetData>();

            foreach (var sheet in sheets)
            {
                var viewportIds = sheet.GetAllViewports();
                var placedViews = new List<int>();
                var placedViewNames = new List<string>();

                foreach (var vpId in viewportIds)
                {
                    var viewport = doc.GetElement(vpId) as Viewport;
                    if (viewport != null)
                    {
                        var view = doc.GetElement(viewport.ViewId) as View;
                        placedViews.Add((int)viewport.ViewId.Value);
                        placedViewNames.Add(view?.Name ?? "");
                    }
                }

                // Get titleblock
                var titleblock = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .FirstElement();

                result.Add(new SheetData
                {
                    SheetId = (int)sheet.Id.Value,
                    SheetNumber = sheet.SheetNumber,
                    SheetName = sheet.Name,
                    TitleblockId = titleblock != null ? (int)titleblock.GetTypeId().Value : 0,
                    TitleblockName = titleblock != null ? doc.GetElement(titleblock.GetTypeId())?.Name ?? "" : "",
                    ViewportCount = viewportIds.Count,
                    PlacedViewIds = placedViews,
                    PlacedViewNames = placedViewNames
                });
            }

            return result;
        }

        private static List<ViewData> GetAllViewsData(Document doc)
        {
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType != ViewType.DrawingSheet && !v.IsTemplate)
                .ToList();

            // Get placement info
            var viewportMap = new Dictionary<int, (int sheetId, string sheetNumber)>();
            var viewports = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .ToList();

            foreach (var vp in viewports)
            {
                var sheet = doc.GetElement(vp.SheetId) as ViewSheet;
                viewportMap[(int)vp.ViewId.Value] = ((int)vp.SheetId.Value, sheet?.SheetNumber ?? "");
            }

            var result = new List<ViewData>();

            foreach (var view in views)
            {
                var viewId = (int)view.Id.Value;
                var isPlaced = viewportMap.ContainsKey(viewId);

                result.Add(new ViewData
                {
                    ViewId = viewId,
                    ViewName = view.Name,
                    ViewType = view.ViewType,
                    ViewTypeName = view.ViewType.ToString(),
                    AssociatedLevelId = view.GenLevel != null ? (int?)view.GenLevel.Id.Value : null,
                    AssociatedLevelName = view.GenLevel?.Name,
                    IsPlaced = isPlaced,
                    PlacedOnSheetId = isPlaced ? viewportMap[viewId].sheetId : (int?)null,
                    PlacedOnSheetNumber = isPlaced ? viewportMap[viewId].sheetNumber : null,
                    CanBePlaced = !isPlaced && Viewport.CanAddViewToSheet(doc, ElementId.InvalidElementId, view.Id),
                    IsTemplate = view.IsTemplate,
                    Scale = view.Scale
                });
            }

            return result;
        }

        private static List<LevelData> GetAllLevelsData(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            var result = new List<LevelData>();

            foreach (var level in levels)
            {
                var levelViews = views.Where(v => v.GenLevel?.Id.Value == level.Id.Value).ToList();
                var floorPlan = levelViews.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan);
                var ceilingPlan = levelViews.FirstOrDefault(v => v.ViewType == ViewType.CeilingPlan);

                result.Add(new LevelData
                {
                    LevelId = (int)level.Id.Value,
                    LevelName = level.Name,
                    Elevation = level.Elevation,
                    HasFloorPlan = floorPlan != null,
                    HasCeilingPlan = ceilingPlan != null,
                    FloorPlanViewId = floorPlan != null ? (int?)floorPlan.Id.Value : null,
                    CeilingPlanViewId = ceilingPlan != null ? (int?)ceilingPlan.Id.Value : null,
                    AssociatedViewCount = levelViews.Count
                });
            }

            return result;
        }

        private static List<ScheduleData> GetAllSchedulesData(Document doc)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .ToList();

            // Get placement info
            var viewportMap = new Dictionary<int, (int sheetId, string sheetNumber)>();
            var viewports = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .ToList();

            // Also check ScheduleSheetInstance for schedules
            var scheduleInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .ToList();

            foreach (var si in scheduleInstances)
            {
                var sheet = doc.GetElement(si.OwnerViewId) as ViewSheet;
                if (sheet != null)
                {
                    viewportMap[(int)si.ScheduleId.Value] = ((int)sheet.Id.Value, sheet.SheetNumber);
                }
            }

            var result = new List<ScheduleData>();

            foreach (var schedule in schedules)
            {
                var scheduleId = (int)schedule.Id.Value;
                var isPlaced = viewportMap.ContainsKey(scheduleId);

                // Try to get category
                var categoryName = "";
                try
                {
                    var def = schedule.Definition;
                    var catId = def.CategoryId;
                    if (catId != ElementId.InvalidElementId)
                    {
                        var cat = Category.GetCategory(doc, catId);
                        categoryName = cat?.Name ?? "";
                    }
                }
                catch { }

                result.Add(new ScheduleData
                {
                    ScheduleId = scheduleId,
                    ScheduleName = schedule.Name,
                    ScheduleCategory = categoryName,
                    IsPlaced = isPlaced,
                    PlacedOnSheetId = isPlaced ? viewportMap[scheduleId].sheetId : (int?)null,
                    PlacedOnSheetNumber = isPlaced ? viewportMap[scheduleId].sheetNumber : null
                });
            }

            return result;
        }

        private static List<TitleblockData> GetTitleblockTypes(Document doc)
        {
            var titleblockTypes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .ToList();

            // Count usage
            var titleblockInstances = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .ToList();

            var usageCounts = titleblockInstances
                .GroupBy(t => t.GetTypeId().Value)
                .ToDictionary(g => g.Key, g => g.Count());

            return titleblockTypes.Select(t => new TitleblockData
            {
                TypeId = (int)t.Id.Value,
                TypeName = t.Name,
                FamilyName = t.FamilyName,
                UsageCount = usageCounts.ContainsKey(t.Id.Value) ? usageCounts[t.Id.Value] : 0
            }).ToList();
        }

        private static object AnalyzeLevelCoverage(List<LevelData> levels, List<ViewData> views)
        {
            var coverage = new List<object>();

            foreach (var level in levels)
            {
                var levelViews = views.Where(v => v.AssociatedLevelId == level.LevelId).ToList();

                coverage.Add(new
                {
                    levelId = level.LevelId,
                    levelName = level.LevelName,
                    hasFloorPlan = level.HasFloorPlan,
                    hasCeilingPlan = level.HasCeilingPlan,
                    viewCount = levelViews.Count,
                    viewTypes = levelViews.GroupBy(v => v.ViewTypeName)
                        .Select(g => new { type = g.Key, count = g.Count() })
                });
            }

            return coverage;
        }

        private static object DetectNamingPatternsInternal(List<SheetData> sheets, List<ViewData> views)
        {
            // Detect sheet numbering pattern
            var sheetNumbers = sheets.Select(s => s.SheetNumber).ToList();
            var sheetPattern = DetectSheetNumberPattern(sheetNumbers);

            // Detect view naming patterns
            var floorPlanNames = views.Where(v => v.ViewType == ViewType.FloorPlan).Select(v => v.ViewName).ToList();
            var ceilingPlanNames = views.Where(v => v.ViewType == ViewType.CeilingPlan).Select(v => v.ViewName).ToList();
            var elevationNames = views.Where(v => v.ViewType == ViewType.Elevation).Select(v => v.ViewName).ToList();

            return new
            {
                sheetNumbering = new
                {
                    pattern = sheetPattern,
                    examples = sheetNumbers.Take(5)
                },
                floorPlanNaming = new
                {
                    pattern = DetectCommonPattern(floorPlanNames),
                    examples = floorPlanNames.Take(3)
                },
                ceilingPlanNaming = new
                {
                    pattern = DetectCommonPattern(ceilingPlanNames),
                    examples = ceilingPlanNames.Take(3)
                },
                elevationNaming = new
                {
                    pattern = DetectCommonPattern(elevationNames),
                    examples = elevationNames.Take(4)
                }
            };
        }

        private static string DetectSheetNumberPattern(List<string> sheetNumbers)
        {
            if (sheetNumbers.Count == 0) return "unknown";

            // Check for common patterns
            var hasDot = sheetNumbers.Any(s => s.Contains("."));
            var hasDash = sheetNumbers.Any(s => s.Contains("-"));

            // Check prefix patterns (A, S, M, E, P, etc.)
            var prefixes = sheetNumbers
                .Select(s => Regex.Match(s, @"^[A-Z]+").Value)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            if (hasDot)
            {
                return $"DISCIPLINE#.# (e.g., A1.1) - Prefixes: {string.Join(", ", prefixes)}";
            }
            else if (hasDash)
            {
                return $"DISCIPLINE-#.# (e.g., A-1.1) - Prefixes: {string.Join(", ", prefixes)}";
            }
            else
            {
                return $"DISCIPLINE### (e.g., A101) - Prefixes: {string.Join(", ", prefixes)}";
            }
        }

        private static string DetectCommonPattern(List<string> names)
        {
            if (names.Count == 0) return "none detected";
            if (names.Count == 1) return names[0];

            // Find common words/patterns
            var words = names.SelectMany(n => n.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                .GroupBy(w => w.ToLower())
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            return string.Join(" + ", words);
        }

        #endregion

        #region Advanced Analysis Methods

        /// <summary>
        /// Extract comprehensive project data matrix with element relationships.
        /// Returns all elements with their hosts, parameters, and relationships.
        /// </summary>
        [MCPMethod("extractProjectDataMatrix", Category = "ProjectAnalysis", Description = "Extract comprehensive project data matrix with element relationships")]
        public static string ExtractProjectDataMatrix(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var includeParams = parameters?["includeParameters"]?.ToObject<bool>() ?? true;
                var categories = parameters?["categories"]?.ToObject<List<string>>();

                var result = new Dictionary<string, object>();

                // Get all rooms with boundaries
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                var roomData = rooms.Select(r => new
                {
                    id = r.Id.Value,
                    name = r.Name,
                    number = r.Number,
                    area = r.Area,
                    level = r.Level?.Name,
                    levelId = r.LevelId?.Value,
                    perimeter = r.Perimeter,
                    unboundedHeight = r.UnboundedHeight,
                    volume = r.Volume
                }).ToList();

                result["rooms"] = roomData;

                // Get all walls with their hosts and openings
                var walls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                var wallData = walls.Select(w =>
                {
                    var locationCurve = w.Location as LocationCurve;
                    var curve = locationCurve?.Curve;
                    var startPoint = curve?.GetEndPoint(0);
                    var endPoint = curve?.GetEndPoint(1);

                    return new
                    {
                        id = w.Id.Value,
                        typeName = doc.GetElement(w.GetTypeId())?.Name,
                        typeId = w.GetTypeId()?.Value,
                        level = doc.GetElement(w.LevelId)?.Name,
                        levelId = w.LevelId?.Value,
                        length = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0,
                        height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                        width = w.Width,
                        isExterior = w.WallType?.Function == WallFunction.Exterior,
                        start = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                        end = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null
                    };
                }).ToList();

                result["walls"] = wallData;

                // Get doors with host walls
                var doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var doorData = doors.Select(d => new
                {
                    id = d.Id.Value,
                    typeName = doc.GetElement(d.GetTypeId())?.Name,
                    typeId = d.GetTypeId()?.Value,
                    hostWallId = d.Host?.Id.Value,
                    hostWallType = d.Host != null ? doc.GetElement(d.Host.GetTypeId())?.Name : null,
                    level = doc.GetElement(d.LevelId)?.Name,
                    levelId = d.LevelId?.Value,
                    fromRoom = d.FromRoom?.Name,
                    fromRoomId = d.FromRoom?.Id.Value,
                    toRoom = d.ToRoom?.Name,
                    toRoomId = d.ToRoom?.Id.Value,
                    width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                    height = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0
                }).ToList();

                result["doors"] = doorData;

                // Get windows with host walls
                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var windowData = windows.Select(w => new
                {
                    id = w.Id.Value,
                    typeName = doc.GetElement(w.GetTypeId())?.Name,
                    typeId = w.GetTypeId()?.Value,
                    hostWallId = w.Host?.Id.Value,
                    hostWallType = w.Host != null ? doc.GetElement(w.Host.GetTypeId())?.Name : null,
                    level = doc.GetElement(w.LevelId)?.Name,
                    levelId = w.LevelId?.Value,
                    width = w.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                    height = w.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                    sillHeight = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0
                }).ToList();

                result["windows"] = windowData;

                // Get floors
                var floors = new FilteredElementCollector(doc)
                    .OfClass(typeof(Floor))
                    .WhereElementIsNotElementType()
                    .Cast<Floor>()
                    .ToList();

                var floorData = floors.Select(f => new
                {
                    id = f.Id.Value,
                    typeName = doc.GetElement(f.GetTypeId())?.Name,
                    typeId = f.GetTypeId()?.Value,
                    level = doc.GetElement(f.LevelId)?.Name,
                    levelId = f.LevelId?.Value,
                    area = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0,
                    thickness = f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0
                }).ToList();

                result["floors"] = floorData;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    exportedAt = DateTime.Now.ToString("o"),
                    summary = new
                    {
                        roomCount = rooms.Count,
                        wallCount = walls.Count,
                        doorCount = doors.Count,
                        windowCount = windows.Count,
                        floorCount = floors.Count
                    },
                    data = result
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Analyze circulation patterns - corridor efficiency, adjacencies, egress paths.
        /// </summary>
        [MCPMethod("analyzeCirculationPatterns", Category = "ProjectAnalysis", Description = "Analyze circulation patterns including corridor efficiency and egress paths")]
        public static string AnalyzeCirculationPatterns(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var levelIdParam = parameters?["levelId"];

                // Get rooms
                var roomCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0);

                if (levelIdParam != null)
                {
                    var levelId = new ElementId(levelIdParam.ToObject<int>());
                    roomCollector = roomCollector.Where(r => r.LevelId == levelId);
                }

                var rooms = roomCollector.ToList();

                // Classify rooms
                var corridorPatterns = new[] { "corridor", "hall", "hallway", "passage", "circulation" };
                var entryPatterns = new[] { "entry", "lobby", "vestibule", "foyer", "reception" };
                var stairPatterns = new[] { "stair", "stairwell", "stairway" };
                var elevatorPatterns = new[] { "elevator", "lift" };

                var corridors = rooms.Where(r => corridorPatterns.Any(p =>
                    r.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                var entries = rooms.Where(r => entryPatterns.Any(p =>
                    r.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                var stairs = rooms.Where(r => stairPatterns.Any(p =>
                    r.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                var elevators = rooms.Where(r => elevatorPatterns.Any(p =>
                    r.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

                var programRooms = rooms.Except(corridors).Except(entries).Except(stairs).Except(elevators).ToList();

                // Calculate metrics
                double totalArea = rooms.Sum(r => r.Area);
                double corridorArea = corridors.Sum(r => r.Area);
                double programArea = programRooms.Sum(r => r.Area);
                double circulationRatio = totalArea > 0 ? (corridorArea / totalArea) * 100 : 0;

                // Analyze corridor widths (estimate from area and perimeter)
                var corridorAnalysis = corridors.Select(c =>
                {
                    // Estimate width: if corridor is rectangular, width ≈ area / (perimeter/2 - width)
                    // Simplified: use perimeter/4 as average dimension
                    double avgDimension = c.Perimeter > 0 ? c.Area / (c.Perimeter / 2) : 0;
                    return new
                    {
                        id = c.Id.Value,
                        name = c.Name,
                        area = c.Area,
                        perimeter = c.Perimeter,
                        estimatedWidth = avgDimension,
                        level = c.Level?.Name,
                        meetsMinWidth = avgDimension >= 3.5 // 3.5 feet minimum for corridors
                    };
                }).ToList();

                // Find adjacencies (rooms sharing doors)
                var doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(d => d.FromRoom != null && d.ToRoom != null)
                    .ToList();

                var adjacencies = doors.Select(d => new
                {
                    doorId = d.Id.Value,
                    doorType = doc.GetElement(d.GetTypeId())?.Name,
                    fromRoom = d.FromRoom?.Name,
                    fromRoomId = d.FromRoom?.Id.Value,
                    toRoom = d.ToRoom?.Name,
                    toRoomId = d.ToRoom?.Id.Value,
                    isExit = d.ToRoom == null ||
                             d.ToRoom.Name.IndexOf("exterior", StringComparison.OrdinalIgnoreCase) >= 0
                }).ToList();

                // Calculate room connectivity
                var roomConnectivity = rooms.Select(r =>
                {
                    var connections = doors.Count(d =>
                        d.FromRoom?.Id.Value == r.Id.Value || d.ToRoom?.Id.Value == r.Id.Value);
                    return new
                    {
                        roomId = r.Id.Value,
                        roomName = r.Name,
                        doorCount = connections,
                        isDeadEnd = connections <= 1
                    };
                }).ToList();

                // Find dead-end corridors (potential egress issues)
                var deadEndCorridors = corridorAnalysis.Where(c =>
                    roomConnectivity.FirstOrDefault(rc => rc.roomId == c.id)?.doorCount <= 1).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new
                    {
                        totalRooms = rooms.Count,
                        corridorCount = corridors.Count,
                        programRoomCount = programRooms.Count,
                        stairCount = stairs.Count,
                        elevatorCount = elevators.Count,
                        entryCount = entries.Count
                    },
                    efficiency = new
                    {
                        totalArea = totalArea,
                        corridorArea = corridorArea,
                        programArea = programArea,
                        circulationPercentage = Math.Round(circulationRatio, 1),
                        efficiencyRating = circulationRatio < 15 ? "Excellent" :
                                          circulationRatio < 20 ? "Good" :
                                          circulationRatio < 25 ? "Average" : "Review recommended"
                    },
                    corridors = corridorAnalysis,
                    adjacencies = adjacencies,
                    connectivity = roomConnectivity,
                    issues = new
                    {
                        deadEndCorridors = deadEndCorridors,
                        narrowCorridors = corridorAnalysis.Where(c => !c.meetsMinWidth).ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Validate room sizes against architectural standards.
        /// </summary>
        [MCPMethod("validateSpaceEfficiency", Category = "ProjectAnalysis", Description = "Validate room sizes against architectural standards")]
        public static string ValidateSpaceEfficiency(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var buildingType = parameters?["buildingType"]?.ToString() ?? "residential";

                // Room size standards (in SF) - varies by building type
                var residentialStandards = new Dictionary<string, (double min, double ideal, double max)>
                {
                    { "master bedroom", (180, 220, 350) },
                    { "bedroom", (100, 130, 200) },
                    { "bathroom", (35, 50, 80) },
                    { "master bath", (60, 90, 150) },
                    { "kitchen", (100, 150, 250) },
                    { "living", (180, 250, 400) },
                    { "dining", (100, 140, 200) },
                    { "laundry", (35, 50, 80) },
                    { "closet", (20, 40, 80) },
                    { "garage", (200, 400, 600) },
                    { "office", (80, 120, 180) },
                    { "entry", (30, 50, 100) }
                };

                var officeStandards = new Dictionary<string, (double min, double ideal, double max)>
                {
                    { "private office", (100, 150, 250) },
                    { "open office", (48, 64, 100) },
                    { "conference", (150, 250, 500) },
                    { "break room", (100, 200, 400) },
                    { "reception", (100, 200, 400) },
                    { "restroom", (50, 80, 150) },
                    { "storage", (50, 100, 200) },
                    { "server", (80, 150, 300) }
                };

                var standards = buildingType.ToLower() == "office" ? officeStandards : residentialStandards;

                // Get rooms
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                var analysis = new List<object>();
                var issues = new List<object>();

                foreach (var room in rooms)
                {
                    var roomName = room.Name.ToLower();
                    var areaSF = room.Area; // Already in SF

                    // Find matching standard
                    var matchingStandard = standards.FirstOrDefault(s =>
                        roomName.Contains(s.Key));

                    string status = "no_standard";
                    string recommendation = null;

                    if (matchingStandard.Key != null)
                    {
                        var (min, ideal, max) = matchingStandard.Value;

                        if (areaSF < min)
                        {
                            status = "undersized";
                            recommendation = $"Room is {Math.Round(min - areaSF, 0)} SF below minimum. Consider expanding to at least {min} SF.";
                            issues.Add(new { roomId = room.Id.Value, roomName = room.Name, issue = "undersized", shortfall = min - areaSF });
                        }
                        else if (areaSF > max)
                        {
                            status = "oversized";
                            recommendation = $"Room is {Math.Round(areaSF - max, 0)} SF above typical maximum. Consider if space could be reallocated.";
                            issues.Add(new { roomId = room.Id.Value, roomName = room.Name, issue = "oversized", excess = areaSF - max });
                        }
                        else if (areaSF >= min && areaSF <= ideal)
                        {
                            status = "adequate";
                        }
                        else
                        {
                            status = "optimal";
                        }
                    }

                    analysis.Add(new
                    {
                        roomId = room.Id.Value,
                        roomName = room.Name,
                        roomNumber = room.Number,
                        level = room.Level?.Name,
                        areaSF = Math.Round(areaSF, 1),
                        matchedStandard = matchingStandard.Key,
                        status = status,
                        recommendation = recommendation
                    });
                }

                // Calculate overall efficiency
                double totalArea = rooms.Sum(r => r.Area);
                var programRooms = rooms.Where(r =>
                    !r.Name.ToLower().Contains("corridor") &&
                    !r.Name.ToLower().Contains("hall") &&
                    !r.Name.ToLower().Contains("circulation")).ToList();
                double programArea = programRooms.Sum(r => r.Area);
                double efficiencyRatio = totalArea > 0 ? (programArea / totalArea) * 100 : 0;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    buildingType = buildingType,
                    summary = new
                    {
                        totalRooms = rooms.Count,
                        totalAreaSF = Math.Round(totalArea, 0),
                        programAreaSF = Math.Round(programArea, 0),
                        efficiencyPercentage = Math.Round(efficiencyRatio, 1),
                        undersizedCount = analysis.Count(a => ((dynamic)a).status == "undersized"),
                        oversizedCount = analysis.Count(a => ((dynamic)a).status == "oversized"),
                        optimalCount = analysis.Count(a => ((dynamic)a).status == "optimal" || ((dynamic)a).status == "adequate")
                    },
                    rooms = analysis,
                    issues = issues,
                    standardsUsed = standards.Select(s => new { roomType = s.Key, minSF = s.Value.min, idealSF = s.Value.ideal, maxSF = s.Value.max })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extract building envelope metrics - WWR, wall area, glazing analysis.
        /// </summary>
        [MCPMethod("extractBuildingEnvelopeMetrics", Category = "ProjectAnalysis", Description = "Extract building envelope metrics including WWR and glazing analysis")]
        public static string ExtractBuildingEnvelopeMetrics(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get exterior walls
                var walls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                var exteriorWalls = walls.Where(w =>
                    w.WallType?.Function == WallFunction.Exterior).ToList();

                var interiorWalls = walls.Where(w =>
                    w.WallType?.Function == WallFunction.Interior).ToList();

                // Calculate wall areas
                double totalExteriorWallArea = 0;
                var wallsByOrientation = new Dictionary<string, double>
                {
                    { "North", 0 }, { "South", 0 }, { "East", 0 }, { "West", 0 }
                };

                foreach (var wall in exteriorWalls)
                {
                    var areaParam = wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    double area = areaParam?.AsDouble() ?? 0;
                    totalExteriorWallArea += area;

                    // Determine orientation from wall curve
                    var locationCurve = wall.Location as LocationCurve;
                    if (locationCurve?.Curve != null)
                    {
                        var direction = (locationCurve.Curve.GetEndPoint(1) - locationCurve.Curve.GetEndPoint(0)).Normalize();
                        // Normal is perpendicular to wall direction
                        var normal = new XYZ(-direction.Y, direction.X, 0);

                        // Classify by normal direction
                        if (Math.Abs(normal.Y) > Math.Abs(normal.X))
                        {
                            if (normal.Y > 0) wallsByOrientation["North"] += area;
                            else wallsByOrientation["South"] += area;
                        }
                        else
                        {
                            if (normal.X > 0) wallsByOrientation["East"] += area;
                            else wallsByOrientation["West"] += area;
                        }
                    }
                }

                // Get windows
                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                double totalGlazingArea = 0;
                var glazingByOrientation = new Dictionary<string, double>
                {
                    { "North", 0 }, { "South", 0 }, { "East", 0 }, { "West", 0 }
                };

                foreach (var window in windows)
                {
                    var width = window.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0;
                    var height = window.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0;
                    double area = width * height;
                    totalGlazingArea += area;

                    // Get host wall orientation
                    var hostWall = window.Host as Wall;
                    if (hostWall != null)
                    {
                        var locationCurve = hostWall.Location as LocationCurve;
                        if (locationCurve?.Curve != null)
                        {
                            var direction = (locationCurve.Curve.GetEndPoint(1) - locationCurve.Curve.GetEndPoint(0)).Normalize();
                            var normal = new XYZ(-direction.Y, direction.X, 0);

                            if (Math.Abs(normal.Y) > Math.Abs(normal.X))
                            {
                                if (normal.Y > 0) glazingByOrientation["North"] += area;
                                else glazingByOrientation["South"] += area;
                            }
                            else
                            {
                                if (normal.X > 0) glazingByOrientation["East"] += area;
                                else glazingByOrientation["West"] += area;
                            }
                        }
                    }
                }

                // Calculate WWR by orientation
                var wwrByOrientation = new Dictionary<string, double>();
                foreach (var orientation in wallsByOrientation.Keys)
                {
                    double wallArea = wallsByOrientation[orientation];
                    double glazingArea = glazingByOrientation[orientation];
                    wwrByOrientation[orientation] = wallArea > 0 ? Math.Round((glazingArea / wallArea) * 100, 1) : 0;
                }

                double overallWWR = totalExteriorWallArea > 0 ?
                    (totalGlazingArea / totalExteriorWallArea) * 100 : 0;

                // Energy code compliance check (simplified)
                string complianceStatus = overallWWR <= 40 ? "Likely Compliant" :
                                         overallWWR <= 50 ? "May Require Trade-offs" :
                                         "Exceeds Typical Limits";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    envelope = new
                    {
                        totalExteriorWallAreaSF = Math.Round(totalExteriorWallArea, 0),
                        totalGlazingAreaSF = Math.Round(totalGlazingArea, 0),
                        overallWWR = Math.Round(overallWWR, 1),
                        windowCount = windows.Count
                    },
                    byOrientation = new
                    {
                        wallArea = wallsByOrientation.ToDictionary(k => k.Key, v => Math.Round(v.Value, 0)),
                        glazingArea = glazingByOrientation.ToDictionary(k => k.Key, v => Math.Round(v.Value, 0)),
                        wwr = wwrByOrientation
                    },
                    wallSummary = new
                    {
                        exteriorWallCount = exteriorWalls.Count,
                        interiorWallCount = interiorWalls.Count,
                        totalWallCount = walls.Count
                    },
                    energyCompliance = new
                    {
                        status = complianceStatus,
                        note = "WWR > 40% typically requires performance path compliance",
                        recommendations = overallWWR > 40 ? new[]
                        {
                            "Consider reducing glazing on East/West facades",
                            "Use high-performance glazing to offset area",
                            "May need energy modeling for compliance"
                        } : new[] { "WWR is within prescriptive limits" }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extract acoustic properties based on wall assemblies.
        /// </summary>
        [MCPMethod("extractAcousticProperties", Category = "ProjectAnalysis", Description = "Extract acoustic properties based on wall assemblies")]
        public static string ExtractAcousticProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // STC ratings by common wall assembly types (simplified lookup)
                var stcRatings = new Dictionary<string, int>
                {
                    { "gypsum", 35 },
                    { "drywall", 35 },
                    { "stud", 40 },
                    { "metal stud", 45 },
                    { "double stud", 55 },
                    { "cmu", 45 },
                    { "concrete", 50 },
                    { "block", 45 },
                    { "brick", 50 },
                    { "acoustic", 55 },
                    { "demising", 55 },
                    { "party wall", 55 },
                    { "exterior", 50 },
                    { "shaft", 60 },
                    { "rated", 60 }
                };

                // Room acoustic requirements by use
                var roomRequirements = new Dictionary<string, int>
                {
                    { "bedroom", 50 },
                    { "living", 45 },
                    { "office", 45 },
                    { "conference", 55 },
                    { "classroom", 50 },
                    { "studio", 60 },
                    { "theater", 60 },
                    { "mechanical", 55 },
                    { "bathroom", 45 }
                };

                // Get wall types and estimate STC
                var wallTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .ToList();

                var wallTypeAnalysis = wallTypes.Select(wt =>
                {
                    string name = wt.Name.ToLower();
                    int estimatedSTC = 35; // Default

                    foreach (var rating in stcRatings)
                    {
                        if (name.Contains(rating.Key))
                        {
                            estimatedSTC = Math.Max(estimatedSTC, rating.Value);
                        }
                    }

                    // Adjust for thickness
                    var width = wt.Width;
                    if (width > 0.5) estimatedSTC += 5; // Thicker walls
                    if (width > 1.0) estimatedSTC += 5;

                    return new
                    {
                        typeId = wt.Id.Value,
                        typeName = wt.Name,
                        width = Math.Round(width * 12, 2), // Convert to inches
                        function = wt.Function.ToString(),
                        estimatedSTC = estimatedSTC,
                        acousticRating = estimatedSTC >= 55 ? "Good" :
                                        estimatedSTC >= 45 ? "Adequate" : "Limited"
                    };
                }).ToList();

                // Get rooms and analyze acoustic separation
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                var roomAnalysis = rooms.Select(r =>
                {
                    string roomName = r.Name.ToLower();
                    int requiredSTC = 45; // Default

                    foreach (var req in roomRequirements)
                    {
                        if (roomName.Contains(req.Key))
                        {
                            requiredSTC = req.Value;
                            break;
                        }
                    }

                    return new
                    {
                        roomId = r.Id.Value,
                        roomName = r.Name,
                        level = r.Level?.Name,
                        requiredSTC = requiredSTC,
                        acousticPriority = requiredSTC >= 55 ? "High" :
                                          requiredSTC >= 50 ? "Medium" : "Standard"
                    };
                }).ToList();

                // Find potential acoustic issues (high-priority rooms next to noisy spaces)
                var noiseSourcePatterns = new[] { "mechanical", "elevator", "laundry", "garage", "kitchen" };
                var noiseSources = rooms.Where(r =>
                    noiseSourcePatterns.Any(p => r.Name.ToLower().Contains(p))).ToList();

                var sensitivePatterns = new[] { "bedroom", "studio", "theater", "conference" };
                var sensitiveRooms = rooms.Where(r =>
                    sensitivePatterns.Any(p => r.Name.ToLower().Contains(p))).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new
                    {
                        wallTypeCount = wallTypes.Count,
                        roomCount = rooms.Count,
                        noiseSourceCount = noiseSources.Count,
                        sensitiveRoomCount = sensitiveRooms.Count
                    },
                    wallTypes = wallTypeAnalysis.OrderByDescending(w => w.estimatedSTC),
                    rooms = roomAnalysis,
                    potentialConflicts = new
                    {
                        noiseSources = noiseSources.Select(r => new { id = r.Id.Value, name = r.Name }),
                        sensitiveSpaces = sensitiveRooms.Select(r => new { id = r.Id.Value, name = r.Name }),
                        recommendation = noiseSources.Count > 0 && sensitiveRooms.Count > 0 ?
                            "Review wall types between noise sources and sensitive spaces" :
                            "No obvious acoustic conflicts detected"
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extract daylighting analysis - window-to-floor ratios, orientation analysis.
        /// </summary>
        [MCPMethod("extractDaylightingAnalysis", Category = "ProjectAnalysis", Description = "Extract daylighting analysis including window-to-floor ratios")]
        public static string ExtractDaylightingAnalysis(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get rooms
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                // Get windows with their rooms
                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                // Calculate window area per room
                var windowsByRoom = new Dictionary<long, List<FamilyInstance>>();
                foreach (var window in windows)
                {
                    // Find which room this window serves (use FromRoom or ToRoom)
                    var room = window.FromRoom ?? window.ToRoom;
                    if (room != null)
                    {
                        if (!windowsByRoom.ContainsKey(room.Id.Value))
                            windowsByRoom[room.Id.Value] = new List<FamilyInstance>();
                        windowsByRoom[room.Id.Value].Add(window);
                    }
                }

                // Analyze each room
                var roomAnalysis = rooms.Select(room =>
                {
                    double floorArea = room.Area;
                    var roomWindows = windowsByRoom.ContainsKey(room.Id.Value) ?
                        windowsByRoom[room.Id.Value] : new List<FamilyInstance>();

                    double totalWindowArea = 0;
                    var orientations = new List<string>();

                    foreach (var window in roomWindows)
                    {
                        var width = window.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0;
                        var height = window.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0;
                        totalWindowArea += width * height;

                        // Get orientation from host wall
                        var hostWall = window.Host as Wall;
                        if (hostWall != null)
                        {
                            var locationCurve = hostWall.Location as LocationCurve;
                            if (locationCurve?.Curve != null)
                            {
                                var direction = (locationCurve.Curve.GetEndPoint(1) - locationCurve.Curve.GetEndPoint(0)).Normalize();
                                var normal = new XYZ(-direction.Y, direction.X, 0);

                                string orientation;
                                if (Math.Abs(normal.Y) > Math.Abs(normal.X))
                                    orientation = normal.Y > 0 ? "North" : "South";
                                else
                                    orientation = normal.X > 0 ? "East" : "West";

                                if (!orientations.Contains(orientation))
                                    orientations.Add(orientation);
                            }
                        }
                    }

                    // Window-to-Floor Ratio (WFR)
                    double wfr = floorArea > 0 ? (totalWindowArea / floorArea) * 100 : 0;

                    // Daylight assessment
                    string daylightRating;
                    string recommendation = null;

                    if (roomWindows.Count == 0)
                    {
                        daylightRating = "No Windows";
                        recommendation = "Consider adding windows or skylights if habitable space";
                    }
                    else if (wfr < 8)
                    {
                        daylightRating = "Limited";
                        recommendation = "Below minimum 8% WFR for most codes. Consider larger windows.";
                    }
                    else if (wfr < 15)
                    {
                        daylightRating = "Adequate";
                    }
                    else if (wfr < 25)
                    {
                        daylightRating = "Good";
                    }
                    else
                    {
                        daylightRating = "Excellent";
                        if (orientations.Contains("West") || orientations.Contains("East"))
                        {
                            recommendation = "High WFR with East/West exposure may cause glare and heat gain";
                        }
                    }

                    // South-facing bonus
                    bool hasSouthExposure = orientations.Contains("South");

                    return new
                    {
                        roomId = room.Id.Value,
                        roomName = room.Name,
                        roomNumber = room.Number,
                        level = room.Level?.Name,
                        floorAreaSF = Math.Round(floorArea, 0),
                        windowCount = roomWindows.Count,
                        windowAreaSF = Math.Round(totalWindowArea, 1),
                        windowToFloorRatio = Math.Round(wfr, 1),
                        orientations = orientations,
                        hasSouthExposure = hasSouthExposure,
                        daylightRating = daylightRating,
                        recommendation = recommendation
                    };
                }).ToList();

                // Summary statistics
                var roomsWithWindows = roomAnalysis.Count(r => ((dynamic)r).windowCount > 0);
                var roomsWithGoodDaylight = roomAnalysis.Count(r =>
                    ((dynamic)r).daylightRating == "Good" || ((dynamic)r).daylightRating == "Excellent");
                var roomsWithLimitedDaylight = roomAnalysis.Count(r =>
                    ((dynamic)r).daylightRating == "Limited" || ((dynamic)r).daylightRating == "No Windows");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new
                    {
                        totalRooms = rooms.Count,
                        roomsWithWindows = roomsWithWindows,
                        roomsWithoutWindows = rooms.Count - roomsWithWindows,
                        totalWindows = windows.Count,
                        roomsWithGoodDaylight = roomsWithGoodDaylight,
                        roomsWithLimitedDaylight = roomsWithLimitedDaylight,
                        averageWFR = roomAnalysis.Count > 0 ?
                            Math.Round(roomAnalysis.Average(r => ((dynamic)r).windowToFloorRatio), 1) : 0
                    },
                    rooms = roomAnalysis.OrderByDescending(r => ((dynamic)r).windowToFloorRatio),
                    issues = roomAnalysis.Where(r => ((dynamic)r).recommendation != null)
                        .Select(r => new {
                            roomId = ((dynamic)r).roomId,
                            roomName = ((dynamic)r).roomName,
                            issue = ((dynamic)r).recommendation
                        }),
                    orientationSummary = new
                    {
                        northFacing = roomAnalysis.Count(r => ((dynamic)r).orientations.Contains("North")),
                        southFacing = roomAnalysis.Count(r => ((dynamic)r).orientations.Contains("South")),
                        eastFacing = roomAnalysis.Count(r => ((dynamic)r).orientations.Contains("East")),
                        westFacing = roomAnalysis.Count(r => ((dynamic)r).orientations.Contains("West"))
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
