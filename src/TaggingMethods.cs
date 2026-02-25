using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    public static class TaggingMethods
    {
        /// <summary>
        /// Tag a single door element
        /// </summary>
        public static string TagDoor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var doorId = new ElementId(int.Parse(parameters["doorId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                bool addLeader = parameters["addLeader"]?.ToObject<bool>() ?? false;

                var door = doc.GetElement(doorId) as FamilyInstance;
                var view = doc.GetElement(viewId) as View;

                if (door == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Door not found or not a FamilyInstance"
                    });
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Tag Door"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get door location
                    var location = (door.Location as LocationPoint).Point;

                    // Create tag
                    var tagMode = TagMode.TM_ADDBY_CATEGORY;
                    var tagOrientation = TagOrientation.Horizontal;

                    var tag = IndependentTag.Create(
                        doc,
                        view.Id,
                        new Reference(door),
                        addLeader,
                        tagMode,
                        tagOrientation,
                        location
                    );

                    trans.Commit();

                    Log.Information($"Tagged door {doorId.Value} in view {viewId.Value}");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = tag.Id.Value,
                        doorId = doorId.Value,
                        viewId = viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error tagging door");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag a single room element
        /// </summary>
        public static string TagRoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var roomId = new ElementId(int.Parse(parameters["roomId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var room = doc.GetElement(roomId) as Room;
                var view = doc.GetElement(viewId) as View;

                if (room == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Room not found"
                    });
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Tag Room"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get room location (center point)
                    var location = (room.Location as LocationPoint).Point;

                    // Create room tag
                    var roomTag = doc.Create.NewRoomTag(
                        new LinkElementId(room.Id),
                        new UV(location.X, location.Y),
                        view.Id
                    );

                    trans.Commit();

                    Log.Information($"Tagged room {roomId.Value} in view {viewId.Value}");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = roomTag.Id.Value,
                        roomId = roomId.Value,
                        viewId = viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error tagging room");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag a single wall element
        /// </summary>
        public static string TagWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                bool addLeader = parameters["addLeader"]?.ToObject<bool>() ?? false;

                var wall = doc.GetElement(wallId) as Wall;
                var view = doc.GetElement(viewId) as View;

                if (wall == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall not found"
                    });
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Tag Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get wall location curve midpoint
                    var locationCurve = wall.Location as LocationCurve;
                    var curve = locationCurve.Curve;
                    var midpoint = curve.Evaluate(0.5, true);

                    // Create tag
                    var tagMode = TagMode.TM_ADDBY_CATEGORY;
                    var tagOrientation = TagOrientation.Horizontal;

                    var tag = IndependentTag.Create(
                        doc,
                        view.Id,
                        new Reference(wall),
                        addLeader,
                        tagMode,
                        tagOrientation,
                        midpoint
                    );

                    trans.Commit();

                    Log.Information($"Tagged wall {wallId.Value} in view {viewId.Value}");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = tag.Id.Value,
                        wallId = wallId.Value,
                        viewId = viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error tagging wall");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag any element by category
        /// </summary>
        public static string TagElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                bool addLeader = parameters["addLeader"]?.ToObject<bool>() ?? false;

                var element = doc.GetElement(elementId);
                var view = doc.GetElement(viewId) as View;

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Tag Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get element location
                    XYZ location = null;
                    if (element.Location is LocationPoint locPoint)
                    {
                        location = locPoint.Point;
                    }
                    else if (element.Location is LocationCurve locCurve)
                    {
                        location = locCurve.Curve.Evaluate(0.5, true);
                    }
                    else
                    {
                        // Use bounding box center as fallback
                        var bbox = element.get_BoundingBox(view);
                        if (bbox != null)
                        {
                            location = (bbox.Min + bbox.Max) / 2;
                        }
                    }

                    if (location == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Could not determine element location"
                        });
                    }

                    // Create tag
                    var tagMode = TagMode.TM_ADDBY_CATEGORY;
                    var tagOrientation = TagOrientation.Horizontal;

                    var tag = IndependentTag.Create(
                        doc,
                        view.Id,
                        new Reference(element),
                        addLeader,
                        tagMode,
                        tagOrientation,
                        location
                    );

                    trans.Commit();

                    Log.Information($"Tagged element {elementId.Value} in view {viewId.Value}");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = tag.Id.Value,
                        elementId = elementId.Value,
                        viewId = viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error tagging element");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag all doors in a view
        /// </summary>
        public static string BatchTagDoors(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                bool addLeader = parameters["addLeader"]?.ToObject<bool>() ?? false;

                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Get all doors in view
                var doors = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var taggedCount = 0;
                var taggedIds = new List<long>();

                using (var trans = new Transaction(doc, "Batch Tag Doors"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var door in doors)
                    {
                        try
                        {
                            var location = (door.Location as LocationPoint).Point;

                            var tag = IndependentTag.Create(
                                doc,
                                view.Id,
                                new Reference(door),
                                addLeader,
                                TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal,
                                location
                            );

                            taggedIds.Add(tag.Id.Value);
                            taggedCount++;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"Failed to tag door {door.Id.Value}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                Log.Information($"Tagged {taggedCount} doors in view {viewId.Value}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalDoors = doors.Count,
                    taggedCount = taggedCount,
                    tagIds = taggedIds,
                    viewId = viewId.Value
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error batch tagging doors");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag all rooms in a view
        /// </summary>
        public static string BatchTagRooms(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Get all rooms in view
                var rooms = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();

                var taggedCount = 0;
                var taggedIds = new List<long>();

                using (var trans = new Transaction(doc, "Batch Tag Rooms"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var room in rooms)
                    {
                        try
                        {
                            var location = (room.Location as LocationPoint).Point;

                            var roomTag = doc.Create.NewRoomTag(
                                new LinkElementId(room.Id),
                                new UV(location.X, location.Y),
                                view.Id
                            );

                            taggedIds.Add(roomTag.Id.Value);
                            taggedCount++;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"Failed to tag room {room.Id.Value}: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                Log.Information($"Tagged {taggedCount} rooms in view {viewId.Value}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalRooms = rooms.Count,
                    taggedCount = taggedCount,
                    tagIds = taggedIds,
                    viewId = viewId.Value
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error batch tagging rooms");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all tags in a view
        /// </summary>
        public static string GetTagsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                // Get all independent tags in view
                var tagsList = new List<object>();
                var tagElements = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .ToList();

                foreach (var tag in tagElements)
                {
                    try
                    {
                        var refs = tag.GetTaggedReferences();
                        var taggedId = refs.Count > 0 ? refs.First().ElementId.Value : -1;

                        tagsList.Add(new
                        {
                            tagId = tag.Id.Value,
                            taggedElementId = taggedId,
                            tagText = tag.TagText,
                            hasLeader = tag.HasLeader,
                            categoryName = tag.Category?.Name
                        });
                    }
                    catch
                    {
                        // Skip tags that can't be processed
                    }
                }

                var tags = tagsList;

                Log.Information($"Found {tags.Count()} tags in view {viewId.Value}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewId.Value,
                    tagCount = tags.Count(),
                    tags = tags
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting tags in view");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a tag by ID
        /// </summary>
        public static string DeleteTag(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var tagId = new ElementId(int.Parse(parameters["tagId"].ToString()));

                var tag = doc.GetElement(tagId);

                if (tag == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Tag not found"
                    });
                }

                using (var trans = new Transaction(doc, "Delete Tag"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(tagId);
                    trans.Commit();
                }

                Log.Information($"Deleted tag {tagId.Value}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    deletedTagId = tagId.Value
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting tag");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets detailed information about a specific tag including position
        /// </summary>
        public static string GetTagInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var tagIdParam = parameters["tagId"];
                if (tagIdParam == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "tagId is required" });
                }

                var tagId = new ElementId(int.Parse(tagIdParam.ToString()));
                var tag = doc.GetElement(tagId) as IndependentTag;

                if (tag == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Tag not found" });
                }

                var position = tag.TagHeadPosition;

                // Get tagged element ID
                var refs = tag.GetTaggedReferences();
                var taggedId = refs.Count > 0 ? refs.First().ElementId.Value : -1;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tag = new
                    {
                        tagId = (int)tag.Id.Value,
                        position = new double[] { position.X, position.Y, position.Z },
                        hasLeader = tag.HasLeader,
                        tagText = tag.TagText,
                        taggedElementId = taggedId
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting tag info");
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
