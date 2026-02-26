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
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall not found"
                    });
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
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid door type found"
                    });
                }

                // NULL SAFETY: Validate level before using
                var level = doc.GetElement(wall.LevelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall level not found. Cannot place door without a valid level."
                    });
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
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Location must be an array of 3 numbers [x, y, z]"
                            });
                        }
                        location = new XYZ(loc[0], loc[1], loc[2]);
                    }
                    else
                    {
                        // Place at wall midpoint - with null safety check
                        var locationCurve = wall.Location as LocationCurve;
                        if (locationCurve == null || locationCurve.Curve == null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Wall does not have a valid location curve. Cannot determine door placement position."
                            });
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
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Transaction failed with status: {commitResult}"
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        doorId = (int)doorId,
                        doorType = doorTypeName,
                        wallId = (int)wallId.Value,
                        level = level.Name
                    });
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
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall not found"
                    });
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
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid window type found"
                    });
                }

                // NULL SAFETY: Validate level before using
                var level = doc.GetElement(wall.LevelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall level not found. Cannot place window without a valid level."
                    });
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
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Location must be an array of 3 numbers [x, y, z]"
                            });
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
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Wall does not have a valid location curve. Cannot determine window placement position."
                            });
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
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Transaction failed with status: {commitResult}"
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        windowId = (int)windowId,
                        windowType = windowTypeName,
                        wallId = (int)wallId.Value,
                        level = level.Name
                    });
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
        public static string GetDoorWindowInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId) as FamilyInstance;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)element.Id.Value,
                    category = category,
                    familyName = familySymbol.Family.Name,
                    typeName = familySymbol.Name,
                    typeId = (int)familySymbol.Id.Value,
                    hostId = host != null ? (int)host.Id.Value : -1,
                    level = level?.Name,
                    levelId = (int)element.LevelId.Value,
                    width = width,
                    height = height,
                    location = location != null ? new[] { location.X, location.Y, location.Z } : null,
                    mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify door/window properties
        /// </summary>
        public static string ModifyDoorWindowProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId) as FamilyInstance;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

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

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)element.Id.Value,
                        modified = modified
                    });
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
        public static string FlipDoorWindow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var flipHand = parameters["flipHand"] != null
                    ? bool.Parse(parameters["flipHand"].ToString())
                    : true;
                var flipFacing = parameters["flipFacing"] != null
                    ? bool.Parse(parameters["flipFacing"].ToString())
                    : false;

                var element = doc.GetElement(elementId) as FamilyInstance;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

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

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)elementId.Value,
                        flippedHand = flipHand,
                        flippedFacing = flipFacing
                    });
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
        public static string GetDoorsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Validate UIApplication and document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document open in Revit"
                    });
                }
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate viewId parameter
                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (!int.TryParse(parameters["viewId"].ToString(), out int viewIdInt))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId must be a valid integer"
                    });
                }

                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    doorCount = doors.Count,
                    doors = doors
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all windows in a view
        /// </summary>
        public static string GetWindowsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Validate UIApplication and document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document open in Revit"
                    });
                }
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate viewId parameter
                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId parameter is required"
                    });
                }

                if (!int.TryParse(parameters["viewId"].ToString(), out int viewIdInt))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId must be a valid integer"
                    });
                }

                var viewId = new ElementId(viewIdInt);
                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    windowCount = windows.Count,
                    windows = windows
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all door types
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    doorTypeCount = doorTypes.Count,
                    doorTypes = doorTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all window types
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    windowTypeCount = windowTypes.Count,
                    windowTypes = windowTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete door/window
        /// </summary>
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

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)elementId.Value,
                        message = "Element deleted successfully"
                    });
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    doorCount = doors.Count,
                    doors = doors
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create window schedule data
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    windowCount = windows.Count,
                    windows = windows
                });
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
                // Validate UIApplication and document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document open in Revit"
                    });
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    doorCount = doors.Count,
                    doors = doors
                });
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
                // Validate UIApplication and document
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document open in Revit"
                    });
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    windowCount = windows.Count,
                    windows = windows
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL furniture in the entire model
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    furnitureCount = furniture.Count,
                    furniture = furniture
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL plumbing fixtures in the entire model
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    fixtureCount = fixtures.Count,
                    plumbingFixtures = fixtures
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL lighting fixtures in the entire model
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    fixtureCount = fixtures.Count,
                    lightingFixtures = fixtures
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL electrical fixtures in the entire model
        /// </summary>
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

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    fixtureCount = fixtures.Count,
                    electricalFixtures = fixtures
                });
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
