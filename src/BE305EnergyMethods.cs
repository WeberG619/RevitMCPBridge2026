using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Services;

namespace RevitMCPBridge
{
    /// <summary>
    /// MCP-exposed methods for BE305 energy compliance validation.
    /// Covers Miami-Dade BE305 ordinance: 15% improvement over IECC 2021 CZ 1A baseline.
    /// Phase 1: validate and report only — no parameter write-back.
    /// Parallels FBCWindLoadMethods.cs in structure and naming conventions.
    /// </summary>
    public static class BE305EnergyMethods
    {
        #region validateEnergyCompliance

        /// <summary>
        /// Scan exterior walls, roofs, windows, and curtain wall panels for U-value and SHGC compliance
        /// against BE305/IECC Climate Zone 1A tables. Returns per-element PASS/FAIL/UNKNOWN results.
        /// </summary>
        [MCPMethod("validateEnergyCompliance", Category = "BE305Compliance",
            Description = "Validate BE305 energy compliance for exterior walls, roofs, windows, and curtain wall panels")]
        public static string ValidateEnergyCompliance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var engine = BE305ComplianceEngine.Instance;
                if (!engine.IsLoaded)
                {
                    return ResponseBuilder.Error(
                        $"BE305 compliance engine not loaded: {engine.LoadError}", "ENGINE_NOT_LOADED").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parameter extraction
                var climateZone  = parameters["climateZone"]?.ToString() ?? "1A";
                var buildingType = parameters["buildingType"]?.ToString() ?? "nonresidential";
                var be305Mode    = parameters["be305Mode"]?.Value<bool?>() ?? true;

                var limits = engine.GetEnvelopeLimits(climateZone, buildingType, be305Mode);
                if (limits == null)
                {
                    return ResponseBuilder.Error(
                        $"No code data found for climate zone '{climateZone}', building type '{buildingType}'.",
                        "DATA_NOT_FOUND").Build();
                }

                // Collect target elements
                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                var elements   = new List<Element>();

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
                    // Exterior walls
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

                    // Curtain wall panels
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                            .WhereElementIsNotElementType());
                }

                // Evaluate each element
                var results = new List<object>();
                int passed = 0, failed = 0, unknown = 0;

                foreach (var elem in elements)
                {
                    var category = elem.Category?.Name ?? "Unknown";
                    bool isWindow = IsWindowOrGlazing(elem);
                    bool isRoof   = elem.Category?.Id.Value == (int)BuiltInCategory.OST_Roofs;

                    if (isWindow)
                    {
                        var result = EvaluateFenestration(doc, engine, elem, limits);
                        results.Add(new
                        {
                            elementId           = result.ElementId,
                            elementType         = result.ElementType,
                            elementName         = result.ElementName,
                            category,
                            uFactor             = result.UFactor.HasValue ? Math.Round(result.UFactor.Value, 4) : (double?)null,
                            maxAllowedUFactor   = result.MaxAllowedUFactor.HasValue ? Math.Round(result.MaxAllowedUFactor.Value, 4) : (double?)null,
                            shgc                = result.SHGC.HasValue ? Math.Round(result.SHGC.Value, 4) : (double?)null,
                            maxAllowedSHGC      = result.MaxAllowedSHGC.HasValue ? Math.Round(result.MaxAllowedSHGC.Value, 4) : (double?)null,
                            hasEnergyData       = result.HasEnergyData,
                            status              = result.Status,
                            notes               = result.Notes
                        });
                    }
                    else
                    {
                        // Wall or roof — opaque envelope
                        var result = EvaluateOpaqueAssembly(doc, engine, elem, limits, isRoof);
                        results.Add(new
                        {
                            elementId           = result.ElementId,
                            elementType         = result.ElementType,
                            elementName         = result.ElementName,
                            category,
                            assemblyUValue      = result.AssemblyUValue.HasValue ? Math.Round(result.AssemblyUValue.Value, 4) : (double?)null,
                            maxAllowedUValue    = result.MaxAllowedUValue.HasValue ? Math.Round(result.MaxAllowedUValue.Value, 4) : (double?)null,
                            be305MaxUValue      = result.BE305MaxUValue.HasValue ? Math.Round(result.BE305MaxUValue.Value, 4) : (double?)null,
                            hasEnergyData       = result.HasEnergyData,
                            status              = result.Status,
                            notes               = result.Notes
                        });
                    }

                    switch (results.Last() is { } r ? GetStatus(r) : "UNKNOWN")
                    {
                        case "PASS":    passed++;  break;
                        case "FAIL":    failed++;  break;
                        default:        unknown++; break;
                    }
                }

