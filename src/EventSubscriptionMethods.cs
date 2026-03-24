using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge
{
    /// <summary>
    /// Event subscription system that pushes Revit state changes to a JSON file.
    /// Enables real-time awareness of what's happening in Revit:
    /// - Document changes (elements added/modified/deleted)
    /// - View changes (active view switched)
    /// - Selection changes
    /// - Document opened/closed/saved
    ///
    /// State is written to a JSON file that Claude can poll.
    /// Events are also queued in memory for retrieval via MCP methods.
    /// </summary>
    public static class EventSubscriptionMethods
    {
        private static bool _subscribed = false;
        private static UIApplication _uiApp;
        private static readonly ConcurrentQueue<RevitEvent> _eventQueue = new ConcurrentQueue<RevitEvent>();
        private static readonly object _lock = new object();
        private static int _maxQueueSize = 100;

        // State file path — Claude polls this
        private static string _stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins",
#if REVIT2025
            "2025",
#else
            "2026",
#endif
            "revit_live_state.json");

        /// <summary>
        /// Internal event record
        /// </summary>
        public class RevitEvent
        {
            public string Type { get; set; }
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> Data { get; set; }
        }

        /// <summary>
        /// Subscribe to all Revit events. Call once from RevitMCPBridgeApp.OnStartup.
        /// </summary>
        public static void SubscribeAll(UIApplication uiApp)
        {
            if (_subscribed) return;
            _uiApp = uiApp;

            try
            {
                // Document events
                uiApp.Application.DocumentChanged += OnDocumentChanged;
                uiApp.Application.DocumentOpened += OnDocumentOpened;
                uiApp.Application.DocumentClosing += OnDocumentClosing;
                uiApp.Application.DocumentSaved += OnDocumentSaved;
                uiApp.Application.DocumentSavedAs += OnDocumentSavedAs;

                // View events
                uiApp.ViewActivated += OnViewActivated;

                // Selection is polled via Idling since there's no SelectionChanged event
                uiApp.Idling += OnIdling;

                _subscribed = true;
                Log.Information("[EventSubscription] Subscribed to all Revit events. State file: {Path}", _stateFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Failed to subscribe to events");
            }
        }

        /// <summary>
        /// Unsubscribe from all events. Call from OnShutdown.
        /// </summary>
        public static void UnsubscribeAll(UIApplication uiApp)
        {
            if (!_subscribed) return;

            try
            {
                uiApp.Application.DocumentChanged -= OnDocumentChanged;
                uiApp.Application.DocumentOpened -= OnDocumentOpened;
                uiApp.Application.DocumentClosing -= OnDocumentClosing;
                uiApp.Application.DocumentSaved -= OnDocumentSaved;
                uiApp.Application.DocumentSavedAs -= OnDocumentSavedAs;
                uiApp.ViewActivated -= OnViewActivated;
                uiApp.Idling -= OnIdling;

                _subscribed = false;
                Log.Information("[EventSubscription] Unsubscribed from all events");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Failed to unsubscribe from events");
            }
        }

        // ─── Event Handlers ─────────────────────────────────────────────

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                var added = e.GetAddedElementIds();
                var modified = e.GetModifiedElementIds();
                var deleted = e.GetDeletedElementIds();

                if (added.Count == 0 && modified.Count == 0 && deleted.Count == 0)
                    return;

                // Get element summaries for added/modified
                var addedSummary = new List<object>();
                foreach (var id in added.Take(20))
                {
                    var el = doc.GetElement(id);
                    if (el != null)
                        addedSummary.Add(new { id = (int)id.Value, category = el.Category?.Name, type = el.GetType().Name });
                }

                var modifiedSummary = new List<object>();
                foreach (var id in modified.Take(20))
                {
                    var el = doc.GetElement(id);
                    if (el != null)
                        modifiedSummary.Add(new { id = (int)id.Value, category = el.Category?.Name, type = el.GetType().Name });
                }

                var evt = new RevitEvent
                {
                    Type = "DocumentChanged",
                    Timestamp = DateTime.Now,
                    Data = new Dictionary<string, object>
                    {
                        ["document"] = doc.Title,
                        ["addedCount"] = added.Count,
                        ["modifiedCount"] = modified.Count,
                        ["deletedCount"] = deleted.Count,
                        ["addedElements"] = addedSummary,
                        ["modifiedElements"] = modifiedSummary,
                        ["deletedElementIds"] = deleted.Select(id => (int)id.Value).Take(20).ToList(),
                        ["transactionNames"] = e.GetTransactionNames().ToList()
                    }
                };

                EnqueueEvent(evt);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Error in OnDocumentChanged");
            }
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                EnqueueEvent(new RevitEvent
                {
                    Type = "DocumentOpened",
                    Timestamp = DateTime.Now,
                    Data = new Dictionary<string, object>
                    {
                        ["document"] = doc.Title,
                        ["path"] = doc.PathName,
                        ["isWorkshared"] = doc.IsWorkshared
                    }
                });
                WriteStateFile();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Error in OnDocumentOpened");
            }
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                var doc = e.Document;
                EnqueueEvent(new RevitEvent
                {
                    Type = "DocumentClosing",
                    Timestamp = DateTime.Now,
                    Data = new Dictionary<string, object>
                    {
                        ["document"] = doc.Title
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Error in OnDocumentClosing");
            }
        }

        private static void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                EnqueueEvent(new RevitEvent
                {
                    Type = "DocumentSaved",
                    Timestamp = DateTime.Now,
                    Data = new Dictionary<string, object>
                    {
                        ["document"] = doc.Title,
                        ["path"] = doc.PathName
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Error in OnDocumentSaved");
            }
        }

        private static void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs e)
        {
            try
            {
                var doc = e.Document;
                EnqueueEvent(new RevitEvent
                {
                    Type = "DocumentSavedAs",
                    Timestamp = DateTime.Now,
                    Data = new Dictionary<string, object>
                    {
                        ["document"] = doc.Title,
                        ["path"] = doc.PathName
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Error in OnDocumentSavedAs");
            }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                var view = e.CurrentActiveView;
                var prevView = e.PreviousActiveView;

                EnqueueEvent(new RevitEvent
                {
                    Type = "ViewActivated",
                    Timestamp = DateTime.Now,
                    Data = new Dictionary<string, object>
                    {
                        ["viewName"] = view?.Name,
                        ["viewType"] = view?.ViewType.ToString(),
                        ["viewId"] = view != null ? (int)view.Id.Value : -1,
                        ["previousView"] = prevView?.Name,
                        ["document"] = view?.Document?.Title
                    }
                });
                WriteStateFile();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Error in OnViewActivated");
            }
        }

        // Track last selection to detect changes
        private static HashSet<long> _lastSelectionIds = new HashSet<long>();
        private static DateTime _lastStateWrite = DateTime.MinValue;
        private static readonly TimeSpan _stateWriteInterval = TimeSpan.FromSeconds(5);

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                var uiApp = sender as UIApplication;
                if (uiApp?.ActiveUIDocument == null) return;

                // Check selection changes
                var selection = uiApp.ActiveUIDocument.Selection.GetElementIds();
                var currentIds = new HashSet<long>(selection.Select(id => id.Value));

                if (!currentIds.SetEquals(_lastSelectionIds))
                {
                    _lastSelectionIds = currentIds;

                    if (currentIds.Count > 0)
                    {
                        var doc = uiApp.ActiveUIDocument.Document;
                        var selectedSummary = new List<object>();
                        foreach (var id in selection.Take(20))
                        {
                            var el = doc.GetElement(id);
                            if (el != null)
                                selectedSummary.Add(new { id = (int)id.Value, category = el.Category?.Name, name = el.Name });
                        }

                        EnqueueEvent(new RevitEvent
                        {
                            Type = "SelectionChanged",
                            Timestamp = DateTime.Now,
                            Data = new Dictionary<string, object>
                            {
                                ["count"] = currentIds.Count,
                                ["elements"] = selectedSummary
                            }
                        });
                    }
                }

                // Periodic state file update (every 5 seconds max)
                if (DateTime.Now - _lastStateWrite > _stateWriteInterval)
                {
                    WriteStateFile();
                    _lastStateWrite = DateTime.Now;
                }
            }
            catch
            {
                // Idling handler must never throw
            }
        }

        // ─── Queue Management ───────────────────────────────────────────

        private static void EnqueueEvent(RevitEvent evt)
        {
            _eventQueue.Enqueue(evt);

            // Trim queue if too large
            while (_eventQueue.Count > _maxQueueSize)
                _eventQueue.TryDequeue(out _);
        }

        // ─── State File ─────────────────────────────────────────────────

        private static void WriteStateFile()
        {
            try
            {
                var uiApp = _uiApp;
                if (uiApp == null) return;

                var state = new Dictionary<string, object>
                {
                    ["timestamp"] = DateTime.Now.ToString("o"),
                    ["subscribed"] = _subscribed,
                    ["eventQueueSize"] = _eventQueue.Count
                };

                // Active document info
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc?.Document != null)
                {
                    var doc = uidoc.Document;
                    state["document"] = new Dictionary<string, object>
                    {
                        ["title"] = doc.Title,
                        ["path"] = doc.PathName,
                        ["isModified"] = doc.IsModified,
                        ["isWorkshared"] = doc.IsWorkshared
                    };

                    // Active view
                    var view = uidoc.ActiveView;
                    if (view != null)
                    {
                        state["activeView"] = new Dictionary<string, object>
                        {
                            ["name"] = view.Name,
                            ["type"] = view.ViewType.ToString(),
                            ["id"] = (int)view.Id.Value,
                            ["scale"] = view.Scale
                        };
                    }

                    // Current selection
                    var selIds = uidoc.Selection.GetElementIds();
                    state["selection"] = new Dictionary<string, object>
                    {
                        ["count"] = selIds.Count,
                        ["elementIds"] = selIds.Select(id => (int)id.Value).Take(50).ToList()
                    };
                }
                else
                {
                    state["document"] = null;
                }

                // Recent events (last 10)
                state["recentEvents"] = _eventQueue.ToArray()
                    .OrderByDescending(e => e.Timestamp)
                    .Take(10)
                    .Select(e => new { e.Type, timestamp = e.Timestamp.ToString("o"), e.Data })
                    .ToList();

                var dir = Path.GetDirectoryName(_stateFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[EventSubscription] Failed to write state file");
            }
        }

        // ─── MCP Methods ────────────────────────────────────────────────

        [MCPMethod("subscribeEvents", Category = "EventSubscription")]
        public static string SubscribeEvents(UIApplication uiApp, JObject parameters)
        {
            SubscribeAll(uiApp);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                subscribed = _subscribed,
                stateFile = _stateFilePath,
                message = "Event subscriptions active. Poll state file or use getEventQueue."
            });
        }

        [MCPMethod("unsubscribeEvents", Category = "EventSubscription")]
        public static string UnsubscribeEvents(UIApplication uiApp, JObject parameters)
        {
            UnsubscribeAll(uiApp);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                subscribed = _subscribed
            });
        }

        [MCPMethod("getEventQueue", Category = "EventSubscription")]
        public static string GetEventQueue(UIApplication uiApp, JObject parameters)
        {
            var since = parameters?["since"]?.Value<string>();
            DateTime? sinceDate = null;
            if (!string.IsNullOrEmpty(since))
                sinceDate = DateTime.Parse(since);

            var events = _eventQueue.ToArray();
            if (sinceDate.HasValue)
                events = events.Where(e => e.Timestamp > sinceDate.Value).ToArray();

            var clear = parameters?["clear"]?.Value<bool>() ?? false;
            if (clear)
            {
                while (_eventQueue.TryDequeue(out _)) { }
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                count = events.Length,
                events = events.Select(e => new
                {
                    e.Type,
                    timestamp = e.Timestamp.ToString("o"),
                    e.Data
                }).ToList()
            });
        }

        [MCPMethod("getRevitState", Category = "EventSubscription")]
        public static string GetRevitState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        document = (object)null,
                        subscribed = _subscribed
                    });
                }

                var doc = uidoc.Document;
                var view = uidoc.ActiveView;
                var selIds = uidoc.Selection.GetElementIds();

                // Selection details
                var selDetails = new List<object>();
                foreach (var id in selIds.Take(50))
                {
                    var el = doc.GetElement(id);
                    if (el != null)
                    {
                        selDetails.Add(new
                        {
                            id = (int)id.Value,
                            category = el.Category?.Name,
                            name = el.Name,
                            type = el.GetType().Name
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    subscribed = _subscribed,
                    document = new
                    {
                        title = doc.Title,
                        path = doc.PathName,
                        isModified = doc.IsModified,
                        isWorkshared = doc.IsWorkshared
                    },
                    activeView = new
                    {
                        name = view?.Name,
                        type = view?.ViewType.ToString(),
                        id = view != null ? (int)view.Id.Value : -1,
                        scale = view?.Scale ?? 0
                    },
                    selection = new
                    {
                        count = selIds.Count,
                        elements = selDetails
                    },
                    eventQueueSize = _eventQueue.Count,
                    stateFile = _stateFilePath
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        [MCPMethod("setEventConfig", Category = "EventSubscription")]
        public static string SetEventConfig(UIApplication uiApp, JObject parameters)
        {
            if (parameters?["maxQueueSize"] != null)
                _maxQueueSize = parameters["maxQueueSize"].Value<int>();

            if (parameters?["stateFilePath"] != null)
                _stateFilePath = parameters["stateFilePath"].ToString();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                maxQueueSize = _maxQueueSize,
                stateFilePath = _stateFilePath
            });
        }

        [MCPMethod("getEventSubscriptionStatus", Category = "EventSubscription")]
        public static string GetEventSubscriptionStatus(UIApplication uiApp, JObject parameters)
        {
            return JsonConvert.SerializeObject(new
            {
                success = true,
                subscribed = _subscribed,
                eventQueueSize = _eventQueue.Count,
                maxQueueSize = _maxQueueSize,
                stateFilePath = _stateFilePath,
                stateFileExists = File.Exists(_stateFilePath)
            });
        }
    }
}
