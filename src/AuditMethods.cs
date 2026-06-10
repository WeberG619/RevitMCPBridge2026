using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge
{
    /// <summary>
    /// Construction Document Audit Methods
    /// Enables Claude AI to verify view content, coordination, and standards compliance
    /// </summary>
    public static class AuditMethods
    {
        /// <summary>
        /// Audit view content - count and list all annotations, tags, and documentation elements
        /// </summary>
        [MCPMethod("auditViewContent", Category = "Audit")]
        public static string AuditViewContent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get view ID (optional - uses active view if not provided)
                ElementId viewId = null;
                if (parameters["viewId"] != null)
                {
                    viewId = new ElementId(parameters["viewId"].Value<int>());
                }
                else
                {
                    viewId = uiApp.ActiveUIDocument.ActiveView.Id;
                }

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Count different annotation types
                var roomTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_RoomTags)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var doorTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_DoorTags)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var windowTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_WindowTags)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var wallTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_WallTags)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var dimensions = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Dimensions)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_TextNotes)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var sectionMarks = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Viewers)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var detailCallouts = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Callouts)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Get keynotes (KeynoteTags category)
                var keynotes = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_KeynoteTags)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Count rooms in view (to check for untagged rooms)
                var rooms = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Count doors in view (to check for untagged doors)
                var doors = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Count windows in view (to check for untagged windows)
                var windows = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Build missing elements list
                var missingElements = new List<string>();

                if (rooms.Count > roomTags.Count)
                    missingElements.Add($"{rooms.Count - roomTags.Count} rooms without tags");
                if (doors.Count > doorTags.Count)
                    missingElements.Add($"{doors.Count - doorTags.Count} doors without tags");
                if (windows.Count > windowTags.Count)
                    missingElements.Add($"{windows.Count - windowTags.Count} windows without tags");
                if (dimensions.Count == 0 && (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.CeilingPlan))
                    missingElements.Add("No dimensions in plan view");
                if (keynotes.Count == 0 && view.ViewType != ViewType.Legend && view.ViewType != ViewType.Schedule)
                    missingElements.Add("No keynotes applied");

                // Calculate completeness score (0-100)
                int totalPossible = 6; // room tags, door tags, window tags, dimensions, keynotes, section marks
                int present = 0;
                if (roomTags.Count >= rooms.Count && rooms.Count > 0) present++;
                if (doorTags.Count >= doors.Count && doors.Count > 0) present++;
                if (windowTags.Count >= windows.Count && windows.Count > 0) present++;
                if (dimensions.Count > 0) present++;
                if (keynotes.Count > 0) present++;
                if (sectionMarks.Count > 0 || detailCallouts.Count > 0) present++;

                int completenessScore = (totalPossible > 0) ? (present * 100 / totalPossible) : 100;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId.Value,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    annotations = new
                    {
                        roomTags = roomTags.Count,
                        doorTags = doorTags.Count,
                        windowTags = windowTags.Count,
                        wallTags = wallTags.Count,
                        dimensions = dimensions.Count,
                        textNotes = textNotes.Count,
                        sectionMarks = sectionMarks.Count,
                        detailCallouts = detailCallouts.Count,
                        keynotes = keynotes.Count
                    },
                    elements = new
                    {
                        rooms = rooms.Count,
                        doors = doors.Count,
                        windows = windows.Count
                    },
                    missingElements = missingElements,
                    completenessScore = completenessScore
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AUDIT] AuditViewContent failed");
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Audit all coordination references - sections, details, and their sheet placements
        /// </summary>
        [MCPMethod("auditCoordination", Category = "Audit")]
        public static string AuditCoordination(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all sections
                var sections = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSection))
                    .Cast<ViewSection>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                // Get all detail views (drafting views)
                var detailViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                // Get all sheets and their viewports
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();

                // Build a map of view IDs to sheet placements
                var viewToSheet = new Dictionary<ElementId, string>();
                foreach (var sheet in sheets)
                {
                    var viewportIds = sheet.GetAllViewports();
                    foreach (var vpId in viewportIds)
                    {
                        var viewport = doc.GetElement(vpId) as Viewport;
                        if (viewport != null)
                        {
                            viewToSheet[viewport.ViewId] = sheet.SheetNumber;
                        }
                    }
                }

                // Find orphaned sections (not placed on any sheet)
                var orphanedSections = sections
                    .Where(s => !viewToSheet.ContainsKey(s.Id))
                    .Select(s => new { viewId = s.Id.Value, name = s.Name })
                    .ToList();

                // Find orphaned details (not placed on any sheet)
                var orphanedDetails = detailViews
                    .Where(d => !viewToSheet.ContainsKey(d.Id))
                    .Select(d => new { viewId = d.Id.Value, name = d.Name })
                    .ToList();

                // Get all section marks and detail callouts to check if they reference valid views
                var allSectionMarks = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Viewers)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var allCallouts = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Callouts)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Check door schedule coordination
                var doorSchedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s.Name.ToLower().Contains("door"))
                    .ToList();

                var allDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Check window schedule coordination
                var windowSchedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s.Name.ToLower().Contains("window"))
                    .ToList();

                var allWindows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Summary statistics
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new
                    {
                        totalSheets = sheets.Count,
                        totalSections = sections.Count,
                        totalDetails = detailViews.Count,
                        totalDoors = allDoors.Count,
                        totalWindows = allWindows.Count
                    },
                    placementStatus = new
                    {
                        sectionsOnSheets = sections.Count - orphanedSections.Count,
                        sectionsUnplaced = orphanedSections.Count,
                        detailsOnSheets = detailViews.Count - orphanedDetails.Count,
                        detailsUnplaced = orphanedDetails.Count
                    },
                    orphanedSections = orphanedSections,
                    orphanedDetails = orphanedDetails,
                    calloutCounts = new
                    {
                        sectionMarks = allSectionMarks.Count,
                        detailCallouts = allCallouts.Count
                    },
                    schedules = new
                    {
                        doorSchedules = doorSchedules.Select(s => s.Name).ToList(),
                        windowSchedules = windowSchedules.Select(s => s.Name).ToList()
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AUDIT] AuditCoordination failed");
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Get all keynotes applied in a view with their keynote values
        /// </summary>
        [MCPMethod("getViewKeynotes", Category = "Audit")]
        public static string GetViewKeynotes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get view ID
                ElementId viewId = null;
                if (parameters["viewId"] != null)
                {
                    viewId = new ElementId(parameters["viewId"].Value<int>());
                }
                else
                {
                    viewId = uiApp.ActiveUIDocument.ActiveView.Id;
                }

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Get all keynote tags in view
                var keynoteTags = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_KeynoteTags)
                    .WhereElementIsNotElementType()
                    .Cast<IndependentTag>()
                    .ToList();

                var keynoteList = new List<object>();
                foreach (var tag in keynoteTags)
                {
                    try
                    {
                        // Get the tagged element
                        var taggedIds = tag.GetTaggedLocalElementIds();
                        string taggedElementInfo = "Unknown";
                        string keynoteValue = "";

                        if (taggedIds.Count > 0)
                        {
                            var taggedElement = doc.GetElement(taggedIds.First());
                            if (taggedElement != null)
                            {
                                taggedElementInfo = $"{taggedElement.Category?.Name}: {taggedElement.Name}";

                                // Try to get keynote parameter
                                var keynoteParam = taggedElement.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                                if (keynoteParam != null)
                                {
                                    keynoteValue = keynoteParam.AsString() ?? "";
                                }
                            }
                        }

                        keynoteList.Add(new
                        {
                            tagId = tag.Id.Value,
                            keynoteValue = keynoteValue,
                            taggedElement = taggedElementInfo,
                            location = tag.TagHeadPosition != null ? new
                            {
                                x = Math.Round(tag.TagHeadPosition.X, 2),
                                y = Math.Round(tag.TagHeadPosition.Y, 2)
                            } : null
                        });
                    }
                    catch { }
                }

                // Group by keynote value for summary
                var keynoteGroups = keynoteList
                    .GroupBy(k => ((dynamic)k).keynoteValue)
                    .Select(g => new { keynote = g.Key, count = g.Count() })
                    .OrderBy(g => g.keynote)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId.Value,
                    viewName = view.Name,
                    totalKeynotes = keynoteList.Count,
                    keynoteSummary = keynoteGroups,
                    keynotes = keynoteList
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AUDIT] GetViewKeynotes failed");
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Audit sheet completeness - check if all required elements are present
        /// </summary>
        [MCPMethod("auditSheet", Category = "Audit")]
        public static string AuditSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get sheet number or ID
                ViewSheet sheet = null;
                if (parameters["sheetNumber"] != null)
                {
                    var sheetNumber = parameters["sheetNumber"].ToString();
                    sheet = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .FirstOrDefault(s => s.SheetNumber == sheetNumber);
                }
                else if (parameters["sheetId"] != null)
                {
                    var sheetId = new ElementId(parameters["sheetId"].Value<int>());
                    sheet = doc.GetElement(sheetId) as ViewSheet;
                }

                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                // Get all viewports on sheet
                var viewportIds = sheet.GetAllViewports();
                var viewports = viewportIds
                    .Select(id => doc.GetElement(id) as Viewport)
                    .Where(vp => vp != null)
                    .ToList();

                // Analyze each viewport
                var viewportDetails = new List<object>();
                foreach (var vp in viewports)
                {
                    var placedView = doc.GetElement(vp.ViewId) as View;
                    if (placedView != null)
                    {
                        // Quick audit of view content
                        var roomTags = new FilteredElementCollector(doc, placedView.Id)
                            .OfCategory(BuiltInCategory.OST_RoomTags)
                            .WhereElementIsNotElementType()
                            .Count();
                        var dimensions = new FilteredElementCollector(doc, placedView.Id)
                            .OfCategory(BuiltInCategory.OST_Dimensions)
                            .WhereElementIsNotElementType()
                            .Count();
                        var keynotes = new FilteredElementCollector(doc, placedView.Id)
                            .OfCategory(BuiltInCategory.OST_KeynoteTags)
                            .WhereElementIsNotElementType()
                            .Count();

                        viewportDetails.Add(new
                        {
                            viewportId = vp.Id.Value,
                            viewId = placedView.Id.Value,
                            viewName = placedView.Name,
                            viewType = placedView.ViewType.ToString(),
                            scale = placedView.Scale,
                            annotations = new
                            {
                                roomTags,
                                dimensions,
                                keynotes
                            }
                        });
                    }
                }

                // Check for required sheet elements
                var issues = new List<string>();

                // Check title block
                var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .ToElements();
                if (titleBlocks.Count == 0)
                    issues.Add("No title block on sheet");

                // Check for any views on sheet
                if (viewports.Count == 0)
                    issues.Add("No views placed on sheet");

                // Check for legends on plan sheets
                var sheetNum = sheet.SheetNumber;
                if (sheetNum.StartsWith("A1") || sheetNum.StartsWith("A2"))
                {
                    var hasLegend = viewportDetails.Any(v => ((dynamic)v).viewType == "Legend");
                    if (!hasLegend)
                        issues.Add("Plan sheet may need legend");
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = sheet.Id.Value,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    viewportCount = viewports.Count,
                    viewports = viewportDetails,
                    titleBlockCount = titleBlocks.Count,
                    issues = issues,
                    isComplete = issues.Count == 0
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AUDIT] AuditSheet failed");
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Run comprehensive QC checks on the entire document
        /// </summary>
        [MCPMethod("runCDQualityCheck", Category = "Audit")]
        public static string RunCDQualityCheck(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var issues = new List<object>();
                var warnings = new List<object>();
                var stats = new Dictionary<string, int>();

                // Get all sheets
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();
                stats["totalSheets"] = sheets.Count;

                // Check for empty sheets
                var emptySheets = sheets.Where(s => s.GetAllViewports().Count == 0).ToList();
                stats["emptySheets"] = emptySheets.Count;
                foreach (var es in emptySheets)
                {
                    warnings.Add(new { type = "EmptySheet", sheetNumber = es.SheetNumber, message = "Sheet has no views placed" });
                }

                // Get all views
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser)
                    .ToList();
                stats["totalViews"] = views.Count;

                // Check floor plans for required annotations
                var floorPlans = views.Where(v => v.ViewType == ViewType.FloorPlan).ToList();
                foreach (var fp in floorPlans)
                {
                    var dims = new FilteredElementCollector(doc, fp.Id)
                        .OfCategory(BuiltInCategory.OST_Dimensions)
                        .WhereElementIsNotElementType()
                        .Count();
                    if (dims == 0)
                    {
                        warnings.Add(new { type = "NoDimensions", viewName = fp.Name, message = "Floor plan has no dimensions" });
                    }
                }

                // Check for untagged rooms
                var allRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Count();
                var allRoomTags = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RoomTags)
                    .WhereElementIsNotElementType()
                    .Count();
                stats["rooms"] = allRooms;
                stats["roomTags"] = allRoomTags;
                if (allRooms > allRoomTags)
                {
                    issues.Add(new { type = "UntaggedRooms", count = allRooms - allRoomTags, message = $"{allRooms - allRoomTags} rooms without tags" });
                }

                // Check for untagged doors
                var allDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Count();
                var allDoorTags = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DoorTags)
                    .WhereElementIsNotElementType()
                    .Count();
                stats["doors"] = allDoors;
                stats["doorTags"] = allDoorTags;
                if (allDoors > allDoorTags)
                {
                    issues.Add(new { type = "UntaggedDoors", count = allDoors - allDoorTags, message = $"{allDoors - allDoorTags} doors without tags" });
                }

                // Check for model warnings
                var revitWarnings = doc.GetWarnings();
                stats["revitWarnings"] = revitWarnings.Count;
                if (revitWarnings.Count > 0)
                {
                    warnings.Add(new { type = "RevitWarnings", count = revitWarnings.Count, message = $"{revitWarnings.Count} Revit warnings in model" });
                }

                // Calculate overall score
                int totalChecks = 5;
                int passedChecks = 0;
                if (emptySheets.Count == 0) passedChecks++;
                if (allRooms <= allRoomTags) passedChecks++;
                if (allDoors <= allDoorTags) passedChecks++;
                if (revitWarnings.Count < 10) passedChecks++;
                if (floorPlans.All(fp => new FilteredElementCollector(doc, fp.Id).OfCategory(BuiltInCategory.OST_Dimensions).WhereElementIsNotElementType().Any())) passedChecks++;

                int qualityScore = (passedChecks * 100) / totalChecks;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = doc.Title,
                    qualityScore = qualityScore,
                    stats = stats,
                    issues = issues,
                    warnings = warnings,
                    recommendation = qualityScore >= 80 ? "Ready for review" : qualityScore >= 60 ? "Address issues before review" : "Significant work needed"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AUDIT] RunCDQualityCheck failed");
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Get BD Architect keynote standards compliance
        /// </summary>
        [MCPMethod("auditKeynoteCompliance", Category = "Audit")]
        public static string AuditKeynoteCompliance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // BD Architect keynote series
                var validSeries = new Dictionary<string, string>
                {
                    { "1", "Demolition (100s)" },
                    { "2", "New Construction (200s)" },
                    { "4", "RCP (400s)" },
                    { "5", "Interior/Equipment (500s)" },
                    { "6", "Equipment Specifics (600s)" }
                };

                // Get all keynote tags in document
                var allKeynotes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_KeynoteTags)
                    .WhereElementIsNotElementType()
                    .Cast<IndependentTag>()
                    .ToList();

                var keynoteValues = new List<string>();
                var invalidKeynotes = new List<object>();
                var keynotesByCategory = new Dictionary<string, int>();

                foreach (var tag in allKeynotes)
                {
                    try
                    {
                        var taggedIds = tag.GetTaggedLocalElementIds();
                        if (taggedIds.Count > 0)
                        {
                            var taggedElement = doc.GetElement(taggedIds.First());
                            if (taggedElement != null)
                            {
                                var keynoteParam = taggedElement.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                                if (keynoteParam != null)
                                {
                                    var value = keynoteParam.AsString() ?? "";
                                    keynoteValues.Add(value);

                                    // Check if valid BD Architect keynote
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        var firstChar = value[0].ToString();
                                        if (validSeries.ContainsKey(firstChar))
                                        {
                                            var category = validSeries[firstChar];
                                            if (!keynotesByCategory.ContainsKey(category))
                                                keynotesByCategory[category] = 0;
                                            keynotesByCategory[category]++;
                                        }
                                        else
                                        {
                                            invalidKeynotes.Add(new { value = value, elementId = taggedElement.Id.Value });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Calculate compliance
                int totalKeynotes = keynoteValues.Count;
                int validKeynotes = totalKeynotes - invalidKeynotes.Count;
                int compliancePercent = totalKeynotes > 0 ? (validKeynotes * 100 / totalKeynotes) : 100;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalKeynotes = totalKeynotes,
                    validKeynotes = validKeynotes,
                    invalidKeynotes = invalidKeynotes.Count,
                    compliancePercent = compliancePercent,
                    keynotesByCategory = keynotesByCategory,
                    invalidKeynoteList = invalidKeynotes,
                    standard = "BD Architect Keynote System",
                    validSeries = validSeries
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[AUDIT] AuditKeynoteCompliance failed");
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }
    }
}
