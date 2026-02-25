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
    /// Level creation, modification, and management methods for MCP Bridge
    /// </summary>
    public static class LevelMethods
    {
        /// <summary>
        /// Create a new level at specified elevation
        /// </summary>
        public static string CreateLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elevation = parameters["elevation"]?.Value<double>() ?? 0;
                var name = parameters["name"]?.ToString();

                using (var trans = new Transaction(doc, "Create Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var level = Level.Create(doc, elevation);

                    if (!string.IsNullOrEmpty(name))
                    {
                        level.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        levelId = level.Id.Value,
                        name = level.Name,
                        elevation = level.Elevation
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all levels in the model
        /// </summary>
        public static string GetLevels(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new
                    {
                        levelId = l.Id.Value,
                        name = l.Name,
                        elevation = l.Elevation
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelCount = levels.Count,
                    levels = levels
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get a specific level by name or ID
        /// </summary>
        public static string GetLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var levelName = parameters["name"]?.ToString();

                Level level = null;

                if (levelId.HasValue)
                {
                    level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                }
                else if (!string.IsNullOrEmpty(levelName))
                {
                    level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name == levelName);
                }

                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelId = level.Id.Value,
                    name = level.Name,
                    elevation = level.Elevation
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rename a level
        /// </summary>
        public static string RenameLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var newName = parameters["newName"]?.ToString();

                if (!levelId.HasValue || string.IsNullOrEmpty(newName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId and newName are required" });
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                using (var trans = new Transaction(doc, "Rename Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    level.Name = newName;
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        levelId = level.Id.Value,
                        name = level.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set level elevation
        /// </summary>
        public static string SetLevelElevation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var elevation = parameters["elevation"]?.Value<double>();

                if (!levelId.HasValue || !elevation.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId and elevation are required" });
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                using (var trans = new Transaction(doc, "Set Level Elevation"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    level.Elevation = elevation.Value;
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        levelId = level.Id.Value,
                        name = level.Name,
                        elevation = level.Elevation
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a level
        /// </summary>
        public static string DeleteLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();

                if (!levelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(levelId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedLevelId = levelId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create multiple levels at regular intervals
        /// </summary>
        public static string CreateLevelArray(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var startElevation = parameters["startElevation"]?.Value<double>() ?? 0;
                var floorHeight = parameters["floorHeight"]?.Value<double>() ?? 10;
                var count = parameters["count"]?.Value<int>() ?? 3;
                var namePrefix = parameters["namePrefix"]?.ToString() ?? "Level ";
                var startNumber = parameters["startNumber"]?.Value<int>() ?? 1;

                var createdLevels = new List<object>();

                using (var trans = new Transaction(doc, "Create Level Array"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    for (int i = 0; i < count; i++)
                    {
                        double elevation = startElevation + (i * floorHeight);
                        var level = Level.Create(doc, elevation);
                        level.Name = namePrefix + (startNumber + i).ToString();

                        createdLevels.Add(new
                        {
                            levelId = level.Id.Value,
                            name = level.Name,
                            elevation = level.Elevation
                        });
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelCount = createdLevels.Count,
                    levels = createdLevels
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get level by elevation (finds closest level)
        /// </summary>
        public static string GetLevelByElevation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var targetElevation = parameters["elevation"]?.Value<double>();
                var tolerance = parameters["tolerance"]?.Value<double>() ?? 0.1;

                if (!targetElevation.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elevation is required" });
                }

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToList();

                var closest = levels
                    .OrderBy(l => Math.Abs(l.Elevation - targetElevation.Value))
                    .FirstOrDefault();

                if (closest == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No levels found" });
                }

                var distance = Math.Abs(closest.Elevation - targetElevation.Value);
                var withinTolerance = distance <= tolerance;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelId = closest.Id.Value,
                    name = closest.Name,
                    elevation = closest.Elevation,
                    distanceFromTarget = distance,
                    withinTolerance = withinTolerance
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get elements on a specific level
        /// </summary>
        public static string GetElementsOnLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var categoryName = parameters["category"]?.ToString();

                if (!levelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                var filter = new ElementLevelFilter(new ElementId(levelId.Value));
                var collector = new FilteredElementCollector(doc).WherePasses(filter);

                // Optional category filter
                if (!string.IsNullOrEmpty(categoryName))
                {
                    var categories = doc.Settings.Categories;
                    Category cat = null;
                    foreach (Category c in categories)
                    {
                        if (c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                        {
                            cat = c;
                            break;
                        }
                    }
                    if (cat != null)
                    {
                        collector = collector.OfCategoryId(cat.Id);
                    }
                }

                var elements = collector
                    .Select(e => new
                    {
                        elementId = e.Id.Value,
                        name = e.Name,
                        category = e.Category?.Name ?? "Unknown"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelId = levelId.Value,
                    levelName = level.Name,
                    elementCount = elements.Count,
                    elements = elements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy elements from one level to another
        /// </summary>
        public static string CopyElementsToLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sourceLevelId = parameters["sourceLevelId"]?.Value<int>();
                var targetLevelId = parameters["targetLevelId"]?.Value<int>();
                var elementIds = parameters["elementIds"]?.ToObject<int[]>();

                if (!sourceLevelId.HasValue || !targetLevelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sourceLevelId and targetLevelId are required" });
                }

                var sourceLevel = doc.GetElement(new ElementId(sourceLevelId.Value)) as Level;
                var targetLevel = doc.GetElement(new ElementId(targetLevelId.Value)) as Level;

                if (sourceLevel == null || targetLevel == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Source or target level not found" });
                }

                var elevationDiff = targetLevel.Elevation - sourceLevel.Elevation;
                var translation = new XYZ(0, 0, elevationDiff);

                ICollection<ElementId> elementsToMove;
                if (elementIds != null && elementIds.Length > 0)
                {
                    elementsToMove = elementIds.Select(id => new ElementId(id)).ToList();
                }
                else
                {
                    // Get all elements on source level
                    var filter = new ElementLevelFilter(new ElementId(sourceLevelId.Value));
                    elementsToMove = new FilteredElementCollector(doc)
                        .WherePasses(filter)
                        .ToElementIds();
                }

                using (var trans = new Transaction(doc, "Copy Elements to Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var copiedIds = ElementTransformUtils.CopyElements(
                        doc,
                        elementsToMove,
                        translation);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        copiedCount = copiedIds.Count,
                        copiedElementIds = copiedIds.Select(id => (int)id.Value).ToList(),
                        targetLevelName = targetLevel.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
