using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge.Services
{
    /// <summary>
    /// Lightweight license tier resolver for the compliance modules.
    /// No auth server required — tier is determined entirely by the presence
    /// and content of a license key in license.json or appsettings.json.
    ///
    /// Free tier:  blank or missing key → 100-element cap per scan
    /// Pro tier:   valid key starting with "PRO-" → unlimited elements
    /// Enterprise: valid key starting with "ENT-" → unlimited elements
    /// </summary>
    public static class ComplianceLicenseHelper
    {
        private const int FreeTierElementCap = 100;
        private static string _cachedTier = null;

        // ----------------------------------------------------------------
        // Public API
        // ----------------------------------------------------------------

        /// <summary>Returns "free", "pro", or "enterprise".</summary>
        public static string GetTier()
        {
            if (_cachedTier != null)
                return _cachedTier;

            string key = ReadLicenseKey();
            _cachedTier = ResolveTier(key);

            Log.Debug("ComplianceLicenseHelper: tier={Tier}", _cachedTier);
            return _cachedTier;
        }

        /// <summary>Returns 100 for free tier, int.MaxValue for paid tiers.</summary>
        public static int GetElementCap()
        {
            return GetTier() == "free" ? FreeTierElementCap : int.MaxValue;
        }

        /// <summary>
        /// Returns an upgrade note string suitable for inclusion in a JSON response
        /// when the element cap is exceeded.  Returns null for paid tiers.
        /// </summary>
        public static string GetUpgradeNote(int totalElements)
        {
            if (GetTier() != "free")
                return null;

            int skipped = totalElements - FreeTierElementCap;
            return $"{skipped} element(s) not scanned (free tier cap: {FreeTierElementCap}). " +
                   "Pro tier: unlimited elements, PDF report, full compliance summary. " +
                   "$79/seat/year — contact weber@bimopsstudio.com";
        }

        // ----------------------------------------------------------------
        // Internal helpers
        // ----------------------------------------------------------------

        private static string ResolveTier(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "free";

            string upper = key.Trim().ToUpperInvariant();

            if (upper.StartsWith("ENT-"))
                return "enterprise";

            if (upper.StartsWith("PRO-"))
                return "pro";

            // Unrecognized key format — degrade to free rather than crash
            Log.Warning("ComplianceLicenseHelper: unrecognized key format, defaulting to free tier.");
            return "free";
        }

        /// <summary>
        /// Read the license key from data/license.json first, then fall back
        /// to the Compliance.LicenseKey field in appsettings.json.
        /// Returns empty string if neither source has a key.
        /// </summary>
        private static string ReadLicenseKey()
        {
            // Primary: data/license.json (sits next to the DLL's data folder)
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var licenseJsonPath = Path.Combine(assemblyDir, "data", "license.json");

                if (File.Exists(licenseJsonPath))
                {
                    var obj = JObject.Parse(File.ReadAllText(licenseJsonPath));
                    var key = obj["license_key"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                        return key;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComplianceLicenseHelper: could not read data/license.json");
            }

            // Fallback: appsettings.json Compliance.LicenseKey
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var settingsPath = Path.Combine(assemblyDir, "appsettings.json");

                if (File.Exists(settingsPath))
                {
                    var settings = JObject.Parse(File.ReadAllText(settingsPath));
                    var key = settings.SelectToken("Compliance.LicenseKey")?.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                        return key;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComplianceLicenseHelper: could not read appsettings.json");
            }

            return string.Empty;
        }
    }
}
