using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
                var faqPath = WriteFaqFile();
                Process.Start(new ProcessStartInfo(faqPath) { UseShellExecute = true });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                var dialog = new TaskDialog("bimmonkey.ai FAQ");
                dialog.MainContent = "Could not open FAQ file.\n\nVisit app.bimmonkey.ai for help.";
                dialog.ExpandedContent = ex.Message;
                dialog.Show();
                return Result.Succeeded;
            }
        }

        private string WriteFaqFile()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2026", "BimMonkey");

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "BimMonkey_FAQ.html");

            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            html.AppendLine("<title>bimmonkey.ai — FAQ</title>");
            html.AppendLine("<style>");
            html.AppendLine("body{font-family:'Segoe UI',Arial,sans-serif;margin:0;background:#f5f5f5;color:#111;}");
            html.AppendLine(".header{background:#000;color:#f5f5f5;padding:2rem 2.5rem;letter-spacing:-0.02em;}");
            html.AppendLine(".header h1{margin:0;font-size:1.6rem;font-weight:300;}");
            html.AppendLine(".header p{margin:0.4rem 0 0;font-size:0.9rem;color:#aaa;font-weight:300;}");
            html.AppendLine(".content{max-width:740px;margin:2rem auto;padding:0 1.5rem;}");
            html.AppendLine("h2{font-size:1rem;font-weight:600;letter-spacing:0.04em;text-transform:uppercase;margin:2rem 0 0.75rem;border-bottom:1px solid #ddd;padding-bottom:0.4rem;}");
            html.AppendLine(".q{font-weight:500;margin:1rem 0 0.25rem;}");
            html.AppendLine(".a{color:#444;font-weight:300;line-height:1.6;margin:0 0 0.75rem 0;}");
            html.AppendLine("code{background:#e8e8e8;padding:0.1rem 0.35rem;border-radius:3px;font-size:0.88em;}");
            html.AppendLine(".step{display:flex;gap:1rem;margin:0.5rem 0;}");
            html.AppendLine(".step-num{font-size:1.1rem;font-weight:600;min-width:1.5rem;}");
            html.AppendLine("a{color:#000;}");
            html.AppendLine("</style></head><body>");

            html.AppendLine("<div class='header'>");
            html.AppendLine("<h1>bimmonkey.ai</h1>");
            html.AppendLine("<p>Revit Plugin — Quick Start &amp; FAQ</p>");
            html.AppendLine("</div>");
            html.AppendLine("<div class='content'>");

            html.AppendLine("<h2>Getting Started</h2>");
            html.AppendLine("<div class='step'><div class='step-num'>1.</div><div>Open Revit and your project file.</div></div>");
            html.AppendLine("<div class='step'><div class='step-num'>2.</div><div>In the <strong>bimmonkey.ai</strong> ribbon tab, click <strong>Start Server</strong>. The server auto-starts on Revit launch, so this is usually already done.</div></div>");
            html.AppendLine("<div class='step'><div class='step-num'>3.</div><div>Open Claude Desktop (or Claude Code) and run your CD generation script. Claude connects to Revit through the named pipe <code>\\\\.\\pipe\\RevitMCPBridge2026</code>.</div></div>");
            html.AppendLine("<div class='step'><div class='step-num'>4.</div><div>When the run completes, log into <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a> to upload completed CD sets to your training library.</div></div>");

            html.AppendLine("<h2>Ribbon Buttons</h2>");
            html.AppendLine("<p class='q'>Start Server</p><p class='a'>Starts the MCP pipe server so Claude can communicate with Revit. Happens automatically when Revit opens — only click this if you stopped it manually.</p>");
            html.AppendLine("<p class='q'>Stop Server</p><p class='a'>Stops the pipe server. Use this before closing Revit or if you need to restart the connection.</p>");
            html.AppendLine("<p class='q'>Server Status</p><p class='a'>Shows whether the server is running, the pipe name, and active connection count.</p>");
            html.AppendLine("<p class='q'>FAQ</p><p class='a'>Opens this page.</p>");

            html.AppendLine("<h2>Training Library</h2>");
            html.AppendLine("<p class='q'>What should I upload?</p><p class='a'>Upload 100% completed Construction Document sets only — permit-ready drawings, not works in progress. The quality of your uploads directly determines the quality of generated output.</p>");
            html.AppendLine("<p class='q'>How do I upload?</p><p class='a'>Go to <a href='https://app.bimmonkey.ai/upload'>app.bimmonkey.ai/upload</a>, drop in a PDF of your CD set, add the project name, building type, and any tags, then click Analyze PDF. Claude reads each page and adds it to your library automatically.</p>");
            html.AppendLine("<p class='q'>How long does uploading take?</p><p class='a'>PDF rendering in your browser takes a few seconds per page. Claude analysis typically takes 30–90 seconds for the full set. You can navigate away from the upload page while it runs.</p>");
            html.AppendLine("<p class='q'>Does output from Revit get added to the library?</p><p class='a'>Yes. Every CD set generated through the plugin feeds back into your training library, so the more you use it the better it gets.</p>");

            html.AppendLine("<h2>Troubleshooting</h2>");
            html.AppendLine("<p class='q'>Claude says it can't connect to Revit.</p><p class='a'>Click <strong>Server Status</strong> in the ribbon. If the server is stopped, click <strong>Start Server</strong>. Make sure Revit has an active project open — the server needs a document loaded to respond.</p>");
            html.AppendLine("<p class='q'>The server starts but commands time out.</p><p class='a'>Revit must not have any modal dialogs open (save prompts, warning dialogs, etc.). Dismiss any dialogs and click in the Revit drawing area to give it focus, then retry.</p>");
            html.AppendLine("<p class='q'>I uploaded a document but it's not showing in the Library.</p><p class='a'>The page count only updates after Claude finishes analyzing all pages. Refresh the Library page or wait for the progress bar to complete.</p>");
            html.AppendLine("<p class='q'>I need to remove a project from the library.</p><p class='a'>Go to <a href='https://app.bimmonkey.ai/library'>app.bimmonkey.ai/library</a> and click Delete next to the project.</p>");

            html.AppendLine("<h2>Support</h2>");
            html.AppendLine("<p class='a'>For help, visit <a href='https://app.bimmonkey.ai'>app.bimmonkey.ai</a> or contact your administrator.</p>");

            html.AppendLine("</div></body></html>");

            File.WriteAllText(path, html.ToString(), Encoding.UTF8);
            return path;
        }
    }
}
