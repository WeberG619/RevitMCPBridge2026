using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Revit Families
    /// Handles family loading, management, type creation, and family instances
    /// </summary>
    public static class FamilyMethods
    {
        #region Family Loading

        /// <summary>
        /// Loads a family from a file into the project
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing familyPath, overwrite (optional)</param>
        /// <returns>JSON response with success status and family ID</returns>
        public static string LoadFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["familyPath"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familyPath is required"
                    });
                }

                var familyPath = parameters["familyPath"].ToString();
                var overwrite = parameters["overwrite"] != null ? bool.Parse(parameters["overwrite"].ToString()) : false;

                // Validate file exists
                if (!System.IO.File.Exists(familyPath))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family file not found: {familyPath}"
                    });
                }

                // Validate file extension
                if (!familyPath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "File must be a Revit family file (.rfa)"
                    });
                }

                using (var trans = new Transaction(doc, "Load Family"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    Family loadedFamily = null;
                    bool wasLoaded = false;

                    // Load the family
                    if (overwrite)
                    {
                        // Overwrite existing family
                        wasLoaded = doc.LoadFamily(familyPath, new FamilyLoadOptions(), out loadedFamily);
                    }
                    else
                    {
                        // Don't overwrite - will fail if family already exists
                        wasLoaded = doc.LoadFamily(familyPath, out loadedFamily);
                    }

                    if (!wasLoaded || loadedFamily == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to load family. Family may already exist in project (use overwrite: true to replace)"
                        });
                    }

                    // Get family type count
                    var familySymbolIds = loadedFamily.GetFamilySymbolIds();
                    var typeCount = familySymbolIds.Count;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        familyId = (int)loadedFamily.Id.Value,
                        familyName = loadedFamily.Name,
                        typeCount = typeCount,
                        filePath = familyPath,
                        overwritten = overwrite,
                        message = $"Family '{loadedFamily.Name}' loaded successfully with {typeCount} type(s)"
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
        /// Helper class for family loading options
        /// </summary>
        private class FamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                // Overwrite the family and its parameter values
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                // Use the shared family from the file
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        /// <summary>
        /// Loads multiple families from a directory
        /// </summary>
        public static string LoadFamiliesFromDirectory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["directoryPath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "directoryPath parameter is required"
                    });
                }

                string directoryPath = parameters["directoryPath"].ToString();
                bool recursive = parameters["recursive"]?.ToObject<bool>() ?? false;
                string fileFilter = parameters["fileFilter"]?.ToString() ?? "*.rfa";
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? true;

                // Validate directory exists
                if (!System.IO.Directory.Exists(directoryPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Directory not found: {directoryPath}"
                    });
                }

                // Get all .rfa files
                var searchOption = recursive ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                var rfaFiles = System.IO.Directory.GetFiles(directoryPath, fileFilter, searchOption);

                var results = new List<object>();
                var loadOptions = new FamilyLoadOptions();

                using (var trans = new Transaction(doc, "Load Families from Directory"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var filePath in rfaFiles)
                    {
                        try
                        {
                            Family loadedFamily = null;
                            bool loaded = doc.LoadFamily(filePath, loadOptions, out loadedFamily);

                            if (loaded && loadedFamily != null)
                            {
                                results.Add(new
                                {
                                    success = true,
                                    filePath,
                                    fileName = System.IO.Path.GetFileName(filePath),
                                    familyId = (int)loadedFamily.Id.Value,
                                    familyName = loadedFamily.Name,
                                    category = loadedFamily.FamilyCategory?.Name
                                });
                            }
                            else
                            {
                                results.Add(new
                                {
                                    success = false,
                                    filePath,
                                    fileName = System.IO.Path.GetFileName(filePath),
                                    error = "Failed to load family"
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            results.Add(new
                            {
                                success = false,
                                filePath,
                                fileName = System.IO.Path.GetFileName(filePath),
                                error = ex.Message
                            });
                        }
                    }

                    trans.Commit();
                }

                var successCount = results.Count(r => ((dynamic)r).success == true);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    directoryPath,
                    filesFound = rfaFiles.Length,
                    familiesLoaded = successCount,
                    familiesFailed = rfaFiles.Length - successCount,
                    recursive,
                    fileFilter,
                    results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Reloads a family that's already in the project
        /// </summary>
        public static string ReloadFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["familyId"] == null && parameters["familyName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either familyId or familyName parameter is required"
                    });
                }

                Family family = null;

                // Try to find by ID first
                if (parameters["familyId"] != null)
                {
                    if (int.TryParse(parameters["familyId"].ToString(), out int familyIdInt))
                    {
                        var familyId = new ElementId(familyIdInt);
                        family = doc.GetElement(familyId) as Family;
                    }
                }

                // Otherwise search by name
                if (family == null && parameters["familyName"] != null)
                {
                    string familyName = parameters["familyName"].ToString();
                    family = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
                }

                if (family == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family not found"
                    });
                }

                // Get the family document path
                var familyDoc = doc.EditFamily(family);
                if (familyDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Unable to access family document"
                    });
                }

                // Close the family document without saving (we just wanted to refresh it)
                familyDoc.Close(false);

                using (var trans = new Transaction(doc, "Reload Family"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get family symbols to count
                    var symbolCountBefore = family.GetFamilySymbolIds().Count;

                    // The family is already in the document, just need to refresh
                    // We can't directly reload, but we showed how to open and close
                    // For a true reload, user would need to provide the file path

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "Family document accessed. For full reload from file, use LoadFamily with the family file path.",
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        typeCount = symbolCountBefore,
                        category = family.FamilyCategory?.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a family type (prevents deletion if it's the last type in the family)
        /// </summary>
        public static string DeleteFamilyType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get type by ID or name
                FamilySymbol familySymbol = null;

                if (parameters["typeId"] != null)
                {
                    var typeIdInt = parameters["typeId"].ToObject<int>();
                    var typeId = new ElementId(typeIdInt);
                    familySymbol = doc.GetElement(typeId) as FamilySymbol;
                }
                else if (parameters["typeName"] != null)
                {
                    string typeName = parameters["typeName"].ToString();

                    // Optional family name filter
                    string familyName = parameters["familyName"]?.ToString();

                    var symbols = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>();

                    if (!string.IsNullOrEmpty(familyName))
                    {
                        symbols = symbols.Where(s => s.Family.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
                    }

                    familySymbol = symbols.FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                }

                if (familySymbol == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family type not found"
                    });
                }

                // Check if this is the last type in the family
                var family = familySymbol.Family;
                var symbolIds = family.GetFamilySymbolIds();

                if (symbolIds.Count <= 1)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot delete the last type in a family",
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        typeId = (int)familySymbol.Id.Value,
                        typeName = familySymbol.Name
                    });
                }

                // Check instance count
                var instanceCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Count(inst => inst.GetTypeId() == familySymbol.Id);

                using (var trans = new Transaction(doc, "Delete Family Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(familySymbol.Id);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedTypeId = (int)familySymbol.Id.Value,
                        deletedTypeName = familySymbol.Name,
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        instancesDeleted = instanceCount,
                        remainingTypesInFamily = symbolIds.Count - 1
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get instance count for a family or family type
        /// </summary>
        public static string GetInstanceCount(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all instances
                var allInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .ToList();

                // Filter by family type if specified
                if (parameters["typeId"] != null)
                {
                    var typeIdInt = parameters["typeId"].ToObject<int>();
                    var typeId = new ElementId(typeIdInt);

                    var typeInstances = allInstances.Where(inst => inst.GetTypeId() == typeId).ToList();
                    var familySymbol = doc.GetElement(typeId) as FamilySymbol;

                    if (familySymbol == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Family type not found"
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeId = typeIdInt,
                        typeName = familySymbol.Name,
                        familyId = (int)familySymbol.Family.Id.Value,
                        familyName = familySymbol.Family.Name,
                        instanceCount = typeInstances.Count,
                        instances = typeInstances.Select(inst => new
                        {
                            instanceId = (int)inst.Id.Value,
                            hostId = inst.Host != null ? (int?)inst.Host.Id.Value : null
                        }).ToList()
                    });
                }
                // Filter by family if specified
                else if (parameters["familyId"] != null || parameters["familyName"] != null)
                {
                    Family family = null;

                    if (parameters["familyId"] != null)
                    {
                        var familyIdInt = parameters["familyId"].ToObject<int>();
                        var familyId = new ElementId(familyIdInt);
                        family = doc.GetElement(familyId) as Family;
                    }
                    else
                    {
                        string familyName = parameters["familyName"].ToString();
                        family = new FilteredElementCollector(doc)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (family == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Family not found"
                        });
                    }

                    var symbolIds = family.GetFamilySymbolIds();
                    var familyInstances = allInstances.Where(inst => symbolIds.Contains(inst.GetTypeId())).ToList();

                    // Count by type
                    var typeBreakdown = new System.Collections.Generic.List<object>();
                    foreach (var symbolId in symbolIds)
                    {
                        var symbol = doc.GetElement(symbolId) as FamilySymbol;
                        var typeCount = familyInstances.Count(inst => inst.GetTypeId() == symbolId);

                        typeBreakdown.Add(new
                        {
                            typeId = (int)symbolId.Value,
                            typeName = symbol?.Name ?? "Unknown",
                            instanceCount = typeCount
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        totalInstanceCount = familyInstances.Count,
                        typeCount = symbolIds.Count,
                        typeBreakdown
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either typeId, familyId, or familyName must be specified"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Search for families by name, category, or properties
        /// </summary>
        public static string SearchFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Search parameters
                string namePattern = parameters["namePattern"]?.ToString();
                string categoryFilter = parameters["category"]?.ToString();
                bool includeSystemFamilies = parameters["includeSystemFamilies"]?.ToObject<bool>() ?? false;
                int maxResults = parameters["maxResults"]?.ToObject<int>() ?? 100;

                // Get all families
                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>();

                // Filter by name pattern (case-insensitive partial match)
                if (!string.IsNullOrEmpty(namePattern))
                {
                    families = families.Where(f => f.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Filter by category
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    families = families.Where(f =>
                        f.FamilyCategory != null &&
                        f.FamilyCategory.Name.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    );
                }

                // Note: IsSystemFamily property not available in Revit 2026 API
                // System families are included by default

                // Take max results
                var results = families.Take(maxResults).ToList();

                // Get instance counts for each family
                var allInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .ToList();

                var searchResults = new System.Collections.Generic.List<object>();

                foreach (var family in results)
                {
                    var symbolIds = family.GetFamilySymbolIds();
                    var instanceCount = allInstances.Count(inst => symbolIds.Contains(inst.GetTypeId()));

                    searchResults.Add(new
                    {
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        category = family.FamilyCategory?.Name ?? "Unknown",
                        categoryId = family.FamilyCategory != null ? (int)family.FamilyCategory.Id.Value : 0,
                        typeCount = symbolIds.Count,
                        instanceCount,
                        isInPlace = family.IsInPlace
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    resultCount = searchResults.Count,
                    maxResults,
                    families = searchResults
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Information

        /// <summary>
        /// Gets all families in the project
        /// </summary>
        public static string GetAllFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Optional category filter
                var categoryFilter = parameters["category"]?.ToString();

                // Get all families
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>();

                // Filter by category if specified
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    collector = collector.Where(f =>
                        f.FamilyCategory != null &&
                        f.FamilyCategory.Name.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    );
                }

                var families = new System.Collections.Generic.List<object>();

                foreach (var family in collector)
                {
                    var symbolIds = family.GetFamilySymbolIds();
                    families.Add(new
                    {
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        category = family.FamilyCategory?.Name ?? "Unknown",
                        typeCount = symbolIds.Count
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyCount = families.Count,
                    categoryFilter = categoryFilter ?? "None",
                    families = families,
                    message = $"Found {families.Count} familie(s)" + (string.IsNullOrEmpty(categoryFilter) ? "" : $" in category '{categoryFilter}'")
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
        /// Gets detailed information about a family
        /// </summary>
        public static string GetFamilyInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters - can provide familyId or familyName
                var familyId = parameters["familyId"] != null ? new ElementId(int.Parse(parameters["familyId"].ToString())) : null;
                var familyName = parameters["familyName"]?.ToString();

                if (familyId == null && string.IsNullOrEmpty(familyName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either familyId or familyName is required"
                    });
                }

                Family family = null;

                // Find family by ID or name
                if (familyId != null)
                {
                    family = doc.GetElement(familyId) as Family;
                    if (family == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Family with ID {familyId.Value} not found"
                        });
                    }
                }
                else
                {
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>();

                    family = collector.FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                    if (family == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Family with name '{familyName}' not found"
                        });
                    }
                }

                // Get all types for this family
                var familySymbolIds = family.GetFamilySymbolIds();
                var types = new System.Collections.Generic.List<object>();

                foreach (var symbolId in familySymbolIds)
                {
                    var symbol = doc.GetElement(symbolId) as FamilySymbol;
                    if (symbol != null)
                    {
                        types.Add(new
                        {
                            typeId = (int)symbol.Id.Value,
                            typeName = symbol.Name,
                            isActive = symbol.IsActive
                        });
                    }
                }

                // Count instances of this family
                var instanceCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>();

                int instanceCount = instanceCollector.Count(fi => familySymbolIds.Contains(fi.GetTypeId()));

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyId = (int)family.Id.Value,
                    familyName = family.Name,
                    category = family.FamilyCategory?.Name ?? "Unknown",
                    typeCount = types.Count,
                    types = types,
                    instanceCount = instanceCount,
                    message = $"Family '{family.Name}' has {types.Count} type(s) and {instanceCount} instance(s)"
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
        /// Gets all types (symbols) for a family
        /// </summary>
        public static string GetFamilyTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters - can provide familyId or familyName
                var familyId = parameters["familyId"] != null ? new ElementId(int.Parse(parameters["familyId"].ToString())) : null;
                var familyName = parameters["familyName"]?.ToString();

                if (familyId == null && string.IsNullOrEmpty(familyName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either familyId or familyName is required"
                    });
                }

                Family family = null;

                // Find family by ID or name
                if (familyId != null)
                {
                    family = doc.GetElement(familyId) as Family;
                    if (family == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Family with ID {familyId.Value} not found"
                        });
                    }
                }
                else
                {
                    // Find by name
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>();

                    family = collector.FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                    if (family == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Family with name '{familyName}' not found"
                        });
                    }
                }

                // Get all types (symbols) for this family
                var familySymbolIds = family.GetFamilySymbolIds();
                var types = new System.Collections.Generic.List<object>();

                foreach (var symbolId in familySymbolIds)
                {
                    var symbol = doc.GetElement(symbolId) as FamilySymbol;
                    if (symbol != null)
                    {
                        types.Add(new
                        {
                            typeId = (int)symbol.Id.Value,
                            typeName = symbol.Name,
                            familyName = symbol.FamilyName,
                            isActive = symbol.IsActive,
                            category = symbol.Category?.Name ?? "Unknown"
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyId = (int)family.Id.Value,
                    familyName = family.Name,
                    typeCount = types.Count,
                    types = types,
                    message = $"Found {types.Count} type(s) in family '{family.Name}'"
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

        #region Family Type Creation and Modification

        /// <summary>
        /// Creates a new type (symbol) for a family
        /// </summary>
        public static string CreateFamilyType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["baseTypeId"] == null || parameters["newTypeName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "baseTypeId and newTypeName are required"
                    });
                }

                var baseTypeId = new ElementId(int.Parse(parameters["baseTypeId"].ToString()));
                var newTypeName = parameters["newTypeName"].ToString();

                // Get the base family symbol
                var baseSymbol = doc.GetElement(baseTypeId) as FamilySymbol;
                if (baseSymbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family type with ID {baseTypeId.Value} not found"
                    });
                }

                // Check if type name already exists
                var family = baseSymbol.Family;
                var existingSymbols = family.GetFamilySymbolIds();
                foreach (var symbolId in existingSymbols)
                {
                    var existingSymbol = doc.GetElement(symbolId) as FamilySymbol;
                    if (existingSymbol != null && existingSymbol.Name.Equals(newTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"A type with name '{newTypeName}' already exists in family '{family.Name}'"
                        });
                    }
                }

                using (var trans = new Transaction(doc, "Create Family Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Duplicate the base symbol
                    var newSymbol = baseSymbol.Duplicate(newTypeName) as FamilySymbol;

                    if (newSymbol == null)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create new family type"
                        });
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        newTypeId = (int)newSymbol.Id.Value,
                        newTypeName = newSymbol.Name,
                        familyName = newSymbol.FamilyName,
                        baseTypeId = (int)baseTypeId.Value,
                        baseTypeName = baseSymbol.Name,
                        message = $"Created new type '{newSymbol.Name}' in family '{newSymbol.FamilyName}'"
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
        /// Modifies parameters of a family type
        /// </summary>
        public static string ModifyFamilyType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["typeId"] == null || parameters["parameters"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId and parameters are required"
                    });
                }

                var typeId = new ElementId(int.Parse(parameters["typeId"].ToString()));
                var paramsToSet = parameters["parameters"] as JObject;

                if (paramsToSet == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameters must be an object with parameter name/value pairs"
                    });
                }

                // Get the family symbol
                var symbol = doc.GetElement(typeId) as FamilySymbol;
                if (symbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family type with ID {typeId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Modify Family Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modifiedParams = new System.Collections.Generic.List<object>();
                    var errors = new System.Collections.Generic.List<string>();

                    foreach (var prop in paramsToSet.Properties())
                    {
                        var paramName = prop.Name;
                        var paramValue = prop.Value.ToString();

                        var param = symbol.LookupParameter(paramName);
                        if (param == null)
                        {
                            errors.Add($"Parameter '{paramName}' not found");
                            continue;
                        }

                        if (param.IsReadOnly)
                        {
                            errors.Add($"Parameter '{paramName}' is read-only");
                            continue;
                        }

                        bool success = false;
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                success = param.Set(paramValue);
                                break;
                            case StorageType.Integer:
                                if (int.TryParse(paramValue, out int intValue))
                                    success = param.Set(intValue);
                                break;
                            case StorageType.Double:
                                if (double.TryParse(paramValue, out double doubleValue))
                                    success = param.Set(doubleValue);
                                break;
                            case StorageType.ElementId:
                                if (int.TryParse(paramValue, out int elemIdValue))
                                    success = param.Set(new ElementId(elemIdValue));
                                break;
                        }

                        if (success)
                        {
                            modifiedParams.Add(new
                            {
                                name = paramName,
                                value = param.AsValueString() ?? param.AsString() ?? paramValue
                            });
                        }
                        else
                        {
                            errors.Add($"Failed to set parameter '{paramName}' to '{paramValue}'");
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeId = (int)typeId.Value,
                        typeName = symbol.Name,
                        familyName = symbol.FamilyName,
                        modifiedCount = modifiedParams.Count,
                        modifiedParameters = modifiedParams,
                        errors = errors.Count > 0 ? errors : null,
                        message = $"Modified {modifiedParams.Count} parameter(s) on type '{symbol.Name}'" +
                                 (errors.Count > 0 ? $" ({errors.Count} error(s))" : "")
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
        /// Renames a family type
        /// </summary>
        public static string RenameFamilyType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["typeId"] == null || parameters["newName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "typeId and newName are required"
                    });
                }

                var typeId = new ElementId(int.Parse(parameters["typeId"].ToString()));
                var newName = parameters["newName"].ToString();

                // Get the family symbol
                var symbol = doc.GetElement(typeId) as FamilySymbol;
                if (symbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family type with ID {typeId.Value} not found"
                    });
                }

                // Get old name for confirmation
                var oldName = symbol.Name;
                var familyName = symbol.FamilyName;

                // Check if new name already exists in this family
                var family = symbol.Family;
                var existingSymbols = family.GetFamilySymbolIds();
                foreach (var symbolId in existingSymbols)
                {
                    var existingSymbol = doc.GetElement(symbolId) as FamilySymbol;
                    if (existingSymbol != null &&
                        existingSymbol.Id != typeId &&
                        existingSymbol.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"A type with name '{newName}' already exists in family '{familyName}'"
                        });
                    }
                }

                using (var trans = new Transaction(doc, "Rename Family Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Rename the symbol
                    symbol.Name = newName;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeId = (int)typeId.Value,
                        oldName = oldName,
                        newName = symbol.Name,
                        familyName = familyName,
                        message = $"Renamed type from '{oldName}' to '{symbol.Name}' in family '{familyName}'"
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

        #endregion

        #region Family Instances

        /// <summary>
        /// Places a family instance at a location
        /// </summary>
        public static string PlaceFamilyInstance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["familySymbolId"] == null || parameters["location"] == null || parameters["levelId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familySymbolId, location, and levelId are required"
                    });
                }

                var symbolId = new ElementId(int.Parse(parameters["familySymbolId"].ToString()));
                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));

                // Parse location (X, Y, Z coordinates)
                var locationData = parameters["location"];
                double x = double.Parse(locationData["x"].ToString());
                double y = double.Parse(locationData["y"].ToString());
                double z = double.Parse(locationData["z"]?.ToString() ?? "0");
                var location = new XYZ(x, y, z);

                // Optional rotation (in radians)
                double rotation = parameters["rotation"] != null ? double.Parse(parameters["rotation"].ToString()) : 0.0;

                // Get the family symbol
                var familySymbol = doc.GetElement(symbolId) as FamilySymbol;
                if (familySymbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family symbol with ID {symbolId.Value} not found"
                    });
                }

                // Get the level
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Level with ID {levelId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Family Instance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the symbol if not already active
                    if (!familySymbol.IsActive)
                    {
                        familySymbol.Activate();
                    }

                    // Create the family instance
                    var instance = doc.Create.NewFamilyInstance(location, familySymbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    // Apply rotation if specified
                    if (Math.Abs(rotation) > 0.0001)
                    {
                        // Rotate around Z axis at the instance location
                        var axis = Line.CreateBound(location, location + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation);
                    }

                    trans.Commit();

                    // Get instance location for confirmation
                    var instanceLocation = (instance.Location as LocationPoint)?.Point;

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        instanceId = (int)instance.Id.Value,
                        familyName = familySymbol.FamilyName,
                        typeName = familySymbol.Name,
                        level = level.Name,
                        location = new
                        {
                            x = instanceLocation?.X ?? x,
                            y = instanceLocation?.Y ?? y,
                            z = instanceLocation?.Z ?? z
                        },
                        rotation = rotation,
                        message = $"Family instance placed: {familySymbol.FamilyName} - {familySymbol.Name}"
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
        /// Gets all instances of a family or family type
        /// </summary>
        public static string GetFamilyInstances(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters - can provide familyId, typeId, or familyName
                var familyId = parameters["familyId"] != null ? new ElementId(int.Parse(parameters["familyId"].ToString())) : null;
                var typeId = parameters["typeId"] != null ? new ElementId(int.Parse(parameters["typeId"].ToString())) : null;
                var familyName = parameters["familyName"]?.ToString();

                if (familyId == null && typeId == null && string.IsNullOrEmpty(familyName))
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either familyId, typeId, or familyName is required"
                    });
                }

                // Start with all family instances
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>();

                // Filter by type ID if provided
                if (typeId != null)
                {
                    collector = collector.Where(fi => fi.GetTypeId().Equals(typeId));
                }
                // Filter by family ID if provided
                else if (familyId != null)
                {
                    var family = doc.GetElement(familyId) as Family;
                    if (family == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Family with ID {familyId.Value} not found"
                        });
                    }

                    var familySymbolIds = family.GetFamilySymbolIds();
                    collector = collector.Where(fi => familySymbolIds.Contains(fi.GetTypeId()));
                }
                // Filter by family name if provided
                else if (!string.IsNullOrEmpty(familyName))
                {
                    collector = collector.Where(fi =>
                    {
                        var symbol = doc.GetElement(fi.GetTypeId()) as FamilySymbol;
                        return symbol != null && symbol.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase);
                    });
                }

                var instances = new System.Collections.Generic.List<object>();

                foreach (var instance in collector)
                {
                    var symbol = doc.GetElement(instance.GetTypeId()) as FamilySymbol;
                    var level = doc.GetElement(instance.LevelId) as Level;
                    var locationPoint = instance.Location as LocationPoint;
                    var point = locationPoint?.Point;

                    // Get rotation in degrees (LocationPoint.Rotation is in radians)
                    double? rotationDegrees = null;
                    if (locationPoint != null)
                    {
                        rotationDegrees = locationPoint.Rotation * 180.0 / Math.PI;
                    }

                    // Get facing orientation (for directional elements like toilets, sinks)
                    XYZ facing = null;
                    XYZ hand = null;
                    try
                    {
                        facing = instance.FacingOrientation;
                        hand = instance.HandOrientation;
                    }
                    catch
                    {
                        // Some families don't have facing orientation
                    }

                    instances.Add(new
                    {
                        instanceId = (int)instance.Id.Value,
                        familyName = symbol?.FamilyName ?? "Unknown",
                        typeName = symbol?.Name ?? "Unknown",
                        typeId = (int)instance.GetTypeId().Value,
                        level = level?.Name ?? "None",
                        location = point != null ? new
                        {
                            x = point.X,
                            y = point.Y,
                            z = point.Z
                        } : null,
                        rotation = rotationDegrees,
                        facingOrientation = facing != null ? new
                        {
                            x = facing.X,
                            y = facing.Y,
                            z = facing.Z
                        } : null,
                        handOrientation = hand != null ? new
                        {
                            x = hand.X,
                            y = hand.Y,
                            z = hand.Z
                        } : null,
                        mirrored = instance.Mirrored,
                        category = instance.Category?.Name ?? "Unknown"
                    });
                }

                string searchCriteria = typeId != null ? $"type ID {typeId.Value}" :
                                       familyId != null ? $"family ID {familyId.Value}" :
                                       $"family name '{familyName}'";

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    instanceCount = instances.Count,
                    searchCriteria = searchCriteria,
                    instances = instances,
                    message = $"Found {instances.Count} instance(s) for {searchCriteria}"
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
        /// Gets information about a family instance
        /// </summary>
        public static string GetFamilyInstanceInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["instanceId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "instanceId is required"
                    });
                }

                var instanceId = new ElementId(int.Parse(parameters["instanceId"].ToString()));

                // Get the family instance
                var instance = doc.GetElement(instanceId) as FamilyInstance;
                if (instance == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family instance with ID {instanceId.Value} not found"
                    });
                }

                // Get type and family info
                var symbol = doc.GetElement(instance.GetTypeId()) as FamilySymbol;
                var familyName = symbol?.FamilyName ?? "Unknown";
                var typeName = symbol?.Name ?? "Unknown";

                // Get level info
                var level = doc.GetElement(instance.LevelId) as Level;
                var levelName = level?.Name ?? "None";

                // Get location and rotation
                var locationPoint = instance.Location as LocationPoint;
                var location = locationPoint?.Point;
                var rotation = locationPoint?.Rotation ?? 0.0;

                // Get host info if hosted
                string hostInfo = "None";
                if (instance.Host != null)
                {
                    hostInfo = $"{instance.Host.Category?.Name ?? "Unknown"} (ID: {instance.Host.Id.Value})";
                }

                // Get parameter count
                int paramCount = instance.Parameters.Size;

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    instanceId = (int)instanceId.Value,
                    familyName = familyName,
                    typeName = typeName,
                    typeId = symbol?.Id.Value,
                    category = instance.Category?.Name ?? "Unknown",
                    level = levelName,
                    levelId = level?.Id.Value,
                    location = location != null ? new
                    {
                        x = location.X,
                        y = location.Y,
                        z = location.Z
                    } : null,
                    rotation = rotation,
                    rotationDegrees = rotation * (180.0 / Math.PI),
                    host = hostInfo,
                    parameterCount = paramCount,
                    message = $"Instance of {familyName} - {typeName} on level '{levelName}'"
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
        /// Modifies properties of a family instance
        /// </summary>
        public static string ModifyFamilyInstance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["instanceId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "instanceId is required"
                    });
                }

                var instanceId = new ElementId(int.Parse(parameters["instanceId"].ToString()));

                // Get the family instance
                var instance = doc.GetElement(instanceId) as FamilyInstance;
                if (instance == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family instance with ID {instanceId.Value} not found"
                    });
                }

                using (var trans = new Transaction(doc, "Modify Family Instance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modifications = new System.Collections.Generic.List<string>();

                    // Modify location if provided
                    if (parameters["location"] != null)
                    {
                        var locationData = parameters["location"];
                        double x = double.Parse(locationData["x"].ToString());
                        double y = double.Parse(locationData["y"].ToString());
                        double z = double.Parse(locationData["z"]?.ToString() ?? "0");
                        var newLocation = new XYZ(x, y, z);

                        var locationPoint = instance.Location as LocationPoint;
                        if (locationPoint != null)
                        {
                            locationPoint.Point = newLocation;
                            modifications.Add($"Location updated to ({x:F2}, {y:F2}, {z:F2})");
                        }
                    }

                    // Modify rotation if provided (in radians)
                    if (parameters["rotation"] != null)
                    {
                        double rotation = double.Parse(parameters["rotation"].ToString());
                        var locationPoint = instance.Location as LocationPoint;

                        if (locationPoint != null && Math.Abs(rotation) > 0.0001)
                        {
                            var axis = Line.CreateBound(locationPoint.Point, locationPoint.Point + XYZ.BasisZ);
                            locationPoint.Rotate(axis, rotation);
                            modifications.Add($"Rotation applied: {rotation} radians");
                        }
                    }

                    trans.Commit();

                    // Get current location for confirmation
                    var currentLocation = (instance.Location as LocationPoint)?.Point;
                    var symbol = doc.GetElement(instance.GetTypeId()) as FamilySymbol;

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        instanceId = (int)instanceId.Value,
                        familyName = symbol?.FamilyName ?? "Unknown",
                        typeName = symbol?.Name ?? "Unknown",
                        modifications = modifications,
                        currentLocation = currentLocation != null ? new
                        {
                            x = currentLocation.X,
                            y = currentLocation.Y,
                            z = currentLocation.Z
                        } : null,
                        message = $"Modified instance: {string.Join(", ", modifications)}"
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
        /// Changes the type of a family instance
        /// </summary>
        public static string ChangeFamilyInstanceType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["instanceId"] == null || parameters["newTypeId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "instanceId and newTypeId are required"
                    });
                }

                var instanceId = new ElementId(int.Parse(parameters["instanceId"].ToString()));
                var newTypeId = new ElementId(int.Parse(parameters["newTypeId"].ToString()));

                // Get the family instance
                var instance = doc.GetElement(instanceId) as FamilyInstance;
                if (instance == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family instance with ID {instanceId.Value} not found"
                    });
                }

                // Get the new family symbol
                var newSymbol = doc.GetElement(newTypeId) as FamilySymbol;
                if (newSymbol == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family type with ID {newTypeId.Value} not found"
                    });
                }

                // Get old type info before changing
                var oldSymbol = doc.GetElement(instance.GetTypeId()) as FamilySymbol;
                var oldTypeName = oldSymbol?.Name ?? "Unknown";
                var oldFamilyName = oldSymbol?.FamilyName ?? "Unknown";

                using (var trans = new Transaction(doc, "Change Family Instance Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate new symbol if not already active
                    if (!newSymbol.IsActive)
                    {
                        newSymbol.Activate();
                    }

                    // Change the type
                    instance.ChangeTypeId(newTypeId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        instanceId = (int)instanceId.Value,
                        oldTypeId = oldSymbol?.Id.Value,
                        oldTypeName = oldTypeName,
                        oldFamilyName = oldFamilyName,
                        newTypeId = (int)newTypeId.Value,
                        newTypeName = newSymbol.Name,
                        newFamilyName = newSymbol.FamilyName,
                        message = $"Changed instance type from '{oldFamilyName} - {oldTypeName}' to '{newSymbol.FamilyName} - {newSymbol.Name}'"
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
        /// Deletes a family instance
        /// </summary>
        public static string DeleteFamilyInstance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["instanceId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "instanceId is required"
                    });
                }

                var instanceId = new ElementId(int.Parse(parameters["instanceId"].ToString()));

                // Get the family instance
                var instance = doc.GetElement(instanceId) as FamilyInstance;
                if (instance == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family instance with ID {instanceId.Value} not found"
                    });
                }

                // Get instance info before deletion
                var symbol = doc.GetElement(instance.GetTypeId()) as FamilySymbol;
                var familyName = symbol?.FamilyName ?? "Unknown";
                var typeName = symbol?.Name ?? "Unknown";
                var location = (instance.Location as LocationPoint)?.Point;

                using (var trans = new Transaction(doc, "Delete Family Instance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete the instance
                    doc.Delete(instanceId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedInstanceId = (int)instanceId.Value,
                        familyName = familyName,
                        typeName = typeName,
                        location = location != null ? new
                        {
                            x = location.X,
                            y = location.Y,
                            z = location.Z
                        } : null,
                        message = $"Deleted instance of {familyName} - {typeName}"
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

        #endregion

        #region Family Parameters

        /// <summary>
        /// Gets all parameters for a family or family type
        /// </summary>
        public static string GetFamilyParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters - can provide elementId (for type or instance)
                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required (can be family type ID or instance ID)"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));

                // Get the element
                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementId.Value} not found"
                    });
                }

                var paramList = new System.Collections.Generic.List<object>();

                // Get all parameters for the element
                foreach (Parameter param in element.Parameters)
                {
                    // Get parameter value as string
                    string value = null;
                    if (param.HasValue)
                    {
                        value = param.AsValueString() ?? param.AsString();
                    }

                    paramList.Add(new
                    {
                        name = param.Definition.Name,
                        value = value,
                        storageType = param.StorageType.ToString(),
                        isReadOnly = param.IsReadOnly,
                        isShared = param.IsShared
                    });
                }

                // Get element name for context
                string elementName = "Unknown";
                if (element is FamilySymbol symbol)
                {
                    elementName = $"{symbol.FamilyName} - {symbol.Name}";
                }
                else if (element is FamilyInstance instance)
                {
                    var instSymbol = doc.GetElement(instance.GetTypeId()) as FamilySymbol;
                    elementName = $"{instSymbol?.FamilyName ?? "Unknown"} - Instance {instance.Id.Value}";
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)elementId.Value,
                    elementName = elementName,
                    parameterCount = paramList.Count,
                    parameters = paramList,
                    message = $"Retrieved {paramList.Count} parameter(s) from {elementName}"
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
        /// Sets parameter values for a family type or instance
        /// </summary>
        public static string SetFamilyParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                if (parameters["elementId"] == null || parameters["parameterName"] == null || parameters["value"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId, parameterName, and value are required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var parameterName = parameters["parameterName"].ToString();
                var value = parameters["value"].ToString();

                // Get the element (can be FamilySymbol or FamilyInstance)
                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementId.Value} not found"
                    });
                }

                // Find the parameter by name
                Parameter param = element.LookupParameter(parameterName);

                if (param == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Parameter '{parameterName}' not found on element"
                    });
                }

                // Check if parameter is read-only
                if (param.IsReadOnly)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Parameter '{parameterName}' is read-only"
                    });
                }

                using (var trans = new Transaction(doc, "Set Family Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    bool setSuccess = false;

                    // Set parameter value based on storage type
                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            setSuccess = param.Set(value);
                            break;

                        case StorageType.Integer:
                            if (int.TryParse(value, out int intValue))
                            {
                                setSuccess = param.Set(intValue);
                            }
                            else
                            {
                                trans.RollBack();
                                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = $"Cannot convert '{value}' to integer for parameter '{parameterName}'"
                                });
                            }
                            break;

                        case StorageType.Double:
                            if (double.TryParse(value, out double doubleValue))
                            {
                                setSuccess = param.Set(doubleValue);
                            }
                            else
                            {
                                trans.RollBack();
                                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = $"Cannot convert '{value}' to double for parameter '{parameterName}'"
                                });
                            }
                            break;

                        case StorageType.ElementId:
                            if (int.TryParse(value, out int elemIdValue))
                            {
                                setSuccess = param.Set(new ElementId(elemIdValue));
                            }
                            else
                            {
                                trans.RollBack();
                                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = $"Cannot convert '{value}' to ElementId for parameter '{parameterName}'"
                                });
                            }
                            break;

                        default:
                            trans.RollBack();
                            return Newtonsoft.Json.JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Unsupported parameter storage type: {param.StorageType}"
                            });
                    }

                    if (!setSuccess)
                    {
                        trans.RollBack();
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Failed to set parameter '{parameterName}' to value '{value}'"
                        });
                    }

                    trans.Commit();

                    // Get the actual value after setting (for confirmation)
                    string actualValue = param.AsValueString() ?? param.AsString() ?? value;

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)elementId.Value,
                        parameterName = parameterName,
                        setValue = value,
                        actualValue = actualValue,
                        storageType = param.StorageType.ToString(),
                        message = $"Parameter '{parameterName}' set to '{actualValue}'"
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

        #endregion

        #region Family Categories

        /// <summary>
        /// Gets the category of a family
        /// </summary>
        public static string GetFamilyCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["familyId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familyId parameter is required"
                    });
                }

                // Parse family ID
                if (!int.TryParse(parameters["familyId"].ToString(), out int familyIdInt))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid familyId format"
                    });
                }

                var familyId = new ElementId(familyIdInt);
                var family = doc.GetElement(familyId) as Family;

                if (family == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family with ID {familyIdInt} not found"
                    });
                }

                // Get family category
                var category = family.FamilyCategory;

                if (category == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family does not have a category"
                    });
                }

                // Get parent category if it exists
                var parentCategory = category.Parent;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyId = familyIdInt,
                    familyName = family.Name,
                    category = new
                    {
                        id = (int)category.Id.Value,
                        name = category.Name,
                        builtInCategory = category.BuiltInCategory.ToString(),
                        hasParent = parentCategory != null,
                        parentCategory = parentCategory != null ? new
                        {
                            id = (int)parentCategory.Id.Value,
                            name = parentCategory.Name
                        } : null,
                        allowsBoundParameters = category.AllowsBoundParameters,
                        isCuttable = category.IsCuttable
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all families in a specific category
        /// </summary>
        public static string GetFamiliesByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["categoryName"] == null && parameters["categoryId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either categoryName or categoryId parameter is required"
                    });
                }

                Category targetCategory = null;

                // Try to find category by ID first
                if (parameters["categoryId"] != null)
                {
                    if (int.TryParse(parameters["categoryId"].ToString(), out int categoryIdInt))
                    {
                        var categoryId = new ElementId(categoryIdInt);
                        targetCategory = Category.GetCategory(doc, categoryId);
                    }
                }

                // If not found by ID, try by name
                if (targetCategory == null && parameters["categoryName"] != null)
                {
                    string categoryName = parameters["categoryName"].ToString();

                    // Try to parse as BuiltInCategory
                    if (Enum.TryParse<BuiltInCategory>(categoryName, out BuiltInCategory builtInCat))
                    {
                        targetCategory = Category.GetCategory(doc, builtInCat);
                    }
                    else
                    {
                        // Search by name in all categories
                        foreach (Category cat in doc.Settings.Categories)
                        {
                            if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                            {
                            targetCategory = cat;
                                break;
                            }
                        }
                    }
                }

                if (targetCategory == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Category not found"
                    });
                }

                // Get all families in this category
                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => f.FamilyCategory != null && f.FamilyCategory.Id == targetCategory.Id)
                    .Select(f => new
                    {
                        id = (int)f.Id.Value,
                        name = f.Name,
                        familyCategory = f.FamilyCategory?.Name,
                        categoryId = f.FamilyCategory?.Id.Value != null ? (int)f.FamilyCategory.Id.Value : 0,
                        typeCount = f.GetFamilySymbolIds().Count
                    })
                    .OrderBy(f => f.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = new
                    {
                        id = (int)targetCategory.Id.Value,
                        name = targetCategory.Name
                    },
                    familyCount = families.Count,
                    families
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region In-Place Families

        /// <summary>
        /// Creates an in-place family element
        /// </summary>
        public static string CreateInPlaceFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["categoryId"] == null || parameters["familyName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categoryId and familyName are required"
                    });
                }

                var categoryIdInt = parameters["categoryId"].ToObject<int>();
                string familyName = parameters["familyName"].ToString();

                // Get category
                var categoryId = new ElementId(categoryIdInt);
                var category = Category.GetCategory(doc, categoryId);

                if (category == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Category not found"
                    });
                }

                // Note: Creating in-place families programmatically requires entering family edit mode
                // which is a complex workflow involving SubTransactions and family editing context
                // This is typically done through UI commands like PostCommand(RevitCommandId.NewInplaceComponent)

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "In-place family creation requires complex family editing workflow. " +
                            "This operation is better performed through the Revit UI using Edit In-Place mode. " +
                            "Consider using LoadFamily for regular families instead.",
                    categoryId = (int)category.Id.Value,
                    categoryName = category.Name,
                    suggestion = "Use Revit UI: Architecture > Component > Model In-Place, or use PostCommand(RevitCommandId.NewInplaceComponent)"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Document Editing

        /// <summary>
        /// Opens a family document for editing
        /// </summary>
        public static string OpenFamilyDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Option 1: Open from file path
                if (parameters["familyPath"] != null)
                {
                    string familyPath = parameters["familyPath"].ToString();

                    if (!System.IO.File.Exists(familyPath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Family file not found at path: " + familyPath
                        });
                    }

                    // Open family document
                    var familyDoc = uiApp.Application.OpenDocumentFile(familyPath);

                    if (familyDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to open family document"
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        documentTitle = familyDoc.Title,
                        documentPath = familyDoc.PathName,
                        isFamilyDocument = familyDoc.IsFamilyDocument,
                        isModifiable = !familyDoc.IsReadOnly,
                        message = "Family document opened. Use Revit UI or API to edit the family."
                    });
                }
                // Option 2: Extract from project (EditFamily)
                else if (parameters["familyId"] != null)
                {
                    var familyIdInt = parameters["familyId"].ToObject<int>();
                    var familyId = new ElementId(familyIdInt);
                    var family = doc.GetElement(familyId) as Family;

                    if (family == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Family not found in project"
                        });
                    }

                    // Open family for editing (EditFamily)
                    var familyDoc = doc.EditFamily(family);

                    if (familyDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Unable to open family document. Family may be in-place or system family."
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        familyId = familyIdInt,
                        familyName = family.Name,
                        documentTitle = familyDoc.Title,
                        isFamilyDocument = familyDoc.IsFamilyDocument,
                        isModifiable = !familyDoc.IsReadOnly,
                        message = "Family document opened from project. Use CloseFamilyDocument to close it."
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either familyPath or familyId is required"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Saves changes to a family document
        /// </summary>
        public static string SaveFamilyDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Note: This method operates on the active document
                // If a family document was opened, it should be the active document
                var doc = uiApp.ActiveUIDocument?.Document;

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document"
                    });
                }

                // SaveAs option
                if (parameters["saveAsPath"] != null)
                {
                    string saveAsPath = parameters["saveAsPath"].ToString();

                    var saveAsOptions = new SaveAsOptions();
                    saveAsOptions.OverwriteExistingFile = parameters["overwrite"]?.ToObject<bool>() ?? false;

                    doc.SaveAs(saveAsPath, saveAsOptions);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        savedPath = saveAsPath,
                        documentTitle = doc.Title,
                        message = "Family document saved to new location"
                    });
                }
                // Regular save
                else
                {
                    if (doc.IsModified)
                    {
                        doc.Save();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            documentTitle = doc.Title,
                            documentPath = doc.PathName,
                            message = "Family document saved"
                        });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            documentTitle = doc.Title,
                            message = "No changes to save"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Closes a family document
        /// </summary>
        public static string CloseFamilyDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document"
                    });
                }

                bool save = parameters["save"]?.ToObject<bool>() ?? false;

                string documentTitle = doc.Title;
                bool wasModified = doc.IsModified;

                // Close the document
                doc.Close(save);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentTitle,
                    saved = save && wasModified,
                    message = save ? "Family document closed and saved" : "Family document closed without saving"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Purging and Cleanup

        /// <summary>
        /// Removes unused families from the project
        /// </summary>
        public static string PurgeUnusedFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Optional category filter
                Category targetCategory = null;
                if (parameters["categoryName"] != null)
                {
                    string categoryName = parameters["categoryName"].ToString();

                    if (Enum.TryParse<BuiltInCategory>(categoryName, out BuiltInCategory builtInCat))
                    {
                        targetCategory = Category.GetCategory(doc, builtInCat);
                    }
                    else
                    {
                        foreach (Category cat in doc.Settings.Categories)
                        {
                            if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetCategory = cat;
                                break;
                            }
                        }
                    }
                }

                // Get all families
                var allFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .ToList();

                // Filter by category if specified
                if (targetCategory != null)
                {
                    allFamilies = allFamilies
                        .Where(f => f.FamilyCategory != null && f.FamilyCategory.Id == targetCategory.Id)
                        .ToList();
                }

                // Get all family instances
                var allInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .ToList();

                // Find families with no instances
                var unusedFamilies = new List<object>();
                var deletedFamilies = new List<object>();

                using (var trans = new Transaction(doc, "Purge Unused Families"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var family in allFamilies)
                    {
                        // Get all types (symbols) for this family
                        var symbolIds = family.GetFamilySymbolIds();

                        // Check if any instance uses any of this family's types
                        bool hasInstances = allInstances.Any(inst => symbolIds.Contains(inst.GetTypeId()));

                        if (!hasInstances)
                        {
                            unusedFamilies.Add(new
                            {
                                id = (int)family.Id.Value,
                                name = family.Name,
                                category = family.FamilyCategory?.Name
                            });

                            try
                            {
                                doc.Delete(family.Id);
                                deletedFamilies.Add(new
                                {
                                    id = (int)family.Id.Value,
                                    name = family.Name,
                                    category = family.FamilyCategory?.Name
                                });
                            }
                            catch (Exception)
                            {
                                // Some families might not be deletable
                                // Continue with the rest
                            }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    unusedFamiliesFound = unusedFamilies.Count,
                    familiesDeleted = deletedFamilies.Count,
                    categoryFilter = targetCategory?.Name,
                    deletedFamilies
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Removes unused family types
        /// </summary>
        public static string PurgeUnusedTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Optional family filter
                Family targetFamily = null;
                if (parameters["familyId"] != null)
                {
                    if (int.TryParse(parameters["familyId"].ToString(), out int familyIdInt))
                    {
                        var familyId = new ElementId(familyIdInt);
                        targetFamily = doc.GetElement(familyId) as Family;

                        if (targetFamily == null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Family with ID {familyIdInt} not found"
                            });
                        }
                    }
                }

                // Get all family instances to check which types are used
                var allInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Select(inst => inst.GetTypeId())
                    .ToHashSet();

                var deletedTypes = new List<object>();
                var skippedTypes = new List<object>();

                using (var trans = new Transaction(doc, "Purge Unused Types"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (targetFamily != null)
                    {
                        // Purge only from the specified family
                        var symbolIds = targetFamily.GetFamilySymbolIds();

                        foreach (var symbolId in symbolIds)
                        {
                            var symbol = doc.GetElement(symbolId) as FamilySymbol;
                            if (symbol == null) continue;

                            // Check if this type is used by any instance
                            if (!allInstances.Contains(symbolId))
                            {
                                // Check if this is the last type in the family
                                if (symbolIds.Count <= 1)
                                {
                                    skippedTypes.Add(new
                                    {
                                        id = (int)symbolId.Value,
                                        name = symbol.Name,
                                        familyName = symbol.FamilyName,
                                        reason = "Cannot delete last type in family"
                                    });
                                    continue;
                                }

                                try
                                {
                                    doc.Delete(symbolId);
                                    deletedTypes.Add(new
                                    {
                                        id = (int)symbolId.Value,
                                        name = symbol.Name,
                                        familyName = symbol.FamilyName
                                    });
                                }
                                catch (Exception ex)
                                {
                                    skippedTypes.Add(new
                                    {
                                        id = (int)symbolId.Value,
                                        name = symbol.Name,
                                        familyName = symbol.FamilyName,
                                        reason = ex.Message
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        // Purge from all families
                        var allFamilies = new FilteredElementCollector(doc)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .ToList();

                        foreach (var family in allFamilies)
                        {
                            var symbolIds = family.GetFamilySymbolIds();

                            foreach (var symbolId in symbolIds)
                            {
                                var symbol = doc.GetElement(symbolId) as FamilySymbol;
                                if (symbol == null) continue;

                                // Check if this type is used
                                if (!allInstances.Contains(symbolId))
                                {
                                    // Don't delete if it's the last type in family
                                    if (symbolIds.Count <= 1)
                                    {
                                        continue;
                                    }

                                    try
                                    {
                                        doc.Delete(symbolId);
                                        deletedTypes.Add(new
                                        {
                                            id = (int)symbolId.Value,
                                            name = symbol.Name,
                                            familyName = symbol.FamilyName
                                        });
                                    }
                                    catch (Exception)
                                    {
                                        // Some types might not be deletable, skip
                                    }
                                }
                            }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typesDeleted = deletedTypes.Count,
                    typesSkipped = skippedTypes.Count,
                    familyFilter = targetFamily?.Name,
                    deletedTypes,
                    skippedTypes
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
        /// Checks if a family is loaded in the project
        /// </summary>
        public static string IsFamilyLoaded(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["familyName"] == null && parameters["familyPath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either familyName or familyPath parameter is required"
                    });
                }

                string searchName = null;

                // If familyPath is provided, extract family name from path
                if (parameters["familyPath"] != null)
                {
                    string familyPath = parameters["familyPath"].ToString();
                    searchName = System.IO.Path.GetFileNameWithoutExtension(familyPath);
                }
                // Otherwise use provided familyName
                else if (parameters["familyName"] != null)
                {
                    searchName = parameters["familyName"].ToString();
                }

                // Search for the family
                var matchingFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => f.Name.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingFamilies.Count > 0)
                {
                    var family = matchingFamilies[0];
                    var symbolIds = family.GetFamilySymbolIds();

                    // Get instance count
                    var instances = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .Where(inst => symbolIds.Contains(inst.GetTypeId()))
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        isLoaded = true,
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        category = family.FamilyCategory?.Name,
                        typeCount = symbolIds.Count,
                        instanceCount = instances.Count,
                        types = symbolIds.Select(id =>
                        {
                            var symbol = doc.GetElement(id) as FamilySymbol;
                            return new
                            {
                                id = (int)id.Value,
                                name = symbol?.Name
                            };
                        }).ToList()
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        isLoaded = false,
                        searchedName = searchName,
                        message = $"Family '{searchName}' is not loaded in the project"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Transfer Between Documents

        /// <summary>
        /// Compares families between source and target documents to find missing families
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing sourceDocumentTitle (optional), targetDocumentTitle (optional), category (optional)</param>
        /// <returns>JSON response with list of missing families</returns>
        public static string GetMissingFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                // Get source and target documents
                string sourceTitle = parameters["sourceDocumentTitle"]?.ToString();
                string targetTitle = parameters["targetDocumentTitle"]?.ToString();
                string categoryFilter = parameters["category"]?.ToString();

                Document sourceDoc = null;
                Document targetDoc = null;

                // Find documents by title
                foreach (Document doc in app.Documents)
                {
                    if (!string.IsNullOrEmpty(sourceTitle) && doc.Title.Equals(sourceTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceDoc = doc;
                    }
                    if (!string.IsNullOrEmpty(targetTitle) && doc.Title.Equals(targetTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDoc = doc;
                    }

                    // If no titles provided, use active as target and first other doc as source
                    if (string.IsNullOrEmpty(sourceTitle) && string.IsNullOrEmpty(targetTitle))
                    {
                        if (doc.Equals(uiApp.ActiveUIDocument.Document))
                        {
                            targetDoc = doc;
                        }
                        else if (sourceDoc == null)
                        {
                            sourceDoc = doc;
                        }
                    }
                }

                if (sourceDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source document not found. Please specify sourceDocumentTitle or ensure two documents are open."
                    });
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Target document not found. Please specify targetDocumentTitle or ensure two documents are open."
                    });
                }

                // Get all families from source document
                var sourceFamilies = new FilteredElementCollector(sourceDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>();

                // Filter by category if specified
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    sourceFamilies = sourceFamilies.Where(f =>
                        f.FamilyCategory != null &&
                        f.FamilyCategory.Name.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    );
                }

                // Get all family names from target document
                var targetFamilyNames = new FilteredElementCollector(targetDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Select(f => f.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Find missing families
                var missingFamilies = new List<object>();
                var existingFamilies = new List<object>();

                foreach (var family in sourceFamilies)
                {
                    var familyInfo = new
                    {
                        familyId = (int)family.Id.Value,
                        familyName = family.Name,
                        category = family.FamilyCategory?.Name ?? "Unknown",
                        categoryId = family.FamilyCategory != null ? (int)family.FamilyCategory.Id.Value : 0,
                        typeCount = family.GetFamilySymbolIds().Count,
                        isInPlace = family.IsInPlace,
                        isEditable = family.IsEditable
                    };

                    if (!targetFamilyNames.Contains(family.Name))
                    {
                        missingFamilies.Add(familyInfo);
                    }
                    else
                    {
                        existingFamilies.Add(familyInfo);
                    }
                }

                // Group missing families by category
                var missingByCategory = missingFamilies
                    .GroupBy(f => ((dynamic)f).category.ToString())
                    .Select(g => new
                    {
                        category = g.Key,
                        count = g.Count(),
                        families = g.ToList()
                    })
                    .OrderBy(g => g.category)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceDocument = sourceDoc.Title,
                    targetDocument = targetDoc.Title,
                    categoryFilter = categoryFilter ?? "All",
                    totalSourceFamilies = sourceFamilies.Count(),
                    missingCount = missingFamilies.Count,
                    existingCount = existingFamilies.Count,
                    missingByCategory,
                    missingFamilies
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Transfers a family from source document to target document by extracting and loading
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing familyName, sourceDocumentTitle (optional), targetDocumentTitle (optional)</param>
        /// <returns>JSON response with transfer status</returns>
        public static string TransferFamilyToDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                // Validate required parameters
                if (parameters["familyName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familyName parameter is required"
                    });
                }

                string familyName = parameters["familyName"].ToString();
                string sourceTitle = parameters["sourceDocumentTitle"]?.ToString();
                string targetTitle = parameters["targetDocumentTitle"]?.ToString();
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

                Document sourceDoc = null;
                Document targetDoc = null;

                // Find documents by title
                foreach (Document doc in app.Documents)
                {
                    if (!string.IsNullOrEmpty(sourceTitle) && doc.Title.Equals(sourceTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceDoc = doc;
                    }
                    if (!string.IsNullOrEmpty(targetTitle) && doc.Title.Equals(targetTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDoc = doc;
                    }

                    // If no titles provided, use active as target and first other doc as source
                    if (string.IsNullOrEmpty(sourceTitle) && string.IsNullOrEmpty(targetTitle))
                    {
                        if (doc.Equals(uiApp.ActiveUIDocument.Document))
                        {
                            targetDoc = doc;
                        }
                        else if (sourceDoc == null)
                        {
                            sourceDoc = doc;
                        }
                    }
                }

                if (sourceDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source document not found"
                    });
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Target document not found"
                    });
                }

                // Find the family in source document
                Family sourceFamily = new FilteredElementCollector(sourceDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (sourceFamily == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family '{familyName}' not found in source document"
                    });
                }

                // Check if family is editable (can be extracted)
                if (!sourceFamily.IsEditable)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family '{familyName}' is not editable and cannot be extracted"
                    });
                }

                // Create temporary file path
                string tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"{familyName}_{DateTime.Now:yyyyMMddHHmmss}.rfa"
                );

                // Open family document and save it to temp file
                Document familyDoc = sourceDoc.EditFamily(sourceFamily);
                if (familyDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Could not open family '{familyName}' for editing"
                    });
                }

                try
                {
                    // Save family to temp file
                    SaveAsOptions saveOptions = new SaveAsOptions();
                    saveOptions.OverwriteExistingFile = true;
                    familyDoc.SaveAs(tempPath, saveOptions);
                    familyDoc.Close(false);

                    // Load family into target document
                    Family loadedFamily = null;
                    bool wasLoaded = false;

                    using (var trans = new Transaction(targetDoc, $"Transfer Family {familyName}"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        if (overwrite)
                        {
                            wasLoaded = targetDoc.LoadFamily(tempPath, new FamilyLoadOptions(), out loadedFamily);
                        }
                        else
                        {
                            wasLoaded = targetDoc.LoadFamily(tempPath, out loadedFamily);
                        }

                        if (wasLoaded && loadedFamily != null)
                        {
                            trans.Commit();
                        }
                        else
                        {
                            trans.RollBack();
                        }
                    }

                    // Clean up temp file
                    if (System.IO.File.Exists(tempPath))
                    {
                        System.IO.File.Delete(tempPath);
                    }

                    if (!wasLoaded || loadedFamily == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Failed to load family into target document. Family may already exist (use overwrite: true to replace)."
                        });
                    }

                    // Get type information
                    var symbolIds = loadedFamily.GetFamilySymbolIds();
                    var types = symbolIds.Select(id =>
                    {
                        var symbol = targetDoc.GetElement(id) as FamilySymbol;
                        return new
                        {
                            typeId = (int)id.Value,
                            typeName = symbol?.Name ?? "Unknown"
                        };
                    }).ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        familyName = loadedFamily.Name,
                        familyId = (int)loadedFamily.Id.Value,
                        category = loadedFamily.FamilyCategory?.Name,
                        typeCount = symbolIds.Count,
                        types,
                        sourceDocument = sourceDoc.Title,
                        targetDocument = targetDoc.Title,
                        message = $"Family '{familyName}' transferred successfully with {symbolIds.Count} type(s)"
                    });
                }
                catch (Exception)
                {
                    // Close family document if still open
                    if (familyDoc != null && familyDoc.IsValidObject)
                    {
                        familyDoc.Close(false);
                    }

                    // Clean up temp file
                    if (System.IO.File.Exists(tempPath))
                    {
                        System.IO.File.Delete(tempPath);
                    }

                    throw;
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch transfers multiple families from source to target document
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing familyNames (array), sourceDocumentTitle (optional), targetDocumentTitle (optional), category (optional)</param>
        /// <returns>JSON response with transfer status for each family</returns>
        public static string BatchTransferFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                string sourceTitle = parameters["sourceDocumentTitle"]?.ToString();
                string targetTitle = parameters["targetDocumentTitle"]?.ToString();
                string categoryFilter = parameters["category"]?.ToString();
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;
                bool transferMissing = parameters["transferMissing"]?.ToObject<bool>() ?? false;
                var familyNames = parameters["familyNames"]?.ToObject<List<string>>();

                Document sourceDoc = null;
                Document targetDoc = null;

                // Find documents
                foreach (Document doc in app.Documents)
                {
                    if (!string.IsNullOrEmpty(sourceTitle) && doc.Title.Equals(sourceTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceDoc = doc;
                    }
                    if (!string.IsNullOrEmpty(targetTitle) && doc.Title.Equals(targetTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDoc = doc;
                    }

                    if (string.IsNullOrEmpty(sourceTitle) && string.IsNullOrEmpty(targetTitle))
                    {
                        if (doc.Equals(uiApp.ActiveUIDocument.Document))
                        {
                            targetDoc = doc;
                        }
                        else if (sourceDoc == null)
                        {
                            sourceDoc = doc;
                        }
                    }
                }

                if (sourceDoc == null || targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source or target document not found"
                    });
                }

                // If transferMissing is true, get all missing families
                if (transferMissing)
                {
                    var targetFamilyNames = new FilteredElementCollector(targetDoc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .Select(f => f.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var sourceFamilies = new FilteredElementCollector(sourceDoc)
                        .OfClass(typeof(Family))
                        .Cast<Family>();

                    if (!string.IsNullOrEmpty(categoryFilter))
                    {
                        sourceFamilies = sourceFamilies.Where(f =>
                            f.FamilyCategory != null &&
                            f.FamilyCategory.Name.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) >= 0
                        );
                    }

                    familyNames = sourceFamilies
                        .Where(f => !targetFamilyNames.Contains(f.Name) && f.IsEditable)
                        .Select(f => f.Name)
                        .ToList();
                }

                if (familyNames == null || familyNames.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "No families to transfer",
                        transferred = 0,
                        failed = 0
                    });
                }

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var name in familyNames)
                {
                    try
                    {
                        // Find family in source
                        Family sourceFamily = new FilteredElementCollector(sourceDoc)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                        if (sourceFamily == null)
                        {
                            results.Add(new { familyName = name, success = false, error = "Family not found in source" });
                            failCount++;
                            continue;
                        }

                        if (!sourceFamily.IsEditable)
                        {
                            results.Add(new { familyName = name, success = false, error = "Family not editable" });
                            failCount++;
                            continue;
                        }

                        // Extract and transfer
                        string tempPath = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            $"{name}_{DateTime.Now:yyyyMMddHHmmssfff}.rfa"
                        );

                        Document familyDoc = sourceDoc.EditFamily(sourceFamily);
                        if (familyDoc == null)
                        {
                            results.Add(new { familyName = name, success = false, error = "Could not open family" });
                            failCount++;
                            continue;
                        }

                        SaveAsOptions saveOptions = new SaveAsOptions();
                        saveOptions.OverwriteExistingFile = true;
                        familyDoc.SaveAs(tempPath, saveOptions);
                        familyDoc.Close(false);

                        // Load into target
                        Family loadedFamily = null;
                        bool wasLoaded = false;

                        using (var trans = new Transaction(targetDoc, $"Transfer Family {name}"))
                        {
                            trans.Start();
                            var failureOptions = trans.GetFailureHandlingOptions();
                            failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                            trans.SetFailureHandlingOptions(failureOptions);

                            if (overwrite)
                            {
                                wasLoaded = targetDoc.LoadFamily(tempPath, new FamilyLoadOptions(), out loadedFamily);
                            }
                            else
                            {
                                wasLoaded = targetDoc.LoadFamily(tempPath, out loadedFamily);
                            }

                            if (wasLoaded && loadedFamily != null)
                            {
                                trans.Commit();
                                results.Add(new
                                {
                                    familyName = name,
                                    success = true,
                                    familyId = (int)loadedFamily.Id.Value,
                                    typeCount = loadedFamily.GetFamilySymbolIds().Count
                                });
                                successCount++;
                            }
                            else
                            {
                                trans.RollBack();
                                results.Add(new { familyName = name, success = false, error = "Failed to load family" });
                                failCount++;
                            }
                        }

                        // Cleanup
                        if (System.IO.File.Exists(tempPath))
                        {
                            System.IO.File.Delete(tempPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { familyName = name, success = false, error = ex.Message });
                        failCount++;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceDocument = sourceDoc.Title,
                    targetDocument = targetDoc.Title,
                    totalRequested = familyNames.Count,
                    transferred = successCount,
                    failed = failCount,
                    results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Label Editing

        /// <summary>
        /// Gets all text labels from a family document (must be active document)
        /// </summary>
        public static string GetFamilyLabels(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document. Use OpenFamilyDocument first."
                    });
                }

                var labels = new List<object>();

                // Get all TextNote elements (labels in family)
                var textNotes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .ToList();

                foreach (var textNote in textNotes)
                {
                    var location = textNote.Coord;
                    labels.Add(new
                    {
                        id = (int)textNote.Id.Value,
                        text = textNote.Text,
                        textTypeId = (int)textNote.GetTypeId().Value,
                        x = location.X,
                        y = location.Y,
                        z = location.Z,
                        elementType = "TextNote"
                    });
                }

                // Get all TextElement elements (another type of text in families)
                var textElements = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextElement))
                    .Cast<TextElement>()
                    .Where(te => !(te is TextNote)) // Exclude TextNotes already collected
                    .ToList();

                foreach (var textElem in textElements)
                {
                    var bb = textElem.get_BoundingBox(null);
                    labels.Add(new
                    {
                        id = (int)textElem.Id.Value,
                        text = textElem.Text,
                        textTypeId = (int)textElem.GetTypeId().Value,
                        x = bb?.Min.X ?? 0,
                        y = bb?.Min.Y ?? 0,
                        z = bb?.Min.Z ?? 0,
                        elementType = "TextElement"
                    });
                }

                // Get all FamilyText (label parameters shown in family)
                var familyManager = doc.FamilyManager;
                var labelParams = new List<object>();

                foreach (FamilyParameter param in familyManager.Parameters)
                {
                    // In Revit 2026, use GetDataType() and check for text types
                    var dataType = param.Definition.GetDataType();
                    bool isTextParam = dataType == SpecTypeId.String.Text ||
                                       dataType.TypeId.Contains("text") ||
                                       dataType.TypeId.Contains("string");

                    if (isTextParam)
                    {
                        labelParams.Add(new
                        {
                            name = param.Definition.Name,
                            isInstance = param.IsInstance,
                            isShared = param.IsShared,
                            hasValue = param.IsDeterminedByFormula || familyManager.CurrentType != null
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyName = doc.Title,
                    textLabels = labels,
                    textLabelCount = labels.Count,
                    textParameters = labelParams,
                    textParameterCount = labelParams.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Edits a text label in a family document
        /// </summary>
        public static string EditFamilyLabel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document. Use OpenFamilyDocument first."
                    });
                }

                // Require either labelId or labelText to identify the label
                if (parameters["labelId"] == null && parameters["searchText"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either labelId or searchText is required to identify the label"
                    });
                }

                if (parameters["newText"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "newText is required"
                    });
                }

                string newText = parameters["newText"].ToString();
                TextNote targetLabel = null;

                // Find by ID
                if (parameters["labelId"] != null)
                {
                    var labelIdInt = parameters["labelId"].ToObject<int>();
                    var labelId = new ElementId(labelIdInt);
                    targetLabel = doc.GetElement(labelId) as TextNote;

                    if (targetLabel == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"TextNote with ID {labelIdInt} not found"
                        });
                    }
                }
                // Find by text content
                else if (parameters["searchText"] != null)
                {
                    string searchText = parameters["searchText"].ToString();
                    bool exactMatch = parameters["exactMatch"]?.ToObject<bool>() ?? false;

                    var textNotes = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNote))
                        .Cast<TextNote>()
                        .ToList();

                    if (exactMatch)
                    {
                        targetLabel = textNotes.FirstOrDefault(t => t.Text == searchText);
                    }
                    else
                    {
                        targetLabel = textNotes.FirstOrDefault(t => t.Text.Contains(searchText));
                    }

                    if (targetLabel == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"No TextNote found containing '{searchText}'",
                            availableLabels = textNotes.Select(t => new { id = (int)t.Id.Value, text = t.Text.Substring(0, Math.Min(50, t.Text.Length)) }).ToList()
                        });
                    }
                }

                string oldText = targetLabel.Text;

                using (var trans = new Transaction(doc, "Edit Family Label"))
                {
                    trans.Start();

                    // Set the new text
                    targetLabel.Text = newText;

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    labelId = (int)targetLabel.Id.Value,
                    oldText = oldText,
                    newText = newText,
                    message = "Label text updated. Use SaveFamilyDocument to save changes."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Adds a new parameter to a family document (Revit 2026 compatible)
        /// </summary>
        public static string AddFamilyParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document. Use OpenFamilyDocument first."
                    });
                }

                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                bool isInstance = parameters["isInstance"]?.ToObject<bool>() ?? true;

                // Default to Text type - Using ForgeTypeId in Revit 2024+
                string paramTypeStr = parameters["parameterType"]?.ToString()?.ToLower() ?? "text";
                ForgeTypeId specTypeId = SpecTypeId.String.Text; // Default to text

                // Map common type strings to ForgeTypeId
                switch (paramTypeStr)
                {
                    case "text":
                    case "string":
                        specTypeId = SpecTypeId.String.Text;
                        break;
                    case "integer":
                    case "int":
                        specTypeId = SpecTypeId.Int.Integer;
                        break;
                    case "number":
                        specTypeId = SpecTypeId.Number;
                        break;
                    case "length":
                        specTypeId = SpecTypeId.Length;
                        break;
                    case "area":
                        specTypeId = SpecTypeId.Area;
                        break;
                    case "volume":
                        specTypeId = SpecTypeId.Volume;
                        break;
                    case "angle":
                        specTypeId = SpecTypeId.Angle;
                        break;
                    case "yesno":
                    case "boolean":
                        specTypeId = SpecTypeId.Boolean.YesNo;
                        break;
                    case "url":
                        specTypeId = SpecTypeId.String.Url;
                        break;
                    case "multilinetext":
                        specTypeId = SpecTypeId.String.MultilineText;
                        break;
                }

                // Default to Data group - Using ForgeTypeId GroupTypeId
                string paramGroupStr = parameters["parameterGroup"]?.ToString()?.ToLower() ?? "data";
                ForgeTypeId groupTypeId = GroupTypeId.Data; // Default

                // Map common group strings to ForgeTypeId
                switch (paramGroupStr)
                {
                    case "data":
                    case "pg_data":
                        groupTypeId = GroupTypeId.Data;
                        break;
                    case "text":
                    case "pg_text":
                        groupTypeId = GroupTypeId.Text;
                        break;
                    case "identity":
                    case "pg_identity_data":
                        groupTypeId = GroupTypeId.IdentityData;
                        break;
                    case "construction":
                    case "pg_construction":
                        groupTypeId = GroupTypeId.Construction;
                        break;
                    case "materials":
                    case "pg_materials":
                        groupTypeId = GroupTypeId.Materials;
                        break;
                    case "graphics":
                    case "pg_graphics":
                        groupTypeId = GroupTypeId.Graphics;
                        break;
                    case "constraints":
                    case "pg_constraints":
                        groupTypeId = GroupTypeId.Constraints;
                        break;
                    case "dimensions":
                    case "pg_geometry":
                        groupTypeId = GroupTypeId.Geometry;
                        break;
                    case "general":
                    case "pg_general":
                        groupTypeId = GroupTypeId.General;
                        break;
                }

                var familyManager = doc.FamilyManager;

                // Check if parameter already exists
                foreach (FamilyParameter existingParam in familyManager.Parameters)
                {
                    if (existingParam.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Parameter '{paramName}' already exists",
                            existingParameterId = (int)existingParam.Id.Value,
                            isInstance = existingParam.IsInstance
                        });
                    }
                }

                FamilyParameter newParam = null;

                using (var trans = new Transaction(doc, "Add Family Parameter"))
                {
                    trans.Start();

                    // Revit 2024+ API: AddParameter(name, groupTypeId, specTypeId, isInstance)
                    newParam = familyManager.AddParameter(paramName, groupTypeId, specTypeId, isInstance);

                    // Set default value if provided
                    if (parameters["defaultValue"] != null && familyManager.CurrentType != null)
                    {
                        string defaultValue = parameters["defaultValue"].ToString();

                        // Determine how to set value based on spec type
                        if (specTypeId == SpecTypeId.String.Text ||
                            specTypeId == SpecTypeId.String.Url ||
                            specTypeId == SpecTypeId.String.MultilineText)
                        {
                            familyManager.Set(newParam, defaultValue);
                        }
                        else if (specTypeId == SpecTypeId.Int.Integer)
                        {
                            if (int.TryParse(defaultValue, out int intVal))
                                familyManager.Set(newParam, intVal);
                        }
                        else if (specTypeId == SpecTypeId.Boolean.YesNo)
                        {
                            if (bool.TryParse(defaultValue, out bool boolVal))
                                familyManager.Set(newParam, boolVal ? 1 : 0);
                        }
                        else
                        {
                            // Numeric types (length, area, volume, angle, number)
                            if (double.TryParse(defaultValue, out double dblVal))
                                familyManager.Set(newParam, dblVal);
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    parameterName = paramName,
                    parameterId = (int)newParam?.Id.Value,
                    parameterType = specTypeId.TypeId,
                    parameterGroup = groupTypeId.TypeId,
                    isInstance = isInstance,
                    message = "Parameter added. Use SaveFamilyDocument to save changes."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Opens a family for editing from a placed instance (Edit Family mode)
        /// </summary>
        public static string EditFamilyFromInstance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                FamilyInstance instance = null;
                Family family = null;

                // Option 1: Use provided element ID
                if (parameters["elementId"] != null)
                {
                    var elementIdInt = parameters["elementId"].ToObject<int>();
                    var elementId = new ElementId(elementIdInt);
                    instance = doc.GetElement(elementId) as FamilyInstance;

                    if (instance == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Element {elementIdInt} is not a FamilyInstance"
                        });
                    }

                    family = instance.Symbol?.Family;
                }
                // Option 2: Use current selection
                else
                {
                    var selection = uiDoc.Selection.GetElementIds();

                    if (selection.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No element selected. Select a family instance first or provide elementId."
                        });
                    }

                    var firstId = selection.First();
                    instance = doc.GetElement(firstId) as FamilyInstance;

                    if (instance == null)
                    {
                        // Maybe it's a family symbol directly selected from browser
                        var symbol = doc.GetElement(firstId) as FamilySymbol;
                        if (symbol != null)
                        {
                            family = symbol.Family;
                        }
                        else
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Selected element is not a FamilyInstance or FamilySymbol"
                            });
                        }
                    }
                    else
                    {
                        family = instance.Symbol?.Family;
                    }
                }

                if (family == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not find family for the selected element"
                    });
                }

                if (!family.IsEditable)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family '{family.Name}' is not editable (may be in-place or system family)"
                    });
                }

                // Open family for editing (EditFamily)
                var familyDoc = doc.EditFamily(family);

                if (familyDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Unable to open family document"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyId = (int)family.Id.Value,
                    familyName = family.Name,
                    familyCategory = family.FamilyCategory?.Name,
                    documentTitle = familyDoc.Title,
                    isFamilyDocument = familyDoc.IsFamilyDocument,
                    instanceId = instance != null ? (int?)instance.Id.Value : null,
                    typeName = instance?.Symbol?.Name,
                    message = "Family opened for editing. Use SaveFamilyDocument and LoadFamilyToProject when done."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Loads a family document back into the project (after editing)
        /// </summary>
        public static string LoadFamilyToProject(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Get the family document (should be active)
                var familyDoc = uiApp.ActiveUIDocument?.Document;

                if (familyDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                if (!familyDoc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document"
                    });
                }

                // Get target project document
                Document targetDoc = null;

                if (parameters["projectDocumentTitle"] != null)
                {
                    string projectTitle = parameters["projectDocumentTitle"].ToString();

                    foreach (Document openDoc in uiApp.Application.Documents)
                    {
                        if (!openDoc.IsFamilyDocument &&
                            openDoc.Title.IndexOf(projectTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            targetDoc = openDoc;
                            break;
                        }
                    }

                    if (targetDoc == null)
                    {
                        var openProjects = new List<string>();
                        foreach (Document openDoc in uiApp.Application.Documents)
                        {
                            if (!openDoc.IsFamilyDocument)
                                openProjects.Add(openDoc.Title);
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Project document '{projectTitle}' not found",
                            openProjects = openProjects
                        });
                    }
                }
                else
                {
                    // Find first open project document
                    foreach (Document openDoc in uiApp.Application.Documents)
                    {
                        if (!openDoc.IsFamilyDocument)
                        {
                            targetDoc = openDoc;
                            break;
                        }
                    }

                    if (targetDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No project document is open. Open a project to load the family into."
                        });
                    }
                }

                bool overwriteExisting = parameters["overwrite"]?.ToObject<bool>() ?? true;
                string familyName = familyDoc.Title.Replace(".rfa", "");

                // Load family into project
                Family loadedFamily = null;

                using (var trans = new Transaction(targetDoc, "Load Family from Editor"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (overwriteExisting)
                    {
                        loadedFamily = familyDoc.LoadFamily(targetDoc, new FamilyLoadOptions());
                    }
                    else
                    {
                        loadedFamily = familyDoc.LoadFamily(targetDoc);
                    }

                    if (loadedFamily != null)
                    {
                        trans.Commit();
                    }
                    else
                    {
                        trans.RollBack();
                    }
                }

                if (loadedFamily == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to load family into project. Family may already exist with same parameters."
                    });
                }

                // Get type info
                var typeIds = loadedFamily.GetFamilySymbolIds();
                var types = typeIds.Select(id =>
                {
                    var symbol = targetDoc.GetElement(id) as FamilySymbol;
                    return new
                    {
                        id = (int)id.Value,
                        name = symbol?.Name
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyName = loadedFamily.Name,
                    familyId = (int)loadedFamily.Id.Value,
                    targetProject = targetDoc.Title,
                    typeCount = types.Count,
                    types = types,
                    message = "Family loaded into project successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Bulk Type Operations

        /// <summary>
        /// Replaces all instances of a family type with a different type across the model.
        /// Preserves instance parameters and placement, only changes the type definition.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - sourceTypeId (required): Family type ID to find
        /// - targetTypeId (required): Family type ID to replace with
        /// - category (optional): Limit to specific category
        /// - preserveParameters (optional): Attempt to copy matching parameters (default: true)
        /// - dryRun (optional): Preview only, don't make changes (default: false)
        /// </param>
        /// <returns>JSON response with replacement results</returns>
        public static string SwapFamilyTypeByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sourceTypeId"] == null || parameters["targetTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceTypeId and targetTypeId are required"
                    });
                }

                int sourceTypeIdInt = parameters["sourceTypeId"].ToObject<int>();
                int targetTypeIdInt = parameters["targetTypeId"].ToObject<int>();
                bool preserveParameters = parameters["preserveParameters"]?.ToObject<bool>() ?? true;
                bool dryRun = parameters["dryRun"]?.ToObject<bool>() ?? false;

                ElementId sourceTypeId = new ElementId(sourceTypeIdInt);
                ElementId targetTypeId = new ElementId(targetTypeIdInt);

                // Verify types exist
                Element sourceType = doc.GetElement(sourceTypeId);
                Element targetType = doc.GetElement(targetTypeId);

                if (sourceType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source type with ID {sourceTypeIdInt} not found"
                    });
                }

                if (targetType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target type with ID {targetTypeIdInt} not found"
                    });
                }

                // Get type names for reporting
                string sourceTypeName = sourceType.Name;
                string targetTypeName = targetType.Name;

                // Find all instances of source type
                var instances = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e =>
                    {
                        var typeId = e.GetTypeId();
                        return typeId == sourceTypeId;
                    })
                    .ToList();

                var results = new List<object>();
                int swappedCount = 0;
                int failedCount = 0;

                if (!dryRun && instances.Count > 0)
                {
                    using (var trans = new Transaction(doc, "Swap Family Type"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        foreach (var instance in instances)
                        {
                            try
                            {
                                // Store parameter values if preserving
                                Dictionary<string, object> paramValues = null;
                                if (preserveParameters)
                                {
                                    paramValues = new Dictionary<string, object>();
                                    foreach (Parameter param in instance.Parameters)
                                    {
                                        if (!param.IsReadOnly && param.HasValue)
                                        {
                                            switch (param.StorageType)
                                            {
                                                case StorageType.String:
                                                    paramValues[param.Definition.Name] = param.AsString();
                                                    break;
                                                case StorageType.Integer:
                                                    paramValues[param.Definition.Name] = param.AsInteger();
                                                    break;
                                                case StorageType.Double:
                                                    paramValues[param.Definition.Name] = param.AsDouble();
                                                    break;
                                            }
                                        }
                                    }
                                }

                                // Change the type
                                instance.ChangeTypeId(targetTypeId);

                                // Restore parameters if preserving
                                if (preserveParameters && paramValues != null)
                                {
                                    foreach (var kvp in paramValues)
                                    {
                                        var param = instance.LookupParameter(kvp.Key);
                                        if (param != null && !param.IsReadOnly)
                                        {
                                            try
                                            {
                                                if (kvp.Value is string strVal)
                                                    param.Set(strVal);
                                                else if (kvp.Value is int intVal)
                                                    param.Set(intVal);
                                                else if (kvp.Value is double dblVal)
                                                    param.Set(dblVal);
                                            }
                                            catch { }
                                        }
                                    }
                                }

                                swappedCount++;
                                results.Add(new
                                {
                                    elementId = (int)instance.Id.Value,
                                    category = instance.Category?.Name ?? "Unknown",
                                    status = "swapped"
                                });
                            }
                            catch (Exception ex)
                            {
                                failedCount++;
                                results.Add(new
                                {
                                    elementId = (int)instance.Id.Value,
                                    category = instance.Category?.Name ?? "Unknown",
                                    status = "failed",
                                    error = ex.Message
                                });
                            }
                        }

                        trans.Commit();
                    }
                }
                else if (dryRun)
                {
                    foreach (var instance in instances)
                    {
                        results.Add(new
                        {
                            elementId = (int)instance.Id.Value,
                            category = instance.Category?.Name ?? "Unknown",
                            status = "would_swap"
                        });
                    }
                }

                // Group by category
                var categoryBreakdown = instances
                    .GroupBy(i => i.Category?.Name ?? "Unknown")
                    .Select(g => new { category = g.Key, count = g.Count() })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    dryRun = dryRun,
                    sourceType = new
                    {
                        typeId = sourceTypeIdInt,
                        typeName = sourceTypeName
                    },
                    targetType = new
                    {
                        typeId = targetTypeIdInt,
                        typeName = targetTypeName
                    },
                    foundCount = instances.Count,
                    swappedCount = dryRun ? 0 : swappedCount,
                    failedCount = dryRun ? 0 : failedCount,
                    preserveParameters = preserveParameters,
                    categoryBreakdown = categoryBreakdown,
                    results = results.Take(100).ToList(),
                    message = dryRun ?
                        $"Dry run: {instances.Count} instances would be swapped from '{sourceTypeName}' to '{targetTypeName}'" :
                        $"Swapped {swappedCount} instances from '{sourceTypeName}' to '{targetTypeName}'"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Export Families to Folder

        /// <summary>
        /// Exports all families of a specified category to a folder as .rfa files.
        /// Organizes into subfolders by category if multiple categories are exported.
        /// </summary>
        public static string ExportFamiliesToFolder(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                // Get parameters
                string outputFolder = parameters["outputFolder"]?.ToString();
                string categoryFilter = parameters["category"]?.ToString();
                bool createSubfolders = parameters["createSubfolders"]?.Value<bool>() ?? true;
                bool overwrite = parameters["overwrite"]?.Value<bool>() ?? true;

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "outputFolder parameter is required"
                    });
                }

                // Create output folder if needed
                if (!System.IO.Directory.Exists(outputFolder))
                {
                    System.IO.Directory.CreateDirectory(outputFolder);
                }

                // Get all families
                var allFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .ToElements()
                    .OfType<Family>()
                    .Where(f => f != null && f.IsEditable && !f.IsInPlace)
                    .ToList();

                // Filter by category if specified
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    allFamilies = allFamilies.Where(f =>
                        f.FamilyCategory?.Name?.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase) ?? false
                    ).ToList();
                }

                if (allFamilies.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = string.IsNullOrEmpty(categoryFilter)
                            ? "No editable families found in project"
                            : $"No editable families found in category '{categoryFilter}'",
                        exportedCount = 0
                    });
                }

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var family in allFamilies)
                {
                    try
                    {
                        string familyName = family.Name;
                        string categoryName = family.FamilyCategory?.Name ?? "Unknown";

                        // Determine output path
                        string targetFolder = outputFolder;
                        if (createSubfolders && !string.IsNullOrEmpty(categoryName))
                        {
                            // Sanitize category name for folder
                            string safeCategoryName = string.Join("_",
                                categoryName.Split(System.IO.Path.GetInvalidFileNameChars()));
                            targetFolder = System.IO.Path.Combine(outputFolder, safeCategoryName);

                            if (!System.IO.Directory.Exists(targetFolder))
                            {
                                System.IO.Directory.CreateDirectory(targetFolder);
                            }
                        }

                        // Sanitize family name for file
                        string safeFileName = string.Join("_",
                            familyName.Split(System.IO.Path.GetInvalidFileNameChars()));
                        string filePath = System.IO.Path.Combine(targetFolder, $"{safeFileName}.rfa");

                        // Check if exists and skip if not overwriting
                        if (System.IO.File.Exists(filePath) && !overwrite)
                        {
                            results.Add(new
                            {
                                familyName,
                                category = categoryName,
                                status = "skipped",
                                reason = "File exists"
                            });
                            continue;
                        }

                        // Open family document
                        Document familyDoc = doc.EditFamily(family);
                        if (familyDoc == null)
                        {
                            results.Add(new
                            {
                                familyName,
                                category = categoryName,
                                status = "failed",
                                reason = "Could not open family for editing"
                            });
                            failCount++;
                            continue;
                        }

                        try
                        {
                            // Save family to file
                            SaveAsOptions saveOptions = new SaveAsOptions();
                            saveOptions.OverwriteExistingFile = overwrite;
                            familyDoc.SaveAs(filePath, saveOptions);

                            results.Add(new
                            {
                                familyName,
                                category = categoryName,
                                status = "success",
                                path = filePath
                            });
                            successCount++;
                        }
                        finally
                        {
                            // Close family document without saving back to project
                            familyDoc.Close(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            familyName = family?.Name ?? "Unknown",
                            category = family?.FamilyCategory?.Name ?? "Unknown",
                            status = "failed",
                            reason = ex.Message
                        });
                        failCount++;
                    }
                }

                // Get summary by category
                var categorySummary = results
                    .Cast<dynamic>()
                    .Where(r => r.status == "success")
                    .GroupBy(r => (string)r.category)
                    .Select(g => new
                    {
                        category = g.Key,
                        count = g.Count()
                    })
                    .OrderByDescending(c => c.count)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputFolder,
                    totalFamilies = allFamilies.Count,
                    exportedCount = successCount,
                    failedCount = failCount,
                    categorySummary,
                    message = $"Exported {successCount} families to {outputFolder}"
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
