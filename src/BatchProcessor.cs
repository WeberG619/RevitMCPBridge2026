using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Batch task processor for autonomous Revit automation.
    /// Executes a queue of tasks sequentially, with progress tracking,
    /// error handling, and state persistence for resumable execution.
    /// </summary>
    public static class BatchProcessor
    {
        /// <summary>
        /// Task status enumeration
        /// </summary>
        public enum TaskStatus
        {
            Pending,
            InProgress,
            Completed,
            Failed,
            Skipped
        }

        /// <summary>
        /// Error handling strategy
        /// </summary>
        public enum ErrorStrategy
        {
            StopOnError,      // Stop batch on first error
            LogAndContinue,   // Log error and continue to next task
            RetryOnce,        // Retry failed task once before continuing
            SkipAndContinue   // Skip failed task and continue
        }

        /// <summary>
        /// Represents a single task in the batch
        /// </summary>
        public class BatchTask
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Method { get; set; }
            public JObject Parameters { get; set; }
            public TaskStatus Status { get; set; } = TaskStatus.Pending;
            public string Result { get; set; }
            public string ErrorMessage { get; set; }
            public DateTime? StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public double DurationSeconds => (EndTime.HasValue && StartTime.HasValue)
                ? (EndTime.Value - StartTime.Value).TotalSeconds : 0;
            public int RetryCount { get; set; } = 0;
            public int MaxRetries { get; set; } = 1;
            public int TimeoutSeconds { get; set; } = 120;
            public bool SwitchToViewAfter { get; set; } = true;
            public int? ViewIdToSwitch { get; set; }

            // Verification results
            public bool? Verified { get; set; }
            public string VerificationMessage { get; set; }
        }

        /// <summary>
        /// Represents a batch of tasks to execute
        /// </summary>
        public class TaskBatch
        {
            public string BatchId { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public ErrorStrategy OnError { get; set; } = ErrorStrategy.LogAndContinue;
            public int CheckpointEvery { get; set; } = 5;  // Save state every N tasks
            public List<BatchTask> Tasks { get; set; } = new List<BatchTask>();
            public int CurrentTaskIndex { get; set; } = 0;
            public string LogFilePath { get; set; }
            public string StateFilePath { get; set; }

            // Statistics
            public int TotalTasks => Tasks.Count;
            public int CompletedTasks => Tasks.Count(t => t.Status == TaskStatus.Completed);
            public int FailedTasks => Tasks.Count(t => t.Status == TaskStatus.Failed);
            public int SkippedTasks => Tasks.Count(t => t.Status == TaskStatus.Skipped);
            public int PendingTasks => Tasks.Count(t => t.Status == TaskStatus.Pending);
            public double ProgressPercent => TotalTasks > 0 ? (double)(CompletedTasks + FailedTasks + SkippedTasks) / TotalTasks * 100 : 0;
        }

        // Current running batch
        private static TaskBatch _currentBatch;
        private static bool _isRunning = false;
        private static bool _isPaused = false;

        // Result verifier for post-operation validation
        private static ResultVerifier _verifier;

        /// <summary>
        /// Initialize the result verifier
        /// </summary>
        private static void EnsureVerifierInitialized()
        {
            if (_verifier == null)
            {
                _verifier = new ResultVerifier();
                // Set up executor to use MCPServer.ExecuteMethod
                _verifier.SetExecutor((method, parameters) =>
                {
                    // This runs in Revit context already, so we call synchronously
                    return Task.FromResult(MCPServer.ExecuteMethodDirect(method, parameters));
                });
            }
        }

        /// <summary>
        /// Load a batch from a JSON file
        /// </summary>
        public static string LoadBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (parameters["filePath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filePath is required"
                    });
                }

                string filePath = parameters["filePath"].ToString();

                if (!File.Exists(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"File not found: {filePath}"
                    });
                }

                string json = File.ReadAllText(filePath);
                var batch = JsonConvert.DeserializeObject<TaskBatch>(json);

                if (batch == null || batch.Tasks == null || batch.Tasks.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid batch file or no tasks defined"
                    });
                }

                // Assign IDs if not present
                for (int i = 0; i < batch.Tasks.Count; i++)
                {
                    if (batch.Tasks[i].Id == 0)
                        batch.Tasks[i].Id = i + 1;
                }

                // Set up logging
                if (string.IsNullOrEmpty(batch.LogFilePath))
                {
                    string dir = Path.GetDirectoryName(filePath);
                    batch.LogFilePath = Path.Combine(dir, $"batch_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                }

                if (string.IsNullOrEmpty(batch.StateFilePath))
                {
                    string dir = Path.GetDirectoryName(filePath);
                    batch.StateFilePath = Path.Combine(dir, $"batch_state_{batch.BatchId ?? "auto"}.json");
                }

                _currentBatch = batch;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    batchId = batch.BatchId,
                    batchName = batch.Name,
                    totalTasks = batch.TotalTasks,
                    pendingTasks = batch.PendingTasks,
                    logFile = batch.LogFilePath,
                    stateFile = batch.StateFilePath,
                    message = $"Loaded batch with {batch.TotalTasks} tasks. Ready to execute."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Resume a batch from a saved state file
        /// </summary>
        public static string ResumeBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (parameters["stateFilePath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "stateFilePath is required"
                    });
                }

                string stateFilePath = parameters["stateFilePath"].ToString();

                if (!File.Exists(stateFilePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"State file not found: {stateFilePath}"
                    });
                }

                string json = File.ReadAllText(stateFilePath);
                var batch = JsonConvert.DeserializeObject<TaskBatch>(json);

                if (batch == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid state file"
                    });
                }

                _currentBatch = batch;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    batchId = batch.BatchId,
                    batchName = batch.Name,
                    totalTasks = batch.TotalTasks,
                    completedTasks = batch.CompletedTasks,
                    failedTasks = batch.FailedTasks,
                    pendingTasks = batch.PendingTasks,
                    progressPercent = batch.ProgressPercent,
                    currentTaskIndex = batch.CurrentTaskIndex,
                    message = $"Resumed batch at task {batch.CurrentTaskIndex + 1} of {batch.TotalTasks}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Execute the next task in the batch
        /// </summary>
        public static string ExecuteNextTask(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (_currentBatch == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No batch loaded. Use loadBatch or resumeBatch first."
                    });
                }

                if (_isPaused)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Batch is paused. Use resumeBatchExecution to continue."
                    });
                }

                // Find next pending task
                var task = _currentBatch.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Pending);

                if (task == null)
                {
                    // All tasks completed
                    _currentBatch.CompletedAt = DateTime.Now;
                    SaveState();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        batchComplete = true,
                        totalTasks = _currentBatch.TotalTasks,
                        completedTasks = _currentBatch.CompletedTasks,
                        failedTasks = _currentBatch.FailedTasks,
                        skippedTasks = _currentBatch.SkippedTasks,
                        message = "All tasks completed"
                    });
                }

                _currentBatch.CurrentTaskIndex = _currentBatch.Tasks.IndexOf(task);

                // Execute the task
                return ExecuteTask(uiApp, task);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Execute a specific task
        /// </summary>
        private static string ExecuteTask(UIApplication uiApp, BatchTask task)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;

            // Set context for callbacks (used by ResultVerifier)
            MCPServer.SetCurrentContext(uiApp);

            task.Status = TaskStatus.InProgress;
            task.StartTime = DateTime.Now;

            LogMessage($"[{task.Id}] Starting: {task.Name ?? task.Method}");

            try
            {
                // Ensure the method exists
                if (string.IsNullOrEmpty(task.Method))
                {
                    throw new Exception("Task method is not specified");
                }

                // ========== PRE-FLIGHT CHECK ==========
                // Validate the operation can succeed before trying
                var preFlightParams = new JObject
                {
                    ["operation"] = task.Method,
                    ["parameters"] = task.Parameters ?? new JObject()
                };
                var preFlightResult = ValidationMethods.PreFlightCheck(uiApp, preFlightParams);
                var preFlightObj = JObject.Parse(preFlightResult);

                if (preFlightObj["canProceed"]?.Value<bool>() == false)
                {
                    var issues = preFlightObj["issues"]?.ToObject<string[]>() ?? new[] { "Pre-flight check failed" };
                    var suggestions = preFlightObj["suggestions"]?.ToObject<string[]>() ?? new string[0];

                    task.Status = TaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    task.ErrorMessage = $"Pre-flight failed: {string.Join("; ", issues)}";

                    LogMessage($"[{task.Id}] Pre-flight FAILED: {task.ErrorMessage}");
                    if (suggestions.Length > 0)
                    {
                        LogMessage($"[{task.Id}] Suggestions: {string.Join("; ", suggestions)}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        taskId = task.Id,
                        phase = "preflight",
                        issues = issues,
                        suggestions = suggestions
                    });
                }

                // Log any warnings from pre-flight
                var warnings = preFlightObj["warnings"]?.ToObject<string[]>() ?? new string[0];
                foreach (var warning in warnings)
                {
                    LogMessage($"[{task.Id}] Warning: {warning}");
                }

                // ========== EXECUTE OPERATION ==========
                var result = MCPServer.ExecuteMethod(uiApp, task.Method, task.Parameters ?? new JObject());

                task.Result = result;
                task.EndTime = DateTime.Now;

                // Parse result to check for success
                var resultObj = JObject.Parse(result);
                bool success = resultObj["success"]?.Value<bool>() ?? false;

                if (success)
                {
                    task.Status = TaskStatus.Completed;
                    LogMessage($"[{task.Id}] Completed in {task.DurationSeconds:F1}s");

                    // ========== RECORD OPERATION FOR ROLLBACK ==========
                    // Track what was created so we can undo if needed
                    try
                    {
                        var affectedIds = new List<int>();

                        // Extract created element IDs from result
                        if (resultObj["elementId"] != null)
                            affectedIds.Add(resultObj["elementId"].Value<int>());
                        if (resultObj["sheetId"] != null)
                            affectedIds.Add(resultObj["sheetId"].Value<int>());
                        if (resultObj["viewId"] != null)
                            affectedIds.Add(resultObj["viewId"].Value<int>());
                        if (resultObj["wallId"] != null)
                            affectedIds.Add(resultObj["wallId"].Value<int>());
                        if (resultObj["viewportId"] != null)
                            affectedIds.Add(resultObj["viewportId"].Value<int>());

                        // Record element IDs for created elements
                        if (resultObj["elementIds"] != null)
                        {
                            var ids = resultObj["elementIds"].ToObject<int[]>();
                            if (ids != null) affectedIds.AddRange(ids);
                        }

                        if (affectedIds.Count > 0)
                        {
                            var recordParams = new JObject
                            {
                                ["operationType"] = task.Method.StartsWith("delete") ? "delete" : "create",
                                ["affectedElementIds"] = JArray.FromObject(affectedIds),
                                ["parameters"] = task.Parameters
                            };
                            SelfHealingMethods.RecordOperation(uiApp, recordParams);
                            LogMessage($"[{task.Id}] Recorded {affectedIds.Count} elements for rollback");
                        }
                    }
                    catch (Exception recordEx)
                    {
                        LogMessage($"[{task.Id}] Warning: Failed to record for rollback: {recordEx.Message}");
                    }

                    // ========== POST-OPERATION VERIFICATION ==========
                    // Verify the operation actually produced expected results
                    try
                    {
                        EnsureVerifierInitialized();
                        var verifyTask = _verifier.VerifyAsync(task.Method, task.Parameters ?? new JObject(), resultObj);

                        // Wait for verification (with timeout)
                        if (verifyTask.Wait(5000))
                        {
                            var verification = verifyTask.Result;
                            task.Verified = verification.Verified;
                            task.VerificationMessage = verification.Message;

                            if (verification.Verified)
                            {
                                LogMessage($"[{task.Id}] Verified: {verification.Message}");
                            }
                            else
                            {
                                LogMessage($"[{task.Id}] Verification WARNING: {verification.Message}");
                            }
                        }
                        else
                        {
                            task.Verified = null;
                            task.VerificationMessage = "Verification timed out";
                            LogMessage($"[{task.Id}] Verification timed out");
                        }
                    }
                    catch (Exception verifyEx)
                    {
                        task.Verified = null;
                        task.VerificationMessage = $"Verification error: {verifyEx.Message}";
                        LogMessage($"[{task.Id}] Verification error: {verifyEx.Message}");
                    }

                    // Switch to view if requested
                    if (task.SwitchToViewAfter && task.ViewIdToSwitch.HasValue)
                    {
                        try
                        {
                            var view = doc.GetElement(new ElementId(task.ViewIdToSwitch.Value)) as View;
                            if (view != null && !view.IsTemplate)
                            {
                                uiDoc.ActiveView = view;
                            }
                        }
                        catch { /* Ignore view switch errors */ }
                    }
                }
                else
                {
                    string error = resultObj["error"]?.ToString() ?? "Unknown error";
                    task.ErrorMessage = error;

                    // Handle based on retry policy
                    if (task.RetryCount < task.MaxRetries)
                    {
                        task.RetryCount++;
                        task.Status = TaskStatus.Pending;
                        LogMessage($"[{task.Id}] Failed, retrying ({task.RetryCount}/{task.MaxRetries}): {error}");
                    }
                    else
                    {
                        task.Status = TaskStatus.Failed;
                        LogMessage($"[{task.Id}] Failed: {error}");

                        // Handle based on error strategy
                        if (_currentBatch.OnError == ErrorStrategy.StopOnError)
                        {
                            _isPaused = true;
                        }
                    }
                }

                // Save checkpoint if needed
                if (_currentBatch.CurrentTaskIndex % _currentBatch.CheckpointEvery == 0)
                {
                    SaveState();
                }

                // Clear context before returning
                MCPServer.ClearCurrentContext();

                return JsonConvert.SerializeObject(new
                {
                    success = task.Status == TaskStatus.Completed,
                    taskId = task.Id,
                    taskName = task.Name,
                    taskMethod = task.Method,
                    status = task.Status.ToString(),
                    durationSeconds = task.DurationSeconds,
                    errorMessage = task.ErrorMessage,
                    verification = new
                    {
                        verified = task.Verified,
                        message = task.VerificationMessage
                    },
                    batchProgress = new
                    {
                        current = _currentBatch.CurrentTaskIndex + 1,
                        total = _currentBatch.TotalTasks,
                        completed = _currentBatch.CompletedTasks,
                        failed = _currentBatch.FailedTasks,
                        percent = _currentBatch.ProgressPercent
                    },
                    result = resultObj
                });
            }
            catch (Exception ex)
            {
                // Clear context on error too
                MCPServer.ClearCurrentContext();

                task.Status = TaskStatus.Failed;
                task.EndTime = DateTime.Now;
                task.ErrorMessage = ex.Message;

                LogMessage($"[{task.Id}] Exception: {ex.Message}");

                if (_currentBatch.OnError == ErrorStrategy.StopOnError)
                {
                    _isPaused = true;
                }

                SaveState();

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    taskId = task.Id,
                    taskName = task.Name,
                    status = task.Status.ToString(),
                    error = ex.Message,
                    stackTrace = ex.StackTrace,
                    batchPaused = _isPaused
                });
            }
        }

        /// <summary>
        /// Execute all remaining tasks in sequence
        /// </summary>
        public static string ExecuteAllTasks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (_currentBatch == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No batch loaded"
                    });
                }

                if (_isRunning)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Batch is already running"
                    });
                }

                _isRunning = true;
                _isPaused = false;
                _currentBatch.StartedAt = DateTime.Now;

                LogMessage($"=== Starting batch: {_currentBatch.Name} ===");
                LogMessage($"Total tasks: {_currentBatch.TotalTasks}");

                var results = new List<object>();
                int executed = 0;

                while (!_isPaused)
                {
                    var task = _currentBatch.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Pending);
                    if (task == null) break;

                    _currentBatch.CurrentTaskIndex = _currentBatch.Tasks.IndexOf(task);
                    var result = ExecuteTask(uiApp, task);
                    results.Add(JsonConvert.DeserializeObject(result));
                    executed++;

                    // Small delay between tasks for UI responsiveness
                    System.Windows.Forms.Application.DoEvents();
                }

                _isRunning = false;
                _currentBatch.CompletedAt = DateTime.Now;
                SaveState();

                LogMessage($"=== Batch finished ===");
                LogMessage($"Executed: {executed}, Completed: {_currentBatch.CompletedTasks}, Failed: {_currentBatch.FailedTasks}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    batchComplete = !_isPaused,
                    tasksExecuted = executed,
                    completedTasks = _currentBatch.CompletedTasks,
                    failedTasks = _currentBatch.FailedTasks,
                    skippedTasks = _currentBatch.SkippedTasks,
                    pendingTasks = _currentBatch.PendingTasks,
                    wasPaused = _isPaused,
                    logFile = _currentBatch.LogFilePath,
                    stateFile = _currentBatch.StateFilePath
                });
            }
            catch (Exception ex)
            {
                _isRunning = false;
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Pause batch execution
        /// </summary>
        public static string PauseBatch(UIApplication uiApp, JObject parameters)
        {
            if (_currentBatch == null)
            {
                return JsonConvert.SerializeObject(new { success = false, error = "No batch loaded" });
            }

            _isPaused = true;
            SaveState();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                message = "Batch paused",
                currentTask = _currentBatch.CurrentTaskIndex + 1,
                totalTasks = _currentBatch.TotalTasks,
                stateFile = _currentBatch.StateFilePath
            });
        }

        /// <summary>
        /// Get current batch status
        /// </summary>
        public static string GetBatchStatus(UIApplication uiApp, JObject parameters)
        {
            if (_currentBatch == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasBatch = false,
                    message = "No batch loaded"
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                hasBatch = true,
                batchId = _currentBatch.BatchId,
                batchName = _currentBatch.Name,
                isRunning = _isRunning,
                isPaused = _isPaused,
                progress = new
                {
                    current = _currentBatch.CurrentTaskIndex + 1,
                    total = _currentBatch.TotalTasks,
                    completed = _currentBatch.CompletedTasks,
                    failed = _currentBatch.FailedTasks,
                    skipped = _currentBatch.SkippedTasks,
                    pending = _currentBatch.PendingTasks,
                    percent = _currentBatch.ProgressPercent
                },
                tasks = _currentBatch.Tasks.Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    method = t.Method,
                    status = t.Status.ToString(),
                    duration = t.DurationSeconds,
                    error = t.ErrorMessage
                }).ToList()
            });
        }

        /// <summary>
        /// Create a batch from a list of tasks
        /// </summary>
        public static string CreateBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var tasks = parameters["tasks"]?.ToObject<List<BatchTask>>();
                if (tasks == null || tasks.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "tasks array is required and must not be empty"
                    });
                }

                var batch = new TaskBatch
                {
                    BatchId = parameters["batchId"]?.ToString() ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = parameters["name"]?.ToString() ?? "Unnamed Batch",
                    Description = parameters["description"]?.ToString(),
                    CreatedAt = DateTime.Now,
                    OnError = Enum.TryParse<ErrorStrategy>(parameters["onError"]?.ToString(), true, out var strategy)
                        ? strategy : ErrorStrategy.LogAndContinue,
                    CheckpointEvery = parameters["checkpointEvery"]?.Value<int>() ?? 5,
                    Tasks = tasks
                };

                // Assign IDs
                for (int i = 0; i < batch.Tasks.Count; i++)
                {
                    batch.Tasks[i].Id = i + 1;
                }

                // Set up log/state files
                string baseDir = parameters["outputDir"]?.ToString()
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitBatches");
                Directory.CreateDirectory(baseDir);

                batch.LogFilePath = Path.Combine(baseDir, $"batch_{batch.BatchId}_log.txt");
                batch.StateFilePath = Path.Combine(baseDir, $"batch_{batch.BatchId}_state.json");

                _currentBatch = batch;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    batchId = batch.BatchId,
                    batchName = batch.Name,
                    totalTasks = batch.TotalTasks,
                    logFile = batch.LogFilePath,
                    stateFile = batch.StateFilePath,
                    message = $"Created batch with {batch.TotalTasks} tasks. Ready to execute."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Save current batch state to file
        /// </summary>
        private static void SaveState()
        {
            if (_currentBatch == null || string.IsNullOrEmpty(_currentBatch.StateFilePath)) return;

            try
            {
                string json = JsonConvert.SerializeObject(_currentBatch, Formatting.Indented);
                File.WriteAllText(_currentBatch.StateFilePath, json);
            }
            catch (Exception ex)
            {
                LogMessage($"Error saving state: {ex.Message}");
            }
        }

        /// <summary>
        /// Log a message to the batch log file
        /// </summary>
        private static void LogMessage(string message)
        {
            if (_currentBatch == null) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logLine = $"[{timestamp}] {message}";

            System.Diagnostics.Debug.WriteLine(logLine);

            if (!string.IsNullOrEmpty(_currentBatch.LogFilePath))
            {
                try
                {
                    File.AppendAllText(_currentBatch.LogFilePath, logLine + Environment.NewLine);
                }
                catch { /* Ignore log errors */ }
            }
        }
    }
}
