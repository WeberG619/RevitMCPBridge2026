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
    /// Core FBC wind load compliance engine.
    /// Loads fbc9_wind_tables.json and provides calculation methods per ASCE 7-22 / FBC 9th Edition.
    /// Singleton — data is loaded once and reused across all MCP calls.
    /// </summary>
    public sealed class FBCComplianceEngine
    {
        private static readonly Lazy<FBCComplianceEngine> _instance =
            new Lazy<FBCComplianceEngine>(() => new FBCComplianceEngine());

        public static FBCComplianceEngine Instance => _instance.Value;

        private JObject _data;
        private bool _loaded;
        private string _loadError;

        // Parsed lookup structures
        private Dictionary<string, CountyWindData> _counties;
        private SortedDictionary<double, Dictionary<string, double>> _kzTable;
        private Dictionary<string, double> _velocityFactors;
        private Dictionary<string, double> _riskCategoryRatios;
        private Dictionary<string, InternalPressureCoefficients> _gcpiTable;
        private Dictionary<string, PressureCoefficient> _gcpCCWalls;
        private Dictionary<string, PressureCoefficient> _gcpCCRoof;
        private Dictionary<string, double> _cpMWFRS;

        private const double GUST_FACTOR_RIGID = 0.85;

        private FBCComplianceEngine()
        {
            _counties = new Dictionary<string, CountyWindData>(StringComparer.OrdinalIgnoreCase);
            _kzTable = new SortedDictionary<double, Dictionary<string, double>>();
            _velocityFactors = new Dictionary<string, double>();
            _riskCategoryRatios = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _gcpiTable = new Dictionary<string, InternalPressureCoefficients>(StringComparer.OrdinalIgnoreCase);
            _gcpCCWalls = new Dictionary<string, PressureCoefficient>(StringComparer.OrdinalIgnoreCase);
            _gcpCCRoof = new Dictionary<string, PressureCoefficient>(StringComparer.OrdinalIgnoreCase);
            _cpMWFRS = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            LoadData();
        }

        #region Data Models

        public class CountyWindData
        {
            public string County { get; set; }
            public int Vult_RCII { get; set; }
            public bool HVHZ { get; set; }
            public Dictionary<string, int> SpeedsByRC { get; set; } = new Dictionary<string, int>();
        }

        public class InternalPressureCoefficients
        {
            public double Positive { get; set; }
            public double Negative { get; set; }
        }

        public class PressureCoefficient
        {
            public double Positive { get; set; }
            public double Negative { get; set; }
        }

        public class WindLoadResult
        {
            public int ElementId { get; set; }
            public string ElementType { get; set; }
            public string ElementName { get; set; }
            public double HeightFt { get; set; }
            public double Kz { get; set; }
            public double Qz_psf { get; set; }
            public double DesignPressure_psf { get; set; }
            public double ElementRatedPressure_psf { get; set; }
            public bool HasRatedPressure { get; set; }
            public string Status { get; set; } // "PASS", "FAIL", "UNKNOWN"
            public string Notes { get; set; }
        }

        public class MissingParameterResult
        {
            public int ElementId { get; set; }
            public string ElementType { get; set; }
            public string ElementName { get; set; }
            public string Category { get; set; }
            public List<string> MissingParameters { get; set; } = new List<string>();
        }

        public class ComplianceSummaryResult
        {
            public string County { get; set; }
            public string ExposureCategory { get; set; }
            public string RiskCategory { get; set; }
            public int WindSpeed_mph { get; set; }
            public bool HVHZ { get; set; }
            public double BuildingHeightFt { get; set; }
            public double Kz_atRoofHeight { get; set; }
            public double Qh_psf { get; set; }
            public Dictionary<string, double> MWFRS_Pressures_psf { get; set; }
            public int ElementsChecked { get; set; }
            public int ElementsPassed { get; set; }
            public int ElementsFailed { get; set; }
            public int ElementsUnknown { get; set; }
            public bool NOA_Required { get; set; }
            public string Notes { get; set; }
        }

        public class WindZoneResult
        {
            public string County { get; set; }
            public int WindSpeed_mph { get; set; }
            public bool HVHZ { get; set; }
            public string ExposureCategory { get; set; }
            public double? BuildingHeightFt { get; set; }
            public double? Kz { get; set; }
            public double? Qz_psf { get; set; }
            public Dictionary<string, int> SpeedsByRiskCategory { get; set; }
            public string Notes { get; set; }
        }

        #endregion

        #region Data Loading

        private void LoadData()
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var jsonPath = Path.Combine(assemblyDir, "data", "FBCData", "fbc9_wind_tables.json");

                if (!File.Exists(jsonPath))
                {
                    _loadError = $"FBC wind tables not found at: {jsonPath}";
                    Log.Warning("FBCComplianceEngine: {Error}", _loadError);
                    _loaded = false;
                    return;
                }

                var json = File.ReadAllText(jsonPath);
                _data = JObject.Parse(json);

                ParseCounties();
                ParseKzTable();
                ParseVelocityFactors();
                ParseRiskCategoryRatios();
                ParseGCpiTable();
                ParseGCpCCWalls();
                ParseGCpCCRoof();
                ParseCpMWFRS();

                _loaded = true;
                Log.Information("FBCComplianceEngine: Loaded {CountyCount} counties, {KzRows} Kz height rows",
                    _counties.Count, _kzTable.Count);
            }
            catch (Exception ex)
            {
                _loadError = $"Failed to load FBC wind tables: {ex.Message}";
                Log.Error(ex, "FBCComplianceEngine: Load failed");
                _loaded = false;
            }
        }

        private void ParseCounties()
        {
            var countiesToken = _data["counties"] as JObject;
            if (countiesToken == null) return;

            foreach (var prop in countiesToken.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj == null) continue;

                var county = new CountyWindData
                {
                    County = prop.Name,
                    Vult_RCII = obj["Vult_RCII"]?.Value<int>() ?? 0,
                    HVHZ = obj["HVHZ"]?.Value<bool>() ?? false
                };

                // Collect all speed keys (Vult_RCII, Vult_RCIII, Vult_RCIV, etc.)
                foreach (var child in obj.Properties())
                {
                    if (child.Name.StartsWith("Vult_"))
                    {
                        county.SpeedsByRC[child.Name] = child.Value.Value<int>();
                    }
                }

                _counties[prop.Name] = county;
            }
        }

        private void ParseKzTable()
        {
            var kzToken = _data.SelectToken("Kz_table.by_height_ft") as JObject;
            if (kzToken == null) return;

            foreach (var prop in kzToken.Properties())
            {
                // Parse height key: "0-15" becomes 15, "20" becomes 20, etc.
                double height = ParseHeightKey(prop.Name);
                var exposures = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                var obj = prop.Value as JObject;
                if (obj == null) continue;

                foreach (var exp in obj.Properties())
                {
                    exposures[exp.Name] = exp.Value.Value<double>();
                }

                _kzTable[height] = exposures;
            }
        }

        private double ParseHeightKey(string key)
        {
            // Handle range keys like "0-15" — use the upper bound
            if (key.Contains("-"))
            {
                var parts = key.Split('-');
                return double.Parse(parts[parts.Length - 1]);
            }
            return double.Parse(key);
        }

        private void ParseVelocityFactors()
        {
            var factorsToken = _data.SelectToken("velocity_pressure.factors") as JObject;
            if (factorsToken == null) return;

            foreach (var prop in factorsToken.Properties())
            {
                if (prop.Value.Type == JTokenType.Float || prop.Value.Type == JTokenType.Integer)
                    _velocityFactors[prop.Name] = prop.Value.Value<double>();
            }
        }

        private void ParseRiskCategoryRatios()
        {
            var rcToken = _data["risk_category_speed_adjustments"] as JObject;
            if (rcToken == null) return;

            foreach (var prop in rcToken.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj?["ratio"] != null)
                {
                    _riskCategoryRatios[prop.Name] = obj["ratio"].Value<double>();
                }
            }
        }

        private void ParseGCpiTable()
        {
            var gcpiToken = _data["GCpi_internal_pressure"] as JObject;
            if (gcpiToken == null) return;

            foreach (var prop in gcpiToken.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj == null) continue;

                _gcpiTable[prop.Name] = new InternalPressureCoefficients
                {
                    Positive = obj["positive"]?.Value<double>() ?? 0,
                    Negative = obj["negative"]?.Value<double>() ?? 0
                };
            }
        }

        private void ParseGCpCCWalls()
        {
            var token = _data["GCp_CC_walls"] as JObject;
            if (token == null) return;

            foreach (var prop in token.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj == null) continue;

                _gcpCCWalls[prop.Name] = new PressureCoefficient
                {
                    Positive = obj["positive"]?.Value<double>() ?? 0,
                    Negative = obj["negative"]?.Value<double>() ?? 0
                };
            }
        }

        private void ParseGCpCCRoof()
        {
            var token = _data["GCp_CC_roof"] as JObject;
            if (token == null) return;

            foreach (var prop in token.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj == null) continue;

                _gcpCCRoof[prop.Name] = new PressureCoefficient
                {
                    Positive = obj["positive"]?.Value<double>() ?? 0,
                    Negative = obj["negative"]?.Value<double>() ?? 0
                };
            }
        }

        private void ParseCpMWFRS()
        {
            var token = _data["Cp_MWFRS"] as JObject;
            if (token == null) return;

            foreach (var prop in token.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj?["Cp"] != null)
                {
                    _cpMWFRS[prop.Name] = obj["Cp"].Value<double>();
                }
            }
        }

        #endregion

        #region Public Query Methods

        /// <summary>
        /// Returns true if the engine loaded its data successfully.
        /// </summary>
        public bool IsLoaded => _loaded;

        /// <summary>
        /// Returns the load error message if loading failed.
        /// </summary>
        public string LoadError => _loadError;

        /// <summary>
        /// Look up the ultimate design wind speed (mph) for a county and risk category.
        /// </summary>
        /// <param name="county">Florida county name (case-insensitive)</param>
        /// <param name="riskCategory">Risk category: "II", "III", or "IV"</param>
        /// <returns>Wind speed in mph, or 0 if not found</returns>
        public int GetWindSpeed(string county, string riskCategory = "II")
        {
            if (!_counties.TryGetValue(county, out var data))
                return 0;

            // Try direct RC key first
            string rcKey = $"Vult_RC{riskCategory}";
            if (data.SpeedsByRC.TryGetValue(rcKey, out int speed))
                return speed;

            // Fall back to RC II base speed with ratio adjustment
            int baseSpeed = data.Vult_RCII;
            string ratioKey = $"RC_{riskCategory}";
            if (_riskCategoryRatios.TryGetValue(ratioKey, out double ratio))
                return (int)Math.Ceiling(baseSpeed * ratio);

            return baseSpeed;
        }

        /// <summary>
        /// Get HVHZ (High Velocity Hurricane Zone) status for a county.
        /// </summary>
        public bool IsHVHZ(string county)
        {
            if (_counties.TryGetValue(county, out var data))
                return data.HVHZ;
            return false;
        }

        /// <summary>
        /// Get county data, or null if not found.
        /// </summary>
        public CountyWindData GetCountyData(string county)
        {
            _counties.TryGetValue(county, out var data);
            return data;
        }

        /// <summary>
        /// Get all loaded county names.
        /// </summary>
        public IEnumerable<string> GetCountyNames()
        {
            return _counties.Keys;
        }

        /// <summary>
        /// Interpolate Kz (velocity pressure exposure coefficient) for a given height and exposure category.
        /// Per ASCE 7-22 Table 26.10-1.
        /// </summary>
        /// <param name="heightFt">Height above ground in feet</param>
        /// <param name="exposureCategory">Exposure category: "B", "C", or "D"</param>
        /// <returns>Kz value (interpolated)</returns>
        public double GetKz(double heightFt, string exposureCategory = "C")
        {
            if (_kzTable.Count == 0)
                return 0.85; // Default to Exposure C at 0-15 ft

            exposureCategory = exposureCategory.ToUpper();

            // Clamp height to table range
            double minHeight = _kzTable.Keys.First();
            double maxHeight = _kzTable.Keys.Last();

            if (heightFt <= minHeight)
            {
                return _kzTable[minHeight].TryGetValue(exposureCategory, out double val) ? val : 0.85;
            }

            if (heightFt >= maxHeight)
            {
                return _kzTable[maxHeight].TryGetValue(exposureCategory, out double val) ? val : 1.0;
            }

            // Interpolate between two bracketing heights
            double lowerHeight = minHeight;
            double upperHeight = maxHeight;

            foreach (var h in _kzTable.Keys)
            {
                if (h <= heightFt)
                    lowerHeight = h;
                if (h >= heightFt)
                {
                    upperHeight = h;
                    break;
                }
            }

            if (Math.Abs(lowerHeight - upperHeight) < 0.001)
            {
                return _kzTable[lowerHeight].TryGetValue(exposureCategory, out double val) ? val : 0.85;
            }

            double lowerKz = _kzTable[lowerHeight].TryGetValue(exposureCategory, out double lk) ? lk : 0.85;
            double upperKz = _kzTable[upperHeight].TryGetValue(exposureCategory, out double uk) ? uk : 0.85;

            // Linear interpolation
            double fraction = (heightFt - lowerHeight) / (upperHeight - lowerHeight);
            return lowerKz + fraction * (upperKz - lowerKz);
        }

        /// <summary>
        /// Calculate velocity pressure qz at a given height.
        /// Formula: qz = 0.00256 * Kz * Kzt * Ke * V^2 (ASCE 7-22 Eq. 26.10-1)
        /// Note: Kd is NOT included here — it is applied in the pressure equations.
        /// </summary>
        /// <param name="heightFt">Height above ground in feet</param>
        /// <param name="windSpeedMph">Ultimate design wind speed V (mph)</param>
        /// <param name="exposureCategory">Exposure category: "B", "C", or "D"</param>
        /// <param name="kzt">Topographic factor (1.0 for flat terrain)</param>
        /// <param name="ke">Ground elevation factor (1.0 at sea level)</param>
        /// <returns>Velocity pressure qz in psf</returns>
        public double CalculateQz(double heightFt, int windSpeedMph, string exposureCategory = "C",
            double? kzt = null, double? ke = null)
        {
            double kz = GetKz(heightFt, exposureCategory);
            double kztVal = kzt ?? _velocityFactors.GetValueOrDefault("Kzt_flat_terrain", 1.0);
            double keVal = ke ?? _velocityFactors.GetValueOrDefault("Ke_sea_level", 1.0);

            return 0.00256 * kz * kztVal * keVal * windSpeedMph * windSpeedMph;
        }

        /// <summary>
        /// Calculate MWFRS design wind pressure on a surface.
        /// Formula: p = q * Kd * G * Cp (ASCE 7-22)
        /// </summary>
        /// <param name="qz">Velocity pressure at height z (psf)</param>
        /// <param name="surfaceType">Surface key from Cp_MWFRS table (e.g., "windward_wall", "leeward_wall")</param>
        /// <returns>Design pressure in psf</returns>
        public double CalculateMWFRS_Pressure(double qz, string surfaceType)
        {
            double kd = _velocityFactors.GetValueOrDefault("Kd_buildings", 0.85);
            double cp = _cpMWFRS.GetValueOrDefault(surfaceType, 0.8);

            return qz * kd * GUST_FACTOR_RIGID * cp;
        }

        /// <summary>
        /// Calculate C&amp;C design wind pressure.
        /// Formula: p = qz * Kd * [(GCp) - (GCpi)] (ASCE 7-22)
        /// Returns both positive and negative pressures (use the worst case).
        /// </summary>
        /// <param name="qz">Velocity pressure at element height (psf)</param>
        /// <param name="zoneKey">Zone key from GCp table (e.g., "zone_4_field", "zone_5_eave")</param>
        /// <param name="enclosureType">Enclosure classification: "enclosed", "partially_enclosed", "open"</param>
        /// <param name="isWall">True for wall C&amp;C, false for roof C&amp;C</param>
        /// <returns>Tuple of (positive pressure, negative pressure) in psf — negative is suction</returns>
        public (double positive, double negative) CalculateCC_Pressure(double qz, string zoneKey,
            string enclosureType = "enclosed", bool isWall = true)
        {
            double kd = _velocityFactors.GetValueOrDefault("Kd_buildings", 0.85);

            var gcpTable = isWall ? _gcpCCWalls : _gcpCCRoof;
            double gcpPos = 0, gcpNeg = 0;

            if (gcpTable.TryGetValue(zoneKey, out var gcp))
            {
                gcpPos = gcp.Positive;
                gcpNeg = gcp.Negative;
            }
            else
            {
                // Default to field zone
                var defaultKey = isWall ? "zone_4_field" : "zone_1_field";
                if (gcpTable.TryGetValue(defaultKey, out var defGcp))
                {
                    gcpPos = defGcp.Positive;
                    gcpNeg = defGcp.Negative;
                }
            }

            double gcpiPos = 0, gcpiNeg = 0;
            if (_gcpiTable.TryGetValue(enclosureType, out var gcpi))
            {
                gcpiPos = gcpi.Positive;
                gcpiNeg = gcpi.Negative;
            }

            // Worst-case positive: max external positive minus most negative internal
            double pPositive = qz * kd * (gcpPos - gcpiNeg);
            // Worst-case negative (suction): most negative external minus most positive internal
            double pNegative = qz * kd * (gcpNeg - gcpiPos);

            return (pPositive, pNegative);
        }

        /// <summary>
        /// Determine overall pass/fail for an element given its rated design pressure vs. calculated pressure.
        /// </summary>
        /// <param name="calculatedPressure_psf">Absolute value of calculated design pressure (psf)</param>
        /// <param name="ratedPressure_psf">Element's rated design pressure (psf), or 0 if unknown</param>
        /// <returns>"PASS" if rated >= calculated, "FAIL" if rated < calculated, "UNKNOWN" if no rating</returns>
        public string EvaluateCompliance(double calculatedPressure_psf, double ratedPressure_psf)
        {
            if (ratedPressure_psf <= 0)
                return "UNKNOWN";

            return ratedPressure_psf >= Math.Abs(calculatedPressure_psf) ? "PASS" : "FAIL";
        }

        /// <summary>
        /// Get all MWFRS surface pressure results for a given qh (velocity pressure at mean roof height).
        /// Returns a dictionary of surface name to pressure in psf.
        /// </summary>
        public Dictionary<string, double> GetAllMWFRS_Pressures(double qh)
        {
            var results = new Dictionary<string, double>();
            double kd = _velocityFactors.GetValueOrDefault("Kd_buildings", 0.85);

            foreach (var kvp in _cpMWFRS)
            {
                results[kvp.Key] = qh * kd * GUST_FACTOR_RIGID * kvp.Value;
            }

            return results;
        }

        /// <summary>
        /// List of compliance-related parameter names to check on elements.
        /// </summary>
        public static readonly string[] ComplianceParameterNames = new[]
        {
            "Wind Exposure",
            "Design Pressure",
            "Design Wind Pressure",
            "Risk Category",
            "County",
            "Jurisdiction",
            "Rated Pressure",
            "Impact Rating",
            "NOA Number",
            "FL Product Approval",
            "HVHZ Approved"
        };

        /// <summary>
        /// Subset of parameters that are critical (not just informational).
        /// </summary>
        public static readonly string[] CriticalParameterNames = new[]
        {
            "Design Pressure",
            "Design Wind Pressure",
            "Rated Pressure"
        };

        #endregion
    }

    /// <summary>
    /// Extension method for dictionary default value (mirrors .NET 6+ GetValueOrDefault for .NET 4.8).
    /// </summary>
    internal static class DictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue = default)
        {
            return dict.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }
}
