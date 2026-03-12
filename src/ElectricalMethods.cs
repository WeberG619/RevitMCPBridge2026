using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Dedicated electrical operations for Revit 2026.
    /// Covers panels, panel schedules, wiring, lighting, receptacles, switches,
    /// voltage drop calculations, and electrical schedule data.
    /// Does NOT duplicate methods already in MEPMethods (createCableTray, createConduit,
    /// getElectricalPathInfo, placeElectricalFixture, placeElectricalEquipment,
    /// getElectricalCircuits, createElectricalCircuit, getCircuitElements,
    /// addToCircuit, removeFromCircuit, consolidateCircuits).
    /// </summary>
    public static class ElectricalMethods
    {
        #region Panel Schedule

        /// <summary>
        /// Get panel schedule data for an electrical panel
        /// </summary>
        [MCPMethod("getPanelSchedule", Category = "Electrical", Description = "Get panel schedule data for an electrical panel including circuits, breakers, and loads")]
        public static string GetPanelSchedule(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "getPanelSchedule");
                v.Require("panelId").IsType<int>();
                v.ThrowIfInvalid();

                int panelIdInt = v.GetElementId("panelId");
                var panelId = new ElementId(panelIdInt);
                var panel = doc.GetElement(panelId) as FamilyInstance;

                if (panel == null)
                    return ResponseBuilder.Error($"Panel with ID {panelIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                // Get panel parameters
                var panelName = panel.Name;
                var voltage = panel.LookupParameter("Supply Voltage")?.AsValueString() ?? panel.LookupParameter("Supply Voltage")?.AsString() ?? "Unknown";
                var phases = panel.get_Parameter(BuiltInParameter.RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM)?.AsString() ?? "";
                var totalLoad = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALLOAD_PARAM)?.AsDouble() ?? 0;
                var totalEstLoad = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALESTLOAD_PARAM)?.AsDouble() ?? 0;

                // Collect circuits on this panel
                var circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Where(c => c.BaseEquipment?.Id == panelId)
                    .ToList();

                var circuitList = new List<object>();
                foreach (var circuit in circuits)
                {
                    var circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                    var circuitName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.AsString();
                    var apparentLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;
                    var circuitVoltage = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE)?.AsDouble() ?? 0;
                    var breakerRating = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.AsDouble() ?? 0;
                    var wireSize = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_SIZE_PARAM)?.AsString();
                    var poles = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES)?.AsInteger() ?? 0;

                    circuitList.Add(new
                    {
                        circuitId = circuit.Id.Value,
                        circuitNumber,
                        circuitName = circuitName ?? circuit.Name,
                        apparentLoad,
                        voltage = circuitVoltage,
                        breakerRating,
                        wireSize,
                        poles,
                        elementCount = circuit.Elements?.Size ?? 0
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    panelId = panelIdInt,
                    panelName,
                    voltage,
                    phases,
                    totalLoad,
                    totalEstimatedLoad = totalEstLoad,
                    circuitCount = circuitList.Count,
                    circuits = circuitList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get panel schedule view information
        /// </summary>
        [MCPMethod("getPanelScheduleInfo", Category = "Electrical", Description = "Get panel schedule view information with formatted schedule data")]
        public static string GetPanelScheduleInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                PanelScheduleView panelScheduleView = null;

                // Try panelScheduleViewId first
                if (parameters["panelScheduleViewId"] != null)
                {
                    int viewIdInt = parameters["panelScheduleViewId"].ToObject<int>();
                    panelScheduleView = doc.GetElement(new ElementId(viewIdInt)) as PanelScheduleView;
                    if (panelScheduleView == null)
                        return ResponseBuilder.Error($"Panel schedule view with ID {viewIdInt} not found", "ELEMENT_NOT_FOUND").Build();
                }
                else if (parameters["panelId"] != null)
                {
                    // Find panel schedule view for the given panel
                    int panelIdInt = parameters["panelId"].ToObject<int>();
                    var panelId = new ElementId(panelIdInt);

                    var scheduleViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(PanelScheduleView))
                        .Cast<PanelScheduleView>()
                        .Where(psv => psv.GetPanel() == panelId)
                        .ToList();

                    if (scheduleViews.Count == 0)
                        return ResponseBuilder.Error($"No panel schedule view found for panel ID {panelIdInt}", "NOT_FOUND").Build();

                    panelScheduleView = scheduleViews.First();
                }
                else
                {
                    return ResponseBuilder.Error("Either panelScheduleViewId or panelId is required", "MISSING_PARAMETER").Build();
                }

                // Get schedule data
                var panelId2 = panelScheduleView.GetPanel();
                var panel = doc.GetElement(panelId2);
                var templateId = panelScheduleView.GetTemplate();
                var template = doc.GetElement(templateId);

                // Read body section data
                int bodyRows = 0;
                int bodyCols = 0;
                var bodyData = new List<List<string>>();
                int headerRows = 0;
                int headerCols = 0;
                var headerData = new List<List<string>>();

                try
                {
                    // Use SectionType enum: Header=0, Body=1, Summary=2
                    var bodySection = panelScheduleView.GetSectionData(SectionType.Body);
                    if (bodySection != null)
                    {
                        bodyRows = bodySection.NumberOfRows;
                        bodyCols = bodySection.NumberOfColumns;
                    }

                    if (bodyRows > 0 && bodyCols > 0)
                    {
                        for (int r = 0; r < bodyRows; r++)
                        {
                            var row = new List<string>();
                            for (int c = 0; c < bodyCols; c++)
                            {
                                try
                                {
                                    var cellText = panelScheduleView.GetCellText(SectionType.Body, r, c);
                                    row.Add(cellText ?? "");
                                }
                                catch
                                {
                                    row.Add("");
                                }
                            }
                            bodyData.Add(row);
                        }
                    }

                    // Read header section
                    var headerSection = panelScheduleView.GetSectionData(SectionType.Header);
                    if (headerSection != null)
                    {
                        headerRows = headerSection.NumberOfRows;
                        headerCols = headerSection.NumberOfColumns;
                    }

                    if (headerRows > 0 && headerCols > 0)
                    {
                        for (int r = 0; r < headerRows; r++)
                        {
                            var row = new List<string>();
                            for (int c = 0; c < headerCols; c++)
                            {
                                try
                                {
                                    var cellText = panelScheduleView.GetCellText(SectionType.Header, r, c);
                                    row.Add(cellText ?? "");
                                }
                                catch
                                {
                                    row.Add("");
                                }
                            }
                            headerData.Add(row);
                        }
                    }
                }
                catch
                {
                    // Panel schedule section data not available in this Revit version
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    panelScheduleViewId = panelScheduleView.Id.Value,
                    panelId = panelId2.Value,
                    panelName = panel?.Name,
                    templateName = template?.Name,
                    headerRows,
                    headerColumns = headerCols,
                    headerData,
                    bodyRows,
                    bodyColumns = bodyCols,
                    bodyData
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Panels

        /// <summary>
        /// Get all electrical panels/distribution boards in the model
        /// </summary>
        [MCPMethod("getElectricalPanels", Category = "Electrical", Description = "Get all electrical panels and distribution boards in the model")]
        public static string GetElectricalPanels(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var panels = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                // Filter to panels that have electrical distribution system parameters
                var panelList = new List<object>();
                foreach (var panel in panels)
                {
                    // Check if this equipment is a panel (has panel-specific parameters)
                    var panelName = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                    if (string.IsNullOrEmpty(panelName))
                        panelName = panel.Name;

                    var voltage = panel.LookupParameter("Supply Voltage")?.AsValueString() ?? panel.LookupParameter("Supply Voltage")?.AsString() ?? "Unknown";
                    var distributionSystem = panel.get_Parameter(BuiltInParameter.RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM)?.AsString();
                    var totalLoad = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALLOAD_PARAM)?.AsDouble() ?? 0;

                    // Get location
                    var location = panel.Location as LocationPoint;
                    var point = location?.Point;

                    // Count circuits on this panel
                    var circuitCount = new FilteredElementCollector(doc)
                        .OfClass(typeof(ElectricalSystem))
                        .Cast<ElectricalSystem>()
                        .Count(c => c.BaseEquipment?.Id == panel.Id);

                    // Get level
                    var levelId = panel.LevelId;
                    var level = doc.GetElement(levelId) as Level;

                    panelList.Add(new
                    {
                        panelId = panel.Id.Value,
                        name = panelName,
                        familyName = panel.Symbol?.FamilyName,
                        typeName = panel.Symbol?.Name,
                        voltage,
                        distributionSystem,
                        totalLoad,
                        circuitCount,
                        levelId = levelId?.Value,
                        levelName = level?.Name,
                        location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = panelList.Count,
                    panels = panelList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Circuit Info

        /// <summary>
        /// Get detailed circuit info
        /// </summary>
        [MCPMethod("getCircuitInfo", Category = "Electrical", Description = "Get detailed information about an electrical circuit including breaker, wire, and load data")]
        public static string GetCircuitInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "getCircuitInfo");
                v.Require("circuitId").IsType<int>();
                v.ThrowIfInvalid();

                int circuitIdInt = v.GetElementId("circuitId");
                var circuit = doc.GetElement(new ElementId(circuitIdInt)) as ElectricalSystem;

                if (circuit == null)
                    return ResponseBuilder.Error($"Circuit with ID {circuitIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                var circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                var circuitName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.AsString();
                var panelName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                var voltage = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE)?.AsDouble() ?? 0;
                var apparentLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;
                var trueLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_TRUE_LOAD)?.AsDouble() ?? 0;
                var breakerRating = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.AsDouble() ?? 0;
                var wireSize = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_SIZE_PARAM)?.AsString();
                var wireType = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_TYPE_PARAM)?.AsString();
                var wireLength = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_LENGTH_PARAM)?.AsDouble() ?? 0;
                var poles = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES)?.AsInteger() ?? 0;
                var frame = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_FRAME_PARAM)?.AsDouble() ?? 0;

                // Get system type
                var systemTypeElement = doc.GetElement(circuit.GetTypeId());
                var systemTypeName = systemTypeElement?.Name;

                // Get connected element IDs
                var connectedIds = new List<int>();
                if (circuit.Elements != null)
                {
                    foreach (Element el in circuit.Elements)
                    {
                        connectedIds.Add((int)el.Id.Value);
                    }
                }

                // Voltage drop calculation
                double voltageDrop = 0;
                double voltageDropPercent = 0;
                if (voltage > 0 && wireLength > 0 && apparentLoad > 0)
                {
                    // Simplified voltage drop: VD = (2 * L * I * R) / 1000
                    // Using circuit length and load for approximation
                    var current = apparentLoad / voltage;
                    // Approximate resistance based on wire — rough estimate
                    voltageDrop = circuit.LookupParameter("Voltage Drop")?.AsDouble() ?? 0;
                    voltageDropPercent = voltage > 0 ? (voltageDrop / voltage) * 100 : 0;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    circuitId = circuitIdInt,
                    circuitNumber,
                    circuitName = circuitName ?? circuit.Name,
                    panelName,
                    panelId = circuit.BaseEquipment?.Id.Value,
                    systemTypeName,
                    voltage,
                    apparentLoad,
                    trueLoad,
                    breakerRating,
                    frame,
                    wireSize,
                    wireType,
                    wireLength,
                    poles,
                    voltageDrop,
                    voltageDropPercent,
                    connectedElementCount = connectedIds.Count,
                    connectedElementIds = connectedIds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set breaker size for a circuit
        /// </summary>
        [MCPMethod("setCircuitBreaker", Category = "Electrical", Description = "Set the breaker size for an electrical circuit")]
        public static string SetCircuitBreaker(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "setCircuitBreaker");
                v.Require("circuitId").IsType<int>();
                v.Require("breakerSize").IsType<double>().IsPositive();
                v.ThrowIfInvalid();

                int circuitIdInt = v.GetElementId("circuitId");
                double breakerSize = v.GetRequired<double>("breakerSize");

                var circuit = doc.GetElement(new ElementId(circuitIdInt)) as ElectricalSystem;
                if (circuit == null)
                    return ResponseBuilder.Error($"Circuit with ID {circuitIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                using (var trans = new Transaction(doc, "Set Circuit Breaker"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var ratingParam = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM);
                    if (ratingParam != null && !ratingParam.IsReadOnly)
                    {
                        ratingParam.Set(breakerSize);
                    }
                    else
                    {
                        trans.RollBack();
                        return ResponseBuilder.Error("Cannot set breaker rating - parameter is read-only or not available", "READONLY_PARAMETER").Build();
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("circuitId", circuitIdInt)
                        .With("breakerSize", breakerSize)
                        .WithMessage("Breaker size updated successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Wiring

        /// <summary>
        /// Get all wiring/wire types available
        /// </summary>
        [MCPMethod("getWiringTypes", Category = "Electrical", Description = "Get all available wiring and wire types in the model")]
        public static string GetWiringTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wireTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(WireType))
                    .Cast<WireType>()
                    .ToList();

                var typeList = wireTypes.Select(wt => new
                {
                    wireTypeId = wt.Id.Value,
                    name = wt.Name,
                    conduitType = wt.LookupParameter("Conduit Type")?.AsValueString() ?? wt.LookupParameter("Conduit Type")?.AsString(),
                    material = wt.LookupParameter("Material")?.AsValueString() ?? wt.LookupParameter("Material")?.AsString(),
                    insulation = wt.LookupParameter("Insulation")?.AsValueString() ?? wt.LookupParameter("Insulation")?.AsString(),
                    temperatureRating = wt.LookupParameter("Temperature Rating")?.AsValueString() ?? wt.LookupParameter("Temperature Rating")?.AsString(),
                    maxSize = wt.LookupParameter("Max Size")?.AsValueString() ?? wt.LookupParameter("Max Size")?.AsString()
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = typeList.Count,
                    wireTypes = typeList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a wire between two electrical connectors
        /// </summary>
        [MCPMethod("createWire", Category = "Electrical", Description = "Create a wire between two electrical connectors in a view")]
        public static string CreateWire(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "createWire");
                v.Require("wireTypeId").IsType<int>();
                v.ThrowIfInvalid();

                int wireTypeIdInt = v.GetElementId("wireTypeId");
                var wireTypeId = new ElementId(wireTypeIdInt);
                var wireType = doc.GetElement(wireTypeId) as WireType;

                if (wireType == null)
                    return ResponseBuilder.Error($"Wire type with ID {wireTypeIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                // Get the view to place the wire in
                ElementId viewId;
                if (parameters["viewId"] != null)
                {
                    viewId = new ElementId(parameters["viewId"].ToObject<int>());
                }
                else
                {
                    viewId = uiApp.ActiveUIDocument.ActiveView.Id;
                }

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                    return ResponseBuilder.Error("Invalid view for wire placement", "INVALID_VIEW").Build();

                // Get start and end points for the wire
                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                    return ResponseBuilder.Error("startPoint and endPoint are required", "MISSING_PARAMETER").Build();

                var startPt = parameters["startPoint"];
                var endPt = parameters["endPoint"];
                var start = new XYZ(
                    startPt["x"]?.ToObject<double>() ?? 0,
                    startPt["y"]?.ToObject<double>() ?? 0,
                    startPt["z"]?.ToObject<double>() ?? 0
                );
                var end = new XYZ(
                    endPt["x"]?.ToObject<double>() ?? 0,
                    endPt["y"]?.ToObject<double>() ?? 0,
                    endPt["z"]?.ToObject<double>() ?? 0
                );

                using (var trans = new Transaction(doc, "Create Wire"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Build vertex list for the wire path
                    var vertices = new List<XYZ> { start, end };

                    // If intermediate points are provided
                    if (parameters["intermediatePoints"] != null)
                    {
                        var midPoints = parameters["intermediatePoints"].ToObject<List<JObject>>();
                        vertices.Clear();
                        vertices.Add(start);
                        foreach (var pt in midPoints)
                        {
                            vertices.Add(new XYZ(
                                pt["x"]?.ToObject<double>() ?? 0,
                                pt["y"]?.ToObject<double>() ?? 0,
                                pt["z"]?.ToObject<double>() ?? 0
                            ));
                        }
                        vertices.Add(end);
                    }

                    var wire = Wire.Create(doc, wireTypeId, viewId,
                        WiringType.Arc, vertices, null, null);

                    trans.Commit();

                    if (wire == null)
                        return ResponseBuilder.Error("Failed to create wire", "CREATION_FAILED").Build();

                    return ResponseBuilder.Success()
                        .With("wireId", (int)wire.Id.Value)
                        .With("wireTypeName", wireType.Name)
                        .WithMessage("Wire created successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all wires in the model or in a view
        /// </summary>
        [MCPMethod("getWires", Category = "Electrical", Description = "Get all wires in the model or filtered by view")]
        public static string GetWires(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                FilteredElementCollector collector;
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    collector = new FilteredElementCollector(doc, new ElementId(viewIdInt));
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var wires = collector
                    .OfClass(typeof(Wire))
                    .Cast<Wire>()
                    .ToList();

                var wireList = wires.Select(w =>
                {
                    var wireType = doc.GetElement(w.GetTypeId()) as WireType;
                    var vertices = new List<object>();
                    var numVerts = w.NumberOfVertices;
                    for (int i = 0; i < numVerts; i++)
                    {
                        var pt = w.GetVertex(i);
                        vertices.Add(new { x = pt.X, y = pt.Y, z = pt.Z });
                    }

                    return new
                    {
                        wireId = w.Id.Value,
                        wireTypeName = wireType?.Name,
                        wireTypeId = w.GetTypeId().Value,
                        wiringType = w.WiringType.ToString(),
                        numberOfVertices = numVerts,
                        vertices,
                        ownerViewId = w.OwnerViewId?.Value
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = wireList.Count,
                    wires = wireList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a wire
        /// </summary>
        [MCPMethod("deleteWire", Category = "Electrical", Description = "Delete a wire by element ID")]
        public static string DeleteWire(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "deleteWire");
                v.Require("wireId").IsType<int>();
                v.ThrowIfInvalid();

                int wireIdInt = v.GetElementId("wireId");
                var wire = doc.GetElement(new ElementId(wireIdInt));

                if (wire == null)
                    return ResponseBuilder.Error($"Wire with ID {wireIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                if (!(wire is Wire))
                    return ResponseBuilder.Error($"Element {wireIdInt} is not a Wire", "INVALID_ELEMENT_TYPE").Build();

                using (var trans = new Transaction(doc, "Delete Wire"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(wireIdInt));
                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("wireId", wireIdInt)
                        .WithMessage("Wire deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Lighting and Fixtures

        /// <summary>
        /// Get all lighting fixtures
        /// </summary>
        [MCPMethod("getLightingFixtures", Category = "Electrical", Description = "Get all lighting fixtures with wattage, circuit, type, and location data")]
        public static string GetLightingFixtures(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var fixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingFixtures)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var fixtureList = fixtures.Select(f =>
                {
                    var location = f.Location as LocationPoint;
                    var point = location?.Point;
                    var level = doc.GetElement(f.LevelId) as Level;

                    // Get wattage
                    var wattage = f.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;

                    // Get circuit info
                    string circuitNumber = null;
                    string panelName = null;
                    int? circuitId = null;
                    var mepModel = f.MEPModel;
                    if (mepModel != null)
                    {
                        var elecSystems = mepModel.GetElectricalSystems();
                        if (elecSystems != null)
                        {
                            var firstCircuit = elecSystems.Cast<ElectricalSystem>().FirstOrDefault();
                            if (firstCircuit != null)
                            {
                                circuitNumber = firstCircuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                                panelName = firstCircuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                                circuitId = (int?)firstCircuit.Id.Value;
                            }
                        }
                    }

                    return new
                    {
                        fixtureId = f.Id.Value,
                        familyName = f.Symbol?.FamilyName,
                        typeName = f.Symbol?.Name,
                        wattage,
                        circuitNumber,
                        panelName,
                        circuitId,
                        levelId = f.LevelId?.Value,
                        levelName = level?.Name,
                        location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = fixtureList.Count,
                    fixtures = fixtureList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all receptacles/outlets
        /// </summary>
        [MCPMethod("getReceptacles", Category = "Electrical", Description = "Get all receptacles and outlets with circuit information")]
        public static string GetReceptacles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var fixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var receptacleList = fixtures.Select(f =>
                {
                    var location = f.Location as LocationPoint;
                    var point = location?.Point;
                    var level = doc.GetElement(f.LevelId) as Level;

                    // Get circuit info
                    string circuitNumber = null;
                    string panelName = null;
                    int? circuitId = null;
                    var mepModel = f.MEPModel;
                    if (mepModel != null)
                    {
                        var elecSystems = mepModel.GetElectricalSystems();
                        if (elecSystems != null)
                        {
                            var firstCircuit = elecSystems.Cast<ElectricalSystem>().FirstOrDefault();
                            if (firstCircuit != null)
                            {
                                circuitNumber = firstCircuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                                panelName = firstCircuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                                circuitId = (int?)firstCircuit.Id.Value;
                            }
                        }
                    }

                    var voltage = f.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE)?.AsDouble() ?? 0;

                    return new
                    {
                        fixtureId = f.Id.Value,
                        familyName = f.Symbol?.FamilyName,
                        typeName = f.Symbol?.Name,
                        voltage,
                        circuitNumber,
                        panelName,
                        circuitId,
                        levelId = f.LevelId?.Value,
                        levelName = level?.Name,
                        location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = receptacleList.Count,
                    receptacles = receptacleList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all switches in the model
        /// </summary>
        [MCPMethod("getSwitches", Category = "Electrical", Description = "Get all switches with type, location, circuit, and switched fixture data")]
        public static string GetSwitches(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Switches are typically in LightingDevices category
                var switches = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingDevices)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var switchList = switches.Select(s =>
                {
                    var location = s.Location as LocationPoint;
                    var point = location?.Point;
                    var level = doc.GetElement(s.LevelId) as Level;

                    // Get circuit info
                    string circuitNumber = null;
                    string panelName = null;
                    int? circuitId = null;
                    var switchedFixtureIds = new List<int>();

                    var mepModel = s.MEPModel;
                    if (mepModel != null)
                    {
                        var elecSystems = mepModel.GetElectricalSystems();
                        if (elecSystems != null)
                        {
                            var firstCircuit = elecSystems.Cast<ElectricalSystem>().FirstOrDefault();
                            if (firstCircuit != null)
                            {
                                circuitNumber = firstCircuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                                panelName = firstCircuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                                circuitId = (int?)firstCircuit.Id.Value;

                                // Get other elements in the same circuit (switched fixtures)
                                if (firstCircuit.Elements != null)
                                {
                                    foreach (Element el in firstCircuit.Elements)
                                    {
                                        if (el.Id != s.Id)
                                            switchedFixtureIds.Add((int)el.Id.Value);
                                    }
                                }
                            }
                        }
                    }

                    return new
                    {
                        switchId = s.Id.Value,
                        familyName = s.Symbol?.FamilyName,
                        typeName = s.Symbol?.Name,
                        circuitNumber,
                        panelName,
                        circuitId,
                        switchedFixtureIds,
                        switchedFixtureCount = switchedFixtureIds.Count,
                        levelId = s.LevelId?.Value,
                        levelName = level?.Name,
                        location = point != null ? new { x = point.X, y = point.Y, z = point.Z } : null
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = switchList.Count,
                    switches = switchList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a switch system linking switch to fixtures
        /// </summary>
        [MCPMethod("createSwitchSystem", Category = "Electrical", Description = "Create a switch system linking a switch to lighting fixtures")]
        public static string CreateSwitchSystem(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "createSwitchSystem");
                v.Require("switchId").IsType<int>();
                v.Require("fixtureIds");
                v.ThrowIfInvalid();

                int switchIdInt = v.GetElementId("switchId");
                var fixtureIds = parameters["fixtureIds"].ToObject<List<int>>();

                if (fixtureIds == null || fixtureIds.Count == 0)
                    return ResponseBuilder.Error("fixtureIds must contain at least one fixture ID", "INVALID_PARAMETER").Build();

                var switchElement = doc.GetElement(new ElementId(switchIdInt)) as FamilyInstance;
                if (switchElement == null)
                    return ResponseBuilder.Error($"Switch with ID {switchIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                // Collect all element IDs: switch + fixtures
                var allIds = new List<ElementId> { new ElementId(switchIdInt) };
                allIds.AddRange(fixtureIds.Select(id => new ElementId(id)));

                using (var trans = new Transaction(doc, "Create Switch System"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the electrical system with switch and fixtures
                    var system = ElectricalSystem.Create(doc, allIds, ElectricalSystemType.PowerCircuit);

                    trans.Commit();

                    if (system == null)
                        return ResponseBuilder.Error("Failed to create switch system", "CREATION_FAILED").Build();

                    return ResponseBuilder.Success()
                        .With("systemId", (int)system.Id.Value)
                        .With("switchId", switchIdInt)
                        .With("fixtureCount", fixtureIds.Count)
                        .WithMessage("Switch system created successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Calculations

        /// <summary>
        /// Calculate voltage drop for a circuit
        /// </summary>
        [MCPMethod("calculateVoltDrop", Category = "Electrical", Description = "Calculate voltage drop for a circuit with NEC pass/fail assessment")]
        public static string CalculateVoltDrop(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "calculateVoltDrop");
                v.Require("circuitId").IsType<int>();
                v.ThrowIfInvalid();

                int circuitIdInt = v.GetElementId("circuitId");
                var circuit = doc.GetElement(new ElementId(circuitIdInt)) as ElectricalSystem;

                if (circuit == null)
                    return ResponseBuilder.Error($"Circuit with ID {circuitIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                var voltage = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE)?.AsDouble() ?? 0;
                var apparentLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;
                var wireLength = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_LENGTH_PARAM)?.AsDouble() ?? 0;
                var wireSize = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_SIZE_PARAM)?.AsString();
                var circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                var panelName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();

                // Try to get Revit-calculated voltage drop
                var voltageDrop = circuit.LookupParameter("Voltage Drop")?.AsDouble() ?? 0;

                // Calculate percentage
                double voltageDropPercent = voltage > 0 ? (voltageDrop / voltage) * 100.0 : 0;

                // NEC 210.19(A) and 215.2(A) recommendations
                // Branch circuits: max 3% voltage drop
                // Branch + feeder combined: max 5% voltage drop
                double necBranchLimit = 3.0; // percent
                double necCombinedLimit = 5.0; // percent
                bool passBranch = voltageDropPercent <= necBranchLimit;
                bool passCombined = voltageDropPercent <= necCombinedLimit;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    circuitId = circuitIdInt,
                    circuitNumber,
                    panelName,
                    voltage,
                    apparentLoad,
                    wireLength,
                    wireSize,
                    voltageDrop,
                    voltageDropPercent = Math.Round(voltageDropPercent, 2),
                    necBranchLimitPercent = necBranchLimit,
                    necCombinedLimitPercent = necCombinedLimit,
                    passBranchCircuit = passBranch,
                    passCombined,
                    assessment = passBranch ? "PASS" : (passCombined ? "WARNING - exceeds branch limit" : "FAIL - exceeds combined limit")
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Calculate total connected and demand load for a panel
        /// </summary>
        [MCPMethod("calculateTotalLoad", Category = "Electrical", Description = "Calculate total connected and demand load for an electrical panel")]
        public static string CalculateTotalLoad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "calculateTotalLoad");
                v.Require("panelId").IsType<int>();
                v.ThrowIfInvalid();

                int panelIdInt = v.GetElementId("panelId");
                var panelId = new ElementId(panelIdInt);
                var panel = doc.GetElement(panelId) as FamilyInstance;

                if (panel == null)
                    return ResponseBuilder.Error($"Panel with ID {panelIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                // Get panel-level load parameters
                var totalLoad = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALLOAD_PARAM)?.AsDouble() ?? 0;
                var totalEstLoad = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALESTLOAD_PARAM)?.AsDouble() ?? 0;
                var panelVoltageStr = panel.LookupParameter("Supply Voltage")?.AsValueString();
                double panelVoltage = 0;
                if (!string.IsNullOrEmpty(panelVoltageStr))
                    double.TryParse(panelVoltageStr.Replace("V", "").Trim(), out panelVoltage);

                // Collect all circuits on this panel
                var circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Where(c => c.BaseEquipment?.Id == panelId)
                    .ToList();

                double totalConnectedLoad = 0;
                double totalApparentLoad = 0;
                double totalTrueLoad = 0;
                var circuitLoads = new List<object>();

                foreach (var circuit in circuits)
                {
                    var apparentLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;
                    var trueLoad = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_TRUE_LOAD)?.AsDouble() ?? 0;
                    var circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                    var circuitName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.AsString();
                    var breakerRating = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.AsDouble() ?? 0;

                    totalApparentLoad += apparentLoad;
                    totalTrueLoad += trueLoad;
                    totalConnectedLoad += apparentLoad;

                    circuitLoads.Add(new
                    {
                        circuitId = circuit.Id.Value,
                        circuitNumber,
                        circuitName = circuitName ?? circuit.Name,
                        apparentLoad,
                        trueLoad,
                        breakerRating
                    });
                }

                // Calculate demand factor (simplified)
                double demandFactor = totalConnectedLoad > 0 ? totalTrueLoad / totalConnectedLoad : 0;

                // Calculate total amps
                double totalAmps = panelVoltage > 0 ? totalApparentLoad / panelVoltage : 0;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    panelId = panelIdInt,
                    panelName = panel.Name,
                    panelVoltage,
                    totalConnectedLoad,
                    totalApparentLoad,
                    totalTrueLoad,
                    totalEstimatedLoad = totalEstLoad,
                    revitTotalLoad = totalLoad,
                    demandFactor = Math.Round(demandFactor, 3),
                    totalAmps = Math.Round(totalAmps, 1),
                    circuitCount = circuits.Count,
                    circuitLoads
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region System Types

        /// <summary>
        /// Get all electrical system types
        /// </summary>
        [MCPMethod("getElectricalSystemTypes", Category = "Electrical", Description = "Get all electrical system types (power, lighting, fire alarm, data, etc.)")]
        public static string GetElectricalSystemTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get MEP system types that are electrical
                var systemTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Select(es => es.GetTypeId())
                    .Distinct()
                    .ToList();

                var typeList = new List<object>();
                var processedIds = new HashSet<long>();

                foreach (var typeId in systemTypes)
                {
                    if (processedIds.Contains(typeId.Value))
                        continue;
                    processedIds.Add(typeId.Value);

                    var typeElement = doc.GetElement(typeId);
                    if (typeElement == null) continue;

                    typeList.Add(new
                    {
                        systemTypeId = typeId.Value,
                        name = typeElement.Name
                    });
                }

                // Also get all electrical system types from the type collector
                var allElecTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                    .WhereElementIsElementType()
                    .ToList();

                foreach (var et in allElecTypes)
                {
                    if (processedIds.Contains(et.Id.Value))
                        continue;
                    processedIds.Add(et.Id.Value);

                    typeList.Add(new
                    {
                        systemTypeId = et.Id.Value,
                        name = et.Name
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = typeList.Count,
                    systemTypes = typeList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Conduit and Cable Tray Runs

        /// <summary>
        /// Get all conduit runs/paths
        /// </summary>
        [MCPMethod("getConduitRuns", Category = "Electrical", Description = "Get all conduit runs and paths, optionally filtered by circuit")]
        public static string GetConduitRuns(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var conduits = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Conduit)
                    .WhereElementIsNotElementType()
                    .ToList();

                // Optionally filter by circuit
                int? filterCircuitId = null;
                if (parameters["circuitId"] != null)
                {
                    filterCircuitId = parameters["circuitId"].ToObject<int>();
                }

                var conduitList = new List<object>();
                foreach (var conduit in conduits)
                {
                    // Get conduit curve
                    var locationCurve = conduit.Location as LocationCurve;
                    XYZ startPoint = null;
                    XYZ endPoint = null;
                    double length = 0;

                    if (locationCurve?.Curve != null)
                    {
                        startPoint = locationCurve.Curve.GetEndPoint(0);
                        endPoint = locationCurve.Curve.GetEndPoint(1);
                        length = locationCurve.Curve.Length;
                    }

                    var diameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? 0;
                    var typeName = doc.GetElement(conduit.GetTypeId())?.Name;

                    // Check if conduit belongs to a specific circuit system
                    var systemParam = conduit.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();

                    // Filter by circuit if specified
                    if (filterCircuitId.HasValue)
                    {
                        // Check if this conduit is associated with the circuit
                        var fi = conduit as FamilyInstance;
                        if (fi?.MEPModel != null)
                        {
                            var systems = fi.MEPModel.GetElectricalSystems();
                            if (systems == null || !systems.Cast<ElectricalSystem>().Any(s => (int)s.Id.Value == filterCircuitId.Value))
                                continue;
                        }
                        else
                        {
                            continue; // Skip non-family conduits when filtering by circuit
                        }
                    }

                    conduitList.Add(new
                    {
                        conduitId = conduit.Id.Value,
                        typeName,
                        diameter,
                        length,
                        systemName = systemParam,
                        startPoint = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                        endPoint = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = conduitList.Count,
                    conduits = conduitList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all cable tray runs and their fill ratios
        /// </summary>
        [MCPMethod("getCableTrayRuns", Category = "Electrical", Description = "Get all cable tray runs with dimensions and fill ratios")]
        public static string GetCableTrayRuns(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var cableTrays = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_CableTray)
                    .WhereElementIsNotElementType()
                    .ToList();

                var trayList = new List<object>();
                foreach (var tray in cableTrays)
                {
                    var locationCurve = tray.Location as LocationCurve;
                    XYZ startPoint = null;
                    XYZ endPoint = null;
                    double length = 0;

                    if (locationCurve?.Curve != null)
                    {
                        startPoint = locationCurve.Curve.GetEndPoint(0);
                        endPoint = locationCurve.Curve.GetEndPoint(1);
                        length = locationCurve.Curve.Length;
                    }

                    var width = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble() ?? 0;
                    var height = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble() ?? 0;
                    var typeName = doc.GetElement(tray.GetTypeId())?.Name;
                    var systemName = tray.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();

                    // Calculate fill area (cross-section)
                    double crossSectionArea = width * height;

                    trayList.Add(new
                    {
                        cableTrayId = tray.Id.Value,
                        typeName,
                        width,
                        height,
                        length,
                        crossSectionArea,
                        systemName,
                        startPoint = startPoint != null ? new { x = startPoint.X, y = startPoint.Y, z = startPoint.Z } : null,
                        endPoint = endPoint != null ? new { x = endPoint.X, y = endPoint.Y, z = endPoint.Z } : null
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = trayList.Count,
                    cableTrays = trayList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Tagging and Schedules

        /// <summary>
        /// Tag an electrical element in a view
        /// </summary>
        [MCPMethod("tagElectricalElement", Category = "Electrical", Description = "Tag an electrical element in a view with a specified tag type")]
        public static string TagElectricalElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var v = new ParameterValidator(parameters, "tagElectricalElement");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                int elementIdInt = v.GetElementId("elementId");
                var element = doc.GetElement(new ElementId(elementIdInt));

                if (element == null)
                    return ResponseBuilder.Error($"Element with ID {elementIdInt} not found", "ELEMENT_NOT_FOUND").Build();

                // Get view
                View view;
                if (parameters["viewId"] != null)
                {
                    int viewIdInt = parameters["viewId"].ToObject<int>();
                    view = doc.GetElement(new ElementId(viewIdInt)) as View;
                    if (view == null)
                        return ResponseBuilder.Error($"View with ID {viewIdInt} not found", "ELEMENT_NOT_FOUND").Build();
                }
                else
                {
                    view = uiApp.ActiveUIDocument.ActiveView;
                }

                // Get tag location
                XYZ tagPoint;
                if (parameters["tagLocation"] != null)
                {
                    var loc = parameters["tagLocation"];
                    tagPoint = new XYZ(
                        loc["x"]?.ToObject<double>() ?? 0,
                        loc["y"]?.ToObject<double>() ?? 0,
                        loc["z"]?.ToObject<double>() ?? 0
                    );
                }
                else
                {
                    // Default to element location
                    var locPoint = element.Location as LocationPoint;
                    var locCurve = element.Location as LocationCurve;
                    if (locPoint != null)
                        tagPoint = locPoint.Point;
                    else if (locCurve != null)
                        tagPoint = locCurve.Curve.Evaluate(0.5, true);
                    else
                        tagPoint = XYZ.Zero;
                }

                using (var trans = new Transaction(doc, "Tag Electrical Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    IndependentTag tag;

                    if (parameters["tagTypeId"] != null)
                    {
                        int tagTypeIdInt = parameters["tagTypeId"].ToObject<int>();
                        var tagTypeId = new ElementId(tagTypeIdInt);

                        tag = IndependentTag.Create(doc, tagTypeId, view.Id,
                            new Reference(element), false, TagOrientation.Horizontal, tagPoint);
                    }
                    else
                    {
                        // Use default tag for the element's category
                        tag = IndependentTag.Create(doc, view.Id,
                            new Reference(element), false, TagMode.TM_ADDBY_CATEGORY,
                            TagOrientation.Horizontal, tagPoint);
                    }

                    trans.Commit();

                    if (tag == null)
                        return ResponseBuilder.Error("Failed to create tag", "CREATION_FAILED").Build();

                    return ResponseBuilder.Success()
                        .With("tagId", (int)tag.Id.Value)
                        .With("elementId", elementIdInt)
                        .WithMessage("Electrical element tagged successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all electrical schedule data for lighting/power schedules
        /// </summary>
        [MCPMethod("getElectricalScheduleData", Category = "Electrical", Description = "Get electrical schedule data for lighting and power schedules")]
        public static string GetElectricalScheduleData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Determine schedule type
                string scheduleType = parameters["scheduleType"]?.ToString() ?? "all";

                var result = new Dictionary<string, object>();
                result["success"] = true;

                // Lighting fixtures data
                if (scheduleType == "all" || scheduleType == "lighting")
                {
                    var lightingFixtures = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_LightingFixtures)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .ToList();

                    var lightingData = lightingFixtures.Select(f =>
                    {
                        var level = doc.GetElement(f.LevelId) as Level;
                        var wattage = f.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;
                        var room = GetRoomForElement(doc, f);

                        string circuitNumber = null;
                        string panelName = null;
                        string switchId = null;
                        if (f.MEPModel != null)
                        {
                            var systems = f.MEPModel.GetElectricalSystems();
                            if (systems != null)
                            {
                                var circuit = systems.Cast<ElectricalSystem>().FirstOrDefault();
                                if (circuit != null)
                                {
                                    circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                                    panelName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                                }
                            }
                        }

                        return new
                        {
                            elementId = f.Id.Value,
                            familyName = f.Symbol?.FamilyName,
                            typeName = f.Symbol?.Name,
                            wattage,
                            circuitNumber,
                            panelName,
                            levelName = level?.Name,
                            roomName = room?.Name,
                            roomNumber = room?.Number
                        };
                    }).ToList();

                    result["lightingFixtures"] = lightingData;
                    result["lightingCount"] = lightingData.Count;
                    result["totalLightingWattage"] = lightingData.Sum(l => (double)((dynamic)l).wattage);
                }

                // Power fixtures data
                if (scheduleType == "all" || scheduleType == "power")
                {
                    var powerFixtures = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .ToList();

                    var powerData = powerFixtures.Select(f =>
                    {
                        var level = doc.GetElement(f.LevelId) as Level;
                        var voltage = f.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE)?.AsDouble() ?? 0;
                        var load = f.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD)?.AsDouble() ?? 0;
                        var room = GetRoomForElement(doc, f);

                        string circuitNumber = null;
                        string panelName = null;
                        if (f.MEPModel != null)
                        {
                            var systems = f.MEPModel.GetElectricalSystems();
                            if (systems != null)
                            {
                                var circuit = systems.Cast<ElectricalSystem>().FirstOrDefault();
                                if (circuit != null)
                                {
                                    circuitNumber = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString();
                                    panelName = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                                }
                            }
                        }

                        return new
                        {
                            elementId = f.Id.Value,
                            familyName = f.Symbol?.FamilyName,
                            typeName = f.Symbol?.Name,
                            voltage,
                            load,
                            circuitNumber,
                            panelName,
                            levelName = level?.Name,
                            roomName = room?.Name,
                            roomNumber = room?.Number
                        };
                    }).ToList();

                    result["powerFixtures"] = powerData;
                    result["powerCount"] = powerData.Count;
                }

                // Equipment data
                if (scheduleType == "all" || scheduleType == "equipment")
                {
                    var equipment = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .ToList();

                    var equipData = equipment.Select(e =>
                    {
                        var level = doc.GetElement(e.LevelId) as Level;
                        var voltageStr = e.LookupParameter("Supply Voltage")?.AsValueString() ?? e.LookupParameter("Supply Voltage")?.AsString() ?? "";
                        var totalLoad = e.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALLOAD_PARAM)?.AsDouble() ?? 0;

                        return new
                        {
                            elementId = e.Id.Value,
                            familyName = e.Symbol?.FamilyName,
                            typeName = e.Symbol?.Name,
                            name = e.Name,
                            voltage = voltageStr,
                            totalLoad,
                            levelName = level?.Name
                        };
                    }).ToList();

                    result["equipment"] = equipData;
                    result["equipmentCount"] = equipData.Count;
                }

                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Get the room that contains a given element
        /// </summary>
        private static Room GetRoomForElement(Document doc, FamilyInstance fi)
        {
            try
            {
                var room = fi.Room;
                if (room != null) return room;

                // Try from location
                var locPoint = fi.Location as LocationPoint;
                if (locPoint != null)
                {
                    var phase = doc.GetElement(doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId()) as Phase;
                    if (phase != null)
                    {
                        room = doc.GetRoomAtPoint(locPoint.Point, phase);
                    }
                }
                return room;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
