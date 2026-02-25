using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

// Suppress obsolete API warning for Curve.Intersect (backward compatibility)
#pragma warning disable CS0618

namespace RevitMCPBridge
{
    /// <summary>
    /// Grid creation, modification, and management methods for MCP Bridge
    /// </summary>
    public static class GridMethods
    {
        /// <summary>
        /// Create a linear grid line
        /// </summary>
        public static string CreateGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var startPoint = parameters["startPoint"]?.ToObject<double[]>();
                var endPoint = parameters["endPoint"]?.ToObject<double[]>();
                var name = parameters["name"]?.ToString();

                if (startPoint == null || endPoint == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "startPoint and endPoint are required" });
                }

                using (var trans = new Transaction(doc, "Create Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var start = new XYZ(startPoint[0], startPoint[1], startPoint.Length > 2 ? startPoint[2] : 0);
                    var end = new XYZ(endPoint[0], endPoint[1], endPoint.Length > 2 ? endPoint[2] : 0);
                    var line = Line.CreateBound(start, end);

                    var grid = Grid.Create(doc, line);

                    if (!string.IsNullOrEmpty(name))
                    {
                        grid.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        gridId = grid.Id.Value,
                        name = grid.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create an arc grid line
        /// </summary>
        public static string CreateArcGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var centerPoint = parameters["centerPoint"]?.ToObject<double[]>();
                var radius = parameters["radius"]?.Value<double>() ?? 10.0;
                var startAngle = parameters["startAngle"]?.Value<double>() ?? 0;
                var endAngle = parameters["endAngle"]?.Value<double>() ?? Math.PI;
                var name = parameters["name"]?.ToString();

                if (centerPoint == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "centerPoint is required" });
                }

                using (var trans = new Transaction(doc, "Create Arc Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var center = new XYZ(centerPoint[0], centerPoint[1], 0);
                    var arc = Arc.Create(center, radius, startAngle, endAngle, XYZ.BasisX, XYZ.BasisY);

                    var grid = Grid.Create(doc, arc);

                    if (!string.IsNullOrEmpty(name))
                    {
                        grid.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        gridId = grid.Id.Value,
                        name = grid.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all grids in the model
        /// </summary>
        public static string GetGrids(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Select(g => new
                    {
                        gridId = g.Id.Value,
                        name = g.Name,
                        isCurved = g.IsCurved,
                        curve = GetCurveInfo(g.Curve)
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    gridCount = grids.Count,
                    grids = grids
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get a specific grid by name or ID
        /// </summary>
        public static string GetGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var gridId = parameters["gridId"]?.Value<int>();
                var gridName = parameters["name"]?.ToString();

                Grid grid = null;

                if (gridId.HasValue)
                {
                    grid = doc.GetElement(new ElementId(gridId.Value)) as Grid;
                }
                else if (!string.IsNullOrEmpty(gridName))
                {
                    grid = new FilteredElementCollector(doc)
                        .OfClass(typeof(Grid))
                        .Cast<Grid>()
                        .FirstOrDefault(g => g.Name == gridName);
                }

                if (grid == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Grid not found" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    gridId = grid.Id.Value,
                    name = grid.Name,
                    isCurved = grid.IsCurved,
                    curve = GetCurveInfo(grid.Curve)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rename a grid
        /// </summary>
        public static string RenameGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var gridId = parameters["gridId"]?.Value<int>();
                var newName = parameters["newName"]?.ToString();

                if (!gridId.HasValue || string.IsNullOrEmpty(newName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "gridId and newName are required" });
                }

                var grid = doc.GetElement(new ElementId(gridId.Value)) as Grid;
                if (grid == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Grid not found" });
                }

                using (var trans = new Transaction(doc, "Rename Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    grid.Name = newName;
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        gridId = grid.Id.Value,
                        name = grid.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a grid
        /// </summary>
        public static string DeleteGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var gridId = parameters["gridId"]?.Value<int>();

                if (!gridId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "gridId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(gridId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedGridId = gridId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create multiple grids at regular intervals (array)
        /// </summary>
        public static string CreateGridArray(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var direction = parameters["direction"]?.ToString()?.ToUpper() ?? "VERTICAL"; // VERTICAL or HORIZONTAL
                var startPosition = parameters["startPosition"]?.Value<double>() ?? 0;
                var spacing = parameters["spacing"]?.Value<double>() ?? 10;
                var count = parameters["count"]?.Value<int>() ?? 5;
                var length = parameters["length"]?.Value<double>() ?? 100;
                var namePrefix = parameters["namePrefix"]?.ToString() ?? "";
                var startNumber = parameters["startNumber"]?.Value<int>() ?? 1;
                var useLetters = parameters["useLetters"]?.Value<bool>() ?? false;

                var createdGrids = new List<object>();

                using (var trans = new Transaction(doc, "Create Grid Array"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    for (int i = 0; i < count; i++)
                    {
                        double position = startPosition + (i * spacing);
                        XYZ start, end;

                        if (direction == "VERTICAL")
                        {
                            start = new XYZ(position, 0, 0);
                            end = new XYZ(position, length, 0);
                        }
                        else // HORIZONTAL
                        {
                            start = new XYZ(0, position, 0);
                            end = new XYZ(length, position, 0);
                        }

                        var line = Line.CreateBound(start, end);
                        var grid = Grid.Create(doc, line);

                        // Set name
                        string gridName;
                        if (useLetters)
                        {
                            gridName = namePrefix + ((char)('A' + startNumber - 1 + i)).ToString();
                        }
                        else
                        {
                            gridName = namePrefix + (startNumber + i).ToString();
                        }
                        grid.Name = gridName;

                        createdGrids.Add(new
                        {
                            gridId = grid.Id.Value,
                            name = grid.Name,
                            position = position
                        });
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    gridCount = createdGrids.Count,
                    grids = createdGrids
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set grid extents (2D extents in a specific view)
        /// </summary>
        public static string SetGridExtents(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var gridId = parameters["gridId"]?.Value<int>();
                var viewId = parameters["viewId"]?.Value<int>();
                var end0Offset = parameters["end0Offset"]?.Value<double>();
                var end1Offset = parameters["end1Offset"]?.Value<double>();

                if (!gridId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "gridId is required" });
                }

                var grid = doc.GetElement(new ElementId(gridId.Value)) as Grid;
                if (grid == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Grid not found" });
                }

                View view = null;
                if (viewId.HasValue)
                {
                    view = doc.GetElement(new ElementId(viewId.Value)) as View;
                }

                using (var trans = new Transaction(doc, "Set Grid Extents"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get the datum extent type
                    if (view != null)
                    {
                        var curve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault();
                        if (curve != null && curve is Line line)
                        {
                            var start = line.GetEndPoint(0);
                            var end = line.GetEndPoint(1);
                            var direction = (end - start).Normalize();

                            if (end0Offset.HasValue)
                            {
                                start = start + direction * end0Offset.Value;
                            }
                            if (end1Offset.HasValue)
                            {
                                end = end + direction * end1Offset.Value;
                            }

                            var newLine = Line.CreateBound(start, end);
                            grid.SetCurveInView(DatumExtentType.ViewSpecific, view, newLine);
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        gridId = grid.Id.Value,
                        name = grid.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the intersection point of two grids
        /// </summary>
        public static string GetGridIntersection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var grid1Id = parameters["grid1Id"]?.Value<int>();
                var grid2Id = parameters["grid2Id"]?.Value<int>();
                var grid1Name = parameters["grid1Name"]?.ToString();
                var grid2Name = parameters["grid2Name"]?.ToString();

                Grid grid1 = null, grid2 = null;

                if (grid1Id.HasValue)
                {
                    grid1 = doc.GetElement(new ElementId(grid1Id.Value)) as Grid;
                }
                else if (!string.IsNullOrEmpty(grid1Name))
                {
                    grid1 = new FilteredElementCollector(doc)
                        .OfClass(typeof(Grid))
                        .Cast<Grid>()
                        .FirstOrDefault(g => g.Name == grid1Name);
                }

                if (grid2Id.HasValue)
                {
                    grid2 = doc.GetElement(new ElementId(grid2Id.Value)) as Grid;
                }
                else if (!string.IsNullOrEmpty(grid2Name))
                {
                    grid2 = new FilteredElementCollector(doc)
                        .OfClass(typeof(Grid))
                        .Cast<Grid>()
                        .FirstOrDefault(g => g.Name == grid2Name);
                }

                if (grid1 == null || grid2 == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "One or both grids not found" });
                }

                var curve1 = grid1.Curve;
                var curve2 = grid2.Curve;

                var results = curve1.Intersect(curve2, out IntersectionResultArray resultArray);

                if (results == SetComparisonResult.Overlap && resultArray != null && resultArray.Size > 0)
                {
                    var intersection = resultArray.get_Item(0).XYZPoint;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        intersection = new { x = intersection.X, y = intersection.Y, z = intersection.Z },
                        grid1 = grid1.Name,
                        grid2 = grid2.Name
                    });
                }

                return JsonConvert.SerializeObject(new { success = false, error = "Grids do not intersect" });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object GetCurveInfo(Curve curve)
        {
            if (curve is Line line)
            {
                return new
                {
                    type = "Line",
                    start = new { x = line.GetEndPoint(0).X, y = line.GetEndPoint(0).Y, z = line.GetEndPoint(0).Z },
                    end = new { x = line.GetEndPoint(1).X, y = line.GetEndPoint(1).Y, z = line.GetEndPoint(1).Z },
                    length = line.Length
                };
            }
            else if (curve is Arc arc)
            {
                return new
                {
                    type = "Arc",
                    center = new { x = arc.Center.X, y = arc.Center.Y, z = arc.Center.Z },
                    radius = arc.Radius,
                    length = arc.Length
                };
            }
            return new { type = "Unknown" };
        }
    }
}
