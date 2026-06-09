using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

// Type aliases to resolve ambiguity between WPF and Revit types
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Binding = System.Windows.Data.Binding;
using Transform = Autodesk.Revit.DB.Transform;
using CheckBox = System.Windows.Controls.CheckBox;

namespace RevitMCPBridge
{
    #region RVT Preview Generation

    /// <summary>
    /// Generates preview images from RVT files by opening them and exporting drafting views
    /// This produces actual view content, not just file icons
    /// </summary>
    internal static class RvtPreviewGenerator
    {
        private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "RevitDetailPreviews");

        static RvtPreviewGenerator()
        {
            if (!Directory.Exists(CacheDir))
                Directory.CreateDirectory(CacheDir);
        }

        /// <summary>
        /// Get cached preview path for an RVT file
        /// </summary>
        public static string GetCachePath(string rvtFilePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(rvtFilePath);
            var hash = rvtFilePath.GetHashCode().ToString("X8");
            return Path.Combine(CacheDir, $"{fileName}_{hash}.png");
        }

        /// <summary>
        /// Check if a valid cached preview exists
        /// </summary>
        public static bool HasCachedPreview(string rvtFilePath)
        {
            var cachePath = GetCachePath(rvtFilePath);
            if (!File.Exists(cachePath))
                return false;

            // Check if cache is older than the RVT file
            var cacheTime = File.GetLastWriteTime(cachePath);
            var rvtTime = File.GetLastWriteTime(rvtFilePath);
            return cacheTime > rvtTime;
        }

        /// <summary>
        /// Load cached preview if available
        /// </summary>
        public static BitmapSource LoadCachedPreview(string rvtFilePath)
        {
            var cachePath = GetCachePath(rvtFilePath);
            if (!File.Exists(cachePath))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(cachePath);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Generate a preview image from an RVT file by exporting its drafting view
        /// Must be called from Revit API context with valid UIApplication
        /// </summary>
        public static string GeneratePreview(Autodesk.Revit.ApplicationServices.Application app, string rvtFilePath)
        {
            if (!File.Exists(rvtFilePath))
                return null;

            var cachePath = GetCachePath(rvtFilePath);
            Document detailDoc = null;

            try
            {
                // Open the detail RVT file
                detailDoc = app.OpenDocumentFile(rvtFilePath);
                if (detailDoc == null)
                    return null;

                View viewToExport = null;

                // 1. Try drafting view first (for Details)
                viewToExport = new FilteredElementCollector(detailDoc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate);

                // 2. Try Legend view (for Legends)
                if (viewToExport == null)
                {
                    viewToExport = new FilteredElementCollector(detailDoc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .FirstOrDefault(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
                }

                // 3. Try Schedule view (for Schedules) - check if it's on a sheet
                ViewSchedule scheduleView = null;
                if (viewToExport == null)
                {
                    scheduleView = new FilteredElementCollector(detailDoc)
                        .OfClass(typeof(ViewSchedule))
                        .Cast<ViewSchedule>()
                        .FirstOrDefault(v => !v.IsTemplate);
                }

                // 4. Fall back to any printable view
                if (viewToExport == null && scheduleView == null)
                {
                    viewToExport = new FilteredElementCollector(detailDoc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .FirstOrDefault(v => !v.IsTemplate && v.CanBePrinted);
                }

                // 5. For schedules, try to find a sheet containing the schedule
                if (viewToExport == null && scheduleView != null)
                {
                    // Try to find a sheet that contains this schedule
                    var sheets = new FilteredElementCollector(detailDoc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsTemplate)
                        .ToList();

                    if (sheets.Any())
                    {
                        // Use the first sheet as the view to export
                        viewToExport = sheets.First();
                    }
                    else
                    {
                        // No sheet found, create a placeholder for schedules
                        detailDoc.Close(false);
                        return CreateSchedulePlaceholder(cachePath, scheduleView.Name);
                    }
                }

                if (viewToExport == null)
                {
                    detailDoc.Close(false);
                    return null;
                }

                // Export the view as an image - HIGH RESOLUTION for zoom to fit
                var exportOptions = new ImageExportOptions
                {
                    FilePath = cachePath.Replace(".png", ""),  // Revit adds extension
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_300,  // Higher resolution for clarity
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = 1200,  // Larger for better zoom quality
                    ExportRange = ExportRange.SetOfViews
                };
                exportOptions.SetViewsAndSheets(new List<ElementId> { viewToExport.Id });

                detailDoc.ExportImage(exportOptions);
                detailDoc.Close(false);

                // Revit exports with different naming, find the actual file
                var exportedFile = FindExportedImage(cachePath.Replace(".png", ""));
                if (exportedFile != null && File.Exists(exportedFile))
                {
                    // Rename to our cache path if different
                    if (exportedFile != cachePath)
                    {
                        if (File.Exists(cachePath))
                            File.Delete(cachePath);
                        File.Move(exportedFile, cachePath);
                    }
                    return cachePath;
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview generation failed: {ex.Message}");
                if (detailDoc != null)
                {
                    try { detailDoc.Close(false); } catch { }
                }
                return null;
            }
        }

        private static string FindExportedImage(string basePath)
        {
            var dir = Path.GetDirectoryName(basePath);
            var baseName = Path.GetFileName(basePath);

            // Revit may append view name or index
            var files = Directory.GetFiles(dir, $"{baseName}*.png");
            if (files.Length > 0)
                return files[0];

            files = Directory.GetFiles(dir, $"{baseName}*.PNG");
            if (files.Length > 0)
                return files[0];

            return null;
        }

        /// <summary>
        /// Create a placeholder image for schedule views that cannot be exported graphically
        /// </summary>
        private static string CreateSchedulePlaceholder(string cachePath, string scheduleName)
        {
            try
            {
                // Create a simple placeholder image using System.Drawing
                using (var bitmap = new System.Drawing.Bitmap(400, 300))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    // Fill background
                    graphics.Clear(System.Drawing.Color.FromArgb(240, 240, 240));

                    // Draw border
                    using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(100, 100, 100), 2))
                    {
                        graphics.DrawRectangle(pen, 1, 1, 398, 298);
                    }

                    // Draw schedule icon (table representation)
                    using (var tablePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(80, 80, 80), 1))
                    {
                        // Draw table outline
                        graphics.DrawRectangle(tablePen, 100, 80, 200, 120);
                        // Draw horizontal lines
                        for (int y = 100; y <= 180; y += 20)
                        {
                            graphics.DrawLine(tablePen, 100, y, 300, y);
                        }
                        // Draw vertical lines
                        for (int x = 150; x <= 250; x += 50)
                        {
                            graphics.DrawLine(tablePen, x, 80, x, 200);
                        }
                    }

                    // Draw text
                    using (var font = new System.Drawing.Font("Segoe UI", 12, System.Drawing.FontStyle.Bold))
                    using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(60, 60, 60)))
                    {
                        var format = new System.Drawing.StringFormat
                        {
                            Alignment = System.Drawing.StringAlignment.Center,
                            LineAlignment = System.Drawing.StringAlignment.Center
                        };

                        graphics.DrawString("📊 SCHEDULE", font, brush, new System.Drawing.RectangleF(0, 220, 400, 30), format);

                        using (var smallFont = new System.Drawing.Font("Segoe UI", 9))
                        {
                            // Truncate name if too long
                            var displayName = scheduleName.Length > 35 ? scheduleName.Substring(0, 32) + "..." : scheduleName;
                            graphics.DrawString(displayName, smallFont, brush, new System.Drawing.RectangleF(0, 250, 400, 25), format);
                        }
                    }

                    bitmap.Save(cachePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return cachePath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Clear the preview cache
        /// </summary>
        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDir))
                {
                    foreach (var file in Directory.GetFiles(CacheDir, "*.png"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    #endregion

    #region External Event Handler for Import

    /// <summary>
    /// External event handler for importing details - runs in valid Revit API context
    /// </summary>
    /// <summary>
    /// General-purpose ExternalEvent handler: runs queued actions in a valid
    /// Revit API context. Queued (not slot-set) because Raise coalesces while
    /// an event is pending.
    /// </summary>
    public class RevitUiActionHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Action<UIApplication>> _pending = new Queue<Action<UIApplication>>();

        public void Enqueue(Action<UIApplication> action)
        {
            lock (_sync) { _pending.Enqueue(action); }
        }

        public void Execute(UIApplication app)
        {
            List<Action<UIApplication>> batch;
            lock (_sync)
            {
                batch = new List<Action<UIApplication>>(_pending);
                _pending.Clear();
            }

            foreach (var action in batch)
            {
                try { action(app); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Revit action failed: {ex.Message}");
                }
            }
        }

        public string GetName()
        {
            return "Detail Library Revit Action";
        }
    }

    public class DetailImportHandler : IExternalEventHandler
    {
        public List<string> FilesToImport { get; set; } = new List<string>();
        public Action<int, int, List<string>> OnComplete { get; set; }

        public void Execute(UIApplication app)
        {
            if (FilesToImport == null || FilesToImport.Count == 0)
                return;

            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                OnComplete?.Invoke(0, FilesToImport.Count, new List<string> { "No active document" });
                return;
            }

            var errors = new List<string>();
            int imported = 0;

            foreach (var rvtPath in FilesToImport)
            {
                string error;
                var success = ImportSingleDetail(app, doc, rvtPath, out error);
                if (success)
                    imported++;
                else
                    errors.Add($"{Path.GetFileNameWithoutExtension(rvtPath)}: {error}");
            }

            OnComplete?.Invoke(imported, FilesToImport.Count, errors);
            FilesToImport.Clear();
        }

        private bool ImportSingleDetail(UIApplication uiApp, Document targetDoc, string rvtPath, out string errorMessage)
        {
            errorMessage = "";
            Document detailDoc = null;

            try
            {
                // Open the detail RVT
                detailDoc = uiApp.Application.OpenDocumentFile(rvtPath);
                if (detailDoc == null)
                {
                    errorMessage = "Could not open file";
                    return false;
                }

                // Find drafting views
                var draftingViews = new FilteredElementCollector(detailDoc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                if (draftingViews.Count == 0)
                {
                    errorMessage = "No drafting views found";
                    detailDoc.Close(false);
                    return false;
                }

                // Find the drafting view with the MOST elements (skip empty views)
                ViewDrafting sourceView = null;
                ICollection<ElementId> elementsInView = null;
                int maxElements = 0;

                foreach (var view in draftingViews)
                {
                    var elements = new FilteredElementCollector(detailDoc, view.Id)
                        .WhereElementIsNotElementType()
                        .Where(e => e.Category != null &&
                               e.Category.Id.Value != (int)BuiltInCategory.OST_Views)
                        .Select(e => e.Id)
                        .ToList();

                    if (elements.Count > maxElements)
                    {
                        maxElements = elements.Count;
                        sourceView = view;
                        elementsInView = elements;
                    }
                }

                if (sourceView == null || elementsInView == null || elementsInView.Count == 0)
                {
                    errorMessage = "No drafting views with content found";
                    detailDoc.Close(false);
                    return false;
                }

                var viewName = sourceView.Name;

                // Create new drafting view in target document
                using (var trans = new Transaction(targetDoc, "Import Detail"))
                {
                    trans.Start();

                    // Get drafting view family type
                    var viewFamilyTypes = new FilteredElementCollector(targetDoc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .Where(vft => vft.ViewFamily == ViewFamily.Drafting)
                        .ToList();

                    if (viewFamilyTypes.Count == 0)
                    {
                        trans.RollBack();
                        errorMessage = "No drafting view type in target";
                        detailDoc.Close(false);
                        return false;
                    }

                    // Create new drafting view
                    var newView = ViewDrafting.Create(targetDoc, viewFamilyTypes[0].Id);

                    // Generate unique name
                    var baseName = viewName;
                    var counter = 1;
                    var finalName = viewName;
                    while (ViewNameExists(targetDoc, finalName))
                    {
                        finalName = $"{baseName} ({counter++})";
                    }
                    newView.Name = finalName;

                    // Copy elements
                    try
                    {
                        var copyOptions = new CopyPasteOptions();
                        copyOptions.SetDuplicateTypeNamesHandler(new ImportDuplicateHandler());

                        var copiedIds = ElementTransformUtils.CopyElements(
                            sourceView,
                            elementsInView,
                            newView,
                            Autodesk.Revit.DB.Transform.Identity,
                            copyOptions);

                        if (copiedIds == null || copiedIds.Count == 0)
                        {
                            trans.RollBack();
                            errorMessage = "No elements copied";
                            detailDoc.Close(false);
                            return false;
                        }
                    }
                    catch (Exception copyEx)
                    {
                        trans.RollBack();
                        errorMessage = $"Copy failed: {copyEx.Message}";
                        detailDoc.Close(false);
                        return false;
                    }

                    trans.CommitAndCheck();
                }

                detailDoc.Close(false);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                if (detailDoc != null)
                {
                    try { detailDoc.Close(false); } catch { }
                }
                return false;
            }
        }

        private static bool ViewNameExists(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => v.Name == name);
        }

        public string GetName() => "Detail Import Handler";
    }

    public class ImportDuplicateHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }

    /// <summary>
    /// External event handler for extracting schedule data - runs in valid Revit API context
    /// </summary>
    public class SchedulePreviewHandler : IExternalEventHandler
    {
        public string RvtFilePath { get; set; }
        public string CsvOutputPath { get; set; }
        public string ScheduleName { get; set; }
        public Action<bool, string, string> OnComplete { get; set; }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(RvtFilePath) || !File.Exists(RvtFilePath))
            {
                OnComplete?.Invoke(false, null, "File not found");
                return;
            }

            Document scheduleDoc = null;
            try
            {
                // Open the schedule RVT file
                scheduleDoc = app.Application.OpenDocumentFile(RvtFilePath);
                if (scheduleDoc == null)
                {
                    OnComplete?.Invoke(false, null, "Could not open file");
                    return;
                }

                // Find the first ViewSchedule
                var viewSchedule = new FilteredElementCollector(scheduleDoc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule);

                if (viewSchedule == null)
                {
                    scheduleDoc.Close(false);
                    OnComplete?.Invoke(false, null, "No schedule found in file");
                    return;
                }

                ScheduleName = viewSchedule.Name;

                // Export schedule to CSV using Revit's built-in export
                var options = new ViewScheduleExportOptions
                {
                    FieldDelimiter = ",",
                    TextQualifier = ExportTextQualifier.DoubleQuote
                };

                // Create temp path if needed
                var csvDir = Path.GetDirectoryName(CsvOutputPath);
                if (!Directory.Exists(csvDir))
                    Directory.CreateDirectory(csvDir);

                var csvFileName = Path.GetFileName(CsvOutputPath);
                viewSchedule.Export(csvDir, csvFileName, options);

                scheduleDoc.Close(false);
                OnComplete?.Invoke(true, CsvOutputPath, ScheduleName);
            }
            catch (Exception ex)
            {
                if (scheduleDoc != null)
                {
                    try { scheduleDoc.Close(false); } catch { }
                }
                OnComplete?.Invoke(false, null, ex.Message);
            }
        }

        public string GetName() => "Schedule Preview Handler";
    }

    #endregion

    /// <summary>
    /// Content types available in the library
    /// </summary>
    public enum LibraryContentType
    {
        Details,    // RVT files with drafting views
        Families,   // RFA family files
        Legends,    // RVT files with legend views
        Schedules   // RVT files with schedule views
    }

    #region Library Settings & Profile Management

    /// <summary>
    /// Library profile for a specific firm/user
    /// </summary>
    public class LibraryProfile
    {
        public string Name { get; set; } = "Default";
        public string Description { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime LastUsed { get; set; } = DateTime.Now;
        public Dictionary<string, string> Paths { get; set; } = new Dictionary<string, string>
        {
            { "Details", "" },
            { "Families", "" },
            { "Legends", "" },
            { "Schedules", "" }
        };
        public List<string> Favorites { get; set; } = new List<string>();
        public List<string> RecentItems { get; set; } = new List<string>();
    }

    /// <summary>
    /// Global library settings with multi-profile support
    /// </summary>
    public class LibrarySettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitMCPBridge", "LibrarySettings.json");

        public string ActiveProfileName { get; set; } = "Default";
        public List<LibraryProfile> Profiles { get; set; } = new List<LibraryProfile>();
        public bool ShowFavoritesFirst { get; set; } = true;
        public int MaxRecentItems { get; set; } = 20;
        public string DeletedItemsPath { get; set; } = "";  // Recycle bin path
        public string SortBy { get; set; } = "Name";  // Name, Date, Size
        public bool SortAscending { get; set; } = true;

        public LibraryProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Name == ActiveProfileName)
            ?? Profiles.FirstOrDefault() ?? CreateDefaultProfile();

