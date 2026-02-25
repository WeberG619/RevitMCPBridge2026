using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Structural Elements in Revit
    /// Handles beams, columns, foundations, structural framing, connections, and analytical models
    /// </summary>
    public static class StructuralMethods
    {
        #region Structural Columns

        /// <summary>
        /// Places a structural column at a specified point
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing location, columnTypeId, levelId, height, rotation</param>
        /// <returns>JSON response with success status and column element ID</returns>
        public static string PlaceStructuralColumn(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["location"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required (provide x, y, z coordinates)"
                    });
                }

                if (parameters["columnTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "columnTypeId is required"
                    });
                }

                if (parameters["baseLevelId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "baseLevelId is required"
                    });
                }

                // Parse location
                var location = parameters["location"];
                double x = location["x"].ToObject<double>();
                double y = location["y"].ToObject<double>();
                double z = location["z"].ToObject<double>();
                XYZ point = new XYZ(x, y, z);

                // Get column type
                int typeIdInt = parameters["columnTypeId"].ToObject<int>();
                ElementId columnTypeId = new ElementId(typeIdInt);
                FamilySymbol columnType = doc.GetElement(columnTypeId) as FamilySymbol;

                if (columnType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Column type with ID {typeIdInt} not found"
                    });
                }

                // Get base level
                int baseLevelIdInt = parameters["baseLevelId"].ToObject<int>();
                ElementId baseLevelId = new ElementId(baseLevelIdInt);
                Level baseLevel = doc.GetElement(baseLevelId) as Level;

                if (baseLevel == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Base level with ID {baseLevelIdInt} not found"
                    });
                }

                // Get top level (optional - can use height instead)
                Level topLevel = null;
                if (parameters["topLevelId"] != null)
                {
                    int topLevelIdInt = parameters["topLevelId"].ToObject<int>();
                    ElementId topLevelId = new ElementId(topLevelIdInt);
                    topLevel = doc.GetElement(topLevelId) as Level;
                }

                using (var trans = new Transaction(doc, "Place Structural Column"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the column type if not active
                    if (!columnType.IsActive)
                    {
                        columnType.Activate();
                    }

                    // Create the column
                    FamilyInstance column = doc.Create.NewFamilyInstance(
                        point,
                        columnType,
                        baseLevel,
                        StructuralType.Column);

                    // Set top level if provided
                    if (topLevel != null)
                    {
                        Parameter topLevelParam = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                        if (topLevelParam != null && !topLevelParam.IsReadOnly)
                        {
                            topLevelParam.Set(topLevel.Id);
                        }
                    }
                    // Otherwise set height if provided
                    else if (parameters["height"] != null)
                    {
                        double height = parameters["height"].ToObject<double>();
                        Parameter topOffsetParam = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                        if (topOffsetParam != null && !topOffsetParam.IsReadOnly)
                        {
                            topOffsetParam.Set(height);
                        }
                    }

                    // Set rotation if provided
                    if (parameters["rotation"] != null)
                    {
                        double rotation = parameters["rotation"].ToObject<double>();
                        XYZ axis = XYZ.BasisZ;
                        ElementTransformUtils.RotateElement(doc, column.Id, Line.CreateBound(point, point + axis), rotation);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        columnId = column.Id.Value,
                        columnType = columnType.Name,
                        family = columnType.FamilyName,
                        baseLevel = baseLevel.Name,
                        topLevel = topLevel?.Name,
                        location = new { x, y, z },
                        message = $"Structural column '{columnType.Name}' placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets detailed information about a structural column
        /// </summary>
        public static string GetStructuralColumnInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["columnId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "columnId is required" });
                }

                int columnIdInt = parameters["columnId"].ToObject<int>();
                ElementId columnId = new ElementId(columnIdInt);
                FamilyInstance column = doc.GetElement(columnId) as FamilyInstance;

                if (column == null || column.StructuralType != StructuralType.Column)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Structural column not found" });
                }

                var location = (column.Location as LocationPoint)?.Point;
                var baseLevel = doc.GetElement(column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId()) as Level;
                var topLevel = doc.GetElement(column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId()) as Level;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    columnId = columnIdInt,
                    type = column.Symbol.Name,
                    family = column.Symbol.FamilyName,
                    location = location != null ? new { x = location.X, y = location.Y, z = location.Z } : null,
                    baseLevel = baseLevel?.Name,
                    baseLevelId = baseLevel?.Id.Value,
                    topLevel = topLevel?.Name,
                    topLevelId = topLevel?.Id.Value,
                    baseLevelOffset = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0,
                    topLevelOffset = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modifies properties of a structural column
        /// </summary>
        public static string ModifyStructuralColumn(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["columnId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "columnId is required" });
                }

                int columnIdInt = parameters["columnId"].ToObject<int>();
                FamilyInstance column = doc.GetElement(new ElementId(columnIdInt)) as FamilyInstance;

                if (column == null || column.StructuralType != StructuralType.Column)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Structural column not found" });
                }

                using (var trans = new Transaction(doc, "Modify Structural Column"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Modify base level
                    if (parameters["baseLevelId"] != null)
                    {
                        int baseLevelIdInt = parameters["baseLevelId"].ToObject<int>();
                        Level newBaseLevel = doc.GetElement(new ElementId(baseLevelIdInt)) as Level;

                        if (newBaseLevel != null)
                        {
                            Parameter baseLevelParam = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                            if (baseLevelParam != null && !baseLevelParam.IsReadOnly)
                            {
                                baseLevelParam.Set(newBaseLevel.Id);
                            }
                        }
                    }

                    // Modify top level
                    if (parameters["topLevelId"] != null)
                    {
                        int topLevelIdInt = parameters["topLevelId"].ToObject<int>();
                        Level newTopLevel = doc.GetElement(new ElementId(topLevelIdInt)) as Level;

                        if (newTopLevel != null)
                        {
                            Parameter topLevelParam = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                            if (topLevelParam != null && !topLevelParam.IsReadOnly)
                            {
                                topLevelParam.Set(newTopLevel.Id);
                            }
                        }
                    }

                    // Modify base offset
                    if (parameters["baseOffset"] != null)
                    {
                        double baseOffset = parameters["baseOffset"].ToObject<double>();
                        Parameter baseOffsetParam = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                        if (baseOffsetParam != null && !baseOffsetParam.IsReadOnly)
                        {
                            baseOffsetParam.Set(baseOffset);
                        }
                    }

                    // Modify top offset
                    if (parameters["topOffset"] != null)
                    {
                        double topOffset = parameters["topOffset"].ToObject<double>();
                        Parameter topOffsetParam = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                        if (topOffsetParam != null && !topOffsetParam.IsReadOnly)
                        {
                            topOffsetParam.Set(topOffset);
                        }
                    }

                    // Modify location
                    if (parameters["location"] != null)
                    {
                        var location = parameters["location"];
                        double x = location["x"].ToObject<double>();
                        double y = location["y"].ToObject<double>();
                        double z = location["z"].ToObject<double>();
                        XYZ newPoint = new XYZ(x, y, z);

                        LocationPoint locPoint = column.Location as LocationPoint;
                        if (locPoint != null)
                        {
                            locPoint.Point = newPoint;
                        }
                    }

                    // Modify rotation
                    if (parameters["rotation"] != null)
                    {
                        double rotation = parameters["rotation"].ToObject<double>();
                        LocationPoint locPoint = column.Location as LocationPoint;

                        if (locPoint != null)
                        {
                            XYZ point = locPoint.Point;
                            XYZ axis = XYZ.BasisZ;
                            Line rotationAxis = Line.CreateBound(point, point + axis);
                            locPoint.Rotate(rotationAxis, rotation);
                        }
                    }

                    // Change column type if specified
                    if (parameters["columnTypeId"] != null)
                    {
                        int newTypeIdInt = parameters["columnTypeId"].ToObject<int>();
                        FamilySymbol newType = doc.GetElement(new ElementId(newTypeIdInt)) as FamilySymbol;

                        if (newType != null)
                        {
                            if (!newType.IsActive)
                            {
                                newType.Activate();
                            }
                            column.Symbol = newType;
                        }
                    }

                    trans.Commit();

                    var resultLocation = (column.Location as LocationPoint)?.Point;
                    var baseLevel = doc.GetElement(column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId()) as Level;
                    var topLevel = doc.GetElement(column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId()) as Level;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        columnId = columnIdInt,
                        type = column.Symbol.Name,
                        location = resultLocation != null ? new { x = resultLocation.X, y = resultLocation.Y, z = resultLocation.Z } : null,
                        baseLevel = baseLevel?.Name,
                        topLevel = topLevel?.Name,
                        baseOffset = column.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0,
                        topOffset = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0,
                        message = "Structural column modified successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all structural column types in the project
        /// </summary>
        public static string GetStructuralColumnTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var columnTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Select(symbol => new
                    {
                        typeId = symbol.Id.Value,
                        typeName = symbol.Name,
                        family = symbol.FamilyName,
                        isActive = symbol.IsActive
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    columnTypes = columnTypes,
                    count = columnTypes.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Structural Beams

        /// <summary>
        /// Creates a structural beam between two points
        /// </summary>
        public static string CreateStructuralBeam(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "startPoint and endPoint are required" });
                }

                if (parameters["beamTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "beamTypeId is required" });
                }

                if (parameters["levelId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(start["x"].ToObject<double>(), start["y"].ToObject<double>(), start["z"].ToObject<double>());

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(end["x"].ToObject<double>(), end["y"].ToObject<double>(), end["z"].ToObject<double>());

                // Get beam type
                int typeIdInt = parameters["beamTypeId"].ToObject<int>();
                FamilySymbol beamType = doc.GetElement(new ElementId(typeIdInt)) as FamilySymbol;

                if (beamType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Beam type not found" });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                Level level = doc.GetElement(new ElementId(levelIdInt)) as Level;

                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                using (var trans = new Transaction(doc, "Create Structural Beam"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!beamType.IsActive)
                    {
                        beamType.Activate();
                    }

                    // Create curve for beam
                    Line beamLine = Line.CreateBound(startPoint, endPoint);

                    // Create beam
                    FamilyInstance beam = doc.Create.NewFamilyInstance(beamLine, beamType, level, StructuralType.Beam);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        beamId = beam.Id.Value,
                        beamType = beamType.Name,
                        family = beamType.FamilyName,
                        level = level.Name,
                        length = beamLine.Length,
                        message = $"Structural beam '{beamType.Name}' created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets detailed information about a structural beam
        /// </summary>
        public static string GetStructuralBeamInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["beamId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "beamId is required" });
                }

                int beamIdInt = parameters["beamId"].ToObject<int>();
                FamilyInstance beam = doc.GetElement(new ElementId(beamIdInt)) as FamilyInstance;

                if (beam == null || beam.StructuralType != StructuralType.Beam)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Structural beam not found" });
                }

                var locationCurve = beam.Location as LocationCurve;
                Line beamLine = locationCurve?.Curve as Line;

                var level = doc.GetElement(beam.LevelId) as Level;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    beamId = beamIdInt,
                    type = beam.Symbol.Name,
                    family = beam.Symbol.FamilyName,
                    startPoint = beamLine != null ? new { x = beamLine.GetEndPoint(0).X, y = beamLine.GetEndPoint(0).Y, z = beamLine.GetEndPoint(0).Z } : null,
                    endPoint = beamLine != null ? new { x = beamLine.GetEndPoint(1).X, y = beamLine.GetEndPoint(1).Y, z = beamLine.GetEndPoint(1).Z } : null,
                    length = beamLine?.Length ?? 0,
                    level = level?.Name,
                    levelId = level?.Id.Value
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modifies properties of a structural beam
        /// </summary>
        public static string ModifyStructuralBeam(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["beamId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "beamId is required" });
                }

                int beamIdInt = parameters["beamId"].ToObject<int>();
                FamilyInstance beam = doc.GetElement(new ElementId(beamIdInt)) as FamilyInstance;

                if (beam == null || beam.StructuralType != StructuralType.Beam)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Structural beam not found" });
                }

                using (var trans = new Transaction(doc, "Modify Structural Beam"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Modify beam curve (start and end points)
                    if (parameters["startPoint"] != null && parameters["endPoint"] != null)
                    {
                        var start = parameters["startPoint"];
                        XYZ startPoint = new XYZ(start["x"].ToObject<double>(), start["y"].ToObject<double>(), start["z"].ToObject<double>());

                        var end = parameters["endPoint"];
                        XYZ endPoint = new XYZ(end["x"].ToObject<double>(), end["y"].ToObject<double>(), end["z"].ToObject<double>());

                        LocationCurve locationCurve = beam.Location as LocationCurve;
                        if (locationCurve != null)
                        {
                            Line newLine = Line.CreateBound(startPoint, endPoint);
                            locationCurve.Curve = newLine;
                        }
                    }

                    // Modify start offset
                    if (parameters["startOffset"] != null)
                    {
                        double startOffset = parameters["startOffset"].ToObject<double>();
                        Parameter startOffsetParam = beam.get_Parameter(BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION);
                        if (startOffsetParam != null && !startOffsetParam.IsReadOnly)
                        {
                            startOffsetParam.Set(startOffset);
                        }
                    }

                    // Modify end offset
                    if (parameters["endOffset"] != null)
                    {
                        double endOffset = parameters["endOffset"].ToObject<double>();
                        Parameter endOffsetParam = beam.get_Parameter(BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION);
                        if (endOffsetParam != null && !endOffsetParam.IsReadOnly)
                        {
                            endOffsetParam.Set(endOffset);
                        }
                    }

                    // Change beam type if specified
                    if (parameters["beamTypeId"] != null)
                    {
                        int newTypeIdInt = parameters["beamTypeId"].ToObject<int>();
                        FamilySymbol newType = doc.GetElement(new ElementId(newTypeIdInt)) as FamilySymbol;

                        if (newType != null)
                        {
                            if (!newType.IsActive)
                            {
                                newType.Activate();
                            }
                            beam.Symbol = newType;
                        }
                    }

                    trans.Commit();

                    var locationCurveResult = beam.Location as LocationCurve;
                    Line beamLine = locationCurveResult?.Curve as Line;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        beamId = beamIdInt,
                        type = beam.Symbol.Name,
                        startPoint = beamLine != null ? new { x = beamLine.GetEndPoint(0).X, y = beamLine.GetEndPoint(0).Y, z = beamLine.GetEndPoint(0).Z } : null,
                        endPoint = beamLine != null ? new { x = beamLine.GetEndPoint(1).X, y = beamLine.GetEndPoint(1).Y, z = beamLine.GetEndPoint(1).Z } : null,
                        length = beamLine?.Length ?? 0,
                        message = "Structural beam modified successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all structural beam types in the project
        /// </summary>
        public static string GetStructuralBeamTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var beamTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(symbol => symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBased)
                    .Select(symbol => new
                    {
                        typeId = symbol.Id.Value,
                        typeName = symbol.Name,
                        family = symbol.FamilyName,
                        isActive = symbol.IsActive
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    beamTypes = beamTypes,
                    count = beamTypes.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Structural Foundations

        /// <summary>
        /// Creates a structural foundation at a specified location
        /// </summary>
        public static string CreateFoundation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["location"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "location is required (provide x, y, z coordinates)" });
                }

                if (parameters["foundationTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "foundationTypeId is required" });
                }

                if (parameters["levelId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                // Parse location
                var location = parameters["location"];
                double x = location["x"].ToObject<double>();
                double y = location["y"].ToObject<double>();
                double z = location["z"].ToObject<double>();
                XYZ point = new XYZ(x, y, z);

                // Get foundation type
                int typeIdInt = parameters["foundationTypeId"].ToObject<int>();
                FamilySymbol foundationType = doc.GetElement(new ElementId(typeIdInt)) as FamilySymbol;

                if (foundationType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Foundation type not found" });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                Level level = doc.GetElement(new ElementId(levelIdInt)) as Level;

                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                using (var trans = new Transaction(doc, "Create Foundation"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!foundationType.IsActive)
                    {
                        foundationType.Activate();
                    }

                    // Create isolated foundation
                    FamilyInstance foundation = doc.Create.NewFamilyInstance(
                        point,
                        foundationType,
                        level,
                        StructuralType.Footing);

                    // Set rotation if provided
                    if (parameters["rotation"] != null)
                    {
                        double rotation = parameters["rotation"].ToObject<double>();
                        XYZ axis = XYZ.BasisZ;
                        ElementTransformUtils.RotateElement(doc, foundation.Id, Line.CreateBound(point, point + axis), rotation);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        foundationId = foundation.Id.Value,
                        foundationType = foundationType.Name,
                        family = foundationType.FamilyName,
                        level = level.Name,
                        location = new { x, y, z },
                        message = $"Foundation '{foundationType.Name}' created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets information about a foundation element
        /// </summary>
        public static string GetFoundationInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["foundationId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "foundationId is required" });
                }

                int foundationIdInt = parameters["foundationId"].ToObject<int>();
                FamilyInstance foundation = doc.GetElement(new ElementId(foundationIdInt)) as FamilyInstance;

                if (foundation == null || foundation.StructuralType != StructuralType.Footing)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Foundation not found" });
                }

                var location = (foundation.Location as LocationPoint)?.Point;
                var level = doc.GetElement(foundation.LevelId) as Level;

                // Get dimensions if available
                var width = foundation.LookupParameter("Width")?.AsDouble() ??
                           foundation.LookupParameter("b")?.AsDouble() ?? 0;
                var length = foundation.LookupParameter("Length")?.AsDouble() ??
                            foundation.LookupParameter("h")?.AsDouble() ?? 0;
                var thickness = foundation.LookupParameter("Thickness")?.AsDouble() ??
                               foundation.LookupParameter("t")?.AsDouble() ?? 0;

                // Get structural usage type
                string structuralUsage = foundation.StructuralType.ToString();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    foundationId = foundationIdInt,
                    type = foundation.Symbol.Name,
                    family = foundation.Symbol.FamilyName,
                    location = location != null ? new { x = location.X, y = location.Y, z = location.Z } : null,
                    level = level?.Name,
                    levelId = level?.Id.Value,
                    width = width,
                    length = length,
                    thickness = thickness,
                    structuralUsage = structuralUsage
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all foundation types in the project
        /// </summary>
        public static string GetFoundationTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var foundationTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Select(symbol => new
                    {
                        typeId = symbol.Id.Value,
                        typeName = symbol.Name,
                        family = symbol.FamilyName,
                        isActive = symbol.IsActive
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    foundationTypes = foundationTypes,
                    count = foundationTypes.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Structural Framing

        /// <summary>
        /// Places structural framing (bracing, trusses, etc.)
        /// </summary>
        public static string PlaceStructuralFraming(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "startPoint and endPoint are required" });
                }

                if (parameters["framingTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "framingTypeId is required" });
                }

                if (parameters["levelId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "levelId is required" });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(start["x"].ToObject<double>(), start["y"].ToObject<double>(), start["z"].ToObject<double>());

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(end["x"].ToObject<double>(), end["y"].ToObject<double>(), end["z"].ToObject<double>());

                // Get framing type
                int typeIdInt = parameters["framingTypeId"].ToObject<int>();
                FamilySymbol framingType = doc.GetElement(new ElementId(typeIdInt)) as FamilySymbol;

                if (framingType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Framing type not found" });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                Level level = doc.GetElement(new ElementId(levelIdInt)) as Level;

                if (level == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Level not found" });
                }

                // Parse structural usage (default to Brace)
                StructuralType structuralUsage = StructuralType.Brace;
                if (parameters["structuralUsage"] != null)
                {
                    string usageStr = parameters["structuralUsage"].ToString().ToLower();
                    switch (usageStr)
                    {
                        case "beam":
                            structuralUsage = StructuralType.Beam;
                            break;
                        case "brace":
                            structuralUsage = StructuralType.Brace;
                            break;
                        case "column":
                            structuralUsage = StructuralType.Column;
                            break;
                        default:
                            structuralUsage = StructuralType.UnknownFraming;
                            break;
                    }
                }

                using (var trans = new Transaction(doc, "Place Structural Framing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (!framingType.IsActive)
                    {
                        framingType.Activate();
                    }

                    // Create curve for framing
                    Line framingLine = Line.CreateBound(startPoint, endPoint);

                    // Create framing element
                    FamilyInstance framing = doc.Create.NewFamilyInstance(framingLine, framingType, level, structuralUsage);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        framingId = framing.Id.Value,
                        framingType = framingType.Name,
                        family = framingType.FamilyName,
                        level = level.Name,
                        structuralUsage = structuralUsage.ToString(),
                        length = framingLine.Length,
                        message = $"Structural framing '{framingType.Name}' placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all structural framing elements in a view
        /// </summary>
        public static string GetStructuralFramingInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                View view = doc.GetElement(new ElementId(viewIdInt)) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var framingElements = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Select(framing =>
                    {
                        var locationCurve = framing.Location as LocationCurve;
                        Line framingLine = locationCurve?.Curve as Line;

                        return new
                        {
                            framingId = framing.Id.Value,
                            type = framing.Symbol.Name,
                            family = framing.Symbol.FamilyName,
                            structuralUsage = framing.StructuralType.ToString(),
                            startPoint = framingLine != null ? new { x = framingLine.GetEndPoint(0).X, y = framingLine.GetEndPoint(0).Y, z = framingLine.GetEndPoint(0).Z } : null,
                            endPoint = framingLine != null ? new { x = framingLine.GetEndPoint(1).X, y = framingLine.GetEndPoint(1).Y, z = framingLine.GetEndPoint(1).Z } : null,
                            length = framingLine?.Length ?? 0
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewName = view.Name,
                    framingElements = framingElements,
                    count = framingElements.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Structural Connections

        /// <summary>
        /// Creates a structural connection between elements
        /// </summary>
        public static string CreateStructuralConnection(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // NOTE: Structural connections in Revit are complex and typically created through
                // Steel Connections extension or StructuralConnectionHandler API which requires
                // specific connection types and configurations. This implementation provides
                // a simplified interface.

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required (elements to connect)"
                    });
                }

                // Parse element IDs
                var elementIdsArray = parameters["elementIds"] as JArray;
                if (elementIdsArray == null || elementIdsArray.Count < 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least 2 element IDs required for connection"
                    });
                }

                List<ElementId> elementIds = new List<ElementId>();
                foreach (var id in elementIdsArray)
                {
                    elementIds.Add(new ElementId(id.ToObject<int>()));
                }

                // Verify elements exist and are structural
                List<Element> elements = new List<Element>();
                foreach (var id in elementIds)
                {
                    Element elem = doc.GetElement(id);
                    if (elem == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Element with ID {id.Value} not found"
                        });
                    }
                    elements.Add(elem);
                }

                // API LIMITATION: Full structural connection creation requires
                // StructuralConnectionHandler and specific connection type families
                // This would require significant additional setup and configuration

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API Limitation: Structural connections require StructuralConnectionHandler API and connection type families. This is a complex operation that typically requires the Steel Connections extension. Consider using Revit UI for connection creation or implementing with specific connection type families loaded in the project.",
                    apiNote = "StructuralConnectionHandler API is available in Revit 2026 but requires extensive setup",
                    elementIds = elementIds.Select(id => id.Value).ToList(),
                    elementCount = elements.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets information about a structural connection
        /// </summary>
        public static string GetConnectionInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["connectionId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "connectionId is required" });
                }

                int connectionIdInt = parameters["connectionId"].ToObject<int>();
                Element connection = doc.GetElement(new ElementId(connectionIdInt));

                if (connection == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Connection element not found" });
                }

                // Check if it's a structural connection (typically FamilyInstance from Steel Connections)
                FamilyInstance connectionInstance = connection as FamilyInstance;

                if (connectionInstance != null)
                {
                    // Get basic connection information
                    var location = (connectionInstance.Location as LocationPoint)?.Point;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        connectionId = connectionIdInt,
                        type = connectionInstance.Symbol?.Name ?? "Unknown",
                        family = connectionInstance.Symbol?.FamilyName ?? "Unknown",
                        category = connectionInstance.Category?.Name ?? "Unknown",
                        location = location != null ? new { x = location.X, y = location.Y, z = location.Z } : null,
                        note = "Connection details depend on specific Steel Connections extension configuration"
                    });
                }

                // Generic element info for non-standard connections
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    connectionId = connectionIdInt,
                    elementName = connection.Name,
                    category = connection.Category?.Name ?? "Unknown",
                    apiNote = "Full connection details require Steel Connections API or StructuralConnectionHandler"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Analytical Model

        /// <summary>
        /// Gets the analytical model for a structural element
        /// </summary>
        public static string GetAnalyticalModel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                Element element = doc.GetElement(new ElementId(elementIdInt));

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                // In Revit 2026, analytical model access has changed
                // Try to get associated analytical element
                var analyticalManager = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);

                if (analyticalManager == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Analytical model not available for this project (may need to enable analytical model)"
                    });
                }

                ElementId analyticalId = analyticalManager.GetAssociatedElementId(element.Id);

                if (analyticalId == null || analyticalId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hasAnalyticalModel = false,
                        elementId = elementIdInt,
                        message = "This element does not have an analytical model"
                    });
                }

                AnalyticalElement analyticalElement = doc.GetElement(analyticalId) as AnalyticalElement;

                if (analyticalElement == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hasAnalyticalModel = false,
                        elementId = elementIdInt
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    hasAnalyticalModel = true,
                    elementId = elementIdInt,
                    analyticalElementId = analyticalId.Value,
                    analyticalCategory = analyticalElement.Category?.Name ?? "Unknown",
                    message = "Analytical model found (detailed properties require specific analytical element API)"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Sets analytical model properties for a structural element
        /// </summary>
        public static string SetAnalyticalProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                Element element = doc.GetElement(new ElementId(elementIdInt));

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                // Get analytical manager
                var analyticalManager = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);

                if (analyticalManager == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Analytical model not available for this project"
                    });
                }

                ElementId analyticalId = analyticalManager.GetAssociatedElementId(element.Id);

                if (analyticalId == null || analyticalId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "This element does not have an analytical model"
                    });
                }

                // API LIMITATION: Setting analytical properties in Revit 2026 requires
                // specific analytical element APIs and depends on element type
                // This is a complex operation best handled through Revit UI or dedicated analytical tools

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API Limitation: Setting analytical properties requires complex element-specific APIs. Analytical model properties (releases, offsets, supports) are best configured through Revit UI or dedicated structural analysis software. The Analytical Model API in Revit 2026 has been restructured and requires element-type-specific implementation.",
                    elementId = elementIdInt,
                    analyticalElementId = analyticalId.Value,
                    apiNote = "Consider using Robot Structural Analysis or other analysis tools for detailed analytical model configuration"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Rebar and Reinforcement

        /// <summary>
        /// Creates rebar in a structural element
        /// </summary>
        public static string CreateRebar(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["hostElementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "hostElementId is required" });
                }

                if (parameters["rebarBarTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "rebarBarTypeId is required" });
                }

                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "startPoint and endPoint are required for rebar curve" });
                }

                // Get host element
                int hostIdInt = parameters["hostElementId"].ToObject<int>();
                Element host = doc.GetElement(new ElementId(hostIdInt));

                if (host == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Host element not found" });
                }

                // Get rebar bar type
                int rebarTypeIdInt = parameters["rebarBarTypeId"].ToObject<int>();
                RebarBarType rebarBarType = doc.GetElement(new ElementId(rebarTypeIdInt)) as RebarBarType;

                if (rebarBarType == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Rebar bar type not found" });
                }

                // Parse start and end points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(start["x"].ToObject<double>(), start["y"].ToObject<double>(), start["z"].ToObject<double>());

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(end["x"].ToObject<double>(), end["y"].ToObject<double>(), end["z"].ToObject<double>());

                // Create curve for rebar
                Line rebarCurve = Line.CreateBound(startPoint, endPoint);

                // Get normal vector (default to Z if not provided)
                XYZ normal = XYZ.BasisZ;
                if (parameters["normal"] != null)
                {
                    var norm = parameters["normal"];
                    normal = new XYZ(norm["x"].ToObject<double>(), norm["y"].ToObject<double>(), norm["z"].ToObject<double>());
                }

                using (var trans = new Transaction(doc, "Create Rebar"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create rebar from curves
                    List<Curve> curves = new List<Curve> { rebarCurve };

#if REVIT2025
                    // Revit 2025 API - use RebarHookOrientation enum
                    Rebar rebar = Rebar.CreateFromCurves(
                        doc,
                        RebarStyle.Standard,
                        rebarBarType,
                        null, // startHook
                        null, // endHook
                        host,
                        normal,
                        curves,
                        RebarHookOrientation.Left, // start hook orientation
                        RebarHookOrientation.Left, // end hook orientation
                        true, // use existing shape if possible
                        true  // create new shape if needed
                    );
#else
                    // Revit 2026+ API - use BarTerminationsData
                    BarTerminationsData barTerminations = new BarTerminationsData(doc);

                    Rebar rebar = Rebar.CreateFromCurves(
                        doc,
                        RebarStyle.Standard,
                        rebarBarType,
                        host,
                        normal,
                        curves,
                        barTerminations,
                        true, // use existing shape if possible
                        true  // create new shape if needed
                    );
#endif

                    if (rebar == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "Failed to create rebar" });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        rebarId = rebar.Id.Value,
                        rebarType = rebarBarType.Name,
                        hostElement = host.Id.Value,
                        quantity = rebar.Quantity,
                        totalLength = rebar.TotalLength,
                        message = "Rebar created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets rebar information from a structural element
        /// </summary>
        public static string GetRebarInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["rebarId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "rebarId is required" });
                }

                int rebarIdInt = parameters["rebarId"].ToObject<int>();
                Rebar rebar = doc.GetElement(new ElementId(rebarIdInt)) as Rebar;

                if (rebar == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Rebar element not found" });
                }

                // Get rebar properties
                RebarBarType barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                Element host = doc.GetElement(rebar.GetHostId());

                // Get rebar shape
                RebarShape rebarShape = null;
                if (rebar.get_Parameter(BuiltInParameter.REBAR_SHAPE) != null)
                {
                    ElementId shapeId = rebar.get_Parameter(BuiltInParameter.REBAR_SHAPE).AsElementId();
                    if (shapeId != null && shapeId != ElementId.InvalidElementId)
                    {
                        rebarShape = doc.GetElement(shapeId) as RebarShape;
                    }
                }

                // Get cover distance (if available)
                double coverDistance = 0;
                Parameter coverParam = rebar.LookupParameter("Cover");
                if (coverParam != null)
                {
                    coverDistance = coverParam.AsDouble();
                }

                // Get bar diameter from parameters (API changed in Revit 2026)
                double barDiameter = 0;
                Parameter diameterParam = barType?.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
                if (diameterParam != null)
                {
                    barDiameter = diameterParam.AsDouble();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    rebarId = rebarIdInt,
                    barType = barType?.Name ?? "Unknown",
                    barDiameter = barDiameter,
                    hostElementId = host?.Id.Value,
                    hostElementName = host?.Name ?? "Unknown",
                    quantity = rebar.Quantity,
                    totalLength = rebar.TotalLength,
                    numberOfBarPositions = rebar.NumberOfBarPositions,
                    rebarShape = rebarShape?.Name ?? "Custom",
                    coverDistance = coverDistance,
                    layoutRule = rebar.LayoutRule.ToString()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Structural Loads

        /// <summary>
        /// Creates a point load on a structural element
        /// </summary>
        public static string CreatePointLoad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["hostElementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "hostElementId is required" });
                }

                if (parameters["location"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "location is required (provide x, y, z coordinates)" });
                }

                if (parameters["force"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "force is required (provide Fx, Fy, Fz)" });
                }

                // Get host element
                int hostIdInt = parameters["hostElementId"].ToObject<int>();
                Element host = doc.GetElement(new ElementId(hostIdInt));

                if (host == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Host element not found" });
                }

                // Parse location
                var loc = parameters["location"];
                XYZ location = new XYZ(loc["x"].ToObject<double>(), loc["y"].ToObject<double>(), loc["z"].ToObject<double>());

                // Parse force vector
                var force = parameters["force"];
                XYZ forceVector = new XYZ(
                    force["Fx"]?.ToObject<double>() ?? 0,
                    force["Fy"]?.ToObject<double>() ?? 0,
                    force["Fz"]?.ToObject<double>() ?? 0
                );

                // Parse moment vector (optional)
                XYZ momentVector = XYZ.Zero;
                if (parameters["moment"] != null)
                {
                    var moment = parameters["moment"];
                    momentVector = new XYZ(
                        moment["Mx"]?.ToObject<double>() ?? 0,
                        moment["My"]?.ToObject<double>() ?? 0,
                        moment["Mz"]?.ToObject<double>() ?? 0
                    );
                }

                using (var trans = new Transaction(doc, "Create Point Load"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create point load - Revit 2026 API requires PointLoadType (can be null)
                    PointLoad pointLoad = PointLoad.Create(doc, host.Id, location, forceVector, momentVector, null);

                    if (pointLoad == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "Failed to create point load" });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        pointLoadId = pointLoad.Id.Value,
                        hostElementId = hostIdInt,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        force = new { Fx = forceVector.X, Fy = forceVector.Y, Fz = forceVector.Z },
                        moment = new { Mx = momentVector.X, My = momentVector.Y, Mz = momentVector.Z },
                        message = "Point load created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates a line load on a structural element
        /// </summary>
        public static string CreateLineLoad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["hostElementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "hostElementId is required" });
                }

                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "startPoint and endPoint are required" });
                }

                if (parameters["forcePerLength"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "forcePerLength is required (provide Fx, Fy, Fz)" });
                }

                // Get host element
                int hostIdInt = parameters["hostElementId"].ToObject<int>();
                Element host = doc.GetElement(new ElementId(hostIdInt));

                if (host == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Host element not found" });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(start["x"].ToObject<double>(), start["y"].ToObject<double>(), start["z"].ToObject<double>());

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(end["x"].ToObject<double>(), end["y"].ToObject<double>(), end["z"].ToObject<double>());

                // Parse force per length
                var forcePerLength = parameters["forcePerLength"];
                XYZ forceVector = new XYZ(
                    forcePerLength["Fx"]?.ToObject<double>() ?? 0,
                    forcePerLength["Fy"]?.ToObject<double>() ?? 0,
                    forcePerLength["Fz"]?.ToObject<double>() ?? 0
                );

                // Parse moment per length (optional)
                XYZ momentVector = XYZ.Zero;
                if (parameters["momentPerLength"] != null)
                {
                    var momentPerLength = parameters["momentPerLength"];
                    momentVector = new XYZ(
                        momentPerLength["Mx"]?.ToObject<double>() ?? 0,
                        momentPerLength["My"]?.ToObject<double>() ?? 0,
                        momentPerLength["Mz"]?.ToObject<double>() ?? 0
                    );
                }

                using (var trans = new Transaction(doc, "Create Line Load"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create line curve from start and end points
                    Line lineCurve = Line.CreateBound(startPoint, endPoint);

                    // Create line load - Revit 2026 API requires Curve and LineLoadType (can be null)
                    LineLoad lineLoad = LineLoad.Create(doc, host.Id, lineCurve, forceVector, momentVector, null);

                    if (lineLoad == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "Failed to create line load" });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineLoadId = lineLoad.Id.Value,
                        hostElementId = hostIdInt,
                        startPoint = new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z },
                        endPoint = new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z },
                        forcePerLength = new { Fx = forceVector.X, Fy = forceVector.Y, Fz = forceVector.Z },
                        momentPerLength = new { Mx = momentVector.X, My = momentVector.Y, Mz = momentVector.Z },
                        message = "Line load created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates an area load
        /// </summary>
        public static string CreateAreaLoad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["hostElementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "hostElementId is required" });
                }

                if (parameters["boundaryPoints"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "boundaryPoints array is required" });
                }

                if (parameters["forcePerArea"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "forcePerArea is required (provide Fx, Fy, Fz)" });
                }

                // Get host element
                int hostIdInt = parameters["hostElementId"].ToObject<int>();
                Element host = doc.GetElement(new ElementId(hostIdInt));

                if (host == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Host element not found" });
                }

                // Parse boundary points
                var boundaryArray = parameters["boundaryPoints"] as JArray;
                List<XYZ> boundaryPoints = new List<XYZ>();
                foreach (var point in boundaryArray)
                {
                    boundaryPoints.Add(new XYZ(
                        point["x"].ToObject<double>(),
                        point["y"].ToObject<double>(),
                        point["z"].ToObject<double>()
                    ));
                }

                if (boundaryPoints.Count < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 3 boundary points required for area load" });
                }

                // Create curve loop from boundary points
                List<Curve> curves = new List<Curve>();
                for (int i = 0; i < boundaryPoints.Count; i++)
                {
                    XYZ start = boundaryPoints[i];
                    XYZ end = boundaryPoints[(i + 1) % boundaryPoints.Count];
                    curves.Add(Line.CreateBound(start, end));
                }

                CurveLoop curveLoop = CurveLoop.Create(curves);

                // Parse force per area
                var forcePerArea = parameters["forcePerArea"];
                XYZ forceVector = new XYZ(
                    forcePerArea["Fx"]?.ToObject<double>() ?? 0,
                    forcePerArea["Fy"]?.ToObject<double>() ?? 0,
                    forcePerArea["Fz"]?.ToObject<double>() ?? 0
                );

                using (var trans = new Transaction(doc, "Create Area Load"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create area load
                    AreaLoad areaLoad = AreaLoad.Create(doc, host.Id, new List<CurveLoop> { curveLoop }, forceVector, null);

                    if (areaLoad == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "Failed to create area load" });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        areaLoadId = areaLoad.Id.Value,
                        hostElementId = hostIdInt,
                        boundaryPointCount = boundaryPoints.Count,
                        forcePerArea = new { Fx = forceVector.X, Fy = forceVector.Y, Fz = forceVector.Z },
                        message = "Area load created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all loads on a structural element
        /// </summary>
        public static string GetElementLoads(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                // Get all loads in the document
                List<object> loads = new List<object>();

                // Get point loads
                var pointLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointLoad))
                    .Cast<PointLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId))
                    .Select(load => new
                    {
                        loadId = load.Id.Value,
                        loadType = "PointLoad",
                        location = load.Point != null ? new { x = load.Point.X, y = load.Point.Y, z = load.Point.Z } : null,
                        force = load.ForceVector != null ? new { Fx = load.ForceVector.X, Fy = load.ForceVector.Y, Fz = load.ForceVector.Z } : null,
                        moment = load.MomentVector != null ? new { Mx = load.MomentVector.X, My = load.MomentVector.Y, Mz = load.MomentVector.Z } : null
                    })
                    .ToList();

                loads.AddRange(pointLoads);

                // Get line loads
                var lineLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(LineLoad))
                    .Cast<LineLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId))
                    .Select(load => new
                    {
                        loadId = load.Id.Value,
                        loadType = "LineLoad",
                        startPoint = load.StartPoint != null ? new { x = load.StartPoint.X, y = load.StartPoint.Y, z = load.StartPoint.Z } : null,
                        endPoint = load.EndPoint != null ? new { x = load.EndPoint.X, y = load.EndPoint.Y, z = load.EndPoint.Z } : null,
                        forcePerLength = load.ForceVector1 != null ? new { Fx = load.ForceVector1.X, Fy = load.ForceVector1.Y, Fz = load.ForceVector1.Z } : null
                    })
                    .ToList();

                loads.AddRange(lineLoads);

                // Get area loads
                var areaLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(AreaLoad))
                    .Cast<AreaLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId))
                    .Select(load => new
                    {
                        loadId = load.Id.Value,
                        loadType = "AreaLoad"
                        // Note: AreaLoad force vectors require accessing individual boundary points
                        // Use AreaLoad.GetLoopVertexForceVector() method for detailed force data
                    })
                    .ToList();

                loads.AddRange(areaLoads);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    loads = loads,
                    count = loads.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Structural Analysis

        /// <summary>
        /// Gets structural analysis results for an element
        /// </summary>
        public static string GetAnalysisResults(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                Element element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                // Get the analytical model
                var analyticalManager = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
                ElementId analyticalId = analyticalManager.GetAssociatedElementId(element.Id);

                if (analyticalId == null || analyticalId == ElementId.InvalidElementId)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "This element does not have an analytical model. Analysis results require an analytical model."
                    });
                }

                AnalyticalElement analyticalElement = doc.GetElement(analyticalId) as AnalyticalElement;

                if (analyticalElement == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not retrieve analytical element"
                    });
                }

                // Note: Revit API does not store analysis results (forces, moments, stresses) in the model
                // These results come from external analysis software like Robot Structural Analysis
                // However, we can retrieve the loads applied to the element
                var appliedLoads = new List<object>();

                // Get point loads
                var pointLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointLoad))
                    .Cast<PointLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId))
                    .Select(load => new
                    {
                        loadId = load.Id.Value,
                        loadType = "PointLoad",
                        force = load.ForceVector != null ? new
                        {
                            Fx = load.ForceVector.X,
                            Fy = load.ForceVector.Y,
                            Fz = load.ForceVector.Z
                        } : null,
                        moment = load.MomentVector != null ? new
                        {
                            Mx = load.MomentVector.X,
                            My = load.MomentVector.Y,
                            Mz = load.MomentVector.Z
                        } : null
                    }).ToList();

                appliedLoads.AddRange(pointLoads);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    hasAnalyticalModel = true,
                    analyticalElementId = analyticalId.Value,
                    appliedLoads = appliedLoads,
                    note = "Revit API does not store computed analysis results (reactions, internal forces, stresses). " +
                           "These must be obtained from external analysis software like Robot Structural Analysis. " +
                           "This method returns the loads applied to the element.",
                    recommendation = "Use Robot Structural Analysis or other FEA software for computed results, " +
                                   "then link results back to Revit using shared parameters or external data."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets all structural elements in the project
        /// </summary>
        public static string GetAllStructuralElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Optional filters
                string structuralType = parameters["structuralType"]?.ToString(); // "column", "beam", "foundation", "framing"
                int? levelIdInt = parameters["levelId"]?.ToObject<int?>();
                int? viewIdInt = parameters["viewId"]?.ToObject<int?>();

                // Base collector
                FilteredElementCollector collector;

                if (viewIdInt.HasValue)
                {
                    View view = doc.GetElement(new ElementId(viewIdInt.Value)) as View;
                    if (view == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View with ID {viewIdInt.Value} not found"
                        });
                    }
                    collector = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                // Collect structural elements based on type
                var structuralElements = new List<object>();

                if (structuralType == null || structuralType.ToLower() == "column")
                {
                    var columns = collector
                        .OfCategory(BuiltInCategory.OST_StructuralColumns)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>();

                    if (levelIdInt.HasValue)
                    {
                        columns = columns.Where(c => c.LevelId.Value == levelIdInt.Value);
                    }

                    structuralElements.AddRange(columns.Select(col => new
                    {
                        elementId = col.Id.Value,
                        elementType = "StructuralColumn",
                        name = col.Name,
                        familyName = col.Symbol?.FamilyName,
                        typeName = col.Symbol?.Name,
                        levelId = col.LevelId?.Value,
                        levelName = col.LevelId != null ? (doc.GetElement(col.LevelId) as Level)?.Name : null,
                        location = col.Location is LocationPoint locPt ? new
                        {
                            x = locPt.Point.X,
                            y = locPt.Point.Y,
                            z = locPt.Point.Z
                        } : null,
                        baseOffset = col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble(),
                        topOffset = col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble(),
                        height = col.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM)?.AsDouble()
                    }));
                }

                if (structuralType == null || structuralType.ToLower() == "framing" || structuralType.ToLower() == "beam")
                {
                    // Reset collector if we already used it
                    if (structuralType != null)
                    {
                        collector = viewIdInt.HasValue
                            ? new FilteredElementCollector(doc, new ElementId(viewIdInt.Value))
                            : new FilteredElementCollector(doc);
                    }

                    var framing = collector
                        .OfCategory(BuiltInCategory.OST_StructuralFraming)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>();

                    if (levelIdInt.HasValue)
                    {
                        framing = framing.Where(f => f.LevelId.Value == levelIdInt.Value);
                    }

                    structuralElements.AddRange(framing.Select(frame => new
                    {
                        elementId = frame.Id.Value,
                        elementType = "StructuralFraming",
                        name = frame.Name,
                        familyName = frame.Symbol?.FamilyName,
                        typeName = frame.Symbol?.Name,
                        levelId = frame.LevelId?.Value,
                        levelName = frame.LevelId != null ? (doc.GetElement(frame.LevelId) as Level)?.Name : null,
                        startPoint = frame.Location is LocationCurve locCurve ? new
                        {
                            x = locCurve.Curve.GetEndPoint(0).X,
                            y = locCurve.Curve.GetEndPoint(0).Y,
                            z = locCurve.Curve.GetEndPoint(0).Z
                        } : null,
                        endPoint = frame.Location is LocationCurve locCurve2 ? new
                        {
                            x = locCurve2.Curve.GetEndPoint(1).X,
                            y = locCurve2.Curve.GetEndPoint(1).Y,
                            z = locCurve2.Curve.GetEndPoint(1).Z
                        } : null,
                        length = frame.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM)?.AsDouble()
                    }));
                }

                if (structuralType == null || structuralType.ToLower() == "foundation")
                {
                    // Reset collector
                    if (structuralType != null)
                    {
                        collector = viewIdInt.HasValue
                            ? new FilteredElementCollector(doc, new ElementId(viewIdInt.Value))
                            : new FilteredElementCollector(doc);
                    }

                    var foundations = collector
                        .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>();

                    if (levelIdInt.HasValue)
                    {
                        foundations = foundations.Where(f => f.LevelId.Value == levelIdInt.Value);
                    }

                    structuralElements.AddRange(foundations.Select(found => new
                    {
                        elementId = found.Id.Value,
                        elementType = "StructuralFoundation",
                        name = found.Name,
                        familyName = found.Symbol?.FamilyName,
                        typeName = found.Symbol?.Name,
                        levelId = found.LevelId?.Value,
                        levelName = found.LevelId != null ? (doc.GetElement(found.LevelId) as Level)?.Name : null,
                        location = found.Location is LocationPoint locPt ? new
                        {
                            x = locPt.Point.X,
                            y = locPt.Point.Y,
                            z = locPt.Point.Z
                        } : null
                    }));
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = structuralElements.Count,
                    filters = new
                    {
                        structuralType = structuralType ?? "all",
                        levelId = levelIdInt,
                        viewId = viewIdInt
                    },
                    elements = structuralElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Deletes a structural element
        /// </summary>
        public static string DeleteStructuralElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                Element element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                // Verify it's a structural element
                bool isStructural = false;
                string elementType = "Unknown";

                if (element.Category != null)
                {
                    var catId = element.Category.Id.Value;
                    if (catId == (int)BuiltInCategory.OST_StructuralColumns)
                    {
                        isStructural = true;
                        elementType = "StructuralColumn";
                    }
                    else if (catId == (int)BuiltInCategory.OST_StructuralFraming)
                    {
                        isStructural = true;
                        elementType = "StructuralFraming";
                    }
                    else if (catId == (int)BuiltInCategory.OST_StructuralFoundation)
                    {
                        isStructural = true;
                        elementType = "StructuralFoundation";
                    }
                }

                if (!isStructural)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element {elementIdInt} is not a structural element (Category: {element.Category?.Name})"
                    });
                }

                // Check for connected loads
                var connectedLoads = new List<int>();

                var pointLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointLoad))
                    .Cast<PointLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId));
                connectedLoads.AddRange(pointLoads.Select(l => (int)l.Id.Value));

                var lineLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(LineLoad))
                    .Cast<LineLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId));
                connectedLoads.AddRange(lineLoads.Select(l => (int)l.Id.Value));

                var areaLoads = new FilteredElementCollector(doc)
                    .OfClass(typeof(AreaLoad))
                    .Cast<AreaLoad>()
                    .Where(load => load.HostElementId != null && load.HostElementId.Equals(elementId));
                connectedLoads.AddRange(areaLoads.Select(l => (int)l.Id.Value));

                // Optional: check if user wants to delete dependent elements
                bool deleteDependents = parameters["deleteDependents"]?.ToObject<bool>() ?? false;

                using (var trans = new Transaction(doc, "Delete Structural Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete connected loads if requested
                    if (deleteDependents && connectedLoads.Count > 0)
                    {
                        foreach (var loadId in connectedLoads)
                        {
                            doc.Delete(new ElementId(loadId));
                        }
                    }
                    else if (connectedLoads.Count > 0)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Cannot delete element {elementIdInt}. It has {connectedLoads.Count} connected load(s).",
                            connectedLoadIds = connectedLoads,
                            suggestion = "Set 'deleteDependents' parameter to true to delete loads along with the element, or manually delete the loads first."
                        });
                    }

                    // Delete the element
                    ICollection<ElementId> deletedIds = doc.Delete(elementId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedElementId = elementIdInt,
                        elementType = elementType,
                        deletedCount = deletedIds.Count,
                        deletedIds = deletedIds.Select(id => id.Value).ToList(),
                        deletedLoadCount = deleteDependents ? connectedLoads.Count : 0,
                        message = deletedIds.Count > 1
                            ? $"Deleted element {elementIdInt} and {deletedIds.Count - 1} dependent element(s)"
                            : $"Deleted element {elementIdInt}"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Escalators

        /// <summary>
        /// Create an escalator element between two levels with configurable width and inclination
        /// Auto-generated from approved tool spec via capability system
        /// </summary>
        public static string CreateEscalator(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters?["baseLevelId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "baseLevelId is required" });
                if (parameters?["topLevelId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "topLevelId is required" });
                if (parameters?["startPoint"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "startPoint is required" });

                var baseLevelId = parameters["baseLevelId"].Value<long>();
                var topLevelId = parameters["topLevelId"].Value<long>();
                var startPointArr = parameters["startPoint"].ToObject<double[]>();
                var width = parameters["width"]?.Value<double>() ?? 48.0; // inches
                var inclination = parameters["inclination"]?.Value<double>() ?? 30.0; // degrees

                // Get levels
                var baseLevel = doc.GetElement(new ElementId(baseLevelId)) as Level;
                var topLevel = doc.GetElement(new ElementId(topLevelId)) as Level;

                if (baseLevel == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"Base level not found: {baseLevelId}" });
                if (topLevel == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"Top level not found: {topLevelId}" });

                // Calculate rise and run
                double rise = topLevel.Elevation - baseLevel.Elevation;
                double inclinationRad = inclination * Math.PI / 180.0;
                double run = rise / Math.Tan(inclinationRad);

                // Convert width from inches to feet
                double widthFeet = width / 12.0;

                // Start point
                var startPoint = new XYZ(startPointArr[0], startPointArr[1], baseLevel.Elevation);
                var endPoint = new XYZ(startPointArr[0] + run, startPointArr[1], topLevel.Elevation);

                // Find escalator family type (or generic model family)
                FamilySymbol escalatorType = null;
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_SpecialityEquipment);

                foreach (FamilySymbol fs in collector)
                {
                    string name = fs.Name.ToLower();
                    if (name.Contains("escalator"))
                    {
                        escalatorType = fs;
                        break;
                    }
                }

                // If no escalator family found, try generic models
                if (escalatorType == null)
                {
                    var genericCollector = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_GenericModel);

                    foreach (FamilySymbol fs in genericCollector)
                    {
                        string name = fs.Name.ToLower();
                        if (name.Contains("escalator"))
                        {
                            escalatorType = fs;
                            break;
                        }
                    }
                }

                using (var trans = new Transaction(doc, "Create Escalator"))
                {
                    trans.Start();

                    ElementId placedElementId = ElementId.InvalidElementId;

                    if (escalatorType != null)
                    {
                        // Activate the symbol if needed
                        if (!escalatorType.IsActive)
                            escalatorType.Activate();

                        // Place the escalator family instance
                        var instance = doc.Create.NewFamilyInstance(
                            startPoint,
                            escalatorType,
                            baseLevel,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                        placedElementId = instance.Id;

                        // Try to set parameters if they exist
                        try
                        {
                            var widthParam = instance.LookupParameter("Width");
                            if (widthParam != null && !widthParam.IsReadOnly)
                                widthParam.Set(widthFeet);

                            var heightParam = instance.LookupParameter("Height") ?? instance.LookupParameter("Rise");
                            if (heightParam != null && !heightParam.IsReadOnly)
                                heightParam.Set(rise);

                            var runParam = instance.LookupParameter("Run") ?? instance.LookupParameter("Length");
                            if (runParam != null && !runParam.IsReadOnly)
                                runParam.Set(run);
                        }
                        catch { /* Parameters may not exist */ }
                    }
                    else
                    {
                        // No escalator family found - create horizontal detail line as placeholder
                        var view = doc.ActiveView;
                        if (view != null && view.ViewType == ViewType.FloorPlan)
                        {
                            // Create flat line at base level for plan view
                            var flatStart = new XYZ(startPoint.X, startPoint.Y, 0);
                            var flatEnd = new XYZ(endPoint.X, endPoint.Y, 0);
                            var line = Line.CreateBound(flatStart, flatEnd);
                            var detailCurve = doc.Create.NewDetailCurve(view, line);
                            placedElementId = detailCurve.Id;
                        }
                        // For other views, we just won't create a placeholder
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = placedElementId.Value,
                        escalatorFamilyFound = escalatorType != null,
                        familyName = escalatorType?.FamilyName,
                        typeName = escalatorType?.Name,
                        baseLevelName = baseLevel.Name,
                        topLevelName = topLevel.Name,
                        rise = rise,
                        run = run,
                        inclination = inclination,
                        widthInches = width,
                        startPoint = new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z },
                        endPoint = new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z },
                        message = escalatorType != null
                            ? "Escalator placed successfully"
                            : "No escalator family found - created placeholder line. Load an escalator family for full functionality."
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
