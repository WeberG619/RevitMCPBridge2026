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

            html.AppendLine("<h2>Getting Started</h2>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span>Open Revit and your project file.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span>In the <strong>BIM Monkey</strong> ribbon tab, click <strong>Start Server</strong>. The server auto-starts on Revit launch — this is usually already done.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span>Open Claude Code in your <strong>BIM Monkey</strong> folder and ask Claude to generate construction documents. Claude connects to Revit via the named pipe <code>\\\\.\\pipe\\RevitMCPBridge2026</code>.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>4.</span>Generated sheets are marked with <code> *</code> in the Revit Project Browser so you can tell them apart from your existing sheets.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>5.</span>Log into <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a> to review output and upload completed CD sets to your training library.</p>");

            html.AppendLine("<h2>Ribbon Buttons</h2>");
            html.AppendLine("<p class='q'>Start Server</p><p class='a'>Starts the MCP pipe server so Claude can communicate with Revit. Happens automatically when Revit opens.</p>");
            html.AppendLine("<p class='q'>Stop Server</p><p class='a'>Stops the pipe server. Use this before closing Revit or to reset a stale connection.</p>");
            html.AppendLine("<p class='q'>Server Status</p><p class='a'>Shows whether the server is running, the pipe name, and active connection count.</p>");
            html.AppendLine("<p class='q'>Platform</p><p class='a'>Opens the BIM Monkey dashboard at app.bimmonkey.ai in your browser.</p>");
            html.AppendLine("<p class='q'>FAQ</p><p class='a'>Opens this page.</p>");

            html.AppendLine("<h2>Training Library</h2>");
            html.AppendLine("<p class='q'>What should I upload?</p><p class='a'>Upload 100% completed Construction Document sets only — permit-ready drawings, not works in progress. The quality of your uploads directly determines the quality of generated output.</p>");
            html.AppendLine("<p class='q'>How do I upload?</p><p class='a'>Go to <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a>, drop in a PDF of your CD set, add the project name and building type, then click Analyze PDF.</p>");
            html.AppendLine("<p class='q'>Does output from Revit get added to the library?</p><p class='a'>Yes. Every CD set generated through the plugin feeds back into your training library automatically.</p>");

            html.AppendLine("<h2>Troubleshooting</h2>");

            html.AppendLine("<p class='q'>Claude says it can't connect to Revit / server isn't responding.</p>");
            html.AppendLine("<p class='a'>The BIM Monkey server needs to be running before Claude can talk to Revit. To restart it:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span>Go to the <strong>BIM Monkey</strong> tab → <strong>Server Control</strong> panel</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span>Click <strong>Stop Server</strong>, wait 2 seconds</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span>Click <strong>Start Server</strong> — the status indicator should turn green</p>");
            html.AppendLine("<p class='step'><span class='step-num'>4.</span>Return to Claude Code and retry your request</p>");

            html.AppendLine("<p class='q'>I opened a new Revit file mid-session and Claude is reading the old file.</p>");
            html.AppendLine("<p class='a'>The server stays bound to the document that was open when it started. After switching files, reset the connection:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span>Open your new Revit project file</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span>BIM Monkey tab → click <strong>Stop Server</strong> then <strong>Start Server</strong></p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span>Claude will now read from the new active document</p>");

            html.AppendLine("<p class='q'>Claude Code doesn't see the Revit tools (revit_execute not available).</p>");
            html.AppendLine("<p class='a'>Claude Code <strong>must be opened from the BIM Monkey folder</strong> — not from a general terminal or another project. The MCP config that connects to Revit lives in <code>Documents\\BIM Monkey\\</code>.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span>Close any existing Claude Code session</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span>In Revit, go to <strong>BIM Monkey tab</strong> and click the <strong>Claude Code</strong> button — this opens Claude Code pointed at the correct folder automatically</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span>Alternatively, open Claude Code manually and navigate to <code>Documents\\BIM Monkey</code> as the working directory</p>");
            html.AppendLine("<p class='step'><span class='step-num'>4.</span>Verify the <code>revit-bridge</code> MCP server shows as connected in Claude's tool list</p>");

            html.AppendLine("<p class='q'>Commands time out.</p><p class='a'>Revit must not have any modal dialogs open. Dismiss any dialogs, click in the drawing area to give Revit focus, then retry.</p>");

            html.AppendLine("<p class='q'>Generation runs but produces no sheets / everything fails immediately.</p>");
            html.AppendLine("<p class='a'>Usually means the API key is missing or the BIM Monkey server is unreachable. Check:</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span>Open <code>Documents\\BIM Monkey\\CLAUDE.md</code> and confirm the line <code>BIM_MONKEY_API_KEY=bm_...</code> is present</p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span>Make sure you have an active internet connection</p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span>Restart the server (Stop → Start) and retry</p>");

            html.AppendLine("<p class='q'>Revit shows a warning about duplicate Type Mark values.</p><p class='a'>This is a standard Revit model quality warning — it has nothing to do with BIM Monkey or the plugin. Revit is flagging elements in your model that share a Type Mark parameter. You can safely dismiss it.</p>");

            html.AppendLine("<p class='q'>A sheet was created but views are missing or placement failed.</p>");
            html.AppendLine("<p class='a'>In Revit, each view can only be placed on one sheet at a time. Claude handles this automatically by duplicating views when needed, but some complex cases may fail. Check the System Report on app.bimmonkey.ai for details on what failed and why, including specific recommendations.</p>");

            html.AppendLine("<p class='q'>API key rejected.</p><p class='a'>Keys start with <code>bm_</code>. Re-run the installer to re-enter your key, or email <a href='mailto:hello@bimmonkey.ai'>hello@bimmonkey.ai</a>.</p>");

            html.AppendLine("<p class='q'>The installer was blocked by Windows / antivirus.</p>");
            html.AppendLine("<p class='a'>The installer is not yet code-signed. Windows SmartScreen may block it.</p>");
            html.AppendLine("<p class='step'><span class='step-num'>1.</span>Right-click the downloaded .zip → Properties → check <strong>Unblock</strong></p>");
            html.AppendLine("<p class='step'><span class='step-num'>2.</span>Extract, right-click <code>BimMonkeySetup.exe</code> → Properties → <strong>Unblock</strong></p>");
            html.AppendLine("<p class='step'><span class='step-num'>3.</span>Run installer → click <strong>More Info → Run Anyway</strong> if SmartScreen appears</p>");

            html.AppendLine("<h2>Support</h2>");
            html.AppendLine("<p class='a'>Email <a href='mailto:hello@bimmonkey.ai'>hello@bimmonkey.ai</a> or visit <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a>.</p>");
            html.AppendLine("</div></body></html>");

            return html.ToString();
        }
    }
}
