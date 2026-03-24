using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Services;

namespace RevitMCPBridge
{
    /// <summary>
    /// MCP-exposed methods for FBC wind load compliance validation.
    /// Phase 1: validate and report only — no auto-fix.
    /// All pressures are in psf (pounds per square foot).
    /// </summary>
    public static class FBCWindLoadMethods
    {
        #region validateWindLoads

        /// <summary>
        /// Validate wind load compliance for walls and roof elements.
        /// Calculates design wind pressure at each element's height and compares against rated pressure.
        /// </summary>
        [MCPMethod("validateWindLoads", Category = "FBCCompliance",
            Description = "Validate FBC wind load compliance for exterior walls and roof elements")]
        public static string ValidateWindLoads(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var engine = FBCComplianceEngine.Instance;
                if (!engine.IsLoaded)
                {
                    return ResponseBuilder.Error(
                        $"FBC compliance engine not loaded: {engine.LoadError}", "ENGINE_NOT_LOADED").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter extraction
                var county = parameters["county"]?.ToString();
                if (string.IsNullOrWhiteSpace(county))
                {
                    return ResponseBuilder.Error("'county' parameter is required.", "MISSING_PARAMETER").Build();
                }

                var exposureCategory = parameters["exposureCategory"]?.ToString() ?? "C";
                var riskCategory = parameters["riskCategory"]?.ToString() ?? "II";

                int windSpeed = engine.GetWindSpeed(county, riskCategory);
                if (windSpeed == 0)
                {
                    return ResponseBuilder.Error(
                        $"County '{county}' not found in FBC wind tables. Use getWindZoneMap to list available counties.",
                        "COUNTY_NOT_FOUND").Build();
                }

                // Collect target elements
                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                var elements = new List<Element>();

                if (elementIds != null && elementIds.Length > 0)
                {
                    foreach (var id in elementIds)
                    {
                        var elem = doc.GetElement(new ElementId(id));
                        if (elem != null)
                            elements.Add(elem);
                    }
                }
                else
                {
                    // Collect all exterior walls
                    var walls = new FilteredElementCollector(doc)
                        .OfClass(typeof(Wall))
                        .WhereElementIsNotElementType()
                        .Cast<Wall>()
                        .Where(w => w.WallType.Function == WallFunction.Exterior)
                        .Cast<Element>()
                        .ToList();
                    elements.AddRange(walls);

                    // Collect all roof elements
                    var roofs = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Roofs)
                        .WhereElementIsNotElementType()
                        .Cast<Element>()
                        .ToList();
                    elements.AddRange(roofs);
                }

                // Evaluate each element
                var results = new List<object>();
                int passed = 0, failed = 0, unknown = 0;

                foreach (var elem in elements)
                {
                    var result = EvaluateElementWindLoad(doc, engine, elem, windSpeed, exposureCategory);
                    results.Add(new
                    {
                        elementId = result.ElementId,
                        elementType = result.ElementType,
                        elementName = result.ElementName,
                        heightFt = Math.Round(result.HeightFt, 2),
                        kz = Math.Round(result.Kz, 4),
                        qz_psf = Math.Round(result.Qz_psf, 2),
                        designPressure_psf = Math.Round(result.DesignPressure_psf, 2),
                        elementRatedPressure_psf = Math.Round(result.ElementRatedPressure_psf, 2),
                        hasRatedPressure = result.HasRatedPressure,
                        status = result.Status,
                        notes = result.Notes
                    });

                    switch (result.Status)
                    {
                        case "PASS": passed++; break;
                        case "FAIL": failed++; break;
                        default: unknown++; break;
                    }
                }

                return ResponseBuilder.Success()
                    .With("county", county)
                    .With("windSpeed_mph", windSpeed)
                    .With("exposureCategory", exposureCategory)
                    .With("riskCategory", riskCategory)
                    .With("hvhz", engine.IsHVHZ(county))
                    .With("summary", new { total = results.Count, passed, failed, unknown })
                    .With("elements", results)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FBCWindLoadMethods.ValidateWindLoads failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region checkMissingComplianceParameters

        /// <summary>
        /// Scan walls, roofs, windows, and doors for missing compliance-related parameters.
        /// </summary>
        [MCPMethod("checkMissingComplianceParameters", Category = "FBCCompliance",
            Description = "Check for missing wind compliance parameters on walls, roofs, windows, doors")]
        public static string CheckMissingComplianceParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                var elements = new List<Element>();

                if (elementIds != null && elementIds.Length > 0)
                {
                    foreach (var id in elementIds)
                    {
                        var elem = doc.GetElement(new ElementId(id));
                        if (elem != null)
                            elements.Add(elem);
                    }
                }
                else
                {
                    // Walls
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfClass(typeof(Wall))
                            .WhereElementIsNotElementType()
                            .Cast<Wall>()
                            .Where(w => w.WallType.Function == WallFunction.Exterior)
                            .Cast<Element>());

                    // Roofs
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Roofs)
                            .WhereElementIsNotElementType());

