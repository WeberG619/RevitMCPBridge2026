using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Enhanced Vision Methods - Enable AI to see and analyze the Revit model.
    /// Provides screenshots, element visibility control, and view analysis.
    /// </summary>
    public static class VisionMethods
    {
        #region Export View Image

        /// <summary>
        /// Export the current view as an image.
        /// </summary>
        public static string ExportViewImage(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var uidoc = uiApp.ActiveUIDocument;
                var view = uidoc.ActiveView;

                var outputPath = parameters["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(outputPath))
                {
                    outputPath = Path.Combine(Path.GetTempPath(), $"revit_view_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                }

                var pixelSize = parameters["pixelSize"]?.Value<int>() ?? 1920;
                var imageFormat = parameters["format"]?.ToString()?.ToLower() ?? "png";

                // Configure export options
                var options = new ImageExportOptions
                {
                    FilePath = outputPath,
                    PixelSize = pixelSize,
                    HLRandWFViewsFileType = imageFormat == "jpg" ? ImageFileType.JPEGMedium : ImageFileType.PNG,
                    ShadowViewsFileType = imageFormat == "jpg" ? ImageFileType.JPEGMedium : ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_300,
                    ExportRange = ExportRange.CurrentView
                };

                // Export
                doc.ExportImage(options);

                // The actual file gets a suffix, find it
                var directory = Path.GetDirectoryName(outputPath);
                var baseName = Path.GetFileNameWithoutExtension(outputPath);
                var exportedFile = Directory.GetFiles(directory, $"{baseName}*")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .FirstOrDefault();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputPath = exportedFile ?? outputPath,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    pixelSize = pixelSize,
                    message = $"View '{view.Name}' exported to image"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting view image");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get View Extents

        /// <summary>
        /// Get the 2D/3D extents of the active view.
        /// </summary>
        public static string GetViewExtents(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uidoc.ActiveView;
                var doc = uidoc.Document;

                // Get crop box extents
                BoundingBoxXYZ cropBox = null;
                if (view.CropBoxActive)
                {
                    cropBox = view.CropBox;
                }

                // Get model extent by finding bounding box of all elements
                var outline = GetModelExtent(doc, view);

                // Get visible zoom extents from UI
                var uiViews = uidoc.GetOpenUIViews();
                var activeUIView = uiViews.FirstOrDefault(uv => uv.ViewId == view.Id);
                IList<XYZ> zoomCorners = null;
                if (activeUIView != null)
                {
                    zoomCorners = activeUIView.GetZoomCorners();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)view.Id.Value,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    cropBoxActive = view.CropBoxActive,
                    cropBox = cropBox != null ? new
                    {
                        min = new { x = cropBox.Min.X, y = cropBox.Min.Y, z = cropBox.Min.Z },
                        max = new { x = cropBox.Max.X, y = cropBox.Max.Y, z = cropBox.Max.Z }
                    } : null,
                    modelExtent = outline != null ? new
                    {
                        min = new { x = outline.MinimumPoint.X, y = outline.MinimumPoint.Y, z = outline.MinimumPoint.Z },
                        max = new { x = outline.MaximumPoint.X, y = outline.MaximumPoint.Y, z = outline.MaximumPoint.Z }
                    } : null,
                    zoomExtent = zoomCorners != null && zoomCorners.Count >= 2 ? new
                    {
                        corner1 = new { x = zoomCorners[0].X, y = zoomCorners[0].Y, z = zoomCorners[0].Z },
                        corner2 = new { x = zoomCorners[1].X, y = zoomCorners[1].Y, z = zoomCorners[1].Z }
                    } : null
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting view extents");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Element Visibility

        /// <summary>
        /// Hide or show specific elements in the current view.
        /// </summary>
        public static string SetElementVisibility(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView;
                var elementIds = parameters["elementIds"] as JArray;
                var hide = parameters["hide"]?.Value<bool>() ?? true;

                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds array required" });
                }

                var ids = elementIds.Select(id => new ElementId(id.Value<int>())).ToList();

                using (var trans = new Transaction(doc, hide ? "Hide Elements" : "Show Elements"))
                {
                    trans.Start();

                    if (hide)
                    {
                        view.HideElements(ids);
                    }
                    else
                    {
                        view.UnhideElements(ids);
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    action = hide ? "hidden" : "shown",
                    elementCount = ids.Count,
                    viewName = view.Name,
                    message = $"{ids.Count} elements {(hide ? "hidden" : "shown")} in view"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting element visibility");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Category Visibility

        /// <summary>
        /// Hide or show an entire category in the current view.
        /// </summary>
        public static string SetCategoryVisibility(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView;
                var categoryName = parameters["category"]?.ToString();
                var visible = parameters["visible"]?.Value<bool>() ?? true;

                if (string.IsNullOrEmpty(categoryName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "category name required" });
                }

                // Find category
                Category category = null;
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        category = cat;
                        break;
                    }
                }

                if (category == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Category '{categoryName}' not found" });
                }

                using (var trans = new Transaction(doc, "Set Category Visibility"))
                {
                    trans.Start();

                    view.SetCategoryHidden(category.Id, !visible);

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = categoryName,
                    visible = visible,
                    viewName = view.Name,
                    message = $"Category '{categoryName}' set to {(visible ? "visible" : "hidden")}"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting category visibility");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Visible Elements

        /// <summary>
        /// Get all elements visible in the current view.
        /// </summary>
        public static string GetVisibleElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView;
                var categoryFilter = parameters["category"]?.ToString();
                var limit = parameters["limit"]?.Value<int>() ?? 100;

                var collector = new FilteredElementCollector(doc, view.Id);

                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    // Find category
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat.Name.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            collector = collector.OfCategoryId(cat.Id);
                            break;
                        }
                    }
                }

                var elements = collector
                    .WhereElementIsNotElementType()
                    .Take(limit)
                    .Select(e => new
                    {
                        id = (int)e.Id.Value,
                        name = e.Name,
                        category = e.Category?.Name ?? "Unknown",
                        location = GetElementCenterPoint(e)
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewName = view.Name,
                    elementCount = elements.Count,
                    limitApplied = limit,
                    elements = elements
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting visible elements");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Graphics Override

        /// <summary>
        /// Set graphics override for elements in the current view.
        /// </summary>
        public static string SetGraphicsOverride(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView;
                var elementIds = parameters["elementIds"] as JArray;

                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds array required" });
                }

                var ids = elementIds.Select(id => new ElementId(id.Value<int>())).ToList();

                // Parse color
                var colorR = parameters["colorR"]?.Value<byte>() ?? 255;
                var colorG = parameters["colorG"]?.Value<byte>() ?? 0;
                var colorB = parameters["colorB"]?.Value<byte>() ?? 0;
                var transparency = parameters["transparency"]?.Value<int>() ?? 0;
                var clear = parameters["clear"]?.Value<bool>() ?? false;

                using (var trans = new Transaction(doc, "Set Graphics Override"))
                {
                    trans.Start();

                    var overrides = new OverrideGraphicSettings();

                    if (!clear)
                    {
                        var color = new Autodesk.Revit.DB.Color(colorR, colorG, colorB);
                        overrides.SetSurfaceForegroundPatternColor(color);
                        overrides.SetProjectionLineColor(color);

                        if (transparency > 0 && transparency <= 100)
                        {
                            overrides.SetSurfaceTransparency(transparency);
                        }
                    }

                    foreach (var id in ids)
                    {
                        view.SetElementOverrides(id, overrides);
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementCount = ids.Count,
                    color = clear ? null : new { r = colorR, g = colorG, b = colorB },
                    transparency = transparency,
                    cleared = clear,
                    viewName = view.Name,
                    message = clear ? "Graphics overrides cleared" : $"Graphics overrides applied to {ids.Count} elements"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting graphics override");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get View Range

        /// <summary>
        /// Get the view range settings for a plan view.
        /// </summary>
        public static string GetViewRange(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView as ViewPlan;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active view is not a plan view"
                    });
                }

                var viewRange = view.GetViewRange();

                // Get level elevations
                var level = doc.GetElement(view.GenLevel.Id) as Level;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewName = view.Name,
                    associatedLevel = level?.Name,
                    levelElevation = level?.Elevation,
                    viewRange = new
                    {
                        topClipPlane = viewRange.GetOffset(PlanViewPlane.TopClipPlane),
                        cutPlane = viewRange.GetOffset(PlanViewPlane.CutPlane),
                        bottomClipPlane = viewRange.GetOffset(PlanViewPlane.BottomClipPlane),
                        viewDepth = viewRange.GetOffset(PlanViewPlane.ViewDepthPlane)
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting view range");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set View Range

        /// <summary>
        /// Set the view range for a plan view.
        /// </summary>
        public static string SetViewRange(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView as ViewPlan;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Active view is not a plan view" });
                }

                var topOffset = parameters["topOffset"]?.Value<double>();
                var cutPlaneOffset = parameters["cutPlaneOffset"]?.Value<double>();
                var bottomOffset = parameters["bottomOffset"]?.Value<double>();
                var viewDepthOffset = parameters["viewDepthOffset"]?.Value<double>();

                using (var trans = new Transaction(doc, "Set View Range"))
                {
                    trans.Start();

                    var viewRange = view.GetViewRange();

                    if (topOffset.HasValue)
                        viewRange.SetOffset(PlanViewPlane.TopClipPlane, topOffset.Value);

                    if (cutPlaneOffset.HasValue)
                        viewRange.SetOffset(PlanViewPlane.CutPlane, cutPlaneOffset.Value);

                    if (bottomOffset.HasValue)
                        viewRange.SetOffset(PlanViewPlane.BottomClipPlane, bottomOffset.Value);

                    if (viewDepthOffset.HasValue)
                        viewRange.SetOffset(PlanViewPlane.ViewDepthPlane, viewDepthOffset.Value);

                    view.SetViewRange(viewRange);

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewName = view.Name,
                    message = "View range updated"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting view range");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Isolate Elements

        /// <summary>
        /// Isolate specific elements in the view (hide everything else).
        /// </summary>
        public static string IsolateElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView;
                var elementIds = parameters["elementIds"] as JArray;
                var reset = parameters["reset"]?.Value<bool>() ?? false;

                if (reset)
                {
                    using (var trans = new Transaction(doc, "Reset Isolation"))
                    {
                        trans.Start();
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        trans.Commit();
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        action = "reset",
                        message = "View isolation reset"
                    });
                }

                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds array required (or set reset=true)" });
                }

                var ids = elementIds.Select(id => new ElementId(id.Value<int>())).ToList();

                using (var trans = new Transaction(doc, "Isolate Elements"))
                {
                    trans.Start();
                    view.IsolateElementsTemporary(ids);
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    action = "isolate",
                    elementCount = ids.Count,
                    viewName = view.Name,
                    message = $"{ids.Count} elements isolated"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error isolating elements");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set 3D Section Box

        /// <summary>
        /// Set or clear the section box on a 3D view.
        /// </summary>
        public static string Set3DSectionBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uiApp.ActiveUIDocument.ActiveView as View3D;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Active view is not a 3D view" });
                }

                var disable = parameters["disable"]?.Value<bool>() ?? false;

                using (var trans = new Transaction(doc, "Set Section Box"))
                {
                    trans.Start();

                    if (disable)
                    {
                        view.IsSectionBoxActive = false;
                    }
                    else
                    {
                        var minX = parameters["minX"]?.Value<double>() ?? -100;
                        var minY = parameters["minY"]?.Value<double>() ?? -100;
                        var minZ = parameters["minZ"]?.Value<double>() ?? -10;
                        var maxX = parameters["maxX"]?.Value<double>() ?? 100;
                        var maxY = parameters["maxY"]?.Value<double>() ?? 100;
                        var maxZ = parameters["maxZ"]?.Value<double>() ?? 100;

                        var sectionBox = new BoundingBoxXYZ
                        {
                            Min = new XYZ(minX, minY, minZ),
                            Max = new XYZ(maxX, maxY, maxZ)
                        };

                        view.SetSectionBox(sectionBox);
                        view.IsSectionBoxActive = true;
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sectionBoxActive = !disable,
                    viewName = view.Name,
                    message = disable ? "Section box disabled" : "Section box set"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting section box");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static Outline GetModelExtent(Document doc, View view)
        {
            try
            {
                var collector = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType();

                var bb = collector
                    .Select(e => e.get_BoundingBox(view))
                    .Where(b => b != null)
                    .ToList();

                if (bb.Count == 0) return null;

                var minX = bb.Min(b => b.Min.X);
                var minY = bb.Min(b => b.Min.Y);
                var minZ = bb.Min(b => b.Min.Z);
                var maxX = bb.Max(b => b.Max.X);
                var maxY = bb.Max(b => b.Max.Y);
                var maxZ = bb.Max(b => b.Max.Z);

                return new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
            }
            catch
            {
                return null;
            }
        }

        private static object GetElementCenterPoint(Element element)
        {
            try
            {
                var location = element.Location;
                if (location is LocationPoint lp)
                {
                    return new { x = lp.Point.X, y = lp.Point.Y, z = lp.Point.Z };
                }
                else if (location is LocationCurve lc)
                {
                    var midpoint = lc.Curve.Evaluate(0.5, true);
                    return new { x = midpoint.X, y = midpoint.Y, z = midpoint.Z };
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Spatial Query Methods

        /// <summary>
        /// Find all elements near a given XY point within a specified radius.
        /// Used to map markup locations to Revit model elements.
        /// </summary>
        public static string FindElementsNearPoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var x = parameters["x"]?.Value<double>();
                var y = parameters["y"]?.Value<double>();
                if (x == null || y == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "x and y coordinates are required" });

                var z = parameters["z"]?.Value<double>() ?? 0;
                var radius = parameters["radius"]?.Value<double>() ?? 3.0; // 3 feet default
                var center = new XYZ(x.Value, y.Value, z);

                // Optional category filter
                var categoryFilter = parameters["category"]?.ToString();
                var viewId = parameters["viewId"]?.Value<int>();
                var maxResults = parameters["maxResults"]?.Value<int>() ?? 20;

                // Build collector - optionally scoped to a view
                FilteredElementCollector collector;
                if (viewId.HasValue)
                {
                    collector = new FilteredElementCollector(doc, new ElementId(viewId.Value));
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                // Apply category filter if specified
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    BuiltInCategory bic;
                    if (Enum.TryParse("OST_" + categoryFilter, out bic))
                    {
                        collector = collector.OfCategory(bic);
                    }
                }

                // Only non-type elements
                collector = collector.WhereElementIsNotElementType();

                // Use bounding box filter for rough spatial query
                var min = new XYZ(x.Value - radius, y.Value - radius, z - 20);
                var max = new XYZ(x.Value + radius, y.Value + radius, z + 20);
                var outline = new Outline(min, max);
                var bbFilter = new BoundingBoxIntersectsFilter(outline);
                collector = collector.WherePasses(bbFilter);

                var results = new List<object>();

                foreach (var element in collector)
                {
                    // Skip non-visible categories
                    if (element.Category == null) continue;

                    // Get element location
                    double elemX = 0, elemY = 0, elemZ = 0;
                    double distance = double.MaxValue;
                    var location = element.Location;

                    if (location is LocationPoint lp)
                    {
                        elemX = lp.Point.X;
                        elemY = lp.Point.Y;
                        elemZ = lp.Point.Z;
                        distance = center.DistanceTo(lp.Point);
                    }
                    else if (location is LocationCurve lc)
                    {
                        var midpoint = lc.Curve.Evaluate(0.5, true);
                        elemX = midpoint.X;
                        elemY = midpoint.Y;
                        elemZ = midpoint.Z;
                        // Distance to nearest point on curve
                        var result = lc.Curve.Project(center);
                        if (result != null)
                            distance = result.Distance;
                        else
                            distance = center.DistanceTo(midpoint);
                    }
                    else
                    {
                        // Use bounding box center
                        var bbox = element.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            var bboxCenter = (bbox.Min + bbox.Max) / 2;
                            elemX = bboxCenter.X;
                            elemY = bboxCenter.Y;
                            elemZ = bboxCenter.Z;
                            distance = center.DistanceTo(bboxCenter);
                        }
                        else continue;
                    }

                    if (distance > radius) continue;

                    var elemInfo = new Dictionary<string, object>
                    {
                        ["id"] = (int)element.Id.Value,
                        ["category"] = element.Category.Name,
                        ["name"] = element.Name,
                        ["distance"] = Math.Round(distance, 3),
                        ["location"] = new { x = Math.Round(elemX, 3), y = Math.Round(elemY, 3), z = Math.Round(elemZ, 3) }
                    };

                    // Add type-specific info
                    if (element is FamilyInstance fi)
                    {
                        elemInfo["familyName"] = fi.Symbol?.Family?.Name ?? "";
                        elemInfo["typeName"] = fi.Symbol?.Name ?? "";
                        var mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                        if (!string.IsNullOrEmpty(mark)) elemInfo["mark"] = mark;
                    }
                    else if (element is Wall wall)
                    {
                        elemInfo["wallType"] = wall.WallType?.Name ?? "";
                        elemInfo["length"] = Math.Round(wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0, 2);
                    }
                    else if (element is Room room)
                    {
                        elemInfo["roomName"] = room.Name;
                        elemInfo["roomNumber"] = room.Number;
                        elemInfo["area"] = Math.Round(room.Area, 1);
                    }

                    results.Add(elemInfo);
                }

                // Sort by distance, limit results
                var sorted = results
                    .OrderBy(r => ((dynamic)r)["distance"])
                    .Take(maxResults)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    searchPoint = new { x = x.Value, y = y.Value, z },
                    radius,
                    count = sorted.Count,
                    elements = sorted
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get view coordinate system info needed for PDF-to-Revit coordinate mapping.
        /// Returns crop region bounds, scale, and coordinate transform.
        /// </summary>
        public static string GetViewCoordinateInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>();

                View view;
                if (viewId.HasValue)
                {
                    view = doc.GetElement(new ElementId(viewId.Value)) as View;
                    if (view == null)
                        return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }
                else
                {
                    view = doc.ActiveView;
                }

                var result = new Dictionary<string, object>
                {
                    ["viewId"] = (int)view.Id.Value,
                    ["viewName"] = view.Name,
                    ["viewType"] = view.ViewType.ToString(),
                    ["scale"] = view.Scale
                };

                // Crop region
                if (view.CropBoxActive)
                {
                    var cropBox = view.CropBox;
                    result["cropBox"] = new
                    {
                        min = new { x = Math.Round(cropBox.Min.X, 4), y = Math.Round(cropBox.Min.Y, 4), z = Math.Round(cropBox.Min.Z, 4) },
                        max = new { x = Math.Round(cropBox.Max.X, 4), y = Math.Round(cropBox.Max.Y, 4), z = Math.Round(cropBox.Max.Z, 4) },
                        width = Math.Round(cropBox.Max.X - cropBox.Min.X, 4),
                        height = Math.Round(cropBox.Max.Y - cropBox.Min.Y, 4)
                    };

                    // Transform (view to model coordinates)
                    var transform = cropBox.Transform;
                    result["transform"] = new
                    {
                        origin = new { x = Math.Round(transform.Origin.X, 4), y = Math.Round(transform.Origin.Y, 4), z = Math.Round(transform.Origin.Z, 4) },
                        basisX = new { x = Math.Round(transform.BasisX.X, 6), y = Math.Round(transform.BasisX.Y, 6), z = Math.Round(transform.BasisX.Z, 6) },
                        basisY = new { x = Math.Round(transform.BasisY.X, 6), y = Math.Round(transform.BasisY.Y, 6), z = Math.Round(transform.BasisY.Z, 6) }
                    };
                }

                // View outline (screen-space bounds)
                var outline = view.Outline;
                result["outline"] = new
                {
                    min = new { u = Math.Round(outline.Min.U, 4), v = Math.Round(outline.Min.V, 4) },
                    max = new { u = Math.Round(outline.Max.U, 4), v = Math.Round(outline.Max.V, 4) }
                };

                // Level info for floor plans
                if (view is ViewPlan viewPlan)
                {
                    var level = viewPlan.GenLevel;
                    if (level != null)
                    {
                        result["level"] = new
                        {
                            name = level.Name,
                            elevation = Math.Round(level.Elevation, 4)
                        };
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewInfo = result
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
