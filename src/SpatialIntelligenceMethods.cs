using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// Spatial Intelligence Methods - The "brain" for intelligent element placement
    ///
    /// Phase 1: Spatial Awareness - Understanding what's on a sheet/view
    /// Phase 2: Analysis - Finding empty space, detecting overlaps
    /// Phase 3: Smart Placement - Intelligent positioning with collision avoidance
    /// </summary>
    public static class SpatialIntelligenceMethods
    {
        #region Phase 1: Spatial Awareness

        /// <summary>
        /// Get the bounding box of any element in sheet or model coordinates
        /// </summary>
        public static string GetElementBoundingBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementId = parameters["elementId"].Value<int>();
                bool useSheetCoordinates = parameters["useSheetCoordinates"]?.Value<bool>() ?? false;

                Element element = doc.GetElement(new ElementId(elementId));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Element {elementId} not found" });
                }

                BoundingBoxXYZ bbox = element.get_BoundingBox(null);
                if (bbox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element has no bounding box" });
                }

                var result = new
                {
                    success = true,
                    elementId = elementId,
                    elementName = element.Name,
                    category = element.Category?.Name ?? "Unknown",
                    boundingBox = new
                    {
                        min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                        max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z },
                        width = bbox.Max.X - bbox.Min.X,
                        height = bbox.Max.Y - bbox.Min.Y,
                        depth = bbox.Max.Z - bbox.Min.Z,
                        center = new
                        {
                            x = (bbox.Min.X + bbox.Max.X) / 2,
                            y = (bbox.Min.Y + bbox.Max.Y) / 2,
                            z = (bbox.Min.Z + bbox.Max.Z) / 2
                        }
                    },
                    units = "feet"
                };

                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all viewports on a sheet with their bounding boxes and positions
        /// Critical for understanding sheet layout
        /// </summary>
        public static string GetViewportBoundingBoxes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                }

                int sheetId = parameters["sheetId"].Value<int>();
                ViewSheet sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Sheet {sheetId} not found" });
                }

                var viewportIds = sheet.GetAllViewports();
                var viewports = new List<object>();

                foreach (ElementId vpId in viewportIds)
                {
                    Viewport vp = doc.GetElement(vpId) as Viewport;
                    if (vp == null) continue;

                    // Get the viewport outline (box on sheet)
                    Outline outline = vp.GetBoxOutline();
                    XYZ center = vp.GetBoxCenter();

                    // Get the view it contains
                    View view = doc.GetElement(vp.ViewId) as View;

                    viewports.Add(new
                    {
                        viewportId = vp.Id.Value,
                        viewId = vp.ViewId.Value,
                        viewName = view?.Name ?? "Unknown",
                        viewType = view?.ViewType.ToString() ?? "Unknown",
                        boxCenter = new { x = center.X, y = center.Y },
                        boxOutline = new
                        {
                            minX = outline.MinimumPoint.X,
                            minY = outline.MinimumPoint.Y,
                            maxX = outline.MaximumPoint.X,
                            maxY = outline.MaximumPoint.Y,
                            width = outline.MaximumPoint.X - outline.MinimumPoint.X,
                            height = outline.MaximumPoint.Y - outline.MinimumPoint.Y
                        },
                        labelOffset = vp.LabelOffset != null ? new { x = vp.LabelOffset.X, y = vp.LabelOffset.Y } : null
                    });
                }

                // Get sheet dimensions from title block
                var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .ToElements();

                object sheetSize = null;
                if (titleBlocks.Count > 0)
                {
                    var tb = titleBlocks[0];
                    BoundingBoxXYZ tbBox = tb.get_BoundingBox(null);
                    if (tbBox != null)
                    {
                        sheetSize = new
                        {
                            width = tbBox.Max.X - tbBox.Min.X,
                            height = tbBox.Max.Y - tbBox.Min.Y,
                            minX = tbBox.Min.X,
                            minY = tbBox.Min.Y,
                            maxX = tbBox.Max.X,
                            maxY = tbBox.Max.Y
                        };
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = sheetId,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    sheetSize = sheetSize,
                    viewportCount = viewports.Count,
                    viewports = viewports,
                    units = "feet"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all annotations in a view with their bounding boxes
        /// Includes text notes, tags, keynotes, dimensions, symbols
        /// </summary>
        public static string GetAnnotationBoundingBoxes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                int viewId = parameters["viewId"].Value<int>();
                View view = doc.GetElement(new ElementId(viewId)) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"View {viewId} not found" });
                }

                var annotations = new List<object>();

                // Collect all annotation categories
                var annotationCategories = new List<BuiltInCategory>
                {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_KeynoteTags,
                    BuiltInCategory.OST_GenericAnnotation,
                    BuiltInCategory.OST_Dimensions,
                    BuiltInCategory.OST_DoorTags,
                    BuiltInCategory.OST_WindowTags,
                    BuiltInCategory.OST_RoomTags,
                    BuiltInCategory.OST_AreaTags,
                    BuiltInCategory.OST_DetailComponents,
                    BuiltInCategory.OST_SpotElevations,
                    BuiltInCategory.OST_SpotCoordinates,
                    BuiltInCategory.OST_RevisionClouds,
                    BuiltInCategory.OST_RevisionCloudTags
                };

                foreach (var category in annotationCategories)
                {
                    try
                    {
                        var collector = new FilteredElementCollector(doc, view.Id)
                            .OfCategory(category)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(view);
                            if (bbox == null) continue;

                            var annotationInfo = new Dictionary<string, object>
                            {
                                { "elementId", elem.Id.Value },
                                { "category", category.ToString().Replace("OST_", "") },
                                { "familyName", GetFamilyName(elem) },
                                { "typeName", GetTypeName(elem) },
                                { "boundingBox", new {
                                    minX = bbox.Min.X,
                                    minY = bbox.Min.Y,
                                    maxX = bbox.Max.X,
                                    maxY = bbox.Max.Y,
                                    width = bbox.Max.X - bbox.Min.X,
                                    height = bbox.Max.Y - bbox.Min.Y,
                                    centerX = (bbox.Min.X + bbox.Max.X) / 2,
                                    centerY = (bbox.Min.Y + bbox.Max.Y) / 2
                                }}
                            };

                            // Add text content if it's a text note
                            if (elem is TextNote textNote)
                            {
                                annotationInfo["text"] = textNote.Text;
                            }

                            // Add location if available
                            if (elem.Location is LocationPoint locPt)
                            {
                                annotationInfo["location"] = new { x = locPt.Point.X, y = locPt.Point.Y, z = locPt.Point.Z };
                            }

                            annotations.Add(annotationInfo);
                        }
                    }
                    catch { /* Skip categories that don't exist in this view */ }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    annotationCount = annotations.Count,
                    annotations = annotations,
                    units = "feet"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get complete sheet layout including titleblock zones, viewports, and annotations
        /// This is the master method for understanding a sheet's spatial organization
        /// </summary>
        public static string GetSheetLayout(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                }

                int sheetId = parameters["sheetId"].Value<int>();
                ViewSheet sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Sheet {sheetId} not found" });
                }

                // Get title block and sheet dimensions
                var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .ToElements();

                double sheetWidth = 3.0; // Default 36" in feet
                double sheetHeight = 2.0; // Default 24" in feet
                double marginLeft = 0.0;
                double marginBottom = 0.0;

                object titleBlockInfo = null;

                if (titleBlocks.Count > 0)
                {
                    var tb = titleBlocks[0];
                    BoundingBoxXYZ tbBox = tb.get_BoundingBox(null);
                    if (tbBox != null)
                    {
                        sheetWidth = tbBox.Max.X - tbBox.Min.X;
                        sheetHeight = tbBox.Max.Y - tbBox.Min.Y;
                        marginLeft = tbBox.Min.X;
                        marginBottom = tbBox.Min.Y;

                        titleBlockInfo = new
                        {
                            elementId = tb.Id.Value,
                            familyName = GetFamilyName(tb),
                            width = sheetWidth,
                            height = sheetHeight,
                            origin = new { x = marginLeft, y = marginBottom }
                        };
                    }
                }

                // Get viewports
                var viewportIds = sheet.GetAllViewports();
                var viewports = new List<object>();
                var occupiedRegions = new List<object>();

                foreach (ElementId vpId in viewportIds)
                {
                    Viewport vp = doc.GetElement(vpId) as Viewport;
                    if (vp == null) continue;

                    Outline outline = vp.GetBoxOutline();
                    XYZ center = vp.GetBoxCenter();
                    View view = doc.GetElement(vp.ViewId) as View;

                    var vpInfo = new
                    {
                        viewportId = vp.Id.Value,
                        viewId = vp.ViewId.Value,
                        viewName = view?.Name ?? "Unknown",
                        viewType = view?.ViewType.ToString() ?? "Unknown",
                        center = new { x = center.X, y = center.Y },
                        bounds = new
                        {
                            minX = outline.MinimumPoint.X,
                            minY = outline.MinimumPoint.Y,
                            maxX = outline.MaximumPoint.X,
                            maxY = outline.MaximumPoint.Y
                        }
                    };
                    viewports.Add(vpInfo);

                    // Add to occupied regions
                    occupiedRegions.Add(new
                    {
                        type = "viewport",
                        id = vp.Id.Value,
                        minX = outline.MinimumPoint.X,
                        minY = outline.MinimumPoint.Y,
                        maxX = outline.MaximumPoint.X,
                        maxY = outline.MaximumPoint.Y
                    });
                }

                // Get all annotations on sheet
                var annotations = new List<object>();
                var annotationCategories = new List<BuiltInCategory>
                {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_GenericAnnotation,
                    BuiltInCategory.OST_LegendComponents
                };

                foreach (var category in annotationCategories)
                {
                    try
                    {
                        var collector = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(category)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(null);
                            if (bbox == null) continue;

                            annotations.Add(new
                            {
                                elementId = elem.Id.Value,
                                category = category.ToString().Replace("OST_", ""),
                                bounds = new
                                {
                                    minX = bbox.Min.X,
                                    minY = bbox.Min.Y,
                                    maxX = bbox.Max.X,
                                    maxY = bbox.Max.Y
                                }
                            });

                            occupiedRegions.Add(new
                            {
                                type = "annotation",
                                id = elem.Id.Value,
                                minX = bbox.Min.X,
                                minY = bbox.Min.Y,
                                maxX = bbox.Max.X,
                                maxY = bbox.Max.Y
                            });
                        }
                    }
                    catch { }
                }

                // Define logical zones based on sheet size
                // These are typical architectural sheet zones
                double zoneMargin = 0.1; // 1.2" margin
                var zones = new
                {
                    titleBlock = new
                    {
                        name = "Title Block",
                        minX = sheetWidth - 0.6 + marginLeft, // Right 7.2" for title block
                        minY = marginBottom,
                        maxX = sheetWidth + marginLeft,
                        maxY = sheetHeight * 0.3 + marginBottom
                    },
                    notesArea = new
                    {
                        name = "General Notes Area",
                        minX = sheetWidth - 0.8 + marginLeft,
                        minY = sheetHeight * 0.3 + marginBottom,
                        maxX = sheetWidth - zoneMargin + marginLeft,
                        maxY = sheetHeight - zoneMargin + marginBottom
                    },
                    legendArea = new
                    {
                        name = "Legend Area",
                        minX = marginLeft + zoneMargin,
                        minY = marginBottom + zoneMargin,
                        maxX = marginLeft + 0.6,
                        maxY = sheetHeight * 0.4 + marginBottom
                    },
                    drawingArea = new
                    {
                        name = "Drawing Area",
                        minX = marginLeft + zoneMargin,
                        minY = marginBottom + zoneMargin,
                        maxX = sheetWidth - 0.8 + marginLeft,
                        maxY = sheetHeight - zoneMargin + marginBottom
                    }
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = sheetId,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    dimensions = new
                    {
                        width = sheetWidth,
                        height = sheetHeight,
                        widthInches = sheetWidth * 12,
                        heightInches = sheetHeight * 12
                    },
                    titleBlock = titleBlockInfo,
                    viewports = viewports,
                    annotations = annotations,
                    occupiedRegions = occupiedRegions,
                    zones = zones,
                    units = "feet"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Phase 2: Analysis

        /// <summary>
        /// Find empty rectangular spaces on a sheet that can fit a given size
        /// This is key for intelligent placement
        /// </summary>
        public static string FindEmptySpaceOnSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                }

                int sheetId = parameters["sheetId"].Value<int>();
                double requiredWidth = parameters["requiredWidth"]?.Value<double>() ?? 0.5; // Default 6" in feet
                double requiredHeight = parameters["requiredHeight"]?.Value<double>() ?? 0.25; // Default 3" in feet
                string preferredZone = parameters["preferredZone"]?.Value<string>() ?? "any"; // notesArea, legendArea, drawingArea, any

                ViewSheet sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Sheet {sheetId} not found" });
                }

                // Get sheet bounds from title block
                var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .ToElements();

                double sheetMinX = 0, sheetMinY = 0, sheetMaxX = 3.0, sheetMaxY = 2.0;

                if (titleBlocks.Count > 0)
                {
                    BoundingBoxXYZ tbBox = titleBlocks[0].get_BoundingBox(null);
                    if (tbBox != null)
                    {
                        sheetMinX = tbBox.Min.X;
                        sheetMinY = tbBox.Min.Y;
                        sheetMaxX = tbBox.Max.X;
                        sheetMaxY = tbBox.Max.Y;
                    }
                }

                // Collect all occupied regions
                var occupiedRects = new List<Tuple<double, double, double, double>>();

                // Add viewports
                foreach (ElementId vpId in sheet.GetAllViewports())
                {
                    Viewport vp = doc.GetElement(vpId) as Viewport;
                    if (vp == null) continue;
                    Outline outline = vp.GetBoxOutline();
                    occupiedRects.Add(Tuple.Create(
                        outline.MinimumPoint.X - 0.05, // Add small buffer
                        outline.MinimumPoint.Y - 0.05,
                        outline.MaximumPoint.X + 0.05,
                        outline.MaximumPoint.Y + 0.05
                    ));
                }

                // Add annotations
                var annotationCategories = new[] {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_GenericAnnotation,
                    BuiltInCategory.OST_LegendComponents
                };

                foreach (var cat in annotationCategories)
                {
                    try
                    {
                        var collector = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(null);
                            if (bbox != null)
                            {
                                occupiedRects.Add(Tuple.Create(
                                    bbox.Min.X - 0.02,
                                    bbox.Min.Y - 0.02,
                                    bbox.Max.X + 0.02,
                                    bbox.Max.Y + 0.02
                                ));
                            }
                        }
                    }
                    catch { }
                }

                // Grid-based search for empty space
                var emptySpaces = new List<object>();
                double gridStep = 0.1; // 1.2" grid
                double margin = 0.1; // Stay away from edges

                // Define search area based on preferred zone
                double searchMinX = sheetMinX + margin;
                double searchMinY = sheetMinY + margin;
                double searchMaxX = sheetMaxX - margin;
                double searchMaxY = sheetMaxY - margin;

                if (preferredZone == "notesArea")
                {
                    searchMinX = sheetMaxX - 0.8;
                    searchMinY = (sheetMaxY - sheetMinY) * 0.3 + sheetMinY;
                }
                else if (preferredZone == "legendArea")
                {
                    searchMaxX = sheetMinX + 0.8;
                    searchMaxY = (sheetMaxY - sheetMinY) * 0.5 + sheetMinY;
                }

                // Scan for empty rectangles
                for (double y = searchMaxY - requiredHeight; y >= searchMinY; y -= gridStep)
                {
                    for (double x = searchMinX; x <= searchMaxX - requiredWidth; x += gridStep)
                    {
                        // Check if this rectangle is clear
                        bool isOccupied = false;
                        foreach (var rect in occupiedRects)
                        {
                            if (RectsOverlap(x, y, x + requiredWidth, y + requiredHeight,
                                           rect.Item1, rect.Item2, rect.Item3, rect.Item4))
                            {
                                isOccupied = true;
                                break;
                            }
                        }

                        if (!isOccupied)
                        {
                            emptySpaces.Add(new
                            {
                                x = x,
                                y = y,
                                width = requiredWidth,
                                height = requiredHeight,
                                centerX = x + requiredWidth / 2,
                                centerY = y + requiredHeight / 2
                            });

                            // Limit results
                            if (emptySpaces.Count >= 20) break;
                        }
                    }
                    if (emptySpaces.Count >= 20) break;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = sheetId,
                    requestedSize = new { width = requiredWidth, height = requiredHeight },
                    preferredZone = preferredZone,
                    emptySpacesFound = emptySpaces.Count,
                    emptySpaces = emptySpaces,
                    recommendation = emptySpaces.Count > 0 ? emptySpaces[0] : null,
                    units = "feet"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check if placing an element at a location would cause overlap
        /// Returns detailed overlap information
        /// </summary>
        public static string CheckForOverlaps(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                int viewId = parameters["viewId"].Value<int>();
                double proposedX = parameters["x"]?.Value<double>() ?? 0;
                double proposedY = parameters["y"]?.Value<double>() ?? 0;
                double proposedWidth = parameters["width"]?.Value<double>() ?? 0.2;
                double proposedHeight = parameters["height"]?.Value<double>() ?? 0.1;
                double buffer = parameters["buffer"]?.Value<double>() ?? 0.02; // ~1/4" buffer

                View view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"View {viewId} not found" });
                }

                // Define proposed bounds
                double propMinX = proposedX - proposedWidth / 2 - buffer;
                double propMinY = proposedY - proposedHeight / 2 - buffer;
                double propMaxX = proposedX + proposedWidth / 2 + buffer;
                double propMaxY = proposedY + proposedHeight / 2 + buffer;

                var overlaps = new List<object>();

                // Check against all annotations in view
                var annotationCategories = new[] {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_GenericAnnotation,
                    BuiltInCategory.OST_KeynoteTags,
                    BuiltInCategory.OST_Dimensions,
                    BuiltInCategory.OST_DoorTags,
                    BuiltInCategory.OST_WindowTags,
                    BuiltInCategory.OST_RoomTags
                };

                foreach (var cat in annotationCategories)
                {
                    try
                    {
                        var collector = new FilteredElementCollector(doc, view.Id)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(view);
                            if (bbox == null) continue;

                            if (RectsOverlap(propMinX, propMinY, propMaxX, propMaxY,
                                           bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y))
                            {
                                overlaps.Add(new
                                {
                                    elementId = elem.Id.Value,
                                    category = cat.ToString().Replace("OST_", ""),
                                    bounds = new
                                    {
                                        minX = bbox.Min.X,
                                        minY = bbox.Min.Y,
                                        maxX = bbox.Max.X,
                                        maxY = bbox.Max.Y
                                    }
                                });
                            }
                        }
                    }
                    catch { }
                }

                bool hasOverlap = overlaps.Count > 0;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasOverlap = hasOverlap,
                    proposedLocation = new { x = proposedX, y = proposedY, width = proposedWidth, height = proposedHeight },
                    overlapCount = overlaps.Count,
                    overlappingElements = overlaps,
                    recommendation = hasOverlap ? "Choose a different location" : "Location is clear for placement"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all annotations within a specified rectangular region
        /// Useful for understanding what's in an area before placing
        /// </summary>
        public static string GetAnnotationsInRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                int viewId = parameters["viewId"].Value<int>();
                double minX = parameters["minX"]?.Value<double>() ?? 0;
                double minY = parameters["minY"]?.Value<double>() ?? 0;
                double maxX = parameters["maxX"]?.Value<double>() ?? 1;
                double maxY = parameters["maxY"]?.Value<double>() ?? 1;

                View view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"View {viewId} not found" });
                }

                var annotationsInRegion = new List<object>();

                var annotationCategories = new[] {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_GenericAnnotation,
                    BuiltInCategory.OST_KeynoteTags,
                    BuiltInCategory.OST_Dimensions,
                    BuiltInCategory.OST_DoorTags,
                    BuiltInCategory.OST_WindowTags,
                    BuiltInCategory.OST_RoomTags,
                    BuiltInCategory.OST_DetailComponents
                };

                foreach (var cat in annotationCategories)
                {
                    try
                    {
                        var collector = new FilteredElementCollector(doc, view.Id)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(view);
                            if (bbox == null) continue;

                            // Check if element's center is within region
                            double centerX = (bbox.Min.X + bbox.Max.X) / 2;
                            double centerY = (bbox.Min.Y + bbox.Max.Y) / 2;

                            if (centerX >= minX && centerX <= maxX && centerY >= minY && centerY <= maxY)
                            {
                                var info = new Dictionary<string, object>
                                {
                                    { "elementId", elem.Id.Value },
                                    { "category", cat.ToString().Replace("OST_", "") },
                                    { "familyName", GetFamilyName(elem) },
                                    { "center", new { x = centerX, y = centerY } },
                                    { "bounds", new {
                                        minX = bbox.Min.X,
                                        minY = bbox.Min.Y,
                                        maxX = bbox.Max.X,
                                        maxY = bbox.Max.Y
                                    }}
                                };

                                if (elem is TextNote tn)
                                {
                                    info["text"] = tn.Text;
                                }

                                annotationsInRegion.Add(info);
                            }
                        }
                    }
                    catch { }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId,
                    region = new { minX, minY, maxX, maxY },
                    annotationCount = annotationsInRegion.Count,
                    annotations = annotationsInRegion
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Phase 3: Smart Placement

        /// <summary>
        /// Place an annotation in a logical zone with automatic positioning
        /// Finds the best location within the zone that avoids overlaps
        /// </summary>
        public static string PlaceAnnotationInZone(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["zone"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId and zone are required" });
                }

                int viewId = parameters["viewId"].Value<int>();
                string zone = parameters["zone"].Value<string>(); // "topLeft", "topRight", "bottomLeft", "bottomRight", "center"
                string annotationType = parameters["annotationType"]?.Value<string>() ?? "text"; // "text", "keynote", "generic"
                string content = parameters["content"]?.Value<string>() ?? "";
                int? typeId = parameters["typeId"]?.Value<int>();
                double spacing = parameters["spacing"]?.Value<double>() ?? 0.08; // ~1" default spacing

                View view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"View {viewId} not found" });
                }

                // Get view bounds
                BoundingBoxXYZ viewBox = view.get_BoundingBox(null);
                if (viewBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Cannot determine view bounds" });
                }

                // Calculate zone bounds
                double viewWidth = viewBox.Max.X - viewBox.Min.X;
                double viewHeight = viewBox.Max.Y - viewBox.Min.Y;
                double zoneMinX, zoneMinY, zoneMaxX, zoneMaxY;

                switch (zone.ToLower())
                {
                    case "topleft":
                        zoneMinX = viewBox.Min.X;
                        zoneMaxX = viewBox.Min.X + viewWidth * 0.3;
                        zoneMinY = viewBox.Max.Y - viewHeight * 0.3;
                        zoneMaxY = viewBox.Max.Y;
                        break;
                    case "topright":
                        zoneMinX = viewBox.Max.X - viewWidth * 0.3;
                        zoneMaxX = viewBox.Max.X;
                        zoneMinY = viewBox.Max.Y - viewHeight * 0.3;
                        zoneMaxY = viewBox.Max.Y;
                        break;
                    case "bottomleft":
                        zoneMinX = viewBox.Min.X;
                        zoneMaxX = viewBox.Min.X + viewWidth * 0.3;
                        zoneMinY = viewBox.Min.Y;
                        zoneMaxY = viewBox.Min.Y + viewHeight * 0.3;
                        break;
                    case "bottomright":
                        zoneMinX = viewBox.Max.X - viewWidth * 0.3;
                        zoneMaxX = viewBox.Max.X;
                        zoneMinY = viewBox.Min.Y;
                        zoneMaxY = viewBox.Min.Y + viewHeight * 0.3;
                        break;
                    default: // center
                        zoneMinX = viewBox.Min.X + viewWidth * 0.35;
                        zoneMaxX = viewBox.Max.X - viewWidth * 0.35;
                        zoneMinY = viewBox.Min.Y + viewHeight * 0.35;
                        zoneMaxY = viewBox.Max.Y - viewHeight * 0.35;
                        break;
                }

                // Find first clear spot in zone (scan from top-left)
                double annotationWidth = 0.2; // Estimate
                double annotationHeight = 0.08; // Estimate
                XYZ placementPoint = null;

                for (double y = zoneMaxY - spacing; y >= zoneMinY + annotationHeight; y -= spacing)
                {
                    for (double x = zoneMinX + spacing; x <= zoneMaxX - annotationWidth; x += spacing)
                    {
                        // Quick overlap check
                        bool isClear = true;
                        var collector = new FilteredElementCollector(doc, view.Id)
                            .OfCategory(BuiltInCategory.OST_TextNotes)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(view);
                            if (bbox != null)
                            {
                                if (RectsOverlap(x - 0.02, y - 0.02, x + annotationWidth + 0.02, y + annotationHeight + 0.02,
                                               bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y))
                                {
                                    isClear = false;
                                    break;
                                }
                            }
                        }

                        if (isClear)
                        {
                            placementPoint = new XYZ(x, y, 0);
                            break;
                        }
                    }
                    if (placementPoint != null) break;
                }

                if (placementPoint == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No clear space found in zone",
                        zone = zone,
                        zoneBounds = new { minX = zoneMinX, minY = zoneMinY, maxX = zoneMaxX, maxY = zoneMaxY }
                    });
                }

                // Place the annotation
                ElementId placedElementId = null;

                using (Transaction trans = new Transaction(doc, "Place Annotation in Zone"))
                {
                    trans.Start();

                    if (annotationType == "text" && !string.IsNullOrEmpty(content))
                    {
                        // Get default text type or specified type
                        TextNoteType textType = null;
                        if (typeId.HasValue)
                        {
                            textType = doc.GetElement(new ElementId(typeId.Value)) as TextNoteType;
                        }
                        if (textType == null)
                        {
                            textType = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .FirstElement() as TextNoteType;
                        }

                        if (textType != null)
                        {
                            TextNote note = TextNote.Create(doc, view.Id, placementPoint, content, textType.Id);
                            placedElementId = note.Id;
                        }
                    }
                    else if (annotationType == "generic" && typeId.HasValue)
                    {
                        FamilySymbol symbol = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
                        if (symbol != null)
                        {
                            if (!symbol.IsActive) symbol.Activate();
                            FamilyInstance instance = doc.Create.NewFamilyInstance(placementPoint, symbol, view);
                            placedElementId = instance.Id;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = placedElementId?.Value,
                    zone = zone,
                    placementLocation = new { x = placementPoint.X, y = placementPoint.Y },
                    zoneBounds = new { minX = zoneMinX, minY = zoneMinY, maxX = zoneMaxX, maxY = zoneMaxY }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place an element relative to another element
        /// Positions: above, below, left, right, with specified offset
        /// </summary>
        public static string PlaceRelativeTo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["referenceElementId"] == null || parameters["position"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "referenceElementId and position are required" });
                }

                int referenceId = parameters["referenceElementId"].Value<int>();
                string position = parameters["position"].Value<string>(); // "above", "below", "left", "right"
                double offset = parameters["offset"]?.Value<double>() ?? 0.08; // ~1" default
                int? viewId = parameters["viewId"]?.Value<int>();
                string annotationType = parameters["annotationType"]?.Value<string>() ?? "text";
                string content = parameters["content"]?.Value<string>() ?? "";
                int? typeId = parameters["typeId"]?.Value<int>();

                Element refElement = doc.GetElement(new ElementId(referenceId));
                if (refElement == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Reference element {referenceId} not found" });
                }

                // Get view
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

                // Get reference element bounds
                BoundingBoxXYZ refBox = refElement.get_BoundingBox(view);
                if (refBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Reference element has no bounding box" });
                }

                double refCenterX = (refBox.Min.X + refBox.Max.X) / 2;
                double refCenterY = (refBox.Min.Y + refBox.Max.Y) / 2;
                double refWidth = refBox.Max.X - refBox.Min.X;
                double refHeight = refBox.Max.Y - refBox.Min.Y;

                // Calculate placement point
                XYZ placementPoint;
                switch (position.ToLower())
                {
                    case "above":
                        placementPoint = new XYZ(refCenterX, refBox.Max.Y + offset, 0);
                        break;
                    case "below":
                        placementPoint = new XYZ(refCenterX, refBox.Min.Y - offset, 0);
                        break;
                    case "left":
                        placementPoint = new XYZ(refBox.Min.X - offset, refCenterY, 0);
                        break;
                    case "right":
                        placementPoint = new XYZ(refBox.Max.X + offset, refCenterY, 0);
                        break;
                    default:
                        return JsonConvert.SerializeObject(new { success = false, error = $"Invalid position: {position}" });
                }

                // Place the element
                ElementId placedElementId = null;

                using (Transaction trans = new Transaction(doc, "Place Relative To"))
                {
                    trans.Start();

                    if (annotationType == "text" && !string.IsNullOrEmpty(content))
                    {
                        TextNoteType textType = null;
                        if (typeId.HasValue)
                        {
                            textType = doc.GetElement(new ElementId(typeId.Value)) as TextNoteType;
                        }
                        if (textType == null)
                        {
                            textType = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .FirstElement() as TextNoteType;
                        }

                        if (textType != null)
                        {
                            TextNote note = TextNote.Create(doc, view.Id, placementPoint, content, textType.Id);
                            placedElementId = note.Id;
                        }
                    }
                    else if (annotationType == "generic" && typeId.HasValue)
                    {
                        FamilySymbol symbol = doc.GetElement(new ElementId(typeId.Value)) as FamilySymbol;
                        if (symbol != null)
                        {
                            if (!symbol.IsActive) symbol.Activate();
                            FamilyInstance instance = doc.Create.NewFamilyInstance(placementPoint, symbol, view);
                            placedElementId = instance.Id;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = placedElementId?.Value,
                    referenceElementId = referenceId,
                    position = position,
                    offset = offset,
                    placementLocation = new { x = placementPoint.X, y = placementPoint.Y },
                    referenceElementBounds = new
                    {
                        minX = refBox.Min.X,
                        minY = refBox.Min.Y,
                        maxX = refBox.Max.X,
                        maxY = refBox.Max.Y
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Automatically arrange a group of annotations in a column or row with equal spacing
        /// Great for organizing keynote legends, note lists, etc.
        /// </summary>
        public static string AutoArrangeAnnotations(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds array is required" });
                }

                var elementIds = parameters["elementIds"].ToObject<List<int>>();
                string arrangement = parameters["arrangement"]?.Value<string>() ?? "column"; // "column" or "row"
                double spacing = parameters["spacing"]?.Value<double>() ?? 0.08; // ~1" default
                double? startX = parameters["startX"]?.Value<double>();
                double? startY = parameters["startY"]?.Value<double>();
                string alignment = parameters["alignment"]?.Value<string>() ?? "left"; // "left", "center", "right" for columns; "top", "center", "bottom" for rows

                if (elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No element IDs provided" });
                }

                // Get elements and their bounds
                var elementsWithBounds = new List<Tuple<Element, BoundingBoxXYZ>>();
                View view = doc.ActiveView;

                foreach (int id in elementIds)
                {
                    Element elem = doc.GetElement(new ElementId(id));
                    if (elem == null) continue;

                    BoundingBoxXYZ bbox = elem.get_BoundingBox(view);
                    if (bbox != null)
                    {
                        elementsWithBounds.Add(Tuple.Create(elem, bbox));
                    }
                }

                if (elementsWithBounds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid elements found" });
                }

                // Determine starting position from first element if not specified
                if (!startX.HasValue || !startY.HasValue)
                {
                    var firstBbox = elementsWithBounds[0].Item2;
                    startX = startX ?? firstBbox.Min.X;
                    startY = startY ?? firstBbox.Max.Y;
                }

                // Calculate new positions
                var movedElements = new List<object>();
                double currentX = startX.Value;
                double currentY = startY.Value;

                using (Transaction trans = new Transaction(doc, "Auto Arrange Annotations"))
                {
                    trans.Start();

                    foreach (var tuple in elementsWithBounds)
                    {
                        Element elem = tuple.Item1;
                        BoundingBoxXYZ bbox = tuple.Item2;
                        double elemWidth = bbox.Max.X - bbox.Min.X;
                        double elemHeight = bbox.Max.Y - bbox.Min.Y;

                        // Calculate target position
                        XYZ targetPoint;
                        if (arrangement == "column")
                        {
                            double targetX = currentX;
                            if (alignment == "center")
                                targetX = currentX + elemWidth / 2;
                            else if (alignment == "right")
                                targetX = currentX + elemWidth;

                            targetPoint = new XYZ(targetX, currentY - elemHeight / 2, 0);
                            currentY -= elemHeight + spacing;
                        }
                        else // row
                        {
                            double targetY = currentY;
                            if (alignment == "center")
                                targetY = currentY - elemHeight / 2;
                            else if (alignment == "bottom")
                                targetY = currentY - elemHeight;

                            targetPoint = new XYZ(currentX + elemWidth / 2, targetY, 0);
                            currentX += elemWidth + spacing;
                        }

                        // Move element
                        if (elem.Location is LocationPoint locPt)
                        {
                            XYZ currentLoc = locPt.Point;
                            XYZ translation = targetPoint - currentLoc;
                            ElementTransformUtils.MoveElement(doc, elem.Id, translation);

                            movedElements.Add(new
                            {
                                elementId = elem.Id.Value,
                                oldLocation = new { x = currentLoc.X, y = currentLoc.Y },
                                newLocation = new { x = targetPoint.X, y = targetPoint.Y }
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    arrangement = arrangement,
                    alignment = alignment,
                    spacing = spacing,
                    startPosition = new { x = startX, y = startY },
                    elementsArranged = movedElements.Count,
                    elements = movedElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Suggest optimal placement location for an annotation near a model element
        /// Considers existing annotations and finds clear space
        /// </summary>
        public static string SuggestPlacementLocation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["targetElementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "targetElementId is required" });
                }

                int targetId = parameters["targetElementId"].Value<int>();
                int? viewId = parameters["viewId"]?.Value<int>();
                double annotationWidth = parameters["annotationWidth"]?.Value<double>() ?? 0.15;
                double annotationHeight = parameters["annotationHeight"]?.Value<double>() ?? 0.08;
                double preferredDistance = parameters["preferredDistance"]?.Value<double>() ?? 0.1;

                Element targetElement = doc.GetElement(new ElementId(targetId));
                if (targetElement == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Target element {targetId} not found" });
                }

                View view = viewId.HasValue ? doc.GetElement(new ElementId(viewId.Value)) as View : doc.ActiveView;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid view" });
                }

                BoundingBoxXYZ targetBox = targetElement.get_BoundingBox(view);
                if (targetBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Target element has no bounding box" });
                }

                double targetCenterX = (targetBox.Min.X + targetBox.Max.X) / 2;
                double targetCenterY = (targetBox.Min.Y + targetBox.Max.Y) / 2;

                // Define candidate positions around the target (8 directions)
                var candidates = new List<Tuple<string, double, double>>
                {
                    Tuple.Create("above", targetCenterX, targetBox.Max.Y + preferredDistance),
                    Tuple.Create("below", targetCenterX, targetBox.Min.Y - preferredDistance - annotationHeight),
                    Tuple.Create("right", targetBox.Max.X + preferredDistance, targetCenterY),
                    Tuple.Create("left", targetBox.Min.X - preferredDistance - annotationWidth, targetCenterY),
                    Tuple.Create("above-right", targetBox.Max.X + preferredDistance * 0.7, targetBox.Max.Y + preferredDistance * 0.7),
                    Tuple.Create("above-left", targetBox.Min.X - preferredDistance * 0.7 - annotationWidth, targetBox.Max.Y + preferredDistance * 0.7),
                    Tuple.Create("below-right", targetBox.Max.X + preferredDistance * 0.7, targetBox.Min.Y - preferredDistance * 0.7 - annotationHeight),
                    Tuple.Create("below-left", targetBox.Min.X - preferredDistance * 0.7 - annotationWidth, targetBox.Min.Y - preferredDistance * 0.7 - annotationHeight)
                };

                // Collect existing annotation bounds
                var existingBounds = new List<Tuple<double, double, double, double>>();
                var annotationCategories = new[] {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_GenericAnnotation,
                    BuiltInCategory.OST_KeynoteTags,
                    BuiltInCategory.OST_DoorTags,
                    BuiltInCategory.OST_WindowTags,
                    BuiltInCategory.OST_RoomTags
                };

                foreach (var cat in annotationCategories)
                {
                    try
                    {
                        var collector = new FilteredElementCollector(doc, view.Id)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            BoundingBoxXYZ bbox = elem.get_BoundingBox(view);
                            if (bbox != null)
                            {
                                existingBounds.Add(Tuple.Create(bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y));
                            }
                        }
                    }
                    catch { }
                }

                // Evaluate each candidate
                var evaluatedCandidates = new List<object>();
                object bestCandidate = null;

                foreach (var candidate in candidates)
                {
                    string direction = candidate.Item1;
                    double x = candidate.Item2;
                    double y = candidate.Item3;

                    // Check for overlaps
                    bool hasOverlap = false;
                    foreach (var existing in existingBounds)
                    {
                        if (RectsOverlap(x, y, x + annotationWidth, y + annotationHeight,
                                       existing.Item1 - 0.02, existing.Item2 - 0.02, existing.Item3 + 0.02, existing.Item4 + 0.02))
                        {
                            hasOverlap = true;
                            break;
                        }
                    }

                    var candidateInfo = new
                    {
                        direction = direction,
                        x = x,
                        y = y,
                        hasOverlap = hasOverlap,
                        distanceFromTarget = Math.Sqrt(Math.Pow(x + annotationWidth / 2 - targetCenterX, 2) +
                                                       Math.Pow(y + annotationHeight / 2 - targetCenterY, 2))
                    };

                    evaluatedCandidates.Add(candidateInfo);

                    if (!hasOverlap && bestCandidate == null)
                    {
                        bestCandidate = candidateInfo;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    targetElementId = targetId,
                    targetBounds = new
                    {
                        minX = targetBox.Min.X,
                        minY = targetBox.Min.Y,
                        maxX = targetBox.Max.X,
                        maxY = targetBox.Max.Y,
                        centerX = targetCenterX,
                        centerY = targetCenterY
                    },
                    annotationSize = new { width = annotationWidth, height = annotationHeight },
                    recommendation = bestCandidate,
                    allCandidates = evaluatedCandidates,
                    existingAnnotationCount = existingBounds.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static bool RectsOverlap(double ax1, double ay1, double ax2, double ay2,
                                         double bx1, double by1, double bx2, double by2)
        {
            return !(ax2 < bx1 || ax1 > bx2 || ay2 < by1 || ay1 > by2);
        }

        private static string GetFamilyName(Element elem)
        {
            if (elem is FamilyInstance fi)
            {
                return fi.Symbol?.Family?.Name ?? "Unknown";
            }
            return elem.GetType().Name;
        }

        private static string GetTypeName(Element elem)
        {
            if (elem is FamilyInstance fi)
            {
                return fi.Symbol?.Name ?? "Unknown";
            }
            ElementId typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                Element typeElem = elem.Document.GetElement(typeId);
                return typeElem?.Name ?? "Unknown";
            }
            return "Unknown";
        }

        #endregion
    }
}
