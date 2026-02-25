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
    /// View creation and management methods for MCP Bridge
    /// </summary>
    public static class ViewMethods
    {
        /// <summary>
        /// Parse a point from JSON - accepts both object {x,y,z} and array [x,y,z] formats
        /// </summary>
        private static XYZ ParsePoint(JToken pointToken)
        {
            if (pointToken == null)
                throw new ArgumentException("Point is required");

            // Try object format {x, y, z}
            if (pointToken.Type == JTokenType.Object)
            {
                var obj = pointToken as JObject;
                var x = obj["x"]?.ToObject<double>() ?? 0;
                var y = obj["y"]?.ToObject<double>() ?? 0;
                var z = obj["z"]?.ToObject<double>() ?? 0;
                return new XYZ(x, y, z);
            }

            // Try array format [x, y, z]
            if (pointToken.Type == JTokenType.Array)
            {
                var arr = pointToken.ToObject<double[]>();
                if (arr.Length >= 3)
                    return new XYZ(arr[0], arr[1], arr[2]);
                if (arr.Length == 2)
                    return new XYZ(arr[0], arr[1], 0);
                throw new ArgumentException("Point array must have at least 2 elements");
            }

            throw new ArgumentException($"Point must be object {{x,y,z}} or array [x,y,z], got {pointToken.Type}");
        }

        /// <summary>
        /// Create a floor plan view
        /// </summary>
        public static string CreateFloorPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var viewName = parameters["viewName"]?.ToString();

                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Level not found"
                    });
                }

                // Get view family type
                var viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan)
                    ?.Id;

                if (viewFamilyTypeId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Floor plan view type not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Floor Plan"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var view = ViewPlan.Create(doc, viewFamilyTypeId, levelId);

                    if (!string.IsNullOrEmpty(viewName))
                    {
                        view.Name = viewName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)view.Id.Value,
                        viewName = view.Name,
                        level = level.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a ceiling plan view
        /// </summary>
        public static string CreateCeilingPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var viewName = parameters["viewName"]?.ToString();

                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Level not found"
                    });
                }

                // Get ceiling plan view family type
                var viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.CeilingPlan)
                    ?.Id;

                if (viewFamilyTypeId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Ceiling plan view type not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Ceiling Plan"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var view = ViewPlan.Create(doc, viewFamilyTypeId, levelId);

                    if (!string.IsNullOrEmpty(viewName))
                    {
                        view.Name = viewName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)view.Id.Value,
                        viewName = view.Name,
                        level = level.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a section view
        /// </summary>
        public static string CreateSection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse points - accepts both {x,y,z} object and [x,y,z] array formats
                var start = ParsePoint(parameters["startPoint"]);
                var end = ParsePoint(parameters["endPoint"]);
                var viewName = parameters["viewName"]?.ToString();

                // Get section view family type
                var viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section)
                    ?.Id;

                if (viewFamilyTypeId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Section view type not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Section"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create bounding box for section
                    var direction = (end - start).Normalize();
                    var up = XYZ.BasisZ;
                    var right = direction.CrossProduct(up).Normalize();

                    var transform = Transform.Identity;
                    transform.Origin = start;
                    transform.BasisX = right;
                    transform.BasisY = up;
                    transform.BasisZ = direction;

                    var bbox = new BoundingBoxXYZ
                    {
                        Transform = transform,
                        Min = new XYZ(-10, -10, 0),
                        Max = new XYZ(10, 10, start.DistanceTo(end))
                    };

                    var section = ViewSection.CreateSection(doc, viewFamilyTypeId, bbox);

                    if (!string.IsNullOrEmpty(viewName))
                    {
                        section.Name = viewName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)section.Id.Value,
                        viewName = section.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create an elevation view
        /// </summary>
        public static string CreateElevation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var location = parameters["location"].ToObject<double[]>();
                var direction = parameters["direction"].ToObject<double[]>();
                var viewName = parameters["viewName"]?.ToString();

                var loc = new XYZ(location[0], location[1], location[2]);
                var dir = new XYZ(direction[0], direction[1], direction[2]).Normalize();

                // Get elevation marker type
                var elevationMarkerTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Elevation)
                    ?.Id;

                if (elevationMarkerTypeId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Elevation view type not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Elevation"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create elevation marker
                    var marker = ElevationMarker.CreateElevationMarker(doc, elevationMarkerTypeId, loc, 50);

                    // Create elevation view in specified direction
                    // Determine which index to use based on direction
                    var elevationView = marker.CreateElevation(doc, doc.ActiveView.Id, 0);

                    if (!string.IsNullOrEmpty(viewName))
                    {
                        elevationView.Name = viewName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)elevationView.Id.Value,
                        viewName = elevationView.Name,
                        markerId = (int)marker.Id.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a drafting view for 2D detailing
        /// </summary>
        public static string CreateDraftingView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewName = parameters["name"]?.ToString() ?? "New Drafting View";
                var scale = parameters["scale"]?.ToObject<int>() ?? 12; // Default 1" = 1'-0"

                // Get drafting view family type
                var viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting)
                    ?.Id;

                if (viewFamilyTypeId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Drafting view type not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Drafting View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var draftingView = ViewDrafting.Create(doc, viewFamilyTypeId);
                    draftingView.Name = viewName;
                    draftingView.Scale = scale;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)draftingView.Id.Value,
                        viewName = draftingView.Name,
                        scale = draftingView.Scale
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a view
        /// </summary>
        public static string DuplicateView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var duplicateOption = parameters["duplicateOption"]?.ToString().ToLower() ?? "duplicate";
                var newName = parameters["newName"]?.ToString();

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Duplicate View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ViewDuplicateOption option = duplicateOption switch
                    {
                        "duplicate" => ViewDuplicateOption.Duplicate,
                        "withdetailing" => ViewDuplicateOption.WithDetailing,
                        "asDependent" => ViewDuplicateOption.AsDependent,
                        _ => ViewDuplicateOption.Duplicate
                    };

                    var newViewId = view.Duplicate(option);
                    var newView = doc.GetElement(newViewId) as View;

                    if (!string.IsNullOrEmpty(newName))
                    {
                        newView.Name = newName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalViewId = (int)viewId.Value,
                        newViewId = (int)newViewId.Value,
                        newViewName = newView.Name,
                        duplicateOption = option.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Apply a view template
        /// </summary>
        public static string ApplyViewTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var templateId = new ElementId(int.Parse(parameters["templateId"].ToString()));

                var view = doc.GetElement(viewId) as View;
                var template = doc.GetElement(templateId) as View;

                if (view == null || template == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View or template not found"
                    });
                }

                using (var trans = new Transaction(doc, "Apply View Template"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.ViewTemplateId = templateId;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        templateId = (int)templateId.Value,
                        templateName = template.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all views in the project
        /// </summary>
        public static string GetAllViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;
                var viewTypeFilter = parameters?["viewType"]?.ToString();
                bool includeSheetInfo = parameters?["includeSheetInfo"]?.Value<bool>() ?? true;

                // Use OfType for safe casting
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .ToElements()
                    .OfType<View>()
                    .Where(v => v != null && !v.IsTemplate);

                if (!string.IsNullOrEmpty(viewTypeFilter))
                {
                    ViewType vt;
                    if (Enum.TryParse(viewTypeFilter, true, out vt))
                    {
                        allViews = allViews.Where(v => v.ViewType == vt);
                    }
                }

                // Pre-fetch viewport info for efficiency
                Dictionary<ElementId, string> viewToSheetMap = new Dictionary<ElementId, string>();
                if (includeSheetInfo)
                {
                    var viewports = new FilteredElementCollector(doc)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .ToList();

                    foreach (var vp in viewports)
                    {
                        try
                        {
                            var sheet = doc.GetElement(vp.SheetId) as ViewSheet;
                            if (sheet != null && !viewToSheetMap.ContainsKey(vp.ViewId))
                            {
                                viewToSheetMap[vp.ViewId] = sheet.SheetNumber;
                            }
                        }
                        catch { }
                    }
                }

                var views = new List<object>();
                foreach (var v in allViews)
                {
                    try
                    {
                        int scale = 0;
                        try { scale = v.Scale; } catch { }

                        string sheetNumber = null;
                        bool isOnSheet = false;
                        if (includeSheetInfo && viewToSheetMap.TryGetValue(v.Id, out string sn))
                        {
                            sheetNumber = sn;
                            isOnSheet = true;
                        }

                        // Also include id/name aliases for compatibility
                        views.Add(new
                        {
                            id = (int)v.Id.Value,          // Alias
                            viewId = (int)v.Id.Value,
                            name = v.Name ?? "",           // Alias
                            viewName = v.Name ?? "",
                            viewType = v.ViewType.ToString(),
                            isTemplate = v.IsTemplate,
                            scale = scale,
                            isOnSheet = isOnSheet,
                            sheetNumber = sheetNumber
                        });
                    }
                    catch { continue; }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewCount = views.Count,
                    views = views
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all view templates
        /// </summary>
        public static string GetViewTemplates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var templates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.IsTemplate)
                    .Select(v => new
                    {
                        templateId = (int)v.Id.Value,
                        templateName = v.Name,
                        viewType = v.ViewType.ToString()
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    templateCount = templates.Count,
                    templates = templates
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set view crop box
        /// </summary>
        public static string SetViewCropBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set View Crop Box"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Enable crop view
                    if (parameters["enableCrop"] != null)
                    {
                        view.CropBoxActive = bool.Parse(parameters["enableCrop"].ToString());
                    }

                    // Check if view has a view template that might control crop
                    var viewTemplateId = view.ViewTemplateId;
                    var hasTemplate = viewTemplateId != null && viewTemplateId != ElementId.InvalidElementId;

                    // Set crop box if provided
                    string cropMethod = "none";
                    string cropError = null;
                    double requestedWidth = 0, requestedHeight = 0;
                    if (parameters["cropBox"] != null)
                    {
                        var cropData = parameters["cropBox"].ToObject<double[][]>();
                        var min = new XYZ(cropData[0][0], cropData[0][1], cropData[0][2]);
                        var max = new XYZ(cropData[1][0], cropData[1][1], cropData[1][2]);
                        requestedWidth = max.X - min.X;
                        requestedHeight = max.Y - min.Y;

                        // Get existing crop box for transform info
                        var existingCrop = view.CropBox;
                        var transform = existingCrop.Transform;
                        var origin = transform.Origin;
                        var right = transform.BasisX;
                        var up = transform.BasisY;

                        // For elevation/section views, modify the existing crop box
                        // preserving the Z values (depth) and only changing X/Y (visible region)
                        var cropManager = view.GetCropRegionShapeManager();
                        bool canHaveShape = cropManager?.CanHaveShape ?? false;

                        // Store original Z values (depth settings)
                        double origMinZ = existingCrop.Min.Z;
                        double origMaxZ = existingCrop.Max.Z;

                        // Create new crop box with new X/Y but preserve Z
                        var newMin = new XYZ(min.X, min.Y, origMinZ);
                        var newMax = new XYZ(max.X, max.Y, origMaxZ);

                        // For ViewSection (elevations, sections), we may need to cast
                        ViewSection viewSection = view as ViewSection;
                        string viewTypeInfo = viewSection != null ? "ViewSection" : view.GetType().Name;

                        // Check if view has a Scope Box - if so, that controls the crop
                        Parameter scopeBoxParam = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                        ElementId scopeBoxId = scopeBoxParam?.AsElementId() ?? ElementId.InvalidElementId;
                        bool hasScopeBox = scopeBoxId != null && scopeBoxId != ElementId.InvalidElementId;

                        if (hasScopeBox)
                        {
                            // Remove the scope box to allow manual crop control
                            scopeBoxParam.Set(ElementId.InvalidElementId);
                            doc.Regenerate();
                        }

                        // Get fresh crop box after potentially removing scope box
                        existingCrop = view.CropBox;

                        // Check if custom transform is provided
                        if (parameters["transform"] != null)
                        {
                            var xformData = parameters["transform"];
                            transform = Transform.Identity;

                            // Parse origin
                            if (xformData["origin"] != null)
                            {
                                var o = xformData["origin"];
                                transform.Origin = new XYZ(
                                    o["x"]?.ToObject<double>() ?? 0,
                                    o["y"]?.ToObject<double>() ?? 0,
                                    o["z"]?.ToObject<double>() ?? 0
                                );
                            }

                            // Parse basisX (right direction)
                            if (xformData["basisX"] != null)
                            {
                                var bx = xformData["basisX"];
                                transform.BasisX = new XYZ(
                                    bx["x"]?.ToObject<double>() ?? 1,
                                    bx["y"]?.ToObject<double>() ?? 0,
                                    bx["z"]?.ToObject<double>() ?? 0
                                );
                            }

                            // Parse basisY (up direction)
                            if (xformData["basisY"] != null)
                            {
                                var by = xformData["basisY"];
                                transform.BasisY = new XYZ(
                                    by["x"]?.ToObject<double>() ?? 0,
                                    by["y"]?.ToObject<double>() ?? 1,
                                    by["z"]?.ToObject<double>() ?? 0
                                );
                            }

                            // Parse basisZ (normal direction for floor plans)
                            if (xformData["basisZ"] != null)
                            {
                                var bz = xformData["basisZ"];
                                transform.BasisZ = new XYZ(
                                    bz["x"]?.ToObject<double>() ?? 0,
                                    bz["y"]?.ToObject<double>() ?? 0,
                                    bz["z"]?.ToObject<double>() ?? 1
                                );
                            }
                        }
                        else
                        {
                            transform = existingCrop.Transform;
                        }

                        // Create new crop box - MUST set Transform first before Min/Max
                        var newCropBox = new BoundingBoxXYZ();
                        newCropBox.Transform = transform;  // Set transform FIRST

                        // Check if user wants to modify far clip (Z values)
                        // If input Z values are different from 0, use them; otherwise preserve existing
                        bool modifyFarClip = (Math.Abs(min.Z) > 0.001 || Math.Abs(max.Z) > 0.001);

                        double newMinZ = modifyFarClip ? min.Z : existingCrop.Min.Z;
                        double newMaxZ = modifyFarClip ? max.Z : existingCrop.Max.Z;

                        // Set Min/Max using the requested values
                        newCropBox.Min = new XYZ(min.X, min.Y, newMinZ);
                        newCropBox.Max = new XYZ(max.X, max.Y, newMaxZ);

                        // Assign to view
                        view.CropBox = newCropBox;

                        // For elevation/section views, also try to set Far Clip Offset via parameter
                        // The CropBox Z values may not apply directly for elevations
                        if (viewSection != null && modifyFarClip)
                        {
                            double requestedDepth = Math.Abs(max.Z - min.Z);

                            // Try Far Clip Offset parameter (VIEWER_BOUND_OFFSET_FAR)
                            Parameter farClipParam = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                            if (farClipParam != null && !farClipParam.IsReadOnly)
                            {
                                farClipParam.Set(requestedDepth);
                            }

                            // Also ensure Far Clipping is active (VIEWER_BOUND_FAR_CLIPPING)
                            // 0 = No clip, 1 = Clip with line, 2 = Clip without line
                            Parameter farClipActiveParam = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                            if (farClipActiveParam != null && !farClipActiveParam.IsReadOnly)
                            {
                                // Set to "Clip without line" (value 2) if not already set
                                int currentValue = farClipActiveParam.AsInteger();
                                if (currentValue == 0) // No clip
                                {
                                    farClipActiveParam.Set(2); // Clip without line
                                }
                            }
                        }

                        // Read back to verify
                        var checkCrop = view.CropBox;
                        double checkWidth = checkCrop.Max.X - checkCrop.Min.X;
                        double checkHeight = checkCrop.Max.Y - checkCrop.Min.Y;

                        cropMethod = $"{viewTypeInfo}: hasScopeBox={hasScopeBox}, result={checkWidth:F0}x{checkHeight:F0}";
                    }

                    trans.Commit();

                    // Return actual crop box values after commit
                    var finalCropBox = view.CropBox;
                    double actualWidth = finalCropBox.Max.X - finalCropBox.Min.X;
                    double actualHeight = finalCropBox.Max.Y - finalCropBox.Min.Y;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        cropBoxActive = view.CropBoxActive,
                        hasViewTemplate = hasTemplate,
                        cropMethod = cropMethod,
                        cropError = cropError,
                        requested = new { width = requestedWidth, height = requestedHeight },
                        actual = new { width = actualWidth, height = actualHeight },
                        actualCropBox = new
                        {
                            min = new { x = finalCropBox.Min.X, y = finalCropBox.Min.Y, z = finalCropBox.Min.Z },
                            max = new { x = finalCropBox.Max.X, y = finalCropBox.Max.Y, z = finalCropBox.Max.Z }
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
        /// Get view crop box coordinates
        /// </summary>
        public static string GetViewCropBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var cropBox = view.CropBox;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    cropBoxActive = view.CropBoxActive,
                    cropBoxVisible = view.CropBoxVisible,
                    cropBox = new
                    {
                        min = new { x = cropBox.Min.X, y = cropBox.Min.Y, z = cropBox.Min.Z },
                        max = new { x = cropBox.Max.X, y = cropBox.Max.Y, z = cropBox.Max.Z }
                    },
                    transform = new
                    {
                        origin = new { x = cropBox.Transform.Origin.X, y = cropBox.Transform.Origin.Y, z = cropBox.Transform.Origin.Z },
                        basisX = new { x = cropBox.Transform.BasisX.X, y = cropBox.Transform.BasisX.Y, z = cropBox.Transform.BasisX.Z },
                        basisY = new { x = cropBox.Transform.BasisY.X, y = cropBox.Transform.BasisY.Y, z = cropBox.Transform.BasisY.Z },
                        basisZ = new { x = cropBox.Transform.BasisZ.X, y = cropBox.Transform.BasisZ.Y, z = cropBox.Transform.BasisZ.Z }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rename a view
        /// </summary>
        public static string RenameView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var newName = parameters["newName"].ToString();

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Rename View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.Name = newName;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        newName = view.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a view
        /// </summary>
        public static string DeleteView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                using (var trans = new Transaction(doc, "Delete View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(viewId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        message = "View deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set view scale
        /// </summary>
        public static string SetViewScale(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var scale = int.Parse(parameters["scale"].ToString());

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set View Scale"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.Scale = scale;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        scale = view.Scale
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the active view in Revit
        /// </summary>
        public static string GetActiveView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;
                var activeView = uiDoc.ActiveView;

                if (activeView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active view found"
                    });
                }

                // Get level if this is a plan view
                string levelName = null;
                if (activeView.GenLevel != null)
                {
                    levelName = activeView.GenLevel.Name;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)activeView.Id.Value,
                    viewName = activeView.Name,
                    viewType = activeView.ViewType.ToString(),
                    level = levelName,
                    scale = activeView.Scale,
                    isTemplate = activeView.IsTemplate
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set the active view in Revit
        /// </summary>
        public static string SetActiveView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found with specified ID"
                    });
                }

                // Check if view can be activated (not a template, not a schedule, etc.)
                if (view.IsTemplate)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot activate a view template"
                    });
                }

                // Set the active view
                uiDoc.ActiveView = view;

                // Get level if applicable
                string levelName = null;
                if (view.GenLevel != null)
                {
                    levelName = view.GenLevel.Name;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)view.Id.Value,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    level = levelName,
                    message = "View activated successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Zoom to fit all elements in the active view
        /// </summary>
        public static string ZoomToFit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiDoc.Document;

                // Get target view (use specified viewId or active view)
                View targetView;
                if (parameters != null && parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    targetView = doc.GetElement(viewId) as View;
                    if (targetView == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "View not found with specified ID"
                        });
                    }
                    // Switch to the view first
                    uiDoc.ActiveView = targetView;
                }
                else
                {
                    targetView = uiDoc.ActiveView;
                }

                if (targetView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active view available"
                    });
                }

                // Get the UIView for zoom operations
                var uiViews = uiDoc.GetOpenUIViews();
                UIView uiView = null;

                // Try to find the UIView for this view
                foreach (var uv in uiViews)
                {
                    if (uv.ViewId == targetView.Id)
                    {
                        uiView = uv;
                        break;
                    }
                }

                // If not found directly, try the first available UIView (for active view)
                if (uiView == null && uiViews.Count > 0)
                {
                    uiView = uiViews[0];
                }

                if (uiView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not get UI view for zoom operation. No open view windows found."
                    });
                }

                // Zoom to fit
                uiView.ZoomToFit();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)targetView.Id.Value,
                    viewName = targetView.Name,
                    message = "Zoomed to fit all elements in view"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Zoom to a specific element in the active view
        /// </summary>
        public static string ZoomToElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiDoc.Document;
                var activeView = uiDoc.ActiveView;

                if (activeView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active view"
                    });
                }

                if (parameters == null || parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found with specified ID"
                    });
                }

                // Get the bounding box of the element
                var bbox = element.get_BoundingBox(activeView);
                if (bbox == null)
                {
                    // Try without view context
                    bbox = element.get_BoundingBox(null);
                }

                if (bbox == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not get bounding box for element"
                    });
                }

                // Get the UIView
                var uiViews = uiDoc.GetOpenUIViews();
                UIView uiView = null;

                // Try to find the UIView for the active view
                foreach (var uv in uiViews)
                {
                    if (uv.ViewId == activeView.Id)
                    {
                        uiView = uv;
                        break;
                    }
                }

                // If not found, use the first available UIView
                if (uiView == null && uiViews.Count > 0)
                {
                    uiView = uiViews[0];
                }

                if (uiView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not get UI view for zoom operation. No open view windows found."
                    });
                }

                // Zoom to the bounding box with some margin
                var margin = 5.0; // 5 feet margin
                var zoomCorners = new List<XYZ>
                {
                    new XYZ(bbox.Min.X - margin, bbox.Min.Y - margin, bbox.Min.Z),
                    new XYZ(bbox.Max.X + margin, bbox.Max.Y + margin, bbox.Max.Z)
                };

                uiView.ZoomAndCenterRectangle(zoomCorners[0], zoomCorners[1]);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)elementId.Value,
                    viewId = (int)uiDoc.ActiveView.Id.Value,
                    message = "Zoomed to element"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Zoom to a specific region defined by min/max coordinates.
        /// Used for autonomous visual inspection.
        /// </summary>
        public static string ZoomToRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var activeView = uiDoc.ActiveView;
                if (activeView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active view"
                    });
                }

                // Parse min and max points
                if (parameters == null || parameters["minPoint"] == null || parameters["maxPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "minPoint and maxPoint arrays are required (e.g., [x, y, z])"
                    });
                }

                var minArray = parameters["minPoint"].ToObject<double[]>();
                var maxArray = parameters["maxPoint"].ToObject<double[]>();

                if (minArray.Length < 2 || maxArray.Length < 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Points must have at least x and y coordinates"
                    });
                }

                var minPoint = new XYZ(minArray[0], minArray[1], minArray.Length > 2 ? minArray[2] : 0);
                var maxPoint = new XYZ(maxArray[0], maxArray[1], maxArray.Length > 2 ? maxArray[2] : 0);

                // Get UIView for zoom
                var uiViews = uiDoc.GetOpenUIViews();
                UIView uiView = null;

                foreach (var uv in uiViews)
                {
                    if (uv.ViewId == activeView.Id)
                    {
                        uiView = uv;
                        break;
                    }
                }

                if (uiView == null && uiViews.Count > 0)
                {
                    uiView = uiViews[0];
                }

                if (uiView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not get UI view for zoom operation"
                    });
                }

                // Zoom to region
                uiView.ZoomAndCenterRectangle(minPoint, maxPoint);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)activeView.Id.Value,
                    viewName = activeView.Name,
                    minPoint = new[] { minPoint.X, minPoint.Y, minPoint.Z },
                    maxPoint = new[] { maxPoint.X, maxPoint.Y, maxPoint.Z },
                    message = "Zoomed to specified region"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Zoom to area around a grid intersection.
        /// Used for autonomous visual inspection at specific grid locations.
        /// </summary>
        public static string ZoomToGridIntersection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiDoc.Document;
                var activeView = uiDoc.ActiveView;

                if (activeView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active view"
                    });
                }

                // Get grid names
                var gridH = parameters?["gridHorizontal"]?.ToString();
                var gridV = parameters?["gridVertical"]?.ToString();
                var margin = parameters?["margin"]?.Value<double>() ?? 30.0;

                if (string.IsNullOrEmpty(gridH) || string.IsNullOrEmpty(gridV))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "gridHorizontal and gridVertical names are required"
                    });
                }

                // Find the grids
                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .ToList();

                Grid grid1 = null, grid2 = null;
                foreach (var g in grids)
                {
                    if (g.Name.Equals(gridH, StringComparison.OrdinalIgnoreCase))
                        grid1 = g;
                    if (g.Name.Equals(gridV, StringComparison.OrdinalIgnoreCase))
                        grid2 = g;
                }

                if (grid1 == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Grid '{gridH}' not found",
                        availableGrids = grids.Select(g => g.Name).Take(20).ToList()
                    });
                }

                if (grid2 == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Grid '{gridV}' not found",
                        availableGrids = grids.Select(g => g.Name).Take(20).ToList()
                    });
                }

                // Get grid curves and find intersection
                var curve1 = grid1.Curve;
                var curve2 = grid2.Curve;

                // Get approximate intersection by finding closest points
                var results = curve1.Project(curve2.GetEndPoint(0));
                XYZ intersection;

                if (results != null)
                {
                    intersection = results.XYZPoint;
                }
                else
                {
                    // Fallback: use midpoint between grid midpoints
                    var mid1 = (curve1.GetEndPoint(0) + curve1.GetEndPoint(1)) / 2.0;
                    var mid2 = (curve2.GetEndPoint(0) + curve2.GetEndPoint(1)) / 2.0;
                    intersection = (mid1 + mid2) / 2.0;
                }

                // Create bounding box with margin
                var minPoint = new XYZ(intersection.X - margin, intersection.Y - margin, intersection.Z);
                var maxPoint = new XYZ(intersection.X + margin, intersection.Y + margin, intersection.Z);

                // Get UIView for zoom
                var uiViews = uiDoc.GetOpenUIViews();
                UIView uiView = null;

                foreach (var uv in uiViews)
                {
                    if (uv.ViewId == activeView.Id)
                    {
                        uiView = uv;
                        break;
                    }
                }

                if (uiView == null && uiViews.Count > 0)
                {
                    uiView = uiViews[0];
                }

                if (uiView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not get UI view for zoom operation"
                    });
                }

                // Zoom to intersection area
                uiView.ZoomAndCenterRectangle(minPoint, maxPoint);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)activeView.Id.Value,
                    viewName = activeView.Name,
                    gridHorizontal = gridH,
                    gridVertical = gridV,
                    intersection = new[] { intersection.X, intersection.Y, intersection.Z },
                    margin = margin,
                    message = $"Zoomed to grid intersection {gridH}/{gridV}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Show an element by finding its view/sheet, opening it, and zooming to it.
        /// This is the "open it up for me" command - finds where an element is and displays it.
        /// Works for elements on sheets (text notes, viewports, etc.) and model elements.
        /// </summary>
        public static string ShowElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiDoc.Document;

                if (parameters == null || parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found with specified ID"
                    });
                }

                // Determine which view/sheet contains this element
                View targetView = null;
                string locationDescription = "";

                // Check if element is owned by a view (text notes, detail items, etc.)
                var ownerViewId = element.OwnerViewId;
                if (ownerViewId != null && ownerViewId != ElementId.InvalidElementId)
                {
                    targetView = doc.GetElement(ownerViewId) as View;
                    if (targetView != null)
                    {
                        locationDescription = targetView is ViewSheet
                            ? $"Sheet '{(targetView as ViewSheet).SheetNumber}'"
                            : $"View '{targetView.Name}'";
                    }
                }

                // If no owner view, check if this IS a view (user wants to open it)
                if (targetView == null && element is View viewElement)
                {
                    targetView = viewElement;
                    locationDescription = $"View '{viewElement.Name}'";
                }

                // If no owner view, try to find a view where this element is visible
                if (targetView == null)
                {
                    // Try floor plans first
                    var floorPlans = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                        .ToList();

                    foreach (var plan in floorPlans)
                    {
                        var bbox = element.get_BoundingBox(plan);
                        if (bbox != null)
                        {
                            targetView = plan;
                            locationDescription = $"Floor Plan '{plan.Name}'";
                            break;
                        }
                    }
                }

                // If still no view, check 3D views
                if (targetView == null)
                {
                    var view3Ds = new FilteredElementCollector(doc)
                        .OfClass(typeof(View3D))
                        .Cast<View3D>()
                        .Where(v => !v.IsTemplate)
                        .ToList();

                    foreach (var v3d in view3Ds)
                    {
                        var bbox = element.get_BoundingBox(v3d);
                        if (bbox != null)
                        {
                            targetView = v3d;
                            locationDescription = $"3D View '{v3d.Name}'";
                            break;
                        }
                    }
                }

                if (targetView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Could not find a view containing element {elementId.Value}. " +
                                "The element may not be visible in any current views.",
                        elementId = (int)elementId.Value,
                        elementType = element.GetType().Name,
                        elementName = element.Name
                    });
                }

                // Can't activate templates
                if (targetView.IsTemplate)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element is in a view template which cannot be opened"
                    });
                }

                // Activate the target view
                uiDoc.ActiveView = targetView;

                // Get the bounding box for zooming
                var elementBbox = element.get_BoundingBox(targetView);
                if (elementBbox == null)
                {
                    elementBbox = element.get_BoundingBox(null);
                }

                // Get the UIView for zoom operations
                var uiViews = uiDoc.GetOpenUIViews();
                UIView uiView = null;

                foreach (var uv in uiViews)
                {
                    if (uv.ViewId == targetView.Id)
                    {
                        uiView = uv;
                        break;
                    }
                }

                if (uiView == null && uiViews.Count > 0)
                {
                    uiView = uiViews[0];
                }

                // Zoom to the element if we have a bounding box
                bool zoomed = false;
                if (uiView != null && elementBbox != null)
                {
                    // For sheet elements, use smaller margin
                    var margin = targetView is ViewSheet ? 0.1 : 5.0;
                    var zoomCorners = new List<XYZ>
                    {
                        new XYZ(elementBbox.Min.X - margin, elementBbox.Min.Y - margin, elementBbox.Min.Z),
                        new XYZ(elementBbox.Max.X + margin, elementBbox.Max.Y + margin, elementBbox.Max.Z)
                    };
                    uiView.ZoomAndCenterRectangle(zoomCorners[0], zoomCorners[1]);
                    zoomed = true;
                }

                // Get element location info
                double[] elementLocation = null;
                if (elementBbox != null)
                {
                    var center = (elementBbox.Min + elementBbox.Max) / 2.0;
                    elementLocation = new[] { center.X, center.Y, center.Z };
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)elementId.Value,
                    elementType = element.GetType().Name,
                    elementName = element.Name ?? "",
                    viewId = (int)targetView.Id.Value,
                    viewName = targetView.Name,
                    viewType = targetView.ViewType.ToString(),
                    location = locationDescription,
                    zoomed = zoomed,
                    elementCenter = elementLocation,
                    message = $"Opened {locationDescription} and zoomed to element"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a legend view
        /// </summary>
        public static string CreateLegendView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewName = parameters["viewName"]?.ToString() ?? "New Legend";
                var scale = parameters["scale"] != null ? int.Parse(parameters["scale"].ToString()) : 96; // Default 1/8" = 1'-0"

                // Get drafting view family type (ViewDrafting requires ViewFamily.Drafting, not Legend)
                var draftingViewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting);

                if (draftingViewFamilyType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Drafting view family type not found in document"
                    });
                }

                using (var trans = new Transaction(doc, "Create Legend View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the drafting view
                    var legendView = ViewDrafting.Create(doc, draftingViewFamilyType.Id);

                    // Set the name
                    legendView.Name = viewName;

                    // Set the scale
                    legendView.Scale = scale;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)legendView.Id.Value,
                        viewName = legendView.Name,
                        viewType = legendView.ViewType.ToString(),
                        scale = legendView.Scale,
                        message = "Legend view created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all legend views in the document
        /// </summary>
        public static string GetLegendViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var legendViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate)
                    .Select(v => new
                    {
                        viewId = (int)v.Id.Value,
                        viewName = v.Name,
                        viewType = v.ViewType.ToString(),
                        scale = v.Scale
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    legendCount = legendViews.Count,
                    legends = legendViews
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all elements visible in a specific view
        /// Parameters:
        /// - viewId: ID of the view to query
        /// - categoryFilter: (optional) Filter by category name (e.g., "Walls", "Text Notes", "Detail Lines")
        /// - includeAnnotations: (optional) Include annotation elements (default true)
        /// - limit: (optional) Maximum number of elements to return (default 500)
        /// </summary>
        public static string GetElementsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Step 1: Get document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }
                var doc = uiApp.ActiveUIDocument.Document;

                // Step 2: Get viewId
                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View not found with ID {viewId.Value}"
                    });
                }

                var categoryFilter = parameters["categoryFilter"]?.ToString();
                var includeAnnotations = parameters["includeAnnotations"]?.ToObject<bool>() ?? true;
                var limit = parameters["limit"]?.ToObject<int>() ?? 500;

                // Step 3: Get elements - use try-catch to identify collector issues
                FilteredElementCollector collector;
                try
                {
                    collector = new FilteredElementCollector(doc, viewId);
                }
                catch (Exception collectorEx)
                {
                    return ResponseBuilder.FromException(collectorEx).Build();
                }

                IEnumerable<Element> elements;

                try
                {
                    if (!string.IsNullOrEmpty(categoryFilter))
                    {
                        // Find the category by name
                        Category targetCategory = null;
                        if (doc.Settings?.Categories != null)
                        {
                            foreach (Category cat in doc.Settings.Categories)
                            {
                                if (cat?.Name != null && cat.Name.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetCategory = cat;
                                    break;
                                }
                            }
                        }

                        if (targetCategory != null)
                        {
                            elements = collector
                                .OfCategoryId(targetCategory.Id)
                                .WhereElementIsNotElementType()
                                .ToElements() ?? new List<Element>();
                        }
                        else
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Category '{categoryFilter}' not found"
                            });
                        }
                    }
                    else
                    {
                        elements = collector
                            .WhereElementIsNotElementType()
                            .ToElements() ?? new List<Element>();
                    }
                }
                catch (Exception elementsEx)
                {
                    return ResponseBuilder.FromException(elementsEx).Build();
                }

                // Filter out annotation elements if requested
                if (!includeAnnotations && elements != null)
                {
                    elements = elements.Where(e =>
                        e == null || e.Category == null ||
                        e.Category.CategoryType != CategoryType.Annotation);
                }

                // Ensure elements is not null
                elements = elements ?? new List<Element>();

                // Build the result - filter out null elements and wrap in try-catch
                var elementList = new List<object>();
                foreach (var e in elements.Take(limit))
                {
                    if (e == null || e.Id == null) continue;

                    try
                    {
                        object location = null;
                        try
                        {
                            if (e.Location is LocationPoint lp)
                            {
                                location = new { type = "Point", x = lp.Point.X, y = lp.Point.Y, z = lp.Point.Z };
                            }
                            else if (e.Location is LocationCurve lc)
                            {
                                var start = lc.Curve.GetEndPoint(0);
                                var end = lc.Curve.GetEndPoint(1);
                                location = new { type = "Curve", startX = start.X, startY = start.Y, endX = end.X, endY = end.Y };
                            }
                        }
                        catch { }

                        // Get type and family info
                        string typeName = null;
                        string familyName = null;
                        try
                        {
                            var typeId = e.GetTypeId();
                            if (typeId != null && typeId != ElementId.InvalidElementId)
                            {
                                var elementType = doc.GetElement(typeId);
                                typeName = elementType?.Name;

                                // For family instances, get the family name
                                if (elementType is FamilySymbol fs)
                                {
                                    familyName = fs.Family?.Name;
                                }
                            }
                            // For family instances without type, try direct access
                            if (e is FamilyInstance fi && familyName == null)
                            {
                                familyName = fi.Symbol?.Family?.Name;
                                typeName = typeName ?? fi.Symbol?.Name;
                            }
                        }
                        catch { }

                        // Get element name safely
                        string elementName = "";
                        try { elementName = e.Name ?? ""; } catch { }

                        elementList.Add(new
                        {
                            id = (int)e.Id.Value,
                            name = elementName,
                            category = e.Category?.Name ?? "Unknown",
                            categoryType = e.Category?.CategoryType.ToString() ?? "Unknown",
                            typeName = typeName,
                            familyName = familyName,
                            location = location
                        });
                    }
                    catch
                    {
                        // Skip elements that throw exceptions
                    }
                }

                // Group by category for summary (use dynamic to access anonymous type properties)
                var categorySummary = elementList
                    .GroupBy(e => ((dynamic)e).category as string ?? "Unknown")
                    .Select(g => new { category = g.Key, count = g.Count() })
                    .OrderByDescending(g => g.count)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    totalElements = elementList.Count,
                    limitApplied = elementList.Count >= limit,
                    categorySummary = categorySummary,
                    elements = elementList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set far clip offset for elevation/section views
        /// </summary>
        public static string SetViewFarClip(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var viewSection = view as ViewSection;
                if (viewSection == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View is not a section/elevation view" });
                }

                double farClipOffset = parameters["farClipOffset"]?.ToObject<double>() ?? 70.0;
                string methodUsed = "none";
                string errorDetails = "";
                double originalDepth = 0;
                double newDepth = 0;

                using (var trans = new Transaction(doc, "Set Far Clip"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get current crop box
                    var cropBox = view.CropBox;
                    originalDepth = Math.Abs(cropBox.Max.Z - cropBox.Min.Z);

                    // Method 1: Try setting VIEWER_BOUND_OFFSET_FAR parameter
                    Parameter farParam = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                    if (farParam != null)
                    {
                        if (!farParam.IsReadOnly)
                        {
                            farParam.Set(farClipOffset);
                            methodUsed = "VIEWER_BOUND_OFFSET_FAR parameter";
                        }
                        else
                        {
                            errorDetails += "VIEWER_BOUND_OFFSET_FAR is read-only. ";
                        }
                    }
                    else
                    {
                        errorDetails += "VIEWER_BOUND_OFFSET_FAR not found. ";
                    }

                    // Method 2: Ensure far clipping is active
                    Parameter farClipActive = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                    if (farClipActive != null && !farClipActive.IsReadOnly)
                    {
                        int currentMode = farClipActive.AsInteger();
                        if (currentMode == 0) // No clip
                        {
                            farClipActive.Set(2); // Clip without line
                            methodUsed += " + enabled far clipping";
                        }
                    }

                    // Method 3: Try recreating the crop box with new Z extent
                    var transform = cropBox.Transform;
                    var newCropBox = new BoundingBoxXYZ();
                    newCropBox.Transform = transform;

                    // Keep X/Y, set new Z range for depth
                    newCropBox.Min = new XYZ(cropBox.Min.X, cropBox.Min.Y, -farClipOffset);
                    newCropBox.Max = new XYZ(cropBox.Max.X, cropBox.Max.Y, 0);

                    view.CropBox = newCropBox;

                    if (methodUsed == "none")
                    {
                        methodUsed = "CropBox Z values";
                    }

                    trans.Commit();

                    // Verify result
                    var finalCropBox = view.CropBox;
                    newDepth = Math.Abs(finalCropBox.Max.Z - finalCropBox.Min.Z);
                }

                bool depthChanged = Math.Abs(newDepth - originalDepth) > 1.0;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    requestedFarClip = farClipOffset,
                    originalDepth = originalDepth,
                    newDepth = newDepth,
                    depthChanged = depthChanged,
                    methodUsed = methodUsed,
                    notes = errorDetails
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Hide or show a category in a view
        /// </summary>
        public static string SetCategoryHidden(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Get category by name or built-in category
                string categoryName = parameters["categoryName"]?.ToString();
                bool hidden = parameters["hidden"]?.ToObject<bool>() ?? true;

                ElementId categoryId = null;

                // Map common category names to built-in categories
                if (categoryName != null)
                {
                    switch (categoryName.ToLower())
                    {
                        case "grids":
                            categoryId = new ElementId(BuiltInCategory.OST_Grids);
                            break;
                        case "levels":
                            categoryId = new ElementId(BuiltInCategory.OST_Levels);
                            break;
                        case "rooms":
                            categoryId = new ElementId(BuiltInCategory.OST_Rooms);
                            break;
                        case "walls":
                            categoryId = new ElementId(BuiltInCategory.OST_Walls);
                            break;
                        case "doors":
                            categoryId = new ElementId(BuiltInCategory.OST_Doors);
                            break;
                        case "windows":
                            categoryId = new ElementId(BuiltInCategory.OST_Windows);
                            break;
                        case "furniture":
                            categoryId = new ElementId(BuiltInCategory.OST_Furniture);
                            break;
                        case "text notes":
                        case "textnotes":
                            categoryId = new ElementId(BuiltInCategory.OST_TextNotes);
                            break;
                        case "dimensions":
                            categoryId = new ElementId(BuiltInCategory.OST_Dimensions);
                            break;
                        default:
                            // Try to find category by name in document
                            foreach (Category cat in doc.Settings.Categories)
                            {
                                if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                                {
                                    categoryId = cat.Id;
                                    break;
                                }
                            }
                            break;
                    }
                }

                if (categoryId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Category '{categoryName}' not found" });
                }

                using (var trans = new Transaction(doc, "Set Category Hidden"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.SetCategoryHidden(categoryId, hidden);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        categoryName = categoryName,
                        hidden = hidden
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Hide multiple categories in a view at once
        /// </summary>
        public static string HideCategoriesInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var categoryNames = parameters["categories"]?.ToObject<string[]>() ?? new string[0];
                bool hidden = parameters["hidden"]?.ToObject<bool>() ?? true;

                var results = new List<object>();

                using (var trans = new Transaction(doc, "Hide Categories"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var categoryName in categoryNames)
                    {
                        ElementId categoryId = null;

                        switch (categoryName.ToLower())
                        {
                            case "grids":
                                categoryId = new ElementId(BuiltInCategory.OST_Grids);
                                break;
                            case "levels":
                                categoryId = new ElementId(BuiltInCategory.OST_Levels);
                                break;
                            case "rooms":
                                categoryId = new ElementId(BuiltInCategory.OST_Rooms);
                                break;
                            case "text notes":
                            case "textnotes":
                                categoryId = new ElementId(BuiltInCategory.OST_TextNotes);
                                break;
                            case "dimensions":
                                categoryId = new ElementId(BuiltInCategory.OST_Dimensions);
                                break;
                            default:
                                foreach (Category cat in doc.Settings.Categories)
                                {
                                    if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        categoryId = cat.Id;
                                        break;
                                    }
                                }
                                break;
                        }

                        if (categoryId != null)
                        {
                            try
                            {
                                view.SetCategoryHidden(categoryId, hidden);
                                results.Add(new { category = categoryName, success = true });
                            }
                            catch (Exception ex)
                            {
                                results.Add(new { category = categoryName, success = false, error = ex.Message });
                            }
                        }
                        else
                        {
                            results.Add(new { category = categoryName, success = false, error = "Category not found" });
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        hidden = hidden,
                        results = results
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Hide specific elements in a view
        /// </summary>
        public static string HideElementsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                var elementIdsList = new List<ElementId>();
                foreach (var idToken in parameters["elementIds"])
                {
                    elementIdsList.Add(new ElementId(int.Parse(idToken.ToString())));
                }

                using (var trans = new Transaction(doc, "Hide Elements in View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.HideElements(elementIdsList);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        hiddenCount = elementIdsList.Count,
                        elementIds = elementIdsList.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Unhide specific elements in a view
        /// </summary>
        public static string UnhideElementsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                var elementIdsList = new List<ElementId>();
                foreach (var idToken in parameters["elementIds"])
                {
                    elementIdsList.Add(new ElementId(int.Parse(idToken.ToString())));
                }

                using (var trans = new Transaction(doc, "Unhide Elements in View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.UnhideElements(elementIdsList);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        unhiddenCount = elementIdsList.Count,
                        elementIds = elementIdsList.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get room tags in a view with their associated room IDs
        /// </summary>
        public static string GetRoomTagsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Get all room tags in the view
                var roomTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_RoomTags)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var tagData = new List<object>();
                foreach (var element in roomTags)
                {
                    var roomTag = element as RoomTag;
                    if (roomTag != null)
                    {
                        var room = roomTag.Room;
                        var tagLocation = roomTag.TagHeadPosition;

                        tagData.Add(new
                        {
                            tagId = (int)roomTag.Id.Value,
                            roomId = room != null ? (int?)room.Id.Value : null,
                            roomName = room?.Name,
                            roomNumber = room?.Number,
                            location = new { x = tagLocation.X, y = tagLocation.Y, z = tagLocation.Z }
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    count = tagData.Count,
                    roomTags = tagData
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Force regeneration of view/document to update displayed values
        /// </summary>
        public static string RegenerateView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters?["viewId"] != null
                    ? new ElementId(int.Parse(parameters["viewId"].ToString()))
                    : ElementId.InvalidElementId;

                using (var trans = new Transaction(doc, "Regenerate"))
                {
                    trans.Start();

                    // Force document regeneration
                    doc.Regenerate();

                    trans.Commit();
                }

                // If specific view requested, refresh it
                if (viewId != ElementId.InvalidElementId)
                {
                    var view = doc.GetElement(viewId) as View;
                    if (view != null)
                    {
                        uiApp.ActiveUIDocument.RefreshActiveView();
                    }
                }
                else
                {
                    // Refresh current active view
                    uiApp.ActiveUIDocument.RefreshActiveView();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Document regenerated and view refreshed"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Verify room parameter values match expected values (for validation after batch operations)
        /// </summary>
        public static string VerifyRoomValues(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var expectedValues = parameters?["expectedValues"]?.ToObject<List<JObject>>();
                var parameterName = parameters?["parameterName"]?.ToString() ?? "Comments";

                if (expectedValues == null || expectedValues.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "expectedValues array is required with objects containing roomId and expectedValue"
                    });
                }

                var results = new List<object>();
                int matchCount = 0;
                int mismatchCount = 0;

                foreach (var expected in expectedValues)
                {
                    var roomId = new ElementId(expected["roomId"].ToObject<int>());
                    var expectedValue = expected["expectedValue"]?.ToString();

                    var room = doc.GetElement(roomId) as Room;
                    if (room == null)
                    {
                        results.Add(new
                        {
                            roomId = (int)roomId.Value,
                            status = "NOT_FOUND",
                            expectedValue = expectedValue,
                            actualValue = (string)null,
                            match = false
                        });
                        mismatchCount++;
                        continue;
                    }

                    var param = room.LookupParameter(parameterName);
                    var actualValue = param?.AsString() ?? "";

                    var isMatch = actualValue.Equals(expectedValue, StringComparison.OrdinalIgnoreCase) ||
                                  actualValue.Replace(" ", "").Equals(expectedValue?.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

                    if (isMatch)
                        matchCount++;
                    else
                        mismatchCount++;

                    results.Add(new
                    {
                        roomId = (int)roomId.Value,
                        roomNumber = room.Number,
                        roomName = room.Name,
                        status = isMatch ? "MATCH" : "MISMATCH",
                        expectedValue = expectedValue,
                        actualValue = actualValue,
                        match = isMatch
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new
                    {
                        total = expectedValues.Count,
                        matches = matchCount,
                        mismatches = mismatchCount,
                        allMatch = mismatchCount == 0
                    },
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Compare expected vs actual element parameter values across multiple elements
        /// </summary>
        public static string CompareExpectedActual(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var comparisons = parameters?["comparisons"]?.ToObject<List<JObject>>();

                if (comparisons == null || comparisons.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "comparisons array is required with objects containing elementId, parameterName, expectedValue"
                    });
                }

                var results = new List<object>();
                int passCount = 0;
                int failCount = 0;

                foreach (var comp in comparisons)
                {
                    var elementId = new ElementId(comp["elementId"].ToObject<int>());
                    var paramName = comp["parameterName"]?.ToString();
                    var expectedValue = comp["expectedValue"]?.ToString();

                    var element = doc.GetElement(elementId);
                    if (element == null)
                    {
                        results.Add(new
                        {
                            elementId = (int)elementId.Value,
                            parameterName = paramName,
                            status = "ELEMENT_NOT_FOUND",
                            pass = false
                        });
                        failCount++;
                        continue;
                    }

                    var param = element.LookupParameter(paramName);
                    if (param == null)
                    {
                        results.Add(new
                        {
                            elementId = (int)elementId.Value,
                            elementName = element.Name,
                            parameterName = paramName,
                            status = "PARAMETER_NOT_FOUND",
                            pass = false
                        });
                        failCount++;
                        continue;
                    }

                    string actualValue;
                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            actualValue = param.AsString() ?? "";
                            break;
                        case StorageType.Double:
                            actualValue = param.AsDouble().ToString();
                            break;
                        case StorageType.Integer:
                            actualValue = param.AsInteger().ToString();
                            break;
                        case StorageType.ElementId:
                            actualValue = param.AsElementId()?.Value.ToString() ?? "";
                            break;
                        default:
                            actualValue = param.AsValueString() ?? "";
                            break;
                    }

                    var isMatch = actualValue.Equals(expectedValue, StringComparison.OrdinalIgnoreCase) ||
                                  actualValue.Replace(" ", "").Equals(expectedValue?.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

                    if (isMatch)
                        passCount++;
                    else
                        failCount++;

                    results.Add(new
                    {
                        elementId = (int)elementId.Value,
                        elementName = element.Name,
                        elementCategory = element.Category?.Name,
                        parameterName = paramName,
                        expectedValue = expectedValue,
                        actualValue = actualValue,
                        status = isMatch ? "PASS" : "FAIL",
                        pass = isMatch
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new
                    {
                        total = comparisons.Count,
                        passed = passCount,
                        failed = failCount,
                        allPassed = failCount == 0
                    },
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #region View Template Methods (CreateViewTemplate, DuplicateViewTemplate)
        // Note: GetViewTemplates and ApplyViewTemplate are defined earlier in this file

        /// <summary>
        /// Create a view template from an existing view
        /// Parameters: sourceViewId, templateName
        /// </summary>
        public static string CreateViewTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceViewId = parameters["sourceViewId"]?.Value<int>() ?? 0;
                var templateName = parameters["templateName"]?.ToString();

                if (sourceViewId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sourceViewId is required" });
                }

                if (string.IsNullOrEmpty(templateName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "templateName is required" });
                }

                var sourceView = doc.GetElement(new ElementId(sourceViewId)) as View;
                if (sourceView == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Source view not found" });
                }

                using (var trans = new Transaction(doc, "Create View Template"))
                {
                    trans.Start();

                    var template = sourceView.CreateViewTemplate();

                    if (template != null)
                    {
                        template.Name = templateName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        templateId = template?.Id.Value ?? 0,
                        templateName = templateName,
                        sourceViewName = sourceView.Name,
                        viewType = sourceView.ViewType.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate an existing view template
        /// Parameters: templateId, newName
        /// </summary>
        public static string DuplicateViewTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var templateId = parameters["templateId"]?.Value<int>() ?? 0;
                var newName = parameters["newName"]?.ToString();

                if (templateId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "templateId is required" });
                }

                if (string.IsNullOrEmpty(newName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "newName is required" });
                }

                var template = doc.GetElement(new ElementId(templateId)) as View;
                if (template == null || !template.IsTemplate)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View template not found" });
                }

                using (var trans = new Transaction(doc, "Duplicate View Template"))
                {
                    trans.Start();

                    var newTemplateId = template.Duplicate(ViewDuplicateOption.Duplicate);
                    var newTemplate = doc.GetElement(newTemplateId) as View;

                    if (newTemplate != null)
                    {
                        newTemplate.Name = newName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        newTemplateId = newTemplateId.Value,
                        newTemplateName = newName,
                        sourceTemplateName = template.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Scope Box Methods

        /// <summary>
        /// Get all scope boxes in the document
        /// Parameters: none
        /// </summary>
        public static string GetScopeBoxes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var scopeBoxes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                    .WhereElementIsNotElementType()
                    .Select(sb =>
                    {
                        var bbox = sb.get_BoundingBox(null);
                        return new
                        {
                            scopeBoxId = sb.Id.Value,
                            name = sb.Name,
                            minPoint = bbox != null ? new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z } : null,
                            maxPoint = bbox != null ? new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z } : null
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = scopeBoxes.Count,
                    scopeBoxes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a new scope box
        /// Parameters: name, minPoint {x, y, z}, maxPoint {x, y, z}
        /// </summary>
        public static string CreateScopeBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var name = parameters["name"]?.ToString();
                var minPointObj = parameters["minPoint"]?.ToObject<Dictionary<string, double>>();
                var maxPointObj = parameters["maxPoint"]?.ToObject<Dictionary<string, double>>();

                if (string.IsNullOrEmpty(name))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "name is required" });
                }

                if (minPointObj == null || maxPointObj == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "minPoint and maxPoint are required" });
                }

                var minPoint = new XYZ(minPointObj["x"], minPointObj["y"], minPointObj["z"]);
                var maxPoint = new XYZ(maxPointObj["x"], maxPointObj["y"], maxPointObj["z"]);

                using (var trans = new Transaction(doc, "Create Scope Box"))
                {
                    trans.Start();

                    // Create scope box using a 3D view
                    var view3D = new FilteredElementCollector(doc)
                        .OfClass(typeof(View3D))
                        .Cast<View3D>()
                        .FirstOrDefault(v => !v.IsTemplate);

                    if (view3D == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "No 3D view available to create scope box" });
                    }

                    // Create outline for the scope box
                    var outline = new Outline(minPoint, maxPoint);
                    var boundingBox = new BoundingBoxXYZ
                    {
                        Min = minPoint,
                        Max = maxPoint
                    };

                    // Use DirectShape to create the scope box
                    var scopeBox = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_VolumeOfInterest));

                    // Create geometry for the scope box
                    var width = maxPoint.X - minPoint.X;
                    var depth = maxPoint.Y - minPoint.Y;
                    var height = maxPoint.Z - minPoint.Z;

                    var center = new XYZ((minPoint.X + maxPoint.X) / 2, (minPoint.Y + maxPoint.Y) / 2, (minPoint.Z + maxPoint.Z) / 2);

                    // Create a simple box geometry
                    var curves = new List<Curve>();
                    // Bottom rectangle
                    curves.Add(Line.CreateBound(new XYZ(minPoint.X, minPoint.Y, minPoint.Z), new XYZ(maxPoint.X, minPoint.Y, minPoint.Z)));
                    curves.Add(Line.CreateBound(new XYZ(maxPoint.X, minPoint.Y, minPoint.Z), new XYZ(maxPoint.X, maxPoint.Y, minPoint.Z)));
                    curves.Add(Line.CreateBound(new XYZ(maxPoint.X, maxPoint.Y, minPoint.Z), new XYZ(minPoint.X, maxPoint.Y, minPoint.Z)));
                    curves.Add(Line.CreateBound(new XYZ(minPoint.X, maxPoint.Y, minPoint.Z), new XYZ(minPoint.X, minPoint.Y, minPoint.Z)));

                    var curveLoop = CurveLoop.Create(curves);
                    var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { curveLoop }, XYZ.BasisZ, height);

                    scopeBox.SetShape(new GeometryObject[] { solid });
                    scopeBox.Name = name;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scopeBoxId = scopeBox.Id.Value,
                        name,
                        minPoint = new { x = minPoint.X, y = minPoint.Y, z = minPoint.Z },
                        maxPoint = new { x = maxPoint.X, y = maxPoint.Y, z = maxPoint.Z }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Apply a scope box to views
        /// Parameters: scopeBoxId, viewIds (array)
        /// </summary>
        public static string ApplyScopeBoxToView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var scopeBoxId = parameters["scopeBoxId"]?.Value<int>() ?? 0;
                var viewIdArray = parameters["viewIds"]?.ToObject<int[]>();

                if (scopeBoxId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "scopeBoxId is required" });
                }

                if (viewIdArray == null || viewIdArray.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewIds array is required" });
                }

                var scopeBox = doc.GetElement(new ElementId(scopeBoxId));
                if (scopeBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Scope box not found" });
                }

                var updatedViews = new List<object>();
                var failedViews = new List<object>();

                using (var trans = new Transaction(doc, "Apply Scope Box to Views"))
                {
                    trans.Start();

                    foreach (var viewId in viewIdArray)
                    {
                        try
                        {
                            var view = doc.GetElement(new ElementId(viewId)) as View;
                            if (view == null)
                            {
                                failedViews.Add(new { viewId, error = "View not found" });
                                continue;
                            }

                            // Get the scope box parameter
                            var scopeBoxParam = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                            if (scopeBoxParam != null && !scopeBoxParam.IsReadOnly)
                            {
                                scopeBoxParam.Set(scopeBox.Id);
                                updatedViews.Add(new
                                {
                                    viewId,
                                    viewName = view.Name
                                });
                            }
                            else
                            {
                                failedViews.Add(new { viewId, viewName = view.Name, error = "View does not support scope boxes" });
                            }
                        }
                        catch (Exception ex)
                        {
                            failedViews.Add(new { viewId, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    scopeBoxName = scopeBox.Name,
                    updatedCount = updatedViews.Count,
                    failedCount = failedViews.Count,
                    updatedViews,
                    failedViews
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove scope box from views (set to None)
        /// Parameters: viewIds (array)
        /// </summary>
        public static string RemoveScopeBoxFromView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdArray = parameters["viewIds"]?.ToObject<int[]>();

                if (viewIdArray == null || viewIdArray.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewIds array is required" });
                }

                var updatedViews = new List<object>();
                var failedViews = new List<object>();

                using (var trans = new Transaction(doc, "Remove Scope Box from Views"))
                {
                    trans.Start();

                    foreach (var viewId in viewIdArray)
                    {
                        try
                        {
                            var view = doc.GetElement(new ElementId(viewId)) as View;
                            if (view == null)
                            {
                                failedViews.Add(new { viewId, error = "View not found" });
                                continue;
                            }

                            var scopeBoxParam = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                            if (scopeBoxParam != null && !scopeBoxParam.IsReadOnly)
                            {
                                scopeBoxParam.Set(ElementId.InvalidElementId);
                                updatedViews.Add(new
                                {
                                    viewId,
                                    viewName = view.Name
                                });
                            }
                            else
                            {
                                failedViews.Add(new { viewId, viewName = view.Name, error = "View does not support scope boxes" });
                            }
                        }
                        catch (Exception ex)
                        {
                            failedViews.Add(new { viewId, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    updatedCount = updatedViews.Count,
                    failedCount = failedViews.Count,
                    updatedViews,
                    failedViews
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Legend Methods

        /// <summary>
        /// Get all legend views in the document
        /// Parameters: none
        /// </summary>
        public static string GetLegends(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var legends = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate)
                    .Select(v => new
                    {
                        legendId = v.Id.Value,
                        name = v.Name,
                        scale = v.Scale
                    })
                    .OrderBy(l => l.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = legends.Count,
                    legends
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a new legend view by duplicating an existing one
        /// Parameters: name, sourceLegendId (optional - will use first existing legend if not provided), scale (optional)
        /// </summary>
        public static string CreateLegend(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var name = parameters["name"]?.ToString();
                var sourceLegendId = parameters["sourceLegendId"]?.Value<int>() ?? 0;
                var scale = parameters["scale"]?.Value<int>() ?? 0;

                if (string.IsNullOrEmpty(name))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "name is required" });
                }

                // Find source legend to duplicate
                View sourceLegend = null;
                if (sourceLegendId > 0)
                {
                    sourceLegend = doc.GetElement(new ElementId(sourceLegendId)) as View;
                }
                else
                {
                    // Find first existing legend
                    sourceLegend = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .FirstOrDefault(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
                }

                if (sourceLegend == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No source legend found to duplicate. Create a legend manually in Revit first." });
                }

                using (var trans = new Transaction(doc, "Create Legend"))
                {
                    trans.Start();

                    var newLegendId = sourceLegend.Duplicate(ViewDuplicateOption.Duplicate);
                    var newLegend = doc.GetElement(newLegendId) as View;

                    if (newLegend != null)
                    {
                        newLegend.Name = name;
                        if (scale > 0)
                        {
                            newLegend.Scale = scale;
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        legendId = newLegendId.Value,
                        name = newLegend?.Name,
                        scale = newLegend?.Scale ?? 0,
                        duplicatedFrom = sourceLegend.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get legend components (symbols that can be placed in legends)
        /// Parameters: category (optional filter)
        /// </summary>
        public static string GetLegendComponents(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryFilter = parameters["category"]?.ToString();

                // Get family symbols that can be placed as legend components
                var symbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family != null)
                    .Select(fs => new
                    {
                        symbolId = fs.Id.Value,
                        familyName = fs.Family.Name,
                        typeName = fs.Name,
                        category = fs.Category?.Name ?? "Unknown"
                    });

                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    symbols = symbols.Where(s => s.category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase));
                }

                var symbolList = symbols.OrderBy(s => s.category).ThenBy(s => s.familyName).ThenBy(s => s.typeName).ToList();

                // Group by category
                var grouped = symbolList.GroupBy(s => s.category)
                    .Select(g => new
                    {
                        category = g.Key,
                        count = g.Count(),
                        symbols = g.ToList()
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalSymbols = symbolList.Count,
                    categories = grouped
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Assembly Views Methods

        /// <summary>
        /// Create an assembly from selected elements
        /// Parameters: elementIds (array of element IDs), name (optional)
        /// </summary>
        public static string CreateAssembly(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds is required" });

                var elementIdInts = parameters["elementIds"].ToObject<int[]>();
                string name = parameters["name"]?.ToString();

                if (elementIdInts == null || elementIdInts.Length == 0)
                    return JsonConvert.SerializeObject(new { success = false, error = "At least one element ID is required" });

                // Convert to ElementId collection
                var elementIds = elementIdInts.Select(id => new ElementId(id)).ToList();

                // Verify all elements exist
                foreach (var id in elementIds)
                {
                    if (doc.GetElement(id) == null)
                        return JsonConvert.SerializeObject(new { success = false, error = $"Element {id.Value} not found" });
                }

                // Get a category from the first element (required for assembly)
                var firstElement = doc.GetElement(elementIds[0]);
                var categoryId = firstElement.Category?.Id ?? new ElementId(BuiltInCategory.OST_GenericModel);

                using (var trans = new Transaction(doc, "Create Assembly"))
                {
                    trans.Start();

                    // Create the assembly
                    var assembly = AssemblyInstance.Create(doc, elementIds, categoryId);

                    if (assembly == null)
                        return JsonConvert.SerializeObject(new { success = false, error = "Failed to create assembly" });

                    // Set name if provided
                    if (!string.IsNullOrEmpty(name))
                    {
                        var assemblyType = doc.GetElement(assembly.GetTypeId()) as AssemblyType;
                        if (assemblyType != null)
                        {
                            assemblyType.Name = name;
                        }
                    }

                    trans.Commit();

                    // Get assembly info
                    var assemblyType2 = doc.GetElement(assembly.GetTypeId()) as AssemblyType;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        assemblyId = (int)assembly.Id.Value,
                        assemblyTypeId = (int)assembly.GetTypeId().Value,
                        name = assemblyType2?.Name ?? "Assembly",
                        memberCount = elementIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create views for an assembly
        /// Note: AssemblyViewUtils is not fully available in Revit 2026 API, use manual view creation instead
        /// </summary>
        public static string CreateAssemblyViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["assemblyId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "assemblyId is required" });

                int assemblyIdInt = parameters["assemblyId"].ToObject<int>();

                var assembly = doc.GetElement(new ElementId(assemblyIdInt)) as AssemblyInstance;
                if (assembly == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "Assembly not found" });

                // Note: Direct assembly view creation via AssemblyViewUtils not available
                // Return info about the assembly instead
                var assemblyType = doc.GetElement(assembly.GetTypeId()) as AssemblyType;

                // Check if views already exist for this assembly
                var existingViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.AssociatedAssemblyInstanceId == assembly.Id)
                    .Select(v => new
                    {
                        viewId = (int)v.Id.Value,
                        viewName = v.Name,
                        viewType = v.ViewType.ToString()
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Assembly view creation via API limited in Revit 2026. Use Revit UI or create views manually.",
                    assemblyId = assemblyIdInt,
                    assemblyName = assemblyType?.Name ?? "Assembly",
                    existingViewCount = existingViews.Count,
                    existingViews = existingViews
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all assemblies in the document
        /// </summary>
        public static string GetAssemblies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all assembly instances
                var assemblies = new FilteredElementCollector(doc)
                    .OfClass(typeof(AssemblyInstance))
                    .Cast<AssemblyInstance>()
                    .Select(a =>
                    {
                        var assemblyType = doc.GetElement(a.GetTypeId()) as AssemblyType;
                        var memberIds = a.GetMemberIds();

                        // Get associated views
                        var views = new FilteredElementCollector(doc)
                            .OfClass(typeof(View))
                            .Cast<View>()
                            .Where(v => v.AssociatedAssemblyInstanceId == a.Id)
                            .Select(v => new
                            {
                                viewId = (int)v.Id.Value,
                                viewName = v.Name,
                                viewType = v.ViewType.ToString()
                            })
                            .ToList();

                        return new
                        {
                            assemblyId = (int)a.Id.Value,
                            assemblyTypeId = (int)a.GetTypeId().Value,
                            name = assemblyType?.Name ?? "Assembly",
                            memberCount = memberIds.Count,
                            memberIds = memberIds.Select(id => (int)id.Value).ToList(),
                            viewCount = views.Count,
                            views = views
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = assemblies.Count,
                    assemblies = assemblies
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Insert Views From File

        /// <summary>
        /// Insert views from another Revit file into the current document.
        /// Supports drafting views, legends, schedules, and other view types.
        /// </summary>
        /// <param name="uiApp">UIApplication instance</param>
        /// <param name="parameters">
        /// filePath (required): Path to source RVT file
        /// viewTypes (optional): Array of view types to import ["DraftingView", "Legend", "FloorPlan", "Schedule"]
        /// viewNames (optional): Array of specific view names to import
        /// importAll (optional): Boolean to import all importable views (default: false)
        /// </param>
        public static string InsertViewsFromFile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var app = uiApp.Application;

                // Validate file path
                var filePath = parameters["filePath"]?.ToString();
                if (string.IsNullOrEmpty(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filePath is required"
                    });
                }

                if (!System.IO.File.Exists(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"File not found: {filePath}"
                    });
                }

                // Parse parameters
                var viewTypes = parameters["viewTypes"]?.ToObject<string[]>() ?? new string[0];
                var viewNames = parameters["viewNames"]?.ToObject<string[]>() ?? new string[0];
                var importAll = parameters["importAll"]?.Value<bool>() ?? false;

                // If no filters specified and importAll is false, return error
                if (viewTypes.Length == 0 && viewNames.Length == 0 && !importAll)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Specify viewTypes, viewNames, or set importAll to true"
                    });
                }

                // Open source document (detached, no worksets)
                var openOptions = new OpenOptions();
                openOptions.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;

                // Don't open worksets to speed up loading
                var worksetConfig = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
                openOptions.SetOpenWorksetsConfiguration(worksetConfig);

                Document sourceDoc = null;
                var copiedViews = new List<object>();
                var errors = new List<string>();

                try
                {
                    // Open source document
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    sourceDoc = app.OpenDocumentFile(modelPath, openOptions);

                    if (sourceDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to open source document"
                        });
                    }

                    // Collect views from source document
                    var allViews = new FilteredElementCollector(sourceDoc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted)
                        .ToList();

                    // Filter views based on parameters
                    var viewsToImport = new List<View>();

                    foreach (var view in allViews)
                    {
                        var viewTypeName = view.ViewType.ToString();
                        var viewName = view.Name;

                        // Skip system views
                        if (viewName == "Project Browser" || viewName == "System Browser")
                            continue;

                        bool shouldImport = false;

                        if (importAll)
                        {
                            // Import all importable view types
                            if (viewTypeName == "DraftingView" || viewTypeName == "Legend" ||
                                viewTypeName == "Schedule" || viewTypeName == "FloorPlan" ||
                                viewTypeName == "CeilingPlan" || viewTypeName == "Section" ||
                                viewTypeName == "Elevation" || viewTypeName == "ThreeD" ||
                                viewTypeName == "AreaPlan" || viewTypeName == "EngineeringPlan")
                            {
                                shouldImport = true;
                            }
                        }
                        else
                        {
                            // Check if matches specified types
                            if (viewTypes.Length > 0 && viewTypes.Contains(viewTypeName, StringComparer.OrdinalIgnoreCase))
                            {
                                shouldImport = true;
                            }

                            // Check if matches specified names
                            if (viewNames.Length > 0 && viewNames.Contains(viewName, StringComparer.OrdinalIgnoreCase))
                            {
                                shouldImport = true;
                            }
                        }

                        if (shouldImport)
                        {
                            viewsToImport.Add(view);
                        }
                    }

                    if (viewsToImport.Count == 0)
                    {
                        sourceDoc.Close(false);
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No matching views found in source document",
                            availableViews = allViews.Select(v => new { name = v.Name, type = v.ViewType.ToString() }).Take(50).ToList()
                        });
                    }

                    // Copy views to target document
                    using (var trans = new Transaction(doc, "Insert Views from File"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        foreach (var sourceView in viewsToImport)
                        {
                            try
                            {
                                ElementId newViewId = ElementId.InvalidElementId;
                                var viewTypeName = sourceView.ViewType.ToString();

                                // Different handling based on view type
                                if (sourceView is ViewDrafting)
                                {
                                    // For drafting views, copy the view and all its elements
                                    var draftingView = sourceView as ViewDrafting;

                                    // Create new drafting view
                                    var viewFamilyTypeId = new FilteredElementCollector(doc)
                                        .OfClass(typeof(ViewFamilyType))
                                        .Cast<ViewFamilyType>()
                                        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting)
                                        ?.Id;

                                    if (viewFamilyTypeId != null)
                                    {
                                        var newDraftingView = ViewDrafting.Create(doc, viewFamilyTypeId);

                                        // Try to set name (may fail if duplicate)
                                        try
                                        {
                                            newDraftingView.Name = sourceView.Name;
                                        }
                                        catch
                                        {
                                            newDraftingView.Name = sourceView.Name + "_imported";
                                        }

                                        newDraftingView.Scale = sourceView.Scale;
                                        newViewId = newDraftingView.Id;

                                        // Copy all elements from source drafting view
                                        var elementsInView = new FilteredElementCollector(sourceDoc, sourceView.Id)
                                            .WhereElementIsNotElementType()
                                            .ToElementIds()
                                            .ToList();

                                        if (elementsInView.Count > 0)
                                        {
                                            var copiedIds = ElementTransformUtils.CopyElements(
                                                sourceView,
                                                elementsInView,
                                                newDraftingView,
                                                Transform.Identity,
                                                new CopyPasteOptions());
                                        }
                                    }
                                }
                                else if (sourceView.ViewType == ViewType.Legend)
                                {
                                    // For legends in Revit 2026, we must duplicate an existing legend
                                    // Find an existing legend to use as base
                                    var existingLegend = new FilteredElementCollector(doc)
                                        .OfClass(typeof(View))
                                        .Cast<View>()
                                        .FirstOrDefault(v => v.ViewType == ViewType.Legend && !v.IsTemplate);

                                    if (existingLegend != null)
                                    {
                                        // Duplicate the existing legend
                                        var newLegendId = existingLegend.Duplicate(ViewDuplicateOption.Duplicate);
                                        var newLegend = doc.GetElement(newLegendId) as View;

                                        if (newLegend != null)
                                        {
                                            try
                                            {
                                                newLegend.Name = sourceView.Name;
                                            }
                                            catch
                                            {
                                                newLegend.Name = sourceView.Name + "_imported";
                                            }

                                            newLegend.Scale = sourceView.Scale;
                                            newViewId = newLegend.Id;

                                            // Delete existing content in duplicated legend
                                            var existingElements = new FilteredElementCollector(doc, newLegend.Id)
                                                .WhereElementIsNotElementType()
                                                .ToElementIds()
                                                .ToList();

                                            if (existingElements.Count > 0)
                                            {
                                                doc.Delete(existingElements);
                                            }

                                            // Copy legend elements from source
                                            var elementsInView = new FilteredElementCollector(sourceDoc, sourceView.Id)
                                                .WhereElementIsNotElementType()
                                                .ToElementIds()
                                                .ToList();

                                            if (elementsInView.Count > 0)
                                            {
                                                var copiedIds = ElementTransformUtils.CopyElements(
                                                    sourceView,
                                                    elementsInView,
                                                    newLegend,
                                                    Transform.Identity,
                                                    new CopyPasteOptions());
                                            }
                                        }
                                    }
                                    else
                                    {
                                        errors.Add($"No existing legend found in target document to duplicate for '{sourceView.Name}'. Create a legend manually first.");
                                        continue;
                                    }
                                }
                                else if (sourceView is ViewSchedule)
                                {
                                    // For schedules, duplicate the definition
                                    var sourceSchedule = sourceView as ViewSchedule;

                                    // Get the category for this schedule
                                    var scheduleDef = sourceSchedule.Definition;
                                    var categoryId = scheduleDef.CategoryId;

                                    if (categoryId != ElementId.InvalidElementId)
                                    {
                                        var newSchedule = ViewSchedule.CreateSchedule(doc, categoryId);

                                        try
                                        {
                                            newSchedule.Name = sourceView.Name;
                                        }
                                        catch
                                        {
                                            newSchedule.Name = sourceView.Name + "_imported";
                                        }

                                        newViewId = newSchedule.Id;
                                        // Note: Full schedule definition copying would require more complex logic
                                    }
                                }
                                else
                                {
                                    // For other view types (FloorPlan, Section, etc.),
                                    // these are model-based and may not transfer cleanly
                                    // Add to errors list
                                    errors.Add($"View type {viewTypeName} ({sourceView.Name}) requires model geometry and cannot be directly copied");
                                    continue;
                                }

                                if (newViewId != ElementId.InvalidElementId)
                                {
                                    copiedViews.Add(new
                                    {
                                        originalName = sourceView.Name,
                                        newViewId = (int)newViewId.Value,
                                        viewType = viewTypeName,
                                        status = "copied"
                                    });
                                }
                            }
                            catch (Exception viewEx)
                            {
                                errors.Add($"Failed to copy view '{sourceView.Name}': {viewEx.Message}");
                            }
                        }

                        trans.Commit();
                    }
                }
                finally
                {
                    // Close source document without saving
                    if (sourceDoc != null)
                    {
                        sourceDoc.Close(false);
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    copiedCount = copiedViews.Count,
                    copiedViews = copiedViews,
                    errorCount = errors.Count,
                    errors = errors,
                    message = $"Copied {copiedViews.Count} views from {System.IO.Path.GetFileName(filePath)}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Color Fill Scheme Methods

        /// <summary>
        /// Get the color fill scheme for a view and category
        /// Parameters: viewId, categoryName (optional, defaults to "Rooms")
        /// </summary>
        public static string GetColorFillScheme(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>() ?? 0;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                if (viewId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Get the category ID
                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Category '{categoryName}' not found" });
                }

                // Get the color fill scheme
                var schemeId = view.GetColorFillSchemeId(categoryId);
                string schemeName = null;
                if (schemeId != ElementId.InvalidElementId)
                {
                    var scheme = doc.GetElement(schemeId);
                    schemeName = scheme?.Name;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId,
                    viewName = view.Name,
                    categoryName = categoryName,
                    categoryId = (int)categoryId.Value,
                    schemeId = schemeId != ElementId.InvalidElementId ? (int?)schemeId.Value : null,
                    schemeName = schemeName,
                    hasColorScheme = schemeId != ElementId.InvalidElementId
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set the color fill scheme for a view and category
        /// Parameters: viewId, schemeId, categoryName (optional, defaults to "Rooms")
        /// </summary>
        public static string SetColorFillScheme(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>() ?? 0;
                var schemeId = parameters["schemeId"]?.Value<int>() ?? 0;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                if (viewId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Get the category ID
                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Category '{categoryName}' not found" });
                }

                using (var trans = new Transaction(doc, "Set Color Fill Scheme"))
                {
                    trans.Start();

                    view.SetColorFillSchemeId(categoryId, new ElementId(schemeId));

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId,
                    viewName = view.Name,
                    schemeId = schemeId,
                    categoryName = categoryName
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy color fill scheme from one view to another
        /// Parameters: sourceViewId, targetViewId (or targetViewIds array), categoryName (optional)
        /// </summary>
        public static string CopyColorFillScheme(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceViewId = parameters["sourceViewId"]?.Value<int>() ?? 0;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                if (sourceViewId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sourceViewId is required" });
                }

                // Get target view IDs - support both single and array
                var targetViewIds = new List<int>();
                if (parameters["targetViewIds"] != null)
                {
                    targetViewIds = parameters["targetViewIds"].ToObject<List<int>>();
                }
                else if (parameters["targetViewId"] != null)
                {
                    targetViewIds.Add(parameters["targetViewId"].Value<int>());
                }

                if (targetViewIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "targetViewId or targetViewIds is required" });
                }

                var sourceView = doc.GetElement(new ElementId(sourceViewId)) as View;
                if (sourceView == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Source view not found" });
                }

                // Get the category ID
                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Category '{categoryName}' not found" });
                }

                // Get the source color fill scheme
                var schemeId = sourceView.GetColorFillSchemeId(categoryId);
                if (schemeId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source view '{sourceView.Name}' has no color fill scheme for category '{categoryName}'"
                    });
                }

                var results = new List<object>();
                int successCount = 0;
                int errorCount = 0;

                using (var trans = new Transaction(doc, "Copy Color Fill Scheme"))
                {
                    trans.Start();

                    foreach (var targetId in targetViewIds)
                    {
                        try
                        {
                            var targetView = doc.GetElement(new ElementId(targetId)) as View;
                            if (targetView == null)
                            {
                                results.Add(new { viewId = targetId, success = false, error = "View not found" });
                                errorCount++;
                                continue;
                            }

                            targetView.SetColorFillSchemeId(categoryId, schemeId);
                            results.Add(new { viewId = targetId, viewName = targetView.Name, success = true });
                            successCount++;
                        }
                        catch (Exception viewEx)
                        {
                            results.Add(new { viewId = targetId, success = false, error = viewEx.Message });
                            errorCount++;
                        }
                    }

                    trans.Commit();
                }

                var scheme = doc.GetElement(schemeId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceViewName = sourceView.Name,
                    schemeName = scheme?.Name,
                    schemeId = (int)schemeId.Value,
                    categoryName = categoryName,
                    totalTargets = targetViewIds.Count,
                    successCount = successCount,
                    errorCount = errorCount,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all color fill schemes in the document
        /// Parameters: categoryName (optional, defaults to "Rooms")
        /// </summary>
        public static string GetAllColorFillSchemes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Category '{categoryName}' not found" });
                }

                var schemes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ColorFillScheme))
                    .Cast<ColorFillScheme>()
                    .Where(s => s.CategoryId == categoryId)
                    .Select(s => new
                    {
                        schemeId = (int)s.Id.Value,
                        name = s.Name,
                        title = s.Title,
                        categoryName = categoryName
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    categoryName = categoryName,
                    count = schemes.Count,
                    schemes = schemes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper method to get category ID by name
        private static ElementId GetCategoryIdByName(string categoryName)
        {
            switch (categoryName.ToLower())
            {
                case "rooms": return new ElementId(BuiltInCategory.OST_Rooms);
                case "areas": return new ElementId(BuiltInCategory.OST_Areas);
                case "spaces": return new ElementId(BuiltInCategory.OST_MEPSpaces);
                case "ducts": return new ElementId(BuiltInCategory.OST_DuctCurves);
                case "pipes": return new ElementId(BuiltInCategory.OST_PipeCurves);
                default: return ElementId.InvalidElementId;
            }
        }

        #endregion
    }
}