                return ResponseBuilder.Success()
                    .With("climateZone", climateZone)
                    .With("buildingType", buildingType)
                    .With("be305Mode", be305Mode)
                    .With("be305ImprovementFactor", engine.BE305ImprovementFactor)
                    .With("limits", new
                    {
                        opaqueWallMaxU      = Math.Round(limits.OpaqueWallMaxU, 4),
                        roofMaxU            = Math.Round(limits.RoofMaxU, 4),
                        fenestrationMaxU    = Math.Round(limits.FenestrationMaxU, 4),
                        fenestrationMaxSHGC = Math.Round(limits.FenestrationMaxSHGC, 4)
                    })
                    .With("summary", new { total = results.Count, passed, failed, unknown })
                    .With("elements", results)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BE305EnergyMethods.ValidateEnergyCompliance failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region checkMissingEnergyParameters

        /// <summary>
        /// Scan walls, roofs, windows, curtain wall panels, lighting fixtures, and mechanical
        /// equipment for missing BE305 shared parameters. Separates critical from informational.
        /// </summary>
        [MCPMethod("checkMissingEnergyParameters", Category = "BE305Compliance",
            Description = "Check for missing BE305 energy compliance parameters on envelope elements, lighting, and HVAC")]
        public static string CheckMissingEnergyParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                var elements   = new List<Element>();

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
                    // Exterior walls
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

