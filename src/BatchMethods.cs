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
    /// Batch operations for efficient multi-element creation with single transaction.
    /// Implements the batch operation patterns from knowledge/batch-operations.md
    /// </summary>
    public static class BatchMethods
    {
        /// <summary>
        /// Execute multiple MCP operations in a single transaction with optional rollback on error.
        /// This is the most powerful batch method - it can execute any combination of operations.
        /// </summary>
        /// <param name="uiApp">Revit UI Application</param>
        /// <param name="parameters">
        /// {
        ///   "operations": [
        ///     { "method": "createWall", "params": { ... } },
        ///     { "method": "placeFamilyInstance", "params": { ... } }
        ///   ],
        ///   "transactionName": "Batch Operation" (optional),
        ///   "rollbackOnError": true (optional, default true),
        ///   "continueOnWarning": true (optional, default true)
        /// }
        /// </param>
        public static string ExecuteBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please open a Revit project first."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var operations = parameters["operations"] as JArray;
                if (operations == null || operations.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No operations provided. 'operations' array is required."
                    });
                }

                var transactionName = parameters["transactionName"]?.ToString() ?? "Batch Operation";
                var rollbackOnError = parameters["rollbackOnError"]?.ToObject<bool>() ?? true;
                var continueOnWarning = parameters["continueOnWarning"]?.ToObject<bool>() ?? true;

                var results = new List<object>();
                var succeeded = 0;
                var failed = 0;
                var createdElementIds = new List<int>();

                using (var trans = new Transaction(doc, transactionName))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    for (int i = 0; i < operations.Count; i++)
                    {
                        var op = operations[i] as JObject;
                        var method = op?["method"]?.ToString();
                        var opParams = op?["params"] as JObject ?? new JObject();

                        try
                        {
                            var result = ExecuteSingleOperation(uiApp, doc, method, opParams);
                            var resultObj = JObject.Parse(result);

                            if (resultObj["success"]?.ToObject<bool>() == true)
                            {
                                succeeded++;

                                // Track created element IDs for potential rollback info
                                if (resultObj["wallId"] != null)
                                    createdElementIds.Add(resultObj["wallId"].ToObject<int>());
                                else if (resultObj["elementId"] != null)
                                    createdElementIds.Add(resultObj["elementId"].ToObject<int>());
                                else if (resultObj["instanceId"] != null)
                                    createdElementIds.Add(resultObj["instanceId"].ToObject<int>());

                                results.Add(new
                                {
                                    index = i,
                                    method = method,
                                    success = true,
                                    result = resultObj
                                });
                            }
                            else
                            {
                                failed++;
                                results.Add(new
                                {
                                    index = i,
                                    method = method,
                                    success = false,
                                    error = resultObj["error"]?.ToString()
                                });

                                if (rollbackOnError)
                                {
                                    trans.RollBack();
                                    return JsonConvert.SerializeObject(new
                                    {
                                        success = false,
                                        error = $"Operation {i} ({method}) failed: {resultObj["error"]}. Transaction rolled back.",
                                        results = results,
                                        summary = new { total = operations.Count, succeeded, failed, rolledBack = true }
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            results.Add(new
                            {
                                index = i,
                                method = method,
                                success = false,
                                error = ex.Message
                            });

                            if (rollbackOnError)
                            {
                                trans.RollBack();
                                return JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = $"Operation {i} ({method}) threw exception: {ex.Message}. Transaction rolled back.",
                                    results = results,
                                    summary = new { total = operations.Count, succeeded, failed, rolledBack = true }
                                });
                            }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failed == 0 || !rollbackOnError,
                    results = results,
                    createdElementIds = createdElementIds,
                    summary = new
                    {
                        total = operations.Count,
                        succeeded,
                        failed,
                        rolledBack = false
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ExecuteBatch failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Execute a single operation within the batch transaction
        /// </summary>
        private static string ExecuteSingleOperation(UIApplication uiApp, Document doc, string method, JObject parameters)
        {
            switch (method)
            {
                case "createWall":
                    return CreateWallInternal(doc, parameters);
                case "placeFamilyInstance":
                    return PlaceFamilyInstanceInternal(doc, parameters);
                case "deleteElements":
                    return DeleteElementsInternal(doc, parameters);
                case "setParameter":
                    return SetParameterInternal(doc, parameters);
                default:
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Unknown batch method: {method}. Supported: createWall, placeFamilyInstance, deleteElements, setParameter"
                    });
            }
        }

        /// <summary>
        /// Create a wall - internal version that doesn't create its own transaction
        /// </summary>
        private static string CreateWallInternal(Document doc, JObject parameters)
        {
            var startPoint = parameters["startPoint"]?.ToObject<double[]>();
            var endPoint = parameters["endPoint"]?.ToObject<double[]>();

            if (startPoint == null || endPoint == null || startPoint.Length < 3 || endPoint.Length < 3)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "startPoint and endPoint are required as [x, y, z] arrays"
                });
            }

            var levelIdInt = parameters["levelId"]?.ToObject<int>() ?? 0;
            var levelId = new ElementId(levelIdInt);
            var height = parameters["height"]?.ToObject<double>() ?? 10.0;

            // Get wall type
            WallType wallType = null;
            if (parameters["wallTypeId"] != null)
            {
                var wallTypeId = new ElementId(parameters["wallTypeId"].ToObject<int>());
                wallType = doc.GetElement(wallTypeId) as WallType;
            }
            else if (parameters["wallTypeName"] != null)
            {
                var typeName = parameters["wallTypeName"].ToString();
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name == typeName);
            }

            if (wallType == null)
            {
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic) ??
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault();
            }

            if (wallType == null)
            {
                return JsonConvert.SerializeObject(new { success = false, error = "No wall type found" });
            }

            var level = doc.GetElement(levelId) as Level;
            if (level == null)
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"Invalid level ID: {levelIdInt}" });
            }

            var start = new XYZ(startPoint[0], startPoint[1], startPoint[2]);
            var end = new XYZ(endPoint[0], endPoint[1], endPoint[2]);
            var line = Line.CreateBound(start, end);

            var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                wallId = (int)wall.Id.Value,
                wallType = wallType.Name,
                level = level.Name
            });
        }

        /// <summary>
        /// Place a family instance - internal version
        /// </summary>
        private static string PlaceFamilyInstanceInternal(Document doc, JObject parameters)
        {
            var location = parameters["location"]?.ToObject<double[]>();
            if (location == null || location.Length < 3)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "location is required as [x, y, z] array"
                });
            }

            var point = new XYZ(location[0], location[1], location[2]);

            // Get family symbol
            FamilySymbol symbol = null;
            if (parameters["typeId"] != null)
            {
                var typeId = new ElementId(parameters["typeId"].ToObject<int>());
                symbol = doc.GetElement(typeId) as FamilySymbol;
            }
            else if (parameters["familyName"] != null && parameters["typeName"] != null)
            {
                var familyName = parameters["familyName"].ToString();
                var typeName = parameters["typeName"].ToString();
                symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family.Name == familyName && fs.Name == typeName);
            }

            if (symbol == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Family symbol not found. Provide typeId or familyName+typeName"
                });
            }

            if (!symbol.IsActive)
                symbol.Activate();

            // Get level
            Level level = null;
            if (parameters["levelId"] != null)
            {
                level = doc.GetElement(new ElementId(parameters["levelId"].ToObject<int>())) as Level;
            }
            else
            {
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault();
            }

            // Check if this needs a host (doors/windows)
            Element host = null;
            if (parameters["hostId"] != null)
            {
                host = doc.GetElement(new ElementId(parameters["hostId"].ToObject<int>()));
            }

            FamilyInstance instance;
            if (host != null)
            {
                // Place hosted element (door/window in wall)
                instance = doc.Create.NewFamilyInstance(point, symbol, host, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            else if (level != null)
            {
                // Place on level
                instance = doc.Create.NewFamilyInstance(point, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            else
            {
                return JsonConvert.SerializeObject(new { success = false, error = "No level found for placement" });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                instanceId = (int)instance.Id.Value,
                familyName = symbol.Family.Name,
                typeName = symbol.Name
            });
        }

        /// <summary>
        /// Delete elements - internal version
        /// </summary>
        private static string DeleteElementsInternal(Document doc, JObject parameters)
        {
            var elementIds = parameters["elementIds"]?.ToObject<int[]>();
            if (elementIds == null || elementIds.Length == 0)
            {
                return JsonConvert.SerializeObject(new { success = false, error = "elementIds array is required" });
            }

            var ids = elementIds.Select(id => new ElementId(id)).ToList();
            var deletedIds = doc.Delete(ids);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                deletedCount = deletedIds.Count,
                deletedIds = deletedIds.Select(id => (int)id.Value).ToList()
            });
        }

        /// <summary>
        /// Set element parameter - internal version
        /// </summary>
        private static string SetParameterInternal(Document doc, JObject parameters)
        {
            var elementId = parameters["elementId"]?.ToObject<int>() ?? 0;
            var paramName = parameters["parameterName"]?.ToString();
            var value = parameters["value"];

            if (elementId == 0 || string.IsNullOrEmpty(paramName) || value == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "elementId, parameterName, and value are required"
                });
            }

            var element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"Element {elementId} not found" });
            }

            var param = element.LookupParameter(paramName);
            if (param == null)
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"Parameter '{paramName}' not found" });
            }

            switch (param.StorageType)
            {
                case StorageType.String:
                    param.Set(value.ToString());
                    break;
                case StorageType.Double:
                    param.Set(value.ToObject<double>());
                    break;
                case StorageType.Integer:
                    param.Set(value.ToObject<int>());
                    break;
                case StorageType.ElementId:
                    param.Set(new ElementId(value.ToObject<int>()));
                    break;
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                elementId,
                parameterName = paramName,
                newValue = value.ToString()
            });
        }

        /// <summary>
        /// Create multiple walls efficiently in a single transaction.
        /// Optimized for batch wall creation with consistent parameters.
        /// </summary>
        public static string CreateWallBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please open a Revit project first."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var walls = parameters["walls"] as JArray;
                if (walls == null || walls.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No walls provided. 'walls' array is required."
                    });
                }

                // Common parameters for all walls in batch
                var defaultLevelId = parameters["levelId"]?.ToObject<int>() ?? 0;
                var defaultHeight = parameters["height"]?.ToObject<double>() ?? 10.0;
                var defaultWallTypeId = parameters["wallTypeId"]?.ToObject<int?>();
                var transactionName = parameters["transactionName"]?.ToString() ?? "Create Wall Batch";

                // Get default wall type
                WallType defaultWallType = null;
                if (defaultWallTypeId.HasValue)
                {
                    defaultWallType = doc.GetElement(new ElementId(defaultWallTypeId.Value)) as WallType;
                }
                if (defaultWallType == null)
                {
                    defaultWallType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                }

                var results = new List<object>();
                var createdIds = new List<int>();
                var succeeded = 0;
                var failed = 0;

                using (var trans = new Transaction(doc, transactionName))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (JObject wallSpec in walls)
                    {
                        try
                        {
                            var startPoint = wallSpec["startPoint"]?.ToObject<double[]>();
                            var endPoint = wallSpec["endPoint"]?.ToObject<double[]>();

                            if (startPoint == null || endPoint == null)
                            {
                                failed++;
                                results.Add(new { success = false, error = "Missing startPoint or endPoint" });
                                continue;
                            }

                            // Use wall-specific or batch default values
                            var levelId = new ElementId(wallSpec["levelId"]?.ToObject<int>() ?? defaultLevelId);
                            var height = wallSpec["height"]?.ToObject<double>() ?? defaultHeight;

                            // Wall type: wall-specific ID > wall-specific name > batch default
                            WallType wallType = defaultWallType;
                            if (wallSpec["wallTypeId"] != null)
                            {
                                wallType = doc.GetElement(new ElementId(wallSpec["wallTypeId"].ToObject<int>())) as WallType ?? defaultWallType;
                            }
                            else if (wallSpec["wallTypeName"] != null)
                            {
                                var typeName = wallSpec["wallTypeName"].ToString();
                                wallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(wt => wt.Name == typeName) ?? defaultWallType;
                            }

                            var level = doc.GetElement(levelId) as Level;
                            if (level == null)
                            {
                                failed++;
                                results.Add(new { success = false, error = $"Level {levelId.Value} not found" });
                                continue;
                            }

                            var start = new XYZ(startPoint[0], startPoint[1], startPoint[2]);
                            var end = new XYZ(endPoint[0], endPoint[1], endPoint[2]);
                            var line = Line.CreateBound(start, end);

                            var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);

                            succeeded++;
                            createdIds.Add((int)wall.Id.Value);
                            results.Add(new
                            {
                                success = true,
                                wallId = (int)wall.Id.Value,
                                wallType = wallType.Name
                            });
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            results.Add(new { success = false, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failed == 0,
                    createdWallIds = createdIds,
                    results = results,
                    summary = new
                    {
                        total = walls.Count,
                        succeeded,
                        failed
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CreateWallBatch failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place multiple family instances efficiently in a single transaction.
        /// Optimized for batch placement of doors, windows, fixtures, furniture.
        /// </summary>
        public static string PlaceElementsBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please open a Revit project first."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var elements = parameters["elements"] as JArray;
                if (elements == null || elements.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No elements provided. 'elements' array is required."
                    });
                }

                var transactionName = parameters["transactionName"]?.ToString() ?? "Place Elements Batch";

                var results = new List<object>();
                var createdIds = new List<int>();
                var succeeded = 0;
                var failed = 0;

                // Pre-load commonly used symbols for efficiency
                var symbolCache = new Dictionary<string, FamilySymbol>();

                using (var trans = new Transaction(doc, transactionName))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (JObject elemSpec in elements)
                    {
                        try
                        {
                            var location = elemSpec["location"]?.ToObject<double[]>();
                            if (location == null || location.Length < 3)
                            {
                                failed++;
                                results.Add(new { success = false, error = "Missing or invalid location" });
                                continue;
                            }

                            var point = new XYZ(location[0], location[1], location[2]);

                            // Get family symbol (with caching)
                            FamilySymbol symbol = null;
                            string cacheKey = null;

                            if (elemSpec["typeId"] != null)
                            {
                                var typeId = elemSpec["typeId"].ToObject<int>();
                                cacheKey = $"id:{typeId}";
                                if (!symbolCache.TryGetValue(cacheKey, out symbol))
                                {
                                    symbol = doc.GetElement(new ElementId(typeId)) as FamilySymbol;
                                    if (symbol != null) symbolCache[cacheKey] = symbol;
                                }
                            }
                            else if (elemSpec["familyName"] != null && elemSpec["typeName"] != null)
                            {
                                var familyName = elemSpec["familyName"].ToString();
                                var typeName = elemSpec["typeName"].ToString();
                                cacheKey = $"{familyName}:{typeName}";

                                if (!symbolCache.TryGetValue(cacheKey, out symbol))
                                {
                                    symbol = new FilteredElementCollector(doc)
                                        .OfClass(typeof(FamilySymbol))
                                        .Cast<FamilySymbol>()
                                        .FirstOrDefault(fs => fs.Family.Name == familyName && fs.Name == typeName);
                                    if (symbol != null) symbolCache[cacheKey] = symbol;
                                }
                            }

                            if (symbol == null)
                            {
                                failed++;
                                results.Add(new { success = false, error = "Family symbol not found" });
                                continue;
                            }

                            if (!symbol.IsActive)
                                symbol.Activate();

                            // Get level
                            Level level = null;
                            if (elemSpec["levelId"] != null)
                            {
                                level = doc.GetElement(new ElementId(elemSpec["levelId"].ToObject<int>())) as Level;
                            }
                            if (level == null)
                            {
                                level = new FilteredElementCollector(doc)
                                    .OfClass(typeof(Level))
                                    .Cast<Level>()
                                    .OrderBy(l => Math.Abs(l.Elevation - point.Z))
                                    .FirstOrDefault();
                            }

                            // Get host element if specified
                            Element host = null;
                            if (elemSpec["hostId"] != null)
                            {
                                host = doc.GetElement(new ElementId(elemSpec["hostId"].ToObject<int>()));
                            }

                            FamilyInstance instance;
                            if (host != null)
                            {
                                instance = doc.Create.NewFamilyInstance(point, symbol, host, level,
                                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                            }
                            else if (level != null)
                            {
                                instance = doc.Create.NewFamilyInstance(point, symbol, level,
                                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                            }
                            else
                            {
                                failed++;
                                results.Add(new { success = false, error = "No level found" });
                                continue;
                            }

                            // Set rotation if specified
                            if (elemSpec["rotation"] != null)
                            {
                                var rotation = elemSpec["rotation"].ToObject<double>();
                                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * Math.PI / 180.0);
                            }

                            succeeded++;
                            createdIds.Add((int)instance.Id.Value);
                            results.Add(new
                            {
                                success = true,
                                instanceId = (int)instance.Id.Value,
                                familyName = symbol.Family.Name,
                                typeName = symbol.Name
                            });
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            results.Add(new { success = false, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failed == 0,
                    createdElementIds = createdIds,
                    results = results,
                    summary = new
                    {
                        total = elements.Count,
                        succeeded,
                        failed
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PlaceElementsBatch failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete multiple elements efficiently in a single transaction.
        /// </summary>
        public static string DeleteElementsBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                if (elementIds == null || elementIds.Length == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var transactionName = parameters["transactionName"]?.ToString() ?? "Delete Elements Batch";

                using (var trans = new Transaction(doc, transactionName))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var ids = elementIds.Select(id => new ElementId(id)).ToList();
                    var deletedIds = doc.Delete(ids);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        requestedCount = elementIds.Length,
                        deletedCount = deletedIds.Count,
                        deletedIds = deletedIds.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DeleteElementsBatch failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set parameters on multiple elements efficiently.
        /// </summary>
        public static string SetParametersBatch(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var updates = parameters["updates"] as JArray;
                if (updates == null || updates.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "'updates' array is required"
                    });
                }

                var transactionName = parameters["transactionName"]?.ToString() ?? "Set Parameters Batch";
                var results = new List<object>();
                var succeeded = 0;
                var failed = 0;

                using (var trans = new Transaction(doc, transactionName))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (JObject update in updates)
                    {
                        try
                        {
                            var result = SetParameterInternal(doc, update);
                            var resultObj = JObject.Parse(result);

                            if (resultObj["success"]?.ToObject<bool>() == true)
                            {
                                succeeded++;
                                results.Add(resultObj);
                            }
                            else
                            {
                                failed++;
                                results.Add(resultObj);
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            results.Add(new { success = false, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = failed == 0,
                    results = results,
                    summary = new { total = updates.Count, succeeded, failed }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SetParametersBatch failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
