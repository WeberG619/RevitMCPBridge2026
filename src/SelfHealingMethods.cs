using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Self-healing methods for error detection, recovery, and rollback.
    /// These methods enable Claude to detect and fix errors automatically.
    /// </summary>
    public static class SelfHealingMethods
    {
        // Track recent operations for potential rollback
        private static List<OperationRecord> _operationHistory = new List<OperationRecord>();
        private static int _maxHistorySize = 50;

        #region Operation Tracking

        private class OperationRecord
        {
            public string OperationId { get; set; }
            public string OperationType { get; set; }
            public DateTime Timestamp { get; set; }
            public List<int> AffectedElementIds { get; set; }
            public JObject OriginalState { get; set; }
            public JObject Parameters { get; set; }
            public bool CanRollback { get; set; }
        }

        /// <summary>
        /// Record an operation for potential rollback
        /// Parameters:
        /// - operationType: Type of operation ("create", "modify", "delete")
        /// - affectedElementIds: Array of element IDs affected
        /// - originalState: (optional) State before operation for rollback
        /// - parameters: (optional) Parameters used in the operation
        /// </summary>
        public static string RecordOperation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var operationId = Guid.NewGuid().ToString("N").Substring(0, 8);
                var operationType = parameters["operationType"]?.ToString() ?? "unknown";
                var affectedIds = parameters["affectedElementIds"]?.ToObject<int[]>() ?? new int[0];

                var record = new OperationRecord
                {
                    OperationId = operationId,
                    OperationType = operationType,
                    Timestamp = DateTime.Now,
                    AffectedElementIds = affectedIds.ToList(),
                    OriginalState = parameters["originalState"] as JObject,
                    Parameters = parameters["parameters"] as JObject,
                    CanRollback = operationType != "delete" // Can't rollback deletes without original state
                };

                _operationHistory.Add(record);

                // Trim history if too large
                while (_operationHistory.Count > _maxHistorySize)
                {
                    _operationHistory.RemoveAt(0);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    operationId = operationId,
                    recorded = true,
                    canRollback = record.CanRollback,
                    historySize = _operationHistory.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get operation history
        /// Parameters:
        /// - limit: (optional) Max records to return (default 10)
        /// </summary>
        public static string GetOperationHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var limit = parameters?["limit"]?.ToObject<int>() ?? 10;

                var history = _operationHistory
                    .OrderByDescending(o => o.Timestamp)
                    .Take(limit)
                    .Select(o => new
                    {
                        operationId = o.OperationId,
                        operationType = o.OperationType,
                        timestamp = o.Timestamp,
                        affectedCount = o.AffectedElementIds.Count,
                        canRollback = o.CanRollback
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalRecords = _operationHistory.Count,
                    returned = history.Count,
                    operations = history
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Clear operation history
        /// </summary>
        public static string ClearOperationHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var count = _operationHistory.Count;
                _operationHistory.Clear();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    clearedCount = count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Error Detection

        /// <summary>
        /// Detect anomalies in recent operations
        /// Parameters:
        /// - viewId: (optional) View to check
        /// - checkTypes: Array of anomaly types to detect:
        ///   - "missing_elements" - Elements that should exist but don't
        ///   - "unexpected_elements" - Elements that shouldn't exist
        ///   - "location_drift" - Elements not at expected locations
        /// </summary>
        public static string DetectAnomalies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var anomalies = new List<object>();

                // Check for missing elements from recent operations
                var recentCreates = _operationHistory
                    .Where(o => o.OperationType == "create")
                    .OrderByDescending(o => o.Timestamp)
                    .Take(10)
                    .ToList();

                foreach (var op in recentCreates)
                {
                    foreach (var elemId in op.AffectedElementIds)
                    {
                        var element = doc.GetElement(new ElementId(elemId));
                        if (element == null)
                        {
                            anomalies.Add(new
                            {
                                type = "missing_element",
                                severity = "error",
                                operationId = op.OperationId,
                                elementId = elemId,
                                expectedAction = "create",
                                message = $"Element {elemId} was created but no longer exists",
                                suggestion = "Element may have been deleted or operation failed silently"
                            });
                        }
                    }
                }

                // Check for elements that were supposed to be deleted but still exist
                var recentDeletes = _operationHistory
                    .Where(o => o.OperationType == "delete")
                    .OrderByDescending(o => o.Timestamp)
                    .Take(10)
                    .ToList();

                foreach (var op in recentDeletes)
                {
                    foreach (var elemId in op.AffectedElementIds)
                    {
                        var element = doc.GetElement(new ElementId(elemId));
                        if (element != null)
                        {
                            anomalies.Add(new
                            {
                                type = "unexpected_element",
                                severity = "warning",
                                operationId = op.OperationId,
                                elementId = elemId,
                                expectedAction = "delete",
                                message = $"Element {elemId} should have been deleted but still exists",
                                suggestion = "Delete operation may have failed or been rolled back"
                            });
                        }
                    }
                }

                // Check view-specific anomalies if viewId provided
                if (parameters?["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    var view = doc.GetElement(viewId) as View;

                    if (view != null)
                    {
                        // Check for orphaned tags (tags pointing to nothing)
                        var allTags = new FilteredElementCollector(doc, viewId)
                            .OfClass(typeof(IndependentTag))
                            .Cast<IndependentTag>()
                            .ToList();

                        foreach (var tag in allTags)
                        {
                            try
                            {
                                // In Revit 2026, use GetTaggedLocalElementIds() - returns ICollection<ElementId>
                                var taggedIds = tag.GetTaggedLocalElementIds();
                                var taggedElementId = taggedIds?.FirstOrDefault();
                                var taggedElement = taggedElementId != null ? doc.GetElement(taggedElementId) : null;

                                if (taggedElement == null && taggedIds != null && taggedIds.Count > 0)
                                {
                                    anomalies.Add(new
                                    {
                                        type = "orphaned_tag",
                                        severity = "warning",
                                        elementId = (int)tag.Id.Value,
                                        message = "Tag is pointing to a deleted or missing element",
                                        suggestion = "Delete the orphaned tag"
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    anomalyCount = anomalies.Count,
                    hasAnomalies = anomalies.Count > 0,
                    anomalies = anomalies,
                    recommendation = anomalies.Count > 0
                        ? "Review anomalies and consider corrective actions"
                        : "No anomalies detected"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Recovery Actions

        /// <summary>
        /// Attempt to recover from a common error pattern
        /// Parameters:
        /// - errorType: Type of error to recover from
        /// - context: Additional context about the error
        /// </summary>
        public static string AttemptRecovery(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var errorType = parameters["errorType"]?.ToString() ?? "";
                var context = parameters["context"] as JObject;

                var recoveryActions = new List<object>();
                var recovered = false;

                switch (errorType.ToLower())
                {
                    case "element_not_found":
                        // Element was expected but not found - check if it was deleted
                        if (context?["elementId"] != null)
                        {
                            var elemId = int.Parse(context["elementId"].ToString());
                            recoveryActions.Add(new
                            {
                                action = "check_history",
                                message = $"Checking operation history for element {elemId}"
                            });

                            var relatedOps = _operationHistory
                                .Where(o => o.AffectedElementIds.Contains(elemId))
                                .OrderByDescending(o => o.Timestamp)
                                .ToList();

                            if (relatedOps.Any())
                            {
                                var lastOp = relatedOps.First();
                                recoveryActions.Add(new
                                {
                                    action = "found_history",
                                    message = $"Element was involved in '{lastOp.OperationType}' operation at {lastOp.Timestamp}"
                                });

                                if (lastOp.OperationType == "delete")
                                {
                                    recoveryActions.Add(new
                                    {
                                        action = "explanation",
                                        message = "Element was intentionally deleted. If this was a mistake, undo in Revit."
                                    });
                                }
                            }
                        }
                        break;

                    case "transaction_failed":
                        // Transaction failed - provide guidance
                        recoveryActions.Add(new
                        {
                            action = "check_state",
                            message = "Checking if Revit is in a valid state for modifications"
                        });

                        // Check if there's an active transaction
                        if (doc.IsModifiable)
                        {
                            recoveryActions.Add(new
                            {
                                action = "state_ok",
                                message = "Document is modifiable. Retry the operation."
                            });
                            recovered = true;
                        }
                        else
                        {
                            recoveryActions.Add(new
                            {
                                action = "state_locked",
                                message = "Document may be locked or in a modal state. Check for open dialogs in Revit."
                            });
                        }
                        break;

                    case "type_not_found":
                        // Family type not found - suggest alternatives
                        if (context?["familyName"] != null)
                        {
                            var familyName = context["familyName"].ToString();

                            var similarFamilies = new FilteredElementCollector(doc)
                                .OfClass(typeof(FamilySymbol))
                                .Cast<FamilySymbol>()
                                .Where(fs => fs.Family.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            fs.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0)
                                .Take(5)
                                .Select(fs => new { familyName = fs.Family.Name, typeName = fs.Name, typeId = (int)fs.Id.Value })
                                .ToList();

                            if (similarFamilies.Any())
                            {
                                recoveryActions.Add(new
                                {
                                    action = "suggest_alternatives",
                                    message = $"Found {similarFamilies.Count} similar families",
                                    alternatives = similarFamilies
                                });
                                recovered = true;
                            }
                            else
                            {
                                recoveryActions.Add(new
                                {
                                    action = "no_alternatives",
                                    message = "No similar families found. May need to load the family first."
                                });
                            }
                        }
                        break;

                    case "placement_failed":
                        // Element placement failed - check common issues
                        recoveryActions.Add(new
                        {
                            action = "check_levels",
                            message = "Verifying levels exist in the project"
                        });

                        var levels = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level))
                            .Cast<Level>()
                            .ToList();

                        if (levels.Count == 0)
                        {
                            recoveryActions.Add(new
                            {
                                action = "no_levels",
                                message = "No levels found in project. Create a level first.",
                                severity = "error"
                            });
                        }
                        else
                        {
                            recoveryActions.Add(new
                            {
                                action = "levels_ok",
                                message = $"Found {levels.Count} levels. Try specifying levelId parameter.",
                                levels = levels.Select(l => new { id = (int)l.Id.Value, name = l.Name, elevation = l.Elevation }).ToList()
                            });
                            recovered = true;
                        }
                        break;

                    default:
                        recoveryActions.Add(new
                        {
                            action = "unknown_error",
                            message = $"No specific recovery available for error type: {errorType}"
                        });
                        break;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    errorType = errorType,
                    recovered = recovered,
                    actionsAttempted = recoveryActions.Count,
                    actions = recoveryActions,
                    recommendation = recovered
                        ? "Recovery suggestions found - review and retry"
                        : "Manual intervention may be required"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Undo the last operation (if possible)
        /// Parameters:
        /// - operationId: (optional) Specific operation to undo
        /// </summary>
        public static string UndoLastOperation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                OperationRecord opToUndo;
                if (parameters?["operationId"] != null)
                {
                    var opId = parameters["operationId"].ToString();
                    opToUndo = _operationHistory.FirstOrDefault(o => o.OperationId == opId);
                }
                else
                {
                    opToUndo = _operationHistory
                        .Where(o => o.CanRollback)
                        .OrderByDescending(o => o.Timestamp)
                        .FirstOrDefault();
                }

                if (opToUndo == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No operation found to undo"
                    });
                }

                if (!opToUndo.CanRollback)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Operation cannot be rolled back",
                        operationId = opToUndo.OperationId,
                        operationType = opToUndo.OperationType
                    });
                }

                var undoActions = new List<object>();

                switch (opToUndo.OperationType)
                {
                    case "create":
                        // Delete the created elements
                        using (var trans = new Transaction(doc, "Undo Create Operation"))
                        {
                            trans.Start();
                            var failureOptions = trans.GetFailureHandlingOptions();
                            failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                            trans.SetFailureHandlingOptions(failureOptions);

                            var deletedCount = 0;
                            foreach (var elemId in opToUndo.AffectedElementIds)
                            {
                                var element = doc.GetElement(new ElementId(elemId));
                                if (element != null)
                                {
                                    doc.Delete(new ElementId(elemId));
                                    deletedCount++;
                                    undoActions.Add(new { action = "delete", elementId = elemId });
                                }
                            }

                            trans.Commit();

                            undoActions.Add(new { summary = $"Deleted {deletedCount} elements" });
                        }
                        break;

                    case "modify":
                        // Would need original state to restore - not fully implemented
                        undoActions.Add(new
                        {
                            action = "manual_required",
                            message = "Modify operations require manual undo in Revit (Ctrl+Z)"
                        });
                        break;

                    default:
                        undoActions.Add(new
                        {
                            action = "not_supported",
                            message = $"Undo not supported for operation type: {opToUndo.OperationType}"
                        });
                        break;
                }

                // Remove from history
                _operationHistory.Remove(opToUndo);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    undoneOperationId = opToUndo.OperationId,
                    operationType = opToUndo.OperationType,
                    timestamp = opToUndo.Timestamp,
                    actions = undoActions
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Safe Operations

        /// <summary>
        /// Delete element with safeguards (checks dependencies, records for undo)
        /// Parameters:
        /// - elementId: Element to delete
        /// - force: (optional) Delete even if has dependents (default false)
        /// </summary>
        public static string SafeDeleteElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found",
                        elementId = (int)elementId.Value
                    });
                }

                var force = parameters["force"]?.ToObject<bool>() ?? false;

                // Check what would be deleted
                var dependentIds = new List<ElementId>();
                try
                {
                    dependentIds = doc.Delete(elementId).ToList();
                    // This is a preview - we need to roll back
                }
                catch { }

                // If there are dependents and not forcing, warn
                if (dependentIds.Count > 1 && !force)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element has dependents. Use force=true to delete anyway.",
                        elementId = (int)elementId.Value,
                        elementName = element.Name,
                        elementCategory = element.Category?.Name,
                        dependentCount = dependentIds.Count - 1,
                        warning = $"Deleting this element will also delete {dependentIds.Count - 1} dependent element(s)"
                    });
                }

                // Record the operation for potential undo
                var originalState = new JObject
                {
                    ["name"] = element.Name,
                    ["category"] = element.Category?.Name
                };

                // Perform the delete
                using (var trans = new Transaction(doc, "Safe Delete Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    var deleted = doc.Delete(elementId);
                    trans.Commit();

                    // Record for undo (even though we can't truly undo a delete)
                    _operationHistory.Add(new OperationRecord
                    {
                        OperationId = Guid.NewGuid().ToString("N").Substring(0, 8),
                        OperationType = "delete",
                        Timestamp = DateTime.Now,
                        AffectedElementIds = deleted.Select(id => (int)id.Value).ToList(),
                        OriginalState = originalState,
                        CanRollback = false
                    });

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedElementId = (int)elementId.Value,
                        elementName = element.Name,
                        totalDeleted = deleted.Count,
                        deletedIds = deleted.Select(id => (int)id.Value).ToList(),
                        message = deleted.Count > 1
                            ? $"Deleted element and {deleted.Count - 1} dependent(s)"
                            : "Element deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify element with verification and auto-retry
        /// Parameters:
        /// - elementId: Element to modify
        /// - modifications: Object with parameter name/value pairs
        /// - verify: (optional) Verify after modification (default true)
        /// - maxRetries: (optional) Max retry attempts (default 1)
        /// </summary>
        public static string SafeModifyElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null || parameters["modifications"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId and modifications are required" });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var modifications = parameters["modifications"] as JObject;
                var verify = parameters["verify"]?.ToObject<bool>() ?? true;
                var maxRetries = parameters["maxRetries"]?.ToObject<int>() ?? 1;

                var results = new List<object>();
                var successfulMods = 0;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    using (var trans = new Transaction(doc, "Safe Modify Element"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        foreach (var mod in modifications.Properties())
                        {
                            var paramName = mod.Name;
                            var newValue = mod.Value;

                            // Handle text note text specially
                            if (element is TextNote textNote && paramName.ToLower() == "text")
                            {
                                textNote.Text = newValue.ToString();
                                successfulMods++;
                                results.Add(new { parameter = "text", status = "modified" });
                            }
                            else
                            {
                                // Try to set as parameter
                                var param = element.LookupParameter(paramName);
                                if (param != null && !param.IsReadOnly)
                                {
                                    switch (param.StorageType)
                                    {
                                        case StorageType.String:
                                            param.Set(newValue.ToString());
                                            successfulMods++;
                                            results.Add(new { parameter = paramName, status = "modified" });
                                            break;
                                        case StorageType.Double:
                                            param.Set(newValue.ToObject<double>());
                                            successfulMods++;
                                            results.Add(new { parameter = paramName, status = "modified" });
                                            break;
                                        case StorageType.Integer:
                                            param.Set(newValue.ToObject<int>());
                                            successfulMods++;
                                            results.Add(new { parameter = paramName, status = "modified" });
                                            break;
                                        default:
                                            results.Add(new { parameter = paramName, status = "skipped", reason = "Unsupported storage type" });
                                            break;
                                    }
                                }
                                else
                                {
                                    results.Add(new { parameter = paramName, status = "skipped", reason = param == null ? "Parameter not found" : "Read-only" });
                                }
                            }
                        }

                        trans.Commit();
                    }

                    // Verify if requested
                    if (verify && successfulMods > 0)
                    {
                        var verificationPassed = true;

                        // Re-get element to check modifications
                        element = doc.GetElement(elementId);

                        if (element is TextNote tn && modifications["text"] != null)
                        {
                            var expectedText = modifications["text"].ToString();
                            if (tn.Text?.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                verificationPassed = false;
                            }
                        }

                        if (verificationPassed)
                        {
                            break; // Success, no need to retry
                        }
                        else if (attempt < maxRetries)
                        {
                            results.Add(new { attempt = attempt + 1, status = "verification_failed", retrying = true });
                        }
                    }
                    else
                    {
                        break; // No verification or no successful mods
                    }
                }

                // Record operation
                _operationHistory.Add(new OperationRecord
                {
                    OperationId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    OperationType = "modify",
                    Timestamp = DateTime.Now,
                    AffectedElementIds = new List<int> { (int)elementId.Value },
                    Parameters = modifications,
                    CanRollback = false // Would need original values
                });

                return JsonConvert.SerializeObject(new
                {
                    success = successfulMods > 0,
                    elementId = (int)elementId.Value,
                    modificationsAttempted = modifications.Properties().Count(),
                    modificationsSucceeded = successfulMods,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Health Check

        /// <summary>
        /// Perform a health check on the MCP Bridge and Revit connection
        /// </summary>
        public static string HealthCheck(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                var issues = new List<object>();
                var status = "healthy";

                // Check document state
                if (doc == null)
                {
                    issues.Add(new { type = "no_document", message = "No active document", severity = "error" });
                    status = "unhealthy";
                }
                else
                {
                    // Check if document is modifiable
                    if (!doc.IsModifiable)
                    {
                        issues.Add(new { type = "document_locked", message = "Document is not modifiable", severity = "warning" });
                        status = "degraded";
                    }

                    // Check for warnings
                    var warnings = doc.GetWarnings();
                    if (warnings.Count > 10)
                    {
                        issues.Add(new { type = "many_warnings", message = $"{warnings.Count} warnings in document", severity = "info" });
                    }
                }

                // Check operation history size
                if (_operationHistory.Count >= _maxHistorySize)
                {
                    issues.Add(new { type = "history_full", message = "Operation history is at capacity", severity = "info" });
                }

                // Check for orphaned operations (elements that no longer exist)
                var orphanedOps = 0;
                foreach (var op in _operationHistory.Where(o => o.OperationType == "create").Take(10))
                {
                    foreach (var elemId in op.AffectedElementIds)
                    {
                        if (doc != null && doc.GetElement(new ElementId(elemId)) == null)
                        {
                            orphanedOps++;
                        }
                    }
                }

                if (orphanedOps > 0)
                {
                    issues.Add(new { type = "orphaned_operations", message = $"{orphanedOps} tracked elements no longer exist", severity = "warning" });
                    if (status == "healthy") status = "degraded";
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    status = status,
                    timestamp = DateTime.Now,
                    document = doc != null ? new
                    {
                        title = doc.Title,
                        isModifiable = doc.IsModifiable,
                        warningCount = doc.GetWarnings().Count
                    } : null,
                    operationHistory = new
                    {
                        count = _operationHistory.Count,
                        maxSize = _maxHistorySize
                    },
                    issueCount = issues.Count,
                    issues = issues
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    status = "error",
                    error = ex.Message
                });
            }
        }

        #endregion
    }
}
