using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;
using RevitMCPBridge.Validation;

namespace RevitMCPBridge
{
    /// <summary>
    /// Coordination, clash detection, and spatial analysis methods for MCP Bridge
    /// </summary>
    public static class CoordinationMethods
    {
        // ──────────────────────────────────────────────
        // 1. checkInterference
        // ──────────────────────────────────────────────

        /// <summary>
        /// Run interference check between two sets of elements
        /// </summary>
        [MCPMethod("checkInterference", Category = "Coordination", Description = "Check geometric interference between two sets of elements")]
        public static string CheckInterference(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "checkInterference");
                v.Require("elementIdsA");
                v.Require("elementIdsB");
                v.ThrowIfInvalid();

                var idsA = parameters["elementIdsA"].ToObject<int[]>();
                var idsB = parameters["elementIdsB"].ToObject<int[]>();

                if (idsA == null || idsA.Length == 0)
                    return ResponseBuilder.Error("elementIdsA must be a non-empty array", "INVALID_PARAMETER").Build();
                if (idsB == null || idsB.Length == 0)
                    return ResponseBuilder.Error("elementIdsB must be a non-empty array", "INVALID_PARAMETER").Build();

                var clashes = new List<object>();

                foreach (var idA in idsA)
                {
                    var elemA = doc.GetElement(new ElementId(idA));
                    if (elemA == null) continue;

                    var filterA = new ElementIntersectsElementFilter(elemA);

                    foreach (var idB in idsB)
                    {
                        if (idA == idB) continue;

                        var elemB = doc.GetElement(new ElementId(idB));
                        if (elemB == null) continue;

                        // Check if B intersects A
                        if (filterA.PassesFilter(elemB))
                        {
                            var bbA = elemA.get_BoundingBox(null);
                            var bbB = elemB.get_BoundingBox(null);

                            var clash = new Dictionary<string, object>
                            {
                                ["elementIdA"] = idA,
                                ["elementNameA"] = elemA.Name,
                                ["categoryA"] = elemA.Category?.Name ?? "Unknown",
                                ["elementIdB"] = idB,
                                ["elementNameB"] = elemB.Name,
                                ["categoryB"] = elemB.Category?.Name ?? "Unknown"
                            };

                            if (bbA != null && bbB != null)
                            {
                                var centerA = (bbA.Min + bbA.Max) / 2.0;
                                var centerB = (bbB.Min + bbB.Max) / 2.0;
                                clash["locationA"] = new { x = centerA.X, y = centerA.Y, z = centerA.Z };
                                clash["locationB"] = new { x = centerB.X, y = centerB.Y, z = centerB.Z };

                                // Intersection region approximation (overlap of bounding boxes)
                                var overlapMin = new XYZ(
                                    Math.Max(bbA.Min.X, bbB.Min.X),
                                    Math.Max(bbA.Min.Y, bbB.Min.Y),
                                    Math.Max(bbA.Min.Z, bbB.Min.Z));
                                var overlapMax = new XYZ(
                                    Math.Min(bbA.Max.X, bbB.Max.X),
                                    Math.Min(bbA.Max.Y, bbB.Max.Y),
                                    Math.Min(bbA.Max.Z, bbB.Max.Z));
                                var overlapCenter = (overlapMin + overlapMax) / 2.0;
                                clash["clashPoint"] = new { x = overlapCenter.X, y = overlapCenter.Y, z = overlapCenter.Z };
                            }

                            clashes.Add(clash);
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .WithCount(clashes.Count, "clashCount")
                    .WithList("clashes", clashes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 2. checkCategoryInterference
        // ──────────────────────────────────────────────

        /// <summary>
        /// Check interference between two entire categories
        /// </summary>
        [MCPMethod("checkCategoryInterference", Category = "Coordination", Description = "Check interference between two element categories")]
        public static string CheckCategoryInterference(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "checkCategoryInterference");
                v.Require("categoryA").IsType<int>();
                v.Require("categoryB").IsType<int>();
                v.ThrowIfInvalid();

                var catIdA = v.GetRequired<int>("categoryA");
                var catIdB = v.GetRequired<int>("categoryB");
                var maxResults = v.GetOptional<int>("maxResults", 100);

                var bicA = (BuiltInCategory)catIdA;
                var bicB = (BuiltInCategory)catIdB;

                var elementsA = new FilteredElementCollector(doc)
                    .OfCategory(bicA)
                    .WhereElementIsNotElementType()
                    .ToList();

                var elementsB = new FilteredElementCollector(doc)
                    .OfCategory(bicB)
                    .WhereElementIsNotElementType()
                    .ToList();

                var clashes = new List<object>();

                foreach (var elemA in elementsA)
                {
                    if (clashes.Count >= maxResults) break;

                    var filter = new ElementIntersectsElementFilter(elemA);

                    foreach (var elemB in elementsB)
                    {
                        if (clashes.Count >= maxResults) break;
                        if (elemA.Id == elemB.Id) continue;

                        if (filter.PassesFilter(elemB))
                        {
                            var bbA = elemA.get_BoundingBox(null);
                            var bbB = elemB.get_BoundingBox(null);

                            var clash = new Dictionary<string, object>
                            {
                                ["elementIdA"] = (int)elemA.Id.Value,
                                ["elementNameA"] = elemA.Name,
                                ["elementIdB"] = (int)elemB.Id.Value,
                                ["elementNameB"] = elemB.Name
                            };

                            if (bbA != null && bbB != null)
                            {
                                var overlapMin = new XYZ(
                                    Math.Max(bbA.Min.X, bbB.Min.X),
                                    Math.Max(bbA.Min.Y, bbB.Min.Y),
                                    Math.Max(bbA.Min.Z, bbB.Min.Z));
                                var overlapMax = new XYZ(
                                    Math.Min(bbA.Max.X, bbB.Max.X),
                                    Math.Min(bbA.Max.Y, bbB.Max.Y),
                                    Math.Min(bbA.Max.Z, bbB.Max.Z));
                                var overlapCenter = (overlapMin + overlapMax) / 2.0;
                                clash["clashPoint"] = new { x = overlapCenter.X, y = overlapCenter.Y, z = overlapCenter.Z };
                            }

                            clashes.Add(clash);
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("categoryA", bicA.ToString())
                    .With("categoryB", bicB.ToString())
                    .With("elementsCheckedA", elementsA.Count)
                    .With("elementsCheckedB", elementsB.Count)
                    .WithCount(clashes.Count, "clashCount")
                    .WithList("clashes", clashes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 3. getElementClearance
        // ──────────────────────────────────────────────

        /// <summary>
        /// Get clearance distance between two elements
        /// </summary>
        [MCPMethod("getElementClearance", Category = "Coordination", Description = "Get minimum clearance distance between two elements")]
        public static string GetElementClearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getElementClearance");
                v.Require("elementIdA").IsType<int>();
                v.Require("elementIdB").IsType<int>();
                v.ThrowIfInvalid();

                var idA = v.GetElementId("elementIdA");
                var idB = v.GetElementId("elementIdB");

                var elemA = doc.GetElement(new ElementId(idA));
                var elemB = doc.GetElement(new ElementId(idB));

                if (elemA == null)
                    return ResponseBuilder.Error($"Element {idA} not found", "ELEMENT_NOT_FOUND").Build();
                if (elemB == null)
                    return ResponseBuilder.Error($"Element {idB} not found", "ELEMENT_NOT_FOUND").Build();

                // Try geometry-based distance first
                var opts = new Options { ComputeReferences = true };
                var geomA = elemA.get_Geometry(opts);
                var geomB = elemB.get_Geometry(opts);

                double minDistance = double.MaxValue;
                XYZ closestPointA = null;
                XYZ closestPointB = null;

                // Extract solids from both elements
                var solidsA = GetSolids(geomA);
                var solidsB = GetSolids(geomB);

                if (solidsA.Count > 0 && solidsB.Count > 0)
                {
                    foreach (var solidA in solidsA)
                    {
                        foreach (var faceA in solidA.Faces.Cast<Face>())
                        {
                            foreach (var solidB in solidsB)
                            {
                                foreach (var faceB in solidB.Faces.Cast<Face>())
                                {
                                    // Sample points on face B and project to face A
                                    var mesh = faceB.Triangulate();
                                    if (mesh == null) continue;

                                    for (int i = 0; i < mesh.NumTriangles; i++)
                                    {
                                        var tri = mesh.get_Triangle(i);
                                        for (int j = 0; j < 3; j++)
                                        {
                                            var ptB = tri.get_Vertex(j);
                                            var result = faceA.Project(ptB);
                                            if (result != null && result.Distance < minDistance)
                                            {
                                                minDistance = result.Distance;
                                                closestPointA = result.XYZPoint;
                                                closestPointB = ptB;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback to bounding box distance
                if (minDistance == double.MaxValue)
                {
                    var bbA = elemA.get_BoundingBox(null);
                    var bbB = elemB.get_BoundingBox(null);

                    if (bbA != null && bbB != null)
                    {
                        minDistance = BoundingBoxDistance(bbA, bbB, out closestPointA, out closestPointB);
                    }
                    else
                    {
                        return ResponseBuilder.Error("Cannot compute clearance - elements have no geometry or bounding box", "NO_GEOMETRY").Build();
                    }
                }

                var response = ResponseBuilder.Success()
                    .With("elementIdA", idA)
                    .With("elementNameA", elemA.Name)
                    .With("elementIdB", idB)
                    .With("elementNameB", elemB.Name)
                    .With("minDistance", minDistance)
                    .With("minDistanceInches", minDistance * 12.0)
                    .With("intersects", minDistance <= 0);

                if (closestPointA != null)
                    response.With("closestPointA", new { x = closestPointA.X, y = closestPointA.Y, z = closestPointA.Z });
                if (closestPointB != null)
                    response.With("closestPointB", new { x = closestPointB.X, y = closestPointB.Y, z = closestPointB.Z });

                return response.Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 4. checkMEPClearances
        // ──────────────────────────────────────────────

        /// <summary>
        /// Check all MEP elements against structural elements for minimum clearance violations
        /// </summary>
        [MCPMethod("checkMEPClearances", Category = "Coordination", Description = "Check MEP elements against structural for clearance violations")]
        public static string CheckMEPClearances(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "checkMEPClearances");
                v.Optional("minClearance").IsPositive();
                v.ThrowIfInvalid();

                var minClearance = v.GetOptional<double>("minClearance", 0.25); // Default 3 inches in feet
                var maxResults = v.GetOptional<int>("maxResults", 200);

                // Collect MEP elements
                var mepCategories = new[]
                {
                    BuiltInCategory.OST_DuctCurves,
                    BuiltInCategory.OST_PipeCurves,
                    BuiltInCategory.OST_Conduit,
                    BuiltInCategory.OST_CableTray,
                    BuiltInCategory.OST_DuctFitting,
                    BuiltInCategory.OST_PipeFitting
                };

                var mepElements = new List<Element>();
                foreach (var cat in mepCategories)
                {
                    try
                    {
                        var elems = new FilteredElementCollector(doc)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType()
                            .ToList();
                        mepElements.AddRange(elems);
                    }
                    catch { }
                }

                // Collect structural elements
                var structCategories = new[]
                {
                    BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_StructuralColumns,
                    BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_StructuralFoundation
                };

                var structElements = new List<Element>();
                foreach (var cat in structCategories)
                {
                    try
                    {
                        var elems = new FilteredElementCollector(doc)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType()
                            .ToList();
                        structElements.AddRange(elems);
                    }
                    catch { }
                }

                var violations = new List<object>();

                foreach (var mepElem in mepElements)
                {
                    if (violations.Count >= maxResults) break;

                    var bbMep = mepElem.get_BoundingBox(null);
                    if (bbMep == null) continue;

                    // Expand bounding box by clearance for quick filter
                    var expandedMin = bbMep.Min - new XYZ(minClearance, minClearance, minClearance);
                    var expandedMax = bbMep.Max + new XYZ(minClearance, minClearance, minClearance);
                    var outline = new Outline(expandedMin, expandedMax);

                    foreach (var structElem in structElements)
                    {
                        if (violations.Count >= maxResults) break;

                        var bbStruct = structElem.get_BoundingBox(null);
                        if (bbStruct == null) continue;

                        var structOutline = new Outline(bbStruct.Min, bbStruct.Max);

                        // Quick check: do expanded MEP and structural bounding boxes overlap?
                        if (!outline.Intersects(structOutline, 0)) continue;

                        // Compute actual distance
                        XYZ ptA, ptB;
                        var distance = BoundingBoxDistance(bbMep, bbStruct, out ptA, out ptB);

                        // Check if intersecting or below clearance
                        bool intersects = false;
                        try
                        {
                            var intersectFilter = new ElementIntersectsElementFilter(mepElem);
                            intersects = intersectFilter.PassesFilter(structElem);
                        }
                        catch { }

                        if (intersects || distance < minClearance)
                        {
                            violations.Add(new
                            {
                                mepElementId = (int)mepElem.Id.Value,
                                mepName = mepElem.Name,
                                mepCategory = mepElem.Category?.Name ?? "Unknown",
                                structElementId = (int)structElem.Id.Value,
                                structName = structElem.Name,
                                structCategory = structElem.Category?.Name ?? "Unknown",
                                clearance = intersects ? 0.0 : distance,
                                clearanceInches = intersects ? 0.0 : distance * 12.0,
                                requiredClearance = minClearance,
                                severity = intersects ? "CLASH" : "CLEARANCE_VIOLATION",
                                location = ptA != null ? new { x = ptA.X, y = ptA.Y, z = ptA.Z } : null
                            });
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("mepElementCount", mepElements.Count)
                    .With("structElementCount", structElements.Count)
                    .With("minClearanceRequired", minClearance)
                    .WithCount(violations.Count, "violationCount")
                    .WithList("violations", violations)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 5. getElementBoundingBox
        // ──────────────────────────────────────────────

        /// <summary>
        /// Get bounding box for an element
        /// </summary>
        [MCPMethod("getElementBoundingBox", Category = "Coordination", Description = "Get bounding box dimensions and location for an element")]
        public static string GetElementBoundingBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getElementBoundingBox");
                v.Require("elementId").IsType<int>();
                v.ThrowIfInvalid();

                var elemId = v.GetElementId("elementId");
                var elem = doc.GetElement(new ElementId(elemId));

                if (elem == null)
                    return ResponseBuilder.Error($"Element {elemId} not found", "ELEMENT_NOT_FOUND").Build();

                var bb = elem.get_BoundingBox(null);
                if (bb == null)
                    return ResponseBuilder.Error($"Element {elemId} has no bounding box", "NO_BOUNDING_BOX").Build();

                var center = (bb.Min + bb.Max) / 2.0;
                var dimensions = bb.Max - bb.Min;

                return ResponseBuilder.Success()
                    .WithElementId(elemId)
                    .With("elementName", elem.Name)
                    .With("category", elem.Category?.Name ?? "Unknown")
                    .With("min", new { x = bb.Min.X, y = bb.Min.Y, z = bb.Min.Z })
                    .With("max", new { x = bb.Max.X, y = bb.Max.Y, z = bb.Max.Z })
                    .With("center", new { x = center.X, y = center.Y, z = center.Z })
                    .With("width", dimensions.X)
                    .With("depth", dimensions.Y)
                    .With("height", dimensions.Z)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 6. getElementsInBoundingBox
        // ──────────────────────────────────────────────

        /// <summary>
        /// Find all elements within a bounding box region
        /// </summary>
        [MCPMethod("getElementsInBoundingBox", Category = "Coordination", Description = "Find all elements within a specified bounding box region")]
        public static string GetElementsInBoundingBox(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getElementsInBoundingBox");
                v.Require("minX").IsType<double>();
                v.Require("minY").IsType<double>();
                v.Require("minZ").IsType<double>();
                v.Require("maxX").IsType<double>();
                v.Require("maxY").IsType<double>();
                v.Require("maxZ").IsType<double>();
                v.ThrowIfInvalid();

                var minX = v.GetRequired<double>("minX");
                var minY = v.GetRequired<double>("minY");
                var minZ = v.GetRequired<double>("minZ");
                var maxX = v.GetRequired<double>("maxX");
                var maxY = v.GetRequired<double>("maxY");
                var maxZ = v.GetRequired<double>("maxZ");
                var maxResults = v.GetOptional<int>("maxResults", 500);

                var min = new XYZ(minX, minY, minZ);
                var max = new XYZ(maxX, maxY, maxZ);
                var outline = new Outline(min, max);

                var bbFilter = new BoundingBoxIntersectsFilter(outline);
                var collector = new FilteredElementCollector(doc)
                    .WherePasses(bbFilter)
                    .WhereElementIsNotElementType();

                // Optional category filter
                var categoriesToken = parameters["categories"];
                List<int> categoryIds = null;
                if (categoriesToken != null)
                {
                    categoryIds = categoriesToken.ToObject<int[]>()?.ToList();
                }

                var elements = new List<object>();
                foreach (var elem in collector)
                {
                    if (elements.Count >= maxResults) break;

                    // Filter by categories if specified
                    if (categoryIds != null && categoryIds.Count > 0)
                    {
                        if (elem.Category == null || !categoryIds.Contains((int)elem.Category.Id.Value))
                            continue;
                    }

                    var bb = elem.get_BoundingBox(null);
                    var center = bb != null ? (bb.Min + bb.Max) / 2.0 : null;

                    elements.Add(new
                    {
                        elementId = (int)elem.Id.Value,
                        name = elem.Name,
                        category = elem.Category?.Name ?? "Unknown",
                        categoryId = elem.Category != null ? (int)elem.Category.Id.Value : 0,
                        location = center != null ? new { x = center.X, y = center.Y, z = center.Z } : null
                    });
                }

                return ResponseBuilder.Success()
                    .With("boundingBox", new { min = new { x = minX, y = minY, z = minZ }, max = new { x = maxX, y = maxY, z = maxZ } })
                    .WithCount(elements.Count)
                    .WithList("elements", elements)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 7. checkHeadroom
        // ──────────────────────────────────────────────

        /// <summary>
        /// Check headroom clearance for rooms/spaces
        /// </summary>
        [MCPMethod("checkHeadroom", Category = "Coordination", Description = "Check headroom clearance for rooms against minimum required height")]
        public static string CheckHeadroom(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "checkHeadroom");
                v.Optional("minClearance").IsPositive();
                v.ThrowIfInvalid();

                var minClearance = v.GetOptional<double>("minClearance", 7.5); // Default 7'-6" in feet
                var roomIdOpt = v.GetOptionalElementId("roomId");

                List<Room> rooms;
                if (roomIdOpt.HasValue)
                {
                    var room = doc.GetElement(new ElementId(roomIdOpt.Value)) as Room;
                    if (room == null)
                        return ResponseBuilder.Error($"Room {roomIdOpt.Value} not found", "ELEMENT_NOT_FOUND").Build();
                    rooms = new List<Room> { room };
                }
                else
                {
                    rooms = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.Area > 0)
                        .ToList();
                }

                var results = new List<object>();

                foreach (var room in rooms)
                {
                    // Get room height parameters
                    var upperLimit = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL)?.AsElementId();
                    var upperOffset = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET)?.AsDouble() ?? 0;
                    var lowerOffset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0;

                    // Get unbounded height from bounding box
                    var bb = room.get_BoundingBox(null);
                    double roomHeight = 0;
                    if (bb != null)
                    {
                        roomHeight = bb.Max.Z - bb.Min.Z;
                    }

                    // Check ceiling height if ceiling exists in the room
                    double? ceilingHeight = null;
                    var roomBB = room.get_BoundingBox(null);
                    if (roomBB != null)
                    {
                        var roomOutline = new Outline(roomBB.Min, roomBB.Max);
                        var ceilingFilter = new BoundingBoxIntersectsFilter(roomOutline);

                        var ceilings = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Ceilings)
                            .WhereElementIsNotElementType()
                            .WherePasses(ceilingFilter)
                            .ToList();

                        if (ceilings.Count > 0)
                        {
                            // Use lowest ceiling as the effective headroom
                            foreach (var ceiling in ceilings)
                            {
                                var cBB = ceiling.get_BoundingBox(null);
                                if (cBB != null)
                                {
                                    var cBottom = cBB.Min.Z;
                                    var floorZ = roomBB.Min.Z;
                                    var ch = cBottom - floorZ;
                                    if (!ceilingHeight.HasValue || ch < ceilingHeight.Value)
                                    {
                                        ceilingHeight = ch;
                                    }
                                }
                            }
                        }
                    }

                    var effectiveHeight = ceilingHeight ?? roomHeight;
                    var passes = effectiveHeight >= minClearance;

                    results.Add(new
                    {
                        roomId = (int)room.Id.Value,
                        roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                        roomNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "",
                        roomHeight = roomHeight,
                        ceilingHeight = ceilingHeight,
                        effectiveHeadroom = effectiveHeight,
                        requiredClearance = minClearance,
                        passes = passes,
                        deficiency = passes ? 0 : minClearance - effectiveHeight
                    });
                }

                var violations = results.Cast<dynamic>().Count(r => !r.passes);

                return ResponseBuilder.Success()
                    .With("minClearanceRequired", minClearance)
                    .With("roomsChecked", results.Count)
                    .With("violations", violations)
                    .WithList("rooms", results)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 8. findOverlappingElements
        // ──────────────────────────────────────────────

        /// <summary>
        /// Find elements that geometrically overlap within a category
        /// </summary>
        [MCPMethod("findOverlappingElements", Category = "Coordination", Description = "Find elements that overlap within a category (duplicates, overlapping walls, etc.)")]
        public static string FindOverlappingElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "findOverlappingElements");
                v.Require("categoryId").IsType<int>();
                v.ThrowIfInvalid();

                var catId = v.GetRequired<int>("categoryId");
                var tolerance = v.GetOptional<double>("tolerance", 0.01); // ~1/8 inch
                var maxResults = v.GetOptional<int>("maxResults", 100);

                var bic = (BuiltInCategory)catId;

                var elements = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToList();

                var overlaps = new List<object>();
                var checkedPairs = new HashSet<string>();

                for (int i = 0; i < elements.Count && overlaps.Count < maxResults; i++)
                {
                    var elemA = elements[i];
                    ElementIntersectsElementFilter filter;
                    try
                    {
                        filter = new ElementIntersectsElementFilter(elemA);
                    }
                    catch { continue; }

                    for (int j = i + 1; j < elements.Count && overlaps.Count < maxResults; j++)
                    {
                        var elemB = elements[j];
                        var pairKey = $"{elemA.Id.Value}_{elemB.Id.Value}";
                        if (checkedPairs.Contains(pairKey)) continue;
                        checkedPairs.Add(pairKey);

                        try
                        {
                            if (filter.PassesFilter(elemB))
                            {
                                var bbA = elemA.get_BoundingBox(null);
                                var bbB = elemB.get_BoundingBox(null);

                                overlaps.Add(new
                                {
                                    elementIdA = (int)elemA.Id.Value,
                                    nameA = elemA.Name,
                                    elementIdB = (int)elemB.Id.Value,
                                    nameB = elemB.Name,
                                    locationA = bbA != null ? new { x = ((bbA.Min + bbA.Max) / 2.0).X, y = ((bbA.Min + bbA.Max) / 2.0).Y, z = ((bbA.Min + bbA.Max) / 2.0).Z } : null,
                                    locationB = bbB != null ? new { x = ((bbB.Min + bbB.Max) / 2.0).X, y = ((bbB.Min + bbB.Max) / 2.0).Y, z = ((bbB.Min + bbB.Max) / 2.0).Z } : null,
                                    sameType = elemA.GetTypeId() == elemB.GetTypeId()
                                });
                            }
                        }
                        catch { }
                    }
                }

                return ResponseBuilder.Success()
                    .With("category", bic.ToString())
                    .With("elementsChecked", elements.Count)
                    .WithCount(overlaps.Count, "overlapCount")
                    .WithList("overlaps", overlaps)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 9. createCoordinationView
        // ──────────────────────────────────────────────

        /// <summary>
        /// Create a 3D view filtered to show specific discipline clash areas
        /// </summary>
        [MCPMethod("createCoordinationView", Category = "Coordination", Description = "Create a 3D coordination view showing specific categories")]
        public static string CreateCoordinationView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "createCoordinationView");
                v.Require("name").NotEmpty();
                v.Require("categoriesA");
                v.Require("categoriesB");
                v.ThrowIfInvalid();

                var viewName = v.GetRequired<string>("name");
                var catIdsA = parameters["categoriesA"].ToObject<int[]>();
                var catIdsB = parameters["categoriesB"].ToObject<int[]>();
                var viewTemplateId = v.GetOptionalElementId("viewTemplateId");

                // Find the 3D view family type
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

                if (viewFamilyType == null)
                    return ResponseBuilder.Error("No 3D view family type found", "NO_VIEW_TYPE").Build();

                using (var trans = new Transaction(doc, "Create Coordination View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var view3d = View3D.CreateIsometric(doc, viewFamilyType.Id);
                    view3d.Name = viewName;
                    view3d.DetailLevel = ViewDetailLevel.Fine;

                    // Apply view template if specified
                    if (viewTemplateId.HasValue)
                    {
                        var template = doc.GetElement(new ElementId(viewTemplateId.Value)) as View;
                        if (template != null)
                        {
                            view3d.ViewTemplateId = template.Id;
                        }
                    }

                    // Combine all desired category IDs
                    var visibleCategoryIds = new HashSet<int>();
                    if (catIdsA != null) foreach (var id in catIdsA) visibleCategoryIds.Add(id);
                    if (catIdsB != null) foreach (var id in catIdsB) visibleCategoryIds.Add(id);

                    // Hide all categories except the specified ones
                    var categories = doc.Settings.Categories;
                    foreach (Category cat in categories)
                    {
                        try
                        {
                            if (!visibleCategoryIds.Contains((int)cat.Id.Value))
                            {
                                if (cat.get_AllowsVisibilityControl(view3d))
                                {
                                    view3d.SetCategoryHidden(cat.Id, true);
                                }
                            }
                        }
                        catch { }
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .WithView((int)view3d.Id.Value, viewName, "ThreeD")
                        .With("categoriesVisible", visibleCategoryIds.Count)
                        .WithMessage($"Coordination view '{viewName}' created")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 10. getModelWarnings
        // ──────────────────────────────────────────────

        /// <summary>
        /// Get all active Revit warnings/errors in the model
        /// </summary>
        [MCPMethod("getModelWarnings", Category = "Coordination", Description = "Get all active warnings and errors in the Revit model")]
        public static string GetModelWarnings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var maxResults = 500;
                if (parameters != null && parameters["maxResults"] != null)
                    maxResults = parameters["maxResults"].Value<int>();

                var warnings = doc.GetWarnings();
                var warningList = new List<object>();

                foreach (var warning in warnings)
                {
                    if (warningList.Count >= maxResults) break;

                    var failingElements = warning.GetFailingElements()?.Select(id => (int)id.Value).ToList() ?? new List<int>();
                    var additionalElements = warning.GetAdditionalElements()?.Select(id => (int)id.Value).ToList() ?? new List<int>();

                    var severity = warning.GetSeverity();

                    warningList.Add(new
                    {
                        description = warning.GetDescriptionText(),
                        severity = severity.ToString(),
                        hasResolution = warning.HasResolutions(),
                        failingElementIds = failingElements,
                        additionalElementIds = additionalElements,
                        elementCount = failingElements.Count + additionalElements.Count
                    });
                }

                // Group by description for summary
                var grouped = warningList
                    .Cast<dynamic>()
                    .GroupBy(w => (string)w.description)
                    .Select(g => new { description = g.Key, count = g.Count() })
                    .OrderByDescending(g => g.count)
                    .ToList();

                return ResponseBuilder.Success()
                    .WithCount(warningList.Count, "totalWarnings")
                    .With("uniqueWarningTypes", grouped.Count)
                    .WithList("warnings", warningList)
                    .WithList("summary", grouped)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 11. resolveWarning
        // ──────────────────────────────────────────────

        /// <summary>
        /// Attempt to resolve a specific warning by deleting one of the involved elements
        /// </summary>
        [MCPMethod("resolveWarning", Category = "Coordination", Description = "Attempt to resolve a model warning using a specified strategy")]
        public static string ResolveWarning(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "resolveWarning");
                v.Require("warningIndex").IsType<int>();
                v.Require("strategy").NotEmpty();
                v.ThrowIfInvalid();

                var warningIndex = v.GetRequired<int>("warningIndex");
                var strategy = v.GetRequired<string>("strategy").ToLowerInvariant();

                var warnings = doc.GetWarnings();
                if (warningIndex < 0 || warningIndex >= warnings.Count)
                    return ResponseBuilder.Error($"Warning index {warningIndex} out of range (0-{warnings.Count - 1})", "INDEX_OUT_OF_RANGE").Build();

                var warning = warnings[warningIndex];
                var failingIds = warning.GetFailingElements()?.ToList() ?? new List<ElementId>();

                if (failingIds.Count == 0)
                    return ResponseBuilder.Error("Warning has no failing elements to resolve", "NO_FAILING_ELEMENTS").Build();

                switch (strategy)
                {
                    case "delete_first":
                    {
                        using (var trans = new Transaction(doc, "Resolve Warning - Delete First"))
                        {
                            trans.Start();
                            var failureOptions = trans.GetFailureHandlingOptions();
                            failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                            trans.SetFailureHandlingOptions(failureOptions);

                            var elemToDelete = failingIds[0];
                            var deletedName = doc.GetElement(elemToDelete)?.Name ?? "Unknown";
                            doc.Delete(elemToDelete);

                            trans.Commit();

                            return ResponseBuilder.Success()
                                .With("strategy", strategy)
                                .With("deletedElementId", (int)elemToDelete.Value)
                                .With("deletedElementName", deletedName)
                                .With("warningDescription", warning.GetDescriptionText())
                                .WithMessage("Warning resolved by deleting first failing element")
                                .Build();
                        }
                    }

                    case "delete_last":
                    {
                        using (var trans = new Transaction(doc, "Resolve Warning - Delete Last"))
                        {
                            trans.Start();
                            var failureOptions = trans.GetFailureHandlingOptions();
                            failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                            trans.SetFailureHandlingOptions(failureOptions);

                            var elemToDelete = failingIds[failingIds.Count - 1];
                            var deletedName = doc.GetElement(elemToDelete)?.Name ?? "Unknown";
                            doc.Delete(elemToDelete);

                            trans.Commit();

                            return ResponseBuilder.Success()
                                .With("strategy", strategy)
                                .With("deletedElementId", (int)elemToDelete.Value)
                                .With("deletedElementName", deletedName)
                                .With("warningDescription", warning.GetDescriptionText())
                                .WithMessage("Warning resolved by deleting last failing element")
                                .Build();
                        }
                    }

                    case "info":
                    {
                        // Just return info about the warning without resolving
                        var elemInfos = failingIds.Select(id =>
                        {
                            var elem = doc.GetElement(id);
                            return new
                            {
                                elementId = (int)id.Value,
                                name = elem?.Name ?? "Unknown",
                                category = elem?.Category?.Name ?? "Unknown"
                            };
                        }).ToList();

                        return ResponseBuilder.Success()
                            .With("warningDescription", warning.GetDescriptionText())
                            .With("severity", warning.GetSeverity().ToString())
                            .With("hasResolutions", warning.HasResolutions())
                            .WithList("failingElements", elemInfos)
                            .WithMessage("Warning info retrieved. Use strategy 'delete_first' or 'delete_last' to resolve.")
                            .Build();
                    }

                    default:
                        return ResponseBuilder.Error($"Unknown strategy '{strategy}'. Use: delete_first, delete_last, info", "INVALID_STRATEGY").Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 12. getElementSpatialRelation
        // ──────────────────────────────────────────────

        /// <summary>
        /// Determine spatial relationship between two elements
        /// </summary>
        [MCPMethod("getElementSpatialRelation", Category = "Coordination", Description = "Determine spatial relationship between two elements (above/below/adjacent/intersecting)")]
        public static string GetElementSpatialRelation(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getElementSpatialRelation");
                v.Require("elementIdA").IsType<int>();
                v.Require("elementIdB").IsType<int>();
                v.ThrowIfInvalid();

                var idA = v.GetElementId("elementIdA");
                var idB = v.GetElementId("elementIdB");

                var elemA = doc.GetElement(new ElementId(idA));
                var elemB = doc.GetElement(new ElementId(idB));

                if (elemA == null)
                    return ResponseBuilder.Error($"Element {idA} not found", "ELEMENT_NOT_FOUND").Build();
                if (elemB == null)
                    return ResponseBuilder.Error($"Element {idB} not found", "ELEMENT_NOT_FOUND").Build();

                var bbA = elemA.get_BoundingBox(null);
                var bbB = elemB.get_BoundingBox(null);

                if (bbA == null || bbB == null)
                    return ResponseBuilder.Error("One or both elements have no bounding box", "NO_BOUNDING_BOX").Build();

                // Check intersection
                bool intersects = false;
                try
                {
                    var filter = new ElementIntersectsElementFilter(elemA);
                    intersects = filter.PassesFilter(elemB);
                }
                catch { }

                // Determine vertical relationship
                var centerA = (bbA.Min + bbA.Max) / 2.0;
                var centerB = (bbB.Min + bbB.Max) / 2.0;

                string verticalRelation;
                if (bbA.Min.Z >= bbB.Max.Z)
                    verticalRelation = "above";
                else if (bbA.Max.Z <= bbB.Min.Z)
                    verticalRelation = "below";
                else
                    verticalRelation = "same_level";

                // Determine horizontal relationship
                string horizontalRelation;
                XYZ ptA, ptB;
                var hDistance = BoundingBoxDistance2D(bbA, bbB);

                if (intersects)
                    horizontalRelation = "intersecting";
                else if (hDistance < 0.5) // Within 6 inches
                    horizontalRelation = "adjacent";
                else if (hDistance < 5.0) // Within 5 feet
                    horizontalRelation = "nearby";
                else
                    horizontalRelation = "separated";

                // Compute overall relation
                string relation;
                if (intersects)
                    relation = "intersecting";
                else if (verticalRelation == "above" || verticalRelation == "below")
                    relation = verticalRelation;
                else if (horizontalRelation == "adjacent")
                    relation = "adjacent";
                else
                    relation = "separated";

                var distance = BoundingBoxDistance(bbA, bbB, out ptA, out ptB);

                return ResponseBuilder.Success()
                    .With("elementIdA", idA)
                    .With("elementNameA", elemA.Name)
                    .With("elementIdB", idB)
                    .With("elementNameB", elemB.Name)
                    .With("relation", relation)
                    .With("verticalRelation", verticalRelation)
                    .With("horizontalRelation", horizontalRelation)
                    .With("intersects", intersects)
                    .With("distance", distance)
                    .With("distanceInches", distance * 12.0)
                    .With("verticalGap", Math.Max(0, Math.Max(bbA.Min.Z - bbB.Max.Z, bbB.Min.Z - bbA.Max.Z)))
                    .With("centerA", new { x = centerA.X, y = centerA.Y, z = centerA.Z })
                    .With("centerB", new { x = centerB.X, y = centerB.Y, z = centerB.Z })
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 13. exportClashReport
        // ──────────────────────────────────────────────

        /// <summary>
        /// Generate a clash report for specified categories
        /// </summary>
        [MCPMethod("exportClashReport", Category = "Coordination", Description = "Generate a clash report between two sets of categories")]
        public static string ExportClashReport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "exportClashReport");
                v.Require("categoriesA");
                v.Require("categoriesB");
                v.Require("outputPath").NotEmpty();
                v.ThrowIfInvalid();

                var catIdsA = parameters["categoriesA"].ToObject<int[]>();
                var catIdsB = parameters["categoriesB"].ToObject<int[]>();
                var outputPath = v.GetRequired<string>("outputPath");
                var format = v.GetOptional<string>("format", "json").ToLowerInvariant();

                // Collect elements from category sets
                var elementsA = new List<Element>();
                foreach (var catId in catIdsA)
                {
                    try
                    {
                        var elems = new FilteredElementCollector(doc)
                            .OfCategory((BuiltInCategory)catId)
                            .WhereElementIsNotElementType()
                            .ToList();
                        elementsA.AddRange(elems);
                    }
                    catch { }
                }

                var elementsB = new List<Element>();
                foreach (var catId in catIdsB)
                {
                    try
                    {
                        var elems = new FilteredElementCollector(doc)
                            .OfCategory((BuiltInCategory)catId)
                            .WhereElementIsNotElementType()
                            .ToList();
                        elementsB.AddRange(elems);
                    }
                    catch { }
                }

                var clashes = new List<Dictionary<string, object>>();
                int clashIndex = 0;

                foreach (var elemA in elementsA)
                {
                    ElementIntersectsElementFilter filter;
                    try
                    {
                        filter = new ElementIntersectsElementFilter(elemA);
                    }
                    catch { continue; }

                    foreach (var elemB in elementsB)
                    {
                        if (elemA.Id == elemB.Id) continue;

                        try
                        {
                            if (filter.PassesFilter(elemB))
                            {
                                clashIndex++;
                                var bbA = elemA.get_BoundingBox(null);
                                var bbB = elemB.get_BoundingBox(null);

                                var clash = new Dictionary<string, object>
                                {
                                    ["clashId"] = clashIndex,
                                    ["elementIdA"] = (int)elemA.Id.Value,
                                    ["elementNameA"] = elemA.Name,
                                    ["categoryA"] = elemA.Category?.Name ?? "Unknown",
                                    ["elementIdB"] = (int)elemB.Id.Value,
                                    ["elementNameB"] = elemB.Name,
                                    ["categoryB"] = elemB.Category?.Name ?? "Unknown"
                                };

                                if (bbA != null && bbB != null)
                                {
                                    var overlapCenter = new XYZ(
                                        (Math.Max(bbA.Min.X, bbB.Min.X) + Math.Min(bbA.Max.X, bbB.Max.X)) / 2.0,
                                        (Math.Max(bbA.Min.Y, bbB.Min.Y) + Math.Min(bbA.Max.Y, bbB.Max.Y)) / 2.0,
                                        (Math.Max(bbA.Min.Z, bbB.Min.Z) + Math.Min(bbA.Max.Z, bbB.Max.Z)) / 2.0);
                                    clash["clashPointX"] = overlapCenter.X;
                                    clash["clashPointY"] = overlapCenter.Y;
                                    clash["clashPointZ"] = overlapCenter.Z;
                                }

                                clashes.Add(clash);
                            }
                        }
                        catch { }
                    }
                }

                // Write report
                string reportContent;
                if (format == "csv")
                {
                    var lines = new List<string>
                    {
                        "ClashId,ElementIdA,ElementNameA,CategoryA,ElementIdB,ElementNameB,CategoryB,ClashPointX,ClashPointY,ClashPointZ"
                    };

                    foreach (var clash in clashes)
                    {
                        lines.Add(string.Format("{0},{1},\"{2}\",\"{3}\",{4},\"{5}\",\"{6}\",{7},{8},{9}",
                            clash["clashId"],
                            clash["elementIdA"],
                            clash["elementNameA"],
                            clash["categoryA"],
                            clash["elementIdB"],
                            clash["elementNameB"],
                            clash["categoryB"],
                            clash.ContainsKey("clashPointX") ? clash["clashPointX"] : "",
                            clash.ContainsKey("clashPointY") ? clash["clashPointY"] : "",
                            clash.ContainsKey("clashPointZ") ? clash["clashPointZ"] : ""));
                    }

                    reportContent = string.Join("\n", lines);
                }
                else
                {
                    var report = new
                    {
                        projectName = doc.Title,
                        generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        totalClashes = clashes.Count,
                        elementsCheckedA = elementsA.Count,
                        elementsCheckedB = elementsB.Count,
                        clashes = clashes
                    };
                    reportContent = JsonConvert.SerializeObject(report, Formatting.Indented);
                }

                System.IO.File.WriteAllText(outputPath, reportContent);

                return ResponseBuilder.Success()
                    .With("outputPath", outputPath)
                    .With("format", format)
                    .WithCount(clashes.Count, "clashCount")
                    .With("elementsCheckedA", elementsA.Count)
                    .With("elementsCheckedB", elementsB.Count)
                    .WithMessage($"Clash report exported to {outputPath}")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 14. checkDuctClearance
        // ──────────────────────────────────────────────

        /// <summary>
        /// Check duct routing clearances against structure and other trades
        /// </summary>
        [MCPMethod("checkDuctClearance", Category = "Coordination", Description = "Check duct clearances against structural and other MEP elements")]
        public static string CheckDuctClearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "checkDuctClearance");
                v.Optional("tolerance").IsPositive();
                v.ThrowIfInvalid();

                var tolerance = v.GetOptional<double>("tolerance", 0.25); // 3 inches default
                var maxResults = v.GetOptional<int>("maxResults", 200);

                // Collect ducts and duct fittings
                var ducts = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DuctCurves)
                    .WhereElementIsNotElementType()
                    .ToList();

                var ductFittings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DuctFitting)
                    .WhereElementIsNotElementType()
                    .ToList();

                var allDucts = ducts.Concat(ductFittings).ToList();

                // Targets: structure + pipes + conduit + cable tray
                var targetCategories = new[]
                {
                    BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_StructuralColumns,
                    BuiltInCategory.OST_PipeCurves,
                    BuiltInCategory.OST_PipeFitting,
                    BuiltInCategory.OST_Conduit,
                    BuiltInCategory.OST_CableTray
                };

                var targets = new List<Element>();
                foreach (var cat in targetCategories)
                {
                    try
                    {
                        targets.AddRange(new FilteredElementCollector(doc)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType()
                            .ToList());
                    }
                    catch { }
                }

                var violations = new List<object>();

                foreach (var duct in allDucts)
                {
                    if (violations.Count >= maxResults) break;

                    var bbDuct = duct.get_BoundingBox(null);
                    if (bbDuct == null) continue;

                    var expandedMin = bbDuct.Min - new XYZ(tolerance, tolerance, tolerance);
                    var expandedMax = bbDuct.Max + new XYZ(tolerance, tolerance, tolerance);
                    var outline = new Outline(expandedMin, expandedMax);

                    foreach (var target in targets)
                    {
                        if (violations.Count >= maxResults) break;

                        var bbTarget = target.get_BoundingBox(null);
                        if (bbTarget == null) continue;

                        var targetOutline = new Outline(bbTarget.Min, bbTarget.Max);
                        if (!outline.Intersects(targetOutline, 0)) continue;

                        bool intersects = false;
                        try
                        {
                            intersects = new ElementIntersectsElementFilter(duct).PassesFilter(target);
                        }
                        catch { }

                        XYZ ptA, ptB;
                        var distance = BoundingBoxDistance(bbDuct, bbTarget, out ptA, out ptB);

                        if (intersects || distance < tolerance)
                        {
                            violations.Add(new
                            {
                                ductId = (int)duct.Id.Value,
                                ductName = duct.Name,
                                ductCategory = duct.Category?.Name ?? "Unknown",
                                targetId = (int)target.Id.Value,
                                targetName = target.Name,
                                targetCategory = target.Category?.Name ?? "Unknown",
                                clearance = intersects ? 0.0 : distance,
                                clearanceInches = intersects ? 0.0 : distance * 12.0,
                                severity = intersects ? "CLASH" : "CLEARANCE_VIOLATION",
                                location = ptA != null ? new { x = ptA.X, y = ptA.Y, z = ptA.Z } : null
                            });
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("ductsChecked", allDucts.Count)
                    .With("targetsChecked", targets.Count)
                    .With("tolerance", tolerance)
                    .WithCount(violations.Count, "violationCount")
                    .WithList("violations", violations)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 15. checkPipeClearance
        // ──────────────────────────────────────────────

        /// <summary>
        /// Check pipe routing clearances against structure and other trades
        /// </summary>
        [MCPMethod("checkPipeClearance", Category = "Coordination", Description = "Check pipe clearances against structural and other MEP elements")]
        public static string CheckPipeClearance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "checkPipeClearance");
                v.Optional("tolerance").IsPositive();
                v.ThrowIfInvalid();

                var tolerance = v.GetOptional<double>("tolerance", 0.167); // 2 inches default
                var maxResults = v.GetOptional<int>("maxResults", 200);

                // Collect pipes and pipe fittings
                var pipes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeCurves)
                    .WhereElementIsNotElementType()
                    .ToList();

                var pipeFittings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .ToList();

                var allPipes = pipes.Concat(pipeFittings).ToList();

                // Targets: structure + ducts + conduit + cable tray
                var targetCategories = new[]
                {
                    BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_StructuralColumns,
                    BuiltInCategory.OST_DuctCurves,
                    BuiltInCategory.OST_DuctFitting,
                    BuiltInCategory.OST_Conduit,
                    BuiltInCategory.OST_CableTray
                };

                var targets = new List<Element>();
                foreach (var cat in targetCategories)
                {
                    try
                    {
                        targets.AddRange(new FilteredElementCollector(doc)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType()
                            .ToList());
                    }
                    catch { }
                }

                var violations = new List<object>();

                foreach (var pipe in allPipes)
                {
                    if (violations.Count >= maxResults) break;

                    var bbPipe = pipe.get_BoundingBox(null);
                    if (bbPipe == null) continue;

                    var expandedMin = bbPipe.Min - new XYZ(tolerance, tolerance, tolerance);
                    var expandedMax = bbPipe.Max + new XYZ(tolerance, tolerance, tolerance);
                    var outline = new Outline(expandedMin, expandedMax);

                    foreach (var target in targets)
                    {
                        if (violations.Count >= maxResults) break;

                        var bbTarget = target.get_BoundingBox(null);
                        if (bbTarget == null) continue;

                        var targetOutline = new Outline(bbTarget.Min, bbTarget.Max);
                        if (!outline.Intersects(targetOutline, 0)) continue;

                        bool intersects = false;
                        try
                        {
                            intersects = new ElementIntersectsElementFilter(pipe).PassesFilter(target);
                        }
                        catch { }

                        XYZ ptA, ptB;
                        var distance = BoundingBoxDistance(bbPipe, bbTarget, out ptA, out ptB);

                        if (intersects || distance < tolerance)
                        {
                            violations.Add(new
                            {
                                pipeId = (int)pipe.Id.Value,
                                pipeName = pipe.Name,
                                pipeCategory = pipe.Category?.Name ?? "Unknown",
                                targetId = (int)target.Id.Value,
                                targetName = target.Name,
                                targetCategory = target.Category?.Name ?? "Unknown",
                                clearance = intersects ? 0.0 : distance,
                                clearanceInches = intersects ? 0.0 : distance * 12.0,
                                severity = intersects ? "CLASH" : "CLEARANCE_VIOLATION",
                                location = ptA != null ? new { x = ptA.X, y = ptA.Y, z = ptA.Z } : null
                            });
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("pipesChecked", allPipes.Count)
                    .With("targetsChecked", targets.Count)
                    .With("tolerance", tolerance)
                    .WithCount(violations.Count, "violationCount")
                    .WithList("violations", violations)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 16. validateCoordination
        // ──────────────────────────────────────────────

        /// <summary>
        /// Run comprehensive coordination check across all disciplines
        /// </summary>
        [MCPMethod("validateCoordination", Category = "Coordination", Description = "Run comprehensive coordination validation across all disciplines")]
        public static string ValidateCoordination(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var maxResultsPerPair = 50;
                if (parameters != null && parameters["maxResultsPerPair"] != null)
                    maxResultsPerPair = parameters["maxResultsPerPair"].Value<int>();

                // Define discipline pairs to check
                var disciplinePairs = new[]
                {
                    new { nameA = "Ducts", catA = BuiltInCategory.OST_DuctCurves, nameB = "Structural Framing", catB = BuiltInCategory.OST_StructuralFraming },
                    new { nameA = "Pipes", catA = BuiltInCategory.OST_PipeCurves, nameB = "Structural Framing", catB = BuiltInCategory.OST_StructuralFraming },
                    new { nameA = "Ducts", catA = BuiltInCategory.OST_DuctCurves, nameB = "Pipes", catB = BuiltInCategory.OST_PipeCurves },
                    new { nameA = "Ducts", catA = BuiltInCategory.OST_DuctCurves, nameB = "Structural Columns", catB = BuiltInCategory.OST_StructuralColumns },
                    new { nameA = "Pipes", catA = BuiltInCategory.OST_PipeCurves, nameB = "Structural Columns", catB = BuiltInCategory.OST_StructuralColumns },
                    new { nameA = "Conduit", catA = BuiltInCategory.OST_Conduit, nameB = "Structural Framing", catB = BuiltInCategory.OST_StructuralFraming },
                    new { nameA = "Cable Tray", catA = BuiltInCategory.OST_CableTray, nameB = "Structural Framing", catB = BuiltInCategory.OST_StructuralFraming },
                    new { nameA = "Ducts", catA = BuiltInCategory.OST_DuctCurves, nameB = "Conduit", catB = BuiltInCategory.OST_Conduit },
                    new { nameA = "Pipes", catA = BuiltInCategory.OST_PipeCurves, nameB = "Conduit", catB = BuiltInCategory.OST_Conduit }
                };

                var pairResults = new List<object>();
                int totalClashes = 0;

                foreach (var pair in disciplinePairs)
                {
                    List<Element> elemsA, elemsB;
                    try
                    {
                        elemsA = new FilteredElementCollector(doc)
                            .OfCategory(pair.catA)
                            .WhereElementIsNotElementType()
                            .ToList();
                    }
                    catch { continue; }

                    try
                    {
                        elemsB = new FilteredElementCollector(doc)
                            .OfCategory(pair.catB)
                            .WhereElementIsNotElementType()
                            .ToList();
                    }
                    catch { continue; }

                    if (elemsA.Count == 0 || elemsB.Count == 0) continue;

                    int pairClashes = 0;

                    foreach (var elemA in elemsA)
                    {
                        if (pairClashes >= maxResultsPerPair) break;

                        try
                        {
                            var filter = new ElementIntersectsElementFilter(elemA);
                            foreach (var elemB in elemsB)
                            {
                                if (pairClashes >= maxResultsPerPair) break;
                                if (filter.PassesFilter(elemB))
                                {
                                    pairClashes++;
                                }
                            }
                        }
                        catch { }
                    }

                    string severity;
                    if (pairClashes == 0) severity = "CLEAR";
                    else if (pairClashes < 5) severity = "LOW";
                    else if (pairClashes < 20) severity = "MEDIUM";
                    else severity = "HIGH";

                    totalClashes += pairClashes;

                    pairResults.Add(new
                    {
                        disciplineA = pair.nameA,
                        disciplineB = pair.nameB,
                        elementsA = elemsA.Count,
                        elementsB = elemsB.Count,
                        clashCount = pairClashes,
                        severity = severity
                    });
                }

                // Also get warning count
                var warnings = doc.GetWarnings();

                return ResponseBuilder.Success()
                    .With("projectName", doc.Title)
                    .With("totalClashes", totalClashes)
                    .With("totalWarnings", warnings.Count)
                    .With("disciplinePairsChecked", pairResults.Count)
                    .WithList("results", pairResults)
                    .WithMessage(totalClashes == 0
                        ? "No clashes detected across disciplines"
                        : $"{totalClashes} clashes detected across {pairResults.Count} discipline pairs")
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 17. getLinkedModelClashes
        // ──────────────────────────────────────────────

        /// <summary>
        /// Check for clashes between current model and linked Revit models
        /// </summary>
        [MCPMethod("getLinkedModelClashes", Category = "Coordination", Description = "Check clashes between current model and linked Revit models")]
        public static string GetLinkedModelClashes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "getLinkedModelClashes");
                v.Require("categoriesA");
                v.Require("categoriesB");
                v.ThrowIfInvalid();

                var catIdsA = parameters["categoriesA"].ToObject<int[]>();
                var catIdsB = parameters["categoriesB"].ToObject<int[]>();
                var linkedDocName = parameters["linkedDocName"]?.Value<string>();
                var maxResults = v.GetOptional<int>("maxResults", 200);

                // Find linked model instances
                var linkInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                if (linkInstances.Count == 0)
                    return ResponseBuilder.Error("No linked Revit models found in the project", "NO_LINKED_MODELS").Build();

                // Filter by name if specified
                if (!string.IsNullOrEmpty(linkedDocName))
                {
                    linkInstances = linkInstances
                        .Where(li => li.Name.IndexOf(linkedDocName, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();

                    if (linkInstances.Count == 0)
                        return ResponseBuilder.Error($"No linked model matching '{linkedDocName}' found", "LINKED_MODEL_NOT_FOUND").Build();
                }

                // Collect elements from host model (categoriesA)
                var hostElements = new List<Element>();
                foreach (var catId in catIdsA)
                {
                    try
                    {
                        hostElements.AddRange(new FilteredElementCollector(doc)
                            .OfCategory((BuiltInCategory)catId)
                            .WhereElementIsNotElementType()
                            .ToList());
                    }
                    catch { }
                }

                var allClashes = new List<object>();

                foreach (var linkInstance in linkInstances)
                {
                    if (allClashes.Count >= maxResults) break;

                    var linkedDoc = linkInstance.GetLinkDocument();
                    if (linkedDoc == null) continue;

                    var transform = linkInstance.GetTotalTransform();

                    // Collect elements from linked model (categoriesB)
                    var linkedElements = new List<Element>();
                    foreach (var catId in catIdsB)
                    {
                        try
                        {
                            linkedElements.AddRange(new FilteredElementCollector(linkedDoc)
                                .OfCategory((BuiltInCategory)catId)
                                .WhereElementIsNotElementType()
                                .ToList());
                        }
                        catch { }
                    }

                    foreach (var hostElem in hostElements)
                    {
                        if (allClashes.Count >= maxResults) break;

                        var bbHost = hostElem.get_BoundingBox(null);
                        if (bbHost == null) continue;

                        foreach (var linkedElem in linkedElements)
                        {
                            if (allClashes.Count >= maxResults) break;

                            var bbLinked = linkedElem.get_BoundingBox(null);
                            if (bbLinked == null) continue;

                            // Transform linked element bounding box to host coordinates
                            var transformedMin = transform.OfPoint(bbLinked.Min);
                            var transformedMax = transform.OfPoint(bbLinked.Max);

                            // Ensure min/max are correct after transform
                            var actualMin = new XYZ(
                                Math.Min(transformedMin.X, transformedMax.X),
                                Math.Min(transformedMin.Y, transformedMax.Y),
                                Math.Min(transformedMin.Z, transformedMax.Z));
                            var actualMax = new XYZ(
                                Math.Max(transformedMin.X, transformedMax.X),
                                Math.Max(transformedMin.Y, transformedMax.Y),
                                Math.Max(transformedMin.Z, transformedMax.Z));

                            // Check bounding box overlap
                            if (bbHost.Max.X < actualMin.X || bbHost.Min.X > actualMax.X ||
                                bbHost.Max.Y < actualMin.Y || bbHost.Min.Y > actualMax.Y ||
                                bbHost.Max.Z < actualMin.Z || bbHost.Min.Z > actualMax.Z)
                                continue;

                            // Bounding boxes overlap - potential clash
                            var overlapCenter = new XYZ(
                                (Math.Max(bbHost.Min.X, actualMin.X) + Math.Min(bbHost.Max.X, actualMax.X)) / 2.0,
                                (Math.Max(bbHost.Min.Y, actualMin.Y) + Math.Min(bbHost.Max.Y, actualMax.Y)) / 2.0,
                                (Math.Max(bbHost.Min.Z, actualMin.Z) + Math.Min(bbHost.Max.Z, actualMax.Z)) / 2.0);

                            allClashes.Add(new
                            {
                                hostElementId = (int)hostElem.Id.Value,
                                hostElementName = hostElem.Name,
                                hostCategory = hostElem.Category?.Name ?? "Unknown",
                                linkedModelName = linkInstance.Name,
                                linkedElementId = (int)linkedElem.Id.Value,
                                linkedElementName = linkedElem.Name,
                                linkedCategory = linkedElem.Category?.Name ?? "Unknown",
                                clashPoint = new { x = overlapCenter.X, y = overlapCenter.Y, z = overlapCenter.Z }
                            });
                        }
                    }
                }

                return ResponseBuilder.Success()
                    .With("hostElementCount", hostElements.Count)
                    .With("linkedModelsChecked", linkInstances.Count)
                    .WithCount(allClashes.Count, "clashCount")
                    .WithList("clashes", allClashes)
                    .Build();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // 18. highlightClashes
        // ──────────────────────────────────────────────

        /// <summary>
        /// Create a view that highlights clashing elements with color overrides
        /// </summary>
        [MCPMethod("highlightClashes", Category = "Coordination", Description = "Apply color overrides to highlight clashing elements in a view")]
        public static string HighlightClashes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var v = new ParameterValidator(parameters, "highlightClashes");
                v.Require("clashPairs");
                v.Require("viewId").IsType<int>();
                v.ThrowIfInvalid();

                var viewId = v.GetElementId("viewId");
                var view = doc.GetElement(new ElementId(viewId)) as View;

                if (view == null)
                    return ResponseBuilder.Error($"View {viewId} not found", "VIEW_NOT_FOUND").Build();

                var clashPairsToken = parameters["clashPairs"];
                var clashPairs = clashPairsToken.ToObject<int[][]>();

                if (clashPairs == null || clashPairs.Length == 0)
                    return ResponseBuilder.Error("clashPairs must be a non-empty array of [idA, idB] pairs", "INVALID_PARAMETER").Build();

                // Parse colors (default: red for A, blue for B)
                var colorAObj = parameters["colorA"];
                var colorBObj = parameters["colorB"];

                byte rA = 255, gA = 0, bA = 0; // Red default
                byte rB = 0, gB = 0, bB = 255; // Blue default

                if (colorAObj != null)
                {
                    rA = (byte)(colorAObj["r"]?.Value<int>() ?? 255);
                    gA = (byte)(colorAObj["g"]?.Value<int>() ?? 0);
                    bA = (byte)(colorAObj["b"]?.Value<int>() ?? 0);
                }

                if (colorBObj != null)
                {
                    rB = (byte)(colorBObj["r"]?.Value<int>() ?? 0);
                    gB = (byte)(colorBObj["g"]?.Value<int>() ?? 0);
                    bB = (byte)(colorBObj["b"]?.Value<int>() ?? 255);
                }

                using (var trans = new Transaction(doc, "Highlight Clashes"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var overrideA = new OverrideGraphicSettings();
                    overrideA.SetProjectionLineColor(new Color(rA, gA, bA));
                    overrideA.SetSurfaceForegroundPatternColor(new Color(rA, gA, bA));
                    overrideA.SetProjectionLineWeight(5);

                    var overrideB = new OverrideGraphicSettings();
                    overrideB.SetProjectionLineColor(new Color(rB, gB, bB));
                    overrideB.SetSurfaceForegroundPatternColor(new Color(rB, gB, bB));
                    overrideB.SetProjectionLineWeight(5);

                    int highlightedCount = 0;
                    var highlightedIdsA = new HashSet<int>();
                    var highlightedIdsB = new HashSet<int>();

                    foreach (var pair in clashPairs)
                    {
                        if (pair.Length < 2) continue;

                        var elemIdA = new ElementId(pair[0]);
                        var elemIdB = new ElementId(pair[1]);

                        if (doc.GetElement(elemIdA) != null)
                        {
                            view.SetElementOverrides(elemIdA, overrideA);
                            highlightedIdsA.Add(pair[0]);
                        }

                        if (doc.GetElement(elemIdB) != null)
                        {
                            view.SetElementOverrides(elemIdB, overrideB);
                            highlightedIdsB.Add(pair[1]);
                        }

                        highlightedCount++;
                    }

                    trans.Commit();

                    return ResponseBuilder.Success()
                        .WithView(viewId, view.Name, view.ViewType.ToString())
                        .With("clashPairsProcessed", highlightedCount)
                        .With("uniqueElementsA", highlightedIdsA.Count)
                        .With("uniqueElementsB", highlightedIdsB.Count)
                        .With("colorA", new { r = (int)rA, g = (int)gA, b = (int)bA })
                        .With("colorB", new { r = (int)rB, g = (int)gB, b = (int)bB })
                        .WithMessage($"Highlighted {highlightedCount} clash pairs in view '{view.Name}'")
                        .Build();
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        // ──────────────────────────────────────────────
        // Helper Methods
        // ──────────────────────────────────────────────

        /// <summary>
        /// Extract all solids from a geometry element (recursively handles instances)
        /// </summary>
        private static List<Solid> GetSolids(GeometryElement geomElem)
        {
            var solids = new List<Solid>();
            if (geomElem == null) return solids;

            foreach (var geomObj in geomElem)
            {
                if (geomObj is Solid solid && solid.Volume > 0)
                {
                    solids.Add(solid);
                }
                else if (geomObj is GeometryInstance geomInst)
                {
                    solids.AddRange(GetSolids(geomInst.GetInstanceGeometry()));
                }
            }

            return solids;
        }

        /// <summary>
        /// Compute minimum distance between two bounding boxes
        /// </summary>
        private static double BoundingBoxDistance(BoundingBoxXYZ bbA, BoundingBoxXYZ bbB, out XYZ closestA, out XYZ closestB)
        {
            // Clamp center of B to A's box and vice versa for closest points
            var clampedX_A = Math.Max(bbA.Min.X, Math.Min(bbB.Min.X + (bbB.Max.X - bbB.Min.X) / 2.0, bbA.Max.X));
            var clampedY_A = Math.Max(bbA.Min.Y, Math.Min(bbB.Min.Y + (bbB.Max.Y - bbB.Min.Y) / 2.0, bbA.Max.Y));
            var clampedZ_A = Math.Max(bbA.Min.Z, Math.Min(bbB.Min.Z + (bbB.Max.Z - bbB.Min.Z) / 2.0, bbA.Max.Z));

            var clampedX_B = Math.Max(bbB.Min.X, Math.Min(bbA.Min.X + (bbA.Max.X - bbA.Min.X) / 2.0, bbB.Max.X));
            var clampedY_B = Math.Max(bbB.Min.Y, Math.Min(bbA.Min.Y + (bbA.Max.Y - bbA.Min.Y) / 2.0, bbB.Max.Y));
            var clampedZ_B = Math.Max(bbB.Min.Z, Math.Min(bbA.Min.Z + (bbA.Max.Z - bbA.Min.Z) / 2.0, bbB.Max.Z));

            closestA = new XYZ(clampedX_A, clampedY_A, clampedZ_A);
            closestB = new XYZ(clampedX_B, clampedY_B, clampedZ_B);

            // Compute distance between closest faces
            double dx = Math.Max(0, Math.Max(bbA.Min.X - bbB.Max.X, bbB.Min.X - bbA.Max.X));
            double dy = Math.Max(0, Math.Max(bbA.Min.Y - bbB.Max.Y, bbB.Min.Y - bbA.Max.Y));
            double dz = Math.Max(0, Math.Max(bbA.Min.Z - bbB.Max.Z, bbB.Min.Z - bbA.Max.Z));

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Compute 2D (XY plane) distance between two bounding boxes
        /// </summary>
        private static double BoundingBoxDistance2D(BoundingBoxXYZ bbA, BoundingBoxXYZ bbB)
        {
            double dx = Math.Max(0, Math.Max(bbA.Min.X - bbB.Max.X, bbB.Min.X - bbA.Max.X));
            double dy = Math.Max(0, Math.Max(bbA.Min.Y - bbB.Max.Y, bbB.Min.Y - bbA.Max.Y));
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
