using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Methods for batch text operations across detail library files
    /// </summary>
    public static class BatchTextMethods
    {
        private static string _logPath = @"D:\RevitMCPBridge2026\logs\batch_text.log";

        #region Text Type Operations

        /// <summary>
        /// Gets all text types in the current document
        /// </summary>
        public static string GetTextTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType));

                var textTypes = new List<object>();

                foreach (TextNoteType textType in collector)
                {
                    var fontParam = textType.get_Parameter(BuiltInParameter.TEXT_FONT);
                    var sizeParam = textType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    var boldParam = textType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD);

                    textTypes.Add(new
                    {
                        id = textType.Id.Value,
                        name = textType.Name,
                        font = fontParam?.AsString() ?? "Unknown",
                        sizeInches = sizeParam != null ? Math.Round(sizeParam.AsDouble() * 12, 4) : 0,
                        bold = boldParam?.AsInteger() == 1
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = textTypes.Count,
                    textTypes = textTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates or gets a standard text type with specified parameters
        /// </summary>
        public static string CreateStandardTextType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                string typeName = parameters["typeName"]?.ToString() ?? "3/32\" ARIAL NOTES";
                string fontName = parameters["fontName"]?.ToString() ?? "Arial";
                double sizeInches = parameters["sizeInches"]?.ToObject<double>() ?? 0.09375; // 3/32"
                bool bold = parameters["bold"]?.ToObject<bool>() ?? false;

                double sizeFeet = sizeInches / 12.0;

                // Check if type already exists
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType));

                TextNoteType existingType = null;
                TextNoteType sourceType = null;

                foreach (TextNoteType tt in collector)
                {
                    if (tt.Name == typeName)
                    {
                        existingType = tt;
                    }
                    if (sourceType == null)
                    {
                        sourceType = tt; // Get first type as source for duplication
                    }
                }

                if (existingType != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeId = existingType.Id.Value,
                        typeName = existingType.Name,
                        message = "Text type already exists"
                    });
                }

                if (sourceType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No text types found in document to duplicate from"
                    });
                }

                using (var trans = new Transaction(doc, "Create Standard Text Type"))
                {
                    trans.Start();

                    // Duplicate the source type
                    var newType = sourceType.Duplicate(typeName) as TextNoteType;

                    // Set font
                    var fontParam = newType.get_Parameter(BuiltInParameter.TEXT_FONT);
                    if (fontParam != null && !fontParam.IsReadOnly)
                    {
                        fontParam.Set(fontName);
                    }

                    // Set size
                    var sizeParam = newType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    if (sizeParam != null && !sizeParam.IsReadOnly)
                    {
                        sizeParam.Set(sizeFeet);
                    }

                    // Set bold
                    var boldParam = newType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD);
                    if (boldParam != null && !boldParam.IsReadOnly)
                    {
                        boldParam.Set(bold ? 1 : 0);
                    }

                    // Set italic off
                    var italicParam = newType.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC);
                    if (italicParam != null && !italicParam.IsReadOnly)
                    {
                        italicParam.Set(0);
                    }

                    // Set underline off
                    var underlineParam = newType.get_Parameter(BuiltInParameter.TEXT_STYLE_UNDERLINE);
                    if (underlineParam != null && !underlineParam.IsReadOnly)
                    {
                        underlineParam.Set(0);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeId = newType.Id.Value,
                        typeName = newType.Name,
                        font = fontName,
                        sizeInches = sizeInches,
                        bold = bold,
                        message = "Text type created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all text notes in current document
        /// </summary>
        public static string GetTextNotes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNote));

                var textNotes = new List<object>();

                foreach (TextNote tn in collector)
                {
                    var textType = doc.GetElement(tn.GetTypeId()) as TextNoteType;

                    textNotes.Add(new
                    {
                        id = tn.Id.Value,
                        text = tn.Text,
                        typeId = tn.GetTypeId().Value,
                        typeName = textType?.Name ?? "Unknown",
                        viewId = tn.OwnerViewId.Value
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = textNotes.Count,
                    textNotes = textNotes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Standardizes all text in the current document:
        /// - Changes all TextNote types to specified type
        /// - Converts all text to UPPERCASE
        /// </summary>
        public static string StandardizeDocumentText(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                string typeName = parameters["typeName"]?.ToString() ?? "3/32\" ARIAL NOTES";
                string fontName = parameters["fontName"]?.ToString() ?? "Arial";
                double sizeInches = parameters["sizeInches"]?.ToObject<double>() ?? 0.09375;
                bool bold = parameters["bold"]?.ToObject<bool>() ?? false;
                bool toUppercase = parameters["toUppercase"]?.ToObject<bool>() ?? true;
                string arrowheadName = parameters["arrowheadName"]?.ToString();

                double sizeFeet = sizeInches / 12.0;

                int typeChanges = 0;
                int textChanges = 0;
                int totalNotes = 0;

                using (var trans = new Transaction(doc, "Standardize Document Text"))
                {
                    trans.Start();

                    // First, get or create the target text type
                    var typeCollector = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNoteType));

                    TextNoteType targetType = null;
                    TextNoteType sourceType = null;

                    foreach (TextNoteType tt in typeCollector)
                    {
                        if (tt.Name == typeName)
                        {
                            targetType = tt;
                        }
                        if (sourceType == null)
                        {
                            sourceType = tt;
                        }
                    }

                    // Create if doesn't exist
                    if (targetType == null && sourceType != null)
                    {
                        targetType = sourceType.Duplicate(typeName) as TextNoteType;

                        var fontParam = targetType.get_Parameter(BuiltInParameter.TEXT_FONT);
                        if (fontParam != null && !fontParam.IsReadOnly) fontParam.Set(fontName);

                        var sizeParam = targetType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                        if (sizeParam != null && !sizeParam.IsReadOnly) sizeParam.Set(sizeFeet);

                        var boldParam = targetType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD);
                        if (boldParam != null && !boldParam.IsReadOnly) boldParam.Set(bold ? 1 : 0);

                        var italicParam = targetType.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC);
                        if (italicParam != null && !italicParam.IsReadOnly) italicParam.Set(0);

                        var underlineParam = targetType.get_Parameter(BuiltInParameter.TEXT_STYLE_UNDERLINE);
                        if (underlineParam != null && !underlineParam.IsReadOnly) underlineParam.Set(0);
                    }

                    // Set arrowhead if specified
                    if (!string.IsNullOrEmpty(arrowheadName) && targetType != null)
                    {
                        // Find arrowhead by name
                        var arrowheadCollector = new FilteredElementCollector(doc)
                            .OfClass(typeof(ElementType))
                            .WhereElementIsElementType();

                        ElementId arrowheadId = null;
                        foreach (ElementType et in arrowheadCollector)
                        {
                            if (et.Name.IndexOf(arrowheadName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                arrowheadName.IndexOf(et.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // Check if it's an arrowhead type
                                if (et.Category != null && et.Category.Name.Contains("Arrow"))
                                {
                                    arrowheadId = et.Id;
                                    break;
                                }
                                // Also try by family name containing "Arrow"
                                if (et.FamilyName != null && et.FamilyName.Contains("Arrow"))
                                {
                                    arrowheadId = et.Id;
                                    break;
                                }
                            }
                        }

                        // Try direct parameter setting
                        if (arrowheadId != null)
                        {
                            var arrowParam = targetType.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD);
                            if (arrowParam != null && !arrowParam.IsReadOnly)
                            {
                                arrowParam.Set(arrowheadId);
                            }
                        }
                    }

                    if (targetType == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Could not find or create target text type"
                        });
                    }

                    ElementId targetTypeId = targetType.Id;

                    // Process all text notes
                    var noteCollector = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNote));

                    foreach (TextNote tn in noteCollector)
                    {
                        totalNotes++;

                        // Change type if different
                        if (tn.GetTypeId() != targetTypeId)
                        {
                            tn.ChangeTypeId(targetTypeId);
                            typeChanges++;
                        }

                        // Convert to uppercase
                        if (toUppercase)
                        {
                            string currentText = tn.Text;
                            if (!string.IsNullOrEmpty(currentText))
                            {
                                string upperText = currentText.ToUpper();
                                if (upperText != currentText)
                                {
                                    tn.Text = upperText;
                                    textChanges++;
                                }
                            }
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalTextNotes = totalNotes,
                    typeChanges = typeChanges,
                    uppercaseChanges = textChanges,
                    targetTypeName = typeName,
                    message = $"Standardized {totalNotes} text notes"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Opens a detail file, standardizes text, and saves it
        /// </summary>
        public static string ProcessDetailFile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                if (parameters["filePath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filePath is required"
                    });
                }

                string filePath = parameters["filePath"].ToString();
                string typeName = parameters["typeName"]?.ToString() ?? "3/32\" ARIAL NOTES";
                string fontName = parameters["fontName"]?.ToString() ?? "Arial";
                double sizeInches = parameters["sizeInches"]?.ToObject<double>() ?? 0.09375;
                bool bold = parameters["bold"]?.ToObject<bool>() ?? false;
                bool toUppercase = parameters["toUppercase"]?.ToObject<bool>() ?? true;
                bool saveFile = parameters["saveFile"]?.ToObject<bool>() ?? true;
                string arrowheadName = parameters["arrowheadName"]?.ToString();

                if (!File.Exists(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"File not found: {filePath}"
                    });
                }

                double sizeFeet = sizeInches / 12.0;
                string fileName = Path.GetFileName(filePath);

                // Open the document
                var openOptions = new OpenOptions();
                openOptions.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;

                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                Document doc = app.OpenDocumentFile(modelPath, openOptions);

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Could not open file: {fileName}"
                    });
                }

                int typeChanges = 0;
                int textChanges = 0;
                int totalNotes = 0;

                try
                {
                    using (var trans = new Transaction(doc, "Standardize Text"))
                    {
                        trans.Start();

                        // Get or create target type
                        var typeCollector = new FilteredElementCollector(doc)
                            .OfClass(typeof(TextNoteType));

                        TextNoteType targetType = null;
                        TextNoteType sourceType = null;

                        foreach (TextNoteType tt in typeCollector)
                        {
                            if (tt.Name == typeName)
                            {
                                targetType = tt;
                            }
                            if (sourceType == null)
                            {
                                sourceType = tt;
                            }
                        }

                        if (targetType == null && sourceType != null)
                        {
                            targetType = sourceType.Duplicate(typeName) as TextNoteType;

                            var fontParam = targetType.get_Parameter(BuiltInParameter.TEXT_FONT);
                            if (fontParam != null && !fontParam.IsReadOnly) fontParam.Set(fontName);

                            var sizeParam = targetType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            if (sizeParam != null && !sizeParam.IsReadOnly) sizeParam.Set(sizeFeet);

                            var boldParam = targetType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD);
                            if (boldParam != null && !boldParam.IsReadOnly) boldParam.Set(bold ? 1 : 0);

                            var italicParam = targetType.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC);
                            if (italicParam != null && !italicParam.IsReadOnly) italicParam.Set(0);

                            var underlineParam = targetType.get_Parameter(BuiltInParameter.TEXT_STYLE_UNDERLINE);
                            if (underlineParam != null && !underlineParam.IsReadOnly) underlineParam.Set(0);
                        }

                        // Set arrowhead if specified
                        if (!string.IsNullOrEmpty(arrowheadName) && targetType != null)
                        {
                            var arrowheadCollector = new FilteredElementCollector(doc)
                                .OfClass(typeof(ElementType))
                                .WhereElementIsElementType();

                            ElementId arrowheadId = null;
                            foreach (ElementType et in arrowheadCollector)
                            {
                                if (et.Name.IndexOf(arrowheadName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    arrowheadName.IndexOf(et.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (et.Category != null && et.Category.Name.Contains("Arrow"))
                                    {
                                        arrowheadId = et.Id;
                                        break;
                                    }
                                    if (et.FamilyName != null && et.FamilyName.Contains("Arrow"))
                                    {
                                        arrowheadId = et.Id;
                                        break;
                                    }
                                }
                            }

                            if (arrowheadId != null)
                            {
                                var arrowParam = targetType.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD);
                                if (arrowParam != null && !arrowParam.IsReadOnly)
                                {
                                    arrowParam.Set(arrowheadId);
                                }
                            }
                        }

                        if (targetType != null)
                        {
                            ElementId targetTypeId = targetType.Id;

                            var noteCollector = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNote));

                            foreach (TextNote tn in noteCollector)
                            {
                                totalNotes++;

                                if (tn.GetTypeId() != targetTypeId)
                                {
                                    tn.ChangeTypeId(targetTypeId);
                                    typeChanges++;
                                }

                                if (toUppercase)
                                {
                                    string currentText = tn.Text;
                                    if (!string.IsNullOrEmpty(currentText))
                                    {
                                        string upperText = currentText.ToUpper();
                                        if (upperText != currentText)
                                        {
                                            tn.Text = upperText;
                                            textChanges++;
                                        }
                                    }
                                }
                            }
                        }

                        trans.Commit();
                    }

                    // Save the file
                    if (saveFile && (typeChanges > 0 || textChanges > 0))
                    {
                        var saveOptions = new SaveAsOptions();
                        saveOptions.OverwriteExistingFile = true;
                        doc.SaveAs(filePath, saveOptions);
                    }
                }
                finally
                {
                    doc.Close(false);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    fileName = fileName,
                    totalTextNotes = totalNotes,
                    typeChanges = typeChanges,
                    uppercaseChanges = textChanges,
                    saved = saveFile && (typeChanges > 0 || textChanges > 0)
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets list of all RVT files in the detail library
        /// </summary>
        public static string GetDetailLibraryFiles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string libraryPath = parameters["libraryPath"]?.ToString()
                    ?? @"D:\Revit Detail Libraries\Revit Details";

                if (!Directory.Exists(libraryPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Library path not found: {libraryPath}"
                    });
                }

                // Filter out Revit backup files (*.0001.rvt, *.0002.rvt, etc.)
                var backupPattern = new System.Text.RegularExpressions.Regex(@"\.\d{4}\.rvt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var files = Directory.GetFiles(libraryPath, "*.rvt", SearchOption.AllDirectories)
                    .Where(f => !backupPattern.IsMatch(f)) // Exclude backup files
                    .Select(f => new
                    {
                        path = f,
                        name = Path.GetFileName(f),
                        folder = Path.GetFileName(Path.GetDirectoryName(f)),
                        sizeKB = new FileInfo(f).Length / 1024
                    })
                    .OrderBy(f => f.folder)
                    .ThenBy(f => f.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    libraryPath = libraryPath,
                    totalFiles = files.Count,
                    files = files
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Batch process all detail files in library - call from external orchestrator
        /// Returns next file to process or completion status
        /// </summary>
        public static string GetNextFileToProcess(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string libraryPath = parameters["libraryPath"]?.ToString()
                    ?? @"D:\Revit Detail Libraries\Revit Details";
                string progressFile = parameters["progressFile"]?.ToString()
                    ?? @"D:\RevitMCPBridge2026\logs\batch_progress.json";

                // Get all files, excluding Revit backup files (*.0001.rvt, *.0002.rvt, etc.)
                var backupPattern = new System.Text.RegularExpressions.Regex(@"\.\d{4}\.rvt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var allFiles = Directory.GetFiles(libraryPath, "*.rvt", SearchOption.AllDirectories)
                    .Where(f => !backupPattern.IsMatch(f))
                    .OrderBy(f => f)
                    .ToList();

                // Load progress
                HashSet<string> processedFiles = new HashSet<string>();
                if (File.Exists(progressFile))
                {
                    try
                    {
                        var progress = JsonConvert.DeserializeObject<BatchProgress>(File.ReadAllText(progressFile));
                        if (progress?.ProcessedFiles != null)
                        {
                            processedFiles = new HashSet<string>(progress.ProcessedFiles);
                        }
                    }
                    catch { }
                }

                // Find next unprocessed file
                string nextFile = allFiles.FirstOrDefault(f => !processedFiles.Contains(f));

                if (nextFile == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        complete = true,
                        totalFiles = allFiles.Count,
                        processedFiles = processedFiles.Count,
                        message = "All files have been processed"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    complete = false,
                    nextFile = nextFile,
                    fileName = Path.GetFileName(nextFile),
                    totalFiles = allFiles.Count,
                    processedCount = processedFiles.Count,
                    remainingCount = allFiles.Count - processedFiles.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Mark a file as processed in the progress tracker
        /// </summary>
        public static string MarkFileProcessed(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string filePath = parameters["filePath"]?.ToString();
                string progressFile = parameters["progressFile"]?.ToString()
                    ?? @"D:\RevitMCPBridge2026\logs\batch_progress.json";
                bool success = parameters["success"]?.ToObject<bool>() ?? true;
                string errorMessage = parameters["errorMessage"]?.ToString();

                if (string.IsNullOrEmpty(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filePath is required"
                    });
                }

                // Load or create progress
                BatchProgress progress;
                if (File.Exists(progressFile))
                {
                    progress = JsonConvert.DeserializeObject<BatchProgress>(File.ReadAllText(progressFile))
                        ?? new BatchProgress();
                }
                else
                {
                    progress = new BatchProgress();
                }

                // Update progress
                if (progress.ProcessedFiles == null)
                    progress.ProcessedFiles = new List<string>();
                if (progress.FailedFiles == null)
                    progress.FailedFiles = new List<FailedFile>();

                if (success)
                {
                    if (!progress.ProcessedFiles.Contains(filePath))
                        progress.ProcessedFiles.Add(filePath);
                    progress.SuccessCount++;
                }
                else
                {
                    progress.FailedFiles.Add(new FailedFile
                    {
                        Path = filePath,
                        Error = errorMessage
                    });
                    progress.FailCount++;
                }

                progress.LastUpdated = DateTime.Now;

                // Save progress
                string dir = Path.GetDirectoryName(progressFile);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(progressFile, JsonConvert.SerializeObject(progress, Formatting.Indented));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    processedCount = progress.ProcessedFiles.Count,
                    failedCount = progress.FailedFiles.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Classes

        private class BatchProgress
        {
            public List<string> ProcessedFiles { get; set; } = new List<string>();
            public List<FailedFile> FailedFiles { get; set; } = new List<FailedFile>();
            public int SuccessCount { get; set; }
            public int FailCount { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        private class FailedFile
        {
            public string Path { get; set; }
            public string Error { get; set; }
        }

        #endregion

        #region Dimension Text Standardization

        /// <summary>
        /// Standardizes dimension text font and size across all dimension types
        /// </summary>
        public static string StandardizeDimensionText(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                string fontName = parameters["fontName"]?.ToString() ?? "Century Gothic";
                double sizeInches = parameters["sizeInches"]?.ToObject<double>() ?? 0.09375;
                double sizeFeet = sizeInches / 12.0;

                int typesModified = 0;
                var modifiedTypes = new List<object>();

                using (var trans = new Transaction(doc, "Standardize Dimension Text"))
                {
                    trans.Start();

                    // Get all dimension types
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(DimensionType));

                    foreach (DimensionType dimType in collector)
                    {
                        bool modified = false;
                        string originalFont = "";
                        double originalSize = 0;

                        // Get text size parameter
                        var textSizeParam = dimType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                        if (textSizeParam != null && !textSizeParam.IsReadOnly)
                        {
                            originalSize = textSizeParam.AsDouble() * 12; // Convert to inches
                            textSizeParam.Set(sizeFeet);
                            modified = true;
                        }

                        // Get text font parameter
                        var textFontParam = dimType.get_Parameter(BuiltInParameter.TEXT_FONT);
                        if (textFontParam != null && !textFontParam.IsReadOnly)
                        {
                            originalFont = textFontParam.AsString() ?? "";
                            textFontParam.Set(fontName);
                            modified = true;
                        }

                        if (modified)
                        {
                            typesModified++;
                            modifiedTypes.Add(new
                            {
                                id = dimType.Id.Value,
                                name = dimType.Name,
                                originalFont = originalFont,
                                originalSizeInches = Math.Round(originalSize, 4),
                                newFont = fontName,
                                newSizeInches = sizeInches
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typesModified = typesModified,
                    fontName = fontName,
                    sizeInches = sizeInches,
                    modifiedTypes = modifiedTypes,
                    message = $"Modified {typesModified} dimension types to {fontName} at {sizeInches}\" size"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Renames all dimension types with a prefix and ensures consistent font/size
        /// </summary>
        public static string RenameDimensionTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                string prefix = parameters["prefix"]?.ToString() ?? "FC";
                string fontName = parameters["fontName"]?.ToString() ?? "Century Gothic";
                double sizeInches = parameters["sizeInches"]?.ToObject<double>() ?? 0.09375;
                double sizeFeet = sizeInches / 12.0;
                bool skipAlreadyPrefixed = parameters["skipAlreadyPrefixed"]?.ToObject<bool>() ?? true;

                int typesRenamed = 0;
                var renamedTypes = new List<object>();

                using (var trans = new Transaction(doc, "Rename Dimension Types"))
                {
                    trans.Start();

                    // Get all dimension types
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(DimensionType));

                    foreach (DimensionType dimType in collector)
                    {
                        string originalName = dimType.Name;

                        // Skip if already has prefix
                        if (skipAlreadyPrefixed && originalName.StartsWith(prefix + " "))
                        {
                            continue;
                        }

                        // Determine the dimension style category from original name
                        string styleCategory = "Dimension";
                        if (originalName.Contains("Linear") || originalName.Contains("Horizontal"))
                            styleCategory = "Linear";
                        else if (originalName.Contains("Radial"))
                            styleCategory = "Radial";
                        else if (originalName.Contains("Angular"))
                            styleCategory = "Angular";
                        else if (originalName.Contains("Diameter"))
                            styleCategory = "Diameter";
                        else if (originalName.Contains("Arc"))
                            styleCategory = "Arc";
                        else if (originalName.Contains("Slope"))
                            styleCategory = "Slope";
                        else if (originalName.Contains("Elevation") || originalName.Contains("Target") || originalName.Contains("Crosshair"))
                            styleCategory = "Spot Elevation";
                        else if (originalName.Contains("Diagonal"))
                            styleCategory = "Diagonal";

                        // Create new name with prefix
                        string newName = $"{prefix} {styleCategory}";

                        // Add unique suffix if there are multiple of same category
                        int counter = 1;
                        string testName = newName;
                        var existingNames = new FilteredElementCollector(doc)
                            .OfClass(typeof(DimensionType))
                            .Cast<DimensionType>()
                            .Select(dt => dt.Name)
                            .ToHashSet();

                        while (existingNames.Contains(testName) && testName != originalName)
                        {
                            counter++;
                            testName = $"{newName} {counter}";
                        }
                        newName = testName;

                        // Skip if name would be unchanged
                        if (newName == originalName)
                        {
                            continue;
                        }

                        try
                        {
                            // Rename the type
                            dimType.Name = newName;

                            // Update font and size
                            var textFontParam = dimType.get_Parameter(BuiltInParameter.TEXT_FONT);
                            if (textFontParam != null && !textFontParam.IsReadOnly)
                            {
                                textFontParam.Set(fontName);
                            }

                            var textSizeParam = dimType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            if (textSizeParam != null && !textSizeParam.IsReadOnly)
                            {
                                textSizeParam.Set(sizeFeet);
                            }

                            typesRenamed++;
                            renamedTypes.Add(new
                            {
                                id = dimType.Id.Value,
                                originalName = originalName,
                                newName = newName,
                                font = fontName,
                                sizeInches = sizeInches
                            });
                        }
                        catch (Exception ex)
                        {
                            // Type might be in use or protected, log and continue
                            renamedTypes.Add(new
                            {
                                id = dimType.Id.Value,
                                originalName = originalName,
                                newName = "FAILED",
                                error = ex.Message
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typesRenamed = typesRenamed,
                    prefix = prefix,
                    fontName = fontName,
                    sizeInches = sizeInches,
                    renamedTypes = renamedTypes,
                    message = $"Renamed {typesRenamed} dimension types with '{prefix}' prefix"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Standardizes all text note types to a consistent font and size
        /// </summary>
        public static string StandardizeTextNoteTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                string fontName = parameters["fontName"]?.ToString() ?? "Century Gothic";
                double sizeInches = parameters["sizeInches"]?.ToObject<double>() ?? 0.09375;
                double sizeFeet = sizeInches / 12.0;
                bool skipCenturyGothic = parameters["skipCenturyGothic"]?.ToObject<bool>() ?? true;

                int typesModified = 0;
                var modifiedTypes = new List<object>();

                using (var trans = new Transaction(doc, "Standardize Text Note Types"))
                {
                    trans.Start();

                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNoteType));

                    foreach (TextNoteType textType in collector)
                    {
                        try
                        {
                            var fontParam = textType.get_Parameter(BuiltInParameter.TEXT_FONT);
                            var sizeParam = textType.get_Parameter(BuiltInParameter.TEXT_SIZE);

                            string originalFont = fontParam?.AsString() ?? "";
                            double originalSize = sizeParam != null ? sizeParam.AsDouble() * 12 : 0;

                            // Skip if already Century Gothic and flag is set
                            if (skipCenturyGothic && originalFont == "Century Gothic")
                            {
                                continue;
                            }

                            bool modified = false;

                            if (fontParam != null && !fontParam.IsReadOnly)
                            {
                                fontParam.Set(fontName);
                                modified = true;
                            }

                            if (sizeParam != null && !sizeParam.IsReadOnly)
                            {
                                sizeParam.Set(sizeFeet);
                                modified = true;
                            }

                            if (modified)
                            {
                                typesModified++;
                                modifiedTypes.Add(new
                                {
                                    id = textType.Id.Value,
                                    name = textType.Name,
                                    originalFont = originalFont,
                                    originalSizeInches = Math.Round(originalSize, 4),
                                    newFont = fontName,
                                    newSizeInches = sizeInches
                                });
                            }
                        }
                        catch (Exception)
                        {
                            // Skip types that can't be modified
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    typesModified = typesModified,
                    fontName = fontName,
                    sizeInches = sizeInches,
                    modifiedTypes = modifiedTypes,
                    message = $"Modified {typesModified} text note types to {fontName} at {sizeInches}\" size"
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
