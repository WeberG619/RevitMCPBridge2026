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
    /// Door and window placement, modification, and management methods for MCP Bridge
    /// </summary>
    public static class DoorWindowMethods
    {
        /// <summary>
        /// Place a door in a wall
        /// </summary>
        [MCPMethod("placeDoor", Category = "DoorWindow", Description = "Place a door in a wall")]
        public static string PlaceDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                // FLEXIBILITY: Accept both 'doorTypeId' and 'typeId' parameter names
                var doorTypeIdParam = parameters["doorTypeId"] ?? parameters["typeId"];
                var doorTypeId = doorTypeIdParam != null
                    ? new ElementId(int.Parse(doorTypeIdParam.ToString()))
                    : null;

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get door type
                FamilySymbol doorType = null;
                if (doorTypeId != null)
                {
                    doorType = doc.GetElement(doorTypeId) as FamilySymbol;
                }
                else
                {
                    // Get first available door type
                    doorType = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(fs => fs.Family.Name.Contains("Door"));
                }

                if (doorType == null)
                {
                    return ResponseBuilder.Error("No valid door type found", "TYPE_NOT_FOUND").Build();
                }

                // NULL SAFETY: Validate level before using
                var level = doc.GetElement(wall.LevelId) as Level;
                if (level == null)
                {
                    return ResponseBuilder.Error("Wall level not found. Cannot place door without a valid level.", "LEVEL_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Place Door"))
                {
                    // Set failure handling options to suppress warnings
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    trans.Start();

                    // Activate the symbol if needed
                    if (!doorType.IsActive)
                    {
                        doorType.Activate();
                    }

                    // Get location on wall
                    XYZ location;
                    if (parameters["location"] != null)
                    {
                        var loc = parameters["location"].ToObject<double[]>();
                        // VALIDATION: Ensure location array has 3 elements
                        if (loc == null || loc.Length < 3)
                        {
                            trans.RollBack();
                            return ResponseBuilder.Error("Location must be an array of 3 numbers [x, y, z]", "VALIDATION_ERROR").Build();
                        }
                        location = new XYZ(loc[0], loc[1], loc[2]);
                    }
                    else
                    {
                        // Place at wall midpoint - with null safety check
                        var locationCurve = wall.Location as LocationCurve;
                        if (locationCurve == null || locationCurve.Curve == null)
                        {
                            return ResponseBuilder.Error("Wall does not have a valid location curve. Cannot determine door placement position.", "INVALID_GEOMETRY").Build();
                        }
                        location = locationCurve.Curve.Evaluate(0.5, true);
                    }

                    // Create the door
                    var door = doc.Create.NewFamilyInstance(
                        location,
                        doorType,
                        wall,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    // Get ID before commit in case of rollback
                    var doorId = door.Id.Value;
                    var doorTypeName = doorType.Name;

                    var commitResult = trans.Commit();

                    if (commitResult != TransactionStatus.Committed)
                    {
                        return ResponseBuilder.Error($"Transaction failed with status: {commitResult}", "TRANSACTION_FAILED").Build();
                    }

                    return ResponseBuilder.Success()
                        .With("doorId", (int)doorId)
                        .With("doorType", doorTypeName)
                        .With("wallId", (int)wallId.Value)
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
        /// Place a window in a wall
        /// </summary>
        [MCPMethod("placeWindow", Category = "DoorWindow", Description = "Place a window in a wall")]
        public static string PlaceWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                // FLEXIBILITY: Accept both 'windowTypeId' and 'typeId' parameter names
                var windowTypeIdParam = parameters["windowTypeId"] ?? parameters["typeId"];
                var windowTypeId = windowTypeIdParam != null
                    ? new ElementId(int.Parse(windowTypeIdParam.ToString()))
                    : null;

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Get window type
                FamilySymbol windowType = null;
                if (windowTypeId != null)
                {
                    windowType = doc.GetElement(windowTypeId) as FamilySymbol;
                }
                else
                {
                    // Get first available window type
                    windowType = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_Windows)
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();
                }

                if (windowType == null)
                {
                    return ResponseBuilder.Error("No valid window type found", "TYPE_NOT_FOUND").Build();
                }

                // NULL SAFETY: Validate level before using
                var level = doc.GetElement(wall.LevelId) as Level;
                if (level == null)
                {
                    return ResponseBuilder.Error("Wall level not found. Cannot place window without a valid level.", "LEVEL_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Place Window"))
                {
                    // Set failure handling options to suppress warnings
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    trans.Start();

                    // Activate the symbol if needed
                    if (!windowType.IsActive)
                    {
                        windowType.Activate();
                    }

                    // Get location on wall
                    XYZ location;
                    if (parameters["location"] != null)
                    {
                        var loc = parameters["location"].ToObject<double[]>();
                        // VALIDATION: Ensure location array has 3 elements
                        if (loc == null || loc.Length < 3)
                        {
                            trans.RollBack();
                            return ResponseBuilder.Error("Location must be an array of 3 numbers [x, y, z]", "VALIDATION_ERROR").Build();
                        }
                        location = new XYZ(loc[0], loc[1], loc[2]);
                    }
                    else
                    {
                        // Place at wall midpoint - with null safety check
                        var locationCurve = wall.Location as LocationCurve;
                        if (locationCurve == null || locationCurve.Curve == null)
                        {
                            trans.RollBack();
                            return ResponseBuilder.Error("Wall does not have a valid location curve. Cannot determine window placement position.", "INVALID_GEOMETRY").Build();
                        }
                        location = locationCurve.Curve.Evaluate(0.5, true);
                    }

                    // Create the window
                    var window = doc.Create.NewFamilyInstance(
                        location,
                        windowType,
                        wall,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    // Get ID before commit in case of rollback
                    var windowId = window.Id.Value;
                    var windowTypeName = windowType.Name;

                    var commitResult = trans.Commit();

                    if (commitResult != TransactionStatus.Committed)
                    {
                        return ResponseBuilder.Error($"Transaction failed with status: {commitResult}", "TRANSACTION_FAILED").Build();
                    }

                    return ResponseBuilder.Success()
                        .With("windowId", (int)windowId)
                        .With("windowType", windowTypeName)
                        .With("wallId", (int)wallId.Value)
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
        /// Get door/window information
        /// </summary>
        [MCPMethod("getDoorWindowInfo", Category = "DoorWindow")]
        public static string GetDoorWindowInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetDoorWindowInfo");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementIdInt = v.GetRequired<int>("elementId");
                var element = ElementLookup.GetElement<FamilyInstance>(doc, elementIdInt);

                var category = element.Category.Name;
                var familySymbol = element.Symbol;
                var host = element.Host;
                var level = doc.GetElement(element.LevelId) as Level;

                // Get dimensions
                var width = element.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                    ?? element.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0;
                var height = element.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble()
                    ?? element.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0;

                // Get location
                var location = (element.Location as LocationPoint)?.Point;

                return ResponseBuilder.Success()
                    .With("elementId", (int)element.Id.Value)
                    .With("category", category)
                    .With("familyName", familySymbol.Family.Name)
                    .With("typeName", familySymbol.Name)
                    .With("typeId", (int)familySymbol.Id.Value)
                    .With("hostId", host != null ? (int)host.Id.Value : -1)
                    .With("level", level?.Name)
                    .With("levelId", (int)element.LevelId.Value)
                    .With("width", width)
                    .With("height", height)
                    .With("location", location != null ? new[] { location.X, location.Y, location.Z } : null)
                    .With("mark", element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                    .With("comments", element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify door/window properties
        /// </summary>
        [MCPMethod("modifyDoorWindowProperties", Category = "DoorWindow")]
        public static string ModifyDoorWindowProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "ModifyDoorWindowProperties");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementIdInt = v.GetRequired<int>("elementId");
                var element = ElementLookup.GetElement<FamilyInstance>(doc, elementIdInt);
                var elementId = element.Id;

                using (var trans = new Transaction(doc, "Modify Door/Window Properties"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modified = new List<string>();

                    // Change type
                    if (parameters["typeId"] != null)
                    {
                        var newTypeId = new ElementId(int.Parse(parameters["typeId"].ToString()));
                        var newType = doc.GetElement(newTypeId) as FamilySymbol;
                        if (newType != null)
                        {
                            if (!newType.IsActive)
                            {
                                newType.Activate();
                            }
                            element.Symbol = newType;
                            modified.Add("type");
                        }
                    }

                    // Change mark
                    if (parameters["mark"] != null)
                    {
                        var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        if (markParam != null && !markParam.IsReadOnly)
                        {
                            markParam.Set(parameters["mark"].ToString());
                            modified.Add("mark");
                        }
                    }

                    // Change comments
                    if (parameters["comments"] != null)
                    {
                        var commentsParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (commentsParam != null && !commentsParam.IsReadOnly)
                        {
                            commentsParam.Set(parameters["comments"].ToString());
                            modified.Add("comments");
                        }
                    }

                    // Change sill height (windows)
                    if (parameters["sillHeight"] != null)
                    {
                        var sillParam = element.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sillParam != null && !sillParam.IsReadOnly)
                        {
                            sillParam.Set(double.Parse(parameters["sillHeight"].ToString()));
                            modified.Add("sillHeight");
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("elementId", (int)element.Id.Value)
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
        /// Flip door/window orientation
        /// </summary>
        [MCPMethod("flipDoorWindow", Category = "DoorWindow")]
        public static string FlipDoorWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "FlipDoorWindow");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementIdInt = v.GetRequired<int>("elementId");
                var flipHand = v.GetOptional<bool>("flipHand", true);
                var flipFacing = v.GetOptional<bool>("flipFacing", false);

                var element = ElementLookup.GetElement<FamilyInstance>(doc, elementIdInt);
                var elementId = element.Id;

                using (var trans = new Transaction(doc, "Flip Door/Window"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (flipHand && element.CanFlipHand)
                    {
                        element.flipHand();
                    }

                    if (flipFacing && element.CanFlipFacing)
                    {
                        element.flipFacing();
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("elementId", (int)elementId.Value)
                        .With("flippedHand", flipHand)
                        .With("flippedFacing", flipFacing)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all doors in a view
        /// </summary>
        [MCPMethod("getDoorsInView", Category = "DoorWindow")]
        public static string GetDoorsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document open in Revit", "NO_ACTIVE_DOCUMENT").Build();
                }
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetDoorsInView");
                v.Require("viewId").IsType<int>();
                v.ThrowIfInvalid();

                var viewIdInt = v.GetRequired<int>("viewId");
                var view = ElementLookup.GetView(doc, viewIdInt);
                var viewId = view.Id;

                var doors = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilyInstance>()
                    .Select(d => new
                    {
                        doorId = (int)d.Id.Value,
                        familyName = d.Symbol?.Family?.Name ?? "Unknown",
                        typeName = d.Symbol?.Name ?? "Unknown",
                        typeId = d.Symbol != null ? (int)d.Symbol.Id.Value : 0,
                        mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                        height = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                        level = doc.GetElement(d.LevelId)?.Name
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("doorCount", doors.Count)
                    .With("doors", doors)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all windows in a view
        /// </summary>
        [MCPMethod("getWindowsInView", Category = "DoorWindow")]
        public static string GetWindowsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document open in Revit", "NO_ACTIVE_DOCUMENT").Build();
                }
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetWindowsInView");
                v.Require("viewId").IsType<int>();
                v.ThrowIfInvalid();

                var viewIdInt = v.GetRequired<int>("viewId");
                var view = ElementLookup.GetView(doc, viewIdInt);
                var viewId = view.Id;

                var windows = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilyInstance>()
                    .Select(w => new
                    {
                        windowId = (int)w.Id.Value,
                        familyName = w.Symbol?.Family?.Name ?? "Unknown",
                        typeName = w.Symbol?.Name ?? "Unknown",
                        typeId = w.Symbol != null ? (int)w.Symbol.Id.Value : 0,
                        mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        width = w.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                        height = w.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                        sillHeight = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0,
                        level = doc.GetElement(w.LevelId)?.Name
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("windowCount", windows.Count)
                    .With("windows", windows)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all door types
        /// </summary>
        [MCPMethod("getDoorTypes", Category = "DoorWindow")]
        public static string GetDoorTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var doorTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilySymbol>()
                    .Select(dt => new
                    {
                        typeId = (int)dt.Id.Value,
                        familyName = dt.Family.Name,
                        typeName = dt.Name,
                        width = dt.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                        height = dt.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                        isActive = dt.IsActive
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("doorTypeCount", doorTypes.Count)
                    .With("doorTypes", doorTypes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all window types
        /// </summary>
        [MCPMethod("getWindowTypes", Category = "DoorWindow")]
        public static string GetWindowTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var windowTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilySymbol>()
                    .Select(wt => new
                    {
                        typeId = (int)wt.Id.Value,
                        familyName = wt.Family.Name,
                        typeName = wt.Name,
                        width = wt.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                        height = wt.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                        isActive = wt.IsActive
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("windowTypeCount", windowTypes.Count)
                    .With("windowTypes", windowTypes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete door/window
        /// </summary>
        [MCPMethod("deleteDoorWindow", Category = "DoorWindow")]
        public static string DeleteDoorWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Door/Window"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(elementId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .WithElementId((int)elementId.Value)
                        .WithMessage("Element deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create door schedule data
        /// </summary>
        [MCPMethod("getDoorSchedule", Category = "DoorWindow")]
        public static string GetDoorSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var doors = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilyInstance>()
                    .Select(d => new
                    {
                        doorId = (int)d.Id.Value,
                        mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        typeName = d.Symbol.Name,
                        width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                        height = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                        level = doc.GetElement(d.LevelId)?.Name,
                        fromRoom = d.FromRoom?.Number,
                        toRoom = d.ToRoom?.Number,
                        comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                    })
                    .OrderBy(d => d.mark)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("doorCount", doors.Count)
                    .With("doors", doors)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create window schedule data
        /// </summary>
        [MCPMethod("getWindowSchedule", Category = "DoorWindow")]
        public static string GetWindowSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var windows = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilyInstance>()
                    .Select(w => new
                    {
                        windowId = (int)w.Id.Value,
                        mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        typeName = w.Symbol.Name,
                        width = w.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                        height = w.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                        sillHeight = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0,
                        level = doc.GetElement(w.LevelId)?.Name,
                        comments = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                    })
                    .OrderBy(w => w.mark)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("windowCount", windows.Count)
                    .With("windows", windows)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL doors in the entire model (not view-specific)
        /// </summary>
        [MCPMethod("getDoors", Category = "DoorWindow", Description = "Get all doors in the entire model")]
        public static string GetDoors(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document open in Revit", "NO_ACTIVE_DOCUMENT").Build();
                }
                var doc = uiApp.ActiveUIDocument.Document;

                var doors = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilyInstance>()
                    .Select(d => {
                        // Get location point
                        var location = d.Location as LocationPoint;
                        var point = location?.Point;

                        // Get host wall ID
                        var hostId = d.Host?.Id.Value ?? -1;

                        return new
                        {
                            doorId = (int)d.Id.Value,
                            familyName = d.Symbol?.Family?.Name ?? "Unknown",
                            typeName = d.Symbol?.Name ?? "Unknown",
                            typeId = d.Symbol != null ? (int)d.Symbol.Id.Value : 0,
                            mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                            height = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                            level = doc.GetElement(d.LevelId)?.Name,
                            hostWallId = (int)hostId,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null,
                            fromRoom = d.FromRoom?.Name,
                            toRoom = d.ToRoom?.Name
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("doorCount", doors.Count)
                    .With("doors", doors)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL windows in the entire model (not view-specific)
        /// </summary>
        [MCPMethod("getWindows", Category = "DoorWindow", Description = "Get all windows in the entire model")]
        public static string GetWindows(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return ResponseBuilder.Error("No active document open in Revit", "NO_ACTIVE_DOCUMENT").Build();
                }
                var doc = uiApp.ActiveUIDocument.Document;

                var windows = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilyInstance>()
                    .Select(w => {
                        // Get location point
                        var location = w.Location as LocationPoint;
                        var point = location?.Point;

                        // Get host wall ID
                        var hostId = w.Host?.Id.Value ?? -1;

                        return new
                        {
                            windowId = (int)w.Id.Value,
                            familyName = w.Symbol?.Family?.Name ?? "Unknown",
                            typeName = w.Symbol?.Name ?? "Unknown",
                            typeId = w.Symbol != null ? (int)w.Symbol.Id.Value : 0,
                            mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            width = w.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                            height = w.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                            sillHeight = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0,
                            level = doc.GetElement(w.LevelId)?.Name,
                            hostWallId = (int)hostId,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("windowCount", windows.Count)
                    .With("windows", windows)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL furniture in the entire model
        /// </summary>
        [MCPMethod("getFurniture", Category = "DoorWindow")]
        public static string GetFurniture(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var furniture = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Furniture)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Select(f => {
                        var location = f.Location as LocationPoint;
                        var point = location?.Point;

                        return new
                        {
                            furnitureId = (int)f.Id.Value,
                            familyName = f.Symbol.Family.Name,
                            typeName = f.Symbol.Name,
                            typeId = (int)f.Symbol.Id.Value,
                            level = doc.GetElement(f.LevelId)?.Name,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null,
                            rotation = (f.Location as LocationPoint)?.Rotation ?? 0
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("furnitureCount", furniture.Count)
                    .With("furniture", furniture)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL plumbing fixtures in the entire model
        /// </summary>
        [MCPMethod("getPlumbingFixtures", Category = "DoorWindow")]
        public static string GetPlumbingFixtures(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var fixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Select(f => {
                        var location = f.Location as LocationPoint;
                        var point = location?.Point;

                        return new
                        {
                            fixtureId = (int)f.Id.Value,
                            familyName = f.Symbol.Family.Name,
                            typeName = f.Symbol.Name,
                            typeId = (int)f.Symbol.Id.Value,
                            level = doc.GetElement(f.LevelId)?.Name,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null,
                            rotation = (f.Location as LocationPoint)?.Rotation ?? 0
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("fixtureCount", fixtures.Count)
                    .With("plumbingFixtures", fixtures)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL lighting fixtures in the entire model
        /// </summary>
        [MCPMethod("getLightingFixtures", Category = "DoorWindow")]
        public static string GetLightingFixtures(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var fixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingFixtures)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Select(f => {
                        var location = f.Location as LocationPoint;
                        var point = location?.Point;

                        return new
                        {
                            fixtureId = (int)f.Id.Value,
                            familyName = f.Symbol.Family.Name,
                            typeName = f.Symbol.Name,
                            typeId = (int)f.Symbol.Id.Value,
                            level = doc.GetElement(f.LevelId)?.Name,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("fixtureCount", fixtures.Count)
                    .With("lightingFixtures", fixtures)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL electrical fixtures in the entire model
        /// </summary>
        [MCPMethod("getElectricalFixtures", Category = "DoorWindow")]
        public static string GetElectricalFixtures(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var fixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Select(f => {
                        var location = f.Location as LocationPoint;
                        var point = location?.Point;

                        return new
                        {
                            fixtureId = (int)f.Id.Value,
                            familyName = f.Symbol.Family.Name,
                            typeName = f.Symbol.Name,
                            typeId = (int)f.Symbol.Id.Value,
                            level = doc.GetElement(f.LevelId)?.Name,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("fixtureCount", fixtures.Count)
                    .With("electricalFixtures", fixtures)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // =====================================================================
        // NEW METHODS: Detailed info, types, modify, swap, flip, move, delete,
        //              by-wall, schedule data, batch placement, from/to room
        // =====================================================================

        /// <summary>
        /// Get detailed info for a specific door
        /// </summary>
        [MCPMethod("getDoorInfo", Category = "DoorWindow", Description = "Get detailed info for a specific door including type, dimensions, host wall, rooms, fire rating, and all instance parameters")]
        public static string GetDoorInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetDoorInfo");
                v.Require("doorId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);

                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                var symbol = door.Symbol;
                var host = door.Host;
                var level = doc.GetElement(door.LevelId) as Level;
                var location = (door.Location as LocationPoint)?.Point;

                // Collect all instance parameters
                var instanceParams = new Dictionary<string, object>();
                foreach (Parameter param in door.Parameters)
                {
                    if (param.IsReadOnly && param.Definition == null) continue;
                    var paramName = param.Definition?.Name;
                    if (string.IsNullOrEmpty(paramName)) continue;

                    object paramValue = null;
                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            paramValue = param.AsString();
                            break;
                        case StorageType.Integer:
                            paramValue = param.AsInteger();
                            break;
                        case StorageType.Double:
                            paramValue = param.AsDouble();
                            break;
                        case StorageType.ElementId:
                            var elemId = param.AsElementId();
                            var elemName = doc.GetElement(elemId)?.Name;
                            paramValue = elemName ?? ((int)elemId.Value).ToString();
                            break;
                    }
                    if (paramValue != null && !instanceParams.ContainsKey(paramName))
                    {
                        instanceParams[paramName] = paramValue;
                    }
                }

                return ResponseBuilder.Success()
                    .With("doorId", doorIdInt)
                    .With("familyName", symbol.Family.Name)
                    .With("typeName", symbol.Name)
                    .With("typeId", (int)symbol.Id.Value)
                    .With("width", door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0)
                    .With("height", door.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0)
                    .With("level", level?.Name)
                    .With("levelId", level != null ? (int)level.Id.Value : -1)
                    .With("hostWallId", host != null ? (int)host.Id.Value : -1)
                    .With("fromRoom", door.FromRoom != null ? new { roomId = (int)door.FromRoom.Id.Value, name = door.FromRoom.Name, number = door.FromRoom.Number } : null)
                    .With("toRoom", door.ToRoom != null ? new { roomId = (int)door.ToRoom.Id.Value, name = door.ToRoom.Name, number = door.ToRoom.Number } : null)
                    .With("fireRating", door.get_Parameter(BuiltInParameter.FIRE_RATING)?.AsString())
                    .With("mark", door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                    .With("comments", door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                    .With("handFlipped", door.HandFlipped)
                    .With("facingFlipped", door.FacingFlipped)
                    .With("location", location != null ? new { x = location.X, y = location.Y, z = location.Z } : null)
                    .With("instanceParameters", instanceParams)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detailed info for a specific window
        /// </summary>
        [MCPMethod("getWindowInfo", Category = "DoorWindow", Description = "Get detailed info for a specific window including type, dimensions, sill height, host wall, and all instance parameters")]
        public static string GetWindowInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetWindowInfo");
                v.Require("windowId").IsType<int>();
                v.ThrowIfInvalid();

                var windowIdInt = v.GetRequired<int>("windowId");
                var window = ElementLookup.GetElement<FamilyInstance>(doc, windowIdInt);

                if (window.Category.BuiltInCategory != BuiltInCategory.OST_Windows)
                {
                    return ResponseBuilder.Error("Element is not a window", "INVALID_CATEGORY").Build();
                }

                var symbol = window.Symbol;
                var host = window.Host;
                var level = doc.GetElement(window.LevelId) as Level;
                var location = (window.Location as LocationPoint)?.Point;

                // Collect all instance parameters
                var instanceParams = new Dictionary<string, object>();
                foreach (Parameter param in window.Parameters)
                {
                    if (param.IsReadOnly && param.Definition == null) continue;
                    var paramName = param.Definition?.Name;
                    if (string.IsNullOrEmpty(paramName)) continue;

                    object paramValue = null;
                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            paramValue = param.AsString();
                            break;
                        case StorageType.Integer:
                            paramValue = param.AsInteger();
                            break;
                        case StorageType.Double:
                            paramValue = param.AsDouble();
                            break;
                        case StorageType.ElementId:
                            var elemId = param.AsElementId();
                            var elemName = doc.GetElement(elemId)?.Name;
                            paramValue = elemName ?? ((int)elemId.Value).ToString();
                            break;
                    }
                    if (paramValue != null && !instanceParams.ContainsKey(paramName))
                    {
                        instanceParams[paramName] = paramValue;
                    }
                }

                return ResponseBuilder.Success()
                    .With("windowId", windowIdInt)
                    .With("familyName", symbol.Family.Name)
                    .With("typeName", symbol.Name)
                    .With("typeId", (int)symbol.Id.Value)
                    .With("width", window.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0)
                    .With("height", window.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0)
                    .With("sillHeight", window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0)
                    .With("level", level?.Name)
                    .With("levelId", level != null ? (int)level.Id.Value : -1)
                    .With("hostWallId", host != null ? (int)host.Id.Value : -1)
                    .With("mark", window.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                    .With("comments", window.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                    .With("handFlipped", window.HandFlipped)
                    .With("facingFlipped", window.FacingFlipped)
                    .With("location", location != null ? new { x = location.X, y = location.Y, z = location.Z } : null)
                    .With("instanceParameters", instanceParams)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all door family types available in the document
        /// </summary>
        [MCPMethod("getDoorTypes", Category = "DoorWindow", Description = "Get all door family types available in the document")]
        public static string GetDoorTypesMethod(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var doorTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilySymbol>()
                    .Select(dt => new
                    {
                        typeId = (int)dt.Id.Value,
                        familyName = dt.Family.Name,
                        typeName = dt.Name,
                        width = dt.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                        height = dt.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                        fireRating = dt.get_Parameter(BuiltInParameter.FIRE_RATING)?.AsString(),
                        isActive = dt.IsActive
                    })
                    .OrderBy(dt => dt.familyName)
                    .ThenBy(dt => dt.typeName)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("doorTypeCount", doorTypes.Count)
                    .With("doorTypes", doorTypes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all window family types available in the document
        /// </summary>
        [MCPMethod("getWindowTypes", Category = "DoorWindow", Description = "Get all window family types available in the document")]
        public static string GetWindowTypesMethod(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var windowTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilySymbol>()
                    .Select(wt => new
                    {
                        typeId = (int)wt.Id.Value,
                        familyName = wt.Family.Name,
                        typeName = wt.Name,
                        width = wt.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                        height = wt.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                        isActive = wt.IsActive
                    })
                    .OrderBy(wt => wt.familyName)
                    .ThenBy(wt => wt.typeName)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("windowTypeCount", windowTypes.Count)
                    .With("windowTypes", windowTypes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify door properties by setting key/value parameter pairs
        /// </summary>
        [MCPMethod("modifyDoor", Category = "DoorWindow", Description = "Modify door properties by setting key/value parameter pairs")]
        public static string ModifyDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "ModifyDoor");
                v.Require("doorId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);

                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                var paramsToSet = parameters["parameters"] as JObject;
                if (paramsToSet == null || !paramsToSet.HasValues)
                {
                    return ResponseBuilder.Error("'parameters' object with key/value pairs is required", "VALIDATION_ERROR").Build();
                }

                using (var trans = new Transaction(doc, "Modify Door"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    var modified = new List<string>();
                    var failed = new List<string>();

                    foreach (var prop in paramsToSet.Properties())
                    {
                        var param = door.LookupParameter(prop.Name);
                        if (param == null || param.IsReadOnly)
                        {
                            failed.Add(prop.Name);
                            continue;
                        }

                        try
                        {
                            switch (param.StorageType)
                            {
                                case StorageType.String:
                                    param.Set(prop.Value.ToString());
                                    modified.Add(prop.Name);
                                    break;
                                case StorageType.Integer:
                                    param.Set(prop.Value.Value<int>());
                                    modified.Add(prop.Name);
                                    break;
                                case StorageType.Double:
                                    param.Set(prop.Value.Value<double>());
                                    modified.Add(prop.Name);
                                    break;
                                case StorageType.ElementId:
                                    param.Set(new ElementId(prop.Value.Value<int>()));
                                    modified.Add(prop.Name);
                                    break;
                                default:
                                    failed.Add(prop.Name);
                                    break;
                            }
                        }
                        catch
                        {
                            failed.Add(prop.Name);
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("doorId", doorIdInt)
                        .With("modified", modified)
                        .With("failed", failed)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify window properties by setting key/value parameter pairs
        /// </summary>
        [MCPMethod("modifyWindow", Category = "DoorWindow", Description = "Modify window properties by setting key/value parameter pairs")]
        public static string ModifyWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "ModifyWindow");
                v.Require("windowId").IsType<int>();
                v.ThrowIfInvalid();

                var windowIdInt = v.GetRequired<int>("windowId");
                var window = ElementLookup.GetElement<FamilyInstance>(doc, windowIdInt);

                if (window.Category.BuiltInCategory != BuiltInCategory.OST_Windows)
                {
                    return ResponseBuilder.Error("Element is not a window", "INVALID_CATEGORY").Build();
                }

                var paramsToSet = parameters["parameters"] as JObject;
                if (paramsToSet == null || !paramsToSet.HasValues)
                {
                    return ResponseBuilder.Error("'parameters' object with key/value pairs is required", "VALIDATION_ERROR").Build();
                }

                using (var trans = new Transaction(doc, "Modify Window"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    var modified = new List<string>();
                    var failed = new List<string>();

                    foreach (var prop in paramsToSet.Properties())
                    {
                        var param = window.LookupParameter(prop.Name);
                        if (param == null || param.IsReadOnly)
                        {
                            failed.Add(prop.Name);
                            continue;
                        }

                        try
                        {
                            switch (param.StorageType)
                            {
                                case StorageType.String:
                                    param.Set(prop.Value.ToString());
                                    modified.Add(prop.Name);
                                    break;
                                case StorageType.Integer:
                                    param.Set(prop.Value.Value<int>());
                                    modified.Add(prop.Name);
                                    break;
                                case StorageType.Double:
                                    param.Set(prop.Value.Value<double>());
                                    modified.Add(prop.Name);
                                    break;
                                case StorageType.ElementId:
                                    param.Set(new ElementId(prop.Value.Value<int>()));
                                    modified.Add(prop.Name);
                                    break;
                                default:
                                    failed.Add(prop.Name);
                                    break;
                            }
                        }
                        catch
                        {
                            failed.Add(prop.Name);
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("windowId", windowIdInt)
                        .With("modified", modified)
                        .With("failed", failed)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change a door's type to a different family type
        /// </summary>
        [MCPMethod("swapDoorType", Category = "DoorWindow", Description = "Change a door's family type")]
        public static string SwapDoorType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "SwapDoorType");
                v.Require("doorId").IsType<int>();
                v.Require("newTypeId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var newTypeIdInt = v.GetRequired<int>("newTypeId");

                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);
                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                var newType = doc.GetElement(new ElementId(newTypeIdInt)) as FamilySymbol;
                if (newType == null)
                {
                    return ResponseBuilder.Error("New door type not found", "TYPE_NOT_FOUND").Build();
                }

                var oldTypeName = door.Symbol.Name;

                using (var trans = new Transaction(doc, "Swap Door Type"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    if (!newType.IsActive)
                    {
                        newType.Activate();
                    }

                    door.Symbol = newType;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("doorId", doorIdInt)
                        .With("oldTypeName", oldTypeName)
                        .With("newTypeName", newType.Name)
                        .With("newTypeId", newTypeIdInt)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change a window's type to a different family type
        /// </summary>
        [MCPMethod("swapWindowType", Category = "DoorWindow", Description = "Change a window's family type")]
        public static string SwapWindowType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "SwapWindowType");
                v.Require("windowId").IsType<int>();
                v.Require("newTypeId").IsType<int>();
                v.ThrowIfInvalid();

                var windowIdInt = v.GetRequired<int>("windowId");
                var newTypeIdInt = v.GetRequired<int>("newTypeId");

                var window = ElementLookup.GetElement<FamilyInstance>(doc, windowIdInt);
                if (window.Category.BuiltInCategory != BuiltInCategory.OST_Windows)
                {
                    return ResponseBuilder.Error("Element is not a window", "INVALID_CATEGORY").Build();
                }

                var newType = doc.GetElement(new ElementId(newTypeIdInt)) as FamilySymbol;
                if (newType == null)
                {
                    return ResponseBuilder.Error("New window type not found", "TYPE_NOT_FOUND").Build();
                }

                var oldTypeName = window.Symbol.Name;

                using (var trans = new Transaction(doc, "Swap Window Type"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    if (!newType.IsActive)
                    {
                        newType.Activate();
                    }

                    window.Symbol = newType;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("windowId", windowIdInt)
                        .With("oldTypeName", oldTypeName)
                        .With("newTypeName", newType.Name)
                        .With("newTypeId", newTypeIdInt)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Flip door hand and/or facing orientation
        /// </summary>
        [MCPMethod("flipDoor", Category = "DoorWindow", Description = "Flip door hand and/or facing orientation")]
        public static string FlipDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "FlipDoor");
                v.Require("doorId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var flipHand = v.GetOptional<bool>("flipHand", false);
                var flipFacing = v.GetOptional<bool>("flipFacing", false);

                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);
                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                if (!flipHand && !flipFacing)
                {
                    return ResponseBuilder.Error("At least one of 'flipHand' or 'flipFacing' must be true", "VALIDATION_ERROR").Build();
                }

                using (var trans = new Transaction(doc, "Flip Door"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    bool handFlipped = false;
                    bool facingFlipped = false;

                    if (flipHand && door.CanFlipHand)
                    {
                        door.flipHand();
                        handFlipped = true;
                    }

                    if (flipFacing && door.CanFlipFacing)
                    {
                        door.flipFacing();
                        facingFlipped = true;
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("doorId", doorIdInt)
                        .With("handFlipped", handFlipped)
                        .With("facingFlipped", facingFlipped)
                        .With("canFlipHand", door.CanFlipHand)
                        .With("canFlipFacing", door.CanFlipFacing)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move a door to a new location point along its host wall
        /// </summary>
        [MCPMethod("moveDoor", Category = "DoorWindow", Description = "Move a door to a new location point")]
        public static string MoveDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "MoveDoor");
                v.Require("doorId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);

                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                var newLocToken = parameters["newLocationPoint"];
                if (newLocToken == null)
                {
                    return ResponseBuilder.Error("'newLocationPoint' with {x, y, z} is required", "VALIDATION_ERROR").Build();
                }

                var x = newLocToken["x"]?.Value<double>() ?? 0;
                var y = newLocToken["y"]?.Value<double>() ?? 0;
                var z = newLocToken["z"]?.Value<double>() ?? 0;
                var newPoint = new XYZ(x, y, z);

                var locationPoint = door.Location as LocationPoint;
                if (locationPoint == null)
                {
                    return ResponseBuilder.Error("Door does not have a point location", "INVALID_GEOMETRY").Build();
                }

                var oldPoint = locationPoint.Point;

                using (var trans = new Transaction(doc, "Move Door"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    locationPoint.Point = newPoint;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("doorId", doorIdInt)
                        .With("oldLocation", new { x = oldPoint.X, y = oldPoint.Y, z = oldPoint.Z })
                        .With("newLocation", new { x = newPoint.X, y = newPoint.Y, z = newPoint.Z })
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move a window to a new location point along its host wall
        /// </summary>
        [MCPMethod("moveWindow", Category = "DoorWindow", Description = "Move a window to a new location point")]
        public static string MoveWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "MoveWindow");
                v.Require("windowId").IsType<int>();
                v.ThrowIfInvalid();

                var windowIdInt = v.GetRequired<int>("windowId");
                var window = ElementLookup.GetElement<FamilyInstance>(doc, windowIdInt);

                if (window.Category.BuiltInCategory != BuiltInCategory.OST_Windows)
                {
                    return ResponseBuilder.Error("Element is not a window", "INVALID_CATEGORY").Build();
                }

                var newLocToken = parameters["newLocationPoint"];
                if (newLocToken == null)
                {
                    return ResponseBuilder.Error("'newLocationPoint' with {x, y, z} is required", "VALIDATION_ERROR").Build();
                }

                var x = newLocToken["x"]?.Value<double>() ?? 0;
                var y = newLocToken["y"]?.Value<double>() ?? 0;
                var z = newLocToken["z"]?.Value<double>() ?? 0;
                var newPoint = new XYZ(x, y, z);

                var locationPoint = window.Location as LocationPoint;
                if (locationPoint == null)
                {
                    return ResponseBuilder.Error("Window does not have a point location", "INVALID_GEOMETRY").Build();
                }

                var oldPoint = locationPoint.Point;

                using (var trans = new Transaction(doc, "Move Window"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    locationPoint.Point = newPoint;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("windowId", windowIdInt)
                        .With("oldLocation", new { x = oldPoint.X, y = oldPoint.Y, z = oldPoint.Z })
                        .With("newLocation", new { x = newPoint.X, y = newPoint.Y, z = newPoint.Z })
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a door from the model
        /// </summary>
        [MCPMethod("deleteDoor", Category = "DoorWindow", Description = "Delete a door from the model")]
        public static string DeleteDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "DeleteDoor");
                v.Require("doorId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);

                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                var typeName = door.Symbol.Name;

                using (var trans = new Transaction(doc, "Delete Door"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    doc.Delete(door.Id);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("deletedDoorId", doorIdInt)
                        .With("typeName", typeName)
                        .WithMessage("Door deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a window from the model
        /// </summary>
        [MCPMethod("deleteWindow", Category = "DoorWindow", Description = "Delete a window from the model")]
        public static string DeleteWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "DeleteWindow");
                v.Require("windowId").IsType<int>();
                v.ThrowIfInvalid();

                var windowIdInt = v.GetRequired<int>("windowId");
                var window = ElementLookup.GetElement<FamilyInstance>(doc, windowIdInt);

                if (window.Category.BuiltInCategory != BuiltInCategory.OST_Windows)
                {
                    return ResponseBuilder.Error("Element is not a window", "INVALID_CATEGORY").Build();
                }

                var typeName = window.Symbol.Name;

                using (var trans = new Transaction(doc, "Delete Window"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    doc.Delete(window.Id);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("deletedWindowId", windowIdInt)
                        .With("typeName", typeName)
                        .WithMessage("Window deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all doors hosted by a specific wall
        /// </summary>
        [MCPMethod("getDoorsByWall", Category = "DoorWindow", Description = "Get all doors hosted by a specific wall")]
        public static string GetDoorsByWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetDoorsByWall");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallIdInt = v.GetRequired<int>("wallId");
                var wall = ElementLookup.GetWall(doc, wallIdInt);

                var doors = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilyInstance>()
                    .Where(d => d.Host != null && d.Host.Id.Value == wall.Id.Value)
                    .Select(d =>
                    {
                        var location = (d.Location as LocationPoint)?.Point;
                        return new
                        {
                            doorId = (int)d.Id.Value,
                            familyName = d.Symbol?.Family?.Name ?? "Unknown",
                            typeName = d.Symbol?.Name ?? "Unknown",
                            typeId = d.Symbol != null ? (int)d.Symbol.Id.Value : 0,
                            mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                            height = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                            location = location != null ? new { x = location.X, y = location.Y, z = location.Z } : null
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallId", wallIdInt)
                    .With("doorCount", doors.Count)
                    .With("doors", doors)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all windows hosted by a specific wall
        /// </summary>
        [MCPMethod("getWindowsByWall", Category = "DoorWindow", Description = "Get all windows hosted by a specific wall")]
        public static string GetWindowsByWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetWindowsByWall");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallIdInt = v.GetRequired<int>("wallId");
                var wall = ElementLookup.GetWall(doc, wallIdInt);

                var windows = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilyInstance>()
                    .Where(w => w.Host != null && w.Host.Id.Value == wall.Id.Value)
                    .Select(w =>
                    {
                        var location = (w.Location as LocationPoint)?.Point;
                        return new
                        {
                            windowId = (int)w.Id.Value,
                            familyName = w.Symbol?.Family?.Name ?? "Unknown",
                            typeName = w.Symbol?.Name ?? "Unknown",
                            typeId = w.Symbol != null ? (int)w.Symbol.Id.Value : 0,
                            mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            width = w.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                            height = w.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                            sillHeight = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0,
                            location = location != null ? new { x = location.X, y = location.Y, z = location.Z } : null
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallId", wallIdInt)
                    .With("windowCount", windows.Count)
                    .With("windows", windows)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get door schedule data for all doors (all schedule-relevant parameters)
        /// </summary>
        [MCPMethod("getDoorScheduleData", Category = "DoorWindow", Description = "Get door schedule data for all doors with all schedule-relevant parameters")]
        public static string GetDoorScheduleData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var doors = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilyInstance>()
                    .Select(d =>
                    {
                        var level = doc.GetElement(d.LevelId) as Level;
                        return new
                        {
                            doorId = (int)d.Id.Value,
                            mark = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            familyName = d.Symbol?.Family?.Name ?? "Unknown",
                            typeName = d.Symbol?.Name ?? "Unknown",
                            typeId = d.Symbol != null ? (int)d.Symbol.Id.Value : 0,
                            width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0,
                            height = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0,
                            level = level?.Name,
                            levelId = level != null ? (int)level.Id.Value : -1,
                            fromRoom = d.FromRoom != null ? new { number = d.FromRoom.Number, name = d.FromRoom.Name } : null,
                            toRoom = d.ToRoom != null ? new { number = d.ToRoom.Number, name = d.ToRoom.Name } : null,
                            fireRating = d.get_Parameter(BuiltInParameter.FIRE_RATING)?.AsString(),
                            comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                            handFlipped = d.HandFlipped,
                            facingFlipped = d.FacingFlipped,
                            hostWallId = d.Host != null ? (int)d.Host.Id.Value : -1
                        };
                    })
                    .OrderBy(d => d.mark)
                    .ThenBy(d => d.level)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("doorCount", doors.Count)
                    .With("doors", doors)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get window schedule data for all windows (all schedule-relevant parameters)
        /// </summary>
        [MCPMethod("getWindowScheduleData", Category = "DoorWindow", Description = "Get window schedule data for all windows with all schedule-relevant parameters")]
        public static string GetWindowScheduleData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var windows = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilyInstance>()
                    .Select(w =>
                    {
                        var level = doc.GetElement(w.LevelId) as Level;
                        return new
                        {
                            windowId = (int)w.Id.Value,
                            mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            familyName = w.Symbol?.Family?.Name ?? "Unknown",
                            typeName = w.Symbol?.Name ?? "Unknown",
                            typeId = w.Symbol != null ? (int)w.Symbol.Id.Value : 0,
                            width = w.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble() ?? 0,
                            height = w.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble() ?? 0,
                            sillHeight = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0,
                            level = level?.Name,
                            levelId = level != null ? (int)level.Id.Value : -1,
                            comments = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                            handFlipped = w.HandFlipped,
                            facingFlipped = w.FacingFlipped,
                            hostWallId = w.Host != null ? (int)w.Host.Id.Value : -1
                        };
                    })
                    .OrderBy(w => w.mark)
                    .ThenBy(w => w.level)
                    .ToList();

                return ResponseBuilder.Success()
                    .With("windowCount", windows.Count)
                    .With("windows", windows)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place multiple doors at once (batch operation)
        /// </summary>
        [MCPMethod("batchPlaceDoors", Category = "DoorWindow", Description = "Place multiple doors at once from an array of placement specs")]
        public static string BatchPlaceDoors(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var doorsArray = parameters["doors"] as JArray;
                if (doorsArray == null || doorsArray.Count == 0)
                {
                    return ResponseBuilder.Error("'doors' array is required with at least one entry", "VALIDATION_ERROR").Build();
                }

                var placed = new List<object>();
                var errors = new List<object>();

                using (var trans = new Transaction(doc, "Batch Place Doors"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    for (int i = 0; i < doorsArray.Count; i++)
                    {
                        try
                        {
                            var spec = doorsArray[i] as JObject;
                            if (spec == null)
                            {
                                errors.Add(new { index = i, error = "Invalid door specification" });
                                continue;
                            }

                            var wallIdToken = spec["wallId"];
                            var typeIdToken = spec["typeId"];
                            var locationToken = spec["location"];
                            var levelIdToken = spec["levelId"];

                            if (wallIdToken == null)
                            {
                                errors.Add(new { index = i, error = "wallId is required" });
                                continue;
                            }

                            var wallId = new ElementId(wallIdToken.Value<int>());
                            var wall = doc.GetElement(wallId) as Wall;
                            if (wall == null)
                            {
                                errors.Add(new { index = i, error = $"Wall {wallIdToken} not found" });
                                continue;
                            }

                            // Get door type
                            FamilySymbol doorType = null;
                            if (typeIdToken != null)
                            {
                                doorType = doc.GetElement(new ElementId(typeIdToken.Value<int>())) as FamilySymbol;
                            }
                            if (doorType == null)
                            {
                                doorType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FamilySymbol))
                                    .OfCategory(BuiltInCategory.OST_Doors)
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault();
                            }
                            if (doorType == null)
                            {
                                errors.Add(new { index = i, error = "No door type available" });
                                continue;
                            }

                            if (!doorType.IsActive) doorType.Activate();

                            // Get level
                            Level level = null;
                            if (levelIdToken != null)
                            {
                                level = doc.GetElement(new ElementId(levelIdToken.Value<int>())) as Level;
                            }
                            if (level == null)
                            {
                                level = doc.GetElement(wall.LevelId) as Level;
                            }
                            if (level == null)
                            {
                                errors.Add(new { index = i, error = "Could not determine level" });
                                continue;
                            }

                            // Get location
                            XYZ location;
                            if (locationToken != null)
                            {
                                var lx = locationToken["x"]?.Value<double>() ?? 0;
                                var ly = locationToken["y"]?.Value<double>() ?? 0;
                                var lz = locationToken["z"]?.Value<double>() ?? 0;
                                location = new XYZ(lx, ly, lz);
                            }
                            else
                            {
                                var lc = wall.Location as LocationCurve;
                                if (lc == null)
                                {
                                    errors.Add(new { index = i, error = "Wall has no location curve and no location specified" });
                                    continue;
                                }
                                location = lc.Curve.Evaluate(0.5, true);
                            }

                            var door = doc.Create.NewFamilyInstance(
                                location, doorType, wall, level,
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                            placed.Add(new
                            {
                                index = i,
                                doorId = (int)door.Id.Value,
                                typeName = doorType.Name,
                                wallId = (int)wallId.Value
                            });
                        }
                        catch (Exception innerEx)
                        {
                            errors.Add(new { index = i, error = innerEx.Message });
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("placedCount", placed.Count)
                        .With("errorCount", errors.Count)
                        .With("placed", placed)
                        .With("errors", errors)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place multiple windows at once (batch operation)
        /// </summary>
        [MCPMethod("batchPlaceWindows", Category = "DoorWindow", Description = "Place multiple windows at once from an array of placement specs")]
        public static string BatchPlaceWindows(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var windowsArray = parameters["windows"] as JArray;
                if (windowsArray == null || windowsArray.Count == 0)
                {
                    return ResponseBuilder.Error("'windows' array is required with at least one entry", "VALIDATION_ERROR").Build();
                }

                var placed = new List<object>();
                var errors = new List<object>();

                using (var trans = new Transaction(doc, "Batch Place Windows"))
                {
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    trans.Start();

                    for (int i = 0; i < windowsArray.Count; i++)
                    {
                        try
                        {
                            var spec = windowsArray[i] as JObject;
                            if (spec == null)
                            {
                                errors.Add(new { index = i, error = "Invalid window specification" });
                                continue;
                            }

                            var wallIdToken = spec["wallId"];
                            var typeIdToken = spec["typeId"];
                            var locationToken = spec["location"];
                            var levelIdToken = spec["levelId"];

                            if (wallIdToken == null)
                            {
                                errors.Add(new { index = i, error = "wallId is required" });
                                continue;
                            }

                            var wallId = new ElementId(wallIdToken.Value<int>());
                            var wall = doc.GetElement(wallId) as Wall;
                            if (wall == null)
                            {
                                errors.Add(new { index = i, error = $"Wall {wallIdToken} not found" });
                                continue;
                            }

                            // Get window type
                            FamilySymbol windowType = null;
                            if (typeIdToken != null)
                            {
                                windowType = doc.GetElement(new ElementId(typeIdToken.Value<int>())) as FamilySymbol;
                            }
                            if (windowType == null)
                            {
                                windowType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FamilySymbol))
                                    .OfCategory(BuiltInCategory.OST_Windows)
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault();
                            }
                            if (windowType == null)
                            {
                                errors.Add(new { index = i, error = "No window type available" });
                                continue;
                            }

                            if (!windowType.IsActive) windowType.Activate();

                            // Get level
                            Level level = null;
                            if (levelIdToken != null)
                            {
                                level = doc.GetElement(new ElementId(levelIdToken.Value<int>())) as Level;
                            }
                            if (level == null)
                            {
                                level = doc.GetElement(wall.LevelId) as Level;
                            }
                            if (level == null)
                            {
                                errors.Add(new { index = i, error = "Could not determine level" });
                                continue;
                            }

                            // Get location
                            XYZ location;
                            if (locationToken != null)
                            {
                                var lx = locationToken["x"]?.Value<double>() ?? 0;
                                var ly = locationToken["y"]?.Value<double>() ?? 0;
                                var lz = locationToken["z"]?.Value<double>() ?? 0;
                                location = new XYZ(lx, ly, lz);
                            }
                            else
                            {
                                var lc = wall.Location as LocationCurve;
                                if (lc == null)
                                {
                                    errors.Add(new { index = i, error = "Wall has no location curve and no location specified" });
                                    continue;
                                }
                                location = lc.Curve.Evaluate(0.5, true);
                            }

                            var window = doc.Create.NewFamilyInstance(
                                location, windowType, wall, level,
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                            placed.Add(new
                            {
                                index = i,
                                windowId = (int)window.Id.Value,
                                typeName = windowType.Name,
                                wallId = (int)wallId.Value
                            });
                        }
                        catch (Exception innerEx)
                        {
                            errors.Add(new { index = i, error = innerEx.Message });
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("placedCount", placed.Count)
                        .With("errorCount", errors.Count)
                        .With("placed", placed)
                        .With("errors", errors)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get from/to room information for a door
        /// </summary>
        [MCPMethod("getDoorFromToRoom", Category = "DoorWindow", Description = "Get from/to room info for a door")]
        public static string GetDoorFromToRoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "GetDoorFromToRoom");
                v.Require("doorId").IsType<int>();
                v.ThrowIfInvalid();

                var doorIdInt = v.GetRequired<int>("doorId");
                var door = ElementLookup.GetElement<FamilyInstance>(doc, doorIdInt);

                if (door.Category.BuiltInCategory != BuiltInCategory.OST_Doors)
                {
                    return ResponseBuilder.Error("Element is not a door", "INVALID_CATEGORY").Build();
                }

                // Get phase for room calculation
                var phase = doc.GetElement(door.CreatedPhaseId) as Phase;

                // Try to get rooms from both phase-aware and direct properties
                Room fromRoom = null;
                Room toRoom = null;

                if (phase != null)
                {
                    fromRoom = door.get_FromRoom(phase);
                    toRoom = door.get_ToRoom(phase);
                }

                // Fallback to direct properties
                if (fromRoom == null) fromRoom = door.FromRoom;
                if (toRoom == null) toRoom = door.ToRoom;

                return ResponseBuilder.Success()
                    .With("doorId", doorIdInt)
                    .With("doorMark", door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                    .With("fromRoom", fromRoom != null ? new
                    {
                        roomId = (int)fromRoom.Id.Value,
                        name = fromRoom.Name,
                        number = fromRoom.Number,
                        level = doc.GetElement(fromRoom.LevelId)?.Name
                    } : null)
                    .With("toRoom", toRoom != null ? new
                    {
                        roomId = (int)toRoom.Id.Value,
                        name = toRoom.Name,
                        number = toRoom.Number,
                        level = doc.GetElement(toRoom.LevelId)?.Name
                    } : null)
                    .With("phase", phase?.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }

    /// <summary>
    /// Failure preprocessor that suppresses warnings during transactions
    /// </summary>
    public class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            foreach (var failure in failures)
            {
                // Delete warnings (severity == Warning)
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
