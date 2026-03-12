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
    /// Extended ceiling query and manipulation methods for MCP Bridge.
    /// Does NOT duplicate methods in FloorCeilingRoofMethods.cs.
    /// </summary>
    public static class CeilingMethods
    {
        #region Query Methods

        /// <summary>
        /// Get all ceilings in the document.
        /// Returns ceilingId, typeName, area, levelName, height offset.
        /// </summary>
        [MCPMethod("getCeilings", Category = "Ceiling", Description = "Get all ceilings in the document with type, area, level, and height")]
        public static string GetCeilings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ceilings = new FilteredElementCollector(doc)
                    .OfClass(typeof(Ceiling))
                    .Cast<Ceiling>()
                    .Select(c =>
                    {
                        var level = doc.GetElement(c.LevelId) as Level;

                        var areaParam = c.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        var heightParam = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        double height = heightParam != null ? heightParam.AsDouble() : 0;

                        return new
                        {
                            ceilingId = (int)c.Id.Value,
                            typeName = c.Name,
                            area = area,
                            levelName = level?.Name ?? "Unknown",
                            levelId = level != null ? (int)level.Id.Value : -1,
                            heightOffset = height
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingCount = ceilings.Count,
                    ceilings = ceilings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detailed info for one ceiling.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// </summary>
        [MCPMethod("getCeilingInfo", Category = "Ceiling", Description = "Get detailed information for a specific ceiling including type, height, area, boundary, material, and grid info")]
        public static string GetCeilingInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                var level = doc.GetElement(ceiling.LevelId) as Level;
                var ceilingType = doc.GetElement(ceiling.GetTypeId()) as CeilingType;

                // Area
                var areaParam = ceiling.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double area = areaParam != null ? areaParam.AsDouble() : 0;

                // Height offset
                var heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                double heightOffset = heightParam != null ? heightParam.AsDouble() : 0;

                // Thickness from type
                double thickness = 0;
                if (ceilingType != null)
                {
                    var cs = ceilingType.GetCompoundStructure();
                    if (cs != null)
                        thickness = cs.GetWidth();
                }

                // Material from first layer
                string materialName = "None";
                int materialId = -1;
                if (ceilingType != null)
                {
                    var cs = ceilingType.GetCompoundStructure();
                    if (cs != null)
                    {
                        var layers = cs.GetLayers();
                        if (layers.Count > 0)
                        {
                            var mat = doc.GetElement(layers[0].MaterialId) as Material;
                            if (mat != null)
                            {
                                materialName = mat.Name;
                                materialId = (int)mat.Id.Value;
                            }
                        }
                    }
                }

                // Boundary points
                var boundaryPoints = new List<object>();
                var sketch = doc.GetElement(ceiling.SketchId) as Sketch;
                if (sketch != null)
                {
                    foreach (CurveArray curveArray in sketch.Profile)
                    {
                        foreach (Curve curve in curveArray)
                        {
                            var start = curve.GetEndPoint(0);
                            boundaryPoints.Add(new { x = start.X, y = start.Y, z = start.Z });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingId = (int)ceiling.Id.Value,
                    typeName = ceilingType?.Name ?? "Unknown",
                    familyName = ceilingType?.FamilyName ?? "Unknown",
                    area = area,
                    heightOffset = heightOffset,
                    thickness = thickness,
                    material = materialName,
                    materialId = materialId,
                    levelName = level?.Name ?? "Unknown",
                    levelId = level != null ? (int)level.Id.Value : -1,
                    boundaryPoints = boundaryPoints
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all ceilings on a specific level.
        /// Parameters:
        /// - levelId: ID of the level
        /// </summary>
        [MCPMethod("getCeilingsOnLevel", Category = "Ceiling", Description = "Get all ceilings on a specific level")]
        public static string GetCeilingsOnLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["levelId"] == null)
                    return ResponseBuilder.Error("levelId is required", "MISSING_PARAM").Build();

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                    return ResponseBuilder.Error("Level not found", "NOT_FOUND").Build();

                var ceilings = new FilteredElementCollector(doc)
                    .OfClass(typeof(Ceiling))
                    .Cast<Ceiling>()
                    .Where(c => c.LevelId == levelId)
                    .Select(c =>
                    {
                        var areaParam = c.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        var heightParam = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        double height = heightParam != null ? heightParam.AsDouble() : 0;

                        return new
                        {
                            ceilingId = (int)c.Id.Value,
                            typeName = c.Name,
                            area = area,
                            heightOffset = height
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelName = level.Name,
                    ceilingCount = ceilings.Count,
                    ceilings = ceilings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ceilings associated with a room using bounding box intersection.
        /// Parameters:
        /// - roomId: ID of the room
        /// </summary>
        [MCPMethod("getCeilingByRoom", Category = "Ceiling", Description = "Get ceilings associated with a room via bounding box intersection")]
        public static string GetCeilingByRoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roomId"] == null)
                    return ResponseBuilder.Error("roomId is required", "MISSING_PARAM").Build();

                var roomId = new ElementId(int.Parse(parameters["roomId"].ToString()));
                var room = doc.GetElement(roomId) as Room;
                if (room == null)
                    return ResponseBuilder.Error("Room not found", "NOT_FOUND").Build();

                var roomBB = room.get_BoundingBox(null);
                if (roomBB == null)
                    return ResponseBuilder.Error("Room has no geometry / bounding box", "NO_GEOMETRY").Build();

                // Create outline for intersection filter
                var outline = new Outline(roomBB.Min, roomBB.Max);
                var bbFilter = new BoundingBoxIntersectsFilter(outline);

                var ceilings = new FilteredElementCollector(doc)
                    .OfClass(typeof(Ceiling))
                    .WherePasses(bbFilter)
                    .Cast<Ceiling>()
                    .Select(c =>
                    {
                        var areaParam = c.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        var heightParam = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        double height = heightParam != null ? heightParam.AsDouble() : 0;

                        return new
                        {
                            ceilingId = (int)c.Id.Value,
                            typeName = c.Name,
                            area = area,
                            heightOffset = height
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roomId = (int)room.Id.Value,
                    roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unknown",
                    ceilingCount = ceilings.Count,
                    ceilings = ceilings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get material info for a ceiling.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// </summary>
        [MCPMethod("getCeilingMaterial", Category = "Ceiling", Description = "Get material information for a ceiling including name, class, and appearance")]
        public static string GetCeilingMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                var ceilingType = doc.GetElement(ceiling.GetTypeId()) as CeilingType;
                var materials = new List<object>();

                if (ceilingType != null)
                {
                    var cs = ceilingType.GetCompoundStructure();
                    if (cs != null)
                    {
                        var layers = cs.GetLayers();
                        int layerIndex = 0;
                        foreach (var layer in layers)
                        {
                            var mat = doc.GetElement(layer.MaterialId) as Material;
                            materials.Add(new
                            {
                                layerIndex = layerIndex,
                                materialId = mat != null ? (int)mat.Id.Value : -1,
                                materialName = mat?.Name ?? "None",
                                materialClass = mat?.MaterialClass ?? "None",
                                layerFunction = layer.Function.ToString(),
                                layerWidth = layer.Width
                            });
                            layerIndex++;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingId = (int)ceiling.Id.Value,
                    typeName = ceilingType?.Name ?? "Unknown",
                    layerCount = materials.Count,
                    materials = materials
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get precise ceiling area.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// </summary>
        [MCPMethod("getCeilingArea", Category = "Ceiling", Description = "Get the precise computed area of a ceiling")]
        public static string GetCeilingArea(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                var areaParam = ceiling.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double area = areaParam != null ? areaParam.AsDouble() : 0;

                var perimeterParam = ceiling.get_Parameter(BuiltInParameter.HOST_PERIMETER_COMPUTED);
                double perimeter = perimeterParam != null ? perimeterParam.AsDouble() : 0;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingId = (int)ceiling.Id.Value,
                    area = area,
                    areaSqFt = area,
                    areaSqM = area * 0.092903,
                    perimeter = perimeter
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Modification Methods

        /// <summary>
        /// Change ceiling type.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// - newTypeId: ID of the new ceiling type
        /// </summary>
        [MCPMethod("modifyCeilingType", Category = "Ceiling", Description = "Change the type of an existing ceiling")]
        public static string ModifyCeilingType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();
                if (parameters["newTypeId"] == null)
                    return ResponseBuilder.Error("newTypeId is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                var newTypeId = new ElementId(int.Parse(parameters["newTypeId"].ToString()));
                var newType = doc.GetElement(newTypeId) as CeilingType;
                if (newType == null)
                    return ResponseBuilder.Error("Ceiling type not found", "NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Modify Ceiling Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ceiling.ChangeTypeId(newTypeId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ceilingId = (int)ceiling.Id.Value,
                        newTypeName = newType.Name,
                        message = "Ceiling type changed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a ceiling.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// </summary>
        [MCPMethod("deleteCeiling", Category = "Ceiling", Description = "Delete a ceiling element from the document")]
        public static string DeleteCeiling(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                string typeName = ceiling.Name;

                using (var trans = new Transaction(doc, "Delete Ceiling"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(ceilingId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedCeilingId = (int)ceilingId.Value,
                        typeName = typeName,
                        message = "Ceiling deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set ceiling height offset from level.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// - heightOffset: height offset from level in feet
        /// </summary>
        [MCPMethod("setCeilingHeight", Category = "Ceiling", Description = "Set the ceiling height offset from its associated level")]
        public static string SetCeilingHeight(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();
                if (parameters["heightOffset"] == null)
                    return ResponseBuilder.Error("heightOffset is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                double heightOffset = double.Parse(parameters["heightOffset"].ToString());

                using (var trans = new Transaction(doc, "Set Ceiling Height"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (heightParam != null && !heightParam.IsReadOnly)
                    {
                        heightParam.Set(heightOffset);
                    }
                    else
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Cannot set height on this ceiling", "INVALID_OPERATION").Build();
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ceilingId = (int)ceiling.Id.Value,
                        heightOffset = heightOffset,
                        message = "Ceiling height set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set ceiling material.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// - materialId: ID of the material to apply
        /// </summary>
        [MCPMethod("setCeilingMaterial", Category = "Ceiling", Description = "Set the material for a ceiling's structural layer")]
        public static string SetCeilingMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();
                if (parameters["materialId"] == null)
                    return ResponseBuilder.Error("materialId is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                var materialElemId = new ElementId(int.Parse(parameters["materialId"].ToString()));
                var material = doc.GetElement(materialElemId) as Material;
                if (material == null)
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();

                var ceilingType = doc.GetElement(ceiling.GetTypeId()) as CeilingType;
                if (ceilingType == null)
                    return ResponseBuilder.Error("Ceiling type not found", "NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Set Ceiling Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var cs = ceilingType.GetCompoundStructure();
                    if (cs != null)
                    {
                        var layers = cs.GetLayers();
                        if (layers.Count > 0)
                        {
                            // Set material on the first (or structural) layer
                            int targetLayerIdx = 0;
                            foreach (var layer in layers)
                            {
                                if (layer.Function == MaterialFunctionAssignment.Structure)
                                {
                                    targetLayerIdx = layers.IndexOf(layer);
                                    break;
                                }
                            }
                            cs.SetMaterialId(targetLayerIdx, materialElemId);
                            ceilingType.SetCompoundStructure(cs);
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ceilingId = (int)ceiling.Id.Value,
                        materialName = material.Name,
                        message = "Ceiling material set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create an opening in a ceiling.
        /// Parameters:
        /// - ceilingId: ID of the ceiling element
        /// - openingPoints: array of [x, y, z] points defining the opening boundary
        /// </summary>
        [MCPMethod("createCeilingOpening", Category = "Ceiling", Description = "Create an opening in a ceiling from boundary points")]
        public static string CreateCeilingOpening(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilingId"] == null)
                    return ResponseBuilder.Error("ceilingId is required", "MISSING_PARAM").Build();
                if (parameters["openingPoints"] == null)
                    return ResponseBuilder.Error("openingPoints is required", "MISSING_PARAM").Build();

                var ceilingId = new ElementId(int.Parse(parameters["ceilingId"].ToString()));
                var ceiling = doc.GetElement(ceilingId) as Ceiling;
                if (ceiling == null)
                    return ResponseBuilder.Error("Ceiling not found", "NOT_FOUND").Build();

                var points = parameters["openingPoints"].ToObject<double[][]>();
                if (points.Length < 3)
                    return ResponseBuilder.Error("At least 3 points are required for an opening", "INVALID_PARAM").Build();

                using (var trans = new Transaction(doc, "Create Ceiling Opening"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve array for opening boundary
                    var curveArray = new CurveArray();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                        var end = new XYZ(points[(i + 1) % points.Length][0],
                                         points[(i + 1) % points.Length][1],
                                         points[(i + 1) % points.Length][2]);
                        curveArray.Append(Line.CreateBound(start, end));
                    }

                    var opening = doc.Create.NewOpening(ceiling, curveArray, true);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ceilingId = (int)ceiling.Id.Value,
                        openingId = opening != null ? (int)opening.Id.Value : -1,
                        pointCount = points.Length,
                        message = "Ceiling opening created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a ceiling type with a new name.
        /// Parameters:
        /// - sourceTypeId: ID of the source ceiling type to duplicate
        /// - newName: name for the duplicated type
        /// </summary>
        [MCPMethod("duplicateCeilingType", Category = "Ceiling", Description = "Duplicate an existing ceiling type with a new name")]
        public static string DuplicateCeilingType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceTypeId"] == null)
                    return ResponseBuilder.Error("sourceTypeId is required", "MISSING_PARAM").Build();
                if (parameters["newName"] == null)
                    return ResponseBuilder.Error("newName is required", "MISSING_PARAM").Build();

                var sourceTypeId = new ElementId(int.Parse(parameters["sourceTypeId"].ToString()));
                var sourceType = doc.GetElement(sourceTypeId) as CeilingType;
                if (sourceType == null)
                    return ResponseBuilder.Error("Source ceiling type not found", "NOT_FOUND").Build();

                string newName = parameters["newName"].ToString();

                // Check if name already exists
                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(CeilingType))
                    .Cast<CeilingType>()
                    .FirstOrDefault(ct => ct.Name == newName);

                if (existing != null)
                    return ResponseBuilder.Error($"A ceiling type named '{newName}' already exists", "DUPLICATE_NAME").Build();

                using (var trans = new Transaction(doc, "Duplicate Ceiling Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var newType = sourceType.Duplicate(newName) as CeilingType;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sourceTypeId = (int)sourceType.Id.Value,
                        sourceTypeName = sourceType.Name,
                        newTypeId = (int)newType.Id.Value,
                        newTypeName = newType.Name,
                        message = "Ceiling type duplicated successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create multiple ceilings at once.
        /// Parameters:
        /// - ceilings: array of { boundaryPoints: [[x,y,z],...], typeId: int, levelId: int, heightOffset: double (optional) }
        /// </summary>
        [MCPMethod("batchCreateCeilings", Category = "Ceiling", Description = "Create multiple ceilings at once from an array of definitions")]
        public static string BatchCreateCeilings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["ceilings"] == null)
                    return ResponseBuilder.Error("ceilings array is required", "MISSING_PARAM").Build();

                var ceilingDefs = parameters["ceilings"] as JArray;
                if (ceilingDefs == null || ceilingDefs.Count == 0)
                    return ResponseBuilder.Error("ceilings array must not be empty", "INVALID_PARAM").Build();

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                using (var trans = new Transaction(doc, "Batch Create Ceilings"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (JObject def in ceilingDefs)
                    {
                        try
                        {
                            var points = def["boundaryPoints"]?.ToObject<double[][]>();
                            if (points == null || points.Length < 3)
                            {
                                results.Add(new { success = false, error = "Invalid boundary points" });
                                failCount++;
                                continue;
                            }

                            var levelId = new ElementId(int.Parse(def["levelId"].ToString()));
                            var level = doc.GetElement(levelId) as Level;
                            if (level == null)
                            {
                                results.Add(new { success = false, error = "Invalid level ID" });
                                failCount++;
                                continue;
                            }

                            // Get ceiling type
                            CeilingType ceilingType = null;
                            if (def["typeId"] != null)
                            {
                                var typeId = new ElementId(int.Parse(def["typeId"].ToString()));
                                ceilingType = doc.GetElement(typeId) as CeilingType;
                            }

                            if (ceilingType == null)
                            {
                                ceilingType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(CeilingType))
                                    .Cast<CeilingType>()
                                    .FirstOrDefault();
                            }

                            if (ceilingType == null)
                            {
                                results.Add(new { success = false, error = "No ceiling type available" });
                                failCount++;
                                continue;
                            }

                            // Create curve loop
                            var curveLoop = new CurveLoop();
                            for (int i = 0; i < points.Length; i++)
                            {
                                var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                                var end = new XYZ(points[(i + 1) % points.Length][0],
                                                 points[(i + 1) % points.Length][1],
                                                 points[(i + 1) % points.Length][2]);
                                curveLoop.Append(Line.CreateBound(start, end));
                            }

                            var curveLoops = new List<CurveLoop> { curveLoop };
                            var ceiling = Ceiling.Create(doc, curveLoops, ceilingType.Id, levelId);

                            // Set height offset if provided
                            double heightOffset = def["heightOffset"]?.ToObject<double>() ?? 0;
                            if (heightOffset != 0)
                            {
                                var heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                                if (heightParam != null && !heightParam.IsReadOnly)
                                {
                                    heightParam.Set(heightOffset);
                                }
                            }

                            results.Add(new
                            {
                                success = true,
                                ceilingId = (int)ceiling.Id.Value,
                                typeName = ceilingType.Name
                            });
                            successCount++;
                        }
                        catch (Exception innerEx)
                        {
                            results.Add(new { success = false, error = innerEx.Message });
                            failCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalRequested = ceilingDefs.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create or adjust ceiling to match room boundary.
        /// Parameters:
        /// - roomId: ID of the room
        /// - ceilingTypeId: (optional) ID of ceiling type, uses default if not specified
        /// - heightOffset: (optional) height offset from level in feet (default 8.0)
        /// </summary>
        [MCPMethod("alignCeilingToRoom", Category = "Ceiling", Description = "Create or adjust a ceiling to match a room's boundary")]
        public static string AlignCeilingToRoom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roomId"] == null)
                    return ResponseBuilder.Error("roomId is required", "MISSING_PARAM").Build();

                var roomId = new ElementId(int.Parse(parameters["roomId"].ToString()));
                var room = doc.GetElement(roomId) as Room;
                if (room == null)
                    return ResponseBuilder.Error("Room not found", "NOT_FOUND").Build();

                // Get room boundary
                var boundaryOptions = new SpatialElementBoundaryOptions();
                var segments = room.GetBoundarySegments(boundaryOptions);
                if (segments == null || segments.Count == 0)
                    return ResponseBuilder.Error("Room has no boundary segments", "NO_GEOMETRY").Build();

                // Get ceiling type
                CeilingType ceilingType = null;
                if (parameters["ceilingTypeId"] != null)
                {
                    var typeId = new ElementId(int.Parse(parameters["ceilingTypeId"].ToString()));
                    ceilingType = doc.GetElement(typeId) as CeilingType;
                }

                if (ceilingType == null)
                {
                    ceilingType = new FilteredElementCollector(doc)
                        .OfClass(typeof(CeilingType))
                        .Cast<CeilingType>()
                        .FirstOrDefault();
                }

                if (ceilingType == null)
                    return ResponseBuilder.Error("No ceiling type available", "NOT_FOUND").Build();

                double heightOffset = parameters["heightOffset"]?.ToObject<double>() ?? 8.0;

                using (var trans = new Transaction(doc, "Align Ceiling To Room"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Build curve loop from room boundary (use first boundary loop)
                    var firstLoop = segments[0];
                    var curveLoop = new CurveLoop();
                    foreach (var segment in firstLoop)
                    {
                        curveLoop.Append(segment.GetCurve());
                    }

                    var curveLoops = new List<CurveLoop> { curveLoop };
                    var ceiling = Ceiling.Create(doc, curveLoops, ceilingType.Id, room.LevelId);

                    // Set height offset
                    var heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (heightParam != null && !heightParam.IsReadOnly)
                    {
                        heightParam.Set(heightOffset);
                    }

                    trans.Commit();

                    var areaParam = ceiling.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    double area = areaParam != null ? areaParam.AsDouble() : 0;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ceilingId = (int)ceiling.Id.Value,
                        roomId = (int)room.Id.Value,
                        roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unknown",
                        typeName = ceilingType.Name,
                        heightOffset = heightOffset,
                        area = area,
                        message = "Ceiling aligned to room boundary successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