        private LibraryProfile CreateDefaultProfile()
        {
            var profile = new LibraryProfile
            {
                Name = "Default",
                Description = "Default library profile",
                Paths = new Dictionary<string, string>
                {
                    { "Details", @"D:\Revit Detail Libraries\Revit Details\" },
                    { "Families", @"D:\Revit Detail Libraries\Master Library\Families\" },
                    { "Legends", @"D:\Revit Detail Libraries\Master Library\Legends\" },
                    { "Schedules", @"D:\Revit Detail Libraries\Master Library\Schedules\" }
                }
            };
            Profiles.Add(profile);
            return profile;
        }

        public static LibrarySettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonConvert.DeserializeObject<LibrarySettings>(json);
                    if (settings != null && settings.Profiles.Count > 0)
                        return settings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            // Return default settings
            var defaultSettings = new LibrarySettings();
            defaultSettings.CreateDefaultProfile();
            defaultSettings.DeletedItemsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitMCPBridge", "DeletedItems");
            return defaultSettings;
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public string GetPath(LibraryContentType contentType)
        {
            var key = contentType.ToString();
            if (ActiveProfile.Paths.TryGetValue(key, out var path))
                return path;
            return "";
        }

        public void SetPath(LibraryContentType contentType, string path)
        {
            var key = contentType.ToString();
            ActiveProfile.Paths[key] = path;
            Save();
        }

        public void AddToFavorites(string filePath)
        {
            if (!ActiveProfile.Favorites.Contains(filePath))
            {
                ActiveProfile.Favorites.Add(filePath);
                Save();
            }
        }

        public void RemoveFromFavorites(string filePath)
        {
            if (ActiveProfile.Favorites.Remove(filePath))
                Save();
        }

        public bool IsFavorite(string filePath) => ActiveProfile.Favorites.Contains(filePath);

        public void AddToRecent(string filePath)
        {
            ActiveProfile.RecentItems.Remove(filePath);  // Remove if exists
            ActiveProfile.RecentItems.Insert(0, filePath);  // Add to front
            while (ActiveProfile.RecentItems.Count > MaxRecentItems)
                ActiveProfile.RecentItems.RemoveAt(ActiveProfile.RecentItems.Count - 1);
            ActiveProfile.LastUsed = DateTime.Now;
            Save();
        }

        public LibraryProfile CreateNewProfile(string name, string description = "")
        {
            var profile = new LibraryProfile
            {
                Name = name,
                Description = description,
                Paths = new Dictionary<string, string>
                {
                    { "Details", "" },
                    { "Families", "" },
                    { "Legends", "" },
                    { "Schedules", "" }
                }
            };
            Profiles.Add(profile);
            Save();
            return profile;
        }

        public bool DeleteProfile(string name)
        {
            if (name == "Default" || Profiles.Count <= 1)
                return false;

            var profile = Profiles.FirstOrDefault(p => p.Name == name);
            if (profile != null)
            {
                Profiles.Remove(profile);
                if (ActiveProfileName == name)
                    ActiveProfileName = Profiles.First().Name;
                Save();
                return true;
            }
            return false;
        }

        public void SwitchProfile(string name)
        {
            if (Profiles.Any(p => p.Name == name))
            {
                ActiveProfileName = name;
                ActiveProfile.LastUsed = DateTime.Now;
                Save();
            }
        }
    }

    #endregion

    #region Batch Rename Dialog

    /// <summary>
    /// Dialog for batch renaming files with patterns
    /// </summary>
    public class BatchRenameDialog : Window
    {
        public enum RenameMode { AddPrefix, AddSuffix, SearchReplace, RemovePrefix, RemoveSuffix }

        private ComboBox _modeSelector;
        private TextBox _pattern1;
        private TextBox _pattern2;
        private TextBlock _pattern1Label;
        private TextBlock _pattern2Label;
        private ListView _previewList;
        private List<(string OldName, string NewName, string FullPath)> _renames = new List<(string, string, string)>();

        public bool DialogResult { get; private set; } = false;
        public List<(string OldPath, string NewPath)> RenameOperations { get; private set; } = new List<(string, string)>();

        private List<DetailFile> _files;

        public BatchRenameDialog(List<DetailFile> files)
        {
            _files = files;
            Title = $"Batch Rename - {files.Count} files selected";
            Width = 700;
            Height = 550;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(SketchPadColors.BgPrimary);
            ResizeMode = ResizeMode.NoResize;

            BuildUI();
            UpdatePreview();
        }

        private void BuildUI()
        {
            var mainStack = new StackPanel { Margin = new Thickness(20) };

            // Mode selector
            var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            modePanel.Children.Add(new TextBlock
            {
                Text = "Rename Mode:",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            _modeSelector = new ComboBox
            {
                Width = 200,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
                Foreground = Brushes.Black
            };
            _modeSelector.Items.Add(new ComboBoxItem { Content = "Add Prefix", Tag = RenameMode.AddPrefix, Foreground = Brushes.Black });
            _modeSelector.Items.Add(new ComboBoxItem { Content = "Add Suffix", Tag = RenameMode.AddSuffix, Foreground = Brushes.Black });
            _modeSelector.Items.Add(new ComboBoxItem { Content = "Search & Replace", Tag = RenameMode.SearchReplace, Foreground = Brushes.Black });
            _modeSelector.Items.Add(new ComboBoxItem { Content = "Remove Prefix", Tag = RenameMode.RemovePrefix, Foreground = Brushes.Black });
            _modeSelector.Items.Add(new ComboBoxItem { Content = "Remove Suffix", Tag = RenameMode.RemoveSuffix, Foreground = Brushes.Black });
            _modeSelector.SelectedIndex = 0;
            _modeSelector.SelectionChanged += (s, e) => { UpdateLabels(); UpdatePreview(); };
            modePanel.Children.Add(_modeSelector);
            mainStack.Children.Add(modePanel);

            // Pattern inputs
            var patternGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            patternGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            patternGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            patternGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            patternGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _pattern1Label = new TextBlock
            {
                Text = "Prefix:",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 10)
            };
            Grid.SetRow(_pattern1Label, 0);
            Grid.SetColumn(_pattern1Label, 0);
            patternGrid.Children.Add(_pattern1Label);

            _pattern1 = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 10)
            };
            _pattern1.TextChanged += (s, e) => UpdatePreview();
            Grid.SetRow(_pattern1, 0);
            Grid.SetColumn(_pattern1, 1);
            patternGrid.Children.Add(_pattern1);

            _pattern2Label = new TextBlock
            {
                Text = "Replace with:",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Visibility = System.Windows.Visibility.Collapsed
            };
            Grid.SetRow(_pattern2Label, 1);
            Grid.SetColumn(_pattern2Label, 0);
            patternGrid.Children.Add(_pattern2Label);

            _pattern2 = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = System.Windows.Visibility.Collapsed
            };
            _pattern2.TextChanged += (s, e) => UpdatePreview();
            Grid.SetRow(_pattern2, 1);
            Grid.SetColumn(_pattern2, 1);
            patternGrid.Children.Add(_pattern2);

            mainStack.Children.Add(patternGrid);

            // Preview header
            mainStack.Children.Add(new TextBlock
            {
                Text = "Preview:",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            });

            // Preview list
            var previewBorder = new Border
            {
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                BorderThickness = new Thickness(1),
                Height = 280
            };

            _previewList = new ListView
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };

            // Set up columns using GridView
            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Original Name",
                DisplayMemberBinding = new Binding("OldName"),
                Width = 280
            });
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "→",
                Width = 30
            });
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "New Name",
                DisplayMemberBinding = new Binding("NewName"),
                Width = 280
            });
            _previewList.View = gridView;

            previewBorder.Child = _previewList;
            mainStack.Children.Add(previewBorder);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(20, 8, 20, 8),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 70)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelBtn);

            var renameBtn = new Button
            {
                Content = "Rename Files",
                Padding = new Thickness(20, 8, 20, 8),
                Background = new SolidColorBrush(SketchPadColors.AccentGreen),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold
            };
            renameBtn.Click += (s, e) => ExecuteRename();
            buttonPanel.Children.Add(renameBtn);

            mainStack.Children.Add(buttonPanel);

            Content = mainStack;
        }

        private void UpdateLabels()
        {
            var mode = GetSelectedMode();
            switch (mode)
            {
                case RenameMode.AddPrefix:
                    _pattern1Label.Text = "Prefix to add:";
                    _pattern2Label.Visibility = System.Windows.Visibility.Collapsed;
                    _pattern2.Visibility = System.Windows.Visibility.Collapsed;
                    break;
                case RenameMode.AddSuffix:
                    _pattern1Label.Text = "Suffix to add:";
                    _pattern2Label.Visibility = System.Windows.Visibility.Collapsed;
                    _pattern2.Visibility = System.Windows.Visibility.Collapsed;
                    break;
                case RenameMode.SearchReplace:
                    _pattern1Label.Text = "Search for:";
                    _pattern2Label.Text = "Replace with:";
                    _pattern2Label.Visibility = System.Windows.Visibility.Visible;
                    _pattern2.Visibility = System.Windows.Visibility.Visible;
                    break;
                case RenameMode.RemovePrefix:
                    _pattern1Label.Text = "Prefix to remove:";
                    _pattern2Label.Visibility = System.Windows.Visibility.Collapsed;
                    _pattern2.Visibility = System.Windows.Visibility.Collapsed;
                    break;
                case RenameMode.RemoveSuffix:
                    _pattern1Label.Text = "Suffix to remove:";
                    _pattern2Label.Visibility = System.Windows.Visibility.Collapsed;
                    _pattern2.Visibility = System.Windows.Visibility.Collapsed;
                    break;
            }
        }

        private RenameMode GetSelectedMode()
        {
            if (_modeSelector.SelectedItem is ComboBoxItem item && item.Tag is RenameMode mode)
                return mode;
            return RenameMode.AddPrefix;
        }

        private void UpdatePreview()
        {
            _renames.Clear();
            var mode = GetSelectedMode();
            var pattern1 = _pattern1.Text;
            var pattern2 = _pattern2.Text;

            foreach (var file in _files)
            {
                var oldName = file.Name;
                string newName = oldName;

                switch (mode)
                {
                    case RenameMode.AddPrefix:
                        if (!string.IsNullOrEmpty(pattern1))
                            newName = pattern1 + oldName;
                        break;
                    case RenameMode.AddSuffix:
                        if (!string.IsNullOrEmpty(pattern1))
                            newName = oldName + pattern1;
                        break;
                    case RenameMode.SearchReplace:
                        if (!string.IsNullOrEmpty(pattern1))
                            newName = oldName.Replace(pattern1, pattern2 ?? "");
                        break;
                    case RenameMode.RemovePrefix:
                        if (!string.IsNullOrEmpty(pattern1) && oldName.StartsWith(pattern1))
                            newName = oldName.Substring(pattern1.Length);
                        break;
                    case RenameMode.RemoveSuffix:
                        if (!string.IsNullOrEmpty(pattern1) && oldName.EndsWith(pattern1))
                            newName = oldName.Substring(0, oldName.Length - pattern1.Length);
                        break;
                }

                _renames.Add((oldName, newName, file.FullPath));
            }

            _previewList.ItemsSource = _renames.Select(r => new { r.OldName, r.NewName }).ToList();
        }

        private void ExecuteRename()
        {
            // Build rename operations
            RenameOperations.Clear();
            foreach (var rename in _renames)
            {
                if (rename.OldName != rename.NewName)
                {
                    var dir = Path.GetDirectoryName(rename.FullPath);
                    var ext = Path.GetExtension(rename.FullPath);
                    var newPath = Path.Combine(dir, rename.NewName + ext);
                    RenameOperations.Add((rename.FullPath, newPath));
                }
            }

            if (RenameOperations.Count == 0)
            {
                MessageBox.Show("No files will be renamed. Check your pattern.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }
    }

    #endregion

    #region Library Statistics Dialog

    /// <summary>
    /// Dialog showing library usage statistics
    /// </summary>
    public class LibraryStatisticsDialog : Window
    {
        public LibraryStatisticsDialog(LibrarySettings settings, Action<string, int, int> updateProgress)
        {
            Title = "Library Statistics";
            Width = 600;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(SketchPadColors.BgPrimary);
            ResizeMode = ResizeMode.NoResize;

            var mainStack = new StackPanel { Margin = new Thickness(20) };

            // Header
            mainStack.Children.Add(new TextBlock
            {
                Text = "📊 Library Statistics",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Calculate stats
            var stats = CalculateStats(settings);

            // Stats grid
            var statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int row = 0;

            // Profile info
            AddStatRow(statsGrid, row++, "Active Profile:", settings.ActiveProfile?.Name ?? "None", "");
            AddStatRow(statsGrid, row++, "", "", ""); // spacer

            // Overall stats
            AddStatRow(statsGrid, row++, "Total Files:", stats.TotalFiles.ToString("N0"), "");
            AddStatRow(statsGrid, row++, "Total Size:", FormatFileSize(stats.TotalSize), "");
            AddStatRow(statsGrid, row++, "Total Categories:", stats.TotalCategories.ToString(), "");
            AddStatRow(statsGrid, row++, "", "", ""); // spacer

            // By type
            AddStatRow(statsGrid, row++, "📐 Details:", $"{stats.DetailsCount:N0} files", FormatFileSize(stats.DetailsSize));
            AddStatRow(statsGrid, row++, "📦 Families:", $"{stats.FamiliesCount:N0} files", FormatFileSize(stats.FamiliesSize));
            AddStatRow(statsGrid, row++, "📋 Legends:", $"{stats.LegendsCount:N0} files", FormatFileSize(stats.LegendsSize));
            AddStatRow(statsGrid, row++, "📊 Schedules:", $"{stats.SchedulesCount:N0} files", FormatFileSize(stats.SchedulesSize));
            AddStatRow(statsGrid, row++, "", "", ""); // spacer

            // Favorites and recent
            AddStatRow(statsGrid, row++, "⭐ Favorites:", stats.FavoritesCount.ToString(), "");
            AddStatRow(statsGrid, row++, "🕐 Recent Items:", stats.RecentCount.ToString(), "");

            mainStack.Children.Add(statsGrid);

            // Top categories section
            if (stats.TopCategories.Count > 0)
            {
                mainStack.Children.Add(new TextBlock
                {
                    Text = "Top Categories by File Count:",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 10, 0, 10)
                });

                foreach (var cat in stats.TopCategories.Take(5))
                {
                    mainStack.Children.Add(new TextBlock
                    {
                        Text = $"  • {cat.Name}: {cat.Count} files ({FormatFileSize(cat.Size)})",
                        Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                        Margin = new Thickness(10, 2, 0, 2)
                    });
                }
            }

            // Close button
            var closeBtn = new Button
            {
                Content = "Close",
                Padding = new Thickness(30, 10, 30, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
                Background = new SolidColorBrush(SketchPadColors.AccentBlue),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            };
            closeBtn.Click += (s, e) => Close();
            mainStack.Children.Add(closeBtn);

            Content = mainStack;
        }

        private void AddStatRow(Grid grid, int row, string label, string value, string extra)
        {
            if (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontWeight = string.IsNullOrEmpty(label) ? FontWeights.Normal : FontWeights.SemiBold,
                Margin = new Thickness(0, 3, 15, 3)
            };
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            var valueBlock = new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                Margin = new Thickness(0, 3, 15, 3)
            };
            Grid.SetRow(valueBlock, row);
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(valueBlock);

            if (!string.IsNullOrEmpty(extra))
            {
                var extraBlock = new TextBlock
                {
                    Text = extra,
                    Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                Grid.SetRow(extraBlock, row);
                Grid.SetColumn(extraBlock, 2);
                grid.Children.Add(extraBlock);
            }
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes >= 1073741824)
                return $"{bytes / 1073741824.0:F2} GB";
            if (bytes >= 1048576)
                return $"{bytes / 1048576.0:F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} bytes";
        }

        private LibraryStats CalculateStats(LibrarySettings settings)
        {
            var stats = new LibraryStats();
            var profile = settings.ActiveProfile;
            if (profile == null) return stats;

            stats.FavoritesCount = profile.Favorites?.Count ?? 0;
            stats.RecentCount = profile.RecentItems?.Count ?? 0;

            var categoryStats = new Dictionary<string, (int Count, long Size)>();

            foreach (var contentType in new[] {
                (LibraryContentType.Details, profile.Paths.GetValueOrDefault("Details", ""), "*.rvt"),
                (LibraryContentType.Families, profile.Paths.GetValueOrDefault("Families", ""), "*.rfa"),
                (LibraryContentType.Legends, profile.Paths.GetValueOrDefault("Legends", ""), "*.rvt"),
                (LibraryContentType.Schedules, profile.Paths.GetValueOrDefault("Schedules", ""), "*.rvt")
            })
            {
                var path = contentType.Item2;
                var pattern = contentType.Item3;
                var type = contentType.Item1;

                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    try
                    {
                        var files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories)
                            .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f, @"\.\d{4}\.(rfa|rvt)$"))
                            .ToList();

                        int count = files.Count;
                        long size = files.Sum(f => new FileInfo(f).Length);

                        switch (type)
                        {
                            case LibraryContentType.Details:
                                stats.DetailsCount = count;
                                stats.DetailsSize = size;
                                break;
                            case LibraryContentType.Families:
                                stats.FamiliesCount = count;
                                stats.FamiliesSize = size;
                                break;
                            case LibraryContentType.Legends:
                                stats.LegendsCount = count;
                                stats.LegendsSize = size;
                                break;
                            case LibraryContentType.Schedules:
                                stats.SchedulesCount = count;
                                stats.SchedulesSize = size;
                                break;
                        }

                        // Track categories
                        foreach (var dir in Directory.GetDirectories(path))
                        {
                            var catName = Path.GetFileName(dir);
                            var catFiles = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)
                                .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f, @"\.\d{4}\.(rfa|rvt)$"))
                                .ToList();
                            if (catFiles.Count > 0)
                            {
                                var catSize = catFiles.Sum(f => new FileInfo(f).Length);
                                if (categoryStats.ContainsKey(catName))
                                {
                                    var existing = categoryStats[catName];
                                    categoryStats[catName] = (existing.Count + catFiles.Count, existing.Size + catSize);
                                }
                                else
                                {
                                    categoryStats[catName] = (catFiles.Count, catSize);
                                    stats.TotalCategories++;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            stats.TotalFiles = stats.DetailsCount + stats.FamiliesCount + stats.LegendsCount + stats.SchedulesCount;
            stats.TotalSize = stats.DetailsSize + stats.FamiliesSize + stats.LegendsSize + stats.SchedulesSize;

            stats.TopCategories = categoryStats
                .OrderByDescending(kv => kv.Value.Count)
                .Take(10)
                .Select(kv => new CategoryStat { Name = kv.Key, Count = kv.Value.Count, Size = kv.Value.Size })
                .ToList();

            return stats;
        }

        private class LibraryStats
        {
            public int TotalFiles { get; set; }
            public long TotalSize { get; set; }
            public int TotalCategories { get; set; }
            public int DetailsCount { get; set; }
            public long DetailsSize { get; set; }
            public int FamiliesCount { get; set; }
            public long FamiliesSize { get; set; }
            public int LegendsCount { get; set; }
            public long LegendsSize { get; set; }
            public int SchedulesCount { get; set; }
            public long SchedulesSize { get; set; }
            public int FavoritesCount { get; set; }
            public int RecentCount { get; set; }
            public List<CategoryStat> TopCategories { get; set; } = new List<CategoryStat>();
        }

        private class CategoryStat
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public long Size { get; set; }
        }
    }

    #endregion

    #region Settings Window

    /// <summary>
    /// Settings window for library configuration
    /// </summary>
    public class LibrarySettingsWindow : Window
    {
        private LibrarySettings _settings;
        private ComboBox _profileSelector;
        private TextBox _detailsPath;
        private TextBox _familiesPath;
        private TextBox _legendsPath;
        private TextBox _schedulesPath;
        private TextBox _profileName;
        private TextBox _profileDescription;
        private ComboBox _sortBySelector;
        private CheckBox _sortAscending;
        private CheckBox _showFavoritesFirst;
        private Action _onSettingsChanged;

        public LibrarySettingsWindow(LibrarySettings settings, Action onSettingsChanged)
        {
            _settings = settings;
            _onSettingsChanged = onSettingsChanged;

            Title = "Content Library Settings";
            Width = 700;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(SketchPadColors.BgPrimary);
            ResizeMode = ResizeMode.NoResize;

            BuildUI();
            LoadCurrentProfile();
        }

        private void BuildUI()
        {
            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Profile selector
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Profile info
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Paths header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Paths
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Options
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

            // === Profile Selector Row ===
            var profilePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            profilePanel.Children.Add(new TextBlock
            {
                Text = "Library Profile:",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 100
            });

            _profileSelector = new ComboBox
            {
                Width = 200,
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Foreground = Brushes.Black
            };
            foreach (var profile in _settings.Profiles)
                _profileSelector.Items.Add(profile.Name);
            _profileSelector.SelectedItem = _settings.ActiveProfileName;
            _profileSelector.SelectionChanged += ProfileSelector_Changed;
            profilePanel.Children.Add(_profileSelector);

            var newProfileBtn = new Button
            {
                Content = "+ New Profile",
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(10, 5, 10, 5),
                Background = new SolidColorBrush(SketchPadColors.AccentBlue),
                Foreground = Brushes.White
            };
            newProfileBtn.Click += NewProfile_Click;
            profilePanel.Children.Add(newProfileBtn);

            var deleteProfileBtn = new Button
            {
                Content = "Delete Profile",
                Margin = new Thickness(5, 0, 0, 0),
                Padding = new Thickness(10, 5, 10, 5),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 60, 60)),
                Foreground = Brushes.White
            };
            deleteProfileBtn.Click += DeleteProfile_Click;
            profilePanel.Children.Add(deleteProfileBtn);

            Grid.SetRow(profilePanel, 0);
            mainGrid.Children.Add(profilePanel);

            // === Profile Info Row ===
            var infoGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.RowDefinitions.Add(new RowDefinition());
            infoGrid.RowDefinitions.Add(new RowDefinition());

            infoGrid.Children.Add(CreateLabel("Profile Name:", 0, 0));
            _profileName = CreateTextBox(0, 1);
            infoGrid.Children.Add(_profileName);

            infoGrid.Children.Add(CreateLabel("Description:", 0, 2));
            _profileDescription = CreateTextBox(0, 3);
            infoGrid.Children.Add(_profileDescription);

            Grid.SetRow(infoGrid, 1);
            mainGrid.Children.Add(infoGrid);

            // === Paths Header ===
            var pathsHeader = new TextBlock
            {
                Text = "Library Paths (folders containing your content)",
                Foreground = new SolidColorBrush(SketchPadColors.AccentBlue),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 10)
            };
            Grid.SetRow(pathsHeader, 2);
            mainGrid.Children.Add(pathsHeader);

            // === Paths Grid ===
            var pathsGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            pathsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            pathsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            for (int i = 0; i < 4; i++)
                pathsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });

            // Details
            pathsGrid.Children.Add(CreateLabel("Details:", 0, 0));
            _detailsPath = CreateTextBox(0, 1);
            Grid.SetRow(_detailsPath, 0);
            pathsGrid.Children.Add(_detailsPath);
            pathsGrid.Children.Add(CreateBrowseButton(0, () => BrowseFolder(_detailsPath)));

            // Families
            pathsGrid.Children.Add(CreateLabel("Families:", 1, 0));
            _familiesPath = CreateTextBox(1, 1);
            Grid.SetRow(_familiesPath, 1);
            pathsGrid.Children.Add(_familiesPath);
            pathsGrid.Children.Add(CreateBrowseButton(1, () => BrowseFolder(_familiesPath)));

            // Legends
            pathsGrid.Children.Add(CreateLabel("Legends:", 2, 0));
            _legendsPath = CreateTextBox(2, 1);
            Grid.SetRow(_legendsPath, 2);
            pathsGrid.Children.Add(_legendsPath);
            pathsGrid.Children.Add(CreateBrowseButton(2, () => BrowseFolder(_legendsPath)));

            // Schedules
            pathsGrid.Children.Add(CreateLabel("Schedules:", 3, 0));
            _schedulesPath = CreateTextBox(3, 1);
            Grid.SetRow(_schedulesPath, 3);
            pathsGrid.Children.Add(_schedulesPath);
            pathsGrid.Children.Add(CreateBrowseButton(3, () => BrowseFolder(_schedulesPath)));

            Grid.SetRow(pathsGrid, 3);
            mainGrid.Children.Add(pathsGrid);

            // === Options ===
            var optionsPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            var sortPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            sortPanel.Children.Add(new TextBlock { Text = "Sort By:", Foreground = Brushes.White, Width = 100, VerticalAlignment = VerticalAlignment.Center });
            _sortBySelector = new ComboBox { Width = 120, Background = new SolidColorBrush(SketchPadColors.BgSecondary) };
            _sortBySelector.Items.Add("Name");
            _sortBySelector.Items.Add("Date Modified");
            _sortBySelector.Items.Add("Size");
            _sortBySelector.SelectedItem = _settings.SortBy;
            sortPanel.Children.Add(_sortBySelector);

            _sortAscending = new CheckBox
            {
                Content = "Ascending",
                Foreground = Brushes.White,
                IsChecked = _settings.SortAscending,
                Margin = new Thickness(15, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            sortPanel.Children.Add(_sortAscending);
            optionsPanel.Children.Add(sortPanel);

            _showFavoritesFirst = new CheckBox
            {
                Content = "Show favorites at top of list",
                Foreground = Brushes.White,
                IsChecked = _settings.ShowFavoritesFirst,
                Margin = new Thickness(0, 5, 0, 0)
            };
            optionsPanel.Children.Add(_showFavoritesFirst);

            // Library Management Buttons
            var mgmtPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };

            var createEmptyBtn = new Button
            {
                Content = "Create Empty Library Structure",
                Padding = new Thickness(15, 8, 15, 8),
                Background = new SolidColorBrush(SketchPadColors.AccentGreen),
                Foreground = Brushes.White
            };
            createEmptyBtn.Click += CreateEmptyLibrary_Click;
            mgmtPanel.Children.Add(createEmptyBtn);

            var clearLibraryBtn = new Button
            {
                Content = "Clear All Library Content",
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(10, 0, 0, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 60, 60)),
                Foreground = Brushes.White
            };
            clearLibraryBtn.Click += ClearLibrary_Click;
            mgmtPanel.Children.Add(clearLibraryBtn);

            optionsPanel.Children.Add(mgmtPanel);

            // Export/Import Row
            var exportImportPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

            var exportBtn = new Button
            {
                Content = "📤 Export Profile",
                Padding = new Thickness(15, 8, 15, 8),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                Foreground = Brushes.White,
                ToolTip = "Export profile settings to JSON file for sharing"
            };
            exportBtn.Click += ExportProfile_Click;
            exportImportPanel.Children.Add(exportBtn);

            var importBtn = new Button
            {
                Content = "📥 Import Profile",
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(10, 0, 0, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                Foreground = Brushes.White,
                ToolTip = "Import profile settings from JSON file"
            };
            importBtn.Click += ImportProfile_Click;
            exportImportPanel.Children.Add(importBtn);

            optionsPanel.Children.Add(exportImportPanel);

            Grid.SetRow(optionsPanel, 4);
            mainGrid.Children.Add(optionsPanel);

            // === Bottom Buttons ===
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 100,
                Padding = new Thickness(0, 8, 0, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancelBtn.Click += (s, e) => Close();
            buttonPanel.Children.Add(cancelBtn);

            var saveBtn = new Button
            {
                Content = "Save Settings",
                Width = 120,
                Padding = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(SketchPadColors.AccentGreen),
                Foreground = Brushes.White
            };
            saveBtn.Click += SaveSettings_Click;
            buttonPanel.Children.Add(saveBtn);

            Grid.SetRow(buttonPanel, 6);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private TextBlock CreateLabel(string text, int row, int col)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            return label;
        }

        private TextBox CreateTextBox(int row, int col)
        {
            var textBox = new TextBox
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Foreground = Brushes.White,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 2, 0, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(textBox, row);
            Grid.SetColumn(textBox, col);
            return textBox;
        }

        private Button CreateBrowseButton(int row, Action onClick)
        {
            var btn = new Button
            {
                Content = "Browse...",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(5, 2, 0, 2)
            };
            btn.Click += (s, e) => onClick();
            Grid.SetRow(btn, row);
            Grid.SetColumn(btn, 2);
            return btn;
        }

        private void BrowseFolder(TextBox targetTextBox)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select library folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(targetTextBox.Text) && Directory.Exists(targetTextBox.Text))
                dialog.SelectedPath = targetTextBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                targetTextBox.Text = dialog.SelectedPath;
            }
        }

        private void LoadCurrentProfile()
        {
            var profile = _settings.ActiveProfile;
            _profileName.Text = profile.Name;
            _profileDescription.Text = profile.Description;
            _detailsPath.Text = profile.Paths.GetValueOrDefault("Details", "");
            _familiesPath.Text = profile.Paths.GetValueOrDefault("Families", "");
            _legendsPath.Text = profile.Paths.GetValueOrDefault("Legends", "");
            _schedulesPath.Text = profile.Paths.GetValueOrDefault("Schedules", "");
        }

        private void ProfileSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_profileSelector.SelectedItem != null)
            {
                _settings.SwitchProfile(_profileSelector.SelectedItem.ToString());
                LoadCurrentProfile();
            }
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            var nameDialog = new Window
            {
                Title = "New Profile",
                Width = 400,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(SketchPadColors.BgPrimary),
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Profile Name:", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 5) });

            var nameBox = new TextBox
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Foreground = Brushes.White,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 0, 0, 15)
            };
            panel.Children.Add(nameBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var createBtn = new Button
            {
                Content = "Create",
                Padding = new Thickness(20, 8, 20, 8),
                Background = new SolidColorBrush(SketchPadColors.AccentGreen),
                Foreground = Brushes.White
            };
            createBtn.Click += (s, args) =>
            {
                var name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show("Please enter a profile name.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (_settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("A profile with this name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _settings.CreateNewProfile(name);
                _profileSelector.Items.Add(name);
                _profileSelector.SelectedItem = name;
                nameDialog.Close();
            };
            btnPanel.Children.Add(createBtn);
            panel.Children.Add(btnPanel);

            nameDialog.Content = panel;
            nameDialog.ShowDialog();
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var profileName = _profileSelector.SelectedItem?.ToString();
            if (profileName == "Default")
            {
                MessageBox.Show("Cannot delete the Default profile.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Delete profile '{profileName}'?\n\nThis will NOT delete any files, only the profile configuration.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _settings.DeleteProfile(profileName);
                _profileSelector.Items.Remove(profileName);
                _profileSelector.SelectedItem = _settings.ActiveProfileName;
                LoadCurrentProfile();
            }
        }

        private void CreateEmptyLibrary_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select ROOT folder for new library (subfolders will be created)",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var rootPath = dialog.SelectedPath;

                // Create folder structure
                var detailsPath = Path.Combine(rootPath, "Details");
                var familiesPath = Path.Combine(rootPath, "Families");
                var legendsPath = Path.Combine(rootPath, "Legends");
                var schedulesPath = Path.Combine(rootPath, "Schedules");

                // Create main folders
                Directory.CreateDirectory(detailsPath);
                Directory.CreateDirectory(familiesPath);
                Directory.CreateDirectory(legendsPath);
                Directory.CreateDirectory(schedulesPath);

                // Create standard detail categories
                var detailCategories = new[]
                {
                    "01 - Roof Details", "02 - Cabinetry", "03 - Wall Details", "04 - Floor Details",
                    "05 - Door Details", "06 - Window Details", "07 - Stair Details", "08 - Ceiling Details",
                    "10 - Sections", "11 - Elevations", "12 - Bathroom Details", "14 - MEP Details",
                    "15 - Typical Details", "99 - General Details"
                };

                foreach (var cat in detailCategories)
                    Directory.CreateDirectory(Path.Combine(detailsPath, cat));

                // Create family categories
                var familyCategories = new[]
                {
                    "Casework", "Doors", "Electrical", "Furniture", "Generic Models",
                    "Lighting", "Mechanical Equipment", "Plumbing Fixtures", "Site", "Windows"
                };

                foreach (var cat in familyCategories)
                    Directory.CreateDirectory(Path.Combine(familiesPath, cat));

                // Update paths in UI
                _detailsPath.Text = detailsPath;
                _familiesPath.Text = familiesPath;
                _legendsPath.Text = legendsPath;
                _schedulesPath.Text = schedulesPath;

                MessageBox.Show($"Library structure created at:\n{rootPath}\n\nPaths have been updated. Click 'Save Settings' to apply.",
                    "Library Created", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ClearLibrary_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "⚠️ WARNING: This will permanently DELETE all files in your library folders!\n\n" +
                "This action cannot be undone.\n\n" +
                "Are you absolutely sure you want to clear all library content?",
                "Clear Library - DESTRUCTIVE ACTION",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // Double confirmation
            var confirm = MessageBox.Show(
                "FINAL WARNING!\n\nType 'DELETE' in your mind and click Yes to confirm deletion of all library files.",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop);

            if (confirm != MessageBoxResult.Yes)
                return;

            int deletedCount = 0;
            var paths = new[] { _detailsPath.Text, _familiesPath.Text, _legendsPath.Text, _schedulesPath.Text };

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                            .Where(f => f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ||
                                       f.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase));

                        foreach (var file in files)
                        {
                            File.Delete(file);
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error clearing {path}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            MessageBox.Show($"Deleted {deletedCount} files from library.", "Library Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportProfile_Click(object sender, RoutedEventArgs e)
        {
            var profile = _settings.ActiveProfile;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Library Profile",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"{profile.Name}_profile.json",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(profile, Formatting.Indented);
                    File.WriteAllText(dialog.FileName, json);
                    MessageBox.Show($"Profile '{profile.Name}' exported successfully.\n\n{dialog.FileName}",
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting profile: {ex.Message}",
                        "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ImportProfile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Library Profile",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var importedProfile = JsonConvert.DeserializeObject<LibraryProfile>(json);

                    if (importedProfile == null)
                    {
                        MessageBox.Show("Invalid profile file.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Check if profile name already exists
                    if (_settings.Profiles.Any(p => p.Name == importedProfile.Name))
                    {
                        var result = MessageBox.Show(
                            $"A profile named '{importedProfile.Name}' already exists.\n\n" +
                            "Do you want to replace it?",
                            "Profile Exists",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            var existing = _settings.Profiles.First(p => p.Name == importedProfile.Name);
                            _settings.Profiles.Remove(existing);
                        }
                        else
                        {
                            // Add suffix to make unique
                            importedProfile.Name = importedProfile.Name + "_imported";
                        }
                    }

                    _settings.Profiles.Add(importedProfile);
                    _settings.ActiveProfileName = importedProfile.Name;
                    _settings.Save();

                    // Refresh UI
                    _profileSelector.Items.Clear();
                    foreach (var p in _settings.Profiles)
                        _profileSelector.Items.Add(p.Name);
                    _profileSelector.SelectedItem = importedProfile.Name;
                    LoadCurrentProfile();

                    MessageBox.Show($"Profile '{importedProfile.Name}' imported successfully!",
                        "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error importing profile: {ex.Message}",
                        "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // Update profile
            var profile = _settings.ActiveProfile;
            profile.Name = _profileName.Text.Trim();
            profile.Description = _profileDescription.Text.Trim();
            profile.Paths["Details"] = _detailsPath.Text.Trim();
            profile.Paths["Families"] = _familiesPath.Text.Trim();
            profile.Paths["Legends"] = _legendsPath.Text.Trim();
            profile.Paths["Schedules"] = _schedulesPath.Text.Trim();

            // Update global settings
            _settings.SortBy = _sortBySelector.SelectedItem?.ToString() ?? "Name";
            _settings.SortAscending = _sortAscending.IsChecked == true;
            _settings.ShowFavoritesFirst = _showFavoritesFirst.IsChecked == true;

            _settings.Save();
            _onSettingsChanged?.Invoke();

            MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }

    #endregion

    /// <summary>
    /// Detail Library Window - Browse, preview, and import detail RVT files and families
    /// </summary>
    public class DetailLibraryWindow : Window
    {
        private UIApplication _uiApp;
        private Document _doc;

        // Settings (loaded from JSON)
        private LibrarySettings _settings;

        // Library paths from settings
        private Dictionary<LibraryContentType, string> _libraryPaths => new Dictionary<LibraryContentType, string>
        {
            { LibraryContentType.Details, _settings.GetPath(LibraryContentType.Details) },
            { LibraryContentType.Families, _settings.GetPath(LibraryContentType.Families) },
            { LibraryContentType.Legends, _settings.GetPath(LibraryContentType.Legends) },
            { LibraryContentType.Schedules, _settings.GetPath(LibraryContentType.Schedules) }
        };

        private LibraryContentType _currentContentType = LibraryContentType.Details;
        private ComboBox ContentTypeSelector;

        // External event for import (runs in valid API context)
        private ExternalEvent _importEvent;
        private DetailImportHandler _importHandler;

        // External event for schedule preview (extracts CSV in Revit context)
        private ExternalEvent _schedulePreviewEvent;
        private SchedulePreviewHandler _schedulePreviewHandler;
        private string _pendingSchedulePreviewCache;

        // General-purpose action runner: this window is modeless, so any
        // Revit API work (OpenDocumentFile, LoadFamily, Transactions) must
        // run inside an ExternalEvent, never in a click handler.
        private ExternalEvent _actionEvent;
        private RevitUiActionHandler _actionHandler;

        // UI Controls
        private TreeView CategoryTree;
        private ListView DetailList;
        private Image PreviewImage;
        private TextBox SearchBox;
        private TextBlock StatusText;
        private TextBlock DetailInfoText;
        private TextBlock PreviewPlaceholder;
        private Button ImportButton;
        private Button ImportCheckedButton;
        private Button DeleteButton;
        private Button OpenButton;
        private Button ExplorerButton;
        private Button SelectAllButton;
        private Button SelectNoneButton;
        private TextBlock CheckedCountText;
        private TextBlock PreviewLoadingText;  // Shows "Loading preview..." while generating
        private ComboBox _sortSelector;
        private Button _sortDirectionBtn;
        private Button _undoDeleteBtn;
        private ProgressBar _progressBar;
        private TextBlock _progressText;
        private Button _searchAllBtn;
        private bool _isGlobalSearchMode = false;

        // Undo Delete feature
        private static readonly string DeletedItemsFolder = Path.Combine(Path.GetTempPath(), "RevitContentLibraryDeleted");
        private List<DeletedItem> _deletedItems = new List<DeletedItem>();

        // Data
        private ObservableCollection<CategoryItem> _categories = new ObservableCollection<CategoryItem>();
        private ObservableCollection<DetailFile> _details = new ObservableCollection<DetailFile>();
        private DetailFile _selectedDetail;
        private string _currentCategory = "";

        public DetailLibraryWindow(UIApplication uiApp)
        {
            _uiApp = uiApp;
            _doc = uiApp?.ActiveUIDocument?.Document;

            // Load settings from JSON (or create defaults)
            _settings = LibrarySettings.Load();

            // Set up external event for import operations
            _importHandler = new DetailImportHandler();
            _importHandler.OnComplete = OnImportComplete;
            _importEvent = ExternalEvent.Create(_importHandler);

            // Set up external event for schedule preview
            _schedulePreviewHandler = new SchedulePreviewHandler();
            _schedulePreviewHandler.OnComplete = OnSchedulePreviewComplete;
            _schedulePreviewEvent = ExternalEvent.Create(_schedulePreviewHandler);

            // Set up the general-purpose Revit action runner
            _actionHandler = new RevitUiActionHandler();
            _actionEvent = ExternalEvent.Create(_actionHandler);

            // Window properties
            var profileName = _settings.ActiveProfile?.Name ?? "Default";
            Title = $"Content Library - {profileName}";
            Width = 2250;
            Height = 1000;
            MinWidth = 1950;
            MinHeight = 750;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(SketchPadColors.BgPrimary);
            ResizeMode = ResizeMode.CanResizeWithGrip;

            // Enable drag-drop for importing files
            AllowDrop = true;
            Drop += Window_Drop;
            DragEnter += Window_DragEnter;
            DragOver += Window_DragOver;

            BuildUI();
            LoadCategories();

            // Keyboard shortcuts
            this.KeyDown += Window_KeyDown;
        }

        private void BuildUI()
        {
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Toolbar
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status bar

            // Row 0: Toolbar
            var toolbar = CreateToolbar();
            Grid.SetRow(toolbar, 0);
            mainGrid.Children.Add(toolbar);

            // Row 1: Main content (3 columns)
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); // Categories
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); // Splitter
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Details
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); // Splitter
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(620), MinWidth = 500 }); // Preview - EVEN LARGER

            // Column 0: Category tree
            var categoryPanel = CreateCategoryPanel();
            Grid.SetColumn(categoryPanel, 0);
            contentGrid.Children.Add(categoryPanel);

            // Column 1: Splitter
            var splitter1 = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(SketchPadColors.Border)
            };
            Grid.SetColumn(splitter1, 1);
            contentGrid.Children.Add(splitter1);

            // Column 2: Detail list
            var detailPanel = CreateDetailPanel();
            Grid.SetColumn(detailPanel, 2);
            contentGrid.Children.Add(detailPanel);

            // Column 3: Splitter
            var splitter2 = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(SketchPadColors.Border)
            };
            Grid.SetColumn(splitter2, 3);
            contentGrid.Children.Add(splitter2);

            // Column 4: Preview panel - LARGER
            var previewPanel = CreatePreviewPanel();
            Grid.SetColumn(previewPanel, 4);
            contentGrid.Children.Add(previewPanel);

            Grid.SetRow(contentGrid, 1);
            mainGrid.Children.Add(contentGrid);

            // Row 2: Status bar
            var statusBar = CreateStatusBar();
            Grid.SetRow(statusBar, 2);
            mainGrid.Children.Add(statusBar);

            this.Content = mainGrid;
        }

        private Border CreateToolbar()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                BorderBrush = new SolidColorBrush(SketchPadColors.Border),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };

            // Content Type selector
            stack.Children.Add(new TextBlock
            {
                Text = "Library:",
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            ContentTypeSelector = new ComboBox
            {
                Width = 140,
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),  // Light background
                Foreground = Brushes.Black,  // BLACK text
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13
            };
            // Add items with BLACK text on light background
            var detailsItem = new ComboBoxItem { Content = "📐 Details", Tag = LibraryContentType.Details, Foreground = Brushes.Black, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)) };
            var familiesItem = new ComboBoxItem { Content = "📦 Families", Tag = LibraryContentType.Families, Foreground = Brushes.Black, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)) };
            var legendsItem = new ComboBoxItem { Content = "📋 Legends", Tag = LibraryContentType.Legends, Foreground = Brushes.Black, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)) };
            var schedulesItem = new ComboBoxItem { Content = "📊 Schedules", Tag = LibraryContentType.Schedules, Foreground = Brushes.Black, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)) };
            ContentTypeSelector.Items.Add(detailsItem);
            ContentTypeSelector.Items.Add(familiesItem);
            ContentTypeSelector.Items.Add(legendsItem);
            ContentTypeSelector.Items.Add(schedulesItem);
            ContentTypeSelector.SelectedIndex = 0;
            ContentTypeSelector.SelectionChanged += ContentTypeSelector_Changed;
            stack.Children.Add(ContentTypeSelector);

            // Spacer
            stack.Children.Add(new Border { Width = 20 });

            // Search icon
            stack.Children.Add(new TextBlock
            {
                Text = "🔍",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            // Search box
            SearchBox = new TextBox
            {
                Width = 250,
                Height = 28,
                Background = new SolidColorBrush(SketchPadColors.BgPrimary),
                Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                BorderBrush = new SolidColorBrush(SketchPadColors.Border),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            SearchBox.TextChanged += SearchBox_TextChanged;
            stack.Children.Add(SearchBox);

            // Search All button - global search across all categories
            stack.Children.Add(new Border { Width = 5 });
            _searchAllBtn = CreateToolbarButton("Search All", "🔍");
            _searchAllBtn.ToolTip = "Search across ALL categories and content types";
            _searchAllBtn.Click += (s, e) => SearchAllCategories();
            stack.Children.Add(_searchAllBtn);

            // Spacer
            stack.Children.Add(new Border { Width = 15 });

            // Sort selector
            stack.Children.Add(new TextBlock
            {
                Text = "Sort:",
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            _sortSelector = new ComboBox
            {
                Width = 100,
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100))
            };
            _sortSelector.Items.Add(new ComboBoxItem { Content = "Name", Tag = "Name", Foreground = Brushes.Black });
            _sortSelector.Items.Add(new ComboBoxItem { Content = "Date", Tag = "Date Modified", Foreground = Brushes.Black });
            _sortSelector.Items.Add(new ComboBoxItem { Content = "Size", Tag = "Size", Foreground = Brushes.Black });
            _sortSelector.SelectedIndex = _settings.SortBy == "Date Modified" ? 1 : (_settings.SortBy == "Size" ? 2 : 0);
            _sortSelector.SelectionChanged += SortSelector_Changed;
            stack.Children.Add(_sortSelector);

            // Sort direction button
            _sortDirectionBtn = new Button
            {
                Content = _settings.SortAscending ? "↑" : "↓",
                Width = 28,
                Height = 28,
                Margin = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 70)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                ToolTip = _settings.SortAscending ? "Ascending (click to reverse)" : "Descending (click to reverse)"
            };
            _sortDirectionBtn.Click += SortDirection_Click;
            stack.Children.Add(_sortDirectionBtn);

            // Spacer
            stack.Children.Add(new Border { Width = 15 });

            // Selection buttons
            SelectAllButton = CreateToolbarButton("Select All", "☑");
            SelectAllButton.Click += (s, e) => SelectAll(true);
            stack.Children.Add(SelectAllButton);

            stack.Children.Add(new Border { Width = 5 });

            SelectNoneButton = CreateToolbarButton("Select None", "☐");
            SelectNoneButton.Click += (s, e) => SelectAll(false);
            stack.Children.Add(SelectNoneButton);

            // Spacer
            stack.Children.Add(new Border { Width = 15 });

            // Checked count
            CheckedCountText = new TextBlock
            {
                Text = "0 checked",
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            stack.Children.Add(CheckedCountText);

            // Refresh button
            var refreshBtn = CreateToolbarButton("Refresh", "↻");
            refreshBtn.Click += (s, e) => RefreshLibrary();
            stack.Children.Add(refreshBtn);

            // Clean Library button - scan ALL categories and delete empty files
            stack.Children.Add(new Border { Width = 5 });
            var cleanBtn = CreateToolbarButton("Clean Library", "🧹");
            cleanBtn.Click += (s, e) => CleanEntireLibrary();
            stack.Children.Add(cleanBtn);

            // Extract from Project button - export drafting views from current project
            stack.Children.Add(new Border { Width = 5 });
            var extractBtn = CreateToolbarButton("Extract from Project", "📤");
            extractBtn.Click += (s, e) => ExtractFromCurrentProject();
            stack.Children.Add(extractBtn);

            // Delete Selected button - remove detail from library
            stack.Children.Add(new Border { Width = 5 });
            DeleteButton = CreateToolbarButton("Delete Selected", "🗑");
            DeleteButton.Click += (s, e) => DeleteSelectedDetail();
            DeleteButton.IsEnabled = false;
            stack.Children.Add(DeleteButton);

            // Undo Delete button
            stack.Children.Add(new Border { Width = 5 });
            _undoDeleteBtn = CreateToolbarButton("Undo Delete", "↩");
            _undoDeleteBtn.Click += (s, e) => UndoDelete();
            _undoDeleteBtn.IsEnabled = false;
            stack.Children.Add(_undoDeleteBtn);

            // Batch Rename button
            stack.Children.Add(new Border { Width = 5 });
            var batchRenameBtn = CreateToolbarButton("Batch Rename", "✏");
            batchRenameBtn.ToolTip = "Rename multiple files using patterns";
            batchRenameBtn.Click += (s, e) => ShowBatchRenameDialog();
            stack.Children.Add(batchRenameBtn);

            // Import Checked button
            stack.Children.Add(new Border { Width = 10 });
            ImportCheckedButton = CreateColoredButton("Import Checked", "📥", SketchPadColors.AccentGreen);
            ImportCheckedButton.MinWidth = 130;  // Ensure text fits
            ImportCheckedButton.Click += (s, e) => ImportCheckedDetails();
            ImportCheckedButton.IsEnabled = false;
            stack.Children.Add(ImportCheckedButton);

            // Import Selected button - same size as Import Checked
            stack.Children.Add(new Border { Width = 5 });
            ImportButton = CreateToolbarButton("Import Selected", "📄");
            ImportButton.MinWidth = 130;  // Match Import Checked size
            ImportButton.Click += (s, e) => ImportSelectedDetails();
            ImportButton.IsEnabled = false;
            stack.Children.Add(ImportButton);

            // Statistics button
            stack.Children.Add(new Border { Width = 15 });
            var statsBtn = CreateToolbarButton("Stats", "📊");
            statsBtn.ToolTip = "View library statistics and usage";
            statsBtn.Click += (s, e) => ShowLibraryStatistics();
            stack.Children.Add(statsBtn);

            // Settings button - opens library configuration
            stack.Children.Add(new Border { Width = 5 });
            var settingsBtn = CreateToolbarButton("Settings", "⚙");
            settingsBtn.Click += (s, e) => OpenSettings();
            stack.Children.Add(settingsBtn);

            border.Child = stack;
            return border;
        }

        private Button CreateToolbarButton(string text, string icon)
        {
            var btn = new Button
            {
                Content = $"{icon}  {text}",
                Padding = new Thickness(8, 6, 8, 6),  // Reduced horizontal padding
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            // ALL appearance properties go in Style so triggers can override them
            var style = new Style(typeof(Button));

            // Normal state (enabled)
            style.Setters.Add(new Setter(Button.BackgroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 70))));
            style.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Button.BorderBrushProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100))));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Button.FontWeightProperty, FontWeights.Medium));
            style.Setters.Add(new Setter(Button.OpacityProperty, 1.0));

            // Disabled state - BLACK text on light gray background for visibility
            var disabledTrigger = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(Button.BackgroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200))));  // Light gray background
            disabledTrigger.Setters.Add(new Setter(Button.ForegroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0))));  // BLACK text when disabled
            disabledTrigger.Setters.Add(new Setter(Button.BorderBrushProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150))));
            style.Triggers.Add(disabledTrigger);

            btn.Style = style;
            return btn;
        }

        /// <summary>
        /// Create a colored toolbar button with high-contrast disabled state
        /// </summary>
        private Button CreateColoredButton(string text, string icon, System.Windows.Media.Color accentColor)
        {
            var btn = new Button
            {
                Content = $"{icon}  {text}",
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand
            };

            // ALL appearance properties go in Style so triggers can override them
            var style = new Style(typeof(Button));

            // Normal state (enabled) - use accent color
            style.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(accentColor)));
            style.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Button.BorderBrushProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100))));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Button.FontWeightProperty, FontWeights.Bold));
            style.Setters.Add(new Setter(Button.OpacityProperty, 1.0));

            // Disabled state - BLACK text on light gray background for visibility
            var disabledTrigger = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(Button.BackgroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200))));  // Light gray background
            disabledTrigger.Setters.Add(new Setter(Button.ForegroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0))));  // BLACK text when disabled
            disabledTrigger.Setters.Add(new Setter(Button.BorderBrushProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150))));
            style.Triggers.Add(disabledTrigger);

            btn.Style = style;
            return btn;
        }

        private Border CreateCategoryPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Margin = new Thickness(5)
            };

            var stack = new StackPanel();

            // Header
            var header = new TextBlock
            {
                Text = "📁 Categories",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                Padding = new Thickness(10, 10, 10, 5)
            };
            stack.Children.Add(header);

            // Tree view with custom item style for selection
            CategoryTree = new TreeView
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5)
            };

            // Style for TreeViewItems - BLACK text when selected
            var treeItemStyle = new Style(typeof(TreeViewItem));
            treeItemStyle.Setters.Add(new Setter(TreeViewItem.ForegroundProperty, new SolidColorBrush(SketchPadColors.TextPrimary)));
            var selectedTrigger = new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(TreeViewItem.ForegroundProperty, Brushes.Black));
            treeItemStyle.Triggers.Add(selectedTrigger);
            CategoryTree.ItemContainerStyle = treeItemStyle;

            CategoryTree.SelectedItemChanged += CategoryTree_SelectedItemChanged;

            var scrollViewer = new ScrollViewer
            {
                Content = CategoryTree,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            stack.Children.Add(scrollViewer);

            border.Child = stack;
            return border;
        }

        private Border CreateDetailPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Margin = new Thickness(0, 5, 0, 5)
            };

            // Header
            var header = new TextBlock
            {
                Text = "📋 Details",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                Padding = new Thickness(10, 10, 10, 5)
            };

            // List view with checkboxes
            DetailList = new ListView
            {
                Background = new SolidColorBrush(SketchPadColors.BgPrimary),
                Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(SketchPadColors.Border),
                SelectionMode = SelectionMode.Extended,
                Margin = new Thickness(5)
            };

            // Create GridView with checkbox column
            var gridView = new GridView();

            // Checkbox column
            var checkBoxColumn = new GridViewColumn
            {
                Header = "✓",
                Width = 40
            };

            // Create a DataTemplate for the checkbox
            var checkBoxTemplate = new DataTemplate();
            var checkBoxFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkBoxFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding("IsChecked") { Mode = BindingMode.TwoWay });
            checkBoxFactory.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler(CheckBox_Changed));
            checkBoxFactory.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler(CheckBox_Changed));
            checkBoxTemplate.VisualTree = checkBoxFactory;
            checkBoxColumn.CellTemplate = checkBoxTemplate;
            gridView.Columns.Add(checkBoxColumn);

            // Name column - shows star for favorites
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Name",
                Width = 350,
                DisplayMemberBinding = new Binding("DisplayName")
            });

            // Size column
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Size",
                Width = 80,
                DisplayMemberBinding = new Binding("SizeText")
            });

            // Modified column
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Modified",
                Width = 120,
                DisplayMemberBinding = new Binding("ModifiedText")
            });

            DetailList.View = gridView;
            DetailList.ItemsSource = _details;
            DetailList.SelectionChanged += DetailList_SelectionChanged;
            DetailList.MouseDoubleClick += DetailList_MouseDoubleClick;

            // Style for ListView items - ensure WHITE text when selected for legibility
            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(ListViewItem.ForegroundProperty, Brushes.White));
            itemStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(4, 2, 4, 2)));
            itemStyle.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 1, 0, 1)));

            // Trigger for selected state - bright white on dark blue
            var selectedTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 90, 158))));  // Dark blue
            selectedTrigger.Setters.Add(new Setter(ListViewItem.ForegroundProperty, Brushes.White));
            selectedTrigger.Setters.Add(new Setter(ListViewItem.FontWeightProperty, FontWeights.SemiBold));
            itemStyle.Triggers.Add(selectedTrigger);

            // Trigger for mouse over
            var hoverTrigger = new Trigger { Property = ListViewItem.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60))));  // Light gray hover
            itemStyle.Triggers.Add(hoverTrigger);

            DetailList.ItemContainerStyle = itemStyle;

            // Context menu for right-click actions
            DetailList.ContextMenu = CreateDetailContextMenu();

            var listScrollViewer = new ScrollViewer
            {
                Content = DetailList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dockPanel = new DockPanel();
            dockPanel.Children.Add(header);
            DockPanel.SetDock(header, Dock.Top);
            dockPanel.Children.Add(listScrollViewer);

            border.Child = dockPanel;
            return border;
        }

        private Border CreatePreviewPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Margin = new Thickness(5)
            };

            var mainStack = new StackPanel();

            // Header
            var header = new TextBlock
            {
                Text = "👁 Preview",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                Padding = new Thickness(10, 10, 10, 5)
            };
            mainStack.Children.Add(header);

            // Preview image area - WHITE BACKGROUND, FILLS AVAILABLE SPACE
            var imageBorder = new Border
            {
                Background = Brushes.White,  // WHITE BACKGROUND for details
                BorderBrush = new SolidColorBrush(SketchPadColors.Border),
                BorderThickness = new Thickness(2),
                Height = 520,  // LARGER for better preview visibility
                Margin = new Thickness(10, 5, 10, 10)
            };

            // Container for image and placeholder
            var imageGrid = new Grid();

            PreviewImage = new Image
            {
                Stretch = Stretch.Uniform,  // Maintains aspect ratio while filling space
                StretchDirection = StretchDirection.Both,  // Allow both up and down scaling
                Margin = new Thickness(5)  // Minimal margin to maximize preview size
            };
            imageGrid.Children.Add(PreviewImage);

            // Placeholder text when no preview - BLACK TEXT on white background
            PreviewPlaceholder = new TextBlock
            {
                Text = "📄\n\nSelect a detail to preview\n\nPreview will load automatically",
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),  // DARK text on white bg
                FontSize = 16,  // LARGER text
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap
            };
            imageGrid.Children.Add(PreviewPlaceholder);

            // Loading indicator
            PreviewLoadingText = new TextBlock
            {
                Text = "⏳ Loading preview...",
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 200)),  // BLUE loading text
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Visibility = System.Windows.Visibility.Collapsed
            };
            imageGrid.Children.Add(PreviewLoadingText);

            imageBorder.Child = imageGrid;
            mainStack.Children.Add(imageBorder);

            // Detail info panel - HIGH CONTRAST for legibility
            var infoBorder = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40)),  // Dark gray
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10, 0, 10, 10),
                Padding = new Thickness(12)
            };

            DetailInfoText = new TextBlock
            {
                Foreground = Brushes.White,  // BRIGHT WHITE text for maximum legibility
                TextWrapping = TextWrapping.Wrap,
                Text = "Select a detail to see information",
                FontSize = 13,
                FontWeight = FontWeights.Medium
            };
            infoBorder.Child = DetailInfoText;
            mainStack.Children.Add(infoBorder);

            // Action buttons
            var buttonStack = new StackPanel
            {
                Margin = new Thickness(10, 5, 10, 10)
            };

            // Import button with disabled style
            ImportButton = new Button
            {
                Content = "📥  Import to Current Drawing",
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                IsEnabled = false
            };
            var importStyle = new Style(typeof(Button));
            importStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(SketchPadColors.AccentGreen)));
            importStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            importStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            var importDisabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            importDisabled.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220))));
            importDisabled.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.Black));
            importStyle.Triggers.Add(importDisabled);
            ImportButton.Style = importStyle;
            ImportButton.Click += (s, e) => ImportSelectedDetails();
            buttonStack.Children.Add(ImportButton);

            // Open button with disabled style
            OpenButton = new Button
            {
                Content = "📂  Open in Revit",
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                FontSize = 13,
                IsEnabled = false
            };
            var openStyle = new Style(typeof(Button));
            openStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(SketchPadColors.AccentBlue)));
            openStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            openStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            var openDisabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            openDisabled.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220))));
            openDisabled.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.Black));
            openStyle.Triggers.Add(openDisabled);
            OpenButton.Style = openStyle;
            OpenButton.Click += (s, e) => OpenSelectedDetail();
            buttonStack.Children.Add(OpenButton);

            // Explorer button with disabled style
            ExplorerButton = new Button
            {
                Content = "📁  Show in Explorer",
                Padding = new Thickness(15, 10, 15, 10),
                Cursor = Cursors.Hand,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                IsEnabled = false
            };
            var explorerStyle = new Style(typeof(Button));
            explorerStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 70))));
            explorerStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            explorerStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100))));
            explorerStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            var explorerDisabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            explorerDisabled.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220))));
            explorerDisabled.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.Black));
            explorerStyle.Triggers.Add(explorerDisabled);
            ExplorerButton.Style = explorerStyle;
            ExplorerButton.Click += (s, e) => ShowInExplorer();
            buttonStack.Children.Add(ExplorerButton);

            mainStack.Children.Add(buttonStack);

            border.Child = mainStack;
            return border;
        }

        private Border CreateStatusBar()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                BorderBrush = new SolidColorBrush(SketchPadColors.Border),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: Status text
            StatusText = new TextBlock
            {
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                Text = "Ready - Select a category to browse details",
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(StatusText, 0);
            grid.Children.Add(StatusText);

            // Center: Progress bar (hidden by default)
            var progressPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = System.Windows.Visibility.Collapsed
            };

            _progressText = new TextBlock
            {
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            progressPanel.Children.Add(_progressText);

            _progressBar = new ProgressBar
            {
                Width = 200,
                Height = 16,
                Minimum = 0,
                Maximum = 100,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                Foreground = new SolidColorBrush(SketchPadColors.AccentGreen)
            };
            progressPanel.Children.Add(_progressBar);

            Grid.SetColumn(progressPanel, 1);
            grid.Children.Add(progressPanel);

            // Right: Keyboard hints
            var hints = new TextBlock
            {
                Text = "Ctrl+F: Search  |  F5: Refresh  |  Enter: Import  |  Esc: Close",
                Foreground = new SolidColorBrush(SketchPadColors.TextSecondary),
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(hints, 2);
            grid.Children.Add(hints);

            border.Child = grid;
            return border;
        }

        private void ShowProgress(string message, int current, int total)
        {
            _progressText.Text = message;
            _progressBar.Value = total > 0 ? (current * 100.0 / total) : 0;
            if (_progressBar.Parent is StackPanel panel)
                panel.Visibility = System.Windows.Visibility.Visible;
        }

        private void HideProgress()
        {
            if (_progressBar.Parent is StackPanel panel)
                panel.Visibility = System.Windows.Visibility.Collapsed;
        }

        /// <summary>
        /// Gets the current library path based on selected content type
        /// </summary>
        private string CurrentLibraryPath => _libraryPaths[_currentContentType];

        /// <summary>
        /// Gets the file extension for the current content type
        /// </summary>
        private string CurrentFileExtension => _currentContentType == LibraryContentType.Families ? "*.rfa" : "*.rvt";

        /// <summary>
        /// Check if a file is a Revit backup file (e.g., filename.0001.rfa, filename.0002.rvt)
        /// </summary>
        private static bool IsBackupFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            // Backup files have pattern: name.####.rfa or name.####.rvt (4 digits before extension)
            // Example: "Door-Single.0001.rfa", "Wall Detail.0003.rvt"
            return System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{4}\.(rfa|rvt)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Get files from directory excluding backup files
        /// </summary>
        private static string[] GetFilesExcludingBackups(string directory, string pattern)
        {
            return Directory.GetFiles(directory, pattern)
                .Where(f => !IsBackupFile(f))
                .ToArray();
        }

        /// <summary>
        /// Gets the content type label for display
        /// </summary>
        private string CurrentContentLabel
        {
            get
            {
                switch (_currentContentType)
                {
                    case LibraryContentType.Families: return "families";
                    case LibraryContentType.Legends: return "legends";
                    case LibraryContentType.Schedules: return "schedules";
                    default: return "details";
                }
            }
        }

        /// <summary>
        /// Gets the icon for the current content type
        /// </summary>
        private string CurrentContentIcon
        {
            get
            {
                switch (_currentContentType)
                {
                    case LibraryContentType.Families: return "📦";
                    case LibraryContentType.Legends: return "📋";
                    case LibraryContentType.Schedules: return "📊";
                    default: return "📐";
                }
            }
        }

        /// <summary>
        /// Handle content type selection change
        /// </summary>
        private void ContentTypeSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ContentTypeSelector.SelectedItem is ComboBoxItem item && item.Tag is LibraryContentType type)
            {
                _currentContentType = type;

                // Update window title
                switch (_currentContentType)
                {
                    case LibraryContentType.Families:
                        Title = "Content Library - Browse & Import Families";
                        break;
                    case LibraryContentType.Legends:
                        Title = "Content Library - Browse & Import Legends";
                        break;
                    case LibraryContentType.Schedules:
                        Title = "Content Library - Browse & Import Schedules";
                        break;
                    default:
                        Title = "Content Library - Browse & Import Details";
                        break;
                }

                // Clear current selection and reload
                _details.Clear();
                _selectedDetail = null;
                PreviewImage.Source = null;
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
                DetailInfoText.Text = "";

                LoadCategories();
            }
        }

        private void LoadCategories()
        {
            _categories.Clear();
            CategoryTree.Items.Clear();

            var libraryPath = CurrentLibraryPath;
            var filePattern = CurrentFileExtension;

            if (!Directory.Exists(libraryPath))
            {
                StatusText.Text = $"Library not found: {libraryPath}";
                return;
            }

            int totalFiles = 0;
            var dirs = Directory.GetDirectories(libraryPath).OrderBy(d => d).ToList();

            // Choose icon based on content type
            string folderIcon = CurrentContentIcon;

            // Special case: Legends is a flat folder with no subcategories
            if (_currentContentType == LibraryContentType.Legends && dirs.Count == 0)
            {
                // Load files directly from root folder (excluding backups)
                var rootFiles = GetFilesExcludingBackups(libraryPath, filePattern);
                totalFiles = rootFiles.Length;

                var item = new TreeViewItem
                {
                    Header = $"{folderIcon} All Legends ({totalFiles})",
                    Tag = libraryPath,
                    Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                    Padding = new Thickness(5, 4, 5, 4),
                    FontSize = 12,
                    IsSelected = true
                };

                _categories.Add(new CategoryItem { Name = "All Legends", Path = libraryPath, Count = totalFiles });
                CategoryTree.Items.Add(item);

                // Auto-load the category
                LoadDetailsForCategory(libraryPath);
            }
            else
            {
                foreach (var dir in dirs)
                {
                    var dirName = Path.GetFileName(dir);
                    // Exclude backup files from count
                    var files = GetFilesExcludingBackups(dir, filePattern);
                    var count = files.Length;
                    totalFiles += count;

                    var item = new TreeViewItem
                    {
                        Header = $"{folderIcon} {dirName} ({count})",
                        Tag = dir,
                        Foreground = new SolidColorBrush(SketchPadColors.TextPrimary),
                        Padding = new Thickness(5, 4, 5, 4),
                        FontSize = 12
                    };

                    _categories.Add(new CategoryItem { Name = dirName, Path = dir, Count = count });
                    CategoryTree.Items.Add(item);
                }
            }

            StatusText.Text = $"{totalFiles} {CurrentContentLabel} in {_categories.Count} categories";
        }

        private void LoadDetailsForCategory(string categoryPath)
        {
            _details.Clear();
            _currentCategory = Path.GetFileName(categoryPath);

            if (!Directory.Exists(categoryPath))
                return;

            // Get files excluding backup files (e.g., name.0001.rfa)
            var files = GetFilesExcludingBackups(categoryPath, CurrentFileExtension)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name);

            foreach (var file in files)
            {
                _details.Add(new DetailFile
                {
                    Name = Path.GetFileNameWithoutExtension(file.Name),
                    FullPath = file.FullName,
                    Size = file.Length,
                    Modified = file.LastWriteTime,
                    IsChecked = false,
                    IsFavorite = _settings.IsFavorite(file.FullName)
                });
            }

            // Sort with favorites first if enabled
            if (_settings.ShowFavoritesFirst)
            {
                var sorted = _details.OrderByDescending(d => d.IsFavorite).ThenBy(d => d.Name).ToList();
                _details.Clear();
                foreach (var d in sorted)
                    _details.Add(d);
            }

            // Track in recent items
            _settings.AddToRecent(categoryPath);

            UpdateCheckedCount();
            StatusText.Text = $"{_details.Count} {CurrentContentLabel} in {_currentCategory}";
        }

        private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (CategoryTree.SelectedItem is TreeViewItem item && item.Tag is string path)
            {
                LoadDetailsForCategory(path);
            }
        }

        private void DetailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = DetailList.SelectedItems.Cast<DetailFile>().ToList();
            var hasSelection = selected.Count > 0;

            // Enable/disable buttons based on selection
            ImportButton.IsEnabled = hasSelection;
            OpenButton.IsEnabled = selected.Count == 1;
            ExplorerButton.IsEnabled = selected.Count == 1;
            DeleteButton.IsEnabled = selected.Count >= 1;  // Can delete one or more

            if (selected.Count == 1)
            {
                _selectedDetail = selected[0];
                UpdatePreview(_selectedDetail);
            }
            else if (selected.Count > 1)
            {
                _selectedDetail = null;
                DetailInfoText.Text = $"📄  {selected.Count} details selected\n\n" +
                                     $"📏  Total size: {FormatSize(selected.Sum(d => d.Size))}\n\n" +
                                     $"Click 'Import to Current Drawing' to import all.";
                PreviewImage.Source = null;
                PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                PreviewPlaceholder.Text = $"📄\n\n{selected.Count} details selected\n\nSelect a single detail\nto see preview";
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                _selectedDetail = null;
                DetailInfoText.Text = "Select a detail to see information";
                PreviewImage.Source = null;
                PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                PreviewPlaceholder.Text = "📄\n\nSelect a detail to preview\n\nPreview loads automatically";
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
            }

            var checkedCount = _details.Count(d => d.IsChecked);
            StatusText.Text = hasSelection
                ? $"{selected.Count} selected, {checkedCount} checked | {_details.Count} {CurrentContentLabel} in {_currentCategory}"
                : $"{checkedCount} checked | {_details.Count} {CurrentContentLabel} in {_currentCategory}";
        }

        private void CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCheckedCount();
        }

        private void UpdateCheckedCount()
        {
            var checkedCount = _details.Count(d => d.IsChecked);
            CheckedCountText.Text = $"{checkedCount} checked";
            ImportCheckedButton.IsEnabled = checkedCount > 0;
        }

        private void SelectAll(bool select)
        {
            foreach (var detail in _details)
            {
                detail.IsChecked = select;
            }
            DetailList.Items.Refresh();
            UpdateCheckedCount();
        }

        private void DetailList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_selectedDetail != null)
            {
                ImportSelectedDetails();
            }
        }

        private void UpdatePreview(DetailFile detail)
        {
            // Different icons and labels based on content type
            string typeIcon = _currentContentType == LibraryContentType.Families ? "📦" : "📄";
            string typeLabel = _currentContentType == LibraryContentType.Families ? "Family" : "Detail";

            // Update detail info with WHITE text for legibility
            DetailInfoText.Text = $"{typeIcon}  {detail.Name}\n\n" +
                                  $"📏  Size: {detail.SizeText}\n" +
                                  $"📅  Modified: {detail.ModifiedText}\n\n" +
                                  $"📁  {detail.FullPath}";

            // For families (RFA), try to load embedded preview image
            if (_currentContentType == LibraryContentType.Families)
            {
                UpdateFamilyPreview(detail);
                return;
            }

            // For Schedules, generate a placeholder preview (schedules can't be exported as images easily)
            if (_currentContentType == LibraryContentType.Schedules)
            {
                UpdateSchedulePreview(detail);
                return;
            }

            // For RVT files (Details, Legends), check for cached preview first (fastest)
            if (RvtPreviewGenerator.HasCachedPreview(detail.FullPath))
            {
                var cached = RvtPreviewGenerator.LoadCachedPreview(detail.FullPath);
                if (cached != null)
                {
                    PreviewImage.Source = cached;
                    PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                    PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                    StatusText.Text = $"Preview: {detail.Name}";
                    return;
                }
            }

            // No cached preview - AUTO-GENERATE IT NOW
            if (_uiApp != null)
            {
                // Show loading state
                PreviewImage.Source = null;
                PreviewLoadingText.Text = "⏳ Loading preview...";
                PreviewLoadingText.Visibility = System.Windows.Visibility.Visible;
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                StatusText.Text = $"Generating preview: {detail.Name}...";

                // Force UI update
                this.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                try
                {
                    // Generate the preview using Revit API
                    var cachePath = RvtPreviewGenerator.GeneratePreview(_uiApp.Application, detail.FullPath);

                    if (cachePath != null && File.Exists(cachePath))
                    {
                        // Load and display the generated preview
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(cachePath);
                        bitmap.EndInit();
                        bitmap.Freeze();

                        PreviewImage.Source = bitmap;
                        PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                        PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                        StatusText.Text = $"Preview: {detail.Name}";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preview generation failed: {ex.Message}");
                }
            }

            // Fallback if generation failed
            PreviewImage.Source = null;
            PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
            PreviewPlaceholder.Text = "📄\n\nPreview not available";
            PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
            StatusText.Text = $"No preview: {detail.Name}";
        }

        /// <summary>
        /// Update preview for RFA family files
        /// RFA files have embedded thumbnail images that can be extracted
        /// </summary>
        private void UpdateFamilyPreview(DetailFile detail)
        {
            try
            {
                // First check if we have a cached preview
                var cachePath = RvtPreviewGenerator.GetCachePath(detail.FullPath);
                if (File.Exists(cachePath))
                {
                    var fileInfo = new FileInfo(cachePath);
                    var rvtInfo = new FileInfo(detail.FullPath);
                    if (fileInfo.LastWriteTime > rvtInfo.LastWriteTime)
                    {
                        // Cached preview is up to date
                        var cached = RvtPreviewGenerator.LoadCachedPreview(detail.FullPath);
                        if (cached != null)
                        {
                            PreviewImage.Source = cached;
                            PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                            PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                            StatusText.Text = $"Family: {detail.Name}";
                            return;
                        }
                    }
                }

                // Try to extract thumbnail from RFA file
                // RFA files are OLE structured storage containing a thumbnail stream
                var thumbnail = ExtractFamilyThumbnail(detail.FullPath);
                if (thumbnail != null)
                {
                    PreviewImage.Source = thumbnail;
                    PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                    PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                    StatusText.Text = $"Family: {detail.Name}";
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Family preview failed: {ex.Message}");
            }

            // Fallback - show family placeholder
            PreviewImage.Source = null;
            PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
            PreviewPlaceholder.Text = "📦\n\nRevit Family\n\n" + detail.Name;
            PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
            StatusText.Text = $"Family: {detail.Name} (no preview)";
        }

        /// <summary>
        /// Update preview for Schedule RVT files
        /// Uses external event to extract actual schedule data in Revit API context
        /// </summary>
        private void UpdateSchedulePreview(DetailFile detail)
        {
            try
            {
                // Check for cached preview first
                var cachePath = RvtPreviewGenerator.GetCachePath(detail.FullPath);
                if (File.Exists(cachePath))
                {
                    var fileInfo = new FileInfo(cachePath);
                    var rvtInfo = new FileInfo(detail.FullPath);
                    if (fileInfo.LastWriteTime > rvtInfo.LastWriteTime)
                    {
                        var cached = RvtPreviewGenerator.LoadCachedPreview(detail.FullPath);
                        if (cached != null)
                        {
                            PreviewImage.Source = cached;
                            PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                            PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                            StatusText.Text = $"Schedule: {detail.Name}";
                            return;
                        }
                    }
                }

                // Show loading indicator
                PreviewLoadingText.Text = "⏳ Extracting schedule data...";
                PreviewLoadingText.Visibility = System.Windows.Visibility.Visible;
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                PreviewImage.Source = null;
                this.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                // Use external event to extract schedule data in Revit context
                if (_schedulePreviewEvent != null && _schedulePreviewHandler != null)
                {
                    // Store cache path for callback
                    _pendingSchedulePreviewCache = cachePath;

                    // Generate a unique CSV path
                    var csvPath = Path.Combine(Path.GetTempPath(), $"schedule_preview_{Guid.NewGuid():N}.csv");

                    _schedulePreviewHandler.RvtFilePath = detail.FullPath;
                    _schedulePreviewHandler.CsvOutputPath = csvPath;
                    _schedulePreviewHandler.ScheduleName = detail.Name;

                    _schedulePreviewEvent.Raise();
                    StatusText.Text = $"Extracting schedule: {detail.Name}...";
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Schedule preview failed: {ex.Message}");
            }

            // Fallback - show text placeholder
            PreviewImage.Source = null;
            PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
            PreviewPlaceholder.Text = "📊\n\nRevit Schedule\n\n" + detail.Name;
            PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
            StatusText.Text = $"Schedule: {detail.Name}";
        }

        /// <summary>
        /// Callback when schedule preview extraction completes
        /// </summary>
        private void OnSchedulePreviewComplete(bool success, string csvPath, string scheduleNameOrError)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (success && !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath))
                    {
                        // Read and parse CSV data
                        var lines = File.ReadAllLines(csvPath);
                        if (lines.Length > 0)
                        {
                            // Parse CSV into headers and rows
                            var headers = ParseCsvLine(lines[0]);
                            var rows = new List<List<string>>();

                            for (int i = 1; i < Math.Min(lines.Length, 16); i++) // First 15 data rows
                            {
                                var row = ParseCsvLine(lines[i]);
                                rows.Add(row);
                            }

                            // Render as image
                            var imagePath = RenderScheduleCsvAsImage(_pendingSchedulePreviewCache, scheduleNameOrError, headers, rows, lines.Length - 1, headers.Count);

                            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.UriSource = new Uri(imagePath);
                                bitmap.EndInit();
                                bitmap.Freeze();

                                PreviewImage.Source = bitmap;
                                PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                                PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                                StatusText.Text = $"Schedule: {scheduleNameOrError} ({lines.Length - 1} rows)";
                            }
                        }

                        // Clean up temp CSV
                        try { File.Delete(csvPath); } catch { }
                    }
                    else
                    {
                        // Show error or fallback
                        PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                        PreviewPlaceholder.Text = $"📊\n\nSchedule\n\n{(string.IsNullOrEmpty(scheduleNameOrError) ? "Error loading" : scheduleNameOrError)}";
                        PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
                        StatusText.Text = $"Schedule preview: {scheduleNameOrError}";
                    }
                }
                catch (Exception ex)
                {
                    PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                    PreviewPlaceholder.Text = "📊\n\nError\n\n" + ex.Message;
                    PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
                }
            }));
        }

        /// <summary>
        /// Parse a CSV line handling quoted fields
        /// </summary>
        private List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var inQuotes = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString().Trim());
            return result;
        }

        /// <summary>
        /// Render CSV schedule data as an image
        /// </summary>
        private string RenderScheduleCsvAsImage(string cachePath, string scheduleName, List<string> headers, List<List<string>> rows, int totalRows, int numCols)
        {
            try
            {
                int imgWidth = 500;
                int imgHeight = 400;
                int headerHeight = 30;
                int rowHeight = 22;
                int titleHeight = 35;
                int marginX = 10;
                int marginY = 10;

                // Calculate column widths
                int availableWidth = imgWidth - (2 * marginX);
                int visibleCols = Math.Min(numCols, 6);
                int colWidth = visibleCols > 0 ? availableWidth / visibleCols : 80;

                using (var bitmap = new System.Drawing.Bitmap(imgWidth, imgHeight))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    // Background
                    graphics.Clear(System.Drawing.Color.White);

                    // Border
                    using (var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, 180, 180), 1))
                    {
                        graphics.DrawRectangle(borderPen, 0, 0, imgWidth - 1, imgHeight - 1);
                    }

                    int currentY = marginY;

                    // Draw schedule title
                    using (var titleFont = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Bold))
                    using (var titleBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 30, 30)))
                    {
                        var displayName = scheduleName.Length > 50 ? scheduleName.Substring(0, 47) + "..." : scheduleName;
                        graphics.DrawString(displayName, titleFont, titleBrush, marginX, currentY);
                    }
                    currentY += titleHeight;

                    int tableX = marginX;

                    // Draw header row with blue background
                    using (var headerBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(70, 130, 180)))
                    using (var headerFont = new System.Drawing.Font("Segoe UI", 8, System.Drawing.FontStyle.Bold))
                    using (var whiteBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                    using (var gridPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(150, 150, 150), 1))
                    {
                        // Header background
                        graphics.FillRectangle(headerBrush, tableX, currentY, visibleCols * colWidth, headerHeight);

                        // Header text
                        for (int col = 0; col < visibleCols && col < headers.Count; col++)
                        {
                            var headerText = headers[col];
                            if (headerText.Length > 12) headerText = headerText.Substring(0, 10) + "..";

                            // Use full cell height and center text vertically
                            var rect = new System.Drawing.RectangleF(tableX + col * colWidth + 3, currentY, colWidth - 6, headerHeight);
                            var format = new System.Drawing.StringFormat
                            {
                                Trimming = System.Drawing.StringTrimming.EllipsisCharacter,
                                LineAlignment = System.Drawing.StringAlignment.Center  // Vertical center
                            };
                            graphics.DrawString(headerText, headerFont, whiteBrush, rect, format);

                            // Column separator
                            if (col > 0)
                                graphics.DrawLine(gridPen, tableX + col * colWidth, currentY, tableX + col * colWidth, currentY + headerHeight);
                        }

                        // Header border
                        graphics.DrawRectangle(gridPen, tableX, currentY, visibleCols * colWidth, headerHeight);
                    }

                    currentY += headerHeight;

                    // Draw data rows
                    using (var bodyFont = new System.Drawing.Font("Segoe UI", 8))
                    using (var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(50, 50, 50)))
                    using (var altBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(248, 248, 248)))
                    using (var gridPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 200, 200), 1))
                    {
                        int maxVisibleRows = Math.Min(rows.Count, 12);
                        for (int row = 0; row < maxVisibleRows; row++)
                        {
                            // Alternating row color
                            if (row % 2 == 1)
                                graphics.FillRectangle(altBrush, tableX, currentY, visibleCols * colWidth, rowHeight);

                            // Row data
                            for (int col = 0; col < visibleCols && col < rows[row].Count; col++)
                            {
                                var cellText = rows[row][col];
                                if (cellText.Length > 15) cellText = cellText.Substring(0, 13) + "..";

                                // Use full cell height and center text vertically
                                var rect = new System.Drawing.RectangleF(tableX + col * colWidth + 3, currentY, colWidth - 6, rowHeight);
                                var format = new System.Drawing.StringFormat
                                {
                                    Trimming = System.Drawing.StringTrimming.EllipsisCharacter,
                                    LineAlignment = System.Drawing.StringAlignment.Center  // Vertical center
                                };
                                graphics.DrawString(cellText, bodyFont, textBrush, rect, format);

                                // Column separator
                                if (col > 0)
                                    graphics.DrawLine(gridPen, tableX + col * colWidth, currentY, tableX + col * colWidth, currentY + rowHeight);
                            }

                            // Row border
                            graphics.DrawLine(gridPen, tableX, currentY + rowHeight, tableX + visibleCols * colWidth, currentY + rowHeight);

                            currentY += rowHeight;
                        }

                        // Table outline
                        graphics.DrawRectangle(gridPen, tableX, marginY + titleHeight, visibleCols * colWidth, currentY - marginY - titleHeight);
                    }

                    // Draw summary at bottom
                    using (var summaryFont = new System.Drawing.Font("Segoe UI", 9))
                    using (var summaryBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(100, 100, 100)))
                    {
                        string summary = $"📊 {totalRows} rows × {numCols} columns";
                        if (totalRows > 12)
                            summary += $" (showing first 12)";
                        if (numCols > 6)
                            summary += $" | {numCols - 6} more cols";

                        graphics.DrawString(summary, summaryFont, summaryBrush, marginX, imgHeight - 25);
                    }

                    bitmap.Save(cachePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return cachePath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderScheduleCsvAsImage failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extract thumbnail from RFA file using Windows Shell API
        /// </summary>
        private BitmapSource ExtractFamilyThumbnail(string rfaPath)
        {
            try
            {
                // Method 1: Try Windows Shell thumbnail extraction (best quality)
                var thumbnail = GetShellThumbnail(rfaPath, 256);
                if (thumbnail != null)
                    return thumbnail;

                // Method 2: Try to open in Revit and get preview (slower but reliable)
                if (_uiApp != null)
                {
                    var preview = GenerateFamilyPreviewViaRevit(rfaPath);
                    if (preview != null)
                        return preview;
                }

                // Method 3: Fallback to file icon
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(rfaPath);
                if (icon != null)
                {
                    using (var bitmap = icon.ToBitmap())
                    {
                        var hBitmap = bitmap.GetHbitmap();
                        try
                        {
                            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap,
                                IntPtr.Zero,
                                System.Windows.Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                            return source;
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractFamilyThumbnail failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get thumbnail using Windows Shell API - works for RFA files that have embedded previews
        /// </summary>
        private BitmapSource GetShellThumbnail(string filePath, int size)
        {
            try
            {
                IntPtr hBitmap = IntPtr.Zero;
                Guid iidImageFactory = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");

                int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iidImageFactory, out IShellItemImageFactory factory);
                if (hr != 0 || factory == null)
                    return null;

                SIZE sz = new SIZE { cx = size, cy = size };
                hr = factory.GetImage(sz, SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_RESIZETOFIT, out hBitmap);

                Marshal.ReleaseComObject(factory);

                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return null;

                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Generate family preview by loading it in Revit
        /// </summary>
        private BitmapSource GenerateFamilyPreviewViaRevit(string rfaPath)
        {
            Document familyDoc = null;
            try
            {
                // Open family document
                familyDoc = _uiApp.Application.OpenDocumentFile(rfaPath);
                if (familyDoc == null)
                    return null;

                // Find a view to export (family documents have default views)
                var view = new FilteredElementCollector(familyDoc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.CanBePrinted);

                if (view == null)
                {
                    familyDoc.Close(false);
                    return null;
                }

                // Export to image
                var cachePath = RvtPreviewGenerator.GetCachePath(rfaPath);
                var exportOptions = new ImageExportOptions
                {
                    FilePath = cachePath.Replace(".png", ""),
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = 400,
                    ExportRange = ExportRange.SetOfViews
                };
                exportOptions.SetViewsAndSheets(new List<ElementId> { view.Id });

                familyDoc.ExportImage(exportOptions);
                familyDoc.Close(false);

                // Find exported file
                var dir = Path.GetDirectoryName(cachePath);
                var baseName = Path.GetFileNameWithoutExtension(cachePath);
                var files = Directory.GetFiles(dir, $"{baseName}*.png");
                if (files.Length == 0)
                    files = Directory.GetFiles(dir, $"{baseName}*.PNG");

                if (files.Length > 0)
                {
                    var exportedFile = files[0];
                    if (exportedFile != cachePath && File.Exists(exportedFile))
                    {
                        if (File.Exists(cachePath)) File.Delete(cachePath);
                        File.Move(exportedFile, cachePath);
                    }

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(cachePath);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GenerateFamilyPreviewViaRevit failed: {ex.Message}");
                if (familyDoc != null)
                {
                    try { familyDoc.Close(false); } catch { }
                }
            }

            return null;
        }

        // Shell API for thumbnail extraction
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [ComImport]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private void GeneratePreviewForSelected()
        {
            if (_selectedDetail == null)
            {
                MessageBox.Show("Please select a detail first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_uiApp == null)
            {
                MessageBox.Show("Revit connection not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Show loading state
            PreviewLoadingText.Text = "⏳ Generating preview...\n\nOpening file in Revit...";
            PreviewLoadingText.Visibility = System.Windows.Visibility.Visible;
            PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
            PreviewImage.Source = null;
            StatusText.Text = $"Generating preview for: {_selectedDetail.Name}...";

            // Force UI update
            this.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            try
            {
                // Generate the preview using Revit API
                var cachePath = RvtPreviewGenerator.GeneratePreview(_uiApp.Application, _selectedDetail.FullPath);

                if (cachePath != null && File.Exists(cachePath))
                {
                    // Load and display the generated preview
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(cachePath);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    PreviewImage.Source = bitmap;
                    PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                    PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
                    StatusText.Text = $"Preview generated: {_selectedDetail.Name}";
                }
                else
                {
                    PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                    PreviewPlaceholder.Text = "📄\n\nPreview generation failed\n\nFile may not contain\na valid drafting view";
                    PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
                    StatusText.Text = $"Failed to generate preview for: {_selectedDetail.Name}";
                }
            }
            catch (Exception ex)
            {
                PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
                PreviewPlaceholder.Text = $"❌\n\nError generating preview:\n\n{ex.Message}";
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void GenerateAllPreviewsForCategory()
        {
            if (_details.Count == 0)
            {
                MessageBox.Show("No details in current category.", "No Details", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_uiApp == null)
            {
                MessageBox.Show("Revit connection not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show(
                $"This will generate previews for {_details.Count} details in '{_currentCategory}'.\n\n" +
                "This process opens each file in Revit and may take several minutes.\n\n" +
                "Continue?",
                "Generate All Previews",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            int generated = 0;
            int failed = 0;
            int skipped = 0;

            foreach (var detail in _details)
            {
                // Skip if already cached
                if (RvtPreviewGenerator.HasCachedPreview(detail.FullPath))
                {
                    skipped++;
                    continue;
                }

                StatusText.Text = $"Generating {generated + failed + 1} of {_details.Count}: {detail.Name}...";
                this.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                try
                {
                    var cachePath = RvtPreviewGenerator.GeneratePreview(_uiApp.Application, detail.FullPath);
                    if (cachePath != null)
                        generated++;
                    else
                        failed++;
                }
                catch
                {
                    failed++;
                }
            }

            MessageBox.Show(
                $"Preview generation complete:\n\n" +
                $"✅ Generated: {generated}\n" +
                $"⏭ Skipped (cached): {skipped}\n" +
                $"❌ Failed: {failed}",
                "Generation Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            StatusText.Text = $"Generated {generated} previews, {skipped} skipped, {failed} failed";

            // Refresh current selection preview
            if (_selectedDetail != null)
                UpdatePreview(_selectedDetail);
        }

        private void CleanEntireLibrary()
        {
            // Only works for detail RVT files (not families)
            if (_currentContentType != LibraryContentType.Details)
            {
                MessageBox.Show("Clean Library only works for Details (RVT files with drafting views).\n\nSwitch to Details mode to use this feature.",
                    "Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_uiApp == null)
            {
                MessageBox.Show("Revit connection not available. Open Revit first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get all RVT files from all categories
            var allFiles = new List<string>();
            var categoryFolders = new List<string>();

            try
            {
                var directories = Directory.GetDirectories(CurrentLibraryPath);
                foreach (var dir in directories)
                {
                    var rvtFiles = Directory.GetFiles(dir, "*.rvt", SearchOption.TopDirectoryOnly);
                    allFiles.AddRange(rvtFiles);
                    if (rvtFiles.Length > 0)
                        categoryFolders.Add(Path.GetFileName(dir));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading library: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (allFiles.Count == 0)
            {
                MessageBox.Show("No RVT files found in library.", "Empty Library", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"CLEAN ENTIRE LIBRARY\n\n" +
                $"Categories: {categoryFolders.Count}\n" +
                $"Total files: {allFiles.Count}\n\n" +
                "This will:\n" +
                "- Open each RVT file in Revit\n" +
                "- Check if drafting views have content\n" +
                "- DELETE files with NO content\n\n" +
                "This may take several minutes.\n\n" +
                "DELETE ALL EMPTY FILES?",
                "Clean Library",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            int scanned = 0;
            int deleted = 0;
            int kept = 0;
            int errors = 0;
            var deletedFiles = new List<string>();

            foreach (var filePath in allFiles)
            {
                scanned++;
                var fileName = Path.GetFileName(filePath);
                var categoryName = Path.GetFileName(Path.GetDirectoryName(filePath));
                StatusText.Text = $"Scanning {scanned}/{allFiles.Count}: {categoryName}/{fileName}...";
                this.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                Document detailDoc = null;
                try
                {
                    // Open the file
                    detailDoc = _uiApp.Application.OpenDocumentFile(filePath);
                    if (detailDoc == null)
                    {
                        errors++;
                        continue;
                    }

                    // Find drafting views
                    var draftingViews = new FilteredElementCollector(detailDoc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .Where(v => !v.IsTemplate)
                        .ToList();

                    // Check if ANY view has elements
                    bool hasContent = false;
                    foreach (var view in draftingViews)
                    {
                        var elementCount = new FilteredElementCollector(detailDoc, view.Id)
                            .WhereElementIsNotElementType()
                            .Where(e => e.Category != null &&
                                   e.Category.Id.Value != (int)BuiltInCategory.OST_Views)
                            .Count();

                        if (elementCount > 0)
                        {
                            hasContent = true;
                            break;
                        }
                    }

                    detailDoc.Close(false);
                    detailDoc = null;

                    if (!hasContent)
                    {
                        // DELETE the empty file
                        try
                        {
                            // Delete cached preview too
                            var cachePath = RvtPreviewGenerator.GetCachePath(filePath);
                            if (File.Exists(cachePath)) File.Delete(cachePath);

                            File.Delete(filePath);
                            deleted++;
                            deletedFiles.Add($"{categoryName}/{fileName}");
                        }
                        catch
                        {
                            errors++;
                        }
                    }
                    else
                    {
                        kept++;
                    }
                }
                catch
                {
                    errors++;
                    if (detailDoc != null)
                    {
                        try { detailDoc.Close(false); } catch { }
                    }
                }
            }

            // Refresh the UI
            RefreshLibrary();
            if (!string.IsNullOrEmpty(_currentCategory))
            {
                var categoryPath = Path.Combine(CurrentLibraryPath, _currentCategory);
                if (Directory.Exists(categoryPath))
                    LoadDetailsForCategory(categoryPath);
            }

            // Show results
            var summary = $"LIBRARY CLEANED\n\n" +
                         $"Scanned: {scanned} files\n" +
                         $"Kept (has content): {kept}\n" +
                         $"DELETED (empty): {deleted}\n" +
                         $"Errors: {errors}";

            if (deleted > 0 && deletedFiles.Count <= 20)
            {
                summary += "\n\nDeleted files:\n" + string.Join("\n", deletedFiles.Take(20));
                if (deletedFiles.Count > 20)
                    summary += $"\n...and {deletedFiles.Count - 20} more";
            }

            MessageBox.Show(summary, "Clean Complete", MessageBoxButton.OK,
                deleted > 0 ? MessageBoxImage.Information : MessageBoxImage.Information);

            StatusText.Text = $"Library cleaned: {deleted} empty files deleted, {kept} files kept";
        }

        private void ScanAndCleanCategory_OLD()
        {
            // OLD METHOD - replaced by CleanEntireLibrary
            if (_details.Count == 0)
            {
                MessageBox.Show("No details in current category. Select a category first.", "No Details", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_uiApp == null)
            {
                MessageBox.Show("Revit connection not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show(
                $"COMPREHENSIVE SCAN: '{_currentCategory}'\n\n" +
                $"This will scan all {_details.Count} detail files to:\n\n" +
                "1️⃣ Generate previews for all files\n" +
                "2️⃣ Identify EMPTY details (no content)\n" +
                "3️⃣ Find DUPLICATES (identical previews)\n\n" +
                "This opens each file in Revit - may take several minutes.\n\n" +
                "Continue?",
                "Scan & Clean",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            var emptyDetails = new List<DetailFile>();
            var previewHashes = new Dictionary<string, List<DetailFile>>();  // hash -> files with same preview
            int scanned = 0;
            int previewsGenerated = 0;

            foreach (var detail in _details.ToList())
            {
                scanned++;
                StatusText.Text = $"Scanning {scanned}/{_details.Count}: {detail.Name}...";
                this.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                try
                {
                    // Check if empty AND generate preview
                    var scanResult = ScanDetailFile(detail.FullPath);

                    if (scanResult.IsEmpty)
                    {
                        emptyDetails.Add(detail);
                    }
                    else if (!string.IsNullOrEmpty(scanResult.PreviewHash))
                    {
                        // Track for duplicate detection
                        if (!previewHashes.ContainsKey(scanResult.PreviewHash))
                            previewHashes[scanResult.PreviewHash] = new List<DetailFile>();
                        previewHashes[scanResult.PreviewHash].Add(detail);

                        if (scanResult.PreviewGenerated)
                            previewsGenerated++;
                    }
                }
                catch
                {
                    // Skip files that can't be opened
                }
            }

            // Find duplicates (same preview hash)
            var duplicateGroups = previewHashes.Where(p => p.Value.Count > 1).ToList();
            var duplicateCount = duplicateGroups.Sum(g => g.Value.Count - 1);  // All but one in each group

            // Build summary report
            var summary = $"SCAN COMPLETE: '{_currentCategory}'\n\n" +
                         $"📊 Scanned: {scanned} files\n" +
                         $"🖼 Previews generated: {previewsGenerated}\n" +
                         $"❌ Empty files: {emptyDetails.Count}\n" +
                         $"🔄 Duplicate groups: {duplicateGroups.Count} ({duplicateCount} duplicates)\n";

            if (emptyDetails.Count == 0 && duplicateGroups.Count == 0)
            {
                MessageBox.Show(summary + "\n✅ No issues found! Library is clean.", "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = $"Scan complete - library is clean";
                return;
            }

            // Show options
            var actionResult = MessageBox.Show(
                summary + "\n" +
                "What would you like to do?\n\n" +
                "YES = Delete empty files & oldest duplicates\n" +
                "NO = Mark them for review (check boxes)\n" +
                "CANCEL = Do nothing",
                "Issues Found",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (actionResult == MessageBoxResult.Yes)
            {
                int deleted = 0;
                int failed = 0;

                // Delete empty files
                foreach (var detail in emptyDetails)
                {
                    try
                    {
                        // Delete cached preview too
                        var cachePath = RvtPreviewGenerator.GetCachePath(detail.FullPath);
                        if (File.Exists(cachePath)) File.Delete(cachePath);

                        File.Delete(detail.FullPath);
                        _details.Remove(detail);
                        deleted++;
                    }
                    catch { failed++; }
                }

                // Delete duplicates (keep the first/oldest, delete the rest)
                foreach (var group in duplicateGroups)
                {
                    // Sort by modified date, keep the oldest
                    var sortedGroup = group.Value.OrderBy(d => d.Modified).ToList();
                    var toDelete = sortedGroup.Skip(1).ToList();  // All but first

                    foreach (var detail in toDelete)
                    {
                        try
                        {
                            var cachePath = RvtPreviewGenerator.GetCachePath(detail.FullPath);
                            if (File.Exists(cachePath)) File.Delete(cachePath);

                            File.Delete(detail.FullPath);
                            _details.Remove(detail);
                            deleted++;
                        }
                        catch { failed++; }
                    }
                }

                DetailList.Items.Refresh();
                UpdateCheckedCount();

                MessageBox.Show(
                    $"Cleanup complete:\n\n✅ Deleted: {deleted} files\n❌ Failed: {failed} files",
                    "Cleanup Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                StatusText.Text = $"Deleted {deleted} files ({emptyDetails.Count} empty, {duplicateCount} duplicates)";
            }
            else if (actionResult == MessageBoxResult.No)
            {
                // Mark empty ones
                foreach (var detail in emptyDetails)
                {
                    detail.IsChecked = true;
                }

                // Mark duplicates (all but first in each group)
                foreach (var group in duplicateGroups)
                {
                    var sortedGroup = group.Value.OrderBy(d => d.Modified).ToList();
                    foreach (var detail in sortedGroup.Skip(1))
                    {
                        detail.IsChecked = true;
                    }
                }

                DetailList.Items.Refresh();
                UpdateCheckedCount();

                StatusText.Text = $"Marked {emptyDetails.Count + duplicateCount} files for review";
            }
            else
            {
                StatusText.Text = $"Found {emptyDetails.Count} empty, {duplicateCount} duplicates - no action taken";
            }
        }

        private class ScanResult
        {
            public bool IsEmpty { get; set; }
            public string PreviewHash { get; set; }
            public bool PreviewGenerated { get; set; }
        }

        private ScanResult ScanDetailFile(string rvtFilePath)
        {
            var result = new ScanResult { IsEmpty = false, PreviewHash = null, PreviewGenerated = false };
            Document detailDoc = null;

            try
            {
                detailDoc = _uiApp.Application.OpenDocumentFile(rvtFilePath);
                if (detailDoc == null)
                    return result;

                // Find drafting views
                var draftingViews = new FilteredElementCollector(detailDoc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                if (draftingViews.Count == 0)
                {
                    result.IsEmpty = true;
                    detailDoc.Close(false);
                    return result;
                }

                // Check if any view has elements
                int totalElements = 0;
                ViewDrafting viewToExport = null;

                foreach (var view in draftingViews)
                {
                    var elementsInView = new FilteredElementCollector(detailDoc, view.Id)
                        .WhereElementIsNotElementType()
                        .ToElementIds()
                        .Count;

                    totalElements += elementsInView;
                    if (elementsInView > 0 && viewToExport == null)
                        viewToExport = view;
                }

                if (totalElements == 0)
                {
                    result.IsEmpty = true;
                    detailDoc.Close(false);
                    return result;
                }

                // Generate preview if not cached
                var cachePath = RvtPreviewGenerator.GetCachePath(rvtFilePath);
                if (!File.Exists(cachePath) && viewToExport != null)
                {
                    try
                    {
                        var exportOptions = new ImageExportOptions
                        {
                            FilePath = cachePath.Replace(".png", ""),
                            FitDirection = FitDirectionType.Horizontal,
                            HLRandWFViewsFileType = ImageFileType.PNG,
                            ShadowViewsFileType = ImageFileType.PNG,
                            ImageResolution = ImageResolution.DPI_300,
                            ZoomType = ZoomFitType.FitToPage,
                            PixelSize = 1200,
                            ExportRange = ExportRange.SetOfViews
                        };
                        exportOptions.SetViewsAndSheets(new List<ElementId> { viewToExport.Id });
                        detailDoc.ExportImage(exportOptions);

                        // Find exported file
                        var exportedFile = FindExportedImage(cachePath.Replace(".png", ""));
                        if (exportedFile != null && exportedFile != cachePath)
                        {
                            if (File.Exists(cachePath)) File.Delete(cachePath);
                            File.Move(exportedFile, cachePath);
                        }

                        result.PreviewGenerated = true;
                    }
                    catch { }
                }

                detailDoc.Close(false);

                // Calculate preview hash for duplicate detection
                if (File.Exists(cachePath))
                {
                    result.PreviewHash = CalculateImageHash(cachePath);
                }

                return result;
            }
            catch
            {
                if (detailDoc != null)
                {
                    try { detailDoc.Close(false); } catch { }
                }
                return result;
            }
        }

        private static string FindExportedImage(string basePath)
        {
            var dir = Path.GetDirectoryName(basePath);
            var baseName = Path.GetFileName(basePath);

            var files = Directory.GetFiles(dir, $"{baseName}*.png");
            if (files.Length > 0) return files[0];

            files = Directory.GetFiles(dir, $"{baseName}*.PNG");
            if (files.Length > 0) return files[0];

            return null;
        }

        private string CalculateImageHash(string imagePath)
        {
            try
            {
                // Simple hash based on file size + first/last bytes
                // For more accurate duplicate detection, could use perceptual hash
                var fileInfo = new FileInfo(imagePath);
                var bytes = File.ReadAllBytes(imagePath);

                // Create a simple hash from size and sample bytes
                var hashInput = $"{fileInfo.Length}_{bytes[0]}_{bytes[bytes.Length / 2]}_{bytes[bytes.Length - 1]}";

                // MD5 hash for comparison
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput + Convert.ToBase64String(bytes.Take(1000).ToArray())));
                    return BitConverter.ToString(hashBytes).Replace("-", "");
                }
            }
            catch
            {
                return null;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = SearchBox.Text.ToLower().Trim();

            if (string.IsNullOrEmpty(searchText))
            {
                // Show all in current category
                DetailList.ItemsSource = _details;
            }
            else
            {
                // Filter current list
                var filtered = _details.Where(d => d.Name.ToLower().Contains(searchText)).ToList();
                DetailList.ItemsSource = filtered;
                StatusText.Text = $"{filtered.Count} matches for '{searchText}'";
            }
        }

        private void RefreshLibrary()
        {
            _isGlobalSearchMode = false;
            LoadCategories();
            _details.Clear();
            _selectedDetail = null;
            DetailInfoText.Text = "Select a detail to see information";
            PreviewImage.Source = null;
            PreviewLoadingText.Visibility = System.Windows.Visibility.Collapsed;
            PreviewPlaceholder.Text = "📄\n\nSelect a detail to preview\n\nPreview loads automatically";
            PreviewPlaceholder.Visibility = System.Windows.Visibility.Visible;
            UpdateCheckedCount();
        }

        /// <summary>
        /// Search across ALL categories and content types
        /// </summary>
        private void SearchAllCategories()
        {
            var searchText = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(searchText))
            {
                MessageBox.Show("Enter search text in the search box, then click Search All.",
                    "Enter Search Text", MessageBoxButton.OK, MessageBoxImage.Information);
                SearchBox.Focus();
                return;
            }

            _isGlobalSearchMode = true;
            _details.Clear();
            var allResults = new List<DetailFile>();
            int totalFiles = 0;
            int processedFiles = 0;

            // Count total files first for progress bar
            foreach (var contentType in Enum.GetValues(typeof(LibraryContentType)).Cast<LibraryContentType>())
            {
                var path = _settings.GetPath(contentType);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    var pattern = contentType == LibraryContentType.Families ? "*.rfa" : "*.rvt";
                    totalFiles += Directory.GetFiles(path, pattern, SearchOption.AllDirectories)
                        .Count(f => !IsBackupFile(f));
                }
            }

            ShowProgress($"Searching {totalFiles} files...", 0, totalFiles);

            // Search each content type
            foreach (var contentType in Enum.GetValues(typeof(LibraryContentType)).Cast<LibraryContentType>())
            {
                var path = _settings.GetPath(contentType);
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    continue;

                var pattern = contentType == LibraryContentType.Families ? "*.rfa" : "*.rvt";
                var typeLabel = contentType.ToString();
                var typeIcon = GetContentTypeIcon(contentType);

                try
                {
                    // Get all folders (categories)
                    var folders = Directory.GetDirectories(path);
                    foreach (var folder in folders)
                    {
                        var categoryName = Path.GetFileName(folder);
                        var files = GetFilesExcludingBackups(folder, pattern);

                        foreach (var file in files)
                        {
                            processedFiles++;
                            if (processedFiles % 50 == 0)
                            {
                                ShowProgress($"Searching... {processedFiles}/{totalFiles}", processedFiles, totalFiles);
                                // Allow UI to update
                                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                            }

                            var fileName = Path.GetFileNameWithoutExtension(file);
                            if (fileName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var info = new FileInfo(file);
                                allResults.Add(new DetailFile
                                {
                                    Name = fileName,
                                    FullPath = file,
                                    Size = info.Length,
                                    DateModified = info.LastWriteTime,
                                    Category = $"{typeIcon} {typeLabel} > {categoryName}"
                                });
                            }
                        }
                    }

                    // Also check root folder
                    var rootFiles = GetFilesExcludingBackups(path, pattern);
                    foreach (var file in rootFiles)
                    {
                        processedFiles++;
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var info = new FileInfo(file);
                            allResults.Add(new DetailFile
                            {
                                Name = fileName,
                                FullPath = file,
                                Size = info.Length,
                                DateModified = info.LastWriteTime,
                                Category = $"{typeIcon} {typeLabel} (root)"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error searching {typeLabel}: {ex.Message}");
                }
            }

            HideProgress();

            // Apply sorting
            allResults = ApplySorting(allResults);

            // Update UI
            foreach (var result in allResults)
            {
                _details.Add(result);
            }

            DetailList.ItemsSource = _details;
            StatusText.Text = $"Found {allResults.Count} matches for '{searchText}' across all libraries";

            // Note: CategoryTree.SelectedItem is read-only; we just clear the tracking variable
            _currentCategory = "[Global Search]";
        }

        private string GetContentTypeIcon(LibraryContentType type)
        {
            switch (type)
            {
                case LibraryContentType.Families: return "📦";
                case LibraryContentType.Legends: return "📋";
                case LibraryContentType.Schedules: return "📊";
                default: return "📐";
            }
        }

        private List<DetailFile> ApplySorting(List<DetailFile> items)
        {
            switch (_settings.SortBy)
            {
                case "Date Modified":
                    return _settings.SortAscending
                        ? items.OrderBy(d => d.DateModified).ToList()
                        : items.OrderByDescending(d => d.DateModified).ToList();
                case "Size":
                    return _settings.SortAscending
                        ? items.OrderBy(d => d.Size).ToList()
                        : items.OrderByDescending(d => d.Size).ToList();
                default: // Name
                    return _settings.SortAscending
                        ? items.OrderBy(d => d.Name).ToList()
                        : items.OrderByDescending(d => d.Name).ToList();
            }
        }

        private void OpenSettings()
        {
            var settingsWindow = new LibrarySettingsWindow(_settings, () =>
            {
                // Called when settings change - refresh library with new paths
                var profileName = _settings.ActiveProfile?.Name ?? "Default";
                Title = $"Content Library - {profileName}";
                RefreshLibrary();
            });
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        /// <summary>
        /// Show library statistics dialog
        /// </summary>
        private void ShowLibraryStatistics()
        {
            var dialog = new LibraryStatisticsDialog(_settings, ShowProgress);
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Show batch rename dialog for checked or selected files
        /// </summary>
        private void ShowBatchRenameDialog()
        {
            // Get files to rename - prefer checked, fall back to selected
            var filesToRename = _details.Where(d => d.IsChecked).ToList();
            if (filesToRename.Count == 0)
            {
                filesToRename = DetailList.SelectedItems.Cast<DetailFile>().ToList();
            }

            if (filesToRename.Count == 0)
            {
                MessageBox.Show("Please check or select files to rename.", "No Files Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new BatchRenameDialog(filesToRename);
            dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.DialogResult && dialog.RenameOperations.Count > 0)
            {
                // Execute the renames
                int successCount = 0;
                int failCount = 0;
                var errors = new List<string>();

                ShowProgress("Renaming files...", 0, dialog.RenameOperations.Count);

                for (int i = 0; i < dialog.RenameOperations.Count; i++)
                {
                    var (oldPath, newPath) = dialog.RenameOperations[i];
                    try
                    {
                        if (File.Exists(newPath))
                        {
                            errors.Add($"{Path.GetFileName(oldPath)}: Target file already exists");
                            failCount++;
                        }
                        else
                        {
                            File.Move(oldPath, newPath);
                            successCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(oldPath)}: {ex.Message}");
                        failCount++;
                    }

                    ShowProgress($"Renaming... {i + 1}/{dialog.RenameOperations.Count}", i + 1, dialog.RenameOperations.Count);
                }

                HideProgress();

                // Report results
                if (failCount == 0)
                {
                    MessageBox.Show($"Successfully renamed {successCount} files.", "Batch Rename Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var errorMsg = $"Renamed {successCount} files. {failCount} failed:\n\n" + string.Join("\n", errors.Take(10));
                    if (errors.Count > 10)
                        errorMsg += $"\n... and {errors.Count - 10} more errors";
                    MessageBox.Show(errorMsg, "Batch Rename Partial", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // Refresh to show new names
                if (!string.IsNullOrEmpty(_currentCategory))
                {
                    LoadDetailsForCategory(_currentCategory);
                }
            }
        }

        private void ImportCheckedDetails()
        {
            var checkedDetails = _details.Where(d => d.IsChecked).ToList();
            if (checkedDetails.Count == 0)
            {
                MessageBox.Show("No details are checked. Use the checkboxes to select details for import.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ImportDetails(checkedDetails);
        }

        private void ImportSelectedDetails()
        {
            var selected = DetailList.SelectedItems.Cast<DetailFile>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select one or more details to import.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ImportDetails(selected);
        }

        private void ImportDetails(List<DetailFile> details)
        {
            if (_doc == null)
            {
                MessageBox.Show("No active Revit document. Please open a document first.", "No Document", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Route based on content type
            switch (_currentContentType)
            {
                case LibraryContentType.Families:
                    ImportFamilies(details);
                    return;

                case LibraryContentType.Legends:
                    ImportLegendsOrSchedules(details, "Legend");
                    return;

                case LibraryContentType.Schedules:
                    ImportLegendsOrSchedules(details, "Schedule");
                    return;

                default:
                    // Details - use external event handler for drafting views
                    if (_importEvent == null || _importHandler == null)
                    {
                        MessageBox.Show("Import system not initialized.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    _importHandler.FilesToImport = details.Select(d => d.FullPath).ToList();
                    StatusText.Text = $"Importing {details.Count} detail(s)... please wait";
                    _importEvent.Raise();
                    return;
            }
        }

        /// <summary>
        /// Import Legend or Schedule RVT files into the current document
        /// Legends and Schedules are stored as views in RVT files
        /// </summary>
        private void ImportLegendsOrSchedules(List<DetailFile> files, string viewType)
        {
            StatusText.Text = $"Importing {files.Count} {viewType.ToLower()}(s)...";

            // Modeless window: OpenDocumentFile/Transaction must run inside
            // the ExternalEvent, not in this click-handler call chain
            RunInRevitContext(app => ImportLegendsOrSchedulesInContext(app, files, viewType));
        }

        private void ImportLegendsOrSchedulesInContext(UIApplication app, List<DetailFile> files, string viewType)
        {
            int imported = 0;
            int failed = 0;
            var errors = new List<string>();

            foreach (var file in files)
            {
                Document sourceDoc = null;
                try
                {
                    // Open the source RVT file
                    sourceDoc = app.Application.OpenDocumentFile(file.FullPath);
                    if (sourceDoc == null)
                    {
                        failed++;
                        errors.Add($"{file.Name}: Could not open file");
                        continue;
                    }

                    // Find the appropriate view type
                    IEnumerable<View> viewsToImport;
                    if (viewType == "Legend")
                    {
                        viewsToImport = new FilteredElementCollector(sourceDoc)
                            .OfClass(typeof(View))
                            .Cast<View>()
                            .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
                    }
                    else // Schedule
                    {
                        viewsToImport = new FilteredElementCollector(sourceDoc)
                            .OfClass(typeof(ViewSchedule))
                            .Cast<View>()
                            .Where(v => !v.IsTemplate);
                    }

                    var viewList = viewsToImport.ToList();
                    if (viewList.Count == 0)
                    {
                        failed++;
                        errors.Add($"{file.Name}: No {viewType.ToLower()} views found");
                        sourceDoc.Close(false);
                        continue;
                    }

                    // Copy each view to target document
                    using (var trans = new Transaction(_doc, $"Import {viewType}"))
                    {
                        trans.Start();

                        foreach (var view in viewList)
                        {
                            try
                            {
                                // Copy view and its elements
                                var elementIds = new List<ElementId> { view.Id };
                                var copyOptions = new CopyPasteOptions();
                                copyOptions.SetDuplicateTypeNamesHandler(new ImportDuplicateHandler());
                                ElementTransformUtils.CopyElements(sourceDoc, elementIds, _doc, Transform.Identity, copyOptions);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to copy view {view.Name}: {ex.Message}");
                            }
                        }

                        trans.CommitAndCheck();
                    }

                    imported++;
                    sourceDoc.Close(false);
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{file.Name}: {ex.Message}");
                    if (sourceDoc != null)
                    {
                        try { sourceDoc.Close(false); } catch { }
                    }
                }
            }

            // Show results (back on the UI dispatcher — we're on Revit's thread)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var message = $"{viewType} Import Complete\n\n" +
                             $"✅ Imported: {imported}\n" +
                             $"❌ Failed: {failed}";

                if (errors.Count > 0)
                {
                    message += "\n\nErrors:\n" + string.Join("\n", errors.Take(5));
                }

                MessageBox.Show(message, "Import Complete", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                StatusText.Text = $"Imported {imported} {viewType.ToLower()}(s), {failed} failed";
            }));
        }

        /// <summary>
        /// Queue an action to run in a valid Revit API context.
        /// </summary>
        private void RunInRevitContext(Action<UIApplication> action)
        {
            if (_actionHandler == null || _actionEvent == null)
            {
                MessageBox.Show("Import system not initialized.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _actionHandler.Enqueue(action);
            _actionEvent.Raise();
        }

        /// <summary>
        /// Import RFA family files into the current document
        /// </summary>
        private void ImportFamilies(List<DetailFile> families)
        {
            StatusText.Text = $"Loading {families.Count} families...";

            // Modeless window: LoadFamily must run inside the ExternalEvent
            RunInRevitContext(app => ImportFamiliesInContext(families));
        }

        private void ImportFamiliesInContext(List<DetailFile> families)
        {
            int loaded = 0;
            int skipped = 0;
            int failed = 0;
            var errors = new List<string>();
            var loadedNames = new List<string>();

            // LoadFamily requires an open transaction in the target document
            using (var trans = new Transaction(_doc, "Load Families"))
            {
                trans.Start();

                foreach (var family in families)
                {
                    try
                    {
                        // Check if family is already loaded
                        var existingFamily = new FilteredElementCollector(_doc)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .FirstOrDefault(f => f.Name == Path.GetFileNameWithoutExtension(family.Name));

                        if (existingFamily != null)
                        {
                            skipped++;
                            continue;
                        }

                        // Load the family
                        Family loadedFamily = null;
                        if (_doc.LoadFamily(family.FullPath, out loadedFamily))
                        {
                            loaded++;
                            loadedNames.Add(family.Name);
                        }
                        else
                        {
                            // Try with overwrite option
                            var options = new FamilyLoadOptions();
                            if (_doc.LoadFamily(family.FullPath, options, out loadedFamily))
                            {
                                loaded++;
                                loadedNames.Add(family.Name);
                            }
                            else
                            {
                                failed++;
                                errors.Add($"{family.Name}: Failed to load");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"{family.Name}: {ex.Message}");
                    }
                }

                trans.CommitAndCheck();
            }

            // Show results (back on the UI dispatcher — we're on Revit's thread)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var message = $"Family Import Complete\n\n" +
                             $"✅ Loaded: {loaded}\n" +
                             $"⏭ Skipped (already loaded): {skipped}\n" +
                             $"❌ Failed: {failed}";

                if (loaded > 0 && loaded <= 10)
                {
                    message += "\n\nLoaded:\n" + string.Join("\n", loadedNames);
                }

                if (errors.Count > 0)
                {
                    message += "\n\nErrors:\n" + string.Join("\n", errors.Take(5));
                }

                MessageBox.Show(message, "Import Complete", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                StatusText.Text = $"Loaded {loaded} families, {skipped} skipped, {failed} failed";
            }));
        }

        /// <summary>
        /// Family load options - overwrite existing
        /// </summary>
        private class FamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true; // Load the family
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true; // Load the shared family
            }
        }

        private void OnImportComplete(int imported, int total, List<string> errors)
        {
            // This callback runs after the external event completes
            // Use dispatcher to update UI from correct thread
            this.Dispatcher.Invoke(() =>
            {
                if (imported == total)
                {
                    MessageBox.Show($"Successfully imported {imported} detail(s).\n\nNew drafting view(s) created in your project.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (imported > 0)
                {
                    var errorList = string.Join("\n", errors.Take(5));
                    if (errors.Count > 5) errorList += $"\n... and {errors.Count - 5} more";
                    MessageBox.Show($"Imported {imported} of {total} details.\n\nFailed:\n{errorList}", "Partial Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    var errorList = string.Join("\n", errors.Take(5));
                    if (errors.Count > 5) errorList += $"\n... and {errors.Count - 5} more";
                    MessageBox.Show($"Import failed.\n\nErrors:\n{errorList}", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                StatusText.Text = $"Imported {imported} of {total} details";
            });
        }

        private bool ViewNameExists(Document doc, string name)
        {
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.Name == name);
            return views.Any();
        }

        private void OpenSelectedDetail()
        {
            if (_selectedDetail == null) return;

            try
            {
                _uiApp.OpenAndActivateDocument(_selectedDetail.FullPath);
                StatusText.Text = $"Opened: {_selectedDetail.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowInExplorer()
        {
            if (_selectedDetail == null) return;

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_selectedDetail.FullPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectAll(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _selectedDetail != null)
            {
                ImportSelectedDetails();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                RefreshLibrary();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                this.Close();
                e.Handled = true;
            }
        }

        #region Drag and Drop

        private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                bool hasValidFiles = files.Any(f =>
                    f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase));

                e.Effects = hasValidFiles ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            Window_DragEnter(sender, e);  // Same logic
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                return;

            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            var validFiles = files.Where(f =>
                f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToList();

            if (!validFiles.Any())
            {
                MessageBox.Show("No valid Revit files found.\n\nDrop RVT or RFA files to import them to your library.",
                    "Invalid Files", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Determine target folder based on current selection
            var targetFolder = GetDropTargetFolder();
            if (string.IsNullOrEmpty(targetFolder))
            {
                MessageBox.Show("No library folder configured for the current content type.\n\nPlease configure library paths in Settings first.",
                    "No Target Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If a category is selected, use that subfolder
            var categoryItem = CategoryTree.SelectedItem as TreeViewItem;
            if (categoryItem?.Tag is CategoryItem cat && !string.IsNullOrEmpty(cat.Path))
            {
                targetFolder = cat.Path;
            }

            // Confirm import
            var result = MessageBox.Show(
                $"Import {validFiles.Count} file(s) to:\n{targetFolder}\n\nContinue?",
                "Import Files", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Copy files
            int imported = 0;
            var errors = new List<string>();

            foreach (var file in validFiles)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Combine(targetFolder, fileName);

                    // Check for existing file
                    if (File.Exists(destPath))
                    {
                        var overwrite = MessageBox.Show(
                            $"File '{fileName}' already exists.\n\nOverwrite?",
                            "File Exists", MessageBoxButton.YesNo, MessageBoxImage.Question);

                        if (overwrite != MessageBoxResult.Yes)
                            continue;
                    }

                    File.Copy(file, destPath, true);
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            // Show result
            var message = $"Imported {imported} of {validFiles.Count} files.";
            if (errors.Any())
            {
                message += $"\n\nErrors:\n{string.Join("\n", errors.Take(5))}";
                if (errors.Count > 5)
                    message += $"\n...and {errors.Count - 5} more errors";
            }

            MessageBox.Show(message, "Import Complete", MessageBoxButton.OK,
                errors.Any() ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // Refresh list to show new files
            if (imported > 0)
            {
                RefreshLibrary();
                // Re-select the category we were in
                if (!string.IsNullOrEmpty(_currentCategory))
                {
                    LoadDetailsForCategory(_currentCategory);
                }
            }
        }

        private string GetDropTargetFolder()
        {
            var basePath = _settings.GetPath(_currentContentType);
            if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
                return null;
            return basePath;
        }

        #endregion

        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        #region Sorting

        private void SortSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_sortSelector.SelectedItem is ComboBoxItem item)
            {
                _settings.SortBy = item.Tag?.ToString() ?? "Name";
                _settings.Save();
                ApplySorting();
            }
        }

        private void SortDirection_Click(object sender, RoutedEventArgs e)
        {
            _settings.SortAscending = !_settings.SortAscending;
            _settings.Save();
            _sortDirectionBtn.Content = _settings.SortAscending ? "↑" : "↓";
            _sortDirectionBtn.ToolTip = _settings.SortAscending ? "Ascending (click to reverse)" : "Descending (click to reverse)";
            ApplySorting();
        }

        private void ApplySorting()
        {
            if (_details.Count == 0) return;

            IEnumerable<DetailFile> sorted;

            // Apply sort
            switch (_settings.SortBy)
            {
                case "Date Modified":
                    sorted = _settings.SortAscending
                        ? _details.OrderBy(d => d.Modified)
                        : _details.OrderByDescending(d => d.Modified);
                    break;
                case "Size":
                    sorted = _settings.SortAscending
                        ? _details.OrderBy(d => d.Size)
                        : _details.OrderByDescending(d => d.Size);
                    break;
                default: // Name
                    sorted = _settings.SortAscending
                        ? _details.OrderBy(d => d.Name)
                        : _details.OrderByDescending(d => d.Name);
                    break;
            }

            // Keep favorites at top if enabled
            if (_settings.ShowFavoritesFirst)
            {
                sorted = sorted.OrderByDescending(d => d.IsFavorite).ThenBy(d => d.Name);
            }

            var sortedList = sorted.ToList();
            _details.Clear();
            foreach (var d in sortedList)
                _details.Add(d);
        }

        #endregion

        #region Context Menu

        private System.Windows.Controls.ContextMenu CreateDetailContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 50)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                Foreground = Brushes.White
            };

            // Import Selected
            var importItem = new System.Windows.Controls.MenuItem { Header = "📥  Import Selected", Foreground = Brushes.White };
            importItem.Click += (s, e) => ImportSelectedDetails();
            menu.Items.Add(importItem);

            // Import Checked
            var importCheckedItem = new System.Windows.Controls.MenuItem { Header = "📦  Import All Checked", Foreground = Brushes.White };
            importCheckedItem.Click += (s, e) => ImportCheckedDetails();
            menu.Items.Add(importCheckedItem);

            menu.Items.Add(new Separator());

            // Toggle Favorite
            var favoriteItem = new System.Windows.Controls.MenuItem { Header = "⭐  Toggle Favorite", Foreground = Brushes.White };
            favoriteItem.Click += (s, e) => ToggleFavorite();
            menu.Items.Add(favoriteItem);

            menu.Items.Add(new Separator());

            // Check Selected
            var checkItem = new System.Windows.Controls.MenuItem { Header = "☑  Check Selected", Foreground = Brushes.White };
            checkItem.Click += (s, e) =>
            {
                foreach (DetailFile detail in DetailList.SelectedItems)
                    detail.IsChecked = true;
                UpdateCheckedCount();
            };
            menu.Items.Add(checkItem);

            // Uncheck Selected
            var uncheckItem = new System.Windows.Controls.MenuItem { Header = "☐  Uncheck Selected", Foreground = Brushes.White };
            uncheckItem.Click += (s, e) =>
            {
                foreach (DetailFile detail in DetailList.SelectedItems)
                    detail.IsChecked = false;
                UpdateCheckedCount();
            };
            menu.Items.Add(uncheckItem);

            menu.Items.Add(new Separator());

            // Open in Explorer
            var explorerItem = new System.Windows.Controls.MenuItem { Header = "📂  Show in Explorer", Foreground = Brushes.White };
            explorerItem.Click += (s, e) => ShowInExplorer();
            menu.Items.Add(explorerItem);

            // Copy Path
            var copyPathItem = new System.Windows.Controls.MenuItem { Header = "📋  Copy File Path", Foreground = Brushes.White };
            copyPathItem.Click += (s, e) => CopyFilePath();
            menu.Items.Add(copyPathItem);

            menu.Items.Add(new Separator());

            // Delete
            var deleteItem = new System.Windows.Controls.MenuItem { Header = "🗑  Delete from Library", Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 120, 120)) };
            deleteItem.Click += (s, e) => DeleteSelectedDetail();
            menu.Items.Add(deleteItem);

            return menu;
        }

        private void ToggleFavorite()
        {
            var selected = DetailList.SelectedItems.Cast<DetailFile>().ToList();
            foreach (var detail in selected)
            {
                if (_settings.IsFavorite(detail.FullPath))
                    _settings.RemoveFromFavorites(detail.FullPath);
                else
                    _settings.AddToFavorites(detail.FullPath);

                detail.IsFavorite = _settings.IsFavorite(detail.FullPath);
            }
            // Refresh the view to show favorite status
            DetailList.Items.Refresh();
        }

        private void CopyFilePath()
        {
            var selected = DetailList.SelectedItem as DetailFile;
            if (selected != null)
            {
                System.Windows.Clipboard.SetText(selected.FullPath);
                StatusText.Text = "Path copied to clipboard";
            }
        }

        #endregion

        #region Delete and Extract Methods

        private void DeleteSelectedDetail()
        {
            var selected = DetailList.SelectedItems.Cast<DetailFile>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("No details selected.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Delete {selected.Count} file(s) from library?\n\n" +
                string.Join("\n", selected.Take(5).Select(d => d.Name)) +
                (selected.Count > 5 ? $"\n...and {selected.Count - 5} more" : "") +
                "\n\nFiles can be restored using 'Undo Delete'.",
                "Delete from Library",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Ensure temp folder exists
            if (!Directory.Exists(DeletedItemsFolder))
                Directory.CreateDirectory(DeletedItemsFolder);

            int moved = 0;
            int failed = 0;
            var newDeletedItems = new List<DeletedItem>();

            foreach (var detail in selected)
            {
                try
                {
                    // Delete cached preview (no need to keep)
                    var cachePath = RvtPreviewGenerator.GetCachePath(detail.FullPath);
                    if (File.Exists(cachePath)) File.Delete(cachePath);

                    // Move to temp folder instead of permanent delete
                    var fileName = Path.GetFileName(detail.FullPath);
                    var tempPath = Path.Combine(DeletedItemsFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{fileName}");
                    File.Move(detail.FullPath, tempPath);

                    // Track for undo
                    newDeletedItems.Add(new DeletedItem
                    {
                        OriginalPath = detail.FullPath,
                        TempPath = tempPath,
                        FileName = fileName,
                        DeletedAt = DateTime.Now
                    });

                    _details.Remove(detail);
                    moved++;
                }
                catch
                {
                    failed++;
                }
            }

            // Add to front of undo list (most recent first)
            _deletedItems.InsertRange(0, newDeletedItems);

            // Limit undo history to 50 items
            while (_deletedItems.Count > 50)
            {
                // Permanently delete old items
                var oldItem = _deletedItems.Last();
                if (File.Exists(oldItem.TempPath))
                    File.Delete(oldItem.TempPath);
                _deletedItems.Remove(oldItem);
            }

            // Enable undo button
            _undoDeleteBtn.IsEnabled = _deletedItems.Count > 0;

            var message = $"Deleted {moved} file(s)." + (failed > 0 ? $" Failed: {failed}" : "");
            StatusText.Text = message + " | Click 'Undo Delete' to restore";
        }

        private void UndoDelete()
        {
            if (_deletedItems.Count == 0)
            {
                MessageBox.Show("Nothing to undo.", "Undo Delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Get the most recently deleted items (same timestamp)
            var mostRecent = _deletedItems.First();
            var sameSession = _deletedItems.Where(d =>
                Math.Abs((d.DeletedAt - mostRecent.DeletedAt).TotalSeconds) < 2).ToList();

            var result = MessageBox.Show(
                $"Restore {sameSession.Count} file(s)?\n\n" +
                string.Join("\n", sameSession.Take(5).Select(d => d.FileName)) +
                (sameSession.Count > 5 ? $"\n...and {sameSession.Count - 5} more" : ""),
                "Undo Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            int restored = 0;
            int failed = 0;

            foreach (var item in sameSession)
            {
                try
                {
                    if (File.Exists(item.TempPath))
                    {
                        // Ensure target directory exists
                        var dir = Path.GetDirectoryName(item.OriginalPath);
                        if (!Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.Move(item.TempPath, item.OriginalPath);
                        _deletedItems.Remove(item);
                        restored++;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            // Update undo button
            _undoDeleteBtn.IsEnabled = _deletedItems.Count > 0;

            // Refresh the list to show restored files
            if (!string.IsNullOrEmpty(_currentCategory))
            {
                var currentPath = _libraryPaths[_currentContentType];
                if (!string.IsNullOrEmpty(currentPath))
                {
                    var categoryPath = Path.Combine(currentPath, _currentCategory);
                    if (Directory.Exists(categoryPath))
                        LoadDetailsForCategory(categoryPath);
                }
            }

            var message = $"Restored {restored} file(s)." + (failed > 0 ? $" Failed: {failed}" : "");
            MessageBox.Show(message, "Undo Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = message;
        }

        private void ExtractFromCurrentProject()
        {
            if (_uiApp == null || _doc == null)
            {
                MessageBox.Show("No Revit project open.", "Extract", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get all drafting views from current project
            var draftingViews = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewDrafting))
                .Cast<ViewDrafting>()
                .Where(v => !v.IsTemplate)
                .OrderBy(v => v.Name)
                .ToList();

            if (draftingViews.Count == 0)
            {
                MessageBox.Show("No drafting views found in current project.", "Extract", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Show selection dialog
            var selectWindow = new Window
            {
                Title = $"Extract Details from {_doc.Title}",
                Width = 600,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(SketchPadColors.BgPrimary)
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            // Header
            var header = new TextBlock
            {
                Text = $"Select drafting views to extract ({draftingViews.Count} available)",
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(10)
            };
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // List of views
            var viewList = new ListBox
            {
                Background = new SolidColorBrush(SketchPadColors.BgSecondary),
                Foreground = Brushes.White,
                SelectionMode = SelectionMode.Multiple,
                Margin = new Thickness(10, 0, 10, 10)
            };
            foreach (var view in draftingViews)
            {
                viewList.Items.Add(new ListBoxItem
                {
                    Content = view.Name,
                    Tag = view.Id,
                    Foreground = Brushes.White
                });
            }
            Grid.SetRow(viewList, 1);
            mainGrid.Children.Add(viewList);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10)
            };

            var selectAllBtn = new Button { Content = "Select All", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(5) };
            selectAllBtn.Click += (s, e) => viewList.SelectAll();
            buttonPanel.Children.Add(selectAllBtn);

            var extractBtn = new Button
            {
                Content = "Extract Selected",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(5),
                Background = new SolidColorBrush(SketchPadColors.AccentGreen),
                Foreground = Brushes.White
            };

            extractBtn.Click += (s, e) =>
            {
                var selectedItems = viewList.SelectedItems.Cast<ListBoxItem>().ToList();
                if (selectedItems.Count == 0)
                {
                    MessageBox.Show("Select at least one view to extract.", "Extract", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                selectWindow.DialogResult = true;
                selectWindow.Close();

                // Snapshot the work list, then run the Revit side
                // (NewProjectDocument + Transactions) in the ExternalEvent —
                // this is a modeless window's click handler
                var extractTargets = selectedItems
                    .Select(item => new { ViewId = (ElementId)item.Tag, ViewName = item.Content.ToString() })
                    .ToList();

                RunInRevitContext(app =>
                {
                    int extracted = 0;
                    int failed = 0;

                    foreach (var target in extractTargets)
                    {
                        var category = GetCategoryForDetailName(target.ViewName);
                        // Always extract to Details library (since we're extracting drafting views)
                        var targetFolder = Path.Combine(_libraryPaths[LibraryContentType.Details], category);

                        if (!Directory.Exists(targetFolder))
                            Directory.CreateDirectory(targetFolder);

                        var targetPath = Path.Combine(targetFolder, SanitizeFileName(target.ViewName) + ".rvt");

                        try
                        {
                            ExtractViewToFile(_doc, target.ViewId, targetPath);
                            extracted++;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to extract {target.ViewName}: {ex.Message}");
                            failed++;
                        }
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshLibrary();
                        MessageBox.Show($"Extracted {extracted} details." + (failed > 0 ? $" Failed: {failed}" : ""),
                            "Extract Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }));
                });
            };

            buttonPanel.Children.Add(extractBtn);

            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(5) };
            cancelBtn.Click += (s, e) => selectWindow.Close();
            buttonPanel.Children.Add(cancelBtn);

            Grid.SetRow(buttonPanel, 2);
            mainGrid.Children.Add(buttonPanel);

            selectWindow.Content = mainGrid;
            selectWindow.ShowDialog();
        }

        private void ExtractViewToFile(Document sourceDoc, ElementId viewId, string targetPath)
        {
            var view = sourceDoc.GetElement(viewId) as ViewDrafting;
            if (view == null) return;

            // Get elements in the view
            var elementsInView = new FilteredElementCollector(sourceDoc, viewId)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.Id.Value != (int)BuiltInCategory.OST_Views)
                .Select(e => e.Id)
                .ToList();

            if (elementsInView.Count == 0) return;

            // Create a new document for this detail
            var newDoc = _uiApp.Application.NewProjectDocument(UnitSystem.Imperial);

            using (var trans = new Transaction(newDoc, "Create Detail"))
            {
                trans.Start();

                // Get drafting view type
                var viewFamilyTypes = new FilteredElementCollector(newDoc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => vft.ViewFamily == ViewFamily.Drafting)
                    .ToList();

                if (viewFamilyTypes.Count == 0)
                {
                    trans.RollBack();
                    newDoc.Close(false);
                    return;
                }

                // Create drafting view
                var newView = ViewDrafting.Create(newDoc, viewFamilyTypes[0].Id);
                newView.Name = view.Name;

                // Copy elements
                var copyOptions = new CopyPasteOptions();
                copyOptions.SetDuplicateTypeNamesHandler(new ImportDuplicateHandler());

                ElementTransformUtils.CopyElements(
                    view,
                    elementsInView,
                    newView,
                    Autodesk.Revit.DB.Transform.Identity,
                    copyOptions);

                trans.CommitAndCheck();
            }

            // Save as
            var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
            newDoc.SaveAs(targetPath, saveOptions);
            newDoc.Close(false);
        }

        private string GetCategoryForDetailName(string name)
        {
            var upperName = name.ToUpperInvariant();

            // Roof details
            if (upperName.Contains("ROOF") || upperName.Contains("EAVE") || upperName.Contains("PARAPET") ||
                upperName.Contains("RIDGE") || upperName.Contains("HIP") || upperName.Contains("FASCIA") ||
                upperName.Contains("SOFFIT") || upperName.Contains("GUTTER"))
                return "01 - Roof Details";

            // Cabinetry
            if (upperName.Contains("CABINET") || upperName.Contains("MILLWORK") || upperName.Contains("CASEWORK"))
                return "02 - Cabinetry Details";

            // Wall details
            if (upperName.Contains("WALL") || upperName.Contains("STUD") || upperName.Contains("CMU") ||
                upperName.Contains("MASONRY") || upperName.Contains("PARTITION") || upperName.Contains("FRAMING"))
                return "03 - Wall Details";

            // Floor details
            if (upperName.Contains("FLOOR") || upperName.Contains("SLAB") || upperName.Contains("FOUNDATION"))
                return "04 - Floor Details";

            // Door details
            if (upperName.Contains("DOOR") || upperName.Contains("FRAME") && upperName.Contains("JAMB"))
                return "05 - Door Details";

            // Window details
            if (upperName.Contains("WINDOW") || upperName.Contains("GLAZING") || upperName.Contains("SILL"))
                return "06 - Window Details";

            // Stair details
            if (upperName.Contains("STAIR") || upperName.Contains("RAILING") || upperName.Contains("HANDRAIL") ||
                upperName.Contains("GUARDRAIL") || upperName.Contains("TREAD") || upperName.Contains("RISER"))
                return "07 - Stair Details";

            // Ceiling details
            if (upperName.Contains("CEILING") || upperName.Contains("SOFFIT"))
                return "08 - Ceiling Details";

            // Sections
            if (upperName.Contains("SECTION") || upperName.Contains("BUILDING SECTION"))
                return "10 - Sections";

            // Elevations
            if (upperName.Contains("ELEVATION") || upperName.Contains("EXTERIOR") && !upperName.Contains("DETAIL"))
                return "11 - Elevations";

            // Bathroom details
            if (upperName.Contains("BATH") || upperName.Contains("SHOWER") || upperName.Contains("TOILET") ||
                upperName.Contains("LAVATORY") || upperName.Contains("TUB") || upperName.Contains("WATERPROOF"))
                return "12 - Bathroom Details";

            // MEP details
            if (upperName.Contains("MEP") || upperName.Contains("HVAC") || upperName.Contains("PLUMB") ||
                upperName.Contains("ELEC") || upperName.Contains("MECHANICAL") || upperName.Contains("DUCT") ||
                upperName.Contains("PIPE") || upperName.Contains("EXHAUST"))
                return "14 - MEP Details";

            // Typical details
            if (upperName.Contains("TYPICAL") || upperName.Contains("TYP"))
                return "15 - Typical Details";

            // Structural
            if (upperName.Contains("STRUCT") || upperName.Contains("BEAM") || upperName.Contains("COLUMN") ||
                upperName.Contains("FOOTING") || upperName.Contains("JOIST") || upperName.Contains("TRUSS"))
                return "16 - Structural Details";

            // Default to General
            return "99 - General Details";
        }

        private string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        }

        #endregion
    }

    // Helper classes
    public class CategoryItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int Count { get; set; }
    }

    public class DetailFile : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isFavorite;

        public string Name { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public DateTime DateModified { get => Modified; set => Modified = value; }
        public string Category { get; set; } = "";

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged(nameof(IsChecked));
                }
            }
        }

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged(nameof(IsFavorite));
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string DisplayName => IsFavorite ? $"⭐ {Name}" : Name;

        public string SizeText => Size < 1024 * 1024
            ? $"{Size / 1024.0:F0} KB"
            : $"{Size / (1024.0 * 1024.0):F1} MB";

        public string ModifiedText => Modified.ToString("MM/dd/yyyy HH:mm");

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Tracks a deleted item for undo functionality
    /// </summary>
    public class DeletedItem
    {
        public string OriginalPath { get; set; }
        public string TempPath { get; set; }
        public string FileName { get; set; }
        public DateTime DeletedAt { get; set; }
    }
}
