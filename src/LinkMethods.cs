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

namespace RevitMCPBridge
{
    /// <summary>
    /// Link management methods (Revit links, CAD imports) for MCP Bridge
    /// </summary>
    public static class LinkMethods
    {
        /// <summary>
        /// Get all Revit links in the model
        /// </summary>
        [MCPMethod("getRevitLinks", Category = "Link", Description = "Get all Revit links in the model")]
        public static string GetRevitLinks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(l => new
                    {
                        linkId = l.Id.Value,
                        name = l.Name,
                        typeId = (int)l.GetTypeId().Value,
                        typeName = doc.GetElement(l.GetTypeId())?.Name ?? "Unknown",
                        isLoaded = RevitLinkType.IsLoaded(doc, l.GetTypeId())
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linkCount = links.Count,
                    revitLinks = links
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get Revit link types
        /// </summary>
        [MCPMethod("getRevitLinkTypes", Category = "Link", Description = "Get all Revit link types in the model")]
        public static string GetRevitLinkTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>()
                    .Select(t => new
                    {
                        typeId = t.Id.Value,
                        name = t.Name,
                        isLoaded = RevitLinkType.IsLoaded(doc, t.Id),
                        pathType = t.PathType.ToString()
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeCount = types.Count,
                    linkTypes = types
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Load a Revit link
        /// </summary>
        [MCPMethod("loadRevitLink", Category = "Link", Description = "Load a Revit link from a file path")]
        public static string LoadRevitLink(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var filePath = parameters["filePath"]?.ToString();

                if (string.IsNullOrEmpty(filePath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "filePath is required" });
                }

                using (var trans = new Transaction(doc, "Load Revit Link"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    var options = new RevitLinkOptions(false);
                    var linkLoadResult = RevitLinkType.Create(doc, modelPath, options);

                    if (linkLoadResult.ElementId == ElementId.InvalidElementId)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "Failed to create link type" });
                    }

                    var instance = RevitLinkInstance.Create(doc, linkLoadResult.ElementId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        linkTypeId = (int)linkLoadResult.ElementId.Value,
                        linkInstanceId = instance.Id.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Reload a Revit link
        /// </summary>
        [MCPMethod("reloadRevitLink", Category = "Link", Description = "Reload a Revit link from its source file")]
        public static string ReloadRevitLink(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var linkTypeId = parameters["linkTypeId"]?.Value<int>();

                if (!linkTypeId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "linkTypeId is required" });
                }

                var linkType = doc.GetElement(new ElementId(linkTypeId.Value)) as RevitLinkType;
                if (linkType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Link type not found" });
                }

                linkType.Reload();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linkTypeId = linkTypeId.Value,
                    isLoaded = RevitLinkType.IsLoaded(doc, new ElementId(linkTypeId.Value))
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Unload a Revit link
        /// </summary>
        [MCPMethod("unloadRevitLink", Category = "Link", Description = "Unload a Revit link without removing it")]
        public static string UnloadRevitLink(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var linkTypeId = parameters["linkTypeId"]?.Value<int>();

                if (!linkTypeId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "linkTypeId is required" });
                }

                var linkType = doc.GetElement(new ElementId(linkTypeId.Value)) as RevitLinkType;
                if (linkType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Link type not found" });
                }

                linkType.Unload(null);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linkTypeId = linkTypeId.Value,
                    isLoaded = false
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a Revit link (type and all instances)
        /// </summary>
        [MCPMethod("deleteRevitLink", Category = "Link", Description = "Delete a Revit link type and all its instances")]
        public static string DeleteRevitLink(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var linkTypeId = parameters["linkTypeId"]?.Value<int>();

                if (!linkTypeId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "linkTypeId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Revit Link"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(linkTypeId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedLinkTypeId = linkTypeId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all CAD imports/links in the model
        /// </summary>
        [MCPMethod("getCADLinks", Category = "Link", Description = "Get all CAD imports and links in the model")]
        public static string GetCADLinks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var cadLinks = new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance))
                    .Cast<ImportInstance>()
                    .Select(i => new
                    {
                        importId = i.Id.Value,
                        name = i.Name,
                        isLinked = i.IsLinked
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    cadLinkCount = cadLinks.Count,
                    cadLinks = cadLinks
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Import a CAD file
        /// </summary>
        [MCPMethod("importCAD", Category = "Link", Description = "Import a CAD file into the model")]
        public static string ImportCAD(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var filePath = parameters["filePath"]?.ToString();
                var viewId = parameters["viewId"]?.Value<int>();
                var asLink = parameters["asLink"]?.Value<bool>() ?? false;
                var unitStr = parameters["unit"]?.ToString()?.ToLower() ?? "foot";

                if (string.IsNullOrEmpty(filePath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "filePath is required" });
                }

                View view = null;
                if (viewId.HasValue)
                {
                    view = doc.GetElement(new ElementId(viewId.Value)) as View;
                }
                else
                {
                    view = doc.ActiveView;
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid view found" });
                }

                // Parse unit parameter
                ImportUnit importUnit = ImportUnit.Foot;
                switch (unitStr)
                {
                    case "default": importUnit = ImportUnit.Default; break;
                    case "foot": importUnit = ImportUnit.Foot; break;
                    case "inch": importUnit = ImportUnit.Inch; break;
                    case "meter": importUnit = ImportUnit.Meter; break;
                    case "centimeter": importUnit = ImportUnit.Centimeter; break;
                    case "millimeter": importUnit = ImportUnit.Millimeter; break;
                    case "decimeter": importUnit = ImportUnit.Decimeter; break;
                }

                using (var trans = new Transaction(doc, asLink ? "Link CAD" : "Import CAD"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var options = new DWGImportOptions();
                    options.ColorMode = ImportColorMode.Preserved;
                    options.Unit = importUnit;

                    ElementId importedId = ElementId.InvalidElementId;

                    if (asLink)
                    {
                        doc.Link(filePath, options, view, out importedId);
                    }
                    else
                    {
                        doc.Import(filePath, options, view, out importedId);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        importedId = (int)importedId.Value,
                        isLink = asLink,
                        unit = unitStr
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a CAD import/link
        /// </summary>
        [MCPMethod("deleteCADLink", Category = "Link", Description = "Delete a CAD import or link from the model")]
        public static string DeleteCADLink(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var importId = parameters["importId"]?.Value<int>();

                if (!importId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "importId is required" });
                }

                using (var trans = new Transaction(doc, "Delete CAD"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(importId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedImportId = importId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get link instance position/transform
        /// </summary>
        [MCPMethod("getLinkTransform", Category = "Link", Description = "Get the position and transform of a link instance")]
        public static string GetLinkTransform(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var linkInstanceId = parameters["linkInstanceId"]?.Value<int>();

                if (!linkInstanceId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "linkInstanceId is required" });
                }

                var linkInstance = doc.GetElement(new ElementId(linkInstanceId.Value)) as RevitLinkInstance;
                if (linkInstance == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Link instance not found" });
                }

                var transform = linkInstance.GetTotalTransform();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linkInstanceId = linkInstanceId.Value,
                    origin = new { x = transform.Origin.X, y = transform.Origin.Y, z = transform.Origin.Z },
                    basisX = new { x = transform.BasisX.X, y = transform.BasisX.Y, z = transform.BasisX.Z },
                    basisY = new { x = transform.BasisY.X, y = transform.BasisY.Y, z = transform.BasisY.Z },
                    basisZ = new { x = transform.BasisZ.X, y = transform.BasisZ.Y, z = transform.BasisZ.Z }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move a link instance
        /// </summary>
        [MCPMethod("moveLinkInstance", Category = "Link", Description = "Move a link instance to a new position")]
        public static string MoveLinkInstance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var linkInstanceId = parameters["linkInstanceId"]?.Value<int>();
                var translation = parameters["translation"]?.ToObject<double[]>();

                if (!linkInstanceId.HasValue || translation == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "linkInstanceId and translation are required" });
                }

                var linkInstance = doc.GetElement(new ElementId(linkInstanceId.Value)) as RevitLinkInstance;
                if (linkInstance == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Link instance not found" });
                }

                using (var trans = new Transaction(doc, "Move Link"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var moveVector = new XYZ(translation[0], translation[1], translation.Length > 2 ? translation[2] : 0);
                    ElementTransformUtils.MoveElement(doc, linkInstance.Id, moveVector);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        linkInstanceId = linkInstanceId.Value,
                        translation = new { x = translation[0], y = translation[1], z = translation.Length > 2 ? translation[2] : 0 }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extract geometry (lines, polylines, arcs) from an imported CAD file
        /// This is the key method for PDF-to-Revit workflow:
        /// 1. Convert PDF to DWF/DWG
        /// 2. Import into Revit
        /// 3. Call this to extract the geometry
        /// 4. Use extracted lines to create walls
        /// </summary>
        [MCPMethod("getCADGeometry", Category = "Link", Description = "Extract geometry lines and arcs from an imported CAD file")]
        public static string GetCADGeometry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var importId = parameters["importId"]?.Value<int>();
                var minLength = parameters["minLength"]?.Value<double>() ?? 1.0; // Min line length in feet
                var includeArcs = parameters["includeArcs"]?.Value<bool>() ?? false;

                ImportInstance importInst = null;

                if (importId.HasValue)
                {
                    importInst = doc.GetElement(new ElementId(importId.Value)) as ImportInstance;
                }
                else
                {
                    // Get first import instance if no ID specified
                    importInst = new FilteredElementCollector(doc)
                        .OfClass(typeof(ImportInstance))
                        .Cast<ImportInstance>()
                        .FirstOrDefault();
                }

                if (importInst == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No CAD import found" });
                }

                var lines = new List<object>();
                var polylines = new List<object>();
                var arcs = new List<object>();

                var options = new Options();
                options.ComputeReferences = true;
                options.IncludeNonVisibleObjects = false;

                var geoElem = importInst.get_Geometry(options);
                if (geoElem != null)
                {
                    foreach (GeometryObject geoObj in geoElem)
                    {
                        var geoInst = geoObj as GeometryInstance;
                        if (geoInst != null)
                        {
                            var instanceGeo = geoInst.GetInstanceGeometry();
                            if (instanceGeo != null)
                            {
                                foreach (GeometryObject geo in instanceGeo)
                                {
                                    if (geo is Line line)
                                    {
                                        var length = line.Length;
                                        if (length >= minLength)
                                        {
                                            lines.Add(new
                                            {
                                                startX = Math.Round(line.GetEndPoint(0).X, 4),
                                                startY = Math.Round(line.GetEndPoint(0).Y, 4),
                                                endX = Math.Round(line.GetEndPoint(1).X, 4),
                                                endY = Math.Round(line.GetEndPoint(1).Y, 4),
                                                length = Math.Round(length, 4)
                                            });
                                        }
                                    }
                                    else if (geo is PolyLine polyLine)
                                    {
                                        var coords = polyLine.GetCoordinates();
                                        var points = new List<object>();
                                        for (int i = 0; i < coords.Count; i++)
                                        {
                                            points.Add(new
                                            {
                                                x = Math.Round(coords[i].X, 4),
                                                y = Math.Round(coords[i].Y, 4)
                                            });
                                        }

                                        // Calculate total length
                                        double totalLength = 0;
                                        for (int i = 0; i < coords.Count - 1; i++)
                                        {
                                            totalLength += coords[i].DistanceTo(coords[i + 1]);
                                        }

                                        if (totalLength >= minLength)
                                        {
                                            polylines.Add(new
                                            {
                                                pointCount = coords.Count,
                                                points = points,
                                                totalLength = Math.Round(totalLength, 4)
                                            });
                                        }
                                    }
                                    else if (includeArcs && geo is Arc arc)
                                    {
                                        if (arc.Length >= minLength)
                                        {
                                            arcs.Add(new
                                            {
                                                centerX = Math.Round(arc.Center.X, 4),
                                                centerY = Math.Round(arc.Center.Y, 4),
                                                radius = Math.Round(arc.Radius, 4),
                                                startX = Math.Round(arc.GetEndPoint(0).X, 4),
                                                startY = Math.Round(arc.GetEndPoint(0).Y, 4),
                                                endX = Math.Round(arc.GetEndPoint(1).X, 4),
                                                endY = Math.Round(arc.GetEndPoint(1).Y, 4),
                                                length = Math.Round(arc.Length, 4)
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Calculate bounding box
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (dynamic line in lines)
                {
                    minX = Math.Min(minX, Math.Min((double)line.startX, (double)line.endX));
                    minY = Math.Min(minY, Math.Min((double)line.startY, (double)line.endY));
                    maxX = Math.Max(maxX, Math.Max((double)line.startX, (double)line.endX));
                    maxY = Math.Max(maxY, Math.Max((double)line.startY, (double)line.endY));
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    importId = importInst.Id.Value,
                    importName = importInst.Name,
                    lineCount = lines.Count,
                    polylineCount = polylines.Count,
                    arcCount = arcs.Count,
                    bounds = new
                    {
                        minX = Math.Round(minX, 4),
                        minY = Math.Round(minY, 4),
                        maxX = Math.Round(maxX, 4),
                        maxY = Math.Round(maxY, 4),
                        width = Math.Round(maxX - minX, 4),
                        height = Math.Round(maxY - minY, 4)
                    },
                    lines = lines,
                    polylines = polylines,
                    arcs = arcs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Filter CAD geometry to find likely wall lines (long, orthogonal lines)
        /// </summary>
        [MCPMethod("getCADWallCandidates", Category = "Link", Description = "Filter CAD geometry to find likely wall lines based on length and orientation")]
        public static string GetCADWallCandidates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var importId = parameters["importId"]?.Value<int>();
                var minWallLength = parameters["minWallLength"]?.Value<double>() ?? 3.0; // 3 feet minimum
                var angleTolerance = parameters["angleTolerance"]?.Value<double>() ?? 5.0; // degrees from horizontal/vertical

                ImportInstance importInst = null;

                if (importId.HasValue)
                {
                    importInst = doc.GetElement(new ElementId(importId.Value)) as ImportInstance;
                }
                else
                {
                    importInst = new FilteredElementCollector(doc)
                        .OfClass(typeof(ImportInstance))
                        .Cast<ImportInstance>()
                        .FirstOrDefault();
                }

                if (importInst == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No CAD import found" });
                }

                var wallCandidates = new List<object>();
                var angleToleranceRad = angleTolerance * Math.PI / 180.0;

                var options = new Options();
                options.ComputeReferences = true;
                options.IncludeNonVisibleObjects = false;

                var geoElem = importInst.get_Geometry(options);
                if (geoElem != null)
                {
                    foreach (GeometryObject geoObj in geoElem)
                    {
                        var geoInst = geoObj as GeometryInstance;
                        if (geoInst != null)
                        {
                            var instanceGeo = geoInst.GetInstanceGeometry();
                            if (instanceGeo != null)
                            {
                                foreach (GeometryObject geo in instanceGeo)
                                {
                                    if (geo is Line line && line.Length >= minWallLength)
                                    {
                                        var start = line.GetEndPoint(0);
                                        var end = line.GetEndPoint(1);
                                        var dx = end.X - start.X;
                                        var dy = end.Y - start.Y;
                                        var angle = Math.Atan2(dy, dx);

                                        // Normalize angle to 0-180 range
                                        while (angle < 0) angle += Math.PI;
                                        while (angle >= Math.PI) angle -= Math.PI;

                                        // Check if near horizontal (0 or 180) or vertical (90)
                                        bool isHorizontal = angle < angleToleranceRad || angle > (Math.PI - angleToleranceRad);
                                        bool isVertical = Math.Abs(angle - Math.PI / 2) < angleToleranceRad;

                                        if (isHorizontal || isVertical)
                                        {
                                            // Snap to exact horizontal/vertical
                                            double snapStartX = start.X, snapStartY = start.Y;
                                            double snapEndX = end.X, snapEndY = end.Y;

                                            if (isHorizontal)
                                            {
                                                var avgY = (start.Y + end.Y) / 2;
                                                snapStartY = avgY;
                                                snapEndY = avgY;
                                            }
                                            else
                                            {
                                                var avgX = (start.X + end.X) / 2;
                                                snapStartX = avgX;
                                                snapEndX = avgX;
                                            }

                                            wallCandidates.Add(new
                                            {
                                                startX = Math.Round(snapStartX, 4),
                                                startY = Math.Round(snapStartY, 4),
                                                endX = Math.Round(snapEndX, 4),
                                                endY = Math.Round(snapEndY, 4),
                                                length = Math.Round(line.Length, 4),
                                                orientation = isHorizontal ? "horizontal" : "vertical",
                                                originalAngleDegrees = Math.Round(angle * 180 / Math.PI, 2)
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Sort by length descending (longest walls first)
                var sorted = wallCandidates
                    .OrderByDescending(w => ((dynamic)w).length)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    importId = importInst.Id.Value,
                    wallCandidateCount = sorted.Count,
                    wallCandidates = sorted
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Intelligent CAD floor plan analyzer
        /// Traces continuous wall paths, identifies openings (doors/windows), and classifies elements
        /// Returns a structured floor plan ready for Revit model creation
        /// </summary>
        [MCPMethod("analyzeCADFloorPlan", Category = "Link", Description = "Analyze a CAD floor plan to identify walls, openings, and structural elements for model creation")]
        public static string AnalyzeCADFloorPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var importId = parameters["importId"]?.Value<int>();
                var exteriorWallThickness = parameters["exteriorWallThickness"]?.Value<double>() ?? 10.5; // inches
                var interiorWallThickness = parameters["interiorWallThickness"]?.Value<double>() ?? 4.5;  // inches
                var tolerance = parameters["tolerance"]?.Value<double>() ?? 2.0; // inches tolerance for matching

                ImportInstance importInst = null;

                if (importId.HasValue)
                {
                    importInst = doc.GetElement(new ElementId(importId.Value)) as ImportInstance;
                }
                else
                {
                    importInst = new FilteredElementCollector(doc)
                        .OfClass(typeof(ImportInstance))
                        .Cast<ImportInstance>()
                        .FirstOrDefault();
                }

                if (importInst == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No CAD import found" });
                }

                // Extract all geometry
                var allLines = new List<CADLine>();
                var allArcs = new List<CADArc>();

                var options = new Options();
                options.ComputeReferences = true;
                options.IncludeNonVisibleObjects = false;

                var geoElem = importInst.get_Geometry(options);
                if (geoElem != null)
                {
                    foreach (GeometryObject geoObj in geoElem)
                    {
                        var geoInst = geoObj as GeometryInstance;
                        if (geoInst != null)
                        {
                            var instanceGeo = geoInst.GetInstanceGeometry();
                            if (instanceGeo != null)
                            {
                                foreach (GeometryObject geo in instanceGeo)
                                {
                                    if (geo is Line line && line.Length >= 0.5) // 6 inches min
                                    {
                                        var cadLine = new CADLine
                                        {
                                            StartX = line.GetEndPoint(0).X,
                                            StartY = line.GetEndPoint(0).Y,
                                            EndX = line.GetEndPoint(1).X,
                                            EndY = line.GetEndPoint(1).Y,
                                            Length = line.Length
                                        };

                                        // Determine orientation
                                        var dx = Math.Abs(cadLine.EndX - cadLine.StartX);
                                        var dy = Math.Abs(cadLine.EndY - cadLine.StartY);
                                        if (dy < cadLine.Length * 0.05) cadLine.Orientation = "H";
                                        else if (dx < cadLine.Length * 0.05) cadLine.Orientation = "V";
                                        else cadLine.Orientation = "D"; // Diagonal

                                        allLines.Add(cadLine);
                                    }
                                    else if (geo is Arc arc && arc.Length >= 0.5)
                                    {
                                        allArcs.Add(new CADArc
                                        {
                                            CenterX = arc.Center.X,
                                            CenterY = arc.Center.Y,
                                            Radius = arc.Radius,
                                            StartX = arc.GetEndPoint(0).X,
                                            StartY = arc.GetEndPoint(0).Y,
                                            EndX = arc.GetEndPoint(1).X,
                                            EndY = arc.GetEndPoint(1).Y,
                                            Length = arc.Length
                                        });
                                    }
                                }
                            }
                        }
                    }
                }

                // Calculate bounds
                double minX = allLines.Min(l => Math.Min(l.StartX, l.EndX));
                double maxX = allLines.Max(l => Math.Max(l.StartX, l.EndX));
                double minY = allLines.Min(l => Math.Min(l.StartY, l.EndY));
                double maxY = allLines.Max(l => Math.Max(l.StartY, l.EndY));

                // Convert thickness from inches to feet for comparison
                double extThickFt = exteriorWallThickness / 12.0;
                double intThickFt = interiorWallThickness / 12.0;
                double tolFt = tolerance / 12.0;

                // Step 1: Find parallel line pairs (wall thickness)
                var horizontalLines = allLines.Where(l => l.Orientation == "H").OrderBy(l => l.StartY).ToList();
                var verticalLines = allLines.Where(l => l.Orientation == "V").OrderBy(l => l.StartX).ToList();

                var wallSegments = new List<WallSegment>();

                // Process horizontal wall pairs
                for (int i = 0; i < horizontalLines.Count; i++)
                {
                    var line1 = horizontalLines[i];
                    if (line1.Used) continue;

                    for (int j = i + 1; j < horizontalLines.Count; j++)
                    {
                        var line2 = horizontalLines[j];
                        if (line2.Used) continue;

                        double yGap = Math.Abs(line2.StartY - line1.StartY);
                        if (yGap > extThickFt + tolFt) break; // Too far, stop searching

                        // Check if this gap matches exterior or interior wall thickness
                        bool isExterior = Math.Abs(yGap - extThickFt) <= tolFt;
                        bool isInterior = Math.Abs(yGap - intThickFt) <= tolFt;

                        if (!isExterior && !isInterior) continue;

                        // Check X overlap
                        double x1Min = Math.Min(line1.StartX, line1.EndX);
                        double x1Max = Math.Max(line1.StartX, line1.EndX);
                        double x2Min = Math.Min(line2.StartX, line2.EndX);
                        double x2Max = Math.Max(line2.StartX, line2.EndX);

                        double overlapStart = Math.Max(x1Min, x2Min);
                        double overlapEnd = Math.Min(x1Max, x2Max);

                        if (overlapEnd - overlapStart >= 1.0) // At least 1 ft overlap
                        {
                            var centerY = (line1.StartY + line2.StartY) / 2;
                            wallSegments.Add(new WallSegment
                            {
                                StartX = overlapStart,
                                StartY = centerY,
                                EndX = overlapEnd,
                                EndY = centerY,
                                Length = overlapEnd - overlapStart,
                                Orientation = "H",
                                WallType = isExterior ? "exterior" : "interior",
                                ThicknessInches = Math.Round(yGap * 12, 1)
                            });
                            line1.Used = true;
                            line2.Used = true;
                        }
                    }
                }

                // Process vertical wall pairs
                for (int i = 0; i < verticalLines.Count; i++)
                {
                    var line1 = verticalLines[i];
                    if (line1.Used) continue;

                    for (int j = i + 1; j < verticalLines.Count; j++)
                    {
                        var line2 = verticalLines[j];
                        if (line2.Used) continue;

                        double xGap = Math.Abs(line2.StartX - line1.StartX);
                        if (xGap > extThickFt + tolFt) break;

                        bool isExterior = Math.Abs(xGap - extThickFt) <= tolFt;
                        bool isInterior = Math.Abs(xGap - intThickFt) <= tolFt;

                        if (!isExterior && !isInterior) continue;

                        // Check Y overlap
                        double y1Min = Math.Min(line1.StartY, line1.EndY);
                        double y1Max = Math.Max(line1.StartY, line1.EndY);
                        double y2Min = Math.Min(line2.StartY, line2.EndY);
                        double y2Max = Math.Max(line2.StartY, line2.EndY);

                        double overlapStart = Math.Max(y1Min, y2Min);
                        double overlapEnd = Math.Min(y1Max, y2Max);

                        if (overlapEnd - overlapStart >= 1.0)
                        {
                            var centerX = (line1.StartX + line2.StartX) / 2;
                            wallSegments.Add(new WallSegment
                            {
                                StartX = centerX,
                                StartY = overlapStart,
                                EndX = centerX,
                                EndY = overlapEnd,
                                Length = overlapEnd - overlapStart,
                                Orientation = "V",
                                WallType = isExterior ? "exterior" : "interior",
                                ThicknessInches = Math.Round(xGap * 12, 1)
                            });
                            line1.Used = true;
                            line2.Used = true;
                        }
                    }
                }

                // Step 2: Merge collinear wall segments into continuous walls
                // Use much larger gap tolerance to find all segments on the same wall line
                var mergedWalls = MergeCollinearWalls(wallSegments, 10.0); // 10 ft gap tolerance to capture openings

                // Step 3: Identify doors from arcs (door swings)
                var doors = new List<object>();
                foreach (var arc in allArcs)
                {
                    // Door swings are typically 90-degree arcs with radius matching door width
                    if (arc.Radius >= 2.0 && arc.Radius <= 3.5) // 2' to 3'6" door widths
                    {
                        // Find which wall this door is on
                        string wallOrientation = null;
                        foreach (var wall in mergedWalls)
                        {
                            if (IsPointNearWall(arc.CenterX, arc.CenterY, wall, 0.5))
                            {
                                wallOrientation = wall.Orientation;
                                break;
                            }
                        }

                        doors.Add(new
                        {
                            centerX = Math.Round(arc.CenterX, 2),
                            centerY = Math.Round(arc.CenterY, 2),
                            widthInches = Math.Round(arc.Radius * 12, 0),
                            widthFeet = Math.Round(arc.Radius, 2),
                            swingRadius = Math.Round(arc.Radius, 2),
                            wallOrientation = wallOrientation
                        });
                    }
                }

                // Step 4: Identify windows (gaps in exterior walls with short parallel lines)
                var windows = FindWindowOpenings(mergedWalls, allLines, extThickFt, tolFt);

                // Build result
                var exteriorWalls = mergedWalls.Where(w => w.WallType == "exterior")
                    .Select(w => new
                    {
                        startX = Math.Round(w.StartX, 2),
                        startY = Math.Round(w.StartY, 2),
                        endX = Math.Round(w.EndX, 2),
                        endY = Math.Round(w.EndY, 2),
                        length = Math.Round(w.Length, 2),
                        orientation = w.Orientation,
                        thicknessInches = w.ThicknessInches,
                        openings = w.Openings?.Select(o => new { start = o.Start, end = o.End, width = Math.Round(o.End - o.Start, 2) }).ToList()
                    }).ToList();

                var interiorWalls = mergedWalls.Where(w => w.WallType == "interior")
                    .Select(w => new
                    {
                        startX = Math.Round(w.StartX, 2),
                        startY = Math.Round(w.StartY, 2),
                        endX = Math.Round(w.EndX, 2),
                        endY = Math.Round(w.EndY, 2),
                        length = Math.Round(w.Length, 2),
                        orientation = w.Orientation,
                        thicknessInches = w.ThicknessInches,
                        openings = w.Openings?.Select(o => new { start = o.Start, end = o.End, width = Math.Round(o.End - o.Start, 2) }).ToList()
                    }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    importId = importInst.Id.Value,
                    bounds = new
                    {
                        minX = Math.Round(minX, 2),
                        minY = Math.Round(minY, 2),
                        maxX = Math.Round(maxX, 2),
                        maxY = Math.Round(maxY, 2),
                        width = Math.Round(maxX - minX, 2),
                        height = Math.Round(maxY - minY, 2)
                    },
                    summary = new
                    {
                        totalLines = allLines.Count,
                        totalArcs = allArcs.Count,
                        exteriorWallCount = exteriorWalls.Count,
                        interiorWallCount = interiorWalls.Count,
                        doorCount = doors.Count,
                        windowCount = windows.Count
                    },
                    exteriorWalls = exteriorWalls,
                    interiorWalls = interiorWalls,
                    doors = doors,
                    windows = windows
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper classes for CAD analysis
        private class CADLine
        {
            public double StartX { get; set; }
            public double StartY { get; set; }
            public double EndX { get; set; }
            public double EndY { get; set; }
            public double Length { get; set; }
            public string Orientation { get; set; }
            public bool Used { get; set; }
        }

        private class CADArc
        {
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double Radius { get; set; }
            public double StartX { get; set; }
            public double StartY { get; set; }
            public double EndX { get; set; }
            public double EndY { get; set; }
            public double Length { get; set; }
        }

        private class WallSegment
        {
            public double StartX { get; set; }
            public double StartY { get; set; }
            public double EndX { get; set; }
            public double EndY { get; set; }
            public double Length { get; set; }
            public string Orientation { get; set; }
            public string WallType { get; set; }
            public double ThicknessInches { get; set; }
            public List<WallOpening> Openings { get; set; }
        }

        private class WallOpening
        {
            public double Start { get; set; }
            public double End { get; set; }
            public string Type { get; set; } // "door" or "window"
        }

        private static List<WallSegment> MergeCollinearWalls(List<WallSegment> segments, double gapTolerance)
        {
            var result = new List<WallSegment>();

            // Group by orientation and wall type FIRST, then by position (Y for H, X for V)
            // Use a tolerance of 0.5 ft for same-line grouping
            var horizontalGroups = segments
                .Where(s => s.Orientation == "H")
                .GroupBy(s => new {
                    Y = Math.Round(s.StartY * 2) / 2, // Round to nearest 0.5 ft
                    Type = s.WallType
                });

            var verticalGroups = segments
                .Where(s => s.Orientation == "V")
                .GroupBy(s => new {
                    X = Math.Round(s.StartX * 2) / 2, // Round to nearest 0.5 ft
                    Type = s.WallType
                });

            // Process horizontal wall groups
            foreach (var group in horizontalGroups)
            {
                var segsOnLine = group.OrderBy(s => Math.Min(s.StartX, s.EndX)).ToList();
                if (segsOnLine.Count == 0) continue;

                // Start with the first segment
                var merged = new WallSegment
                {
                    StartX = Math.Min(segsOnLine[0].StartX, segsOnLine[0].EndX),
                    EndX = Math.Max(segsOnLine[0].StartX, segsOnLine[0].EndX),
                    StartY = segsOnLine[0].StartY,
                    EndY = segsOnLine[0].StartY,
                    Orientation = "H",
                    WallType = segsOnLine[0].WallType,
                    ThicknessInches = segsOnLine[0].ThicknessInches,
                    Openings = new List<WallOpening>()
                };

                // Merge all segments on this line
                for (int i = 1; i < segsOnLine.Count; i++)
                {
                    var seg = segsOnLine[i];
                    double segMin = Math.Min(seg.StartX, seg.EndX);
                    double segMax = Math.Max(seg.StartX, seg.EndX);

                    // Gap between current merged end and this segment start
                    double gap = segMin - merged.EndX;

                    if (gap > 0.25 && gap <= gapTolerance) // Gap is an opening (3" to 10')
                    {
                        merged.Openings.Add(new WallOpening
                        {
                            Start = merged.EndX,
                            End = segMin,
                            Type = gap >= 2.0 ? "door" : "window" // 2' or more = likely door
                        });
                    }

                    // Extend the merged wall
                    merged.EndX = Math.Max(merged.EndX, segMax);
                }

                merged.Length = merged.EndX - merged.StartX;

                // Only add if wall is reasonably long (3+ feet)
                if (merged.Length >= 3.0)
                    result.Add(merged);
            }

            // Process vertical wall groups
            foreach (var group in verticalGroups)
            {
                var segsOnLine = group.OrderBy(s => Math.Min(s.StartY, s.EndY)).ToList();
                if (segsOnLine.Count == 0) continue;

                var merged = new WallSegment
                {
                    StartY = Math.Min(segsOnLine[0].StartY, segsOnLine[0].EndY),
                    EndY = Math.Max(segsOnLine[0].StartY, segsOnLine[0].EndY),
                    StartX = segsOnLine[0].StartX,
                    EndX = segsOnLine[0].StartX,
                    Orientation = "V",
                    WallType = segsOnLine[0].WallType,
                    ThicknessInches = segsOnLine[0].ThicknessInches,
                    Openings = new List<WallOpening>()
                };

                for (int i = 1; i < segsOnLine.Count; i++)
                {
                    var seg = segsOnLine[i];
                    double segMin = Math.Min(seg.StartY, seg.EndY);
                    double segMax = Math.Max(seg.StartY, seg.EndY);

                    double gap = segMin - merged.EndY;

                    if (gap > 0.25 && gap <= gapTolerance)
                    {
                        merged.Openings.Add(new WallOpening
                        {
                            Start = merged.EndY,
                            End = segMin,
                            Type = gap >= 2.0 ? "door" : "window"
                        });
                    }

                    merged.EndY = Math.Max(merged.EndY, segMax);
                }

                merged.Length = merged.EndY - merged.StartY;

                if (merged.Length >= 3.0)
                    result.Add(merged);
            }

            return result;
        }

        private static bool IsPointNearWall(double x, double y, WallSegment wall, double tolerance)
        {
            if (wall.Orientation == "H")
            {
                double minX = Math.Min(wall.StartX, wall.EndX);
                double maxX = Math.Max(wall.StartX, wall.EndX);
                return Math.Abs(y - wall.StartY) <= tolerance && x >= minX - tolerance && x <= maxX + tolerance;
            }
            else
            {
                double minY = Math.Min(wall.StartY, wall.EndY);
                double maxY = Math.Max(wall.StartY, wall.EndY);
                return Math.Abs(x - wall.StartX) <= tolerance && y >= minY - tolerance && y <= maxY + tolerance;
            }
        }

        private static List<object> FindWindowOpenings(List<WallSegment> walls, List<CADLine> allLines, double wallThickFt, double tolFt)
        {
            var windows = new List<object>();

            // Windows are typically short parallel lines (sill/head) within exterior walls
            foreach (var wall in walls.Where(w => w.WallType == "exterior"))
            {
                if (wall.Openings == null) continue;

                foreach (var opening in wall.Openings)
                {
                    double width = opening.End - opening.Start;
                    if (width >= 1.5 && width <= 8.0) // Typical window widths 1.5' to 8'
                    {
                        double centerPos = (opening.Start + opening.End) / 2;
                        windows.Add(new
                        {
                            wallOrientation = wall.Orientation,
                            position = wall.Orientation == "H"
                                ? new { x = Math.Round(centerPos, 2), y = Math.Round(wall.StartY, 2) }
                                : new { x = Math.Round(wall.StartX, 2), y = Math.Round(centerPos, 2) },
                            widthFeet = Math.Round(width, 2),
                            widthInches = Math.Round(width * 12, 0)
                        });
                    }
                }
            }

            return windows;
        }

        /// <summary>
        /// Query elements in linked models with their coordinates in host model space.
        /// </summary>
        [MCPMethod("queryLinkedElementCoordinates", Category = "Link", Description = "Query elements in linked models and return their coordinates in host model space")]
        public static string QueryLinkedElementCoordinates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryFilter = parameters?["category"]?.ToString();
                var linkName = parameters?["linkName"]?.ToString();
                var includeGeometry = parameters?["includeGeometry"]?.ToObject<bool>() ?? false;

                // Get all Revit link instances
                var linkInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                if (!string.IsNullOrEmpty(linkName))
                {
                    linkInstances = linkInstances.Where(l =>
                        l.Name.IndexOf(linkName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                var results = new List<object>();

                foreach (var linkInstance in linkInstances)
                {
                    var linkDoc = linkInstance.GetLinkDocument();
                    if (linkDoc == null) continue;

                    // Get transform from link to host
                    var transform = linkInstance.GetTotalTransform();

                    // Collect elements from linked model
                    var collector = new FilteredElementCollector(linkDoc)
                        .WhereElementIsNotElementType();

                    // Apply category filter
                    if (!string.IsNullOrEmpty(categoryFilter))
                    {
                        if (Enum.TryParse<BuiltInCategory>(categoryFilter, out var cat))
                        {
                            collector = collector.OfCategory(cat) as FilteredElementCollector;
                        }
                    }

                    var elements = collector.Take(200).ToList(); // Limit for performance

                    var linkElements = new List<object>();

                    foreach (var elem in elements)
                    {
                        // Get element location in link coordinates
                        XYZ linkLocation = null;
                        var locPoint = elem.Location as LocationPoint;
                        var locCurve = elem.Location as LocationCurve;

                        if (locPoint != null)
                        {
                            linkLocation = locPoint.Point;
                        }
                        else if (locCurve != null)
                        {
                            linkLocation = (locCurve.Curve.GetEndPoint(0) + locCurve.Curve.GetEndPoint(1)) / 2;
                        }
                        else
                        {
                            // Try bounding box center
                            var bb = elem.get_BoundingBox(null);
                            if (bb != null)
                            {
                                linkLocation = (bb.Min + bb.Max) / 2;
                            }
                        }

                        if (linkLocation == null) continue;

                        // Transform to host model coordinates
                        var hostLocation = transform.OfPoint(linkLocation);

                        var elemData = new Dictionary<string, object>
                        {
                            { "elementId", elem.Id.Value },
                            { "category", elem.Category?.Name },
                            { "name", elem.Name },
                            { "linkCoordinates", new { x = Math.Round(linkLocation.X, 2), y = Math.Round(linkLocation.Y, 2), z = Math.Round(linkLocation.Z, 2) } },
                            { "hostCoordinates", new { x = Math.Round(hostLocation.X, 2), y = Math.Round(hostLocation.Y, 2), z = Math.Round(hostLocation.Z, 2) } }
                        };

                        if (includeGeometry)
                        {
                            var bb = elem.get_BoundingBox(null);
                            if (bb != null)
                            {
                                var minHost = transform.OfPoint(bb.Min);
                                var maxHost = transform.OfPoint(bb.Max);
                                elemData["boundingBox"] = new
                                {
                                    min = new { x = Math.Round(minHost.X, 2), y = Math.Round(minHost.Y, 2), z = Math.Round(minHost.Z, 2) },
                                    max = new { x = Math.Round(maxHost.X, 2), y = Math.Round(maxHost.Y, 2), z = Math.Round(maxHost.Z, 2) }
                                };
                            }
                        }

                        linkElements.Add(elemData);
                    }

                    results.Add(new
                    {
                        linkInstanceId = linkInstance.Id.Value,
                        linkName = linkInstance.Name,
                        linkPath = linkDoc.PathName,
                        transform = new
                        {
                            origin = new { x = Math.Round(transform.Origin.X, 2), y = Math.Round(transform.Origin.Y, 2), z = Math.Round(transform.Origin.Z, 2) },
                            hasRotation = !transform.BasisX.IsAlmostEqualTo(XYZ.BasisX)
                        },
                        elementCount = linkElements.Count,
                        elements = linkElements
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linksFound = linkInstances.Count,
                    categoryFilter = categoryFilter ?? "all",
                    links = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
