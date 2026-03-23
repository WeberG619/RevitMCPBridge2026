using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Serilog;

namespace RevitMCPBridge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StandardsCommand : IExternalCommand
    {
        private const string ApiBase = "https://bimmonkey-production.up.railway.app";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var apiKey = ReadApiKey();
                if (string.IsNullOrEmpty(apiKey))
                {
                    TaskDialog.Show("BIM Monkey", "API key not found.\n\nMake sure BIM_MONKEY_API_KEY is set in Documents\\BIM Monkey\\CLAUDE.md.");
                    return Result.Succeeded;
                }

                var json = FetchStandards(apiKey);
                if (json == null)
                {
                    TaskDialog.Show("BIM Monkey", "Could not reach BIM Monkey. Check your internet connection.");
                    return Result.Succeeded;
                }

                ShowStatsDialog(json);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StandardsCommand failed");
                TaskDialog.Show("BIM Monkey", $"Error fetching standards: {ex.Message}");
                return Result.Succeeded;
            }
        }

        private static string ReadApiKey()
        {
            var claudeMd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "BIM Monkey", "CLAUDE.md");

            if (!File.Exists(claudeMd)) return null;

            foreach (var line in File.ReadAllLines(claudeMd))
            {
                if (line.StartsWith("BIM_MONKEY_API_KEY="))
                    return line.Substring("BIM_MONKEY_API_KEY=".Length).Trim();
            }
            return null;
        }

        private static JObject FetchStandards(string apiKey)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    var response = client.GetAsync($"{ApiBase}/api/standards").GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode) return null;
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return JObject.Parse(body);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to fetch standards from API");
                return null;
            }
        }

        private static void ShowStatsDialog(JObject d)
        {
            var score       = d["libraryScore"]?.Value<int>() ?? 0;
            var pages       = d["totalPages"]?.Value<int>() ?? 0;
            var projects    = d["totalProjects"]?.Value<int>() ?? 0;
            var generations = d["totalGenerations"]?.Value<int>() ?? 0;
            var covered     = d["typesWithCoverage"]?.Value<int>() ?? 0;
            var total       = d["totalDetailTypes"]?.Value<int>() ?? 0;

            var breakdown = d["libraryScoreBreakdown"];
            var covPts  = breakdown?["coveragePts"]?.Value<int>() ?? 0;
            var depPts  = breakdown?["depthPts"]?.Value<int>() ?? 0;
            var projPts = breakdown?["projectPts"]?.Value<int>() ?? 0;

            var sb = new StringBuilder();
            sb.AppendLine($"Library Score:      {score} / 100");
            sb.AppendLine();
            sb.AppendLine($"Pages analyzed:     {pages:N0}");
            sb.AppendLine($"Projects:           {projects:N0}");
            sb.AppendLine($"Detail types:       {covered} / {total}");
            sb.AppendLine($"Generations run:    {generations:N0}");
            sb.AppendLine();
            sb.AppendLine("Score breakdown:");
            sb.AppendLine($"  Coverage:  {covPts} / 40");
            sb.AppendLine($"  Depth:     {depPts} / 40");
            sb.AppendLine($"  Breadth:   {projPts} / 20");

            var gaps = new List<string>();
            var detailCoverage = d["detailCoverage"] as JArray;
            if (detailCoverage != null)
                foreach (var item in detailCoverage)
                    if (item["tier"]?.Value<string>() == "none")
                        gaps.Add(item["detailType"]?.Value<string>() ?? "");

            if (gaps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Missing coverage:");
                sb.AppendLine("  " + string.Join(", ", gaps));
            }

            var dialog = new TaskDialog("BIM Monkey — Standards");
            dialog.MainContent = sb.ToString();
            dialog.MainIcon = TaskDialogIcon.TaskDialogIconInformation;
            dialog.Show();
        }
    }
}
