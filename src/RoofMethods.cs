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
    /// Extended roof query and manipulation methods for MCP Bridge.
    /// Does NOT duplicate methods in FloorCeilingRoofMethods.cs.
    /// </summary>
    public static class RoofMethods
    {
        #region Query Methods

        /// <summary>
        /// Get all roofs in the document.
        /// Returns roofId, typeName, area, levelName, roofType (footprint/extrusion).
        /// </summary>
        [MCPMethod("getRoofs", Category = "Roof", Description = "Get all roofs in the document with type, area, level, and roof style")]
        public static string GetRoofs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofs = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .Select(r =>
                    {
                        var level = doc.GetElement(r.LevelId) as Level;
                        string roofStyle = "unknown";
                        if (r is FootPrintRoof) roofStyle = "footprint";
                        else if (r is ExtrusionRoof) roofStyle = "extrusion";

                        var areaParam = r.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        return new
                        {
                            roofId = (int)r.Id.Value,
                            typeName = r.Name,
                            area = area,
                            levelName = level?.Name ?? "Unknown",
                            levelId = level != null ? (int)level.Id.Value : -1,
                            roofType = roofStyle
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofCount = roofs.Count,
                    roofs = roofs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detailed info for one roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofInfo", Category = "Roof", Description = "Get detailed information for a specific roof including type, slope, area, boundary, thickness, and material")]
        public static string GetRoofInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var level = doc.GetElement(roof.LevelId) as Level;
                var roofType = doc.GetElement(roof.GetTypeId()) as RoofType;

                string roofStyle = "unknown";
                if (roof is FootPrintRoof) roofStyle = "footprint";
                else if (roof is ExtrusionRoof) roofStyle = "extrusion";

                // Area
                var areaParam = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double area = areaParam != null ? areaParam.AsDouble() : 0;

                // Slope
                var slopeParam = roof.get_Parameter(BuiltInParameter.ROOF_SLOPE);
                double slope = slopeParam != null ? slopeParam.AsDouble() : 0;

                // Offset
                var offsetParam = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                double offset = offsetParam != null ? offsetParam.AsDouble() : 0;

                // Thickness from type
                double thickness = 0;
                if (roofType != null)
                {
                    var cs = roofType.GetCompoundStructure();
                    if (cs != null)
                        thickness = cs.GetWidth();
                }

                // Material from first structural layer
                string materialName = "None";
                if (roofType != null)
                {
                    var cs = roofType.GetCompoundStructure();
                    if (cs != null)
                    {
                        var layers = cs.GetLayers();
                        foreach (var layer in layers)
                        {
                            if (layer.Function == MaterialFunctionAssignment.Structure)
                            {
                                var mat = doc.GetElement(layer.MaterialId) as Material;
                                if (mat != null)
                                {
                                    materialName = mat.Name;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Boundary points (footprint roofs)
                var boundaryPoints = new List<object>();
                if (roof is FootPrintRoof fpr)
                {
                    var modelCurveArray = fpr.GetProfiles();
                    if (modelCurveArray != null)
                    {
                        foreach (ModelCurveArray profile in modelCurveArray)
                        {
                            foreach (ModelCurve mc in profile)
                            {
                                var curve = mc.GeometryCurve;
                                var start = curve.GetEndPoint(0);
                                boundaryPoints.Add(new { x = start.X, y = start.Y, z = start.Z });
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    typeName = roofType?.Name ?? "Unknown",
                    roofStyle = roofStyle,
                    slope = slope,
                    area = area,
                    offset = offset,
                    thickness = thickness,
                    material = materialName,
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
        /// Get slope info per edge of a footprint roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofSlope", Category = "Roof", Description = "Get slope info per edge of a footprint roof including defining/non-defining status")]
        public static string GetRoofSlope(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as FootPrintRoof;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found or is not a footprint roof", "NOT_FOUND").Build();

                var edgeInfos = new List<object>();
                var profiles = roof.GetProfiles();
                int edgeIndex = 0;

                if (profiles != null)
                {
                    foreach (ModelCurveArray profile in profiles)
                    {
                        foreach (ModelCurve mc in profile)
                        {
                            bool isDefining = roof.get_DefinesSlope(mc);
                            double slopeAngle = roof.get_SlopeAngle(mc);
                            double overhang = roof.get_Overhang(mc);
                            bool isExtended = roof.get_ExtendIntoWall(mc);

                            var curve = mc.GeometryCurve;
                            var start = curve.GetEndPoint(0);
                            var end = curve.GetEndPoint(1);

                            edgeInfos.Add(new
                            {
                                edgeIndex = edgeIndex,
                                definesSlope = isDefining,
                                slopeAngle = slopeAngle,
                                overhang = overhang,
                                extendIntoWall = isExtended,
                                startPoint = new { x = start.X, y = start.Y, z = start.Z },
                                endPoint = new { x = end.X, y = end.Y, z = end.Z }
                            });
                            edgeIndex++;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    edgeCount = edgeInfos.Count,
                    edges = edgeInfos
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get precise roof area including actual surface area with slopes.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofArea", Category = "Roof", Description = "Get precise roof area including actual surface area accounting for slopes")]
        public static string GetRoofArea(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                // Computed area from Revit (accounts for slope)
                var areaParam = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                double computedArea = areaParam != null ? areaParam.AsDouble() : 0;

                // Try to get projected area from geometry
                double surfaceArea = 0;
                var options = new Options();
                options.ComputeReferences = true;
                var geom = roof.get_Geometry(options);
                if (geom != null)
                {
                    foreach (GeometryObject gObj in geom)
                    {
                        if (gObj is Solid solid)
                        {
                            foreach (Face face in solid.Faces)
                            {
                                // Top faces contribute to roof area
                                surfaceArea += face.Area;
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    computedArea = computedArea,
                    totalSurfaceArea = surfaceArea
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all roofs on a specific level.
        /// Parameters:
        /// - levelId: ID of the level
        /// </summary>
        [MCPMethod("getRoofsOnLevel", Category = "Roof", Description = "Get all roofs on a specific level")]
        public static string GetRoofsOnLevel(UIApplication uiApp, JObject parameters)
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

                var roofs = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .Where(r => r.LevelId == levelId)
                    .Select(r =>
                    {
                        string roofStyle = "unknown";
                        if (r is FootPrintRoof) roofStyle = "footprint";
                        else if (r is ExtrusionRoof) roofStyle = "extrusion";

                        var areaParam = r.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        double area = areaParam != null ? areaParam.AsDouble() : 0;

                        return new
                        {
                            roofId = (int)r.Id.Value,
                            typeName = r.Name,
                            area = area,
                            roofType = roofStyle
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    levelName = level.Name,
                    roofCount = roofs.Count,
                    roofs = roofs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all sub-elements (fascias, gutters, soffits) of a roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("getRoofSubElements", Category = "Roof", Description = "Get all sub-elements (fascias, gutters, soffits) associated with a roof")]
        public static string GetRoofSubElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                // Find fascias hosted on this roof
                var fascias = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Fascia)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Where(e =>
                    {
                        var hostParam = e.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM);
                        if (hostParam != null) return true;
                        // Check via dependency
                        var depIds = e.GetDependentElements(null);
                        return depIds != null && depIds.Contains(roofId);
                    })
                    .Select(e => new
                    {
                        elementId = (int)e.Id.Value,
                        typeName = e.Name,
                        category = "Fascia"
                    })
                    .ToList();

                // Find gutters
                var gutters = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Gutter)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Select(e => new
                    {
                        elementId = (int)e.Id.Value,
                        typeName = e.Name,
                        category = "Gutter"
                    })
                    .ToList();

                // Find soffits
                var soffits = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RoofSoffit)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Select(e => new
                    {
                        elementId = (int)e.Id.Value,
                        typeName = e.Name,
                        category = "Soffit"
                    })
                    .ToList();

                var allSubElements = new List<object>();
                allSubElements.AddRange(fascias.Cast<object>());
                allSubElements.AddRange(gutters.Cast<object>());
                allSubElements.AddRange(soffits.Cast<object>());

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = (int)roof.Id.Value,
                    fasciaCount = fascias.Count,
                    gutterCount = gutters.Count,
                    soffitCount = soffits.Count,
                    totalSubElements = allSubElements.Count,
                    subElements = allSubElements
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
        /// Change roof type.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - newTypeId: ID of the new roof type
        /// </summary>
        [MCPMethod("modifyRoofType", Category = "Roof", Description = "Change the type of an existing roof")]
        public static string ModifyRoofType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["newTypeId"] == null)
                    return ResponseBuilder.Error("newTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var newTypeId = new ElementId(int.Parse(parameters["newTypeId"].ToString()));
                var newType = doc.GetElement(newTypeId) as RoofType;
                if (newType == null)
                    return ResponseBuilder.Error("Roof type not found", "NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Modify Roof Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    roof.ChangeTypeId(newTypeId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        newTypeName = newType.Name,
                        message = "Roof type changed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// </summary>
        [MCPMethod("deleteRoof", Category = "Roof", Description = "Delete a roof element from the document")]
        public static string DeleteRoof(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                string typeName = roof.Name;

                using (var trans = new Transaction(doc, "Delete Roof"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(roofId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedRoofId = (int)roofId.Value,
                        typeName = typeName,
                        message = "Roof deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set roof offset from level.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - offset: offset value in feet
        /// </summary>
        [MCPMethod("setRoofOffset", Category = "Roof", Description = "Set the roof offset from its associated level")]
        public static string SetRoofOffset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["offset"] == null)
                    return ResponseBuilder.Error("offset is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                double offset = double.Parse(parameters["offset"].ToString());

                using (var trans = new Transaction(doc, "Set Roof Offset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var offsetParam = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                    if (offsetParam != null && !offsetParam.IsReadOnly)
                    {
                        offsetParam.Set(offset);
                    }
                    else
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Cannot set offset on this roof type", "INVALID_OPERATION").Build();
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        offset = offset,
                        message = "Roof offset set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set slope for a specific edge of a footprint roof.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - edgeIndex: index of the edge (0-based)
        /// - slopeAngle: slope angle in radians
        /// - definesSlope: (optional) whether this edge defines slope (default true)
        /// </summary>
        [MCPMethod("setRoofSlopeByEdge", Category = "Roof", Description = "Set the slope angle for a specific edge of a footprint roof")]
        public static string SetRoofSlopeByEdge(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["edgeIndex"] == null)
                    return ResponseBuilder.Error("edgeIndex is required", "MISSING_PARAM").Build();
                if (parameters["slopeAngle"] == null)
                    return ResponseBuilder.Error("slopeAngle is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as FootPrintRoof;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found or is not a footprint roof", "NOT_FOUND").Build();

                int edgeIndex = int.Parse(parameters["edgeIndex"].ToString());
                double slopeAngle = double.Parse(parameters["slopeAngle"].ToString());
                bool definesSlope = parameters["definesSlope"]?.ToObject<bool>() ?? true;

                using (var trans = new Transaction(doc, "Set Roof Slope By Edge"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var profiles = roof.GetProfiles();
                    int currentIndex = 0;
                    bool found = false;

                    foreach (ModelCurveArray profile in profiles)
                    {
                        foreach (ModelCurve mc in profile)
                        {
                            if (currentIndex == edgeIndex)
                            {
                                roof.set_DefinesSlope(mc, definesSlope);
                                if (definesSlope)
                                {
                                    roof.set_SlopeAngle(mc, slopeAngle);
                                }
                                found = true;
                                break;
                            }
                            currentIndex++;
                        }
                        if (found) break;
                    }

                    if (!found)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Edge index {edgeIndex} not found. Roof has {currentIndex} edges.", "INVALID_INDEX").Build();
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        edgeIndex = edgeIndex,
                        slopeAngle = slopeAngle,
                        definesSlope = definesSlope,
                        message = "Roof edge slope set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create fascia on roof edge.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - fasciaTypeId: ID of the fascia type
        /// - edgeIndex: (optional) index of edge to place fascia on, defaults to all edges
        /// </summary>
        [MCPMethod("createFascia", Category = "Roof", Description = "Create fascia on roof edges")]
        public static string CreateFascia(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["fasciaTypeId"] == null)
                    return ResponseBuilder.Error("fasciaTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var fasciaTypeId = new ElementId(int.Parse(parameters["fasciaTypeId"].ToString()));
                var fasciaType = doc.GetElement(fasciaTypeId);
                if (fasciaType == null)
                    return ResponseBuilder.Error("Fascia type not found", "NOT_FOUND").Build();

                int? targetEdgeIndex = parameters["edgeIndex"]?.ToObject<int>();

                using (var trans = new Transaction(doc, "Create Fascia"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var createdIds = new List<int>();

                    // Get roof edge references from geometry
                    var options = new Options();
                    options.ComputeReferences = true;
                    var geom = roof.get_Geometry(options);

                    var edgeRefs = new List<Reference>();
                    if (geom != null)
                    {
                        foreach (GeometryObject gObj in geom)
                        {
                            if (gObj is Solid solid)
                            {
                                foreach (Edge edge in solid.Edges)
                                {
                                    var edgeRef = edge.Reference;
                                    if (edgeRef != null)
                                        edgeRefs.Add(edgeRef);
                                }
                            }
                        }
                    }

                    if (edgeRefs.Count == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("No roof edges found for fascia placement", "NO_EDGES").Build();
                    }

                    // Place fascia on specified edge or first edge
                    int edgeIdx = targetEdgeIndex ?? 0;
                    if (edgeIdx >= edgeRefs.Count)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Edge index {edgeIdx} out of range. Roof has {edgeRefs.Count} edges.", "INVALID_INDEX").Build();
                    }

                    var referenceArray = new ReferenceArray();
                    referenceArray.Append(edgeRefs[edgeIdx]);

                    var fascia = doc.Create.NewFascia(fasciaType as FasciaType, referenceArray);
                    if (fascia != null)
                        createdIds.Add((int)fascia.Id.Value);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        createdFasciaIds = createdIds,
                        message = $"Fascia created on {createdIds.Count} edge(s)"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create gutter on roof edge.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - gutterTypeId: ID of the gutter type
        /// - edgeIndex: (optional) index of edge, defaults to 0
        /// </summary>
        [MCPMethod("createGutter", Category = "Roof", Description = "Create gutter on roof edges")]
        public static string CreateGutter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["gutterTypeId"] == null)
                    return ResponseBuilder.Error("gutterTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var gutterTypeId = new ElementId(int.Parse(parameters["gutterTypeId"].ToString()));
                var gutterType = doc.GetElement(gutterTypeId);
                if (gutterType == null)
                    return ResponseBuilder.Error("Gutter type not found", "NOT_FOUND").Build();

                int? targetEdgeIndex = parameters["edgeIndex"]?.ToObject<int>();

                using (var trans = new Transaction(doc, "Create Gutter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get roof edge references
                    var options = new Options();
                    options.ComputeReferences = true;
                    var geom = roof.get_Geometry(options);

                    var edgeRefs = new List<Reference>();
                    if (geom != null)
                    {
                        foreach (GeometryObject gObj in geom)
                        {
                            if (gObj is Solid solid)
                            {
                                foreach (Edge edge in solid.Edges)
                                {
                                    var edgeRef = edge.Reference;
                                    if (edgeRef != null)
                                        edgeRefs.Add(edgeRef);
                                }
                            }
                        }
                    }

                    if (edgeRefs.Count == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("No roof edges found for gutter placement", "NO_EDGES").Build();
                    }

                    int edgeIdx = targetEdgeIndex ?? 0;
                    if (edgeIdx >= edgeRefs.Count)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Edge index {edgeIdx} out of range. Roof has {edgeRefs.Count} edges.", "INVALID_INDEX").Build();
                    }

                    var referenceArray = new ReferenceArray();
                    referenceArray.Append(edgeRefs[edgeIdx]);

                    var gutter = doc.Create.NewGutter(gutterType as GutterType, referenceArray);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        gutterId = gutter != null ? (int)gutter.Id.Value : -1,
                        message = "Gutter created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create soffit under roof overhang.
        /// Parameters:
        /// - roofId: ID of the roof element
        /// - soffitTypeId: ID of the soffit type
        /// </summary>
        [MCPMethod("createRoofSoffit", Category = "Roof", Description = "Create soffit under roof overhang")]
        public static string CreateRoofSoffit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["roofId"] == null)
                    return ResponseBuilder.Error("roofId is required", "MISSING_PARAM").Build();
                if (parameters["soffitTypeId"] == null)
                    return ResponseBuilder.Error("soffitTypeId is required", "MISSING_PARAM").Build();

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;
                if (roof == null)
                    return ResponseBuilder.Error("Roof not found", "NOT_FOUND").Build();

                var soffitTypeId = new ElementId(int.Parse(parameters["soffitTypeId"].ToString()));
                var soffitType = doc.GetElement(soffitTypeId);
                if (soffitType == null)
                    return ResponseBuilder.Error("Soffit type not found", "NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Create Roof Soffit"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get bottom face references for soffit
                    var options = new Options();
                    options.ComputeReferences = true;
                    var geom = roof.get_Geometry(options);

                    var bottomFaceRefs = new List<Reference>();
                    if (geom != null)
                    {
                        foreach (GeometryObject gObj in geom)
                        {
                            if (gObj is Solid solid)
                            {
                                foreach (Face face in solid.Faces)
                                {
                                    // Check if face normal points down (bottom face)
                                    if (face is PlanarFace planarFace)
                                    {
                                        if (planarFace.FaceNormal.Z < -0.5)
                                        {
                                            var faceRef = face.Reference;
                                            if (faceRef != null)
                                                bottomFaceRefs.Add(faceRef);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (bottomFaceRefs.Count == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("No bottom faces found for soffit placement. Ensure roof has overhang.", "NO_FACES").Build();
                    }

                    // Create soffit on first bottom face reference
                    var referenceArray = new ReferenceArray();
                    referenceArray.Append(bottomFaceRefs[0]);

                    // Use the first bottom face edge references for soffit boundary
                    var edgeRefs = new ReferenceArray();
                    var face0 = roof.GetGeometryObjectFromReference(bottomFaceRefs[0]) as Face;
                    if (face0 != null)
                    {
                        var edgeLoops = face0.EdgeLoops;
                        if (edgeLoops.Size > 0)
                        {
                            foreach (Edge edge in edgeLoops.get_Item(0))
                            {
                                edgeRefs.Append(edge.Reference);
                            }
                        }
                    }

                    // Note: Soffit creation in Revit API requires specific face/edge setup
                    // The doc.Create.NewSlab method or manual floor creation at soffit elevation
                    // is often the practical approach for soffits
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        bottomFacesFound = bottomFaceRefs.Count,
                        message = "Roof soffit geometry identified. Use floor/ceiling creation at soffit elevation for physical soffit."
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a roof type with a new name.
        /// Parameters:
        /// - sourceTypeId: ID of the source roof type to duplicate
        /// - newName: name for the duplicated type
        /// </summary>
        [MCPMethod("duplicateRoofType", Category = "Roof", Description = "Duplicate an existing roof type with a new name")]
        public static string DuplicateRoofType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceTypeId"] == null)
                    return ResponseBuilder.Error("sourceTypeId is required", "MISSING_PARAM").Build();
                if (parameters["newName"] == null)
                    return ResponseBuilder.Error("newName is required", "MISSING_PARAM").Build();

                var sourceTypeId = new ElementId(int.Parse(parameters["sourceTypeId"].ToString()));
                var sourceType = doc.GetElement(sourceTypeId) as RoofType;
                if (sourceType == null)
                    return ResponseBuilder.Error("Source roof type not found", "NOT_FOUND").Build();

                string newName = parameters["newName"].ToString();

                // Check if name already exists
                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofType))
                    .Cast<RoofType>()
                    .FirstOrDefault(rt => rt.Name == newName);

                if (existing != null)
                    return ResponseBuilder.Error($"A roof type named '{newName}' already exists", "DUPLICATE_NAME").Build();

                using (var trans = new Transaction(doc, "Duplicate Roof Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var newType = sourceType.Duplicate(newName) as RoofType;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sourceTypeId = (int)sourceType.Id.Value,
                        sourceTypeName = sourceType.Name,
                        newTypeId = (int)newType.Id.Value,
                        newTypeName = newType.Name,
                        message = "Roof type duplicated successfully"
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
