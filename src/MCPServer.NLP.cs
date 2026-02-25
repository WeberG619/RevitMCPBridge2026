using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// MCPServer partial class - Natural Language Processing Methods
    /// Extracted from main MCPServer.cs for better code organization.
    /// </summary>
    public partial class MCPServer
    {
        #region Natural Language Processing Methods

        // Pending confirmation commands (keyed by confirmation token)
        private static Dictionary<string, SafeCommandProcessor.ProcessedCommand> _pendingConfirmations =
            new Dictionary<string, SafeCommandProcessor.ProcessedCommand>();
        private static SafeCommandProcessor _nlpProcessor;

        /// <summary>
        /// Process natural language input using SafeCommandProcessor
        /// </summary>
        private async Task<string> ProcessNaturalLanguageInput(JObject parameters)
        {
            try
            {
                var input = parameters?["input"]?.ToString();
                var execute = parameters?["execute"]?.ToObject<bool>() ?? false;
                var knowledgeContext = parameters?["context"]?.ToString();

                if (string.IsNullOrWhiteSpace(input))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Input is required"
                    });
                }

                // Initialize processor if needed
                if (_nlpProcessor == null)
                {
                    _nlpProcessor = new SafeCommandProcessor();
                }

                // Inject knowledge context if provided
                if (!string.IsNullOrEmpty(knowledgeContext))
                {
                    _nlpProcessor.InjectKnowledge(knowledgeContext);
                }

                // Process the input
                var command = await _nlpProcessor.ProcessInputAsync(input);

                // If clarification needed, return it
                if (!string.IsNullOrEmpty(command.ClarificationNeeded))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        needsClarification = true,
                        clarification = command.ClarificationNeeded,
                        partialData = new
                        {
                            intent = command.Intent.ToString(),
                            confidence = command.Confidence,
                            entities = command.Entities
                        }
                    });
                }

                // If requires confirmation, store and return for user approval
                if (command.RequiresConfirmation)
                {
                    var confirmationToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                    _pendingConfirmations[confirmationToken] = command;

                    // Clean old confirmations (older than 5 minutes)
                    CleanOldConfirmations();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        requiresConfirmation = true,
                        confirmationToken = confirmationToken,
                        proposedCommand = new
                        {
                            method = command.Method,
                            description = command.Description,
                            affectedCount = command.AffectedCount,
                            parameters = command.Parameters,
                            confidence = command.Confidence,
                            intent = command.Intent.ToString()
                        },
                        message = $"This command will {command.Description}. Reply with 'confirmNaturalLanguageCommand' and the token to proceed."
                    });
                }

                // If execute=false, just return the processed command for review
                if (!execute)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        processed = true,
                        command = new
                        {
                            method = command.Method,
                            description = command.Description,
                            parameters = command.Parameters,
                            confidence = command.Confidence,
                            intent = command.Intent.ToString()
                        },
                        message = "Command processed. Set execute=true to run, or call the method directly."
                    });
                }

                // Execute the command
                return await ExecuteProcessedCommand(command);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing natural language input");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Confirm and execute a pending natural language command
        /// </summary>
        private async Task<string> ConfirmAndExecuteNaturalLanguageCommand(JObject parameters)
        {
            try
            {
                var token = parameters?["token"]?.ToString();

                if (string.IsNullOrEmpty(token))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Confirmation token is required"
                    });
                }

                if (!_pendingConfirmations.TryGetValue(token, out var command))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Confirmation token not found or expired. Please process the command again."
                    });
                }

                // Remove from pending
                _pendingConfirmations.Remove(token);

                // Execute the confirmed command
                return await ExecuteProcessedCommand(command);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error confirming natural language command");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Execute a processed command by calling the appropriate MCP method
        /// </summary>
        private async Task<string> ExecuteProcessedCommand(SafeCommandProcessor.ProcessedCommand command)
        {
            try
            {
                if (string.IsNullOrEmpty(command.Method))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No method specified in processed command"
                    });
                }

                // Build the request as if it came from external caller
                var request = new JObject
                {
                    ["method"] = command.Method,
                    ["params"] = command.Parameters ?? new JObject()
                };

                // Process it through the normal dispatch (recursive call to ProcessMessage)
                var result = await ProcessMessage(request.ToString());

                // Parse result to add context about NLP processing
                try
                {
                    var resultObj = JObject.Parse(result);
                    resultObj["nlpContext"] = new JObject
                    {
                        ["originalIntent"] = command.Intent.ToString(),
                        ["confidence"] = command.Confidence,
                        ["method"] = command.Method,
                        ["description"] = command.Description
                    };
                    return resultObj.ToString();
                }
                catch
                {
                    return result; // Return as-is if we can't parse
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing processed command");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get NLP system status
        /// </summary>
        private string GetNLPStatus()
        {
            try
            {
                bool ollamaAvailable = false;
                string ollamaModel = "qwen2.5:7b";

                // Quick check if Ollama is running
                try
                {
                    using (var client = new System.Net.Http.HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(2);
                        var response = client.GetAsync("http://localhost:11434/api/tags").Result;
                        ollamaAvailable = response.IsSuccessStatusCode;
                    }
                }
                catch { }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        processorInitialized = _nlpProcessor != null,
                        ollamaAvailable = ollamaAvailable,
                        defaultModel = ollamaModel,
                        pendingConfirmations = _pendingConfirmations.Count,
                        supportedIntents = new[] { "Create", "Find", "Modify", "Delete", "Question", "List" },
                        features = new
                        {
                            spellCorrection = true,
                            fuzzyMatching = true,
                            intentClassification = true,
                            entityExtraction = true,
                            confirmationForDangerousOps = true,
                            safeDefaults = true,
                            knowledgeInjection = true
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Clean confirmation tokens older than 5 minutes
        /// </summary>
        private void CleanOldConfirmations()
        {
            // For simplicity, just limit to 50 pending confirmations
            if (_pendingConfirmations.Count > 50)
            {
                // Remove oldest half
                var toRemove = _pendingConfirmations.Keys.Take(25).ToList();
                foreach (var key in toRemove)
                {
                    _pendingConfirmations.Remove(key);
                }
            }
        }

        #endregion
    }
}
