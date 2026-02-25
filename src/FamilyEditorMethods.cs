using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Family Editor Methods - Enable AI to create and modify Revit families.
    /// Provides access to family document creation, geometry, and parameters.
    /// </summary>
    public static class FamilyEditorMethods
    {
        #region Open Family for Editing

        /// <summary>
        /// Open a family for editing from the current document.
        /// </summary>
        public static string OpenFamilyForEditing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                // Get family by ID or name
                Family family = null;
                if (parameters["familyId"] != null)
                {
                    var familyId = new ElementId(parameters["familyId"].Value<int>());
                    family = doc.GetElement(familyId) as Family;
                }
                else if (parameters["familyName"] != null)
                {
                    var familyName = parameters["familyName"].ToString();
                    family = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
                }

                if (family == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Family not found" });
                }

                if (!family.IsEditable)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family is not editable (may be system family or in-place)"
                    });
                }

                // Open family document
                var familyDoc = doc.EditFamily(family);
                if (familyDoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Failed to open family for editing" });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyName = family.Name,
                    familyCategory = family.FamilyCategory?.Name,
                    documentTitle = familyDoc.Title,
                    isFamilyDocument = familyDoc.IsFamilyDocument,
                    message = $"Family '{family.Name}' opened for editing"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening family for editing");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Family Document Info

        /// <summary>
        /// Get information about the currently active family document.
        /// </summary>
        public static string GetFamilyDocumentInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                if (!doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Active document is not a family document",
                        documentTitle = doc.Title
                    });
                }

                var familyManager = doc.FamilyManager;

                // Get parameters
                var familyParams = new List<object>();
                foreach (FamilyParameter param in familyManager.Parameters)
                {
                    familyParams.Add(new
                    {
                        name = param.Definition.Name,
                        isInstance = param.IsInstance,
                        isShared = param.IsShared,
                        isReporting = param.IsReporting,
                        storageType = param.StorageType.ToString(),
                        groupTypeId = param.Definition.GetGroupTypeId()?.TypeId,
                        formula = param.Formula
                    });
                }

                // Get types
                var types = new List<object>();
                foreach (FamilyType type in familyManager.Types)
                {
                    types.Add(new
                    {
                        name = type.Name
                    });
                }

                // Get reference planes
                var refPlanes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ReferencePlane))
                    .Cast<ReferencePlane>()
                    .Select(rp => new
                    {
                        id = (int)rp.Id.Value,
                        name = rp.Name,
                        isReference = !string.IsNullOrEmpty(rp.Name)
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    title = doc.Title,
                    pathName = doc.PathName,
                    isModified = doc.IsModified,
                    familyCategory = doc.OwnerFamily?.FamilyCategory?.Name,
                    currentType = familyManager.CurrentType?.Name,
                    parameterCount = familyParams.Count,
                    parameters = familyParams,
                    typeCount = types.Count,
                    types = types,
                    referencePlaneCount = refPlanes.Count,
                    referencePlanes = refPlanes
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting family document info");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Add Family Parameter

        /// <summary>
        /// Add a new parameter to the family.
        /// </summary>
        public static string AddFamilyParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var paramName = parameters["name"]?.ToString();
                if (string.IsNullOrEmpty(paramName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Parameter name required" });
                }

                var isInstance = parameters["isInstance"]?.Value<bool>() ?? false;

                // Use ForgeTypeId for Revit 2026+ parameter group and spec type
                var groupTypeId = new ForgeTypeId("autodesk.parameter.group:general-v1.0.0");

                var familyManager = doc.FamilyManager;

                using (var trans = new Transaction(doc, "Add Family Parameter"))
                {
                    trans.Start();

                    // Check if parameter already exists
                    var existingParam = familyManager.get_Parameter(paramName);
                    if (existingParam != null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Parameter '{paramName}' already exists"
                        });
                    }

                    // Add parameter - use the simpler overload for Revit 2026
                    var newParam = familyManager.AddParameter(
                        paramName,
                        groupTypeId,
                        SpecTypeId.Number,
                        isInstance);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        parameterName = paramName,
                        isInstance = isInstance,
                        message = $"Parameter '{paramName}' added successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding family parameter");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Add Family Type

        /// <summary>
        /// Add a new type to the family.
        /// </summary>
        public static string AddFamilyType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var typeName = parameters["typeName"]?.ToString();
                if (string.IsNullOrEmpty(typeName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Type name required" });
                }

                var familyManager = doc.FamilyManager;

                using (var trans = new Transaction(doc, "Add Family Type"))
                {
                    trans.Start();

                    // Check if type already exists
                    foreach (FamilyType existingType in familyManager.Types)
                    {
                        if (existingType.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                        {
                            trans.RollBack();
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Type '{typeName}' already exists"
                            });
                        }
                    }

                    var newType = familyManager.NewType(typeName);
                    familyManager.CurrentType = newType;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        typeName = typeName,
                        message = $"Type '{typeName}' created and set as current"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding family type");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Parameter Value

        /// <summary>
        /// Set a parameter value in the current family type.
        /// </summary>
        public static string SetFamilyParameterValue(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var paramName = parameters["parameterName"]?.ToString();
                if (string.IsNullOrEmpty(paramName))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Parameter name required" });
                }

                var familyManager = doc.FamilyManager;
                var familyParam = familyManager.get_Parameter(paramName);

                if (familyParam == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Parameter '{paramName}' not found" });
                }

                using (var trans = new Transaction(doc, "Set Parameter Value"))
                {
                    trans.Start();

                    // Set value based on storage type
                    switch (familyParam.StorageType)
                    {
                        case StorageType.Double:
                            var doubleValue = parameters["value"]?.Value<double>() ?? 0;
                            familyManager.Set(familyParam, doubleValue);
                            break;

                        case StorageType.Integer:
                            var intValue = parameters["value"]?.Value<int>() ?? 0;
                            familyManager.Set(familyParam, intValue);
                            break;

                        case StorageType.String:
                            var stringValue = parameters["value"]?.ToString() ?? "";
                            familyManager.Set(familyParam, stringValue);
                            break;

                        case StorageType.ElementId:
                            var idValue = parameters["value"]?.Value<int>() ?? -1;
                            familyManager.Set(familyParam, new ElementId(idValue));
                            break;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        parameterName = paramName,
                        currentType = familyManager.CurrentType?.Name,
                        message = "Parameter value set"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting parameter value");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Add Reference Plane

        /// <summary>
        /// Add a reference plane to the family.
        /// </summary>
        public static string AddReferencePlane(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var name = parameters["name"]?.ToString();
                var x1 = parameters["x1"]?.Value<double>() ?? 0;
                var y1 = parameters["y1"]?.Value<double>() ?? 0;
                var z1 = parameters["z1"]?.Value<double>() ?? 0;
                var x2 = parameters["x2"]?.Value<double>() ?? 10;
                var y2 = parameters["y2"]?.Value<double>() ?? 0;
                var z2 = parameters["z2"]?.Value<double>() ?? 0;

                var bubbleEnd = new XYZ(x1, y1, z1);
                var freeEnd = new XYZ(x2, y2, z2);
                var cutVector = XYZ.BasisZ;

                using (var trans = new Transaction(doc, "Add Reference Plane"))
                {
                    trans.Start();

                    var refPlane = doc.FamilyCreate.NewReferencePlane(bubbleEnd, freeEnd, cutVector, doc.ActiveView);

                    if (!string.IsNullOrEmpty(name))
                    {
                        refPlane.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        referencePlaneId = (int)refPlane.Id.Value,
                        name = refPlane.Name,
                        message = "Reference plane created"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding reference plane");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Create Extrusion

        /// <summary>
        /// Create an extrusion in the family document.
        /// </summary>
        public static string CreateExtrusion(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var profilePoints = parameters["profilePoints"] as JArray;
                if (profilePoints == null || profilePoints.Count < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least 3 profile points required",
                        example = "[{\"x\":0,\"y\":0},{\"x\":1,\"y\":0},{\"x\":1,\"y\":1},{\"x\":0,\"y\":1}]"
                    });
                }

                var startOffset = parameters["startOffset"]?.Value<double>() ?? 0;
                var endOffset = parameters["endOffset"]?.Value<double>() ?? 1;
                var isSolid = parameters["isSolid"]?.Value<bool>() ?? true;

                using (var trans = new Transaction(doc, "Create Extrusion"))
                {
                    trans.Start();

                    // Create sketch plane (XY plane at Z=0)
                    var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

                    // Create profile curves
                    var curveArray = new CurveArray();
                    var points = profilePoints.Select(p => new XYZ(
                        p["x"]?.Value<double>() ?? 0,
                        p["y"]?.Value<double>() ?? 0,
                        0
                    )).ToList();

                    // Close the profile
                    for (int i = 0; i < points.Count; i++)
                    {
                        var nextIndex = (i + 1) % points.Count;
                        curveArray.Append(Line.CreateBound(points[i], points[nextIndex]));
                    }

                    // Create curve array for profile
                    var curveArrayArray = new CurveArrArray();
                    curveArrayArray.Append(curveArray);

                    // Create extrusion
                    var extrusion = doc.FamilyCreate.NewExtrusion(isSolid, curveArrayArray, sketchPlane, endOffset - startOffset);

                    // Adjust base offset if needed
                    if (Math.Abs(startOffset) > 0.001)
                    {
                        ElementTransformUtils.MoveElement(doc, extrusion.Id, new XYZ(0, 0, startOffset));
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        extrusionId = (int)extrusion.Id.Value,
                        isSolid = isSolid,
                        startOffset = startOffset,
                        endOffset = endOffset,
                        message = "Extrusion created"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating extrusion");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Load Family Into Project

        /// <summary>
        /// Load the currently open family document back into a project.
        /// </summary>
        public static string LoadFamilyIntoProject(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var overwrite = parameters["overwriteExisting"]?.Value<bool>() ?? true;

                // Find the project document to load into
                Document targetDoc = null;
                var targetDocTitle = parameters["targetDocument"]?.ToString();

                foreach (Document openDoc in uiApp.Application.Documents)
                {
                    if (!openDoc.IsFamilyDocument)
                    {
                        if (string.IsNullOrEmpty(targetDocTitle) ||
                            openDoc.Title.Equals(targetDocTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            targetDoc = openDoc;
                            break;
                        }
                    }
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No project document found to load family into"
                    });
                }

                // The family document must be saved first before loading into project
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Family must be saved before loading into project. Use saveFamily first."
                    });
                }

                // Load family into project from file path
                Family loadedFamily = null;
                var familyLoadOptions = new FamilyLoadOptions(overwrite);

                using (var trans = new Transaction(targetDoc, "Load Family"))
                {
                    trans.Start();

                    var loaded = targetDoc.LoadFamily(doc.PathName, familyLoadOptions, out loadedFamily);

                    if (!loaded)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to load family (may already exist and overwrite is disabled)"
                        });
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyId = loadedFamily != null ? (int?)loadedFamily.Id.Value : null,
                    familyName = loadedFamily?.Name,
                    targetDocument = targetDoc.Title,
                    message = $"Family loaded into '{targetDoc.Title}'"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading family into project");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Save Family

        /// <summary>
        /// Save the family document.
        /// </summary>
        public static string SaveFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var savePath = parameters["path"]?.ToString();
                var saveAs = parameters["saveAs"]?.Value<bool>() ?? false;

                if (saveAs || string.IsNullOrEmpty(doc.PathName))
                {
                    if (string.IsNullOrEmpty(savePath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Path required for SaveAs or new family"
                        });
                    }

                    // Ensure .rfa extension
                    if (!savePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                    {
                        savePath += ".rfa";
                    }

                    doc.SaveAs(savePath);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = savePath,
                        message = "Family saved as new file"
                    });
                }
                else
                {
                    doc.Save();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path = doc.PathName,
                        message = "Family saved"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving family");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Close Family

        /// <summary>
        /// Close the family document.
        /// </summary>
        public static string CloseFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var save = parameters["save"]?.Value<bool>() ?? false;
                var familyName = doc.Title;

                if (save && doc.IsModified)
                {
                    doc.Save();
                }

                doc.Close(!save); // If save=false, discard changes

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    closedFamily = familyName,
                    saved = save,
                    message = $"Family '{familyName}' closed"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error closing family");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Family Categories

        /// <summary>
        /// Get available family categories for creating new families.
        /// </summary>
        public static string GetFamilyCategories(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No active document" });
                }

                var categories = new List<object>();
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.AllowsBoundParameters && cat.CategoryType == CategoryType.Model)
                    {
                        categories.Add(new
                        {
                            name = cat.Name,
                            id = (int)cat.Id.Value,
                            categoryType = cat.CategoryType.ToString()
                        });
                    }
                }

                categories = categories.OrderBy(c => ((dynamic)c).name).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = categories.Count,
                    categories = categories
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting family categories");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Create New Family Document

        /// <summary>
        /// Create a new family document from a template.
        /// </summary>
        public static string CreateNewFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var templatePath = parameters["templatePath"]?.ToString();
                if (string.IsNullOrEmpty(templatePath))
                {
                    // Try default Revit family templates location
                    var defaultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Autodesk", "Revit 2026", "Family Templates", "English");

                    if (Directory.Exists(defaultPath))
                    {
                        var templates = Directory.GetFiles(defaultPath, "*.rft");
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Template path required",
                            availableTemplates = templates.Select(Path.GetFileName).Take(20).ToList(),
                            templateFolder = defaultPath
                        });
                    }

                    return JsonConvert.SerializeObject(new { success = false, error = "Template path required" });
                }

                if (!File.Exists(templatePath))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Template not found: {templatePath}" });
                }

                // Create new family document (opens in background initially)
                var familyDoc = uiApp.Application.NewFamilyDocument(templatePath);

                if (familyDoc == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Failed to create family document" });
                }

                // Note: The document is created but may need to be made active manually.
                // It will appear in the document switcher in Revit.

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentTitle = familyDoc.Title,
                    isFamilyDocument = familyDoc.IsFamilyDocument,
                    message = "New family document created. Use Revit's window switcher to activate it."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating new family");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Create Model Line

        /// <summary>
        /// Create a model line in the family document.
        /// Model lines are actual geometry that shows in all views.
        /// Perfect for parking stripes, floor patterns, etc.
        /// </summary>
        public static string CreateModelLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                // Get line endpoints (in feet)
                var x1 = parameters["x1"]?.Value<double>() ?? 0;
                var y1 = parameters["y1"]?.Value<double>() ?? 0;
                var z1 = parameters["z1"]?.Value<double>() ?? 0;
                var x2 = parameters["x2"]?.Value<double>() ?? 1;
                var y2 = parameters["y2"]?.Value<double>() ?? 0;
                var z2 = parameters["z2"]?.Value<double>() ?? 0;

                var startPoint = new XYZ(x1, y1, z1);
                var endPoint = new XYZ(x2, y2, z2);

                // Validate line length
                if (startPoint.DistanceTo(endPoint) < 0.001)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Line too short - start and end points must be different"
                    });
                }

                using (var trans = new Transaction(doc, "Create Model Line"))
                {
                    trans.Start();

                    // Create sketch plane for the line
                    // Use XY plane at the Z level of the line
                    var zLevel = (z1 + z2) / 2;
                    Plane plane;

                    // Determine plane orientation based on line direction
                    var lineDir = (endPoint - startPoint).Normalize();
                    if (Math.Abs(lineDir.Z) > 0.99)
                    {
                        // Vertical line - use XZ plane
                        plane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(x1, y1, zLevel));
                    }
                    else
                    {
                        // Horizontal or diagonal - use XY plane
                        plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, zLevel));
                    }

                    var sketchPlane = SketchPlane.Create(doc, plane);

                    // Create the model line
                    var line = Line.CreateBound(startPoint, endPoint);
                    var modelLine = doc.FamilyCreate.NewModelCurve(line, sketchPlane);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        modelLineId = (int)modelLine.Id.Value,
                        startPoint = new { x = x1, y = y1, z = z1 },
                        endPoint = new { x = x2, y = y2, z = z2 },
                        length = startPoint.DistanceTo(endPoint),
                        message = "Model line created"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating model line");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Create Model Lines (Batch)

        /// <summary>
        /// Create multiple model lines at once - more efficient for complex shapes.
        /// Pass an array of line segments.
        /// </summary>
        public static string CreateModelLines(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var lines = parameters["lines"] as JArray;
                if (lines == null || lines.Count == 0)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Lines array required",
                        example = "[{\"x1\":0,\"y1\":0,\"x2\":1,\"y2\":0},{\"x1\":1,\"y1\":0,\"x2\":1,\"y2\":0.5}]"
                    });
                }

                var createdLines = new List<object>();
                var errors = new List<string>();

                using (var trans = new Transaction(doc, "Create Model Lines"))
                {
                    trans.Start();

                    // Create a single sketch plane at Z=0 for all lines (assuming 2D family)
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    var sketchPlane = SketchPlane.Create(doc, plane);

                    foreach (var lineData in lines)
                    {
                        try
                        {
                            var x1 = lineData["x1"]?.Value<double>() ?? 0;
                            var y1 = lineData["y1"]?.Value<double>() ?? 0;
                            var z1 = lineData["z1"]?.Value<double>() ?? 0;
                            var x2 = lineData["x2"]?.Value<double>() ?? 0;
                            var y2 = lineData["y2"]?.Value<double>() ?? 0;
                            var z2 = lineData["z2"]?.Value<double>() ?? 0;

                            var startPoint = new XYZ(x1, y1, z1);
                            var endPoint = new XYZ(x2, y2, z2);

                            if (startPoint.DistanceTo(endPoint) < 0.001)
                            {
                                errors.Add($"Line too short at ({x1},{y1}) to ({x2},{y2})");
                                continue;
                            }

                            var line = Line.CreateBound(startPoint, endPoint);
                            var modelLine = doc.FamilyCreate.NewModelCurve(line, sketchPlane);

                            createdLines.Add(new
                            {
                                id = (int)modelLine.Id.Value,
                                start = new { x = x1, y = y1, z = z1 },
                                end = new { x = x2, y = y2, z = z2 }
                            });
                        }
                        catch (Exception lineEx)
                        {
                            errors.Add($"Line error: {lineEx.Message}");
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    createdCount = createdLines.Count,
                    lines = createdLines,
                    errorCount = errors.Count,
                    errors = errors.Count > 0 ? errors : null,
                    message = $"Created {createdLines.Count} model lines"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating model lines");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Create Symbolic Line

        /// <summary>
        /// Create a symbolic line in the family document.
        /// Symbolic lines only show in the view they're created in (plan, elevation, etc.)
        /// Good for annotation-style graphics.
        /// </summary>
        public static string CreateSymbolicLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var x1 = parameters["x1"]?.Value<double>() ?? 0;
                var y1 = parameters["y1"]?.Value<double>() ?? 0;
                var x2 = parameters["x2"]?.Value<double>() ?? 1;
                var y2 = parameters["y2"]?.Value<double>() ?? 0;

                var startPoint = new XYZ(x1, y1, 0);
                var endPoint = new XYZ(x2, y2, 0);

                if (startPoint.DistanceTo(endPoint) < 0.001)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Line too short"
                    });
                }

                using (var trans = new Transaction(doc, "Create Symbolic Line"))
                {
                    trans.Start();

                    var line = Line.CreateBound(startPoint, endPoint);
                    var symbolicLine = doc.FamilyCreate.NewSymbolicCurve(line, doc.ActiveView.SketchPlane);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        symbolicLineId = (int)symbolicLine.Id.Value,
                        startPoint = new { x = x1, y = y1 },
                        endPoint = new { x = x2, y = y2 },
                        length = startPoint.DistanceTo(endPoint),
                        message = "Symbolic line created"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating symbolic line");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Create Model Arc

        /// <summary>
        /// Create a model arc in the family document.
        /// Useful for curved parking stripes, radius corners, etc.
        /// </summary>
        public static string CreateModelArc(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                // Arc can be defined by center/radius/angles or by 3 points
                var useThreePoints = parameters["useThreePoints"]?.Value<bool>() ?? false;

                Arc arc;

                if (useThreePoints)
                {
                    // Three point arc
                    var x1 = parameters["x1"]?.Value<double>() ?? 0;
                    var y1 = parameters["y1"]?.Value<double>() ?? 0;
                    var x2 = parameters["x2"]?.Value<double>() ?? 0.5;
                    var y2 = parameters["y2"]?.Value<double>() ?? 0.5;
                    var x3 = parameters["x3"]?.Value<double>() ?? 1;
                    var y3 = parameters["y3"]?.Value<double>() ?? 0;

                    var p1 = new XYZ(x1, y1, 0);
                    var p2 = new XYZ(x2, y2, 0);
                    var p3 = new XYZ(x3, y3, 0);

                    arc = Arc.Create(p1, p3, p2);
                }
                else
                {
                    // Center/radius/angles
                    var centerX = parameters["centerX"]?.Value<double>() ?? 0;
                    var centerY = parameters["centerY"]?.Value<double>() ?? 0;
                    var radius = parameters["radius"]?.Value<double>() ?? 1;
                    var startAngle = parameters["startAngle"]?.Value<double>() ?? 0; // in degrees
                    var endAngle = parameters["endAngle"]?.Value<double>() ?? 90; // in degrees

                    var center = new XYZ(centerX, centerY, 0);
                    var startRad = startAngle * Math.PI / 180;
                    var endRad = endAngle * Math.PI / 180;

                    arc = Arc.Create(center, radius, startRad, endRad, XYZ.BasisX, XYZ.BasisY);
                }

                using (var trans = new Transaction(doc, "Create Model Arc"))
                {
                    trans.Start();

                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    var sketchPlane = SketchPlane.Create(doc, plane);

                    var modelArc = doc.FamilyCreate.NewModelCurve(arc, sketchPlane);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        modelArcId = (int)modelArc.Id.Value,
                        center = new { x = arc.Center.X, y = arc.Center.Y },
                        radius = arc.Radius,
                        length = arc.Length,
                        message = "Model arc created"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating model arc");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Family Geometry

        /// <summary>
        /// Get all geometry elements in the current family document.
        /// Useful for understanding what's already in a family before modifying it.
        /// </summary>
        public static string GetFamilyGeometry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                // Get model lines
                var modelLines = new FilteredElementCollector(doc)
                    .OfClass(typeof(CurveElement))
                    .Cast<CurveElement>()
                    .Where(c => c is ModelCurve || c is ModelLine || c is ModelArc)
                    .Select(c => new
                    {
                        id = (int)c.Id.Value,
                        type = c.GetType().Name,
                        curveType = c.GeometryCurve?.GetType().Name,
                        length = c.GeometryCurve?.Length ?? 0,
                        startPoint = c.GeometryCurve?.GetEndPoint(0) != null ? new {
                            x = c.GeometryCurve.GetEndPoint(0).X,
                            y = c.GeometryCurve.GetEndPoint(0).Y,
                            z = c.GeometryCurve.GetEndPoint(0).Z
                        } : null,
                        endPoint = c.GeometryCurve?.GetEndPoint(1) != null ? new {
                            x = c.GeometryCurve.GetEndPoint(1).X,
                            y = c.GeometryCurve.GetEndPoint(1).Y,
                            z = c.GeometryCurve.GetEndPoint(1).Z
                        } : null
                    })
                    .ToList();

                // Get symbolic lines
                var symbolicLines = new FilteredElementCollector(doc)
                    .OfClass(typeof(SymbolicCurve))
                    .Cast<SymbolicCurve>()
                    .Select(c => new
                    {
                        id = (int)c.Id.Value,
                        type = "SymbolicCurve",
                        curveType = c.GeometryCurve?.GetType().Name,
                        length = c.GeometryCurve?.Length ?? 0
                    })
                    .ToList();

                // Get extrusions
                var extrusions = new FilteredElementCollector(doc)
                    .OfClass(typeof(Extrusion))
                    .Cast<Extrusion>()
                    .Select(e => new
                    {
                        id = (int)e.Id.Value,
                        type = "Extrusion",
                        isSolid = e.IsSolid
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    modelLineCount = modelLines.Count,
                    modelLines = modelLines,
                    symbolicLineCount = symbolicLines.Count,
                    symbolicLines = symbolicLines,
                    extrusionCount = extrusions.Count,
                    extrusions = extrusions,
                    totalGeometry = modelLines.Count + symbolicLines.Count + extrusions.Count
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting family geometry");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Delete Family Element

        /// <summary>
        /// Delete an element from the family document.
        /// </summary>
        public static string DeleteFamilyElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var elementId = parameters["elementId"]?.Value<int>();
                if (elementId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId required" });
                }

                var id = new ElementId(elementId.Value);
                var element = doc.GetElement(id);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var elementName = element.Name;
                var elementType = element.GetType().Name;

                using (var trans = new Transaction(doc, "Delete Family Element"))
                {
                    trans.Start();
                    doc.Delete(id);
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    deletedId = elementId,
                    deletedType = elementType,
                    message = $"Deleted {elementType} element"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting family element");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Associate Geometry With Parameter

        /// <summary>
        /// Associate a geometry element's visibility with a Yes/No parameter.
        /// This makes the geometry show/hide based on the parameter value.
        /// </summary>
        public static string AssociateGeometryWithParameter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var elementId = parameters["elementId"]?.Value<int>();
                var parameterName = parameters["parameterName"]?.ToString();

                if (elementId == null || string.IsNullOrEmpty(parameterName))
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "elementId and parameterName are required"
                    });
                }

                var element = doc.GetElement(new ElementId(elementId.Value));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                // Get the visibility parameter on the element
                var visParam = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                if (visParam == null)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Element does not have a visibility parameter"
                    });
                }

                // Get the family parameter to associate with
                var familyManager = doc.FamilyManager;
                var familyParam = familyManager.get_Parameter(parameterName);
                if (familyParam == null)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = $"Family parameter '{parameterName}' not found"
                    });
                }

                using (var trans = new Transaction(doc, "Associate Visibility"))
                {
                    trans.Start();

                    // Associate the element's visibility with the family parameter
                    familyManager.AssociateElementParameterToFamilyParameter(visParam, familyParam);

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId,
                    parameterName = parameterName,
                    message = $"Element visibility now controlled by '{parameterName}'"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error associating geometry with parameter");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Line Styles

        /// <summary>
        /// Get all available line styles in the family document.
        /// </summary>
        public static string GetLineStyles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var lineStyles = new List<object>();
                var categories = doc.Settings.Categories;
                var linesCat = categories.get_Item(BuiltInCategory.OST_Lines);

                if (linesCat != null)
                {
                    foreach (Category subCat in linesCat.SubCategories)
                    {
                        lineStyles.Add(new
                        {
                            id = (int)subCat.Id.Value,
                            name = subCat.Name,
                            lineWeight = subCat.GetLineWeight(GraphicsStyleType.Projection)
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = lineStyles.Count,
                    lineStyles = lineStyles.OrderBy(ls => ((dynamic)ls).name).ToList()
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting line styles");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Set Line Style

        /// <summary>
        /// Set the line style/subcategory for a curve element.
        /// </summary>
        public static string SetLineStyle(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var elementId = parameters["elementId"]?.Value<int>();
                var lineStyleName = parameters["lineStyleName"]?.ToString();
                var lineStyleId = parameters["lineStyleId"]?.Value<int>();

                if (elementId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId required" });
                }

                var element = doc.GetElement(new ElementId(elementId.Value));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var curveElement = element as CurveElement;
                if (curveElement == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element is not a curve" });
                }

                // Find the line style
                GraphicsStyle lineStyle = null;
                var categories = doc.Settings.Categories;
                var linesCat = categories.get_Item(BuiltInCategory.OST_Lines);

                if (lineStyleId != null)
                {
                    lineStyle = doc.GetElement(new ElementId(lineStyleId.Value)) as GraphicsStyle;
                }
                else if (!string.IsNullOrEmpty(lineStyleName))
                {
                    foreach (Category subCat in linesCat.SubCategories)
                    {
                        if (subCat.Name.Equals(lineStyleName, StringComparison.OrdinalIgnoreCase))
                        {
                            lineStyle = subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                            break;
                        }
                    }
                }

                if (lineStyle == null)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Line style not found. Use getLineStyles to see available options."
                    });
                }

                using (var trans = new Transaction(doc, "Set Line Style"))
                {
                    trans.Start();
                    curveElement.LineStyle = lineStyle;
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId,
                    lineStyle = lineStyle.Name,
                    message = $"Line style set to '{lineStyle.Name}'"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting line style");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Element Constraints

        /// <summary>
        /// Get all constraints (dimensions, alignments) on an element.
        /// </summary>
        public static string GetElementConstraints(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var elementId = parameters["elementId"]?.Value<int>();
                if (elementId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId required" });
                }

                var element = doc.GetElement(new ElementId(elementId.Value));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                // Get dimensions that reference this element
                var dimensions = new FilteredElementCollector(doc)
                    .OfClass(typeof(Dimension))
                    .Cast<Dimension>()
                    .Where(d => d.References != null &&
                           d.References.Cast<Reference>().Any(r => r.ElementId == element.Id))
                    .Select(d => new
                    {
                        id = (int)d.Id.Value,
                        value = d.Value,
                        isLocked = d.IsLocked,
                        labelParameter = d.FamilyLabel?.Definition?.Name
                    })
                    .ToList();

                // Check if element is on a sketch plane
                string sketchPlaneName = null;
                if (element is CurveElement curveElem)
                {
                    var sketchPlane = curveElem.SketchPlane;
                    sketchPlaneName = sketchPlane?.Name;
                }

                // Get visibility parameter association
                var visParam = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                string visibilityParameter = null;
                if (visParam != null)
                {
                    var familyManager = doc.FamilyManager;
                    var assocParam = familyManager.GetAssociatedFamilyParameter(visParam);
                    if (assocParam != null)
                    {
                        visibilityParameter = assocParam.Definition.Name;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId,
                    elementType = element.GetType().Name,
                    sketchPlane = sketchPlaneName,
                    visibilityControlledBy = visibilityParameter,
                    dimensionCount = dimensions.Count,
                    dimensions = dimensions
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting element constraints");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Add Dimension

        /// <summary>
        /// Add a dimension between references in the family.
        /// Can optionally label the dimension with a parameter for parametric control.
        /// Supports:
        ///   - Two-point dimensions: elementId1, elementId2
        ///   - Multi-point EQ dimensions: elementIds array with setEQ=true
        /// For EQ dimensions (e.g., Left-Center-Right), use elementIds=[leftId, centerId, rightId] with setEQ=true
        /// </summary>
        public static string AddDimension(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var labelParameter = parameters["labelParameter"]?.ToString();
                var offset = parameters["offset"]?.Value<double>() ?? 1.0;
                var setEQ = parameters["setEQ"]?.Value<bool>() ?? false;

                // Support both old style (elementId1, elementId2) and new style (elementIds array)
                var elementIds = parameters["elementIds"]?.ToObject<int[]>();
                var elementId1 = parameters["elementId1"]?.Value<int>();
                var elementId2 = parameters["elementId2"]?.Value<int>();

                // Build element list
                List<Element> elements = new List<Element>();

                if (elementIds != null && elementIds.Length >= 2)
                {
                    // New style: array of element IDs
                    foreach (var id in elementIds)
                    {
                        var elem = doc.GetElement(new ElementId(id));
                        if (elem != null) elements.Add(elem);
                    }
                }
                else if (elementId1.HasValue && elementId2.HasValue)
                {
                    // Old style: two element IDs
                    var e1 = doc.GetElement(new ElementId(elementId1.Value));
                    var e2 = doc.GetElement(new ElementId(elementId2.Value));
                    if (e1 != null) elements.Add(e1);
                    if (e2 != null) elements.Add(e2);
                }

                if (elements.Count < 2)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "At least 2 valid element IDs required. Use elementIds array or elementId1/elementId2"
                    });
                }

                var elem1 = elements.First();
                var elem2 = elements.Last();

                if (elem1 == null || elem2 == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "One or both elements not found" });
                }

                // Find a suitable plan view for the dimension
                View planView = null;
                var viewCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.EngineeringPlan));

                planView = viewCollector.FirstOrDefault();
                if (planView == null)
                {
                    // Try elevation views
                    planView = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Elevation);
                }
                if (planView == null)
                {
                    planView = doc.ActiveView;
                }

                using (var trans = new Transaction(doc, "Add Dimension"))
                {
                    trans.Start();

                    // Get references from elements
                    Reference ref1 = null;
                    Reference ref2 = null;
                    XYZ point1 = XYZ.Zero;
                    XYZ point2 = XYZ.Zero;
                    XYZ direction1 = XYZ.Zero;
                    XYZ direction2 = XYZ.Zero;

                    // For reference planes, get the reference and geometry info
                    if (elem1 is ReferencePlane rp1)
                    {
                        ref1 = rp1.GetReference();
                        point1 = (rp1.BubbleEnd + rp1.FreeEnd) / 2;
                        direction1 = (rp1.FreeEnd - rp1.BubbleEnd).Normalize();
                    }
                    else if (elem1 is CurveElement ce1)
                    {
                        ref1 = ce1.GeometryCurve.Reference;
                        point1 = (ce1.GeometryCurve.GetEndPoint(0) + ce1.GeometryCurve.GetEndPoint(1)) / 2;
                    }

                    if (elem2 is ReferencePlane rp2)
                    {
                        ref2 = rp2.GetReference();
                        point2 = (rp2.BubbleEnd + rp2.FreeEnd) / 2;
                        direction2 = (rp2.FreeEnd - rp2.BubbleEnd).Normalize();
                    }
                    else if (elem2 is CurveElement ce2)
                    {
                        ref2 = ce2.GeometryCurve.Reference;
                        point2 = (ce2.GeometryCurve.GetEndPoint(0) + ce2.GeometryCurve.GetEndPoint(1)) / 2;
                    }

                    if (ref1 == null || ref2 == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new {
                            success = false,
                            error = "Could not get references from elements"
                        });
                    }

                    // Create reference array with ALL elements (for multi-point dimensions)
                    var refArray = new ReferenceArray();
                    var allPoints = new List<XYZ>();

                    foreach (var elem in elements)
                    {
                        Reference elemRef = null;
                        XYZ elemPoint = XYZ.Zero;

                        if (elem is ReferencePlane rp)
                        {
                            elemRef = rp.GetReference();
                            elemPoint = (rp.BubbleEnd + rp.FreeEnd) / 2;
                        }
                        else if (elem is CurveElement ce)
                        {
                            elemRef = ce.GeometryCurve.Reference;
                            elemPoint = (ce.GeometryCurve.GetEndPoint(0) + ce.GeometryCurve.GetEndPoint(1)) / 2;
                        }

                        if (elemRef != null)
                        {
                            refArray.Append(elemRef);
                            allPoints.Add(elemPoint);
                        }
                    }

                    // Calculate the dimension line
                    // For parallel reference planes, the dimension line must be PERPENDICULAR to the planes
                    XYZ dimLineStart, dimLineEnd;

                    // Get the vector between the two reference plane centers
                    var vectorBetween = point2 - point1;
                    var distance = vectorBetween.GetLength();

                    if (distance < 0.001)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new {
                            success = false,
                            error = "Reference planes are at the same location"
                        });
                    }

                    // The dimension line should be perpendicular to the reference planes
                    // For vertical planes (Left/Right), dimension line is horizontal
                    // For horizontal planes (Front/Back), dimension line is vertical in plan

                    // Calculate points on each reference plane for the dimension line
                    // Use the common Y (or X) coordinate offset by the offset parameter
                    if (elem1 is ReferencePlane refPlane1 && elem2 is ReferencePlane refPlane2)
                    {
                        // Determine if planes are vertical (running in Y direction) or horizontal (running in X direction)
                        var dir1 = (refPlane1.FreeEnd - refPlane1.BubbleEnd).Normalize();
                        var isVerticalPlane = Math.Abs(dir1.X) < 0.1; // Plane runs in Y direction

                        if (isVerticalPlane)
                        {
                            // Planes are vertical (Left/Right) - dimension line is horizontal at Y = offset
                            var y = offset;
                            var z = (point1.Z + point2.Z) / 2;
                            dimLineStart = new XYZ(point1.X, y, z);
                            dimLineEnd = new XYZ(point2.X, y, z);
                        }
                        else
                        {
                            // Planes are horizontal (Front/Back) - dimension line is vertical at X = offset
                            var x = offset;
                            var z = (point1.Z + point2.Z) / 2;
                            dimLineStart = new XYZ(x, point1.Y, z);
                            dimLineEnd = new XYZ(x, point2.Y, z);
                        }
                    }
                    else
                    {
                        // Fallback: use direct line between points with offset
                        var midPoint = (point1 + point2) / 2;
                        var perpVector = vectorBetween.CrossProduct(XYZ.BasisZ).Normalize();
                        if (perpVector.IsZeroLength())
                        {
                            perpVector = vectorBetween.CrossProduct(XYZ.BasisY).Normalize();
                        }
                        dimLineStart = point1 + perpVector * offset;
                        dimLineEnd = point2 + perpVector * offset;
                    }

                    // Ensure the dimension line has sufficient length
                    var dimLineLength = dimLineStart.DistanceTo(dimLineEnd);
                    if (dimLineLength < 0.01)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new {
                            success = false,
                            error = $"Dimension line too short: {dimLineLength:F4} ft. Check that reference planes are not at the same position."
                        });
                    }

                    var dimLine = Line.CreateBound(dimLineStart, dimLineEnd);

                    // Create the dimension
                    Dimension dimension = null;
                    try
                    {
                        dimension = doc.FamilyCreate.NewDimension(planView, dimLine, refArray);
                    }
                    catch (Exception dimEx)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new {
                            success = false,
                            error = $"Failed to create dimension: {dimEx.Message}",
                            dimLineStart = new { x = dimLineStart.X, y = dimLineStart.Y, z = dimLineStart.Z },
                            dimLineEnd = new { x = dimLineEnd.X, y = dimLineEnd.Y, z = dimLineEnd.Z },
                            dimLineLength = dimLineLength,
                            viewName = planView?.Name
                        });
                    }

                    if (dimension == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new {
                            success = false,
                            error = "Dimension creation returned null"
                        });
                    }

                    // Set EQ constraint if requested (for multi-segment dimensions)
                    bool eqApplied = false;
                    if (setEQ && dimension.Segments != null && dimension.Segments.Size > 1)
                    {
                        try
                        {
                            dimension.AreSegmentsEqual = true;
                            eqApplied = true;
                        }
                        catch (Exception eqEx)
                        {
                            // EQ might not be applicable for all dimension types
                            Log.Debug($"Could not set EQ: {eqEx.Message}");
                        }
                    }

                    // Label with parameter if specified
                    string labelResult = null;
                    if (!string.IsNullOrEmpty(labelParameter))
                    {
                        var familyManager = doc.FamilyManager;
                        var familyParam = familyManager.get_Parameter(labelParameter);
                        if (familyParam != null)
                        {
                            try
                            {
                                dimension.FamilyLabel = familyParam;
                                labelResult = $"Labeled with '{labelParameter}'";
                            }
                            catch (Exception labelEx)
                            {
                                labelResult = $"Could not label: {labelEx.Message}";
                            }
                        }
                        else
                        {
                            labelResult = $"Parameter '{labelParameter}' not found";
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        dimensionId = (int)dimension.Id.Value,
                        value = dimension.Value,
                        segmentCount = dimension.Segments?.Size ?? 1,
                        isEQ = eqApplied,
                        labelParameter = labelParameter,
                        labelResult = labelResult,
                        viewUsed = planView?.Name,
                        message = eqApplied ? "EQ dimension created" : "Dimension created"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error adding dimension");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static XYZ GetElementLocation(Element elem)
        {
            if (elem is ReferencePlane rp)
            {
                return (rp.BubbleEnd + rp.FreeEnd) / 2;
            }
            else if (elem is CurveElement ce)
            {
                return (ce.GeometryCurve.GetEndPoint(0) + ce.GeometryCurve.GetEndPoint(1)) / 2;
            }
            return XYZ.Zero;
        }

        #endregion

        #region Constrain To Reference Plane

        /// <summary>
        /// Lock/align a curve element's endpoint to a reference plane.
        /// </summary>
        public static string ConstrainToReferencePlane(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var elementId = parameters["elementId"]?.Value<int>();
                var referencePlaneId = parameters["referencePlaneId"]?.Value<int>();
                var referencePlaneName = parameters["referencePlaneName"]?.ToString();
                var endpoint = parameters["endpoint"]?.Value<int>() ?? 0; // 0 = start, 1 = end

                if (elementId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId required" });
                }

                var element = doc.GetElement(new ElementId(elementId.Value));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var curveElement = element as CurveElement;
                if (curveElement == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element is not a curve" });
                }

                // Find reference plane
                ReferencePlane refPlane = null;
                if (referencePlaneId != null)
                {
                    refPlane = doc.GetElement(new ElementId(referencePlaneId.Value)) as ReferencePlane;
                }
                else if (!string.IsNullOrEmpty(referencePlaneName))
                {
                    refPlane = new FilteredElementCollector(doc)
                        .OfClass(typeof(ReferencePlane))
                        .Cast<ReferencePlane>()
                        .FirstOrDefault(rp => rp.Name.Equals(referencePlaneName, StringComparison.OrdinalIgnoreCase));
                }

                if (refPlane == null)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Reference plane not found"
                    });
                }

                using (var trans = new Transaction(doc, "Constrain to Reference Plane"))
                {
                    trans.Start();

                    // Get the curve's endpoint
                    var curve = curveElement.GeometryCurve;
                    var point = curve.GetEndPoint(endpoint);

                    // Create alignment constraint
                    // Note: Direct alignment via API is limited, we use dimension with lock instead
                    var refPlaneRef = refPlane.GetReference();

                    // Get reference from curve endpoint
                    var curveRef = curve.GetEndPointReference(endpoint);

                    if (curveRef != null && refPlaneRef != null)
                    {
                        var refArray = new ReferenceArray();
                        refArray.Append(curveRef);
                        refArray.Append(refPlaneRef);

                        // Create a zero-length locked dimension (this acts as an alignment)
                        var dimLine = Line.CreateBound(point, point + XYZ.BasisX);
                        var dim = doc.FamilyCreate.NewDimension(doc.ActiveView, dimLine, refArray);
                        dim.IsLocked = true;

                        trans.Commit();

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            elementId = elementId,
                            referencePlane = refPlane.Name,
                            endpoint = endpoint,
                            constraintDimensionId = (int)dim.Id.Value,
                            message = $"Endpoint {endpoint} constrained to '{refPlane.Name}'"
                        });
                    }
                    else
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new {
                            success = false,
                            error = "Could not get references for constraint"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error constraining to reference plane");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Open Nested Family

        /// <summary>
        /// Open a nested family for editing from within the current family.
        /// </summary>
        public static string OpenNestedFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var nestedFamilyName = parameters["familyName"]?.ToString();
                var nestedFamilyId = parameters["familyId"]?.Value<int>();

                Family nestedFamily = null;

                if (nestedFamilyId != null)
                {
                    nestedFamily = doc.GetElement(new ElementId(nestedFamilyId.Value)) as Family;
                }
                else if (!string.IsNullOrEmpty(nestedFamilyName))
                {
                    nestedFamily = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(nestedFamilyName, StringComparison.OrdinalIgnoreCase));
                }

                if (nestedFamily == null)
                {
                    // List available nested families
                    var availableFamilies = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .Select(f => new { id = (int)f.Id.Value, name = f.Name })
                        .ToList();

                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Nested family not found",
                        availableNestedFamilies = availableFamilies
                    });
                }

                if (!nestedFamily.IsEditable)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Nested family is not editable"
                    });
                }

                // Open the nested family for editing
                var nestedDoc = doc.EditFamily(nestedFamily);
                if (nestedDoc == null)
                {
                    return JsonConvert.SerializeObject(new {
                        success = false,
                        error = "Failed to open nested family"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    nestedFamilyName = nestedFamily.Name,
                    nestedFamilyId = (int)nestedFamily.Id.Value,
                    documentTitle = nestedDoc.Title,
                    message = $"Opened nested family '{nestedFamily.Name}' for editing"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening nested family");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Get Nested Families

        /// <summary>
        /// List all nested families in the current family document.
        /// </summary>
        public static string GetNestedFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var nestedFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Select(f => new
                    {
                        id = (int)f.Id.Value,
                        name = f.Name,
                        category = f.FamilyCategory?.Name,
                        isEditable = f.IsEditable,
                        isInPlace = f.IsInPlace
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = nestedFamilies.Count,
                    nestedFamilies = nestedFamilies
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting nested families");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Move Element

        /// <summary>
        /// Move an element within the family document.
        /// </summary>
        public static string MoveFamilyElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsFamilyDocument)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Not in family editor" });
                }

                var elementId = parameters["elementId"]?.Value<int>();
                var dx = parameters["dx"]?.Value<double>() ?? 0;
                var dy = parameters["dy"]?.Value<double>() ?? 0;
                var dz = parameters["dz"]?.Value<double>() ?? 0;

                if (elementId == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "elementId required" });
                }

                var element = doc.GetElement(new ElementId(elementId.Value));
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var translation = new XYZ(dx, dy, dz);

                using (var trans = new Transaction(doc, "Move Family Element"))
                {
                    trans.Start();
                    ElementTransformUtils.MoveElement(doc, element.Id, translation);
                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId,
                    moved = new { dx, dy, dz },
                    message = "Element moved"
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error moving family element");
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Options for loading families - handles overwrite scenarios
        /// </summary>
        private class FamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwriteExisting;

            public FamilyLoadOptions(bool overwriteExisting)
            {
                _overwriteExisting = overwriteExisting;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwriteExisting;
                return _overwriteExisting;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwriteExisting;
                return _overwriteExisting;
            }
        }

        #endregion
    }
}
