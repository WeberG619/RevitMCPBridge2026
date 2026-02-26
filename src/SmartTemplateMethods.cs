using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Smart Template Methods - Provides intelligent element information with linked views,
    /// schedules, manufacturer data, and specifications for AI-queryable templates.
    /// </summary>
    public static class SmartTemplateMethods
    {
        // Parameter names for Smart Template system
        private static readonly string[] SmartTemplateParameters = new[]
        {
            "ST_DetailViewNames",
            "ST_ScheduleName",
            "ST_Specification",
            "ST_ManufacturerURL",
            "ST_ManufacturerName",
            "ST_ProductModel",
            "ST_FireRating",
            "ST_ULCode",
            "ST_STCRating",
            "ST_NOANumber",
            "ST_HardwareGroup",
            "ST_GlazingType",
            "ST_FinishOptions",
            "ST_MountingType",
            "ST_ADACompliant",
            "ST_Notes",
            "ST_SubmittalLink",
            "ST_InstallGuideLink"
        };

        #region Core Query Methods

        /// <summary>
        /// Gets comprehensive smart template info for the currently selected element
        /// </summary>
        [MCPMethod("getSelectedElementSmartInfo", Category = "SmartTemplate", Description = "Get comprehensive smart template info for the currently selected element")]
        public static string GetSelectedElementSmartInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uidoc.Document;
                var selection = uidoc.Selection.GetElementIds();

                if (selection.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No element selected. Please select an element first."
                    });
                }

                var elementId = selection.First();
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Selected element not found"
                    });
                }

                return GetSmartInfoForElement(doc, element);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets smart template info for a specific element by ID
        /// </summary>
        [MCPMethod("getSmartElementInfo", Category = "SmartTemplate", Description = "Get smart template info for a specific element by ID")]
        public static string GetSmartElementInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(parameters["elementId"].ToObject<int>());
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementId.Value} not found"
                    });
                }

                return GetSmartInfoForElement(doc, element);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets smart template info for an element type by type ID
        /// </summary>
        [MCPMethod("getSmartTypeInfo", Category = "SmartTemplate", Description = "Get smart template info for an element type by type ID")]
        public static string GetSmartTypeInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["typeId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId is required"
                    });
                }

                var typeId = new ElementId(parameters["typeId"].ToObject<int>());
                var elementType = doc.GetElement(typeId) as ElementType;

                if (elementType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element type with ID {typeId.Value} not found"
                    });
                }

                return GetSmartInfoForType(doc, elementType);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Linked Views

        /// <summary>
        /// Gets all linked drafting views for an element type
        /// </summary>
        [MCPMethod("getLinkedViews", Category = "SmartTemplate", Description = "Get all linked drafting views for an element type")]
        public static string GetLinkedViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["typeId"] == null && parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either typeId or elementId is required"
                    });
                }

                ElementType elementType = null;

                if (parameters["typeId"] != null)
                {
                    var typeId = new ElementId(parameters["typeId"].ToObject<int>());
                    elementType = doc.GetElement(typeId) as ElementType;
                }
                else if (parameters["elementId"] != null)
                {
                    var elementId = new ElementId(parameters["elementId"].ToObject<int>());
                    var element = doc.GetElement(elementId);
                    if (element != null)
                    {
                        var typeIdValue = element.GetTypeId();
                        if (typeIdValue != ElementId.InvalidElementId)
                        {
                            elementType = doc.GetElement(typeIdValue) as ElementType;
                        }
                    }
                }

                if (elementType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element type not found"
                    });
                }

                // Get linked view names from ST_DetailViewNames parameter
                var detailViewNamesParam = elementType.LookupParameter("ST_DetailViewNames");
                if (detailViewNamesParam == null || !detailViewNamesParam.HasValue)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeId = elementType.Id.Value,
                        typeName = elementType.Name,
                        linkedViews = new object[0],
                        message = "No linked views defined for this element type"
                    });
                }

                var viewNames = detailViewNamesParam.AsString()
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => n.Trim())
                    .ToList();

                // Find the actual views
                var linkedViews = new List<object>();
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                foreach (var viewName in viewNames)
                {
                    var matchingView = allViews.FirstOrDefault(v =>
                        v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

                    if (matchingView != null)
                    {
                        linkedViews.Add(new
                        {
                            viewId = matchingView.Id.Value,
                            viewName = matchingView.Name,
                            viewType = matchingView.ViewType.ToString(),
                            found = true
                        });
                    }
                    else
                    {
                        linkedViews.Add(new
                        {
                            viewName = viewName,
                            found = false,
                            message = "View not found in document"
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeId = elementType.Id.Value,
                    typeName = elementType.Name,
                    linkedViewCount = linkedViews.Count(v => ((dynamic)v).found),
                    linkedViews
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Navigates to a linked view for an element type
        /// </summary>
        [MCPMethod("navigateToLinkedView", Category = "SmartTemplate", Description = "Navigate to a linked view for an element type")]
        public static string NavigateToLinkedView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uidoc.Document;

                // Accept either viewId directly or viewName + typeId
                View targetView = null;

                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(parameters["viewId"].ToObject<int>());
                    targetView = doc.GetElement(viewId) as View;
                }
                else if (parameters["viewName"] != null)
                {
                    var viewName = parameters["viewName"].ToString();
                    targetView = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .FirstOrDefault(v => v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase) && !v.IsTemplate);
                }

                if (targetView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Target view not found"
                    });
                }

                // Navigate to the view
                uidoc.ActiveView = targetView;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = targetView.Id.Value,
                    viewName = targetView.Name,
                    viewType = targetView.ViewType.ToString(),
                    message = $"Navigated to view: {targetView.Name}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Bulk Queries

        /// <summary>
        /// Gets all element types with smart template data for a specific category
        /// </summary>
        [MCPMethod("getAllSmartTypes", Category = "SmartTemplate", Description = "Get all element types with smart template data for a specific category")]
        public static string GetAllSmartTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["category"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "category is required"
                    });
                }

                var categoryName = parameters["category"].ToString();
                BuiltInCategory? builtInCategory = GetBuiltInCategory(categoryName);

                if (!builtInCategory.HasValue)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category '{categoryName}' not recognized. Use: Walls, Doors, Windows, Casework, PlumbingFixtures, etc."
                    });
                }

                // Get all types for this category
                var elementTypes = new FilteredElementCollector(doc)
                    .OfCategory(builtInCategory.Value)
                    .WhereElementIsElementType()
                    .Cast<ElementType>()
                    .ToList();

                var smartTypes = new List<object>();

                foreach (var elemType in elementTypes)
                {
                    var smartData = ExtractSmartTemplateData(elemType);

                    // Only include types that have at least one ST_ parameter populated
                    bool hasSmartData = smartData.Values.Any(v => v != null && !string.IsNullOrEmpty(v.ToString()));

                    smartTypes.Add(new
                    {
                        typeId = elemType.Id.Value,
                        typeName = elemType.Name,
                        familyName = elemType.FamilyName,
                        hasSmartTemplateData = hasSmartData,
                        smartTemplateData = smartData
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = categoryName,
                    typeCount = smartTypes.Count,
                    typesWithSmartData = smartTypes.Count(t => ((dynamic)t).hasSmartTemplateData),
                    types = smartTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Searches for element types by smart template criteria
        /// </summary>
        [MCPMethod("searchSmartTypes", Category = "SmartTemplate", Description = "Search for element types by smart template criteria")]
        public static string SearchSmartTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                // Build filter criteria
                var fireRating = parameters["fireRating"]?.ToString();
                var manufacturer = parameters["manufacturer"]?.ToString();
                var hasNOA = parameters["hasNOA"]?.ToObject<bool>();
                var isADACompliant = parameters["isADACompliant"]?.ToObject<bool>();
                var category = parameters["category"]?.ToString();

                // Collect element types
                FilteredElementCollector collector;
                if (!string.IsNullOrEmpty(category))
                {
                    var builtInCat = GetBuiltInCategory(category);
                    if (builtInCat.HasValue)
                    {
                        collector = new FilteredElementCollector(doc)
                            .OfCategory(builtInCat.Value)
                            .WhereElementIsElementType();
                    }
                    else
                    {
                        collector = new FilteredElementCollector(doc)
                            .WhereElementIsElementType();
                    }
                }
                else
                {
                    collector = new FilteredElementCollector(doc)
                        .WhereElementIsElementType();
                }

                var matchingTypes = new List<object>();

                foreach (var elemType in collector.Cast<ElementType>())
                {
                    var smartData = ExtractSmartTemplateData(elemType);
                    bool matches = true;

                    // Apply filters
                    if (!string.IsNullOrEmpty(fireRating))
                    {
                        var typeFireRating = smartData.ContainsKey("fireRating") ? smartData["fireRating"]?.ToString() : null;
                        if (string.IsNullOrEmpty(typeFireRating) || !typeFireRating.Contains(fireRating, StringComparison.OrdinalIgnoreCase))
                        {
                            matches = false;
                        }
                    }

                    if (!string.IsNullOrEmpty(manufacturer) && matches)
                    {
                        var typeMfr = smartData.ContainsKey("manufacturerName") ? smartData["manufacturerName"]?.ToString() : null;
                        if (string.IsNullOrEmpty(typeMfr) || !typeMfr.Contains(manufacturer, StringComparison.OrdinalIgnoreCase))
                        {
                            matches = false;
                        }
                    }

                    if (hasNOA.HasValue && hasNOA.Value && matches)
                    {
                        var typeNOA = smartData.ContainsKey("noaNumber") ? smartData["noaNumber"]?.ToString() : null;
                        if (string.IsNullOrEmpty(typeNOA))
                        {
                            matches = false;
                        }
                    }

                    if (isADACompliant.HasValue && matches)
                    {
                        var typeADA = smartData.ContainsKey("adaCompliant") ? smartData["adaCompliant"] : null;
                        if (typeADA == null || (bool)typeADA != isADACompliant.Value)
                        {
                            matches = false;
                        }
                    }

                    if (matches)
                    {
                        matchingTypes.Add(new
                        {
                            typeId = elemType.Id.Value,
                            typeName = elemType.Name,
                            familyName = elemType.FamilyName,
                            category = elemType.Category?.Name,
                            smartTemplateData = smartData
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    matchCount = matchingTypes.Count,
                    searchCriteria = new { fireRating, manufacturer, hasNOA, isADACompliant, category },
                    matches = matchingTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Update Methods

        /// <summary>
        /// Updates smart template parameters for an element type
        /// </summary>
        [MCPMethod("updateSmartParameters", Category = "SmartTemplate", Description = "Update smart template parameters for an element type")]
        public static string UpdateSmartParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["typeId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId is required"
                    });
                }

                var typeId = new ElementId(parameters["typeId"].ToObject<int>());
                var elementType = doc.GetElement(typeId) as ElementType;

                if (elementType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element type with ID {typeId.Value} not found"
                    });
                }

                var updates = new Dictionary<string, object>();
                var failures = new List<string>();

                using (var trans = new Transaction(doc, "Update Smart Template Parameters"))
                {
                    trans.Start();

                    // Map JSON parameter names to actual ST_ parameter names
                    var parameterMappings = new Dictionary<string, string>
                    {
                        { "detailViewNames", "ST_DetailViewNames" },
                        { "scheduleName", "ST_ScheduleName" },
                        { "specification", "ST_Specification" },
                        { "manufacturerURL", "ST_ManufacturerURL" },
                        { "manufacturerName", "ST_ManufacturerName" },
                        { "productModel", "ST_ProductModel" },
                        { "fireRating", "ST_FireRating" },
                        { "ulCode", "ST_ULCode" },
                        { "stcRating", "ST_STCRating" },
                        { "noaNumber", "ST_NOANumber" },
                        { "hardwareGroup", "ST_HardwareGroup" },
                        { "glazingType", "ST_GlazingType" },
                        { "finishOptions", "ST_FinishOptions" },
                        { "mountingType", "ST_MountingType" },
                        { "adaCompliant", "ST_ADACompliant" },
                        { "notes", "ST_Notes" },
                        { "submittalLink", "ST_SubmittalLink" },
                        { "installGuideLink", "ST_InstallGuideLink" }
                    };

                    foreach (var mapping in parameterMappings)
                    {
                        if (parameters[mapping.Key] != null)
                        {
                            var param = elementType.LookupParameter(mapping.Value);
                            if (param != null && !param.IsReadOnly)
                            {
                                try
                                {
                                    var value = parameters[mapping.Key];

                                    if (param.StorageType == StorageType.String)
                                    {
                                        param.Set(value.ToString());
                                        updates[mapping.Key] = value.ToString();
                                    }
                                    else if (param.StorageType == StorageType.Integer)
                                    {
                                        if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                                        {
                                            param.Set(value.ToObject<bool>() ? 1 : 0);
                                            updates[mapping.Key] = value.ToObject<bool>();
                                        }
                                        else
                                        {
                                            param.Set(value.ToObject<int>());
                                            updates[mapping.Key] = value.ToObject<int>();
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    failures.Add($"{mapping.Key}: {ex.Message}");
                                }
                            }
                            else if (param == null)
                            {
                                failures.Add($"{mapping.Key}: Parameter {mapping.Value} not found on element type");
                            }
                            else
                            {
                                failures.Add($"{mapping.Key}: Parameter is read-only");
                            }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failures.Count == 0,
                    typeId = elementType.Id.Value,
                    typeName = elementType.Name,
                    updatedParameters = updates,
                    failedParameters = failures,
                    message = failures.Count == 0
                        ? $"Successfully updated {updates.Count} parameters"
                        : $"Updated {updates.Count} parameters with {failures.Count} failures"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Bulk updates smart template parameters for multiple element types
        /// </summary>
        [MCPMethod("bulkUpdateSmartParameters", Category = "SmartTemplate", Description = "Bulk update smart template parameters for multiple element types")]
        public static string BulkUpdateSmartParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["updates"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "updates array is required"
                    });
                }

                var updatesArray = parameters["updates"].ToObject<JArray>();
                var results = new List<object>();

                using (var trans = new Transaction(doc, "Bulk Update Smart Template Parameters"))
                {
                    trans.Start();

                    foreach (var updateItem in updatesArray)
                    {
                        var typeId = new ElementId(updateItem["typeId"].ToObject<int>());
                        var elementType = doc.GetElement(typeId) as ElementType;

                        if (elementType != null)
                        {
                            var itemUpdates = new Dictionary<string, object>();
                            var itemFailures = new List<string>();

                            foreach (var prop in ((JObject)updateItem).Properties())
                            {
                                if (prop.Name == "typeId") continue;

                                var paramName = "ST_" + char.ToUpper(prop.Name[0]) + prop.Name.Substring(1);
                                var param = elementType.LookupParameter(paramName);

                                if (param != null && !param.IsReadOnly)
                                {
                                    try
                                    {
                                        if (param.StorageType == StorageType.String)
                                        {
                                            param.Set(prop.Value.ToString());
                                            itemUpdates[prop.Name] = prop.Value.ToString();
                                        }
                                        else if (param.StorageType == StorageType.Integer)
                                        {
                                            param.Set(prop.Value.ToObject<int>());
                                            itemUpdates[prop.Name] = prop.Value.ToObject<int>();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        itemFailures.Add($"{prop.Name}: {ex.Message}");
                                    }
                                }
                            }

                            results.Add(new
                            {
                                typeId = typeId.Value,
                                typeName = elementType.Name,
                                success = itemFailures.Count == 0,
                                updatedCount = itemUpdates.Count,
                                failedCount = itemFailures.Count
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    processedCount = results.Count,
                    results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Schedule and Resource Access

        /// <summary>
        /// Opens the schedule associated with an element type
        /// </summary>
        [MCPMethod("openLinkedSchedule", Category = "SmartTemplate", Description = "Open the schedule associated with an element type")]
        public static string OpenLinkedSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uidoc.Document;

                if (parameters["typeId"] == null && parameters["scheduleName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either typeId or scheduleName is required"
                    });
                }

                string scheduleName = null;

                if (parameters["scheduleName"] != null)
                {
                    scheduleName = parameters["scheduleName"].ToString();
                }
                else
                {
                    var typeId = new ElementId(parameters["typeId"].ToObject<int>());
                    var elementType = doc.GetElement(typeId) as ElementType;

                    if (elementType != null)
                    {
                        var scheduleParam = elementType.LookupParameter("ST_ScheduleName");
                        if (scheduleParam != null && scheduleParam.HasValue)
                        {
                            scheduleName = scheduleParam.AsString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(scheduleName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No schedule name found for this element type"
                    });
                }

                // Find the schedule
                var schedule = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(vs => vs.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase));

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule '{scheduleName}' not found in document"
                    });
                }

                // Open the schedule
                uidoc.ActiveView = schedule;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = schedule.Id.Value,
                    scheduleName = schedule.Name,
                    message = $"Opened schedule: {schedule.Name}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all resources (URLs) associated with an element type
        /// </summary>
        [MCPMethod("getTypeResources", Category = "SmartTemplate", Description = "Get all resources (URLs) associated with an element type")]
        public static string GetTypeResources(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (parameters["typeId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId is required"
                    });
                }

                var typeId = new ElementId(parameters["typeId"].ToObject<int>());
                var elementType = doc.GetElement(typeId) as ElementType;

                if (elementType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element type with ID {typeId.Value} not found"
                    });
                }

                var resources = new Dictionary<string, string>();

                // Get all URL parameters
                var urlParams = new[] { "ST_ManufacturerURL", "ST_SubmittalLink", "ST_InstallGuideLink" };
                foreach (var paramName in urlParams)
                {
                    var param = elementType.LookupParameter(paramName);
                    if (param != null && param.HasValue)
                    {
                        var key = paramName.Replace("ST_", "").ToLower();
                        resources[key] = param.AsString();
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typeId = elementType.Id.Value,
                    typeName = elementType.Name,
                    resources,
                    resourceCount = resources.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static string GetSmartInfoForElement(Document doc, Element element)
        {
            // Get element type
            var typeId = element.GetTypeId();
            ElementType elementType = null;
            if (typeId != ElementId.InvalidElementId)
            {
                elementType = doc.GetElement(typeId) as ElementType;
            }

            // Extract standard element info
            var elementInfo = new Dictionary<string, object>
            {
                { "elementId", element.Id.Value },
                { "elementName", element.Name },
                { "category", element.Category?.Name ?? "Unknown" },
                { "categoryId", element.Category?.Id.Value }
            };

            // Add type info if available
            if (elementType != null)
            {
                elementInfo["typeId"] = elementType.Id.Value;
                elementInfo["typeName"] = elementType.Name;
                elementInfo["familyName"] = elementType.FamilyName;
                elementInfo["smartTemplateData"] = ExtractSmartTemplateData(elementType);
            }

            // Add some common instance parameters
            var instanceParams = new Dictionary<string, object>();
            foreach (Parameter param in element.Parameters)
            {
                if (param.HasValue && !string.IsNullOrEmpty(param.Definition.Name))
                {
                    var paramValue = GetParameterValue(param);
                    if (paramValue != null)
                    {
                        instanceParams[param.Definition.Name] = paramValue;
                    }
                }
            }
            elementInfo["instanceParameters"] = instanceParams;

            return JsonConvert.SerializeObject(new
            {
                success = true,
                element = elementInfo
            });
        }

        private static string GetSmartInfoForType(Document doc, ElementType elementType)
        {
            var typeInfo = new Dictionary<string, object>
            {
                { "typeId", elementType.Id.Value },
                { "typeName", elementType.Name },
                { "familyName", elementType.FamilyName },
                { "category", elementType.Category?.Name ?? "Unknown" },
                { "categoryId", elementType.Category?.Id.Value }
            };

            // Add smart template data
            typeInfo["smartTemplateData"] = ExtractSmartTemplateData(elementType);

            // Add all type parameters
            var typeParams = new Dictionary<string, object>();
            foreach (Parameter param in elementType.Parameters)
            {
                if (param.HasValue && !string.IsNullOrEmpty(param.Definition.Name))
                {
                    var paramValue = GetParameterValue(param);
                    if (paramValue != null)
                    {
                        typeParams[param.Definition.Name] = paramValue;
                    }
                }
            }
            typeInfo["typeParameters"] = typeParams;

            return JsonConvert.SerializeObject(new
            {
                success = true,
                type = typeInfo
            });
        }

        private static Dictionary<string, object> ExtractSmartTemplateData(ElementType elementType)
        {
            var data = new Dictionary<string, object>();

            var parameterMappings = new Dictionary<string, string>
            {
                { "ST_DetailViewNames", "detailViewNames" },
                { "ST_ScheduleName", "scheduleName" },
                { "ST_Specification", "specification" },
                { "ST_ManufacturerURL", "manufacturerURL" },
                { "ST_ManufacturerName", "manufacturerName" },
                { "ST_ProductModel", "productModel" },
                { "ST_FireRating", "fireRating" },
                { "ST_ULCode", "ulCode" },
                { "ST_STCRating", "stcRating" },
                { "ST_NOANumber", "noaNumber" },
                { "ST_HardwareGroup", "hardwareGroup" },
                { "ST_GlazingType", "glazingType" },
                { "ST_FinishOptions", "finishOptions" },
                { "ST_MountingType", "mountingType" },
                { "ST_ADACompliant", "adaCompliant" },
                { "ST_Notes", "notes" },
                { "ST_SubmittalLink", "submittalLink" },
                { "ST_InstallGuideLink", "installGuideLink" }
            };

            foreach (var mapping in parameterMappings)
            {
                var param = elementType.LookupParameter(mapping.Key);
                if (param != null && param.HasValue)
                {
                    data[mapping.Value] = GetParameterValue(param);
                }
            }

            return data;
        }

        private static object GetParameterValue(Parameter param)
        {
            if (!param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                    {
                        return param.AsInteger() == 1;
                    }
                    return param.AsInteger();
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.ElementId:
                    return param.AsElementId()?.Value;
                default:
                    return null;
            }
        }

        private static BuiltInCategory? GetBuiltInCategory(string categoryName)
        {
            var mappings = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                { "Walls", BuiltInCategory.OST_Walls },
                { "Doors", BuiltInCategory.OST_Doors },
                { "Windows", BuiltInCategory.OST_Windows },
                { "Casework", BuiltInCategory.OST_Casework },
                { "PlumbingFixtures", BuiltInCategory.OST_PlumbingFixtures },
                { "Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures },
                { "Furniture", BuiltInCategory.OST_Furniture },
                { "SpecialtyEquipment", BuiltInCategory.OST_SpecialityEquipment },
                { "Specialty Equipment", BuiltInCategory.OST_SpecialityEquipment },
                { "GenericModels", BuiltInCategory.OST_GenericModel },
                { "Generic Models", BuiltInCategory.OST_GenericModel },
                { "Floors", BuiltInCategory.OST_Floors },
                { "Ceilings", BuiltInCategory.OST_Ceilings },
                { "Roofs", BuiltInCategory.OST_Roofs },
                { "Columns", BuiltInCategory.OST_Columns },
                { "StructuralColumns", BuiltInCategory.OST_StructuralColumns },
                { "Structural Columns", BuiltInCategory.OST_StructuralColumns }
            };

            return mappings.TryGetValue(categoryName, out var result) ? result : (BuiltInCategory?)null;
        }

        #endregion

        #region Parameter Binding

        /// <summary>
        /// Binds Smart Template shared parameters from a shared parameter file to specified categories.
        /// This method actually creates the parameter bindings programmatically.
        /// </summary>
        [MCPMethod("bindSmartParametersFromFile", Category = "SmartTemplate", Description = "Bind smart template shared parameters from a shared parameter file to specified categories")]
        public static string BindSmartParametersFromFile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                // Get the shared parameter file path
                var sharedParamFilePath = parameters["sharedParameterFile"]?.ToString();
                if (string.IsNullOrEmpty(sharedParamFilePath))
                {
                    // Default path
                    sharedParamFilePath = @"D:\Revit Templates\Firm Templates\Fantal Consultant\SmartTemplate_SharedParameters.txt";
                }

                // Check if file exists
                if (!System.IO.File.Exists(sharedParamFilePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Shared parameter file not found: {sharedParamFilePath}"
                    });
                }

                // Store original shared parameter file and set our file
                var originalFile = app.SharedParametersFilename;
                app.SharedParametersFilename = sharedParamFilePath;

                try
                {
                    var sharedParamFile = app.OpenSharedParameterFile();
                    if (sharedParamFile == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Could not open shared parameter file. Check file format."
                        });
                    }

                    // Find the Smart Template Parameters group
                    var group = sharedParamFile.Groups.get_Item("Smart Template Parameters");
                    if (group == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Smart Template Parameters group not found in shared parameter file. Expected group name: 'Smart Template Parameters'"
                        });
                    }

                    // Categories to bind to
                    var categoryNames = parameters["categories"]?.ToObject<List<string>>() ?? new List<string>
                    {
                        "Walls", "Doors", "Windows", "Casework", "PlumbingFixtures",
                        "Furniture", "SpecialtyEquipment", "GenericModels"
                    };

                    // Build category set
                    var categorySet = new CategorySet();
                    var validCategories = new List<string>();
                    foreach (var catName in categoryNames)
                    {
                        var builtInCat = GetBuiltInCategory(catName);
                        if (builtInCat.HasValue)
                        {
                            var category = doc.Settings.Categories.get_Item(builtInCat.Value);
                            if (category != null && category.AllowsBoundParameters)
                            {
                                categorySet.Insert(category);
                                validCategories.Add(catName);
                            }
                        }
                    }

                    if (categorySet.Size == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No valid categories found for parameter binding"
                        });
                    }

                    var boundParams = new List<string>();
                    var failedParams = new List<object>();
                    var skippedParams = new List<string>();

                    using (var trans = new Transaction(doc, "Bind Smart Template Parameters"))
                    {
                        trans.Start();

                        // Create type binding for all ST_ parameters
                        var typeBinding = app.Create.NewTypeBinding(categorySet);

                        foreach (var paramName in SmartTemplateParameters)
                        {
                            try
                            {
                                // Get definition from shared parameter file
                                var definition = group.Definitions.get_Item(paramName);
                                if (definition == null)
                                {
                                    failedParams.Add(new
                                    {
                                        name = paramName,
                                        reason = $"Definition not found in SmartTemplate group"
                                    });
                                    continue;
                                }

                                // Check if already bound
                                var existingBinding = doc.ParameterBindings.get_Item(definition);
                                if (existingBinding != null)
                                {
                                    skippedParams.Add($"{paramName} (already bound)");
                                    continue;
                                }

                                // Bind the parameter (use Data group in Revit 2026)
                                bool success = doc.ParameterBindings.Insert(definition, typeBinding, GroupTypeId.Data);

                                if (success)
                                {
                                    boundParams.Add(paramName);
                                }
                                else
                                {
                                    failedParams.Add(new
                                    {
                                        name = paramName,
                                        reason = "Insert failed (unknown reason)"
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                failedParams.Add(new
                                {
                                    name = paramName,
                                    reason = ex.Message
                                });
                            }
                        }

                        trans.Commit();
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "Parameter binding complete",
                        sharedParameterFile = sharedParamFilePath,
                        targetCategories = validCategories,
                        newlyBoundParameters = boundParams,
                        alreadyBoundParameters = skippedParams,
                        failedParameters = failedParams,
                        totalBound = boundParams.Count + skippedParams.Count,
                        totalFailed = failedParams.Count
                    });
                }
                finally
                {
                    // Restore original shared parameter file
                    if (!string.IsNullOrEmpty(originalFile))
                    {
                        app.SharedParametersFilename = originalFile;
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Binds Smart Template shared parameters to specified categories.
        /// This method should be called once per template to set up the parameters.
        /// </summary>
        [MCPMethod("bindSmartParameters", Category = "SmartTemplate", Description = "Bind smart template shared parameters to specified categories")]
        public static string BindSmartParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                // Get the shared parameter file path from parameters, or use default
                var sharedParamFilePath = parameters["sharedParameterFile"]?.ToString();

                // Categories to bind to (can be specified or use defaults)
                var categoryNames = parameters["categories"]?.ToObject<List<string>>() ?? new List<string>
                {
                    "Walls", "Doors", "Windows", "Casework", "PlumbingFixtures",
                    "Furniture", "SpecialtyEquipment", "GenericModels"
                };

                var boundParams = new List<string>();
                var failedParams = new List<object>();
                var boundCategories = new List<string>();

                using (var trans = new Transaction(doc, "Bind Smart Template Parameters"))
                {
                    trans.Start();

                    // Get the category set
                    var categorySet = new CategorySet();
                    foreach (var catName in categoryNames)
                    {
                        var builtInCat = GetBuiltInCategory(catName);
                        if (builtInCat.HasValue)
                        {
                            var category = doc.Settings.Categories.get_Item(builtInCat.Value);
                            if (category != null && category.AllowsBoundParameters)
                            {
                                categorySet.Insert(category);
                                boundCategories.Add(catName);
                            }
                        }
                    }

                    if (categorySet.Size == 0)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No valid categories found for parameter binding"
                        });
                    }

                    // Parameters to check for
                    var smartParams = SmartTemplateParameters;

                    // Try to find each parameter
                    var binding = doc.ParameterBindings;

                    foreach (var paramName in smartParams)
                    {
                        try
                        {
                            // Check if parameter already exists
                            var iter = binding.ForwardIterator();
                            bool paramExists = false;
                            while (iter.MoveNext())
                            {
                                var def = iter.Key;
                                if (def.Name == paramName)
                                {
                                    paramExists = true;
                                    boundParams.Add($"{paramName} (already bound)");
                                    break;
                                }
                            }

                            if (!paramExists)
                            {
                                // Parameter not found - needs to be added via shared parameter file
                                failedParams.Add(new
                                {
                                    name = paramName,
                                    reason = "Not bound. Use Manage > Project Parameters > Add from shared parameter file."
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            failedParams.Add(new
                            {
                                name = paramName,
                                reason = ex.Message
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Parameter binding check complete",
                    boundParameters = boundParams,
                    unboundParameters = failedParams,
                    targetCategories = boundCategories,
                    note = "To add new shared parameters, create them in a shared parameter file and bind via Revit UI or use the SharedParameterElement API with an external definition file."
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
