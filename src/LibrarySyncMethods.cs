using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Methods for scanning projects, comparing against library, and extracting new content.
    /// Supports multi-firm library management.
    /// </summary>
    public static class LibrarySyncMethods
    {
        private static readonly string DefaultLibraryPath = @"D:\Revit Detail Libraries";
        private static readonly string FirmLibrariesFolder = "Firm Libraries";
        private static readonly string FirmProfilesFile = "firm_profiles.json";
        private static readonly string IndexFileName = "library_index.json";

        #region Scanning Methods

        /// <summary>
        /// Scans the current project and catalogs all library-compatible content:
        /// - Loaded families (by category)
        /// - Drafting views
        /// - Legends
        /// - Schedules
        /// - Detail components used
        /// Returns a complete inventory of the project's reusable content.
        ///
        /// Parameters:
        /// - quickScan: If true, skips instance counting for faster results (default: true)
        /// - limit: Maximum items per category (default: 500, 0 = unlimited)
        /// - offset: Skip first N items (for pagination)
        /// - scanType: "all" | "families" | "views" | "legends" | "schedules" (default: "all")
        /// </summary>
        public static string ScanProjectContent(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var includeSystemFamilies = parameters?["includeSystemFamilies"]?.Value<bool>() ?? false;
                var includePlacedOnly = parameters?["includePlacedOnly"]?.Value<bool>() ?? false;
                var quickScan = parameters?["quickScan"]?.Value<bool>() ?? true; // Default to quick mode
                var limit = parameters?["limit"]?.Value<int>() ?? 500; // Default limit per category
                var offset = parameters?["offset"]?.Value<int>() ?? 0;
                var scanType = parameters?["scanType"]?.ToString() ?? "all";

                var result = new ProjectContentScan
                {
                    ProjectName = doc.Title,
                    ProjectNumber = GetProjectNumber(doc),
                    ClientName = GetClientName(doc),
                    ScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // Scan Families
                if (scanType == "all" || scanType == "families")
                {
                    var families = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .Where(f => !f.IsInPlace) // Exclude in-place families
                        .ToList();

                    if (!includeSystemFamilies)
                    {
                        families = families.Where(f => !IsSystemFamily(f)).ToList();
                    }

                    // Store total count before pagination
                    result.TotalFamilies = families.Count;

                    // Apply pagination
                    if (offset > 0) families = families.Skip(offset).ToList();
                    if (limit > 0) families = families.Take(limit).ToList();

                    // Pre-fetch all family symbols once for performance
                    var allSymbols = quickScan ? null : new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .GroupBy(fs => fs.Family.Id)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    foreach (var family in families)
                    {
                        var categoryName = family.FamilyCategory?.Name ?? "Unknown";
                        var typeCount = 0;
                        var instanceCount = 0;

                        if (!quickScan)
                        {
                            // Get types count from pre-fetched data
                            if (allSymbols != null && allSymbols.TryGetValue(family.Id, out var types))
                            {
                                typeCount = types.Count;

                                // Check if any instances are placed (only if required)
                                if (includePlacedOnly)
                                {
                                    foreach (var type in types)
                                    {
                                        instanceCount += new FilteredElementCollector(doc)
                                            .OfClass(typeof(FamilyInstance))
                                            .Cast<FamilyInstance>()
                                            .Count(fi => fi.Symbol.Id == type.Id);
                                    }
                                    if (instanceCount == 0) continue;
                                }
                            }
                        }

                        result.Families.Add(new ScannedItem
                        {
                            Name = family.Name,
                            Category = categoryName,
                            TypeCount = typeCount,
                            InstanceCount = instanceCount,
                            Id = family.Id.Value,
                            ContentHash = ComputeFamilyHash(family.Name, categoryName)
                        });
                    }
                }

                // Scan Drafting Views
                if (scanType == "all" || scanType == "views")
                {
                    var draftingViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .Where(v => !v.IsTemplate)
                        .ToList();

                    result.TotalDraftingViews = draftingViews.Count;

                    // Apply pagination
                    if (offset > 0) draftingViews = draftingViews.Skip(offset).ToList();
                    if (limit > 0) draftingViews = draftingViews.Take(limit).ToList();

                    foreach (var view in draftingViews)
                    {
                        // Count elements in the view (skip if quickScan for performance)
                        var elementCount = quickScan ? 0 : new FilteredElementCollector(doc, view.Id)
                            .WhereElementIsNotElementType()
                            .GetElementCount();

                        result.DraftingViews.Add(new ScannedItem
                        {
                            Name = view.Name,
                            Category = "Drafting View",
                            Id = view.Id.Value,
                            ElementCount = elementCount,
                            Scale = view.Scale,
                            ContentHash = ComputeViewHash(view.Name, "DraftingView")
                        });
                    }
                }

                // Scan Legends
                if (scanType == "all" || scanType == "legends")
                {
                    var legends = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate)
                        .ToList();

                    result.TotalLegends = legends.Count;

                    // Apply pagination
                    if (offset > 0) legends = legends.Skip(offset).ToList();
                    if (limit > 0) legends = legends.Take(limit).ToList();

                    foreach (var legend in legends)
                    {
                        var elementCount = quickScan ? 0 : new FilteredElementCollector(doc, legend.Id)
                            .WhereElementIsNotElementType()
                            .GetElementCount();

                        result.Legends.Add(new ScannedItem
                        {
                            Name = legend.Name,
                            Category = "Legend",
                            Id = legend.Id.Value,
                            ElementCount = elementCount,
                            Scale = legend.Scale,
                            ContentHash = ComputeViewHash(legend.Name, "Legend")
                        });
                    }
                }

                // Scan Schedules
                if (scanType == "all" || scanType == "schedules")
                {
                    var schedules = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSchedule))
                        .Cast<ViewSchedule>()
                        .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
                        .ToList();

                    result.TotalSchedules = schedules.Count;

                    // Apply pagination
                    if (offset > 0) schedules = schedules.Skip(offset).ToList();
                    if (limit > 0) schedules = schedules.Take(limit).ToList();

                    foreach (var schedule in schedules)
                    {
                        var schedDef = schedule.Definition;
                        var fieldCount = quickScan ? 0 : (schedDef?.GetFieldCount() ?? 0);

                        // Determine schedule category
                        var schedCategory = "Other Schedules";
                        var scheduleName = schedule.Name.ToLower();
                        if (scheduleName.Contains("door")) schedCategory = "Door Schedules";
                        else if (scheduleName.Contains("window")) schedCategory = "Window Schedules";
                        else if (scheduleName.Contains("room")) schedCategory = "Room Schedules";
                        else if (scheduleName.Contains("sheet")) schedCategory = "Sheet Schedules";
                        else if (scheduleName.Contains("floor")) schedCategory = "Floor Schedules";
                        else if (scheduleName.Contains("ceiling")) schedCategory = "Ceiling Schedules";
                        else if (scheduleName.Contains("equipment")) schedCategory = "Equipment Schedules";
                        else if (scheduleName.Contains("plumbing") || scheduleName.Contains("fixture")) schedCategory = "Plumbing Schedules";
                        else if (scheduleName.Contains("area")) schedCategory = "Area Schedules";

                        result.Schedules.Add(new ScannedItem
                        {
                            Name = schedule.Name,
                            Category = schedCategory,
                            Id = schedule.Id.Value,
                            FieldCount = fieldCount,
                            ContentHash = ComputeViewHash(schedule.Name, "Schedule")
                        });
                    }
                }

                // Calculate totals
                result.TotalItems = result.TotalFamilies + result.TotalDraftingViews +
                                   result.TotalLegends + result.TotalSchedules;

                // Get unique categories
                result.FamilyCategories = result.Families
                    .Select(f => f.Category)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                // Calculate returned counts for pagination info
                var returnedFamilies = result.Families.Count;
                var returnedViews = result.DraftingViews.Count;
                var returnedLegends = result.Legends.Count;
                var returnedSchedules = result.Schedules.Count;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    quickScan = quickScan,
                    pagination = new
                    {
                        offset = offset,
                        limit = limit,
                        scanType = scanType,
                        returnedFamilies = returnedFamilies,
                        returnedViews = returnedViews,
                        returnedLegends = returnedLegends,
                        returnedSchedules = returnedSchedules,
                        hasMore = (returnedFamilies < result.TotalFamilies - offset) ||
                                 (returnedViews < result.TotalDraftingViews - offset) ||
                                 (returnedLegends < result.TotalLegends - offset) ||
                                 (returnedSchedules < result.TotalSchedules - offset)
                    },
                    scan = result
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Comparison Methods

        /// <summary>
        /// Compares the current project's content against the library.
        /// Returns:
        /// - NEW: Items in project but not in library (candidates for extraction)
        /// - MATCHING: Items that exist in both
        /// - AVAILABLE: Items in library but not in project (could be loaded)
        /// </summary>
        public static string CompareProjectToLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;
                var firmName = parameters?["firmName"]?.ToString();
                var compareType = parameters?["type"]?.ToString(); // families, drafting_views, legends, schedules, or null for all

                // First, scan the project
                var scanResult = ScanProjectContent(uiApp, parameters);
                var scanObj = JObject.Parse(scanResult);

                if (scanObj["success"]?.Value<bool>() != true)
                {
                    return scanResult; // Return scan error
                }

                var projectScan = scanObj["scan"] as JObject;

                // Load the library index
                var indexPath = Path.Combine(libraryPath, IndexFileName);
                if (!File.Exists(indexPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Library index not found at {indexPath}. Build index first."
                    });
                }

                var indexJson = File.ReadAllText(indexPath);
                var libraryIndex = JObject.Parse(indexJson);
                var libraryCategories = libraryIndex["categories"] as JObject;

                // Build lookup sets from library
                var libraryFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var libraryDraftingViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var libraryLegends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var librarySchedules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Also store full items for "available" reporting
                var libraryFamilyItems = new List<JObject>();
                var libraryDraftingViewItems = new List<JObject>();
                var libraryLegendItems = new List<JObject>();
                var libraryScheduleItems = new List<JObject>();

                if (libraryCategories != null)
                {
                    // Families
                    var famCat = libraryCategories["families"] as JObject;
                    if (famCat != null)
                    {
                        var items = famCat["items"] as JArray;
                        if (items != null)
                        {
                            foreach (JObject item in items)
                            {
                                var name = item["name"]?.ToString();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    libraryFamilies.Add(name);
                                    libraryFamilyItems.Add(item);
                                }
                            }
                        }
                    }

                    // Drafting Views
                    var dvCat = libraryCategories["drafting_views"] as JObject;
                    if (dvCat != null)
                    {
                        var items = dvCat["items"] as JArray;
                        if (items != null)
                        {
                            foreach (JObject item in items)
                            {
                                var name = item["name"]?.ToString();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    libraryDraftingViews.Add(name);
                                    libraryDraftingViewItems.Add(item);
                                }
                            }
                        }
                    }

                    // Legends
                    var legCat = libraryCategories["legends"] as JObject;
                    if (legCat != null)
                    {
                        var items = legCat["items"] as JArray;
                        if (items != null)
                        {
                            foreach (JObject item in items)
                            {
                                var name = item["name"]?.ToString();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    libraryLegends.Add(name);
                                    libraryLegendItems.Add(item);
                                }
                            }
                        }
                    }

                    // Schedules
                    var schCat = libraryCategories["schedules"] as JObject;
                    if (schCat != null)
                    {
                        var items = schCat["items"] as JArray;
                        if (items != null)
                        {
                            foreach (JObject item in items)
                            {
                                var name = item["name"]?.ToString();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    librarySchedules.Add(name);
                                    libraryScheduleItems.Add(item);
                                }
                            }
                        }
                    }
                }

                // Compare and categorize
                var comparison = new ComparisonResult
                {
                    ProjectName = projectScan["ProjectName"]?.ToString(),
                    LibraryPath = libraryPath,
                    ComparedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // Compare Families
                var projectFamilies = projectScan["Families"] as JArray ?? new JArray();
                foreach (JObject pf in projectFamilies)
                {
                    var name = pf["name"]?.ToString();
                    if (libraryFamilies.Contains(name))
                    {
                        comparison.MatchingFamilies.Add(pf);
                    }
                    else
                    {
                        comparison.NewFamilies.Add(pf);
                    }
                }

                // Compare Drafting Views
                var projectDraftingViews = projectScan["DraftingViews"] as JArray ?? new JArray();
                foreach (JObject pdv in projectDraftingViews)
                {
                    var name = pdv["name"]?.ToString();
                    if (libraryDraftingViews.Contains(name))
                    {
                        comparison.MatchingDraftingViews.Add(pdv);
                    }
                    else
                    {
                        comparison.NewDraftingViews.Add(pdv);
                    }
                }

                // Compare Legends
                var projectLegends = projectScan["Legends"] as JArray ?? new JArray();
                foreach (JObject pl in projectLegends)
                {
                    var name = pl["name"]?.ToString();
                    if (libraryLegends.Contains(name))
                    {
                        comparison.MatchingLegends.Add(pl);
                    }
                    else
                    {
                        comparison.NewLegends.Add(pl);
                    }
                }

                // Compare Schedules
                var projectSchedules = projectScan["Schedules"] as JArray ?? new JArray();
                foreach (JObject ps in projectSchedules)
                {
                    var name = ps["name"]?.ToString();
                    if (librarySchedules.Contains(name))
                    {
                        comparison.MatchingSchedules.Add(ps);
                    }
                    else
                    {
                        comparison.NewSchedules.Add(ps);
                    }
                }

                // Find available items (in library but not in project)
                var projectFamilyNames = new HashSet<string>(
                    projectFamilies.Select(f => f["name"]?.ToString()),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var lf in libraryFamilyItems)
                {
                    var name = lf["name"]?.ToString();
                    if (!projectFamilyNames.Contains(name))
                    {
                        comparison.AvailableFamilies.Add(lf);
                    }
                }

                // Calculate summary
                comparison.Summary = new ComparisonSummary
                {
                    TotalProjectItems = projectFamilies.Count + projectDraftingViews.Count +
                                       projectLegends.Count + projectSchedules.Count,
                    TotalLibraryItems = libraryFamilies.Count + libraryDraftingViews.Count +
                                       libraryLegends.Count + librarySchedules.Count,
                    NewItemsCount = comparison.NewFamilies.Count + comparison.NewDraftingViews.Count +
                                   comparison.NewLegends.Count + comparison.NewSchedules.Count,
                    MatchingItemsCount = comparison.MatchingFamilies.Count + comparison.MatchingDraftingViews.Count +
                                        comparison.MatchingLegends.Count + comparison.MatchingSchedules.Count,
                    AvailableItemsCount = comparison.AvailableFamilies.Count,

                    NewFamiliesCount = comparison.NewFamilies.Count,
                    NewDraftingViewsCount = comparison.NewDraftingViews.Count,
                    NewLegendsCount = comparison.NewLegends.Count,
                    NewSchedulesCount = comparison.NewSchedules.Count
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    comparison = comparison
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Extraction Methods

        /// <summary>
        /// Extracts new items from the current project to the library.
        /// Can extract to Master library or a firm-specific library.
        /// </summary>
        public static string ExtractNewToLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;
                var firmName = parameters?["firmName"]?.ToString(); // If null, use Master library
                var extractFamilies = parameters?["extractFamilies"]?.Value<bool>() ?? true;
                var extractDraftingViews = parameters?["extractDraftingViews"]?.Value<bool>() ?? true;
                var extractLegends = parameters?["extractLegends"]?.Value<bool>() ?? true;
                var extractSchedules = parameters?["extractSchedules"]?.Value<bool>() ?? true;
                var itemNames = parameters?["itemNames"] as JArray; // Specific items to extract, or null for all new

                // First, get the comparison to know what's new
                var compareResult = CompareProjectToLibrary(uiApp, parameters);
                var compareObj = JObject.Parse(compareResult);

                if (compareObj["success"]?.Value<bool>() != true)
                {
                    return compareResult;
                }

                var comparison = compareObj["comparison"] as JObject;

                // Determine output path
                string outputBasePath;
                if (!string.IsNullOrEmpty(firmName))
                {
                    outputBasePath = Path.Combine(libraryPath, FirmLibrariesFolder, SanitizeFirmName(firmName));

                    // Create firm profile if it doesn't exist
                    var firmProfilePath = Path.Combine(outputBasePath, "firm_profile.json");
                    if (!File.Exists(firmProfilePath))
                    {
                        Directory.CreateDirectory(outputBasePath);
                        var firmProfile = new
                        {
                            firmName = firmName,
                            createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            sourceProject = doc.Title,
                            libraryPath = outputBasePath
                        };
                        File.WriteAllText(firmProfilePath, JsonConvert.SerializeObject(firmProfile, Formatting.Indented));
                    }
                }
                else
                {
                    outputBasePath = Path.Combine(libraryPath, "Master Library");
                }

                var extractionResult = new ExtractionResult
                {
                    FirmName = firmName ?? "Master",
                    OutputPath = outputBasePath,
                    ExtractedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                var app = uiApp.Application;
                var itemNamesSet = itemNames != null
                    ? new HashSet<string>(itemNames.Select(n => n.ToString()), StringComparer.OrdinalIgnoreCase)
                    : null;

                // Extract Families
                if (extractFamilies)
                {
                    var newFamilies = comparison["NewFamilies"] as JArray ?? new JArray();
                    var familiesFolder = Path.Combine(outputBasePath, "Families");

                    foreach (JObject famInfo in newFamilies)
                    {
                        var famName = famInfo["name"]?.ToString();
                        var famCategory = famInfo["category"]?.ToString() ?? "Unknown";
                        var famId = famInfo["Id"]?.Value<long>() ?? 0;

                        if (itemNamesSet != null && !itemNamesSet.Contains(famName))
                            continue;

                        try
                        {
                            var family = doc.GetElement(new ElementId(famId)) as Family;
                            if (family != null && family.IsEditable)
                            {
                                var categoryFolder = Path.Combine(familiesFolder, SanitizeFileName(famCategory));
                                Directory.CreateDirectory(categoryFolder);

                                var famDoc = doc.EditFamily(family);
                                if (famDoc != null)
                                {
                                    var savePath = Path.Combine(categoryFolder, SanitizeFileName(famName) + ".rfa");
                                    var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };
                                    famDoc.SaveAs(savePath, saveOpts);
                                    famDoc.Close(false);

                                    extractionResult.ExtractedFamilies.Add(new ExtractedItem
                                    {
                                        Name = famName,
                                        Category = famCategory,
                                        Path = savePath
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            extractionResult.Errors.Add($"Family '{famName}': {ex.Message}");
                        }
                    }
                }

                // Extract Drafting Views
                if (extractDraftingViews)
                {
                    var newDraftingViews = comparison["NewDraftingViews"] as JArray ?? new JArray();
                    var draftingFolder = Path.Combine(outputBasePath, "Drafting Views");
                    Directory.CreateDirectory(draftingFolder);

                    foreach (JObject dvInfo in newDraftingViews)
                    {
                        var dvName = dvInfo["name"]?.ToString();
                        var dvId = dvInfo["Id"]?.Value<long>() ?? 0;

                        if (itemNamesSet != null && !itemNamesSet.Contains(dvName))
                            continue;

                        try
                        {
                            var view = doc.GetElement(new ElementId(dvId)) as View;
                            if (view != null)
                            {
                                var savePath = Path.Combine(draftingFolder, SanitizeFileName(dvName) + ".rvt");
                                ExportViewToRvt(doc, app, view, savePath);

                                extractionResult.ExtractedDraftingViews.Add(new ExtractedItem
                                {
                                    Name = dvName,
                                    Category = "Drafting View",
                                    Path = savePath
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            extractionResult.Errors.Add($"Drafting View '{dvName}': {ex.Message}");
                        }
                    }
                }

                // Extract Legends
                if (extractLegends)
                {
                    var newLegends = comparison["NewLegends"] as JArray ?? new JArray();
                    var legendsFolder = Path.Combine(outputBasePath, "Legends");
                    Directory.CreateDirectory(legendsFolder);

                    foreach (JObject legInfo in newLegends)
                    {
                        var legName = legInfo["name"]?.ToString();
                        var legId = legInfo["Id"]?.Value<long>() ?? 0;

                        if (itemNamesSet != null && !itemNamesSet.Contains(legName))
                            continue;

                        try
                        {
                            var view = doc.GetElement(new ElementId(legId)) as View;
                            if (view != null)
                            {
                                var savePath = Path.Combine(legendsFolder, SanitizeFileName(legName) + ".rvt");
                                ExportViewToRvt(doc, app, view, savePath);

                                extractionResult.ExtractedLegends.Add(new ExtractedItem
                                {
                                    Name = legName,
                                    Category = "Legend",
                                    Path = savePath
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            extractionResult.Errors.Add($"Legend '{legName}': {ex.Message}");
                        }
                    }
                }

                // Extract Schedules
                if (extractSchedules)
                {
                    var newSchedules = comparison["NewSchedules"] as JArray ?? new JArray();
                    var schedulesFolder = Path.Combine(outputBasePath, "Schedules");
                    Directory.CreateDirectory(schedulesFolder);

                    foreach (JObject schInfo in newSchedules)
                    {
                        var schName = schInfo["name"]?.ToString();
                        var schCategory = schInfo["category"]?.ToString() ?? "Other Schedules";
                        var schId = schInfo["Id"]?.Value<long>() ?? 0;

                        if (itemNamesSet != null && !itemNamesSet.Contains(schName))
                            continue;

                        try
                        {
                            var schedule = doc.GetElement(new ElementId(schId)) as ViewSchedule;
                            if (schedule != null)
                            {
                                var categoryFolder = Path.Combine(schedulesFolder, SanitizeFileName(schCategory));
                                Directory.CreateDirectory(categoryFolder);

                                var savePath = Path.Combine(categoryFolder, SanitizeFileName(schName) + ".rvt");
                                ExportViewToRvt(doc, schedule, app, savePath);

                                extractionResult.ExtractedSchedules.Add(new ExtractedItem
                                {
                                    Name = schName,
                                    Category = schCategory,
                                    Path = savePath
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            extractionResult.Errors.Add($"Schedule '{schName}': {ex.Message}");
                        }
                    }
                }

                // Calculate totals
                extractionResult.TotalExtracted = extractionResult.ExtractedFamilies.Count +
                                                  extractionResult.ExtractedDraftingViews.Count +
                                                  extractionResult.ExtractedLegends.Count +
                                                  extractionResult.ExtractedSchedules.Count;

                // Rebuild the library index if items were extracted
                if (extractionResult.TotalExtracted > 0)
                {
                    // TODO: Call index rebuild or append to existing index
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    extraction = extractionResult
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Exports a view to its own RVT file by copying the view element to a new document.
        /// </summary>
        private static void ExportViewToRvt(Document doc, Autodesk.Revit.ApplicationServices.Application app, View view, string filePath)
        {
            Document newDoc = null;
            try
            {
                newDoc = app.NewProjectDocument(UnitSystem.Imperial);

                var copyOptions = new CopyPasteOptions();
                copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                using (var trans = new Transaction(newDoc, "Copy View"))
                {
                    trans.Start();

                    var sourceIds = new List<ElementId> { view.Id };
                    ElementTransformUtils.CopyElements(
                        doc,
                        sourceIds,
                        newDoc,
                        Transform.Identity,
                        copyOptions);

                    trans.Commit();
                }

                var saveOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    Compact = true
                };
                newDoc.SaveAs(filePath, saveOptions);
            }
            finally
            {
                newDoc?.Close(false);
            }
        }

        /// <summary>
        /// Exports a schedule view to its own RVT file.
        /// </summary>
        private static void ExportViewToRvt(Document doc, ViewSchedule schedule, Autodesk.Revit.ApplicationServices.Application app, string filePath)
        {
            ExportViewToRvt(doc, app, schedule as View, filePath);
        }

        #endregion

        #region Firm Profile Methods

        /// <summary>
        /// Creates a new firm profile for storing firm-specific library content.
        /// </summary>
        public static string CreateFirmProfile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var firmName = parameters?["firmName"]?.ToString();
                var description = parameters?["description"]?.ToString();
                var contactEmail = parameters?["contactEmail"]?.ToString();
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;

                if (string.IsNullOrEmpty(firmName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "firmName is required" });
                }

                var firmFolderName = SanitizeFirmName(firmName);
                var firmPath = Path.Combine(libraryPath, FirmLibrariesFolder, firmFolderName);

                if (Directory.Exists(firmPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Firm profile already exists at {firmPath}"
                    });
                }

                // Create firm directory structure
                Directory.CreateDirectory(firmPath);
                Directory.CreateDirectory(Path.Combine(firmPath, "Families"));
                Directory.CreateDirectory(Path.Combine(firmPath, "Drafting Views"));
                Directory.CreateDirectory(Path.Combine(firmPath, "Legends"));
                Directory.CreateDirectory(Path.Combine(firmPath, "Schedules"));

                // Create firm profile
                var profile = new FirmProfile
                {
                    FirmName = firmName,
                    FirmId = firmFolderName,
                    Description = description,
                    ContactEmail = contactEmail,
                    LibraryPath = firmPath,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ItemCounts = new ItemCounts()
                };

                var profilePath = Path.Combine(firmPath, "firm_profile.json");
                File.WriteAllText(profilePath, JsonConvert.SerializeObject(profile, Formatting.Indented));

                // Update master firm profiles list
                UpdateFirmProfilesList(libraryPath, profile);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    profile = profile,
                    message = $"Firm profile created for '{firmName}'"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all registered firm profiles.
        /// </summary>
        public static string GetFirmProfiles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;
                var profilesPath = Path.Combine(libraryPath, FirmProfilesFile);

                if (!File.Exists(profilesPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        firms = new List<object>(),
                        message = "No firm profiles found"
                    });
                }

                var profilesJson = File.ReadAllText(profilesPath);
                var profiles = JsonConvert.DeserializeObject<List<FirmProfile>>(profilesJson);

                // Update item counts for each firm
                foreach (var profile in profiles)
                {
                    if (Directory.Exists(profile.LibraryPath))
                    {
                        profile.ItemCounts = CountFirmItems(profile.LibraryPath);
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    firmCount = profiles.Count,
                    firms = profiles
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Detects the firm from title blocks on sheets (primary) and project information (fallback).
        /// Title blocks are the authoritative source as they contain the firm name that appears on printed drawings.
        /// </summary>
        public static string DetectFirmFromProject(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;

                // Get project info (fallback)
                var projectInfo = doc.ProjectInformation;
                var clientName = projectInfo?.ClientName ?? "";
                var organizationName = projectInfo?.OrganizationName ?? "";
                var author = projectInfo?.Author ?? "";
                var projectName = doc.Title;

                // PRIMARY: Extract firm name from title blocks on sheets
                string titleBlockFirmName = null;
                string titleBlockFamilyName = null;
                var titleBlockInfo = new Dictionary<string, string>();

                // Get sheets and their title blocks
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .Take(10) // Sample first 10 sheets
                    .ToList();

                foreach (var sheet in sheets)
                {
                    // Get title block on this sheet
                    var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .ToList();

                    foreach (var tb in titleBlocks)
                    {
                        // Get the title block family name (often contains firm name)
                        if (string.IsNullOrEmpty(titleBlockFamilyName))
                        {
                            titleBlockFamilyName = tb.Symbol?.Family?.Name;
                        }

                        // Common parameters that contain firm/company name
                        var firmParams = new[]
                        {
                            "Firm Name", "Company Name", "Architect", "Architecture Firm",
                            "Designed By", "Design Firm", "AOR", "Architect of Record",
                            "Drawn By Organization", "Firm", "Office Name", "Studio Name"
                        };

                        foreach (var paramName in firmParams)
                        {
                            var param = tb.LookupParameter(paramName);
                            if (param != null && param.HasValue && param.StorageType == StorageType.String)
                            {
                                var value = param.AsString()?.Trim();
                                if (!string.IsNullOrEmpty(value) && value.Length > 2)
                                {
                                    titleBlockInfo[paramName] = value;
                                    if (string.IsNullOrEmpty(titleBlockFirmName))
                                    {
                                        titleBlockFirmName = value;
                                    }
                                }
                            }
                        }

                        // Also check instance parameters
                        foreach (Parameter param in tb.Parameters)
                        {
                            if (param.StorageType == StorageType.String && param.HasValue)
                            {
                                var paramDef = param.Definition?.Name?.ToLower() ?? "";
                                if (paramDef.Contains("firm") || paramDef.Contains("architect") ||
                                    paramDef.Contains("company") || paramDef.Contains("office"))
                                {
                                    var value = param.AsString()?.Trim();
                                    if (!string.IsNullOrEmpty(value) && value.Length > 2)
                                    {
                                        titleBlockInfo[param.Definition.Name] = value;
                                        if (string.IsNullOrEmpty(titleBlockFirmName))
                                        {
                                            titleBlockFirmName = value;
                                        }
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(titleBlockFirmName)) break;
                    }

                    if (!string.IsNullOrEmpty(titleBlockFirmName)) break;
                }

                // Determine the best firm name to use (priority order)
                var detectedFirmName = titleBlockFirmName ?? organizationName ?? clientName ?? author;

                // Get existing firm profiles
                var profilesPath = Path.Combine(libraryPath, FirmProfilesFile);
                var profiles = new List<FirmProfile>();

                if (File.Exists(profilesPath))
                {
                    var profilesJson = File.ReadAllText(profilesPath);
                    profiles = JsonConvert.DeserializeObject<List<FirmProfile>>(profilesJson) ?? new List<FirmProfile>();
                }

                // Try to match against existing profiles
                FirmProfile matchedProfile = null;
                string matchReason = null;
                string matchSource = null;

                foreach (var profile in profiles)
                {
                    // Check title block firm name first (most reliable)
                    if (!string.IsNullOrEmpty(titleBlockFirmName) &&
                        (profile.FirmName.Contains(titleBlockFirmName, StringComparison.OrdinalIgnoreCase) ||
                         titleBlockFirmName.Contains(profile.FirmName, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchedProfile = profile;
                        matchReason = $"Title block firm name matches: {titleBlockFirmName}";
                        matchSource = "title_block";
                        break;
                    }

                    // Check title block family name
                    if (!string.IsNullOrEmpty(titleBlockFamilyName) &&
                        profile.FirmName.Contains(titleBlockFamilyName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedProfile = profile;
                        matchReason = $"Title block family name matches: {titleBlockFamilyName}";
                        matchSource = "title_block_family";
                        break;
                    }

                    // Check organization name
                    if (!string.IsNullOrEmpty(organizationName) &&
                        profile.FirmName.Contains(organizationName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedProfile = profile;
                        matchReason = $"Organization name matches: {organizationName}";
                        matchSource = "project_info";
                        break;
                    }

                    // Check client name
                    if (!string.IsNullOrEmpty(clientName) &&
                        profile.FirmName.Contains(clientName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedProfile = profile;
                        matchReason = $"Client name matches: {clientName}";
                        matchSource = "project_info";
                        break;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectInfo = new
                    {
                        projectName = projectName,
                        clientName = clientName,
                        organizationName = organizationName,
                        author = author
                    },
                    titleBlockInfo = new
                    {
                        firmName = titleBlockFirmName,
                        familyName = titleBlockFamilyName,
                        parameters = titleBlockInfo,
                        sheetsScanned = sheets.Count
                    },
                    detectedFirmName = detectedFirmName,
                    detectedFirm = matchedProfile,
                    matchReason = matchReason,
                    matchSource = matchSource,
                    isNewFirm = matchedProfile == null,
                    suggestion = matchedProfile == null
                        ? $"No matching firm found. Create new profile for '{detectedFirmName}' or use Master library."
                        : $"Matched to firm: {matchedProfile.FirmName}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rebuilds the library index to include all firm libraries.
        /// </summary>
        public static string RebuildLibraryIndex(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;
                var includeFirmLibraries = parameters?["includeFirmLibraries"]?.Value<bool>() ?? true;

                var index = new JObject
                {
                    ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["source"] = "RebuildLibraryIndex",
                    ["library_path"] = libraryPath
                };

                var categories = new JObject();
                var totalFiles = 0;
                double totalSizeMb = 0;

                // Index Master Library
                var masterPath = Path.Combine(libraryPath, "Master Library");
                if (Directory.Exists(masterPath))
                {
                    IndexLibraryFolder(masterPath, categories, ref totalFiles, ref totalSizeMb);
                }

                // Index Revit Details folder
                var detailsPath = Path.Combine(libraryPath, "Revit Details");
                if (Directory.Exists(detailsPath))
                {
                    IndexDraftingViews(detailsPath, categories, ref totalFiles, ref totalSizeMb);
                }

                // Index Firm Libraries if requested
                if (includeFirmLibraries)
                {
                    var firmLibrariesPath = Path.Combine(libraryPath, FirmLibrariesFolder);
                    if (Directory.Exists(firmLibrariesPath))
                    {
                        foreach (var firmDir in Directory.GetDirectories(firmLibrariesPath))
                        {
                            IndexLibraryFolder(firmDir, categories, ref totalFiles, ref totalSizeMb, Path.GetFileName(firmDir));
                        }
                    }
                }

                index["categories"] = categories;
                index["stats"] = new JObject
                {
                    ["total_files"] = totalFiles,
                    ["total_size_mb"] = Math.Round(totalSizeMb, 1)
                };

                // Save index
                var indexPath = Path.Combine(libraryPath, IndexFileName);
                File.WriteAllText(indexPath, index.ToString(Formatting.Indented));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    indexPath = indexPath,
                    totalFiles = totalFiles,
                    totalSizeMb = Math.Round(totalSizeMb, 1),
                    message = "Library index rebuilt successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static string GetProjectNumber(Document doc)
        {
            try { return doc.ProjectInformation?.Number ?? ""; }
            catch { return ""; }
        }

        private static string GetClientName(Document doc)
        {
            try { return doc.ProjectInformation?.ClientName ?? ""; }
            catch { return ""; }
        }

        private static bool IsSystemFamily(Family family)
        {
            // Check if it's a system family (walls, floors, etc. that can't be exported)
            try
            {
                return !family.IsEditable;
            }
            catch
            {
                return true;
            }
        }

        private static string ComputeFamilyHash(string name, string category)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{category}:{name}"));
                return Convert.ToBase64String(bytes).Substring(0, 16);
            }
        }

        private static string ComputeViewHash(string name, string viewType)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{viewType}:{name}"));
                return Convert.ToBase64String(bytes).Substring(0, 16);
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string SanitizeFirmName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ' ' }).ToArray();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        }

        private static void UpdateFirmProfilesList(string libraryPath, FirmProfile newProfile)
        {
            var profilesPath = Path.Combine(libraryPath, FirmProfilesFile);
            var profiles = new List<FirmProfile>();

            if (File.Exists(profilesPath))
            {
                var json = File.ReadAllText(profilesPath);
                profiles = JsonConvert.DeserializeObject<List<FirmProfile>>(json) ?? new List<FirmProfile>();
            }

            // Remove existing profile with same ID if exists
            profiles.RemoveAll(p => p.FirmId == newProfile.FirmId);
            profiles.Add(newProfile);

            File.WriteAllText(profilesPath, JsonConvert.SerializeObject(profiles, Formatting.Indented));
        }

        private static ItemCounts CountFirmItems(string firmPath)
        {
            var counts = new ItemCounts();

            var familiesPath = Path.Combine(firmPath, "Families");
            if (Directory.Exists(familiesPath))
            {
                counts.Families = Directory.GetFiles(familiesPath, "*.rfa", SearchOption.AllDirectories).Length;
            }

            var draftingPath = Path.Combine(firmPath, "Drafting Views");
            if (Directory.Exists(draftingPath))
            {
                counts.DraftingViews = Directory.GetFiles(draftingPath, "*.rvt", SearchOption.AllDirectories).Length;
            }

            var legendsPath = Path.Combine(firmPath, "Legends");
            if (Directory.Exists(legendsPath))
            {
                counts.Legends = Directory.GetFiles(legendsPath, "*.rvt", SearchOption.AllDirectories).Length;
            }

            var schedulesPath = Path.Combine(firmPath, "Schedules");
            if (Directory.Exists(schedulesPath))
            {
                counts.Schedules = Directory.GetFiles(schedulesPath, "*.rvt", SearchOption.AllDirectories).Length;
            }

            return counts;
        }

        private static void IndexLibraryFolder(string folderPath, JObject categories, ref int totalFiles, ref double totalSizeMb, string firmPrefix = null)
        {
            // Index Families
            var familiesPath = Path.Combine(folderPath, "Families");
            if (Directory.Exists(familiesPath))
            {
                var familyItems = new JArray();
                foreach (var file in Directory.GetFiles(familiesPath, "*.rfa", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    var category = Path.GetFileName(Path.GetDirectoryName(file));

                    familyItems.Add(new JObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(file),
                        ["filename"] = fi.Name,
                        ["category"] = category,
                        ["path"] = file,
                        ["size_kb"] = Math.Round(fi.Length / 1024.0, 1),
                        ["type"] = "family",
                        ["firm"] = firmPrefix
                    });

                    totalFiles++;
                    totalSizeMb += fi.Length / (1024.0 * 1024.0);
                }

                if (familyItems.Count > 0)
                {
                    var catKey = firmPrefix != null ? $"families_{firmPrefix}" : "families";
                    categories[catKey] = new JObject
                    {
                        ["count"] = familyItems.Count,
                        ["size_mb"] = Math.Round(familyItems.Sum(i => i["size_kb"].Value<double>()) / 1024.0, 1),
                        ["items"] = familyItems
                    };
                }
            }

            // Index Legends
            var legendsPath = Path.Combine(folderPath, "Legends");
            if (Directory.Exists(legendsPath))
            {
                var legendItems = new JArray();
                foreach (var file in Directory.GetFiles(legendsPath, "*.rvt", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);

                    legendItems.Add(new JObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(file),
                        ["filename"] = fi.Name,
                        ["category"] = "legend",
                        ["path"] = file,
                        ["size_kb"] = Math.Round(fi.Length / 1024.0, 1),
                        ["type"] = "legend",
                        ["firm"] = firmPrefix
                    });

                    totalFiles++;
                    totalSizeMb += fi.Length / (1024.0 * 1024.0);
                }

                if (legendItems.Count > 0)
                {
                    var catKey = firmPrefix != null ? $"legends_{firmPrefix}" : "legends";
                    categories[catKey] = new JObject
                    {
                        ["count"] = legendItems.Count,
                        ["size_mb"] = Math.Round(legendItems.Sum(i => i["size_kb"].Value<double>()) / 1024.0, 1),
                        ["items"] = legendItems
                    };
                }
            }

            // Index Schedules
            var schedulesPath = Path.Combine(folderPath, "Schedules");
            if (Directory.Exists(schedulesPath))
            {
                var scheduleItems = new JArray();
                foreach (var file in Directory.GetFiles(schedulesPath, "*.rvt", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    var category = Path.GetFileName(Path.GetDirectoryName(file));

                    scheduleItems.Add(new JObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(file),
                        ["filename"] = fi.Name,
                        ["category"] = category,
                        ["path"] = file,
                        ["size_kb"] = Math.Round(fi.Length / 1024.0, 1),
                        ["type"] = "schedule",
                        ["firm"] = firmPrefix
                    });

                    totalFiles++;
                    totalSizeMb += fi.Length / (1024.0 * 1024.0);
                }

                if (scheduleItems.Count > 0)
                {
                    var catKey = firmPrefix != null ? $"schedules_{firmPrefix}" : "schedules";
                    categories[catKey] = new JObject
                    {
                        ["count"] = scheduleItems.Count,
                        ["size_mb"] = Math.Round(scheduleItems.Sum(i => i["size_kb"].Value<double>()) / 1024.0, 1),
                        ["items"] = scheduleItems
                    };
                }
            }
        }

        private static void IndexDraftingViews(string folderPath, JObject categories, ref int totalFiles, ref double totalSizeMb)
        {
            var draftingItems = new JArray();

            foreach (var file in Directory.GetFiles(folderPath, "*.rvt", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(file);
                var category = Path.GetFileName(Path.GetDirectoryName(file));

                draftingItems.Add(new JObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(file),
                    ["filename"] = fi.Name,
                    ["category"] = category,
                    ["path"] = file,
                    ["size_kb"] = Math.Round(fi.Length / 1024.0, 1),
                    ["type"] = "drafting_view"
                });

                totalFiles++;
                totalSizeMb += fi.Length / (1024.0 * 1024.0);
            }

            if (draftingItems.Count > 0)
            {
                categories["drafting_views"] = new JObject
                {
                    ["count"] = draftingItems.Count,
                    ["size_mb"] = Math.Round(draftingItems.Sum(i => i["size_kb"].Value<double>()) / 1024.0, 1),
                    ["items"] = draftingItems
                };
            }
        }

        #endregion

        #region Data Classes

        private class ProjectContentScan
        {
            public string ProjectName { get; set; }
            public string ProjectNumber { get; set; }
            public string ClientName { get; set; }
            public string ScannedAt { get; set; }
            public int TotalItems { get; set; }
            public int TotalFamilies { get; set; }
            public int TotalDraftingViews { get; set; }
            public int TotalLegends { get; set; }
            public int TotalSchedules { get; set; }
            public List<string> FamilyCategories { get; set; } = new List<string>();
            public List<ScannedItem> Families { get; set; } = new List<ScannedItem>();
            public List<ScannedItem> DraftingViews { get; set; } = new List<ScannedItem>();
            public List<ScannedItem> Legends { get; set; } = new List<ScannedItem>();
            public List<ScannedItem> Schedules { get; set; } = new List<ScannedItem>();
        }

        private class ScannedItem
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public long Id { get; set; }
            public int TypeCount { get; set; }
            public int InstanceCount { get; set; }
            public int ElementCount { get; set; }
            public int FieldCount { get; set; }
            public int Scale { get; set; }
            public string ContentHash { get; set; }
        }

        private class ComparisonResult
        {
            public string ProjectName { get; set; }
            public string LibraryPath { get; set; }
            public string ComparedAt { get; set; }
            public ComparisonSummary Summary { get; set; }
            public List<JObject> NewFamilies { get; set; } = new List<JObject>();
            public List<JObject> NewDraftingViews { get; set; } = new List<JObject>();
            public List<JObject> NewLegends { get; set; } = new List<JObject>();
            public List<JObject> NewSchedules { get; set; } = new List<JObject>();
            public List<JObject> MatchingFamilies { get; set; } = new List<JObject>();
            public List<JObject> MatchingDraftingViews { get; set; } = new List<JObject>();
            public List<JObject> MatchingLegends { get; set; } = new List<JObject>();
            public List<JObject> MatchingSchedules { get; set; } = new List<JObject>();
            public List<JObject> AvailableFamilies { get; set; } = new List<JObject>();
        }

        private class ComparisonSummary
        {
            public int TotalProjectItems { get; set; }
            public int TotalLibraryItems { get; set; }
            public int NewItemsCount { get; set; }
            public int MatchingItemsCount { get; set; }
            public int AvailableItemsCount { get; set; }
            public int NewFamiliesCount { get; set; }
            public int NewDraftingViewsCount { get; set; }
            public int NewLegendsCount { get; set; }
            public int NewSchedulesCount { get; set; }
        }

        private class ExtractionResult
        {
            public string FirmName { get; set; }
            public string OutputPath { get; set; }
            public string ExtractedAt { get; set; }
            public int TotalExtracted { get; set; }
            public List<ExtractedItem> ExtractedFamilies { get; set; } = new List<ExtractedItem>();
            public List<ExtractedItem> ExtractedDraftingViews { get; set; } = new List<ExtractedItem>();
            public List<ExtractedItem> ExtractedLegends { get; set; } = new List<ExtractedItem>();
            public List<ExtractedItem> ExtractedSchedules { get; set; } = new List<ExtractedItem>();
            public List<string> Errors { get; set; } = new List<string>();
        }

        private class ExtractedItem
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public string Path { get; set; }
        }

        private class FirmProfile
        {
            public string FirmName { get; set; }
            public string FirmId { get; set; }
            public string Description { get; set; }
            public string ContactEmail { get; set; }
            public string LibraryPath { get; set; }
            public string CreatedAt { get; set; }
            public string LastUpdated { get; set; }
            public ItemCounts ItemCounts { get; set; }
        }

        private class ItemCounts
        {
            public int Families { get; set; }
            public int DraftingViews { get; set; }
            public int Legends { get; set; }
            public int Schedules { get; set; }
        }

        private class DuplicateTypeNamesHandler : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        #endregion
    }
}
