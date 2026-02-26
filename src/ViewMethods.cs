using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

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
        [MCPMethod("createFloorPlan", Category = "View", Description = "Create a floor plan view for a given level")]
        public static string CreateFloorPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createFloorPlan");
                v.Require("levelId").IsType<int>();
                v.ThrowIfInvalid();

                var levelIdInt = v.GetRequired<int>("levelId");
                var viewName = v.GetOptional<string>("viewName");
                var level = ElementLookup.GetLevel(doc, levelIdInt);
                var levelId = level.Id;

                // Get view family type
                var viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan)
                    ?.Id;

                if (viewFamilyTypeId == null)
                {
                    return ResponseBuilder.Error("Floor plan view type not found", "TYPE_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .WithView((int)view.Id.Value, view.Name, "FloorPlan")
                        .With("level", level.Name)
                        .Build();
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
        [MCPMethod("createCeilingPlan", Category = "View", Description = "Create a ceiling plan view for a given level")]
        public static string CreateCeilingPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createCeilingPlan");
                v.Require("levelId").IsType<int>();
                v.ThrowIfInvalid();

                var levelIdInt = v.GetRequired<int>("levelId");
                var viewName = v.GetOptional<string>("viewName");
                var level = ElementLookup.GetLevel(doc, levelIdInt);
                var levelId = level.Id;

                // Get ceiling plan view family type
                var viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.CeilingPlan)
                    ?.Id;

                if (viewFamilyTypeId == null)
                {
                    return ResponseBuilder.Error("Ceiling plan view type not found", "TYPE_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .WithView((int)view.Id.Value, view.Name, "CeilingPlan")
                        .With("level", level.Name)
                        .Build();
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
        [MCPMethod("createSection", Category = "View", Description = "Create a section view from a crop box region")]
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
                    return ResponseBuilder.Error("Section view type not found", "TYPE_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .WithView((int)section.Id.Value, section.Name, "Section")
                        .Build();
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
        [MCPMethod("createElevation", Category = "View", Description = "Create an elevation view")]
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
                    return ResponseBuilder.Error("Elevation view type not found", "TYPE_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .WithView((int)elevationView.Id.Value, elevationView.Name, "Elevation")
                        .With("markerId", (int)marker.Id.Value)
                        .Build();
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
        [MCPMethod("createDraftingView", Category = "View", Description = "Create a drafting view for 2D detailing")]
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
                    return ResponseBuilder.Error("Drafting view type not found", "TYPE_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .WithView((int)draftingView.Id.Value, draftingView.Name, "Drafting")
                        .With("scale", draftingView.Scale)
                        .Build();
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
        [MCPMethod("duplicateView", Category = "View", Description = "Duplicate an existing view")]
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
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .With("originalViewId", (int)viewId.Value)
                        .With("newViewId", (int)newViewId.Value)
                        .With("newViewName", newView.Name)
                        .With("duplicateOption", option.ToString())
                        .Build();
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
        [MCPMethod("applyViewTemplate", Category = "View", Description = "Apply a view template to a view")]
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
                    return ResponseBuilder.Error("View or template not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Apply View Template"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.ViewTemplateId = templateId;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("templateId", (int)templateId.Value)
                        .With("templateName", template.Name)
                        .Build();
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
        [MCPMethod("getAllViews", Category = "View", Description = "Get all views in the project")]
        public static string GetAllViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
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

                return ResponseBuilder.Success()
                    .WithCount(views.Count, "viewCount")
                    .With("views", views)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all view templates
        /// </summary>
        [MCPMethod("getViewTemplates", Category = "View", Description = "Get all view templates in the document")]
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

                return ResponseBuilder.Success()
                    .WithCount(templates.Count, "templateCount")
                    .With("templates", templates)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set view crop box
        /// </summary>
        [MCPMethod("setViewCropBox", Category = "View", Description = "Set the crop box region for a view")]
        public static string SetViewCropBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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
                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("cropBoxActive", view.CropBoxActive)
                        .With("hasViewTemplate", hasTemplate)
                        .With("cropMethod", cropMethod)
                        .With("cropError", cropError)
                        .With("requested", new { width = requestedWidth, height = requestedHeight })
                        .With("actual", new { width = actualWidth, height = actualHeight })
                        .With("actualCropBox", new
                        {
                            min = new { x = finalCropBox.Min.X, y = finalCropBox.Min.Y, z = finalCropBox.Min.Z },
                            max = new { x = finalCropBox.Max.X, y = finalCropBox.Max.Y, z = finalCropBox.Max.Z }
                        })
                        .Build();
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
        [MCPMethod("getViewCropBox", Category = "View", Description = "Get the crop box coordinates for a view")]
        public static string GetViewCropBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                var cropBox = view.CropBox;

                return ResponseBuilder.Success()
                    .With("viewId", (int)viewId.Value)
                    .With("viewName", view.Name)
                    .With("viewType", view.ViewType.ToString())
                    .With("cropBoxActive", view.CropBoxActive)
                    .With("cropBoxVisible", view.CropBoxVisible)
                    .With("cropBox", new
                    {
                        min = new { x = cropBox.Min.X, y = cropBox.Min.Y, z = cropBox.Min.Z },
                        max = new { x = cropBox.Max.X, y = cropBox.Max.Y, z = cropBox.Max.Z }
                    })
                    .With("transform", new
                    {
                        origin = new { x = cropBox.Transform.Origin.X, y = cropBox.Transform.Origin.Y, z = cropBox.Transform.Origin.Z },
                        basisX = new { x = cropBox.Transform.BasisX.X, y = cropBox.Transform.BasisX.Y, z = cropBox.Transform.BasisX.Z },
                        basisY = new { x = cropBox.Transform.BasisY.X, y = cropBox.Transform.BasisY.Y, z = cropBox.Transform.BasisY.Z },
                        basisZ = new { x = cropBox.Transform.BasisZ.X, y = cropBox.Transform.BasisZ.Y, z = cropBox.Transform.BasisZ.Z }
                    })
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rename a view
        /// </summary>
        [MCPMethod("renameView", Category = "View", Description = "Rename an existing view")]
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
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Rename View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.Name = newName;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("newName", view.Name)
                        .Build();
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
        [MCPMethod("deleteView", Category = "View", Description = "Delete a view from the document")]
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

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .WithMessage("View deleted successfully")
                        .Build();
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
        [MCPMethod("setViewScale", Category = "View", Description = "Set the scale of a view")]
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
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Set View Scale"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.Scale = scale;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("scale", view.Scale)
                        .Build();
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
        [MCPMethod("getActiveView", Category = "View", Description = "Get the currently active view")]
        public static string GetActiveView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;
                var activeView = uiDoc.ActiveView;

                if (activeView == null)
                {
                    return ResponseBuilder.Error("No active view found", "NO_ACTIVE_VIEW").Build();
                }

                // Get level if this is a plan view
                string levelName = null;
                if (activeView.GenLevel != null)
                {
                    levelName = activeView.GenLevel.Name;
                }

                return ResponseBuilder.Success()
                    .WithView((int)activeView.Id.Value, activeView.Name, activeView.ViewType.ToString())
                    .With("level", levelName)
                    .With("scale", activeView.Scale)
                    .With("isTemplate", activeView.IsTemplate)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set the active view in Revit
        /// </summary>
        [MCPMethod("setActiveView", Category = "View", Description = "Set the active view by ID or name")]
        public static string SetActiveView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                var v = new ParameterValidator(parameters, "setActiveView");
                v.Require("viewId").IsType<int>();
                v.ThrowIfInvalid();

                var viewIdInt = v.GetRequired<int>("viewId");
                var view = ElementLookup.GetView(doc, viewIdInt);

                if (view.IsTemplate)
                {
                    return ResponseBuilder.Error("Cannot activate a view template", "VIEW_IS_TEMPLATE").Build();
                }

                // Set the active view
                uiDoc.ActiveView = view;

                // Get level if applicable
                string levelName = null;
                if (view.GenLevel != null)
                {
                    levelName = view.GenLevel.Name;
                }

                return ResponseBuilder.Success()
                    .WithView((int)view.Id.Value, view.Name, view.ViewType.ToString())
                    .With("level", levelName)
                    .WithMessage("View activated successfully")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Zoom to fit all elements in the active view
        /// </summary>
        [MCPMethod("zoomToFit", Category = "View", Description = "Zoom to fit all elements in the active view")]
        public static string ZoomToFit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
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
                        return ResponseBuilder.Error("View not found with specified ID", "ELEMENT_NOT_FOUND").Build();
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
                    return ResponseBuilder.Error("No active view available", "NO_ACTIVE_VIEW").Build();
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
                    return ResponseBuilder.Error("Could not get UI view for zoom operation. No open view windows found.", "UI_VIEW_NOT_FOUND").Build();
                }

                // Zoom to fit
                uiView.ZoomToFit();

                return ResponseBuilder.Success()
                    .WithView((int)targetView.Id.Value, targetView.Name)
                    .WithMessage("Zoomed to fit all elements in view")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Zoom to a specific element in the active view
        /// </summary>
        [MCPMethod("zoomToElement", Category = "View", Description = "Zoom to a specific element in the active view")]
        public static string ZoomToElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiDoc.Document;
                var activeView = uiDoc.ActiveView;

                if (activeView == null)
                {
                    return ResponseBuilder.Error("No active view", "NO_ACTIVE_VIEW").Build();
                }

                var v = new ParameterValidator(parameters, "zoomToElement");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementIdInt = v.GetRequired<int>("elementId");
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return ResponseBuilder.Error("Element not found with specified ID", "ELEMENT_NOT_FOUND").Build();
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
                    return ResponseBuilder.Error("Could not get bounding box for element", "NO_BOUNDING_BOX").Build();
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
                    return ResponseBuilder.Error("Could not get UI view for zoom operation. No open view windows found.", "UI_VIEW_NOT_FOUND").Build();
                }

                // Zoom to the bounding box with some margin
                var margin = 5.0; // 5 feet margin
                var zoomCorners = new List<XYZ>
                {
                    new XYZ(bbox.Min.X - margin, bbox.Min.Y - margin, bbox.Min.Z),
                    new XYZ(bbox.Max.X + margin, bbox.Max.Y + margin, bbox.Max.Z)
                };

                uiView.ZoomAndCenterRectangle(zoomCorners[0], zoomCorners[1]);

                return ResponseBuilder.Success()
                    .WithElementId((int)elementId.Value)
                    .With("viewId", (int)uiDoc.ActiveView.Id.Value)
                    .WithMessage("Zoomed to element")
                    .Build();
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
        [MCPMethod("zoomToRegion", Category = "View", Description = "Zoom to a region defined by min/max coordinates")]
        public static string ZoomToRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
                }

                var activeView = uiDoc.ActiveView;
                if (activeView == null)
                {
                    return ResponseBuilder.Error("No active view", "NO_ACTIVE_VIEW").Build();
                }

                var v = new ParameterValidator(parameters, "zoomToRegion");
                v.Require("minPoint");
                v.Require("maxPoint");
                v.ThrowIfInvalid();

                var minArray = parameters["minPoint"].ToObject<double[]>();
                var maxArray = parameters["maxPoint"].ToObject<double[]>();

                if (minArray.Length < 2 || maxArray.Length < 2)
                {
                    return ResponseBuilder.Error("Points must have at least x and y coordinates", "VALIDATION_ERROR").Build();
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
                    return ResponseBuilder.Error("Could not get UI view for zoom operation", "UI_VIEW_NOT_FOUND").Build();
                }

                // Zoom to region
                uiView.ZoomAndCenterRectangle(minPoint, maxPoint);

                return ResponseBuilder.Success()
                    .WithView((int)activeView.Id.Value, activeView.Name)
                    .With("minPoint", new[] { minPoint.X, minPoint.Y, minPoint.Z })
                    .With("maxPoint", new[] { maxPoint.X, maxPoint.Y, maxPoint.Z })
                    .WithMessage("Zoomed to specified region")
                    .Build();
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
        [MCPMethod("zoomToGridIntersection", Category = "View", Description = "Zoom to area around a grid intersection")]
        public static string ZoomToGridIntersection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiDoc.Document;
                var activeView = uiDoc.ActiveView;

                if (activeView == null)
                {
                    return ResponseBuilder.Error("No active view", "NO_ACTIVE_VIEW").Build();
                }

                // Get grid names
                var v = new ParameterValidator(parameters, "zoomToGridIntersection");
                v.Require("gridHorizontal").NotEmpty();
                v.Require("gridVertical").NotEmpty();
                v.ThrowIfInvalid();

                var gridH = v.GetRequired<string>("gridHorizontal");
                var gridV = v.GetRequired<string>("gridVertical");
                var margin = v.GetOptional<double>("margin", 30.0);

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
                    return ResponseBuilder.Error($"Grid '{gridH}' not found", "ELEMENT_NOT_FOUND")
                        .With("availableGrids", grids.Select(g => g.Name).Take(20).ToList())
                        .Build();
                }

                if (grid2 == null)
                {
                    return ResponseBuilder.Error($"Grid '{gridV}' not found", "ELEMENT_NOT_FOUND")
                        .With("availableGrids", grids.Select(g => g.Name).Take(20).ToList())
                        .Build();
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
                    return ResponseBuilder.Error("Could not get UI view for zoom operation", "UI_VIEW_NOT_FOUND").Build();
                }

                // Zoom to intersection area
                uiView.ZoomAndCenterRectangle(minPoint, maxPoint);

                return ResponseBuilder.Success()
                    .WithView((int)activeView.Id.Value, activeView.Name)
                    .With("gridHorizontal", gridH)
                    .With("gridVertical", gridV)
                    .With("intersection", new[] { intersection.X, intersection.Y, intersection.Z })
                    .With("margin", margin)
                    .WithMessage($"Zoomed to grid intersection {gridH}/{gridV}")
                    .Build();
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
        [MCPMethod("showElement", Category = "View", Description = "Find and display an element by opening its view and zooming to it")]
        public static string ShowElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                if (uiDoc == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiDoc.Document;

                var v = new ParameterValidator(parameters, "showElement");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementIdInt = v.GetRequired<int>("elementId");
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return ResponseBuilder.Error("Element not found with specified ID", "ELEMENT_NOT_FOUND").Build();
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
                    return ResponseBuilder.Error(
                        $"Could not find a view containing element {elementId.Value}. The element may not be visible in any current views.",
                        "VIEW_NOT_FOUND")
                        .WithElementId((int)elementId.Value)
                        .With("elementType", element.GetType().Name)
                        .With("elementName", element.Name)
                        .Build();
                }

                // Can't activate templates
                if (targetView.IsTemplate)
                {
                    return ResponseBuilder.Error("Element is in a view template which cannot be opened", "VIEW_IS_TEMPLATE").Build();
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

                return ResponseBuilder.Success()
                    .WithElementId((int)elementId.Value)
                    .With("elementType", element.GetType().Name)
                    .With("elementName", element.Name ?? "")
                    .WithView((int)targetView.Id.Value, targetView.Name, targetView.ViewType.ToString())
                    .With("location", locationDescription)
                    .With("zoomed", zoomed)
                    .With("elementCenter", elementLocation)
                    .WithMessage($"Opened {locationDescription} and zoomed to element")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a legend view
        /// </summary>
        [MCPMethod("createLegendView", Category = "View", Description = "Create a legend view")]
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
                    return ResponseBuilder.Error("Drafting view family type not found in document", "TYPE_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .WithView((int)legendView.Id.Value, legendView.Name, legendView.ViewType.ToString())
                        .With("scale", legendView.Scale)
                        .WithMessage("Legend view created successfully")
                        .Build();
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
        [MCPMethod("getLegendViews", Category = "View", Description = "Get all legend views in the document")]
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

                return ResponseBuilder.Success()
                    .WithCount(legendViews.Count, "legendCount")
                    .With("legends", legendViews)
                    .Build();
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
        [MCPMethod("getElementsInView", Category = "View", Description = "Get all elements visible in a specific view")]
        public static string GetElementsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Step 1: Get document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document", "NO_ACTIVE_DOCUMENT").Build();
                }
                var doc = uiApp.ActiveUIDocument.Document;

                // Step 2: Get viewId
                var pv = new ParameterValidator(parameters, "getElementsInView");
                pv.Require("viewId").IsType<int>();
                pv.ThrowIfInvalid();

                var viewIdInt = pv.GetRequired<int>("viewId");
                var view = ElementLookup.GetView(doc, viewIdInt);
                var viewId = view.Id;

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
                            return ResponseBuilder.Error($"Category '{categoryFilter}' not found", "ELEMENT_NOT_FOUND").Build();
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

                return ResponseBuilder.Success()
                    .With("viewId", (int)viewId.Value)
                    .With("viewName", view.Name)
                    .With("totalElements", elementList.Count)
                    .With("limitApplied", elementList.Count >= limit)
                    .With("categorySummary", categorySummary)
                    .With("elements", elementList)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set far clip offset for elevation/section views
        /// </summary>
        [MCPMethod("setViewFarClip", Category = "View", Description = "Set the far clip offset for elevation or section views")]
        public static string SetViewFarClip(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                var viewSection = view as ViewSection;
                if (viewSection == null)
                {
                    return ResponseBuilder.Error("View is not a section/elevation view", "VALIDATION_ERROR").Build();
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

                return ResponseBuilder.Success()
                    .With("viewId", (int)viewId.Value)
                    .With("viewName", view.Name)
                    .With("requestedFarClip", farClipOffset)
                    .With("originalDepth", originalDepth)
                    .With("newDepth", newDepth)
                    .With("depthChanged", depthChanged)
                    .With("methodUsed", methodUsed)
                    .With("notes", errorDetails)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Hide or show a category in a view
        /// </summary>
        [MCPMethod("setCategoryHidden", Category = "View", Description = "Hide or show a category in a view")]
        public static string SetCategoryHidden(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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
                    return ResponseBuilder.Error($"Category '{categoryName}' not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Set Category Hidden"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.SetCategoryHidden(categoryId, hidden);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("viewName", view.Name)
                        .With("categoryName", categoryName)
                        .With("hidden", hidden)
                        .Build();
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
        [MCPMethod("hideCategoriesInView", Category = "View", Description = "Hide multiple categories in a view at once")]
        public static string HideCategoriesInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("viewName", view.Name)
                        .With("hidden", hidden)
                        .With("results", results)
                        .Build();
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
        [MCPMethod("hideElementsInView", Category = "View", Description = "Hide specific elements in a view")]
        public static string HideElementsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                if (parameters["elementIds"] == null)
                {
                    return ResponseBuilder.Error("elementIds array is required", "MISSING_PARAMETER").Build();
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("viewName", view.Name)
                        .With("hiddenCount", elementIdsList.Count)
                        .With("elementIds", elementIdsList.Select(id => (int)id.Value).ToList())
                        .Build();
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
        [MCPMethod("unhideElementsInView", Category = "View", Description = "Unhide specific elements in a view")]
        public static string UnhideElementsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                if (parameters["elementIds"] == null)
                {
                    return ResponseBuilder.Error("elementIds array is required", "MISSING_PARAMETER").Build();
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .With("viewId", (int)viewId.Value)
                        .With("viewName", view.Name)
                        .With("unhiddenCount", elementIdsList.Count)
                        .With("elementIds", elementIdsList.Select(id => (int)id.Value).ToList())
                        .Build();
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
        [MCPMethod("getRoomTagsInView", Category = "View", Description = "Get room tags in a view with their associated room IDs")]
        public static string GetRoomTagsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
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

                return ResponseBuilder.Success()
                    .With("viewId", (int)viewId.Value)
                    .With("viewName", view.Name)
                    .With("count", tagData.Count)
                    .With("roomTags", tagData)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Force regeneration of view/document to update displayed values
        /// </summary>
        [MCPMethod("regenerateView", Category = "View", Description = "Force regeneration of a view to update displayed values")]
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

                return ResponseBuilder.Success()
                    .WithMessage("Document regenerated and view refreshed")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Verify room parameter values match expected values (for validation after batch operations)
        /// </summary>
        [MCPMethod("verifyRoomValues", Category = "View", Description = "Verify room parameter values match expected values")]
        public static string VerifyRoomValues(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var expectedValues = parameters?["expectedValues"]?.ToObject<List<JObject>>();
                var parameterName = parameters?["parameterName"]?.ToString() ?? "Comments";

                if (expectedValues == null || expectedValues.Count == 0)
                {
                    return ResponseBuilder.Error("expectedValues array is required with objects containing roomId and expectedValue", "MISSING_PARAMETER").Build();
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

                return ResponseBuilder.Success()
                    .With("summary", new
                    {
                        total = expectedValues.Count,
                        matches = matchCount,
                        mismatches = mismatchCount,
                        allMatch = mismatchCount == 0
                    })
                    .With("results", results)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Compare expected vs actual element parameter values across multiple elements
        /// </summary>
        [MCPMethod("compareExpectedActual", Category = "View", Description = "Compare expected vs actual element parameter values")]
        public static string CompareExpectedActual(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var comparisons = parameters?["comparisons"]?.ToObject<List<JObject>>();

                if (comparisons == null || comparisons.Count == 0)
                {
                    return ResponseBuilder.Error("comparisons array is required with objects containing elementId, parameterName, expectedValue", "MISSING_PARAMETER").Build();
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

                return ResponseBuilder.Success()
                    .With("summary", new
                    {
                        total = comparisons.Count,
                        passed = passCount,
                        failed = failCount,
                        allPassed = failCount == 0
                    })
                    .With("results", results)
                    .Build();
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
        [MCPMethod("createViewTemplate", Category = "View", Description = "Create a view template from an existing view")]
        public static string CreateViewTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceViewId = parameters["sourceViewId"]?.Value<int>() ?? 0;
                var templateName = parameters["templateName"]?.ToString();

                if (sourceViewId == 0)
                {
                    return ResponseBuilder.Error("sourceViewId is required", "MISSING_PARAMETER").Build();
                }

                if (string.IsNullOrEmpty(templateName))
                {
                    return ResponseBuilder.Error("templateName is required", "MISSING_PARAMETER").Build();
                }

                var sourceView = doc.GetElement(new ElementId(sourceViewId)) as View;
                if (sourceView == null)
                {
                    return ResponseBuilder.Error("Source view not found", "ELEMENT_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .With("templateId", template?.Id.Value ?? 0)
                        .With("templateName", templateName)
                        .With("sourceViewName", sourceView.Name)
                        .With("viewType", sourceView.ViewType.ToString())
                        .Build();
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
        [MCPMethod("duplicateViewTemplate", Category = "View", Description = "Duplicate an existing view template")]
        public static string DuplicateViewTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var templateId = parameters["templateId"]?.Value<int>() ?? 0;
                var newName = parameters["newName"]?.ToString();

                if (templateId == 0)
                {
                    return ResponseBuilder.Error("templateId is required", "MISSING_PARAMETER").Build();
                }

                if (string.IsNullOrEmpty(newName))
                {
                    return ResponseBuilder.Error("newName is required", "MISSING_PARAMETER").Build();
                }

                var template = doc.GetElement(new ElementId(templateId)) as View;
                if (template == null || !template.IsTemplate)
                {
                    return ResponseBuilder.Error("View template not found", "ELEMENT_NOT_FOUND").Build();
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

                    return ResponseBuilder.Success()
                        .With("newTemplateId", newTemplateId.Value)
                        .With("newTemplateName", newName)
                        .With("sourceTemplateName", template.Name)
                        .Build();
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
        [MCPMethod("getScopeBoxes", Category = "View", Description = "Get all scope boxes in the document")]
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

                return ResponseBuilder.Success()
                    .With("count", scopeBoxes.Count)
                    .With("scopeBoxes", scopeBoxes)
                    .Build();
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
        [MCPMethod("createScopeBox", Category = "View", Description = "Create a new scope box with defined extents")]
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
                    return ResponseBuilder.Error("name is required", "MISSING_PARAMETER").Build();
                }

                if (minPointObj == null || maxPointObj == null)
                {
                    return ResponseBuilder.Error("minPoint and maxPoint are required", "VALIDATION_ERROR").Build();
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
                        return ResponseBuilder.Error("No 3D view available to create scope box", "VALIDATION_ERROR").Build();
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

                    return ResponseBuilder.Success()
                        .With("scopeBoxId", scopeBox.Id.Value)
                        .With("name", name)
                        .With("minPoint", new { x = minPoint.X, y = minPoint.Y, z = minPoint.Z })
                        .With("maxPoint", new { x = maxPoint.X, y = maxPoint.Y, z = maxPoint.Z })
                        .Build();
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
        [MCPMethod("applyScopeBoxToView", Category = "View", Description = "Apply a scope box to one or more views")]
        public static string ApplyScopeBoxToView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var scopeBoxId = parameters["scopeBoxId"]?.Value<int>() ?? 0;
                var viewIdArray = parameters["viewIds"]?.ToObject<int[]>();

                if (scopeBoxId == 0)
                {
                    return ResponseBuilder.Error("scopeBoxId is required", "MISSING_PARAMETER").Build();
                }

                if (viewIdArray == null || viewIdArray.Length == 0)
                {
                    return ResponseBuilder.Error("viewIds array is required", "MISSING_PARAMETER").Build();
                }

                var scopeBox = doc.GetElement(new ElementId(scopeBoxId));
                if (scopeBox == null)
                {
                    return ResponseBuilder.Error("Scope box not found", "ELEMENT_NOT_FOUND").Build();
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

                return ResponseBuilder.Success()
                    .With("scopeBoxName", scopeBox.Name)
                    .With("updatedCount", updatedViews.Count)
                    .With("failedCount", failedViews.Count)
                    .With("updatedViews", updatedViews)
                    .With("failedViews", failedViews)
                    .Build();
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
        [MCPMethod("removeScopeBoxFromView", Category = "View", Description = "Remove scope box from one or more views")]
        public static string RemoveScopeBoxFromView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdArray = parameters["viewIds"]?.ToObject<int[]>();

                if (viewIdArray == null || viewIdArray.Length == 0)
                {
                    return ResponseBuilder.Error("viewIds array is required", "MISSING_PARAMETER").Build();
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

                return ResponseBuilder.Success()
                    .With("updatedCount", updatedViews.Count)
                    .With("failedCount", failedViews.Count)
                    .With("updatedViews", updatedViews)
                    .With("failedViews", failedViews)
                    .Build();
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
        [MCPMethod("getLegends", Category = "View", Description = "Get all legend views in the document")]
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

                return ResponseBuilder.Success()
                    .With("count", legends.Count)
                    .With("legends", legends)
                    .Build();
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
        [MCPMethod("createLegend", Category = "View", Description = "Create a new legend view")]
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
                    return ResponseBuilder.Error("name is required", "MISSING_PARAMETER").Build();
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
                    return ResponseBuilder.Error("No source legend found to duplicate. Create a legend manually in Revit first.", "VALIDATION_ERROR").Build();
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

                    return ResponseBuilder.Success()
                        .With("legendId", newLegendId.Value)
                        .With("name", newLegend?.Name)
                        .With("scale", newLegend?.Scale ?? 0)
                        .With("duplicatedFrom", sourceLegend.Name)
                        .Build();
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
        [MCPMethod("getLegendComponents", Category = "View", Description = "Get legend components available for placement in legends")]
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

                return ResponseBuilder.Success()
                    .With("totalSymbols", symbolList.Count)
                    .With("categories", grouped)
                    .Build();
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
        [MCPMethod("createAssembly", Category = "View", Description = "Create an assembly from selected elements")]
        public static string CreateAssembly(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null)
                    return ResponseBuilder.Error("elementIds is required", "MISSING_PARAMETER").Build();

                var elementIdInts = parameters["elementIds"].ToObject<int[]>();
                string name = parameters["name"]?.ToString();

                if (elementIdInts == null || elementIdInts.Length == 0)
                    return ResponseBuilder.Error("At least one element ID is required", "MISSING_PARAMETER").Build();

                // Convert to ElementId collection
                var elementIds = elementIdInts.Select(id => new ElementId(id)).ToList();

                // Verify all elements exist
                foreach (var id in elementIds)
                {
                    if (doc.GetElement(id) == null)
                        return ResponseBuilder.Error($"Element {id.Value} not found", "ELEMENT_NOT_FOUND").Build();
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
                        return ResponseBuilder.Error("Failed to create assembly", "VALIDATION_ERROR").Build();

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

                    return ResponseBuilder.Success()
                        .With("assemblyId", (int)assembly.Id.Value)
                        .With("assemblyTypeId", (int)assembly.GetTypeId().Value)
                        .With("name", assemblyType2?.Name ?? "Assembly")
                        .With("memberCount", elementIds.Count)
                        .Build();
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
        [MCPMethod("createAssemblyViews", Category = "View", Description = "Create views for an assembly instance")]
        public static string CreateAssemblyViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["assemblyId"] == null)
                    return ResponseBuilder.Error("assemblyId is required", "MISSING_PARAMETER").Build();

                int assemblyIdInt = parameters["assemblyId"].ToObject<int>();

                var assembly = doc.GetElement(new ElementId(assemblyIdInt)) as AssemblyInstance;
                if (assembly == null)
                    return ResponseBuilder.Error("Assembly not found", "ELEMENT_NOT_FOUND").Build();

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

                return ResponseBuilder.Success()
                    .WithMessage("Assembly view creation via API limited in Revit 2026. Use Revit UI or create views manually.")
                    .With("assemblyId", assemblyIdInt)
                    .With("assemblyName", assemblyType?.Name ?? "Assembly")
                    .With("existingViewCount", existingViews.Count)
                    .With("existingViews", existingViews)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all assemblies in the document
        /// </summary>
        [MCPMethod("getAssemblies", Category = "View", Description = "Get all assemblies in the document")]
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

                return ResponseBuilder.Success()
                    .With("count", assemblies.Count)
                    .With("assemblies", assemblies)
                    .Build();
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
        [MCPMethod("insertViewsFromFile", Category = "View", Description = "Insert views from another Revit file into the current document")]
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
                    return ResponseBuilder.Error("filePath is required", "MISSING_PARAMETER").Build();
                }

                if (!System.IO.File.Exists(filePath))
                {
                    return ResponseBuilder.Error($"File not found: {filePath}", "ELEMENT_NOT_FOUND").Build();
                }

                // Parse parameters
                var viewTypes = parameters["viewTypes"]?.ToObject<string[]>() ?? new string[0];
                var viewNames = parameters["viewNames"]?.ToObject<string[]>() ?? new string[0];
                var importAll = parameters["importAll"]?.Value<bool>() ?? false;

                // If no filters specified and importAll is false, return error
                if (viewTypes.Length == 0 && viewNames.Length == 0 && !importAll)
                {
                    return ResponseBuilder.Error("Specify viewTypes, viewNames, or set importAll to true", "VALIDATION_ERROR").Build();
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
                        return ResponseBuilder.Error("Failed to open source document", "OPERATION_FAILED").Build();
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
                        return ResponseBuilder.Error("No matching views found in source document", "ELEMENT_NOT_FOUND")
                            .With("availableViews", allViews.Select(v => new { name = v.Name, type = v.ViewType.ToString() }).Take(50).ToList())
                            .Build();
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

                return ResponseBuilder.Success()
                    .With("copiedCount", copiedViews.Count)
                    .With("copiedViews", copiedViews)
                    .With("errorCount", errors.Count)
                    .With("errors", errors)
                    .WithMessage($"Copied {copiedViews.Count} views from {System.IO.Path.GetFileName(filePath)}")
                    .Build();
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
        [MCPMethod("getColorFillScheme", Category = "View", Description = "Get the color fill scheme applied to a view")]
        public static string GetColorFillScheme(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>() ?? 0;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                if (viewId == 0)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get the category ID
                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return ResponseBuilder.Error($"Category '{categoryName}' not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get the color fill scheme
                var schemeId = view.GetColorFillSchemeId(categoryId);
                string schemeName = null;
                if (schemeId != ElementId.InvalidElementId)
                {
                    var scheme = doc.GetElement(schemeId);
                    schemeName = scheme?.Name;
                }

                return ResponseBuilder.Success()
                    .With("viewId", viewId)
                    .With("viewName", view.Name)
                    .With("categoryName", categoryName)
                    .With("categoryId", (int)categoryId.Value)
                    .With("schemeId", schemeId != ElementId.InvalidElementId ? (int?)schemeId.Value : null)
                    .With("schemeName", schemeName)
                    .With("hasColorScheme", schemeId != ElementId.InvalidElementId)
                    .Build();
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
        [MCPMethod("setColorFillScheme", Category = "View", Description = "Set the color fill scheme for a view")]
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
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get the category ID
                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return ResponseBuilder.Error($"Category '{categoryName}' not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Set Color Fill Scheme"))
                {
                    trans.Start();

                    view.SetColorFillSchemeId(categoryId, new ElementId(schemeId));

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("viewId", viewId)
                    .With("viewName", view.Name)
                    .With("schemeId", schemeId)
                    .With("categoryName", categoryName)
                    .Build();
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
        [MCPMethod("copyColorFillScheme", Category = "View", Description = "Copy a color fill scheme from one view to another")]
        public static string CopyColorFillScheme(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sourceViewId = parameters["sourceViewId"]?.Value<int>() ?? 0;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                if (sourceViewId == 0)
                {
                    return ResponseBuilder.Error("sourceViewId is required", "MISSING_PARAMETER").Build();
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
                    return ResponseBuilder.Error("targetViewId or targetViewIds is required", "MISSING_PARAMETER").Build();
                }

                var sourceView = doc.GetElement(new ElementId(sourceViewId)) as View;
                if (sourceView == null)
                {
                    return ResponseBuilder.Error("Source view not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get the category ID
                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return ResponseBuilder.Error($"Category '{categoryName}' not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get the source color fill scheme
                var schemeId = sourceView.GetColorFillSchemeId(categoryId);
                if (schemeId == ElementId.InvalidElementId)
                {
                    return ResponseBuilder.Error($"Source view '{sourceView.Name}' has no color fill scheme for category '{categoryName}'", "VALIDATION_ERROR").Build();
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

                return ResponseBuilder.Success()
                    .With("sourceViewName", sourceView.Name)
                    .With("schemeName", scheme?.Name)
                    .With("schemeId", (int)schemeId.Value)
                    .With("categoryName", categoryName)
                    .With("totalTargets", targetViewIds.Count)
                    .With("successCount", successCount)
                    .With("errorCount", errorCount)
                    .With("results", results)
                    .Build();
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
        [MCPMethod("getAllColorFillSchemes", Category = "View", Description = "Get all color fill schemes in the document")]
        public static string GetAllColorFillSchemes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryName = parameters["categoryName"]?.ToString() ?? "Rooms";

                var categoryId = GetCategoryIdByName(categoryName);
                if (categoryId == ElementId.InvalidElementId)
                {
                    return ResponseBuilder.Error($"Category '{categoryName}' not found", "ELEMENT_NOT_FOUND").Build();
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

                return ResponseBuilder.Success()
                    .With("categoryName", categoryName)
                    .With("count", schemes.Count)
                    .With("schemes", schemes)
                    .Build();
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
