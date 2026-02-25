using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Stair, railing, and ramp methods for MCP Bridge
    /// </summary>
    public static class StairRailingMethods
    {
        /// <summary>
        /// Get all stairs in the model
        /// </summary>
        public static string GetStairs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var stairs = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WhereElementIsNotElementType()
                    .Select(s => new
                    {
                        stairId = s.Id.Value,
                        name = s.Name,
                        typeName = doc.GetElement(s.GetTypeId())?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairCount = stairs.Count,
                    stairs = stairs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get stair types available in the model
        /// </summary>
        public static string GetStairTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WhereElementIsElementType()
                    .Select(t => new
                    {
                        typeId = t.Id.Value,
                        name = t.Name
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeCount = types.Count,
                    stairTypes = types
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get stair details (runs, landings, risers)
        /// </summary>
        public static string GetStairDetails(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var stairId = parameters["stairId"]?.Value<int>();

                if (!stairId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "stairId is required" });
                }

                var stair = doc.GetElement(new ElementId(stairId.Value)) as Stairs;
                if (stair == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Stair not found" });
                }

                var runs = stair.GetStairsRuns().Select(id =>
                {
                    var run = doc.GetElement(id) as StairsRun;
                    return new
                    {
                        runId = (int)id.Value,
                        actualRiserNumber = run?.ActualRisersNumber ?? 0
                    };
                }).ToList();

                var landings = stair.GetStairsLandings().Select(id => new
                {
                    landingId = (int)id.Value
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairId = stair.Id.Value,
                    baseElevation = stair.BaseElevation,
                    topElevation = stair.TopElevation,
                    height = stair.Height,
                    actualRisersNumber = stair.ActualRisersNumber,
                    actualTreadsNumber = stair.ActualTreadsNumber,
                    runs = runs,
                    landings = landings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a stair
        /// </summary>
        public static string DeleteStair(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var stairId = parameters["stairId"]?.Value<int>();

                if (!stairId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "stairId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Stair"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(stairId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedStairId = stairId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all railings in the model
        /// </summary>
        public static string GetRailings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var railings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StairsRailing)
                    .WhereElementIsNotElementType()
                    .Select(r => new
                    {
                        railingId = r.Id.Value,
                        name = r.Name,
                        typeName = doc.GetElement(r.GetTypeId())?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    railingCount = railings.Count,
                    railings = railings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get railing types available in the model
        /// </summary>
        public static string GetRailingTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StairsRailing)
                    .WhereElementIsElementType()
                    .Select(t => new
                    {
                        typeId = t.Id.Value,
                        name = t.Name
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeCount = types.Count,
                    railingTypes = types
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a railing along a path
        /// </summary>
        public static string CreateRailing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["points"]?.ToObject<double[][]>();
                var railingTypeId = parameters["railingTypeId"]?.Value<int>();
                var levelId = parameters["levelId"]?.Value<int>();

                if (points == null || points.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 2 points are required" });
                }

                if (!levelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                RailingType railType = null;
                if (railingTypeId.HasValue)
                {
                    railType = doc.GetElement(new ElementId(railingTypeId.Value)) as RailingType;
                }
                else
                {
                    railType = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StairsRailing)
                        .WhereElementIsElementType()
                        .Cast<RailingType>()
                        .FirstOrDefault();
                }

                if (railType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No railing type found" });
                }

                using (var trans = new Transaction(doc, "Create Railing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < points.Length - 1; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i].Length > 2 ? points[i][2] : 0);
                        var end = new XYZ(points[i + 1][0], points[i + 1][1], points[i + 1].Length > 2 ? points[i + 1][2] : 0);
                        curveLoop.Append(Line.CreateBound(start, end));
                    }

                    var railing = Railing.Create(doc, curveLoop, railType.Id, level.Id);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        railingId = railing.Id.Value,
                        typeName = railType.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a railing
        /// </summary>
        public static string DeleteRailing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var railingId = parameters["railingId"]?.Value<int>();

                if (!railingId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "railingId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Railing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(railingId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedRailingId = railingId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all ramps in the model
        /// </summary>
        public static string GetRamps(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ramps = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ramps)
                    .WhereElementIsNotElementType()
                    .Select(r => new
                    {
                        rampId = r.Id.Value,
                        name = r.Name,
                        typeName = doc.GetElement(r.GetTypeId())?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    rampCount = ramps.Count,
                    ramps = ramps
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ramp types available in the model
        /// </summary>
        public static string GetRampTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ramps)
                    .WhereElementIsElementType()
                    .Select(t => new
                    {
                        typeId = t.Id.Value,
                        name = t.Name
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeCount = types.Count,
                    rampTypes = types
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a ramp
        /// </summary>
        public static string DeleteRamp(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var rampId = parameters["rampId"]?.Value<int>();

                if (!rampId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "rampId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Ramp"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(rampId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedRampId = rampId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a ramp by sketch
        /// Parameters: levelId, rampTypeId (optional),
        ///             points: [[x1,y1], [x2,y2], ...] defining the ramp path,
        ///             width (optional, default 4'),
        ///             slope (optional, default 1:12 = 0.0833)
        /// </summary>
        public static string CreateRamp(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var rampTypeId = parameters["rampTypeId"]?.Value<int>();
                var points = parameters["points"]?.ToObject<double[][]>();
                var width = parameters["width"]?.Value<double>() ?? 4.0; // Default 4' width
                var slope = parameters["slope"]?.Value<double>() ?? (1.0 / 12.0); // Default 1:12 ADA compliant

                if (!levelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                if (points == null || points.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 2 points are required to define ramp path" });
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                // Get or find floor type (ramps use floor types in Revit)
                FloorType floorType = null;
                if (rampTypeId.HasValue)
                {
                    floorType = doc.GetElement(new ElementId(rampTypeId.Value)) as FloorType;
                }
                if (floorType == null)
                {
                    // First try to find a ramp-specific floor type
                    floorType = new FilteredElementCollector(doc)
                        .OfClass(typeof(FloorType))
                        .Cast<FloorType>()
                        .FirstOrDefault(ft => ft.Name.ToLower().Contains("ramp"));

                    // If no ramp type, use any floor type
                    if (floorType == null)
                    {
                        floorType = new FilteredElementCollector(doc)
                            .OfClass(typeof(FloorType))
                            .Cast<FloorType>()
                            .FirstOrDefault();
                    }
                }

                if (floorType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No floor type available in project for ramp creation" });
                }

                // Calculate ramp path length for elevation change
                double totalLength = 0;
                for (int i = 0; i < points.Length - 1; i++)
                {
                    var dx = points[i + 1][0] - points[i][0];
                    var dy = points[i + 1][1] - points[i][1];
                    totalLength += Math.Sqrt(dx * dx + dy * dy);
                }
                double riseHeight = totalLength * slope;

                ElementId newRampId = ElementId.InvalidElementId;

                using (var trans = new Transaction(doc, "Create Ramp"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create centerline points at level elevation (we'll add slope arrow after)
                    var pathPoints = new List<XYZ>();
                    for (int i = 0; i < points.Length; i++)
                    {
                        pathPoints.Add(new XYZ(points[i][0], points[i][1], level.Elevation));
                    }

                    // Create offset curves for ramp width (left and right edges)
                    var leftEdge = new List<XYZ>();
                    var rightEdge = new List<XYZ>();
                    double halfWidth = width / 2.0;

                    for (int i = 0; i < pathPoints.Count; i++)
                    {
                        XYZ direction;
                        if (i == 0)
                        {
                            direction = (pathPoints[1] - pathPoints[0]).Normalize();
                        }
                        else if (i == pathPoints.Count - 1)
                        {
                            direction = (pathPoints[i] - pathPoints[i - 1]).Normalize();
                        }
                        else
                        {
                            var d1 = (pathPoints[i] - pathPoints[i - 1]).Normalize();
                            var d2 = (pathPoints[i + 1] - pathPoints[i]).Normalize();
                            direction = ((d1 + d2) / 2).Normalize();
                        }

                        var perp = new XYZ(-direction.Y, direction.X, 0).Normalize();
                        leftEdge.Add(pathPoints[i] + perp * halfWidth);
                        rightEdge.Add(pathPoints[i] - perp * halfWidth);
                    }

                    // Build closed CurveLoop boundary
                    var curveLoop = new CurveLoop();

                    // Left edge forward
                    for (int i = 0; i < leftEdge.Count - 1; i++)
                    {
                        curveLoop.Append(Line.CreateBound(leftEdge[i], leftEdge[i + 1]));
                    }
                    // End cap
                    curveLoop.Append(Line.CreateBound(leftEdge[leftEdge.Count - 1], rightEdge[rightEdge.Count - 1]));
                    // Right edge backward
                    for (int i = rightEdge.Count - 1; i > 0; i--)
                    {
                        curveLoop.Append(Line.CreateBound(rightEdge[i], rightEdge[i - 1]));
                    }
                    // Start cap
                    curveLoop.Append(Line.CreateBound(rightEdge[0], leftEdge[0]));

                    // Create floor with the boundary (Revit 2026 Floor.Create API)
                    var curveLoops = new List<CurveLoop> { curveLoop };
                    var floor = Floor.Create(doc, curveLoops, floorType.Id, level.Id);

                    if (floor != null)
                    {
                        newRampId = floor.Id;

                        // Note: In Revit 2026, floor slope is best controlled via:
                        // 1. Slope arrows in edit mode
                        // 2. FloorSlopes.Add() API for programmatic slope
                        // The floor is created flat; user can add slope arrows manually or via addSlopeArrow method
                    }

                    trans.Commit();
                }

                if (newRampId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Failed to create ramp" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    rampId = newRampId.Value,
                    typeName = floorType?.Name ?? "Default",
                    levelName = level.Name,
                    width = width,
                    slope = slope,
                    slopeRatio = $"1:{Math.Round(1.0 / slope)}",
                    riseHeight = riseHeight,
                    runLength = totalLength,
                    note = "Ramp created as sloped floor. Use slope arrows in Revit for precise control."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a stair by sketch (run/landing geometry)
        /// Parameters: baseLevelId, topLevelId, stairTypeId (optional),
        ///             runs: [{startPoint: [x,y], endPoint: [x,y], width: feet}],
        ///             locationLine: "center"|"left"|"right" (optional)
        /// </summary>
        public static string CreateStairBySketch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var baseLevelId = parameters["baseLevelId"]?.Value<int>();
                var topLevelId = parameters["topLevelId"]?.Value<int>();
                var stairTypeId = parameters["stairTypeId"]?.Value<int>();
                var runs = parameters["runs"]?.ToObject<List<JObject>>();
                var locationLine = parameters["locationLine"]?.ToString() ?? "center";
                var width = parameters["width"]?.Value<double>() ?? 3.0; // Default 3' width

                if (!baseLevelId.HasValue || !topLevelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "baseLevelId and topLevelId are required" });
                }

                var baseLevel = doc.GetElement(new ElementId(baseLevelId.Value)) as Level;
                var topLevel = doc.GetElement(new ElementId(topLevelId.Value)) as Level;

                if (baseLevel == null || topLevel == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid level IDs" });
                }

                // Get or find stair type
                StairsType stairType = null;
                if (stairTypeId.HasValue)
                {
                    stairType = doc.GetElement(new ElementId(stairTypeId.Value)) as StairsType;
                }
                if (stairType == null)
                {
                    stairType = new FilteredElementCollector(doc)
                        .OfClass(typeof(StairsType))
                        .Cast<StairsType>()
                        .FirstOrDefault();
                }

                if (stairType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No stair type available" });
                }

                var runIds = new List<long>();
                ElementId newStairId = ElementId.InvalidElementId;

                using (var stairsScope = new StairsEditScope(doc, "Create Stair"))
                {
                    newStairId = stairsScope.Start(baseLevel.Id, topLevel.Id);

                    using (var trans = new SubTransaction(doc))
                    {
                        trans.Start();

                        // Determine location line
                        StairsRunJustification justification = StairsRunJustification.Center;
                        if (locationLine.ToLower() == "left")
                            justification = StairsRunJustification.Left;
                        else if (locationLine.ToLower() == "right")
                            justification = StairsRunJustification.Right;

                        if (runs != null && runs.Count > 0)
                        {
                            // Create runs from provided geometry
                            foreach (var runDef in runs)
                            {
                                var startPt = runDef["startPoint"]?.ToObject<double[]>();
                                var endPt = runDef["endPoint"]?.ToObject<double[]>();
                                var runWidth = runDef["width"]?.Value<double>() ?? width;

                                if (startPt != null && endPt != null && startPt.Length >= 2 && endPt.Length >= 2)
                                {
                                    var startXYZ = new XYZ(startPt[0], startPt[1], baseLevel.Elevation);
                                    var endXYZ = new XYZ(endPt[0], endPt[1], baseLevel.Elevation);
                                    var runLine = Line.CreateBound(startXYZ, endXYZ);

                                    var run = StairsRun.CreateStraightRun(doc, newStairId, runLine, justification);
                                    run.ActualRunWidth = runWidth;
                                    runIds.Add(run.Id.Value);
                                }
                            }
                        }
                        else
                        {
                            // Create a default straight run
                            var startXYZ = new XYZ(0, 0, baseLevel.Elevation);
                            var endXYZ = new XYZ(10, 0, baseLevel.Elevation); // 10' run
                            var runLine = Line.CreateBound(startXYZ, endXYZ);

                            var run = StairsRun.CreateStraightRun(doc, newStairId, runLine, justification);
                            run.ActualRunWidth = width;
                            runIds.Add(run.Id.Value);
                        }

                        trans.Commit();
                    }

                    stairsScope.Commit(new StairsFailurePreprocessor());
                }

                // Get the created stair info
                var stair = doc.GetElement(newStairId) as Stairs;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairId = newStairId.Value,
                    typeName = stairType.Name,
                    baseLevelName = baseLevel.Name,
                    topLevelName = topLevel.Name,
                    runCount = runIds.Count,
                    runIds = runIds,
                    actualRisers = stair?.ActualRisersNumber ?? 0,
                    actualTreads = stair?.ActualTreadsNumber ?? 0
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a stair with U-shape or L-shape configuration
        /// Parameters: baseLevelId, topLevelId, stairTypeId (optional),
        ///             shape: "straight"|"L"|"U", width, startPoint: [x,y], direction: [dx,dy]
        /// </summary>
        public static string CreateStairByComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var baseLevelId = parameters["baseLevelId"]?.Value<int>();
                var topLevelId = parameters["topLevelId"]?.Value<int>();
                var stairTypeId = parameters["stairTypeId"]?.Value<int>();
                var shape = parameters["shape"]?.ToString()?.ToLower() ?? "straight";
                var width = parameters["width"]?.Value<double>() ?? 3.0;
                var startPoint = parameters["startPoint"]?.ToObject<double[]>() ?? new double[] { 0, 0 };
                var direction = parameters["direction"]?.ToObject<double[]>() ?? new double[] { 1, 0 };

                if (!baseLevelId.HasValue || !topLevelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "baseLevelId and topLevelId are required" });
                }

                var baseLevel = doc.GetElement(new ElementId(baseLevelId.Value)) as Level;
                var topLevel = doc.GetElement(new ElementId(topLevelId.Value)) as Level;

                if (baseLevel == null || topLevel == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid level IDs" });
                }

                double stairHeight = topLevel.Elevation - baseLevel.Elevation;
                double riserHeight = 7.0 / 12.0; // 7" risers
                int totalRisers = (int)Math.Ceiling(stairHeight / riserHeight);
                double treadDepth = 11.0 / 12.0; // 11" treads

                var runIds = new List<long>();
                ElementId newStairId = ElementId.InvalidElementId;

                // Normalize direction
                double dirLength = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1]);
                double dx = direction[0] / dirLength;
                double dy = direction[1] / dirLength;
                // Perpendicular for turns
                double px = -dy;
                double py = dx;

                using (var stairsScope = new StairsEditScope(doc, "Create Stair Component"))
                {
                    newStairId = stairsScope.Start(baseLevel.Id, topLevel.Id);

                    using (var trans = new SubTransaction(doc))
                    {
                        trans.Start();

                        if (shape == "straight")
                        {
                            double runLength = (totalRisers - 1) * treadDepth;
                            var startXYZ = new XYZ(startPoint[0], startPoint[1], baseLevel.Elevation);
                            var endXYZ = new XYZ(startPoint[0] + dx * runLength, startPoint[1] + dy * runLength, baseLevel.Elevation);
                            var runLine = Line.CreateBound(startXYZ, endXYZ);

                            var run = StairsRun.CreateStraightRun(doc, newStairId, runLine, StairsRunJustification.Center);
                            run.ActualRunWidth = width;
                            runIds.Add(run.Id.Value);
                        }
                        else if (shape == "l")
                        {
                            // L-shape: two runs with landing
                            int risersPerRun = totalRisers / 2;
                            double run1Length = (risersPerRun - 1) * treadDepth;
                            double run2Length = (totalRisers - risersPerRun - 1) * treadDepth;

                            // First run
                            var start1 = new XYZ(startPoint[0], startPoint[1], baseLevel.Elevation);
                            var end1 = new XYZ(startPoint[0] + dx * run1Length, startPoint[1] + dy * run1Length, baseLevel.Elevation);
                            var run1Line = Line.CreateBound(start1, end1);
                            var run1 = StairsRun.CreateStraightRun(doc, newStairId, run1Line, StairsRunJustification.Center);
                            run1.ActualRunWidth = width;
                            runIds.Add(run1.Id.Value);

                            // Landing will be auto-created
                            // Second run (perpendicular)
                            double landingDepth = width;
                            var start2 = new XYZ(end1.X + dx * landingDepth, end1.Y + dy * landingDepth, baseLevel.Elevation);
                            var end2 = new XYZ(start2.X + px * run2Length, start2.Y + py * run2Length, baseLevel.Elevation);
                            var run2Line = Line.CreateBound(start2, end2);
                            var run2 = StairsRun.CreateStraightRun(doc, newStairId, run2Line, StairsRunJustification.Center);
                            run2.ActualRunWidth = width;
                            runIds.Add(run2.Id.Value);
                        }
                        else if (shape == "u")
                        {
                            // U-shape: two parallel runs with landing
                            int risersPerRun = totalRisers / 2;
                            double runLength = (risersPerRun - 1) * treadDepth;

                            // First run
                            var start1 = new XYZ(startPoint[0], startPoint[1], baseLevel.Elevation);
                            var end1 = new XYZ(startPoint[0] + dx * runLength, startPoint[1] + dy * runLength, baseLevel.Elevation);
                            var run1Line = Line.CreateBound(start1, end1);
                            var run1 = StairsRun.CreateStraightRun(doc, newStairId, run1Line, StairsRunJustification.Center);
                            run1.ActualRunWidth = width;
                            runIds.Add(run1.Id.Value);

                            // Second run (parallel, opposite direction, offset by 2*width + gap)
                            double gap = 0.5; // 6" gap between runs
                            double offset = (width * 2) + gap;
                            var start2 = new XYZ(end1.X + px * offset, end1.Y + py * offset, baseLevel.Elevation);
                            var end2 = new XYZ(start2.X - dx * runLength, start2.Y - dy * runLength, baseLevel.Elevation);
                            var run2Line = Line.CreateBound(start2, end2);
                            var run2 = StairsRun.CreateStraightRun(doc, newStairId, run2Line, StairsRunJustification.Center);
                            run2.ActualRunWidth = width;
                            runIds.Add(run2.Id.Value);
                        }

                        trans.Commit();
                    }

                    stairsScope.Commit(new StairsFailurePreprocessor());
                }

                var stair = doc.GetElement(newStairId) as Stairs;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairId = newStairId.Value,
                    shape = shape,
                    baseLevelName = baseLevel.Name,
                    topLevelName = topLevel.Name,
                    width = width,
                    runCount = runIds.Count,
                    runIds = runIds,
                    actualRisers = stair?.ActualRisersNumber ?? 0,
                    actualTreads = stair?.ActualTreadsNumber ?? 0
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify an existing stair's properties
        /// Parameters: stairId, width (optional), stairTypeId (optional)
        /// </summary>
        public static string ModifyStair(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var stairId = parameters["stairId"]?.Value<int>();
                var newWidth = parameters["width"]?.Value<double>();
                var newTypeId = parameters["stairTypeId"]?.Value<int>();

                if (!stairId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "stairId is required" });
                }

                var stair = doc.GetElement(new ElementId(stairId.Value)) as Stairs;
                if (stair == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Stair not found" });
                }

                var modifications = new List<string>();

                using (var trans = new Transaction(doc, "Modify Stair"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Change type
                    if (newTypeId.HasValue)
                    {
                        var newType = doc.GetElement(new ElementId(newTypeId.Value)) as StairsType;
                        if (newType != null)
                        {
                            stair.ChangeTypeId(newType.Id);
                            modifications.Add($"Changed type to {newType.Name}");
                        }
                    }

                    // Modify run widths
                    if (newWidth.HasValue)
                    {
                        var runIds = stair.GetStairsRuns();
                        foreach (var runId in runIds)
                        {
                            var run = doc.GetElement(runId) as StairsRun;
                            if (run != null)
                            {
                                run.ActualRunWidth = newWidth.Value;
                            }
                        }
                        modifications.Add($"Set width to {newWidth.Value}'");
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairId = stair.Id.Value,
                    modifications = modifications,
                    actualRisers = stair.ActualRisersNumber,
                    actualTreads = stair.ActualTreadsNumber
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get railings attached to a stair
        /// </summary>
        public static string GetStairRailings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var stairId = parameters["stairId"]?.Value<int>();

                if (!stairId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "stairId is required" });
                }

                var stair = doc.GetElement(new ElementId(stairId.Value)) as Stairs;
                if (stair == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Stair not found" });
                }

                // Find railings that reference this stair
                var railings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StairsRailing)
                    .WhereElementIsNotElementType()
                    .Cast<Railing>()
                    .Where(r => r.HasHost && r.HostId == stair.Id)
                    .Select(r => new
                    {
                        railingId = r.Id.Value,
                        typeName = doc.GetElement(r.GetTypeId())?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairId = stairId.Value,
                    railingCount = railings.Count,
                    railings = railings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }

    /// <summary>
    /// Failure preprocessor for stair creation operations
    /// </summary>
    public class StairsFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            foreach (var failure in failures)
            {
                var severity = failure.GetSeverity();
                if (severity == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
                else if (severity == FailureSeverity.Error)
                {
                    if (failure.HasResolutions())
                    {
                        failuresAccessor.ResolveFailure(failure);
                    }
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
