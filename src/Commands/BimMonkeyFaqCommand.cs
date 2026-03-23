using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPBridge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BimMonkeyFaqCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var win = new FaqWindow();
                win.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIM Monkey FAQ", ex.Message);
                return Result.Succeeded;
            }
        }
    }

    internal class FaqWindow : Window
    {
        public FaqWindow()
        {
            Title = "BIM Monkey — FAQ";
            Width = 780;
            Height = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;

            var browser = new WebBrowser();
            Content = browser;
            browser.NavigateToString(BuildHtml());
        }

        private static string BuildHtml()
        {
            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            html.AppendLine("<style>");
            html.AppendLine("body{font-family:'Segoe UI',Arial,sans-serif;margin:0;background:#f5f5f5;color:#111;}");
            html.AppendLine(".header{background:#000;color:#f5f5f5;padding:1.5rem 2rem;letter-spacing:-0.02em;}");
            html.AppendLine(".header h1{margin:0;font-size:1.4rem;font-weight:300;}");
            html.AppendLine(".header p{margin:0.3rem 0 0;font-size:0.85rem;color:#ccc;font-weight:300;}");
            html.AppendLine(".content{max-width:680px;margin:1.5rem auto;padding:0 2.5rem 3rem;}");
            html.AppendLine("h2{font-size:0.78rem;font-weight:600;letter-spacing:0.06em;text-transform:uppercase;margin:1.75rem 0 0.6rem;border-bottom:2px solid #0055cc;padding-bottom:0.35rem;color:#0055cc;}");
            html.AppendLine(".q{font-weight:600;margin:0.9rem 0 0.2rem;font-size:0.95rem;color:#000;border-left:3px solid #0055cc;padding-left:0.5rem;}");
            html.AppendLine(".a{color:#000;font-weight:300;line-height:1.6;margin:0 0 0.5rem 0;font-size:0.9rem;}");
            html.AppendLine("code{background:#e8e8e8;padding:0.1rem 0.35rem;border-radius:3px;font-size:0.85em;color:#000;}");
            html.AppendLine(".step{margin:0.4rem 0;font-size:0.9rem;color:#000;padding-left:1.8rem;text-indent:-1.8rem;}");
            html.AppendLine(".step-num{font-weight:600;display:inline-block;width:1.8rem;}");
            html.AppendLine("a{color:#0066cc;}");
            html.AppendLine("</style></head><body>");

            html.AppendLine("<div class='header'><h1>BIM Monkey</h1><p>Revit Plugin — Quick Start &amp; FAQ</p></div>");
            html.AppendLine("<div class='content'>");

            // ── Getting Started ────────────────────────────────────────────────
            html.AppendLine("<h2>Getting Started</h2>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span> Open Revit and your project file.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span> The BIM Monkey server starts automatically when Revit loads. Confirm it's running via <strong>BIM Monkey tab → Server Control → Server Status</strong>.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span> Open Claude Code from <code>Documents\\BIM Monkey\\</code> and tell Claude to generate construction documents. Claude reads your model through the named pipe and calls the BIM Monkey API to produce a CD plan.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>4.</span> Claude executes the plan in three phases: <strong>Phase 1</strong> — sheets and view placements; <strong>Phase 2</strong> — section and assembly details; <strong>Phase 3</strong> — door schedule, window schedule, room finish schedule, and keynote legend.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>5.</span> Generated sheets are marked <code>*</code> in the Project Browser. Review results and add notes at <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a>.</p>");

            // ── What Claude generates ──────────────────────────────────────────
            html.AppendLine("<h2>What Claude Generates</h2>");
            html.AppendLine("<p class='q'>Sheets &amp; Views (Phase 1)</p><p class='a'>Cover sheet, floor plans, reflected ceiling plans, elevations, building sections, and detail sheets — populated with existing views from your model, matched to your firm's layout style.</p>");
            html.AppendLine("<p class='q'>Construction Details (Phase 2)</p><p class='a'>New drafting views drawn from scratch: wall-roof connections, foundation conditions, window/door head-sill-jamb details, parapet sections. Existing unplaced details are placed first before any new ones are generated.</p>");
            html.AppendLine("<p class='q'>Schedules (Phase 3)</p><p class='a'>Door schedule, window schedule, room finish schedule, and keynote legend — created as live Revit schedules and placed on the appropriate sheets (G1.xx, A5.xx, G0.xx) automatically.</p>");

            // ── Ribbon Buttons ─────────────────────────────────────────────────
            html.AppendLine("<h2>Ribbon Buttons</h2>");
            html.AppendLine("<p class='q'>Start Server</p><p class='a'>Starts the MCP pipe server so Claude can communicate with Revit. Runs automatically on Revit launch — only needed if the server was stopped manually.</p>");
            html.AppendLine("<p class='q'>Stop Server</p><p class='a'>Stops the pipe server. Use this before closing Revit or to reset a stale connection. Always Stop → Start after opening a different project file mid-session.</p>");
            html.AppendLine("<p class='q'>Server Status</p><p class='a'>Shows whether the server is running, the pipe name (<code>RevitMCPBridge2026</code>), and active connection count.</p>");
            html.AppendLine("<p class='q'>Standards</p><p class='a'>Fetches your firm's library score from the BIM Monkey API — pages analyzed, projects uploaded, detail type coverage, and score breakdown.</p>");
            html.AppendLine("<p class='q'>Platform</p><p class='a'>Opens <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a> in your browser — review runs, upload CD sets, view your training library.</p>");
            html.AppendLine("<p class='q'>FAQ</p><p class='a'>Opens this page.</p>");

            // ── Training Library ───────────────────────────────────────────────
            html.AppendLine("<h2>Training Library</h2>");
            html.AppendLine("<p class='q'>What should I upload?</p><p class='a'>100% completed Construction Document sets only — permit-ready drawings, not works in progress. The quality of uploads directly determines the quality of generated output. Works in progress degrade results.</p>");
            html.AppendLine("<p class='q'>How do I upload?</p><p class='a'>Go to <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a> → Upload tab. Drop in a PDF, select building type, click Analyze. Claude reads every page and adds it to your library automatically — no review step required.</p>");
            html.AppendLine("<p class='q'>Does generated output feed back into the library?</p><p class='a'>Yes. Every CD set Claude generates is saved and used as a reference for future runs. The more you run it, the better it matches your standards.</p>");
            html.AppendLine("<p class='q'>How do I see my library health?</p><p class='a'>Click <strong>Standards</strong> in the ribbon or visit the Standards tab at app.bimmonkey.ai. Your library score (0–100) shows coverage, depth, and breadth across detail types.</p>");

            // ── Troubleshooting ────────────────────────────────────────────────
            html.AppendLine("<h2>Troubleshooting</h2>");

            html.AppendLine("<p class='q'>Claude can't connect to Revit / server isn't responding.</p>");
            html.AppendLine("<p class='a'>The server must be running before Claude can send any commands. Reset it:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span> BIM Monkey tab → Server Control → click <strong>Stop Server</strong>, wait 2 seconds</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span> Click <strong>Start Server</strong> — status should turn green</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span> Return to Claude Code and retry</p>");

            html.AppendLine("<p class='q'>I opened a different Revit file and Claude is still reading the old one.</p>");
            html.AppendLine("<p class='a'>The server binds to the document open at startup. You must restart it every time you switch files:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span> Open your new project file in Revit</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span> BIM Monkey tab → <strong>Stop Server</strong> then <strong>Start Server</strong></p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span> Claude will now read from the new active document</p>");

            html.AppendLine("<p class='q'>Detail sheets (A4.xx) are empty after generation.</p>");
            html.AppendLine("<p class='a'>Phase 2 (detail drafting views) was skipped or failed. This is the most common generation issue. In Claude Code, after Phase 1 completes, explicitly tell Claude: <em>\"Now execute Phase 2 — create and place all detail drafting views.\"</em> Phase 2 must always run after Phase 1.</p>");

            html.AppendLine("<p class='q'>Schedule sheets are empty after generation.</p>");
            html.AppendLine("<p class='a'>Phase 3 (door schedule, window schedule, room finish, keynote legend) was skipped. Tell Claude: <em>\"Execute Phase 3 — create all schedules and place them on their sheets.\"</em></p>");

            html.AppendLine("<p class='q'>Views on a detail sheet are all stacked on top of each other.</p>");
            html.AppendLine("<p class='a'>This happens when too many viewports are placed on one sheet, or when Phase 1 and Phase 2 both target the same sheet. Each detail sheet holds a maximum of 6 viewports comfortably. If you see stacking, run the generation again — the plan will split details across A4.02, A4.03, etc. as needed.</p>");

            html.AppendLine("<p class='q'>Claude Code doesn't see the Revit tools.</p>");
            html.AppendLine("<p class='a'>Claude Code must be opened from <code>Documents\\BIM Monkey\\</code> — the MCP config that connects to Revit lives there.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span> Close any existing Claude Code session</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span> Open Claude Code and set working directory to <code>Documents\\BIM Monkey</code></p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span> Confirm <code>revit-bridge</code> shows as connected in Claude's tool list</p>");

            html.AppendLine("<p class='q'>Commands time out.</p><p class='a'>Revit must not have any modal dialogs open. Dismiss all dialogs, click in the drawing area to give Revit focus, then retry the command.</p>");

            html.AppendLine("<p class='q'>Generation starts but nothing is created.</p>");
            html.AppendLine("<p class='a'>Usually a missing API key or unreachable server. Check:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span> Open <code>Documents\\BIM Monkey\\CLAUDE.md</code> — confirm <code>BIM_MONKEY_API_KEY=bm_...</code> is present</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span> Confirm active internet connection</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span> Stop → Start the server and retry</p>");

            html.AppendLine("<p class='q'>Revit warns about duplicate Type Mark values.</p><p class='a'>This is a standard Revit model quality warning unrelated to BIM Monkey. Revit is flagging elements in your model that share a Type Mark parameter. Dismiss it safely.</p>");

            html.AppendLine("<p class='q'>API key rejected.</p><p class='a'>Keys start with <code>bm_</code> and are emailed on signup. Re-run the installer to re-enter your key, or email <a href='mailto:hello@bimmonkey.ai'>hello@bimmonkey.ai</a>.</p>");

            html.AppendLine("<p class='q'>Installer blocked by Windows / antivirus.</p>");
            html.AppendLine("<p class='a'>The installer is not yet code-signed. To bypass SmartScreen:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span> Right-click the downloaded file → Properties → check <strong>Unblock</strong></p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span> Run <code>BimMonkeySetup.exe</code> → click <strong>More Info → Run Anyway</strong></p>");

            // ── Support ────────────────────────────────────────────────────────
            html.AppendLine("<h2>Support</h2>");
            html.AppendLine("<p class='a'>Email <a href='mailto:hello@bimmonkey.ai'>hello@bimmonkey.ai</a> or visit <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a>.</p>");
            html.AppendLine("</div></body></html>");

            return html.ToString();
        }
    }
}
