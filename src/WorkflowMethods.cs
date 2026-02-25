using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Autonomous Workflow Execution Engine
    /// Enables Claude AI to execute complete architectural deliverables systematically
    /// </summary>
    public static class WorkflowMethods
    {
        private static Dictionary<string, WorkflowState> _activeWorkflows = new Dictionary<string, WorkflowState>();

        // Workflow templates path - try multiple locations
        private static string _workflowTemplatesPath = GetWorkflowTemplatesPath();

        /// <summary>
        /// Find workflow templates directory - checks multiple locations
        /// </summary>
        private static string GetWorkflowTemplatesPath()
        {
            // Try multiple locations in order of preference
            var possiblePaths = new[]
            {
                // 1. Project directory (for development and deployed scenarios)
                @"D:\RevitMCPBridge2026\Workflows",

                // 2. Relative to assembly location (for bin/Release during dev)
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "..", "..", "..", "Workflows"),

                // 3. Same directory as assembly (if templates copied to deployment)
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Workflows"),

                // 4. Parent of assembly directory
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "..", "Workflows")
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (Directory.Exists(fullPath))
                    {
                        Log.Information($"[WORKFLOW] Found templates directory: {fullPath}");
                        return fullPath;
                    }
                }
                catch
                {
                    // Skip invalid paths
                }
            }

            // Fall back to project directory even if it doesn't exist
            var fallback = @"D:\RevitMCPBridge2026\Workflows";
            Log.Warning($"[WORKFLOW] Templates directory not found, using fallback: {fallback}");
            return fallback;
        }

        /// <summary>
        /// Workflow execution state tracking
        /// </summary>
        private class WorkflowState
        {
            public string WorkflowId { get; set; }
            public string WorkflowType { get; set; }
            public DateTime StartTime { get; set; }
            public string CurrentPhase { get; set; }
            public int CurrentTaskIndex { get; set; }
            public List<string> CompletedTasks { get; set; }
            public List<string> FailedTasks { get; set; }
            public List<WorkflowDecision> DecisionsMade { get; set; }
            public Dictionary<string, object> Context { get; set; }
            public bool IsPaused { get; set; }
            public string Status { get; set; }
        }

        /// <summary>
        /// Autonomous decisions made by AI during execution
        /// </summary>
        private class WorkflowDecision
        {
            public string Task { get; set; }
            public string Decision { get; set; }
            public string Reason { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Execute a workflow autonomously
        /// This is the main entry point for Claude to run complete deliverables
        /// </summary>
        public static string ExecuteWorkflow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var workflowType = parameters["workflowType"]?.ToString();
                var projectType = parameters["projectType"]?.ToString() ?? "General";
                var buildingCode = parameters["buildingCode"]?.ToString() ?? "IBC_2021";
                var customParameters = parameters["parameters"] as JObject ?? new JObject();

                if (string.IsNullOrEmpty(workflowType))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "workflowType is required (e.g., 'DD_Package', 'CD_Set')"
                    });
                }

                Log.Information($"Starting autonomous workflow: {workflowType} for {projectType}");

                // Generate unique workflow ID
                var workflowId = Guid.NewGuid().ToString();

                // Initialize workflow state
                var state = new WorkflowState
                {
                    WorkflowId = workflowId,
                    WorkflowType = workflowType,
                    StartTime = DateTime.Now,
                    CompletedTasks = new List<string>(),
                    FailedTasks = new List<string>(),
                    DecisionsMade = new List<WorkflowDecision>(),
                    Context = new Dictionary<string, object>
                    {
                        ["projectType"] = projectType,
                        ["buildingCode"] = buildingCode,
                        ["customParameters"] = customParameters
                    },
                    IsPaused = false,
                    Status = "Running"
                };

                _activeWorkflows[workflowId] = state;

                // Load workflow template
                var template = LoadWorkflowTemplate(workflowType, projectType);
                if (template == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Workflow template not found: {workflowType}"
                    });
                }

                // Execute workflow phases
                var result = ExecuteWorkflowPhases(uiApp, state, template);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    workflowId = workflowId,
                    workflowType = workflowType,
                    status = state.Status,
                    tasksCompleted = state.CompletedTasks.Count,
                    tasksFailed = state.FailedTasks.Count,
                    decisionsMade = state.DecisionsMade.Count,
                    executionTime = (DateTime.Now - state.StartTime).TotalSeconds,
                    summary = result
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing workflow");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get status of a running or completed workflow
        /// </summary>
        public static string GetWorkflowStatus(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var workflowId = parameters["workflowId"]?.ToString();

                if (string.IsNullOrEmpty(workflowId))
                {
                    // Return all active workflows
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        activeWorkflows = _activeWorkflows.Values.Select(w => new
                        {
                            workflowId = w.WorkflowId,
                            workflowType = w.WorkflowType,
                            status = w.Status,
                            currentPhase = w.CurrentPhase,
                            tasksCompleted = w.CompletedTasks.Count,
                            runtime = (DateTime.Now - w.StartTime).TotalSeconds
                        })
                    });
                }

                if (!_activeWorkflows.ContainsKey(workflowId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Workflow not found"
                    });
                }

                var state = _activeWorkflows[workflowId];

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    workflowId = state.WorkflowId,
                    workflowType = state.WorkflowType,
                    status = state.Status,
                    currentPhase = state.CurrentPhase,
                    tasksCompleted = state.CompletedTasks,
                    tasksFailed = state.FailedTasks,
                    decisionsMade = state.DecisionsMade,
                    runtime = (DateTime.Now - state.StartTime).TotalSeconds,
                    isPaused = state.IsPaused
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting workflow status");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// List available workflow templates
        /// </summary>
        public static string ListWorkflowTemplates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                Directory.CreateDirectory(_workflowTemplatesPath);
                var templateFiles = Directory.GetFiles(_workflowTemplatesPath, "*.json");

                var templates = templateFiles.Select(f =>
                {
                    var content = File.ReadAllText(f);
                    var template = JObject.Parse(content);
                    return new
                    {
                        workflowType = template["workflowType"]?.ToString(),
                        name = template["name"]?.ToString(),
                        description = template["description"]?.ToString(),
                        projectTypes = template["projectTypes"]?.ToObject<string[]>(),
                        phases = template["phases"]?.Count() ?? 0,
                        estimatedTime = template["estimatedTime"]?.ToString()
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    templates = templates,
                    count = templates.Count
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error listing workflow templates");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Pause a running workflow
        /// </summary>
        public static string PauseWorkflow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var workflowId = parameters["workflowId"]?.ToString();

                if (!_activeWorkflows.ContainsKey(workflowId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Workflow not found"
                    });
                }

                var state = _activeWorkflows[workflowId];
                state.IsPaused = true;
                state.Status = "Paused";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Workflow paused",
                    workflowId = workflowId
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Resume a paused workflow
        /// </summary>
        public static string ResumeWorkflow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var workflowId = parameters["workflowId"]?.ToString();

                if (!_activeWorkflows.ContainsKey(workflowId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Workflow not found"
                    });
                }

                var state = _activeWorkflows[workflowId];
                state.IsPaused = false;
                state.Status = "Running";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Workflow resumed",
                    workflowId = workflowId
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ==================== PRIVATE HELPER METHODS ====================

        /// <summary>
        /// Load workflow template from JSON file
        /// </summary>
        private static JObject LoadWorkflowTemplate(string workflowType, string projectType)
        {
            try
            {
                Directory.CreateDirectory(_workflowTemplatesPath);

                // Try to load specific template: {workflowType}_{projectType}.json
                var specificPath = Path.Combine(_workflowTemplatesPath, $"{workflowType}_{projectType}.json");
                if (File.Exists(specificPath))
                {
                    return JObject.Parse(File.ReadAllText(specificPath));
                }

                // Fall back to generic template: {workflowType}.json
                var genericPath = Path.Combine(_workflowTemplatesPath, $"{workflowType}.json");
                if (File.Exists(genericPath))
                {
                    return JObject.Parse(File.ReadAllText(genericPath));
                }

                Log.Warning($"Workflow template not found: {workflowType}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error loading workflow template: {workflowType}");
                return null;
            }
        }

        /// <summary>
        /// Execute all phases of a workflow
        /// </summary>
        private static object ExecuteWorkflowPhases(UIApplication uiApp, WorkflowState state, JObject template)
        {
            var phases = template["phases"] as JArray;
            var summary = new Dictionary<string, object>();

            foreach (var phase in phases)
            {
                state.CurrentPhase = phase["name"]?.ToString();
                Log.Information($"[{state.WorkflowType}] Executing phase: {state.CurrentPhase}");

                var phaseSummary = ExecutePhase(uiApp, state, phase as JObject);
                summary[state.CurrentPhase] = phaseSummary;

                // Check if paused
                if (state.IsPaused)
                {
                    Log.Information($"Workflow paused at phase: {state.CurrentPhase}");
                    break;
                }
            }

            if (!state.IsPaused)
            {
                state.Status = state.FailedTasks.Count > 0 ? "Completed with errors" : "Completed successfully";
            }

            return summary;
        }

        /// <summary>
        /// Execute a single phase of a workflow
        /// </summary>
        private static object ExecutePhase(UIApplication uiApp, WorkflowState state, JObject phase)
        {
            var tasks = phase["tasks"] as JArray;
            int tasksCompleted = 0;
            int tasksFailed = 0;

            foreach (var taskDef in tasks)
            {
                var taskObj = taskDef as JObject;
                var taskId = taskObj["id"]?.ToString() ?? "unknown";
                var taskDescription = taskObj["description"]?.ToString() ?? taskId;

                try
                {
                    Log.Information($"[{state.WorkflowType}] Executing: {taskDescription}");

                    // Execute the task with full definition
                    var result = ExecuteTask(uiApp, state, taskObj);

                    if (result.Success)
                    {
                        state.CompletedTasks.Add(taskDescription);
                        tasksCompleted++;

                        // Record any autonomous decisions made
                        if (result.Decision != null)
                        {
                            state.DecisionsMade.Add(result.Decision);
                            Log.Information($"[DECISION] {result.Decision.Decision} - {result.Decision.Reason}");
                        }
                    }
                    else
                    {
                        state.FailedTasks.Add(taskDescription);
                        tasksFailed++;
                        Log.Warning($"Task failed: {taskDescription} - {result.Error}");
                    }
                }
                catch (Exception ex)
                {
                    state.FailedTasks.Add(taskDescription);
                    tasksFailed++;
                    Log.Error(ex, $"Error executing task: {taskDescription}");
                }

                // Check for pause
                if (state.IsPaused)
                {
                    break;
                }
            }

            return new
            {
                tasksCompleted = tasksCompleted,
                tasksFailed = tasksFailed,
                decisionsCount = state.DecisionsMade.Count
            };
        }

        /// <summary>
        /// Execute a single task with autonomous decision-making
        /// </summary>
        private static TaskResult ExecuteTask(UIApplication uiApp, WorkflowState state, JObject taskDef)
        {
            try
            {
                var taskId = taskDef["id"]?.ToString();
                var method = taskDef["method"]?.ToString();
                var parameters = taskDef["parameters"] as JObject ?? new JObject();
                var autonomousDecision = taskDef["autonomous_decision"]?.ToString();

                // If no method specified, this is a custom/placeholder task
                if (string.IsNullOrEmpty(method) || method == "custom")
                {
                    // For custom tasks, just log the autonomous decision and mark as success
                    return new TaskResult
                    {
                        Success = true,
                        Decision = new WorkflowDecision
                        {
                            Task = taskId,
                            Decision = "Custom task - marked for future implementation",
                            Reason = autonomousDecision ?? "No specific logic defined yet",
                            Timestamp = DateTime.Now
                        },
                        Error = null
                    };
                }

                // Execute the actual Revit API method
                var apiResult = CallRevitAPIMethod(uiApp, method, parameters, state);

                // Parse result
                var resultObj = JObject.Parse(apiResult);
                var success = resultObj["success"]?.Value<bool>() ?? false;

                if (success)
                {
                    // Task succeeded - log decision if autonomous behavior was applied
                    WorkflowDecision decision = null;
                    if (!string.IsNullOrEmpty(autonomousDecision))
                    {
                        decision = new WorkflowDecision
                        {
                            Task = taskId,
                            Decision = $"Executed {method} successfully",
                            Reason = autonomousDecision,
                            Timestamp = DateTime.Now
                        };
                    }

                    // Store result data in workflow context for later tasks
                    if (resultObj["scheduleId"] != null)
                    {
                        state.Context[$"lastScheduleId"] = resultObj["scheduleId"].ToString();
                    }
                    if (resultObj["sheetId"] != null)
                    {
                        state.Context[$"lastSheetId"] = resultObj["sheetId"].ToString();
                    }
                    if (resultObj["viewId"] != null)
                    {
                        state.Context[$"lastViewId"] = resultObj["viewId"].ToString();
                    }

                    return new TaskResult
                    {
                        Success = true,
                        Decision = decision,
                        Error = null
                    };
                }
                else
                {
                    var error = resultObj["error"]?.ToString() ?? "Unknown error";
                    return new TaskResult
                    {
                        Success = false,
                        Decision = null,
                        Error = error
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error in ExecuteTask");
                return new TaskResult
                {
                    Success = false,
                    Decision = null,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Call a Revit API method through the existing method infrastructure
        /// </summary>
        private static string CallRevitAPIMethod(UIApplication uiApp, string methodName, JObject parameters, WorkflowState state)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            // Inject workflow context into parameters if needed
            if (parameters != null)
            {
                // Auto-inject IDs from context if not provided
                if (parameters["scheduleId"] == null && state.Context.ContainsKey("lastScheduleId"))
                {
                    parameters["scheduleId"] = state.Context["lastScheduleId"].ToString();
                }
                if (parameters["sheetId"] == null && state.Context.ContainsKey("lastSheetId"))
                {
                    parameters["sheetId"] = state.Context["lastSheetId"].ToString();
                }
                if (parameters["viewId"] == null && state.Context.ContainsKey("lastViewId"))
                {
                    parameters["viewId"] = state.Context["lastViewId"].ToString();
                }
            }

            // Route to appropriate method based on method name
            Log.Information($"[WORKFLOW] Routing to method: {methodName}");

            // Dynamic method routing to actual implementations
            switch (methodName.ToLower())
            {
                // Wall methods
                case "getwalls":
                    return WallMethods.GetWalls(uiApp, parameters);
                case "createwall":
                    return WallMethods.CreateWallByPoints(uiApp, parameters);
                case "getwalltypes":
                    return WallMethods.GetWallTypes(uiApp, parameters);

                // Door/Window methods
                case "getdoors":
                    return DoorWindowMethods.GetDoors(uiApp, parameters);
                case "getwindows":
                    return DoorWindowMethods.GetWindows(uiApp, parameters);
                case "getdoortypes":
                    return DoorWindowMethods.GetDoorTypes(uiApp, parameters);
                case "getwindowtypes":
                    return DoorWindowMethods.GetWindowTypes(uiApp, parameters);

                // Room methods
                case "getrooms":
                    return RoomMethods.GetRooms(uiApp, parameters);
                case "createroom":
                    return RoomMethods.CreateRoom(uiApp, parameters);

                // View methods
                case "getviews":
                    return ViewMethods.GetAllViews(uiApp, parameters);
                case "createfloorplan":
                    return ViewMethods.CreateFloorPlan(uiApp, parameters);
                case "createsection":
                    return ViewMethods.CreateSection(uiApp, parameters);

                // Sheet methods
                case "getsheets":
                case "getallsheets":
                    return SheetMethods.GetAllSheets(uiApp, parameters);
                case "createsheet":
                    return SheetMethods.CreateSheet(uiApp, parameters);
                case "placeviewonsheet":
                    return SheetMethods.PlaceViewOnSheet(uiApp, parameters);

                // Schedule methods (in RevitMCPBridge2026 namespace)
                case "getschedules":
                case "getallschedules":
                    return RevitMCPBridge2026.ScheduleMethods.GetAllSchedules(uiApp, parameters);
                case "createschedule":
                    return RevitMCPBridge2026.ScheduleMethods.CreateSchedule(uiApp, parameters);
                case "addschedulefield":
                    return RevitMCPBridge2026.ScheduleMethods.AddScheduleField(uiApp, parameters);
                case "getscheduledata":
                    return RevitMCPBridge2026.ScheduleMethods.GetScheduleData(uiApp, parameters);
                case "exportscheduletocsv":
                    return RevitMCPBridge2026.ScheduleMethods.ExportScheduleToCSV(uiApp, parameters);

                // Tag methods
                case "tagallbycategory":
                    return TextTagMethods.TagAllByCategory(uiApp, parameters);
                case "tagallrooms":
                    if (parameters == null) parameters = new JObject();
                    parameters["category"] = "Rooms";
                    return TextTagMethods.TagAllByCategory(uiApp, parameters);

                // Dimension methods
                case "batchdimensionwalls":
                    return DimensioningMethods.BatchDimensionWalls(uiApp, parameters);

                // Level/Grid methods
                case "getlevels":
                    return LevelMethods.GetLevels(uiApp, parameters);
                case "getgrids":
                    return GridMethods.GetGrids(uiApp, parameters);

                // Family methods (in RevitMCPBridge2026 namespace)
                case "getloadedfamilies":
                case "getallfamilies":
                    return RevitMCPBridge2026.FamilyMethods.GetAllFamilies(uiApp, parameters);
                case "placefamilyinstance":
                    return RevitMCPBridge2026.FamilyMethods.PlaceFamilyInstance(uiApp, parameters);

                // Default: method not implemented in workflow routing
                default:
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        method = methodName,
                        error = $"Method '{methodName}' not implemented in workflow routing. Use direct MCP call instead."
                    });
            }
        }

        /// <summary>
        /// Task execution result
        /// </summary>
        private class TaskResult
        {
            public bool Success { get; set; }
            public WorkflowDecision Decision { get; set; }
            public string Error { get; set; }
        }
    }
}
