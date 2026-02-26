using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Transaction Control Methods - Enable AI to manage undo/redo and transaction groups.
    /// Provides checkpoint system for safe, reversible operations.
    /// </summary>
    public static class TransactionMethods
    {
        // Store for transaction group management
        private static TransactionGroup _activeTransactionGroup;
        private static string _activeGroupName;
        private static readonly List<string> _checkpointHistory = new List<string>();

        #region Start Transaction Group

        /// <summary>
        /// Start a transaction group to combine multiple operations into one undoable action.
        /// </summary>
        [MCPMethod("startTransactionGroup", Category = "Transaction", Description = "Start a transaction group to combine multiple operations into one undoable action")]
        public static string StartTransactionGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                if (_activeTransactionGroup != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "A transaction group is already active",
                        activeGroup = _activeGroupName
                    });
                }

                var groupName = parameters["name"]?.ToString() ?? $"AI Operation {DateTime.Now:HH:mm:ss}";

                _activeTransactionGroup = new TransactionGroup(doc, groupName);
                _activeTransactionGroup.Start();
                _activeGroupName = groupName;
                _checkpointHistory.Clear();
                _checkpointHistory.Add($"Started: {groupName}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupName = groupName,
                    message = $"Transaction group '{groupName}' started. All operations will be combined into one undo."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error starting transaction group");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Commit Transaction Group

        /// <summary>
        /// Commit the active transaction group, finalizing all operations.
        /// </summary>
        [MCPMethod("commitTransactionGroup", Category = "Transaction", Description = "Commit the active transaction group, finalizing all operations")]
        public static string CommitTransactionGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (_activeTransactionGroup == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active transaction group"
                    });
                }

                var groupName = _activeGroupName;
                _activeTransactionGroup.Assimilate();
                _activeTransactionGroup.Dispose();
                _activeTransactionGroup = null;
                _activeGroupName = null;

                var checkpoints = new List<string>(_checkpointHistory);
                _checkpointHistory.Clear();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupName = groupName,
                    checkpoints = checkpoints,
                    message = $"Transaction group '{groupName}' committed. Can be undone as single action."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error committing transaction group");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Rollback Transaction Group

        /// <summary>
        /// Rollback the active transaction group, undoing all operations since it started.
        /// </summary>
        [MCPMethod("rollbackTransactionGroup", Category = "Transaction", Description = "Rollback the active transaction group, undoing all operations since it started")]
        public static string RollbackTransactionGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (_activeTransactionGroup == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active transaction group"
                    });
                }

                var groupName = _activeGroupName;
                _activeTransactionGroup.RollBack();
                _activeTransactionGroup.Dispose();
                _activeTransactionGroup = null;
                _activeGroupName = null;

                var checkpoints = new List<string>(_checkpointHistory);
                _checkpointHistory.Clear();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupName = groupName,
                    rolledBackCheckpoints = checkpoints,
                    message = $"Transaction group '{groupName}' rolled back. All operations undone."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error rolling back transaction group");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Add Checkpoint

        /// <summary>
        /// Add a named checkpoint to track progress within a transaction group.
        /// </summary>
        [MCPMethod("addCheckpoint", Category = "Transaction", Description = "Add a named checkpoint to track progress within a transaction group")]
        public static string AddCheckpoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var checkpointName = parameters["name"]?.ToString() ?? $"Checkpoint {_checkpointHistory.Count}";

                _checkpointHistory.Add($"{DateTime.Now:HH:mm:ss} - {checkpointName}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkpoint = checkpointName,
                    checkpointCount = _checkpointHistory.Count,
                    activeGroup = _activeGroupName,
                    message = $"Checkpoint '{checkpointName}' added"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding checkpoint");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Transaction Status

        /// <summary>
        /// Get the current status of transaction groups and checkpoints.
        /// </summary>
        [MCPMethod("getTransactionStatus", Category = "Transaction", Description = "Get the current status of transaction groups and checkpoints")]
        public static string GetTransactionStatus(UIApplication uiApp, JObject parameters)
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasActiveGroup = _activeTransactionGroup != null,
                    activeGroupName = _activeGroupName,
                    checkpointCount = _checkpointHistory.Count,
                    checkpoints = _checkpointHistory.ToList()
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting transaction status");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Undo History

        /// <summary>
        /// Get available undo operations from Revit's undo stack.
        /// Note: Revit API has limited access to undo stack details.
        /// </summary>
        [MCPMethod("getUndoHistory", Category = "Transaction", Description = "Get available undo operations from Revit's undo stack")]
        public static string GetUndoHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Note: Revit doesn't expose undo stack directly through API
                // We can only track what we've done through checkpoints

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    note = "Revit API doesn't expose full undo history",
                    checkpointsInSession = _checkpointHistory.ToList(),
                    activeTransactionGroup = _activeGroupName,
                    suggestion = "Use transaction groups to create undoable operation bundles"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting undo history");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Execute With Undo

        /// <summary>
        /// Execute a method with explicit undo point creation.
        /// </summary>
        [MCPMethod("executeWithUndo", Category = "Transaction", Description = "Execute a method with explicit undo point creation")]
        public static string ExecuteWithUndo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var methodName = parameters["method"]?.ToString();
                var methodParams = parameters["params"] as JObject ?? new JObject();
                var undoName = parameters["undoName"]?.ToString() ?? methodName;

                if (string.IsNullOrEmpty(methodName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "method name required" });
                }

                // Check if method exists
                if (!MCPServer.HasMethod(methodName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Method '{methodName}' not found" });
                }

                // Execute the method - it will create its own transaction internally
                // We track it as a checkpoint
                _checkpointHistory.Add($"{DateTime.Now:HH:mm:ss} - Execute: {undoName}");

                var result = MCPServer.ExecuteMethodDirect(methodName, methodParams);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    executedMethod = methodName,
                    undoName = undoName,
                    result = JsonConvert.DeserializeObject(result),
                    message = $"Executed '{methodName}' with undo point"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing with undo");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Batch Execute

        /// <summary>
        /// Execute multiple methods as a single undoable operation.
        /// </summary>
        [MCPMethod("batchExecute", Category = "Transaction", Description = "Execute multiple methods as a single undoable operation")]
        public static string BatchExecute(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var operations = parameters["operations"] as JArray;
                var batchName = parameters["batchName"]?.ToString() ?? "Batch Operation";
                var stopOnError = parameters["stopOnError"]?.Value<bool>() ?? true;

                if (operations == null || operations.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "operations array required" });
                }

                var results = new List<object>();
                var successCount = 0;
                var failCount = 0;

                // Create a transaction group for the batch
                using (var transGroup = new TransactionGroup(doc, batchName))
                {
                    transGroup.Start();

                    foreach (var op in operations)
                    {
                        var methodName = op["method"]?.ToString();
                        var methodParams = op["params"] as JObject ?? new JObject();

                        try
                        {
                            if (string.IsNullOrEmpty(methodName))
                            {
                                results.Add(new { method = "(empty)", success = false, error = "Method name required" });
                                failCount++;
                                if (stopOnError)
                                {
                                    transGroup.RollBack();
                                    break;
                                }
                                continue;
                            }

                            var result = MCPServer.ExecuteMethodDirect(methodName, methodParams);
                            var parsed = JsonConvert.DeserializeObject<dynamic>(result);

                            results.Add(new
                            {
                                method = methodName,
                                success = (bool)(parsed?.success ?? false),
                                result = parsed
                            });

                            if ((bool)(parsed?.success ?? false))
                            {
                                successCount++;
                            }
                            else
                            {
                                failCount++;
                                if (stopOnError)
                                {
                                    transGroup.RollBack();
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            results.Add(new { method = methodName, success = false, error = ex.Message });
                            failCount++;
                            if (stopOnError)
                            {
                                transGroup.RollBack();
                                break;
                            }
                        }
                    }

                    if (failCount == 0 || !stopOnError)
                    {
                        transGroup.Assimilate();
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failCount == 0,
                    batchName = batchName,
                    totalOperations = operations.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results,
                    message = failCount == 0 ? $"Batch '{batchName}' completed successfully" : $"Batch had {failCount} failures"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in batch execute");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Safe Execute (with auto-rollback on failure)

        /// <summary>
        /// Execute a method with automatic rollback if it fails.
        /// </summary>
        [MCPMethod("safeExecute", Category = "Transaction", Description = "Execute a method with automatic rollback if it fails")]
        public static string SafeExecute(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var methodName = parameters["method"]?.ToString();
                var methodParams = parameters["params"] as JObject ?? new JObject();
                var operationName = parameters["operationName"]?.ToString() ?? methodName;

                if (string.IsNullOrEmpty(methodName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "method name required" });
                }

                using (var transGroup = new TransactionGroup(doc, operationName))
                {
                    transGroup.Start();

                    try
                    {
                        var result = MCPServer.ExecuteMethodDirect(methodName, methodParams);
                        var parsed = JsonConvert.DeserializeObject<dynamic>(result);

                        if ((bool)(parsed?.success ?? false))
                        {
                            transGroup.Assimilate();
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                method = methodName,
                                result = parsed,
                                wasRolledBack = false,
                                message = $"'{operationName}' completed successfully"
                            });
                        }
                        else
                        {
                            transGroup.RollBack();
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                method = methodName,
                                result = parsed,
                                wasRolledBack = true,
                                message = $"'{operationName}' failed and was rolled back"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        transGroup.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            method = methodName,
                            error = ex.Message,
                            wasRolledBack = true,
                            message = $"'{operationName}' threw exception and was rolled back"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in safe execute");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Verify and Rollback

        /// <summary>
        /// Execute a method, then run verification. Rollback if verification fails.
        /// </summary>
        [MCPMethod("verifyAndRollback", Category = "Transaction", Description = "Execute a method, then run verification; rollback if verification fails")]
        public static string VerifyAndRollback(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var methodName = parameters["method"]?.ToString();
                var methodParams = parameters["params"] as JObject ?? new JObject();
                var verifyMethod = parameters["verifyMethod"]?.ToString();
                var verifyParams = parameters["verifyParams"] as JObject ?? new JObject();
                var operationName = parameters["operationName"]?.ToString() ?? methodName;

                if (string.IsNullOrEmpty(methodName) || string.IsNullOrEmpty(verifyMethod))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Both method and verifyMethod are required"
                    });
                }

                using (var transGroup = new TransactionGroup(doc, operationName))
                {
                    transGroup.Start();

                    // Execute main operation
                    var mainResult = MCPServer.ExecuteMethodDirect(methodName, methodParams);
                    var mainParsed = JsonConvert.DeserializeObject<dynamic>(mainResult);

                    if (!(bool)(mainParsed?.success ?? false))
                    {
                        transGroup.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            phase = "execution",
                            mainResult = mainParsed,
                            wasRolledBack = true,
                            message = "Main operation failed, rolled back"
                        });
                    }

                    // Execute verification
                    var verifyResult = MCPServer.ExecuteMethodDirect(verifyMethod, verifyParams);
                    var verifyParsed = JsonConvert.DeserializeObject<dynamic>(verifyResult);

                    if ((bool)(verifyParsed?.success ?? false))
                    {
                        transGroup.Assimilate();
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            phase = "complete",
                            mainResult = mainParsed,
                            verifyResult = verifyParsed,
                            wasRolledBack = false,
                            message = "Operation and verification both succeeded"
                        });
                    }
                    else
                    {
                        transGroup.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            phase = "verification",
                            mainResult = mainParsed,
                            verifyResult = verifyParsed,
                            wasRolledBack = true,
                            message = "Verification failed, changes rolled back"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in verify and rollback");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
