using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// MCPServer partial class - Level 3 Autonomous Intelligence Methods
    /// Extracted from main MCPServer.cs for better code organization.
    /// </summary>
    public partial class MCPServer
    {
        #region Level 3: Autonomous Intelligence Methods

        /// <summary>
        /// Check if a method should be auto-verified after execution
        /// </summary>
        private static bool ShouldAutoVerify(string method) => _verifiableMethods.Contains(method);

        /// <summary>
        /// Check if a method should have corrections checked before execution
        /// </summary>
        private static bool ShouldCheckCorrections(string method) => _correctionCheckMethods.Contains(method);

        /// <summary>
        /// Get pre-execution intelligence for a method
        /// Returns corrections, warnings, and recommendations based on past experience
        /// </summary>
        private static string GetPreExecutionIntelligence(string method, JObject parameters)
        {
            try
            {
                var corrections = CorrectionLearnerInstance.GetMethodCorrections(method);
                var warnings = new List<string>();
                var recommendations = new List<string>();

                // Add method-specific warnings based on corrections
                foreach (var correction in corrections)
                {
                    if (correction.TimesApplied > 3) // Frequently needed correction
                    {
                        warnings.Add($"Common issue: {correction.WhatWentWrong} - Fix: {correction.CorrectApproach}");
                    }
                }

                // Check preference data for recommendations
                if (method == "createSheet" || method == "placeViewOnSheet")
                {
                    var prefs = PreferenceLearnerInstance.GetAllPreferences();
                    if (prefs != null)
                    {
                        recommendations.Add("Consider learned placement preferences for this user");
                    }
                }

                var intelligence = new
                {
                    method = method,
                    corrections = corrections,
                    recommendations = recommendations,
                    warnings = warnings
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    intelligence = intelligence
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting pre-execution intelligence");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Auto-verify an operation and learn from failures
        /// Called automatically after verifiable methods execute
        /// </summary>
        private async Task<string> AutoVerifyAndLearn(string method, JObject parameters, string originalResult)
        {
            try
            {
                // Parse original result
                var result = JObject.Parse(originalResult);
                if (result["success"]?.Value<bool>() != true)
                {
                    // Operation already failed - learn from the error
                    var errorMsg = result["error"]?.ToString() ?? "Unknown error";
                    Log.Information($"[AutoVerify] Learning from failed {method}: {errorMsg}");

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        verified = false,
                        originalResult = result,
                        learningNote = "Error captured for pattern learning"
                    });
                }

                // Get created element ID if available
                var elementId = result["result"]?["elementId"]?.Value<long>()
                    ?? result["result"]?["id"]?.Value<long>()
                    ?? result["elementId"]?.Value<long>();

                if (elementId == null)
                {
                    // No element to verify
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        verified = true,
                        verificationSkipped = true,
                        reason = "No element ID in result to verify"
                    });
                }

                // Perform verification using ResultVerifier
                var verificationParams = new JObject
                {
                    ["method"] = method,
                    ["elementId"] = elementId,
                    ["expectedParameters"] = parameters
                };

                var verifyResult = await ExecuteInRevitContext(uiApp =>
                    IntelligenceMethods.VerifyOperationResult(uiApp, verificationParams));

                var verification = JObject.Parse(verifyResult);

                // If verification found issues, log for learning
                if (verification["result"]?["issues"] != null)
                {
                    var issues = verification["result"]["issues"] as JArray;
                    if (issues?.Count > 0)
                    {
                        Log.Warning($"[AutoVerify] Issues found after {method}: {string.Join(", ", issues)}");
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verified = verification["success"]?.Value<bool>() ?? false,
                    verification = verification["result"],
                    originalResult = result
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in auto-verify");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    verified = false,
                    error = ex.Message,
                    originalResult = originalResult
                });
            }
        }

        /// <summary>
        /// Execute a multi-step workflow autonomously
        /// </summary>
        private async Task<string> ExecuteWorkflow(JObject parameters)
        {
            try
            {
                var workflowRequest = parameters["request"]?.ToString();
                var autoExecute = parameters["autoExecute"]?.Value<bool>() ?? false;

                if (string.IsNullOrEmpty(workflowRequest))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "request parameter is required"
                    });
                }

                // Analyze the request to create a plan
                var plan = WorkflowPlannerInstance.AnalyzeRequest(workflowRequest);
                _currentWorkflow = plan;
                _currentWorkflowStep = 0;

                if (!autoExecute)
                {
                    // Return plan for user approval
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        requiresApproval = true,
                        plan = new
                        {
                            isMultiStep = plan.IsMultiStep,
                            isBatch = plan.IsBatchOperation,
                            templateName = plan.TemplateName,
                            steps = plan.Steps.Select(s => new
                            {
                                stepNumber = s.StepNumber,
                                method = s.Method,
                                description = s.Description,
                                status = s.Status.ToString()
                            })
                        },
                        message = "Workflow plan created. Call executeWorkflowStep to execute each step, or set autoExecute=true to run all."
                    });
                }

                // Auto-execute all steps
                var results = new List<object>();
                foreach (var step in plan.Steps)
                {
                    step.Status = StepStatus.InProgress;

                    // Build parameters for this step
                    var stepParams = step.Parameters ?? new JObject();

                    // Execute the step
                    var stepResult = await SmartExecute(step.Method, stepParams);
                    var parsedResult = JObject.Parse(stepResult);

                    step.Result = stepResult;
                    step.Status = parsedResult["success"]?.Value<bool>() == true
                        ? StepStatus.Completed
                        : StepStatus.Failed;

                    results.Add(new
                    {
                        stepNumber = step.StepNumber,
                        method = step.Method,
                        status = step.Status.ToString(),
                        success = step.Status == StepStatus.Completed,
                        result = parsedResult
                    });

                    _currentWorkflowStep++;

                    // Stop on failure unless configured otherwise
                    if (step.Status == StepStatus.Failed)
                    {
                        break;
                    }
                }

                var completedSteps = plan.Steps.Count(s => s.Status == StepStatus.Completed);
                var failedSteps = plan.Steps.Count(s => s.Status == StepStatus.Failed);

                return JsonConvert.SerializeObject(new
                {
                    success = failedSteps == 0,
                    workflow = new
                    {
                        originalRequest = plan.OriginalRequest,
                        templateName = plan.TemplateName,
                        totalSteps = plan.Steps.Count,
                        completedSteps = completedSteps,
                        failedSteps = failedSteps,
                        progress = WorkflowPlannerInstance.GetProgressSummary()
                    },
                    results = results
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing workflow");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Smart execute - wrapper that adds pre-execution checks and post-execution verification
        /// This is the core Level 3 intelligence entry point
        /// </summary>
        private async Task<string> SmartExecute(string method, JObject parameters)
        {
            try
            {
                Log.Information($"[SmartExecute] Starting {method} with intelligence checks");

                // Step 1: Pre-execution intelligence check
                string preIntelligence = null;
                if (ShouldCheckCorrections(method))
                {
                    preIntelligence = GetPreExecutionIntelligence(method, parameters);
                    var intel = JObject.Parse(preIntelligence);

                    // Log any warnings
                    var warnings = intel["intelligence"]?["warnings"] as JArray;
                    if (warnings?.Count > 0)
                    {
                        Log.Warning($"[SmartExecute] Pre-execution warnings for {method}: {string.Join("; ", warnings)}");
                    }
                }

                // Step 2: Execute the actual method
                string result;
                switch (method.ToLower())
                {
                    case "createwall":
                    case "createwallbypoints":
                        result = await ExecuteInRevitContext(uiApp => WallMethods.CreateWallByPoints(uiApp, parameters));
                        break;
                    case "createroom":
                        result = await ExecuteInRevitContext(uiApp => RoomMethods.CreateRoom(uiApp, parameters));
                        break;
                    case "createsheet":
                        result = await ExecuteInRevitContext(uiApp => SheetMethods.CreateSheet(uiApp, parameters));
                        break;
                    case "placeviewonsheet":
                        result = await ExecuteInRevitContext(uiApp => SheetMethods.PlaceViewOnSheet(uiApp, parameters));
                        break;
                    case "placedoor":
                        result = await ExecuteInRevitContext(uiApp => DoorWindowMethods.PlaceDoor(uiApp, parameters));
                        break;
                    case "placewindow":
                        result = await ExecuteInRevitContext(uiApp => DoorWindowMethods.PlaceWindow(uiApp, parameters));
                        break;
                    case "placefamilyinstance":
                        result = await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.PlaceFamilyInstance(uiApp, parameters));
                        break;
                    default:
                        // Fall back to regular method dispatch
                        if (_methodRegistry.TryGetValue(method, out var registeredMethod))
                        {
                            result = await ExecuteInRevitContext(uiApp => registeredMethod(uiApp, parameters));
                        }
                        else
                        {
                            result = JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Method '{method}' not found in smart execute registry"
                            });
                        }
                        break;
                }

                // Step 3: Auto-verify if applicable
                string verificationResult = null;
                if (ShouldAutoVerify(method))
                {
                    verificationResult = await AutoVerifyAndLearn(method, parameters, result);
                }

                // Step 4: Log successful operations for preference learning
                var parsedResult = JObject.Parse(result);
                if (parsedResult["success"]?.Value<bool>() == true)
                {
                    // Preference learning happens via LearnFromView, LearnFromViewportPlacement, etc.
                    // when those operations are executed. Log for future pattern detection.
                    Log.Information($"[SmartExecute] Successfully completed {method}");
                }

                // Return enriched result
                return JsonConvert.SerializeObject(new
                {
                    success = parsedResult["success"]?.Value<bool>() ?? false,
                    result = parsedResult["result"],
                    intelligence = new
                    {
                        preExecutionChecked = ShouldCheckCorrections(method),
                        autoVerified = ShouldAutoVerify(method),
                        preIntelligence = preIntelligence != null ? JObject.Parse(preIntelligence)["intelligence"] : null,
                        verification = verificationResult != null ? JObject.Parse(verificationResult) : null
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error in SmartExecute for {method}");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    method = method
                });
            }
        }

        /// <summary>
        /// Get current workflow status
        /// </summary>
        private string GetWorkflowStatus()
        {
            if (_currentWorkflow == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasActiveWorkflow = false,
                    message = "No active workflow"
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                hasActiveWorkflow = true,
                workflow = new
                {
                    originalRequest = _currentWorkflow.OriginalRequest,
                    templateName = _currentWorkflow.TemplateName,
                    isMultiStep = _currentWorkflow.IsMultiStep,
                    currentStep = _currentWorkflowStep + 1,
                    totalSteps = _currentWorkflow.Steps.Count,
                    progress = WorkflowPlannerInstance.GetProgressSummary(),
                    steps = _currentWorkflow.Steps.Select(s => new
                    {
                        stepNumber = s.StepNumber,
                        method = s.Method,
                        description = s.Description,
                        status = s.Status.ToString()
                    })
                }
            });
        }

        /// <summary>
        /// Execute the next step in current workflow
        /// </summary>
        private async Task<string> ExecuteWorkflowStep(JObject parameters)
        {
            if (_currentWorkflow == null || _currentWorkflowStep >= _currentWorkflow.Steps.Count)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "No active workflow or all steps completed"
                });
            }

            var step = _currentWorkflow.Steps[_currentWorkflowStep];
            step.Status = StepStatus.InProgress;

            // Merge any override parameters
            var stepParams = step.Parameters ?? new JObject();
            if (parameters["overrideParams"] is JObject overrides)
            {
                foreach (var prop in overrides.Properties())
                {
                    stepParams[prop.Name] = prop.Value;
                }
            }

            // Execute with smart wrapper
            var result = await SmartExecute(step.Method, stepParams);
            var parsedResult = JObject.Parse(result);

            step.Result = result;
            step.Status = parsedResult["success"]?.Value<bool>() == true
                ? StepStatus.Completed
                : StepStatus.Failed;

            _currentWorkflowStep++;

            var nextStep = _currentWorkflowStep < _currentWorkflow.Steps.Count
                ? _currentWorkflow.Steps[_currentWorkflowStep]
                : null;

            return JsonConvert.SerializeObject(new
            {
                success = step.Status == StepStatus.Completed,
                step = new
                {
                    stepNumber = step.StepNumber,
                    method = step.Method,
                    description = step.Description,
                    status = step.Status.ToString(),
                    result = parsedResult
                },
                nextStep = nextStep != null ? new
                {
                    stepNumber = nextStep.StepNumber,
                    method = nextStep.Method,
                    description = nextStep.Description
                } : null,
                workflowProgress = WorkflowPlannerInstance.GetProgressSummary()
            });
        }

        #endregion
    }
}
