using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Serilog;

namespace RevitMCPBridge
{
    /// <summary>
    /// Modeless floating "Element Info" palette — the project's live information + control hub.
    /// Click anything in Revit and the panel reflects it (no modal, selection stays).
    /// Layout: project/view context (top) | editors + per-element info (scrolling middle) |
    /// pinned prompt (bottom). EVERY element is editable in place: a Type dropdown for any
    /// family (doors/windows/furniture/equipment/walls), editable Mark/Comments, plus wall
    /// height; and a plain-language prompt. All edits marshal through an ExternalEvent
    /// (WPF handlers can't open a Transaction). No LLM.
    /// </summary>
    public static class ElementInfoPanel
    {
        private static Window _win;
        private static TextBlock _title;
        private static StackPanel _body;
        private static System.Windows.Controls.TextBox _chatBox;
        private static TextBlock _statusText;
        private static StackPanel _convoPanel;            // conversation transcript (persists across selections)
        private static TextBlock _projName, _projSub;     // header project name + subtitle
        private static TextBlock _costText;               // live token/cost meter
        private static Border _infoCard;                  // selected-element info card (hidden until selection)
        private static ScrollViewer _convoScroll;
        private static readonly Newtonsoft.Json.Linq.JArray _history = new Newtonsoft.Json.Linq.JArray();   // recent turns for the model
        private static string _historyDocKey = "";   // history is per-document — cleared when the active doc changes
        private static UIApplication _lastUiApp;          // cached for post-chat refresh
        private static string _pendingImageB64;           // pasted screenshot waiting for the next question
        private static int _currentElementId = -1;
        private static bool _loading;

        // ---- local LLM (Ollama) — OPTIONAL. Present on Weber's machine; absent on a
        // transferred copy, where the panel falls back to rule commands. No cloud, no API. ----
        private const string OllamaUrl = "http://localhost:11434/api/chat";
        private const string OllamaModel = "qwen3:32b";   // warm the same model the agent uses
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        private static string _ctx;   // grounding context for the selected element (built in Update)
        private static string _modelSummary;   // whole-model overview, cached per document
        private static string _summaryDocKey;

        /// <summary>Drop the cached whole-model summary so the next question rebuilds it.
        /// Wired to DocumentChanged — without this the Copilot answers model-wide questions
        /// (counts, longest wall, project name) from a snapshot taken at panel-open.</summary>
        public static void InvalidateModelSummary() { _modelSummary = null; }

        // ---- model-edit marshalling ----
        private enum EditKind { Height, Type, Chat, Param }
        private class EditRequest { public EditKind Kind; public int ElementId; public double HeightFeet; public int TypeId; public string Text; public string ParamName; public string ParamValue; }
        private static EditRequest _pending;
        private static ExternalEvent _editEvent;
        private static string _status;
        private static int _statusElementId = -1;

        private class EditHandler : IExternalEventHandler
        {
            public string GetName() => "ElementInfoPanel.EditHandler";
            public void Execute(UIApplication app)
            {
                try
                {
                    var req = _pending; _pending = null;
                    if (req == null || app.ActiveUIDocument == null) return;
                    var doc = app.ActiveUIDocument.Document;
                    var el = doc.GetElement(new ElementId(req.ElementId));
                    if (el == null) return;
                    var wall = el as Wall;
                    var units = doc.GetUnits();

                    using (var t = new Transaction(doc, "Element Info edit"))
                    {
                        t.Start();
                        bool changed = false;
                        if (req.Kind == EditKind.Height && wall != null)
                        {
                            var p = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                            if (p != null && !p.IsReadOnly) { p.Set(req.HeightFeet); changed = true; _status = "Height set to: " + FmtLen(units, req.HeightFeet); }
                        }
                        else if (req.Kind == EditKind.Type)
                        {
                            var nt = doc.GetElement(new ElementId(req.TypeId));
                            if (nt is FamilySymbol fsym && !fsym.IsActive) fsym.Activate();
                            el.ChangeTypeId(new ElementId(req.TypeId));
                            changed = true;
                            _status = "Changed type to: " + ((nt as ElementType)?.Name ?? "(type)");
                        }
                        else if (req.Kind == EditKind.Param)
                        {
                            var p = el.LookupParameter(req.ParamName);
                            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) { p.Set(req.ParamValue ?? ""); changed = true; _status = req.ParamName + " set to: " + req.ParamValue; }
                        }
                        else if (req.Kind == EditKind.Chat)
                        {
                            if (wall != null) changed = ApplyChat(doc, wall, req.Text, units);
                            else _status = "Plain-language edits are wall-only so far (use the fields above for other elements).";
                        }
                        if (changed) t.Commit(); else t.RollBack();
                    }
                    _statusElementId = req.ElementId;
                    Update(app, doc.GetElement(new ElementId(req.ElementId)));
                }
                catch (Exception ex) { Log.Debug($"ElementInfoPanel.EditHandler: {ex.Message}"); }
            }

            private static bool ApplyChat(Document doc, Wall wall, string text, Units units)
            {
                string lt = (text ?? "").ToLowerInvariant().Trim();
                if (lt.Length == 0) { _status = ""; return false; }

                var stop = new HashSet<string> { "generic", "wall", "the", "and", "over", "with", "for", "make", "it", "change", "this", "that", "to" };
                var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
                WallType best = null; int bestScore = 0;
                foreach (var ty in types)
                {
                    var sig = ty.Name.ToLowerInvariant().Split(new[] { ' ', '-', '/', '"', '\'', '(', ')', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Where(w => w.Length >= 3 && !stop.Contains(w));
                    int score = sig.Count(w => lt.Contains(w));
                    if (score > bestScore) { bestScore = score; best = ty; }
                }
                if (best != null && bestScore >= 1 && best.Id != wall.GetTypeId())
                {
                    wall.ChangeTypeId(best.Id);
                    _status = "Changed type to: " + best.Name;
                    return true;
                }

                string token = ExtractLengthToken(lt);
                double feet;
                if (token != null && UnitFormatUtils.TryParse(units, SpecTypeId.Length, token, out feet) && feet > 0)
                {
                    var p = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (p != null && !p.IsReadOnly)
                    {
                        p.Set(feet);
                        _status = "Height set to: " + FmtLen(units, feet);
                        return true;
                    }
                }
                _status = "Didn't catch that. Try:  8'   |   height 8'-6\"   |   make it <type name>";
                return false;
            }

            private static string ExtractLengthToken(string lt)
            {
                string s = System.Text.RegularExpressions.Regex.Replace(lt, @"\b(feet|foot|ft)\b", "'");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(inches|inch|in)\b", "\"");
                var m = System.Text.RegularExpressions.Regex.Match(s, @"\d[\d\s'""\-\./]*");
                return m.Success ? m.Value.Trim() : null;
            }
        }

        public static void InitExternalEvent()
        {
            try { if (_editEvent == null) _editEvent = ExternalEvent.Create(new EditHandler()); }
            catch (Exception ex) { Log.Debug($"InitExternalEvent: {ex.Message}"); }
            RevitAgent.Init();   // tool-execution ExternalEvent for the LLM agent
        }
        private static void RaiseEdit(EditRequest req) { _pending = req; try { _editEvent?.Raise(); } catch (Exception ex) { Log.Debug($"RaiseEdit: {ex.Message}"); } }

