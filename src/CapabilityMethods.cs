using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Self-expanding capability system for RevitMCPBridge.
    /// Handles failure classification, tool spec proposals, method registry, and test artifacts.
    /// </summary>
    public static class CapabilityMethods
    {
        private static readonly string CapabilitySystemPath = @"D:\RevitMCPBridge2026\capability_system";
        private static readonly string ToolSpecsPath = Path.Combine(CapabilitySystemPath, "tool_specs");
        private static readonly string MethodRegistryPath = Path.Combine(CapabilitySystemPath, "method_registry");
        private static readonly string MethodGymPath = Path.Combine(CapabilitySystemPath, "method_gym");
        private static readonly string FailureLogsPath = Path.Combine(CapabilitySystemPath, "failure_logs");

        #region Failure Classification

        /// <summary>
        /// Classifies a task failure into one of five categories:
        /// - MISSING_CAPABILITY: No tool exists for this task
        /// - BAD_PARAMETERS: Tool exists but parameters are wrong
        /// - CONTEXT_MISMATCH: Wrong Revit state (view, selection, document)
        /// - REVIT_CONSTRAINT: Revit rule violation (transaction, element constraints)
        /// - TOOL_BUG: Exception or edge case in existing tool
        /// </summary>
        public static string ClassifyFailure(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var userIntent = parameters?["userIntent"]?.ToString() ?? "";
                var attemptedMethod = parameters?["attemptedMethod"]?.ToString();
                var errorMessage = parameters?["errorMessage"]?.ToString() ?? "";
                var errorType = parameters?["errorType"]?.ToString() ?? "";
                var methodParams = parameters?["parameters"] as JObject;

                var failureId = $"fail_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString().Substring(0, 8)}";

                var classification = AnalyzeFailure(
                    userIntent, attemptedMethod, errorMessage, errorType, methodParams);

                // Build the failure record
                var failureRecord = new JObject
                {
                    ["failureId"] = failureId,
                    ["timestamp"] = DateTime.Now.ToString("o"),
                    ["classification"] = new JObject
                    {
                        ["type"] = classification.Type,
                        ["subType"] = classification.SubType,
                        ["confidence"] = classification.Confidence,
                        ["reasoning"] = classification.Reasoning
                    },
                    ["originalTask"] = new JObject
                    {
                        ["userIntent"] = userIntent,
                        ["attemptedMethod"] = attemptedMethod,
                        ["parameters"] = methodParams,
                        ["context"] = GetCurrentContext(uiApp)
                    },
                    ["errorDetails"] = new JObject
                    {
                        ["errorMessage"] = errorMessage,
                        ["errorType"] = errorType
                    },
                    ["analysis"] = classification.Analysis,
                    ["recommendedAction"] = classification.RecommendedAction
                };

                // Save failure log
                var logPath = Path.Combine(FailureLogsPath, $"{failureId}.json");
                Directory.CreateDirectory(FailureLogsPath);
                File.WriteAllText(logPath, failureRecord.ToString(Formatting.Indented));

                // If missing capability, auto-generate a tool spec proposal
                JObject proposedSpec = null;
                if (classification.Type == "MISSING_CAPABILITY")
                {
                    proposedSpec = GenerateToolSpecProposal(userIntent, attemptedMethod, classification);
                    if (proposedSpec != null)
                    {
                        var specPath = SaveToolSpec(proposedSpec, "proposed");
                        failureRecord["proposedSpecPath"] = specPath;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    failureId = failureId,
                    classification = new
                    {
                        type = classification.Type,
                        subType = classification.SubType,
                        confidence = classification.Confidence,
                        reasoning = classification.Reasoning
                    },
                    recommendedAction = classification.RecommendedAction,
                    proposedSpec = proposedSpec,
                    logPath = logPath
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static FailureClassification AnalyzeFailure(
            string userIntent, string attemptedMethod, string errorMessage,
            string errorType, JObject methodParams)
        {
            var classification = new FailureClassification();

            // Pattern matching for failure classification
            var errorLower = errorMessage.ToLower();
            var intentLower = userIntent.ToLower();

            // Check for MISSING_CAPABILITY
            if (string.IsNullOrEmpty(attemptedMethod) ||
                errorLower.Contains("method not found") ||
                errorLower.Contains("no method") ||
                errorLower.Contains("not implemented") ||
                errorLower.Contains("cannot find"))
            {
                classification.Type = "MISSING_CAPABILITY";
                classification.Confidence = 0.9;
                classification.Reasoning = "No existing method matches the requested task";
                classification.Analysis = new JObject
                {
                    ["missingCapability"] = new JObject
                    {
                        ["requiredCapability"] = userIntent,
                        ["closestExistingMethod"] = FindClosestMethod(userIntent),
                        ["gapDescription"] = $"Need method to: {userIntent}"
                    }
                };
                classification.RecommendedAction = new JObject
                {
                    ["action"] = "PROPOSE_NEW_TOOL",
                    ["details"] = "Generate a tool specification for the missing capability",
                    ["autoExecutable"] = true
                };
                return classification;
            }

            // Check for BAD_PARAMETERS
            if (errorLower.Contains("parameter") ||
                errorLower.Contains("argument") ||
                errorLower.Contains("invalid") ||
                errorLower.Contains("required") ||
                errorLower.Contains("null") ||
                errorType.Contains("ArgumentException"))
            {
                classification.Type = "BAD_PARAMETERS";
                classification.Confidence = 0.85;
                classification.Reasoning = "Method exists but received invalid parameters";
                classification.Analysis = new JObject
                {
                    ["badParameters"] = new JObject
                    {
                        ["invalidParams"] = ExtractInvalidParams(errorMessage),
                        ["missingParams"] = ExtractMissingParams(errorMessage)
                    }
                };
                classification.RecommendedAction = new JObject
                {
                    ["action"] = "FIX_PARAMETERS",
                    ["details"] = "Correct the parameter values and retry",
                    ["autoExecutable"] = true
                };
                return classification;
            }

            // Check for CONTEXT_MISMATCH
            if (errorLower.Contains("no active") ||
                errorLower.Contains("selection") ||
                errorLower.Contains("active view") ||
                errorLower.Contains("active document") ||
                errorLower.Contains("not open"))
            {
                classification.Type = "CONTEXT_MISMATCH";
                classification.Confidence = 0.8;
                classification.Reasoning = "Revit context doesn't match method requirements";
                classification.Analysis = new JObject
                {
                    ["contextMismatch"] = new JObject
                    {
                        ["requiredContext"] = ExtractRequiredContext(errorMessage),
                        ["suggestion"] = "Ensure correct view/selection/document is active"
                    }
                };
                classification.RecommendedAction = new JObject
                {
                    ["action"] = "CHANGE_CONTEXT",
                    ["details"] = "Switch to appropriate view or make required selection",
                    ["autoExecutable"] = false
                };
                return classification;
            }

            // Check for REVIT_CONSTRAINT
            if (errorLower.Contains("transaction") ||
                errorLower.Contains("constraint") ||
                errorLower.Contains("cannot modify") ||
                errorLower.Contains("read-only") ||
                errorLower.Contains("invalid operation") ||
                errorType.Contains("InvalidOperationException"))
            {
                classification.Type = "REVIT_CONSTRAINT";
                classification.Confidence = 0.75;
                classification.Reasoning = "Operation violates Revit rules or constraints";
                classification.Analysis = new JObject
                {
                    ["revitConstraint"] = new JObject
                    {
                        ["constraintType"] = DetermineConstraintType(errorMessage),
                        ["violatedRule"] = errorMessage,
                        ["workaround"] = SuggestWorkaround(errorMessage)
                    }
                };
                classification.RecommendedAction = new JObject
                {
                    ["action"] = "USE_WORKAROUND",
                    ["details"] = SuggestWorkaround(errorMessage),
                    ["autoExecutable"] = false
                };
                return classification;
            }

            // Default to TOOL_BUG
            classification.Type = "TOOL_BUG";
            classification.Confidence = 0.6;
            classification.Reasoning = "Exception or unexpected behavior in existing tool";
            classification.Analysis = new JObject
            {
                ["toolBug"] = new JObject
                {
                    ["bugDescription"] = errorMessage,
                    ["reproducible"] = true,
                    ["severity"] = DetermineSeverity(errorMessage),
                    ["affectedMethod"] = attemptedMethod
                }
            };
            classification.RecommendedAction = new JObject
            {
                ["action"] = "REPORT_BUG",
                ["details"] = "Log bug for investigation and potential fix",
                ["autoExecutable"] = false
            };
            return classification;
        }

        #endregion

        #region Tool Spec Management

        /// <summary>
        /// Proposes a new tool specification based on a capability gap.
        /// </summary>
        public static string ProposeToolSpec(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var name = parameters?["name"]?.ToString();
                var intent = parameters?["intent"]?.ToString();
                var category = parameters?["category"]?.ToString() ?? "capability";
                var tier = parameters?["tier"]?.Value<int>() ?? 3;
                var inputs = parameters?["inputs"] as JObject ?? new JObject();
                var preconditions = parameters?["preconditions"] as JArray ?? new JArray();
                var algorithm = parameters?["algorithm"] as JArray ?? new JArray();
                var validation = parameters?["validation"] as JObject;
                var failureModes = parameters?["failureModes"] as JArray;
                var apiDependencies = parameters?["apiDependencies"] as JArray;
                var triggeringTask = parameters?["triggeringTask"]?.ToString();

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(intent))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "name and intent are required"
                    });
                }

                // Normalize method name to camelCase
                name = ToCamelCase(name);

                var spec = new JObject
                {
                    ["specVersion"] = "1.0",
                    ["method"] = new JObject
                    {
                        ["name"] = name,
                        ["intent"] = intent,
                        ["tier"] = tier,
                        ["category"] = category
                    },
                    ["inputs"] = inputs,
                    ["preconditions"] = preconditions,
                    ["algorithm"] = algorithm,
                    ["validation"] = validation ?? new JObject
                    {
                        ["successCriteria"] = "Method completes without error",
                        ["evidence"] = new JArray { "success", "message" }
                    },
                    ["failureModes"] = failureModes ?? new JArray(),
                    ["apiDependencies"] = apiDependencies ?? new JArray(),
                    ["revitVersions"] = new JArray { "2026" },
                    ["metadata"] = new JObject
                    {
                        ["proposedBy"] = "Claude",
                        ["proposedAt"] = DateTime.Now.ToString("o"),
                        ["triggeringTask"] = triggeringTask
                    }
                };

                var specPath = SaveToolSpec(spec, "proposed");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    specId = name,
                    specPath = specPath,
                    spec = spec,
                    message = $"Tool spec '{name}' proposed. Review and approve to proceed with implementation."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Approves a proposed tool specification, moving it to the approved folder.
        /// </summary>
        public static string ApproveToolSpec(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var specName = parameters?["specName"]?.ToString();
                var approvedBy = parameters?["approvedBy"]?.ToString() ?? "User";
                var notes = parameters?["notes"]?.ToString();

                if (string.IsNullOrEmpty(specName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "specName is required"
                    });
                }

                var proposedPath = Path.Combine(ToolSpecsPath, "proposed", $"{specName}.json");
                if (!File.Exists(proposedPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Proposed spec not found: {specName}"
                    });
                }

                var specJson = File.ReadAllText(proposedPath);
                var spec = JObject.Parse(specJson);

                // Update metadata
                spec["metadata"]["approvedBy"] = approvedBy;
                spec["metadata"]["approvedAt"] = DateTime.Now.ToString("o");
                if (!string.IsNullOrEmpty(notes))
                {
                    spec["metadata"]["approvalNotes"] = notes;
                }

                // Move to approved folder
                var approvedPath = Path.Combine(ToolSpecsPath, "approved", $"{specName}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(approvedPath));
                File.WriteAllText(approvedPath, spec.ToString(Formatting.Indented));
                File.Delete(proposedPath);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    specName = specName,
                    approvedPath = approvedPath,
                    message = $"Spec '{specName}' approved. Ready for implementation."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Lists all tool specifications by status.
        /// </summary>
        public static string ListToolSpecs(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var status = parameters?["status"]?.ToString(); // proposed, approved, implemented, or null for all

                var result = new JObject
                {
                    ["proposed"] = new JArray(),
                    ["approved"] = new JArray(),
                    ["implemented"] = new JArray()
                };

                var statuses = string.IsNullOrEmpty(status)
                    ? new[] { "proposed", "approved", "implemented" }
                    : new[] { status };

                foreach (var s in statuses)
                {
                    var folderPath = Path.Combine(ToolSpecsPath, s);
                    if (Directory.Exists(folderPath))
                    {
                        foreach (var file in Directory.GetFiles(folderPath, "*.json"))
                        {
                            var specJson = File.ReadAllText(file);
                            var spec = JObject.Parse(specJson);
                            result[s].Value<JArray>().Add(new JObject
                            {
                                ["name"] = spec["method"]?["name"],
                                ["intent"] = spec["method"]?["intent"],
                                ["tier"] = spec["method"]?["tier"],
                                ["category"] = spec["method"]?["category"],
                                ["proposedAt"] = spec["metadata"]?["proposedAt"],
                                ["path"] = file
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    counts = new
                    {
                        proposed = result["proposed"].Count(),
                        approved = result["approved"].Count(),
                        implemented = result["implemented"].Count()
                    },
                    specs = result
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets a specific tool specification by name.
        /// </summary>
        public static string GetToolSpec(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var specName = parameters?["specName"]?.ToString();

                if (string.IsNullOrEmpty(specName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "specName is required"
                    });
                }

                // Search in all folders
                foreach (var status in new[] { "proposed", "approved", "implemented" })
                {
                    var path = Path.Combine(ToolSpecsPath, status, $"{specName}.json");
                    if (File.Exists(path))
                    {
                        var specJson = File.ReadAllText(path);
                        var spec = JObject.Parse(specJson);
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            status = status,
                            path = path,
                            spec = spec
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = $"Spec not found: {specName}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Method Registry

        /// <summary>
        /// Gets the method registry with tier metadata.
        /// </summary>
        public static string GetMethodRegistry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var category = parameters?["category"]?.ToString();
                var tier = parameters?["tier"]?.Value<int>();

                var registryPath = Path.Combine(MethodRegistryPath, "method_registry.json");

                if (!File.Exists(registryPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Method registry not found. Run rebuildMethodRegistry first."
                    });
                }

                var registryJson = File.ReadAllText(registryPath);
                var registry = JObject.Parse(registryJson);
                var methods = registry["methods"] as JObject;

                // Filter if requested
                if (!string.IsNullOrEmpty(category) || tier.HasValue)
                {
                    var filtered = new JObject();
                    foreach (var prop in methods.Properties())
                    {
                        var method = prop.Value as JObject;
                        var matchCategory = string.IsNullOrEmpty(category) ||
                            method["category"]?.ToString() == category;
                        var matchTier = !tier.HasValue ||
                            method["tier"]?.Value<int>() == tier.Value;

                        if (matchCategory && matchTier)
                        {
                            filtered[prop.Name] = method;
                        }
                    }
                    methods = filtered;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    stats = registry["stats"],
                    methodCount = methods.Count,
                    methods = methods
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rebuilds the method registry from source files.
        /// </summary>
        public static string RebuildMethodRegistry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var registry = BuildMethodRegistry();

                var registryPath = Path.Combine(MethodRegistryPath, "method_registry.json");
                Directory.CreateDirectory(MethodRegistryPath);
                File.WriteAllText(registryPath, registry.ToString(Formatting.Indented));

                var stats = registry["stats"] as JObject;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    registryPath = registryPath,
                    totalMethods = stats["totalMethods"],
                    tier1Count = stats["tier1Count"],
                    tier2Count = stats["tier2Count"],
                    tier3Count = stats["tier3Count"],
                    message = "Method registry rebuilt successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Method Gym (Test Artifacts)

        /// <summary>
        /// Creates a test artifact for a method.
        /// </summary>
        public static string CreateTestArtifact(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var methodName = parameters?["methodName"]?.ToString();
                var taskRequest = parameters?["taskRequest"] as JObject;
                var expectedOutcome = parameters?["expectedOutcome"] as JObject;
                var triggerType = parameters?["triggerType"]?.ToString() ?? "manual";

                if (string.IsNullOrEmpty(methodName) || taskRequest == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "methodName and taskRequest are required"
                    });
                }

                var testId = $"{methodName}_{DateTime.Now:yyyyMMdd_HHmmss}";
                var testFolder = Path.Combine(MethodGymPath, "tests", methodName, testId);
                Directory.CreateDirectory(testFolder);

                var artifact = new JObject
                {
                    ["testId"] = testId,
                    ["methodName"] = methodName,
                    ["createdAt"] = DateTime.Now.ToString("o"),
                    ["triggerType"] = triggerType,
                    ["taskRequest"] = taskRequest,
                    ["initialState"] = GetCurrentContext(uiApp),
                    ["expectedOutcome"] = expectedOutcome ?? new JObject
                    {
                        ["success"] = true
                    },
                    ["execution"] = new JObject
                    {
                        ["status"] = "pending"
                    }
                };

                var artifactPath = Path.Combine(testFolder, "test_artifact.json");
                File.WriteAllText(artifactPath, artifact.ToString(Formatting.Indented));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    testId = testId,
                    testFolder = testFolder,
                    artifactPath = artifactPath,
                    message = $"Test artifact created for '{methodName}'"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Runs a test from a test artifact - actually executes the method and validates results.
        /// </summary>
        public static string RunTest(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var testId = parameters?["testId"]?.ToString();
                var methodName = parameters?["methodName"]?.ToString();

                if (string.IsNullOrEmpty(testId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "testId is required"
                    });
                }

                // Find the test artifact
                var testFolder = FindTestFolder(testId, methodName);
                if (testFolder == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Test not found: {testId}"
                    });
                }

                var artifactPath = Path.Combine(testFolder, "test_artifact.json");
                var artifactJson = File.ReadAllText(artifactPath);
                var artifact = JObject.Parse(artifactJson);

                // Capture initial state
                var initialState = CaptureModelState(uiApp);
                artifact["initialState"] = initialState;

                // Update execution status
                artifact["execution"]["status"] = "running";
                artifact["execution"]["startedAt"] = DateTime.Now.ToString("o");
                File.WriteAllText(artifactPath, artifact.ToString(Formatting.Indented));

                // Execute the method via MCPServer
                var taskRequest = artifact["taskRequest"] as JObject;
                var method = taskRequest["method"]?.ToString();
                var methodParams = taskRequest["params"] as JObject ?? new JObject();

                JObject actualResult;
                try
                {
                    // Actually execute the method
                    var resultJson = MCPServer.ExecuteMethod(uiApp, method, methodParams);
                    actualResult = JObject.Parse(resultJson);
                }
                catch (Exception methodEx)
                {
                    actualResult = new JObject
                    {
                        ["success"] = false,
                        ["error"] = methodEx.Message,
                        ["stackTrace"] = methodEx.StackTrace
                    };
                }

                // Capture final state
                var finalState = CaptureModelState(uiApp);

                // Validate assertions
                var expectedOutcome = artifact["expectedOutcome"] as JObject;
                var assertionResults = ValidateAssertions(expectedOutcome, actualResult, initialState, finalState);
                var allPassed = assertionResults.All(r => r["passed"].Value<bool>());

                // Capture evidence
                var evidence = new JObject
                {
                    ["createdElementIds"] = FindNewElements(initialState, finalState),
                    ["modifiedElementIds"] = FindModifiedElements(initialState, finalState),
                    ["stateChange"] = new JObject
                    {
                        ["before"] = initialState,
                        ["after"] = finalState
                    }
                };

                // Update artifact with results
                artifact["execution"]["status"] = allPassed ? "passed" : "failed";
                artifact["execution"]["completedAt"] = DateTime.Now.ToString("o");
                artifact["execution"]["durationMs"] =
                    (DateTime.Now - DateTime.Parse(artifact["execution"]["startedAt"].ToString())).TotalMilliseconds;
                artifact["actualResult"] = actualResult;
                artifact["assertionResults"] = new JArray(assertionResults);
                artifact["evidence"] = evidence;

                File.WriteAllText(artifactPath, artifact.ToString(Formatting.Indented));

                // Save to results folder
                var resultPath = Path.Combine(MethodGymPath, "results", $"{testId}_result.json");
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                File.WriteAllText(resultPath, artifact.ToString(Formatting.Indented));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    testId = testId,
                    methodExecuted = method,
                    passed = allPassed,
                    assertionResults = assertionResults,
                    evidence = evidence,
                    resultPath = resultPath
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Generates implementation code from an approved tool spec.
        /// </summary>
        public static string GenerateImplementation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var specName = parameters?["specName"]?.ToString();
                var targetFile = parameters?["targetFile"]?.ToString();

                if (string.IsNullOrEmpty(specName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "specName is required" });
                }

                // Load approved spec
                var approvedPath = Path.Combine(ToolSpecsPath, "approved", $"{specName}.json");
                if (!File.Exists(approvedPath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Approved spec not found: {specName}" });
                }

                var spec = JObject.Parse(File.ReadAllText(approvedPath));
                var methodInfo = spec["method"] as JObject;
                var inputs = spec["inputs"] as JObject ?? new JObject();
                var algorithm = spec["algorithm"] as JArray ?? new JArray();
                var validation = spec["validation"] as JObject;
                var failureModes = spec["failureModes"] as JArray ?? new JArray();

                // Generate C# code
                var code = GenerateCSharpMethod(methodInfo, inputs, algorithm, validation, failureModes);

                // Generate switch case for MCPServer
                var switchCase = GenerateSwitchCase(methodInfo["name"]?.ToString(), methodInfo["category"]?.ToString());

                // Generate test artifact template
                var testTemplate = GenerateTestTemplate(methodInfo, inputs);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    specName = specName,
                    generatedCode = code,
                    switchCase = switchCase,
                    testTemplate = testTemplate,
                    nextSteps = new[]
                    {
                        $"1. Add method to {targetFile ?? "NewMethods.cs"}",
                        "2. Add switch case to MCPServer.cs",
                        "3. Build and deploy",
                        "4. Create test artifact and run tests",
                        "5. Move spec to implemented/ after tests pass"
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Runs all pending tests for regression.
        /// </summary>
        public static string RunRegressionTests(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var methodFilter = parameters?["methodName"]?.ToString();
                var testsPath = Path.Combine(MethodGymPath, "tests");
                var results = new JArray();
                int passed = 0, failed = 0, errors = 0;

                if (!Directory.Exists(testsPath))
                {
                    return JsonConvert.SerializeObject(new { success = true, message = "No tests found", passed = 0, failed = 0 });
                }

                var methodFolders = string.IsNullOrEmpty(methodFilter)
                    ? Directory.GetDirectories(testsPath)
                    : new[] { Path.Combine(testsPath, methodFilter) }.Where(Directory.Exists);

                foreach (var methodFolder in methodFolders)
                {
                    foreach (var testFolder in Directory.GetDirectories(methodFolder))
                    {
                        var testId = Path.GetFileName(testFolder);
                        var testParams = new JObject { ["testId"] = testId };

                        try
                        {
                            var resultJson = RunTest(uiApp, testParams);
                            var result = JObject.Parse(resultJson);

                            if (result["success"]?.Value<bool>() == true)
                            {
                                if (result["passed"]?.Value<bool>() == true)
                                    passed++;
                                else
                                    failed++;
                            }
                            else
                            {
                                errors++;
                            }

                            results.Add(new JObject
                            {
                                ["testId"] = testId,
                                ["passed"] = result["passed"],
                                ["error"] = result["error"]
                            });
                        }
                        catch (Exception testEx)
                        {
                            errors++;
                            results.Add(new JObject
                            {
                                ["testId"] = testId,
                                ["passed"] = false,
                                ["error"] = testEx.Message
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    summary = new { passed, failed, errors, total = passed + failed + errors },
                    results = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Lists test artifacts for a method or all methods.
        /// </summary>
        public static string ListTests(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var methodName = parameters?["methodName"]?.ToString();
                var status = parameters?["status"]?.ToString(); // pending, passed, failed, or null for all

                var testsPath = Path.Combine(MethodGymPath, "tests");
                var tests = new JArray();

                if (!Directory.Exists(testsPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        testCount = 0,
                        tests = tests
                    });
                }

                var methodFolders = string.IsNullOrEmpty(methodName)
                    ? Directory.GetDirectories(testsPath)
                    : new[] { Path.Combine(testsPath, methodName) }.Where(Directory.Exists);

                foreach (var methodFolder in methodFolders)
                {
                    foreach (var testFolder in Directory.GetDirectories(methodFolder))
                    {
                        var artifactPath = Path.Combine(testFolder, "test_artifact.json");
                        if (File.Exists(artifactPath))
                        {
                            var artifact = JObject.Parse(File.ReadAllText(artifactPath));
                            var testStatus = artifact["execution"]?["status"]?.ToString() ?? "pending";

                            if (string.IsNullOrEmpty(status) || testStatus == status)
                            {
                                tests.Add(new JObject
                                {
                                    ["testId"] = artifact["testId"],
                                    ["methodName"] = artifact["methodName"],
                                    ["status"] = testStatus,
                                    ["createdAt"] = artifact["createdAt"],
                                    ["path"] = testFolder
                                });
                            }
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    testCount = tests.Count,
                    tests = tests
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Capability Status

        /// <summary>
        /// Gets the overall status of the capability system.
        /// </summary>
        public static string GetCapabilityStatus(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Count specs in each status
                var proposedCount = CountFilesInFolder(Path.Combine(ToolSpecsPath, "proposed"));
                var approvedCount = CountFilesInFolder(Path.Combine(ToolSpecsPath, "approved"));
                var implementedCount = CountFilesInFolder(Path.Combine(ToolSpecsPath, "implemented"));

                // Count failure logs
                var failureCount = CountFilesInFolder(FailureLogsPath);

                // Count tests by status
                var pendingTests = 0;
                var passedTests = 0;
                var failedTests = 0;
                var testsPath = Path.Combine(MethodGymPath, "tests");
                if (Directory.Exists(testsPath))
                {
                    foreach (var file in Directory.GetFiles(testsPath, "test_artifact.json", SearchOption.AllDirectories))
                    {
                        var artifact = JObject.Parse(File.ReadAllText(file));
                        var status = artifact["execution"]?["status"]?.ToString() ?? "pending";
                        switch (status)
                        {
                            case "pending": pendingTests++; break;
                            case "passed": passedTests++; break;
                            case "failed": failedTests++; break;
                        }
                    }
                }

                // Get registry stats
                var registryPath = Path.Combine(MethodRegistryPath, "method_registry.json");
                JObject registryStats = null;
                if (File.Exists(registryPath))
                {
                    var registry = JObject.Parse(File.ReadAllText(registryPath));
                    registryStats = registry["stats"] as JObject;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    toolSpecs = new
                    {
                        proposed = proposedCount,
                        approved = approvedCount,
                        implemented = implementedCount,
                        total = proposedCount + approvedCount + implementedCount
                    },
                    failureLogs = failureCount,
                    methodGym = new
                    {
                        pending = pendingTests,
                        passed = passedTests,
                        failed = failedTests,
                        total = pendingTests + passedTests + failedTests
                    },
                    methodRegistry = registryStats,
                    systemPath = CapabilitySystemPath
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Methods

        private static JObject GetCurrentContext(UIApplication uiApp)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                var activeView = uiApp?.ActiveUIDocument?.ActiveView;

                return new JObject
                {
                    ["projectName"] = doc?.Title,
                    ["projectPath"] = doc?.PathName,
                    ["activeViewId"] = activeView?.Id?.Value,
                    ["activeViewName"] = activeView?.Name,
                    ["activeViewType"] = activeView?.ViewType.ToString(),
                    ["revitVersion"] = uiApp?.Application?.VersionNumber,
                    ["timestamp"] = DateTime.Now.ToString("o")
                };
            }
            catch
            {
                return new JObject { ["error"] = "Could not get context" };
            }
        }

        private static string SaveToolSpec(JObject spec, string status)
        {
            var name = spec["method"]?["name"]?.ToString() ?? $"spec_{DateTime.Now:yyyyMMdd_HHmmss}";
            var folderPath = Path.Combine(ToolSpecsPath, status);
            Directory.CreateDirectory(folderPath);
            var filePath = Path.Combine(folderPath, $"{name}.json");
            File.WriteAllText(filePath, spec.ToString(Formatting.Indented));
            return filePath;
        }

        private static JObject GenerateToolSpecProposal(string userIntent, string attemptedMethod, FailureClassification classification)
        {
            var methodName = GenerateMethodName(userIntent);

            return new JObject
            {
                ["specVersion"] = "1.0",
                ["method"] = new JObject
                {
                    ["name"] = methodName,
                    ["intent"] = userIntent,
                    ["tier"] = 3,
                    ["category"] = InferCategory(userIntent)
                },
                ["inputs"] = InferInputs(userIntent),
                ["preconditions"] = new JArray { "Active document exists" },
                ["algorithm"] = new JArray { $"TODO: Implement {userIntent}" },
                ["validation"] = new JObject
                {
                    ["successCriteria"] = "Method completes successfully",
                    ["evidence"] = new JArray { "success", "message" }
                },
                ["metadata"] = new JObject
                {
                    ["proposedBy"] = "Claude (auto-generated)",
                    ["proposedAt"] = DateTime.Now.ToString("o"),
                    ["triggeringTask"] = userIntent,
                    ["autoGenerated"] = true
                }
            };
        }

        private static string GenerateMethodName(string intent)
        {
            // Extract key verbs and nouns to create method name
            var words = Regex.Replace(intent, @"[^\w\s]", "").Split(' ')
                .Where(w => w.Length > 2)
                .Take(4)
                .ToArray();

            if (words.Length == 0) return "newMethod";

            return ToCamelCase(string.Join(" ", words));
        }

        private static string ToCamelCase(string input)
        {
            var words = input.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return input;

            return words[0].ToLower() +
                string.Join("", words.Skip(1).Select(w => char.ToUpper(w[0]) + w.Substring(1).ToLower()));
        }

        private static string InferCategory(string intent)
        {
            var intentLower = intent.ToLower();
            if (intentLower.Contains("wall")) return "wall";
            if (intentLower.Contains("door") || intentLower.Contains("window")) return "door_window";
            if (intentLower.Contains("room")) return "room";
            if (intentLower.Contains("view") || intentLower.Contains("elevation") || intentLower.Contains("section")) return "view";
            if (intentLower.Contains("sheet")) return "sheet";
            if (intentLower.Contains("schedule")) return "schedule";
            if (intentLower.Contains("family")) return "family";
            if (intentLower.Contains("parameter")) return "parameter";
            if (intentLower.Contains("structural") || intentLower.Contains("column") || intentLower.Contains("beam")) return "structural";
            if (intentLower.Contains("mep") || intentLower.Contains("pipe") || intentLower.Contains("duct")) return "mep";
            if (intentLower.Contains("detail")) return "detail";
            if (intentLower.Contains("material")) return "material";
            return "capability";
        }

        private static JObject InferInputs(string intent)
        {
            var inputs = new JObject();
            var intentLower = intent.ToLower();

            if (intentLower.Contains("room"))
                inputs["roomId"] = new JObject { ["type"] = "long", ["required"] = true, ["description"] = "Room element ID" };
            if (intentLower.Contains("wall"))
                inputs["wallId"] = new JObject { ["type"] = "long", ["required"] = true, ["description"] = "Wall element ID" };
            if (intentLower.Contains("view"))
                inputs["viewId"] = new JObject { ["type"] = "long", ["required"] = false, ["description"] = "View element ID" };

            return inputs;
        }

        private static string FindClosestMethod(string intent)
        {
            // This would search the registry for similar methods
            // For now, return a placeholder
            return "getElements";
        }

        private static JArray ExtractInvalidParams(string errorMessage)
        {
            var result = new JArray();
            var match = Regex.Match(errorMessage, @"parameter[:\s]+(\w+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                result.Add(new JObject { ["name"] = match.Groups[1].Value });
            }
            return result;
        }

        private static JArray ExtractMissingParams(string errorMessage)
        {
            var result = new JArray();
            var match = Regex.Match(errorMessage, @"required[:\s]+(\w+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                result.Add(match.Groups[1].Value);
            }
            return result;
        }

        private static string ExtractRequiredContext(string errorMessage)
        {
            if (errorMessage.ToLower().Contains("view")) return "Active view required";
            if (errorMessage.ToLower().Contains("selection")) return "Element selection required";
            if (errorMessage.ToLower().Contains("document")) return "Active document required";
            return "Specific Revit context required";
        }

        private static string DetermineConstraintType(string errorMessage)
        {
            if (errorMessage.ToLower().Contains("transaction")) return "TransactionRequired";
            if (errorMessage.ToLower().Contains("read-only")) return "ReadOnlyDocument";
            if (errorMessage.ToLower().Contains("constraint")) return "ElementConstraint";
            return "RevitRule";
        }

        private static string SuggestWorkaround(string errorMessage)
        {
            if (errorMessage.ToLower().Contains("transaction"))
                return "Ensure operation is within a Transaction block";
            if (errorMessage.ToLower().Contains("read-only"))
                return "Close and reopen document with write access";
            return "Review Revit constraints for this operation";
        }

        private static string DetermineSeverity(string errorMessage)
        {
            if (errorMessage.ToLower().Contains("critical") || errorMessage.ToLower().Contains("crash"))
                return "critical";
            if (errorMessage.ToLower().Contains("corrupt") || errorMessage.ToLower().Contains("data loss"))
                return "high";
            return "medium";
        }

        private static JObject BuildMethodRegistry()
        {
            // This builds the method registry from the known methods
            // In production, this would scan the source files
            var methods = new JObject();
            var categoryCounts = new Dictionary<string, int>();
            int tier1 = 0, tier2 = 0, tier3 = 0;

            // Define method categories and their tiers
            var methodDefinitions = GetMethodDefinitions();

            foreach (var def in methodDefinitions)
            {
                methods[def.Name] = new JObject
                {
                    ["name"] = def.Name,
                    ["tier"] = def.Tier,
                    ["category"] = def.Category,
                    ["status"] = "active",
                    ["version"] = "1.0.0",
                    ["description"] = def.Description,
                    ["sourceFile"] = def.SourceFile
                };

                switch (def.Tier)
                {
                    case 1: tier1++; break;
                    case 2: tier2++; break;
                    case 3: tier3++; break;
                }

                if (!categoryCounts.ContainsKey(def.Category))
                    categoryCounts[def.Category] = 0;
                categoryCounts[def.Category]++;
            }

            return new JObject
            {
                ["version"] = "1.0",
                ["generatedAt"] = DateTime.Now.ToString("o"),
                ["stats"] = new JObject
                {
                    ["totalMethods"] = methods.Count,
                    ["tier1Count"] = tier1,
                    ["tier2Count"] = tier2,
                    ["tier3Count"] = tier3,
                    ["categoryCounts"] = JObject.FromObject(categoryCounts)
                },
                ["methods"] = methods
            };
        }

        private static List<MethodDefinition> GetMethodDefinitions()
        {
            // Core method definitions with tiers
            // Tier 1: Stable, pure public API, deterministic
            // Tier 2: Beta, complex/fragile, many edge cases
            // Tier 3: Experimental, reflection/internal, version-gated

            return new List<MethodDefinition>
            {
                // Wall Methods (Tier 1 - stable)
                new MethodDefinition("createWall", 1, "wall", "Creates a wall between two points", "WallMethods.cs"),
                new MethodDefinition("getWalls", 1, "wall", "Gets all walls in the document", "WallMethods.cs"),
                new MethodDefinition("getWallById", 1, "wall", "Gets a wall by element ID", "WallMethods.cs"),
                new MethodDefinition("deleteWall", 1, "wall", "Deletes a wall by ID", "WallMethods.cs"),
                new MethodDefinition("getWallTypes", 1, "wall", "Gets all wall types", "WallMethods.cs"),

                // Room Methods (Tier 1)
                new MethodDefinition("createRoom", 1, "room", "Creates a room at a point", "RoomMethods.cs"),
                new MethodDefinition("getRooms", 1, "room", "Gets all rooms", "RoomMethods.cs"),
                new MethodDefinition("getRoomById", 1, "room", "Gets a room by ID", "RoomMethods.cs"),
                new MethodDefinition("tagRoom", 1, "room", "Tags a room", "RoomMethods.cs"),

                // View Methods (Tier 1-2)
                new MethodDefinition("getViews", 1, "view", "Gets all views", "ViewMethods.cs"),
                new MethodDefinition("getActiveView", 1, "view", "Gets the active view", "ViewMethods.cs"),
                new MethodDefinition("setActiveView", 1, "view", "Sets the active view", "ViewMethods.cs"),
                new MethodDefinition("createFloorPlan", 2, "view", "Creates a floor plan view", "ViewMethods.cs"),
                new MethodDefinition("createSection", 2, "view", "Creates a section view", "ViewMethods.cs"),
                new MethodDefinition("create3DView", 2, "view", "Creates a 3D view", "ViewMethods.cs"),

                // Sheet Methods (Tier 1-2)
                new MethodDefinition("getSheets", 1, "sheet", "Gets all sheets", "SheetMethods.cs"),
                new MethodDefinition("createSheet", 2, "sheet", "Creates a new sheet", "SheetMethods.cs"),
                new MethodDefinition("placeViewOnSheet", 2, "sheet", "Places a view on a sheet", "SheetMethods.cs"),

                // Family Methods (Tier 1-2)
                new MethodDefinition("getFamilies", 1, "family", "Gets loaded families", "FamilyMethods.cs"),
                new MethodDefinition("loadFamily", 2, "family", "Loads a family file", "FamilyMethods.cs"),
                new MethodDefinition("placeFamilyInstance", 2, "family", "Places a family instance", "FamilyMethods.cs"),

                // Parameter Methods (Tier 1)
                new MethodDefinition("getParameter", 1, "parameter", "Gets element parameter value", "ParameterMethods.cs"),
                new MethodDefinition("setParameter", 1, "parameter", "Sets element parameter value", "ParameterMethods.cs"),

                // Document Methods (Tier 1)
                new MethodDefinition("getProjectInfo", 1, "document", "Gets project information", "DocumentMethods.cs"),
                new MethodDefinition("getLevels", 1, "document", "Gets all levels", "DocumentMethods.cs"),
                new MethodDefinition("getElements", 1, "document", "Gets elements by category", "DocumentMethods.cs"),

                // Library Methods (Tier 2)
                new MethodDefinition("searchLibrary", 2, "library", "Searches the detail library", "LibraryMethods.cs"),
                new MethodDefinition("loadLibraryFamily", 2, "library", "Loads family from library", "LibraryMethods.cs"),
                new MethodDefinition("insertLibraryView", 2, "library", "Inserts view from library", "LibraryMethods.cs"),

                // Capability Methods (Tier 2-3)
                new MethodDefinition("classifyFailure", 2, "capability", "Classifies task failures", "CapabilityMethods.cs"),
                new MethodDefinition("proposeToolSpec", 2, "capability", "Proposes new tool spec", "CapabilityMethods.cs"),
                new MethodDefinition("approveToolSpec", 2, "capability", "Approves tool spec", "CapabilityMethods.cs"),
                new MethodDefinition("getMethodRegistry", 2, "capability", "Gets method registry", "CapabilityMethods.cs"),
                new MethodDefinition("createTestArtifact", 3, "capability", "Creates test artifact", "CapabilityMethods.cs"),
                new MethodDefinition("runTest", 3, "capability", "Runs a test", "CapabilityMethods.cs")
            };
        }

        private static string FindTestFolder(string testId, string methodName)
        {
            var testsPath = Path.Combine(MethodGymPath, "tests");

            if (!string.IsNullOrEmpty(methodName))
            {
                var path = Path.Combine(testsPath, methodName, testId);
                return Directory.Exists(path) ? path : null;
            }

            // Search all method folders
            if (Directory.Exists(testsPath))
            {
                foreach (var methodFolder in Directory.GetDirectories(testsPath))
                {
                    var path = Path.Combine(methodFolder, testId);
                    if (Directory.Exists(path)) return path;
                }
            }
            return null;
        }

        private static List<JObject> ValidateAssertions(JObject expectedOutcome, JObject actualResult,
            JObject initialState = null, JObject finalState = null)
        {
            var results = new List<JObject>();

            // Basic success check
            var expectedSuccess = expectedOutcome?["success"]?.Value<bool>() ?? true;
            var actualSuccess = actualResult?["success"]?.Value<bool>() ?? false;

            results.Add(new JObject
            {
                ["assertion"] = new JObject { ["type"] = "successEquals" },
                ["passed"] = expectedSuccess == actualSuccess,
                ["expected"] = expectedSuccess,
                ["actual"] = actualSuccess,
                ["message"] = expectedSuccess == actualSuccess ? "Success check passed" : $"Expected success={expectedSuccess}, got {actualSuccess}"
            });

            // Check specific assertions from expectedOutcome
            var assertions = expectedOutcome?["assertions"] as JArray;
            if (assertions != null)
            {
                foreach (JObject assertion in assertions)
                {
                    var type = assertion["type"]?.ToString();
                    var target = assertion["target"]?.ToString();
                    var expected = assertion["expected"];

                    switch (type)
                    {
                        case "elementCreated":
                            var created = FindNewElements(initialState, finalState);
                            results.Add(new JObject
                            {
                                ["assertion"] = assertion,
                                ["passed"] = created.Count > 0,
                                ["actual"] = created.Count,
                                ["message"] = created.Count > 0 ? $"{created.Count} elements created" : "No elements created"
                            });
                            break;

                        case "countEquals":
                            var actualCount = actualResult?[target]?.Value<int>() ?? 0;
                            var expectedCount = expected?.Value<int>() ?? 0;
                            results.Add(new JObject
                            {
                                ["assertion"] = assertion,
                                ["passed"] = actualCount == expectedCount,
                                ["expected"] = expectedCount,
                                ["actual"] = actualCount,
                                ["message"] = actualCount == expectedCount ? "Count matches" : $"Expected {expectedCount}, got {actualCount}"
                            });
                            break;

                        case "parameterEquals":
                            var actualValue = actualResult?[target]?.ToString();
                            var expectedValue = expected?.ToString();
                            results.Add(new JObject
                            {
                                ["assertion"] = assertion,
                                ["passed"] = actualValue == expectedValue,
                                ["expected"] = expectedValue,
                                ["actual"] = actualValue,
                                ["message"] = actualValue == expectedValue ? "Parameter matches" : $"Expected {expectedValue}, got {actualValue}"
                            });
                            break;

                        case "noError":
                            var hasError = actualResult?["error"] != null;
                            results.Add(new JObject
                            {
                                ["assertion"] = assertion,
                                ["passed"] = !hasError,
                                ["actual"] = actualResult?["error"],
                                ["message"] = hasError ? $"Error occurred: {actualResult?["error"]}" : "No errors"
                            });
                            break;
                    }
                }
            }

            return results;
        }

        private static JObject CaptureModelState(UIApplication uiApp)
        {
            try
            {
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null) return new JObject { ["error"] = "No active document" };

                // Capture element counts by category
                var elementCounts = new JObject();
                var categories = new[] { "Walls", "Doors", "Windows", "Rooms", "Views", "Sheets" };

                foreach (var catName in categories)
                {
                    try
                    {
                        var bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), $"OST_{catName}");
                        var collector = new FilteredElementCollector(doc)
                            .OfCategory(bic)
                            .WhereElementIsNotElementType();
                        elementCounts[catName] = collector.GetElementCount();
                    }
                    catch { }
                }

                return new JObject
                {
                    ["timestamp"] = DateTime.Now.ToString("o"),
                    ["projectName"] = doc.Title,
                    ["elementCounts"] = elementCounts,
                    ["activeViewId"] = uiApp.ActiveUIDocument?.ActiveView?.Id?.Value
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message };
            }
        }

        private static JArray FindNewElements(JObject initialState, JObject finalState)
        {
            var newElements = new JArray();

            // Compare element counts - simplified for now
            // In full implementation, would track specific element IDs
            var initialCounts = initialState?["elementCounts"] as JObject ?? new JObject();
            var finalCounts = finalState?["elementCounts"] as JObject ?? new JObject();

            foreach (var prop in finalCounts.Properties())
            {
                var initialCount = initialCounts[prop.Name]?.Value<int>() ?? 0;
                var finalCount = prop.Value.Value<int>();
                if (finalCount > initialCount)
                {
                    newElements.Add(new JObject
                    {
                        ["category"] = prop.Name,
                        ["count"] = finalCount - initialCount
                    });
                }
            }

            return newElements;
        }

        private static JArray FindModifiedElements(JObject initialState, JObject finalState)
        {
            // Placeholder - would track parameter changes in full implementation
            return new JArray();
        }

        private static string GenerateCSharpMethod(JObject methodInfo, JObject inputs, JArray algorithm,
            JObject validation, JArray failureModes)
        {
            var name = methodInfo["name"]?.ToString() ?? "newMethod";
            var pascalName = char.ToUpper(name[0]) + name.Substring(1);
            var intent = methodInfo["intent"]?.ToString() ?? "TODO: Add description";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// {intent}");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public static string {pascalName}(UIApplication uiApp, JObject parameters)");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            try");
            sb.AppendLine($"            {{");
            sb.AppendLine($"                var doc = uiApp.ActiveUIDocument.Document;");
            sb.AppendLine();

            // Generate parameter extraction
            foreach (var prop in inputs.Properties())
            {
                var paramName = prop.Name;
                var paramInfo = prop.Value as JObject;
                var paramType = paramInfo?["type"]?.ToString() ?? "string";
                var required = paramInfo?["required"]?.Value<bool>() ?? false;
                var defaultVal = paramInfo?["default"];

                if (required)
                {
                    sb.AppendLine($"                if (parameters?[\"{paramName}\"] == null)");
                    sb.AppendLine($"                    return JsonConvert.SerializeObject(new {{ success = false, error = \"{paramName} is required\" }});");
                }

                switch (paramType.ToLower())
                {
                    case "int":
                    case "long":
                    case "elementid":
                        sb.AppendLine($"                var {paramName} = parameters?[\"{paramName}\"]?.Value<long>() ?? {defaultVal ?? "0"};");
                        break;
                    case "double":
                        sb.AppendLine($"                var {paramName} = parameters?[\"{paramName}\"]?.Value<double>() ?? {defaultVal ?? "0.0"};");
                        break;
                    case "bool":
                        sb.AppendLine($"                var {paramName} = parameters?[\"{paramName}\"]?.Value<bool>() ?? {defaultVal?.ToString().ToLower() ?? "false"};");
                        break;
                    default:
                        sb.AppendLine($"                var {paramName} = parameters?[\"{paramName}\"]?.ToString(){(defaultVal != null ? $" ?? \"{defaultVal}\"" : "")};");
                        break;
                }
            }

            sb.AppendLine();
            sb.AppendLine($"                using (var trans = new Transaction(doc, \"{pascalName}\"))");
            sb.AppendLine($"                {{");
            sb.AppendLine($"                    trans.Start();");
            sb.AppendLine();

            // Add algorithm as TODO comments
            foreach (var step in algorithm)
            {
                sb.AppendLine($"                    // TODO: {step}");
            }

            sb.AppendLine();
            sb.AppendLine($"                    trans.Commit();");
            sb.AppendLine($"                }}");
            sb.AppendLine();
            sb.AppendLine($"                return JsonConvert.SerializeObject(new");
            sb.AppendLine($"                {{");
            sb.AppendLine($"                    success = true,");
            sb.AppendLine($"                    message = \"{pascalName} completed\"");
            sb.AppendLine($"                }});");
            sb.AppendLine($"            }}");
            sb.AppendLine($"            catch (Exception ex)");
            sb.AppendLine($"            {{");
            sb.AppendLine($"                return JsonConvert.SerializeObject(new {{ success = false, error = ex.Message }});");
            sb.AppendLine($"            }}");
            sb.AppendLine($"        }}");

            return sb.ToString();
        }

        private static string GenerateSwitchCase(string methodName, string category)
        {
            var pascalName = char.ToUpper(methodName[0]) + methodName.Substring(1);
            var className = GetClassNameForCategory(category);
            return $"                    case \"{methodName}\":\n                        return await ExecuteInRevitContext(uiApp => {className}.{pascalName}(uiApp, parameters));";
        }

        private static string GetClassNameForCategory(string category)
        {
            switch (category?.ToLower())
            {
                case "wall": return "WallMethods";
                case "door_window": return "DoorWindowMethods";
                case "room": return "RoomMethods";
                case "view": return "ViewMethods";
                case "sheet": return "SheetMethods";
                case "schedule": return "ScheduleMethods";
                case "family": return "FamilyMethods";
                case "parameter": return "ParameterMethods";
                case "structural": return "StructuralMethods";
                case "mep": return "MEPMethods";
                case "capability": return "CapabilityMethods";
                default: return "NewMethods";
            }
        }

        private static JObject GenerateTestTemplate(JObject methodInfo, JObject inputs)
        {
            var methodName = methodInfo["name"]?.ToString();
            var testParams = new JObject();

            foreach (var prop in inputs.Properties())
            {
                var paramInfo = prop.Value as JObject;
                var paramType = paramInfo?["type"]?.ToString() ?? "string";
                var defaultVal = paramInfo?["default"];

                switch (paramType.ToLower())
                {
                    case "int":
                    case "long":
                    case "elementid":
                        testParams[prop.Name] = defaultVal?.Value<long>() ?? 0;
                        break;
                    case "double":
                        testParams[prop.Name] = defaultVal?.Value<double>() ?? 0.0;
                        break;
                    case "bool":
                        testParams[prop.Name] = defaultVal?.Value<bool>() ?? false;
                        break;
                    default:
                        testParams[prop.Name] = defaultVal?.ToString() ?? "TODO";
                        break;
                }
            }

            return new JObject
            {
                ["testId"] = $"{methodName}_{DateTime.Now:yyyyMMdd_HHmmss}",
                ["methodName"] = methodName,
                ["triggerType"] = "new_implementation",
                ["taskRequest"] = new JObject
                {
                    ["method"] = methodName,
                    ["params"] = testParams,
                    ["userIntent"] = methodInfo["intent"]
                },
                ["expectedOutcome"] = new JObject
                {
                    ["success"] = true,
                    ["assertions"] = new JArray
                    {
                        new JObject { ["type"] = "noError" }
                    }
                }
            };
        }

        private static int CountFilesInFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;
            return Directory.GetFiles(folderPath, "*.json").Length;
        }

        #endregion

        #region Data Classes

        private class FailureClassification
        {
            public string Type { get; set; } = "UNKNOWN";
            public string SubType { get; set; }
            public double Confidence { get; set; } = 0.5;
            public string Reasoning { get; set; }
            public JObject Analysis { get; set; } = new JObject();
            public JObject RecommendedAction { get; set; } = new JObject();
        }

        private class MethodDefinition
        {
            public string Name { get; set; }
            public int Tier { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
            public string SourceFile { get; set; }

            public MethodDefinition(string name, int tier, string category, string description, string sourceFile)
            {
                Name = name;
                Tier = tier;
                Category = category;
                Description = description;
                SourceFile = sourceFile;
            }
        }

        #endregion
    }
}
