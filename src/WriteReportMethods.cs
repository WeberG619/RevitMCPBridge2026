using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Write a report / data to a file on the Desktop and open it. Powers "give me a PDF /
    /// Word / Excel of this." pdf -> real PDF via headless Edge/Chrome (no library needed);
    /// word -> .doc (Word opens HTML); excel/csv -> .csv (Excel opens it); html/txt/md too.
    /// </summary>
    public static class WriteReportMethods
    {
        [MCPMethod("writeReport", Category = "Export",
            Description = "Write a report or data to a file on the Desktop and open it. format: pdf | word | excel | csv | html | txt | md. Provide 'title' and 'content' (the full report text; for excel/csv provide comma-separated rows, one per line). Returns the file path.")]
        public static string WriteReport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string format = (parameters["format"]?.ToString() ?? "html").Trim().ToLowerInvariant();
                string title = parameters["title"]?.ToString();
                if (string.IsNullOrWhiteSpace(title)) title = "Report";
                string content = parameters["content"]?.ToString() ?? parameters["body"]?.ToString() ?? "";

                string safe = title;
                foreach (var ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
                string baseName = safe + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                string path;
                bool isPdf = false;
                switch (format)
                {
                    case "csv":
                    case "excel":
                    case "xlsx":
                        path = Path.Combine(dir, baseName + ".csv");
                        File.WriteAllText(path, content, Encoding.UTF8);
                        break;
                    case "word":
                    case "doc":
                    case "docx":
                        path = Path.Combine(dir, baseName + ".doc");      // Word opens HTML-as-doc
                        File.WriteAllText(path, HtmlWrap(title, content), Encoding.UTF8);
                        break;
                    case "txt":
                        path = Path.Combine(dir, baseName + ".txt");
                        File.WriteAllText(path, content, Encoding.UTF8);
                        break;
                    case "md":
                    case "markdown":
                        path = Path.Combine(dir, baseName + ".md");
                        File.WriteAllText(path, content, Encoding.UTF8);
                        break;
                    case "pdf":
                        isPdf = true;
                        path = Path.Combine(dir, baseName + ".html");      // write html, convert below
                        File.WriteAllText(path, HtmlWrap(title, content), Encoding.UTF8);
                        break;
                    default:
                        path = Path.Combine(dir, baseName + ".html");
                        File.WriteAllText(path, HtmlWrap(title, content), Encoding.UTF8);
                        break;
                }

                string finalPath = path;
                if (isPdf)
                {
                    string pdfPath = Path.ChangeExtension(path, ".pdf");
                    if (TryHtmlToPdf(path, pdfPath))
                    {
                        finalPath = pdfPath;
                        try { File.Delete(path); } catch { }
                    }
                    // else: fall back to opening the HTML (user can print to PDF)
                }

                try { Process.Start(new ProcessStartInfo { FileName = finalPath, UseShellExecute = true }); } catch { }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = finalPath,
                    format,
                    message = "Report written to " + finalPath + (isPdf && !finalPath.EndsWith(".pdf") ? " (PDF converter unavailable — opened HTML; use Print > Save as PDF)" : "")
                });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }

        private static string HtmlWrap(string title, string body)
        {
            string esc = System.Net.WebUtility.HtmlEncode(body ?? "");
            esc = System.Text.RegularExpressions.Regex.Replace(esc, @"\*\*(.+?)\*\*", "<b>$1</b>");   // **bold**
            esc = esc.Replace("\r\n", "\n").Replace("\n", "<br>\n");
            string t = System.Net.WebUtility.HtmlEncode(title);
            return "<!doctype html><html><head><meta charset='utf-8'><title>" + t + "</title><style>" +
                "body{font-family:'Segoe UI',Arial,sans-serif;margin:40px;color:#222;line-height:1.55}" +
                "h1{font-size:20px;border-bottom:2px solid #444;padding-bottom:6px;margin-bottom:4px}" +
                ".meta{color:#888;font-size:12px;margin-bottom:22px}" +
                "</style></head><body><h1>" + t + "</h1><div class='meta'>Generated " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "</div><div>" + esc + "</div></body></html>";
        }

        private static bool TryHtmlToPdf(string htmlPath, string pdfPath)
        {
            string[] exes =
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            };
            foreach (var exe in exes)
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "--headless=new --disable-gpu --no-pdf-header-footer --print-to-pdf=\"" + pdfPath + "\" \"file:///" + htmlPath.Replace("\\", "/") + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var p = Process.Start(psi);
                    if (p != null && p.WaitForExit(12000) && File.Exists(pdfPath)) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