        // ---------------------------------------------------------------- window plumbing
        private static readonly Brush Dim = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xdd, 0xdd, 0xdd));
        private static readonly Brush Ctx = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7a, 0x9c, 0xc4));

        // ---- premium dark design tokens (minimal-&-calm theme) ----
        private static System.Windows.Media.Color Hex(string h) { h = h.TrimStart('#'); return System.Windows.Media.Color.FromRgb(Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16)); }
        private static SolidColorBrush Br(string h) => new SolidColorBrush(Hex(h));
        private static readonly Brush BgMain = Br("0F172A");
        private static readonly Brush BgCard = Br("1E293B");
        private static readonly Brush BgElev = Br("263449");
        private static readonly Brush BorderClr = Br("334155");
        private static readonly Brush AccentPrimary = Br("6366F1");
        private static readonly Brush AccentBlue = Br("3B82F6");
        private static readonly Brush OkClr = Br("22C55E");
        private static readonly Brush WarnClr = Br("F59E0B");
        private static readonly Brush ErrClr = Br("EF4444");
        private static readonly Brush TextPri = Br("F8FAFC");
        private static readonly Brush TextSec = Br("CBD5E1");
        private static readonly Brush TextMut = Br("94A3B8");
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI");

        private static Border Card(UIElement child, Brush bg = null, Thickness? pad = null, double radius = 12, Brush border = null)
            => new Border { Background = bg ?? BgCard, CornerRadius = new CornerRadius(radius), BorderBrush = border ?? BorderClr, BorderThickness = new Thickness(1), Padding = pad ?? new Thickness(14), Margin = new Thickness(0, 0, 0, 10), Child = child };

        private static Border Chip(string text, Brush fg, Brush bg)
            => new Border { Background = bg, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, Foreground = fg, FontSize = 11, FontFamily = UiFont } };

        private static System.Windows.Shapes.Ellipse Dot(Brush c)
            => new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = c, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

        /// <summary>The BIM Ops Studio logo (copilot_logo.png next to the DLL) for the header; null if absent.</summary>
        private static System.Windows.Controls.Image LoadLogo(double size)
        {
            try
            {
                string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "copilot_logo.png");
                if (!System.IO.File.Exists(p)) return null;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(p); bmp.EndInit(); bmp.Freeze();
                return new System.Windows.Controls.Image { Source = bmp, Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Stretch = System.Windows.Media.Stretch.Uniform };
            }
            catch { return null; }
        }

        /// <summary>Flat, modern button via a code-built ControlTemplate (rounded, hover) — no console chrome.</summary>
        private static Button FlatButton(object content, Brush bg, Brush hover, Brush border = null, double radius = 10, Thickness? pad = null)
        {
            var b = new Button { Content = content, Cursor = System.Windows.Input.Cursors.Hand, Foreground = TextPri, FontSize = 14, FontFamily = UiFont };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty, bg);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            bd.SetValue(Border.BorderBrushProperty, border ?? System.Windows.Media.Brushes.Transparent);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(border != null ? 1 : 0));
            bd.SetValue(Border.PaddingProperty, pad ?? new Thickness(14, 10, 14, 10));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            if (hover != null)
            {
                var trig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                trig.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
                tpl.Triggers.Add(trig);
            }
            b.Template = tpl;
            return b;
        }

        /// <summary>A primary-action row: label on the left, chevron on the right (the minimal action list).</summary>
        private static Button RowAction(string label, RoutedEventHandler onClick)
        {
            var dock = new DockPanel { LastChildFill = true };
            var chev = new TextBlock { Text = "›", Foreground = TextMut, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(chev, Dock.Right);
            dock.Children.Add(chev);
            dock.Children.Add(new TextBlock { Text = label, Foreground = TextPri, FontSize = 14, FontFamily = UiFont, VerticalAlignment = VerticalAlignment.Center });
            var b = FlatButton(dock, BgCard, BgElev, BorderClr, 10, new Thickness(14, 11, 14, 11));
            b.Margin = new Thickness(0, 0, 0, 8);
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            b.Click += onClick;
            return b;
        }

        private static void EnsureWindow(UIApplication uiApp)
        {
            if (_win != null) return;
            _win = new Window
            {
                Title = "Model Copilot",
                Width = 460,
                Height = 720,
                WindowStyle = WindowStyle.ToolWindow,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 90,
                Top = 120,
                Background = BgMain,
                FontFamily = UiFont
            };
            _win.Closing += (s, e) => { e.Cancel = true; _win.Hide(); };

            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12), LastChildFill = true };

            // ---------- HEADER ----------
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(header, Dock.Top);
            var titleRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal };
            var logoEl = LoadLogo(24);
            if (logoEl != null) brand.Children.Add(logoEl);
            else brand.Children.Add(new TextBlock { Text = "✦", Foreground = AccentPrimary, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            brand.Children.Add(new TextBlock { Text = "Model Copilot", Foreground = TextPri, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(brand, Dock.Left); titleRow.Children.Add(brand);
            var closeBtn = FlatButton(new TextBlock { Text = "✕", Foreground = TextMut, FontSize = 13 }, System.Windows.Media.Brushes.Transparent, BgElev, null, 8, new Thickness(8, 3, 8, 3));
            closeBtn.ToolTip = "Hide panel"; closeBtn.Click += (s, e) => _win.Hide();
            DockPanel.SetDock(closeBtn, Dock.Right); titleRow.Children.Add(closeBtn);
            header.Children.Add(titleRow);
            _projName = new TextBlock { Foreground = TextPri, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            header.Children.Add(_projName);
            _projSub = new TextBlock { Foreground = TextMut, FontSize = 12, Margin = new Thickness(0, 1, 0, 8), TextWrapping = TextWrapping.Wrap };
            header.Children.Add(_projSub);
            var statusRow = new DockPanel { LastChildFill = false };
            var stat = new StackPanel { Orientation = Orientation.Horizontal };
            stat.Children.Add(Dot(OkClr));
            stat.Children.Add(new TextBlock { Text = "Ready", Foreground = OkClr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(stat, Dock.Left); statusRow.Children.Add(stat);
            var verChip = Chip("Revit 2025", TextSec, BgElev); DockPanel.SetDock(verChip, Dock.Right); statusRow.Children.Add(verChip);
            _costText = new TextBlock { Foreground = TextMut, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(_costText, Dock.Right); statusRow.Children.Add(_costText);
            header.Children.Add(statusRow);
            root.Children.Add(header);
            UpdateCostText();

            // ---------- PROMPT (pinned under header) ----------
            var promptWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(promptWrap, Dock.Top);
            _chatBox = new System.Windows.Controls.TextBox
            {
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = TextPri, CaretBrush = TextPri, FontSize = 14, FontFamily = UiFont,
                VerticalContentAlignment = VerticalAlignment.Top,
                AcceptsReturn = true,            // allow multi-line / large pasted prompts
                AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 24, MaxHeight = 220,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            _chatBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsImage())
                        {
                            var img = System.Windows.Clipboard.GetImage();
                            if (img != null) { _pendingImageB64 = EncodePng(img); _statusText.Text = "📎 image attached — type your question and press Enter"; _statusText.Foreground = AccentBlue; e.Handled = true; }
                        }
                    }
                    catch { }
                }
                // Enter submits; Shift+Enter inserts a new line (so big multi-step prompts can be composed)
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true; SubmitChat();
                }
            };
            var ph = new TextBlock { Text = "Ask Model Copilot…  (Shift+Enter for a new line)", Foreground = TextMut, FontSize = 14, FontFamily = UiFont, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 0, 0, 0) };
            _chatBox.TextChanged += (s, e) => ph.Visibility = string.IsNullOrEmpty(_chatBox.Text) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            var inputGrid = new System.Windows.Controls.Grid();
            inputGrid.Children.Add(_chatBox); inputGrid.Children.Add(ph);
            var promptDock = new DockPanel { LastChildFill = true };
            var sendBtn = FlatButton(new TextBlock { Text = "➤", Foreground = System.Windows.Media.Brushes.White, FontSize = 13 }, AccentBlue, Br("2563EB"), null, 8, new Thickness(10, 5, 10, 5));
            sendBtn.ToolTip = "Send"; sendBtn.Click += (s, e) => SubmitChat();
            DockPanel.SetDock(sendBtn, Dock.Right); promptDock.Children.Add(sendBtn);
            var spark = new TextBlock { Text = "✦", Foreground = AccentPrimary, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            DockPanel.SetDock(spark, Dock.Left); promptDock.Children.Add(spark);
            promptDock.Children.Add(inputGrid);
            promptWrap.Children.Add(Card(promptDock, BgCard, new Thickness(10, 7, 8, 7), 12));
            _statusText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = TextMut, Margin = new Thickness(4, 4, 0, 0), Text = "Type a question, or use the actions below" };
            promptWrap.Children.Add(_statusText);
            root.Children.Add(promptWrap);

            // ---------- FOOTER (pinned bottom) ----------
            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            var newChat = FlatButton(new TextBlock { Text = "＋ New chat", Foreground = TextSec, FontSize = 12 }, BgCard, BgElev, BorderClr, 8, new Thickness(12, 6, 12, 6));
            newChat.ToolTip = "Clear the conversation"; newChat.Click += (s, e) => ClearConversation();
            DockPanel.SetDock(newChat, Dock.Left); footer.Children.Add(newChat);
            var stdBtn = FlatButton(new TextBlock { Text = "★ Standards", Foreground = TextSec, FontSize = 12 }, BgCard, BgElev, BorderClr, 8, new Thickness(12, 6, 12, 6));
            stdBtn.Margin = new Thickness(8, 0, 0, 0); stdBtn.ToolTip = "View / add the standards the Copilot follows";
            stdBtn.Click += (s, e) => ShowStandards();
            DockPanel.SetDock(stdBtn, Dock.Left); footer.Children.Add(stdBtn);
            var setBtn = FlatButton(new TextBlock { Text = "⚙ Settings", Foreground = TextMut, FontSize = 12 }, System.Windows.Media.Brushes.Transparent, BgElev, null, 8, new Thickness(10, 6, 10, 6));
            setBtn.ToolTip = "Settings"; setBtn.Click += (s, e) => ShowSettings();
            DockPanel.SetDock(setBtn, Dock.Right); footer.Children.Add(setBtn);
            root.Children.Add(footer);

            // ---------- MIDDLE (scrolls): primary actions + element info + conversation ----------
            var midScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var mid = new StackPanel();
            mid.Children.Add(RowAction("Coordinate Model", (s, e) => { if (!_loading) AskModelAsync("coordinate the model and tell me what is missing"); }));
            mid.Children.Add(RowAction("Run Code Check", (s, e) => { if (!_loading) AskModelAsync("run a building code check and list any violations"); }));
            mid.Children.Add(RowAction("Find Duplicates", (s, e) => { if (!_loading) AskModelAsync("find duplicate or overlapping walls"); }));
            mid.Children.Add(RowAction("Export Report", (s, e) => { if (!_loading) AskModelAsync("write a coordination report as a PDF"); }));
            mid.Children.Add(RowAction("Look at View", (s, e) => { if (!_loading) AskModelAsync("look at the current view and tell me what you see — anything notable for the construction documents?"); }));

            _title = new TextBlock { Foreground = TextSec, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            _body = new StackPanel();
            var infoInner = new StackPanel(); infoInner.Children.Add(_title); infoInner.Children.Add(_body);
            _infoCard = Card(infoInner, BgCard, new Thickness(14), 12);
            _infoCard.Visibility = System.Windows.Visibility.Collapsed;
            mid.Children.Add(_infoCard);

            _convoPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            mid.Children.Add(_convoPanel);
            midScroll.Content = mid;
            _convoScroll = midScroll;
            root.Children.Add(midScroll);

            _win.Content = root;
            try { new WindowInteropHelper(_win) { Owner = uiApp.MainWindowHandle }; } catch { }
            try { var d0 = uiApp.ActiveUIDocument?.Document; if (d0 != null) SetHeaderProject(d0); } catch { }
            WarmModel();   // load the local model into VRAM in the background, if present
        }

        private static void SubmitChat()
        {
            if (_loading || _chatBox == null || string.IsNullOrWhiteSpace(_chatBox.Text)) return;
            string q = _chatBox.Text.Trim();
            if (LooksLikeEdit(q)) RaiseEdit(new EditRequest { Kind = EditKind.Chat, ElementId = _currentElementId, Text = q });
            else AskModelAsync(q);
        }

        private static void ClearConversation()
        {
            try { _convoPanel.Children.Clear(); _history.Clear(); _statusText.Text = "New chat — ask me anything"; _statusText.Foreground = TextMut; } catch { }
        }

        private static void SetHeaderProject(Document doc)
        {
            try
            {
                if (_projName == null || doc == null) return;
                string proj = PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NAME);
                if (string.IsNullOrWhiteSpace(proj)) proj = doc.Title;
                string sub = PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_BUILDING_NAME);
                if (string.IsNullOrWhiteSpace(sub)) sub = PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NUMBER);
                if (proj != null && proj.Contains(" - "))
                {
                    var parts = proj.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    _projName.Text = parts[0].Trim();
                    if (string.IsNullOrWhiteSpace(sub)) sub = parts[1].Trim();
                }
                else _projName.Text = proj;
                _projSub.Text = sub ?? "";
            }
            catch { }
        }

        private static TextBlock SettingLabel(string t) => new TextBlock { Text = t, Foreground = TextSec, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        private static TextBlock SettingHint(string t) => new TextBlock { Text = t, Foreground = TextMut, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12) };
        private static System.Windows.Controls.TextBox SettingInput(string text) => new System.Windows.Controls.TextBox
        {
            Text = text ?? "", Background = BgElev, Foreground = TextPri, CaretBrush = TextPri,
            BorderBrush = BorderClr, BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13, FontFamily = UiFont, Margin = new Thickness(0, 0, 0, 2)
        };

        private static void UpdateCostText()
        {
            try
            {
                if (_costText == null) return;
                if (RevitAgent.IsLocal) { _costText.Text = "Local · free"; _costText.Foreground = OkClr; return; }
                double spent = RevitAgent.SpentTodayUsd, cap = RevitAgent.CfgDailyCap;
                _costText.Text = "Today $" + spent.ToString("0.00") + " / $" + cap.ToString("0.00");
                _costText.Foreground = spent >= cap ? ErrClr : (spent >= cap * 0.8 ? WarnClr : TextMut);
            }
            catch { }
        }

        private static void ShowStandards()
        {
            try
            {
                var win = new Window { Title = "Model Copilot — Standards", Width = 470, Height = 580, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.Manual, Background = BgMain, FontFamily = UiFont, Topmost = true };
                try { if (_win != null) win.Owner = _win; } catch { }
                try { win.Left = (_win?.Left ?? 90) + (_win?.Width ?? 460) + 16; win.Top = (_win?.Top ?? 120); } catch { }   // open just to the RIGHT of the panel, tops aligned
                var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true };

                var hdr = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                DockPanel.SetDock(hdr, Dock.Top);
                hdr.Children.Add(new TextBlock { Text = "Standards & preferences", Foreground = TextPri, FontSize = 18, FontWeight = FontWeights.SemiBold });
                hdr.Children.Add(new TextBlock { Text = "Rules the Copilot follows in every session — your firm's way of working. It also adds to these when you say “remember…” in chat.", Foreground = TextMut, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                root.Children.Add(hdr);

                var addRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 12, 0, 0) };
                DockPanel.SetDock(addRow, Dock.Bottom);
                var addBox = SettingInput(""); addBox.Margin = new Thickness(0, 0, 8, 0);
                var addBtn = FlatButton(new TextBlock { Text = "Add", Foreground = System.Windows.Media.Brushes.White, FontSize = 13 }, AccentBlue, Br("2563EB"), null, 8, new Thickness(18, 6, 18, 6));
                DockPanel.SetDock(addBtn, Dock.Right); addRow.Children.Add(addBtn); addRow.Children.Add(addBox);
                root.Children.Add(addRow);

                var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var list = new StackPanel();
                listScroll.Content = list;
                root.Children.Add(listScroll);

                System.Action refresh = null;
                refresh = () =>
                {
                    list.Children.Clear();
                    var items = RevitAgent.GetStandards();
                    if (items.Count == 0) list.Children.Add(new TextBlock { Text = "No standards yet — add one below, or tell the Copilot “remember…”.", Foreground = TextMut, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
                    foreach (var s in items)
                    {
                        var rowDock = new DockPanel { LastChildFill = true };
                        var rm = FlatButton(new TextBlock { Text = "✕", Foreground = TextMut, FontSize = 12 }, System.Windows.Media.Brushes.Transparent, ErrClr, null, 6, new Thickness(7, 3, 7, 3));
                        rm.ToolTip = "Remove this standard"; string cap = s; rm.Click += (a, b) => { RevitAgent.ForgetStandard(cap); refresh(); };
                        DockPanel.SetDock(rm, Dock.Right); rowDock.Children.Add(rm);
                        rowDock.Children.Add(new TextBlock { Text = s, Foreground = TextSec, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                        list.Children.Add(Card(rowDock, BgCard, new Thickness(12, 8, 8, 8), 10));
                    }
                };
                addBtn.Click += (a, b) => { if (!string.IsNullOrWhiteSpace(addBox.Text)) { RevitAgent.Remember(addBox.Text.Trim()); addBox.Text = ""; refresh(); } };
                addBox.KeyDown += (a, b) => { if (b.Key == Key.Enter && !string.IsNullOrWhiteSpace(addBox.Text)) { RevitAgent.Remember(addBox.Text.Trim()); addBox.Text = ""; refresh(); } };
                refresh();

                win.Content = root;
                win.ShowDialog();
            }
            catch (Exception ex) { Log.Debug($"ShowStandards: {ex.Message}"); }
        }

        private static void ShowSettings()
        {
            try
            {
                var win = new Window
                {
                    Title = "Model Copilot — Settings", Width = 470, Height = 600,
                    WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Background = BgMain, FontFamily = UiFont, Topmost = true
                };
                try { if (_win != null) win.Owner = _win; } catch { }
                try { win.Left = (_win?.Left ?? 90) + (_win?.Width ?? 460) + 16; win.Top = (_win?.Top ?? 120); } catch { }   // open just to the RIGHT of the panel, tops aligned
                var root = new StackPanel { Margin = new Thickness(18) };

                root.Children.Add(new TextBlock { Text = "Settings", Foreground = TextPri, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                root.Children.Add(new TextBlock { Text = "By default the Copilot runs on your LOCAL model (Ollama) — private and free. You can instead point it at any OpenAI-compatible API (OpenAI, OpenRouter, Groq, LM Studio, vLLM…).", Foreground = TextMut, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });

                root.Children.Add(SettingLabel("AI provider"));
                var rbLocal = new System.Windows.Controls.RadioButton { Content = "Local model (Ollama) — default", IsChecked = RevitAgent.CfgApiType != "openai", Foreground = TextSec, FontSize = 13, GroupName = "prov", Margin = new Thickness(0, 0, 0, 4) };
                var rbApi = new System.Windows.Controls.RadioButton { Content = "Custom API (OpenAI-compatible)", IsChecked = RevitAgent.CfgApiType == "openai", Foreground = TextSec, FontSize = 13, GroupName = "prov", Margin = new Thickness(0, 0, 0, 14) };
                root.Children.Add(rbLocal); root.Children.Add(rbApi);

                root.Children.Add(SettingLabel("Endpoint URL"));
                var url = SettingInput(RevitAgent.CfgUrl); root.Children.Add(url);
                root.Children.Add(SettingHint("Local: http://localhost:11434/api/chat    ·    Hosted: https://api.openai.com/v1/chat/completions"));

                root.Children.Add(SettingLabel("Model name"));
                var model = SettingInput(RevitAgent.CfgModel); root.Children.Add(model);
                root.Children.Add(SettingHint("e.g. qwen3:32b (local)  ·  gpt-4o / openai/gpt-4o (hosted)"));

                root.Children.Add(SettingLabel("Vision model (for image / screenshot questions)"));
                var vision = SettingInput(RevitAgent.CfgVisionModel); root.Children.Add(vision);
                root.Children.Add(SettingHint("e.g. qwen2.5vl:7b (local)  ·  gpt-4o (hosted). Used when you paste an image or click “Look at View”."));

                var keyLabel = SettingLabel("API key");
                var keyBox = SettingInput(RevitAgent.CfgApiKey);
                var keyHint = SettingHint("Only needed for a hosted API. Leave blank for a local model. Stored in model_copilot.json on this machine.");
                root.Children.Add(keyLabel); root.Children.Add(keyBox); root.Children.Add(keyHint);

                // ---- cost guardrail (API only; local is always free) ----
                var costHdr = new TextBlock { Text = "Cost guardrail (API only — local is always free)", Foreground = TextPri, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 8) };
                root.Children.Add(costHdr);
                var capLabel = SettingLabel("Daily spending cap (USD)");
                var cap = SettingInput(RevitAgent.CfgDailyCap.ToString("0.##"));
                var capHint = SettingHint("Hard stop — when today's API spend hits this, the assistant pauses until tomorrow. Spent today: $" + RevitAgent.SpentTodayUsd.ToString("0.00") + "  (" + RevitAgent.TokensTodayCount + " tokens)");
                root.Children.Add(capLabel); root.Children.Add(cap); root.Children.Add(capHint);
                var inLabel = SettingLabel("Input price ($ per 1M tokens)");
                var inRate = SettingInput(RevitAgent.CfgInRate.ToString("0.##"));
                var inHint = SettingHint("Match your model — Opus ~15 · Sonnet ~3 · Haiku ~0.8 · local = free");
                root.Children.Add(inLabel); root.Children.Add(inRate); root.Children.Add(inHint);
                var outLabel = SettingLabel("Output price ($ per 1M tokens)");
                var outRate = SettingInput(RevitAgent.CfgOutRate.ToString("0.##"));
                var outHint = SettingHint("Opus ~75 · Sonnet ~15 · Haiku ~4");
                root.Children.Add(outLabel); root.Children.Add(outRate); root.Children.Add(outHint);

                var enabled = new System.Windows.Controls.CheckBox { Content = "Assistant enabled", IsChecked = RevitAgent.CfgEnabled, Foreground = TextSec, FontSize = 13, Margin = new Thickness(0, 4, 0, 18) };
                root.Children.Add(enabled);

                System.Action upd = () =>
                {
                    var v = rbApi.IsChecked == true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    keyLabel.Visibility = keyBox.Visibility = keyHint.Visibility = v;
                };
                rbApi.Checked += (s, e) => upd(); rbLocal.Checked += (s, e) => upd(); upd();

                var btnRow = new DockPanel { LastChildFill = false };
                var save = FlatButton(new TextBlock { Text = "Save", Foreground = System.Windows.Media.Brushes.White, FontSize = 13 }, AccentBlue, Br("2563EB"), null, 8, new Thickness(20, 8, 20, 8));
                save.Click += (s, e) =>
                {
                    double capV, inV, outV;
                    if (!double.TryParse(cap.Text, out capV) || capV <= 0) capV = 5.0;
                    if (!double.TryParse(inRate.Text, out inV)) inV = RevitAgent.CfgInRate;
                    if (!double.TryParse(outRate.Text, out outV)) outV = RevitAgent.CfgOutRate;
                    RevitAgent.SaveConfig(rbApi.IsChecked == true ? "openai" : "ollama", url.Text, model.Text, vision.Text, keyBox.Text, enabled.IsChecked == true, capV, inV, outV);
                    try { if (_statusText != null) { _statusText.Text = "Settings saved — " + (rbApi.IsChecked == true ? "using custom API" : "using local model (free)"); _statusText.Foreground = OkClr; } } catch { }
                    try { UpdateCostText(); } catch { }
                    win.Close();
                };
                DockPanel.SetDock(save, Dock.Right); btnRow.Children.Add(save);
                var cancel = FlatButton(new TextBlock { Text = "Cancel", Foreground = TextSec, FontSize = 13 }, BgCard, BgElev, BorderClr, 8, new Thickness(16, 8, 16, 8));
                cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += (s, e) => win.Close();
                DockPanel.SetDock(cancel, Dock.Right); btnRow.Children.Add(cancel);
                var openFile = FlatButton(new TextBlock { Text = "Edit file…", Foreground = TextMut, FontSize = 11 }, System.Windows.Media.Brushes.Transparent, BgElev, null, 6, new Thickness(8, 6, 8, 6));
                openFile.ToolTip = RevitAgent.ConfigPath;
                openFile.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = RevitAgent.ConfigPath, UseShellExecute = true }); } catch { } };
                DockPanel.SetDock(openFile, Dock.Left); btnRow.Children.Add(openFile);
                root.Children.Add(btnRow);

                win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                win.ShowDialog();
            }
            catch (Exception ex) { Log.Debug($"ShowSettings: {ex.Message}"); }
        }

        private static bool _warmed;
        /// <summary>Fire-and-forget warm-up so the first real question is fast. No-op if Ollama is absent.</summary>
        private static async void WarmModel()
        {
            if (_warmed) return; _warmed = true;
            try
            {
                if (RevitAgent.CfgApiType != "ollama") return;   // don't ping a hosted API just to warm it
                var payload = new { model = RevitAgent.CfgModel, stream = false, think = false, keep_alive = "30m", messages = new object[] { new { role = "user", content = "ok" } } };
                using (var c = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
                    await _http.PostAsync(RevitAgent.CfgUrl, c);
            }
            catch (Exception ex) { Log.Debug($"WarmModel: {ex.Message}"); }
        }

        /// <summary>Open the Copilot from the ribbon with nothing selected — whole-model chat.</summary>
        public static void OpenStandalone(UIApplication uiApp)
        {
            try
            {
                EnsureWindow(uiApp);
                _lastUiApp = uiApp;
                _loading = true;
                _currentElementId = -1;
                if (_infoCard != null) _infoCard.Visibility = System.Windows.Visibility.Collapsed;   // nothing selected

                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc != null)
                {
                    SetHeaderProject(doc);
                    EnsureModelSummary(doc);
                    _ctx = "You are a practical BIM assistant inside a live Revit model. No single element is selected right now — you can still answer about the whole model and run model-wide actions (coordinate, code check, find duplicates, purge, export schedules/PDF, list materials). The user may also click an element to focus it.\n\n"
                         + "PROJECT: " + (PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NAME) ?? doc.Title) + "\n"
                         + "ACTIVE VIEW: " + (doc.ActiveView?.Name ?? "?") + "\n\n"
                         + (_modelSummary ?? "");
                }
                else { _ctx = null; }

                if (_statusText != null) { _statusText.Text = "Type a question, or use the actions below"; _statusText.Foreground = TextMut; }
                _loading = false;
                _win.Show();
                try { _win.Activate(); _chatBox.Focus(); } catch { }
            }
            catch (Exception ex) { Log.Debug($"OpenStandalone: {ex.Message}"); }
        }

        public static void Update(UIApplication uiApp, Element el)
        {
            try
            {
                if (el == null) return;
                EnsureWindow(uiApp);
                _lastUiApp = uiApp;
                _loading = true;
                _currentElementId = (int)el.Id.Value;
                if (_statusElementId != _currentElementId) _status = "";
                _statusText.Text = _status ?? "";
                _statusText.Foreground = (!string.IsNullOrEmpty(_status) && (_status.StartsWith("Didn't") || _status.Contains("wall-only"))) ? Brushes.Orange : Brushes.LightGreen;
                _body.Children.Clear();

                var doc = uiApp.ActiveUIDocument.Document;
                if (_infoCard != null) _infoCard.Visibility = System.Windows.Visibility.Visible;
                SetHeaderProject(doc);
                EnsureModelSummary(doc);
                try { _ctx = BuildContextString(doc, el); } catch { _ctx = null; }
                AddContext(doc);
                AddEditors(doc, el);

                if (el is Wall wall) BuildWall(doc, wall);
                else if (el is TextNote tnote) { _title.Text = "Text Note"; _body.Children.Add(Line(string.IsNullOrWhiteSpace(tnote.Text) ? "(empty)" : tnote.Text.Trim())); }
                else if (el is Autodesk.Revit.DB.Grid grd2) { _title.Text = "Grid " + grd2.Name; }
                else if (el is Level lvl2) { _title.Text = "Level " + lvl2.Name; _body.Children.Add(Line("Elevation: " + FmtLen(doc.GetUnits(), lvl2.Elevation))); }
                else if (el is Dimension dim2) { _title.Text = "Dimension"; try { _body.Children.Add(Line("Value: " + (dim2.ValueString ?? "?") + "\nSegments: " + dim2.NumberOfSegments)); } catch { } }
                else if (el is IndependentTag tag2) { _title.Text = "Tag"; try { _body.Children.Add(Line("Tags: \"" + tag2.TagText + "\"")); } catch { } }
                else if (el is Autodesk.Revit.DB.Architecture.Room room) BuildRoom(doc, room);
                else if (IsCat(el, BuiltInCategory.OST_Doors)) BuildDoor(doc, el as FamilyInstance);
                else if (IsCat(el, BuiltInCategory.OST_Windows)) BuildWindow(doc, el as FamilyInstance);
                else if (IsEquip(el)) BuildEquipment(doc, el as FamilyInstance);
                else BuildGeneric(doc, el);

                _loading = false;
                if (!_win.IsVisible) _win.Show();
            }
            catch (Exception ex) { _loading = false; Log.Debug($"ElementInfoPanel.Update: {ex.Message}"); }
        }

        public static void Toggle() { if (_win != null) { if (_win.IsVisible) _win.Hide(); else _win.Show(); } }
        public static void ShowPanel() { if (_win != null && !_win.IsVisible) _win.Show(); }
        public static void HidePanel() { if (_win != null) _win.Hide(); }

        // ---------------------------------------------------------------- editors (any element)
        private class TypeOption { public ElementId Id; public string Name; public override string ToString() => Name; }

        private static List<TypeOption> GetTypeOptions(Document doc, Element el)
        {
            var list = new List<TypeOption>();
            try
            {
                if (el is Wall)
                    foreach (var wt in new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>())
                        list.Add(new TypeOption { Id = wt.Id, Name = wt.Name });
                else if (el is FamilyInstance fi && fi.Symbol != null)
                    foreach (var id in fi.Symbol.Family.GetFamilySymbolIds())
                    {
                        var s = doc.GetElement(id) as FamilySymbol;
                        if (s != null) list.Add(new TypeOption { Id = id, Name = s.Name });
                    }
            }
            catch { }
            return list.OrderBy(o => o.Name).ToList();
        }

        /// <summary>In-place editors shown for any element: Type dropdown (same family),
        /// wall height, and writable Mark / Comments. Everything commits through the ExternalEvent.</summary>
        private static void AddEditors(Document doc, Element el)
        {
            var u = doc.GetUnits();
            var opts = GetTypeOptions(doc, el);
            if (opts.Count > 0)
            {
                _body.Children.Add(new TextBlock { Text = "Type", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 1) });
                var combo = new System.Windows.Controls.ComboBox { ItemsSource = opts, Margin = new Thickness(0, 0, 0, 6) };
                var cur = el.GetTypeId();
                combo.SelectedItem = opts.FirstOrDefault(o => o.Id == cur);
                combo.SelectionChanged += (s, e) => { if (!_loading && combo.SelectedItem is TypeOption to) RaiseEdit(new EditRequest { Kind = EditKind.Type, ElementId = _currentElementId, TypeId = (int)to.Id.Value }); };
                _body.Children.Add(combo);
            }

            if (el is Wall wall)
            {
                double ht = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
                _body.Children.Add(new TextBlock { Text = "Height  (e.g. 8' or 8'-6\", Enter)", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 1) });
                var htBox = new System.Windows.Controls.TextBox { Text = FmtLen(u, ht), Margin = new Thickness(0, 0, 0, 6) };
                htBox.KeyDown += (s, e) =>
                {
                    if (e.Key != Key.Enter || _loading) return;
                    double v;
                    if (UnitFormatUtils.TryParse(u, SpecTypeId.Length, htBox.Text, out v) && v > 0)
                        RaiseEdit(new EditRequest { Kind = EditKind.Height, ElementId = _currentElementId, HeightFeet = v });
                };
                _body.Children.Add(htBox);
            }

            AddEditText(el, "Mark");
            AddEditText(el, "Comments");
        }

        private static void AddEditText(Element el, string paramName)
        {
            var p = el.LookupParameter(paramName);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return;
            _body.Children.Add(new TextBlock { Text = paramName + "  (Enter)", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 1) });
            var box = new System.Windows.Controls.TextBox { Text = p.AsString() ?? "", Margin = new Thickness(0, 0, 0, 6) };
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter && !_loading) RaiseEdit(new EditRequest { Kind = EditKind.Param, ElementId = _currentElementId, ParamName = paramName, ParamValue = box.Text }); };
            _body.Children.Add(box);
        }

        // ---------------------------------------------------------------- helpers
        private static bool IsCat(Element el, BuiltInCategory bic) => el is FamilyInstance && el.Category != null && el.Category.Id.Value == (int)bic;

        private static readonly BuiltInCategory[] EquipCats =
        {
            BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems
        };
        private static bool IsEquip(Element el)
        {
            if (!(el is FamilyInstance) || el.Category == null) return false;
            long id = el.Category.Id.Value;
            return EquipCats.Any(c => (long)c == id);
        }
        private static readonly string[] UtilityParams =
        { "Voltage", "Wattage", "Apparent Load", "Number of Poles", "MCA", "MOCP", "Amps", "Load Classification",
          "Horsepower", "Air Flow", "CFM", "Flow", "Hot Water", "Cold Water", "Sanitary", "Waste", "Drain", "Power" };
        private static TextBlock Line(string s) => new TextBlock { Text = s, Foreground = Dim, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        private static string PStr(Element el, BuiltInParameter bip)
        {
            if (el == null) return null;
            var p = el.get_Parameter(bip);
            if (p == null) return null;
            string v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        private static string PName(Element el, string n)
        {
            if (el == null) return null;
            var p = el.LookupParameter(n);
            if (p == null) return null;
            string v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        private static string FmtLen(Units u, double feet) { try { return UnitFormatUtils.Format(u, SpecTypeId.Length, feet, false); } catch { return $"{feet:0.##} ft"; } }

        private static string InferRating(string explicitRating, string typeName)
        {
            if (!string.IsNullOrWhiteSpace(explicitRating)) return explicitRating;
            if (string.IsNullOrEmpty(typeName)) return "-";
            var m = System.Text.RegularExpressions.Regex.Match(typeName, @"(\d+)\s*HR", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + " HR (from type)";
            if (typeName.IndexOf("RATED", StringComparison.OrdinalIgnoreCase) >= 0) return "rated (from type)";
            return "-";
        }

        // ---------------------------------------------------------------- local model (Ollama)
        /// <summary>True for clear edit commands (handled instantly by rules); false routes to the model.</summary>
        private static bool LooksLikeEdit(string q)
        {
            // ONLY a bare measurement (e.g. "8", "8'", "8'-6\"", "9 feet") takes the instant
            // rule path (wall height). Everything else — "change/make/set ...", and every
            // question — goes to the tool-enabled agent, which can act on ANY element.
            string t = q.ToLowerInvariant().Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d[\d'""\.\-\s]*$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+(\.\d+)?\s*(ft|feet|foot|in|inch|inches)$")) return true;
            return false;   // default: a question or action -> the tool-enabled agent
        }

        /// <summary>Ask the local Ollama model about the selected element, grounded in _ctx.
        /// Read-only (text only) so it runs on a background thread — Revit is never blocked.
        /// If Ollama isn't running (e.g. a transferred copy), shows a graceful fallback note.</summary>
        /// <summary>Add a line to the conversation transcript; returns the message TextBlock so it can be updated.</summary>
        private static TextBlock AppendConvo(string who, string text, bool isUser)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = who, Foreground = isUser ? Ctx : Brushes.LightGreen, FontWeight = FontWeights.Bold, FontSize = 10, Margin = new Thickness(0, 0, 0, 2) });
            var msg = new TextBlock { Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            RenderMarkdown(msg, text);
            stack.Children.Add(msg);
            var bubble = new Border
            {
                Background = new SolidColorBrush(isUser ? System.Windows.Media.Color.FromRgb(0x2c, 0x3e, 0x57) : System.Windows.Media.Color.FromRgb(0x2b, 0x2b, 0x2b)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6, 9, 7),
                Margin = new Thickness(isUser ? 34 : 0, 6, isUser ? 0 : 34, 0),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = stack
            };
            _convoPanel.Children.Add(bubble);
            _convoScroll?.ScrollToEnd();
            return msg;
        }

        /// <summary>Render light markdown (**bold**, # headers, - bullets, line breaks) into a TextBlock's inlines.</summary>
        private static void RenderMarkdown(TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            bool first = true;
            foreach (var raw in lines)
            {
                if (!first) tb.Inlines.Add(new LineBreak());
                first = false;
                string line = raw;
                bool header = line.StartsWith("#");
                if (header) line = line.TrimStart('#', ' ');
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) line = "   • " + trimmed.Substring(2);
                foreach (var inl in ParseBold(line, header)) tb.Inlines.Add(inl);
            }
        }

        private static IEnumerable<Inline> ParseBold(string line, bool allBold)
        {
            var parts = line.Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                var run = new Run(parts[i]);
                if (allBold || i % 2 == 1) yield return new Bold(run);
                else yield return run;
            }
        }

        // last-turn capture + busy flag — lets the bridge (copilotAsk/copilotStatus) drive the panel
        // deterministically from scripts instead of brittle UI automation
        private static string _lastQuestion = "", _lastAnswer = null;
        private static bool _chatBusy;

        /// <summary>Submit a question exactly as if typed into the chat box (bridge: copilotAsk).</summary>
        public static string AskFromBridge(string question)
        {
            try
            {
                if (_win == null || _chatBox == null) return "{\"success\":false,\"error\":\"the Model Copilot panel is not open - open it from the MCP Bridge ribbon first\"}";
                if (_chatBusy) return "{\"success\":false,\"error\":\"the Copilot is still working on the previous request - poll copilotStatus\"}";
                _win.Dispatcher.Invoke(() => { _chatBox.Text = question; SubmitChat(); });
                return "{\"success\":true,\"message\":\"submitted - poll copilotStatus for the reply\"}";
            }
            catch (Exception ex) { return "{\"success\":false,\"error\":\"" + (ex.Message ?? "").Replace("\"", "'") + "\"}"; }
        }

        /// <summary>Panel state for scripts (bridge: copilotStatus) — busy + last question/answer.</summary>
        public static string StatusFromBridge()
        {
            var jo = new Newtonsoft.Json.Linq.JObject
            {
                ["success"] = true,
                ["panelOpen"] = _win != null,
                ["busy"] = _chatBusy,
                ["lastQuestion"] = _lastQuestion ?? "",
                ["lastAnswer"] = _lastAnswer
            };
            return jo.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static async void AskModelAsync(string question)
        {
            _lastQuestion = question; _lastAnswer = null; _chatBusy = true;
            try
            {
                // Code-enforced safety: if a destructive action is awaiting confirmation,
                // this message either confirms it (runs it) or cancels it.
                if (RevitAgent.HasPendingDestructive)
                {
                    string ql = question.Trim().ToLowerInvariant();
                    bool confirm = ql == "confirm" || ql == "yes" || ql.StartsWith("confirm") || ql.StartsWith("yes ") || ql.Contains("go ahead") || ql.Contains("do it") || ql.Contains("proceed");
                    if (confirm)
                    {
                        AppendConvo("You", question, true);
                        _chatBox.Text = "";
                        var creply = AppendConvo("Copilot", "working…", false);
                        string res = await Task.Run(() => RevitAgent.ConfirmPendingDestructive());
                        bool ok = res != null && (res.Contains("\"success\":true") || res.Contains("\"success\": true"));
                        RenderMarkdown(creply, ok ? "✓ Done — the action completed." : "The action did not complete.");
                        _convoScroll?.ScrollToEnd();
                        _lastAnswer = ok ? "✓ Done — the action completed." : "The action did not complete."; _chatBusy = false;
                        return;
                    }
                    RevitAgent.CancelPendingDestructive();   // anything else cancels the pending destructive action
                }

                // a pasted screenshot, or a "look at the view" request, routes to the VISION model
                string imgB64 = _pendingImageB64; _pendingImageB64 = null;
                bool wantsView = imgB64 == null && LooksLikeVisionRequest(question);

                AppendConvo("You", question + (imgB64 != null ? "   [📎 image]" : ""), true);
                _chatBox.Text = "";                    // clear the input for the next one
                _statusText.Text = "";
                var reply = AppendConvo("Copilot", (imgB64 != null || wantsView) ? "looking…" : "thinking…", false);

                // The panel no longer auto-updates on element click — so read the CURRENT selection
                // LIVE right now and ground the Copilot on it (single element → its details; otherwise
                // the whole-model overview). This keeps it selection-aware without popping up on clicks.
                try
                {
                    var uidocNow = _lastUiApp?.ActiveUIDocument;
                    if (uidocNow != null)
                    {
                        // a DIFFERENT document makes old turns toxic (the local model copies look-alike
                        // answers, fabricating element ids that belonged to the previous file) — start clean
                        string docKey = uidocNow.Document?.Title ?? "";
                        if (_historyDocKey != docKey) { _history.Clear(); _historyDocKey = docKey; }
                        EnsureModelSummary(uidocNow.Document);   // no-op unless DocumentChanged invalidated it
                        var selNow = uidocNow.Selection.GetElementIds();
                        if (selNow.Count == 1)
                        {
                            var selEl = uidocNow.Document.GetElement(selNow.First());
                            if (selEl != null) { _currentElementId = (int)selEl.Id.Value; _ctx = BuildContextString(uidocNow.Document, selEl); }
                        }
                        else if (selNow.Count > 1) { _currentElementId = -1; _ctx = BuildMultiSelectContextString(uidocNow.Document, selNow); }
                        else { _currentElementId = -1; _ctx = BuildProjectContextString(uidocNow.Document, selNow.Count); }
                    }
                }
                catch { }
                string ctx = _ctx ?? "You are a BIM assistant. No element context is available.";
                string answer;

                if (imgB64 != null)
                {
                    answer = await Task.Run(() => RevitAgent.ChatVisionAsync(question, imgB64, ctx));   // pasted screenshot
                }
                else if (wantsView)
                {
                    int viewId = -1;
                    try { viewId = (int)(_lastUiApp?.ActiveUIDocument?.Document?.ActiveView?.Id.Value ?? -1); } catch { }
                    string viewImg = await Task.Run(() => RevitAgent.CaptureViewB64(viewId));
                    answer = viewImg != null
                        ? await Task.Run(() => RevitAgent.ChatVisionAsync(question, viewImg, ctx))
                        : "I couldn't capture the current view. Open a graphical view (plan/elevation/section/3D) and try again.";
                }
                else
                {
                    // Background thread so the agent's in-loop tool calls (which marshal to Revit's
                    // API thread and block) never deadlock the UI thread.
                    answer = await Task.Run(() => RevitAgent.ChatPlannedAsync(question, ctx, _history,
                        s => { try { _win.Dispatcher.Invoke(() => { reply.Text = s; _convoScroll?.ScrollToEnd(); }); } catch { } }));
                    // local models occasionally emit an empty turn — one clean retry beats
                    // showing the user "(the model returned nothing)"
                    if (string.IsNullOrWhiteSpace(answer))
                        answer = await Task.Run(() => RevitAgent.ChatPlannedAsync(question, ctx, _history,
                            s => { try { _win.Dispatcher.Invoke(() => { reply.Text = s; _convoScroll?.ScrollToEnd(); }); } catch { } }));
                }

                if (answer == null) answer = "No local model running — start Ollama to chat. Quick commands still work:  8'  ·  make it CMU";
                else if (string.IsNullOrWhiteSpace(answer)) answer = "(the model returned nothing)";

                RenderMarkdown(reply, answer);
                _convoScroll?.ScrollToEnd();
                _lastAnswer = answer; _chatBusy = false;

                // keep recent turns so follow-ups work ("give me a PDF of that")
                _history.Add(new Newtonsoft.Json.Linq.JObject { ["role"] = "user", ["content"] = question });
                _history.Add(new Newtonsoft.Json.Linq.JObject { ["role"] = "assistant", ["content"] = answer });
                while (_history.Count > 8) _history.RemoveAt(0);

                // refresh the element panel so any edits the agent just made show immediately (no reselect)
                try { var d = _lastUiApp?.ActiveUIDocument?.Document; var e = (_currentElementId > 0) ? d?.GetElement(new ElementId(_currentElementId)) : null; if (e != null) Update(_lastUiApp, e); } catch { }
                UpdateCostText();   // refresh the live spend meter
            }
            catch (Exception ex) { Log.Debug($"AskModelAsync: {ex.Message}"); }
            finally { _chatBusy = false; }
        }

        private static string StripThink(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

        /// <summary>Encode a clipboard/bitmap image to a base64 PNG for the vision model.</summary>
        private static string EncodePng(System.Windows.Media.Imaging.BitmapSource src)
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using (var ms = new System.IO.MemoryStream()) { enc.Save(ms); return Convert.ToBase64String(ms.ToArray()); }
        }

        /// <summary>True when the user wants the model to LOOK at the current view (→ capture + vision model).</summary>
        private static bool LooksLikeVisionRequest(string q)
        {
            string s = (q ?? "").ToLowerInvariant();
            return s.Contains("look at") || s.Contains("what do you see") || s.Contains("screenshot")
                || s.Contains("see the view") || s.Contains("see the model") || s.Contains("take a picture")
                || s.Contains("capture the view") || s.Contains("what's on screen") || s.Contains("what is on screen")
                || s.Contains("examine the view") || s.Contains("review the view") || s.Contains("look at the");
        }

        /// <summary>Build a whole-model overview once per document (cached) so the model can
        /// answer project-wide questions cheaply — no per-question cost.</summary>
        private static void EnsureModelSummary(Document doc)
        {
            try
            {
                string key = (doc.PathName ?? "") + "|" + doc.Title;
                if (_modelSummary != null && _summaryDocKey == key) return;
                _summaryDocKey = key;
                _modelSummary = BuildModelSummary(doc);
            }
            catch (Exception ex) { Log.Debug($"EnsureModelSummary: {ex.Message}"); }
        }

        private static string BuildModelSummary(Document doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MODEL OVERVIEW (whole project):");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
            if (levels.Count > 0) sb.AppendLine("Levels: " + string.Join(", ", levels));

            int Cnt(BuiltInCategory c) { try { return new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount(); } catch { return 0; } }
            sb.AppendLine($"Counts: Walls {Cnt(BuiltInCategory.OST_Walls)}, Doors {Cnt(BuiltInCategory.OST_Doors)}, Windows {Cnt(BuiltInCategory.OST_Windows)}, Rooms {Cnt(BuiltInCategory.OST_Rooms)}, Plumbing Fixtures {Cnt(BuiltInCategory.OST_PlumbingFixtures)}, Specialty Equip {Cnt(BuiltInCategory.OST_SpecialityEquipment)}, Mech Equip {Cnt(BuiltInCategory.OST_MechanicalEquipment)}, Sheets {Cnt(BuiltInCategory.OST_Sheets)}");

            try
            {
                var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();
                if (doors.Count > 0)
                {
                    int nr = doors.Count(d => { var f = PName(d, "Fire Rating") ?? PName(doc.GetElement(d.GetTypeId()), "Fire Rating"); return string.IsNullOrWhiteSpace(f) || f.Equals("NR", StringComparison.OrdinalIgnoreCase); });
                    sb.AppendLine($"Doors: {doors.Count} total, ~{nr} non-rated / {doors.Count - nr} rated");
                }
            }
            catch { }

            try
            {
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.Architecture.Room>().Where(r => r.Area > 0).ToList();
                if (rooms.Count > 0)
                {
                    sb.AppendLine($"Rooms ({rooms.Count}):");
                    foreach (var r in rooms.Take(80)) sb.AppendLine($"  {PStr(r, BuiltInParameter.ROOM_NUMBER)} {PStr(r, BuiltInParameter.ROOM_NAME)} - {r.Level?.Name}, {r.Area:0} sf");
                    if (rooms.Count > 80) sb.AppendLine($"  ...and {rooms.Count - 80} more rooms");
                }
            }
            catch { }

            try { sb.AppendLine($"Open warnings: {doc.GetWarnings().Count}"); } catch { }
            return sb.ToString();
        }

        /// <summary>Plain-text grounding facts for the selected element + project, fed to the model.</summary>
        /// <summary>Whole-model context when nothing single is selected (the panel no longer auto-shows per element).</summary>
        private static string BuildProjectContextString(Document doc, int selCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a practical BIM assistant embedded in a live Revit model. Answer in plain language, like talking to a busy architect-drafter. Use ONLY the facts in the MODEL OVERVIEW below, or call tools to read more. If something isn't listed, say so or look it up. Do NOT invent values. Answer directly — no reasoning steps.");
            sb.AppendLine();
            sb.AppendLine("PROJECT: " + (PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NAME) ?? doc.Title));
            string addr = PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_ADDRESS);
            if (addr != null) sb.AppendLine("ADDRESS: " + addr);
            sb.AppendLine("ACTIVE VIEW: " + (doc.ActiveView?.Name ?? "?") + "  (view id: " + (doc.ActiveView != null ? doc.ActiveView.Id.Value : -1) + ")");
            sb.AppendLine(selCount > 1 ? ("The user has " + selCount + " elements selected right now.") : "No element is currently selected.");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(_modelSummary)) sb.AppendLine(_modelSummary);
            return sb.ToString();
        }

        /// <summary>Context for a MULTI-selection: lists each selected element (id, category, type,
        /// mark) so the agent can act on "these/them" with real ids — capped to keep the prompt small.</summary>
        private static string BuildMultiSelectContextString(Document doc, ICollection<ElementId> ids)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a practical BIM assistant embedded in a live Revit model. The user has MULTIPLE elements selected right now — 'these', 'them', 'the selected ones' means EXACTLY this selection; pass these element ids to tools that act on them. Use ONLY the facts here, or call tools to read more. Do NOT invent values. Answer directly — no reasoning steps.");
            sb.AppendLine();
            sb.AppendLine("PROJECT: " + (PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NAME) ?? doc.Title));
            sb.AppendLine("ACTIVE VIEW: " + (doc.ActiveView?.Name ?? "?") + "  (view id: " + (doc.ActiveView != null ? doc.ActiveView.Id.Value : -1) + ")");
            sb.AppendLine();
            sb.AppendLine("CURRENTLY SELECTED: " + ids.Count + " elements" + (ids.Count > 25 ? " (first 25 listed; the category totals cover all of them)" : "") + ":");
            int n = 0;
            var byCat = new Dictionary<string, int>();
            foreach (var id in ids)
            {
                var el = doc.GetElement(id); if (el == null) continue;
                string cat = el.Category?.Name ?? "?";
                byCat[cat] = byCat.TryGetValue(cat, out var c) ? c + 1 : 1;
                if (++n > 25) continue;
                var type = doc.GetElement(el.GetTypeId()) as ElementType;
                string mark = PStr(el, BuiltInParameter.ALL_MODEL_MARK);
                sb.AppendLine("- id " + el.Id.Value + ": " + cat + ", '" + (type?.Name ?? el.Name ?? "?") + "'" + (string.IsNullOrWhiteSpace(mark) ? "" : ", Mark " + mark));
            }
            sb.AppendLine("By category: " + string.Join(", ", byCat.Select(kv => kv.Value + " " + kv.Key)) + ".");
            if (!string.IsNullOrEmpty(_modelSummary)) { sb.AppendLine(); sb.AppendLine(_modelSummary); }
            return sb.ToString();
        }

        private static string BuildContextString(Document doc, Element el)
        {
            var u = doc.GetUnits();
            var type = doc.GetElement(el.GetTypeId());
            var sb = new StringBuilder();
            sb.AppendLine("You are a practical BIM assistant embedded in a live Revit model. Answer in plain language, like talking to a busy architect-drafter. You can answer about the SELECTED element OR about the whole model using the MODEL OVERVIEW below. Use ONLY the facts provided here; if something isn't listed, say it's not in the model. Do NOT invent attributes, hardware, features, or values that aren't given. Answer directly — no reasoning steps.");
            sb.AppendLine();
            sb.AppendLine("PROJECT: " + (PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NAME) ?? doc.Title));
            string addr = PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_ADDRESS);
            if (addr != null) sb.AppendLine("ADDRESS: " + addr);
            sb.AppendLine("ACTIVE VIEW: " + (doc.ActiveView?.Name ?? "?") + "  (view id: " + (doc.ActiveView != null ? doc.ActiveView.Id.Value : -1) + ")");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(_modelSummary)) { sb.AppendLine(_modelSummary); sb.AppendLine(); }
            sb.AppendLine("CURRENTLY SELECTED ELEMENT:");
            sb.AppendLine("Element id: " + el.Id.Value + "  (use this id when an action targets the selected element)");
            sb.AppendLine("Type id: " + el.GetTypeId().Value);
            sb.AppendLine("Category: " + (el.Category?.Name ?? "?"));
            if (el is FamilyInstance fam && fam.Symbol != null) sb.AppendLine("Family: " + fam.Symbol.Family?.Name);
            sb.AppendLine("Type: " + ((type as ElementType)?.Name ?? el.Name ?? "?"));
            if (el is TextNote ctxNote && !string.IsNullOrWhiteSpace(ctxNote.Text)) sb.AppendLine("TEXT CONTENT (the note's actual text): \"" + ctxNote.Text.Trim() + "\"");
            else if (el is Autodesk.Revit.DB.Grid ctxGrid) sb.AppendLine("GRID name: " + ctxGrid.Name);
            else if (el is Level ctxLvl) sb.AppendLine("LEVEL: " + ctxLvl.Name + " at elevation " + FmtLen(u, ctxLvl.Elevation));
            else if (el is Dimension ctxDim) { try { sb.AppendLine("DIMENSION value: " + (ctxDim.ValueString ?? "?") + "  (" + ctxDim.NumberOfSegments + " segment(s))"); } catch { } }
            else if (el is IndependentTag ctxTag) { try { sb.AppendLine("TAG text: \"" + ctxTag.TagText + "\""); } catch { } }
            else if (el is ViewSheet ctxVs) sb.AppendLine("SHEET: " + ctxVs.SheetNumber + " - " + ctxVs.Name);
            else if (el is View ctxView) sb.AppendLine("VIEW: " + ctxView.Name + " (" + ctxView.ViewType + ", scale 1:" + ctxView.Scale + ")");

            void Add(string label, string val) { if (!string.IsNullOrWhiteSpace(val)) sb.AppendLine(label + ": " + val); }
            Add("Mark", PStr(el, BuiltInParameter.ALL_MODEL_MARK));
            Add("Comments", PStr(el, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            Add("Level", PStr(el, BuiltInParameter.FAMILY_LEVEL_PARAM) ?? PStr(el, BuiltInParameter.SCHEDULE_LEVEL_PARAM));
            Add("Manufacturer", PStr(el, BuiltInParameter.ALL_MODEL_MANUFACTURER) ?? PStr(type, BuiltInParameter.ALL_MODEL_MANUFACTURER));
            Add("Model", PStr(el, BuiltInParameter.ALL_MODEL_MODEL) ?? PStr(type, BuiltInParameter.ALL_MODEL_MODEL));
            Add("Description", PStr(el, BuiltInParameter.ALL_MODEL_DESCRIPTION) ?? PStr(type, BuiltInParameter.ALL_MODEL_DESCRIPTION));
            Add("Fire rating", InferRating(PName(el, "Fire Rating") ?? PName(type, "Fire Rating"), (type as ElementType)?.Name));

            if (el is Wall w)
            {
                var wt = type as WallType;
                Add("Function", wt?.Function.ToString());
                Add("Length", FmtLen(u, w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0));
                Add("Height", FmtLen(u, w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0));
                var cs = wt?.GetCompoundStructure();
                if (cs != null)
                    Add("Assembly (ext->int)", string.Join(", ", cs.GetLayers().Select(L => { var m = doc.GetElement(L.MaterialId) as Material; return $"{L.Function} {(m != null ? m.Name : "-")} {FmtLen(u, L.Width)}"; })));
            }
            else if (el is Autodesk.Revit.DB.Architecture.Room rm)
            {
                Add("Number", PStr(rm, BuiltInParameter.ROOM_NUMBER));
                Add("Name", PStr(rm, BuiltInParameter.ROOM_NAME));
                Add("Department", PStr(rm, BuiltInParameter.ROOM_DEPARTMENT));
                Add("Area", $"{rm.Area:0.##} sf");
            }
            else if (el is FamilyInstance fi)
            {
                Add("Width", PName(type, "Width"));
                Add("Height", PName(type, "Height"));
                try { string fr = fi.FromRoom?.Name, tr = fi.ToRoom?.Name; if (fr != null || tr != null) Add("Connects (room to room)", (fr ?? "(none)") + " -> " + (tr ?? "(none)")); } catch { }
            }
            return sb.ToString();
        }

        private static void AddContext(Document doc)
        {
            string proj = PStr(doc.ProjectInformation, BuiltInParameter.PROJECT_NAME) ?? doc.Title;
            string view = doc.ActiveView?.Name;
            _body.Children.Add(new TextBlock { Text = proj + (view != null ? "   ·   " + view : ""), Foreground = Ctx, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }

        // ---------------------------------------------------------------- element facts (read-only)
        private static void BuildWall(Document doc, Wall wall)
        {
            var wt = doc.GetElement(wall.GetTypeId()) as WallType;
            var u = doc.GetUnits();
            double len = wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
            double area = wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
            string func = wt?.Function.ToString() ?? "?";
            string fire = InferRating(PName(wall, "Fire Rating") ?? PName(wt, "Fire Rating"), wt?.Name);
            _title.Text = "Wall";

            var sb = new StringBuilder();
            sb.AppendLine("Function:    " + func);
            sb.AppendLine("Length:      " + FmtLen(u, len));
            sb.AppendLine($"Face area:   {area:0.##} sf");
            sb.AppendLine("Fire rating: " + fire);
            var cs = wt?.GetCompoundStructure();
            if (cs != null)
            {
                sb.AppendLine();
                sb.AppendLine("Assembly (ext -> int):");
                foreach (var L in cs.GetLayers())
                {
                    var mat = doc.GetElement(L.MaterialId) as Material;
                    sb.AppendLine($"  - {L.Function}: {(mat != null ? mat.Name : "(no material)")}  ({FmtLen(u, L.Width)})");
                }
            }
            _body.Children.Add(Line(sb.ToString().TrimEnd()));
        }

        private static void BuildRoom(Document doc, Autodesk.Revit.DB.Architecture.Room room)
        {
            var u = doc.GetUnits();
            string num = PStr(room, BuiltInParameter.ROOM_NUMBER);
            string name = PStr(room, BuiltInParameter.ROOM_NAME);
            _title.Text = "Room " + (num ?? "") + (name != null ? "  -  " + name : "");

            var sb = new StringBuilder();
            string dept = PStr(room, BuiltInParameter.ROOM_DEPARTMENT);
            if (dept != null) sb.AppendLine("Department:  " + dept);
            if (room.Level != null) sb.AppendLine("Level:       " + room.Level.Name);
            sb.AppendLine($"Area:        {room.Area:0.##} sf");
            try { sb.AppendLine("Perimeter:   " + FmtLen(u, room.Perimeter)); } catch { }

            string ff = PStr(room, BuiltInParameter.ROOM_FINISH_FLOOR);
            string fb = PStr(room, BuiltInParameter.ROOM_FINISH_BASE);
            string fw = PStr(room, BuiltInParameter.ROOM_FINISH_WALL);
            string fc = PStr(room, BuiltInParameter.ROOM_FINISH_CEILING);
            if (ff != null || fb != null || fw != null || fc != null)
            {
                sb.AppendLine();
                sb.AppendLine("Finishes:");
                if (ff != null) sb.AppendLine("  Floor:    " + ff);
                if (fb != null) sb.AppendLine("  Base:     " + fb);
                if (fw != null) sb.AppendLine("  Wall:     " + fw);
                if (fc != null) sb.AppendLine("  Ceiling:  " + fc);
            }
            _body.Children.Add(Line(sb.ToString().TrimEnd()));
        }

        private static void BuildDoor(Document doc, FamilyInstance fi)
        {
            if (fi == null) return;
            var type = doc.GetElement(fi.GetTypeId());
            string typeName = (type as ElementType)?.Name ?? "Door";
            _title.Text = "Door  -  " + typeName;

            var sb = new StringBuilder();
            string w = PName(type, "Width"); string h = PName(type, "Height");
            if (w != null || h != null) sb.AppendLine("Size:        " + (w ?? "?") + "  x  " + (h ?? "?"));
            string lvl = PStr(fi, BuiltInParameter.FAMILY_LEVEL_PARAM) ?? PStr(fi, BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (lvl != null) sb.AppendLine("Level:       " + lvl);
            sb.AppendLine("Fire rating: " + InferRating(PName(fi, "Fire Rating") ?? PName(type, "Fire Rating"), typeName));
            try
            {
                string from = fi.FromRoom?.Name; string to = fi.ToRoom?.Name;
                if (from != null || to != null) sb.AppendLine("Connects:    " + (from ?? "(none)") + "  ->  " + (to ?? "(none)"));
            }
            catch { }
            _body.Children.Add(Line(sb.ToString().TrimEnd()));
        }

        private static void BuildWindow(Document doc, FamilyInstance fi)
        {
            if (fi == null) return;
            var type = doc.GetElement(fi.GetTypeId());
            string typeName = (type as ElementType)?.Name ?? "Window";
            _title.Text = "Window  -  " + typeName;

            var sb = new StringBuilder();
            string w = PName(type, "Width"); string h = PName(type, "Height");
            if (w != null || h != null) sb.AppendLine("Size:       " + (w ?? "?") + "  x  " + (h ?? "?"));
            string sill = PStr(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill != null) sb.AppendLine("Sill ht:    " + sill);
            string lvl = PStr(fi, BuiltInParameter.FAMILY_LEVEL_PARAM) ?? PStr(fi, BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (lvl != null) sb.AppendLine("Level:      " + lvl);
            string host = (fi.Host as Wall)?.Name;
            if (host != null) sb.AppendLine("Host wall:  " + host);
            _body.Children.Add(Line(sb.ToString().TrimEnd()));
        }

        private static void BuildEquipment(Document doc, FamilyInstance fi)
        {
            if (fi == null) return;
            var type = doc.GetElement(fi.GetTypeId());
            string typeName = (type as ElementType)?.Name ?? fi.Name ?? "Equipment";
            string cat = fi.Category?.Name ?? "Equipment";
            _title.Text = cat + "  -  " + typeName;

            string mfr = PStr(fi, BuiltInParameter.ALL_MODEL_MANUFACTURER) ?? PStr(type, BuiltInParameter.ALL_MODEL_MANUFACTURER);
            string model = PStr(fi, BuiltInParameter.ALL_MODEL_MODEL) ?? PStr(type, BuiltInParameter.ALL_MODEL_MODEL);
            string cost = PStr(fi, BuiltInParameter.ALL_MODEL_COST) ?? PStr(type, BuiltInParameter.ALL_MODEL_COST);
            string desc = PStr(fi, BuiltInParameter.ALL_MODEL_DESCRIPTION) ?? PStr(type, BuiltInParameter.ALL_MODEL_DESCRIPTION);
            string lvl = PStr(fi, BuiltInParameter.FAMILY_LEVEL_PARAM) ?? PStr(fi, BuiltInParameter.SCHEDULE_LEVEL_PARAM);

            var sb = new StringBuilder();
            if (mfr != null) sb.AppendLine("Manufacturer: " + mfr);
            if (model != null) sb.AppendLine("Model:        " + model);
            if (cost != null) sb.AppendLine("Cost:         " + cost);
            if (lvl != null) sb.AppendLine("Level:        " + lvl);

            // Utility / connection params — whatever the family carries (CSPD equipment coordination).
            var conns = new List<string>();
            foreach (var pn in UtilityParams) { string v = PName(fi, pn) ?? PName(type, pn); if (v != null) conns.Add("  " + pn + ":  " + v); }
            if (conns.Count > 0) { sb.AppendLine(); sb.AppendLine("Connections / utilities:"); foreach (var c in conns) sb.AppendLine(c); }
            if (desc != null) sb.AppendLine("\n" + desc);

            string body = sb.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(body)) body = "(no manufacturer/model data on this equipment yet — add it to the type)";
            _body.Children.Add(Line(body));

            string url = PStr(fi, BuiltInParameter.ALL_MODEL_URL) ?? PStr(type, BuiltInParameter.ALL_MODEL_URL);
            string cut = PName(fi, "Cut Sheet") ?? PName(type, "Cut Sheet") ?? PName(type, "Spec Sheet");
            string buy = PName(fi, "Buy URL") ?? PName(type, "Buy URL") ?? PName(type, "Where to Buy");
            void AddLink(string label, string u2)
            {
                var btn = new Button { Content = label, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
                btn.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = u2, UseShellExecute = true }); } catch (Exception ex) { Log.Debug($"link: {ex.Message}"); } };
                _body.Children.Add(btn);
            }
            if (url != null) AddLink("Open manufacturer website", url);
            if (cut != null) AddLink("Open cut sheet / spec", cut);
            if (buy != null) AddLink("Where to buy", buy);
        }

        private static void BuildGeneric(Document doc, Element el)
        {
            var type = doc.GetElement(el.GetTypeId());
            string typeName = (type as ElementType)?.Name ?? el.Name ?? "Element";
            string cat = el.Category?.Name ?? "Element";
            _title.Text = cat + "  -  " + typeName;

            string url = PStr(el, BuiltInParameter.ALL_MODEL_URL) ?? PStr(type, BuiltInParameter.ALL_MODEL_URL);
            string mfr = PStr(el, BuiltInParameter.ALL_MODEL_MANUFACTURER) ?? PStr(type, BuiltInParameter.ALL_MODEL_MANUFACTURER);
            string model = PStr(el, BuiltInParameter.ALL_MODEL_MODEL) ?? PStr(type, BuiltInParameter.ALL_MODEL_MODEL);
            string desc = PStr(el, BuiltInParameter.ALL_MODEL_DESCRIPTION) ?? PStr(type, BuiltInParameter.ALL_MODEL_DESCRIPTION);
            string cost = PStr(el, BuiltInParameter.ALL_MODEL_COST) ?? PStr(type, BuiltInParameter.ALL_MODEL_COST);
            string cut = PName(el, "Cut Sheet") ?? PName(type, "Cut Sheet") ?? PName(type, "Spec Sheet");
            string buy = PName(el, "Buy URL") ?? PName(type, "Buy URL") ?? PName(type, "Where to Buy");

            var sb = new StringBuilder();
            if (mfr != null) sb.AppendLine("Manufacturer: " + mfr);
            if (model != null) sb.AppendLine("Model:        " + model);
            if (cost != null) sb.AppendLine("Cost:         " + cost);
            string lvl = PStr(el, BuiltInParameter.FAMILY_LEVEL_PARAM) ?? PStr(el, BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (lvl != null) sb.AppendLine("Level:        " + lvl);
            if (desc != null) sb.AppendLine("\n" + desc);

            string body = sb.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(body)) _body.Children.Add(Line(body));

            void AddLink(string label, string u2)
            {
                var btn = new Button { Content = label, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
                btn.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = u2, UseShellExecute = true }); } catch (Exception ex) { Log.Debug($"link: {ex.Message}"); } };
                _body.Children.Add(btn);
            }
            if (url != null) AddLink("Open manufacturer website", url);
            if (cut != null) AddLink("Open cut sheet / spec", cut);
            if (buy != null) AddLink("Where to buy", buy);
        }
    }
}
