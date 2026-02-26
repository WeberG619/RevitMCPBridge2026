using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Group and assembly methods for MCP Bridge
    /// </summary>
    public static class GroupMethods
    {
        /// <summary>
        /// Get all groups in the model
        /// </summary>
        [MCPMethod("getGroups", Category = "Group", Description = "Get all groups in the model")]
        public static string GetGroups(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groups = new FilteredElementCollector(doc)
                    .OfClass(typeof(Group))
                    .Cast<Group>()
                    .Select(g => new
                    {
                        groupId = g.Id.Value,
                        name = g.Name,
                        groupTypeId = g.GroupType?.Id.Value ?? -1,
                        groupTypeName = g.GroupType?.Name ?? "Unknown",
                        memberCount = g.GetMemberIds().Count
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupCount = groups.Count,
                    groups = groups
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all group types in the model
        /// </summary>
        [MCPMethod("getGroupTypes", Category = "Group", Description = "Get all group types in the model")]
        public static string GetGroupTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(GroupType))
                    .Cast<GroupType>()
                    .Select(gt => new
                    {
                        groupTypeId = gt.Id.Value,
                        name = gt.Name,
                        category = gt.Category?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupTypeCount = groupTypes.Count,
                    groupTypes = groupTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get group members
        /// </summary>
        [MCPMethod("getGroupMembers", Category = "Group", Description = "Get group members")]
        public static string GetGroupMembers(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupId = parameters["groupId"]?.Value<int>();

                if (!groupId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "groupId is required" });
                }

                var group = doc.GetElement(new ElementId(groupId.Value)) as Group;
                if (group == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Group not found" });
                }

                var members = group.GetMemberIds().Select(id =>
                {
                    var elem = doc.GetElement(id);
                    return new
                    {
                        elementId = (int)id.Value,
                        name = elem?.Name ?? "Unknown",
                        category = elem?.Category?.Name ?? "Unknown"
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupId = groupId.Value,
                    groupName = group.Name,
                    memberCount = members.Count,
                    members = members
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a group from selected elements
        /// </summary>
        [MCPMethod("createGroup", Category = "Group", Description = "Create a group from selected elements")]
        public static string CreateGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                var groupName = parameters["groupName"]?.ToString();

                if (elementIds == null || elementIds.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementIds are required" });
                }

                using (var trans = new Transaction(doc, "Create Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var ids = elementIds.Select(id => new ElementId(id)).ToList();
                    var group = doc.Create.NewGroup(ids);

                    if (!string.IsNullOrEmpty(groupName))
                    {
                        group.GroupType.Name = groupName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = group.Id.Value,
                        groupTypeId = group.GroupType.Id.Value,
                        groupTypeName = group.GroupType.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place an instance of a group type
        /// </summary>
        [MCPMethod("placeGroup", Category = "Group", Description = "Place an instance of a group type")]
        public static string PlaceGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypeId = parameters["groupTypeId"]?.Value<int>();
                var x = parameters["x"]?.Value<double>();
                var y = parameters["y"]?.Value<double>();
                var z = parameters["z"]?.Value<double>() ?? 0;

                if (!groupTypeId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "groupTypeId is required" });
                }

                if (!x.HasValue || !y.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "x and y are required" });
                }

                var groupType = doc.GetElement(new ElementId(groupTypeId.Value)) as GroupType;
                if (groupType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Group type not found" });
                }

                using (var trans = new Transaction(doc, "Place Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var location = new XYZ(x.Value, y.Value, z);
                    var group = doc.Create.PlaceGroup(location, groupType);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = group.Id.Value,
                        location = new { x = x.Value, y = y.Value, z = z }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Ungroup a group
        /// </summary>
        [MCPMethod("ungroupGroup", Category = "Group", Description = "Ungroup a group")]
        public static string UngroupGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupId = parameters["groupId"]?.Value<int>();

                if (!groupId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "groupId is required" });
                }

                var group = doc.GetElement(new ElementId(groupId.Value)) as Group;
                if (group == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Group not found" });
                }

                using (var trans = new Transaction(doc, "Ungroup"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var memberIds = group.UngroupMembers();

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ungroupedElementCount = memberIds.Count,
                        ungroupedElementIds = memberIds.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a group type
        /// </summary>
        [MCPMethod("deleteGroupType", Category = "Group", Description = "Delete a group type")]
        public static string DeleteGroupType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypeId = parameters["groupTypeId"]?.Value<int>();

                if (!groupTypeId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "groupTypeId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Group Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(groupTypeId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedGroupTypeId = groupTypeId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rename a group type
        /// </summary>
        [MCPMethod("renameGroupType", Category = "Group", Description = "Rename a group type")]
        public static string RenameGroupType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypeId = parameters["groupTypeId"]?.Value<int>();
                var newName = parameters["newName"]?.ToString();

                if (!groupTypeId.HasValue || string.IsNullOrEmpty(newName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "groupTypeId and newName are required" });
                }

                var groupType = doc.GetElement(new ElementId(groupTypeId.Value)) as GroupType;
                if (groupType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Group type not found" });
                }

                using (var trans = new Transaction(doc, "Rename Group Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    groupType.Name = newName;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupTypeId = groupTypeId.Value,
                        newName = newName
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a group type
        /// </summary>
        [MCPMethod("duplicateGroupType", Category = "Group", Description = "Duplicate a group type")]
        public static string DuplicateGroupType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypeId = parameters["groupTypeId"]?.Value<int>();
                var newName = parameters["newName"]?.ToString();

                if (!groupTypeId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "groupTypeId is required" });
                }

                var groupType = doc.GetElement(new ElementId(groupTypeId.Value)) as GroupType;
                if (groupType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Group type not found" });
                }

                using (var trans = new Transaction(doc, "Duplicate Group Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var duplicatedType = groupType.Duplicate(newName ?? (groupType.Name + " Copy"));

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalGroupTypeId = groupTypeId.Value,
                        newGroupTypeId = duplicatedType.Id.Value,
                        newName = duplicatedType.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all model groups (excludes detail groups)
        /// </summary>
        [MCPMethod("getModelGroups", Category = "Group", Description = "Get all model groups (excludes detail groups)")]
        public static string GetModelGroups(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groups = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                    .WhereElementIsNotElementType()
                    .Cast<Group>()
                    .Select(g => new
                    {
                        groupId = g.Id.Value,
                        name = g.Name,
                        groupTypeId = g.GroupType?.Id.Value ?? -1,
                        groupTypeName = g.GroupType?.Name ?? "Unknown",
                        memberCount = g.GetMemberIds().Count
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    modelGroupCount = groups.Count,
                    modelGroups = groups
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all detail groups
        /// </summary>
        [MCPMethod("getDetailGroups", Category = "Group", Description = "Get all detail groups")]
        public static string GetDetailGroups(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groups = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                    .WhereElementIsNotElementType()
                    .Cast<Group>()
                    .Select(g => new
                    {
                        groupId = g.Id.Value,
                        name = g.Name,
                        groupTypeId = g.GroupType?.Id.Value ?? -1,
                        groupTypeName = g.GroupType?.Name ?? "Unknown",
                        memberCount = g.GetMemberIds().Count,
                        ownerViewId = (int?)g.OwnerViewId?.Value ?? -1
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    detailGroupCount = groups.Count,
                    detailGroups = groups
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a group from elements and replicate in a pattern (grid, linear, radial).
        /// </summary>
        [MCPMethod("createGroupAndReplicatePattern", Category = "Group", Description = "Create a group from elements and replicate in a pattern")]
        public static string CreateGroupAndReplicatePattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementIds = parameters?["elementIds"]?.ToObject<List<int>>();
                var groupName = parameters?["groupName"]?.ToString() ?? $"Group_{DateTime.Now:yyyyMMdd_HHmmss}";
                var patternType = parameters?["patternType"]?.ToString() ?? "grid"; // grid, linear, radial
                var countX = parameters?["countX"]?.ToObject<int>() ?? 2;
                var countY = parameters?["countY"]?.ToObject<int>() ?? 2;
                var spacingX = parameters?["spacingX"]?.ToObject<double>() ?? 10.0; // feet
                var spacingY = parameters?["spacingY"]?.ToObject<double>() ?? 10.0; // feet
                var radialCount = parameters?["radialCount"]?.ToObject<int>() ?? 6;
                var radius = parameters?["radius"]?.ToObject<double>() ?? 10.0; // feet

                if (elementIds == null || elementIds.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds are required"
                    });
                }

                var ids = elementIds.Select(id => new ElementId(id)).ToList();
                var idCollection = new List<ElementId>(ids);

                Group createdGroup = null;
                var placedInstances = new List<object>();

                using (var trans = new Transaction(doc, "Create Group and Replicate"))
                {
                    trans.Start();

                    // Create the group
                    createdGroup = doc.Create.NewGroup(idCollection);
                    if (!string.IsNullOrEmpty(groupName))
                    {
                        createdGroup.GroupType.Name = groupName;
                    }

                    // Get group center point
                    var bb = createdGroup.get_BoundingBox(null);
                    var origin = bb != null ? (bb.Min + bb.Max) / 2 : XYZ.Zero;
                    var baseZ = origin.Z;

                    placedInstances.Add(new
                    {
                        index = 0,
                        groupId = createdGroup.Id.Value,
                        position = new { x = Math.Round(origin.X, 2), y = Math.Round(origin.Y, 2) },
                        isOriginal = true
                    });

                    // Create pattern instances
                    if (patternType.ToLower() == "grid")
                    {
                        for (int i = 0; i < countX; i++)
                        {
                            for (int j = 0; j < countY; j++)
                            {
                                if (i == 0 && j == 0) continue; // Skip original position

                                var offset = new XYZ(i * spacingX, j * spacingY, 0);
                                var newPos = origin + offset;

                                var instance = doc.Create.PlaceGroup(newPos, createdGroup.GroupType);
                                placedInstances.Add(new
                                {
                                    index = placedInstances.Count,
                                    groupId = instance.Id.Value,
                                    position = new { x = Math.Round(newPos.X, 2), y = Math.Round(newPos.Y, 2) },
                                    isOriginal = false
                                });
                            }
                        }
                    }
                    else if (patternType.ToLower() == "linear")
                    {
                        for (int i = 1; i < countX; i++)
                        {
                            var offset = new XYZ(i * spacingX, 0, 0);
                            var newPos = origin + offset;

                            var instance = doc.Create.PlaceGroup(newPos, createdGroup.GroupType);
                            placedInstances.Add(new
                            {
                                index = i,
                                groupId = instance.Id.Value,
                                position = new { x = Math.Round(newPos.X, 2), y = Math.Round(newPos.Y, 2) },
                                isOriginal = false
                            });
                        }
                    }
                    else if (patternType.ToLower() == "radial")
                    {
                        double angleStep = 2 * Math.PI / radialCount;
                        for (int i = 0; i < radialCount; i++)
                        {
                            double angle = i * angleStep;
                            var offset = new XYZ(radius * Math.Cos(angle), radius * Math.Sin(angle), 0);
                            var newPos = origin + offset;

                            var instance = doc.Create.PlaceGroup(newPos, createdGroup.GroupType);
                            placedInstances.Add(new
                            {
                                index = i,
                                groupId = instance.Id.Value,
                                position = new { x = Math.Round(newPos.X, 2), y = Math.Round(newPos.Y, 2) },
                                angleDegrees = Math.Round(angle * 180 / Math.PI, 1),
                                isOriginal = false
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupTypeId = createdGroup.GroupType.Id.Value,
                    groupTypeName = createdGroup.GroupType.Name,
                    patternType = patternType,
                    totalInstances = placedInstances.Count,
                    memberCount = createdGroup.GetMemberIds().Count,
                    instances = placedInstances
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
