using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.CIPS.Models;
using RevitMCPBridge.CIPS.Services;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge.CIPS
{
    /// <summary>
    /// MCP method endpoints for the CIPS system.
    /// All methods use the cips_ prefix to avoid conflicts with existing methods.
    /// </summary>
    public static class CIPSMethods
    {
        /// <summary>
        /// Register CIPS methods if enabled
        /// </summary>
        public static void RegisterIfEnabled(Dictionary<string, Func<UIApplication, JObject, string>> registry)
        {
            // Always register CIPS methods - configuration check disabled temporarily for debugging
            // TODO: Re-enable after fixing configuration loading timing issue
            // if (!CIPSConfiguration.Instance.Enabled)
            // {
            //     Log.Information("[CIPS] CIPS is disabled, methods not registered");
            //     return;
            // }
            Log.Information("[CIPS] Registering CIPS methods (config check bypassed for debugging)");

            registry["cips_processOperation"] = ProcessOperation;
            registry["cips_getBatchConfidence"] = GetBatchConfidence;
            registry["cips_executePass"] = ExecutePass;
            registry["cips_getQueueStatus"] = GetQueueStatus;
            registry["cips_getReviewQueue"] = GetReviewQueue;
            registry["cips_submitReview"] = SubmitReview;
            registry["cips_getFeedbackHistory"] = GetFeedbackHistory;
            registry["cips_getConfidenceStats"] = GetConfidenceStats;
            registry["cips_setThreshold"] = SetThreshold;
            registry["cips_forceExecute"] = ForceExecute;
            registry["cips_getConfiguration"] = GetConfiguration;

            // Enhancement methods (Enhancements #1-6)
            registry["cips_getValidationRules"] = GetValidationRules;
            registry["cips_validateOperation"] = ValidateOperation;
            registry["cips_getDebugVisualization"] = GetDebugVisualization;
            registry["cips_getDependencyGraph"] = GetDependencyGraph;
            registry["cips_getSessionContext"] = GetSessionContext;
            registry["cips_verifyExecution"] = VerifyExecution;

            // Predictive Intelligence methods (from extracted rules)
            registry["cips_analyzeProjectRules"] = AnalyzeProjectRules;
            registry["cips_getExecutableRules"] = GetExecutableRules;
            registry["cips_suggestNextSteps"] = SuggestNextSteps;
            registry["cips_getSheetPattern"] = GetSheetPattern;

            // Add simple test method to diagnose serialization issues
            registry["cips_simpleTest"] = (uiApp, parameters) =>
            {
                return JsonConvert.SerializeObject(new { success = true, message = "Simple test from CIPSMethods.cs works!" });
            };

            // Test accessing CIPSConfiguration
            registry["cips_configTest"] = (uiApp, parameters) =>
            {
                try
                {
                    var enabled = CIPSConfiguration.Instance.Enabled;
                    var high = CIPSConfiguration.Instance.Thresholds.High;
                    return JsonConvert.SerializeObject(new { success = true, enabled = enabled, highThreshold = high });
                }
                catch (Exception ex)
                {
                    return ResponseBuilder.FromException(ex).Build();
                }
            };

            // Step-by-step diagnostic for AnalyzeProjectRules
            registry["cips_analyzeDebug"] = (uiApp, parameters) =>
            {
                var steps = new List<string>();
                try
                {
                    steps.Add("Starting");

                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "No document", steps = steps });
                    }
                    steps.Add("Got document: " + doc.Title);

                    var evaluator = Services.RuleEvaluator.Instance;
                    steps.Add("Got RuleEvaluator instance");

                    var analysis = evaluator.AnalyzeProject(doc, uiApp);
                    steps.Add("Called AnalyzeProject");

                    return JsonConvert.SerializeObject(new {
                        success = true,
                        steps = steps,
                        projectType = analysis.ProjectType,
                        firm = analysis.DetectedFirm
                    });
                }
                catch (Exception ex)
                {
                    steps.Add("ERROR: " + ex.Message);
                    return JsonConvert.SerializeObject(new { success = false, steps = steps, error = ex.Message, stack = ex.StackTrace?.Substring(0, Math.Min(500, ex.StackTrace?.Length ?? 0)) });
                }
            };

            // Full serialization test - mimics AnalyzeProjectRules exactly
            registry["cips_serializeTest"] = (uiApp, parameters) =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "No document" });
                    }

                    var analysis = Services.RuleEvaluator.Instance.AnalyzeProject(doc, uiApp);

                    if (!analysis.Success)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = analysis.Error ?? "Unknown error" });
                    }

                    // Build response piece by piece to find what breaks
                    var response = new JObject();
                    response["success"] = true;
                    response["projectName"] = doc.Title;

                    // Test detection object
                    var detection = new JObject();
                    detection["projectType"] = analysis.ProjectType ?? "null";
                    detection["confidence"] = analysis.ProjectTypeConfidence;
                    detection["firm"] = analysis.DetectedFirm ?? "null";
                    detection["firmPattern"] = analysis.FirmPattern ?? "null";
                    detection["indicators"] = analysis.Indicators != null ? JArray.FromObject(analysis.Indicators) : new JArray();
                    response["detection"] = detection;

                    // Test rules object
                    var rules = new JObject();
                    rules["applicable"] = analysis.ApplicableRuleCount;
                    rules["triggered"] = analysis.TriggeredRuleCount;
                    rules["triggeredRules"] = analysis.TriggeredRules != null
                        ? JArray.FromObject(analysis.TriggeredRules.Select(r => new {
                            ruleId = r.RuleId ?? "",
                            name = r.Name ?? "",
                            category = r.Category ?? "",
                            confidence = r.Confidence
                        }))
                        : new JArray();
                    response["rules"] = rules;

                    // Test suggested actions
                    response["suggestedActions"] = analysis.SuggestedActions != null
                        ? JArray.FromObject(analysis.SuggestedActions.Select(a => new {
                            ruleId = a.RuleId ?? "",
                            ruleName = a.RuleName ?? "",
                            action = a.ActionType ?? "",
                            description = a.Description ?? "",
                            sheetPattern = a.SheetPattern ?? "",
                            namingPattern = a.NamingPattern ?? "",
                            confidence = a.Confidence,
                            mcpMethods = a.McpMethods ?? new List<string>()
                        }))
                        : new JArray();

                    return response.ToString(Formatting.None);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = ex.Message,
                        exceptionType = ex.GetType().Name,
                        stack = ex.StackTrace?.Substring(0, Math.Min(500, ex.StackTrace?.Length ?? 0)) ?? ""
                    });
                }
            };

            // Incremental test - returns at step parameter to isolate failure
            registry["cips_incrementalTest"] = (uiApp, parameters) =>
            {
                int step = parameters["step"]?.Value<int>() ?? 0;

                try
                {
                    if (step == 0) return "{\"success\":true,\"step\":0,\"msg\":\"Before any operation\"}";

                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "{\"success\":false,\"error\":\"No document\"}";
                    if (step == 1) return "{\"success\":true,\"step\":1,\"msg\":\"Got document: " + doc.Title + "\"}";

                    var analysis = Services.RuleEvaluator.Instance.AnalyzeProject(doc, uiApp);
                    if (step == 2) return "{\"success\":true,\"step\":2,\"msg\":\"AnalyzeProject returned, Success=" + analysis.Success + "\"}";

                    if (!analysis.Success) return "{\"success\":false,\"error\":\"" + (analysis.Error ?? "Unknown") + "\"}";
                    if (step == 3) return "{\"success\":true,\"step\":3,\"projectType\":\"" + (analysis.ProjectType ?? "null") + "\"}";

                    int triggeredCount = analysis.TriggeredRules?.Count ?? 0;
                    if (step == 4) return "{\"success\":true,\"step\":4,\"triggeredCount\":" + triggeredCount + "}";

                    int actionsCount = analysis.SuggestedActions?.Count ?? 0;
                    if (step == 5) return "{\"success\":true,\"step\":5,\"actionsCount\":" + actionsCount + "}";

                    // Try serializing just the basic detection info
                    if (step == 6)
                    {
                        var basic = new {
                            success = true,
                            step = 6,
                            projectType = analysis.ProjectType ?? "null",
                            firm = analysis.DetectedFirm ?? "null"
                        };
                        return JsonConvert.SerializeObject(basic);
                    }

                    // Try serializing with triggered rules count but not content
                    if (step == 7)
                    {
                        var withCounts = new {
                            success = true,
                            step = 7,
                            triggeredRuleCount = triggeredCount,
                            suggestedActionCount = actionsCount
                        };
                        return JsonConvert.SerializeObject(withCounts);
                    }

                    // Try serializing first triggered rule if any
                    if (step == 8 && triggeredCount > 0)
                    {
                        var firstRule = analysis.TriggeredRules[0];
                        return JsonConvert.SerializeObject(new {
                            success = true,
                            step = 8,
                            firstRuleId = firstRule.RuleId ?? "null",
                            firstRuleName = firstRule.Name ?? "null"
                        });
                    }

                    return "{\"success\":true,\"step\":\"done\",\"msg\":\"All steps passed\"}";
                }
                catch (Exception ex)
                {
                    return "{\"success\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\",\"type\":\"" + ex.GetType().Name + "\"}";
                }
            };

            // Initialize the orchestrator
            Log.Information("[CIPS] Registered 22 CIPS methods (11 core + 6 enhancements + 4 predictive + 1 test)");
        }

        /// <summary>
        /// Process an operation through the confidence system.
        /// Parameters:
        /// - method: The MCP method to execute (e.g., "createWall")
        /// - parameters: The parameters for that method
        /// - autoExecute: (optional) Execute high-confidence immediately (default: true)
        /// </summary>
        public static string ProcessOperation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var method = parameters["method"]?.ToString();
                var methodParams = parameters["parameters"] as JObject ?? new JObject();
                var autoExecute = parameters["autoExecute"]?.Value<bool>() ?? true;

                if (string.IsNullOrEmpty(method))
                {
                    return Error("'method' parameter is required");
                }

                var envelope = CIPSOrchestrator.Instance.ProcessOperation(method, methodParams, autoExecute);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    envelope = envelope,
                    confidenceLevel = envelope.GetLevel().ToString(),
                    executed = envelope.Status == ProcessingStatus.Executed || envelope.Status == ProcessingStatus.Verified,
                    inReview = envelope.Status == ProcessingStatus.InReview
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get confidence scores for a batch of operations without executing.
        /// Parameters:
        /// - operations: Array of { method, parameters }
        /// </summary>
        public static string GetBatchConfidence(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var operations = parameters["operations"] as JArray;
                if (operations == null || operations.Count == 0)
                {
                    return Error("'operations' array is required");
                }

                var results = new List<object>();
                foreach (var op in operations)
                {
                    var method = op["method"]?.ToString();
                    var methodParams = op["parameters"] as JObject ?? new JObject();

                    if (!string.IsNullOrEmpty(method))
                    {
                        var envelope = CIPSOrchestrator.Instance.CalculateConfidence(method, methodParams);
                        results.Add(new
                        {
                            method = method,
                            confidence = envelope.OverallConfidence,
                            level = envelope.GetLevel().ToString(),
                            factors = envelope.Factors.Select(f => new
                            {
                                name = f.FactorName,
                                score = f.Score,
                                reason = f.Reason
                            }),
                            alternativeCount = envelope.Alternatives.Count
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    operationCount = results.Count,
                    results = results,
                    highConfidence = results.Count(r => ((dynamic)r).level == "High"),
                    mediumConfidence = results.Count(r => ((dynamic)r).level == "Medium"),
                    lowConfidence = results.Count(r => ((dynamic)r).level == "Low")
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Execute a specific pass for a workflow.
        /// Parameters:
        /// - workflowId: ID of the workflow
        /// - passNumber: 1, 2, or 3
        /// </summary>
        public static string ExecutePass(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                // For now, return not implemented - full workflow support is Phase 2
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Full workflow support is planned for Phase 2. Use cips_processOperation for single operations."
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get current queue status.
        /// </summary>
        public static string GetQueueStatus(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var stats = CIPSOrchestrator.Instance.GetQueueStats();
                var feedbackStats = CIPSOrchestrator.Instance.GetFeedbackStats();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    reviewQueue = new
                    {
                        total = stats.TotalItems,
                        pending = stats.PendingItems,
                        approved = stats.ApprovedItems,
                        modified = stats.ModifiedItems,
                        rejected = stats.RejectedItems,
                        expired = stats.ExpiredItems,
                        averageConfidence = stats.AverageConfidence,
                        oldestPending = stats.OldestPending
                    },
                    feedback = new
                    {
                        totalRecords = feedbackStats.TotalFeedback,
                        accuracyRate = feedbackStats.OverallAccuracyRate,
                        patternCount = feedbackStats.PatternCount
                    },
                    configuration = new
                    {
                        enabled = CIPSConfiguration.Instance.Enabled,
                        highThreshold = CIPSConfiguration.Instance.Thresholds.High,
                        mediumThreshold = CIPSConfiguration.Instance.Thresholds.Medium,
                        maxPasses = CIPSConfiguration.Instance.MultiPass.MaxPasses
                    }
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get items in the human review queue.
        /// Parameters:
        /// - limit: (optional) Maximum items to return (default: 20)
        /// </summary>
        public static string GetReviewQueue(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var limit = parameters["limit"]?.Value<int>() ?? 20;
                var items = CIPSOrchestrator.Instance.GetPendingReviews().Take(limit).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = items.Count,
                    items = items.Select(i => new
                    {
                        reviewId = i.ReviewId,
                        methodName = i.Envelope?.MethodName,
                        confidence = i.Envelope?.OverallConfidence,
                        reason = i.Reason,
                        questions = i.Questions,
                        options = i.Options.Select(o => new
                        {
                            id = o.Id,
                            label = o.Label,
                            description = o.Description,
                            isRecommended = o.IsRecommended
                        }),
                        aiRecommendation = i.AIRecommendation,
                        queuedAt = i.QueuedAt,
                        expiresAt = i.ExpiresAt
                    })
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Submit a human review decision.
        /// Parameters:
        /// - reviewId: ID of the review item
        /// - decision: "approve", "modify", "reject", or "skip"
        /// - modifiedParameters: (optional) Modified parameters if decision is "modify"
        /// - notes: (optional) Reviewer notes
        /// </summary>
        public static string SubmitReview(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var reviewId = parameters["reviewId"]?.ToString();
                var decisionStr = parameters["decision"]?.ToString();
                var modifiedParams = parameters["modifiedParameters"] as JObject;
                var notes = parameters["notes"]?.ToString();

                if (string.IsNullOrEmpty(reviewId))
                {
                    return Error("'reviewId' is required");
                }

                if (string.IsNullOrEmpty(decisionStr))
                {
                    return Error("'decision' is required (approve, modify, reject, skip)");
                }

                if (!Enum.TryParse<ReviewDecision>(decisionStr, true, out var decision))
                {
                    return Error($"Invalid decision: {decisionStr}. Use: approve, modify, reject, or skip");
                }

                var success = CIPSOrchestrator.Instance.SubmitReview(reviewId, decision, modifiedParams, notes);

                if (!success)
                {
                    return Error($"Review item not found or already processed: {reviewId}");
                }

                // If approved/modified, execute the operation
                var item = CIPSOrchestrator.Instance.ReviewQueue.GetItem(reviewId);
                object executionResult = null;

                if ((decision == ReviewDecision.Approve || decision == ReviewDecision.Modify) && item?.Envelope != null)
                {
                    var paramsToUse = modifiedParams ?? item.Envelope.Parameters;
                    var envelope = CIPSOrchestrator.Instance.ForceExecute(item.Envelope.MethodName, paramsToUse);
                    executionResult = new
                    {
                        executed = envelope.Status == ProcessingStatus.Executed,
                        result = envelope.Result
                    };
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    reviewId = reviewId,
                    decision = decision.ToString(),
                    execution = executionResult
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get feedback history.
        /// Parameters:
        /// - limit: (optional) Maximum records to return (default: 50)
        /// - methodName: (optional) Filter by method name
        /// </summary>
        public static string GetFeedbackHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var limit = parameters["limit"]?.Value<int>() ?? 50;
                var methodFilter = parameters["methodName"]?.ToString();

                var history = CIPSOrchestrator.Instance.FeedbackLearner.GetHistory(limit);

                if (!string.IsNullOrEmpty(methodFilter))
                {
                    history = history.Where(f => f.MethodName.Equals(methodFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                var stats = CIPSOrchestrator.Instance.GetFeedbackStats();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stats = stats,
                    records = history.Select(f => new
                    {
                        feedbackId = f.FeedbackId,
                        methodName = f.MethodName,
                        originalConfidence = f.OriginalConfidence,
                        humanDecision = f.HumanDecision.ToString(),
                        aiWasCorrect = f.AIWasCorrect,
                        recordedAt = f.RecordedAt
                    })
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get confidence statistics.
        /// </summary>
        public static string GetConfidenceStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var feedbackStats = CIPSOrchestrator.Instance.GetFeedbackStats();
                var queueStats = CIPSOrchestrator.Instance.GetQueueStats();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    overall = new
                    {
                        totalFeedback = feedbackStats.TotalFeedback,
                        totalCorrect = feedbackStats.TotalCorrect,
                        accuracyRate = feedbackStats.OverallAccuracyRate,
                        patternCount = feedbackStats.PatternCount
                    },
                    topMethods = feedbackStats.TopMethods,
                    thresholds = new
                    {
                        high = CIPSConfiguration.Instance.Thresholds.High,
                        medium = CIPSConfiguration.Instance.Thresholds.Medium,
                        low = CIPSConfiguration.Instance.Thresholds.Low
                    },
                    queue = new
                    {
                        pending = queueStats.PendingItems,
                        averageConfidence = queueStats.AverageConfidence
                    }
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Dynamically set threshold for an operation type.
        /// Parameters:
        /// - methodName: The method to set threshold for
        /// - high: (optional) High threshold
        /// - medium: (optional) Medium threshold
        /// - low: (optional) Low threshold
        /// </summary>
        public static string SetThreshold(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Note: This would require modifying configuration at runtime
                // For now, return guidance to modify appsettings.json
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = "Runtime threshold modification not yet implemented. " +
                              "Please modify the CIPS.OperationThresholds section in appsettings.json"
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Force execute an operation regardless of confidence.
        /// Parameters:
        /// - method: The MCP method to execute
        /// - parameters: The parameters for that method
        /// </summary>
        public static string ForceExecute(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var method = parameters["method"]?.ToString();
                var methodParams = parameters["parameters"] as JObject ?? new JObject();

                if (string.IsNullOrEmpty(method))
                {
                    return Error("'method' parameter is required");
                }

                var envelope = CIPSOrchestrator.Instance.ForceExecute(method, methodParams);

                return JsonConvert.SerializeObject(new
                {
                    success = envelope.Status == ProcessingStatus.Executed,
                    envelope = envelope,
                    warning = "Executed without confidence check"
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get current CIPS configuration.
        /// </summary>
        public static string GetConfiguration(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var config = CIPSConfiguration.Instance;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    enabled = config.Enabled,
                    thresholds = new
                    {
                        high = config.Thresholds.High,
                        medium = config.Thresholds.Medium,
                        low = config.Thresholds.Low
                    },
                    multiPass = new
                    {
                        maxPasses = config.MultiPass.MaxPasses,
                        contextBoostPerPass = config.MultiPass.ContextBoostPerPass
                    },
                    reviewQueue = new
                    {
                        path = config.GetReviewQueuePath(),
                        maxSize = config.ReviewQueue.MaxSize,
                        expireHours = config.ReviewQueue.ExpireHours
                    },
                    feedback = new
                    {
                        minSamplesToLearn = config.Feedback.MinSamplesToLearn,
                        maxAdjustment = config.Feedback.MaxAdjustment
                    },
                    operationThresholds = config.OperationThresholds
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        #region Enhancement Methods (Enhancements #1-6)

        /// <summary>
        /// Get current architectural validation rules.
        /// Enhancement #3: Architectural Validation Rules
        /// </summary>
        public static string GetValidationRules(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var config = CIPSConfiguration.Instance;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    validationEnabled = true,
                    rules = new
                    {
                        walls = new
                        {
                            minLengthInches = 6,
                            maxLengthFeet = 100,
                            validThicknessesInches = new[] { 4, 6, 8, 10, 12 },
                            maxHeightFeet = 40,
                            minHeightFeet = 7
                        },
                        doors = new
                        {
                            minWidthInches = 24,
                            maxWidthInches = 48,
                            standardWidthsInches = new[] { 28, 30, 32, 34, 36 },
                            minHeightInches = 78,
                            maxHeightInches = 96
                        },
                        windows = new
                        {
                            minWidthInches = 12,
                            maxWidthInches = 120,
                            minHeightInches = 12,
                            maxHeightInches = 96,
                            minSillHeightInches = 18
                        },
                        rooms = new
                        {
                            minAreaSqft = 25,
                            minDimensionFeet = 4,
                            bathroomMinSqft = 35,
                            bedroomMinSqft = 70,
                            kitchenMinSqft = 50
                        }
                    }
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Validate an operation without executing.
        /// Enhancement #3: Architectural Validation Rules
        /// Parameters:
        /// - method: The MCP method to validate
        /// - parameters: The parameters for that method
        /// </summary>
        public static string ValidateOperation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var method = parameters["method"]?.ToString();
                var methodParams = parameters["parameters"] as JObject ?? new JObject();

                if (string.IsNullOrEmpty(method))
                {
                    return Error("'method' parameter is required");
                }

                var validator = new ArchitecturalValidator();
                var factor = validator.Validate(method, methodParams);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    method = method,
                    validation = new
                    {
                        score = factor.Score,
                        passed = factor.Score >= 1.0,
                        reason = factor.Reason,
                        factorName = factor.FactorName,
                        weight = factor.Weight
                    }
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get debug visualization for an operation.
        /// Enhancement #2: Visual Debug Overlay
        /// Parameters:
        /// - operationId: ID of the operation to visualize
        /// - outputFormat: (optional) "html", "svg", "json", or "all" (default: "html")
        /// - openInBrowser: (optional) Open HTML in browser (default: false)
        /// </summary>
        public static string GetDebugVisualization(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var operationId = parameters["operationId"]?.ToString();
                var formatStr = parameters["outputFormat"]?.ToString() ?? "html";
                var openBrowser = parameters["openInBrowser"]?.Value<bool>() ?? false;

                if (string.IsNullOrEmpty(operationId))
                {
                    return Error("'operationId' parameter is required");
                }

                // Try to find the envelope in the orchestrator
                var envelope = CIPSOrchestrator.Instance.GetEnvelopeById(operationId);
                if (envelope == null)
                {
                    return Error($"Operation not found: {operationId}");
                }

                var debugService = new VisualDebugService();
                VisualizationFormat format = VisualizationFormat.Html;
                Enum.TryParse(formatStr, true, out format);

                var settings = new OverlaySettings { OutputFormat = format };
                var visualization = debugService.GenerateVisualization(envelope, settings);

                if (openBrowser && !string.IsNullOrEmpty(visualization.OutputPath))
                {
                    System.Diagnostics.Process.Start(visualization.OutputPath);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    operationId = operationId,
                    format = format.ToString(),
                    outputPath = visualization.OutputPath,
                    generatedAt = visualization.GeneratedAt,
                    hasHtml = !string.IsNullOrEmpty(visualization.HtmlReport),
                    hasSvg = !string.IsNullOrEmpty(visualization.SvgDiagram),
                    hasJson = !string.IsNullOrEmpty(visualization.JsonData),
                    // Include content based on format
                    content = format == VisualizationFormat.Json ? visualization.JsonData :
                              format == VisualizationFormat.Svg ? visualization.SvgDiagram :
                              visualization.HtmlReport
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get the dependency graph for a workflow.
        /// Enhancement #4: Decision Dependency Graph
        /// Parameters:
        /// - workflowId: (optional) ID of the workflow to get graph for
        /// </summary>
        public static string GetDependencyGraph(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var graphData = CIPSOrchestrator.Instance.PassCoordinator.GetDependencyGraph();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    graph = new
                    {
                        nodeCount = graphData.Nodes.Count,
                        edgeCount = graphData.Edges.Count,
                        nodes = graphData.Nodes,
                        edges = graphData.Edges.Select(e => new
                        {
                            from = e.From,
                            to = e.To,
                            type = e.Type.ToString(),
                            strength = e.Strength,
                            notes = e.Notes
                        })
                    }
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get current session learning state.
        /// Enhancement #5: Session Learning
        /// Parameters:
        /// - startNew: (optional) Start a new session (default: false)
        /// - projectName: (optional) Project name for new session
        /// - endCurrent: (optional) End current session and get summary
        /// </summary>
        public static string GetSessionContext(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var startNew = parameters["startNew"]?.Value<bool>() ?? false;
                var projectName = parameters["projectName"]?.ToString();
                var endCurrent = parameters["endCurrent"]?.Value<bool>() ?? false;

                var feedbackLearner = CIPSOrchestrator.Instance.FeedbackLearner;

                if (endCurrent && feedbackLearner.CurrentSession != null)
                {
                    var outcome = feedbackLearner.EndSession();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        action = "ended",
                        outcome = outcome
                    }, Formatting.None);
                }

                if (startNew)
                {
                    var session = feedbackLearner.StartSession(projectName);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        action = "started",
                        session = new
                        {
                            sessionId = session.SessionId,
                            projectName = session.ProjectName,
                            startedAt = session.StartedAt
                        }
                    }, Formatting.None);
                }

                // Return current session state
                var currentSession = feedbackLearner.CurrentSession;
                if (currentSession == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hasActiveSession = false,
                        message = "No active session. Use startNew=true to start one."
                    }, Formatting.None);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasActiveSession = true,
                    session = new
                    {
                        sessionId = currentSession.SessionId,
                        projectName = currentSession.ProjectName,
                        projectPath = currentSession.ProjectPath,
                        startedAt = currentSession.StartedAt,
                        patternsLearned = currentSession.LearnedPatterns.Count,
                        rulesCount = currentSession.ProjectRules.Count,
                        terminologyCount = currentSession.TerminologyMap.Count,
                        patterns = currentSession.LearnedPatterns.Select(p => new
                        {
                            patternId = p.PatternId,
                            methodName = p.MethodName,
                            description = p.Description,
                            confidenceAdjustment = p.ConfidenceAdjustment,
                            sampleCount = p.SampleCount
                        }),
                        rules = currentSession.ProjectRules
                    }
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Manually trigger verification on an executed element.
        /// Enhancement #6: Verification Loops
        /// Parameters:
        /// - operationId: ID of the operation to verify
        /// </summary>
        public static string VerifyExecution(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureInitialized(uiApp);

                var operationId = parameters["operationId"]?.ToString();

                if (string.IsNullOrEmpty(operationId))
                {
                    return Error("'operationId' parameter is required");
                }

                var envelope = CIPSOrchestrator.Instance.GetEnvelopeById(operationId);
                if (envelope == null)
                {
                    return Error($"Operation not found: {operationId}");
                }

                if (envelope.Status != ProcessingStatus.Executed &&
                    envelope.Status != ProcessingStatus.Verified &&
                    envelope.Status != ProcessingStatus.VerificationFailed)
                {
                    return Error($"Operation has not been executed yet. Status: {envelope.Status}");
                }

                // Run verification
                var verifier = new PostExecutionVerifier(uiApp);
                var report = verifier.RunVerifications(envelope);
                envelope.VerificationReport = report;

                // Update status based on verification
                if (report.AllPassed)
                {
                    envelope.Status = ProcessingStatus.Verified;
                }
                else
                {
                    envelope.Status = ProcessingStatus.VerificationFailed;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    operationId = operationId,
                    verification = new
                    {
                        passed = report.AllPassed,
                        totalChecks = report.TotalChecks,
                        passedCount = report.PassedCount,
                        failedCount = report.FailedCount,
                        summary = report.GetSummary(),
                        checks = report.Checks.Select(c => new
                        {
                            checkName = c.CheckName,
                            passed = c.Passed,
                            expected = c.Expected,
                            actual = c.Actual,
                            tolerance = c.Tolerance,
                            deviation = c.Deviation,
                            message = c.Message,
                            executionTimeMs = c.ExecutionTimeMs
                        }),
                        failures = report.Failures,
                        executionTimeMs = report.TotalExecutionTimeMs
                    },
                    newStatus = envelope.Status.ToString()
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        #endregion

        #region Predictive Intelligence Methods

        /// <summary>
        /// Analyze the current project against executable rules.
        /// Detects project type, firm pattern, and returns applicable rules with suggested actions.
        /// Parameters: None required (uses active document)
        /// </summary>
        public static string AnalyzeProjectRules(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return Error("No active document");
                }

                var analysis = RuleEvaluator.Instance.AnalyzeProject(doc, uiApp);

                if (!analysis.Success)
                {
                    return Error(analysis.Error);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    detection = new
                    {
                        projectType = analysis.ProjectType,
                        confidence = analysis.ProjectTypeConfidence,
                        firm = analysis.DetectedFirm,
                        firmPattern = analysis.FirmPattern,
                        indicators = analysis.Indicators
                    },
                    rules = new
                    {
                        applicable = analysis.ApplicableRuleCount,
                        triggered = analysis.TriggeredRuleCount,
                        triggeredRules = analysis.TriggeredRules
                    },
                    suggestedActions = analysis.SuggestedActions
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get the loaded executable rules data.
        /// Parameters:
        /// - category: (optional) Filter by rule category
        /// - projectType: (optional) Filter by applicable project type
        /// </summary>
        public static string GetExecutableRules(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var rulesData = RuleEvaluator.Instance.GetRulesData();
                var category = parameters["category"]?.ToString();
                var projectType = parameters["projectType"]?.ToString();

                var rules = rulesData["rules"] as JArray;
                if (rules == null)
                {
                    return Error("No rules loaded");
                }

                var filteredRules = rules.Cast<JObject>();

                // Apply category filter
                if (!string.IsNullOrEmpty(category))
                {
                    filteredRules = filteredRules.Where(r =>
                        (r["category"]?.ToString() ?? "").Equals(category, StringComparison.OrdinalIgnoreCase));
                }

                // Apply project type filter
                if (!string.IsNullOrEmpty(projectType))
                {
                    filteredRules = filteredRules.Where(r =>
                    {
                        var appliesTo = r["applies_to"]?.ToObject<List<string>>() ?? new List<string>();
                        return appliesTo.Contains(projectType) || appliesTo.Contains("all");
                    });
                }

                var rulesList = filteredRules.ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    version = rulesData["version"]?.ToString(),
                    generatedAt = rulesData["generated_at"]?.ToString(),
                    totalRules = rulesData["total_rules"]?.Value<int>() ?? 0,
                    filteredCount = rulesList.Count,
                    rules = rulesList.Select(r => new
                    {
                        ruleId = r["rule_id"]?.ToString(),
                        name = r["name"]?.ToString(),
                        category = r["category"]?.ToString(),
                        appliesTo = r["applies_to"]?.ToObject<List<string>>(),
                        confidence = r["confidence"]?.Value<double>() ?? 0,
                        validationCount = r["validation_count"]?.Value<int>() ?? 0,
                        mcpMethods = r["mcp_methods"]?.ToObject<List<string>>()
                    }),
                    firmPatterns = rulesData["firm_numbering_patterns"],
                    projectTypeDetection = rulesData["project_type_detection"]
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Suggest next steps based on current project state and rules.
        /// Returns prioritized list of actions to take.
        /// Parameters:
        /// - maxSuggestions: (optional) Maximum number of suggestions (default: 5)
        /// - includeCompleted: (optional) Include suggestions for rules already satisfied
        /// </summary>
        public static string SuggestNextSteps(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return Error("No active document");
                }

                var maxSuggestions = parameters["maxSuggestions"]?.Value<int>() ?? 5;
                var includeCompleted = parameters["includeCompleted"]?.Value<bool>() ?? false;

                var analysis = RuleEvaluator.Instance.AnalyzeProject(doc, uiApp);

                if (!analysis.Success)
                {
                    return Error(analysis.Error);
                }

                // Sort actions by confidence and priority
                var prioritizedActions = analysis.SuggestedActions
                    .OrderByDescending(a => a.Confidence)
                    .Take(maxSuggestions)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectType = analysis.ProjectType,
                    firm = analysis.DetectedFirm,
                    firmPattern = analysis.FirmPattern,
                    suggestionCount = prioritizedActions.Count,
                    nextSteps = prioritizedActions.Select((a, idx) => new
                    {
                        priority = idx + 1,
                        ruleId = a.RuleId,
                        ruleName = a.RuleName,
                        action = a.ActionType,
                        description = a.Description,
                        sheetPattern = a.SheetPattern,
                        namingPattern = a.NamingPattern,
                        confidence = a.Confidence,
                        mcpMethods = a.McpMethods,
                        example = GenerateExample(a, analysis.DetectedFirm)
                    }),
                    message = prioritizedActions.Count > 0
                        ? $"Found {prioritizedActions.Count} suggested actions based on project type '{analysis.ProjectType}'"
                        : "No specific suggestions - project may be complete or type not fully detected"
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Get the sheet numbering pattern for a specific rule and firm.
        /// Parameters:
        /// - ruleId: ID of the rule (e.g., "LIFE_SAFETY_PLANS")
        /// - firmName: (optional) Firm name to get pattern for
        /// </summary>
        public static string GetSheetPattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var ruleId = parameters["ruleId"]?.ToString();
                var firmName = parameters["firmName"]?.ToString();

                if (string.IsNullOrEmpty(ruleId))
                {
                    return Error("'ruleId' parameter is required");
                }

                var pattern = RuleEvaluator.Instance.GetSheetPattern(ruleId, firmName);

                if (pattern == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"No pattern found for rule '{ruleId}'"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ruleId = ruleId,
                    firmName = firmName ?? RuleEvaluator.Instance.DetectedFirm ?? "default",
                    sheetPattern = pattern,
                    example = pattern
                        .Replace("{level}", "2")
                        .Replace("{unit}", "A")
                        .Replace("{n}", "1")
                        .Replace("{discipline}", "A")
                        .Replace("{phase}", "1")
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Generate an example based on a suggested action
        /// </summary>
        private static object GenerateExample(SuggestedAction action, string firm)
        {
            if (string.IsNullOrEmpty(action.SheetPattern))
                return null;

            var example = action.SheetPattern
                .Replace("{level}", "2")
                .Replace("{unit}", "A")
                .Replace("{n}", "1")
                .Replace("{discipline}", "A")
                .Replace("{phase}", "1")
                .Replace("{firm_pattern}", firm ?? "default");

            var naming = action.NamingPattern?
                .Replace("{level_name}", "LEVEL 2")
                .Replace("{unit_letter}", "A")
                .Replace("{direction1}", "NORTH")
                .Replace("{direction2}", "SOUTH");

            return new
            {
                sheetNumber = example,
                sheetName = naming
            };
        }

        #endregion

        /// <summary>
        /// Ensure the orchestrator is initialized
        /// </summary>
        private static void EnsureInitialized(UIApplication uiApp)
        {
            if (!CIPSConfiguration.Instance.Enabled)
            {
                throw new InvalidOperationException("CIPS is not enabled. Set CIPS.Enabled = true in appsettings.json");
            }

            CIPSOrchestrator.Instance.Initialize(uiApp);
        }

        /// <summary>
        /// Create error response
        /// </summary>
        private static string Error(string message)
        {
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = message
            });
        }
    }
}
