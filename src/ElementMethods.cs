using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// General element methods for location, placement, and bounding box operations
    /// </summary>
    public static class ElementMethods
    {
        #region Location Methods

        /// <summary>
        /// Get the location of any element (point or curve)
        /// Parameters:
        /// - elementId: ID of the element
        /// Returns location as point or curve endpoints
        /// </summary>
        public static string GetElementLocation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                var location = element.Location;

                if (location is LocationPoint locPt)
                {
                    var point = locPt.Point;
                    var rotation = locPt.Rotation;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)elementId.Value,
                        locationType = "Point",
                        point = new[] { point.X, point.Y, point.Z },
                        rotation = rotation * 180.0 / Math.PI // Convert to degrees
                    });
                }
                else if (location is LocationCurve locCurve)
                {
                    var curve = locCurve.Curve;
                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)elementId.Value,
                        locationType = "Curve",
                        startPoint = new[] { start.X, start.Y, start.Z },
                        endPoint = new[] { end.X, end.Y, end.Z },
                        length = curve.Length
                    });
                }
                else
                {
                    // Try to get bounding box center as fallback
                    var bbox = element.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        var center = (bbox.Min + bbox.Max) / 2;
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            elementId = (int)elementId.Value,
                            locationType = "BoundingBoxCenter",
                            point = new[] { center.X, center.Y, center.Z },
                            min = new[] { bbox.Min.X, bbox.Min.Y, bbox.Min.Z },
                            max = new[] { bbox.Max.X, bbox.Max.Y, bbox.Max.Z }
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element has no valid location"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the bounding box of any element
        /// Parameters:
        /// - elementId: ID of the element
        /// - viewId: (optional) view ID for view-specific bounding box
        /// </summary>
        public static string GetBoundingBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                View view = null;
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    view = doc.GetElement(viewId) as View;
                }

                var bbox = element.get_BoundingBox(view);

                if (bbox == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element has no bounding box"
                    });
                }

                var center = (bbox.Min + bbox.Max) / 2;
                var size = bbox.Max - bbox.Min;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)elementId.Value,
                    min = new[] { bbox.Min.X, bbox.Min.Y, bbox.Min.Z },
                    max = new[] { bbox.Max.X, bbox.Max.Y, bbox.Max.Z },
                    center = new[] { center.X, center.Y, center.Z },
                    size = new[] { size.X, size.Y, size.Z }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move an element by a specified offset (translation vector)
        /// Parameters:
        /// - elementId: ID of the element to move
        /// - offset: {x, y, z} offset in feet
        /// Or use absolute positioning:
        /// - elementId: ID of the element to move
        /// - targetLocation: {x, y, z} target location in feet
        /// </summary>
        public static string MoveElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                XYZ translation;

                // Check if using offset or target location
                if (parameters["offset"] != null)
                {
                    var offsetObj = parameters["offset"];
                    double x = offsetObj["x"]?.ToObject<double>() ?? 0;
                    double y = offsetObj["y"]?.ToObject<double>() ?? 0;
                    double z = offsetObj["z"]?.ToObject<double>() ?? 0;
                    translation = new XYZ(x, y, z);
                }
                else if (parameters["targetLocation"] != null)
                {
                    // Get current location and calculate offset
                    var targetObj = parameters["targetLocation"];
                    double targetX = targetObj["x"]?.ToObject<double>() ?? 0;
                    double targetY = targetObj["y"]?.ToObject<double>() ?? 0;
                    double targetZ = targetObj["z"]?.ToObject<double>() ?? 0;
                    var targetPoint = new XYZ(targetX, targetY, targetZ);

                    // Get current location
                    XYZ currentPoint = null;
                    var location = element.Location;
                    if (location is LocationPoint locPt)
                    {
                        currentPoint = locPt.Point;
                    }
                    else if (location is LocationCurve locCurve)
                    {
                        currentPoint = (locCurve.Curve.GetEndPoint(0) + locCurve.Curve.GetEndPoint(1)) / 2;
                    }
                    else
                    {
                        var bbox = element.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            currentPoint = (bbox.Min + bbox.Max) / 2;
                        }
                    }

                    if (currentPoint == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Cannot determine element's current location"
                        });
                    }

                    translation = targetPoint - currentPoint;
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Either 'offset' or 'targetLocation' is required"
                    });
                }

                using (var trans = new Transaction(doc, "Move Element"))
                {
                    trans.Start();

                    ElementTransformUtils.MoveElement(doc, elementId, translation);

                    trans.Commit();

                    // Get new location
                    XYZ newLocation = null;
                    var newLoc = element.Location;
                    if (newLoc is LocationPoint newLocPt)
                    {
                        newLocation = newLocPt.Point;
                    }
                    else if (newLoc is LocationCurve newLocCurve)
                    {
                        newLocation = (newLocCurve.Curve.GetEndPoint(0) + newLocCurve.Curve.GetEndPoint(1)) / 2;
                    }
                    else
                    {
                        var bbox = element.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            newLocation = (bbox.Min + bbox.Max) / 2;
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = (int)elementId.Value,
                        translation = new { x = translation.X, y = translation.Y, z = translation.Z },
                        newLocation = newLocation != null ? new { x = newLocation.X, y = newLocation.Y, z = newLocation.Z } : null,
                        message = "Element moved successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Move multiple elements by a specified offset
        /// Parameters:
        /// - elementIds: array of element IDs to move
        /// - offset: {x, y, z} offset in feet
        /// </summary>
        public static string MoveElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds is required"
                    });
                }

                if (parameters["offset"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "offset is required"
                    });
                }

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id))
                    .ToList();

                var offsetObj = parameters["offset"];
                double x = offsetObj["x"]?.ToObject<double>() ?? 0;
                double y = offsetObj["y"]?.ToObject<double>() ?? 0;
                double z = offsetObj["z"]?.ToObject<double>() ?? 0;
                var translation = new XYZ(x, y, z);

                using (var trans = new Transaction(doc, "Move Elements"))
                {
                    trans.Start();

                    ElementTransformUtils.MoveElements(doc, elementIds, translation);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        movedCount = elementIds.Count,
                        translation = new { x = translation.X, y = translation.Y, z = translation.Z },
                        message = $"{elementIds.Count} elements moved successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Instance Placement

        /// <summary>
        /// Place a family instance (casework, furniture, plumbing fixture, etc.)
        /// Parameters:
        /// - familyTypeId: ID of the family type to place
        /// - location: [x, y, z] point for placement
        /// - levelId: (optional) ID of the level to place on
        /// - rotation: (optional) rotation angle in degrees (default 0)
        /// - hostId: (optional) ID of the host element (for face-based families)
        /// </summary>
        public static string PlaceFamilyInstance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate required parameters
                if (parameters["familyTypeId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familyTypeId is required"
                    });
                }

                if (parameters["location"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "location is required"
                    });
                }

                var familyTypeId = new ElementId(int.Parse(parameters["familyTypeId"].ToString()));
                var familySymbol = doc.GetElement(familyTypeId) as FamilySymbol;

                if (familySymbol == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid family type ID or not a family type"
                    });
                }

                var locationArray = parameters["location"].ToObject<double[]>();
                var point = new XYZ(locationArray[0], locationArray[1], locationArray[2]);

                var rotation = (parameters["rotation"]?.ToObject<double>() ?? 0) * Math.PI / 180.0; // Convert to radians
                var shouldMirror = parameters["mirrored"]?.ToObject<bool>() ?? false;

                // Get hand orientation for mirror plane (if provided)
                XYZ handOrientation = XYZ.BasisY; // Default to Y axis
                if (parameters["handOrientation"] != null)
                {
                    var handArray = parameters["handOrientation"].ToObject<double[]>();
                    handOrientation = new XYZ(handArray[0], handArray[1], handArray[2]);
                }

                // Get target facing orientation for post-mirror correction
                XYZ targetFacing = null;
                if (parameters["facingOrientation"] != null)
                {
                    var facingArray = parameters["facingOrientation"].ToObject<double[]>();
                    targetFacing = new XYZ(facingArray[0], facingArray[1], 0).Normalize();
                }

                // Get level
                Level level = null;
                if (parameters["levelId"] != null)
                {
                    var levelId = new ElementId(int.Parse(parameters["levelId"].ToString()));
                    level = doc.GetElement(levelId) as Level;
                }
                else
                {
                    // Find the appropriate level based on Z coordinate
                    level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => Math.Abs(l.Elevation - point.Z))
                        .FirstOrDefault();
                }

                using (var trans = new Transaction(doc, "Place Family Instance"))
                {
                    trans.Start();

                    // Add failure handling to suppress warnings that would cause rollback
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Activate the family symbol if not active
                    if (!familySymbol.IsActive)
                    {
                        familySymbol.Activate();
                        doc.Regenerate();
                    }

                    FamilyInstance instance = null;

                    // Check if there's a host element
                    if (parameters["hostId"] != null)
                    {
                        var hostId = new ElementId(int.Parse(parameters["hostId"].ToString()));
                        var host = doc.GetElement(hostId);

                        if (host != null)
                        {
                            // Try to place on host
                            instance = doc.Create.NewFamilyInstance(point, familySymbol, host, level,
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        }
                    }

                    // Standard placement if no host or host placement failed
                    if (instance == null)
                    {
                        instance = doc.Create.NewFamilyInstance(point, familySymbol, level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    }

                    // Apply rotation if specified
                    if (rotation != 0)
                    {
                        var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation);
                    }

                    // Note: We no longer use MirrorElements as it moves elements to wrong positions.
                    // The mirrored flag from source affects internal appearance (door swing, etc.)
                    // but location and rotation are what matter for spatial positioning.
                    // If hand/facing flip is needed, it should be done through family parameters.

                    // Capture element ID and info before commit
                    if (instance == null)
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to create family instance - NewFamilyInstance returned null"
                        });
                    }

                    var instanceId = instance.Id.Value;
                    var familyName = familySymbol.Family.Name;
                    var typeName = familySymbol.Name;
                    var levelName = level?.Name ?? "None";

                    var commitStatus = trans.Commit();

                    if (commitStatus != TransactionStatus.Committed)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Transaction commit failed with status: {commitStatus}",
                            instanceId = (int)instanceId
                        });
                    }

                    // Verify element still exists after commit
                    var verifyElement = doc.GetElement(new ElementId((int)instanceId));
                    if (verifyElement == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Element was created but does not exist after commit - may have been deleted by warning resolution",
                            instanceId = (int)instanceId
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        instanceId = (int)instanceId,
                        familyName = familyName,
                        typeName = typeName,
                        location = new[] { point.X, point.Y, point.Z },
                        rotation = rotation * 180.0 / Math.PI,
                        mirrored = shouldMirror,
                        level = levelName
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available family types for a category
        /// Parameters:
        /// - category: Category name (e.g., "Casework", "Plumbing Fixtures", "Furniture")
        /// </summary>
        public static string GetFamilyInstanceTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["category"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "category is required"
                    });
                }

                var categoryName = parameters["category"].ToString();

                // Find the category
                BuiltInCategory? builtInCat = null;
                foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
                {
                    try
                    {
                        var cat = Category.GetCategory(doc, bic);
                        if (cat != null && cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                        {
                            builtInCat = bic;
                            break;
                        }
                    }
                    catch { }
                }

                if (builtInCat == null)
                {
                    // Try common category mappings
                    var categoryMap = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Casework", BuiltInCategory.OST_Casework },
                        { "Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures },
                        { "Furniture", BuiltInCategory.OST_Furniture },
                        { "Furniture Systems", BuiltInCategory.OST_FurnitureSystems },
                        { "Generic Models", BuiltInCategory.OST_GenericModel },
                        { "Specialty Equipment", BuiltInCategory.OST_SpecialityEquipment },
                        { "Electrical Fixtures", BuiltInCategory.OST_ElectricalFixtures },
                        { "Lighting Fixtures", BuiltInCategory.OST_LightingFixtures },
                        { "Mechanical Equipment", BuiltInCategory.OST_MechanicalEquipment }
                    };

                    if (categoryMap.TryGetValue(categoryName, out var mappedCat))
                    {
                        builtInCat = mappedCat;
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

                // Get all family symbols in this category
                var familyTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(builtInCat.Value)
                    .Cast<FamilySymbol>()
                    .Select(fs => new
                    {
                        typeId = (int)fs.Id.Value,
                        familyName = fs.Family.Name,
                        typeName = fs.Name,
                        fullName = $"{fs.Family.Name} - {fs.Name}"
                    })
                    .OrderBy(t => t.familyName)
                    .ThenBy(t => t.typeName)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    category = categoryName,
                    typeCount = familyTypes.Count,
                    familyTypes = familyTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Copy elements to a new location
        /// Parameters:
        /// - elementIds: array of element IDs to copy
        /// - translation: [x, y, z] translation vector
        /// </summary>
        public static string CopyElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds is required"
                    });
                }

                if (parameters["translation"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "translation is required"
                    });
                }

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id))
                    .ToList();

                var translationArray = parameters["translation"].ToObject<double[]>();
                var translation = new XYZ(translationArray[0], translationArray[1], translationArray[2]);

                using (var trans = new Transaction(doc, "Copy Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var copiedIds = ElementTransformUtils.CopyElements(
                        doc,
                        elementIds,
                        translation);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalCount = elementIds.Count,
                        copiedCount = copiedIds.Count,
                        copiedIds = copiedIds.Select(id => (int)id.Value).ToArray(),
                        translation = translationArray
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete elements by ID
        /// Parameters:
        /// - elementIds: array of element IDs to delete
        /// </summary>
        public static string DeleteElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementIds"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds is required"
                    });
                }

                var elementIds = parameters["elementIds"].ToObject<int[]>()
                    .Select(id => new ElementId(id))
                    .ToList();

                using (var trans = new Transaction(doc, "Delete Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var deletedIds = doc.Delete(elementIds);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        requestedCount = elementIds.Count,
                        deletedCount = deletedIds.Count,
                        deletedIds = deletedIds.Select(id => (int)id.Value).ToArray()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a single element by ID (convenience method)
        /// Parameters:
        /// - elementId: ID of the element to delete
        /// </summary>
        public static string DeleteElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["elementId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementId.Value} not found"
                    });
                }

                var elementName = element.Name ?? "Unnamed";
                var elementCategory = element.Category?.Name ?? "Unknown";

                using (var trans = new Transaction(doc, "Delete Element"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var deletedIds = doc.Delete(elementId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deletedElementId = (int)elementId.Value,
                        elementName = elementName,
                        elementCategory = elementCategory,
                        totalDeleted = deletedIds.Count,
                        message = $"Deleted element '{elementName}' and {deletedIds.Count - 1} dependent elements"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Family Loading

        /// <summary>
        /// Load a family from an .rfa file into the project
        /// Parameters:
        /// - familyPath: Full path to the .rfa file
        /// Returns the loaded family and its types
        /// </summary>
        public static string LoadFamily(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Null check for uiApp
                if (uiApp == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "UIApplication is null"
                    });
                }

                // Null check for ActiveUIDocument
                if (uiApp.ActiveUIDocument == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please open a Revit project first."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;
                if (doc == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Document is null"
                    });
                }

                // Parameter validation
                if (parameters == null || parameters["familyPath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familyPath is required"
                    });
                }

                var familyPath = parameters["familyPath"].ToString();

                if (string.IsNullOrEmpty(familyPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "familyPath cannot be empty"
                    });
                }

                if (!System.IO.File.Exists(familyPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"File not found: {familyPath}"
                    });
                }

                // Validate file extension
                if (!familyPath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "File must be a Revit family file (.rfa)"
                    });
                }

                Family family = null;
                bool loaded = false;
                bool alreadyLoaded = false;
                var familyName = System.IO.Path.GetFileNameWithoutExtension(familyPath);

                // First check if family is already loaded
                family = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (family != null)
                {
                    alreadyLoaded = true;
                    loaded = false;
                }
                else
                {
                    // Family not loaded, load it now
                    using (var trans = new Transaction(doc, "Load Family"))
                    {
                        trans.Start();

                        try
                        {
                            var failureOptions = trans.GetFailureHandlingOptions();
                            failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                            trans.SetFailureHandlingOptions(failureOptions);

                            loaded = doc.LoadFamily(familyPath, out family);

                            if (loaded && family != null)
                            {
                                trans.Commit();
                            }
                            else
                            {
                                trans.RollBack();

                                // Try once more to find it (LoadFamily returns false if already loaded)
                                family = new FilteredElementCollector(doc)
                                    .OfClass(typeof(Family))
                                    .Cast<Family>()
                                    .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                                if (family != null)
                                {
                                    alreadyLoaded = true;
                                }
                            }
                        }
                        catch (Exception loadEx)
                        {
                            if (trans.HasStarted() && !trans.HasEnded())
                            {
                                trans.RollBack();
                            }
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Error loading family: {loadEx.Message}",
                                familyPath = familyPath
                            });
                        }
                    }
                }

                // Final check - if we still don't have the family, fail
                if (family == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Failed to load family. The file may be corrupted or incompatible with this Revit version.",
                        familyPath = familyPath
                    });
                }

                // Get all types in the loaded family
                var typeIds = family.GetFamilySymbolIds();
                var familyTypes = new List<object>();

                if (typeIds != null)
                {
                    foreach (var id in typeIds)
                    {
                        var fs = doc.GetElement(id) as FamilySymbol;
                        if (fs != null)
                        {
                            familyTypes.Add(new
                            {
                                typeId = id.Value,
                                typeName = fs.Name,
                                fullName = $"{family.Name} - {fs.Name}"
                            });
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    loaded = loaded,
                    alreadyLoaded = alreadyLoaded,
                    familyId = family.Id.Value,
                    familyName = family.Name,
                    category = family.FamilyCategory?.Name ?? "Unknown",
                    typeCount = familyTypes.Count,
                    types = familyTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// List .rfa files in a directory (for browsing Revit library)
        /// Parameters:
        /// - directoryPath: Path to search (e.g., Revit library folder)
        /// - searchPattern: (optional) search pattern (default "*.rfa")
        /// - recursive: (optional) search subdirectories (default false)
        /// </summary>
        public static string ListFamilyFiles(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (parameters["directoryPath"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "directoryPath is required"
                    });
                }

                var directoryPath = parameters["directoryPath"].ToString();
                var searchPattern = parameters["searchPattern"]?.ToString() ?? "*.rfa";
                var recursive = parameters["recursive"]?.ToObject<bool>() ?? false;

                if (!System.IO.Directory.Exists(directoryPath))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Directory not found: {directoryPath}"
                    });
                }

                var searchOption = recursive ?
                    System.IO.SearchOption.AllDirectories :
                    System.IO.SearchOption.TopDirectoryOnly;

                var files = System.IO.Directory.GetFiles(directoryPath, searchPattern, searchOption)
                    .Select(path => new
                    {
                        path = path,
                        name = System.IO.Path.GetFileNameWithoutExtension(path),
                        directory = System.IO.Path.GetDirectoryName(path)
                    })
                    .OrderBy(f => f.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    directoryPath = directoryPath,
                    fileCount = files.Count,
                    files = files
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the default Revit library paths
        /// Returns common library locations for the current Revit version
        /// </summary>
        public static string GetLibraryPaths(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var paths = new List<object>();

                // Get Revit version year
                var version = uiApp.Application.VersionNumber;

                // Common library locations
                var commonPaths = new[]
                {
                    $@"C:\ProgramData\Autodesk\RVT {version}\Libraries\US Imperial",
                    $@"C:\ProgramData\Autodesk\RVT {version}\Libraries\US Metric",
                    $@"C:\ProgramData\Autodesk\RVT {version}\Libraries",
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + $@"\Revit {version}\Libraries"
                };

                foreach (var path in commonPaths)
                {
                    if (System.IO.Directory.Exists(path))
                    {
                        var subDirs = System.IO.Directory.GetDirectories(path)
                            .Select(d => System.IO.Path.GetFileName(d))
                            .ToArray();

                        paths.Add(new
                        {
                            path = path,
                            exists = true,
                            subdirectories = subDirs
                        });
                    }
                    else
                    {
                        paths.Add(new
                        {
                            path = path,
                            exists = false
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    revitVersion = version,
                    libraryPaths = paths
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all loaded families in the project
        /// Parameters:
        /// - category: (optional) filter by category name
        /// </summary>
        public static string GetLoadedFamilies(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document"
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Use OfType for safe casting
                var allFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .ToElements()
                    .OfType<Family>()
                    .Where(f => f != null);

                // Filter by category if specified
                if (parameters?["category"] != null)
                {
                    var categoryName = parameters["category"].ToString();
                    allFamilies = allFamilies.Where(f =>
                        f.FamilyCategory?.Name?.Equals(categoryName, StringComparison.OrdinalIgnoreCase) ?? false);
                }

                var families = new List<object>();
                foreach (var f in allFamilies)
                {
                    try
                    {
                        families.Add(new
                        {
                            familyId = (int)f.Id.Value,
                            name = f.Name ?? "",
                            category = f.FamilyCategory?.Name ?? "Unknown",
                            typeCount = f.GetFamilySymbolIds()?.Count ?? 0,
                            isEditable = f.IsEditable,
                            isInPlace = f.IsInPlace
                        });
                    }
                    catch { continue; }
                }

                var sortedFamilies = families
                    .Cast<dynamic>()
                    .OrderBy(f => (string)f.category)
                    .ThenBy(f => (string)f.name)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    familyCount = sortedFamilies.Count,
                    families = sortedFamilies
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Open the Load Autodesk Family dialog (cloud family library)
        /// This uses PostableCommand to open the dialog - user interaction required to select family
        /// </summary>
        public static string OpenLoadAutodeskFamilyDialog(UIApplication uiApp, JObject parameters)
        {
            try
            {
                // Look up the PostableCommand for Load Autodesk Family
                RevitCommandId commandId = RevitCommandId.LookupPostableCommandId(PostableCommand.LoadAutodeskFamily);

                if (commandId == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "LoadAutodeskFamily command not available in this version of Revit"
                    });
                }

                // Check if the command can be posted
                if (!uiApp.CanPostCommand(commandId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot post LoadAutodeskFamily command at this time. Ensure no modal dialogs are open."
                    });
                }

                // Post the command - this will open the dialog after the current API call completes
                uiApp.PostCommand(commandId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Load Autodesk Family dialog will open after this command completes. User interaction required to select and load the family.",
                    note = "The dialog accesses Autodesk's cloud-based family library. Requires internet connection and valid Autodesk subscription."
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Import/CAD Geometry Extraction Methods

        /// <summary>
        /// Get all imported CAD/PDF instances in the current view or document
        /// Returns basic info about each import instance
        /// Parameters:
        /// - viewId: (optional) Limit to specific view
        /// </summary>
        public static string GetImportedInstances(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                FilteredElementCollector collector;

                if (parameters != null && parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    var view = doc.GetElement(viewId) as View;
                    if (view == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                    }
                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var imports = collector
                    .OfClass(typeof(ImportInstance))
                    .Cast<ImportInstance>()
                    .Select(imp =>
                    {
                        var bbox = imp.get_BoundingBox(null);
                        var transform = imp.GetTransform();
                        return new
                        {
                            elementId = (int)imp.Id.Value,
                            name = imp.Name ?? "Unnamed",
                            isLinked = imp.IsLinked,
                            category = imp.Category?.Name ?? "Unknown",
                            origin = transform != null ? new[] { transform.Origin.X, transform.Origin.Y, transform.Origin.Z } : null,
                            boundingBoxMin = bbox != null ? new[] { bbox.Min.X, bbox.Min.Y, bbox.Min.Z } : null,
                            boundingBoxMax = bbox != null ? new[] { bbox.Max.X, bbox.Max.Y, bbox.Max.Z } : null
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = imports.Count,
                    imports = imports
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Extract geometry (lines, arcs, polylines) from an imported CAD/PDF instance
        /// This is the KEY method for tracing imported drawings
        /// Parameters:
        /// - importId: ID of the ImportInstance element
        /// - geometryOptions: (optional) "coarse", "medium", "fine" (default: "fine")
        /// - filterByLayer: (optional) only return geometry from specific layer name
        /// </summary>
        public static string GetImportedGeometry(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["importId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "importId is required" });
                }

                var importId = new ElementId(int.Parse(parameters["importId"].ToString()));
                var import = doc.GetElement(importId) as ImportInstance;

                if (import == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Import instance not found" });
                }

                // Set up geometry options
                var options = new Options();
                var detailLevel = parameters["geometryOptions"]?.ToString() ?? "fine";
                switch (detailLevel.ToLower())
                {
                    case "coarse":
                        options.DetailLevel = ViewDetailLevel.Coarse;
                        break;
                    case "medium":
                        options.DetailLevel = ViewDetailLevel.Medium;
                        break;
                    default:
                        options.DetailLevel = ViewDetailLevel.Fine;
                        break;
                }
                options.ComputeReferences = true;

                var filterLayer = parameters["filterByLayer"]?.ToString();

                var geometry = import.get_Geometry(options);
                if (geometry == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No geometry found in import" });
                }

                var transform = import.GetTransform();
                var lines = new List<object>();
                var arcs = new List<object>();
                var polylines = new List<object>();
                var otherGeometry = new List<object>();

                // Recursively extract geometry
                ExtractGeometryRecursive(geometry, transform, filterLayer, lines, arcs, polylines, otherGeometry);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    importId = (int)importId.Value,
                    importName = import.Name,
                    transform = new
                    {
                        origin = new[] { transform.Origin.X, transform.Origin.Y, transform.Origin.Z },
                        basisX = new[] { transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z },
                        basisY = new[] { transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z },
                        basisZ = new[] { transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z },
                        scale = transform.Scale
                    },
                    lineCount = lines.Count,
                    arcCount = arcs.Count,
                    polylineCount = polylines.Count,
                    otherCount = otherGeometry.Count,
                    lines = lines,
                    arcs = arcs,
                    polylines = polylines,
                    otherGeometry = otherGeometry
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper method to recursively extract geometry from GeometryElement
        /// </summary>
        private static void ExtractGeometryRecursive(
            GeometryElement geometryElement,
            Transform transform,
            string filterLayer,
            List<object> lines,
            List<object> arcs,
            List<object> polylines,
            List<object> otherGeometry)
        {
            foreach (GeometryObject geomObj in geometryElement)
            {
                if (geomObj is GeometryInstance geomInstance)
                {
                    // Get the geometry from the instance with its transform
                    var instanceGeom = geomInstance.GetInstanceGeometry(transform);
                    if (instanceGeom != null)
                    {
                        ExtractGeometryRecursive(instanceGeom, Transform.Identity, filterLayer, lines, arcs, polylines, otherGeometry);
                    }
                }
                else if (geomObj is Line line)
                {
                    var start = transform.OfPoint(line.GetEndPoint(0));
                    var end = transform.OfPoint(line.GetEndPoint(1));

                    lines.Add(new
                    {
                        type = "line",
                        startPoint = new { x = start.X, y = start.Y, z = start.Z },
                        endPoint = new { x = end.X, y = end.Y, z = end.Z },
                        length = line.Length
                    });
                }
                else if (geomObj is Arc arc)
                {
                    var start = transform.OfPoint(arc.GetEndPoint(0));
                    var end = transform.OfPoint(arc.GetEndPoint(1));
                    var center = transform.OfPoint(arc.Center);

                    arcs.Add(new
                    {
                        type = "arc",
                        startPoint = new { x = start.X, y = start.Y, z = start.Z },
                        endPoint = new { x = end.X, y = end.Y, z = end.Z },
                        center = new { x = center.X, y = center.Y, z = center.Z },
                        radius = arc.Radius,
                        length = arc.Length
                    });
                }
                else if (geomObj is PolyLine polyLine)
                {
                    var coords = polyLine.GetCoordinates()
                        .Select(pt =>
                        {
                            var transformed = transform.OfPoint(pt);
                            return new { x = transformed.X, y = transformed.Y, z = transformed.Z };
                        })
                        .ToList();

                    polylines.Add(new
                    {
                        type = "polyline",
                        pointCount = coords.Count,
                        points = coords
                    });
                }
                else if (geomObj is Curve curve)
                {
                    // Handle other curve types
                    var start = transform.OfPoint(curve.GetEndPoint(0));
                    var end = transform.OfPoint(curve.GetEndPoint(1));

                    otherGeometry.Add(new
                    {
                        type = curve.GetType().Name,
                        startPoint = new { x = start.X, y = start.Y, z = start.Z },
                        endPoint = new { x = end.X, y = end.Y, z = end.Z },
                        length = curve.Length
                    });
                }
                else if (geomObj is Solid solid)
                {
                    // Extract edges from solids
                    foreach (Edge edge in solid.Edges)
                    {
                        var edgeCurve = edge.AsCurve();
                        if (edgeCurve != null)
                        {
                            var start = transform.OfPoint(edgeCurve.GetEndPoint(0));
                            var end = transform.OfPoint(edgeCurve.GetEndPoint(1));

                            if (edgeCurve is Line)
                            {
                                lines.Add(new
                                {
                                    type = "line",
                                    startPoint = new { x = start.X, y = start.Y, z = start.Z },
                                    endPoint = new { x = end.X, y = end.Y, z = end.Z },
                                    length = edgeCurve.Length,
                                    source = "solid_edge"
                                });
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get a simplified list of just the line segments from an imported CAD/PDF
        /// Returns only lines (not arcs or curves) in a simple format for wall tracing
        /// Parameters:
        /// - importId: ID of the ImportInstance element
        /// - minLength: (optional) minimum line length to include (default 0.5 feet)
        /// - maxResults: (optional) maximum number of lines to return (default 1000)
        /// </summary>
        public static string GetImportedLines(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["importId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "importId is required" });
                }

                var importId = new ElementId(int.Parse(parameters["importId"].ToString()));
                var import = doc.GetElement(importId) as ImportInstance;

                if (import == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Import instance not found" });
                }

                var minLength = parameters["minLength"]?.ToObject<double>() ?? 0.5;
                var maxResults = parameters["maxResults"]?.ToObject<int>() ?? 1000;

                var options = new Options { DetailLevel = ViewDetailLevel.Fine };
                var geometry = import.get_Geometry(options);
                if (geometry == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No geometry found" });
                }

                var transform = import.GetTransform();
                var allLines = new List<object>();

                ExtractLinesOnly(geometry, transform, allLines);

                // Filter by minimum length and limit results
                var filteredLines = allLines
                    .Cast<dynamic>()
                    .Where(l => l.length >= minLength)
                    .OrderByDescending(l => l.length)
                    .Take(maxResults)
                    .ToList();

                // Calculate bounding box of all lines
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (dynamic line in filteredLines)
                {
                    minX = Math.Min(minX, Math.Min(line.startX, line.endX));
                    minY = Math.Min(minY, Math.Min(line.startY, line.endY));
                    maxX = Math.Max(maxX, Math.Max(line.startX, line.endX));
                    maxY = Math.Max(maxY, Math.Max(line.startY, line.endY));
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    importId = (int)importId.Value,
                    totalLinesFound = allLines.Count,
                    linesReturned = filteredLines.Count,
                    minLengthFilter = minLength,
                    boundingBox = new
                    {
                        minX = minX,
                        minY = minY,
                        maxX = maxX,
                        maxY = maxY,
                        width = maxX - minX,
                        height = maxY - minY
                    },
                    lines = filteredLines
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Helper to extract only lines from geometry
        /// </summary>
        private static void ExtractLinesOnly(GeometryElement geometryElement, Transform transform, List<object> lines)
        {
            foreach (GeometryObject geomObj in geometryElement)
            {
                if (geomObj is GeometryInstance geomInstance)
                {
                    var instanceGeom = geomInstance.GetInstanceGeometry(transform);
                    if (instanceGeom != null)
                    {
                        ExtractLinesOnly(instanceGeom, Transform.Identity, lines);
                    }
                }
                else if (geomObj is Line line)
                {
                    var start = transform.OfPoint(line.GetEndPoint(0));
                    var end = transform.OfPoint(line.GetEndPoint(1));

                    lines.Add(new
                    {
                        startX = Math.Round(start.X, 4),
                        startY = Math.Round(start.Y, 4),
                        endX = Math.Round(end.X, 4),
                        endY = Math.Round(end.Y, 4),
                        length = Math.Round(line.Length, 4)
                    });
                }
                else if (geomObj is Solid solid)
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        var edgeCurve = edge.AsCurve();
                        if (edgeCurve is Line edgeLine)
                        {
                            var start = transform.OfPoint(edgeLine.GetEndPoint(0));
                            var end = transform.OfPoint(edgeLine.GetEndPoint(1));

                            lines.Add(new
                            {
                                startX = Math.Round(start.X, 4),
                                startY = Math.Round(start.Y, 4),
                                endX = Math.Round(end.X, 4),
                                endY = Math.Round(end.Y, 4),
                                length = Math.Round(edgeLine.Length, 4)
                            });
                        }
                    }
                }
            }
        }

        #endregion

        #region Transform Methods

        /// <summary>
        /// Mirror elements about a line axis.
        /// </summary>
        /// <param name="elementIds">Array of element IDs to mirror</param>
        /// <param name="axisStart">Start point of mirror axis [x, y, z]</param>
        /// <param name="axisEnd">End point of mirror axis [x, y, z]</param>
        /// <param name="copyElements">If true, creates mirrored copies; if false, moves originals</param>
        public static string MirrorElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please ensure a Revit project is open."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var elementIds = parameters["elementIds"].ToObject<int[]>();
                var axisStart = parameters["axisStart"].ToObject<double[]>();
                var axisEnd = parameters["axisEnd"].ToObject<double[]>();
                var copyElements = parameters["copyElements"] != null
                    ? bool.Parse(parameters["copyElements"].ToString())
                    : true;

                // Convert to ElementId collection
                var elemIds = elementIds.Select(id => new ElementId(id)).ToList();

                // Create mirror axis line
                var startPoint = new XYZ(axisStart[0], axisStart[1], axisStart[2]);
                var endPoint = new XYZ(axisEnd[0], axisEnd[1], axisEnd[2]);
                var axisLine = Line.CreateBound(startPoint, endPoint);

                // Create a plane from the axis (vertical plane through axis)
                var axisDirection = (endPoint - startPoint).Normalize();
                var planeNormal = axisDirection.CrossProduct(XYZ.BasisZ).Normalize();
                if (planeNormal.IsZeroLength())
                {
                    // Axis is vertical, use X direction for normal
                    planeNormal = XYZ.BasisX;
                }
                var mirrorPlane = Plane.CreateByNormalAndOrigin(planeNormal, startPoint);

                using (var trans = new Transaction(doc, "Mirror Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    ICollection<ElementId> mirroredIds;
                    if (copyElements)
                    {
                        // Create mirrored copies
                        mirroredIds = ElementTransformUtils.MirrorElements(doc, elemIds, mirrorPlane, true);
                    }
                    else
                    {
                        // Mirror in place (move originals)
                        ElementTransformUtils.MirrorElements(doc, elemIds, mirrorPlane, false);
                        mirroredIds = elemIds;
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        originalCount = elementIds.Length,
                        mirroredIds = mirroredIds.Select(id => (int)id.Value).ToArray(),
                        copyElements = copyElements,
                        axisStart = axisStart,
                        axisEnd = axisEnd
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a linear or radial array of elements.
        /// </summary>
        /// <param name="elementIds">Array of element IDs to array</param>
        /// <param name="arrayType">Type: "linear" or "radial"</param>
        /// <param name="count">Number of copies (including original)</param>
        /// <param name="spacing">For linear: distance between copies; For radial: angle between copies (degrees)</param>
        /// <param name="direction">For linear: direction vector [x, y, z]; For radial: rotation axis [x, y, z]</param>
        /// <param name="centerPoint">For radial: center point of rotation [x, y, z]</param>
        public static string ArrayElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please ensure a Revit project is open."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var elementIds = parameters["elementIds"].ToObject<int[]>();
                var arrayType = parameters["arrayType"]?.ToString() ?? "linear";
                var count = int.Parse(parameters["count"].ToString());
                var spacing = double.Parse(parameters["spacing"].ToString());
                var direction = parameters["direction"]?.ToObject<double[]>() ?? new double[] { 1, 0, 0 };

                if (count < 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Count must be at least 2"
                    });
                }

                var elemIds = elementIds.Select(id => new ElementId(id)).ToList();
                var allNewIds = new List<int>();

                using (var trans = new Transaction(doc, "Array Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (arrayType.ToLower() == "linear")
                    {
                        // Linear array
                        var dirVector = new XYZ(direction[0], direction[1], direction[2]).Normalize();

                        for (int i = 1; i < count; i++)
                        {
                            var offset = dirVector.Multiply(spacing * i);
                            var copiedIds = ElementTransformUtils.CopyElements(doc, elemIds, offset);
                            allNewIds.AddRange(copiedIds.Select(id => (int)id.Value));
                        }
                    }
                    else if (arrayType.ToLower() == "radial")
                    {
                        // Radial array
                        var centerPoint = parameters["centerPoint"]?.ToObject<double[]>() ?? new double[] { 0, 0, 0 };
                        var center = new XYZ(centerPoint[0], centerPoint[1], centerPoint[2]);
                        var axis = new XYZ(direction[0], direction[1], direction[2]).Normalize();

                        // Create rotation axis line
                        var rotAxis = Line.CreateUnbound(center, axis);

                        for (int i = 1; i < count; i++)
                        {
                            // Copy elements first
                            var copiedIds = ElementTransformUtils.CopyElements(doc, elemIds, XYZ.Zero);

                            // Then rotate each copy
                            var angleRad = spacing * i * Math.PI / 180.0; // Convert degrees to radians
                            foreach (var copyId in copiedIds)
                            {
                                ElementTransformUtils.RotateElement(doc, copyId, rotAxis, angleRad);
                            }

                            allNewIds.AddRange(copiedIds.Select(id => (int)id.Value));
                        }
                    }
                    else
                    {
                        trans.RollBack();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown array type: {arrayType}. Use 'linear' or 'radial'"
                        });
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        arrayType = arrayType,
                        originalCount = elementIds.Length,
                        copyCount = count - 1,
                        spacing = spacing,
                        newElementIds = allNewIds
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Align elements to a reference point or line.
        /// </summary>
        /// <param name="elementIds">Array of element IDs to align</param>
        /// <param name="alignType">Type: "left", "right", "top", "bottom", "centerH", "centerV", or "toPoint"</param>
        /// <param name="referencePoint">For "toPoint": target point [x, y, z]; otherwise: optional reference point</param>
        public static string AlignElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please ensure a Revit project is open."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var elementIds = parameters["elementIds"].ToObject<int[]>();
                var alignType = parameters["alignType"]?.ToString() ?? "left";
                var referencePoint = parameters["referencePoint"]?.ToObject<double[]>();

                if (elementIds.Length == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No elements provided"
                    });
                }

                // Get bounding boxes of all elements
                var elements = new List<Element>();
                var boundingBoxes = new Dictionary<ElementId, BoundingBoxXYZ>();

                foreach (var id in elementIds)
                {
                    var elemId = new ElementId(id);
                    var element = doc.GetElement(elemId);
                    if (element != null)
                    {
                        elements.Add(element);
                        var bbox = element.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            boundingBoxes[elemId] = bbox;
                        }
                    }
                }

                if (elements.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No valid elements found"
                    });
                }

                // Calculate alignment target
                double targetValue = 0;
                bool alignX = false;
                bool alignY = false;
                bool alignToCenter = false;

                switch (alignType.ToLower())
                {
                    case "left":
                        targetValue = referencePoint != null ? referencePoint[0] : boundingBoxes.Values.Min(b => b.Min.X);
                        alignX = true;
                        break;
                    case "right":
                        targetValue = referencePoint != null ? referencePoint[0] : boundingBoxes.Values.Max(b => b.Max.X);
                        alignX = true;
                        break;
                    case "top":
                        targetValue = referencePoint != null ? referencePoint[1] : boundingBoxes.Values.Max(b => b.Max.Y);
                        alignY = true;
                        break;
                    case "bottom":
                        targetValue = referencePoint != null ? referencePoint[1] : boundingBoxes.Values.Min(b => b.Min.Y);
                        alignY = true;
                        break;
                    case "centerh":
                        var avgX = boundingBoxes.Values.Average(b => (b.Min.X + b.Max.X) / 2);
                        targetValue = referencePoint != null ? referencePoint[0] : avgX;
                        alignX = true;
                        alignToCenter = true;
                        break;
                    case "centerv":
                        var avgY = boundingBoxes.Values.Average(b => (b.Min.Y + b.Max.Y) / 2);
                        targetValue = referencePoint != null ? referencePoint[1] : avgY;
                        alignY = true;
                        alignToCenter = true;
                        break;
                    case "topoint":
                        if (referencePoint == null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "referencePoint is required for 'toPoint' alignment"
                            });
                        }
                        break;
                    default:
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown align type: {alignType}. Use 'left', 'right', 'top', 'bottom', 'centerH', 'centerV', or 'toPoint'"
                        });
                }

                using (var trans = new Transaction(doc, "Align Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var movedCount = 0;
                    foreach (var element in elements)
                    {
                        var bbox = boundingBoxes.GetValueOrDefault(element.Id);
                        if (bbox == null) continue;

                        XYZ translation;
                        if (alignType.ToLower() == "topoint")
                        {
                            // Move element center to reference point
                            var center = (bbox.Min + bbox.Max) / 2;
                            var target = new XYZ(referencePoint[0], referencePoint[1], center.Z);
                            translation = target - center;
                        }
                        else if (alignX)
                        {
                            var currentValue = alignToCenter ? (bbox.Min.X + bbox.Max.X) / 2 :
                                (alignType == "right" ? bbox.Max.X : bbox.Min.X);
                            translation = new XYZ(targetValue - currentValue, 0, 0);
                        }
                        else // alignY
                        {
                            var currentValue = alignToCenter ? (bbox.Min.Y + bbox.Max.Y) / 2 :
                                (alignType == "top" ? bbox.Max.Y : bbox.Min.Y);
                            translation = new XYZ(0, targetValue - currentValue, 0);
                        }

                        if (!translation.IsZeroLength())
                        {
                            ElementTransformUtils.MoveElement(doc, element.Id, translation);
                            movedCount++;
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        alignType = alignType,
                        elementCount = elements.Count,
                        movedCount = movedCount,
                        targetValue = alignType.ToLower() == "topoint" ? null : (object)targetValue
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Rotate elements around a point.
        /// </summary>
        /// <param name="elementIds">Array of element IDs to rotate</param>
        /// <param name="centerPoint">Center of rotation [x, y, z]</param>
        /// <param name="angle">Rotation angle in degrees</param>
        /// <param name="axis">Rotation axis (optional, defaults to Z axis) [x, y, z]</param>
        public static string RotateElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No active document. Please ensure a Revit project is open."
                    });
                }

                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var elementIds = parameters["elementIds"].ToObject<int[]>();
                var centerPoint = parameters["centerPoint"].ToObject<double[]>();
                var angleDegrees = double.Parse(parameters["angle"].ToString());
                var axis = parameters["axis"]?.ToObject<double[]>() ?? new double[] { 0, 0, 1 }; // Default Z axis

                var center = new XYZ(centerPoint[0], centerPoint[1], centerPoint[2]);
                var axisDirection = new XYZ(axis[0], axis[1], axis[2]).Normalize();
                var rotationAxis = Line.CreateUnbound(center, axisDirection);
                var angleRadians = angleDegrees * Math.PI / 180.0;

                var elemIds = elementIds.Select(id => new ElementId(id)).ToList();

                using (var trans = new Transaction(doc, "Rotate Elements"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (var elemId in elemIds)
                    {
                        ElementTransformUtils.RotateElement(doc, elemId, rotationAxis, angleRadians);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementCount = elementIds.Length,
                        centerPoint = centerPoint,
                        angleDegrees = angleDegrees,
                        axis = axis
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion
    }
}
