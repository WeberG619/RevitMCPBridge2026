using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

// Suppress obsolete API warnings for backward compatibility with older Revit models
#pragma warning disable CS0618

namespace RevitMCPBridge
{
    /// <summary>
    /// Project setup and utility methods - Grids, Levels, Site, Groups, Element operations
    /// </summary>
    public static class ProjectSetupMethods
    {
        #region Level Methods

        /// <summary>
        /// Create a new level
        /// </summary>
        public static string CreateLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "[DEBUG-LEVEL] No active document. uiApp or ActiveUIDocument is null."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No Revit project is currently open. Please open or create a project first."
                    });
                }

                if (parameters["elevation"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elevation is required"
                    });
                }

                var elevation = parameters["elevation"].ToObject<double>();
                var name = parameters["name"]?.ToString();

                using (var trans = new Transaction(doc, "Create Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var level = Level.Create(doc, elevation);

                    if (!string.IsNullOrEmpty(name))
                    {
                        level.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        levelId = (int)level.Id.Value,
                        name = level.Name,
                        elevation = level.Elevation
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a level
        /// </summary>
        public static string DeleteLevel(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Level"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(levelId);
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "Level deleted"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Document Creation Methods

        /// <summary>
        /// Create a new Revit project document
        /// </summary>
        public static string CreateNewProject(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                // Get optional template path
                var templatePath = parameters["templatePath"]?.ToString();
                var projectName = parameters["projectName"]?.ToString() ?? "New Project";
                var useMetric = parameters["useMetric"]?.ToObject<bool>() ?? false;

                Document newDoc = null;

                if (!string.IsNullOrEmpty(templatePath) && System.IO.File.Exists(templatePath))
                {
                    // Create from template
                    newDoc = app.NewProjectDocument(templatePath);
                }
                else
                {
                    // Create blank project with appropriate units
                    var unitSystem = useMetric ? UnitSystem.Metric : UnitSystem.Imperial;
                    newDoc = app.NewProjectDocument(unitSystem);
                }

                if (newDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to create new project document"
                    });
                }

                // Set project info if requested
                if (!string.IsNullOrEmpty(projectName))
                {
                    using (var trans = new Transaction(newDoc, "Set Project Name"))
                    {
                        trans.Start();
                        var failureOptions = trans.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                        trans.SetFailureHandlingOptions(failureOptions);

                        var projectInfo = newDoc.ProjectInformation;
                        if (projectInfo != null)
                        {
                            projectInfo.Name = projectName;
                        }

                        trans.Commit();
                    }
                }

                // Save to temp file and reopen to make it the active document
                // NewProjectDocument creates the document but doesn't activate it in the UI
                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevitMCP");
                System.IO.Directory.CreateDirectory(tempDir);
                var tempPath = System.IO.Path.Combine(tempDir,
                    "Project_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".rvt");

                // Get info before saving (in case close clears it)
                var levels = new FilteredElementCollector(newDoc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .Select(l => new { id = (int)l.Id.Value, name = l.Name, elevation = l.Elevation })
                    .ToList();
                var docTitle = newDoc.Title;
                var projName = newDoc.ProjectInformation?.Name ?? projectName;

                try
                {
                    // Save, close, and reopen to activate
                    newDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                    newDoc.Close(false);
                    uiApp.OpenAndActivateDocument(tempPath);
                }
                catch (Exception saveEx)
                {
                    // If save/reopen fails, document still exists — just not active
                    System.Diagnostics.Debug.WriteLine($"CreateNewProject save/reopen: {saveEx.Message}");
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    projectName = projName,
                    documentTitle = docTitle,
                    filePath = tempPath,
                    isMetric = useMetric,
                    usedTemplate = !string.IsNullOrEmpty(templatePath),
                    levels = levels,
                    levelCount = levels.Count,
                    message = "New project created and activated successfully."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get list of available project templates
        /// </summary>
        public static string GetProjectTemplates(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var templates = new List<object>();

                // Common template locations
                var templatePaths = new[]
                {
                    @"C:\ProgramData\Autodesk\RVT 2026\Templates\US Imperial",
                    @"C:\ProgramData\Autodesk\RVT 2026\Templates\US Metric",
                    @"C:\ProgramData\Autodesk\RVT 2026\Family Templates",
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) + @"\Autodesk\RVT 2026\Templates"
                };

                foreach (var basePath in templatePaths)
                {
                    if (System.IO.Directory.Exists(basePath))
                    {
                        var files = System.IO.Directory.GetFiles(basePath, "*.rte", System.IO.SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            templates.Add(new
                            {
                                name = System.IO.Path.GetFileNameWithoutExtension(file),
                                path = file,
                                folder = System.IO.Path.GetDirectoryName(file)
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    templateCount = templates.Count,
                    templates = templates
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Grid Methods

        /// <summary>
        /// Create a linear grid
        /// </summary>
        public static string CreateGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var startPoint = parameters["startPoint"].ToObject<double[]>();
                var endPoint = parameters["endPoint"].ToObject<double[]>();
                var name = parameters["name"]?.ToString();

                using (var trans = new Transaction(doc, "Create Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var start = new XYZ(startPoint[0], startPoint[1], startPoint[2]);
                    var end = new XYZ(endPoint[0], endPoint[1], endPoint[2]);
                    var line = Line.CreateBound(start, end);

                    var grid = Grid.Create(doc, line);

                    if (!string.IsNullOrEmpty(name))
                    {
                        grid.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        gridId = (int)grid.Id.Value,
                        name = grid.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create an arc grid
        /// </summary>
        public static string CreateArcGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var center = parameters["center"].ToObject<double[]>();
                var radius = parameters["radius"].ToObject<double>();
                var startAngle = parameters["startAngle"]?.ToObject<double>() ?? 0;
                var endAngle = parameters["endAngle"]?.ToObject<double>() ?? Math.PI;
                var name = parameters["name"]?.ToString();

                using (var trans = new Transaction(doc, "Create Arc Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var centerPoint = new XYZ(center[0], center[1], center[2]);
                    var arc = Arc.Create(centerPoint, radius, startAngle, endAngle, XYZ.BasisX, XYZ.BasisY);

                    var grid = Grid.Create(doc, arc);

                    if (!string.IsNullOrEmpty(name))
                    {
                        grid.Name = name;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        gridId = (int)grid.Id.Value,
                        name = grid.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all grids in the project
        /// </summary>
        public static string GetGrids(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Select(g => new
                    {
                        gridId = (int)g.Id.Value,
                        name = g.Name,
                        isCurved = g.IsCurved
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    gridCount = grids.Count,
                    grids = grids
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a grid
        /// </summary>
        public static string DeleteGrid(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var gridId = new ElementId(int.Parse(parameters["gridId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Grid"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(gridId);
                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "Grid deleted"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Site Methods

        /// <summary>
        /// Create a topography surface from points
        /// </summary>
        public static string CreateTopography(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["points"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "points array is required (each point needs x, y, z)"
                    });
                }

                var pointsData = parameters["points"].ToObject<double[][]>();
                var points = new List<XYZ>();

                foreach (var p in pointsData)
                {
                    points.Add(new XYZ(p[0], p[1], p[2]));
                }

                if (points.Count < 3)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least 3 points are required"
                    });
                }

                using (var trans = new Transaction(doc, "Create Topography"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var topo = TopographySurface.Create(doc, points);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topographyId = (int)topo.Id.Value,
                        pointCount = points.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add points to existing topography
        /// </summary>
        public static string ModifyTopography(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var topoId = new ElementId(int.Parse(parameters["topographyId"].ToString()));
                var topo = doc.GetElement(topoId) as TopographySurface;

                if (topo == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Topography not found"
                    });
                }

                using (var trans = new Transaction(doc, "Modify Topography"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Add points
                    if (parameters["addPoints"] != null)
                    {
                        var addPointsData = parameters["addPoints"].ToObject<double[][]>();
                        var addPoints = addPointsData.Select(p => new XYZ(p[0], p[1], p[2])).ToList();

                        using (var editScope = new TopographyEditScope(doc, "Add Points"))
                        {
                            editScope.Start(topoId);
                            topo.AddPoints(addPoints);
                            editScope.Commit(new TopographyEditFailuresPreprocessor());
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        topographyId = (int)topoId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a building pad on topography
        /// </summary>
        public static string CreateBuildingPad(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var points = parameters["boundaryPoints"].ToObject<double[][]>();
                var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));

                // Get building pad type
                BuildingPadType padType = null;
                if (parameters["padTypeId"] != null)
                {
                    var padTypeId = new ElementId(int.Parse(parameters["padTypeId"].ToString()));
                    padType = doc.GetElement(padTypeId) as BuildingPadType;
                }
                else
                {
                    padType = new FilteredElementCollector(doc)
                        .OfClass(typeof(BuildingPadType))
                        .Cast<BuildingPadType>()
                        .FirstOrDefault();
                }

                if (padType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No building pad type found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Building Pad"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create curve loop
                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], points[i][2]);
                        var end = new XYZ(points[(i + 1) % points.Length][0],
                                         points[(i + 1) % points.Length][1],
                                         points[(i + 1) % points.Length][2]);
                        curveLoop.Append(Line.CreateBound(start, end));
                    }

                    var curveLoops = new List<CurveLoop> { curveLoop };
                    var pad = BuildingPad.Create(doc, padType.Id, levelId, curveLoops);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        buildingPadId = (int)pad.Id.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Element Utility Methods

        /// <summary>
        /// Copy elements with translation
        /// </summary>
        public static string CopyElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();
                var translation = parameters["translation"].ToObject<double[]>();

                using (var trans = new Transaction(doc, "Copy Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var vector = new XYZ(translation[0], translation[1], translation[2]);
                    var copiedIds = ElementTransformUtils.CopyElements(
                        doc, elementIds, vector);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        copiedElementIds = copiedIds.Select(id => (int)id.Value).ToList(),
                        count = copiedIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move elements by translation
        /// </summary>
        public static string MoveElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();
                var translation = parameters["translation"].ToObject<double[]>();

                using (var trans = new Transaction(doc, "Move Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var vector = new XYZ(translation[0], translation[1], translation[2]);
                    ElementTransformUtils.MoveElements(doc, elementIds, vector);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        movedCount = elementIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rotate elements around an axis
        /// </summary>
        public static string RotateElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();
                var axisPoint = parameters["axisPoint"].ToObject<double[]>();
                var angle = parameters["angle"].ToObject<double>(); // in degrees

                using (var trans = new Transaction(doc, "Rotate Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(axisPoint[0], axisPoint[1], axisPoint[2]);
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    var radians = angle * Math.PI / 180.0;

                    ElementTransformUtils.RotateElements(doc, elementIds, axis, radians);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        rotatedCount = elementIds.Count,
                        angleDegrees = angle
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Mirror elements across a plane
        /// </summary>
        public static string MirrorElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();
                var planeOrigin = parameters["planeOrigin"].ToObject<double[]>();
                var planeNormal = parameters["planeNormal"].ToObject<double[]>();

                using (var trans = new Transaction(doc, "Mirror Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var origin = new XYZ(planeOrigin[0], planeOrigin[1], planeOrigin[2]);
                    var normal = new XYZ(planeNormal[0], planeNormal[1], planeNormal[2]);
                    var plane = Plane.CreateByNormalAndOrigin(normal, origin);

                    ElementTransformUtils.MirrorElements(doc, elementIds, plane, true);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        mirroredCount = elementIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a linear array of elements
        /// </summary>
        public static string ArrayElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();
                var count = parameters["count"].ToObject<int>();
                var spacing = parameters["spacing"].ToObject<double[]>();

                using (var trans = new Transaction(doc, "Array Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var allCopiedIds = new List<ElementId>();
                    var vector = new XYZ(spacing[0], spacing[1], spacing[2]);

                    for (int i = 1; i < count; i++)
                    {
                        var translation = vector * i;
                        var copiedIds = ElementTransformUtils.CopyElements(
                            doc, elementIds, translation);
                        allCopiedIds.AddRange(copiedIds);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        totalCopies = count - 1,
                        copiedElementIds = allCopiedIds.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete multiple elements
        /// </summary>
        public static string DeleteElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();

                using (var trans = new Transaction(doc, "Delete Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(elementIds);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedCount = elementIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy elements between documents
        /// </summary>
        public static string CopyElementsBetweenDocuments(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                // Get source and target document names
                if (parameters["sourceDocumentName"] == null || parameters["targetDocumentName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceDocumentName and targetDocumentName are required"
                    });
                }

                var sourceDocName = parameters["sourceDocumentName"].ToString();
                var targetDocName = parameters["targetDocumentName"].ToString();

                // Find documents
                Document sourceDoc = null;
                Document targetDoc = null;

                foreach (Document doc in app.Documents)
                {
                    if (doc.Title == sourceDocName || doc.Title.StartsWith(sourceDocName))
                    {
                        sourceDoc = doc;
                    }
                    if (doc.Title == targetDocName || doc.Title.StartsWith(targetDocName))
                    {
                        targetDoc = doc;
                    }
                }

                if (sourceDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source document '{sourceDocName}' not found"
                    });
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target document '{targetDocName}' not found"
                    });
                }

                // Get element IDs to copy
                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();

                // Set up copy options
                var copyOptions = new CopyPasteOptions();
                copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                // Perform the copy
                using (var trans = new Transaction(targetDoc, "Copy Elements Between Documents"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var copiedIds = ElementTransformUtils.CopyElements(
                        sourceDoc,
                        elementIds,
                        targetDoc,
                        Transform.Identity,
                        copyOptions);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        copiedCount = copiedIds.Count,
                        newElementIds = copiedIds.Select(id => (int)id.Value).ToList(),
                        sourceDocument = sourceDoc.Title,
                        targetDocument = targetDoc.Title
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Transfer loadable families between documents (cabinets, furniture, fixtures, etc.)
        /// </summary>
        public static string TransferFamilyBetweenDocuments(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                // Get source and target document names
                if (parameters["sourceDocumentName"] == null || parameters["targetDocumentName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceDocumentName and targetDocumentName are required"
                    });
                }

                var sourceDocName = parameters["sourceDocumentName"].ToString();
                var targetDocName = parameters["targetDocumentName"].ToString();

                // Find documents
                Document sourceDoc = null;
                Document targetDoc = null;

                foreach (Document doc in app.Documents)
                {
                    if (doc.Title == sourceDocName || doc.Title.StartsWith(sourceDocName))
                    {
                        sourceDoc = doc;
                    }
                    if (doc.Title == targetDocName || doc.Title.StartsWith(targetDocName))
                    {
                        targetDoc = doc;
                    }
                }

                if (sourceDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source document '{sourceDocName}' not found"
                    });
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target document '{targetDocName}' not found"
                    });
                }

                var elementIdsToCopy = new List<ElementId>();
                var familyInfo = new List<object>();

                // Option 1: Transfer by family name
                if (parameters["familyName"] != null)
                {
                    var familyName = parameters["familyName"].ToString();

                    // Find all family symbols (types) for this family
                    var familySymbols = new FilteredElementCollector(sourceDoc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(fs => fs.Family.Name == familyName || fs.Family.Name.Contains(familyName))
                        .ToList();

                    if (familySymbols.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"No family found matching '{familyName}' in source document"
                        });
                    }

                    foreach (var symbol in familySymbols)
                    {
                        elementIdsToCopy.Add(symbol.Id);
                        familyInfo.Add(new
                        {
                            familyName = symbol.Family.Name,
                            typeName = symbol.Name,
                            typeId = (int)symbol.Id.Value,
                            category = symbol.Category?.Name ?? "Unknown"
                        });
                    }
                }
                // Option 2: Transfer by category
                else if (parameters["categoryName"] != null)
                {
                    var categoryName = parameters["categoryName"].ToString();

                    // Find all family symbols in this category
                    var familySymbols = new FilteredElementCollector(sourceDoc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(fs => fs.Category != null &&
                               (fs.Category.Name == categoryName ||
                                fs.Category.Name.Contains(categoryName)))
                        .ToList();

                    if (familySymbols.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"No families found in category '{categoryName}' in source document"
                        });
                    }

                    foreach (var symbol in familySymbols)
                    {
                        elementIdsToCopy.Add(symbol.Id);
                        familyInfo.Add(new
                        {
                            familyName = symbol.Family.Name,
                            typeName = symbol.Name,
                            typeId = (int)symbol.Id.Value,
                            category = symbol.Category?.Name ?? "Unknown"
                        });
                    }
                }
                // Option 3: Transfer by specific type IDs
                else if (parameters["typeIds"] != null)
                {
                    var typeIds = parameters["typeIds"].ToObject<int[]>();

                    foreach (var id in typeIds)
                    {
                        var elementId = new ElementId(id);
                        var symbol = sourceDoc.GetElement(elementId) as FamilySymbol;

                        if (symbol != null)
                        {
                            elementIdsToCopy.Add(elementId);
                            familyInfo.Add(new
                            {
                                familyName = symbol.Family.Name,
                                typeName = symbol.Name,
                                typeId = id,
                                category = symbol.Category?.Name ?? "Unknown"
                            });
                        }
                    }

                    if (elementIdsToCopy.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No valid family types found for the provided IDs"
                        });
                    }
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Must provide familyName, categoryName, or typeIds"
                    });
                }

                // Set up copy options
                var copyOptions = new CopyPasteOptions();
                copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                // Perform the copy
                using (var trans = new Transaction(targetDoc, "Transfer Family"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var copiedIds = ElementTransformUtils.CopyElements(
                        sourceDoc,
                        elementIdsToCopy,
                        targetDoc,
                        Transform.Identity,
                        copyOptions);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        transferredCount = copiedIds.Count,
                        newTypeIds = copiedIds.Select(id => (int)id.Value).ToList(),
                        families = familyInfo,
                        sourceDocument = sourceDoc.Title,
                        targetDocument = targetDoc.Title
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all loadable families in a document by category
        /// </summary>
        public static string GetFamiliesByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var categoryFilter = parameters["categoryName"]?.ToString();

                var familySymbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => string.IsNullOrEmpty(categoryFilter) ||
                           (fs.Category != null &&
                            (fs.Category.Name == categoryFilter ||
                             fs.Category.Name.Contains(categoryFilter))))
                    .GroupBy(fs => fs.Family.Name)
                    .Select(g => new
                    {
                        familyName = g.Key,
                        category = g.First().Category?.Name ?? "Unknown",
                        typeCount = g.Count(),
                        types = g.Select(fs => new
                        {
                            typeId = (int)fs.Id.Value,
                            typeName = fs.Name
                        }).ToList()
                    })
                    .OrderBy(f => f.category)
                    .ThenBy(f => f.familyName)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyCount = familySymbols.Count,
                    families = familySymbols
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Group Methods

        /// <summary>
        /// Create a group from elements
        /// </summary>
        public static string CreateGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id)).ToList();
                var groupName = parameters["groupName"]?.ToString();

                using (var trans = new Transaction(doc, "Create Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var group = doc.Create.NewGroup(elementIds);

                    if (!string.IsNullOrEmpty(groupName))
                    {
                        group.GroupType.Name = groupName;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = (int)group.Id.Value,
                        groupTypeId = (int)group.GroupType.Id.Value,
                        groupName = group.GroupType.Name,
                        elementCount = elementIds.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a group instance
        /// </summary>
        public static string PlaceGroup(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypeId = new ElementId(int.Parse(parameters["groupTypeId"].ToString()));
                var location = parameters["location"].ToObject<double[]>();

                var groupType = doc.GetElement(groupTypeId) as GroupType;
                if (groupType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Group type not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Group"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(location[0], location[1], location[2]);
                    var group = doc.Create.PlaceGroup(point, groupType);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = (int)group.Id.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all group types
        /// </summary>
        public static string GetGroupTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(GroupType))
                    .Cast<GroupType>()
                    .Select(gt => new
                    {
                        groupTypeId = (int)gt.Id.Value,
                        name = gt.Name,
                        category = gt.Category?.Name ?? "Model"
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    groupTypeCount = groupTypes.Count,
                    groupTypes = groupTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Ungroup a group instance
        /// </summary>
        public static string UngroupElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var groupId = new ElementId(int.Parse(parameters["groupId"].ToString()));
                var group = doc.GetElement(groupId) as Group;

                if (group == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Group not found"
                    });
                }

                using (var trans = new Transaction(doc, "Ungroup"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var memberIds = group.UngroupMembers();

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        ungroupedElementIds = memberIds.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Helper Classes

        private class TopographyEditFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                return FailureProcessingResult.Continue;
            }
        }

        private class DuplicateTypeNamesHandler : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                // Use the destination types when duplicates are found
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        #endregion

        #region Base Point and Survey Point Methods

        /// <summary>
        /// Get the Project Base Point location
        /// </summary>
        public static string GetProjectBasePoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var basePoint = BasePoint.GetProjectBasePoint(doc);
                var position = basePoint.Position;

                // Get shared coordinates
                var sharedPosition = basePoint.SharedPosition;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)basePoint.Id.Value,
                    position = new { x = position.X, y = position.Y, z = position.Z },
                    sharedPosition = new { x = sharedPosition.X, y = sharedPosition.Y, z = sharedPosition.Z },
                    angle = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM)?.AsDouble() ?? 0
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set/Move the Project Base Point location
        /// </summary>
        public static string SetProjectBasePoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["position"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "position is required (array of [x, y, z])"
                    });
                }

                var posArray = parameters["position"].ToObject<double[]>();
                var newPosition = new XYZ(posArray[0], posArray[1], posArray[2]);

                using (var trans = new Transaction(doc, "Move Project Base Point"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var basePoint = BasePoint.GetProjectBasePoint(doc);

                    // Move the base point
                    var currentPos = basePoint.Position;
                    var translation = newPosition - currentPos;
                    ElementTransformUtils.MoveElement(doc, basePoint.Id, translation);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        newPosition = new { x = newPosition.X, y = newPosition.Y, z = newPosition.Z }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the Survey Point location
        /// </summary>
        public static string GetSurveyPoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var surveyPoint = BasePoint.GetSurveyPoint(doc);
                var position = surveyPoint.Position;
                var sharedPosition = surveyPoint.SharedPosition;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)surveyPoint.Id.Value,
                    position = new { x = position.X, y = position.Y, z = position.Z },
                    sharedPosition = new { x = sharedPosition.X, y = sharedPosition.Y, z = sharedPosition.Z },
                    angle = surveyPoint.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM)?.AsDouble() ?? 0
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set/Move the Survey Point location
        /// </summary>
        public static string SetSurveyPoint(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["position"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "position is required (array of [x, y, z])"
                    });
                }

                var posArray = parameters["position"].ToObject<double[]>();
                var newPosition = new XYZ(posArray[0], posArray[1], posArray[2]);

                using (var trans = new Transaction(doc, "Move Survey Point"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var surveyPoint = BasePoint.GetSurveyPoint(doc);

                    // Move the survey point
                    var currentPos = surveyPoint.Position;
                    var translation = newPosition - currentPos;
                    ElementTransformUtils.MoveElement(doc, surveyPoint.Id, translation);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        newPosition = new { x = newPosition.X, y = newPosition.Y, z = newPosition.Z }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Line Methods

        /// <summary>
        /// Create a model line (3D line in model space)
        /// </summary>
        public static string CreateModelLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "startPoint and endPoint are required"
                    });
                }

                var startArray = parameters["startPoint"].ToObject<double[]>();
                var endArray = parameters["endPoint"].ToObject<double[]>();
                var start = new XYZ(startArray[0], startArray[1], startArray[2]);
                var end = new XYZ(endArray[0], endArray[1], endArray[2]);

                using (var trans = new Transaction(doc, "Create Model Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create a sketch plane for the model line
                    var normal = XYZ.BasisZ;
                    var origin = start;

                    // If line is vertical, use different plane
                    var lineDir = (end - start).Normalize();
                    if (Math.Abs(lineDir.DotProduct(XYZ.BasisZ)) > 0.99)
                    {
                        normal = XYZ.BasisX;
                    }

                    var plane = Plane.CreateByNormalAndOrigin(normal, origin);
                    var sketchPlane = SketchPlane.Create(doc, plane);

                    var line = Line.CreateBound(start, end);
                    var modelLine = doc.Create.NewModelCurve(line, sketchPlane);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineId = (int)modelLine.Id.Value,
                        length = line.Length
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a detail line (2D line in view)
        /// </summary>
        public static string CreateDetailLine(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var view = uiApp.ActiveUIDocument.ActiveView;

                if (parameters["startPoint"] == null || parameters["endPoint"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "startPoint and endPoint are required"
                    });
                }

                // Get view if specified
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    view = doc.GetElement(viewId) as View;
                }

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid view"
                    });
                }

                var startArray = parameters["startPoint"].ToObject<double[]>();
                var endArray = parameters["endPoint"].ToObject<double[]>();
                var start = new XYZ(startArray[0], startArray[1], 0);
                var end = new XYZ(endArray[0], endArray[1], 0);

                using (var trans = new Transaction(doc, "Create Detail Line"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var line = Line.CreateBound(start, end);
                    var detailLine = doc.Create.NewDetailCurve(view, line);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        lineId = (int)detailLine.Id.Value,
                        viewId = (int)view.Id.Value,
                        length = line.Length
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy view-specific content (text notes, detail lines, etc.) between views in different documents.
        /// This solves the limitation where CopyElementsBetweenDocuments fails for view-specific elements.
        /// Use case: Copying legend/drafting view content after creating matching views.
        /// </summary>
        public static string CopyViewContentBetweenDocuments(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                // Validate required parameters
                if (parameters["sourceDocumentName"] == null || parameters["targetDocumentName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceDocumentName and targetDocumentName are required"
                    });
                }

                if (parameters["sourceViewId"] == null || parameters["targetViewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sourceViewId and targetViewId are required"
                    });
                }

                var sourceDocName = parameters["sourceDocumentName"].ToString();
                var targetDocName = parameters["targetDocumentName"].ToString();
                var sourceViewId = new ElementId(parameters["sourceViewId"].Value<int>());
                var targetViewId = new ElementId(parameters["targetViewId"].Value<int>());

                // Find documents
                Document sourceDoc = null;
                Document targetDoc = null;

                foreach (Document doc in app.Documents)
                {
                    if (doc.Title == sourceDocName || doc.Title.StartsWith(sourceDocName))
                    {
                        sourceDoc = doc;
                    }
                    if (doc.Title == targetDocName || doc.Title.StartsWith(targetDocName))
                    {
                        targetDoc = doc;
                    }
                }

                if (sourceDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source document '{sourceDocName}' not found"
                    });
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target document '{targetDocName}' not found"
                    });
                }

                // Get source view
                var sourceView = sourceDoc.GetElement(sourceViewId) as View;
                if (sourceView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Source view with ID {sourceViewId.Value} not found"
                    });
                }

                // Get target view
                var targetView = targetDoc.GetElement(targetViewId) as View;
                if (targetView == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target view with ID {targetViewId.Value} not found"
                    });
                }

                // Get all elements in source view that are view-specific
                var collector = new FilteredElementCollector(sourceDoc, sourceViewId);
                var allElements = collector.ToElements();

                // Filter out elements we don't want to copy
                var elementsToCopy = new List<ElementId>();
                foreach (var elem in allElements)
                {
                    // Skip null or invalid
                    if (elem == null || !elem.IsValidObject) continue;

                    // Skip ExtentElem and other system elements
                    var catName = elem.Category?.Name ?? "";
                    if (catName.Contains("Extent") || catName == "Cameras") continue;

                    // Skip the view itself
                    if (elem.Id == sourceViewId) continue;

                    // Include: TextNotes, DetailLines, DetailCurves, FilledRegions, Dimensions, etc.
                    elementsToCopy.Add(elem.Id);
                }

                if (elementsToCopy.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No copyable elements found in source view"
                    });
                }

                // Set up copy options
                var copyOptions = new CopyPasteOptions();
                copyOptions.SetDuplicateTypeNamesHandler(new DuplicateTypeNamesHandler());

                // Perform the view-to-view copy
                using (var trans = new Transaction(targetDoc, "Copy View Content Between Documents"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // This is the key API: view-to-view CopyElements for view-specific elements
                    var copiedIds = ElementTransformUtils.CopyElements(
                        sourceView,
                        elementsToCopy,
                        targetView,
                        Transform.Identity,
                        copyOptions);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sourceDocument = sourceDoc.Title,
                        targetDocument = targetDoc.Title,
                        sourceView = sourceView.Name,
                        targetView = targetView.Name,
                        sourceViewId = sourceViewId.Value,
                        targetViewId = targetViewId.Value,
                        elementsFound = elementsToCopy.Count,
                        elementsCopied = copiedIds.Count,
                        newElementIds = copiedIds.Select(id => (int)id.Value).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get sheets from a specific document by name
        /// </summary>
        public static string GetSheetsFromDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                if (parameters["documentName"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "documentName is required"
                    });
                }

                var docName = parameters["documentName"].ToString();
                Document targetDoc = null;

                foreach (Document doc in app.Documents)
                {
                    if (doc.Title == docName || doc.Title.StartsWith(docName))
                    {
                        targetDoc = doc;
                        break;
                    }
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Document '{docName}' not found among open documents"
                    });
                }

                var sheets = new FilteredElementCollector(targetDoc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .OrderBy(s => s.SheetNumber)
                    .Select(s => new
                    {
                        id = (int)s.Id.Value,
                        sheetNumber = s.SheetNumber,
                        sheetName = s.Name
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        documentName = targetDoc.Title,
                        totalSheets = sheets.Count,
                        sheets = sheets
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get detail lines from a specific view in a specific document
        /// </summary>
        public static string GetDetailLinesFromDocument(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var app = uiApp.Application;

                if (parameters["documentName"] == null || parameters["viewId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "documentName and viewId are required"
                    });
                }

                var docName = parameters["documentName"].ToString();
                var viewIdInt = parameters["viewId"].Value<int>();
                Document targetDoc = null;

                foreach (Document doc in app.Documents)
                {
                    if (doc.Title == docName || doc.Title.StartsWith(docName))
                    {
                        targetDoc = doc;
                        break;
                    }
                }

                if (targetDoc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Document '{docName}' not found among open documents"
                    });
                }

                var viewId = new ElementId(viewIdInt);
                var view = targetDoc.GetElement(viewId) as View;

                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found in document '{docName}'"
                    });
                }

                var detailLines = new FilteredElementCollector(targetDoc, viewId)
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .WhereElementIsNotElementType()
                    .Cast<CurveElement>()
                    .Where(ce => ce.LineStyle != null)
                    .ToList();

                var lines = detailLines.Select(line =>
                {
                    var curve = line.GeometryCurve;
                    var lineStyle = line.LineStyle as GraphicsStyle;
                    XYZ start = null, end = null;

                    if (curve is Line lineGeom)
                    {
                        start = lineGeom.GetEndPoint(0);
                        end = lineGeom.GetEndPoint(1);
                    }

                    return new
                    {
                        id = (int)line.Id.Value,
                        lineStyle = lineStyle?.Name ?? "Unknown",
                        lineStyleId = lineStyle != null ? (int)lineStyle.Id.Value : -1,
                        start = start != null ? new { x = Math.Round(start.X, 4), y = Math.Round(start.Y, 4), z = Math.Round(start.Z, 4) } : null,
                        end = end != null ? new { x = Math.Round(end.X, 4), y = Math.Round(end.Y, 4), z = Math.Round(end.Z, 4) } : null
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        documentName = targetDoc.Title,
                        viewId = viewIdInt,
                        viewName = view.Name,
                        lineCount = lines.Count,
                        lines = lines
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