                    // Curtain wall panels
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                            .WhereElementIsNotElementType());

                    // Lighting fixtures
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_LightingFixtures)
                            .WhereElementIsNotElementType());

                    // Mechanical equipment
                    elements.AddRange(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                            .WhereElementIsNotElementType());
                }

                // Determine which parameters to expect per category
                var results = new List<object>();

                foreach (var elem in elements)
                {
                    var expectedParams = GetExpectedParamsForElement(elem);
                    if (expectedParams.Length == 0) continue;

                    var missing  = new List<string>();

                    foreach (var paramName in expectedParams)
                    {
                        var param = elem.LookupParameter(paramName);
                        if (param == null || !param.HasValue || IsParameterEmpty(param))
                        {
                            // Check type parameters as fallback
                            var elemType  = doc.GetElement(elem.GetTypeId());
                            var typeParam = elemType?.LookupParameter(paramName);

                            if (typeParam == null || !typeParam.HasValue || IsParameterEmpty(typeParam))
                            {
                                missing.Add(paramName);
                            }
                        }
                    }

                    if (missing.Count > 0)
                    {
                        results.Add(new
                        {
                            elementId        = (int)elem.Id.Value,
                            elementType      = elem.GetType().Name,
                            elementName      = elem.Name ?? "(unnamed)",
                            category         = elem.Category?.Name ?? "Unknown",
                            missingParameters = missing,
                            missingCritical   = missing.Intersect(BE305ComplianceEngine.CriticalEnergyParameterNames).ToList()
                        });
                    }
                }

                int totalChecked    = elements.Count;
                int withMissing     = results.Count;
                int fullyCompliant  = totalChecked - withMissing;

                return ResponseBuilder.Success()
                    .With("totalElementsChecked", totalChecked)
                    .With("elementsWithMissingParams", withMissing)
                    .With("fullyCompliantElements", fullyCompliant)
                    .With("elements", results)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BE305EnergyMethods.CheckMissingEnergyParameters failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region getEnergySummary

        /// <summary>
        /// Get overall model energy compliance summary: envelope pass/fail counts,
        /// calculated LPD by room, HVAC equipment status, and a compliance opinion.
        /// </summary>
        [MCPMethod("getEnergySummary", "getEnergyComplianceSummary", Category = "BE305Compliance",
            Description = "Get overall BE305 energy compliance summary for the model including LPD and HVAC")]
        public static string GetEnergySummary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var engine = BE305ComplianceEngine.Instance;
                if (!engine.IsLoaded)
                {
                    return ResponseBuilder.Error(
                        $"BE305 compliance engine not loaded: {engine.LoadError}", "ENGINE_NOT_LOADED").Build();
                }

                var doc = uiApp.ActiveUIDocument.Document;

                var climateZone  = parameters["climateZone"]?.ToString() ?? "1A";
                var buildingType = parameters["buildingType"]?.ToString() ?? "nonresidential";
                var be305Mode    = parameters["be305Mode"]?.Value<bool?>() ?? true;

                var limits = engine.GetEnvelopeLimits(climateZone, buildingType, be305Mode);
                if (limits == null)
                {
                    return ResponseBuilder.Error(
                        $"No code data found for climate zone '{climateZone}', building type '{buildingType}'.",
                        "DATA_NOT_FOUND").Build();
                }

                // --- Envelope ---
                var envelopeElements = new List<Element>();

                var extWalls = new FilteredElementCollector(doc)
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

                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .ToList();

                var cwPanels = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .ToList();

                envelopeElements.AddRange(extWalls);
                envelopeElements.AddRange(roofs);
                envelopeElements.AddRange(windows);
                envelopeElements.AddRange(cwPanels);

                int envPassed = 0, envFailed = 0, envUnknown = 0;
                foreach (var elem in envelopeElements)
                {
                    bool isWindow = IsWindowOrGlazing(elem);
                    bool isRoof   = elem.Category?.Id.Value == (int)BuiltInCategory.OST_Roofs;

                    string status;
                    if (isWindow)
                        status = EvaluateFenestration(doc, engine, elem, limits).Status;
                    else
                        status = EvaluateOpaqueAssembly(doc, engine, elem, limits, isRoof).Status;

                    switch (status)
                    {
                        case "PASS":    envPassed++;  break;
                        case "FAIL":    envFailed++;  break;
                        default:        envUnknown++; break;
                    }
                }

                // --- LPD by Room ---
                var lpdResults = CalculateLPDByRoom(doc, engine, climateZone, buildingType, be305Mode);
                int lpdPassed  = lpdResults.Count(r => r.Status == "PASS");
                int lpdFailed  = lpdResults.Count(r => r.Status == "FAIL");
                int lpdUnknown = lpdResults.Count(r => r.Status == "UNKNOWN");

                double grossConditionedAreaSF = parameters["grossConditionedArea_sf"]?.Value<double?>()
                    ?? CalculateGrossConditionedArea(doc);

                // --- HVAC ---
                var hvacResults    = EvaluateAllHVAC(doc, engine, climateZone, buildingType);
                int hvacPassed     = hvacResults.Count(r => r.Status == "PASS");
                int hvacFailed     = hvacResults.Count(r => r.Status == "FAIL");
                int hvacUnknown    = hvacResults.Count(r => r.Status == "UNKNOWN");

                // --- Overall Opinion ---
                string overallOpinion;
                if (envFailed > 0 || lpdFailed > 0 || hvacFailed > 0)
                    overallOpinion = "NON_COMPLIANT";
                else if (envPassed + lpdPassed + hvacPassed > 0 &&
                         envUnknown == 0 && lpdUnknown == 0 && hvacUnknown == 0)
                    overallOpinion = "COMPLIANT";
                else
                    overallOpinion = "INSUFFICIENT_DATA";

                // --- Notes ---
                var notes = new List<string>();
                if (envUnknown > 0)
                    notes.Add($"{envUnknown} envelope element(s) lack U-value or SHGC parameters — cannot be evaluated.");
                if (lpdUnknown > 0)
                    notes.Add($"{lpdUnknown} room(s) have unknown LPD — missing fixture wattage parameters.");
                if (hvacUnknown > 0)
                    notes.Add($"{hvacUnknown} HVAC unit(s) could not be evaluated — missing EER/COP or unit type.");
                if (be305Mode)
                    notes.Add($"BE305 mode active: limits tightened 15% over IECC CZ 1A baseline.");
                if (grossConditionedAreaSF > 0)
                    notes.Add($"Gross conditioned area: {grossConditionedAreaSF:F0} SF (from Rooms).");

                return ResponseBuilder.Success()
                    .With("climateZone", climateZone)
                    .With("buildingType", buildingType)
                    .With("be305Mode", be305Mode)
                    .With("overallOpinion", overallOpinion)
                    .With("grossConditionedAreaSF", Math.Round(grossConditionedAreaSF, 1))
                    .With("envelope", new
                    {
                        wallCount    = extWalls.Count,
                        roofCount    = roofs.Count,
                        windowCount  = windows.Count,
                        cwPanelCount = cwPanels.Count,
                        passed       = envPassed,
                        failed       = envFailed,
                        unknown      = envUnknown
                    })
                    .With("lpd", new
                    {
                        roomsChecked = lpdResults.Count,
                        passed       = lpdPassed,
                        failed       = lpdFailed,
                        unknown      = lpdUnknown,
                        rooms        = lpdResults.Select(r => new
                        {
                            r.RoomId,
                            r.RoomName,
                            r.SpaceType,
                            areaSF               = Math.Round(r.AreaSF, 1),
                            totalFixtureWatts     = Math.Round(r.TotalFixtureWatts, 1),
                            actualLPD_W_per_SF    = Math.Round(r.ActualLPD_W_per_SF, 3),
                            maxAllowedLPD_W_per_SF = Math.Round(r.MaxAllowedLPD_W_per_SF, 3),
                            r.FixtureCount,
                            r.Status,
                            r.Notes
                        })
                    })
                    .With("hvac", new
                    {
                        unitsChecked = hvacResults.Count,
                        passed       = hvacPassed,
                        failed       = hvacFailed,
                        unknown      = hvacUnknown,
                        units        = hvacResults.Select(r => new
                        {
                            r.ElementId,
                            r.ElementName,
                            r.UnitType,
                            r.Metric,
                            actualValue     = r.ActualValue.HasValue   ? Math.Round(r.ActualValue.Value, 2)   : (double?)null,
                            minimumRequired = r.MinimumRequired.HasValue ? Math.Round(r.MinimumRequired.Value, 2) : (double?)null,
                            r.Status,
                            r.Notes
                        })
                    })
                    .With("notes", notes)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BE305EnergyMethods.GetEnergySummary failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region getEnergyCodeTables

        /// <summary>
        /// Return the raw code tables for a given climate zone and building type.
        /// Shows both IECC baseline and BE305-adjusted limits side-by-side.
        /// </summary>
        [MCPMethod("getEnergyCodeTables", Category = "BE305Compliance",
            Description = "Return BE305/IECC energy code limit tables for a given climate zone and building type")]
        public static string GetEnergyCodeTables(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var engine = BE305ComplianceEngine.Instance;
                if (!engine.IsLoaded)
                {
                    return ResponseBuilder.Error(
                        $"BE305 compliance engine not loaded: {engine.LoadError}", "ENGINE_NOT_LOADED").Build();
                }

                var climateZone  = parameters["climateZone"]?.ToString() ?? "1A";
                var buildingType = parameters["buildingType"]?.ToString() ?? "nonresidential";

                // IECC baseline limits (be305Mode = false)
                var ieccLimits = engine.GetEnvelopeLimits(climateZone, buildingType, be305Mode: false);
                if (ieccLimits == null)
                {
                    // Return list of available climate zones
                    var available = engine.GetClimateZoneNames().OrderBy(z => z).ToList();
                    return ResponseBuilder.Error(
                        $"No data for climate zone '{climateZone}', building type '{buildingType}'. Available zones: {string.Join(", ", available)}",
                        "DATA_NOT_FOUND").Build();
                }

                // BE305-adjusted limits
                var be305Limits = engine.GetEnvelopeLimits(climateZone, buildingType, be305Mode: true);

                // LPD limits — both IECC and BE305
                double lpdDefault_IECC  = engine.GetLPDLimit(climateZone, "default", buildingType, be305Mode: false);
                double lpdOffice_IECC   = engine.GetLPDLimit(climateZone, "office", buildingType, be305Mode: false);
                double lpdRetail_IECC   = engine.GetLPDLimit(climateZone, "retail", buildingType, be305Mode: false);
                double lpdDefault_BE305 = engine.GetLPDLimit(climateZone, "default", buildingType, be305Mode: true);
                double lpdOffice_BE305  = engine.GetLPDLimit(climateZone, "office", buildingType, be305Mode: true);
                double lpdRetail_BE305  = engine.GetLPDLimit(climateZone, "retail", buildingType, be305Mode: true);

                // Raw zone data from JSON
                var rawZone = engine.GetRawTableData(climateZone);
                var rawBe305 = engine.GetRawBE305Adjustment();

                var notes = new List<string>
                {
                    $"BE305 applies {engine.BE305ImprovementFactor:P0} improvement over IECC CZ {climateZone} baseline.",
                    "Lower U-value and SHGC limits require better-performing assemblies and glazing products.",
                    "LPD limits are per IECC 2021 Table C405.3.2(1) space-by-space method.",
                    "HVAC minimum EER/COP from ASHRAE 90.1-2019 — not adjusted by BE305 in Phase 1.",
                    "For permit documents, verify all values against the current ASCE 7 Hazard Tool and local AHJ."
                };

                return ResponseBuilder.Success()
                    .With("climateZone", climateZone)
                    .With("buildingType", buildingType)
                    .With("iecc_baseline", new
                    {
                        opaque_wall_max_u        = Math.Round(ieccLimits.OpaqueWallMaxU, 4),
                        roof_max_u               = Math.Round(ieccLimits.RoofMaxU, 4),
                        fenestration_max_u        = Math.Round(ieccLimits.FenestrationMaxU, 4),
                        fenestration_max_shgc     = Math.Round(ieccLimits.FenestrationMaxSHGC, 4),
                        lpd_default_max_w_per_sf  = Math.Round(lpdDefault_IECC, 3),
                        lpd_office_max_w_per_sf   = Math.Round(lpdOffice_IECC, 3),
                        lpd_retail_max_w_per_sf   = Math.Round(lpdRetail_IECC, 3)
                    })
                    .With("be305_adjusted", new
                    {
                        opaque_wall_max_u        = Math.Round(be305Limits.OpaqueWallMaxU, 4),
                        roof_max_u               = Math.Round(be305Limits.RoofMaxU, 4),
                        fenestration_max_u        = Math.Round(be305Limits.FenestrationMaxU, 4),
                        fenestration_max_shgc     = Math.Round(be305Limits.FenestrationMaxSHGC, 4),
                        lpd_default_max_w_per_sf  = Math.Round(lpdDefault_BE305, 3),
                        lpd_office_max_w_per_sf   = Math.Round(lpdOffice_BE305, 3),
                        lpd_retail_max_w_per_sf   = Math.Round(lpdRetail_BE305, 3)
                    })
                    .With("be305_improvement_factor", engine.BE305ImprovementFactor)
                    .With("notes", notes)
                    .Build();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BE305EnergyMethods.GetEnergyCodeTables failed");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Private Helpers — Element Evaluation

        /// <summary>
        /// Evaluate an opaque wall or roof assembly against U-value limits.
        /// Reads U-value from BE305 shared params, falls back to standard param aliases,
        /// and converts R-value to U-value when only R is available.
        /// </summary>
        private static BE305ComplianceEngine.EnergyComplianceResult EvaluateOpaqueAssembly(
            Document doc, BE305ComplianceEngine engine, Element elem,
            BE305ComplianceEngine.EnvelopeLimits limits, bool isRoof)
        {
            var result = new BE305ComplianceEngine.EnergyComplianceResult
            {
                ElementId   = (int)elem.Id.Value,
                ElementType = elem.GetType().Name,
                ElementName = elem.Name ?? "(unnamed)",
                Category    = elem.Category?.Name ?? "Unknown"
            };

            // Try U-value parameter names
            var uParamKey    = isRoof ? "roof_u_value" : "wall_u_value";
            var rParamKey    = isRoof ? "roof_r_value" : "wall_r_value";
            var uParamNames  = engine.GetParameterNames(uParamKey);
            var rParamNames  = engine.GetParameterNames(rParamKey);

            double uValue = GetNumericParamValue(doc, elem, uParamNames);
            bool usedRValue = false;

            if (uValue <= 0)
            {
                // Fall back to R-value and convert
                double rValue = GetNumericParamValue(doc, elem, rParamNames);
                if (rValue > 0)
                {
                    uValue     = engine.RValueToUValue(rValue);
                    usedRValue = true;
                }
            }

            double maxU = isRoof ? limits.RoofMaxU : limits.OpaqueWallMaxU;

            result.HasEnergyData = uValue > 0;
            result.AssemblyUValue  = uValue > 0 ? uValue : (double?)null;
            result.MaxAllowedUValue = maxU;
            result.BE305MaxUValue   = maxU; // already BE305-adjusted if limits.BE305Applied

            result.Status = engine.EvaluateWall(uValue, maxU);

            var notes = new List<string>();
            if (!result.HasEnergyData)
                notes.Add("No U-value or R-value parameter found on element or type. Run checkMissingEnergyParameters.");
            else if (usedRValue)
                notes.Add("U-value derived from R-value parameter (U = 1/R).");
            if (result.Status == "FAIL")
                notes.Add($"Assembly U-{uValue:F4} exceeds {(limits.BE305Applied ? "BE305" : "IECC")} max U-{maxU:F4}.");

            result.Notes = notes.Count > 0 ? string.Join(" ", notes) : null;

            return result;
        }

        /// <summary>
        /// Evaluate a window or curtain wall panel against U-factor and SHGC limits.
        /// </summary>
        private static BE305ComplianceEngine.EnergyComplianceResult EvaluateFenestration(
            Document doc, BE305ComplianceEngine engine, Element elem,
            BE305ComplianceEngine.EnvelopeLimits limits)
        {
            var result = new BE305ComplianceEngine.EnergyComplianceResult
            {
                ElementId   = (int)elem.Id.Value,
                ElementType = elem.GetType().Name,
                ElementName = elem.Name ?? "(unnamed)",
                Category    = elem.Category?.Name ?? "Unknown"
            };

            var uParamNames    = engine.GetParameterNames("window_u_factor");
            var shgcParamNames = engine.GetParameterNames("window_shgc");

            double uFactor = GetNumericParamValue(doc, elem, uParamNames);
            double shgc    = GetNumericParamValue(doc, elem, shgcParamNames);

            result.HasEnergyData      = uFactor > 0 || shgc > 0;
            result.UFactor            = uFactor > 0 ? uFactor : (double?)null;
            result.MaxAllowedUFactor  = limits.FenestrationMaxU;
            result.SHGC               = shgc > 0 ? shgc : (double?)null;
            result.MaxAllowedSHGC     = limits.FenestrationMaxSHGC;

            result.Status = engine.EvaluateWindow(uFactor, shgc, limits, out string failReason);

            var notes = new List<string>();
            if (!result.HasEnergyData)
                notes.Add("No U-factor or SHGC parameter found on element or type. Run checkMissingEnergyParameters.");
            if (failReason != null)
                notes.Add(failReason);

            result.Notes = notes.Count > 0 ? string.Join(" ", notes) : null;

            return result;
        }

        /// <summary>
        /// Calculate LPD for each room by grouping lighting fixtures spatially.
        /// Uses Room.IsPointInRoom() to assign fixtures to rooms.
        /// </summary>
        private static List<BE305ComplianceEngine.LPDRoomResult> CalculateLPDByRoom(
            Document doc, BE305ComplianceEngine engine,
            string climateZone, string buildingType, bool be305Mode)
        {
            var results = new List<BE305ComplianceEngine.LPDRoomResult>();

            // Collect all rooms
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<SpatialElement>()
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            if (rooms.Count == 0) return results;

            // Collect all lighting fixtures
            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            // Assign fixtures to rooms
            var roomFixtures = new Dictionary<int, List<Element>>();
            foreach (var room in rooms)
                roomFixtures[(int)room.Id.Value] = new List<Element>();

            var wattsParamNames = engine.GetParameterNames("fixture_watts");

            foreach (var fixture in fixtures)
            {
                var loc = fixture.Location as LocationPoint;
                if (loc == null) continue;

                // Find which room contains this fixture
                foreach (var room in rooms)
                {
                    if (room.IsPointInRoom(loc.Point))
                    {
                        roomFixtures[(int)room.Id.Value].Add(fixture);
                        break;
                    }
                }
            }

            // Evaluate each room
            foreach (var room in rooms)
            {
                var roomFixtureList = roomFixtures[(int)room.Id.Value];

                double totalWatts = 0;
                foreach (var fixture in roomFixtureList)
                {
                    double watts = GetNumericParamValue(doc, fixture, wattsParamNames);
                    totalWatts += watts;
                }

                // Room area in square feet (Revit internal unit is sq ft for area)
                double areaSF = room.Area;   // FilteredElementCollector area is in internal units (sq ft)

                // Read space type from room parameters
                string spaceType = GetRoomSpaceType(room);

                double lpd      = engine.CalculateLPD(totalWatts, areaSF);
                double lpdLimit = engine.GetLPDLimit(climateZone, spaceType, buildingType, be305Mode);

                string status;
                string notes = null;

                if (totalWatts <= 0)
                {
                    status = roomFixtureList.Count > 0 ? "UNKNOWN" : "UNKNOWN";
                    notes  = roomFixtureList.Count > 0
                        ? $"{roomFixtureList.Count} fixture(s) found but no wattage data. Add BE305_Fixture_Watts parameter."
                        : "No lighting fixtures found in room.";
                }
                else
                {
                    status = engine.EvaluateLPD(lpd, lpdLimit);
                    if (status == "FAIL")
                        notes = $"LPD {lpd:F3} W/sf exceeds {(be305Mode ? "BE305" : "IECC")} max {lpdLimit:F3} W/sf for space type '{spaceType}'.";
                }

                results.Add(new BE305ComplianceEngine.LPDRoomResult
                {
                    RoomId                 = (int)room.Id.Value,
                    RoomName               = room.Name ?? "(unnamed)",
                    SpaceType              = spaceType,
                    AreaSF                 = areaSF,
                    TotalFixtureWatts      = totalWatts,
                    ActualLPD_W_per_SF     = lpd,
                    MaxAllowedLPD_W_per_SF = lpdLimit,
                    FixtureCount           = roomFixtureList.Count,
                    Status                 = status,
                    Notes                  = notes
                });
            }

            return results;
        }

        /// <summary>
        /// Evaluate all mechanical equipment elements against HVAC minimum efficiency requirements.
        /// </summary>
        private static List<BE305ComplianceEngine.HVACComplianceResult> EvaluateAllHVAC(
            Document doc, BE305ComplianceEngine engine, string climateZone, string buildingType)
        {
            var results = new List<BE305ComplianceEngine.HVACComplianceResult>();

            var hvacElements = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            var eerParamNames      = engine.GetParameterNames("hvac_eer");
            var copParamNames      = engine.GetParameterNames("hvac_cop");
            var unitTypeParamNames = engine.GetParameterNames("hvac_unit_type");

            foreach (var elem in hvacElements)
            {
                var hvacResult = new BE305ComplianceEngine.HVACComplianceResult
                {
                    ElementId   = (int)elem.Id.Value,
                    ElementType = elem.GetType().Name,
                    ElementName = elem.Name ?? "(unnamed)"
                };

                // Determine unit type for table lookup
                string unitType = GetStringParamValue(doc, elem, unitTypeParamNames) ?? "default_ac";
                hvacResult.UnitType = unitType;

                var entry = engine.GetHVACMinimum(unitType, climateZone, buildingType);

                // Try EER first, then COP based on what the entry expects
                double eer = GetNumericParamValue(doc, elem, eerParamNames);
                double cop = GetNumericParamValue(doc, elem, copParamNames);

                if (entry != null && entry.Metric == "COP" && cop > 0)
                {
                    hvacResult.Metric          = "COP";
                    hvacResult.ActualValue     = cop;
                    hvacResult.MinimumRequired = entry.Minimum;
                    hvacResult.Status          = engine.EvaluateHVAC(cop, entry, out string failReason);
                    hvacResult.Notes           = failReason;
                }
                else if (eer > 0)
                {
                    hvacResult.Metric          = "EER";
                    hvacResult.ActualValue     = eer;
                    hvacResult.MinimumRequired = entry?.Minimum;
                    hvacResult.Status          = engine.EvaluateHVAC(eer, entry, out string failReason);
                    hvacResult.Notes           = failReason;
                }
                else
                {
                    hvacResult.Metric = entry?.Metric ?? "EER";
                    hvacResult.Status = "UNKNOWN";
                    hvacResult.Notes  = "No EER or COP parameter found on unit. Add BE305_EER or BE305_COP, and BE305_Unit_Type.";
                }

                results.Add(hvacResult);
            }

            return results;
        }

        #endregion

        #region Private Helpers — Parameter Reading

        /// <summary>
        /// Try to read a numeric double value from an element using an ordered list of parameter names.
        /// Checks instance first, then element type. Returns 0 if not found.
        /// </summary>
        private static double GetNumericParamValue(Document doc, Element elem, string[] paramNames)
        {
            if (paramNames == null || paramNames.Length == 0) return 0;

            // Check instance parameters
            foreach (var name in paramNames)
            {
                var param = elem.LookupParameter(name);
                if (param != null && param.HasValue)
                {
                    double val = ExtractDouble(param);
                    if (val > 0) return val;
                }
            }

            // Check element type parameters
            var elemType = doc.GetElement(elem.GetTypeId());
            if (elemType != null)
            {
                foreach (var name in paramNames)
                {
                    var param = elemType.LookupParameter(name);
                    if (param != null && param.HasValue)
                    {
                        double val = ExtractDouble(param);
                        if (val > 0) return val;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Try to read a string value from an element using an ordered list of parameter names.
        /// Checks instance first, then element type. Returns null if not found.
        /// </summary>
        private static string GetStringParamValue(Document doc, Element elem, string[] paramNames)
        {
            if (paramNames == null || paramNames.Length == 0) return null;

            foreach (var name in paramNames)
            {
                var param = elem.LookupParameter(name);
                if (param != null && param.HasValue && param.StorageType == StorageType.String)
                {
                    var val = param.AsString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }

            var elemType = doc.GetElement(elem.GetTypeId());
            if (elemType != null)
            {
                foreach (var name in paramNames)
                {
                    var param = elemType.LookupParameter(name);
                    if (param != null && param.HasValue && param.StorageType == StorageType.String)
                    {
                        var val = param.AsString();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Extract a double value from a Revit parameter regardless of storage type.
        /// </summary>
        private static double ExtractDouble(Parameter param)
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
        /// Check whether a parameter has a value that is effectively empty.
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

        /// <summary>
        /// Determine which BE305 parameters are expected for an element based on its category.
        /// </summary>
        private static string[] GetExpectedParamsForElement(Element elem)
        {
            var catId = elem.Category?.Id.Value ?? 0;

            if (catId == (int)BuiltInCategory.OST_Windows ||
                catId == (int)BuiltInCategory.OST_CurtainWallPanels)
            {
                return new[] { "BE305_U_Factor", "BE305_SHGC" };
            }

            if (catId == (int)BuiltInCategory.OST_Roofs ||
                elem is Wall wall && wall.WallType?.Function == WallFunction.Exterior)
            {
                return new[] { "BE305_Assembly_U_Value", "BE305_R_Value" };
            }

            if (catId == (int)BuiltInCategory.OST_LightingFixtures)
            {
                return new[] { "BE305_Fixture_Watts" };
            }

            if (catId == (int)BuiltInCategory.OST_MechanicalEquipment)
            {
                return new[] { "BE305_EER", "BE305_COP", "BE305_Unit_Type" };
            }

            // Generic wall (non-exterior also checked but with no required params in Phase 1)
            return Array.Empty<string>();
        }

        /// <summary>
        /// Determine if an element is a window, curtain wall panel, or other glazing type.
        /// </summary>
        private static bool IsWindowOrGlazing(Element elem)
        {
            var catId = elem.Category?.Id.Value ?? 0;
            return catId == (int)BuiltInCategory.OST_Windows ||
                   catId == (int)BuiltInCategory.OST_CurtainWallPanels;
        }

        /// <summary>
        /// Read the space type from a room's parameters, mapping to LPD table keys.
        /// Falls back to "default".
        /// </summary>
        private static string GetRoomSpaceType(Room room)
        {
            // Try "Space Type" parameter first (IECC / ASHRAE space classification)
            string[] spaceTypeParamNames = { "Space Type", "Room: Space Type", "Occupancy", "Room: Occupancy" };

            foreach (var name in spaceTypeParamNames)
            {
                var param = room.LookupParameter(name);
                if (param != null && param.HasValue && param.StorageType == StorageType.String)
                {
                    var val = param.AsString();
                    if (!string.IsNullOrWhiteSpace(val))
                        return NormalizeSpaceType(val);
                }
            }

            // Try to guess from room name
            return NormalizeSpaceType(room.Name ?? "");
        }

        /// <summary>
        /// Normalize a room name or occupancy string to a recognized LPD table key.
        /// </summary>
        private static string NormalizeSpaceType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "default";

            var lower = raw.ToLowerInvariant().Trim();

            if (lower.Contains("office"))          return "office";
            if (lower.Contains("conference"))       return "conference_room";
            if (lower.Contains("meeting"))          return "conference_room";
            if (lower.Contains("corridor") ||
                lower.Contains("hallway"))          return "corridor";
            if (lower.Contains("retail") ||
                lower.Contains("shop"))             return "retail";
            if (lower.Contains("exam"))             return "healthcare_exam_room";
            if (lower.Contains("clinic") ||
                lower.Contains("medical"))          return "healthcare_clinic";
            if (lower.Contains("hotel") ||
                lower.Contains("guest room") ||
                lower.Contains("motel"))            return "hotel_motel_guest_room";
            if (lower.Contains("lobby"))            return "lobby_general";
            if (lower.Contains("dining") ||
                lower.Contains("restaurant"))       return "restaurant_dining";
            if (lower.Contains("kitchen"))          return "restaurant_kitchen";
            if (lower.Contains("warehouse") ||
                lower.Contains("storage"))          return "warehouse";
            if (lower.Contains("classroom") ||
                lower.Contains("class"))            return "school_classroom";
            if (lower.Contains("assembly") ||
                lower.Contains("auditorium"))       return "assembly_general";
            if (lower.Contains("stair"))            return "stairwell";
            if (lower.Contains("restroom") ||
                lower.Contains("toilet") ||
                lower.Contains("bathroom"))         return "restroom";
            if (lower.Contains("mechanical"))       return "mechanical_room";
            if (lower.Contains("parking") ||
                lower.Contains("garage"))           return "parking_garage_interior";

            return "default";
        }

        /// <summary>
        /// Calculate gross conditioned floor area from all rooms with area > 0.
        /// Area in Revit is returned in internal units (square feet for imperial projects).
        /// </summary>
        private static double CalculateGrossConditionedArea(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<SpatialElement>()
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .Sum(r => r.Area);
        }

        /// <summary>
        /// Helper to extract the "status" field from an anonymous object returned by element evaluation.
        /// Used only for the inline switch in ValidateEnergyCompliance.
        /// </summary>
        private static string GetStatus(object result)
        {
            return result?.GetType().GetProperty("status")?.GetValue(result)?.ToString() ?? "UNKNOWN";
        }

        #endregion
    }
}