                    // Windows
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Windows)
                            .WhereElementIsNotElementType());

                    // Doors
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Doors)
                            .WhereElementIsNotElementType());
                }

                var results = new List<object>();

                foreach (var elem in elements)
                {
                    var missing = new List<string>();

                    foreach (var paramName in FBCComplianceEngine.ComplianceParameterNames)
                    {
                        var param = elem.LookupParameter(paramName);
                        if (param == null || !param.HasValue || IsParameterEmpty(param))
                        {
                            missing.Add(paramName);
                        }
                    }

                    // Also check the element's type parameters
                    var elemType = doc.GetElement(elem.GetTypeId());
                    if (elemType != null)
                    {
                        foreach (var paramName in FBCComplianceEngine.CriticalParameterNames)
                        {
                            // Skip if already found on instance
                            if (!missing.Contains(paramName))
                                continue;

                            var typeParam = elemType.LookupParameter(paramName);
                            if (typeParam != null && typeParam.HasValue && !IsParameterEmpty(typeParam))
                            {
                                missing.Remove(paramName);
                            }
                        }
                    }

                    if (missing.Count > 0)
                    {
                        results.Add(new
                        {
                            elementId = elem.Id.Value,
                            elementType = elem.GetType().Name,
                            elementName = elem.Name ?? "(unnamed)",
                            category = elem.Category?.Name ?? "Unknown",
                            missingParameters = missing,
                            missingCritical = missing.Intersect(FBCComplianceEngine.CriticalParameterNames).ToList()
                        });
                    }
                }

                int totalChecked = elements.Count;
                int withMissing = results.Count;
                int fullyCompliant = totalChecked - withMissing;

                return ResponseBuilder.Success()
                    .With("totalElementsChecked", totalChecked)
                    .With("elementsWithMissingParams", withMissing)
                    .With("fullyCompliantElements", fullyCompliant)
                    .With("elements", results)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FBCWindLoadMethods.CheckMissingComplianceParameters failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region getComplianceSummary

        /// <summary>
        /// Get overall model wind compliance summary including building height,
        /// design pressures, and element pass/fail counts.
        /// </summary>
        [MCPMethod("getComplianceSummary", "getWindComplianceSummary", Category = "FBCCompliance",
            Description = "Get overall FBC wind compliance summary for the model")]
        public static string GetComplianceSummary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var engine = FBCComplianceEngine.Instance;
                if (!engine.IsLoaded)
                {
                    return ResponseBuilder.Error(
                        $"FBC compliance engine not loaded: {engine.LoadError}", "ENGINE_NOT_LOADED").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var county = parameters["county"]?.ToString();
                if (string.IsNullOrWhiteSpace(county))
                {
                    return ResponseBuilder.Error("'county' parameter is required.", "MISSING_PARAMETER").Build();
                }

                var exposureCategory = parameters["exposureCategory"]?.ToString() ?? "C";
                var riskCategory = parameters["riskCategory"]?.ToString() ?? "II";
                var scope = parameters["scope"]?.ToString() ?? "wind";

                int windSpeed = engine.GetWindSpeed(county, riskCategory);
                if (windSpeed == 0)
                {
                    return ResponseBuilder.Error(
                        $"County '{county}' not found in FBC wind tables.", "COUNTY_NOT_FOUND").Build();
                }

                // Determine building height from levels
                double buildingHeightFt = GetBuildingHeight(doc);

                // Calculate qh at mean roof height
                double kzAtRoof = engine.GetKz(buildingHeightFt, exposureCategory);
                double qh = engine.CalculateQz(buildingHeightFt, windSpeed, exposureCategory);

                // Get MWFRS pressures
                var mwfrsPressures = engine.GetAllMWFRS_Pressures(qh);
                var roundedPressures = new Dictionary<string, double>();
                foreach (var kvp in mwfrsPressures)
                {
                    roundedPressures[kvp.Key] = Math.Round(kvp.Value, 2);
                }

                // Evaluate all exterior walls and roofs
                var walls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(w => w.WallType.Function == WallFunction.Exterior)
                    .Cast<Element>()
                    .ToList();

                var roofs = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Roofs)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .ToList();

                var allElements = walls.Concat(roofs).ToList();
                int passed = 0, failedCount = 0, unknownCount = 0;

                foreach (var elem in allElements)
                {
                    var result = EvaluateElementWindLoad(doc, engine, elem, windSpeed, exposureCategory);
                    switch (result.Status)
                    {
                        case "PASS": passed++; break;
                        case "FAIL": failedCount++; break;
                        default: unknownCount++; break;
                    }
                }

                bool isHVHZ = engine.IsHVHZ(county);
                bool noaRequired = isHVHZ;

                var notes = new List<string>();
                if (isHVHZ)
                    notes.Add("HVHZ jurisdiction: Miami-Dade NOA (Notice of Acceptance) required for all exterior products.");
                if (windSpeed >= 170)
                    notes.Add($"Wind speed {windSpeed} mph: impact-rated glazing required per FBC 1626.2.");
                if (unknownCount > 0)
                    notes.Add($"{unknownCount} element(s) lack rated pressure data and could not be evaluated.");

                return ResponseBuilder.Success()
                    .With("county", county)
                    .With("exposureCategory", exposureCategory)
                    .With("riskCategory", riskCategory)
                    .With("windSpeed_mph", windSpeed)
                    .With("hvhz", isHVHZ)
                    .With("noaRequired", noaRequired)
                    .With("buildingHeightFt", Math.Round(buildingHeightFt, 2))
                    .With("kz_atRoofHeight", Math.Round(kzAtRoof, 4))
                    .With("qh_psf", Math.Round(qh, 2))
                    .With("mwfrs_pressures_psf", roundedPressures)
                    .With("elementsChecked", allElements.Count)
                    .With("elementsPassed", passed)
                    .With("elementsFailed", failedCount)
                    .With("elementsUnknown", unknownCount)
                    .With("wallCount", walls.Count)
                    .With("roofCount", roofs.Count)
                    .With("notes", notes)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FBCWindLoadMethods.GetComplianceSummary failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region getWindZoneMap

        /// <summary>
        /// Get wind speed, HVHZ status, and exposure info for a Florida county.
        /// Optionally calculate Kz and qz at a specific building height.
        /// </summary>
        [MCPMethod("getWindZoneMap", Category = "FBCCompliance",
            Description = "Get wind zone data for a Florida county, optionally with height-specific calculations")]
        public static string GetWindZoneMap(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var engine = FBCComplianceEngine.Instance;
                if (!engine.IsLoaded)
                {
                    return ResponseBuilder.Error(
                        $"FBC compliance engine not loaded: {engine.LoadError}", "ENGINE_NOT_LOADED").Build();
                }

                var county = parameters["county"]?.ToString();
                if (string.IsNullOrWhiteSpace(county))
                {
                    // Return list of all available counties
                    var counties = engine.GetCountyNames().OrderBy(c => c).ToList();
                    return ResponseBuilder.Success()
                        .With("availableCounties", counties)
                        .WithCount(counties.Count)
                        .WithMessage("No county specified. Provide 'county' parameter to get wind zone data.")
                        .Build();
                }

                var countyData = engine.GetCountyData(county);
                if (countyData == null)
                {
                    var available = engine.GetCountyNames().OrderBy(c => c).ToList();
                    return ResponseBuilder.Error(
                        $"County '{county}' not found. Available counties: {string.Join(", ", available.Take(10))}...",
                        "COUNTY_NOT_FOUND").Build();
                }

                var exposureCategory = parameters["exposureCategory"]?.ToString() ?? "C";
                var buildingHeight = parameters["buildingHeight"]?.Value<double?>();

                var result = new Dictionary<string, object>
                {
                    ["county"] = countyData.County,
                    ["windSpeed_mph_RCII"] = countyData.Vult_RCII,
                    ["hvhz"] = countyData.HVHZ,
                    ["exposureCategory"] = exposureCategory,
                    ["speedsByRiskCategory"] = countyData.SpeedsByRC
                };

                var notes = new List<string>();

                if (countyData.HVHZ)
                {
                    notes.Add("High Velocity Hurricane Zone: Miami-Dade NOA required for exterior products.");
                    notes.Add("Large missile impact test (9 lb 2x4 at 50 fps) required for glazing below 30 ft.");
                }

                if (countyData.Vult_RCII >= 170)
                {
                    notes.Add("Wind-borne debris region: impact-rated glazing or shutters required.");
                }

                if (buildingHeight.HasValue && buildingHeight.Value > 0)
                {
                    double h = buildingHeight.Value;
                    double kz = engine.GetKz(h, exposureCategory);
                    double qz = engine.CalculateQz(h, countyData.Vult_RCII, exposureCategory);

                    result["buildingHeightFt"] = Math.Round(h, 2);
                    result["kz"] = Math.Round(kz, 4);
                    result["qz_psf"] = Math.Round(qz, 2);

                    notes.Add($"Kz={kz:F4} interpolated for {h:F1} ft, Exposure {exposureCategory}.");
                    notes.Add($"qz={qz:F2} psf (velocity pressure at {h:F1} ft, V={countyData.Vult_RCII} mph).");
                }

                result["notes"] = notes;

                return ResponseBuilder.Success()
                    .With("windZone", result)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FBCWindLoadMethods.GetWindZoneMap failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Evaluate a single element for wind load compliance.
        /// </summary>
        private static FBCComplianceEngine.WindLoadResult EvaluateElementWindLoad(
            Document doc, FBCComplianceEngine engine, Element elem, int windSpeed, string exposureCategory)
        {
            var result = new FBCComplianceEngine.WindLoadResult
            {
                ElementId = (int)elem.Id.Value,
                ElementType = elem.GetType().Name,
                ElementName = elem.Name ?? "(unnamed)"
            };

            // Determine element height
            result.HeightFt = GetElementHeight(doc, elem);

            // Calculate Kz and qz
            result.Kz = engine.GetKz(result.HeightFt, exposureCategory);
            result.Qz_psf = engine.CalculateQz(result.HeightFt, windSpeed, exposureCategory);

            // Determine if this is a wall or roof for pressure calculation
            bool isRoof = elem.Category != null &&
                          elem.Category.Id.Value == (int)BuiltInCategory.OST_Roofs;

            if (isRoof)
            {
                // For roofs, use C&C negative (suction) as the critical case
                var (_, negative) = engine.CalculateCC_Pressure(
                    result.Qz_psf, "zone_1_field", "enclosed", false);
                result.DesignPressure_psf = Math.Abs(negative);
            }
            else
            {
                // For walls, use C&C — take the larger absolute value
                var (positive, negative) = engine.CalculateCC_Pressure(
                    result.Qz_psf, "zone_4_field", "enclosed", true);
                result.DesignPressure_psf = Math.Max(Math.Abs(positive), Math.Abs(negative));
            }

            // Try to read the element's rated design pressure
            result.ElementRatedPressure_psf = GetElementRatedPressure(doc, elem);
            result.HasRatedPressure = result.ElementRatedPressure_psf > 0;

            // Evaluate
            result.Status = engine.EvaluateCompliance(result.DesignPressure_psf, result.ElementRatedPressure_psf);

            // Build notes
            var notes = new List<string>();
            if (!result.HasRatedPressure)
                notes.Add("No rated pressure parameter found on element or type.");
            if (result.Status == "FAIL")
                notes.Add($"Element rated {result.ElementRatedPressure_psf:F1} psf < required {result.DesignPressure_psf:F1} psf.");

            result.Notes = notes.Count > 0 ? string.Join(" ", notes) : null;

            return result;
        }

        /// <summary>
        /// Get the height of an element above ground level in feet.
        /// Uses the element's bounding box midpoint or associated level elevation.
        /// </summary>
        private static double GetElementHeight(Document doc, Element elem)
        {
            // Try bounding box midpoint first
            var bb = elem.get_BoundingBox(null);
            if (bb != null)
            {
                // Midpoint Z in Revit internal units (feet)
                double midZ = (bb.Min.Z + bb.Max.Z) / 2.0;
                return midZ;
            }

            // Fall back to level elevation
            var levelParam = elem.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                          ?? elem.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)
                          ?? elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);

            if (levelParam != null)
            {
                var levelId = levelParam.AsElementId();
                var level = doc.GetElement(levelId) as Level;
                if (level != null)
                    return level.Elevation;
            }

            // Default: ground level
            return 0;
        }

        /// <summary>
        /// Attempt to read a design/rated pressure value from the element or its type.
        /// Checks multiple common parameter names.
        /// Returns 0 if no pressure parameter found.
        /// </summary>
        private static double GetElementRatedPressure(Document doc, Element elem)
        {
            string[] pressureParamNames = new[]
            {
                "Design Pressure",
                "Design Wind Pressure",
                "Rated Pressure",
                "Wind Pressure Rating",
                "DP Rating"
            };

            // Check instance parameters
            foreach (var name in pressureParamNames)
            {
                var param = elem.LookupParameter(name);
                if (param != null && param.HasValue)
                {
                    double val = GetParameterDoubleValue(param);
                    if (val > 0) return val;
                }
            }

            // Check type parameters
            var elemType = doc.GetElement(elem.GetTypeId());
            if (elemType != null)
            {
                foreach (var name in pressureParamNames)
                {
                    var param = elemType.LookupParameter(name);
                    if (param != null && param.HasValue)
                    {
                        double val = GetParameterDoubleValue(param);
                        if (val > 0) return val;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Extract a double value from a parameter regardless of storage type.
        /// </summary>
        private static double GetParameterDoubleValue(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.String:
                    double.TryParse(param.AsString(), out double parsed);
                    return parsed;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Get the building height by finding the maximum level elevation.
        /// </summary>
        private static double GetBuildingHeight(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .WhereElementIsNotElementType()
                .Cast<Level>()
                .ToList();

            if (levels.Count == 0)
                return 0;

            return levels.Max(l => l.Elevation);
        }

        /// <summary>
        /// Check if a parameter has a value but it is effectively empty.
        /// </summary>
        private static bool IsParameterEmpty(Parameter param)
        {
            if (!param.HasValue) return true;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return string.IsNullOrWhiteSpace(param.AsString());
                case StorageType.Double:
                    return Math.Abs(param.AsDouble()) < 0.0001;
                case StorageType.Integer:
                    return param.AsInteger() == 0;
                case StorageType.ElementId:
                    return param.AsElementId() == ElementId.InvalidElementId;
                default:
                    return true;
            }
        }

        #endregion
    }
}
