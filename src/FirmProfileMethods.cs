using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Firm Profile Methods - Universal template system with switchable firm-specific standards.
    ///
    /// Loads profiles from: knowledge/standards/firm-profiles/
    /// Auto-detects firm based on project info, titleblock, or path patterns.
    /// Applies firm-specific naming conventions, text styles, and sheet numbering.
    ///
    /// Methods (10):
    /// - listProfiles: List all available firm profiles
    /// - getProfile: Get details of a specific profile
    /// - getCurrentProfile: Get the currently active profile
    /// - setProfile: Manually set the active profile
    /// - detectProfile: Auto-detect profile from project info
    /// - getProfileSetting: Get a specific setting from current profile
    /// - getTextTypeForProfile: Get the correct text type name for current firm
    /// - getDimensionTypeForProfile: Get the correct dimension type for current firm
    /// - getSheetNumberFormat: Get the sheet number format/pattern
    /// - getViewNamingConvention: Get view naming rules
    /// </summary>
    public static class FirmProfileMethods
    {
        #region Profile Storage

        /// <summary>
        /// Path to profile folder (relative to knowledge base)
        /// </summary>
        private static readonly string ProfileFolderName = "knowledge/standards/firm-profiles";

        /// <summary>
        /// Cache of loaded profiles
        /// </summary>
        private static Dictionary<string, JObject> _profileCache = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Profile index (loaded from _profile-index.json)
        /// </summary>
        private static JObject _profileIndex;

        /// <summary>
        /// Universal core defaults
        /// </summary>
        private static JObject _universalCore;

        /// <summary>
        /// Currently active profile ID
        /// </summary>
        private static string _currentProfileId = null;

        /// <summary>
        /// Flag to track if profiles have been loaded
        /// </summary>
        private static bool _profilesLoaded = false;

        #endregion

        #region Initialization

        /// <summary>
        /// Get the profile folder path by looking for CLAUDE.md and going from there
        /// </summary>
        private static string GetProfileFolderPath()
        {
            // Try multiple possible locations
            var possiblePaths = new[]
            {
                @"D:\RevitMCPBridge2026\knowledge\standards\firm-profiles",
                @"C:\RevitMCPBridge2026\knowledge\standards\firm-profiles",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    @"RevitMCPBridge2026\knowledge\standards\firm-profiles")
            };

            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    Log.Information($"[FirmProfile] Found profile folder at: {path}");
                    return path;
                }
            }

            Log.Warning("[FirmProfile] Profile folder not found, using default path");
            return possiblePaths[0]; // Default to D: drive
        }

        /// <summary>
        /// Load all profiles from the file system
        /// </summary>
        private static void EnsureProfilesLoaded()
        {
            if (_profilesLoaded) return;

            try
            {
                var folderPath = GetProfileFolderPath();

                // Load profile index
                var indexPath = Path.Combine(folderPath, "_profile-index.json");
                if (File.Exists(indexPath))
                {
                    var indexJson = File.ReadAllText(indexPath);
                    _profileIndex = JObject.Parse(indexJson);
                    Log.Information($"[FirmProfile] Loaded profile index with {_profileIndex["profiles"]?.Count() ?? 0} profiles");
                }
                else
                {
                    Log.Warning($"[FirmProfile] Profile index not found at {indexPath}");
                    _profileIndex = new JObject();
                }

                // Load universal core
                var corePath = Path.Combine(folderPath, "_universal-core.json");
                if (File.Exists(corePath))
                {
                    var coreJson = File.ReadAllText(corePath);
                    _universalCore = JObject.Parse(coreJson);
                    Log.Information("[FirmProfile] Loaded universal core standards");
                }
                else
                {
                    _universalCore = new JObject();
                }

                // Load all profile files
                var profilesSection = _profileIndex["profiles"] as JObject;
                if (profilesSection != null)
                {
                    foreach (var profile in profilesSection.Properties())
                    {
                        var profileId = profile.Name;
                        var profileInfo = profile.Value as JObject;
                        var fileName = profileInfo?["file"]?.ToString();

                        if (!string.IsNullOrEmpty(fileName))
                        {
                            var profilePath = Path.Combine(folderPath, fileName);
                            if (File.Exists(profilePath))
                            {
                                var profileJson = File.ReadAllText(profilePath);
                                _profileCache[profileId] = JObject.Parse(profileJson);
                                Log.Information($"[FirmProfile] Loaded profile: {profileId}");
                            }
                        }
                    }
                }

                _profilesLoaded = true;
                Log.Information($"[FirmProfile] Profile system initialized with {_profileCache.Count} profiles");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FirmProfile] Failed to load profiles");
                _profilesLoaded = true; // Prevent repeated failed attempts
            }
        }

        #endregion

        #region MCP Methods

        /// <summary>
        /// List all available firm profiles
        /// </summary>
        public static string ListProfiles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var profiles = new List<object>();
                var profilesSection = _profileIndex?["profiles"] as JObject;

                if (profilesSection != null)
                {
                    foreach (var profile in profilesSection.Properties())
                    {
                        var profileId = profile.Name;
                        var profileInfo = profile.Value as JObject;
                        var profileData = _profileCache.ContainsKey(profileId) ? _profileCache[profileId] : null;

                        profiles.Add(new
                        {
                            id = profileId,
                            firmName = profileInfo?["firmName"]?.ToString() ?? profileData?["firmName"]?.ToString(),
                            status = profileInfo?["status"]?.ToString() ?? "unknown",
                            projectTypes = profileInfo?["projectTypes"]?.ToObject<string[]>(),
                            region = profileInfo?["region"]?.ToString(),
                            isActive = profileId.Equals(_currentProfileId, StringComparison.OrdinalIgnoreCase),
                            extractedFrom = profileInfo?["extractedFrom"]?.ToString()
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profiles = profiles,
                    count = profiles.Count,
                    currentProfile = _currentProfileId,
                    defaultProfile = _profileIndex?["defaultProfile"]?.ToString()
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get details of a specific profile
        /// </summary>
        public static string GetProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var profileId = parameters?["profileId"]?.ToString();
                if (string.IsNullOrEmpty(profileId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "profileId parameter is required"
                    });
                }

                if (!_profileCache.ContainsKey(profileId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Profile '{profileId}' not found",
                        availableProfiles = _profileCache.Keys.ToList()
                    });
                }

                var profile = _profileCache[profileId];

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profile = profile,
                    isActive = profileId.Equals(_currentProfileId, StringComparison.OrdinalIgnoreCase)
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the currently active profile (or auto-detect if none set)
        /// </summary>
        public static string GetCurrentProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                // If no profile set, try to auto-detect
                if (string.IsNullOrEmpty(_currentProfileId))
                {
                    var detectResult = DetectProfileInternal(uiApp);
                    if (detectResult.success)
                    {
                        _currentProfileId = detectResult.profileId;
                    }
                    else
                    {
                        // Fall back to default
                        _currentProfileId = _profileIndex?["defaultProfile"]?.ToString() ?? "arky";
                    }
                }

                if (!_profileCache.ContainsKey(_currentProfileId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        profileId = _currentProfileId,
                        profile = (object)null,
                        message = "Profile not found in cache - using default settings"
                    });
                }

                var profile = _profileCache[_currentProfileId];

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profileId = _currentProfileId,
                    firmName = profile["firmName"]?.ToString(),
                    profile = profile
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set the active profile manually
        /// </summary>
        public static string SetProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var profileId = parameters?["profileId"]?.ToString();
                if (string.IsNullOrEmpty(profileId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "profileId parameter is required",
                        availableProfiles = _profileCache.Keys.ToList()
                    });
                }

                if (!_profileCache.ContainsKey(profileId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Profile '{profileId}' not found",
                        availableProfiles = _profileCache.Keys.ToList()
                    });
                }

                var previousProfile = _currentProfileId;
                _currentProfileId = profileId;

                var profile = _profileCache[profileId];

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Profile switched from '{previousProfile}' to '{profileId}'",
                    profileId = profileId,
                    firmName = profile["firmName"]?.ToString(),
                    sheetPattern = profile["sheetNumbering"]?["pattern"]?.ToString(),
                    textTypesCount = (profile["textTypes"] as JObject)?.Count ?? 0
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Auto-detect the appropriate profile based on project info
        /// </summary>
        public static string DetectProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var result = DetectProfileInternal(uiApp);

                if (result.success)
                {
                    // Optionally auto-set the profile
                    var autoSet = parameters?["autoSet"]?.Value<bool>() ?? false;
                    if (autoSet)
                    {
                        _currentProfileId = result.profileId;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = result.success,
                    detectedProfile = result.profileId,
                    firmName = result.firmName,
                    confidence = result.confidence,
                    matchedOn = result.matchedOn,
                    isNowActive = (result.profileId == _currentProfileId),
                    message = result.success
                        ? $"Detected profile: {result.firmName}"
                        : "Could not detect profile - using default"
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get a specific setting from the current profile
        /// </summary>
        public static string GetProfileSetting(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var settingPath = parameters?["setting"]?.ToString();
                if (string.IsNullOrEmpty(settingPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "setting parameter is required (e.g., 'textTypes.primary.notes')"
                    });
                }

                // Ensure we have a current profile
                if (string.IsNullOrEmpty(_currentProfileId) || !_profileCache.ContainsKey(_currentProfileId))
                {
                    GetCurrentProfile(uiApp, null); // Will auto-detect or use default
                }

                var profile = _profileCache.ContainsKey(_currentProfileId) ? _profileCache[_currentProfileId] : null;

                // Navigate to the setting using dot notation
                JToken value = profile;
                foreach (var part in settingPath.Split('.'))
                {
                    if (value == null) break;
                    value = value[part];
                }

                // If not found in profile, check universal core
                if (value == null && _universalCore != null)
                {
                    value = _universalCore;
                    foreach (var part in settingPath.Split('.'))
                    {
                        if (value == null) break;
                        value = value[part];
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profileId = _currentProfileId,
                    setting = settingPath,
                    value = value,
                    source = value != null ? (profile?[settingPath.Split('.')[0]] != null ? "profile" : "universalCore") : "not_found"
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the correct text type name for the current firm
        /// </summary>
        public static string GetTextTypeForProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var textCategory = parameters?["category"]?.ToString() ?? "notes";

                if (string.IsNullOrEmpty(_currentProfileId) || !_profileCache.ContainsKey(_currentProfileId))
                {
                    GetCurrentProfile(uiApp, null);
                }

                var profile = _profileCache.ContainsKey(_currentProfileId) ? _profileCache[_currentProfileId] : null;

                // Navigate: textTypes -> primary -> category
                var textTypes = profile?["textTypes"]?["primary"] as JObject;
                var textTypeInfo = textTypes?[textCategory];

                string styleName = textTypeInfo?["styleName"]?.ToString();
                string font = textTypeInfo?["font"]?.ToString();
                string size = textTypeInfo?["size"]?.ToString();

                // Fall back to universal core
                if (string.IsNullOrEmpty(styleName))
                {
                    var coreDefaults = _universalCore?["textDefaults"];
                    size = coreDefaults?[textCategory + "Size"]?.ToString() ?? "3/32\"";
                    font = coreDefaults?["defaultFont"]?.ToString() ?? "Arial";
                    styleName = $"{size} {font}";
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profileId = _currentProfileId,
                    category = textCategory,
                    styleName = styleName,
                    font = font,
                    size = size
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the correct dimension type for the current firm
        /// </summary>
        public static string GetDimensionTypeForProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                var dimCategory = parameters?["category"]?.ToString() ?? "default";

                if (string.IsNullOrEmpty(_currentProfileId) || !_profileCache.ContainsKey(_currentProfileId))
                {
                    GetCurrentProfile(uiApp, null);
                }

                var profile = _profileCache.ContainsKey(_currentProfileId) ? _profileCache[_currentProfileId] : null;

                var dimTypes = profile?["dimensionTypes"] as JObject;
                string dimTypeName = dimTypes?[dimCategory]?.ToString();

                // Fall back to default
                if (string.IsNullOrEmpty(dimTypeName))
                {
                    dimTypeName = dimTypes?["default"]?.ToString() ?? "Linear - 3/32\" Arial";
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profileId = _currentProfileId,
                    category = dimCategory,
                    dimensionType = dimTypeName
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the sheet number format/pattern for the current firm
        /// </summary>
        public static string GetSheetNumberFormat(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                if (string.IsNullOrEmpty(_currentProfileId) || !_profileCache.ContainsKey(_currentProfileId))
                {
                    GetCurrentProfile(uiApp, null);
                }

                var profile = _profileCache.ContainsKey(_currentProfileId) ? _profileCache[_currentProfileId] : null;

                var sheetNumbering = profile?["sheetNumbering"] as JObject;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profileId = _currentProfileId,
                    pattern = sheetNumbering?["pattern"]?.ToString(),
                    examples = sheetNumbering?["examples"]?.ToObject<string[]>(),
                    disciplines = sheetNumbering?["disciplines"],
                    categories = sheetNumbering?["categories"]
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get view naming rules for the current firm
        /// </summary>
        public static string GetViewNamingConvention(UIApplication uiApp, JObject parameters)
        {
            try
            {
                EnsureProfilesLoaded();

                if (string.IsNullOrEmpty(_currentProfileId) || !_profileCache.ContainsKey(_currentProfileId))
                {
                    GetCurrentProfile(uiApp, null);
                }

                var profile = _profileCache.ContainsKey(_currentProfileId) ? _profileCache[_currentProfileId] : null;

                var viewNaming = profile?["viewNaming"] as JObject;
                var viewTemplates = profile?["viewTemplates"] as JObject;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profileId = _currentProfileId,
                    prefix = viewNaming?["prefix"]?.ToString(),
                    floorPlanFormat = viewNaming?["floorPlanFormat"]?.ToString(),
                    elevationFormat = viewNaming?["elevationFormat"]?.ToString(),
                    examples = viewNaming?["examples"]?.ToObject<string[]>(),
                    viewTemplates = viewTemplates
                }, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Internal Detection Logic

        private class DetectionResult
        {
            public bool success;
            public string profileId;
            public string firmName;
            public string confidence;
            public string matchedOn;
        }

        /// <summary>
        /// Internal profile detection logic
        /// </summary>
        private static DetectionResult DetectProfileInternal(UIApplication uiApp)
        {
            var result = new DetectionResult { success = false };

            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return result;
                }

                // Get project info
                var projectInfo = doc.ProjectInformation;
                var projectName = projectInfo?.Name ?? "";
                var author = projectInfo?.Author ?? "";
                var clientName = projectInfo?.ClientName ?? "";
                var organizationName = projectInfo?.OrganizationName ?? "";
                var filePath = doc.PathName ?? "";

                Log.Information($"[FirmProfile] Detecting profile - Project: {projectName}, Author: {author}, Client: {clientName}, Path: {filePath}");

                // Check each profile's detection patterns in priority order
                var detectionPriority = _profileIndex?["detectionPriority"]?.ToObject<string[]>() ?? _profileCache.Keys.ToArray();

                foreach (var profileId in detectionPriority)
                {
                    if (!_profileCache.ContainsKey(profileId)) continue;

                    var profile = _profileCache[profileId];
                    var detection = profile["detection"] as JObject;
                    if (detection == null) continue;

                    // Check path patterns
                    var pathPatterns = detection["pathPatterns"]?.ToObject<string[]>();
                    if (pathPatterns != null)
                    {
                        foreach (var pattern in pathPatterns)
                        {
                            if (filePath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                result.success = true;
                                result.profileId = profileId;
                                result.firmName = profile["firmName"]?.ToString();
                                result.confidence = "high";
                                result.matchedOn = $"path pattern: {pattern}";
                                return result;
                            }
                        }
                    }

                    // Check titleblock families
                    var titleBlockFamilies = detection["titleBlockFamilies"]?.ToObject<string[]>();
                    if (titleBlockFamilies != null)
                    {
                        var sheets = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>()
                            .Take(5)
                            .ToList();

                        foreach (var sheet in sheets)
                        {
                            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                                .FirstElement();

                            if (titleBlock != null)
                            {
                                var familyName = titleBlock.Name;
                                var familyType = doc.GetElement(titleBlock.GetTypeId());
                                var typeName = familyType?.Name ?? "";

                                foreach (var tbPattern in titleBlockFamilies)
                                {
                                    if (familyName.IndexOf(tbPattern, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        typeName.IndexOf(tbPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        result.success = true;
                                        result.profileId = profileId;
                                        result.firmName = profile["firmName"]?.ToString();
                                        result.confidence = "high";
                                        result.matchedOn = $"titleblock: {tbPattern}";
                                        return result;
                                    }
                                }
                            }
                        }
                    }

                    // Check project info patterns
                    var projectInfoPatterns = detection["projectInfoPatterns"] as JObject;
                    if (projectInfoPatterns != null)
                    {
                        foreach (var pattern in projectInfoPatterns.Properties())
                        {
                            var patternValue = pattern.Value?.ToString();
                            if (string.IsNullOrEmpty(patternValue)) continue;

                            string valueToCheck = "";
                            switch (pattern.Name.ToLower())
                            {
                                case "author": valueToCheck = author; break;
                                case "clientname": valueToCheck = clientName; break;
                                case "organizationname": valueToCheck = organizationName; break;
                            }

                            if (!string.IsNullOrEmpty(valueToCheck) &&
                                valueToCheck.IndexOf(patternValue, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                result.success = true;
                                result.profileId = profileId;
                                result.firmName = profile["firmName"]?.ToString();
                                result.confidence = "medium";
                                result.matchedOn = $"projectInfo.{pattern.Name}: {patternValue}";
                                return result;
                            }
                        }
                    }
                }

                // No match found - use default
                var defaultProfile = _profileIndex?["defaultProfile"]?.ToString() ?? "arky";
                if (_profileCache.ContainsKey(defaultProfile))
                {
                    result.success = true;
                    result.profileId = defaultProfile;
                    result.firmName = _profileCache[defaultProfile]["firmName"]?.ToString();
                    result.confidence = "default";
                    result.matchedOn = "no match - using default";
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FirmProfile] Detection failed");
                return result;
            }
        }

        #endregion

        #region Utility Methods for Other Services

        /// <summary>
        /// Get a setting value directly (for use by other MCP methods)
        /// </summary>
        public static JToken GetSettingDirect(string settingPath)
        {
            EnsureProfilesLoaded();

            if (string.IsNullOrEmpty(_currentProfileId) || !_profileCache.ContainsKey(_currentProfileId))
            {
                _currentProfileId = _profileIndex?["defaultProfile"]?.ToString() ?? "arky";
            }

            var profile = _profileCache.ContainsKey(_currentProfileId) ? _profileCache[_currentProfileId] : null;

            JToken value = profile;
            foreach (var part in settingPath.Split('.'))
            {
                if (value == null) break;
                value = value[part];
            }

            // Fall back to universal core
            if (value == null && _universalCore != null)
            {
                value = _universalCore;
                foreach (var part in settingPath.Split('.'))
                {
                    if (value == null) break;
                    value = value[part];
                }
            }

            return value;
        }

        /// <summary>
        /// Get current profile ID directly
        /// </summary>
        public static string GetCurrentProfileId()
        {
            EnsureProfilesLoaded();
            return _currentProfileId ?? _profileIndex?["defaultProfile"]?.ToString() ?? "arky";
        }

        /// <summary>
        /// Refresh profiles from disk (call after editing profile files)
        /// </summary>
        public static string RefreshProfiles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                _profilesLoaded = false;
                _profileCache.Clear();
                _profileIndex = null;
                _universalCore = null;

                EnsureProfilesLoaded();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Profiles refreshed from disk",
                    profileCount = _profileCache.Count,
                    profiles = _profileCache.Keys.ToList()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
