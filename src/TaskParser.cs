using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Parses natural language task descriptions into structured BatchTask objects.
    /// Enables users to describe what they want in plain English.
    /// </summary>
    public static class TaskParser
    {
        // Pattern definitions for common task types
        private static readonly Dictionary<string, TaskPattern> _patterns = new Dictionary<string, TaskPattern>
        {
            // Sheet operations
            { "createSheets", new TaskPattern
                {
                    Method = "createSheet",
                    Patterns = new[]
                    {
                        @"create\s+(\d+)\s+sheets?\s+(?:for\s+)?(.+)",
                        @"add\s+(\d+)\s+sheets?\s+(?:for\s+)?(.+)",
                        @"make\s+(\d+)\s+sheets?\s+(?:for\s+)?(.+)",
                        @"create\s+(?:a\s+)?sheet\s+(?:for\s+)?(.+)",
                        @"add\s+(?:a\s+)?sheet\s+(?:for\s+)?(.+)"
                    },
                    Extractor = ExtractSheetParams
                }
            },

            // Wall operations
            { "createWalls", new TaskPattern
                {
                    Method = "createWallByPoints",
                    Patterns = new[]
                    {
                        @"create\s+(?:a\s+)?wall\s+from\s+\(?([\d.,-]+)\)?\s+to\s+\(?([\d.,-]+)\)?",
                        @"draw\s+(?:a\s+)?wall\s+from\s+\(?([\d.,-]+)\)?\s+to\s+\(?([\d.,-]+)\)?",
                        @"add\s+(?:a\s+)?wall\s+from\s+\(?([\d.,-]+)\)?\s+to\s+\(?([\d.,-]+)\)?",
                        @"create\s+(\d+)\s+walls?\s+(.+)"
                    },
                    Extractor = ExtractWallParams
                }
            },

            // Door operations
            { "placeDoors", new TaskPattern
                {
                    Method = "placeDoor",
                    Patterns = new[]
                    {
                        @"place\s+(?:a\s+)?door\s+on\s+wall\s+(\d+)",
                        @"add\s+(?:a\s+)?door\s+(?:to|on)\s+wall\s+(\d+)",
                        @"put\s+(?:a\s+)?door\s+(?:in|on)\s+wall\s+(\d+)",
                        @"place\s+(?:a\s+)?(\d+)['""x]?\s*x?\s*(\d+)['""x]?\s+door\s+on\s+wall\s+(\d+)"
                    },
                    Extractor = ExtractDoorParams
                }
            },

            // Window operations
            { "placeWindows", new TaskPattern
                {
                    Method = "placeWindow",
                    Patterns = new[]
                    {
                        @"place\s+(?:a\s+)?window\s+on\s+wall\s+(\d+)",
                        @"add\s+(?:a\s+)?window\s+(?:to|on)\s+wall\s+(\d+)",
                        @"place\s+(?:a\s+)?(\d+)['""x]?\s*x?\s*(\d+)['""x]?\s+window\s+on\s+wall\s+(\d+)"
                    },
                    Extractor = ExtractWindowParams
                }
            },

            // Room operations
            { "createRooms", new TaskPattern
                {
                    Method = "createRoom",
                    Patterns = new[]
                    {
                        @"create\s+(?:a\s+)?room\s+(?:named\s+)?[""']?(.+?)[""']?\s+at\s+\(?([\d.,-]+)\)?",
                        @"add\s+(?:a\s+)?room\s+(?:called\s+)?[""']?(.+?)[""']?\s+at\s+\(?([\d.,-]+)\)?",
                        @"create\s+(?:a\s+)?(.+?)\s+room\s+at\s+\(?([\d.,-]+)\)?"
                    },
                    Extractor = ExtractRoomParams
                }
            },

            // View placement
            { "placeViews", new TaskPattern
                {
                    Method = "placeViewOnSheetSmart",
                    Patterns = new[]
                    {
                        @"place\s+view\s+(\d+)\s+on\s+sheet\s+(\d+)",
                        @"add\s+view\s+(\d+)\s+to\s+sheet\s+(\d+)",
                        @"put\s+(.+?)\s+view\s+on\s+sheet\s+(.+)"
                    },
                    Extractor = ExtractViewPlacementParams
                }
            },

            // Text notes
            { "addText", new TaskPattern
                {
                    Method = "placeTextNote",
                    Patterns = new[]
                    {
                        @"add\s+text\s+[""'](.+?)[""']\s+at\s+\(?([\d.,-]+)\)?",
                        @"place\s+text\s+[""'](.+?)[""']\s+at\s+\(?([\d.,-]+)\)?",
                        @"write\s+[""'](.+?)[""']\s+at\s+\(?([\d.,-]+)\)?"
                    },
                    Extractor = ExtractTextParams
                }
            },

            // Delete operations
            { "deleteElements", new TaskPattern
                {
                    Method = "deleteElements",
                    Patterns = new[]
                    {
                        @"delete\s+elements?\s+([\d,\s]+)",
                        @"remove\s+elements?\s+([\d,\s]+)",
                        @"delete\s+all\s+(.+)\s+on\s+(.+)"
                    },
                    Extractor = ExtractDeleteParams
                }
            },

            // Batch operations
            { "batchWalls", new TaskPattern
                {
                    Method = "batchCreateWalls",
                    Patterns = new[]
                    {
                        @"create\s+walls?\s+for\s+(?:a\s+)?room\s+(?:of\s+)?(\d+)['""x]?\s*x?\s*(\d+)['""x]?",
                        @"build\s+(?:a\s+)?(\d+)['""x]?\s*x?\s*(\d+)['""x]?\s+room",
                        @"create\s+(?:a\s+)?rectangular\s+room\s+(\d+)['""x]?\s*x?\s*(\d+)['""x]?"
                    },
                    Extractor = ExtractBatchWallParams
                }
            }
        };

        /// <summary>
        /// Parse natural language input into batch tasks
        /// </summary>
        public static string ParseTasks(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var input = parameters["input"]?.ToString();
                if (string.IsNullOrWhiteSpace(input))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "input text is required"
                    });
                }

                // Split input into lines/sentences for multiple tasks
                var lines = SplitIntoTasks(input);
                var tasks = new List<BatchProcessor.BatchTask>();
                var unrecognized = new List<string>();

                int taskId = 1;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    var parsedTasks = ParseSingleTask(trimmed, ref taskId);
                    if (parsedTasks.Count > 0)
                    {
                        tasks.AddRange(parsedTasks);
                    }
                    else
                    {
                        unrecognized.Add(trimmed);
                    }
                }

                if (tasks.Count == 0 && unrecognized.Count > 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not parse any tasks from input",
                        unrecognizedLines = unrecognized,
                        hint = "Try phrases like: 'Create 5 sheets for floor plans', 'Place a door on wall 12345', 'Create a wall from (0,0) to (10,0)'"
                    });
                }

                // Create batch if requested
                var createBatch = parameters["createBatch"]?.Value<bool>() ?? true;
                if (createBatch && tasks.Count > 0)
                {
                    var batchParams = new JObject
                    {
                        ["name"] = parameters["batchName"]?.ToString() ?? "Parsed Tasks",
                        ["description"] = $"Auto-generated from: {input.Substring(0, Math.Min(50, input.Length))}...",
                        ["tasks"] = JArray.FromObject(tasks),
                        ["onError"] = parameters["onError"]?.ToString() ?? "LogAndContinue"
                    };

                    return BatchProcessor.CreateBatch(uiApp, batchParams);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tasksCount = tasks.Count,
                    tasks = tasks.Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        method = t.Method,
                        parameters = t.Parameters
                    }).ToList(),
                    unrecognizedLines = unrecognized.Count > 0 ? unrecognized : null,
                    message = $"Parsed {tasks.Count} tasks from input"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Split input into individual task lines
        /// </summary>
        private static List<string> SplitIntoTasks(string input)
        {
            var tasks = new List<string>();

            // Split by common delimiters
            var separators = new[] { "\n", "\r\n", ";", " and then ", " then ", ". " };
            var current = new List<string> { input };

            foreach (var sep in separators)
            {
                var newList = new List<string>();
                foreach (var item in current)
                {
                    newList.AddRange(item.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries));
                }
                current = newList;
            }

            return current.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        /// <summary>
        /// Parse a single task line into BatchTask(s)
        /// </summary>
        private static List<BatchProcessor.BatchTask> ParseSingleTask(string line, ref int taskId)
        {
            var tasks = new List<BatchProcessor.BatchTask>();
            var lowered = line.ToLower();

            foreach (var kvp in _patterns)
            {
                var pattern = kvp.Value;

                foreach (var regex in pattern.Patterns)
                {
                    var match = Regex.Match(lowered, regex, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var extractedTasks = pattern.Extractor(match, pattern.Method, ref taskId, line);
                        if (extractedTasks.Count > 0)
                        {
                            tasks.AddRange(extractedTasks);
                            return tasks; // Found a match, return
                        }
                    }
                }
            }

            return tasks;
        }

        #region Parameter Extractors

        private static List<BatchProcessor.BatchTask> ExtractSheetParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            // Check for count + description pattern (e.g., "create 5 sheets for floor plans")
            if (match.Groups.Count >= 3 && int.TryParse(match.Groups[1].Value, out int count))
            {
                var description = match.Groups[2].Value.Trim();
                var prefix = GetSheetPrefix(description);

                for (int i = 1; i <= count; i++)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Create sheet {prefix}-{i}",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["sheetNumber"] = $"{prefix}-{i}",
                            ["sheetName"] = $"{description} {i}"
                        }
                    });
                }
            }
            // Single sheet pattern (e.g., "create a sheet for cover page")
            else if (match.Groups.Count >= 2)
            {
                var description = match.Groups[1].Value.Trim();
                var prefix = GetSheetPrefix(description);

                tasks.Add(new BatchProcessor.BatchTask
                {
                    Id = taskId++,
                    Name = $"Create sheet for {description}",
                    Method = method,
                    Parameters = new JObject
                    {
                        ["sheetNumber"] = $"{prefix}-1",
                        ["sheetName"] = description
                    }
                });
            }

            return tasks;
        }

        private static string GetSheetPrefix(string description)
        {
            var desc = description.ToLower();
            if (desc.Contains("floor") || desc.Contains("plan")) return "A1";
            if (desc.Contains("elevation")) return "A2";
            if (desc.Contains("section")) return "A3";
            if (desc.Contains("detail")) return "A5";
            if (desc.Contains("schedule")) return "A6";
            if (desc.Contains("cover")) return "G0";
            if (desc.Contains("site")) return "C1";
            if (desc.Contains("structural")) return "S1";
            if (desc.Contains("mechanical") || desc.Contains("hvac")) return "M1";
            if (desc.Contains("electrical")) return "E1";
            if (desc.Contains("plumbing")) return "P1";
            return "A1"; // Default
        }

        private static List<BatchProcessor.BatchTask> ExtractWallParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            // Pattern: create wall from (x1,y1) to (x2,y2)
            if (match.Groups.Count >= 3)
            {
                var start = ParseCoordinates(match.Groups[1].Value);
                var end = ParseCoordinates(match.Groups[2].Value);

                if (start != null && end != null)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Create wall from ({start[0]},{start[1]}) to ({end[0]},{end[1]})",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["startX"] = start[0],
                            ["startY"] = start[1],
                            ["endX"] = end[0],
                            ["endY"] = end[1],
                            ["height"] = 10.0 // Default height
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractDoorParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            // Pattern: place door on wall 12345
            if (match.Groups.Count >= 2)
            {
                int wallId = 0;
                double width = 3.0;  // Default 3'
                double height = 7.0; // Default 7'

                // Check for dimension pattern first
                if (match.Groups.Count >= 4)
                {
                    double.TryParse(match.Groups[1].Value, out width);
                    double.TryParse(match.Groups[2].Value, out height);
                    int.TryParse(match.Groups[3].Value, out wallId);
                }
                else
                {
                    int.TryParse(match.Groups[1].Value, out wallId);
                }

                if (wallId > 0)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Place {width}' x {height}' door on wall {wallId}",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["hostWallId"] = wallId,
                            ["location"] = 0.5 // Center of wall
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractWindowParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            if (match.Groups.Count >= 2)
            {
                int wallId = 0;
                int.TryParse(match.Groups[match.Groups.Count - 1].Value, out wallId);

                if (wallId > 0)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Place window on wall {wallId}",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["hostWallId"] = wallId,
                            ["location"] = 0.5,
                            ["sillHeight"] = 3.0 // Default sill height
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractRoomParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            if (match.Groups.Count >= 3)
            {
                var roomName = match.Groups[1].Value.Trim();
                var coords = ParseCoordinates(match.Groups[2].Value);

                if (coords != null)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Create room '{roomName}'",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["roomName"] = roomName,
                            ["x"] = coords[0],
                            ["y"] = coords[1]
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractViewPlacementParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            if (match.Groups.Count >= 3)
            {
                int viewId = 0;
                int sheetId = 0;

                int.TryParse(match.Groups[1].Value, out viewId);
                int.TryParse(match.Groups[2].Value, out sheetId);

                if (viewId > 0 && sheetId > 0)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Place view {viewId} on sheet {sheetId}",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["viewId"] = viewId,
                            ["sheetId"] = sheetId
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractTextParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            if (match.Groups.Count >= 3)
            {
                var text = match.Groups[1].Value;
                var coords = ParseCoordinates(match.Groups[2].Value);

                if (coords != null)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Add text note",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["text"] = text,
                            ["x"] = coords[0],
                            ["y"] = coords[1]
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractDeleteParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            if (match.Groups.Count >= 2)
            {
                var idsText = match.Groups[1].Value;
                var ids = idsText.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => { int.TryParse(s.Trim(), out int id); return id; })
                    .Where(id => id > 0)
                    .ToArray();

                if (ids.Length > 0)
                {
                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Delete {ids.Length} element(s)",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["elementIds"] = JArray.FromObject(ids)
                        }
                    });
                }
            }

            return tasks;
        }

        private static List<BatchProcessor.BatchTask> ExtractBatchWallParams(Match match, string method, ref int taskId, string originalLine)
        {
            var tasks = new List<BatchProcessor.BatchTask>();

            if (match.Groups.Count >= 3)
            {
                double width = 0, length = 0;
                double.TryParse(match.Groups[1].Value, out width);
                double.TryParse(match.Groups[2].Value, out length);

                if (width > 0 && length > 0)
                {
                    // Create 4 walls for a rectangular room
                    var walls = new JArray
                    {
                        new JObject { ["startX"] = 0, ["startY"] = 0, ["endX"] = width, ["endY"] = 0 },
                        new JObject { ["startX"] = width, ["startY"] = 0, ["endX"] = width, ["endY"] = length },
                        new JObject { ["startX"] = width, ["startY"] = length, ["endX"] = 0, ["endY"] = length },
                        new JObject { ["startX"] = 0, ["startY"] = length, ["endX"] = 0, ["endY"] = 0 }
                    };

                    tasks.Add(new BatchProcessor.BatchTask
                    {
                        Id = taskId++,
                        Name = $"Create {width}' x {length}' room walls",
                        Method = method,
                        Parameters = new JObject
                        {
                            ["walls"] = walls,
                            ["height"] = 10.0
                        }
                    });
                }
            }

            return tasks;
        }

        #endregion

        #region Utilities

        private static double[] ParseCoordinates(string coordString)
        {
            // Parse formats: "x,y" or "x, y" or "x y"
            var parts = coordString.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                if (double.TryParse(parts[0], out double x) && double.TryParse(parts[1], out double y))
                {
                    return new[] { x, y };
                }
            }
            return null;
        }

        #endregion

        /// <summary>
        /// Pattern definition for task parsing
        /// </summary>
        private class TaskPattern
        {
            public string Method { get; set; }
            public string[] Patterns { get; set; }
            public TaskExtractor Extractor { get; set; }
        }

        /// <summary>
        /// Delegate for task extraction with ref parameter support
        /// </summary>
        private delegate List<BatchProcessor.BatchTask> TaskExtractor(Match match, string method, ref int taskId, string originalLine);

        /// <summary>
        /// Get list of supported patterns and examples
        /// </summary>
        public static string GetSupportedPatterns(UIApplication uiApp, JObject parameters)
        {
            var examples = new Dictionary<string, string[]>
            {
                ["Sheets"] = new[]
                {
                    "Create 5 sheets for floor plans",
                    "Add a sheet for cover page",
                    "Create 3 sheets for elevations"
                },
                ["Walls"] = new[]
                {
                    "Create a wall from (0,0) to (20,0)",
                    "Create walls for a room of 12' x 14'",
                    "Build a 15' x 20' room"
                },
                ["Doors"] = new[]
                {
                    "Place a door on wall 12345",
                    "Add a 3' x 7' door on wall 67890"
                },
                ["Windows"] = new[]
                {
                    "Place a window on wall 12345",
                    "Add a 4' x 5' window on wall 67890"
                },
                ["Rooms"] = new[]
                {
                    "Create a room named 'Living Room' at (10,10)",
                    "Add a bedroom room at (25, 15)"
                },
                ["Views"] = new[]
                {
                    "Place view 12345 on sheet 67890"
                },
                ["Text"] = new[]
                {
                    "Add text 'Note: See detail' at (5,5)"
                },
                ["Delete"] = new[]
                {
                    "Delete elements 12345, 67890, 11111"
                }
            };

            return JsonConvert.SerializeObject(new
            {
                success = true,
                supportedPatterns = examples,
                usage = "Use parseTasks with { \"input\": \"your natural language description\" } to convert to batch tasks"
            });
        }
    }
}
