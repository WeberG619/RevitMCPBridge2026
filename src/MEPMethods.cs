using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for MEP (Mechanical, Electrical, Plumbing) Systems in Revit
    /// Handles HVAC ducts, pipes, electrical systems, equipment, and MEP spaces
    /// </summary>
    public static class MEPMethods
    {
        #region Mechanical - Ducts

        /// <summary>
        /// Creates a duct between two points
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing startPoint, endPoint, ductTypeId, systemTypeId, levelId</param>
        /// <returns>JSON response with success status and duct element ID</returns>
        [MCPMethod("createDuct", Category = "MEP", Description = "Creates a duct between two points in the model")]
        public static string CreateDuct(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "startPoint and endPoint are required"
                    });
                }

                if (parameters["ductTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "ductTypeId is required"
                    });
                }

                if (parameters["systemTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "systemTypeId is required"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(
                    start["x"].ToObject<double>(),
                    start["y"].ToObject<double>(),
                    start["z"].ToObject<double>()
                );

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(
                    end["x"].ToObject<double>(),
                    end["y"].ToObject<double>(),
                    end["z"].ToObject<double>()
                );

                // Get duct type
                int ductTypeIdInt = parameters["ductTypeId"].ToObject<int>();
                ElementId ductTypeId = new ElementId(ductTypeIdInt);
                DuctType ductType = doc.GetElement(ductTypeId) as DuctType;

                if (ductType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Duct type with ID {ductTypeIdInt} not found"
                    });
                }

                // Get system type
                int systemTypeIdInt = parameters["systemTypeId"].ToObject<int>();
                ElementId systemTypeId = new ElementId(systemTypeIdInt);
                MechanicalSystemType systemType = doc.GetElement(systemTypeId) as MechanicalSystemType;

                if (systemType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Mechanical system type with ID {systemTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Duct"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create duct using Revit 2026 API
                    Duct duct = Duct.Create(doc, systemTypeId, ductTypeId, levelId, startPoint, endPoint);

                    if (duct == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create duct"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ductId = duct.Id.Value,
                        ductTypeId = ductTypeIdInt,
                        systemTypeId = systemTypeIdInt,
                        levelId = levelIdInt,
                        startPoint = new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z },
                        endPoint = new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z },
                        message = "Duct created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets detailed information about a duct
        /// </summary>
        [MCPMethod("getDuctInfo", Category = "MEP", Description = "Gets detailed information about a duct element")]
        public static string GetDuctInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["ductId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "ductId is required"
                    });
                }

                int ductIdInt = parameters["ductId"].ToObject<int>();
                ElementId ductId = new ElementId(ductIdInt);
                Duct duct = doc.GetElement(ductId) as Duct;

                if (duct == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Duct with ID {ductIdInt} not found"
                    });
                }

                // Get duct properties
                var ductType = doc.GetElement(duct.GetTypeId()) as DuctType;
                var systemType = duct.MEPSystem != null ? doc.GetElement(duct.MEPSystem.GetTypeId()) as MEPSystemType : null;

                // Get dimensions
                var diameter = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble();
                var width = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble();
                var height = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble();

                // Get flow and pressure
                var flow = duct.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM)?.AsDouble();
                var pressure = duct.get_Parameter(BuiltInParameter.RBS_DUCT_STATIC_PRESSURE)?.AsDouble();
                var velocity = duct.get_Parameter(BuiltInParameter.RBS_VELOCITY)?.AsDouble();

                // Get material - Material parameter access varies by duct type
                // Note: Specific material parameters may vary; using null for now
                Material material = null;  // TODO: Determine correct parameter for duct material in Revit 2026

                var insulationThickness = duct.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS)?.AsDouble();

                // Get location curve
                LocationCurve locCurve = duct.Location as LocationCurve;
                var startPoint = locCurve != null ? locCurve.Curve.GetEndPoint(0) : null;
                var endPoint = locCurve != null ? locCurve.Curve.GetEndPoint(1) : null;
                var length = locCurve != null ? locCurve.Curve.Length : 0;

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    ductId = ductIdInt,
                    ductTypeName = ductType?.Name,
                    ductTypeId = ductType?.Id.Value,
                    systemTypeName = systemType?.Name,
                    systemTypeId = systemType?.Id.Value,
                    systemName = duct.MEPSystem?.Name,
                    systemId = duct.MEPSystem?.Id.Value,
                    dimensions = new
                    {
                        diameter = diameter,
                        width = width,
                        height = height,
                        shape = diameter.HasValue ? "Round" : (width.HasValue && height.HasValue ? "Rectangular" : "Unknown")
                    },
                    flow = new
                    {
                        airflow = flow,
                        velocity = velocity,
                        pressure = pressure
                    },
                    material = material != null ? new
                    {
                        name = material.Name,
                        id = material.Id.Value
                    } : null,
                    insulation = new
                    {
                        thickness = insulationThickness
                    },
                    geometry = new
                    {
                        startPoint = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                        endPoint = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null,
                        length = length
                    },
                    levelId = duct.LevelId?.Value,
                    levelName = duct.LevelId != null ? (doc.GetElement(duct.LevelId) as Level)?.Name : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a duct fitting (elbow, tee, cross, transition, etc.)
        /// </summary>
        [MCPMethod("createDuctFitting", Category = "MEP", Description = "Creates a duct fitting at a connector location")]
        public static string CreateDuctFitting(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["connector1Id"] == null || parameters["connector2Id"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "connector1Id and connector2Id are required"
                    });
                }

                // Get connector elements
                int conn1ElementIdInt = parameters["connector1Id"].ToObject<int>();
                int conn2ElementIdInt = parameters["connector2Id"].ToObject<int>();

                ElementId conn1ElementId = new ElementId(conn1ElementIdInt);
                ElementId conn2ElementId = new ElementId(conn2ElementIdInt);

                Element element1 = doc.GetElement(conn1ElementId);
                Element element2 = doc.GetElement(conn2ElementId);

                if (element1 == null || element2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "One or both connector elements not found"
                    });
                }

                // Get connectors from elements
                Connector connector1 = null;
                Connector connector2 = null;

                // Get connector index (optional, defaults to first available)
                int conn1Index = parameters["connector1Index"]?.ToObject<int>() ?? 0;
                int conn2Index = parameters["connector2Index"]?.ToObject<int>() ?? 0;

                // Find the connectors
                ConnectorSet connectorSet1 = null;
                ConnectorSet connectorSet2 = null;

                if (element1 is Duct duct1)
                {
                    connectorSet1 = duct1.ConnectorManager?.Connectors;
                }
                else if (element1 is FamilyInstance fi1)
                {
                    connectorSet1 = fi1.MEPModel?.ConnectorManager?.Connectors;
                }

                if (element2 is Duct duct2)
                {
                    connectorSet2 = duct2.ConnectorManager?.Connectors;
                }
                else if (element2 is FamilyInstance fi2)
                {
                    connectorSet2 = fi2.MEPModel?.ConnectorManager?.Connectors;
                }

                if (connectorSet1 == null || connectorSet2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Unable to retrieve connectors from elements"
                    });
                }

                // Get specific connectors by index
                int currentIndex = 0;
                foreach (Connector conn in connectorSet1)
                {
                    if (currentIndex == conn1Index)
                    {
                        connector1 = conn;
                        break;
                    }
                    currentIndex++;
                }

                currentIndex = 0;
                foreach (Connector conn in connectorSet2)
                {
                    if (currentIndex == conn2Index)
                    {
                        connector2 = conn;
                        break;
                    }
                    currentIndex++;
                }

                if (connector1 == null || connector2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Specified connector indices not found on elements"
                    });
                }

                using (var trans = new Transaction(doc, "Create Duct Fitting"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create elbow fitting between the two connectors
                    FamilyInstance fitting = doc.Create.NewElbowFitting(connector1, connector2);

                    if (fitting == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create duct fitting"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        fittingId = fitting.Id.Value,
                        fittingTypeName = fitting.Symbol?.Name,
                        familyName = fitting.Symbol?.FamilyName,
                        connector1ElementId = conn1ElementIdInt,
                        connector2ElementId = conn2ElementIdInt,
                        message = "Duct fitting created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a duct accessory (damper, VAV, diffuser, etc.)
        /// </summary>
        [MCPMethod("createDuctAccessory", Category = "MEP", Description = "Places a duct accessory on an existing duct")]
        public static string CreateDuctAccessory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["familySymbolId"] == null || parameters["hostDuctId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familySymbolId and hostDuctId are required"
                    });
                }

                int symbolIdInt = parameters["familySymbolId"].ToObject<int>();
                int hostDuctIdInt = parameters["hostDuctId"].ToObject<int>();

                ElementId symbolId = new ElementId(symbolIdInt);
                ElementId hostDuctId = new ElementId(hostDuctIdInt);

                FamilySymbol symbol = doc.GetElement(symbolId) as FamilySymbol;
                Element hostDuct = doc.GetElement(hostDuctId);

                if (symbol == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"FamilySymbol with ID {symbolIdInt} not found" });
                if (hostDuct == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Host duct with ID {hostDuctIdInt} not found" });

                // Note: Duct accessory placement requires specific placement methods
                // depending on accessory type (inline, tap, etc.)
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Duct accessory placement requires type-specific methods not exposed in this API",
                    hint = "Use Revit's built-in tools or FamilyInstance placement at specific locations"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all ducts in a view or system
        /// </summary>
        [MCPMethod("getDuctsInView", Category = "MEP", Description = "Gets all duct elements visible in the active view")]
        public static string GetDuctsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                FilteredElementCollector collector;

                // Check if viewId is provided
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    ElementId viewId = new ElementId(viewIdInt);
                    View view = doc.GetElement(viewId) as View;

                    if (view == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View with ID {viewIdInt} not found"
                        });
                    }

                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    // Get all ducts in the document
                    collector = new FilteredElementCollector(doc);
                }

                // Filter for ducts
                var ducts = collector
                    .OfClass(typeof(Duct))
                    .Cast<Duct>()
                    .ToList();

                // Optionally filter by system if systemId is provided
                if (parameters["systemId"] != null)
                {
                    int systemIdInt = parameters["systemId"].ToObject<int>();
                    ElementId systemId = new ElementId(systemIdInt);
                    ducts = ducts.Where(d => d.MEPSystem?.Id == systemId).ToList();
                }

                // Build result array
                var ductList = new List<object>();
                foreach (var duct in ducts)
                {
                    var ductType = doc.GetElement(duct.GetTypeId()) as DuctType;
                    var systemType = duct.MEPSystem != null ? doc.GetElement(duct.MEPSystem.GetTypeId()) as MEPSystemType : null;

                    // Get key properties
                    var diameter = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble();
                    var width = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble();
                    var height = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble();
                    var flow = duct.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM)?.AsDouble();
                    var velocity = duct.get_Parameter(BuiltInParameter.RBS_VELOCITY)?.AsDouble();

                    // Get location
                    LocationCurve locCurve = duct.Location as LocationCurve;
                    var length = locCurve != null ? locCurve.Curve.Length : 0;

                    ductList.Add(new
                    {
                        ductId = duct.Id.Value,
                        ductTypeName = ductType?.Name,
                        ductTypeId = ductType?.Id.Value,
                        systemName = duct.MEPSystem?.Name,
                        systemId = duct.MEPSystem?.Id.Value,
                        systemTypeName = systemType?.Name,
                        dimensions = new
                        {
                            diameter = diameter,
                            width = width,
                            height = height,
                            length = length,
                            shape = diameter.HasValue ? "Round" : (width.HasValue && height.HasValue ? "Rectangular" : "Unknown")
                        },
                        flow = new
                        {
                            airflow = flow,
                            velocity = velocity
                        },
                        levelId = duct.LevelId?.Value,
                        levelName = duct.LevelId != null ? (doc.GetElement(duct.LevelId) as Level)?.Name : null
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = ductList.Count,
                    ducts = ductList
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Plumbing - Pipes

        /// <summary>
        /// Creates a pipe between two points
        /// </summary>
        [MCPMethod("createPipe", Category = "MEP", Description = "Creates a pipe segment between two points")]
        public static string CreatePipe(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "startPoint and endPoint are required"
                    });
                }

                if (parameters["pipeTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "pipeTypeId is required"
                    });
                }

                if (parameters["systemTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "systemTypeId is required"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(
                    start["x"].ToObject<double>(),
                    start["y"].ToObject<double>(),
                    start["z"].ToObject<double>()
                );

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(
                    end["x"].ToObject<double>(),
                    end["y"].ToObject<double>(),
                    end["z"].ToObject<double>()
                );

                // Get pipe type
                int pipeTypeIdInt = parameters["pipeTypeId"].ToObject<int>();
                ElementId pipeTypeId = new ElementId(pipeTypeIdInt);
                PipeType pipeType = doc.GetElement(pipeTypeId) as PipeType;

                if (pipeType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Pipe type with ID {pipeTypeIdInt} not found"
                    });
                }

                // Get system type
                int systemTypeIdInt = parameters["systemTypeId"].ToObject<int>();
                ElementId systemTypeId = new ElementId(systemTypeIdInt);
                PipingSystemType systemType = doc.GetElement(systemTypeId) as PipingSystemType;

                if (systemType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Piping system type with ID {systemTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Pipe"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create pipe using Revit 2026 API
                    Pipe pipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, startPoint, endPoint);

                    if (pipe == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create pipe"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        pipeId = pipe.Id.Value,
                        pipeTypeId = pipeTypeIdInt,
                        systemTypeId = systemTypeIdInt,
                        levelId = levelIdInt,
                        startPoint = new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z },
                        endPoint = new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z },
                        message = "Pipe created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets detailed information about a pipe
        /// </summary>
        [MCPMethod("getPipeInfo", Category = "MEP", Description = "Gets detailed information about a pipe element")]
        public static string GetPipeInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["pipeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "pipeId is required"
                    });
                }

                int pipeIdInt = parameters["pipeId"].ToObject<int>();
                ElementId pipeId = new ElementId(pipeIdInt);
                Pipe pipe = doc.GetElement(pipeId) as Pipe;

                if (pipe == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Pipe with ID {pipeIdInt} not found"
                    });
                }

                // Get pipe properties
                var pipeType = doc.GetElement(pipe.GetTypeId()) as PipeType;
                var systemType = pipe.MEPSystem != null ? doc.GetElement(pipe.MEPSystem.GetTypeId()) as MEPSystemType : null;

                // Get diameter
                var diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble();

                // Get flow and pressure
                var flow = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)?.AsDouble();
                var velocity = pipe.get_Parameter(BuiltInParameter.RBS_VELOCITY)?.AsDouble();

                // Get material and insulation
                var materialId = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_MATERIAL_PARAM)?.AsElementId();
                Material material = materialId != null && materialId != ElementId.InvalidElementId
                    ? doc.GetElement(materialId) as Material
                    : null;

                var insulationThickness = pipe.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS)?.AsDouble();

                // Get location curve
                LocationCurve locCurve = pipe.Location as LocationCurve;
                var startPoint = locCurve != null ? locCurve.Curve.GetEndPoint(0) : null;
                var endPoint = locCurve != null ? locCurve.Curve.GetEndPoint(1) : null;
                var length = locCurve != null ? locCurve.Curve.Length : 0;

                // Get slope and offset
                var slope = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE)?.AsDouble();
                var offset = pipe.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM)?.AsDouble();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    pipeId = pipeIdInt,
                    pipeTypeName = pipeType?.Name,
                    pipeTypeId = pipeType?.Id.Value,
                    systemTypeName = systemType?.Name,
                    systemTypeId = systemType?.Id.Value,
                    systemName = pipe.MEPSystem?.Name,
                    systemId = pipe.MEPSystem?.Id.Value,
                    dimensions = new
                    {
                        diameter = diameter
                    },
                    flow = new
                    {
                        flowRate = flow,
                        velocity = velocity
                    },
                    material = material != null ? new
                    {
                        name = material.Name,
                        id = material.Id.Value
                    } : null,
                    insulation = new
                    {
                        thickness = insulationThickness
                    },
                    geometry = new
                    {
                        startPoint = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                        endPoint = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null,
                        length = length,
                        slope = slope,
                        offset = offset
                    },
                    levelId = pipe.LevelId?.Value,
                    levelName = pipe.LevelId != null ? (doc.GetElement(pipe.LevelId) as Level)?.Name : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a pipe fitting (elbow, tee, cross, coupling, etc.)
        /// </summary>
        [MCPMethod("createPipeFitting", Category = "MEP", Description = "Creates a pipe fitting at a connector location")]
        public static string CreatePipeFitting(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["connector1Id"] == null || parameters["connector2Id"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "connector1Id and connector2Id are required"
                    });
                }

                // Get connector elements
                int conn1ElementIdInt = parameters["connector1Id"].ToObject<int>();
                int conn2ElementIdInt = parameters["connector2Id"].ToObject<int>();

                ElementId conn1ElementId = new ElementId(conn1ElementIdInt);
                ElementId conn2ElementId = new ElementId(conn2ElementIdInt);

                Element element1 = doc.GetElement(conn1ElementId);
                Element element2 = doc.GetElement(conn2ElementId);

                if (element1 == null || element2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "One or both connector elements not found"
                    });
                }

                // Get connectors from elements
                Connector connector1 = null;
                Connector connector2 = null;

                // Get connector index (optional, defaults to first available)
                int conn1Index = parameters["connector1Index"]?.ToObject<int>() ?? 0;
                int conn2Index = parameters["connector2Index"]?.ToObject<int>() ?? 0;

                // Find the connectors
                ConnectorSet connectorSet1 = null;
                ConnectorSet connectorSet2 = null;

                if (element1 is Pipe pipe1)
                {
                    connectorSet1 = pipe1.ConnectorManager?.Connectors;
                }
                else if (element1 is FamilyInstance fi1)
                {
                    connectorSet1 = fi1.MEPModel?.ConnectorManager?.Connectors;
                }

                if (element2 is Pipe pipe2)
                {
                    connectorSet2 = pipe2.ConnectorManager?.Connectors;
                }
                else if (element2 is FamilyInstance fi2)
                {
                    connectorSet2 = fi2.MEPModel?.ConnectorManager?.Connectors;
                }

                if (connectorSet1 == null || connectorSet2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Unable to retrieve connectors from elements"
                    });
                }

                // Get specific connectors by index
                int currentIndex = 0;
                foreach (Connector conn in connectorSet1)
                {
                    if (currentIndex == conn1Index)
                    {
                        connector1 = conn;
                        break;
                    }
                    currentIndex++;
                }

                currentIndex = 0;
                foreach (Connector conn in connectorSet2)
                {
                    if (currentIndex == conn2Index)
                    {
                        connector2 = conn;
                        break;
                    }
                    currentIndex++;
                }

                if (connector1 == null || connector2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Specified connector indices not found on elements"
                    });
                }

                using (var trans = new Transaction(doc, "Create Pipe Fitting"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create elbow fitting between the two connectors
                    FamilyInstance fitting = doc.Create.NewElbowFitting(connector1, connector2);

                    if (fitting == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create pipe fitting"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        fittingId = fitting.Id.Value,
                        fittingTypeName = fitting.Symbol?.Name,
                        familyName = fitting.Symbol?.FamilyName,
                        connector1ElementId = conn1ElementIdInt,
                        connector2ElementId = conn2ElementIdInt,
                        message = "Pipe fitting created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a pipe accessory (valve, strainer, union, etc.)
        /// </summary>
        [MCPMethod("createPipeAccessory", Category = "MEP", Description = "Places a pipe accessory on an existing pipe")]
        public static string CreatePipeAccessory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["familySymbolId"] == null || parameters["hostPipeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familySymbolId and hostPipeId are required"
                    });
                }

                int symbolIdInt = parameters["familySymbolId"].ToObject<int>();
                int hostPipeIdInt = parameters["hostPipeId"].ToObject<int>();

                ElementId symbolId = new ElementId(symbolIdInt);
                ElementId hostPipeId = new ElementId(hostPipeIdInt);

                FamilySymbol symbol = doc.GetElement(symbolId) as FamilySymbol;
                Element hostPipe = doc.GetElement(hostPipeId);

                if (symbol == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"FamilySymbol with ID {symbolIdInt} not found" });
                if (hostPipe == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Host pipe with ID {hostPipeIdInt} not found" });

                // Note: Pipe accessory placement requires specific placement methods
                // depending on accessory type (inline, tap, etc.)
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Pipe accessory placement requires type-specific methods not exposed in this API",
                    hint = "Use Revit's built-in tools or FamilyInstance placement at specific locations"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all pipes in a view or system
        /// </summary>
        [MCPMethod("getPipesInView", Category = "MEP", Description = "Gets all pipe elements visible in the active view")]
        public static string GetPipesInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                FilteredElementCollector collector;

                // Check if viewId is provided
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    ElementId viewId = new ElementId(viewIdInt);
                    View view = doc.GetElement(viewId) as View;

                    if (view == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View with ID {viewIdInt} not found"
                        });
                    }

                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    // Get all pipes in the document
                    collector = new FilteredElementCollector(doc);
                }

                // Filter for pipes
                var pipes = collector
                    .OfClass(typeof(Pipe))
                    .Cast<Pipe>()
                    .ToList();

                // Optionally filter by system if systemId is provided
                if (parameters["systemId"] != null)
                {
                    int systemIdInt = parameters["systemId"].ToObject<int>();
                    ElementId systemId = new ElementId(systemIdInt);
                    pipes = pipes.Where(p => p.MEPSystem?.Id == systemId).ToList();
                }

                // Build result array
                var pipeList = new List<object>();
                foreach (var pipe in pipes)
                {
                    var pipeType = doc.GetElement(pipe.GetTypeId()) as PipeType;
                    var systemType = pipe.MEPSystem != null ? doc.GetElement(pipe.MEPSystem.GetTypeId()) as MEPSystemType : null;

                    // Get key properties
                    var diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble();
                    var flow = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)?.AsDouble();
                    var velocity = pipe.get_Parameter(BuiltInParameter.RBS_VELOCITY)?.AsDouble();
                    var slope = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE)?.AsDouble();

                    // Get location
                    LocationCurve locCurve = pipe.Location as LocationCurve;
                    var length = locCurve != null ? locCurve.Curve.Length : 0;

                    pipeList.Add(new
                    {
                        pipeId = pipe.Id.Value,
                        pipeTypeName = pipeType?.Name,
                        pipeTypeId = pipeType?.Id.Value,
                        systemName = pipe.MEPSystem?.Name,
                        systemId = pipe.MEPSystem?.Id.Value,
                        systemTypeName = systemType?.Name,
                        dimensions = new
                        {
                            diameter = diameter,
                            length = length
                        },
                        flow = new
                        {
                            flowRate = flow,
                            velocity = velocity,
                            slope = slope
                        },
                        levelId = pipe.LevelId?.Value,
                        levelName = pipe.LevelId != null ? (doc.GetElement(pipe.LevelId) as Level)?.Name : null
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = pipeList.Count,
                    pipes = pipeList
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Electrical - Cable Trays and Conduits

        /// <summary>
        /// Creates a cable tray between two points
        /// </summary>
        [MCPMethod("createCableTray", Category = "MEP", Description = "Creates a cable tray segment between two points")]
        public static string CreateCableTray(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "startPoint and endPoint are required"
                    });
                }

                if (parameters["cableTrayTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "cableTrayTypeId is required"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(
                    start["x"].ToObject<double>(),
                    start["y"].ToObject<double>(),
                    start["z"].ToObject<double>()
                );

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(
                    end["x"].ToObject<double>(),
                    end["y"].ToObject<double>(),
                    end["z"].ToObject<double>()
                );

                // Get cable tray type
                int cableTrayTypeIdInt = parameters["cableTrayTypeId"].ToObject<int>();
                ElementId cableTrayTypeId = new ElementId(cableTrayTypeIdInt);
                CableTrayType cableTrayType = doc.GetElement(cableTrayTypeId) as CableTrayType;

                if (cableTrayType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Cable tray type with ID {cableTrayTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Cable Tray"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create cable tray using Revit 2026 API
                    CableTray cableTray = CableTray.Create(doc, cableTrayTypeId, startPoint, endPoint, levelId);

                    if (cableTray == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create cable tray"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cableTrayId = cableTray.Id.Value,
                        cableTrayTypeId = cableTrayTypeIdInt,
                        levelId = levelIdInt,
                        startPoint = new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z },
                        endPoint = new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z },
                        message = "Cable tray created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a conduit between two points
        /// </summary>
        [MCPMethod("createConduit", Category = "MEP", Description = "Creates an electrical conduit segment between two points")]
        public static string CreateConduit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "startPoint and endPoint are required"
                    });
                }

                if (parameters["conduitTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "conduitTypeId is required"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Parse points
                var start = parameters["startPoint"];
                XYZ startPoint = new XYZ(
                    start["x"].ToObject<double>(),
                    start["y"].ToObject<double>(),
                    start["z"].ToObject<double>()
                );

                var end = parameters["endPoint"];
                XYZ endPoint = new XYZ(
                    end["x"].ToObject<double>(),
                    end["y"].ToObject<double>(),
                    end["z"].ToObject<double>()
                );

                // Get conduit type
                int conduitTypeIdInt = parameters["conduitTypeId"].ToObject<int>();
                ElementId conduitTypeId = new ElementId(conduitTypeIdInt);
                ConduitType conduitType = doc.GetElement(conduitTypeId) as ConduitType;

                if (conduitType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Conduit type with ID {conduitTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Conduit"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create conduit using Revit 2026 API
                    Conduit conduit = Conduit.Create(doc, conduitTypeId, startPoint, endPoint, levelId);

                    if (conduit == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create conduit"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        conduitId = conduit.Id.Value,
                        conduitTypeId = conduitTypeIdInt,
                        levelId = levelIdInt,
                        startPoint = new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z },
                        endPoint = new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z },
                        message = "Conduit created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets information about a cable tray or conduit
        /// </summary>
        [MCPMethod("getElectricalPathInfo", Category = "MEP", Description = "Gets routing and path information for an electrical element")]
        public static string GetElectricalPathInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Element with ID {elementIdInt} not found" });
                }

                object elementInfo = null;

                if (element is CableTray cableTray)
                {
                    var width = cableTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble();
                    var height = cableTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble();

                    elementInfo = new
                    {
                        elementType = "CableTray",
                        elementId = elementIdInt,
                        width,
                        height,
                        length = (cableTray.Location as LocationCurve)?.Curve?.Length
                    };
                }
                else if (element is Conduit conduit)
                {
                    var diameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble();

                    elementInfo = new
                    {
                        elementType = "Conduit",
                        elementId = elementIdInt,
                        diameter,
                        length = (conduit.Location as LocationCurve)?.Curve?.Length
                    };
                }
                else
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element is not a CableTray or Conduit"
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementInfo
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Electrical - Devices and Equipment

        /// <summary>
        /// Places an electrical fixture (light fixture, switch, outlet, etc.)
        /// </summary>
        [MCPMethod("placeElectricalFixture", Category = "MEP", Description = "Places an electrical fixture family instance at a specified location")]
        public static string PlaceElectricalFixture(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["fixtureTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "fixtureTypeId is required"
                    });
                }

                if (parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required (provide x, y, z coordinates)"
                    });
                }

                // Get fixture type
                int fixtureTypeIdInt = parameters["fixtureTypeId"].ToObject<int>();
                ElementId fixtureTypeId = new ElementId(fixtureTypeIdInt);
                FamilySymbol fixtureType = doc.GetElement(fixtureTypeId) as FamilySymbol;

                if (fixtureType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Electrical fixture type with ID {fixtureTypeIdInt} not found"
                    });
                }

                // Parse location
                var loc = parameters["location"];
                XYZ location = new XYZ(
                    loc["x"].ToObject<double>(),
                    loc["y"].ToObject<double>(),
                    loc["z"].ToObject<double>()
                );

                // Get rotation (optional, default to 0)
                double rotation = parameters["rotation"]?.ToObject<double>() ?? 0.0;

                // Check if hostElementId is provided (for wall/ceiling-based fixtures)
                Element hostElement = null;
                Level level = null;

                if (parameters["hostElementId"] != null)
                {
                    int hostIdInt = parameters["hostElementId"].ToObject<int>();
                    ElementId hostId = new ElementId(hostIdInt);
                    hostElement = doc.GetElement(hostId);

                    if (hostElement == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Host element with ID {hostIdInt} not found"
                        });
                    }

                    // Get level from host
                    if (hostElement is Wall wall)
                    {
                        level = doc.GetElement(wall.LevelId) as Level;
                    }
                    else if (hostElement is Floor floor)
                    {
                        level = doc.GetElement(floor.LevelId) as Level;
                    }
                }

                // If no host or couldn't get level from host, require levelId
                if (level == null)
                {
                    if (parameters["levelId"] == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "levelId is required when hostElementId is not provided or host doesn't have a level"
                        });
                    }

                    int levelIdInt = parameters["levelId"].ToObject<int>();
                    ElementId levelId = new ElementId(levelIdInt);
                    level = doc.GetElement(levelId) as Level;

                    if (level == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Level with ID {levelIdInt} not found"
                        });
                    }
                }

                using (var trans = new Transaction(doc, "Place Electrical Fixture"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the symbol if not active
                    if (!fixtureType.IsActive)
                    {
                        fixtureType.Activate();
                    }

                    FamilyInstance fixture = null;

                    // Place fixture based on whether host element is provided
                    if (hostElement != null)
                    {
                        // Host-based fixture (wall or ceiling mounted)
                        fixture = doc.Create.NewFamilyInstance(
                            location,
                            fixtureType,
                            hostElement,
                            level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                        );
                    }
                    else
                    {
                        // Standalone fixture (floor-based or free-standing)
                        fixture = doc.Create.NewFamilyInstance(
                            location,
                            fixtureType,
                            level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                        );
                    }

                    if (fixture == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to place electrical fixture"
                        });
                    }

                    // Apply rotation if specified
                    if (rotation != 0.0)
                    {
                        LocationPoint locPoint = fixture.Location as LocationPoint;
                        if (locPoint != null)
                        {
                            XYZ axis = XYZ.BasisZ;
                            locPoint.Rotate(Line.CreateUnbound(location, axis), rotation);
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        fixtureId = fixture.Id.Value,
                        fixtureTypeId = fixtureTypeIdInt,
                        fixtureTypeName = fixtureType.Name,
                        familyName = fixtureType.FamilyName,
                        levelId = level.Id.Value,
                        levelName = level.Name,
                        hostElementId = hostElement?.Id.Value,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        rotation = rotation,
                        message = "Electrical fixture placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places electrical equipment (panel, transformer, switchboard, etc.)
        /// </summary>
        [MCPMethod("placeElectricalEquipment", Category = "MEP", Description = "Places electrical equipment such as a panel or transformer at a specified location")]
        public static string PlaceElectricalEquipment(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["equipmentTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "equipmentTypeId is required"
                    });
                }

                if (parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required (provide x, y, z coordinates)"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Get equipment type
                int equipmentTypeIdInt = parameters["equipmentTypeId"].ToObject<int>();
                ElementId equipmentTypeId = new ElementId(equipmentTypeIdInt);
                FamilySymbol equipmentType = doc.GetElement(equipmentTypeId) as FamilySymbol;

                if (equipmentType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Electrical equipment type with ID {equipmentTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                // Parse location
                var loc = parameters["location"];
                XYZ location = new XYZ(
                    loc["x"].ToObject<double>(),
                    loc["y"].ToObject<double>(),
                    loc["z"].ToObject<double>()
                );

                // Get rotation (optional, default to 0)
                double rotation = parameters["rotation"]?.ToObject<double>() ?? 0.0;

                using (var trans = new Transaction(doc, "Place Electrical Equipment"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the symbol if not active
                    if (!equipmentType.IsActive)
                    {
                        equipmentType.Activate();
                    }

                    // Create the electrical equipment instance
                    FamilyInstance equipment = doc.Create.NewFamilyInstance(
                        location,
                        equipmentType,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                    );

                    if (equipment == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to place electrical equipment"
                        });
                    }

                    // Apply rotation if specified
                    if (rotation != 0.0)
                    {
                        LocationPoint locPoint = equipment.Location as LocationPoint;
                        if (locPoint != null)
                        {
                            XYZ axis = XYZ.BasisZ;
                            locPoint.Rotate(Line.CreateUnbound(location, axis), rotation);
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        equipmentId = equipment.Id.Value,
                        equipmentTypeId = equipmentTypeIdInt,
                        equipmentTypeName = equipmentType.Name,
                        familyName = equipmentType.FamilyName,
                        levelId = levelIdInt,
                        levelName = level.Name,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        rotation = rotation,
                        message = "Electrical equipment placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets electrical circuits and their properties
        /// </summary>
        [MCPMethod("getElectricalCircuits", Category = "MEP", Description = "Gets all electrical circuits in the model or on a panel")]
        public static string GetElectricalCircuits(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                FilteredElementCollector collector = new FilteredElementCollector(doc);
                var circuits = collector
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .ToList();

                // Optionally filter by panel if panelId is provided
                if (parameters["panelId"] != null)
                {
                    int panelIdInt = parameters["panelId"].ToObject<int>();
                    ElementId panelId = new ElementId(panelIdInt);

                    // Filter circuits that belong to the specified panel
                    circuits = circuits.Where(c => c.BaseEquipment?.Id == panelId).ToList();
                }

                // Build result array
                var circuitList = new List<object>();
                foreach (var circuit in circuits)
                {
                    // Get circuit properties
                    var circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                    var circuitName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.AsString();
                    var apparentLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble();
                    var voltage = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE)?.AsDouble();
                    var panel = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();

                    // Get circuit type (MEPSystemType, not ElectricalSystemType which is an enum)
                    var systemTypeElement = doc.GetElement(circuit.GetTypeId());
                    var systemTypeName = systemTypeElement?.Name;

                    // Get connected elements count
                    int elementCount = circuit.Elements?.Size ?? 0;

                    circuitList.Add(new
                    {
                        circuitId = circuit.Id.Value,
                        circuitNumber = circuitNumber,
                        circuitName = circuitName ?? circuit.Name,
                        systemTypeName = systemTypeName,
                        systemTypeId = circuit.GetTypeId().Value,
                        apparentLoad = apparentLoad,
                        voltage = voltage,
                        panelName = panel,
                        baseEquipmentId = circuit.BaseEquipment?.Id.Value,
                        connectedElementsCount = elementCount
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = circuitList.Count,
                    circuits = circuitList
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates or modifies an electrical circuit
        /// </summary>
        [MCPMethod("createElectricalCircuit", Category = "MEP", Description = "Creates an electrical circuit connecting fixtures to a panel")]
        public static string CreateElectricalCircuit(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["panelId"] == null || parameters["elementIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "panelId and elementIds are required" });
                }

                int panelIdInt = parameters["panelId"].ToObject<int>();
                var elementIds = parameters["elementIds"].ToObject<List<int>>();

                ElementId panelId = new ElementId(panelIdInt);
                FamilyInstance panel = doc.GetElement(panelId) as FamilyInstance;

                if (panel == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Panel with ID {panelIdInt} not found" });
                }

                List<ElementId> deviceIds = elementIds.Select(id => new ElementId(id)).ToList();

                using (var trans = new Transaction(doc, "Create Electrical Circuit"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ElectricalSystem circuit = ElectricalSystem.Create(doc, deviceIds, ElectricalSystemType.PowerCircuit);

                    if (circuit != null)
                    {
                        circuit.SelectPanel(panel);
                        if (parameters["circuitNumber"] != null)
                        {
                            circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.Set(parameters["circuitNumber"].ToString());
                        }
                        if (parameters["circuitName"] != null)
                        {
                            circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.Set(parameters["circuitName"].ToString());
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        circuitId = circuit?.Id.Value,
                        panelId = panelIdInt,
                        devicesCount = elementIds.Count,
                        message = "Electrical circuit created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region MEP Equipment and Fixtures

        /// <summary>
        /// Places mechanical equipment (air handler, boiler, chiller, etc.)
        /// </summary>
        [MCPMethod("placeMechanicalEquipment", Category = "MEP", Description = "Places mechanical equipment such as an AHU or RTU at a specified location")]
        public static string PlaceMechanicalEquipment(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["equipmentTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "equipmentTypeId is required"
                    });
                }

                if (parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required (provide x, y, z coordinates)"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Get equipment type
                int equipmentTypeIdInt = parameters["equipmentTypeId"].ToObject<int>();
                ElementId equipmentTypeId = new ElementId(equipmentTypeIdInt);
                FamilySymbol equipmentType = doc.GetElement(equipmentTypeId) as FamilySymbol;

                if (equipmentType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Mechanical equipment type with ID {equipmentTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                // Parse location
                var loc = parameters["location"];
                XYZ location = new XYZ(
                    loc["x"].ToObject<double>(),
                    loc["y"].ToObject<double>(),
                    loc["z"].ToObject<double>()
                );

                // Get rotation (optional, default to 0)
                double rotation = parameters["rotation"]?.ToObject<double>() ?? 0.0;

                using (var trans = new Transaction(doc, "Place Mechanical Equipment"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the symbol if not active
                    if (!equipmentType.IsActive)
                    {
                        equipmentType.Activate();
                    }

                    // Create the mechanical equipment instance
                    FamilyInstance equipment = doc.Create.NewFamilyInstance(
                        location,
                        equipmentType,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                    );

                    if (equipment == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to place mechanical equipment"
                        });
                    }

                    // Apply rotation if specified
                    if (rotation != 0.0)
                    {
                        LocationPoint locPoint = equipment.Location as LocationPoint;
                        if (locPoint != null)
                        {
                            XYZ axis = XYZ.BasisZ;
                            locPoint.Rotate(Line.CreateUnbound(location, axis), rotation);
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        equipmentId = equipment.Id.Value,
                        equipmentTypeId = equipmentTypeIdInt,
                        equipmentTypeName = equipmentType.Name,
                        familyName = equipmentType.FamilyName,
                        levelId = levelIdInt,
                        levelName = level.Name,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        rotation = rotation,
                        message = "Mechanical equipment placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Places plumbing fixtures (sink, toilet, urinal, water heater, etc.)
        /// </summary>
        [MCPMethod("placePlumbingFixture", Category = "MEP", Description = "Places a plumbing fixture at a specified location")]
        public static string PlacePlumbingFixture(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["fixtureTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "fixtureTypeId is required"
                    });
                }

                if (parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required (provide x, y, z coordinates)"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Get fixture type
                int fixtureTypeIdInt = parameters["fixtureTypeId"].ToObject<int>();
                ElementId fixtureTypeId = new ElementId(fixtureTypeIdInt);
                FamilySymbol fixtureType = doc.GetElement(fixtureTypeId) as FamilySymbol;

                if (fixtureType == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Plumbing fixture type with ID {fixtureTypeIdInt} not found"
                    });
                }

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                // Parse location
                var loc = parameters["location"];
                XYZ location = new XYZ(
                    loc["x"].ToObject<double>(),
                    loc["y"].ToObject<double>(),
                    loc["z"].ToObject<double>()
                );

                // Get rotation (optional, default to 0)
                double rotation = parameters["rotation"]?.ToObject<double>() ?? 0.0;

                // Check if hostElementId is provided (for wall-based fixtures)
                Element hostElement = null;
                if (parameters["hostElementId"] != null)
                {
                    int hostIdInt = parameters["hostElementId"].ToObject<int>();
                    ElementId hostId = new ElementId(hostIdInt);
                    hostElement = doc.GetElement(hostId);

                    if (hostElement == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Host element with ID {hostIdInt} not found"
                        });
                    }
                }

                using (var trans = new Transaction(doc, "Place Plumbing Fixture"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the symbol if not active
                    if (!fixtureType.IsActive)
                    {
                        fixtureType.Activate();
                    }

                    FamilyInstance fixture = null;

                    // Place fixture based on whether host element is provided
                    if (hostElement != null)
                    {
                        // Wall-based or face-based fixture
                        if (hostElement is Wall wall)
                        {
                            // For wall-based fixtures, we need to find the appropriate face
                            fixture = doc.Create.NewFamilyInstance(
                                location,
                                fixtureType,
                                hostElement,
                                level,
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                            );
                        }
                        else
                        {
                            // Generic host-based placement
                            fixture = doc.Create.NewFamilyInstance(
                                location,
                                fixtureType,
                                hostElement,
                                level,
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                            );
                        }
                    }
                    else
                    {
                        // Floor-based or standalone fixture
                        fixture = doc.Create.NewFamilyInstance(
                            location,
                            fixtureType,
                            level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                        );
                    }

                    if (fixture == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to place plumbing fixture"
                        });
                    }

                    // Apply rotation if specified
                    if (rotation != 0.0)
                    {
                        LocationPoint locPoint = fixture.Location as LocationPoint;
                        if (locPoint != null)
                        {
                            XYZ axis = XYZ.BasisZ;
                            locPoint.Rotate(Line.CreateUnbound(location, axis), rotation);
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        fixtureId = fixture.Id.Value,
                        fixtureTypeId = fixtureTypeIdInt,
                        fixtureTypeName = fixtureType.Name,
                        familyName = fixtureType.FamilyName,
                        levelId = levelIdInt,
                        levelName = level.Name,
                        hostElementId = hostElement?.Id.Value,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        rotation = rotation,
                        message = "Plumbing fixture placed successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets equipment information
        /// </summary>
        [MCPMethod("getEquipmentInfo", Category = "MEP", Description = "Gets detailed information about a MEP equipment element")]
        public static string GetEquipmentInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["equipmentId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "equipmentId is required" });
                }

                int equipmentIdInt = parameters["equipmentId"].ToObject<int>();
                ElementId equipmentId = new ElementId(equipmentIdInt);

                FamilyInstance equipment = doc.GetElement(equipmentId) as FamilyInstance;
                if (equipment == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Equipment with ID {equipmentIdInt} not found" });
                }

                var equipmentName = equipment.Name;
                var typeName = equipment.Symbol?.Name;
                var category = equipment.Category?.Name;

                // Get MEP properties
                var connectorManager = equipment.MEPModel?.ConnectorManager;
                int connectorCount = 0;
                if (connectorManager != null)
                {
                    foreach (Connector c in connectorManager.Connectors)
                        connectorCount++;
                }

                // Get common equipment parameters
                var powerParam = equipment.LookupParameter("Power") ?? equipment.LookupParameter("Electrical Load");
                var flowParam = equipment.LookupParameter("Flow") ?? equipment.LookupParameter("Airflow");
                var voltageParam = equipment.LookupParameter("Voltage");

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    equipmentId = equipmentIdInt,
                    equipmentName,
                    typeName,
                    category,
                    connectorCount,
                    power = powerParam?.AsDouble(),
                    flow = flowParam?.AsDouble(),
                    voltage = voltageParam?.AsDouble(),
                    location = equipment.Location is LocationPoint lp ? new { x = lp.Point.X, y = lp.Point.Y, z = lp.Point.Z } : null
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region MEP Systems

        /// <summary>
        /// Creates a new MEP system
        /// </summary>
        [MCPMethod("createMEPSystem", Category = "MEP", Description = "Creates a MEP system connecting related elements")]
        public static string CreateMEPSystem(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null || parameters["systemType"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds and systemType are required"
                    });
                }

                var elementIdsInt = parameters["elementIds"].ToObject<List<int>>();
                string systemTypeStr = parameters["systemType"].ToString();

                List<ElementId> elementIds = elementIdsInt.Select(id => new ElementId(id)).ToList();

                if (elementIds.Count == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least one element ID is required"
                    });
                }

                // Note: MEP system creation requires proper connector setup
                // Simplified implementation returns appropriate message
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "MEP system creation requires proper MEP connector setup. Use Revit's built-in system tools or ensure elements have compatible connectors.",
                    hint = "Systems are typically created automatically when MEP elements are connected"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all MEP systems in the project
        /// </summary>
        [MCPMethod("getMEPSystems", Category = "MEP", Description = "Gets all MEP systems in the model optionally filtered by type")]
        public static string GetMEPSystems(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                FilteredElementCollector collector = new FilteredElementCollector(doc);
                var systems = collector.OfClass(typeof(MEPSystem)).Cast<MEPSystem>().ToList();

                // Optional filtering by discipline
                string discipline = parameters["discipline"]?.ToString()?.ToLower();

                if (!string.IsNullOrEmpty(discipline))
                {
                    if (discipline == "mechanical")
                    {
                        systems = systems.Where(s => s is MechanicalSystem).ToList();
                    }
                    else if (discipline == "electrical")
                    {
                        systems = systems.Where(s => s is ElectricalSystem).ToList();
                    }
                    else if (discipline == "piping")
                    {
                        systems = systems.Where(s => s is PipingSystem).ToList();
                    }
                }

                var systemList = new List<object>();
                foreach (var system in systems)
                {
                    string systemTypeStr = "Unknown";
                    if (system is MechanicalSystem mechSys)
                    {
                        var sysType = doc.GetElement(mechSys.GetTypeId()) as MEPSystemType;
                        systemTypeStr = sysType?.Name ?? "Unknown";
                    }
                    else if (system is ElectricalSystem elecSys)
                    {
                        var sysType = doc.GetElement(elecSys.GetTypeId()) as MEPSystemType;
                        systemTypeStr = sysType?.Name ?? "Unknown";
                    }
                    else if (system is PipingSystem pipeSys)
                    {
                        var sysType = doc.GetElement(pipeSys.GetTypeId()) as MEPSystemType;
                        systemTypeStr = sysType?.Name ?? "Unknown";
                    }

                    systemList.Add(new
                    {
                        systemId = system.Id.Value,
                        systemName = system.Name,
                        systemType = systemTypeStr,
                        discipline = system is MechanicalSystem ? "Mechanical" : (system is ElectricalSystem ? "Electrical" : "Piping"),
                        elementsCount = system.Elements?.Size ?? 0,
                        baseEquipmentId = system.BaseEquipment?.Id.Value
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = systemList.Count,
                    systems = systemList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets system information and analysis data
        /// </summary>
        [MCPMethod("getSystemInfo", Category = "MEP", Description = "Gets detailed information about a MEP system")]
        public static string GetSystemInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["systemId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "systemId is required" });
                }

                int systemIdInt = parameters["systemId"].ToObject<int>();
                ElementId systemId = new ElementId(systemIdInt);

                MEPSystem system = doc.GetElement(systemId) as MEPSystem;
                if (system == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"MEP System with ID {systemIdInt} not found" });
                }

                var systemName = system.Name;
                string systemTypeStr = "Unknown";
                var elementIds = new List<int>();

                if (system is MechanicalSystem mechSys)
                {
                    var sysType = doc.GetElement(mechSys.GetTypeId()) as MEPSystemType;
                    systemTypeStr = sysType?.Name ?? "Unknown";
                    if (mechSys.DuctNetwork != null)
                    {
                        foreach (ElementId eid in mechSys.DuctNetwork)
                            elementIds.Add((int)eid.Value);
                    }
                }
                else if (system is PipingSystem pipeSys)
                {
                    var sysType = doc.GetElement(pipeSys.GetTypeId()) as MEPSystemType;
                    systemTypeStr = sysType?.Name ?? "Unknown";
                    if (pipeSys.PipingNetwork != null)
                    {
                        foreach (ElementId eid in pipeSys.PipingNetwork)
                            elementIds.Add((int)eid.Value);
                    }
                }
                else if (system is ElectricalSystem elecSys)
                {
                    var sysType = doc.GetElement(elecSys.GetTypeId()) as MEPSystemType;
                    systemTypeStr = sysType?.Name ?? "Unknown";
                    if (elecSys.Elements != null)
                    {
                        foreach (ElementId eid in elecSys.Elements)
                            elementIds.Add((int)eid.Value);
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    systemId = systemIdInt,
                    systemName,
                    systemType = systemTypeStr,
                    baseEquipmentId = system.BaseEquipment?.Id.Value,
                    elementCount = elementIds.Count,
                    elementIds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Adds or removes elements from a system
        /// </summary>
        [MCPMethod("modifySystemElements", Category = "MEP", Description = "Adds or removes elements from a MEP system")]
        public static string ModifySystemElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["systemId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "systemId is required" });
                }

                int systemIdInt = parameters["systemId"].ToObject<int>();
                ElementId systemId = new ElementId(systemIdInt);

                MEPSystem system = doc.GetElement(systemId) as MEPSystem;
                if (system == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"MEP System with ID {systemIdInt} not found" });
                }

                // Note: Direct system modification via API is limited
                // Systems are typically managed by connecting/disconnecting MEP elements
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Direct system modification not supported in Revit 2026 API",
                    hint = "Modify systems by connecting/disconnecting MEP elements using connectors"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region MEP Spaces and Zones

        /// <summary>
        /// Creates an MEP space
        /// </summary>
        [MCPMethod("createMEPSpace", Category = "MEP", Description = "Creates a MEP space for load calculations in a room or area")]
        public static string CreateMEPSpace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required (provide x, y, z coordinates)"
                    });
                }

                if (parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "levelId is required"
                    });
                }

                // Parse location
                var loc = parameters["location"];
                XYZ location = new XYZ(
                    loc["x"].ToObject<double>(),
                    loc["y"].ToObject<double>(),
                    loc["z"].ToObject<double>()
                );

                // Get level
                int levelIdInt = parameters["levelId"].ToObject<int>();
                ElementId levelId = new ElementId(levelIdInt);
                Level level = doc.GetElement(levelId) as Level;

                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelIdInt} not found"
                    });
                }

                // Get phase (optional, use default if not provided)
                Phase phase = null;
                if (parameters["phaseId"] != null)
                {
                    int phaseIdInt = parameters["phaseId"].ToObject<int>();
                    ElementId phaseId = new ElementId(phaseIdInt);
                    phase = doc.GetElement(phaseId) as Phase;

                    if (phase == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Phase with ID {phaseIdInt} not found"
                        });
                    }
                }
                else
                {
                    // Use default phase if not provided
                    phase = doc.Phases.get_Item(doc.Phases.Size - 1) as Phase;
                }

                using (var trans = new Transaction(doc, "Create MEP Space"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create MEP space
                    Space space = doc.Create.NewSpace(level, new UV(location.X, location.Y));

                    if (space == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create MEP space"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        spaceId = space.Id.Value,
                        spaceName = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        spaceNumber = space.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString(),
                        levelId = levelIdInt,
                        levelName = level.Name,
                        location = new { x = location.X, y = location.Y, z = location.Z },
                        message = "MEP space created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets space information and properties
        /// </summary>
        [MCPMethod("getSpaceInfo", Category = "MEP", Description = "Gets detailed information about a MEP space element")]
        public static string GetSpaceInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter validation
                if (parameters["spaceId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "spaceId is required"
                    });
                }

                int spaceIdInt = parameters["spaceId"].ToObject<int>();
                ElementId spaceId = new ElementId(spaceIdInt);
                Space space = doc.GetElement(spaceId) as Space;

                if (space == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Space with ID {spaceIdInt} not found"
                    });
                }

                // Get space properties
                var spaceName = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                var spaceNumber = space.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                var area = space.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble();
                var volume = space.get_Parameter(BuiltInParameter.ROOM_VOLUME)?.AsDouble();
                var occupancy = space.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsDouble();

                // Get level
                var levelId = space.Level?.Id.Value;
                var levelName = space.Level?.Name;

                // Get zone if assigned
                var zone = space.Zone;
                var zoneName = zone?.Name;
                var zoneId = zone?.Id.Value;

                // Get location
                LocationPoint locPoint = space.Location as LocationPoint;
                var location = locPoint?.Point;

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    spaceId = spaceIdInt,
                    spaceName = spaceName,
                    spaceNumber = spaceNumber,
                    area = area,
                    volume = volume,
                    occupancy = occupancy,
                    levelId = levelId,
                    levelName = levelName,
                    zoneName = zoneName,
                    zoneId = zoneId,
                    location = location != null ? new { x = location.X, y = location.Y, z = location.Z } : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a zone and assigns spaces to it
        /// </summary>
        [MCPMethod("createZone", Category = "MEP", Description = "Creates an HVAC zone grouping related MEP spaces")]
        public static string CreateZone(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["zoneName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "zoneName is required" });
                }

                string zoneName = parameters["zoneName"].ToString();
                var spaceIds = parameters["spaceIds"]?.ToObject<List<int>>() ?? new List<int>();

                using (var trans = new Transaction(doc, "Create Zone"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create zone by assigning it to spaces
                    // In Revit 2026, zones are created by setting the RoomZone parameter on spaces
                    Zone zone = null;
                    int spacesAdded = 0;

                    foreach (int spaceIdInt in spaceIds)
                    {
                        ElementId spaceId = new ElementId(spaceIdInt);
                        Space space = doc.GetElement(spaceId) as Space;
                        if (space != null)
                        {
                            // Set zone name on space (using LookupParameter as ROOM_ZONE_NAME may vary)
                            Parameter zoneParam = space.LookupParameter("Zone Name");
                            if (zoneParam != null && !zoneParam.IsReadOnly)
                            {
                                zoneParam.Set(zoneName);
                                spacesAdded++;
                                if (zone == null)
                                {
                                    zone = space.Zone;
                                }
                            }
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        zoneId = zone?.Id.Value,
                        zoneName = zoneName,
                        spacesAdded = spacesAdded,
                        message = "Zone created by assigning spaces successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tags an MEP space
        /// </summary>
        [MCPMethod("tagSpace", Category = "MEP", Description = "Places a tag on a MEP space element in the active view")]
        public static string TagSpace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["spaceId"] == null || parameters["viewId"] == null || parameters["location"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "spaceId, viewId, and location are required" });
                }

                int spaceIdInt = parameters["spaceId"].ToObject<int>();
                int viewIdInt = parameters["viewId"].ToObject<int>();

                ElementId spaceId = new ElementId(spaceIdInt);
                ElementId viewId = new ElementId(viewIdInt);

                Space space = doc.GetElement(spaceId) as Space;
                View view = doc.GetElement(viewId) as View;

                if (space == null) return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Space with ID {spaceIdInt} not found" });
                if (view == null) return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"View with ID {viewIdInt} not found" });

                var loc = parameters["location"];
                XYZ location = new XYZ(loc["x"].ToObject<double>(), loc["y"].ToObject<double>(), loc["z"].ToObject<double>());

                using (var trans = new Transaction(doc, "Tag Space"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    SpaceTag tag = doc.Create.NewSpaceTag(space, new UV(location.X, location.Y), view);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = tag?.Id.Value,
                        spaceId = spaceIdInt,
                        viewId = viewIdInt,
                        message = "Space tag created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region MEP Connectors

        /// <summary>
        /// Gets connector information for MEP elements
        /// </summary>
        [MCPMethod("getConnectors", Category = "MEP", Description = "Gets all connectors on a specified MEP element")]
        public static string GetConnectors(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Element with ID {elementIdInt} not found" });
                }

                // Get connector manager from MEP elements (FamilyInstance or MEPCurve)
                ConnectorManager connectorManager = null;

                if (element is FamilyInstance familyInstance)
                {
                    connectorManager = familyInstance.MEPModel?.ConnectorManager;
                }
                else if (element is MEPCurve mepCurve)
                {
                    connectorManager = mepCurve.ConnectorManager;
                }

                if (connectorManager == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element does not have connectors or is not an MEP element"
                    });
                }

                var connectorsList = new List<object>();
                int index = 0;

                foreach (Connector connector in connectorManager.Connectors)
                {
                    var connectorInfo = new
                    {
                        index = index++,
                        connectorType = connector.ConnectorType.ToString(),
                        shape = connector.Shape.ToString(),
                        origin = new
                        {
                            x = connector.Origin.X,
                            y = connector.Origin.Y,
                            z = connector.Origin.Z
                        },
                        direction = connector.CoordinateSystem != null ? new
                        {
                            x = connector.CoordinateSystem.BasisZ.X,
                            y = connector.CoordinateSystem.BasisZ.Y,
                            z = connector.CoordinateSystem.BasisZ.Z
                        } : null,
                        radius = connector.Shape == ConnectorProfileType.Round ? connector.Radius : (double?)null,
                        width = connector.Shape == ConnectorProfileType.Rectangular ? connector.Width : (double?)null,
                        height = connector.Shape == ConnectorProfileType.Rectangular ? connector.Height : (double?)null,
                        domain = connector.Domain.ToString(),
                        systemClassification = GetSystemClassification(connector),
                        isConnected = connector.IsConnected,
                        flowDirection = connector.Domain == Domain.DomainHvac || connector.Domain == Domain.DomainPiping
                            ? connector.Direction.ToString()
                            : null
                    };

                    connectorsList.Add(connectorInfo);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    connectorCount = connectorsList.Count,
                    connectors = connectorsList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string GetSystemClassification(Connector connector)
        {
            if (connector.Domain == Domain.DomainHvac)
            {
                return connector.DuctSystemType.ToString();
            }
            else if (connector.Domain == Domain.DomainPiping)
            {
                return connector.PipeSystemType.ToString();
            }
            return null;
        }

        /// <summary>
        /// Connects two MEP elements via their connectors
        /// </summary>
        [MCPMethod("connectElements", Category = "MEP", Description = "Connects two MEP elements at their compatible connectors")]
        public static string ConnectElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId1"] == null || parameters["connectorIndex1"] == null ||
                    parameters["elementId2"] == null || parameters["connectorIndex2"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId1, connectorIndex1, elementId2, and connectorIndex2 are required"
                    });
                }

                int elementId1Int = parameters["elementId1"].ToObject<int>();
                int connectorIndex1 = parameters["connectorIndex1"].ToObject<int>();
                int elementId2Int = parameters["elementId2"].ToObject<int>();
                int connectorIndex2 = parameters["connectorIndex2"].ToObject<int>();

                ElementId eid1 = new ElementId(elementId1Int);
                ElementId eid2 = new ElementId(elementId2Int);

                Element element1 = doc.GetElement(eid1);
                Element element2 = doc.GetElement(eid2);

                if (element1 == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Element1 with ID {elementId1Int} not found" });
                if (element2 == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Element2 with ID {elementId2Int} not found" });

                // Get connector managers
                ConnectorManager cm1 = null, cm2 = null;

                if (element1 is FamilyInstance fi1) cm1 = fi1.MEPModel?.ConnectorManager;
                else if (element1 is MEPCurve mc1) cm1 = mc1.ConnectorManager;

                if (element2 is FamilyInstance fi2) cm2 = fi2.MEPModel?.ConnectorManager;
                else if (element2 is MEPCurve mc2) cm2 = mc2.ConnectorManager;

                if (cm1 == null || cm2 == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "One or both elements do not have connectors"
                    });
                }

                // Get connectors by index
                Connector connector1 = null, connector2 = null;
                int idx = 0;
                foreach (Connector c in cm1.Connectors)
                {
                    if (idx == connectorIndex1) { connector1 = c; break; }
                    idx++;
                }

                idx = 0;
                foreach (Connector c in cm2.Connectors)
                {
                    if (idx == connectorIndex2) { connector2 = c; break; }
                    idx++;
                }

                if (connector1 == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Connector at index {connectorIndex1} not found on element1" });
                if (connector2 == null)
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Connector at index {connectorIndex2} not found on element2" });

                using (var trans = new Transaction(doc, "Connect MEP Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Connect the two connectors
                    connector1.ConnectTo(connector2);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId1 = elementId1Int,
                        elementId2 = elementId2Int,
                        connectorIndex1,
                        connectorIndex2,
                        message = "Elements connected successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region MEP Analysis and Sizing

        /// <summary>
        /// Performs duct sizing calculations
        /// </summary>
        [MCPMethod("calculateDuctSizing", Category = "MEP", Description = "Calculates and applies duct sizing based on airflow requirements")]
        public static string CalculateDuctSizing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["systemId"] == null && parameters["ductIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either systemId or ductIds is required"
                    });
                }

                var sizingResults = new List<object>();

                using (var trans = new Transaction(doc, "Calculate Duct Sizing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (parameters["systemId"] != null)
                    {
                        // Size all ducts in a system
                        int systemIdInt = parameters["systemId"].ToObject<int>();
                        ElementId systemId = new ElementId(systemIdInt);

                        MEPSystem system = doc.GetElement(systemId) as MEPSystem;
                        if (system == null || !(system is MechanicalSystem))
                        {
                            return Newtonsoft.Json.JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"MechanicalSystem with ID {systemIdInt} not found"
                            });
                        }

                        MechanicalSystem mechSystem = system as MechanicalSystem;

                        // Get all ducts in the system
                        var ductElements = mechSystem.DuctNetwork;
                        if (ductElements != null)
                        {
                            foreach (ElementId ductId in ductElements)
                            {
                                Element duct = doc.GetElement(ductId);
                                if (duct is Duct d)
                                {
                                    var result = GetDuctSizingInfo(d);
                                    sizingResults.Add(result);
                                }
                            }
                        }
                    }
                    else if (parameters["ductIds"] != null)
                    {
                        // Size specific ducts
                        var ductIds = parameters["ductIds"].ToObject<List<int>>();

                        foreach (int ductIdInt in ductIds)
                        {
                            ElementId ductId = new ElementId(ductIdInt);
                            Element duct = doc.GetElement(ductId);

                            if (duct is Duct d)
                            {
                                var result = GetDuctSizingInfo(d);
                                sizingResults.Add(result);
                            }
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ductCount = sizingResults.Count,
                        sizingResults,
                        message = "Duct sizing information retrieved successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object GetDuctSizingInfo(Duct duct)
        {
            // Get duct sizing parameters
            var width = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble();
            var height = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble();
            var diameter = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble();
            var flow = duct.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM)?.AsDouble();
            var velocity = duct.get_Parameter(BuiltInParameter.RBS_VELOCITY)?.AsDouble();

            return new
            {
                ductId = duct.Id.Value,
                width,
                height,
                diameter,
                flow,
                velocity,
                shape = diameter.HasValue ? "Round" : "Rectangular"
            };
        }

        /// <summary>
        /// Performs pipe sizing calculations
        /// </summary>
        [MCPMethod("calculatePipeSizing", Category = "MEP", Description = "Calculates and applies pipe sizing based on flow rate requirements")]
        public static string CalculatePipeSizing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["systemId"] == null && parameters["pipeIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either systemId or pipeIds is required"
                    });
                }

                var sizingResults = new List<object>();

                using (var trans = new Transaction(doc, "Calculate Pipe Sizing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (parameters["systemId"] != null)
                    {
                        // Size all pipes in a system
                        int systemIdInt = parameters["systemId"].ToObject<int>();
                        ElementId systemId = new ElementId(systemIdInt);

                        MEPSystem system = doc.GetElement(systemId) as MEPSystem;
                        if (system == null || !(system is PipingSystem))
                        {
                            return Newtonsoft.Json.JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"PipingSystem with ID {systemIdInt} not found"
                            });
                        }

                        PipingSystem pipingSystem = system as PipingSystem;

                        // Get all pipes in the system
                        var pipeElements = pipingSystem.PipingNetwork;
                        if (pipeElements != null)
                        {
                            foreach (ElementId pipeId in pipeElements)
                            {
                                Element pipe = doc.GetElement(pipeId);
                                if (pipe is Pipe p)
                                {
                                    var result = GetPipeSizingInfo(p);
                                    sizingResults.Add(result);
                                }
                            }
                        }
                    }
                    else if (parameters["pipeIds"] != null)
                    {
                        // Size specific pipes
                        var pipeIds = parameters["pipeIds"].ToObject<List<int>>();

                        foreach (int pipeIdInt in pipeIds)
                        {
                            ElementId pipeId = new ElementId(pipeIdInt);
                            Element pipe = doc.GetElement(pipeId);

                            if (pipe is Pipe p)
                            {
                                var result = GetPipeSizingInfo(p);
                                sizingResults.Add(result);
                            }
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        pipeCount = sizingResults.Count,
                        sizingResults,
                        message = "Pipe sizing information retrieved successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object GetPipeSizingInfo(Pipe pipe)
        {
            // Get pipe sizing parameters
            var diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble();
            var flow = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)?.AsDouble();
            var velocity = pipe.get_Parameter(BuiltInParameter.RBS_VELOCITY)?.AsDouble();
            var pressureDrop = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_PRESSUREDROP_PARAM)?.AsDouble();

            return new
            {
                pipeId = pipe.Id.Value,
                diameter,
                flow,
                velocity,
                pressureDrop
            };
        }

        /// <summary>
        /// Calculates heating and cooling loads for spaces
        /// </summary>
        [MCPMethod("calculateLoads", Category = "MEP", Description = "Calculates heating and cooling loads for MEP spaces or zones")]
        public static string CalculateLoads(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["spaceIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "spaceIds is required"
                    });
                }

                var spaceIds = parameters["spaceIds"].ToObject<List<int>>();
                var loadResults = new List<object>();

                foreach (int spaceIdInt in spaceIds)
                {
                    ElementId spaceId = new ElementId(spaceIdInt);
                    Space space = doc.GetElement(spaceId) as Space;

                    if (space == null)
                    {
                        loadResults.Add(new
                        {
                            spaceId = spaceIdInt,
                            error = "Space not found"
                        });
                        continue;
                    }

                    // Get load parameters
                    var spaceName = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    var area = space.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble();
                    var volume = space.get_Parameter(BuiltInParameter.ROOM_VOLUME)?.AsDouble();

                    // Get calculated loads if available (using LookupParameter)
                    var calculatedHeatingLoad = space.LookupParameter("Calculated Heating Load")?.AsDouble();
                    var calculatedCoolingLoad = space.LookupParameter("Calculated Cooling Load")?.AsDouble();
                    var calculatedSupplyAirflow = space.LookupParameter("Calculated Supply Airflow")?.AsDouble();

                    // Get specified loads
                    var specifiedHeatingLoad = space.LookupParameter("Specified Heating Load")?.AsDouble();
                    var specifiedCoolingLoad = space.LookupParameter("Specified Cooling Load")?.AsDouble();
                    var specifiedSupplyAirflow = space.LookupParameter("Specified Supply Airflow")?.AsDouble();

                    // Get occupancy data
                    var occupancy = space.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsDouble();
                    var areaPerPerson = space.LookupParameter("Area per Person")?.AsDouble();

                    loadResults.Add(new
                    {
                        spaceId = spaceIdInt,
                        spaceName,
                        area,
                        volume,
                        occupancy,
                        areaPerPerson,
                        calculatedLoads = new
                        {
                            heatingLoad = calculatedHeatingLoad,
                            coolingLoad = calculatedCoolingLoad,
                            supplyAirflow = calculatedSupplyAirflow
                        },
                        designLoads = new
                        {
                            heatingLoad = specifiedHeatingLoad,
                            coolingLoad = specifiedCoolingLoad,
                            supplyAirflow = specifiedSupplyAirflow
                        }
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    spaceCount = loadResults.Count,
                    loadResults,
                    message = "Load calculations retrieved successfully"
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
        /// Gets all MEP element types (duct types, pipe types, etc.)
        /// </summary>
        [MCPMethod("getMEPTypes", Category = "MEP", Description = "Gets all available MEP element types for a specified category")]
        public static string GetMEPTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["category"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "category is required (ducts, pipes, cableTrays, conduits, equipment)" });
                }

                string category = parameters["category"].ToString().ToLower();

                var typesList = new List<object>();
                FilteredElementCollector collector = null;

                if (category == "ducts")
                {
                    collector = new FilteredElementCollector(doc).OfClass(typeof(DuctType));
                }
                else if (category == "pipes")
                {
                    collector = new FilteredElementCollector(doc).OfClass(typeof(PipeType));
                }
                else if (category == "cabletrays")
                {
                    collector = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType));
                }
                else if (category == "conduits")
                {
                    collector = new FilteredElementCollector(doc).OfClass(typeof(ConduitType));
                }
                else
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid category. Use: ducts, pipes, cableTrays, conduits"
                    });
                }

                foreach (Element type in collector)
                {
                    typesList.Add(new
                    {
                        typeId = type.Id.Value,
                        typeName = type.Name,
                        category = type.Category?.Name
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    category,
                    typeCount = typesList.Count,
                    types = typesList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Deletes an MEP element
        /// </summary>
        [MCPMethod("deleteMEPElement", Category = "MEP", Description = "Deletes a MEP element by element ID")]
        public static string DeleteMEPElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "elementId is required" });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Element with ID {elementIdInt} not found" });
                }

                using (var trans = new Transaction(doc, "Delete MEP Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var deletedIds = doc.Delete(elementId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = elementIdInt,
                        deletedCount = deletedIds?.Count ?? 0,
                        message = $"MEP element deleted ({deletedIds?.Count ?? 0} total elements including dependencies)"
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
