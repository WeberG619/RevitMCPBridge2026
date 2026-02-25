using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Methods for searching and loading items from the Revit Detail Library
    /// </summary>
    public static class LibraryMethods
    {
        // Default library path - can be overridden via parameters
        private static readonly string DefaultLibraryPath = @"D:\Revit Detail Libraries";
        private static readonly string IndexFileName = "library_index.json";

        #region Search Methods

        /// <summary>
        /// Searches the library index for items matching the query.
        /// Supports filtering by type (drafting_view, family, legend, schedule) and category.
        /// </summary>
        public static string SearchLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var query = parameters?["query"]?.ToString() ?? "";
                var typeFilter = parameters?["type"]?.ToString(); // drafting_view, family, legend, schedule
                var categoryFilter = parameters?["category"]?.ToString();
                var limit = parameters?["limit"]?.Value<int>() ?? 50;
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;

                // Load the index
                var indexPath = Path.Combine(libraryPath, IndexFileName);
                if (!File.Exists(indexPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Library index not found at {indexPath}. Run index generation first."
                    });
                }

                var indexJson = File.ReadAllText(indexPath);
                var index = JObject.Parse(indexJson);
                var categories = index["categories"] as JObject;

                if (categories == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid index format - categories not found"
                    });
                }

                var results = new List<object>();
                var queryLower = query.ToLower();

                // Search each category
                foreach (var catProp in categories.Properties())
                {
                    var catName = catProp.Name;
                    var catData = catProp.Value as JObject;
                    var items = catData?["items"] as JArray;

                    if (items == null) continue;

                    foreach (var item in items)
                    {
                        var itemObj = item as JObject;
                        if (itemObj == null) continue;

                        var name = itemObj["name"]?.ToString() ?? "";
                        var itemType = itemObj["type"]?.ToString() ?? "";
                        var itemCategory = itemObj["category"]?.ToString() ?? "";

                        // Apply type filter
                        if (!string.IsNullOrEmpty(typeFilter) && !itemType.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Apply category filter
                        if (!string.IsNullOrEmpty(categoryFilter) && !itemCategory.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Apply query filter (search in name)
                        if (!string.IsNullOrEmpty(query) && !name.ToLower().Contains(queryLower))
                            continue;

                        results.Add(new
                        {
                            name = name,
                            type = itemType,
                            category = itemCategory,
                            filename = itemObj["filename"]?.ToString(),
                            path = itemObj["path"]?.ToString(),
                            size_kb = itemObj["size_kb"]?.Value<double>() ?? 0
                        });

                        if (results.Count >= limit) break;
                    }

                    if (results.Count >= limit) break;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    query = query,
                    typeFilter = typeFilter,
                    categoryFilter = categoryFilter,
                    resultCount = results.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets library statistics and category breakdown
        /// </summary>
        public static string GetLibraryStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;
                var indexPath = Path.Combine(libraryPath, IndexFileName);

                if (!File.Exists(indexPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Library index not found at {indexPath}"
                    });
                }

                var indexJson = File.ReadAllText(indexPath);
                var index = JObject.Parse(indexJson);

                var stats = index["stats"] as JObject;
                var categories = index["categories"] as JObject;

                var categoryStats = new List<object>();
                if (categories != null)
                {
                    foreach (var catProp in categories.Properties())
                    {
                        var catData = catProp.Value as JObject;
                        var items = catData?["items"] as JArray;

                        // Get unique subcategories
                        var subcategories = new HashSet<string>();
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                var subcat = item["category"]?.ToString();
                                if (!string.IsNullOrEmpty(subcat))
                                    subcategories.Add(subcat);
                            }
                        }

                        categoryStats.Add(new
                        {
                            name = catProp.Name,
                            count = catData?["count"]?.Value<int>() ?? 0,
                            size_mb = catData?["size_mb"]?.Value<double>() ?? 0,
                            subcategories = subcategories.OrderBy(s => s).ToList()
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    libraryPath = libraryPath,
                    generated = index["generated"]?.ToString(),
                    source_project = index["source_project"]?.ToString(),
                    total_files = stats?["total_files"]?.Value<int>() ?? 0,
                    total_size_mb = stats?["total_size_mb"]?.Value<double>() ?? 0,
                    categories = categoryStats
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Load Methods

        /// <summary>
        /// Loads a family (.rfa) from the library into the current project.
        /// Returns the loaded family ID and available types.
        /// </summary>
        public static string LoadLibraryFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var familyPath = parameters?["path"]?.ToString();
                var familyName = parameters?["name"]?.ToString();
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;

                // If name provided but not path, search for it
                if (string.IsNullOrEmpty(familyPath) && !string.IsNullOrEmpty(familyName))
                {
                    var searchResult = SearchLibrary(uiApp, JObject.FromObject(new
                    {
                        query = familyName,
                        type = "family",
                        limit = 1,
                        libraryPath = libraryPath
                    }));

                    var searchObj = JObject.Parse(searchResult);
                    if (searchObj["success"]?.Value<bool>() == true)
                    {
                        var results = searchObj["results"] as JArray;
                        if (results != null && results.Count > 0)
                        {
                            familyPath = results[0]["path"]?.ToString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(familyPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family path required. Provide 'path' or 'name' parameter."
                    });
                }

                if (!File.Exists(familyPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family file not found: {familyPath}"
                    });
                }

                // Check if family already loaded
                var existingFamily = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(Path.GetFileNameWithoutExtension(familyPath), StringComparison.OrdinalIgnoreCase));

                if (existingFamily != null)
                {
                    // Already loaded - return existing types
                    var existingTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(fs => fs.Family.Id == existingFamily.Id)
                        .Select(fs => new { id = fs.Id.Value, name = fs.Name })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        alreadyLoaded = true,
                        familyId = existingFamily.Id.Value,
                        familyName = existingFamily.Name,
                        types = existingTypes
                    });
                }

                // Load the family
                Family loadedFamily = null;
                using (var trans = new Transaction(doc, "Load Library Family"))
                {
                    trans.Start();

                    if (doc.LoadFamily(familyPath, out loadedFamily))
                    {
                        trans.Commit();
                    }
                    else
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to load family. It may already exist or be incompatible."
                        });
                    }
                }

                // Get available types
                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family.Id == loadedFamily.Id)
                    .Select(fs => new { id = fs.Id.Value, name = fs.Name })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    alreadyLoaded = false,
                    familyId = loadedFamily.Id.Value,
                    familyName = loadedFamily.Name,
                    types = types,
                    message = $"Family '{loadedFamily.Name}' loaded with {types.Count} type(s)"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Inserts a drafting view, legend, or schedule from the library into the current project.
        /// For views: copies the view and its contents.
        /// </summary>
        public static string InsertLibraryView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var viewPath = parameters?["path"]?.ToString();
                var viewName = parameters?["name"]?.ToString();
                var viewType = parameters?["type"]?.ToString(); // drafting_view, legend, schedule
                var libraryPath = parameters?["libraryPath"]?.ToString() ?? DefaultLibraryPath;

                // If name provided but not path, search for it
                if (string.IsNullOrEmpty(viewPath) && !string.IsNullOrEmpty(viewName))
                {
                    var searchParams = new Dictionary<string, object>
                    {
                        { "query", viewName },
                        { "limit", 1 },
                        { "libraryPath", libraryPath }
                    };

                    if (!string.IsNullOrEmpty(viewType))
                    {
                        searchParams["type"] = viewType;
                    }

                    var searchResult = SearchLibrary(uiApp, JObject.FromObject(searchParams));
                    var searchObj = JObject.Parse(searchResult);

                    if (searchObj["success"]?.Value<bool>() == true)
                    {
                        var results = searchObj["results"] as JArray;
                        if (results != null && results.Count > 0)
                        {
                            viewPath = results[0]["path"]?.ToString();
                            viewType = results[0]["type"]?.ToString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(viewPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View path required. Provide 'path' or 'name' parameter."
                    });
                }

                if (!File.Exists(viewPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View file not found: {viewPath}"
                    });
                }

                // Open the library document
                var app = uiApp.Application;
                Document libDoc = null;

                try
                {
                    libDoc = app.OpenDocumentFile(viewPath);
                }
                catch (Exception ex)
                {
                    return ResponseBuilder.FromException(ex).Build();
                }

                try
                {
                    // Find the view in the library document
                    var views = new FilteredElementCollector(libDoc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted)
                        .ToList();

                    if (views.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No printable views found in library file"
                        });
                    }

                    var sourceView = views.First();

                    // Copy the view to the current document
                    var copyOptions = new CopyPasteOptions();
                    copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                    ICollection<ElementId> copiedIds = null;

                    using (var trans = new Transaction(doc, "Insert Library View"))
                    {
                        trans.Start();

                        copiedIds = ElementTransformUtils.CopyElements(
                            libDoc,
                            new List<ElementId> { sourceView.Id },
                            doc,
                            Transform.Identity,
                            copyOptions);

                        trans.Commit();
                    }

                    if (copiedIds == null || copiedIds.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to copy view to document"
                        });
                    }

                    var copiedViewId = copiedIds.First();
                    var copiedView = doc.GetElement(copiedViewId) as View;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = copiedViewId.Value,
                        viewName = copiedView?.Name ?? "Unknown",
                        viewType = copiedView?.ViewType.ToString() ?? viewType,
                        message = $"View '{copiedView?.Name}' inserted successfully"
                    });
                }
                finally
                {
                    // Close the library document without saving
                    libDoc?.Close(false);
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Handler for duplicate type names during copy operations
        /// </summary>
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
