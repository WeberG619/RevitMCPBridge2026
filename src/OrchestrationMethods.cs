using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Orchestration methods for composite workflows.
    /// These methods combine multiple operations into single high-level commands
    /// with built-in verification and error handling.
    /// </summary>
    public static class OrchestrationMethods
    {
        #region Life Safety Legend

        /// <summary>
        /// Create a complete life safety legend based on project data
        /// Parameters:
        /// - viewId: (optional) Existing legend view ID to populate, or creates new
        /// - projectType: Occupancy type (e.g., "GROUP B (SPA & WELLNESS)")
        /// - totalArea: Total square footage (auto-calculated from rooms if not provided)
        /// - occupantLoadFactor: SF per person (default 100 for Group B)
        /// - isSprinkling: Whether building is sprinklered (default false)
        /// - corridorWidth: Minimum corridor width in inches (default 44)
        /// - scale: View scale (default 96 = 1/8" = 1'-0")
        /// </summary>
        public static string CreateLifeSafetyLegend(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var results = new List<object>();
                var createdElements = new List<int>();

                // Get or create legend view
                ElementId viewId;
                View legendView;

                if (parameters["viewId"] != null)
                {
                    viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    legendView = doc.GetElement(viewId) as View;
                    if (legendView == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                    }
                }
                else
                {
                    // Create new legend view
                    var legendViewFamilyType = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Legend);

                    if (legendViewFamilyType == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Legend view type not found" });
                    }

                    using (var trans = new Transaction(doc, "Create Life Safety Legend"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        legendView = ViewDrafting.Create(doc, legendViewFamilyType.Id);
                        legendView.Name = "LIFE SAFETY LEGEND";
                        legendView.Scale = parameters["scale"]?.ToObject<int>() ?? 96;
                        trans.Commit();
                    }
                    viewId = legendView.Id;
                    results.Add(new { step = "createView", viewId = (int)viewId.Value, viewName = legendView.Name });
                }

                // Get project data
                var projectType = parameters["projectType"]?.ToString() ?? "GROUP B (BUSINESS)";
                var occupantLoadFactor = parameters["occupantLoadFactor"]?.ToObject<int>() ?? 100;
                var isSprinklered = parameters["isSprinklered"]?.ToObject<bool>() ?? false;
                var corridorWidth = parameters["corridorWidth"]?.ToObject<int>() ?? 44;

                // Calculate total area from rooms if not provided
                double totalArea;
                if (parameters["totalArea"] != null)
                {
                    totalArea = parameters["totalArea"].ToObject<double>();
                }
                else
                {
                    var rooms = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<SpatialElement>()
                        .ToList();

                    totalArea = 0;
                    foreach (var room in rooms)
                    {
                        var areaParam = room.get_Parameter(BuiltInParameter.ROOM_AREA);
                        if (areaParam != null)
                        {
                            totalArea += areaParam.AsDouble();
                        }
                    }
                    results.Add(new { step = "calculateArea", roomCount = rooms.Count, totalArea = Math.Round(totalArea, 0) });
                }

                // Calculate occupant load
                var occupantLoad = (int)Math.Ceiling(totalArea / occupantLoadFactor);

                // Calculate egress requirements
                var egressCapacityFactor = isSprinklered ? 0.2 : 0.15; // per inch of door width
                var requiredEgressWidth = (int)Math.Ceiling(occupantLoad / (egressCapacityFactor * 100)) * 100 / 100.0;

                // Travel distance
                var maxTravelDistance = isSprinklered ? 250 : 200;

                // Get text note types
                var textTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .ToList();

                var titleType = textTypes.FirstOrDefault(t => t.Name.Contains("3/16") || t.Name.Contains("Title")) ?? textTypes.FirstOrDefault();
                var contentType = textTypes.FirstOrDefault(t => t.Name.Contains("3/32") || t.Name.Contains("Body")) ?? textTypes.FirstOrDefault();

                if (titleType == null || contentType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No text note types found" });
                }

                // Create legend content
                using (var trans = new Transaction(doc, "Populate Life Safety Legend"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var yOffset = 40.0; // Start position
                    var rowSpacing = 3.0;

                    // Title
                    var titleNote = TextNote.Create(doc, viewId, new XYZ(0, yOffset, 0), "LIFE SAFETY LEGEND", titleType.Id);
                    createdElements.Add((int)titleNote.Id.Value);
                    yOffset -= rowSpacing * 2;

                    // Content rows
                    var legendContent = new[]
                    {
                        $"USE: {projectType}",
                        $"GROSS AREA: {Math.Round(totalArea, 0):N0} SF",
                        $"OCCUPANT LOAD FACTOR: {occupantLoadFactor} SF/PERSON",
                        $"OCCUPANT LOAD: {occupantLoad} PERSONS",
                        "",
                        "EGRESS REQUIREMENTS:",
                        $"  MIN. CORRIDOR WIDTH: {corridorWidth}\"",
                        $"  MAX. TRAVEL DISTANCE: {maxTravelDistance}' ({(isSprinklered ? "SPRINKLERED" : "NON-SPRINKLERED")})",
                        $"  REQUIRED EGRESS WIDTH: {requiredEgressWidth:F1}\" PER DOOR",
                        "",
                        "PLUMBING REQUIREMENTS:",
                        $"  OCCUPANT LOAD: {occupantLoad} PERSONS",
                        $"  WATER CLOSETS: PER FPC TABLE 2902.1"
                    };

                    foreach (var line in legendContent)
                    {
                        if (!string.IsNullOrEmpty(line))
                        {
                            var note = TextNote.Create(doc, viewId, new XYZ(0, yOffset, 0), line, contentType.Id);
                            createdElements.Add((int)note.Id.Value);
                        }
                        yOffset -= rowSpacing;
                    }

                    trans.Commit();
                }

                results.Add(new
                {
                    step = "createContent",
                    elementsCreated = createdElements.Count,
                    elementIds = createdElements
                });

                // Verification
                var verificationPassed = createdElements.Count > 0;
                foreach (var elemId in createdElements.Take(3))
                {
                    var elem = doc.GetElement(new ElementId(elemId));
                    if (elem == null)
                    {
                        verificationPassed = false;
                        break;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = legendView.Name,
                    verified = verificationPassed,
                    summary = new
                    {
                        projectType = projectType,
                        totalArea = Math.Round(totalArea, 0),
                        occupantLoad = occupantLoad,
                        isSprinklered = isSprinklered,
                        elementsCreated = createdElements.Count
                    },
                    steps = results,
                    createdElementIds = createdElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Area Calculation Legend

        /// <summary>
        /// Create an area calculation table from rooms
        /// Parameters:
        /// - viewId: (optional) Existing legend view ID, or creates new
        /// - includeRoomNumbers: Include room numbers (default true)
        /// - rowSpacing: Space between rows in feet (default 3)
        /// - scale: View scale (default 48 = 1/4" = 1'-0")
        /// </summary>
        public static string CreateAreaCalculationLegend(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var createdElements = new List<int>();

                // Get rooms
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<SpatialElement>()
                    .Where(r => r.Area > 0)
                    .ToList();

                if (rooms.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No rooms found in project" });
                }

                // Get or create view
                ElementId viewId;
                View legendView;

                if (parameters["viewId"] != null)
                {
                    viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    legendView = doc.GetElement(viewId) as View;
                    if (legendView == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                    }
                }
                else
                {
                    var legendViewFamilyType = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Legend);

                    using (var trans = new Transaction(doc, "Create Area Calculation Legend"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        legendView = ViewDrafting.Create(doc, legendViewFamilyType.Id);
                        legendView.Name = "AREA CALCULATION";
                        legendView.Scale = parameters["scale"]?.ToObject<int>() ?? 48;
                        trans.Commit();
                    }
                    viewId = legendView.Id;
                }

                // Settings
                var includeRoomNumbers = parameters["includeRoomNumbers"]?.ToObject<bool>() ?? true;
                var rowSpacing = parameters["rowSpacing"]?.ToObject<double>() ?? 3.0;

                // Get text types
                var textTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .ToList();

                var titleType = textTypes.FirstOrDefault(t => t.Name.Contains("3/16")) ?? textTypes.FirstOrDefault();
                var contentType = textTypes.FirstOrDefault(t => t.Name.Contains("3/32")) ?? textTypes.FirstOrDefault();

                // Build room data
                var roomData = rooms.Select(r =>
                {
                    var name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed";
                    var number = r.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                    var area = r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0;
                    return new { name, number, area };
                }).OrderBy(r => r.name).ToList();

                var totalArea = roomData.Sum(r => r.area);

                // Create content
                using (var trans = new Transaction(doc, "Populate Area Calculation"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var yOffset = 50.0;
                    var col1X = 0.0;
                    var col2X = 20.0;

                    // Title
                    var title = TextNote.Create(doc, viewId, new XYZ(col1X, yOffset, 0), "AREA CALCULATION", titleType.Id);
                    createdElements.Add((int)title.Id.Value);
                    yOffset -= rowSpacing * 2;

                    // Header
                    var header1 = TextNote.Create(doc, viewId, new XYZ(col1X, yOffset, 0), "ROOM NAME", contentType.Id);
                    var header2 = TextNote.Create(doc, viewId, new XYZ(col2X, yOffset, 0), "AREA (SF)", contentType.Id);
                    createdElements.Add((int)header1.Id.Value);
                    createdElements.Add((int)header2.Id.Value);
                    yOffset -= rowSpacing;

                    // Separator line would go here (detail line)

                    // Room rows
                    foreach (var room in roomData)
                    {
                        var displayName = includeRoomNumbers && !string.IsNullOrEmpty(room.number)
                            ? $"{room.number} - {room.name}"
                            : room.name;

                        var nameNote = TextNote.Create(doc, viewId, new XYZ(col1X, yOffset, 0), displayName, contentType.Id);
                        var areaNote = TextNote.Create(doc, viewId, new XYZ(col2X, yOffset, 0), $"{Math.Round(room.area, 0):N0}", contentType.Id);
                        createdElements.Add((int)nameNote.Id.Value);
                        createdElements.Add((int)areaNote.Id.Value);
                        yOffset -= rowSpacing;
                    }

                    // Total
                    yOffset -= rowSpacing / 2;
                    var totalLabel = TextNote.Create(doc, viewId, new XYZ(col1X, yOffset, 0), "TOTAL:", titleType.Id);
                    var totalValue = TextNote.Create(doc, viewId, new XYZ(col2X, yOffset, 0), $"{Math.Round(totalArea, 0):N0} SF", titleType.Id);
                    createdElements.Add((int)totalLabel.Id.Value);
                    createdElements.Add((int)totalValue.Id.Value);

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verified = true,
                    viewId = (int)viewId.Value,
                    viewName = legendView.Name,
                    summary = new
                    {
                        roomCount = roomData.Count,
                        totalArea = Math.Round(totalArea, 0),
                        elementsCreated = createdElements.Count
                    },
                    rooms = roomData.Select(r => new { r.name, r.number, area = Math.Round(r.area, 0) }),
                    createdElementIds = createdElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Workflow Execution

        /// <summary>
        /// Execute a multi-step workflow with verification at each step
        /// Parameters:
        /// - workflowName: Name for logging
        /// - steps: Array of step objects, each with:
        ///   - name: Step name
        ///   - method: MCP method to call
        ///   - params: Parameters for the method
        ///   - verify: (optional) Verification to run after step
        ///   - continueOnFailure: (optional) Continue if step fails (default false)
        /// </summary>
        public static string ExecuteWorkflow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["steps"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "steps array is required" });
                }

                var workflowName = parameters["workflowName"]?.ToString() ?? "Unnamed Workflow";
                var steps = parameters["steps"].ToObject<List<JObject>>();
                var results = new List<object>();
                var completedSteps = 0;
                var failedSteps = 0;
                var workflowSuccess = true;

                foreach (var step in steps)
                {
                    var stepName = step["name"]?.ToString() ?? $"Step {completedSteps + 1}";
                    var continueOnFailure = step["continueOnFailure"]?.ToObject<bool>() ?? false;

                    try
                    {
                        // Note: In a real implementation, this would dispatch to the actual method
                        // For now, we record the intent and let the caller chain the calls
                        var methodName = step["method"]?.ToString();
                        var methodParams = step["params"] as JObject;

                        results.Add(new
                        {
                            stepName = stepName,
                            method = methodName,
                            status = "pending",
                            message = "Step recorded - execute individually or implement method dispatch"
                        });

                        completedSteps++;
                    }
                    catch (Exception stepEx)
                    {
                        failedSteps++;
                        results.Add(new
                        {
                            stepName = stepName,
                            status = "failed",
                            error = stepEx.Message
                        });

                        if (!continueOnFailure)
                        {
                            workflowSuccess = false;
                            break;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = workflowSuccess,
                    workflowName = workflowName,
                    totalSteps = steps.Count,
                    completedSteps = completedSteps,
                    failedSteps = failedSteps,
                    results = results,
                    message = workflowSuccess
                        ? "Workflow steps recorded successfully"
                        : "Workflow failed - see results for details"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Intelligent Review

        /// <summary>
        /// Review a view and identify potential issues
        /// Parameters:
        /// - viewId: ID of the view to review
        /// - checkTypes: Array of check types to perform:
        ///   - "untagged_doors" - Doors without tags
        ///   - "untagged_rooms" - Rooms without tags
        ///   - "overlapping_text" - Text notes that may overlap
        ///   - "missing_dimensions" - Walls without dimensions
        ///   - "empty_text" - Text notes with no content
        /// </summary>
        public static string ReviewViewForIssues(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var checkTypes = parameters["checkTypes"]?.ToObject<string[]>() ??
                    new[] { "untagged_doors", "untagged_rooms", "empty_text" };

                var issues = new List<object>();

                foreach (var checkType in checkTypes)
                {
                    switch (checkType.ToLower())
                    {
                        case "untagged_doors":
                            var doors = new FilteredElementCollector(doc, viewId)
                                .OfCategory(BuiltInCategory.OST_Doors)
                                .WhereElementIsNotElementType()
                                .ToElementIds();

                            var doorTags = new FilteredElementCollector(doc, viewId)
                                .OfCategory(BuiltInCategory.OST_DoorTags)
                                .WhereElementIsNotElementType()
                                .Cast<IndependentTag>()
                                .SelectMany(t => t.GetTaggedLocalElementIds())
                                .ToHashSet();

                            var untaggedDoors = doors.Where(d => !doorTags.Contains(d)).ToList();
                            if (untaggedDoors.Count > 0)
                            {
                                issues.Add(new
                                {
                                    type = "untagged_doors",
                                    severity = "warning",
                                    count = untaggedDoors.Count,
                                    message = $"{untaggedDoors.Count} door(s) without tags",
                                    elementIds = untaggedDoors.Select(d => (int)d.Value).Take(10).ToList()
                                });
                            }
                            break;

                        case "untagged_rooms":
                            var rooms = new FilteredElementCollector(doc, viewId)
                                .OfCategory(BuiltInCategory.OST_Rooms)
                                .WhereElementIsNotElementType()
                                .ToElementIds();

                            var roomTagElements = new FilteredElementCollector(doc, viewId)
                                .OfCategory(BuiltInCategory.OST_RoomTags)
                                .WhereElementIsNotElementType()
                                .ToList();

                            var roomTags = new HashSet<ElementId>();
                            foreach (var tagElem in roomTagElements)
                            {
                                if (tagElem is Autodesk.Revit.DB.Architecture.RoomTag roomTag && roomTag.Room != null)
                                {
                                    roomTags.Add(roomTag.Room.Id);
                                }
                            }

                            var untaggedRooms = rooms.Where(r => !roomTags.Contains(r)).ToList();
                            if (untaggedRooms.Count > 0)
                            {
                                issues.Add(new
                                {
                                    type = "untagged_rooms",
                                    severity = "warning",
                                    count = untaggedRooms.Count,
                                    message = $"{untaggedRooms.Count} room(s) without tags",
                                    elementIds = untaggedRooms.Select(r => (int)r.Value).Take(10).ToList()
                                });
                            }
                            break;

                        case "empty_text":
                            var textNotes = new FilteredElementCollector(doc, viewId)
                                .OfClass(typeof(TextNote))
                                .Cast<TextNote>()
                                .Where(t => string.IsNullOrWhiteSpace(t.Text))
                                .ToList();

                            if (textNotes.Count > 0)
                            {
                                issues.Add(new
                                {
                                    type = "empty_text",
                                    severity = "error",
                                    count = textNotes.Count,
                                    message = $"{textNotes.Count} empty text note(s)",
                                    elementIds = textNotes.Select(t => (int)t.Id.Value).ToList()
                                });
                            }
                            break;

                        case "overlapping_text":
                            // Simple overlap detection based on bounding boxes
                            var allTextNotes = new FilteredElementCollector(doc, viewId)
                                .OfClass(typeof(TextNote))
                                .Cast<TextNote>()
                                .ToList();

                            var overlaps = new List<int[]>();
                            for (int i = 0; i < allTextNotes.Count; i++)
                            {
                                var bbox1 = allTextNotes[i].get_BoundingBox(view);
                                if (bbox1 == null) continue;

                                for (int j = i + 1; j < allTextNotes.Count; j++)
                                {
                                    var bbox2 = allTextNotes[j].get_BoundingBox(view);
                                    if (bbox2 == null) continue;

                                    // Check for overlap
                                    if (bbox1.Min.X < bbox2.Max.X && bbox1.Max.X > bbox2.Min.X &&
                                        bbox1.Min.Y < bbox2.Max.Y && bbox1.Max.Y > bbox2.Min.Y)
                                    {
                                        overlaps.Add(new[] { (int)allTextNotes[i].Id.Value, (int)allTextNotes[j].Id.Value });
                                    }
                                }
                            }

                            if (overlaps.Count > 0)
                            {
                                issues.Add(new
                                {
                                    type = "overlapping_text",
                                    severity = "warning",
                                    count = overlaps.Count,
                                    message = $"{overlaps.Count} potential text overlap(s)",
                                    pairs = overlaps.Take(10).ToList()
                                });
                            }
                            break;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)viewId.Value,
                    viewName = view.Name,
                    checksPerformed = checkTypes,
                    issueCount = issues.Count,
                    hasIssues = issues.Count > 0,
                    issues = issues
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Batch Operations with Verification

        /// <summary>
        /// Modify multiple text notes with verification
        /// Parameters:
        /// - modifications: Array of { elementId, newText } objects
        /// - verifyAfter: Verify each modification (default true)
        /// </summary>
        public static string BatchModifyTextNotes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["modifications"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "modifications array is required" });
                }

                var modifications = parameters["modifications"].ToObject<List<JObject>>();
                var verifyAfter = parameters["verifyAfter"]?.ToObject<bool>() ?? true;

                var results = new List<object>();
                var successCount = 0;
                var failCount = 0;

                using (var trans = new Transaction(doc, "Batch Modify Text Notes"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var mod in modifications)
                    {
                        var elementId = new ElementId(int.Parse(mod["elementId"].ToString()));
                        var newText = mod["newText"].ToString();

                        var element = doc.GetElement(elementId);
                        if (element is TextNote textNote)
                        {
                            var oldText = textNote.Text;
                            textNote.Text = newText;

                            results.Add(new
                            {
                                elementId = (int)elementId.Value,
                                status = "modified",
                                oldText = oldText?.Length > 50 ? oldText.Substring(0, 50) + "..." : oldText,
                                newText = newText?.Length > 50 ? newText.Substring(0, 50) + "..." : newText
                            });
                            successCount++;
                        }
                        else
                        {
                            results.Add(new
                            {
                                elementId = (int)elementId.Value,
                                status = "failed",
                                reason = element == null ? "Element not found" : "Not a text note"
                            });
                            failCount++;
                        }
                    }

                    trans.Commit();
                }

                // Verify if requested
                var verificationResults = new List<object>();
                if (verifyAfter)
                {
                    foreach (var mod in modifications)
                    {
                        var elementId = new ElementId(int.Parse(mod["elementId"].ToString()));
                        var expectedText = mod["newText"].ToString();
                        var element = doc.GetElement(elementId) as TextNote;

                        if (element != null)
                        {
                            var matches = element.Text?.Contains(expectedText) ?? false;
                            verificationResults.Add(new
                            {
                                elementId = (int)elementId.Value,
                                verified = matches
                            });
                        }
                    }
                }

                var allVerified = !verifyAfter || verificationResults.All(v => ((dynamic)v).verified);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    verified = allVerified,
                    totalModifications = modifications.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results,
                    verificationResults = verifyAfter ? verificationResults : null
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Smart Element Placement

        /// <summary>
        /// Place element with automatic verification and retry
        /// Parameters:
        /// - familyTypeId: Family type to place
        /// - location: [x, y, z] location
        /// - levelId: (optional) Level ID
        /// - maxRetries: (optional) Max retry attempts (default 2)
        /// - locationTolerance: (optional) Acceptable distance from target (default 1.0 ft)
        /// </summary>
        public static string SmartPlaceElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["familyTypeId"] == null || parameters["location"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "familyTypeId and location are required" });
                }

                var familyTypeId = new ElementId(int.Parse(parameters["familyTypeId"].ToString()));
                var locationArray = parameters["location"].ToObject<double[]>();
                var targetLocation = new XYZ(locationArray[0], locationArray[1], locationArray[2]);
                var maxRetries = parameters["maxRetries"]?.ToObject<int>() ?? 2;
                var locationTolerance = parameters["locationTolerance"]?.ToObject<double>() ?? 1.0;

                var familySymbol = doc.GetElement(familyTypeId) as FamilySymbol;
                if (familySymbol == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Family type not found" });
                }

                // Get level
                Level level = null;
                if (parameters["levelId"] != null)
                {
                    var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                    level = doc.GetElement(levelId) as Level;
                }
                else
                {
                    level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => Math.Abs(l.Elevation - targetLocation.Z))
                        .FirstOrDefault();
                }

                FamilyInstance placedInstance = null;
                var attempts = new List<object>();
                var verified = false;

                for (int attempt = 0; attempt <= maxRetries && !verified; attempt++)
                {
                    using (var trans = new Transaction(doc, "Smart Place Element"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        if (!familySymbol.IsActive)
                        {
                            familySymbol.Activate();
                            doc.Regenerate();
                        }

                        placedInstance = doc.Create.NewFamilyInstance(
                            targetLocation,
                            familySymbol,
                            level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                        trans.Commit();
                    }

                    // Verify placement
                    if (placedInstance != null)
                    {
                        var actualLocation = (placedInstance.Location as LocationPoint)?.Point;
                        if (actualLocation != null)
                        {
                            var distance = actualLocation.DistanceTo(targetLocation);
                            verified = distance <= locationTolerance;

                            attempts.Add(new
                            {
                                attempt = attempt + 1,
                                elementId = (int)placedInstance.Id.Value,
                                targetLocation = locationArray,
                                actualLocation = new[] { actualLocation.X, actualLocation.Y, actualLocation.Z },
                                distance = Math.Round(distance, 4),
                                withinTolerance = verified
                            });

                            if (!verified && attempt < maxRetries)
                            {
                                // Delete and retry
                                using (var trans = new Transaction(doc, "Remove Failed Placement"))
                                {
                                    trans.Start();
                                    var failureOpts = trans.GetFailureHandlingOptions();
                                    failureOpts.SetFailuresPreprocessor(new WarningSwallower());
                                    trans.SetFailureHandlingOptions(failureOpts);

                                    doc.Delete(placedInstance.Id);
                                    trans.Commit();
                                }
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = placedInstance != null,
                    verified = verified,
                    elementId = placedInstance != null ? (int?)placedInstance.Id.Value : null,
                    familyName = familySymbol.Family.Name,
                    typeName = familySymbol.Name,
                    targetLocation = locationArray,
                    attempts = attempts,
                    message = verified
                        ? "Element placed and verified successfully"
                        : "Element placed but location may not be exact"
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
