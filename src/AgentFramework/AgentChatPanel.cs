using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.IO.Pipes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.UI;
using RevitMCPBridge; // For VerificationResult
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026.AgentFramework
{
    /// <summary>
    /// AI Assistant Chat Panel - Built entirely in code for Revit compatibility
    /// This provides the same power as Claude Code but in a visual UI
    /// </summary>
    public class AgentChatPanel : Window
    {
        // UI Elements
        private TextBlock _statusText;
        private StackPanel _chatHistory;
        private ScrollViewer _chatScrollViewer;
        private System.Windows.Controls.TextBox _inputTextBox;
        private Button _sendButton;
        private Button _stopButton;
        private Border _progressPanel;
        private TextBlock _progressTitle;
        private TextBlock _progressDetail;

        // Agent
        private AgentCore _agent;
        private UIApplication _uiApp;
        private string _apiKey;
        private string _selectedModel;
        private bool _isProcessing;

        // Available models with pricing info
        private static readonly Dictionary<string, string> AvailableModels = new Dictionary<string, string>
        {
            { "claude-opus-4-5-20251101", "Opus 4.5 - Smartest ($15/$75 per 1M tokens)" },
            { "claude-sonnet-4-20250514", "Sonnet 4 - Balanced ($3/$15 per 1M tokens)" },
            { "claude-3-5-haiku-20241022", "Haiku 3.5 - Cheapest ($0.80/$4 per 1M tokens)" }
        };

        // Persistent MCP connection
        private NamedPipeClientStream _mcpPipe;
        private StreamReader _mcpReader;
        private StreamWriter _mcpWriter;
        private readonly object _pipeLock = new object();

        // Feedback tracking - what was the last action for thumbs up/down
        private string _lastUserMessage;
        private string _lastAssistantResponse;
        private string _lastToolCall;
        private int _feedbackMessageIndex = 0;

        public AgentChatPanel(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // Initialize project name for session tracking
            _sessionProjectName = uiApp?.ActiveUIDocument?.Document?.Title ?? "Unknown";

            // Window setup
            Title = "BIM Ops Studio - AI Assistant";
            Width = 500;
            Height = 700;
            MinWidth = 350;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));

            // Build UI
            BuildUI();

            // Load config (API key and model selection)
            LoadConfig();

            if (string.IsNullOrEmpty(_apiKey))
            {
                ShowSettingsDialog();
            }
            else
            {
                InitializeAgent();
            }

            // Check for previous session
            var previousSession = LoadSession();
            bool sessionRestored = false;

            if (previousSession != null && previousSession.Messages.Count > 0)
            {
                if (AskToContinueSession(previousSession))
                {
                    RestoreSession(previousSession);
                    sessionRestored = true;
                    AddAssistantMessage("Welcome back! I remember our previous conversation. What would you like to continue working on?");
                }
            }

            if (!sessionRestored)
            {
                // Welcome message for new session
                AddAssistantMessage("Hello! I'm your Revit AI assistant. I can help you with:\n\n" +
                    "• Placing and organizing annotations\n" +
                    "• Finding information about elements\n" +
                    "• Managing sheets and views\n" +
                    "• Intelligent placement with collision avoidance\n\n" +
                    "What would you like to do?");
            }

            // Cleanup and save on close
            Closing += (s, e) =>
            {
                SaveSession();
                DisconnectMCP();
            };
        }

        private void BuildUI()
        {
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = CreateHeader();
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // Chat history area
            var chatArea = CreateChatArea();
            Grid.SetRow(chatArea, 1);
            mainGrid.Children.Add(chatArea);

            // Progress panel
            _progressPanel = CreateProgressPanel();
            Grid.SetRow(_progressPanel, 2);
            mainGrid.Children.Add(_progressPanel);

            // Input area
            var inputArea = CreateInputArea();
            Grid.SetRow(inputArea, 3);
            mainGrid.Children.Add(inputArea);

            Content = mainGrid;
        }

        private Border CreateHeader()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Padding = new Thickness(16)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Title and status
            var titleStack = new StackPanel();
            var title = new TextBlock
            {
                Text = "BIM Ops Studio",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            };
            _statusText = new TextBlock
            {
                Text = "Ready",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0)
            };
            titleStack.Children.Add(title);
            titleStack.Children.Add(_statusText);
            Grid.SetColumn(titleStack, 0);
            grid.Children.Add(titleStack);

            // Buttons
            var buttonStack = new StackPanel { Orientation = Orientation.Horizontal };

            var clearButton = CreateButton("Clear", false);
            clearButton.Click += (s, e) => ClearChat();
            buttonStack.Children.Add(clearButton);

            var settingsButton = CreateButton("Settings", false);
            settingsButton.Margin = new Thickness(8, 0, 0, 0);
            settingsButton.Click += (s, e) => ShowSettingsDialog();
            buttonStack.Children.Add(settingsButton);

            Grid.SetColumn(buttonStack, 1);
            grid.Children.Add(buttonStack);

            border.Child = grid;
            return border;
        }

        private Border CreateChatArea()
        {
            var border = new Border
            {
                Margin = new Thickness(8),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(8)
            };

            _chatScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8)
            };

            _chatHistory = new StackPanel();
            _chatScrollViewer.Content = _chatHistory;
            border.Child = _chatScrollViewer;

            return border;
        }

        private Border CreateProgressPanel()
        {
            var border = new Border
            {
                Margin = new Thickness(8, 0, 8, 8),
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(8),
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel();

            _progressTitle = new TextBlock
            {
                Text = "Working...",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var progressBar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 4,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212))
            };

            _progressDetail = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            stack.Children.Add(_progressTitle);
            stack.Children.Add(progressBar);
            stack.Children.Add(_progressDetail);
            border.Child = stack;

            return border;
        }

        private Border CreateInputArea()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Padding = new Thickness(12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _inputTextBox = new System.Windows.Controls.TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                Padding = new Thickness(12, 10, 12, 10),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,  // Allow multi-line input (Shift+Enter for newline)
                MaxHeight = 400,       // Large but bounded so it doesn't take over the whole panel
                MinHeight = 40,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CaretBrush = Brushes.White,
                MaxLength = 0          // No character limit (0 = unlimited)
            };
            _inputTextBox.KeyDown += InputTextBox_KeyDown;
            Grid.SetColumn(_inputTextBox, 0);
            grid.Children.Add(_inputTextBox);

            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 0, 0)
            };

            _stopButton = CreateButton("Stop", false);
            _stopButton.Visibility = Visibility.Collapsed;
            _stopButton.Click += (s, e) => StopAgent();
            buttonStack.Children.Add(_stopButton);

            _sendButton = CreateButton("Send", true);
            _sendButton.Margin = new Thickness(8, 0, 0, 0);
            _sendButton.Click += async (s, e) => await SendMessage();
            buttonStack.Children.Add(_sendButton);

            Grid.SetColumn(buttonStack, 1);
            grid.Children.Add(buttonStack);

            border.Child = grid;
            return border;
        }

        private Button CreateButton(string text, bool isPrimary)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(16, 8, 16, 8),
                Background = isPrimary
                    ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
                    : new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            return button;
        }

        // Config file path - use user's home directory for portability
        private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bimops");
        private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bimops", "config.json");
        private static readonly string SessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bimops", "session.json");
        private static readonly string DefaultModel = "claude-sonnet-4-20250514"; // Default to Sonnet for cost-effectiveness

        // Session data for persistence
        private List<ChatMessage> _sessionMessages = new List<ChatMessage>();
        private string _sessionProjectName;

        private void LoadConfig()
        {
            // Try environment variable for API key
            _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            _selectedModel = DefaultModel;

            // Try config file
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var config = JObject.Parse(File.ReadAllText(ConfigPath));

                    var savedKey = config["anthropic_api_key"]?.ToString();
                    if (!string.IsNullOrEmpty(savedKey))
                    {
                        _apiKey = savedKey;
                    }

                    var savedModel = config["selected_model"]?.ToString();
                    if (!string.IsNullOrEmpty(savedModel) && AvailableModels.ContainsKey(savedModel))
                    {
                        _selectedModel = savedModel;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                // Create directory if it doesn't exist
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                // Load existing config or create new one (preserve other settings)
                JObject config;
                if (File.Exists(ConfigPath))
                {
                    try
                    {
                        config = JObject.Parse(File.ReadAllText(ConfigPath));
                    }
                    catch
                    {
                        config = new JObject();
                    }
                }
                else
                {
                    config = new JObject();
                }

                // Update settings
                config["anthropic_api_key"] = _apiKey;
                config["selected_model"] = _selectedModel;
                config["last_updated"] = DateTime.Now.ToString("o");

                // Save
                File.WriteAllText(ConfigPath, config.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        #region Session Persistence

        /// <summary>
        /// Message types for session persistence
        /// </summary>
        public class ChatMessage
        {
            public string Type { get; set; }  // "user", "assistant", "tool", "error", "system"
            public string Content { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Session data structure
        /// </summary>
        public class SessionData
        {
            public string ProjectName { get; set; }
            public string LastTask { get; set; }
            public DateTime SavedAt { get; set; }
            public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        }

        /// <summary>
        /// Save the current session to disk (called on window close)
        /// </summary>
        private void SaveSession()
        {
            try
            {
                // Update project name from current document
                _sessionProjectName = _uiApp?.ActiveUIDocument?.Document?.Title ?? _sessionProjectName ?? "Unknown";

                // Force immediate save (bypass debounce)
                _lastSaveTime = DateTime.MinValue;
                SaveSessionInternal();

                System.Diagnostics.Debug.WriteLine($"Session saved on close: {_sessionMessages.Count} messages");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving session: {ex.Message}");
            }
        }

        /// <summary>
        /// Load a previous session if it exists
        /// </summary>
        private SessionData LoadSession()
        {
            try
            {
                if (File.Exists(SessionPath))
                {
                    var json = File.ReadAllText(SessionPath);
                    return JsonConvert.DeserializeObject<SessionData>(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading session: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Track a message for session persistence
        /// </summary>
        private void TrackMessage(string type, string content)
        {
            _sessionMessages.Add(new ChatMessage
            {
                Type = type,
                Content = content,
                Timestamp = DateTime.Now
            });

            // AUTO-SAVE: Save session immediately after each message
            // This ensures persistence even if Revit crashes
            SaveSessionAsync();
        }

        // Debounce timer to avoid too-frequent saves
        private DateTime _lastSaveTime = DateTime.MinValue;
        private readonly object _saveLock = new object();

        /// <summary>
        /// Save session asynchronously with debouncing
        /// </summary>
        private void SaveSessionAsync()
        {
            // Debounce: only save if at least 2 seconds since last save
            lock (_saveLock)
            {
                if ((DateTime.Now - _lastSaveTime).TotalSeconds < 2)
                {
                    return; // Skip, will save on next message or on close
                }
                _lastSaveTime = DateTime.Now;
            }

            // Save on background thread to avoid UI lag
            Task.Run(() =>
            {
                try
                {
                    SaveSessionInternal();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Auto-save error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Internal save method (thread-safe)
        /// </summary>
        private void SaveSessionInternal()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                string projectName;
                List<ChatMessage> messagesToSave;
                string lastMessage;

                // Get data on UI thread if needed
                lock (_saveLock)
                {
                    projectName = _sessionProjectName ?? "Unknown";
                    lastMessage = _lastUserMessage ?? "";

                    // Keep last 50 messages
                    messagesToSave = _sessionMessages.Count > 50
                        ? _sessionMessages.Skip(_sessionMessages.Count - 50).ToList()
                        : _sessionMessages.ToList();
                }

                var session = new SessionData
                {
                    ProjectName = projectName,
                    LastTask = lastMessage,
                    SavedAt = DateTime.Now,
                    Messages = messagesToSave
                };

                var json = JsonConvert.SerializeObject(session, Formatting.Indented);

                // Write to temp file first, then rename (atomic operation)
                var tempPath = SessionPath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(SessionPath))
                {
                    File.Delete(SessionPath);
                }
                File.Move(tempPath, SessionPath);

                System.Diagnostics.Debug.WriteLine($"Session auto-saved: {messagesToSave.Count} messages");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in SaveSessionInternal: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore messages from a previous session
        /// </summary>
        private void RestoreSession(SessionData session)
        {
            _sessionMessages = session.Messages.ToList();
            _sessionProjectName = session.ProjectName;
            _lastUserMessage = session.LastTask;

            // CRITICAL: Restore the AgentCore's conversation history
            // This ensures the AI remembers the previous context
            if (_agent != null)
            {
                var historyItems = session.Messages
                    .Where(m => m.Type == "user" || m.Type == "assistant")
                    .Select(m => new ChatHistoryItem
                    {
                        Role = m.Type,
                        Content = m.Content
                    })
                    .ToList();

                _agent.RestoreHistory(historyItems);
                System.Diagnostics.Debug.WriteLine($"Restored {historyItems.Count} messages to AgentCore");
            }

            // Show last 20 messages in UI
            var messagesToShow = session.Messages.Count > 20
                ? session.Messages.Skip(session.Messages.Count - 20)
                : session.Messages;

            foreach (var msg in messagesToShow)
            {
                switch (msg.Type)
                {
                    case "user":
                        RestoreUserMessage(msg.Content);
                        break;
                    case "assistant":
                        RestoreAssistantMessage(msg.Content);
                        break;
                    case "tool":
                        RestoreToolMessage(msg.Content);
                        break;
                }
            }

            // Add continuation message
            AddSystemMessage($"--- Session restored from {session.SavedAt:g} ---");
            if (!string.IsNullOrEmpty(session.LastTask))
            {
                AddSystemMessage($"Last task: {session.LastTask}");
            }
        }

        // Restore methods without tracking (to avoid duplicating in session)
        private void RestoreUserMessage(string text)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                CornerRadius = new CornerRadius(12, 12, 0, 12),
                Padding = new Thickness(12),
                Margin = new Thickness(50, 8, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                Opacity = 0.7  // Slightly faded to show it's from previous session
            };
            border.Child = new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            _chatHistory.Children.Add(border);
        }

        private void RestoreAssistantMessage(string text)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(12, 12, 12, 0),
                Padding = new Thickness(12),
                Margin = new Thickness(8, 8, 50, 8),
                Opacity = 0.7
            };
            border.Child = new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            _chatHistory.Children.Add(border);
        }

        private void RestoreToolMessage(string text)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(10),
                Margin = new Thickness(20, 4, 20, 4),
                Opacity = 0.7
            };
            border.Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas")
            };
            _chatHistory.Children.Add(border);
        }

        /// <summary>
        /// Show dialog to ask user if they want to continue previous session
        /// </summary>
        private bool AskToContinueSession(SessionData session)
        {
            var timeSince = DateTime.Now - session.SavedAt;
            string timeDesc;
            if (timeSince.TotalMinutes < 60)
                timeDesc = $"{(int)timeSince.TotalMinutes} minutes ago";
            else if (timeSince.TotalHours < 24)
                timeDesc = $"{(int)timeSince.TotalHours} hours ago";
            else
                timeDesc = $"{(int)timeSince.TotalDays} days ago";

            var result = System.Windows.MessageBox.Show(
                $"Found a previous session from {timeDesc}.\n\n" +
                $"Project: {session.ProjectName}\n" +
                $"Last task: {(session.LastTask?.Length > 50 ? session.LastTask.Substring(0, 50) + "..." : session.LastTask ?? "None")}\n\n" +
                "Would you like to continue where you left off?",
                "Continue Previous Session?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return result == MessageBoxResult.Yes;
        }

        #endregion

        private void ShowSettingsDialog()
        {
            var dialog = new Window
            {
                Title = "AI Assistant Settings",
                Width = 500,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var stack = new StackPanel { Margin = new Thickness(20) };

            // API Key section
            stack.Children.Add(new TextBlock
            {
                Text = "Anthropic API Key:",
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 5)
            });

            var apiKeyBox = new System.Windows.Controls.TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                Padding = new Thickness(10),
                FontSize = 14,
                Text = _apiKey ?? ""
            };
            stack.Children.Add(apiKeyBox);

            // Model selection section
            stack.Children.Add(new TextBlock
            {
                Text = "AI Model:",
                Foreground = Brushes.White,
                Margin = new Thickness(0, 15, 0, 5)
            });

            var modelCombo = new System.Windows.Controls.ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.Black,
                Padding = new Thickness(10),
                FontSize = 14
            };

            foreach (var model in AvailableModels)
            {
                modelCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = model.Value,
                    Tag = model.Key,
                    IsSelected = model.Key == _selectedModel
                });
            }
            stack.Children.Add(modelCombo);

            // Model info
            stack.Children.Add(new TextBlock
            {
                Text = "Opus = Smartest (expensive), Sonnet = Balanced, Haiku = Cheapest (for testing)",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            // Save button
            var button = CreateButton("Save & Connect", true);
            button.Margin = new Thickness(0, 20, 0, 0);
            button.Click += (s, e) =>
            {
                _apiKey = apiKeyBox.Text.Trim();
                var selectedItem = modelCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
                if (selectedItem != null)
                {
                    _selectedModel = selectedItem.Tag.ToString();
                }

                if (!string.IsNullOrEmpty(_apiKey))
                {
                    SaveConfig();
                    InitializeAgent();
                    dialog.Close();
                }
            };
            stack.Children.Add(button);

            dialog.Content = stack;
            dialog.ShowDialog();
        }

        // Knowledge base directory - embedded intelligence
        private static readonly string KnowledgeDir = @"D:\RevitMCPBridge2026\knowledge";

        // Core files to always load (small, essential for every session)
        private static readonly string[] CoreKnowledgeFiles = new[]
        {
            "_index.md",              // Index of all files - tells agent what's available
            "user-preferences.md",    // How to communicate
            "voice-corrections.md",   // Wispr Flow fixes
            "error-recovery.md",      // How to handle errors
            "revit-api-lessons.md",   // Key API gotchas
            "annotation-standards.md" // Text sizes, keynotes, dimensions - CRITICAL
        };

        /// <summary>
        /// Load only core knowledge files to stay within Haiku's 200K context limit.
        /// The full 99-file knowledge base is 207K+ tokens - too large!
        /// Agent can use getKnowledgeFile tool to load additional files on demand.
        /// </summary>
        private string LoadCoreKnowledge()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== CORE KNOWLEDGE (Always Available) ===");
            sb.AppendLine("Note: Use 'getKnowledgeFile' tool to load additional knowledge files on demand.");
            sb.AppendLine("See _index.md below for all 99 available knowledge files.\n");

            try
            {
                if (Directory.Exists(KnowledgeDir))
                {
                    foreach (var fileName in CoreKnowledgeFiles)
                    {
                        var filePath = Path.Combine(KnowledgeDir, fileName);
                        if (File.Exists(filePath))
                        {
                            try
                            {
                                var content = File.ReadAllText(filePath);
                                sb.AppendLine($"--- {fileName} ---");
                                sb.AppendLine(content);
                                sb.AppendLine();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading core knowledge: {ex.Message}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Load a specific knowledge file by name (called by getKnowledgeFile tool)
        /// </summary>
        public static string LoadKnowledgeFile(string fileName)
        {
            try
            {
                // Ensure .md extension
                if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    fileName += ".md";

                var filePath = Path.Combine(KnowledgeDir, fileName);
                if (File.Exists(filePath))
                {
                    return File.ReadAllText(filePath);
                }
                return $"Knowledge file '{fileName}' not found. Use listKnowledgeFiles to see available files.";
            }
            catch (Exception ex)
            {
                return $"Error loading knowledge file: {ex.Message}";
            }
        }

        /// <summary>
        /// List all available knowledge files
        /// </summary>
        public static string ListKnowledgeFiles()
        {
            try
            {
                if (Directory.Exists(KnowledgeDir))
                {
                    var files = Directory.GetFiles(KnowledgeDir, "*.md")
                        .Select(f => Path.GetFileName(f))
                        .OrderBy(f => f)
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        count = files.Count,
                        files = files,
                        hint = "Use getKnowledgeFile(fileName) to load a specific file"
                    });
                }
                return JsonConvert.SerializeObject(new { success = false, error = "Knowledge directory not found" });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private void InitializeAgent()
        {
            _agent = new AgentCore(_apiKey, _selectedModel);
            _agent.RegisterTools(ToolDefinitions.GetAllTools());
            _agent.SetToolExecutor(ExecuteMCPMethodAsync);

            _agent.OnThinking += (msg) => Dispatcher.Invoke(() => ShowProgress(msg));
            _agent.OnToolCall += (msg) => Dispatcher.Invoke(() => { _lastToolCall = msg; UpdateProgress(msg); AddToolMessage(msg, false); });
            _agent.OnToolResult += (msg) => Dispatcher.Invoke(() => {
                UpdateProgress(msg);
                AddToolMessage(msg, true);
                // Try to display image if the result contains an image path
                TryDisplayImageFromResult(msg);
            });
            _agent.OnResponse += (msg) => Dispatcher.Invoke(() => AddAssistantMessage(msg));
            _agent.OnError += (msg) => Dispatcher.Invoke(() => { AddErrorMessage(msg); HideProgress(); SetProcessing(false); });
            _agent.OnComplete += () => Dispatcher.Invoke(() => { HideProgress(); SetProcessing(false); });

            // LOCAL MODEL event - show when qwen2.5:7b is processing
            _agent.OnLocalModel += (msg) => Dispatcher.Invoke(() => {
                UpdateProgress(msg);
                // Update status to show local model is active
                if (msg.Contains("Processing with local"))
                {
                    _statusText.Text = "Using Local (qwen2.5:7b)";
                }
                else if (msg.Contains("using Anthropic"))
                {
                    var modelName = _selectedModel.Contains("opus") ? "Opus 4.5" :
                                   _selectedModel.Contains("sonnet") ? "Sonnet 4" : "Haiku 3.5";
                    _statusText.Text = $"Connected ({modelName})";
                }
            });

            // VERIFICATION event - show if commands actually worked
            _agent.OnVerification += (result) => Dispatcher.Invoke(() => {
                if (result != null)
                {
                    if (result.Verified)
                    {
                        AddToolMessage($"✅ Verified: {result.Message}", true);
                    }
                    else
                    {
                        AddToolMessage($"⚠️ Verification failed: {result.Message}", false);
                    }
                }
            });

            // Show which model is active (with local model indicator)
            var modelName = _selectedModel.Contains("opus") ? "Opus 4.5" :
                           _selectedModel.Contains("sonnet") ? "Sonnet 4" : "Haiku 3.5";
            _statusText.Text = $"Connected ({modelName} + Local)";
        }

        private void EnsureMCPConnection()
        {
            lock (_pipeLock)
            {
                if (_mcpPipe == null || !_mcpPipe.IsConnected)
                {
                    // Dispose old connection if exists
                    _mcpWriter?.Dispose();
                    _mcpReader?.Dispose();
                    _mcpPipe?.Dispose();

                    // Create new connection
                    _mcpPipe = new NamedPipeClientStream(".", "RevitMCPBridge2026", PipeDirection.InOut);
                    _mcpPipe.Connect(5000);
                    _mcpWriter = new StreamWriter(_mcpPipe) { AutoFlush = true };
                    _mcpReader = new StreamReader(_mcpPipe);
                }
            }
        }

        private void DisconnectMCP()
        {
            lock (_pipeLock)
            {
                _mcpWriter?.Dispose();
                _mcpReader?.Dispose();
                _mcpPipe?.Dispose();
                _mcpWriter = null;
                _mcpReader = null;
                _mcpPipe = null;
            }
        }

        private async Task<string> ExecuteMCPMethodAsync(string methodName, JObject parameters)
        {
            // Handle local tools (knowledge base) - don't need MCP
            if (methodName == "listKnowledgeFiles")
            {
                return await Task.FromResult(ListKnowledgeFiles());
            }
            if (methodName == "getKnowledgeFile")
            {
                var fileName = parameters?["fileName"]?.ToString();
                if (string.IsNullOrEmpty(fileName))
                {
                    return await Task.FromResult(JsonConvert.SerializeObject(new { success = false, error = "fileName parameter is required" }));
                }
                var content = LoadKnowledgeFile(fileName);
                return await Task.FromResult(JsonConvert.SerializeObject(new { success = true, fileName = fileName, content = content }));
            }

            // Handle vision analysis - needs API key which we have locally
            if (methodName == "analyzeView")
            {
                // First capture the view via MCP, then analyze with Claude vision
                parameters = parameters ?? new JObject();
                parameters["apiKey"] = _apiKey;  // Pass our API key for vision analysis

                // Call MCP to execute the analysis in Revit context
                var mcpRequest = new JObject
                {
                    ["method"] = "analyzeView",
                    ["params"] = parameters
                };
                return await ExecuteMCPWithRetryAsync("analyzeView", parameters);
            }

            // Handle file operation tools locally
            var fileResult = await HandleFileOperationAsync(methodName, parameters);
            if (fileResult != null)
            {
                return fileResult;
            }

            // Handle memory tools locally
            var memoryResult = await HandleMemoryOperationAsync(methodName, parameters);
            if (memoryResult != null)
            {
                return memoryResult;
            }

            // All other tools go through MCP with retry logic
            return await ExecuteMCPWithRetryAsync(methodName, parameters);
        }

        // Retry configuration
        private const int MaxRetryAttempts = 3;
        private const int InitialRetryDelayMs = 500;
        private const int MCPTimeoutMs = 30000;

        /// <summary>
        /// Execute MCP method with automatic retry and enhanced error handling
        /// </summary>
        private async Task<string> ExecuteMCPWithRetryAsync(string methodName, JObject parameters)
        {
            var lastError = "";
            var request = new JObject
            {
                ["method"] = methodName,
                ["params"] = parameters ?? new JObject()
            };
            var requestJson = request.ToString(Formatting.None);

            for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    var response = await Task.Run(() =>
                    {
                        lock (_pipeLock)
                        {
                            try
                            {
                                EnsureMCPConnection();
                                _mcpWriter.WriteLine(requestJson);

                                // Read with timeout simulation via check
                                var result = _mcpReader.ReadLine();
                                return result;
                            }
                            catch (IOException ioEx)
                            {
                                // Pipe broken - need to reconnect
                                DisconnectMCP();
                                throw new MCPConnectionException("Connection lost", ioEx);
                            }
                            catch (TimeoutException)
                            {
                                throw new MCPTimeoutException($"Method '{methodName}' timed out");
                            }
                        }
                    });

                    if (string.IsNullOrEmpty(response))
                    {
                        // Empty response - likely connection issue
                        DisconnectMCP();
                        lastError = "Empty response from MCP server";

                        if (attempt < MaxRetryAttempts)
                        {
                            await Task.Delay(InitialRetryDelayMs * attempt);
                            continue;
                        }
                    }
                    else
                    {
                        // Got a response - check if it's an error response
                        try
                        {
                            var parsed = JObject.Parse(response);
                            if (parsed["success"]?.ToObject<bool>() == false)
                            {
                                var error = parsed["error"]?.ToString() ?? "Unknown error";

                                // Don't retry method-level errors (they'll fail again)
                                if (!IsRetryableError(error))
                                {
                                    return response; // Return the error response as-is
                                }

                                lastError = error;
                                if (attempt < MaxRetryAttempts)
                                {
                                    await Task.Delay(InitialRetryDelayMs * attempt);
                                    continue;
                                }
                            }
                        }
                        catch { } // Not valid JSON, return as-is

                        return response;
                    }
                }
                catch (MCPConnectionException connEx)
                {
                    lastError = connEx.Message;
                    if (attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(InitialRetryDelayMs * attempt);
                        continue;
                    }
                }
                catch (MCPTimeoutException timeoutEx)
                {
                    lastError = timeoutEx.Message;
                    // Timeouts often indicate Revit is busy - give it time
                    if (attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(InitialRetryDelayMs * attempt * 2);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    DisconnectMCP();
                    lastError = ex.Message;
                    if (attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(InitialRetryDelayMs * attempt);
                        continue;
                    }
                }
            }

            // All retries failed - return helpful error
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = lastError,
                method = methodName,
                attempts = MaxRetryAttempts,
                suggestion = GetErrorSuggestion(lastError)
            });
        }

        /// <summary>
        /// Check if an error is retryable (transient) vs permanent
        /// </summary>
        private bool IsRetryableError(string error)
        {
            if (string.IsNullOrEmpty(error)) return true;
            var lower = error.ToLower();

            // Transient errors that might succeed on retry
            if (lower.Contains("timeout")) return true;
            if (lower.Contains("busy")) return true;
            if (lower.Contains("connection")) return true;
            if (lower.Contains("pipe")) return true;
            if (lower.Contains("unavailable")) return true;

            // Permanent errors - don't retry
            if (lower.Contains("not found")) return false;
            if (lower.Contains("invalid")) return false;
            if (lower.Contains("required")) return false;
            if (lower.Contains("does not exist")) return false;
            if (lower.Contains("permission")) return false;

            return true; // Default to retryable
        }

        /// <summary>
        /// Get a helpful suggestion based on the error type
        /// </summary>
        private string GetErrorSuggestion(string error)
        {
            if (string.IsNullOrEmpty(error)) return "Check if Revit is running and the MCP server is active.";
            var lower = error.ToLower();

            if (lower.Contains("timeout") || lower.Contains("busy"))
                return "Revit may be busy or have a dialog open. Close any dialogs and click in the drawing area.";

            if (lower.Contains("connection") || lower.Contains("pipe"))
                return "MCP connection lost. The server will automatically reconnect on the next command.";

            if (lower.Contains("not found"))
                return "The requested element or method was not found. Verify the parameters are correct.";

            if (lower.Contains("transaction"))
                return "Revit transaction error. The model may be in an invalid state. Try a simpler operation first.";

            if (lower.Contains("document"))
                return "Document error. Ensure a Revit document is open and active.";

            return "If the problem persists, try restarting the MCP server from the Revit ribbon.";
        }

        // Custom exception types for better error handling
        private class MCPConnectionException : Exception
        {
            public MCPConnectionException(string message, Exception inner = null) : base(message, inner) { }
        }

        private class MCPTimeoutException : Exception
        {
            public MCPTimeoutException(string message) : base(message) { }
        }

        /// <summary>
        /// Handle file operation tools locally (no MCP needed)
        /// Provides Claude Code-like file system access
        /// </summary>
        private async Task<string> HandleFileOperationAsync(string methodName, JObject parameters)
        {
            return await Task.Run(() =>
            {
                try
                {
                    switch (methodName)
                    {
                        case "readFile":
                            return HandleReadFile(parameters);

                        case "writeFile":
                            return HandleWriteFile(parameters);

                        case "listDirectory":
                            return HandleListDirectory(parameters);

                        case "searchFiles":
                            return HandleSearchFiles(parameters);

                        case "fileInfo":
                            return HandleFileInfo(parameters);

                        case "copyFile":
                            return HandleCopyFile(parameters);

                        case "deleteFile":
                            return HandleDeleteFile(parameters);

                        case "createDirectory":
                            return HandleCreateDirectory(parameters);

                        default:
                            return null; // Not a file operation, let MCP handle it
                    }
                }
                catch (Exception ex)
                {
                    return ResponseBuilder.FromException(ex).Build();
                }
            });
        }

        #region File Operation Handlers

        private string HandleReadFile(JObject parameters)
        {
            var filePath = parameters?["path"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "path parameter is required" });
            }

            if (!File.Exists(filePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"File not found: {filePath}" });
            }

            try
            {
                var content = File.ReadAllText(filePath);
                var fileInfo = new FileInfo(filePath);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = filePath,
                    content = content,
                    size = fileInfo.Length,
                    lastModified = fileInfo.LastWriteTime.ToString("o")
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleWriteFile(JObject parameters)
        {
            var filePath = parameters?["path"]?.ToString();
            var content = parameters?["content"]?.ToString();

            if (string.IsNullOrEmpty(filePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "path parameter is required" });
            }

            if (content == null)
            {
                return JsonConvert.SerializeObject(new { success = false, error = "content parameter is required" });
            }

            try
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(filePath, content);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = filePath,
                    bytesWritten = content.Length,
                    message = $"File written successfully: {filePath}"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleListDirectory(JObject parameters)
        {
            var dirPath = parameters?["path"]?.ToString() ?? Environment.CurrentDirectory;
            var pattern = parameters?["pattern"]?.ToString() ?? "*";
            var recursive = parameters?["recursive"]?.ToObject<bool>() ?? false;

            if (!Directory.Exists(dirPath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"Directory not found: {dirPath}" });
            }

            try
            {
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                var files = Directory.GetFiles(dirPath, pattern, searchOption)
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = f,
                        type = "file",
                        size = new FileInfo(f).Length
                    }).ToList();

                var dirs = Directory.GetDirectories(dirPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(d => new
                    {
                        name = Path.GetFileName(d),
                        path = d,
                        type = "directory",
                        size = 0L
                    }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = dirPath,
                    pattern = pattern,
                    directories = dirs,
                    files = files,
                    totalFiles = files.Count,
                    totalDirectories = dirs.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleSearchFiles(JObject parameters)
        {
            var dirPath = parameters?["path"]?.ToString() ?? Environment.CurrentDirectory;
            var pattern = parameters?["pattern"]?.ToString();
            var searchText = parameters?["searchText"]?.ToString();
            var maxResults = parameters?["maxResults"]?.ToObject<int>() ?? 100;

            if (string.IsNullOrEmpty(pattern) && string.IsNullOrEmpty(searchText))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "Either pattern or searchText is required" });
            }

            if (!Directory.Exists(dirPath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"Directory not found: {dirPath}" });
            }

            try
            {
                var results = new List<object>();
                var searchPattern = pattern ?? "*";

                foreach (var file in Directory.EnumerateFiles(dirPath, searchPattern, SearchOption.AllDirectories))
                {
                    if (results.Count >= maxResults) break;

                    // If searchText specified, check file contents
                    if (!string.IsNullOrEmpty(searchText))
                    {
                        try
                        {
                            var content = File.ReadAllText(file);
                            if (content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                results.Add(new
                                {
                                    path = file,
                                    name = Path.GetFileName(file),
                                    matchType = "content"
                                });
                            }
                        }
                        catch { } // Skip files that can't be read
                    }
                    else
                    {
                        results.Add(new
                        {
                            path = file,
                            name = Path.GetFileName(file),
                            matchType = "pattern"
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    searchPath = dirPath,
                    pattern = pattern,
                    searchText = searchText,
                    results = results,
                    count = results.Count,
                    limitReached = results.Count >= maxResults
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleFileInfo(JObject parameters)
        {
            var filePath = parameters?["path"]?.ToString();

            if (string.IsNullOrEmpty(filePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "path parameter is required" });
            }

            try
            {
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = filePath,
                        exists = true,
                        type = "file",
                        name = info.Name,
                        extension = info.Extension,
                        size = info.Length,
                        created = info.CreationTime.ToString("o"),
                        modified = info.LastWriteTime.ToString("o"),
                        accessed = info.LastAccessTime.ToString("o"),
                        isReadOnly = info.IsReadOnly
                    });
                }
                else if (Directory.Exists(filePath))
                {
                    var info = new DirectoryInfo(filePath);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = filePath,
                        exists = true,
                        type = "directory",
                        name = info.Name,
                        created = info.CreationTime.ToString("o"),
                        modified = info.LastWriteTime.ToString("o"),
                        accessed = info.LastAccessTime.ToString("o")
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = filePath,
                        exists = false
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleCopyFile(JObject parameters)
        {
            var sourcePath = parameters?["source"]?.ToString();
            var destPath = parameters?["destination"]?.ToString();
            var overwrite = parameters?["overwrite"]?.ToObject<bool>() ?? false;

            if (string.IsNullOrEmpty(sourcePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "source parameter is required" });
            }

            if (string.IsNullOrEmpty(destPath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "destination parameter is required" });
            }

            if (!File.Exists(sourcePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = $"Source file not found: {sourcePath}" });
            }

            try
            {
                // Ensure destination directory exists
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(sourcePath, destPath, overwrite);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    source = sourcePath,
                    destination = destPath,
                    message = "File copied successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleDeleteFile(JObject parameters)
        {
            var filePath = parameters?["path"]?.ToString();
            var confirm = parameters?["confirm"]?.ToObject<bool>() ?? false;

            if (string.IsNullOrEmpty(filePath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "path parameter is required" });
            }

            if (!confirm)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Deletion requires confirm=true for safety",
                    path = filePath,
                    exists = File.Exists(filePath)
                });
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = filePath,
                        message = "File deleted successfully"
                    });
                }
                else if (Directory.Exists(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Path is a directory. Use deleteDirectory for directories."
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"File not found: {filePath}"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private string HandleCreateDirectory(JObject parameters)
        {
            var dirPath = parameters?["path"]?.ToString();

            if (string.IsNullOrEmpty(dirPath))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "path parameter is required" });
            }

            try
            {
                if (Directory.Exists(dirPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = dirPath,
                        message = "Directory already exists",
                        created = false
                    });
                }

                Directory.CreateDirectory(dirPath);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = dirPath,
                    message = "Directory created successfully",
                    created = true
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Memory Operation Handlers

        // Memory storage file path - use user's home directory for portability
        private static readonly string MemoryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bimops", "memory");
        private static readonly string MemoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bimops", "memory", "memories.json");

        /// <summary>
        /// Handle memory operation tools locally (no MCP needed)
        /// Provides persistent memory across sessions
        /// </summary>
        private async Task<string> HandleMemoryOperationAsync(string methodName, JObject parameters)
        {
            return await Task.Run(() =>
            {
                try
                {
                    switch (methodName)
                    {
                        case "memoryStore":
                            return HandleMemoryStore(parameters);

                        case "memoryRecall":
                            return HandleMemoryRecall(parameters);

                        case "memoryGetContext":
                            return HandleMemoryGetContext(parameters);

                        case "memoryStoreCorrection":
                            return HandleMemoryStoreCorrection(parameters);

                        case "memoryGetCorrections":
                            return HandleMemoryGetCorrections(parameters);

                        case "memorySummarizeSession":
                            return HandleMemorySummarizeSession(parameters);

                        case "memoryStats":
                            return HandleMemoryStats();

                        default:
                            return null; // Not a memory operation
                    }
                }
                catch (Exception ex)
                {
                    return ResponseBuilder.FromException(ex).Build();
                }
            });
        }

        private List<MemoryItem> LoadMemories()
        {
            try
            {
                if (File.Exists(MemoryFile))
                {
                    var json = File.ReadAllText(MemoryFile);
                    return JsonConvert.DeserializeObject<List<MemoryItem>>(json) ?? new List<MemoryItem>();
                }
            }
            catch { }
            return new List<MemoryItem>();
        }

        private void SaveMemories(List<MemoryItem> memories)
        {
            try
            {
                if (!Directory.Exists(MemoryDir))
                {
                    Directory.CreateDirectory(MemoryDir);
                }
                File.WriteAllText(MemoryFile, JsonConvert.SerializeObject(memories, Formatting.Indented));
            }
            catch { }
        }

        private string HandleMemoryStore(JObject parameters)
        {
            var content = parameters?["content"]?.ToString();
            if (string.IsNullOrEmpty(content))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "content is required" });
            }

            var memory = new MemoryItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Content = content,
                MemoryType = parameters?["memoryType"]?.ToString() ?? "context",
                Project = parameters?["project"]?.ToString(),
                Importance = parameters?["importance"]?.ToObject<int>() ?? 5,
                Tags = parameters?["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                CreatedAt = DateTime.Now,
                Source = "revit-ai"
            };

            var memories = LoadMemories();
            memories.Add(memory);
            SaveMemories(memories);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                id = memory.Id,
                message = "Memory stored successfully"
            });
        }

        private string HandleMemoryRecall(JObject parameters)
        {
            var query = parameters?["query"]?.ToString();
            if (string.IsNullOrEmpty(query))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "query is required" });
            }

            var project = parameters?["project"]?.ToString();
            var memoryType = parameters?["memoryType"]?.ToString();
            var limit = parameters?["limit"]?.ToObject<int>() ?? 10;

            var memories = LoadMemories();
            var queryLower = query.ToLower();

            var results = memories
                .Where(m =>
                    (string.IsNullOrEmpty(project) || m.Project == project) &&
                    (string.IsNullOrEmpty(memoryType) || m.MemoryType == memoryType) &&
                    (m.Content.ToLower().Contains(queryLower) ||
                     (m.Tags != null && m.Tags.Any(t => t.ToLower().Contains(queryLower)))))
                .OrderByDescending(m => m.Importance)
                .ThenByDescending(m => m.CreatedAt)
                .Take(limit)
                .Select(m => new
                {
                    m.Id,
                    m.Content,
                    m.MemoryType,
                    m.Project,
                    m.Importance,
                    m.Tags,
                    createdAt = m.CreatedAt.ToString("o")
                })
                .ToList();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                query = query,
                count = results.Count,
                memories = results
            });
        }

        private string HandleMemoryGetContext(JObject parameters)
        {
            var project = parameters?["project"]?.ToString();
            var includeCorrections = parameters?["includeCorrections"]?.ToObject<bool>() ?? true;

            var memories = LoadMemories();

            // Get high-importance memories
            var importantMemories = memories
                .Where(m => m.Importance >= 7 && (string.IsNullOrEmpty(project) || m.Project == project))
                .OrderByDescending(m => m.CreatedAt)
                .Take(5)
                .ToList();

            // Get recent memories
            var recentMemories = memories
                .Where(m => m.CreatedAt > DateTime.Now.AddDays(-7) && (string.IsNullOrEmpty(project) || m.Project == project))
                .OrderByDescending(m => m.CreatedAt)
                .Take(5)
                .ToList();

            // Get corrections if requested
            var corrections = new List<MemoryItem>();
            if (includeCorrections)
            {
                corrections = memories
                    .Where(m => m.MemoryType == "correction")
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(10)
                    .ToList();
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                project = project,
                importantMemories = importantMemories.Select(m => new { m.Id, m.Content, m.MemoryType, m.Importance }),
                recentMemories = recentMemories.Select(m => new { m.Id, m.Content, m.MemoryType, createdAt = m.CreatedAt.ToString("g") }),
                corrections = corrections.Select(m => new { m.Id, m.Content }),
                hint = "Use memoryRecall to search for specific memories"
            });
        }

        private string HandleMemoryStoreCorrection(JObject parameters)
        {
            var whatISaid = parameters?["whatISaid"]?.ToString();
            var whatWasWrong = parameters?["whatWasWrong"]?.ToString();
            var correctApproach = parameters?["correctApproach"]?.ToString();

            if (string.IsNullOrEmpty(whatISaid) || string.IsNullOrEmpty(whatWasWrong) || string.IsNullOrEmpty(correctApproach))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "whatISaid, whatWasWrong, and correctApproach are all required" });
            }

            var memory = new MemoryItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Content = $"CORRECTION:\nWhat I said: {whatISaid}\nWhat was wrong: {whatWasWrong}\nCorrect approach: {correctApproach}",
                MemoryType = "correction",
                Project = parameters?["project"]?.ToString(),
                Importance = 9, // Corrections are high importance
                Tags = new List<string> { "correction", parameters?["category"]?.ToString() ?? "general" },
                CreatedAt = DateTime.Now,
                Source = "revit-ai"
            };

            var memories = LoadMemories();
            memories.Add(memory);
            SaveMemories(memories);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                id = memory.Id,
                message = "Correction stored with high priority"
            });
        }

        private string HandleMemoryGetCorrections(JObject parameters)
        {
            var project = parameters?["project"]?.ToString();
            var limit = parameters?["limit"]?.ToObject<int>() ?? 10;

            var memories = LoadMemories();

            var corrections = memories
                .Where(m => m.MemoryType == "correction" && (string.IsNullOrEmpty(project) || m.Project == project))
                .OrderByDescending(m => m.CreatedAt)
                .Take(limit)
                .Select(m => new
                {
                    m.Id,
                    m.Content,
                    m.Project,
                    m.Tags,
                    createdAt = m.CreatedAt.ToString("o")
                })
                .ToList();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                count = corrections.Count,
                corrections = corrections
            });
        }

        private string HandleMemorySummarizeSession(JObject parameters)
        {
            var project = parameters?["project"]?.ToString();
            var summary = parameters?["summary"]?.ToString();

            if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(summary))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "project and summary are required" });
            }

            var keyOutcomes = parameters?["keyOutcomes"]?.ToObject<List<string>>() ?? new List<string>();
            var decisionsMade = parameters?["decisionsMade"]?.ToObject<List<string>>() ?? new List<string>();
            var problemsSolved = parameters?["problemsSolved"]?.ToObject<List<string>>() ?? new List<string>();
            var openQuestions = parameters?["openQuestions"]?.ToObject<List<string>>() ?? new List<string>();
            var nextSteps = parameters?["nextSteps"]?.ToObject<List<string>>() ?? new List<string>();

            var contentBuilder = new System.Text.StringBuilder();
            contentBuilder.AppendLine($"SESSION SUMMARY - {project}");
            contentBuilder.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
            contentBuilder.AppendLine();
            contentBuilder.AppendLine($"Summary: {summary}");

            if (keyOutcomes.Count > 0)
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("Key Outcomes:");
                foreach (var outcome in keyOutcomes)
                    contentBuilder.AppendLine($"  - {outcome}");
            }

            if (decisionsMade.Count > 0)
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("Decisions Made:");
                foreach (var decision in decisionsMade)
                    contentBuilder.AppendLine($"  - {decision}");
            }

            if (problemsSolved.Count > 0)
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("Problems Solved:");
                foreach (var problem in problemsSolved)
                    contentBuilder.AppendLine($"  - {problem}");
            }

            if (openQuestions.Count > 0)
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("Open Questions:");
                foreach (var question in openQuestions)
                    contentBuilder.AppendLine($"  - {question}");
            }

            if (nextSteps.Count > 0)
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("Next Steps:");
                foreach (var step in nextSteps)
                    contentBuilder.AppendLine($"  - {step}");
            }

            var memory = new MemoryItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Content = contentBuilder.ToString(),
                MemoryType = "session",
                Project = project,
                Importance = 8,
                Tags = new List<string> { "session-summary", project.ToLower().Replace(" ", "-") },
                CreatedAt = DateTime.Now,
                Source = "revit-ai"
            };

            var memories = LoadMemories();
            memories.Add(memory);
            SaveMemories(memories);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                id = memory.Id,
                message = "Session summary stored"
            });
        }

        private string HandleMemoryStats()
        {
            var memories = LoadMemories();

            var stats = new
            {
                success = true,
                totalMemories = memories.Count,
                byType = memories.GroupBy(m => m.MemoryType).ToDictionary(g => g.Key, g => g.Count()),
                byProject = memories.Where(m => !string.IsNullOrEmpty(m.Project))
                    .GroupBy(m => m.Project).ToDictionary(g => g.Key, g => g.Count()),
                corrections = memories.Count(m => m.MemoryType == "correction"),
                recentCount = memories.Count(m => m.CreatedAt > DateTime.Now.AddDays(-7)),
                oldestMemory = memories.Min(m => (DateTime?)m.CreatedAt)?.ToString("g"),
                newestMemory = memories.Max(m => (DateTime?)m.CreatedAt)?.ToString("g")
            };

            return JsonConvert.SerializeObject(stats);
        }

        #endregion

        /// <summary>
        /// Memory item for local storage
        /// </summary>
        private class MemoryItem
        {
            public string Id { get; set; }
            public string Content { get; set; }
            public string MemoryType { get; set; }
            public string Project { get; set; }
            public int Importance { get; set; }
            public List<string> Tags { get; set; }
            public DateTime CreatedAt { get; set; }
            public string Source { get; set; }
        }

        private async void InputTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Ctrl+Enter or just Enter (when not holding Shift) to submit
            if (e.Key == System.Windows.Input.Key.Enter && !_isProcessing)
            {
                bool ctrlPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
                bool shiftPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

                // Submit on Ctrl+Enter, or plain Enter (Shift+Enter adds newline)
                if (ctrlPressed || !shiftPressed)
                {
                    e.Handled = true;
                    await SendMessage();
                }
                // Shift+Enter allows adding a newline
            }
        }

        private async Task SendMessage()
        {
            var message = _inputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(message) || _isProcessing) return;

            // Track for feedback context
            _lastUserMessage = message;
            _lastToolCall = null;

            _inputTextBox.Text = "";
            AddUserMessage(message);
            SetProcessing(true);
            ShowProgress("Thinking...");

            try
            {
                var projectName = _uiApp?.ActiveUIDocument?.Document?.Title ?? "Unknown";

                // Load CORE knowledge only to stay within Haiku's 200K context limit
                // Agent can use getKnowledgeFile tool to load additional files on demand
                var knowledgeBase = LoadCoreKnowledge();

                var systemPrompt = $@"You are an expert Revit automation assistant with full access to the Revit API. You are integrated directly into Autodesk Revit and can read and modify the model.

CURRENT PROJECT: {projectName}

YOUR CAPABILITIES:
- Query model data: getProjectInfo, getViews, getSheets, getElements, getRooms, getLevels, getWalls, getDoors, getWindows
- VISUAL VERIFICATION: analyzeView - SEE what you're doing! Capture and analyze views to verify your work
- Capture visuals: captureViewport (take screenshots of current view)
- Spatial analysis: checkForOverlaps, suggestPlacementLocation, findEmptySpaceOnSheet
- Create elements: createWall, placeDoor, placeWindow, placeFamilyInstance
- Annotations: placeTextNote, placeKeynote, tagElements
- Sheets/Views: createSheet, placeViewOnSheet, duplicateView

IMPORTANT - USE YOUR EYES:
After placing elements on sheets, USE analyzeView to SEE the result and verify it worked!
Example: After placeViewOnSheet, call analyzeView with question: 'Is the viewport visible and positioned correctly?'
This helps you catch issues like:
- Views that didn't actually get placed
- Overlapping viewports
- Elements in wrong locations
- Empty sheets that should have content

{knowledgeBase}

STYLE:
- Be direct and technical
- Give specific element counts, names, and IDs
- When something is wrong, explain exactly what and suggest how to fix it
- Don't just describe what you could do - actually do it
- Follow the WORKFLOWS exactly as specified above
- VERIFY your work visually when placing elements on sheets";

                await _agent.RunAsync(message, systemPrompt);
            }
            catch (Exception ex)
            {
                AddErrorMessage(ex.Message);
                HideProgress();
                SetProcessing(false);
            }
        }

        private void StopAgent()
        {
            _agent?.Stop();
            HideProgress();
            SetProcessing(false);
            AddSystemMessage("Operation cancelled.");
        }

        private void ClearChat()
        {
            _chatHistory.Children.Clear();
            _agent?.ClearHistory();
            _sessionMessages.Clear();  // Clear session memory too
            AddAssistantMessage("Chat cleared. How can I help you?");
        }

        #region Message Display Methods

        private void AddUserMessage(string text)
        {
            // Track for session persistence
            TrackMessage("user", text);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                CornerRadius = new CornerRadius(12, 12, 0, 12),
                Padding = new Thickness(12),
                Margin = new Thickness(50, 8, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            border.Child = new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            _chatHistory.Children.Add(border);
            ScrollToBottom();
        }

        private void AddAssistantMessage(string text)
        {
            // Track for session persistence
            TrackMessage("assistant", text);

            _lastAssistantResponse = text;
            _feedbackMessageIndex++;
            var messageIndex = _feedbackMessageIndex;

            // Main container
            var container = new StackPanel { Margin = new Thickness(8, 8, 50, 8), HorizontalAlignment = HorizontalAlignment.Left };

            // Message bubble
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(12, 12, 12, 0),
                Padding = new Thickness(12)
            };
            border.Child = new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            container.Children.Add(border);

            // Feedback buttons (thumbs up/down)
            var feedbackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // Store context for this message
            var userMsg = _lastUserMessage;
            var assistMsg = text;
            var toolCall = _lastToolCall;

            // Thumbs up button
            var thumbsUp = new Button
            {
                Content = "\U0001F44D",
                FontSize = 14,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = messageIndex
            };
            thumbsUp.Click += (s, e) => OnThumbsUp(userMsg, assistMsg, (Button)s, feedbackPanel);
            feedbackPanel.Children.Add(thumbsUp);

            // Thumbs down button
            var thumbsDown = new Button
            {
                Content = "\U0001F44E",
                FontSize = 14,
                Padding = new Thickness(6, 2, 6, 2),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = messageIndex
            };
            thumbsDown.Click += (s, e) => OnThumbsDown(userMsg, assistMsg, toolCall, (Button)s, feedbackPanel);
            feedbackPanel.Children.Add(thumbsDown);

            container.Children.Add(feedbackPanel);
            _chatHistory.Children.Add(container);
            ScrollToBottom();
        }

        private void OnThumbsUp(string userMsg, string assistMsg, Button button, StackPanel panel)
        {
            // Change button to indicate it was clicked
            button.Content = "\u2705"; // Checkmark
            button.Foreground = new SolidColorBrush(Color.FromRgb(80, 200, 80));
            button.IsEnabled = false;

            // Disable the other button
            foreach (var child in panel.Children)
            {
                if (child is Button btn && btn != button)
                {
                    btn.IsEnabled = false;
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                }
            }

            // Learn from this successful interaction
            // Extract method from last tool call if available
            var method = _lastToolCall?.Replace("Calling: ", "") ?? "unknown";
            _agent?.ReportSuccess(userMsg, method, null);
        }

        private void OnThumbsDown(string userMsg, string assistMsg, string toolCall, Button button, StackPanel panel)
        {
            // Change button to indicate it was clicked
            button.Content = "\u274C"; // X mark
            button.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 80));
            button.IsEnabled = false;

            // Disable the other button
            foreach (var child in panel.Children)
            {
                if (child is Button btn && btn != button)
                {
                    btn.IsEnabled = false;
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                }
            }

            // Show feedback dialog to capture what went wrong
            ShowFeedbackDialog(userMsg, assistMsg, toolCall);
        }

        private void ShowFeedbackDialog(string userMsg, string assistMsg, string toolCall)
        {
            // Create a simple feedback dialog
            var dialog = new Window
            {
                Title = "What went wrong?",
                Width = 400,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(20) };

            stack.Children.Add(new TextBlock
            {
                Text = "Help me learn from this mistake:",
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var issueBox = new System.Windows.Controls.TextBox
            {
                Height = 100,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(8)
            };
            issueBox.Text = ""; // Placeholder
            stack.Children.Add(issueBox);

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
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Gray
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            buttonPanel.Children.Add(cancelBtn);

            var submitBtn = new Button
            {
                Content = "Submit Feedback",
                Padding = new Thickness(20, 8, 20, 8),
                Background = new SolidColorBrush(Color.FromRgb(0, 100, 180)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            submitBtn.Click += (s, e) =>
            {
                var issue = issueBox.Text;
                if (!string.IsNullOrWhiteSpace(issue))
                {
                    // Store the correction
                    _agent?.ReportCorrection(
                        whatWasAttempted: $"User asked: {userMsg}",
                        whatWentWrong: issue,
                        correctApproach: "User feedback - needs improvement"
                    );

                    AddToolMessage("Thanks for the feedback! I'll learn from this.", true);
                }
                dialog.Close();
            };
            buttonPanel.Children.Add(submitBtn);

            stack.Children.Add(buttonPanel);
            dialog.Content = stack;
            dialog.ShowDialog();
        }

        private void AddToolMessage(string text, bool isResult)
        {
            // Track tool results for session persistence (skip calls to reduce clutter)
            if (isResult && text.Length < 500)  // Only track short results
            {
                TrackMessage("tool", text);
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                BorderBrush = isResult ? new SolidColorBrush(Color.FromRgb(16, 124, 16)) : new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(10),
                Margin = new Thickness(20, 4, 20, 4)
            };
            border.Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas")
            };
            _chatHistory.Children.Add(border);
            ScrollToBottom();
        }

        private void AddErrorMessage(string text)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 30, 30)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(8)
            };
            border.Child = new TextBlock
            {
                Text = "Error: " + text,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };
            _chatHistory.Children.Add(border);
            ScrollToBottom();
        }

        private void AddSystemMessage(string text)
        {
            _chatHistory.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            ScrollToBottom();
        }

        /// <summary>
        /// Display an image in the chat (for viewport captures, renders, etc.)
        /// </summary>
        private void AddImageMessage(string imagePath, string caption = null)
        {
            try
            {
                if (!File.Exists(imagePath))
                {
                    AddErrorMessage($"Image not found: {imagePath}");
                    return;
                }

                var container = new StackPanel
                {
                    Margin = new Thickness(8, 8, 50, 8),
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                // Add caption if provided
                if (!string.IsNullOrEmpty(caption))
                {
                    container.Children.Add(new TextBlock
                    {
                        Text = caption,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 4)
                    });
                }

                // Load and display image
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                var image = new Image
                {
                    Source = bitmap,
                    MaxWidth = 500,
                    MaxHeight = 400,
                    Stretch = Stretch.Uniform,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                // Click to open full size
                image.MouseLeftButtonUp += (s, e) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = imagePath,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                };

                var imageBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(4),
                    Child = image
                };

                container.Children.Add(imageBorder);

                // Add file path hint
                container.Children.Add(new TextBlock
                {
                    Text = $"📷 {Path.GetFileName(imagePath)} (click to open)",
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    FontSize = 10,
                    Margin = new Thickness(0, 4, 0, 0)
                });

                _chatHistory.Children.Add(container);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Failed to display image: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a tool result contains an image path and display it
        /// </summary>
        private bool TryDisplayImageFromResult(string toolResult)
        {
            try
            {
                var json = JObject.Parse(toolResult);

                // Check for imagePath or filePath in result
                var imagePath = json["imagePath"]?.ToString()
                    ?? json["filePath"]?.ToString()
                    ?? json["path"]?.ToString()
                    ?? json["result"]?["imagePath"]?.ToString()
                    ?? json["result"]?["filePath"]?.ToString();

                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    var ext = Path.GetExtension(imagePath).ToLower();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
                    {
                        var caption = json["caption"]?.ToString() ?? json["viewName"]?.ToString();
                        AddImageMessage(imagePath, caption);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        #endregion

        #region Progress UI

        private void ShowProgress(string title)
        {
            _progressPanel.Visibility = Visibility.Visible;
            _progressTitle.Text = title;
            _progressDetail.Text = "";
        }

        private void UpdateProgress(string detail)
        {
            _progressDetail.Text = detail;
        }

        private void HideProgress()
        {
            _progressPanel.Visibility = Visibility.Collapsed;
        }

        private void SetProcessing(bool isProcessing)
        {
            _isProcessing = isProcessing;
            _sendButton.IsEnabled = !isProcessing;
            _stopButton.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
            _statusText.Text = isProcessing ? "Processing..." : "Ready";
        }

        private void ScrollToBottom()
        {
            _chatScrollViewer.ScrollToEnd();
        }

        #endregion
    }
}
