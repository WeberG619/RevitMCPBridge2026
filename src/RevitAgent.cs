using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge
{
    /// <summary>
    /// In-process LLM agent: wires the local Ollama model to the FULL bridge method registry
    /// (all 437+ methods) through just TWO "master-key" tools — find_method + run_method — so the
    /// model has 100% access to every Revit operation WITHOUT being handed hundreds of tool
    /// schemas (which makes models choke). Tool execution is marshalled onto Revit's API thread
    /// via an ExternalEvent; the calling (background) thread blocks on a signal until it's done.
    /// Optional & local: if Ollama isn't running, callers fall back to rule commands. No cloud.
    /// </summary>
    public static class RevitAgent
    {
        private static string OllamaUrl = "http://localhost:11434/api/chat";
        private static string OllamaModel = "qwen3:32b";   // default; override via model_copilot.json
        private static string VisionModel = "qwen2.5vl:7b";  // vision model (7b fits w/ encoder headroom on a 24GB GPU; 32b doesn't)
        private static string ApiType = "ollama";          // "ollama" (local, default) | "openai" (any OpenAI-compatible API)
        private static string ApiKey = "";                 // bearer token, for openai-type endpoints (OpenAI/OpenRouter/Groq/LM Studio…)
        public static bool Enabled = true;                 // LLM on/off (transferability — works without it)

        // ---- cost guardrail (only applies to paid API; local is always free) ----
        private static double DailyCapUsd = 5.0;           // hard daily spend cap for API calls
        private static double InRatePerM = 15.0;           // $ per million INPUT tokens (default ~Opus; set to your model in Settings)
        private static double OutRatePerM = 75.0;          // $ per million OUTPUT tokens
        private static double _spentToday = 0.0;
        private static long _tokensToday = 0;
        private static string _usageDate = "";

        // ---- read accessors + save, for the Settings dialog ----
        public static string CfgApiType => ApiType;
        public static string CfgUrl => OllamaUrl;
        public static string CfgModel => OllamaModel;
        public static string CfgVisionModel => VisionModel;
        public static string CfgApiKey => ApiKey;
        public static bool CfgEnabled => Enabled;
        public static bool IsLocal => ApiType == "ollama";
        public static double CfgDailyCap => DailyCapUsd;
        public static double CfgInRate => InRatePerM;
        public static double CfgOutRate => OutRatePerM;
        public static double SpentTodayUsd { get { RollUsageDate(); return _spentToday; } }
        public static long TokensTodayCount { get { RollUsageDate(); return _tokensToday; } }
        public static string ConfigPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "model_copilot.json");
        private static string UsagePath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "model_copilot_usage.json");

        /// <summary>Reset/restore today's spend counter on a new day (persists across restarts).</summary>
        private static void RollUsageDate()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_usageDate == today) return;
            try
            {
                if (System.IO.File.Exists(UsagePath))
                {
                    var u = JObject.Parse(System.IO.File.ReadAllText(UsagePath));
                    if ((string)u["date"] == today) { _spentToday = (double)(u["spentUsd"] ?? 0); _tokensToday = (long)(u["tokens"] ?? 0); _usageDate = today; return; }
                }
            }
            catch { }
            _spentToday = 0; _tokensToday = 0; _usageDate = today; SaveUsage();
        }
        private static void SaveUsage()
        {
            try { System.IO.File.WriteAllText(UsagePath, new JObject { ["date"] = _usageDate, ["spentUsd"] = _spentToday, ["tokens"] = _tokensToday }.ToString(Formatting.None)); } catch { }
        }
        /// <summary>Record token usage from a response and add to today's running cost (local = free).</summary>
        private static void TrackUsage(JObject jo, bool openai)
        {
            try
            {
                RollUsageDate();
                long inT, outT;
                if (openai && jo["usage"] != null) { inT = (long)(jo["usage"]["prompt_tokens"] ?? 0); outT = (long)(jo["usage"]["completion_tokens"] ?? 0); }
                else { inT = (long)(jo["prompt_eval_count"] ?? 0); outT = (long)(jo["eval_count"] ?? 0); }
                _tokensToday += inT + outT;
                if (!IsLocal) _spentToday += inT / 1_000_000.0 * InRatePerM + outT / 1_000_000.0 * OutRatePerM;
                SaveUsage();
            }
            catch { }
        }
        /// <summary>True if a paid-API call would exceed today's cap (local is never capped).</summary>
        public static bool OverDailyCap() { if (IsLocal) return false; RollUsageDate(); return _spentToday >= DailyCapUsd; }
        public static string CapMessage() => $"Daily API spending cap reached (${_spentToday:0.00} of ${DailyCapUsd:0.00}). It resets tomorrow — or raise the cap in Settings. Tip: switch to the Local model in Settings to keep working for free.";

        // ---- learning memory: the firm's standards + corrections the model follows and grows ----
        private static string MemoryPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "model_copilot_memory.json");

        /// <summary>The standards/preferences block injected into every system prompt (so it acts the firm's way).</summary>
        public static string StandardsBlock()
        {
            try
            {
                if (!System.IO.File.Exists(MemoryPath)) return "";
                var arr = JArray.Parse(System.IO.File.ReadAllText(MemoryPath));
                if (arr.Count == 0) return "";
                var sb = new StringBuilder();
                sb.AppendLine("BIM OPS STUDIO STANDARDS & LEARNED PREFERENCES (the user is Weber Gouin; ALWAYS follow these — they OVERRIDE generic Revit defaults):");
                foreach (var n in arr) { var t = (n is JObject o ? o["note"]?.ToString() : n.ToString()); if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine("- " + t); }
                sb.AppendLine();
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string KnowledgePath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "model_copilot_knowledge.txt");

        /// <summary>Curated Revit best-practice know-how injected into the system prompt (the "expert" layer).</summary>
        public static string KnowledgeBlock()
        {
            try
            {
                if (!System.IO.File.Exists(KnowledgePath)) return "";
                var t = System.IO.File.ReadAllText(KnowledgePath).Trim();
                return t.Length == 0 ? "" : "REVIT BEST-PRACTICE KNOW-HOW (apply this expertise when advising or acting; prefer the standard, clean Revit way):\n" + t + "\n\n";
            }
            catch { return ""; }
        }

        private static string RecipesPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "model_copilot_recipes.txt");

        /// <summary>Proven task -> tool-sequence recipes (few-shot examples). Local models copy these far better
        /// than they follow instructions. Grows as workflows get certified.</summary>
        public static string RecipesBlock()
        {
            try
            {
                if (!System.IO.File.Exists(RecipesPath)) return "";
                var t = System.IO.File.ReadAllText(RecipesPath).Trim();
                return t.Length == 0 ? "" : "PROVEN RECIPES — when a request matches one of these, FOLLOW THE EXACT PATTERN (these are verified to work):\n" + t + "\n\n";
            }
            catch { return ""; }
        }

        /// <summary>All saved standards/preferences (for the Standards manager UI).</summary>
        public static List<string> GetStandards()
        {
            var list = new List<string>();
            try
            {
                if (!System.IO.File.Exists(MemoryPath)) return list;
                foreach (var n in JArray.Parse(System.IO.File.ReadAllText(MemoryPath)))
                {
                    var t = (n is JObject o ? o["note"]?.ToString() : n.ToString());
                    if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Remove a saved standard by its exact text (from the Standards manager UI).</summary>
        public static void ForgetStandard(string note)
        {
            try
            {
                if (!System.IO.File.Exists(MemoryPath)) return;
                var arr = JArray.Parse(System.IO.File.ReadAllText(MemoryPath));
                var keep = new JArray();
                foreach (var n in arr) { var t = (n is JObject o ? o["note"]?.ToString() : n.ToString()); if (t != note) keep.Add(n); }
                System.IO.File.WriteAllText(MemoryPath, keep.ToString(Formatting.Indented));
            }
            catch { }
        }

        /// <summary>Append a standard/preference/correction the user taught us, so future sessions follow it.</summary>
        public static string Remember(string note)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(note)) return "{\"success\":false,\"error\":\"nothing to remember\"}";
                JArray arr = System.IO.File.Exists(MemoryPath) ? JArray.Parse(System.IO.File.ReadAllText(MemoryPath)) : new JArray();
                arr.Add(new JObject { ["note"] = note.Trim(), ["ts"] = DateTime.Now.ToString("yyyy-MM-dd") });
                System.IO.File.WriteAllText(MemoryPath, arr.ToString(Formatting.Indented));
                return "{\"success\":true,\"saved\":\"" + Esc(note.Trim()) + "\"}";
            }
            catch (Exception ex) { return "{\"success\":false,\"error\":\"" + Esc(ex.Message) + "\"}"; }
        }

        /// <summary>Write the config file from the Settings dialog and apply it immediately.</summary>
        public static void SaveConfig(string apiType, string url, string model, string visionModel, string apiKey, bool enabled,
            double dailyCapUsd, double inputCostPerM, double outputCostPerM)
        {
            try
            {
                var cfg = new JObject
                {
                    ["apiType"] = string.IsNullOrWhiteSpace(apiType) ? "ollama" : apiType.Trim().ToLowerInvariant(),
                    ["apiUrl"] = url?.Trim(),
                    ["model"] = model?.Trim(),
                    ["visionModel"] = visionModel?.Trim(),
                    ["apiKey"] = apiKey ?? "",
                    ["enabled"] = enabled,
                    ["dailyCapUsd"] = dailyCapUsd,
                    ["inputCostPerM"] = inputCostPerM,
                    ["outputCostPerM"] = outputCostPerM
                };
                System.IO.File.WriteAllText(ConfigPath, cfg.ToString(Formatting.Indented));
                LoadConfig();   // apply now
            }
            catch (Exception ex) { Log.Debug($"SaveConfig: {ex.Message}"); }
        }
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        /// <summary>Load optional config (model_copilot.json next to the DLL) — endpoint, model, on/off.</summary>
        private static void LoadConfig()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(dir ?? "", "model_copilot.json");
                if (!System.IO.File.Exists(path)) return;
                var cfg = JObject.Parse(System.IO.File.ReadAllText(path));
                if (cfg["apiType"] != null) ApiType = cfg["apiType"].ToString().Trim().ToLowerInvariant();
                if (cfg["apiUrl"] != null) OllamaUrl = cfg["apiUrl"].ToString();
                else if (cfg["ollamaUrl"] != null) OllamaUrl = cfg["ollamaUrl"].ToString();   // back-compat
                if (cfg["model"] != null) OllamaModel = cfg["model"].ToString();
                if (cfg["visionModel"] != null) VisionModel = cfg["visionModel"].ToString();
                if (cfg["apiKey"] != null) ApiKey = cfg["apiKey"].ToString();
                if (cfg["enabled"] != null && cfg["enabled"].Type == JTokenType.Boolean) Enabled = (bool)cfg["enabled"];
                if (cfg["dailyCapUsd"] != null) DailyCapUsd = (double)cfg["dailyCapUsd"];
                if (cfg["inputCostPerM"] != null) InRatePerM = (double)cfg["inputCostPerM"];
                if (cfg["outputCostPerM"] != null) OutRatePerM = (double)cfg["outputCostPerM"];
                if (ApiType != "ollama" && ApiType != "openai") ApiType = "ollama";
                Log.Information($"Model Copilot config loaded: apiType={ApiType} model={OllamaModel} url={OllamaUrl} vision={VisionModel} key={(string.IsNullOrEmpty(ApiKey) ? "none" : "set")} enabled={Enabled}");
            }
            catch (Exception ex) { Log.Debug($"LoadConfig: {ex.Message}"); }
        }

        // ---- tool execution on the Revit API thread ----
        private static ExternalEvent _toolEvent;
        private static readonly object _gate = new object();   // serialize tool calls
        private static string _toolMethod, _toolParamsJson, _toolResult;
        private static ManualResetEventSlim _toolDone;

        private class ToolHandler : IExternalEventHandler
        {
            public string GetName() => "RevitAgent.ToolHandler";
            public void Execute(UIApplication app)
            {
                // While the Copilot is acting, sense + auto-clear Revit warning/message dialogs so they
                // never silently block the bridge. Restored immediately after, so the user's own manual
                // dialogs are never touched.
                bool prevAuto = RevitMCPBridgeApp.AutoHandleDialogs;
                RevitMCPBridgeApp.AutoHandleDialogs = true;
                try { RevitMCPBridgeApp.AgentDialogLog.Clear(); } catch { }
                try
                {
                    string result;
                    try
                    {
                        JObject p;
                        try { p = string.IsNullOrWhiteSpace(_toolParamsJson) ? new JObject() : JObject.Parse(_toolParamsJson); }
                        catch (Exception ex) { _toolResult = "{\"success\":false,\"error\":\"bad params JSON: " + Esc(ex.Message) + "\"}"; return; }
                        result = MCPServer.ExecuteMethod(app, _toolMethod, p);
                    }
                    catch (Exception ex) { result = "{\"success\":false,\"error\":\"" + Esc(ex.Message) + "\"}"; }
                    _toolResult = result;
                }
                finally
                {
                    RevitMCPBridgeApp.AutoHandleDialogs = prevAuto;
                    try
                    {
                        if (RevitMCPBridgeApp.AgentDialogLog.Count > 0)
                            _toolResult = (_toolResult ?? "") + "  | NOTE — Revit popups handled during this step: " + string.Join("; ", RevitMCPBridgeApp.AgentDialogLog);
                    }
                    catch { }
                    _toolDone.Set();
                }
            }
        }

        public static void Init()
        {
            try { LoadConfig(); if (_toolEvent == null) _toolEvent = ExternalEvent.Create(new ToolHandler()); }
            catch (Exception ex) { Log.Debug($"RevitAgent.Init: {ex.Message}"); }
        }

        // ---- destructive-action safety gate ----
        private static string _pendMethod, _pendParams;
        public static bool HasPendingDestructive => _pendMethod != null;

        /// <summary>True for actions that destroy/remove model content and must be confirmed.</summary>
        private static bool IsDestructive(string method, string paramsJson)
        {
            var m = (method ?? "").ToLowerInvariant();
            if (m.Contains("delete")) return true;                       // deleteElement/Wall/Room/Sheet/View/...
            if (m == "purgeunused")
            {
                try { var p = JObject.Parse(paramsJson ?? "{}"); if (p["dryRun"] != null && p["dryRun"].Type == JTokenType.Boolean && (bool)p["dryRun"] == false) return true; } catch { }
                return false;   // dry-run report is safe
            }
            return false;
        }

        /// <summary>Execute the action that was blocked pending confirmation. Called after the user confirms.</summary>
        public static string ConfirmPendingDestructive()
        {
            if (_pendMethod == null) return "{\"success\":false,\"error\":\"nothing pending\"}";
            string m = _pendMethod, p = _pendParams; _pendMethod = null; _pendParams = null;
            return RunMethodCore(m, p, true);
        }
        public static void CancelPendingDestructive() { _pendMethod = null; _pendParams = null; }

        /// <summary>Run a bridge method by name on the API thread. Destructive actions are BLOCKED
        /// (not executed) until the user confirms — enforced here in code, not left to the model.</summary>
        private static string RunMethod(string method, string paramsJson) => RunMethodCore(method, paramsJson, false);

        private static string RunMethodCore(string method, string paramsJson, bool confirmed)
        {
            if (string.IsNullOrWhiteSpace(method)) return "{\"success\":false,\"error\":\"no method name\"}";
            // models copy find_method's "Class.method" form — the registry only knows the bare name
            if (method.Contains('.')) method = method.Substring(method.LastIndexOf('.') + 1);
            if (method.Length > 1 && char.IsUpper(method[0])) method = char.ToLowerInvariant(method[0]) + method.Substring(1);
            // never offer a confirm ceremony for a GUESSED method — validate existence first, else
            // the user confirms a destructive action that then dies as METHOD_NOT_FOUND
            try
            {
                var cat = Catalog();
                if (cat != null && cat.Count > 0 && !cat.Any(kv => kv.Key.Equals(method, StringComparison.OrdinalIgnoreCase)))
                    return "{\"success\":false,\"error\":\"Method '" + Esc(method) + "' does not exist — never guess method names. Use find_method to search, or read the elements (getTextNotes/getWalls...) and act on their ids with known tools.\"}";
            }
            catch { }
            if (!confirmed && IsDestructive(method, paramsJson))
            {
                _pendMethod = method; _pendParams = paramsJson;
                return "{\"blocked\":true,\"needsConfirmation\":true,\"message\":\"This is a DESTRUCTIVE action (" + Esc(method) + ") and was NOT executed. Tell the user EXACTLY what it will delete/change and ask them to reply 'confirm' to proceed (anything else cancels).\"}";
            }
            lock (_gate)
            {
                _toolMethod = method; _toolParamsJson = paramsJson; _toolResult = null;
                _toolDone = new ManualResetEventSlim(false);
                try { _toolEvent?.Raise(); } catch (Exception ex) { return "{\"success\":false,\"error\":\"raise failed: " + Esc(ex.Message) + "\"}"; }
                if (!_toolDone.Wait(TimeSpan.FromSeconds(60))) return "{\"success\":false,\"error\":\"tool timed out\"}";
                return _toolResult ?? "{\"success\":false,\"error\":\"no result\"}";
            }
        }

        // ---- method catalog (for find_method) ----
        private static List<KeyValuePair<string, string>> _catalog;
        private static List<KeyValuePair<string, string>> Catalog()
        {
            if (_catalog != null) return _catalog;
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var scan = MCPMethodScanner.Scan();
                var meta = scan?.Metadata;
                foreach (var name in MCPServer.GetRegisteredMethods())
                {
                    string desc = (meta != null && meta.TryGetValue(name, out var mi)) ? mi.Description : name;
                    list.Add(new KeyValuePair<string, string>(name, desc ?? name));
                }
            }
            catch (Exception ex) { Log.Debug($"RevitAgent.Catalog: {ex.Message}"); }
            _catalog = list;
            return list;
        }

        private static string FindMethods(string query)
        {
            var terms = (query ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '_', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 2)
                .Select(t => t.Length > 3 && t.EndsWith("s") ? t.Substring(0, t.Length - 1) : t)   // plural-blind: 'notes' must match 'deleteTextNote'
                .Distinct().ToList();
            if (terms.Count == 0) return "Provide keywords (e.g. 'door type', 'tag room').";
            var scored = Catalog()
                .Select(kv =>
                {
                    string nameL = kv.Key.ToLowerInvariant(), descL = (kv.Value ?? "").ToLowerInvariant();
                    int nameHits = terms.Count(t => nameL.Contains(t));
                    int score = nameHits * 2 + terms.Count(t => descL.Contains(t));   // the NAME is the signal
                    if (nameHits == terms.Count) score += 10;                          // every term in the name = almost certainly it
                    return new { kv.Key, kv.Value, score };
                })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.Key.Length)
                .Take(15).ToList();
            if (scored.Count == 0) return "No methods matched '" + query + "'. Try simpler keywords.";
            var sb = new StringBuilder();
            foreach (var s in scored) sb.AppendLine(s.Key + " — " + s.Value);
            return sb.ToString().Trim();
        }

        // ---- the tool-use chat loop ----
        private static string Friendly(string tool)
        {
            switch (tool)
            {
                case "find_method": return "searching operations…";
                case "run_method": return "running an operation…";
                case "coordinate_model": return "coordinating the model…";
                case "find_duplicates": return "checking for duplicates…";
                case "run_clash_check": return "checking clashes…";
                case "get_warnings": return "reading warnings…";
                case "quality_check": return "running a QC pass…";
                case "audit_coordination": return "auditing coordination…";
                case "run_code_check": return "checking building code…";
                case "purge_unused": return "scanning for unused elements…";
                case "write_report": return "writing the report…";
                case "export_schedule": return "exporting the schedule…";
                case "export_sheets_pdf": return "exporting to PDF…";
                case "tag_all": return "tagging…";
                case "set_param_all": return "updating all of them…";
                case "auto_mark": return "numbering…";
                case "analyze_pdf": return "reading the PDF set (finding the floor plans)…";
                case "find_detail": return "searching the detail library…";
                case "import_detail": return "bringing the detail in…";
                case "create_schedule": return "creating the schedule…";
                case "set_marks": return "numbering the marks…";
                case "fix_marks": return "fixing duplicate marks…";
                case "tag_untagged": return "tagging the untagged…";
                case "import_pdf": return "importing the PDF…";
                case "import_image": return "importing the image…";
                case "create_walls": return "building the walls…";
                case "add_door": return "placing the door…";
                case "add_window": return "placing the window…";
                case "create_sheet": return "creating the sheet…";
                case "place_view": return "placing the view on the sheet…";
                case "undo": return "undoing the last change…";
                case "create_3d_view": return "creating the 3D view…";
                case "create_section": return "creating the section…";
                case "create_elevation": return "creating the elevation…";
                case "create_callout": return "creating the callout…";
                case "duplicate_view": return "duplicating the view…";
                case "set_view_scale": return "setting the view scale…";
                case "apply_view_template": return "applying the view template…";
                case "place_keynote": return "placing the keynote…";
                case "create_revision_cloud": return "drawing the revision cloud…";
                case "place_spot_elevation": return "placing the spot elevation…";
                case "create_room": return "placing the room…";
                case "create_floor": return "creating the floor…";
                case "create_ceiling": return "creating the ceiling…";
                case "create_roof": return "creating the roof…";
                case "create_curtain_wall": return "creating the curtain wall…";
                case "place_column": return "placing the column…";
                case "create_beam": return "creating the beam…";
                case "create_stair": return "creating the stair…";
                case "create_duct": return "creating the duct…";
                case "create_pipe": return "creating the pipe…";
                case "place_fixture": return "placing the fixture…";
                case "create_level": return "creating the level…";
                case "create_grid": return "creating the grid…";
                case "move_element": return "moving it…";
                case "copy_element": return "copying it…";
                case "rotate_element": return "rotating it…";
                case "array_element": return "arraying it…";
                case "place_text": return "placing the text…";
                case "name_room": return "naming the room…";
                case "renumber_rooms": return "renumbering the rooms…";
                case "select_category": return "selecting them…";
                case "dimension_walls": return "dimensioning to the core-finish face…";
                case "select_element": return "selecting it…";
                case "find_cad": return "finding the CAD/DWG imports…";
                case "find_wall": return "locating the wall…";
                case "set_parameter": case "set_text": case "change_type": case "tag_element": return "making the change…";
                case "remember": return "saving that to memory…";
                case "search_files": return "searching your files…";
                case "read_file": return "reading the file…";
                case "web_search": return "searching the web…";
                case "load_family": return "finding a family in your libraries…";
                case "load_autodesk_family": return "searching the Autodesk cloud library…";
                case "load_family_file": return "loading the family…";
                case "download_family": return "downloading the family…";
                case "place_family": return "placing the family…";
                case "delete_element": return "preparing…";
                default: return "working… (" + tool + ")";
            }
        }

        /// <summary>Ask the VISION model about an image (base64 PNG) — a pasted screenshot or a captured view.</summary>
        public static async Task<string> ChatVisionAsync(string question, string base64Image, string context)
        {
            try
            {
                if (!Enabled) return "The AI assistant is turned off in model_copilot.json (enabled=false).";
                if (OverDailyCap()) return CapMessage();
                if (string.IsNullOrEmpty(base64Image)) return "No image to look at.";
                string proj = "";
                if (!string.IsNullOrEmpty(context)) { int i = context.IndexOf("PROJECT"); if (i >= 0) proj = context.Substring(i, Math.Min(160, context.Length - i)); }
                string sys = "You are a BIM/architecture assistant looking at an image from a Revit model — a view, plan, detail, schedule, markup, or screenshot. Describe what you see and answer the user's question practically and concisely. " + proj;
                string q = string.IsNullOrWhiteSpace(question) ? "What do you see in this image? Anything notable for a set of construction documents?" : question;
                JObject userMsg = ApiType == "openai"
                    ? new JObject { ["role"] = "user", ["content"] = new JArray {
                          new JObject { ["type"] = "text", ["text"] = q },
                          new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64," + base64Image } } } }
                    : new JObject { ["role"] = "user", ["content"] = q, ["images"] = new JArray { base64Image } };
                var messages = new JArray { new JObject { ["role"] = "system", ["content"] = sys }, userMsg };
                var msg = await PostChatAsync(VisionModel, messages, null, false).ConfigureAwait(false);
                if (msg == null) return "Vision model error — check the '" + VisionModel + "' model and endpoint in Settings.";
                string ans = msg["content"]?.ToString();
                return string.IsNullOrWhiteSpace(ans) ? "(the vision model returned nothing)" : StripThink(ans);
            }
            catch (Exception ex) { return "Vision error: " + ex.Message; }
        }

        /// <summary>Export a view to PNG (via the bridge) and return it base64 for the vision model.</summary>
        public static string CaptureViewB64(int viewId)
        {
            try
            {
                if (viewId <= 0) return null;
                string res = RunMethod("exportViewImage", "{\"viewId\":" + viewId + "}");
                var jo = JObject.Parse(res);
                string path = jo["result"]?["outputPath"]?.ToString() ?? jo["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                return Convert.ToBase64String(System.IO.File.ReadAllBytes(path));
            }
            catch (Exception ex) { Log.Debug($"CaptureViewB64: {ex.Message}"); return null; }
        }

        // ---- LIGHTWEIGHT PLANNING LAYER: local models stall/fixate on long multi-rule prompts, so
        // batch requests (numbered lists, multi-line task dumps, long then-chains) are split in CODE
        // into focused sub-prompts run one at a time, each with the prior steps' results carried
        // forward (so step 2 can use ids created in step 1). Single requests pass straight through.

        /// <summary>Split a batch request into steps. Returns empty list (not a batch) or 2+ steps.</summary>
        private static List<string> SplitSteps(string q, out string preamble)
        {
            preamble = "";
            var steps = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(q)) return steps;
                var lines = q.Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                var itemRx = new System.Text.RegularExpressions.Regex(@"^(?:\d{1,2}[\.\)]\s+|[-•*]\s+|step\s+\d+\s*[:\.\)]\s*|task\s+\d+\s*[:\.\)]\s*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // 1) Explicit numbered/bulleted items — anything before the first item is shared preamble.
                var pre = new StringBuilder(); bool sawItem = false;
                foreach (var ln in lines)
                {
                    if (itemRx.IsMatch(ln)) { sawItem = true; steps.Add(itemRx.Replace(ln, "").Trim()); }
                    else if (!sawItem) pre.AppendLine(ln);
                    else if (steps.Count > 0) steps[steps.Count - 1] += " " + ln;   // wrapped continuation of the item above
                }
                if (steps.Count >= 2) { preamble = pre.ToString().Trim(); return steps; }
                steps.Clear();

                // 2) A multi-line dump of bare commands (3+ short lines, Shift+Enter batch) — each line is a step.
                //    A leading line ending in ':' is treated as preamble ("do all of these:").
                if (lines.Count >= 3 && lines.All(l => l.Length <= 250))
                {
                    int start = 0;
                    if (lines[0].EndsWith(":")) { preamble = lines[0]; start = 1; }
                    for (int i = start; i < lines.Count; i++) steps.Add(lines[i]);
                    if (steps.Count >= 3) return steps;
                    steps.Clear(); preamble = "";
                }

                // 3) One long then-chained sentence ("…, then …, then …") — only when it splits into 3+ parts.
                if (lines.Count == 1 && q.Length > 120)
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(q.Trim(), @"(?:;|,|\.)\s+(?:and\s+)?then\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
                    if (parts.Count >= 3) return parts;
                }
            }
            catch { steps.Clear(); preamble = ""; }
            return steps;
        }

        /// <summary>Tool calls executed during the most recent ChatAsync — 0 means the model only TALKED.</summary>
        public static int LastChatToolCalls { get; private set; }

        /// <summary>What the most recent ChatAsync actually CALLED (tool name + args) — ground truth
        /// the planner carries between steps so "the same wall" resolves to a real id.</summary>
        public static readonly List<string> LastChatToolTrace = new List<string>();

        // repetition guard state (see dispatch): consecutive identical tool calls get blocked
        private static string _lastToolSig;
        private static int _toolSigCount;

        /// <summary>True when every id appears somewhere in this conversation (context, a tool
        /// result, or an earlier turn) — fabricated ids (e.g. 123, 456, 789) never do. Hard
        /// anti-fabrication check for id-consuming destructive/selection tools.</summary>
        private static bool IdsSeenInConversation(JArray messages, JArray ids)
        {
            try
            {
                var blob = new StringBuilder();
                foreach (var m in messages) blob.Append((string)m["content"]).Append('\n');
                string text = blob.ToString();
                foreach (var t in ids)
                {
                    string id = t?.ToObject<long>().ToString();
                    if (string.IsNullOrEmpty(id) || !text.Contains(id)) return false;
                }
                return true;
            }
            catch { return true; }   // never block on a guard malfunction
        }

        /// <summary>True when the model's reply PROMISES an action instead of doing it
        /// ("let me fix that", "I'll try again") — the follow-through guard keys on this.</summary>
        private static bool PromisesAction(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.ToLowerInvariant();
            string[] promises =
            {
                "let me fix", "let me try", "let me resolve", "let me correct", "let me attempt",
                "let me investigate", "let me look", "let me find the correct", "let me retry",
                "i'll fix", "i'll try", "i'll retry", "i'll correct", "i'll resolve", "i'll investigate",
                "i will fix", "i will try", "i will retry", "i will correct", "i will resolve",
                "let's try again", "let's correct", "trying again", "and try again", "and attempt",
                "attempt to create the wall again", "let me resolve this"
            };
            return promises.Any(p => t.Contains(p));
        }

        /// <summary>True when a step is an ACTION (must touch the model via tools) rather than a question.</summary>
        private static bool LooksLikeActionStep(string step)
        {
            string s = (step ?? "").ToLowerInvariant();
            string[] verbs = { "create", "add", "place", "make", "build", "draw", "put", "number", "tag", "set ", "rename", "renumber", "move", "copy", "rotate", "array", "delete", "import", "export", "dimension", "duplicate", "apply", "load", "fix", "purge", "select", "find " };
            return verbs.Any(v => s.Contains(v));
        }

        /// <summary>The panel's entry point: batch requests run as a sequence of focused steps;
        /// everything else goes straight to the normal agent loop.</summary>
        public static async Task<string> ChatPlannedAsync(string question, string context, JArray history = null, Action<string> onTool = null)
        {
            List<string> steps; string preamble;
            try { steps = SplitSteps(question, out preamble); }
            catch { steps = new List<string>(); preamble = ""; }
            if (steps.Count < 2) return await ChatAsync(question, context, history, onTool).ConfigureAwait(false);

            int n = steps.Count, completed = 0;
            try { onTool?.Invoke("planned " + n + " steps — working through them one at a time…"); } catch { }
            var localHist = new JArray();
            if (history != null) foreach (var h in history) localHist.Add(h.DeepClone());
            var report = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                int no = i + 1; string step = steps[i];
                string focused =
                    (string.IsNullOrWhiteSpace(preamble) ? "" : "Overall request: " + preamble + "\n") +
                    "This is step " + no + " of " + n + " of the user's request. Do ONLY this step now: " + step +
                    "\nWhen it is done and verified, reply with ONE short confirmation sentence that includes any new element ids.";
                string ans;
                try
                {
                    ans = await ChatAsync(focused, context, localHist,
                        s => { try { onTool?.Invoke("step " + no + "/" + n + " — " + s); } catch { } }).ConfigureAwait(false);
                    // ANTI-FABRICATION GUARD: an action step that ends with ZERO tool calls means the
                    // model only TALKED (it copies look-alike successes from history). Make it act.
                    if (ans != null && LastChatToolCalls == 0 && LooksLikeActionStep(step))
                    {
                        try { onTool?.Invoke("step " + no + "/" + n + " — it answered without acting; insisting it actually does it…"); } catch { }
                        string stern = focused + "\nIMPORTANT: your previous reply was REJECTED because you did not call any tool — you only described a result, which is fabrication. This step requires really ACTING on the model: call the right tool(s) NOW and only then report what the tool results say.";
                        ans = await ChatAsync(stern, context, localHist,
                            s => { try { onTool?.Invoke("step " + no + "/" + n + " — " + s); } catch { } }).ConfigureAwait(false);
                        if (ans != null && LastChatToolCalls == 0)
                            ans = "⚠ not executed — the model failed to call any tool for this step. " + ans;
                    }
                }
                catch { ans = null; }
                if (ans == null)
                {
                    report.AppendLine(no + ". " + step + "\n   → ✗ the model/API stopped responding — halted here.");
                    break;
                }
                completed++;
                string brief = ans.Trim(); if (brief.Length > 700) brief = brief.Substring(0, 700) + "…";
                report.AppendLine(no + ". " + step + "\n   → " + brief.Replace("\n", "\n   "));
                // carry this step's outcome forward so later steps can use the ids it created —
                // including the EXACT tool calls made (the model's own summary often omits the ids
                // it acted ON, e.g. the host wall, and the next step then guesses wrong)
                string carry = brief;
                try { if (LastChatToolTrace.Count > 0) carry += "\n[tools actually called this step: " + string.Join("; ", LastChatToolTrace) + "]"; } catch { }
                localHist.Add(new JObject { ["role"] = "user", ["content"] = step });
                localHist.Add(new JObject { ["role"] = "assistant", ["content"] = carry });
                while (localHist.Count > 12) localHist.RemoveAt(0);
            }
            string head = completed == n ? ("Done — all " + n + " steps completed:") : ("Completed " + completed + " of " + n + " steps:");
            return head + "\n" + report.ToString().TrimEnd();
        }

        public static async Task<string> ChatAsync(string question, string context, JArray history = null, Action<string> onTool = null)
        {
            try
            {
                if (!Enabled) return "The AI assistant is turned off in model_copilot.json (enabled=false). Element reading and quick rule-edits still work without it.";
                if (OverDailyCap()) return CapMessage();
                var tools = new JArray
                {
                    // Curated typed tool — reliable for the most common action (changing a property).
                    ToolDef("set_parameter", "Set a parameter value on an element — e.g. Mark, Comments, Fire Rating, Head Height, Sill Height. Use this to change a property of the selected element (or any element by id). Prefer this for property changes.",
                        new JObject {
                            ["element_id"] = new JObject { ["type"] = "integer", ["description"] = "element id (use the selected element id from the context)" },
                            ["parameter_name"] = SchemaStr("exact Revit parameter name, e.g. 'Mark', 'Comments', 'Fire Rating'"),
                            ["value"] = SchemaStr("the new value")
                        }, new JArray { "element_id", "parameter_name", "value" }),
                    ToolDef("tag_element", "Place a tag on ONE element (the selected element) in the active view. For ALL of a category use tag_all instead.",
                        new JObject {
                            ["element_id"] = new JObject { ["type"] = "integer", ["description"] = "the element id to tag (selected element id from context)" },
                            ["view_id"] = new JObject { ["type"] = "integer", ["description"] = "the active view id from the context" }
                        }, new JArray { "element_id", "view_id" }),
                    ToolDef("tag_all", "Tag EVERY element of a category in the active view (e.g. 'tag all the doors', 'tag all rooms'). Model-wide, NOT just the selected element. Uses the active view id from context.",
                        new JObject {
                            ["category"] = SchemaStr("category to tag, e.g. 'Doors','Rooms','Windows'"),
                            ["view_id"] = new JObject { ["type"] = "integer", ["description"] = "the active view id from the context" }
                        }, new JArray { "category", "view_id" }),
                    ToolDef("select_element", "Select element(s) in Revit so the user SEES them highlighted and zoomed to — use whenever the user says 'select that', 'select the door', 'highlight it', 'show me that element'. Pass the element id(s) from the context.",
                        new JObject { ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "element id(s) to select" } }, new JArray { "element_ids" }),
                    ToolDef("find_cad", "Find the CAD / DWG imports and links in the model (with their element ids). Use when the user wants to select or work with 'the DWG' / a CAD file — then pass the id(s) to select_element. No params.",
                        new JObject { }, new JArray { }),
                    ToolDef("delete_element", "Delete an element from the model (the selected element, by id). This is DESTRUCTIVE — it will be blocked for confirmation.",
                        new JObject { ["element_id"] = new JObject { ["type"] = "integer", ["description"] = "the element id to delete (from context)" } }, new JArray { "element_id" }),
                    ToolDef("remember", "Save a standard, preference, or correction the user taught you, so you follow it in EVERY future session. Use whenever the user says 'remember…', 'always…', 'from now on…', 'never…', or corrects how you did something. Phrase it as a clear durable rule.",
                        new JObject { ["note"] = SchemaStr("the rule/standard to remember, e.g. 'Always dimension to the exterior core-finish face, never centerline.'") }, new JArray { "note" }),
                    ToolDef("analyze_pdf", "READ a PDF drawing set BEFORE importing — finds which pages are floor plans and vision-reads each plan's scale, title, and overall dimensions (so you know what to bring in and at what scale). Use this first when the user wants to import/trace a PDF. Params: path.",
                        new JObject { ["path"] = SchemaStr("full path to the .pdf set") }, new JArray { "path" }),
                    ToolDef("find_detail", "Search the firm's DETAIL LIBRARY for a construction detail (door head, parapet, footing, etc.) — use when the user asks if we have a detail or wants to bring one in. Returns matching detail names + their file paths; then use import_detail. Params: query.",
                        new JObject { ["query"] = SchemaStr("what detail to look for, e.g. 'door head', 'parapet', 'footing'") }, new JArray { "query" }),
                    ToolDef("import_detail", "Bring a detail from the library into the current project as a drafting view. Params: path (the detail's file path from find_detail).",
                        new JObject { ["path"] = SchemaStr("full path to the detail .rvt from find_detail") }, new JArray { "path" }),
                    ToolDef("create_schedule", "Create a schedule WITH fields (door, window, room, wall, etc.). Params: category (e.g. 'Doors', 'Windows', 'Rooms'); name (optional schedule name); fields (optional array of field names — otherwise a sensible default set is added).",
                        new JObject { ["category"] = SchemaStr("category to schedule, e.g. Doors, Windows, Rooms"), ["name"] = SchemaStr("optional schedule name"), ["fields"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "optional field names" } }, new JArray { "category" }),
                    ToolDef("fix_marks", "Find elements with DUPLICATE or BLANK marks and renumber the offenders to unique marks (keeps the first of each). Use for 'find/fix duplicate marks', model cleanup. Params: category (OPTIONAL — Doors/Walls/Rooms/Windows…; OMIT to sweep the WHOLE MODEL, which is what 'any elements / all duplicates' means); prefix (optional).",
                        new JObject { ["category"] = SchemaStr("optional category, e.g. Doors, Walls, Rooms — omit to sweep every category"), ["prefix"] = SchemaStr("optional new-mark prefix") }, new JArray { }),
                    ToolDef("tag_untagged", "Tag all UNTAGGED elements of a category in the active view — for 'tag the untagged rooms/doors', model cleanup. Params: category (default Rooms).",
                        new JObject { ["category"] = SchemaStr("category to tag, e.g. Rooms, Doors") }, new JArray { }),
                    ToolDef("set_marks", "Give elements UNIQUE sequential Mark values (e.g. MC-TEST-001, 002, 003…) — use whenever the user wants numbered/unique marks; NEVER set the same Mark on several elements (Revit warns about duplicates). Params: prefix; element_ids (array) OR category; start (default 1); comments (optional, set on all).",
                        new JObject { ["prefix"] = SchemaStr("mark prefix, e.g. 'MC-TEST-' or 'D-'"), ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, ["category"] = SchemaStr("category instead of ids, e.g. Doors"), ["start"] = new JObject { ["type"] = "integer" }, ["comments"] = SchemaStr("optional Comments to set on all") }, new JArray { "prefix" }),
                    ToolDef("import_pdf", "Import/attach a PDF (e.g. a floor plan) into the project, placed on the active view as an underlay to build on or trace. Params: path (full path to the .pdf); page (optional page number).",
                        new JObject { ["path"] = SchemaStr("full path to the .pdf file"), ["page"] = new JObject { ["type"] = "integer", ["description"] = "optional page number" } }, new JArray { "path" }),
                    ToolDef("import_image", "Import an image (PNG/JPG floor plan or sketch) onto the active view as an underlay. Params: path (full path to the image).",
                        new JObject { ["path"] = SchemaStr("full path to the image file") }, new JArray { "path" }),
                    ToolDef("create_walls", "Build 4 walls forming a rectangular room on the active level — for 'create a room', 'draw walls', 'make a 24x16 room'. Params: width, depth (feet); x, y (origin corner, default 0); height (feet, optional); wall_type (optional).",
                        new JObject {
                            ["width"] = new JObject { ["type"] = "number" }, ["depth"] = new JObject { ["type"] = "number" },
                            ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" },
                            ["height"] = new JObject { ["type"] = "number" }, ["wall_type"] = SchemaStr("optional wall type name")
                        }, new JArray { "width", "depth" }),
                    ToolDef("create_wall", "Create ONE straight wall between two points — for 'create a wall from (x1,y1) to (x2,y2)', 'draw a wall along...'. NOT for rectangular rooms (use create_walls for those). Params: x1, y1, x2, y2 (feet, plan coordinates); height (feet, default 10); level (optional level name, default = lowest level); wall_type (optional).",
                        new JObject {
                            ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" },
                            ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" },
                            ["height"] = new JObject { ["type"] = "number" }, ["level"] = SchemaStr("optional level name"),
                            ["wall_type"] = SchemaStr("optional wall type name")
                        }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("delete_category", "Delete ALL elements of a category, model-wide — for 'delete all the text notes / walls / doors / rooms'. It READS the elements itself (you never pass ids) and asks the user to confirm before anything is deleted. Params: category.",
                        new JObject {
                            ["category"] = SchemaStr("text notes | walls | doors | rooms")
                        }, new JArray { "category" }),
                    ToolDef("delete_elements", "Delete SEVERAL elements by their ids — for 'delete all the X': READ them first (getTextNotes/getWalls/getDoors...), collect the ids, then call this. Destructive: it asks the user to confirm before executing. Params: element_ids (array of integers).",
                        new JObject {
                            ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "real ids read from a tool result — never guessed" }
                        }, new JArray { "element_ids" }),
                    ToolDef("create_floor_plan", "Create a FLOOR PLAN VIEW for a level — for 'create a floor plan (view) for level X'. NOT a floor slab (that is create_floor). Params: level (name or id); view_name (optional).",
                        new JObject {
                            ["level"] = SchemaStr("the level's name (e.g. 'CERT-L2') or id"),
                            ["view_name"] = SchemaStr("optional name for the new view")
                        }, new JArray { "level" }),
                    ToolDef("select_walls_by_length", "Select every wall whose length matches a condition — for 'select the walls longer/shorter than X feet'. Params: min_feet (select walls LONGER than this) and/or max_feet (shorter than this).",
                        new JObject {
                            ["min_feet"] = new JObject { ["type"] = "number", ["description"] = "select walls longer than this (feet)" },
                            ["max_feet"] = new JObject { ["type"] = "number", ["description"] = "select walls shorter than this (feet)" }
                        }, new JArray()),
                    ToolDef("rename_level", "Rename a LEVEL (the datum, not its plan view) — for 'rename level X to Y'. Resolves the level by its current name, so no id needed. Params: level (current name or id); new_name.",
                        new JObject {
                            ["level"] = SchemaStr("the level's CURRENT name (e.g. 'CERT-L2') or its id"),
                            ["new_name"] = SchemaStr("the new name")
                        }, new JArray { "level", "new_name" }),
                    ToolDef("select_elements", "Select specific elements by their ids — use to select a FILTERED SUBSET you computed (e.g. 'select the walls longer than 25 feet': getWalls, filter by length, then select_elements with the matching ids). For a whole category use select_category instead. Params: element_ids (array of integers).",
                        new JObject {
                            ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "ids of the elements to select" }
                        }, new JArray { "element_ids" }),
                    ToolDef("find_wall", "Resolve plan language to a real wall: returns the wall's id, endpoints, length and midpoint. Use a SIDE — 'north/south/east/west' (front=south, back=north, left=west, right=east), 'longest'/'shortest', 'center' (plan center point) — OR pass x, y (feet) to get the wall NEAREST that point ('the wall near (25, 10)'). Use BEFORE add_door/add_window/set_parameter/move when the user names a wall by direction or position. Report the returned length verbatim.",
                        new JObject {
                            ["side"] = SchemaStr("north | south | east | west | front | back | left | right | longest | shortest | center (omit when using x,y)"),
                            ["x"] = new JObject { ["type"] = "number", ["description"] = "point x (feet) — wall nearest this point" },
                            ["y"] = new JObject { ["type"] = "number", ["description"] = "point y (feet)" }
                        }, new JArray()),
                    ToolDef("add_window", "Place a window on a wall — for 'add a window', 'put a window in that wall'. Auto-loads a default window family if none is loaded. Params: wall_id; position (0-1 along the wall, default 0.5); sill_height (feet, optional); window_type (optional name).",
                        new JObject {
                            ["wall_id"] = new JObject { ["type"] = "integer", ["description"] = "the wall id to host the window" },
                            ["position"] = new JObject { ["type"] = "number", ["description"] = "0-1 along the wall, default 0.5" },
                            ["sill_height"] = new JObject { ["type"] = "number" },
                            ["window_type"] = SchemaStr("optional window type name")
                        }, new JArray { "wall_id" }),
                    ToolDef("create_sheet", "Create a drawing sheet. Params: number (e.g. 'A-101'); name (e.g. 'First Floor Plan').",
                        new JObject { ["number"] = SchemaStr("sheet number, e.g. A-101"), ["name"] = SchemaStr("sheet name") }, new JArray { "number" }),
                    ToolDef("place_view", "Place a view onto a sheet. Params: sheet_id; view_id.",
                        new JObject { ["sheet_id"] = new JObject { ["type"] = "integer" }, ["view_id"] = new JObject { ["type"] = "integer" } }, new JArray { "sheet_id", "view_id" }),
                    ToolDef("undo", "Undo the last change you made to the model — use when the user says 'undo that', 'undo', 'revert that'. No params.",
                        new JObject { }, new JArray { }),
                    ToolDef("create_3d_view", "Create a 3D view. Params: name (optional); perspective (true for camera, default false).",
                        new JObject { ["name"] = SchemaStr("view name"), ["perspective"] = new JObject { ["type"] = "boolean" } }, new JArray { }),
                    ToolDef("create_section", "Create a section view cutting along a line. Params: x1, y1, x2, y2 (the cut line, feet); name (optional).",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" }, ["name"] = SchemaStr("view name") }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("create_elevation", "Create an elevation view at a point facing a direction. Params: x, y (marker location, feet); direction (north/south/east/west); name (optional).",
                        new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["direction"] = SchemaStr("north/south/east/west"), ["name"] = SchemaStr("view name") }, new JArray { "x", "y", "direction" }),
                    ToolDef("create_callout", "Create a callout (detail) of a region in the active view. Params: x1, y1, x2, y2 (the region box, feet).",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("duplicate_view", "Duplicate the active view (or a given view). Params: view_id (optional, default active); with_detailing (true to copy annotations); name (optional).",
                        new JObject { ["view_id"] = new JObject { ["type"] = "integer" }, ["with_detailing"] = new JObject { ["type"] = "boolean" }, ["name"] = SchemaStr("new view name") }, new JArray { }),
                    ToolDef("set_view_scale", "Set the scale of the active view (or a given view). Params: scale (the denominator, e.g. 48 for 1/4\"=1'-0\"); view_id (optional).",
                        new JObject { ["scale"] = new JObject { ["type"] = "integer" }, ["view_id"] = new JObject { ["type"] = "integer" } }, new JArray { "scale" }),
                    ToolDef("apply_view_template", "Apply a view template (by name) to the active view (or a given view). Params: template (template name); view_id (optional).",
                        new JObject { ["template"] = SchemaStr("view template name"), ["view_id"] = new JObject { ["type"] = "integer" } }, new JArray { "template" }),
                    ToolDef("place_keynote", "Place a keynote tag on an element in the active view. Params: element_id; x, y (leader/tag location, feet).",
                        new JObject { ["element_id"] = new JObject { ["type"] = "integer" }, ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } }, new JArray { "element_id" }),
                    ToolDef("create_revision_cloud", "Draw a revision cloud around a rectangular region in the active view. Params: x1, y1, x2, y2 (the region box, feet).",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("place_spot_elevation", "Place a spot elevation on an element in the active view. Params: element_id; x, y (location, feet).",
                        new JObject { ["element_id"] = new JObject { ["type"] = "integer" }, ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } }, new JArray { "element_id", "x", "y" }),
                    ToolDef("create_room", "Place a room at a point on the active level ('add a room', 'place a room here'). Params: x, y (feet); name; number.",
                        new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["name"] = SchemaStr("room name"), ["number"] = SchemaStr("room number") }, new JArray { "x", "y" }),
                    ToolDef("create_floor", "Create a rectangular floor on the active level. Params: width, depth (feet); x, y (origin corner).",
                        new JObject { ["width"] = new JObject { ["type"] = "number" }, ["depth"] = new JObject { ["type"] = "number" }, ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } }, new JArray { "width", "depth" }),
                    ToolDef("create_ceiling", "Create a rectangular ceiling on the active level. Params: width, depth (feet); x, y (origin); height (above level, feet, default 9).",
                        new JObject { ["width"] = new JObject { ["type"] = "number" }, ["depth"] = new JObject { ["type"] = "number" }, ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["height"] = new JObject { ["type"] = "number" } }, new JArray { "width", "depth" }),
                    ToolDef("create_roof", "Create a rectangular (flat footprint) roof over the active level. Params: width, depth (feet); x, y (origin); offset (feet above the level, default 10 = on top of standard walls).",
                        new JObject { ["width"] = new JObject { ["type"] = "number" }, ["depth"] = new JObject { ["type"] = "number" }, ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["offset"] = new JObject { ["type"] = "number" } }, new JArray { "width", "depth" }),
                    ToolDef("create_curtain_wall", "Create a curtain wall along a line on the active level. Params: x1, y1, x2, y2 (endpoints, feet); height (feet, default 10).",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" }, ["height"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("place_column", "Place a structural column at a point on the active level. Params: x, y (feet). Auto-loads a column family if needed.",
                        new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } }, new JArray { "x", "y" }),
                    ToolDef("create_beam", "Create a structural beam along a line on the active level. Params: x1, y1, x2, y2 (feet). Auto-loads a framing family if needed.",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("create_stair", "Create a straight stair from the active level up to the next level. Params: x, y (run start, feet); direction (north/south/east/west); width (feet, default 3.5).",
                        new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["direction"] = SchemaStr("north/south/east/west"), ["width"] = new JObject { ["type"] = "number" } }, new JArray { "x", "y" }),
                    ToolDef("create_duct", "Create a duct between two 3D points (needs an MEP project). Params: x1,y1,z1, x2,y2,z2 (feet).",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["z1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" }, ["z2"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("create_pipe", "Create a pipe between two 3D points (needs an MEP project). Params: x1,y1,z1, x2,y2,z2 (feet).",
                        new JObject { ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["z1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" }, ["z2"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("place_fixture", "Place a plumbing or lighting fixture at a point on the active level. Params: x, y (feet); kind ('plumbing' or 'lighting'). Auto-loads a fixture family if needed.",
                        new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["kind"] = SchemaStr("plumbing or lighting") }, new JArray { "x", "y" }),
                    ToolDef("create_level", "Create a level. Params: name; elevation (feet).",
                        new JObject { ["name"] = SchemaStr("level name"), ["elevation"] = new JObject { ["type"] = "number" } }, new JArray { "name", "elevation" }),
                    ToolDef("create_grid", "Create a grid line. Params: name; x1, y1, x2, y2 (endpoints, feet).",
                        new JObject { ["name"] = SchemaStr("grid name e.g. A or 1"), ["x1"] = new JObject { ["type"] = "number" }, ["y1"] = new JObject { ["type"] = "number" }, ["x2"] = new JObject { ["type"] = "number" }, ["y2"] = new JObject { ["type"] = "number" } }, new JArray { "x1", "y1", "x2", "y2" }),
                    ToolDef("move_element", "Move an element by an offset. Params: element_id; dx, dy (feet).",
                        new JObject { ["element_id"] = new JObject { ["type"] = "integer" }, ["dx"] = new JObject { ["type"] = "number" }, ["dy"] = new JObject { ["type"] = "number" } }, new JArray { "element_id" }),
                    ToolDef("copy_element", "Copy element(s) by an offset. Params: element_ids (array); dx, dy (feet).",
                        new JObject { ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, ["dx"] = new JObject { ["type"] = "number" }, ["dy"] = new JObject { ["type"] = "number" } }, new JArray { "element_ids" }),
                    ToolDef("rotate_element", "Rotate element(s) around a pivot point. Params: element_ids; x, y (pivot); angle (degrees).",
                        new JObject { ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["angle"] = new JObject { ["type"] = "number" } }, new JArray { "element_ids", "angle" }),
                    ToolDef("array_element", "Make a linear array of element(s). Params: element_ids; count; dx, dy (spacing, feet).",
                        new JObject { ["element_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, ["count"] = new JObject { ["type"] = "integer" }, ["dx"] = new JObject { ["type"] = "number" }, ["dy"] = new JObject { ["type"] = "number" } }, new JArray { "element_ids", "count" }),
                    ToolDef("place_text", "Place a text note in the active view. Params: text; x, y (feet).",
                        new JObject { ["text"] = SchemaStr("the note text"), ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } }, new JArray { "text", "x", "y" }),
                    ToolDef("name_room", "Set a room's name and/or number. Params: room_id; name; number.",
                        new JObject { ["room_id"] = new JObject { ["type"] = "integer" }, ["name"] = SchemaStr("room name"), ["number"] = SchemaStr("room number") }, new JArray { "room_id" }),
                    ToolDef("renumber_rooms", "Renumber ALL rooms sequentially. Params: prefix (optional); start (default 1).",
                        new JObject { ["prefix"] = SchemaStr("optional number prefix"), ["start"] = new JObject { ["type"] = "integer" } }, new JArray { }),
                    ToolDef("select_category", "Select ALL elements of a category ('select all the doors', 'select every wall'). Params: category.",
                        new JObject { ["category"] = SchemaStr("category, e.g. Doors, Walls, Rooms, Furniture") }, new JArray { "category" }),
                    ToolDef("dimension_walls", "Dimension walls to their EXTERIOR core-finish FACE (the firm standard — NEVER centerline) in the active view. Params: wall_ids (optional array; defaults to all walls in view); direction (horizontal or vertical).",
                        new JObject { ["wall_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, ["direction"] = SchemaStr("horizontal or vertical") }, new JArray { }),
                    ToolDef("add_door", "Place a door on a wall — for 'add a door', 'put a door in that wall'. Auto-loads a default door family if none is loaded. Params: wall_id; position (0-1 along the wall, default 0.5 = middle); door_type (optional name).",
                        new JObject {
                            ["wall_id"] = new JObject { ["type"] = "integer", ["description"] = "the wall id to host the door" },
                            ["position"] = new JObject { ["type"] = "number", ["description"] = "0-1 along the wall, default 0.5" },
                            ["door_type"] = SchemaStr("optional door type name")
                        }, new JArray { "wall_id" }),
                    ToolDef("set_text", "Replace the TEXT of a text note (rewrite/edit a note's wording). Use for 'rewrite this note', 'change the note text', 'fix the wording'. element_id = the selected text note's id; the current text is in the context as TEXT CONTENT.",
                        new JObject { ["element_id"] = new JObject { ["type"] = "integer", ["description"] = "the text note element id from context" }, ["text"] = SchemaStr("the new full text for the note") }, new JArray { "element_id", "text" }),
                    // Coordination / QC tools (read-only, no required params -> reliable).
                    ToolDef("run_clash_check", "Run a comprehensive multi-discipline clash/coordination check across the whole model (ducts vs structure, pipes vs structure, etc.). Returns clashes found. Read-only.", new JObject(), new JArray()),
                    ToolDef("get_warnings", "Get all active Revit warnings/errors in the model (its internal conflicts), with the elements involved. Read-only.", new JObject(), new JArray()),
                    ToolDef("quality_check", "Run a construction-document quality check on the whole model — scores doc health 0-100 and finds empty sheets, untagged elements, missing dimensions, warnings. Read-only.", new JObject(), new JArray()),
                    ToolDef("audit_coordination", "Audit coordination — find orphaned views (sections/details not placed on sheets) and placement issues. Read-only.", new JObject(), new JArray()),
                    ToolDef("set_param_all", "Set a parameter to a value on EVERY element of a category (model-wide) — e.g. 'set Manufacturer to Lithonia on all lighting fixtures', 'set Comments to reviewed on all doors'. Handles type params (Manufacturer/Model) automatically. Use for ANY 'set/give X to all <category>'.",
                        new JObject {
                            ["category"] = SchemaStr("category, e.g. 'Doors','Lighting Fixtures','Rooms'"),
                            ["parameter_name"] = SchemaStr("exact parameter name, e.g. 'Mark','Manufacturer','Model','Comments','Fire Rating'"),
                            ["value"] = SchemaStr("the value to set")
                        }, new JArray { "category", "parameter_name", "value" }),
                    ToolDef("auto_mark", "Assign sequential Mark numbers to a whole category — e.g. 'number all the doors D-1, D-2', 'mark the lighting fixtures'. By default only fills BLANK marks (leaves existing ones).",
                        new JObject {
                            ["category"] = SchemaStr("category, e.g. 'Doors','Lighting Fixtures','Windows'"),
                            ["prefix"] = SchemaStr("optional prefix, e.g. 'D-' (default none)"),
                            ["start"] = new JObject { ["type"] = "integer", ["description"] = "optional starting number (default 1)" }
                        }, new JArray { "category" }),
                    ToolDef("coordinate_model", "Whole-model completeness audit across EVERY category present — doors, windows, rooms, equipment, plumbing/electrical/lighting fixtures, furniture. Reports counts and what key fields are MISSING (Mark, Manufacturer, Model, Fire Rating, Number, unplaced rooms). Skips categories not in the model. Use for 'coordinate everything / what's missing / coordinate doors/rooms/equipment/fixtures'. Read-only.", new JObject(), new JArray()),
                    ToolDef("purge_unused", "Report or remove UNUSED elements (unused view types, line patterns, materials) to clean up the model. apply=false (default) only REPORTS what's purgeable; apply=true actually purges. Use for 'what can I purge / clean up the model'.",
                        new JObject { ["apply"] = new JObject { ["type"] = "boolean", ["description"] = "false = report only (default), true = actually purge" } }, new JArray()),
                    ToolDef("find_duplicates", "Find overlapping/duplicate elements in a category (e.g. 'are there duplicate walls?', stacked elements at the same spot). Read-only.",
                        new JObject { ["category"] = SchemaStr("category, e.g. 'Walls','Doors','Rooms'") }, new JArray { "category" }),
                    ToolDef("run_code_check", "Run a building-code / compliance check on the model (Florida Building Code by default) — corridor/door widths, room areas, clearances, ceiling heights, fire ratings, etc. Use for 'check code / compliance / does this meet code / FBC'. Read-only.", new JObject(), new JArray()),
                    ToolDef("find_types", "Find available element TYPES by category (and optional name text), returning their ids — use this BEFORE change_type to get the target type id.",
                        new JObject { ["category"] = SchemaStr("category name, e.g. 'Doors','Windows','Walls','Lighting Fixtures'"), ["contains"] = SchemaStr("optional text to match in the type name, e.g. \"3'-0\"") }, new JArray { "category" }),
                    ToolDef("change_type", "Change TYPE to a different type id (from find_types). ONE element: pass element_id (the INSTANCE id from context — never the type id). ALL of a category ('change all the doors to X'): pass category instead of element_id.",
                        new JObject { ["element_id"] = new JObject { ["type"] = "integer", ["description"] = "the element INSTANCE id from context (not a type id)" }, ["category"] = SchemaStr("instead of element_id: apply to ALL of this category, e.g. 'Doors'"), ["type_id"] = new JObject { ["type"] = "integer", ["description"] = "target type id from find_types" } }, new JArray { "type_id" }),
                    ToolDef("export_sheets_pdf", "Export sheets to a PDF on the Desktop (e.g. 'export the cover sheet to PDF', 'export all the sheets'). sheets='all' or a sheet number/name keyword.",
                        new JObject { ["sheets"] = SchemaStr("'all' or a sheet number/name keyword, e.g. 'Cover','A-101'") }, new JArray { "sheets" }),
                    ToolDef("export_schedule", "Export a schedule to Excel/CSV on the Desktop (e.g. 'give me the door schedule', 'export the room finish schedule'). Provide the schedule name (matched loosely).",
                        new JObject { ["schedule_name"] = SchemaStr("schedule name or keyword, e.g. 'Door','Room Finish','Window'") }, new JArray { "schedule_name" }),
                    ToolDef("write_report", "Write a report or data to a file on the Desktop and open it (for 'give me a PDF / Word doc / Excel / file of this'). Provide format (pdf|word|excel|csv|html|txt), a title, and content (the full report text; for excel/csv make content comma-separated rows, one per line with a header row).",
                        new JObject {
                            ["format"] = SchemaStr("pdf | word | excel | csv | html | txt"),
                            ["title"] = SchemaStr("short report title (also the file name)"),
                            ["content"] = SchemaStr("the full report text to write; for excel/csv use comma-separated rows")
                        }, new JArray { "format", "title", "content" }),
                    // Escape hatch — 100% reach over all bridge methods (model must supply correct params).
                    ToolDef("find_method", "Search the Revit bridge for operations (methods) by keyword when no curated tool fits. Returns method names + descriptions.",
                        new JObject { ["query"] = SchemaStr("keywords, e.g. 'change door type' or 'tag room'") }, new JArray { "query" }),
                    ToolDef("run_method", "Execute any Revit bridge method by exact name with JSON params. Use AFTER find_method for operations not covered by a curated tool.",
                        new JObject {
                            ["method"] = SchemaStr("exact method name from find_method"),
                            ["params_json"] = SchemaStr("JSON object string of params, e.g. {\"elementId\":123,\"typeId\":456}")
                        }, new JArray { "method", "params_json" }),
                    ToolDef("search_files", "Search the user's folders/computer for files by name — e.g. find a spec, a PDF, a drawing, a schedule. Params: query (filename keyword), path (optional folder to search; defaults to the projects folder). Returns matching file paths.",
                        new JObject { ["query"] = SchemaStr("filename keyword to look for"), ["path"] = SchemaStr("optional folder to search in") }, new JArray { "query" }),
                    ToolDef("read_file", "Read a text file's contents by full path (txt, md, csv, json, log, etc.) — e.g. a spec, notes, or a schedule export you found with search_files. Params: path.",
                        new JObject { ["path"] = SchemaStr("full file path") }, new JArray { "path" }),
                    ToolDef("web_search", "Search the WEB for current/external information — building codes, product specs, manufacturers, how-tos, anything not in the model. Params: query. Returns top results (title, snippet, link).",
                        new JObject { ["query"] = SchemaStr("what to search the web for") }, new JArray { "query" }),
                    ToolDef("load_family", "Find a Revit family on the computer (your D: family libraries + the Revit library) by keyword and load it into the project — e.g. 'chair', 'single door', 'toilet'. Use when the user needs a family/component they don't have loaded. Params: search_term, category (optional).",
                        new JObject { ["search_term"] = SchemaStr("what family to find, e.g. 'office chair'"), ["category"] = SchemaStr("optional Revit category") }, new JArray { "search_term" }),
                    ToolDef("load_family_file", "Load a Revit family (.rfa) from a file path on the computer into the project — e.g. one found with search_files or downloaded. Params: path.",
                        new JObject { ["path"] = SchemaStr("full path to the .rfa file") }, new JArray { "path" }),
                    ToolDef("download_family", "Download a Revit family (.rfa) from a direct web URL (e.g. a link found via web_search) and load it into the project. Params: url, name (optional file name).",
                        new JObject { ["url"] = SchemaStr("direct URL to an .rfa file"), ["name"] = SchemaStr("optional file name") }, new JArray { "url" }),
                    ToolDef("place_family", "Place a loaded family into the model at a point. Get family_type_id from load_family's result (its 'types'). Params: family_type_id, x, y (feet, Revit coordinates), z (optional, default 0), rotation (optional degrees). To place near an element, first read that element's location (bounding-box center).",
                        new JObject {
                            ["family_type_id"] = new JObject { ["type"] = "integer", ["description"] = "the family type id to place" },
                            ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" },
                            ["z"] = new JObject { ["type"] = "number", ["description"] = "optional, default 0" },
                            ["rotation"] = new JObject { ["type"] = "number", ["description"] = "optional degrees" }
                        }, new JArray { "family_type_id", "x", "y" })
                };

                var messages = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = StandardsBlock() + KnowledgeBlock() + RecipesBlock() + context +
                        "\n\nYou have access to the ENTIRE Revit bridge — about 1200 operations — through two universal tools: find_method (search operations by keyword) and run_method (execute ANY operation by exact name with JSON params). The other tools listed are just fast shortcuts for the most common tasks; they are NOT the limit of what you can do. " +
                        "CRITICAL RULE: if no shortcut tool clearly fits the request, you MUST call find_method with good keywords to discover the right operation, then run_method to perform it. NEVER tell the user 'I can't', 'no function fits', or 'that's not available' WITHOUT first calling find_method to look — searching is cheap, giving up is wrong. " +
                        "If run_method returns an error naming required parameters (e.g. 'elementId, value are required'), read them and call run_method again with those parameters filled from the context. You can read AND modify almost anything this way — materials, views, sheets, families, parameters, geometry, schedules — the whole model. " +
                        "To change/add/tag/modify something: use a shortcut tool if one fits (set_parameter, tag_element, change_type, set_text, set_param_all...); otherwise find_method then run_method (pass element ids from the context). " +
                        "IMPORTANT — MODEL-WIDE vs SELECTED: 'all/every/the <category>' means model-wide, use the ALL tool: tag_all to tag a category, set_param_all to set a parameter on a whole category (e.g. 'set Manufacturer to X on all lighting fixtures'). Use the single-element tools (set_parameter, tag_element) ONLY when the user means the one selected element. " +
                        "To BRING IN A FLOOR PLAN from a PDF: if it's a multi-page drawing SET, FIRST call analyze_pdf(path) to find which page is the right floor plan and read its scale + dimensions; THEN import_pdf(path, page) brings that page in. For a single image use import_image(path). It attaches onto the active view as an underlay to trace/build on (find the file with search_files first if needed). After it's in, you can LOOK at it and trace it with create_walls / add_door. " +
                        "To BUILD: create_walls(width, depth) makes a rectangular room of walls; create_wall(x1,y1,x2,y2) makes ONE straight wall between two points; add_door(wall_id, position) puts a door in a wall; add_window(wall_id, position) puts a window in a wall (both auto-load a family if needed — get wall ids from getWalls/create_walls' result). " +
                        "PLAN DIRECTIONS: when the user names a wall by direction or position ('the north wall', 'the front wall', 'the longest wall') call find_wall(side) FIRST — it returns the real wall id + endpoints + midpoint; for 'the middle/center of the room' call find_wall('center') for the coordinates. Never guess which wall id is which direction. " +
                        "SHEETS: create_sheet(number, name) makes a drawing sheet; place_view(sheet_id, view_id) puts a view on it (get the sheet id from create_sheet's result, view ids from getAllViews/getActiveView). " +
                        "VIEWS: create_3d_view(); create_section(x1,y1,x2,y2) cuts a section along a line; create_elevation(x,y,direction); create_callout(x1,y1,x2,y2) of a region; duplicate_view(); set_view_scale(scale); apply_view_template(template). " +
                        "MORE ANNOTATION: place_keynote(element_id); create_revision_cloud(x1,y1,x2,y2); place_spot_elevation(element_id,x,y). " +
                        "UNDO: if the user says 'undo that' / 'revert', call undo. " +
                        "MORE BUILDING: create_room(x,y) places a room; create_floor(width,depth) a floor; create_ceiling(width,depth) a ceiling; create_roof(width,depth) a roof; create_level(name,elevation); create_grid(name,x1,y1,x2,y2). EDIT GEOMETRY: move_element(element_id,dx,dy); copy_element(element_ids,dx,dy); rotate_element(element_ids,x,y,angle); array_element(element_ids,count,dx,dy). ROOMS: name_room(room_id,name,number); renumber_rooms(prefix). place_text(text,x,y) adds a note in the active view. select_category(category) selects all of a category (e.g. 'select all the doors'); select_elements(element_ids) selects a specific filtered subset (read ids first, filter, then select). " +
                        "DIMENSIONS: dimension_walls dimensions walls to their EXTERIOR core-finish face — the firm standard, NEVER the centerline. " +
                        "STRUCTURAL: create_curtain_wall(x1,y1,x2,y2); place_column(x,y); create_beam(x1,y1,x2,y2); create_stair(x,y,direction). MEP (needs an MEP project): create_duct(x1,y1,z1,x2,y2,z2); create_pipe(...); place_fixture(x,y,kind). " +
                        "DETAILS & SCHEDULES: when the user asks whether we have a detail, or to bring one in ('do we have a door-head detail?', 'add my parapet detail'), call find_detail(query) to search the firm's detail library, then import_detail(path) to bring the chosen one in as a drafting view. To make a schedule ('create a door schedule'), call create_schedule(category) — it adds sensible fields automatically. " +
                        "MARKS: for UNIQUE or sequential marks (the user wants elements numbered, e.g. 'mark them MC-TEST-001, 002, 003' or 'number the doors D-1, D-2'), use set_marks(prefix, element_ids or category). Do NOT use set_param_all for Mark — that puts the SAME value on every element and Revit warns about duplicate marks. " +
                        "MODEL CLEANUP / FIX: to find and fix duplicate or blank marks use fix_marks — with NO category it sweeps the WHOLE model (use that for 'any elements'/'all duplicates'); pass a category only to limit it. To tag elements that have no tag use tag_untagged(category). When the user asks you to 'assess and fix' or 'clean up' a model, first read the issues (get_warnings, find_duplicates, the untagged count) then fix what's safe (tag_untagged, fix_marks) and report before/after. " +
                        "To SELECT or highlight an element for the user ('select that', 'select the door', 'show me it', 'highlight it'): call select_element with the element id(s) from the context — it selects them in Revit and zooms to them so the user can see them. To select a DWG / CAD import ('select the DWG', 'select the CAD'): first call find_cad to get the import id(s), then select_element with them. " +
                        "To change an element's TYPE: call find_types(category, name text) to get the target type id, then change_type(element_id, type_id). " +
                        "For CODE / COMPLIANCE / 'does this meet code' / FBC / building code: call run_code_check. " +
                        "To rewrite/edit a TEXT NOTE's wording, use set_text(element_id, text) — the note's current text is in the context as TEXT CONTENT; read it, rewrite it, and call set_text with the new full text. " +
                        "After acting, confirm in ONE short sentence what you did. " +
                        "If the user asks to COORDINATE EVERYTHING / what's MISSING / coordinate doors/rooms/spaces/uses/equipment/plumbing/electrical/lighting fixtures: call coordinate_model and report PER CATEGORY what's present and what's missing (skip categories not in the model). " +
                        "If the user asks for a CLASH check, model HEALTH, WARNINGS, or QC: call run_clash_check, get_warnings, and quality_check. " +
                        "Synthesize results into a SHORT report grouped Critical / Major / Minor with a couple of concrete next steps. " +
                        "If the user wants a PDF / Word / Excel / file of a report: first gather the data (e.g. coordinate_model), then call write_report with the format, a title, and the full report as content (for excel/csv make the content comma-separated rows). " +
                        "BEYOND THE MODEL: to find a file on the computer use search_files; to read a file's text use read_file; for current or external information NOT in the model (building codes, product specs, manufacturers, how-tos) use web_search. " +
                        "FAMILIES / COMPONENTS: when the user needs a Revit family or component they don't have (a chair, a specific door, a fixture, equipment…), proactively get it — use load_family to find it in the user's local libraries (fast, reliable); or download_family with a direct .rfa URL you found via web_search; or load_family_file for a local .rfa found via search_files. If nothing is found locally, do NOT try to open Autodesk's cloud dialog yourself — instead tell the user to click Revit's 'Load Autodesk Family' button (Insert tab) to pick from the cloud, then you can place it. After loading, use place_family(family_type_id, x, y) to drop it into the model (get the type id from load_family's result; to place near an element, read its bounding-box center first). You are aware of the user's ACTIVE VIEW and current SELECTION (in the context) — use that to assist with what they're working on right now. " +
                        "SELF-CHECK: after each build/edit tool, the result carries a 'VERIFY' note — the live model was READ BACK in code to confirm the elements really exist (or what a parameter now actually holds). TRUST the VERIFY note over the method's own message: if it says VERIFY FAILED / an element is missing, that step did NOT happen — retry a different way or tell the user precisely what failed; NEVER report success. " +
                        "REVIT POPUPS: while you act, Revit warning/message dialogs are sensed and answered for you automatically (the safe button), so they never block you. When a tool result ends with 'NOTE — Revit popups handled during this step: …', READ it — a dialog appeared and was handled; if it matters to the user (e.g. a warning about duplicates, unresolved references, or skipped elements), tell them what it said and what was done. " +
                        "For plain questions, answer directly from the facts — no tools. Never invent ids or values. " +
                        "\n\nWORK LIKE A REVIT EXPERT WHO FINISHES THE JOB: for any multi-step request, first think briefly about the BEST way to do it (there is often more than one approach in Revit — choose the cleanest, most standard one), then carry it out step by step with tools, checking the result of each step before the next. Do NOT stop after a single step if the goal isn't met — keep going: gather, act, verify, then continue until the user's actual goal is achieved, or you are genuinely blocked (then say precisely what you need). Prefer completing the whole job over asking permission for each small step — destructive actions (delete/purge) are confirmed separately, so you don't need to ask about those either. When useful, briefly say your plan before doing it. " +
                        "LEARN THE FIRM'S WAY: when the user teaches you a rule or corrects how you did something ('always…', 'never…', 'remember…', 'from now on…', 'do it this way'), call remember(note) to save it as a durable rule — it will then guide you in every future session. The standards block at the very top of this prompt is exactly those saved rules; treat them as binding." }
                };
                if (history != null) foreach (var h in history) messages.Add(h.DeepClone());   // prior turns for follow-ups
                messages.Add(new JObject { ["role"] = "user", ["content"] = question });

                tools = SelectTools(tools, question);   // TIERING: expose CORE + intent-matched tool groups only — keeps the prompt focused (escape hatch stays in core)

                LastChatToolCalls = 0;   // lets the planner detect a turn that claimed success without ACTING
                LastChatToolTrace.Clear();   // code-owned record of what was actually called (planner carries it between steps)
                _lastToolSig = null; _toolSigCount = 0;
                int followThroughNudges = 0;
                for (int turn = 0; turn < 24; turn++)   // deeper agentic loop — plan, act, verify, continue (raised for batch/multi-task prompts)
                {
                    var msg = await PostChatAsync(OllamaModel, messages, tools, false).ConfigureAwait(false);
                    if (msg == null) return null;   // unreachable / API error -> caller shows fallback

                    var toolCalls = msg["tool_calls"] as JArray;
                    if (toolCalls == null || toolCalls.Count == 0)
                    {
                        string content = StripThink((string)msg["content"]);
                        // FOLLOW-THROUGH GUARD (code-enforced): a turn must not END on a promise
                        // ("let me fix that and try again"), on empty text, or — for an ACTION
                        // request — without a single tool call (silent fabrication). Push it to
                        // actually act, up to 2 nudges, always re-anchored to the ORIGINAL request.
                        bool isQuestionOnly = System.Text.RegularExpressions.Regex.IsMatch(
                            (question ?? "").TrimStart(), @"^(how|what|why|when|where|which|who|can|could|does|do|is|are|tell|explain|list|describe)\b",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        bool claimedWithoutActing = LastChatToolCalls == 0 && !isQuestionOnly && LooksLikeActionStep(question);
                        if (followThroughNudges < 2 && (string.IsNullOrWhiteSpace(content) || PromisesAction(content) || claimedWithoutActing))
                        {
                            followThroughNudges++;
                            try { onTool?.Invoke("following through…"); } catch { }
                            messages.Add(new JObject { ["role"] = "assistant", ["content"] = content ?? "" });
                            messages.Add(new JObject { ["role"] = "user", ["content"] =
                                "STOP — nothing is done until tools actually run. The request is: \"" + question + "\" — " +
                                "accomplish EXACTLY that now with tool calls in this turn; do not do something else, do not describe, do not promise. " +
                                "If a previous attempt errored, READ the error, change the method or arguments, and retry " +
                                "(find_method can locate the right method). Reply in text only when the action is verified done, " +
                                "or you are truly blocked — then state exactly what failed." } );
                            continue;
                        }
                        return content;
                    }
                    LastChatToolCalls += toolCalls.Count;

                    messages.Add(msg);   // keep the assistant's tool-call turn in the history
                    foreach (var tc in toolCalls)
                    {
                        var fn = tc["function"] as JObject;
                        string name = (string)fn?["name"];
                        var args = fn?["arguments"];
                        try { onTool?.Invoke(Friendly(name)); } catch { }
                        try
                        {
                            if (LastChatToolTrace.Count < 12)
                            {
                                string a = args == null ? "" : (args.Type == JTokenType.String ? (string)args : args.ToString(Formatting.None));
                                if (a != null && a.Length > 140) a = a.Substring(0, 140) + "…";
                                LastChatToolTrace.Add(name + " " + a);
                            }
                        }
                        catch { }
                        string result;
                        // REPETITION GUARD: a model stuck on one idea repeats the identical call
                        // (observed: 20 identical create_floor calls chasing 'floor plan') — block
                        // the 3rd+ identical consecutive call with corrective feedback instead.
                        string callSig = name + "|" + (args == null ? "" : args.ToString(Formatting.None));
                        if (callSig == _lastToolSig) _toolSigCount++; else { _lastToolSig = callSig; _toolSigCount = 1; }
                        if (_toolSigCount >= 3)
                        {
                            result = "{\"success\":false,\"error\":\"STOP — this is the exact same call for the " + _toolSigCount +
                                     "th time and it will not be executed again. The earlier result stands. Re-read the request and use a DIFFERENT tool or different arguments, or report honestly what happened.\"}";
                        }
                        else if (name == "set_parameter")
                        {
                            var p = new JObject
                            {
                                ["elementId"] = ArgToken(args, "element_id"),
                                ["parameterName"] = ArgStr(args, "parameter_name"),
                                ["value"] = ArgToken(args, "value")
                            };
                            result = RunMethod("setParameterValue", p.ToString(Formatting.None));   // registry name is setParameterValue
                        }
                        else if (name == "tag_element")
                        {
                            var p = new JObject { ["elementId"] = ArgToken(args, "element_id"), ["viewId"] = ArgToken(args, "view_id") };
                            result = RunMethod("tagElement", p.ToString(Formatting.None));
                        }
                        else if (name == "run_clash_check") result = RunMethod("validateCoordination", "{}");
                        else if (name == "get_warnings") result = RunMethod("getModelWarnings", "{}");
                        else if (name == "quality_check") result = RunMethod("runCDQualityCheck", "{}");
                        else if (name == "audit_coordination") result = RunMethod("auditCoordination", "{}");
                        else if (name == "set_param_all")
                        {
                            var p = new JObject { ["category"] = ArgStr(args, "category"), ["parameterName"] = ArgStr(args, "parameter_name"), ["value"] = ArgStr(args, "value") };
                            result = RunMethod("setParameterByCategory", p.ToString(Formatting.None));
                        }
                        else if (name == "auto_mark")
                        {
                            var p = new JObject { ["category"] = ArgStr(args, "category"), ["prefix"] = ArgStr(args, "prefix"), ["start"] = ArgToken(args, "start") };
                            result = RunMethod("autoMarkByCategory", p.ToString(Formatting.None));
                        }
                        else if (name == "coordinate_model") result = RunMethod("coordinateModel", "{}");
                        else if (name == "purge_unused")
                        {
                            var a = ArgToken(args, "apply");
                            bool apply = a != null && (a.Type == JTokenType.Boolean ? (bool)a : a.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
                            result = RunMethod("purgeUnused", new JObject { ["dryRun"] = !apply }.ToString(Formatting.None));
                        }
                        else if (name == "find_duplicates")
                        {
                            var p = new JObject { ["category"] = ArgStr(args, "category") };
                            result = RunMethod("findDuplicates", p.ToString(Formatting.None));
                        }
                        else if (name == "run_code_check") result = RunMethod("runComplianceCheck", "{}");
                        else if (name == "find_types")
                        {
                            var p = new JObject { ["category"] = ArgStr(args, "category"), ["contains"] = ArgStr(args, "contains") };
                            result = RunMethod("findTypes", p.ToString(Formatting.None));
                        }
                        else if (name == "change_type")
                        {
                            var catArg = ArgStr(args, "category");
                            if (!string.IsNullOrWhiteSpace(catArg))
                            {
                                var p = new JObject { ["category"] = catArg };
                                var ty = ArgToken(args, "type_id"); if (ty != null) p["typeId"] = ty.DeepClone();
                                result = RunMethod("changeTypeByCategory", p.ToString(Formatting.None));
                            }
                            else
                            {
                                var p = new JObject { ["elementId"] = ArgToken(args, "element_id"), ["typeId"] = ArgToken(args, "type_id") };
                                result = RunMethod("changeElementType", p.ToString(Formatting.None));
                            }
                        }
                        else if (name == "export_sheets_pdf")
                        {
                            var p = new JObject { ["sheets"] = ArgStr(args, "sheets") };
                            result = RunMethod("exportSheetsPdf", p.ToString(Formatting.None));
                        }
                        else if (name == "export_schedule")
                        {
                            var p = new JObject { ["scheduleName"] = ArgStr(args, "schedule_name") };
                            result = RunMethod("exportSchedule", p.ToString(Formatting.None));
                        }
                        else if (name == "write_report")
                        {
                            var p = new JObject { ["format"] = ArgStr(args, "format"), ["title"] = ArgStr(args, "title"), ["content"] = ArgStr(args, "content") };
                            result = RunMethod("writeReport", p.ToString(Formatting.None));
                        }
                        else if (name == "find_cad") result = RunMethod("getCADLinks", "{}");
                        else if (name == "select_element")
                        {
                            var t = ArgToken(args, "element_ids");
                            JArray arr;
                            if (t is JArray ja) arr = ja;
                            else { arr = new JArray(); var s = (t != null && t.Type != JTokenType.Null && t.Type != JTokenType.Array) ? t : ArgToken(args, "element_id"); if (s != null && s.Type != JTokenType.Null) arr.Add(s); }
                            result = RunMethod("setSelection", new JObject { ["elementIds"] = arr }.ToString(Formatting.None));
                            try { if (arr.Count > 0) RunMethod("zoomToElements", new JObject { ["elementIds"] = arr }.ToString(Formatting.None)); } catch { }
                        }
                        else if (name == "delete_element")
                        {
                            long delId = ArgToken(args, "element_id")?.ToObject<long>() ?? 0;
                            if (delId <= 0)
                                result = "{\"success\":false,\"error\":\"no valid element id — READ the elements first (getTextNotes/getWalls/getDoors...) and pass their real ids; never guess or pass 0\"}";
                            else
                                result = RunMethod("deleteElement", new JObject { ["elementId"] = delId }.ToString(Formatting.None));
                        }
                        else if (name == "delete_category")
                        {
                            // CODE gathers the ids — the model repeatedly grabbed look-alike ids
                            // from history (round 9: tried to delete the fixture WALLS as "text notes")
                            string delCat = (ArgStr(args, "category") ?? "").ToLowerInvariant();
                            string readMethod = delCat.Contains("text") || delCat.Contains("note") ? "getTextNotes"
                                : delCat.Contains("wall") ? "getWalls"
                                : delCat.Contains("door") ? "getDoors"
                                : delCat.Contains("room") ? "getRooms" : null;
                            if (readMethod == null)
                                result = "{\"success\":false,\"error\":\"delete_category supports text notes, walls, doors, rooms — for other categories read the elements and use delete_elements with their ids\"}";
                            else
                            {
                                var catIds = new JArray();
                                try
                                {
                                    var rr = JObject.Parse(RunMethod(readMethod, "{}"));
                                    var arr = (rr["textNotes"] ?? rr["walls"] ?? rr["doors"] ?? rr["rooms"]) as JArray;
                                    foreach (var el in arr ?? new JArray())
                                    {
                                        var idTok = el["id"] ?? el["textNoteId"] ?? el["wallId"] ?? el["doorId"] ?? el["roomId"];
                                        if (idTok != null) catIds.Add(idTok.DeepClone());
                                    }
                                }
                                catch { }
                                result = catIds.Count == 0
                                    ? "{\"success\":true,\"deletedCount\":0,\"message\":\"there are no " + Esc(delCat) + " in the model — nothing to delete\"}"
                                    : RunMethod("deleteElements", new JObject { ["elementIds"] = catIds }.ToString(Formatting.None));
                            }
                        }
                        else if (name == "delete_elements")
                        {
                            var delIds = ArgToken(args, "element_ids") as JArray;
                            if (delIds == null || delIds.Count == 0 || delIds.Any(t => (t?.ToObject<long>() ?? 0) <= 0))
                                result = "{\"success\":false,\"error\":\"no valid element ids — READ the elements first (getTextNotes/getWalls/getDoors...) and pass their real ids\"}";
                            else if (!IdsSeenInConversation(messages, delIds))
                                result = "{\"success\":false,\"error\":\"those ids were never returned by any tool in this conversation — they are invented. READ the elements first (getTextNotes/getWalls/getDoors...) and pass the ids from that result.\"}";
                            else
                                result = RunMethod("deleteElements", new JObject { ["elementIds"] = delIds.DeepClone() }.ToString(Formatting.None));
                        }
                        else if (name == "create_floor_plan")
                        {
                            // resolve the LEVEL by name — "floor plan" otherwise collides with create_floor (a slab)
                            string fpLevel = ArgStr(args, "level") ?? "";
                            long fpLid = 0; long.TryParse(fpLevel.Trim(), out fpLid);
                            if (fpLid == 0)
                            {
                                try
                                {
                                    if (JObject.Parse(RunMethod("getLevels", "{}"))["levels"] is JArray lvls)
                                        foreach (var l in lvls)
                                            if (string.Equals(l["name"]?.ToString(), fpLevel.Trim(), StringComparison.OrdinalIgnoreCase))
                                            { fpLid = l["levelId"]?.ToObject<long>() ?? 0; break; }
                                }
                                catch { }
                            }
                            if (fpLid == 0)
                                result = "{\"success\":false,\"error\":\"no level named '" + Esc(fpLevel) + "' — getLevels lists the real names\"}";
                            else
                            {
                                var p = new JObject { ["levelId"] = fpLid };
                                var vn = ArgStr(args, "view_name"); if (!string.IsNullOrWhiteSpace(vn)) p["viewName"] = vn;
                                result = RunMethod("createFloorPlan", p.ToString(Formatting.None));
                            }
                        }
                        else if (name == "analyze_pdf") result = AnalyzePdf(ArgStr(args, "path"));
                        else if (name == "find_detail") result = FindDetail(ArgStr(args, "query"));
                        else if (name == "import_detail")
                        {
                            string dpath = ArgStr(args, "path");
                            bool incompat = false; string verMsg = "";
                            try
                            {
                                var vr = JObject.Parse(RunMethod("getRvtVersion", new JObject { ["path"] = dpath }.ToString(Formatting.None)));
                                if (vr["success"]?.ToObject<bool>() == true && vr["compatible"]?.ToObject<bool>() == false)
                                { incompat = true; verMsg = "That detail is Revit " + vr["version"] + ", newer than this session (Revit " + vr["running"] + ") — it can't be opened here. Use find_detail to get a compatible (older) copy."; }
                            }
                            catch { }
                            if (incompat) result = "{\"success\":false,\"error\":" + JsonConvert.ToString(verMsg) + "}";
                            else result = RunMethod("importDetailToDocument", new JObject { ["detailPath"] = dpath }.ToString(Formatting.None));
                        }
                        else if (name == "create_schedule")
                        {
                            string cat = ArgStr(args, "category") ?? "";
                            string nm = ArgStr(args, "name"); if (string.IsNullOrWhiteSpace(nm)) nm = cat + " Schedule";
                            var p = new JObject { ["category"] = cat, ["scheduleName"] = nm };
                            var flds = ArgToken(args, "fields"); if (flds is JArray fja && fja.Count > 0) p["fields"] = fja.DeepClone();
                            result = RunMethod("createScheduleWithFields", p.ToString(Formatting.None));
                        }
                        else if (name == "fix_marks")
                        {
                            var p = new JObject { ["category"] = ArgStr(args, "category") };
                            var pf = ArgStr(args, "prefix"); if (!string.IsNullOrWhiteSpace(pf)) p["prefix"] = pf;
                            result = RunMethod("fixDuplicateMarks", p.ToString(Formatting.None));
                        }
                        else if (name == "tag_untagged")
                        {
                            int vid = 0; try { vid = JObject.Parse(RunMethod("getActiveView", "{}"))["viewId"]?.ToObject<int>() ?? 0; } catch { }
                            var p = new JObject { ["viewId"] = vid, ["category"] = ArgStr(args, "category") ?? "Rooms" };
                            result = RunMethod("autoTagUntagged", p.ToString(Formatting.None));
                        }
                        else if (name == "set_marks")
                        {
                            var p = new JObject { ["prefix"] = ArgStr(args, "prefix") };
                            var ids = ArgToken(args, "element_ids"); if (ids is JArray ija && ija.Count > 0) p["elementIds"] = ija.DeepClone();
                            var cat2 = ArgStr(args, "category"); if (!string.IsNullOrWhiteSpace(cat2)) p["category"] = cat2;
                            var st = ArgToken(args, "start"); if (st != null && st.Type != JTokenType.Null) p["start"] = st.DeepClone();
                            var cm = ArgStr(args, "comments"); if (!string.IsNullOrWhiteSpace(cm)) p["comments"] = cm;
                            result = RunMethod("setSequentialMarks", p.ToString(Formatting.None));
                        }
                        else if (name == "import_pdf")
                        {
                            var p = new JObject { ["filePath"] = ArgStr(args, "path") };
                            var pg = ArgToken(args, "page"); if (pg != null && pg.Type != JTokenType.Null) p["page"] = pg.DeepClone();
                            result = RunMethod("importPDF", p.ToString(Formatting.None));
                        }
                        else if (name == "import_image")
                        {
                            int vid = 0; try { var jo = JObject.Parse(RunMethod("getActiveView", "{}")); vid = jo["viewId"]?.ToObject<int>() ?? 0; } catch { }
                            var p = new JObject { ["imagePath"] = ArgStr(args, "path"), ["viewId"] = vid };
                            result = RunMethod("importImage", p.ToString(Formatting.None));
                        }
                        else if (name == "create_walls")
                        {
                            var p = new JObject();
                            foreach (var key in new[] { "width", "depth", "x", "y", "height" })
                            { var v = ArgToken(args, key); if (v != null && v.Type != JTokenType.Null) p[key] = v.DeepClone(); }
                            var wtName = ArgStr(args, "wall_type"); if (!string.IsNullOrWhiteSpace(wtName)) p["wallTypeName"] = wtName;
                            result = RunMethod("createRoomWalls", p.ToString(Formatting.None));
                        }
                        else if (name == "add_window")
                        {
                            var p = new JObject();
                            var w = ArgToken(args, "wall_id"); if (w != null && w.Type != JTokenType.Null) p["wallId"] = w.DeepClone();
                            var pos = ArgToken(args, "position"); if (pos != null && pos.Type != JTokenType.Null) p["position"] = pos.DeepClone();
                            var sh = ArgToken(args, "sill_height"); if (sh != null && sh.Type != JTokenType.Null) p["sillHeight"] = sh.DeepClone();
                            var wt = ArgStr(args, "window_type"); if (!string.IsNullOrWhiteSpace(wt)) p["windowTypeName"] = wt;
                            result = RunMethod("addWindow", p.ToString(Formatting.None));
                        }
                        else if (name == "create_sheet")
                        {
                            var p = new JObject { ["sheetNumber"] = ArgStr(args, "number"), ["sheetName"] = ArgStr(args, "name") ?? "" };
                            result = RunMethod("createSheet", p.ToString(Formatting.None));
                        }
                        else if (name == "place_view")
                        {
                            var p = new JObject();
                            var sid = ArgToken(args, "sheet_id"); if (sid != null) p["sheetId"] = sid.DeepClone();
                            var vid = ArgToken(args, "view_id"); if (vid != null) p["viewId"] = vid.DeepClone();
                            result = RunMethod("placeViewOnSheet", p.ToString(Formatting.None));
                        }
                        else if (name == "undo") result = RunMethod("undoLastOperation", "{}");
                        else if (name == "create_3d_view")
                        {
                            var p = new JObject { ["viewName"] = ArgStr(args, "name") };
                            var pers = ArgToken(args, "perspective"); if (pers != null && pers.Type != JTokenType.Null) p["isPerspective"] = pers.DeepClone();
                            result = RunMethod("create3DView", p.ToString(Formatting.None));
                        }
                        else if (name == "create_section")
                        {
                            var p = new JObject { ["startPoint"] = new JArray { Dbl(args, "x1"), Dbl(args, "y1"), 0 }, ["endPoint"] = new JArray { Dbl(args, "x2"), Dbl(args, "y2"), 0 }, ["viewName"] = ArgStr(args, "name") };
                            result = RunMethod("createSection", p.ToString(Formatting.None));
                        }
                        else if (name == "create_elevation")
                        {
                            string dir = (ArgStr(args, "direction") ?? "north").ToLowerInvariant();
                            double dx = 0, dy = 1;
                            if (dir.StartsWith("s")) { dx = 0; dy = -1; } else if (dir.StartsWith("e")) { dx = 1; dy = 0; } else if (dir.StartsWith("w")) { dx = -1; dy = 0; }
                            var p = new JObject { ["location"] = new JArray { Dbl(args, "x"), Dbl(args, "y"), 0 }, ["direction"] = new JArray { dx, dy, 0 }, ["viewName"] = ArgStr(args, "name") };
                            result = RunMethod("createElevation", p.ToString(Formatting.None));
                        }
                        else if (name == "create_callout")
                        {
                            var p = new JObject { ["parentViewId"] = ActiveViewId(), ["minPoint"] = new JArray { Dbl(args, "x1"), Dbl(args, "y1"), 0 }, ["maxPoint"] = new JArray { Dbl(args, "x2"), Dbl(args, "y2"), 0 } };
                            result = RunMethod("createCallout", p.ToString(Formatting.None));
                        }
                        else if (name == "duplicate_view")
                        {
                            int vid = ArgToken(args, "view_id")?.ToObject<int>() ?? ActiveViewId();
                            bool det = ArgToken(args, "with_detailing")?.ToObject<bool>() ?? false;
                            var p = new JObject { ["viewId"] = vid, ["duplicateOption"] = det ? "withDetailing" : "duplicate", ["newName"] = ArgStr(args, "name") };
                            result = RunMethod("duplicateView", p.ToString(Formatting.None));
                        }
                        else if (name == "set_view_scale")
                        {
                            int vid = ArgToken(args, "view_id")?.ToObject<int>() ?? ActiveViewId();
                            var p = new JObject { ["viewId"] = vid, ["scale"] = ArgToken(args, "scale")?.DeepClone() };
                            result = RunMethod("setViewScale", p.ToString(Formatting.None));
                        }
                        else if (name == "apply_view_template")
                        {
                            int vid = ArgToken(args, "view_id")?.ToObject<int>() ?? ActiveViewId();
                            string tname = ArgStr(args, "template") ?? "";
                            int tid = 0;
                            try { var tr = JObject.Parse(RunMethod("getViewTemplates", "{}")); var arr = (tr["templates"] as JArray) ?? (tr["viewTemplates"] as JArray); if (arr != null) foreach (var tt in arr) { var nm = tt["name"]?.ToString(); if (nm != null && nm.IndexOf(tname, StringComparison.OrdinalIgnoreCase) >= 0) { tid = tt["id"]?.ToObject<int>() ?? tt["templateId"]?.ToObject<int>() ?? 0; break; } } } catch { }
                            if (tid == 0) result = "{\"success\":false,\"error\":\"no view template matching '" + Esc(tname) + "' found\"}";
                            else result = RunMethod("applyViewTemplate", new JObject { ["viewId"] = vid, ["templateId"] = tid }.ToString(Formatting.None));
                        }
                        else if (name == "place_keynote")
                        {
                            var p = new JObject { ["viewId"] = ActiveViewId(), ["referenceId"] = ArgToken(args, "element_id")?.DeepClone(), ["location"] = new JArray { Dbl(args, "x"), Dbl(args, "y"), 0 } };
                            result = RunMethod("placeKeynote", p.ToString(Formatting.None));
                        }
                        else if (name == "create_revision_cloud")
                        {
                            double x1 = Dbl(args, "x1"), y1 = Dbl(args, "y1"), x2 = Dbl(args, "x2"), y2 = Dbl(args, "y2");
                            var bp = new JArray { new JArray { x1, y1, 0 }, new JArray { x2, y1, 0 }, new JArray { x2, y2, 0 }, new JArray { x1, y2, 0 } };
                            result = RunMethod("createRevisionCloud", new JObject { ["viewId"] = ActiveViewId(), ["boundaryPoints"] = bp }.ToString(Formatting.None));
                        }
                        else if (name == "place_spot_elevation")
                        {
                            var p = new JObject { ["viewId"] = ActiveViewId(), ["referenceId"] = ArgToken(args, "element_id")?.DeepClone(), ["location"] = new JArray { Dbl(args, "x"), Dbl(args, "y"), 0 } };
                            result = RunMethod("placeSpotElevation", p.ToString(Formatting.None));
                        }
                        else if (name == "create_room")
                        {
                            var p = new JObject();
                            foreach (var k in new[] { "x", "y" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            var nm = ArgStr(args, "name"); if (!string.IsNullOrWhiteSpace(nm)) p["name"] = nm;
                            var num = ArgStr(args, "number"); if (!string.IsNullOrWhiteSpace(num)) p["number"] = num;
                            result = RunMethod("createRoomAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "create_floor")
                        {
                            var p = new JObject();
                            foreach (var k in new[] { "width", "depth", "x", "y" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createFloorRect", p.ToString(Formatting.None));
                        }
                        else if (name == "create_ceiling")
                        {
                            var p = new JObject();
                            foreach (var k in new[] { "width", "depth", "x", "y", "height" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createCeilingRect", p.ToString(Formatting.None));
                        }
                        else if (name == "create_roof")
                        {
                            var p = new JObject();
                            foreach (var k in new[] { "width", "depth", "x", "y", "offset" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createRoofRect", p.ToString(Formatting.None));
                        }
                        else if (name == "create_curtain_wall")
                        {
                            var p = new JObject();
                            foreach (var k in new[] { "x1", "y1", "x2", "y2", "height" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createCurtainWallAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "place_column")
                        {
                            var p = new JObject(); foreach (var k in new[] { "x", "y" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("placeColumnAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "create_beam")
                        {
                            var p = new JObject(); foreach (var k in new[] { "x1", "y1", "x2", "y2" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createBeamAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "create_stair")
                        {
                            var p = new JObject(); foreach (var k in new[] { "x", "y", "width" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            var d = ArgStr(args, "direction"); if (!string.IsNullOrWhiteSpace(d)) p["direction"] = d;
                            result = RunMethod("createStairAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "create_duct")
                        {
                            var p = new JObject(); foreach (var k in new[] { "x1", "y1", "z1", "x2", "y2", "z2" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createDuctAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "create_pipe")
                        {
                            var p = new JObject(); foreach (var k in new[] { "x1", "y1", "z1", "x2", "y2", "z2" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            result = RunMethod("createPipeAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "place_fixture")
                        {
                            var p = new JObject(); foreach (var k in new[] { "x", "y" }) { var v = ArgToken(args, k); if (v != null && v.Type != JTokenType.Null) p[k] = v.DeepClone(); }
                            var kd = ArgStr(args, "kind"); if (!string.IsNullOrWhiteSpace(kd)) p["kind"] = kd;
                            result = RunMethod("placeFixtureAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "create_level")
                        {
                            var p = new JObject { ["name"] = ArgStr(args, "name") };
                            var ev = ArgToken(args, "elevation"); if (ev != null && ev.Type != JTokenType.Null) p["elevation"] = ev.DeepClone();
                            result = RunMethod("createLevel", p.ToString(Formatting.None));
                        }
                        else if (name == "create_grid")
                        {
                            double gx1 = ArgToken(args, "x1")?.ToObject<double>() ?? 0, gy1 = ArgToken(args, "y1")?.ToObject<double>() ?? 0;
                            double gx2 = ArgToken(args, "x2")?.ToObject<double>() ?? 0, gy2 = ArgToken(args, "y2")?.ToObject<double>() ?? 0;
                            var p = new JObject { ["name"] = ArgStr(args, "name"), ["startPoint"] = new JArray { gx1, gy1, 0 }, ["endPoint"] = new JArray { gx2, gy2, 0 } };
                            result = RunMethod("createGrid", p.ToString(Formatting.None));
                        }
                        else if (name == "move_element")
                        {
                            double dx = ArgToken(args, "dx")?.ToObject<double>() ?? 0, dy = ArgToken(args, "dy")?.ToObject<double>() ?? 0;
                            var p = new JObject { ["elementId"] = ArgToken(args, "element_id")?.DeepClone(), ["offset"] = new JObject { ["x"] = dx, ["y"] = dy, ["z"] = 0 } };
                            result = RunMethod("moveElement", p.ToString(Formatting.None));
                        }
                        else if (name == "copy_element")
                        {
                            double dx = ArgToken(args, "dx")?.ToObject<double>() ?? 0, dy = ArgToken(args, "dy")?.ToObject<double>() ?? 0;
                            var ids = ArgToken(args, "element_ids"); var arr = ids is JArray ja ? (JArray)ja.DeepClone() : new JArray();
                            var p = new JObject { ["elementIds"] = arr, ["translation"] = new JArray { dx, dy, 0 } };
                            result = RunMethod("copyElements", p.ToString(Formatting.None));
                        }
                        else if (name == "rotate_element")
                        {
                            double rx = ArgToken(args, "x")?.ToObject<double>() ?? 0, ry = ArgToken(args, "y")?.ToObject<double>() ?? 0;
                            var ids = ArgToken(args, "element_ids"); var arr = ids is JArray ja ? (JArray)ja.DeepClone() : new JArray();
                            var p = new JObject { ["elementIds"] = arr, ["axisPoint"] = new JArray { rx, ry, 0 }, ["angle"] = ArgToken(args, "angle")?.DeepClone() };
                            result = RunMethod("rotateElements", p.ToString(Formatting.None));
                        }
                        else if (name == "array_element")
                        {
                            double dx = ArgToken(args, "dx")?.ToObject<double>() ?? 0, dy = ArgToken(args, "dy")?.ToObject<double>() ?? 0;
                            var ids = ArgToken(args, "element_ids"); var arr = ids is JArray ja ? (JArray)ja.DeepClone() : new JArray();
                            var p = new JObject { ["elementIds"] = arr, ["count"] = ArgToken(args, "count")?.DeepClone(), ["spacing"] = new JArray { dx, dy, 0 } };
                            result = RunMethod("arrayElements", p.ToString(Formatting.None));
                        }
                        else if (name == "place_text")
                        {
                            int vid = 0; try { vid = JObject.Parse(RunMethod("getActiveView", "{}"))["viewId"]?.ToObject<int>() ?? 0; } catch { }
                            double tx = ArgToken(args, "x")?.ToObject<double>() ?? 0, ty = ArgToken(args, "y")?.ToObject<double>() ?? 0;
                            var p = new JObject { ["viewId"] = vid, ["location"] = new JArray { tx, ty, 0.0 }, ["text"] = ArgStr(args, "text") };
                            result = RunMethod("placeTextNote", p.ToString(Formatting.None));
                        }
                        else if (name == "name_room")
                        {
                            var p = new JObject { ["roomId"] = ArgToken(args, "room_id")?.DeepClone() };
                            var nm = ArgStr(args, "name"); if (!string.IsNullOrWhiteSpace(nm)) p["name"] = nm;
                            var num = ArgStr(args, "number"); if (!string.IsNullOrWhiteSpace(num)) p["number"] = num;
                            result = RunMethod("setRoomName", p.ToString(Formatting.None));
                        }
                        else if (name == "renumber_rooms")
                        {
                            var p = new JObject();
                            var pf = ArgStr(args, "prefix"); if (!string.IsNullOrWhiteSpace(pf)) p["prefix"] = pf;
                            var st = ArgToken(args, "start"); if (st != null && st.Type != JTokenType.Null) p["start"] = st.DeepClone();
                            result = RunMethod("renumberRoomsAuto", p.ToString(Formatting.None));
                        }
                        else if (name == "select_category") result = RunMethod("selectByCategory", new JObject { ["category"] = ArgStr(args, "category") }.ToString(Formatting.None));
                        else if (name == "dimension_walls")
                        {
                            int vid = 0; try { vid = JObject.Parse(RunMethod("getActiveView", "{}"))["viewId"]?.ToObject<int>() ?? 0; } catch { }
                            var p = new JObject { ["viewId"] = vid, ["side"] = "exterior" };
                            var wids = ArgToken(args, "wall_ids");
                            JArray wallArr = (wids is JArray wja && wja.Count > 0) ? (JArray)wja.DeepClone() : null;
                            if (wallArr == null)   // no walls given — default to the walls in the active view
                            {
                                try
                                {
                                    var wr = JObject.Parse(RunMethod("getWallsInView", new JObject { ["viewId"] = vid }.ToString(Formatting.None)));
                                    if (wr["walls"] is JArray wallsJ) { wallArr = new JArray(); foreach (var w in wallsJ) { var id = w["wallId"]; if (id != null) wallArr.Add(id.DeepClone()); } }
                                }
                                catch { }
                            }
                            if (wallArr != null && wallArr.Count == 1)
                            {
                                // ONE wall: dimensionWalls needs 2+, so dimension this wall's own length
                                result = RunMethod("dimensionWallLength", new JObject { ["wallId"] = wallArr[0].DeepClone(), ["viewId"] = vid }.ToString(Formatting.None));
                            }
                            else
                            {
                                if (wallArr != null && wallArr.Count > 0) p["wallIds"] = wallArr;
                                var dir = ArgStr(args, "direction"); if (!string.IsNullOrWhiteSpace(dir)) p["direction"] = dir;
                                result = RunMethod("dimensionWalls", p.ToString(Formatting.None));
                            }
                        }
                        else if (name == "add_door")
                        {
                            var p = new JObject();
                            var w = ArgToken(args, "wall_id"); if (w != null && w.Type != JTokenType.Null) p["wallId"] = w.DeepClone();
                            var pos = ArgToken(args, "position"); if (pos != null && pos.Type != JTokenType.Null) p["position"] = pos.DeepClone();
                            var dt = ArgStr(args, "door_type"); if (!string.IsNullOrWhiteSpace(dt)) p["doorTypeName"] = dt;
                            result = RunMethod("addDoor", p.ToString(Formatting.None));
                        }
                        else if (name == "create_wall")
                        {
                            var p = new JObject();
                            double x1 = ArgToken(args, "x1")?.ToObject<double>() ?? 0, y1 = ArgToken(args, "y1")?.ToObject<double>() ?? 0;
                            double x2 = ArgToken(args, "x2")?.ToObject<double>() ?? 0, y2 = ArgToken(args, "y2")?.ToObject<double>() ?? 0;
                            p["startPoint"] = new JArray { x1, y1, 0.0 };
                            p["endPoint"] = new JArray { x2, y2, 0.0 };
                            var ht = ArgToken(args, "height"); p["height"] = (ht != null && ht.Type != JTokenType.Null && ht.ToObject<double>() > 0) ? ht.ToObject<double>() : 10.0;
                            // resolve level: by name if given, else the lowest level in the model
                            int levelId = 0;
                            var lvlName = ArgStr(args, "level");
                            try
                            {
                                if (JObject.Parse(RunMethod("getLevels", "{}"))["levels"] is JArray lvls && lvls.Count > 0)
                                {
                                    JToken pick = null;
                                    if (!string.IsNullOrWhiteSpace(lvlName))
                                        foreach (var l in lvls) { if (string.Equals(l["name"]?.ToString(), lvlName.Trim(), StringComparison.OrdinalIgnoreCase)) { pick = l; break; } }
                                    if (pick == null)
                                        foreach (var l in lvls) { if (pick == null || (l["elevation"]?.ToObject<double>() ?? 0) < (pick["elevation"]?.ToObject<double>() ?? 0)) pick = l; }
                                    levelId = pick?["levelId"]?.ToObject<int>() ?? 0;
                                }
                            }
                            catch { }
                            p["levelId"] = levelId;
                            var cwt = ArgStr(args, "wall_type"); if (!string.IsNullOrWhiteSpace(cwt)) p["wallTypeName"] = cwt;
                            // NOTE: must be the registry name — "createWall" only exists as a pipe-switch alias
                            result = RunMethod("createWallByPoints", p.ToString(Formatting.None));
                        }
                        else if (name == "select_elements")
                        {
                            var selIds = ArgToken(args, "element_ids") as JArray ?? new JArray();
                            if (selIds.Count > 0 && !IdsSeenInConversation(messages, selIds))
                                result = "{\"success\":false,\"error\":\"those ids were never returned by any tool in this conversation — they are invented. READ the elements first and pass the ids from that result.\"}";
                            else
                                result = RunMethod("setSelection", new JObject { ["elementIds"] = selIds }.ToString(Formatting.None));
                        }
                        else if (name == "select_walls_by_length")
                        {
                            double minF = ArgToken(args, "min_feet")?.ToObject<double?>() ?? double.MinValue;
                            double maxF = ArgToken(args, "max_feet")?.ToObject<double?>() ?? double.MaxValue;
                            var matchIds = new JArray();
                            try
                            {
                                if (JObject.Parse(RunMethod("getWalls", "{}"))["walls"] is JArray allWalls)
                                    foreach (var w in allWalls)
                                    {
                                        double wl = w["length"]?.ToObject<double>() ?? 0;
                                        if (wl > minF && wl < maxF) matchIds.Add(w["wallId"].DeepClone());
                                    }
                            }
                            catch { }
                            result = matchIds.Count == 0
                                ? "{\"success\":true,\"selectedCount\":0,\"message\":\"no walls match that length condition\"}"
                                : RunMethod("setSelection", new JObject { ["elementIds"] = matchIds }.ToString(Formatting.None));
                        }
                        else if (name == "rename_level")
                        {
                            // resolve the LEVEL id ourselves — models grab look-alike ids (views,
                            // rooms named the same) from history when left to find it
                            string lvlArg = ArgStr(args, "level") ?? "", newName = ArgStr(args, "new_name") ?? "";
                            long lid = 0; long.TryParse(lvlArg.Trim(), out lid);
                            if (lid == 0)
                            {
                                try
                                {
                                    if (JObject.Parse(RunMethod("getLevels", "{}"))["levels"] is JArray lvls)
                                        foreach (var l in lvls)
                                            if (string.Equals(l["name"]?.ToString(), lvlArg.Trim(), StringComparison.OrdinalIgnoreCase))
                                            { lid = l["levelId"]?.ToObject<long>() ?? 0; break; }
                                }
                                catch { }
                            }
                            result = lid == 0
                                ? "{\"success\":false,\"error\":\"no level named '" + Esc(lvlArg) + "' — getLevels lists the real names\"}"
                                : RunMethod("renameLevel", new JObject { ["levelId"] = lid, ["newName"] = newName }.ToString(Formatting.None));
                        }
                        else if (name == "set_text")
                        {
                            var p = new JObject { ["textNoteId"] = ArgToken(args, "element_id"), ["text"] = ArgStr(args, "text") };
                            result = RunMethod("setTextNoteText", p.ToString(Formatting.None));
                        }
                        else if (name == "remember") result = Remember(ArgStr(args, "note"));
                        else if (name == "tag_all")
                        {
                            var p = new JObject { ["category"] = ArgStr(args, "category"), ["viewId"] = ArgToken(args, "view_id") };
                            result = RunMethod("tagAllByCategory", p.ToString(Formatting.None));
                        }
                        else if (name == "find_wall")
                        {
                            var fxT = ArgToken(args, "x"); var fyT = ArgToken(args, "y");
                            double? fx = (fxT != null && fxT.Type != JTokenType.Null) ? fxT.ToObject<double>() : (double?)null;
                            double? fy = (fyT != null && fyT.Type != JTokenType.Null) ? fyT.ToObject<double>() : (double?)null;
                            result = FindWallBySide(ArgStr(args, "side"), fx, fy);
                        }
                        else if (name == "find_method") result = FindMethods(ArgStr(args, "query"));
                        else if (name == "run_method") result = RunMethod(ArgStr(args, "method"), ArgJson(args, "params_json"));
                        else if (name == "search_files") result = SearchFiles(ArgStr(args, "query"), ArgStr(args, "path"));
                        else if (name == "read_file") result = ReadFileTool(ArgStr(args, "path"));
                        else if (name == "web_search") result = WebSearch(ArgStr(args, "query"));
                        else if (name == "load_family") result = LoadFamilyFromDisk(ArgStr(args, "search_term"), ArgStr(args, "category"));
                        else if (name == "load_family_file")
                            result = RunMethod("loadFamily", new JObject { ["familyPath"] = ArgStr(args, "path") }.ToString(Formatting.None));
                        else if (name == "download_family") result = DownloadFamily(ArgStr(args, "url"), ArgStr(args, "name"));
                        else if (name == "place_family")
                        {
                            var zt = ArgToken(args, "z"); double z = 0; try { if (zt != null) z = zt.ToObject<double>(); } catch { }
                            var rt = ArgToken(args, "rotation"); double rot = 0; try { if (rt != null) rot = rt.ToObject<double>(); } catch { }
                            var p = new JObject { ["familyTypeId"] = ArgToken(args, "family_type_id"), ["location"] = new JArray { ArgToken(args, "x"), ArgToken(args, "y"), z }, ["rotation"] = rot };
                            result = RunMethod("placeFamilyInstance", p.ToString(Formatting.None));
                        }
                        else result = "unknown tool: " + name;
                        result = SelfVerify(name, args, result);   // read the model back so the LLM sees ground truth, not just the method's success flag
                        // tool-trace: ground truth of every agent tool call for cert/debugging
                        try
                        {
                            string argLog = args == null ? "" : (args.Type == JTokenType.String ? (string)args : args.ToString(Formatting.None));
                            if (argLog != null && argLog.Length > 300) argLog = argLog.Substring(0, 300) + "…";
                            string resLog = result ?? "(null)";
                            if (resLog.Length > 300) resLog = resLog.Substring(0, 300) + "…";
                            Log.Information("[Copilot tool] {Tool} args={Args} result={Result}", name, argLog, resLog);
                        }
                        catch { }
                        var toolMsg = new JObject { ["role"] = "tool", ["content"] = result };
                        if (tc["id"] != null) toolMsg["tool_call_id"] = tc["id"];   // OpenAI-compatible APIs require this
                        if (name != null) toolMsg["name"] = name;
                        messages.Add(toolMsg);
                    }
                }
                return "(stopped after several steps — try rephrasing what you want)";
            }
            catch (Exception ex) { Log.Debug($"RevitAgent.ChatAsync: {ex.Message}"); return null; }
        }

        // arguments may arrive as an object (JObject) or a JSON string — handle both
        private static string ArgStr(JToken args, string key)
        {
            if (args == null) return null;
            if (args.Type == JTokenType.String) { try { args = JObject.Parse((string)args); } catch { return null; } }
            return (string)args[key];
        }
        private static JToken ArgToken(JToken args, string key)
        {
            if (args == null) return null;
            if (args.Type == JTokenType.String) { try { args = JObject.Parse((string)args); } catch { return null; } }
            return args[key];
        }
        private static string ArgJson(JToken args, string key)
        {
            if (args == null) return "{}";
            if (args.Type == JTokenType.String) { try { args = JObject.Parse((string)args); } catch { return "{}"; } }
            var t = args[key];
            if (t == null) return "{}";
            return t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        // ---- SELF-VERIFICATION: after a mutating tool, read the live model back and append what we
        // actually find to the tool result. The LLM then sees ground truth (element really exists /
        // parameter really took the value) instead of trusting a success flag — code-driven, so it
        // works no matter how small the model is.
        private static readonly HashSet<string> VerifyCreateTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create_walls","create_wall","add_door","add_window","create_sheet","create_room","create_floor","create_ceiling",
            "create_roof","create_curtain_wall","place_column","create_beam","create_stair","create_duct",
            "create_pipe","place_fixture","create_level","create_grid","place_family","place_text",
            "create_3d_view","create_section","create_elevation","create_callout","duplicate_view",
            "place_keynote","create_revision_cloud","place_spot_elevation","tag_element","change_type",
            "move_element","copy_element","rotate_element"
        };
        private static readonly HashSet<string> VerifyIdKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "elementId","wallId","doorId","windowId","roomId","sheetId","viewId","levelId","gridId",
            "floorId","ceilingId","roofId","columnId","beamId","stairId","ductId","pipeId","fixtureId",
            "instanceId","familyInstanceId","textNoteId","tagId","newElementId","newViewId","calloutId","cloudId"
        };
        private static readonly HashSet<string> VerifyIdArrayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "elementIds","wallIds","createdIds","newElementIds","copiedIds","ids"
        };

        /// <summary>Pull element ids out of a tool-result JSON (top level + nested one level under e.g. "result").</summary>
        private static void ExtractIds(JToken tok, List<long> found, int depth = 0)
        {
            if (tok == null || found.Count >= 6 || depth > 3) return;
            if (tok is JObject jo)
            {
                foreach (var prop in jo.Properties())
                {
                    if (found.Count >= 6) break;
                    if (VerifyIdKeys.Contains(prop.Name) && (prop.Value.Type == JTokenType.Integer))
                    { long v = (long)prop.Value; if (v > 0 && !found.Contains(v)) found.Add(v); }
                    else if (VerifyIdArrayKeys.Contains(prop.Name) && prop.Value is JArray ja)
                    { foreach (var t in ja) { if (found.Count >= 6) break; if (t.Type == JTokenType.Integer) { long v = (long)t; if (v > 0 && !found.Contains(v)) found.Add(v); } } }
                    else if (prop.Value is JObject || prop.Value is JArray) ExtractIds(prop.Value, found, depth + 1);
                }
            }
            else if (tok is JArray arr) foreach (var t in arr) { if (found.Count >= 6) break; if (t is JObject || t is JArray) ExtractIds(t, found, depth + 1); }
        }

        /// <summary>Append a read-back verification line to a mutating tool's result.</summary>
        private static string SelfVerify(string name, JToken args, string result)
        {
            try
            {
                if (string.IsNullOrEmpty(result)) return result;

                // set_parameter: read the value back and show what the model now actually holds
                if (name == "set_parameter")
                {
                    var eid = ArgToken(args, "element_id"); var pname = ArgStr(args, "parameter_name");
                    if (eid == null || string.IsNullOrWhiteSpace(pname) || result.Contains("\"success\":false")) return result;
                    var rb = RunMethod("getParameterValue", new JObject { ["elementId"] = eid.DeepClone(), ["parameterName"] = pname }.ToString(Formatting.None));
                    try
                    {
                        var rj = JObject.Parse(rb);
                        if (rj["success"]?.ToObject<bool>() == true)
                            return result + "  | VERIFY (read back from the model): '" + pname + "' now = " + (rj["value"]?.ToString(Formatting.None) ?? "null");
                        return result + "  | VERIFY FAILED: could not read '" + pname + "' back (" + (rj["error"] ?? "") + ") — do NOT claim success; re-check.";
                    }
                    catch { return result; }
                }

                if (!VerifyCreateTools.Contains(name)) return result;
                if (result.Contains("\"success\":false") || result.Contains("\"blocked\":true")) return result;

                JToken parsed; try { parsed = JToken.Parse(result.Split(new[] { "  | NOTE — " }, StringSplitOptions.None)[0]); } catch { return result; }
                var ids = new List<long>(); ExtractIds(parsed, ids);
                if (ids.Count == 0) return result;

                var ok = new List<long>(); var missing = new List<long>();
                foreach (var id in ids.Take(4))
                {
                    var rb = RunMethod("getElementLocation", new JObject { ["elementId"] = id }.ToString(Formatting.None));
                    if (rb != null && rb.Contains("Element not found")) missing.Add(id); else ok.Add(id);
                }
                if (missing.Count > 0)
                    return result + "  | VERIFY FAILED: element(s) " + string.Join(", ", missing) + " NOT found in the model — this step did NOT take effect. Do not claim success; retry differently or tell the user exactly what failed." + (ok.Count > 0 ? " (confirmed in model: " + string.Join(", ", ok) + ")" : "");
                return result + "  | VERIFY: read back from the live model — element(s) " + string.Join(", ", ok) + " exist. Confirmed.";
            }
            catch { return result; }
        }

        // ---- folder/system search, file read, and web search (run in-process; no Revit API needed) ----
        private static string StripHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(s).Trim();
        }

        private static string SearchFiles(string query, string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return "Provide a filename keyword to search for.";
                string root = path;
                if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root))
                    root = System.IO.Directory.Exists("D:\\BD-Architect-Files") ? "D:\\BD-Architect-Files" : "D:\\";
                if (!System.IO.Directory.Exists(root)) return "Folder not found: " + root;
                var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
                var hits = new List<string>();
                foreach (var f in System.IO.Directory.EnumerateFiles(root, "*", opts))
                {
                    if (System.IO.Path.GetFileName(f).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    { hits.Add(f); if (hits.Count >= 30) break; }
                }
                return hits.Count == 0 ? ("No files matching '" + query + "' under " + root) : ("Found " + hits.Count + " file(s):\n" + string.Join("\n", hits));
            }
            catch (Exception ex) { return "search error: " + ex.Message; }
        }

        private static string ReadFileTool(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return "File not found: " + path;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                var ok = new HashSet<string> { ".txt", ".md", ".csv", ".json", ".xml", ".log", ".cs", ".py", ".html", ".htm", ".ini", ".cfg", ".yaml", ".yml" };
                if (!ok.Contains(ext)) return "Not a readable text file (" + ext + "). I can read text files (txt, md, csv, json, xml, log…).";
                if (new System.IO.FileInfo(path).Length > 200_000) return "File too large (>200KB) to read inline.";
                string content = System.IO.File.ReadAllText(path);
                return content.Length > 12000 ? content.Substring(0, 12000) + "\n…[truncated]" : content;
            }
            catch (Exception ex) { return "read error: " + ex.Message; }
        }

        private static string LoadFamilyFromDisk(string term, string category)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(term)) return "Tell me what family to find (e.g. 'chair', 'single door', 'toilet').";
                var words = term.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string key = words.Length > 0 ? words[words.Length - 1] : term.ToLowerInvariant();   // the object noun
                var roots = new List<string> { "D:\\003 - RESOURCES\\_FAMILY_CLOUD", "D:\\BD-Architect-Files", "C:\\ProgramData\\Autodesk\\RVT 2025\\Libraries", "D:\\" };
                var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
                string found = null; int examined = 0;
                foreach (var root in roots)
                {
                    if (!System.IO.Directory.Exists(root)) continue;
                    foreach (var f in System.IO.Directory.EnumerateFiles(root, "*.rfa", opts))
                    {
                        if (++examined > 150000) break;
                        var fn = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                        if (fn.Contains(key)) { found = f; break; }
                    }
                    if (found != null) break;
                }
                if (found == null) return "No family matching '" + term + "' found on disk (searched D:\\ and the Revit library). Try a simpler term, use search_files for the exact name, or download_family with a URL.";
                var loaded = RunMethod("loadFamily", new JObject { ["familyPath"] = found }.ToString(Formatting.None));
                return "Found and loaded: " + System.IO.Path.GetFileName(found) + "\n(from " + System.IO.Path.GetDirectoryName(found) + ")\n" + loaded;
            }
            catch (Exception ex) { return "load family error: " + ex.Message; }
        }

        private static string LoadAutodeskFamily(string searchTerm, string category)
        {
            try
            {
                // Open Autodesk's cloud library for the user to pick from (PostCommand = non-blocking + safe;
                // the fully-automated loader was dropped because it could hang Revit's modal web dialog).
                var res = RunMethod("openLoadAutodeskFamilyDialog", "{}");
                bool ok = res != null && res.Contains("\"success\":true");
                if (ok)
                    return "I've opened the Autodesk cloud family library for you. Search for " +
                           (string.IsNullOrWhiteSpace(searchTerm) ? "the family you want" : "'" + searchTerm + "'") +
                           ", pick it, and it'll load into your project. (You need to be signed into Autodesk.) " +
                           "Tip: for an instant, hands-free load I can also pull from your local libraries with load_family.";
                return "Couldn't open the Autodesk cloud library here. Use load_family for your local libraries, or download_family with an .rfa link. " + (res ?? "");
            }
            catch (Exception ex) { return "autodesk family error: " + ex.Message; }
        }

        /// <summary>Search the detail library and return a clean, de-duplicated, capped list for the model.</summary>
        private static string FindDetail(string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return "Tell me what detail to look for (e.g. 'door head', 'parapet').";
                var raw = RunMethod("findCompatibleDetails", new JObject { ["searchTerm"] = query }.ToString(Formatting.None));
                var jo = JObject.Parse(raw);
                if (jo["success"]?.ToObject<bool>() != true) return raw;
                var results = jo["results"] as JArray;
                int skipped = jo["skippedNewer"]?.ToObject<int>() ?? 0;
                if (results == null || results.Count == 0)
                    return "No version-compatible details matching '" + query + "' found." + (skipped > 0 ? " (" + skipped + " matches exist but are a newer Revit version than this session.)" : "");
                var sb = new StringBuilder();
                foreach (var r in results)
                    sb.AppendLine("• " + r["name"] + "  [Revit " + r["version"] + ", " + r["library"] + "]  → " + r["path"]);
                string note = skipped > 0 ? "\n(" + skipped + " newer-version matches skipped — not openable in this Revit.)" : "";
                return "Found " + results.Count + " compatible detail(s) for '" + query + "':\n" + sb.ToString() + note + "Use import_detail(path) to bring one in.";
            }
            catch (Exception ex) { return "find_detail error: " + ex.Message; }
        }

        /// <summary>SPATIAL HELPER: resolve "the north wall" / "the longest wall" / "the center of the
        /// room" to real ids + coordinates by reading wall geometry from the active view — so the
        /// local model never has to guess plan directions from raw numbers.</summary>
        private static string FindWallBySide(string side, double? px = null, double? py = null)
        {
            try
            {
                string s = (side ?? "").Trim().ToLowerInvariant();
                if (s == "front") s = "south"; else if (s == "back") s = "north"; else if (s == "left") s = "west"; else if (s == "right") s = "east";
                // a point passed as the side string — "(25, 30)" / "25, 30" — becomes a nearest-wall search
                if (!px.HasValue || !py.HasValue)
                {
                    var pm = System.Text.RegularExpressions.Regex.Match(s, @"\(?\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)?");
                    if (pm.Success) { px = double.Parse(pm.Groups[1].Value); py = double.Parse(pm.Groups[2].Value); }
                }
                if (s.Length == 0 && !(px.HasValue && py.HasValue)) return "Tell me which wall: north/south/east/west (or front/back/left/right), longest, shortest, 'center' — or pass x,y for the wall nearest a point.";
                int vid = ActiveViewId();
                if (vid <= 0) return "No active view — open a plan view first.";
                var wr = JObject.Parse(RunMethod("getWallsInView", new JObject { ["viewId"] = vid }.ToString(Formatting.None)));
                if (wr["success"]?.ToObject<bool>() != true) return "Couldn't read the walls: " + (wr["error"] ?? "unknown error");
                var walls = wr["walls"] as JArray;
                if (walls == null || walls.Count == 0)
                {
                    // active view may be a sheet/schedule (no walls visible) — fall back to the whole model
                    try
                    {
                        var wrAll = JObject.Parse(RunMethod("getWalls", "{}"));
                        walls = wrAll["walls"] as JArray;
                    }
                    catch { }
                    if (walls == null || walls.Count == 0) return "There are no walls in the model.";
                }

                // pull each wall's curve endpoints (cap to keep it quick)
                var infos = new List<(long id, string type, double len, double x1, double y1, double x2, double y2, double mx, double my, bool horiz)>();
                foreach (var w in walls.Take(60))
                {
                    long id = w["wallId"]?.ToObject<long>() ?? 0; if (id <= 0) continue;
                    try
                    {
                        var loc = JObject.Parse(RunMethod("getElementLocation", new JObject { ["elementId"] = id }.ToString(Formatting.None)));
                        if (loc["locationType"]?.ToString() != "Curve") continue;
                        var sp = loc["startPoint"] as JArray; var ep = loc["endPoint"] as JArray;
                        if (sp == null || ep == null) continue;
                        double x1 = (double)sp[0], y1 = (double)sp[1], x2 = (double)ep[0], y2 = (double)ep[1];
                        infos.Add((id, w["wallType"]?.ToString() ?? "?", w["length"]?.ToObject<double>() ?? 0,
                            x1, y1, x2, y2, (x1 + x2) / 2, (y1 + y2) / 2, Math.Abs(x2 - x1) >= Math.Abs(y2 - y1)));
                    }
                    catch { }
                }
                if (infos.Count == 0) return "Couldn't read wall geometry in the active view.";
                bool hasSide = s == "longest" || s == "shortest" || s == "center" || s == "north" || s == "south" || s == "east" || s == "west" || s.Contains("middle");
                if (px.HasValue && py.HasValue && hasSide)
                {
                    // side + point = "the south wall of the room AT (x, y)": restrict the pool to
                    // that neighborhood, then apply the side logic. Ignore filler (0,0) points.
                    if (Math.Abs(px.Value) < 1e-9 && Math.Abs(py.Value) < 1e-9) { px = null; py = null; }
                    else
                    {
                        var near20 = infos.Where(i =>
                        {
                            double dx2 = i.mx - px.Value, dy2 = i.my - py.Value;
                            return Math.Sqrt(dx2 * dx2 + dy2 * dy2) <= 20.0;
                        }).ToList();
                        if (near20.Count > 0) infos = near20;
                        px = null; py = null;   // fall through to side selection on the restricted pool
                    }
                }
                if (px.HasValue && py.HasValue)
                {
                    // nearest wall to the point, by true point-to-segment distance
                    double DistSeg(double x1, double y1, double x2, double y2)
                    {
                        double dx = x2 - x1, dy = y2 - y1, len2 = dx * dx + dy * dy;
                        double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px.Value - x1) * dx + (py.Value - y1) * dy) / len2));
                        double nx = x1 + t * dx - px.Value, ny = y1 + t * dy - py.Value;
                        return Math.Sqrt(nx * nx + ny * ny);
                    }
                    var near = infos.OrderBy(i => DistSeg(i.x1, i.y1, i.x2, i.y2)).First();
                    return "The wall nearest (" + px.Value.ToString("0.#") + ", " + py.Value.ToString("0.#") + "): id " + near.id +
                        ", '" + near.type + "', length " + near.len.ToString("0.#") + " ft, from (" + near.x1.ToString("0.#") + ", " + near.y1.ToString("0.#") +
                        ") to (" + near.x2.ToString("0.#") + ", " + near.y2.ToString("0.#") + "), midpoint (" + near.mx.ToString("0.#") + ", " + near.my.ToString("0.#") + ").";
                }
                double cx = infos.Average(i => i.mx), cy = infos.Average(i => i.my);
                string center = "Plan center ≈ (" + cx.ToString("0.#") + ", " + cy.ToString("0.#") + ") ft.";
                if (s == "center" || s.Contains("middle")) return center + " (average of the " + infos.Count + " walls in the active view)";

                (long id, string type, double len, double x1, double y1, double x2, double y2, double mx, double my, bool horiz) pick;
                if (s == "longest") pick = infos.OrderByDescending(i => i.len).First();
                else if (s == "shortest") pick = infos.OrderBy(i => i.len).First();
                else
                {
                    // sides: prefer walls running ALONG that side (horizontal for N/S, vertical for E/W)
                    var pool = (s == "north" || s == "south") ? infos.Where(i => i.horiz).ToList() : infos.Where(i => !i.horiz).ToList();
                    if (pool.Count == 0) pool = infos;
                    switch (s)
                    {
                        case "north": pick = pool.OrderByDescending(i => i.my).First(); break;
                        case "south": pick = pool.OrderBy(i => i.my).First(); break;
                        case "east": pick = pool.OrderByDescending(i => i.mx).First(); break;
                        case "west": pick = pool.OrderBy(i => i.mx).First(); break;
                        default: return "Unknown side '" + side + "'. Use north/south/east/west, longest, shortest, or center.";
                    }
                }
                return "The " + s + " wall: id " + pick.id + ", '" + pick.type + "', " + pick.len.ToString("0.#") + " ft, from (" +
                    pick.x1.ToString("0.#") + ", " + pick.y1.ToString("0.#") + ") to (" + pick.x2.ToString("0.#") + ", " + pick.y2.ToString("0.#") +
                    "), midpoint (" + pick.mx.ToString("0.#") + ", " + pick.my.ToString("0.#") + "). " + center;
            }
            catch (Exception ex) { return "find_wall error: " + ex.Message; }
        }

        /// <summary>Resolve the active view's id (0 if none) — used by view/annotation tools.</summary>
        private static int ActiveViewId()
        {
            try { return JObject.Parse(RunMethod("getActiveView", "{}"))["viewId"]?.ToObject<int>() ?? 0; } catch { return 0; }
        }

        private static double Dbl(JToken args, string key, double dflt = 0)
        {
            try { var v = ArgToken(args, key); return (v != null && v.Type != JTokenType.Null) ? v.ToObject<double>() : dflt; } catch { return dflt; }
        }

        private static string WinToWsl(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Replace("\\", "/");
            if (p.Length > 1 && p[1] == ':') p = "/mnt/" + char.ToLower(p[0]) + p.Substring(2);
            return p;
        }

        /// <summary>Read a PDF drawing set (via the WSL pymupdf+vision analyzer): find floor-plan pages + their scale/dims.</summary>
        private static string AnalyzePdf(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "Give me the PDF path (use search_files to find it).";
                string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                string scriptWin = System.IO.Path.Combine(dir, "analyze_pdf_plan.py");
                if (!System.IO.File.Exists(scriptWin)) return "PDF analyzer is not installed (analyze_pdf_plan.py).";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = "python3 \"" + WinToWsl(scriptWin) + "\" \"" + WinToWsl(path) + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                string outp = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(200000);
                if (string.IsNullOrWhiteSpace(outp)) return "The analyzer produced no output. " + (err ?? "");
                try
                {
                    var jo = JObject.Parse(outp);
                    var sb = new StringBuilder();
                    sb.AppendLine("PDF set: " + jo["pageCount"] + " pages.");
                    var fps = jo["floorPlans"] as JArray;
                    if (fps != null)
                    {
                        sb.AppendLine("Floor-plan candidate pages: " + string.Join(", ", fps.Select(f => f["page"])));
                        foreach (var f in fps) if (f["vision"] != null) sb.AppendLine("\nPage " + f["page"] + " (" + f["sheet"] + "):\n" + f["vision"].ToString().Trim());
                    }
                    sb.AppendLine("\nUse import_pdf(path, page) to bring in the right page, then scale it to its stated scale.");
                    return sb.ToString();
                }
                catch { return outp; }
            }
            catch (Exception ex) { return "analyze error: " + ex.Message; }
        }

        private static string DownloadFamily(string url, string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return "Provide a direct https URL to an .rfa file.";
                string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CopilotFamilies");
                System.IO.Directory.CreateDirectory(folder);
                string fname = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(new Uri(url).LocalPath) : name;
                if (string.IsNullOrWhiteSpace(fname)) fname = "downloaded_family";
                if (!fname.ToLowerInvariant().EndsWith(".rfa")) fname += ".rfa";
                string path = System.IO.Path.Combine(folder, fname);
                var bytes = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (bytes == null || bytes.Length < 100) return "Download returned no usable file from " + url;
                System.IO.File.WriteAllBytes(path, bytes);
                var loaded = RunMethod("loadFamily", new JObject { ["familyPath"] = path }.ToString(Formatting.None));
                return "Downloaded to " + path + "\nLoad result: " + loaded;
            }
            catch (Exception ex) { return "download error: " + ex.Message; }
        }

        private static string WebSearch(string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return "Provide a web search query.";
                string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&count=10";
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    var r = _http.SendAsync(req).GetAwaiter().GetResult();
                    if (!r.IsSuccessStatusCode) return "Web search unavailable (HTTP " + (int)r.StatusCode + "). Check the internet connection.";
                    string html = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var blocks = System.Text.RegularExpressions.Regex.Split(html, "<li class=\"b_algo\"");
                    var titleRx = new System.Text.RegularExpressions.Regex("<h2>.*?<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", System.Text.RegularExpressions.RegexOptions.Singleline);
                    var snipRx = new System.Text.RegularExpressions.Regex("<p[^>]*>(.*?)</p>", System.Text.RegularExpressions.RegexOptions.Singleline);
                    var outp = new List<string>();
                    for (int i = 1; i < blocks.Length && outp.Count < 5; i++)
                    {
                        var tm = titleRx.Match(blocks[i]);
                        if (!tm.Success) continue;
                        string title = StripHtml(tm.Groups[2].Value);
                        string href = tm.Groups[1].Value;
                        var sm = snipRx.Match(blocks[i]);
                        string snip = sm.Success ? StripHtml(sm.Groups[1].Value) : "";
                        if (snip.Length > 240) snip = snip.Substring(0, 240) + "…";
                        outp.Add((outp.Count + 1) + ". " + title + (string.IsNullOrEmpty(snip) ? "" : "\n   " + snip) + (string.IsNullOrEmpty(href) ? "" : "\n   " + href));
                    }
                    return outp.Count == 0 ? ("No web results for '" + query + "'.") : string.Join("\n\n", outp);
                }
            }
            catch (Exception ex) { return "web search error: " + ex.Message; }
        }

        /// <summary>Post a chat turn to Ollama (/api/chat) OR any OpenAI-compatible API (/v1/chat/completions).
        /// Returns the assistant message {content, tool_calls}, normalized across both response shapes.</summary>
        private static async Task<JObject> PostChatAsync(string model, JArray messages, JArray tools, bool think)
        {
            try
            {
                bool openai = ApiType == "openai";
                var payload = new JObject { ["model"] = model, ["messages"] = messages, ["stream"] = false };
                if (tools != null && tools.Count > 0) { payload["tools"] = tools; if (openai) payload["tool_choice"] = "auto"; }
                if (!openai)
                {
                    payload["think"] = think; payload["keep_alive"] = "30m";
                    // Big system prompt (40+ tool schemas + standards/knowledge) plus a multi-turn batch
                    // needs a large context window, else Ollama truncates the input and the model returns
                    // nothing. qwen3:32b supports 40960; give it generous room.
                    // Low temperature: same question -> same behavior (unpinned sampling made
                    // identical cert prompts pass/fail randomly across runs).
                    payload["options"] = new JObject { ["num_ctx"] = 24576, ["temperature"] = 0.15 };
                }
                using (var req = new HttpRequestMessage(HttpMethod.Post, OllamaUrl))
                {
                    req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    if (openai && !string.IsNullOrEmpty(ApiKey))
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
                    var r = await _http.SendAsync(req).ConfigureAwait(false);
                    string txt = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode) { Log.Debug($"chat {(int)r.StatusCode}: {txt}"); return null; }
                    var jo = JObject.Parse(txt);
                    TrackUsage(jo, openai);   // meter tokens + cost (local = free)
                    return (openai ? jo["choices"]?[0]?["message"] : jo["message"]) as JObject;
                }
            }
            catch (Exception ex) { Log.Debug($"PostChatAsync: {ex.Message}"); return null; }
        }

        // ---- TOOL TIERING: with ~80 tools, sending every schema each turn bloats the prompt and makes
        // the local model lose focus on long requests. So expose a CORE set always + attach only the
        // specialized groups whose keywords appear in the request. find_method/run_method stay in CORE,
        // so anything filtered out is still reachable via the escape hatch.
        private static readonly HashSet<string> CoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "set_parameter","set_param_all","set_text","change_type","tag_element","tag_all","delete_element",
            "select_element","find_cad","select_category","find_wall",
            "create_walls","create_wall","add_door","add_window","move_element","copy_element","undo","select_elements","rename_level","select_walls_by_length","delete_elements","delete_category","create_floor_plan",
            "coordinate_model","get_warnings","find_duplicates","quality_check",
            "find_types","find_method","run_method","remember","web_search"
        };
        private static readonly (string[] keys, string[] tools)[] ToolGroups = new (string[], string[])[]
        {
            (new[]{"section","elevation","callout","3d","view scale","view template","duplicate view","viewport"," view"},
             new[]{"create_3d_view","create_section","create_elevation","create_callout","duplicate_view","set_view_scale","apply_view_template"}),
            (new[]{"keynote","revision","cloud","spot","note","text","dimension","mark","number","renumber","annotat"},
             new[]{"place_keynote","create_revision_cloud","place_spot_elevation","place_text","dimension_walls","set_marks","fix_marks","name_room","renumber_rooms","auto_mark"}),
            (new[]{"sheet","schedule","pdf","export","deliverable","report","csv","title block"},
             new[]{"create_sheet","place_view","create_schedule","export_schedule","export_sheets_pdf","write_report"}),
            (new[]{"column","beam","stair","curtain","structural","footing","foundation"},
             new[]{"place_column","create_beam","create_stair","create_curtain_wall"}),
            (new[]{"duct","pipe","mep","mechanical","plumbing","fixture","hvac","electrical","light"},
             new[]{"create_duct","create_pipe","place_fixture"}),
            (new[]{"room","floor","ceiling","roof","level","grid","build","shell"},
             new[]{"create_room","create_floor","create_ceiling","create_roof","create_level","create_grid","tag_untagged"}),
            (new[]{"family","families","chair","furniture","component"},
             new[]{"load_family","load_family_file","download_family","place_family"}),
            (new[]{"detail","head","jamb","sill","parapet"},
             new[]{"find_detail","import_detail"}),
            (new[]{"pdf","image","underlay","trace","floor plan","scan","import"},
             new[]{"analyze_pdf","import_pdf","import_image"}),
            (new[]{"rotate","array","mirror"},
             new[]{"rotate_element","array_element"}),
            (new[]{"code","clash","audit","purge","compliance","ada","fbc","ibc"},
             new[]{"audit_coordination","run_code_check","run_clash_check","purge_unused"}),
            (new[]{"file","look up","internet","read file"},
             new[]{"search_files","read_file"}),
        };
        private static JArray SelectTools(JArray all, string question)
        {
            try
            {
                string q = (question ?? "").ToLowerInvariant();
                var active = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase);
                foreach (var g in ToolGroups) if (g.keys.Any(k => q.Contains(k))) foreach (var tn in g.tools) active.Add(tn);
                var outArr = new JArray();
                foreach (var t in all) { var nm = t?["function"]?["name"]?.ToString(); if (nm != null && active.Contains(nm)) outArr.Add(t.DeepClone()); }
                return outArr.Count < 8 ? all : outArr;   // safety: never strip down to nothing
            }
            catch { return all; }
        }

        private static JObject ToolDef(string name, string desc, JObject props, JArray required) =>
            new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = name, ["description"] = desc, ["parameters"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = required } } };
        private static JObject SchemaStr(string desc) => new JObject { ["type"] = "string", ["description"] = desc };
        private static string StripThink(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
        private static string Esc(string s) => (s ?? "").Replace("\\", "/").Replace("\"", "'");
    }
}
