using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Detail Elements in Revit
    /// Handles detail lines, filled regions, detail components, insulation, and break lines
    /// </summary>
    public static class DetailMethods
    {
        #region Detail Lines

        /// <summary>
        /// Creates a detail line in a view
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing viewId, startPoint, endPoint, lineStyleId</param>
        /// <returns>JSON response with success status and detail line ID</returns>
        [MCPMethod("createDetailLine", Category = "Detail", Description = "Creates a detail line in a view")]
        public static string CreateDetailLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, startPoint, and endPoint are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"View with ID {viewIdInt} not found" });
                }

                var startPt = parameters["startPoint"];
                var endPt = parameters["endPoint"];

                XYZ start = new XYZ(startPt["x"].ToObject<double>(), startPt["y"].ToObject<double>(), startPt["z"]?.ToObject<double>() ?? 0);
                XYZ end = new XYZ(endPt["x"].ToObject<double>(), endPt["y"].ToObject<double>(), endPt["z"]?.ToObject<double>() ?? 0);

                Line line = Line.CreateBound(start, end);

                using (var trans = new Transaction(doc, "Create Detail Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    DetailCurve detailLine = doc.Create.NewDetailCurve(view, line);

                    // Set line style if provided
                    if (parameters["lineStyleId"] != null)
                    {
                        int lineStyleIdInt = parameters["lineStyleId"].ToObject<int>();
                        ElementId lineStyleId = new ElementId(lineStyleIdInt);
                        GraphicsStyle lineStyle = doc.GetElement(lineStyleId) as GraphicsStyle;
                        if (lineStyle != null)
                        {
                            detailLine.LineStyle = lineStyle;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineId = detailLine.Id.Value,
                        viewId = viewIdInt,
                        message = "Detail line created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates a detail arc in a view
        /// </summary>
        [MCPMethod("createDetailArc", Category = "Detail", Description = "Creates a detail arc in a view")]
        public static string CreateDetailArc(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["center"] == null || parameters["radius"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, center, and radius are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"View with ID {viewIdInt} not found" });
                }

                var centerPt = parameters["center"];
                XYZ center = new XYZ(centerPt["x"].ToObject<double>(), centerPt["y"].ToObject<double>(), centerPt["z"]?.ToObject<double>() ?? 0);
                double radius = parameters["radius"].ToObject<double>();
                double startAngle = parameters["startAngle"]?.ToObject<double>() ?? 0;
                double endAngle = parameters["endAngle"]?.ToObject<double>() ?? 2 * Math.PI;

                Arc arc = Arc.Create(center, radius, startAngle, endAngle, XYZ.BasisX, XYZ.BasisY);

                using (var trans = new Transaction(doc, "Create Detail Arc"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    DetailCurve detailArc = doc.Create.NewDetailCurve(view, arc);

                    if (parameters["lineStyleId"] != null)
                    {
                        int lineStyleIdInt = parameters["lineStyleId"].ToObject<int>();
                        ElementId lineStyleId = new ElementId(lineStyleIdInt);
                        GraphicsStyle lineStyle = doc.GetElement(lineStyleId) as GraphicsStyle;
                        if (lineStyle != null)
                        {
                            detailArc.LineStyle = lineStyle;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        arcId = detailArc.Id.Value,
                        viewId = viewIdInt,
                        message = "Detail arc created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates detail lines from a polyline (multiple connected lines)
        /// </summary>
        [MCPMethod("createDetailPolyline", Category = "Detail", Description = "Creates a detail polyline in a view")]
        public static string CreateDetailPolyline(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["points"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "viewId and points are required" });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                View view = doc.GetElement(new ElementId(viewIdInt)) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"View with ID {viewIdInt} not found" });
                }

                var points = parameters["points"].ToObject<List<JObject>>();
                bool closed = parameters["closed"]?.ToObject<bool>() ?? false;

                List<XYZ> xyzPoints = points.Select(p => new XYZ(
                    p["x"].ToObject<double>(),
                    p["y"].ToObject<double>(),
                    p["z"]?.ToObject<double>() ?? 0
                )).ToList();

                using (var trans = new Transaction(doc, "Create Detail Polyline"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    List<int> lineIds = new List<int>();
                    for (int i = 0; i < xyzPoints.Count - 1; i++)
                    {
                        Line line = Line.CreateBound(xyzPoints[i], xyzPoints[i + 1]);
                        DetailCurve dc = doc.Create.NewDetailCurve(view, line);
                        lineIds.Add((int)dc.Id.Value);
                    }

                    if (closed && xyzPoints.Count > 2)
                    {
                        Line closingLine = Line.CreateBound(xyzPoints[xyzPoints.Count - 1], xyzPoints[0]);
                        DetailCurve dc = doc.Create.NewDetailCurve(view, closingLine);
                        lineIds.Add((int)dc.Id.Value);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineIds,
                        count = lineIds.Count,
                        message = "Detail polyline created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets information about a detail line
        /// </summary>
        [MCPMethod("getDetailLineInfo", Category = "Detail", Description = "Gets info about a detail line element")]
        public static string GetDetailLineInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int lineIdInt = parameters["lineId"].ToObject<int>();
                ElementId lineId = new ElementId(lineIdInt);

                // Get the detail line
                DetailCurve detailLine = doc.GetElement(lineId) as DetailCurve;
                if (detailLine == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Detail line not found or element is not a DetailCurve"
                    });
                }

                // Get curve information
                Curve curve = detailLine.GeometryCurve;
                string curveType = curve.GetType().Name;

                // Get curve endpoints
                XYZ startPoint = curve.GetEndPoint(0);
                XYZ endPoint = curve.GetEndPoint(1);

                // Get line style
                GraphicsStyle lineStyle = detailLine.LineStyle as GraphicsStyle;
                string lineStyleName = lineStyle?.Name ?? "Default";
                int lineStyleId = lineStyle != null ? (int)lineStyle.Id.Value : -1;

                // Get view
                View view = doc.GetElement(detailLine.OwnerViewId) as View;
                string viewName = view?.Name ?? "Unknown";
                int viewId = view != null ? (int)view.Id.Value : -1;

                // Additional curve properties
                double length = curve.Length;
                bool isBound = curve.IsBound;

                // Build result object
                var result = new
                {
                    success = true,
                    lineId = lineIdInt,
                    curveType,
                    startPoint = new
                    {
                        x = startPoint.X,
                        y = startPoint.Y,
                        z = startPoint.Z
                    },
                    endPoint = new
                    {
                        x = endPoint.X,
                        y = endPoint.Y,
                        z = endPoint.Z
                    },
                    length,
                    isBound,
                    lineStyle = new
                    {
                        id = lineStyleId,
                        name = lineStyleName
                    },
                    view = new
                    {
                        id = viewId,
                        name = viewName
                    }
                };

                // For arcs, add additional properties
                if (curve is Arc arc)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        result.success,
                        result.lineId,
                        result.curveType,
                        result.startPoint,
                        result.endPoint,
                        result.length,
                        result.isBound,
                        result.lineStyle,
                        result.view,
                        arcProperties = new
                        {
                            center = new
                            {
                                x = arc.Center.X,
                                y = arc.Center.Y,
                                z = arc.Center.Z
                            },
                            radius = arc.Radius,
                            startAngle = arc.GetEndParameter(0),
                            endAngle = arc.GetEndParameter(1)
                        }
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Modifies a detail line
        /// </summary>
        [MCPMethod("modifyDetailLine", Category = "Detail", Description = "Modifies an existing detail line")]
        public static string ModifyDetailLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int lineIdInt = parameters["lineId"].ToObject<int>();
                ElementId lineId = new ElementId(lineIdInt);

                // Get the detail line
                DetailCurve detailLine = doc.GetElement(lineId) as DetailCurve;
                if (detailLine == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Detail line not found or element is not a DetailCurve"
                    });
                }

                using (var trans = new Transaction(doc, "Modify Detail Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    bool modified = false;

                    // Modify curve geometry if provided
                    if (parameters["newCurve"] != null)
                    {
                        var curveData = parameters["newCurve"];
                        Curve newCurve = null;

                        // Check if it's a line or arc
                        if (curveData["type"] != null && curveData["type"].ToString() == "arc")
                        {
                            // Create arc
                            var centerPt = curveData["center"];
                            XYZ center = new XYZ(
                                centerPt["x"].ToObject<double>(),
                                centerPt["y"].ToObject<double>(),
                                centerPt["z"]?.ToObject<double>() ?? 0
                            );
                            double radius = curveData["radius"].ToObject<double>();
                            double startAngle = curveData["startAngle"]?.ToObject<double>() ?? 0;
                            double endAngle = curveData["endAngle"]?.ToObject<double>() ?? 2 * Math.PI;

                            newCurve = Arc.Create(center, radius, startAngle, endAngle, XYZ.BasisX, XYZ.BasisY);
                        }
                        else
                        {
                            // Create line from start/end points
                            var startPt = curveData["startPoint"];
                            var endPt = curveData["endPoint"];

                            XYZ start = new XYZ(
                                startPt["x"].ToObject<double>(),
                                startPt["y"].ToObject<double>(),
                                startPt["z"]?.ToObject<double>() ?? 0
                            );
                            XYZ end = new XYZ(
                                endPt["x"].ToObject<double>(),
                                endPt["y"].ToObject<double>(),
                                endPt["z"]?.ToObject<double>() ?? 0
                            );

                            newCurve = Line.CreateBound(start, end);
                        }

                        // Set the new curve
                        detailLine.SetGeometryCurve(newCurve, true);
                        modified = true;
                    }

                    // Modify line style if provided
                    if (parameters["lineStyleId"] != null)
                    {
                        int lineStyleIdInt = parameters["lineStyleId"].ToObject<int>();
                        ElementId lineStyleId = new ElementId(lineStyleIdInt);
                        GraphicsStyle lineStyle = doc.GetElement(lineStyleId) as GraphicsStyle;

                        if (lineStyle != null)
                        {
                            detailLine.LineStyle = lineStyle;
                            modified = true;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineId = lineIdInt,
                        modified,
                        message = modified ? "Detail line modified successfully" : "No changes made"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all detail lines in a view
        /// </summary>
        [MCPMethod("getDetailLinesInView", Category = "Detail", Description = "Gets all detail lines in a view")]
        public static string GetDetailLinesInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get view ID
                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);
                View view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                // Get all DetailCurve elements in the view
                FilteredElementCollector collector = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(DetailCurve));

                var detailLines = new List<object>();

                foreach (DetailCurve detailLine in collector)
                {
                    Curve curve = detailLine.GeometryCurve;
                    GraphicsStyle lineStyle = detailLine.LineStyle as GraphicsStyle;

                    var curveData = new
                    {
                        curveType = curve.GetType().Name,
                        isBound = curve.IsBound,
                        length = curve.Length
                    };

                    // Add curve-specific properties
                    object curveSpecific = null;
                    if (curve is Line line)
                    {
                        XYZ start = line.GetEndPoint(0);
                        XYZ end = line.GetEndPoint(1);
                        curveSpecific = new
                        {
                            startPoint = new { x = start.X, y = start.Y, z = start.Z },
                            endPoint = new { x = end.X, y = end.Y, z = end.Z }
                        };
                    }
                    else if (curve is Arc arc)
                    {
                        XYZ center = arc.Center;
                        curveSpecific = new
                        {
                            center = new { x = center.X, y = center.Y, z = center.Z },
                            radius = arc.Radius,
                            startAngle = arc.GetEndParameter(0),
                            endAngle = arc.GetEndParameter(1)
                        };
                    }

                    detailLines.Add(new
                    {
                        detailLineId = (int)detailLine.Id.Value,
                        curve = curveData,
                        curveProperties = curveSpecific,
                        lineStyle = new
                        {
                            id = lineStyle != null ? (int)lineStyle.Id.Value : -1,
                            name = lineStyle?.Name ?? "Unknown"
                        }
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    viewName = view.Name,
                    count = detailLines.Count,
                    lines = detailLines  // Changed from detailLines to lines
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Filled Regions

        /// <summary>
        /// Creates a filled region in a view
        /// </summary>
        [MCPMethod("createFilledRegion", Category = "Detail", Description = "Creates a filled region in a view")]
        public static string CreateFilledRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int viewIdInt = parameters["viewId"].ToObject<int>();
                View view = doc.GetElement(new ElementId(viewIdInt)) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                int filledRegionTypeIdInt = parameters["filledRegionTypeId"].ToObject<int>();
                ElementId filledRegionTypeId = new ElementId(filledRegionTypeIdInt);

                // Get boundary loops (array of arrays of points)
                var boundaryLoops = parameters["boundaryLoops"].ToObject<List<List<JObject>>>();

                // Create CurveLoop list
                List<CurveLoop> curveLoops = new List<CurveLoop>();

                foreach (var loopPoints in boundaryLoops)
                {
                    if (loopPoints.Count < 3)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Each boundary loop must have at least 3 points"
                        });
                    }

                    // Convert points to XYZ
                    List<XYZ> xyzPoints = loopPoints.Select(p => new XYZ(
                        p["x"].ToObject<double>(),
                        p["y"].ToObject<double>(),
                        p["z"]?.ToObject<double>() ?? 0
                    )).ToList();

                    // Create CurveLoop from points
                    CurveLoop curveLoop = new CurveLoop();
                    for (int i = 0; i < xyzPoints.Count; i++)
                    {
                        XYZ start = xyzPoints[i];
                        XYZ end = xyzPoints[(i + 1) % xyzPoints.Count]; // Loop back to first point

                        Line line = Line.CreateBound(start, end);
                        curveLoop.Append(line);
                    }

                    curveLoops.Add(curveLoop);
                }

                using (var trans = new Transaction(doc, "Create Filled Region"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    FilledRegion filledRegion = FilledRegion.Create(doc, filledRegionTypeId, view.Id, curveLoops);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        regionId = (int)filledRegion.Id.Value,
                        viewId = viewIdInt,
                        loopCount = curveLoops.Count,
                        message = "Filled region created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets information about a filled region
        /// </summary>
        [MCPMethod("getFilledRegionInfo", Category = "Detail", Description = "Gets info about a filled region")]
        public static string GetFilledRegionInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int regionIdInt = parameters["regionId"].ToObject<int>();
                ElementId regionId = new ElementId(regionIdInt);

                // Get the filled region
                FilledRegion filledRegion = doc.GetElement(regionId) as FilledRegion;
                if (filledRegion == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Filled region not found"
                    });
                }

                // Get type information
                FilledRegionType regionType = doc.GetElement(filledRegion.GetTypeId()) as FilledRegionType;
                string typeName = regionType?.Name ?? "Unknown";
                int typeId = regionType != null ? (int)regionType.Id.Value : -1;

                // Get view
                View view = doc.GetElement(filledRegion.OwnerViewId) as View;
                string viewName = view?.Name ?? "Unknown";
                int viewId = view != null ? (int)view.Id.Value : -1;

                // Get boundary curves
                var boundaryLoops = new List<object>();
                IList<CurveLoop> loops = filledRegion.GetBoundaries();

                foreach (CurveLoop loop in loops)
                {
                    var curves = new List<object>();
                    foreach (Curve curve in loop)
                    {
                        curves.Add(new
                        {
                            curveType = curve.GetType().Name,
                            startPoint = new
                            {
                                x = curve.GetEndPoint(0).X,
                                y = curve.GetEndPoint(0).Y,
                                z = curve.GetEndPoint(0).Z
                            },
                            endPoint = new
                            {
                                x = curve.GetEndPoint(1).X,
                                y = curve.GetEndPoint(1).Y,
                                z = curve.GetEndPoint(1).Z
                            },
                            length = curve.Length
                        });
                    }
                    boundaryLoops.Add(new
                    {
                        curveCount = curves.Count,
                        curves
                    });
                }

                // Get fill pattern (using LookupParameter instead of BuiltInParameter)
                FillPatternElement fillPattern = null;
                Parameter fillPatternParam = regionType?.LookupParameter("Background");
                if (fillPatternParam != null && fillPatternParam.HasValue)
                {
                    ElementId patternId = fillPatternParam.AsElementId();
                    if (patternId != null && patternId != ElementId.InvalidElementId)
                    {
                        fillPattern = doc.GetElement(patternId) as FillPatternElement;
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    regionId = regionIdInt,
                    type = new
                    {
                        id = typeId,
                        name = typeName
                    },
                    view = new
                    {
                        id = viewId,
                        name = viewName
                    },
                    loopCount = boundaryLoops.Count,
                    boundaryLoops,
                    fillPattern = fillPattern != null ? new
                    {
                        id = (int)fillPattern.Id.Value,
                        name = fillPattern.Name
                    } : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Modifies a filled region's boundary
        /// </summary>
        [MCPMethod("modifyFilledRegionBoundary", Category = "Detail", Description = "Modifies the boundary of a filled region")]
        public static string ModifyFilledRegionBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int regionIdInt = parameters["regionId"].ToObject<int>();
                ElementId regionId = new ElementId(regionIdInt);

                // Get the filled region
                FilledRegion filledRegion = doc.GetElement(regionId) as FilledRegion;
                if (filledRegion == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Filled region not found"
                    });
                }

                // Get new boundary loops (array of arrays of points)
                var newBoundaryLoops = parameters["newBoundaryLoops"].ToObject<List<List<JObject>>>();

                // Create new CurveLoop list
                List<CurveLoop> newCurveLoops = new List<CurveLoop>();

                foreach (var loopPoints in newBoundaryLoops)
                {
                    if (loopPoints.Count < 3)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Each boundary loop must have at least 3 points"
                        });
                    }

                    // Convert points to XYZ
                    List<XYZ> xyzPoints = loopPoints.Select(p => new XYZ(
                        p["x"].ToObject<double>(),
                        p["y"].ToObject<double>(),
                        p["z"]?.ToObject<double>() ?? 0
                    )).ToList();

                    // Create CurveLoop from points
                    CurveLoop curveLoop = new CurveLoop();
                    for (int i = 0; i < xyzPoints.Count; i++)
                    {
                        XYZ start = xyzPoints[i];
                        XYZ end = xyzPoints[(i + 1) % xyzPoints.Count]; // Loop back to first point

                        Line line = Line.CreateBound(start, end);
                        curveLoop.Append(line);
                    }

                    newCurveLoops.Add(curveLoop);
                }

                // Note: Revit 2026 API does not support direct boundary modification for filled regions
                // Workaround: Delete and recreate the filled region with new boundaries
                ElementId filledRegionTypeId = filledRegion.GetTypeId();
                ElementId viewId = filledRegion.OwnerViewId;

                using (var trans = new Transaction(doc, "Modify Filled Region Boundary"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete the old filled region
                    doc.Delete(regionId);

                    // Create new filled region with new boundaries
                    FilledRegion newFilledRegion = FilledRegion.Create(doc, filledRegionTypeId, viewId, newCurveLoops);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        oldRegionId = regionIdInt,
                        newRegionId = (int)newFilledRegion.Id.Value,
                        loopCount = newCurveLoops.Count,
                        message = "Filled region boundary modified successfully (recreated)"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all filled region types
        /// </summary>
        [MCPMethod("getFilledRegionTypes", Category = "Detail", Description = "Gets all filled region types in the document")]
        public static string GetFilledRegionTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all filled region types
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType));

                var types = new List<object>();

                foreach (FilledRegionType regionType in collector)
                {
                    // Get fill pattern for background (using LookupParameter for Revit 2026)
                    FillPatternElement backgroundPattern = null;
                    Parameter backgroundPatternParam = regionType.LookupParameter("Background");
                    if (backgroundPatternParam != null && backgroundPatternParam.HasValue)
                    {
                        ElementId patternId = backgroundPatternParam.AsElementId();
                        if (patternId != null && patternId != ElementId.InvalidElementId)
                        {
                            backgroundPattern = doc.GetElement(patternId) as FillPatternElement;
                        }
                    }

                    // Get fill pattern for foreground (if masking)
                    FillPatternElement foregroundPattern = null;
                    Parameter foregroundPatternParam = regionType.LookupParameter("Foreground");
                    if (foregroundPatternParam != null && foregroundPatternParam.HasValue)
                    {
                        ElementId patternId = foregroundPatternParam.AsElementId();
                        if (patternId != null && patternId != ElementId.InvalidElementId)
                        {
                            foregroundPattern = doc.GetElement(patternId) as FillPatternElement;
                        }
                    }

                    // Get color (using LookupParameter)
                    Parameter colorParam = regionType.LookupParameter("Color");
                    string colorHex = null;
                    if (colorParam != null && colorParam.HasValue)
                    {
                        int colorInt = colorParam.AsInteger();
                        Color color = new Color((byte)(colorInt & 0xFF), (byte)((colorInt >> 8) & 0xFF), (byte)((colorInt >> 16) & 0xFF));
                        colorHex = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
                    }

                    // Check if masking (has foreground pattern)
                    bool isMasking = foregroundPattern != null;

                    types.Add(new
                    {
                        typeId = (int)regionType.Id.Value,
                        typeName = regionType.Name,
                        backgroundPattern = backgroundPattern != null ? new
                        {
                            id = (int)backgroundPattern.Id.Value,
                            name = backgroundPattern.Name
                        } : null,
                        foregroundPattern = foregroundPattern != null ? new
                        {
                            id = (int)foregroundPattern.Id.Value,
                            name = foregroundPattern.Name
                        } : null,
                        color = colorHex,
                        isMasking
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = types.Count,
                    types
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all filled regions in a view
        /// </summary>
        [MCPMethod("getFilledRegionsInView", Category = "Detail", Description = "Gets all filled regions in a view")]
        public static string GetFilledRegionsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Get all filled regions in the view
                FilteredElementCollector collector = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion));

                var regions = new List<object>();

                foreach (FilledRegion filledRegion in collector)
                {
                    // Get type information
                    FilledRegionType regionType = doc.GetElement(filledRegion.GetTypeId()) as FilledRegionType;
                    string typeName = regionType?.Name ?? "Unknown";
                    int typeId = regionType != null ? (int)regionType.Id.Value : -1;

                    // Get boundary loop count
                    IList<CurveLoop> loops = filledRegion.GetBoundaries();

                    // Calculate approximate area (sum of all loop areas)
                    double totalArea = 0;
                    foreach (CurveLoop loop in loops)
                    {
                        if (loop.IsOpen())
                            continue;

                        totalArea += loop.GetExactLength(); // Note: This is perimeter, not area
                        // Revit doesn't provide direct area calculation for CurveLoop
                        // Users would need to calculate based on vertices
                    }

                    regions.Add(new
                    {
                        regionId = (int)filledRegion.Id.Value,
                        type = new
                        {
                            id = typeId,
                            name = typeName
                        },
                        loopCount = loops.Count,
                        totalPerimeter = totalArea // Note: This is perimeter length, not area
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    viewName = view.Name,
                    count = regions.Count,
                    regions
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Detail Components

        /// <summary>
        /// Places a detail component (detail item) in a view
        /// </summary>
        [MCPMethod("placeDetailComponent", Category = "Detail", Description = "Places a detail component by type ID")]
        public static string PlaceDetailComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int viewIdInt = parameters["viewId"].ToObject<int>();
                View view = doc.GetElement(new ElementId(viewIdInt)) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                int componentTypeIdInt = parameters["componentTypeId"].ToObject<int>();
                FamilySymbol componentType = doc.GetElement(new ElementId(componentTypeIdInt)) as FamilySymbol;

                if (componentType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Detail component type not found"
                    });
                }

                // Parse location
                var locationData = parameters["location"];
                XYZ location = new XYZ(
                    locationData["x"].ToObject<double>(),
                    locationData["y"].ToObject<double>(),
                    locationData["z"]?.ToObject<double>() ?? 0
                );

                // Get rotation (in degrees, convert to radians)
                double rotationDegrees = parameters["rotation"]?.ToObject<double>() ?? 0;
                double rotationRadians = rotationDegrees * (Math.PI / 180.0);

                using (var trans = new Transaction(doc, "Place Detail Component"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the family symbol if needed
                    if (!componentType.IsActive)
                    {
                        componentType.Activate();
                    }

                    // Create the detail component (family instance)
                    FamilyInstance detailComponent = doc.Create.NewFamilyInstance(location, componentType, view);

                    // Apply rotation if specified
                    if (Math.Abs(rotationRadians) > 0.001)
                    {
                        // Rotation axis is perpendicular to view plane (Z-axis for plan views)
                        XYZ rotationAxis = view.ViewDirection;
                        Line rotationLine = Line.CreateBound(location, location + rotationAxis);
                        ElementTransformUtils.RotateElement(doc, detailComponent.Id, rotationLine, rotationRadians);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        componentId = (int)detailComponent.Id.Value,
                        viewId = viewIdInt,
                        typeName = componentType.Name,
                        familyName = componentType.Family.Name,
                        rotation = rotationDegrees,
                        message = "Detail component placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places a detail component by family name and type name (looks up the type ID automatically)
        /// Parameters: viewId, familyName, typeName, x, y, rotation (optional)
        /// </summary>
        [MCPMethod("placeDetailComponentByName", Category = "Detail", Description = "Places a detail component by family and type name")]
        public static string PlaceDetailComponentByName(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse required parameters
                var viewIdParam = parameters?["viewId"];
                var familyNameParam = parameters?["familyName"];
                var typeNameParam = parameters?["typeName"];

                if (viewIdParam == null || familyNameParam == null || typeNameParam == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, familyName, and typeName are required"
                    });
                }

                int viewId = viewIdParam.ToObject<int>();
                string familyName = familyNameParam.ToString();
                string typeName = typeNameParam.ToString();
                double x = parameters?["x"]?.ToObject<double>() ?? 0;
                double y = parameters?["y"]?.ToObject<double>() ?? 0;
                double z = parameters?["z"]?.ToObject<double>() ?? 0;
                double rotation = parameters?["rotation"]?.ToObject<double>() ?? 0;

                View view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId} not found"
                    });
                }

                // Find the family symbol by name
                FamilySymbol componentType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family.Name == familyName && fs.Name == typeName);

                if (componentType == null)
                {
                    // Try partial match
                    componentType = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_DetailComponents)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(fs => fs.Family.Name.Contains(familyName) && fs.Name.Contains(typeName));
                }

                if (componentType == null)
                {
                    // List available families for debugging
                    var availableFamilies = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_DetailComponents)
                        .Cast<FamilySymbol>()
                        .Select(fs => $"{fs.Family.Name}:{fs.Name}")
                        .Distinct()
                        .Take(20)
                        .ToList();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Detail component type '{familyName}:{typeName}' not found",
                        availableTypes = availableFamilies
                    });
                }

                XYZ location = new XYZ(x, y, z);
                double rotationRadians = rotation * (Math.PI / 180.0);

                using (var trans = new Transaction(doc, "Place Detail Component"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the family symbol if needed
                    if (!componentType.IsActive)
                    {
                        componentType.Activate();
                    }

                    // Create the detail component
                    FamilyInstance detailComponent = doc.Create.NewFamilyInstance(location, componentType, view);

                    // Apply rotation if specified
                    if (Math.Abs(rotationRadians) > 0.001)
                    {
                        XYZ rotationAxis = view.ViewDirection;
                        Line rotationLine = Line.CreateBound(location, location + rotationAxis);
                        ElementTransformUtils.RotateElement(doc, detailComponent.Id, rotationLine, rotationRadians);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        componentId = (int)detailComponent.Id.Value,
                        typeId = (int)componentType.Id.Value,
                        viewId = viewId,
                        familyName = componentType.Family.Name,
                        typeName = componentType.Name,
                        location = new { x, y, z },
                        rotation = rotation
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places a repeating detail component along a path
        /// </summary>
        [MCPMethod("placeRepeatingDetailComponent", Category = "Detail", Description = "Places a repeating detail component along a path")]
        public static string PlaceRepeatingDetailComponent(UIApplication uiApp, JObject parameters)
        {
            // Note: Revit 2026 API has changed the repeating detail classes
            // Direct API creation may require using different approach or UI commands
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "Repeating detail component placement not directly supported in Revit 2026 API",
                hint = "Use Revit's built-in Repeating Detail tool or consider placing individual detail components programmatically"
            });
        }

        /// <summary>
        /// Gets information about a detail component
        /// </summary>
        [MCPMethod("getDetailComponentInfo", Category = "Detail", Description = "Gets info about a detail component instance")]
        public static string GetDetailComponentInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int componentIdInt = parameters["componentId"].ToObject<int>();
                ElementId componentId = new ElementId(componentIdInt);

                // Get the detail component
                FamilyInstance component = doc.GetElement(componentId) as FamilyInstance;
                if (component == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Detail component not found or element is not a FamilyInstance"
                    });
                }

                // Get type and family information
                FamilySymbol symbol = component.Symbol;
                string typeName = symbol.Name;
                string familyName = symbol.Family.Name;
                int typeId = (int)symbol.Id.Value;

                // Get location
                LocationPoint locationPoint = component.Location as LocationPoint;
                XYZ location = locationPoint?.Point;

                // Get rotation (in radians, convert to degrees)
                double rotationRadians = locationPoint?.Rotation ?? 0;
                double rotationDegrees = rotationRadians * (180.0 / Math.PI);

                // Get view
                View view = doc.GetElement(component.OwnerViewId) as View;
                string viewName = view?.Name ?? "Unknown";
                int viewId = view != null ? (int)view.Id.Value : -1;

                // Get bounding box
                BoundingBoxXYZ bbox = component.get_BoundingBox(view);
                object boundingBox = null;
                if (bbox != null)
                {
                    boundingBox = new
                    {
                        min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                        max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                    };
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    componentId = componentIdInt,
                    type = new
                    {
                        id = typeId,
                        name = typeName,
                        familyName
                    },
                    location = location != null ? new
                    {
                        x = location.X,
                        y = location.Y,
                        z = location.Z
                    } : null,
                    rotation = rotationDegrees,
                    view = new
                    {
                        id = viewId,
                        name = viewName
                    },
                    boundingBox,
                    category = component.Category?.Name
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all detail component types
        /// </summary>
        [MCPMethod("getDetailComponentTypes", Category = "Detail", Description = "Gets all detail component types in the document")]
        public static string GetDetailComponentTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all family symbols (types) in the Detail Items category
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_DetailComponents);

                var types = new List<object>();

                foreach (FamilySymbol symbol in collector)
                {
                    types.Add(new
                    {
                        typeId = (int)symbol.Id.Value,
                        typeName = symbol.Name,
                        familyName = symbol.Family.Name,
                        familyId = (int)symbol.Family.Id.Value,
                        isActive = symbol.IsActive,
                        category = symbol.Category?.Name
                    });
                }

                // Note: Repeating detail type enumeration not supported in Revit 2026 API
                // BuiltInCategory.OST_RepeatingDetail category removed from API
                var repeatingTypes = new List<object>();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    detailComponentTypes = new
                    {
                        count = types.Count,
                        types
                    },
                    repeatingDetailTypes = new
                    {
                        count = repeatingTypes.Count,
                        types = repeatingTypes
                    }
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all detail components in a view
        /// </summary>
        [MCPMethod("getDetailComponentsInView", Category = "Detail", Description = "Gets all detail components in a view")]
        public static string GetDetailComponentsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Get all detail components in the view
                FilteredElementCollector collector = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_DetailComponents);

                var components = new List<object>();

                foreach (FamilyInstance component in collector)
                {
                    // Get location
                    LocationPoint locationPoint = component.Location as LocationPoint;
                    XYZ location = locationPoint?.Point;

                    // Get rotation (convert to degrees)
                    double rotationRadians = locationPoint?.Rotation ?? 0;
                    double rotationDegrees = rotationRadians * (180.0 / Math.PI);

                    components.Add(new
                    {
                        componentId = (int)component.Id.Value,
                        typeName = component.Symbol.Name,
                        familyName = component.Symbol.Family.Name,
                        location = location != null ? new
                        {
                            x = location.X,
                            y = location.Y,
                            z = location.Z
                        } : null,
                        rotation = rotationDegrees
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    viewName = view.Name,
                    count = components.Count,
                    components
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Insulation

        /// <summary>
        /// Adds insulation to a duct, pipe, or other element
        /// </summary>
        [MCPMethod("addInsulation", Category = "Detail", Description = "Adds insulation to a detail line")]
        public static string AddInsulation(UIApplication uiApp, JObject parameters)
        {
            // Note: Insulation creation in Revit 2026 API is complex and element-type specific
            // DuctInsulation and PipeInsulation classes exist but require specific workflows
            // Insulation is typically added through the UI or type-specific MEP methods
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "Insulation creation not directly supported in current API implementation",
                hint = "Use Revit's Insulation tool or set insulation parameters on duct/pipe types"
            });
        }

        /// <summary>
        /// Gets insulation information
        /// </summary>
        [MCPMethod("getInsulationInfo", Category = "Detail", Description = "Gets info about an insulation element")]
        public static string GetInsulationInfo(UIApplication uiApp, JObject parameters)
        {
            // Note: Insulation retrieval in Revit 2026 API requires element-specific access
            // Insulation data is typically accessed through parameters on the host element
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "Insulation info retrieval not directly supported in current API implementation",
                hint = "Access insulation parameters directly from duct/pipe elements (e.g., RBS_REFERENCE_INSULATION_THICKNESS)"
            });
        }

        /// <summary>
        /// Removes insulation from an element
        /// </summary>
        [MCPMethod("removeInsulation", Category = "Detail", Description = "Removes insulation from a detail line")]
        public static string RemoveInsulation(UIApplication uiApp, JObject parameters)
        {
            // Note: Insulation removal in Revit 2026 API requires element-specific workflows
            // Insulation is typically removed through the UI or by resetting insulation parameters
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "Insulation removal not directly supported in current API implementation",
                hint = "Use Revit's Remove Insulation tool or clear insulation parameters on duct/pipe elements"
            });
        }

        #endregion

        #region Break Lines and Symbols

        /// <summary>
        /// Creates a break line symbol
        /// </summary>
        [MCPMethod("createBreakLine", Category = "Detail", Description = "Creates a break line symbol in a view")]
        public static string CreateBreakLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["viewId"] == null || parameters["breakLineTypeId"] == null || parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, breakLineTypeId, and location parameters are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);
                View view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                int breakLineTypeIdInt = parameters["breakLineTypeId"].ToObject<int>();
                ElementId breakLineTypeId = new ElementId(breakLineTypeIdInt);
                FamilySymbol breakLineSymbol = doc.GetElement(breakLineTypeId) as FamilySymbol;

                if (breakLineSymbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Break line symbol type with ID {breakLineTypeIdInt} not found"
                    });
                }

                var locationData = parameters["location"];
                XYZ location = new XYZ(
                    locationData["x"].ToObject<double>(),
                    locationData["y"].ToObject<double>(),
                    locationData["z"]?.ToObject<double>() ?? 0
                );

                double rotation = parameters["rotation"]?.ToObject<double>() ?? 0;
                double rotationRadians = rotation * (Math.PI / 180.0);

                using (var trans = new Transaction(doc, "Create Break Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!breakLineSymbol.IsActive)
                    {
                        breakLineSymbol.Activate();
                    }

                    // Place break line symbol as detail component
                    FamilyInstance breakLine = doc.Create.NewFamilyInstance(location, breakLineSymbol, view);

                    // Apply rotation if specified
                    if (Math.Abs(rotationRadians) > 0.001)
                    {
                        XYZ rotationAxis = view.ViewDirection;
                        Line rotationLine = Line.CreateBound(location, location + rotationAxis);
                        ElementTransformUtils.RotateElement(doc, breakLine.Id, rotationLine, rotationRadians);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        breakLineId = (int)breakLine.Id.Value,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        rotation
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places a section/elevation marker symbol
        /// </summary>
        [MCPMethod("placeMarkerSymbol", Category = "Detail", Description = "Places a marker symbol annotation in a view")]
        public static string PlaceMarkerSymbol(UIApplication uiApp, JObject parameters)
        {
            // Note: Section, elevation, and callout markers in Revit are automatically created
            // when creating the corresponding views. They cannot be independently placed.
            // Use ViewMethods to create sections/elevations which will create the markers.
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "Marker symbol placement not directly supported - markers are auto-created with views",
                hint = "Use CreateSection, CreateElevation, or CreateCallout methods from ViewMethods to create markers"
            });
        }

        #endregion

        #region Line Styles

        /// <summary>
        /// Gets all line styles (line patterns) in the project
        /// </summary>
        [MCPMethod("getLineStyles", Category = "Detail", Description = "Gets all line styles in the document")]
        public static string GetLineStyles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var lineStyles = new List<object>();

                // Get the Lines category
                Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

                if (linesCategory != null && linesCategory.SubCategories != null)
                {
                    foreach (Category subCat in linesCategory.SubCategories)
                    {
                        // Get the GraphicsStyle (line style) for this subcategory
                        GraphicsStyle style = subCat.GetGraphicsStyle(GraphicsStyleType.Projection);

                        if (style != null)
                        {
                            lineStyles.Add(new
                            {
                                styleId = (int)style.Id.Value,
                                styleName = style.Name,
                                categoryName = subCat.Name,
                                categoryId = (int)subCat.Id.Value
                            });
                        }
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    lineStylesCount = lineStyles.Count,
                    lineStyles
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a new line style
        /// </summary>
        [MCPMethod("createLineStyle", Category = "Detail", Description = "Creates a new line style")]
        public static string CreateLineStyle(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["name"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "name parameter is required"
                    });
                }

                string name = parameters["name"].ToString();

                // Get the Lines category
                Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

                if (linesCategory == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Lines category not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Line Style"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create new subcategory (line style)
                    Category newLineStyle = doc.Settings.Categories.NewSubcategory(linesCategory, name);

                    // Note: In Revit 2026, direct property setting on GraphicsStyle is limited
                    // Properties like LineWeight, LineColor, LinePattern are typically set via UI
                    // or require more complex API access

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineStyleId = (int)newLineStyle.Id.Value,
                        lineStyleName = newLineStyle.Name,
                        message = "Line style created. Use ModifyLineStyle to set properties (weight, color, pattern)."
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Modifies a line style
        /// </summary>
        [MCPMethod("modifyLineStyle", Category = "Detail", Description = "Modifies an existing line style")]
        public static string ModifyLineStyle(UIApplication uiApp, JObject parameters)
        {
            // Note: Direct modification of GraphicsStyle properties (LineWeight, LineColor, LinePattern)
            // has limited API support in Revit 2026. These properties are typically set via UI.
            // The API allows category/subcategory renaming but not direct graphics property modification.
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "Line style property modification not directly supported in Revit 2026 API",
                hint = "Line style properties (weight, color, pattern) are typically modified via Revit UI > Object Styles"
            });
        }

        #endregion

        #region Detail Groups

        /// <summary>
        /// Creates a detail group from selected elements
        /// </summary>
        [MCPMethod("createDetailGroup", Category = "Detail", Description = "Creates a detail group from selected elements")]
        public static string CreateDetailGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["elementIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds parameter is required"
                    });
                }

                var elementIdsInt = parameters["elementIds"].ToObject<List<int>>();
                string groupName = parameters["groupName"]?.ToString() ?? "Detail Group";

                if (elementIdsInt.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least one element ID is required"
                    });
                }

                // Convert to ElementId collection
                ICollection<ElementId> elementIds = new List<ElementId>();
                foreach (int id in elementIdsInt)
                {
                    elementIds.Add(new ElementId(id));
                }

                using (var trans = new Transaction(doc, "Create Detail Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the group
                    Group group = doc.Create.NewGroup(elementIds);

                    // Set the group name if provided
                    if (!string.IsNullOrEmpty(groupName))
                    {
                        group.GroupType.Name = groupName;
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = (int)group.Id.Value,
                        groupTypeId = (int)group.GroupType.Id.Value,
                        groupName = group.GroupType.Name,
                        memberCount = elementIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places an instance of a detail group
        /// </summary>
        [MCPMethod("placeDetailGroup", Category = "Detail", Description = "Places an instance of a detail group")]
        public static string PlaceDetailGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["groupTypeId"] == null || parameters["viewId"] == null || parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "groupTypeId, viewId, and location are required"
                    });
                }

                int groupTypeIdInt = parameters["groupTypeId"].ToObject<int>();
                ElementId groupTypeId = new ElementId(groupTypeIdInt);

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                var locationData = parameters["location"];
                XYZ location = new XYZ(
                    locationData["x"].ToObject<double>(),
                    locationData["y"].ToObject<double>(),
                    locationData["z"]?.ToObject<double>() ?? 0
                );

                GroupType groupType = doc.GetElement(groupTypeId) as GroupType;
                if (groupType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Group type with ID {groupTypeIdInt} not found"
                    });
                }

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Detail Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Place the group at the specified location
                    Group group = doc.Create.PlaceGroup(location, groupType);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = (int)group.Id.Value,
                        groupTypeId = (int)group.GroupType.Id.Value,
                        groupName = group.GroupType.Name,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        memberCount = group.GetMemberIds().Count
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all detail group types
        /// </summary>
        [MCPMethod("getDetailGroupTypes", Category = "Detail", Description = "Gets all detail group types in the document")]
        public static string GetDetailGroupTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypes = new List<object>();

                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(GroupType));

                foreach (GroupType groupType in collector)
                {
                    // Filter for detail groups (groups that contain detail elements)
                    // In Revit, detail groups are distinguished from model groups
                    int memberCount = 0;

                    if (groupType.Groups.IsEmpty)
                    {
                        // This is a group type with no instances yet - cannot get member count
                        groupTypes.Add(new
                        {
                            groupTypeId = (int)groupType.Id.Value,
                            groupTypeName = groupType.Name,
                            memberCount = 0,
                            instanceCount = 0
                        });
                    }
                    else
                    {
                        // Check first instance to get member count
                        var firstGroupId = groupType.Groups.Cast<ElementId>().FirstOrDefault();
                        if (firstGroupId != null)
                        {
                            Group firstGroup = doc.GetElement(firstGroupId) as Group;
                            if (firstGroup != null)
                            {
                                memberCount = firstGroup.GetMemberIds().Count;

                                groupTypes.Add(new
                                {
                                    groupTypeId = (int)groupType.Id.Value,
                                    groupTypeName = groupType.Name,
                                    memberCount = memberCount,
                                    instanceCount = groupType.Groups.Size
                                });
                            }
                        }
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupTypesCount = groupTypes.Count,
                    groupTypes
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Masking Regions

        /// <summary>
        /// Creates a masking region in a view
        /// </summary>
        [MCPMethod("createMaskingRegion", Category = "Detail", Description = "Creates a masking region in a view")]
        public static string CreateMaskingRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["boundaryPoints"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and boundaryPoints are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                var boundaryPoints = parameters["boundaryPoints"].ToObject<List<Dictionary<string, double>>>();

                // Convert boundary points to XYZ
                List<XYZ> points = new List<XYZ>();
                foreach (var point in boundaryPoints)
                {
                    points.Add(new XYZ(point["x"], point["y"], point.ContainsKey("z") ? point["z"] : 0));
                }

                // Create curve loop from points
                List<Curve> curves = new List<Curve>();
                for (int i = 0; i < points.Count; i++)
                {
                    XYZ start = points[i];
                    XYZ end = points[(i + 1) % points.Count];
                    curves.Add(Line.CreateBound(start, end));
                }

                CurveLoop curveLoop = CurveLoop.Create(curves);
                List<CurveLoop> curveLoops = new List<CurveLoop> { curveLoop };

                // Find a masking region type (filled region type with foreground pattern for masking)
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType));

                FilledRegionType maskingType = null;
                foreach (FilledRegionType frt in collector)
                {
                    // Check if this is a masking region type (has foreground pattern)
                    Parameter foregroundParam = frt.LookupParameter("Foreground");
                    if (foregroundParam != null && foregroundParam.AsElementId() != ElementId.InvalidElementId)
                    {
                        maskingType = frt;
                        break;
                    }
                }

                // If no masking type found, use first available filled region type
                if (maskingType == null)
                {
                    maskingType = collector.FirstElement() as FilledRegionType;
                }

                if (maskingType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No filled region type found for masking region"
                    });
                }

                using (var trans = new Transaction(doc, "Create Masking Region"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    FilledRegion maskingRegion = FilledRegion.Create(doc, maskingType.Id, viewId, curveLoops);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        maskingRegionId = (int)maskingRegion.Id.Value,
                        filledRegionTypeId = (int)maskingType.Id.Value,
                        filledRegionTypeName = maskingType.Name,
                        viewId = viewIdInt,
                        boundaryPointsCount = points.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region View-Specific Graphics

        /// <summary>
        /// Overrides graphics for an element in a specific view
        /// </summary>
        [MCPMethod("overrideElementGraphics", Category = "Detail", Description = "Overrides graphics settings for an element in a view")]
        public static string OverrideElementGraphics(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and elementId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Override Element Graphics"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    OverrideGraphicSettings overrides = new OverrideGraphicSettings();

                    // Set line color if provided
                    if (parameters["lineColor"] != null)
                    {
                        var colorData = parameters["lineColor"];
                        Color lineColor = new Color(
                            colorData["r"].ToObject<byte>(),
                            colorData["g"].ToObject<byte>(),
                            colorData["b"].ToObject<byte>()
                        );
                        overrides.SetProjectionLineColor(lineColor);
                        overrides.SetCutLineColor(lineColor);
                    }

                    // Set line weight if provided
                    if (parameters["lineWeight"] != null)
                    {
                        int lineWeight = parameters["lineWeight"].ToObject<int>();
                        overrides.SetProjectionLineWeight(lineWeight);
                        overrides.SetCutLineWeight(lineWeight);
                    }

                    // Set line pattern if provided
                    if (parameters["linePatternId"] != null)
                    {
                        int linePatternIdInt = parameters["linePatternId"].ToObject<int>();
                        ElementId linePatternId = new ElementId(linePatternIdInt);
                        overrides.SetProjectionLinePatternId(linePatternId);
                        overrides.SetCutLinePatternId(linePatternId);
                    }

                    // Set fill pattern if provided
                    if (parameters["fillPatternId"] != null)
                    {
                        int fillPatternIdInt = parameters["fillPatternId"].ToObject<int>();
                        ElementId fillPatternId = new ElementId(fillPatternIdInt);
                        overrides.SetSurfaceForegroundPatternId(fillPatternId);
                    }

                    // Set fill color if provided
                    if (parameters["fillColor"] != null)
                    {
                        var colorData = parameters["fillColor"];
                        Color fillColor = new Color(
                            colorData["r"].ToObject<byte>(),
                            colorData["g"].ToObject<byte>(),
                            colorData["b"].ToObject<byte>()
                        );
                        overrides.SetSurfaceForegroundPatternColor(fillColor);
                    }

                    // Set transparency if provided (0-100)
                    if (parameters["transparency"] != null)
                    {
                        int transparency = parameters["transparency"].ToObject<int>();
                        overrides.SetSurfaceTransparency(transparency);
                    }

                    // Set halftone if provided
                    if (parameters["halftone"] != null)
                    {
                        bool halftone = parameters["halftone"].ToObject<bool>();
                        overrides.SetHalftone(halftone);
                    }

                    view.SetElementOverrides(elementId, overrides);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        elementId = elementIdInt,
                        message = "Graphics overrides applied successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets graphics overrides for an element in a view
        /// </summary>
        [MCPMethod("getElementGraphicsOverrides", Category = "Detail", Description = "Gets graphics overrides for an element in a view")]
        public static string GetElementGraphicsOverrides(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and elementId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                OverrideGraphicSettings overrides = view.GetElementOverrides(elementId);

                // Extract projection line color
                Color projLineColor = overrides.ProjectionLineColor;
                object lineColor = projLineColor.IsValid ? new
                {
                    r = projLineColor.Red,
                    g = projLineColor.Green,
                    b = projLineColor.Blue
                } : null;

                // Extract cut line color
                Color cutColor = overrides.CutLineColor;
                object cutLineColor = cutColor.IsValid ? new
                {
                    r = cutColor.Red,
                    g = cutColor.Green,
                    b = cutColor.Blue
                } : null;

                // Extract surface color
                Color surfColor = overrides.SurfaceForegroundPatternColor;
                object surfaceColor = surfColor.IsValid ? new
                {
                    r = surfColor.Red,
                    g = surfColor.Green,
                    b = surfColor.Blue
                } : null;

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    elementId = elementIdInt,
                    overrides = new
                    {
                        projectionLineColor = lineColor,
                        projectionLineWeight = overrides.ProjectionLineWeight,
                        projectionLinePatternId = overrides.ProjectionLinePatternId.Value != -1 ? (int)overrides.ProjectionLinePatternId.Value : -1,
                        cutLineColor = cutLineColor,
                        cutLineWeight = overrides.CutLineWeight,
                        cutLinePatternId = overrides.CutLinePatternId.Value != -1 ? (int)overrides.CutLinePatternId.Value : -1,
                        surfaceForegroundPatternId = overrides.SurfaceForegroundPatternId.Value != -1 ? (int)overrides.SurfaceForegroundPatternId.Value : -1,
                        surfaceForegroundPatternColor = surfaceColor,
                        surfaceTransparency = overrides.Transparency,
                        halftone = overrides.Halftone
                    }
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Clears graphics overrides for an element in a view
        /// </summary>
        [MCPMethod("clearElementGraphicsOverrides", Category = "Detail", Description = "Clears graphics overrides for an element in a view")]
        public static string ClearElementGraphicsOverrides(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and elementId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Clear Element Graphics Overrides"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Set empty overrides to clear all existing overrides
                    OverrideGraphicSettings overrides = new OverrideGraphicSettings();
                    view.SetElementOverrides(elementId, overrides);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        elementId = elementIdInt,
                        message = "Graphics overrides cleared successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Deletes a detail element
        /// </summary>
        [MCPMethod("deleteDetailElement", Category = "Detail", Description = "Deletes a detail element from the document")]
        public static string DeleteDetailElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                string elementType = element.GetType().Name;
                string elementName = element.Name;

                using (var trans = new Transaction(doc, "Delete Detail Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ICollection<ElementId> deletedIds = doc.Delete(elementId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedElementId = elementIdInt,
                        deletedElementType = elementType,
                        deletedElementName = elementName,
                        deletedCount = deletedIds.Count,
                        message = $"Element deleted successfully (total {deletedIds.Count} elements removed including dependencies)"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Copies detail elements from one view to another
        /// </summary>
        [MCPMethod("copyDetailElements", Category = "Detail", Description = "Copies detail elements within or between views")]
        public static string CopyDetailElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceViewId"] == null || parameters["targetViewId"] == null || parameters["elementIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId, targetViewId, and elementIds are required"
                    });
                }

                int sourceViewIdInt = parameters["sourceViewId"].ToObject<int>();
                ElementId sourceViewId = new ElementId(sourceViewIdInt);

                int targetViewIdInt = parameters["targetViewId"].ToObject<int>();
                ElementId targetViewId = new ElementId(targetViewIdInt);

                var elementIdsInt = parameters["elementIds"].ToObject<List<int>>();

                View sourceView = doc.GetElement(sourceViewId) as View;
                if (sourceView == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source view with ID {sourceViewIdInt} not found"
                    });
                }

                View targetView = doc.GetElement(targetViewId) as View;
                if (targetView == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target view with ID {targetViewIdInt} not found"
                    });
                }

                // Convert element IDs
                ICollection<ElementId> elementIds = new List<ElementId>();
                foreach (int id in elementIdsInt)
                {
                    elementIds.Add(new ElementId(id));
                }

                // Get offset if provided
                XYZ offset = XYZ.Zero;
                if (parameters["offset"] != null)
                {
                    var offsetData = parameters["offset"];
                    offset = new XYZ(
                        offsetData["x"]?.ToObject<double>() ?? 0,
                        offsetData["y"]?.ToObject<double>() ?? 0,
                        offsetData["z"]?.ToObject<double>() ?? 0
                    );
                }

                using (var trans = new Transaction(doc, "Copy Detail Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Copy elements using ElementTransformUtils
                    ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElements(
                        sourceView,
                        elementIds,
                        targetView,
                        null, // No transform (copy in place)
                        new CopyPasteOptions()
                    );

                    // Apply offset if specified
                    if (!offset.IsZeroLength() && copiedIds.Count > 0)
                    {
                        ElementTransformUtils.MoveElements(doc, copiedIds, offset);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sourceViewId = sourceViewIdInt,
                        targetViewId = targetViewIdInt,
                        sourceElementsCount = elementIds.Count,
                        copiedElementsCount = copiedIds.Count,
                        copiedElementIds = copiedIds.Select(id => (int)id.Value).ToList(),
                        offset = offset.IsZeroLength() ? null : new { x = offset.X, y = offset.Y, z = offset.Z }
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Detail Library and Pattern Operations

        /// <summary>
        /// Creates a reusable detail component package by extracting detail elements from a view region.
        /// Captures detail lines, filled regions, text notes, and detail components for reuse.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - sourceViewId (required): View ID containing the detail
        /// - boundingBox (required): {minX, minY, maxX, maxY} region to capture
        /// - libraryName (required): Name for the detail package
        /// - includeTextNotes (optional): Include text annotations (default: true)
        /// - includeDimensions (optional): Include dimensions (default: false)
        /// </param>
        /// <returns>JSON response with captured detail elements and library info</returns>
        [MCPMethod("createDetailComponentLibrary", Category = "Detail", Description = "Creates a detail component library from view content")]
        public static string CreateDetailComponentLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["sourceViewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId is required"
                    });
                }

                if (parameters["boundingBox"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "boundingBox with minX, minY, maxX, maxY is required"
                    });
                }

                string libraryName = parameters["libraryName"]?.ToString() ?? "Detail Library";
                bool includeTextNotes = parameters["includeTextNotes"]?.ToObject<bool>() ?? true;
                bool includeDimensions = parameters["includeDimensions"]?.ToObject<bool>() ?? false;

                int sourceViewIdInt = parameters["sourceViewId"].ToObject<int>();
                ElementId sourceViewId = new ElementId(sourceViewIdInt);
                View sourceView = doc.GetElement(sourceViewId) as View;

                if (sourceView == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {sourceViewIdInt} not found"
                    });
                }

                // Parse bounding box
                double minX = parameters["boundingBox"]["minX"]?.ToObject<double>() ?? 0;
                double minY = parameters["boundingBox"]["minY"]?.ToObject<double>() ?? 0;
                double maxX = parameters["boundingBox"]["maxX"]?.ToObject<double>() ?? 10;
                double maxY = parameters["boundingBox"]["maxY"]?.ToObject<double>() ?? 10;

                XYZ minPt = new XYZ(minX, minY, -1000);
                XYZ maxPt = new XYZ(maxX, maxY, 1000);
                Outline outline = new Outline(minPt, maxPt);
                BoundingBoxIntersectsFilter bbFilter = new BoundingBoxIntersectsFilter(outline);

                // Collect detail elements in region
                var detailLines = new FilteredElementCollector(doc, sourceViewId)
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .WherePasses(bbFilter)
                    .ToList();

                var filledRegions = new FilteredElementCollector(doc, sourceViewId)
                    .OfClass(typeof(FilledRegion))
                    .WherePasses(bbFilter)
                    .ToList();

                var detailComponents = new FilteredElementCollector(doc, sourceViewId)
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .WherePasses(bbFilter)
                    .ToList();

                var textNotes = includeTextNotes ?
                    new FilteredElementCollector(doc, sourceViewId)
                        .OfClass(typeof(TextNote))
                        .WherePasses(bbFilter)
                        .ToList() : new List<Element>();

                var dimensions = includeDimensions ?
                    new FilteredElementCollector(doc, sourceViewId)
                        .OfClass(typeof(Dimension))
                        .WherePasses(bbFilter)
                        .ToList() : new List<Element>();

                // Build library data structure
                var libraryElements = new List<object>();
                XYZ centerPoint = new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0);

                // Process detail lines
                foreach (var elem in detailLines)
                {
                    if (elem is DetailLine detailLine)
                    {
                        var curve = detailLine.GeometryCurve;
                        if (curve is Line line)
                        {
                            libraryElements.Add(new
                            {
                                type = "detailLine",
                                elementId = (int)elem.Id.Value,
                                startX = line.GetEndPoint(0).X - centerPoint.X,
                                startY = line.GetEndPoint(0).Y - centerPoint.Y,
                                endX = line.GetEndPoint(1).X - centerPoint.X,
                                endY = line.GetEndPoint(1).Y - centerPoint.Y,
                                lineStyleId = (int)detailLine.LineStyle.Id.Value,
                                lineStyleName = detailLine.LineStyle.Name
                            });
                        }
                    }
                }

                // Process filled regions
                foreach (var elem in filledRegions)
                {
                    if (elem is FilledRegion fr)
                    {
                        var boundaries = new List<object>();
                        var loops = fr.GetBoundaries();
                        foreach (var loop in loops)
                        {
                            var points = new List<object>();
                            foreach (var curve in loop)
                            {
                                var pt = curve.GetEndPoint(0);
                                points.Add(new
                                {
                                    x = pt.X - centerPoint.X,
                                    y = pt.Y - centerPoint.Y
                                });
                            }
                            boundaries.Add(points);
                        }

                        libraryElements.Add(new
                        {
                            type = "filledRegion",
                            elementId = (int)elem.Id.Value,
                            typeId = (int)fr.GetTypeId().Value,
                            typeName = doc.GetElement(fr.GetTypeId())?.Name ?? "Unknown",
                            boundaries = boundaries
                        });
                    }
                }

                // Process detail components
                foreach (var elem in detailComponents)
                {
                    if (elem is FamilyInstance fi)
                    {
                        var loc = fi.Location as LocationPoint;
                        if (loc != null)
                        {
                            libraryElements.Add(new
                            {
                                type = "detailComponent",
                                elementId = (int)elem.Id.Value,
                                familyName = fi.Symbol.Family.Name,
                                typeName = fi.Symbol.Name,
                                typeId = (int)fi.Symbol.Id.Value,
                                x = loc.Point.X - centerPoint.X,
                                y = loc.Point.Y - centerPoint.Y,
                                rotation = loc.Rotation
                            });
                        }
                    }
                }

                // Process text notes
                foreach (var elem in textNotes)
                {
                    if (elem is TextNote tn)
                    {
                        libraryElements.Add(new
                        {
                            type = "textNote",
                            elementId = (int)elem.Id.Value,
                            text = tn.Text,
                            x = tn.Coord.X - centerPoint.X,
                            y = tn.Coord.Y - centerPoint.Y,
                            typeId = (int)tn.GetTypeId().Value
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    libraryName = libraryName,
                    sourceViewId = sourceViewIdInt,
                    sourceViewName = sourceView.Name,
                    centerPoint = new { x = centerPoint.X, y = centerPoint.Y },
                    boundingBox = new { minX, minY, maxX, maxY },
                    statistics = new
                    {
                        detailLines = detailLines.Count,
                        filledRegions = filledRegions.Count,
                        detailComponents = detailComponents.Count,
                        textNotes = textNotes.Count,
                        dimensions = dimensions.Count,
                        totalElements = libraryElements.Count
                    },
                    elements = libraryElements,
                    message = $"Captured {libraryElements.Count} detail elements for library '{libraryName}'"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Extracts filled regions matching criteria and replaces them with a different pattern type.
        /// Useful for standardizing hatching patterns across a project.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - viewId (optional): Limit to specific view, or null for all views
        /// - sourceTypeId (required): Filled region type ID to find
        /// - targetTypeId (required): Filled region type ID to replace with
        /// - dryRun (optional): Preview only, don't make changes (default: false)
        /// </param>
        /// <returns>JSON response with replacement results</returns>
        [MCPMethod("extractAndReplaceFilledRegions", Category = "Detail", Description = "Extracts and replaces filled regions in a view")]
        public static string ExtractAndReplaceFilledRegions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceTypeId"] == null || parameters["targetTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceTypeId and targetTypeId are required"
                    });
                }

                int sourceTypeIdInt = parameters["sourceTypeId"].ToObject<int>();
                int targetTypeIdInt = parameters["targetTypeId"].ToObject<int>();
                bool dryRun = parameters["dryRun"]?.ToObject<bool>() ?? false;

                ElementId sourceTypeId = new ElementId(sourceTypeIdInt);
                ElementId targetTypeId = new ElementId(targetTypeIdInt);

                // Verify types exist
                var sourceType = doc.GetElement(sourceTypeId) as FilledRegionType;
                var targetType = doc.GetElement(targetTypeId) as FilledRegionType;

                if (sourceType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source filled region type with ID {sourceTypeIdInt} not found"
                    });
                }

                if (targetType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target filled region type with ID {targetTypeIdInt} not found"
                    });
                }

                // Collect filled regions
                FilteredElementCollector collector;
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    ElementId viewId = new ElementId(viewIdInt);
                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var filledRegions = collector
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .Where(fr => fr.GetTypeId() == sourceTypeId)
                    .ToList();

                var results = new List<object>();
                int replacedCount = 0;

                if (!dryRun && filledRegions.Count > 0)
                {
                    using (var trans = new Transaction(doc, "Replace Filled Region Types"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        foreach (var fr in filledRegions)
                        {
                            try
                            {
                                fr.ChangeTypeId(targetTypeId);
                                results.Add(new
                                {
                                    elementId = (int)fr.Id.Value,
                                    viewId = (int)fr.OwnerViewId.Value,
                                    status = "replaced"
                                });
                                replacedCount++;
                            }
                            catch (Exception ex)
                            {
                                results.Add(new
                                {
                                    elementId = (int)fr.Id.Value,
                                    viewId = (int)fr.OwnerViewId.Value,
                                    status = "failed",
                                    error = ex.Message
                                });
                            }
                        }

                        trans.Commit();
                    }
                }
                else
                {
                    // Dry run - just report what would change
                    foreach (var fr in filledRegions)
                    {
                        results.Add(new
                        {
                            elementId = (int)fr.Id.Value,
                            viewId = (int)fr.OwnerViewId.Value,
                            status = "would_replace"
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    dryRun = dryRun,
                    sourceType = new
                    {
                        typeId = sourceTypeIdInt,
                        typeName = sourceType.Name
                    },
                    targetType = new
                    {
                        typeId = targetTypeIdInt,
                        typeName = targetType.Name
                    },
                    foundCount = filledRegions.Count,
                    replacedCount = dryRun ? 0 : replacedCount,
                    results = results,
                    message = dryRun ?
                        $"Dry run: {filledRegions.Count} filled regions would be replaced" :
                        $"Replaced {replacedCount} filled regions from '{sourceType.Name}' to '{targetType.Name}'"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Drafting View Enhancement Methods

        /// <summary>
        /// Get all available break line family types in the project.
        /// Use the returned typeId with placeBreakLineAuto or createBreakLine.
        /// </summary>
        [MCPMethod("getBreakLineTypes", Category = "Detail", Description = "Gets all break line types in the document")]
        public static string GetBreakLineTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Find all detail component families that might be break lines
                var breakLineTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family.Name.ToLower().Contains("break") ||
                                 fs.Name.ToLower().Contains("break"))
                    .Select(fs => new
                    {
                        typeId = fs.Id.Value,
                        familyName = fs.Family.Name,
                        typeName = fs.Name,
                        isActive = fs.IsActive
                    })
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = breakLineTypes.Count,
                    breakLineTypes = breakLineTypes
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Place a break line symbol with automatic type detection.
        /// Parameters:
        ///   viewId (required): Target view ID
        ///   x, y (required): Location in view coordinates
        ///   rotation (optional): Rotation in degrees (default: 0)
        ///   typeName (optional): Specific break line type name to use
        /// </summary>
        [MCPMethod("placeBreakLineAuto", Category = "Detail", Description = "Places a break line automatically in a view")]
        public static string PlaceBreakLineAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["x"] == null || parameters["y"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, x, and y parameters are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                double x = parameters["x"].ToObject<double>();
                double y = parameters["y"].ToObject<double>();
                double z = parameters["z"]?.ToObject<double>() ?? 0;
                double rotation = parameters["rotation"]?.ToObject<double>() ?? 0;
                string typeName = parameters["typeName"]?.ToString();

                // Find break line symbol
                FamilySymbol breakLineSymbol = null;

                var breakLineTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family.Name.ToLower().Contains("break") ||
                                 fs.Name.ToLower().Contains("break"))
                    .ToList();

                if (breakLineTypes.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No break line family types found in project. Load a break line family first."
                    });
                }

                if (!string.IsNullOrEmpty(typeName))
                {
                    breakLineSymbol = breakLineTypes.FirstOrDefault(fs =>
                        fs.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                        fs.Family.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                }

                if (breakLineSymbol == null)
                {
                    breakLineSymbol = breakLineTypes.First();
                }

                // For section views, z is the elevation; for drafting views, z should be 0
                XYZ location = new XYZ(x, y, z);

                using (var trans = new Transaction(doc, "MCP Place Break Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!breakLineSymbol.IsActive)
                    {
                        breakLineSymbol.Activate();
                    }

                    var breakLine = doc.Create.NewFamilyInstance(location, breakLineSymbol, view);

                    // Apply rotation if specified
                    if (Math.Abs(rotation) > 0.001)
                    {
                        double rotationRadians = rotation * (Math.PI / 180.0);
                        XYZ rotationAxis = view.ViewDirection;
                        Line rotationLine = Line.CreateBound(location, location + rotationAxis);
                        ElementTransformUtils.RotateElement(doc, breakLine.Id, rotationLine, rotationRadians);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        breakLineId = breakLine.Id.Value,
                        familyName = breakLineSymbol.Family.Name,
                        typeName = breakLineSymbol.Name,
                        location = new { x, y, z },
                        rotation
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Create a detail line in a drafting view using view-relative coordinates.
        /// This method properly handles the drafting view coordinate system.
        /// Parameters:
        ///   viewId (required): Target drafting view ID
        ///   startX, startY (required): Start point in view coordinates
        ///   endX, endY (required): End point in view coordinates
        ///   lineStyle (optional): Name of line style to use
        /// </summary>
        [MCPMethod("createDetailLineInDraftingView", Category = "Detail", Description = "Creates a detail line in a drafting view")]
        public static string CreateDetailLineInDraftingView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                double startX = parameters["startX"]?.ToObject<double>() ?? 0;
                double startY = parameters["startY"]?.ToObject<double>() ?? 0;
                double endX = parameters["endX"]?.ToObject<double>() ?? 0;
                double endY = parameters["endY"]?.ToObject<double>() ?? 0;
                string lineStyleName = parameters["lineStyle"]?.ToString();

                // For drafting views, coordinates are in the view's plane (Z=0)
                XYZ startPoint = new XYZ(startX, startY, 0);
                XYZ endPoint = new XYZ(endX, endY, 0);

                // Ensure points are different
                if (startPoint.IsAlmostEqualTo(endPoint))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Start and end points cannot be the same"
                    });
                }

                var line = Line.CreateBound(startPoint, endPoint);

                using (var trans = new Transaction(doc, "MCP Create Detail Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var detailCurve = doc.Create.NewDetailCurve(view, line);

                    // Set line style if specified
                    if (!string.IsNullOrEmpty(lineStyleName) && detailCurve != null)
                    {
                        var lineStyle = new FilteredElementCollector(doc)
                            .OfClass(typeof(GraphicsStyle))
                            .Cast<GraphicsStyle>()
                            .FirstOrDefault(gs => gs.Name.Equals(lineStyleName, StringComparison.OrdinalIgnoreCase));

                        if (lineStyle != null)
                        {
                            detailCurve.LineStyle = lineStyle;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineId = detailCurve?.Id?.Value ?? -1,
                        viewId = viewIdInt,
                        start = new { x = startX, y = startY },
                        end = new { x = endX, y = endY },
                        lineStyle = detailCurve?.LineStyle?.Name ?? "Default",
                        length = line.Length
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Dimension between two detail lines or their endpoints.
        /// This is specifically designed for drafting/detail views where model elements don't exist.
        /// Parameters:
        ///   viewId (required): Target view ID
        ///   lineId1 (required): First detail line element ID
        ///   lineId2 (required): Second detail line element ID
        ///   dimensionOffset (optional): Offset distance for dimension line placement
        ///   direction (optional): "horizontal" or "vertical" - forces dimension direction
        /// </summary>
        [MCPMethod("dimensionDetailLines", Category = "Detail", Description = "Adds dimensions to detail lines in a view")]
        public static string DimensionDetailLines(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["lineId1"] == null || parameters["lineId2"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, lineId1, and lineId2 are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                int lineId1Int = parameters["lineId1"].ToObject<int>();
                int lineId2Int = parameters["lineId2"].ToObject<int>();
                double offset = parameters["dimensionOffset"]?.ToObject<double>() ?? 0.5;
                string direction = parameters["direction"]?.ToString()?.ToLower();

                var line1 = doc.GetElement(new ElementId(lineId1Int)) as DetailCurve;
                var line2 = doc.GetElement(new ElementId(lineId2Int)) as DetailCurve;

                if (line1 == null || line2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "One or both detail lines not found"
                    });
                }

                // Get geometry curves
                var curve1 = line1.GeometryCurve as Line;
                var curve2 = line2.GeometryCurve as Line;

                if (curve1 == null || curve2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Elements must be straight detail lines"
                    });
                }

                using (var trans = new Transaction(doc, "MCP Dimension Detail Lines"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get references from the detail curves
                    var refArray = new ReferenceArray();
                    refArray.Append(line1.GeometryCurve.Reference);
                    refArray.Append(line2.GeometryCurve.Reference);

                    // Calculate dimension line location
                    XYZ midPoint1 = curve1.Evaluate(0.5, true);
                    XYZ midPoint2 = curve2.Evaluate(0.5, true);

                    // Determine dimension line direction based on curves or user preference
                    XYZ dimLineDir;
                    if (direction == "horizontal")
                    {
                        dimLineDir = XYZ.BasisX;
                    }
                    else if (direction == "vertical")
                    {
                        dimLineDir = XYZ.BasisY;
                    }
                    else
                    {
                        // Auto-detect: perpendicular to line connecting midpoints
                        XYZ connectionDir = (midPoint2 - midPoint1).Normalize();
                        dimLineDir = new XYZ(-connectionDir.Y, connectionDir.X, 0);
                    }

                    // Create dimension line
                    XYZ dimLinePoint = (midPoint1 + midPoint2) / 2 + dimLineDir * offset;
                    Line dimLine = Line.CreateBound(
                        dimLinePoint - dimLineDir * 10,
                        dimLinePoint + dimLineDir * 10
                    );

                    var dimension = doc.Create.NewDimension(view, dimLine, refArray);

                    trans.Commit();

                    if (dimension == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create dimension - lines may not be suitable for dimensioning"
                        });
                    }

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        dimensionId = dimension.Id.Value,
                        viewId = viewIdInt,
                        lineId1 = lineId1Int,
                        lineId2 = lineId2Int,
                        value = dimension.Value.HasValue ? dimension.Value.Value : 0,
                        valueString = dimension.ValueString
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Get the bounds of a drafting view for element placement.
        /// Returns the coordinate extents of existing elements or the view's outline.
        /// </summary>
        [MCPMethod("getDraftingViewBounds", Category = "Detail", Description = "Gets the bounding box of a drafting view")]
        public static string GetDraftingViewBounds(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                // Get all elements in the view
                var collector = new FilteredElementCollector(doc, viewId);
                var elements = collector.ToElements();

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var elem in elements)
                {
                    var bbox = elem.get_BoundingBox(view);
                    if (bbox != null)
                    {
                        if (bbox.Min.X < minX) minX = bbox.Min.X;
                        if (bbox.Min.Y < minY) minY = bbox.Min.Y;
                        if (bbox.Max.X > maxX) maxX = bbox.Max.X;
                        if (bbox.Max.Y > maxY) maxY = bbox.Max.Y;
                    }
                }

                if (minX == double.MaxValue)
                {
                    // No elements found, use default bounds
                    minX = -1; minY = -1;
                    maxX = 1; maxY = 1;
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    bounds = new
                    {
                        minX = Math.Round(minX, 4),
                        minY = Math.Round(minY, 4),
                        maxX = Math.Round(maxX, 4),
                        maxY = Math.Round(maxY, 4),
                        width = Math.Round(maxX - minX, 4),
                        height = Math.Round(maxY - minY, 4),
                        centerX = Math.Round((minX + maxX) / 2, 4),
                        centerY = Math.Round((minY + maxY) / 2, 4)
                    },
                    elementCount = elements.Count
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Place an insulation batt pattern between two points.
        /// Uses the repeating detail component system for consistent spacing.
        /// Parameters:
        ///   viewId (required): Target view ID
        ///   startX, startY (required): Start point
        ///   endX, endY (required): End point
        ///   width (optional): Insulation width in feet (default: 0.333 = 4")
        ///   typeName (optional): Specific insulation type name
        /// </summary>
        [MCPMethod("placeInsulationPattern", Category = "Detail", Description = "Places an insulation pattern between two lines")]
        public static string PlaceInsulationPattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                double startX = parameters["startX"]?.ToObject<double>() ?? 0;
                double startY = parameters["startY"]?.ToObject<double>() ?? 0;
                double endX = parameters["endX"]?.ToObject<double>() ?? 0;
                double endY = parameters["endY"]?.ToObject<double>() ?? 0;
                double width = parameters["width"]?.ToObject<double>() ?? 0.333; // 4" default
                string typeName = parameters["typeName"]?.ToString();

                // Find insulation detail component
                var insulationTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family.Name.ToLower().Contains("insul") ||
                                 fs.Name.ToLower().Contains("insul") ||
                                 fs.Family.Name.ToLower().Contains("batt") ||
                                 fs.Name.ToLower().Contains("batt"))
                    .ToList();

                if (insulationTypes.Count == 0)
                {
                    // Try to find any repeating detail that might be insulation
                    insulationTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_DetailComponents)
                        .Cast<FamilySymbol>()
                        .Where(fs => fs.Name.Contains("\"") &&
                               (fs.Name.Contains("4") || fs.Name.Contains("6") || fs.Name.Contains("8")))
                        .Take(5)
                        .ToList();
                }

                FamilySymbol insulationSymbol = null;
                if (!string.IsNullOrEmpty(typeName))
                {
                    insulationSymbol = insulationTypes.FirstOrDefault(fs =>
                        fs.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                }

                if (insulationSymbol == null && insulationTypes.Count > 0)
                {
                    insulationSymbol = insulationTypes.First();
                }

                if (insulationSymbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No insulation detail component found. Load an insulation family first.",
                        availableTypes = insulationTypes.Select(t => t.Name).ToList()
                    });
                }

                XYZ startPoint = new XYZ(startX, startY, 0);
                XYZ endPoint = new XYZ(endX, endY, 0);
                Line path = Line.CreateBound(startPoint, endPoint);

                using (var trans = new Transaction(doc, "MCP Place Insulation Pattern"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!insulationSymbol.IsActive)
                    {
                        insulationSymbol.Activate();
                    }

                    // Calculate number of insulation segments
                    double length = path.Length;
                    int segments = Math.Max(1, (int)(length / width));
                    var placedIds = new List<int>();

                    for (int i = 0; i < segments; i++)
                    {
                        double param = (i + 0.5) / segments;
                        XYZ location = path.Evaluate(param, true);

                        var instance = doc.Create.NewFamilyInstance(location, insulationSymbol, view);
                        if (instance != null)
                        {
                            placedIds.Add((int)instance.Id.Value);
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        segmentsPlaced = placedIds.Count,
                        insulationType = insulationSymbol.Name,
                        familyName = insulationSymbol.Family.Name,
                        elementIds = placedIds,
                        length = Math.Round(length, 4)
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region View Conversion

        /// <summary>
        /// Converts a section/detail view to a drafting view by copying all 2D content.
        /// Extracts detail lines, filled regions, detail components, text notes, and dimensions
        /// from the source view and recreates them in a new drafting view.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - sourceViewId (required): View ID of the section/detail view to convert
        /// - newViewName (optional): Name for the new drafting view (default: "Drafting - [SourceName]")
        /// - includeTextNotes (optional): Include text annotations (default: true)
        /// - includeDimensions (optional): Include dimensions (default: true)
        /// - includeDetailComponents (optional): Include detail components (default: true)
        /// - includeFilledRegions (optional): Include filled regions (default: true)
        /// - includeDetailLines (optional): Include detail lines (default: true)
        /// </param>
        /// <returns>JSON response with new drafting view ID and copied element counts</returns>
        [MCPMethod("convertDetailToDraftingView", Category = "Detail", Description = "Converts a detail view to a drafting view")]
        public static string ConvertDetailToDraftingView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["sourceViewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId is required"
                    });
                }

                int sourceViewIdInt = parameters["sourceViewId"].ToObject<int>();
                ElementId sourceViewId = new ElementId(sourceViewIdInt);
                View sourceView = doc.GetElement(sourceViewId) as View;

                if (sourceView == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {sourceViewIdInt} not found"
                    });
                }

                // Check if source view can be converted (section, detail, callout, elevation)
                var validViewTypes = new[] {
                    ViewType.Section,
                    ViewType.Detail,
                    ViewType.Elevation,
                    ViewType.CeilingPlan,
                    ViewType.FloorPlan,
                    ViewType.EngineeringPlan
                };

                if (!validViewTypes.Contains(sourceView.ViewType))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View type '{sourceView.ViewType}' cannot be converted. Expected section, detail, elevation, or plan view."
                    });
                }

                // Parse options
                string newViewName = parameters["newViewName"]?.ToString() ?? $"Drafting - {sourceView.Name}";
                bool includeTextNotes = parameters["includeTextNotes"]?.ToObject<bool>() ?? true;
                bool includeDimensions = parameters["includeDimensions"]?.ToObject<bool>() ?? true;
                bool includeDetailComponents = parameters["includeDetailComponents"]?.ToObject<bool>() ?? true;
                bool includeFilledRegions = parameters["includeFilledRegions"]?.ToObject<bool>() ?? true;
                bool includeDetailLines = parameters["includeDetailLines"]?.ToObject<bool>() ?? true;

                // Get drafting view family type
                var draftingViewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting);

                if (draftingViewFamilyType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No drafting view family type found in project"
                    });
                }

                // Collect elements from source view
                var elementsToConvert = new List<ElementId>();
                var elementCounts = new Dictionary<string, int>();

                // Detail Lines (category OST_Lines includes detail curves)
                if (includeDetailLines)
                {
                    var detailLines = new FilteredElementCollector(doc, sourceViewId)
                        .OfCategory(BuiltInCategory.OST_Lines)
                        .WhereElementIsNotElementType()
                        .ToElementIds();
                    elementsToConvert.AddRange(detailLines);
                    elementCounts["detailLines"] = detailLines.Count;
                }

                // Filled Regions
                if (includeFilledRegions)
                {
                    var filledRegions = new FilteredElementCollector(doc, sourceViewId)
                        .OfClass(typeof(FilledRegion))
                        .ToElementIds();
                    elementsToConvert.AddRange(filledRegions);
                    elementCounts["filledRegions"] = filledRegions.Count;
                }

                // Detail Components
                if (includeDetailComponents)
                {
                    var detailComponents = new FilteredElementCollector(doc, sourceViewId)
                        .OfCategory(BuiltInCategory.OST_DetailComponents)
                        .WhereElementIsNotElementType()
                        .ToElementIds();
                    elementsToConvert.AddRange(detailComponents);
                    elementCounts["detailComponents"] = detailComponents.Count;
                }

                // Text Notes
                if (includeTextNotes)
                {
                    var textNotes = new FilteredElementCollector(doc, sourceViewId)
                        .OfClass(typeof(TextNote))
                        .ToElementIds();
                    elementsToConvert.AddRange(textNotes);
                    elementCounts["textNotes"] = textNotes.Count;
                }

                // Dimensions
                if (includeDimensions)
                {
                    var dimensions = new FilteredElementCollector(doc, sourceViewId)
                        .OfClass(typeof(Dimension))
                        .ToElementIds();
                    elementsToConvert.AddRange(dimensions);
                    elementCounts["dimensions"] = dimensions.Count;
                }

                // Also get generic annotations
                var genericAnnotations = new FilteredElementCollector(doc, sourceViewId)
                    .OfCategory(BuiltInCategory.OST_GenericAnnotation)
                    .WhereElementIsNotElementType()
                    .ToElementIds();
                if (genericAnnotations.Count > 0)
                {
                    elementsToConvert.AddRange(genericAnnotations);
                    elementCounts["genericAnnotations"] = genericAnnotations.Count;
                }

                // Get masking regions (uses same FilledRegion class as filled regions)
                // Note: Masking regions are already included in the FilledRegion collector above

                if (elementsToConvert.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No elements found in source view to convert"
                    });
                }

                // Perform conversion in transaction
                ViewDrafting newDraftingView = null;
                ICollection<ElementId> copiedElementIds = null;

                using (var trans = new Transaction(doc, "Convert Detail to Drafting View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create new drafting view
                    newDraftingView = ViewDrafting.Create(doc, draftingViewFamilyType.Id);
                    newDraftingView.Name = newViewName;
                    newDraftingView.Scale = sourceView.Scale;

                    trans.Commit();
                }

                // Copy elements in separate transaction (required for view-to-view copy)
                using (var trans = new Transaction(doc, "Copy Elements to Drafting View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        // Copy elements from source to new drafting view
                        copiedElementIds = ElementTransformUtils.CopyElements(
                            sourceView,
                            elementsToConvert,
                            newDraftingView,
                            Transform.Identity,
                            new CopyPasteOptions()
                        );
                    }
                    catch (Exception copyEx)
                    {
                        trans.RollBack();

                        // Delete the created view since copy failed
                        using (var cleanupTrans = new Transaction(doc, "Cleanup Failed Conversion"))
                        {
                            cleanupTrans.Start();
                            doc.Delete(newDraftingView.Id);
                            cleanupTrans.Commit();
                        }

                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Failed to copy elements: {copyEx.Message}",
                            elementsAttempted = elementsToConvert.Count
                        });
                    }

                    trans.Commit();
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceViewId = sourceViewIdInt,
                    sourceViewName = sourceView.Name,
                    sourceViewType = sourceView.ViewType.ToString(),
                    newDraftingViewId = (int)newDraftingView.Id.Value,
                    newDraftingViewName = newDraftingView.Name,
                    scale = newDraftingView.Scale,
                    elementsFound = elementsToConvert.Count,
                    elementsCopied = copiedElementIds?.Count ?? 0,
                    elementCounts = elementCounts,
                    copiedElementIds = copiedElementIds?.Select(id => (int)id.Value).ToList()
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Batch converts multiple detail/section views to drafting views.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - sourceViewIds (required): Array of view IDs to convert
        /// - namePrefix (optional): Prefix for new view names (default: "Drafting - ")
        /// - includeTextNotes (optional): Include text annotations (default: true)
        /// - includeDimensions (optional): Include dimensions (default: true)
        /// - includeDetailComponents (optional): Include detail components (default: true)
        /// - includeFilledRegions (optional): Include filled regions (default: true)
        /// - includeDetailLines (optional): Include detail lines (default: true)
        /// </param>
        /// <returns>JSON response with array of conversion results</returns>
        [MCPMethod("batchConvertDetailsToDraftingViews", Category = "Detail", Description = "Batch converts detail views to drafting views")]
        public static string BatchConvertDetailsToDraftingViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceViewIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewIds array is required"
                    });
                }

                var sourceViewIds = parameters["sourceViewIds"].ToObject<List<int>>();
                string namePrefix = parameters["namePrefix"]?.ToString() ?? "Drafting - ";

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var viewId in sourceViewIds)
                {
                    // Create parameters for individual conversion
                    var conversionParams = new JObject
                    {
                        ["sourceViewId"] = viewId,
                        ["includeTextNotes"] = parameters["includeTextNotes"] ?? true,
                        ["includeDimensions"] = parameters["includeDimensions"] ?? true,
                        ["includeDetailComponents"] = parameters["includeDetailComponents"] ?? true,
                        ["includeFilledRegions"] = parameters["includeFilledRegions"] ?? true,
                        ["includeDetailLines"] = parameters["includeDetailLines"] ?? true
                    };

                    // Get view name for new view naming
                    var view = doc.GetElement(new ElementId(viewId)) as View;
                    if (view != null)
                    {
                        conversionParams["newViewName"] = namePrefix + view.Name;
                    }

                    // Convert this view
                    var resultJson = ConvertDetailToDraftingView(uiApp, conversionParams);
                    var result = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(resultJson);

                    if (result != null && result.ContainsKey("success") && (bool)result["success"])
                    {
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                    }

                    results.Add(result);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = failCount == 0,
                    totalViews = sourceViewIds.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Trace Section to Drafting View

        /// <summary>
        /// Traces a section/detail view to create a drafting view by extracting visible geometry
        /// from model elements and recreating them as 2D detail elements.
        /// This creates a true "flattened" version of the section with detail lines and filled regions.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - sourceViewId (required): View ID of the section/detail view to trace
        /// - newViewName (optional): Name for the new drafting view
        /// - traceModelGeometry (optional): Trace visible model elements (default: true)
        /// - copyAnnotations (optional): Copy existing text notes, dimensions (default: true)
        /// - copyDetailElements (optional): Copy existing detail lines, filled regions (default: true)
        /// - lineStyleId (optional): Line style ID for traced lines (default: uses medium lines)
        /// </param>
        /// <returns>JSON response with new drafting view ID and element counts</returns>
        [MCPMethod("traceDetailToDraftingView", Category = "Detail", Description = "Traces detail elements into a drafting view")]
        public static string TraceDetailToDraftingView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["sourceViewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId is required"
                    });
                }

                int sourceViewIdInt = parameters["sourceViewId"].ToObject<int>();
                ElementId sourceViewId = new ElementId(sourceViewIdInt);
                View sourceView = doc.GetElement(sourceViewId) as View;

                if (sourceView == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {sourceViewIdInt} not found"
                    });
                }

                // Check if source view is a section or detail view
                if (sourceView.ViewType != ViewType.Section &&
                    sourceView.ViewType != ViewType.Detail &&
                    sourceView.ViewType != ViewType.Elevation)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View type '{sourceView.ViewType}' is not supported. Use Section, Detail, or Elevation views."
                    });
                }

                // Parse options
                string newViewName = parameters["newViewName"]?.ToString() ?? $"Traced - {sourceView.Name}";
                bool traceModelGeometry = parameters["traceModelGeometry"]?.ToObject<bool>() ?? true;
                bool copyAnnotations = parameters["copyAnnotations"]?.ToObject<bool>() ?? true;
                bool copyDetailElements = parameters["copyDetailElements"]?.ToObject<bool>() ?? true;

                // Get crop box for view bounds
                BoundingBoxXYZ cropBox = sourceView.CropBox;
                Transform viewTransform = cropBox.Transform;
                XYZ viewOrigin = viewTransform.Origin;
                XYZ viewBasisX = viewTransform.BasisX;
                XYZ viewBasisY = viewTransform.BasisY;
                XYZ viewNormal = viewTransform.BasisZ;

                // Get crop region min/max in view coordinates
                XYZ cropMin = cropBox.Min;
                XYZ cropMax = cropBox.Max;
                double viewWidth = cropMax.X - cropMin.X;
                double viewHeight = cropMax.Y - cropMin.Y;

                // Get drafting view family type
                var draftingViewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting);

                if (draftingViewFamilyType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No drafting view family type found in project"
                    });
                }

                // Get line style for traced lines (try to find Medium Lines or use default)
                GraphicsStyle lineStyle = null;
                if (parameters["lineStyleId"] != null)
                {
                    int lineStyleIdInt = parameters["lineStyleId"].ToObject<int>();
                    lineStyle = doc.GetElement(new ElementId(lineStyleIdInt)) as GraphicsStyle;
                }
                else
                {
                    // Try to find "Medium Lines" or similar
                    var lineStyles = new FilteredElementCollector(doc)
                        .OfClass(typeof(GraphicsStyle))
                        .Cast<GraphicsStyle>()
                        .Where(gs => gs.GraphicsStyleCategory?.Parent?.Id.Value == (int)BuiltInCategory.OST_Lines)
                        .ToList();

                    lineStyle = lineStyles.FirstOrDefault(ls => ls.Name.Contains("Medium")) ??
                                lineStyles.FirstOrDefault(ls => ls.Name.Contains("Thin")) ??
                                lineStyles.FirstOrDefault();
                }

                // Statistics
                int tracedCurveCount = 0;
                int filledRegionCount = 0;
                int copiedAnnotationCount = 0;
                int copiedDetailCount = 0;
                var errors = new List<string>();

                ViewDrafting newDraftingView = null;

                // Create the drafting view
                using (var trans = new Transaction(doc, "Create Drafting View for Trace"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    newDraftingView = ViewDrafting.Create(doc, draftingViewFamilyType.Id);
                    newDraftingView.Name = newViewName;
                    newDraftingView.Scale = sourceView.Scale;

                    trans.Commit();
                }

                // Trace model geometry
                if (traceModelGeometry)
                {
                    using (var trans = new Transaction(doc, "Trace Model Geometry"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        // Get visible model elements in the view
                        var visibleCategories = new[]
                        {
                            BuiltInCategory.OST_Walls,
                            BuiltInCategory.OST_Floors,
                            BuiltInCategory.OST_Roofs,
                            BuiltInCategory.OST_Ceilings,
                            BuiltInCategory.OST_StructuralColumns,
                            BuiltInCategory.OST_StructuralFraming,
                            BuiltInCategory.OST_StructuralFoundation,
                            BuiltInCategory.OST_Doors,
                            BuiltInCategory.OST_Windows,
                            BuiltInCategory.OST_Stairs,
                            BuiltInCategory.OST_StairsRailing,
                            BuiltInCategory.OST_GenericModel
                        };

                        // Create geometry options with view-specific settings
                        var geomOptions = new Options()
                        {
                            View = sourceView,
                            IncludeNonVisibleObjects = false,
                            ComputeReferences = false
                        };

                        foreach (var category in visibleCategories)
                        {
                            try
                            {
                                var elements = new FilteredElementCollector(doc, sourceViewId)
                                    .OfCategory(category)
                                    .WhereElementIsNotElementType()
                                    .ToList();

                                foreach (var element in elements)
                                {
                                    try
                                    {
                                        // Get geometry as it appears in the section view
                                        GeometryElement geom = element.get_Geometry(geomOptions);
                                        if (geom == null) continue;

                                        // Extract curves from geometry
                                        var curves = ExtractCurvesFromGeometry(geom, viewTransform, cropMin, cropMax);

                                        foreach (var curve in curves)
                                        {
                                            try
                                            {
                                                // Transform curve to drafting view coordinates
                                                // The curve is already in view-relative coordinates from extraction
                                                var detailCurve = doc.Create.NewDetailCurve(newDraftingView, curve);
                                                if (detailCurve != null && lineStyle != null)
                                                {
                                                    detailCurve.LineStyle = lineStyle;
                                                }
                                                tracedCurveCount++;
                                            }
                                            catch
                                            {
                                                // Skip curves that can't be created
                                            }
                                        }
                                    }
                                    catch (Exception elemEx)
                                    {
                                        // Continue with other elements
                                    }
                                }
                            }
                            catch (Exception catEx)
                            {
                                errors.Add($"Error processing category {category}: {catEx.Message}");
                            }
                        }

                        trans.Commit();
                    }
                }

                // Copy existing detail elements
                if (copyDetailElements)
                {
                    using (var trans = new Transaction(doc, "Copy Detail Elements"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        var detailElementIds = new List<ElementId>();

                        // Detail lines
                        var detailLines = new FilteredElementCollector(doc, sourceViewId)
                            .OfCategory(BuiltInCategory.OST_Lines)
                            .WhereElementIsNotElementType()
                            .ToElementIds();
                        detailElementIds.AddRange(detailLines);

                        // Filled regions
                        var filledRegions = new FilteredElementCollector(doc, sourceViewId)
                            .OfClass(typeof(FilledRegion))
                            .ToElementIds();
                        detailElementIds.AddRange(filledRegions);

                        // Detail components
                        var detailComponents = new FilteredElementCollector(doc, sourceViewId)
                            .OfCategory(BuiltInCategory.OST_DetailComponents)
                            .WhereElementIsNotElementType()
                            .ToElementIds();
                        detailElementIds.AddRange(detailComponents);

                        if (detailElementIds.Count > 0)
                        {
                            try
                            {
                                var copiedIds = ElementTransformUtils.CopyElements(
                                    sourceView,
                                    detailElementIds,
                                    newDraftingView,
                                    Transform.Identity,
                                    new CopyPasteOptions()
                                );
                                copiedDetailCount = copiedIds.Count;
                            }
                            catch (Exception copyEx)
                            {
                                errors.Add($"Error copying detail elements: {copyEx.Message}");
                            }
                        }

                        trans.Commit();
                    }
                }

                // Copy annotations
                if (copyAnnotations)
                {
                    using (var trans = new Transaction(doc, "Copy Annotations"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        var annotationIds = new List<ElementId>();

                        // Text notes
                        var textNotes = new FilteredElementCollector(doc, sourceViewId)
                            .OfClass(typeof(TextNote))
                            .ToElementIds();
                        annotationIds.AddRange(textNotes);

                        // Dimensions
                        var dimensions = new FilteredElementCollector(doc, sourceViewId)
                            .OfClass(typeof(Dimension))
                            .ToElementIds();
                        annotationIds.AddRange(dimensions);

                        // Generic annotations
                        var genericAnnotations = new FilteredElementCollector(doc, sourceViewId)
                            .OfCategory(BuiltInCategory.OST_GenericAnnotation)
                            .WhereElementIsNotElementType()
                            .ToElementIds();
                        annotationIds.AddRange(genericAnnotations);

                        if (annotationIds.Count > 0)
                        {
                            try
                            {
                                var copiedIds = ElementTransformUtils.CopyElements(
                                    sourceView,
                                    annotationIds,
                                    newDraftingView,
                                    Transform.Identity,
                                    new CopyPasteOptions()
                                );
                                copiedAnnotationCount = copiedIds.Count;
                            }
                            catch (Exception copyEx)
                            {
                                errors.Add($"Error copying annotations: {copyEx.Message}");
                            }
                        }

                        trans.Commit();
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceViewId = sourceViewIdInt,
                    sourceViewName = sourceView.Name,
                    sourceViewType = sourceView.ViewType.ToString(),
                    newDraftingViewId = (int)newDraftingView.Id.Value,
                    newDraftingViewName = newDraftingView.Name,
                    scale = newDraftingView.Scale,
                    viewBounds = new
                    {
                        width = Math.Round(viewWidth, 4),
                        height = Math.Round(viewHeight, 4)
                    },
                    statistics = new
                    {
                        tracedCurves = tracedCurveCount,
                        filledRegions = filledRegionCount,
                        copiedDetailElements = copiedDetailCount,
                        copiedAnnotations = copiedAnnotationCount
                    },
                    errors = errors.Count > 0 ? errors : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Extracts curves from geometry element, transforming them to view coordinates.
        /// </summary>
        private static List<Curve> ExtractCurvesFromGeometry(GeometryElement geom, Transform viewTransform, XYZ cropMin, XYZ cropMax)
        {
            var curves = new List<Curve>();
            var viewNormal = viewTransform.BasisZ;
            var viewOrigin = viewTransform.Origin;

            foreach (GeometryObject geomObj in geom)
            {
                try
                {
                    if (geomObj is Solid solid && solid.Volume > 0)
                    {
                        // Extract edges from solid faces
                        foreach (Face face in solid.Faces)
                        {
                            try
                            {
                                // Check if face is roughly perpendicular to view direction (visible in section)
                                XYZ faceNormal = face.ComputeNormal(new UV(0.5, 0.5));
                                double dot = Math.Abs(faceNormal.DotProduct(viewNormal));

                                // Get edge loops from the face
                                foreach (EdgeArray edgeArray in face.EdgeLoops)
                                {
                                    foreach (Edge edge in edgeArray)
                                    {
                                        try
                                        {
                                            Curve edgeCurve = edge.AsCurve();
                                            if (edgeCurve != null)
                                            {
                                                // Transform curve to view coordinates
                                                Curve transformedCurve = TransformCurveToViewCoordinates(
                                                    edgeCurve, viewTransform, cropMin, cropMax);

                                                if (transformedCurve != null && IsValidCurve(transformedCurve))
                                                {
                                                    curves.Add(transformedCurve);
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            // Skip invalid edges
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Skip faces with errors
                            }
                        }
                    }
                    else if (geomObj is Curve curve)
                    {
                        // Direct curve geometry
                        Curve transformedCurve = TransformCurveToViewCoordinates(
                            curve, viewTransform, cropMin, cropMax);

                        if (transformedCurve != null && IsValidCurve(transformedCurve))
                        {
                            curves.Add(transformedCurve);
                        }
                    }
                    else if (geomObj is GeometryInstance instance)
                    {
                        // Recurse into geometry instances (family instances, etc.)
                        GeometryElement instanceGeom = instance.GetInstanceGeometry();
                        if (instanceGeom != null)
                        {
                            curves.AddRange(ExtractCurvesFromGeometry(instanceGeom, viewTransform, cropMin, cropMax));
                        }
                    }
                }
                catch
                {
                    // Skip geometry objects that cause errors
                }
            }

            return curves;
        }

        /// <summary>
        /// Transforms a 3D curve to 2D view coordinates suitable for a drafting view.
        /// </summary>
        private static Curve TransformCurveToViewCoordinates(Curve curve, Transform viewTransform, XYZ cropMin, XYZ cropMax)
        {
            try
            {
                // Get curve endpoints
                XYZ startPoint = curve.GetEndPoint(0);
                XYZ endPoint = curve.GetEndPoint(1);

                // Transform points to view coordinate system
                Transform inverse = viewTransform.Inverse;
                XYZ startInView = inverse.OfPoint(startPoint);
                XYZ endInView = inverse.OfPoint(endPoint);

                // Project to Z=0 plane (the drafting view plane)
                XYZ start2D = new XYZ(startInView.X, startInView.Y, 0);
                XYZ end2D = new XYZ(endInView.X, endInView.Y, 0);

                // Check if within crop bounds (with some tolerance)
                double tolerance = 0.1;
                bool startInBounds = start2D.X >= cropMin.X - tolerance && start2D.X <= cropMax.X + tolerance &&
                                     start2D.Y >= cropMin.Y - tolerance && start2D.Y <= cropMax.Y + tolerance;
                bool endInBounds = end2D.X >= cropMin.X - tolerance && end2D.X <= cropMax.X + tolerance &&
                                   end2D.Y >= cropMin.Y - tolerance && end2D.Y <= cropMax.Y + tolerance;

                // Skip curves entirely outside bounds
                if (!startInBounds && !endInBounds)
                {
                    return null;
                }

                // Check for valid line length
                double length = start2D.DistanceTo(end2D);
                if (length < 0.001) // Too short
                {
                    return null;
                }

                // Create the 2D line
                if (curve is Line)
                {
                    return Line.CreateBound(start2D, end2D);
                }
                else if (curve is Arc arc)
                {
                    // For arcs, also transform the center point
                    XYZ centerInView = inverse.OfPoint(arc.Center);
                    XYZ center2D = new XYZ(centerInView.X, centerInView.Y, 0);
                    double radius = arc.Radius;

                    // Create arc in 2D
                    try
                    {
                        return Arc.Create(start2D, end2D, center2D);
                    }
                    catch
                    {
                        // Fall back to line if arc creation fails
                        return Line.CreateBound(start2D, end2D);
                    }
                }
                else
                {
                    // For other curve types, approximate with a line
                    return Line.CreateBound(start2D, end2D);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Validates that a curve is suitable for creating a detail curve.
        /// </summary>
        private static bool IsValidCurve(Curve curve)
        {
            try
            {
                if (curve == null) return false;

                // Check curve is bound
                if (!curve.IsBound) return false;

                // Check curve has valid length
                double length = curve.Length;
                if (length < 0.001 || length > 10000) return false;

                // Check endpoints are valid
                XYZ start = curve.GetEndPoint(0);
                XYZ end = curve.GetEndPoint(1);

                if (double.IsNaN(start.X) || double.IsNaN(start.Y) ||
                    double.IsNaN(end.X) || double.IsNaN(end.Y))
                {
                    return false;
                }

                // Check endpoints are not the same
                if (start.IsAlmostEqualTo(end))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Intelligent Tracing Methods

        /// <summary>
        /// Material mapping entry for intelligent tracing.
        /// </summary>
        private class MaterialMapping
        {
            public string FilledRegionType { get; set; }
            public string DetailComponent { get; set; }
            public int LineWeight { get; set; } = 2;
            public int Priority { get; set; } = 100;
            public bool Skip { get; set; } = false;
        }

        /// <summary>
        /// Geometry piece with material information for intelligent placement.
        /// </summary>
        private class MaterialGeometry
        {
            public List<CurveLoop> Loops { get; set; } = new List<CurveLoop>();
            public List<Curve> Curves { get; set; } = new List<Curve>();
            public string MaterialName { get; set; }
            public ElementId MaterialId { get; set; }
        }

        /// <summary>
        /// Intelligently traces a section/detail view to a drafting view using appropriate
        /// detail components and filled regions based on material mapping.
        /// Uses 2.5D approach: traces visible edges first, then adds detail items based on
        /// wall/floor compound structure layers.
        /// </summary>
        [MCPMethod("intelligentTraceDetailToDraftingView", Category = "Detail", Description = "Intelligently traces detail elements into a drafting view")]
        public static string IntelligentTraceDetailToDraftingView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["sourceViewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId is required"
                    });
                }

                int sourceViewIdInt = parameters["sourceViewId"].Value<int>();
                var sourceViewId = new ElementId(sourceViewIdInt);
                var sourceView = doc.GetElement(sourceViewId) as View;

                if (sourceView == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {sourceViewIdInt} not found"
                    });
                }

                // Load material mappings
                var materialMappings = LoadMaterialMappings();

                // Get filled region types in document (handle duplicates by taking first)
                var filledRegionTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .GroupBy(frt => frt.Name)
                    .ToDictionary(g => g.Key, g => g.First().Id);

                // Get detail component families in document (handle duplicates by taking first)
                var detailFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .Cast<FamilySymbol>()
                    .GroupBy(fs => fs.Name)
                    .ToDictionary(g => g.Key, g => g.First());

                // Statistics
                int tracedCurveCount = 0;
                int filledRegionCount = 0;
                int detailComponentCount = 0;
                int copiedDetailCount = 0;
                int copiedAnnotationCount = 0;
                var layersProcessed = new Dictionary<string, int>();
                var errors = new List<string>();

                // Get crop box information
                var cropBox = sourceView.CropBox;
                var cropTransform = cropBox.Transform;
                var cropMin = cropBox.Min;
                var cropMax = cropBox.Max;

                double viewWidth = cropMax.X - cropMin.X;
                double viewHeight = cropMax.Y - cropMin.Y;

                // Get line style for traced lines
                GraphicsStyle lineStyle = new FilteredElementCollector(doc)
                    .OfClass(typeof(GraphicsStyle))
                    .Cast<GraphicsStyle>()
                    .Where(gs => gs.GraphicsStyleCategory?.Parent?.Id.Value == (int)BuiltInCategory.OST_Lines)
                    .FirstOrDefault(ls => ls.Name.Contains("Medium")) ??
                    new FilteredElementCollector(doc)
                    .OfClass(typeof(GraphicsStyle))
                    .Cast<GraphicsStyle>()
                    .Where(gs => gs.GraphicsStyleCategory?.Parent?.Id.Value == (int)BuiltInCategory.OST_Lines)
                    .FirstOrDefault();

                ViewDrafting newDraftingView = null;

                // Create the drafting view
                using (var trans = new Transaction(doc, "Create Drafting View for Smart Trace"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var draftingViewFamilyType = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting);

                    if (draftingViewFamilyType == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No drafting view family type found"
                        });
                    }

                    newDraftingView = ViewDrafting.Create(doc, draftingViewFamilyType.Id);
                    newDraftingView.Name = "Smart Trace - " + sourceView.Name;
                    newDraftingView.Scale = sourceView.Scale;

                    trans.Commit();
                }

                // STEP 1: Trace visible geometry as lines (this works well)
                using (var trans = new Transaction(doc, "Trace Visible Geometry"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var visibleCategories = new[]
                    {
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_Floors,
                        BuiltInCategory.OST_Roofs,
                        BuiltInCategory.OST_Ceilings,
                        BuiltInCategory.OST_StructuralColumns,
                        BuiltInCategory.OST_StructuralFraming,
                        BuiltInCategory.OST_StructuralFoundation,
                        BuiltInCategory.OST_Doors,
                        BuiltInCategory.OST_Windows,
                        BuiltInCategory.OST_GenericModel
                    };

                    var geomOptions = new Options()
                    {
                        View = sourceView,
                        IncludeNonVisibleObjects = false,
                        ComputeReferences = false
                    };

                    foreach (var category in visibleCategories)
                    {
                        try
                        {
                            var elements = new FilteredElementCollector(doc, sourceViewId)
                                .OfCategory(category)
                                .WhereElementIsNotElementType()
                                .ToList();

                            foreach (var element in elements)
                            {
                                try
                                {
                                    GeometryElement geom = element.get_Geometry(geomOptions);
                                    if (geom == null) continue;

                                    var curves = ExtractCurvesFromGeometry(geom, cropTransform, cropMin, cropMax);

                                    foreach (var curve in curves)
                                    {
                                        try
                                        {
                                            var detailCurve = doc.Create.NewDetailCurve(newDraftingView, curve);
                                            if (lineStyle != null)
                                            {
                                                detailCurve.LineStyle = lineStyle;
                                            }
                                            tracedCurveCount++;
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    trans.Commit();
                }

                // STEP 2: Analyze compound structures and add detail components/filled regions
                using (var trans = new Transaction(doc, "Add Detail Components for Layers"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get walls in the view
                    var walls = new FilteredElementCollector(doc, sourceViewId)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType()
                        .Cast<Wall>()
                        .ToList();

                    foreach (var wall in walls)
                    {
                        try
                        {
                            var wallType = wall.WallType;
                            var cs = wallType.GetCompoundStructure();
                            if (cs == null) continue;

                            // Get wall location and orientation
                            var locCurve = wall.Location as LocationCurve;
                            if (locCurve == null) continue;

                            var wallCurve = locCurve.Curve;
                            var wallStart = wallCurve.GetEndPoint(0);
                            var wallEnd = wallCurve.GetEndPoint(1);
                            var wallDir = (wallEnd - wallStart).Normalize();

                            // Wall normal (perpendicular to wall direction, in XY plane)
                            var wallNormal = new XYZ(-wallDir.Y, wallDir.X, 0).Normalize();

                            // Get wall base and top
                            var wallBBox = wall.get_BoundingBox(sourceView);
                            if (wallBBox == null) continue;

                            // Transform wall points to view coordinates
                            Transform inverse = cropTransform.Inverse;

                            // Process each layer in the compound structure
                            double layerOffset = 0;
                            int layerIndex = 0;

                            foreach (var layer in cs.GetLayers())
                            {
                                try
                                {
                                    double layerWidth = layer.Width;
                                    var materialId = layer.MaterialId;
                                    string materialName = "Default";

                                    if (materialId != null && materialId != ElementId.InvalidElementId)
                                    {
                                        var material = doc.GetElement(materialId) as Material;
                                        if (material != null)
                                            materialName = material.Name;
                                    }

                                    // Track layer
                                    if (!layersProcessed.ContainsKey(materialName))
                                        layersProcessed[materialName] = 0;
                                    layersProcessed[materialName]++;

                                    // Get mapping for this material
                                    MaterialMapping mapping = null;
                                    if (materialMappings.ContainsKey(materialName))
                                    {
                                        mapping = materialMappings[materialName];
                                    }
                                    else
                                    {
                                        // Try partial match
                                        foreach (var key in materialMappings.Keys)
                                        {
                                            if (materialName.ToLower().Contains(key.ToLower()) ||
                                                key.ToLower().Contains(materialName.ToLower()))
                                            {
                                                mapping = materialMappings[key];
                                                break;
                                            }
                                        }
                                    }

                                    if (mapping == null)
                                        mapping = materialMappings.ContainsKey("Default") ? materialMappings["Default"] : new MaterialMapping();

                                    if (mapping.Skip)
                                    {
                                        layerOffset += layerWidth;
                                        layerIndex++;
                                        continue;
                                    }

                                    // Try to create filled region if mapping specifies one
                                    if (!string.IsNullOrEmpty(mapping.FilledRegionType) &&
                                        filledRegionTypes.ContainsKey(mapping.FilledRegionType) &&
                                        layerWidth > 0.001) // Only for layers with meaningful width (> 1/64")
                                    {
                                        try
                                        {
                                            // Get wall geometry in view coordinates
                                            // For a section view, we need to find where the wall intersects the section plane
                                            var minInView = inverse.OfPoint(wallBBox.Min);
                                            var maxInView = inverse.OfPoint(wallBBox.Max);

                                            // Get wall height range (Y in view)
                                            double yMin = Math.Min(minInView.Y, maxInView.Y);
                                            double yMax = Math.Max(minInView.Y, maxInView.Y);

                                            // Get wall width range (X in view) - this represents the wall thickness
                                            double xMin = Math.Min(minInView.X, maxInView.X);
                                            double xMax = Math.Max(minInView.X, maxInView.X);
                                            double wallWidth = xMax - xMin;

                                            // Calculate layer position within wall width
                                            // Layer offset is from exterior face
                                            double layerStartX = xMin + layerOffset;
                                            double layerEndX = layerStartX + layerWidth;

                                            // Ensure layer is within wall bounds
                                            if (layerEndX > xMax) layerEndX = xMax;
                                            if (layerStartX < xMin) layerStartX = xMin;

                                            // Check if layer is visible in crop region
                                            bool inViewBounds = (layerEndX > cropMin.X - 1 && layerStartX < cropMax.X + 1 &&
                                                                 yMax > cropMin.Y - 1 && yMin < cropMax.Y + 1);

                                            if (inViewBounds && (layerEndX - layerStartX) > 0.001 && (yMax - yMin) > 0.1)
                                            {
                                                // Clamp to view crop bounds with small margin
                                                double x1 = Math.Max(layerStartX, cropMin.X - 0.5);
                                                double x2 = Math.Min(layerEndX, cropMax.X + 0.5);
                                                double y1 = Math.Max(yMin, cropMin.Y - 0.5);
                                                double y2 = Math.Min(yMax, cropMax.Y + 0.5);

                                                // Verify we still have a valid region
                                                if ((x2 - x1) > 0.001 && (y2 - y1) > 0.1)
                                                {
                                                    var p1 = new XYZ(x1, y1, 0);
                                                    var p2 = new XYZ(x2, y1, 0);
                                                    var p3 = new XYZ(x2, y2, 0);
                                                    var p4 = new XYZ(x1, y2, 0);

                                                    var loop = new CurveLoop();
                                                    loop.Append(Line.CreateBound(p1, p2));
                                                    loop.Append(Line.CreateBound(p2, p3));
                                                    loop.Append(Line.CreateBound(p3, p4));
                                                    loop.Append(Line.CreateBound(p4, p1));

                                                    var regionTypeId = filledRegionTypes[mapping.FilledRegionType];
                                                    FilledRegion.Create(doc, regionTypeId, newDraftingView.Id, new List<CurveLoop> { loop });
                                                    filledRegionCount++;
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            errors.Add($"Filled region error for {materialName} layer {layerIndex}: {ex.Message}");
                                        }
                                    }

                                    layerOffset += layerWidth;
                                    layerIndex++;
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Error processing wall {wall.Id}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                // STEP 3: Copy existing detail elements and annotations from source
                using (var trans = new Transaction(doc, "Copy Detail Elements and Annotations"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        var detailCategories = new List<BuiltInCategory>
                        {
                            BuiltInCategory.OST_DetailComponents,
                            BuiltInCategory.OST_Lines,
                            BuiltInCategory.OST_FilledRegion,
                            BuiltInCategory.OST_InsulationLines
                        };

                        var detailElementIds = new List<ElementId>();
                        foreach (var cat in detailCategories)
                        {
                            try
                            {
                                var elements = new FilteredElementCollector(doc, sourceView.Id)
                                    .OfCategory(cat)
                                    .ToElementIds();
                                detailElementIds.AddRange(elements);
                            }
                            catch { }
                        }

                        if (detailElementIds.Count > 0)
                        {
                            var copiedIds = ElementTransformUtils.CopyElements(
                                sourceView, detailElementIds, newDraftingView,
                                Transform.Identity, new CopyPasteOptions());
                            copiedDetailCount = copiedIds.Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error copying detail elements: {ex.Message}");
                    }

                    try
                    {
                        var annotationCategories = new List<BuiltInCategory>
                        {
                            BuiltInCategory.OST_TextNotes,
                            BuiltInCategory.OST_Dimensions,
                            BuiltInCategory.OST_GenericAnnotation
                        };

                        var annotationIds = new List<ElementId>();
                        foreach (var cat in annotationCategories)
                        {
                            try
                            {
                                var elements = new FilteredElementCollector(doc, sourceView.Id)
                                    .OfCategory(cat)
                                    .ToElementIds();
                                annotationIds.AddRange(elements);
                            }
                            catch { }
                        }

                        if (annotationIds.Count > 0)
                        {
                            var copiedIds = ElementTransformUtils.CopyElements(
                                sourceView, annotationIds, newDraftingView,
                                Transform.Identity, new CopyPasteOptions());
                            copiedAnnotationCount = copiedIds.Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error copying annotations: {ex.Message}");
                    }

                    trans.Commit();
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceViewId = sourceViewIdInt,
                    sourceViewName = sourceView.Name,
                    sourceViewType = sourceView.ViewType.ToString(),
                    newDraftingViewId = (int)newDraftingView.Id.Value,
                    newDraftingViewName = newDraftingView.Name,
                    scale = newDraftingView.Scale,
                    viewBounds = new
                    {
                        width = Math.Round(viewWidth, 4),
                        height = Math.Round(viewHeight, 4)
                    },
                    statistics = new
                    {
                        tracedCurves = tracedCurveCount,
                        filledRegions = filledRegionCount,
                        detailComponents = detailComponentCount,
                        copiedDetailElements = copiedDetailCount,
                        copiedAnnotations = copiedAnnotationCount,
                        layersAnalyzed = layersProcessed.Count
                    },
                    layersProcessed = layersProcessed,
                    errors = errors.Count > 0 ? errors : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Loads material mappings from configuration or returns defaults.
        /// </summary>
        private static Dictionary<string, MaterialMapping> LoadMaterialMappings()
        {
            var mappings = new Dictionary<string, MaterialMapping>(StringComparer.OrdinalIgnoreCase);

            // ============================================================
            // STUCCO / PLASTER - Exterior finish materials
            // ============================================================
            mappings["Stucco"] = new MaterialMapping { FilledRegionType = "STUCCO", LineWeight = 1 };
            mappings["Stucco Finish"] = new MaterialMapping { FilledRegionType = "STUCCO", LineWeight = 1 };
            mappings["Stucco Finish - 001"] = new MaterialMapping { FilledRegionType = "STUCCO", LineWeight = 1 };
            mappings["Stucco Finish - 002"] = new MaterialMapping { FilledRegionType = "STUCCO", LineWeight = 1 };
            mappings["Finish - Exterior - Stucco"] = new MaterialMapping { FilledRegionType = "STUCCO", LineWeight = 1 };
            mappings["Plaster"] = new MaterialMapping { FilledRegionType = "STUCCO", LineWeight = 1 };

            // ============================================================
            // CONCRETE - Cast-in-place and precast
            // ============================================================
            mappings["Concrete"] = new MaterialMapping { FilledRegionType = "CONCRETE", LineWeight = 2 };
            mappings["Concrete - Cast-in-Place"] = new MaterialMapping { FilledRegionType = "CONCRETE", LineWeight = 2 };
            mappings["Concrete, Cast-in-Place"] = new MaterialMapping { FilledRegionType = "CONCRETE", LineWeight = 2 };
            mappings["Concrete, Cast-in-Place gray"] = new MaterialMapping { FilledRegionType = "CONCRETE", LineWeight = 2 };
            mappings["Concrete - Precast"] = new MaterialMapping { FilledRegionType = "CONCRETE 2", LineWeight = 2 };

            // ============================================================
            // CMU / MASONRY - Block walls
            // ============================================================
            mappings["CMU"] = new MaterialMapping { FilledRegionType = "GROUT", LineWeight = 2 };
            mappings["Concrete Masonry Units"] = new MaterialMapping { FilledRegionType = "GROUT", LineWeight = 2 };
            mappings["Masonry - Concrete Masonry Units"] = new MaterialMapping { FilledRegionType = "GROUT", LineWeight = 2 };

            // ============================================================
            // GYPSUM / DRYWALL - Interior finish
            // ============================================================
            mappings["Gypsum Wall Board"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 1 };
            mappings["Gypsum Board"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 1 };
            mappings["GWB"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 1 };
            mappings["Drywall"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 1 };

            // ============================================================
            // WOOD - Framing and finish
            // ============================================================
            mappings["Wood"] = new MaterialMapping { FilledRegionType = "WOOD", LineWeight = 2 };
            mappings["Wood - Framing"] = new MaterialMapping { FilledRegionType = "WOOD", LineWeight = 2 };
            mappings["Wood - Stud Layer"] = new MaterialMapping { FilledRegionType = "WOOD", LineWeight = 2 };
            mappings["Softwood, Lumber"] = new MaterialMapping { FilledRegionType = "WOOD", LineWeight = 2 };
            mappings["Softwood - Lumber"] = new MaterialMapping { FilledRegionType = "WOOD", LineWeight = 2 };
            mappings["Plywood"] = new MaterialMapping { FilledRegionType = "Wood 1", LineWeight = 1 };
            mappings["Plywood, Sheathing"] = new MaterialMapping { FilledRegionType = "Wood 1", LineWeight = 1 };

            // ============================================================
            // METAL - Steel and framing
            // ============================================================
            mappings["Metal"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 3 };
            mappings["Metal - Steel"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 3 };
            mappings["Steel"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 3 };
            mappings["Metal - Stud Layer"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 2 };
            mappings["Metal Stud"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 2 };
            mappings["Metal Furring"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 2 };
            mappings["Metal Deck"] = new MaterialMapping { FilledRegionType = "STEEL", LineWeight = 2 };
            mappings["Aluminum"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 2 };

            // ============================================================
            // INSULATION - Batt and rigid
            // ============================================================
            mappings["Insulation"] = new MaterialMapping { FilledRegionType = "CMU INSULATION", LineWeight = 1 };
            mappings["Rigid insulation"] = new MaterialMapping { FilledRegionType = "Diagonal Crosshatch", LineWeight = 1 };
            mappings["Rigid Insulation"] = new MaterialMapping { FilledRegionType = "Diagonal Crosshatch", LineWeight = 1 };
            mappings["Insulation / Thermal Barriers - Batt"] = new MaterialMapping { FilledRegionType = "CMU INSULATION", LineWeight = 1 };
            mappings["Insulation / Thermal Barriers - Rigid"] = new MaterialMapping { FilledRegionType = "Diagonal Crosshatch", LineWeight = 1 };
            mappings["Insulation - Batt"] = new MaterialMapping { FilledRegionType = "CMU INSULATION", LineWeight = 1 };
            mappings["Batt Insulation"] = new MaterialMapping { FilledRegionType = "CMU INSULATION", LineWeight = 1 };

            // ============================================================
            // ROOFING - Membrane and built-up
            // ============================================================
            mappings["Roofing"] = new MaterialMapping { FilledRegionType = "Solid Black", LineWeight = 2 };
            mappings["Roofing - TPO"] = new MaterialMapping { FilledRegionType = "Solid Black", LineWeight = 2 };
            mappings["Roofing - EPDM"] = new MaterialMapping { FilledRegionType = "Solid Black", LineWeight = 2 };
            mappings["Roofing, EPDM Membrane"] = new MaterialMapping { FilledRegionType = "Solid Black", LineWeight = 2 };
            mappings["Roofing - Built Up"] = new MaterialMapping { FilledRegionType = "GRAY SHADE", LineWeight = 2 };

            // ============================================================
            // EARTH / SOIL - Site materials
            // ============================================================
            mappings["Earth"] = new MaterialMapping { FilledRegionType = "EARTH", LineWeight = 2 };
            mappings["Soil"] = new MaterialMapping { FilledRegionType = "SOIL", LineWeight = 2 };
            mappings["Stone"] = new MaterialMapping { FilledRegionType = "STONE", LineWeight = 2 };

            // ============================================================
            // GLASS
            // ============================================================
            mappings["Glass"] = new MaterialMapping { FilledRegionType = "LT GRAY TRANSPARENT 2", LineWeight = 1 };

            // ============================================================
            // SKIP - Air gaps and voids
            // ============================================================
            mappings["Air"] = new MaterialMapping { Skip = true };
            mappings["Air Space"] = new MaterialMapping { Skip = true };
            mappings["Membrane Layer"] = new MaterialMapping { Skip = true };

            // ============================================================
            // DEFAULT - Fallback for unmapped materials
            // ============================================================
            mappings["Default"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 2 };
            mappings["Default Wall"] = new MaterialMapping { FilledRegionType = "SOLID FILL LT GRAY", LineWeight = 2 };
            mappings["Default Floor"] = new MaterialMapping { FilledRegionType = "CONCRETE", LineWeight = 2 };
            mappings["Default Roof"] = new MaterialMapping { FilledRegionType = "CONCRETE", LineWeight = 2 };

            return mappings;
        }

        /// <summary>
        /// Extracts geometry pieces with their associated materials.
        /// </summary>
        private static List<MaterialGeometry> ExtractMaterialGeometry(
            Document doc, Element element, GeometryElement geom,
            Transform viewTransform, XYZ cropMin, XYZ cropMax)
        {
            var result = new List<MaterialGeometry>();
            var materialCurves = new Dictionary<ElementId, MaterialGeometry>();

            foreach (GeometryObject geomObj in geom)
            {
                try
                {
                    if (geomObj is Solid solid && solid.Volume > 0)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            try
                            {
                                // Get material from face
                                ElementId materialId = face.MaterialElementId;
                                string materialName = "Default";

                                if (materialId != null && materialId != ElementId.InvalidElementId)
                                {
                                    var material = doc.GetElement(materialId) as Material;
                                    if (material != null)
                                        materialName = material.Name;
                                }

                                // Initialize material geometry entry
                                if (!materialCurves.ContainsKey(materialId ?? ElementId.InvalidElementId))
                                {
                                    materialCurves[materialId ?? ElementId.InvalidElementId] = new MaterialGeometry
                                    {
                                        MaterialId = materialId ?? ElementId.InvalidElementId,
                                        MaterialName = materialName
                                    };
                                }

                                var matGeom = materialCurves[materialId ?? ElementId.InvalidElementId];

                                // Try to get face boundary as curve loop
                                try
                                {
                                    var edgeLoops = face.GetEdgesAsCurveLoops();
                                    foreach (var loop in edgeLoops)
                                    {
                                        // Transform loop to view coordinates
                                        var transformedLoop = TransformCurveLoopToView(loop, viewTransform, cropMin, cropMax);
                                        if (transformedLoop != null)
                                        {
                                            matGeom.Loops.Add(transformedLoop);
                                        }
                                    }
                                }
                                catch
                                {
                                    // Fall back to extracting edges as curves
                                    foreach (EdgeArray edgeArray in face.EdgeLoops)
                                    {
                                        foreach (Edge edge in edgeArray)
                                        {
                                            try
                                            {
                                                Curve edgeCurve = edge.AsCurve();
                                                if (edgeCurve != null)
                                                {
                                                    Curve transformed = TransformCurveToViewCoordinates(
                                                        edgeCurve, viewTransform, cropMin, cropMax);
                                                    if (transformed != null && IsValidCurve(transformed))
                                                    {
                                                        matGeom.Curves.Add(transformed);
                                                    }
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    else if (geomObj is GeometryInstance geomInst)
                    {
                        var instGeom = geomInst.GetInstanceGeometry();
                        if (instGeom != null)
                        {
                            var nested = ExtractMaterialGeometry(doc, element, instGeom, viewTransform, cropMin, cropMax);
                            result.AddRange(nested);
                        }
                    }
                }
                catch { }
            }

            result.AddRange(materialCurves.Values);
            return result;
        }

        /// <summary>
        /// Transforms a curve loop to 2D view coordinates.
        /// </summary>
        private static CurveLoop TransformCurveLoopToView(CurveLoop loop, Transform viewTransform, XYZ cropMin, XYZ cropMax)
        {
            try
            {
                var newCurves = new List<Curve>();
                Transform inverse = viewTransform.Inverse;

                foreach (Curve curve in loop)
                {
                    try
                    {
                        XYZ startPoint = curve.GetEndPoint(0);
                        XYZ endPoint = curve.GetEndPoint(1);

                        // Transform to view coordinates
                        XYZ startInView = inverse.OfPoint(startPoint);
                        XYZ endInView = inverse.OfPoint(endPoint);

                        // Project to 2D (Z=0)
                        XYZ start2D = new XYZ(startInView.X, startInView.Y, 0);
                        XYZ end2D = new XYZ(endInView.X, endInView.Y, 0);

                        // Check bounds
                        double tolerance = 0.5;
                        bool inBounds = (start2D.X >= cropMin.X - tolerance && start2D.X <= cropMax.X + tolerance) ||
                                        (end2D.X >= cropMin.X - tolerance && end2D.X <= cropMax.X + tolerance);

                        if (!inBounds) continue;

                        // Check for valid length
                        if (start2D.DistanceTo(end2D) < 0.001) continue;

                        // Create 2D curve
                        Curve newCurve = Line.CreateBound(start2D, end2D);
                        newCurves.Add(newCurve);
                    }
                    catch { }
                }

                if (newCurves.Count >= 3) // Need at least 3 curves for a valid loop
                {
                    // Try to create a closed loop
                    return CurveLoop.Create(newCurves);
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Detail Component Family Management

        /// <summary>
        /// Gets all loaded detail component families and their types
        /// This is essential for placing detail items in drafting views
        /// </summary>
        [MCPMethod("getDetailComponentFamilies", Category = "Detail", Description = "Gets all detail component families in the document")]
        public static string GetDetailComponentFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var searchTerm = parameters?["search"]?.ToString()?.ToLower();
                var categoryFilter = parameters?["category"]?.ToString();

                // Get all detail component families
                var detailFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => f.FamilyCategory != null &&
                           (f.FamilyCategory.Id.Value == (int)BuiltInCategory.OST_DetailComponents ||
                            f.FamilyCategory.Id.Value == (int)BuiltInCategory.OST_DetailComponentsHiddenLines ||
                            f.FamilyCategory.Id.Value == (int)BuiltInCategory.OST_DetailComponentTags))
                    .ToList();

                // Apply search filter if provided
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    detailFamilies = detailFamilies
                        .Where(f => f.Name.ToLower().Contains(searchTerm))
                        .ToList();
                }

                var familyData = new List<object>();

                foreach (var family in detailFamilies)
                {
                    // Get all types for this family
                    var types = new List<object>();
                    foreach (ElementId typeId in family.GetFamilySymbolIds())
                    {
                        var symbol = doc.GetElement(typeId) as FamilySymbol;
                        if (symbol != null)
                        {
                            types.Add(new
                            {
                                typeId = (int)typeId.Value,
                                typeName = symbol.Name,
                                isActive = symbol.IsActive
                            });
                        }
                    }

                    familyData.Add(new
                    {
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        category = family.FamilyCategory?.Name ?? "Unknown",
                        categoryId = family.FamilyCategory?.Id.Value ?? 0,
                        isEditable = family.IsEditable,
                        typeCount = types.Count,
                        types = types
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = familyData.Count,
                    families = familyData,
                    searchTerm = searchTerm,
                    message = $"Found {familyData.Count} detail component families"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Loads a detail component family from a local .rfa file
        /// </summary>
        [MCPMethod("loadDetailComponentFamily", Category = "Detail", Description = "Loads a detail component family from disk")]
        public static string LoadDetailComponentFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var familyPath = parameters?["path"]?.ToString();

                if (string.IsNullOrEmpty(familyPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family path is required"
                    });
                }

                // Check if file exists
                if (!System.IO.File.Exists(familyPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family file not found: {familyPath}"
                    });
                }

                Family loadedFamily = null;

                using (var trans = new Transaction(doc, "Load Detail Component Family"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    bool loaded = doc.LoadFamily(familyPath, out loadedFamily);

                    if (!loaded && loadedFamily == null)
                    {
                        // Family might already be loaded, try to find it
                        string familyName = System.IO.Path.GetFileNameWithoutExtension(familyPath);
                        loadedFamily = new FilteredElementCollector(doc)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                        if (loadedFamily == null)
                        {
                            trans.RollBack();
                            return Newtonsoft.Json.JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Failed to load family"
                            });
                        }
                    }

                    trans.Commit();
                }

                // Get types from loaded family
                var types = new List<object>();
                foreach (ElementId typeId in loadedFamily.GetFamilySymbolIds())
                {
                    var symbol = doc.GetElement(typeId) as FamilySymbol;
                    if (symbol != null)
                    {
                        types.Add(new
                        {
                            typeId = (int)typeId.Value,
                            typeName = symbol.Name,
                            isActive = symbol.IsActive
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyId = (int)loadedFamily.Id.Value,
                    familyName = loadedFamily.Name,
                    category = loadedFamily.FamilyCategory?.Name ?? "Unknown",
                    types = types,
                    message = $"Family '{loadedFamily.Name}' loaded successfully with {types.Count} types"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Searches local folders for detail component families (.rfa files)
        /// </summary>
        [MCPMethod("searchLocalDetailFamilies", Category = "Detail", Description = "Searches local disk for detail component families")]
        public static string SearchLocalDetailFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var searchPath = parameters?["path"]?.ToString();
                var searchTerm = parameters?["search"]?.ToString()?.ToLower() ?? "";
                var recursive = parameters?["recursive"]?.ToObject<bool>() ?? true;

                // Default paths to search
                var searchPaths = new List<string>();

                if (!string.IsNullOrEmpty(searchPath))
                {
                    searchPaths.Add(searchPath);
                }
                else
                {
                    // Common Revit library locations
                    searchPaths.Add(@"C:\ProgramData\Autodesk\RVT 2026\Libraries");
                    searchPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\Autodesk\Revit\Libraries");

                    // User's custom library paths from Revit
                    var app = uiApp.Application;
                    var libraryPaths = app.GetLibraryPaths();
                    foreach (var kvp in libraryPaths)
                    {
                        if (!string.IsNullOrEmpty(kvp.Value))
                        {
                            searchPaths.Add(kvp.Value);
                        }
                    }
                }

                var foundFamilies = new List<object>();
                var searchOption = recursive ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;

                foreach (var path in searchPaths.Where(p => System.IO.Directory.Exists(p)))
                {
                    try
                    {
                        var files = System.IO.Directory.GetFiles(path, "*.rfa", searchOption);
                        foreach (var file in files)
                        {
                            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);

                            // Filter by search term if provided
                            if (string.IsNullOrEmpty(searchTerm) || fileName.ToLower().Contains(searchTerm))
                            {
                                // Check if it's likely a detail component (by folder structure or name)
                                bool isDetailComponent = file.ToLower().Contains("detail") ||
                                                        file.ToLower().Contains("annotation") ||
                                                        file.ToLower().Contains("2d") ||
                                                        System.IO.Path.GetDirectoryName(file).ToLower().Contains("detail");

                                foundFamilies.Add(new
                                {
                                    fileName = fileName,
                                    fullPath = file,
                                    directory = System.IO.Path.GetDirectoryName(file),
                                    isLikelyDetailComponent = isDetailComponent,
                                    fileSize = new System.IO.FileInfo(file).Length
                                });
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { /* Skip folders we can't access */ }
                    catch (System.IO.DirectoryNotFoundException) { /* Skip missing folders */ }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = foundFamilies.Count,
                    searchPaths = searchPaths,
                    searchTerm = searchTerm,
                    families = foundFamilies.Take(100).ToList(), // Limit to 100 results
                    message = $"Found {foundFamilies.Count} family files"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Activates a family symbol (type) so it can be placed
        /// Required before placing any family instance
        /// </summary>
        [MCPMethod("activateDetailComponentType", Category = "Detail", Description = "Activates a detail component type for placement")]
        public static string ActivateDetailComponentType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var typeId = parameters?["typeId"]?.ToObject<int>();

                if (typeId == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId is required"
                    });
                }

                var symbol = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
                if (symbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family symbol with ID {typeId} not found"
                    });
                }

                if (!symbol.IsActive)
                {
                    using (var trans = new Transaction(doc, "Activate Family Symbol"))
                    {
                        trans.Start();
                        symbol.Activate();
                        trans.Commit();
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeId = typeId,
                    typeName = symbol.Name,
                    familyName = symbol.Family.Name,
                    isActive = symbol.IsActive,
                    message = $"Type '{symbol.Name}' is now active and ready for placement"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places a detail component in a drafting or detail view
        /// </summary>
        [MCPMethod("placeDetailComponentAdvanced", Category = "Detail", Description = "Places a detail component with advanced options")]
        public static string PlaceDetailComponentAdvanced(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters?["viewId"]?.ToObject<int>();
                var typeId = parameters?["typeId"]?.ToObject<int>();
                var x = parameters?["x"]?.ToObject<double>() ?? 0;
                var y = parameters?["y"]?.ToObject<double>() ?? 0;
                var rotation = parameters?["rotation"]?.ToObject<double>() ?? 0; // in degrees

                if (viewId == null || typeId == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and typeId are required"
                    });
                }

                var view = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId} not found"
                    });
                }

                var symbol = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
                if (symbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family symbol with ID {typeId} not found"
                    });
                }

                FamilyInstance instance = null;

                using (var trans = new Transaction(doc, "Place Detail Component"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate symbol if not active
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                    }

                    // Create point
                    XYZ location = new XYZ(x, y, 0);

                    // Place the detail component
                    instance = doc.Create.NewFamilyInstance(location, symbol, view);

                    // Apply rotation if specified
                    if (rotation != 0 && instance != null)
                    {
                        double radians = rotation * Math.PI / 180.0;
                        Line axis = Line.CreateBound(location, new XYZ(location.X, location.Y, location.Z + 1));
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, radians);
                    }

                    trans.Commit();
                }

                if (instance == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to place detail component"
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    instanceId = (int)instance.Id.Value,
                    typeName = symbol.Name,
                    familyName = symbol.Family.Name,
                    location = new { x = x, y = y },
                    rotation = rotation,
                    message = $"Placed '{symbol.Family.Name}: {symbol.Name}' at ({x:F3}, {y:F3})"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Automated Family Loading from Autodesk Cloud

        /// <summary>
        /// Automates loading a family from the Autodesk cloud library
        /// Opens the dialog and uses UI automation to search and load
        /// </summary>
        [MCPMethod("loadAutodeskFamilyAutomated", Category = "Detail", Description = "Loads an Autodesk family with automated dialog handling")]
        public static string LoadAutodeskFamilyAutomated(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Get search parameters
                string searchTerm = parameters["searchTerm"]?.ToString();
                string category = parameters["category"]?.ToString();
                bool autoLoad = parameters["autoLoad"]?.Value<bool>() ?? true;
                int timeout = parameters["timeout"]?.Value<int>() ?? 15;

                if (string.IsNullOrEmpty(searchTerm))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "searchTerm is required"
                    });
                }

                // Path to the automation script
                string scriptPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(DetailMethods).Assembly.Location),
                    "..", "scripts", "load_autodesk_family.py"
                );

                // Normalize path
                scriptPath = System.IO.Path.GetFullPath(scriptPath);

                // Also check in the project directory
                if (!System.IO.File.Exists(scriptPath))
                {
                    scriptPath = @"D:\RevitMCPBridge2026\scripts\load_autodesk_family.py";
                }

                if (!System.IO.File.Exists(scriptPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Automation script not found at {scriptPath}"
                    });
                }

                // Build command arguments
                string args = $"\"{searchTerm}\"";
                if (!string.IsNullOrEmpty(category))
                {
                    args += $" --category \"{category}\"";
                }
                if (!autoLoad)
                {
                    args += " --no-load";
                }
                args += $" --timeout {timeout}";

                // Launch Python script in background (it will wait for dialog)
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = System.Diagnostics.Process.Start(startInfo);

                // Small delay to let script start
                System.Threading.Thread.Sleep(500);

                // Now open the Load Autodesk Family dialog
                RevitCommandId commandId = RevitCommandId.LookupPostableCommandId(PostableCommand.LoadAutodeskFamily);

                if (commandId == null)
                {
                    process?.Kill();
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "LoadAutodeskFamily command not available in this version of Revit"
                    });
                }

                if (!uiApp.CanPostCommand(commandId))
                {
                    process?.Kill();
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot post LoadAutodeskFamily command at this time. Ensure no modal dialogs are open."
                    });
                }

                // Post the command - dialog will open after this method returns
                uiApp.PostCommand(commandId);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Automation started. Searching for '{searchTerm}'...",
                    searchTerm = searchTerm,
                    category = category,
                    autoLoad = autoLoad,
                    scriptPath = scriptPath,
                    note = "The Load Autodesk Family dialog will open. UI automation is handling the search and load."
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Takes a screenshot of the Revit window and analyzes it for the Load Autodesk Family dialog
        /// Used for debugging and manual automation
        /// </summary>
        [MCPMethod("captureDialogState", Category = "Detail", Description = "Captures the current state of open Revit dialogs")]
        public static string CaptureDialogState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string savePath = parameters["savePath"]?.ToString();

                if (string.IsNullOrEmpty(savePath))
                {
                    savePath = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                        $"revit_dialog_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                    );
                }

                // Use Windows API to capture the active window
                IntPtr hwnd = uiApp.MainWindowHandle;

                // Get window bounds
                RECT rect;
                GetWindowRect(hwnd, out rect);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                using (var bitmap = new System.Drawing.Bitmap(width, height))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
                    }
                    bitmap.Save(savePath, System.Drawing.Imaging.ImageFormat.Png);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    savedTo = savePath,
                    windowBounds = new { left = rect.Left, top = rect.Top, width = width, height = height }
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        #endregion

        #region Detail Library Methods

        /// <summary>
        /// Gets the detail library structure - categories and files
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters (optional: libraryPath)</param>
        /// <returns>JSON with categories and their detail files</returns>
        [MCPMethod("getDetailLibrary", Category = "Detail", Description = "Gets the detail library contents")]
        public static string GetDetailLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string libraryPath = parameters["libraryPath"]?.ToString()
                    ?? @"D:\Revit Detail Libraries\Revit Details\";

                if (!System.IO.Directory.Exists(libraryPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Library path not found: {libraryPath}"
                    });
                }

                var categories = new List<object>();
                var categoryDirs = System.IO.Directory.GetDirectories(libraryPath)
                    .OrderBy(d => System.IO.Path.GetFileName(d));

                foreach (var dir in categoryDirs)
                {
                    var dirName = System.IO.Path.GetFileName(dir);
                    var files = System.IO.Directory.GetFiles(dir, "*.rvt")
                        .Select(f => new
                        {
                            name = System.IO.Path.GetFileNameWithoutExtension(f),
                            fileName = System.IO.Path.GetFileName(f),
                            path = f,
                            size = new System.IO.FileInfo(f).Length,
                            modified = new System.IO.FileInfo(f).LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                        })
                        .OrderBy(f => f.name)
                        .ToList();

                    categories.Add(new
                    {
                        name = dirName,
                        path = dir,
                        fileCount = files.Count,
                        files = files
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    libraryPath = libraryPath,
                    totalCategories = categories.Count,
                    totalFiles = categories.Sum(c => ((dynamic)c).fileCount),
                    categories = categories
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets details in a specific category folder
        /// </summary>
        [MCPMethod("getDetailsInCategory", Category = "Detail", Description = "Gets all details in a specified category")]
        public static string GetDetailsInCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string categoryPath = parameters["categoryPath"]?.ToString();

                if (string.IsNullOrEmpty(categoryPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categoryPath is required"
                    });
                }

                if (!System.IO.Directory.Exists(categoryPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category path not found: {categoryPath}"
                    });
                }

                var files = System.IO.Directory.GetFiles(categoryPath, "*.rvt")
                    .Select(f => new
                    {
                        name = System.IO.Path.GetFileNameWithoutExtension(f),
                        fileName = System.IO.Path.GetFileName(f),
                        path = f,
                        size = new System.IO.FileInfo(f).Length,
                        sizeFormatted = FormatFileSize(new System.IO.FileInfo(f).Length),
                        modified = new System.IO.FileInfo(f).LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    })
                    .OrderBy(f => f.name)
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    categoryPath = categoryPath,
                    categoryName = System.IO.Path.GetFileName(categoryPath),
                    fileCount = files.Count,
                    files = files
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }

        /// <summary>
        /// Imports a detail RVT file into the current document
        /// Copies the first drafting view found in the source document
        /// </summary>
        [MCPMethod("importDetailToDocument", Category = "Detail", Description = "Imports a detail from the library into the document")]
        public static string ImportDetailToDocument(UIApplication uiApp, JObject parameters)
        {
            Document sourceDoc = null;
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                string detailPath = parameters["detailPath"]?.ToString();
                if (string.IsNullOrEmpty(detailPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "detailPath is required"
                    });
                }

                if (!System.IO.File.Exists(detailPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Detail file not found: {detailPath}"
                    });
                }

                // Open the source document with options to avoid dialogs
                var openOptions = new OpenOptions();
                openOptions.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;

                ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(detailPath);
                sourceDoc = uiApp.Application.OpenDocumentFile(modelPath, openOptions);

                if (sourceDoc == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to open source document"
                    });
                }

                // Find drafting views in source
                var draftingViews = new FilteredElementCollector(sourceDoc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                if (!draftingViews.Any())
                {
                    sourceDoc.Close(false);
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No drafting views found in source document"
                    });
                }

                // Get the first (or primary) drafting view
                var sourceView = draftingViews.First();
                string viewName = sourceView.Name;

                // Check if view already exists in target
                var existingView = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .FirstOrDefault(v => v.Name == viewName);

                string targetViewName = viewName;
                if (existingView != null)
                {
                    // Generate unique name
                    int suffix = 1;
                    while (new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .Any(v => v.Name == $"{viewName} ({suffix})"))
                    {
                        suffix++;
                    }
                    targetViewName = $"{viewName} ({suffix})";
                }

                // Get all elements in the source view - be more inclusive
                // Include detail lines, filled regions, text, dimensions, detail components, etc.
                var elementsInView = new FilteredElementCollector(sourceDoc, sourceView.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => !(e is View)) // Exclude the view itself
                    .Where(e => e.Category != null) // Must have a category
                    .Select(e => e.Id)
                    .ToList();

                // Also try to get elements that might be owned by the view
                var ownedElements = new FilteredElementCollector(sourceDoc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.OwnerViewId == sourceView.Id)
                    .Select(e => e.Id)
                    .ToList();

                // Combine view + all its elements for copying together
                var allElementIds = elementsInView.Union(ownedElements).Distinct().ToList();
                int sourceElementCount = allElementIds.Count;

                // Add the view itself to the copy list - copy view AND elements together
                var elementsToCopy = new List<ElementId> { sourceView.Id };
                elementsToCopy.AddRange(allElementIds);

                int actualCopiedCount = 0;
                long newViewId = 0;
                ViewDrafting newView = null;

                using (var trans = new Transaction(doc, "Import Detail"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var options = new CopyPasteOptions();
                    options.SetDuplicateTypeNamesHandler(new DuplicateTypeHandler());

                    ICollection<ElementId> copiedIds = null;

                    try
                    {
                        // Copy view AND all its elements together
                        // This should maintain the view-element relationship
                        copiedIds = ElementTransformUtils.CopyElements(
                            sourceDoc,
                            elementsToCopy,
                            doc,
                            Autodesk.Revit.DB.Transform.Identity,
                            options);
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        sourceDoc.Close(false);
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Failed to copy: {ex.Message}"
                        });
                    }

                    if (copiedIds == null || copiedIds.Count == 0)
                    {
                        trans.RollBack();
                        sourceDoc.Close(false);
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Copy returned no elements"
                        });
                    }

                    // Find the copied view among the returned IDs
                    foreach (var copiedId in copiedIds)
                    {
                        var elem = doc.GetElement(copiedId);
                        if (elem is ViewDrafting dv)
                        {
                            newView = dv;
                            break;
                        }
                    }

                    if (newView == null)
                    {
                        trans.RollBack();
                        sourceDoc.Close(false);
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "View was not found among copied elements"
                        });
                    }

                    newViewId = newView.Id.Value;

                    // Rename the view if needed
                    try
                    {
                        if (!string.IsNullOrEmpty(targetViewName) && newView.Name != targetViewName)
                        {
                            newView.Name = targetViewName;
                        }
                    }
                    catch { /* Rename might fail if name exists */ }

                    // Regenerate
                    doc.Regenerate();

                    // Count elements in the new view
                    int finalElementCount = 0;
                    try
                    {
                        finalElementCount = new FilteredElementCollector(doc, newView.Id)
                            .WhereElementIsNotElementType()
                            .GetElementCount();
                    }
                    catch { }

                    actualCopiedCount = copiedIds.Count - 1; // Subtract 1 for the view itself

                    trans.Commit();
                }

                // Close source document AFTER transaction commits
                sourceDoc.Close(false);
                sourceDoc = null;

                // Final verification - count elements in the view
                int verifiedElementCount = 0;
                string actualViewName = "";
                try
                {
                    var finalView = doc.GetElement(new ElementId(newViewId)) as ViewDrafting;
                    if (finalView != null)
                    {
                        actualViewName = finalView.Name;
                        verifiedElementCount = new FilteredElementCollector(doc, finalView.Id)
                            .WhereElementIsNotElementType()
                            .GetElementCount();
                    }
                }
                catch { }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = newViewId,
                    viewName = actualViewName,
                    sourceFile = System.IO.Path.GetFileName(detailPath),
                    sourceElementCount = allElementIds.Count,
                    elementsCopied = actualCopiedCount,
                    verifiedElementsInView = verifiedElementCount,
                    message = $"Imported '{actualViewName}' with {verifiedElementCount} elements in view"
                });
            }
            catch (Exception ex)
            {
                // Make sure source doc is closed on error
                try { sourceDoc?.Close(false); } catch { }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Searches the detail library for files matching a search term
        /// </summary>
        [MCPMethod("searchDetailLibrary", Category = "Detail", Description = "Searches the detail library by keyword")]
        public static string SearchDetailLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string searchTerm = parameters["searchTerm"]?.ToString();
                string libraryPath = parameters["libraryPath"]?.ToString()
                    ?? @"D:\Revit Detail Libraries\Revit Details\";

                if (string.IsNullOrEmpty(searchTerm))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "searchTerm is required"
                    });
                }

                if (!System.IO.Directory.Exists(libraryPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Library path not found: {libraryPath}"
                    });
                }

                var results = new List<object>();
                var searchLower = searchTerm.ToLower();

                foreach (var dir in System.IO.Directory.GetDirectories(libraryPath))
                {
                    var categoryName = System.IO.Path.GetFileName(dir);
                    var files = System.IO.Directory.GetFiles(dir, "*.rvt")
                        .Where(f => System.IO.Path.GetFileNameWithoutExtension(f).ToLower().Contains(searchLower))
                        .Select(f => new
                        {
                            name = System.IO.Path.GetFileNameWithoutExtension(f),
                            fileName = System.IO.Path.GetFileName(f),
                            path = f,
                            category = categoryName,
                            size = new System.IO.FileInfo(f).Length,
                            sizeFormatted = FormatFileSize(new System.IO.FileInfo(f).Length)
                        });

                    results.AddRange(files);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    searchTerm = searchTerm,
                    resultCount = results.Count,
                    results = results.OrderBy(r => ((dynamic)r).name).ToList()
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Handler for duplicate type names during copy operations
        /// </summary>
        private class DuplicateTypeHandler : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        #endregion
    }
}
