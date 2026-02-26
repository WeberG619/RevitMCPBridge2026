using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Revit Filters
    /// Handles view filters, parameter filters, selection filters, and filter rules
    /// </summary>
    public static class FilterMethods
    {
        #region View Filters

        /// <summary>
        /// Creates a new view filter (parameter filter)
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing filterName, categories, rules</param>
        /// <returns>JSON response with success status and filter ID</returns>
        [MCPMethod("createViewFilter", Category = "Filter", Description = "Creates a new view filter (parameter filter)")]
        public static string CreateViewFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["filterName"] == null || parameters["categories"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterName and categories are required"
                    });
                }

                string filterName = parameters["filterName"].ToString();
                var categoryIdsInt = parameters["categories"].ToObject<List<int>>();

                // Convert category IDs
                ICollection<ElementId> categoryIds = new List<ElementId>();
                foreach (int catId in categoryIdsInt)
                {
                    categoryIds.Add(new ElementId(catId));
                }

                using (var trans = new Transaction(doc, "Create View Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create parameter filter element
                    // Note: If no rules provided, create with empty rules (can be added later)
                    ParameterFilterElement filter = ParameterFilterElement.Create(doc, filterName, categoryIds);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = (int)filter.Id.Value,
                        filterName = filter.Name,
                        categoriesCount = categoryIds.Count,
                        message = "View filter created successfully. Use AddRuleToFilter to add filter rules."
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
        /// Gets all view filters in the project
        /// </summary>
        [MCPMethod("getAllViewFilters", Category = "Filter", Description = "Gets all view filters in the project")]
        public static string GetAllViewFilters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var filters = new List<object>();

                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement));

                foreach (ParameterFilterElement filter in collector)
                {
                    ICollection<ElementId> categories = filter.GetCategories();
                    bool hasRules = false;

                    try
                    {
                        // Check if filter has rules
                        var filterRules = filter.GetElementFilter();
                        hasRules = (filterRules != null);
                    }
                    catch
                    {
                        hasRules = false;
                    }

                    filters.Add(new
                    {
                        filterId = (int)filter.Id.Value,
                        filterName = filter.Name,
                        categoriesCount = categories.Count,
                        categoryIds = categories.Select(id => (int)id.Value).ToList(),
                        hasRules = hasRules
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filtersCount = filters.Count,
                    filters
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
        /// Gets information about a specific view filter
        /// </summary>
        [MCPMethod("getViewFilterInfo", Category = "Filter", Description = "Gets information about a specific view filter")]
        public static string GetViewFilterInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                ICollection<ElementId> categories = filter.GetCategories();

                // Get filter rules information
                bool hasRules = false;
                string filterType = "None";
                try
                {
                    var elementFilter = filter.GetElementFilter();
                    if (elementFilter != null)
                    {
                        hasRules = true;
                        filterType = elementFilter.GetType().Name;
                    }
                }
                catch
                {
                    hasRules = false;
                    filterType = "None";
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    categories = categories.Select(id => (int)id.Value).ToList(),
                    categoriesCount = categories.Count,
                    hasRules = hasRules,
                    filterType = filterType
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
        /// Modifies a view filter
        /// </summary>
        [MCPMethod("modifyViewFilter", Category = "Filter", Description = "Modifies an existing view filter")]
        public static string ModifyViewFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Modify View Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Modify filter name if provided
                    if (parameters["filterName"] != null)
                    {
                        string newName = parameters["filterName"].ToString();
                        filter.Name = newName;
                    }

                    // Modify categories if provided
                    if (parameters["categories"] != null)
                    {
                        var categoryIdsInt = parameters["categories"].ToObject<List<int>>();
                        ICollection<ElementId> categoryIds = new List<ElementId>();
                        foreach (int catId in categoryIdsInt)
                        {
                            categoryIds.Add(new ElementId(catId));
                        }
                        filter.SetCategories(categoryIds);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        message = "Filter modified successfully"
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
        /// Deletes a view filter
        /// </summary>
        [MCPMethod("deleteViewFilter", Category = "Filter", Description = "Deletes a view filter")]
        public static string DeleteViewFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                string filterName = filter.Name;

                using (var trans = new Transaction(doc, "Delete View Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ICollection<ElementId> deletedIds = doc.Delete(filterId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedFilterId = filterIdInt,
                        deletedFilterName = filterName,
                        deletedCount = deletedIds.Count,
                        message = "Filter deleted successfully"
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

        #region Filter Rules

        /// <summary>
        /// Creates a filter rule for parameter-based filtering
        /// </summary>
        [MCPMethod("createFilterRule", Category = "Filter", Description = "Creates a filter rule for parameter-based filtering")]
        public static string CreateFilterRule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["parameterId"] == null || parameters["ruleType"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterId and ruleType are required"
                    });
                }

                int paramIdInt = parameters["parameterId"].ToObject<int>();
                ElementId parameterId = new ElementId(paramIdInt);
                string ruleType = parameters["ruleType"].ToString().ToLower();

                // API Limitation: Filter rules cannot be created standalone in Revit API
                // They must be created and immediately applied to a filter using ElementParameterFilter
                // This method returns a rule specification that can be used with AddRuleToFilter

                var ruleSpec = new
                {
                    success = true,
                    parameterId = paramIdInt,
                    ruleType = ruleType,
                    value = parameters["value"]?.ToString(),
                    message = "Rule specification created. Use AddRuleToFilter to apply this rule to a filter.",
                    note = "Filter rules in Revit API must be created inline with filters - use AddRuleToFilter for actual implementation"
                };

                return Newtonsoft.Json.JsonConvert.SerializeObject(ruleSpec);
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
        /// Adds a rule to an existing view filter
        /// </summary>
        [MCPMethod("addRuleToFilter", Category = "Filter", Description = "Adds a rule to an existing view filter")]
        public static string AddRuleToFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null || parameters["parameterId"] == null ||
                    parameters["ruleType"] == null || parameters["value"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId, parameterId, ruleType, and value are required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                int paramIdInt = parameters["parameterId"].ToObject<int>();
                ElementId parameterId = new ElementId(paramIdInt);

                string ruleType = parameters["ruleType"].ToString().ToLower();
                string value = parameters["value"].ToString();

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Add Rule to Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create filter rule based on type
                    FilterRule rule = null;

                    switch (ruleType)
                    {
                        case "equals":
                            rule = ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value);
                            break;
                        case "notequals":
                            rule = ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, value);
                            break;
                        case "contains":
                            rule = ParameterFilterRuleFactory.CreateContainsRule(parameterId, value);
                            break;
                        case "beginswith":
                            rule = ParameterFilterRuleFactory.CreateBeginsWithRule(parameterId, value);
                            break;
                        case "endswith":
                            rule = ParameterFilterRuleFactory.CreateEndsWithRule(parameterId, value);
                            break;
                        case "greater":
                            double greaterVal = double.Parse(value);
                            rule = ParameterFilterRuleFactory.CreateGreaterRule(parameterId, greaterVal, 1e-6);
                            break;
                        case "greaterorequal":
                            double greaterEqVal = double.Parse(value);
                            rule = ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, greaterEqVal, 1e-6);
                            break;
                        case "less":
                            double lessVal = double.Parse(value);
                            rule = ParameterFilterRuleFactory.CreateLessRule(parameterId, lessVal, 1e-6);
                            break;
                        case "lessorequal":
                            double lessEqVal = double.Parse(value);
                            rule = ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, lessEqVal, 1e-6);
                            break;
                        default:
                            trans.RollBack();
                            return Newtonsoft.Json.JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Unknown rule type: {ruleType}. Supported: equals, notequals, contains, beginswith, endswith, greater, greaterorequal, less, lessorequal"
                            });
                    }

                    // Get existing rules and add new one
                    var existingFilter = filter.GetElementFilter() as ElementParameterFilter;
                    IList<FilterRule> rules = new List<FilterRule>();

                    if (existingFilter != null)
                    {
                        var existingRules = existingFilter.GetRules();
                        foreach (var r in existingRules)
                        {
                            rules.Add(r);
                        }
                    }

                    rules.Add(rule);

                    // Create new ElementParameterFilter with all rules
                    var newFilter = new ElementParameterFilter(rules);
                    filter.SetElementFilter(newFilter);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        parameterId = paramIdInt,
                        ruleType = ruleType,
                        value = value,
                        totalRules = rules.Count,
                        message = "Rule added to filter successfully"
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
        /// Gets all rules for a view filter
        /// </summary>
        [MCPMethod("getFilterRules", Category = "Filter", Description = "Gets all rules for a view filter")]
        public static string GetFilterRules(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                var elementFilter = filter.GetElementFilter() as ElementParameterFilter;
                if (elementFilter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        rulesCount = 0,
                        rules = new List<object>(),
                        message = "Filter has no rules"
                    });
                }

                var rules = new List<object>();
                var filterRules = elementFilter.GetRules();

                foreach (var rule in filterRules)
                {
                    var ruleInfo = new
                    {
                        parameterId = (int)rule.GetRuleParameter().Value,
                        ruleType = rule.GetType().Name
                    };

                    rules.Add(ruleInfo);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    rulesCount = rules.Count,
                    rules = rules
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
        /// Removes a rule from a view filter
        /// </summary>
        [MCPMethod("removeRuleFromFilter", Category = "Filter", Description = "Removes a rule from a view filter")]
        public static string RemoveRuleFromFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null || parameters["ruleIndex"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId and ruleIndex are required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);
                int ruleIndex = parameters["ruleIndex"].ToObject<int>();

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Remove Rule from Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var elementFilter = filter.GetElementFilter() as ElementParameterFilter;
                    if (elementFilter == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Filter has no rules to remove"
                        });
                    }

                    var existingRules = elementFilter.GetRules();
                    if (ruleIndex < 0 || ruleIndex >= existingRules.Count)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Rule index {ruleIndex} out of range. Filter has {existingRules.Count} rules (0-{existingRules.Count - 1})"
                        });
                    }

                    // Create new list without the specified rule
                    IList<FilterRule> newRules = new List<FilterRule>();
                    for (int i = 0; i < existingRules.Count; i++)
                    {
                        if (i != ruleIndex)
                        {
                            newRules.Add(existingRules[i]);
                        }
                    }

                    // Set new filter with remaining rules (or clear if no rules left)
                    if (newRules.Count > 0)
                    {
                        var newFilter = new ElementParameterFilter(newRules);
                        filter.SetElementFilter(newFilter);
                    }
                    else
                    {
                        filter.ClearRules();
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        removedRuleIndex = ruleIndex,
                        remainingRules = newRules.Count,
                        message = newRules.Count > 0
                            ? $"Rule at index {ruleIndex} removed. {newRules.Count} rules remaining."
                            : "Rule removed. Filter now has no rules."
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

        #region Applying Filters to Views

        /// <summary>
        /// Applies a view filter to a view with graphics overrides
        /// </summary>
        [MCPMethod("applyFilterToView", Category = "Filter", Description = "Applies a view filter to a view with graphics overrides")]
        public static string ApplyFilterToView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and filterId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Apply Filter to View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Add filter to view
                    view.AddFilter(filterId);

                    // Set visibility if provided (default is visible)
                    bool visible = parameters["visible"]?.ToObject<bool>() ?? true;
                    view.SetFilterVisibility(filterId, visible);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        visible = visible,
                        message = "Filter applied to view successfully. Use SetFilterOverrides to set graphics overrides."
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
        /// Removes a filter from a view
        /// </summary>
        [MCPMethod("removeFilterFromView", Category = "Filter", Description = "Removes a filter from a view")]
        public static string RemoveFilterFromView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and filterId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Remove Filter from View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    view.RemoveFilter(filterId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        filterId = filterIdInt,
                        message = "Filter removed from view successfully"
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
        /// Gets all filters applied to a view
        /// </summary>
        [MCPMethod("getFiltersInView", Category = "Filter", Description = "Gets all filters applied to a view")]
        public static string GetFiltersInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                var filters = new List<object>();
                ICollection<ElementId> filterIds = view.GetFilters();

                foreach (ElementId filterId in filterIds)
                {
                    ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                    if (filter != null)
                    {
                        bool isVisible = view.GetFilterVisibility(filterId);

                        filters.Add(new
                        {
                            filterId = (int)filterId.Value,
                            filterName = filter.Name,
                            visible = isVisible
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    filtersCount = filters.Count,
                    filters
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
        /// Sets graphics overrides for a filter in a view
        /// </summary>
        [MCPMethod("setFilterOverrides", Category = "Filter", Description = "Sets graphics overrides for a filter in a view")]
        public static string SetFilterOverrides(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and filterId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set Filter Overrides"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    OverrideGraphicSettings overrides = new OverrideGraphicSettings();

                    // Set line color if provided
                    if (parameters["lineColor"] != null)
                    {
                        var colorData = parameters["lineColor"];
                        Color lineColor = new Color(
                            colorData["r"].ToObject<byte>(),
                            colorData["g"].ToObject<byte>(),
                            colorData["b"].ToObject<byte>()
                        );
                        overrides.SetProjectionLineColor(lineColor);
                        overrides.SetCutLineColor(lineColor);
                    }

                    // Set line weight if provided
                    if (parameters["lineWeight"] != null)
                    {
                        int lineWeight = parameters["lineWeight"].ToObject<int>();
                        overrides.SetProjectionLineWeight(lineWeight);
                        overrides.SetCutLineWeight(lineWeight);
                    }

                    // Set transparency if provided
                    if (parameters["transparency"] != null)
                    {
                        int transparency = parameters["transparency"].ToObject<int>();
                        overrides.SetSurfaceTransparency(transparency);
                    }

                    // Set halftone if provided
                    if (parameters["halftone"] != null)
                    {
                        bool halftone = parameters["halftone"].ToObject<bool>();
                        overrides.SetHalftone(halftone);
                    }

                    view.SetFilterOverrides(filterId, overrides);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        filterId = filterIdInt,
                        message = "Filter overrides set successfully"
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
        /// Gets graphics overrides for a filter in a view
        /// </summary>
        [MCPMethod("getFilterOverrides", Category = "Filter", Description = "Gets graphics overrides for a filter in a view")]
        public static string GetFilterOverrides(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["viewId"] == null || parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and filterId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                ElementId viewId = new ElementId(viewIdInt);

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get view and filter
                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                // Get overrides
                OverrideGraphicSettings overrides = view.GetFilterOverrides(filterId);

                // Extract override settings
                var result = new
                {
                    success = true,
                    viewId = viewIdInt,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    overrides = new
                    {
                        hasProjectionLineColor = overrides.ProjectionLineColor.IsValid,
                        projectionLineColor = overrides.ProjectionLineColor.IsValid ? new
                        {
                            r = overrides.ProjectionLineColor.Red,
                            g = overrides.ProjectionLineColor.Green,
                            b = overrides.ProjectionLineColor.Blue
                        } : null,
                        hasCutLineColor = overrides.CutLineColor.IsValid,
                        cutLineColor = overrides.CutLineColor.IsValid ? new
                        {
                            r = overrides.CutLineColor.Red,
                            g = overrides.CutLineColor.Green,
                            b = overrides.CutLineColor.Blue
                        } : null,
                        projectionLineWeight = overrides.ProjectionLineWeight,
                        cutLineWeight = overrides.CutLineWeight,
                        transparency = overrides.Transparency,
                        halftone = overrides.Halftone
                    }
                };

                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
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

        #region Selection Filters

        /// <summary>
        /// Creates a selection filter to select elements by criteria
        /// </summary>
        [MCPMethod("selectElementsByFilter", Category = "Filter", Description = "Selects elements matching filter criteria")]
        public static string SelectElementsByFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["categoryId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categoryId is required"
                    });
                }

                int categoryIdInt = parameters["categoryId"].ToObject<int>();
                ElementId categoryId = new ElementId(categoryIdInt);

                // Start with category filter
                FilteredElementCollector collector = new FilteredElementCollector(doc);

                // If viewId is provided, collect from view
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    ElementId viewId = new ElementId(viewIdInt);
                    View view = doc.GetElement(viewId) as View;

                    if (view == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View with ID {viewIdInt} not found"
                        });
                    }

                    collector = new FilteredElementCollector(doc, viewId);
                }

                collector.OfCategoryId(categoryId).WhereElementIsNotElementType();

                // Apply parameter filters if provided
                if (parameters["parameterFilters"] != null)
                {
                    var paramFilters = parameters["parameterFilters"].ToObject<List<JObject>>();
                    IList<FilterRule> rules = new List<FilterRule>();

                    foreach (var filter in paramFilters)
                    {
                        if (filter["parameterId"] == null || filter["ruleType"] == null || filter["value"] == null)
                        {
                            continue; // Skip invalid filters
                        }

                        int paramIdInt = filter["parameterId"].ToObject<int>();
                        ElementId parameterId = new ElementId(paramIdInt);
                        string ruleType = filter["ruleType"].ToString().ToLower();
                        string value = filter["value"].ToString();

                        FilterRule rule = null;

                        switch (ruleType)
                        {
                            case "equals":
                                rule = ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value);
                                break;
                            case "notequals":
                                rule = ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, value);
                                break;
                            case "contains":
                                rule = ParameterFilterRuleFactory.CreateContainsRule(parameterId, value);
                                break;
                            case "beginswith":
                                rule = ParameterFilterRuleFactory.CreateBeginsWithRule(parameterId, value);
                                break;
                            case "endswith":
                                rule = ParameterFilterRuleFactory.CreateEndsWithRule(parameterId, value);
                                break;
                            case "greater":
                                double greaterVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateGreaterRule(parameterId, greaterVal, 1e-6);
                                break;
                            case "greaterorequal":
                                double greaterEqVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, greaterEqVal, 1e-6);
                                break;
                            case "less":
                                double lessVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateLessRule(parameterId, lessVal, 1e-6);
                                break;
                            case "lessorequal":
                                double lessEqVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, lessEqVal, 1e-6);
                                break;
                        }

                        if (rule != null)
                        {
                            rules.Add(rule);
                        }
                    }

                    if (rules.Count > 0)
                    {
                        var elementFilter = new ElementParameterFilter(rules);
                        collector = collector.WherePasses(elementFilter);
                    }
                }

                // Collect element IDs
                var elementIds = collector.ToElementIds();
                var elementIdsList = elementIds.Select(id => (int)id.Value).ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    categoryId = categoryIdInt,
                    viewId = parameters["viewId"] != null ? parameters["viewId"].ToObject<int>() : (int?)null,
                    elementsCount = elementIdsList.Count,
                    elementIds = elementIdsList,
                    filtersApplied = parameters["parameterFilters"]?.ToObject<List<JObject>>()?.Count ?? 0
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
        /// Counts elements matching filter criteria
        /// </summary>
        [MCPMethod("countElementsByFilter", Category = "Filter", Description = "Counts elements matching filter criteria")]
        public static string CountElementsByFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["categoryId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categoryId is required"
                    });
                }

                int categoryIdInt = parameters["categoryId"].ToObject<int>();
                ElementId categoryId = new ElementId(categoryIdInt);

                // Start with category filter
                FilteredElementCollector collector = new FilteredElementCollector(doc);

                // If viewId is provided, collect from view
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    ElementId viewId = new ElementId(viewIdInt);
                    View view = doc.GetElement(viewId) as View;

                    if (view == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View with ID {viewIdInt} not found"
                        });
                    }

                    collector = new FilteredElementCollector(doc, viewId);
                }

                collector.OfCategoryId(categoryId).WhereElementIsNotElementType();

                // Apply parameter filters if provided
                if (parameters["parameterFilters"] != null)
                {
                    var paramFilters = parameters["parameterFilters"].ToObject<List<JObject>>();
                    IList<FilterRule> rules = new List<FilterRule>();

                    foreach (var filter in paramFilters)
                    {
                        if (filter["parameterId"] == null || filter["ruleType"] == null || filter["value"] == null)
                        {
                            continue;
                        }

                        int paramIdInt = filter["parameterId"].ToObject<int>();
                        ElementId parameterId = new ElementId(paramIdInt);
                        string ruleType = filter["ruleType"].ToString().ToLower();
                        string value = filter["value"].ToString();

                        FilterRule rule = null;

                        switch (ruleType)
                        {
                            case "equals":
                                rule = ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value);
                                break;
                            case "notequals":
                                rule = ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, value);
                                break;
                            case "contains":
                                rule = ParameterFilterRuleFactory.CreateContainsRule(parameterId, value);
                                break;
                            case "beginswith":
                                rule = ParameterFilterRuleFactory.CreateBeginsWithRule(parameterId, value);
                                break;
                            case "endswith":
                                rule = ParameterFilterRuleFactory.CreateEndsWithRule(parameterId, value);
                                break;
                            case "greater":
                                double greaterVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateGreaterRule(parameterId, greaterVal, 1e-6);
                                break;
                            case "greaterorequal":
                                double greaterEqVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, greaterEqVal, 1e-6);
                                break;
                            case "less":
                                double lessVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateLessRule(parameterId, lessVal, 1e-6);
                                break;
                            case "lessorequal":
                                double lessEqVal = double.Parse(value);
                                rule = ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, lessEqVal, 1e-6);
                                break;
                        }

                        if (rule != null)
                        {
                            rules.Add(rule);
                        }
                    }

                    if (rules.Count > 0)
                    {
                        var elementFilter = new ElementParameterFilter(rules);
                        collector = collector.WherePasses(elementFilter);
                    }
                }

                // Count elements
                int count = collector.GetElementCount();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    categoryId = categoryIdInt,
                    viewId = parameters["viewId"] != null ? parameters["viewId"].ToObject<int>() : (int?)null,
                    count = count,
                    filtersApplied = parameters["parameterFilters"]?.ToObject<List<JObject>>()?.Count ?? 0
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

        #region Category Filters

        /// <summary>
        /// Creates a category filter for element collection
        /// </summary>
        [MCPMethod("createCategoryFilter", Category = "Filter", Description = "Creates a category filter for element collection")]
        public static string CreateCategoryFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API Limitation: ElementMulticategoryFilter and ElementCategoryFilter are used with
                // FilteredElementCollector, not stored as ParameterFilterElement
                // This method returns a category filter specification for use with collectors

                if (parameters["categories"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categories array is required"
                    });
                }

                var categoriesInt = parameters["categories"].ToObject<List<int>>();
                bool inverted = parameters["inverted"]?.ToObject<bool>() ?? false;

                var categoryIds = categoriesInt.Select(id => new ElementId(id)).ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    categoryCount = categoryIds.Count,
                    categories = categoriesInt,
                    inverted = inverted,
                    message = "Category filter specification created. Use with FilteredElementCollector.OfCategoryId() or OfCategory()",
                    note = "ElementCategoryFilter and ElementMulticategoryFilter are used inline with collectors, not stored as filter elements"
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
        /// Gets all categories in a filter
        /// </summary>
        [MCPMethod("getFilterCategories", Category = "Filter", Description = "Gets all categories assigned to a filter")]
        public static string GetFilterCategories(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                var categories = filter.GetCategories();
                var categoriesList = new List<object>();

                foreach (ElementId catId in categories)
                {
                    Category category = Category.GetCategory(doc, catId);
                    categoriesList.Add(new
                    {
                        categoryId = (int)catId.Value,
                        categoryName = category != null ? category.Name : "Unknown"
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    categoriesCount = categoriesList.Count,
                    categories = categoriesList
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
        /// Adds categories to a filter
        /// </summary>
        [MCPMethod("addCategoriesToFilter", Category = "Filter", Description = "Adds categories to a filter")]
        public static string AddCategoriesToFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null || parameters["categoryIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId and categoryIds are required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                var categoryIdsInt = parameters["categoryIds"].ToObject<List<int>>();

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Add Categories to Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get existing categories
                    var existingCategories = filter.GetCategories();
                    var allCategories = new List<ElementId>(existingCategories);

                    // Add new categories
                    foreach (int catIdInt in categoryIdsInt)
                    {
                        ElementId catId = new ElementId(catIdInt);
                        if (!allCategories.Contains(catId))
                        {
                            allCategories.Add(catId);
                        }
                    }

                    // Set the updated categories
                    filter.SetCategories(allCategories);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        categoriesAdded = categoryIdsInt.Count,
                        totalCategories = allCategories.Count,
                        message = $"Categories added to filter. Total categories: {allCategories.Count}"
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
        /// Removes categories from a filter
        /// </summary>
        [MCPMethod("removeCategoriesFromFilter", Category = "Filter", Description = "Removes categories from a filter")]
        public static string RemoveCategoriesFromFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null || parameters["categoryIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId and categoryIds are required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                var categoryIdsInt = parameters["categoryIds"].ToObject<List<int>>();

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Remove Categories from Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get existing categories
                    var existingCategories = filter.GetCategories();
                    var remainingCategories = new List<ElementId>();

                    // Remove specified categories
                    foreach (ElementId catId in existingCategories)
                    {
                        if (!categoryIdsInt.Contains((int)catId.Value))
                        {
                            remainingCategories.Add(catId);
                        }
                    }

                    if (remainingCategories.Count == 0)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Cannot remove all categories from filter. Filter must have at least one category."
                        });
                    }

                    // Set the updated categories
                    filter.SetCategories(remainingCategories);

                    trans.Commit();

                    int removedCount = existingCategories.Count - remainingCategories.Count;

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filter.Name,
                        categoriesRemoved = removedCount,
                        remainingCategories = remainingCategories.Count,
                        message = $"{removedCount} categories removed. {remainingCategories.Count} categories remaining."
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

        #region Filter Templates

        /// <summary>
        /// Creates a filter from a template/preset
        /// </summary>
        [MCPMethod("createFilterFromTemplate", Category = "Filter", Description = "Creates a filter from a template or preset")]
        public static string CreateFilterFromTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["templateName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "templateName is required",
                        availableTemplates = new[] { "structural", "architectural", "mep", "walls", "doors", "windows", "rooms" }
                    });
                }

                string templateName = parameters["templateName"].ToString().ToLower();
                string filterName = parameters["filterName"]?.ToString() ?? $"{templateName} Elements";

                ICollection<ElementId> categoryIds = new List<ElementId>();

                // Define template categories
                switch (templateName)
                {
                    case "structural":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_StructuralColumns));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_StructuralFraming));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_StructuralFoundation));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Floors));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Walls));
                        break;
                    case "architectural":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Walls));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Doors));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Windows));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Rooms));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Ceilings));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Floors));
                        break;
                    case "mep":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_DuctCurves));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_PipeCurves));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_CableTray));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Conduit));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_MechanicalEquipment));
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_ElectricalEquipment));
                        break;
                    case "walls":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Walls));
                        break;
                    case "doors":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Doors));
                        break;
                    case "windows":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Windows));
                        break;
                    case "rooms":
                        categoryIds.Add(new ElementId((int)BuiltInCategory.OST_Rooms));
                        break;
                    default:
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown template: {templateName}",
                            availableTemplates = new[] { "structural", "architectural", "mep", "walls", "doors", "windows", "rooms" }
                        });
                }

                using (var trans = new Transaction(doc, "Create Filter from Template"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ParameterFilterElement filter = ParameterFilterElement.Create(doc, filterName, categoryIds);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = (int)filter.Id.Value,
                        filterName = filter.Name,
                        template = templateName,
                        categoriesCount = categoryIds.Count,
                        message = $"Filter created from '{templateName}' template with {categoryIds.Count} categories"
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
        /// Duplicates an existing filter
        /// </summary>
        [MCPMethod("duplicateFilter", Category = "Filter", Description = "Duplicates an existing filter")]
        public static string DuplicateFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null || parameters["newName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId and newName are required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);
                string newName = parameters["newName"].ToString();

                // Get the original filter
                ParameterFilterElement originalFilter = doc.GetElement(filterId) as ParameterFilterElement;
                if (originalFilter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Duplicate Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get categories and rules from original
                    var categories = originalFilter.GetCategories();

                    // Create new filter with same categories
                    ParameterFilterElement newFilter = ParameterFilterElement.Create(doc, newName, categories);

                    // Copy rules if they exist
                    var elementFilter = originalFilter.GetElementFilter() as ElementParameterFilter;
                    if (elementFilter != null)
                    {
                        newFilter.SetElementFilter(elementFilter);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalFilterId = filterIdInt,
                        originalFilterName = originalFilter.Name,
                        newFilterId = (int)newFilter.Id.Value,
                        newFilterName = newFilter.Name,
                        categoriesCount = categories.Count,
                        hasRules = elementFilter != null,
                        message = $"Filter duplicated successfully as '{newName}'"
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

        #region Filter Search and Analysis

        /// <summary>
        /// Finds views where a filter is applied
        /// </summary>
        [MCPMethod("findViewsUsingFilter", Category = "Filter", Description = "Finds all views where a filter is applied")]
        public static string FindViewsUsingFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                // Collect all views
                var viewsUsingFilter = new List<object>();
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View));

                foreach (View view in collector)
                {
                    if (view.IsTemplate) continue;

                    try
                    {
                        var filters = view.GetFilters();
                        if (filters.Contains(filterId))
                        {
                            viewsUsingFilter.Add(new
                            {
                                viewId = (int)view.Id.Value,
                                viewName = view.Name,
                                viewType = view.ViewType.ToString(),
                                filterVisible = view.GetFilterVisibility(filterId)
                            });
                        }
                    }
                    catch
                    {
                        // Some views don't support filters, skip them
                        continue;
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    viewsCount = viewsUsingFilter.Count,
                    views = viewsUsingFilter
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
        /// Tests a filter to see which elements it would select
        /// </summary>
        [MCPMethod("testFilter", Category = "Filter", Description = "Tests a filter to preview which elements it would select")]
        public static string TestFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                var categories = filter.GetCategories();
                var elementFilter = filter.GetElementFilter() as ElementParameterFilter;

                var matchingElements = new List<int>();

                // Test filter against each category
                foreach (ElementId catId in categories)
                {
                    FilteredElementCollector collector = new FilteredElementCollector(doc)
                        .OfCategoryId(catId)
                        .WhereElementIsNotElementType();

                    if (elementFilter != null)
                    {
                        collector = collector.WherePasses(elementFilter);
                    }

                    var elementIds = collector.ToElementIds();
                    foreach (ElementId id in elementIds)
                    {
                        matchingElements.Add((int)id.Value);
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    categoriesCount = categories.Count,
                    hasRules = elementFilter != null,
                    matchingElementsCount = matchingElements.Count,
                    matchingElementIds = matchingElements
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
        /// Analyzes filter performance and element count
        /// </summary>
        [MCPMethod("analyzeFilter", Category = "Filter", Description = "Analyzes filter performance and element count")]
        public static string AnalyzeFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                var categories = filter.GetCategories();
                var elementFilter = filter.GetElementFilter() as ElementParameterFilter;
                int ruleCount = 0;
                if (elementFilter != null)
                {
                    var rules = elementFilter.GetRules();
                    ruleCount = rules.Count;
                }

                // Count matching elements
                int totalElements = 0;
                var categoryCounts = new List<object>();

                foreach (ElementId catId in categories)
                {
                    FilteredElementCollector collector = new FilteredElementCollector(doc)
                        .OfCategoryId(catId)
                        .WhereElementIsNotElementType();

                    if (elementFilter != null)
                    {
                        collector = collector.WherePasses(elementFilter);
                    }

                    int count = collector.GetElementCount();
                    totalElements += count;

                    Category category = Category.GetCategory(doc, catId);
                    categoryCounts.Add(new
                    {
                        categoryId = (int)catId.Value,
                        categoryName = category != null ? category.Name : "Unknown",
                        elementCount = count
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    analysis = new
                    {
                        totalElementsMatching = totalElements,
                        categoriesCount = categories.Count,
                        rulesCount = ruleCount,
                        hasRules = ruleCount > 0,
                        complexity = ruleCount == 0 ? "simple" : ruleCount <= 3 ? "moderate" : "complex",
                        categoryBreakdown = categoryCounts
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

        #region Utility Methods

        /// <summary>
        /// Gets all filterable parameters for a category
        /// </summary>
        [MCPMethod("getFilterableParameters", Category = "Filter", Description = "Gets all filterable parameters for a category")]
        public static string GetFilterableParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["categoryId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categoryId is required"
                    });
                }

                int categoryIdInt = parameters["categoryId"].ToObject<int>();
                ElementId categoryId = new ElementId(categoryIdInt);

                Category category = Category.GetCategory(doc, categoryId);
                if (category == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category with ID {categoryIdInt} not found"
                    });
                }

                // Get filterable parameters using ParameterFilterUtilities
                var filterableParams = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, new[] { categoryId });
                var parametersList = new List<object>();

                foreach (ElementId paramId in filterableParams)
                {
                    try
                    {
                        var param = doc.GetElement(paramId);
                        string paramName = param != null ? param.Name : $"Parameter {paramId.Value}";

                        parametersList.Add(new
                        {
                            parameterId = (int)paramId.Value,
                            parameterName = paramName
                        });
                    }
                    catch
                    {
                        // Some parameters might not be accessible, skip them
                        continue;
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    categoryId = categoryIdInt,
                    categoryName = category.Name,
                    filterableParametersCount = parametersList.Count,
                    filterableParameters = parametersList
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
        /// Validates filter rules
        /// </summary>
        [MCPMethod("validateFilterRules", Category = "Filter", Description = "Validates filter rules for correctness")]
        public static string ValidateFilterRules(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filterId is required"
                    });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                // Get the filter
                ParameterFilterElement filter = doc.GetElement(filterId) as ParameterFilterElement;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Filter with ID {filterIdInt} not found"
                    });
                }

                var validationResults = new List<object>();
                var categories = filter.GetCategories();
                var elementFilter = filter.GetElementFilter() as ElementParameterFilter;

                // Validate categories
                if (categories.Count == 0)
                {
                    validationResults.Add(new
                    {
                        type = "error",
                        message = "Filter has no categories assigned"
                    });
                }

                // Validate rules
                if (elementFilter == null)
                {
                    validationResults.Add(new
                    {
                        type = "warning",
                        message = "Filter has no rules. All elements in specified categories will match."
                    });
                }
                else
                {
                    var rules = elementFilter.GetRules();
                    if (rules.Count == 0)
                    {
                        validationResults.Add(new
                        {
                            type = "warning",
                            message = "Filter has an element filter but no rules"
                        });
                    }
                    else
                    {
                        validationResults.Add(new
                        {
                            type = "info",
                            message = $"Filter has {rules.Count} rule(s)"
                        });
                    }
                }

                // Check if filter is used in any views
                int viewCount = 0;
                FilteredElementCollector viewCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View));

                foreach (View view in viewCollector)
                {
                    if (view.IsTemplate) continue;
                    try
                    {
                        var filters = view.GetFilters();
                        if (filters.Contains(filterId))
                        {
                            viewCount++;
                        }
                    }
                    catch { continue; }
                }

                if (viewCount == 0)
                {
                    validationResults.Add(new
                    {
                        type = "info",
                        message = "Filter is not currently applied to any views"
                    });
                }
                else
                {
                    validationResults.Add(new
                    {
                        type = "info",
                        message = $"Filter is applied to {viewCount} view(s)"
                    });
                }

                bool hasErrors = validationResults.Any(r => ((dynamic)r).type == "error");

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name,
                    isValid = !hasErrors,
                    validationResults = validationResults
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
