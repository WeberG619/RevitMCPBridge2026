using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Selection Bridge Methods - Enable interactive element selection between user and AI.
    /// Allows Claude to receive user selections and programmatically select elements.
    /// </summary>
    public static class SelectionMethods
    {
        // Store for pending selection requests
        private static TaskCompletionSource<List<ElementId>> _selectionWaiter;
        private static int _expectedSelectionCount = 0;
        private static CancellationTokenSource _selectionCts;

        #region Get Current Selection

        /// <summary>
        /// Get currently selected elements in Revit.
        /// </summary>
        [MCPMethod("getSelection", Category = "Selection", Description = "Get currently selected elements in Revit")]
        public static string GetSelection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var selection = uidoc.Selection;
                var selectedIds = selection.GetElementIds();

                var elements = new List<object>();
                foreach (var id in selectedIds)
                {
                    var element = doc.GetElement(id);
                    if (element != null)
                    {
                        elements.Add(new
                        {
                            id = id.Value,
                            name = element.Name,
                            category = element.Category?.Name ?? "Unknown",
                            typeName = GetElementTypeName(element),
                            levelId = GetElementLevelId(element),
                            location = GetElementLocation(element)
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = elements.Count,
                    elements = elements,
                    message = elements.Count == 0 ? "No elements selected" : $"{elements.Count} element(s) selected"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting selection");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Wait For Selection

        /// <summary>
        /// Prompt user to select elements and wait for selection.
        /// Returns when user completes selection or timeout occurs.
        /// </summary>
        [MCPMethod("waitForSelection", Category = "Selection", Description = "Prompt user to select elements and wait for selection")]
        public static string WaitForSelection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                // Parameters
                var count = parameters["count"]?.Value<int>() ?? 0; // 0 = any number
                var prompt = parameters["prompt"]?.ToString() ?? "Select elements";
                var timeoutSeconds = parameters["timeoutSeconds"]?.Value<int>() ?? 60;
                var categoryFilter = parameters["category"]?.ToString();
                var allowMultiple = parameters["allowMultiple"]?.Value<bool>() ?? true;

                var doc = uidoc.Document;
                var selection = uidoc.Selection;

                // Create selection filter if category specified
                ISelectionFilter filter = null;
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    filter = new CategorySelectionFilter(doc, categoryFilter);
                }

                List<Reference> refs = new List<Reference>();

                try
                {
                    if (allowMultiple)
                    {
                        // Pick multiple elements
                        if (filter != null)
                        {
                            refs = selection.PickObjects(ObjectType.Element, filter, prompt).ToList();
                        }
                        else
                        {
                            refs = selection.PickObjects(ObjectType.Element, prompt).ToList();
                        }
                    }
                    else
                    {
                        // Pick single element
                        Reference r;
                        if (filter != null)
                        {
                            r = selection.PickObject(ObjectType.Element, filter, prompt);
                        }
                        else
                        {
                            r = selection.PickObject(ObjectType.Element, prompt);
                        }
                        if (r != null) refs.Add(r);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cancelled = true,
                        count = 0,
                        elements = new List<object>(),
                        message = "Selection cancelled by user"
                    });
                }

                // Build response with selected elements
                var elements = new List<object>();
                foreach (var r in refs)
                {
                    var element = doc.GetElement(r.ElementId);
                    if (element != null)
                    {
                        elements.Add(new
                        {
                            id = r.ElementId.Value,
                            name = element.Name,
                            category = element.Category?.Name ?? "Unknown",
                            typeName = GetElementTypeName(element),
                            levelId = GetElementLevelId(element),
                            location = GetElementLocation(element)
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    cancelled = false,
                    count = elements.Count,
                    elements = elements,
                    message = $"User selected {elements.Count} element(s)"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error waiting for selection");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Select Elements Programmatically

        /// <summary>
        /// Select elements programmatically by their IDs.
        /// </summary>
        [MCPMethod("selectElements", Category = "Selection", Description = "Select elements programmatically by their IDs")]
        public static string SelectElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var elementIds = parameters["elementIds"]?.ToObject<List<int>>();
                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds required" });
                }

                var doc = uidoc.Document;
                var idsToSelect = new List<ElementId>();
                var notFound = new List<int>();

                foreach (var id in elementIds)
                {
                    var elemId = new ElementId(id);
                    var element = doc.GetElement(elemId);
                    if (element != null)
                    {
                        idsToSelect.Add(elemId);
                    }
                    else
                    {
                        notFound.Add(id);
                    }
                }

                // Set the selection
                uidoc.Selection.SetElementIds(idsToSelect);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    selectedCount = idsToSelect.Count,
                    requestedCount = elementIds.Count,
                    notFoundIds = notFound,
                    message = $"Selected {idsToSelect.Count} of {elementIds.Count} elements"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error selecting elements");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Highlight Elements

        /// <summary>
        /// Highlight elements temporarily without changing selection.
        /// Uses OverrideGraphicSettings for visual feedback.
        /// </summary>
        [MCPMethod("highlightElements", Category = "Selection", Description = "Highlight elements temporarily without changing selection")]
        public static string HighlightElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var view = uidoc.ActiveView;

                var elementIds = parameters["elementIds"]?.ToObject<List<int>>();
                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds required" });
                }

                // Highlight color (default: cyan)
                var r = parameters["colorR"]?.Value<byte>() ?? 0;
                var g = parameters["colorG"]?.Value<byte>() ?? 255;
                var b = parameters["colorB"]?.Value<byte>() ?? 255;
                var color = new Color(r, g, b);

                var clear = parameters["clear"]?.Value<bool>() ?? false;

                using (var trans = new Transaction(doc, "Highlight Elements"))
                {
                    trans.Start();

                    var ogs = new OverrideGraphicSettings();

                    if (!clear)
                    {
                        ogs.SetProjectionLineColor(color);
                        ogs.SetSurfaceForegroundPatternColor(color);
                        ogs.SetProjectionLineWeight(6);
                    }

                    int highlighted = 0;
                    foreach (var id in elementIds)
                    {
                        var elemId = new ElementId(id);
                        var element = doc.GetElement(elemId);
                        if (element != null)
                        {
                            view.SetElementOverrides(elemId, clear ? new OverrideGraphicSettings() : ogs);
                            highlighted++;
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        highlightedCount = highlighted,
                        action = clear ? "cleared" : "highlighted",
                        color = clear ? null : new { r, g, b },
                        message = clear ? $"Cleared highlights on {highlighted} elements" : $"Highlighted {highlighted} elements"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error highlighting elements");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Clear Selection

        /// <summary>
        /// Clear the current selection in Revit.
        /// </summary>
        [MCPMethod("clearSelection", Category = "Selection", Description = "Clear the current selection in Revit")]
        public static string ClearSelection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var previousCount = uidoc.Selection.GetElementIds().Count;
                uidoc.Selection.SetElementIds(new List<ElementId>());

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    previousCount = previousCount,
                    message = $"Cleared selection ({previousCount} elements were selected)"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error clearing selection");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Pick Point

        /// <summary>
        /// Prompt user to pick a point in the model.
        /// </summary>
        [MCPMethod("pickPoint", Category = "Selection", Description = "Prompt user to pick a point in the model")]
        public static string PickPoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var prompt = parameters["prompt"]?.ToString() ?? "Pick a point";
                var snapToElements = parameters["snapToElements"]?.Value<bool>() ?? true;

                XYZ point;
                try
                {
                    if (snapToElements)
                    {
                        point = uidoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | ObjectSnapTypes.Intersections, prompt);
                    }
                    else
                    {
                        point = uidoc.Selection.PickPoint(prompt);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cancelled = true,
                        message = "Point selection cancelled by user"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    cancelled = false,
                    point = new
                    {
                        x = Math.Round(point.X, 6),
                        y = Math.Round(point.Y, 6),
                        z = Math.Round(point.Z, 6),
                        xFeet = Math.Round(point.X, 4),
                        yFeet = Math.Round(point.Y, 4),
                        zFeet = Math.Round(point.Z, 4)
                    },
                    message = $"Point picked at ({point.X:F2}, {point.Y:F2}, {point.Z:F2})"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error picking point");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Pick Edge/Face

        /// <summary>
        /// Prompt user to pick an edge on an element.
        /// </summary>
        [MCPMethod("pickEdge", Category = "Selection", Description = "Prompt user to pick an edge on an element")]
        public static string PickEdge(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var prompt = parameters["prompt"]?.ToString() ?? "Pick an edge";

                Reference edgeRef;
                try
                {
                    edgeRef = uidoc.Selection.PickObject(ObjectType.Edge, prompt);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cancelled = true,
                        message = "Edge selection cancelled by user"
                    });
                }

                var element = doc.GetElement(edgeRef.ElementId);
                var edge = element.GetGeometryObjectFromReference(edgeRef) as Edge;

                object edgeInfo = null;
                if (edge != null)
                {
                    var curve = edge.AsCurve();
                    edgeInfo = new
                    {
                        length = Math.Round(curve.Length, 4),
                        startPoint = new { x = curve.GetEndPoint(0).X, y = curve.GetEndPoint(0).Y, z = curve.GetEndPoint(0).Z },
                        endPoint = new { x = curve.GetEndPoint(1).X, y = curve.GetEndPoint(1).Y, z = curve.GetEndPoint(1).Z },
                        curveType = curve.GetType().Name
                    };
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    cancelled = false,
                    elementId = edgeRef.ElementId.Value,
                    elementName = element?.Name,
                    edge = edgeInfo,
                    message = "Edge picked successfully"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error picking edge");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Prompt user to pick a face on an element.
        /// </summary>
        [MCPMethod("pickFace", Category = "Selection", Description = "Prompt user to pick a face on an element")]
        public static string PickFace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var prompt = parameters["prompt"]?.ToString() ?? "Pick a face";

                Reference faceRef;
                try
                {
                    faceRef = uidoc.Selection.PickObject(ObjectType.Face, prompt);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cancelled = true,
                        message = "Face selection cancelled by user"
                    });
                }

                var element = doc.GetElement(faceRef.ElementId);
                var face = element.GetGeometryObjectFromReference(faceRef) as Face;

                object faceInfo = null;
                if (face != null)
                {
                    faceInfo = new
                    {
                        area = Math.Round(face.Area, 4),
                        faceType = face.GetType().Name,
                        materialId = face.MaterialElementId?.Value
                    };
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    cancelled = false,
                    elementId = faceRef.ElementId.Value,
                    elementName = element?.Name,
                    face = faceInfo,
                    message = "Face picked successfully"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error picking face");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Zoom To Selection

        /// <summary>
        /// Zoom the active view to fit selected or specified elements.
        /// </summary>
        [MCPMethod("zoomToElements", Category = "Selection", Description = "Zoom the active view to fit selected or specified elements")]
        public static string ZoomToElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var elementIds = parameters["elementIds"]?.ToObject<List<int>>();

                ICollection<ElementId> idsToZoom;

                if (elementIds != null && elementIds.Count > 0)
                {
                    idsToZoom = elementIds.Select(id => new ElementId(id)).ToList();
                }
                else
                {
                    // Use current selection
                    idsToZoom = uidoc.Selection.GetElementIds();
                }

                if (idsToZoom.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No elements to zoom to" });
                }

                // Get bounding box of all elements
                BoundingBoxXYZ combinedBox = null;
                foreach (var id in idsToZoom)
                {
                    var element = doc.GetElement(id);
                    if (element != null)
                    {
                        var box = element.get_BoundingBox(uidoc.ActiveView);
                        if (box != null)
                        {
                            if (combinedBox == null)
                            {
                                combinedBox = new BoundingBoxXYZ();
                                combinedBox.Min = box.Min;
                                combinedBox.Max = box.Max;
                            }
                            else
                            {
                                combinedBox.Min = new XYZ(
                                    Math.Min(combinedBox.Min.X, box.Min.X),
                                    Math.Min(combinedBox.Min.Y, box.Min.Y),
                                    Math.Min(combinedBox.Min.Z, box.Min.Z));
                                combinedBox.Max = new XYZ(
                                    Math.Max(combinedBox.Max.X, box.Max.X),
                                    Math.Max(combinedBox.Max.Y, box.Max.Y),
                                    Math.Max(combinedBox.Max.Z, box.Max.Z));
                            }
                        }
                    }
                }

                if (combinedBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Could not determine element bounds" });
                }

                // Add padding
                var padding = 5.0; // 5 feet padding
                combinedBox.Min = new XYZ(combinedBox.Min.X - padding, combinedBox.Min.Y - padding, combinedBox.Min.Z);
                combinedBox.Max = new XYZ(combinedBox.Max.X + padding, combinedBox.Max.Y + padding, combinedBox.Max.Z);

                // Zoom to fit
                uidoc.GetOpenUIViews().FirstOrDefault()?.ZoomAndCenterRectangle(combinedBox.Min, combinedBox.Max);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementCount = idsToZoom.Count,
                    bounds = new
                    {
                        min = new { x = combinedBox.Min.X, y = combinedBox.Min.Y, z = combinedBox.Min.Z },
                        max = new { x = combinedBox.Max.X, y = combinedBox.Max.Y, z = combinedBox.Max.Z }
                    },
                    message = $"Zoomed to {idsToZoom.Count} element(s)"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error zooming to elements");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static string GetElementTypeName(Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var type = element.Document.GetElement(typeId);
                    return type?.Name ?? "Unknown";
                }
                return element.Name;
            }
            catch
            {
                return "Unknown";
            }
        }

        private static int? GetElementLevelId(Element element)
        {
            try
            {
                var levelParam = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                if (levelParam != null)
                {
                    return (int)levelParam.AsElementId().Value;
                }

                var levelIdParam = element.LevelId;
                if (levelIdParam != null && levelIdParam != ElementId.InvalidElementId)
                {
                    return (int)levelIdParam.Value;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static object GetElementLocation(Element element)
        {
            try
            {
                var location = element.Location;
                if (location is LocationPoint lp)
                {
                    return new { type = "point", x = lp.Point.X, y = lp.Point.Y, z = lp.Point.Z };
                }
                else if (location is LocationCurve lc)
                {
                    var curve = lc.Curve;
                    return new
                    {
                        type = "curve",
                        start = new { x = curve.GetEndPoint(0).X, y = curve.GetEndPoint(0).Y, z = curve.GetEndPoint(0).Z },
                        end = new { x = curve.GetEndPoint(1).X, y = curve.GetEndPoint(1).Y, z = curve.GetEndPoint(1).Z },
                        length = curve.Length
                    };
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Selection Filter

        /// <summary>
        /// Filter for selecting elements by category name.
        /// </summary>
        private class CategorySelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly string _categoryName;

            public CategorySelectionFilter(Document doc, string categoryName)
            {
                _doc = doc;
                _categoryName = categoryName;
            }

            public bool AllowElement(Element elem)
            {
                if (elem == null) return false;
                return elem.Category?.Name?.Equals(_categoryName, StringComparison.OrdinalIgnoreCase) ?? false;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                var element = _doc.GetElement(reference);
                return AllowElement(element);
            }
        }

        #endregion
    }
}
