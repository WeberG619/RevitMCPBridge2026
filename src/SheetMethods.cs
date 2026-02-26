using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Sheet creation and management methods for MCP Bridge
    /// </summary>
    public static class SheetMethods
    {
        /// <summary>
        /// Helper method to switch the active view in Revit.
        /// This makes the user see the view/sheet being worked on.
        /// </summary>
        /// <param name="uiDoc">The UIDocument</param>
        /// <param name="view">The view to switch to</param>
        /// <param name="forceSwitch">If true, always switch. If false, only switch if not already active.</param>
        /// <returns>True if switched or already active, false if switch failed</returns>
        private static bool SwitchToView(UIDocument uiDoc, View view, bool forceSwitch = true)
        {
            try
            {
                if (view == null || view.IsTemplate)
                    return false;

                // Check if already on this view
                if (!forceSwitch && uiDoc.ActiveView?.Id == view.Id)
                    return true;

                // Switch to the view
                uiDoc.ActiveView = view;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwitchToView] Failed to switch to view {view?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Extracts the prefix from a view name for grouping similar views.
        /// Examples: "STAIR 1" -> "STAIR", "WINDOW DETAIL 3" -> "WINDOW DETAIL", "A-101 FLOOR" -> "A-101 FLOOR"
        /// </summary>
        private static string GetViewNamePrefix(string viewName)
        {
            if (string.IsNullOrEmpty(viewName))
                return "";

            // Remove trailing numbers and whitespace to get the prefix
            // "STAIR 1" -> "STAIR", "WINDOW DETAIL 03" -> "WINDOW DETAIL"
            var trimmed = viewName.TrimEnd();

            // Find where the trailing number starts
            int i = trimmed.Length - 1;
            while (i >= 0 && (char.IsDigit(trimmed[i]) || trimmed[i] == ' ' || trimmed[i] == '-' || trimmed[i] == '_'))
            {
                // Stop if we hit a letter before the number (e.g., "A-101" should keep the whole thing)
                if (i > 0 && char.IsLetter(trimmed[i - 1]) && char.IsDigit(trimmed[i]))
                    break;
                i--;
            }

            // If we found a prefix, return it; otherwise return the whole name
            if (i >= 0 && i < trimmed.Length - 1)
                return trimmed.Substring(0, i + 1).TrimEnd(' ', '-', '_');

            return trimmed;
        }

        /// <summary>
        /// Extracts the trailing number from a view name for ordering within groups.
        /// Examples: "STAIR 1" -> 1, "WINDOW DETAIL 03" -> 3, "FLOOR PLAN" -> 0
        /// </summary>
        private static int GetViewNameNumber(string viewName)
        {
            if (string.IsNullOrEmpty(viewName))
                return 0;

            // Find trailing digits
            var trimmed = viewName.TrimEnd();
            int endIndex = trimmed.Length - 1;
            int startIndex = endIndex;

            // Walk backwards to find start of trailing number
            while (startIndex >= 0 && char.IsDigit(trimmed[startIndex]))
            {
                startIndex--;
            }
            startIndex++; // Move back to first digit

            if (startIndex <= endIndex && startIndex < trimmed.Length)
            {
                string numStr = trimmed.Substring(startIndex);
                if (int.TryParse(numStr, out int num))
                    return num;
            }

            return 0;
        }

        /// <summary>
        /// Gets the estimated size of a view when placed on a sheet.
        /// Returns dimensions in feet that the view will occupy at its current scale.
        /// </summary>
        private static (double width, double height) GetViewDimensions(View view)
        {
            // Maximum reasonable dimensions for grid layout (in feet)
            // 8" x 6" is a typical detail size, cap at 12" x 10" to prevent grid collapse
            const double maxDetailWidth = 12.0 / 12.0;   // 12 inches = 1 foot
            const double maxDetailHeight = 10.0 / 12.0;  // 10 inches
            const double defaultWidth = 4.0 / 12.0;      // 4 inches (small detail)
            const double defaultHeight = 3.0 / 12.0;     // 3 inches

            try
            {
                // For drafting views, use the crop region or outline
                if (view is ViewDrafting draftingView)
                {
                    // Try to get the view's outline
                    var outline = view.Outline;
                    if (outline != null)
                    {
                        double width = (outline.Max.U - outline.Min.U);
                        double height = (outline.Max.V - outline.Min.V);

                        // Cap at reasonable maximums for grid layout
                        width = Math.Min(width, maxDetailWidth);
                        height = Math.Min(height, maxDetailHeight);

                        // If dimensions are unreasonably small or negative, use defaults
                        if (width < 0.01 || height < 0.01)
                            return (defaultWidth, defaultHeight);

                        return (width, height);
                    }
                }

                // For model views with crop box
                if (view.CropBoxActive)
                {
                    var cropBox = view.CropBox;
                    if (cropBox != null)
                    {
                        double width = (cropBox.Max.X - cropBox.Min.X) / view.Scale;
                        double height = (cropBox.Max.Y - cropBox.Min.Y) / view.Scale;

                        // Cap at reasonable maximums
                        width = Math.Min(width, maxDetailWidth);
                        height = Math.Min(height, maxDetailHeight);

                        if (width < 0.01 || height < 0.01)
                            return (defaultWidth, defaultHeight);

                        return (width, height);
                    }
                }

                // Fallback: try outline for any view
                var viewOutline = view.Outline;
                if (viewOutline != null)
                {
                    double width = (viewOutline.Max.U - viewOutline.Min.U);
                    double height = (viewOutline.Max.V - viewOutline.Min.V);

                    // Cap at reasonable maximums
                    width = Math.Min(width, maxDetailWidth);
                    height = Math.Min(height, maxDetailHeight);

                    if (width < 0.01 || height < 0.01)
                        return (defaultWidth, defaultHeight);

                    return (width, height);
                }
            }
            catch
            {
                // If we can't get dimensions, fall back to defaults
            }

            // Default: assume 4" x 3" typical small detail size
            return (defaultWidth, defaultHeight);
        }

        /// <summary>
        /// MCP method to switch to a specific view or sheet.
        /// This is useful for showing the user what's being worked on.
        /// </summary>
        [MCPMethod("switchToView", "switchToSheet", Category = "Sheet", Description = "Switch the active view to a specific view or sheet")]
        public static string SwitchToViewMCP(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                var v = new ParameterValidator(parameters, "SwitchToViewMCP");
                v.Require("viewId").IsType<int>();
                v.ThrowIfInvalid();

                var viewIdInt = v.GetRequired<int>("viewId");
                var view = ElementLookup.GetView(doc, viewIdInt);

                if (view.IsTemplate)
                {
                    return ResponseBuilder.Error("Cannot switch to a view template", "VIEW_IS_TEMPLATE").Build();
                }

                bool switched = SwitchToView(uiDoc, view);

                return ResponseBuilder.Success()
                    .With("viewId", (int)view.Id.Value)
                    .With("viewName", view.Name)
                    .With("viewType", view.ViewType.ToString())
                    .With("isSheet", view is ViewSheet)
                    .With("message", switched ? $"Switched to {view.Name}" : "Failed to switch view")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a new sheet
        /// </summary>
        [MCPMethod("createSheet", Category = "Sheet", Description = "Create a new sheet in the project")]
        public static string CreateSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                var v = new ParameterValidator(parameters, "CreateSheet");
                v.ThrowIfInvalid();

                var sheetNumber = v.GetOptional<string>("sheetNumber", "A101");
                var sheetName = v.GetOptional<string>("sheetName", "Unnamed Sheet");

                // AUTO-SWITCH: Switch to the sheet after creation (default: true)
                bool switchTo = v.GetOptional<bool>("switchTo", true);

                // Get titleblock type
                FamilySymbol titleblock = null;
                if (parameters["titleblockId"] != null)
                {
                    var titleblockIdInt = v.GetRequired<int>("titleblockId");
                    titleblock = ElementLookup.GetFamilySymbol(doc, titleblockIdInt);
                }
                else
                {
                    // SMART DEFAULT: Use the most commonly used titleblock in the project
                    // This ensures new sheets match the project's established standards
                    var existingSheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .ToList();

                    var titleblockUsage = new Dictionary<ElementId, int>();
                    var titleblockInfo = new Dictionary<ElementId, FamilySymbol>();

                    foreach (var sheet in existingSheets)
                    {
                        var titleblockInstance = new FilteredElementCollector(doc, sheet.Id)
                            .OfClass(typeof(FamilyInstance))
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .FirstOrDefault() as FamilyInstance;

                        if (titleblockInstance != null)
                        {
                            var symbolId = titleblockInstance.Symbol.Id;
                            if (titleblockUsage.ContainsKey(symbolId))
                                titleblockUsage[symbolId]++;
                            else
                            {
                                titleblockUsage[symbolId] = 1;
                                titleblockInfo[symbolId] = titleblockInstance.Symbol;
                            }
                        }
                    }

                    // Use the most commonly used titleblock
                    if (titleblockUsage.Any())
                    {
                        var mostUsed = titleblockUsage.OrderByDescending(kv => kv.Value).First();
                        titleblock = titleblockInfo[mostUsed.Key];
                        // Log which titleblock was detected
                        System.Diagnostics.Debug.WriteLine($"[CreateSheet] Auto-detected titleblock: {titleblock.FamilyName} - {titleblock.Name} (used {mostUsed.Value} times)");
                    }
                    else
                    {
                        // Fallback: Get first available titleblock only if no sheets exist
                        System.Diagnostics.Debug.WriteLine($"[CreateSheet] No existing sheets with titleblocks found. Falling back to first available.");
                        titleblock = new FilteredElementCollector(doc)
                            .OfClass(typeof(FamilySymbol))
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .Cast<FamilySymbol>()
                            .FirstOrDefault();
                    }
                }

                if (titleblock == null)
                {
                    return ResponseBuilder.Error(
                        "No titleblock found in project. Please load a titleblock family first.",
                        "NO_TITLEBLOCK")
                        .With("hint", "Go to Insert > Load Family and load a titleblock from the library")
                        .Build();
                }

                using (var trans = new Transaction(doc, "Create Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var sheet = ViewSheet.Create(doc, titleblock.Id);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;

                    trans.Commit();

                    // AUTO-SWITCH: Switch to the newly created sheet so user sees it
                    bool didSwitch = false;
                    if (switchTo)
                    {
                        didSwitch = SwitchToView(uiDoc, sheet);
                    }

                    return ResponseBuilder.Success()
                        .With("sheetId", (int)sheet.Id.Value)
                        .With("sheetNumber", sheet.SheetNumber)
                        .With("sheetName", sheet.Name)
                        .With("titleblockId", (int)titleblock.Id.Value)
                        .With("titleblockName", titleblock.FamilyName + " - " + titleblock.Name)
                        .With("titleblockAutoDetected", parameters["titleblockId"] == null)
                        .With("switchedTo", didSwitch)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper class to hold sheet printable area information
        /// </summary>
        public class SheetPrintableArea
        {
            public double MinX { get; set; }
            public double MinY { get; set; }
            public double MaxX { get; set; }
            public double MaxY { get; set; }
            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;
            public double CenterX => (MinX + MaxX) / 2.0;
            public double CenterY => (MinY + MaxY) / 2.0;
            public string Source { get; set; } // "titleblock", "guidegrid", "default"
            public double AppliedMargin { get; set; }
        }

        /// <summary>
        /// Get the printable/usable area of a sheet with smart boundary detection.
        /// Priority: 1) Titleblock bounds, 2) Guide Grid, 3) Default sheet size
        /// Applies proper margins to ensure viewports don't go off the edge.
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <param name="sheet">The ViewSheet to analyze</param>
        /// <param name="marginInches">Margin from edges in inches (default 1.0)</param>
        /// <returns>SheetPrintableArea with bounds and metadata</returns>
        public static SheetPrintableArea GetSheetPrintableArea(Document doc, ViewSheet sheet, double marginInches = 1.0)
        {
            var result = new SheetPrintableArea();
            double marginFeet = marginInches / 12.0; // Convert inches to feet
            result.AppliedMargin = marginInches;

            // Default sheet size (ARCH D: 36" x 24")
            double defaultMinX = 0;
            double defaultMinY = 0;
            double defaultMaxX = 3.0;  // 36 inches = 3 feet
            double defaultMaxY = 2.0;  // 24 inches = 2 feet

            // PRIORITY 1: Try to get titleblock bounds
            var titleblockElement = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .FirstOrDefault() as FamilyInstance;

            if (titleblockElement != null)
            {
                var bbox = titleblockElement.get_BoundingBox(sheet);
                if (bbox != null)
                {
                    // Apply margin from titleblock edges
                    result.MinX = bbox.Min.X + marginFeet;
                    result.MinY = bbox.Min.Y + marginFeet;
                    result.MaxX = bbox.Max.X - marginFeet;
                    result.MaxY = bbox.Max.Y - marginFeet;
                    result.Source = "titleblock";

                    // Validate we have a reasonable area
                    if (result.Width > 0.5 && result.Height > 0.5) // At least 6" x 6"
                    {
                        return result;
                    }
                }
            }

            // PRIORITY 2: Try to get guide grid bounds
            try
            {
                // Check if sheet has a guide grid assigned via parameter
                var guideGridParam = sheet.get_Parameter(BuiltInParameter.SHEET_GUIDE_GRID);
                if (guideGridParam != null && guideGridParam.HasValue)
                {
                    var guideGridId = guideGridParam.AsElementId();
                    if (guideGridId != null && guideGridId != ElementId.InvalidElementId)
                    {
                        var guideGrid = doc.GetElement(guideGridId);
                        if (guideGrid != null)
                        {
                            var gridBbox = guideGrid.get_BoundingBox(sheet);
                            if (gridBbox != null)
                            {
                                // Guide grid already represents the usable area, add small margin
                                double smallMargin = 0.04; // ~0.5 inch additional safety margin
                                result.MinX = gridBbox.Min.X + smallMargin;
                                result.MinY = gridBbox.Min.Y + smallMargin;
                                result.MaxX = gridBbox.Max.X - smallMargin;
                                result.MaxY = gridBbox.Max.Y - smallMargin;
                                result.Source = "guidegrid";

                                if (result.Width > 0.5 && result.Height > 0.5)
                                {
                                    return result;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Guide grid API might not be available, continue to fallback
            }

            // PRIORITY 3: Check for any existing viewports to infer usable area
            var existingViewports = sheet.GetAllViewports();
            if (existingViewports != null && existingViewports.Count > 0)
            {
                double inferredMinX = double.MaxValue;
                double inferredMinY = double.MaxValue;
                double inferredMaxX = double.MinValue;
                double inferredMaxY = double.MinValue;

                foreach (ElementId vpId in existingViewports)
                {
                    var viewport = doc.GetElement(vpId) as Viewport;
                    if (viewport != null)
                    {
                        var outline = viewport.GetBoxOutline();
                        if (outline != null)
                        {
                            inferredMinX = Math.Min(inferredMinX, outline.MinimumPoint.X);
                            inferredMinY = Math.Min(inferredMinY, outline.MinimumPoint.Y);
                            inferredMaxX = Math.Max(inferredMaxX, outline.MaximumPoint.X);
                            inferredMaxY = Math.Max(inferredMaxY, outline.MaximumPoint.Y);
                        }
                    }
                }

                // If we found viewports, expand slightly to allow more space
                if (inferredMinX < double.MaxValue)
                {
                    // Use titleblock bounds if available, otherwise expand inferred area
                    if (titleblockElement != null)
                    {
                        var bbox = titleblockElement.get_BoundingBox(sheet);
                        if (bbox != null)
                        {
                            result.MinX = bbox.Min.X + marginFeet;
                            result.MinY = bbox.Min.Y + marginFeet;
                            result.MaxX = bbox.Max.X - marginFeet;
                            result.MaxY = bbox.Max.Y - marginFeet;
                            result.Source = "titleblock+viewports";
                            return result;
                        }
                    }

                    // Expand inferred area by 20% on each side
                    double expandX = (inferredMaxX - inferredMinX) * 0.2;
                    double expandY = (inferredMaxY - inferredMinY) * 0.2;
                    result.MinX = inferredMinX - expandX;
                    result.MinY = inferredMinY - expandY;
                    result.MaxX = inferredMaxX + expandX;
                    result.MaxY = inferredMaxY + expandY;
                    result.Source = "inferred";
                    return result;
                }
            }

            // FALLBACK: Use default sheet size with margin
            result.MinX = defaultMinX + marginFeet;
            result.MinY = defaultMinY + marginFeet;
            result.MaxX = defaultMaxX - marginFeet;
            result.MaxY = defaultMaxY - marginFeet;
            result.Source = "default";

            return result;
        }

        /// <summary>
        /// MCP-accessible method to get printable area of a sheet
        /// </summary>
        [MCPMethod("getSheetPrintableArea", Category = "Sheet", Description = "Get the printable area of a sheet")]
        public static string GetSheetPrintableAreaMCP(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var sheet = doc.GetElement(sheetId) as ViewSheet;

                if (sheet == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });

                double marginInches = parameters["marginInches"]?.Value<double>() ?? 1.0;

                var area = GetSheetPrintableArea(doc, sheet, marginInches);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = (int)sheetId.Value,
                    sheetNumber = sheet.SheetNumber,
                    printableArea = new
                    {
                        minX = Math.Round(area.MinX, 4),
                        minY = Math.Round(area.MinY, 4),
                        maxX = Math.Round(area.MaxX, 4),
                        maxY = Math.Round(area.MaxY, 4),
                        widthFeet = Math.Round(area.Width, 4),
                        heightFeet = Math.Round(area.Height, 4),
                        widthInches = Math.Round(area.Width * 12, 2),
                        heightInches = Math.Round(area.Height * 12, 2),
                        centerX = Math.Round(area.CenterX, 4),
                        centerY = Math.Round(area.CenterY, 4)
                    },
                    detectionSource = area.Source,
                    appliedMarginInches = area.AppliedMargin
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a view on a sheet
        /// Accepts either "location" array [x,y] OR separate "x"/"y" params
        /// </summary>
        [MCPMethod("placeViewOnSheet", Category = "Sheet", Description = "Place a view onto a sheet at a specified location")]
        public static string PlaceViewOnSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                // Validate required parameters
                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                }
                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                // Validate sheet exists
                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Sheet with ID {sheetId.Value} not found or is not a ViewSheet"
                    });
                }

                // AUTO-SWITCH: Switch to the sheet before working on it (default: true)
                bool switchTo = parameters["switchTo"]?.Value<bool>() ?? true;
                if (switchTo)
                {
                    SwitchToView(uiDoc, sheet);
                }

                // Get titleblock bounds for smart positioning
                var titleblockElement = new FilteredElementCollector(doc, sheetId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .FirstOrDefault() as FamilyInstance;

                double printMinX = 0, printMinY = 0, printMaxX = 2.0, printMaxY = 1.5; // Fallback defaults
                double centerX = 1.0, centerY = 0.75;

                if (titleblockElement != null)
                {
                    var bbox = titleblockElement.get_BoundingBox(sheet);
                    if (bbox != null)
                    {
                        // Define margins for printable area (conservative estimates in feet)
                        double marginLeft = 0.04;    // ~0.5 inch
                        double marginRight = 0.04;   // ~0.5 inch
                        double marginTop = 0.04;     // ~0.5 inch
                        double marginBottom = 0.04;  // ~0.5 inch

                        printMinX = bbox.Min.X + marginLeft;
                        printMinY = bbox.Min.Y + marginBottom;
                        printMaxX = bbox.Max.X - marginRight;
                        printMaxY = bbox.Max.Y - marginTop;

                        centerX = (printMinX + printMaxX) / 2.0;
                        centerY = (printMinY + printMaxY) / 2.0;
                    }
                }

                // Parse location - accept either "location" array OR "x"/"y" params
                // Default to center of printable area instead of hardcoded values
                double x = centerX, y = centerY;

                if (parameters["location"] != null)
                {
                    var location = parameters["location"].ToObject<double[]>();
                    if (location != null && location.Length >= 2)
                    {
                        x = location[0];
                        y = location[1];
                    }
                }
                else
                {
                    // Accept x/y as separate params
                    if (parameters["x"] != null) { x = double.Parse(parameters["x"].ToString()); }
                    if (parameters["y"] != null) { y = double.Parse(parameters["y"].ToString()); }
                }

                // CONSTRAIN position to be within titleblock printable area
                // This prevents elements from being placed off the sheet
                bool wasConstrained = false;
                double originalX = x, originalY = y;

                if (x < printMinX) { x = printMinX + 0.1; wasConstrained = true; }
                if (x > printMaxX) { x = printMaxX - 0.1; wasConstrained = true; }
                if (y < printMinY) { y = printMinY + 0.1; wasConstrained = true; }
                if (y > printMaxY) { y = printMaxY - 0.1; wasConstrained = true; }

                // Validate view exists
                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewId.Value} not found or is not a View"
                    });
                }

                // Check if view can be placed on sheet (Revit API validation)
                if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
                {
                    // Try to determine why
                    string reason = "Unknown reason";
                    if (view.ViewType == ViewType.DrawingSheet)
                    {
                        reason = "Cannot place a sheet on another sheet";
                    }
                    else if (view.IsTemplate)
                    {
                        reason = "Cannot place a view template";
                    }
                    else
                    {
                        // Check if already placed
                        var existingViewports = new FilteredElementCollector(doc)
                            .OfClass(typeof(Viewport))
                            .Cast<Viewport>()
                            .Where(vp => vp.ViewId == viewId)
                            .ToList();

                        if (existingViewports.Any())
                        {
                            var existingSheet = doc.GetElement(existingViewports[0].SheetId) as ViewSheet;
                            reason = $"View is already placed on sheet '{existingSheet?.SheetNumber ?? "unknown"}'";
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Cannot place view '{view.Name}' on sheet '{sheet.SheetNumber}': {reason}",
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        sheetNumber = sheet.SheetNumber
                    });
                }

                // For drafting views, ensure the view has been opened/activated at least once
                // This is needed for Revit to calculate proper view bounds
                if (view is ViewDrafting)
                {
                    try
                    {
                        // Temporarily activate the view to force Revit to calculate its bounds
                        uiDoc.ActiveView = view;
                        System.Threading.Thread.Sleep(100); // Brief pause for Revit to process
                        uiDoc.ActiveView = sheet; // Switch back to sheet
                    }
                    catch { /* Ignore activation errors */ }
                }

                using (var trans = new Transaction(doc, "Place View on Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Regenerate inside transaction to ensure view geometry is current
                    doc.Regenerate();

                    var point = new XYZ(x, y, 0);

                    // Debug: Check CanAddViewToSheet again inside transaction
                    bool canAdd = Viewport.CanAddViewToSheet(doc, sheetId, viewId);

                    Viewport viewport = null;
                    Exception createException = null;

                    // Try multiple placement attempts with slight variations
                    var attempts = new XYZ[]
                    {
                        point,
                        new XYZ(x + 0.01, y + 0.01, 0),  // Slight offset
                        new XYZ(centerX, centerY, 0)      // Center of sheet
                    };

                    foreach (var attemptPoint in attempts)
                    {
                        try
                        {
                            viewport = Viewport.Create(doc, sheetId, viewId, attemptPoint);
                            if (viewport != null)
                            {
                                // Move to the intended position if we used a different point
                                if (attemptPoint != point)
                                {
                                    try { viewport.SetBoxCenter(point); } catch { }
                                }
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            createException = ex;
                            viewport = null;
                        }
                    }

                    if (viewport == null && createException != null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Viewport.Create threw exception: {createException.Message}",
                            viewType = view.ViewType.ToString(),
                            viewName = view.Name,
                            canAddViewToSheet = canAdd
                        });
                    }

                    if (viewport == null)
                    {
                        // Last resort: Check if the view has any content
                        int elementCount = 0;
                        try
                        {
                            elementCount = new FilteredElementCollector(doc, viewId)
                                .WhereElementIsNotElementType()
                                .GetElementCount();
                        }
                        catch { }

                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = elementCount == 0
                                ? "Viewport.Create returned null - view appears to be empty (no elements). Import the detail again or add content to the view first."
                                : "Viewport.Create returned null - placement failed. Try regenerating the view or reopening Revit.",
                            viewType = view.ViewType.ToString(),
                            viewName = view.Name,
                            canAddViewToSheet = canAdd,
                            sheetNumber = sheet.SheetNumber,
                            location = new[] { x, y },
                            viewElementCount = elementCount
                        });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = (int)viewport.Id.Value,
                        sheetId = (int)sheetId.Value,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        sheetNumber = sheet.SheetNumber,
                        location = new[] { point.X, point.Y, 0.0 },
                        positionConstrained = wasConstrained,
                        originalLocation = wasConstrained ? new[] { originalX, originalY } : null,
                        printableArea = new
                        {
                            minX = Math.Round(printMinX, 4),
                            minY = Math.Round(printMinY, 4),
                            maxX = Math.Round(printMaxX, 4),
                            maxY = Math.Round(printMaxY, 4),
                            centerX = Math.Round(centerX, 4),
                            centerY = Math.Round(centerY, 4)
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all sheets in the project
        /// </summary>
        [MCPMethod("getAllSheets", "getSheets", Category = "Sheet", Description = "Get all sheets in the project")]
        public static string GetAllSheets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheets = ElementLookup.GetAllSheets(doc)
                    .Where(s => !s.IsPlaceholder)
                    .Select(s => new
                    {
                        sheetId = (int)s.Id.Value,
                        sheetNumber = s.SheetNumber,
                        sheetName = s.Name,
                        isPlaceholder = s.IsPlaceholder,
                        viewportCount = s.GetAllViewports()?.Count ?? 0
                    })
                    .OrderBy(s => s.sheetNumber)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("sheetCount", sheets.Count)
                    .With("sheets", sheets)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get viewports on a sheet
        /// </summary>
        [MCPMethod("getViewportsOnSheet", Category = "Sheet", Description = "Get all viewports placed on a sheet")]
        public static string GetViewportsOnSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetViewportsOnSheet");
                v.Require("sheetId").IsType<int>();
                v.ThrowIfInvalid();

                var sheetIdInt = v.GetRequired<int>("sheetId");
                var sheet = ElementLookup.GetSheet(doc, sheetIdInt);

                var viewportIds = sheet.GetAllViewports();
                var viewports = new List<object>();

                foreach (var vpId in viewportIds)
                {
                    try
                    {
                        var viewport = doc.GetElement(vpId) as Viewport;
                        if (viewport != null)
                        {
                            var view = doc.GetElement(viewport.ViewId) as View;
                            var location = viewport.GetBoxCenter();

                            viewports.Add(new
                            {
                                viewportId = (int)viewport.Id.Value,
                                viewId = (int)viewport.ViewId.Value,
                                viewName = view?.Name ?? "",
                                location = new[] { location.X, location.Y, location.Z },
                                typeId = (int)viewport.GetTypeId().Value
                            });
                        }
                    }
                    catch { continue; }
                }

                return ResponseBuilder.Success()
                    .With("sheetId", sheetIdInt)
                    .With("sheetNumber", sheet.SheetNumber ?? "")
                    .With("sheetName", sheet.Name ?? "")
                    .With("viewportCount", viewports.Count)
                    .With("viewports", viewports)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify sheet properties
        /// </summary>
        [MCPMethod("modifySheetProperties", Category = "Sheet", Description = "Modify properties of an existing sheet")]
        public static string ModifySheetProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "ModifySheetProperties");
                v.Require("sheetId").IsType<int>();
                v.ThrowIfInvalid();

                var sheetIdInt = v.GetRequired<int>("sheetId");
                var sheet = ElementLookup.GetSheet(doc, sheetIdInt);

                using (var trans = new Transaction(doc, "Modify Sheet Properties"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modified = new List<string>();

                    // Change sheet number
                    if (parameters["sheetNumber"] != null)
                    {
                        sheet.SheetNumber = parameters["sheetNumber"].ToString();
                        modified.Add("sheetNumber");
                    }

                    // Change sheet name
                    if (parameters["sheetName"] != null)
                    {
                        sheet.Name = parameters["sheetName"].ToString();
                        modified.Add("sheetName");
                    }

                    // Change drawn by
                    if (parameters["drawnBy"] != null)
                    {
                        var drawnByParam = sheet.get_Parameter(BuiltInParameter.SHEET_DRAWN_BY);
                        if (drawnByParam != null && !drawnByParam.IsReadOnly)
                        {
                            drawnByParam.Set(parameters["drawnBy"].ToString());
                            modified.Add("drawnBy");
                        }
                    }

                    // Change checked by
                    if (parameters["checkedBy"] != null)
                    {
                        var checkedByParam = sheet.get_Parameter(BuiltInParameter.SHEET_CHECKED_BY);
                        if (checkedByParam != null && !checkedByParam.IsReadOnly)
                        {
                            checkedByParam.Set(parameters["checkedBy"].ToString());
                            modified.Add("checkedBy");
                        }
                    }

                    // Change sheet issue date
                    if (parameters["issueDate"] != null)
                    {
                        var issueDateParam = sheet.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE);
                        if (issueDateParam != null && !issueDateParam.IsReadOnly)
                        {
                            issueDateParam.Set(parameters["issueDate"].ToString());
                            modified.Add("issueDate");
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("sheetId", sheetIdInt)
                        .With("modified", modified)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move viewport on sheet - tries multiple approaches for reliable positioning
        /// </summary>
        [MCPMethod("moveViewport", Category = "Sheet", Description = "Move a viewport to a new position on a sheet")]
        public static string MoveViewport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewportId = new ElementId(int.Parse(parameters["viewportId"].ToString()));
                var newLocation = parameters["newLocation"].ToObject<double[]>();

                var viewport = doc.GetElement(viewportId) as Viewport;
                if (viewport == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Viewport not found"
                    });
                }

                // Get current center
                var currentCenter = viewport.GetBoxCenter();
                var targetPoint = new XYZ(newLocation[0], newLocation[1], 0);  // Z should always be 0 for sheet

                // Calculate the movement delta
                var delta = targetPoint - currentCenter;

                string methodUsed = "";
                bool moved = false;

                using (var trans = new Transaction(doc, "Move Viewport"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // CRITICAL: Clear "Saved Position" parameter first - this unlocks viewport movement
                    try
                    {
                        var savedPosParam = viewport.LookupParameter("Saved Position");
                        if (savedPosParam != null && !savedPosParam.IsReadOnly)
                        {
                            savedPosParam.Set(ElementId.InvalidElementId);
                        }
                    }
                    catch { }

                    // Method 1: Try SetBoxCenter (the documented approach)
                    try
                    {
                        viewport.SetBoxCenter(targetPoint);
                        doc.Regenerate();
                        var checkCenter = viewport.GetBoxCenter();
                        if (Math.Abs(checkCenter.X - targetPoint.X) < 0.01 && Math.Abs(checkCenter.Y - targetPoint.Y) < 0.01)
                        {
                            moved = true;
                            methodUsed = "SetBoxCenter";
                        }
                    }
                    catch { }

                    // Method 2: Try Location.Move if SetBoxCenter didn't work
                    if (!moved)
                    {
                        try
                        {
                            var location = viewport.Location;
                            if (location != null)
                            {
                                location.Move(delta);
                                doc.Regenerate();
                                var checkCenter = viewport.GetBoxCenter();
                                if (Math.Abs(checkCenter.X - targetPoint.X) < 0.01 && Math.Abs(checkCenter.Y - targetPoint.Y) < 0.01)
                                {
                                    moved = true;
                                    methodUsed = "Location.Move";
                                }
                            }
                        }
                        catch { }
                    }

                    // Method 3: Try ElementTransformUtils
                    if (!moved)
                    {
                        try
                        {
                            // Recalculate delta based on current position
                            var newCurrentCenter = viewport.GetBoxCenter();
                            var newDelta = targetPoint - newCurrentCenter;
                            ElementTransformUtils.MoveElement(doc, viewportId, newDelta);
                            doc.Regenerate();
                            var checkCenter = viewport.GetBoxCenter();
                            if (Math.Abs(checkCenter.X - targetPoint.X) < 0.01 && Math.Abs(checkCenter.Y - targetPoint.Y) < 0.01)
                            {
                                moved = true;
                                methodUsed = "ElementTransformUtils";
                            }
                        }
                        catch { }
                    }

                    trans.Commit();

                    // Verify the final position
                    var actualCenter = viewport.GetBoxCenter();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = (int)viewportId.Value,
                        requestedLocation = new[] { targetPoint.X, targetPoint.Y, 0.0 },
                        previousCenter = new[] { currentCenter.X, currentCenter.Y, currentCenter.Z },
                        actualCenter = new[] { actualCenter.X, actualCenter.Y, actualCenter.Z },
                        delta = new[] { delta.X, delta.Y, delta.Z },
                        moved = moved,
                        methodUsed = methodUsed,
                        positionError = new[] {
                            Math.Round(Math.Abs(actualCenter.X - targetPoint.X), 4),
                            Math.Round(Math.Abs(actualCenter.Y - targetPoint.Y), 4)
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a sheet
        /// </summary>
        [MCPMethod("deleteSheet", Category = "Sheet", Description = "Delete a sheet from the project")]
        public static string DeleteSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(sheetId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sheetId = (int)sheetId.Value,
                        message = "Sheet deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove viewport from sheet
        /// </summary>
        [MCPMethod("removeViewport", Category = "Sheet", Description = "Remove a viewport from a sheet")]
        public static string RemoveViewport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewportId = new ElementId(int.Parse(parameters["viewportId"].ToString()));

                using (var trans = new Transaction(doc, "Remove Viewport"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(viewportId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = (int)viewportId.Value,
                        message = "Viewport removed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all titleblock types
        /// </summary>
        [MCPMethod("getTitleblockTypes", Category = "Sheet", Description = "Get all titleblock types available in the project")]
        public static string GetTitleblockTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var titleblocks = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .Select(tb => new
                    {
                        titleblockId = (int)tb.Id.Value,
                        familyName = tb.Family.Name,
                        typeName = tb.Name,
                        isActive = tb.IsActive
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    titleblockCount = titleblocks.Count,
                    titleblocks = titleblocks
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a sheet
        /// </summary>
        [MCPMethod("duplicateSheet", Category = "Sheet", Description = "Duplicate an existing sheet")]
        public static string DuplicateSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var newNumber = parameters["newNumber"]?.ToString();
                var newName = parameters["newName"]?.ToString();
                var duplicateViewports = parameters["duplicateViewports"] != null
                    ? bool.Parse(parameters["duplicateViewports"].ToString())
                    : false;

                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Sheet not found"
                    });
                }

                using (var trans = new Transaction(doc, "Duplicate Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get titleblock from original sheet
                    var titleblockId = new FilteredElementCollector(doc, sheetId)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstElementId();

                    if (titleblockId == ElementId.InvalidElementId)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No titleblock found on original sheet"
                        });
                    }

                    var titleblock = doc.GetElement(titleblockId) as FamilyInstance;
                    var newSheet = ViewSheet.Create(doc, titleblock.Symbol.Id);

                    newSheet.SheetNumber = newNumber ?? $"{sheet.SheetNumber} - Copy";
                    newSheet.Name = newName ?? sheet.Name;

                    var newViewportIds = new List<int>();

                    // Duplicate viewports if requested
                    if (duplicateViewports)
                    {
                        var viewportIds = sheet.GetAllViewports();
                        foreach (var vpId in viewportIds)
                        {
                            var viewport = doc.GetElement(vpId) as Viewport;
                            if (viewport != null)
                            {
                                var view = doc.GetElement(viewport.ViewId) as View;
                                if (view != null && !view.IsTemplate)
                                {
                                    // Duplicate the view
                                    var newViewId = view.Duplicate(ViewDuplicateOption.Duplicate);
                                    var location = viewport.GetBoxCenter();

                                    // Place on new sheet
                                    var newViewport = Viewport.Create(doc, newSheet.Id, newViewId, location);
                                    newViewportIds.Add((int)newViewport.Id.Value);
                                }
                            }
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalSheetId = (int)sheetId.Value,
                        newSheetId = (int)newSheet.Id.Value,
                        newSheetNumber = newSheet.SheetNumber,
                        newSheetName = newSheet.Name,
                        duplicatedViewports = newViewportIds.Count,
                        viewportIds = newViewportIds
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set viewport type
        /// </summary>
        [MCPMethod("setViewportType", Category = "Sheet", Description = "Set the viewport type for a viewport on a sheet")]
        public static string SetViewportType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewportId = new ElementId(int.Parse(parameters["viewportId"].ToString()));
                var typeId = new ElementId(int.Parse(parameters["typeId"].ToString()));

                var viewport = doc.GetElement(viewportId) as Viewport;
                if (viewport == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Viewport not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set Viewport Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    viewport.ChangeTypeId(typeId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = (int)viewportId.Value,
                        newTypeId = (int)typeId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get viewport label offset (position of view title relative to viewport)
        /// </summary>
        [MCPMethod("getViewportLabelOffset", Category = "Sheet", Description = "Get the label offset for a viewport on a sheet")]
        public static string GetViewportLabelOffset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters?["viewportId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewportId is required"
                    });
                }

                var viewportId = new ElementId(int.Parse(parameters["viewportId"].ToString()));
                var viewport = doc.GetElement(viewportId) as Viewport;

                if (viewport == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Viewport not found"
                    });
                }

                var labelOffset = viewport.LabelOffset;
                var boxCenter = viewport.GetBoxCenter();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewportId = (int)viewportId.Value,
                    labelOffset = new[] { labelOffset.X, labelOffset.Y, labelOffset.Z },
                    boxCenter = new[] { boxCenter.X, boxCenter.Y, boxCenter.Z },
                    note = "labelOffset is relative to viewport box center. Positive Y moves title up."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set viewport label offset (move view title position)
        /// </summary>
        [MCPMethod("setViewportLabelOffset", Category = "Sheet", Description = "Set the label offset position for a viewport on a sheet")]
        public static string SetViewportLabelOffset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters?["viewportId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewportId is required"
                    });
                }

                var viewportId = new ElementId(int.Parse(parameters["viewportId"].ToString()));
                var viewport = doc.GetElement(viewportId) as Viewport;

                if (viewport == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Viewport not found"
                    });
                }

                // Get offset values - can be array or individual x,y,z params
                double offsetX = 0, offsetY = 0, offsetZ = 0;

                if (parameters["offset"] != null)
                {
                    var offsetArray = parameters["offset"].ToObject<double[]>();
                    offsetX = offsetArray.Length > 0 ? offsetArray[0] : 0;
                    offsetY = offsetArray.Length > 1 ? offsetArray[1] : 0;
                    offsetZ = offsetArray.Length > 2 ? offsetArray[2] : 0;
                }
                else
                {
                    offsetX = parameters["offsetX"]?.ToObject<double>() ?? 0;
                    offsetY = parameters["offsetY"]?.ToObject<double>() ?? 0;
                    offsetZ = parameters["offsetZ"]?.ToObject<double>() ?? 0;
                }

                var oldOffset = viewport.LabelOffset;

                using (var trans = new Transaction(doc, "Set Viewport Label Offset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    viewport.LabelOffset = new XYZ(offsetX, offsetY, offsetZ);

                    trans.Commit();

                    var newOffset = viewport.LabelOffset;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = (int)viewportId.Value,
                        previousOffset = new[] { oldOffset.X, oldOffset.Y, oldOffset.Z },
                        newOffset = new[] { newOffset.X, newOffset.Y, newOffset.Z },
                        message = "Label offset updated. Positive Y moves title up, negative moves down."
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Renumber sheets
        /// </summary>
        [MCPMethod("renumberSheets", Category = "Sheet", Description = "Renumber sheets according to a new numbering scheme")]
        public static string RenumberSheets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetIdsArray = parameters["sheetIds"].ToObject<string[]>();
                var startNumber = parameters["startNumber"]?.ToString() ?? "A101";
                var prefix = parameters["prefix"]?.ToString() ?? "";

                var sheetIds = sheetIdsArray.Select(id => new ElementId(int.Parse(id))).ToList();

                using (var trans = new Transaction(doc, "Renumber Sheets"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var renumbered = new List<object>();
                    var currentNumber = int.Parse(System.Text.RegularExpressions.Regex.Match(startNumber, @"\d+").Value);

                    foreach (var sheetId in sheetIds)
                    {
                        var sheet = doc.GetElement(sheetId) as ViewSheet;
                        if (sheet != null)
                        {
                            var newNumber = $"{prefix}{currentNumber:D3}";
                            sheet.SheetNumber = newNumber;
                            renumbered.Add(new
                            {
                                sheetId = (int)sheetId.Value,
                                newNumber = newNumber,
                                sheetName = sheet.Name
                            });
                            currentNumber++;
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        renumberedCount = renumbered.Count,
                        sheets = renumbered
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get title block dimensions and details
        /// </summary>
        [MCPMethod("getTitleblockDimensions", Category = "Sheet", Description = "Get the dimensions and details of a title block")]
        public static string GetTitleblockDimensions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var titleblocks = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .Select(tb => {
                        // Get sheet size from titleblock parameters
                        double width = 0, height = 0;
                        string sheetSize = "";

                        var widthParam = tb.LookupParameter("Sheet Width") ?? tb.LookupParameter("Width");
                        var heightParam = tb.LookupParameter("Sheet Height") ?? tb.LookupParameter("Height");

                        if (widthParam != null) width = widthParam.AsDouble();
                        if (heightParam != null) height = heightParam.AsDouble();

                        // Try to get sheet size parameter
                        var sizeParam = tb.LookupParameter("Sheet Size") ?? tb.LookupParameter("Size");
                        if (sizeParam != null) sheetSize = sizeParam.AsString() ?? sizeParam.AsValueString() ?? "";

                        return new
                        {
                            titleblockId = (int)tb.Id.Value,
                            familyName = tb.Family.Name,
                            typeName = tb.Name,
                            width = width,  // In feet
                            height = height,  // In feet
                            widthInches = width * 12,  // Convert to inches
                            heightInches = height * 12,  // Convert to inches
                            sheetSize = sheetSize,
                            isActive = tb.IsActive
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    titleblockCount = titleblocks.Count,
                    titleblocks = titleblocks
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get viewport bounding boxes and details
        /// </summary>
        [MCPMethod("getViewportBounds", Category = "Sheet", Description = "Get bounding box and position details for viewports on a sheet")]
        public static string GetViewportBounds(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var sheet = doc.GetElement(sheetId) as ViewSheet;

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                var viewportIds = sheet.GetAllViewports();
                var viewportData = new List<object>();

                foreach (var vpId in viewportIds)
                {
                    var viewport = doc.GetElement(vpId) as Viewport;
                    if (viewport != null)
                    {
                        var view = doc.GetElement(viewport.ViewId) as View;
                        var outline = viewport.GetBoxOutline();
                        var center = viewport.GetBoxCenter();

                        var min = outline.MinimumPoint;
                        var max = outline.MaximumPoint;

                        viewportData.Add(new
                        {
                            viewportId = (int)viewport.Id.Value,
                            viewId = (int)viewport.ViewId.Value,
                            viewName = view?.Name,
                            viewScale = view?.Scale ?? 0,
                            center = new[] { center.X, center.Y, center.Z },
                            minPoint = new[] { min.X, min.Y, min.Z },
                            maxPoint = new[] { max.X, max.Y, max.Z },
                            width = max.X - min.X,
                            height = max.Y - min.Y
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = (int)sheetId.Value,
                    viewportCount = viewportData.Count,
                    viewports = viewportData
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Analyze sheet layout for overlaps and boundary issues
        /// </summary>
        [MCPMethod("analyzeSheetLayout", Category = "Sheet", Description = "Analyze a sheet layout for viewport overlaps and boundary issues")]
        public static string AnalyzeSheetLayout(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var sheet = doc.GetElement(sheetId) as ViewSheet;

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                // Get sheet bounds from titleblock - FIXED VERSION
                var titleblockElement = new FilteredElementCollector(doc, sheetId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .FirstOrDefault();

                double sheetWidth = 0, sheetHeight = 0;
                double drawingAreaWidth = 0, drawingAreaHeight = 0;
                double titleBlockHeight = 0;

                if (titleblockElement != null)
                {
                    // Get bounding box in the sheet view context
                    var bbox = titleblockElement.get_BoundingBox(sheet);
                    if (bbox != null)
                    {
                        sheetWidth = bbox.Max.X - bbox.Min.X;
                        sheetHeight = bbox.Max.Y - bbox.Min.Y;

                        // Estimate title block height (usually bottom 6-8 inches)
                        titleBlockHeight = 0.6;  // ~7 inches in feet

                        // Calculate available drawing area
                        drawingAreaWidth = sheetWidth - 0.3;  // 3-4" margins on sides
                        drawingAreaHeight = sheetHeight - titleBlockHeight - 0.2;  // Title block + top margin
                    }
                }

                var viewportIds = sheet.GetAllViewports();
                var viewports = new List<(Viewport vp, Outline outline)>();
                var issues = new List<object>();

                // Collect all viewports with their bounds
                foreach (var vpId in viewportIds)
                {
                    var viewport = doc.GetElement(vpId) as Viewport;
                    if (viewport != null)
                    {
                        viewports.Add((viewport, viewport.GetBoxOutline()));
                    }
                }

                // Check for overlaps
                for (int i = 0; i < viewports.Count; i++)
                {
                    var (vp1, outline1) = viewports[i];
                    var view1 = doc.GetElement(vp1.ViewId) as View;

                    // Check if viewport is off sheet
                    if (sheetWidth > 0 && sheetHeight > 0)
                    {
                        var min1 = outline1.MinimumPoint;
                        var max1 = outline1.MaximumPoint;

                        if (min1.X < 0 || min1.Y < 0 || max1.X > sheetWidth || max1.Y > sheetHeight)
                        {
                            issues.Add(new
                            {
                                type = "OFF_SHEET",
                                viewportId = (int)vp1.Id.Value,
                                viewName = view1?.Name,
                                message = $"Viewport extends beyond sheet boundaries"
                            });
                        }
                    }

                    // Check for overlaps with other viewports
                    for (int j = i + 1; j < viewports.Count; j++)
                    {
                        var (vp2, outline2) = viewports[j];

                        if (outline1.Intersects(outline2, 0.01))
                        {
                            var view2 = doc.GetElement(vp2.ViewId) as View;
                            issues.Add(new
                            {
                                type = "OVERLAP",
                                viewport1Id = (int)vp1.Id.Value,
                                viewport2Id = (int)vp2.Id.Value,
                                view1Name = view1?.Name,
                                view2Name = view2?.Name,
                                message = $"Viewports overlap: {view1?.Name} and {view2?.Name}"
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = (int)sheetId.Value,
                    sheetWidth = sheetWidth,
                    sheetHeight = sheetHeight,
                    sheetWidthInches = sheetWidth * 12,
                    sheetHeightInches = sheetHeight * 12,
                    drawingAreaWidth = drawingAreaWidth,
                    drawingAreaHeight = drawingAreaHeight,
                    drawingAreaWidthInches = drawingAreaWidth * 12,
                    drawingAreaHeightInches = drawingAreaHeight * 12,
                    titleBlockHeight = titleBlockHeight,
                    viewportCount = viewports.Count,
                    issueCount = issues.Count,
                    issues = issues,
                    hasOverlaps = issues.Any(i => ((dynamic)i).type == "OVERLAP"),
                    hasOffSheetViews = issues.Any(i => ((dynamic)i).type == "OFF_SHEET")
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Calculate optimal view scale to fit on sheet
        /// </summary>
        [MCPMethod("calculateOptimalScale", Category = "Sheet", Description = "Calculate the optimal scale for a view to fit on a sheet")]
        public static string CalculateOptimalScale(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var targetWidth = double.Parse(parameters["targetWidth"].ToString());  // Available width on sheet in feet
                var targetHeight = double.Parse(parameters["targetHeight"].ToString());  // Available height on sheet in feet

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Get view crop box extents
                var cropBox = view.CropBox;
                if (cropBox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View has no crop box" });
                }

                var viewWidth = cropBox.Max.X - cropBox.Min.X;
                var viewHeight = cropBox.Max.Y - cropBox.Min.Y;

                // Calculate scale needed to fit width and height
                var scaleForWidth = (int)Math.Ceiling(viewWidth / targetWidth);
                var scaleForHeight = (int)Math.Ceiling(viewHeight / targetHeight);

                // Use the larger scale to ensure it fits both dimensions
                var recommendedScale = Math.Max(scaleForWidth, scaleForHeight);

                // Round to standard architectural scales
                var standardScales = new[] { 1, 2, 4, 8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384 };
                var closestScale = standardScales.OrderBy(s => Math.Abs(s - recommendedScale)).First();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    viewWidth = viewWidth,
                    viewHeight = viewHeight,
                    targetWidth = targetWidth,
                    targetHeight = targetHeight,
                    currentScale = view.Scale,
                    recommendedScale = closestScale,
                    scaleDescription = $"1/{closestScale}\""
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// List all available title blocks in the project
        /// Returns family name, type name, dimensions, and ID for each
        /// </summary>
        [MCPMethod("listTitleBlocks", Category = "Sheet", Description = "List all available title block families and types in the project")]
        public static string ListTitleBlocks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var titleBlocks = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .Select(tb =>
                    {
                        // Get sheet dimensions from title block family
                        double width = 0, height = 0;
                        try
                        {
                            var widthParam = tb.LookupParameter("Sheet Width") ?? tb.get_Parameter(BuiltInParameter.SHEET_WIDTH);
                            var heightParam = tb.LookupParameter("Sheet Height") ?? tb.get_Parameter(BuiltInParameter.SHEET_HEIGHT);
                            if (widthParam != null) width = widthParam.AsDouble();
                            if (heightParam != null) height = heightParam.AsDouble();
                        }
                        catch { }

                        return new
                        {
                            titleBlockId = (int)tb.Id.Value,
                            familyName = tb.FamilyName ?? "",
                            typeName = tb.Name ?? "",
                            fullName = $"{tb.FamilyName}: {tb.Name}",
                            widthFeet = Math.Round(width, 4),
                            heightFeet = Math.Round(height, 4),
                            widthInches = Math.Round(width * 12, 2),
                            heightInches = Math.Round(height * 12, 2),
                            isActive = tb.IsActive
                        };
                    })
                    .OrderBy(tb => tb.familyName)
                    .ThenBy(tb => tb.typeName)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    titleBlockCount = titleBlocks.Count,
                    titleBlocks = titleBlocks
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the most commonly used titleblock in the project.
        /// This is what createSheet will auto-detect if no titleblockId is provided.
        /// Call this to see what titleblock will be used for new sheets.
        /// </summary>
        [MCPMethod("getPreferredTitleblock", Category = "Sheet", Description = "Get the most commonly used titleblock in the project")]
        public static string GetPreferredTitleblock(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all existing sheets and their titleblocks
                var existingSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .ToList();

                // Count titleblock usage
                var titleblockUsage = new Dictionary<ElementId, int>();
                var titleblockInfo = new Dictionary<ElementId, FamilySymbol>();

                foreach (var sheet in existingSheets)
                {
                    var titleblockInstance = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstOrDefault() as FamilyInstance;

                    if (titleblockInstance != null)
                    {
                        var symbolId = titleblockInstance.Symbol.Id;
                        if (titleblockUsage.ContainsKey(symbolId))
                            titleblockUsage[symbolId]++;
                        else
                        {
                            titleblockUsage[symbolId] = 1;
                            titleblockInfo[symbolId] = titleblockInstance.Symbol;
                        }
                    }
                }

                if (titleblockUsage.Any())
                {
                    var mostUsed = titleblockUsage.OrderByDescending(kv => kv.Value).First();
                    var titleblock = titleblockInfo[mostUsed.Key];

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        found = true,
                        titleblockId = (int)titleblock.Id.Value,
                        familyName = titleblock.FamilyName,
                        typeName = titleblock.Name,
                        fullName = $"{titleblock.FamilyName}: {titleblock.Name}",
                        usageCount = mostUsed.Value,
                        totalSheetsAnalyzed = existingSheets.Count,
                        message = $"Detected '{titleblock.FamilyName}: {titleblock.Name}' as the most used titleblock ({mostUsed.Value} of {existingSheets.Count} sheets)"
                    });
                }
                else
                {
                    // No sheets with titleblocks found - get first available
                    var firstAvailable = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();

                    if (firstAvailable != null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            found = true,
                            titleblockId = (int)firstAvailable.Id.Value,
                            familyName = firstAvailable.FamilyName,
                            typeName = firstAvailable.Name,
                            fullName = $"{firstAvailable.FamilyName}: {firstAvailable.Name}",
                            usageCount = 0,
                            totalSheetsAnalyzed = existingSheets.Count,
                            message = "No existing sheets with titleblocks found. Using first available titleblock.",
                            warning = "No reference sheets found - consider checking if existing sheets have titleblocks"
                        });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            found = false,
                            message = "No titleblocks found in project. Please load a titleblock family first."
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get complete sheet coordinate system with zones for intelligent view placement.
        /// Returns origin, bounds, 9-zone grid, printable area, and current viewport positions.
        /// Coordinates are in FEET (Revit internal units).
        ///
        /// Zone Layout (like a phone keypad):
        /// | 7-TL | 8-TC | 9-TR |
        /// | 4-ML | 5-MC | 6-MR |
        /// | 1-BL | 2-BC | 3-BR |
        ///
        /// Where: T=Top, M=Middle, B=Bottom, L=Left, C=Center, R=Right
        /// </summary>
        [MCPMethod("getSheetCoordinateSystem", Category = "Sheet", Description = "Get the coordinate system and zone grid for a sheet")]
        public static string GetSheetCoordinateSystem(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters?["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                }

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var sheet = doc.GetElement(sheetId) as ViewSheet;

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                // Get title block to determine sheet size
                var titleblockElement = new FilteredElementCollector(doc, sheetId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .FirstOrDefault() as FamilyInstance;

                double sheetWidth = 0, sheetHeight = 0;
                double minX = 0, minY = 0, maxX = 0, maxY = 0;

                if (titleblockElement != null)
                {
                    var bbox = titleblockElement.get_BoundingBox(sheet);
                    if (bbox != null)
                    {
                        minX = bbox.Min.X;
                        minY = bbox.Min.Y;
                        maxX = bbox.Max.X;
                        maxY = bbox.Max.Y;
                        sheetWidth = maxX - minX;
                        sheetHeight = maxY - minY;
                    }
                }

                // Define margins for printable area (conservative estimates)
                double marginLeft = 0.04;    // ~0.5 inch
                double marginRight = 0.04;   // ~0.5 inch
                double marginTop = 0.04;     // ~0.5 inch
                double marginBottom = 0.04;  // ~0.5 inch (title block info is part of the titleblock, not a margin)

                // Printable area bounds
                double printMinX = minX + marginLeft;
                double printMinY = minY + marginBottom;
                double printMaxX = maxX - marginRight;
                double printMaxY = maxY - marginTop;
                double printWidth = printMaxX - printMinX;
                double printHeight = printMaxY - printMinY;

                // Create 9-zone grid (3x3)
                double zoneWidth = printWidth / 3.0;
                double zoneHeight = printHeight / 3.0;

                var zones = new Dictionary<string, object>();
                string[] zoneNames = { "1-BL", "2-BC", "3-BR", "4-ML", "5-MC", "6-MR", "7-TL", "8-TC", "9-TR" };
                string[] zoneDescriptions = {
                    "Bottom-Left", "Bottom-Center", "Bottom-Right",
                    "Middle-Left", "Middle-Center", "Middle-Right",
                    "Top-Left", "Top-Center", "Top-Right"
                };

                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        int index = row * 3 + col;
                        double zMinX = printMinX + col * zoneWidth;
                        double zMaxX = zMinX + zoneWidth;
                        double zMinY = printMinY + row * zoneHeight;
                        double zMaxY = zMinY + zoneHeight;
                        double zCenterX = (zMinX + zMaxX) / 2.0;
                        double zCenterY = (zMinY + zMaxY) / 2.0;

                        zones[zoneNames[index]] = new
                        {
                            name = zoneNames[index],
                            description = zoneDescriptions[index],
                            minX = Math.Round(zMinX, 4),
                            minY = Math.Round(zMinY, 4),
                            maxX = Math.Round(zMaxX, 4),
                            maxY = Math.Round(zMaxY, 4),
                            centerX = Math.Round(zCenterX, 4),
                            centerY = Math.Round(zCenterY, 4),
                            widthFeet = Math.Round(zoneWidth, 4),
                            heightFeet = Math.Round(zoneHeight, 4),
                            widthInches = Math.Round(zoneWidth * 12, 2),
                            heightInches = Math.Round(zoneHeight * 12, 2)
                        };
                    }
                }

                // Get existing viewports
                var viewportIds = sheet.GetAllViewports();
                var existingViewports = new List<object>();

                foreach (var vpId in viewportIds)
                {
                    var viewport = doc.GetElement(vpId) as Viewport;
                    if (viewport != null)
                    {
                        var view = doc.GetElement(viewport.ViewId) as View;
                        var outline = viewport.GetBoxOutline();
                        var center = viewport.GetBoxCenter();

                        var vpMin = outline.MinimumPoint;
                        var vpMax = outline.MaximumPoint;
                        var vpWidth = vpMax.X - vpMin.X;
                        var vpHeight = vpMax.Y - vpMin.Y;

                        // Determine which zone(s) the viewport is in
                        var occupiedZones = new List<string>();
                        foreach (var zoneKV in zones)
                        {
                            dynamic zone = zoneKV.Value;
                            // Check if viewport overlaps with zone
                            if (vpMin.X < (double)zone.maxX && vpMax.X > (double)zone.minX &&
                                vpMin.Y < (double)zone.maxY && vpMax.Y > (double)zone.minY)
                            {
                                occupiedZones.Add(zoneKV.Key);
                            }
                        }

                        existingViewports.Add(new
                        {
                            viewportId = (int)viewport.Id.Value,
                            viewId = (int)viewport.ViewId.Value,
                            viewName = view?.Name ?? "",
                            viewScale = view?.Scale ?? 0,
                            center = new { x = Math.Round(center.X, 4), y = Math.Round(center.Y, 4) },
                            bounds = new
                            {
                                minX = Math.Round(vpMin.X, 4),
                                minY = Math.Round(vpMin.Y, 4),
                                maxX = Math.Round(vpMax.X, 4),
                                maxY = Math.Round(vpMax.Y, 4)
                            },
                            size = new
                            {
                                widthFeet = Math.Round(vpWidth, 4),
                                heightFeet = Math.Round(vpHeight, 4),
                                widthInches = Math.Round(vpWidth * 12, 2),
                                heightInches = Math.Round(vpHeight * 12, 2)
                            },
                            occupiedZones = occupiedZones,
                            isOnSheet = vpMin.X >= minX && vpMin.Y >= minY && vpMax.X <= maxX && vpMax.Y <= maxY
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = (int)sheetId.Value,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,

                    // Sheet bounds (origin at bottom-left)
                    sheet = new
                    {
                        origin = new { x = Math.Round(minX, 4), y = Math.Round(minY, 4) },
                        bounds = new
                        {
                            minX = Math.Round(minX, 4),
                            minY = Math.Round(minY, 4),
                            maxX = Math.Round(maxX, 4),
                            maxY = Math.Round(maxY, 4)
                        },
                        size = new
                        {
                            widthFeet = Math.Round(sheetWidth, 4),
                            heightFeet = Math.Round(sheetHeight, 4),
                            widthInches = Math.Round(sheetWidth * 12, 2),
                            heightInches = Math.Round(sheetHeight * 12, 2)
                        },
                        center = new
                        {
                            x = Math.Round((minX + maxX) / 2.0, 4),
                            y = Math.Round((minY + maxY) / 2.0, 4)
                        }
                    },

                    // Printable area (inside margins)
                    printableArea = new
                    {
                        bounds = new
                        {
                            minX = Math.Round(printMinX, 4),
                            minY = Math.Round(printMinY, 4),
                            maxX = Math.Round(printMaxX, 4),
                            maxY = Math.Round(printMaxY, 4)
                        },
                        size = new
                        {
                            widthFeet = Math.Round(printWidth, 4),
                            heightFeet = Math.Round(printHeight, 4),
                            widthInches = Math.Round(printWidth * 12, 2),
                            heightInches = Math.Round(printHeight * 12, 2)
                        },
                        center = new
                        {
                            x = Math.Round((printMinX + printMaxX) / 2.0, 4),
                            y = Math.Round((printMinY + printMaxY) / 2.0, 4)
                        }
                    },

                    // 9-zone grid for placement targeting
                    zones = zones,

                    // Existing viewports
                    viewports = existingViewports,
                    viewportCount = existingViewports.Count,

                    // Instructions for placement
                    instructions = new
                    {
                        coordinateSystem = "Origin (0,0) is at bottom-left corner of sheet. Coordinates are in FEET.",
                        placementMethod = "Use placeViewOnSheetSmart with targetZone OR targetCenter for accurate placement.",
                        zoneLayout = "Zones numbered like phone keypad: 1-3 bottom, 4-6 middle, 7-9 top. L=Left, C=Center, R=Right.",
                        viewportCenter = "Viewport placement uses CENTER point - the view extends equally in all directions from this center."
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Smart view placement on sheet with zone targeting and validation.
        /// Calculates correct center point to position view in target zone.
        ///
        /// Parameters:
        /// - sheetId (required): Target sheet ID
        /// - viewId (required): View to place
        /// - targetZone: Zone name (e.g., "5-MC" for middle-center) - uses zone center
        /// - targetCenter: Specific center point [x, y] in feet - overrides targetZone
        /// - validateOnly: If true, only validate without placing (default: false)
        ///
        /// The method calculates the view's size at scale, then determines if it fits
        /// in the target area before placing.
        /// </summary>
        [MCPMethod("placeViewOnSheetSmart", Category = "Sheet", Description = "Place a view on a sheet with smart collision avoidance")]
        public static string PlaceViewOnSheetSmart(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                // Validate required parameters
                if (parameters["sheetId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                if (parameters["viewId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var validateOnly = parameters["validateOnly"]?.Value<bool>() ?? false;

                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });

                // AUTO-SWITCH: Switch to the sheet before working on it (default: true)
                bool switchTo = parameters["switchTo"]?.Value<bool>() ?? true;
                if (switchTo && !validateOnly)
                {
                    SwitchToView(uiDoc, sheet);
                }

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });

                // Check if view can be placed
                if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
                {
                    string reason = "View cannot be placed on this sheet";
                    var existingVp = new FilteredElementCollector(doc)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .FirstOrDefault(vp => vp.ViewId == viewId);
                    if (existingVp != null)
                    {
                        var existingSheet = doc.GetElement(existingVp.SheetId) as ViewSheet;
                        reason = $"View already placed on sheet '{existingSheet?.SheetNumber}'";
                    }
                    return JsonConvert.SerializeObject(new { success = false, error = reason });
                }

                // Get sheet dimensions from title block
                var titleblockElement = new FilteredElementCollector(doc, sheetId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .FirstOrDefault() as FamilyInstance;

                double sheetMinX = 0, sheetMinY = 0, sheetMaxX = 0, sheetMaxY = 0;
                if (titleblockElement != null)
                {
                    var bbox = titleblockElement.get_BoundingBox(sheet);
                    if (bbox != null)
                    {
                        sheetMinX = bbox.Min.X;
                        sheetMinY = bbox.Min.Y;
                        sheetMaxX = bbox.Max.X;
                        sheetMaxY = bbox.Max.Y;
                    }
                }

                double sheetWidth = sheetMaxX - sheetMinX;
                double sheetHeight = sheetMaxY - sheetMinY;

                // Calculate printable area
                double margin = 0.04; // ~0.5 inch
                double printMinX = sheetMinX + margin;
                double printMinY = sheetMinY + margin;
                double printMaxX = sheetMaxX - margin;
                double printMaxY = sheetMaxY - margin;
                double printWidth = printMaxX - printMinX;
                double printHeight = printMaxY - printMinY;

                // Calculate view size at current scale
                // View dimensions on sheet = Model dimensions / scale
                double viewScale = view.Scale;
                BoundingBoxXYZ cropBox = view.CropBox;

                double viewModelWidth = 0, viewModelHeight = 0;
                if (cropBox != null)
                {
                    viewModelWidth = cropBox.Max.X - cropBox.Min.X;
                    viewModelHeight = cropBox.Max.Y - cropBox.Min.Y;
                }
                else
                {
                    // If no crop box, estimate from view outline
                    var viewOutline = view.Outline;
                    if (viewOutline != null)
                    {
                        viewModelWidth = viewOutline.Max.U - viewOutline.Min.U;
                        viewModelHeight = viewOutline.Max.V - viewOutline.Min.V;
                    }
                }

                // Size on sheet (in feet)
                double viewSheetWidth = viewModelWidth / viewScale;
                double viewSheetHeight = viewModelHeight / viewScale;

                // Determine target center point
                double targetX = 0, targetY = 0;
                string targetDescription = "";

                if (parameters["targetCenter"] != null)
                {
                    // Use specific coordinates
                    var coords = parameters["targetCenter"].ToObject<double[]>();
                    if (coords != null && coords.Length >= 2)
                    {
                        targetX = coords[0];
                        targetY = coords[1];
                        targetDescription = $"Custom center ({targetX:F3}, {targetY:F3})";
                    }
                }
                else if (parameters["targetZone"] != null)
                {
                    // Calculate zone center
                    string zoneName = parameters["targetZone"].ToString().ToUpper();
                    double zoneWidth = printWidth / 3.0;
                    double zoneHeight = printHeight / 3.0;

                    int col = 0, row = 0;
                    switch (zoneName)
                    {
                        case "1-BL": case "1": col = 0; row = 0; break;
                        case "2-BC": case "2": col = 1; row = 0; break;
                        case "3-BR": case "3": col = 2; row = 0; break;
                        case "4-ML": case "4": col = 0; row = 1; break;
                        case "5-MC": case "5": case "CENTER": col = 1; row = 1; break;
                        case "6-MR": case "6": col = 2; row = 1; break;
                        case "7-TL": case "7": col = 0; row = 2; break;
                        case "8-TC": case "8": col = 1; row = 2; break;
                        case "9-TR": case "9": col = 2; row = 2; break;
                        default:
                            // Default to center
                            col = 1; row = 1;
                            break;
                    }

                    double zMinX = printMinX + col * zoneWidth;
                    double zMinY = printMinY + row * zoneHeight;
                    targetX = zMinX + zoneWidth / 2.0;
                    targetY = zMinY + zoneHeight / 2.0;
                    targetDescription = $"Zone {zoneName}";
                }
                else
                {
                    // Default to printable area center
                    targetX = (printMinX + printMaxX) / 2.0;
                    targetY = (printMinY + printMaxY) / 2.0;
                    targetDescription = "Sheet center (default)";
                }

                // Calculate where viewport bounds will be when placed at target center
                double vpMinX = targetX - viewSheetWidth / 2.0;
                double vpMaxX = targetX + viewSheetWidth / 2.0;
                double vpMinY = targetY - viewSheetHeight / 2.0;
                double vpMaxY = targetY + viewSheetHeight / 2.0;

                // Validate: will viewport be on sheet?
                bool willFitOnSheet = vpMinX >= sheetMinX && vpMinY >= sheetMinY &&
                                     vpMaxX <= sheetMaxX && vpMaxY <= sheetMaxY;
                bool willFitInPrintArea = vpMinX >= printMinX && vpMinY >= printMinY &&
                                         vpMaxX <= printMaxX && vpMaxY <= printMaxY;

                // Calculate overflow amounts if any
                var overflow = new
                {
                    left = Math.Max(0, sheetMinX - vpMinX),
                    right = Math.Max(0, vpMaxX - sheetMaxX),
                    bottom = Math.Max(0, sheetMinY - vpMinY),
                    top = Math.Max(0, vpMaxY - sheetMaxY)
                };

                bool hasOverflow = overflow.left > 0 || overflow.right > 0 ||
                                  overflow.bottom > 0 || overflow.top > 0;

                // Build validation result
                var validation = new
                {
                    willFitOnSheet = willFitOnSheet,
                    willFitInPrintArea = willFitInPrintArea,
                    hasOverflow = hasOverflow,
                    overflow = overflow,
                    overflowInches = new
                    {
                        left = Math.Round(overflow.left * 12, 2),
                        right = Math.Round(overflow.right * 12, 2),
                        bottom = Math.Round(overflow.bottom * 12, 2),
                        top = Math.Round(overflow.top * 12, 2)
                    },
                    suggestion = hasOverflow ?
                        "View is too large for target location. Consider: 1) Increase view scale, 2) Adjust crop box, or 3) Use different target zone." :
                        "View fits within sheet bounds."
                };

                // If validate only, return without placing
                if (validateOnly)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        validateOnly = true,
                        sheetId = (int)sheetId.Value,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        viewScale = viewScale,
                        target = new
                        {
                            description = targetDescription,
                            centerX = Math.Round(targetX, 4),
                            centerY = Math.Round(targetY, 4)
                        },
                        predictedViewport = new
                        {
                            bounds = new
                            {
                                minX = Math.Round(vpMinX, 4),
                                minY = Math.Round(vpMinY, 4),
                                maxX = Math.Round(vpMaxX, 4),
                                maxY = Math.Round(vpMaxY, 4)
                            },
                            size = new
                            {
                                widthFeet = Math.Round(viewSheetWidth, 4),
                                heightFeet = Math.Round(viewSheetHeight, 4),
                                widthInches = Math.Round(viewSheetWidth * 12, 2),
                                heightInches = Math.Round(viewSheetHeight * 12, 2)
                            }
                        },
                        sheet = new
                        {
                            bounds = new { minX = sheetMinX, minY = sheetMinY, maxX = sheetMaxX, maxY = sheetMaxY },
                            widthInches = Math.Round(sheetWidth * 12, 2),
                            heightInches = Math.Round(sheetHeight * 12, 2)
                        },
                        validation = validation
                    });
                }

                // Actually place the viewport
                using (var trans = new Transaction(doc, "Smart Place View on Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Regenerate();

                    var point = new XYZ(targetX, targetY, 0);
                    Viewport viewport = null;

                    try
                    {
                        viewport = Viewport.Create(doc, sheetId, viewId, point);
                    }
                    catch (Exception createEx)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Viewport.Create failed: {createEx.Message}",
                            targetCenter = new { x = targetX, y = targetY }
                        });
                    }

                    if (viewport == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Viewport.Create returned null"
                        });
                    }

                    // Check if Revit placed it at the wrong location and correct it
                    var initialCenter = viewport.GetBoxCenter();
                    var targetPoint = new XYZ(targetX, targetY, 0);
                    bool positionCorrected = false;

                    // If position is off by more than 0.001 feet (about 0.01 inch), correct it
                    if (Math.Abs(initialCenter.X - targetX) > 0.001 || Math.Abs(initialCenter.Y - targetY) > 0.001)
                    {
                        // CRITICAL: Clear the "Saved Position" parameter first - this unlocks viewport movement
                        var savedPosParam = viewport.LookupParameter("Saved Position");
                        if (savedPosParam != null && !savedPosParam.IsReadOnly)
                        {
                            savedPosParam.Set(ElementId.InvalidElementId);
                        }

                        // Now SetBoxCenter will work
                        viewport.SetBoxCenter(targetPoint);
                        positionCorrected = true;
                    }

                    trans.Commit();

                    // Get actual placement results after correction
                    var actualOutline = viewport.GetBoxOutline();
                    var actualCenter = viewport.GetBoxCenter();

                    // Recalculate if viewport is now on sheet
                    var finalVpMinX = actualOutline.MinimumPoint.X;
                    var finalVpMaxX = actualOutline.MaximumPoint.X;
                    var finalVpMinY = actualOutline.MinimumPoint.Y;
                    var finalVpMaxY = actualOutline.MaximumPoint.Y;
                    bool actuallyOnSheet = finalVpMinX >= sheetMinX && finalVpMinY >= sheetMinY &&
                                          finalVpMaxX <= sheetMaxX && finalVpMaxY <= sheetMaxY;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = (int)viewport.Id.Value,
                        sheetId = (int)sheetId.Value,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        viewScale = viewScale,
                        target = new
                        {
                            description = targetDescription,
                            requestedCenter = new { x = Math.Round(targetX, 4), y = Math.Round(targetY, 4) }
                        },
                        actualPlacement = new
                        {
                            center = new
                            {
                                x = Math.Round(actualCenter.X, 4),
                                y = Math.Round(actualCenter.Y, 4)
                            },
                            bounds = new
                            {
                                minX = Math.Round(actualOutline.MinimumPoint.X, 4),
                                minY = Math.Round(actualOutline.MinimumPoint.Y, 4),
                                maxX = Math.Round(actualOutline.MaximumPoint.X, 4),
                                maxY = Math.Round(actualOutline.MaximumPoint.Y, 4)
                            },
                            size = new
                            {
                                widthInches = Math.Round((actualOutline.MaximumPoint.X - actualOutline.MinimumPoint.X) * 12, 2),
                                heightInches = Math.Round((actualOutline.MaximumPoint.Y - actualOutline.MinimumPoint.Y) * 12, 2)
                            }
                        },
                        positionCorrected = positionCorrected,
                        validation = validation,
                        isOnSheet = actuallyOnSheet
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place multiple views on a sheet with automatic grid layout.
        /// Views are arranged in a grid pattern to avoid overlapping.
        ///
        /// Parameters:
        /// - sheetId: Target sheet ID (required)
        /// - viewIds: Array of view IDs to place (required)
        /// - layout: Layout preset (optional, default "auto")
        ///     "auto" - Smart detection based on view count and type
        ///     "row" - Single horizontal row
        ///     "column" - Single vertical column
        ///     "grid-2x2" - 2 columns, 2 rows
        ///     "grid-2x3" - 2 columns, 3 rows (portrait-oriented)
        ///     "grid-3x2" - 3 columns, 2 rows (landscape-oriented)
        ///     "grid-3x3" - 3 columns, 3 rows
        ///     "grid-4x3" - 4 columns, 3 rows
        ///     "left-column" - Views stacked in left 1/3 of sheet
        ///     "right-column" - Views stacked in right 1/3 of sheet
        ///     "top-row" - Views in top 1/3 of sheet
        ///     "bottom-row" - Views in bottom 1/3 of sheet
        /// - columns: Override columns (optional)
        /// - margin: Space between viewports in feet (optional, default 0.08)
        /// - startPosition: "top-left" | "bottom-left" (optional, default "top-left")
        /// </summary>
        [MCPMethod("placeMultipleViewsOnSheet", Category = "Sheet", Description = "Place multiple views on a sheet with automatic layout")]
        public static string PlaceMultipleViewsOnSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                // Support either sheetId OR sheetNumber
                ElementId sheetId = null;
                ViewSheet sheet = null;

                if (parameters["sheetId"] != null)
                {
                    sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                    sheet = doc.GetElement(sheetId) as ViewSheet;
                }
                else if (parameters["sheetNumber"] != null)
                {
                    // Look up sheet by number
                    string sheetNumber = parameters["sheetNumber"].ToString();
                    var allSheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .ToList();

                    sheet = allSheets.FirstOrDefault(s =>
                        s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));

                    if (sheet != null)
                        sheetId = sheet.Id;
                }
                else
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Either sheetId or sheetNumber is required" });
                }

                if (sheet == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });

                // If no viewIds provided, auto-find unplaced views based on requested type
                int[] viewIdArray = null;
                string requestedViewType = parameters["viewType"]?.ToString()?.ToLower() ?? "drafting"; // Default to drafting/details

                if (parameters["viewIds"] != null)
                {
                    viewIdArray = parameters["viewIds"].ToObject<int[]>();
                }
                else
                {
                    // Get all placed view IDs to exclude
                    var placedViewIds = new FilteredElementCollector(doc)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .Select(vp => (int)vp.ViewId.Value)
                        .ToHashSet();

                    // SMART VIEW TYPE FILTERING - based on user's command
                    List<View> unplacedViews;
                    string viewTypeDescription;

                    switch (requestedViewType)
                    {
                        case "drafting":
                        case "detail":
                        case "details":
                            // Drafting/detail views only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewDrafting))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "drafting/detail";
                            break;

                        case "floorplan":
                        case "floor":
                        case "plan":
                        case "plans":
                            // Floor plans only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewPlan))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "floor plan";
                            break;

                        case "ceiling":
                        case "ceilingplan":
                        case "rcp":
                            // Ceiling plans only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewPlan))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && v.ViewType == ViewType.CeilingPlan && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "ceiling plan";
                            break;

                        case "3d":
                        case "threed":
                        case "3dview":
                        case "perspective":
                            // 3D views only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(View3D))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "3D";
                            break;

                        case "section":
                        case "sections":
                            // Sections only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewSection))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Section && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "section";
                            break;

                        case "elevation":
                        case "elevations":
                            // Elevations only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewSection))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "elevation";
                            break;

                        case "callout":
                        case "callouts":
                            // Callouts (detail views from model)
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(View))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Detail && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "callout/detail";
                            break;

                        case "legend":
                        case "legends":
                            // Legends only
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(View))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "legend";
                            break;

                        case "all":
                        case "any":
                            // All placeable views
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(View))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate &&
                                           v.ViewType != ViewType.DrawingSheet &&
                                           v.ViewType != ViewType.ProjectBrowser &&
                                           v.ViewType != ViewType.SystemBrowser &&
                                           v.ViewType != ViewType.Internal &&
                                           !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "any";
                            break;

                        default:
                            // Default to drafting views
                            unplacedViews = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewDrafting))
                                .Cast<View>()
                                .Where(v => !v.IsTemplate && !placedViewIds.Contains((int)v.Id.Value))
                                .ToList();
                            viewTypeDescription = "drafting/detail";
                            break;
                    }

                    if (unplacedViews.Count == 0)
                        return JsonConvert.SerializeObject(new { success = false, error = $"No unplaced {viewTypeDescription} views found" });

                    // FEATURE 1: Content-aware filtering by keywords
                    string contentFilter = parameters["contentFilter"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(contentFilter))
                    {
                        var keywords = contentFilter.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        unplacedViews = unplacedViews
                            .Where(v => keywords.Any(kw =>
                                v.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0))
                            .ToList();

                        if (unplacedViews.Count == 0)
                            return JsonConvert.SerializeObject(new {
                                success = false,
                                error = $"No unplaced {viewTypeDescription} views found matching '{contentFilter}'"
                            });
                    }

                    // FEATURE 2: Group by naming pattern (keeps STAIR 1, STAIR 2, STAIR 3 together)
                    unplacedViews = unplacedViews
                        .OrderBy(v => GetViewNamePrefix(v.Name))
                        .ThenBy(v => GetViewNameNumber(v.Name))
                        .ThenBy(v => v.Name)
                        .ToList();

                    // FEATURE 3: Scale matching - group same-scale views together
                    unplacedViews = unplacedViews
                        .OrderBy(v => v.Scale)
                        .ThenBy(v => GetViewNamePrefix(v.Name))
                        .ThenBy(v => GetViewNameNumber(v.Name))
                        .ToList();

                    viewIdArray = unplacedViews.Select(v => (int)v.Id.Value).ToArray();
                }

                if (viewIdArray == null || viewIdArray.Length == 0)
                    return JsonConvert.SerializeObject(new { success = false, error = "No views to place" });

                // Check for count limit - randomly select if count is specified
                int requestedCount = parameters["count"]?.Value<int>() ?? 0;

                // SMART DEFAULT: If no count specified, calculate reasonable number based on sheet size
                if (requestedCount <= 0)
                {
                    // Get sheet dimensions to estimate capacity
                    var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstOrDefault();

                    double sheetWidth = 36.0 / 12.0;  // Default 36"x24" (in feet)
                    double sheetHeight = 24.0 / 12.0;

                    if (titleBlock != null)
                    {
                        var bbox = titleBlock.get_BoundingBox(sheet);
                        if (bbox != null)
                        {
                            sheetWidth = bbox.Max.X - bbox.Min.X;
                            sheetHeight = bbox.Max.Y - bbox.Min.Y;
                        }
                    }

                    // Estimate: typical detail is about 6"x6" to 8"x8"
                    // Leave margins and calculate grid capacity
                    double usableWidth = sheetWidth * 0.85;  // 85% of width usable
                    double usableHeight = sheetHeight * 0.75; // 75% of height usable (title block takes bottom)
                    double avgDetailSize = 8.0 / 12.0; // Assume 8" average detail size

                    int estimatedCols = Math.Max(1, (int)(usableWidth / avgDetailSize));
                    int estimatedRows = Math.Max(1, (int)(usableHeight / avgDetailSize));
                    int smartCapacity = estimatedCols * estimatedRows;

                    // Cap at reasonable limits (6-12 typical for detail sheets)
                    requestedCount = Math.Min(smartCapacity, Math.Min(viewIdArray.Length, 12));
                    requestedCount = Math.Max(requestedCount, 1); // At least 1
                }

                if (requestedCount > 0 && requestedCount < viewIdArray.Length)
                {
                    // Randomly shuffle and take requested count
                    var random = new Random();
                    viewIdArray = viewIdArray
                        .OrderBy(x => random.Next())
                        .Take(requestedCount)
                        .ToArray();
                }

                // AUTO-SWITCH: Switch to the sheet before working on it (default: true)
                // This lets the user see what's being modified in real-time
                bool switchTo = parameters["switchTo"]?.Value<bool>() ?? true;
                if (switchTo)
                {
                    SwitchToView(uiDoc, sheet);
                }

                // Get optional parameters
                double marginBetweenViews = parameters["margin"]?.Value<double>() ?? 0.08; // 0.08 feet = ~1 inch between views
                double edgeMarginInches = parameters["edgeMarginInches"]?.Value<double>() ?? 1.0; // 1 inch from sheet edges
                string startPosition = parameters["startPosition"]?.ToString() ?? "top-left";
                string layoutPreset = parameters["layout"]?.ToString()?.ToLower() ?? "auto";
                int? requestedColumns = parameters["columns"]?.Value<int>();

                // FEATURE 5: Learning preferences - passed in from memory recall
                // These can be populated by Claude based on learned user patterns
                var preferences = parameters["preferences"] as JObject;
                if (preferences != null)
                {
                    // Override layout if user has a preferred layout for this sheet type
                    if (preferences["preferredLayout"] != null && layoutPreset == "auto")
                        layoutPreset = preferences["preferredLayout"].ToString().ToLower();

                    // Override margin if user prefers compact/spacious layouts
                    if (preferences["preferredSpacing"] != null)
                    {
                        string spacing = preferences["preferredSpacing"].ToString().ToLower();
                        if (spacing == "compact") marginBetweenViews = 0.05; // 0.6 inch
                        else if (spacing == "spacious") marginBetweenViews = 0.125; // 1.5 inch
                    }

                    // Override start position if user prefers different starting corner
                    if (preferences["preferredStartPosition"] != null)
                        startPosition = preferences["preferredStartPosition"].ToString().ToLower();
                }

                // SMART BOUNDARY DETECTION: Get printable area using titleblock, guide grid, or defaults
                // This ensures viewports stay within the usable sheet area
                var printableArea = GetSheetPrintableArea(doc, sheet, edgeMarginInches);

                // Full printable area from smart detection
                double fullPrintMinX = printableArea.MinX;
                double fullPrintMinY = printableArea.MinY;
                double fullPrintMaxX = printableArea.MaxX;
                double fullPrintMaxY = printableArea.MaxY;
                double fullPrintWidth = printableArea.Width;
                double fullPrintHeight = printableArea.Height;
                string boundarySource = printableArea.Source;

                // Layout area (may be subset of full printable area for partial layouts)
                double printMinX = fullPrintMinX;
                double printMinY = fullPrintMinY;
                double printMaxX = fullPrintMaxX;
                double printMaxY = fullPrintMaxY;
                double printWidth = fullPrintWidth;
                double printHeight = fullPrintHeight;

                // Calculate grid layout based on preset or auto-detection
                int viewCount = viewIdArray.Length;
                int columns, rows;
                string layoutUsed = layoutPreset;

                // FEATURE 4: Size-aware placement - calculate actual view dimensions
                double maxViewWidth = 8.0 / 12.0;   // Default 8 inches
                double maxViewHeight = 6.0 / 12.0;  // Default 6 inches
                double avgViewWidth = 0, avgViewHeight = 0;
                var viewSizes = new List<(int viewId, double width, double height)>();

                // Get actual dimensions of each view
                foreach (int vid in viewIdArray)
                {
                    var v = doc.GetElement(new ElementId(vid)) as View;
                    if (v != null)
                    {
                        var dims = GetViewDimensions(v);
                        viewSizes.Add((vid, dims.width, dims.height));
                        maxViewWidth = Math.Max(maxViewWidth, dims.width);
                        maxViewHeight = Math.Max(maxViewHeight, dims.height);
                        avgViewWidth += dims.width;
                        avgViewHeight += dims.height;
                    }
                }
                if (viewSizes.Count > 0)
                {
                    avgViewWidth /= viewSizes.Count;
                    avgViewHeight /= viewSizes.Count;
                }

                // Calculate how many views can fit based on actual sizes (plus margin)
                double viewCellWidth = maxViewWidth + marginBetweenViews;
                double viewCellHeight = maxViewHeight + marginBetweenViews;
                int maxPossibleCols = Math.Max(1, (int)(printWidth / viewCellWidth));
                int maxPossibleRows = Math.Max(1, (int)(printHeight / viewCellHeight));
                int sizeAwareCapacity = maxPossibleCols * maxPossibleRows;

                // Apply layout preset
                switch (layoutPreset)
                {
                    case "row":
                        // Single row - all views side by side
                        columns = viewCount;
                        rows = 1;
                        break;

                    case "column":
                        // Single column - all views stacked vertically
                        columns = 1;
                        rows = viewCount;
                        break;

                    case "grid-2x2":
                        columns = 2; rows = 2;
                        break;

                    case "grid-2x3":
                        columns = 2; rows = 3;
                        break;

                    case "grid-3x2":
                        columns = 3; rows = 2;
                        break;

                    case "grid-3x3":
                        columns = 3; rows = 3;
                        break;

                    case "grid-4x3":
                        columns = 4; rows = 3;
                        break;

                    case "grid-4x4":
                        columns = 4; rows = 4;
                        break;

                    case "left-column":
                        // Use left 1/3 of sheet, stack vertically
                        printMaxX = fullPrintMinX + fullPrintWidth / 3;
                        printWidth = printMaxX - printMinX;
                        columns = 1;
                        rows = viewCount;
                        break;

                    case "right-column":
                        // Use right 1/3 of sheet, stack vertically
                        printMinX = fullPrintMaxX - fullPrintWidth / 3;
                        printWidth = printMaxX - printMinX;
                        columns = 1;
                        rows = viewCount;
                        break;

                    case "top-row":
                        // Use top 1/3 of sheet, arrange horizontally
                        printMinY = fullPrintMaxY - fullPrintHeight / 3;
                        printHeight = printMaxY - printMinY;
                        columns = viewCount;
                        rows = 1;
                        break;

                    case "bottom-row":
                        // Use bottom 1/3 of sheet, arrange horizontally
                        printMaxY = fullPrintMinY + fullPrintHeight / 3;
                        printHeight = printMaxY - printMinY;
                        columns = viewCount;
                        rows = 1;
                        break;

                    case "auto":
                    default:
                        // SIZE-AWARE auto-detection based on actual view dimensions
                        // First, determine optimal columns based on actual view widths
                        columns = Math.Min(maxPossibleCols, viewCount);
                        rows = (int)Math.Ceiling((double)viewCount / columns);

                        // Validate that this fits, otherwise reduce columns
                        while (columns > 1 && rows > maxPossibleRows)
                        {
                            columns--;
                            rows = (int)Math.Ceiling((double)viewCount / columns);
                        }

                        // Generate layout name based on calculated grid
                        if (viewCount == 1) { layoutUsed = "single"; }
                        else if (rows == 1) { layoutUsed = $"row-{columns}"; }
                        else if (columns == 1) { layoutUsed = $"column-{rows}"; }
                        else { layoutUsed = $"grid-{columns}x{rows}-sizeaware"; }

                        // Log size-aware calculation details
                        System.Diagnostics.Debug.WriteLine($"[PlaceMultipleViews] Size-aware: maxView={maxViewWidth*12:F1}\"x{maxViewHeight*12:F1}\", " +
                            $"capacity={sizeAwareCapacity}, chosen={columns}x{rows}");
                        break;
                }

                // Allow manual override of columns
                if (requestedColumns.HasValue && requestedColumns.Value > 0)
                {
                    columns = requestedColumns.Value;
                    rows = (int)Math.Ceiling((double)viewCount / columns);
                    layoutUsed = $"custom-{columns}x{rows}";
                }

                // Calculate cell size with margins between cells
                double cellWidth = (printWidth - marginBetweenViews * (columns + 1)) / columns;
                double cellHeight = (printHeight - marginBetweenViews * (rows + 1)) / rows;

                // HONOR USER REQUEST: Don't reduce grid based on view sizes
                // User asked for N views, place N views. Views may overlap slightly
                // but Revit handles this gracefully and user can adjust if needed.
                // The capped GetViewDimensions ensures we have reasonable size estimates.

                // Only warn in debug if views are significantly larger than cells
                if (maxViewWidth > cellWidth * 1.5 || maxViewHeight > cellHeight * 1.5)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlaceMultipleViews] Warning: Views may overlap. " +
                        $"MaxView={maxViewWidth*12:F1}\"x{maxViewHeight*12:F1}\", Cell={cellWidth*12:F1}\"x{cellHeight*12:F1}\"");
                }

                // Place all views that were requested - don't artificially limit
                // The grid will accommodate what the user asked for

                // Track placement results
                var placements = new List<object>();
                var errors = new List<object>();

                using (var trans = new Transaction(doc, "Place Multiple Views on Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Regenerate();

                    for (int i = 0; i < viewIdArray.Length; i++)
                    {
                        var viewId = new ElementId(viewIdArray[i]);
                        var view = doc.GetElement(viewId) as View;

                        if (view == null)
                        {
                            errors.Add(new { index = i, viewId = viewIdArray[i], error = "View not found" });
                            continue;
                        }

                        // Check if view can be placed
                        if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
                        {
                            string reason = "Cannot place on sheet";
                            var existingVp = new FilteredElementCollector(doc)
                                .OfClass(typeof(Viewport))
                                .Cast<Viewport>()
                                .FirstOrDefault(vp => vp.ViewId == viewId);
                            if (existingVp != null)
                            {
                                var existingSheet = doc.GetElement(existingVp.SheetId) as ViewSheet;
                                reason = $"Already on sheet '{existingSheet?.SheetNumber}'";
                            }
                            errors.Add(new { index = i, viewId = viewIdArray[i], viewName = view.Name, error = reason });
                            continue;
                        }

                        // Calculate grid position (column, row)
                        int col = i % columns;
                        int row = i / columns;

                        // Calculate center point for this cell
                        double cellCenterX, cellCenterY;

                        if (startPosition == "bottom-left")
                        {
                            // Start from bottom-left, go right then up
                            cellCenterX = printMinX + marginBetweenViews + cellWidth / 2 + col * (cellWidth + marginBetweenViews);
                            cellCenterY = printMinY + marginBetweenViews + cellHeight / 2 + row * (cellHeight + marginBetweenViews);
                        }
                        else
                        {
                            // Start from top-left, go right then down (default)
                            cellCenterX = printMinX + marginBetweenViews + cellWidth / 2 + col * (cellWidth + marginBetweenViews);
                            cellCenterY = printMaxY - marginBetweenViews - cellHeight / 2 - row * (cellHeight + marginBetweenViews);
                        }

                        try
                        {
                            var point = new XYZ(cellCenterX, cellCenterY, 0);
                            var viewport = Viewport.Create(doc, sheetId, viewId, point);

                            if (viewport != null)
                            {
                                // Correct position if needed
                                var actualCenter = viewport.GetBoxCenter();
                                if (Math.Abs(actualCenter.X - cellCenterX) > 0.001 || Math.Abs(actualCenter.Y - cellCenterY) > 0.001)
                                {
                                    var savedPosParam = viewport.LookupParameter("Saved Position");
                                    if (savedPosParam != null && !savedPosParam.IsReadOnly)
                                    {
                                        savedPosParam.Set(ElementId.InvalidElementId);
                                    }
                                    viewport.SetBoxCenter(point);
                                }

                                placements.Add(new
                                {
                                    index = i,
                                    viewportId = (int)viewport.Id.Value,
                                    viewId = viewIdArray[i],
                                    viewName = view.Name,
                                    gridPosition = new { column = col, row = row },
                                    center = new { x = Math.Round(cellCenterX, 4), y = Math.Round(cellCenterY, 4) }
                                });
                            }
                            else
                            {
                                errors.Add(new { index = i, viewId = viewIdArray[i], viewName = view.Name, error = "Viewport.Create returned null" });
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new { index = i, viewId = viewIdArray[i], viewName = view.Name, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = placements.Count > 0,
                    sheetId = (int)sheetId.Value,
                    sheetNumber = sheet.SheetNumber,
                    totalRequested = viewIdArray.Length,
                    placedCount = placements.Count,
                    errorCount = errors.Count,
                    layout = new
                    {
                        preset = layoutUsed,
                        columns = columns,
                        rows = rows,
                        cellWidthInches = Math.Round(cellWidth * 12, 2),
                        cellHeightInches = Math.Round(cellHeight * 12, 2),
                        sizeAware = new
                        {
                            maxViewWidthInches = Math.Round(maxViewWidth * 12, 2),
                            maxViewHeightInches = Math.Round(maxViewHeight * 12, 2),
                            avgViewWidthInches = Math.Round(avgViewWidth * 12, 2),
                            avgViewHeightInches = Math.Round(avgViewHeight * 12, 2),
                            calculatedCapacity = sizeAwareCapacity
                        }
                    },
                    boundaryDetection = new
                    {
                        source = boundarySource, // "titleblock", "guidegrid", "inferred", "default"
                        edgeMarginInches = edgeMarginInches,
                        marginBetweenViewsInches = Math.Round(marginBetweenViews * 12, 2),
                        printableArea = new
                        {
                            minX = Math.Round(fullPrintMinX, 4),
                            minY = Math.Round(fullPrintMinY, 4),
                            maxX = Math.Round(fullPrintMaxX, 4),
                            maxY = Math.Round(fullPrintMaxY, 4),
                            widthInches = Math.Round(fullPrintWidth * 12, 2),
                            heightInches = Math.Round(fullPrintHeight * 12, 2)
                        }
                    },
                    availableLayouts = new[]
                    {
                        "auto", "row", "column",
                        "grid-2x2", "grid-2x3", "grid-3x2", "grid-3x3", "grid-4x3", "grid-4x4",
                        "left-column", "right-column", "top-row", "bottom-row"
                    },
                    placements = placements,
                    errors = errors.Count > 0 ? errors : null,
                    // FEATURE 5: Learning data - Claude should store successful patterns in memory
                    learnableOutcome = placements.Count > 0 ? new
                    {
                        success = true,
                        sheetType = sheet.SheetNumber.Substring(0, Math.Min(2, sheet.SheetNumber.Length)), // e.g., "A6" for detail sheets
                        viewCount = placements.Count,
                        effectiveLayout = layoutUsed,
                        effectiveColumns = columns,
                        effectiveRows = rows,
                        marginUsed = Math.Round(marginBetweenViews * 12, 2),
                        startPositionUsed = startPosition,
                        contentFilterUsed = parameters["contentFilter"]?.ToString() ?? "",
                        // Hints for Claude to store as preferences
                        suggestedMemory = $"For {sheet.SheetNumber.Substring(0, Math.Min(2, sheet.SheetNumber.Length))} sheets with {placements.Count} views, user's successful layout: {layoutUsed}, spacing: {Math.Round(marginBetweenViews * 12, 2)}in"
                    } : null
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Import an image (PNG, JPG, BMP, PDF) onto a sheet or view
        /// </summary>
        [MCPMethod("importImage", Category = "Sheet", Description = "Import an image onto a sheet or view")]
        public static string ImportImage(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Required parameters
                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required (sheet or view to place image on)"
                    });
                }

                if (parameters["imagePath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "imagePath is required (full path to image file)"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found with specified viewId"
                    });
                }

                var imagePath = parameters["imagePath"].ToString();
                if (!System.IO.File.Exists(imagePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Image file not found: {imagePath}"
                    });
                }

                // Optional position (defaults to center of view)
                double x = parameters["x"] != null ? double.Parse(parameters["x"].ToString()) : 0.35;
                double y = parameters["y"] != null ? double.Parse(parameters["y"].ToString()) : 0.85;

                using (var trans = new Transaction(doc, "Import Image"))
                {
                    trans.Start();

                    // Create ImageType from file
                    var imageTypeOptions = new ImageTypeOptions(imagePath, false, ImageTypeSource.Import);
                    var imageType = ImageType.Create(doc, imageTypeOptions);

                    // Create placement options
                    var placementOptions = new ImagePlacementOptions();
                    placementOptions.PlacementPoint = BoxPlacement.Center;
                    placementOptions.Location = new XYZ(x, y, 0);

                    // Place the image instance on the view
                    var imageInstance = ImageInstance.Create(doc, view, imageType.Id, placementOptions);

                    trans.Commit();

                    // Get image dimensions
                    var bbox = imageInstance.get_BoundingBox(view);
                    double widthFeet = bbox != null ? bbox.Max.X - bbox.Min.X : 0;
                    double heightFeet = bbox != null ? bbox.Max.Y - bbox.Min.Y : 0;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        imageInstanceId = (int)imageInstance.Id.Value,
                        imageTypeId = (int)imageType.Id.Value,
                        viewId = (int)view.Id.Value,
                        viewName = view.Name,
                        imagePath = imagePath,
                        position = new { x, y },
                        size = new
                        {
                            widthFeet,
                            heightFeet,
                            widthInches = Math.Round(widthFeet * 12, 2),
                            heightInches = Math.Round(heightFeet * 12, 2)
                        },
                        message = "Image imported successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move/resize an existing image instance on a sheet
        /// </summary>
        [MCPMethod("moveImage", Category = "Sheet", Description = "Move or resize an image on a sheet")]
        public static string MoveImage(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["imageId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "imageId is required"
                    });
                }

                var imageId = new ElementId(int.Parse(parameters["imageId"].ToString()));
                var imageInstance = doc.GetElement(imageId) as ImageInstance;
                if (imageInstance == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Image instance not found"
                    });
                }

                using (var trans = new Transaction(doc, "Move Image"))
                {
                    trans.Start();

                    // Move if coordinates provided
                    if (parameters["x"] != null && parameters["y"] != null)
                    {
                        double x = double.Parse(parameters["x"].ToString());
                        double y = double.Parse(parameters["y"].ToString());

                        var location = imageInstance.Location as LocationPoint;
                        if (location != null)
                        {
                            var currentPoint = location.Point;
                            var newPoint = new XYZ(x, y, currentPoint.Z);
                            location.Point = newPoint;
                        }
                    }

                    // Scale if width provided
                    if (parameters["widthInches"] != null)
                    {
                        double targetWidthInches = double.Parse(parameters["widthInches"].ToString());
                        double targetWidthFeet = targetWidthInches / 12.0;

                        var bbox = imageInstance.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            double currentWidth = bbox.Max.X - bbox.Min.X;
                            if (currentWidth > 0)
                            {
                                double scale = targetWidthFeet / currentWidth;
                                // Note: ImageInstance doesn't have direct scale - would need to recreate
                            }
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        imageId = (int)imageInstance.Id.Value,
                        message = "Image moved successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete an image from a sheet
        /// </summary>
        [MCPMethod("deleteImage", Category = "Sheet", Description = "Delete an image from a sheet or view")]
        public static string DeleteImage(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["imageId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "imageId is required"
                    });
                }

                var imageId = new ElementId(int.Parse(parameters["imageId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Image"))
                {
                    trans.Start();
                    doc.Delete(imageId);
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedImageId = (int)imageId.Value,
                        message = "Image deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy elements from one sheet/view to another at the same position
        /// This is useful for replicating layouts across multiple sheets
        /// </summary>
        [MCPMethod("copyElementsToSheet", Category = "Sheet", Description = "Copy elements from one sheet to another at the same position")]
        public static string CopyElementsToSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceViewId"] == null || parameters["targetViewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId and targetViewId are required"
                    });
                }

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var sourceViewId = new ElementId(int.Parse(parameters["sourceViewId"].ToString()));
                var targetViewId = new ElementId(int.Parse(parameters["targetViewId"].ToString()));
                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();

                var sourceView = doc.GetElement(sourceViewId) as View;
                var targetView = doc.GetElement(targetViewId) as View;

                if (sourceView == null || targetView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source or target view not found"
                    });
                }

                using (var trans = new Transaction(doc, "Copy Elements to Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Copy elements between views using the view-specific overload
                    // This preserves position relative to the view
                    var copiedIds = ElementTransformUtils.CopyElements(
                        sourceView,
                        elementIds,
                        targetView,
                        Transform.Identity,
                        new CopyPasteOptions());

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sourceViewId = (int)sourceViewId.Value,
                        targetViewId = (int)targetViewId.Value,
                        originalCount = elementIds.Count,
                        copiedCount = copiedIds.Count,
                        copiedIds = copiedIds.Select(id => (int)id.Value).ToArray()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set the text content of a text note
        /// </summary>
        [MCPMethod("setTextNoteText", Category = "Sheet", Description = "Set the text content of a text note element")]
        public static string SetTextNoteText(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["textNoteId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "textNoteId is required"
                    });
                }

                if (parameters["text"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "text is required"
                    });
                }

                var textNoteId = new ElementId(int.Parse(parameters["textNoteId"].ToString()));
                var newText = parameters["text"].ToString();

                var textNote = doc.GetElement(textNoteId) as TextNote;
                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "TextNote not found with ID: " + textNoteId.Value
                    });
                }

                var oldText = textNote.Text;

                using (var trans = new Transaction(doc, "Update TextNote Text"))
                {
                    trans.Start();
                    textNote.Text = newText;
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textNoteId = (int)textNoteId.Value,
                        oldText = oldText,
                        newText = newText
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Export sheets to PDF files
        /// </summary>
        [MCPMethod("exportSheetsToPDF", Category = "Sheet", Description = "Export sheets to PDF files")]
        public static string ExportSheetsToPDF(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var sheetIds = new List<ElementId>();
                if (parameters["sheetIds"] != null)
                {
                    foreach (var id in parameters["sheetIds"])
                    {
                        sheetIds.Add(new ElementId(int.Parse(id.ToString())));
                    }
                }
                else if (parameters["sheetId"] != null)
                {
                    sheetIds.Add(new ElementId(int.Parse(parameters["sheetId"].ToString())));
                }
                else
                {
                    // Export all sheets if none specified
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .OrderBy(s => s.SheetNumber)
                        .ToList();
                    sheetIds = sheets.Select(s => s.Id).ToList();
                }

                if (sheetIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No sheets to export"
                    });
                }

                // Output folder
                var outputFolder = parameters["outputFolder"]?.ToString();
                if (string.IsNullOrEmpty(outputFolder))
                {
                    outputFolder = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(doc.PathName) ?? System.IO.Path.GetTempPath(),
                        "PDF_Export"
                    );
                }

                // Create output folder if it doesn't exist
                if (!System.IO.Directory.Exists(outputFolder))
                {
                    System.IO.Directory.CreateDirectory(outputFolder);
                }

                var combineIntoOne = parameters["combineIntoOne"]?.ToObject<bool>() ?? false;
                var baseName = parameters["fileName"]?.ToString() ?? "Sheets";

                // Create PDF export options
                var pdfOptions = new PDFExportOptions();
                pdfOptions.FileName = baseName;
                pdfOptions.Combine = combineIntoOne;

                // Set paper size (use sheet size)
                pdfOptions.PaperFormat = ExportPaperFormat.Default;

                // Color options
                var colorMode = parameters["colorMode"]?.ToString()?.ToLower() ?? "color";
                switch (colorMode)
                {
                    case "blackwhite":
                    case "bw":
                        pdfOptions.ColorDepth = ColorDepthType.BlackLine;
                        break;
                    case "grayscale":
                    case "gray":
                        pdfOptions.ColorDepth = ColorDepthType.GrayScale;
                        break;
                    default:
                        pdfOptions.ColorDepth = ColorDepthType.Color;
                        break;
                }

                // Raster quality
                var rasterQuality = parameters["rasterQuality"]?.ToObject<int>() ?? 300;
                if (rasterQuality <= 72) pdfOptions.RasterQuality = RasterQualityType.Presentation;
                else if (rasterQuality <= 150) pdfOptions.RasterQuality = RasterQualityType.Medium;
                else pdfOptions.RasterQuality = RasterQualityType.High;

                // Hide scope boxes, crop boundaries, etc.
                pdfOptions.HideScopeBoxes = true;
                pdfOptions.HideCropBoundaries = true;
                pdfOptions.HideReferencePlane = true;
                pdfOptions.HideUnreferencedViewTags = true;
                pdfOptions.MaskCoincidentLines = true;
                pdfOptions.ViewLinksInBlue = false;
                pdfOptions.ReplaceHalftoneWithThinLines = false;

                // Zoom/Fit settings - fit content to page without margins
                var zoomType = parameters["zoomType"]?.ToString()?.ToLower() ?? "fittopage";
                if (zoomType == "zoom" || zoomType == "percentage")
                {
                    pdfOptions.ZoomType = ZoomType.Zoom;
                    pdfOptions.ZoomPercentage = parameters["zoomPercentage"]?.ToObject<int>() ?? 100;
                }
                else
                {
                    // Default: Fit to page - scales content to fill the page
                    pdfOptions.ZoomType = ZoomType.FitToPage;
                }

                // Paper placement - center the content on the page
                var paperPlacement = parameters["paperPlacement"]?.ToString()?.ToLower() ?? "center";
                switch (paperPlacement)
                {
                    case "lowerleft":
                        pdfOptions.PaperPlacement = PaperPlacementType.LowerLeft;
                        break;
                    case "margins":
                        pdfOptions.PaperPlacement = PaperPlacementType.Margins;
                        break;
                    default:
                        pdfOptions.PaperPlacement = PaperPlacementType.Center;
                        break;
                }

                // Paper format - ANSI A (8.5 x 11) Letter
                var paperFormat = parameters["paperFormat"]?.ToString()?.ToLower() ?? "letter";
                switch (paperFormat)
                {
                    case "letter":
                    case "ansia":
                        pdfOptions.PaperFormat = ExportPaperFormat.ANSI_A;
                        break;
                    case "legal":
                        pdfOptions.PaperFormat = ExportPaperFormat.ANSI_B;
                        break;
                    case "tabloid":
                    case "ansib":
                        pdfOptions.PaperFormat = ExportPaperFormat.ANSI_B;
                        break;
                    case "arch_d":
                    case "archd":
                        pdfOptions.PaperFormat = ExportPaperFormat.ARCH_D;
                        break;
                    default:
                        pdfOptions.PaperFormat = ExportPaperFormat.Default;
                        break;
                }

                // Export the PDFs
                var exportedFiles = new List<object>();
                var failedSheets = new List<object>();

                // Convert to List<ElementId> for the API
                var sheetIdList = sheetIds.ToList();

                // Export sheets
                doc.Export(outputFolder, sheetIdList, pdfOptions);

                // Scan the output folder for generated PDFs
                var pdfFiles = System.IO.Directory.GetFiles(outputFolder, "*.pdf")
                    .OrderBy(f => f)
                    .ToList();

                // Gather results
                foreach (var sheetId in sheetIds)
                {
                    var sheet = doc.GetElement(sheetId) as ViewSheet;
                    if (sheet != null)
                    {
                        // Look for any PDF that contains the sheet number
                        var matchingPdf = pdfFiles.FirstOrDefault(f =>
                            System.IO.Path.GetFileName(f).Contains(sheet.SheetNumber));

                        if (matchingPdf != null)
                        {
                            exportedFiles.Add(new
                            {
                                sheetId = sheetId.Value,
                                sheetNumber = sheet.SheetNumber,
                                sheetName = sheet.Name,
                                filePath = matchingPdf
                            });
                        }
                        else
                        {
                            failedSheets.Add(new
                            {
                                sheetId = sheetId.Value,
                                sheetNumber = sheet.SheetNumber,
                                sheetName = sheet.Name
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputFolder = outputFolder,
                    combine = combineIntoOne,
                    exportedCount = exportedFiles.Count,
                    failedCount = failedSheets.Count,
                    exportedFiles = exportedFiles,
                    failedSheets = failedSheets,
                    allPdfs = pdfFiles
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Analyze project standards - scans the entire project to detect formatting conventions.
        /// Returns: text styles, titleblocks, view organization, legends, schedules, and more.
        /// Use this at the start of any project to understand its established standards.
        /// </summary>
        [MCPMethod("analyzeProjectStandards", Category = "Sheet", Description = "Analyze project standards including text styles, titleblocks, and view organization")]
        public static string AnalyzeProjectStandards(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var projectName = doc.Title;
                var projectPath = doc.PathName;

                // ===== 1. TEXT STYLES ANALYSIS =====
                var textTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .ToList();

                var textStyleUsage = new Dictionary<ElementId, int>();
                var allTextNotes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .ToList();

                foreach (var note in allTextNotes)
                {
                    var typeId = note.GetTypeId();
                    if (textStyleUsage.ContainsKey(typeId))
                        textStyleUsage[typeId]++;
                    else
                        textStyleUsage[typeId] = 1;
                }

                var textStyles = textTypes.Select(tt => {
                    var textSizeParam = tt.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    double textSize = textSizeParam?.AsDouble() ?? 0;
                    var fontParam = tt.get_Parameter(BuiltInParameter.TEXT_FONT);
                    string font = fontParam?.AsString() ?? "Unknown";

                    int usage = textStyleUsage.ContainsKey(tt.Id) ? textStyleUsage[tt.Id] : 0;

                    return new
                    {
                        id = (int)tt.Id.Value,
                        name = tt.Name,
                        fontName = font,
                        textSizeFeet = Math.Round(textSize, 6),
                        textSizeInches = Math.Round(textSize * 12, 4),
                        textSizeFraction = GetFractionString(textSize * 12),
                        usageCount = usage
                    };
                })
                .OrderByDescending(t => t.usageCount)
                .ToList();

                // Identify primary text styles by size category
                var smallTextStyle = textStyles.Where(t => t.textSizeInches <= 0.1 && t.usageCount > 0).OrderByDescending(t => t.usageCount).FirstOrDefault();
                var mediumTextStyle = textStyles.Where(t => t.textSizeInches > 0.1 && t.textSizeInches <= 0.2 && t.usageCount > 0).OrderByDescending(t => t.usageCount).FirstOrDefault();
                var largeTextStyle = textStyles.Where(t => t.textSizeInches > 0.2 && t.usageCount > 0).OrderByDescending(t => t.usageCount).FirstOrDefault();

                // ===== 2. TITLEBLOCK ANALYSIS =====
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .ToList();

                var titleblockUsage = new Dictionary<ElementId, int>();
                foreach (var sheet in sheets)
                {
                    var tb = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstOrDefault() as FamilyInstance;
                    if (tb != null)
                    {
                        var symbolId = tb.Symbol.Id;
                        if (titleblockUsage.ContainsKey(symbolId))
                            titleblockUsage[symbolId]++;
                        else
                            titleblockUsage[symbolId] = 1;
                    }
                }

                var primaryTitleblock = titleblockUsage.OrderByDescending(kv => kv.Value).FirstOrDefault();
                FamilySymbol primaryTbSymbol = null;
                if (primaryTitleblock.Key != null)
                    primaryTbSymbol = doc.GetElement(primaryTitleblock.Key) as FamilySymbol;

                // ===== 3. LEGEND ANALYSIS =====
                var legends = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate)
                    .Select(v => new
                    {
                        id = (int)v.Id.Value,
                        name = v.Name,
                        scale = v.Scale
                    })
                    .ToList();

                // ===== 4. SCHEDULE ANALYSIS =====
                var schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule)
                    .Select(s => {
                        string category = "";
                        try { category = s.Definition.CategoryId != ElementId.InvalidElementId ?
                            Category.GetCategory(doc, s.Definition.CategoryId)?.Name ?? "Multi-Category" : "Multi-Category"; }
                        catch { category = "Unknown"; }

                        return new
                        {
                            id = (int)s.Id.Value,
                            name = s.Name,
                            category = category
                        };
                    })
                    .ToList();

                // ===== 5. VIEW ORGANIZATION ANALYSIS =====
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                var viewsByType = allViews
                    .GroupBy(v => v.ViewType)
                    .Select(g => new { viewType = g.Key.ToString(), count = g.Count() })
                    .OrderByDescending(g => g.count)
                    .ToList();

                // ===== 6. SHEET NUMBERING PATTERN ANALYSIS =====
                var sheetNumbers = sheets.Select(s => s.SheetNumber).OrderBy(n => n).ToList();
                string numberingPattern = "Unknown";
                if (sheetNumbers.Any())
                {
                    var first = sheetNumbers.First();
                    if (first.Contains("."))
                        numberingPattern = "Decimal (A1.0, A1.1)";
                    else if (first.Contains("-"))
                        numberingPattern = "Hyphenated (A-1.0, A-1.1)";
                    else
                        numberingPattern = "Sequential (A101, A102)";
                }

                // ===== 7. DIMENSION STYLES =====
                var dimTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .Select(dt => new
                    {
                        id = (int)dt.Id.Value,
                        name = dt.Name
                    })
                    .ToList();

                // ===== 8. VIEW TEMPLATES =====
                var viewTemplates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.IsTemplate)
                    .Select(v => new
                    {
                        id = (int)v.Id.Value,
                        name = v.Name,
                        viewType = v.ViewType.ToString()
                    })
                    .ToList();

                // ===== BUILD RESPONSE =====
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectInfo = new
                    {
                        name = projectName,
                        path = projectPath,
                        sheetCount = sheets.Count,
                        viewCount = allViews.Count,
                        legendCount = legends.Count,
                        scheduleCount = schedules.Count
                    },
                    textStandards = new
                    {
                        allStyles = textStyles,
                        recommended = new
                        {
                            smallText = smallTextStyle != null ? new { smallTextStyle.id, smallTextStyle.name, smallTextStyle.textSizeFraction, smallTextStyle.fontName } : null,
                            mediumText = mediumTextStyle != null ? new { mediumTextStyle.id, mediumTextStyle.name, mediumTextStyle.textSizeFraction, mediumTextStyle.fontName } : null,
                            largeText = largeTextStyle != null ? new { largeTextStyle.id, largeTextStyle.name, largeTextStyle.textSizeFraction, largeTextStyle.fontName } : null
                        },
                        primaryFont = textStyles.Where(t => t.usageCount > 0).GroupBy(t => t.fontName).OrderByDescending(g => g.Sum(t => t.usageCount)).FirstOrDefault()?.Key ?? "Unknown"
                    },
                    sheetStandards = new
                    {
                        primaryTitleblock = primaryTbSymbol != null ? new
                        {
                            id = (int)primaryTbSymbol.Id.Value,
                            familyName = primaryTbSymbol.FamilyName,
                            typeName = primaryTbSymbol.Name,
                            usedBySheets = primaryTitleblock.Value
                        } : null,
                        numberingPattern = numberingPattern,
                        existingSheetNumbers = sheetNumbers.Take(10).ToList(),
                        totalSheets = sheets.Count
                    },
                    legends = legends,
                    schedules = schedules.Take(20).ToList(),
                    viewOrganization = new
                    {
                        byType = viewsByType,
                        templates = viewTemplates
                    },
                    dimensionTypes = dimTypes.Take(10).ToList(),
                    instructions = new
                    {
                        textPlacement = "Use the recommended text styles based on usage frequency. Small for notes (3/32\"), Medium for labels (3/16\"), Large for titles (1/4\").",
                        sheetCreation = "Use createSheetAuto to match existing titleblock. Follow the detected numbering pattern.",
                        consistency = "Always match the primary font and text sizes used in this project."
                    }
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper to convert decimal inches to fraction string
        private static string GetFractionString(double inches)
        {
            if (Math.Abs(inches - 0.09375) < 0.001) return "3/32\"";
            if (Math.Abs(inches - 0.0625) < 0.001) return "1/16\"";
            if (Math.Abs(inches - 0.125) < 0.001) return "1/8\"";
            if (Math.Abs(inches - 0.1875) < 0.001) return "3/16\"";
            if (Math.Abs(inches - 0.25) < 0.001) return "1/4\"";
            if (Math.Abs(inches - 0.3125) < 0.001) return "5/16\"";
            if (Math.Abs(inches - 0.375) < 0.001) return "3/8\"";
            if (Math.Abs(inches - 0.5) < 0.001) return "1/2\"";
            return $"{Math.Round(inches, 3)}\"";
        }

        /// <summary>
        /// Create a sheet automatically using the same titleblock as existing sheets.
        /// Detects the most commonly used titleblock in the project and uses that.
        /// Parameters:
        /// - sheetNumber (required): The sheet number (e.g., "A15.0")
        /// - sheetName (required): The sheet name (e.g., "FLOOR PLAN")
        /// </summary>
        [MCPMethod("createSheetAuto", Category = "Sheet", Description = "Create a sheet using the project's existing titleblock automatically")]
        public static string CreateSheetAuto(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetNumber = parameters["sheetNumber"]?.ToString();
                var sheetName = parameters["sheetName"]?.ToString();

                if (string.IsNullOrEmpty(sheetNumber))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sheetNumber is required"
                    });
                }

                if (string.IsNullOrEmpty(sheetName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sheetName is required"
                    });
                }

                // Get all existing sheets and their titleblocks
                var existingSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .ToList();

                // Find titleblock instances and count usage
                var titleblockUsage = new Dictionary<ElementId, int>();
                var titleblockInfo = new Dictionary<ElementId, FamilySymbol>();

                foreach (var sheet in existingSheets)
                {
                    var titleblockInstance = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstOrDefault() as FamilyInstance;

                    if (titleblockInstance != null)
                    {
                        var symbolId = titleblockInstance.Symbol.Id;
                        if (titleblockUsage.ContainsKey(symbolId))
                        {
                            titleblockUsage[symbolId]++;
                        }
                        else
                        {
                            titleblockUsage[symbolId] = 1;
                            titleblockInfo[symbolId] = titleblockInstance.Symbol;
                        }
                    }
                }

                // Find the most commonly used titleblock
                FamilySymbol selectedTitleblock = null;
                int maxUsage = 0;

                foreach (var kvp in titleblockUsage)
                {
                    if (kvp.Value > maxUsage)
                    {
                        maxUsage = kvp.Value;
                        selectedTitleblock = titleblockInfo[kvp.Key];
                    }
                }

                // If no titleblocks found in existing sheets, get first available
                if (selectedTitleblock == null)
                {
                    selectedTitleblock = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();
                }

                if (selectedTitleblock == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No titleblock found in project"
                    });
                }

                using (var trans = new Transaction(doc, "Create Sheet (Auto)"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate titleblock if needed
                    if (!selectedTitleblock.IsActive)
                    {
                        selectedTitleblock.Activate();
                        doc.Regenerate();
                    }

                    var sheet = ViewSheet.Create(doc, selectedTitleblock.Id);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;

                    trans.Commit();

                    // Get sheet dimensions
                    var titleblockInstance = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstOrDefault() as FamilyInstance;

                    double sheetWidth = 0, sheetHeight = 0;
                    if (titleblockInstance != null)
                    {
                        var bbox = titleblockInstance.get_BoundingBox(sheet);
                        if (bbox != null)
                        {
                            sheetWidth = bbox.Max.X - bbox.Min.X;
                            sheetHeight = bbox.Max.Y - bbox.Min.Y;
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sheetId = (int)sheet.Id.Value,
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        titleblock = new
                        {
                            id = (int)selectedTitleblock.Id.Value,
                            familyName = selectedTitleblock.FamilyName,
                            typeName = selectedTitleblock.Name,
                            usedBy = maxUsage > 0 ? $"{maxUsage} other sheets" : "default (no existing sheets)"
                        },
                        sheetSize = new
                        {
                            widthFeet = Math.Round(sheetWidth, 4),
                            heightFeet = Math.Round(sheetHeight, 4),
                            widthInches = Math.Round(sheetWidth * 12, 2),
                            heightInches = Math.Round(sheetHeight * 12, 2)
                        },
                        message = "Sheet created with auto-detected titleblock"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// SMART BATCH SHEET CREATION - Auto-detects numbering patterns and creates multiple sheets intelligently
        /// This bypasses the LLM for fast sheet creation with intelligent defaults.
        /// Parameters:
        /// - count (optional): Number of sheets to create (default 1)
        /// - discipline (optional): Discipline letter (A, S, M, E, P, etc.)
        /// - rangeStart/rangeEnd (optional): Specific number range (e.g., 601-605)
        /// - sheetType (optional): Sheet type name (e.g., "DETAILS", "FLOOR PLAN")
        /// - sheetName (optional): Custom sheet name override
        /// </summary>
        [MCPMethod("createSheetsIntelligent", Category = "Sheet", Description = "Intelligently create sheets using project standards and numbering patterns")]
        public static string CreateSheetsIntelligent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters with robust null handling
                int count = 1;
                if (parameters["count"] != null && parameters["count"].Type != JTokenType.Null)
                    int.TryParse(parameters["count"].ToString(), out count);
                if (count < 1) count = 1;

                string discipline = parameters["discipline"]?.ToString()?.ToUpper() ?? "";

                int? rangeStart = null;
                if (parameters["rangeStart"] != null && parameters["rangeStart"].Type != JTokenType.Null)
                {
                    if (int.TryParse(parameters["rangeStart"].ToString(), out int rs))
                        rangeStart = rs;
                }

                int? rangeEnd = null;
                if (parameters["rangeEnd"] != null && parameters["rangeEnd"].Type != JTokenType.Null)
                {
                    if (int.TryParse(parameters["rangeEnd"].ToString(), out int re))
                        rangeEnd = re;
                }

                string sheetType = parameters["sheetType"]?.ToString() ?? "";
                string customName = parameters["sheetName"]?.ToString();

                // Get all existing sheets for pattern analysis
                var existingSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .ToList();

                // PATTERN ANALYSIS: Analyze existing sheet numbering
                var sheetsByDiscipline = new Dictionary<string, List<(string number, int numericPart, string format)>>();
                foreach (var sheet in existingSheets)
                {
                    var match = Regex.Match(sheet.SheetNumber, @"^([A-Za-z]+)[-.]?(\d+)(?:[-.](\d+))?");
                    if (match.Success)
                    {
                        string disc = match.Groups[1].Value.ToUpper();
                        int mainNum = int.Parse(match.Groups[2].Value);
                        string format = sheet.SheetNumber; // Keep format for pattern detection

                        if (!sheetsByDiscipline.ContainsKey(disc))
                            sheetsByDiscipline[disc] = new List<(string, int, string)>();
                        sheetsByDiscipline[disc].Add((sheet.SheetNumber, mainNum, format));
                    }
                }

                // If no discipline specified, default to "A" (Architectural)
                if (string.IsNullOrEmpty(discipline))
                    discipline = "A";

                // Detect numbering pattern for this discipline
                string numberFormat = "{0}-{1:D3}"; // Default: A-001
                int nextNumber = 101; // Default starting number
                string separator = "-";

                if (sheetsByDiscipline.ContainsKey(discipline) && sheetsByDiscipline[discipline].Count > 0)
                {
                    var disciplineSheets = sheetsByDiscipline[discipline].OrderBy(s => s.numericPart).ToList();
                    var lastSheet = disciplineSheets.Last();

                    // Detect separator (- or .)
                    if (lastSheet.format.Contains("."))
                        separator = ".";

                    // Detect number format (padding)
                    var numMatch = Regex.Match(lastSheet.format, @"(\d+)(?:[-.](\d+))?$");
                    if (numMatch.Success)
                    {
                        string numPart = numMatch.Groups[1].Value;
                        int padLength = numPart.Length;
                        nextNumber = int.Parse(numPart) + 1;

                        // Check for sub-numbers (e.g., A-6.01)
                        if (numMatch.Groups[2].Success)
                        {
                            // Has sub-number format like A-6.01
                            int mainPart = int.Parse(numMatch.Groups[1].Value);
                            int subPart = int.Parse(numMatch.Groups[2].Value) + 1;
                            numberFormat = $"{{0}}{separator}{mainPart}{separator}{{1:D{numMatch.Groups[2].Value.Length}}}";
                            nextNumber = subPart;
                        }
                        else
                        {
                            numberFormat = $"{{0}}{separator}{{1:D{padLength}}}";
                        }
                    }
                }

                // Use range if specified
                if (rangeStart.HasValue)
                    nextNumber = rangeStart.Value;

                // Find most commonly used titleblock
                var titleblockUsage = new Dictionary<ElementId, int>();
                var titleblockInfo = new Dictionary<ElementId, FamilySymbol>();

                foreach (var sheet in existingSheets)
                {
                    var titleblockInstance = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .FirstOrDefault() as FamilyInstance;

                    if (titleblockInstance != null)
                    {
                        var symbolId = titleblockInstance.Symbol.Id;
                        if (titleblockUsage.ContainsKey(symbolId))
                            titleblockUsage[symbolId]++;
                        else
                        {
                            titleblockUsage[symbolId] = 1;
                            titleblockInfo[symbolId] = titleblockInstance.Symbol;
                        }
                    }
                }

                FamilySymbol selectedTitleblock = null;
                if (titleblockUsage.Count > 0)
                {
                    var mostUsed = titleblockUsage.OrderByDescending(kvp => kvp.Value).First();
                    selectedTitleblock = titleblockInfo[mostUsed.Key];
                }
                else
                {
                    selectedTitleblock = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();
                }

                if (selectedTitleblock == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No titleblock found in project"
                    });
                }

                // Create sheets
                var createdSheets = new List<object>();
                var errors = new List<object>();

                using (var trans = new Transaction(doc, $"Create {count} Sheets"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!selectedTitleblock.IsActive)
                    {
                        selectedTitleblock.Activate();
                        doc.Regenerate();
                    }

                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            string sheetNumber = string.Format(numberFormat, discipline, nextNumber + i);

                            // Check if sheet number already exists
                            if (existingSheets.Any(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase)))
                            {
                                // Skip to next available number
                                int skipCount = 0;
                                while (existingSheets.Any(s => s.SheetNumber.Equals(
                                    string.Format(numberFormat, discipline, nextNumber + i + skipCount),
                                    StringComparison.OrdinalIgnoreCase)) && skipCount < 100)
                                {
                                    skipCount++;
                                }
                                sheetNumber = string.Format(numberFormat, discipline, nextNumber + i + skipCount);
                            }

                            // Determine sheet name
                            string sheetName = customName;
                            if (string.IsNullOrEmpty(sheetName))
                            {
                                if (!string.IsNullOrEmpty(sheetType))
                                    sheetName = count > 1 ? $"{sheetType} {i + 1}" : sheetType;
                                else
                                    sheetName = "NEW SHEET";
                            }

                            var sheet = ViewSheet.Create(doc, selectedTitleblock.Id);
                            sheet.SheetNumber = sheetNumber;
                            sheet.Name = sheetName;

                            createdSheets.Add(new
                            {
                                sheetId = (int)sheet.Id.Value,
                                sheetNumber = sheet.SheetNumber,
                                sheetName = sheet.Name
                            });
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new { index = i, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = createdSheets.Count > 0,
                    createdCount = createdSheets.Count,
                    requestedCount = count,
                    discipline = discipline,
                    detectedPattern = numberFormat.Replace("{0}", discipline).Replace("{1:D3}", "###"),
                    titleblock = selectedTitleblock.FamilyName + ": " + selectedTitleblock.Name,
                    sheets = createdSheets,
                    errors = errors.Count > 0 ? errors : null,
                    message = $"Created {createdSheets.Count} {discipline} sheet(s)"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch operation: Create multiple sheets AND add details to each one.
        /// Compound command support for "create 5 A sheets and add 6 details on each"
        /// Parameters:
        /// - sheetCount: Number of sheets to create
        /// - discipline: Sheet discipline letter (A, S, M, E, P, etc.)
        /// - rangeStart: Starting number for sheet numbering
        /// - detailsPerSheet: Number of details to place on each sheet
        /// - layout: Layout style for details (auto, grid, column, row)
        /// </summary>
        [MCPMethod("batchCreateSheetsWithDetails", Category = "Sheet", Description = "Batch create sheets with pre-placed detail views")]
        public static string BatchCreateSheetsWithDetails(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                int sheetCount = parameters["sheetCount"]?.Value<int>() ?? 1;
                string discipline = parameters["discipline"]?.ToString() ?? "A";
                int rangeStart = parameters["rangeStart"]?.Value<int>() ?? 1;
                int detailsPerSheet = parameters["detailsPerSheet"]?.Value<int>() ?? 6;
                string layout = parameters["layout"]?.ToString() ?? "auto";
                string viewType = parameters["viewType"]?.ToString() ?? "drafting"; // SMART: View type filter

                var results = new List<object>();
                var errors = new List<object>();

                // Step 1: Create the sheets using CreateSheetsIntelligent
                var createParams = JObject.FromObject(new {
                    count = sheetCount,
                    discipline = discipline,
                    rangeStart = rangeStart
                });

                string createResult = CreateSheetsIntelligent(uiApp, createParams);
                var createResponse = JObject.Parse(createResult);

                if (createResponse["success"]?.Value<bool>() != true)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Failed to create sheets: " + (createResponse["error"]?.ToString() ?? "Unknown error")
                    });
                }

                var createdSheets = createResponse["sheets"] as JArray;
                if (createdSheets == null || createdSheets.Count == 0)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "No sheets were created"
                    });
                }

                // Step 2: Add details to each created sheet
                foreach (var sheet in createdSheets)
                {
                    string sheetNumber = sheet["sheetNumber"]?.ToString();
                    int sheetId = sheet["sheetId"]?.Value<int>() ?? 0;

                    if (string.IsNullOrEmpty(sheetNumber)) continue;

                    try
                    {
                        var placeParams = JObject.FromObject(new {
                            sheetNumber = sheetNumber,
                            count = detailsPerSheet,
                            layout = layout,
                            viewType = viewType  // SMART: Pass view type for filtering
                        });

                        string placeResult = PlaceMultipleViewsOnSheet(uiApp, placeParams);
                        var placeResponse = JObject.Parse(placeResult);

                        results.Add(new {
                            sheetNumber = sheetNumber,
                            sheetId = sheetId,
                            detailsPlaced = placeResponse["placedCount"]?.Value<int>() ?? 0,
                            success = placeResponse["success"]?.Value<bool>() ?? false,
                            message = placeResponse["message"]?.ToString()
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new {
                            sheetNumber = sheetNumber,
                            error = ex.Message
                        });
                    }
                }

                int totalSheetsCreated = createdSheets.Count;
                int totalDetailsPlaced = results.Sum(r => ((dynamic)r).detailsPlaced);

                return JsonConvert.SerializeObject(new {
                    success = true,
                    sheetsCreated = totalSheetsCreated,
                    totalDetailsPlaced = totalDetailsPlaced,
                    detailsPerSheet = detailsPerSheet,
                    discipline = discipline,
                    results = results,
                    errors = errors.Count > 0 ? errors : null,
                    message = $"Created {totalSheetsCreated} {discipline} sheets with {totalDetailsPlaced} total details placed"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change/swap the titleblock on an existing sheet
        /// Parameters:
        /// - sheetId (required): The sheet to modify
        /// - titleBlockId (required): The new titleblock type ID to use
        /// </summary>
        [MCPMethod("changeTitleBlock", Category = "Sheet", Description = "Change the titleblock on an existing sheet")]
        public static string ChangeTitleBlock(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sheetId is required"
                    });
                }

                if (parameters["titleBlockId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "titleBlockId is required"
                    });
                }

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var newTitleBlockId = new ElementId(int.Parse(parameters["titleBlockId"].ToString()));

                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Sheet not found"
                    });
                }

                // Verify the new titleblock type exists
                var newTitleBlockType = doc.GetElement(newTitleBlockId) as FamilySymbol;
                if (newTitleBlockType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "TitleBlock type not found with specified ID"
                    });
                }

                // Find the existing titleblock instance on the sheet
                var titleblockInstance = new FilteredElementCollector(doc, sheetId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .FirstOrDefault() as FamilyInstance;

                if (titleblockInstance == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No titleblock found on sheet"
                    });
                }

                var oldTitleBlockId = titleblockInstance.Symbol.Id;
                var oldTitleBlockName = titleblockInstance.Symbol.FamilyName + ": " + titleblockInstance.Symbol.Name;

                using (var trans = new Transaction(doc, "Change TitleBlock"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the new titleblock type if not already active
                    if (!newTitleBlockType.IsActive)
                    {
                        newTitleBlockType.Activate();
                        doc.Regenerate();
                    }

                    // Change the titleblock type
                    titleblockInstance.Symbol = newTitleBlockType;

                    trans.Commit();

                    // Get new sheet dimensions
                    var bbox = titleblockInstance.get_BoundingBox(sheet);
                    double newWidth = 0, newHeight = 0;
                    if (bbox != null)
                    {
                        newWidth = bbox.Max.X - bbox.Min.X;
                        newHeight = bbox.Max.Y - bbox.Min.Y;
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sheetId = (int)sheetId.Value,
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        oldTitleBlockId = (int)oldTitleBlockId.Value,
                        oldTitleBlockName = oldTitleBlockName,
                        newTitleBlockId = (int)newTitleBlockId.Value,
                        newTitleBlockName = newTitleBlockType.FamilyName + ": " + newTitleBlockType.Name,
                        newSheetSize = new
                        {
                            widthFeet = Math.Round(newWidth, 4),
                            heightFeet = Math.Round(newHeight, 4),
                            widthInches = Math.Round(newWidth * 12, 2),
                            heightInches = Math.Round(newHeight * 12, 2)
                        },
                        message = "TitleBlock changed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Auto-populate sheet titleblock fields from project information.
        /// </summary>
        [MCPMethod("autoPopulateSheetFields", Category = "Sheet", Description = "Auto-populate titleblock fields from project information")]
        public static string AutoPopulateSheetFields(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sheetIds = parameters?["sheetIds"]?.ToObject<List<int>>();
                var fieldMappings = parameters?["fieldMappings"]?.ToObject<Dictionary<string, string>>();

                // Get project info
                var projectInfo = doc.ProjectInformation;
                var projectData = new Dictionary<string, string>
                {
                    { "ProjectName", projectInfo.Name ?? "" },
                    { "ProjectNumber", projectInfo.Number ?? "" },
                    { "ProjectAddress", projectInfo.Address ?? "" },
                    { "ClientName", projectInfo.ClientName ?? "" },
                    { "BuildingName", projectInfo.BuildingName ?? "" },
                    { "Author", projectInfo.Author ?? "" },
                    { "OrganizationName", projectInfo.OrganizationName ?? "" },
                    { "OrganizationDescription", projectInfo.OrganizationDescription ?? "" },
                    { "IssueDate", projectInfo.IssueDate ?? DateTime.Now.ToString("MM/dd/yyyy") },
                    { "Status", projectInfo.Status ?? "" }
                };

                // Get sheets to process
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => sheetIds == null || sheetIds.Count == 0 || sheetIds.Contains((int)s.Id.Value))
                    .ToList();

                int updatedCount = 0;
                int parameterUpdates = 0;
                var results = new List<object>();

                using (var trans = new Transaction(doc, "Auto Populate Sheet Fields"))
                {
                    trans.Start();

                    foreach (var sheet in sheets)
                    {
                        var sheetUpdates = new List<string>();

                        // Get titleblock on this sheet
                        var titleblock = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .FirstOrDefault() as FamilyInstance;

                        if (titleblock == null) continue;

                        // Default field mappings if not provided
                        var mappings = fieldMappings ?? new Dictionary<string, string>
                        {
                            { "Project Name", "ProjectName" },
                            { "Project Number", "ProjectNumber" },
                            { "Project Address", "ProjectAddress" },
                            { "Client Name", "ClientName" },
                            { "Drawn By", "Author" },
                            { "Issue Date", "IssueDate" }
                        };

                        // Update titleblock parameters
                        foreach (var mapping in mappings)
                        {
                            var param = titleblock.LookupParameter(mapping.Key);
                            if (param != null && !param.IsReadOnly && projectData.ContainsKey(mapping.Value))
                            {
                                var value = projectData[mapping.Value];
                                if (!string.IsNullOrEmpty(value) && param.AsString() != value)
                                {
                                    param.Set(value);
                                    sheetUpdates.Add(mapping.Key);
                                    parameterUpdates++;
                                }
                            }
                        }

                        if (sheetUpdates.Count > 0)
                        {
                            updatedCount++;
                            results.Add(new
                            {
                                sheetId = sheet.Id.Value,
                                sheetNumber = sheet.SheetNumber,
                                sheetName = sheet.Name,
                                fieldsUpdated = sheetUpdates
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetsProcessed = sheets.Count,
                    sheetsUpdated = updatedCount,
                    totalParameterUpdates = parameterUpdates,
                    projectData = projectData,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch print/export sheets to PDF or printer.
        /// </summary>
        [MCPMethod("batchPrintSheets", Category = "Sheet", Description = "Batch print or export sheets to PDF")]
        public static string BatchPrintSheets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sheetNumbers = parameters?["sheetNumbers"]?.ToObject<List<string>>();
                var sheetRange = parameters?["sheetRange"]?.ToString(); // e.g., "A1.0-A1.5"
                var outputFolder = parameters?["outputFolder"]?.ToString();
                var exportToPDF = parameters?["exportToPDF"]?.ToObject<bool>() ?? true;
                var combinePDF = parameters?["combinePDF"]?.ToObject<bool>() ?? false;
                var fileNamePattern = parameters?["fileNamePattern"]?.ToString() ?? "{SheetNumber}_{SheetName}";

                // Get all sheets
                var allSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .OrderBy(s => s.SheetNumber)
                    .ToList();

                // Filter sheets by number list or range
                var sheetsToProcess = new List<ViewSheet>();

                if (sheetNumbers != null && sheetNumbers.Count > 0)
                {
                    sheetsToProcess = allSheets.Where(s => sheetNumbers.Contains(s.SheetNumber)).ToList();
                }
                else if (!string.IsNullOrEmpty(sheetRange))
                {
                    // Parse range like "A1.0-A1.5"
                    var parts = sheetRange.Split('-');
                    if (parts.Length == 2)
                    {
                        var startSheet = parts[0].Trim();
                        var endSheet = parts[1].Trim();
                        bool inRange = false;

                        foreach (var sheet in allSheets)
                        {
                            if (sheet.SheetNumber == startSheet) inRange = true;
                            if (inRange) sheetsToProcess.Add(sheet);
                            if (sheet.SheetNumber == endSheet) break;
                        }
                    }
                }
                else
                {
                    sheetsToProcess = allSheets;
                }

                // Prepare export info
                var exportResults = new List<object>();
                var viewIds = sheetsToProcess.Select(s => s.Id).ToList();

                // Build result info (actual export requires PrintManager which needs UI interaction)
                foreach (var sheet in sheetsToProcess)
                {
                    var fileName = fileNamePattern
                        .Replace("{SheetNumber}", sheet.SheetNumber)
                        .Replace("{SheetName}", sheet.Name)
                        .Replace("{ProjectName}", doc.Title)
                        .Replace(" ", "_");

                    // Clean filename
                    foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                    {
                        fileName = fileName.Replace(c, '_');
                    }

                    exportResults.Add(new
                    {
                        sheetId = sheet.Id.Value,
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        suggestedFileName = fileName + ".pdf",
                        viewportCount = new FilteredElementCollector(doc, sheet.Id)
                            .OfClass(typeof(Viewport))
                            .Count()
                    });
                }

                // Check if PDF export is available
                bool canExportPDF = false;
                try
                {
                    var pdfExportOptions = new PDFExportOptions();
                    canExportPDF = true;
                }
                catch
                {
                    canExportPDF = false;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetsFound = sheetsToProcess.Count,
                    sheetIds = viewIds.Select(id => id.Value).ToList(),
                    canExportPDF = canExportPDF,
                    outputFolder = outputFolder ?? "Use Revit's default PDF folder",
                    exportSettings = new
                    {
                        exportToPDF = exportToPDF,
                        combinePDF = combinePDF,
                        fileNamePattern = fileNamePattern
                    },
                    sheets = exportResults,
                    note = "To execute print: Use Revit's Print dialog or File > Export > PDF. Sheet IDs provided for batch selection.",
                    apiNote = canExportPDF
                        ? "PDF export API available in Revit 2022+. Use doc.Export() with PDFExportOptions."
                        : "PDF export requires manual configuration through Revit UI."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Intelligently arranges multiple viewports on a sheet based on strategy.
        /// Respects margin zones, prevents overlap, and maintains consistent spacing.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - sheetId (required): Sheet ID to arrange viewports on
        /// - strategy (optional): "grid", "cascade", "optimize", "horizontal", "vertical" (default: "grid")
        /// - margin (optional): Margin from sheet edges in feet (default: 0.25)
        /// - spacing (optional): Spacing between viewports in feet (default: 0.1)
        /// - alignToGrid (optional): Snap to grid lines if present (default: false)
        /// </param>
        /// <returns>JSON response with arrangement results</returns>
        [MCPMethod("generateViewportLayout", Category = "Sheet", Description = "Generate an automatic viewport layout arrangement on a sheet")]
        public static string GenerateViewportLayout(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sheetId is required"
                    });
                }

                int sheetIdInt = parameters["sheetId"].ToObject<int>();
                ElementId sheetId = new ElementId(sheetIdInt);
                ViewSheet sheet = doc.GetElement(sheetId) as ViewSheet;

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Sheet with ID {sheetIdInt} not found"
                    });
                }

                string strategy = parameters["strategy"]?.ToString()?.ToLower() ?? "grid";
                double margin = parameters["margin"]?.ToObject<double>() ?? 0.25; // feet
                double spacing = parameters["spacing"]?.ToObject<double>() ?? 0.1; // feet

                // Get titleblock dimensions for printable area
                var titleblocks = new FilteredElementCollector(doc, sheetId)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .ToList();

                double sheetWidth = 2.83333; // Default 34" (ARCH D)
                double sheetHeight = 1.83333; // Default 22"

                if (titleblocks.Count > 0)
                {
                    var tb = titleblocks[0];
                    var widthParam = tb.LookupParameter("Sheet Width") ?? tb.get_Parameter(BuiltInParameter.SHEET_WIDTH);
                    var heightParam = tb.LookupParameter("Sheet Height") ?? tb.get_Parameter(BuiltInParameter.SHEET_HEIGHT);

                    if (widthParam != null) sheetWidth = widthParam.AsDouble();
                    if (heightParam != null) sheetHeight = heightParam.AsDouble();
                }

                // Calculate printable area
                double printableMinX = margin;
                double printableMinY = margin;
                double printableMaxX = sheetWidth - margin;
                double printableMaxY = sheetHeight - margin;
                double printableWidth = printableMaxX - printableMinX;
                double printableHeight = printableMaxY - printableMinY;

                // Get all viewports on sheet
                var viewportIds = sheet.GetAllViewports();
                var viewports = viewportIds.Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).ToList();

                if (viewports.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No viewports found on sheet"
                    });
                }

                // Get viewport bounds
                var viewportData = viewports.Select(v =>
                {
                    var outline = v.GetBoxOutline();
                    double width = outline.MaximumPoint.X - outline.MinimumPoint.X;
                    double height = outline.MaximumPoint.Y - outline.MinimumPoint.Y;
                    return new
                    {
                        Viewport = v,
                        Width = width,
                        Height = height,
                        Center = v.GetBoxCenter()
                    };
                }).ToList();

                var results = new List<object>();
                int movedCount = 0;

                using (var trans = new Transaction(doc, "Generate Viewport Layout"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (strategy == "grid")
                    {
                        // Calculate optimal grid
                        int cols = (int)Math.Ceiling(Math.Sqrt(viewports.Count));
                        int rows = (int)Math.Ceiling((double)viewports.Count / cols);

                        double cellWidth = (printableWidth - spacing * (cols - 1)) / cols;
                        double cellHeight = (printableHeight - spacing * (rows - 1)) / rows;

                        int index = 0;
                        for (int row = 0; row < rows && index < viewports.Count; row++)
                        {
                            for (int col = 0; col < cols && index < viewports.Count; col++)
                            {
                                var vp = viewportData[index];
                                double centerX = printableMinX + cellWidth / 2 + col * (cellWidth + spacing);
                                double centerY = printableMaxY - cellHeight / 2 - row * (cellHeight + spacing);

                                vp.Viewport.SetBoxCenter(new XYZ(centerX, centerY, 0));
                                movedCount++;

                                results.Add(new
                                {
                                    viewportId = (int)vp.Viewport.Id.Value,
                                    viewName = doc.GetElement(vp.Viewport.ViewId)?.Name ?? "Unknown",
                                    newCenterX = centerX,
                                    newCenterY = centerY,
                                    gridPosition = $"Row {row + 1}, Col {col + 1}"
                                });

                                index++;
                            }
                        }
                    }
                    else if (strategy == "horizontal")
                    {
                        // Arrange in a single row
                        double totalWidth = viewportData.Sum(v => v.Width) + spacing * (viewports.Count - 1);
                        double startX = printableMinX + (printableWidth - totalWidth) / 2;
                        double centerY = printableMinY + printableHeight / 2;

                        double currentX = startX;
                        foreach (var vp in viewportData)
                        {
                            double centerX = currentX + vp.Width / 2;
                            vp.Viewport.SetBoxCenter(new XYZ(centerX, centerY, 0));
                            currentX += vp.Width + spacing;
                            movedCount++;

                            results.Add(new
                            {
                                viewportId = (int)vp.Viewport.Id.Value,
                                viewName = doc.GetElement(vp.Viewport.ViewId)?.Name ?? "Unknown",
                                newCenterX = centerX,
                                newCenterY = centerY
                            });
                        }
                    }
                    else if (strategy == "vertical")
                    {
                        // Arrange in a single column
                        double totalHeight = viewportData.Sum(v => v.Height) + spacing * (viewports.Count - 1);
                        double centerX = printableMinX + printableWidth / 2;
                        double startY = printableMaxY - (printableHeight - totalHeight) / 2;

                        double currentY = startY;
                        foreach (var vp in viewportData)
                        {
                            double centerY = currentY - vp.Height / 2;
                            vp.Viewport.SetBoxCenter(new XYZ(centerX, centerY, 0));
                            currentY -= vp.Height + spacing;
                            movedCount++;

                            results.Add(new
                            {
                                viewportId = (int)vp.Viewport.Id.Value,
                                viewName = doc.GetElement(vp.Viewport.ViewId)?.Name ?? "Unknown",
                                newCenterX = centerX,
                                newCenterY = centerY
                            });
                        }
                    }
                    else if (strategy == "cascade")
                    {
                        // Cascade from top-left
                        double offsetX = 0.3;
                        double offsetY = 0.3;
                        double currentX = printableMinX + viewportData[0].Width / 2;
                        double currentY = printableMaxY - viewportData[0].Height / 2;

                        foreach (var vp in viewportData)
                        {
                            vp.Viewport.SetBoxCenter(new XYZ(currentX, currentY, 0));
                            movedCount++;

                            results.Add(new
                            {
                                viewportId = (int)vp.Viewport.Id.Value,
                                viewName = doc.GetElement(vp.Viewport.ViewId)?.Name ?? "Unknown",
                                newCenterX = currentX,
                                newCenterY = currentY
                            });

                            currentX += offsetX;
                            currentY -= offsetY;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = sheetIdInt,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    strategy = strategy,
                    printableArea = new
                    {
                        width = printableWidth,
                        height = printableHeight,
                        margin = margin
                    },
                    viewportCount = viewports.Count,
                    movedCount = movedCount,
                    results = results,
                    message = $"Arranged {movedCount} viewports using '{strategy}' strategy"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #region Title Block Methods

        /// <summary>
        /// Get information about title blocks on sheets
        /// </summary>
        [MCPMethod("getTitleBlockInfo", Category = "Sheet", Description = "Get information about title blocks on sheets")]
        public static string GetTitleBlockInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                int? sheetIdInt = parameters["sheetId"]?.ToObject<int>();

                var results = new List<object>();

                if (sheetIdInt.HasValue)
                {
                    // Get title block for specific sheet
                    var sheet = doc.GetElement(new ElementId(sheetIdInt.Value)) as ViewSheet;
                    if (sheet == null)
                        return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });

                    var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .ToList();

                    foreach (var tb in titleBlocks)
                    {
                        results.Add(GetTitleBlockData(tb, sheet));
                    }
                }
                else
                {
                    // Get all title blocks
                    var titleBlocks = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .ToList();

                    foreach (var tb in titleBlocks)
                    {
                        var sheet = doc.GetElement(tb.OwnerViewId) as ViewSheet;
                        results.Add(GetTitleBlockData(tb, sheet));
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = results.Count,
                    titleBlocks = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object GetTitleBlockData(FamilyInstance tb, ViewSheet sheet)
        {
            var parameters = new Dictionary<string, object>();
            foreach (Parameter param in tb.Parameters)
            {
                if (param.HasValue && param.Definition?.Name != null)
                {
                    try
                    {
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                parameters[param.Definition.Name] = param.AsString();
                                break;
                            case StorageType.Double:
                                parameters[param.Definition.Name] = param.AsDouble();
                                break;
                            case StorageType.Integer:
                                parameters[param.Definition.Name] = param.AsInteger();
                                break;
                            case StorageType.ElementId:
                                parameters[param.Definition.Name] = (int)param.AsElementId().Value;
                                break;
                        }
                    }
                    catch { }
                }
            }

            return new
            {
                id = (int)tb.Id.Value,
                familyName = tb.Symbol.FamilyName,
                typeName = tb.Symbol.Name,
                sheetId = sheet != null ? (int)sheet.Id.Value : -1,
                sheetNumber = sheet?.SheetNumber,
                sheetName = sheet?.Name,
                parameters = parameters
            };
        }

        /// <summary>
        /// Update parameters on a title block
        /// </summary>
        [MCPMethod("updateTitleBlockParameters", Category = "Sheet", Description = "Update parameters on a title block")]
        public static string UpdateTitleBlockParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["titleBlockId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "titleBlockId is required" });
                if (parameters["updates"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "updates is required" });

                int titleBlockIdInt = parameters["titleBlockId"].ToObject<int>();
                var updates = parameters["updates"] as JObject;

                var tb = doc.GetElement(new ElementId(titleBlockIdInt)) as FamilyInstance;
                if (tb == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "Title block not found" });

                var updatedParams = new List<string>();
                var failedParams = new List<object>();

                using (var trans = new Transaction(doc, "Update Title Block Parameters"))
                {
                    trans.Start();

                    foreach (var update in updates)
                    {
                        string paramName = update.Key;
                        var param = tb.LookupParameter(paramName);

                        if (param == null)
                        {
                            failedParams.Add(new { name = paramName, error = "Parameter not found" });
                            continue;
                        }

                        if (param.IsReadOnly)
                        {
                            failedParams.Add(new { name = paramName, error = "Parameter is read-only" });
                            continue;
                        }

                        try
                        {
                            switch (param.StorageType)
                            {
                                case StorageType.String:
                                    param.Set(update.Value.ToString());
                                    break;
                                case StorageType.Double:
                                    param.Set(update.Value.ToObject<double>());
                                    break;
                                case StorageType.Integer:
                                    param.Set(update.Value.ToObject<int>());
                                    break;
                            }
                            updatedParams.Add(paramName);
                        }
                        catch (Exception ex)
                        {
                            failedParams.Add(new { name = paramName, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    titleBlockId = titleBlockIdInt,
                    updatedCount = updatedParams.Count,
                    updated = updatedParams,
                    failedCount = failedParams.Count,
                    failed = failedParams
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch update title blocks across multiple sheets
        /// </summary>
        [MCPMethod("batchUpdateTitleBlocks", Category = "Sheet", Description = "Batch update title block parameters across multiple sheets")]
        public static string BatchUpdateTitleBlocks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["updates"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "updates is required" });

                var updates = parameters["updates"] as JObject;
                var sheetIds = parameters["sheetIds"]?.ToObject<int[]>();

                // Get title blocks to update
                IEnumerable<FamilyInstance> titleBlocks;
                if (sheetIds != null && sheetIds.Length > 0)
                {
                    // Only specified sheets
                    var allTb = new List<FamilyInstance>();
                    foreach (int sheetId in sheetIds)
                    {
                        var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
                        if (sheet != null)
                        {
                            var sheetTb = new FilteredElementCollector(doc, sheet.Id)
                                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                                .OfClass(typeof(FamilyInstance))
                                .Cast<FamilyInstance>();
                            allTb.AddRange(sheetTb);
                        }
                    }
                    titleBlocks = allTb;
                }
                else
                {
                    // All sheets
                    titleBlocks = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>();
                }

                var results = new List<object>();
                int successCount = 0;
                int errorCount = 0;

                using (var trans = new Transaction(doc, "Batch Update Title Blocks"))
                {
                    trans.Start();

                    foreach (var tb in titleBlocks)
                    {
                        var tbUpdates = new List<string>();
                        var tbErrors = new List<string>();

                        foreach (var update in updates)
                        {
                            string paramName = update.Key;
                            var param = tb.LookupParameter(paramName);

                            if (param == null || param.IsReadOnly)
                            {
                                continue; // Skip silently for batch operations
                            }

                            try
                            {
                                switch (param.StorageType)
                                {
                                    case StorageType.String:
                                        param.Set(update.Value.ToString());
                                        break;
                                    case StorageType.Double:
                                        param.Set(update.Value.ToObject<double>());
                                        break;
                                    case StorageType.Integer:
                                        param.Set(update.Value.ToObject<int>());
                                        break;
                                }
                                tbUpdates.Add(paramName);
                            }
                            catch
                            {
                                tbErrors.Add(paramName);
                            }
                        }

                        var sheet = doc.GetElement(tb.OwnerViewId) as ViewSheet;
                        results.Add(new
                        {
                            titleBlockId = (int)tb.Id.Value,
                            sheetNumber = sheet?.SheetNumber,
                            updated = tbUpdates,
                            errors = tbErrors
                        });

                        if (tbUpdates.Count > 0) successCount++;
                        if (tbErrors.Count > 0) errorCount++;
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalTitleBlocks = results.Count,
                    successCount = successCount,
                    errorCount = errorCount,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region PlaceViewOnSheetForced - Enhanced placement for copied views

        /// <summary>
        /// Places a view on a sheet with enhanced handling for views copied between documents.
        /// This method forces view initialization that may be needed for copied DraftingViews.
        /// </summary>
        [MCPMethod("placeViewOnSheetForced", Category = "Sheet", Description = "Place a view on a sheet with forced initialization for copied views")]
        public static string PlaceViewOnSheetForced(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                // Validate required parameters
                if (parameters["sheetId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                if (parameters["viewId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"Sheet {sheetId.Value} not found" });

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"View {viewId.Value} not found" });

                // Parse location
                double x = 1.5, y = 1.0;
                if (parameters["location"] != null)
                {
                    var location = parameters["location"].ToObject<double[]>();
                    if (location != null && location.Length >= 2)
                    {
                        x = location[0];
                        y = location[1];
                    }
                }

                // STEP 1: Force view initialization by opening it in the UI
                View originalActiveView = uiDoc.ActiveView;
                try
                {
                    uiDoc.ActiveView = view;
                    System.Threading.Thread.Sleep(500); // Wait for Revit to fully initialize the view
                }
                catch { /* May fail for some view types */ }

                // STEP 2: Force regeneration outside any transaction
                try
                {
                    doc.Regenerate();
                }
                catch { }

                // STEP 3: For DraftingViews, try to access the outline to force bounds calculation
                if (view is ViewDrafting draftingView)
                {
                    try
                    {
                        // Force the view to calculate its outline
                        var outline = draftingView.Outline;
                        if (outline != null)
                        {
                            var min = outline.Min;
                            var max = outline.Max;
                        }
                    }
                    catch { }

                    // Try getting CropBox which also forces calculation
                    try
                    {
                        var cropBox = draftingView.CropBox;
                    }
                    catch { }
                }

                // STEP 4: Switch back to sheet
                try
                {
                    uiDoc.ActiveView = sheet;
                    System.Threading.Thread.Sleep(200);
                }
                catch { }

                // STEP 5: Check if view can be added
                if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
                {
                    // Check if already on a sheet
                    var existingVp = new FilteredElementCollector(doc)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .FirstOrDefault(vp => vp.ViewId == viewId);

                    if (existingVp != null)
                    {
                        var existingSheet = doc.GetElement(existingVp.SheetId) as ViewSheet;
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View already placed on sheet {existingSheet?.SheetNumber}",
                            existingViewportId = existingVp.Id.Value
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot add view to sheet - Revit validation failed",
                        viewType = view.ViewType.ToString(),
                        viewName = view.Name
                    });
                }

                // STEP 6: Create viewport with multiple strategies
                using (var trans = new Transaction(doc, "Place View on Sheet (Forced)"))
                {
                    trans.Start();
                    var failOpts = trans.GetFailureHandlingOptions();
                    failOpts.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failOpts);

                    // Regenerate inside transaction
                    doc.Regenerate();

                    Viewport viewport = null;
                    string successMethod = null;

                    // Strategy A: Direct placement
                    try
                    {
                        viewport = Viewport.Create(doc, sheetId, viewId, new XYZ(x, y, 0));
                        if (viewport != null) successMethod = "Direct";
                    }
                    catch { viewport = null; }

                    // Strategy B: Place at sheet center, then move
                    if (viewport == null)
                    {
                        try
                        {
                            doc.Regenerate();
                            viewport = Viewport.Create(doc, sheetId, viewId, new XYZ(1.5, 1.0, 0));
                            if (viewport != null)
                            {
                                successMethod = "CenterThenMove";
                                try { viewport.SetBoxCenter(new XYZ(x, y, 0)); } catch { }
                            }
                        }
                        catch { viewport = null; }
                    }

                    // Strategy C: For DraftingViews, duplicate and place the duplicate
                    if (viewport == null && view is ViewDrafting)
                    {
                        try
                        {
                            // Duplicate the view
                            var dupId = view.Duplicate(ViewDuplicateOption.Duplicate);
                            if (dupId != ElementId.InvalidElementId)
                            {
                                doc.Regenerate();
                                var dupView = doc.GetElement(dupId) as View;
                                if (dupView != null && Viewport.CanAddViewToSheet(doc, sheetId, dupId))
                                {
                                    viewport = Viewport.Create(doc, sheetId, dupId, new XYZ(x, y, 0));
                                    if (viewport != null)
                                    {
                                        successMethod = "DuplicatedView";
                                        // Rename duplicate to match original
                                        try
                                        {
                                            string origName = view.Name;
                                            view.Name = origName + "_OLD";
                                            dupView.Name = origName;
                                            doc.Delete(viewId); // Delete original
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (viewport == null)
                    {
                        trans.RollBack();

                        // Gather diagnostic info
                        int elemCount = 0;
                        try
                        {
                            elemCount = new FilteredElementCollector(doc, viewId)
                                .WhereElementIsNotElementType()
                                .GetElementCount();
                        }
                        catch { }

                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "All placement strategies failed. The view may need manual placement.",
                            viewName = view.Name,
                            viewType = view.ViewType.ToString(),
                            elementCount = elemCount,
                            suggestion = "Try opening the view in Revit first, then drag it from Project Browser to the sheet"
                        });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewportId = viewport.Id.Value,
                        sheetId = sheetId.Value,
                        viewId = viewport.ViewId.Value,
                        viewName = doc.GetElement(viewport.ViewId)?.Name ?? view.Name,
                        sheetNumber = sheet.SheetNumber,
                        location = new[] { x, y, 0.0 },
                        placementMethod = successMethod
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
