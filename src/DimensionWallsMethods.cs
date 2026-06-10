using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPBridge
{
    /// <summary>
    /// Dimension a set of walls to their face (Weber's standard = exterior CORE-FINISH face,
    /// never centerline). Thin orchestrator over the proven createDimensionString — builds the
    /// wall_face references in order, then calls it. The FACE correctness is Weber's standard
    /// to verify; this just wires the capability. Read-write.
    /// </summary>
    public static class DimensionWallsMethods
    {
        [MCPMethod("dimensionWalls", Category = "Dimensioning",
            Description = "Create ONE dimension string across several walls, dimensioning to their face. Params: viewId, wallIds (int array, 2+), side ('exterior'|'interior', default exterior), direction ('horizontal'|'vertical', default horizontal), offset (feet, default 5). Follows the standard of dimensioning to the wall face, not centerline.")]
        public static string DimensionWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["viewId"] == null) return JsonConvert.SerializeObject(new { success = false, error = "viewId is required" });
                int viewId = parameters["viewId"].ToObject<int>();
                var wallIdsTok = parameters["wallIds"] as JArray;
                if (wallIdsTok == null || wallIdsTok.Count < 2)
                    return JsonConvert.SerializeObject(new { success = false, error = "need at least 2 wallIds" });

                string side = (parameters["side"]?.ToString() ?? "exterior").ToLowerInvariant();
                string direction = (parameters["direction"]?.ToString() ?? "horizontal").ToLowerInvariant();
                double offset = parameters["offset"]?.ToObject<double>() ?? 5.0;

                // order walls along the axis the dimension runs (horizontal -> by X, vertical -> by Y)
                var walls = wallIdsTok.Select(t => doc.GetElement(new ElementId(t.ToObject<int>())) as Wall)
                                      .Where(w => w != null).ToList();
                Func<Wall, double> key = w =>
                {
                    var lc = (w.Location as LocationCurve)?.Curve;
                    if (lc == null) return 0;
                    var mid = lc.Evaluate(0.5, true);
                    return direction == "vertical" ? mid.Y : mid.X;
                };
                walls = walls.OrderBy(key).ToList();

                var refPoints = new JArray();
                foreach (var w in walls)
                    refPoints.Add(new JObject { ["type"] = "wall_face", ["elementId"] = (int)w.Id.Value, ["side"] = side });

                var p = new JObject
                {
                    ["viewId"] = viewId,
                    ["referencePoints"] = refPoints,
                    ["direction"] = direction,
                    ["offset"] = offset
                };
                // delegate to the proven dimension-string method
                return DimensioningMethods.CreateDimensionString(uiApp, p);
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
