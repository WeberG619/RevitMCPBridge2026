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

// Suppress obsolete API warnings for TopographySurface (backward compatibility)
#pragma warning disable CS0618

namespace RevitMCPBridge
{
    /// <summary>
    /// Site, topography, and property methods for MCP Bridge
    /// </summary>
    public static class SiteMethods
    {
        /// <summary>
        /// Get all topography surfaces in the model
        /// </summary>
        public static string GetTopographySurfaces(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topos = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Topography)
                    .WhereElementIsNotElementType()
                    .Select(t => new
                    {
                        topoId = t.Id.Value,
                        name = t.Name
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    topoCount = topos.Count,
                    topographies = topos
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a topography surface from points
        /// </summary>
        public static string CreateTopography(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["points"]?.ToObject<double[][]>();

                if (points == null || points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 3 points are required" });
                }

                using (var trans = new Transaction(doc, "Create Topography"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var xyzPoints = points.Select(p => new XYZ(p[0], p[1], p.Length > 2 ? p[2] : 0)).ToList();
                    var topo = TopographySurface.Create(doc, xyzPoints);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topoId = topo.Id.Value,
                        pointCount = xyzPoints.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get topography points
        /// </summary>
        public static string GetTopographyPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();

                if (!topoId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId is required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                var points = topo.GetPoints().Select(p => new { x = p.X, y = p.Y, z = p.Z }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    topoId = topoId.Value,
                    pointCount = points.Count,
                    points = points
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add points to topography
        /// </summary>
        public static string AddTopographyPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();
                var points = parameters["points"]?.ToObject<double[][]>();

                if (!topoId.HasValue || points == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId and points are required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                using (var trans = new Transaction(doc, "Add Topography Points"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var xyzPoints = points.Select(p => new XYZ(p[0], p[1], p.Length > 2 ? p[2] : 0)).ToList();

                    using (var editScope = new TopographyEditScope(doc, "Add Points"))
                    {
                        editScope.Start(topo.Id);
                        topo.AddPoints(xyzPoints);
                        editScope.Commit(new TopographyEditFailuresPreprocessor());
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topoId = topoId.Value,
                        addedPointCount = xyzPoints.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a topography surface
        /// </summary>
        public static string DeleteTopography(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();

                if (!topoId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Topography"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(topoId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedTopoId = topoId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all building pads
        /// </summary>
        public static string GetBuildingPads(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var pads = new FilteredElementCollector(doc)
                    .OfClass(typeof(BuildingPad))
                    .Cast<BuildingPad>()
                    .Select(p => new
                    {
                        padId = p.Id.Value,
                        name = p.Name,
                        levelName = (doc.GetElement(p.LevelId) as Level)?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    padCount = pads.Count,
                    buildingPads = pads
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a building pad
        /// </summary>
        public static string CreateBuildingPad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["points"]?.ToObject<double[][]>();
                var levelId = parameters["levelId"]?.Value<int>();
                var typeId = parameters["typeId"]?.Value<int>();

                if (points == null || points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 3 points are required" });
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

                BuildingPadType padType = null;
                if (typeId.HasValue)
                {
                    padType = doc.GetElement(new ElementId(typeId.Value)) as BuildingPadType;
                }
                else
                {
                    padType = new FilteredElementCollector(doc)
                        .OfClass(typeof(BuildingPadType))
                        .Cast<BuildingPadType>()
                        .FirstOrDefault();
                }

                if (padType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No building pad type found" });
                }

                using (var trans = new Transaction(doc, "Create Building Pad"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], 0);
                        var end = new XYZ(points[(i + 1) % points.Length][0], points[(i + 1) % points.Length][1], 0);
                        curveLoop.Append(Line.CreateBound(start, end));
                    }

                    var curves = new List<CurveLoop> { curveLoop };
                    var pad = BuildingPad.Create(doc, padType.Id, level.Id, curves);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        padId = pad.Id.Value,
                        typeName = padType.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all property lines (site boundary elements)
        /// </summary>
        public static string GetPropertyLines(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Property lines are in the Site category
                var lines = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Site)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Name.Contains("Property") || e.Category?.Name == "Property Lines")
                    .Select(l => new
                    {
                        lineId = l.Id.Value,
                        name = l.Name
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    lineCount = lines.Count,
                    propertyLines = lines
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a property line (model lines in Site category)
        /// Note: Revit 2026 doesn't have PropertyLine.Create - using model lines instead
        /// </summary>
        public static string CreatePropertyLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["points"]?.ToObject<double[][]>();

                if (points == null || points.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 2 points are required" });
                }

                using (var trans = new Transaction(doc, "Create Property Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var createdLines = new List<long>();
                    var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

                    for (int i = 0; i < points.Length - 1; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], 0);
                        var end = new XYZ(points[i + 1][0], points[i + 1][1], 0);
                        var line = Line.CreateBound(start, end);
                        var modelLine = doc.Create.NewModelCurve(line, sketchPlane);
                        createdLines.Add(modelLine.Id.Value);
                    }

                    // Close the loop if needed
                    var closeLoop = parameters["closeLoop"]?.Value<bool>() ?? false;
                    if (closeLoop && points.Length > 2)
                    {
                        var start = new XYZ(points[points.Length - 1][0], points[points.Length - 1][1], 0);
                        var end = new XYZ(points[0][0], points[0][1], 0);
                        var line = Line.CreateBound(start, end);
                        var modelLine = doc.Create.NewModelCurve(line, sketchPlane);
                        createdLines.Add(modelLine.Id.Value);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        propertyLineIds = createdLines,
                        segmentCount = createdLines.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get site information
        /// </summary>
        public static string GetSiteInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var projectInfo = doc.ProjectInformation;
                var siteLocation = doc.SiteLocation;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = projectInfo.Name,
                    projectNumber = projectInfo.Number,
                    projectAddress = projectInfo.Address,
                    latitude = siteLocation.Latitude * (180.0 / Math.PI),
                    longitude = siteLocation.Longitude * (180.0 / Math.PI),
                    timeZone = siteLocation.TimeZone
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set site location
        /// </summary>
        public static string SetSiteLocation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var latitude = parameters["latitude"]?.Value<double>();
                var longitude = parameters["longitude"]?.Value<double>();

                if (!latitude.HasValue || !longitude.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "latitude and longitude are required" });
                }

                using (var trans = new Transaction(doc, "Set Site Location"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var siteLocation = doc.SiteLocation;
                    siteLocation.Latitude = latitude.Value * (Math.PI / 180.0);
                    siteLocation.Longitude = longitude.Value * (Math.PI / 180.0);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        latitude = latitude.Value,
                        longitude = longitude.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper class for topography editing
        private class TopographyEditFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                return FailureProcessingResult.Continue;
            }
        }
    }
}
