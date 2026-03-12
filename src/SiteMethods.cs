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
        [MCPMethod("getTopographySurfaces", Category = "Site", Description = "Get all topography surfaces in the model")]
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
        [MCPMethod("createTopography", Category = "Site", Description = "Create a topography surface from a list of XYZ points")]
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
        [MCPMethod("getTopographyPoints", Category = "Site", Description = "Get all points that define a topography surface")]
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
        [MCPMethod("addTopographyPoints", Category = "Site", Description = "Add new points to an existing topography surface")]
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
        [MCPMethod("deleteTopography", Category = "Site", Description = "Delete a topography surface from the model")]
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
        [MCPMethod("getBuildingPads", Category = "Site", Description = "Get all building pads in the model")]
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
        [MCPMethod("createBuildingPad", Category = "Site", Description = "Create a building pad from boundary points on a level")]
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
        [MCPMethod("getPropertyLines", Category = "Site", Description = "Get all property lines and site boundary elements")]
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
        [MCPMethod("createPropertyLine", Category = "Site", Description = "Create a property line from a series of points")]
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
        [MCPMethod("getSiteInfo", Category = "Site", Description = "Get site information including project address and geographic coordinates")]
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
        [MCPMethod("setSiteLocation", Category = "Site", Description = "Set the site geographic location by latitude and longitude")]
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

        /// <summary>
        /// Move existing points on a topography surface
        /// </summary>
        [MCPMethod("modifyTopographyPoints", Category = "Site", Description = "Move existing points on a topography surface to new positions")]
        public static string ModifyTopographyPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();
                var modifications = parameters["modifications"]?.ToObject<JArray>();

                if (!topoId.HasValue || modifications == null || modifications.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId and modifications array are required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                using (var trans = new Transaction(doc, "Modify Topography Points"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    int movedCount = 0;

                    using (var editScope = new TopographyEditScope(doc, "Modify Points"))
                    {
                        editScope.Start(topo.Id);

                        foreach (var mod in modifications)
                        {
                            var origArr = mod["originalPoint"]?.ToObject<double[]>();
                            var newArr = mod["newPoint"]?.ToObject<double[]>();

                            if (origArr == null || origArr.Length < 3 || newArr == null || newArr.Length < 3)
                                continue;

                            var originalPoint = new XYZ(origArr[0], origArr[1], origArr[2]);
                            var newPoint = new XYZ(newArr[0], newArr[1], newArr[2]);

                            topo.MovePoint(originalPoint, newPoint);
                            movedCount++;
                        }

                        editScope.Commit(new TopographyEditFailuresPreprocessor());
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topoId = topoId.Value,
                        movedPointCount = movedCount
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove specific points from a topography surface
        /// </summary>
        [MCPMethod("deleteTopographyPoints", Category = "Site", Description = "Remove specific points from a topography surface")]
        public static string DeleteTopographyPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();
                var points = parameters["points"]?.ToObject<double[][]>();

                if (!topoId.HasValue || points == null || points.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId and points array are required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                using (var trans = new Transaction(doc, "Delete Topography Points"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var xyzPoints = points.Select(p => new XYZ(p[0], p[1], p.Length > 2 ? p[2] : 0)).ToList();

                    using (var editScope = new TopographyEditScope(doc, "Delete Points"))
                    {
                        editScope.Start(topo.Id);
                        topo.DeletePoints(xyzPoints);
                        editScope.Commit(new TopographyEditFailuresPreprocessor());
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topoId = topoId.Value,
                        deletedPointCount = xyzPoints.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detailed information about a topography surface
        /// </summary>
        [MCPMethod("getTopographyInfo", Category = "Site", Description = "Get detailed info about a topography surface including area, perimeter, elevation range, and point count")]
        public static string GetTopographyInfo(UIApplication uiApp, JObject parameters)
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

                var pts = topo.GetPoints();
                double minElev = double.MaxValue;
                double maxElev = double.MinValue;

                foreach (var pt in pts)
                {
                    if (pt.Z < minElev) minElev = pt.Z;
                    if (pt.Z > maxElev) maxElev = pt.Z;
                }

                // Get area and perimeter from parameters
                var areaParam = topo.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                var perimParam = topo.get_Parameter(BuiltInParameter.HOST_PERIMETER_COMPUTED);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    topoId = topoId.Value,
                    name = topo.Name,
                    pointCount = pts.Count(),
                    minElevation = minElev,
                    maxElevation = maxElev,
                    elevationRange = maxElev - minElev,
                    area = areaParam?.AsDouble() ?? 0.0,
                    perimeter = perimParam?.AsDouble() ?? 0.0,
                    isSiteSubRegion = topo.IsSiteSubRegion
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a sub-region on a topography surface
        /// </summary>
        [MCPMethod("createSubRegion", Category = "Site", Description = "Create a sub-region on a topography surface with optional material")]
        public static string CreateSubRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();
                var points = parameters["boundaryPoints"]?.ToObject<double[][]>();
                var materialId = parameters["materialId"]?.Value<int>();

                if (!topoId.HasValue || points == null || points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId and at least 3 boundaryPoints are required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                using (var trans = new Transaction(doc, "Create Sub-Region"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i].Length > 2 ? points[i][2] : 0);
                        var end = new XYZ(points[(i + 1) % points.Length][0], points[(i + 1) % points.Length][1],
                            points[(i + 1) % points.Length].Length > 2 ? points[(i + 1) % points.Length][2] : 0);
                        curveLoop.Append(Line.CreateBound(start, end));
                    }

                    var curveLoops = new List<CurveLoop> { curveLoop };
                    var subRegion = SiteSubRegion.Create(doc, curveLoops, topo.Id);

                    if (materialId.HasValue)
                    {
                        var subTopoSurface = subRegion.TopographySurface;
                        var matParam = subTopoSurface.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (matParam != null && !matParam.IsReadOnly)
                        {
                            matParam.Set(new ElementId(materialId.Value));
                        }
                    }

                    trans.Commit();

                    var subTopoId = subRegion.TopographySurface.Id;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        subRegionId = subTopoId.Value,
                        topoSurfaceId = subTopoId.Value,
                        hostTopoId = topoId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all sub-regions on a topography surface
        /// </summary>
        [MCPMethod("getSubRegions", Category = "Site", Description = "Get all sub-regions on a topography surface")]
        public static string GetSubRegions(UIApplication uiApp, JObject parameters)
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

                var subRegionIds = topo.GetHostedSubRegionIds();
                var subRegions = subRegionIds.Select(id =>
                {
                    var srTopo = doc.GetElement(id) as TopographySurface;
                    if (srTopo == null) return null;
                    return new
                    {
                        subRegionId = id.Value,
                        topoSurfaceId = srTopo.Id.Value,
                        name = srTopo.Name ?? "Unknown",
                        pointCount = srTopo.GetPoints()?.Count() ?? 0
                    };
                })
                .Where(x => x != null)
                .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    topoId = topoId.Value,
                    subRegionCount = subRegions.Count,
                    subRegions = subRegions
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a sub-region
        /// </summary>
        [MCPMethod("deleteSubRegion", Category = "Site", Description = "Delete a sub-region from a topography surface")]
        public static string DeleteSubRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var subRegionId = parameters["subRegionId"]?.Value<int>();

                if (!subRegionId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "subRegionId is required" });
                }

                var subRegionElem = doc.GetElement(new ElementId(subRegionId.Value));
                if (subRegionElem == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sub-region not found" });
                }

                using (var trans = new Transaction(doc, "Delete Sub-Region"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(subRegionId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedSubRegionId = subRegionId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all site components (benches, trees, etc.)
        /// </summary>
        [MCPMethod("getSiteComponents", Category = "Site", Description = "Get all site components including planting, entourage, and site furniture")]
        public static string GetSiteComponents(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var categories = new[]
                {
                    BuiltInCategory.OST_Site,
                    BuiltInCategory.OST_Planting,
                    BuiltInCategory.OST_Entourage
                };

                var components = new List<object>();

                foreach (var cat in categories)
                {
                    var elements = new FilteredElementCollector(doc)
                        .OfCategory(cat)
                        .WhereElementIsNotElementType()
                        .ToList();

                    foreach (var elem in elements)
                    {
                        var fi = elem as FamilyInstance;
                        var loc = elem.Location as LocationPoint;

                        components.Add(new
                        {
                            elementId = elem.Id.Value,
                            name = elem.Name,
                            category = cat.ToString().Replace("OST_", ""),
                            familyName = fi?.Symbol?.Family?.Name ?? "",
                            typeName = fi?.Symbol?.Name ?? elem.Name,
                            location = loc != null ? new { x = loc.Point.X, y = loc.Point.Y, z = loc.Point.Z } : null,
                            rotation = loc?.Rotation ?? 0.0
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    componentCount = components.Count,
                    components = components
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a site component family instance
        /// </summary>
        [MCPMethod("placeSiteComponent", Category = "Site", Description = "Place a site component family instance at a location with optional rotation")]
        public static string PlaceSiteComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var familyTypeId = parameters["familyTypeId"]?.Value<int>();
                var location = parameters["location"]?.ToObject<double[]>();
                var rotation = parameters["rotation"]?.Value<double>() ?? 0.0;

                if (!familyTypeId.HasValue || location == null || location.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "familyTypeId and location [x, y, z] are required" });
                }

                var familySymbol = doc.GetElement(new ElementId(familyTypeId.Value)) as FamilySymbol;
                if (familySymbol == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Family type not found" });
                }

                using (var trans = new Transaction(doc, "Place Site Component"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!familySymbol.IsActive)
                        familySymbol.Activate();

                    var point = new XYZ(location[0], location[1], location.Length > 2 ? location[2] : 0);
                    var instance = doc.Create.NewFamilyInstance(point, familySymbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    // Apply rotation if specified
                    if (Math.Abs(rotation) > 0.001)
                    {
                        var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * (Math.PI / 180.0));
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = instance.Id.Value,
                        familyName = familySymbol.Family.Name,
                        typeName = familySymbol.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a site component
        /// </summary>
        [MCPMethod("deleteSiteComponent", Category = "Site", Description = "Delete a site component element")]
        public static string DeleteSiteComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementId = parameters["elementId"]?.Value<int>();

                if (!elementId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                var element = doc.GetElement(new ElementId(elementId.Value));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                using (var trans = new Transaction(doc, "Delete Site Component"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(elementId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedElementId = elementId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify building pad properties
        /// </summary>
        [MCPMethod("modifyBuildingPad", Category = "Site", Description = "Modify building pad properties such as height offset and type")]
        public static string ModifyBuildingPad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var padId = parameters["padId"]?.Value<int>();
                var heightOffset = parameters["heightOffset"]?.Value<double>();
                var typeId = parameters["typeId"]?.Value<int>();

                if (!padId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "padId is required" });
                }

                var pad = doc.GetElement(new ElementId(padId.Value)) as BuildingPad;
                if (pad == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Building pad not found" });
                }

                using (var trans = new Transaction(doc, "Modify Building Pad"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (heightOffset.HasValue)
                    {
                        var offsetParam = pad.get_Parameter(BuiltInParameter.BUILDINGPAD_HEIGHTABOVELEVEL_PARAM);
                        if (offsetParam != null && !offsetParam.IsReadOnly)
                        {
                            offsetParam.Set(heightOffset.Value);
                        }
                    }

                    if (typeId.HasValue)
                    {
                        var newType = doc.GetElement(new ElementId(typeId.Value)) as BuildingPadType;
                        if (newType != null)
                        {
                            pad.ChangeTypeId(new ElementId(typeId.Value));
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        padId = padId.Value,
                        heightOffset = heightOffset,
                        typeId = typeId
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a building pad
        /// </summary>
        [MCPMethod("deleteBuildingPad", Category = "Site", Description = "Delete a building pad from the model")]
        public static string DeleteBuildingPad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var padId = parameters["padId"]?.Value<int>();

                if (!padId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "padId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Building Pad"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(padId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedPadId = padId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Calculate grading info between two points or for an area
        /// </summary>
        [MCPMethod("getGradingInfo", Category = "Site", Description = "Calculate grading info including slope percentage between two points or elevation stats for a topography")]
        public static string GetGradingInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var point1 = parameters["point1"]?.ToObject<double[]>();
                var point2 = parameters["point2"]?.ToObject<double[]>();
                var topoId = parameters["topoId"]?.Value<int>();

                if (point1 != null && point2 != null && point1.Length >= 3 && point2.Length >= 3)
                {
                    // Calculate slope between two points
                    var rise = point2[2] - point1[2];
                    var run = Math.Sqrt(Math.Pow(point2[0] - point1[0], 2) + Math.Pow(point2[1] - point1[1], 2));
                    var slopePercent = run > 0 ? (rise / run) * 100.0 : 0.0;
                    var slopeRatio = run > 0 ? $"1:{Math.Abs(run / rise):F1}" : "flat";
                    var distance = Math.Sqrt(Math.Pow(point2[0] - point1[0], 2) +
                                            Math.Pow(point2[1] - point1[1], 2) +
                                            Math.Pow(point2[2] - point1[2], 2));

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        rise = rise,
                        run = run,
                        slopePercent = Math.Round(slopePercent, 2),
                        slopeRatio = slopeRatio,
                        distance = Math.Round(distance, 4),
                        direction = rise > 0 ? "uphill" : rise < 0 ? "downhill" : "flat"
                    });
                }
                else if (topoId.HasValue)
                {
                    // Calculate grading stats for a topography
                    var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                    if (topo == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                    }

                    var pts = topo.GetPoints();
                    if (pts.Count() == 0)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Topography has no points" });
                    }

                    double minElev = pts.Min(p => p.Z);
                    double maxElev = pts.Max(p => p.Z);
                    double avgElev = pts.Average(p => p.Z);
                    double minX = pts.Min(p => p.X);
                    double maxX = pts.Max(p => p.X);
                    double minY = pts.Min(p => p.Y);
                    double maxY = pts.Max(p => p.Y);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topoId = topoId.Value,
                        minElevation = Math.Round(minElev, 4),
                        maxElevation = Math.Round(maxElev, 4),
                        averageElevation = Math.Round(avgElev, 4),
                        elevationRange = Math.Round(maxElev - minElev, 4),
                        extentX = Math.Round(maxX - minX, 4),
                        extentY = Math.Round(maxY - minY, 4),
                        pointCount = pts.Count
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Provide either point1+point2 or topoId" });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a parking space
        /// </summary>
        [MCPMethod("createParkingSpace", Category = "Site", Description = "Place a parking space family instance at a location with rotation")]
        public static string CreateParkingSpace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var location = parameters["location"]?.ToObject<double[]>();
                var rotation = parameters["rotation"]?.Value<double>() ?? 0.0;
                var typeId = parameters["typeId"]?.Value<int>();

                if (location == null || location.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "location [x, y, z] is required" });
                }

                FamilySymbol parkingType = null;
                if (typeId.HasValue)
                {
                    parkingType = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
                }
                else
                {
                    // Find first parking family type
                    parkingType = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Parking)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();
                }

                if (parkingType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No parking family type found. Load a parking family first." });
                }

                using (var trans = new Transaction(doc, "Create Parking Space"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!parkingType.IsActive)
                        parkingType.Activate();

                    var point = new XYZ(location[0], location[1], location.Length > 2 ? location[2] : 0);
                    var instance = doc.Create.NewFamilyInstance(point, parkingType, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    if (Math.Abs(rotation) > 0.001)
                    {
                        var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * (Math.PI / 180.0));
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = instance.Id.Value,
                        familyName = parkingType.Family.Name,
                        typeName = parkingType.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all parking spaces in the model
        /// </summary>
        [MCPMethod("getParkingSpaces", Category = "Site", Description = "Get all parking spaces in the model")]
        public static string GetParkingSpaces(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var spaces = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Parking)
                    .WhereElementIsNotElementType()
                    .Select(e =>
                    {
                        var fi = e as FamilyInstance;
                        var loc = e.Location as LocationPoint;
                        return new
                        {
                            elementId = e.Id.Value,
                            familyName = fi?.Symbol?.Family?.Name ?? "",
                            typeName = fi?.Symbol?.Name ?? e.Name,
                            location = loc != null ? new { x = loc.Point.X, y = loc.Point.Y, z = loc.Point.Z } : null,
                            rotation = loc?.Rotation ?? 0.0
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    spaceCount = spaces.Count,
                    parkingSpaces = spaces
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set project address information
        /// </summary>
        [MCPMethod("setSiteAddress", Category = "Site", Description = "Set the project address information (address, city, state, zip)")]
        public static string SetSiteAddress(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var address = parameters["address"]?.Value<string>();
                var city = parameters["city"]?.Value<string>();
                var state = parameters["state"]?.Value<string>();
                var zip = parameters["zip"]?.Value<string>();

                if (address == null && city == null && state == null && zip == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least one of address, city, state, or zip is required" });
                }

                using (var trans = new Transaction(doc, "Set Site Address"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var projectInfo = doc.ProjectInformation;

                    // Build full address string
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(address)) parts.Add(address);
                    if (!string.IsNullOrEmpty(city)) parts.Add(city);
                    if (!string.IsNullOrEmpty(state)) parts.Add(state);
                    if (!string.IsNullOrEmpty(zip)) parts.Add(zip);

                    projectInfo.Address = string.Join(", ", parts);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        address = projectInfo.Address
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get contour line elevation intervals for a topography
        /// </summary>
        [MCPMethod("getContourLines", Category = "Site", Description = "Get contour line elevation intervals and point distribution for a topography")]
        public static string GetContourLines(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();
                var interval = parameters["interval"]?.Value<double>() ?? 1.0;

                if (!topoId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId is required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                var pts = topo.GetPoints();
                if (pts.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography has no points" });
                }

                double minElev = pts.Min(p => p.Z);
                double maxElev = pts.Max(p => p.Z);

                // Generate contour elevations at the specified interval
                var contours = new List<object>();
                double startElev = Math.Floor(minElev / interval) * interval;

                for (double elev = startElev; elev <= maxElev; elev += interval)
                {
                    // Count points near this elevation (within half interval)
                    var nearbyPoints = pts.Count(p => Math.Abs(p.Z - elev) < interval / 2.0);
                    contours.Add(new
                    {
                        elevation = Math.Round(elev, 4),
                        nearbyPointCount = nearbyPoints,
                        isMajor = Math.Abs(elev % (interval * 5)) < 0.001
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    topoId = topoId.Value,
                    interval = interval,
                    minElevation = Math.Round(minElev, 4),
                    maxElevation = Math.Round(maxElev, 4),
                    contourCount = contours.Count,
                    contours = contours
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a retaining wall on site
        /// </summary>
        [MCPMethod("createRetainingWall", Category = "Site", Description = "Create a retaining wall along a series of points with specified height and type")]
        public static string CreateRetainingWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["points"]?.ToObject<double[][]>();
                var height = parameters["height"]?.Value<double>() ?? 4.0;
                var typeId = parameters["typeId"]?.Value<int>();
                var levelId = parameters["levelId"]?.Value<int>();

                if (points == null || points.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 2 points are required" });
                }

                // Get or find level
                Level level = null;
                if (levelId.HasValue)
                {
                    level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                }
                else
                {
                    level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .FirstOrDefault();
                }

                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No level found" });
                }

                // Get wall type
                WallType wallType = null;
                if (typeId.HasValue)
                {
                    wallType = doc.GetElement(new ElementId(typeId.Value)) as WallType;
                }
                else
                {
                    // Try to find a retaining wall type, fall back to first available
                    wallType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Name.IndexOf("retaining", StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? new FilteredElementCollector(doc)
                            .OfClass(typeof(WallType))
                            .Cast<WallType>()
                            .FirstOrDefault();
                }

                if (wallType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No wall type found" });
                }

                using (var trans = new Transaction(doc, "Create Retaining Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var createdWalls = new List<long>();

                    for (int i = 0; i < points.Length - 1; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i].Length > 2 ? points[i][2] : 0);
                        var end = new XYZ(points[i + 1][0], points[i + 1][1], points[i + 1].Length > 2 ? points[i + 1][2] : 0);
                        var line = Line.CreateBound(start, end);

                        var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);
                        createdWalls.Add(wall.Id.Value);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        wallIds = createdWalls,
                        wallCount = createdWalls.Count,
                        wallTypeName = wallType.Name,
                        height = height
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the property boundary as coordinate list
        /// </summary>
        [MCPMethod("getSiteBoundary", Category = "Site", Description = "Get the property boundary as a list of coordinates from property lines")]
        public static string GetSiteBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Look for property line elements under site category
                var propertyLines = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Site)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Name.IndexOf("Property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                e.Name.IndexOf("Boundary", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                var boundaryPoints = new List<object>();
                var segments = new List<object>();

                foreach (var elem in propertyLines)
                {
                    var locCurve = elem.Location as LocationCurve;
                    if (locCurve != null)
                    {
                        var curve = locCurve.Curve;
                        var startPt = curve.GetEndPoint(0);
                        var endPt = curve.GetEndPoint(1);

                        boundaryPoints.Add(new { x = startPt.X, y = startPt.Y, z = startPt.Z });
                        segments.Add(new
                        {
                            elementId = elem.Id.Value,
                            start = new { x = startPt.X, y = startPt.Y, z = startPt.Z },
                            end = new { x = endPt.X, y = endPt.Y, z = endPt.Z },
                            length = curve.Length
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    propertyLineCount = propertyLines.Count,
                    segmentCount = segments.Count,
                    boundaryPoints = boundaryPoints,
                    segments = segments
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Mirror/duplicate a topography surface along an axis
        /// </summary>
        [MCPMethod("mirrorTopography", Category = "Site", Description = "Mirror/duplicate a topography surface along an axis (X or Y)")]
        public static string MirrorTopography(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = parameters["topoId"]?.Value<int>();
                var axis = parameters["axis"]?.Value<string>()?.ToUpper() ?? "X";

                if (!topoId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "topoId is required" });
                }

                var topo = doc.GetElement(new ElementId(topoId.Value)) as TopographySurface;
                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Topography not found" });
                }

                var originalPoints = topo.GetPoints();

                using (var trans = new Transaction(doc, "Mirror Topography"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Mirror points across the specified axis
                    var mirroredPoints = new List<XYZ>();
                    foreach (var pt in originalPoints)
                    {
                        if (axis == "X")
                            mirroredPoints.Add(new XYZ(-pt.X, pt.Y, pt.Z));
                        else if (axis == "Y")
                            mirroredPoints.Add(new XYZ(pt.X, -pt.Y, pt.Z));
                        else
                        {
                            trans.RollBack();
                            return JsonConvert.SerializeObject(new { success = false, error = "axis must be 'X' or 'Y'" });
                        }
                    }

                    var newTopo = TopographySurface.Create(doc, mirroredPoints);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalTopoId = topoId.Value,
                        newTopoId = newTopo.Id.Value,
                        axis = axis,
                        pointCount = mirroredPoints.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Calculate cut and fill volumes for a building pad on topography
        /// </summary>
        [MCPMethod("calculateCutFill", Category = "Site", Description = "Calculate cut and fill volumes for a building pad relative to the topography surface")]
        public static string CalculateCutFill(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var padId = parameters["padId"]?.Value<int>();

                if (!padId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "padId is required" });
                }

                var pad = doc.GetElement(new ElementId(padId.Value)) as BuildingPad;
                if (pad == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Building pad not found" });
                }

                // Get pad elevation info
                var level = doc.GetElement(pad.LevelId) as Level;
                var offsetParam = pad.get_Parameter(BuiltInParameter.BUILDINGPAD_HEIGHTABOVELEVEL_PARAM);
                double padElevation = (level?.Elevation ?? 0) + (offsetParam?.AsDouble() ?? 0);

                // Get the pad's area
                var areaParam = pad.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double padArea = areaParam?.AsDouble() ?? 0;

                // Find the topography this pad sits on
                var topos = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Topography)
                    .WhereElementIsNotElementType()
                    .Cast<TopographySurface>()
                    .Where(t => !t.IsSiteSubRegion)
                    .ToList();

                double cutVolume = 0;
                double fillVolume = 0;
                int topoPointsAnalyzed = 0;
                double avgTopoElevation = 0;

                if (topos.Count() > 0)
                {
                    // Use the first main topography surface
                    var mainTopo = topos.First();
                    var topoPoints = mainTopo.GetPoints();
                    topoPointsAnalyzed = topoPoints.Count();

                    if (topoPoints.Count() > 0)
                    {
                        avgTopoElevation = topoPoints.Average(p => p.Z);

                        // Estimate cut/fill based on pad elevation vs average topo elevation
                        double elevDiff = padElevation - avgTopoElevation;

                        if (elevDiff > 0)
                        {
                            // Pad is above topo - fill needed
                            fillVolume = padArea * Math.Abs(elevDiff);
                        }
                        else if (elevDiff < 0)
                        {
                            // Pad is below topo - cut needed
                            cutVolume = padArea * Math.Abs(elevDiff);
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    padId = padId.Value,
                    padElevation = Math.Round(padElevation, 4),
                    padArea = Math.Round(padArea, 4),
                    averageTopoElevation = Math.Round(avgTopoElevation, 4),
                    estimatedCutVolume = Math.Round(cutVolume, 4),
                    estimatedFillVolume = Math.Round(fillVolume, 4),
                    netVolume = Math.Round(fillVolume - cutVolume, 4),
                    volumeUnit = "cubic feet",
                    note = "Volumes are estimates based on average topography elevation under pad area",
                    topoPointsAnalyzed = topoPointsAnalyzed
                });
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
