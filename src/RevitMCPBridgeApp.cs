using System;
using System.Collections.Generic;
using System.Windows;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.Attributes;
using Serilog;

namespace RevitMCPBridge
{
    public class RevitMCPBridgeApp : IExternalApplication
    {
        private static MCPServer _mcpServer;
        private static UIApplication _uiApplication;
        private static string _tabName = "BIM Monkey";
        private static MCPRequestHandler _requestHandler;
        private static ExternalEvent _externalEvent;

        // Dialog handling - disabled by default to allow normal user interaction
        private static bool _autoHandleDialogs = false;
        private static int _defaultDialogResult = 1; // 1 = OK/Yes, 2 = No, 0 = Cancel
        private static List<DialogRecord> _dialogHistory = new List<DialogRecord>();
        private static object _dialogLock = new object();

        // Dialog history record
        public class DialogRecord
        {
            public DateTime Timestamp { get; set; }
            public string DialogId { get; set; }
            public string Message { get; set; }
            public string[] Buttons { get; set; }
            public int ResultClicked { get; set; }
            public string ResultName { get; set; }
        }

        // Public accessors for dialog settings
        public static bool AutoHandleDialogs
        {
            get => _autoHandleDialogs;
            set => _autoHandleDialogs = value;
        }

        public static int DefaultDialogResult
        {
            get => _defaultDialogResult;
            set => _defaultDialogResult = value;
        }

        public static List<DialogRecord> GetDialogHistory()
        {
            lock (_dialogLock)
            {
                return new List<DialogRecord>(_dialogHistory);
            }
        }

        public static void ClearDialogHistory()
        {
            lock (_dialogLock)
            {
                _dialogHistory.Clear();
            }
        }
        
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Store UI application reference and initialize ChangeTracker
                application.ControlledApplication.ApplicationInitialized += (sender, args) =>
                {
                    _uiApplication = new UIApplication(sender as Autodesk.Revit.ApplicationServices.Application);

                    // Initialize the ChangeTracker for real-time change detection
                    try
                    {
                        ChangeTracker.Instance.Initialize(_uiApplication);
                        Log.Information("ChangeTracker initialized for real-time change detection");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to initialize ChangeTracker");
                    }

                    // Fix: auto-restart pipe server when a new document is opened.
                    // Revit's named pipe context becomes stale after switching files —
                    // a fresh Stop+Start clears any dead connections and re-initialises
                    // the server against the newly active document.
                    _uiApplication.Application.DocumentOpened += (s, e) =>
                    {
                        try
                        {
                            if (_mcpServer != null)
                            {
                                _mcpServer.Stop();
                                _mcpServer.Start();
                                Log.Information($"MCP Server restarted after document open: {e.Document.Title}");
                            }
                        }
                        catch (Exception restartEx)
                        {
                            Log.Error(restartEx, "Failed to restart MCP Server after document open");
                        }
                    };
                };
                
