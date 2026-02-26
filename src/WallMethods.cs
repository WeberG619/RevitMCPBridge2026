using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Wall creation, modification, and management methods for MCP Bridge
    /// </summary>
    public static class WallMethods
    {
        /// <summary>
        /// Create a wall from two points
        /// </summary>
        [MCPMethod("createWallByPoints", "createWall", Category = "Wall", Description = "Create a wall between two XYZ points")]
        public static string CreateWallByPoints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                var v = new ParameterValidator(parameters, "createWallByPoints");
                v.Require("startPoint");
                v.Require("endPoint");
                v.Require("levelId").IsType<int>();
                v.Optional("height").IsPositive();
                v.ThrowIfInvalid();

                // Extract point arrays
                var startPoint = parameters["startPoint"].ToObject<double[]>();
                var endPoint = parameters["endPoint"].ToObject<double[]>();
                var levelIdInt = v.GetRequired<int>("levelId");
                var height = v.GetOptional<double>("height", 10.0);

                // Resolve level via ElementLookup (throws MCPRevitException if not found)
                var level = ElementLookup.GetLevel(doc, levelIdInt);

                // Resolve wall type: by ID, by name, or fall back to default
                WallType wallType = null;
                var wallTypeIdToken = parameters["wallTypeId"];
                var wallTypeNameToken = parameters["wallTypeName"];

                if (wallTypeIdToken != null)
                {
                    wallType = ElementLookup.GetWallType(doc, wallTypeIdToken.Value<int>());
                }
                else if (wallTypeNameToken != null)
                {
                    wallType = ElementLookup.GetWallType(doc, wallTypeNameToken.Value<string>());
                }
                else
                {
                    wallType = ElementLookup.GetDefaultWallType(doc);
                }

                if (wallType == null)
                {
                    return ResponseBuilder.Error("No valid wall type found in document", "NO_WALL_TYPE").Build();
                }

                using (var trans = new Transaction(doc, "Create Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var start = new XYZ(startPoint[0], startPoint[1], startPoint[2]);
                    var end = new XYZ(endPoint[0], endPoint[1], endPoint[2]);
                    var line = Line.CreateBound(start, end);

                    var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wall.Id.Value)
                        .With("wallType", wallType.Name)
                        .With("level", level.Name)
                        .With("length", wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0)
                        .With("height", height)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create multiple walls from a series of points (polyline)
        /// </summary>
        [MCPMethod("createWallsFromPolyline", Category = "Wall", Description = "Create walls from a polyline of points")]
        public static string CreateWallsFromPolyline(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return ResponseBuilder.Error("No active document. Please ensure a Revit project is open and active.", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["points"].ToObject<double[][]>();
                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                var height = parameters["height"] != null
                    ? double.Parse(parameters["height"].ToString())
                    : 10.0;
                var closed = parameters["closed"] != null
                    ? bool.Parse(parameters["closed"].ToString())
                    : false;

                WallType wallType = null;
                if (parameters["wallTypeId"] != null)
                {
                    var wallTypeId = new ElementId(int.Parse(parameters["wallTypeId"].ToString()));
                    wallType = doc.GetElement(wallTypeId) as WallType;
                }
                else
                {
                    wallType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                }

                var level = doc.GetElement(levelId) as Level;

                using (var trans = new Transaction(doc, "Create Walls from Polyline"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var wallIds = new List<int>();
                    var pointCount = closed ? points.Length : points.Length - 1;

                    for (int i = 0; i < pointCount; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                        var endIndex = (i + 1) % points.Length;
                        var end = new XYZ(points[endIndex][0], points[endIndex][1], points[endIndex][2]);

                        var line = Line.CreateBound(start, end);
                        var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);
                        wallIds.Add((int)wall.Id.Value);
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallCount", wallIds.Count)
                        .With("wallIds", wallIds)
                        .With("closed", closed)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get wall information
        /// </summary>
        [MCPMethod("getWallInfo", Category = "Wall", Description = "Get detailed info about a specific wall")]
        public static string GetWallInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                var wallType = wall.WallType;
                var curve = (wall.Location as LocationCurve)?.Curve;
                var level = doc.GetElement(wall.LevelId) as Level;

                // NULL SAFETY: Handle cases where curve or wallType is null
                double[] startPoint = null;
                double[] endPoint = null;
                if (curve != null)
                {
                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);
                    startPoint = new[] { start.X, start.Y, start.Z };
                    endPoint = new[] { end.X, end.Y, end.Z };
                }

                return ResponseBuilder.Success()
                    .With("wallId", (int)wall.Id.Value)
                    .With("wallType", wallType?.Name ?? "Unknown")
                    .With("wallTypeId", wallType != null ? (int)wallType.Id.Value : -1)
                    .With("level", level?.Name)
                    .With("levelId", (int)wall.LevelId.Value)
                    .With("length", wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0)
                    .With("height", wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0)
                    .With("width", wall.Width)
                    .With("area", wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0)
                    .With("volume", wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0)
                    .With("structural", wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1)
                    .With("startPoint", startPoint)
                    .With("endPoint", endPoint)
                    .With("hasCurve", curve != null)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify wall properties
        /// </summary>
        [MCPMethod("modifyWallProperties", "modifyWall", Category = "Wall", Description = "Modify wall properties (type, height, offset, etc.)")]
        public static string ModifyWallProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Modify Wall Properties"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modified = new List<string>();

                    // Change wall type
                    if (parameters["wallTypeId"] != null)
                    {
                        var newTypeId = new ElementId(int.Parse(parameters["wallTypeId"].ToString()));
                        wall.WallType = doc.GetElement(newTypeId) as WallType;
                        modified.Add("wallType");
                    }

                    // Change height
                    if (parameters["height"] != null)
                    {
                        var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                        if (heightParam != null && !heightParam.IsReadOnly)
                        {
                            heightParam.Set(double.Parse(parameters["height"].ToString()));
                            modified.Add("height");
                        }
                    }

                    // Change base offset
                    if (parameters["baseOffset"] != null)
                    {
                        var offsetParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                        if (offsetParam != null && !offsetParam.IsReadOnly)
                        {
                            offsetParam.Set(double.Parse(parameters["baseOffset"].ToString()));
                            modified.Add("baseOffset");
                        }
                    }

                    // Change top offset
                    if (parameters["topOffset"] != null)
                    {
                        var topParam = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                        if (topParam != null && !topParam.IsReadOnly)
                        {
                            topParam.Set(double.Parse(parameters["topOffset"].ToString()));
                            modified.Add("topOffset");
                        }
                    }

                    // Change structural property
                    if (parameters["structural"] != null)
                    {
                        var structParam = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                        if (structParam != null && !structParam.IsReadOnly)
                        {
                            structParam.Set(bool.Parse(parameters["structural"].ToString()) ? 1 : 0);
                            modified.Add("structural");
                        }
                    }

                    // Change location line (controls where room boundary is calculated from)
                    if (parameters["locationLine"] != null)
                    {
                        var locationLineParam = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                        if (locationLineParam != null && !locationLineParam.IsReadOnly)
                        {
                            var locationLineStr = parameters["locationLine"].ToString();
                            WallLocationLine locationLine;

                            switch (locationLineStr.ToLower())
                            {
                                case "wallcenterline":
                                    locationLine = WallLocationLine.WallCenterline;
                                    break;
                                case "corecenterline":
                                    locationLine = WallLocationLine.CoreCenterline;
                                    break;
                                case "finishfaceexterior":
                                    locationLine = WallLocationLine.FinishFaceExterior;
                                    break;
                                case "finishfaceinterior":
                                    locationLine = WallLocationLine.FinishFaceInterior;
                                    break;
                                case "coreexterior":
                                    locationLine = WallLocationLine.CoreExterior;
                                    break;
                                case "coreinterior":
                                    locationLine = WallLocationLine.CoreInterior;
                                    break;
                                default:
                                    return ResponseBuilder.Error($"Invalid locationLine value: {locationLineStr}. Valid values: WallCenterline, CoreCenterline, FinishFaceExterior, FinishFaceInterior, CoreExterior, CoreInterior", "INVALID_PARAMETER").Build();
                            }

                            locationLineParam.Set((int)locationLine);
                            modified.Add("locationLine");
                        }
                    }

                    // Change room bounding property
                    if (parameters["roomBounding"] != null)
                    {
                        var roomBoundingParam = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                        if (roomBoundingParam != null && !roomBoundingParam.IsReadOnly)
                        {
                            roomBoundingParam.Set(bool.Parse(parameters["roomBounding"].ToString()) ? 1 : 0);
                            modified.Add("roomBounding");
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wall.Id.Value)
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
        /// Split a wall at a point
        /// </summary>
        [MCPMethod("splitWall", Category = "Wall", Description = "Split a wall at a given point")]
        public static string SplitWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var splitPoint = parameters["splitPoint"].ToObject<double[]>();
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Split Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(splitPoint[0], splitPoint[1], splitPoint[2]);

                    // NULL SAFETY: Validate locationCurve before accessing Curve
                    var locationCurve = wall.Location as LocationCurve;
                    if (locationCurve == null)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Wall does not have a valid location curve", "INVALID_GEOMETRY").Build();
                    }
                    var curve = locationCurve.Curve;

                    // Project point onto wall curve
                    var result = curve.Project(point);
                    if (result == null)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Point cannot be projected onto wall", "INVALID_GEOMETRY").Build();
                    }

                    var splitParameter = result.Parameter;

                    // In Revit 2026, wall splitting is more complex
                    // We'll need to manually split by creating two new walls and deleting the original
                    var curve1 = Line.CreateBound(curve.GetEndPoint(0), result.XYZPoint);
                    var curve2 = Line.CreateBound(result.XYZPoint, curve.GetEndPoint(1));

                    var wallType = wall.WallType;
                    // NULL SAFETY: Validate level before using
                    var level = doc.GetElement(wall.LevelId) as Level;
                    if (level == null)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Wall level not found", "ELEMENT_NOT_FOUND").Build();
                    }

                    var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10.0;
                    var baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;

                    // Create two new walls
                    var wall1 = Wall.Create(doc, curve1, wallType.Id, level.Id, height, baseOffset, false, false);
                    var wall2 = Wall.Create(doc, curve2, wallType.Id, level.Id, height, baseOffset, false, false);

                    // Copy parameters from original wall to new walls
                    var paramsToCheck = new[] {
                        BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT,
                        BuiltInParameter.WALL_TOP_OFFSET,
                        BuiltInParameter.WALL_BASE_CONSTRAINT
                    };

                    foreach (var paramId in paramsToCheck)
                    {
                        var origParam = wall.get_Parameter(paramId);
                        if (origParam != null && !origParam.IsReadOnly)
                        {
                            var param1 = wall1.get_Parameter(paramId);
                            var param2 = wall2.get_Parameter(paramId);
                            if (param1 != null && !param1.IsReadOnly)
                            {
                                if (origParam.StorageType == StorageType.Integer)
                                    param1.Set(origParam.AsInteger());
                                else if (origParam.StorageType == StorageType.Double)
                                    param1.Set(origParam.AsDouble());
                            }
                            if (param2 != null && !param2.IsReadOnly)
                            {
                                if (origParam.StorageType == StorageType.Integer)
                                    param2.Set(origParam.AsInteger());
                                else if (origParam.StorageType == StorageType.Double)
                                    param2.Set(origParam.AsDouble());
                            }
                        }
                    }

                    // Delete the original wall
                    doc.Delete(wallId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("originalWallId", (int)wallId.Value)
                        .With("newWallIds", new List<int> { (int)wall1.Id.Value, (int)wall2.Id.Value })
                        .With("splitPoint", new[] { result.XYZPoint.X, result.XYZPoint.Y, result.XYZPoint.Z })
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Join two walls
        /// </summary>
        [MCPMethod("joinWalls", Category = "Wall", Description = "Join two walls together")]
        public static string JoinWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wall1Id = new ElementId(int.Parse(parameters["wall1Id"].ToString()));
                var wall2Id = new ElementId(int.Parse(parameters["wall2Id"].ToString()));

                var wall1 = doc.GetElement(wall1Id) as Wall;
                var wall2 = doc.GetElement(wall2Id) as Wall;

                if (wall1 == null || wall2 == null)
                {
                    return ResponseBuilder.Error("One or both walls not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Join Walls"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Join the walls at their common endpoint
                    JoinGeometryUtils.JoinGeometry(doc, wall1, wall2);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wall1Id", (int)wall1Id.Value)
                        .With("wall2Id", (int)wall2Id.Value)
                        .WithMessage("Walls joined successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Unjoin two walls
        /// </summary>
        [MCPMethod("unjoinWalls", Category = "Wall", Description = "Unjoin two previously joined walls")]
        public static string UnjoinWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wall1Id = new ElementId(int.Parse(parameters["wall1Id"].ToString()));
                var wall2Id = new ElementId(int.Parse(parameters["wall2Id"].ToString()));

                var wall1 = doc.GetElement(wall1Id) as Wall;
                var wall2 = doc.GetElement(wall2Id) as Wall;

                if (wall1 == null || wall2 == null)
                {
                    return ResponseBuilder.Error("One or both walls not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Unjoin Walls"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    JoinGeometryUtils.UnjoinGeometry(doc, wall1, wall2);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wall1Id", (int)wall1Id.Value)
                        .With("wall2Id", (int)wall2Id.Value)
                        .WithMessage("Walls unjoined successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all walls in the document with geometry
        /// </summary>
        [MCPMethod("getWalls", Category = "Wall", Description = "Get all walls in the active document")]
        public static string GetWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var walls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .Select(w => {
                        // Get wall location curve
                        var locationCurve = w.Location as LocationCurve;
                        var curve = locationCurve?.Curve;
                        XYZ startPoint = null;
                        XYZ endPoint = null;

                        if (curve != null)
                        {
                            startPoint = curve.GetEndPoint(0);
                            endPoint = curve.GetEndPoint(1);
                        }

                        // Get level info
                        var baseLevel = doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                        var topLevel = doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId) as Level;

                        return new
                        {
                            wallId = (int)w.Id.Value,
                            wallType = w.WallType.Name,
                            wallTypeId = (int)w.WallType.Id.Value,
                            length = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0,
                            height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                            width = w.Width,
                            structural = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1,
                            startPoint = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                            endPoint = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null,
                            baseLevel = baseLevel?.Name,
                            topLevel = topLevel?.Name,
                            baseOffset = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0,
                            topOffset = w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallCount", walls.Count)
                    .WithList("walls", walls)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all walls in a view
        /// </summary>
        [MCPMethod("getWallsInView", Category = "Wall", Description = "Get walls visible in a specific view")]
        public static string GetWallsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view = doc.GetElement(viewId) as View;

                if (view == null)
                {
                    return ResponseBuilder.Error("View not found", "ELEMENT_NOT_FOUND").Build();
                }

                var walls = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .Select(w => new
                    {
                        wallId = (int)w.Id.Value,
                        wallType = w.WallType.Name,
                        wallTypeId = (int)w.WallType.Id.Value,
                        length = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0,
                        height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                        width = w.Width,
                        structural = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("viewId", (int)viewId.Value)
                    .With("viewName", view.Name)
                    .With("wallCount", walls.Count)
                    .WithList("walls", walls)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all available wall types
        /// </summary>
        [MCPMethod("getWallTypes", Category = "Wall", Description = "Get all wall types in the document")]
        public static string GetWallTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .Select(wt => new
                    {
                        wallTypeId = (int)wt.Id.Value,
                        name = wt.Name,
                        kind = wt.Kind.ToString(),
                        width = wt.Width,
                        familyName = wt.FamilyName
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallTypeCount", wallTypes.Count)
                    .WithList("wallTypes", wallTypes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicate a wall type with a new name
        /// </summary>
        [MCPMethod("duplicateWallType", Category = "Wall", Description = "Duplicate a wall type with a new name")]
        public static string DuplicateWallType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sourceTypeId = new ElementId(int.Parse(parameters["sourceTypeId"].ToString()));
                var newTypeName = parameters["newTypeName"].ToString();

                var sourceType = doc.GetElement(sourceTypeId) as WallType;
                if (sourceType == null)
                {
                    return ResponseBuilder.Error("Source wall type not found", "ELEMENT_NOT_FOUND").Build();
                }

                // Check if type with this name already exists
                var existingType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name == newTypeName);

                if (existingType != null)
                {
                    return ResponseBuilder.Success()
                        .With("newTypeId", (int)existingType.Id.Value)
                        .With("newTypeName", existingType.Name)
                        .WithMessage("Wall type with this name already exists")
                        .Build();
                }

                using (var trans = new Transaction(doc, "Duplicate Wall Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var newType = sourceType.Duplicate(newTypeName) as WallType;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("newTypeId", (int)newType.Id.Value)
                        .With("newTypeName", newType.Name)
                        .With("sourceTypeName", sourceType.Name)
                        .With("width", newType.Width)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Flip wall orientation
        /// </summary>
        [MCPMethod("flipWall", Category = "Wall", Description = "Flip a wall's interior/exterior face")]
        public static string FlipWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Flip Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    wall.Flip();

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .WithMessage("Wall flipped successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a wall
        /// </summary>
        [MCPMethod("deleteWall", Category = "Wall", Description = "Delete a wall by element ID")]
        public static string DeleteWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(wallId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .WithMessage("Wall deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create multiple walls in a single transaction (batch operation to avoid timeouts)
        /// </summary>
        [MCPMethod("batchCreateWalls", Category = "Wall", Description = "Create multiple walls in a single transaction")]
        public static string BatchCreateWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return ResponseBuilder.Error("No active document. Please ensure a Revit project is open and active.", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var walls = parameters["walls"].ToObject<JArray>();
                var createdWalls = new List<object>();
                var failedWalls = new List<object>();

                using (var trans = new Transaction(doc, "Batch Create Walls"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var wallData in walls)
                    {
                        try
                        {
                            var startPoint = wallData["startPoint"].ToObject<double[]>();
                            var endPoint = wallData["endPoint"].ToObject<double[]>();
                            var levelId = new ElementId(int.Parse(wallData["levelId"].ToString()));
                            var height = wallData["height"] != null
                                ? double.Parse(wallData["height"].ToString())
                                : 10.0;

                            // Get wall type - by ID, by name, or use default (matches createWall pattern)
                            WallType wallType = null;
                            if (wallData["wallTypeId"] != null)
                            {
                                var wallTypeId = new ElementId(int.Parse(wallData["wallTypeId"].ToString()));
                                wallType = doc.GetElement(wallTypeId) as WallType;
                            }
                            else if (wallData["wallTypeName"] != null)
                            {
                                var typeName = wallData["wallTypeName"].ToString();
                                wallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(wt => wt.Name == typeName);
                            }

                            // Fallback to default wall type
                            if (wallType == null)
                            {
                                wallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                            }

                            var level = doc.GetElement(levelId) as Level;
                            if (level == null || wallType == null)
                            {
                                failedWalls.Add(new
                                {
                                    error = "Invalid level or wall type",
                                    startPoint = startPoint,
                                    endPoint = endPoint
                                });
                                continue;
                            }

                            var start = new XYZ(startPoint[0], startPoint[1], startPoint[2]);
                            var end = new XYZ(endPoint[0], endPoint[1], endPoint[2]);
                            var line = Line.CreateBound(start, end);

                            var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);

                            createdWalls.Add(new
                            {
                                wallId = (int)wall.Id.Value,
                                wallType = wallType.Name,
                                level = level.Name
                            });
                        }
                        catch (Exception ex)
                        {
                            failedWalls.Add(new
                            {
                                error = ex.Message
                            });
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("createdCount", createdWalls.Count)
                        .With("failedCount", failedWalls.Count)
                        .With("createdWalls", createdWalls)
                        .With("failedWalls", failedWalls)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change the type of an existing wall
        /// </summary>
        [MCPMethod("modifyWallType", Category = "Wall", Description = "Modify wall type properties (layers, function, etc.)")]
        public static string ModifyWallType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var newTypeId = new ElementId(int.Parse(parameters["newTypeId"].ToString()));

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();
                }

                var newType = doc.GetElement(newTypeId) as WallType;
                if (newType == null)
                {
                    return ResponseBuilder.Error("Wall type not found", "TYPE_NOT_FOUND").Build();
                }

                var oldTypeName = wall.WallType.Name;

                using (var trans = new Transaction(doc, "Modify Wall Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    wall.WallType = newType;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .With("oldType", oldTypeName)
                        .With("newType", newType.Name)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch modify wall types for multiple walls
        /// </summary>
        [MCPMethod("batchModifyWallTypes", Category = "Wall", Description = "Modify multiple wall types in a single transaction")]
        public static string BatchModifyWallTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallIds = parameters["wallIds"].ToObject<int[]>();
                var newTypeId = new ElementId(int.Parse(parameters["newTypeId"].ToString()));

                var newType = doc.GetElement(newTypeId) as WallType;
                if (newType == null)
                {
                    return ResponseBuilder.Error("Wall type not found", "TYPE_NOT_FOUND").Build();
                }

                var modifiedCount = 0;
                var failedCount = 0;

                using (var trans = new Transaction(doc, "Batch Modify Wall Types"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var id in wallIds)
                    {
                        try
                        {
                            var wall = doc.GetElement(new ElementId(id)) as Wall;
                            if (wall != null)
                            {
                                wall.WallType = newType;
                                modifiedCount++;
                            }
                            else
                            {
                                failedCount++;
                            }
                        }
                        catch
                        {
                            failedCount++;
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("modifiedCount", modifiedCount)
                        .With("failedCount", failedCount)
                        .With("newTypeName", newType.Name)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set a single wall endpoint to a new location.
        /// Used for adjusting wall connections without recreating walls.
        /// </summary>
        /// <param name="wallId">ID of the wall to modify</param>
        /// <param name="endIndex">0 for start point, 1 for end point</param>
        /// <param name="newPoint">New coordinates [x, y, z] in feet</param>
        [MCPMethod("setWallEndpoint", Category = "Wall", Description = "Set the start or end point of a wall")]
        public static string SetWallEndpoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return ResponseBuilder.Error("No active document. Please ensure a Revit project is open.", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var endIndex = int.Parse(parameters["endIndex"].ToString()); // 0 = start, 1 = end
                var newPoint = parameters["newPoint"].ToObject<double[]>();

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error($"Wall with ID {wallId.Value} not found", "ELEMENT_NOT_FOUND").Build();
                }

                var locationCurve = wall.Location as LocationCurve;
                if (locationCurve == null)
                {
                    return ResponseBuilder.Error("Wall does not have a valid location curve", "INVALID_GEOMETRY").Build();
                }

                var curve = locationCurve.Curve;
                var oldStart = curve.GetEndPoint(0);
                var oldEnd = curve.GetEndPoint(1);
                var newXYZ = new XYZ(newPoint[0], newPoint[1], newPoint[2]);

                using (var trans = new Transaction(doc, "Set Wall Endpoint"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create new line based on which endpoint to move
                    Line newLine;
                    if (endIndex == 0)
                    {
                        // Move start point
                        newLine = Line.CreateBound(newXYZ, oldEnd);
                    }
                    else
                    {
                        // Move end point
                        newLine = Line.CreateBound(oldStart, newXYZ);
                    }

                    // Set the new curve
                    locationCurve.Curve = newLine;

                    trans.Commit();

                    // Get updated curve
                    var updatedCurve = (wall.Location as LocationCurve).Curve;
                    var updatedStart = updatedCurve.GetEndPoint(0);
                    var updatedEnd = updatedCurve.GetEndPoint(1);

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .With("endIndex", endIndex)
                        .With("oldPoint", endIndex == 0
                            ? new[] { oldStart.X, oldStart.Y, oldStart.Z }
                            : new[] { oldEnd.X, oldEnd.Y, oldEnd.Z })
                        .With("newPoint", new[] { newXYZ.X, newXYZ.Y, newXYZ.Z })
                        .With("updatedStartPoint", new[] { updatedStart.X, updatedStart.Y, updatedStart.Z })
                        .With("updatedEndPoint", new[] { updatedEnd.X, updatedEnd.Y, updatedEnd.Z })
                        .With("newLength", updatedCurve.Length)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extend a wall to meet another element (wall, grid, reference plane).
        /// </summary>
        /// <param name="wallId">ID of the wall to extend</param>
        /// <param name="endIndex">0 for start point, 1 for end point</param>
        /// <param name="targetElementId">ID of the element to extend to</param>
        [MCPMethod("extendWallToElement", Category = "Wall", Description = "Extend a wall to meet another element")]
        public static string ExtendWallToElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return ResponseBuilder.Error("No active document. Please ensure a Revit project is open.", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var endIndex = int.Parse(parameters["endIndex"].ToString());
                var targetElementId = new ElementId(int.Parse(parameters["targetElementId"].ToString()));

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error($"Wall with ID {wallId.Value} not found", "ELEMENT_NOT_FOUND").Build();
                }

                var targetElement = doc.GetElement(targetElementId);
                if (targetElement == null)
                {
                    return ResponseBuilder.Error($"Target element with ID {targetElementId.Value} not found", "ELEMENT_NOT_FOUND").Build();
                }

                var locationCurve = wall.Location as LocationCurve;
                if (locationCurve == null)
                {
                    return ResponseBuilder.Error("Wall does not have a valid location curve", "INVALID_GEOMETRY").Build();
                }

                var curve = locationCurve.Curve as Line;
                if (curve == null)
                {
                    return ResponseBuilder.Error("Wall curve is not a line (curved walls not supported)", "INVALID_GEOMETRY").Build();
                }

                // Get target curve/plane to intersect with
                Curve targetCurve = null;
                if (targetElement is Wall targetWall)
                {
                    var targetLocation = targetWall.Location as LocationCurve;
                    targetCurve = targetLocation?.Curve;
                }
                else if (targetElement is Grid grid)
                {
                    targetCurve = grid.Curve;
                }
                else if (targetElement is ReferencePlane refPlane)
                {
                    // Create a line from reference plane
                    var bubbleEnd = refPlane.BubbleEnd;
                    var freeEnd = refPlane.FreeEnd;
                    targetCurve = Line.CreateBound(bubbleEnd, freeEnd);
                }

                if (targetCurve == null)
                {
                    return ResponseBuilder.Error("Target element does not have a valid curve for intersection", "INVALID_GEOMETRY").Build();
                }

                // Find intersection point
                var wallStart = curve.GetEndPoint(0);
                var wallEnd = curve.GetEndPoint(1);
                var wallDirection = (wallEnd - wallStart).Normalize();

                // Extend the wall line infinitely in the appropriate direction
                var pointToExtend = endIndex == 0 ? wallStart : wallEnd;
                var extendDirection = endIndex == 0 ? -wallDirection : wallDirection;

                // Create extended line (100 feet extension should be enough)
                var extendedPoint = pointToExtend + extendDirection * 100;
                var extendedLine = endIndex == 0
                    ? Line.CreateBound(extendedPoint, wallEnd)
                    : Line.CreateBound(wallStart, extendedPoint);

                // Find intersection with target
                var resultArray = new IntersectionResultArray();
                var setCompResult = extendedLine.Intersect(targetCurve, out resultArray);

                if (setCompResult != SetComparisonResult.Overlap || resultArray == null || resultArray.Size == 0)
                {
                    return ResponseBuilder.Error("Wall line does not intersect with target element", "INVALID_GEOMETRY").Build();
                }

                var intersectionPoint = resultArray.get_Item(0).XYZPoint;

                using (var trans = new Transaction(doc, "Extend Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create new line with extended endpoint
                    Line newLine;
                    if (endIndex == 0)
                    {
                        newLine = Line.CreateBound(intersectionPoint, wallEnd);
                    }
                    else
                    {
                        newLine = Line.CreateBound(wallStart, intersectionPoint);
                    }

                    locationCurve.Curve = newLine;

                    trans.Commit();

                    var updatedCurve = (wall.Location as LocationCurve).Curve;

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .With("endIndex", endIndex)
                        .With("targetElementId", (int)targetElementId.Value)
                        .With("intersectionPoint", new[] { intersectionPoint.X, intersectionPoint.Y, intersectionPoint.Z })
                        .With("newLength", updatedCurve.Length)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Trim a wall at another element (wall, grid, reference plane).
        /// </summary>
        /// <param name="wallId">ID of the wall to trim</param>
        /// <param name="endIndex">0 for start point, 1 for end point</param>
        /// <param name="targetElementId">ID of the element to trim to</param>
        [MCPMethod("trimWallToElement", Category = "Wall", Description = "Trim a wall to another element")]
        public static string TrimWallToElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return ResponseBuilder.Error("No active document. Please ensure a Revit project is open.", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var endIndex = int.Parse(parameters["endIndex"].ToString());
                var targetElementId = new ElementId(int.Parse(parameters["targetElementId"].ToString()));

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error($"Wall with ID {wallId.Value} not found", "ELEMENT_NOT_FOUND").Build();
                }

                var targetElement = doc.GetElement(targetElementId);
                if (targetElement == null)
                {
                    return ResponseBuilder.Error($"Target element with ID {targetElementId.Value} not found", "ELEMENT_NOT_FOUND").Build();
                }

                var locationCurve = wall.Location as LocationCurve;
                if (locationCurve == null)
                {
                    return ResponseBuilder.Error("Wall does not have a valid location curve", "INVALID_GEOMETRY").Build();
                }

                var curve = locationCurve.Curve as Line;
                if (curve == null)
                {
                    return ResponseBuilder.Error("Wall curve is not a line (curved walls not supported)", "INVALID_GEOMETRY").Build();
                }

                // Get target curve
                Curve targetCurve = null;
                if (targetElement is Wall targetWall)
                {
                    var targetLocation = targetWall.Location as LocationCurve;
                    targetCurve = targetLocation?.Curve;
                }
                else if (targetElement is Grid grid)
                {
                    targetCurve = grid.Curve;
                }
                else if (targetElement is ReferencePlane refPlane)
                {
                    var bubbleEnd = refPlane.BubbleEnd;
                    var freeEnd = refPlane.FreeEnd;
                    targetCurve = Line.CreateBound(bubbleEnd, freeEnd);
                }

                if (targetCurve == null)
                {
                    return ResponseBuilder.Error("Target element does not have a valid curve for intersection", "INVALID_GEOMETRY").Build();
                }

                // Find intersection
                var resultArray = new IntersectionResultArray();
                var setCompResult = curve.Intersect(targetCurve, out resultArray);

                if (setCompResult != SetComparisonResult.Overlap || resultArray == null || resultArray.Size == 0)
                {
                    return ResponseBuilder.Error("Wall does not intersect with target element", "INVALID_GEOMETRY").Build();
                }

                var intersectionPoint = resultArray.get_Item(0).XYZPoint;
                var wallStart = curve.GetEndPoint(0);
                var wallEnd = curve.GetEndPoint(1);

                using (var trans = new Transaction(doc, "Trim Wall"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create new line trimmed at intersection
                    Line newLine;
                    if (endIndex == 0)
                    {
                        // Trim from start - new start is intersection point
                        newLine = Line.CreateBound(intersectionPoint, wallEnd);
                    }
                    else
                    {
                        // Trim from end - new end is intersection point
                        newLine = Line.CreateBound(wallStart, intersectionPoint);
                    }

                    locationCurve.Curve = newLine;

                    trans.Commit();

                    var updatedCurve = (wall.Location as LocationCurve).Curve;

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .With("endIndex", endIndex)
                        .With("targetElementId", (int)targetElementId.Value)
                        .With("trimPoint", new[] { intersectionPoint.X, intersectionPoint.Y, intersectionPoint.Z })
                        .With("newLength", updatedCurve.Length)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extend or trim a wall to a specific point along its direction.
        /// Simpler version that just moves endpoint to a specified coordinate.
        /// </summary>
        /// <param name="wallId">ID of the wall to modify</param>
        /// <param name="endIndex">0 for start point, 1 for end point</param>
        /// <param name="targetPoint">Target point [x, y, z] - wall endpoint will project to this</param>
        [MCPMethod("extendWallToPoint", Category = "Wall", Description = "Extend a wall to a specific XYZ point")]
        public static string ExtendWallToPoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return ResponseBuilder.Error("No active document. Please ensure a Revit project is open.", "NO_ACTIVE_DOCUMENT").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var endIndex = int.Parse(parameters["endIndex"].ToString());
                var targetPoint = parameters["targetPoint"].ToObject<double[]>();

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error($"Wall with ID {wallId.Value} not found", "ELEMENT_NOT_FOUND").Build();
                }

                var locationCurve = wall.Location as LocationCurve;
                if (locationCurve == null)
                {
                    return ResponseBuilder.Error("Wall does not have a valid location curve", "INVALID_GEOMETRY").Build();
                }

                var curve = locationCurve.Curve as Line;
                if (curve == null)
                {
                    return ResponseBuilder.Error("Wall curve is not a line", "INVALID_GEOMETRY").Build();
                }

                var wallStart = curve.GetEndPoint(0);
                var wallEnd = curve.GetEndPoint(1);
                var targetXYZ = new XYZ(targetPoint[0], targetPoint[1], targetPoint[2]);

                // Project target point onto wall line (keep wall straight)
                var wallDirection = (wallEnd - wallStart).Normalize();
                var toTarget = targetXYZ - (endIndex == 0 ? wallEnd : wallStart);
                var projectedDistance = toTarget.DotProduct(endIndex == 0 ? -wallDirection : wallDirection);
                var projectedPoint = (endIndex == 0 ? wallStart : wallEnd) + wallDirection * (endIndex == 0 ? -projectedDistance : projectedDistance);

                // For simplicity, just use the X,Y of target with Z from wall
                var newEndpoint = new XYZ(targetPoint[0], targetPoint[1], endIndex == 0 ? wallStart.Z : wallEnd.Z);

                using (var trans = new Transaction(doc, "Extend/Trim Wall to Point"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    Line newLine;
                    if (endIndex == 0)
                    {
                        newLine = Line.CreateBound(newEndpoint, wallEnd);
                    }
                    else
                    {
                        newLine = Line.CreateBound(wallStart, newEndpoint);
                    }

                    locationCurve.Curve = newLine;

                    trans.Commit();

                    var updatedCurve = (wall.Location as LocationCurve).Curve;
                    var updatedStart = updatedCurve.GetEndPoint(0);
                    var updatedEnd = updatedCurve.GetEndPoint(1);

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wallId.Value)
                        .With("endIndex", endIndex)
                        .With("newEndpoint", new[] { newEndpoint.X, newEndpoint.Y, newEndpoint.Z })
                        .With("updatedStartPoint", new[] { updatedStart.X, updatedStart.Y, updatedStart.Z })
                        .With("updatedEndPoint", new[] { updatedEnd.X, updatedEnd.Y, updatedEnd.Z })
                        .With("newLength", updatedCurve.Length)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
