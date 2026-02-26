using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Document Manager Methods - Enable AI to manage Revit documents.
    /// Provides access to document operations: create, open, save, sync, purge.
    /// </summary>
    public static class DocumentMethods
    {
        #region Create New Document

        /// <summary>
        /// Create a new Revit document from a template.
        /// </summary>
        [MCPMethod("createNewDocument", Category = "Document", Description = "Create a new Revit document from a template")]
        public static string CreateNewDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var templatePath = parameters["templatePath"]?.ToString();

                // If no template specified, try default Revit templates
                if (string.IsNullOrEmpty(templatePath))
                {
                    var defaultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Autodesk", "Revit 2026", "Templates", "English");

                    if (Directory.Exists(defaultPath))
                    {
                        var templates = Directory.GetFiles(defaultPath, "*.rte");
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Template path required",
                            availableTemplates = templates.Select(Path.GetFileName).Take(20).ToList(),
                            templateFolder = defaultPath
                        });
                    }

                    return JsonConvert.SerializeObject(new { success = false, error = "Template path required" });
                }

                if (!File.Exists(templatePath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Template not found: {templatePath}" });
                }

                // Create new document
                var newDoc = uiApp.Application.NewProjectDocument(templatePath);

                if (newDoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Failed to create document" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentTitle = newDoc.Title,
                    isWorkshared = newDoc.IsWorkshared,
                    message = "New document created"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating new document");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// List available project templates (.rte files)
        /// </summary>
        [MCPMethod("listProjectTemplates", Category = "Document", Description = "List available project templates")]
        public static string ListProjectTemplates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var searchPath = parameters["searchPath"]?.ToString();
                var templates = new List<object>();

                // Default Revit template locations
                var searchPaths = new List<string>();

                // Add user-specified path
                if (!string.IsNullOrEmpty(searchPath) && Directory.Exists(searchPath))
                {
                    searchPaths.Add(searchPath);
                }

                // Revit 2026 default templates
                var revitPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Autodesk", "Revit 2026", "Templates", "English");
                if (Directory.Exists(revitPath))
                    searchPaths.Add(revitPath);

                // Revit 2026 US Imperial
                var imperialPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Autodesk", "Revit 2026", "Templates", "English-Imperial");
                if (Directory.Exists(imperialPath))
                    searchPaths.Add(imperialPath);

                // User's custom templates
                var userTemplates = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Autodesk Revit 2026", "Templates");
                if (Directory.Exists(userTemplates))
                    searchPaths.Add(userTemplates);

                // Common project templates location
                var commonTemplates = @"D:\Revit Templates";
                if (Directory.Exists(commonTemplates))
                    searchPaths.Add(commonTemplates);

                // Firm-specific templates
                var firmTemplates = @"D:\Revit Templates\Firm Templates";
                if (Directory.Exists(firmTemplates))
                    searchPaths.Add(firmTemplates);

                foreach (var path in searchPaths.Distinct())
                {
                    try
                    {
                        var files = Directory.GetFiles(path, "*.rte", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            var fileInfo = new FileInfo(file);
                            templates.Add(new
                            {
                                name = Path.GetFileNameWithoutExtension(file),
                                fileName = Path.GetFileName(file),
                                fullPath = file,
                                folder = Path.GetDirectoryName(file),
                                sizeKB = Math.Round(fileInfo.Length / 1024.0, 1),
                                modified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd")
                            });
                        }
                    }
                    catch { /* Skip inaccessible paths */ }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = templates.Count,
                    searchedPaths = searchPaths,
                    templates = templates.OrderBy(t => ((dynamic)t).name).ToList()
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error listing templates");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Open Document

        /// <summary>
        /// Open an existing Revit document.
        /// </summary>
        [MCPMethod("openDocument", Category = "Document", Description = "Open an existing Revit document")]
        public static string OpenDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var filePath = parameters["filePath"]?.ToString();
                if (string.IsNullOrEmpty(filePath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "File path required" });
                }

                if (!File.Exists(filePath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"File not found: {filePath}" });
                }

                var detachFromCentral = parameters["detachFromCentral"]?.Value<bool>() ?? false;
                var audit = parameters["audit"]?.Value<bool>() ?? false;

                // Configure open options
                var openOptions = new OpenOptions();

                if (detachFromCentral)
                {
                    openOptions.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                }

                if (audit)
                {
                    openOptions.Audit = true;
                }

                // Create model path
                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);

                // Open document
                var doc = uiApp.Application.OpenDocumentFile(modelPath, openOptions);

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Failed to open document" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentTitle = doc.Title,
                    pathName = doc.PathName,
                    isWorkshared = doc.IsWorkshared,
                    isModified = doc.IsModified,
                    message = $"Document '{doc.Title}' opened successfully"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening document");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Save Document

        /// <summary>
        /// Save the active document.
        /// </summary>
        [MCPMethod("saveDocument", Category = "Document", Description = "Save the active document")]
        public static string SaveDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var saveAsPath = parameters["saveAsPath"]?.ToString();
                var compact = parameters["compact"]?.Value<bool>() ?? false;

                if (!string.IsNullOrEmpty(saveAsPath))
                {
                    // Save As
                    var saveAsOptions = new SaveAsOptions { OverwriteExistingFile = true };
                    if (compact)
                    {
                        saveAsOptions.Compact = true;
                    }

                    doc.SaveAs(saveAsPath, saveAsOptions);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        pathName = saveAsPath,
                        message = $"Document saved as '{Path.GetFileName(saveAsPath)}'"
                    });
                }
                else
                {
                    // Regular Save
                    if (string.IsNullOrEmpty(doc.PathName))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Document has not been saved. Provide saveAsPath."
                        });
                    }

                    doc.Save();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        pathName = doc.PathName,
                        message = "Document saved"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving document");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Save the active document as a project template (.rte file).
        /// This creates a reusable template that can be used to start new projects.
        /// </summary>
        [MCPMethod("saveAsTemplate", Category = "Document", Description = "Save the active document as a project template")]
        public static string SaveAsTemplate(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var templateName = parameters["templateName"]?.ToString();
                var templatePath = parameters["templatePath"]?.ToString();
                var firmName = parameters["firmName"]?.ToString();
                var description = parameters["description"]?.ToString();
                var overwrite = parameters["overwrite"]?.Value<bool>() ?? false;

                if (string.IsNullOrEmpty(templateName) && string.IsNullOrEmpty(templatePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either templateName or templatePath is required"
                    });
                }

                // Determine the full path
                string fullPath;
                if (!string.IsNullOrEmpty(templatePath))
                {
                    fullPath = templatePath;
                    // Ensure .rte extension
                    if (!fullPath.EndsWith(".rte", StringComparison.OrdinalIgnoreCase))
                    {
                        fullPath += ".rte";
                    }
                }
                else
                {
                    // Create path in firm templates folder
                    var firmTemplatesFolder = @"D:\Revit Templates\Firm Templates";
                    if (!string.IsNullOrEmpty(firmName))
                    {
                        firmTemplatesFolder = Path.Combine(firmTemplatesFolder, firmName);
                    }

                    // Ensure directory exists
                    if (!Directory.Exists(firmTemplatesFolder))
                    {
                        Directory.CreateDirectory(firmTemplatesFolder);
                    }

                    // Clean template name for filename
                    var cleanName = string.Join("_", templateName.Split(Path.GetInvalidFileNameChars()));
                    fullPath = Path.Combine(firmTemplatesFolder, cleanName + ".rte");
                }

                // Ensure parent directory exists
                var parentDir = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                // Check if file exists
                if (File.Exists(fullPath) && !overwrite)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Template already exists: {fullPath}",
                        hint = "Use overwrite: true to replace existing template"
                    });
                }

                // Set project info if description provided
                if (!string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(firmName))
                {
                    using (var trans = new Transaction(doc, "Set Template Info"))
                    {
                        trans.Start();

                        var projectInfo = doc.ProjectInformation;
                        if (!string.IsNullOrEmpty(firmName))
                        {
                            projectInfo.OrganizationName = firmName;
                        }
                        if (!string.IsNullOrEmpty(description))
                        {
                            // Try to set description in project info if parameter exists
                            try
                            {
                                var descParam = projectInfo.LookupParameter("Project Description");
                                if (descParam != null && !descParam.IsReadOnly)
                                {
                                    descParam.Set(description);
                                }
                            }
                            catch { /* Ignore if parameter doesn't exist */ }
                        }

                        trans.Commit();
                    }
                }

                // Save as template
                var saveAsOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = overwrite,
                    Compact = true
                };

                doc.SaveAs(fullPath, saveAsOptions);

                // Get file info for response
                var fileInfo = new FileInfo(fullPath);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    templatePath = fullPath,
                    templateName = Path.GetFileNameWithoutExtension(fullPath),
                    firmName = firmName ?? "Not specified",
                    sizeKB = Math.Round(fileInfo.Length / 1024.0, 1),
                    message = $"Template saved: {Path.GetFileName(fullPath)}"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving template");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Close Document

        /// <summary>
        /// Close a document.
        /// </summary>
        [MCPMethod("closeDocument", Category = "Document", Description = "Close a document")]
        public static string CloseDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var save = parameters["save"]?.Value<bool>() ?? false;
                var documentTitle = parameters["documentTitle"]?.ToString();

                Document docToClose = null;

                if (!string.IsNullOrEmpty(documentTitle))
                {
                    // Find specific document
                    foreach (Document openDoc in uiApp.Application.Documents)
                    {
                        if (openDoc.Title.Equals(documentTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            docToClose = openDoc;
                            break;
                        }
                    }

                    if (docToClose == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = $"Document '{documentTitle}' not found" });
                    }
                }
                else
                {
                    // Close active document
                    docToClose = uiApp.ActiveUIDocument?.Document;
                }

                if (docToClose == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No document to close" });
                }

                var closedTitle = docToClose.Title;

                if (save && docToClose.IsModified)
                {
                    docToClose.Save();
                }

                docToClose.Close(!save);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    closedDocument = closedTitle,
                    saved = save,
                    message = $"Document '{closedTitle}' closed"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error closing document");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Sync With Central

        /// <summary>
        /// Sync a workshared document with central.
        /// </summary>
        [MCPMethod("syncWithCentral", Category = "Document", Description = "Sync a workshared document with central")]
        public static string SyncWithCentral(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                if (!doc.IsWorkshared)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is not workshared"
                    });
                }

                var comment = parameters["comment"]?.ToString() ?? "";
                var compact = parameters["compact"]?.Value<bool>() ?? false;
                var saveLocalBefore = parameters["saveLocalBefore"]?.Value<bool>() ?? true;
                var saveLocalAfter = parameters["saveLocalAfter"]?.Value<bool>() ?? true;
                var relinquishAll = parameters["relinquishAll"]?.Value<bool>() ?? false;

                // Configure sync options
                var syncOptions = new SynchronizeWithCentralOptions();

                if (compact)
                {
                    syncOptions.Compact = true;
                }

                syncOptions.SaveLocalBefore = saveLocalBefore;
                syncOptions.SaveLocalAfter = saveLocalAfter;

                if (!string.IsNullOrEmpty(comment))
                {
                    syncOptions.Comment = comment;
                }

                // Configure relinquish options
                var relinquishOptions = new RelinquishOptions(false);
                if (relinquishAll)
                {
                    relinquishOptions = new RelinquishOptions(true);
                }
                syncOptions.SetRelinquishOptions(relinquishOptions);

                // Sync
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOptions);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentTitle = doc.Title,
                    comment = comment,
                    message = "Synchronized with central successfully"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error syncing with central");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Document Info

        /// <summary>
        /// Get detailed information about the active document.
        /// </summary>
        [MCPMethod("getDocumentInfo", Category = "Document", Description = "Get detailed information about the active document")]
        public static string GetDocumentInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var projectInfo = doc.ProjectInformation;

                // Get linked documents
                var links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(link => new
                    {
                        id = (int)link.Id.Value,
                        name = link.Name,
                        isLoaded = link.GetLinkDocument() != null
                    })
                    .ToList();

                // Get worksets if workshared
                List<object> worksets = null;
                if (doc.IsWorkshared)
                {
                    worksets = new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset)
                        .Select(ws => new
                        {
                            id = ws.Id.IntegerValue,
                            name = ws.Name,
                            isOpen = ws.IsOpen,
                            isDefaultWorkset = ws.IsDefaultWorkset
                        })
                        .ToList<object>();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    title = doc.Title,
                    pathName = doc.PathName,
                    isModified = doc.IsModified,
                    isWorkshared = doc.IsWorkshared,
                    isFamilyDocument = doc.IsFamilyDocument,
                    projectInfo = projectInfo != null ? new
                    {
                        name = projectInfo.Name,
                        number = projectInfo.Number,
                        clientName = projectInfo.ClientName,
                        address = projectInfo.Address,
                        issueDate = projectInfo.IssueDate,
                        status = projectInfo.Status,
                        author = projectInfo.Author
                    } : null,
                    linkedDocuments = links,
                    linkCount = links.Count,
                    worksets = worksets,
                    worksetCount = worksets?.Count ?? 0
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting document info");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Switch Document

        /// <summary>
        /// Switch the active document to a different open document.
        /// Enables AI to move between open projects in the same Revit session.
        /// </summary>
        [MCPMethod("switchDocument", Category = "Document", Description = "Switch the active document to a different open document")]
        public static string SwitchDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var targetTitle = parameters["documentTitle"]?.ToString();
                if (string.IsNullOrEmpty(targetTitle))
                {
                    // Return list of available documents if no title specified
                    var availableDocs = new List<string>();
                    foreach (Document doc in uiApp.Application.Documents)
                    {
                        if (!doc.IsFamilyDocument)
                            availableDocs.Add(doc.Title);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "documentTitle is required",
                        availableDocuments = availableDocs
                    });
                }

                var previousTitle = uiApp.ActiveUIDocument?.Document?.Title;

                // Check if already active
                if (previousTitle != null && previousTitle.Equals(targetTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"'{targetTitle}' is already the active document",
                        activeDocument = targetTitle
                    });
                }

                // Find the target document
                Document targetDoc = null;
                foreach (Document doc in uiApp.Application.Documents)
                {
                    if (doc.Title.Equals(targetTitle, StringComparison.OrdinalIgnoreCase) ||
                        doc.Title.IndexOf(targetTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetDoc = doc;
                        break;
                    }
                }

                if (targetDoc == null)
                {
                    var availableDocs = new List<string>();
                    foreach (Document doc in uiApp.Application.Documents)
                    {
                        if (!doc.IsFamilyDocument)
                            availableDocs.Add(doc.Title);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Document '{targetTitle}' not found",
                        availableDocuments = availableDocs
                    });
                }

                // Switch to the target document by requesting a view change
                // Create a UIDocument from the Document
                var uiDoc = new UIDocument(targetDoc);

                // Get the active view from the target document
                var activeView = targetDoc.ActiveView;
                if (activeView == null)
                {
                    // Try to find any valid view in the document
                    var views = new FilteredElementCollector(targetDoc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted)
                        .ToList();

                    if (views.Count > 0)
                    {
                        activeView = views[0];
                    }
                }

                if (activeView != null)
                {
                    // Request view change - this switches the active document
                    uiDoc.RequestViewChange(activeView);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    previousDocument = previousTitle,
                    activeDocument = targetDoc.Title,
                    pathName = targetDoc.PathName,
                    switchedToView = activeView?.Name,
                    message = $"Switched from '{previousTitle}' to '{targetDoc.Title}'"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error switching document");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Purge Unused

        /// <summary>
        /// Purge unused elements from the document.
        /// </summary>
        [MCPMethod("purgeUnused", Category = "Document", Description = "Purge unused elements from the document")]
        public static string PurgeUnused(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var dryRun = parameters["dryRun"]?.Value<bool>() ?? true;

                // Get purgeable elements
                var purgeableIds = new List<ElementId>();
                var failureMessages = new List<string>();

                // This is a simplified purge - in production you might use the Revit API's
                // Performance Adviser or third-party tools for comprehensive purging

                // Purge unused view types
                var viewTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => !IsViewTypeUsed(doc, vft))
                    .Select(vft => vft.Id)
                    .ToList();
                purgeableIds.AddRange(viewTypes);

                // Purge unused line patterns
                var linePatterns = new FilteredElementCollector(doc)
                    .OfClass(typeof(LinePatternElement))
                    .Where(lp => !IsElementUsed(doc, lp))
                    .Select(lp => lp.Id)
                    .ToList();
                purgeableIds.AddRange(linePatterns);

                // Purge unused materials
                var materials = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Where(m => !IsElementUsed(doc, m))
                    .Select(m => m.Id)
                    .ToList();
                purgeableIds.AddRange(materials);

                if (dryRun)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        dryRun = true,
                        purgeableCount = purgeableIds.Count,
                        viewTypesUnused = viewTypes.Count,
                        linePatternsUnused = linePatterns.Count,
                        materialsUnused = materials.Count,
                        message = $"Would purge {purgeableIds.Count} elements. Set dryRun=false to execute."
                    });
                }

                // Actually purge
                using (var trans = new Transaction(doc, "Purge Unused"))
                {
                    trans.Start();

                    var deleted = 0;
                    foreach (var id in purgeableIds)
                    {
                        try
                        {
                            doc.Delete(id);
                            deleted++;
                        }
                        catch (Exception ex)
                        {
                            failureMessages.Add($"Failed to delete {id.Value}: {ex.Message}");
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        dryRun = false,
                        purgedCount = deleted,
                        failures = failureMessages.Take(10).ToList(),
                        failureCount = failureMessages.Count,
                        message = $"Purged {deleted} unused elements"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error purging unused");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Reload Links

        /// <summary>
        /// Reload Revit links in the document.
        /// </summary>
        [MCPMethod("reloadLinks", Category = "Document", Description = "Reload Revit links in the document")]
        public static string ReloadLinks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var linkIdFilter = parameters["linkId"]?.Value<int>();
                var reloadAll = parameters["reloadAll"]?.Value<bool>() ?? false;

                var links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>()
                    .ToList();

                if (links.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = true, message = "No links to reload" });
                }

                var reloaded = new List<string>();
                var failed = new List<string>();

                using (var trans = new Transaction(doc, "Reload Links"))
                {
                    trans.Start();

                    foreach (var link in links)
                    {
                        if (linkIdFilter.HasValue && link.Id.Value != linkIdFilter.Value)
                            continue;

                        try
                        {
                            link.Reload();
                            reloaded.Add(link.Name);
                        }
                        catch (Exception ex)
                        {
                            failed.Add($"{link.Name}: {ex.Message}");
                        }

                        if (!reloadAll && !linkIdFilter.HasValue)
                            break; // Only reload first if not specified
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    reloadedCount = reloaded.Count,
                    reloadedLinks = reloaded,
                    failedCount = failed.Count,
                    failures = failed
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reloading links");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Project Info

        /// <summary>
        /// Set project information properties.
        /// </summary>
        [MCPMethod("setProjectInfo", Category = "Document", Description = "Set project information properties")]
        public static string SetProjectInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var projectInfo = doc.ProjectInformation;
                if (projectInfo == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Project information not available" });
                }

                using (var trans = new Transaction(doc, "Set Project Info"))
                {
                    trans.Start();

                    if (parameters["name"] != null)
                        projectInfo.Name = parameters["name"].ToString();

                    if (parameters["number"] != null)
                        projectInfo.Number = parameters["number"].ToString();

                    if (parameters["clientName"] != null)
                        projectInfo.ClientName = parameters["clientName"].ToString();

                    if (parameters["address"] != null)
                        projectInfo.Address = parameters["address"].ToString();

                    if (parameters["issueDate"] != null)
                        projectInfo.IssueDate = parameters["issueDate"].ToString();

                    if (parameters["status"] != null)
                        projectInfo.Status = parameters["status"].ToString();

                    if (parameters["author"] != null)
                        projectInfo.Author = parameters["author"].ToString();

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        projectInfo = new
                        {
                            name = projectInfo.Name,
                            number = projectInfo.Number,
                            clientName = projectInfo.ClientName,
                            address = projectInfo.Address,
                            issueDate = projectInfo.IssueDate,
                            status = projectInfo.Status,
                            author = projectInfo.Author
                        },
                        message = "Project information updated"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting project info");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Enable Worksharing

        /// <summary>
        /// Enable worksharing on a document.
        /// </summary>
        [MCPMethod("enableWorksharing", Category = "Document", Description = "Enable worksharing on a document")]
        public static string EnableWorksharing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                if (doc.IsWorkshared)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is already workshared"
                    });
                }

                var defaultWorksetName = parameters["defaultWorksetName"]?.ToString() ?? "Shared Levels and Grids";

                using (var trans = new Transaction(doc, "Enable Worksharing"))
                {
                    trans.Start();

                    doc.EnableWorksharing(defaultWorksetName, "Workset1");

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Worksharing enabled. Save to central to complete setup."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error enabling worksharing");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Export To IFC

        /// <summary>
        /// Export document to IFC format.
        /// </summary>
        [MCPMethod("exportToIFC", Category = "Document", Description = "Export document to IFC format")]
        public static string ExportToIFC(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputPath = parameters["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(outputPath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Output path required" });
                }

                var fileName = Path.GetFileNameWithoutExtension(outputPath);
                var outputDir = Path.GetDirectoryName(outputPath);

                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // Configure IFC export options
                var ifcOptions = new IFCExportOptions();

                // Export
                using (var trans = new Transaction(doc, "Export IFC"))
                {
                    trans.Start();
                    doc.Export(outputDir, fileName, ifcOptions);
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputPath = outputPath,
                    message = $"Exported to IFC: {fileName}.ifc"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting to IFC");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region DWG Export Methods

        /// <summary>
        /// Export selected views to DWG format
        /// Parameters: viewIds (array), outputFolder, fileNamingPattern (optional), dwgVersion (optional: "AutoCAD2018", "AutoCAD2013", etc.)
        /// </summary>
        [MCPMethod("exportViewsToDWG", Category = "Document", Description = "Export selected views to DWG format")]
        public static string ExportViewsToDWG(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var viewIds = parameters["viewIds"]?.ToObject<int[]>();
                var outputFolder = parameters["outputFolder"]?.ToString();
                var namingPattern = parameters["fileNamingPattern"]?.ToString() ?? "{ViewName}";
                var dwgVersionStr = parameters["dwgVersion"]?.ToString() ?? "AutoCAD2018";

                if (viewIds == null || viewIds.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewIds array is required" });
                }

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Parse DWG version
                ACADVersion dwgVersion = ACADVersion.R2018;
                switch (dwgVersionStr.ToUpper())
                {
                    case "AUTOCAD2013": dwgVersion = ACADVersion.R2013; break;
                    case "AUTOCAD2018": dwgVersion = ACADVersion.R2018; break;
                    case "AUTOCAD2007": dwgVersion = ACADVersion.R2007; break;
                    default: dwgVersion = ACADVersion.R2018; break;
                }

                // Set up DWG export options
                var dwgOptions = new DWGExportOptions
                {
                    FileVersion = dwgVersion,
                    ExportOfSolids = SolidGeometry.ACIS,
                    PropOverrides = PropOverrideMode.ByEntity,
                    SharedCoords = false,
                    MergedViews = false
                };

                var exportedFiles = new List<object>();
                var failedViews = new List<object>();

                foreach (var viewId in viewIds)
                {
                    try
                    {
                        var view = doc.GetElement(new ElementId(viewId)) as View;
                        if (view == null)
                        {
                            failedViews.Add(new { viewId, error = "View not found" });
                            continue;
                        }

                        // Generate file name from pattern
                        var fileName = namingPattern
                            .Replace("{ViewName}", SanitizeFileName(view.Name))
                            .Replace("{ViewType}", view.ViewType.ToString())
                            .Replace("{Scale}", view.Scale.ToString());

                        var exportIds = new List<ElementId> { view.Id };

                        // Export
                        doc.Export(outputFolder, fileName, exportIds, dwgOptions);

                        exportedFiles.Add(new
                        {
                            viewId,
                            viewName = view.Name,
                            fileName = fileName + ".dwg",
                            fullPath = Path.Combine(outputFolder, fileName + ".dwg")
                        });
                    }
                    catch (Exception ex)
                    {
                        failedViews.Add(new { viewId, error = ex.Message });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    exportedCount = exportedFiles.Count,
                    failedCount = failedViews.Count,
                    outputFolder,
                    dwgVersion = dwgVersionStr,
                    exportedFiles,
                    failedViews
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting views to DWG");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Export sheets to DWG format (each sheet becomes a DWG file)
        /// Parameters: sheetIds (array, optional - exports all if not specified), outputFolder, includeViewsOnSheet (bool), dwgVersion
        /// </summary>
        [MCPMethod("exportSheetsToDWG", Category = "Document", Description = "Export sheets to DWG format")]
        public static string ExportSheetsToDWG(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var sheetIds = parameters["sheetIds"]?.ToObject<int[]>();
                var outputFolder = parameters["outputFolder"]?.ToString();
                var includeViewsOnSheet = parameters["includeViewsOnSheet"]?.Value<bool>() ?? true;
                var dwgVersionStr = parameters["dwgVersion"]?.ToString() ?? "AutoCAD2018";
                var namingPattern = parameters["fileNamingPattern"]?.ToString() ?? "{SheetNumber} - {SheetName}";

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get sheets to export
                List<ViewSheet> sheetsToExport;
                if (sheetIds != null && sheetIds.Length > 0)
                {
                    sheetsToExport = sheetIds
                        .Select(id => doc.GetElement(new ElementId(id)) as ViewSheet)
                        .Where(s => s != null)
                        .ToList();
                }
                else
                {
                    sheetsToExport = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .OrderBy(s => s.SheetNumber)
                        .ToList();
                }

                // Parse DWG version
                ACADVersion dwgVersion = ACADVersion.R2018;
                switch (dwgVersionStr.ToUpper())
                {
                    case "AUTOCAD2013": dwgVersion = ACADVersion.R2013; break;
                    case "AUTOCAD2018": dwgVersion = ACADVersion.R2018; break;
                    case "AUTOCAD2007": dwgVersion = ACADVersion.R2007; break;
                    default: dwgVersion = ACADVersion.R2018; break;
                }

                var dwgOptions = new DWGExportOptions
                {
                    FileVersion = dwgVersion,
                    ExportOfSolids = SolidGeometry.ACIS,
                    PropOverrides = PropOverrideMode.ByEntity,
                    SharedCoords = false,
                    MergedViews = !includeViewsOnSheet // If including views, don't merge
                };

                var exportedFiles = new List<object>();
                var failedSheets = new List<object>();

                foreach (var sheet in sheetsToExport)
                {
                    try
                    {
                        var fileName = namingPattern
                            .Replace("{SheetNumber}", SanitizeFileName(sheet.SheetNumber))
                            .Replace("{SheetName}", SanitizeFileName(sheet.Name));

                        var exportIds = new List<ElementId> { sheet.Id };

                        doc.Export(outputFolder, fileName, exportIds, dwgOptions);

                        exportedFiles.Add(new
                        {
                            sheetId = sheet.Id.Value,
                            sheetNumber = sheet.SheetNumber,
                            sheetName = sheet.Name,
                            fileName = fileName + ".dwg",
                            fullPath = Path.Combine(outputFolder, fileName + ".dwg")
                        });
                    }
                    catch (Exception ex)
                    {
                        failedSheets.Add(new { sheetId = sheet.Id.Value, sheetNumber = sheet.SheetNumber, error = ex.Message });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    exportedCount = exportedFiles.Count,
                    failedCount = failedSheets.Count,
                    outputFolder,
                    dwgVersion = dwgVersionStr,
                    exportedFiles,
                    failedSheets
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting sheets to DWG");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch export all sheets and/or views to DWG with organized folder structure
        /// Parameters: outputFolder, exportSheets (bool), exportViews (bool), organizeFolders (bool), dwgVersion
        /// </summary>
        [MCPMethod("batchExportDWG", Category = "Document", Description = "Batch export all sheets and/or views to DWG")]
        public static string BatchExportDWG(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputFolder = parameters["outputFolder"]?.ToString();
                var exportSheets = parameters["exportSheets"]?.Value<bool>() ?? true;
                var exportViews = parameters["exportViews"]?.Value<bool>() ?? false;
                var organizeFolders = parameters["organizeFolders"]?.Value<bool>() ?? true;
                var dwgVersionStr = parameters["dwgVersion"]?.ToString() ?? "AutoCAD2018";
                var viewTypes = parameters["viewTypes"]?.ToObject<string[]>(); // Optional filter

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Parse DWG version
                ACADVersion dwgVersion = ACADVersion.R2018;
                switch (dwgVersionStr.ToUpper())
                {
                    case "AUTOCAD2013": dwgVersion = ACADVersion.R2013; break;
                    case "AUTOCAD2018": dwgVersion = ACADVersion.R2018; break;
                    case "AUTOCAD2007": dwgVersion = ACADVersion.R2007; break;
                    default: dwgVersion = ACADVersion.R2018; break;
                }

                var dwgOptions = new DWGExportOptions
                {
                    FileVersion = dwgVersion,
                    ExportOfSolids = SolidGeometry.ACIS,
                    PropOverrides = PropOverrideMode.ByEntity,
                    SharedCoords = false
                };

                var exportedFiles = new List<object>();
                var failedExports = new List<object>();
                var projectName = doc.Title.Replace(".rvt", "");

                // Export sheets
                if (exportSheets)
                {
                    var sheetsFolder = organizeFolders ? Path.Combine(outputFolder, "Sheets") : outputFolder;
                    if (!Directory.Exists(sheetsFolder)) Directory.CreateDirectory(sheetsFolder);

                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .OrderBy(s => s.SheetNumber);

                    foreach (var sheet in sheets)
                    {
                        try
                        {
                            var fileName = SanitizeFileName($"{sheet.SheetNumber} - {sheet.Name}");
                            var sheetIds = new List<ElementId> { sheet.Id };

                            doc.Export(sheetsFolder, fileName, sheetIds, dwgOptions);

                            exportedFiles.Add(new
                            {
                                type = "Sheet",
                                id = sheet.Id.Value,
                                name = $"{sheet.SheetNumber} - {sheet.Name}",
                                fileName = fileName + ".dwg"
                            });
                        }
                        catch (Exception ex)
                        {
                            failedExports.Add(new { type = "Sheet", id = sheet.Id.Value, name = sheet.SheetNumber, error = ex.Message });
                        }
                    }
                }

                // Export views
                if (exportViews)
                {
                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted && !(v is ViewSheet));

                    // Filter by view types if specified
                    if (viewTypes != null && viewTypes.Length > 0)
                    {
                        views = views.Where(v => viewTypes.Contains(v.ViewType.ToString(), StringComparer.OrdinalIgnoreCase));
                    }

                    foreach (var view in views)
                    {
                        try
                        {
                            var viewTypeName = view.ViewType.ToString();
                            var viewsFolder = organizeFolders
                                ? Path.Combine(outputFolder, "Views", viewTypeName)
                                : outputFolder;
                            if (!Directory.Exists(viewsFolder)) Directory.CreateDirectory(viewsFolder);

                            var fileName = SanitizeFileName(view.Name);
                            var viewIds = new List<ElementId> { view.Id };

                            doc.Export(viewsFolder, fileName, viewIds, dwgOptions);

                            exportedFiles.Add(new
                            {
                                type = viewTypeName,
                                id = view.Id.Value,
                                name = view.Name,
                                fileName = fileName + ".dwg"
                            });
                        }
                        catch (Exception ex)
                        {
                            failedExports.Add(new { type = view.ViewType.ToString(), id = view.Id.Value, name = view.Name, error = ex.Message });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName,
                    outputFolder,
                    dwgVersion = dwgVersionStr,
                    exportedCount = exportedFiles.Count,
                    failedCount = failedExports.Count,
                    sheetsExported = exportSheets,
                    viewsExported = exportViews,
                    exportedFiles,
                    failedExports
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in batch DWG export");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Sanitize file name by removing invalid characters
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        }

        #endregion

        #region Revision Methods

        /// <summary>
        /// Create a new revision in the document
        /// Parameters: description, issuedBy, issuedTo, date (optional), visibility (optional: "CloudAndTagVisible", "Hidden", "TagVisible")
        /// </summary>
        [MCPMethod("createRevision", Category = "Document", Description = "Create a new revision in the document")]
        public static string CreateRevision(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var description = parameters["description"]?.ToString() ?? "";
                var issuedBy = parameters["issuedBy"]?.ToString() ?? "";
                var issuedTo = parameters["issuedTo"]?.ToString() ?? "";
                var dateStr = parameters["date"]?.ToString();
                var visibilityStr = parameters["visibility"]?.ToString() ?? "CloudAndTagVisible";

                using (var trans = new Transaction(doc, "Create Revision"))
                {
                    trans.Start();

                    var revision = Revision.Create(doc);
                    revision.Description = description;
                    revision.IssuedBy = issuedBy;
                    revision.IssuedTo = issuedTo;

                    if (!string.IsNullOrEmpty(dateStr))
                    {
                        revision.RevisionDate = dateStr;
                    }

                    // Set visibility
                    switch (visibilityStr.ToLower())
                    {
                        case "hidden":
                            revision.Visibility = RevisionVisibility.Hidden;
                            break;
                        case "tagvisible":
                            revision.Visibility = RevisionVisibility.TagVisible;
                            break;
                        default:
                            revision.Visibility = RevisionVisibility.CloudAndTagVisible;
                            break;
                    }

                    trans.Commit();

                    // Get the sequence number
                    var allRevisions = Revision.GetAllRevisionIds(doc);
                    var sequenceNumber = allRevisions.IndexOf(revision.Id) + 1;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        revisionId = revision.Id.Value,
                        sequenceNumber,
                        description = revision.Description,
                        issuedBy = revision.IssuedBy,
                        issuedTo = revision.IssuedTo,
                        date = revision.RevisionDate,
                        visibility = revision.Visibility.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating revision");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all revisions in the document
        /// Parameters: none
        /// </summary>
        [MCPMethod("getRevisions", Category = "Document", Description = "Get all revisions in the document")]
        public static string GetRevisions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var revisionIds = Revision.GetAllRevisionIds(doc);
                var revisions = new List<object>();

                int sequence = 1;
                foreach (var revId in revisionIds)
                {
                    var revision = doc.GetElement(revId) as Revision;
                    if (revision != null)
                    {
                        revisions.Add(new
                        {
                            revisionId = revision.Id.Value,
                            sequenceNumber = sequence++,
                            description = revision.Description,
                            issuedBy = revision.IssuedBy,
                            issuedTo = revision.IssuedTo,
                            date = revision.RevisionDate,
                            visibility = revision.Visibility.ToString(),
                            issued = revision.Issued
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = revisions.Count,
                    revisions
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting revisions");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add a revision to one or more sheets
        /// Parameters: revisionId, sheetIds (array)
        /// </summary>
        [MCPMethod("addRevisionToSheets", Category = "Document", Description = "Add a revision to one or more sheets")]
        public static string AddRevisionToSheets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var revisionId = parameters["revisionId"]?.Value<int>() ?? 0;
                var sheetIdArray = parameters["sheetIds"]?.ToObject<int[]>();

                if (revisionId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId is required" });
                }

                if (sheetIdArray == null || sheetIdArray.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetIds array is required" });
                }

                var revision = doc.GetElement(new ElementId(revisionId)) as Revision;
                if (revision == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision not found" });
                }

                var updatedSheets = new List<object>();
                var failedSheets = new List<object>();

                using (var trans = new Transaction(doc, "Add Revision to Sheets"))
                {
                    trans.Start();

                    foreach (var sheetId in sheetIdArray)
                    {
                        try
                        {
                            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
                            if (sheet == null)
                            {
                                failedSheets.Add(new { sheetId, error = "Sheet not found" });
                                continue;
                            }

                            var currentRevisions = sheet.GetAdditionalRevisionIds().ToList();
                            if (!currentRevisions.Contains(revision.Id))
                            {
                                currentRevisions.Add(revision.Id);
                                sheet.SetAdditionalRevisionIds(currentRevisions);
                            }

                            updatedSheets.Add(new
                            {
                                sheetId,
                                sheetNumber = sheet.SheetNumber,
                                sheetName = sheet.Name
                            });
                        }
                        catch (Exception ex)
                        {
                            failedSheets.Add(new { sheetId, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    revisionId,
                    revisionDescription = revision.Description,
                    updatedCount = updatedSheets.Count,
                    failedCount = failedSheets.Count,
                    updatedSheets,
                    failedSheets
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding revision to sheets");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove a revision from one or more sheets
        /// Parameters: revisionId, sheetIds (array)
        /// </summary>
        [MCPMethod("removeRevisionFromSheets", Category = "Document", Description = "Remove a revision from one or more sheets")]
        public static string RemoveRevisionFromSheets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var revisionId = parameters["revisionId"]?.Value<int>() ?? 0;
                var sheetIdArray = parameters["sheetIds"]?.ToObject<int[]>();

                if (revisionId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId is required" });
                }

                if (sheetIdArray == null || sheetIdArray.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetIds array is required" });
                }

                var updatedSheets = new List<object>();
                var failedSheets = new List<object>();

                using (var trans = new Transaction(doc, "Remove Revision from Sheets"))
                {
                    trans.Start();

                    foreach (var sheetId in sheetIdArray)
                    {
                        try
                        {
                            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
                            if (sheet == null)
                            {
                                failedSheets.Add(new { sheetId, error = "Sheet not found" });
                                continue;
                            }

                            var currentRevisions = sheet.GetAdditionalRevisionIds().ToList();
                            var revId = new ElementId(revisionId);
                            if (currentRevisions.Contains(revId))
                            {
                                currentRevisions.Remove(revId);
                                sheet.SetAdditionalRevisionIds(currentRevisions);

                                updatedSheets.Add(new
                                {
                                    sheetId,
                                    sheetNumber = sheet.SheetNumber,
                                    sheetName = sheet.Name
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            failedSheets.Add(new { sheetId, error = ex.Message });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    revisionId,
                    updatedCount = updatedSheets.Count,
                    failedCount = failedSheets.Count,
                    updatedSheets,
                    failedSheets
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error removing revision from sheets");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a revision cloud in a view
        /// Parameters: viewId, revisionId, points (array of {x, y} coordinates forming the cloud boundary)
        /// </summary>
        [MCPMethod("placeRevisionCloud", Category = "Document", Description = "Place a revision cloud in a view")]
        public static string PlaceRevisionCloud(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>() ?? 0;
                var revisionId = parameters["revisionId"]?.Value<int>() ?? 0;
                var pointsArray = parameters["points"]?.ToObject<List<Dictionary<string, double>>>();

                if (viewId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                }

                if (revisionId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId is required" });
                }

                if (pointsArray == null || pointsArray.Count < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 3 points required for cloud boundary" });
                }

                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var revision = doc.GetElement(new ElementId(revisionId)) as Revision;
                if (revision == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision not found" });
                }

                using (var trans = new Transaction(doc, "Place Revision Cloud"))
                {
                    trans.Start();

                    // Create curves from points
                    var curves = new List<Curve>();
                    for (int i = 0; i < pointsArray.Count; i++)
                    {
                        var p1 = pointsArray[i];
                        var p2 = pointsArray[(i + 1) % pointsArray.Count];

                        var start = new XYZ(p1["x"], p1["y"], 0);
                        var end = new XYZ(p2["x"], p2["y"], 0);

                        if (start.DistanceTo(end) > 0.001)
                        {
                            curves.Add(Line.CreateBound(start, end));
                        }
                    }

                    if (curves.Count < 3)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new { success = false, error = "Could not create valid boundary from points" });
                    }

                    var cloud = RevisionCloud.Create(doc, view, revision.Id, curves);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cloudId = cloud.Id.Value,
                        revisionId = revision.Id.Value,
                        revisionDescription = revision.Description,
                        viewId = view.Id.Value,
                        viewName = view.Name
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error placing revision cloud");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all revision clouds in the document or a specific view
        /// Parameters: viewId (optional - if not specified, gets all clouds)
        /// </summary>
        [MCPMethod("getRevisionClouds", Category = "Document", Description = "Get all revision clouds in the document or a specific view")]
        public static string GetRevisionClouds(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = parameters["viewId"]?.Value<int>();

                FilteredElementCollector collector;
                if (viewId.HasValue && viewId.Value != 0)
                {
                    var view = doc.GetElement(new ElementId(viewId.Value)) as View;
                    if (view == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                    }
                    collector = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var clouds = collector
                    .OfClass(typeof(RevisionCloud))
                    .Cast<RevisionCloud>()
                    .Select(cloud =>
                    {
                        var revision = doc.GetElement(cloud.RevisionId) as Revision;
                        var ownerView = doc.GetElement(cloud.OwnerViewId) as View;

                        return new
                        {
                            cloudId = cloud.Id.Value,
                            revisionId = cloud.RevisionId.Value,
                            revisionDescription = revision?.Description ?? "",
                            revisionSequence = revision != null ? Revision.GetAllRevisionIds(doc).IndexOf(revision.Id) + 1 : 0,
                            viewId = cloud.OwnerViewId.Value,
                            viewName = ownerView?.Name ?? ""
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = clouds.Count,
                    clouds
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting revision clouds");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag a revision cloud
        /// Parameters: cloudId, location (optional {x, y} - auto-places if not specified)
        /// </summary>
        [MCPMethod("tagRevisionCloud", Category = "Document", Description = "Tag a revision cloud")]
        public static string TagRevisionCloud(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var cloudId = parameters["cloudId"]?.Value<int>() ?? 0;
                var locationObj = parameters["location"]?.ToObject<Dictionary<string, double>>();

                if (cloudId == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "cloudId is required" });
                }

                var cloud = doc.GetElement(new ElementId(cloudId)) as RevisionCloud;
                if (cloud == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision cloud not found" });
                }

                var view = doc.GetElement(cloud.OwnerViewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Owner view not found" });
                }

                using (var trans = new Transaction(doc, "Tag Revision Cloud"))
                {
                    trans.Start();

                    XYZ tagLocation;
                    if (locationObj != null)
                    {
                        tagLocation = new XYZ(locationObj["x"], locationObj["y"], 0);
                    }
                    else
                    {
                        // Auto-calculate location from cloud bounding box
                        var bbox = cloud.get_BoundingBox(view);
                        if (bbox != null)
                        {
                            tagLocation = new XYZ(
                                (bbox.Min.X + bbox.Max.X) / 2,
                                bbox.Max.Y + 1, // Above the cloud
                                0
                            );
                        }
                        else
                        {
                            tagLocation = XYZ.Zero;
                        }
                    }

                    var tag = IndependentTag.Create(
                        doc,
                        view.Id,
                        new Reference(cloud),
                        false,
                        TagMode.TM_ADDBY_CATEGORY,
                        TagOrientation.Horizontal,
                        tagLocation
                    );

                    trans.Commit();

                    var revision = doc.GetElement(cloud.RevisionId) as Revision;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = tag.Id.Value,
                        cloudId = cloud.Id.Value,
                        revisionId = cloud.RevisionId.Value,
                        revisionDescription = revision?.Description ?? "",
                        location = new { x = tagLocation.X, y = tagLocation.Y }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error tagging revision cloud");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region PDF Export Methods

        /// <summary>
        /// Export sheets to PDF
        /// Parameters: sheetIds (array, optional - exports all if not specified), outputFolder,
        ///             fileName (optional pattern with {SheetNumber}, {SheetName}),
        ///             combineIntoSingle (bool, default false), paperSize (optional)
        /// </summary>
        [MCPMethod("exportSheetsToPDF", Category = "Document", Description = "Export sheets to PDF")]
        public static string ExportSheetsToPDF(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var sheetIdArray = parameters["sheetIds"]?.ToObject<int[]>();
                var outputFolder = parameters["outputFolder"]?.ToString();
                var fileNamePattern = parameters["fileName"]?.ToString() ?? "{SheetNumber} - {SheetName}";
                var combineIntoSingle = parameters["combineIntoSingle"]?.Value<bool>() ?? false;
                var combinedFileName = parameters["combinedFileName"]?.ToString() ?? "CombinedSheets";

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get sheets to export
                List<ViewSheet> sheetsToExport;
                if (sheetIdArray != null && sheetIdArray.Length > 0)
                {
                    sheetsToExport = sheetIdArray
                        .Select(id => doc.GetElement(new ElementId(id)) as ViewSheet)
                        .Where(s => s != null)
                        .ToList();
                }
                else
                {
                    sheetsToExport = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .OrderBy(s => s.SheetNumber)
                        .ToList();
                }

                if (sheetsToExport.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid sheets to export" });
                }

                // Set up PDF export options
                var pdfOptions = new PDFExportOptions
                {
                    FileName = combinedFileName,
                    Combine = combineIntoSingle,
                    PaperFormat = ExportPaperFormat.Default,
                    ZoomType = ZoomType.Zoom,
                    ZoomPercentage = 100,
                    ExportQuality = PDFExportQualityType.DPI300,
                    ColorDepth = ColorDepthType.Color,
                    RasterQuality = RasterQualityType.High
                };

                var exportedFiles = new List<object>();
                var failedSheets = new List<object>();

                if (combineIntoSingle)
                {
                    // Export all sheets to a single PDF
                    var sheetIds = sheetsToExport.Select(s => s.Id).ToList();

                    try
                    {
                        pdfOptions.FileName = combinedFileName;
                        doc.Export(outputFolder, sheetIds, pdfOptions);

                        exportedFiles.Add(new
                        {
                            fileName = combinedFileName + ".pdf",
                            fullPath = Path.Combine(outputFolder, combinedFileName + ".pdf"),
                            sheetCount = sheetsToExport.Count
                        });
                    }
                    catch (Exception ex)
                    {
                        return ResponseBuilder.FromException(ex).Build();
                    }
                }
                else
                {
                    // Export each sheet individually
                    foreach (var sheet in sheetsToExport)
                    {
                        try
                        {
                            var fileName = fileNamePattern
                                .Replace("{SheetNumber}", SanitizeFileName(sheet.SheetNumber))
                                .Replace("{SheetName}", SanitizeFileName(sheet.Name));

                            pdfOptions.FileName = fileName;
                            var sheetList = new List<ElementId> { sheet.Id };
                            doc.Export(outputFolder, sheetList, pdfOptions);

                            exportedFiles.Add(new
                            {
                                sheetId = sheet.Id.Value,
                                sheetNumber = sheet.SheetNumber,
                                sheetName = sheet.Name,
                                fileName = fileName + ".pdf",
                                fullPath = Path.Combine(outputFolder, fileName + ".pdf")
                            });
                        }
                        catch (Exception ex)
                        {
                            failedSheets.Add(new { sheetId = sheet.Id.Value, sheetNumber = sheet.SheetNumber, error = ex.Message });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    combined = combineIntoSingle,
                    exportedCount = exportedFiles.Count,
                    failedCount = failedSheets.Count,
                    outputFolder,
                    exportedFiles,
                    failedSheets
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting sheets to PDF");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch export to PDF with advanced options
        /// Parameters: outputFolder, exportSheets (bool), exportViews (bool),
        ///             organizeFolders (bool), viewTypes (array, optional),
        ///             sheetFilter (optional: "all", "issued", "notIssued")
        /// </summary>
        [MCPMethod("batchExportPDF", Category = "Document", Description = "Batch export to PDF with advanced options")]
        public static string BatchExportPDF(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var outputFolder = parameters["outputFolder"]?.ToString();
                var exportSheets = parameters["exportSheets"]?.Value<bool>() ?? true;
                var exportViews = parameters["exportViews"]?.Value<bool>() ?? false;
                var organizeFolders = parameters["organizeFolders"]?.Value<bool>() ?? true;
                var viewTypes = parameters["viewTypes"]?.ToObject<string[]>();
                var sheetFilter = parameters["sheetFilter"]?.ToString()?.ToLower() ?? "all";

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                var pdfOptions = new PDFExportOptions
                {
                    Combine = false,
                    ExportQuality = PDFExportQualityType.DPI300,
                    ColorDepth = ColorDepthType.Color,
                    RasterQuality = RasterQualityType.High
                };

                var exportedFiles = new List<object>();
                var failedExports = new List<object>();

                // Export Sheets
                if (exportSheets)
                {
                    var sheetsFolder = organizeFolders ? Path.Combine(outputFolder, "Sheets") : outputFolder;
                    if (!Directory.Exists(sheetsFolder)) Directory.CreateDirectory(sheetsFolder);

                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder);

                    // Apply sheet filter
                    switch (sheetFilter)
                    {
                        case "issued":
                            // Get sheets with issued revisions
                            sheets = sheets.Where(s =>
                            {
                                var revIds = s.GetAllRevisionIds();
                                return revIds.Any(rid =>
                                {
                                    var rev = doc.GetElement(rid) as Revision;
                                    return rev != null && rev.Issued;
                                });
                            });
                            break;
                        case "notissued":
                            sheets = sheets.Where(s =>
                            {
                                var revIds = s.GetAllRevisionIds();
                                return !revIds.Any(rid =>
                                {
                                    var rev = doc.GetElement(rid) as Revision;
                                    return rev != null && rev.Issued;
                                });
                            });
                            break;
                    }

                    foreach (var sheet in sheets.OrderBy(s => s.SheetNumber))
                    {
                        try
                        {
                            var fileName = SanitizeFileName($"{sheet.SheetNumber} - {sheet.Name}");
                            pdfOptions.FileName = fileName;
                            var sheetList = new List<ElementId> { sheet.Id };
                            doc.Export(sheetsFolder, sheetList, pdfOptions);

                            exportedFiles.Add(new
                            {
                                type = "Sheet",
                                id = sheet.Id.Value,
                                number = sheet.SheetNumber,
                                name = sheet.Name,
                                fileName = fileName + ".pdf"
                            });
                        }
                        catch (Exception ex)
                        {
                            failedExports.Add(new { type = "Sheet", id = sheet.Id.Value, name = sheet.SheetNumber, error = ex.Message });
                        }
                    }
                }

                // Export Views
                if (exportViews)
                {
                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted && !(v is ViewSheet));

                    // Filter by view types if specified
                    if (viewTypes != null && viewTypes.Length > 0)
                    {
                        views = views.Where(v => viewTypes.Contains(v.ViewType.ToString(), StringComparer.OrdinalIgnoreCase));
                    }

                    foreach (var view in views)
                    {
                        try
                        {
                            var viewTypeName = view.ViewType.ToString();
                            var viewsFolder = organizeFolders
                                ? Path.Combine(outputFolder, "Views", viewTypeName)
                                : outputFolder;
                            if (!Directory.Exists(viewsFolder)) Directory.CreateDirectory(viewsFolder);

                            var fileName = SanitizeFileName(view.Name);
                            pdfOptions.FileName = fileName;
                            var viewList = new List<ElementId> { view.Id };
                            doc.Export(viewsFolder, viewList, pdfOptions);

                            exportedFiles.Add(new
                            {
                                type = viewTypeName,
                                id = view.Id.Value,
                                name = view.Name,
                                fileName = fileName + ".pdf"
                            });
                        }
                        catch (Exception ex)
                        {
                            failedExports.Add(new { type = view.ViewType.ToString(), id = view.Id.Value, name = view.Name, error = ex.Message });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    exportedCount = exportedFiles.Count,
                    failedCount = failedExports.Count,
                    outputFolder,
                    exportedFiles,
                    failedExports
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in batch PDF export");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available print/export settings
        /// Parameters: none
        /// </summary>
        [MCPMethod("getPrintSettings", Category = "Document", Description = "Get available print/export settings")]
        public static string GetPrintSettings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get print settings
                var printSettings = new FilteredElementCollector(doc)
                    .OfClass(typeof(PrintSetting))
                    .Cast<PrintSetting>()
                    .Select(ps => new
                    {
                        id = ps.Id.Value,
                        name = ps.Name
                    })
                    .ToList();

                // Get paper sizes available
                var paperSizes = Enum.GetValues(typeof(ExportPaperFormat))
                    .Cast<ExportPaperFormat>()
                    .Select(pf => pf.ToString())
                    .ToList();

                // Get available printers (if any configured)
                var printerNames = new List<string>();
                try
                {
                    var printManager = doc.PrintManager;
                    // Note: Getting printer list requires different approach in Revit API
                }
                catch { }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    printSettings,
                    availablePaperFormats = paperSizes,
                    pdfExportQualities = new[] { "DPI72", "DPI150", "DPI300", "DPI600" },
                    colorOptions = new[] { "Color", "GrayScale", "BlackLine" }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting print settings");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Linked Models Methods

        /// <summary>
        /// Get all linked Revit models in the document
        /// </summary>
        [MCPMethod("getLinkedModels", Category = "Document", Description = "Get all linked Revit models in the document")]
        public static string GetLinkedModels(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all Revit link types
                var linkTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>()
                    .Select(lt => new
                    {
                        id = (int)lt.Id.Value,
                        name = lt.Name,
                        isLoaded = lt.GetLinkedFileStatus() == LinkedFileStatus.Loaded,
                        status = lt.GetLinkedFileStatus().ToString(),
                        filePath = GetLinkPath(lt)
                    })
                    .ToList();

                // Get all link instances
                var linkInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(li => new
                    {
                        instanceId = (int)li.Id.Value,
                        typeId = (int)li.GetTypeId().Value,
                        name = li.Name,
                        transform = li.GetTotalTransform() != null ? new
                        {
                            origin = new[] { li.GetTotalTransform().Origin.X, li.GetTotalTransform().Origin.Y, li.GetTotalTransform().Origin.Z },
                            basisX = new[] { li.GetTotalTransform().BasisX.X, li.GetTotalTransform().BasisX.Y, li.GetTotalTransform().BasisX.Z },
                            basisY = new[] { li.GetTotalTransform().BasisY.X, li.GetTotalTransform().BasisY.Y, li.GetTotalTransform().BasisY.Z },
                            basisZ = new[] { li.GetTotalTransform().BasisZ.X, li.GetTotalTransform().BasisZ.Y, li.GetTotalTransform().BasisZ.Z }
                        } : null
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linkTypeCount = linkTypes.Count,
                    linkInstanceCount = linkInstances.Count,
                    linkTypes = linkTypes,
                    linkInstances = linkInstances
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting linked models");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string GetLinkPath(RevitLinkType linkType)
        {
            try
            {
                var externalRef = linkType.GetExternalFileReference();
                if (externalRef != null)
                {
                    return ModelPathUtils.ConvertModelPathToUserVisiblePath(externalRef.GetPath());
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        // Note: ReloadLinks is defined earlier in this file (in the Reload Links region)

        /// <summary>
        /// Set link visibility in a view
        /// </summary>
        [MCPMethod("setLinkVisibility", Category = "Document", Description = "Set link visibility in a view")]
        public static string SetLinkVisibility(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                if (parameters["linkTypeId"] == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "linkTypeId is required" });

                int viewIdInt = parameters["viewId"].ToObject<int>();
                int linkTypeIdInt = parameters["linkTypeId"].ToObject<int>();
                bool visible = parameters["visible"]?.ToObject<bool>() ?? true;

                var view = doc.GetElement(new ElementId(viewIdInt)) as View;
                if (view == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });

                var linkType = doc.GetElement(new ElementId(linkTypeIdInt)) as RevitLinkType;
                if (linkType == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "Link type not found" });

                // Get all instances of this link type
                var linkInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(li => li.GetTypeId().Value == linkTypeIdInt)
                    .ToList();

                using (var trans = new Transaction(doc, "Set Link Visibility"))
                {
                    trans.Start();

                    foreach (var instance in linkInstances)
                    {
                        if (visible)
                            view.UnhideElements(new List<ElementId> { instance.Id });
                        else
                            view.HideElements(new List<ElementId> { instance.Id });
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    linkTypeId = linkTypeIdInt,
                    instancesAffected = linkInstances.Count,
                    visible = visible
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting link visibility");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get worksets from linked models
        /// </summary>
        [MCPMethod("getLinkWorksets", Category = "Document", Description = "Get worksets from linked models")]
        public static string GetLinkWorksets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                int? linkTypeIdInt = parameters["linkTypeId"]?.ToObject<int>();

                var results = new List<object>();

                // Get link instances
                IEnumerable<RevitLinkInstance> linkInstances;
                if (linkTypeIdInt.HasValue)
                {
                    linkInstances = new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>()
                        .Where(li => li.GetTypeId().Value == linkTypeIdInt.Value);
                }
                else
                {
                    linkInstances = new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>();
                }

                foreach (var instance in linkInstances)
                {
                    var linkedDoc = instance.GetLinkDocument();
                    if (linkedDoc == null || !linkedDoc.IsWorkshared)
                        continue;

                    var worksets = new FilteredWorksetCollector(linkedDoc)
                        .OfKind(WorksetKind.UserWorkset)
                        .Select(ws => new
                        {
                            id = ws.Id.IntegerValue,
                            name = ws.Name,
                            isOpen = ws.IsOpen,
                            isEditable = ws.IsEditable,
                            owner = ws.Owner
                        })
                        .ToList();

                    results.Add(new
                    {
                        linkInstanceId = (int)instance.Id.Value,
                        linkName = instance.Name,
                        worksetCount = worksets.Count,
                        worksets = worksets
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    linkedModelCount = results.Count,
                    linkedModels = results
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting link worksets");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        // Note: Design Options methods removed - DesignOptionSet is inaccessible in Revit 2026 API

        #region Export Drafting Views

        /// <summary>
        /// Exports all drafting views to a folder, organized by category based on view names.
        /// Creates subfolders for categories like Roof, Cabinetry, Wall Details, etc.
        /// </summary>
        public static string ExportDraftingViewsToFolder(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputFolder = parameters["outputFolder"]?.ToString();
                var exportFormat = parameters["format"]?.ToString()?.ToUpper() ?? "DWG";
                var createSubfolders = parameters["createSubfolders"]?.Value<bool>() ?? true;
                var imageWidth = parameters["imageWidth"]?.Value<int>() ?? 2048;
                var dwgVersionStr = parameters["dwgVersion"]?.ToString() ?? "AutoCAD2018";

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder parameter is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get all drafting views
                var draftingViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .Where(v => !v.IsTemplate)
                    .OrderBy(v => v.Name)
                    .ToList();

                if (draftingViews.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "No drafting views found in project",
                        exportedCount = 0
                    });
                }

                // Categorize views by name patterns
                var categorizedViews = new Dictionary<string, List<ViewDrafting>>();

                foreach (var view in draftingViews)
                {
                    string category = CategorizeViewByName(view.Name);

                    if (!categorizedViews.ContainsKey(category))
                    {
                        categorizedViews[category] = new List<ViewDrafting>();
                    }
                    categorizedViews[category].Add(view);
                }

                // Set up DWG export options if using DWG format
                DWGExportOptions dwgOptions = null;
                if (exportFormat == "DWG")
                {
                    ACADVersion dwgVersion = ACADVersion.R2018;
                    switch (dwgVersionStr.ToUpper())
                    {
                        case "AUTOCAD2013": dwgVersion = ACADVersion.R2013; break;
                        case "AUTOCAD2018": dwgVersion = ACADVersion.R2018; break;
                        case "AUTOCAD2007": dwgVersion = ACADVersion.R2007; break;
                    }

                    dwgOptions = new DWGExportOptions
                    {
                        FileVersion = dwgVersion,
                        ExportOfSolids = SolidGeometry.ACIS,
                        PropOverrides = PropOverrideMode.ByEntity,
                        SharedCoords = false,
                        MergedViews = false
                    };
                }

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;
                var categorySummary = new Dictionary<string, int>();

                foreach (var kvp in categorizedViews.OrderBy(x => x.Key))
                {
                    string category = kvp.Key;
                    var views = kvp.Value;

                    // Create category subfolder
                    string targetFolder = outputFolder;
                    if (createSubfolders)
                    {
                        string safeCategoryName = SanitizeFileName(category);
                        targetFolder = Path.Combine(outputFolder, safeCategoryName);
                        if (!Directory.Exists(targetFolder))
                        {
                            Directory.CreateDirectory(targetFolder);
                        }
                    }

                    foreach (var view in views)
                    {
                        try
                        {
                            string safeFileName = SanitizeFileName(view.Name);
                            string filePath;

                            if (exportFormat == "DWG")
                            {
                                // Export as DWG
                                var exportIds = new List<ElementId> { view.Id };
                                doc.Export(targetFolder, safeFileName, exportIds, dwgOptions);
                                filePath = Path.Combine(targetFolder, safeFileName + ".dwg");
                            }
                            else
                            {
                                // Export as image (PNG)
                                filePath = Path.Combine(targetFolder, safeFileName + ".png");
                                var imageOptions = new ImageExportOptions
                                {
                                    ExportRange = ExportRange.SetOfViews,
                                    FilePath = filePath,
                                    FitDirection = FitDirectionType.Horizontal,
                                    HLRandWFViewsFileType = ImageFileType.PNG,
                                    ImageResolution = ImageResolution.DPI_300,
                                    PixelSize = imageWidth,
                                    ShadowViewsFileType = ImageFileType.PNG,
                                    ZoomType = ZoomFitType.FitToPage
                                };
                                imageOptions.SetViewsAndSheets(new List<ElementId> { view.Id });
                                doc.ExportImage(imageOptions);
                            }

                            results.Add(new
                            {
                                viewName = view.Name,
                                viewId = (int)view.Id.Value,
                                category,
                                status = "success",
                                path = filePath
                            });

                            successCount++;
                            if (!categorySummary.ContainsKey(category))
                                categorySummary[category] = 0;
                            categorySummary[category]++;
                        }
                        catch (Exception ex)
                        {
                            results.Add(new
                            {
                                viewName = view.Name,
                                viewId = (int)view.Id.Value,
                                category,
                                status = "failed",
                                error = ex.Message
                            });
                            failCount++;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputFolder,
                    format = exportFormat,
                    totalViews = draftingViews.Count,
                    exportedCount = successCount,
                    failedCount = failCount,
                    categorySummary = categorySummary.OrderByDescending(x => x.Value).Select(x => new { category = x.Key, count = x.Value }).ToList(),
                    message = $"Exported {successCount} drafting views to {outputFolder}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Categorizes a view based on its name using common architectural patterns.
        /// </summary>
        private static string CategorizeViewByName(string viewName)
        {
            if (string.IsNullOrEmpty(viewName))
                return "General Details";

            string nameLower = viewName.ToLower();

            // Check patterns in order of specificity
            if (nameLower.Contains("roof") || nameLower.Contains("parapet") || nameLower.Contains("eave") || nameLower.Contains("fascia"))
                return "01 - Roof Details";

            if (nameLower.Contains("cabinet") || nameLower.Contains("millwork") || nameLower.Contains("casework") ||
                nameLower.Contains("counter") || nameLower.Contains("vanity") || nameLower.Contains("shelv"))
                return "02 - Cabinetry & Millwork";

            if (nameLower.Contains("wall") || nameLower.Contains("partition") || nameLower.Contains("cmu") ||
                nameLower.Contains("stucco") || nameLower.Contains("drywall") || nameLower.Contains("gwb"))
                return "03 - Wall Details";

            if (nameLower.Contains("floor") || nameLower.Contains("slab") || nameLower.Contains("tile") ||
                nameLower.Contains("flooring"))
                return "04 - Floor Details";

            if (nameLower.Contains("door") || nameLower.Contains("frame") || nameLower.Contains("jamb"))
                return "05 - Door Details";

            if (nameLower.Contains("window") || nameLower.Contains("glazing") || nameLower.Contains("storefront"))
                return "06 - Window Details";

            if (nameLower.Contains("stair") || nameLower.Contains("railing") || nameLower.Contains("handrail") ||
                nameLower.Contains("guard") || nameLower.Contains("tread") || nameLower.Contains("riser"))
                return "07 - Stair & Railing";

            if (nameLower.Contains("ceiling") || nameLower.Contains("soffit") || nameLower.Contains("act"))
                return "08 - Ceiling Details";

            if (nameLower.Contains("foundation") || nameLower.Contains("footing") || nameLower.Contains("grade beam") ||
                nameLower.Contains("slab on grade"))
                return "09 - Foundation Details";

            if (nameLower.Contains("section") && !nameLower.Contains("wall"))
                return "10 - Building Sections";

            if (nameLower.Contains("elev") && !nameLower.Contains("elevator"))
                return "11 - Elevations";

            if (nameLower.Contains("bathroom") || nameLower.Contains("toilet") || nameLower.Contains("restroom") ||
                nameLower.Contains("shower") || nameLower.Contains("tub"))
                return "12 - Bathroom Details";

            if (nameLower.Contains("kitchen") || nameLower.Contains("appliance"))
                return "13 - Kitchen Details";

            if (nameLower.Contains("mep") || nameLower.Contains("hvac") || nameLower.Contains("plumb") ||
                nameLower.Contains("elec") || nameLower.Contains("mech") || nameLower.Contains("duct"))
                return "14 - MEP Details";

            if (nameLower.Contains("typical") || nameLower.Contains("standard") || nameLower.Contains("general"))
                return "15 - Typical Details";

            if (nameLower.Contains("waterproof") || nameLower.Contains("flash") || nameLower.Contains("membrane") ||
                nameLower.Contains("insulation") || nameLower.Contains("vapor"))
                return "16 - Waterproofing & Insulation";

            if (nameLower.Contains("struct") || nameLower.Contains("beam") || nameLower.Contains("column") ||
                nameLower.Contains("connect"))
                return "17 - Structural Details";

            if (nameLower.Contains("exterior") || nameLower.Contains("facade") || nameLower.Contains("cladding"))
                return "18 - Exterior Details";

            return "99 - General Details";
        }

        #endregion

        #region Export Drafting Views to Revit Files

        /// <summary>
        /// Exports all drafting views to individual Revit project files (.rvt).
        /// Each drafting view becomes a standalone Revit file containing that view and its elements.
        /// Organized by category based on view names.
        /// </summary>
        public static string ExportDraftingViewsToRvt(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputFolder = parameters["outputFolder"]?.ToString();
                var createSubfolders = parameters["createSubfolders"]?.Value<bool>() ?? true;
                var templatePath = parameters["templatePath"]?.ToString();
                var viewIdsParam = parameters["viewIds"]?.ToObject<List<int>>();
                var maxViews = parameters["maxViews"]?.Value<int>() ?? 0; // 0 = no limit

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder parameter is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get drafting views - either specific IDs or all
                List<ViewDrafting> draftingViews;
                if (viewIdsParam != null && viewIdsParam.Count > 0)
                {
                    // Get specific views by ID
                    draftingViews = viewIdsParam
                        .Select(id => doc.GetElement(new ElementId(id)) as ViewDrafting)
                        .Where(v => v != null && !v.IsTemplate)
                        .ToList();
                }
                else
                {
                    // Get all drafting views
                    draftingViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .Where(v => !v.IsTemplate)
                        .OrderBy(v => v.Name)
                        .ToList();

                    // Apply maxViews limit if specified
                    if (maxViews > 0 && draftingViews.Count > maxViews)
                    {
                        draftingViews = draftingViews.Take(maxViews).ToList();
                    }
                }

                if (draftingViews.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "No drafting views found in project",
                        exportedCount = 0
                    });
                }

                // Categorize views
                var categorizedViews = new Dictionary<string, List<ViewDrafting>>();
                foreach (var view in draftingViews)
                {
                    string category = CategorizeViewByName(view.Name);
                    if (!categorizedViews.ContainsKey(category))
                    {
                        categorizedViews[category] = new List<ViewDrafting>();
                    }
                    categorizedViews[category].Add(view);
                }

                var app = uiApp.Application;
                int successCount = 0;
                int failCount = 0;
                var categorySummary = new Dictionary<string, int>();
                var errors = new List<object>();

                foreach (var kvp in categorizedViews.OrderBy(x => x.Key))
                {
                    string category = kvp.Key;
                    var views = kvp.Value;

                    // Create category subfolder
                    string targetFolder = outputFolder;
                    if (createSubfolders)
                    {
                        string safeCategoryName = SanitizeFileName(category);
                        targetFolder = Path.Combine(outputFolder, safeCategoryName);
                        if (!Directory.Exists(targetFolder))
                        {
                            Directory.CreateDirectory(targetFolder);
                        }
                    }

                    foreach (var sourceView in views)
                    {
                        Document newDoc = null;
                        try
                        {
                            string safeFileName = SanitizeFileName(sourceView.Name);
                            string filePath = Path.Combine(targetFolder, safeFileName + ".rvt");

                            // Create new project document
                            if (!string.IsNullOrEmpty(templatePath) && File.Exists(templatePath))
                            {
                                newDoc = app.NewProjectDocument(templatePath);
                            }
                            else
                            {
                                // Create blank project (no template)
                                newDoc = app.NewProjectDocument(UnitSystem.Imperial);
                            }

                            if (newDoc == null)
                            {
                                throw new Exception("Failed to create new project document");
                            }

                            // Get all elements owned by this drafting view (detail lines, text, components, etc.)
                            var elementsInView = new FilteredElementCollector(doc, sourceView.Id)
                                .WhereElementIsNotElementType()
                                .ToElementIds()
                                .ToList();

                            // Copy the view AND its contents together to the new document
                            var copyOptions = new CopyPasteOptions();
                            copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                            ICollection<ElementId> copiedIds = null;
                            ElementId copiedViewId = ElementId.InvalidElementId;

                            using (var trans = new Transaction(newDoc, "Copy Drafting View"))
                            {
                                trans.Start();

                                // Include both the view and all its content elements
                                var allElementsToCopy = new List<ElementId> { sourceView.Id };
                                allElementsToCopy.AddRange(elementsInView);

                                copiedIds = ElementTransformUtils.CopyElements(
                                    doc,
                                    allElementsToCopy,
                                    newDoc,
                                    Transform.Identity,
                                    copyOptions);

                                trans.Commit();
                            }

                            if (copiedIds == null || copiedIds.Count == 0)
                            {
                                throw new Exception("Failed to copy drafting view - no elements copied");
                            }

                            // Find the copied view ID
                            copiedViewId = copiedIds.First();
                            var copiedView = newDoc.GetElement(copiedViewId) as ViewDrafting;
                            if (copiedView == null)
                            {
                                // The first ID might not be the view - search for it
                                foreach (var id in copiedIds)
                                {
                                    var elem = newDoc.GetElement(id) as ViewDrafting;
                                    if (elem != null)
                                    {
                                        copiedView = elem;
                                        copiedViewId = id;
                                        break;
                                    }
                                }
                            }

                            if (copiedView == null)
                            {
                                throw new Exception("Drafting view not found in copied elements");
                            }

                            // PURGE: Clean up the document to make it lightweight
                            PurgeNewDocumentKeepView(newDoc, copiedViewId);

                            // Delete existing file if it exists
                            if (File.Exists(filePath))
                            {
                                File.Delete(filePath);
                            }

                            // Save the new document with compact option
                            var saveOptions = new SaveAsOptions
                            {
                                OverwriteExistingFile = true,
                                Compact = true
                            };
                            newDoc.SaveAs(filePath, saveOptions);

                            successCount++;
                            if (!categorySummary.ContainsKey(category))
                                categorySummary[category] = 0;
                            categorySummary[category]++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new
                            {
                                viewName = sourceView.Name,
                                viewId = (int)sourceView.Id.Value,
                                category,
                                error = ex.Message
                            });
                            failCount++;
                        }
                        finally
                        {
                            // Close the new document without saving again
                            if (newDoc != null && newDoc.IsValidObject)
                            {
                                newDoc.Close(false);
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputFolder,
                    format = "RVT",
                    totalViews = draftingViews.Count,
                    exportedCount = successCount,
                    failedCount = failCount,
                    categorySummary = categorySummary.OrderByDescending(x => x.Value).Select(x => new { category = x.Key, count = x.Value }).ToList(),
                    errors = errors.Count > 0 ? errors : null,
                    message = $"Exported {successCount} drafting views as Revit files to {outputFolder}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Exports drafting views to Revit files - ONE FILE PER CATEGORY.
        /// Much more stable than individual files - creates only 16 files instead of 700+.
        /// Each .rvt file contains all drafting views for that category.
        /// </summary>
        public static string ExportDraftingViewsByCategoryToRvt(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputFolder = parameters["outputFolder"]?.ToString();

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder parameter is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get all drafting views
                var draftingViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .Where(v => !v.IsTemplate)
                    .OrderBy(v => v.Name)
                    .ToList();

                if (draftingViews.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "No drafting views found in project",
                        exportedCount = 0
                    });
                }

                // Categorize views
                var categorizedViews = new Dictionary<string, List<ViewDrafting>>();
                foreach (var view in draftingViews)
                {
                    string category = CategorizeViewByName(view.Name);
                    if (!categorizedViews.ContainsKey(category))
                    {
                        categorizedViews[category] = new List<ViewDrafting>();
                    }
                    categorizedViews[category].Add(view);
                }

                var app = uiApp.Application;
                int successCount = 0;
                int failCount = 0;
                var results = new List<object>();

                // Create ONE file per category
                foreach (var kvp in categorizedViews.OrderBy(x => x.Key))
                {
                    string category = kvp.Key;
                    var views = kvp.Value;
                    Document newDoc = null;

                    try
                    {
                        string safeFileName = SanitizeFileName(category);
                        string filePath = Path.Combine(outputFolder, safeFileName + ".rvt");

                        // Create new project document
                        newDoc = app.NewProjectDocument(UnitSystem.Imperial);

                        if (newDoc == null)
                        {
                            throw new Exception("Failed to create new project document");
                        }

                        // Get ViewFamilyType for drafting view
                        ViewFamilyType draftingViewType = null;
                        using (var trans = new Transaction(newDoc, "Get View Type"))
                        {
                            trans.Start();
                            draftingViewType = new FilteredElementCollector(newDoc)
                                .OfClass(typeof(ViewFamilyType))
                                .Cast<ViewFamilyType>()
                                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting);
                            trans.Commit();
                        }

                        if (draftingViewType == null)
                        {
                            throw new Exception("No drafting view type found");
                        }

                        int viewsExported = 0;

                        // Copy each drafting view from source to destination
                        foreach (var sourceView in views)
                        {
                            try
                            {
                                // Get elements from source drafting view
                                var elementsInView = new FilteredElementCollector(doc, sourceView.Id)
                                    .WhereElementIsNotElementType()
                                    .ToElementIds()
                                    .ToList();

                                ViewDrafting newView = null;

                                using (var trans = new Transaction(newDoc, "Create View"))
                                {
                                    trans.Start();
                                    newView = ViewDrafting.Create(newDoc, draftingViewType.Id);
                                    newView.Name = sourceView.Name;
                                    newView.Scale = sourceView.Scale;
                                    trans.Commit();
                                }

                                // Copy elements
                                if (elementsInView.Count > 0 && newView != null)
                                {
                                    using (var trans = new Transaction(newDoc, "Copy Elements"))
                                    {
                                        trans.Start();
                                        try
                                        {
                                            ElementTransformUtils.CopyElements(
                                                sourceView,
                                                elementsInView,
                                                newView,
                                                Transform.Identity,
                                                new CopyPasteOptions());
                                        }
                                        catch { }
                                        trans.Commit();
                                    }
                                }

                                viewsExported++;
                            }
                            catch { }
                        }

                        // Light cleanup - just remove default views we don't need
                        using (var trans = new Transaction(newDoc, "Cleanup"))
                        {
                            trans.Start();
                            try
                            {
                                // Delete default floor plan views
                                var defaultViews = new FilteredElementCollector(newDoc)
                                    .OfClass(typeof(View))
                                    .Cast<View>()
                                    .Where(v => v.ViewType == ViewType.FloorPlan ||
                                               v.ViewType == ViewType.CeilingPlan ||
                                               v.ViewType == ViewType.Elevation ||
                                               v.ViewType == ViewType.ThreeD)
                                    .ToList();

                                foreach (var v in defaultViews)
                                {
                                    try { newDoc.Delete(v.Id); } catch { }
                                }

                                // Delete extra levels
                                var levels = new FilteredElementCollector(newDoc)
                                    .OfClass(typeof(Level))
                                    .ToElementIds()
                                    .Skip(1)
                                    .ToList();
                                foreach (var id in levels)
                                {
                                    try { newDoc.Delete(id); } catch { }
                                }
                            }
                            catch { }
                            trans.Commit();
                        }

                        // Delete existing file if it exists
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }

                        // Save
                        var saveOptions = new SaveAsOptions
                        {
                            OverwriteExistingFile = true,
                            Compact = true
                        };
                        newDoc.SaveAs(filePath, saveOptions);

                        results.Add(new
                        {
                            category,
                            file = filePath,
                            viewCount = viewsExported,
                            status = "success"
                        });

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            category,
                            viewCount = views.Count,
                            status = "failed",
                            error = ex.Message
                        });
                        failCount++;
                    }
                    finally
                    {
                        if (newDoc != null && newDoc.IsValidObject)
                        {
                            newDoc.Close(false);
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputFolder,
                    format = "RVT (by category)",
                    totalCategories = categorizedViews.Count,
                    totalViews = draftingViews.Count,
                    filesCreated = successCount,
                    filesFailed = failCount,
                    results,
                    message = $"Created {successCount} category files containing {draftingViews.Count} drafting views"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Purges a newly created document to remove unused elements.
        /// Light version - just removes extra views to keep file clean and stable.
        /// </summary>
        private static void PurgeNewDocument(Document doc, ElementId keepViewId)
        {
            // Single pass - just remove extra views, keep it simple and stable
            using (var trans = new Transaction(doc, "Clean Document"))
            {
                trans.Start();

                var elementsToDelete = new List<ElementId>();

                // 1. Delete all views except the one we want to keep (and required templates)
                try
                {
                    var allViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => v.Id != keepViewId &&
                                   !v.IsTemplate &&
                                   v.ViewType != ViewType.ProjectBrowser &&
                                   v.ViewType != ViewType.SystemBrowser)
                        .ToList();

                    foreach (var view in allViews)
                    {
                        try
                        {
                            // Skip if it's a required system view
                            if (view.ViewType == ViewType.Internal ||
                                view.ViewType == ViewType.Undefined)
                                continue;

                            elementsToDelete.Add(view.Id);
                        }
                        catch { }
                    }
                }
                catch { }

                // 2. Delete extra levels (keep first one)
                try
                {
                    var levels = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .ToElementIds()
                        .Skip(1)
                        .ToList();
                    elementsToDelete.AddRange(levels);
                }
                catch { }

                // 3. Delete sheets
                try
                {
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .ToElementIds();
                    elementsToDelete.AddRange(sheets);
                }
                catch { }

                // Delete elements one by one, skip failures
                foreach (var id in elementsToDelete.Distinct())
                {
                    try
                    {
                        var elem = doc.GetElement(id);
                        if (elem != null)
                            doc.Delete(id);
                    }
                    catch { }
                }

                trans.Commit();
            }
        }

        private static bool IsViewTypeUsed(Document doc, ViewFamilyType viewType)
        {
            // Check if any views use this type
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => v.GetTypeId() == viewType.Id);
        }

        private static bool IsElementUsed(Document doc, Element element)
        {
            // Simplified check - in production would need more comprehensive analysis
            try
            {
                var dependents = element.GetDependentElements(null);
                return dependents != null && dependents.Count > 1;
            }
            catch
            {
                return true; // Assume used if we can't determine
            }
        }

        /// <summary>
        /// Exports all legend views to individual .rvt files.
        /// Copies the legend VIEW element itself to new documents, preserving the view and its contents.
        /// </summary>
        public static string ExportLegendsToRvt(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "UIApplication is null" });
                }

                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputFolder = parameters?["outputFolder"]?.ToString();

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder parameter is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get all legend views with safe filtering
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .ToList();

                var legends = allViews
                    .Where(v => v != null && v.ViewType == ViewType.Legend && !v.IsTemplate)
                    .OrderBy(v => v.Name)
                    .ToList();

                if (legends.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "No legend views found in project",
                        exportedCount = 0
                    });
                }

                var app = uiApp.Application;
                int successCount = 0;
                int failCount = 0;
                var exportedFiles = new List<object>();
                var errors = new List<object>();

                foreach (var legend in legends)
                {
                    Document newDoc = null;
                    try
                    {
                        string safeFileName = SanitizeFileName(legend.Name);
                        string filePath = Path.Combine(outputFolder, safeFileName + ".rvt");

                        // Create new project document
                        newDoc = app.NewProjectDocument(UnitSystem.Imperial);

                        // Copy the legend view element itself to the new document
                        var copyOptions = new CopyPasteOptions();
                        copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                        ICollection<ElementId> copiedIds = null;

                        using (var trans = new Transaction(newDoc, "Copy Legend"))
                        {
                            trans.Start();

                            // Copy the legend view (this copies the view AND its contents)
                            var sourceIds = new List<ElementId> { legend.Id };
                            copiedIds = ElementTransformUtils.CopyElements(
                                doc,
                                sourceIds,
                                newDoc,
                                Transform.Identity,
                                copyOptions);

                            trans.Commit();
                        }

                        if (copiedIds != null && copiedIds.Count > 0)
                        {
                            // Light purge - remove default views we don't need
                            PurgeNewDocumentKeepView(newDoc, copiedIds.First());

                            // Save the document
                            var saveOptions = new SaveAsOptions
                            {
                                OverwriteExistingFile = true,
                                Compact = true
                            };
                            newDoc.SaveAs(filePath, saveOptions);

                            exportedFiles.Add(new
                            {
                                id = legend.Id.Value,
                                name = legend.Name,
                                fileName = safeFileName + ".rvt",
                                scale = legend.Scale
                            });
                            successCount++;
                        }
                        else
                        {
                            throw new Exception("Failed to copy legend view");
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        errors.Add(new
                        {
                            viewName = legend.Name,
                            error = ex.Message
                        });
                    }
                    finally
                    {
                        if (newDoc != null)
                        {
                            newDoc.Close(false);
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalLegends = legends.Count,
                    exportedCount = successCount,
                    failedCount = failCount,
                    outputFolder = outputFolder,
                    format = "RVT",
                    exportedFiles = exportedFiles,
                    errors = errors.Count > 0 ? errors : null
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper class to handle duplicate type names during copy operations
        /// </summary>
        private class DuplicateTypeNamesHandler : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                // Use the destination types when duplicates are found
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        /// <summary>
        /// Purges a new document but keeps the specified view
        /// </summary>
        private static void PurgeNewDocumentKeepView(Document doc, ElementId keepViewId)
        {
            using (var trans = new Transaction(doc, "Purge"))
            {
                trans.Start();

                // Delete default views except the one we want to keep
                var viewsToDelete = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.Id != keepViewId && !v.IsTemplate && v.CanBePrinted)
                    .Select(v => v.Id)
                    .ToList();

                foreach (var viewId in viewsToDelete)
                {
                    try
                    {
                        doc.Delete(viewId);
                    }
                    catch { } // Some views can't be deleted, that's OK
                }

                trans.Commit();
            }
        }

        /// <summary>
        /// Exports all schedules to individual .rvt files.
        /// Copies the schedule VIEW element itself (with its definition: columns, formatting, filters).
        /// The schedule will be empty but will populate when loaded into a project with matching elements.
        /// </summary>
        public static string ExportSchedulesToRvt(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var outputFolder = parameters["outputFolder"]?.ToString();
                var createSubfolders = parameters["createSubfolders"]?.Value<bool>() ?? true;

                if (string.IsNullOrEmpty(outputFolder))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder parameter is required" });
                }

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }

                // Get all schedule views (not templates, not revision schedules)
                var schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
                    .OrderBy(v => v.Name)
                    .ToList();

                if (schedules.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "No schedules found in project",
                        exportedCount = 0
                    });
                }

                // Categorize schedules by type
                var categorizedSchedules = new Dictionary<string, List<ViewSchedule>>();
                foreach (var sched in schedules)
                {
                    string category = CategorizeSchedule(sched);
                    if (!categorizedSchedules.ContainsKey(category))
                    {
                        categorizedSchedules[category] = new List<ViewSchedule>();
                    }
                    categorizedSchedules[category].Add(sched);
                }

                var app = uiApp.Application;
                int successCount = 0;
                int failCount = 0;
                var categorySummary = new Dictionary<string, int>();
                var exportedFiles = new List<object>();
                var errors = new List<object>();

                foreach (var kvp in categorizedSchedules.OrderBy(x => x.Key))
                {
                    string category = kvp.Key;
                    var scheds = kvp.Value;
                    categorySummary[category] = scheds.Count;

                    // Create category subfolder
                    string targetFolder = outputFolder;
                    if (createSubfolders)
                    {
                        string safeCategoryName = SanitizeFileName(category);
                        targetFolder = Path.Combine(outputFolder, safeCategoryName);
                        if (!Directory.Exists(targetFolder))
                        {
                            Directory.CreateDirectory(targetFolder);
                        }
                    }

                    foreach (var schedule in scheds)
                    {
                        Document newDoc = null;
                        try
                        {
                            string safeFileName = SanitizeFileName(schedule.Name);
                            string filePath = Path.Combine(targetFolder, safeFileName + ".rvt");

                            // Create new project document
                            newDoc = app.NewProjectDocument(UnitSystem.Imperial);

                            // Copy the schedule view element itself to the new document
                            var copyOptions = new CopyPasteOptions();
                            copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                            ICollection<ElementId> copiedIds = null;

                            using (var trans = new Transaction(newDoc, "Copy Schedule"))
                            {
                                trans.Start();

                                // Copy the schedule view (this copies the view definition)
                                var sourceIds = new List<ElementId> { schedule.Id };
                                copiedIds = ElementTransformUtils.CopyElements(
                                    doc,
                                    sourceIds,
                                    newDoc,
                                    Transform.Identity,
                                    copyOptions);

                                trans.Commit();
                            }

                            if (copiedIds != null && copiedIds.Count > 0)
                            {
                                // Light purge
                                PurgeNewDocumentKeepView(newDoc, copiedIds.First());

                                // Save the document
                                var saveOptions = new SaveAsOptions
                                {
                                    OverwriteExistingFile = true,
                                    Compact = true
                                };
                                newDoc.SaveAs(filePath, saveOptions);

                                // Get schedule info for report
                                var tableData = schedule.GetTableData();
                                var sectionData = tableData.GetSectionData(SectionType.Body);

                                exportedFiles.Add(new
                                {
                                    id = schedule.Id.Value,
                                    name = schedule.Name,
                                    category = category,
                                    fileName = safeFileName + ".rvt",
                                    columns = sectionData.NumberOfColumns
                                });
                                successCount++;
                            }
                            else
                            {
                                throw new Exception("Failed to copy schedule");
                            }
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            errors.Add(new
                            {
                                scheduleName = schedule.Name,
                                error = ex.Message
                            });
                        }
                        finally
                        {
                            if (newDoc != null)
                            {
                                newDoc.Close(false);
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalSchedules = schedules.Count,
                    exportedCount = successCount,
                    failedCount = failCount,
                    format = "RVT",
                    note = "Schedule definitions exported. They will populate with data when loaded into a project with matching elements.",
                    categorySummary = categorySummary,
                    outputFolder = outputFolder,
                    exportedFiles = exportedFiles,
                    errors = errors.Count > 0 ? errors : null
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string CategorizeSchedule(ViewSchedule schedule)
        {
            string name = schedule.Name.ToUpper();

            if (name.Contains("DOOR")) return "Door Schedules";
            if (name.Contains("WINDOW")) return "Window Schedules";
            if (name.Contains("ROOM")) return "Room Schedules";
            if (name.Contains("FINISH")) return "Finish Schedules";
            if (name.Contains("FIXTURE") || name.Contains("PLUMBING")) return "Plumbing Schedules";
            if (name.Contains("LIGHTING") || name.Contains("LIGHT")) return "Lighting Schedules";
            if (name.Contains("ELECTRICAL") || name.Contains("PANEL")) return "Electrical Schedules";
            if (name.Contains("EQUIPMENT")) return "Equipment Schedules";
            if (name.Contains("WALL")) return "Wall Schedules";
            if (name.Contains("FLOOR")) return "Floor Schedules";
            if (name.Contains("CEILING")) return "Ceiling Schedules";
            if (name.Contains("KEY") || name.Contains("KEYNOTE")) return "Keynote Schedules";
            if (name.Contains("SHEET")) return "Sheet Schedules";
            if (name.Contains("AREA")) return "Area Schedules";
            if (name.Contains("MATERIAL")) return "Material Schedules";
            if (name.Contains("REVISION")) return "Revision Schedules";

            return "Other Schedules";
        }

        private static void PurgeNewDocumentForSchedule(Document doc)
        {
            // Simple purge for schedule documents - just remove extra views and levels
            var elementsToDelete = new List<ElementId>();

            try
            {
                using (var trans = new Transaction(doc, "Purge"))
                {
                    trans.Start();

                    // Delete extra levels (keep first one)
                    try
                    {
                        var levels = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level))
                            .ToElementIds()
                            .Skip(1)
                            .ToList();

                        foreach (var id in levels)
                        {
                            try { doc.Delete(id); } catch { }
                        }
                    }
                    catch { }

                    // Delete sheets
                    try
                    {
                        var sheets = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSheet))
                            .ToElementIds()
                            .ToList();

                        foreach (var id in sheets)
                        {
                            try { doc.Delete(id); } catch { }
                        }
                    }
                    catch { }

                    trans.Commit();
                }
            }
            catch { }
        }

        #endregion

        #region NWC Export and Batch Image Export

        /// <summary>
        /// Export model to Navisworks NWC format.
        /// </summary>
        [MCPMethod("exportToNWC", Category = "Document", Description = "Export model to Navisworks NWC format")]
        public static string ExportToNWC(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });

                var outputFolder = parameters["outputFolder"]?.ToString();
                if (string.IsNullOrEmpty(outputFolder))
                {
                    outputFolder = System.IO.Path.GetDirectoryName(doc.PathName);
                    if (string.IsNullOrEmpty(outputFolder))
                        return JsonConvert.SerializeObject(new { success = false, error = "outputFolder required (document not saved)" });
                }

                if (!System.IO.Directory.Exists(outputFolder))
                    System.IO.Directory.CreateDirectory(outputFolder);

                var fileName = parameters["fileName"]?.ToString()
                    ?? System.IO.Path.GetFileNameWithoutExtension(doc.PathName);

                // Check if NavisworksExportOptions is available
                var options = new NavisworksExportOptions();

                // Configure export settings
                var exportScope = parameters["scope"]?.ToString()?.ToLower();
                if (exportScope == "view")
                {
                    var viewId = parameters["viewId"]?.Value<int>();
                    if (viewId.HasValue)
                    {
                        options.ExportScope = NavisworksExportScope.View;
                        options.ViewId = new ElementId(viewId.Value);
                    }
                }
                else
                {
                    options.ExportScope = NavisworksExportScope.Model;
                }

                options.ExportLinks = parameters["exportLinks"]?.Value<bool>() ?? false;
                options.ExportRoomAsAttribute = parameters["exportRoomAttributes"]?.Value<bool>() ?? true;
                options.ExportRoomGeometry = parameters["exportRoomGeometry"]?.Value<bool>() ?? false;
                options.ConvertElementProperties = parameters["convertProperties"]?.Value<bool>() ?? true;
                options.DivideFileIntoLevels = parameters["divideByLevels"]?.Value<bool>() ?? true;
                options.ExportUrls = false;
                options.FindMissingMaterials = true;

                doc.Export(outputFolder, fileName, options);

                var fullPath = System.IO.Path.Combine(outputFolder, fileName + ".nwc");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    filePath = fullPath,
                    fileName = fileName + ".nwc",
                    fileExists = System.IO.File.Exists(fullPath),
                    message = $"Exported to {fullPath}"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    hint = ex.Message.Contains("Navisworks") ?
                        "Navisworks exporter add-in may not be installed" : null
                });
            }
        }

        /// <summary>
        /// Batch export view/sheet images (PNG/JPEG) for review or markup.
        /// </summary>
        [MCPMethod("batchExportImages", Category = "Document", Description = "Batch export view/sheet images for review or markup")]
        public static string BatchExportImages(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });

                var outputFolder = parameters["outputFolder"]?.ToString();
                if (string.IsNullOrEmpty(outputFolder))
                    return JsonConvert.SerializeObject(new { success = false, error = "outputFolder is required" });

                if (!System.IO.Directory.Exists(outputFolder))
                    System.IO.Directory.CreateDirectory(outputFolder);

                var format = parameters["format"]?.ToString()?.ToUpper() ?? "PNG";
                var pixelSize = parameters["pixelSize"]?.Value<int>() ?? 1920;
                var exportType = parameters["exportType"]?.ToString()?.ToLower() ?? "sheets";

                // Collect views to export
                var viewsToExport = new List<View>();

                if (exportType == "sheets")
                {
                    var sheetNumbers = parameters["sheetNumbers"]?.ToObject<string[]>();
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder);

                    if (sheetNumbers != null && sheetNumbers.Length > 0)
                        sheets = sheets.Where(s => sheetNumbers.Contains(s.SheetNumber));

                    viewsToExport.AddRange(sheets);
                }
                else if (exportType == "views")
                {
                    var viewIds = parameters["viewIds"]?.ToObject<int[]>();
                    if (viewIds != null)
                    {
                        foreach (var id in viewIds)
                        {
                            var view = doc.GetElement(new ElementId(id)) as View;
                            if (view != null) viewsToExport.Add(view);
                        }
                    }
                }

                if (viewsToExport.Count == 0)
                    return JsonConvert.SerializeObject(new { success = false, error = "No views/sheets found to export" });

                var imageType = format == "JPEG" || format == "JPG"
                    ? ImageFileType.JPEGLossless
                    : ImageFileType.PNG;

                var results = new List<object>();
                var successCount = 0;

                foreach (var view in viewsToExport)
                {
                    try
                    {
                        var viewName = view is ViewSheet sheet
                            ? $"{sheet.SheetNumber} - {sheet.Name}"
                            : view.Name;

                        // Clean filename
                        var safeName = string.Join("_",
                            viewName.Split(System.IO.Path.GetInvalidFileNameChars()));

                        var exportOptions = new ImageExportOptions
                        {
                            ZoomType = ZoomFitType.FitToPage,
                            PixelSize = pixelSize,
                            FilePath = System.IO.Path.Combine(outputFolder, safeName),
                            HLRandWFViewsFileType = imageType,
                            ShadowViewsFileType = imageType,
                            ExportRange = ExportRange.SetOfViews
                        };
                        exportOptions.SetViewsAndSheets(new List<ElementId> { view.Id });

                        doc.ExportImage(exportOptions);

                        var extension = format == "JPEG" || format == "JPG" ? ".jpg" : ".png";
                        results.Add(new
                        {
                            viewName,
                            fileName = safeName + extension,
                            success = true
                        });
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            viewName = view.Name,
                            success = false,
                            error = ex.Message
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    outputFolder,
                    format,
                    totalRequested = viewsToExport.Count,
                    successCount,
                    errorCount = viewsToExport.Count - successCount,
                    results
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
