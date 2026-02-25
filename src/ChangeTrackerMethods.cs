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
    /// MCP methods for querying the ChangeTracker
    /// </summary>
    public static class ChangeTrackerMethods
    {
        /// <summary>
        /// Get recent changes from the change log
        /// Parameters: count (optional, default 50), changeType (optional filter)
        /// </summary>
        public static string GetRecentChanges(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int count = parameters["count"]?.ToObject<int>() ?? 50;
                string changeTypeFilter = parameters["changeType"]?.ToString();

                List<ChangeRecord> changes;

                if (!string.IsNullOrEmpty(changeTypeFilter) && Enum.TryParse<ChangeType>(changeTypeFilter, out var changeType))
                {
                    changes = ChangeTracker.Instance.GetChangesByType(changeType, count);
                }
                else
                {
                    changes = ChangeTracker.Instance.GetRecentChanges(count);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = changes.Count,
                    changes = changes.Select(c => new
                    {
                        timestamp = c.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        changeType = c.ChangeType.ToString(),
                        description = c.Description,
                        transactionName = c.TransactionName,
                        documentName = c.DocumentName,
                        elementCount = c.ElementCount,
                        details = c.Details
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get changes since a specific time
        /// Parameters: since (ISO datetime string or seconds ago)
        /// </summary>
        public static string GetChangesSince(UIApplication uiApp, JObject parameters)
        {
            try
            {
                DateTime since;

                if (parameters["since"] != null)
                {
                    var sinceParam = parameters["since"].ToString();

                    // Try parsing as datetime
                    if (DateTime.TryParse(sinceParam, out since))
                    {
                        // Parsed successfully
                    }
                    // Try parsing as seconds ago
                    else if (int.TryParse(sinceParam, out int secondsAgo))
                    {
                        since = DateTime.Now.AddSeconds(-secondsAgo);
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Invalid 'since' parameter. Use ISO datetime or seconds ago." });
                    }
                }
                else
                {
                    // Default to last 60 seconds
                    since = DateTime.Now.AddSeconds(-60);
                }

                var changes = ChangeTracker.Instance.GetChangesSince(since);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    since = since.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    count = changes.Count,
                    changes = changes.Select(c => new
                    {
                        timestamp = c.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        changeType = c.ChangeType.ToString(),
                        description = c.Description,
                        transactionName = c.TransactionName,
                        documentName = c.DocumentName,
                        elementCount = c.ElementCount,
                        details = c.Details
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the currently selected elements
        /// </summary>
        public static string GetCurrentSelection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                // Get selection from UIDocument (most accurate)
                var selection = uiApp.ActiveUIDocument.Selection.GetElementIds();

                var selectedElements = new List<object>();
                foreach (var id in selection)
                {
                    var elem = doc.GetElement(id);
                    if (elem != null)
                    {
                        var elemInfo = new Dictionary<string, object>
                        {
                            { "id", id.Value },
                            { "category", elem.Category?.Name ?? "Unknown" },
                            { "name", elem.Name ?? "" },
                            { "familyName", GetFamilyName(elem) },
                            { "typeName", GetTypeName(elem, doc) }
                        };

                        // Add location info if available
                        var location = elem.Location;
                        if (location is LocationPoint locPoint)
                        {
                            elemInfo["location"] = new
                            {
                                type = "point",
                                x = locPoint.Point.X,
                                y = locPoint.Point.Y,
                                z = locPoint.Point.Z
                            };
                        }
                        else if (location is LocationCurve locCurve)
                        {
                            var curve = locCurve.Curve;
                            elemInfo["location"] = new
                            {
                                type = "curve",
                                startX = curve.GetEndPoint(0).X,
                                startY = curve.GetEndPoint(0).Y,
                                startZ = curve.GetEndPoint(0).Z,
                                endX = curve.GetEndPoint(1).X,
                                endY = curve.GetEndPoint(1).Y,
                                endZ = curve.GetEndPoint(1).Z,
                                length = curve.Length
                            };
                        }

                        selectedElements.Add(elemInfo);
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = selectedElements.Count,
                    elements = selectedElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get information about the active view
        /// </summary>
        public static string GetActiveViewInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var activeView = uiApp.ActiveUIDocument?.ActiveView;

                if (doc == null || activeView == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active view" });
                }

                var viewInfo = new Dictionary<string, object>
                {
                    { "id", activeView.Id.Value },
                    { "name", activeView.Name },
                    { "viewType", activeView.ViewType.ToString() },
                    { "isSheet", activeView is ViewSheet },
                    { "scale", activeView.Scale },
                    { "detailLevel", activeView.DetailLevel.ToString() }
                };

                // Add sheet-specific info
                if (activeView is ViewSheet sheet)
                {
                    viewInfo["sheetNumber"] = sheet.SheetNumber;
                    viewInfo["sheetName"] = sheet.Name;

                    // Get viewports on the sheet
                    var viewportIds = sheet.GetAllViewports();
                    var viewports = new List<object>();
                    foreach (var vpId in viewportIds)
                    {
                        var vp = doc.GetElement(vpId) as Viewport;
                        if (vp != null)
                        {
                            var vpView = doc.GetElement(vp.ViewId) as View;
                            viewports.Add(new
                            {
                                viewportId = vp.Id.Value,
                                viewId = vp.ViewId.Value,
                                viewName = vpView?.Name ?? "Unknown",
                                viewType = vpView?.ViewType.ToString() ?? "Unknown"
                            });
                        }
                    }
                    viewInfo["viewports"] = viewports;
                }

                // Add floor plan specific info
                if (activeView is ViewPlan viewPlan)
                {
                    var level = doc.GetElement(viewPlan.GenLevel?.Id ?? ElementId.InvalidElementId) as Level;
                    viewInfo["levelName"] = level?.Name ?? "";
                    viewInfo["levelElevation"] = level?.Elevation ?? 0;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentName = doc.Title,
                    documentPath = doc.PathName,
                    view = viewInfo
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the last time any change occurred
        /// </summary>
        public static string GetLastChangeTime(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var lastChange = ChangeTracker.Instance.GetLastChangeTime();
                var secondsAgo = (DateTime.Now - lastChange).TotalSeconds;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    lastChangeTime = lastChange.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    secondsAgo = Math.Round(secondsAgo, 1),
                    isRecent = secondsAgo < 5 // Changed in last 5 seconds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get change tracker statistics
        /// </summary>
        public static string GetChangeStatistics(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var stats = ChangeTracker.Instance.GetStatistics();
                var (viewName, viewId) = ChangeTracker.Instance.GetCurrentViewInfo();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    currentView = viewName,
                    currentViewId = viewId,
                    statistics = stats,
                    totalChangesLogged = stats.Values.Sum()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Clear the change log
        /// </summary>
        public static string ClearChangeLog(UIApplication uiApp, JObject parameters)
        {
            try
            {
                ChangeTracker.Instance.ClearLog();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Change log cleared"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Subscribe to real-time changes (returns current state and sets up polling endpoint)
        /// </summary>
        public static string WatchChanges(UIApplication uiApp, JObject parameters)
        {
            try
            {
                int pollIntervalSeconds = parameters["pollInterval"]?.ToObject<int>() ?? 2;

                // Get current state
                var doc = uiApp.ActiveUIDocument?.Document;
                var activeView = uiApp.ActiveUIDocument?.ActiveView;
                var selection = uiApp.ActiveUIDocument?.Selection.GetElementIds();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"To watch changes, poll getChangesSince every {pollIntervalSeconds} seconds",
                    currentState = new
                    {
                        documentName = doc?.Title ?? "None",
                        activeView = activeView?.Name ?? "None",
                        activeViewId = activeView?.Id.Value ?? -1,
                        activeViewType = activeView?.ViewType.ToString() ?? "None",
                        isSheet = activeView is ViewSheet,
                        sheetNumber = (activeView as ViewSheet)?.SheetNumber ?? "",
                        selectionCount = selection?.Count ?? 0,
                        lastChangeTime = ChangeTracker.Instance.GetLastChangeTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
                    },
                    instructions = new
                    {
                        step1 = "Store the lastChangeTime",
                        step2 = $"Every {pollIntervalSeconds} seconds, call getChangesSince with that timestamp",
                        step3 = "Process any new changes returned",
                        step4 = "Update your stored timestamp to the latest change"
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #region Helper Methods

        private static string GetFamilyName(Element elem)
        {
            try
            {
                if (elem is FamilyInstance fi)
                {
                    return fi.Symbol?.Family?.Name ?? "";
                }
            }
            catch { }
            return "";
        }

        private static string GetTypeName(Element elem, Document doc)
        {
            try
            {
                var typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var type = doc.GetElement(typeId);
                    return type?.Name ?? "";
                }
            }
            catch { }
            return "";
        }

        #endregion
    }
}
