using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Code compliance validation methods for MCP Bridge
    /// Supports: Florida Building Code, Miami-Dade, ADA, Florida Residential Code
    /// </summary>
    public static class ComplianceMethods
    {
        #region Main Validation Methods

        /// <summary>
        /// Run all compliance checks on the current model
        /// </summary>
        [MCPMethod("runComplianceCheck", Category = "Compliance", Description = "Run all compliance checks on the current model")]
        public static string RunComplianceCheck(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var jurisdiction = parameters["jurisdiction"]?.ToString() ?? "FBC";
                var levelId = parameters["levelId"] != null
                    ? new ElementId(int.Parse(parameters["levelId"].ToString()))
                    : null;
                var checkTypes = parameters["checkTypes"]?.ToObject<string[]>()
                    ?? new[] { "all" };

                var results = new List<object>();
                var summary = new { passed = 0, warnings = 0, failures = 0 };
                int passed = 0, warnings = 0, failures = 0;

                // Run selected checks
                if (checkTypes.Contains("all") || checkTypes.Contains("egress"))
                {
                    var egressResults = CheckEgressRequirements(doc, levelId);
                    foreach (var r in egressResults)
                    {
                        results.Add(r);
                        if (r.Status == "PASS") passed++;
                        else if (r.Status == "WARNING") warnings++;
                        else failures++;
                    }
                }

                if (checkTypes.Contains("all") || checkTypes.Contains("accessibility"))
                {
                    var adaResults = CheckAccessibilityRequirements(doc, levelId);
                    foreach (var r in adaResults)
                    {
                        results.Add(r);
                        if (r.Status == "PASS") passed++;
                        else if (r.Status == "WARNING") warnings++;
                        else failures++;
                    }
                }

                if (checkTypes.Contains("all") || checkTypes.Contains("rooms"))
                {
                    var roomResults = CheckRoomRequirements(doc, levelId);
                    foreach (var r in roomResults)
                    {
                        results.Add(r);
                        if (r.Status == "PASS") passed++;
                        else if (r.Status == "WARNING") warnings++;
                        else failures++;
                    }
                }

                if (checkTypes.Contains("all") || checkTypes.Contains("doors"))
                {
                    var doorResults = CheckDoorRequirements(doc, levelId);
                    foreach (var r in doorResults)
                    {
                        results.Add(r);
                        if (r.Status == "PASS") passed++;
                        else if (r.Status == "WARNING") warnings++;
                        else failures++;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    jurisdiction = jurisdiction,
                    summary = new { passed, warnings, failures, total = passed + warnings + failures },
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check corridor widths against code requirements
        /// </summary>
        [MCPMethod("checkCorridorWidths", Category = "Compliance", Description = "Check corridor widths against code requirements")]
        public static string CheckCorridorWidths(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var minWidth = parameters["minWidth"] != null
                    ? double.Parse(parameters["minWidth"].ToString())
                    : 44.0; // Default 44" for occupant load > 50

                var results = new List<object>();

                // Get all rooms that are corridors/hallways
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0 &&
                           (r.Name.ToUpper().Contains("CORRIDOR") ||
                            r.Name.ToUpper().Contains("HALL") ||
                            r.Name.ToUpper().Contains("PASSAGE")))
                    .ToList();

                foreach (var room in rooms)
                {
                    var boundary = GetRoomBoundary(room);
                    var width = EstimateCorridorWidth(boundary);
                    var widthInches = width * 12;

                    var status = widthInches >= minWidth ? "PASS" : "FAIL";
                    results.Add(new
                    {
                        ruleId = "EGR-001",
                        ruleName = "Corridor Width",
                        elementId = (int)room.Id.Value,
                        elementName = room.Name,
                        level = room.Level?.Name,
                        measuredValue = Math.Round(widthInches, 1),
                        requiredValue = minWidth,
                        unit = "inches",
                        status = status,
                        message = status == "PASS"
                            ? $"Corridor width {widthInches:F1}\" meets minimum {minWidth}\""
                            : $"Corridor width {widthInches:F1}\" is less than required {minWidth}\""
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "corridor_width",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check door clear widths
        /// </summary>
        [MCPMethod("checkDoorWidths", Category = "Compliance", Description = "Check door clear widths against ADA and code requirements")]
        public static string CheckDoorWidths(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var minWidth = parameters["minWidth"] != null
                    ? double.Parse(parameters["minWidth"].ToString())
                    : 32.0; // ADA minimum 32" clear

                var levelId = parameters["levelId"] != null
                    ? new ElementId(int.Parse(parameters["levelId"].ToString()))
                    : null;

                var results = new List<object>();

                var collector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType();

                var doors = levelId != null
                    ? collector.Where(d => d.LevelId == levelId).ToList()
                    : collector.ToList();

                foreach (var door in doors)
                {
                    var fi = door as FamilyInstance;
                    if (fi == null) continue;

                    // Get door width from type
                    var doorType = doc.GetElement(fi.GetTypeId()) as FamilySymbol;
                    var widthParam = doorType?.get_Parameter(BuiltInParameter.DOOR_WIDTH);
                    var width = widthParam?.AsDouble() ?? 0;
                    var widthInches = width * 12;

                    // Clear width is typically frame width minus 2" (rough estimate)
                    var clearWidth = widthInches - 2;

                    var status = clearWidth >= minWidth ? "PASS" : "FAIL";
                    var level = doc.GetElement(fi.LevelId) as Level;

                    results.Add(new
                    {
                        ruleId = "EGR-002",
                        ruleName = "Door Clear Width",
                        elementId = (int)door.Id.Value,
                        elementName = doorType?.Name ?? "Unknown",
                        mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        level = level?.Name,
                        measuredValue = Math.Round(clearWidth, 1),
                        requiredValue = minWidth,
                        unit = "inches",
                        status = status,
                        message = status == "PASS"
                            ? $"Door clear width ~{clearWidth:F0}\" meets minimum {minWidth}\""
                            : $"Door clear width ~{clearWidth:F0}\" is less than required {minWidth}\""
                    });
                }

                var passed = results.Count(r => ((dynamic)r).status == "PASS");
                var failed = results.Count - passed;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "door_width",
                    summary = new { passed, failed, total = results.Count },
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check room areas against minimums
        /// </summary>
        [MCPMethod("checkRoomAreas", Category = "Compliance", Description = "Check room areas against minimum code requirements")]
        public static string CheckRoomAreas(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var minArea = parameters["minArea"] != null
                    ? double.Parse(parameters["minArea"].ToString())
                    : 70.0; // FRC minimum 70 sf for habitable

                var levelId = parameters["levelId"] != null
                    ? new ElementId(int.Parse(parameters["levelId"].ToString()))
                    : null;

                var results = new List<object>();

                var collector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType();

                var rooms = collector.Cast<Room>().Where(r => r.Area > 0).ToList();

                if (levelId != null)
                {
                    rooms = rooms.Where(r => r.LevelId == levelId).ToList();
                }

                foreach (var room in rooms)
                {
                    var areaSF = room.Area; // Already in square feet
                    var isHabitable = IsHabitableRoom(room.Name);
                    var requiredMin = isHabitable ? minArea : 0;

                    string status;
                    if (!isHabitable)
                        status = "N/A";
                    else if (areaSF >= requiredMin)
                        status = "PASS";
                    else
                        status = "FAIL";

                    results.Add(new
                    {
                        ruleId = "RSZ-001",
                        ruleName = "Room Minimum Area",
                        elementId = (int)room.Id.Value,
                        roomName = room.Name,
                        roomNumber = room.Number,
                        level = room.Level?.Name,
                        measuredValue = Math.Round(areaSF, 1),
                        requiredValue = requiredMin,
                        unit = "sf",
                        isHabitable = isHabitable,
                        status = status,
                        message = status == "PASS"
                            ? $"Room area {areaSF:F0} sf meets minimum"
                            : status == "N/A"
                            ? "Non-habitable room - no minimum required"
                            : $"Room area {areaSF:F0} sf is below minimum {requiredMin} sf"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "room_area",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check ADA toilet clearances
        /// </summary>
        [MCPMethod("checkToiletClearances", Category = "Compliance", Description = "Check ADA toilet clearance requirements in bathroom spaces")]
        public static string CheckToiletClearances(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var results = new List<object>();

                // Get all plumbing fixtures (toilets)
                var toilets = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol.FamilyName.ToUpper().Contains("TOILET") ||
                                 fi.Symbol.FamilyName.ToUpper().Contains("WATER CLOSET") ||
                                 fi.Symbol.FamilyName.ToUpper().Contains("WC"))
                    .ToList();

                foreach (var toilet in toilets)
                {
                    var location = (toilet.Location as LocationPoint)?.Point;
                    if (location == null) continue;

                    // Find nearest wall to check centerline distance
                    var nearestWallDist = FindNearestWallDistance(doc, location);
                    var distInches = nearestWallDist * 12;

                    // ADA requires 16" - 18" from side wall centerline
                    string status;
                    if (distInches >= 16 && distInches <= 18)
                        status = "PASS";
                    else if (distInches >= 15 && distInches <= 19)
                        status = "WARNING";
                    else
                        status = "FAIL";

                    var level = doc.GetElement(toilet.LevelId) as Level;

                    results.Add(new
                    {
                        ruleId = "ADA-003",
                        ruleName = "Toilet Centerline to Wall",
                        elementId = (int)toilet.Id.Value,
                        elementName = toilet.Symbol.FamilyName,
                        level = level?.Name,
                        measuredValue = Math.Round(distInches, 1),
                        requiredMin = 16,
                        requiredMax = 18,
                        unit = "inches",
                        status = status,
                        message = status == "PASS"
                            ? $"Toilet centerline {distInches:F1}\" is within 16\"-18\" requirement"
                            : status == "WARNING"
                            ? $"Toilet centerline {distInches:F1}\" is close to limits (16\"-18\")"
                            : $"Toilet centerline {distInches:F1}\" does not meet 16\"-18\" requirement"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "toilet_clearance",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check door swing direction (egress direction check)
        /// </summary>
        [MCPMethod("checkDoorSwing", Category = "Compliance", Description = "Check door swing direction for egress compliance")]
        public static string CheckDoorSwing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var results = new List<object>();

                var levelId = parameters["levelId"] != null
                    ? new ElementId(int.Parse(parameters["levelId"].ToString()))
                    : null;

                var collector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType();

                var doors = levelId != null
                    ? collector.Where(d => d.LevelId == levelId).ToList()
                    : collector.ToList();

                foreach (var door in doors)
                {
                    var fi = door as FamilyInstance;
                    if (fi == null) continue;

                    var fromRoom = fi.FromRoom;
                    var toRoom = fi.ToRoom;
                    var level = doc.GetElement(fi.LevelId) as Level;
                    var mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();

                    // Determine if door should swing in direction of egress
                    // Egress typically goes FROM occupied space TO corridor/exit
                    var fromRoomName = fromRoom?.Name ?? "Exterior";
                    var toRoomName = toRoom?.Name ?? "Exterior";

                    var swingsToEgress = IsEgressDirection(fromRoomName, toRoomName);

                    string status;
                    string message;

                    if (swingsToEgress)
                    {
                        status = "PASS";
                        message = $"Door swings toward egress path ({fromRoomName} → {toRoomName})";
                    }
                    else
                    {
                        // Check if this door serves > 50 occupants (would require egress swing)
                        // For now, mark as warning for review
                        status = "WARNING";
                        message = $"Door swings away from typical egress ({toRoomName} → {fromRoomName}) - verify occupant load";
                    }

                    results.Add(new
                    {
                        ruleId = "EGR-005",
                        ruleName = "Door Swing Direction",
                        elementId = (int)door.Id.Value,
                        mark = mark,
                        level = level?.Name,
                        fromRoom = fromRoomName,
                        toRoom = toRoomName,
                        status = status,
                        message = message
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "door_swing",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check stair dimensions against code requirements
        /// </summary>
        [MCPMethod("checkStairDimensions", Category = "Compliance", Description = "Check stair dimensions including riser height and tread depth against code")]
        public static string CheckStairDimensions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var results = new List<object>();

                // Get all stairs
                var stairs = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (var stair in stairs)
                {
                    var stairElement = stair as Autodesk.Revit.DB.Architecture.Stairs;
                    if (stairElement == null) continue;

                    // Get stair parameters
                    var riserHeight = stairElement.ActualRiserHeight * 12; // Convert to inches
                    var treadDepth = stairElement.ActualTreadDepth * 12;

                    // Get width from stair runs
                    double stairWidth = 36.0; // Default 36" if can't determine
                    var stairRuns = stairElement.GetStairsRuns();
                    if (stairRuns.Count > 0)
                    {
                        var firstRun = doc.GetElement(stairRuns.First()) as StairsRun;
                        if (firstRun != null)
                        {
                            stairWidth = firstRun.ActualRunWidth * 12; // Convert to inches
                        }
                    }

                    var issues = new List<string>();
                    var status = "PASS";

                    // Check riser height (4" min, 7" max per FBC)
                    if (riserHeight < 4 || riserHeight > 7)
                    {
                        issues.Add($"Riser {riserHeight:F2}\" outside 4\"-7\" range");
                        status = "FAIL";
                    }

                    // Check tread depth (11" min per FBC)
                    if (treadDepth < 11)
                    {
                        issues.Add($"Tread {treadDepth:F2}\" below 11\" minimum");
                        status = "FAIL";
                    }

                    // Check width (36" min, 44" for OL > 50)
                    if (stairWidth < 36)
                    {
                        issues.Add($"Width {stairWidth:F1}\" below 36\" minimum");
                        status = "FAIL";
                    }
                    else if (stairWidth < 44)
                    {
                        issues.Add($"Width {stairWidth:F1}\" - verify occupant load ≤ 50");
                        if (status != "FAIL") status = "WARNING";
                    }

                    results.Add(new
                    {
                        ruleId = "STR-001",
                        ruleName = "Stair Dimensions",
                        elementId = (int)stair.Id.Value,
                        elementName = stair.Name,
                        riserHeight = Math.Round(riserHeight, 2),
                        treadDepth = Math.Round(treadDepth, 2),
                        width = Math.Round(stairWidth, 1),
                        status = status,
                        issues = issues,
                        message = status == "PASS"
                            ? "Stair dimensions meet code requirements"
                            : string.Join("; ", issues)
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "stair_dimensions",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check ceiling heights against code requirements
        /// </summary>
        [MCPMethod("checkCeilingHeights", Category = "Compliance", Description = "Check ceiling heights against minimum code requirements")]
        public static string CheckCeilingHeights(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var results = new List<object>();

                var levelId = parameters["levelId"] != null
                    ? new ElementId(int.Parse(parameters["levelId"].ToString()))
                    : null;

                // Get all rooms
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0);

                if (levelId != null)
                    rooms = rooms.Where(r => r.LevelId == levelId);

                foreach (var room in rooms)
                {
                    // Get unbounded height parameter
                    var heightParam = room.get_Parameter(BuiltInParameter.ROOM_HEIGHT);
                    var heightFeet = heightParam?.AsDouble() ?? 0;
                    var heightInches = heightFeet * 12;

                    var roomName = room.Name.ToUpper();
                    double requiredHeight;
                    string roomType;

                    // Determine required height based on room type
                    if (roomName.Contains("BATH") || roomName.Contains("TOILET") ||
                        roomName.Contains("LAUNDRY") || roomName.Contains("UTILITY"))
                    {
                        requiredHeight = 80; // 6'-8"
                        roomType = "Service";
                    }
                    else if (roomName.Contains("MECH") || roomName.Contains("ELEC") ||
                             roomName.Contains("STORAGE") || roomName.Contains("CLOSET"))
                    {
                        requiredHeight = 0; // No requirement
                        roomType = "Non-habitable";
                    }
                    else
                    {
                        requiredHeight = 90; // 7'-6" for habitable (commercial)
                        roomType = "Habitable";
                    }

                    string status;
                    if (requiredHeight == 0)
                        status = "N/A";
                    else if (heightInches >= requiredHeight)
                        status = "PASS";
                    else
                        status = "FAIL";

                    results.Add(new
                    {
                        ruleId = "RSZ-002",
                        ruleName = "Ceiling Height",
                        elementId = (int)room.Id.Value,
                        roomName = room.Name,
                        roomNumber = room.Number,
                        level = room.Level?.Name,
                        roomType = roomType,
                        measuredValue = Math.Round(heightInches, 1),
                        requiredValue = requiredHeight,
                        unit = "inches",
                        status = status,
                        message = status == "PASS"
                            ? $"Ceiling height {heightFeet:F1}' meets {requiredHeight / 12:F1}' minimum"
                            : status == "N/A"
                            ? "Non-habitable - no height requirement"
                            : $"Ceiling height {heightFeet:F1}' below {requiredHeight / 12:F1}' minimum"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "ceiling_height",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check wall fire ratings
        /// </summary>
        [MCPMethod("checkWallFireRatings", Category = "Compliance", Description = "Check wall fire ratings against required ratings for occupancy and construction type")]
        public static string CheckWallFireRatings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var results = new List<object>();

                // Get all walls
                var walls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                foreach (var wall in walls)
                {
                    var wallType = doc.GetElement(wall.GetTypeId()) as WallType;
                    var wallTypeName = wallType?.Name ?? "Unknown";

                    // Check if wall type name indicates fire rating
                    var hasRating = wallTypeName.ToUpper().Contains("FIRE") ||
                                   wallTypeName.Contains("1-HR") || wallTypeName.Contains("1 HR") ||
                                   wallTypeName.Contains("2-HR") || wallTypeName.Contains("2 HR") ||
                                   wallTypeName.ToUpper().Contains("RATED");

                    // Check if wall is at corridor or shaft (should be rated)
                    var shouldBeRated = wallTypeName.ToUpper().Contains("CORRIDOR") ||
                                       wallTypeName.ToUpper().Contains("SHAFT") ||
                                       wallTypeName.ToUpper().Contains("STAIR") ||
                                       wallTypeName.ToUpper().Contains("EXIT");

                    string status;
                    string message;

                    if (shouldBeRated && !hasRating)
                    {
                        status = "WARNING";
                        message = $"Wall type '{wallTypeName}' may require fire rating - verify";
                    }
                    else if (hasRating)
                    {
                        status = "PASS";
                        message = $"Wall type '{wallTypeName}' appears to have fire rating";
                    }
                    else
                    {
                        status = "N/A";
                        message = "Standard wall - no rating required";
                    }

                    // Only include walls that have potential rating issues
                    if (status != "N/A")
                    {
                        var level = doc.GetElement(wall.LevelId) as Level;
                        results.Add(new
                        {
                            ruleId = "FIR-001",
                            ruleName = "Wall Fire Rating",
                            elementId = (int)wall.Id.Value,
                            wallType = wallTypeName,
                            level = level?.Name,
                            hasRatingIndicator = hasRating,
                            shouldBeRated = shouldBeRated,
                            status = status,
                            message = message
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    checkType = "wall_fire_rating",
                    count = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Generate a full compliance report
        /// </summary>
        [MCPMethod("generateComplianceReport", Category = "Compliance", Description = "Generate a comprehensive compliance report for the model")]
        public static string GenerateComplianceReport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var jurisdiction = parameters["jurisdiction"]?.ToString() ?? "FBC";

                var report = new
                {
                    projectName = doc.Title,
                    projectNumber = doc.ProjectInformation?.Number,
                    jurisdiction = jurisdiction,
                    generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    sections = new List<object>()
                };

                var sections = (List<object>)report.sections;

                // Run all checks and compile results
                var corridorCheck = JsonConvert.DeserializeObject<dynamic>(
                    CheckCorridorWidths(uiApp, new JObject()));
                var doorCheck = JsonConvert.DeserializeObject<dynamic>(
                    CheckDoorWidths(uiApp, new JObject()));
                var roomCheck = JsonConvert.DeserializeObject<dynamic>(
                    CheckRoomAreas(uiApp, new JObject()));
                var ceilingCheck = JsonConvert.DeserializeObject<dynamic>(
                    CheckCeilingHeights(uiApp, new JObject()));
                var stairCheck = JsonConvert.DeserializeObject<dynamic>(
                    CheckStairDimensions(uiApp, new JObject()));

                int totalPassed = 0, totalWarnings = 0, totalFailed = 0;

                // Count results
                void CountResults(dynamic check)
                {
                    if (check?.results == null) return;
                    foreach (var r in check.results)
                    {
                        string status = r.status?.ToString() ?? "N/A";
                        if (status == "PASS") totalPassed++;
                        else if (status == "WARNING") totalWarnings++;
                        else if (status == "FAIL") totalFailed++;
                    }
                }

                CountResults(corridorCheck);
                CountResults(doorCheck);
                CountResults(roomCheck);
                CountResults(ceilingCheck);
                CountResults(stairCheck);

                sections.Add(new { category = "Egress - Corridors", results = corridorCheck?.results });
                sections.Add(new { category = "Egress - Doors", results = doorCheck?.results });
                sections.Add(new { category = "Room Areas", results = roomCheck?.results });
                sections.Add(new { category = "Ceiling Heights", results = ceilingCheck?.results });
                sections.Add(new { category = "Stairs", results = stairCheck?.results });

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    report = new
                    {
                        projectName = doc.Title,
                        projectNumber = doc.ProjectInformation?.Number,
                        jurisdiction = jurisdiction,
                        generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        summary = new
                        {
                            passed = totalPassed,
                            warnings = totalWarnings,
                            failed = totalFailed,
                            total = totalPassed + totalWarnings + totalFailed,
                            complianceRate = totalPassed + totalWarnings + totalFailed > 0
                                ? Math.Round((double)totalPassed / (totalPassed + totalWarnings + totalFailed) * 100, 1)
                                : 100
                        },
                        sections = sections
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static List<ComplianceResult> CheckEgressRequirements(Document doc, ElementId levelId)
        {
            var results = new List<ComplianceResult>();

            // Check corridor widths
            var corridors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0 &&
                       (r.Name.ToUpper().Contains("CORRIDOR") || r.Name.ToUpper().Contains("HALL")));

            if (levelId != null)
                corridors = corridors.Where(r => r.LevelId == levelId);

            foreach (var corridor in corridors)
            {
                var boundary = GetRoomBoundary(corridor);
                var width = EstimateCorridorWidth(boundary) * 12; // Convert to inches

                results.Add(new ComplianceResult
                {
                    RuleId = "EGR-001",
                    RuleName = "Corridor Width",
                    ElementId = (int)corridor.Id.Value,
                    ElementName = corridor.Name,
                    MeasuredValue = width,
                    RequiredValue = 44,
                    Unit = "inches",
                    Status = width >= 44 ? "PASS" : "FAIL",
                    Message = width >= 44
                        ? $"Corridor {corridor.Name} width {width:F0}\" OK"
                        : $"Corridor {corridor.Name} width {width:F0}\" < 44\" minimum"
                });
            }

            return results;
        }

        private static List<ComplianceResult> CheckAccessibilityRequirements(Document doc, ElementId levelId)
        {
            var results = new List<ComplianceResult>();

            // Check door widths for ADA
            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>();

            if (levelId != null)
                doors = doors.Where(d => d.LevelId == levelId);

            foreach (var door in doors)
            {
                var doorType = doc.GetElement(door.GetTypeId()) as FamilySymbol;
                var widthParam = doorType?.get_Parameter(BuiltInParameter.DOOR_WIDTH);
                var width = (widthParam?.AsDouble() ?? 0) * 12 - 2; // Clear width estimate

                results.Add(new ComplianceResult
                {
                    RuleId = "ADA-002",
                    RuleName = "Door Clear Width",
                    ElementId = (int)door.Id.Value,
                    ElementName = doorType?.Name ?? "Door",
                    MeasuredValue = width,
                    RequiredValue = 32,
                    Unit = "inches",
                    Status = width >= 32 ? "PASS" : "FAIL",
                    Message = width >= 32
                        ? $"Door clear width {width:F0}\" OK"
                        : $"Door clear width {width:F0}\" < 32\" ADA minimum"
                });
            }

            return results;
        }

        private static List<ComplianceResult> CheckRoomRequirements(Document doc, ElementId levelId)
        {
            var results = new List<ComplianceResult>();

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0);

            if (levelId != null)
                rooms = rooms.Where(r => r.LevelId == levelId);

            foreach (var room in rooms)
            {
                if (!IsHabitableRoom(room.Name)) continue;

                var area = room.Area;
                results.Add(new ComplianceResult
                {
                    RuleId = "RSZ-001",
                    RuleName = "Habitable Room Area",
                    ElementId = (int)room.Id.Value,
                    ElementName = room.Name,
                    MeasuredValue = area,
                    RequiredValue = 70,
                    Unit = "sf",
                    Status = area >= 70 ? "PASS" : "FAIL",
                    Message = area >= 70
                        ? $"Room {room.Name} area {area:F0} sf OK"
                        : $"Room {room.Name} area {area:F0} sf < 70 sf minimum"
                });
            }

            return results;
        }

        private static List<ComplianceResult> CheckDoorRequirements(Document doc, ElementId levelId)
        {
            // Placeholder - implemented in CheckDoorSwing
            return new List<ComplianceResult>();
        }

        private static List<XYZ> GetRoomBoundary(Room room)
        {
            var points = new List<XYZ>();
            try
            {
                var options = new SpatialElementBoundaryOptions();
                var boundaries = room.GetBoundarySegments(options);
                if (boundaries != null && boundaries.Count > 0)
                {
                    foreach (var segment in boundaries[0])
                    {
                        var curve = segment.GetCurve();
                        points.Add(curve.GetEndPoint(0));
                    }
                }
            }
            catch { }
            return points;
        }

        private static double EstimateCorridorWidth(List<XYZ> boundary)
        {
            if (boundary.Count < 4) return 0;

            // Find the minimum dimension (width of corridor)
            double minDist = double.MaxValue;

            for (int i = 0; i < boundary.Count; i++)
            {
                for (int j = i + 2; j < boundary.Count; j++)
                {
                    var dist = boundary[i].DistanceTo(boundary[j]);
                    if (dist < minDist && dist > 0.1) // Avoid diagonal measurements
                        minDist = dist;
                }
            }

            return minDist == double.MaxValue ? 0 : minDist;
        }

        private static bool IsHabitableRoom(string roomName)
        {
            var upper = roomName.ToUpper();
            var nonHabitable = new[] { "MECH", "ELEC", "STORAGE", "CLOSET", "SHAFT", "CHASE", "STAIR" };
            return !nonHabitable.Any(n => upper.Contains(n));
        }

        private static double FindNearestWallDistance(Document doc, XYZ point)
        {
            var walls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            double minDist = double.MaxValue;
            foreach (var wall in walls)
            {
                var curve = (wall.Location as LocationCurve)?.Curve;
                if (curve == null) continue;

                var result = curve.Project(point);
                if (result != null && result.Distance < minDist)
                    minDist = result.Distance;
            }

            return minDist == double.MaxValue ? 0 : minDist;
        }

        private static bool IsEgressDirection(string fromRoom, string toRoom)
        {
            var egressSpaces = new[] { "CORRIDOR", "HALL", "LOBBY", "EXIT", "STAIR", "EXTERIOR" };
            var toUpper = toRoom.ToUpper();
            return egressSpaces.Any(e => toUpper.Contains(e));
        }

        #endregion

        #region Result Classes

        private class ComplianceResult
        {
            public string RuleId { get; set; }
            public string RuleName { get; set; }
            public int ElementId { get; set; }
            public string ElementName { get; set; }
            public double MeasuredValue { get; set; }
            public double RequiredValue { get; set; }
            public string Unit { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
        }

        #endregion
    }
}
