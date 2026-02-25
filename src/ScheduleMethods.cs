using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Schedules in Revit
    /// Handles schedule creation, modification, formatting, and data extraction
    /// </summary>
    public static class ScheduleMethods
    {
        #region Schedule Creation

        /// <summary>
        /// Creates a new schedule for a specified category
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing scheduleName, category, viewId (optional)</param>
        /// <returns>JSON response with success status and schedule view ID</returns>
        public static string CreateSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var scheduleName = parameters["scheduleName"]?.ToString();
                var categoryName = parameters["category"]?.ToString();

                if (string.IsNullOrEmpty(scheduleName) || string.IsNullOrEmpty(categoryName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleName and category are required parameters"
                    });
                }

                // Find the category by name
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
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category '{categoryName}' not found. Use categories like: Doors, Windows, Walls, Rooms, etc."
                    });
                }

                using (var trans = new Transaction(doc, "Create Schedule"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the schedule
                    var schedule = ViewSchedule.CreateSchedule(doc, category.Id);
                    schedule.Name = scheduleName;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)schedule.Id.Value,
                        scheduleName = schedule.Name,
                        category = category.Name,
                        message = $"Schedule '{scheduleName}' created successfully for category '{category.Name}'"
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
        /// Creates a key schedule
        /// </summary>
        public static string CreateKeySchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var scheduleName = parameters["scheduleName"]?.ToString();
                var categoryName = parameters["category"]?.ToString();

                if (string.IsNullOrEmpty(scheduleName) || string.IsNullOrEmpty(categoryName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleName and category are required parameters"
                    });
                }

                // Find the category by name
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
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category '{categoryName}' not found. Use categories that support key schedules like: Door, Window, etc."
                    });
                }

                using (var trans = new Transaction(doc, "Create Key Schedule"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the key schedule
                    var schedule = ViewSchedule.CreateKeySchedule(doc, category.Id);
                    schedule.Name = scheduleName;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)schedule.Id.Value,
                        scheduleName = schedule.Name,
                        category = category.Name,
                        scheduleType = "KeySchedule",
                        message = $"Key schedule '{scheduleName}' created successfully for category '{category.Name}'"
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
        /// Creates a material takeoff schedule
        /// </summary>
        public static string CreateMaterialTakeoff(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var scheduleName = parameters["scheduleName"]?.ToString();
                var categoryName = parameters["category"]?.ToString();

                if (string.IsNullOrEmpty(scheduleName) || string.IsNullOrEmpty(categoryName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleName and category are required parameters"
                    });
                }

                // Find the category by name
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
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category '{categoryName}' not found. Use categories like: Walls, Floors, Roofs, etc."
                    });
                }

                using (var trans = new Transaction(doc, "Create Material Takeoff"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the material takeoff schedule
                    var schedule = ViewSchedule.CreateMaterialTakeoff(doc, category.Id);
                    schedule.Name = scheduleName;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)schedule.Id.Value,
                        scheduleName = schedule.Name,
                        category = category.Name,
                        scheduleType = "MaterialTakeoff",
                        message = $"Material takeoff schedule '{scheduleName}' created successfully for category '{category.Name}'"
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
        /// Creates a sheet list schedule
        /// </summary>
        public static string CreateSheetList(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var scheduleName = parameters["scheduleName"]?.ToString();

                if (string.IsNullOrEmpty(scheduleName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleName is required"
                    });
                }

                // Get the Sheets category (built-in category)
                var sheetCategoryId = new ElementId(BuiltInCategory.OST_Sheets);

                using (var trans = new Transaction(doc, "Create Sheet List"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the sheet list schedule
                    var schedule = ViewSchedule.CreateSchedule(doc, sheetCategoryId);
                    schedule.Name = scheduleName;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)schedule.Id.Value,
                        scheduleName = schedule.Name,
                        category = "Sheets",
                        scheduleType = "SheetList",
                        message = $"Sheet list '{scheduleName}' created successfully"
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

        #endregion

        #region Schedule Fields

        /// <summary>
        /// Adds a field (column) to a schedule
        /// </summary>
        public static string AddScheduleField(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldName = parameters["fieldName"]?.ToString();
                var customHeading = parameters["heading"]?.ToString();

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Add Schedule Field"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get schedulable fields
                    var schedulableFields = schedule.Definition.GetSchedulableFields();
                    SchedulableField targetField = null;

                    // Find the field by name
                    foreach (var field in schedulableFields)
                    {
                        var paramId = field.ParameterId;
                        // Built-in parameters have negative IDs
                        if (paramId.Value < 0)
                        {
                            // Built-in parameter
                            var builtInParam = field.ParameterId;
                            string paramName = null;

                            // Safely get the label - some built-in parameters are invalid
                            try
                            {
                                paramName = LabelUtils.GetLabelFor((BuiltInParameter)builtInParam.Value);
                            }
                            catch
                            {
                                // Invalid built-in parameter, skip it
                                continue;
                            }

                            if (paramName != null && paramName.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetField = field;
                                break;
                            }
                        }
                        else
                        {
                            // Shared or project parameter
                            var param = doc.GetElement(paramId);
                            if (param != null && param.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetField = field;
                                break;
                            }
                        }
                    }

                    if (targetField == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Field '{fieldName}' not found in available schedulable fields for this category"
                        });
                    }

                    // Get diagnostic info about the field we're about to add
                    var fieldParamId = targetField.ParameterId;
                    var fieldTypeName = fieldParamId.Value < 0 ? "BuiltIn" : "Custom";

                    // Add the field
                    ScheduleField scheduleField = null;
                    try
                    {
                        scheduleField = schedule.Definition.AddField(targetField);
                    }
                    catch (Exception addEx)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Revit API Error: {addEx.Message}",
                            fieldName = fieldName,
                            parameterId = fieldParamId.Value,
                            fieldType = fieldTypeName,
                            scheduleId = scheduleId.Value,
                            stackTrace = addEx.StackTrace,
                            diagnosticMessage = $"Failed to add field '{fieldName}' (ParameterId: {fieldParamId.Value}, Type: {fieldTypeName}) to schedule {scheduleId.Value}"
                        });
                    }

                    // Set custom heading if provided
                    if (!string.IsNullOrEmpty(customHeading))
                    {
                        scheduleField.ColumnHeading = customHeading;
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        fieldIndex = scheduleField.FieldIndex,
                        fieldName = scheduleField.GetName(),
                        heading = scheduleField.ColumnHeading,
                        message = $"Field '{fieldName}' added to schedule at index {scheduleField.FieldIndex}"
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
        /// Removes a field from a schedule
        /// </summary>
        public static string RemoveScheduleField(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and fieldIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate field index
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                // Get field info before removing
                var field = definition.GetField(fieldIndex);
                var fieldHeading = field.ColumnHeading;
                var fieldId = definition.GetFieldId(fieldIndex);

                using (var trans = new Transaction(doc, "Remove Schedule Field"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Remove the field
                    definition.RemoveField(fieldId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        removedFieldIndex = fieldIndex,
                        removedFieldHeading = fieldHeading,
                        remainingFieldCount = definition.GetFieldCount(),
                        message = $"Field '{fieldHeading}' (index {fieldIndex}) removed from schedule"
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
        /// Reorders fields in a schedule
        /// </summary>
        public static string ReorderScheduleFields(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                if (parameters["fieldOrder"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "fieldOrder is required (array of field indices)"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                var definition = schedule.Definition;
                var fieldOrder = parameters["fieldOrder"].ToObject<int[]>();

                // Validate field order
                var fieldCount = definition.GetFieldCount();
                if (fieldOrder.Length != fieldCount)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"fieldOrder length ({fieldOrder.Length}) does not match schedule field count ({fieldCount})"
                    });
                }

                // Check that all indices are valid and unique
                var uniqueIndices = new System.Collections.Generic.HashSet<int>(fieldOrder);
                if (uniqueIndices.Count != fieldOrder.Length)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "fieldOrder contains duplicate indices"
                    });
                }

                foreach (var index in fieldOrder)
                {
                    if (index < 0 || index >= fieldCount)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Invalid field index: {index}. Must be between 0 and {fieldCount - 1}"
                        });
                    }
                }

                // Get current field order
                var currentFields = new System.Collections.Generic.List<object>();
                for (int i = 0; i < definition.GetFieldCount(); i++)
                {
                    var field = definition.GetField(i);
                    currentFields.Add(new
                    {
                        index = i,
                        fieldId = field.FieldId.IntegerValue,
                        heading = field.GetName(),
                        parameterId = field.ParameterId != null ? (int)field.ParameterId.Value : -1
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API Limitation: Revit 2026 API does not support field reordering programmatically",
                    note = "Schedule field order can only be modified through the Revit UI",
                    scheduleId = scheduleIdInt,
                    scheduleName = schedule.Name,
                    fieldCount = definition.GetFieldCount(),
                    currentFields,
                    requestedOrder = fieldOrder,
                    workaround = "Remove and re-add fields in the desired order using RemoveScheduleField and AddScheduleField methods"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all fields (columns) in a schedule
        /// </summary>
        public static string GetScheduleFields(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters?["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;
                if (definition == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule definition is null"
                    });
                }

                var fields = new System.Collections.Generic.List<object>();

                // Iterate through all fields with try-catch per field
                for (int i = 0; i < definition.GetFieldCount(); i++)
                {
                    try
                    {
                        var field = definition.GetField(i);
                        if (field == null) continue;

                        fields.Add(new
                        {
                            fieldIndex = i,
                            fieldId = i,
                            columnHeading = field.ColumnHeading ?? "",
                            fieldType = field.FieldType.ToString(),
                            isHidden = field.IsHidden,
                            hasTotal = field.DisplayType == ScheduleFieldDisplayType.Totals
                        });
                    }
                    catch { continue; }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = (int)scheduleId.Value,
                    scheduleName = schedule.Name ?? "",
                    fieldCount = fields.Count,
                    fields = fields
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
        /// Modifies a schedule field's properties
        /// </summary>
        public static string ModifyScheduleField(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and fieldIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate field index
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                using (var trans = new Transaction(doc, "Modify Schedule Field"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var field = definition.GetField(fieldIndex);

                    // Modify column heading if provided
                    if (parameters["heading"] != null)
                    {
                        var heading = parameters["heading"].ToString();
                        field.ColumnHeading = heading;
                    }

                    // Modify width if provided
                    if (parameters["width"] != null)
                    {
                        var width = double.Parse(parameters["width"].ToString());
                        field.SheetColumnWidth = width;
                    }

                    // Modify horizontal alignment if provided
                    if (parameters["horizontalAlignment"] != null)
                    {
                        var alignmentStr = parameters["horizontalAlignment"].ToString().ToLower();
                        ScheduleHorizontalAlignment alignment = ScheduleHorizontalAlignment.Left;

                        switch (alignmentStr)
                        {
                            case "center":
                                alignment = ScheduleHorizontalAlignment.Center;
                                break;
                            case "right":
                                alignment = ScheduleHorizontalAlignment.Right;
                                break;
                            default:
                                alignment = ScheduleHorizontalAlignment.Left;
                                break;
                        }

                        field.HorizontalAlignment = alignment;
                    }

                    // Modify hidden status if provided
                    if (parameters["isHidden"] != null)
                    {
                        var isHidden = bool.Parse(parameters["isHidden"].ToString());
                        field.IsHidden = isHidden;
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        fieldIndex = fieldIndex,
                        columnHeading = field.ColumnHeading,
                        width = field.SheetColumnWidth,
                        horizontalAlignment = field.HorizontalAlignment.ToString(),
                        isHidden = field.IsHidden,
                        message = $"Field at index {fieldIndex} modified successfully"
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
        /// Gets all available schedulable fields for a schedule (not just added fields)
        /// </summary>
        public static string GetAvailableSchedulableFields(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                // Get schedulable fields
                var schedulableFields = schedule.Definition.GetSchedulableFields();
                var availableFields = new System.Collections.Generic.List<object>();

                foreach (var field in schedulableFields)
                {
                    var paramId = field.ParameterId;
                    string fieldName = "";
                    string fieldType = "";

                    if (paramId.Value < 0)
                    {
                        // Built-in parameter
                        try
                        {
                            var builtInParam = (BuiltInParameter)paramId.Value;
                            fieldName = LabelUtils.GetLabelFor(builtInParam);
                            fieldType = "BuiltIn";
                        }
                        catch
                        {
                            fieldName = $"BuiltIn_{Math.Abs(paramId.Value)}";
                            fieldType = "BuiltIn";
                        }
                    }
                    else if (paramId != ElementId.InvalidElementId)
                    {
                        // Shared or project parameter
                        var param = doc.GetElement(paramId);
                        if (param != null)
                        {
                            fieldName = param.Name;
                            fieldType = "Custom";
                        }
                    }

                    if (!string.IsNullOrEmpty(fieldName))
                    {
                        availableFields.Add(new
                        {
                            name = fieldName,
                            parameterId = paramId.Value,
                            fieldType = fieldType
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = scheduleId.Value,
                    availableFields = availableFields,
                    count = availableFields.Count
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

        #region Schedule Filters

        /// <summary>
        /// Adds a filter to a schedule
        /// </summary>
        public static string AddScheduleFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and fieldIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());
                var filterType = parameters["filterType"]?.ToString()?.ToLower() ?? "equals";
                var filterValue = parameters["value"]?.ToString() ?? "";

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                // Map filter type string to Revit ScheduleFilterType
                ScheduleFilterType revitFilterType;
                switch (filterType)
                {
                    case "equals":
                        revitFilterType = ScheduleFilterType.Equal;
                        break;
                    case "notequals":
                    case "not_equals":
                        revitFilterType = ScheduleFilterType.NotEqual;
                        break;
                    case "greater":
                    case "greaterthan":
                        revitFilterType = ScheduleFilterType.GreaterThan;
                        break;
                    case "greaterorequal":
                    case "greater_or_equal":
                        revitFilterType = ScheduleFilterType.GreaterThanOrEqual;
                        break;
                    case "less":
                    case "lessthan":
                        revitFilterType = ScheduleFilterType.LessThan;
                        break;
                    case "lessorequal":
                    case "less_or_equal":
                        revitFilterType = ScheduleFilterType.LessThanOrEqual;
                        break;
                    case "contains":
                        revitFilterType = ScheduleFilterType.Contains;
                        break;
                    case "notcontains":
                    case "not_contains":
                        revitFilterType = ScheduleFilterType.NotContains;
                        break;
                    case "beginswith":
                    case "begins_with":
                        revitFilterType = ScheduleFilterType.BeginsWith;
                        break;
                    case "notbeginswith":
                    case "not_begins_with":
                        revitFilterType = ScheduleFilterType.NotBeginsWith;
                        break;
                    case "endswith":
                    case "ends_with":
                        revitFilterType = ScheduleFilterType.EndsWith;
                        break;
                    case "notendswith":
                    case "not_ends_with":
                        revitFilterType = ScheduleFilterType.NotEndsWith;
                        break;
                    default:
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Invalid filter type '{filterType}'. Use: equals, notEquals, greater, less, contains, etc."
                        });
                }

                using (var trans = new Transaction(doc, "Add Schedule Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Add the filter (Revit 2026 API uses ScheduleFieldId)
                    var fieldId = new ScheduleFieldId(fieldIndex);

                    // Get the field to determine value type
                    var scheduleField = schedule.Definition.GetField(fieldId);
                    ScheduleFilter filter;

                    if (scheduleField != null)
                    {
                        var fieldType = scheduleField.FieldType;
                        var paramId = scheduleField.ParameterId;

                        // Check if field is numeric by trying to parse value
                        if (double.TryParse(filterValue, out double doubleVal))
                        {
                            // Numeric filter - use double constructor
                            filter = new ScheduleFilter(fieldId, revitFilterType, doubleVal);
                        }
                        else if (int.TryParse(filterValue, out int intVal) && (filterValue == "0" || filterValue == "1"))
                        {
                            // Integer/boolean filter
                            filter = new ScheduleFilter(fieldId, revitFilterType, intVal);
                        }
                        else
                        {
                            // String filter
                            filter = new ScheduleFilter(fieldId, revitFilterType, filterValue);
                        }
                    }
                    else
                    {
                        // Fallback to string filter
                        filter = new ScheduleFilter(fieldId, revitFilterType, filterValue);
                    }

                    schedule.Definition.AddFilter(filter);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        fieldIndex = fieldIndex,
                        filterType = filterType,
                        value = filterValue,
                        filterCount = schedule.Definition.GetFilterCount(),
                        message = $"Filter added to field {fieldIndex}: {filterType} '{filterValue}'"
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
        /// Removes a filter from a schedule
        /// </summary>
        public static string RemoveScheduleFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["filterIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and filterIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var filterIndex = int.Parse(parameters["filterIndex"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate filter index
                if (filterIndex < 0 || filterIndex >= definition.GetFilterCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid filter index {filterIndex}. Schedule has {definition.GetFilterCount()} filters."
                    });
                }

                // Get filter info before removing
                var filter = definition.GetFilter(filterIndex);
                var filterType = filter.FilterType.ToString();
                var filterValue = filter.GetStringValue() ?? "";

                using (var trans = new Transaction(doc, "Remove Schedule Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Remove the filter
                    definition.RemoveFilter(filterIndex);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        removedFilterIndex = filterIndex,
                        removedFilterType = filterType,
                        removedFilterValue = filterValue,
                        remainingFilterCount = definition.GetFilterCount(),
                        message = $"Filter at index {filterIndex} ({filterType}: '{filterValue}') removed from schedule"
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
        /// Gets all filters in a schedule
        /// </summary>
        public static string GetScheduleFilters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;
                var filters = new System.Collections.Generic.List<object>();

                // Iterate through all filters
                for (int i = 0; i < definition.GetFilterCount(); i++)
                {
                    var filter = definition.GetFilter(i);
                    var fieldId = filter.FieldId;

                    // Get field information
                    string fieldHeading = "Unknown";
                    int fieldIndex = -1;

                    // Find the field by ID
                    for (int j = 0; j < definition.GetFieldCount(); j++)
                    {
                        if (definition.GetFieldId(j).IntegerValue == fieldId.IntegerValue)
                        {
                            var field = definition.GetField(j);
                            fieldHeading = field.ColumnHeading;
                            fieldIndex = j;
                            break;
                        }
                    }

                    filters.Add(new
                    {
                        filterIndex = i,
                        fieldIndex = fieldIndex,
                        fieldHeading = fieldHeading,
                        filterType = filter.FilterType.ToString(),
                        value = filter.GetStringValue() ?? ""
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = (int)scheduleId.Value,
                    scheduleName = schedule.Name,
                    filterCount = filters.Count,
                    filters = filters
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
        /// Filters a schedule by level, excluding specified levels.
        /// Adds Level as a hidden field if not already present, then applies filters.
        /// </summary>
        public static string FilterScheduleByLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var excludeLevels = parameters["excludeLevels"]?.ToObject<string[]>() ?? new string[0];
                var includeLevels = parameters["includeLevels"]?.ToObject<string[]>() ?? new string[0];

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;
                int levelFieldIndex = -1;
                bool levelFieldAdded = false;

                using (var trans = new Transaction(doc, "Filter Schedule By Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // First, check if Level is already a regular field (not just sort/group)
                    for (int i = 0; i < definition.GetFieldCount(); i++)
                    {
                        var field = definition.GetField(i);
                        if (field != null)
                        {
                            // Check if it's the Level parameter
                            var paramId = field.ParameterId;
                            if (paramId.Value == (int)BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM ||
                                paramId.Value == (int)BuiltInParameter.FAMILY_LEVEL_PARAM ||
                                paramId.Value == (int)BuiltInParameter.SCHEDULE_LEVEL_PARAM ||
                                field.ColumnHeading.ToLower().Contains("level"))
                            {
                                levelFieldIndex = i;
                                break;
                            }
                        }
                    }

                    // If Level field not found, try to add it
                    if (levelFieldIndex == -1)
                    {
                        var schedulableFields = definition.GetSchedulableFields();
                        SchedulableField levelSchedulableField = null;

                        foreach (var sf in schedulableFields)
                        {
                            var paramId = sf.ParameterId;
                            if (paramId.Value < 0)
                            {
                                try
                                {
                                    var builtIn = (BuiltInParameter)paramId.Value;
                                    var label = LabelUtils.GetLabelFor(builtIn);
                                    if (label != null && label.ToLower().Contains("level"))
                                    {
                                        levelSchedulableField = sf;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (levelSchedulableField != null)
                        {
                            var addedField = definition.AddField(levelSchedulableField);
                            levelFieldIndex = addedField.FieldIndex;
                            addedField.IsHidden = true; // Hide the field
                            levelFieldAdded = true;
                        }
                    }

                    if (levelFieldIndex == -1)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Could not find or add Level field to schedule"
                        });
                    }

                    var fieldId = new ScheduleFieldId(levelFieldIndex);
                    var filtersAdded = new System.Collections.Generic.List<string>();

                    // Add exclude filters (NotEqual)
                    foreach (var levelName in excludeLevels)
                    {
                        try
                        {
                            var filter = new ScheduleFilter(fieldId, ScheduleFilterType.NotEqual, levelName);
                            definition.AddFilter(filter);
                            filtersAdded.Add($"Level != {levelName}");
                        }
                        catch (Exception ex)
                        {
                            // Filter may already exist or not be applicable
                            filtersAdded.Add($"Level != {levelName} (failed: {ex.Message})");
                        }
                    }

                    // Add include filters (Equal) - note: multiple Equal filters create OR logic
                    foreach (var levelName in includeLevels)
                    {
                        try
                        {
                            var filter = new ScheduleFilter(fieldId, ScheduleFilterType.Equal, levelName);
                            definition.AddFilter(filter);
                            filtersAdded.Add($"Level = {levelName}");
                        }
                        catch (Exception ex)
                        {
                            filtersAdded.Add($"Level = {levelName} (failed: {ex.Message})");
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        levelFieldIndex = levelFieldIndex,
                        levelFieldAdded = levelFieldAdded,
                        filtersAdded = filtersAdded,
                        message = $"Level filter applied to schedule"
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
        /// Modifies an existing schedule filter
        /// </summary>
        public static string ModifyScheduleFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["filterIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and filterIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var filterIndex = int.Parse(parameters["filterIndex"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate filter index
                if (filterIndex < 0 || filterIndex >= definition.GetFilterCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid filter index {filterIndex}. Schedule has {definition.GetFilterCount()} filters."
                    });
                }

                using (var trans = new Transaction(doc, "Modify Schedule Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get existing filter
                    var existingFilter = definition.GetFilter(filterIndex);
                    var fieldId = existingFilter.FieldId;

                    // Determine new filter type (use existing if not provided)
                    ScheduleFilterType newFilterType = existingFilter.FilterType;
                    if (parameters["filterType"] != null)
                    {
                        var filterTypeStr = parameters["filterType"].ToString().ToLower();
                        switch (filterTypeStr)
                        {
                            case "equals":
                                newFilterType = ScheduleFilterType.Equal;
                                break;
                            case "notequals":
                            case "not_equals":
                                newFilterType = ScheduleFilterType.NotEqual;
                                break;
                            case "greater":
                            case "greaterthan":
                                newFilterType = ScheduleFilterType.GreaterThan;
                                break;
                            case "greaterorequal":
                            case "greater_or_equal":
                                newFilterType = ScheduleFilterType.GreaterThanOrEqual;
                                break;
                            case "less":
                            case "lessthan":
                                newFilterType = ScheduleFilterType.LessThan;
                                break;
                            case "lessorequal":
                            case "less_or_equal":
                                newFilterType = ScheduleFilterType.LessThanOrEqual;
                                break;
                            case "contains":
                                newFilterType = ScheduleFilterType.Contains;
                                break;
                            case "notcontains":
                            case "not_contains":
                                newFilterType = ScheduleFilterType.NotContains;
                                break;
                            case "beginswith":
                            case "begins_with":
                                newFilterType = ScheduleFilterType.BeginsWith;
                                break;
                            case "notbeginswith":
                            case "not_begins_with":
                                newFilterType = ScheduleFilterType.NotBeginsWith;
                                break;
                            case "endswith":
                            case "ends_with":
                                newFilterType = ScheduleFilterType.EndsWith;
                                break;
                            case "notendswith":
                            case "not_ends_with":
                                newFilterType = ScheduleFilterType.NotEndsWith;
                                break;
                        }
                    }

                    // Determine new value (use existing if not provided)
                    var newValue = parameters["value"]?.ToString() ?? existingFilter.GetStringValue();

                    // Remove the old filter and add the modified one
                    definition.RemoveFilter(filterIndex);
                    var newFilter = new ScheduleFilter(fieldId, newFilterType, newValue);
                    definition.AddFilter(newFilter);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        filterIndex = filterIndex,
                        newFilterType = newFilterType.ToString(),
                        newValue = newValue,
                        message = $"Filter at index {filterIndex} modified successfully"
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

        #endregion

        #region Schedule Sorting and Grouping

        /// <summary>
        /// Adds sorting to a schedule field
        /// </summary>
        public static string AddScheduleSorting(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and fieldIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());
                var sortOrder = parameters["sortOrder"]?.ToString()?.ToLower() ?? "ascending";

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                // Validate field index
                var definition = schedule.Definition;
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                // Determine sort order
                bool isAscending = sortOrder == "ascending" || sortOrder == "asc";

                using (var trans = new Transaction(doc, "Add Schedule Sorting"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get the field to sort by
                    var fieldId = definition.GetFieldId(fieldIndex);

                    // Add sorting
                    var sortGroupField = new ScheduleSortGroupField(fieldId);
                    sortGroupField.SortOrder = isAscending ? ScheduleSortOrder.Ascending : ScheduleSortOrder.Descending;
                    definition.AddSortGroupField(sortGroupField);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        fieldIndex = fieldIndex,
                        sortOrder = isAscending ? "Ascending" : "Descending",
                        message = $"Sorting added to field index {fieldIndex}"
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
        /// Adds grouping to a schedule field
        /// </summary>
        public static string AddScheduleGrouping(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and fieldIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());
                var showHeader = parameters["showHeader"] != null ? bool.Parse(parameters["showHeader"].ToString()) : true;
                var showFooter = parameters["showFooter"] != null ? bool.Parse(parameters["showFooter"].ToString()) : false;
                var showBlankLine = parameters["showBlankLine"] != null ? bool.Parse(parameters["showBlankLine"].ToString()) : false;

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                // Validate field index
                var definition = schedule.Definition;
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                using (var trans = new Transaction(doc, "Add Schedule Grouping"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get the field to group by
                    var fieldId = definition.GetFieldId(fieldIndex);

                    // Create sort/group field with grouping settings
                    var sortGroupField = new ScheduleSortGroupField(fieldId);
                    sortGroupField.ShowHeader = showHeader;
                    sortGroupField.ShowFooter = showFooter;
                    sortGroupField.ShowBlankLine = showBlankLine;

                    definition.AddSortGroupField(sortGroupField);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        fieldIndex = fieldIndex,
                        showHeader = showHeader,
                        showFooter = showFooter,
                        showBlankLine = showBlankLine,
                        message = $"Grouping added to field index {fieldIndex}"
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
        /// Gets all sorting and grouping settings
        /// </summary>
        public static string GetScheduleSortGrouping(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;
                var sortGroupFields = new System.Collections.Generic.List<object>();

                // Get all sort/group fields
                int sortGroupCount = definition.GetSortGroupFieldCount();

                for (int i = 0; i < sortGroupCount; i++)
                {
                    var sortGroupField = definition.GetSortGroupField(i);
                    var fieldId = sortGroupField.FieldId;

                    // Find the corresponding field in the schedule
                    string fieldHeading = "Unknown";
                    int fieldIndex = -1;

                    for (int j = 0; j < definition.GetFieldCount(); j++)
                    {
                        if (definition.GetFieldId(j).IntegerValue == fieldId.IntegerValue)
                        {
                            var field = definition.GetField(j);
                            fieldHeading = field.ColumnHeading;
                            fieldIndex = j;
                            break;
                        }
                    }

                    sortGroupFields.Add(new
                    {
                        sortGroupIndex = i,
                        fieldIndex = fieldIndex,
                        fieldHeading = fieldHeading,
                        sortOrder = sortGroupField.SortOrder.ToString(),
                        showHeader = sortGroupField.ShowHeader,
                        showFooter = sortGroupField.ShowFooter,
                        showBlankLine = sortGroupField.ShowBlankLine
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = (int)scheduleId.Value,
                    scheduleName = schedule.Name,
                    sortGroupCount = sortGroupFields.Count,
                    sortGroupFields = sortGroupFields,
                    message = $"Retrieved {sortGroupFields.Count} sort/group settings from schedule"
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
        /// Removes sorting or grouping from a field
        /// </summary>
        public static string RemoveScheduleSorting(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["sortGroupIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and sortGroupIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var sortGroupIndex = int.Parse(parameters["sortGroupIndex"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate sort/group index
                if (sortGroupIndex < 0 || sortGroupIndex >= definition.GetSortGroupFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid sort/group index {sortGroupIndex}. Schedule has {definition.GetSortGroupFieldCount()} sort/group fields."
                    });
                }

                // Get info before removing
                var sortGroupField = definition.GetSortGroupField(sortGroupIndex);
                var fieldId = sortGroupField.FieldId;

                // Find field heading for info
                string fieldHeading = "Unknown";
                for (int j = 0; j < definition.GetFieldCount(); j++)
                {
                    if (definition.GetFieldId(j).IntegerValue == fieldId.IntegerValue)
                    {
                        var field = definition.GetField(j);
                        fieldHeading = field.ColumnHeading;
                        break;
                    }
                }

                using (var trans = new Transaction(doc, "Remove Schedule Sort/Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Remove the sort/group field
                    definition.RemoveSortGroupField(sortGroupIndex);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        removedSortGroupIndex = sortGroupIndex,
                        removedFieldHeading = fieldHeading,
                        remainingSortGroupCount = definition.GetSortGroupFieldCount(),
                        message = $"Sort/group removed from field '{fieldHeading}' at index {sortGroupIndex}"
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

        #endregion

        #region Schedule Formatting

        /// <summary>
        /// Sets schedule appearance properties
        /// </summary>
        public static string FormatScheduleAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Format Schedule Appearance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modifiedProperties = new System.Collections.Generic.List<string>();

                    // Set grid lines (horizontal and vertical)
                    if (parameters["showGridLines"] != null)
                    {
                        bool showGrid = bool.Parse(parameters["showGridLines"].ToString());
                        schedule.Definition.ShowGridLines = showGrid;
                        modifiedProperties.Add($"ShowGridLines = {showGrid}");
                    }

                    // Note: OutlineSegments property removed in Revit 2026 API
                    // Outline functionality may be accessed through different API in future

                    // Set blank row before data
                    if (parameters["blankRowBeforeData"] != null)
                    {
                        bool blankRow = bool.Parse(parameters["blankRowBeforeData"].ToString());
                        schedule.Definition.IsItemized = blankRow;
                        modifiedProperties.Add($"BlankRowBeforeData = {blankRow}");
                    }

                    // Set schedule title
                    if (parameters["showTitle"] != null)
                    {
                        bool showTitle = bool.Parse(parameters["showTitle"].ToString());
                        schedule.Definition.ShowTitle = showTitle;
                        modifiedProperties.Add($"ShowTitle = {showTitle}");
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        modifiedProperties = modifiedProperties,
                        message = $"Schedule appearance updated: {modifiedProperties.Count} properties modified"
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
        /// Sets conditional formatting for a field
        /// </summary>
        public static string SetConditionalFormatting(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and fieldIndex are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate field index
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                var field = definition.GetField(fieldIndex);

                // Note: Conditional formatting API is very limited in Revit 2026
                // The TableCellStyleOverrideOptions class exists but has limited programmatic access
                // Value-based conditional formatting (e.g., "color cells where value > 100")
                // must be configured manually through the Revit UI

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    scheduleId = (int)scheduleId.Value,
                    fieldIndex = fieldIndex,
                    fieldHeading = field.ColumnHeading,
                    error = "Conditional formatting has limited API support in Revit 2026",
                    message = "Conditional formatting must be configured manually in the Revit UI (Schedule Properties > Formatting tab)",
                    note = "The Revit API does not expose methods to programmatically set conditional formatting rules in Revit 2026. This feature requires manual configuration.",
                    workaround = "You can access conditional formatting by: 1) Open schedule, 2) Click 'Formatting' tab in Schedule Properties, 3) Select field, 4) Add conditional format rules"
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
        /// Sets column width for a field
        /// </summary>
        public static string SetColumnWidth(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null || parameters["width"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId, fieldIndex, and width are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());
                var width = double.Parse(parameters["width"].ToString());

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate field index
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                // Validate width (must be positive)
                if (width <= 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Width must be greater than 0"
                    });
                }

                using (var trans = new Transaction(doc, "Set Column Width"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var field = definition.GetField(fieldIndex);
                    field.SheetColumnWidth = width;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        fieldIndex = fieldIndex,
                        fieldHeading = field.ColumnHeading,
                        width = field.SheetColumnWidth,
                        message = $"Column width set to {width} for field '{field.ColumnHeading}'"
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
        /// Sets text alignment for a field
        /// </summary>
        public static string SetFieldAlignment(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldIndex"] == null || parameters["alignment"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId, fieldIndex, and alignment are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldIndex = int.Parse(parameters["fieldIndex"].ToString());
                var alignmentStr = parameters["alignment"].ToString().ToLower();

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                var definition = schedule.Definition;

                // Validate field index
                if (fieldIndex < 0 || fieldIndex >= definition.GetFieldCount())
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid field index {fieldIndex}. Schedule has {definition.GetFieldCount()} fields."
                    });
                }

                // Parse alignment
                ScheduleHorizontalAlignment alignment;
                switch (alignmentStr)
                {
                    case "left":
                        alignment = ScheduleHorizontalAlignment.Left;
                        break;
                    case "center":
                        alignment = ScheduleHorizontalAlignment.Center;
                        break;
                    case "right":
                        alignment = ScheduleHorizontalAlignment.Right;
                        break;
                    default:
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Invalid alignment '{alignmentStr}'. Use: left, center, or right"
                        });
                }

                using (var trans = new Transaction(doc, "Set Field Alignment"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var field = definition.GetField(fieldIndex);
                    field.HorizontalAlignment = alignment;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        fieldIndex = fieldIndex,
                        fieldHeading = field.ColumnHeading,
                        alignment = field.HorizontalAlignment.ToString(),
                        message = $"Field '{field.ColumnHeading}' alignment set to {alignment}"
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

        #endregion

        #region Schedule Data Extraction

        /// <summary>
        /// Gets all data from a schedule as JSON
        /// </summary>
        public static string GetScheduleData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var includeHeaders = parameters["includeHeaders"] != null
                    ? bool.Parse(parameters["includeHeaders"].ToString())
                    : true;

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                // Get the table data
                var tableData = schedule.GetTableData();
                var sectionData = tableData.GetSectionData(SectionType.Body);

                int firstRow = sectionData.FirstRowNumber;
                int lastRow = sectionData.LastRowNumber;
                int firstCol = sectionData.FirstColumnNumber;
                int lastCol = sectionData.LastColumnNumber;

                var rows = new List<List<string>>();

                // Get headers if requested
                if (includeHeaders)
                {
                    var headerRow = new List<string>();
                    for (int col = firstCol; col <= lastCol; col++)
                    {
                        var cellText = schedule.GetCellText(SectionType.Header, sectionData.FirstRowNumber - 1, col);
                        headerRow.Add(cellText ?? "");
                    }
                    rows.Add(headerRow);
                }

                // Get body data
                for (int row = firstRow; row <= lastRow; row++)
                {
                    var dataRow = new List<string>();
                    for (int col = firstCol; col <= lastCol; col++)
                    {
                        var cellText = schedule.GetCellText(SectionType.Body, row, col);
                        dataRow.Add(cellText ?? "");
                    }
                    rows.Add(dataRow);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleName = schedule.Name,
                    scheduleId = (int)schedule.Id.Value,
                    rowCount = rows.Count,
                    columnCount = (lastCol - firstCol + 1),
                    data = rows,
                    message = $"Retrieved {rows.Count} rows from schedule '{schedule.Name}'"
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
        /// Exports schedule data to CSV format
        /// </summary>
        public static string ExportScheduleToCSV(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["filePath"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId and filePath are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var filePath = parameters["filePath"].ToString();

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                // Get the table data
                var tableData = schedule.GetTableData();
                var bodySection = tableData.GetSectionData(SectionType.Body);
                var headerSection = tableData.GetSectionData(SectionType.Header);

                int firstRow = bodySection.FirstRowNumber;
                int lastRow = bodySection.LastRowNumber;
                int firstCol = bodySection.FirstColumnNumber;
                int lastCol = bodySection.LastColumnNumber;

                using (var writer = new System.IO.StreamWriter(filePath))
                {
                    // Write headers from header section (use last header row for column names)
                    var headerCells = new List<string>();
                    int headerLastRow = headerSection.LastRowNumber;
                    for (int col = headerSection.FirstColumnNumber; col <= headerSection.LastColumnNumber; col++)
                    {
                        try
                        {
                            var cellText = schedule.GetCellText(SectionType.Header, headerLastRow, col);
                            var escapedText = EscapeCSVField(cellText ?? "");
                            headerCells.Add(escapedText);
                        }
                        catch
                        {
                            headerCells.Add("");
                        }
                    }
                    writer.WriteLine(string.Join(",", headerCells));

                    // Write body rows
                    for (int row = firstRow; row <= lastRow; row++)
                    {
                        var rowCells = new List<string>();
                        for (int col = firstCol; col <= lastCol; col++)
                        {
                            try
                            {
                                var cellText = schedule.GetCellText(SectionType.Body, row, col);
                                var escapedText = EscapeCSVField(cellText ?? "");
                                rowCells.Add(escapedText);
                            }
                            catch
                            {
                                rowCells.Add("");
                            }
                        }
                        writer.WriteLine(string.Join(",", rowCells));
                    }
                }

                var fileInfo = new System.IO.FileInfo(filePath);

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleName = schedule.Name,
                    scheduleId = (int)schedule.Id.Value,
                    filePath = filePath,
                    rowCount = (lastRow - firstRow + 1),
                    columnCount = (lastCol - firstCol + 1),
                    fileSize = fileInfo.Length,
                    message = $"Schedule '{schedule.Name}' exported to CSV: {filePath}"
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
        /// Helper method to escape CSV fields (handles commas, quotes, newlines)
        /// </summary>
        private static string EscapeCSVField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return field;

            // Check if field needs escaping (contains comma, quote, or newline)
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                // Escape quotes by doubling them
                field = field.Replace("\"", "\"\"");
                // Wrap in quotes
                field = "\"" + field + "\"";
            }

            return field;
        }

        /// <summary>
        /// Gets a specific cell value from a schedule
        /// </summary>
        public static string GetScheduleCellValue(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                if (parameters["rowIndex"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "rowIndex is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                var rowIndex = parameters["rowIndex"].ToObject<int>();
                var tableData = schedule.GetTableData();
                var sectionData = tableData.GetSectionData(SectionType.Body);

                // Validate row index
                var rowCount = sectionData.NumberOfRows;
                if (rowIndex < 0 || rowIndex >= rowCount)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid rowIndex: {rowIndex}. Must be between 0 and {rowCount - 1}"
                    });
                }

                // Determine column index
                int columnIndex = -1;
                if (parameters["columnIndex"] != null)
                {
                    columnIndex = parameters["columnIndex"].ToObject<int>();
                }
                else if (parameters["fieldName"] != null)
                {
                    // Find field by name
                    string fieldName = parameters["fieldName"].ToString();
                    var definition = schedule.Definition;

                    for (int i = 0; i < definition.GetFieldCount(); i++)
                    {
                        var field = definition.GetField(i);
                        if (field.GetName().Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                        {
                            columnIndex = i;
                            break;
                        }
                    }

                    if (columnIndex == -1)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Field '{fieldName}' not found in schedule"
                        });
                    }
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either columnIndex or fieldName is required"
                    });
                }

                // Validate column index
                var columnCount = sectionData.NumberOfColumns;
                if (columnIndex < 0 || columnIndex >= columnCount)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Invalid columnIndex: {columnIndex}. Must be between 0 and {columnCount - 1}"
                    });
                }

                // Get cell value
                var cellValue = schedule.GetCellText(SectionType.Body, rowIndex, columnIndex);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = scheduleIdInt,
                    scheduleName = schedule.Name,
                    rowIndex,
                    columnIndex,
                    value = cellValue ?? ""
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets schedule totals and calculations
        /// </summary>
        public static string GetScheduleTotals(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                var definition = schedule.Definition;
                var tableData = schedule.GetTableData();
                var sectionData = tableData.GetSectionData(SectionType.Body);

                // Collect totals information from fields
                var fieldTotals = new System.Collections.Generic.List<object>();

                for (int i = 0; i < definition.GetFieldCount(); i++)
                {
                    var field = definition.GetField(i);

                    // Check if field has totals enabled
                    var fieldTotalInfo = new
                    {
                        fieldIndex = i,
                        fieldName = field.GetName(),
                        displayType = field.DisplayType.ToString(),
                        // Note: Revit API doesn't directly expose calculated total values
                        // Totals are displayed in the UI but not accessible via API
                        note = "Total values are calculated by Revit UI but not exposed through API"
                    };

                    fieldTotals.Add(fieldTotalInfo);
                }

                // Try to read totals from footer section if available
                var footerTotals = new System.Collections.Generic.List<object>();
                try
                {
                    var footerData = tableData.GetSectionData(SectionType.Footer);
                    var footerRows = footerData.NumberOfRows;
                    var footerCols = footerData.NumberOfColumns;

                    for (int row = 0; row < footerRows; row++)
                    {
                        var rowValues = new System.Collections.Generic.List<string>();
                        for (int col = 0; col < footerCols; col++)
                        {
                            var cellText = schedule.GetCellText(SectionType.Footer, row, col);
                            rowValues.Add(cellText ?? "");
                        }

                        footerTotals.Add(new
                        {
                            rowIndex = row,
                            values = rowValues
                        });
                    }
                }
                catch
                {
                    // Footer section may not be accessible
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = scheduleIdInt,
                    scheduleName = schedule.Name,
                    fieldsWithTotals = fieldTotals,
                    footerTotals = footerTotals,
                    apiLimitation = "Revit API does not expose calculated total values. Use GetScheduleData to retrieve all cell values including totals.",
                    note = "To get actual total values, parse the schedule data from GetScheduleData method"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Schedule Modification

        /// <summary>
        /// Modifies schedule properties
        /// </summary>
        public static string ModifyScheduleProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                var modifiedProperties = new System.Collections.Generic.List<string>();

                using (var trans = new Transaction(doc, "Modify Schedule Properties"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Modify name
                    if (parameters["name"] != null)
                    {
                        string newName = parameters["name"].ToString();
                        schedule.Name = newName;
                        modifiedProperties.Add($"name -> {newName}");
                    }

                    var definition = schedule.Definition;

                    // Modify itemize every instance
                    if (parameters["itemizeEveryInstance"] != null)
                    {
                        bool itemize = parameters["itemizeEveryInstance"].ToObject<bool>();
                        definition.IsItemized = itemize;
                        modifiedProperties.Add($"itemizeEveryInstance -> {itemize}");
                    }

                    // Modify show title
                    if (parameters["showTitle"] != null)
                    {
                        bool showTitle = parameters["showTitle"].ToObject<bool>();
                        definition.ShowTitle = showTitle;
                        modifiedProperties.Add($"showTitle -> {showTitle}");
                    }

                    // Modify show headers
                    if (parameters["showHeaders"] != null)
                    {
                        bool showHeaders = parameters["showHeaders"].ToObject<bool>();
                        definition.ShowHeaders = showHeaders;
                        modifiedProperties.Add($"showHeaders -> {showHeaders}");
                    }

                    // Modify show grand totals
                    if (parameters["showGrandTotal"] != null)
                    {
                        bool showGrandTotal = parameters["showGrandTotal"].ToObject<bool>();
                        definition.ShowGrandTotal = showGrandTotal;
                        modifiedProperties.Add($"showGrandTotal -> {showGrandTotal}");
                    }

                    // Modify grand total title
                    if (parameters["grandTotalTitle"] != null)
                    {
                        string title = parameters["grandTotalTitle"].ToString();
                        definition.GrandTotalTitle = title;
                        modifiedProperties.Add($"grandTotalTitle -> {title}");
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = scheduleIdInt,
                        scheduleName = schedule.Name,
                        modifiedProperties,
                        modifiedCount = modifiedProperties.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicates an existing schedule
        /// </summary>
        public static string DuplicateSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var newName = parameters["newName"]?.ToString();

                // Get the original schedule
                var originalSchedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (originalSchedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Duplicate Schedule"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Duplicate the schedule using ViewSchedule.Duplicate
                    var newScheduleId = originalSchedule.Duplicate(ViewDuplicateOption.Duplicate);
                    var newSchedule = doc.GetElement(newScheduleId) as ViewSchedule;

                    // Set the new name if provided
                    if (!string.IsNullOrEmpty(newName))
                    {
                        newSchedule.Name = newName;
                    }
                    else
                    {
                        // Revit automatically adds "Copy of" prefix
                        newName = newSchedule.Name;
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalScheduleId = (int)scheduleId.Value,
                        originalScheduleName = originalSchedule.Name,
                        newScheduleId = (int)newScheduleId.Value,
                        newScheduleName = newName,
                        fieldCount = newSchedule.Definition.GetFieldCount(),
                        filterCount = newSchedule.Definition.GetFilterCount(),
                        message = $"Schedule duplicated successfully as '{newName}'"
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

        #endregion

        #region Calculated Values

        /// <summary>
        /// Adds a calculated value field to a schedule
        /// </summary>
        public static string AddCalculatedField(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["scheduleId"] == null || parameters["fieldName"] == null || parameters["formula"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId, fieldName, and formula are required"
                    });
                }

                var scheduleId = new ElementId(int.Parse(parameters["scheduleId"].ToString()));
                var fieldName = parameters["fieldName"].ToString();
                var formula = parameters["formula"].ToString();

                // Get the schedule
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Schedule with ID {scheduleId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Add Calculated Field"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var definition = schedule.Definition;

                    // Get available schedulable fields to find formula type
                    var schedulableFields = definition.GetSchedulableFields();
                    SchedulableField formulaField = null;

                    foreach (var field in schedulableFields)
                    {
                        if (field.FieldType == ScheduleFieldType.Formula)
                        {
                            formulaField = field;
                            break;
                        }
                    }

                    if (formulaField == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Formula fields are not supported for this schedule type. Note: Calculated fields have limited API support in Revit 2026."
                        });
                    }

                    // Add the formula field
                    var scheduleField = definition.AddField(formulaField);
                    scheduleField.ColumnHeading = fieldName;

                    // Note: Setting formula via API may have limitations in Revit 2026
                    // The formula might need to be set manually in Revit UI after creation

                    var fieldIndex = scheduleField.FieldIndex;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = (int)scheduleId.Value,
                        fieldName = fieldName,
                        requestedFormula = formula,
                        fieldIndex = fieldIndex,
                        message = $"Calculated field '{fieldName}' added. Note: Formula '{formula}' may need to be set manually in Revit UI due to API limitations.",
                        warning = "Calculated field formulas have limited API support in Revit 2026"
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
        /// Modifies a calculated field formula
        /// </summary>
        public static string ModifyCalculatedField(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                if (parameters["fieldIndex"] == null && parameters["fieldName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either fieldIndex or fieldName is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                var definition = schedule.Definition;

                // Find the field
                ScheduleField targetField = null;
                int fieldIndex = -1;

                if (parameters["fieldIndex"] != null)
                {
                    fieldIndex = parameters["fieldIndex"].ToObject<int>();
                    if (fieldIndex >= 0 && fieldIndex < definition.GetFieldCount())
                    {
                        targetField = definition.GetField(fieldIndex);
                    }
                }
                else
                {
                    string fieldName = parameters["fieldName"].ToString();
                    for (int i = 0; i < definition.GetFieldCount(); i++)
                    {
                        var field = definition.GetField(i);
                        if (field.GetName().Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetField = field;
                            fieldIndex = i;
                            break;
                        }
                    }
                }

                if (targetField == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Field not found"
                    });
                }

                // Check if field is a calculated field
                if (!targetField.IsCalculatedField)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Field is not a calculated field"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API Limitation: Revit 2026 API does not support modifying calculated field formulas programmatically",
                    note = "Calculated field formulas can only be modified through the Revit UI. Use RemoveScheduleField and AddCalculatedField as a workaround.",
                    fieldIndex,
                    fieldName = targetField.GetName(),
                    isCalculated = targetField.IsCalculatedField,
                    workaround = "1. Remove the existing calculated field using RemoveScheduleField. 2. Add a new calculated field with the new formula using AddCalculatedField."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Schedule Lists

        /// <summary>
        /// Gets all schedules in the project
        /// </summary>
        public static string GetAllSchedules(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Optional filter by category name
                var categoryFilter = parameters?["category"]?.ToString();

                // Collect all ViewSchedule elements using safe casting
                var allSchedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .ToElements()
                    .OfType<ViewSchedule>()
                    .Where(s => s != null);

                var schedules = new System.Collections.Generic.List<object>();

                foreach (var schedule in allSchedules)
                {
                    try
                    {
                        // Skip templates
                        if (schedule.IsTemplate)
                            continue;

                        var definition = schedule.Definition;
                        if (definition == null)
                            continue;

                        var categoryId = definition.CategoryId;

                        // Get category name
                        string categoryName = "Unknown";
                        if (categoryId != ElementId.InvalidElementId)
                        {
                            var category = Category.GetCategory(doc, categoryId);
                            if (category != null)
                            {
                                categoryName = category.Name;
                            }
                        }

                        // Apply category filter if specified
                        if (!string.IsNullOrEmpty(categoryFilter) &&
                            !categoryName.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        schedules.Add(new
                        {
                            scheduleId = (int)schedule.Id.Value,
                            scheduleName = schedule.Name ?? "",
                            category = categoryName,
                            fieldCount = definition.GetFieldCount(),
                            filterCount = definition.GetFilterCount(),
                            isKeySchedule = definition.IsKeySchedule,
                            isMaterialTakeoff = definition.IsMaterialTakeoff
                        });
                    }
                    catch { continue; }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleCount = schedules.Count,
                    schedules = schedules,
                    message = string.IsNullOrEmpty(categoryFilter)
                        ? $"Found {schedules.Count} schedules in project"
                        : $"Found {schedules.Count} schedules for category '{categoryFilter}'"
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
        /// Gets schedule information
        /// </summary>
        public static string GetScheduleInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                var definition = schedule.Definition;
                var tableData = schedule.GetTableData();
                var sectionData = tableData.GetSectionData(SectionType.Body);

                // Get category information
                string categoryName = "Unknown";
                int categoryId = -1;
                if (definition.CategoryId != ElementId.InvalidElementId)
                {
                    var category = Category.GetCategory(doc, definition.CategoryId);
                    if (category != null)
                    {
                        categoryName = category.Name;
                        categoryId = (int)category.Id.Value;
                    }
                }

                // Get field count and details
                var fieldCount = definition.GetFieldCount();
                var fields = new System.Collections.Generic.List<object>();
                for (int i = 0; i < fieldCount; i++)
                {
                    var field = definition.GetField(i);
                    fields.Add(new
                    {
                        index = i,
                        name = field.GetName(),
                        isCalculated = field.IsCalculatedField,
                        isHidden = field.IsHidden,
                        parameterId = field.ParameterId != null ? (int)field.ParameterId.Value : -1
                    });
                }

                // Get filter count
                var filterCount = definition.GetFilterCount();

                // Get sorting/grouping count
                var sortingCount = definition.GetSortGroupFieldCount();

                // Get row and column counts
                var rowCount = sectionData.NumberOfRows;
                var columnCount = sectionData.NumberOfColumns;

                // Get schedule properties
                var scheduleInfo = new
                {
                    success = true,
                    scheduleId = scheduleIdInt,
                    name = schedule.Name,
                    category = categoryName,
                    categoryId,
                    isTemplate = schedule.IsTemplate,
                    isKeySchedule = definition.IsKeySchedule,
                    isItemized = definition.IsItemized,
                    showTitle = definition.ShowTitle,
                    showHeaders = definition.ShowHeaders,
                    showGrandTotal = definition.ShowGrandTotal,
                    grandTotalTitle = definition.GrandTotalTitle,
                    fieldCount,
                    fields,
                    filterCount,
                    sortGroupFieldCount = sortingCount,
                    rowCount,
                    columnCount
                };

                return JsonConvert.SerializeObject(scheduleInfo);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Deletes a schedule
        /// </summary>
        public static string DeleteSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                string scheduleName = schedule.Name;

                using (var trans = new Transaction(doc, "Delete Schedule"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete the schedule
                    doc.Delete(scheduleId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        scheduleId = scheduleIdInt,
                        scheduleName,
                        message = $"Schedule '{scheduleName}' deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Refreshes schedule data
        /// </summary>
        public static string RefreshSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["scheduleId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "scheduleId is required"
                    });
                }

                var scheduleIdInt = parameters["scheduleId"].ToObject<int>();
                var scheduleId = new ElementId(scheduleIdInt);
                var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                if (schedule == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule not found"
                    });
                }

                // Note: Revit schedules automatically update when element data changes
                // There's no explicit Refresh() method in the API
                // However, we can force a refresh by regenerating the document
                using (var trans = new Transaction(doc, "Refresh Schedule"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Force document regeneration which will refresh schedules
                    doc.Regenerate();

                    trans.Commit();
                }

                // Get updated row count
                var tableData = schedule.GetTableData();
                var sectionData = tableData.GetSectionData(SectionType.Body);
                var rowCount = sectionData.NumberOfRows;
                var columnCount = sectionData.NumberOfColumns;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    scheduleId = scheduleIdInt,
                    scheduleName = schedule.Name,
                    rowCount,
                    columnCount,
                    note = "Revit schedules automatically update when element data changes. Document regeneration was triggered to ensure latest data.",
                    message = $"Schedule '{schedule.Name}' refreshed successfully"
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