                // Initialize logger
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins", "2026", "Logs",
                    $"mcp_{DateTime.Now:yyyyMMdd}.log");
                
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(logPath, 
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();
                
                Log.Information("Starting MCP Bridge for Revit 2026");

                // Initialize MCP request handler and external event
                _requestHandler = new MCPRequestHandler();
                _externalEvent = ExternalEvent.Create(_requestHandler);
                Log.Information("MCP Request Handler and ExternalEvent initialized");

                // Subscribe to dialog events for automatic handling
                application.DialogBoxShowing += OnDialogBoxShowing;
                Log.Information("Dialog handler subscribed");

                // Create ribbon tab - MCP Bridge gets its own tab!
                try
                {
                    application.CreateRibbonTab(_tabName);
                    Log.Information($"Created ribbon tab: {_tabName}");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Ribbon tab already exists: {ex.Message}");
                }
                
                // Create panels
                CreateServerPanel(application);
                
                // Auto-start MCP server
                try
                {
                    _mcpServer = new MCPServer();
                    _mcpServer.Start();
                    Log.Information("MCP Server started automatically on Revit startup");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to auto-start MCP server");
                    // Don't fail the whole add-in if server fails to start
                    // User can still manually start it via button
                }
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize MCP Bridge");
                Autodesk.Revit.UI.TaskDialog.Show("MCP Bridge Error", $"Failed to initialize: {ex.Message}");
                return Result.Failed;
            }
        }
        
        private void CreateServerPanel(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel(_tabName, "Server Control");

            // Claude button
            var claudeButtonData = new PushButtonData(
                "OpenClaude",
                "Claude\nCode",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.OpenClaudeCommand")
            {
                ToolTip = "Open Claude Code in BIM Monkey folder"
            };
            var claudeButton = panel.AddItem(claudeButtonData) as PushButton;
            claudeButton.LargeImage = CreateButtonIcon("claude", 32);
            claudeButton.Image = CreateButtonIcon("claude", 16);

            // Platform button
            var platformButtonData = new PushButtonData(
                "BimMonkeyPlatform",
                "Web\nPlatform",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.OpenPlatformCommand")
            {
                ToolTip = "Open BIM Monkey dashboard"
            };
            var platformButton = panel.AddItem(platformButtonData) as PushButton;
            platformButton.LargeImage = CreateButtonIcon("monkey", 32);
            platformButton.Image = CreateButtonIcon("monkey", 16);

            panel.AddSeparator();

            // Start Server button
            var startButtonData = new PushButtonData(
                "StartMCPServer",
                "Start\nServer",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.StartServerCommand")
            {
                ToolTip = "Start BIM Monkey server",
                AvailabilityClassName = "RevitMCPBridge.Commands.ServerStoppedAvailability"
            };
            var startButton = panel.AddItem(startButtonData) as PushButton;
            startButton.LargeImage = CreateButtonIcon("start", 32);
            startButton.Image = CreateButtonIcon("start", 16);

            // Stop Server button
            var stopButtonData = new PushButtonData(
                "StopMCPServer",
                "Stop\nServer",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.StopServerCommand")
            {
                ToolTip = "Stop BIM Monkey server",
                AvailabilityClassName = "RevitMCPBridge.Commands.ServerRunningAvailability"
            };
            var stopButton = panel.AddItem(stopButtonData) as PushButton;
            stopButton.LargeImage = CreateButtonIcon("stop", 32);
            stopButton.Image = CreateButtonIcon("stop", 16);

            // Server Status button
            var statusButtonData = new PushButtonData(
                "MCPServerStatus",
                "Server\nStatus",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.ServerStatusCommand")
            {
                ToolTip = "Check BIM Monkey server status"
            };
            var statusButton = panel.AddItem(statusButtonData) as PushButton;
            statusButton.LargeImage = CreateButtonIcon("status", 32);
            statusButton.Image = CreateButtonIcon("status", 16);
        }

        private void CreateToolsPanel(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel(_tabName, "MCP Tools");
            
            // Query Revit button
            var queryButtonData = new PushButtonData(
                "QueryRevit",
                "Query\nRevit",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.QueryRevitCommand")
            {
                ToolTip = "Query Revit model data",
                LongDescription = "Execute queries against the current Revit model through MCP"
            };
            
            var queryButton = panel.AddItem(queryButtonData) as PushButton;
            queryButton.LargeImage = CreateButtonIcon("query", 32);
            queryButton.Image = CreateButtonIcon("query", 16);
            
            // Execute Command button
            var executeButtonData = new PushButtonData(
                "ExecuteMCPCommand",
                "Execute\nCommand",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.ExecuteCommandCommand")
            {
                ToolTip = "Execute MCP command",
                LongDescription = "Execute a command in Revit through the MCP Bridge"
            };
            
            var executeButton = panel.AddItem(executeButtonData) as PushButton;
            executeButton.LargeImage = CreateButtonIcon("execute", 32);
            executeButton.Image = CreateButtonIcon("execute", 16);
            
            // View Logs button
            var logsButtonData = new PushButtonData(
                "ViewMCPLogs",
                "View\nLogs",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.ViewLogsCommand")
            {
                ToolTip = "View MCP Bridge logs",
                LongDescription = "Opens the log viewer to see MCP Bridge activity and debug information"
            };
            
            var logsButton = panel.AddItem(logsButtonData) as PushButton;
            logsButton.LargeImage = CreateButtonIcon("logs", 32);
            logsButton.Image = CreateButtonIcon("logs", 16);

            // Add separator
            panel.AddSeparator();

            // SketchPad button - Draw to Revit!
            var sketchPadButtonData = new PushButtonData(
                "SketchPad",
                "Sketch\nPad",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.SketchPadCommand")
            {
                ToolTip = "SketchPad - Draw to Revit",
                LongDescription = "Open the SketchPad to draw walls directly into Revit in real-time. You can also load floor plan images to trace over."
            };

            var sketchPadButton = panel.AddItem(sketchPadButtonData) as PushButton;
            sketchPadButton.LargeImage = CreateButtonIcon("sketchpad", 32);
            sketchPadButton.Image = CreateButtonIcon("sketchpad", 16);

            // Floor Plan Tracer button
            var tracerButtonData = new PushButtonData(
                "FloorPlanTracer",
                "Floor Plan\nTracer",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.FloorPlanTracerCommand")
            {
                ToolTip = "Floor Plan Tracer",
                LongDescription = "Load a floor plan image and trace walls to create them in Revit. Supports auto-detection of walls."
            };

            var tracerButton = panel.AddItem(tracerButtonData) as PushButton;
            tracerButton.LargeImage = CreateButtonIcon("tracer", 32);
            tracerButton.Image = CreateButtonIcon("tracer", 16);

            // Detail Library button
            var detailLibraryButtonData = new PushButtonData(
                "DetailLibrary",
                "Detail\nLibrary",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.ShowDetailLibraryCommand")
            {
                ToolTip = "Detail Library",
                LongDescription = "Browse, preview, and import detail RVT files from your detail library. Search across categories and import directly into your current document."
            };

            var detailLibraryButton = panel.AddItem(detailLibraryButtonData) as PushButton;
            detailLibraryButton.LargeImage = CreateButtonIcon("library", 32);
            detailLibraryButton.Image = CreateButtonIcon("library", 16);

            // Add separator before AI tools
            panel.AddSeparator();

            // AI Assistant button - Launch the Agent Chat Panel
            var aiAssistantButtonData = new PushButtonData(
                "AIAssistant",
                "AI\nAssistant",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge2026.AgentFramework.LaunchAgentCommand")
            {
                ToolTip = "AI Assistant - Chat with Claude",
                LongDescription = "Opens an AI-powered assistant that can help you automate Revit tasks using natural language. " +
                                  "Features intelligent placement, collision detection, and full access to 400+ Revit commands."
            };

            var aiAssistantButton = panel.AddItem(aiAssistantButtonData) as PushButton;
            aiAssistantButton.LargeImage = CreateButtonIcon("ai", 32);
            aiAssistantButton.Image = CreateButtonIcon("ai", 16);

            // Smart Element Panel button - Shows element info and linked views
            var smartPanelButtonData = new PushButtonData(
                "SmartElementPanel",
                "Smart\nPanel",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.ShowSmartPanelCommand")
            {
                ToolTip = "Smart Element Info Panel",
                LongDescription = "Opens a panel that displays comprehensive information about selected elements including specifications, " +
                                  "linked drafting views, schedules, manufacturer data, and fire ratings. Click links to navigate directly to views.",
                AvailabilityClassName = "RevitMCPBridge.Commands.SmartPanelAvailability"
            };

            var smartPanelButton = panel.AddItem(smartPanelButtonData) as PushButton;
            smartPanelButton.LargeImage = CreateButtonIcon("smart", 32);
            smartPanelButton.Image = CreateButtonIcon("smart", 16);
        }

        private void CreateSettingsPanel(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel(_tabName, "Settings");
            
            // Settings button
            var settingsButtonData = new PushButtonData(
                "MCPSettings",
                "MCP\nSettings",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.SettingsCommand")
            {
                ToolTip = "Configure MCP Bridge settings",
                LongDescription = "Configure server port, logging, and other MCP Bridge options"
            };
            
            var settingsButton = panel.AddItem(settingsButtonData) as PushButton;
            settingsButton.LargeImage = CreateButtonIcon("settings", 32);
            settingsButton.Image = CreateButtonIcon("settings", 16);
            
            // Help button
            var helpButtonData = new PushButtonData(
                "MCPHelp",
                "Help",
                Assembly.GetExecutingAssembly().Location,
                "RevitMCPBridge.Commands.HelpCommand")
            {
                ToolTip = "MCP Bridge help",
                LongDescription = "View documentation and examples for using MCP Bridge"
            };
            
            var helpButton = panel.AddItem(helpButtonData) as PushButton;
            helpButton.LargeImage = CreateButtonIcon("help", 32);
            helpButton.Image = CreateButtonIcon("help", 16);
        }
        
        private BitmapSource CreateButtonIcon(string iconType, int size)
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    switch (iconType)
                    {
                        case "claude":
                            DrawClaudeIcon(dc, size);
                            break;
                        case "start":
                            DrawStartIcon(dc, size);
                            break;
                        case "stop":
                            DrawStopIcon(dc, size);
                            break;
                        case "status":
                            DrawStatusIcon(dc, size);
                            break;
                        case "query":
                            DrawQueryIcon(dc, size);
                            break;
                        case "execute":
                            DrawExecuteIcon(dc, size);
                            break;
                        case "logs":
                            DrawLogsIcon(dc, size);
                            break;
                        case "settings":
                            DrawSettingsIcon(dc, size);
                            break;
                        case "help":
                            DrawHelpIcon(dc, size);
                            break;
                        case "sketchpad":
                            DrawSketchPadIcon(dc, size);
                            break;
                        case "tracer":
                            DrawTracerIcon(dc, size);
                            break;
                        case "ai":
                            DrawAiIcon(dc, size);
                            break;
                        case "monkey":
                            DrawMonkeyIcon(dc, size);
                            break;
                        case "library":
                            DrawLibraryIcon(dc, size);
                            break;
                        case "smart":
                            DrawSmartIcon(dc, size);
                            break;
                    }
                }
                
                var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to create icon for {iconType}");
                return null;
            }
        }
        
        private void DrawMonkeyIcon(DrawingContext dc, int size)
        {
            // Monkey face: white fill, dark outline — matches bimmonkey-mark.svg
            double s = size / 80.0;
            var white = new SolidColorBrush(Colors.White);
            var dark = new SolidColorBrush(Color.FromRgb(17, 17, 17));
            var pen = new Pen(dark, Math.Max(1.0, 1.5 * s));
            var penThin = new Pen(dark, Math.Max(0.8, 1.2 * s));

            // Ears (behind body — draw first)
            dc.DrawEllipse(white, pen, new Point(20 * s, 36 * s), 8 * s, 10 * s);
            dc.DrawEllipse(white, pen, new Point(60 * s, 36 * s), 8 * s, 10 * s);
            // Body
            dc.DrawEllipse(white, pen, new Point(40 * s, 38 * s), 20 * s, 22 * s);
            // Muzzle
            dc.DrawEllipse(white, penThin, new Point(40 * s, 48 * s), 10 * s, 7 * s);
            // Eyes
            dc.DrawEllipse(dark, null, new Point(34 * s, 34 * s), 3.5 * s, 3.5 * s);
            dc.DrawEllipse(dark, null, new Point(46 * s, 34 * s), 3.5 * s, 3.5 * s);
            // Nostrils (higher on muzzle)
            dc.DrawEllipse(dark, null, new Point(37 * s, 46 * s), 1.4 * s, 1.4 * s);
            dc.DrawEllipse(dark, null, new Point(43 * s, 46 * s), 1.4 * s, 1.4 * s);
            // Smile
            var smileFigure = new System.Windows.Media.PathFigure { StartPoint = new Point(33 * s, 51 * s) };
            smileFigure.Segments.Add(new System.Windows.Media.QuadraticBezierSegment(new Point(40 * s, 55 * s), new Point(47 * s, 51 * s), true));
            var smilePath = new System.Windows.Media.PathGeometry();
            smilePath.Figures.Add(smileFigure);
            var smilePen = new Pen(dark, Math.Max(0.8, 1.0 * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, smilePen, smilePath);
        }

        private void DrawClaudeIcon(DrawingContext dc, int size)
        {
            // Claude Code icon: dark rounded square with coral/orange ">" mark
            var margin = size * 0.08;
            var rect = new Rect(margin, margin, size - 2 * margin, size - 2 * margin);
            var bg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            dc.DrawRoundedRectangle(bg, null, rect, size * 0.15, size * 0.15);

            // Coral ">" chevron — Claude Code terminal prompt style
            var coral = new SolidColorBrush(Color.FromRgb(214, 103, 76));
            var pen = new Pen(coral, size * 0.115) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var cx = size * 0.48;
            var cy = size * 0.5;
            var arm = size * 0.19;
            dc.DrawLine(pen, new Point(cx - arm * 0.5, cy - arm), new Point(cx + arm * 0.5, cy));
            dc.DrawLine(pen, new Point(cx - arm * 0.5, cy + arm), new Point(cx + arm * 0.5, cy));
        }

        private void DrawStartIcon(DrawingContext dc, int size)
        {
            // Green play button
            var margin = size * 0.2;
            var playPath = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(margin, margin) };
            figure.Segments.Add(new LineSegment(new Point(margin, size - margin), true));
            figure.Segments.Add(new LineSegment(new Point(size - margin, size / 2), true));
            figure.IsClosed = true;
            playPath.Figures.Add(figure);
            
            var gradient = new LinearGradientBrush(
                Color.FromRgb(76, 175, 80),
                Color.FromRgb(56, 142, 60),
                90);
            
            dc.DrawGeometry(gradient, new Pen(new SolidColorBrush(Color.FromRgb(46, 125, 50)), 1), playPath);
        }
        
        private void DrawStopIcon(DrawingContext dc, int size)
        {
            // Red stop square
            var margin = size * 0.25;
            var rect = new Rect(margin, margin, size - 2 * margin, size - 2 * margin);
            
            var gradient = new LinearGradientBrush(
                Color.FromRgb(244, 67, 54),
                Color.FromRgb(211, 47, 47),
                90);
            
            dc.DrawRoundedRectangle(gradient, 
                new Pen(new SolidColorBrush(Color.FromRgb(183, 28, 28)), 1), 
                rect, 2, 2);
        }
        
        private void DrawStatusIcon(DrawingContext dc, int size)
        {
            // Status indicator with dots
            var centerY = size / 2;
            var dotRadius = size * 0.08;
            var spacing = size * 0.25;
            
            // Three dots
            for (int i = 0; i < 3; i++)
            {
                var x = size / 2 - spacing + i * spacing;
                var color = i == 0 ? Colors.Green : (i == 1 ? Colors.Orange : Colors.Gray);
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(x, centerY), dotRadius, dotRadius);
            }
            
            // Connection lines
            var linePen = new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1);
            dc.DrawLine(linePen, 
                new Point(size / 2 - spacing + dotRadius, centerY),
                new Point(size / 2 - dotRadius, centerY));
            dc.DrawLine(linePen,
                new Point(size / 2 + dotRadius, centerY),
                new Point(size / 2 + spacing - dotRadius, centerY));
        }
        
        private void DrawQueryIcon(DrawingContext dc, int size)
        {
            // Database/query icon
            var margin = size * 0.15;
            var width = size - 2 * margin;
            var height = size * 0.7;
            var ellipseHeight = height * 0.15;
            
            // Database cylinder
            var topEllipse = new EllipseGeometry(
                new Point(size / 2, margin + ellipseHeight / 2),
                width / 2, ellipseHeight / 2);
            
            var bodyPath = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(margin, margin + ellipseHeight / 2) };
            figure.Segments.Add(new LineSegment(new Point(margin, margin + height - ellipseHeight / 2), true));
            figure.Segments.Add(new ArcSegment(
                new Point(size - margin, margin + height - ellipseHeight / 2),
                new Size(width / 2, ellipseHeight / 2),
                0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(size - margin, margin + ellipseHeight / 2), true));
            bodyPath.Figures.Add(figure);
            
            var gradient = new LinearGradientBrush(
                Color.FromRgb(33, 150, 243),
                Color.FromRgb(25, 118, 210),
                90);
            
            dc.DrawGeometry(gradient, new Pen(new SolidColorBrush(Color.FromRgb(13, 71, 161)), 1), topEllipse);
            dc.DrawGeometry(gradient, new Pen(new SolidColorBrush(Color.FromRgb(13, 71, 161)), 1), bodyPath);
            
            // Query arrow
            var arrowPen = new Pen(new SolidColorBrush(Colors.White), 2);
            dc.DrawLine(arrowPen,
                new Point(size * 0.35, margin + height * 0.4),
                new Point(size * 0.65, margin + height * 0.4));
            dc.DrawLine(arrowPen,
                new Point(size * 0.55, margin + height * 0.3),
                new Point(size * 0.65, margin + height * 0.4));
            dc.DrawLine(arrowPen,
                new Point(size * 0.55, margin + height * 0.5),
                new Point(size * 0.65, margin + height * 0.4));
        }
        
        private void DrawExecuteIcon(DrawingContext dc, int size)
        {
            // Terminal/command icon
            var margin = size * 0.15;
            var rect = new Rect(margin, margin, size - 2 * margin, size - 2 * margin);
            
            // Terminal background
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(33, 33, 33)),
                new Pen(new SolidColorBrush(Color.FromRgb(66, 66, 66)), 1),
                rect, 3, 3);
            
            // Terminal prompt
            var promptPen = new Pen(new SolidColorBrush(Colors.LimeGreen), 2);
            var promptY = size * 0.45;
            dc.DrawLine(promptPen,
                new Point(margin + size * 0.1, promptY),
                new Point(margin + size * 0.25, promptY));
            
            // Cursor
            var cursorRect = new Rect(margin + size * 0.3, promptY - size * 0.05, size * 0.02, size * 0.1);
            dc.DrawRectangle(new SolidColorBrush(Colors.White), null, cursorRect);
        }
        
        private void DrawLogsIcon(DrawingContext dc, int size)
        {
            // Document with lines
            var margin = size * 0.15;
            var docWidth = size - 2 * margin;
            var docHeight = size - 2 * margin;
            
            // Document background
            var docRect = new Rect(margin, margin, docWidth, docHeight);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Colors.White),
                new Pen(new SolidColorBrush(Color.FromRgb(158, 158, 158)), 1),
                docRect, 2, 2);
            
            // Log lines
            var linePen = new Pen(new SolidColorBrush(Color.FromRgb(97, 97, 97)), 1);
            var lineSpacing = docHeight / 6;
            for (int i = 1; i <= 4; i++)
            {
                var y = margin + i * lineSpacing;
                var lineWidth = i % 2 == 0 ? docWidth * 0.7 : docWidth * 0.85;
                dc.DrawLine(linePen,
                    new Point(margin + docWidth * 0.1, y),
                    new Point(margin + docWidth * 0.1 + lineWidth, y));
            }
        }
        
        private void DrawSettingsIcon(DrawingContext dc, int size)
        {
            // Gear icon
            var centerX = size / 2;
            var centerY = size / 2;
            var outerRadius = size * 0.35;
            var innerRadius = size * 0.15;
            var teethCount = 8;
            
            var gearPath = new PathGeometry();
            
            for (int i = 0; i < teethCount; i++)
            {
                var angle = i * 360.0 / teethCount * Math.PI / 180;
                var nextAngle = (i + 1) * 360.0 / teethCount * Math.PI / 180;
                var midAngle = (angle + nextAngle) / 2;
                
                var toothFigure = new PathFigure
                {
                    StartPoint = new Point(
                        centerX + Math.Cos(angle - 0.1) * outerRadius * 0.8,
                        centerY + Math.Sin(angle - 0.1) * outerRadius * 0.8)
                };
                
                toothFigure.Segments.Add(new LineSegment(
                    new Point(
                        centerX + Math.Cos(angle) * outerRadius,
                        centerY + Math.Sin(angle) * outerRadius), true));
                
                toothFigure.Segments.Add(new LineSegment(
                    new Point(
                        centerX + Math.Cos(angle + 0.1) * outerRadius,
                        centerY + Math.Sin(angle + 0.1) * outerRadius), true));
                
                toothFigure.Segments.Add(new ArcSegment(
                    new Point(
                        centerX + Math.Cos(midAngle) * outerRadius * 0.8,
                        centerY + Math.Sin(midAngle) * outerRadius * 0.8),
                    new Size(outerRadius * 0.8, outerRadius * 0.8),
                    0, false, SweepDirection.Clockwise, true));
                
                gearPath.Figures.Add(toothFigure);
            }
            
            var gradient = new RadialGradientBrush(
                Color.FromRgb(117, 117, 117),
                Color.FromRgb(66, 66, 66));
            
            dc.DrawGeometry(gradient, new Pen(new SolidColorBrush(Color.FromRgb(33, 33, 33)), 1), gearPath);
            
            // Center hole
            dc.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(33, 33, 33)),
                null,
                new Point(centerX, centerY),
                innerRadius, innerRadius);
        }
        
        private void DrawHelpIcon(DrawingContext dc, int size)
        {
            // Question mark in circle
            var centerX = size / 2;
            var centerY = size / 2;
            var radius = size * 0.35;
            
            // Circle
            var gradient = new RadialGradientBrush(
                Color.FromRgb(100, 181, 246),
                Color.FromRgb(33, 150, 243));
            
            dc.DrawEllipse(gradient, 
                new Pen(new SolidColorBrush(Color.FromRgb(25, 118, 210)), 1),
                new Point(centerX, centerY), radius, radius);
            
            // Question mark
            var questionMark = new FormattedText(
                "?",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), 
                    System.Windows.FontStyles.Normal, 
                    System.Windows.FontWeights.Bold, 
                    System.Windows.FontStretches.Normal),
                size * 0.5,
                new SolidColorBrush(Colors.White),
                96);
            
            dc.DrawText(questionMark, 
                new Point(centerX - questionMark.Width / 2, centerY - questionMark.Height / 2));
        }
        
        private void DrawSketchPadIcon(DrawingContext dc, int size)
        {
            // Pencil/sketch icon
            var margin = size * 0.15;

            // Pencil body
            var pencilPath = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(size * 0.2, size * 0.8) };
            figure.Segments.Add(new LineSegment(new Point(size * 0.25, size * 0.7), true));
            figure.Segments.Add(new LineSegment(new Point(size * 0.7, size * 0.25), true));
            figure.Segments.Add(new LineSegment(new Point(size * 0.8, size * 0.35), true));
            figure.Segments.Add(new LineSegment(new Point(size * 0.35, size * 0.8), true));
            figure.IsClosed = true;
            pencilPath.Figures.Add(figure);

            var gradient = new LinearGradientBrush(
                Color.FromRgb(255, 193, 7),
                Color.FromRgb(255, 160, 0),
                45);

            dc.DrawGeometry(gradient, new Pen(new SolidColorBrush(Color.FromRgb(245, 127, 23)), 1), pencilPath);

            // Pencil tip
            var tipPath = new PathGeometry();
            var tipFigure = new PathFigure { StartPoint = new Point(size * 0.2, size * 0.8) };
            tipFigure.Segments.Add(new LineSegment(new Point(size * 0.15, size * 0.85), true));
            tipFigure.Segments.Add(new LineSegment(new Point(size * 0.35, size * 0.8), true));
            tipFigure.IsClosed = true;
            tipPath.Figures.Add(tipFigure);

            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(62, 39, 35)), null, tipPath);

            // Paper/canvas background hint
            var paperRect = new Rect(size * 0.4, size * 0.4, size * 0.5, size * 0.5);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromRgb(189, 189, 189)), 1), paperRect);
        }

        private void DrawTracerIcon(DrawingContext dc, int size)
        {
            // Floor plan outline icon
            var margin = size * 0.15;

            // Outer rectangle (building outline)
            var outerRect = new Rect(margin, margin, size - 2 * margin, size - 2 * margin);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(33, 150, 243)), 2), outerRect);

            // Inner room divisions
            var linePen = new Pen(new SolidColorBrush(Color.FromRgb(100, 181, 246)), 1);

            // Horizontal division
            dc.DrawLine(linePen, new Point(margin, size * 0.5), new Point(size - margin, size * 0.5));

            // Vertical division
            dc.DrawLine(linePen, new Point(size * 0.4, margin), new Point(size * 0.4, size - margin));

            // Door gap indicator
            var doorGap = new Rect(size * 0.35, size * 0.47, size * 0.1, size * 0.06);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, doorGap);

            // Tracing cursor
            var cursorPath = new PathGeometry();
            var cursorFigure = new PathFigure { StartPoint = new Point(size * 0.7, size * 0.2) };
            cursorFigure.Segments.Add(new LineSegment(new Point(size * 0.7, size * 0.35), true));
            cursorFigure.Segments.Add(new LineSegment(new Point(size * 0.75, size * 0.3), true));
            cursorFigure.IsClosed = true;
            cursorPath.Figures.Add(cursorFigure);

            dc.DrawGeometry(new SolidColorBrush(Colors.Red), null, cursorPath);
        }

        private void DrawAiIcon(DrawingContext dc, int size)
        {
            // AI/Brain icon - stylized brain/circuit
            var margin = size * 0.15;
            var center = size / 2.0;

            // Background circle
            var bgGradient = new LinearGradientBrush(
                Color.FromRgb(138, 43, 226),  // Purple
                Color.FromRgb(75, 0, 130),    // Indigo
                90);
            dc.DrawEllipse(bgGradient, null, new Point(center, center), center - margin, center - margin);

            // Neural network nodes (small circles)
            var nodeColor = new SolidColorBrush(Colors.White);
            var nodeRadius = size * 0.06;

            // Center node (larger)
            dc.DrawEllipse(nodeColor, null, new Point(center, center), nodeRadius * 1.5, nodeRadius * 1.5);

            // Surrounding nodes
            var positions = new[]
            {
                new Point(center - size * 0.2, center - size * 0.15),
                new Point(center + size * 0.2, center - size * 0.15),
                new Point(center - size * 0.2, center + size * 0.15),
                new Point(center + size * 0.2, center + size * 0.15),
                new Point(center, center - size * 0.25),
                new Point(center, center + size * 0.25)
            };

            // Connection lines
            var linePen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1);
            foreach (var pos in positions)
            {
                dc.DrawLine(linePen, new Point(center, center), pos);
                dc.DrawEllipse(nodeColor, null, pos, nodeRadius, nodeRadius);
            }

            // Outer glow effect (optional sparkle)
            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 0.5);
            dc.DrawEllipse(null, glowPen, new Point(center, center), center - margin + 2, center - margin + 2);
        }

        private void DrawLibraryIcon(DrawingContext dc, int size)
        {
            // Library/folder icon with documents
            var margin = size * 0.15;
            var folderColor = new SolidColorBrush(Color.FromRgb(255, 183, 77));  // Orange/gold
            var docColor = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(200, 140, 50)), 1);

            // Folder back
            var folderBack = new RectangleGeometry(new Rect(margin, margin + size * 0.1, size - 2 * margin, size - 2 * margin - size * 0.1));
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(230, 160, 60)), borderPen, folderBack);

            // Folder tab
            var tabPath = new PathGeometry();
            var tabFigure = new PathFigure { StartPoint = new Point(margin, margin + size * 0.1) };
            tabFigure.Segments.Add(new LineSegment(new Point(margin, margin), true));
            tabFigure.Segments.Add(new LineSegment(new Point(margin + size * 0.3, margin), true));
            tabFigure.Segments.Add(new LineSegment(new Point(margin + size * 0.35, margin + size * 0.1), true));
            tabFigure.IsClosed = false;
            tabPath.Figures.Add(tabFigure);
            dc.DrawGeometry(null, borderPen, tabPath);

            // Document pages (stacked)
            var docWidth = size * 0.35;
            var docHeight = size * 0.45;
            var docLeft = size / 2 - docWidth / 2;
            var docTop = margin + size * 0.2;

            // Back page
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 0.5),
                new Rect(docLeft + 3, docTop - 2, docWidth, docHeight));

            // Front page
            dc.DrawRectangle(docColor,
                new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 0.5),
                new Rect(docLeft, docTop, docWidth, docHeight));

            // Lines on document
            var linePen = new Pen(new SolidColorBrush(Color.FromRgb(180, 180, 180)), 0.5);
            for (int i = 0; i < 3; i++)
            {
                var lineY = docTop + size * 0.1 + i * size * 0.1;
                dc.DrawLine(linePen,
                    new Point(docLeft + size * 0.05, lineY),
                    new Point(docLeft + docWidth - size * 0.05, lineY));
            }
        }

        private void DrawSmartIcon(DrawingContext dc, int size)
        {
            // Smart/Info icon - lightbulb with info badge
            var margin = size * 0.1;
            var center = size / 2.0;

            // Lightbulb background (yellow glow)
            var glowGradient = new RadialGradientBrush(
                Color.FromRgb(255, 235, 59),   // Yellow center
                Color.FromRgb(255, 193, 7));   // Amber edge
            dc.DrawEllipse(glowGradient, null, new Point(center, center - size * 0.05),
                size * 0.35, size * 0.35);

            // Lightbulb body
            var bulbPath = new PathGeometry();
            var bulbFigure = new PathFigure { StartPoint = new Point(center - size * 0.2, center) };

            // Left arc up to top
            bulbFigure.Segments.Add(new ArcSegment(
                new Point(center, center - size * 0.3),
                new Size(size * 0.25, size * 0.25),
                0, false, SweepDirection.Clockwise, true));

            // Right arc down
            bulbFigure.Segments.Add(new ArcSegment(
                new Point(center + size * 0.2, center),
                new Size(size * 0.25, size * 0.25),
                0, false, SweepDirection.Clockwise, true));

            // Base narrowing
            bulbFigure.Segments.Add(new LineSegment(new Point(center + size * 0.12, center + size * 0.15), true));
            bulbFigure.Segments.Add(new LineSegment(new Point(center - size * 0.12, center + size * 0.15), true));
            bulbFigure.IsClosed = true;
            bulbPath.Figures.Add(bulbFigure);

            var bulbGradient = new LinearGradientBrush(
                Color.FromRgb(255, 255, 255),
                Color.FromRgb(255, 241, 118),
                90);
            dc.DrawGeometry(bulbGradient, new Pen(new SolidColorBrush(Color.FromRgb(255, 160, 0)), 1), bulbPath);

            // Lightbulb base (screw part)
            var baseRect = new Rect(center - size * 0.1, center + size * 0.15, size * 0.2, size * 0.12);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(158, 158, 158)),
                new Pen(new SolidColorBrush(Color.FromRgb(97, 97, 97)), 0.5), baseRect);

            // Screw lines
            var screwPen = new Pen(new SolidColorBrush(Color.FromRgb(97, 97, 97)), 0.5);
            dc.DrawLine(screwPen,
                new Point(center - size * 0.08, center + size * 0.19),
                new Point(center + size * 0.08, center + size * 0.19));
            dc.DrawLine(screwPen,
                new Point(center - size * 0.08, center + size * 0.23),
                new Point(center + size * 0.08, center + size * 0.23));

            // Info badge (small circle with 'i')
            var badgeCenter = new Point(center + size * 0.25, center - size * 0.2);
            var badgeRadius = size * 0.15;

            // Badge background
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                new Pen(new SolidColorBrush(Colors.White), 1), badgeCenter, badgeRadius, badgeRadius);

            // Info 'i' symbol
            var infoText = new FormattedText(
                "i",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"),
                    System.Windows.FontStyles.Italic,
                    System.Windows.FontWeights.Bold,
                    System.Windows.FontStretches.Normal),
                size * 0.18,
                new SolidColorBrush(Colors.White),
                96);

            dc.DrawText(infoText,
                new Point(badgeCenter.X - infoText.Width / 2, badgeCenter.Y - infoText.Height / 2));

            // Rays emanating from bulb (smart/enlightenment)
            var rayPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 193, 7)), 1);
            var rayLength = size * 0.08;
            var rayDistance = size * 0.38;

            // Top ray
            dc.DrawLine(rayPen,
                new Point(center, center - rayDistance),
                new Point(center, center - rayDistance - rayLength));

            // Top-left ray
            dc.DrawLine(rayPen,
                new Point(center - rayDistance * 0.7, center - rayDistance * 0.5),
                new Point(center - (rayDistance + rayLength) * 0.7, center - (rayDistance + rayLength) * 0.5));

            // Top-right ray
            dc.DrawLine(rayPen,
                new Point(center + rayDistance * 0.7, center - rayDistance * 0.5),
                new Point(center + (rayDistance + rayLength) * 0.7, center - (rayDistance + rayLength) * 0.5));
        }

        // Dialog handling event
        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                var record = new DialogRecord
                {
                    Timestamp = DateTime.Now,
                    DialogId = e.DialogId
                };

                // Handle TaskDialog
                if (e is TaskDialogShowingEventArgs taskArgs)
                {
                    record.Message = taskArgs.Message;

                    // Log the dialog
                    Log.Information($"TaskDialog shown: {taskArgs.DialogId} - {taskArgs.Message}");

                    if (_autoHandleDialogs)
                    {
                        // Override with default result
                        taskArgs.OverrideResult(_defaultDialogResult);
                        record.ResultClicked = _defaultDialogResult;
                        record.ResultName = _defaultDialogResult == 1 ? "OK/Yes" :
                                           (_defaultDialogResult == 2 ? "No" : "Cancel");

                        Log.Information($"Auto-handled TaskDialog with result: {record.ResultName}");
                    }
                }
                // Handle standard MessageBox dialogs
                else if (e is MessageBoxShowingEventArgs msgArgs)
                {
                    record.Message = msgArgs.Message;

                    Log.Information($"MessageBox shown: {msgArgs.DialogId} - {msgArgs.Message}");

                    if (_autoHandleDialogs)
                    {
                        // Override with default result
                        msgArgs.OverrideResult(_defaultDialogResult);
                        record.ResultClicked = _defaultDialogResult;
                        record.ResultName = _defaultDialogResult == 1 ? "OK/Yes" :
                                           (_defaultDialogResult == 2 ? "No" : "Cancel");

                        Log.Information($"Auto-handled MessageBox with result: {record.ResultName}");
                    }
                }
                else
                {
                    // Generic dialog
                    record.Message = "Unknown dialog type";
                    Log.Information($"Generic dialog shown: {e.DialogId}");

                    if (_autoHandleDialogs)
                    {
                        e.OverrideResult(_defaultDialogResult);
                        record.ResultClicked = _defaultDialogResult;
                        record.ResultName = "Auto-handled";
                    }
                }

                // Store in history
                lock (_dialogLock)
                {
                    _dialogHistory.Add(record);
                    // Keep only last 100 dialogs
                    if (_dialogHistory.Count > 100)
                    {
                        _dialogHistory.RemoveAt(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling dialog");
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                // Unsubscribe from events
                application.DialogBoxShowing -= OnDialogBoxShowing;

                // Shutdown ChangeTracker
                try
                {
                    ChangeTracker.Instance.Shutdown();
                    Log.Information("ChangeTracker shutdown");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error shutting down ChangeTracker");
                }

                if (_mcpServer != null)
                {
                    _mcpServer.Stop();
                    Log.Information("MCP Server stopped");
                }

                Log.Information("MCP Bridge shutdown complete");
                Log.CloseAndFlush();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("MCP Bridge Error", $"Error during shutdown: {ex.Message}");
                return Result.Failed;
            }
        }
        
        internal static MCPServer GetServer()
        {
            return _mcpServer;
        }
        
        internal static void SetServer(MCPServer server)
        {
            _mcpServer = server;
        }
        
        internal static UIApplication GetUIApplication()
        {
            return _uiApplication;
        }

        internal static MCPRequestHandler GetRequestHandler()
        {
            return _requestHandler;
        }

        internal static ExternalEvent GetExternalEvent()
        {
            return _externalEvent;
        }
    }
}
