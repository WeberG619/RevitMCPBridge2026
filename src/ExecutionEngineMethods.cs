using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Services;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Execution Engine Methods for Predictive Intelligence System
    /// Phase 5: Execute predictions and automated actions
    /// </summary>
    public static class ExecutionEngineMethods
    {
        #region Execute Single Prediction

        /// <summary>
        /// Execute a single predicted action
        /// </summary>
        public static string ExecutePrediction(UIApplication uiApp, JObject parameters)
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
                var action = parameters?["action"]?.ToString();
                var elementId = parameters?["elementId"]?.ToObject<int?>();
                var actionParams = parameters?["params"] as JObject ?? new JObject();

                if (string.IsNullOrEmpty(action))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "action is required"
                    });
                }

                object result = null;
                string message = "";

                switch (action.ToLower())
                {
                    case "place_schedule":
                        result = ExecutePlaceSchedule(doc, elementId, actionParams);
                        message = "Schedule placed on sheet";
                        break;

                    case "place_view":
                        result = ExecutePlaceView(doc, elementId, actionParams);
                        message = "View placed on sheet";
                        break;

                    case "create_elevation":
                        result = ExecuteCreateElevation(doc, actionParams);
                        message = "Elevation view created";
                        break;

                    case "create_floor_plan":
                        result = ExecuteCreateFloorPlan(doc, actionParams);
                        message = "Floor plan created";
                        break;

                    case "create_ceiling_plan":
                        result = ExecuteCreateCeilingPlan(doc, actionParams);
                        message = "Ceiling plan created";
                        break;

                    case "create_section":
                        result = ExecuteCreateSection(doc, actionParams);
                        message = "Section view created";
                        break;

                    case "delete_empty_sheet":
                        result = ExecuteDeleteSheet(doc, elementId);
                        message = "Empty sheet deleted";
                        break;

                    default:
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown action: {action}"
                        });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    action = action,
                    message = message,
                    result = result
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Execute Batch

        /// <summary>
        /// Execute multiple predictions in sequence
        /// </summary>
        public static string ExecutePredictions(UIApplication uiApp, JObject parameters)
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
                var predictions = parameters?["predictions"]?.ToObject<JArray>();
                var stopOnError = parameters?["stopOnError"]?.ToObject<bool>() ?? false;

                if (predictions == null || predictions.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "predictions array is required"
                    });
                }

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var pred in predictions)
                {
                    try
                    {
                        var action = pred["action"]?.ToString();
                        var elementId = pred["elementId"]?.ToObject<int?>();
                        var actionParams = pred["params"] as JObject ?? new JObject();

                        object result = null;
                        bool success = false;

                        switch (action?.ToLower())
                        {
                            case "place_schedule":
                                result = ExecutePlaceSchedule(doc, elementId, actionParams);
                                success = result != null;
                                break;

                            case "place_view":
                                result = ExecutePlaceView(doc, elementId, actionParams);
                                success = result != null;
                                break;

                            case "create_elevation":
                                result = ExecuteCreateElevation(doc, actionParams);
                                success = result != null;
                                break;

                            default:
                                result = new { error = $"Unknown action: {action}" };
                                success = false;
                                break;
                        }

                        if (success) successCount++;
                        else failCount++;

                        results.Add(new
                        {
                            action = action,
                            success = success,
                            result = result
                        });

                        if (!success && stopOnError)
                            break;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        results.Add(new
                        {
                            action = pred["action"]?.ToString(),
                            success = false,
                            error = ex.Message
                        });

                        if (stopOnError)
                            break;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failCount == 0,
                    totalActions = predictions.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Auto-Place Schedule

        /// <summary>
        /// Automatically place a schedule on the most appropriate sheet
        /// </summary>
        public static string AutoPlaceSchedule(UIApplication uiApp, JObject parameters)
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
                var scheduleId = parameters?["scheduleId"]?.ToObject<int>();

                if (!scheduleId.HasValue)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var schedule = doc.GetElement(new ElementId(scheduleId.Value)) as ViewSchedule;
                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                // Check if a specific target sheet was provided
                ViewSheet targetSheet = null;
                var targetSheetId = parameters?["targetSheetId"]?.ToObject<int?>();

                if (targetSheetId.HasValue)
                {
                    targetSheet = doc.GetElement(new ElementId(targetSheetId.Value)) as ViewSheet;
                    if (targetSheet == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Target sheet with ID {targetSheetId.Value} not found"
                        });
                    }
                }
                else
                {
                    // Find best sheet for this schedule
                    targetSheet = FindBestSheetForSchedule(doc, schedule);
                }

                if (targetSheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No suitable sheet found for schedule",
                        suggestion = "Create a schedule sheet (e.g., A5.0 or A0.5)"
                    });
                }

                // Find best position on sheet
                var position = FindBestPositionOnSheet(doc, targetSheet, schedule);

                using (var trans = new Transaction(doc, "Auto-place Schedule"))
                {
                    trans.Start();

                    var instance = ScheduleSheetInstance.Create(doc, targetSheet.Id, schedule.Id, position);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleName = schedule.Name,
                        sheetNumber = targetSheet.SheetNumber,
                        sheetName = targetSheet.Name,
                        position = new { x = position.X, y = position.Y },
                        instanceId = (int)instance.Id.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Auto-Place View

        /// <summary>
        /// Automatically place a view on the most appropriate sheet
        /// </summary>
        public static string AutoPlaceView(UIApplication uiApp, JObject parameters)
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
                var viewId = parameters?["viewId"]?.ToObject<int>();

                if (!viewId.HasValue)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                var view = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Check if view can be placed
                if (!Viewport.CanAddViewToSheet(doc, ElementId.InvalidElementId, view.Id))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View cannot be placed on a sheet (may already be placed or is a template)"
                    });
                }

                // Find best sheet for this view
                var targetSheet = FindBestSheetForView(doc, view);

                if (targetSheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No suitable sheet found for view",
                        suggestion = $"Create a sheet for {view.ViewType} views"
                    });
                }

                // Find best position on sheet
                var position = FindBestPositionForView(doc, targetSheet, view);

                using (var trans = new Transaction(doc, "Auto-place View"))
                {
                    trans.Start();

                    var viewport = Viewport.Create(doc, targetSheet.Id, view.Id, position);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        sheetNumber = targetSheet.SheetNumber,
                        sheetName = targetSheet.Name,
                        position = new { x = position.X, y = position.Y },
                        viewportId = (int)viewport.Id.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Auto-Fix All Gaps

        /// <summary>
        /// Automatically fix all detected gaps that can be auto-resolved
        /// </summary>
        public static string AutoFixGaps(UIApplication uiApp, JObject parameters)
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
                var dryRun = parameters?["dryRun"]?.ToObject<bool>() ?? true;
                var maxFixes = parameters?["maxFixes"]?.ToObject<int>() ?? 10;

                var fixes = new List<object>();
                int fixCount = 0;

                // Get unplaced schedules
                var scheduleInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .Select(si => (int)si.ScheduleId.Value)
                    .ToHashSet();

                var unplacedSchedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                    .Where(s => !scheduleInstances.Contains((int)s.Id.Value))
                    .Where(s => IsImportantSchedule(s.Name))
                    .ToList();

                // Plan fixes for schedules
                foreach (var schedule in unplacedSchedules.Take(maxFixes - fixCount))
                {
                    var targetSheet = FindBestSheetForSchedule(doc, schedule);
                    if (targetSheet != null)
                    {
                        var fix = new
                        {
                            action = "place_schedule",
                            scheduleId = (int)schedule.Id.Value,
                            scheduleName = schedule.Name,
                            targetSheet = targetSheet.SheetNumber,
                            willExecute = !dryRun
                        };
                        fixes.Add(fix);

                        if (!dryRun)
                        {
                            var position = FindBestPositionOnSheet(doc, targetSheet, schedule);
                            using (var trans = new Transaction(doc, $"Place {schedule.Name}"))
                            {
                                trans.Start();
                                ScheduleSheetInstance.Create(doc, targetSheet.Id, schedule.Id, position);
                                trans.Commit();
                            }
                        }
                        fixCount++;
                    }
                }

                // Get unplaced important views
                var placedViewIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Select(vp => (int)vp.ViewId.Value)
                    .ToHashSet();

                var unplacedViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet)
                    .Where(v => !placedViewIds.Contains((int)v.Id.Value))
                    .Where(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.Elevation ||
                               v.ViewType == ViewType.Section || v.ViewType == ViewType.CeilingPlan)
                    .Where(v => Viewport.CanAddViewToSheet(doc, ElementId.InvalidElementId, v.Id))
                    .ToList();

                // Plan fixes for views
                foreach (var view in unplacedViews.Take(maxFixes - fixCount))
                {
                    var targetSheet = FindBestSheetForView(doc, view);
                    if (targetSheet != null)
                    {
                        var fix = new
                        {
                            action = "place_view",
                            viewId = (int)view.Id.Value,
                            viewName = view.Name,
                            viewType = view.ViewType.ToString(),
                            targetSheet = targetSheet.SheetNumber,
                            willExecute = !dryRun
                        };
                        fixes.Add(fix);

                        if (!dryRun)
                        {
                            var position = FindBestPositionForView(doc, targetSheet, view);
                            using (var trans = new Transaction(doc, $"Place {view.Name}"))
                            {
                                trans.Start();
                                Viewport.Create(doc, targetSheet.Id, view.Id, position);
                                trans.Commit();
                            }
                        }
                        fixCount++;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    dryRun = dryRun,
                    fixesPlanned = fixes.Count,
                    fixesExecuted = dryRun ? 0 : fixCount,
                    fixes = fixes,
                    message = dryRun ? "Dry run - no changes made. Set dryRun=false to execute." : $"Executed {fixCount} fixes"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods - Execution

        private static object ExecutePlaceSchedule(Document doc, int? scheduleId, JObject actionParams)
        {
            if (!scheduleId.HasValue)
                throw new ArgumentException("scheduleId required for place_schedule action");

            var schedule = doc.GetElement(new ElementId(scheduleId.Value)) as ViewSchedule;
            if (schedule == null)
                throw new ArgumentException("Schedule not found");

            var targetSheetId = actionParams["sheetId"]?.ToObject<int?>();
            ViewSheet targetSheet = null;

            if (targetSheetId.HasValue)
            {
                targetSheet = doc.GetElement(new ElementId(targetSheetId.Value)) as ViewSheet;
            }
            else
            {
                targetSheet = FindBestSheetForSchedule(doc, schedule);
            }

            if (targetSheet == null)
                throw new ArgumentException("No suitable sheet found");

            var position = FindBestPositionOnSheet(doc, targetSheet, schedule);

            using (var trans = new Transaction(doc, "Place Schedule"))
            {
                trans.Start();
                var instance = ScheduleSheetInstance.Create(doc, targetSheet.Id, schedule.Id, position);
                trans.Commit();

                return new
                {
                    instanceId = (int)instance.Id.Value,
                    sheetNumber = targetSheet.SheetNumber,
                    position = new { x = position.X, y = position.Y }
                };
            }
        }

        private static object ExecutePlaceView(Document doc, int? viewId, JObject actionParams)
        {
            if (!viewId.HasValue)
                throw new ArgumentException("viewId required for place_view action");

            var view = doc.GetElement(new ElementId(viewId.Value)) as View;
            if (view == null)
                throw new ArgumentException("View not found");

            if (!Viewport.CanAddViewToSheet(doc, ElementId.InvalidElementId, view.Id))
                throw new ArgumentException("View cannot be placed (may already be placed)");

            var targetSheetId = actionParams["sheetId"]?.ToObject<int?>();
            ViewSheet targetSheet = null;

            if (targetSheetId.HasValue)
            {
                targetSheet = doc.GetElement(new ElementId(targetSheetId.Value)) as ViewSheet;
            }
            else
            {
                targetSheet = FindBestSheetForView(doc, view);
            }

            if (targetSheet == null)
                throw new ArgumentException("No suitable sheet found");

            var position = FindBestPositionForView(doc, targetSheet, view);

            using (var trans = new Transaction(doc, "Place View"))
            {
                trans.Start();
                var viewport = Viewport.Create(doc, targetSheet.Id, view.Id, position);
                trans.Commit();

                return new
                {
                    viewportId = (int)viewport.Id.Value,
                    sheetNumber = targetSheet.SheetNumber,
                    position = new { x = position.X, y = position.Y }
                };
            }
        }

        private static object ExecuteCreateElevation(Document doc, JObject actionParams)
        {
            var direction = actionParams["direction"]?.ToString() ?? "North";
            var viewName = actionParams["viewName"]?.ToString() ?? $"{direction} Elevation";

            try
            {
                // Convert direction string to direction vector
                double[] directionVector = direction.ToLower() switch
                {
                    "north" => new double[] { 0, 1, 0 },
                    "south" => new double[] { 0, -1, 0 },
                    "east" => new double[] { 1, 0, 0 },
                    "west" => new double[] { -1, 0, 0 },
                    "northeast" or "ne" => new double[] { 0.707, 0.707, 0 },
                    "northwest" or "nw" => new double[] { -0.707, 0.707, 0 },
                    "southeast" or "se" => new double[] { 0.707, -0.707, 0 },
                    "southwest" or "sw" => new double[] { -0.707, -0.707, 0 },
                    _ => new double[] { 0, 1, 0 }
                };

                // Get model center for elevation location
                // Use bounding box of all model elements or active view crop box
                XYZ center;
                try
                {
                    var boundingBox = GetModelBoundingBox(doc);
                    if (boundingBox != null)
                    {
                        center = (boundingBox.Min + boundingBox.Max) / 2;
                    }
                    else
                    {
                        // Fallback to origin
                        center = XYZ.Zero;
                    }
                }
                catch
                {
                    center = XYZ.Zero;
                }

                // Build parameters for ViewMethods.CreateElevation
                var viewParams = new JObject
                {
                    ["location"] = new JArray { center.X, center.Y, center.Z },
                    ["direction"] = new JArray(directionVector),
                    ["viewName"] = viewName
                };

                // Call the existing ViewMethods.CreateElevation
                var uiApp = RevitMCPBridgeApp.GetUIApplication();
                if (uiApp == null)
                {
                    return new
                    {
                        success = false,
                        error = "UIApplication not available"
                    };
                }

                var resultJson = ViewMethods.CreateElevation(uiApp, viewParams);
                var result = JObject.Parse(resultJson);

                return new
                {
                    success = result["success"]?.ToObject<bool>() ?? false,
                    viewId = result["viewId"]?.ToObject<int?>(),
                    viewName = result["viewName"]?.ToString(),
                    markerId = result["markerId"]?.ToObject<int?>(),
                    direction = direction,
                    error = result["error"]?.ToString()
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    error = ex.Message,
                    direction = direction
                };
            }
        }

        /// <summary>
        /// Get bounding box of all model elements
        /// </summary>
        private static BoundingBoxXYZ GetModelBoundingBox(Document doc)
        {
            // Get all model elements to compute bounding box
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model);

            BoundingBoxXYZ modelBox = null;
            foreach (var element in collector)
            {
                var elemBox = element.get_BoundingBox(null);
                if (elemBox != null)
                {
                    if (modelBox == null)
                    {
                        modelBox = new BoundingBoxXYZ
                        {
                            Min = elemBox.Min,
                            Max = elemBox.Max
                        };
                    }
                    else
                    {
                        modelBox.Min = new XYZ(
                            Math.Min(modelBox.Min.X, elemBox.Min.X),
                            Math.Min(modelBox.Min.Y, elemBox.Min.Y),
                            Math.Min(modelBox.Min.Z, elemBox.Min.Z));
                        modelBox.Max = new XYZ(
                            Math.Max(modelBox.Max.X, elemBox.Max.X),
                            Math.Max(modelBox.Max.Y, elemBox.Max.Y),
                            Math.Max(modelBox.Max.Z, elemBox.Max.Z));
                    }
                }
            }
            return modelBox;
        }

        private static object ExecuteCreateFloorPlan(Document doc, JObject actionParams)
        {
            var levelId = actionParams["levelId"]?.ToObject<int?>();

            if (!levelId.HasValue)
                return new { success = false, error = "levelId required" };

            var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
            if (level == null)
                return new { success = false, error = "Level not found" };

            // Find floor plan view family type
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

            if (viewFamilyType == null)
                return new { success = false, error = "No floor plan view type found" };

            using (var trans = new Transaction(doc, "Create Floor Plan"))
            {
                trans.Start();

                var floorPlan = ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
                floorPlan.Name = $"{level.Name} - Floor Plan";

                trans.Commit();

                return new
                {
                    success = true,
                    viewId = (int)floorPlan.Id.Value,
                    viewName = floorPlan.Name,
                    levelName = level.Name
                };
            }
        }

        private static object ExecuteCreateCeilingPlan(Document doc, JObject actionParams)
        {
            var levelId = actionParams["levelId"]?.ToObject<int?>();

            if (!levelId.HasValue)
                return new { success = false, error = "levelId required" };

            var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
            if (level == null)
                return new { success = false, error = "Level not found" };

            // Find ceiling plan view family type
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.CeilingPlan);

            if (viewFamilyType == null)
                return new { success = false, error = "No ceiling plan view type found" };

            using (var trans = new Transaction(doc, "Create Ceiling Plan"))
            {
                trans.Start();

                var ceilingPlan = ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
                ceilingPlan.Name = $"{level.Name} - RCP";

                trans.Commit();

                return new
                {
                    success = true,
                    viewId = (int)ceilingPlan.Id.Value,
                    viewName = ceilingPlan.Name,
                    levelName = level.Name
                };
            }
        }

        private static object ExecuteCreateSection(Document doc, JObject actionParams)
        {
            try
            {
                var viewName = actionParams["viewName"]?.ToString();

                // Section requires either elementId (to cut through) or explicit start/end points
                var elementId = actionParams["elementId"]?.ToObject<int?>();

                XYZ startPoint, endPoint;

                if (elementId.HasValue)
                {
                    // Create section through the center of an element
                    var element = doc.GetElement(new ElementId(elementId.Value));
                    if (element == null)
                    {
                        return new { success = false, error = "Element not found" };
                    }

                    var bbox = element.get_BoundingBox(null);
                    if (bbox == null)
                    {
                        return new { success = false, error = "Could not get element bounding box" };
                    }

                    var center = (bbox.Min + bbox.Max) / 2;
                    var length = (bbox.Max - bbox.Min).GetLength();

                    // Default section direction - perpendicular to longest axis
                    var xLen = Math.Abs(bbox.Max.X - bbox.Min.X);
                    var yLen = Math.Abs(bbox.Max.Y - bbox.Min.Y);

                    if (xLen > yLen)
                    {
                        // Section runs along Y axis
                        startPoint = new XYZ(center.X, bbox.Min.Y - 5, center.Z);
                        endPoint = new XYZ(center.X, bbox.Max.Y + 5, center.Z);
                    }
                    else
                    {
                        // Section runs along X axis
                        startPoint = new XYZ(bbox.Min.X - 5, center.Y, center.Z);
                        endPoint = new XYZ(bbox.Max.X + 5, center.Y, center.Z);
                    }
                }
                else if (actionParams["startPoint"] != null && actionParams["endPoint"] != null)
                {
                    var startArr = actionParams["startPoint"].ToObject<double[]>();
                    var endArr = actionParams["endPoint"].ToObject<double[]>();
                    startPoint = new XYZ(startArr[0], startArr[1], startArr[2]);
                    endPoint = new XYZ(endArr[0], endArr[1], endArr[2]);
                }
                else
                {
                    // Default: create section through model center
                    var modelBox = GetModelBoundingBox(doc);
                    if (modelBox == null)
                    {
                        return new { success = false, error = "No model geometry to create section through" };
                    }

                    var center = (modelBox.Min + modelBox.Max) / 2;
                    startPoint = new XYZ(modelBox.Min.X - 5, center.Y, center.Z);
                    endPoint = new XYZ(modelBox.Max.X + 5, center.Y, center.Z);
                }

                // Build parameters for ViewMethods.CreateSection
                var viewParams = new JObject
                {
                    ["startPoint"] = new JArray { startPoint.X, startPoint.Y, startPoint.Z },
                    ["endPoint"] = new JArray { endPoint.X, endPoint.Y, endPoint.Z },
                    ["viewName"] = viewName ?? "Section"
                };

                var uiApp = RevitMCPBridgeApp.GetUIApplication();
                if (uiApp == null)
                {
                    return new { success = false, error = "UIApplication not available" };
                }

                var resultJson = ViewMethods.CreateSection(uiApp, viewParams);
                var result = JObject.Parse(resultJson);

                return new
                {
                    success = result["success"]?.ToObject<bool>() ?? false,
                    viewId = result["viewId"]?.ToObject<int?>(),
                    viewName = result["viewName"]?.ToString(),
                    error = result["error"]?.ToString()
                };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message };
            }
        }

        private static object ExecuteDeleteSheet(Document doc, int? sheetId)
        {
            if (!sheetId.HasValue)
                throw new ArgumentException("sheetId required");

            var sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet;
            if (sheet == null)
                throw new ArgumentException("Sheet not found");

            // Safety check - don't delete sheets with views
            if (sheet.GetAllViewports().Count > 0)
                throw new ArgumentException("Cannot delete sheet with views placed on it");

            var sheetNumber = sheet.SheetNumber;
            var sheetName = sheet.Name;

            using (var trans = new Transaction(doc, "Delete Empty Sheet"))
            {
                trans.Start();
                doc.Delete(sheet.Id);
                trans.Commit();

                return new
                {
                    deleted = true,
                    sheetNumber = sheetNumber,
                    sheetName = sheetName
                };
            }
        }

        #endregion

        #region Helper Methods - Finding Best Locations

        // SmartSheetMatcher singleton for pattern-aware sheet matching
        private static SmartSheetMatcher _sheetMatcher;

        private static SmartSheetMatcher GetSheetMatcher(Document doc)
        {
            if (_sheetMatcher == null)
            {
                _sheetMatcher = new SmartSheetMatcher();
            }

            // Initialize/update with current document
            var uiApp = RevitMCPBridgeApp.GetUIApplication();
            if (uiApp != null && doc != null)
            {
                _sheetMatcher.Initialize(doc, uiApp);
            }

            return _sheetMatcher;
        }

        private static ViewSheet FindBestSheetForSchedule(Document doc, ViewSchedule schedule)
        {
            // Use SmartSheetMatcher for pattern-aware matching
            var matcher = GetSheetMatcher(doc);
            var result = matcher.FindSheetForSchedule(doc, schedule);

            if (result != null)
                return result;

            // Fall back to traditional matching if SmartSheetMatcher returns null
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber)
                .ToList();

            var scheduleName = schedule.Name.ToLower();

            if (scheduleName.Contains("door"))
            {
                var match = sheets.FirstOrDefault(s =>
                    s.SheetNumber.Contains("2.3") ||
                    s.Name.ToLower().Contains("door") ||
                    s.Name.ToLower().Contains("schedule"));
                if (match != null) return match;
            }

            if (scheduleName.Contains("window"))
            {
                var match = sheets.FirstOrDefault(s =>
                    s.SheetNumber.Contains("2.3") ||
                    s.Name.ToLower().Contains("window") ||
                    s.Name.ToLower().Contains("schedule"));
                if (match != null) return match;
            }

            return sheets.FirstOrDefault(s =>
                s.SheetNumber.StartsWith("A5") ||
                s.SheetNumber.Contains(".5") ||
                s.Name.ToLower().Contains("schedule"));
        }

        private static ViewSheet FindBestSheetForView(Document doc, View view)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber)
                .ToList();

            // Phase 4: Check FocusContext for recently used sheet
            // If user recently placed a related view on a sheet, prefer that sheet
            var focus = ProactiveMonitor.Instance.CurrentFocus;
            if (!focus.IsStale && !string.IsNullOrEmpty(focus.LastPlacedSheetNumber))
            {
                // Check if the last placed view type is related to current view
                var isRelatedType = IsRelatedViewType(focus.LastPlacedViewType, view.ViewType.ToString());

                if (isRelatedType)
                {
                    var lastSheet = sheets.FirstOrDefault(s => s.SheetNumber == focus.LastPlacedSheetNumber);
                    if (lastSheet != null && HasSpaceForView(doc, lastSheet, view))
                    {
                        Log.Debug("[ExecutionEngine] Using last-placed sheet {SheetNumber} for related {ViewType}",
                            lastSheet.SheetNumber, view.ViewType);

                        // Record this placement
                        ProactiveMonitor.Instance.RecordViewPlacement(lastSheet.SheetNumber, view.ViewType.ToString());
                        return lastSheet;
                    }
                }
            }

            // Use SmartSheetMatcher for pattern-aware matching
            var matcher = GetSheetMatcher(doc);
            var result = matcher.FindSheetForView(doc, view);

            if (result != null)
            {
                // Record this placement for learning (both SmartSheetMatcher and ProactiveMonitor)
                matcher.RecordPlacement(view.ViewType, result.SheetNumber);
                ProactiveMonitor.Instance.RecordViewPlacement(result.SheetNumber, view.ViewType.ToString());
                return result;
            }

            // Fall back to traditional matching if SmartSheetMatcher returns null
            ViewSheet fallbackSheet = null;
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                    fallbackSheet = sheets.FirstOrDefault(s =>
                        s.SheetNumber.StartsWith("A1") ||
                        s.SheetNumber.StartsWith("A2.1") ||
                        s.SheetNumber.StartsWith("A2.2") ||
                        s.Name.ToLower().Contains("plan"));
                    break;

                case ViewType.CeilingPlan:
                    fallbackSheet = sheets.FirstOrDefault(s =>
                        s.SheetNumber.StartsWith("A3") ||
                        s.Name.ToLower().Contains("ceiling") ||
                        s.Name.ToLower().Contains("rcp"));
                    break;

                case ViewType.Elevation:
                    fallbackSheet = sheets.FirstOrDefault(s =>
                        s.SheetNumber.StartsWith("A5") ||
                        s.SheetNumber.StartsWith("A4") ||
                        s.Name.ToLower().Contains("elevation"));
                    break;

                case ViewType.Section:
                    fallbackSheet = sheets.FirstOrDefault(s =>
                        s.SheetNumber.StartsWith("A7") ||
                        s.SheetNumber.StartsWith("A6") ||
                        s.Name.ToLower().Contains("section"));
                    break;

                default:
                    fallbackSheet = sheets.FirstOrDefault(s => s.GetAllViewports().Count < 6);
                    break;
            }

            // Record fallback sheet placement for learning
            if (fallbackSheet != null)
            {
                ProactiveMonitor.Instance.RecordViewPlacement(fallbackSheet.SheetNumber, view.ViewType.ToString());
            }

            return fallbackSheet;
        }

        /// <summary>
        /// Check if two view types are related (would go on same or similar sheets)
        /// </summary>
        private static bool IsRelatedViewType(string lastViewType, string currentViewType)
        {
            if (string.IsNullOrEmpty(lastViewType) || string.IsNullOrEmpty(currentViewType))
                return false;

            // Same type is always related
            if (lastViewType.Equals(currentViewType, StringComparison.OrdinalIgnoreCase))
                return true;

            // Define related type groups
            var planTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FloorPlan", "CeilingPlan", "AreaPlan", "EngineeringPlan"
            };

            var elevationSectionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Elevation", "Section"
            };

            var detailTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Detail", "DraftingView"
            };

            // Check if both types are in the same group
            if (planTypes.Contains(lastViewType) && planTypes.Contains(currentViewType))
                return true;
            if (elevationSectionTypes.Contains(lastViewType) && elevationSectionTypes.Contains(currentViewType))
                return true;
            if (detailTypes.Contains(lastViewType) && detailTypes.Contains(currentViewType))
                return true;

            return false;
        }

        /// <summary>
        /// Check if a sheet has space for another view (simple heuristic)
        /// </summary>
        private static bool HasSpaceForView(Document doc, ViewSheet sheet, View view)
        {
            try
            {
                var viewports = sheet.GetAllViewports();

                // Simple heuristic: limit views per sheet
                if (viewports.Count >= 8)
                    return false;

                // For floor plans and elevations, usually 1-2 per sheet
                if (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.Elevation)
                {
                    var sameTypeCount = viewports
                        .Select(vpId => doc.GetElement(vpId) as Viewport)
                        .Count(vp => vp != null &&
                            doc.GetElement(vp.ViewId) is View v &&
                            v.ViewType == view.ViewType);

                    if (sameTypeCount >= 2)
                        return false;
                }

                return true;
            }
            catch
            {
                return true; // Assume space available if we can't check
            }
        }

        private static XYZ FindBestPositionOnSheet(Document doc, ViewSheet sheet, ViewSchedule schedule)
        {
            // Get existing schedule instances on this sheet
            var existingInstances = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .ToList();

            // Default position (center-ish of sheet)
            // Assuming 24x36 sheet = 2ft x 3ft
            double x = 1.5; // Center horizontally
            double y = 1.0; // Center vertically

            if (existingInstances.Count > 0)
            {
                // Stack below existing schedules (Point returns XYZ in 2026)
                var lowestY = existingInstances.Min(si => si.Point.Y);
                y = lowestY - 0.5; // Half foot below
            }

            return new XYZ(x, y, 0);
        }

        private static XYZ FindBestPositionForView(Document doc, ViewSheet sheet, View view)
        {
            // Get existing viewports on this sheet
            var existingViewports = sheet.GetAllViewports()
                .Select(vpId => doc.GetElement(vpId) as Viewport)
                .Where(vp => vp != null)
                .ToList();

            // Default position (center of sheet)
            // Assuming 24x36 sheet = 2ft x 3ft
            double x = 1.5; // Center horizontally
            double y = 1.0; // Center vertically

            if (existingViewports.Count > 0)
            {
                // Find empty space
                var occupiedPositions = existingViewports.Select(vp => vp.GetBoxCenter()).ToList();

                // Try positions in a grid
                var gridX = new[] { 0.8, 1.5, 2.2 };
                var gridY = new[] { 0.5, 1.0, 1.5 };

                foreach (var gx in gridX)
                {
                    foreach (var gy in gridY)
                    {
                        var testPos = new XYZ(gx, gy, 0);
                        if (!occupiedPositions.Any(op => Math.Abs(op.X - gx) < 0.4 && Math.Abs(op.Y - gy) < 0.4))
                        {
                            return testPos;
                        }
                    }
                }
            }

            return new XYZ(x, y, 0);
        }

        private static bool IsImportantSchedule(string name)
        {
            var lower = name.ToLower();
            return lower.Contains("door") || lower.Contains("window") ||
                   lower.Contains("room") || lower.Contains("finish") ||
                   lower.Contains("fixture");
        }

        #endregion
    }
}
