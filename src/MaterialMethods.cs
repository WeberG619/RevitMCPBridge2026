using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;

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
        public static string CreateMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialName is required"
                    });
                }

                string materialName = parameters["materialName"].ToString();

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

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = (int)materialId.Value,
                        materialName = material.Name,
                        message = "Material created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all materials in the project
        /// </summary>
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = materials.Count,
                    materials = materials
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets detailed information about a material
        /// </summary>
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
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either materialId or materialName is required"
                    });
                }

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = (int)material.Id.Value,
                    name = material.Name,
                    materialClass = material.MaterialClass ?? "",
                    color = new[] { material.Color.Red, material.Color.Green, material.Color.Blue },
                    transparency = material.Transparency,
                    shininess = material.Shininess
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Modifies material properties
        /// </summary>
        public static string ModifyMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
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

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = (int)material.Id.Value,
                        materialName = material.Name,
                        message = "Material modified successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Duplicates a material
        /// </summary>
        public static string DuplicateMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null || parameters["newName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId and newName are required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                string newName = parameters["newName"].ToString();

                using (var trans = new Transaction(doc, "Duplicate Material"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    Material newMaterial = material.Duplicate(newName);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalMaterialId = (int)material.Id.Value,
                        newMaterialId = (int)newMaterial.Id.Value,
                        newMaterialName = newMaterial.Name,
                        message = "Material duplicated successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Deletes a material
        /// </summary>
        public static string DeleteMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                ElementId materialId = new ElementId(materialIdInt);
                Material material = doc.GetElement(materialId) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
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

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = materialIdInt,
                        materialName = materialName,
                        deletedCount = deletedIds.Count,
                        message = "Material deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Appearance

        /// <summary>
        /// Sets material appearance properties
        /// </summary>
        public static string SetMaterialAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
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

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = materialIdInt,
                        appearanceAssetId = appearanceAssetId != null ? (int?)appearanceAssetId.Value : null,
                        message = "Material appearance updated successfully",
                        note = "Advanced appearance asset editing requires additional asset manipulation"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets material appearance properties
        /// </summary>
        public static string GetMaterialAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = materialIdInt,
                    materialName = material.Name,
                    appearanceAssetId = appearanceAssetId != null ? (int?)appearanceAssetId.Value : null,
                    appearanceAssetName = appearanceAssetName,
                    useRenderAppearanceForShading = material.UseRenderAppearanceForShading,
                    hasAppearanceAsset = appearanceAsset != null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets material texture/image
        /// </summary>
        public static string SetMaterialTexture(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                // API LIMITATION: Direct texture setting requires AppearanceAssetElement manipulation
                // which is complex and requires working with Asset properties
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "SetMaterialTexture requires complex AppearanceAssetElement manipulation not fully supported in this API version",
                    note = "Use Revit UI to set material textures, or use SetRenderAppearance to assign appearance assets",
                    materialId = materialIdInt,
                    materialName = material.Name
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets material render appearance
        /// </summary>
        public static string SetRenderAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null || parameters["appearanceAssetId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId and appearanceAssetId are required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                int appearanceAssetIdInt = parameters["appearanceAssetId"].ToObject<int>();
                ElementId appearanceAssetId = new ElementId(appearanceAssetIdInt);

                using (var trans = new Transaction(doc, "Set Render Appearance"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    material.AppearanceAssetId = appearanceAssetId;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = materialIdInt,
                        materialName = material.Name,
                        appearanceAssetId = appearanceAssetIdInt,
                        message = "Render appearance set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Surface Patterns

        /// <summary>
        /// Sets material surface pattern for cut/surface
        /// </summary>
        public static string SetMaterialSurfacePattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                // API LIMITATION: Surface pattern properties (CutPatternId, SurfacePatternId, etc.)
                // were removed in Revit 2026 API
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "SetMaterialSurfacePattern not supported in Revit 2026 API",
                    note = "Surface pattern properties (CutPatternId, SurfacePatternId) were removed from Material class in Revit 2026",
                    workaround = "Use Revit UI to set material surface patterns",
                    materialId = materialIdInt,
                    materialName = material.Name
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets material surface pattern settings
        /// </summary>
        public static string GetMaterialSurfacePattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                // API LIMITATION: Surface pattern properties were removed in Revit 2026
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "GetMaterialSurfacePattern not supported in Revit 2026 API",
                    note = "Surface pattern properties (CutPatternId, SurfacePatternId, CutPatternColor, SurfacePatternColor) were removed from Material class",
                    workaround = "Surface patterns must be accessed through UI or use pre-2026 API",
                    materialId = materialIdInt,
                    materialName = material.Name
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Physical Properties

        /// <summary>
        /// Sets material physical/thermal properties
        /// </summary>
        public static string SetMaterialPhysicalProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                // API LIMITATION: Physical/thermal properties require complex PropertySetElement and Asset manipulation
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "SetMaterialPhysicalProperties requires complex Asset manipulation not fully supported",
                    note = "Physical properties require working with StructuralAsset and ThermalAsset classes through PropertySetElement",
                    workaround = "Use Revit UI to set material physical/thermal properties, or use SetStructuralAssetId/SetThermalAssetId",
                    materialId = materialIdInt,
                    materialName = material.Name,
                    structuralAssetId = material.StructuralAssetId != null ? (int?)material.StructuralAssetId.Value : null,
                    thermalAssetId = material.ThermalAssetId != null ? (int?)material.ThermalAssetId.Value : null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets material physical/thermal properties
        /// </summary>
        public static string GetMaterialPhysicalProperties(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                // Get basic asset IDs
                ElementId structuralAssetId = material.StructuralAssetId;
                ElementId thermalAssetId = material.ThermalAssetId;

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = materialIdInt,
                    materialName = material.Name,
                    structuralAssetId = structuralAssetId != null ? (int?)structuralAssetId.Value : null,
                    thermalAssetId = thermalAssetId != null ? (int?)thermalAssetId.Value : null,
                    hasStructuralAsset = structuralAssetId != null && structuralAssetId != ElementId.InvalidElementId,
                    hasThermalAsset = thermalAssetId != null && thermalAssetId != ElementId.InvalidElementId,
                    note = "Detailed asset properties require PropertySetElement access - use structuralAssetId/thermalAssetId to retrieve full details"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Classes

        /// <summary>
        /// Gets all material classes
        /// </summary>
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = sortedClasses.Count,
                    materialClasses = sortedClasses,
                    note = "Material classes are user-defined strings. Common values: Concrete, Masonry, Metal, Wood, Plastic, Glass, etc."
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets material class
        /// </summary>
        public static string SetMaterialClass(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null || parameters["materialClass"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId and materialClass are required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
                Material material = doc.GetElement(new ElementId(materialIdInt)) as Material;

                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Material not found"
                    });
                }

                string materialClass = parameters["materialClass"].ToString();

                using (var trans = new Transaction(doc, "Set Material Class"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    material.MaterialClass = materialClass;

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = materialIdInt,
                        materialName = material.Name,
                        materialClass = material.MaterialClass,
                        message = "Material class set successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Usage

        /// <summary>
        /// Finds all elements using a material
        /// </summary>
        public static string FindElementsWithMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = materialIdInt,
                    count = elementsWithMaterial.Count,
                    elements = elementsWithMaterial
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Replaces material in all elements
        /// </summary>
        public static string ReplaceMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["oldMaterialId"] == null || parameters["newMaterialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "oldMaterialId and newMaterialId are required"
                    });
                }

                int oldMaterialIdInt = parameters["oldMaterialId"].ToObject<int>();
                int newMaterialIdInt = parameters["newMaterialId"].ToObject<int>();
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    oldMaterialId = oldMaterialIdInt,
                    newMaterialId = newMaterialIdInt,
                    replacedCount = replacedCount,
                    message = $"Replaced material in {replacedCount} parameter instances"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets material usage statistics
        /// </summary>
        public static string GetMaterialUsageStats(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = materialIdInt,
                    totalElements = totalElements,
                    categoriesUsed = categories.Count,
                    categoryBreakdown = categories
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Libraries

        /// <summary>
        /// Loads material from Autodesk material library
        /// </summary>
        public static string LoadMaterialFromLibrary(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API LIMITATION: Direct material library loading requires complex file manipulation
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "LoadMaterialFromLibrary not fully supported in Revit 2026 API",
                    note = "Material library loading requires complex file operations and asset manipulation",
                    workaround = "Use Revit UI Material Browser to load materials from library, or manually import .adsklib files"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Exports material to library file
        /// </summary>
        public static string ExportMaterial(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API LIMITATION: Material export requires complex file operations
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "ExportMaterial not fully supported in Revit 2026 API",
                    note = "Material export to .adsklib files requires complex Asset serialization",
                    workaround = "Use Revit UI Material Browser to export materials to library files"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Material Search

        /// <summary>
        /// Searches for materials by name or properties
        /// </summary>
        public static string SearchMaterials(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["searchTerm"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "searchTerm is required"
                    });
                }

                string searchTerm = parameters["searchTerm"].ToString().ToLower();
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    searchTerm = searchTerm,
                    searchIn = searchIn,
                    count = matchingMaterials.Count,
                    materials = matchingMaterials
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Asset Management

        /// <summary>
        /// Gets all appearance assets in project
        /// </summary>
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = appearanceAssets.Count,
                    appearanceAssets = appearanceAssets
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a new appearance asset
        /// </summary>
        public static string CreateAppearanceAsset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // API LIMITATION: Appearance asset creation requires complex Asset class manipulation
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "CreateAppearanceAsset not fully supported in Revit 2026 API",
                    note = "Appearance asset creation requires working with Asset class and complex property manipulation",
                    workaround = "Use Revit UI Material Browser or duplicate existing appearance assets"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Duplicates an appearance asset
        /// </summary>
        public static string DuplicateAppearanceAsset(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["assetId"] == null || parameters["newName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "assetId and newName are required"
                    });
                }

                int assetIdInt = parameters["assetId"].ToObject<int>();
                AppearanceAssetElement asset = doc.GetElement(new ElementId(assetIdInt)) as AppearanceAssetElement;

                if (asset == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Appearance asset not found"
                    });
                }

                string newName = parameters["newName"].ToString();

                using (var trans = new Transaction(doc, "Duplicate Appearance Asset"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    AppearanceAssetElement newAsset = asset.Duplicate(newName);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalAssetId = assetIdInt,
                        newAssetId = (int)newAsset.Id.Value,
                        newAssetName = newAsset.Name,
                        message = "Appearance asset duplicated successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Modifies an appearance asset's color/tint (limited support in Revit 2026)
        /// </summary>
        public static string ModifyAppearanceAssetColor(UIApplication uiApp, JObject parameters)
        {
            // Note: AppearanceAssetEditScope is internal in Revit 2026 API
            // This method returns information about the limitation
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "ModifyAppearanceAssetColor is not fully supported in Revit 2026 API",
                note = "AppearanceAssetEditScope is internal/protected in Revit 2026",
                workaround = "Use CreateMaterialWithAppearance to duplicate an existing appearance asset, then manually adjust colors in Revit UI if needed",
                recommendation = "Choose a base appearance asset that closely matches your desired color"
            });
        }

        /// <summary>
        /// Gets detailed information about an appearance asset including its properties
        /// </summary>
        public static string GetAppearanceAssetDetails(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["assetId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "assetId is required"
                    });
                }

                int assetIdInt = parameters["assetId"].ToObject<int>();
                AppearanceAssetElement assetElem = doc.GetElement(new ElementId(assetIdInt)) as AppearanceAssetElement;

                if (assetElem == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Appearance asset not found"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    assetId = assetIdInt,
                    assetName = assetElem.Name,
                    propertyCount = properties.Count,
                    properties = properties
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Creates a complete material with appearance asset in one call
        /// </summary>
        public static string CreateMaterialWithAppearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialName is required"
                    });
                }

                string materialName = parameters["materialName"].ToString();

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

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        materialId = (int)materialId.Value,
                        materialName = material.Name,
                        appearanceAssetId = newAssetId != ElementId.InvalidElementId ? (int?)newAssetId.Value : null,
                        graphicColor = new { r = red, g = green, b = blue },
                        useRenderAppearanceForShading = material.UseRenderAppearanceForShading,
                        message = "Material with appearance created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets material by name
        /// </summary>
        public static string GetMaterialByName(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialName is required"
                    });
                }

                string materialName = parameters["materialName"].ToString();

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material));

                foreach (Material material in collector)
                {
                    if (material.Name == materialName)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = true,
                            materialId = (int)material.Id.Value,
                            name = material.Name,
                            materialClass = material.MaterialClass ?? "",
                            color = new[] { material.Color.Red, material.Color.Green, material.Color.Blue },
                            transparency = material.Transparency,
                            shininess = material.Shininess,
                            found = true
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialName = materialName,
                    found = false,
                    message = "Material not found"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Checks if material is in use
        /// </summary>
        public static string IsMaterialInUse(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                int materialIdInt = parameters["materialId"].ToObject<int>();
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = materialIdInt,
                    isInUse = elementCount > 0,
                    elementCount = elementCount,
                    message = elementCount > 0 ? $"Material is used by {elementCount} elements" : "Material is not in use"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
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
        public static string PaintElementFace(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                var elementId = new ElementId(Convert.ToInt64(parameters["elementId"].ToString()));
                var materialId = new ElementId(Convert.ToInt64(parameters["materialId"].ToString()));
                var faceIndex = parameters["faceIndex"] != null ? int.Parse(parameters["faceIndex"].ToString()) : -1;

                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element not found: {elementId.Value}"
                    });
                }

                var material = doc.GetElement(materialId) as Material;
                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Material not found: {materialId.Value}"
                    });
                }

                // Get element geometry
                var options = new Options();
                options.ComputeReferences = true;
                var geomElement = element.get_Geometry(options);

                if (geomElement == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not get element geometry"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId.Value,
                    materialId = materialId.Value,
                    materialName = material.Name,
                    facesPainted = paintedFaces,
                    totalFaces = faceCounter
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Paint wall faces with a material
        /// Parameters:
        /// - wallId: The wall to paint
        /// - materialId: The material to apply
        /// - side: "interior", "exterior", or "both" (default: "both")
        /// </summary>
        public static string PaintWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["wallId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "wallId is required"
                    });
                }

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                var wallId = new ElementId(Convert.ToInt64(parameters["wallId"].ToString()));
                var materialId = new ElementId(Convert.ToInt64(parameters["materialId"].ToString()));
                var side = parameters["side"]?.ToString()?.ToLower() ?? "both";

                var wall = doc.GetElement(wallId) as Wall;
                if (wall == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Wall not found: {wallId.Value}"
                    });
                }

                var material = doc.GetElement(materialId) as Material;
                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Material not found: {materialId.Value}"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    wallId = wallId.Value,
                    materialId = materialId.Value,
                    materialName = material.Name,
                    side = side,
                    facesPainted = paintedFaces.Count,
                    paintedSides = paintedFaces.Distinct().ToList()
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Paint multiple walls with a material
        /// Parameters:
        /// - wallIds: Array of wall IDs to paint
        /// - materialId: The material to apply
        /// - side: "interior", "exterior", or "both" (default: "both")
        /// </summary>
        public static string PaintWalls(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["wallIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "wallIds is required"
                    });
                }

                if (parameters["materialId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "materialId is required"
                    });
                }

                var wallIds = parameters["wallIds"].ToObject<List<long>>();
                var materialId = new ElementId(Convert.ToInt64(parameters["materialId"].ToString()));
                var side = parameters["side"]?.ToString()?.ToLower() ?? "both";

                var material = doc.GetElement(materialId) as Material;
                if (material == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Material not found: {materialId.Value}"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    materialId = materialId.Value,
                    materialName = material.Name,
                    side = side,
                    totalWalls = wallIds.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Remove paint from an element face
        /// Parameters:
        /// - elementId: The element to remove paint from
        /// - faceIndex: (optional) Specific face index, or remove from all faces
        /// </summary>
        public static string RemovePaint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(Convert.ToInt64(parameters["elementId"].ToString()));
                var faceIndex = parameters["faceIndex"] != null ? int.Parse(parameters["faceIndex"].ToString()) : -1;

                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element not found: {elementId.Value}"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId.Value,
                    facesUnpainted = removedCount
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Check if an element face is painted
        /// Parameters:
        /// - elementId: The element to check
        /// </summary>
        public static string IsPainted(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(Convert.ToInt64(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element not found: {elementId.Value}"
                    });
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

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId.Value,
                    totalFaces = faceCounter,
                    paintedFaceCount = paintedFaces.Count,
                    paintedFaces = paintedFaces
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion
    }
}
