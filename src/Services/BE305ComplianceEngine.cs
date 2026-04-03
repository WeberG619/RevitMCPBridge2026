using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge.Services
{
    /// <summary>
    /// Core BE305 energy compliance engine.
    /// Loads be305_energy_tables.json and provides evaluation methods per
    /// IECC 2021 / ASHRAE 90.1-2019 / Florida Building Code 9th Edition Energy Volume
    /// with Miami-Dade BE305 15% improvement amendment.
    /// Singleton — data is loaded once and reused across all MCP calls.
    /// </summary>
    public sealed class BE305ComplianceEngine
    {
        private static readonly Lazy<BE305ComplianceEngine> _instance =
            new Lazy<BE305ComplianceEngine>(() => new BE305ComplianceEngine());

        public static BE305ComplianceEngine Instance => _instance.Value;

        private JObject _data;
        private bool _loaded;
        private string _loadError;

        // Parsed lookup structures
        private Dictionary<string, ClimateZoneData> _climateZones;
        private Dictionary<string, string[]> _parameterNames;
        private double _be305ImprovementFactor;

        private BE305ComplianceEngine()
        {
            _climateZones = new Dictionary<string, ClimateZoneData>(StringComparer.OrdinalIgnoreCase);
            _parameterNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            _be305ImprovementFactor = 0.15;

            LoadData();
        }

        #region Data Models

        public class EnvelopeLimits
        {
            public double OpaqueWallMaxU { get; set; }
            public double RoofMaxU { get; set; }
            public double FenestrationMaxU { get; set; }
            public double FenestrationMaxSHGC { get; set; }
            public string ClimateZone { get; set; }
            public string BuildingType { get; set; }
            public bool BE305Applied { get; set; }
        }

        public class HVACEfficiencyEntry
        {
            public string Metric { get; set; }   // "EER" or "COP"
            public double Minimum { get; set; }
            public string Description { get; set; }
        }

        public class ClimateZoneData
        {
            public string Description { get; set; }
            // Keyed by building type: "nonresidential", "residential", "semiheated"
            public Dictionary<string, BuildingTypeData> BuildingTypes { get; set; }
                = new Dictionary<string, BuildingTypeData>(StringComparer.OrdinalIgnoreCase);
        }

        public class BuildingTypeData
        {
            public double OpaqueWallMaxU { get; set; }
            public double RoofMaxU { get; set; }
            public double FenestrationMaxU { get; set; }
            public double FenestrationMaxSHGC { get; set; }
            public Dictionary<string, double> LPDBySpaceType { get; set; }
                = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HVACEfficiencyEntry> HVACMinima { get; set; }
                = new Dictionary<string, HVACEfficiencyEntry>(StringComparer.OrdinalIgnoreCase);
        }

        public class EnergyComplianceResult
        {
            public int ElementId { get; set; }
            public string ElementType { get; set; }
            public string ElementName { get; set; }
            public string Category { get; set; }
            // Envelope — walls and roofs
            public double? AssemblyUValue { get; set; }
            public double? MaxAllowedUValue { get; set; }
            public double? BE305MaxUValue { get; set; }
            // Fenestration — windows and curtain wall panels
            public double? UFactor { get; set; }
            public double? MaxAllowedUFactor { get; set; }
            public double? SHGC { get; set; }
            public double? MaxAllowedSHGC { get; set; }
            // LPD — rooms / spaces
            public double? ActualLPD_W_per_SF { get; set; }
            public double? MaxAllowedLPD_W_per_SF { get; set; }
            // HVAC
            public double? ActualEER { get; set; }
            public double? MinRequiredEER { get; set; }
            public double? ActualCOP { get; set; }
            public double? MinRequiredCOP { get; set; }
            // Status
            public bool HasEnergyData { get; set; }
            public string Status { get; set; }  // "PASS", "FAIL", "UNKNOWN"
            public string Notes { get; set; }
        }

        public class LPDRoomResult
        {
            public int RoomId { get; set; }
            public string RoomName { get; set; }
            public string SpaceType { get; set; }
            public double AreaSF { get; set; }
            public double TotalFixtureWatts { get; set; }
            public double ActualLPD_W_per_SF { get; set; }
            public double MaxAllowedLPD_W_per_SF { get; set; }
            public int FixtureCount { get; set; }
            public string Status { get; set; }
            public string Notes { get; set; }
        }

        public class HVACComplianceResult
        {
            public int ElementId { get; set; }
            public string ElementType { get; set; }
            public string ElementName { get; set; }
            public string UnitType { get; set; }
            public string Metric { get; set; }
            public double? ActualValue { get; set; }
            public double? MinimumRequired { get; set; }
            public string Status { get; set; }
            public string Notes { get; set; }
        }

        #endregion

        #region Data Loading

        private void LoadData()
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var jsonPath = Path.Combine(assemblyDir, "data", "BE305Data", "be305_energy_tables.json");

                if (!File.Exists(jsonPath))
                {
                    _loadError = $"BE305 energy tables not found at: {jsonPath}";
                    Log.Warning("BE305ComplianceEngine: {Error}", _loadError);
                    _loaded = false;
                    return;
                }

                var json = File.ReadAllText(jsonPath);
                _data = JObject.Parse(json);

                // Read the improvement factor from JSON so it can be updated without recompile
                _be305ImprovementFactor = _data.SelectToken("be305_adjustment.improvement_factor")?.Value<double>() ?? 0.15;

                ParseClimateZones();
                ParseParameterNames();

                _loaded = true;
                Log.Information("BE305ComplianceEngine: Loaded {ZoneCount} climate zones, BE305 improvement factor = {Factor:P0}",
                    _climateZones.Count, _be305ImprovementFactor);
            }
            catch (Exception ex)
            {
                _loadError = $"Failed to load BE305 energy tables: {ex.Message}";
                Log.Error(ex, "BE305ComplianceEngine: Load failed");
                _loaded = false;
            }
        }

        private void ParseClimateZones()
        {
            var zonesToken = _data["climate_zones"] as JObject;
            if (zonesToken == null) return;

            foreach (var zoneProp in zonesToken.Properties())
            {
                var zoneObj = zoneProp.Value as JObject;
                if (zoneObj == null) continue;

                var czData = new ClimateZoneData
                {
                    Description = zoneObj["description"]?.ToString()
                };

                // Parse each building type within the climate zone
                foreach (var btProp in zoneObj.Properties())
                {
                    if (btProp.Name.StartsWith("_") || btProp.Name == "description" || btProp.Name == "counties_fl")
                        continue;

                    var btObj = btProp.Value as JObject;
                    if (btObj == null) continue;

                    var btData = new BuildingTypeData();

                    // Opaque wall U-value
                    btData.OpaqueWallMaxU = btObj.SelectToken("opaque_wall.max_u_value")?.Value<double>() ?? 0;

                    // Roof U-value (prefer above-deck, fall back to below-deck or plain "roof")
                    btData.RoofMaxU = btObj.SelectToken("roof_insulation_above_deck.max_u_value")?.Value<double>()
                                   ?? btObj.SelectToken("roof_ceiling.max_u_value")?.Value<double>()
                                   ?? btObj.SelectToken("roof.max_u_value")?.Value<double>()
                                   ?? 0;

                    // Fenestration
                    btData.FenestrationMaxU    = btObj.SelectToken("fenestration.max_u_factor")?.Value<double>() ?? 0;
                    btData.FenestrationMaxSHGC = btObj.SelectToken("fenestration.max_shgc")?.Value<double>() ?? 0;

                    // LPD by space type
                    var lpdToken = btObj["lpd_interior_w_per_sf"] as JObject;
                    if (lpdToken != null)
                    {
                        foreach (var lpdProp in lpdToken.Properties())
                        {
                            if (lpdProp.Name.StartsWith("_") || lpdProp.Name == "notes") continue;
                            if (lpdProp.Value.Type == JTokenType.Float || lpdProp.Value.Type == JTokenType.Integer)
                            {
                                btData.LPDBySpaceType[lpdProp.Name] = lpdProp.Value.Value<double>();
                            }
                        }
                    }

                    // HVAC efficiency minimums
                    var hvacToken = btObj["hvac_efficiency"] as JObject;
                    if (hvacToken != null)
                    {
                        foreach (var hvacProp in hvacToken.Properties())
                        {
                            if (hvacProp.Name.StartsWith("_") || hvacProp.Name == "notes") continue;
                            var hvacEntry = hvacProp.Value as JObject;
                            if (hvacEntry == null) continue;

                            btData.HVACMinima[hvacProp.Name] = new HVACEfficiencyEntry
                            {
                                Metric      = hvacEntry["metric"]?.ToString() ?? "EER",
                                Minimum     = hvacEntry["minimum"]?.Value<double>() ?? 0,
                                Description = hvacEntry["description"]?.ToString()
                            };
                        }
                    }

                    czData.BuildingTypes[btProp.Name] = btData;
                }

                _climateZones[zoneProp.Name] = czData;
            }
        }

        private void ParseParameterNames()
        {
            var paramToken = _data["revit_parameter_names"] as JObject;
            if (paramToken == null) return;

            foreach (var prop in paramToken.Properties())
            {
                if (prop.Name.StartsWith("_")) continue;

                var arr = prop.Value as JArray;
                if (arr != null)
                {
                    _parameterNames[prop.Name] = arr.Select(t => t.ToString()).ToArray();
                }
            }
        }

        #endregion

        #region Public Properties

        /// <summary>Returns true if the engine loaded its data successfully.</summary>
        public bool IsLoaded => _loaded;

        /// <summary>Returns the load error message if loading failed.</summary>
        public string LoadError => _loadError;

        /// <summary>BE305 improvement factor (0.15 = 15%).</summary>
        public double BE305ImprovementFactor => _be305ImprovementFactor;

        #endregion

        #region Code Table Queries

        /// <summary>
        /// Get the raw JObject for a climate zone and building type from the loaded data.
        /// Used by getEnergyCodeTables to return the raw table to the caller.
        /// </summary>
        public JObject GetRawTableData(string climateZone = "1A")
        {
            return _data.SelectToken($"climate_zones.{climateZone}") as JObject;
        }

        /// <summary>
        /// Get the BE305 adjustment section from raw data.
        /// </summary>
        public JObject GetRawBE305Adjustment()
        {
            return _data["be305_adjustment"] as JObject;
        }

        /// <summary>
        /// Get envelope compliance limits for the given climate zone and building type,
        /// with optional BE305 15% tightening applied.
        /// </summary>
        /// <param name="climateZone">Climate zone, e.g. "1A"</param>
        /// <param name="buildingType">"nonresidential", "residential", or "semiheated"</param>
        /// <param name="be305Mode">When true, applies 15% improvement over IECC baseline</param>
        /// <returns>EnvelopeLimits or null if zone/type not found</returns>
        public EnvelopeLimits GetEnvelopeLimits(string climateZone = "1A", string buildingType = "nonresidential", bool be305Mode = true)
        {
            if (!_climateZones.TryGetValue(climateZone, out var czData))
                return null;
            if (!czData.BuildingTypes.TryGetValue(buildingType, out var btData))
                return null;

            var limits = new EnvelopeLimits
            {
                ClimateZone        = climateZone,
                BuildingType       = buildingType,
                BE305Applied       = be305Mode,
                OpaqueWallMaxU     = btData.OpaqueWallMaxU,
                RoofMaxU           = btData.RoofMaxU,
                FenestrationMaxU   = btData.FenestrationMaxU,
                FenestrationMaxSHGC = btData.FenestrationMaxSHGC
            };

            if (be305Mode)
            {
                limits.OpaqueWallMaxU      = ApplyBE305(limits.OpaqueWallMaxU);
                limits.RoofMaxU            = ApplyBE305(limits.RoofMaxU);
                limits.FenestrationMaxU    = ApplyBE305(limits.FenestrationMaxU);
                limits.FenestrationMaxSHGC = ApplyBE305(limits.FenestrationMaxSHGC);
            }

            return limits;
        }

        /// <summary>
        /// Get LPD limit (W/sf) for a given space type.
        /// Falls back to "default" if spaceType is not found.
        /// </summary>
        /// <param name="climateZone">Climate zone, e.g. "1A"</param>
        /// <param name="spaceType">Space type key from LPD table, or free text mapped to a key</param>
        /// <param name="buildingType">"nonresidential", "residential", or "semiheated"</param>
        /// <param name="be305Mode">When true, applies 15% tightening</param>
        /// <returns>LPD limit in W/sf, or 0 if not found</returns>
        public double GetLPDLimit(string climateZone = "1A", string spaceType = "default",
            string buildingType = "nonresidential", bool be305Mode = true)
        {
            if (!_climateZones.TryGetValue(climateZone, out var czData)) return 0;
            if (!czData.BuildingTypes.TryGetValue(buildingType, out var btData)) return 0;

            // Try exact match first, then normalized match, then default
            double limit = 0;
            if (btData.LPDBySpaceType.TryGetValue(spaceType, out double exact))
            {
                limit = exact;
            }
            else if (btData.LPDBySpaceType.TryGetValue("default", out double def))
            {
                limit = def;
            }

            return be305Mode && limit > 0 ? ApplyBE305(limit) : limit;
        }

        /// <summary>
        /// Get HVAC minimum efficiency entry for a given unit type key.
        /// Falls back to "default_ac" if unitType key is not found.
        /// </summary>
        public HVACEfficiencyEntry GetHVACMinimum(string unitType, string climateZone = "1A",
            string buildingType = "nonresidential")
        {
            if (!_climateZones.TryGetValue(climateZone, out var czData)) return null;
            if (!czData.BuildingTypes.TryGetValue(buildingType, out var btData)) return null;

            if (btData.HVACMinima.TryGetValue(unitType, out var entry))
                return entry;

            // Fall back to default_ac
            btData.HVACMinima.TryGetValue("default_ac", out var defaultEntry);
            return defaultEntry;
        }

        /// <summary>
        /// Get all available climate zone names.
        /// </summary>
        public IEnumerable<string> GetClimateZoneNames()
        {
            return _climateZones.Keys;
        }

        #endregion

        #region Evaluation Methods

        /// <summary>
        /// Apply the BE305 15% improvement factor to an IECC limit value.
        /// For U-values and LPD: lower is stricter, so multiply by (1 - factor).
        /// </summary>
        /// <param name="ieccLimit">The IECC baseline limit</param>
        /// <returns>BE305-adjusted limit</returns>
        public double ApplyBE305(double ieccLimit)
        {
            return ieccLimit * (1.0 - _be305ImprovementFactor);
        }

        /// <summary>
        /// Evaluate an opaque wall or roof assembly U-value against the limit.
        /// </summary>
        /// <param name="assemblyUValue">Measured assembly U-value (Btu/h·ft²·°F)</param>
        /// <param name="maxAllowedU">Maximum allowed U-value from code table</param>
        /// <returns>"PASS" if assemblyUValue <= maxAllowedU, "FAIL" if over, "UNKNOWN" if no data</returns>
        public string EvaluateWall(double assemblyUValue, double maxAllowedU)
        {
            if (assemblyUValue <= 0 || maxAllowedU <= 0)
                return "UNKNOWN";

            return assemblyUValue <= maxAllowedU ? "PASS" : "FAIL";
        }

        /// <summary>
        /// Evaluate a fenestration product against U-factor and SHGC limits.
        /// Both criteria must pass; the worst failure is reported.
        /// </summary>
        /// <param name="uFactor">Whole-unit U-factor</param>
        /// <param name="shgc">Solar Heat Gain Coefficient (0–1)</param>
        /// <param name="limits">Envelope limits from GetEnvelopeLimits()</param>
        /// <param name="failReason">Output: description of what failed</param>
        /// <returns>"PASS", "FAIL", or "UNKNOWN"</returns>
        public string EvaluateWindow(double uFactor, double shgc, EnvelopeLimits limits, out string failReason)
        {
            failReason = null;

            bool hasU    = uFactor > 0;
            bool hasSHGC = shgc > 0;

            if (!hasU && !hasSHGC)
                return "UNKNOWN";

            var failures = new List<string>();

            if (hasU && limits.FenestrationMaxU > 0 && uFactor > limits.FenestrationMaxU)
            {
                failures.Add($"U-factor {uFactor:F3} exceeds {(limits.BE305Applied ? "BE305" : "IECC")} max {limits.FenestrationMaxU:F3}");
            }

            if (hasSHGC && limits.FenestrationMaxSHGC > 0 && shgc > limits.FenestrationMaxSHGC)
            {
                failures.Add($"SHGC {shgc:F3} exceeds {(limits.BE305Applied ? "BE305" : "IECC")} max {limits.FenestrationMaxSHGC:F3} for CZ 1A {limits.BuildingType}");
            }

            if (failures.Count > 0)
            {
                failReason = string.Join(". ", failures) + ".";
                return "FAIL";
            }

            return "PASS";
        }

        /// <summary>
        /// Calculate Lighting Power Density in watts per square foot.
        /// </summary>
        /// <param name="totalFixtureWatts">Sum of all fixture wattages in the space</param>
        /// <param name="roomAreaSF">Room area in square feet</param>
        /// <returns>LPD in W/sf, or 0 if area is zero</returns>
        public double CalculateLPD(double totalFixtureWatts, double roomAreaSF)
        {
            if (roomAreaSF <= 0) return 0;
            return totalFixtureWatts / roomAreaSF;
        }

        /// <summary>
        /// Evaluate actual LPD against the allowed limit.
        /// </summary>
        public string EvaluateLPD(double actualLPD, double limitLPD)
        {
            if (actualLPD <= 0 || limitLPD <= 0)
                return "UNKNOWN";

            return actualLPD <= limitLPD ? "PASS" : "FAIL";
        }

        /// <summary>
        /// Evaluate HVAC equipment efficiency (EER or COP).
        /// </summary>
        /// <param name="actualValue">Actual EER or COP value from equipment</param>
        /// <param name="entry">HVAC minimum entry from GetHVACMinimum()</param>
        /// <param name="failReason">Output: failure description if FAIL</param>
        /// <returns>"PASS", "FAIL", or "UNKNOWN"</returns>
        public string EvaluateHVAC(double actualValue, HVACEfficiencyEntry entry, out string failReason)
        {
            failReason = null;

            if (actualValue <= 0 || entry == null || entry.Minimum <= 0)
                return "UNKNOWN";

            if (actualValue >= entry.Minimum)
                return "PASS";

            failReason = $"{entry.Metric} {actualValue:F2} below minimum {entry.Minimum:F2} for {entry.Description}.";
            return "FAIL";
        }

        /// <summary>
        /// Convert assembly R-value (IP units: h·ft²·°F/Btu) to U-value.
        /// </summary>
        public double RValueToUValue(double rValue)
        {
            if (rValue <= 0) return 0;
            return 1.0 / rValue;
        }

        #endregion

        #region Parameter Name Lists

        /// <summary>
        /// Get the ordered list of Revit parameter names to try for a given data key.
        /// </summary>
        /// <param name="dataKey">Key from revit_parameter_names table (e.g. "wall_u_value")</param>
        /// <returns>Ordered array of parameter names, or empty array if not found</returns>
        public string[] GetParameterNames(string dataKey)
        {
            return _parameterNames.TryGetValue(dataKey, out var names) ? names : Array.Empty<string>();
        }

        /// <summary>
        /// Complete list of BE305 parameter names that should be present on envelope elements
        /// for a full compliance check. Missing any of these triggers an "incomplete" record.
        /// </summary>
        public static readonly string[] AllEnergyParameterNames = new[]
        {
            "BE305_Assembly_U_Value",
            "BE305_R_Value",
            "BE305_U_Factor",
            "BE305_SHGC",
            "BE305_Fixture_Watts",
            "BE305_EER",
            "BE305_COP",
            "BE305_Unit_Type"
        };

        /// <summary>
        /// Critical parameters whose absence prevents any compliance check from running.
        /// </summary>
        public static readonly string[] CriticalEnergyParameterNames = new[]
        {
            "BE305_Assembly_U_Value",
            "BE305_U_Factor",
            "BE305_SHGC"
        };

        #endregion
    }
}
