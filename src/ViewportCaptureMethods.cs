using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Viewport capture and camera control methods for MCP Bridge.
    /// Enables AI-assisted rendering workflows by capturing Revit views.
    /// </summary>
    public static class ViewportCaptureMethods
    {
        #region Viewport Capture Methods

        /// <summary>
        /// Capture the current view or a specified view to an image file.
        /// Uses Revit's ExportImage API for high-quality output.
        /// </summary>
        /// <param name="parameters">
        /// - viewId (optional): View ElementId to capture. Uses active view if not specified.
        /// - outputPath: Full path for output image file (PNG, JPG, BMP, TIFF supported).
        /// - width (optional): Image width in pixels. Default 1920.
        /// - height (optional): Image height in pixels. Default 1080.
        /// - quality (optional): Image quality 1-100 for JPG. Default 90.
        /// - fitToView (optional): If true, fits content to image. Default true.
        /// </param>
        [MCPMethod("captureViewport", Category = "ViewportCapture", Description = "Capture the current view or a specified view to an image file")]
        public static string CaptureViewport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                // Get view to capture
                View view;
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    view = doc.GetElement(viewId) as View;
                    if (view == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "View not found with specified ID"
                        });
                    }
                }
                else
                {
                    view = uidoc.ActiveView;
                }

                // Validate view can be exported
                if (!view.CanBePrinted)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View '{view.Name}' cannot be exported to image"
                    });
                }

                // Get output path
                var outputPath = parameters["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(outputPath))
                {
                    // Default to temp folder with timestamp
                    var tempDir = Path.Combine(Path.GetTempPath(), "RevitMCPCaptures");
                    Directory.CreateDirectory(tempDir);
                    outputPath = Path.Combine(tempDir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                }

                // Ensure directory exists
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Get image dimensions
                var width = parameters["width"]?.ToObject<int>() ?? 1920;
                var height = parameters["height"]?.ToObject<int>() ?? 1080;
                var quality = parameters["quality"]?.ToObject<int>() ?? 90;
                var fitToView = parameters["fitToView"]?.ToObject<bool>() ?? true;

                // Determine image format from extension
                var extension = Path.GetExtension(outputPath).ToLowerInvariant();
                ImageFileType fileType;
                switch (extension)
                {
                    case ".jpg":
                    case ".jpeg":
                        fileType = ImageFileType.JPEGLossless;
                        break;
                    case ".bmp":
                        fileType = ImageFileType.BMP;
                        break;
                    case ".tif":
                    case ".tiff":
                        fileType = ImageFileType.TIFF;
                        break;
                    case ".png":
                    default:
                        fileType = ImageFileType.PNG;
                        if (extension != ".png")
                        {
                            outputPath = Path.ChangeExtension(outputPath, ".png");
                        }
                        break;
                }

                // Create export options
                var options = new ImageExportOptions
                {
                    ZoomType = fitToView ? ZoomFitType.FitToPage : ZoomFitType.Zoom,
                    PixelSize = width,
                    ImageResolution = ImageResolution.DPI_150,
                    FitDirection = FitDirectionType.Horizontal,
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.ChangeExtension(outputPath, null), // Revit adds extension
                    HLRandWFViewsFileType = fileType,
                    ShadowViewsFileType = fileType,
                    ShouldCreateWebSite = false
                };

                // Set view to export
                options.SetViewsAndSheets(new List<ElementId> { view.Id });

                // Export the image
                doc.ExportImage(options);

                // Verify file was created (Revit adds extension)
                var actualPath = outputPath;
                if (!File.Exists(actualPath))
                {
                    // Revit appends " - ViewType - ViewName" to exported files
                    var basePath = Path.ChangeExtension(outputPath, null);
                    var viewTypeName = view.ViewType.ToString();
                    var possiblePaths = new[]
                    {
                        outputPath,
                        $"{basePath}{extension}",
                        $"{basePath} - {view.Name}{extension}",
                        $"{basePath} - {viewTypeName} - {view.Name}{extension}"  // Revit 2026 format
                    };

                    actualPath = possiblePaths.FirstOrDefault(File.Exists);
                }

                if (string.IsNullOrEmpty(actualPath) || !File.Exists(actualPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Image export completed but file not found at expected location",
                        expectedPath = outputPath
                    });
                }

                // Get file info
                var fileInfo = new FileInfo(actualPath);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        filePath = actualPath,
                        viewId = view.Id.Value,
                        viewName = view.Name,
                        viewType = view.ViewType.ToString(),
                        width = width,
                        height = height,
                        fileSize = fileInfo.Length,
                        format = fileType.ToString()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Capture viewport and return image as Base64 string (for direct transmission).
        /// </summary>
        [MCPMethod("captureViewportToBase64", Category = "ViewportCapture", Description = "Capture viewport and return image as Base64 string")]
        public static string CaptureViewportToBase64(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // First capture to temp file
                var tempPath = Path.Combine(Path.GetTempPath(), $"revit_capture_{Guid.NewGuid()}.png");
                parameters["outputPath"] = tempPath;

                var captureResult = CaptureViewport(uiApp, parameters);
                var result = JObject.Parse(captureResult);

                if (result["success"]?.ToObject<bool>() != true)
                {
                    return captureResult; // Return error as-is
                }

                var filePath = result["result"]["filePath"].ToString();

                // Read file and convert to Base64
                var imageBytes = File.ReadAllBytes(filePath);
                var base64 = Convert.ToBase64String(imageBytes);

                // Clean up temp file
                try { File.Delete(filePath); } catch { }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        base64 = base64,
                        mimeType = "image/png",
                        viewId = result["result"]["viewId"],
                        viewName = result["result"]["viewName"],
                        width = result["result"]["width"],
                        height = result["result"]["height"]
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Camera Control Methods (3D Views)

        /// <summary>
        /// Set camera position and orientation for a 3D view.
        /// </summary>
        /// <param name="parameters">
        /// - viewId: 3D View ElementId to modify.
        /// - eyePosition: Camera position {x, y, z} in feet.
        /// - targetPosition: Look-at point {x, y, z} in feet.
        /// - upDirection (optional): Up vector {x, y, z}. Default {0, 0, 1}.
        /// </param>
        [MCPMethod("setCamera", Category = "ViewportCapture", Description = "Set camera position and orientation for a 3D view")]
        public static string SetCamera(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get view
                if (parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var view3d = doc.GetElement(viewId) as View3D;

                if (view3d == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View is not a 3D view or not found"
                    });
                }

                if (view3d.IsLocked)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "3D view is locked and cannot be modified"
                    });
                }

                // Parse positions
                var eyePos = parameters["eyePosition"] as JObject;
                var targetPos = parameters["targetPosition"] as JObject;

                if (eyePos == null || targetPos == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "eyePosition and targetPosition are required"
                    });
                }

                var eye = new XYZ(
                    eyePos["x"]?.ToObject<double>() ?? 0,
                    eyePos["y"]?.ToObject<double>() ?? 0,
                    eyePos["z"]?.ToObject<double>() ?? 0
                );

                var target = new XYZ(
                    targetPos["x"]?.ToObject<double>() ?? 0,
                    targetPos["y"]?.ToObject<double>() ?? 0,
                    targetPos["z"]?.ToObject<double>() ?? 0
                );

                // Parse up direction (default to Z-up)
                XYZ up = XYZ.BasisZ;
                var upDir = parameters["upDirection"] as JObject;
                if (upDir != null)
                {
                    up = new XYZ(
                        upDir["x"]?.ToObject<double>() ?? 0,
                        upDir["y"]?.ToObject<double>() ?? 0,
                        upDir["z"]?.ToObject<double>() ?? 1
                    );
                }

                // Calculate forward direction
                var forward = (target - eye).Normalize();

                // Ensure up is perpendicular to forward
                var right = forward.CrossProduct(up).Normalize();
                up = right.CrossProduct(forward).Normalize();

                using (var trans = new Transaction(doc, "Set Camera"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create orientation
                    var orientation = new ViewOrientation3D(eye, up, forward);
                    view3d.SetOrientation(orientation);

                    // Disable section box if enabled (can interfere with camera)
                    if (view3d.IsSectionBoxActive)
                    {
                        // Keep it active but adjust if needed
                    }

                    trans.Commit();
                }

                // Return new camera state
                var newOrientation = view3d.GetOrientation();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = view3d.Id.Value,
                        viewName = view3d.Name,
                        eyePosition = new { x = newOrientation.EyePosition.X, y = newOrientation.EyePosition.Y, z = newOrientation.EyePosition.Z },
                        forwardDirection = new { x = newOrientation.ForwardDirection.X, y = newOrientation.ForwardDirection.Y, z = newOrientation.ForwardDirection.Z },
                        upDirection = new { x = newOrientation.UpDirection.X, y = newOrientation.UpDirection.Y, z = newOrientation.UpDirection.Z }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get current camera position and orientation for a 3D view.
        /// </summary>
        [MCPMethod("getCamera", Category = "ViewportCapture", Description = "Get current camera position and orientation for a 3D view")]
        public static string GetCamera(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                // Get view
                View3D view3d;
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    view3d = doc.GetElement(viewId) as View3D;
                }
                else
                {
                    view3d = uidoc.ActiveView as View3D;
                }

                if (view3d == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View is not a 3D view or not found"
                    });
                }

                var orientation = view3d.GetOrientation();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = view3d.Id.Value,
                        viewName = view3d.Name,
                        isPerspective = view3d.IsPerspective,
                        isLocked = view3d.IsLocked,
                        eyePosition = new
                        {
                            x = orientation.EyePosition.X,
                            y = orientation.EyePosition.Y,
                            z = orientation.EyePosition.Z
                        },
                        forwardDirection = new
                        {
                            x = orientation.ForwardDirection.X,
                            y = orientation.ForwardDirection.Y,
                            z = orientation.ForwardDirection.Z
                        },
                        upDirection = new
                        {
                            x = orientation.UpDirection.X,
                            y = orientation.UpDirection.Y,
                            z = orientation.UpDirection.Z
                        },
                        sectionBoxActive = view3d.IsSectionBoxActive
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Visual Style Methods

        /// <summary>
        /// Set the visual style of a view.
        /// </summary>
        /// <param name="parameters">
        /// - viewId (optional): View ElementId. Uses active view if not specified.
        /// - displayStyle: One of: Wireframe, HiddenLine, Shaded, ShadedWithEdges, Consistent, Realistic, Rendering, RaytracedRendering
        /// - detailLevel (optional): One of: Coarse, Medium, Fine
        /// </param>
        [MCPMethod("setViewStyle", Category = "ViewportCapture", Description = "Set the visual style of a view")]
        public static string SetViewStyle(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var uidoc = uiApp.ActiveUIDocument;

                // Get view
                View view;
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    view = doc.GetElement(viewId) as View;
                }
                else
                {
                    view = uidoc.ActiveView;
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set View Style"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Set display style
                    var styleStr = parameters["displayStyle"]?.ToString();
                    if (!string.IsNullOrEmpty(styleStr))
                    {
                        if (Enum.TryParse<DisplayStyle>(styleStr, true, out var displayStyle))
                        {
                            view.DisplayStyle = displayStyle;
                        }
                        else
                        {
                            trans.RollBack();
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Invalid displayStyle: {styleStr}. Valid values: Wireframe, HiddenLine, Shaded, ShadedWithEdges, Consistent, Realistic, Rendering, RaytracedRendering"
                            });
                        }
                    }

                    // Set detail level
                    var detailStr = parameters["detailLevel"]?.ToString();
                    if (!string.IsNullOrEmpty(detailStr))
                    {
                        if (Enum.TryParse<ViewDetailLevel>(detailStr, true, out var detailLevel))
                        {
                            view.DetailLevel = detailLevel;
                        }
                        else
                        {
                            trans.RollBack();
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Invalid detailLevel: {detailStr}. Valid values: Coarse, Medium, Fine"
                            });
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = view.Id.Value,
                        viewName = view.Name,
                        displayStyle = view.DisplayStyle.ToString(),
                        detailLevel = view.DetailLevel.ToString()
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available views in the model, optionally filtered by type.
        /// </summary>
        /// <param name="parameters">
        /// - viewType (optional): Filter by type: FloorPlan, CeilingPlan, Elevation, Section, ThreeD, Schedule, etc.
        /// - includeTemplates (optional): Include view templates. Default false.
        /// </param>
        [MCPMethod("listViews", Category = "ViewportCapture", Description = "List all views in the document")]
        public static string ListViews(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var includeTemplates = parameters["includeTemplates"]?.ToObject<bool>() ?? false;
                var viewTypeFilter = parameters["viewType"]?.ToString();

                // Get all views
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => includeTemplates || !v.IsTemplate)
                    .Where(v => v.CanBePrinted || v.ViewType == ViewType.ThreeD) // Include 3D views
                    .ToList();

                // Apply type filter
                if (!string.IsNullOrEmpty(viewTypeFilter))
                {
                    if (Enum.TryParse<ViewType>(viewTypeFilter, true, out var vt))
                    {
                        views = views.Where(v => v.ViewType == vt).ToList();
                    }
                }

                var viewList = views.Select(v => new
                {
                    viewId = v.Id.Value,
                    name = v.Name,
                    viewType = v.ViewType.ToString(),
                    isTemplate = v.IsTemplate,
                    canExport = v.CanBePrinted,
                    displayStyle = v.DisplayStyle.ToString(),
                    detailLevel = v.DetailLevel.ToString(),
                    scale = v.Scale,
                    is3D = v is View3D,
                    isPerspective = (v as View3D)?.IsPerspective ?? false
                }).OrderBy(v => v.viewType).ThenBy(v => v.name).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = viewList.Count,
                        views = viewList
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a new 3D view, optionally as perspective camera.
        /// </summary>
        /// <param name="parameters">
        /// - viewName: Name for the new view.
        /// - isPerspective (optional): If true, create perspective view. Default false (isometric).
        /// - eyePosition (optional for perspective): Camera position {x, y, z}.
        /// - targetPosition (optional for perspective): Look-at point {x, y, z}.
        /// </param>
        [MCPMethod("create3DView", Category = "ViewportCapture", Description = "Create a new 3D view")]
        public static string Create3DView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewName = parameters["viewName"]?.ToString() ?? $"3D View {DateTime.Now:HHmmss}";
                var isPerspective = parameters["isPerspective"]?.ToObject<bool>() ?? false;

                // Get 3D view family type
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

                if (viewFamilyType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "3D view family type not found"
                    });
                }

                View3D view3d;

                using (var trans = new Transaction(doc, "Create 3D View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (isPerspective)
                    {
                        // Create perspective view
                        var eyePos = parameters["eyePosition"] as JObject;
                        var targetPos = parameters["targetPosition"] as JObject;

                        XYZ eye, target, up;

                        if (eyePos != null && targetPos != null)
                        {
                            eye = new XYZ(
                                eyePos["x"]?.ToObject<double>() ?? 50,
                                eyePos["y"]?.ToObject<double>() ?? 50,
                                eyePos["z"]?.ToObject<double>() ?? 30
                            );
                            target = new XYZ(
                                targetPos["x"]?.ToObject<double>() ?? 0,
                                targetPos["y"]?.ToObject<double>() ?? 0,
                                targetPos["z"]?.ToObject<double>() ?? 0
                            );
                        }
                        else
                        {
                            // Default camera position
                            eye = new XYZ(50, 50, 30);
                            target = new XYZ(0, 0, 0);
                        }

                        up = XYZ.BasisZ;
                        var forward = (target - eye).Normalize();

                        view3d = View3D.CreatePerspective(doc, viewFamilyType.Id);
                        var orientation = new ViewOrientation3D(eye, up, forward);
                        view3d.SetOrientation(orientation);
                    }
                    else
                    {
                        // Create isometric view
                        view3d = View3D.CreateIsometric(doc, viewFamilyType.Id);
                    }

                    view3d.Name = viewName;

                    trans.Commit();
                }

                var orient = view3d.GetOrientation();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = view3d.Id.Value,
                        viewName = view3d.Name,
                        isPerspective = view3d.IsPerspective,
                        eyePosition = new { x = orient.EyePosition.X, y = orient.EyePosition.Y, z = orient.EyePosition.Z },
                        forwardDirection = new { x = orient.ForwardDirection.X, y = orient.ForwardDirection.Y, z = orient.ForwardDirection.Z }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region AI Vision Analysis

        /// <summary>
        /// Capture current view and analyze it with Claude's vision capability.
        /// This gives the AI "eyes" to see what it's doing and verify results.
        /// </summary>
        /// <param name="uiApp">Revit UIApplication</param>
        /// <param name="parameters">
        /// - viewId (optional): View to analyze. Uses active view if not specified.
        /// - question: What to look for or analyze in the view.
        /// </param>
        /// <param name="apiKey">Anthropic API key for Claude</param>
        public static string AnalyzeView(UIApplication uiApp, JObject parameters, string apiKey)
        {
            try
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "API key required for vision analysis"
                    });
                }

                var question = parameters["question"]?.ToString() ?? "Describe what you see in this Revit view.";

                // Capture view to base64
                var captureParams = new JObject();
                if (parameters["viewId"] != null)
                {
                    captureParams["viewId"] = parameters["viewId"];
                }
                captureParams["width"] = 1200;  // Good resolution for analysis
                captureParams["height"] = 800;

                var captureResult = CaptureViewportToBase64(uiApp, captureParams);
                var capture = JObject.Parse(captureResult);

                if (capture["success"]?.ToObject<bool>() != true)
                {
                    return captureResult;
                }

                var base64Image = capture["result"]["base64"].ToString();
                var viewName = capture["result"]["viewName"]?.ToString() ?? "Unknown View";
                var viewId = capture["result"]["viewId"]?.ToObject<int>() ?? 0;

                // Call Claude's vision API
                var analysisResult = CallClaudeVision(apiKey, base64Image, question, viewName);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        viewId = viewId,
                        viewName = viewName,
                        question = question,
                        analysis = analysisResult,
                        capturedAt = DateTime.Now.ToString("o")
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Call Claude API with vision capability to analyze an image.
        /// </summary>
        private static string CallClaudeVision(string apiKey, string base64Image, string question, string viewName)
        {
            using (var client = new System.Net.Http.HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                var requestBody = new
                {
                    model = "claude-3-5-haiku-20241022",  // Use Haiku for cost-effective vision
                    max_tokens = 1024,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new
                                {
                                    type = "image",
                                    source = new
                                    {
                                        type = "base64",
                                        media_type = "image/png",
                                        data = base64Image
                                    }
                                },
                                new
                                {
                                    type = "text",
                                    text = $"You are analyzing a Revit view named '{viewName}'.\n\n" +
                                           $"Question: {question}\n\n" +
                                           "Please analyze the image and answer the question. " +
                                           "Focus on:\n" +
                                           "- What elements are visible (views, viewports, text, annotations)\n" +
                                           "- Layout and positioning\n" +
                                           "- Any issues or problems you notice\n" +
                                           "- Whether the layout looks correct for a construction document\n\n" +
                                           "Be specific and concise."
                                }
                            }
                        }
                    }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = client.PostAsync("https://api.anthropic.com/v1/messages", content).Result;
                var responseBody = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Claude API error: {response.StatusCode} - {responseBody}");
                }

                var result = JObject.Parse(responseBody);
                var analysisText = result["content"]?[0]?["text"]?.ToString() ?? "No analysis available";

                return analysisText;
            }
        }

        #endregion
    }
}
