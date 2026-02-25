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
    /// Validation methods for self-verification of operations.
    /// These methods allow Claude to verify its own work before reporting success.
    /// </summary>
    public static class ValidationMethods
    {
        #region Element Verification

        /// <summary>
        /// Verify an element exists and matches expected properties
        /// Parameters:
        /// - elementId: ID of element to verify
        /// - expectedCategory: (optional) Expected category name
        /// - expectedName: (optional) Expected element name
        /// - expectedLocation: (optional) Expected [x, y, z] location
        /// - tolerance: (optional) Location tolerance in feet (default 0.1)
        /// </summary>
        public static string VerifyElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        verified = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        verified = false,
                        reason = "Element not found",
                        elementId = (int)elementId.Value
                    });
                }

                var issues = new List<string>();
                var details = new Dictionary<string, object>();

                // Check category
                var actualCategory = element.Category?.Name ?? "Unknown";
                details["actualCategory"] = actualCategory;

                if (parameters["expectedCategory"] != null)
                {
                    var expectedCategory = parameters["expectedCategory"].ToString();
                    if (!actualCategory.Equals(expectedCategory, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add($"Category mismatch: expected '{expectedCategory}', got '{actualCategory}'");
                    }
                }

                // Check name
                var actualName = element.Name ?? "";
                details["actualName"] = actualName;

                if (parameters["expectedName"] != null)
                {
                    var expectedName = parameters["expectedName"].ToString();
                    if (!actualName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add($"Name mismatch: expected '{expectedName}', got '{actualName}'");
                    }
                }

                // Check location
                if (parameters["expectedLocation"] != null)
                {
                    var tolerance = parameters["tolerance"]?.ToObject<double>() ?? 0.1;
                    var expectedLoc = parameters["expectedLocation"].ToObject<double[]>();
                    var expectedPoint = new XYZ(expectedLoc[0], expectedLoc[1], expectedLoc[2]);

                    XYZ actualPoint = null;
                    if (element.Location is LocationPoint lp)
                    {
                        actualPoint = lp.Point;
                    }
                    else if (element.Location is LocationCurve lc)
                    {
                        actualPoint = (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) / 2;
                    }

                    if (actualPoint != null)
                    {
                        details["actualLocation"] = new[] { actualPoint.X, actualPoint.Y, actualPoint.Z };
                        var distance = actualPoint.DistanceTo(expectedPoint);
                        details["locationDistance"] = distance;

                        if (distance > tolerance)
                        {
                            issues.Add($"Location mismatch: {distance:F2} ft from expected (tolerance: {tolerance} ft)");
                        }
                    }
                    else
                    {
                        issues.Add("Could not determine element location");
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verified = issues.Count == 0,
                    elementId = (int)elementId.Value,
                    issueCount = issues.Count,
                    issues = issues,
                    details = details
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    verified = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Verify a text note contains expected content
        /// Parameters:
        /// - elementId: ID of the text note
        /// - expectedText: Text that should be present (partial match)
        /// - exactMatch: (optional) Require exact match (default false)
        /// </summary>
        public static string VerifyTextContent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null || parameters["expectedText"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        verified = false,
                        error = "elementId and expectedText are required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        verified = false,
                        reason = "Text note not found"
                    });
                }

                string actualText = "";
                if (element is TextNote textNote)
                {
                    actualText = textNote.Text ?? "";
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        verified = false,
                        reason = "Element is not a text note",
                        actualType = element.GetType().Name
                    });
                }

                var expectedText = parameters["expectedText"].ToString();
                var exactMatch = parameters["exactMatch"]?.ToObject<bool>() ?? false;

                bool matches;
                if (exactMatch)
                {
                    matches = actualText.Equals(expectedText, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    matches = actualText.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verified = matches,
                    elementId = (int)elementId.Value,
                    expectedText = expectedText,
                    actualText = actualText,
                    matchType = exactMatch ? "exact" : "contains",
                    reason = matches ? "Text matches" : "Text does not match"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    verified = false,
                    error = ex.Message
                });
            }
        }

        #endregion

        #region View State Verification

        /// <summary>
        /// Get a snapshot of view contents for comparison
        /// Parameters:
        /// - viewId: ID of the view
        /// - categories: (optional) Array of category names to include
        /// </summary>
        public static string GetViewSnapshot(UIApplication uiApp, JObject parameters)
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

                var categoryFilter = parameters["categories"]?.ToObject<string[]>();

                var collector = new FilteredElementCollector(doc, viewId)
                    .WhereElementIsNotElementType();

                var elements = collector.ToElements();

                // Filter by categories if specified
                if (categoryFilter != null && categoryFilter.Length > 0)
                {
                    elements = elements
                        .Where(e => e.Category != null &&
                                   categoryFilter.Any(c => c.Equals(e.Category.Name, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                // Build snapshot
                var snapshot = new Dictionary<string, object>();
                var categoryCounts = elements
                    .Where(e => e.Category != null)
                    .GroupBy(e => e.Category.Name)
                    .ToDictionary(g => g.Key, g => g.Count());

                var elementSummaries = elements.Take(100).Select(e =>
                {
                    var summary = new Dictionary<string, object>
                    {
                        ["id"] = (int)e.Id.Value,
                        ["category"] = e.Category?.Name ?? "Unknown",
                        ["name"] = e.Name ?? ""
                    };

                    // Add text content for text notes
                    if (e is TextNote tn)
                    {
                        summary["text"] = tn.Text?.Length > 100 ? tn.Text.Substring(0, 100) + "..." : tn.Text;
                    }

                    return summary;
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    snapshotTime = DateTime.Now,
                    totalElements = elements.Count,
                    categoryCounts = categoryCounts,
                    elements = elementSummaries
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Compare current view state against a previous snapshot
        /// Parameters:
        /// - viewId: ID of the view
        /// - previousSnapshot: Previous snapshot data from GetViewSnapshot
        /// </summary>
        public static string CompareViewState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["previousSnapshot"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and previousSnapshot are required"
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

                var previousSnapshot = parameters["previousSnapshot"] as JObject;
                var previousCounts = previousSnapshot["categoryCounts"]?.ToObject<Dictionary<string, int>>() ?? new Dictionary<string, int>();
                var previousElements = previousSnapshot["elements"]?.ToObject<List<Dictionary<string, object>>>() ?? new List<Dictionary<string, object>>();
                var previousIds = previousElements.Select(e => Convert.ToInt32(e["id"])).ToHashSet();

                // Get current state
                var collector = new FilteredElementCollector(doc, viewId)
                    .WhereElementIsNotElementType();

                var currentElements = collector.ToElements();
                var currentCounts = currentElements
                    .Where(e => e.Category != null)
                    .GroupBy(e => e.Category.Name)
                    .ToDictionary(g => g.Key, g => g.Count());

                var currentIds = currentElements.Select(e => (int)e.Id.Value).ToHashSet();

                // Find changes
                var addedIds = currentIds.Except(previousIds).ToList();
                var removedIds = previousIds.Except(currentIds).ToList();

                var categoryChanges = new Dictionary<string, object>();
                var allCategories = previousCounts.Keys.Union(currentCounts.Keys);
                foreach (var cat in allCategories)
                {
                    int prev = 0;
                    int curr = 0;
                    previousCounts.TryGetValue(cat, out prev);
                    currentCounts.TryGetValue(cat, out curr);
                    if (prev != curr)
                    {
                        categoryChanges[cat] = new { previous = prev, current = curr, change = curr - prev };
                    }
                }

                // Get details of added elements
                var addedDetails = currentElements
                    .Where(e => addedIds.Contains((int)e.Id.Value))
                    .Take(20)
                    .Select(e => new
                    {
                        id = (int)e.Id.Value,
                        category = e.Category?.Name ?? "Unknown",
                        name = e.Name ?? ""
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    hasChanges = addedIds.Count > 0 || removedIds.Count > 0,
                    summary = new
                    {
                        elementsAdded = addedIds.Count,
                        elementsRemoved = removedIds.Count,
                        previousTotal = previousIds.Count,
                        currentTotal = currentIds.Count
                    },
                    categoryChanges = categoryChanges,
                    addedElementIds = addedIds.Take(50).ToList(),
                    removedElementIds = removedIds.Take(50).ToList(),
                    addedDetails = addedDetails
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Batch Verification

        /// <summary>
        /// Verify multiple elements in a single call
        /// Parameters:
        /// - verifications: Array of verification objects, each with:
        ///   - elementId: ID to verify
        ///   - expectedCategory: (optional)
        ///   - expectedName: (optional)
        ///   - expectedText: (optional, for text notes)
        /// </summary>
        public static string VerifyBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["verifications"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "verifications array is required"
                    });
                }

                var verifications = parameters["verifications"].ToObject<List<JObject>>();
                var results = new List<object>();
                var passCount = 0;
                var failCount = 0;

                foreach (var verification in verifications)
                {
                    var elementId = new ElementId(int.Parse(verification["elementId"].ToString()));
                    var element = doc.GetElement(elementId);

                    if (element == null)
                    {
                        results.Add(new
                        {
                            elementId = (int)elementId.Value,
                            passed = false,
                            reason = "Element not found"
                        });
                        failCount++;
                        continue;
                    }

                    var issues = new List<string>();

                    // Check category
                    if (verification["expectedCategory"] != null)
                    {
                        var expected = verification["expectedCategory"].ToString();
                        var actual = element.Category?.Name ?? "Unknown";
                        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add($"Category: expected '{expected}', got '{actual}'");
                        }
                    }

                    // Check name
                    if (verification["expectedName"] != null)
                    {
                        var expected = verification["expectedName"].ToString();
                        var actual = element.Name ?? "";
                        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add($"Name: expected '{expected}', got '{actual}'");
                        }
                    }

                    // Check text content
                    if (verification["expectedText"] != null && element is TextNote tn)
                    {
                        var expected = verification["expectedText"].ToString();
                        var actual = tn.Text ?? "";
                        if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            issues.Add($"Text: expected to contain '{expected}'");
                        }
                    }

                    var passed = issues.Count == 0;
                    if (passed) passCount++; else failCount++;

                    results.Add(new
                    {
                        elementId = (int)elementId.Value,
                        passed = passed,
                        issues = issues
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalVerifications = verifications.Count,
                    passed = passCount,
                    failed = failCount,
                    allPassed = failCount == 0,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Operation Verification

        /// <summary>
        /// Verify that a recent operation completed successfully
        /// Parameters:
        /// - operationType: Type of operation ("create", "modify", "delete")
        /// - elementId: ID of element that was operated on
        /// - expectedState: What state the element should be in
        /// </summary>
        public static string VerifyOperation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["operationType"] == null || parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "operationType and elementId are required"
                    });
                }

                var operationType = parameters["operationType"].ToString().ToLower();
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                switch (operationType)
                {
                    case "create":
                        if (element == null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                verified = false,
                                operationType = operationType,
                                reason = "Element was not created - not found in document"
                            });
                        }
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            verified = true,
                            operationType = operationType,
                            elementId = (int)elementId.Value,
                            elementCategory = element.Category?.Name,
                            elementName = element.Name,
                            message = "Create operation verified - element exists"
                        });

                    case "delete":
                        if (element != null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                verified = false,
                                operationType = operationType,
                                reason = "Element still exists - delete may have failed"
                            });
                        }
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            verified = true,
                            operationType = operationType,
                            elementId = (int)elementId.Value,
                            message = "Delete operation verified - element no longer exists"
                        });

                    case "modify":
                        if (element == null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                verified = false,
                                operationType = operationType,
                                reason = "Element not found - may have been deleted during modification"
                            });
                        }

                        // Check expected state if provided
                        if (parameters["expectedState"] != null)
                        {
                            var expectedState = parameters["expectedState"] as JObject;
                            var issues = new List<string>();

                            if (expectedState["text"] != null && element is TextNote tn)
                            {
                                var expectedText = expectedState["text"].ToString();
                                if (tn.Text?.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    issues.Add($"Text does not contain expected: '{expectedText}'");
                                }
                            }

                            if (expectedState["name"] != null)
                            {
                                var expectedName = expectedState["name"].ToString();
                                if (!element.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                                {
                                    issues.Add($"Name mismatch: expected '{expectedName}', got '{element.Name}'");
                                }
                            }

                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                verified = issues.Count == 0,
                                operationType = operationType,
                                elementId = (int)elementId.Value,
                                issues = issues,
                                message = issues.Count == 0 ? "Modify operation verified" : "Modification may not have applied correctly"
                            });
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            verified = true,
                            operationType = operationType,
                            elementId = (int)elementId.Value,
                            message = "Element exists (no expectedState provided to verify content)"
                        });

                    default:
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown operation type: {operationType}. Use 'create', 'modify', or 'delete'."
                        });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Count Verification

        /// <summary>
        /// Verify element count in a view matches expectations
        /// Parameters:
        /// - viewId: ID of the view
        /// - category: Category name to count
        /// - expectedCount: Expected number of elements
        /// - tolerance: (optional) Allowed difference (default 0)
        /// </summary>
        public static string VerifyElementCount(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["category"] == null || parameters["expectedCount"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId, category, and expectedCount are required"
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

                var categoryName = parameters["category"].ToString();
                var expectedCount = parameters["expectedCount"].ToObject<int>();
                var tolerance = parameters["tolerance"]?.ToObject<int>() ?? 0;

                // Find category
                Category targetCategory = null;
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetCategory = cat;
                        break;
                    }
                }

                if (targetCategory == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Category '{categoryName}' not found"
                    });
                }

                var actualCount = new FilteredElementCollector(doc, viewId)
                    .OfCategoryId(targetCategory.Id)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                var difference = Math.Abs(actualCount - expectedCount);
                var verified = difference <= tolerance;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verified = verified,
                    viewId = (int)viewId.Value,
                    category = categoryName,
                    expectedCount = expectedCount,
                    actualCount = actualCount,
                    difference = actualCount - expectedCount,
                    tolerance = tolerance,
                    message = verified
                        ? $"Count verified: {actualCount} {categoryName} elements"
                        : $"Count mismatch: expected {expectedCount} (+/- {tolerance}), found {actualCount}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Model State Awareness

        /// <summary>
        /// Get comprehensive model state for AI awareness before operations.
        /// This tells the AI what's in the model so it can make intelligent decisions.
        /// Parameters:
        /// - includeElementCounts: (optional) Include element counts by category (default: true)
        /// - includeLevels: (optional) Include level information (default: true)
        /// - includeViews: (optional) Include view summary (default: true)
        /// - includeSheets: (optional) Include sheet summary (default: true)
        /// - includeTypes: (optional) Include type counts (default: false, slower)
        /// </summary>
        public static string GetModelState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uiDoc = uiApp.ActiveUIDocument;

                bool includeElementCounts = parameters?["includeElementCounts"]?.Value<bool>() ?? true;
                bool includeLevels = parameters?["includeLevels"]?.Value<bool>() ?? true;
                bool includeViews = parameters?["includeViews"]?.Value<bool>() ?? true;
                bool includeSheets = parameters?["includeSheets"]?.Value<bool>() ?? true;
                bool includeTypes = parameters?["includeTypes"]?.Value<bool>() ?? false;

                var state = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["projectName"] = doc.Title,
                    ["projectPath"] = doc.PathName,
                    ["isWorkshared"] = doc.IsWorkshared,
                    ["activeView"] = new
                    {
                        id = uiDoc.ActiveView?.Id.Value ?? -1,
                        name = uiDoc.ActiveView?.Name ?? "None",
                        type = uiDoc.ActiveView?.ViewType.ToString() ?? "Unknown"
                    },
                    ["timestamp"] = DateTime.Now.ToString("o")
                };

                // Level information
                if (includeLevels)
                {
                    var levels = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .Select(l => new
                        {
                            id = (int)l.Id.Value,
                            name = l.Name,
                            elevation = l.Elevation
                        })
                        .ToList();

                    state["levels"] = new { count = levels.Count, items = levels };
                }

                // Element counts by category
                if (includeElementCounts)
                {
                    var elementCounts = new Dictionary<string, int>();
                    var importantCategories = new[]
                    {
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_Doors,
                        BuiltInCategory.OST_Windows,
                        BuiltInCategory.OST_Rooms,
                        BuiltInCategory.OST_Floors,
                        BuiltInCategory.OST_Roofs,
                        BuiltInCategory.OST_Ceilings,
                        BuiltInCategory.OST_Stairs,
                        BuiltInCategory.OST_Furniture,
                        BuiltInCategory.OST_Casework,
                        BuiltInCategory.OST_PlumbingFixtures,
                        BuiltInCategory.OST_LightingFixtures,
                        BuiltInCategory.OST_ElectricalFixtures,
                        BuiltInCategory.OST_StructuralColumns,
                        BuiltInCategory.OST_StructuralFraming
                    };

                    foreach (var cat in importantCategories)
                    {
                        try
                        {
                            var count = new FilteredElementCollector(doc)
                                .OfCategory(cat)
                                .WhereElementIsNotElementType()
                                .GetElementCount();

                            if (count > 0)
                            {
                                string catName = LabelUtils.GetLabelFor(cat);
                                elementCounts[catName] = count;
                            }
                        }
                        catch { /* Skip categories that don't work */ }
                    }

                    state["elements"] = new
                    {
                        totalCategories = elementCounts.Count,
                        counts = elementCounts
                    };
                }

                // View summary
                if (includeViews)
                {
                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate)
                        .GroupBy(v => v.ViewType)
                        .Select(g => new { type = g.Key.ToString(), count = g.Count() })
                        .ToList();

                    state["views"] = new
                    {
                        totalCount = views.Sum(v => v.count),
                        byType = views
                    };
                }

                // Sheet summary
                if (includeSheets)
                {
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .ToList();

                    var sheetSeries = sheets
                        .GroupBy(s => s.SheetNumber.Length > 0 ? s.SheetNumber[0].ToString() : "?")
                        .Select(g => new { series = g.Key, count = g.Count() })
                        .ToList();

                    state["sheets"] = new
                    {
                        totalCount = sheets.Count,
                        bySeries = sheetSeries
                    };
                }

                // Type counts (optional, slower)
                if (includeTypes)
                {
                    var typeCounts = new Dictionary<string, int>();

                    // Wall types
                    typeCounts["WallTypes"] = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType)).GetElementCount();

                    // Floor types
                    typeCounts["FloorTypes"] = new FilteredElementCollector(doc)
                        .OfClass(typeof(FloorType)).GetElementCount();

                    // Door types
                    typeCounts["DoorTypes"] = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .WhereElementIsElementType().GetElementCount();

                    // Window types
                    typeCounts["WindowTypes"] = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Windows)
                        .WhereElementIsElementType().GetElementCount();

                    state["types"] = typeCounts;
                }

                // Check for common issues
                var issues = new List<string>();

                if (includeLevels)
                {
                    var levelCount = (state["levels"] as dynamic)?.count ?? 0;
                    if (levelCount == 0)
                        issues.Add("No levels defined in model");
                }

                if (includeSheets)
                {
                    var sheetCount = (state["sheets"] as dynamic)?.totalCount ?? 0;
                    if (sheetCount == 0)
                        issues.Add("No sheets in model");
                }

                state["issues"] = issues;
                state["hasIssues"] = issues.Count > 0;

                return JsonConvert.SerializeObject(state);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Pre-flight check before executing an operation.
        /// Returns whether the operation can proceed and any issues to address first.
        /// Parameters:
        /// - operation: Operation name (e.g., "createWall", "placeViewOnSheet")
        /// - parameters: The parameters that will be passed to the operation
        /// </summary>
        public static string PreFlightCheck(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var operation = parameters["operation"]?.ToString();
                var opParams = parameters["parameters"] as JObject;

                if (string.IsNullOrEmpty(operation))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "operation is required"
                    });
                }

                var issues = new List<string>();
                var warnings = new List<string>();
                var suggestions = new List<string>();
                bool canProceed = true;

                switch (operation.ToLower())
                {
                    case "createsheet":
                        // Check for titleblocks
                        var titleblocks = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsElementType()
                            .GetElementCount();
                        if (titleblocks == 0)
                        {
                            issues.Add("No titleblock families loaded");
                            suggestions.Add("Load a titleblock family first using loadFamily");
                            canProceed = false;
                        }
                        break;

                    case "createwall":
                    case "createwallbypoints":
                    case "batchcreatewalls":
                        // Check for levels
                        var levels = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level))
                            .GetElementCount();
                        if (levels == 0)
                        {
                            issues.Add("No levels defined in model");
                            suggestions.Add("Create a level first");
                            canProceed = false;
                        }
                        // Check for wall types
                        var wallTypes = new FilteredElementCollector(doc)
                            .OfClass(typeof(WallType))
                            .GetElementCount();
                        if (wallTypes == 0)
                        {
                            issues.Add("No wall types available");
                            canProceed = false;
                        }
                        break;

                    case "placeviewonsheet":
                    case "placemultipleviewsonsheet":
                        // Check if sheet exists
                        if (opParams?["sheetId"] != null)
                        {
                            var sheetId = new ElementId(opParams["sheetId"].Value<int>());
                            var sheet = doc.GetElement(sheetId) as ViewSheet;
                            if (sheet == null)
                            {
                                issues.Add($"Sheet with ID {sheetId.Value} not found");
                                canProceed = false;
                            }
                        }
                        // Check if view(s) exist
                        if (opParams?["viewId"] != null)
                        {
                            var viewId = new ElementId(opParams["viewId"].Value<int>());
                            var view = doc.GetElement(viewId) as View;
                            if (view == null)
                            {
                                issues.Add($"View with ID {viewId.Value} not found");
                                canProceed = false;
                            }
                            else if (!Viewport.CanAddViewToSheet(doc,
                                new ElementId(opParams["sheetId"]?.Value<int>() ?? -1), viewId))
                            {
                                issues.Add("View cannot be placed on this sheet (already placed or invalid)");
                                canProceed = false;
                            }
                        }
                        break;

                    case "placedoor":
                    case "placewindow":
                        // Check if host wall exists
                        if (opParams?["wallId"] != null)
                        {
                            var wallId = new ElementId(opParams["wallId"].Value<int>());
                            var wall = doc.GetElement(wallId) as Wall;
                            if (wall == null)
                            {
                                issues.Add($"Wall with ID {wallId.Value} not found");
                                canProceed = false;
                            }
                        }
                        break;

                    case "createroom":
                        // Check for room bounding elements
                        var roomBoundingWalls = new FilteredElementCollector(doc)
                            .OfClass(typeof(Wall))
                            .Cast<Wall>()
                            .Where(w => w.WallType.Function == WallFunction.Interior ||
                                       w.WallType.Function == WallFunction.Exterior)
                            .Count();
                        if (roomBoundingWalls == 0)
                        {
                            warnings.Add("No room-bounding walls found. Room may not calculate area.");
                        }
                        break;

                    default:
                        // Generic checks
                        warnings.Add($"No specific pre-flight checks for '{operation}'");
                        break;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    operation = operation,
                    canProceed = canProceed,
                    issues = issues,
                    warnings = warnings,
                    suggestions = suggestions,
                    message = canProceed
                        ? (warnings.Count > 0 ? "Can proceed with warnings" : "Ready to proceed")
                        : "Cannot proceed - issues must be resolved first"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Advanced Validation & Analysis Methods

        /// <summary>
        /// Detect geometric clashes between elements of different categories.
        /// Essential for coordination and QC.
        /// </summary>
        public static string DetectClashes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var category1 = parameters?["category1"]?.ToString();
                var category2 = parameters?["category2"]?.ToString();
                var tolerance = parameters?["tolerance"]?.ToObject<double>() ?? 0.1; // feet
                var maxResults = parameters?["maxResults"]?.ToObject<int>() ?? 100;

                // Default: check MEP vs Structural
                BuiltInCategory cat1 = BuiltInCategory.OST_MechanicalEquipment;
                BuiltInCategory cat2 = BuiltInCategory.OST_StructuralFraming;

                if (!string.IsNullOrEmpty(category1))
                {
                    Enum.TryParse(category1, out cat1);
                }
                if (!string.IsNullOrEmpty(category2))
                {
                    Enum.TryParse(category2, out cat2);
                }

                // Get elements from both categories
                var elements1 = new FilteredElementCollector(doc)
                    .OfCategory(cat1)
                    .WhereElementIsNotElementType()
                    .ToList();

                var elements2 = new FilteredElementCollector(doc)
                    .OfCategory(cat2)
                    .WhereElementIsNotElementType()
                    .ToList();

                var clashes = new List<object>();
                int clashCount = 0;

                foreach (var elem1 in elements1)
                {
                    if (clashCount >= maxResults) break;

                    var bb1 = elem1.get_BoundingBox(null);
                    if (bb1 == null) continue;

                    // Expand bounding box by tolerance
                    var min1 = new XYZ(bb1.Min.X - tolerance, bb1.Min.Y - tolerance, bb1.Min.Z - tolerance);
                    var max1 = new XYZ(bb1.Max.X + tolerance, bb1.Max.Y + tolerance, bb1.Max.Z + tolerance);

                    foreach (var elem2 in elements2)
                    {
                        if (clashCount >= maxResults) break;

                        var bb2 = elem2.get_BoundingBox(null);
                        if (bb2 == null) continue;

                        // Check bounding box intersection
                        bool intersects = !(max1.X < bb2.Min.X || min1.X > bb2.Max.X ||
                                           max1.Y < bb2.Min.Y || min1.Y > bb2.Max.Y ||
                                           max1.Z < bb2.Min.Z || min1.Z > bb2.Max.Z);

                        if (intersects)
                        {
                            // Calculate clash point (center of intersection)
                            var clashX = (Math.Max(min1.X, bb2.Min.X) + Math.Min(max1.X, bb2.Max.X)) / 2;
                            var clashY = (Math.Max(min1.Y, bb2.Min.Y) + Math.Min(max1.Y, bb2.Max.Y)) / 2;
                            var clashZ = (Math.Max(min1.Z, bb2.Min.Z) + Math.Min(max1.Z, bb2.Max.Z)) / 2;

                            clashes.Add(new
                            {
                                element1 = new
                                {
                                    id = elem1.Id.Value,
                                    name = elem1.Name,
                                    category = elem1.Category?.Name
                                },
                                element2 = new
                                {
                                    id = elem2.Id.Value,
                                    name = elem2.Name,
                                    category = elem2.Category?.Name
                                },
                                clashPoint = new { x = Math.Round(clashX, 2), y = Math.Round(clashY, 2), z = Math.Round(clashZ, 2) },
                                severity = "hard" // bounding box intersection = hard clash
                            });
                            clashCount++;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category1 = cat1.ToString(),
                    category2 = cat2.ToString(),
                    elements1Count = elements1.Count,
                    elements2Count = elements2.Count,
                    clashCount = clashes.Count,
                    tolerance = tolerance,
                    clashes = clashes,
                    truncated = clashCount >= maxResults
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Run comprehensive clash detection across multiple category pairs.
        /// Parameters:
        /// - clashTests: Array of {category1, category2, tolerance} objects (optional)
        /// - useDefaults: If true, runs standard MEP/Structural/Architectural tests (default: true)
        /// - maxClashesPerTest: Max clashes to report per test (default: 100)
        /// </summary>
        public static string RunFullClashDetection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var useDefaults = parameters?["useDefaults"]?.ToObject<bool>() ?? true;
                var maxPerTest = parameters?["maxClashesPerTest"]?.ToObject<int>() ?? 100;
                var customTests = parameters?["clashTests"]?.ToObject<List<JObject>>();

                var testResults = new List<object>();
                var allClashes = new List<object>();
                int criticalCount = 0, majorCount = 0, minorCount = 0;

                // Define default clash tests
                var tests = new List<(string cat1, string cat2, double tolerance, string priority)>();

                if (useDefaults)
                {
                    // Structure vs MEP (Critical)
                    tests.Add(("Structural Framing", "Ducts", 0.167, "critical")); // 2" clearance
                    tests.Add(("Structural Framing", "Pipes", 0.083, "critical")); // 1" clearance
                    tests.Add(("Structural Columns", "Ducts", 0.167, "critical"));
                    tests.Add(("Structural Columns", "Pipes", 0.083, "critical"));

                    // Architecture vs MEP (Major)
                    tests.Add(("Walls", "Ducts", 0.0, "major"));
                    tests.Add(("Walls", "Pipes", 0.0, "major"));
                    tests.Add(("Floors", "Ducts", 0.0, "major"));

                    // MEP vs MEP (Minor)
                    tests.Add(("Ducts", "Pipes", 0.083, "minor"));
                    tests.Add(("Ducts", "Cable Trays", 0.083, "minor"));
                    tests.Add(("Pipes", "Conduits", 0.042, "minor")); // 0.5" clearance
                }

                // Add custom tests
                if (customTests != null)
                {
                    foreach (var test in customTests)
                    {
                        tests.Add((
                            test["category1"]?.ToString() ?? "",
                            test["category2"]?.ToString() ?? "",
                            test["tolerance"]?.ToObject<double>() ?? 0.0,
                            test["priority"]?.ToString() ?? "major"
                        ));
                    }
                }

                // Run each test
                foreach (var test in tests)
                {
                    try
                    {
                        BuiltInCategory cat1, cat2;
                        if (!TryParseCategory(test.cat1, out cat1) || !TryParseCategory(test.cat2, out cat2))
                        {
                            continue; // Skip invalid categories
                        }

                        var elements1 = new FilteredElementCollector(doc)
                            .OfCategory(cat1)
                            .WhereElementIsNotElementType()
                            .ToList();

                        var elements2 = new FilteredElementCollector(doc)
                            .OfCategory(cat2)
                            .WhereElementIsNotElementType()
                            .ToList();

                        var clashesInTest = new List<object>();
                        int clashCount = 0;

                        foreach (var elem1 in elements1)
                        {
                            if (clashCount >= maxPerTest) break;

                            var bb1 = elem1.get_BoundingBox(null);
                            if (bb1 == null) continue;

                            var min1 = new XYZ(bb1.Min.X - test.tolerance, bb1.Min.Y - test.tolerance, bb1.Min.Z - test.tolerance);
                            var max1 = new XYZ(bb1.Max.X + test.tolerance, bb1.Max.Y + test.tolerance, bb1.Max.Z + test.tolerance);

                            foreach (var elem2 in elements2)
                            {
                                if (clashCount >= maxPerTest) break;

                                var bb2 = elem2.get_BoundingBox(null);
                                if (bb2 == null) continue;

                                bool intersects = !(max1.X < bb2.Min.X || min1.X > bb2.Max.X ||
                                                   max1.Y < bb2.Min.Y || min1.Y > bb2.Max.Y ||
                                                   max1.Z < bb2.Min.Z || min1.Z > bb2.Max.Z);

                                if (intersects)
                                {
                                    var clashX = (Math.Max(min1.X, bb2.Min.X) + Math.Min(max1.X, bb2.Max.X)) / 2;
                                    var clashY = (Math.Max(min1.Y, bb2.Min.Y) + Math.Min(max1.Y, bb2.Max.Y)) / 2;
                                    var clashZ = (Math.Max(min1.Z, bb2.Min.Z) + Math.Min(max1.Z, bb2.Max.Z)) / 2;

                                    var clash = new
                                    {
                                        id = $"{elem1.Id.Value}_{elem2.Id.Value}",
                                        element1Id = elem1.Id.Value,
                                        element1Name = elem1.Name,
                                        element1Category = test.cat1,
                                        element2Id = elem2.Id.Value,
                                        element2Name = elem2.Name,
                                        element2Category = test.cat2,
                                        clashPoint = new { x = Math.Round(clashX, 2), y = Math.Round(clashY, 2), z = Math.Round(clashZ, 2) },
                                        priority = test.priority,
                                        testName = $"{test.cat1} vs {test.cat2}"
                                    };

                                    clashesInTest.Add(clash);
                                    allClashes.Add(clash);
                                    clashCount++;

                                    if (test.priority == "critical") criticalCount++;
                                    else if (test.priority == "major") majorCount++;
                                    else minorCount++;
                                }
                            }
                        }

                        testResults.Add(new
                        {
                            testName = $"{test.cat1} vs {test.cat2}",
                            category1 = test.cat1,
                            category2 = test.cat2,
                            tolerance = test.tolerance,
                            priority = test.priority,
                            elements1Count = elements1.Count,
                            elements2Count = elements2.Count,
                            clashCount = clashesInTest.Count
                        });
                    }
                    catch
                    {
                        // Skip failed tests
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    testsRun = testResults.Count,
                    totalClashes = allClashes.Count,
                    summary = new
                    {
                        critical = criticalCount,
                        major = majorCount,
                        minor = minorCount
                    },
                    healthStatus = criticalCount > 0 ? "Critical Issues" :
                                   majorCount > 5 ? "Needs Attention" :
                                   allClashes.Count == 0 ? "No Clashes" : "Minor Issues",
                    testResults = testResults,
                    clashes = allClashes.Take(500)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a 3D view focused on a clash location.
        /// Parameters:
        /// - clashPoint: {x, y, z} location of clash
        /// - element1Id: First element ID
        /// - element2Id: Second element ID
        /// - viewName: Name for the clash view (optional)
        /// - zoomMargin: Extra margin around clash in feet (default: 5)
        /// </summary>
        public static string CreateClashView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var clashPoint = parameters?["clashPoint"]?.ToObject<Dictionary<string, double>>();
                var element1Id = parameters?["element1Id"]?.ToObject<int>();
                var element2Id = parameters?["element2Id"]?.ToObject<int>();
                var viewName = parameters?["viewName"]?.ToString() ?? $"Clash View {DateTime.Now:yyyyMMdd_HHmmss}";
                var zoomMargin = parameters?["zoomMargin"]?.ToObject<double>() ?? 5.0;

                if (clashPoint == null || !element1Id.HasValue || !element2Id.HasValue)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "clashPoint, element1Id, and element2Id are required"
                    });
                }

                ElementId newViewId = ElementId.InvalidElementId;

                using (var trans = new Transaction(doc, "Create Clash View"))
                {
                    trans.Start();

                    // Find a 3D view type
                    var viewType3D = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

                    if (viewType3D == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No 3D view type available"
                        });
                    }

                    // Create new 3D view
                    var view3D = View3D.CreateIsometric(doc, viewType3D.Id);
                    view3D.Name = viewName;

                    // Get bounding boxes of both elements
                    var elem1 = doc.GetElement(new ElementId(element1Id.Value));
                    var elem2 = doc.GetElement(new ElementId(element2Id.Value));

                    BoundingBoxXYZ combinedBB = null;
                    var bb1 = elem1?.get_BoundingBox(null);
                    var bb2 = elem2?.get_BoundingBox(null);

                    if (bb1 != null && bb2 != null)
                    {
                        combinedBB = new BoundingBoxXYZ
                        {
                            Min = new XYZ(
                                Math.Min(bb1.Min.X, bb2.Min.X) - zoomMargin,
                                Math.Min(bb1.Min.Y, bb2.Min.Y) - zoomMargin,
                                Math.Min(bb1.Min.Z, bb2.Min.Z) - zoomMargin
                            ),
                            Max = new XYZ(
                                Math.Max(bb1.Max.X, bb2.Max.X) + zoomMargin,
                                Math.Max(bb1.Max.Y, bb2.Max.Y) + zoomMargin,
                                Math.Max(bb1.Max.Z, bb2.Max.Z) + zoomMargin
                            )
                        };
                    }
                    else
                    {
                        // Use clash point with margin
                        var pt = new XYZ(clashPoint["x"], clashPoint["y"], clashPoint["z"]);
                        combinedBB = new BoundingBoxXYZ
                        {
                            Min = new XYZ(pt.X - zoomMargin, pt.Y - zoomMargin, pt.Z - zoomMargin),
                            Max = new XYZ(pt.X + zoomMargin, pt.Y + zoomMargin, pt.Z + zoomMargin)
                        };
                    }

                    // Set section box
                    view3D.SetSectionBox(combinedBB);
                    view3D.IsSectionBoxActive = true;

                    // Isolate the clashing elements
                    var elementsToIsolate = new List<ElementId>();
                    if (elem1 != null) elementsToIsolate.Add(elem1.Id);
                    if (elem2 != null) elementsToIsolate.Add(elem2.Id);

                    if (elementsToIsolate.Count > 0)
                    {
                        try
                        {
                            view3D.IsolateElementsTemporary(elementsToIsolate);
                        }
                        catch
                        {
                            // Isolation may fail on some elements
                        }
                    }

                    newViewId = view3D.Id;
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = newViewId.Value,
                    viewName = viewName,
                    clashPoint = clashPoint,
                    element1Id = element1Id,
                    element2Id = element2Id,
                    message = "3D view created with section box focused on clash"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Export clash detection results to file (HTML report).
        /// Parameters:
        /// - clashes: Array of clash objects from detectClashes/runFullClashDetection
        /// - outputPath: File path for output (optional, uses temp folder if not specified)
        /// - projectName: Name to include in report (optional)
        /// - format: "html" (default), "csv", or "json"
        /// </summary>
        public static string ExportClashReport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var clashes = parameters?["clashes"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                var projectName = parameters?["projectName"]?.ToString() ?? doc.Title;
                var format = parameters?["format"]?.ToString()?.ToLower() ?? "html";
                var outputPath = parameters?["outputPath"]?.ToString();

                if (string.IsNullOrEmpty(outputPath))
                {
                    var tempDir = System.IO.Path.GetTempPath();
                    var fileName = $"ClashReport_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";
                    outputPath = System.IO.Path.Combine(tempDir, fileName);
                }

                // Count by priority
                var critical = clashes.Count(c => c["priority"]?.ToString() == "critical");
                var major = clashes.Count(c => c["priority"]?.ToString() == "major");
                var minor = clashes.Count(c => c["priority"]?.ToString() == "minor");

                if (format == "html")
                {
                    var html = new System.Text.StringBuilder();
                    html.AppendLine("<!DOCTYPE html>");
                    html.AppendLine("<html><head><title>Clash Detection Report</title>");
                    html.AppendLine("<style>");
                    html.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
                    html.AppendLine("h1 { color: #333; }");
                    html.AppendLine("table { border-collapse: collapse; width: 100%; margin-top: 20px; }");
                    html.AppendLine("th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
                    html.AppendLine("th { background-color: #4CAF50; color: white; }");
                    html.AppendLine("tr:nth-child(even) { background-color: #f2f2f2; }");
                    html.AppendLine(".critical { background-color: #ffcccc; }");
                    html.AppendLine(".major { background-color: #ffffcc; }");
                    html.AppendLine(".minor { background-color: #ccffcc; }");
                    html.AppendLine(".summary { display: flex; gap: 20px; margin: 20px 0; }");
                    html.AppendLine(".summary-box { padding: 15px; border-radius: 5px; text-align: center; }");
                    html.AppendLine(".summary-critical { background: #ff6666; color: white; }");
                    html.AppendLine(".summary-major { background: #ffcc00; }");
                    html.AppendLine(".summary-minor { background: #66cc66; color: white; }");
                    html.AppendLine("</style></head><body>");
                    html.AppendLine($"<h1>Clash Detection Report</h1>");
                    html.AppendLine($"<p><strong>Project:</strong> {projectName}</p>");
                    html.AppendLine($"<p><strong>Generated:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
                    html.AppendLine($"<p><strong>Total Clashes:</strong> {clashes.Count}</p>");

                    html.AppendLine("<div class='summary'>");
                    html.AppendLine($"<div class='summary-box summary-critical'><h2>{critical}</h2><p>Critical</p></div>");
                    html.AppendLine($"<div class='summary-box summary-major'><h2>{major}</h2><p>Major</p></div>");
                    html.AppendLine($"<div class='summary-box summary-minor'><h2>{minor}</h2><p>Minor</p></div>");
                    html.AppendLine("</div>");

                    html.AppendLine("<table>");
                    html.AppendLine("<tr><th>#</th><th>Priority</th><th>Element 1</th><th>Element 2</th><th>Location</th><th>Test</th></tr>");

                    int idx = 1;
                    foreach (var clash in clashes)
                    {
                        var priority = clash["priority"]?.ToString() ?? "minor";
                        var elem1 = clash["element1Name"]?.ToString() ?? clash["element1Id"]?.ToString() ?? "Unknown";
                        var elem2 = clash["element2Name"]?.ToString() ?? clash["element2Id"]?.ToString() ?? "Unknown";
                        var point = clash["clashPoint"];
                        var location = point != null ? $"({point["x"]}, {point["y"]}, {point["z"]})" : "N/A";
                        var test = clash["testName"]?.ToString() ?? "Custom";

                        html.AppendLine($"<tr class='{priority}'>");
                        html.AppendLine($"<td>{idx++}</td>");
                        html.AppendLine($"<td>{priority.ToUpper()}</td>");
                        html.AppendLine($"<td>{elem1} (ID: {clash["element1Id"]})</td>");
                        html.AppendLine($"<td>{elem2} (ID: {clash["element2Id"]})</td>");
                        html.AppendLine($"<td>{location}</td>");
                        html.AppendLine($"<td>{test}</td>");
                        html.AppendLine("</tr>");
                    }

                    html.AppendLine("</table>");
                    html.AppendLine("</body></html>");

                    System.IO.File.WriteAllText(outputPath, html.ToString());
                }
                else if (format == "csv")
                {
                    var csv = new System.Text.StringBuilder();
                    csv.AppendLine("Index,Priority,Element1_ID,Element1_Name,Element2_ID,Element2_Name,Location_X,Location_Y,Location_Z,Test");

                    int idx = 1;
                    foreach (var clash in clashes)
                    {
                        var point = clash["clashPoint"];
                        csv.AppendLine($"{idx++},{clash["priority"]},{clash["element1Id"]},\"{clash["element1Name"]}\",{clash["element2Id"]},\"{clash["element2Name"]}\",{point?["x"]},{point?["y"]},{point?["z"]},\"{clash["testName"]}\"");
                    }

                    System.IO.File.WriteAllText(outputPath, csv.ToString());
                }
                else // json
                {
                    var report = new
                    {
                        projectName = projectName,
                        generatedAt = DateTime.Now.ToString("o"),
                        summary = new { total = clashes.Count, critical, major, minor },
                        clashes = clashes
                    };
                    System.IO.File.WriteAllText(outputPath, JsonConvert.SerializeObject(report, Formatting.Indented));
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    filePath = outputPath,
                    format = format,
                    clashCount = clashes.Count,
                    summary = new { critical, major, minor }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get quick summary of potential clashes without full detection.
        /// Checks element counts and overlap potential for common problem pairs.
        /// </summary>
        public static string GetClashRiskAssessment(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var risks = new List<object>();

                // Count elements by category
                var categoryCounts = new Dictionary<string, int>
                {
                    ["Ducts"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctCurves).WhereElementIsNotElementType().GetElementCount(),
                    ["Pipes"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().GetElementCount(),
                    ["StructuralFraming"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().GetElementCount(),
                    ["StructuralColumns"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount(),
                    ["Walls"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().GetElementCount(),
                    ["Floors"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().GetElementCount(),
                    ["CableTray"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_CableTray).WhereElementIsNotElementType().GetElementCount(),
                    ["Conduit"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Conduit).WhereElementIsNotElementType().GetElementCount()
                };

                // Assess risk for common pairs
                if (categoryCounts["Ducts"] > 0 && categoryCounts["StructuralFraming"] > 0)
                {
                    int potential = categoryCounts["Ducts"] * categoryCounts["StructuralFraming"];
                    risks.Add(new
                    {
                        pair = "Ducts vs Structural Framing",
                        riskLevel = potential > 10000 ? "High" : potential > 1000 ? "Medium" : "Low",
                        ductCount = categoryCounts["Ducts"],
                        framingCount = categoryCounts["StructuralFraming"],
                        potentialChecks = potential,
                        recommendation = "Run detectClashes with category1='Ducts', category2='Structural Framing'"
                    });
                }

                if (categoryCounts["Pipes"] > 0 && categoryCounts["StructuralFraming"] > 0)
                {
                    int potential = categoryCounts["Pipes"] * categoryCounts["StructuralFraming"];
                    risks.Add(new
                    {
                        pair = "Pipes vs Structural Framing",
                        riskLevel = potential > 10000 ? "High" : potential > 1000 ? "Medium" : "Low",
                        pipeCount = categoryCounts["Pipes"],
                        framingCount = categoryCounts["StructuralFraming"],
                        potentialChecks = potential,
                        recommendation = "Run detectClashes with category1='Pipes', category2='Structural Framing'"
                    });
                }

                if (categoryCounts["Ducts"] > 0 && categoryCounts["Pipes"] > 0)
                {
                    int potential = categoryCounts["Ducts"] * categoryCounts["Pipes"];
                    risks.Add(new
                    {
                        pair = "Ducts vs Pipes",
                        riskLevel = potential > 10000 ? "High" : potential > 1000 ? "Medium" : "Low",
                        ductCount = categoryCounts["Ducts"],
                        pipeCount = categoryCounts["Pipes"],
                        potentialChecks = potential,
                        recommendation = "Run detectClashes with category1='Ducts', category2='Pipes'"
                    });
                }

                string overallRisk = risks.Any(r => ((dynamic)r).riskLevel == "High") ? "High" :
                                    risks.Any(r => ((dynamic)r).riskLevel == "Medium") ? "Medium" : "Low";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    overallRisk = overallRisk,
                    categoryCounts = categoryCounts,
                    riskAssessments = risks,
                    recommendation = overallRisk == "High" ?
                        "Multiple high-risk category pairs detected. Run runFullClashDetection for comprehensive analysis." :
                        overallRisk == "Medium" ?
                        "Some clash risk detected. Consider running targeted clash tests." :
                        "Low clash risk. Model appears well-coordinated."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper method to parse category names to BuiltInCategory
        /// </summary>
        private static bool TryParseCategory(string categoryName, out BuiltInCategory category)
        {
            category = BuiltInCategory.INVALID;

            var categoryMap = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["Walls"] = BuiltInCategory.OST_Walls,
                ["Floors"] = BuiltInCategory.OST_Floors,
                ["Ceilings"] = BuiltInCategory.OST_Ceilings,
                ["Roofs"] = BuiltInCategory.OST_Roofs,
                ["Doors"] = BuiltInCategory.OST_Doors,
                ["Windows"] = BuiltInCategory.OST_Windows,
                ["Ducts"] = BuiltInCategory.OST_DuctCurves,
                ["Pipes"] = BuiltInCategory.OST_PipeCurves,
                ["Conduits"] = BuiltInCategory.OST_Conduit,
                ["Conduit"] = BuiltInCategory.OST_Conduit,
                ["Cable Trays"] = BuiltInCategory.OST_CableTray,
                ["CableTray"] = BuiltInCategory.OST_CableTray,
                ["Structural Framing"] = BuiltInCategory.OST_StructuralFraming,
                ["StructuralFraming"] = BuiltInCategory.OST_StructuralFraming,
                ["Structural Columns"] = BuiltInCategory.OST_StructuralColumns,
                ["StructuralColumns"] = BuiltInCategory.OST_StructuralColumns,
                ["Columns"] = BuiltInCategory.OST_Columns,
                ["Furniture"] = BuiltInCategory.OST_Furniture,
                ["Casework"] = BuiltInCategory.OST_Casework,
                ["Mechanical Equipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["Plumbing Fixtures"] = BuiltInCategory.OST_PlumbingFixtures,
                ["Electrical Equipment"] = BuiltInCategory.OST_ElectricalEquipment,
                ["Electrical Fixtures"] = BuiltInCategory.OST_ElectricalFixtures,
                ["Lighting Fixtures"] = BuiltInCategory.OST_LightingFixtures
            };

            if (categoryMap.TryGetValue(categoryName, out category))
            {
                return true;
            }

            // Try direct enum parse
            if (Enum.TryParse($"OST_{categoryName.Replace(" ", "")}", true, out category))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Comprehensive model health validation.
        /// Checks for orphaned elements, floating furniture, missing parameters, etc.
        /// </summary>
        public static string ValidateModelHealth(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var includeWarnings = parameters?["includeWarnings"]?.ToObject<bool>() ?? true;

                var issues = new List<object>();
                var warnings = new List<object>();
                var stats = new Dictionary<string, int>();

                // 1. Check for model warnings
                if (includeWarnings)
                {
                    var revitWarnings = doc.GetWarnings();
                    stats["revitWarnings"] = revitWarnings.Count;

                    foreach (var warning in revitWarnings.Take(20))
                    {
                        warnings.Add(new
                        {
                            severity = warning.GetSeverity().ToString(),
                            description = warning.GetDescriptionText(),
                            elementIds = warning.GetFailingElements().Select(e => e.Value).ToList()
                        });
                    }
                }

                // 2. Check for unplaced rooms
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .ToList();

                var unplacedRooms = rooms.Where(r => r.Area == 0 || r.Location == null).ToList();
                stats["totalRooms"] = rooms.Count;
                stats["unplacedRooms"] = unplacedRooms.Count;

                foreach (var room in unplacedRooms.Take(10))
                {
                    issues.Add(new
                    {
                        type = "unplaced_room",
                        severity = "medium",
                        elementId = room.Id.Value,
                        name = room.Name,
                        message = "Room is not enclosed or has no area"
                    });
                }

                // 3. Check for orphaned views (not on sheets, not templates)
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet)
                    .ToList();

                var placedViewIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Select(vp => vp.ViewId.Value)
                    .ToHashSet();

                var orphanedViews = views.Where(v =>
                    !placedViewIds.Contains(v.Id.Value) &&
                    Viewport.CanAddViewToSheet(doc, ElementId.InvalidElementId, v.Id)).ToList();

                stats["totalViews"] = views.Count;
                stats["orphanedViews"] = orphanedViews.Count;

                // 4. Check for elements at wrong levels
                var furniture = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Furniture)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                int floatingFurniture = 0;
                foreach (var item in furniture)
                {
                    var bb = item.get_BoundingBox(null);
                    if (bb != null && bb.Min.Z < -1) // More than 1 foot below ground
                    {
                        floatingFurniture++;
                        if (floatingFurniture <= 5)
                        {
                            issues.Add(new
                            {
                                type = "floating_element",
                                severity = "low",
                                elementId = item.Id.Value,
                                name = item.Name,
                                message = $"Furniture below grade (Z={Math.Round(bb.Min.Z, 2)})"
                            });
                        }
                    }
                }
                stats["floatingFurniture"] = floatingFurniture;

                // 5. Check for missing required parameters on doors
                var doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                int doorsWithoutMarks = doors.Count(d =>
                    string.IsNullOrEmpty(d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()));
                stats["doorsWithoutMarks"] = doorsWithoutMarks;

                if (doorsWithoutMarks > 0)
                {
                    issues.Add(new
                    {
                        type = "missing_parameter",
                        severity = "medium",
                        count = doorsWithoutMarks,
                        message = $"{doorsWithoutMarks} doors are missing Mark values"
                    });
                }

                // 6. Check model file size
                var fileInfo = new System.IO.FileInfo(doc.PathName);
                long fileSizeMB = fileInfo.Exists ? fileInfo.Length / (1024 * 1024) : 0;
                stats["fileSizeMB"] = (int)fileSizeMB;

                if (fileSizeMB > 300)
                {
                    issues.Add(new
                    {
                        type = "large_file",
                        severity = "warning",
                        message = $"Model file is {fileSizeMB}MB - consider purging unused elements"
                    });
                }

                // Calculate health score
                int issueScore = issues.Count * 10 + warnings.Count;
                string healthRating = issueScore == 0 ? "Excellent" :
                                     issueScore < 10 ? "Good" :
                                     issueScore < 30 ? "Fair" : "Needs Attention";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    healthRating = healthRating,
                    issueCount = issues.Count,
                    warningCount = warnings.Count,
                    statistics = stats,
                    issues = issues,
                    warnings = warnings.Take(20),
                    recommendations = new[]
                    {
                        issues.Any(i => ((dynamic)i).type == "unplaced_room") ? "Review and place unenclosed rooms" : null,
                        stats.GetValueOrDefault("orphanedViews") > 20 ? "Consider deleting unused views" : null,
                        stats.GetValueOrDefault("doorsWithoutMarks") > 0 ? "Add Mark values to doors for scheduling" : null,
                        fileSizeMB > 200 ? "Purge unused families and groups" : null
                    }.Where(r => r != null)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Auto-place keynotes on elements in a view with collision avoidance.
        /// </summary>
        public static string AutoPlaceKeynotes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewIdParam = parameters?["viewId"];
                var categoryParam = parameters?["category"]?.ToString();
                var keynoteTagFamilyName = parameters?["keynoteTagFamily"]?.ToString() ?? "Keynote Tag";

                if (viewIdParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
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

                // Get keynote tag type
                var keynoteTagTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_KeynoteTags)
                    .Cast<FamilySymbol>()
                    .ToList();

                if (keynoteTagTypes.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No keynote tag families found. Load a keynote tag family first."
                    });
                }

                var tagType = keynoteTagTypes.FirstOrDefault(t =>
                    t.FamilyName.IndexOf(keynoteTagFamilyName, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? keynoteTagTypes.First();

                // Get elements to tag
                var collector = new FilteredElementCollector(doc, viewId)
                    .WhereElementIsNotElementType();

                if (!string.IsNullOrEmpty(categoryParam) && Enum.TryParse<BuiltInCategory>(categoryParam, out var cat))
                {
                    collector = collector.OfCategory(cat) as FilteredElementCollector;
                }

                var elementsToTag = collector.ToList();

                // Get existing tag locations for collision avoidance
                var existingTags = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .Select(t => t.TagHeadPosition)
                    .Where(p => p != null)
                    .ToList();

                int placedCount = 0;
                int skippedCount = 0;
                var placedTags = new List<object>();

                using (var trans = new Transaction(doc, "Auto Place Keynotes"))
                {
                    trans.Start();

                    foreach (var elem in elementsToTag.Take(50)) // Limit to 50 elements
                    {
                        // Check if element has keynote parameter
                        var keynoteParam = elem.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                        if (keynoteParam == null || string.IsNullOrEmpty(keynoteParam.AsString()))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Get element location
                        var bb = elem.get_BoundingBox(view);
                        if (bb == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        // Calculate tag position (offset from element center)
                        var center = (bb.Min + bb.Max) / 2;
                        var tagOffset = new XYZ(2, 2, 0); // 2 feet offset

                        // Find non-colliding position
                        var tagPos = center + tagOffset;
                        int attempts = 0;
                        while (attempts < 4 && existingTags.Any(t =>
                            Math.Abs(t.X - tagPos.X) < 1 && Math.Abs(t.Y - tagPos.Y) < 1))
                        {
                            // Rotate offset 90 degrees
                            tagOffset = new XYZ(-tagOffset.Y, tagOffset.X, 0);
                            tagPos = center + tagOffset;
                            attempts++;
                        }

                        try
                        {
                            // Create the keynote tag
                            var reference = new Reference(elem);
                            var tag = IndependentTag.Create(doc, viewId, reference, false, TagMode.TM_ADDBY_MATERIAL, TagOrientation.Horizontal, tagPos);

                            if (tag != null)
                            {
                                existingTags.Add(tagPos);
                                placedTags.Add(new
                                {
                                    tagId = tag.Id.Value,
                                    elementId = elem.Id.Value,
                                    position = new { x = tagPos.X, y = tagPos.Y }
                                });
                                placedCount++;
                            }
                        }
                        catch
                        {
                            skippedCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId.Value,
                    viewName = view.Name,
                    elementsProcessed = elementsToTag.Count,
                    tagsPlaced = placedCount,
                    skipped = skippedCount,
                    tags = placedTags
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Generate a legend from model element types.
        /// </summary>
        public static string GenerateLegendFromTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var categoryParam = parameters?["category"]?.ToString() ?? "Doors";
                var legendName = parameters?["legendName"]?.ToString() ?? $"{categoryParam} Legend";

                // Parse category
                BuiltInCategory category = BuiltInCategory.OST_Doors;
                if (!string.IsNullOrEmpty(categoryParam))
                {
                    if (categoryParam.ToLower().Contains("window")) category = BuiltInCategory.OST_Windows;
                    else if (categoryParam.ToLower().Contains("wall")) category = BuiltInCategory.OST_Walls;
                    else if (categoryParam.ToLower().Contains("floor")) category = BuiltInCategory.OST_Floors;
                    else if (categoryParam.ToLower().Contains("ceiling")) category = BuiltInCategory.OST_Ceilings;
                    else if (categoryParam.ToLower().Contains("furniture")) category = BuiltInCategory.OST_Furniture;
                }

                // Get all types in use
                var instances = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToList();

                var typeIds = instances.Select(e => e.GetTypeId()).Distinct().ToList();
                var typesInUse = typeIds
                    .Select(id => doc.GetElement(id))
                    .Where(t => t != null)
                    .OrderBy(t => t.Name)
                    .ToList();

                // Build legend data
                var legendData = typesInUse.Select(t =>
                {
                    var symbol = t as FamilySymbol;
                    var elementType = t as ElementType;

                    return new
                    {
                        typeId = t.Id.Value,
                        typeName = t.Name,
                        familyName = symbol?.FamilyName ?? "",
                        instanceCount = instances.Count(i => i.GetTypeId() == t.Id),
                        description = elementType?.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? "",
                        mark = elementType?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? ""
                    };
                }).ToList();

                // Check if legend view exists or create one
                var existingLegend = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => v.ViewType == ViewType.Legend && v.Name == legendName);

                var legendViewId = existingLegend?.Id.Value;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = category.ToString(),
                    legendName = legendName,
                    existingLegendViewId = legendViewId,
                    typeCount = typesInUse.Count,
                    totalInstances = instances.Count,
                    types = legendData,
                    note = existingLegend == null
                        ? "Legend view does not exist. Use Revit UI to create a legend view, then populate manually or use legend component placement."
                        : "Legend view exists. Review and update as needed."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Export construction data for takeoffs and estimating.
        /// </summary>
        public static string ExportConstructionData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var includeQuantities = parameters?["includeQuantities"]?.ToObject<bool>() ?? true;

                var data = new Dictionary<string, object>();

                // Walls - linear feet and area
                var walls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                var wallData = walls.GroupBy(w => doc.GetElement(w.GetTypeId())?.Name ?? "Unknown")
                    .Select(g => new
                    {
                        typeName = g.Key,
                        count = g.Count(),
                        totalLengthFeet = Math.Round(g.Sum(w =>
                            w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0), 1),
                        totalAreaSF = Math.Round(g.Sum(w =>
                            w.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0), 1)
                    })
                    .OrderByDescending(w => w.totalAreaSF)
                    .ToList();

                data["walls"] = new { summary = new { count = walls.Count, types = wallData.Count }, byType = wallData };

                // Doors - count by type
                var doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var doorData = doors.GroupBy(d => doc.GetElement(d.GetTypeId())?.Name ?? "Unknown")
                    .Select(g => new
                    {
                        typeName = g.Key,
                        count = g.Count(),
                        width = g.First().get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                        height = g.First().get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0
                    })
                    .OrderByDescending(d => d.count)
                    .ToList();

                data["doors"] = new { summary = new { count = doors.Count, types = doorData.Count }, byType = doorData };

                // Windows
                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var windowData = windows.GroupBy(w => doc.GetElement(w.GetTypeId())?.Name ?? "Unknown")
                    .Select(g => new
                    {
                        typeName = g.Key,
                        count = g.Count(),
                        width = g.First().get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                        height = g.First().get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0
                    })
                    .OrderByDescending(w => w.count)
                    .ToList();

                data["windows"] = new { summary = new { count = windows.Count, types = windowData.Count }, byType = windowData };

                // Floors - area by type
                var floors = new FilteredElementCollector(doc)
                    .OfClass(typeof(Floor))
                    .WhereElementIsNotElementType()
                    .Cast<Floor>()
                    .ToList();

                var floorData = floors.GroupBy(f => doc.GetElement(f.GetTypeId())?.Name ?? "Unknown")
                    .Select(g => new
                    {
                        typeName = g.Key,
                        count = g.Count(),
                        totalAreaSF = Math.Round(g.Sum(f =>
                            f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0), 1)
                    })
                    .OrderByDescending(f => f.totalAreaSF)
                    .ToList();

                data["floors"] = new { summary = new { count = floors.Count, types = floorData.Count }, byType = floorData };

                // Rooms - for programming verification
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                var roomData = rooms.Select(r => new
                {
                    name = r.Name,
                    number = r.Number,
                    level = r.Level?.Name,
                    areaSF = Math.Round(r.Area, 1),
                    perimeter = Math.Round(r.Perimeter, 1)
                }).OrderBy(r => r.level).ThenBy(r => r.number).ToList();

                data["rooms"] = new
                {
                    summary = new
                    {
                        count = rooms.Count,
                        totalAreaSF = Math.Round(rooms.Sum(r => r.Area), 0)
                    },
                    rooms = roomData
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    exportedAt = DateTime.Now.ToString("o"),
                    data = data
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Analyze furniture layout and suggest optimizations based on clearances and accessibility.
        /// </summary>
        public static string OptimizeFurnitureLayout(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var roomIdParam = parameters?["roomId"];

                // Get rooms to analyze
                var roomCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0);

                if (roomIdParam != null)
                {
                    var roomId = new ElementId(roomIdParam.ToObject<int>());
                    roomCollector = roomCollector.Where(r => r.Id == roomId);
                }

                var rooms = roomCollector.ToList();
                var analysis = new List<object>();

                // Get all furniture
                var allFurniture = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Furniture)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                // ADA clearance requirements (in feet)
                const double minClearance = 3.0; // 36" min for wheelchair
                const double idealClearance = 5.0; // 60" for turning radius

                foreach (var room in rooms.Take(10)) // Limit to 10 rooms
                {
                    var roomBB = room.get_BoundingBox(null);
                    if (roomBB == null) continue;

                    // Find furniture in this room (by bounding box overlap)
                    var roomFurniture = allFurniture.Where(f =>
                    {
                        var fbb = f.get_BoundingBox(null);
                        if (fbb == null) return false;

                        return fbb.Min.X < roomBB.Max.X && fbb.Max.X > roomBB.Min.X &&
                               fbb.Min.Y < roomBB.Max.Y && fbb.Max.Y > roomBB.Min.Y;
                    }).ToList();

                    // Calculate room dimensions
                    double roomWidth = roomBB.Max.X - roomBB.Min.X;
                    double roomDepth = roomBB.Max.Y - roomBB.Min.Y;
                    double roomArea = room.Area;

                    // Calculate furniture area
                    double furnitureArea = 0;
                    foreach (var f in roomFurniture)
                    {
                        var fbb = f.get_BoundingBox(null);
                        if (fbb != null)
                        {
                            furnitureArea += (fbb.Max.X - fbb.Min.X) * (fbb.Max.Y - fbb.Min.Y);
                        }
                    }

                    double utilizationPercent = roomArea > 0 ? (furnitureArea / roomArea) * 100 : 0;

                    // Check clearances between furniture
                    var clearanceIssues = new List<object>();
                    for (int i = 0; i < roomFurniture.Count; i++)
                    {
                        var bb1 = roomFurniture[i].get_BoundingBox(null);
                        if (bb1 == null) continue;

                        for (int j = i + 1; j < roomFurniture.Count; j++)
                        {
                            var bb2 = roomFurniture[j].get_BoundingBox(null);
                            if (bb2 == null) continue;

                            // Calculate distance between bounding boxes
                            double dx = Math.Max(0, Math.Max(bb1.Min.X - bb2.Max.X, bb2.Min.X - bb1.Max.X));
                            double dy = Math.Max(0, Math.Max(bb1.Min.Y - bb2.Max.Y, bb2.Min.Y - bb1.Max.Y));
                            double distance = Math.Sqrt(dx * dx + dy * dy);

                            if (distance < minClearance && distance > 0.1)
                            {
                                clearanceIssues.Add(new
                                {
                                    element1 = roomFurniture[i].Name,
                                    element2 = roomFurniture[j].Name,
                                    clearanceFeet = Math.Round(distance, 2),
                                    issue = distance < 2.5 ? "Too close - blocks circulation" : "Below ADA minimum"
                                });
                            }
                        }
                    }

                    // Generate recommendations
                    var recommendations = new List<string>();

                    if (utilizationPercent > 60)
                        recommendations.Add("Room may be over-furnished. Consider removing items for better circulation.");
                    else if (utilizationPercent < 20 && roomFurniture.Count > 0)
                        recommendations.Add("Room appears under-utilized. Consider adding functional furniture.");

                    if (clearanceIssues.Count > 0)
                        recommendations.Add($"{clearanceIssues.Count} furniture items have clearance issues. Review spacing.");

                    if (roomFurniture.Count == 0)
                        recommendations.Add("Room has no furniture. Consider furnishing based on room function.");

                    analysis.Add(new
                    {
                        roomId = room.Id.Value,
                        roomName = room.Name,
                        roomNumber = room.Number,
                        level = room.Level?.Name,
                        dimensions = new
                        {
                            widthFeet = Math.Round(roomWidth, 1),
                            depthFeet = Math.Round(roomDepth, 1),
                            areaSF = Math.Round(roomArea, 1)
                        },
                        furniture = new
                        {
                            count = roomFurniture.Count,
                            areaSF = Math.Round(furnitureArea, 1),
                            utilizationPercent = Math.Round(utilizationPercent, 1)
                        },
                        clearanceIssues = clearanceIssues,
                        layoutRating = clearanceIssues.Count == 0 && utilizationPercent >= 20 && utilizationPercent <= 50 ? "Good" :
                                      clearanceIssues.Count <= 2 && utilizationPercent <= 60 ? "Acceptable" : "Review Recommended",
                        recommendations = recommendations
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roomsAnalyzed = analysis.Count,
                    clearanceStandards = new
                    {
                        minimumClearanceFeet = minClearance,
                        idealClearanceFeet = idealClearance,
                        note = "Based on ADA accessibility guidelines"
                    },
                    rooms = analysis
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Spacing and Alignment Validation

        /// <summary>
        /// Validates element spacing and alignment against configurable tolerances and code requirements.
        /// Checks doors, fixtures, and other elements for proper clearances and grid alignment.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - category (required): BuiltInCategory name to validate (e.g., "OST_Doors")
        /// - minClearance (optional): Minimum clearance between elements in feet (default: 3.0 for ADA)
        /// - checkAlignment (optional): Check alignment to grids/levels (default: true)
        /// - alignmentTolerance (optional): Tolerance for alignment checks in feet (default: 0.01)
        /// - viewId (optional): Limit to specific view
        /// </param>
        /// <returns>JSON response with validation violations and suggestions</returns>
        public static string ValidateElementSpacingAndAlignment(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["category"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "category is required"
                    });
                }

                string categoryName = parameters["category"].ToString();
                double minClearance = parameters["minClearance"]?.ToObject<double>() ?? 3.0; // ADA default
                bool checkAlignment = parameters["checkAlignment"]?.ToObject<bool>() ?? true;
                double alignmentTolerance = parameters["alignmentTolerance"]?.ToObject<double>() ?? 0.01;

                // Parse category
                BuiltInCategory bic;
                if (!Enum.TryParse(categoryName, out bic))
                {
                    if (!Enum.TryParse("OST_" + categoryName, out bic))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = $"Invalid category: {categoryName}" });
                    }
                }

                // Collect elements
                FilteredElementCollector collector;
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    collector = new FilteredElementCollector(doc, new ElementId(viewIdInt));
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var elements = collector
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToList();

                // Get grids and levels for alignment checking
                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .ToList();

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToList();

                var violations = new List<object>();
                var passed = new List<object>();
                int spacingViolations = 0;
                int alignmentViolations = 0;

                // Check spacing between elements
                for (int i = 0; i < elements.Count; i++)
                {
                    var elem1 = elements[i];
                    var loc1 = GetElementLocation(elem1);
                    if (loc1 == null) continue;

                    bool hasSpacingViolation = false;
                    var nearbyElements = new List<object>();

                    // Check against other elements
                    for (int j = i + 1; j < elements.Count; j++)
                    {
                        var elem2 = elements[j];
                        var loc2 = GetElementLocation(elem2);
                        if (loc2 == null) continue;

                        double distance = loc1.DistanceTo(loc2);

                        if (distance < minClearance)
                        {
                            hasSpacingViolation = true;
                            spacingViolations++;
                            nearbyElements.Add(new
                            {
                                elementId = (int)elem2.Id.Value,
                                name = elem2.Name,
                                distance = Math.Round(distance, 3),
                                requiredClearance = minClearance,
                                shortfall = Math.Round(minClearance - distance, 3)
                            });
                        }
                    }

                    // Check alignment
                    bool isAligned = true;
                    string alignedTo = null;

                    if (checkAlignment)
                    {
                        // Check alignment to grids
                        foreach (var grid in grids)
                        {
                            var gridCurve = grid.Curve;
                            double distToGrid = gridCurve.Distance(loc1);
                            if (distToGrid <= alignmentTolerance)
                            {
                                isAligned = true;
                                alignedTo = $"Grid {grid.Name}";
                                break;
                            }
                        }

                        // Check if aligned to level (Z coordinate)
                        foreach (var level in levels)
                        {
                            if (Math.Abs(loc1.Z - level.Elevation) <= alignmentTolerance)
                            {
                                alignedTo = alignedTo != null ? $"{alignedTo}, {level.Name}" : level.Name;
                                break;
                            }
                        }

                        if (alignedTo == null)
                        {
                            isAligned = false;
                            alignmentViolations++;
                        }
                    }

                    if (hasSpacingViolation || !isAligned)
                    {
                        violations.Add(new
                        {
                            elementId = (int)elem1.Id.Value,
                            name = elem1.Name,
                            location = new { x = Math.Round(loc1.X, 3), y = Math.Round(loc1.Y, 3), z = Math.Round(loc1.Z, 3) },
                            spacingViolation = hasSpacingViolation,
                            nearbyElements = nearbyElements,
                            alignmentViolation = !isAligned,
                            alignedTo = alignedTo,
                            suggestions = GetSuggestions(hasSpacingViolation, !isAligned, nearbyElements, grids, loc1)
                        });
                    }
                    else
                    {
                        passed.Add(new
                        {
                            elementId = (int)elem1.Id.Value,
                            name = elem1.Name,
                            alignedTo = alignedTo
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = categoryName,
                    settings = new
                    {
                        minClearance = minClearance,
                        checkAlignment = checkAlignment,
                        alignmentTolerance = alignmentTolerance
                    },
                    summary = new
                    {
                        totalElements = elements.Count,
                        passedCount = passed.Count,
                        violationCount = violations.Count,
                        spacingViolations = spacingViolations,
                        alignmentViolations = alignmentViolations
                    },
                    violations = violations,
                    passed = passed.Take(20).ToList(),
                    message = violations.Count == 0 ?
                        $"All {elements.Count} elements pass spacing and alignment checks" :
                        $"Found {violations.Count} violations ({spacingViolations} spacing, {alignmentViolations} alignment)"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets element location point
        /// </summary>
        private static XYZ GetElementLocation(Element elem)
        {
            var loc = elem.Location;
            if (loc is LocationPoint lp)
                return lp.Point;
            if (loc is LocationCurve lc)
                return lc.Curve.Evaluate(0.5, true);

            // Try bounding box center
            var bb = elem.get_BoundingBox(null);
            if (bb != null)
                return (bb.Min + bb.Max) / 2;

            return null;
        }

        /// <summary>
        /// Generates fix suggestions for violations
        /// </summary>
        private static List<string> GetSuggestions(bool hasSpacing, bool hasAlignment, List<object> nearbyElements, List<Grid> grids, XYZ location)
        {
            var suggestions = new List<string>();

            if (hasSpacing)
            {
                suggestions.Add("Consider relocating element to increase clearance from nearby elements");
                if (nearbyElements.Count > 0)
                {
                    suggestions.Add($"Closest conflicting element is {((dynamic)nearbyElements[0]).distance:F2} ft away");
                }
            }

            if (hasAlignment && grids.Count > 0)
            {
                // Find nearest grid
                double minDist = double.MaxValue;
                string nearestGrid = null;
                foreach (var grid in grids)
                {
                    double dist = grid.Curve.Distance(location);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestGrid = grid.Name;
                    }
                }
                if (nearestGrid != null)
                {
                    suggestions.Add($"Nearest grid is '{nearestGrid}' at {minDist:F2} ft - consider aligning");
                }
            }

            return suggestions;
        }

        #endregion
    }
}
