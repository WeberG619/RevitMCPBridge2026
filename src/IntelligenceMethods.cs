using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// MCP methods for accessing the intelligence system.
    /// These methods expose workflow analysis, preference learning,
    /// layout intelligence, and proactive assistance to Claude.
    /// </summary>
    public static class IntelligenceMethods
    {
        #region Proactive Assistant Methods

        /// <summary>
        /// Get current suggestions from the proactive assistant
        /// Parameters: maxCount (optional, default 5)
        /// </summary>
        [MCPMethod("getSuggestions", Category = "Intelligence", Description = "Get current suggestions from the proactive assistant")]
        public static string GetSuggestions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int maxCount = parameters["maxCount"]?.ToObject<int>() ?? 5;

                // Update context first
                ProactiveAssistant.Instance.UpdateContext(uiApp);

                var suggestions = ProactiveAssistant.Instance.GetSuggestions(maxCount);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = suggestions.Count,
                    suggestions = suggestions.Select(s => new
                    {
                        type = s.Type.ToString(),
                        title = s.Title,
                        description = s.Description,
                        relevance = Math.Round(s.Relevance, 2),
                        suggestedAction = s.SuggestedAction != null ? new
                        {
                            actionType = s.SuggestedAction.ActionType,
                            parameters = s.SuggestedAction.Parameters
                        } : null
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get comprehensive assistance summary
        /// </summary>
        [MCPMethod("getAssistanceSummary", Category = "Intelligence", Description = "Get comprehensive assistance summary")]
        public static string GetAssistanceSummary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                // Update context
                ProactiveAssistant.Instance.UpdateContext(uiApp);

                var summary = ProactiveAssistant.Instance.GetAssistanceSummary(doc);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    context = new
                    {
                        document = summary.CurrentContext.DocumentName,
                        activeView = summary.CurrentContext.ActiveViewName,
                        viewType = summary.CurrentContext.ActiveViewType,
                        isOnSheet = summary.CurrentContext.IsOnSheet,
                        sheetNumber = summary.CurrentContext.SheetNumber,
                        selectedCount = summary.CurrentContext.SelectedElementCount
                    },
                    suggestions = summary.TopSuggestions.Select(s => new
                    {
                        type = s.Type.ToString(),
                        title = s.Title,
                        description = s.Description,
                        relevance = s.Relevance
                    }),
                    learning = new
                    {
                        status = summary.LearningProgress.Status,
                        progress = Math.Round(summary.LearningProgress.ProgressPercentage, 1),
                        observations = summary.LearningProgress.TotalObservations,
                        patterns = summary.LearningProgress.PatternsDiscovered,
                        preferences = summary.LearningProgress.PreferencesLearned
                    },
                    effectiveness = new
                    {
                        suggestionsAccepted = summary.SuggestionsAccepted,
                        suggestionsRejected = summary.SuggestionsRejected
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get task-specific assistance
        /// Parameters: task (string description of what user wants to do)
        /// </summary>
        [MCPMethod("getTaskAssistance", Category = "Intelligence", Description = "Get task-specific assistance")]
        public static string GetTaskAssistance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                string taskDescription = parameters["task"]?.ToString();
                if (string.IsNullOrEmpty(taskDescription))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "task parameter is required" });
                }

                var assistance = ProactiveAssistant.Instance.GetTaskAssistance(taskDescription, doc);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    task = assistance.TaskDescription,
                    steps = assistance.Steps.Select(s => new
                    {
                        step = s.StepNumber,
                        description = s.Description,
                        action = s.Action
                    }),
                    recommendations = assistance.Recommendations
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Accept or reject a suggestion (for learning)
        /// Parameters: title (suggestion title), accepted (bool)
        /// </summary>
        [MCPMethod("respondToSuggestion", Category = "Intelligence", Description = "Accept or reject a suggestion for learning")]
        public static string RespondToSuggestion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string title = parameters["title"]?.ToString();
                bool accepted = parameters["accepted"]?.ToObject<bool>() ?? false;

                if (string.IsNullOrEmpty(title))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "title is required" });
                }

                if (accepted)
                {
                    ProactiveAssistant.Instance.AcceptSuggestion(title);
                }
                else
                {
                    ProactiveAssistant.Instance.RejectSuggestion(title);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Suggestion '{title}' was {(accepted ? "accepted" : "rejected")}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Workflow Analysis Methods

        /// <summary>
        /// Get workflow statistics
        /// </summary>
        [MCPMethod("getWorkflowStatistics", Category = "Intelligence", Description = "Get workflow statistics")]
        public static string GetWorkflowStatistics(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var stats = WorkflowAnalyzer.Instance.GetStatistics();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    statistics = new
                    {
                        totalActions = stats.TotalActionsRecorded,
                        uniqueActionTypes = stats.UniqueActionTypes,
                        patternsDetected = stats.PatternsDetected,
                        taskSessions = stats.TaskSessionsRecorded,
                        mostCommonAction = stats.MostCommonAction,
                        sessionDuration = stats.SessionDuration.TotalMinutes.ToString("F1") + " minutes",
                        currentTask = stats.CurrentTask
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detected workflow patterns
        /// Parameters: minOccurrences (optional, default 3)
        /// </summary>
        [MCPMethod("getWorkflowPatterns", Category = "Intelligence", Description = "Get detected workflow patterns")]
        public static string GetWorkflowPatterns(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int minOccurrences = parameters["minOccurrences"]?.ToObject<int>() ?? 3;

                var patterns = WorkflowAnalyzer.Instance.GetPatterns(minOccurrences);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = patterns.Count,
                    patterns = patterns.Select(p => new
                    {
                        description = p.Description,
                        occurrences = p.Occurrences,
                        sequenceLength = p.SequenceLength,
                        firstSeen = p.FirstSeen.ToString("yyyy-MM-dd HH:mm"),
                        lastSeen = p.LastSeen.ToString("yyyy-MM-dd HH:mm")
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get most frequent actions
        /// Parameters: top (optional, default 20)
        /// </summary>
        [MCPMethod("getActionFrequencies", Category = "Intelligence", Description = "Get most frequent actions")]
        public static string GetActionFrequencies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int top = parameters["top"]?.ToObject<int>() ?? 20;

                var frequencies = WorkflowAnalyzer.Instance.GetActionFrequencies(top);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = frequencies.Count,
                    actions = frequencies.Select(kvp => new
                    {
                        action = kvp.Key,
                        count = kvp.Value
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Start tracking a named task
        /// Parameters: taskName (string)
        /// </summary>
        [MCPMethod("startTaskTracking", Category = "Intelligence", Description = "Start tracking a named task")]
        public static string StartTaskTracking(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string taskName = parameters["taskName"]?.ToString();
                if (string.IsNullOrEmpty(taskName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "taskName is required" });
                }

                WorkflowAnalyzer.Instance.StartTask(taskName);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Started tracking task: {taskName}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// End current task tracking
        /// </summary>
        [MCPMethod("endTaskTracking", Category = "Intelligence", Description = "End current task tracking")]
        public static string EndTaskTracking(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var session = WorkflowAnalyzer.Instance.EndTask();

                if (session == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active task to end" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    session = new
                    {
                        taskName = session.TaskName,
                        duration = (session.EndTime - session.StartTime).TotalMinutes.ToString("F1") + " minutes",
                        actionCount = session.Actions.Count
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Preference Learning Methods

        /// <summary>
        /// Get all learned preferences
        /// </summary>
        [MCPMethod("getLearnedPreferences", Category = "Intelligence", Description = "Get all learned preferences")]
        public static string GetLearnedPreferences(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var prefs = PreferenceLearner.Instance.GetAllPreferences();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalObservations = prefs.TotalObservations,
                    scalePreferences = prefs.ScalePreferences.Select(s => new
                    {
                        viewType = s.ViewType,
                        preferredScale = s.PreferredScale,
                        scaleDescription = GetScaleDescription(s.PreferredScale)
                    }),
                    placementPreferences = prefs.PlacementPreferences
                        .Where(p => p.IsConsistent)
                        .Select(p => new
                        {
                            category = p.Category,
                            subCategory = p.SubCategory,
                            preferredQuadrant = p.QuadrantPreference,
                            isConsistent = p.IsConsistent
                        }),
                    namingPatterns = prefs.NamingPreferences
                        .Where(n => n.DetectedPatterns.Any())
                        .Select(n => new
                        {
                            category = n.Category,
                            context = n.Context,
                            patterns = n.DetectedPatterns
                        })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get preferred scale for a view type
        /// Parameters: viewType (string)
        /// </summary>
        [MCPMethod("getPreferredScale", Category = "Intelligence", Description = "Get preferred scale for a view type")]
        public static string GetPreferredScale(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string viewType = parameters["viewType"]?.ToString();
                if (string.IsNullOrEmpty(viewType))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewType is required" });
                }

                int? preferredScale = PreferenceLearner.Instance.GetPreferredScale(viewType);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewType = viewType,
                    hasPreference = preferredScale.HasValue,
                    preferredScale = preferredScale,
                    scaleDescription = preferredScale.HasValue ? GetScaleDescription(preferredScale.Value) : null
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Export preferences for Claude Memory storage
        /// </summary>
        [MCPMethod("exportPreferencesForMemory", Category = "Intelligence", Description = "Export preferences for Claude Memory storage")]
        public static string ExportPreferencesForMemory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string exportJson = PreferenceLearner.Instance.ExportForMemory();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    export = JObject.Parse(exportJson)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Layout Intelligence Methods

        /// <summary>
        /// Get sheet layout recommendation
        /// Parameters: sheetNumber (string), viewIds (optional array of view IDs to place)
        /// </summary>
        [MCPMethod("getSheetLayoutRecommendation", Category = "Intelligence", Description = "Get sheet layout recommendation")]
        public static string GetSheetLayoutRecommendation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                string sheetNumber = parameters["sheetNumber"]?.ToString();
                if (string.IsNullOrEmpty(sheetNumber))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetNumber is required" });
                }

                // Find the sheet
                var sheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber == sheetNumber);

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Sheet {sheetNumber} not found" });
                }

                // Get views to place (either specified or suggest some)
                List<View> viewsToPlace = new List<View>();

                var viewIds = parameters["viewIds"]?.ToObject<List<long>>();
                if (viewIds != null && viewIds.Any())
                {
                    foreach (var id in viewIds)
                    {
                        var view = doc.GetElement(new ElementId(id)) as View;
                        if (view != null)
                        {
                            viewsToPlace.Add(view);
                        }
                    }
                }
                else
                {
                    // Suggest views based on sheet name/number
                    viewsToPlace = SuggestViewsForSheet(sheet, doc);
                }

                var recommendation = LayoutIntelligence.Instance.GetSheetLayout(sheet, viewsToPlace, doc);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    recommendation = new
                    {
                        sheetNumber = recommendation.SheetNumber,
                        sheetName = recommendation.SheetName,
                        strategy = recommendation.Strategy,
                        confidence = Math.Round(recommendation.Confidence, 2),
                        viewPlacements = recommendation.ViewPlacements.Select(vp => new
                        {
                            viewId = vp.ViewId,
                            viewName = vp.ViewName,
                            viewType = vp.ViewType,
                            centerX = Math.Round(vp.CenterX * 12, 2), // Convert to inches
                            centerY = Math.Round(vp.CenterY * 12, 2),
                            recommendedScale = vp.RecommendedScale,
                            scaleDescription = GetScaleDescription(vp.RecommendedScale),
                            alternativeScale = vp.AlternativeScale,
                            reason = vp.Reason,
                            preferenceNote = vp.PreferenceNote
                        }).ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get recommended scale for a view
        /// Parameters: viewId (long), availableWidth (optional, inches), availableHeight (optional, inches)
        /// </summary>
        [MCPMethod("getRecommendedScale", Category = "Intelligence", Description = "Get recommended scale for a view")]
        public static string GetRecommendedScale(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                long viewId = parameters["viewId"]?.ToObject<long>() ?? -1;
                if (viewId == -1)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Default to ARCH D usable area
                double availableWidth = parameters["availableWidth"]?.ToObject<double>() ?? 32.0; // inches
                double availableHeight = parameters["availableHeight"]?.ToObject<double>() ?? 20.0;

                // Convert to feet
                availableWidth /= 12.0;
                availableHeight /= 12.0;

                var recommendation = LayoutIntelligence.Instance.RecommendScale(view, availableWidth, availableHeight);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    recommendation = new
                    {
                        recommendedScale = recommendation.RecommendedScale,
                        scaleDescription = GetScaleDescription(recommendation.RecommendedScale),
                        alternativeScale = recommendation.AlternativeScale,
                        alternativeDescription = recommendation.AlternativeScale.HasValue
                            ? GetScaleDescription(recommendation.AlternativeScale.Value)
                            : null,
                        reason = recommendation.Reason,
                        confidence = Math.Round(recommendation.Confidence, 2)
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Suggest viewport position for a view on a sheet
        /// Parameters: viewId (long), sheetNumber (string)
        /// </summary>
        [MCPMethod("suggestViewportPosition", Category = "Intelligence", Description = "Suggest viewport position for a view on a sheet")]
        public static string SuggestViewportPosition(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                long viewId = parameters["viewId"]?.ToObject<long>() ?? -1;
                string sheetNumber = parameters["sheetNumber"]?.ToString();

                if (viewId == -1 || string.IsNullOrEmpty(sheetNumber))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId and sheetNumber are required" });
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                var sheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber == sheetNumber);

                if (view == null || sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View or sheet not found" });
                }

                // Get existing viewports
                var existingViewports = sheet.GetAllViewports()
                    .Select(id => doc.GetElement(id) as Viewport)
                    .Where(vp => vp != null)
                    .ToList();

                var suggestion = LayoutIntelligence.Instance.SuggestViewportPosition(view, sheet, existingViewports, doc);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    suggestion = new
                    {
                        viewId = suggestion.ViewId,
                        viewName = suggestion.ViewName,
                        centerX = Math.Round(suggestion.CenterX * 12, 2), // Convert to inches
                        centerY = Math.Round(suggestion.CenterY * 12, 2),
                        recommendedScale = suggestion.RecommendedScale,
                        scaleDescription = GetScaleDescription(suggestion.RecommendedScale),
                        reason = suggestion.Reason
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Learning Report Methods

        /// <summary>
        /// Get comprehensive learning report
        /// </summary>
        [MCPMethod("getLearningReport", Category = "Intelligence", Description = "Get comprehensive learning report")]
        public static string GetLearningReport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string report = ProactiveAssistant.Instance.GetLearningReport();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    report = JObject.Parse(report)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Export all workflow data for Claude Memory
        /// </summary>
        [MCPMethod("exportWorkflowData", Category = "Intelligence", Description = "Export all workflow data for Claude Memory")]
        public static string ExportWorkflowData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string workflowExport = WorkflowAnalyzer.Instance.ExportLearnedPatterns();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    export = JObject.Parse(workflowExport)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Correction Learning Methods

        // Static instance of CorrectionLearner for MCP access
        private static CorrectionLearner _correctionLearner = new CorrectionLearner();

        /// <summary>
        /// Store a correction when Claude makes a mistake
        /// Parameters:
        /// - whatWasAttempted: What was attempted
        /// - whatWentWrong: What went wrong
        /// - correctApproach: The correct way to handle this
        /// - category: (optional) Category like "code", "placement", "workflow"
        /// </summary>
        [MCPMethod("storeCorrection", Category = "Intelligence", Description = "Store a correction when an operation fails")]
        public static string StoreCorrection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string whatWasAttempted = parameters["whatWasAttempted"]?.ToString();
                string whatWentWrong = parameters["whatWentWrong"]?.ToString();
                string correctApproach = parameters["correctApproach"]?.ToString();
                string category = parameters["category"]?.ToString() ?? "general";

                if (string.IsNullOrEmpty(whatWasAttempted) || string.IsNullOrEmpty(whatWentWrong) || string.IsNullOrEmpty(correctApproach))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "whatWasAttempted, whatWentWrong, and correctApproach are required" });
                }

                _correctionLearner.StoreCorrection(whatWasAttempted, whatWentWrong, correctApproach, category);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Correction stored successfully",
                    stats = GetCorrectionStatsInternal()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get relevant corrections for a query
        /// Parameters:
        /// - query: Search query
        /// - limit: (optional) Max results (default 5)
        /// </summary>
        [MCPMethod("getRelevantCorrections", Category = "Intelligence", Description = "Get relevant corrections for a query")]
        public static string GetRelevantCorrections(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string query = parameters["query"]?.ToString();
                int limit = parameters["limit"]?.ToObject<int>() ?? 5;

                if (string.IsNullOrEmpty(query))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "query is required" });
                }

                var corrections = _correctionLearner.GetRelevantCorrections(query, limit);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = corrections.Count,
                    corrections = corrections.Select(c => new
                    {
                        id = c.Id,
                        whatWasAttempted = c.WhatWasAttempted,
                        whatWentWrong = c.WhatWentWrong,
                        correctApproach = c.CorrectApproach,
                        category = c.Category,
                        method = c.Method,
                        timestamp = c.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                        timesApplied = c.TimesApplied
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get corrections for a specific MCP method
        /// Parameters:
        /// - method: The MCP method name
        /// </summary>
        [MCPMethod("getMethodCorrections", Category = "Intelligence", Description = "Get corrections for a specific MCP method")]
        public static string GetMethodCorrections(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string method = parameters["method"]?.ToString();

                if (string.IsNullOrEmpty(method))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "method is required" });
                }

                var corrections = _correctionLearner.GetMethodCorrections(method);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    method = method,
                    count = corrections.Count,
                    corrections = corrections.Select(c => new
                    {
                        id = c.Id,
                        whatWentWrong = c.WhatWentWrong,
                        correctApproach = c.CorrectApproach,
                        timestamp = c.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                        timesApplied = c.TimesApplied
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get correction statistics
        /// </summary>
        [MCPMethod("getCorrectionStats", Category = "Intelligence", Description = "Get correction statistics")]
        public static string GetCorrectionStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stats = GetCorrectionStatsInternal()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get corrections formatted as knowledge context
        /// Returns recent corrections formatted for AI consumption
        /// </summary>
        [MCPMethod("getCorrectionsAsKnowledge", Category = "Intelligence", Description = "Get corrections formatted as knowledge context")]
        public static string GetCorrectionsAsKnowledge(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string knowledge = _correctionLearner.GetCorrectionsAsKnowledge();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    knowledge = knowledge,
                    hasCorrections = !string.IsNullOrEmpty(knowledge)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Mark a correction as applied (increases its priority)
        /// Parameters:
        /// - correctionId: The correction ID
        /// </summary>
        [MCPMethod("markCorrectionApplied", Category = "Intelligence", Description = "Mark a correction as applied")]
        public static string MarkCorrectionApplied(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string correctionId = parameters["correctionId"]?.ToString();

                if (string.IsNullOrEmpty(correctionId))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "correctionId is required" });
                }

                _correctionLearner.MarkCorrectionApplied(correctionId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Correction {correctionId} marked as applied"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a correction
        /// Parameters:
        /// - correctionId: The correction ID to delete
        /// </summary>
        [MCPMethod("deleteCorrection", Category = "Intelligence", Description = "Delete a correction by ID")]
        public static string DeleteCorrection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string correctionId = parameters["correctionId"]?.ToString();

                if (string.IsNullOrEmpty(correctionId))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "correctionId is required" });
                }

                _correctionLearner.DeleteCorrection(correctionId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Correction {correctionId} deleted"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object GetCorrectionStatsInternal()
        {
            var stats = _correctionLearner.GetStats();
            return new
            {
                totalCorrections = stats.TotalCorrections,
                categoriesCount = stats.CategoriesCount,
                mostCommonCategory = stats.MostCommonCategory,
                recentCorrections = stats.RecentCorrections,
                totalTimesApplied = stats.TotalTimesApplied
            };
        }

        #endregion

        #region Result Verification Methods

        // Static instance of ResultVerifier
        private static ResultVerifier _resultVerifier = new ResultVerifier();

        /// <summary>
        /// Verify the result of an MCP operation
        /// Parameters:
        /// - method: The MCP method that was called
        /// - parameters: The parameters that were passed
        /// - result: The result returned
        /// </summary>
        [MCPMethod("verifyOperationResult", Category = "Intelligence", Description = "Verify the result of an MCP operation")]
        public static string VerifyOperationResult(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string method = parameters["method"]?.ToString();
                var methodParams = parameters["parameters"] as JObject;
                var result = parameters["result"] as JObject;

                if (string.IsNullOrEmpty(method) || result == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "method and result are required" });
                }

                // For now, return synchronous verification (async would require different handling)
                var verification = _resultVerifier.VerifyAsync(method, methodParams, result).GetAwaiter().GetResult();

                // If verification failed, store as correction
                if (!verification.Verified && verification.CommandSucceeded)
                {
                    _correctionLearner.StoreFromVerification(verification, method, methodParams);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verification = new
                    {
                        verified = verification.Verified,
                        commandSucceeded = verification.CommandSucceeded,
                        method = verification.Method,
                        message = verification.Message,
                        elementId = verification.ElementId > 0 ? (long?)verification.ElementId : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Workflow Planning Methods

        // Static instance of WorkflowPlanner
        private static WorkflowPlanner _workflowPlanner = new WorkflowPlanner();

        /// <summary>
        /// Analyze a user request and create a workflow plan
        /// Parameters:
        /// - request: The user's natural language request
        /// </summary>
        [MCPMethod("analyzeWorkflowRequest", Category = "Intelligence", Description = "Analyze a user request and create a workflow plan")]
        public static string AnalyzeWorkflowRequest(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string request = parameters["request"]?.ToString();

                if (string.IsNullOrEmpty(request))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "request is required" });
                }

                var plan = _workflowPlanner.AnalyzeRequest(request);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    plan = new
                    {
                        isMultiStep = plan.IsMultiStep,
                        templateMatched = plan.TemplateMatched,
                        steps = plan.Steps.Select((s, i) => new
                        {
                            stepNumber = i + 1,
                            method = s.Method,
                            description = s.Description,
                            parameters = s.Parameters,
                            dependsOn = s.DependsOnPreviousStep
                        }).ToList(),
                        estimatedSteps = plan.Steps.Count,
                        complexity = plan.Steps.Count <= 2 ? "simple" : plan.Steps.Count <= 5 ? "moderate" : "complex"
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available workflow templates
        /// </summary>
        [MCPMethod("getWorkflowTemplates", Category = "Intelligence", Description = "Get available workflow templates")]
        public static string GetWorkflowTemplates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var templates = _workflowPlanner.GetAvailableTemplates();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = templates.Count,
                    templates = templates.Select(t => new
                    {
                        name = t.Key,
                        steps = t.Value
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set project context for workflow planning
        /// Parameters:
        /// - projectName: Name of the project
        /// - projectType: Type (residential, commercial, etc.)
        /// - clientFirm: (optional) Client/firm name for standards matching
        /// </summary>
        [MCPMethod("setProjectContext", Category = "Intelligence", Description = "Set project context for workflow planning")]
        public static string SetProjectContext(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string projectName = parameters["projectName"]?.ToString();
                string projectType = parameters["projectType"]?.ToString();
                string clientFirm = parameters["clientFirm"]?.ToString();

                if (string.IsNullOrEmpty(projectName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "projectName is required" });
                }

                _workflowPlanner.SetProjectContext(projectName, projectType, clientFirm);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Project context set: {projectName}",
                    projectType = projectType ?? "unspecified",
                    clientFirm = clientFirm ?? "unspecified"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get current project context
        /// </summary>
        [MCPMethod("getProjectContext", Category = "Intelligence", Description = "Get current project context")]
        public static string GetProjectContext(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var context = _workflowPlanner.GetProjectContext();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    context = context
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Memory Sync Methods

        /// <summary>
        /// Export all intelligence data for syncing to Claude Memory
        /// This returns everything Claude needs to store in claude-memory MCP
        /// </summary>
        [MCPMethod("exportAllIntelligence", Category = "Intelligence", Description = "Export all intelligence data for syncing to Claude Memory")]
        public static string ExportAllIntelligence(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;

                // Get all intelligence data
                var workflowExport = WorkflowAnalyzer.Instance.ExportLearnedPatterns();
                var prefsExport = PreferenceLearner.Instance.ExportForMemory();
                var correctionsKnowledge = _correctionLearner.GetCorrectionsAsKnowledge();
                var projectContext = _workflowPlanner.GetProjectContext();

                // Update proactive context if we have a doc
                if (doc != null)
                {
                    ProactiveAssistant.Instance.UpdateContext(uiApp);
                }
                var learningReport = ProactiveAssistant.Instance.GetLearningReport();

                // Compile everything into a structured export
                var export = new
                {
                    exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    projectContext = projectContext != null ? new
                    {
                        projectName = projectContext.ProjectName,
                        projectType = projectContext.ProjectType,
                        clientFirm = projectContext.ClientFirm,
                        relevantKnowledge = projectContext.RelevantKnowledge
                    } : null,
                    workflows = JObject.Parse(workflowExport),
                    preferences = JObject.Parse(prefsExport),
                    corrections = correctionsKnowledge,
                    learning = JObject.Parse(learningReport),
                    correctionStats = GetCorrectionStatsInternal()
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    export = export,
                    // Instructions for Claude on how to store this
                    storageInstructions = new
                    {
                        action = "Call mcp__claude-memory__memory_store for each important item",
                        categories = new[]
                        {
                            new { type = "preference", importanceRange = "6-8", data = "preferences" },
                            new { type = "decision", importanceRange = "7-9", data = "workflow patterns" },
                            new { type = "error", importanceRange = "8-10", data = "corrections" },
                            new { type = "context", importanceRange = "5-7", data = "project context" }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get a summary of what should be saved to memory at session end
        /// This is optimized for storing in claude-memory MCP
        /// </summary>
        [MCPMethod("getSessionSummaryForMemory", Category = "Intelligence", Description = "Get a summary of what should be saved to memory at session end")]
        public static string GetSessionSummaryForMemory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var projectContext = _workflowPlanner.GetProjectContext();
                var stats = WorkflowAnalyzer.Instance.GetStatistics();
                var patterns = WorkflowAnalyzer.Instance.GetPatterns(3);
                var corrections = _correctionLearner.GetStats();

                // Create a summary optimized for memory storage
                var summary = new
                {
                    session = new
                    {
                        timestamp = DateTime.Now,
                        project = projectContext?.ProjectName ?? "Unknown",
                        projectType = projectContext?.ProjectType ?? "Unknown",
                        actionsPerformed = stats.TotalActionsRecorded,
                        sessionDuration = stats.SessionDuration.TotalMinutes.ToString("F1") + " minutes"
                    },
                    keyLearnings = new List<object>()
                };

                // Add patterns as learnings
                var learnings = (List<object>)summary.keyLearnings;
                foreach (var pattern in patterns.Where(p => p.Occurrences >= 3))
                {
                    learnings.Add(new
                    {
                        type = "workflow_pattern",
                        description = pattern.Description,
                        occurrences = pattern.Occurrences,
                        importance = 7
                    });
                }

                // Add correction learnings
                if (corrections.RecentCorrections > 0)
                {
                    learnings.Add(new
                    {
                        type = "corrections",
                        count = corrections.RecentCorrections,
                        mostCommonCategory = corrections.MostCommonCategory,
                        importance = 8,
                        note = "Review recent corrections to avoid repeating mistakes"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = summary,
                    suggestedMemoryEntries = GenerateSuggestedMemoryEntries(projectContext, patterns, corrections)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static List<object> GenerateSuggestedMemoryEntries(ProjectContext project, IEnumerable<WorkflowPattern> patterns, CorrectionStats corrections)
        {
            var entries = new List<object>();

            // Workflow patterns
            foreach (var pattern in patterns.Where(p => p.Occurrences >= 5))
            {
                entries.Add(new
                {
                    content = $"Workflow pattern: {pattern.Description}",
                    memoryType = "fact",
                    tags = new[] { "workflow", "pattern", project?.ProjectType ?? "general" },
                    importance = 7,
                    project = project?.ProjectName ?? "RevitMCPBridge"
                });
            }

            // Correction summary
            if (corrections.TotalCorrections > 0)
            {
                entries.Add(new
                {
                    content = $"Session had {corrections.RecentCorrections} corrections. Most common issue: {corrections.MostCommonCategory}",
                    memoryType = "outcome",
                    tags = new[] { "corrections", "learning" },
                    importance = 8,
                    project = project?.ProjectName ?? "RevitMCPBridge"
                });
            }

            return entries;
        }

        /// <summary>
        /// Import preferences from a previous session (from claude-memory)
        /// Parameters: preferencesJson - JSON string of preferences to restore
        /// </summary>
        [MCPMethod("importPreferencesFromMemory", Category = "Intelligence", Description = "Import preferences from a previous session")]
        public static string ImportPreferencesFromMemory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string preferencesJson = parameters["preferencesJson"]?.ToString();

                if (string.IsNullOrEmpty(preferencesJson))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "preferencesJson is required" });
                }

                PreferenceLearner.Instance.ImportFromMemory(preferencesJson);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Preferences imported from memory successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static string GetScaleDescription(int scale)
        {
            switch (scale)
            {
                case 1: return "Full Size";
                case 2: return "6\" = 1'-0\"";
                case 4: return "3\" = 1'-0\"";
                case 8: return "1-1/2\" = 1'-0\"";
                case 12: return "1\" = 1'-0\"";
                case 16: return "3/4\" = 1'-0\"";
                case 24: return "1/2\" = 1'-0\"";
                case 48: return "1/4\" = 1'-0\"";
                case 96: return "1/8\" = 1'-0\"";
                case 192: return "1/16\" = 1'-0\"";
                default: return $"1:{scale}";
            }
        }

        private static List<View> SuggestViewsForSheet(ViewSheet sheet, Document doc)
        {
            var suggestedViews = new List<View>();

            // Analyze sheet number to determine type
            string sheetNumber = sheet.SheetNumber.ToUpper();

            // Get unplaced views
            var placedViewIds = new HashSet<long>();
            foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                foreach (var vpId in s.GetAllViewports())
                {
                    var vp = doc.GetElement(vpId) as Viewport;
                    if (vp != null)
                    {
                        placedViewIds.Add(vp.ViewId.Value);
                    }
                }
            }

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted && !placedViewIds.Contains(v.Id.Value))
                .ToList();

            // Suggest based on sheet type
            if (sheetNumber.StartsWith("A1") || sheetNumber.Contains("PLAN"))
            {
                suggestedViews.AddRange(allViews.Where(v => v.ViewType == ViewType.FloorPlan).Take(2));
            }
            else if (sheetNumber.StartsWith("A2") || sheetNumber.Contains("ELEV"))
            {
                suggestedViews.AddRange(allViews.Where(v => v.ViewType == ViewType.Elevation).Take(4));
            }
            else if (sheetNumber.StartsWith("A3") || sheetNumber.Contains("SECT"))
            {
                suggestedViews.AddRange(allViews.Where(v => v.ViewType == ViewType.Section).Take(3));
            }
            else if (sheetNumber.StartsWith("A4") || sheetNumber.Contains("DET"))
            {
                suggestedViews.AddRange(allViews.Where(v => v.ViewType == ViewType.Detail).Take(6));
            }
            else if (sheetNumber.StartsWith("A5") || sheetNumber.Contains("SCHED"))
            {
                suggestedViews.AddRange(allViews.Where(v => v.ViewType == ViewType.Schedule).Take(4));
            }

            return suggestedViews;
        }

        #endregion

        #region Level 4: Proactive Intelligence Methods

        /// <summary>
        /// Run proactive analysis - detects gaps, generates suggestions
        /// Returns comprehensive report of model state and recommendations
        /// </summary>
        [MCPMethod("runProactiveAnalysis", Category = "Intelligence", Description = "Run proactive analysis and detect model gaps")]
        public static string RunProactiveAnalysis(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var report = ProactiveMonitor.Instance.RunProactiveAnalysis(doc);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    report = new
                    {
                        timestamp = report.Timestamp,
                        projectName = report.ProjectName,
                        summary = report.Summary,
                        snapshot = report.Snapshot != null ? new
                        {
                            walls = report.Snapshot.WallCount,
                            rooms = report.Snapshot.RoomCount,
                            doors = report.Snapshot.DoorCount,
                            windows = report.Snapshot.WindowCount,
                            views = report.Snapshot.ViewCount,
                            sheets = report.Snapshot.SheetCount,
                            warnings = report.Snapshot.WarningCount,
                            unplacedViewCount = report.Snapshot.UnplacedViews.Count,
                            untaggedRoomCount = report.Snapshot.UntaggedRooms.Count,
                            emptySheetCount = report.Snapshot.EmptySheets.Count
                        } : null,
                        suggestions = report.Suggestions.Select(s => new
                        {
                            id = s.Id,
                            type = s.Type.ToString(),
                            priority = s.Priority.ToString(),
                            title = s.Title,
                            description = s.Description,
                            actionMethod = s.ActionMethod,
                            actionParams = s.ActionParams,
                            affectedElementCount = s.AffectedElements.Count
                        }),
                        changes = report.Changes
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get proactive monitoring suggestions based on current model state (Level 4)
        /// </summary>
        [MCPMethod("getMonitorSuggestions", Category = "Intelligence", Description = "Get proactive monitoring suggestions based on current model state")]
        public static string GetMonitorSuggestions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                bool highPriorityOnly = parameters["highPriorityOnly"]?.Value<bool>() ?? false;
                int limit = parameters["limit"]?.Value<int>() ?? 10;

                var suggestions = ProactiveMonitor.Instance.GenerateSuggestions(doc);

                if (highPriorityOnly)
                {
                    suggestions = suggestions.Where(s => s.Priority == SuggestionPriority.High).ToList();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = suggestions.Count,
                    suggestions = suggestions.Take(limit).Select(s => new
                    {
                        id = s.Id,
                        type = s.Type.ToString(),
                        priority = s.Priority.ToString(),
                        title = s.Title,
                        description = s.Description,
                        actionMethod = s.ActionMethod,
                        actionParams = s.ActionParams,
                        affectedElementCount = s.AffectedElements.Count,
                        affectedElements = s.AffectedElements.Take(10)
                    })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Execute a suggestion's action
        /// </summary>
        [MCPMethod("executeSuggestion", Category = "Intelligence", Description = "Execute a suggestion's action")]
        public static string ExecuteSuggestion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string suggestionId = parameters["suggestionId"]?.ToString();
                string actionMethod = parameters["actionMethod"]?.ToString();
                JObject actionParams = parameters["actionParams"] as JObject;

                if (string.IsNullOrEmpty(actionMethod))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "actionMethod is required" });
                }

                // Mark suggestion as shown to avoid repeating
                if (!string.IsNullOrEmpty(suggestionId))
                {
                    ProactiveMonitor.Instance.MarkSuggestionShown(suggestionId);
                }

                // Execute the action via method registry
                var result = MCPServer.ExecuteMethod(uiApp, actionMethod, actionParams ?? new JObject());

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    suggestionId = suggestionId,
                    actionMethod = actionMethod,
                    executionResult = JObject.Parse(result)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Take a model state snapshot for comparison
        /// </summary>
        [MCPMethod("takeModelSnapshot", Category = "Intelligence", Description = "Take a model state snapshot for comparison")]
        public static string TakeModelSnapshot(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var snapshot = ProactiveMonitor.Instance.TakeSnapshot(doc);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    snapshot = new
                    {
                        timestamp = snapshot.Timestamp,
                        projectName = snapshot.ProjectName,
                        elementCounts = new
                        {
                            walls = snapshot.WallCount,
                            rooms = snapshot.RoomCount,
                            doors = snapshot.DoorCount,
                            windows = snapshot.WindowCount,
                            views = snapshot.ViewCount,
                            sheets = snapshot.SheetCount
                        },
                        gaps = new
                        {
                            unplacedViews = snapshot.UnplacedViews.Select(v => new { id = v.Id, name = v.Name, viewType = v.ViewType }),
                            untaggedRooms = snapshot.UntaggedRooms.Select(r => new { id = r.Id, name = r.Name, number = r.Number, level = r.Level }),
                            emptySheets = snapshot.EmptySheets.Select(s => new { id = s.Id, number = s.Number, name = s.Name }),
                            roomsWithoutNumbers = snapshot.RoomsWithoutNumbers.Select(r => new { id = r.Id, name = r.Name, level = r.Level })
                        },
                        warnings = snapshot.WarningCount
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get unplaced views that should be on sheets
        /// </summary>
        [MCPMethod("getUnplacedViews", Category = "Intelligence", Description = "Get unplaced views that should be on sheets")]
        public static string GetUnplacedViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                string viewTypeFilter = parameters["viewType"]?.ToString();

                var snapshot = ProactiveMonitor.Instance.TakeSnapshot(doc);
                var unplacedViews = snapshot.UnplacedViews;

                if (!string.IsNullOrEmpty(viewTypeFilter))
                {
                    unplacedViews = unplacedViews.Where(v => v.ViewType.Equals(viewTypeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = unplacedViews.Count,
                    views = unplacedViews.Select(v => new { id = v.Id, name = v.Name, viewType = v.ViewType }),
                    byType = unplacedViews.GroupBy(v => v.ViewType).Select(g => new { viewType = g.Key, count = g.Count() })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get untagged rooms
        /// </summary>
        [MCPMethod("getUntaggedRooms", Category = "Intelligence", Description = "Get untagged rooms")]
        public static string GetUntaggedRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                string levelFilter = parameters["level"]?.ToString();

                var snapshot = ProactiveMonitor.Instance.TakeSnapshot(doc);
                var untaggedRooms = snapshot.UntaggedRooms;

                if (!string.IsNullOrEmpty(levelFilter))
                {
                    untaggedRooms = untaggedRooms.Where(r => r.Level != null && r.Level.Contains(levelFilter)).ToList();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = untaggedRooms.Count,
                    rooms = untaggedRooms.Select(r => new { id = r.Id, name = r.Name, number = r.Number, level = r.Level }),
                    byLevel = untaggedRooms.Where(r => r.Level != null).GroupBy(r => r.Level).Select(g => new { level = g.Key, count = g.Count() })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get auto-applicable corrections for a method
        /// </summary>
        [MCPMethod("getAutoCorrections", Category = "Intelligence", Description = "Get auto-applicable corrections for a method")]
        public static string GetAutoCorrections(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string method = parameters["method"]?.ToString();
                if (string.IsNullOrEmpty(method))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "method parameter is required" });
                }

                var autoCorrections = ProactiveMonitor.Instance.GetAutoApplicableCorrections(method, parameters);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    method = method,
                    count = autoCorrections.Count,
                    corrections = autoCorrections.Select(c => new
                    {
                        correctionId = c.CorrectionId,
                        originalIssue = c.OriginalIssue,
                        suggestedFix = c.SuggestedFix,
                        confidence = c.Confidence,
                        canAutoApply = c.CanAutoApply
                    })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Dismiss a suggestion so it won't be shown again for a while
        /// </summary>
        [MCPMethod("dismissSuggestion", Category = "Intelligence", Description = "Dismiss a suggestion so it won't be shown again for a while")]
        public static string DismissSuggestion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string suggestionId = parameters["suggestionId"]?.ToString();
                if (string.IsNullOrEmpty(suggestionId))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "suggestionId is required" });
                }

                ProactiveMonitor.Instance.MarkSuggestionShown(suggestionId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Suggestion '{suggestionId}' dismissed"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Level 5: Autonomous Execution Methods

        /// <summary>
        /// Execute a high-level goal autonomously
        /// Parameters: goal (string), context (optional object)
        /// </summary>
        public static async Task<string> ExecuteGoal(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string goal = parameters["goal"]?.ToString();
                if (string.IsNullOrEmpty(goal))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "goal parameter is required" });
                }

                JObject context = parameters["context"] as JObject;

                var result = await AutonomousExecutor.Instance.ExecuteGoal(uiApp, goal, context);

                return JsonConvert.SerializeObject(new
                {
                    success = result.Success,
                    taskId = result.TaskId,
                    status = result.Status,
                    message = result.Message,
                    plan = result.Plan != null ? new
                    {
                        id = result.Plan.Id,
                        goal = result.Plan.Goal,
                        stepCount = result.Plan.Steps.Count,
                        steps = result.Plan.Steps.Select(s => new
                        {
                            step = s.StepNumber,
                            description = s.Description,
                            method = s.Method,
                            required = s.IsRequired
                        })
                    } : null,
                    assessment = result.Assessment != null ? new
                    {
                        meetsGoal = result.Assessment.MeetsGoal,
                        successRate = Math.Round(result.Assessment.SuccessRate * 100, 1),
                        stepsExecuted = result.Assessment.StepsExecuted,
                        stepsSucceeded = result.Assessment.StepsSucceeded,
                        summary = result.Assessment.Summary,
                        recommendations = result.Assessment.Recommendations
                    } : null,
                    duration = result.Duration?.TotalSeconds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Approve a task that requires approval
        /// Parameters: taskId (string)
        /// </summary>
        public static async Task<string> ApproveTask(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string taskId = parameters["taskId"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "taskId is required" });
                }

                var result = await AutonomousExecutor.Instance.ApproveTask(uiApp, taskId);

                return JsonConvert.SerializeObject(new
                {
                    success = result.Success,
                    taskId = result.TaskId,
                    status = result.Status,
                    message = result.Message
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Cancel an active autonomous task
        /// Parameters: taskId (string)
        /// </summary>
        [MCPMethod("cancelTask", Category = "Intelligence", Description = "Cancel an active autonomous task")]
        public static string CancelTask(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string taskId = parameters["taskId"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "taskId is required" });
                }

                var cancelled = AutonomousExecutor.Instance.CancelTask(taskId);

                return JsonConvert.SerializeObject(new
                {
                    success = cancelled,
                    taskId = taskId,
                    message = cancelled ? "Task cancelled" : "Task not found or already completed"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get status of all active autonomous tasks
        /// </summary>
        [MCPMethod("getActiveTasks", Category = "Intelligence", Description = "Get status of all active autonomous tasks")]
        public static string GetActiveTasks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var tasks = AutonomousExecutor.Instance.GetActiveTasks();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = tasks.Count,
                    tasks = tasks.Select(t => new
                    {
                        id = t.Id,
                        goal = t.Goal,
                        status = t.Status,
                        progress = t.TotalSteps > 0 ? $"{t.CurrentStep}/{t.TotalSteps}" : "planning",
                        startTime = t.StartTime,
                        retryCount = t.RetryCount,
                        pendingApproval = t.PendingApprovalReason
                    })
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get result of a completed autonomous task
        /// Parameters: taskId (string)
        /// </summary>
        [MCPMethod("getTaskResult", Category = "Intelligence", Description = "Get result of a completed autonomous task")]
        public static string GetTaskResult(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string taskId = parameters["taskId"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "taskId is required" });
                }

                var result = AutonomousExecutor.Instance.GetTaskResult(taskId);
                if (result == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Task result not found"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        taskId = result.TaskId,
                        goalSuccess = result.Success,
                        status = result.Status,
                        message = result.Message,
                        duration = result.Duration?.TotalSeconds,
                        assessment = result.Assessment != null ? new
                        {
                            meetsGoal = result.Assessment.MeetsGoal,
                            successRate = Math.Round(result.Assessment.SuccessRate * 100, 1),
                            summary = result.Assessment.Summary
                        } : null
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Configure autonomous execution settings
        /// Parameters: maxRetries, maxConcurrentTasks, requireApprovalForDestructive
        /// </summary>
        [MCPMethod("configureAutonomy", Category = "Intelligence", Description = "Configure autonomous execution settings")]
        public static string ConfigureAutonomy(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int? maxRetries = parameters["maxRetries"]?.Value<int>();
                int? maxConcurrent = parameters["maxConcurrentTasks"]?.Value<int>();
                bool? requireApproval = parameters["requireApprovalForDestructive"]?.Value<bool>();

                AutonomousExecutor.Instance.Configure(maxRetries, maxConcurrent, requireApproval);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    configuration = AutonomousExecutor.Instance.GetConfiguration()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get autonomous execution statistics
        /// </summary>
        [MCPMethod("getAutonomyStats", Category = "Intelligence", Description = "Get autonomous execution statistics")]
        public static string GetAutonomyStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    statistics = AutonomousExecutor.Instance.GetStatistics(),
                    configuration = AutonomousExecutor.Instance.GetConfiguration()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get list of supported autonomous goals
        /// </summary>
        [MCPMethod("getSupportedGoals", Category = "Intelligence", Description = "Get list of supported autonomous goals")]
        public static string GetSupportedGoals(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var supportedGoals = new[]
                {
                    new { goal = "create sheet set", description = "Creates sheets for all levels with floor plan views", example = "Create sheet set for this project" },
                    new { goal = "place views on sheets", description = "Places unplaced views on appropriate sheets", example = "Place all views on sheets" },
                    new { goal = "tag all rooms", description = "Tags all untagged rooms in the model", example = "Tag all rooms" },
                    new { goal = "create room schedule", description = "Creates a room schedule with standard fields", example = "Create room schedule" },
                    new { goal = "setup project levels", description = "Creates levels at specified elevations", example = "Setup levels: Level 1, Level 2, Level 3" },
                    new { goal = "create wall layout", description = "Creates walls from coordinate data", example = "Create wall layout from coordinates" }
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = supportedGoals.Length,
                    goals = supportedGoals,
                    note = "Goals are matched by keyword - include the key phrases in your goal description"
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
