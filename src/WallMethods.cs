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

                // DUPLICATE GUARD (hard rule): never draw a wall ON TOP of an existing one on the
                // same level — earlier work (user's or a prior agent pass) must be reused, not
                // double-drawn. allowDuplicate:true bypasses ONLY for intentional stacking.
                bool allowDuplicate = parameters["allowDuplicate"]?.ToObject<bool>() ?? false;
                if (!allowDuplicate)
                {
                    var dup = FindWallOnSegment(doc,
                        new XYZ(startPoint[0], startPoint[1], 0), new XYZ(endPoint[0], endPoint[1], 0), level.Elevation);
                    if (dup != null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            errorCode = "DUPLICATE_WALL",
                            existingWallId = (int)dup.Id.Value,
                            existingWallType = dup.Name,
                            error = "A wall already runs along this line on this level (id " + dup.Id.Value + ", '" + dup.Name +
                                    "'). NOT drawing on top of it — reuse or modify the existing wall instead, or pass allowDuplicate:true only if stacking is intentional."
                        });
                    }
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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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
        /// Create or update a Basic Wall type with a requested total thickness.
        /// </summary>
        [MCPMethod("createBasicWallType", Category = "Wall", Description = "Create or update a Basic Wall type with a requested total thickness")]
        public static string CreateBasicWallType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var newTypeName = parameters["newTypeName"]?.ToString();
                if (string.IsNullOrWhiteSpace(newTypeName))
                    return ResponseBuilder.Error("newTypeName is required", "MISSING_PARAMETER").Build();

                var widthFt = parameters["widthFt"] != null
                    ? parameters["widthFt"].ToObject<double>()
                    : parameters["widthInches"] != null
                        ? parameters["widthInches"].ToObject<double>() / 12.0
                        : 0.0;

                if (widthFt <= 0)
                    return ResponseBuilder.Error("widthFt or widthInches must be greater than zero", "INVALID_PARAMETER").Build();

                WallType sourceType = null;
                if (parameters["sourceTypeId"] != null)
                {
                    sourceType = doc.GetElement(new ElementId(parameters["sourceTypeId"].ToObject<int>())) as WallType;
                }

                if (sourceType == null && parameters["sourceTypeName"] != null)
                {
                    var sourceTypeName = parameters["sourceTypeName"].ToString();
                    sourceType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Name == sourceTypeName && wt.Kind == WallKind.Basic);
                }

                if (sourceType == null)
                {
                    sourceType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                }

                if (sourceType == null)
                    return ResponseBuilder.Error("No Basic Wall source type found", "TYPE_NOT_FOUND").Build();

                WallType targetType = null;

                using (var trans = new Transaction(doc, "Create Basic Wall Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    targetType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Name == newTypeName && wt.Kind == WallKind.Basic);

                    if (targetType == null)
                    {
                        targetType = sourceType.Duplicate(newTypeName) as WallType;
                    }

                    var structure = targetType.GetCompoundStructure();
                    if (structure == null || structure.LayerCount == 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Target wall type has no editable compound structure", "INVALID_STRUCTURE").Build();
                    }

                    var layers = structure.GetLayers();
                    var adjustableLayerIndex = 0;
                    var maxLayerWidth = double.MinValue;
                    var fixedWidth = 0.0;

                    for (var i = 0; i < layers.Count; i++)
                    {
                        if (layers[i].Width > maxLayerWidth)
                        {
                            maxLayerWidth = layers[i].Width;
                            adjustableLayerIndex = i;
                        }
                    }

                    for (var i = 0; i < layers.Count; i++)
                    {
                        if (i != adjustableLayerIndex)
                            fixedWidth += layers[i].Width;
                    }

                    var adjustableWidth = widthFt - fixedWidth;
                    if (adjustableWidth <= 0)
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error($"Requested width {widthFt} ft is not greater than fixed layer width {fixedWidth} ft", "INVALID_WIDTH").Build();
                    }

                    structure.SetLayerWidth(adjustableLayerIndex, adjustableWidth);
                    targetType.SetCompoundStructure(structure);

                    trans.CommitAndCheck();
                }

                return ResponseBuilder.Success()
                    .With("wallTypeId", (int)targetType.Id.Value)
                    .With("wallTypeName", targetType.Name)
                    .With("width", targetType.Width)
                    .With("widthInches", targetType.Width * 12.0)
                    .With("sourceTypeId", (int)sourceType.Id.Value)
                    .With("sourceTypeName", sourceType.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a rectangular opening hosted by a wall.
        /// </summary>
        [MCPMethod("createWallOpening", Category = "Wall", Description = "Create a rectangular opening hosted by a wall")]
        public static string CreateWallOpening(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(parameters["wallId"].ToObject<int>());
                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                XYZ p1;
                XYZ p2;

                if (parameters["startPoint"] != null && parameters["endPoint"] != null)
                {
                    var start = parameters["startPoint"].ToObject<double[]>();
                    var end = parameters["endPoint"].ToObject<double[]>();
                    p1 = new XYZ(start[0], start[1], start[2]);
                    p2 = new XYZ(end[0], end[1], end[2]);
                }
                else
                {
                    var center = parameters["centerPoint"].ToObject<double[]>();
                    var width = parameters["width"]?.ToObject<double>() ?? 3.0;
                    var sillHeight = parameters["sillHeight"]?.ToObject<double>() ?? 0.0;
                    var height = parameters["height"]?.ToObject<double>() ?? 7.0;

                    var locationCurve = wall.Location as LocationCurve;
                    var line = locationCurve?.Curve as Line;
                    if (line == null)
                        return ResponseBuilder.Error("Wall has no straight location curve", "INVALID_WALL").Build();

                    var direction = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
                    var centerPoint = new XYZ(center[0], center[1], center.Length > 2 ? center[2] : 0.0);
                    var halfWidth = width / 2.0;
                    p1 = centerPoint - direction * halfWidth + XYZ.BasisZ * sillHeight;
                    p2 = centerPoint + direction * halfWidth + XYZ.BasisZ * (sillHeight + height);
                }

                using (var trans = new Transaction(doc, "Create Wall Opening"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var opening = doc.Create.NewOpening(wall, p1, p2);

                    trans.CommitAndCheck();

                    return ResponseBuilder.Success()
                        .With("openingId", (int)opening.Id.Value)
                        .With("wallId", (int)wall.Id.Value)
                        .With("startPoint", new[] { p1.X, p1.Y, p1.Z })
                        .With("endPoint", new[] { p2.X, p2.Y, p2.Z })
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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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
        [MCPMethod("modifyWallType", Category = "Wall", Description = "Change which wall type a wall instance uses (requires wallId and newTypeId). To author or edit type layers, use createCompoundWallType.")]
        public static string ModifyWallType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["wallId"] == null || parameters["newTypeId"] == null)
                {
                    return ResponseBuilder.Error(
                        "wallId and newTypeId are required. This method changes which type a wall instance uses; to author or edit type layers, use createCompoundWallType.",
                        "INVALID_PARAMETER").Build();
                }

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

        // ===================================================================
        // COMPOUND (multi-layer) WALL TYPES  — finishes / insulation / core
        // ===================================================================

        /// <summary>Get-or-create a Material by name. Returns Invalid id if name blank.</summary>
        private static ElementId EnsureMaterialId(Document doc, string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
                return ElementId.InvalidElementId;
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase));
            return existing != null ? existing.Id : Material.Create(doc, materialName);
        }

        /// <summary>Map a layer-function string to a Revit MaterialFunctionAssignment.</summary>
        private static MaterialFunctionAssignment ParseLayerFunction(string fn)
        {
            switch ((fn ?? "").Trim().ToLowerInvariant())
            {
                case "structure": case "core": return MaterialFunctionAssignment.Structure;
                case "substrate": return MaterialFunctionAssignment.Substrate;
                case "insulation": case "thermal": case "thermal/air": case "thermalair": case "air":
                    return MaterialFunctionAssignment.Insulation;
                case "finish1": case "finishexterior": case "exteriorfinish": case "finish_ext":
                    return MaterialFunctionAssignment.Finish1;
                case "finish2": case "finishinterior": case "interiorfinish": case "finish_int":
                    return MaterialFunctionAssignment.Finish2;
                case "membrane": return MaterialFunctionAssignment.Membrane;
                case "structuraldeck": case "deck": return MaterialFunctionAssignment.StructuralDeck;
                default: return MaterialFunctionAssignment.Finish2;
            }
        }

        /// <summary>
        /// Create (or replace) a multi-layer wall type with finish/insulation/structure layers.
        /// layers[] are ordered EXTERIOR -> INTERIOR; each: {function, thicknessInches|thicknessFt, materialName?}.
        /// Core is inferred from Structure-function layers unless exteriorShellCount/interiorShellCount given.
        /// Optional wallFunction: interior|exterior|foundation|retaining|soffit|coreshaft.
        /// </summary>
        [MCPMethod("createCompoundWallType", Category = "Wall", Description = "Create a multi-layer wall type (finishes, insulation, core) with materials")]
        public static string CreateCompoundWallType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var newTypeName = parameters["newTypeName"]?.ToString();
                if (string.IsNullOrWhiteSpace(newTypeName))
                    return ResponseBuilder.Error("newTypeName is required", "MISSING_PARAMETER").Build();

                var layerArr = parameters["layers"] as JArray;
                if (layerArr == null || layerArr.Count == 0)
                    return ResponseBuilder.Error("layers array is required (ordered exterior->interior)", "MISSING_PARAMETER").Build();

                WallType sourceType = null;
                if (parameters["sourceTypeId"] != null)
                    sourceType = doc.GetElement(new ElementId(parameters["sourceTypeId"].ToObject<long>())) as WallType;
                if (sourceType == null && parameters["sourceTypeName"] != null)
                {
                    var sn = parameters["sourceTypeName"].ToString();
                    sourceType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .FirstOrDefault(wt => wt.Name == sn && wt.Kind == WallKind.Basic);
                }
                if (sourceType == null)
                    sourceType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                if (sourceType == null)
                    return ResponseBuilder.Error("No Basic Wall source type found", "TYPE_NOT_FOUND").Build();

                WallType targetType = null;
                bool replacedExisting = false;
                var layerInfo = new List<object>();
                double total = 0;

                using (var trans = new Transaction(doc, "Create Compound Wall Type"))
                {
                    trans.Start();
                    var fo = trans.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(fo);

                    targetType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .FirstOrDefault(wt => wt.Name == newTypeName && wt.Kind == WallKind.Basic);
                    replacedExisting = targetType != null;
                    if (targetType == null)
                        targetType = sourceType.Duplicate(newTypeName) as WallType;
                    if (targetType == null)
                        return ResponseBuilder.Error($"Failed to duplicate source type '{sourceType.Name}'", "OPERATION_FAILED").Build();

                    var layers = new List<CompoundStructureLayer>();
                    int firstStruct = -1, lastStruct = -1;
                    for (int i = 0; i < layerArr.Count; i++)
                    {
                        var l = (JObject)layerArr[i];
                        var fn = ParseLayerFunction(l["function"]?.ToString());
                        double widthFt = l["thicknessFt"] != null ? l["thicknessFt"].ToObject<double>()
                            : l["thicknessInches"] != null ? l["thicknessInches"].ToObject<double>() / 12.0
                            : 0.0;
                        if (fn == MaterialFunctionAssignment.Membrane)
                        {
                            widthFt = 0.0; // membranes must be zero-width
                        }
                        else if (widthFt <= 0)
                        {
                            // Zero/negative width makes the structure invalid and
                            // SetCompoundStructure would throw a cryptic
                            // ArgumentException mid-transaction
                            return ResponseBuilder.Error(
                                $"layers[{i}] ({fn}): thickness must be > 0 — pass thicknessInches or thicknessFt (only membrane layers may be zero-width)",
                                "INVALID_PARAMETER").Build();
                        }
                        var matName = l["materialName"]?.ToString();
                        var matId = EnsureMaterialId(doc, matName);
                        layers.Add(new CompoundStructureLayer(widthFt, fn, matId));
                        if (fn == MaterialFunctionAssignment.Structure)
                        {
                            if (firstStruct < 0) firstStruct = i;
                            lastStruct = i;
                        }
                        total += widthFt;
                        layerInfo.Add(new {
                            index = i,
                            function = fn.ToString(),
                            thicknessInches = Math.Round(widthFt * 12.0, 4),
                            material = string.IsNullOrWhiteSpace(matName) ? "None" : matName
                        });
                    }

                    var cs = CompoundStructure.CreateSimpleCompoundStructure(layers);

                    int extShell = parameters["exteriorShellCount"] != null
                        ? parameters["exteriorShellCount"].ToObject<int>()
                        : (firstStruct < 0 ? 0 : firstStruct);
                    int intShell = parameters["interiorShellCount"] != null
                        ? parameters["interiorShellCount"].ToObject<int>()
                        : (lastStruct < 0 ? 0 : (layers.Count - 1 - lastStruct));

                    if (extShell < 0 || intShell < 0)
                        return ResponseBuilder.Error(
                            $"exteriorShellCount/interiorShellCount must be >= 0 (got {extShell}/{intShell})",
                            "INVALID_PARAMETER").Build();

                    // Must leave at least one core layer between the shells —
                    // error instead of silently leaving every layer in the core.
                    if (extShell + intShell > layers.Count - 1)
                        return ResponseBuilder.Error(
                            $"Shell layers ({extShell} exterior + {intShell} interior) must leave at least one core layer out of {layers.Count} total",
                            "INVALID_PARAMETER").Build();

                    if (extShell > 0) cs.SetNumberOfShellLayers(ShellLayerType.Exterior, extShell);
                    if (intShell > 0) cs.SetNumberOfShellLayers(ShellLayerType.Interior, intShell);

                    // Validate before touching the wall type so the caller gets
                    // the per-layer error map instead of a raw Revit exception
                    IDictionary<int, CompoundStructureError> csErrors;
                    IDictionary<int, int> twoLayerErrors;
                    if (!cs.IsValid(doc, out csErrors, out twoLayerErrors))
                    {
                        var details = csErrors != null
                            ? string.Join("; ", csErrors.Select(kv => $"layers[{kv.Key}]: {kv.Value}"))
                            : "unknown";
                        return ResponseBuilder.Error(
                            $"Compound structure is invalid: {details}",
                            "INVALID_STRUCTURE").Build();
                    }

                    targetType.SetCompoundStructure(cs);

                    var fnParam = parameters["wallFunction"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(fnParam))
                    {
                        int wf = -1;
                        switch (fnParam.Trim().ToLowerInvariant())
                        {
                            case "interior": wf = (int)WallFunction.Interior; break;
                            case "exterior": wf = (int)WallFunction.Exterior; break;
                            case "foundation": wf = (int)WallFunction.Foundation; break;
                            case "retaining": wf = (int)WallFunction.Retaining; break;
                            case "soffit": wf = (int)WallFunction.Soffit; break;
                            case "coreshaft": case "core-shaft": wf = (int)WallFunction.Coreshaft; break;
                        }
                        if (wf >= 0)
                            targetType.get_Parameter(BuiltInParameter.FUNCTION_PARAM)?.Set(wf);
                    }

                    trans.CommitAndCheck();
                }

                return ResponseBuilder.Success()
                    .With("wallTypeId", (int)targetType.Id.Value)
                    .With("wallTypeName", targetType.Name)
                    .With("replacedExisting", replacedExisting)
                    .With("totalThicknessInches", Math.Round(total * 12.0, 4))
                    .With("layerCount", layerInfo.Count)
                    .With("layers", layerInfo)
                    .Build();
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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

                    trans.CommitAndCheck();

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

        #region Precision Wall Methods

        /// <summary>
        /// Get precision geometry for a placed wall: centerline, both face positions,
        /// corner points, and wall type thickness data.
        /// This is the "measure" step for precision wall placement.
        /// </summary>
        [MCPMethod("getWallPrecisionGeometry", Category = "Wall", Description = "Get exact wall face positions, centerline, and corner geometry for precision placement")]
        public static string GetWallPrecisionGeometry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["wallId"] == null)
                    return ResponseBuilder.Error("wallId is required").Build();

                var wallId = new ElementId(parameters["wallId"].ToObject<int>());
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                var locationCurve = wall.Location as LocationCurve;
                if (locationCurve == null)
                    return ResponseBuilder.Error("Wall has no location curve").Build();

                var curve = locationCurve.Curve as Line;
                if (curve == null)
                    return ResponseBuilder.Error("Wall is not a straight line").Build();

                var startPt = curve.GetEndPoint(0);
                var endPt = curve.GetEndPoint(1);
                double wallWidth = wall.Width;
                double halfWidth = wallWidth / 2.0;

                // Wall direction and perpendicular (for face offset)
                XYZ wallDir = (endPt - startPt).Normalize();
                // Perpendicular: rotate 90 degrees CCW in plan
                XYZ perpDir = new XYZ(-wallDir.Y, wallDir.X, 0);

                // Determine which side is exterior vs interior
                // In Revit, the "exterior" face is on the positive perpendicular side
                // when wall is not flipped. If flipped, it's reversed.
                bool isFlipped = wall.Flipped;
                XYZ exteriorDir = isFlipped ? -perpDir : perpDir;
                XYZ interiorDir = isFlipped ? perpDir : -perpDir;

                // Centerline points
                var centerStart = new double[] { Math.Round(startPt.X, 6), Math.Round(startPt.Y, 6), Math.Round(startPt.Z, 6) };
                var centerEnd = new double[] { Math.Round(endPt.X, 6), Math.Round(endPt.Y, 6), Math.Round(endPt.Z, 6) };

                // Exterior face points (offset from centerline by halfWidth)
                XYZ extStart = startPt + exteriorDir * halfWidth;
                XYZ extEnd = endPt + exteriorDir * halfWidth;
                var exteriorStart = new double[] { Math.Round(extStart.X, 6), Math.Round(extStart.Y, 6), Math.Round(extStart.Z, 6) };
                var exteriorEnd = new double[] { Math.Round(extEnd.X, 6), Math.Round(extEnd.Y, 6), Math.Round(extEnd.Z, 6) };

                // Interior face points
                XYZ intStart = startPt + interiorDir * halfWidth;
                XYZ intEnd = endPt + interiorDir * halfWidth;
                var interiorStart = new double[] { Math.Round(intStart.X, 6), Math.Round(intStart.Y, 6), Math.Round(intStart.Z, 6) };
                var interiorEnd = new double[] { Math.Round(intEnd.X, 6), Math.Round(intEnd.Y, 6), Math.Round(intEnd.Z, 6) };

                // Wall type info
                var wallType = wall.WallType;
                string function = "Unknown";
                try { function = wallType.Function.ToString(); } catch { }

                // Get layer info if compound
                var layers = new List<object>();
                var structure = wallType.GetCompoundStructure();
                if (structure != null)
                {
                    double cumOffset = 0;
                    for (int i = 0; i < structure.LayerCount; i++)
                    {
                        var layer = structure.GetLayers()[i];
                        var material = doc.GetElement(layer.MaterialId) as Material;
                        layers.Add(new
                        {
                            index = i,
                            function = layer.Function.ToString(),
                            thickness = Math.Round(layer.Width, 6),
                            thicknessInches = Math.Round(layer.Width * 12, 4),
                            materialName = material?.Name ?? "None",
                            offsetFromExterior = Math.Round(cumOffset, 6)
                        });
                        cumOffset += layer.Width;
                    }
                }

                // Height and level
                double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
                var level = doc.GetElement(wall.LevelId) as Level;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    wallId = (int)wall.Id.Value,
                    wallTypeName = wallType.Name,
                    wallTypeId = (int)wallType.Id.Value,
                    function = function,
                    isFlipped = isFlipped,
                    width = Math.Round(wallWidth, 6),
                    widthInches = Math.Round(wallWidth * 12, 4),
                    halfWidth = Math.Round(halfWidth, 6),
                    height = Math.Round(height, 6),
                    length = Math.Round(curve.Length, 6),
                    level = level?.Name,
                    centerline = new { start = centerStart, end = centerEnd },
                    exteriorFace = new { start = exteriorStart, end = exteriorEnd },
                    interiorFace = new { start = interiorStart, end = interiorEnd },
                    direction = new double[] { Math.Round(wallDir.X, 6), Math.Round(wallDir.Y, 6), 0 },
                    perpendicularToExterior = new double[] { Math.Round(exteriorDir.X, 6), Math.Round(exteriorDir.Y, 6), 0 },
                    layers = layers
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get precision geometry for ALL walls in the document or a specific view.
        /// Returns face positions for every wall so AI can compute exact connection points.
        /// </summary>
        [MCPMethod("getWallsPrecisionGeometry", Category = "Wall", Description = "Get precision geometry for all walls (faces, corners, thickness)")]
        public static string GetWallsPrecisionGeometry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Optional view filter
                FilteredElementCollector collector;
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(parameters["viewId"].ToObject<int>());
                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var walls = collector
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                var results = new List<object>();

                foreach (var wall in walls)
                {
                    try
                    {
                        var locationCurve = wall.Location as LocationCurve;
                        if (locationCurve == null) continue;

                        var curve = locationCurve.Curve as Line;
                        if (curve == null) continue;

                        var startPt = curve.GetEndPoint(0);
                        var endPt = curve.GetEndPoint(1);
                        double wallWidth = wall.Width;
                        double halfWidth = wallWidth / 2.0;

                        XYZ wallDir = (endPt - startPt).Normalize();
                        XYZ perpDir = new XYZ(-wallDir.Y, wallDir.X, 0);

                        bool isFlipped = wall.Flipped;
                        XYZ exteriorDir = isFlipped ? -perpDir : perpDir;
                        XYZ interiorDir = isFlipped ? perpDir : -perpDir;

                        XYZ extStart = startPt + exteriorDir * halfWidth;
                        XYZ extEnd = endPt + exteriorDir * halfWidth;
                        XYZ intStart = startPt + interiorDir * halfWidth;
                        XYZ intEnd = endPt + interiorDir * halfWidth;

                        var level = doc.GetElement(wall.LevelId) as Level;

                        results.Add(new
                        {
                            wallId = (int)wall.Id.Value,
                            wallTypeName = wall.WallType.Name,
                            width = Math.Round(wallWidth, 6),
                            halfWidth = Math.Round(halfWidth, 6),
                            length = Math.Round(curve.Length, 6),
                            level = level?.Name,
                            isFlipped = isFlipped,
                            centerline = new
                            {
                                startX = Math.Round(startPt.X, 6), startY = Math.Round(startPt.Y, 6),
                                endX = Math.Round(endPt.X, 6), endY = Math.Round(endPt.Y, 6)
                            },
                            exteriorFace = new
                            {
                                startX = Math.Round(extStart.X, 6), startY = Math.Round(extStart.Y, 6),
                                endX = Math.Round(extEnd.X, 6), endY = Math.Round(extEnd.Y, 6)
                            },
                            interiorFace = new
                            {
                                startX = Math.Round(intStart.X, 6), startY = Math.Round(intStart.Y, 6),
                                endX = Math.Round(intEnd.X, 6), endY = Math.Round(intEnd.Y, 6)
                            }
                        });
                    }
                    catch { /* skip problematic walls */ }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    wallCount = results.Count,
                    walls = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a wall by specifying where a FACE goes, not the centerline.
        /// Automatically computes the centerline offset based on wall type thickness.
        /// This eliminates the guesswork of "where will the face actually be?"
        /// </summary>
        [MCPMethod("createWallByFace", Category = "Wall", Description = "Create a wall by specifying face position (exterior or interior) instead of centerline")]
        public static string CreateWallByFace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Required params
                if (parameters["faceStartPoint"] == null || parameters["faceEndPoint"] == null ||
                    parameters["levelId"] == null)
                    return ResponseBuilder.Error("faceStartPoint, faceEndPoint, and levelId are required").Build();

                var faceStart = parameters["faceStartPoint"].ToObject<double[]>();
                var faceEnd = parameters["faceEndPoint"].ToObject<double[]>();
                var levelIdInt = parameters["levelId"].ToObject<int>();
                var level = doc.GetElement(new ElementId(levelIdInt)) as Level;
                if (level == null)
                    return ResponseBuilder.Error("Level not found").Build();

                // Which face are we specifying? "exterior" (default) or "interior"
                string faceType = parameters["faceType"]?.ToString()?.ToLower() ?? "exterior";
                double height = parameters["height"]?.ToObject<double>() ?? 10.0;

                // Resolve wall type
                WallType wallType = null;
                if (parameters["wallTypeId"] != null)
                    wallType = doc.GetElement(new ElementId(parameters["wallTypeId"].ToObject<int>())) as WallType;
                else if (parameters["wallTypeName"] != null)
                {
                    string name = parameters["wallTypeName"].ToString();
                    wallType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Name == name);
                }

                if (wallType == null)
                    wallType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault();

                if (wallType == null)
                    return ResponseBuilder.Error("No wall type available").Build();

                double wallWidth = wallType.Width;
                double halfWidth = wallWidth / 2.0;

                // Compute centerline from face position
                // Wall direction: from faceStart to faceEnd
                XYZ faceStartPt = new XYZ(faceStart[0], faceStart[1], faceStart.Length > 2 ? faceStart[2] : 0);
                XYZ faceEndPt = new XYZ(faceEnd[0], faceEnd[1], faceEnd.Length > 2 ? faceEnd[2] : 0);
                XYZ wallDir = (faceEndPt - faceStartPt).Normalize();

                // Perpendicular direction (CCW rotation for exterior offset)
                XYZ perpDir = new XYZ(-wallDir.Y, wallDir.X, 0);

                // Offset from face to centerline
                XYZ offset;
                if (faceType == "exterior")
                {
                    // Exterior face is at the specified position
                    // Centerline is halfWidth INWARD (opposite to exterior normal)
                    // In Revit, exterior is on the positive perpendicular side (unflipped)
                    // So centerline = face - perpDir * halfWidth
                    offset = -perpDir * halfWidth;
                }
                else // interior
                {
                    // Interior face specified, centerline is halfWidth toward exterior
                    offset = perpDir * halfWidth;
                }

                XYZ centerStart = faceStartPt + offset;
                XYZ centerEnd = faceEndPt + offset;

                using (var trans = new Transaction(doc, "Create Wall By Face"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var line = Line.CreateBound(centerStart, centerEnd);
                    var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);

                    // Verify: compute actual face positions
                    var actualCurve = (wall.Location as LocationCurve)?.Curve as Line;
                    var actStart = actualCurve?.GetEndPoint(0) ?? centerStart;
                    var actEnd = actualCurve?.GetEndPoint(1) ?? centerEnd;

                    XYZ actExtStart = actStart + perpDir * halfWidth;
                    XYZ actExtEnd = actEnd + perpDir * halfWidth;
                    XYZ actIntStart = actStart - perpDir * halfWidth;
                    XYZ actIntEnd = actEnd - perpDir * halfWidth;

                    trans.CommitAndCheck();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        wallId = (int)wall.Id.Value,
                        wallTypeName = wallType.Name,
                        width = Math.Round(wallWidth, 6),
                        widthInches = Math.Round(wallWidth * 12, 4),
                        halfWidth = Math.Round(halfWidth, 6),
                        height = height,
                        level = level.Name,
                        faceTypeSpecified = faceType,
                        centerline = new
                        {
                            start = new double[] { Math.Round(actStart.X, 6), Math.Round(actStart.Y, 6), Math.Round(actStart.Z, 6) },
                            end = new double[] { Math.Round(actEnd.X, 6), Math.Round(actEnd.Y, 6), Math.Round(actEnd.Z, 6) }
                        },
                        exteriorFace = new
                        {
                            start = new double[] { Math.Round(actExtStart.X, 6), Math.Round(actExtStart.Y, 6), Math.Round(actExtStart.Z, 6) },
                            end = new double[] { Math.Round(actExtEnd.X, 6), Math.Round(actExtEnd.Y, 6), Math.Round(actExtEnd.Z, 6) }
                        },
                        interiorFace = new
                        {
                            start = new double[] { Math.Round(actIntStart.X, 6), Math.Round(actIntStart.Y, 6), Math.Round(actIntStart.Z, 6) },
                            end = new double[] { Math.Round(actIntEnd.X, 6), Math.Round(actIntEnd.Y, 6), Math.Round(actIntEnd.Z, 6) }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get wall type thickness and face offset data WITHOUT placing any walls.
        /// Use this to pre-compute wall positions before placement.
        /// </summary>
        [MCPMethod("getWallTypeThickness", Category = "Wall", Description = "Get wall type thickness and centerline-to-face offset for precision placement")]
        public static string GetWallTypeThickness(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Accept single type or batch
                var results = new List<object>();

                if (parameters["wallTypeId"] != null || parameters["wallTypeName"] != null)
                {
                    // Single type lookup
                    WallType wt = null;
                    if (parameters["wallTypeId"] != null)
                        wt = doc.GetElement(new ElementId(parameters["wallTypeId"].ToObject<int>())) as WallType;
                    else
                    {
                        string name = parameters["wallTypeName"].ToString();
                        wt = new FilteredElementCollector(doc)
                            .OfClass(typeof(WallType))
                            .Cast<WallType>()
                            .FirstOrDefault(w => w.Name == name);
                    }

                    if (wt == null)
                        return ResponseBuilder.Error("Wall type not found").Build();

                    results.Add(BuildWallTypeThicknessInfo(doc, wt));
                }
                else if (parameters["wallTypeIds"] != null)
                {
                    // Batch lookup
                    var ids = parameters["wallTypeIds"].ToObject<List<int>>();
                    foreach (int id in ids)
                    {
                        var wt = doc.GetElement(new ElementId(id)) as WallType;
                        if (wt != null)
                            results.Add(BuildWallTypeThicknessInfo(doc, wt));
                    }
                }
                else
                {
                    // Return all wall types
                    var allTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .ToList();

                    foreach (var wt in allTypes)
                    {
                        results.Add(BuildWallTypeThicknessInfo(doc, wt));
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = results.Count,
                    wallTypes = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object BuildWallTypeThicknessInfo(Document doc, WallType wt)
        {
            double width = wt.Width;
            double halfWidth = width / 2.0;

            var layers = new List<object>();
            var structure = wt.GetCompoundStructure();
            if (structure != null)
            {
                double cumOffset = 0;
                for (int i = 0; i < structure.LayerCount; i++)
                {
                    var layer = structure.GetLayers()[i];
                    var material = doc.GetElement(layer.MaterialId) as Material;
                    layers.Add(new
                    {
                        index = i,
                        function = layer.Function.ToString(),
                        thickness = Math.Round(layer.Width, 6),
                        thicknessInches = Math.Round(layer.Width * 12, 4),
                        materialName = material?.Name ?? "None",
                        offsetFromExterior = Math.Round(cumOffset, 6)
                    });
                    cumOffset += layer.Width;
                }
            }

            string function = "Unknown";
            try { function = wt.Function.ToString(); } catch { }

            return new
            {
                wallTypeId = (int)wt.Id.Value,
                wallTypeName = wt.Name,
                function = function,
                kind = wt.Kind.ToString(),
                totalWidth = Math.Round(width, 6),
                totalWidthInches = Math.Round(width * 12, 4),
                halfWidth = Math.Round(halfWidth, 6),
                halfWidthInches = Math.Round(halfWidth * 12, 4),
                centerlineToExteriorFace = Math.Round(halfWidth, 6),
                centerlineToInteriorFace = Math.Round(halfWidth, 6),
                layerCount = layers.Count,
                layers = layers
            };
        }
        /// <summary>An existing wall that the proposed segment a→b would lie ON TOP of, on the
        /// same level (base elevation within 1 ft): nearly collinear (<~3°), laterally within
        /// 4", sharing ≥1 ft of run. Parallel walls further apart (legit double walls) pass.</summary>
        internal static Wall FindWallOnSegment(Document doc, XYZ a, XYZ b, double baseElevation)
        {
            const double LATERAL_TOL = 0.35, MIN_OVERLAP = 1.0, ELEV_TOL = 1.0;
            try
            {
                double dX = b.X - a.X, dY = b.Y - a.Y;
                double len = Math.Sqrt(dX * dX + dY * dY);
                if (len < 1e-6) return null;
                double ux = dX / len, uy = dY / len;
                foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    var line = (w.Location as LocationCurve)?.Curve as Line;
                    if (line == null) continue;
                    var lvl = doc.GetElement(w.LevelId) as Level;
                    if (lvl != null && Math.Abs(lvl.Elevation - baseElevation) > ELEV_TOL) continue;
                    var p2 = line.GetEndPoint(0); var q = line.GetEndPoint(1);
                    double eX = q.X - p2.X, eY = q.Y - p2.Y;
                    double elen = Math.Sqrt(eX * eX + eY * eY);
                    if (elen < 1e-6) continue;
                    double vx = eX / elen, vy = eY / elen;
                    if (Math.Abs(ux * vy - uy * vx) > 0.05) continue;
                    if (Math.Abs((a.X - p2.X) * vy - (a.Y - p2.Y) * vx) > LATERAL_TOL) continue;
                    double tA = (a.X - p2.X) * vx + (a.Y - p2.Y) * vy;
                    double tB = (b.X - p2.X) * vx + (b.Y - p2.Y) * vy;
                    double lo = Math.Max(Math.Min(tA, tB), 0), hi = Math.Min(Math.Max(tA, tB), elen);
                    if (hi - lo >= MIN_OVERLAP) return w;
                }
            }
            catch { }
            return null;
        }

        #endregion
    }
}
