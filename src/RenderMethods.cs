using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// MCP methods for AI rendering integration.
    /// These methods interface with the Python DiffusionService for
    /// Stable Diffusion rendering of viewport captures.
    /// </summary>
    public static class RenderMethods
    {
        private static readonly string PythonPath = "python";
        private static readonly string ServiceScript = Path.Combine(
            Path.GetDirectoryName(typeof(RenderMethods).Assembly.Location),
            "..", "python", "diffusion_service.py"
        );

        /// <summary>
        /// Submit a viewport capture for AI rendering.
        /// </summary>
        /// <param name="uiApp">Revit UI Application</param>
        /// <param name="parameters">
        /// Required:
        ///   - imagePath: Path to the viewport capture image
        /// Optional:
        ///   - prompt: Description for the AI rendering
        ///   - stylePreset: photorealistic, sketch, watercolor, blueprint, night_render, minimalist
        ///   - negativePrompt: What to avoid in the render
        ///   - backend: automatic1111 or comfyui (default: automatic1111)
        ///   - backendUrl: URL of the AI backend (default: http://localhost:7860)
        /// </param>
        [MCPMethod("submitRender", Category = "Render", Description = "Submit a viewport capture for AI rendering")]
        public static string SubmitRender(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Required parameter
                if (parameters["imagePath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "imagePath is required"
                    });
                }

                var imagePath = parameters["imagePath"].ToString();
                if (!File.Exists(imagePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Image file not found: {imagePath}"
                    });
                }

                // Optional parameters
                var prompt = parameters["prompt"]?.ToString() ?? "architectural visualization";
                var stylePreset = parameters["stylePreset"]?.ToString() ?? "photorealistic";
                var negativePrompt = parameters["negativePrompt"]?.ToString() ?? "";
                var backend = parameters["backend"]?.ToString() ?? "automatic1111";
                var backendUrl = parameters["backendUrl"]?.ToString() ?? "http://localhost:7860";

                // Generate a job ID
                var jobId = Guid.NewGuid().ToString().Substring(0, 8);

                // Create the render job request
                var jobRequest = new
                {
                    jobId = jobId,
                    imagePath = imagePath,
                    prompt = prompt,
                    stylePreset = stylePreset,
                    negativePrompt = negativePrompt,
                    backend = backend,
                    backendUrl = backendUrl,
                    status = "queued",
                    createdAt = DateTime.UtcNow.ToString("o")
                };

                // Store job info for tracking
                var jobsDir = Path.Combine(Path.GetTempPath(), "RevitMCPBridge_Renders");
                Directory.CreateDirectory(jobsDir);
                var jobFile = Path.Combine(jobsDir, $"{jobId}.json");
                File.WriteAllText(jobFile, JsonConvert.SerializeObject(jobRequest, Formatting.Indented));

                // Start async render process
                Task.Run(() => ExecuteRenderAsync(jobId, imagePath, prompt, stylePreset, negativePrompt, backend, backendUrl, jobFile));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    jobId = jobId,
                    message = "Render job queued",
                    status = "queued"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the status of a render job.
        /// </summary>
        [MCPMethod("getRenderStatus", Category = "Render", Description = "Get the status of a render job")]
        public static string GetRenderStatus(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (parameters["jobId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "jobId is required"
                    });
                }

                var jobId = parameters["jobId"].ToString();
                var jobsDir = Path.Combine(Path.GetTempPath(), "RevitMCPBridge_Renders");
                var jobFile = Path.Combine(jobsDir, $"{jobId}.json");

                if (!File.Exists(jobFile))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Job not found: {jobId}"
                    });
                }

                var jobData = JObject.Parse(File.ReadAllText(jobFile));
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    jobId = jobId,
                    status = jobData["status"]?.ToString(),
                    progress = jobData["progress"]?.Value<double>() ?? 0,
                    error = jobData["error"]?.ToString(),
                    resultImage = jobData["resultImage"]?.ToString()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the result of a completed render job.
        /// </summary>
        [MCPMethod("getRenderResult", Category = "Render", Description = "Get the result of a completed render job")]
        public static string GetRenderResult(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (parameters["jobId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "jobId is required"
                    });
                }

                var jobId = parameters["jobId"].ToString();
                var jobsDir = Path.Combine(Path.GetTempPath(), "RevitMCPBridge_Renders");
                var jobFile = Path.Combine(jobsDir, $"{jobId}.json");

                if (!File.Exists(jobFile))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Job not found: {jobId}"
                    });
                }

                var jobData = JObject.Parse(File.ReadAllText(jobFile));
                var status = jobData["status"]?.ToString();

                if (status != "completed")
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Job not completed. Current status: {status}",
                        status = status
                    });
                }

                var resultImage = jobData["resultImage"]?.ToString();
                string base64Image = null;

                // Optionally return base64 encoded result
                var returnBase64 = parameters["returnBase64"]?.Value<bool>() ?? false;
                if (returnBase64 && File.Exists(resultImage))
                {
                    var imageBytes = File.ReadAllBytes(resultImage);
                    base64Image = Convert.ToBase64String(imageBytes);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    jobId = jobId,
                    status = status,
                    resultImage = resultImage,
                    base64Image = base64Image,
                    prompt = jobData["prompt"]?.ToString(),
                    stylePreset = jobData["stylePreset"]?.ToString()
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// List all render jobs.
        /// </summary>
        [MCPMethod("listRenderJobs", Category = "Render", Description = "List all render jobs")]
        public static string ListRenderJobs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var jobsDir = Path.Combine(Path.GetTempPath(), "RevitMCPBridge_Renders");
                if (!Directory.Exists(jobsDir))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        jobs = new object[0]
                    });
                }

                var statusFilter = parameters["status"]?.ToString();
                var jobs = new System.Collections.Generic.List<object>();

                foreach (var jobFile in Directory.GetFiles(jobsDir, "*.json"))
                {
                    try
                    {
                        var jobData = JObject.Parse(File.ReadAllText(jobFile));
                        var jobStatus = jobData["status"]?.ToString();

                        if (statusFilter != null && jobStatus != statusFilter)
                            continue;

                        jobs.Add(new
                        {
                            jobId = jobData["jobId"]?.ToString(),
                            status = jobStatus,
                            stylePreset = jobData["stylePreset"]?.ToString(),
                            createdAt = jobData["createdAt"]?.ToString(),
                            progress = jobData["progress"]?.Value<double>() ?? 0
                        });
                    }
                    catch
                    {
                        // Skip malformed job files
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = jobs.Count,
                    jobs = jobs
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Cancel a render job.
        /// </summary>
        [MCPMethod("cancelRender", Category = "Render", Description = "Cancel a render job")]
        public static string CancelRender(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (parameters["jobId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "jobId is required"
                    });
                }

                var jobId = parameters["jobId"].ToString();
                var jobsDir = Path.Combine(Path.GetTempPath(), "RevitMCPBridge_Renders");
                var jobFile = Path.Combine(jobsDir, $"{jobId}.json");

                if (!File.Exists(jobFile))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Job not found: {jobId}"
                    });
                }

                var jobData = JObject.Parse(File.ReadAllText(jobFile));
                var status = jobData["status"]?.ToString();

                if (status == "completed" || status == "failed")
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot cancel a finished job"
                    });
                }

                jobData["status"] = "cancelled";
                File.WriteAllText(jobFile, jobData.ToString(Formatting.Indented));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Job {jobId} cancelled"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available style presets.
        /// </summary>
        [MCPMethod("getRenderPresets", Category = "Render", Description = "Get available render style presets")]
        public static string GetRenderPresets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var presets = new
                {
                    photorealistic = new
                    {
                        description = "Photorealistic architectural visualization",
                        steps = 30,
                        denoisingStrength = 0.5
                    },
                    sketch = new
                    {
                        description = "Architectural pencil sketch",
                        steps = 25,
                        denoisingStrength = 0.7
                    },
                    watercolor = new
                    {
                        description = "Watercolor architectural illustration",
                        steps = 28,
                        denoisingStrength = 0.65
                    },
                    blueprint = new
                    {
                        description = "Technical blueprint style",
                        steps = 25,
                        denoisingStrength = 0.8
                    },
                    night_render = new
                    {
                        description = "Nighttime architectural visualization",
                        steps = 35,
                        denoisingStrength = 0.55
                    },
                    minimalist = new
                    {
                        description = "Minimalist architectural rendering",
                        steps = 25,
                        denoisingStrength = 0.6
                    }
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    presets = presets
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Capture viewport and immediately submit for rendering (convenience method).
        /// </summary>
        [MCPMethod("captureAndRender", Category = "Render", Description = "Capture viewport and immediately submit for rendering")]
        public static string CaptureAndRender(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // First capture the viewport
                var captureResult = ViewportCaptureMethods.CaptureViewport(uiApp, parameters);
                var captureResponse = JObject.Parse(captureResult);

                if (captureResponse["success"]?.Value<bool>() != true)
                {
                    return captureResult;
                }

                var imagePath = captureResponse["filePath"]?.ToString();
                if (string.IsNullOrEmpty(imagePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Viewport capture did not return a file path"
                    });
                }

                // Now submit for rendering
                parameters["imagePath"] = imagePath;
                return SubmitRender(uiApp, parameters);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Execute the render asynchronously using the Python service.
        /// </summary>
        private static async Task ExecuteRenderAsync(
            string jobId,
            string imagePath,
            string prompt,
            string stylePreset,
            string negativePrompt,
            string backend,
            string backendUrl,
            string jobFile)
        {
            try
            {
                // Update status to processing
                UpdateJobStatus(jobFile, "processing", 0);

                // Call the Python diffusion service
                var outputDir = Path.Combine(Path.GetTempPath(), "RevitMCPBridge_Renders", "outputs");
                Directory.CreateDirectory(outputDir);

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = PythonPath,
                        Arguments = $"\"{ServiceScript}\" --backend {backend} --url \"{backendUrl}\" " +
                                   $"--output \"{outputDir}\" --test --image \"{imagePath}\" " +
                                   $"--prompt \"{prompt.Replace("\"", "\\\"")}\" --style {stylePreset}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode == 0)
                {
                    // Parse output to find result path
                    var resultPath = ParseResultPath(output, outputDir, jobId);
                    UpdateJobStatus(jobFile, "completed", 100, resultPath);
                }
                else
                {
                    UpdateJobStatus(jobFile, "failed", 0, null, error);
                }
            }
            catch (Exception ex)
            {
                UpdateJobStatus(jobFile, "failed", 0, null, ex.Message);
            }
        }

        private static void UpdateJobStatus(string jobFile, string status, double progress, string resultImage = null, string error = null)
        {
            try
            {
                var jobData = JObject.Parse(File.ReadAllText(jobFile));
                jobData["status"] = status;
                jobData["progress"] = progress;
                if (resultImage != null)
                    jobData["resultImage"] = resultImage;
                if (error != null)
                    jobData["error"] = error;
                File.WriteAllText(jobFile, jobData.ToString(Formatting.Indented));
            }
            catch
            {
                // Ignore file update errors
            }
        }

        private static string ParseResultPath(string output, string outputDir, string jobId)
        {
            // Look for "Result: <path>" in output
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("Result:"))
                {
                    return line.Substring(7).Trim();
                }
            }

            // Fallback: look for job ID in output directory
            var possiblePath = Path.Combine(outputDir, $"{jobId}_render.png");
            if (File.Exists(possiblePath))
                return possiblePath;

            return null;
        }
    }
}
