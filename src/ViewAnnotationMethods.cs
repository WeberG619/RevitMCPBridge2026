using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Methods for view-aware annotation placement.
    /// These methods handle coordinate transformations and crop regions properly.
    /// </summary>
    public static class ViewAnnotationMethods
    {
        /// <summary>
        /// Get the view's crop region bounds in view coordinates.
        /// Returns the min/max XY of the crop box plus annotation crop info.
        /// </summary>
        public static string GetViewCropRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                // Get crop box if available
                BoundingBoxXYZ cropBox = null;
                double minX = 0, minY = 0, maxX = 0, maxY = 0;
                bool hasCropBox = false;
                bool cropBoxActive = false;
                bool annotationCropActive = false;

                try
                {
                    cropBox = view.CropBox;
                    if (cropBox != null)
                    {
                        hasCropBox = true;
                        minX = cropBox.Min.X;
                        minY = cropBox.Min.Y;
                        maxX = cropBox.Max.X;
                        maxY = cropBox.Max.Y;
                    }
                    cropBoxActive = view.CropBoxActive;
                }
                catch { }

                // Check annotation crop
                try
                {
                    // AreAnnotationCategoriesHidden tells if annotations are hidden
                    annotationCropActive = view.AreAnnotationCategoriesHidden == false;
                }
                catch { }

                // Get view-specific outline (works for sections/callouts)
                BoundingBoxUV outline = null;
                try
                {
                    outline = view.Outline;
                }
                catch { }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        hasCropBox = hasCropBox,
                        cropBoxActive = cropBoxActive,
                        annotationCropActive = annotationCropActive,
                        cropRegion = hasCropBox ? new
                        {
                            minX = minX,
                            minY = minY,
                            maxX = maxX,
                            maxY = maxY,
                            width = maxX - minX,
                            height = maxY - minY,
                            centerX = (minX + maxX) / 2,
                            centerY = (minY + maxY) / 2
                        } : null,
                        outline = outline != null ? new
                        {
                            minU = outline.Min.U,
                            minV = outline.Min.V,
                            maxU = outline.Max.U,
                            maxV = outline.Max.V
                        } : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Expand the view's crop region to include the specified point.
        /// Useful for ensuring annotations are visible.
        /// </summary>
        public static string ExpandViewCropRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var x = parameters?["x"]?.ToObject<double>() ?? 0;
                var y = parameters?["y"]?.ToObject<double>() ?? 0;
                var margin = parameters?["margin"]?.ToObject<double>() ?? 0.5; // Default 6" margin

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "MCP Expand Crop Region"))
                {
                    trans.Start();

                    try
                    {
                        var cropBox = view.CropBox;
                        if (cropBox == null)
                        {
                            trans.RollBack();
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "View does not have a crop box"
                            });
                        }

                        bool modified = false;
                        var newMin = cropBox.Min;
                        var newMax = cropBox.Max;

                        // Expand min if needed
                        if (x - margin < cropBox.Min.X)
                        {
                            newMin = new XYZ(x - margin, newMin.Y, newMin.Z);
                            modified = true;
                        }
                        if (y - margin < cropBox.Min.Y)
                        {
                            newMin = new XYZ(newMin.X, y - margin, newMin.Z);
                            modified = true;
                        }

                        // Expand max if needed
                        if (x + margin > cropBox.Max.X)
                        {
                            newMax = new XYZ(x + margin, newMax.Y, newMax.Z);
                            modified = true;
                        }
                        if (y + margin > cropBox.Max.Y)
                        {
                            newMax = new XYZ(newMax.X, y + margin, newMax.Z);
                            modified = true;
                        }

                        if (modified)
                        {
                            cropBox.Min = newMin;
                            cropBox.Max = newMax;
                            view.CropBox = cropBox;
                        }

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = new
                            {
                                viewId = viewId.Value,
                                modified = modified,
                                newCropRegion = new
                                {
                                    minX = newMin.X,
                                    minY = newMin.Y,
                                    maxX = newMax.X,
                                    maxY = newMax.Y
                                }
                            }
                        });
                    }
                    catch (Exception)
                    {
                        trans.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the location and properties of a text note.
        /// </summary>
        public static string GetTextNoteLocation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdParam = parameters?["elementId"];

                if (elementIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId parameter is required"
                    });
                }

                var elementId = new ElementId(elementIdParam.ToObject<int>());
                var textNote = doc.GetElement(elementId) as TextNote;

                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"TextNote with ID {elementId.Value} not found"
                    });
                }

                var coord = textNote.Coord;
                var viewId = textNote.OwnerViewId;
                var view = doc.GetElement(viewId) as View;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        elementId = elementId.Value,
                        text = textNote.Text,
                        x = coord.X,
                        y = coord.Y,
                        z = coord.Z,
                        viewId = viewId.Value,
                        viewName = view?.Name,
                        width = textNote.Width,
                        horizontalAlignment = textNote.HorizontalAlignment.ToString(),
                        leaderCount = textNote.LeaderCount
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a text note with automatic crop region expansion.
        /// Ensures the text will be visible in the view.
        /// </summary>
        public static string CreateTextNoteInCrop(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var text = parameters?["text"]?.ToString();
                var x = parameters?["x"]?.ToObject<double>() ?? 0;
                var y = parameters?["y"]?.ToObject<double>() ?? 0;
                var expandCrop = parameters?["expandCrop"]?.ToObject<bool>() ?? true;
                var typeIdParam = parameters?["typeId"];

                if (viewIdParam == null || string.IsNullOrEmpty(text))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and text parameters are required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "MCP Create Text Note In Crop"))
                {
                    trans.Start();

                    try
                    {
                        // Expand crop region first if requested
                        if (expandCrop && view.CropBox != null)
                        {
                            var cropBox = view.CropBox;
                            var margin = 0.5; // 6 inches
                            bool modified = false;
                            var newMin = cropBox.Min;
                            var newMax = cropBox.Max;

                            if (x - margin < cropBox.Min.X)
                            {
                                newMin = new XYZ(x - margin, newMin.Y, newMin.Z);
                                modified = true;
                            }
                            if (y - margin < cropBox.Min.Y)
                            {
                                newMin = new XYZ(newMin.X, y - margin, newMin.Z);
                                modified = true;
                            }
                            if (x + margin > cropBox.Max.X)
                            {
                                newMax = new XYZ(x + margin, newMax.Y, newMax.Z);
                                modified = true;
                            }
                            if (y + margin > cropBox.Max.Y)
                            {
                                newMax = new XYZ(newMax.X, y + margin, newMax.Z);
                                modified = true;
                            }

                            if (modified)
                            {
                                cropBox.Min = newMin;
                                cropBox.Max = newMax;
                                view.CropBox = cropBox;
                            }
                        }

                        // Get text note type
                        ElementId textNoteTypeId;
                        if (typeIdParam != null)
                        {
                            textNoteTypeId = new ElementId(typeIdParam.ToObject<int>());
                        }
                        else
                        {
                            var textNoteType = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .FirstOrDefault();

                            if (textNoteType == null)
                            {
                                trans.RollBack();
                                return JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = "No TextNoteType found in document"
                                });
                            }
                            textNoteTypeId = textNoteType.Id;
                        }

                        var position = new XYZ(x, y, 0);
                        var textNote = TextNote.Create(doc, viewId, position, text, textNoteTypeId);

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = new
                            {
                                id = textNote.Id.Value,
                                text = textNote.Text,
                                viewId = viewId.Value,
                                viewName = view.Name,
                                position = new { x = position.X, y = position.Y, z = position.Z }
                            }
                        });
                    }
                    catch (Exception)
                    {
                        trans.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a linear dimension between two points.
        /// </summary>
        public static string CreateLinearDimension(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var x1 = parameters?["x1"]?.ToObject<double>() ?? 0;
                var y1 = parameters?["y1"]?.ToObject<double>() ?? 0;
                var x2 = parameters?["x2"]?.ToObject<double>() ?? 0;
                var y2 = parameters?["y2"]?.ToObject<double>() ?? 0;
                var dimLineX = parameters?["dimLineX"]?.ToObject<double?>();
                var dimLineY = parameters?["dimLineY"]?.ToObject<double?>();

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "MCP Create Linear Dimension"))
                {
                    trans.Start();

                    try
                    {
                        // Create reference points using detail lines (temporary)
                        var point1 = new XYZ(x1, y1, 0);
                        var point2 = new XYZ(x2, y2, 0);

                        // Create a line between the two points
                        var line = Line.CreateBound(point1, point2);

                        // Determine dimension line position
                        XYZ dimLinePoint;
                        if (dimLineX.HasValue && dimLineY.HasValue)
                        {
                            dimLinePoint = new XYZ(dimLineX.Value, dimLineY.Value, 0);
                        }
                        else
                        {
                            // Default: offset perpendicular from midpoint
                            var midpoint = (point1 + point2) / 2;
                            var direction = (point2 - point1).Normalize();
                            var perpendicular = new XYZ(-direction.Y, direction.X, 0);
                            dimLinePoint = midpoint + perpendicular * 0.5;
                        }

                        // For drafting views, we need to create detail lines first
                        // and use their references for dimensioning
                        var detailLine1 = doc.Create.NewDetailCurve(view, Line.CreateBound(point1, point1 + new XYZ(0.001, 0, 0)));
                        var detailLine2 = doc.Create.NewDetailCurve(view, Line.CreateBound(point2, point2 + new XYZ(0.001, 0, 0)));

                        var referenceArray = new ReferenceArray();
                        referenceArray.Append(detailLine1.GeometryCurve.GetEndPointReference(0));
                        referenceArray.Append(detailLine2.GeometryCurve.GetEndPointReference(0));

                        var dimension = doc.Create.NewDimension(view, line, referenceArray);

                        // Clean up temporary detail lines
                        doc.Delete(detailLine1.Id);
                        doc.Delete(detailLine2.Id);

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = new
                            {
                                id = dimension?.Id?.Value ?? -1,
                                viewId = viewId.Value,
                                point1 = new { x = x1, y = y1 },
                                point2 = new { x = x2, y = y2 }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Failed to create dimension: {ex.Message}",
                            note = "Linear dimensions in drafting views require reference elements. Consider using detail lines as dimension references."
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a text note with a leader line pointing to a specific location.
        /// </summary>
        public static string CreateTextNoteWithLeader(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var text = parameters?["text"]?.ToString();
                var textX = parameters?["textX"]?.ToObject<double>() ?? 0;
                var textY = parameters?["textY"]?.ToObject<double>() ?? 0;
                var textZ = parameters?["textZ"]?.ToObject<double>() ?? 0;
                var leaderX = parameters?["leaderX"]?.ToObject<double>() ?? 0;
                var leaderY = parameters?["leaderY"]?.ToObject<double>() ?? 0;
                var leaderZ = parameters?["leaderZ"]?.ToObject<double>() ?? 0;
                var typeIdParam = parameters?["typeId"];
                var expandCrop = parameters?["expandCrop"]?.ToObject<bool>() ?? true;
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null || string.IsNullOrEmpty(text))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and text parameters are required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "MCP Create Text Note With Leader"))
                {
                    trans.Start();

                    try
                    {
                        // Expand crop region if needed
                        if (expandCrop && view.CropBox != null)
                        {
                            var cropBox = view.CropBox;
                            var margin = 0.5;
                            var minX = Math.Min(textX, leaderX) - margin;
                            var minY = Math.Min(textY, leaderY) - margin;
                            var maxX = Math.Max(textX, leaderX) + margin;
                            var maxY = Math.Max(textY, leaderY) + margin;

                            var newMin = new XYZ(
                                Math.Min(cropBox.Min.X, minX),
                                Math.Min(cropBox.Min.Y, minY),
                                cropBox.Min.Z);
                            var newMax = new XYZ(
                                Math.Max(cropBox.Max.X, maxX),
                                Math.Max(cropBox.Max.Y, maxY),
                                cropBox.Max.Z);

                            cropBox.Min = newMin;
                            cropBox.Max = newMax;
                            view.CropBox = cropBox;
                        }

                        // Get text note type
                        ElementId textNoteTypeId;
                        if (typeIdParam != null)
                        {
                            textNoteTypeId = new ElementId(typeIdParam.ToObject<int>());
                        }
                        else
                        {
                            var textNoteType = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .FirstOrDefault();

                            if (textNoteType == null)
                            {
                                trans.RollBack();
                                return JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = "No TextNoteType found in document"
                                });
                            }
                            textNoteTypeId = textNoteType.Id;
                        }

                        // Create text note options with leader
                        var options = new TextNoteOptions(textNoteTypeId)
                        {
                            HorizontalAlignment = HorizontalTextAlignment.Left
                        };

                        XYZ textPosition;
                        XYZ leaderEnd;

                        // For section views, use CropBox transform to convert view coords to model coords
                        if (useViewCoords && view.CropBox != null)
                        {
                            var cropBox = view.CropBox;
                            var transform = cropBox.Transform;
                            // Transform from view coordinates to model coordinates
                            textPosition = transform.OfPoint(new XYZ(textX, textY, textZ));
                            leaderEnd = transform.OfPoint(new XYZ(leaderX, leaderY, leaderZ));
                        }
                        else
                        {
                            // Use coordinates directly (model space)
                            textPosition = new XYZ(textX, textY, textZ);
                            leaderEnd = new XYZ(leaderX, leaderY, leaderZ);
                        }

                        // Create text note
                        var textNote = TextNote.Create(doc, viewId, textPosition, text, textNoteTypeId);

                        // Add leader
                        if (textNote != null)
                        {
                            var leader = textNote.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                            if (leader != null)
                            {
                                leader.End = leaderEnd;
                            }
                        }

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = new
                            {
                                id = textNote?.Id?.Value ?? -1,
                                text = text,
                                viewId = viewId.Value,
                                viewName = view.Name,
                                textPosition = new { x = textX, y = textY },
                                leaderEnd = new { x = leaderX, y = leaderY },
                                leaderCount = textNote?.LeaderCount ?? 0
                            }
                        });
                    }
                    catch (Exception)
                    {
                        trans.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move an existing text note to a new location.
        /// </summary>
        public static string MoveTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdParam = parameters?["elementId"];
                var x = parameters?["x"]?.ToObject<double?>();
                var y = parameters?["y"]?.ToObject<double?>();

                if (elementIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId parameter is required"
                    });
                }

                var elementId = new ElementId(elementIdParam.ToObject<int>());
                var textNote = doc.GetElement(elementId) as TextNote;

                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"TextNote with ID {elementId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "MCP Move Text Note"))
                {
                    trans.Start();

                    var oldCoord = textNote.Coord;
                    var newX = x ?? oldCoord.X;
                    var newY = y ?? oldCoord.Y;
                    var newCoord = new XYZ(newX, newY, oldCoord.Z);

                    textNote.Coord = newCoord;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            elementId = elementId.Value,
                            oldPosition = new { x = oldCoord.X, y = oldCoord.Y },
                            newPosition = new { x = newX, y = newY }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all text note types available in the document.
        /// </summary>
        public static string GetTextNoteTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .Select(t => new
                    {
                        id = t.Id.Value,
                        name = t.Name,
                        textSize = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0,
                        bold = t.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.AsInteger() == 1,
                        italic = t.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC)?.AsInteger() == 1
                    })
                    .OrderBy(t => t.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = types.Count,
                        types = types
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add a leader to an existing text note.
        /// </summary>
        public static string AddLeaderToTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var textNoteIdParam = parameters?["textNoteId"];
                var leaderX = parameters?["leaderX"]?.ToObject<double>() ?? 0;
                var leaderY = parameters?["leaderY"]?.ToObject<double>() ?? 0;
                var leaderZ = parameters?["leaderZ"]?.ToObject<double>() ?? 0;
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (textNoteIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "textNoteId parameter is required"
                    });
                }

                var textNoteId = new ElementId(textNoteIdParam.ToObject<int>());
                var textNote = doc.GetElement(textNoteId) as TextNote;

                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"TextNote with ID {textNoteId.Value} not found"
                    });
                }

                var view = doc.GetElement(textNote.OwnerViewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not find owner view for text note"
                    });
                }

                XYZ leaderEnd;
                if (useViewCoords && view.CropBox != null)
                {
                    var cropBox = view.CropBox;
                    var transform = cropBox.Transform;
                    leaderEnd = transform.OfPoint(new XYZ(leaderX, leaderY, leaderZ));
                }
                else
                {
                    leaderEnd = new XYZ(leaderX, leaderY, leaderZ);
                }

                using (var trans = new Transaction(doc, "MCP Add Leader to Text Note"))
                {
                    trans.Start();

                    var leader = textNote.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                    if (leader != null)
                    {
                        leader.End = leaderEnd;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            textNoteId = textNoteId.Value,
                            text = textNote.Text,
                            viewId = textNote.OwnerViewId.Value,
                            viewName = view.Name,
                            leaderEnd = new { x = leaderX, y = leaderY, z = leaderZ },
                            leaderCount = textNote.LeaderCount
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set/reposition an existing leader's endpoint on a text note.
        /// If leaderIndex is not specified, repositions the first leader.
        /// </summary>
        public static string SetTextNoteLeaderEndpoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var textNoteIdParam = parameters?["textNoteId"];
                var leaderX = parameters?["leaderX"]?.ToObject<double>() ?? 0;
                var leaderY = parameters?["leaderY"]?.ToObject<double>() ?? 0;
                var leaderZ = parameters?["leaderZ"]?.ToObject<double>() ?? 0;
                var leaderIndex = parameters?["leaderIndex"]?.ToObject<int>() ?? 0;
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (textNoteIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "textNoteId parameter is required"
                    });
                }

                var textNoteId = new ElementId(textNoteIdParam.ToObject<int>());
                var textNote = doc.GetElement(textNoteId) as TextNote;

                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"TextNote with ID {textNoteId.Value} not found"
                    });
                }

                var leaders = textNote.GetLeaders();
                if (leaders == null || leaders.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"TextNote {textNoteId.Value} has no leaders to reposition"
                    });
                }

                if (leaderIndex >= leaders.Count)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Leader index {leaderIndex} is out of range. TextNote has {leaders.Count} leader(s)"
                    });
                }

                var view = doc.GetElement(textNote.OwnerViewId) as View;
                XYZ leaderEnd;
                if (useViewCoords && view?.CropBox != null)
                {
                    var cropBox = view.CropBox;
                    var transform = cropBox.Transform;
                    leaderEnd = transform.OfPoint(new XYZ(leaderX, leaderY, leaderZ));
                }
                else
                {
                    leaderEnd = new XYZ(leaderX, leaderY, leaderZ);
                }

                using (var trans = new Transaction(doc, "MCP Set Leader Endpoint"))
                {
                    trans.Start();

                    var leader = leaders[leaderIndex];
                    leader.End = leaderEnd;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            textNoteId = textNoteId.Value,
                            text = textNote.Text.Trim(),
                            viewId = textNote.OwnerViewId.Value,
                            leaderIndex = leaderIndex,
                            newEndpoint = new { x = leaderX, y = leaderY, z = leaderZ },
                            totalLeaders = leaders.Count
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove all leaders from a text note, optionally add a new one with specified endpoint.
        /// </summary>
        public static string ResetTextNoteLeaders(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var textNoteIdParam = parameters?["textNoteId"];
                var addNew = parameters?["addNewLeader"]?.ToObject<bool>() ?? false;
                var leaderX = parameters?["leaderX"]?.ToObject<double>() ?? 0;
                var leaderY = parameters?["leaderY"]?.ToObject<double>() ?? 0;
                var leaderZ = parameters?["leaderZ"]?.ToObject<double>() ?? 0;

                if (textNoteIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "textNoteId parameter is required"
                    });
                }

                var textNoteId = new ElementId(textNoteIdParam.ToObject<int>());
                var textNote = doc.GetElement(textNoteId) as TextNote;

                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"TextNote with ID {textNoteId.Value} not found"
                    });
                }

                var view = doc.GetElement(textNote.OwnerViewId) as View;
                int removedCount = textNote.LeaderCount;

                using (var trans = new Transaction(doc, "MCP Reset Text Note Leaders"))
                {
                    trans.Start();

                    // Remove all existing leaders
                    textNote.RemoveLeaders();

                    // Optionally add a new leader
                    if (addNew)
                    {
                        var leader = textNote.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                        if (leader != null)
                        {
                            leader.End = new XYZ(leaderX, leaderY, leaderZ);
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            textNoteId = textNoteId.Value,
                            text = textNote.Text.Trim(),
                            viewId = textNote.OwnerViewId.Value,
                            viewName = view?.Name,
                            removedLeaders = removedCount,
                            newLeaderCount = textNote.LeaderCount,
                            newEndpoint = addNew ? new { x = leaderX, y = leaderY, z = leaderZ } : null
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // TEXT ALIGNMENT METHODS
        // =====================================================

        /// <summary>
        /// Get positions of all text notes in a view for alignment reference.
        /// Returns text notes sorted by position for easy alignment decisions.
        /// </summary>
        public static string GetTextNotePositions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Select(tn => {
                        var coord = tn.Coord;
                        var leaders = tn.GetLeaders();
                        var leaderEndpoints = new List<object>();
                        if (leaders != null && leaders.Count > 0)
                        {
                            foreach (var leader in leaders)
                            {
                                var end = leader.End;
                                leaderEndpoints.Add(new
                                {
                                    x = Math.Round(end.X, 4),
                                    y = Math.Round(end.Y, 4),
                                    z = Math.Round(end.Z, 4)
                                });
                            }
                        }
                        return new
                        {
                            id = tn.Id.Value,
                            text = tn.Text.Replace("\r", "\\r").Replace("\n", "\\n"),
                            x = Math.Round(coord.X, 4),
                            y = Math.Round(coord.Y, 4),
                            z = Math.Round(coord.Z, 4),
                            hasLeader = tn.LeaderCount > 0,
                            leaderCount = tn.LeaderCount,
                            leaderEndpoints = leaderEndpoints
                        };
                    })
                    .OrderByDescending(t => t.z)  // Sort by elevation (Z) for section views
                    .ThenBy(t => t.x)
                    .ToList();

                // Calculate common X positions (for vertical alignment reference)
                var commonXPositions = textNotes
                    .GroupBy(t => Math.Round(t.x, 2))
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        count = textNotes.Count,
                        textNotes = textNotes,
                        commonXPositions = commonXPositions
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Align multiple text notes to a reference X or Y position.
        /// alignAxis: "x" or "y" - which axis to align
        /// referencePosition: the value to align to
        /// textNoteIds: array of text note IDs to align
        /// </summary>
        public static string AlignTextNotes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var alignAxis = parameters?["alignAxis"]?.ToString()?.ToLower() ?? "x";
                var referencePosition = parameters?["referencePosition"]?.ToObject<double>() ?? 0;
                var textNoteIdsParam = parameters?["textNoteIds"] as JArray;

                if (textNoteIdsParam == null || textNoteIdsParam.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "textNoteIds array is required"
                    });
                }

                var textNoteIds = textNoteIdsParam.Select(id => new ElementId(id.ToObject<int>())).ToList();
                var alignedNotes = new List<object>();

                using (var trans = new Transaction(doc, "MCP Align Text Notes"))
                {
                    trans.Start();

                    foreach (var textNoteId in textNoteIds)
                    {
                        var textNote = doc.GetElement(textNoteId) as TextNote;
                        if (textNote == null) continue;

                        var currentCoord = textNote.Coord;
                        XYZ newCoord;

                        if (alignAxis == "x")
                        {
                            newCoord = new XYZ(referencePosition, currentCoord.Y, currentCoord.Z);
                        }
                        else // y
                        {
                            newCoord = new XYZ(currentCoord.X, referencePosition, currentCoord.Z);
                        }

                        // Move the text note
                        var moveVector = newCoord - currentCoord;
                        ElementTransformUtils.MoveElement(doc, textNoteId, moveVector);

                        alignedNotes.Add(new
                        {
                            id = textNoteId.Value,
                            text = textNote.Text.Substring(0, Math.Min(30, textNote.Text.Length)),
                            oldPosition = new { x = currentCoord.X, y = currentCoord.Y, z = currentCoord.Z },
                            newPosition = new { x = newCoord.X, y = newCoord.Y, z = newCoord.Z }
                        });
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        alignAxis = alignAxis,
                        referencePosition = referencePosition,
                        alignedCount = alignedNotes.Count,
                        alignedNotes = alignedNotes
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a text note aligned with an existing text note (same X position).
        /// </summary>
        public static string CreateTextNoteAlignedWith(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var referenceTextNoteIdParam = parameters?["referenceTextNoteId"];
                var text = parameters?["text"]?.ToString() ?? "";
                var offsetY = parameters?["offsetY"]?.ToObject<double>() ?? -0.5; // Default offset below reference
                var offsetZ = parameters?["offsetZ"]?.ToObject<double>() ?? 0; // For section views
                var typeIdParam = parameters?["typeId"];
                var addLeader = parameters?["addLeader"]?.ToObject<bool>() ?? false;
                var leaderOffsetX = parameters?["leaderOffsetX"]?.ToObject<double>() ?? -1.0;
                var leaderOffsetY = parameters?["leaderOffsetY"]?.ToObject<double>() ?? 0;
                var leaderOffsetZ = parameters?["leaderOffsetZ"]?.ToObject<double>() ?? 0;

                if (referenceTextNoteIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "referenceTextNoteId parameter is required"
                    });
                }

                if (string.IsNullOrEmpty(text))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "text parameter is required"
                    });
                }

                var referenceId = new ElementId(referenceTextNoteIdParam.ToObject<int>());
                var referenceNote = doc.GetElement(referenceId) as TextNote;

                if (referenceNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Reference TextNote with ID {referenceId.Value} not found"
                    });
                }

                var refCoord = referenceNote.Coord;
                var newPosition = new XYZ(refCoord.X, refCoord.Y + offsetY, refCoord.Z + offsetZ);

                // Use same type as reference if not specified
                var textNoteTypeId = typeIdParam != null
                    ? new ElementId(typeIdParam.ToObject<int>())
                    : referenceNote.GetTypeId();

                using (var trans = new Transaction(doc, "MCP Create Aligned Text Note"))
                {
                    trans.Start();

                    var textNote = TextNote.Create(doc, referenceNote.OwnerViewId, newPosition, text, textNoteTypeId);

                    if (addLeader && textNote != null)
                    {
                        var leader = textNote.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                        if (leader != null)
                        {
                            leader.End = new XYZ(
                                newPosition.X + leaderOffsetX,
                                newPosition.Y + leaderOffsetY,
                                newPosition.Z + leaderOffsetZ
                            );
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = textNote?.Id?.Value ?? -1,
                            text = text,
                            alignedWithId = referenceId.Value,
                            position = new { x = newPosition.X, y = newPosition.Y, z = newPosition.Z },
                            hasLeader = addLeader
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // DETAIL COMPONENT METHODS
        // =====================================================

        /// <summary>
        /// Get all detail component families loaded in the project.
        /// </summary>
        public static string GetDetailComponentFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => f.FamilyCategory?.BuiltInCategory == BuiltInCategory.OST_DetailComponents)
                    .Select(f => new
                    {
                        id = f.Id.Value,
                        name = f.Name,
                        typeCount = f.GetFamilySymbolIds().Count
                    })
                    .OrderBy(f => f.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = families.Count,
                        families = families
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all types for a specific detail component family.
        /// </summary>
        public static string GetDetailComponentTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var familyIdParam = parameters?["familyId"];
                var familyName = parameters?["familyName"]?.ToString();

                Family family = null;

                if (familyIdParam != null)
                {
                    var familyId = new ElementId(familyIdParam.ToObject<int>());
                    family = doc.GetElement(familyId) as Family;
                }
                else if (!string.IsNullOrEmpty(familyName))
                {
                    family = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
                }

                if (family == null)
                {
                    // If no family specified, return all detail component types
                    var allTypes = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_DetailComponents)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Select(fs => new
                        {
                            id = fs.Id.Value,
                            name = fs.Name,
                            familyName = fs.Family?.Name ?? "Unknown"
                        })
                        .OrderBy(t => t.familyName)
                        .ThenBy(t => t.name)
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            allTypes = true,
                            count = allTypes.Count,
                            types = allTypes
                        }
                    });
                }

                var typeIds = family.GetFamilySymbolIds();
                var types = typeIds
                    .Select(id => doc.GetElement(id) as FamilySymbol)
                    .Where(fs => fs != null)
                    .Select(fs => new
                    {
                        id = fs.Id.Value,
                        name = fs.Name,
                        isActive = fs.IsActive
                    })
                    .OrderBy(t => t.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        familyId = family.Id.Value,
                        familyName = family.Name,
                        count = types.Count,
                        types = types
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a detail component in a view.
        /// </summary>
        public static string PlaceDetailComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var typeIdParam = parameters?["typeId"];
                var x = parameters?["x"]?.ToObject<double>() ?? 0;
                var y = parameters?["y"]?.ToObject<double>() ?? 0;
                var z = parameters?["z"]?.ToObject<double>() ?? 0;
                var rotation = parameters?["rotation"]?.ToObject<double>() ?? 0; // degrees
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (typeIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var typeId = new ElementId(typeIdParam.ToObject<int>());
                var familySymbol = doc.GetElement(typeId) as FamilySymbol;

                if (familySymbol == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"FamilySymbol with ID {typeId.Value} not found"
                    });
                }

                XYZ location;
                if (useViewCoords && view.CropBox != null)
                {
                    var cropBox = view.CropBox;
                    var transform = cropBox.Transform;
                    location = transform.OfPoint(new XYZ(x, y, z));
                }
                else
                {
                    location = new XYZ(x, y, z);
                }

                using (var trans = new Transaction(doc, "MCP Place Detail Component"))
                {
                    trans.Start();

                    // Activate the symbol if not active
                    if (!familySymbol.IsActive)
                    {
                        familySymbol.Activate();
                        doc.Regenerate();
                    }

                    // Place the detail component
                    var instance = doc.Create.NewFamilyInstance(location, familySymbol, view);

                    // Rotate if needed
                    if (rotation != 0 && instance != null)
                    {
                        var axis = Line.CreateBound(location, location + XYZ.BasisZ);
                        var radians = rotation * Math.PI / 180.0;
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, radians);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = instance?.Id?.Value ?? -1,
                            typeName = familySymbol.Name,
                            familyName = familySymbol.Family?.Name,
                            viewId = viewId.Value,
                            viewName = view.Name,
                            location = new { x = x, y = y, z = z },
                            rotation = rotation
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all detail components in a view.
        /// </summary>
        public static string GetDetailComponentsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var components = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Select(fi => {
                        var loc = (fi.Location as LocationPoint)?.Point ?? XYZ.Zero;
                        return new
                        {
                            id = fi.Id.Value,
                            typeId = fi.Symbol?.Id?.Value ?? 0,
                            typeName = fi.Symbol?.Name ?? "Unknown",
                            familyName = fi.Symbol?.Family?.Name ?? "Unknown",
                            location = new { x = Math.Round(loc.X, 4), y = Math.Round(loc.Y, 4), z = Math.Round(loc.Z, 4) }
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        count = components.Count,
                        components = components
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move a detail component to a new location.
        /// </summary>
        public static string MoveDetailComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdParam = parameters?["elementId"];
                var x = parameters?["x"]?.ToObject<double?>();
                var y = parameters?["y"]?.ToObject<double?>();
                var z = parameters?["z"]?.ToObject<double?>();
                var deltaX = parameters?["deltaX"]?.ToObject<double>() ?? 0;
                var deltaY = parameters?["deltaY"]?.ToObject<double>() ?? 0;
                var deltaZ = parameters?["deltaZ"]?.ToObject<double>() ?? 0;

                if (elementIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId parameter is required"
                    });
                }

                var elementId = new ElementId(elementIdParam.ToObject<int>());
                var element = doc.GetElement(elementId) as FamilyInstance;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Detail component with ID {elementId.Value} not found"
                    });
                }

                var currentLoc = (element.Location as LocationPoint)?.Point ?? XYZ.Zero;
                XYZ moveVector;

                if (x.HasValue || y.HasValue || z.HasValue)
                {
                    // Absolute position
                    var newX = x ?? currentLoc.X;
                    var newY = y ?? currentLoc.Y;
                    var newZ = z ?? currentLoc.Z;
                    moveVector = new XYZ(newX, newY, newZ) - currentLoc;
                }
                else
                {
                    // Relative movement
                    moveVector = new XYZ(deltaX, deltaY, deltaZ);
                }

                using (var trans = new Transaction(doc, "MCP Move Detail Component"))
                {
                    trans.Start();
                    ElementTransformUtils.MoveElement(doc, elementId, moveVector);
                    trans.Commit();
                }

                var newLoc = (element.Location as LocationPoint)?.Point ?? XYZ.Zero;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        id = elementId.Value,
                        oldLocation = new { x = currentLoc.X, y = currentLoc.Y, z = currentLoc.Z },
                        newLocation = new { x = newLoc.X, y = newLoc.Y, z = newLoc.Z }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rotate a detail component.
        /// </summary>
        public static string RotateDetailComponent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdParam = parameters?["elementId"];
                var angle = parameters?["angle"]?.ToObject<double>() ?? 0; // degrees

                if (elementIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId parameter is required"
                    });
                }

                var elementId = new ElementId(elementIdParam.ToObject<int>());
                var element = doc.GetElement(elementId) as FamilyInstance;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Detail component with ID {elementId.Value} not found"
                    });
                }

                var loc = (element.Location as LocationPoint)?.Point ?? XYZ.Zero;

                using (var trans = new Transaction(doc, "MCP Rotate Detail Component"))
                {
                    trans.Start();
                    var axis = Line.CreateBound(loc, loc + XYZ.BasisZ);
                    var radians = angle * Math.PI / 180.0;
                    ElementTransformUtils.RotateElement(doc, elementId, axis, radians);
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        id = elementId.Value,
                        rotationApplied = angle,
                        location = new { x = loc.X, y = loc.Y, z = loc.Z }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // DETAIL LINE METHODS
        // =====================================================

        /// <summary>
        /// Create a detail line in a view.
        /// </summary>
        public static string CreateDetailLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var startX = parameters?["startX"]?.ToObject<double>() ?? 0;
                var startY = parameters?["startY"]?.ToObject<double>() ?? 0;
                var startZ = parameters?["startZ"]?.ToObject<double>() ?? 0;
                var endX = parameters?["endX"]?.ToObject<double>() ?? 0;
                var endY = parameters?["endY"]?.ToObject<double>() ?? 0;
                var endZ = parameters?["endZ"]?.ToObject<double>() ?? 0;
                var lineStyleName = parameters?["lineStyle"]?.ToString();
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                XYZ startPoint, endPoint;
                if (useViewCoords && view.CropBox != null)
                {
                    var transform = view.CropBox.Transform;
                    startPoint = transform.OfPoint(new XYZ(startX, startY, startZ));
                    endPoint = transform.OfPoint(new XYZ(endX, endY, endZ));
                }
                else
                {
                    startPoint = new XYZ(startX, startY, startZ);
                    endPoint = new XYZ(endX, endY, endZ);
                }

                var line = Line.CreateBound(startPoint, endPoint);

                using (var trans = new Transaction(doc, "MCP Create Detail Line"))
                {
                    trans.Start();

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

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = detailCurve?.Id?.Value ?? -1,
                            viewId = viewId.Value,
                            start = new { x = startX, y = startY, z = startZ },
                            end = new { x = endX, y = endY, z = endZ },
                            lineStyle = detailCurve?.LineStyle?.Name ?? "Default"
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available line styles in the document.
        /// </summary>
        public static string GetLineStyles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var lineStyles = new FilteredElementCollector(doc)
                    .OfClass(typeof(GraphicsStyle))
                    .Cast<GraphicsStyle>()
                    .Where(gs => gs.GraphicsStyleCategory?.Parent?.BuiltInCategory == BuiltInCategory.OST_Lines)
                    .Select(gs => new
                    {
                        id = gs.Id.Value,
                        name = gs.Name,
                        category = gs.GraphicsStyleCategory?.Name ?? "Unknown"
                    })
                    .OrderBy(ls => ls.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = lineStyles.Count,
                        lineStyles = lineStyles
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a detail arc in a view.
        /// </summary>
        public static string CreateDetailArc(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var centerX = parameters?["centerX"]?.ToObject<double>() ?? 0;
                var centerY = parameters?["centerY"]?.ToObject<double>() ?? 0;
                var centerZ = parameters?["centerZ"]?.ToObject<double>() ?? 0;
                var radius = parameters?["radius"]?.ToObject<double>() ?? 1.0;
                var startAngle = parameters?["startAngle"]?.ToObject<double>() ?? 0; // degrees
                var endAngle = parameters?["endAngle"]?.ToObject<double>() ?? 180; // degrees
                var lineStyleName = parameters?["lineStyle"]?.ToString();
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                XYZ center;
                if (useViewCoords && view.CropBox != null)
                {
                    var transform = view.CropBox.Transform;
                    center = transform.OfPoint(new XYZ(centerX, centerY, centerZ));
                }
                else
                {
                    center = new XYZ(centerX, centerY, centerZ);
                }

                var startRad = startAngle * Math.PI / 180.0;
                var endRad = endAngle * Math.PI / 180.0;

                var arc = Arc.Create(center, radius, startRad, endRad, XYZ.BasisX, XYZ.BasisY);

                using (var trans = new Transaction(doc, "MCP Create Detail Arc"))
                {
                    trans.Start();

                    var detailCurve = doc.Create.NewDetailCurve(view, arc);

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

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = detailCurve?.Id?.Value ?? -1,
                            viewId = viewId.Value,
                            center = new { x = centerX, y = centerY, z = centerZ },
                            radius = radius,
                            startAngle = startAngle,
                            endAngle = endAngle
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // FILLED REGION METHODS
        // =====================================================

        /// <summary>
        /// Get all filled region types available in the document.
        /// </summary>
        public static string GetFilledRegionTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .Select(frt => new
                    {
                        id = frt.Id.Value,
                        name = frt.Name,
                        isMasking = frt.IsMasking
                    })
                    .OrderBy(t => t.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = types.Count,
                        types = types
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a rectangular filled region in a view.
        /// </summary>
        public static string CreateFilledRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var typeIdParam = parameters?["typeId"];
                var minX = parameters?["minX"]?.ToObject<double>() ?? 0;
                var minY = parameters?["minY"]?.ToObject<double>() ?? 0;
                var maxX = parameters?["maxX"]?.ToObject<double>() ?? 1;
                var maxY = parameters?["maxY"]?.ToObject<double>() ?? 1;
                var z = parameters?["z"]?.ToObject<double>() ?? 0;
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (typeIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var typeId = new ElementId(typeIdParam.ToObject<int>());

                XYZ p1, p2, p3, p4;
                if (useViewCoords && view.CropBox != null)
                {
                    var transform = view.CropBox.Transform;
                    p1 = transform.OfPoint(new XYZ(minX, minY, z));
                    p2 = transform.OfPoint(new XYZ(maxX, minY, z));
                    p3 = transform.OfPoint(new XYZ(maxX, maxY, z));
                    p4 = transform.OfPoint(new XYZ(minX, maxY, z));
                }
                else
                {
                    p1 = new XYZ(minX, minY, z);
                    p2 = new XYZ(maxX, minY, z);
                    p3 = new XYZ(maxX, maxY, z);
                    p4 = new XYZ(minX, maxY, z);
                }

                var curveLoop = new CurveLoop();
                curveLoop.Append(Line.CreateBound(p1, p2));
                curveLoop.Append(Line.CreateBound(p2, p3));
                curveLoop.Append(Line.CreateBound(p3, p4));
                curveLoop.Append(Line.CreateBound(p4, p1));

                var curveLoops = new List<CurveLoop> { curveLoop };

                using (var trans = new Transaction(doc, "MCP Create Filled Region"))
                {
                    trans.Start();

                    var filledRegion = FilledRegion.Create(doc, typeId, viewId, curveLoops);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = filledRegion?.Id?.Value ?? -1,
                            viewId = viewId.Value,
                            bounds = new { minX, minY, maxX, maxY }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // COMPOUND LAYER ANALYSIS METHODS
        // =====================================================

        /// <summary>
        /// Get the layer structure of a wall type.
        /// Returns all layers with material, thickness, and function.
        /// </summary>
        public static string GetWallTypeLayers(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wallIdParam = parameters?["wallId"];
                var typeIdParam = parameters?["typeId"];

                WallType wallType = null;

                if (wallIdParam != null)
                {
                    var wallId = new ElementId(wallIdParam.ToObject<int>());
                    var wall = doc.GetElement(wallId) as Wall;
                    if (wall != null)
                    {
                        wallType = wall.WallType;
                    }
                }
                else if (typeIdParam != null)
                {
                    var typeId = new ElementId(typeIdParam.ToObject<int>());
                    wallType = doc.GetElement(typeId) as WallType;
                }

                if (wallType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall or WallType not found. Provide wallId or typeId."
                    });
                }

                var structure = wallType.GetCompoundStructure();
                if (structure == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            typeId = wallType.Id.Value,
                            typeName = wallType.Name,
                            isCompound = false,
                            totalThickness = wallType.Width,
                            layers = new object[0]
                        }
                    });
                }

                var layers = new List<object>();
                double cumulativeOffset = 0;

                for (int i = 0; i < structure.LayerCount; i++)
                {
                    var layer = structure.GetLayers()[i];
                    var material = doc.GetElement(layer.MaterialId) as Material;

                    layers.Add(new
                    {
                        index = i,
                        function = layer.Function.ToString(),
                        thickness = layer.Width,
                        thicknessInches = layer.Width * 12,
                        materialId = layer.MaterialId?.Value ?? -1,
                        materialName = material?.Name ?? "None",
                        offsetFromExterior = cumulativeOffset,
                        offsetFromExteriorInches = cumulativeOffset * 12
                    });

                    cumulativeOffset += layer.Width;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        typeId = wallType.Id.Value,
                        typeName = wallType.Name,
                        isCompound = true,
                        totalThickness = structure.GetWidth(),
                        totalThicknessInches = structure.GetWidth() * 12,
                        layerCount = structure.LayerCount,
                        layers = layers
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the layer structure of a roof type.
        /// </summary>
        public static string GetRoofTypeLayers(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var roofIdParam = parameters?["roofId"];
                var typeIdParam = parameters?["typeId"];

                RoofType roofType = null;

                if (roofIdParam != null)
                {
                    var roofId = new ElementId(roofIdParam.ToObject<int>());
                    var roof = doc.GetElement(roofId) as RoofBase;
                    if (roof != null)
                    {
                        roofType = doc.GetElement(roof.GetTypeId()) as RoofType;
                    }
                }
                else if (typeIdParam != null)
                {
                    var typeId = new ElementId(typeIdParam.ToObject<int>());
                    roofType = doc.GetElement(typeId) as RoofType;
                }

                if (roofType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Roof or RoofType not found. Provide roofId or typeId."
                    });
                }

                var structure = roofType.GetCompoundStructure();
                if (structure == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            typeId = roofType.Id.Value,
                            typeName = roofType.Name,
                            isCompound = false,
                            layers = new object[0]
                        }
                    });
                }

                var layers = new List<object>();
                double cumulativeOffset = 0;

                for (int i = 0; i < structure.LayerCount; i++)
                {
                    var layer = structure.GetLayers()[i];
                    var material = doc.GetElement(layer.MaterialId) as Material;

                    layers.Add(new
                    {
                        index = i,
                        function = layer.Function.ToString(),
                        thickness = layer.Width,
                        thicknessInches = layer.Width * 12,
                        materialId = layer.MaterialId?.Value ?? -1,
                        materialName = material?.Name ?? "None",
                        offsetFromTop = cumulativeOffset,
                        offsetFromTopInches = cumulativeOffset * 12
                    });

                    cumulativeOffset += layer.Width;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        typeId = roofType.Id.Value,
                        typeName = roofType.Name,
                        isCompound = true,
                        totalThickness = structure.GetWidth(),
                        totalThicknessInches = structure.GetWidth() * 12,
                        layerCount = structure.LayerCount,
                        layers = layers
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the layer structure of a floor type.
        /// </summary>
        public static string GetFloorTypeLayers(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var floorIdParam = parameters?["floorId"];
                var typeIdParam = parameters?["typeId"];

                FloorType floorType = null;

                if (floorIdParam != null)
                {
                    var floorId = new ElementId(floorIdParam.ToObject<int>());
                    var floor = doc.GetElement(floorId) as Floor;
                    if (floor != null)
                    {
                        floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                    }
                }
                else if (typeIdParam != null)
                {
                    var typeId = new ElementId(typeIdParam.ToObject<int>());
                    floorType = doc.GetElement(typeId) as FloorType;
                }

                if (floorType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Floor or FloorType not found. Provide floorId or typeId."
                    });
                }

                var structure = floorType.GetCompoundStructure();
                if (structure == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            typeId = floorType.Id.Value,
                            typeName = floorType.Name,
                            isCompound = false,
                            layers = new object[0]
                        }
                    });
                }

                var layers = new List<object>();
                double cumulativeOffset = 0;

                for (int i = 0; i < structure.LayerCount; i++)
                {
                    var layer = structure.GetLayers()[i];
                    var material = doc.GetElement(layer.MaterialId) as Material;

                    layers.Add(new
                    {
                        index = i,
                        function = layer.Function.ToString(),
                        thickness = layer.Width,
                        thicknessInches = layer.Width * 12,
                        materialId = layer.MaterialId?.Value ?? -1,
                        materialName = material?.Name ?? "None",
                        offsetFromTop = cumulativeOffset,
                        offsetFromTopInches = cumulativeOffset * 12
                    });

                    cumulativeOffset += layer.Width;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        typeId = floorType.Id.Value,
                        typeName = floorType.Name,
                        isCompound = true,
                        totalThickness = structure.GetWidth(),
                        totalThicknessInches = structure.GetWidth() * 12,
                        layerCount = structure.LayerCount,
                        layers = layers
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // ELEMENT BOUNDING BOX METHODS
        // =====================================================

        /// <summary>
        /// Get the bounding box of an element in a specific view's coordinate system.
        /// </summary>
        public static string GetElementBoundingBoxInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdParam = parameters?["elementId"];
                var viewIdParam = parameters?["viewId"];

                if (elementIdParam == null || viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId and viewId parameters are required"
                    });
                }

                var elementId = new ElementId(elementIdParam.ToObject<int>());
                var viewId = new ElementId(viewIdParam.ToObject<int>());

                var element = doc.GetElement(elementId);
                var view = doc.GetElement(viewId) as View;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementId.Value} not found"
                    });
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                // Get bounding box in view
                var bbox = element.get_BoundingBox(view);
                if (bbox == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element has no bounding box in this view"
                    });
                }

                // Transform to view coordinates if crop box exists
                var minInView = bbox.Min;
                var maxInView = bbox.Max;

                if (view.CropBox != null)
                {
                    var transform = view.CropBox.Transform.Inverse;
                    minInView = transform.OfPoint(bbox.Min);
                    maxInView = transform.OfPoint(bbox.Max);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        elementId = elementId.Value,
                        elementName = element.Name,
                        viewId = viewId.Value,
                        viewName = view.Name,
                        modelCoords = new
                        {
                            min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                            max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                        },
                        viewCoords = new
                        {
                            min = new { x = minInView.X, y = minInView.Y, z = minInView.Z },
                            max = new { x = maxInView.X, y = maxInView.Y, z = maxInView.Z }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // ADVANCED FILLED REGION METHODS
        // =====================================================

        /// <summary>
        /// Create a filled region from an array of points (polygon).
        /// Points should be in order to form a closed loop.
        /// </summary>
        public static string CreateFilledRegionFromPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var typeIdParam = parameters?["typeId"];
                var pointsParam = parameters?["points"] as JArray;
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (typeIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId parameter is required"
                    });
                }

                if (pointsParam == null || pointsParam.Count < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "points array with at least 3 points is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var typeId = new ElementId(typeIdParam.ToObject<int>());

                // Parse points
                var points = new List<XYZ>();
                Transform transform = null;
                if (useViewCoords && view.CropBox != null)
                {
                    transform = view.CropBox.Transform;
                }

                foreach (var pt in pointsParam)
                {
                    var x = pt["x"]?.ToObject<double>() ?? 0;
                    var y = pt["y"]?.ToObject<double>() ?? 0;
                    var z = pt["z"]?.ToObject<double>() ?? 0;

                    var point = new XYZ(x, y, z);
                    if (transform != null)
                    {
                        point = transform.OfPoint(point);
                    }
                    points.Add(point);
                }

                // Create curve loop
                var curveLoop = new CurveLoop();
                for (int i = 0; i < points.Count; i++)
                {
                    var start = points[i];
                    var end = points[(i + 1) % points.Count];
                    curveLoop.Append(Line.CreateBound(start, end));
                }

                var curveLoops = new List<CurveLoop> { curveLoop };

                using (var trans = new Transaction(doc, "MCP Create Filled Region From Points"))
                {
                    trans.Start();

                    var filledRegion = FilledRegion.Create(doc, typeId, viewId, curveLoops);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = filledRegion?.Id?.Value ?? -1,
                            viewId = viewId.Value,
                            pointCount = points.Count
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all filled regions in a view.
        /// </summary>
        public static string GetFilledRegionsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var regions = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .Select(fr =>
                    {
                        var frType = doc.GetElement(fr.GetTypeId()) as FilledRegionType;
                        var bbox = fr.get_BoundingBox(view);
                        return new
                        {
                            id = fr.Id.Value,
                            typeName = frType?.Name ?? "Unknown",
                            typeId = fr.GetTypeId().Value,
                            boundingBox = bbox != null ? new
                            {
                                min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                                max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                            } : null
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        count = regions.Count,
                        regions = regions
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all detail lines/curves in a view.
        /// </summary>
        public static string GetDetailLinesInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                var lines = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(CurveElement))
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .Cast<CurveElement>()
                    .Where(ce => ce is DetailLine || ce is DetailArc || ce is DetailCurve)
                    .Select(ce =>
                    {
                        var curve = ce.GeometryCurve;
                        var start = curve.GetEndPoint(0);
                        var end = curve.GetEndPoint(1);
                        return new
                        {
                            id = ce.Id.Value,
                            curveType = ce.GetType().Name,
                            lineStyle = ce.LineStyle?.Name ?? "Unknown",
                            start = new { x = start.X, y = start.Y, z = start.Z },
                            end = new { x = end.X, y = end.Y, z = end.Z },
                            length = curve.Length
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        count = lines.Count,
                        lines = lines
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create multiple detail lines from an array of points (polyline).
        /// </summary>
        public static string CreateDetailLinesFromPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var pointsParam = parameters?["points"] as JArray;
                var lineStyleName = parameters?["lineStyle"]?.ToString();
                var closedLoop = parameters?["closedLoop"]?.ToObject<bool>() ?? false;
                var useViewCoords = parameters?["useViewCoords"]?.ToObject<bool>() ?? false;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (pointsParam == null || pointsParam.Count < 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "points array with at least 2 points is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                // Parse points
                var points = new List<XYZ>();
                Transform transform = null;
                if (useViewCoords && view.CropBox != null)
                {
                    transform = view.CropBox.Transform;
                }

                foreach (var pt in pointsParam)
                {
                    var x = pt["x"]?.ToObject<double>() ?? 0;
                    var y = pt["y"]?.ToObject<double>() ?? 0;
                    var z = pt["z"]?.ToObject<double>() ?? 0;

                    var point = new XYZ(x, y, z);
                    if (transform != null)
                    {
                        point = transform.OfPoint(point);
                    }
                    points.Add(point);
                }

                // Find line style
                GraphicsStyle lineStyle = null;
                if (!string.IsNullOrEmpty(lineStyleName))
                {
                    lineStyle = new FilteredElementCollector(doc)
                        .OfClass(typeof(GraphicsStyle))
                        .Cast<GraphicsStyle>()
                        .FirstOrDefault(gs => gs.Name.Equals(lineStyleName, StringComparison.OrdinalIgnoreCase));
                }

                var createdIds = new List<int>();

                using (var trans = new Transaction(doc, "MCP Create Detail Lines From Points"))
                {
                    trans.Start();

                    int segmentCount = closedLoop ? points.Count : points.Count - 1;

                    for (int i = 0; i < segmentCount; i++)
                    {
                        var start = points[i];
                        var end = points[(i + 1) % points.Count];

                        var line = Line.CreateBound(start, end);
                        var detailCurve = doc.Create.NewDetailCurve(view, line);

                        if (lineStyle != null && detailCurve != null)
                        {
                            detailCurve.LineStyle = lineStyle;
                        }

                        if (detailCurve != null)
                        {
                            createdIds.Add((int)detailCurve.Id.Value);
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            viewId = viewId.Value,
                            lineCount = createdIds.Count,
                            closedLoop = closedLoop,
                            lineIds = createdIds
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // COMPREHENSIVE VIEW ANALYSIS
        // =====================================================

        /// <summary>
        /// Analyze a detail view comprehensively.
        /// Returns all elements, their positions, and what might be missing.
        /// </summary>
        public static string AnalyzeDetailView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                // Get crop region info
                var cropInfo = new
                {
                    hasCropBox = view.CropBox != null,
                    cropBoxActive = view.CropBoxActive
                };

                BoundingBoxXYZ cropBox = null;
                object cropRegion = null;
                if (view.CropBox != null)
                {
                    cropBox = view.CropBox;
                    cropRegion = new
                    {
                        minX = cropBox.Min.X,
                        minY = cropBox.Min.Y,
                        maxX = cropBox.Max.X,
                        maxY = cropBox.Max.Y,
                        width = cropBox.Max.X - cropBox.Min.X,
                        height = cropBox.Max.Y - cropBox.Min.Y
                    };
                }

                // Count elements by category
                var walls = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var roofs = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Roofs)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var floors = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var detailComponents = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var detailLines = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(CurveElement))
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .ToElements();

                var filledRegions = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .ToElements();

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(TextNote))
                    .ToElements();

                var dimensions = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(Dimension))
                    .ToElements();

                // Get wall types and their layers
                var wallTypesWithLayers = walls
                    .Cast<Wall>()
                    .Select(w => w.WallType)
                    .Distinct()
                    .Select(wt =>
                    {
                        var structure = wt.GetCompoundStructure();
                        return new
                        {
                            typeId = wt.Id.Value,
                            typeName = wt.Name,
                            layerCount = structure?.LayerCount ?? 0,
                            totalThickness = structure?.GetWidth() ?? wt.Width
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        cropRegion = cropRegion,
                        elementCounts = new
                        {
                            walls = walls.Count,
                            roofs = roofs.Count,
                            floors = floors.Count,
                            detailComponents = detailComponents.Count,
                            detailLines = detailLines.Count,
                            filledRegions = filledRegions.Count,
                            textNotes = textNotes.Count,
                            dimensions = dimensions.Count
                        },
                        wallTypes = wallTypesWithLayers,
                        recommendations = new List<string>
                        {
                            detailComponents.Count == 0 ? "No detail components - consider adding break lines, insulation symbols" : null,
                            filledRegions.Count == 0 ? "No filled regions - consider adding material hatching" : null,
                            dimensions.Count == 0 ? "No dimensions - consider adding key dimensions" : null
                        }.Where(r => r != null).ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================
        // SCALE DETAIL VIEW ELEMENTS
        // =====================================================

        /// <summary>
        /// Scale all elements in a drafting view by a given factor.
        /// This is useful when a detail was drawn at the wrong scale.
        /// </summary>
        /// <param name="viewId">The drafting view ID</param>
        /// <param name="scaleFactor">The scale factor (e.g., 2.0 = double size)</param>
        /// <param name="centerX">Optional center X for scaling (default: centroid of elements)</param>
        /// <param name="centerY">Optional center Y for scaling (default: centroid of elements)</param>
        public static string ScaleDetailViewElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var scaleFactorParam = parameters?["scaleFactor"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (scaleFactorParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scaleFactor parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var scaleFactor = scaleFactorParam.ToObject<double>();
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found"
                    });
                }

                // Only allow drafting views for now
                if (view.ViewType != ViewType.DraftingView)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"This method only works with Drafting Views. Current view type: {view.ViewType}"
                    });
                }

                // Collect all elements in the view
                var detailLines = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(CurveElement))
                    .Cast<CurveElement>()
                    .ToList();

                var filledRegions = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .ToList();

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .ToList();

                var detailComponents = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                // Calculate centroid of all elements (or use provided center)
                var allPoints = new List<XYZ>();

                foreach (var line in detailLines)
                {
                    try
                    {
                        var curve = line.GeometryCurve;
                        if (curve != null)
                        {
                            allPoints.Add(curve.GetEndPoint(0));
                            allPoints.Add(curve.GetEndPoint(1));
                        }
                    }
                    catch { }
                }

                foreach (var text in textNotes)
                {
                    try
                    {
                        allPoints.Add(text.Coord);
                    }
                    catch { }
                }

                foreach (var comp in detailComponents)
                {
                    try
                    {
                        var loc = comp.Location as LocationPoint;
                        if (loc != null)
                        {
                            allPoints.Add(loc.Point);
                        }
                    }
                    catch { }
                }

                if (allPoints.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No elements found in view to scale"
                    });
                }

                // Calculate centroid
                double centerX = parameters?["centerX"]?.ToObject<double>() ?? allPoints.Average(p => p.X);
                double centerY = parameters?["centerY"]?.ToObject<double>() ?? allPoints.Average(p => p.Y);
                var center = new XYZ(centerX, centerY, 0);

                int linesScaled = 0;
                int filledRegionsScaled = 0;
                int textNotesScaled = 0;
                int detailComponentsScaled = 0;
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Scale Detail View Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        // Scale detail lines by recreating them
                        foreach (var line in detailLines)
                        {
                            try
                            {
                                var curve = line.GeometryCurve;
                                if (curve == null) continue;

                                var graphicsStyle = line.LineStyle as GraphicsStyle;
                                var startPoint = curve.GetEndPoint(0);
                                var endPoint = curve.GetEndPoint(1);

                                // Calculate scaled points
                                var newStart = ScalePoint(startPoint, center, scaleFactor);
                                var newEnd = ScalePoint(endPoint, center, scaleFactor);

                                // Create new line
                                Line newLine = null;
                                try
                                {
                                    newLine = Line.CreateBound(newStart, newEnd);
                                }
                                catch
                                {
                                    // If line creation fails (zero length?), skip
                                    continue;
                                }

                                // Delete old line
                                doc.Delete(line.Id);

                                // Create new detail line
                                var newDetailLine = doc.Create.NewDetailCurve(view, newLine);
                                if (graphicsStyle != null && newDetailLine != null)
                                {
                                    newDetailLine.LineStyle = graphicsStyle;
                                }

                                linesScaled++;
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Line {line.Id.Value}: {ex.Message}");
                            }
                        }

                        // Scale filled regions by recreating them
                        foreach (var region in filledRegions)
                        {
                            try
                            {
                                var typeId = region.GetTypeId();
                                var boundaries = region.GetBoundaries();

                                if (boundaries == null || boundaries.Count == 0) continue;

                                var scaledBoundaries = new List<CurveLoop>();
                                foreach (var boundary in boundaries)
                                {
                                    var scaledLoop = new CurveLoop();
                                    foreach (var curve in boundary)
                                    {
                                        var startPoint = curve.GetEndPoint(0);
                                        var endPoint = curve.GetEndPoint(1);
                                        var newStart = ScalePoint(startPoint, center, scaleFactor);
                                        var newEnd = ScalePoint(endPoint, center, scaleFactor);

                                        try
                                        {
                                            scaledLoop.Append(Line.CreateBound(newStart, newEnd));
                                        }
                                        catch { }
                                    }
                                    if (scaledLoop.Count() > 0)
                                    {
                                        scaledBoundaries.Add(scaledLoop);
                                    }
                                }

                                if (scaledBoundaries.Count > 0)
                                {
                                    // Delete old region
                                    doc.Delete(region.Id);

                                    // Create new region
                                    FilledRegion.Create(doc, typeId, viewId, scaledBoundaries);
                                    filledRegionsScaled++;
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"FilledRegion {region.Id.Value}: {ex.Message}");
                            }
                        }

                        // Scale text notes by moving them
                        foreach (var text in textNotes)
                        {
                            try
                            {
                                var coord = text.Coord;
                                var newCoord = ScalePoint(coord, center, scaleFactor);
                                var moveVector = newCoord - coord;
                                ElementTransformUtils.MoveElement(doc, text.Id, moveVector);
                                textNotesScaled++;
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"TextNote {text.Id.Value}: {ex.Message}");
                            }
                        }

                        // Scale detail components by moving them
                        foreach (var comp in detailComponents)
                        {
                            try
                            {
                                var loc = comp.Location as LocationPoint;
                                if (loc != null)
                                {
                                    var point = loc.Point;
                                    var newPoint = ScalePoint(point, center, scaleFactor);
                                    var moveVector = newPoint - point;
                                    ElementTransformUtils.MoveElement(doc, comp.Id, moveVector);
                                    detailComponentsScaled++;
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"DetailComponent {comp.Id.Value}: {ex.Message}");
                            }
                        }

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = new
                            {
                                viewId = viewId.Value,
                                viewName = view.Name,
                                scaleFactor = scaleFactor,
                                center = new { x = centerX, y = centerY },
                                scaled = new
                                {
                                    detailLines = linesScaled,
                                    filledRegions = filledRegionsScaled,
                                    textNotes = textNotesScaled,
                                    detailComponents = detailComponentsScaled,
                                    total = linesScaled + filledRegionsScaled + textNotesScaled + detailComponentsScaled
                                },
                                errors = errors.Count > 0 ? errors.Take(10).ToList() : null,
                                errorCount = errors.Count
                            }
                        });
                    }
                    catch (Exception)
                    {
                        trans.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper method to scale a point from a center point by a factor
        /// </summary>
        private static XYZ ScalePoint(XYZ point, XYZ center, double factor)
        {
            return new XYZ(
                center.X + (point.X - center.X) * factor,
                center.Y + (point.Y - center.Y) * factor,
                point.Z  // Keep Z the same for 2D drafting views
            );
        }

        /// <summary>
        /// Mirror all elements in a drafting view about an axis.
        /// Axis can be "horizontal", "vertical", or custom line defined by two points.
        /// </summary>
        public static string MirrorDetailViewElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var axisType = parameters?["axis"]?.ToString()?.ToLower() ?? "vertical";

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null || view.ViewType != ViewType.DraftingView)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Valid drafting view required"
                    });
                }

                // Collect all elements
                var detailLines = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(CurveElement))
                    .Cast<CurveElement>()
                    .ToList();

                var filledRegions = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .ToList();

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .ToList();

                var detailComponents = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                // Calculate center of all elements
                var allPoints = new List<XYZ>();
                foreach (var line in detailLines)
                {
                    try
                    {
                        var curve = line.GeometryCurve;
                        if (curve != null)
                        {
                            allPoints.Add(curve.GetEndPoint(0));
                            allPoints.Add(curve.GetEndPoint(1));
                        }
                    }
                    catch { }
                }

                if (allPoints.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No elements found in view"
                    });
                }

                double centerX = parameters?["centerX"]?.ToObject<double>() ?? allPoints.Average(p => p.X);
                double centerY = parameters?["centerY"]?.ToObject<double>() ?? allPoints.Average(p => p.Y);

                int linesMirrored = 0, filledRegionsMirrored = 0, textNotesMirrored = 0, detailComponentsMirrored = 0;

                using (var trans = new Transaction(doc, "Mirror Detail View Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Mirror detail lines
                    foreach (var line in detailLines)
                    {
                        try
                        {
                            var curve = line.GeometryCurve;
                            if (curve == null) continue;

                            var graphicsStyle = line.LineStyle as GraphicsStyle;
                            var startPoint = curve.GetEndPoint(0);
                            var endPoint = curve.GetEndPoint(1);

                            var newStart = MirrorPoint(startPoint, centerX, centerY, axisType);
                            var newEnd = MirrorPoint(endPoint, centerX, centerY, axisType);

                            Line newLine = Line.CreateBound(newStart, newEnd);
                            doc.Delete(line.Id);
                            var newDetailLine = doc.Create.NewDetailCurve(view, newLine);
                            if (graphicsStyle != null) newDetailLine.LineStyle = graphicsStyle;
                            linesMirrored++;
                        }
                        catch { }
                    }

                    // Mirror filled regions
                    foreach (var region in filledRegions)
                    {
                        try
                        {
                            var typeId = region.GetTypeId();
                            var boundaries = region.GetBoundaries();
                            if (boundaries == null || boundaries.Count == 0) continue;

                            var mirroredBoundaries = new List<CurveLoop>();
                            foreach (var boundary in boundaries)
                            {
                                var mirroredLoop = new CurveLoop();
                                var curves = boundary.ToList();
                                // Reverse order for mirrored regions
                                for (int i = curves.Count - 1; i >= 0; i--)
                                {
                                    var curve = curves[i];
                                    var start = MirrorPoint(curve.GetEndPoint(1), centerX, centerY, axisType);
                                    var end = MirrorPoint(curve.GetEndPoint(0), centerX, centerY, axisType);
                                    mirroredLoop.Append(Line.CreateBound(start, end));
                                }
                                mirroredBoundaries.Add(mirroredLoop);
                            }

                            doc.Delete(region.Id);
                            FilledRegion.Create(doc, typeId, viewId, mirroredBoundaries);
                            filledRegionsMirrored++;
                        }
                        catch { }
                    }

                    // Mirror text notes
                    foreach (var text in textNotes)
                    {
                        try
                        {
                            var coord = text.Coord;
                            var newCoord = MirrorPoint(coord, centerX, centerY, axisType);
                            var moveVec = newCoord - coord;
                            ElementTransformUtils.MoveElement(doc, text.Id, moveVec);
                            textNotesMirrored++;
                        }
                        catch { }
                    }

                    // Mirror detail components
                    foreach (var comp in detailComponents)
                    {
                        try
                        {
                            var loc = comp.Location as LocationPoint;
                            if (loc == null) continue;
                            var newPoint = MirrorPoint(loc.Point, centerX, centerY, axisType);
                            var moveVec = newPoint - loc.Point;
                            ElementTransformUtils.MoveElement(doc, comp.Id, moveVec);
                            detailComponentsMirrored++;
                        }
                        catch { }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        axis = axisType,
                        center = new { x = centerX, y = centerY },
                        mirrored = new
                        {
                            detailLines = linesMirrored,
                            filledRegions = filledRegionsMirrored,
                            textNotes = textNotesMirrored,
                            detailComponents = detailComponentsMirrored,
                            total = linesMirrored + filledRegionsMirrored + textNotesMirrored + detailComponentsMirrored
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper method to mirror a point about an axis
        /// </summary>
        private static XYZ MirrorPoint(XYZ point, double centerX, double centerY, string axisType)
        {
            switch (axisType)
            {
                case "vertical":
                    return new XYZ(2 * centerX - point.X, point.Y, point.Z);
                case "horizontal":
                    return new XYZ(point.X, 2 * centerY - point.Y, point.Z);
                default:
                    return new XYZ(2 * centerX - point.X, point.Y, point.Z);
            }
        }

        /// <summary>
        /// Rotate all elements in a drafting view about a center point.
        /// </summary>
        public static string RotateDetailViewElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var angleParam = parameters?["angle"]; // in degrees

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (angleParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "angle parameter is required (in degrees)"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var angleDegrees = angleParam.ToObject<double>();
                var angleRadians = angleDegrees * Math.PI / 180.0;
                var view = doc.GetElement(viewId) as View;

                if (view == null || view.ViewType != ViewType.DraftingView)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Valid drafting view required"
                    });
                }

                // Collect all elements
                var detailLines = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(CurveElement))
                    .Cast<CurveElement>()
                    .ToList();

                var filledRegions = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .ToList();

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .ToList();

                var detailComponents = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_DetailComponents)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                // Calculate center
                var allPoints = new List<XYZ>();
                foreach (var line in detailLines)
                {
                    try
                    {
                        var curve = line.GeometryCurve;
                        if (curve != null)
                        {
                            allPoints.Add(curve.GetEndPoint(0));
                            allPoints.Add(curve.GetEndPoint(1));
                        }
                    }
                    catch { }
                }

                if (allPoints.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No elements found in view"
                    });
                }

                double centerX = parameters?["centerX"]?.ToObject<double>() ?? allPoints.Average(p => p.X);
                double centerY = parameters?["centerY"]?.ToObject<double>() ?? allPoints.Average(p => p.Y);
                var center = new XYZ(centerX, centerY, 0);

                int linesRotated = 0, filledRegionsRotated = 0, textNotesRotated = 0, detailComponentsRotated = 0;

                using (var trans = new Transaction(doc, "Rotate Detail View Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Rotate detail lines
                    foreach (var line in detailLines)
                    {
                        try
                        {
                            var curve = line.GeometryCurve;
                            if (curve == null) continue;

                            var graphicsStyle = line.LineStyle as GraphicsStyle;
                            var startPoint = curve.GetEndPoint(0);
                            var endPoint = curve.GetEndPoint(1);

                            var newStart = RotatePoint(startPoint, center, angleRadians);
                            var newEnd = RotatePoint(endPoint, center, angleRadians);

                            Line newLine = Line.CreateBound(newStart, newEnd);
                            doc.Delete(line.Id);
                            var newDetailLine = doc.Create.NewDetailCurve(view, newLine);
                            if (graphicsStyle != null) newDetailLine.LineStyle = graphicsStyle;
                            linesRotated++;
                        }
                        catch { }
                    }

                    // Rotate filled regions
                    foreach (var region in filledRegions)
                    {
                        try
                        {
                            var typeId = region.GetTypeId();
                            var boundaries = region.GetBoundaries();
                            if (boundaries == null || boundaries.Count == 0) continue;

                            var rotatedBoundaries = new List<CurveLoop>();
                            foreach (var boundary in boundaries)
                            {
                                var rotatedLoop = new CurveLoop();
                                foreach (var curve in boundary)
                                {
                                    var start = RotatePoint(curve.GetEndPoint(0), center, angleRadians);
                                    var end = RotatePoint(curve.GetEndPoint(1), center, angleRadians);
                                    rotatedLoop.Append(Line.CreateBound(start, end));
                                }
                                rotatedBoundaries.Add(rotatedLoop);
                            }

                            doc.Delete(region.Id);
                            FilledRegion.Create(doc, typeId, viewId, rotatedBoundaries);
                            filledRegionsRotated++;
                        }
                        catch { }
                    }

                    // Rotate text notes
                    foreach (var text in textNotes)
                    {
                        try
                        {
                            var coord = text.Coord;
                            var newCoord = RotatePoint(coord, center, angleRadians);
                            var moveVec = newCoord - coord;
                            ElementTransformUtils.MoveElement(doc, text.Id, moveVec);
                            textNotesRotated++;
                        }
                        catch { }
                    }

                    // Rotate detail components
                    foreach (var comp in detailComponents)
                    {
                        try
                        {
                            var loc = comp.Location as LocationPoint;
                            if (loc == null) continue;
                            var newPoint = RotatePoint(loc.Point, center, angleRadians);
                            var moveVec = newPoint - loc.Point;
                            ElementTransformUtils.MoveElement(doc, comp.Id, moveVec);
                            detailComponentsRotated++;
                        }
                        catch { }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        angleDegrees = angleDegrees,
                        center = new { x = centerX, y = centerY },
                        rotated = new
                        {
                            detailLines = linesRotated,
                            filledRegions = filledRegionsRotated,
                            textNotes = textNotesRotated,
                            detailComponents = detailComponentsRotated,
                            total = linesRotated + filledRegionsRotated + textNotesRotated + detailComponentsRotated
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper method to rotate a point about a center
        /// </summary>
        private static XYZ RotatePoint(XYZ point, XYZ center, double angleRadians)
        {
            double cos = Math.Cos(angleRadians);
            double sin = Math.Sin(angleRadians);
            double dx = point.X - center.X;
            double dy = point.Y - center.Y;
            return new XYZ(
                center.X + dx * cos - dy * sin,
                center.Y + dx * sin + dy * cos,
                point.Z
            );
        }

        /// <summary>
        /// Align elements in a view to a common edge or center.
        /// Works on selected element IDs or all elements of a type.
        /// </summary>
        public static string AlignElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdsParam = parameters?["elementIds"] as JArray;
                var alignTo = parameters?["alignTo"]?.ToString()?.ToLower() ?? "left"; // left, right, top, bottom, centerH, centerV
                var referenceIdParam = parameters?["referenceId"]; // Optional: align to this element

                if (elementIdsParam == null || elementIdsParam.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var elementIds = elementIdsParam.Select(id => new ElementId(id.ToObject<int>())).ToList();

                // Get bounding boxes of all elements
                var elementBounds = new Dictionary<ElementId, BoundingBoxXYZ>();
                foreach (var id in elementIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem != null)
                    {
                        var bb = elem.get_BoundingBox(null);
                        if (bb != null)
                        {
                            elementBounds[id] = bb;
                        }
                    }
                }

                if (elementBounds.Count < 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Need at least 2 elements with bounding boxes to align"
                    });
                }

                // Determine reference value
                double referenceValue = 0;
                if (referenceIdParam != null)
                {
                    var refId = new ElementId(referenceIdParam.ToObject<int>());
                    if (elementBounds.ContainsKey(refId))
                    {
                        var refBB = elementBounds[refId];
                        referenceValue = GetAlignmentValue(refBB, alignTo);
                    }
                }
                else
                {
                    // Use first element as reference
                    var firstBB = elementBounds.Values.First();
                    referenceValue = GetAlignmentValue(firstBB, alignTo);
                }

                int alignedCount = 0;

                using (var trans = new Transaction(doc, "Align Elements"))
                {
                    trans.Start();

                    foreach (var kvp in elementBounds)
                    {
                        var elemId = kvp.Key;
                        var bb = kvp.Value;
                        var currentValue = GetAlignmentValue(bb, alignTo);
                        var delta = referenceValue - currentValue;

                        if (Math.Abs(delta) > 0.001) // Only move if needed
                        {
                            XYZ moveVec;
                            if (alignTo == "left" || alignTo == "right" || alignTo == "centerh")
                            {
                                moveVec = new XYZ(delta, 0, 0);
                            }
                            else
                            {
                                moveVec = new XYZ(0, delta, 0);
                            }

                            try
                            {
                                ElementTransformUtils.MoveElement(doc, elemId, moveVec);
                                alignedCount++;
                            }
                            catch { }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        alignedCount = alignedCount,
                        alignTo = alignTo,
                        referenceValue = referenceValue
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static double GetAlignmentValue(BoundingBoxXYZ bb, string alignTo)
        {
            switch (alignTo)
            {
                case "left": return bb.Min.X;
                case "right": return bb.Max.X;
                case "top": return bb.Max.Y;
                case "bottom": return bb.Min.Y;
                case "centerh": return (bb.Min.X + bb.Max.X) / 2;
                case "centerv": return (bb.Min.Y + bb.Max.Y) / 2;
                default: return bb.Min.X;
            }
        }

        /// <summary>
        /// Distribute elements evenly between the first and last element.
        /// </summary>
        public static string DistributeElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIdsParam = parameters?["elementIds"] as JArray;
                var direction = parameters?["direction"]?.ToString()?.ToLower() ?? "horizontal"; // horizontal or vertical

                if (elementIdsParam == null || elementIdsParam.Count < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array with at least 3 elements is required"
                    });
                }

                var elementIds = elementIdsParam.Select(id => new ElementId(id.ToObject<int>())).ToList();

                // Get centers of all elements
                var elementCenters = new List<(ElementId id, double center)>();
                foreach (var id in elementIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem != null)
                    {
                        var bb = elem.get_BoundingBox(null);
                        if (bb != null)
                        {
                            double center = direction == "horizontal"
                                ? (bb.Min.X + bb.Max.X) / 2
                                : (bb.Min.Y + bb.Max.Y) / 2;
                            elementCenters.Add((id, center));
                        }
                    }
                }

                if (elementCenters.Count < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Need at least 3 elements with bounding boxes to distribute"
                    });
                }

                // Sort by current position
                elementCenters = elementCenters.OrderBy(e => e.center).ToList();

                // Calculate even spacing
                double firstCenter = elementCenters.First().center;
                double lastCenter = elementCenters.Last().center;
                double totalSpan = lastCenter - firstCenter;
                double spacing = totalSpan / (elementCenters.Count - 1);

                int distributedCount = 0;

                using (var trans = new Transaction(doc, "Distribute Elements"))
                {
                    trans.Start();

                    for (int i = 1; i < elementCenters.Count - 1; i++) // Skip first and last
                    {
                        var targetCenter = firstCenter + spacing * i;
                        var currentCenter = elementCenters[i].center;
                        var delta = targetCenter - currentCenter;

                        if (Math.Abs(delta) > 0.001)
                        {
                            XYZ moveVec = direction == "horizontal"
                                ? new XYZ(delta, 0, 0)
                                : new XYZ(0, delta, 0);

                            try
                            {
                                ElementTransformUtils.MoveElement(doc, elementCenters[i].id, moveVec);
                                distributedCount++;
                            }
                            catch { }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        distributedCount = distributedCount,
                        direction = direction,
                        spacing = spacing
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Find duplicate or overlapping elements in the model.
        /// Checks walls, columns, beams, detail lines for identical positions.
        /// </summary>
        public static string FindDuplicateElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryName = parameters?["category"]?.ToString() ?? "Walls";
                var tolerance = parameters?["tolerance"]?.ToObject<double>() ?? 0.01; // 1/8" default
                var viewIdParam = parameters?["viewId"];

                BuiltInCategory category;
                switch (categoryName.ToLower())
                {
                    case "walls": category = BuiltInCategory.OST_Walls; break;
                    case "columns": category = BuiltInCategory.OST_Columns; break;
                    case "beams": category = BuiltInCategory.OST_StructuralFraming; break;
                    case "doors": category = BuiltInCategory.OST_Doors; break;
                    case "windows": category = BuiltInCategory.OST_Windows; break;
                    case "detaillines": category = BuiltInCategory.OST_Lines; break;
                    default: category = BuiltInCategory.OST_Walls; break;
                }

                FilteredElementCollector collector;
                if (viewIdParam != null)
                {
                    var viewId = new ElementId(viewIdParam.ToObject<int>());
                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var elements = collector
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToList();

                var duplicates = new List<object>();
                var checkedPairs = new HashSet<string>();

                for (int i = 0; i < elements.Count; i++)
                {
                    var elem1 = elements[i];
                    var bb1 = elem1.get_BoundingBox(null);
                    if (bb1 == null) continue;

                    for (int j = i + 1; j < elements.Count; j++)
                    {
                        var elem2 = elements[j];
                        var bb2 = elem2.get_BoundingBox(null);
                        if (bb2 == null) continue;

                        // Check if bounding boxes overlap significantly
                        var center1 = (bb1.Min + bb1.Max) / 2;
                        var center2 = (bb2.Min + bb2.Max) / 2;
                        var distance = center1.DistanceTo(center2);

                        if (distance < tolerance)
                        {
                            var pairKey = $"{Math.Min(elem1.Id.Value, elem2.Id.Value)}_{Math.Max(elem1.Id.Value, elem2.Id.Value)}";
                            if (!checkedPairs.Contains(pairKey))
                            {
                                checkedPairs.Add(pairKey);
                                duplicates.Add(new
                                {
                                    element1Id = elem1.Id.Value,
                                    element2Id = elem2.Id.Value,
                                    distance = distance,
                                    location = new { x = center1.X, y = center1.Y, z = center1.Z }
                                });
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        category = categoryName,
                        totalElements = elements.Count,
                        duplicatesFound = duplicates.Count,
                        duplicates = duplicates.Take(100).ToList() // Limit to first 100
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Automatically tag all untagged elements of a category in a view.
        /// Uses smart placement to avoid overlapping existing tags.
        /// </summary>
        public static string AutoTagUntagged(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var categoryName = parameters?["category"]?.ToString() ?? "Doors";
                var tagOffset = parameters?["offset"]?.ToObject<double>() ?? 2.0; // 2 feet default

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                BuiltInCategory elemCategory;
                BuiltInCategory tagCategory;
                switch (categoryName.ToLower())
                {
                    case "doors":
                        elemCategory = BuiltInCategory.OST_Doors;
                        tagCategory = BuiltInCategory.OST_DoorTags;
                        break;
                    case "windows":
                        elemCategory = BuiltInCategory.OST_Windows;
                        tagCategory = BuiltInCategory.OST_WindowTags;
                        break;
                    case "rooms":
                        elemCategory = BuiltInCategory.OST_Rooms;
                        tagCategory = BuiltInCategory.OST_RoomTags;
                        break;
                    case "walls":
                        elemCategory = BuiltInCategory.OST_Walls;
                        tagCategory = BuiltInCategory.OST_WallTags;
                        break;
                    default:
                        elemCategory = BuiltInCategory.OST_Doors;
                        tagCategory = BuiltInCategory.OST_DoorTags;
                        break;
                }

                // Get all elements in view
                var elements = new FilteredElementCollector(doc, viewId)
                    .OfCategory(elemCategory)
                    .WhereElementIsNotElementType()
                    .ToList();

                // Get existing tags
                var existingTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(tagCategory)
                    .WhereElementIsNotElementType()
                    .Cast<IndependentTag>()
                    .ToList();

                // Find which elements are already tagged
                var taggedElementIds = new HashSet<long>();
                foreach (var tag in existingTags)
                {
                    try
                    {
                        var taggedIds = tag.GetTaggedLocalElementIds();
                        foreach (var id in taggedIds)
                        {
                            taggedElementIds.Add(id.Value);
                        }
                    }
                    catch { }
                }

                // Get existing tag positions for collision avoidance
                var existingTagPositions = new List<XYZ>();
                foreach (var tag in existingTags)
                {
                    try
                    {
                        var bb = tag.get_BoundingBox(view);
                        if (bb != null)
                        {
                            existingTagPositions.Add((bb.Min + bb.Max) / 2);
                        }
                    }
                    catch { }
                }

                int tagsPlaced = 0;
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Auto Tag Untagged Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var elem in elements)
                    {
                        if (taggedElementIds.Contains(elem.Id.Value))
                            continue; // Already tagged

                        try
                        {
                            var bb = elem.get_BoundingBox(view);
                            if (bb == null) continue;

                            var center = (bb.Min + bb.Max) / 2;

                            // Find best tag position avoiding existing tags
                            var tagPosition = FindBestTagPosition(center, existingTagPositions, tagOffset);
                            existingTagPositions.Add(tagPosition);

                            var reference = new Reference(elem);
                            var tag = IndependentTag.Create(doc, viewId, reference, false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, tagPosition);

                            if (tag != null)
                            {
                                tagsPlaced++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Element {elem.Id.Value}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        category = categoryName,
                        totalElements = elements.Count,
                        alreadyTagged = taggedElementIds.Count,
                        tagsPlaced = tagsPlaced,
                        errors = errors.Count > 0 ? errors.Take(10).ToList() : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static XYZ FindBestTagPosition(XYZ elementCenter, List<XYZ> existingPositions, double offset)
        {
            // Try different positions around the element
            var offsets = new XYZ[]
            {
                new XYZ(0, offset, 0),      // Above
                new XYZ(offset, 0, 0),       // Right
                new XYZ(0, -offset, 0),      // Below
                new XYZ(-offset, 0, 0),      // Left
                new XYZ(offset, offset, 0),  // Top-right
                new XYZ(-offset, offset, 0), // Top-left
            };

            foreach (var off in offsets)
            {
                var candidatePos = elementCenter + off;
                bool hasConflict = false;

                foreach (var existing in existingPositions)
                {
                    if (candidatePos.DistanceTo(existing) < offset * 0.5)
                    {
                        hasConflict = true;
                        break;
                    }
                }

                if (!hasConflict)
                {
                    return candidatePos;
                }
            }

            // If all positions have conflicts, return above with slight random offset
            return elementCenter + new XYZ(offset * 0.3, offset, 0);
        }

        /// <summary>
        /// Offset detail curves (lines, arcs) by a specified distance.
        /// Creates new curves parallel to existing ones.
        /// </summary>
        public static string OffsetDetailCurves(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var offsetDistance = parameters?["distance"]?.ToObject<double>() ?? 0.5; // 6" default
                var side = parameters?["side"]?.ToString()?.ToLower() ?? "left"; // left or right
                var elementIdsParam = parameters?["elementIds"] as JArray;

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                List<CurveElement> curves;
                if (elementIdsParam != null && elementIdsParam.Count > 0)
                {
                    // Use specified elements
                    curves = elementIdsParam
                        .Select(id => doc.GetElement(new ElementId(id.ToObject<int>())) as CurveElement)
                        .Where(c => c != null)
                        .ToList();
                }
                else
                {
                    // Get all detail lines in view
                    curves = new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(CurveElement))
                        .Cast<CurveElement>()
                        .ToList();
                }

                int offsetCount = 0;
                var newCurveIds = new List<int>();

                using (var trans = new Transaction(doc, "Offset Detail Curves"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var curveElem in curves)
                    {
                        try
                        {
                            var curve = curveElem.GeometryCurve;
                            if (curve == null) continue;

                            var graphicsStyle = curveElem.LineStyle as GraphicsStyle;

                            // Get curve direction and perpendicular
                            var start = curve.GetEndPoint(0);
                            var end = curve.GetEndPoint(1);
                            var direction = (end - start).Normalize();
                            var perpendicular = new XYZ(-direction.Y, direction.X, 0);

                            if (side == "right")
                            {
                                perpendicular = perpendicular.Negate();
                            }

                            var offsetVec = perpendicular * offsetDistance;
                            var newStart = start + offsetVec;
                            var newEnd = end + offsetVec;

                            var newCurve = Line.CreateBound(newStart, newEnd);
                            var newDetailCurve = doc.Create.NewDetailCurve(view, newCurve);

                            if (graphicsStyle != null)
                            {
                                newDetailCurve.LineStyle = graphicsStyle;
                            }

                            newCurveIds.Add((int)newDetailCurve.Id.Value);
                            offsetCount++;
                        }
                        catch { }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        originalCurves = curves.Count,
                        offsetCreated = offsetCount,
                        offsetDistance = offsetDistance,
                        side = side,
                        newCurveIds = newCurveIds
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Update parameters conditionally based on rules.
        /// Example: If room area > 200 SF, set "Large Room" = true
        /// </summary>
        public static string ConditionalParameterUpdate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryName = parameters?["category"]?.ToString() ?? "Rooms";
                var conditionParam = parameters?["conditionParameter"]?.ToString();
                var conditionOperator = parameters?["operator"]?.ToString() ?? ">";
                var conditionValue = parameters?["conditionValue"]?.ToObject<double>() ?? 0;
                var targetParam = parameters?["targetParameter"]?.ToString();
                var targetValue = parameters?["targetValue"]?.ToString();

                if (string.IsNullOrEmpty(conditionParam) || string.IsNullOrEmpty(targetParam))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "conditionParameter and targetParameter are required"
                    });
                }

                BuiltInCategory category;
                switch (categoryName.ToLower())
                {
                    case "rooms": category = BuiltInCategory.OST_Rooms; break;
                    case "doors": category = BuiltInCategory.OST_Doors; break;
                    case "windows": category = BuiltInCategory.OST_Windows; break;
                    case "walls": category = BuiltInCategory.OST_Walls; break;
                    case "floors": category = BuiltInCategory.OST_Floors; break;
                    default: category = BuiltInCategory.OST_Rooms; break;
                }

                var elements = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToList();

                int updatedCount = 0;
                int matchedCount = 0;
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Conditional Parameter Update"))
                {
                    trans.Start();

                    foreach (var elem in elements)
                    {
                        try
                        {
                            // Get condition parameter value
                            var condParam = elem.LookupParameter(conditionParam);
                            if (condParam == null) continue;

                            double paramValue = 0;
                            if (condParam.StorageType == StorageType.Double)
                            {
                                paramValue = condParam.AsDouble();
                            }
                            else if (condParam.StorageType == StorageType.Integer)
                            {
                                paramValue = condParam.AsInteger();
                            }
                            else
                            {
                                continue; // Skip non-numeric parameters
                            }

                            // Evaluate condition
                            bool conditionMet = false;
                            switch (conditionOperator)
                            {
                                case ">": conditionMet = paramValue > conditionValue; break;
                                case "<": conditionMet = paramValue < conditionValue; break;
                                case ">=": conditionMet = paramValue >= conditionValue; break;
                                case "<=": conditionMet = paramValue <= conditionValue; break;
                                case "==": conditionMet = Math.Abs(paramValue - conditionValue) < 0.001; break;
                                case "!=": conditionMet = Math.Abs(paramValue - conditionValue) >= 0.001; break;
                            }

                            if (conditionMet)
                            {
                                matchedCount++;
                                var targetParamObj = elem.LookupParameter(targetParam);
                                if (targetParamObj != null && !targetParamObj.IsReadOnly)
                                {
                                    switch (targetParamObj.StorageType)
                                    {
                                        case StorageType.String:
                                            targetParamObj.Set(targetValue);
                                            break;
                                        case StorageType.Integer:
                                            if (int.TryParse(targetValue, out int intVal))
                                                targetParamObj.Set(intVal);
                                            else if (targetValue.ToLower() == "true")
                                                targetParamObj.Set(1);
                                            else if (targetValue.ToLower() == "false")
                                                targetParamObj.Set(0);
                                            break;
                                        case StorageType.Double:
                                            if (double.TryParse(targetValue, out double dblVal))
                                                targetParamObj.Set(dblVal);
                                            break;
                                    }
                                    updatedCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Element {elem.Id.Value}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        category = categoryName,
                        totalElements = elements.Count,
                        conditionMatched = matchedCount,
                        updated = updatedCount,
                        rule = $"IF {conditionParam} {conditionOperator} {conditionValue} THEN SET {targetParam} = {targetValue}",
                        errors = errors.Count > 0 ? errors.Take(10).ToList() : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Update titleblock parameters across all sheets.
        /// </summary>
        public static string BatchUpdateTitleblocks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var parameterName = parameters?["parameterName"]?.ToString();
                var parameterValue = parameters?["parameterValue"]?.ToString();
                var sheetFilter = parameters?["sheetFilter"]?.ToString(); // Optional: filter sheets by number pattern

                if (string.IsNullOrEmpty(parameterName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                // Get all sheets
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();

                if (!string.IsNullOrEmpty(sheetFilter))
                {
                    sheets = sheets.Where(s => s.SheetNumber.Contains(sheetFilter)).ToList();
                }

                int sheetsUpdated = 0;
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Batch Update Titleblocks"))
                {
                    trans.Start();

                    foreach (var sheet in sheets)
                    {
                        try
                        {
                            // Get titleblock on this sheet
                            var titleblocks = new FilteredElementCollector(doc, sheet.Id)
                                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                                .WhereElementIsNotElementType()
                                .Cast<FamilyInstance>()
                                .ToList();

                            foreach (var tb in titleblocks)
                            {
                                var param = tb.LookupParameter(parameterName);
                                if (param != null && !param.IsReadOnly)
                                {
                                    switch (param.StorageType)
                                    {
                                        case StorageType.String:
                                            param.Set(parameterValue ?? "");
                                            break;
                                        case StorageType.Integer:
                                            if (int.TryParse(parameterValue, out int intVal))
                                                param.Set(intVal);
                                            break;
                                        case StorageType.Double:
                                            if (double.TryParse(parameterValue, out double dblVal))
                                                param.Set(dblVal);
                                            break;
                                    }
                                    sheetsUpdated++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Sheet {sheet.SheetNumber}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        totalSheets = sheets.Count,
                        sheetsUpdated = sheetsUpdated,
                        parameterName = parameterName,
                        parameterValue = parameterValue,
                        errors = errors.Count > 0 ? errors.Take(10).ToList() : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Replace all instances of one element type with another type.
        /// Example: Swap all "Door-Single-Panel" with "Door-Single-Glass"
        /// </summary>
        public static string BatchSwapTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceTypeIdParam = parameters?["sourceTypeId"];
                var targetTypeIdParam = parameters?["targetTypeId"];
                var categoryName = parameters?["category"]?.ToString();

                if (sourceTypeIdParam == null || targetTypeIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceTypeId and targetTypeId are required"
                    });
                }

                var sourceTypeId = new ElementId(sourceTypeIdParam.ToObject<int>());
                var targetTypeId = new ElementId(targetTypeIdParam.ToObject<int>());

                var sourceType = doc.GetElement(sourceTypeId);
                var targetType = doc.GetElement(targetTypeId);

                if (sourceType == null || targetType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source or target type not found"
                    });
                }

                // Find all instances of source type
                var instances = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.GetTypeId() == sourceTypeId)
                    .ToList();

                int swappedCount = 0;
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Batch Swap Types"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var instance in instances)
                    {
                        try
                        {
                            // Try to change the type
                            var typeParam = instance.get_Parameter(BuiltInParameter.ELEM_TYPE_PARAM);
                            if (typeParam != null && !typeParam.IsReadOnly)
                            {
                                typeParam.Set(targetTypeId);
                                swappedCount++;
                            }
                            else
                            {
                                // For family instances, use ChangeTypeId
                                if (instance is FamilyInstance fi)
                                {
                                    fi.ChangeTypeId(targetTypeId);
                                    swappedCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Element {instance.Id.Value}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        sourceTypeName = sourceType.Name,
                        targetTypeName = targetType.Name,
                        instancesFound = instances.Count,
                        swapped = swappedCount,
                        errors = errors.Count > 0 ? errors.Take(10).ToList() : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy properties from one element to multiple target elements.
        /// Like a "Format Painter" for Revit elements.
        /// </summary>
        public static string MatchElementProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceIdParam = parameters?["sourceId"];
                var targetIdsParam = parameters?["targetIds"] as JArray;
                var propertiesToMatch = parameters?["properties"] as JArray; // Optional: specific properties

                if (sourceIdParam == null || targetIdsParam == null || targetIdsParam.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceId and targetIds array are required"
                    });
                }

                var sourceId = new ElementId(sourceIdParam.ToObject<int>());
                var sourceElem = doc.GetElement(sourceId);

                if (sourceElem == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source element not found"
                    });
                }

                var targetIds = targetIdsParam.Select(id => new ElementId(id.ToObject<int>())).ToList();
                var propertyFilter = propertiesToMatch?.Select(p => p.ToString()).ToHashSet();

                int matchedCount = 0;
                int propertiesCopied = 0;
                var errors = new List<string>();

                // Get all writable parameters from source (declare outside transaction for result reporting)
                var sourceParams = new Dictionary<string, (StorageType type, object value)>();

                using (var trans = new Transaction(doc, "Match Element Properties"))
                {
                    trans.Start();
                    foreach (Parameter param in sourceElem.Parameters)
                    {
                        if (param.IsReadOnly) continue;
                        if (propertyFilter != null && !propertyFilter.Contains(param.Definition.Name)) continue;

                        try
                        {
                            object value = null;
                            switch (param.StorageType)
                            {
                                case StorageType.String:
                                    value = param.AsString();
                                    break;
                                case StorageType.Integer:
                                    value = param.AsInteger();
                                    break;
                                case StorageType.Double:
                                    value = param.AsDouble();
                                    break;
                                case StorageType.ElementId:
                                    value = param.AsElementId();
                                    break;
                            }
                            if (value != null)
                            {
                                sourceParams[param.Definition.Name] = (param.StorageType, value);
                            }
                        }
                        catch { }
                    }

                    // Apply to targets
                    foreach (var targetId in targetIds)
                    {
                        var targetElem = doc.GetElement(targetId);
                        if (targetElem == null) continue;

                        bool anyChanged = false;
                        foreach (var kvp in sourceParams)
                        {
                            try
                            {
                                var targetParam = targetElem.LookupParameter(kvp.Key);
                                if (targetParam != null && !targetParam.IsReadOnly)
                                {
                                    switch (kvp.Value.type)
                                    {
                                        case StorageType.String:
                                            targetParam.Set((string)kvp.Value.value);
                                            break;
                                        case StorageType.Integer:
                                            targetParam.Set((int)kvp.Value.value);
                                            break;
                                        case StorageType.Double:
                                            targetParam.Set((double)kvp.Value.value);
                                            break;
                                        case StorageType.ElementId:
                                            targetParam.Set((ElementId)kvp.Value.value);
                                            break;
                                    }
                                    propertiesCopied++;
                                    anyChanged = true;
                                }
                            }
                            catch { }
                        }
                        if (anyChanged) matchedCount++;
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        sourceId = sourceId.Value,
                        targetsProcessed = targetIds.Count,
                        elementsModified = matchedCount,
                        propertiesCopied = propertiesCopied,
                        propertiesAvailable = sourceParams.Keys.ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Detect elements that intersect/clash with each other.
        /// Useful for coordination and QC.
        /// </summary>
        public static string DetectElementClashes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var category1Name = parameters?["category1"]?.ToString() ?? "Walls";
                var category2Name = parameters?["category2"]?.ToString() ?? "Doors";
                var tolerance = parameters?["tolerance"]?.ToObject<double>() ?? 0.01;

                BuiltInCategory GetCategory(string name)
                {
                    switch (name.ToLower())
                    {
                        case "walls": return BuiltInCategory.OST_Walls;
                        case "doors": return BuiltInCategory.OST_Doors;
                        case "windows": return BuiltInCategory.OST_Windows;
                        case "floors": return BuiltInCategory.OST_Floors;
                        case "columns": return BuiltInCategory.OST_Columns;
                        case "beams": return BuiltInCategory.OST_StructuralFraming;
                        case "pipes": return BuiltInCategory.OST_PipeCurves;
                        case "ducts": return BuiltInCategory.OST_DuctCurves;
                        case "furniture": return BuiltInCategory.OST_Furniture;
                        default: return BuiltInCategory.OST_Walls;
                    }
                }

                var cat1 = GetCategory(category1Name);
                var cat2 = GetCategory(category2Name);

                var elements1 = new FilteredElementCollector(doc)
                    .OfCategory(cat1)
                    .WhereElementIsNotElementType()
                    .ToList();

                var elements2 = new FilteredElementCollector(doc)
                    .OfCategory(cat2)
                    .WhereElementIsNotElementType()
                    .ToList();

                var clashes = new List<object>();

                foreach (var elem1 in elements1)
                {
                    var bb1 = elem1.get_BoundingBox(null);
                    if (bb1 == null) continue;

                    foreach (var elem2 in elements2)
                    {
                        if (elem1.Id == elem2.Id) continue;

                        var bb2 = elem2.get_BoundingBox(null);
                        if (bb2 == null) continue;

                        // Check bounding box intersection
                        bool intersects =
                            bb1.Min.X <= bb2.Max.X + tolerance && bb1.Max.X >= bb2.Min.X - tolerance &&
                            bb1.Min.Y <= bb2.Max.Y + tolerance && bb1.Max.Y >= bb2.Min.Y - tolerance &&
                            bb1.Min.Z <= bb2.Max.Z + tolerance && bb1.Max.Z >= bb2.Min.Z - tolerance;

                        if (intersects)
                        {
                            // Calculate intersection point (center of overlap)
                            var overlapCenter = new XYZ(
                                (Math.Max(bb1.Min.X, bb2.Min.X) + Math.Min(bb1.Max.X, bb2.Max.X)) / 2,
                                (Math.Max(bb1.Min.Y, bb2.Min.Y) + Math.Min(bb1.Max.Y, bb2.Max.Y)) / 2,
                                (Math.Max(bb1.Min.Z, bb2.Min.Z) + Math.Min(bb1.Max.Z, bb2.Max.Z)) / 2
                            );

                            clashes.Add(new
                            {
                                element1Id = elem1.Id.Value,
                                element1Category = category1Name,
                                element2Id = elem2.Id.Value,
                                element2Category = category2Name,
                                location = new { x = overlapCenter.X, y = overlapCenter.Y, z = overlapCenter.Z }
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        category1 = category1Name,
                        category2 = category2Name,
                        elements1Count = elements1.Count,
                        elements2Count = elements2.Count,
                        clashesFound = clashes.Count,
                        clashes = clashes.Take(100).ToList() // Limit to first 100
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create floor plan views automatically for each room.
        /// Views are cropped to room boundaries with optional padding.
        /// </summary>
        public static string CreateViewsFromRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewTemplateIdParam = parameters?["viewTemplateId"];
                var padding = parameters?["padding"]?.ToObject<double>() ?? 2.0; // 2 feet padding
                var viewScale = parameters?["scale"]?.ToObject<int>() ?? 48; // 1/4" = 1'-0"
                var roomIdsParam = parameters?["roomIds"] as JArray; // Optional: specific rooms

                // Get view family type for floor plans
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

                if (viewFamilyType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No floor plan view family type found"
                    });
                }

                // Get rooms
                List<Room> rooms;
                if (roomIdsParam != null && roomIdsParam.Count > 0)
                {
                    rooms = roomIdsParam
                        .Select(id => doc.GetElement(new ElementId(id.ToObject<int>())) as Room)
                        .Where(r => r != null)
                        .ToList();
                }
                else
                {
                    rooms = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.Area > 0) // Only placed rooms
                        .ToList();
                }

                var createdViews = new List<object>();
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Create Views From Rooms"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var room in rooms)
                    {
                        try
                        {
                            var bb = room.get_BoundingBox(null);
                            if (bb == null) continue;

                            // Get room level
                            var levelId = room.LevelId;
                            if (levelId == ElementId.InvalidElementId) continue;

                            // Create new floor plan view
                            var newView = ViewPlan.Create(doc, viewFamilyType.Id, levelId);
                            newView.Name = $"Room - {room.Name}".Replace(":", "-").Replace("/", "-");
                            newView.Scale = viewScale;

                            // Apply view template if specified
                            if (viewTemplateIdParam != null)
                            {
                                var templateId = new ElementId(viewTemplateIdParam.ToObject<int>());
                                newView.ViewTemplateId = templateId;
                            }

                            // Set crop box with padding
                            newView.CropBoxActive = true;
                            var cropBox = new BoundingBoxXYZ();
                            cropBox.Min = new XYZ(bb.Min.X - padding, bb.Min.Y - padding, bb.Min.Z);
                            cropBox.Max = new XYZ(bb.Max.X + padding, bb.Max.Y + padding, bb.Max.Z);
                            newView.CropBox = cropBox;
                            newView.CropBoxVisible = false;

                            createdViews.Add(new
                            {
                                viewId = newView.Id.Value,
                                viewName = newView.Name,
                                roomId = room.Id.Value,
                                roomName = room.Name
                            });
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Room {room.Name}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        roomsProcessed = rooms.Count,
                        viewsCreated = createdViews.Count,
                        views = createdViews,
                        errors = errors.Count > 0 ? errors.Take(10).ToList() : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy parameter values from one element to all similar elements.
        /// Similar = same category and type.
        /// </summary>
        public static string PropagateParameterValues(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceIdParam = parameters?["sourceId"];
                var parameterNamesParam = parameters?["parameterNames"] as JArray;
                var sameTypeOnly = parameters?["sameTypeOnly"]?.ToObject<bool>() ?? true;

                if (sourceIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceId is required"
                    });
                }

                var sourceId = new ElementId(sourceIdParam.ToObject<int>());
                var sourceElem = doc.GetElement(sourceId);

                if (sourceElem == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source element not found"
                    });
                }

                var parameterNames = parameterNamesParam?.Select(p => p.ToString()).ToList();
                var sourceCategory = sourceElem.Category?.Id;
                var sourceTypeId = sourceElem.GetTypeId();

                if (sourceCategory == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source element has no category"
                    });
                }

                // Find similar elements
                var similarElements = new FilteredElementCollector(doc)
                    .OfCategoryId(sourceCategory)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Id != sourceId)
                    .ToList();

                if (sameTypeOnly)
                {
                    similarElements = similarElements.Where(e => e.GetTypeId() == sourceTypeId).ToList();
                }

                // Collect source parameter values
                var sourceValues = new Dictionary<string, (StorageType type, object value)>();
                foreach (Parameter param in sourceElem.Parameters)
                {
                    if (param.IsReadOnly) continue;
                    if (parameterNames != null && !parameterNames.Contains(param.Definition.Name)) continue;

                    try
                    {
                        object value = null;
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                value = param.AsString();
                                break;
                            case StorageType.Integer:
                                value = param.AsInteger();
                                break;
                            case StorageType.Double:
                                value = param.AsDouble();
                                break;
                        }
                        if (value != null)
                        {
                            sourceValues[param.Definition.Name] = (param.StorageType, value);
                        }
                    }
                    catch { }
                }

                int elementsUpdated = 0;
                int valuesSet = 0;

                using (var trans = new Transaction(doc, "Propagate Parameter Values"))
                {
                    trans.Start();

                    foreach (var elem in similarElements)
                    {
                        bool updated = false;
                        foreach (var kvp in sourceValues)
                        {
                            try
                            {
                                var param = elem.LookupParameter(kvp.Key);
                                if (param != null && !param.IsReadOnly)
                                {
                                    switch (kvp.Value.type)
                                    {
                                        case StorageType.String:
                                            param.Set((string)kvp.Value.value);
                                            break;
                                        case StorageType.Integer:
                                            param.Set((int)kvp.Value.value);
                                            break;
                                        case StorageType.Double:
                                            param.Set((double)kvp.Value.value);
                                            break;
                                    }
                                    valuesSet++;
                                    updated = true;
                                }
                            }
                            catch { }
                        }
                        if (updated) elementsUpdated++;
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        sourceId = sourceId.Value,
                        similarElementsFound = similarElements.Count,
                        elementsUpdated = elementsUpdated,
                        valuesSet = valuesSet,
                        parametersPropagated = sourceValues.Keys.ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extracts the actual geometry of model elements as they appear cut in a section view.
        /// Returns boundary curves for walls, floors, roofs with their material layers.
        /// This allows accurate replication of section cut geometry in drafting views.
        /// </summary>
        public static string GetSectionCutGeometry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var viewId = new ElementId(viewIdParam.ToObject<int>());
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var cutGeometry = new List<object>();
                var options = new Options
                {
                    View = view,
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = false
                };

                // Get view's section box/crop for bounds checking
                var cropBox = view.CropBox;
                var viewDirection = view.ViewDirection;
                var viewOrigin = view.Origin;

                // Find walls visible in this view
                var wallCollector = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .ToList();

                foreach (var wall in wallCollector)
                {
                    try
                    {
                        var wallType = wall.WallType;
                        var structure = wallType.GetCompoundStructure();

                        if (structure == null) continue;

                        // Get wall location line
                        var locationCurve = wall.Location as LocationCurve;
                        if (locationCurve == null) continue;

                        var wallCurve = locationCurve.Curve;
                        var wallLine = wallCurve as Line;
                        if (wallLine == null) continue;

                        // Get wall geometry bounds
                        var bbox = wall.get_BoundingBox(view);
                        if (bbox == null) continue;

                        // Get wall base/top heights
                        var baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                        var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10;

                        // Calculate wall direction and perpendicular
                        var wallDir = (wallLine.GetEndPoint(1) - wallLine.GetEndPoint(0)).Normalize();
                        var perpDir = new XYZ(-wallDir.Y, wallDir.X, 0); // Perpendicular in plan

                        // Get wall width and layer offsets
                        double totalWidth = structure.GetWidth();
                        double cumulativeOffset = 0;

                        var layers = new List<object>();

                        for (int i = 0; i < structure.LayerCount; i++)
                        {
                            var layer = structure.GetLayers()[i];
                            var layerWidth = layer.Width;
                            var layerFunction = layer.Function;

                            // Get material info
                            string materialName = "Unknown";
                            string fillPatternName = "";
                            int materialId = -1;

                            if (layer.MaterialId != ElementId.InvalidElementId)
                            {
                                var material = doc.GetElement(layer.MaterialId) as Material;
                                if (material != null)
                                {
                                    materialName = material.Name;
                                    materialId = (int)material.Id.Value;

                                    // Get cut fill pattern
                                    var cutPatternId = material.CutForegroundPatternId;
                                    if (cutPatternId != ElementId.InvalidElementId)
                                    {
                                        var pattern = doc.GetElement(cutPatternId) as FillPatternElement;
                                        if (pattern != null)
                                        {
                                            fillPatternName = pattern.Name;
                                        }
                                    }
                                }
                            }

                            // Calculate layer boundaries relative to wall centerline
                            // Exterior side is negative offset, interior is positive
                            double layerStart = -totalWidth / 2 + cumulativeOffset;
                            double layerEnd = layerStart + layerWidth;

                            // Create boundary points for this layer in section view coordinates
                            // The section cuts perpendicular to wall, so we see the layer as a rectangle
                            var layerBounds = new
                            {
                                layerIndex = i,
                                function = layerFunction.ToString(),
                                material = materialName,
                                materialId = materialId,
                                fillPattern = fillPatternName,
                                width = layerWidth,
                                // Boundary in view coordinates (X = horizontal position, Y = vertical/Z)
                                boundary = new
                                {
                                    // These are offsets from wall centerline
                                    startOffset = layerStart,
                                    endOffset = layerEnd,
                                    // Actual coordinates depend on wall position in view
                                    minX = bbox.Min.X + (layerStart + totalWidth/2) * Math.Abs(perpDir.X),
                                    maxX = bbox.Min.X + (layerEnd + totalWidth/2) * Math.Abs(perpDir.X),
                                    minZ = bbox.Min.Z,
                                    maxZ = bbox.Max.Z
                                }
                            };

                            layers.Add(layerBounds);
                            cumulativeOffset += layerWidth;
                        }

                        cutGeometry.Add(new
                        {
                            elementType = "Wall",
                            elementId = (int)wall.Id.Value,
                            typeName = wallType.Name,
                            typeId = (int)wallType.Id.Value,
                            totalWidth = totalWidth,
                            boundingBox = new
                            {
                                min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                                max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                            },
                            layers = layers
                        });
                    }
                    catch (Exception ex)
                    {
                        // Skip problematic walls
                        continue;
                    }
                }

                // Find floors visible in this view
                var floorCollector = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(Floor))
                    .Cast<Floor>()
                    .ToList();

                foreach (var floor in floorCollector)
                {
                    try
                    {
                        var floorType = floor.FloorType;
                        var structure = floorType.GetCompoundStructure();
                        var bbox = floor.get_BoundingBox(view);
                        if (bbox == null) continue;

                        var layers = new List<object>();
                        if (structure != null)
                        {
                            double cumulativeOffset = 0;
                            double totalThickness = structure.GetWidth();

                            for (int i = 0; i < structure.LayerCount; i++)
                            {
                                var layer = structure.GetLayers()[i];
                                string materialName = "Unknown";
                                if (layer.MaterialId != ElementId.InvalidElementId)
                                {
                                    var material = doc.GetElement(layer.MaterialId) as Material;
                                    if (material != null) materialName = material.Name;
                                }

                                layers.Add(new
                                {
                                    layerIndex = i,
                                    function = layer.Function.ToString(),
                                    material = materialName,
                                    width = layer.Width,
                                    offset = cumulativeOffset
                                });
                                cumulativeOffset += layer.Width;
                            }
                        }

                        cutGeometry.Add(new
                        {
                            elementType = "Floor",
                            elementId = (int)floor.Id.Value,
                            typeName = floorType.Name,
                            boundingBox = new
                            {
                                min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                                max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                            },
                            layers = layers
                        });
                    }
                    catch { continue; }
                }

                // Find roofs visible in this view
                var roofCollector = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .ToList();

                foreach (var roof in roofCollector)
                {
                    try
                    {
                        var roofType = roof.RoofType;
                        var structure = roofType.GetCompoundStructure();
                        var bbox = roof.get_BoundingBox(view);
                        if (bbox == null) continue;

                        var layers = new List<object>();
                        if (structure != null)
                        {
                            double cumulativeOffset = 0;
                            for (int i = 0; i < structure.LayerCount; i++)
                            {
                                var layer = structure.GetLayers()[i];
                                string materialName = "Unknown";
                                if (layer.MaterialId != ElementId.InvalidElementId)
                                {
                                    var material = doc.GetElement(layer.MaterialId) as Material;
                                    if (material != null) materialName = material.Name;
                                }

                                layers.Add(new
                                {
                                    layerIndex = i,
                                    function = layer.Function.ToString(),
                                    material = materialName,
                                    width = layer.Width,
                                    offset = cumulativeOffset
                                });
                                cumulativeOffset += layer.Width;
                            }
                        }

                        cutGeometry.Add(new
                        {
                            elementType = "Roof",
                            elementId = (int)roof.Id.Value,
                            typeName = roofType.Name,
                            boundingBox = new
                            {
                                min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                                max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                            },
                            layers = layers
                        });
                    }
                    catch { continue; }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId.Value,
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        elementCount = cutGeometry.Count,
                        elements = cutGeometry
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
