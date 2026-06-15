using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP methods for a roof TYPE's compound structure — the sheeting / underlayment /
    /// roofing layers that sit on top of the trusses. (The trusses carry the load; the roof
    /// element is the layered assembly: structural deck / sheathing / membrane / shingles.)
    /// </summary>
    public static class RoofLayerMethods
    {
        private static string Err(string m) => JsonConvert.SerializeObject(new { success = false, error = m });

        private static MaterialFunctionAssignment MapFunction(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "structure": case "structural": return MaterialFunctionAssignment.Structure;
                case "substrate": case "sheathing": case "deck": case "sheeting": return MaterialFunctionAssignment.Substrate;
                case "insulation": case "thermal": return MaterialFunctionAssignment.Insulation;
                case "membrane": case "underlayment": return MaterialFunctionAssignment.Membrane;
                case "finish1": case "finish": case "roofing": case "shingles": return MaterialFunctionAssignment.Finish1;
                case "finish2": return MaterialFunctionAssignment.Finish2;
                default: return MaterialFunctionAssignment.Substrate;
            }
        }

        [MCPMethod("setRoofLayers", Category = "Architecture",
            Description = "Sets the compound-structure layers (sheathing / membrane / shingles) on a roof type. Params: roofTypeId (int), layers: array of {function (Structure|Substrate/Sheathing|Insulation|Membrane|Finish1/Shingles|Finish2), thickness (ft), materialId (int, optional)} ordered exterior->interior.")]
        public static string SetRoofLayers(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                if (parameters["roofTypeId"] == null) return Err("roofTypeId is required");
                if (parameters["layers"] == null) return Err("layers array is required");

                var roofType = doc.GetElement(new ElementId(parameters["roofTypeId"].ToObject<int>())) as RoofType;
                if (roofType == null) return Err("roofTypeId is not a RoofType");

                var layerDefs = parameters["layers"] as JArray;
                if (layerDefs == null || layerDefs.Count == 0) return Err("layers array is empty");

                var layers = new List<CompoundStructureLayer>();
                int structuralIndex = -1;
                int i = 0;
                foreach (var ld in layerDefs)
                {
                    double th = ld["thickness"]?.ToObject<double>() ?? (1.0 / 12.0);
                    var fn = MapFunction(ld["function"]?.ToObject<string>());
                    ElementId matId = ld["materialId"] != null
                        ? new ElementId(ld["materialId"].ToObject<int>())
                        : ElementId.InvalidElementId;
                    layers.Add(new CompoundStructureLayer(th, fn, matId));
                    if (fn == MaterialFunctionAssignment.Structure && structuralIndex < 0) structuralIndex = i;
                    i++;
                }

                ElementId resultId;
                using (var trans = new Transaction(doc, "Set Roof Layers"))
                {
                    trans.Start();
                    CompoundStructure cs = CompoundStructure.CreateSimpleCompoundStructure(layers);
                    if (structuralIndex >= 0)
                    {
                        try { cs.StructuralMaterialIndex = structuralIndex; } catch { }
                    }
                    roofType.SetCompoundStructure(cs);
                    resultId = roofType.Id;
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roofTypeId = resultId.Value,
                    roofTypeName = roofType.Name,
                    layerCount = layers.Count
                });
            }
            catch (Exception ex) { return ResponseBuilder.FromException(ex).Build(); }
        }
    }
}
