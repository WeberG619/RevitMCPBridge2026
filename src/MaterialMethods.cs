using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Revit Materials
    /// Handles material creation, modification, appearance assets, and material management
    /// </summary>
    public static class MaterialMethods
    {
        #region Material Creation and Management

        /// <summary>
        /// Creates a new material in the project
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing materialName, properties</param>
        /// <returns>JSON response with success status and material ID</returns>
        [MCPMethod("createMaterial", Category = "Material", Description = "Creates a new material in the project")]
        public static string CreateMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createMaterial");
                v.Require("materialName");
                v.ThrowIfInvalid();

                string materialName = v.GetRequired<string>("materialName");

                using (var trans = new Transaction(doc, "Create Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create material
                    ElementId materialId = Material.Create(doc, materialName);
                    Material material = doc.GetElement(materialId) as Material;

                    // Optional: Set color if provided
                    if (parameters["color"] != null)
                    {
                        var colorArray = parameters["color"].ToObject<int[]>();
                        material.Color = new Color((byte)colorArray[0], (byte)colorArray[1], (byte)colorArray[2]);
                    }

                    // Optional: Set transparency if provided
                    if (parameters["transparency"] != null)
                    {
                        int transparency = parameters["transparency"].ToObject<int>();
                        material.Transparency = transparency;
                    }

                    // Optional: Set shininess if provided
                    if (parameters["shininess"] != null)
                    {
                        int shininess = parameters["shininess"].ToObject<int>();
                        material.Shininess = shininess;
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("materialId", (int)materialId.Value)
                        .With("materialName", material.Name)
                        .With("message", "Material created successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all materials in the project
        /// </summary>
        [MCPMethod("getAllMaterials", Category = "Material", Description = "Gets all materials in the project")]
        public static string GetAllMaterials(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var materials = new List<object>();
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material));

                // Optional filter by class
                string filterClass = parameters["materialClass"]?.ToString();

                foreach (Material material in collector)
                {
                    // Skip if filter is provided and doesn't match
                    if (!string.IsNullOrEmpty(filterClass) && material.MaterialClass != filterClass)
                        continue;

                    materials.Add(new
                    {
                        materialId = (int)material.Id.Value,
                        name = material.Name,
                        materialClass = material.MaterialClass ?? "",
                        color = new[] { material.Color.Red, material.Color.Green, material.Color.Blue },
                        transparency = material.Transparency,
                        shininess = material.Shininess
                    });
                }

                return ResponseBuilder.Success()
                    .With("count", materials.Count)
                    .With("materials", materials)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets detailed information about a material
        /// </summary>
        [MCPMethod("getMaterialInfo", Category = "Material", Description = "Gets detailed information about a material")]
        public static string GetMaterialInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                Material material = null;

                // Get material by ID or name
                if (parameters["materialId"] != null)
                {
                    int materialIdInt = parameters["materialId"].ToObject<int>();
                    material = doc.GetElement(new ElementId(materialIdInt)) as Material;
                }
                else if (parameters["materialName"] != null)
                {
                    string materialName = parameters["materialName"].ToString();
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(Material));

                    foreach (Material mat in collector)
                    {
                        if (mat.Name == materialName)
                        {
                            material = mat;
                            break;
                        }
                    }
                }
                else
                {
                    return ResponseBuilder.Error("Either materialId or materialName is required").Build();
                }

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                return ResponseBuilder.Success()
                    .With("materialId", (int)material.Id.Value)
                    .With("name", material.Name)
                    .With("materialClass", material.MaterialClass ?? "")
                    .With("color", new[] { material.Color.Red, material.Color.Green, material.Color.Blue })
                    .With("transparency", material.Transparency)
                    .With("shininess", material.Shininess)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modifies material properties
        /// </summary>
        [MCPMethod("modifyMaterial", Category = "Material", Description = "Modifies material properties")]
        public static string ModifyMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "modifyMaterial");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Modify Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Modify name if provided
                    if (parameters["name"] != null)
                    {
                        material.Name = parameters["name"].ToString();
                    }

                    // Modify color if provided
                    if (parameters["color"] != null)
                    {
                        var colorArray = parameters["color"].ToObject<int[]>();
                        material.Color = new Color((byte)colorArray[0], (byte)colorArray[1], (byte)colorArray[2]);
                    }

                    // Modify transparency if provided
                    if (parameters["transparency"] != null)
                    {
                        int transparency = parameters["transparency"].ToObject<int>();
                        material.Transparency = transparency;
                    }

                    // Modify shininess if provided
                    if (parameters["shininess"] != null)
                    {
                        int shininess = parameters["shininess"].ToObject<int>();
                        material.Shininess = shininess;
                    }

                    // Modify material class if provided
                    if (parameters["materialClass"] != null)
                    {
                        material.MaterialClass = parameters["materialClass"].ToString();
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("materialId", (int)material.Id.Value)
                        .With("materialName", material.Name)
                        .With("message", "Material modified successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicates a material
        /// </summary>
        [MCPMethod("duplicateMaterial", Category = "Material", Description = "Duplicates an existing material")]
        public static string DuplicateMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "duplicateMaterial");
                v.Require("materialId").IsType<int>();
                v.Require("newName");
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                string newName = v.GetRequired<string>("newName");

                using (var trans = new Transaction(doc, "Duplicate Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    Material newMaterial = material.Duplicate(newName);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("originalMaterialId", (int)material.Id.Value)
                        .With("newMaterialId", (int)newMaterial.Id.Value)
                        .With("newMaterialName", newMaterial.Name)
                        .With("message", "Material duplicated successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Deletes a material
        /// </summary>
        [MCPMethod("deleteMaterial", Category = "Material", Description = "Deletes a material from the project")]
        public static string DeleteMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "deleteMaterial");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                ElementId materialId = new ElementId(materialIdInt);
                Material material = doc.GetElement(materialId) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                string materialName = material.Name;

                using (var trans = new Transaction(doc, "Delete Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Attempt to delete
                    ICollection<ElementId> deletedIds = doc.Delete(materialId);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("materialId", materialIdInt)
                        .With("materialName", materialName)
                        .With("deletedCount", deletedIds.Count)
                        .With("message", "Material deleted successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Appearance

        /// <summary>
        /// Sets material appearance properties
        /// </summary>
        [MCPMethod("setMaterialAppearance", Category = "Material", Description = "Sets material appearance properties")]
        public static string SetMaterialAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setMaterialAppearance");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                using (var trans = new Transaction(doc, "Set Material Appearance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get or create appearance asset
                    ElementId appearanceAssetId = material.AppearanceAssetId;

                    // Set UseRenderAppearanceForShading if provided
                    if (parameters["useRenderAppearance"] != null)
                    {
                        bool useRenderAppearance = parameters["useRenderAppearance"].ToObject<bool>();
                        material.UseRenderAppearanceForShading = useRenderAppearance;
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("materialId", materialIdInt)
                        .With("appearanceAssetId", appearanceAssetId != null ? (int?)appearanceAssetId.Value : null)
                        .With("message", "Material appearance updated successfully")
                        .With("note", "Advanced appearance asset editing requires additional asset manipulation")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets material appearance properties
        /// </summary>
        [MCPMethod("getMaterialAppearance", Category = "Material", Description = "Gets material appearance properties")]
        public static string GetMaterialAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getMaterialAppearance");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                ElementId appearanceAssetId = material.AppearanceAssetId;
                AppearanceAssetElement appearanceAsset = null;
                string appearanceAssetName = null;

                if (appearanceAssetId != null && appearanceAssetId != ElementId.InvalidElementId)
                {
                    appearanceAsset = doc.GetElement(appearanceAssetId) as AppearanceAssetElement;
                    if (appearanceAsset != null)
                    {
                        appearanceAssetName = appearanceAsset.Name;
                    }
                }

                return ResponseBuilder.Success()
                    .With("materialId", materialIdInt)
                    .With("materialName", material.Name)
                    .With("appearanceAssetId", appearanceAssetId != null ? (int?)appearanceAssetId.Value : null)
                    .With("appearanceAssetName", appearanceAssetName)
                    .With("useRenderAppearanceForShading", material.UseRenderAppearanceForShading)
                    .With("hasAppearanceAsset", appearanceAsset != null)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Sets material texture/image
        /// </summary>
        [MCPMethod("setMaterialTexture", Category = "Material", Description = "Sets the texture or image for a material")]
        public static string SetMaterialTexture(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setMaterialTexture");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                // API LIMITATION: Direct texture setting requires AppearanceAssetElement manipulation
                // which is complex and requires working with Asset properties
                return ResponseBuilder.Error("SetMaterialTexture requires complex AppearanceAssetElement manipulation not fully supported in this API version", "NOT_SUPPORTED")
                    .With("note", "Use Revit UI to set material textures, or use SetRenderAppearance to assign appearance assets")
                    .With("materialId", materialIdInt)
                    .With("materialName", material.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Sets material render appearance
        /// </summary>
        [MCPMethod("setRenderAppearance", Category = "Material", Description = "Sets material render appearance asset")]
        public static string SetRenderAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setRenderAppearance");
                v.Require("materialId").IsType<int>();
                v.Require("appearanceAssetId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                int appearanceAssetIdInt = v.GetRequired<int>("appearanceAssetId");
                ElementId appearanceAssetId = new ElementId(appearanceAssetIdInt);

                using (var trans = new Transaction(doc, "Set Render Appearance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    material.AppearanceAssetId = appearanceAssetId;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("materialId", materialIdInt)
                        .With("materialName", material.Name)
                        .With("appearanceAssetId", appearanceAssetIdInt)
                        .With("message", "Render appearance set successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Surface Patterns

        /// <summary>
        /// Sets material surface pattern for cut/surface
        /// </summary>
        [MCPMethod("setMaterialSurfacePattern", Category = "Material", Description = "Sets the surface pattern for a material")]
        public static string SetMaterialSurfacePattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setMaterialSurfacePattern");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                // API LIMITATION: Surface pattern properties (CutPatternId, SurfacePatternId, etc.)
                // were removed in Revit 2026 API
                return ResponseBuilder.Error("SetMaterialSurfacePattern not supported in Revit 2026 API", "NOT_SUPPORTED")
                    .With("note", "Surface pattern properties (CutPatternId, SurfacePatternId) were removed from Material class in Revit 2026")
                    .With("workaround", "Use Revit UI to set material surface patterns")
                    .With("materialId", materialIdInt)
                    .With("materialName", material.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets material surface pattern settings
        /// </summary>
        [MCPMethod("getMaterialSurfacePattern", Category = "Material", Description = "Gets surface pattern settings for a material")]
        public static string GetMaterialSurfacePattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getMaterialSurfacePattern");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                // API LIMITATION: Surface pattern properties were removed in Revit 2026
                return ResponseBuilder.Error("GetMaterialSurfacePattern not supported in Revit 2026 API", "NOT_SUPPORTED")
                    .With("note", "Surface pattern properties (CutPatternId, SurfacePatternId, CutPatternColor, SurfacePatternColor) were removed from Material class")
                    .With("workaround", "Surface patterns must be accessed through UI or use pre-2026 API")
                    .With("materialId", materialIdInt)
                    .With("materialName", material.Name)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Physical Properties

        /// <summary>
        /// Sets material physical/thermal properties
        /// </summary>
        [MCPMethod("setMaterialPhysicalProperties", Category = "Material", Description = "Sets physical and thermal properties for a material")]
        public static string SetMaterialPhysicalProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setMaterialPhysicalProperties");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                // API LIMITATION: Physical/thermal properties require complex PropertySetElement and Asset manipulation
                return ResponseBuilder.Error("SetMaterialPhysicalProperties requires complex Asset manipulation not fully supported", "NOT_SUPPORTED")
                    .With("note", "Physical properties require working with StructuralAsset and ThermalAsset classes through PropertySetElement")
                    .With("workaround", "Use Revit UI to set material physical/thermal properties, or use SetStructuralAssetId/SetThermalAssetId")
                    .With("materialId", materialIdInt)
                    .With("materialName", material.Name)
                    .With("structuralAssetId", material.StructuralAssetId != null ? (int?)material.StructuralAssetId.Value : null)
                    .With("thermalAssetId", material.ThermalAssetId != null ? (int?)material.ThermalAssetId.Value : null)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets material physical/thermal properties
        /// </summary>
        [MCPMethod("getMaterialPhysicalProperties", Category = "Material", Description = "Gets physical and thermal properties for a material")]
        public static string GetMaterialPhysicalProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getMaterialPhysicalProperties");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                // Get basic asset IDs
                ElementId structuralAssetId = material.StructuralAssetId;
                ElementId thermalAssetId = material.ThermalAssetId;

                return ResponseBuilder.Success()
                    .With("materialId", materialIdInt)
                    .With("materialName", material.Name)
                    .With("structuralAssetId", structuralAssetId != null ? (int?)structuralAssetId.Value : null)
                    .With("thermalAssetId", thermalAssetId != null ? (int?)thermalAssetId.Value : null)
                    .With("hasStructuralAsset", structuralAssetId != null && structuralAssetId != ElementId.InvalidElementId)
                    .With("hasThermalAsset", thermalAssetId != null && thermalAssetId != ElementId.InvalidElementId)
                    .With("note", "Detailed asset properties require PropertySetElement access - use structuralAssetId/thermalAssetId to retrieve full details")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Classes

        /// <summary>
        /// Gets all material classes
        /// </summary>
        [MCPMethod("getMaterialClasses", Category = "Material", Description = "Gets all material classes in the project")]
        public static string GetMaterialClasses(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all unique material classes from existing materials
                var materialClasses = new HashSet<string>();

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material));

                foreach (Material material in collector)
                {
                    if (!string.IsNullOrEmpty(material.MaterialClass))
                    {
                        materialClasses.Add(material.MaterialClass);
                    }
                }

                var sortedClasses = materialClasses.OrderBy(c => c).ToList();

                return ResponseBuilder.Success()
                    .With("count", sortedClasses.Count)
                    .With("materialClasses", sortedClasses)
                    .With("note", "Material classes are user-defined strings. Common values: Concrete, Masonry, Metal, Wood, Plastic, Glass, etc.")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Sets material class
        /// </summary>
        [MCPMethod("setMaterialClass", Category = "Material", Description = "Sets the class for a material")]
        public static string SetMaterialClass(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "setMaterialClass");
                v.Require("materialId").IsType<int>();
                v.Require("materialClass");
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return ResponseBuilder.Error("Material not found", "NOT_FOUND").Build();
                }

                string materialClass = v.GetRequired<string>("materialClass");

                using (var trans = new Transaction(doc, "Set Material Class"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    material.MaterialClass = materialClass;

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("materialId", materialIdInt)
                        .With("materialName", material.Name)
                        .With("materialClass", material.MaterialClass)
                        .With("message", "Material class set successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Usage

        /// <summary>
        /// Finds all elements using a material
        /// </summary>
        [MCPMethod("findElementsWithMaterial", Category = "Material", Description = "Finds all elements using a specific material")]
        public static string FindElementsWithMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "findElementsWithMaterial");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                ElementId materialId = new ElementId(materialIdInt);

                var elementsWithMaterial = new List<object>();

                // Collect all elements in the document
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType();

                foreach (Element elem in collector)
                {
                    // Check if element has the material
                    bool hasMaterial = false;

                    // Check MaterialId parameter
                    Parameter matParam = elem.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (matParam != null && matParam.AsElementId() == materialId)
                    {
                        hasMaterial = true;
                    }

                    // Check all material-related parameters
                    if (!hasMaterial)
                    {
                        foreach (Parameter param in elem.Parameters)
                        {
                            if (param.StorageType == StorageType.ElementId && param.AsElementId() == materialId)
                            {
                                hasMaterial = true;
                                break;
                            }
                        }
                    }

                    if (hasMaterial)
                    {
                        elementsWithMaterial.Add(new
                        {
                            elementId = (int)elem.Id.Value,
                            category = elem.Category?.Name ?? "None",
                            elementType = elem.GetType().Name
                        });
                    }
                }

                return ResponseBuilder.Success()
                    .With("materialId", materialIdInt)
                    .With("count", elementsWithMaterial.Count)
                    .With("elements", elementsWithMaterial)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Replaces material in all elements
        /// </summary>
        [MCPMethod("replaceMaterial", Category = "Material", Description = "Replaces a material across all elements that use it")]
        public static string ReplaceMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "replaceMaterial");
                v.Require("oldMaterialId").IsType<int>();
                v.Require("newMaterialId").IsType<int>();
                v.ThrowIfInvalid();

                int oldMaterialIdInt = v.GetRequired<int>("oldMaterialId");
                int newMaterialIdInt = v.GetRequired<int>("newMaterialId");
                ElementId oldMaterialId = new ElementId(oldMaterialIdInt);
                ElementId newMaterialId = new ElementId(newMaterialIdInt);

                int replacedCount = 0;

                using (var trans = new Transaction(doc, "Replace Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var collector = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType();

                    foreach (Element elem in collector)
                    {
                        // Check and replace MaterialId parameter
                        Parameter matParam = elem.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (matParam != null && !matParam.IsReadOnly && matParam.AsElementId() == oldMaterialId)
                        {
                            matParam.Set(newMaterialId);
                            replacedCount++;
                        }

                        // Check and replace all material-related parameters
                        foreach (Parameter param in elem.Parameters)
                        {
                            if (!param.IsReadOnly && param.StorageType == StorageType.ElementId && param.AsElementId() == oldMaterialId)
                            {
                                param.Set(newMaterialId);
                                replacedCount++;
                            }
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("oldMaterialId", oldMaterialIdInt)
                    .With("newMaterialId", newMaterialIdInt)
                    .With("replacedCount", replacedCount)
                    .With("message", $"Replaced material in {replacedCount} parameter instances")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets material usage statistics
        /// </summary>
        [MCPMethod("getMaterialUsageStats", Category = "Material", Description = "Gets usage statistics for a material")]
        public static string GetMaterialUsageStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getMaterialUsageStats");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                ElementId materialId = new ElementId(materialIdInt);

                var categories = new Dictionary<string, int>();
                int totalElements = 0;

                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType();

                foreach (Element elem in collector)
                {
                    bool hasMaterial = false;

                    Parameter matParam = elem.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (matParam != null && matParam.AsElementId() == materialId)
                    {
                        hasMaterial = true;
                    }

                    if (!hasMaterial)
                    {
                        foreach (Parameter param in elem.Parameters)
                        {
                            if (param.StorageType == StorageType.ElementId && param.AsElementId() == materialId)
                            {
                                hasMaterial = true;
                                break;
                            }
                        }
                    }

                    if (hasMaterial)
                    {
                        totalElements++;
                        string categoryName = elem.Category?.Name ?? "None";
                        if (categories.ContainsKey(categoryName))
                        {
                            categories[categoryName]++;
                        }
                        else
                        {
                            categories[categoryName] = 1;
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("materialId", materialIdInt)
                    .With("totalElements", totalElements)
                    .With("categoriesUsed", categories.Count)
                    .With("categoryBreakdown", categories)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Libraries

        /// <summary>
        /// Loads material from Autodesk material library
        /// </summary>
        [MCPMethod("loadMaterialFromLibrary", Category = "Material", Description = "Loads a material from the Autodesk material library")]
        public static string LoadMaterialFromLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API LIMITATION: Direct material library loading requires complex file manipulation
                return ResponseBuilder.Error("LoadMaterialFromLibrary not fully supported in Revit 2026 API", "NOT_SUPPORTED")
                    .With("note", "Material library loading requires complex file operations and asset manipulation")
                    .With("workaround", "Use Revit UI Material Browser to load materials from library, or manually import .adsklib files")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Exports material to library file
        /// </summary>
        [MCPMethod("exportMaterial", Category = "Material", Description = "Exports a material to a library file")]
        public static string ExportMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API LIMITATION: Material export requires complex file operations
                return ResponseBuilder.Error("ExportMaterial not fully supported in Revit 2026 API", "NOT_SUPPORTED")
                    .With("note", "Material export to .adsklib files requires complex Asset serialization")
                    .With("workaround", "Use Revit UI Material Browser to export materials to library files")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Material Search

        /// <summary>
        /// Searches for materials by name or properties
        /// </summary>
        [MCPMethod("searchMaterials", Category = "Material", Description = "Searches for materials by name or properties")]
        public static string SearchMaterials(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "searchMaterials");
                v.Require("searchTerm");
                v.ThrowIfInvalid();

                string searchTerm = v.GetRequired<string>("searchTerm").ToLower();
                string searchIn = parameters["searchIn"]?.ToString()?.ToLower() ?? "name";

                var matchingMaterials = new List<object>();

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material));

                foreach (Material material in collector)
                {
                    bool matches = false;

                    if (searchIn == "name" || searchIn == "all")
                    {
                        if (material.Name.ToLower().Contains(searchTerm))
                        {
                            matches = true;
                        }
                    }

                    if (!matches && (searchIn == "class" || searchIn == "all"))
                    {
                        if (!string.IsNullOrEmpty(material.MaterialClass) && material.MaterialClass.ToLower().Contains(searchTerm))
                        {
                            matches = true;
                        }
                    }

                    if (matches)
                    {
                        matchingMaterials.Add(new
                        {
                            materialId = (int)material.Id.Value,
                            name = material.Name,
                            materialClass = material.MaterialClass ?? "",
                            color = new[] { material.Color.Red, material.Color.Green, material.Color.Blue }
                        });
                    }
                }

                return ResponseBuilder.Success()
                    .With("searchTerm", searchTerm)
                    .With("searchIn", searchIn)
                    .With("count", matchingMaterials.Count)
                    .With("materials", matchingMaterials)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Asset Management

        /// <summary>
        /// Gets all appearance assets in project
        /// </summary>
        [MCPMethod("getAppearanceAssets", Category = "Material", Description = "Gets all appearance assets in the project")]
        public static string GetAppearanceAssets(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var appearanceAssets = new List<object>();

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(AppearanceAssetElement));

                foreach (AppearanceAssetElement asset in collector)
                {
                    appearanceAssets.Add(new
                    {
                        assetId = (int)asset.Id.Value,
                        name = asset.Name
                    });
                }

                return ResponseBuilder.Success()
                    .With("count", appearanceAssets.Count)
                    .With("appearanceAssets", appearanceAssets)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates a new appearance asset
        /// </summary>
        [MCPMethod("createAppearanceAsset", Category = "Material", Description = "Creates a new appearance asset")]
        public static string CreateAppearanceAsset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API LIMITATION: Appearance asset creation requires complex Asset class manipulation
                return ResponseBuilder.Error("CreateAppearanceAsset not fully supported in Revit 2026 API", "NOT_SUPPORTED")
                    .With("note", "Appearance asset creation requires working with Asset class and complex property manipulation")
                    .With("workaround", "Use Revit UI Material Browser or duplicate existing appearance assets")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Duplicates an appearance asset
        /// </summary>
        [MCPMethod("duplicateAppearanceAsset", Category = "Material", Description = "Duplicates an existing appearance asset")]
        public static string DuplicateAppearanceAsset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "duplicateAppearanceAsset");
                v.Require("assetId").IsType<int>();
                v.Require("newName");
                v.ThrowIfInvalid();

                int assetIdInt = v.GetRequired<int>("assetId");
                AppearanceAssetElement asset = doc.GetElement(new ElementId(assetIdInt)) as AppearanceAssetElement;

                if (asset == null)
                {
                    return ResponseBuilder.Error("Appearance asset not found", "NOT_FOUND").Build();
                }

                string newName = v.GetRequired<string>("newName");

                using (var trans = new Transaction(doc, "Duplicate Appearance Asset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    AppearanceAssetElement newAsset = asset.Duplicate(newName);

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .With("originalAssetId", assetIdInt)
                        .With("newAssetId", (int)newAsset.Id.Value)
                        .With("newAssetName", newAsset.Name)
                        .With("message", "Appearance asset duplicated successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modifies an appearance asset's color/tint (limited support in Revit 2026)
        /// </summary>
        [MCPMethod("modifyAppearanceAssetColor", Category = "Material", Description = "Modifies the color or tint of an appearance asset")]
        public static string ModifyAppearanceAssetColor(UIApplication uiApp, JObject parameters)
        {
            // Note: AppearanceAssetEditScope is internal in Revit 2026 API
            // This method returns information about the limitation
            return ResponseBuilder.Error("ModifyAppearanceAssetColor is not fully supported in Revit 2026 API", "NOT_SUPPORTED")
                .With("note", "AppearanceAssetEditScope is internal/protected in Revit 2026")
                .With("workaround", "Use CreateMaterialWithAppearance to duplicate an existing appearance asset, then manually adjust colors in Revit UI if needed")
                .With("recommendation", "Choose a base appearance asset that closely matches your desired color")
                .Build();
        }

        /// <summary>
        /// Gets detailed information about an appearance asset including its properties
        /// </summary>
        [MCPMethod("getAppearanceAssetDetails", Category = "Material", Description = "Gets detailed information about an appearance asset")]
        public static string GetAppearanceAssetDetails(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getAppearanceAssetDetails");
                v.Require("assetId").IsType<int>();
                v.ThrowIfInvalid();

                int assetIdInt = v.GetRequired<int>("assetId");
                AppearanceAssetElement assetElem = doc.GetElement(new ElementId(assetIdInt)) as AppearanceAssetElement;

                if (assetElem == null)
                {
                    return ResponseBuilder.Error("Appearance asset not found", "NOT_FOUND").Build();
                }

                Asset renderingAsset = assetElem.GetRenderingAsset();
                var properties = new List<object>();

                if (renderingAsset != null)
                {
                    for (int i = 0; i < renderingAsset.Size; i++)
                    {
                        AssetProperty prop = renderingAsset[i];
                        if (prop != null)
                        {
                            object value = null;
                            string typeStr = prop.Type.ToString();

                            try
                            {
                                // Read-only access to properties using type checking
                                // Revit 2026 API changed - use 'is' pattern matching instead of AssetPropertyType enum
                                if (prop is AssetPropertyDoubleArray4d colorProp)
                                {
                                    var vals = colorProp.GetValueAsDoubles();
                                    value = new { r = (int)(vals[0] * 255), g = (int)(vals[1] * 255), b = (int)(vals[2] * 255), a = vals[3] };
                                }
                                else if (prop is AssetPropertyDouble doubleProp)
                                {
                                    value = doubleProp.Value;
                                }
                                else if (prop is AssetPropertyString stringProp)
                                {
                                    value = stringProp.Value;
                                }
                                else if (prop is AssetPropertyInteger intProp)
                                {
                                    value = intProp.Value;
                                }
                                else if (prop is AssetPropertyBoolean boolProp)
                                {
                                    value = boolProp.Value;
                                }
                            }
                            catch { }

                            properties.Add(new
                            {
                                name = prop.Name,
                                type = typeStr,
                                value = value
                            });
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("assetId", assetIdInt)
                    .With("assetName", assetElem.Name)
                    .With("propertyCount", properties.Count)
                    .With("properties", properties)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Creates a complete material with appearance asset in one call
        /// </summary>
        [MCPMethod("createMaterialWithAppearance", Category = "Material", Description = "Creates a complete material with appearance asset in one call")]
        public static string CreateMaterialWithAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createMaterialWithAppearance");
                v.Require("materialName");
                v.ThrowIfInvalid();

                string materialName = v.GetRequired<string>("materialName");

                // Get color
                byte red = 128, green = 128, blue = 128;
                if (parameters["color"] != null)
                {
                    var colorArray = parameters["color"].ToObject<int[]>();
                    red = (byte)colorArray[0];
                    green = (byte)colorArray[1];
                    blue = (byte)colorArray[2];
                }

                // Get base appearance asset to duplicate (optional)
                int? baseAssetId = parameters["baseAppearanceAssetId"]?.ToObject<int>();

                using (var trans = new Transaction(doc, "Create Material With Appearance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the material
                    ElementId materialId = Material.Create(doc, materialName);
                    Material material = doc.GetElement(materialId) as Material;

                    // Set graphic color
                    material.Color = new Color(red, green, blue);

                    ElementId newAssetId = ElementId.InvalidElementId;

                    // If base asset provided, duplicate it and modify color
                    if (baseAssetId.HasValue)
                    {
                        AppearanceAssetElement baseAsset = doc.GetElement(new ElementId(baseAssetId.Value)) as AppearanceAssetElement;
                        if (baseAsset != null)
                        {
                            // Duplicate the appearance asset
                            string assetName = materialName + "_Appearance";
                            AppearanceAssetElement newAsset = baseAsset.Duplicate(assetName);
                            newAssetId = newAsset.Id;

                            // Assign to material
                            material.AppearanceAssetId = newAssetId;

                            // Enable render appearance for shading
                            material.UseRenderAppearanceForShading = true;
                        }
                    }

                    trans.Commit();

                    // Note: AppearanceAssetEditScope is internal/protected in Revit 2026 API
                    // Color modification of appearance assets must be done manually in Revit UI
                    // The duplicated appearance asset inherits the base asset's appearance

                    return ResponseBuilder.Success()
                        .With("materialId", (int)materialId.Value)
                        .With("materialName", material.Name)
                        .With("appearanceAssetId", newAssetId != ElementId.InvalidElementId ? (int?)newAssetId.Value : null)
                        .With("graphicColor", new { r = red, g = green, b = blue })
                        .With("useRenderAppearanceForShading", material.UseRenderAppearanceForShading)
                        .With("message", "Material with appearance created successfully")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets material by name
        /// </summary>
        [MCPMethod("getMaterialByName", Category = "Material", Description = "Gets a material by its name")]
        public static string GetMaterialByName(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getMaterialByName");
                v.Require("materialName");
                v.ThrowIfInvalid();

                string materialName = v.GetRequired<string>("materialName");

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material));

                foreach (Material material in collector)
                {
                    if (material.Name == materialName)
                    {
                        return ResponseBuilder.Success()
                            .With("materialId", (int)material.Id.Value)
                            .With("name", material.Name)
                            .With("materialClass", material.MaterialClass ?? "")
                            .With("color", new[] { material.Color.Red, material.Color.Green, material.Color.Blue })
                            .With("transparency", material.Transparency)
                            .With("shininess", material.Shininess)
                            .With("found", true)
                            .Build();
                    }
                }

                return ResponseBuilder.Success()
                    .With("materialName", materialName)
                    .With("found", false)
                    .With("message", "Material not found")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Checks if material is in use
        /// </summary>
        [MCPMethod("isMaterialInUse", Category = "Material", Description = "Checks if a material is currently in use by any element")]
        public static string IsMaterialInUse(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "isMaterialInUse");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                int materialIdInt = v.GetRequired<int>("materialId");
                ElementId materialId = new ElementId(materialIdInt);

                int elementCount = 0;

                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType();

                foreach (Element elem in collector)
                {
                    Parameter matParam = elem.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (matParam != null && matParam.AsElementId() == materialId)
                    {
                        elementCount++;
                        continue;
                    }

                    foreach (Parameter param in elem.Parameters)
                    {
                        if (param.StorageType == StorageType.ElementId && param.AsElementId() == materialId)
                        {
                            elementCount++;
                            break;
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("materialId", materialIdInt)
                    .With("isInUse", elementCount > 0)
                    .With("elementCount", elementCount)
                    .With("message", elementCount > 0 ? $"Material is used by {elementCount} elements" : "Material is not in use")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Paint Methods

        /// <summary>
        /// Paint an element face with a material
        /// Parameters:
        /// - elementId: The element to paint
        /// - materialId: The material to apply
        /// - faceIndex: (optional) Specific face index to paint (default paints all faces)
        /// </summary>
        [MCPMethod("paintElementFace", Category = "Material")]
        public static string PaintElementFace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "PaintElementFace");
                v.Require("elementId").IsType<int>();
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                var elementId = new ElementId(Convert.ToInt64(parameters["elementId"].ToString()));
                var materialId = new ElementId(Convert.ToInt64(parameters["materialId"].ToString()));
                var faceIndex = parameters["faceIndex"] != null ? int.Parse(parameters["faceIndex"].ToString()) : -1;

                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return ResponseBuilder.Error($"Element not found: {elementId.Value}", "NOT_FOUND").Build();
                }

                var material = doc.GetElement(materialId) as Material;
                if (material == null)
                {
                    return ResponseBuilder.Error($"Material not found: {materialId.Value}", "NOT_FOUND").Build();
                }

                // Get element geometry
                var options = new Options();
                options.ComputeReferences = true;
                var geomElement = element.get_Geometry(options);

                if (geomElement == null)
                {
                    return ResponseBuilder.Error("Could not get element geometry", "GEOMETRY_ERROR").Build();
                }

                var paintedFaces = 0;
                var faceCounter = 0;

                using (var trans = new Transaction(doc, "Paint Element Face"))
                {
                    trans.Start();

                    foreach (var geomObj in geomElement)
                    {
                        var solid = geomObj as Solid;
                        if (solid == null || solid.Faces.Size == 0)
                        {
                            // Try to get solid from geometry instance
                            var geomInst = geomObj as GeometryInstance;
                            if (geomInst != null)
                            {
                                foreach (var instObj in geomInst.GetInstanceGeometry())
                                {
                                    solid = instObj as Solid;
                                    if (solid != null && solid.Faces.Size > 0)
                                        break;
                                }
                            }
                        }

                        if (solid != null)
                        {
                            foreach (Face face in solid.Faces)
                            {
                                if (faceIndex == -1 || faceIndex == faceCounter)
                                {
                                    try
                                    {
                                        doc.Paint(elementId, face, materialId);
                                        paintedFaces++;
                                    }
                                    catch { }
                                }
                                faceCounter++;
                            }
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("elementId", elementId.Value)
                    .With("materialId", materialId.Value)
                    .With("materialName", material.Name)
                    .With("facesPainted", paintedFaces)
                    .With("totalFaces", faceCounter)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Paint wall faces with a material
        /// Parameters:
        /// - wallId: The wall to paint
        /// - materialId: The material to apply
        /// - side: "interior", "exterior", or "both" (default: "both")
        /// </summary>
        [MCPMethod("paintWall", Category = "Material")]
        public static string PaintWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "PaintWall");
                v.Require("wallId").IsType<int>();
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                var wallId = new ElementId(Convert.ToInt64(parameters["wallId"].ToString()));
                var materialId = new ElementId(Convert.ToInt64(parameters["materialId"].ToString()));
                var side = parameters["side"]?.ToString()?.ToLower() ?? "both";

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return ResponseBuilder.Error($"Wall not found: {wallId.Value}", "NOT_FOUND").Build();
                }

                var material = doc.GetElement(materialId) as Material;
                if (material == null)
                {
                    return ResponseBuilder.Error($"Material not found: {materialId.Value}", "NOT_FOUND").Build();
                }

                // Get wall geometry
                var options = new Options();
                options.ComputeReferences = true;
                var geomElement = wall.get_Geometry(options);

                var paintedFaces = new List<string>();

                using (var trans = new Transaction(doc, "Paint Wall"))
                {
                    trans.Start();

                    foreach (var geomObj in geomElement)
                    {
                        var solid = geomObj as Solid;
                        if (solid == null || solid.Faces.Size == 0) continue;

                        foreach (Face face in solid.Faces)
                        {
                            var planarFace = face as PlanarFace;
                            if (planarFace == null) continue;

                            // Determine if face is interior or exterior based on normal
                            var normal = planarFace.FaceNormal;
                            var locationCurve = wall.Location as LocationCurve;
                            if (locationCurve == null) continue;

                            var curve = locationCurve.Curve;
                            var wallDirection = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                            var wallNormal = wallDirection.CrossProduct(XYZ.BasisZ).Normalize();
                            var dot = normal.DotProduct(wallNormal);

                            var isExterior = dot > 0.5;
                            var isInterior = dot < -0.5;

                            var shouldPaint = side == "both" ||
                                            (side == "exterior" && isExterior) ||
                                            (side == "interior" && isInterior);

                            if (shouldPaint && (isExterior || isInterior))
                            {
                                try
                                {
                                    doc.Paint(wallId, face, materialId);
                                    paintedFaces.Add(isExterior ? "exterior" : "interior");
                                }
                                catch { }
                            }
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("wallId", wallId.Value)
                    .With("materialId", materialId.Value)
                    .With("materialName", material.Name)
                    .With("side", side)
                    .With("facesPainted", paintedFaces.Count)
                    .With("paintedSides", paintedFaces.Distinct().ToList())
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Paint multiple walls with a material
        /// Parameters:
        /// - wallIds: Array of wall IDs to paint
        /// - materialId: The material to apply
        /// - side: "interior", "exterior", or "both" (default: "both")
        /// </summary>
        [MCPMethod("paintWalls", Category = "Material")]
        public static string PaintWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "PaintWalls");
                v.Require("wallIds");
                v.Require("materialId").IsType<int>();
                v.ThrowIfInvalid();

                var wallIds = parameters["wallIds"].ToObject<List<long>>();
                var materialId = new ElementId(Convert.ToInt64(parameters["materialId"].ToString()));
                var side = parameters["side"]?.ToString()?.ToLower() ?? "both";

                var material = doc.GetElement(materialId) as Material;
                if (material == null)
                {
                    return ResponseBuilder.Error($"Material not found: {materialId.Value}", "NOT_FOUND").Build();
                }

                var results = new List<object>();
                var successCount = 0;
                var failCount = 0;

                using (var trans = new Transaction(doc, "Paint Multiple Walls"))
                {
                    trans.Start();

                    foreach (var wId in wallIds)
                    {
                        var wallId = new ElementId(wId);
                        var wall = doc.GetElement(wallId) as Wall;

                        if (wall == null)
                        {
                            results.Add(new { wallId = wId, success = false, error = "Wall not found" });
                            failCount++;
                            continue;
                        }

                        var options = new Options();
                        options.ComputeReferences = true;
                        var geomElement = wall.get_Geometry(options);
                        var paintedFaces = 0;

                        foreach (var geomObj in geomElement)
                        {
                            var solid = geomObj as Solid;
                            if (solid == null || solid.Faces.Size == 0) continue;

                            foreach (Face face in solid.Faces)
                            {
                                var planarFace = face as PlanarFace;
                                if (planarFace == null) continue;

                                var normal = planarFace.FaceNormal;
                                var locationCurve = wall.Location as LocationCurve;
                                if (locationCurve == null) continue;

                                var curve = locationCurve.Curve;
                                var wallDirection = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                                var wallNormal = wallDirection.CrossProduct(XYZ.BasisZ).Normalize();
                                var dot = normal.DotProduct(wallNormal);

                                var isExterior = dot > 0.5;
                                var isInterior = dot < -0.5;

                                var shouldPaint = side == "both" ||
                                                (side == "exterior" && isExterior) ||
                                                (side == "interior" && isInterior);

                                if (shouldPaint && (isExterior || isInterior))
                                {
                                    try
                                    {
                                        doc.Paint(wallId, face, materialId);
                                        paintedFaces++;
                                    }
                                    catch { }
                                }
                            }
                        }

                        results.Add(new { wallId = wId, success = true, facesPainted = paintedFaces });
                        successCount++;
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("materialId", materialId.Value)
                    .With("materialName", material.Name)
                    .With("side", side)
                    .With("totalWalls", wallIds.Count)
                    .With("successCount", successCount)
                    .With("failCount", failCount)
                    .With("results", results)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove paint from an element face
        /// Parameters:
        /// - elementId: The element to remove paint from
        /// - faceIndex: (optional) Specific face index, or remove from all faces
        /// </summary>
        [MCPMethod("removePaint", Category = "Material")]
        public static string RemovePaint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "RemovePaint");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementId = new ElementId(Convert.ToInt64(parameters["elementId"].ToString()));
                var faceIndex = parameters["faceIndex"] != null ? int.Parse(parameters["faceIndex"].ToString()) : -1;

                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return ResponseBuilder.Error($"Element not found: {elementId.Value}", "NOT_FOUND").Build();
                }

                var options = new Options();
                options.ComputeReferences = true;
                var geomElement = element.get_Geometry(options);

                var removedCount = 0;
                var faceCounter = 0;

                using (var trans = new Transaction(doc, "Remove Paint"))
                {
                    trans.Start();

                    foreach (var geomObj in geomElement)
                    {
                        var solid = geomObj as Solid;
                        if (solid == null || solid.Faces.Size == 0) continue;

                        foreach (Face face in solid.Faces)
                        {
                            if (faceIndex == -1 || faceIndex == faceCounter)
                            {
                                if (doc.IsPainted(elementId, face))
                                {
                                    doc.RemovePaint(elementId, face);
                                    removedCount++;
                                }
                            }
                            faceCounter++;
                        }
                    }

                    trans.Commit();
                }

                return ResponseBuilder.Success()
                    .With("elementId", elementId.Value)
                    .With("facesUnpainted", removedCount)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Check if an element face is painted
        /// Parameters:
        /// - elementId: The element to check
        /// </summary>
        [MCPMethod("isPainted", Category = "Material")]
        public static string IsPainted(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "IsPainted");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elementId = new ElementId(Convert.ToInt64(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return ResponseBuilder.Error($"Element not found: {elementId.Value}", "NOT_FOUND").Build();
                }

                var options = new Options();
                options.ComputeReferences = true;
                var geomElement = element.get_Geometry(options);

                var paintedFaces = new List<object>();
                var faceCounter = 0;

                foreach (var geomObj in geomElement)
                {
                    var solid = geomObj as Solid;
                    if (solid == null || solid.Faces.Size == 0) continue;

                    foreach (Face face in solid.Faces)
                    {
                        var isPainted = doc.IsPainted(elementId, face);
                        if (isPainted)
                        {
                            var materialId = doc.GetPaintedMaterial(elementId, face);
                            var material = doc.GetElement(materialId) as Material;
                            paintedFaces.Add(new
                            {
                                faceIndex = faceCounter,
                                materialId = materialId.Value,
                                materialName = material?.Name ?? "Unknown"
                            });
                        }
                        faceCounter++;
                    }
                }

                return ResponseBuilder.Success()
                    .With("elementId", elementId.Value)
                    .With("totalFaces", faceCounter)
                    .With("paintedFaceCount", paintedFaces.Count)
                    .With("paintedFaces", paintedFaces)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
