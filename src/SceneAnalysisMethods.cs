using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Scene analysis methods for AI rendering workflows.
    /// Extracts material data, element info, and generates prompts from Revit views
    /// so Flux Pro gets accurate descriptions instead of generic guesses.
    /// </summary>
    public static class SceneAnalysisMethods
    {
        #region Public Methods

        /// <summary>
        /// Collect all visible elements in the current 3D view, grouped by category with material data.
        /// </summary>
        [MCPMethod("getVisibleElements", Category = "SceneAnalysis", Description = "Collect all visible elements in the current 3D view grouped by category")]
        public static string GetVisibleElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                View view = GetTargetView(doc, uidoc, parameters);
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid view found" });
                }

                var collector = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType();

                var categories = new Dictionary<string, List<object>>();
                var materialCounts = new Dictionary<string, int>();
                int totalElements = 0;

                foreach (var element in collector)
                {
                    if (element.Category == null) continue;

                    var catName = element.Category.Name;
                    if (!categories.ContainsKey(catName))
                        categories[catName] = new List<object>();

                    var materialIds = element.GetMaterialIds(false);
                    var materials = new List<object>();

                    foreach (var matId in materialIds)
                    {
                        var mat = doc.GetElement(matId) as Material;
                        if (mat == null) continue;

                        var matName = mat.Name;
                        if (!materialCounts.ContainsKey(matName))
                            materialCounts[matName] = 0;
                        materialCounts[matName]++;

                        materials.Add(new
                        {
                            name = mat.Name,
                            color = new { r = mat.Color.Red, g = mat.Color.Green, b = mat.Color.Blue },
                            colorName = RgbToColorName(mat.Color.Red, mat.Color.Green, mat.Color.Blue),
                            materialClass = mat.MaterialClass ?? "",
                            transparency = mat.Transparency,
                            shininess = mat.Shininess
                        });
                    }

                    // Get family/type name
                    string typeName = "";
                    var typeId = element.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var type = doc.GetElement(typeId);
                        if (type != null) typeName = type.Name;
                    }

                    categories[catName].Add(new
                    {
                        elementId = element.Id.Value,
                        typeName = typeName,
                        materialCount = materials.Count,
                        materials = materials
                    });

                    totalElements++;
                }

                // Sort categories by element count
                var sortedCategories = categories
                    .OrderByDescending(c => c.Value.Count)
                    .ToDictionary(c => c.Key, c => new
                    {
                        count = c.Value.Count,
                        elements = c.Value.Take(50).ToList() // Cap per category to avoid huge responses
                    });

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewName = view.Name,
                        viewId = view.Id.Value,
                        totalElements = totalElements,
                        categoryCount = categories.Count,
                        categories = sortedCategories,
                        topMaterials = materialCounts
                            .OrderByDescending(m => m.Value)
                            .Take(20)
                            .ToDictionary(m => m.Key, m => m.Value)
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get material data for the current view - lighter version focused on prompt building.
        /// Returns dominant colors and a one-line material summary.
        /// </summary>
        [MCPMethod("getViewMaterials", Category = "SceneAnalysis", Description = "Get material data for the current view focused on prompt building")]
        public static string GetViewMaterials(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                View view = GetTargetView(doc, uidoc, parameters);
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid view found" });
                }

                var collector = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType();

                // Track materials by category
                var categoryMaterials = new Dictionary<string, Dictionary<string, MaterialInfo>>();
                var allColors = new Dictionary<string, ColorCount>();

                foreach (var element in collector)
                {
                    if (element.Category == null) continue;
                    var catName = element.Category.Name;

                    var materialIds = element.GetMaterialIds(false);
                    foreach (var matId in materialIds)
                    {
                        var mat = doc.GetElement(matId) as Material;
                        if (mat == null) continue;

                        // Track by category
                        if (!categoryMaterials.ContainsKey(catName))
                            categoryMaterials[catName] = new Dictionary<string, MaterialInfo>();

                        var catMats = categoryMaterials[catName];
                        if (!catMats.ContainsKey(mat.Name))
                        {
                            catMats[mat.Name] = new MaterialInfo
                            {
                                Name = mat.Name,
                                R = mat.Color.Red,
                                G = mat.Color.Green,
                                B = mat.Color.Blue,
                                MaterialClass = mat.MaterialClass ?? "",
                                Count = 0
                            };
                        }
                        catMats[mat.Name].Count++;

                        // Track dominant colors
                        var colorName = RgbToColorName(mat.Color.Red, mat.Color.Green, mat.Color.Blue);
                        if (!allColors.ContainsKey(colorName))
                            allColors[colorName] = new ColorCount { Name = colorName, Count = 0, R = mat.Color.Red, G = mat.Color.Green, B = mat.Color.Blue };
                        allColors[colorName].Count++;
                    }
                }

                // Build dominant colors (top 5)
                var dominantColors = allColors.Values
                    .OrderByDescending(c => c.Count)
                    .Take(5)
                    .Select(c => new { color = c.Name, count = c.Count, rgb = new { r = c.R, g = c.G, b = c.B } })
                    .ToList();

                // Build per-category material summary
                var materialsByCategory = new Dictionary<string, object>();
                foreach (var cat in categoryMaterials.OrderByDescending(c => c.Value.Values.Sum(m => m.Count)))
                {
                    var topMats = cat.Value.Values
                        .OrderByDescending(m => m.Count)
                        .Take(5)
                        .Select(m => new
                        {
                            name = m.Name,
                            colorName = RgbToColorName(m.R, m.G, m.B),
                            materialClass = m.MaterialClass,
                            count = m.Count
                        })
                        .ToList();

                    materialsByCategory[cat.Key] = topMats;
                }

                // Build one-line summary
                var summaryParts = new List<string>();
                foreach (var cat in new[] { "Walls", "Floors", "Ceilings", "Furniture", "Roofs" })
                {
                    if (categoryMaterials.ContainsKey(cat))
                    {
                        var topMat = categoryMaterials[cat].Values.OrderByDescending(m => m.Count).FirstOrDefault();
                        if (topMat != null)
                        {
                            var colorName = RgbToColorName(topMat.R, topMat.G, topMat.B);
                            summaryParts.Add($"{cat}: {colorName} {topMat.MaterialClass}".TrimEnd());
                        }
                    }
                }
                var materialSummary = string.Join(", ", summaryParts);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewName = view.Name,
                        viewId = view.Id.Value,
                        dominantColors = dominantColors,
                        materialsByCategory = materialsByCategory,
                        materialSummary = materialSummary
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Auto-generate a natural language scene description and suggested Flux prompt.
        /// Detects interior vs exterior, identifies room type, builds material descriptions.
        /// </summary>
        [MCPMethod("getSceneDescription", Category = "SceneAnalysis", Description = "Auto-generate a natural language scene description and suggested render prompt")]
        public static string GetSceneDescription(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                View view = GetTargetView(doc, uidoc, parameters);
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid view found" });
                }

                var collector = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType();

                // Count elements by category for scene detection
                var categoryCounts = new Dictionary<string, int>();
                var categoryMaterials = new Dictionary<string, Dictionary<string, MaterialInfo>>();
                string roomName = null;

                foreach (var element in collector)
                {
                    if (element.Category == null) continue;
                    var catName = element.Category.Name;

                    if (!categoryCounts.ContainsKey(catName))
                        categoryCounts[catName] = 0;
                    categoryCounts[catName]++;

                    // Capture room names
                    if (element is Room room && !string.IsNullOrEmpty(room.Name))
                    {
                        roomName = room.Name;
                    }

                    // Collect materials
                    var materialIds = element.GetMaterialIds(false);
                    foreach (var matId in materialIds)
                    {
                        var mat = doc.GetElement(matId) as Material;
                        if (mat == null) continue;

                        if (!categoryMaterials.ContainsKey(catName))
                            categoryMaterials[catName] = new Dictionary<string, MaterialInfo>();

                        var catMats = categoryMaterials[catName];
                        if (!catMats.ContainsKey(mat.Name))
                        {
                            catMats[mat.Name] = new MaterialInfo
                            {
                                Name = mat.Name,
                                R = mat.Color.Red,
                                G = mat.Color.Green,
                                B = mat.Color.Blue,
                                MaterialClass = mat.MaterialClass ?? "",
                                Count = 0
                            };
                        }
                        catMats[mat.Name].Count++;
                    }
                }

                // Detect interior vs exterior
                bool hasRooms = categoryCounts.ContainsKey("Rooms");
                bool hasCeilings = categoryCounts.ContainsKey("Ceilings");
                bool hasFurniture = categoryCounts.ContainsKey("Furniture");
                bool hasRoofs = categoryCounts.ContainsKey("Roofs");
                bool hasSite = categoryCounts.ContainsKey("Site") || categoryCounts.ContainsKey("Topography");
                bool hasLighting = categoryCounts.ContainsKey("Lighting Fixtures");

                bool isInterior = (hasRooms || hasCeilings) && hasFurniture;
                string sceneType = isInterior ? "interior" : "exterior";

                // Detect room type from room name
                string roomType = DetectRoomType(roomName);

                // Build material descriptions per category
                var descriptions = new List<string>();
                descriptions.Add(BuildCategoryDescription("Walls", categoryMaterials));
                descriptions.Add(BuildCategoryDescription("Floors", categoryMaterials));
                if (isInterior)
                {
                    descriptions.Add(BuildCategoryDescription("Ceilings", categoryMaterials));
                }
                if (hasLighting)
                {
                    int lightCount = categoryCounts.ContainsKey("Lighting Fixtures") ? categoryCounts["Lighting Fixtures"] : 0;
                    descriptions.Add($"{lightCount} lighting fixture(s)");
                }
                if (hasFurniture)
                {
                    int furnCount = categoryCounts["Furniture"];
                    descriptions.Add($"{furnCount} furniture piece(s)");
                }

                descriptions.RemoveAll(string.IsNullOrEmpty);
                var sceneDescription = string.Join(". ", descriptions);

                // Build suggested prompt
                string suggestedPrompt;
                if (isInterior)
                {
                    suggestedPrompt = BuildInteriorPrompt(roomType, categoryMaterials, categoryCounts);
                }
                else
                {
                    suggestedPrompt = BuildExteriorPrompt(categoryMaterials, categoryCounts);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewName = view.Name,
                        viewId = view.Id.Value,
                        sceneType = sceneType,
                        roomType = roomType ?? "unknown",
                        roomName = roomName ?? "",
                        sceneDescription = sceneDescription,
                        suggestedPrompt = suggestedPrompt,
                        categoryCounts = categoryCounts.OrderByDescending(c => c.Value)
                            .ToDictionary(c => c.Key, c => c.Value)
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Composite method: clean high-res export + scene analysis in one call.
        /// Temporarily sets Realistic display, hides annotations, exports image,
        /// then restores original settings.
        /// </summary>
        [MCPMethod("exportViewForRender", Category = "SceneAnalysis", Description = "Clean high-res export plus scene analysis in one call")]
        public static string ExportViewForRender(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                View view = GetTargetView(doc, uidoc, parameters);
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No valid view found" });
                }

                var width = parameters["width"]?.ToObject<int>() ?? 2048;

                // Save original view settings
                var originalStyle = view.DisplayStyle;
                var originalDetail = view.DetailLevel;

                // Categories to hide for clean render export
                var annotationCategoryIds = new List<ElementId>();
                var categoriesToHide = new[]
                {
                    BuiltInCategory.OST_Grids,
                    BuiltInCategory.OST_Levels,
                    BuiltInCategory.OST_ReferencePoints,
                    BuiltInCategory.OST_SectionBox,
                    BuiltInCategory.OST_GridHeads,
                    BuiltInCategory.OST_LevelHeads
                };

                foreach (var bic in categoriesToHide)
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat != null)
                    {
                        annotationCategoryIds.Add(cat.Id);
                    }
                }

                // Track which categories were already hidden
                var wasHidden = new Dictionary<ElementId, bool>();
                foreach (var catId in annotationCategoryIds)
                {
                    wasHidden[catId] = !view.GetCategoryHidden(catId) ? false : true;
                }

                // Apply render-ready settings in a transaction
                using (var trans = new Transaction(doc, "Prepare View for Render Export"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        view.DisplayStyle = DisplayStyle.Realistic;
                        view.DetailLevel = ViewDetailLevel.Fine;

                        // Hide annotation categories
                        foreach (var catId in annotationCategoryIds)
                        {
                            if (!wasHidden[catId] && view.CanCategoryBeHidden(catId))
                            {
                                view.SetCategoryHidden(catId, true);
                            }
                        }

                        trans.Commit();
                    }
                    catch
                    {
                        if (trans.HasStarted() && !trans.HasEnded())
                            trans.RollBack();
                        throw;
                    }
                }

                // Export the image using the same pattern as ViewportCaptureMethods
                var tempDir = Path.Combine(Path.GetTempPath(), "RevitMCPCaptures");
                Directory.CreateDirectory(tempDir);
                var outputPath = Path.Combine(tempDir, $"render_export_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                var options = new ImageExportOptions
                {
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = width,
                    ImageResolution = ImageResolution.DPI_150,
                    FitDirection = FitDirectionType.Horizontal,
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.ChangeExtension(outputPath, null),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ShouldCreateWebSite = false
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });

                doc.ExportImage(options);

                // Find the exported file (Revit may append view name)
                var actualPath = FindExportedImage(outputPath, view);

                // Restore original view settings
                using (var trans = new Transaction(doc, "Restore View Settings After Export"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        view.DisplayStyle = originalStyle;
                        view.DetailLevel = originalDetail;

                        // Unhide categories that were not originally hidden
                        foreach (var catId in annotationCategoryIds)
                        {
                            if (!wasHidden[catId] && view.CanCategoryBeHidden(catId))
                            {
                                view.SetCategoryHidden(catId, false);
                            }
                        }

                        trans.Commit();
                    }
                    catch
                    {
                        if (trans.HasStarted() && !trans.HasEnded())
                            trans.RollBack();
                        throw;
                    }
                }

                if (string.IsNullOrEmpty(actualPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Image export completed but file not found",
                        expectedPath = outputPath
                    });
                }

                // Get scene description (reuse our own method internally)
                var sceneResult = JObject.Parse(GetSceneDescription(uiApp, parameters));
                var sceneData = sceneResult["result"];

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        imagePath = actualPath,
                        imageWidth = width,
                        viewName = view.Name,
                        viewId = view.Id.Value,
                        sceneType = sceneData?["sceneType"]?.ToString() ?? "unknown",
                        roomType = sceneData?["roomType"]?.ToString() ?? "unknown",
                        sceneDescription = sceneData?["sceneDescription"]?.ToString() ?? "",
                        suggestedPrompt = sceneData?["suggestedPrompt"]?.ToString() ?? "",
                        categoryCounts = sceneData?["categoryCounts"]
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static View GetTargetView(Document doc, UIDocument uidoc, JObject parameters)
        {
            if (parameters != null && parameters["viewId"] != null)
            {
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                return doc.GetElement(viewId) as View;
            }
            return uidoc.ActiveView;
        }

        private static string FindExportedImage(string outputPath, View view)
        {
            if (File.Exists(outputPath)) return outputPath;

            var ext = Path.GetExtension(outputPath);
            var basePath = Path.ChangeExtension(outputPath, null);
            var possiblePaths = new[]
            {
                outputPath,
                $"{basePath}{ext}",
                $"{basePath} - {view.Name}{ext}",
                $"{basePath} - {view.ViewType} - {view.Name}{ext}"
            };

            return possiblePaths.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// Convert RGB to architectural color name using HSL conversion.
        /// </summary>
        public static string RgbToColorName(byte r, byte g, byte b)
        {
            // Convert to HSL
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;

            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double lum = (max + min) / 2.0;
            double sat = 0;
            double hue = 0;

            if (max != min)
            {
                double delta = max - min;
                sat = lum > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

                if (max == rd)
                    hue = ((gd - bd) / delta + (gd < bd ? 6 : 0)) * 60;
                else if (max == gd)
                    hue = ((bd - rd) / delta + 2) * 60;
                else
                    hue = ((rd - gd) / delta + 4) * 60;
            }

            // Near-black
            if (lum < 0.08) return "black";

            // Near-white
            if (lum > 0.92 && sat < 0.1) return "white";

            // Grays
            if (sat < 0.08)
            {
                if (lum > 0.8) return "light gray";
                if (lum > 0.5) return "medium gray";
                if (lum > 0.25) return "dark gray";
                return "charcoal";
            }

            // Warm whites / off-whites
            if (hue >= 30 && hue <= 60 && sat < 0.15 && lum > 0.8)
                return "warm white";

            // Wood tones
            if (hue >= 20 && hue <= 45 && sat > 0.3)
            {
                if (lum > 0.6) return "light wood";
                if (lum > 0.35) return "medium wood";
                return "dark wood";
            }

            // Named colors by hue range
            if (hue < 15 || hue >= 345) return lum > 0.5 ? "light red" : "red";
            if (hue < 30) return lum > 0.5 ? "coral" : "rust";
            if (hue < 45) return lum > 0.5 ? "peach" : "orange";
            if (hue < 65) return lum > 0.5 ? "gold" : "amber";
            if (hue < 80) return lum > 0.5 ? "yellow" : "dark yellow";
            if (hue < 160) return lum > 0.5 ? "light green" : "green";
            if (hue < 200) return lum > 0.5 ? "light teal" : "teal";
            if (hue < 250) return lum > 0.5 ? "light blue" : "blue";
            if (hue < 290) return lum > 0.5 ? "lavender" : "purple";
            if (hue < 345) return lum > 0.5 ? "pink" : "magenta";

            return "neutral";
        }

        private static string DetectRoomType(string roomName)
        {
            if (string.IsNullOrEmpty(roomName)) return null;

            var lower = roomName.ToLowerInvariant();

            if (lower.Contains("bath") || lower.Contains("restroom") || lower.Contains("toilet") || lower.Contains("wc"))
                return "bathroom";
            if (lower.Contains("kitchen") || lower.Contains("pantry"))
                return "kitchen";
            if (lower.Contains("living") || lower.Contains("great room") || lower.Contains("family"))
                return "living room";
            if (lower.Contains("bed") || lower.Contains("master") || lower.Contains("guest"))
                return "bedroom";
            if (lower.Contains("office") || lower.Contains("study") || lower.Contains("den"))
                return "office";
            if (lower.Contains("reception") || lower.Contains("lobby") || lower.Contains("waiting"))
                return "reception";
            if (lower.Contains("dining"))
                return "dining room";
            if (lower.Contains("conference") || lower.Contains("meeting"))
                return "conference room";
            if (lower.Contains("corridor") || lower.Contains("hall") || lower.Contains("foyer") || lower.Contains("entry"))
                return "hallway";
            if (lower.Contains("exam") || lower.Contains("treatment") || lower.Contains("procedure"))
                return "medical exam room";
            if (lower.Contains("retail") || lower.Contains("shop") || lower.Contains("store"))
                return "retail space";

            return "room";
        }

        private static string BuildCategoryDescription(string categoryName, Dictionary<string, Dictionary<string, MaterialInfo>> categoryMaterials)
        {
            if (!categoryMaterials.ContainsKey(categoryName)) return "";

            var topMat = categoryMaterials[categoryName].Values
                .OrderByDescending(m => m.Count)
                .FirstOrDefault();

            if (topMat == null) return "";

            var colorName = RgbToColorName(topMat.R, topMat.G, topMat.B);
            var matClass = !string.IsNullOrEmpty(topMat.MaterialClass) ? topMat.MaterialClass : "";

            return $"{categoryName}: {colorName} {matClass} ({topMat.Name})".TrimEnd();
        }

        private static string BuildInteriorPrompt(string roomType, Dictionary<string, Dictionary<string, MaterialInfo>> categoryMaterials, Dictionary<string, int> categoryCounts)
        {
            var parts = new List<string>();

            parts.Add("Ultra photorealistic interior photograph");

            // Room type
            if (!string.IsNullOrEmpty(roomType) && roomType != "room" && roomType != "unknown")
            {
                parts.Add(roomType);
            }

            // Wall description
            var wallDesc = GetMaterialDescription("Walls", categoryMaterials);
            if (!string.IsNullOrEmpty(wallDesc))
                parts.Add($"{wallDesc} walls");

            // Floor description
            var floorDesc = GetMaterialDescription("Floors", categoryMaterials);
            if (!string.IsNullOrEmpty(floorDesc))
                parts.Add($"{floorDesc} flooring");

            // Ceiling description
            var ceilDesc = GetMaterialDescription("Ceilings", categoryMaterials);
            if (!string.IsNullOrEmpty(ceilDesc))
                parts.Add($"{ceilDesc} ceiling");

            // Lighting
            if (categoryCounts.ContainsKey("Lighting Fixtures"))
            {
                int count = categoryCounts["Lighting Fixtures"];
                parts.Add(count > 3 ? "warm ambient lighting from multiple fixtures" : "soft architectural lighting");
            }

            // Furniture
            if (categoryCounts.ContainsKey("Furniture"))
            {
                parts.Add("furnished interior");
            }

            parts.Add("preserving exact room geometry and proportions");
            parts.Add("natural interior lighting");
            parts.Add("8K resolution");
            parts.Add("Architectural Digest quality");

            return string.Join(", ", parts);
        }

        private static string BuildExteriorPrompt(Dictionary<string, Dictionary<string, MaterialInfo>> categoryMaterials, Dictionary<string, int> categoryCounts)
        {
            var parts = new List<string>();

            parts.Add("Ultra photorealistic exterior architectural photography");
            parts.Add("exact same building design and structure as reference");

            // Wall/facade description
            var wallDesc = GetMaterialDescription("Walls", categoryMaterials);
            if (!string.IsNullOrEmpty(wallDesc))
                parts.Add($"{wallDesc} exterior facade");

            // Roof
            var roofDesc = GetMaterialDescription("Roofs", categoryMaterials);
            if (!string.IsNullOrEmpty(roofDesc))
                parts.Add($"{roofDesc} roof");

            parts.Add("preserving all windows, doors, roof lines, and proportions exactly");
            parts.Add("professional landscaping");
            parts.Add("clear blue sky");
            parts.Add("natural daylight");
            parts.Add("8K resolution");
            parts.Add("professional real estate photography");

            return string.Join(", ", parts);
        }

        private static string GetMaterialDescription(string categoryName, Dictionary<string, Dictionary<string, MaterialInfo>> categoryMaterials)
        {
            if (!categoryMaterials.ContainsKey(categoryName)) return "";

            var topMat = categoryMaterials[categoryName].Values
                .OrderByDescending(m => m.Count)
                .FirstOrDefault();

            if (topMat == null) return "";

            var colorName = RgbToColorName(topMat.R, topMat.G, topMat.B);
            var matClass = !string.IsNullOrEmpty(topMat.MaterialClass) ? topMat.MaterialClass.ToLowerInvariant() : "";

            if (!string.IsNullOrEmpty(matClass))
                return $"{colorName} {matClass}";
            return colorName;
        }

        #endregion

        #region Internal Types

        private class MaterialInfo
        {
            public string Name { get; set; }
            public byte R { get; set; }
            public byte G { get; set; }
            public byte B { get; set; }
            public string MaterialClass { get; set; }
            public int Count { get; set; }
        }

        private class ColorCount
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public byte R { get; set; }
            public byte G { get; set; }
            public byte B { get; set; }
        }

        #endregion
    }
}
