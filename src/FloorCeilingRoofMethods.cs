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
    /// Floor, ceiling, and roof creation methods for MCP Bridge
    /// </summary>
    public static class FloorCeilingRoofMethods
    {
        #region Floor Methods

        /// <summary>
        /// Create a floor from boundary points
        /// Parameters:
        /// - boundaryPoints: array of [x, y, z] points defining the floor boundary
        /// - floorTypeId: (optional) ID of the floor type to use
        /// - levelId: ID of the level to place the floor on
        /// - structural: (optional) whether the floor is structural (default false)
        /// </summary>
        [MCPMethod("createFloor", Category = "FloorCeilingRoof", Description = "Create a floor from boundary points on a level")]
        public static string CreateFloor(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse boundary points
                if (parameters["boundaryPoints"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "boundaryPoints is required"
                    });
                }

                var points = parameters["boundaryPoints"].ToObject<double[][]>();
                if (points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least 3 points are required to create a floor"
                    });
                }

                // Parse level
                if (parameters["levelId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid level ID"
                    });
                }

                // Get floor type
                FloorType floorType = null;
                if (parameters["floorTypeId"] != null)
                {
                    var floorTypeId = new ElementId(int.Parse(parameters["floorTypeId"].ToString()));
                    floorType = doc.GetElement(floorTypeId) as FloorType;
                }
                else
                {
                    // Get first available floor type
                    floorType = new FilteredElementCollector(doc)
                        .OfClass(typeof(FloorType))
                        .Cast<FloorType>()
                        .FirstOrDefault();
                }

                if (floorType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid floor type found"
                    });
                }

                var structural = parameters["structural"]?.ToObject<bool>() ?? false;

                using (var trans = new Transaction(doc, "Create Floor"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve loop from points
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

                    // Create the floor
                    var floor = Floor.Create(doc, curveLoops, floorType.Id, levelId);

                    // Set structural property if needed
                    if (structural)
                    {
                        var structParam = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                        if (structParam != null && !structParam.IsReadOnly)
                        {
                            structParam.Set(1);
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        floorId = (int)floor.Id.Value,
                        floorType = floorType.Name,
                        level = level.Name,
                        area = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0,
                        structural = structural
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all available floor types
        /// </summary>
        [MCPMethod("getFloorTypes", Category = "FloorCeilingRoof", Description = "Get all available floor types in the model")]
        public static string GetFloorTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var floorTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .Select(ft => new
                    {
                        floorTypeId = (int)ft.Id.Value,
                        name = ft.Name,
                        familyName = ft.FamilyName,
                        isFoundation = ft.IsFoundationSlab
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    floorTypeCount = floorTypes.Count,
                    floorTypes = floorTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create an opening in a floor
        /// </summary>
        public static string CreateFloorOpening(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var floorId = new ElementId(int.Parse(parameters["floorId"].ToString()));
                var floor = doc.GetElement(floorId) as Floor;

                if (floor == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Floor not found"
                    });
                }

                var points = parameters["boundaryPoints"].ToObject<double[][]>();

                using (var trans = new Transaction(doc, "Create Floor Opening"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve array for opening
                    var curveArray = new CurveArray();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                        var end = new XYZ(points[(i + 1) % points.Length][0],
                                         points[(i + 1) % points.Length][1],
                                         points[(i + 1) % points.Length][2]);
                        curveArray.Append(Line.CreateBound(start, end));
                    }

                    // Create the opening
                    var opening = doc.Create.NewOpening(floor, curveArray, true);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        openingId = (int)opening.Id.Value,
                        floorId = (int)floorId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Ceiling Methods

        /// <summary>
        /// Create a ceiling from boundary points
        /// Parameters:
        /// - boundaryPoints: array of [x, y, z] points defining the ceiling boundary
        /// - ceilingTypeId: (optional) ID of the ceiling type to use
        /// - levelId: ID of the level to place the ceiling on
        /// - heightOffset: (optional) height offset from level (default 8 feet)
        /// </summary>
        [MCPMethod("createCeiling", Category = "FloorCeilingRoof", Description = "Create a ceiling from boundary points on a level")]
        public static string CreateCeiling(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse boundary points
                if (parameters["boundaryPoints"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "boundaryPoints is required"
                    });
                }

                var points = parameters["boundaryPoints"].ToObject<double[][]>();
                if (points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least 3 points are required to create a ceiling"
                    });
                }

                // Parse level
                if (parameters["levelId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid level ID"
                    });
                }

                // Get ceiling type
                CeilingType ceilingType = null;
                if (parameters["ceilingTypeId"] != null)
                {
                    var ceilingTypeId = new ElementId(int.Parse(parameters["ceilingTypeId"].ToString()));
                    ceilingType = doc.GetElement(ceilingTypeId) as CeilingType;
                }
                else
                {
                    // Get first available ceiling type
                    ceilingType = new FilteredElementCollector(doc)
                        .OfClass(typeof(CeilingType))
                        .Cast<CeilingType>()
                        .FirstOrDefault();
                }

                if (ceilingType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid ceiling type found"
                    });
                }

                var heightOffset = parameters["heightOffset"]?.ToObject<double>() ?? 8.0; // Default 8 feet

                using (var trans = new Transaction(doc, "Create Ceiling"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve loop from points
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

                    // Create the ceiling
                    var ceiling = Ceiling.Create(doc, curveLoops, ceilingType.Id, levelId);

                    // Set height offset
                    var heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (heightParam != null && !heightParam.IsReadOnly)
                    {
                        heightParam.Set(heightOffset);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ceilingId = (int)ceiling.Id.Value,
                        ceilingType = ceilingType.Name,
                        level = level.Name,
                        heightOffset = heightOffset,
                        area = ceiling.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all available ceiling types
        /// </summary>
        [MCPMethod("getCeilingTypes", Category = "FloorCeilingRoof", Description = "Get all available ceiling types in the model")]
        public static string GetCeilingTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ceilingTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(CeilingType))
                    .Cast<CeilingType>()
                    .Select(ct => new
                    {
                        ceilingTypeId = (int)ct.Id.Value,
                        name = ct.Name,
                        familyName = ct.FamilyName
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingTypeCount = ceilingTypes.Count,
                    ceilingTypes = ceilingTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Roof Methods

        /// <summary>
        /// Create a roof from footprint (boundary points)
        /// Parameters:
        /// - boundaryPoints: array of [x, y, z] points defining the roof footprint
        /// - roofTypeId: (optional) ID of the roof type to use
        /// - levelId: ID of the level to place the roof on
        /// - slope: (optional) default slope in degrees (default 30)
        /// - overhang: (optional) roof overhang in feet (default 1)
        /// </summary>
        [MCPMethod("createRoofByFootprint", "createFootprintRoof", Category = "FloorCeilingRoof", Description = "Create a roof by footprint from boundary points")]
        public static string CreateRoofByFootprint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse boundary points
                if (parameters["boundaryPoints"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "boundaryPoints is required"
                    });
                }

                var points = parameters["boundaryPoints"].ToObject<double[][]>();
                if (points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least 3 points are required to create a roof"
                    });
                }

                // Parse level
                if (parameters["levelId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid level ID"
                    });
                }

                // Get roof type
                RoofType roofType = null;
                if (parameters["roofTypeId"] != null)
                {
                    var roofTypeId = new ElementId(int.Parse(parameters["roofTypeId"].ToString()));
                    roofType = doc.GetElement(roofTypeId) as RoofType;
                }
                else
                {
                    // Get first available roof type
                    roofType = new FilteredElementCollector(doc)
                        .OfClass(typeof(RoofType))
                        .Cast<RoofType>()
                        .FirstOrDefault();
                }

                if (roofType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid roof type found"
                    });
                }

                var slopeDegrees = parameters["slope"]?.ToObject<double>() ?? 30.0;
                var slope = Math.Tan(slopeDegrees * Math.PI / 180.0); // Convert to rise/run
                var overhang = parameters["overhang"]?.ToObject<double>() ?? 1.0;

                using (var trans = new Transaction(doc, "Create Roof"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve array from points
                    var footprint = new CurveArray();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                        var end = new XYZ(points[(i + 1) % points.Length][0],
                                         points[(i + 1) % points.Length][1],
                                         points[(i + 1) % points.Length][2]);
                        footprint.Append(Line.CreateBound(start, end));
                    }

                    // Create model curve array for the footprint
                    var modelCurves = new ModelCurveArray();

                    // Create the roof
                    var roof = doc.Create.NewFootPrintRoof(
                        footprint,
                        level,
                        roofType,
                        out modelCurves);

                    // Set slope for each edge
                    foreach (ModelCurve mc in modelCurves)
                    {
                        roof.set_DefinesSlope(mc, true);
                        roof.set_SlopeAngle(mc, slope);
                        // Overhang only works for roofs created by pick walls
                        try
                        {
                            roof.set_Overhang(mc, overhang);
                        }
                        catch
                        {
                            // Overhang not supported for footprint-based roofs - skip silently
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roofId = (int)roof.Id.Value,
                        roofType = roofType.Name,
                        level = level.Name,
                        slopeDegrees = slopeDegrees,
                        overhang = overhang,
                        area = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all available roof types
        /// </summary>
        [MCPMethod("getRoofTypes", Category = "FloorCeilingRoof", Description = "Get all available roof types in the model")]
        public static string GetRoofTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofType))
                    .Cast<RoofType>()
                    .Select(rt => new
                    {
                        roofTypeId = (int)rt.Id.Value,
                        name = rt.Name,
                        familyName = rt.FamilyName
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofTypeCount = roofTypes.Count,
                    roofTypes = roofTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a roof opening (skylight cutout)
        /// </summary>
        public static string CreateRoofOpening(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId) as RoofBase;

                if (roof == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Roof not found"
                    });
                }

                var points = parameters["boundaryPoints"].ToObject<double[][]>();

                using (var trans = new Transaction(doc, "Create Roof Opening"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve array for opening
                    var curveArray = new CurveArray();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                        var end = new XYZ(points[(i + 1) % points.Length][0],
                                         points[(i + 1) % points.Length][1],
                                         points[(i + 1) % points.Length][2]);
                        curveArray.Append(Line.CreateBound(start, end));
                    }

                    // Create the opening
                    var opening = doc.Create.NewOpening(roof, curveArray, true);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        openingId = (int)opening.Id.Value,
                        roofId = (int)roofId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Get all levels in the project
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
                        levelId = (int)l.Id.Value,
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
        /// Get ALL floors in the entire model
        /// </summary>
        public static string GetFloors(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var floors = new FilteredElementCollector(doc)
                    .OfClass(typeof(Floor))
                    .Cast<Floor>()
                    .Select(f => {
                        // Get area parameter
                        var areaParam = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        var area = areaParam?.AsDouble() ?? 0;

                        // Get level
                        var levelId = f.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
                        var level = levelId != null ? doc.GetElement(levelId)?.Name : null;

                        // Get floor type info
                        var floorType = doc.GetElement(f.GetTypeId()) as FloorType;

                        return new
                        {
                            floorId = (int)f.Id.Value,
                            typeName = floorType?.Name ?? "Unknown",
                            typeId = (int)(f.GetTypeId()?.Value ?? 0),
                            level = level,
                            area = area,
                            thickness = floorType?.get_Parameter(BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM)?.AsDouble() ?? 0,
                            structural = f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    floorCount = floors.Count,
                    floors = floors
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL ceilings in the entire model
        /// </summary>
        public static string GetCeilings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ceilings = new FilteredElementCollector(doc)
                    .OfClass(typeof(Ceiling))
                    .Cast<Ceiling>()
                    .Select(c => {
                        // Get area parameter
                        var areaParam = c.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                        var area = areaParam?.AsDouble() ?? 0;

                        // Get level
                        var levelId = c.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
                        var level = levelId != null ? doc.GetElement(levelId)?.Name : null;

                        // Get height offset
                        var heightOffset = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;

                        // Get ceiling type info
                        var ceilingType = doc.GetElement(c.GetTypeId()) as CeilingType;

                        return new
                        {
                            ceilingId = (int)c.Id.Value,
                            typeName = ceilingType?.Name ?? "Unknown",
                            typeId = (int)(c.GetTypeId()?.Value ?? 0),
                            level = level,
                            area = area,
                            heightOffset = heightOffset
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
        /// Get ALL stairs in the entire model
        /// </summary>
        public static string GetStairs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var stairs = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WhereElementIsNotElementType()
                    .Select(s => {
                        var levelId = s.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId();
                        var topLevelId = s.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId();

                        return new
                        {
                            stairId = (int)s.Id.Value,
                            typeName = doc.GetElement(s.GetTypeId())?.Name ?? "Unknown",
                            typeId = (int)(s.GetTypeId()?.Value ?? 0),
                            baseLevel = levelId != null ? doc.GetElement(levelId)?.Name : null,
                            topLevel = topLevelId != null ? doc.GetElement(topLevelId)?.Name : null,
                            desiredRisers = s.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUM_RISERS)?.AsInteger() ?? 0,
                            actualRisers = s.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_NUM_RISERS)?.AsInteger() ?? 0,
                            actualTreadDepth = s.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH)?.AsDouble() ?? 0
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stairCount = stairs.Count,
                    stairs = stairs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL railings in the entire model
        /// </summary>
        public static string GetRailings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var railings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StairsRailing)
                    .WhereElementIsNotElementType()
                    .Select(r => {
                        var levelId = r.get_Parameter(BuiltInParameter.STAIRS_RAILING_BASE_LEVEL_PARAM)?.AsElementId();

                        return new
                        {
                            railingId = (int)r.Id.Value,
                            typeName = doc.GetElement(r.GetTypeId())?.Name ?? "Unknown",
                            typeId = (int)(r.GetTypeId()?.Value ?? 0),
                            level = levelId != null ? doc.GetElement(levelId)?.Name : null,
                            length = r.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    railingCount = railings.Count,
                    railings = railings
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL roofs in the entire model
        /// </summary>
        public static string GetRoofs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofs = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .Select(r => {
                        var levelId = r.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId();

                        return new
                        {
                            roofId = (int)r.Id.Value,
                            typeName = doc.GetElement(r.GetTypeId())?.Name ?? "Unknown",
                            typeId = (int)(r.GetTypeId()?.Value ?? 0),
                            level = levelId != null ? doc.GetElement(levelId)?.Name : null,
                            area = r.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0,
                            slope = r.get_Parameter(BuiltInParameter.ROOF_SLOPE)?.AsDouble() ?? 0
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
        /// Get ALL columns in the entire model (architectural and structural)
        /// </summary>
        public static string GetColumns(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get both architectural and structural columns
                var archColumns = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Columns)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>();

                var structColumns = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>();

                var allColumns = archColumns.Concat(structColumns)
                    .Select(c => {
                        var location = c.Location as LocationPoint;
                        var point = location?.Point;
                        var baseLevelId = c.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
                        var topLevelId = c.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId();

                        return new
                        {
                            columnId = (int)c.Id.Value,
                            familyName = c.Symbol.Family.Name,
                            typeName = c.Symbol.Name,
                            typeId = (int)c.Symbol.Id.Value,
                            baseLevel = baseLevelId != null ? doc.GetElement(baseLevelId)?.Name : null,
                            topLevel = topLevelId != null ? doc.GetElement(topLevelId)?.Name : null,
                            location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null,
                            isStructural = c.Category.Id.Value == (int)BuiltInCategory.OST_StructuralColumns
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    columnCount = allColumns.Count,
                    columns = allColumns
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get ALL curtain walls in the entire model
        /// </summary>
        public static string GetCurtainWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var curtainWalls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .Where(w => w.WallType.Kind == WallKind.Curtain)
                    .Select(w => {
                        var locationCurve = w.Location as LocationCurve;
                        var curve = locationCurve?.Curve;
                        XYZ startPoint = null;
                        XYZ endPoint = null;

                        if (curve != null)
                        {
                            startPoint = curve.GetEndPoint(0);
                            endPoint = curve.GetEndPoint(1);
                        }

                        var baseLevel = doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level;

                        return new
                        {
                            wallId = (int)w.Id.Value,
                            wallType = w.WallType.Name,
                            typeId = (int)w.WallType.Id.Value,
                            baseLevel = baseLevel?.Name,
                            length = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0,
                            height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                            startPoint = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                            endPoint = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    curtainWallCount = curtainWalls.Count,
                    curtainWalls = curtainWalls
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Ceiling Enhancement Methods

        /// <summary>
        /// Create a ceiling with grid layout for ACT tiles
        /// Parameters: roomId OR boundaryPoints, ceilingTypeId (optional), heightOffset,
        ///             gridRotation (degrees, optional), gridSpacing (optional, defaults to 2' for 2x2 or 2x4)
        /// </summary>
        [MCPMethod("createCeilingGrid", Category = "FloorCeilingRoof", Description = "Create a ceiling with ACT tile grid layout")]
        public static string CreateCeilingGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roomId = parameters["roomId"]?.Value<int>();
                var boundaryPoints = parameters["boundaryPoints"]?.ToObject<double[][]>();
                var ceilingTypeId = parameters["ceilingTypeId"]?.Value<int>();
                var heightOffset = parameters["heightOffset"]?.Value<double>() ?? 9.0; // 9' default
                var gridRotation = parameters["gridRotation"]?.Value<double>() ?? 0.0;
                var levelId = parameters["levelId"]?.Value<int>();

                CurveLoop boundary = null;
                Level level = null;

                if (roomId.HasValue)
                {
                    var room = doc.GetElement(new ElementId(roomId.Value)) as Room;
                    if (room == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Room not found" });
                    }

                    level = doc.GetElement(room.LevelId) as Level;
                    var options = new SpatialElementBoundaryOptions();
                    var segments = room.GetBoundarySegments(options);
                    if (segments != null && segments.Count > 0)
                    {
                        boundary = new CurveLoop();
                        foreach (var segment in segments[0])
                        {
                            boundary.Append(segment.GetCurve());
                        }
                    }
                }
                else if (boundaryPoints != null && boundaryPoints.Length >= 3)
                {
                    if (!levelId.HasValue)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "levelId required when using boundaryPoints" });
                    }
                    level = doc.GetElement(new ElementId(levelId.Value)) as Level;

                    boundary = new CurveLoop();
                    for (int i = 0; i < boundaryPoints.Length; i++)
                    {
                        var start = new XYZ(boundaryPoints[i][0], boundaryPoints[i][1], 0);
                        var end = new XYZ(boundaryPoints[(i + 1) % boundaryPoints.Length][0], boundaryPoints[(i + 1) % boundaryPoints.Length][1], 0);
                        boundary.Append(Line.CreateBound(start, end));
                    }
                }
                else
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Either roomId or boundaryPoints is required" });
                }

                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Could not determine level" });
                }

                // Get or find ceiling type
                CeilingType ceilingType = null;
                if (ceilingTypeId.HasValue)
                {
                    ceilingType = doc.GetElement(new ElementId(ceilingTypeId.Value)) as CeilingType;
                }
                if (ceilingType == null)
                {
                    ceilingType = new FilteredElementCollector(doc)
                        .OfClass(typeof(CeilingType))
                        .Cast<CeilingType>()
                        .FirstOrDefault(ct => ct.Name.Contains("ACT") || ct.Name.Contains("Acoustic"));
                    if (ceilingType == null)
                    {
                        ceilingType = new FilteredElementCollector(doc)
                            .OfClass(typeof(CeilingType))
                            .Cast<CeilingType>()
                            .FirstOrDefault();
                    }
                }

                if (ceilingType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No ceiling type found" });
                }

                Ceiling ceiling = null;

                using (var trans = new Transaction(doc, "Create Ceiling Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var curveLoops = new List<CurveLoop> { boundary };
                    ceiling = Ceiling.Create(doc, curveLoops, ceilingType.Id, level.Id);

                    if (ceiling != null)
                    {
                        // Set height offset
                        var heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        heightParam?.Set(heightOffset);

                        // Apply grid rotation if specified
                        if (Math.Abs(gridRotation) > 0.001)
                        {
                            // Find the sketch plane and rotate
                            // Note: Grid rotation in Revit is typically done through the ceiling instance
                            // This is a simplified approach - full implementation would need pattern rotation
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingId = ceiling?.Id.Value ?? 0,
                    ceilingType = ceilingType.Name,
                    heightOffset = heightOffset,
                    gridRotation = gridRotation,
                    levelName = level.Name
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place light fixtures in ceiling at specified points or in grid pattern
        /// Parameters: ceilingId, fixtureTypeId (optional), points: [[x,y], ...] OR gridSpacing (for auto layout)
        /// </summary>
        [MCPMethod("placeLightFixture", Category = "FloorCeilingRoof", Description = "Place light fixtures in ceiling at specified points or in a grid pattern")]
        public static string PlaceLightFixture(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ceilingId = parameters["ceilingId"]?.Value<int>();
                var fixtureTypeId = parameters["fixtureTypeId"]?.Value<int>();
                var points = parameters["points"]?.ToObject<double[][]>();
                var gridSpacing = parameters["gridSpacing"]?.Value<double>();

                if (!ceilingId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "ceilingId is required" });
                }

                var ceiling = doc.GetElement(new ElementId(ceilingId.Value)) as Ceiling;
                if (ceiling == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Ceiling not found" });
                }

                // Find light fixture family type
                FamilySymbol fixtureType = null;
                if (fixtureTypeId.HasValue)
                {
                    fixtureType = doc.GetElement(new ElementId(fixtureTypeId.Value)) as FamilySymbol;
                }
                if (fixtureType == null)
                {
                    fixtureType = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_LightingFixtures)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(f => f.Name.Contains("2x4") || f.Name.Contains("Troffer") || f.Name.Contains("Recessed"));

                    if (fixtureType == null)
                    {
                        fixtureType = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_LightingFixtures)
                            .OfClass(typeof(FamilySymbol))
                            .Cast<FamilySymbol>()
                            .FirstOrDefault();
                    }
                }

                if (fixtureType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No light fixture type found. Load a lighting fixture family first." });
                }

                var level = doc.GetElement(ceiling.LevelId) as Level;
                var heightOffset = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 9.0;
                var placedFixtures = new List<long>();

                using (var trans = new Transaction(doc, "Place Light Fixtures"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!fixtureType.IsActive)
                    {
                        fixtureType.Activate();
                    }

                    List<XYZ> placementPoints = new List<XYZ>();

                    if (points != null && points.Length > 0)
                    {
                        // Use specified points
                        foreach (var pt in points)
                        {
                            if (pt.Length >= 2)
                            {
                                placementPoints.Add(new XYZ(pt[0], pt[1], level.Elevation + heightOffset));
                            }
                        }
                    }
                    else if (gridSpacing.HasValue)
                    {
                        // Auto-generate grid points within ceiling bounds
                        var bb = ceiling.get_BoundingBox(null);
                        if (bb != null)
                        {
                            double startX = bb.Min.X + gridSpacing.Value / 2;
                            double startY = bb.Min.Y + gridSpacing.Value / 2;
                            double z = level.Elevation + heightOffset;

                            for (double x = startX; x < bb.Max.X; x += gridSpacing.Value)
                            {
                                for (double y = startY; y < bb.Max.Y; y += gridSpacing.Value)
                                {
                                    placementPoints.Add(new XYZ(x, y, z));
                                }
                            }
                        }
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Either points or gridSpacing is required" });
                    }

                    foreach (var pt in placementPoints)
                    {
                        try
                        {
                            var instance = doc.Create.NewFamilyInstance(pt, fixtureType, ceiling, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                            if (instance != null)
                            {
                                placedFixtures.Add(instance.Id.Value);
                            }
                        }
                        catch
                        {
                            // Skip points that fail (may be outside ceiling boundary)
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    fixtureCount = placedFixtures.Count,
                    fixtureIds = placedFixtures,
                    fixtureType = fixtureType.Name,
                    ceilingId = ceilingId.Value
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag ceiling heights in a view
        /// Parameters: viewId, ceilingIds (optional - if not provided, tags all ceilings in view)
        /// </summary>
        [MCPMethod("tagCeilingHeight", Category = "FloorCeilingRoof", Description = "Tag ceiling heights in a view")]
        public static string TagCeilingHeight(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = parameters["viewId"]?.Value<int>();
                var ceilingIds = parameters["ceilingIds"]?.ToObject<int[]>();

                if (!viewId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                var view = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Find ceiling tag family
                var tagType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_CeilingTags)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (tagType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No ceiling tag type found" });
                }

                // Get ceilings to tag
                List<Ceiling> ceilings;
                if (ceilingIds != null && ceilingIds.Length > 0)
                {
                    ceilings = ceilingIds
                        .Select(id => doc.GetElement(new ElementId(id)) as Ceiling)
                        .Where(c => c != null)
                        .ToList();
                }
                else
                {
                    ceilings = new FilteredElementCollector(doc, view.Id)
                        .OfCategory(BuiltInCategory.OST_Ceilings)
                        .WhereElementIsNotElementType()
                        .Cast<Ceiling>()
                        .ToList();
                }

                var createdTags = new List<object>();

                using (var trans = new Transaction(doc, "Tag Ceiling Heights"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var ceiling in ceilings)
                    {
                        try
                        {
                            var bb = ceiling.get_BoundingBox(view);
                            if (bb != null)
                            {
                                var center = (bb.Min + bb.Max) / 2;
                                var tagLocation = new XYZ(center.X, center.Y, 0);

                                var tag = IndependentTag.Create(doc, view.Id, new Reference(ceiling), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, tagLocation);

                                if (tag != null)
                                {
                                    var height = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
                                    createdTags.Add(new
                                    {
                                        tagId = tag.Id.Value,
                                        ceilingId = ceiling.Id.Value,
                                        height = Math.Round(height, 2)
                                    });
                                }
                            }
                        }
                        catch
                        {
                            // Skip ceilings that fail to tag
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tagCount = createdTags.Count,
                    tags = createdTags
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Roof Enhancement Methods

        /// <summary>
        /// Create a roof by extrusion (shed roof, barrel roof, etc.)
        /// Parameters: levelId, roofTypeId (optional), profilePoints: [[x,z], ...], extrusionLength,
        ///             extrusionStart: [x,y] (optional)
        /// </summary>
        [MCPMethod("createRoofByExtrusion", Category = "FloorCeilingRoof", Description = "Create a roof by extrusion for shed or barrel roof styles")]
        public static string CreateRoofByExtrusion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var levelId = parameters["levelId"]?.Value<int>();
                var roofTypeId = parameters["roofTypeId"]?.Value<int>();
                var profilePoints = parameters["profilePoints"]?.ToObject<double[][]>();
                var extrusionLength = parameters["extrusionLength"]?.Value<double>() ?? 20.0;
                var extrusionStart = parameters["extrusionStart"]?.ToObject<double[]>() ?? new double[] { 0, 0 };

                if (!levelId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                if (profilePoints == null || profilePoints.Length < 2)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "profilePoints with at least 2 points [x,z] required" });
                }

                var level = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                // Get or find roof type
                RoofType roofType = null;
                if (roofTypeId.HasValue)
                {
                    roofType = doc.GetElement(new ElementId(roofTypeId.Value)) as RoofType;
                }
                if (roofType == null)
                {
                    roofType = new FilteredElementCollector(doc)
                        .OfClass(typeof(RoofType))
                        .Cast<RoofType>()
                        .FirstOrDefault();
                }

                if (roofType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No roof type found" });
                }

                ExtrusionRoof roof = null;

                using (var trans = new Transaction(doc, "Create Roof By Extrusion"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create profile curve array (in XZ plane)
                    var profile = new CurveArray();
                    for (int i = 0; i < profilePoints.Length - 1; i++)
                    {
                        var start = new XYZ(profilePoints[i][0], 0, profilePoints[i][1]);
                        var end = new XYZ(profilePoints[i + 1][0], 0, profilePoints[i + 1][1]);
                        profile.Append(Line.CreateBound(start, end));
                    }

                    // Create reference plane for extrusion
                    var normal = XYZ.BasisY; // Extrude along Y
                    var origin = new XYZ(extrusionStart[0], extrusionStart[1], level.Elevation);
                    var plane = Plane.CreateByNormalAndOrigin(normal, origin);
                    var refPlane = doc.Create.NewReferencePlane(origin, origin + XYZ.BasisX, origin + XYZ.BasisZ, doc.ActiveView);

                    roof = doc.Create.NewExtrusionRoof(profile, refPlane, level, roofType, extrusionStart[1], extrusionStart[1] + extrusionLength);

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = roof?.Id.Value ?? 0,
                    roofType = roofType.Name,
                    extrusionLength = extrusionLength,
                    levelName = level.Name
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set slope direction on a footprint roof by modifying edge slopes
        /// Parameters: roofId, tailPoint: [x,y], headPoint: [x,y], slopeValue (rise/run, e.g., 0.25 for 1/4:12)
        /// Note: This sets the slope on the edge closest to the tail point
        /// </summary>
        [MCPMethod("addSlopeArrow", Category = "FloorCeilingRoof", Description = "Set slope direction on a footprint roof by modifying edge slopes")]
        public static string AddSlopeArrow(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofId = parameters["roofId"]?.Value<int>();
                var tailPoint = parameters["tailPoint"]?.ToObject<double[]>();
                var headPoint = parameters["headPoint"]?.ToObject<double[]>();
                var slopeValue = parameters["slopeValue"]?.Value<double>() ?? 0.25; // Default 1/4":12

                if (!roofId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "roofId is required" });
                }

                if (tailPoint == null || headPoint == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "tailPoint and headPoint are required" });
                }

                var roof = doc.GetElement(new ElementId(roofId.Value)) as FootPrintRoof;
                if (roof == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Roof not found or not a footprint roof" });
                }

                var edgesModified = new List<int>();
                var tailXYZ = new XYZ(tailPoint[0], tailPoint[1], 0);

                using (var trans = new Transaction(doc, "Set Roof Slope Direction"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get the footprint boundary
                    var footprint = roof.GetProfiles();
                    if (footprint != null)
                    {
                        int edgeIndex = 0;
                        double minDist = double.MaxValue;
                        ModelCurve closestEdge = null;
                        int closestEdgeIndex = 0;

                        foreach (ModelCurveArray curveArray in footprint)
                        {
                            foreach (ModelCurve mc in curveArray)
                            {
                                var curve = mc.GeometryCurve;
                                var midPoint = curve.Evaluate(0.5, true);
                                var dist = midPoint.DistanceTo(tailXYZ);

                                if (dist < minDist)
                                {
                                    minDist = dist;
                                    closestEdge = mc;
                                    closestEdgeIndex = edgeIndex;
                                }
                                edgeIndex++;
                            }
                        }

                        if (closestEdge != null)
                        {
                            // Set slope on this edge
                            roof.set_DefinesSlope(closestEdge, true);
                            roof.set_SlopeAngle(closestEdge, Math.Atan(slopeValue)); // Convert slope ratio to radians
                            edgesModified.Add(closestEdgeIndex);
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = roofId.Value,
                    edgesModified = edgesModified,
                    slopeValue = slopeValue,
                    slopeRatio = $"{slopeValue * 12}:12",
                    slopeDegrees = Math.Round(Math.Atan(slopeValue) * 180 / Math.PI, 2)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify slope of existing roof edges
        /// Parameters: roofId, edgeIndex (optional - modifies all if not specified), slopeAngle (degrees) OR slope (rise/run)
        /// </summary>
        [MCPMethod("modifyRoofSlope", Category = "FloorCeilingRoof", Description = "Modify slope of existing roof edges")]
        public static string ModifyRoofSlope(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofId = parameters["roofId"]?.Value<int>();
                var edgeIndex = parameters["edgeIndex"]?.Value<int>();
                var slopeAngle = parameters["slopeAngle"]?.Value<double>();
                var slope = parameters["slope"]?.Value<double>();

                if (!roofId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "roofId is required" });
                }

                var roof = doc.GetElement(new ElementId(roofId.Value)) as FootPrintRoof;
                if (roof == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Roof not found or not a footprint roof" });
                }

                double targetSlope;
                if (slopeAngle.HasValue)
                {
                    targetSlope = Math.Tan(slopeAngle.Value * Math.PI / 180);
                }
                else if (slope.HasValue)
                {
                    targetSlope = slope.Value;
                }
                else
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Either slopeAngle or slope is required" });
                }

                var modifiedEdges = new List<int>();

                using (var trans = new Transaction(doc, "Modify Roof Slope"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var footprint = roof.GetProfiles();
                    int edgeCount = 0;
                    foreach (ModelCurveArray profile in footprint)
                    {
                        foreach (ModelCurve curve in profile)
                        {
                            if (!edgeIndex.HasValue || edgeCount == edgeIndex.Value)
                            {
                                // Set the slope
                                roof.set_SlopeAngle(curve, targetSlope);
                                roof.set_DefinesSlope(curve, true);
                                modifiedEdges.Add(edgeCount);
                            }
                            edgeCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = roofId.Value,
                    modifiedEdges = modifiedEdges,
                    newSlope = targetSlope,
                    slopeRatio = $"{Math.Round(targetSlope * 12, 2)}:12"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Boundary Editing Methods (Revit 2022+ API)

        /// <summary>
        /// Edit an existing floor's boundary by replacing it with new points
        /// Parameters: floorId, newBoundaryPoints: [[x,y,z], ...]
        /// Note: This deletes and recreates the floor with the new boundary
        /// </summary>
        [MCPMethod("editFloorBoundary", Category = "FloorCeilingRoof", Description = "Edit an existing floor's boundary by replacing it with new points")]
        public static string EditFloorBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var floorId = parameters["floorId"]?.Value<int>();
                var newBoundaryPoints = parameters["newBoundaryPoints"]?.ToObject<double[][]>();

                if (!floorId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "floorId is required" });
                }

                if (newBoundaryPoints == null || newBoundaryPoints.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "newBoundaryPoints with at least 3 points required" });
                }

                var floor = doc.GetElement(new ElementId(floorId.Value)) as Floor;
                if (floor == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Floor not found" });
                }

                // Get existing floor properties
                var floorTypeId = floor.GetTypeId();
                var levelParam = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                var levelId = levelParam?.AsElementId() ?? ElementId.InvalidElementId;
                var isStructural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
                var heightOffset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;

                Floor newFloor = null;
                long oldId = floor.Id.Value;

                using (var trans = new Transaction(doc, "Edit Floor Boundary"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete the old floor
                    doc.Delete(floor.Id);

                    // Create new boundary curve loop
                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < newBoundaryPoints.Length; i++)
                    {
                        var start = new XYZ(newBoundaryPoints[i][0], newBoundaryPoints[i][1],
                            newBoundaryPoints[i].Length > 2 ? newBoundaryPoints[i][2] : 0);
                        var end = new XYZ(newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][0],
                            newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][1],
                            newBoundaryPoints[(i + 1) % newBoundaryPoints.Length].Length > 2 ? newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][2] : 0);
                        curveLoop.Append(Line.CreateBound(start, end));
                    }

                    var curveLoops = new List<CurveLoop> { curveLoop };

                    // Create the new floor
                    newFloor = Floor.Create(doc, curveLoops, floorTypeId, levelId);

                    // Restore properties
                    if (newFloor != null)
                    {
                        var structParam = newFloor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                        if (structParam != null && !structParam.IsReadOnly)
                        {
                            structParam.Set(isStructural ? 1 : 0);
                        }

                        var offsetParam = newFloor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                        if (offsetParam != null && !offsetParam.IsReadOnly)
                        {
                            offsetParam.Set(heightOffset);
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    oldFloorId = oldId,
                    newFloorId = newFloor?.Id.Value ?? 0,
                    pointCount = newBoundaryPoints.Length,
                    message = "Floor boundary updated successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Edit an existing ceiling's boundary by replacing it with new points
        /// Parameters: ceilingId, newBoundaryPoints: [[x,y], ...]
        /// </summary>
        [MCPMethod("editCeilingBoundary", Category = "FloorCeilingRoof", Description = "Edit an existing ceiling's boundary by replacing it with new points")]
        public static string EditCeilingBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ceilingId = parameters["ceilingId"]?.Value<int>();
                var newBoundaryPoints = parameters["newBoundaryPoints"]?.ToObject<double[][]>();

                if (!ceilingId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "ceilingId is required" });
                }

                if (newBoundaryPoints == null || newBoundaryPoints.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "newBoundaryPoints with at least 3 points required" });
                }

                var ceiling = doc.GetElement(new ElementId(ceilingId.Value)) as Ceiling;
                if (ceiling == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Ceiling not found" });
                }

                // Get existing ceiling properties
                var ceilingTypeId = ceiling.GetTypeId();
                var levelId = ceiling.LevelId;
                var heightOffset = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 9.0;

                Ceiling newCeiling = null;
                long oldId = ceiling.Id.Value;

                using (var trans = new Transaction(doc, "Edit Ceiling Boundary"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete the old ceiling
                    doc.Delete(ceiling.Id);

                    // Create new boundary curve loop
                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < newBoundaryPoints.Length; i++)
                    {
                        var start = new XYZ(newBoundaryPoints[i][0], newBoundaryPoints[i][1], 0);
                        var end = new XYZ(newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][0],
                            newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][1], 0);
                        curveLoop.Append(Line.CreateBound(start, end));
                    }

                    var curveLoops = new List<CurveLoop> { curveLoop };

                    // Create the new ceiling
                    newCeiling = Ceiling.Create(doc, curveLoops, ceilingTypeId, levelId);

                    // Restore height offset
                    if (newCeiling != null)
                    {
                        var heightParam = newCeiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        if (heightParam != null && !heightParam.IsReadOnly)
                        {
                            heightParam.Set(heightOffset);
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    oldCeilingId = oldId,
                    newCeilingId = newCeiling?.Id.Value ?? 0,
                    pointCount = newBoundaryPoints.Length,
                    heightOffset = heightOffset,
                    message = "Ceiling boundary updated successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Edit an existing roof's footprint boundary by replacing it with new points
        /// Parameters: roofId, newBoundaryPoints: [[x,y,z], ...], preserveSlopes (bool, optional)
        /// </summary>
        [MCPMethod("editRoofBoundary", Category = "FloorCeilingRoof", Description = "Edit an existing roof's footprint boundary by replacing it with new points")]
        public static string EditRoofBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofId = parameters["roofId"]?.Value<int>();
                var newBoundaryPoints = parameters["newBoundaryPoints"]?.ToObject<double[][]>();
                var preserveSlopes = parameters["preserveSlopes"]?.Value<bool>() ?? true;
                var defaultSlope = parameters["defaultSlope"]?.Value<double>() ?? 0.25; // 3:12

                if (!roofId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "roofId is required" });
                }

                if (newBoundaryPoints == null || newBoundaryPoints.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "newBoundaryPoints with at least 3 points required" });
                }

                var roof = doc.GetElement(new ElementId(roofId.Value)) as FootPrintRoof;
                if (roof == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Roof not found or not a footprint roof" });
                }

                // Get existing roof properties
                var roofTypeId = roof.GetTypeId();
                var roofType = doc.GetElement(roofTypeId) as RoofType;
                var levelId = roof.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                var level = doc.GetElement(levelId) as Level;
                var overhang = roof.get_Parameter(BuiltInParameter.ROOF_EAVE_CUT_PARAM)?.AsDouble() ?? 1.0;

                // Try to capture existing slopes if preserving
                var existingSlopes = new List<double>();
                if (preserveSlopes)
                {
                    var profiles = roof.GetProfiles();
                    foreach (ModelCurveArray profile in profiles)
                    {
                        foreach (ModelCurve mc in profile)
                        {
                            existingSlopes.Add(roof.get_SlopeAngle(mc));
                        }
                    }
                }

                FootPrintRoof newRoof = null;
                long oldId = roof.Id.Value;

                using (var trans = new Transaction(doc, "Edit Roof Boundary"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete the old roof
                    doc.Delete(roof.Id);

                    // Create new footprint
                    var footprint = new CurveArray();
                    for (int i = 0; i < newBoundaryPoints.Length; i++)
                    {
                        var start = new XYZ(newBoundaryPoints[i][0], newBoundaryPoints[i][1],
                            newBoundaryPoints[i].Length > 2 ? newBoundaryPoints[i][2] : 0);
                        var end = new XYZ(newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][0],
                            newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][1],
                            newBoundaryPoints[(i + 1) % newBoundaryPoints.Length].Length > 2 ? newBoundaryPoints[(i + 1) % newBoundaryPoints.Length][2] : 0);
                        footprint.Append(Line.CreateBound(start, end));
                    }

                    var modelCurves = new ModelCurveArray();
                    newRoof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out modelCurves);

                    // Apply slopes
                    if (newRoof != null && modelCurves != null)
                    {
                        int edgeIndex = 0;
                        foreach (ModelCurve mc in modelCurves)
                        {
                            newRoof.set_DefinesSlope(mc, true);

                            // Use existing slope if available, otherwise default
                            double slopeToUse = (preserveSlopes && edgeIndex < existingSlopes.Count)
                                ? existingSlopes[edgeIndex]
                                : defaultSlope;

                            newRoof.set_SlopeAngle(mc, slopeToUse);
                            edgeIndex++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    oldRoofId = oldId,
                    newRoofId = newRoof?.Id.Value ?? 0,
                    pointCount = newBoundaryPoints.Length,
                    preservedSlopes = preserveSlopes,
                    message = "Roof boundary updated successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the boundary points of an existing floor
        /// Parameters: floorId
        /// Returns: The floor's boundary as an array of points
        /// </summary>
        [MCPMethod("getFloorBoundary", Category = "FloorCeilingRoof", Description = "Get the boundary points of an existing floor")]
        public static string GetFloorBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var floorId = parameters["floorId"]?.Value<int>();
                if (!floorId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "floorId is required" });
                }

                var floor = doc.GetElement(new ElementId(floorId.Value)) as Floor;
                if (floor == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Floor not found" });
                }

                // Get the floor's geometry to extract boundary
                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var geom = floor.get_Geometry(opt);

                var boundaryPoints = new List<object>();
                var boundaries = new List<List<object>>();

                foreach (GeometryObject gObj in geom)
                {
                    if (gObj is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            // Get bottom face (horizontal, facing down)
                            if (face is PlanarFace planarFace)
                            {
                                var normal = planarFace.FaceNormal;
                                if (Math.Abs(normal.Z + 1) < 0.01) // Facing down
                                {
                                    foreach (EdgeArray edgeLoop in planarFace.EdgeLoops)
                                    {
                                        var loopPoints = new List<object>();
                                        foreach (Edge edge in edgeLoop)
                                        {
                                            var curve = edge.AsCurve();
                                            var pt = curve.GetEndPoint(0);
                                            loopPoints.Add(new { x = Math.Round(pt.X, 4), y = Math.Round(pt.Y, 4), z = Math.Round(pt.Z, 4) });
                                        }
                                        if (loopPoints.Count > 0)
                                        {
                                            boundaries.Add(loopPoints);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Use first boundary loop as main boundary
                if (boundaries.Count > 0)
                {
                    boundaryPoints = boundaries[0];
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    floorId = floorId.Value,
                    boundaryCount = boundaries.Count,
                    mainBoundary = boundaryPoints,
                    allBoundaries = boundaries
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the boundary points of an existing ceiling
        /// Parameters: ceilingId
        /// </summary>
        [MCPMethod("getCeilingBoundary", Category = "FloorCeilingRoof", Description = "Get the boundary points of an existing ceiling")]
        public static string GetCeilingBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var ceilingId = parameters["ceilingId"]?.Value<int>();
                if (!ceilingId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "ceilingId is required" });
                }

                var ceiling = doc.GetElement(new ElementId(ceilingId.Value)) as Ceiling;
                if (ceiling == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Ceiling not found" });
                }

                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var geom = ceiling.get_Geometry(opt);

                var boundaryPoints = new List<object>();

                foreach (GeometryObject gObj in geom)
                {
                    if (gObj is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace planarFace)
                            {
                                var normal = planarFace.FaceNormal;
                                // Get top face (facing up)
                                if (Math.Abs(normal.Z - 1) < 0.01)
                                {
                                    foreach (EdgeArray edgeLoop in planarFace.EdgeLoops)
                                    {
                                        foreach (Edge edge in edgeLoop)
                                        {
                                            var curve = edge.AsCurve();
                                            var pt = curve.GetEndPoint(0);
                                            boundaryPoints.Add(new { x = Math.Round(pt.X, 4), y = Math.Round(pt.Y, 4), z = Math.Round(pt.Z, 4) });
                                        }
                                        break; // Only first loop
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    ceilingId = ceilingId.Value,
                    pointCount = boundaryPoints.Count,
                    boundary = boundaryPoints
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the footprint boundary of an existing roof
        /// Parameters: roofId
        /// </summary>
        [MCPMethod("getRoofBoundary", Category = "FloorCeilingRoof", Description = "Get the footprint boundary points of an existing roof")]
        public static string GetRoofBoundary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roofId = parameters["roofId"]?.Value<int>();
                if (!roofId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "roofId is required" });
                }

                var roof = doc.GetElement(new ElementId(roofId.Value)) as FootPrintRoof;
                if (roof == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Roof not found or not a footprint roof" });
                }

                var boundaryPoints = new List<object>();
                var edgeInfo = new List<object>();

                var profiles = roof.GetProfiles();
                foreach (ModelCurveArray profile in profiles)
                {
                    foreach (ModelCurve mc in profile)
                    {
                        var curve = mc.GeometryCurve;
                        var startPt = curve.GetEndPoint(0);
                        var endPt = curve.GetEndPoint(1);

                        var definesSlope = roof.get_DefinesSlope(mc);
                        var slopeAngle = roof.get_SlopeAngle(mc);
                        var overhang = roof.get_Overhang(mc);

                        boundaryPoints.Add(new { x = Math.Round(startPt.X, 4), y = Math.Round(startPt.Y, 4), z = Math.Round(startPt.Z, 4) });

                        edgeInfo.Add(new
                        {
                            startPoint = new { x = Math.Round(startPt.X, 4), y = Math.Round(startPt.Y, 4), z = Math.Round(startPt.Z, 4) },
                            endPoint = new { x = Math.Round(endPt.X, 4), y = Math.Round(endPt.Y, 4), z = Math.Round(endPt.Z, 4) },
                            definesSlope,
                            slopeAngle = Math.Round(slopeAngle, 4),
                            slopeRatio = $"{Math.Round(slopeAngle * 12, 2)}:12",
                            overhang = Math.Round(overhang, 4)
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = roofId.Value,
                    pointCount = boundaryPoints.Count,
                    boundary = boundaryPoints,
                    edges = edgeInfo
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Attach walls to a roof (join geometry so walls are cut by roof)
        /// Parameters:
        /// - wallIds: array of wall element IDs to attach
        /// - roofId: ID of the roof to attach to
        /// </summary>
        [MCPMethod("attachWallsToRoof", Category = "FloorCeilingRoof", Description = "Attach walls to a roof so walls are cut by the roof geometry")]
        public static string AttachWallsToRoof(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse roof ID
                if (parameters["roofId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "roofId is required"
                    });
                }

                var roofId = new ElementId(int.Parse(parameters["roofId"].ToString()));
                var roof = doc.GetElement(roofId);
                if (roof == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid roof ID"
                    });
                }

                // Parse wall IDs
                if (parameters["wallIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "wallIds is required"
                    });
                }

                var wallIdInts = parameters["wallIds"].ToObject<int[]>();
                var attachedWalls = new List<int>();
                var failedWalls = new List<object>();

                using (var trans = new Transaction(doc, "Attach Walls to Roof"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var wallIdInt in wallIdInts)
                    {
                        try
                        {
                            var wallId = new ElementId(wallIdInt);
                            var wall = doc.GetElement(wallId) as Wall;

                            if (wall == null)
                            {
                                failedWalls.Add(new { wallId = wallIdInt, reason = "Wall not found" });
                                continue;
                            }

                            // Use Revit 2026 Wall.AddAttachment API
                            wall.AddAttachment(roofId, AttachmentLocation.Top);
                            attachedWalls.Add(wallIdInt);
                        }
                        catch (Exception ex)
                        {
                            failedWalls.Add(new { wallId = wallIdInt, reason = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofId = roofId.Value,
                    attachedCount = attachedWalls.Count,
                    attachedWalls = attachedWalls,
                    failedCount = failedWalls.Count,
                    failedWalls = failedWalls
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
