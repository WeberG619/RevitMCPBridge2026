using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Room and space creation, modification, and management methods for MCP Bridge
    /// </summary>
    public static class RoomMethods
    {
        /// <summary>
        /// Create a room at a point
        /// </summary>
        [MCPMethod("createRoom", Category = "Room", Description = "Create a room at a specified point on a level")]
        public static string CreateRoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Validate UIApplication and document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document open in Revit", "NO_ACTIVE_DOCUMENT").Build();
                }
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                var v = new ParameterValidator(parameters, "CreateRoom");
                v.Require("location");
                v.Require("levelId").IsType<int>();
                v.ThrowIfInvalid();

                var location = parameters["location"].ToObject<double[]>();
                if (location == null || location.Length < 2)
                {
                    return ResponseBuilder.Error("location must be an array with at least 2 values [x, y]", "INVALID_PARAMETER").Build();
                }

                var levelIdInt = v.GetRequired<int>("levelId");
                var name = v.GetOptional<string>("name");
                var number = v.GetOptional<string>("number");

                var level = ElementLookup.GetLevel(doc, levelIdInt);

                using (var trans = new Transaction(doc, "Create Room"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create room at point
                    var point = new UV(location[0], location[1]);
                    var room = doc.Create.NewRoom(level, point);

                    if (room == null)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Failed to create room - point may not be in an enclosed area", "ROOM_CREATION_FAILED").Build();
                    }

                    // Set room properties
                    if (!string.IsNullOrEmpty(name))
                    {
                        room.Name = name;
                    }

                    if (!string.IsNullOrEmpty(number))
                    {
                        room.Number = number;
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
                        .With("name", room.Name)
                        .With("number", room.Number)
                        .With("area", room.Area)
                        .With("perimeter", room.Perimeter)
                        .With("volume", room.Volume)
                        .With("level", level.Name)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get room information
        /// </summary>
        [MCPMethod("getRoomInfo", Category = "Room", Description = "Get detailed information about a specific room")]
        public static string GetRoomInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetRoomInfo");
                v.Require("roomId").IsType<int>();
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                var level = ElementLookup.TryGetElement<Level>(doc, (int)room.LevelId.Value);

                // Get room boundaries
                var boundaries = new List<object>();
                var options = new SpatialElementBoundaryOptions();
                var segments = room.GetBoundarySegments(options);

                if (segments != null)
                {
                    foreach (var segmentList in segments)
                    {
                        var boundaryLoop = new List<object>();
                        foreach (var segment in segmentList)
                        {
                            var curve = segment.GetCurve();
                            boundaryLoop.Add(new
                            {
                                startPoint = new[] { curve.GetEndPoint(0).X, curve.GetEndPoint(0).Y, curve.GetEndPoint(0).Z },
                                endPoint = new[] { curve.GetEndPoint(1).X, curve.GetEndPoint(1).Y, curve.GetEndPoint(1).Z },
                                length = curve.Length
                            });
                        }
                        boundaries.Add(boundaryLoop);
                    }
                }

                // Get room location point
                var locationPoint = (room.Location as LocationPoint)?.Point;

                return ResponseBuilder.Success()
                    .With("roomId", (int)room.Id.Value)
                    .With("name", room.Name)
                    .With("number", room.Number)
                    .With("area", room.Area)
                    .With("perimeter", room.Perimeter)
                    .With("volume", room.Volume)
                    .With("unboundedHeight", room.UnboundedHeight)
                    .With("level", level?.Name)
                    .With("levelId", (int)room.LevelId.Value)
                    .With("location", locationPoint != null ? new[] { locationPoint.X, locationPoint.Y, locationPoint.Z } : null)
                    .With("department", room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString())
                    .With("comments", room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                    .With("boundaries", boundaries)
                    .With("boundaryCount", segments?.Count ?? 0)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify room properties
        /// </summary>
        [MCPMethod("modifyRoomProperties", Category = "Room", Description = "Modify properties of an existing room")]
        public static string ModifyRoomProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "ModifyRoomProperties");
                v.Require("roomId").IsType<int>();
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                using (var trans = new Transaction(doc, "Modify Room Properties"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modified = new List<string>();

                    // Change name
                    if (parameters["name"] != null)
                    {
                        room.Name = parameters["name"].ToString();
                        modified.Add("name");
                    }

                    // Change number
                    if (parameters["number"] != null)
                    {
                        room.Number = parameters["number"].ToString();
                        modified.Add("number");
                    }

                    // Change department
                    if (parameters["department"] != null)
                    {
                        var deptParam = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                        if (deptParam != null && !deptParam.IsReadOnly)
                        {
                            deptParam.Set(parameters["department"].ToString());
                            modified.Add("department");
                        }
                    }

                    // Change comments
                    if (parameters["comments"] != null)
                    {
                        var commentsParam = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (commentsParam != null && !commentsParam.IsReadOnly)
                        {
                            commentsParam.Set(parameters["comments"].ToString());
                            modified.Add("comments");
                        }
                    }

                    // Change base offset
                    if (parameters["baseOffset"] != null)
                    {
                        var offsetParam = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET);
                        if (offsetParam != null && !offsetParam.IsReadOnly)
                        {
                            offsetParam.Set(v.GetOptional<double>("baseOffset"));
                            modified.Add("baseOffset");
                        }
                    }

                    // Change upper limit
                    if (parameters["upperLimit"] != null)
                    {
                        var limitParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                        if (limitParam != null && !limitParam.IsReadOnly)
                        {
                            limitParam.Set(v.GetOptional<double>("upperLimit"));
                            modified.Add("upperLimit");
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
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
        /// Place a room tag
        /// </summary>
        [MCPMethod("placeRoomTag", Category = "Room", Description = "Place a room tag annotation in a view")]
        public static string PlaceRoomTag(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "PlaceRoomTag");
                v.Require("roomId").IsType<int>();
                v.Require("viewId").IsType<int>();
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var viewIdInt = v.GetRequired<int>("viewId");

                var room = ElementLookup.GetRoom(doc, roomIdInt);
                var view = ElementLookup.GetView(doc, viewIdInt);

                var roomElementId = new ElementId(roomIdInt);
                var viewElementId = new ElementId(viewIdInt);

                using (var trans = new Transaction(doc, "Place Room Tag"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get room location or use provided location
                    UV tagLocation;
                    if (parameters["location"] != null)
                    {
                        var loc = parameters["location"].ToObject<double[]>();
                        tagLocation = new UV(loc[0], loc[1]);
                    }
                    else
                    {
                        var roomLoc = (room.Location as LocationPoint)?.Point;
                        if (roomLoc == null)
                        {
                            trans.RollBack();
                            return ResponseBuilder.Error("Cannot determine room location for tag", "INVALID_ROOM_LOCATION").Build();
                        }
                        tagLocation = new UV(roomLoc.X, roomLoc.Y);
                    }

                    var tag = doc.Create.NewRoomTag(new LinkElementId(roomElementId), tagLocation, viewElementId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("tagId", (int)tag.Id.Value)
                        .With("roomId", roomIdInt)
                        .With("viewId", viewIdInt)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all rooms in a view or level
        /// </summary>
        [MCPMethod("getRooms", Category = "Room", Description = "Get all rooms in the model, optionally filtered by level or view")]
        public static string GetRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Check if UIApplication and active document are available
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document open in Revit", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "GetRooms");

                // Get all rooms using category filter (most reliable)
                var allRooms = ElementLookup.GetAllRooms(doc)
                    .Where(r => r != null && r.Area > 0); // Only bounded rooms

                // Apply optional level filter
                if (parameters != null && parameters["levelId"] != null)
                {
                    var levelIdInt = v.GetOptional<int>("levelId");
                    var levelElementId = new ElementId(levelIdInt);
                    allRooms = allRooms.Where(r => r.LevelId == levelElementId);
                }

                var roomList = new List<object>();
                foreach (var r in allRooms)
                {
                    try
                    {
                        var levelName = "";
                        var levelElement = doc.GetElement(r.LevelId);
                        if (levelElement != null)
                        {
                            levelName = levelElement.Name;
                        }

                        var deptParam = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                        var department = deptParam != null ? deptParam.AsString() : "";

                        roomList.Add(new
                        {
                            roomId = (int)r.Id.Value,
                            name = r.Name ?? "",
                            number = r.Number ?? "",
                            area = r.Area,
                            perimeter = r.Perimeter,
                            volume = r.Volume,
                            level = levelName,
                            levelId = (int)r.LevelId.Value,
                            department = department
                        });
                    }
                    catch
                    {
                        // Skip problematic rooms
                        continue;
                    }
                }

                return ResponseBuilder.Success()
                    .With("roomCount", roomList.Count)
                    .With("rooms", roomList)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set a room's number property. Uses Room.Number property directly for reliable updates.
        /// </summary>
        [MCPMethod("setRoomNumber", Category = "Room", Description = "Set the number property of a room")]
        public static string SetRoomNumber(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "SetRoomNumber");
                v.Require("roomId").IsType<int>();
                v.Require("number");
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var newNumber = v.GetRequired<string>("number");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                var oldNumber = room.Number;

                using (var trans = new Transaction(doc, "Set Room Number"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    room.Number = newNumber;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
                        .With("oldNumber", oldNumber)
                        .With("newNumber", room.Number)
                        .With("name", room.Name)
                        .WithMessage($"Room number changed from '{oldNumber}' to '{room.Number}'")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set room name property
        /// </summary>
        [MCPMethod("setRoomName", Category = "Room", Description = "Set the name property of a room")]
        public static string SetRoomName(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "SetRoomName");
                v.Require("roomId").IsType<int>();
                v.Require("name");
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var newName = v.GetRequired<string>("name");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                var oldName = room.Name;

                using (var trans = new Transaction(doc, "Set Room Name"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    room.Name = newName;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
                        .With("oldName", oldName)
                        .With("newName", room.Name)
                        .With("number", room.Number)
                        .WithMessage($"Room name changed from '{oldName}' to '{room.Name}'")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set room department property
        /// </summary>
        [MCPMethod("setRoomDepartment", Category = "Room", Description = "Set the department property of a room")]
        public static string SetRoomDepartment(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "SetRoomDepartment");
                v.Require("roomId").IsType<int>();
                v.Require("department");
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var newDepartment = v.GetRequired<string>("department");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                var deptParam = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                if (deptParam == null)
                {
                    return ResponseBuilder.Error("Department parameter not found", "PARAMETER_NOT_FOUND").Build();
                }

                var oldDepartment = deptParam.AsString() ?? "";

                using (var trans = new Transaction(doc, "Set Room Department"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    deptParam.Set(newDepartment);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
                        .With("oldDepartment", oldDepartment)
                        .With("newDepartment", newDepartment)
                        .With("name", room.Name)
                        .With("number", room.Number)
                        .WithMessage($"Room department changed from '{oldDepartment}' to '{newDepartment}'")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set room comments property
        /// </summary>
        [MCPMethod("setRoomComments", Category = "Room", Description = "Set the comments property of a room")]
        public static string SetRoomComments(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "SetRoomComments");
                v.Require("roomId").IsType<int>();
                v.Require("comments");
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var newComments = v.GetRequired<string>("comments");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                var commentsParam = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (commentsParam == null)
                {
                    return ResponseBuilder.Error("Comments parameter not found", "PARAMETER_NOT_FOUND").Build();
                }

                var oldComments = commentsParam.AsString() ?? "";

                using (var trans = new Transaction(doc, "Set Room Comments"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    commentsParam.Set(newComments);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
                        .With("oldComments", oldComments)
                        .With("newComments", newComments)
                        .With("name", room.Name)
                        .With("number", room.Number)
                        .WithMessage("Room comments updated")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get rooms filtered by level
        /// </summary>
        [MCPMethod("getRoomsByLevel", Category = "Room", Description = "Get all rooms on a specific level")]
        public static string GetRoomsByLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetRoomsByLevel");
                // At least one of levelId or levelName required
                if (parameters["levelId"] == null && parameters["levelName"] == null)
                {
                    return ResponseBuilder.Error("levelId or levelName is required", "MISSING_PARAMETER").Build();
                }

                Level targetLevel = null;

                if (parameters["levelId"] != null)
                {
                    var levelId = new ElementId(parameters["levelId"].ToObject<int>());
                    targetLevel = doc.GetElement(levelId) as Level;
                }
                else if (parameters["levelName"] != null)
                {
                    var levelName = parameters["levelName"].ToString();
                    targetLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                }

                if (targetLevel == null)
                {
                    return ResponseBuilder.Error("Level not found", "ELEMENT_NOT_FOUND").Build();
                }

                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r != null && r.Area > 0 && r.LevelId == targetLevel.Id)
                    .Select(r => new
                    {
                        roomId = (int)r.Id.Value,
                        name = r.Name ?? "",
                        number = r.Number ?? "",
                        area = r.Area,
                        department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? ""
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("levelId", (int)targetLevel.Id.Value)
                    .With("levelName", targetLevel.Name)
                    .With("roomCount", rooms.Count)
                    .WithList("rooms", rooms)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch update multiple room properties at once
        /// </summary>
        [MCPMethod("batchUpdateRooms", Category = "Room", Description = "Update properties on multiple rooms in a single transaction")]
        public static string BatchUpdateRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var updates = parameters["updates"]?.ToObject<JArray>();
                if (updates == null || updates.Count == 0)
                {
                    return ResponseBuilder.Error("updates array is required", "MISSING_PARAMETER").Build();
                }

                var results = new List<object>();
                int successCount = 0;
                int failureCount = 0;

                using (var trans = new Transaction(doc, "Batch Update Rooms"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (JObject update in updates)
                    {
                        try
                        {
                            if (update["roomId"] == null)
                            {
                                results.Add(new { roomId = (int?)null, success = false, error = "roomId missing" });
                                failureCount++;
                                continue;
                            }

                            var roomId = new ElementId(update["roomId"].ToObject<int>());
                            var room = doc.GetElement(roomId) as Room;

                            if (room == null)
                            {
                                results.Add(new { roomId = roomId.Value, success = false, error = "Room not found" });
                                failureCount++;
                                continue;
                            }

                            // Update name if provided
                            if (update["name"] != null)
                            {
                                room.Name = update["name"].ToString();
                            }

                            // Update number if provided
                            if (update["number"] != null)
                            {
                                room.Number = update["number"].ToString();
                            }

                            // Update department if provided
                            if (update["department"] != null)
                            {
                                var deptParam = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                                deptParam?.Set(update["department"].ToString());
                            }

                            // Update comments if provided
                            if (update["comments"] != null)
                            {
                                var commentsParam = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                                commentsParam?.Set(update["comments"].ToString());
                            }

                            results.Add(new
                            {
                                roomId = (int)room.Id.Value,
                                success = true,
                                name = room.Name,
                                number = room.Number
                            });
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            results.Add(new { roomId = update["roomId"]?.ToObject<int>(), success = false, error = ex.Message });
                            failureCount++;
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("success", failureCount == 0)
                    .With("totalUpdates", updates.Count)
                    .With("successCount", successCount)
                    .With("failureCount", failureCount)
                    .With("results", results)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Validate room data and find issues
        /// </summary>
        [MCPMethod("validateRoomData", Category = "Room", Description = "Validate room data and report any issues found")]
        public static string ValidateRoomData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r != null && r.Area > 0)
                    .ToList();

                var issues = new List<object>();

                // Check for duplicate room numbers
                var numberGroups = rooms.GroupBy(r => r.Number).Where(g => g.Count() > 1 && !string.IsNullOrEmpty(g.Key));
                foreach (var group in numberGroups)
                {
                    // Skip stair shafts (intentional duplicates across levels)
                    var isStairShaft = group.All(r => r.Name?.ToUpper().Contains("STAIR") == true);
                    if (!isStairShaft)
                    {
                        issues.Add(new
                        {
                            type = "DUPLICATE_NUMBER",
                            severity = "WARNING",
                            number = group.Key,
                            count = group.Count(),
                            roomIds = group.Select(r => (int)r.Id.Value).ToList(),
                            message = $"Room number '{group.Key}' is used by {group.Count()} rooms"
                        });
                    }
                }

                // Check for rooms without names
                var unnamedRooms = rooms.Where(r => string.IsNullOrWhiteSpace(r.Name));
                foreach (var room in unnamedRooms)
                {
                    issues.Add(new
                    {
                        type = "MISSING_NAME",
                        severity = "ERROR",
                        roomId = (int)room.Id.Value,
                        number = room.Number,
                        message = $"Room {room.Number} has no name"
                    });
                }

                // Check for rooms without numbers
                var unnumberedRooms = rooms.Where(r => string.IsNullOrWhiteSpace(r.Number));
                foreach (var room in unnumberedRooms)
                {
                    issues.Add(new
                    {
                        type = "MISSING_NUMBER",
                        severity = "ERROR",
                        roomId = (int)room.Id.Value,
                        name = room.Name,
                        message = $"Room '{room.Name}' has no number"
                    });
                }

                // Check for very small rooms (might be errors)
                var smallRooms = rooms.Where(r => r.Area < 10); // Less than 10 SF
                foreach (var room in smallRooms)
                {
                    issues.Add(new
                    {
                        type = "SMALL_ROOM",
                        severity = "INFO",
                        roomId = (int)room.Id.Value,
                        name = room.Name,
                        number = room.Number,
                        area = room.Area,
                        message = $"Room '{room.Name}' is very small ({room.Area:F1} SF)"
                    });
                }

                // Count by severity using JSON serialization
                var issueList = issues.Select(i => JObject.FromObject(i)).ToList();
                int errorCount = issueList.Count(i => i["severity"]?.ToString() == "ERROR");
                int warningCount = issueList.Count(i => i["severity"]?.ToString() == "WARNING");
                int infoCount = issueList.Count(i => i["severity"]?.ToString() == "INFO");

                return ResponseBuilder.Success()
                    .With("totalRooms", rooms.Count)
                    .With("issueCount", issues.Count)
                    .With("errorCount", errorCount)
                    .With("warningCount", warningCount)
                    .With("infoCount", infoCount)
                    .With("issues", issues)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create room separation lines
        /// </summary>
        [MCPMethod("createRoomSeparationLine", Category = "Room", Description = "Create a room separation line to define room boundaries")]
        public static string CreateRoomSeparationLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var startPoint = parameters["startPoint"].ToObject<double[]>();
                var endPoint = parameters["endPoint"].ToObject<double[]>();
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Create Room Separation Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var start = new XYZ(startPoint[0], startPoint[1], startPoint[2]);
                    var end = new XYZ(endPoint[0], endPoint[1], endPoint[2]);
                    var line = Line.CreateBound(start, end);

                    // In Revit 2026, use NewModelCurve with room boundary line style
                    var sketchPlane = view.SketchPlane;
                    if (sketchPlane == null)
                    {
                        // Create a sketch plane at the view's level
                        var levelId = view.GenLevel?.Id ?? doc.ActiveView.GenLevel.Id;
                        var level = doc.GetElement(levelId) as Level;
                        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation));
                        sketchPlane = SketchPlane.Create(doc, plane);
                    }

                    var modelCurve = doc.Create.NewModelCurve(line, sketchPlane);

                    // Set it as room boundary line by changing its subcategory
                    var roomBoundaryCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_RoomSeparationLines);
                    if (roomBoundaryCat != null)
                    {
                        // Get the graphics style for room separation lines
                        var graphicsStyle = roomBoundaryCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                        if (graphicsStyle != null)
                        {
                            modelCurve.LineStyle = graphicsStyle;
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("separationLineId", (int)modelCurve.Id.Value)
                        .With("viewId", (int)viewId.Value)
                        .With("length", line.Length)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a room
        /// </summary>
        [MCPMethod("deleteRoom", Category = "Room", Description = "Delete a room element from the model")]
        public static string DeleteRoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var roomId = new ElementId(int.Parse(parameters["roomId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Room"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(roomId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)roomId.Value)
                        .WithMessage("Room deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get room at point
        /// </summary>
        [MCPMethod("getRoomAtPoint", Category = "Room", Description = "Get the room that contains a given XYZ point")]
        public static string GetRoomAtPoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var point = parameters["point"].ToObject<double[]>();
                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));

                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return ResponseBuilder.Error("Level not found", "ELEMENT_NOT_FOUND").Build();
                }

                var xyz = new XYZ(point[0], point[1], point[2]);
                // In Revit 2026, GetRoomAtPoint requires a Phase parameter
                var phase = doc.Phases.get_Item(doc.Phases.Size - 1) as Phase; // Get the last (current) phase
                var room = doc.GetRoomAtPoint(xyz, phase);

                if (room == null)
                {
                    return ResponseBuilder.Error("No room found at the specified point", "ELEMENT_NOT_FOUND").Build();
                }

                return ResponseBuilder.Success()
                    .With("roomId", (int)room.Id.Value)
                    .With("name", room.Name)
                    .With("number", room.Number)
                    .With("area", room.Area)
                    .With("level", level.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Renumber rooms sequentially
        /// </summary>
        [MCPMethod("renumberRooms", Category = "Room", Description = "Renumber rooms sequentially based on a specified order")]
        public static string RenumberRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var roomIdsArray = parameters["roomIds"].ToObject<string[]>();
                var startNumber = parameters["startNumber"] != null
                    ? int.Parse(parameters["startNumber"].ToString())
                    : 1;
                var prefix = parameters["prefix"]?.ToString() ?? "";

                var roomIds = roomIdsArray.Select(id => new ElementId(int.Parse(id))).ToList();

                using (var trans = new Transaction(doc, "Renumber Rooms"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var renumbered = new List<object>();
                    var currentNumber = startNumber;

                    foreach (var roomId in roomIds)
                    {
                        var room = doc.GetElement(roomId) as Room;
                        if (room != null)
                        {
                            var newNumber = $"{prefix}{currentNumber}";
                            room.Number = newNumber;
                            renumbered.Add(new
                            {
                                roomId = (int)roomId.Value,
                                newNumber = newNumber,
                                name = room.Name
                            });
                            currentNumber++;
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("renumberedCount", renumbered.Count)
                        .With("rooms", renumbered)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Calculate filled region area for a room and update room parameter with adjusted square footage
        /// </summary>
        [MCPMethod("updateRoomAreaFromFilledRegion", Category = "Room", Description = "Calculate filled region area and update room parameter with adjusted square footage")]
        public static string UpdateRoomAreaFromFilledRegion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "UpdateRoomAreaFromFilledRegion");
                v.Require("roomId").IsType<int>();
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                // Get multiplier (default 1.2)
                var multiplier = parameters["multiplier"] != null
                    ? double.Parse(parameters["multiplier"].ToString())
                    : 1.2;

                // Get the active view
                var activeView = doc.ActiveView;

                // Get room's bounding box to find nearby filled regions
                var roomBBox = room.get_BoundingBox(activeView);
                if (roomBBox == null)
                {
                    return ResponseBuilder.Error("Could not get room bounding box", "INVALID_GEOMETRY").Build();
                }

                // Find filled regions in the view
                var filledRegions = new FilteredElementCollector(doc, activeView.Id)
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .ToList();

                if (!filledRegions.Any())
                {
                    return ResponseBuilder.Error("No filled regions found in active view", "ELEMENT_NOT_FOUND").Build();
                }

                // Find the filled region that overlaps with the room
                // We'll use bounding box intersection to find the right one
                FilledRegion targetFilledRegion = null;
                var roomCenter = new XYZ(
                    (roomBBox.Min.X + roomBBox.Max.X) / 2,
                    (roomBBox.Min.Y + roomBBox.Max.Y) / 2,
                    (roomBBox.Min.Z + roomBBox.Max.Z) / 2
                );

                foreach (var fr in filledRegions)
                {
                    var frBBox = fr.get_BoundingBox(activeView);
                    if (frBBox != null)
                    {
                        // Check if room center is inside filled region bounding box
                        if (roomCenter.X >= frBBox.Min.X && roomCenter.X <= frBBox.Max.X &&
                            roomCenter.Y >= frBBox.Min.Y && roomCenter.Y <= frBBox.Max.Y)
                        {
                            targetFilledRegion = fr;
                            break;
                        }
                    }
                }

                if (targetFilledRegion == null)
                {
                    return ResponseBuilder.Error("Could not find filled region for this room", "ELEMENT_NOT_FOUND")
                        .With("filledRegionCount", filledRegions.Count)
                        .With("roomCenter", new[] { roomCenter.X, roomCenter.Y, roomCenter.Z })
                        .Build();
                }

                // Calculate filled region area from its boundaries
                var boundaries = targetFilledRegion.GetBoundaries();
                double filledRegionArea = 0.0;

                foreach (var curveLoop in boundaries)
                {
                    // Calculate area using Green's theorem (shoelace formula)
                    double area = 0.0;
                    var curves = curveLoop.ToList();

                    for (int i = 0; i < curves.Count; i++)
                    {
                        var curve = curves[i];
                        var p1 = curve.GetEndPoint(0);
                        var p2 = curve.GetEndPoint(1);

                        // Add cross product contribution
                        area += (p1.X * p2.Y - p2.X * p1.Y);
                    }

                    filledRegionArea += Math.Abs(area / 2.0);
                }

                // Multiply by the adjustment factor
                var adjustedArea = filledRegionArea * multiplier;

                // Update the room's Comments parameter with the adjusted square footage
                using (var trans = new Transaction(doc, "Update Room Area from Filled Region"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var commentsParam = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentsParam != null && !commentsParam.IsReadOnly)
                    {
                        var areaText = $"{Math.Round(adjustedArea, 0)} SF";
                        commentsParam.Set(areaText);
                    }
                    else
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Comments parameter is read-only or not available", "PARAMETER_NOT_FOUND").Build();
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("roomId", (int)room.Id.Value)
                        .With("roomNumber", room.Number)
                        .With("roomName", room.Name)
                        .With("originalRoomArea", Math.Round(room.Area, 2))
                        .With("filledRegionId", (int)targetFilledRegion.Id.Value)
                        .With("filledRegionArea", Math.Round(filledRegionArea, 2))
                        .With("multiplier", multiplier)
                        .With("adjustedArea", Math.Round(adjustedArea, 2))
                        .With("commentsUpdated", true)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get walls that bound a specific room with classification
        /// </summary>
        [MCPMethod("getRoomBoundaryWalls", Category = "Room", Description = "Get all boundary walls for a room with their classifications")]
        public static string GetRoomBoundaryWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetRoomBoundaryWalls");
                v.Require("roomId").IsType<int>();
                v.ThrowIfInvalid();

                var roomIdInt = v.GetRequired<int>("roomId");
                var room = ElementLookup.GetRoom(doc, roomIdInt);

                var boundaryWalls = new List<object>();

                // Get room boundary segments
                var options = new SpatialElementBoundaryOptions();
                var boundarySegments = room.GetBoundarySegments(options);

                if (boundarySegments == null || boundarySegments.Count == 0)
                {
                    return ResponseBuilder.Error("Room has no boundary segments", "INVALID_GEOMETRY").Build();
                }

                foreach (var segmentList in boundarySegments)
                {
                    foreach (var segment in segmentList)
                    {
                        var wallElement = doc.GetElement(segment.ElementId) as Wall;
                        if (wallElement != null)
                        {
                            // Get wall type
                            var wallType = doc.GetElement(wallElement.GetTypeId()) as WallType;
                            var wallTypeName = wallType?.Name ?? "Unknown";

                            // Determine wall classification
                            var classification = "Interior";
                            var adjacentSpace = "Unknown";

                            // Check wall function
                            if (wallType != null)
                            {
                                if (wallType.Function == WallFunction.Exterior)
                                {
                                    classification = "Exterior";
                                    adjacentSpace = "Exterior";
                                }
                            }

                            // Check if wall bounds multiple rooms (demising wall)
                            var roomsOnWall = new FilteredElementCollector(doc)
                                .OfClass(typeof(SpatialElement))
                                .Cast<Room>()
                                .Where(r => r.Id != room.Id && r.Area > 0)
                                .Where(r =>
                                {
                                    var rBoundaries = r.GetBoundarySegments(options);
                                    if (rBoundaries == null) return false;
                                    foreach (var rSegList in rBoundaries)
                                    {
                                        if (rSegList.Any(s => s.ElementId == wallElement.Id))
                                            return true;
                                    }
                                    return false;
                                })
                                .ToList();

                            if (roomsOnWall.Any())
                            {
                                var adjacentRoom = roomsOnWall.First();
                                adjacentSpace = $"Room {adjacentRoom.Number} - {adjacentRoom.Name}";

                                // If adjacent to another office, it's a demising wall
                                if (adjacentRoom.Name != null && adjacentRoom.Name.ToUpper().Contains("OFFICE"))
                                {
                                    classification = "Demising";
                                }
                                else if (adjacentRoom.Name != null &&
                                        (adjacentRoom.Name.ToUpper().Contains("HALL") ||
                                         adjacentRoom.Name.ToUpper().Contains("CORRIDOR")))
                                {
                                    classification = "Hallway";
                                }
                            }

                            // Get current location line
                            var locationLineParam = wallElement.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                            var currentLocationLine = "Unknown";
                            if (locationLineParam != null)
                            {
                                var locLineValue = locationLineParam.AsInteger();
                                currentLocationLine = ((WallLocationLine)locLineValue).ToString();
                            }

                            // Get room bounding parameter
                            var roomBoundingParam = wallElement.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                            var isRoomBounding = roomBoundingParam?.AsInteger() == 1;

                            boundaryWalls.Add(new
                            {
                                wallId = (int)wallElement.Id.Value,
                                wallType = wallTypeName,
                                classification = classification,
                                adjacentSpace = adjacentSpace,
                                currentLocationLine = currentLocationLine,
                                isRoomBounding = isRoomBounding,
                                length = segment.GetCurve().Length,
                                recommendedLocationLine = classification == "Demising" ? "WallCenterline" : "FinishFaceExterior"
                            });
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("roomId", (int)room.Id.Value)
                    .With("roomNumber", room.Number)
                    .With("roomName", room.Name)
                    .With("boundaryWallCount", boundaryWalls.Count)
                    .With("boundaryWalls", boundaryWalls)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set the Area Computation setting for the document (affects all room area calculations)
        /// Options: "Finish" (wall finish/interior), "Center" (wall center), "CoreBoundary", "CoreCenter"
        /// </summary>
        [MCPMethod("setAreaComputation", Category = "Room", Description = "Set the area computation method for room area calculations")]
        public static string SetAreaComputation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get computation type parameter (default to Center if not specified)
                var computationType = parameters["computationType"]?.ToString() ?? "Center";

                // Map string to enum
                SpatialElementBoundaryLocation boundaryLocation;
                switch (computationType.ToLower())
                {
                    case "finish":
                        boundaryLocation = SpatialElementBoundaryLocation.Finish;
                        break;
                    case "center":
                        boundaryLocation = SpatialElementBoundaryLocation.Center;
                        break;
                    case "coreboundary":
                        boundaryLocation = SpatialElementBoundaryLocation.CoreBoundary;
                        break;
                    case "corecenter":
                        boundaryLocation = SpatialElementBoundaryLocation.CoreCenter;
                        break;
                    default:
                        return ResponseBuilder.Error($"Invalid computation type: {computationType}. Valid options: Finish, Center, CoreBoundary, CoreCenter", "INVALID_PARAMETER").Build();
                }

                using (var trans = new Transaction(doc, "Set Area Computation"))
                {
                    trans.Start();

                    // Get the AreaVolumeSettings for the document
                    var settings = AreaVolumeSettings.GetAreaVolumeSettings(doc);

                    // Get current setting for comparison
                    var previousSetting = settings.GetSpatialElementBoundaryLocation(SpatialElementType.Room);

                    // Set the new boundary location for rooms (Revit 2026 API: location first, then type)
                    settings.SetSpatialElementBoundaryLocation(boundaryLocation, SpatialElementType.Room);

                    // Force regeneration to recalculate all room areas
                    doc.Regenerate();

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("previousSetting", previousSetting.ToString())
                        .With("newSetting", boundaryLocation.ToString())
                        .WithMessage($"Area computation changed from {previousSetting} to {boundaryLocation}. All room areas will now be calculated from wall {boundaryLocation.ToString().ToLower()}.")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the current Area Computation setting
        /// </summary>
        [MCPMethod("getAreaComputation", Category = "Room", Description = "Get the current area computation method setting for the document")]
        public static string GetAreaComputation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var settings = AreaVolumeSettings.GetAreaVolumeSettings(doc);
                var roomSetting = settings.GetSpatialElementBoundaryLocation(SpatialElementType.Room);
                var areaSetting = settings.GetSpatialElementBoundaryLocation(SpatialElementType.Area);

                return ResponseBuilder.Success()
                    .With("roomBoundary", roomSetting.ToString())
                    .With("areaBoundary", areaSetting.ToString())
                    .With("roomBoundaryDescription", GetBoundaryDescription(roomSetting))
                    .With("areaBoundaryDescription", GetBoundaryDescription(areaSetting))
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string GetBoundaryDescription(SpatialElementBoundaryLocation loc)
        {
            switch (loc)
            {
                case SpatialElementBoundaryLocation.Finish:
                    return "Room areas measured to interior wall finish (smallest area)";
                case SpatialElementBoundaryLocation.Center:
                    return "Room areas measured to wall centerline (larger area, industry standard for lease calculations)";
                case SpatialElementBoundaryLocation.CoreBoundary:
                    return "Room areas measured to structural core boundary";
                case SpatialElementBoundaryLocation.CoreCenter:
                    return "Room areas measured to center of structural core";
                default:
                    return "Unknown boundary location";
            }
        }

        /// <summary>
        /// Create an area plan view for gross/rentable area calculations
        /// Parameters: levelId, areaScheme: "Gross Building"|"Rentable" (optional), viewName (optional)
        /// </summary>
        [MCPMethod("createAreaPlan", Category = "Room", Description = "Create an area plan view for gross or rentable area calculations")]
        public static string CreateAreaPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var areaScheme = parameters["areaScheme"]?.ToString() ?? "Gross Building";
                var viewName = parameters["viewName"]?.ToString();

                if (!levelId.HasValue)
                {
                    return ResponseBuilder.Error("levelId is required", "MISSING_PARAMETER").Build();
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return ResponseBuilder.Error("Level not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Find the area scheme
                var areaSchemes = new FilteredElementCollector(doc)
                    .OfClass(typeof(AreaScheme))
                    .Cast<AreaScheme>()
                    .ToList();

                var scheme = areaSchemes.FirstOrDefault(s =>
                    s.Name.IndexOf(areaScheme, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? areaSchemes.FirstOrDefault();

                if (scheme == null)
                {
                    return ResponseBuilder.Error("No area scheme found", "ELEMENT_NOT_FOUND")
                        .With("availableSchemes", areaSchemes.Select(s => s.Name).ToList())
                        .Build();
                }

                ViewPlan areaPlan = null;

                using (var trans = new Transaction(doc, "Create Area Plan"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    areaPlan = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);

                    if (!string.IsNullOrEmpty(viewName))
                    {
                        areaPlan.Name = viewName;
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("viewId", areaPlan.Id.Value)
                    .With("viewName", areaPlan.Name)
                    .With("levelName", level.Name)
                    .With("areaScheme", scheme.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create area boundary lines in an area plan
        /// Parameters: viewId, points: [[x1,y1], [x2,y2], ...], closed: true|false (optional)
        /// </summary>
        [MCPMethod("createAreaBoundary", Category = "Room", Description = "Create area boundary lines in an area plan view")]
        public static string CreateAreaBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = parameters["viewId"]?.Value<int>();
                var points = parameters["points"]?.ToObject<double[][]>();
                var closed = parameters["closed"]?.Value<bool>() ?? true;

                if (!viewId.HasValue)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                if (points == null || points.Length < 2)
                {
                    return ResponseBuilder.Error("At least 2 points are required", "INVALID_PARAMETER").Build();
                }

                var view = doc.GetElement(new ElementId(viewId.Value)) as ViewPlan;
                if (view == null || view.ViewType != ViewType.AreaPlan)
                {
                    return ResponseBuilder.Error("View must be an area plan", "INVALID_PARAMETER").Build();
                }

                var sketchPlane = view.SketchPlane;
                var createdLineIds = new List<long>();

                using (var trans = new Transaction(doc, "Create Area Boundary"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create boundary lines
                    for (int i = 0; i < points.Length - 1; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], 0);
                        var end = new XYZ(points[i + 1][0], points[i + 1][1], 0);
                        var line = Line.CreateBound(start, end);

                        var boundaryLine = doc.Create.NewAreaBoundaryLine(sketchPlane, line, view);
                        if (boundaryLine != null)
                        {
                            createdLineIds.Add(boundaryLine.Id.Value);
                        }
                    }

                    // Close the loop if requested
                    if (closed && points.Length > 2)
                    {
                        var start = new XYZ(points[points.Length - 1][0], points[points.Length - 1][1], 0);
                        var end = new XYZ(points[0][0], points[0][1], 0);
                        var line = Line.CreateBound(start, end);

                        var boundaryLine = doc.Create.NewAreaBoundaryLine(sketchPlane, line, view);
                        if (boundaryLine != null)
                        {
                            createdLineIds.Add(boundaryLine.Id.Value);
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("viewId", viewId.Value)
                    .With("boundaryLineCount", createdLineIds.Count)
                    .With("lineIds", createdLineIds)
                    .With("closed", closed)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all areas in the model or in a specific view
        /// Parameters: viewId (optional) - if not provided, gets all areas
        /// </summary>
        [MCPMethod("getAreas", Category = "Room", Description = "Get all areas in the model or in a specific area plan view")]
        public static string GetAreas(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>();

                FilteredElementCollector collector;
                if (viewId.HasValue)
                {
                    var view = doc.GetElement(new ElementId(viewId.Value)) as View;
                    if (view == null)
                    {
                        return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                    }
                    collector = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var areas = collector
                    .OfCategory(BuiltInCategory.OST_Areas)
                    .WhereElementIsNotElementType()
                    .Cast<Area>()
                    .Where(a => a.Area > 0) // Only placed areas
                    .Select(a => new
                    {
                        areaId = a.Id.Value,
                        name = a.Name,
                        number = a.Number,
                        area = Math.Round(a.Area, 2),
                        areaSqFt = Math.Round(a.Area, 2),
                        levelName = doc.GetElement(a.LevelId)?.Name ?? "Unknown",
                        areaScheme = (doc.GetElement(a.AreaScheme.Id) as AreaScheme)?.Name ?? "Unknown",
                        perimeter = Math.Round(a.Perimeter, 2)
                    })
                    .ToList();

                var totalAreaValue = areas.Sum(a => a.area);

                return ResponseBuilder.Success()
                    .With("areaCount", areas.Count)
                    .With("totalArea", Math.Round(totalAreaValue, 2))
                    .WithList("areas", areas)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place an area in an area plan at a specific point
        /// Parameters: viewId, point: [x, y], areaName (optional), areaNumber (optional)
        /// </summary>
        [MCPMethod("placeArea", Category = "Room", Description = "Place an area element in an area plan at a specified point")]
        public static string PlaceArea(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = parameters["viewId"]?.Value<int>();
                var point = parameters["point"]?.ToObject<double[]>();
                var areaName = parameters["areaName"]?.ToString();
                var areaNumber = parameters["areaNumber"]?.ToString();

                if (!viewId.HasValue)
                {
                    return ResponseBuilder.Error("viewId is required", "MISSING_PARAMETER").Build();
                }

                if (point == null || point.Length < 2)
                {
                    return ResponseBuilder.Error("point [x, y] is required", "INVALID_PARAMETER").Build();
                }

                var view = doc.GetElement(new ElementId(viewId.Value)) as ViewPlan;
                if (view == null || view.ViewType != ViewType.AreaPlan)
                {
                    return ResponseBuilder.Error("View must be an area plan", "INVALID_PARAMETER").Build();
                }

                Area newArea = null;

                using (var trans = new Transaction(doc, "Place Area"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var uv = new UV(point[0], point[1]);
                    newArea = doc.Create.NewArea(view, uv);

                    if (newArea != null)
                    {
                        if (!string.IsNullOrEmpty(areaName))
                        {
                            var nameParam = newArea.get_Parameter(BuiltInParameter.ROOM_NAME);
                            nameParam?.Set(areaName);
                        }

                        if (!string.IsNullOrEmpty(areaNumber))
                        {
                            var numParam = newArea.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                            numParam?.Set(areaNumber);
                        }
                    }

                    trans.Commit();
                }

                if (newArea == null)
                {
                    return ResponseBuilder.Error("Failed to create area - check if point is inside a closed boundary", "OPERATION_FAILED").Build();
                }

                return ResponseBuilder.Success()
                    .With("areaId", newArea.Id.Value)
                    .With("name", newArea.Name)
                    .With("number", newArea.Number)
                    .With("area", Math.Round(newArea.Area, 2))
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #region Room Finish Methods

        /// <summary>
        /// Create a room finish schedule automatically with standard finish parameters
        /// Parameters: scheduleName (optional), includeFloor, includeCeiling, includeWalls, includeBase (all optional bools)
        /// </summary>
        [MCPMethod("createRoomFinishSchedule", Category = "Room", Description = "Create a room finish schedule with floor, ceiling, wall, and base finish parameters")]
        public static string CreateRoomFinishSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var scheduleName = parameters["scheduleName"]?.ToString() ?? "Room Finish Schedule";
                var includeFloor = parameters["includeFloor"]?.Value<bool>() ?? true;
                var includeCeiling = parameters["includeCeiling"]?.Value<bool>() ?? true;
                var includeWalls = parameters["includeWalls"]?.Value<bool>() ?? true;
                var includeBase = parameters["includeBase"]?.Value<bool>() ?? true;

                ViewSchedule schedule = null;
                var fieldsAdded = new List<string>();

                using (var trans = new Transaction(doc, "Create Room Finish Schedule"))
                {
                    trans.Start();

                    // Create schedule for Rooms category
                    schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rooms));
                    schedule.Name = scheduleName;

                    var definition = schedule.Definition;

                    // Get schedulable fields
                    var schedulableFields = definition.GetSchedulableFields();

                    // Add standard room fields first
                    var standardFields = new[] { "Number", "Name", "Area", "Level" };
                    foreach (var fieldName in standardFields)
                    {
                        var field = schedulableFields.FirstOrDefault(f =>
                            f.GetName(doc).Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                        if (field != null)
                        {
                            definition.AddField(field);
                            fieldsAdded.Add(fieldName);
                        }
                    }

                    // Add finish fields based on parameters
                    var finishFieldNames = new List<string>();
                    if (includeFloor)
                    {
                        finishFieldNames.Add("Floor Finish");
                        finishFieldNames.Add("Base Finish"); // Often paired with floor
                    }
                    if (includeBase && !includeFloor)
                    {
                        finishFieldNames.Add("Base Finish");
                    }
                    if (includeCeiling)
                    {
                        finishFieldNames.Add("Ceiling Finish");
                        finishFieldNames.Add("Ceiling Height"); // Useful with ceiling
                    }
                    if (includeWalls)
                    {
                        finishFieldNames.Add("Wall Finish");
                    }

                    // Add common additional fields
                    finishFieldNames.Add("Comments");

                    foreach (var fieldName in finishFieldNames)
                    {
                        var field = schedulableFields.FirstOrDefault(f =>
                            f.GetName(doc).Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
                            f.GetName(doc).Replace(" ", "").Equals(fieldName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                        if (field != null)
                        {
                            definition.AddField(field);
                            fieldsAdded.Add(fieldName);
                        }
                    }

                    // Add sorting by Level then Room Number
                    var fields = definition.GetFieldOrder();
                    foreach (var fieldId in fields)
                    {
                        var field = definition.GetField(fieldId);
                        var name = field.GetName();
                        if (name.Equals("Level", StringComparison.OrdinalIgnoreCase))
                        {
                            field.IsHidden = false;
                            var sortGroup = new ScheduleSortGroupField(fieldId);
                            definition.AddSortGroupField(sortGroup);
                            break;
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("scheduleId", schedule.Id.Value)
                    .With("scheduleName", schedule.Name)
                    .With("fieldsAdded", fieldsAdded)
                    .With("fieldCount", fieldsAdded.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get room finish data for all rooms or specific rooms
        /// Parameters: roomIds (optional array), levelId (optional filter)
        /// </summary>
        [MCPMethod("getRoomFinishes", Category = "Room", Description = "Get finish data for all rooms or a specific set of rooms")]
        public static string GetRoomFinishes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roomIds = parameters["roomIds"]?.ToObject<int[]>();
                var levelId = parameters["levelId"]?.Value<int>();

                IEnumerable<Room> rooms;

                if (roomIds != null && roomIds.Length > 0)
                {
                    rooms = roomIds
                        .Select(id => doc.GetElement(new ElementId(id)) as Room)
                        .Where(r => r != null && r.Area > 0);
                }
                else
                {
                    var collector = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.Area > 0);

                    if (levelId.HasValue)
                    {
                        collector = collector.Where(r => r.LevelId.Value == levelId.Value);
                    }

                    rooms = collector;
                }

                var roomFinishes = rooms.Select(r => new
                {
                    roomId = r.Id.Value,
                    number = r.Number,
                    name = r.Name,
                    level = doc.GetElement(r.LevelId)?.Name ?? "Unknown",
                    area = Math.Round(r.Area, 2),
                    floorFinish = GetParameterValue(r, "Floor Finish"),
                    ceilingFinish = GetParameterValue(r, "Ceiling Finish"),
                    wallFinish = GetParameterValue(r, "Wall Finish"),
                    baseFinish = GetParameterValue(r, "Base Finish"),
                    ceilingHeight = GetParameterValue(r, "Limit Offset"),
                    comments = GetParameterValue(r, "Comments")
                }).OrderBy(r => r.level).ThenBy(r => r.number).ToList();

                return ResponseBuilder.Success()
                    .With("roomCount", roomFinishes.Count)
                    .With("rooms", roomFinishes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Bulk update room finishes based on room type/name patterns or explicit room IDs
        /// Parameters:
        ///   updates: array of { roomIds: [ids] OR roomNamePattern: "pattern", floorFinish, ceilingFinish, wallFinish, baseFinish }
        /// </summary>
        [MCPMethod("updateRoomFinishes", Category = "Room", Description = "Bulk update floor, ceiling, wall, and base finishes for rooms by ID or name pattern")]
        public static string UpdateRoomFinishes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var updates = parameters["updates"]?.ToObject<JArray>();
                if (updates == null || updates.Count == 0)
                {
                    return ResponseBuilder.Error("updates array is required", "MISSING_PARAMETER").Build();
                }

                var updatedRooms = new List<object>();
                var failedUpdates = new List<object>();

                using (var trans = new Transaction(doc, "Update Room Finishes"))
                {
                    trans.Start();

                    foreach (JObject update in updates)
                    {
                        var roomIds = update["roomIds"]?.ToObject<int[]>();
                        var roomNamePattern = update["roomNamePattern"]?.ToString();

                        IEnumerable<Room> targetRooms;

                        if (roomIds != null && roomIds.Length > 0)
                        {
                            targetRooms = roomIds
                                .Select(id => doc.GetElement(new ElementId(id)) as Room)
                                .Where(r => r != null);
                        }
                        else if (!string.IsNullOrEmpty(roomNamePattern))
                        {
                            // Match rooms by name pattern (case-insensitive contains)
                            targetRooms = new FilteredElementCollector(doc)
                                .OfCategory(BuiltInCategory.OST_Rooms)
                                .WhereElementIsNotElementType()
                                .Cast<Room>()
                                .Where(r => r.Area > 0 &&
                                    r.Name.IndexOf(roomNamePattern, StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        else
                        {
                            failedUpdates.Add(new { error = "Either roomIds or roomNamePattern is required" });
                            continue;
                        }

                        var floorFinish = update["floorFinish"]?.ToString();
                        var ceilingFinish = update["ceilingFinish"]?.ToString();
                        var wallFinish = update["wallFinish"]?.ToString();
                        var baseFinish = update["baseFinish"]?.ToString();

                        foreach (var room in targetRooms)
                        {
                            try
                            {
                                var updated = new List<string>();

                                if (!string.IsNullOrEmpty(floorFinish))
                                {
                                    if (SetParameterValue(room, "Floor Finish", floorFinish))
                                        updated.Add("Floor Finish");
                                }
                                if (!string.IsNullOrEmpty(ceilingFinish))
                                {
                                    if (SetParameterValue(room, "Ceiling Finish", ceilingFinish))
                                        updated.Add("Ceiling Finish");
                                }
                                if (!string.IsNullOrEmpty(wallFinish))
                                {
                                    if (SetParameterValue(room, "Wall Finish", wallFinish))
                                        updated.Add("Wall Finish");
                                }
                                if (!string.IsNullOrEmpty(baseFinish))
                                {
                                    if (SetParameterValue(room, "Base Finish", baseFinish))
                                        updated.Add("Base Finish");
                                }

                                if (updated.Count > 0)
                                {
                                    updatedRooms.Add(new
                                    {
                                        roomId = room.Id.Value,
                                        roomNumber = room.Number,
                                        roomName = room.Name,
                                        updatedFields = updated
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                failedUpdates.Add(new { roomId = room.Id.Value, error = ex.Message });
                            }
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("updatedCount", updatedRooms.Count)
                    .With("failedCount", failedUpdates.Count)
                    .With("updatedRooms", updatedRooms)
                    .With("failedUpdates", failedUpdates)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper to get parameter value as string
        /// </summary>
        private static string GetParameterValue(Room room, string paramName)
        {
            var param = room.LookupParameter(paramName);
            if (param == null) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Double:
                    return Math.Round(param.AsDouble(), 2).ToString();
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.ElementId:
                    return param.AsElementId().Value.ToString();
                default:
                    return null;
            }
        }

        /// <summary>
        /// Helper to set parameter value
        /// </summary>
        private static bool SetParameterValue(Room room, string paramName, string value)
        {
            var param = room.LookupParameter(paramName);
            if (param == null || param.IsReadOnly) return false;

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value);
                        return true;
                    case StorageType.Double:
                        if (double.TryParse(value, out double d))
                        {
                            param.Set(d);
                            return true;
                        }
                        break;
                    case StorageType.Integer:
                        if (int.TryParse(value, out int i))
                        {
                            param.Set(i);
                            return true;
                        }
                        break;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        /// <summary>
        /// Set the upper limit and offset for a room element
        /// </summary>
        [MCPMethod("setRoomUpperLimit", Category = "Room", Description = "Set the upper limit level and offset for a room element")]
        public static string Setroomupperlimit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;


                using (var trans = new Transaction(doc, "Setroomupperlimit"))
                {
                    trans.Start();


                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .WithMessage("Setroomupperlimit completed")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }


        /// <summary>
        /// Get the bounding box coordinates for a room element
        /// </summary>
        [MCPMethod("getRoomBoundingBox", Category = "Room", Description = "Get the bounding box coordinates for a room element")]
        public static string Getroomboundingbox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;


                using (var trans = new Transaction(doc, "Getroomboundingbox"))
                {
                    trans.Start();


                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .WithMessage("Getroomboundingbox completed")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

    }

    // Helper extension method
    public static class CollectorExtensions
    {
        public static FilteredElementCollector ToFilteredElementCollector(this IList<Element> elements, Document doc)
        {
            var collector = new FilteredElementCollector(doc);
            return collector.WherePasses(new ElementIdSetFilter(elements.Select(e => e.Id).ToList()));
        }
    }
}
