using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// UI Automation Methods - Enable AI to interact with Revit's UI layer.
    /// Provides programmatic access to ribbon commands, dialogs, and user prompts.
    /// </summary>
    public static class UIAutomationMethods
    {
        // Store for dialog results
        private static string _lastDialogResult;
        private static bool _dialogPending;

        #region Execute Revit Command

        /// <summary>
        /// Execute a built-in Revit command by its PostableCommand enum value.
        /// </summary>
        public static string ExecuteRevitCommand(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var commandName = parameters["command"]?.ToString();
                if (string.IsNullOrEmpty(commandName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "command parameter is required" });
                }

                // Try to parse as PostableCommand enum
                if (!Enum.TryParse<PostableCommand>(commandName, true, out var postableCommand))
                {
                    // Check for common aliases
                    postableCommand = GetCommandFromAlias(commandName);
                    if (postableCommand == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown command: {commandName}",
                            hint = "Use listRevitCommands to see available commands"
                        });
                    }
                }

                // Get the RevitCommandId
                var commandId = RevitCommandId.LookupPostableCommandId(postableCommand);
                if (commandId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Command not available: {commandName}" });
                }

                // Check if command can be executed
                if (!uiApp.CanPostCommand(commandId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Command cannot be executed in current context: {commandName}",
                        suggestion = "Command may require specific view type or selection"
                    });
                }

                // Post the command
                uiApp.PostCommand(commandId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    command = commandName,
                    message = $"Command '{commandName}' queued for execution"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing Revit command");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region List Revit Commands

        /// <summary>
        /// List all available PostableCommand values.
        /// </summary>
        public static string ListRevitCommands(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var category = parameters["category"]?.ToString()?.ToLower() ?? "all";
                var searchTerm = parameters["search"]?.ToString()?.ToLower();

                var commands = Enum.GetValues(typeof(PostableCommand))
                    .Cast<PostableCommand>()
                    .Select(cmd => new
                    {
                        name = cmd.ToString(),
                        value = (int)cmd,
                        category = CategorizeCommand(cmd.ToString())
                    })
                    .Where(cmd =>
                        (category == "all" || cmd.category.ToLower() == category) &&
                        (string.IsNullOrEmpty(searchTerm) || cmd.name.ToLower().Contains(searchTerm)))
                    .OrderBy(cmd => cmd.category)
                    .ThenBy(cmd => cmd.name)
                    .ToList();

                var categories = commands.Select(c => c.category).Distinct().OrderBy(c => c).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = commands.Count,
                    categories = categories,
                    commands = commands
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error listing Revit commands");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Show Task Dialog

        /// <summary>
        /// Show a TaskDialog to the user with customizable buttons and content.
        /// </summary>
        public static string ShowTaskDialog(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var title = parameters["title"]?.ToString() ?? "Message";
                var mainInstruction = parameters["mainInstruction"]?.ToString() ?? "";
                var mainContent = parameters["mainContent"]?.ToString() ?? "";
                var footerText = parameters["footer"]?.ToString();
                var expandedContent = parameters["expandedContent"]?.ToString();
                var verificationText = parameters["verificationText"]?.ToString();
                var allowCancel = parameters["allowCancel"]?.Value<bool>() ?? false;

                // Button configuration
                var buttonsParam = parameters["buttons"]?.ToString()?.ToLower() ?? "ok";
                var commandLinks = parameters["commandLinks"] as JArray;

                var dialog = new TaskDialog(title)
                {
                    MainInstruction = mainInstruction,
                    MainContent = mainContent,
                    AllowCancellation = allowCancel
                };

                if (!string.IsNullOrEmpty(footerText))
                    dialog.FooterText = footerText;

                if (!string.IsNullOrEmpty(expandedContent))
                    dialog.ExpandedContent = expandedContent;

                if (!string.IsNullOrEmpty(verificationText))
                    dialog.VerificationText = verificationText;

                // Set buttons
                switch (buttonsParam)
                {
                    case "okcancel":
                        dialog.CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel;
                        break;
                    case "yesno":
                        dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        break;
                    case "yesnocancel":
                        dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel;
                        break;
                    case "retrycancel":
                        dialog.CommonButtons = TaskDialogCommonButtons.Retry | TaskDialogCommonButtons.Cancel;
                        break;
                    case "close":
                        dialog.CommonButtons = TaskDialogCommonButtons.Close;
                        break;
                    default:
                        dialog.CommonButtons = TaskDialogCommonButtons.Ok;
                        break;
                }

                // Add command links if provided
                if (commandLinks != null && commandLinks.Count > 0)
                {
                    foreach (var link in commandLinks)
                    {
                        var linkText = link["text"]?.ToString();
                        if (!string.IsNullOrEmpty(linkText))
                        {
                            var linkId = link["id"]?.Value<int>() ?? (1000 + commandLinks.IndexOf(link));
                            dialog.AddCommandLink((TaskDialogCommandLinkId)linkId, linkText);
                        }
                    }
                }

                // Show dialog and get result
                var result = dialog.Show();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = result.ToString(),
                    wasVerificationChecked = dialog.WasVerificationChecked()
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing task dialog");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Show Input Dialog

        /// <summary>
        /// Show a simple input dialog to get text from user.
        /// Uses TaskDialog with verification text as a workaround since Revit lacks native input dialogs.
        /// </summary>
        public static string ShowInputDialog(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var title = parameters["title"]?.ToString() ?? "Input Required";
                var prompt = parameters["prompt"]?.ToString() ?? "Enter value:";
                var defaultValue = parameters["defaultValue"]?.ToString() ?? "";

                // Since Revit doesn't have native text input dialogs,
                // we'll use a simple approach with TaskDialog
                var dialog = new TaskDialog(title)
                {
                    MainInstruction = prompt,
                    MainContent = $"Default: {defaultValue}\n\nNote: This is a confirmation dialog. " +
                                  "For text input, use the Revit API's PromptForFamilyInstancePlacement or similar.",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                    AllowCancellation = true
                };

                var result = dialog.Show();

                return JsonConvert.SerializeObject(new
                {
                    success = result == TaskDialogResult.Ok,
                    confirmed = result == TaskDialogResult.Ok,
                    result = result.ToString(),
                    value = result == TaskDialogResult.Ok ? defaultValue : null,
                    note = "For complex input, consider using Windows Forms or WPF dialogs"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing input dialog");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Active View Info

        /// <summary>
        /// Get detailed information about the currently active view.
        /// </summary>
        public static string GetActiveViewInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var view = uidoc.ActiveView;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active view" });
                }

                var doc = uidoc.Document;

                // Get view-specific info
                object viewSpecificInfo = null;
                if (view is View3D view3D)
                {
                    viewSpecificInfo = new
                    {
                        isPerspective = view3D.IsPerspective,
                        isSectionBoxActive = view3D.IsSectionBoxActive,
                        orientation = view3D.GetOrientation() != null ? new
                        {
                            eyePosition = new { x = view3D.GetOrientation().EyePosition.X, y = view3D.GetOrientation().EyePosition.Y, z = view3D.GetOrientation().EyePosition.Z },
                            upDirection = new { x = view3D.GetOrientation().UpDirection.X, y = view3D.GetOrientation().UpDirection.Y, z = view3D.GetOrientation().UpDirection.Z },
                            forwardDirection = new { x = view3D.GetOrientation().ForwardDirection.X, y = view3D.GetOrientation().ForwardDirection.Y, z = view3D.GetOrientation().ForwardDirection.Z }
                        } : null
                    };
                }
                else if (view is ViewPlan viewPlan)
                {
                    var level = doc.GetElement(viewPlan.GenLevel.Id) as Level;
                    viewSpecificInfo = new
                    {
                        associatedLevel = level?.Name,
                        levelElevation = level?.Elevation,
                        viewRange = GetViewRangeInfo(viewPlan)
                    };
                }
                else if (view is ViewSection viewSection)
                {
                    viewSpecificInfo = new
                    {
                        direction = viewSection.ViewDirection.ToString()
                    };
                }
                else if (view is ViewSheet viewSheet)
                {
                    viewSpecificInfo = new
                    {
                        sheetNumber = viewSheet.SheetNumber,
                        sheetName = viewSheet.Name,
                        viewportsOnSheet = new FilteredElementCollector(doc, viewSheet.Id)
                            .OfClass(typeof(Viewport))
                            .Cast<Viewport>()
                            .Select(vp => (int)vp.ViewId.Value)
                            .ToList()
                    };
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    view = new
                    {
                        id = (int)view.Id.Value,
                        name = view.Name,
                        viewType = view.ViewType.ToString(),
                        scale = view.Scale,
                        detailLevel = view.DetailLevel.ToString(),
                        displayStyle = view.DisplayStyle.ToString(),
                        isTemplate = view.IsTemplate,
                        canBePrinted = view.CanBePrinted,
                        viewSpecificInfo = viewSpecificInfo
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting active view info");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Activate View

        /// <summary>
        /// Activate/switch to a specific view by ID or name.
        /// </summary>
        public static string ActivateView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                View targetView = null;

                // Find view by ID or name
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(parameters["viewId"].Value<int>());
                    targetView = doc.GetElement(viewId) as View;
                }
                else if (parameters["viewName"] != null)
                {
                    var viewName = parameters["viewName"].ToString();
                    targetView = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .FirstOrDefault(v => v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId or viewName required" });
                }

                if (targetView == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                if (!targetView.CanBePrinted)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot activate this view type (template or system view)"
                    });
                }

                // Activate the view
                uidoc.ActiveView = targetView;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    activatedView = new
                    {
                        id = (int)targetView.Id.Value,
                        name = targetView.Name,
                        viewType = targetView.ViewType.ToString()
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error activating view");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Open Documents

        /// <summary>
        /// Get list of all open documents in the Revit session.
        /// </summary>
        public static string GetOpenDocuments(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var documents = new List<object>();

                foreach (Document doc in uiApp.Application.Documents)
                {
                    var basicInfo = doc.ProjectInformation;
                    documents.Add(new
                    {
                        title = doc.Title,
                        pathName = doc.PathName,
                        isModified = doc.IsModified,
                        isWorkshared = doc.IsWorkshared,
                        isFamilyDocument = doc.IsFamilyDocument,
                        isActive = doc == uiApp.ActiveUIDocument?.Document,
                        projectInfo = basicInfo != null ? new
                        {
                            projectName = basicInfo.Name,
                            projectNumber = basicInfo.Number,
                            clientName = basicInfo.ClientName,
                            projectAddress = basicInfo.Address
                        } : null
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = documents.Count,
                    documents = documents
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting open documents");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Refresh Active View

        /// <summary>
        /// Force refresh of the active view.
        /// </summary>
        public static string RefreshActiveView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                uidoc.RefreshActiveView();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Active view refreshed",
                    viewName = uidoc.ActiveView?.Name
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error refreshing view");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get UI State

        /// <summary>
        /// Get current UI state including active ribbon tab, open views, etc.
        /// </summary>
        public static string GetUIState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                var doc = uidoc?.Document;

                // Get open views
                var openViews = new List<object>();
                if (uidoc != null)
                {
                    foreach (var viewId in uidoc.GetOpenUIViews().Select(uv => uv.ViewId))
                    {
                        var view = doc.GetElement(viewId) as View;
                        if (view != null)
                        {
                            openViews.Add(new
                            {
                                id = (int)view.Id.Value,
                                name = view.Name,
                                viewType = view.ViewType.ToString(),
                                isActive = view.Id == uidoc.ActiveView?.Id
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasActiveDocument = uidoc != null,
                    activeDocumentTitle = doc?.Title,
                    activeViewId = uidoc?.ActiveView != null ? (int?)uidoc.ActiveView.Id.Value : null,
                    activeViewName = uidoc?.ActiveView?.Name,
                    openViewCount = openViews.Count,
                    openViews = openViews,
                    revitVersion = uiApp.Application.VersionName,
                    revitBuild = uiApp.Application.VersionBuild
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting UI state");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Close View

        /// <summary>
        /// Close a specific open view.
        /// </summary>
        public static string CloseView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var viewId = parameters["viewId"]?.Value<int>();
                if (viewId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId required" });
                }

                var targetViewId = new ElementId(viewId.Value);
                var openUIViews = uidoc.GetOpenUIViews();
                var uiView = openUIViews.FirstOrDefault(uv => uv.ViewId == targetViewId);

                if (uiView == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View is not open in UI" });
                }

                // Can't close last view
                if (openUIViews.Count <= 1)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot close the last open view"
                    });
                }

                uiView.Close();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"View closed",
                    closedViewId = viewId
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error closing view");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set View Zoom

        /// <summary>
        /// Set zoom level or zoom to fit for active view.
        /// </summary>
        public static string SetViewZoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var zoomToFit = parameters["zoomToFit"]?.Value<bool>() ?? false;
                var zoomPercent = parameters["zoomPercent"]?.Value<double>();

                var openUIViews = uidoc.GetOpenUIViews();
                var activeUIView = openUIViews.FirstOrDefault(uv => uv.ViewId == uidoc.ActiveView.Id);

                if (activeUIView == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Could not find active UI view" });
                }

                if (zoomToFit)
                {
                    activeUIView.ZoomToFit();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        action = "zoomToFit",
                        message = "View zoomed to fit"
                    });
                }
                else if (zoomPercent.HasValue)
                {
                    // Get current zoom corners (IList<XYZ> with 2 elements)
                    var corners = activeUIView.GetZoomCorners();
                    var center = (corners[0] + corners[1]) / 2;
                    var currentSize = corners[1] - corners[0];

                    // Calculate new size based on percentage (100% = current, 50% = zoom in 2x)
                    var scaleFactor = 100.0 / zoomPercent.Value;
                    var newHalfSize = currentSize * scaleFactor / 2;

                    var newCorner1 = center - newHalfSize;
                    var newCorner2 = center + newHalfSize;

                    activeUIView.ZoomAndCenterRectangle(newCorner1, newCorner2);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        action = "zoomPercent",
                        zoomPercent = zoomPercent,
                        message = $"View zoomed to {zoomPercent}%"
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Specify zoomToFit=true or zoomPercent"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting view zoom");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static PostableCommand GetCommandFromAlias(string alias)
        {
            // Common aliases for PostableCommands (Revit 2026 verified)
            var aliases = new Dictionary<string, PostableCommand>(StringComparer.OrdinalIgnoreCase)
            {
                // Document
                { "save", PostableCommand.Save },
                { "print", PostableCommand.Print },

                // Edit
                { "undo", PostableCommand.Undo },
                { "redo", PostableCommand.Redo },
                { "copy", PostableCommand.Copy },
                { "delete", PostableCommand.Delete },
                { "move", PostableCommand.Move },
                { "rotate", PostableCommand.Rotate },
                { "array", PostableCommand.Array },
                { "align", PostableCommand.Align },
                { "offset", PostableCommand.Offset },
                { "trim", PostableCommand.TrimOrExtendToCorner },
                { "split", PostableCommand.SplitElement },

                // View
                { "3dview", PostableCommand.Default3DView },

                // Architecture
                { "wall", PostableCommand.Wall },
                { "door", PostableCommand.Door },
                { "window", PostableCommand.Window },
                { "roof", PostableCommand.RoofByFootprint },
                { "railing", PostableCommand.Railing },
                { "room", PostableCommand.Room },

                // Annotate
                { "dimension", PostableCommand.AlignedDimension },
                { "tag", PostableCommand.TagByCategory }
            };

            aliases.TryGetValue(alias, out var command);
            return command;
        }

        private static string CategorizeCommand(string commandName)
        {
            var name = commandName.ToLower();

            if (name.Contains("wall") || name.Contains("door") || name.Contains("window") ||
                name.Contains("floor") || name.Contains("roof") || name.Contains("ceiling") ||
                name.Contains("stair") || name.Contains("ramp") || name.Contains("railing") ||
                name.Contains("column") || name.Contains("beam") || name.Contains("room"))
                return "Architecture";

            if (name.Contains("duct") || name.Contains("pipe") || name.Contains("cable") ||
                name.Contains("conduit") || name.Contains("hvac") || name.Contains("plumbing") ||
                name.Contains("electrical") || name.Contains("mep"))
                return "MEP";

            if (name.Contains("structural") || name.Contains("foundation") || name.Contains("framing") ||
                name.Contains("brace") || name.Contains("truss"))
                return "Structural";

            if (name.Contains("view") || name.Contains("section") || name.Contains("elevation") ||
                name.Contains("plan") || name.Contains("3d") || name.Contains("camera") ||
                name.Contains("zoom") || name.Contains("sheet"))
                return "View";

            if (name.Contains("dimension") || name.Contains("text") || name.Contains("tag") ||
                name.Contains("keynote") || name.Contains("annotate") || name.Contains("legend") ||
                name.Contains("symbol"))
                return "Annotate";

            if (name.Contains("family") || name.Contains("load") || name.Contains("type"))
                return "Families";

            if (name.Contains("schedule") || name.Contains("material") || name.Contains("quantity"))
                return "Schedules";

            if (name.Contains("export") || name.Contains("import") || name.Contains("print") ||
                name.Contains("cad") || name.Contains("ifc") || name.Contains("dwg"))
                return "Export/Import";

            if (name.Contains("edit") || name.Contains("modify") || name.Contains("move") ||
                name.Contains("copy") || name.Contains("rotate") || name.Contains("mirror") ||
                name.Contains("array") || name.Contains("delete") || name.Contains("undo") ||
                name.Contains("redo"))
                return "Modify";

            if (name.Contains("file") || name.Contains("save") || name.Contains("open") ||
                name.Contains("close") || name.Contains("new") || name.Contains("document"))
                return "File";

            if (name.Contains("collaboration") || name.Contains("workset") || name.Contains("sync") ||
                name.Contains("reload") || name.Contains("central"))
                return "Collaboration";

            return "Other";
        }

        private static object GetViewRangeInfo(ViewPlan viewPlan)
        {
            try
            {
                var viewRange = viewPlan.GetViewRange();
                var doc = viewPlan.Document;

                double GetLevelOffset(PlanViewPlane plane)
                {
                    var levelId = viewRange.GetLevelId(plane);
                    var offset = viewRange.GetOffset(plane);
                    return offset;
                }

                return new
                {
                    topClipPlane = GetLevelOffset(PlanViewPlane.TopClipPlane),
                    cutPlane = GetLevelOffset(PlanViewPlane.CutPlane),
                    bottomClipPlane = GetLevelOffset(PlanViewPlane.BottomClipPlane),
                    viewDepth = GetLevelOffset(PlanViewPlane.ViewDepthPlane)
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Get Selection Info

        /// <summary>
        /// Get detailed information about currently selected elements.
        /// </summary>
        public static string GetSelectionInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var selection = uidoc.Selection;
                var selectedIds = selection.GetElementIds();

                var elements = new List<object>();
                foreach (var id in selectedIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem != null)
                    {
                        elements.Add(new
                        {
                            id = (int)id.Value,
                            name = elem.Name,
                            category = elem.Category?.Name,
                            typeName = doc.GetElement(elem.GetTypeId())?.Name,
                            levelId = (elem.LevelId != null && elem.LevelId != ElementId.InvalidElementId)
                                ? (int?)elem.LevelId.Value : null
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = elements.Count,
                    elements = elements
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting selection info");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Send Key Sequence

        /// <summary>
        /// Send keyboard shortcuts to Revit (e.g., Escape, Ctrl+Z, Tab).
        /// Uses Windows SendKeys API.
        /// </summary>
        public static string SendKeySequence(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var keys = parameters["keys"]?.ToString();
                if (string.IsNullOrEmpty(keys))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "keys parameter is required" });
                }

                // Convert friendly key names to SendKeys format
                var sendKeysFormat = ConvertToSendKeysFormat(keys);

                // Use Windows Forms SendKeys
                System.Windows.Forms.SendKeys.SendWait(sendKeysFormat);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sentKeys = keys,
                    sendKeysFormat = sendKeysFormat
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error sending key sequence");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string ConvertToSendKeysFormat(string keys)
        {
            // Convert friendly names to SendKeys format
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Escape", "{ESC}" },
                { "Esc", "{ESC}" },
                { "Enter", "{ENTER}" },
                { "Return", "{ENTER}" },
                { "Tab", "{TAB}" },
                { "Backspace", "{BACKSPACE}" },
                { "Delete", "{DELETE}" },
                { "Del", "{DELETE}" },
                { "Home", "{HOME}" },
                { "End", "{END}" },
                { "PageUp", "{PGUP}" },
                { "PageDown", "{PGDN}" },
                { "Up", "{UP}" },
                { "Down", "{DOWN}" },
                { "Left", "{LEFT}" },
                { "Right", "{RIGHT}" },
                { "F1", "{F1}" },
                { "F2", "{F2}" },
                { "F3", "{F3}" },
                { "F4", "{F4}" },
                { "F5", "{F5}" },
                { "F6", "{F6}" },
                { "F7", "{F7}" },
                { "F8", "{F8}" },
                { "F9", "{F9}" },
                { "F10", "{F10}" },
                { "F11", "{F11}" },
                { "F12", "{F12}" },
                { "Space", " " },
                // Modifier combinations
                { "Ctrl+Z", "^z" },
                { "Ctrl+Y", "^y" },
                { "Ctrl+S", "^s" },
                { "Ctrl+C", "^c" },
                { "Ctrl+V", "^v" },
                { "Ctrl+X", "^x" },
                { "Ctrl+A", "^a" },
                { "Ctrl+Shift+S", "^+s" },
                { "Alt+F4", "%{F4}" }
            };

            if (mapping.TryGetValue(keys, out var mapped))
            {
                return mapped;
            }

            // Already in SendKeys format or plain text
            return keys;
        }

        #endregion

        #region Get Ribbon State

        /// <summary>
        /// Get current ribbon state including all tabs.
        /// Note: Revit API has limited access to ribbon internals,
        /// this returns what's available via the API.
        /// </summary>
        public static string GetRibbonState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var panels = new List<object>();

                // Known Revit ribbon tab names
                var knownTabs = new[]
                {
                    "Architecture", "Structure", "Steel", "Precast",
                    "Systems", "Insert", "Annotate", "Analyze",
                    "Massing & Site", "Collaborate", "View", "Manage",
                    "Add-Ins", "Modify"
                };

                foreach (var tabName in knownTabs)
                {
                    try
                    {
                        var tabPanels = uiApp.GetRibbonPanels(tabName);
                        if (tabPanels != null && tabPanels.Count > 0)
                        {
                            var panelInfos = new List<object>();
                            foreach (var panel in tabPanels)
                            {
                                var items = new List<object>();
                                foreach (var item in panel.GetItems())
                                {
                                    items.Add(new
                                    {
                                        name = item.Name,
                                        text = item.ItemText,
                                        visible = item.Visible,
                                        enabled = item.Enabled,
                                        itemType = item.ItemType.ToString()
                                    });
                                }

                                panelInfos.Add(new
                                {
                                    name = panel.Name,
                                    title = panel.Title,
                                    visible = panel.Visible,
                                    enabled = panel.Enabled,
                                    items = items
                                });
                            }

                            panels.Add(new
                            {
                                tabName = tabName,
                                panels = panelInfos
                            });
                        }
                    }
                    catch
                    {
                        // Tab doesn't exist or no access
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tabCount = panels.Count,
                    tabs = panels,
                    note = "This shows add-in ribbon panels. Built-in Revit panels require UI Automation."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting ribbon state");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Check Command Availability

        /// <summary>
        /// Check if a specific command can be executed in the current context.
        /// </summary>
        public static string CanExecuteCommand(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var commandName = parameters["command"]?.ToString();
                if (string.IsNullOrEmpty(commandName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "command parameter is required" });
                }

                // Try to parse as PostableCommand
                PostableCommand postableCommand;
                if (!Enum.TryParse(commandName, true, out postableCommand))
                {
                    postableCommand = GetCommandFromAlias(commandName);
                    if (postableCommand == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            command = commandName,
                            canExecute = false,
                            reason = "Unknown command"
                        });
                    }
                }

                var commandId = RevitCommandId.LookupPostableCommandId(postableCommand);
                if (commandId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        command = commandName,
                        canExecute = false,
                        reason = "Command not available in this Revit version"
                    });
                }

                var canExecute = uiApp.CanPostCommand(commandId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    command = commandName,
                    postableCommand = postableCommand.ToString(),
                    canExecute = canExecute,
                    reason = canExecute ? "Command is available" : "Command cannot execute in current context"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error checking command availability");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Clear Selection

        /// <summary>
        /// Clear the current selection.
        /// </summary>
        public static string ClearSelection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var previousCount = uidoc.Selection.GetElementIds().Count;
                uidoc.Selection.SetElementIds(new List<ElementId>());

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    clearedCount = previousCount,
                    message = $"Cleared {previousCount} selected elements"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error clearing selection");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Selection

        /// <summary>
        /// Set selection to specific elements by ID.
        /// </summary>
        public static string SetSelection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var elementIds = parameters["elementIds"] as JArray;
                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds array is required" });
                }

                var ids = new List<ElementId>();
                foreach (var id in elementIds)
                {
                    ids.Add(new ElementId(id.Value<int>()));
                }

                uidoc.Selection.SetElementIds(ids);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    selectedCount = ids.Count,
                    message = $"Selected {ids.Count} elements"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting selection");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Properties Palette State

        /// <summary>
        /// Get information about the Properties palette.
        /// Note: Full Properties palette access requires UI Automation.
        /// This method returns selection-based property information.
        /// </summary>
        public static string GetPropertiesPaletteState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;
                var selectedIds = uidoc.Selection.GetElementIds();

                if (selectedIds.Count == 0)
                {
                    // No selection - return view properties
                    var view = uidoc.ActiveView;
                    var viewParams = new Dictionary<string, object>();

                    foreach (Parameter param in view.Parameters)
                    {
                        if (param.HasValue)
                        {
                            var value = GetParameterValueAsString(param);
                            if (!string.IsNullOrEmpty(value))
                            {
                                viewParams[param.Definition.Name] = value;
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        context = "view",
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        properties = viewParams
                    });
                }
                else if (selectedIds.Count == 1)
                {
                    // Single selection - return element properties
                    var elem = doc.GetElement(selectedIds.First());
                    var elemParams = new Dictionary<string, object>();

                    foreach (Parameter param in elem.Parameters)
                    {
                        if (param.HasValue)
                        {
                            var value = GetParameterValueAsString(param);
                            if (!string.IsNullOrEmpty(value))
                            {
                                elemParams[param.Definition.Name] = value;
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        context = "element",
                        elementId = (int)elem.Id.Value,
                        elementName = elem.Name,
                        category = elem.Category?.Name,
                        properties = elemParams
                    });
                }
                else
                {
                    // Multiple selection - return summary
                    var categories = selectedIds
                        .Select(id => doc.GetElement(id)?.Category?.Name)
                        .Where(c => c != null)
                        .Distinct()
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        context = "multiple",
                        count = selectedIds.Count,
                        categories = categories,
                        note = "Select single element for detailed properties"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting properties palette state");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string GetParameterValueAsString(Parameter param)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.AsString();
                    case StorageType.Integer:
                        return param.AsInteger().ToString();
                    case StorageType.Double:
                        return param.AsDouble().ToString("F4");
                    case StorageType.ElementId:
                        return param.AsElementId().Value.ToString();
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Get Project Browser State

        /// <summary>
        /// Get the Project Browser tree structure.
        /// Note: This returns the view organization, not the full tree.
        /// Full tree access requires UI Automation.
        /// </summary>
        public static string GetProjectBrowserState(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var doc = uidoc.Document;

                // Get view types and their views
                var viewsByType = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.CanBePrinted)
                    .GroupBy(v => v.ViewType)
                    .Select(g => new
                    {
                        viewType = g.Key.ToString(),
                        count = g.Count(),
                        views = g.Select(v => new
                        {
                            id = (int)v.Id.Value,
                            name = v.Name,
                            isActive = v.Id == uidoc.ActiveView?.Id
                        }).ToList()
                    })
                    .ToList();

                // Get sheets
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Select(s => new
                    {
                        id = (int)s.Id.Value,
                        number = s.SheetNumber,
                        name = s.Name
                    })
                    .OrderBy(s => s.number)
                    .ToList();

                // Get families count
                var familyCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .GetElementCount();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    activeView = uidoc.ActiveView?.Name,
                    viewsByType = viewsByType,
                    sheets = new
                    {
                        count = sheets.Count,
                        items = sheets.Take(50) // Limit for performance
                    },
                    familyCount = familyCount,
                    note = "For full Project Browser tree navigation, use UI Automation"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting project browser state");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Zoom To Selected

        /// <summary>
        /// Zoom the view to fit selected elements.
        /// </summary>
        public static string ZoomToSelected(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var selectedIds = uidoc.Selection.GetElementIds();
                if (selectedIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No elements selected" });
                }

                // Get bounding box of selection
                var doc = uidoc.Document;
                BoundingBoxXYZ combinedBox = null;

                foreach (var id in selectedIds)
                {
                    var elem = doc.GetElement(id);
                    var bbox = elem?.get_BoundingBox(uidoc.ActiveView);
                    if (bbox != null)
                    {
                        if (combinedBox == null)
                        {
                            combinedBox = new BoundingBoxXYZ
                            {
                                Min = bbox.Min,
                                Max = bbox.Max
                            };
                        }
                        else
                        {
                            combinedBox.Min = new XYZ(
                                Math.Min(combinedBox.Min.X, bbox.Min.X),
                                Math.Min(combinedBox.Min.Y, bbox.Min.Y),
                                Math.Min(combinedBox.Min.Z, bbox.Min.Z)
                            );
                            combinedBox.Max = new XYZ(
                                Math.Max(combinedBox.Max.X, bbox.Max.X),
                                Math.Max(combinedBox.Max.Y, bbox.Max.Y),
                                Math.Max(combinedBox.Max.Z, bbox.Max.Z)
                            );
                        }
                    }
                }

                if (combinedBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Could not determine bounds of selection" });
                }

                // Add some padding
                var padding = 2.0; // feet
                var corner1 = new XYZ(combinedBox.Min.X - padding, combinedBox.Min.Y - padding, combinedBox.Min.Z);
                var corner2 = new XYZ(combinedBox.Max.X + padding, combinedBox.Max.Y + padding, combinedBox.Max.Z);

                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
                if (uiView != null)
                {
                    uiView.ZoomAndCenterRectangle(corner1, corner2);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    zoomedToElements = selectedIds.Count,
                    message = $"Zoomed to {selectedIds.Count} selected elements"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error zooming to selection");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
