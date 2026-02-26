using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Configuration;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// System methods for MCP Bridge - version info, health checks, configuration
    /// </summary>
    public static class SystemMethods
    {
        /// <summary>
        /// Get version information for the MCP Bridge
        /// </summary>
        [MCPMethod("getVersion", Category = "System", Description = "Get version information for the MCP Bridge")]
        public static string GetVersion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var config = BridgeConfiguration.Instance;
                var assembly = Assembly.GetExecutingAssembly();
                var assemblyVersion = assembly.GetName().Version;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        version = config.Version.FullVersion,
                        displayVersion = config.Version.DisplayVersion,
                        major = int.Parse(config.Version.Major),
                        minor = int.Parse(config.Version.Minor),
                        patch = int.Parse(config.Version.Patch),
                        buildDate = config.Version.BuildDate,
                        revitVersion = config.Version.RevitVersion,
                        assemblyVersion = assemblyVersion.ToString(),
                        pipeName = config.GetFullPipeName()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get current configuration values
        /// </summary>
        [MCPMethod("getConfiguration", Category = "System", Description = "Get current configuration values")]
        public static string GetConfiguration(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var config = BridgeConfiguration.Instance;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        pipe = new
                        {
                            name = config.Pipe.Name,
                            timeoutMs = config.Pipe.TimeoutMs,
                            maxConnections = config.Pipe.MaxConnections
                        },
                        logging = new
                        {
                            level = config.Logging.Level,
                            logDirectory = config.GetLogDirectory(),
                            retainedDays = config.Logging.RetainedFileDays
                        },
                        autonomy = new
                        {
                            enabled = config.Autonomy.Enabled,
                            maxRetries = config.Autonomy.MaxRetries,
                            maxElementsPerBatch = config.Autonomy.MaxElementsPerBatch,
                            maxDeletionsPerBatch = config.Autonomy.MaxDeletionsPerBatch
                        },
                        ai = new
                        {
                            learningEnabled = config.AI.EnableLearning,
                            correctionsEnabled = config.AI.StoreCorrections,
                            proactiveAssistance = config.AI.EnableProactiveAssistance
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
        /// Health check endpoint
        /// </summary>
        [MCPMethod("healthCheck", Category = "System", Description = "Health check endpoint")]
        public static string HealthCheck(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                var config = BridgeConfiguration.Instance;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        status = "healthy",
                        timestamp = DateTime.UtcNow.ToString("o"),
                        revitRunning = true,
                        documentOpen = doc != null,
                        documentTitle = doc?.Title,
                        documentPath = doc?.PathName,
                        pipeName = config.Pipe.Name,
                        version = config.Version.FullVersion,
                        uptime = GetUptime()
                    }
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    status = "unhealthy"
                });
            }
        }

        /// <summary>
        /// Get statistics about MCP server usage
        /// </summary>
        [MCPMethod("getStats", Category = "System", Description = "Get statistics about MCP server usage")]
        public static string GetStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        uptime = GetUptime(),
                        totalRequests = MCPServer.TotalRequestCount,
                        successfulRequests = MCPServer.SuccessfulRequestCount,
                        failedRequests = MCPServer.FailedRequestCount,
                        averageResponseTimeMs = MCPServer.AverageResponseTimeMs,
                        peakResponseTimeMs = MCPServer.PeakResponseTimeMs,
                        currentConnections = MCPServer.CurrentConnectionCount,
                        memoryUsageMB = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Reload configuration from disk
        /// </summary>
        [MCPMethod("reloadConfiguration", Category = "System", Description = "Reload configuration from disk")]
        public static string ReloadConfiguration(UIApplication uiApp, JObject parameters)
        {
            try
            {
                BridgeConfiguration.Reload();
                Log.Information("Configuration reloaded via MCP command");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Configuration reloaded successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available MCP methods
        /// </summary>
        [MCPMethod("getMethods", Category = "System", Description = "Get available MCP methods")]
        public static string GetMethods(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var methods = MCPServer.GetRegisteredMethods();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = methods.Count,
                        methods = methods
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string GetUptime()
        {
            var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
            if (uptime.TotalDays >= 1)
                return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalHours >= 1)
                return $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        }

        /// <summary>
        /// Configure automatic dialog handling - when enabled, dialogs are auto-dismissed
        /// Parameters:
        ///   enabled (bool): true to enable auto-handling, false to disable
        ///   defaultResult (int, optional): 1=OK/Yes (default), 2=No, 0=Cancel
        /// </summary>
        [MCPMethod("setDialogAutoHandle", Category = "System", Description = "Configure automatic dialog handling")]
        public static string SetDialogAutoHandle(UIApplication uiApp, JObject parameters)
        {
            try
            {
                bool enabled = parameters["enabled"]?.ToObject<bool>() ?? false;
                int defaultResult = parameters["defaultResult"]?.ToObject<int>() ?? 1;

                // Validate defaultResult
                if (defaultResult < 0 || defaultResult > 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "defaultResult must be 0 (Cancel), 1 (OK/Yes), or 2 (No)"
                    });
                }

                RevitMCPBridgeApp.AutoHandleDialogs = enabled;
                RevitMCPBridgeApp.DefaultDialogResult = defaultResult;

                string resultName = defaultResult == 0 ? "Cancel" : (defaultResult == 1 ? "OK/Yes" : "No");

                Log.Information("Dialog auto-handling set to {Enabled} with default result {Result} ({ResultName})",
                    enabled, defaultResult, resultName);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        autoHandleEnabled = enabled,
                        defaultResult = defaultResult,
                        defaultResultName = resultName,
                        message = enabled
                            ? $"Auto-handling enabled. Dialogs will be dismissed with '{resultName}'"
                            : "Auto-handling disabled. Dialogs will require user interaction"
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get current dialog handling settings
        /// </summary>
        [MCPMethod("getDialogSettings", Category = "System", Description = "Get current dialog handling settings")]
        public static string GetDialogSettings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                bool enabled = RevitMCPBridgeApp.AutoHandleDialogs;
                int defaultResult = RevitMCPBridgeApp.DefaultDialogResult;
                string resultName = defaultResult == 0 ? "Cancel" : (defaultResult == 1 ? "OK/Yes" : "No");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        autoHandleEnabled = enabled,
                        defaultResult = defaultResult,
                        defaultResultName = resultName,
                        historyCount = RevitMCPBridgeApp.GetDialogHistory().Count
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get history of dialogs that have appeared
        /// Parameters:
        ///   limit (int, optional): Maximum number of records to return (default: 50)
        /// </summary>
        [MCPMethod("getDialogHistory", Category = "System", Description = "Get history of dialogs that have appeared")]
        public static string GetDialogHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int limit = parameters["limit"]?.ToObject<int>() ?? 50;
                var history = RevitMCPBridgeApp.GetDialogHistory();

                // Get most recent entries up to limit
                var recentHistory = history.Count > limit
                    ? history.GetRange(history.Count - limit, limit)
                    : history;

                var formattedHistory = new List<object>();
                foreach (var record in recentHistory)
                {
                    formattedHistory.Add(new
                    {
                        timestamp = record.Timestamp.ToString("o"),
                        dialogId = record.DialogId,
                        message = record.Message,
                        buttons = record.Buttons,
                        resultClicked = record.ResultClicked,
                        resultName = record.ResultName
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        totalCount = history.Count,
                        returnedCount = formattedHistory.Count,
                        history = formattedHistory
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Clear the dialog history
        /// </summary>
        [MCPMethod("clearDialogHistory", Category = "System", Description = "Clear the dialog history")]
        public static string ClearDialogHistory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int countBefore = RevitMCPBridgeApp.GetDialogHistory().Count;
                RevitMCPBridgeApp.ClearDialogHistory();

                Log.Information("Dialog history cleared ({Count} records)", countBefore);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        cleared = true,
                        recordsCleared = countBefore
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
