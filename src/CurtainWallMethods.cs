using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Curtain wall creation, modification, and management methods for MCP Bridge
    /// </summary>
    public static class CurtainWallMethods
    {
        /// <summary>
        /// Get all curtain walls in the document
        /// </summary>
        [MCPMethod("getCurtainWalls", Category = "CurtainWall", Description = "Get all curtain walls in the document")]
        public static string GetCurtainWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var curtainWalls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .Where(w => w.WallType.Kind == WallKind.Curtain)
                    .Select(w =>
                    {
                        var level = doc.GetElement(w.LevelId) as Level;
                        return new
                        {
                            wallId = (int)w.Id.Value,
                            name = w.Name,
                            typeName = w.WallType.Name,
                            length = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0,
                            levelName = level?.Name ?? "Unknown"
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("curtainWalls", curtainWalls)
                    .WithCount(curtainWalls.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detailed info about a specific curtain wall
        /// </summary>
        [MCPMethod("getCurtainWallInfo", Category = "CurtainWall", Description = "Get detailed info about a specific curtain wall")]
        public static string GetCurtainWallInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getCurtainWallInfo");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var curtainGrid = wall.CurtainGrid;
                var wallType = wall.WallType;
                var level = doc.GetElement(wall.LevelId) as Level;
                var curve = (wall.Location as LocationCurve)?.Curve;

                // Grid info
                var uGridIds = curtainGrid?.GetUGridLineIds() ?? new List<ElementId>();
                var vGridIds = curtainGrid?.GetVGridLineIds() ?? new List<ElementId>();
                var panelIds = curtainGrid?.GetPanelIds() ?? new List<ElementId>();
                var mullionIds = curtainGrid?.GetMullionIds() ?? new List<ElementId>();

                // Dimensions
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
                    .With("typeName", wallType.Name)
                    .With("typeId", (int)wallType.Id.Value)
                    .With("level", level?.Name ?? "Unknown")
                    .With("levelId", (int)wall.LevelId.Value)
                    .With("length", wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0)
                    .With("height", wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0)
                    .With("startPoint", startPoint)
                    .With("endPoint", endPoint)
                    .With("uGridLineCount", uGridIds.Count)
                    .With("vGridLineCount", vGridIds.Count)
                    .With("panelCount", panelIds.Count)
                    .With("mullionCount", mullionIds.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all curtain wall types available in the document
        /// </summary>
        [MCPMethod("getCurtainWallTypes", Category = "CurtainWall", Description = "Get all curtain wall types available in the document")]
        public static string GetCurtainWallTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .Where(wt => wt.Kind == WallKind.Curtain)
                    .Select(wt => new
                    {
                        typeId = (int)wt.Id.Value,
                        name = wt.Name
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("curtainWallTypes", types)
                    .WithCount(types.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a curtain wall between two points
        /// </summary>
        [MCPMethod("createCurtainWall", Category = "CurtainWall", Description = "Create a curtain wall between two points")]
        public static string CreateCurtainWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createCurtainWall");
                v.Require("startPoint");
                v.Require("endPoint");
                v.Require("levelId").IsType<int>();
                v.Optional("height").IsPositive();
                v.Optional("typeId").IsType<int>();
                v.ThrowIfInvalid();

                var startPoint = parameters["startPoint"].ToObject<double[]>();
                var endPoint = parameters["endPoint"].ToObject<double[]>();
                var levelIdInt = v.GetRequired<int>("levelId");
                var height = v.GetOptional<double>("height", 10.0);

                var level = doc.GetElement(new ElementId(levelIdInt)) as Level;
                if (level == null)
                    return ResponseBuilder.Error("Level not found", "ELEMENT_NOT_FOUND").Build();

                // Resolve curtain wall type
                WallType wallType = null;
                if (parameters["typeId"] != null)
                {
                    var typeId = new ElementId(v.GetRequired<int>("typeId"));
                    wallType = doc.GetElement(typeId) as WallType;
                    if (wallType == null || wallType.Kind != WallKind.Curtain)
                        return ResponseBuilder.Error("Specified type is not a curtain wall type", "INVALID_WALL_TYPE").Build();
                }
                else
                {
                    wallType = new FilteredElementCollector(doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .FirstOrDefault(wt => wt.Kind == WallKind.Curtain);

                    if (wallType == null)
                        return ResponseBuilder.Error("No curtain wall type found in document", "NO_WALL_TYPE").Build();
                }

                using (var trans = new Transaction(doc, "Create Curtain Wall"))
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
        /// Change the type of a curtain wall
        /// </summary>
        [MCPMethod("modifyCurtainWallType", Category = "CurtainWall", Description = "Change the type of a curtain wall")]
        public static string ModifyCurtainWallType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "modifyCurtainWallType");
                v.Require("wallId").IsType<int>();
                v.Require("newTypeId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var newTypeId = new ElementId(v.GetRequired<int>("newTypeId"));

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var newType = doc.GetElement(newTypeId) as WallType;
                if (newType == null || newType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("New type is not a curtain wall type", "INVALID_WALL_TYPE").Build();

                using (var trans = new Transaction(doc, "Modify Curtain Wall Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var oldTypeName = wall.WallType.Name;
                    wall.WallType = newType;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wall.Id.Value)
                        .With("oldTypeName", oldTypeName)
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
        /// Get all grid lines for a curtain wall
        /// </summary>
        [MCPMethod("getCurtainGridLines", Category = "CurtainWall", Description = "Get all grid lines for a curtain wall (U and V directions)")]
        public static string GetCurtainGridLines(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getCurtainGridLines");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var curtainGrid = wall.CurtainGrid;
                if (curtainGrid == null)
                    return ResponseBuilder.Error("Curtain wall has no grid", "NO_CURTAIN_GRID").Build();

                var uGridLines = curtainGrid.GetUGridLineIds()
                    .Select(id =>
                    {
                        var gridLine = doc.GetElement(id) as CurtainGridLine;
                        var fullCurve = gridLine?.FullCurve;
                        return new
                        {
                            gridLineId = (int)id.Value,
                            direction = "U",
                            startPoint = fullCurve != null ? new[] { fullCurve.GetEndPoint(0).X, fullCurve.GetEndPoint(0).Y, fullCurve.GetEndPoint(0).Z } : null,
                            endPoint = fullCurve != null ? new[] { fullCurve.GetEndPoint(1).X, fullCurve.GetEndPoint(1).Y, fullCurve.GetEndPoint(1).Z } : null
                        };
                    })
                    .ToList();

                var vGridLines = curtainGrid.GetVGridLineIds()
                    .Select(id =>
                    {
                        var gridLine = doc.GetElement(id) as CurtainGridLine;
                        var fullCurve = gridLine?.FullCurve;
                        return new
                        {
                            gridLineId = (int)id.Value,
                            direction = "V",
                            startPoint = fullCurve != null ? new[] { fullCurve.GetEndPoint(0).X, fullCurve.GetEndPoint(0).Y, fullCurve.GetEndPoint(0).Z } : null,
                            endPoint = fullCurve != null ? new[] { fullCurve.GetEndPoint(1).X, fullCurve.GetEndPoint(1).Y, fullCurve.GetEndPoint(1).Z } : null
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallId", (int)wall.Id.Value)
                    .With("uGridLines", uGridLines)
                    .With("vGridLines", vGridLines)
                    .With("uGridLineCount", uGridLines.Count)
                    .With("vGridLineCount", vGridLines.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add a grid line to a curtain wall
        /// </summary>
        [MCPMethod("addCurtainGridLine", Category = "CurtainWall", Description = "Add a grid line to a curtain wall at a specified position")]
        public static string AddCurtainGridLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "addCurtainGridLine");
                v.Require("wallId").IsType<int>();
                v.Require("isUGrid");
                v.Require("position");
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var isUGrid = v.GetRequired<bool>("isUGrid");
                var position = parameters["position"].ToObject<double[]>();

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var curtainGrid = wall.CurtainGrid;
                if (curtainGrid == null)
                    return ResponseBuilder.Error("Curtain wall has no grid", "NO_CURTAIN_GRID").Build();

                using (var trans = new Transaction(doc, "Add Curtain Grid Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(position[0], position[1], position[2]);
                    var oneSegment = v.GetOptional<bool>("oneSegment", false);
                    var gridLine = curtainGrid.AddGridLine(isUGrid, point, oneSegment);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wall.Id.Value)
                        .With("gridLineId", gridLine != null ? (int)gridLine.Id.Value : -1)
                        .With("direction", isUGrid ? "U" : "V")
                        .WithMessage("Grid line added successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove a curtain grid line
        /// </summary>
        [MCPMethod("removeCurtainGridLine", Category = "CurtainWall", Description = "Remove a curtain grid line by ID")]
        public static string RemoveCurtainGridLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "removeCurtainGridLine");
                v.Require("gridLineId").IsType<int>();
                v.ThrowIfInvalid();

                var gridLineId = new ElementId(v.GetRequired<int>("gridLineId"));
                var gridLine = doc.GetElement(gridLineId) as CurtainGridLine;

                if (gridLine == null)
                    return ResponseBuilder.Error("Curtain grid line not found", "ELEMENT_NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Remove Curtain Grid Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(gridLineId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("deletedGridLineId", (int)gridLineId.Value)
                        .WithMessage("Curtain grid line removed successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all panels in a curtain wall
        /// </summary>
        [MCPMethod("getCurtainPanels", Category = "CurtainWall", Description = "Get all panels in a curtain wall")]
        public static string GetCurtainPanels(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getCurtainPanels");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var curtainGrid = wall.CurtainGrid;
                if (curtainGrid == null)
                    return ResponseBuilder.Error("Curtain wall has no grid", "NO_CURTAIN_GRID").Build();

                var panels = curtainGrid.GetPanelIds()
                    .Select(id =>
                    {
                        var element = doc.GetElement(id);
                        var panel = element as Panel;
                        var familyInstance = element as FamilyInstance;

                        string typeName = "Unknown";
                        double width = 0;
                        double height = 0;

                        if (panel != null)
                        {
                            typeName = doc.GetElement(panel.GetTypeId())?.Name ?? "Unknown";
                            width = panel.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH)?.AsDouble() ?? 0;
                            height = panel.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_HEIGHT)?.AsDouble() ?? 0;
                        }
                        else if (familyInstance != null)
                        {
                            typeName = familyInstance.Symbol?.Name ?? "Unknown";
                        }

                        return new
                        {
                            panelId = (int)id.Value,
                            typeName,
                            width,
                            height,
                            categoryName = element?.Category?.Name ?? "Unknown"
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallId", (int)wall.Id.Value)
                    .With("panels", panels)
                    .WithCount(panels.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change the type of a curtain panel
        /// </summary>
        [MCPMethod("setCurtainPanelType", Category = "CurtainWall", Description = "Change the type of a curtain panel")]
        public static string SetCurtainPanelType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setCurtainPanelType");
                v.Require("panelId").IsType<int>();
                v.Require("newTypeId").IsType<int>();
                v.ThrowIfInvalid();

                var panelId = new ElementId(v.GetRequired<int>("panelId"));
                var newTypeId = new ElementId(v.GetRequired<int>("newTypeId"));

                var element = doc.GetElement(panelId);
                if (element == null)
                    return ResponseBuilder.Error("Panel not found", "ELEMENT_NOT_FOUND").Build();

                var panel = element as Panel;
                var familyInstance = element as FamilyInstance;

                using (var trans = new Transaction(doc, "Set Curtain Panel Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    string oldTypeName = "Unknown";
                    string newTypeName = "Unknown";

                    if (panel != null)
                    {
                        oldTypeName = doc.GetElement(panel.GetTypeId())?.Name ?? "Unknown";
                        panel.ChangeTypeId(newTypeId);
                        newTypeName = doc.GetElement(newTypeId)?.Name ?? "Unknown";
                    }
                    else if (familyInstance != null)
                    {
                        oldTypeName = familyInstance.Symbol?.Name ?? "Unknown";
                        var newSymbol = doc.GetElement(newTypeId) as FamilySymbol;
                        if (newSymbol == null)
                            return ResponseBuilder.Error("New type is not a valid family symbol", "INVALID_TYPE").Build();
                        familyInstance.Symbol = newSymbol;
                        newTypeName = newSymbol.Name;
                    }
                    else
                    {
                        return ResponseBuilder.Error("Element is not a panel or family instance", "INVALID_ELEMENT").Build();
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("panelId", (int)panelId.Value)
                        .With("oldTypeName", oldTypeName)
                        .With("newTypeName", newTypeName)
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Unlock a curtain panel for independent modification
        /// </summary>
        [MCPMethod("unlockCurtainPanel", Category = "CurtainWall", Description = "Unlock/pin a curtain panel for independent modification")]
        public static string UnlockCurtainPanel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "unlockCurtainPanel");
                v.Require("panelId").IsType<int>();
                v.ThrowIfInvalid();

                var panelId = new ElementId(v.GetRequired<int>("panelId"));
                var element = doc.GetElement(panelId);

                if (element == null)
                    return ResponseBuilder.Error("Panel not found", "ELEMENT_NOT_FOUND").Build();

                var panel = element as Panel;
                if (panel == null)
                    return ResponseBuilder.Error("Element is not a curtain panel", "INVALID_ELEMENT").Build();

                using (var trans = new Transaction(doc, "Unlock Curtain Panel"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var pinned = element.Pinned;
                    element.Pinned = !pinned;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("panelId", (int)panelId.Value)
                        .With("wasPinned", pinned)
                        .With("isNowPinned", !pinned)
                        .WithMessage(pinned ? "Panel unpinned" : "Panel pinned")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all mullions in a curtain wall
        /// </summary>
        [MCPMethod("getCurtainMullions", Category = "CurtainWall", Description = "Get all mullions in a curtain wall")]
        public static string GetCurtainMullions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getCurtainMullions");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var curtainGrid = wall.CurtainGrid;
                if (curtainGrid == null)
                    return ResponseBuilder.Error("Curtain wall has no grid", "NO_CURTAIN_GRID").Build();

                var mullions = curtainGrid.GetMullionIds()
                    .Select(id =>
                    {
                        var mullion = doc.GetElement(id) as Mullion;
                        var mullionType = mullion != null ? doc.GetElement(mullion.GetTypeId()) as MullionType : null;
                        var locationCurve = mullion?.LocationCurve;

                        double length = 0;
                        double[] startPt = null;
                        double[] endPt = null;

                        if (locationCurve != null)
                        {
                            length = locationCurve.Length;
                            var s = locationCurve.GetEndPoint(0);
                            var e = locationCurve.GetEndPoint(1);
                            startPt = new[] { s.X, s.Y, s.Z };
                            endPt = new[] { e.X, e.Y, e.Z };
                        }

                        return new
                        {
                            mullionId = (int)id.Value,
                            typeName = mullionType?.Name ?? "Unknown",
                            typeId = mullionType != null ? (int)mullionType.Id.Value : -1,
                            length,
                            startPoint = startPt,
                            endPoint = endPt
                        };
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("wallId", (int)wall.Id.Value)
                    .With("mullions", mullions)
                    .WithCount(mullions.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change the type of a mullion
        /// </summary>
        [MCPMethod("setCurtainMullionType", Category = "CurtainWall", Description = "Change the type of a curtain mullion")]
        public static string SetCurtainMullionType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setCurtainMullionType");
                v.Require("mullionId").IsType<int>();
                v.Require("newTypeId").IsType<int>();
                v.ThrowIfInvalid();

                var mullionId = new ElementId(v.GetRequired<int>("mullionId"));
                var newTypeId = new ElementId(v.GetRequired<int>("newTypeId"));

                var mullion = doc.GetElement(mullionId) as Mullion;
                if (mullion == null)
                    return ResponseBuilder.Error("Mullion not found", "ELEMENT_NOT_FOUND").Build();

                var newType = doc.GetElement(newTypeId) as MullionType;
                if (newType == null)
                    return ResponseBuilder.Error("New type is not a valid mullion type", "INVALID_TYPE").Build();

                using (var trans = new Transaction(doc, "Set Curtain Mullion Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var oldType = doc.GetElement(mullion.GetTypeId()) as MullionType;
                    var oldTypeName = oldType?.Name ?? "Unknown";

                    mullion.ChangeTypeId(newTypeId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("mullionId", (int)mullionId.Value)
                        .With("oldTypeName", oldTypeName)
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
        /// Get all available mullion types
        /// </summary>
        [MCPMethod("getMullionTypes", Category = "CurtainWall", Description = "Get all available mullion types in the document")]
        public static string GetMullionTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(MullionType))
                    .Cast<MullionType>()
                    .Select(mt => new
                    {
                        typeId = (int)mt.Id.Value,
                        name = mt.Name,
                        familyName = mt.FamilyName
                    })
                    .ToList();

                return ResponseBuilder.Success()
                    .With("mullionTypes", types)
                    .WithCount(types.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all available curtain panel types
        /// </summary>
        [MCPMethod("getPanelTypes", Category = "CurtainWall", Description = "Get all available curtain panel types (system and wall panels)")]
        public static string GetPanelTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get system panel types (PanelType)
                var panelTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(PanelType))
                    .Cast<PanelType>()
                    .Select(pt => new
                    {
                        typeId = (int)pt.Id.Value,
                        name = pt.Name,
                        familyName = pt.FamilyName,
                        panelCategory = "SystemPanel"
                    })
                    .ToList<object>();

                // Get family-based panel types (curtain wall panel families)
                var familyPanelTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .Where(fs => fs != null)
                    .Select(fs => new
                    {
                        typeId = (int)fs.Id.Value,
                        name = fs.Name,
                        familyName = fs.FamilyName,
                        panelCategory = "FamilyPanel"
                    })
                    .ToList<object>();

                var allTypes = panelTypes.Concat(familyPanelTypes).ToList();

                return ResponseBuilder.Success()
                    .With("panelTypes", allTypes)
                    .WithCount(allTypes.Count)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a curtain system on faces
        /// </summary>
        [MCPMethod("createCurtainSystem", Category = "CurtainWall", Description = "Create a curtain system on face references")]
        public static string CreateCurtainSystem(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createCurtainSystem");
                v.Require("hostElementId").IsType<int>();
                v.Optional("typeId").IsType<int>();
                v.ThrowIfInvalid();

                var hostElementId = new ElementId(v.GetRequired<int>("hostElementId"));
                var hostElement = doc.GetElement(hostElementId);

                if (hostElement == null)
                    return ResponseBuilder.Error("Host element not found", "ELEMENT_NOT_FOUND").Build();

                // Get faces from the host element geometry
                var geomOptions = new Options { ComputeReferences = true };
                var geomElement = hostElement.get_Geometry(geomOptions);
                if (geomElement == null)
                    return ResponseBuilder.Error("Could not get geometry from host element", "NO_GEOMETRY").Build();

                var faceArray = new FaceArray();
                foreach (var geomObj in geomElement)
                {
                    var solid = geomObj as Solid;
                    if (solid == null) continue;

                    foreach (Face face in solid.Faces)
                    {
                        faceArray.Append(face);
                    }
                }

                if (faceArray.Size == 0)
                    return ResponseBuilder.Error("No faces found on host element", "NO_FACES").Build();

                // Resolve curtain system type
                CurtainSystemType csType = null;
                if (parameters["typeId"] != null)
                {
                    var typeId = new ElementId(v.GetRequired<int>("typeId"));
                    csType = doc.GetElement(typeId) as CurtainSystemType;
                    if (csType == null)
                        return ResponseBuilder.Error("Specified type is not a curtain system type", "INVALID_TYPE").Build();
                }
                else
                {
                    csType = new FilteredElementCollector(doc)
                        .OfClass(typeof(CurtainSystemType))
                        .Cast<CurtainSystemType>()
                        .FirstOrDefault();

                    if (csType == null)
                        return ResponseBuilder.Error("No curtain system type found in document", "NO_TYPE").Build();
                }

                using (var trans = new Transaction(doc, "Create Curtain System"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var curtainSystem = doc.Create.NewCurtainSystem(faceArray, csType);

                    trans.Commit();

                    var systemId = curtainSystem != null ? (int)curtainSystem.Id.Value : -1;

                    return ResponseBuilder.Success()
                        .With("curtainSystemId", systemId)
                        .With("typeName", csType.Name)
                        .With("faceCount", faceArray.Size)
                        .WithMessage("Curtain system created successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get full grid layout info for a curtain wall
        /// </summary>
        [MCPMethod("getCurtainGridInfo", Category = "CurtainWall", Description = "Get full grid layout info for a curtain wall")]
        public static string GetCurtainGridInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getCurtainGridInfo");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var curtainGrid = wall.CurtainGrid;
                if (curtainGrid == null)
                    return ResponseBuilder.Error("Curtain wall has no grid", "NO_CURTAIN_GRID").Build();

                var uGridIds = curtainGrid.GetUGridLineIds();
                var vGridIds = curtainGrid.GetVGridLineIds();
                var panelIds = curtainGrid.GetPanelIds();
                var mullionIds = curtainGrid.GetMullionIds();

                // Get grid spacing from wall type parameters
                var wallType = wall.WallType;
                var spacingLayoutU = wallType.LookupParameter("Spacing 1")?.AsDouble() ?? wallType.LookupParameter("Grid 1 Spacing")?.AsDouble() ?? 0;
                var spacingLayoutV = wallType.LookupParameter("Spacing 2")?.AsDouble() ?? wallType.LookupParameter("Grid 2 Spacing")?.AsDouble() ?? 0;

                // Calculate number of cells (panels between grid lines)
                var numCellsU = uGridIds.Count + 1;
                var numCellsV = vGridIds.Count + 1;

                return ResponseBuilder.Success()
                    .With("wallId", (int)wall.Id.Value)
                    .With("typeName", wallType.Name)
                    .With("uGridLineCount", uGridIds.Count)
                    .With("vGridLineCount", vGridIds.Count)
                    .With("panelCount", panelIds.Count)
                    .With("mullionCount", mullionIds.Count)
                    .With("cellsU", numCellsU)
                    .With("cellsV", numCellsV)
                    .With("spacingU", spacingLayoutU)
                    .With("spacingV", spacingLayoutV)
                    .With("wallLength", wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0)
                    .With("wallHeight", wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify curtain grid spacing by changing wall type parameters
        /// </summary>
        [MCPMethod("modifyCurtainGridSpacing", Category = "CurtainWall", Description = "Modify curtain grid U/V spacing by changing wall type parameters")]
        public static string ModifyCurtainGridSpacing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "modifyCurtainGridSpacing");
                v.Require("wallId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(v.GetRequired<int>("wallId"));
                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                    return ResponseBuilder.Error("Wall not found", "ELEMENT_NOT_FOUND").Build();

                if (wall.WallType.Kind != WallKind.Curtain)
                    return ResponseBuilder.Error("Wall is not a curtain wall", "INVALID_WALL_TYPE").Build();

                var spacingU = parameters["spacingU"];
                var spacingV = parameters["spacingV"];

                if (spacingU == null && spacingV == null)
                    return ResponseBuilder.Error("At least one of spacingU or spacingV must be provided", "MISSING_PARAMETER").Build();

                using (var trans = new Transaction(doc, "Modify Curtain Grid Spacing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Duplicate the wall type to avoid modifying the original type for all walls
                    var wallType = wall.WallType;
                    var modified = new List<string>();

                    if (spacingU != null)
                    {
                        var paramU = wallType.LookupParameter("Spacing 1") ?? wallType.LookupParameter("Grid 1 Spacing");
                        if (paramU != null && !paramU.IsReadOnly)
                        {
                            paramU.Set(spacingU.Value<double>());
                            modified.Add("spacingU");
                        }
                    }

                    if (spacingV != null)
                    {
                        var paramV = wallType.LookupParameter("Spacing 2") ?? wallType.LookupParameter("Grid 2 Spacing");
                        if (paramV != null && !paramV.IsReadOnly)
                        {
                            paramV.Set(spacingV.Value<double>());
                            modified.Add("spacingV");
                        }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wallId", (int)wall.Id.Value)
                        .With("typeName", wallType.Name)
                        .With("modifiedProperties", modified)
                        .WithMessage($"Modified {modified.Count} grid spacing properties")
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
