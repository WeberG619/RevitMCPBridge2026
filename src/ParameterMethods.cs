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
    /// MCP Server Methods for Revit Parameters
    /// Handles project parameters, shared parameters, instance/type parameters
    /// </summary>
    public static class ParameterMethods
    {
        #region Project Parameters

        /// <summary>
        /// Creates a new project parameter
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing name, group, parameterType, categories, isInstance</param>
        /// <returns>JSON response with success status and parameter info</returns>
        [MCPMethod("createProjectParameter", Category = "Parameter", Description = "Creates a new project parameter")]
        public static string CreateProjectParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["name"] == null || parameters["parameterType"] == null || parameters["categories"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "name, parameterType, and categories are required"
                    });
                }

                string paramName = parameters["name"].ToString();
                string paramTypeStr = parameters["parameterType"].ToString();
                bool isInstance = parameters["isInstance"]?.ToObject<bool>() ?? true;
                string groupName = parameters["group"]?.ToString() ?? "PG_DATA";

                // Parse parameter type - use ForgeTypeId
                ForgeTypeId specTypeId = paramTypeStr.ToLower() switch
                {
                    "text" => SpecTypeId.String.Text,
                    "integer" => SpecTypeId.Int.Integer,
                    "number" => SpecTypeId.Number,
                    "length" => SpecTypeId.Length,
                    "area" => SpecTypeId.Area,
                    "volume" => SpecTypeId.Volume,
                    "angle" => SpecTypeId.Angle,
                    "yesno" => SpecTypeId.Boolean.YesNo,
                    "url" => SpecTypeId.String.Url,
                    _ => SpecTypeId.String.Text
                };

                // Parse parameter group - use GroupTypeId
                ForgeTypeId groupTypeId = groupName.ToUpper() switch
                {
                    "PG_DATA" => GroupTypeId.Data,
                    "PG_IDENTITY_DATA" => GroupTypeId.IdentityData,
                    "PG_GEOMETRY" => GroupTypeId.Geometry,
                    "PG_CONSTRUCTION" => GroupTypeId.Construction,
                    "PG_MATERIALS" => GroupTypeId.Materials,
                    _ => GroupTypeId.Data
                };

                // Get categories
                var categorySet = new CategorySet();
                var categoryArray = parameters["categories"].ToObject<JArray>();

                foreach (var catToken in categoryArray)
                {
                    Category category = null;

                    if (catToken.Type == JTokenType.Integer)
                    {
                        var catId = new ElementId(catToken.ToObject<int>());
                        category = Category.GetCategory(doc, catId);
                    }
                    else if (catToken.Type == JTokenType.String)
                    {
                        string catName = catToken.ToString();
                        // Try to get by BuiltInCategory
                        if (Enum.TryParse<BuiltInCategory>(catName, out BuiltInCategory builtInCat))
                        {
                            category = Category.GetCategory(doc, builtInCat);
                        }
                    }

                    if (category != null)
                    {
                        categorySet.Insert(category);
                    }
                }

                if (categorySet.IsEmpty)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid categories found"
                    });
                }

                // Note: Project parameters in Revit 2026 are created through the UI or SharedParameterElement
                // Direct API creation of project parameters has limited support
                // Recommend using shared parameters for programmatic parameter creation

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Project parameter creation through API has limited support in Revit 2026. " +
                            "Use CreateSharedParameter instead, or create parameters through Revit UI.",
                    suggestion = "Use CreateSharedParameter method with a shared parameter file for full API support"
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
        /// Gets all project parameters
        /// </summary>
        [MCPMethod("getProjectParameters", Category = "Parameter", Description = "Gets all project parameters")]
        public static string GetProjectParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var projectParameters = new List<object>();
                var bindingMap = doc.ParameterBindings;
                var iterator = bindingMap.ForwardIterator();

                while (iterator.MoveNext())
                {
                    var definition = iterator.Key as Definition;
                    var binding = iterator.Current as ElementBinding;

                    if (definition != null && binding != null)
                    {
                        var categories = new List<object>();
                        foreach (Category cat in binding.Categories)
                        {
                            categories.Add(new
                            {
                                id = (int)cat.Id.Value,
                                name = cat.Name
                            });
                        }

                        projectParameters.Add(new
                        {
                            name = definition.Name,
                            parameterType = definition.GetDataType()?.TypeId ?? "Unknown",
                            group = definition.GetGroupTypeId()?.TypeId ?? "Unknown",
                            isInstance = binding is InstanceBinding,
                            isType = binding is TypeBinding,
                            categoryCount = binding.Categories.Size,
                            categories
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    parameterCount = projectParameters.Count,
                    parameters = projectParameters
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
        /// Modifies a project parameter
        /// </summary>
        [MCPMethod("modifyProjectParameter", Category = "Parameter", Description = "Modifies a project parameter")]
        public static string ModifyProjectParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();

                // Find the parameter definition in bindings
                var bindingMap = doc.ParameterBindings;
                var iterator = bindingMap.ForwardIterator();
                Definition foundDefinition = null;
                ElementBinding foundBinding = null;

                while (iterator.MoveNext())
                {
                    var definition = iterator.Key as Definition;
                    if (definition != null && definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                    {
                        foundDefinition = definition;
                        foundBinding = iterator.Current as ElementBinding;
                        break;
                    }
                }

                if (foundDefinition == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Parameter not found in project"
                    });
                }

                // Get modification options
                bool modifyCategories = parameters["categories"] != null;

                if (!modifyCategories)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No modifications specified. Provide 'categories' to rebind parameter."
                    });
                }

                using (var trans = new Transaction(doc, "Modify Project Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Modify category binding
                    if (modifyCategories)
                    {
                        var categoryIds = parameters["categories"].ToObject<int[]>();
                        var categorySet = uiApp.Application.Create.NewCategorySet();

                        foreach (var catId in categoryIds)
                        {
                            var category = Category.GetCategory(doc, new ElementId(catId));
                            if (category != null)
                            {
                                categorySet.Insert(category);
                            }
                        }

                        if (categorySet.IsEmpty)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "No valid categories provided"
                            });
                        }

                        // Determine binding type (preserve instance vs type)
                        bool isInstance = foundBinding is InstanceBinding;
                        var newBinding = isInstance
                            ? (ElementBinding)uiApp.Application.Create.NewInstanceBinding(categorySet)
                            : uiApp.Application.Create.NewTypeBinding(categorySet);

                        // Rebind with new categories
                        bool rebound = bindingMap.ReInsert(foundDefinition, newBinding, foundDefinition.GetGroupTypeId());

                        if (!rebound)
                        {
                            trans.RollBack();
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Failed to rebind parameter to new categories"
                            });
                        }

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            parameterName = foundDefinition.Name,
                            isInstance = isInstance,
                            categoryCount = categorySet.Size,
                            message = "Project parameter modified successfully"
                        });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No modifications performed"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Deletes a project parameter
        /// </summary>
        [MCPMethod("deleteProjectParameter", Category = "Parameter", Description = "Deletes a project parameter")]
        public static string DeleteProjectParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();

                // Find the parameter definition in bindings
                var bindingMap = doc.ParameterBindings;
                var iterator = bindingMap.ForwardIterator();
                Definition foundDefinition = null;

                while (iterator.MoveNext())
                {
                    var definition = iterator.Key as Definition;
                    if (definition != null && definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                    {
                        foundDefinition = definition;
                        break;
                    }
                }

                if (foundDefinition == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Parameter not found in project"
                    });
                }

                using (var trans = new Transaction(doc, "Delete Project Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Remove the parameter binding
                    bool removed = bindingMap.Remove(foundDefinition);

                    if (removed)
                    {
                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            parameterName = foundDefinition.Name,
                            message = "Project parameter deleted successfully"
                        });
                    }
                    else
                    {
                        trans.RollBack();

                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to remove parameter binding"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Shared Parameters

        /// <summary>
        /// Creates a new shared parameter definition
        /// </summary>
        [MCPMethod("createSharedParameter", Category = "Parameter", Description = "Creates a new shared parameter definition")]
        public static string CreateSharedParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["name"] == null || parameters["parameterType"] == null ||
                    parameters["sharedParameterFile"] == null || parameters["categories"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "name, parameterType, sharedParameterFile, and categories are required"
                    });
                }

                string paramName = parameters["name"].ToString();
                string paramTypeStr = parameters["parameterType"].ToString();
                string sharedParamFile = parameters["sharedParameterFile"].ToString();
                string groupName = parameters["group"]?.ToString() ?? "Parameters";
                bool isInstance = parameters["isInstance"]?.ToObject<bool>() ?? true;

                // Parse parameter type - use ForgeTypeId
                ForgeTypeId specTypeId = paramTypeStr.ToLower() switch
                {
                    "text" => SpecTypeId.String.Text,
                    "integer" => SpecTypeId.Int.Integer,
                    "number" => SpecTypeId.Number,
                    "length" => SpecTypeId.Length,
                    "area" => SpecTypeId.Area,
                    "volume" => SpecTypeId.Volume,
                    "angle" => SpecTypeId.Angle,
                    "yesno" => SpecTypeId.Boolean.YesNo,
                    "url" => SpecTypeId.String.Url,
                    _ => SpecTypeId.String.Text
                };

                // Set shared parameter file
                uiApp.Application.SharedParametersFilename = sharedParamFile;

                var definitionFile = uiApp.Application.OpenSharedParameterFile();
                if (definitionFile == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to open shared parameter file. File may not exist or be invalid."
                    });
                }

                // Get or create definition group
                var defGroup = definitionFile.Groups.get_Item(groupName);
                if (defGroup == null)
                {
                    defGroup = definitionFile.Groups.Create(groupName);
                }

                // Check if definition already exists
                var existingDef = defGroup.Definitions.get_Item(paramName);
                ExternalDefinition externalDef = null;

                if (existingDef != null)
                {
                    externalDef = existingDef as ExternalDefinition;
                }
                else
                {
                    // Create new external definition
                    var defOptions = new ExternalDefinitionCreationOptions(paramName, specTypeId);
                    externalDef = defGroup.Definitions.Create(defOptions) as ExternalDefinition;
                }

                if (externalDef == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to create external definition"
                    });
                }

                // Get categories
                var categorySet = new CategorySet();
                var categoryArray = parameters["categories"].ToObject<JArray>();

                foreach (var catToken in categoryArray)
                {
                    Category category = null;

                    if (catToken.Type == JTokenType.Integer)
                    {
                        var catId = new ElementId(catToken.ToObject<int>());
                        category = Category.GetCategory(doc, catId);
                    }
                    else if (catToken.Type == JTokenType.String)
                    {
                        string catName = catToken.ToString();
                        if (Enum.TryParse<BuiltInCategory>(catName, out BuiltInCategory builtInCat))
                        {
                            category = Category.GetCategory(doc, builtInCat);
                        }
                    }

                    if (category != null)
                    {
                        categorySet.Insert(category);
                    }
                }

                if (categorySet.IsEmpty)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid categories found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Shared Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create parameter binding
                    var binding = isInstance
                        ? (ElementBinding)uiApp.Application.Create.NewInstanceBinding(categorySet)
                        : uiApp.Application.Create.NewTypeBinding(categorySet);

                    // Bind parameter
                    var bindingMap = doc.ParameterBindings;
                    var groupTypeId = GroupTypeId.Data;
                    bool bound = bindingMap.Insert(externalDef, binding, groupTypeId);

                    if (!bound)
                    {
                        bound = bindingMap.ReInsert(externalDef, binding, groupTypeId);
                    }

                    trans.Commit();

                    if (bound)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = true,
                            parameterName = paramName,
                            parameterGUID = externalDef.GUID.ToString(),
                            parameterType = specTypeId.TypeId,
                            group = groupName,
                            isInstance,
                            categoryCount = categorySet.Size,
                            sharedParameterFile = sharedParamFile,
                            message = "Shared parameter created successfully"
                        });
                    }
                    else
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to bind shared parameter to categories"
                        });
                    }
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
        /// Loads shared parameters from a file
        /// </summary>
        [MCPMethod("loadSharedParameterFile", Category = "Parameter", Description = "Loads shared parameters from a file")]
        public static string LoadSharedParameterFile(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Validate required parameters
                if (parameters["filePath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "filePath is required"
                    });
                }

                string filePath = parameters["filePath"].ToString();

                // Check if file exists
                if (!System.IO.File.Exists(filePath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Shared parameter file not found: {filePath}"
                    });
                }

                // Set the shared parameter file
                uiApp.Application.SharedParametersFilename = filePath;

                // Try to open it to verify it's valid
                DefinitionFile defFile = uiApp.Application.OpenSharedParameterFile();

                if (defFile == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to open shared parameter file. File may be invalid or corrupted."
                    });
                }

                // Get file info
                int groupCount = defFile.Groups.Size;
                int totalDefinitions = 0;

                foreach (DefinitionGroup group in defFile.Groups)
                {
                    totalDefinitions += group.Definitions.Size;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    filePath,
                    groupCount,
                    definitionCount = totalDefinitions,
                    message = "Shared parameter file loaded successfully"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets all shared parameters in the project
        /// </summary>
        [MCPMethod("getSharedParameters", Category = "Parameter", Description = "Gets all shared parameters in the project")]
        public static string GetSharedParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sharedParameters = new List<object>();
                var bindingMap = doc.ParameterBindings;
                var iterator = bindingMap.ForwardIterator();

                while (iterator.MoveNext())
                {
                    var definition = iterator.Key as Definition;
                    var binding = iterator.Current as ElementBinding;

                    // Check if this is a shared parameter (has ExternalDefinition)
                    if (definition is ExternalDefinition externalDef && binding != null)
                    {
                        var categories = new List<object>();
                        foreach (Category cat in binding.Categories)
                        {
                            categories.Add(new
                            {
                                id = (int)cat.Id.Value,
                                name = cat.Name
                            });
                        }

                        sharedParameters.Add(new
                        {
                            name = externalDef.Name,
                            guid = externalDef.GUID.ToString(),
                            parameterType = externalDef.GetDataType()?.TypeId ?? "Unknown",
                            group = externalDef.GetGroupTypeId()?.TypeId ?? "Unknown",
                            isInstance = binding is InstanceBinding,
                            isType = binding is TypeBinding,
                            categoryCount = binding.Categories.Size,
                            categories
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sharedParameterCount = sharedParameters.Count,
                    parameters = sharedParameters
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets shared parameter definitions from file
        /// </summary>
        [MCPMethod("getSharedParameterDefinitions", Category = "Parameter", Description = "Gets shared parameter definitions from file")]
        public static string GetSharedParameterDefinitions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                string filePath = null;

                // If filePath provided, use it; otherwise use currently loaded file
                if (parameters["filePath"] != null)
                {
                    filePath = parameters["filePath"].ToString();

                    if (!System.IO.File.Exists(filePath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Shared parameter file not found: {filePath}"
                        });
                    }

                    // Temporarily set this as the active file
                    uiApp.Application.SharedParametersFilename = filePath;
                }
                else
                {
                    filePath = uiApp.Application.SharedParametersFilename;
                }

                // Open the shared parameter file
                DefinitionFile defFile = uiApp.Application.OpenSharedParameterFile();

                if (defFile == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No shared parameter file loaded. Provide filePath or load a file first."
                    });
                }

                // Get all groups and definitions
                var groups = new List<object>();

                foreach (DefinitionGroup group in defFile.Groups)
                {
                    var definitions = new List<object>();

                    foreach (Definition definition in group.Definitions)
                    {
                        var externalDef = definition as ExternalDefinition;

                        definitions.Add(new
                        {
                            name = definition.Name,
                            parameterType = definition.GetDataType()?.TypeId ?? "Unknown",
                            group = definition.GetGroupTypeId()?.TypeId ?? "Unknown",
                            guid = externalDef?.GUID.ToString(),
                            visible = externalDef?.Visible ?? true
                        });
                    }

                    groups.Add(new
                    {
                        name = group.Name,
                        definitionCount = group.Definitions.Size,
                        definitions
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    filePath,
                    groupCount = defFile.Groups.Size,
                    groups
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Binds a shared parameter to categories
        /// </summary>
        [MCPMethod("bindSharedParameter", Category = "Parameter", Description = "Binds a shared parameter to categories")]
        public static string BindSharedParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["categories"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "categories array is required"
                    });
                }

                // Get shared parameter by GUID or name
                string paramGuid = parameters["guid"]?.ToString();
                string paramName = parameters["name"]?.ToString();
                bool isInstance = parameters["isInstance"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrEmpty(paramGuid) && string.IsNullOrEmpty(paramName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either guid or name must be provided"
                    });
                }

                // Find the shared parameter definition in the current bindings
                var bindingMap = doc.ParameterBindings;
                var iterator = bindingMap.ForwardIterator();
                ExternalDefinition foundDef = null;

                while (iterator.MoveNext())
                {
                    var definition = iterator.Key as ExternalDefinition;
                    if (definition != null)
                    {
                        bool match = false;
                        if (!string.IsNullOrEmpty(paramGuid))
                        {
                            match = definition.GUID.ToString().Equals(paramGuid, StringComparison.OrdinalIgnoreCase);
                        }
                        else if (!string.IsNullOrEmpty(paramName))
                        {
                            match = definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase);
                        }

                        if (match)
                        {
                            foundDef = definition;
                            break;
                        }
                    }
                }

                if (foundDef == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Shared parameter not found in project. Use CreateSharedParameter first."
                    });
                }

                // Parse categories
                var categoryIds = parameters["categories"].ToObject<int[]>();
                var categorySet = uiApp.Application.Create.NewCategorySet();

                foreach (var catId in categoryIds)
                {
                    var category = Category.GetCategory(doc, new ElementId(catId));
                    if (category != null)
                    {
                        categorySet.Insert(category);
                    }
                }

                if (categorySet.IsEmpty)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid categories found"
                    });
                }

                // Create new binding or update existing
                using (var trans = new Transaction(doc, "Bind Shared Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var binding = isInstance
                        ? (ElementBinding)uiApp.Application.Create.NewInstanceBinding(categorySet)
                        : uiApp.Application.Create.NewTypeBinding(categorySet);

                    // Try to rebind (will replace if already bound)
                    bool bound = bindingMap.ReInsert(foundDef, binding, foundDef.GetGroupTypeId());

                    if (!bound)
                    {
                        // If rebind failed, try insert
                        bound = bindingMap.Insert(foundDef, binding, foundDef.GetGroupTypeId());
                    }

                    trans.Commit();

                    if (bound)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            parameterName = foundDef.Name,
                            parameterGUID = foundDef.GUID.ToString(),
                            isInstance,
                            categoryCount = categorySet.Size,
                            message = "Shared parameter bound successfully to categories"
                        });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to bind shared parameter to categories"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Parameter Values

        /// <summary>
        /// Gets parameter value from an element
        /// </summary>
        [MCPMethod("getParameterValue", Category = "Parameter", Description = "Gets parameter value from an element")]
        public static string GetParameterValue(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementId"] == null || parameters["parameterName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId and parameterName are required"
                    });
                }

                var elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                // Get parameter by name
                string paramName = parameters["parameterName"].ToString();
                Parameter param = element.LookupParameter(paramName);

                if (param == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Parameter not found on element"
                    });
                }

                object value = null;
                string storageType = param.StorageType.ToString();

                switch (param.StorageType)
                {
                    case StorageType.String:
                        value = param.AsString();
                        break;
                    case StorageType.Integer:
                        value = param.AsInteger();
                        break;
                    case StorageType.Double:
                        value = param.AsDouble();
                        break;
                    case StorageType.ElementId:
                        var elemId = param.AsElementId();
                        value = elemId != null ? (int)elemId.Value : -1;
                        break;
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    parameterName = param.Definition.Name,
                    value,
                    displayValue = param.AsValueString(),
                    storageType,
                    isReadOnly = param.IsReadOnly,
                    hasValue = param.HasValue,
                    unit = param.GetUnitTypeId()?.TypeId
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
        /// Sets parameter value for an element
        /// </summary>
        [MCPMethod("setParameterValue", Category = "Parameter", Description = "Sets parameter value for an element")]
        public static string SetParameterValue(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementId"] == null || parameters["parameterName"] == null || parameters["value"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId, parameterName, and value are required"
                    });
                }

                var elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                // Get parameter by name
                string paramName = parameters["parameterName"].ToString();
                Parameter param = element.LookupParameter(paramName);

                if (param == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Parameter not found on element"
                    });
                }

                if (param.IsReadOnly)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Parameter is read-only"
                    });
                }

                using (var trans = new Transaction(doc, "Set Parameter Value"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    bool success = false;

                    // Set value based on storage type
                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            success = param.Set(parameters["value"].ToString());
                            break;

                        case StorageType.Integer:
                            success = param.Set(parameters["value"].ToObject<int>());
                            break;

                        case StorageType.Double:
                            success = param.Set(parameters["value"].ToObject<double>());
                            break;

                        case StorageType.ElementId:
                            var valueInt = parameters["value"].ToObject<int>();
                            success = param.Set(new ElementId(valueInt));
                            break;
                    }

                    trans.Commit();

                    if (success)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = true,
                            elementId = elementIdInt,
                            parameterName = param.Definition.Name,
                            newValue = parameters["value"],
                            message = "Parameter value set successfully"
                        });
                    }
                    else
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to set parameter value. Value may be invalid for this parameter type."
                        });
                    }
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
        /// Gets all parameters for an element
        /// </summary>
        [MCPMethod("getElementParameters", Category = "Parameter", Description = "Gets all parameters for an element")]
        public static string GetElementParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                bool includeReadOnly = parameters["includeReadOnly"]?.ToObject<bool>() ?? true;

                var parameterList = new List<object>();

                foreach (Parameter param in element.Parameters)
                {
                    // Skip read-only parameters if requested
                    if (!includeReadOnly && param.IsReadOnly)
                        continue;

                    object value = null;
                    try
                    {
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                value = param.AsString();
                                break;
                            case StorageType.Integer:
                                value = param.AsInteger();
                                break;
                            case StorageType.Double:
                                value = param.AsDouble();
                                break;
                            case StorageType.ElementId:
                                var elemId = param.AsElementId();
                                value = elemId != null ? (int)elemId.Value : -1;
                                break;
                        }
                    }
                    catch
                    {
                        value = null;
                    }

                    parameterList.Add(new
                    {
                        name = param.Definition.Name,
                        value,
                        displayValue = param.AsValueString(),
                        storageType = param.StorageType.ToString(),
                        isReadOnly = param.IsReadOnly,
                        isShared = param.IsShared,
                        hasValue = param.HasValue,
                        unit = param.GetUnitTypeId()?.TypeId,
                        parameterType = param.Definition.GetDataType()?.TypeId ?? "Unknown",
                        group = param.Definition.GetGroupTypeId()?.TypeId ?? "Unknown"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    elementCategory = element.Category?.Name ?? "Unknown",
                    parameterCount = parameterList.Count,
                    parameters = parameterList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Sets multiple parameter values for an element
        /// </summary>
        [MCPMethod("setMultipleParameterValues", Category = "Parameter", Description = "Sets multiple parameter values for an element")]
        public static string SetMultipleParameterValues(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementId"] == null || parameters["parameters"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId and parameters (dictionary) are required"
                    });
                }

                var elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                var parameterDict = parameters["parameters"].ToObject<Dictionary<string, object>>();
                var results = new List<object>();
                int successCount = 0;
                int failureCount = 0;

                using (var trans = new Transaction(doc, "Set Multiple Parameter Values"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var kvp in parameterDict)
                    {
                        string paramName = kvp.Key;
                        object paramValue = kvp.Value;

                        try
                        {
                            Parameter param = element.LookupParameter(paramName);

                            if (param == null)
                            {
                                results.Add(new
                                {
                                    parameterName = paramName,
                                    success = false,
                                    error = "Parameter not found"
                                });
                                failureCount++;
                                continue;
                            }

                            if (param.IsReadOnly)
                            {
                                results.Add(new
                                {
                                    parameterName = paramName,
                                    success = false,
                                    error = "Parameter is read-only"
                                });
                                failureCount++;
                                continue;
                            }

                            bool setSuccess = false;

                            // Set value based on storage type
                            switch (param.StorageType)
                            {
                                case StorageType.String:
                                    setSuccess = param.Set(paramValue.ToString());
                                    break;

                                case StorageType.Integer:
                                    setSuccess = param.Set(Convert.ToInt32(paramValue));
                                    break;

                                case StorageType.Double:
                                    setSuccess = param.Set(Convert.ToDouble(paramValue));
                                    break;

                                case StorageType.ElementId:
                                    setSuccess = param.Set(new ElementId(Convert.ToInt32(paramValue)));
                                    break;
                            }

                            if (setSuccess)
                            {
                                results.Add(new
                                {
                                    parameterName = paramName,
                                    success = true,
                                    newValue = paramValue
                                });
                                successCount++;
                            }
                            else
                            {
                                results.Add(new
                                {
                                    parameterName = paramName,
                                    success = false,
                                    error = "Failed to set value (invalid for parameter type)"
                                });
                                failureCount++;
                            }
                        }
                        catch (Exception paramEx)
                        {
                            results.Add(new
                            {
                                parameterName = paramName,
                                success = false,
                                error = paramEx.Message
                            });
                            failureCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    totalParameters = parameterDict.Count,
                    successCount,
                    failureCount,
                    results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Parameter Groups

        /// <summary>
        /// Gets all parameter groups
        /// </summary>
        [MCPMethod("getParameterGroups", Category = "Parameter", Description = "Gets all parameter groups")]
        public static string GetParameterGroups(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Get all built-in parameter groups using GroupTypeId
                var groups = new List<object>();

                // Common parameter groups in Revit 2026
                var groupTypeIds = new[]
                {
                    GroupTypeId.Data,
                    GroupTypeId.IdentityData,
                    GroupTypeId.Constraints,
                    GroupTypeId.Phasing,
                    GroupTypeId.Materials,
                    GroupTypeId.Construction,
                    GroupTypeId.Geometry,
                    GroupTypeId.Text,
                    GroupTypeId.Graphics,
                    GroupTypeId.AnalyticalProperties,
                    GroupTypeId.AnalyticalAlignment,
                    GroupTypeId.Electrical,
                    GroupTypeId.Mechanical,
                    GroupTypeId.MechanicalAirflow,
                    GroupTypeId.MechanicalLoads,
                    GroupTypeId.Plumbing,
                    GroupTypeId.Structural,
                    GroupTypeId.StructuralAnalysis,
                    GroupTypeId.Forces,
                    GroupTypeId.Pattern,
                    GroupTypeId.Underlay
                };

                foreach (var groupTypeId in groupTypeIds)
                {
                    groups.Add(new
                    {
                        id = groupTypeId.TypeId,
                        name = GetGroupTypeName(groupTypeId)
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupCount = groups.Count,
                    groups
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper method to get friendly group type names
        private static string GetGroupTypeName(ForgeTypeId groupTypeId)
        {
            if (groupTypeId == GroupTypeId.Data) return "Data";
            if (groupTypeId == GroupTypeId.IdentityData) return "Identity Data";
            if (groupTypeId == GroupTypeId.Constraints) return "Constraints";
            if (groupTypeId == GroupTypeId.Phasing) return "Phasing";
            if (groupTypeId == GroupTypeId.Materials) return "Materials and Finishes";
            if (groupTypeId == GroupTypeId.Construction) return "Construction";
            if (groupTypeId == GroupTypeId.Geometry) return "Geometry";
            if (groupTypeId == GroupTypeId.Text) return "Text";
            if (groupTypeId == GroupTypeId.Graphics) return "Graphics";
            if (groupTypeId == GroupTypeId.AnalyticalProperties) return "Analytical Properties";
            if (groupTypeId == GroupTypeId.AnalyticalAlignment) return "Analytical Alignment";
            if (groupTypeId == GroupTypeId.Electrical) return "Electrical";
            if (groupTypeId == GroupTypeId.Mechanical) return "Mechanical";
            if (groupTypeId == GroupTypeId.MechanicalAirflow) return "Mechanical - Airflow";
            if (groupTypeId == GroupTypeId.MechanicalLoads) return "Mechanical - Loads";
            if (groupTypeId == GroupTypeId.Plumbing) return "Plumbing";
            if (groupTypeId == GroupTypeId.Structural) return "Structural";
            if (groupTypeId == GroupTypeId.StructuralAnalysis) return "Structural Analysis";
            if (groupTypeId == GroupTypeId.Forces) return "Forces";
            if (groupTypeId == GroupTypeId.Pattern) return "Pattern";
            if (groupTypeId == GroupTypeId.Underlay) return "Underlay";
            return groupTypeId.TypeId ?? "Unknown";
        }

        /// <summary>
        /// Creates a new parameter group
        /// </summary>
        [MCPMethod("createParameterGroup", Category = "Parameter", Description = "Creates a new parameter group")]
        public static string CreateParameterGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Note: In Revit API, parameter groups (GroupTypeId) are predefined
                // and cannot be created programmatically. This method returns the list of
                // available built-in groups that can be used when creating parameters.

                var builtInGroups = new List<object>();

                // Add all common parameter groups using GroupTypeId
                var groups = new[]
                {
                    GroupTypeId.Data,
                    GroupTypeId.IdentityData,
                    GroupTypeId.Constraints,
                    GroupTypeId.Phasing,
                    GroupTypeId.Materials,
                    GroupTypeId.Construction,
                    GroupTypeId.Geometry,
                    GroupTypeId.Text,
                    GroupTypeId.Graphics,
                    GroupTypeId.AnalyticalProperties,
                    GroupTypeId.AnalyticalAlignment,
                    GroupTypeId.Electrical,
                    GroupTypeId.Mechanical,
                    GroupTypeId.MechanicalAirflow,
                    GroupTypeId.MechanicalLoads,
                    GroupTypeId.Plumbing,
                    GroupTypeId.Structural,
                    GroupTypeId.StructuralAnalysis,
                    GroupTypeId.Forces,
                    GroupTypeId.Pattern,
                    GroupTypeId.Underlay
                };

                foreach (var group in groups)
                {
                    builtInGroups.Add(new
                    {
                        typeId = group.TypeId,
                        displayName = GetGroupTypeName(group)
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    apiLimitation = "Revit API does not support creating custom parameter groups. The groups listed below are built-in groups that can be used when creating parameters.",
                    availableGroups = builtInGroups,
                    groupCount = builtInGroups.Count,
                    usage = "Use the 'typeId' when creating project or shared parameters to assign them to a specific group"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Parameter Filters and Search

        /// <summary>
        /// Finds all elements with a specific parameter value
        /// </summary>
        [MCPMethod("findElementsByParameterValue", Category = "Parameter", Description = "Finds all elements with a specific parameter value")]
        public static string FindElementsByParameterValue(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                if (parameters["value"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "value is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                string valueStr = parameters["value"].ToString();
                string comparison = parameters["comparison"]?.ToString()?.ToLower() ?? "equals";

                // Start with all elements
                FilteredElementCollector collector = new FilteredElementCollector(doc);

                // Apply category filter if specified
                if (parameters["category"] != null)
                {
                    string categoryName = parameters["category"].ToString();

                    // Try to find the category
                    Category foundCategory = null;
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundCategory = cat;
                            break;
                        }
                    }

                    if (foundCategory != null)
                    {
                        collector = collector.OfCategoryId(foundCategory.Id);
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Category '{categoryName}' not found"
                        });
                    }
                }
                else
                {
                    // Filter to exclude view-specific elements if no category specified
                    collector = collector.WhereElementIsNotElementType();
                }

                // Collect all elements and filter manually by parameter
                // Note: Revit's ParameterValueProvider requires a ParameterId which is complex to obtain
                // For flexibility, we'll use manual filtering
                var matchingElements = new List<object>();

                foreach (Element elem in collector)
                {
                    Parameter param = elem.LookupParameter(paramName);

                    if (param == null || !param.HasValue)
                        continue;

                    bool isMatch = false;

                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            {
                                string paramValue = param.AsString() ?? "";
                                switch (comparison)
                                {
                                    case "equals":
                                        isMatch = paramValue.Equals(valueStr, StringComparison.OrdinalIgnoreCase);
                                        break;
                                    case "contains":
                                        isMatch = paramValue.IndexOf(valueStr, StringComparison.OrdinalIgnoreCase) >= 0;
                                        break;
                                    default:
                                        // String comparison not supported for greater/less
                                        break;
                                }
                            }
                            break;

                        case StorageType.Integer:
                            {
                                if (int.TryParse(valueStr, out int targetValue))
                                {
                                    int paramValue = param.AsInteger();
                                    switch (comparison)
                                    {
                                        case "equals":
                                            isMatch = paramValue == targetValue;
                                            break;
                                        case "greater":
                                            isMatch = paramValue > targetValue;
                                            break;
                                        case "less":
                                            isMatch = paramValue < targetValue;
                                            break;
                                    }
                                }
                            }
                            break;

                        case StorageType.Double:
                            {
                                if (double.TryParse(valueStr, out double targetValue))
                                {
                                    double paramValue = param.AsDouble();
                                    switch (comparison)
                                    {
                                        case "equals":
                                            isMatch = Math.Abs(paramValue - targetValue) < 0.0001;
                                            break;
                                        case "greater":
                                            isMatch = paramValue > targetValue;
                                            break;
                                        case "less":
                                            isMatch = paramValue < targetValue;
                                            break;
                                    }
                                }
                            }
                            break;

                        case StorageType.ElementId:
                            {
                                if (int.TryParse(valueStr, out int targetIdInt))
                                {
                                    var paramValue = param.AsElementId();
                                    if (paramValue != null)
                                    {
                                        isMatch = paramValue.Value == targetIdInt && comparison == "equals";
                                    }
                                }
                            }
                            break;
                    }

                    if (isMatch)
                    {
                        matchingElements.Add(new
                        {
                            elementId = (int)elem.Id.Value,
                            category = elem.Category?.Name ?? "Unknown",
                            name = elem.Name ?? "Unnamed",
                            parameterValue = GetParameterValueObject(param)
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    parameterName = paramName,
                    searchValue = valueStr,
                    comparison,
                    matchCount = matchingElements.Count,
                    elements = matchingElements
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Searches for parameters by name
        /// </summary>
        [MCPMethod("searchParameters", Category = "Parameter", Description = "Searches for parameters by name")]
        public static string SearchParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["searchTerm"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "searchTerm is required"
                    });
                }

                string searchTerm = parameters["searchTerm"].ToString();
                string searchIn = parameters["searchIn"]?.ToString()?.ToLower() ?? "all";
                bool caseSensitive = parameters["caseSensitive"]?.ToObject<bool>() ?? false;

                var projectParams = new List<object>();
                var sharedParams = new List<object>();
                var builtInParams = new List<object>();

                StringComparison comparison = caseSensitive ?
                    StringComparison.Ordinal :
                    StringComparison.OrdinalIgnoreCase;

                // Search project parameters
                if (searchIn == "all" || searchIn == "project")
                {
                    var iterator = doc.ParameterBindings.ForwardIterator();
                    while (iterator.MoveNext())
                    {
                        var definition = iterator.Key as Definition;
                        if (definition != null &&
                            definition.Name.IndexOf(searchTerm, comparison) >= 0)
                        {
                            var binding = iterator.Current as ElementBinding;
                            var categories = new List<string>();

                            if (binding != null)
                            {
                                foreach (Category cat in binding.Categories)
                                {
                                    categories.Add(cat.Name);
                                }
                            }

                            projectParams.Add(new
                            {
                                name = definition.Name,
                                parameterType = definition.GetDataType().TypeId,
                                parameterGroup = definition.GetGroupTypeId().TypeId,
                                isShared = definition is ExternalDefinition,
                                categories = categories,
                                bindingType = binding?.GetType().Name
                            });
                        }
                    }
                }

                // Search shared parameters
                if (searchIn == "all" || searchIn == "shared")
                {
                    var sharedParamFile = uiApp.Application.OpenSharedParameterFile();
                    if (sharedParamFile != null)
                    {
                        foreach (DefinitionGroup group in sharedParamFile.Groups)
                        {
                            foreach (ExternalDefinition def in group.Definitions)
                            {
                                if (def.Name.IndexOf(searchTerm, comparison) >= 0)
                                {
                                    sharedParams.Add(new
                                    {
                                        name = def.Name,
                                        groupName = group.Name,
                                        guid = def.GUID.ToString(),
                                        parameterType = def.GetDataType().TypeId,
                                        parameterGroup = def.GetGroupTypeId().TypeId,
                                        description = def.Description ?? ""
                                    });
                                }
                            }
                        }
                    }
                }

                // Search built-in parameters
                if (searchIn == "all" || searchIn == "builtin")
                {
                    foreach (BuiltInParameter bip in Enum.GetValues(typeof(BuiltInParameter)))
                    {
                        // Skip INVALID
                        if (bip == BuiltInParameter.INVALID)
                            continue;

                        string paramName = bip.ToString();
                        string label = "";

                        try
                        {
                            label = LabelUtils.GetLabelFor(bip);
                        }
                        catch
                        {
                            // Some built-in parameters don't have labels
                            label = paramName;
                        }

                        if (paramName.IndexOf(searchTerm, comparison) >= 0 ||
                            label.IndexOf(searchTerm, comparison) >= 0)
                        {
                            builtInParams.Add(new
                            {
                                id = (int)bip,
                                name = paramName,
                                label = label
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    searchTerm,
                    searchIn,
                    caseSensitive,
                    projectParameters = projectParams,
                    projectParametersCount = projectParams.Count,
                    sharedParameters = sharedParams,
                    sharedParametersCount = sharedParams.Count,
                    builtInParameters = builtInParams,
                    builtInParametersCount = builtInParams.Count,
                    totalMatches = projectParams.Count + sharedParams.Count + builtInParams.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Parameter Types and Definitions

        /// <summary>
        /// Gets parameter definition information
        /// </summary>
        [MCPMethod("getParameterDefinition", Category = "Parameter", Description = "Gets parameter definition information")]
        public static string GetParameterDefinition(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                Definition foundDefinition = null;
                ElementBinding foundBinding = null;
                Parameter sampleParameter = null;

                // Option 1: If elementId is provided, get definition from element's parameter
                if (parameters["elementId"] != null)
                {
                    var elementIdInt = parameters["elementId"].ToObject<int>();
                    var elementId = new ElementId(elementIdInt);
                    var element = doc.GetElement(elementId);

                    if (element != null)
                    {
                        sampleParameter = element.LookupParameter(paramName);
                        if (sampleParameter != null)
                        {
                            foundDefinition = sampleParameter.Definition;
                        }
                    }
                }

                // Option 2: Search in project parameter bindings
                if (foundDefinition == null)
                {
                    var bindingMap = doc.ParameterBindings;
                    var iterator = bindingMap.ForwardIterator();

                    while (iterator.MoveNext())
                    {
                        var definition = iterator.Key as Definition;
                        if (definition != null && definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundDefinition = definition;
                            foundBinding = iterator.Current as ElementBinding;
                            break;
                        }
                    }
                }

                if (foundDefinition == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Parameter definition not found"
                    });
                }

                // Build detailed definition information
                var definitionInfo = new
                {
                    name = foundDefinition.Name,
                    parameterType = foundDefinition.GetDataType()?.TypeId ?? "Unknown",
                    group = foundDefinition.GetGroupTypeId()?.TypeId ?? "Unknown",
                    isShared = foundDefinition is ExternalDefinition,
                    guid = (foundDefinition is ExternalDefinition externalDef) ? externalDef.GUID.ToString() : null,

                    // Storage type and value info (if we have a sample parameter)
                    storageType = sampleParameter?.StorageType.ToString(),
                    unit = sampleParameter?.GetUnitTypeId()?.TypeId,

                    // Binding info (if found in project parameters)
                    hasBinding = foundBinding != null,
                    isInstanceBinding = foundBinding is InstanceBinding,
                    isTypeBinding = foundBinding is TypeBinding,
                    boundCategories = foundBinding != null ? GetBoundCategories(foundBinding) : null,

                    // Parameter properties (from sample parameter if available)
                    isReadOnly = sampleParameter?.IsReadOnly,
                    hasValue = sampleParameter?.HasValue,
                    builtInParameter = sampleParameter?.Id?.Value.ToString()
                };

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    definition = definitionInfo
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper method to get bound categories
        private static List<object> GetBoundCategories(ElementBinding binding)
        {
            var categories = new List<object>();
            foreach (Category cat in binding.Categories)
            {
                categories.Add(new
                {
                    id = (int)cat.Id.Value,
                    name = cat.Name
                });
            }
            return categories;
        }

        /// <summary>
        /// Gets all available parameter types
        /// </summary>
        [MCPMethod("getParameterTypes", Category = "Parameter", Description = "Gets all available parameter types")]
        public static string GetParameterTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // In Revit 2026, parameter types use ForgeTypeId from SpecTypeId class
                // Get common spec types that can be used for parameters

                var parameterTypes = new List<object>();

                // Add common spec types
                AddSpecType(parameterTypes, "String", SpecTypeId.String.Text, "Text string");
                AddSpecType(parameterTypes, "Integer", SpecTypeId.Int.Integer, "Integer number");
                AddSpecType(parameterTypes, "Number", SpecTypeId.Number, "Floating point number");
                AddSpecType(parameterTypes, "Boolean", SpecTypeId.Boolean.YesNo, "Yes/No boolean");

                // Length types
                AddSpecType(parameterTypes, "Length", SpecTypeId.Length, "Linear dimension");

                // Area types
                AddSpecType(parameterTypes, "Area", SpecTypeId.Area, "Area measurement");

                // Volume types
                AddSpecType(parameterTypes, "Volume", SpecTypeId.Volume, "Volume measurement");

                // Angle types
                AddSpecType(parameterTypes, "Angle", SpecTypeId.Angle, "Angle measurement");

                // Currency
                AddSpecType(parameterTypes, "Currency", SpecTypeId.Currency, "Currency value");

                // URL
                AddSpecType(parameterTypes, "URL", SpecTypeId.String.Url, "URL string");

                // Material
                AddSpecType(parameterTypes, "Material", SpecTypeId.Reference.Material, "Material reference");

                // Get all parameter groups using GroupTypeId for reference
                var parameterGroups = new List<object>();
                var groups = new[]
                {
                    GroupTypeId.Data,
                    GroupTypeId.IdentityData,
                    GroupTypeId.Constraints,
                    GroupTypeId.Phasing,
                    GroupTypeId.Materials,
                    GroupTypeId.Construction,
                    GroupTypeId.Geometry,
                    GroupTypeId.Text,
                    GroupTypeId.Graphics,
                    GroupTypeId.AnalyticalProperties,
                    GroupTypeId.AnalyticalAlignment,
                    GroupTypeId.Electrical,
                    GroupTypeId.Mechanical,
                    GroupTypeId.MechanicalAirflow,
                    GroupTypeId.MechanicalLoads,
                    GroupTypeId.Plumbing,
                    GroupTypeId.Structural,
                    GroupTypeId.StructuralAnalysis,
                    GroupTypeId.Forces,
                    GroupTypeId.Pattern,
                    GroupTypeId.Underlay
                };

                foreach (var group in groups)
                {
                    parameterGroups.Add(new
                    {
                        typeId = group.TypeId,
                        displayName = GetGroupTypeName(group)
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    note = "Revit 2026 uses ForgeTypeId for parameter types. Common types are listed below.",
                    parameterTypes = parameterTypes,
                    parameterTypeCount = parameterTypes.Count,
                    parameterGroups = parameterGroups,
                    parameterGroupCount = parameterGroups.Count
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static void AddSpecType(List<object> list, string name, ForgeTypeId typeId, string description)
        {
            list.Add(new
            {
                name = name,
                typeId = typeId.TypeId,
                description = description
            });
        }

        #endregion

        #region Global Parameters

        /// <summary>
        /// Creates a global parameter
        /// </summary>
        [MCPMethod("createGlobalParameter", Category = "Parameter", Description = "Creates a global parameter")]
        public static string CreateGlobalParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["name"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "name is required"
                    });
                }

                if (parameters["parameterType"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterType is required (e.g., 'autodesk.spec.aec:length-2.0.0', 'autodesk.spec:number-2.0.0')"
                    });
                }

                string name = parameters["name"].ToString();
                string paramTypeString = parameters["parameterType"].ToString();

                // Create ForgeTypeId from string
                ForgeTypeId specTypeId = new ForgeTypeId(paramTypeString);

                using (var trans = new Transaction(doc, "Create Global Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create the global parameter
                    var globalParam = GlobalParameter.Create(doc, name, specTypeId);

                    // Set initial value if provided
                    if (parameters["value"] != null)
                    {
                        if (globalParam.GetDefinition().GetDataType().TypeId == SpecTypeId.String.Text.TypeId)
                        {
                            globalParam.SetValue(new StringParameterValue(parameters["value"].ToString()));
                        }
                        else if (globalParam.GetDefinition().GetDataType().TypeId == SpecTypeId.Int.Integer.TypeId)
                        {
                            int intValue = parameters["value"].ToObject<int>();
                            globalParam.SetValue(new IntegerParameterValue(intValue));
                        }
                        else if (globalParam.GetDefinition().GetDataType().TypeId == SpecTypeId.Boolean.YesNo.TypeId)
                        {
                            int boolValue = parameters["value"].ToObject<bool>() ? 1 : 0;
                            globalParam.SetValue(new IntegerParameterValue(boolValue));
                        }
                        else
                        {
                            // For numeric types (length, area, etc.), treat as double
                            double doubleValue = parameters["value"].ToObject<double>();
                            globalParam.SetValue(new DoubleParameterValue(doubleValue));
                        }
                    }

                    trans.Commit();

                    // Get the created parameter info
                    var definition = globalParam.GetDefinition();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        globalParameterId = globalParam.Id.Value,
                        name = globalParam.GetDefinition().Name,
                        parameterType = definition.GetDataType().TypeId,
                        value = GetGlobalParameterValue(globalParam),
                        message = $"Global parameter '{name}' created successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static object GetGlobalParameterValue(GlobalParameter globalParam)
        {
            if (globalParam == null)
                return null;

            var value = globalParam.GetValue();
            if (value == null)
                return null;

            if (value is StringParameterValue strValue)
                return strValue.Value;
            else if (value is IntegerParameterValue intValue)
                return intValue.Value;
            else if (value is DoubleParameterValue dblValue)
                return dblValue.Value;
            else
                return value.ToString();
        }

        /// <summary>
        /// Gets all global parameters
        /// </summary>
        [MCPMethod("getGlobalParameters", Category = "Parameter", Description = "Gets all global parameters")]
        public static string GetGlobalParameters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Collect all global parameters
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(GlobalParameter));

                var globalParams = new List<object>();

                foreach (GlobalParameter globalParam in collector)
                {
                    var definition = globalParam.GetDefinition();
                    var value = GetGlobalParameterValue(globalParam);

                    // Get formula if it exists
                    string formula = null;
                    try
                    {
                        formula = globalParam.GetFormula();
                    }
                    catch
                    {
                        // Formula may not be set or accessible for all parameter types
                    }

                    globalParams.Add(new
                    {
                        id = globalParam.Id.Value,
                        name = definition.Name,
                        parameterType = definition.GetDataType().TypeId,
                        value = value,
                        formula = formula,
                        hasFormula = formula != null,
                        isReporting = globalParam.IsReporting
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    globalParameters = globalParams,
                    count = globalParams.Count,
                    note = "Global parameters can be used to drive multiple elements. Reporting parameters reflect calculated values from the model."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modifies a global parameter value
        /// </summary>
        [MCPMethod("modifyGlobalParameter", Category = "Parameter", Description = "Modifies a global parameter value")]
        public static string ModifyGlobalParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get global parameter by ID or name
                GlobalParameter globalParam = null;

                if (parameters["globalParameterId"] != null)
                {
                    var gpIdInt = parameters["globalParameterId"].ToObject<int>();
                    var gpId = new ElementId(gpIdInt);
                    globalParam = doc.GetElement(gpId) as GlobalParameter;
                }
                else if (parameters["name"] != null)
                {
                    string name = parameters["name"].ToString();
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(GlobalParameter));

                    foreach (GlobalParameter gp in collector)
                    {
                        if (gp.GetDefinition().Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            globalParam = gp;
                            break;
                        }
                    }
                }

                if (globalParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Global parameter not found. Provide globalParameterId or name."
                    });
                }

                using (var trans = new Transaction(doc, "Modify Global Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Set formula if provided
                    if (parameters["formula"] != null)
                    {
                        string formula = parameters["formula"].ToString();
                        globalParam.SetFormula(formula);
                    }
                    // Otherwise set value directly
                    else if (parameters["value"] != null)
                    {
                        var dataType = globalParam.GetDefinition().GetDataType();

                        if (dataType.TypeId == SpecTypeId.String.Text.TypeId)
                        {
                            globalParam.SetValue(new StringParameterValue(parameters["value"].ToString()));
                        }
                        else if (dataType.TypeId == SpecTypeId.Int.Integer.TypeId)
                        {
                            int intValue = parameters["value"].ToObject<int>();
                            globalParam.SetValue(new IntegerParameterValue(intValue));
                        }
                        else if (dataType.TypeId == SpecTypeId.Boolean.YesNo.TypeId)
                        {
                            int boolValue = parameters["value"].ToObject<bool>() ? 1 : 0;
                            globalParam.SetValue(new IntegerParameterValue(boolValue));
                        }
                        else
                        {
                            // For numeric types (length, area, etc.)
                            double doubleValue = parameters["value"].ToObject<double>();
                            globalParam.SetValue(new DoubleParameterValue(doubleValue));
                        }
                    }
                    else
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Either 'value' or 'formula' must be provided"
                        });
                    }

                    trans.Commit();

                    // Get updated info
                    var definition = globalParam.GetDefinition();
                    string currentFormula = null;
                    try
                    {
                        currentFormula = globalParam.GetFormula();
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        globalParameterId = globalParam.Id.Value,
                        name = definition.Name,
                        parameterType = definition.GetDataType().TypeId,
                        value = GetGlobalParameterValue(globalParam),
                        formula = currentFormula,
                        message = $"Global parameter '{definition.Name}' modified successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Deletes a global parameter
        /// </summary>
        [MCPMethod("deleteGlobalParameter", Category = "Parameter", Description = "Deletes a global parameter")]
        public static string DeleteGlobalParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get global parameter by ID or name
                GlobalParameter globalParam = null;
                ElementId gpId = null;

                if (parameters["globalParameterId"] != null)
                {
                    var gpIdInt = parameters["globalParameterId"].ToObject<int>();
                    gpId = new ElementId(gpIdInt);
                    globalParam = doc.GetElement(gpId) as GlobalParameter;
                }
                else if (parameters["name"] != null)
                {
                    string name = parameters["name"].ToString();
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(GlobalParameter));

                    foreach (GlobalParameter gp in collector)
                    {
                        if (gp.GetDefinition().Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            globalParam = gp;
                            gpId = gp.Id;
                            break;
                        }
                    }
                }

                if (globalParam == null || gpId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Global parameter not found. Provide globalParameterId or name."
                    });
                }

                // Get info before deletion
                string paramName = globalParam.GetDefinition().Name;
                int paramId = (int)gpId.Value;

                using (var trans = new Transaction(doc, "Delete Global Parameter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);
                    doc.Delete(gpId);
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        globalParameterId = paramId,
                        name = paramName,
                        message = $"Global parameter '{paramName}' deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Parameter Formulas

        /// <summary>
        /// Sets a formula for a parameter
        /// </summary>
        [MCPMethod("setParameterFormula", Category = "Parameter", Description = "Sets a formula for a parameter")]
        public static string SetParameterFormula(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Check if we're in a family document
                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "SetParameterFormula only works in family documents. Current document is a project.",
                        note = "For project-level formulas, use global parameters with CreateGlobalParameter or ModifyGlobalParameter"
                    });
                }

                // Validate required parameters
                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                if (parameters["formula"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "formula is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                string formula = parameters["formula"].ToString();

                // Get the family manager
                FamilyManager familyManager = doc.FamilyManager;

                // Find the family parameter
                FamilyParameter familyParam = null;
                foreach (FamilyParameter fp in familyManager.Parameters)
                {
                    if (fp.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                    {
                        familyParam = fp;
                        break;
                    }
                }

                if (familyParam == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Family parameter '{paramName}' not found"
                    });
                }

                using (var trans = new Transaction(doc, "Set Parameter Formula"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Set the formula
                    familyManager.SetFormula(familyParam, formula);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        parameterName = paramName,
                        formula = formula,
                        isReporting = familyParam.IsReporting,
                        parameterType = familyParam.Definition.GetDataType().TypeId,
                        message = $"Formula set successfully for parameter '{paramName}'"
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace,
                    note = "Ensure the formula syntax is valid and references existing parameters"
                });
            }
        }

        /// <summary>
        /// Gets the formula for a parameter
        /// </summary>
        [MCPMethod("getParameterFormula", Category = "Parameter", Description = "Gets the formula for a parameter")]
        public static string GetParameterFormula(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                string formula = null;
                bool isReporting = false;
                string parameterType = null;
                string location = null;

                // Check if we're in a family document
                if (doc.IsFamilyDocument)
                {
                    // Get from family parameter
                    FamilyManager familyManager = doc.FamilyManager;

                    FamilyParameter familyParam = null;
                    foreach (FamilyParameter fp in familyManager.Parameters)
                    {
                        if (fp.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                        {
                            familyParam = fp;
                            break;
                        }
                    }

                    if (familyParam == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Family parameter '{paramName}' not found"
                        });
                    }

                    formula = familyParam.Formula;
                    isReporting = familyParam.IsReporting;
                    parameterType = familyParam.Definition.GetDataType().TypeId;
                    location = "Family Document";
                }
                else
                {
                    // Check global parameters in project
                    var collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(GlobalParameter));

                    GlobalParameter globalParam = null;
                    foreach (GlobalParameter gp in collector)
                    {
                        if (gp.GetDefinition().Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                        {
                            globalParam = gp;
                            break;
                        }
                    }

                    if (globalParam == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Global parameter '{paramName}' not found",
                            note = "GetParameterFormula works with family parameters (in family documents) or global parameters (in projects)"
                        });
                    }

                    try
                    {
                        formula = globalParam.GetFormula();
                    }
                    catch
                    {
                        // No formula set
                        formula = null;
                    }

                    isReporting = globalParam.IsReporting;
                    parameterType = globalParam.GetDefinition().GetDataType().TypeId;
                    location = "Global Parameter";
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    parameterName = paramName,
                    formula = formula ?? "",
                    hasFormula = !string.IsNullOrEmpty(formula),
                    isReporting = isReporting,
                    parameterType = parameterType,
                    location = location,
                    message = string.IsNullOrEmpty(formula) ?
                        $"Parameter '{paramName}' has no formula" :
                        $"Formula retrieved for parameter '{paramName}'"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Bulk Operations

        /// <summary>
        /// Copies parameter values from one element to another
        /// </summary>
        [MCPMethod("copyParameterValues", Category = "Parameter", Description = "Copies parameter values from one element to another")]
        public static string CopyParameterValues(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["sourceElementId"] == null || parameters["targetElementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceElementId and targetElementId are required"
                    });
                }

                var sourceIdInt = parameters["sourceElementId"].ToObject<int>();
                var targetIdInt = parameters["targetElementId"].ToObject<int>();

                var sourceElement = doc.GetElement(new ElementId(sourceIdInt));
                var targetElement = doc.GetElement(new ElementId(targetIdInt));

                if (sourceElement == null || targetElement == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Source or target element not found"
                    });
                }

                // Get parameter names to copy
                bool copyAll = parameters["parameterNames"]?.ToString().ToLower() == "all" || parameters["parameterNames"] == null;
                string[] parameterNames = null;

                if (!copyAll)
                {
                    parameterNames = parameters["parameterNames"].ToObject<string[]>();
                }

                var results = new List<object>();
                int successCount = 0;
                int failureCount = 0;

                using (var trans = new Transaction(doc, "Copy Parameter Values"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (copyAll)
                    {
                        // Copy all matching parameters
                        foreach (Parameter sourceParam in sourceElement.Parameters)
                        {
                            var targetParam = targetElement.LookupParameter(sourceParam.Definition.Name);
                            var result = CopyParameterValue(sourceParam, targetParam);
                            results.Add(result);
                            if (result.success) successCount++; else failureCount++;
                        }
                    }
                    else
                    {
                        // Copy specific parameters
                        foreach (var paramName in parameterNames)
                        {
                            var sourceParam = sourceElement.LookupParameter(paramName);
                            var targetParam = targetElement.LookupParameter(paramName);
                            var result = CopyParameterValue(sourceParam, targetParam, paramName);
                            results.Add(result);
                            if (result.success) successCount++; else failureCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sourceElementId = sourceIdInt,
                    targetElementId = targetIdInt,
                    totalParameters = results.Count,
                    successCount,
                    failureCount,
                    results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // Helper method to copy a single parameter value
        private static dynamic CopyParameterValue(Parameter sourceParam, Parameter targetParam, string paramName = null)
        {
            if (sourceParam == null)
            {
                return new
                {
                    parameterName = paramName ?? "Unknown",
                    success = false,
                    error = "Source parameter not found"
                };
            }

            if (targetParam == null)
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = false,
                    error = "Target parameter not found"
                };
            }

            if (targetParam.IsReadOnly)
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = false,
                    error = "Target parameter is read-only"
                };
            }

            if (!sourceParam.HasValue)
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = false,
                    error = "Source parameter has no value"
                };
            }

            if (sourceParam.StorageType != targetParam.StorageType)
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = false,
                    error = $"Storage type mismatch: {sourceParam.StorageType} vs {targetParam.StorageType}"
                };
            }

            bool setSuccess = false;

            try
            {
                switch (sourceParam.StorageType)
                {
                    case StorageType.String:
                        setSuccess = targetParam.Set(sourceParam.AsString());
                        break;
                    case StorageType.Integer:
                        setSuccess = targetParam.Set(sourceParam.AsInteger());
                        break;
                    case StorageType.Double:
                        setSuccess = targetParam.Set(sourceParam.AsDouble());
                        break;
                    case StorageType.ElementId:
                        setSuccess = targetParam.Set(sourceParam.AsElementId());
                        break;
                }
            }
            catch (Exception ex)
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = false,
                    error = ex.Message
                };
            }

            if (setSuccess)
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = true,
                    value = GetParameterValueObject(sourceParam)
                };
            }
            else
            {
                return new
                {
                    parameterName = sourceParam.Definition.Name,
                    success = false,
                    error = "Failed to set parameter value"
                };
            }
        }

        // Helper method to get parameter value as object
        private static object GetParameterValueObject(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.ElementId:
                    var elemId = param.AsElementId();
                    return elemId != null ? (int)elemId.Value : -1;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Sets the same parameter value for multiple elements
        /// </summary>
        [MCPMethod("setParameterForMultipleElements", Category = "Parameter", Description = "Sets the same parameter value for multiple elements")]
        public static string SetParameterForMultipleElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementIds"] == null || parameters["parameterName"] == null || parameters["value"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds (array), parameterName, and value are required"
                    });
                }

                var elementIds = parameters["elementIds"].ToObject<int[]>();
                string paramName = parameters["parameterName"].ToString();
                object paramValue = parameters["value"];

                var results = new List<object>();
                int successCount = 0;
                int failureCount = 0;

                using (var trans = new Transaction(doc, "Set Parameter For Multiple Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var elementIdInt in elementIds)
                    {
                        try
                        {
                            var elementId = new ElementId(elementIdInt);
                            var element = doc.GetElement(elementId);

                            if (element == null)
                            {
                                results.Add(new
                                {
                                    elementId = elementIdInt,
                                    success = false,
                                    error = "Element not found"
                                });
                                failureCount++;
                                continue;
                            }

                            Parameter param = element.LookupParameter(paramName);

                            if (param == null)
                            {
                                results.Add(new
                                {
                                    elementId = elementIdInt,
                                    success = false,
                                    error = "Parameter not found on element"
                                });
                                failureCount++;
                                continue;
                            }

                            if (param.IsReadOnly)
                            {
                                results.Add(new
                                {
                                    elementId = elementIdInt,
                                    success = false,
                                    error = "Parameter is read-only"
                                });
                                failureCount++;
                                continue;
                            }

                            bool setSuccess = false;

                            // Set value based on storage type
                            switch (param.StorageType)
                            {
                                case StorageType.String:
                                    setSuccess = param.Set(paramValue.ToString());
                                    break;

                                case StorageType.Integer:
                                    setSuccess = param.Set(Convert.ToInt32(paramValue));
                                    break;

                                case StorageType.Double:
                                    setSuccess = param.Set(Convert.ToDouble(paramValue));
                                    break;

                                case StorageType.ElementId:
                                    setSuccess = param.Set(new ElementId(Convert.ToInt32(paramValue)));
                                    break;
                            }

                            if (setSuccess)
                            {
                                results.Add(new
                                {
                                    elementId = elementIdInt,
                                    success = true
                                });
                                successCount++;
                            }
                            else
                            {
                                results.Add(new
                                {
                                    elementId = elementIdInt,
                                    success = false,
                                    error = "Failed to set value (invalid for parameter type)"
                                });
                                failureCount++;
                            }
                        }
                        catch (Exception elemEx)
                        {
                            results.Add(new
                            {
                                elementId = elementIdInt,
                                success = false,
                                error = elemEx.Message
                            });
                            failureCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    parameterName = paramName,
                    value = paramValue,
                    totalElements = elementIds.Length,
                    successCount,
                    failureCount,
                    results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Checks if a parameter exists on an element
        /// </summary>
        [MCPMethod("parameterExists", Category = "Parameter", Description = "Checks if a parameter exists on an element")]
        public static string ParameterExists(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementId"] == null || parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId and parameterName are required"
                    });
                }

                var elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                Parameter param = element.LookupParameter(paramName);

                if (param != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        exists = true,
                        parameterName = param.Definition.Name,
                        storageType = param.StorageType.ToString(),
                        isReadOnly = param.IsReadOnly,
                        isShared = param.IsShared,
                        hasValue = param.HasValue,
                        parameterType = param.Definition.GetDataType()?.TypeId ?? "Unknown",
                        group = param.Definition.GetGroupTypeId()?.TypeId ?? "Unknown",
                        unit = param.GetUnitTypeId()?.TypeId
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        exists = false,
                        parameterName = paramName,
                        message = "Parameter not found on element"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets parameter storage type
        /// </summary>
        [MCPMethod("getParameterStorageType", Category = "Parameter", Description = "Gets parameter storage type")]
        public static string GetParameterStorageType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                if (parameters["parameterName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "parameterName is required"
                    });
                }

                var elementIdInt = parameters["elementId"].ToObject<int>();
                var elementId = new ElementId(elementIdInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                string paramName = parameters["parameterName"].ToString();
                Parameter param = element.LookupParameter(paramName);

                if (param == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Parameter '{paramName}' not found on element"
                    });
                }

                // Get storage type information
                var storageType = param.StorageType;
                var storageTypeName = storageType.ToString();

                // Additional parameter information
                var result = new
                {
                    success = true,
                    parameterName = param.Definition.Name,
                    storageType = storageTypeName,
                    storageTypeEnum = (int)storageType,
                    hasValue = param.HasValue,
                    isReadOnly = param.IsReadOnly,
                    isShared = param.IsShared,
                    parameterType = param.Definition.GetDataType()?.TypeId ?? "Unknown",
                    unit = param.GetUnitTypeId()?.TypeId
                };

                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Bulk Conditional Operations

        /// <summary>
        /// Updates parameters on multiple elements based on conditional logic.
        /// Supports complex if-then rules with AND/OR logic.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - category (required): BuiltInCategory name (e.g., "OST_Doors")
        /// - conditions (required): Array of condition objects:
        ///   {parameterName, operator, value} where operator is "equals", "contains", "startsWith", "greaterThan", "lessThan"
        /// - conditionLogic (optional): "AND" or "OR" (default: "AND")
        /// - targetParameter (required): Parameter name to set
        /// - targetValue (required): Value to set
        /// - dryRun (optional): Preview only, don't make changes (default: false)
        /// Example:
        ///   {"category": "OST_Doors", "conditions": [{"parameterName": "Frame Type", "operator": "equals", "value": "Aluminum"}],
        ///    "targetParameter": "Cost Code", "targetValue": "ALU-STD"}
        /// </param>
        /// <returns>JSON response with update results</returns>
        [MCPMethod("bulkSetParameterConditional", Category = "Parameter", Description = "Updates parameters on multiple elements based on conditional logic")]
        public static string BulkSetParameterConditional(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["category"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "category is required" });
                }
                if (parameters["conditions"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "conditions array is required" });
                }
                if (parameters["targetParameter"] == null || parameters["targetValue"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "targetParameter and targetValue are required" });
                }

                string categoryName = parameters["category"].ToString();
                string conditionLogic = parameters["conditionLogic"]?.ToString()?.ToUpper() ?? "AND";
                string targetParamName = parameters["targetParameter"].ToString();
                string targetValue = parameters["targetValue"].ToString();
                bool dryRun = parameters["dryRun"]?.ToObject<bool>() ?? false;

                // Parse category
                BuiltInCategory bic;
                if (!Enum.TryParse(categoryName, out bic))
                {
                    if (!Enum.TryParse("OST_" + categoryName, out bic))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = $"Invalid category: {categoryName}" });
                    }
                }

                // Parse conditions
                var conditionsArray = parameters["conditions"] as JArray;
                var conditions = new List<ParameterCondition>();
                foreach (JObject cond in conditionsArray)
                {
                    conditions.Add(new ParameterCondition
                    {
                        ParameterName = cond["parameterName"]?.ToString(),
                        Operator = cond["operator"]?.ToString()?.ToLower() ?? "equals",
                        Value = cond["value"]?.ToString()
                    });
                }

                // Collect elements
                var elements = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToList();

                var matchingElements = new List<Element>();
                var results = new List<object>();

                // Evaluate conditions for each element
                foreach (var elem in elements)
                {
                    bool matches = EvaluateConditions(elem, conditions, conditionLogic);
                    if (matches)
                    {
                        matchingElements.Add(elem);
                    }
                }

                int updatedCount = 0;
                int failedCount = 0;

                if (!dryRun && matchingElements.Count > 0)
                {
                    using (var trans = new Transaction(doc, "Bulk Set Parameter Conditional"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        foreach (var elem in matchingElements)
                        {
                            try
                            {
                                var targetParam = elem.LookupParameter(targetParamName);
                                if (targetParam != null && !targetParam.IsReadOnly)
                                {
                                    bool success = SetParameterValue(targetParam, targetValue);
                                    if (success)
                                    {
                                        updatedCount++;
                                        results.Add(new
                                        {
                                            elementId = (int)elem.Id.Value,
                                            name = elem.Name,
                                            status = "updated"
                                        });
                                    }
                                    else
                                    {
                                        failedCount++;
                                        results.Add(new
                                        {
                                            elementId = (int)elem.Id.Value,
                                            name = elem.Name,
                                            status = "failed",
                                            error = "Could not set value"
                                        });
                                    }
                                }
                                else
                                {
                                    failedCount++;
                                    results.Add(new
                                    {
                                        elementId = (int)elem.Id.Value,
                                        name = elem.Name,
                                        status = "failed",
                                        error = targetParam == null ? "Parameter not found" : "Parameter is read-only"
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                failedCount++;
                                results.Add(new
                                {
                                    elementId = (int)elem.Id.Value,
                                    name = elem.Name,
                                    status = "failed",
                                    error = ex.Message
                                });
                            }
                        }

                        trans.Commit();
                    }
                }
                else if (dryRun)
                {
                    foreach (var elem in matchingElements)
                    {
                        results.Add(new
                        {
                            elementId = (int)elem.Id.Value,
                            name = elem.Name,
                            status = "would_update"
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    dryRun = dryRun,
                    category = categoryName,
                    conditions = conditions.Select(c => new { c.ParameterName, c.Operator, c.Value }),
                    conditionLogic = conditionLogic,
                    targetParameter = targetParamName,
                    targetValue = targetValue,
                    totalElementsInCategory = elements.Count,
                    matchingElements = matchingElements.Count,
                    updatedCount = dryRun ? 0 : updatedCount,
                    failedCount = dryRun ? 0 : failedCount,
                    results = results.Take(100).ToList(),
                    message = dryRun ?
                        $"Dry run: {matchingElements.Count} elements match conditions" :
                        $"Updated {updatedCount} elements, {failedCount} failed"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper class for parameter conditions
        /// </summary>
        private class ParameterCondition
        {
            public string ParameterName { get; set; }
            public string Operator { get; set; }
            public string Value { get; set; }
        }

        /// <summary>
        /// Evaluates conditions against an element
        /// </summary>
        private static bool EvaluateConditions(Element elem, List<ParameterCondition> conditions, string logic)
        {
            if (conditions.Count == 0) return true;

            var results = new List<bool>();

            foreach (var cond in conditions)
            {
                var param = elem.LookupParameter(cond.ParameterName);
                if (param == null)
                {
                    results.Add(false);
                    continue;
                }

                string paramValue = GetParameterValueAsString(param);
                bool matches = EvaluateCondition(paramValue, cond.Operator, cond.Value);
                results.Add(matches);
            }

            if (logic == "OR")
            {
                return results.Any(r => r);
            }
            else // AND
            {
                return results.All(r => r);
            }
        }

        /// <summary>
        /// Evaluates a single condition
        /// </summary>
        private static bool EvaluateCondition(string actualValue, string op, string expectedValue)
        {
            if (actualValue == null) return false;

            switch (op)
            {
                case "equals":
                    return string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase);
                case "contains":
                    return actualValue.IndexOf(expectedValue, StringComparison.OrdinalIgnoreCase) >= 0;
                case "startswith":
                    return actualValue.StartsWith(expectedValue, StringComparison.OrdinalIgnoreCase);
                case "endswith":
                    return actualValue.EndsWith(expectedValue, StringComparison.OrdinalIgnoreCase);
                case "greaterthan":
                    if (double.TryParse(actualValue, out double actual1) && double.TryParse(expectedValue, out double expected1))
                        return actual1 > expected1;
                    return false;
                case "lessthan":
                    if (double.TryParse(actualValue, out double actual2) && double.TryParse(expectedValue, out double expected2))
                        return actual2 < expected2;
                    return false;
                case "notequals":
                    return !string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase);
                case "isempty":
                    return string.IsNullOrWhiteSpace(actualValue);
                case "isnotempty":
                    return !string.IsNullOrWhiteSpace(actualValue);
                default:
                    return string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Gets parameter value as string
        /// </summary>
        private static string GetParameterValueAsString(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "";
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsDouble().ToString();
                case StorageType.ElementId:
                    return param.AsElementId().Value.ToString();
                default:
                    return param.AsValueString() ?? "";
            }
        }

        /// <summary>
        /// Sets parameter value from string
        /// </summary>
        private static bool SetParameterValue(Parameter param, string value)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value);
                        return true;
                    case StorageType.Integer:
                        if (int.TryParse(value, out int intVal))
                        {
                            param.Set(intVal);
                            return true;
                        }
                        return false;
                    case StorageType.Double:
                        if (double.TryParse(value, out double doubleVal))
                        {
                            param.Set(doubleVal);
                            return true;
                        }
                        return false;
                    case StorageType.ElementId:
                        if (long.TryParse(value, out long idVal))
                        {
                            param.Set(new ElementId(idVal));
                            return true;
                        }
                        return false;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
