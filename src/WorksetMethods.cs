using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Revit Worksets (Worksharing)
    /// Handles workset creation, management, element assignment, and worksharing operations
    /// </summary>
    public static class WorksetMethods
    {
        #region Workset Creation and Management

        /// <summary>
        /// Creates a new workset
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing worksetName</param>
        /// <returns>JSON response with success status and workset ID</returns>
        [MCPMethod("createWorkset", Category = "Workset", Description = "Creates a new workset in a workshared document")]
        public static string CreateWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Check if document is workshared
                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared. Enable worksharing first."
                    });
                }

                // Parse parameters
                var worksetName = parameters["worksetName"]?.ToString();
                if (string.IsNullOrEmpty(worksetName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "worksetName is required"
                    });
                }

                using (var trans = new Transaction(doc, "Create Workset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create new workset
                    var workset = Workset.Create(doc, worksetName);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        worksetId = workset.Id.IntegerValue,
                        worksetName = workset.Name,
                        message = $"Workset '{worksetName}' created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all worksets in the project
        /// </summary>
        [MCPMethod("getAllWorksets", Category = "Workset", Description = "Gets all worksets in the project")]
        public static string GetAllWorksets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Check if document is workshared
                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Parse parameters
                var includeSystem = parameters["includeSystem"]?.ToObject<bool>() ?? false;

                // Get all worksets
                var worksetCollector = new FilteredWorksetCollector(doc);
                var worksets = worksetCollector.OfKind(WorksetKind.UserWorkset).ToList();

                if (includeSystem)
                {
                    worksets.AddRange(worksetCollector.OfKind(WorksetKind.StandardWorkset));
                }

                var worksetList = new List<object>();
                foreach (var workset in worksets)
                {
                    worksetList.Add(new
                    {
                        worksetId = workset.Id.IntegerValue,
                        name = workset.Name,
                        kind = workset.Kind.ToString(),
                        owner = workset.Owner,
                        isEditable = workset.IsEditable,
                        isOpen = workset.IsOpen,
                        isDefaultWorkset = workset.IsDefaultWorkset
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    worksets = worksetList,
                    count = worksetList.Count
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets information about a specific workset
        /// </summary>
        [MCPMethod("getWorksetInfo", Category = "Workset", Description = "Gets detailed information about a specific workset")]
        public static string GetWorksetInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Parse parameters
                if (parameters["worksetId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "worksetId is required"
                    });
                }

                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));

                // Get workset
                var workset = doc.GetWorksetTable().GetWorkset(worksetId);

                // Count elements in workset
                var collector = new FilteredElementCollector(doc)
                    .WherePasses(new ElementWorksetFilter(worksetId));
                var elementCount = collector.GetElementCount();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    worksetId = workset.Id.IntegerValue,
                    name = workset.Name,
                    kind = workset.Kind.ToString(),
                    owner = workset.Owner,
                    isEditable = workset.IsEditable,
                    isOpen = workset.IsOpen,
                    isDefaultWorkset = workset.IsDefaultWorkset,
                    elementCount = elementCount
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Renames a workset
        /// </summary>
        [MCPMethod("renameWorkset", Category = "Workset", Description = "Renames an existing workset")]
        public static string RenameWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Document is not workshared" });
                }

                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));
                var newName = parameters["newName"]?.ToString();

                if (string.IsNullOrEmpty(newName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "newName is required" });
                }

                using (var trans = new Transaction(doc, "Rename Workset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    WorksetTable.RenameWorkset(doc, worksetId, newName);
                    var workset = doc.GetWorksetTable().GetWorkset(worksetId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        worksetId = workset.Id.IntegerValue,
                        newName = workset.Name,
                        message = $"Workset renamed to '{newName}'"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Deletes a workset
        /// </summary>
        [MCPMethod("deleteWorkset", Category = "Workset", Description = "Deletes a workset from the project")]
        public static string DeleteWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Document is not workshared" });
                }

                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));
                var targetWorksetId = new WorksetId(int.Parse(parameters["targetWorksetId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Workset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var settings = new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, targetWorksetId);
                    WorksetTable.DeleteWorkset(doc, worksetId, settings);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"Workset {worksetId.IntegerValue} deleted, elements moved to workset {targetWorksetId.IntegerValue}"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Element Workset Assignment

        /// <summary>
        /// Sets the workset for an element
        /// </summary>
        [MCPMethod("setElementWorkset", Category = "Workset", Description = "Assigns an element to a specific workset")]
        public static string SetElementWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Document is not workshared" });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));

                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                using (var trans = new Transaction(doc, "Set Element Workset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var param = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (param != null && !param.IsReadOnly)
                    {
                        param.Set(worksetId.IntegerValue);
                    }
                    else
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Element workset parameter is read-only or not available" });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = elementId.Value,
                        worksetId = worksetId.IntegerValue,
                        message = "Element workset updated"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets the workset for an element
        /// </summary>
        [MCPMethod("getElementWorkset", Category = "Workset", Description = "Gets the workset an element belongs to")]
        public static string GetElementWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Document is not workshared" });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var worksetId = element.WorksetId;
                var workset = doc.GetWorksetTable().GetWorkset(worksetId);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId.Value,
                    worksetId = worksetId.IntegerValue,
                    worksetName = workset.Name,
                    isEditable = workset.IsEditable,
                    owner = workset.Owner
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all elements in a workset
        /// </summary>
        [MCPMethod("getElementsInWorkset", Category = "Workset", Description = "Gets all elements belonging to a specific workset")]
        public static string GetElementsInWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Document is not workshared" });
                }

                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));

                var collector = new FilteredElementCollector(doc)
                    .WherePasses(new ElementWorksetFilter(worksetId));

                var elements = new List<object>();
                foreach (var elem in collector)
                {
                    elements.Add(new
                    {
                        elementId = elem.Id.Value,
                        category = elem.Category?.Name ?? "No Category",
                        name = elem.Name
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    worksetId = worksetId.IntegerValue,
                    elements = elements,
                    count = elements.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Moves elements to a different workset
        /// </summary>
        [MCPMethod("moveElementsToWorkset", Category = "Workset", Description = "Moves multiple elements to a different workset")]
        public static string MoveElementsToWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "Document is not workshared" });
                }

                var elementIds = parameters["elementIds"]?.ToObject<List<int>>();
                var targetWorksetId = new WorksetId(int.Parse(parameters["targetWorksetId"].ToString()));

                if (elementIds == null || elementIds.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "elementIds array is required" });
                }

                using (var trans = new Transaction(doc, "Move Elements to Workset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    int successCount = 0;
                    int failedCount = 0;

                    foreach (var id in elementIds)
                    {
                        var element = doc.GetElement(new ElementId(id));
                        if (element != null)
                        {
                            var param = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                            if (param != null && !param.IsReadOnly)
                            {
                                param.Set(targetWorksetId.IntegerValue);
                                successCount++;
                            }
                            else
                            {
                                failedCount++;
                            }
                        }
                        else
                        {
                            failedCount++;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        targetWorksetId = targetWorksetId.IntegerValue,
                        successCount = successCount,
                        failedCount = failedCount,
                        totalProcessed = elementIds.Count,
                        message = $"Moved {successCount} elements to workset {targetWorksetId.IntegerValue}"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Workset Visibility

        /// <summary>
        /// Sets workset visibility in a view
        /// </summary>
        [MCPMethod("setWorksetVisibility", Category = "Workset", Description = "Sets workset visibility override in a view")]
        public static string SetWorksetVisibility(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));
                var visibility = parameters["visibility"]?.ToString(); // "visible", "hidden", "global"

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set Workset Visibility"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    WorksetVisibility worksetVis;

                    if (visibility == "visible")
                    {
                        worksetVis = WorksetVisibility.Visible;
                    }
                    else if (visibility == "hidden")
                    {
                        worksetVis = WorksetVisibility.Hidden;
                    }
                    else if (visibility == "global")
                    {
                        worksetVis = WorksetVisibility.UseGlobalSetting;
                    }
                    else
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Invalid visibility value. Use 'visible', 'hidden', or 'global'"
                        });
                    }

                    view.SetWorksetVisibility(worksetId, worksetVis);
                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewId.Value,
                        worksetId = worksetId.IntegerValue,
                        visibility = visibility,
                        message = $"Workset visibility set to '{visibility}' in view"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets workset visibility settings for a view
        /// </summary>
        [MCPMethod("getWorksetVisibilityInView", Category = "Workset", Description = "Gets workset visibility settings for a view")]
        public static string GetWorksetVisibilityInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                var worksetVisibilities = new System.Collections.Generic.List<object>();

                var worksetCollector = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                foreach (Workset workset in worksetCollector)
                {
                    var visibility = view.GetWorksetVisibility(workset.Id);
                    string visibilityStr = visibility == WorksetVisibility.Visible ? "visible" :
                                           visibility == WorksetVisibility.Hidden ? "hidden" : "global";

                    worksetVisibilities.Add(new
                    {
                        worksetId = workset.Id.IntegerValue,
                        worksetName = workset.Name,
                        visibility = visibilityStr
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId.Value,
                    viewName = view.Name,
                    worksets = worksetVisibilities,
                    count = worksetVisibilities.Count
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets global workset visibility
        /// </summary>
        [MCPMethod("setGlobalWorksetVisibility", Category = "Workset", Description = "Sets global visibility for a workset across all views")]
        public static string SetGlobalWorksetVisibility(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));
                var visibility = parameters["visibility"]?.ToString(); // "visible" or "hidden"

                // API LIMITATION: Revit 2026 doesn't provide a direct API to set global/default workset visibility
                // The Workset class no longer has a DefaultVisibility property
                // Global visibility must be managed through individual views

                var workset = doc.GetWorksetTable().GetWorkset(worksetId);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API LIMITATION: Setting global workset visibility is not supported in Revit 2026 API",
                    limitation = "Revit 2026 removed global/default workset visibility API",
                    worksetId = worksetId.IntegerValue,
                    worksetName = workset.Name,
                    workaround = "Use SetWorksetVisibility to control visibility in specific views"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Workset Editing and Borrowing

        /// <summary>
        /// Checks if a workset is editable by current user
        /// </summary>
        [MCPMethod("isWorksetEditable", Category = "Workset", Description = "Checks if a workset is editable by the current user")]
        public static string IsWorksetEditable(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var worksetId = new WorksetId(int.Parse(parameters["worksetId"].ToString()));
                var workset = doc.GetWorksetTable().GetWorkset(worksetId);

                bool isEditable = workset.IsEditable;
                bool isOpen = workset.IsOpen;
                string owner = workset.Owner;

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    worksetId = worksetId.IntegerValue,
                    worksetName = workset.Name,
                    isEditable = isEditable,
                    isOpen = isOpen,
                    owner = owner,
                    message = isEditable ? "Workset is editable by current user" : $"Workset is not editable (owned by {owner})"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Checks if an element is borrowed by current user
        /// </summary>
        [MCPMethod("isElementBorrowed", Category = "Workset", Description = "Checks if an element is borrowed by the current user")]
        public static string IsElementBorrowed(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                var checkoutStatus = WorksharingUtils.GetCheckoutStatus(doc, elementId);
                bool isBorrowed = (checkoutStatus == CheckoutStatus.OwnedByCurrentUser);
                bool isOwnedByOther = (checkoutStatus == CheckoutStatus.OwnedByOtherUser);
                string owner = "";

                if (isOwnedByOther || isBorrowed)
                {
                    // Note: Getting the actual owner name requires additional API calls
                    owner = isOwnedByOther ? "Another user" : "Current user";
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId.Value,
                    isBorrowed = isBorrowed,
                    isOwnedByOther = isOwnedByOther,
                    checkoutStatus = checkoutStatus.ToString(),
                    owner = owner,
                    message = isBorrowed ? "Element is borrowed by current user" :
                              isOwnedByOther ? "Element is owned by another user" :
                              "Element is available"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Relinquishes ownership of elements/worksets
        /// </summary>
        [MCPMethod("relinquishOwnership", Category = "Workset", Description = "Relinquishes ownership of borrowed elements or worksets")]
        public static string RelinquishOwnership(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Parse relinquish options (default: relinquish all)
                bool relinquishElements = parameters["relinquishElements"]?.ToObject<bool>() ?? true;
                bool relinquishWorksets = parameters["relinquishWorksets"]?.ToObject<bool>() ?? true;
                bool relinquishFamilies = parameters["relinquishFamilies"]?.ToObject<bool>() ?? true;
                bool relinquishViewWorksets = parameters["relinquishViewWorksets"]?.ToObject<bool>() ?? true;
                bool relinquishStandards = parameters["relinquishStandards"]?.ToObject<bool>() ?? true;

                var options = new RelinquishOptions(true); // Compact the central model
                options.CheckedOutElements = relinquishElements;
                options.UserWorksets = relinquishWorksets;
                options.FamilyWorksets = relinquishFamilies;
                options.ViewWorksets = relinquishViewWorksets;
                options.StandardWorksets = relinquishStandards;

                var transactWithCentralOptions = new TransactWithCentralOptions();
                var transactStatus = WorksharingUtils.RelinquishOwnership(doc, options, transactWithCentralOptions);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    status = transactStatus.ToString(),
                    relinquishedElements = relinquishElements,
                    relinquishedWorksets = relinquishWorksets,
                    relinquishedFamilies = relinquishFamilies,
                    relinquishedViewWorksets = relinquishViewWorksets,
                    relinquishedStandards = relinquishStandards,
                    message = "Ownership relinquished successfully"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets checkout status for elements
        /// </summary>
        [MCPMethod("getCheckoutStatus", Category = "Workset", Description = "Gets the checkout and ownership status for elements")]
        public static string GetCheckoutStatus(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Parse element IDs (optional)
                var elementIds = new System.Collections.Generic.List<ElementId>();

                if (parameters["elementIds"] != null)
                {
                    var idArray = parameters["elementIds"] as JArray;
                    foreach (var id in idArray)
                    {
                        elementIds.Add(new ElementId(int.Parse(id.ToString())));
                    }
                }

                var checkoutStatuses = new System.Collections.Generic.List<object>();

                foreach (var elementId in elementIds)
                {
                    var element = doc.GetElement(elementId);
                    if (element == null) continue;

                    var checkoutStatus = WorksharingUtils.GetCheckoutStatus(doc, elementId);

                    checkoutStatuses.Add(new
                    {
                        elementId = elementId.Value,
                        elementName = element.Name,
                        checkoutStatus = checkoutStatus.ToString(),
                        isBorrowed = (checkoutStatus == CheckoutStatus.OwnedByCurrentUser),
                        isOwnedByOther = (checkoutStatus == CheckoutStatus.OwnedByOtherUser)
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elements = checkoutStatuses,
                    count = checkoutStatuses.Count
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Worksharing Settings

        /// <summary>
        /// Enables worksharing in a non-workshared document
        /// </summary>
        public static string EnableWorksharing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is already workshared"
                    });
                }

                // Parse default workset names (optional)
                string defaultWorksetName = parameters["defaultWorksetName"]?.ToString() ?? "Workset1";

                using (var trans = new Transaction(doc, "Enable Worksharing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.EnableWorksharing(defaultWorksetName, "Shared Levels and Grids");

                    trans.Commit();

                    var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                    var worksetList = new System.Collections.Generic.List<object>();

                    foreach (Workset workset in worksets)
                    {
                        worksetList.Add(new
                        {
                            worksetId = workset.Id.IntegerValue,
                            worksetName = workset.Name
                        });
                    }

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        isWorkshared = doc.IsWorkshared,
                        defaultWorkset = defaultWorksetName,
                        worksets = worksetList,
                        message = "Worksharing enabled successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Checks if the document is workshared
        /// </summary>
        [MCPMethod("isWorkshared", Category = "Workset", Description = "Checks if the document has worksharing enabled")]
        public static string IsWorkshared(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                bool isWorkshared = doc.IsWorkshared;
                string centralModelPath = "";
                int worksetCount = 0;

                if (isWorkshared)
                {
                    var modelPath = doc.GetWorksharingCentralModelPath();
                    if (modelPath != null)
                    {
                        centralModelPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                    }

                    var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                    worksetCount = worksets.Count();
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    isWorkshared = isWorkshared,
                    centralModelPath = centralModelPath,
                    worksetCount = worksetCount,
                    documentTitle = doc.Title,
                    message = isWorkshared ?
                        $"Document is workshared with {worksetCount} worksets" :
                        "Document is not workshared"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets worksharing options for the document
        /// </summary>
        [MCPMethod("getWorksharingOptions", Category = "Workset", Description = "Gets worksharing configuration options for the document")]
        public static string GetWorksharingOptions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var centralModelPath = doc.GetWorksharingCentralModelPath();
                string centralPath = "";
                if (centralModelPath != null)
                {
                    centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralModelPath);
                }

                var worksharingInfo = new
                {
                    centralModelPath = centralPath,
                    isWorkshared = doc.IsWorkshared,
                    isDetached = doc.IsDetached,
                    documentTitle = doc.Title,
                    pathName = doc.PathName,
                    worksetCount = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Count()
                };

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    worksharingOptions = worksharingInfo
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Synchronization

        /// <summary>
        /// Synchronizes with central model
        /// </summary>
        [MCPMethod("synchronizeWithCentral", Category = "Workset", Description = "Synchronizes the local file with the central model")]
        public static string SynchronizeWithCentral(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                string comment = parameters["comment"]?.ToString() ?? "Synchronized via MCP";
                bool compact = parameters["compact"]?.ToObject<bool>() ?? true;
                bool relinquishElements = parameters["relinquishElements"]?.ToObject<bool>() ?? false;
                bool relinquishWorksets = parameters["relinquishWorksets"]?.ToObject<bool>() ?? false;

                var syncOptions = new SynchronizeWithCentralOptions();
                syncOptions.Comment = comment;
                syncOptions.Compact = compact;
                syncOptions.SaveLocalBefore = true;
                syncOptions.SaveLocalAfter = true;

                var relinquishOptions = new RelinquishOptions(compact);
                relinquishOptions.CheckedOutElements = relinquishElements;
                relinquishOptions.UserWorksets = relinquishWorksets;
                syncOptions.SetRelinquishOptions(relinquishOptions);

                var transactOptions = new TransactWithCentralOptions();
                doc.SynchronizeWithCentral(transactOptions, syncOptions);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Synchronized with central model",
                    comment = comment,
                    compact = compact,
                    relinquishedElements = relinquishElements,
                    relinquishedWorksets = relinquishWorksets
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Reloads latest changes from central
        /// </summary>
        [MCPMethod("reloadLatest", Category = "Workset", Description = "Reloads the latest changes from the central model")]
        public static string ReloadLatest(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var reloadOptions = new ReloadLatestOptions();
                doc.ReloadLatest(reloadOptions);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Latest changes reloaded from central model",
                    documentTitle = doc.Title
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets synchronization history
        /// </summary>
        [MCPMethod("getSyncHistory", Category = "Workset", Description = "Gets the synchronization history with central")]
        public static string GetSyncHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // API LIMITATION: Revit 2026 doesn't provide direct API access to sync history
                // Sync history is stored internally but not exposed through the API
                // Workaround: Can get basic worksharing info but not detailed sync events

                var centralModelPath = doc.GetWorksharingCentralModelPath();
                string centralPath = centralModelPath != null ?
                    ModelPathUtils.ConvertModelPathToUserVisiblePath(centralModelPath) : "";

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API LIMITATION: Sync history not accessible via Revit 2026 API",
                    limitation = "Detailed synchronization history is not exposed through the Revit API",
                    workaround = "Use Revit UI (Collaborate > Synchronize > History) or check journal files",
                    centralModelPath = centralPath,
                    documentTitle = doc.Title
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Workset Organization

        /// <summary>
        /// Gets worksets by category
        /// </summary>
        [MCPMethod("getWorksetsByCategory", Category = "Workset", Description = "Gets workset assignments grouped by element category")]
        public static string GetWorksetsByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Get category parameter
                string categoryName = parameters["category"]?.ToString();
                if (string.IsNullOrEmpty(categoryName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "category parameter is required"
                    });
                }

                // Get built-in category from name
                BuiltInCategory bic;
                if (!Enum.TryParse(categoryName, out bic))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid category name: {categoryName}"
                    });
                }

                // Get all elements in this category
                var collector = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType();

                // Build a dictionary of workset usage counts
                var worksetUsage = new Dictionary<int, int>();
                var worksetNames = new Dictionary<int, string>();

                foreach (Element elem in collector)
                {
                    var worksetId = elem.WorksetId;
                    if (worksetId != WorksetId.InvalidWorksetId)
                    {
                        int wsId = worksetId.IntegerValue;
                        if (!worksetUsage.ContainsKey(wsId))
                        {
                            worksetUsage[wsId] = 0;
                            var workset = doc.GetWorksetTable().GetWorkset(worksetId);
                            worksetNames[wsId] = workset.Name;
                        }
                        worksetUsage[wsId]++;
                    }
                }

                // Sort by usage count (most used first)
                var sortedWorksets = worksetUsage
                    .OrderByDescending(kvp => kvp.Value)
                    .Select(kvp => new
                    {
                        worksetId = kvp.Key,
                        worksetName = worksetNames[kvp.Key],
                        elementCount = kvp.Value
                    })
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = categoryName,
                    worksets = sortedWorksets,
                    totalWorksets = sortedWorksets.Count,
                    totalElements = worksetUsage.Values.Sum()
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates workset naming scheme
        /// </summary>
        [MCPMethod("createWorksetNamingScheme", Category = "Workset", Description = "Creates worksets based on a defined naming scheme")]
        public static string CreateWorksetNamingScheme(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                string scheme = parameters["scheme"]?.ToString()?.ToLower();
                if (string.IsNullOrEmpty(scheme))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheme parameter is required (by_level, by_discipline, by_building, custom)"
                    });
                }

                var worksetNames = new List<string>();

                if (scheme == "by_level")
                {
                    // Get all levels in document
                    var levels = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .ToList();

                    foreach (var level in levels)
                    {
                        worksetNames.Add(level.Name);
                    }
                }
                else if (scheme == "by_discipline")
                {
                    worksetNames.AddRange(new[]
                    {
                        "Architecture",
                        "Structure",
                        "MEP",
                        "Electrical",
                        "Plumbing",
                        "HVAC",
                        "Fire Protection",
                        "Site"
                    });
                }
                else if (scheme == "by_building")
                {
                    int buildingCount = parameters["buildingCount"]?.ToObject<int>() ?? 4;
                    for (int i = 1; i <= buildingCount; i++)
                    {
                        worksetNames.Add($"Building {i}");
                    }
                }
                else if (scheme == "custom")
                {
                    var names = parameters["worksetNames"]?.ToObject<List<string>>();
                    if (names == null || names.Count == 0)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "worksetNames parameter is required for custom scheme"
                        });
                    }
                    worksetNames.AddRange(names);
                }
                else
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid scheme: {scheme}. Valid schemes: by_level, by_discipline, by_building, custom"
                    });
                }

                // Create the worksets
                var createdWorksets = new List<object>();
                using (var trans = new Transaction(doc, "Create Workset Naming Scheme"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var name in worksetNames)
                    {
                        // Check if workset already exists
                        var existing = new FilteredWorksetCollector(doc)
                            .OfKind(WorksetKind.UserWorkset)
                            .FirstOrDefault(w => w.Name == name);

                        if (existing == null)
                        {
                            var newWorkset = Workset.Create(doc, name);
                            createdWorksets.Add(new
                            {
                                worksetId = newWorkset.Id.IntegerValue,
                                worksetName = newWorkset.Name,
                                status = "created"
                            });
                        }
                        else
                        {
                            createdWorksets.Add(new
                            {
                                worksetId = existing.Id.IntegerValue,
                                worksetName = existing.Name,
                                status = "already_exists"
                            });
                        }
                    }

                    trans.Commit();
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheme = scheme,
                    worksets = createdWorksets,
                    totalCreated = createdWorksets.Count(w => ((dynamic)w).status == "created"),
                    totalExisting = createdWorksets.Count(w => ((dynamic)w).status == "already_exists")
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets active workset for the session
        /// </summary>
        [MCPMethod("getActiveWorkset", Category = "Workset", Description = "Gets the active workset for the current session")]
        public static string GetActiveWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Get the active workset from the document
                var activeWorksetId = doc.GetWorksetTable().GetActiveWorksetId();

                if (activeWorksetId == WorksetId.InvalidWorksetId)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active workset found"
                    });
                }

                var activeWorkset = doc.GetWorksetTable().GetWorkset(activeWorksetId);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    activeWorkset = new
                    {
                        worksetId = activeWorksetId.IntegerValue,
                        worksetName = activeWorkset.Name,
                        kind = activeWorkset.Kind.ToString(),
                        isOpen = activeWorkset.IsOpen,
                        isEditable = activeWorkset.IsEditable,
                        isDefaultWorkset = activeWorkset.IsDefaultWorkset
                    }
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets active workset for the session
        /// </summary>
        [MCPMethod("setActiveWorkset", Category = "Workset", Description = "Sets the active workset for the current session")]
        public static string SetActiveWorkset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                // Get workset ID parameter
                if (parameters["worksetId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "worksetId parameter is required"
                    });
                }

                int worksetIdInt = parameters["worksetId"].ToObject<int>();
                var worksetId = new WorksetId(worksetIdInt);

                // Verify workset exists and is editable
                var workset = doc.GetWorksetTable().GetWorkset(worksetId);
                if (workset == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Workset with ID {worksetIdInt} not found"
                    });
                }

                if (!workset.IsEditable)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Workset '{workset.Name}' is not editable"
                    });
                }

                // Set the active workset
                doc.GetWorksetTable().SetActiveWorksetId(worksetId);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Active workset set to '{workset.Name}'",
                    activeWorkset = new
                    {
                        worksetId = worksetId.IntegerValue,
                        worksetName = workset.Name,
                        kind = workset.Kind.ToString(),
                        isOpen = workset.IsOpen,
                        isEditable = workset.IsEditable
                    }
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Bulk Operations

        /// <summary>
        /// Assigns elements to worksets based on their category.
        /// Automatically organizes model by moving elements to appropriate worksets.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - categoryMappings (required): Array of {category, worksetId} or {category, worksetName}
        /// - createMissingWorksets (optional): Create worksets that don't exist (default: false)
        /// - skipIfAlreadyAssigned (optional): Skip elements already in target workset (default: true)
        /// Example categoryMappings:
        ///   [{"category": "OST_Walls", "worksetName": "Architecture"},
        ///    {"category": "OST_StructuralColumns", "worksetName": "Structure"},
        ///    {"category": "OST_MechanicalEquipment", "worksetName": "MEP"}]
        /// </param>
        /// <returns>JSON response with assignment results</returns>
        [MCPMethod("bulkSetWorksetsByCategory", Category = "Workset", Description = "Assigns elements to worksets in bulk based on their category")]
        public static string BulkSetWorksetsByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (!doc.IsWorkshared)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared. Enable worksharing first."
                    });
                }

                // Parse category mappings
                var mappingsArray = parameters["categoryMappings"] as JArray;
                if (mappingsArray == null || mappingsArray.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categoryMappings array is required. Format: [{category, worksetId/worksetName}, ...]"
                    });
                }

                bool createMissingWorksets = parameters["createMissingWorksets"]?.ToObject<bool>() ?? false;
                bool skipIfAlreadyAssigned = parameters["skipIfAlreadyAssigned"]?.ToObject<bool>() ?? true;

                // Build workset lookup by name
                var worksetTable = doc.GetWorksetTable();
                var worksetsByName = new Dictionary<string, WorksetId>(StringComparer.OrdinalIgnoreCase);
                var allWorksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                foreach (Workset ws in allWorksets)
                {
                    worksetsByName[ws.Name] = ws.Id;
                }

                // Parse and validate mappings
                var categoryToWorkset = new Dictionary<BuiltInCategory, WorksetId>();
                var worksetCreationNeeded = new List<string>();

                foreach (JObject mapping in mappingsArray)
                {
                    string categoryName = mapping["category"]?.ToString();
                    if (string.IsNullOrEmpty(categoryName))
                    {
                        continue;
                    }

                    // Parse category
                    BuiltInCategory bic;
                    if (!Enum.TryParse(categoryName, out bic))
                    {
                        // Try with OST_ prefix
                        if (!Enum.TryParse("OST_" + categoryName, out bic))
                        {
                            continue;
                        }
                    }

                    // Resolve workset
                    WorksetId targetWorksetId = WorksetId.InvalidWorksetId;

                    if (mapping["worksetId"] != null)
                    {
                        int wsId = mapping["worksetId"].ToObject<int>();
                        targetWorksetId = new WorksetId(wsId);
                    }
                    else if (mapping["worksetName"] != null)
                    {
                        string wsName = mapping["worksetName"].ToString();
                        if (worksetsByName.ContainsKey(wsName))
                        {
                            targetWorksetId = worksetsByName[wsName];
                        }
                        else if (createMissingWorksets)
                        {
                            worksetCreationNeeded.Add(wsName);
                        }
                    }

                    if (targetWorksetId != WorksetId.InvalidWorksetId ||
                        (createMissingWorksets && mapping["worksetName"] != null))
                    {
                        categoryToWorkset[bic] = targetWorksetId;
                    }
                }

                if (categoryToWorkset.Count == 0 && worksetCreationNeeded.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid category-to-workset mappings found"
                    });
                }

                int totalProcessed = 0;
                int successCount = 0;
                int skippedCount = 0;
                int failedCount = 0;
                var results = new List<object>();
                var failures = new List<string>();

                using (var trans = new Transaction(doc, "Bulk Set Worksets by Category"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create missing worksets first
                    foreach (string wsName in worksetCreationNeeded.Distinct())
                    {
                        if (!worksetsByName.ContainsKey(wsName))
                        {
                            var newWorkset = Workset.Create(doc, wsName);
                            worksetsByName[wsName] = newWorkset.Id;
                        }
                    }

                    // Update category mappings with newly created worksets
                    foreach (JObject mapping in mappingsArray)
                    {
                        if (mapping["worksetName"] != null && mapping["worksetId"] == null)
                        {
                            string wsName = mapping["worksetName"].ToString();
                            string categoryName = mapping["category"]?.ToString();

                            BuiltInCategory bic;
                            if (!Enum.TryParse(categoryName, out bic))
                            {
                                if (!Enum.TryParse("OST_" + categoryName, out bic))
                                {
                                    continue;
                                }
                            }

                            if (worksetsByName.ContainsKey(wsName))
                            {
                                categoryToWorkset[bic] = worksetsByName[wsName];
                            }
                        }
                    }

                    // Process each category
                    foreach (var kvp in categoryToWorkset)
                    {
                        BuiltInCategory category = kvp.Key;
                        WorksetId targetWorksetId = kvp.Value;

                        if (targetWorksetId == WorksetId.InvalidWorksetId)
                        {
                            continue;
                        }

                        int categorySuccess = 0;
                        int categorySkipped = 0;
                        int categoryFailed = 0;

                        // Get elements in this category
                        var collector = new FilteredElementCollector(doc)
                            .OfCategory(category)
                            .WhereElementIsNotElementType();

                        foreach (Element elem in collector)
                        {
                            totalProcessed++;

                            try
                            {
                                // Check if already in target workset
                                if (skipIfAlreadyAssigned && elem.WorksetId == targetWorksetId)
                                {
                                    categorySkipped++;
                                    skippedCount++;
                                    continue;
                                }

                                // Set workset
                                var param = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                if (param != null && !param.IsReadOnly)
                                {
                                    param.Set(targetWorksetId.IntegerValue);
                                    categorySuccess++;
                                    successCount++;
                                }
                                else
                                {
                                    categoryFailed++;
                                    failedCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Add($"{category}: Element {elem.Id.Value} - {ex.Message}");
                                categoryFailed++;
                                failedCount++;
                            }
                        }

                        // Get workset name for result
                        var workset = worksetTable.GetWorkset(targetWorksetId);

                        results.Add(new
                        {
                            category = category.ToString(),
                            worksetId = targetWorksetId.IntegerValue,
                            worksetName = workset?.Name ?? "Unknown",
                            assigned = categorySuccess,
                            skipped = categorySkipped,
                            failed = categoryFailed
                        });
                    }

                    trans.Commit();
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalProcessed = totalProcessed,
                    successCount = successCount,
                    skippedCount = skippedCount,
                    failedCount = failedCount,
                    categoriesProcessed = results.Count,
                    results = results,
                    worksetsCreated = worksetCreationNeeded.Distinct().Count(),
                    failures = failures.Take(10).ToList(),
                    message = $"Assigned {successCount} elements to worksets by category ({skippedCount} skipped, {failedCount} failed)"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion
    }
}
