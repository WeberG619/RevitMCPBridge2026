using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge2026;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    public static class DimensionMethods
    {
        /// <summary>
        /// Add two dimensions to a room: one horizontal (width), one vertical (height)
        /// Dimensions are placed INSIDE the room using wall face references
        /// </summary>
        public static string AddRoomDimensions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var activeView = doc.ActiveView;

                if (parameters["roomId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "roomId required" });
                }

                var roomId = new ElementId(int.Parse(parameters["roomId"].ToString()));
                var room = doc.GetElement(roomId) as Room;

                if (room == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Room not found" });
                }

                var boundaryOptions = new SpatialElementBoundaryOptions();
                var boundarySegments = room.GetBoundarySegments(boundaryOptions);

                if (boundarySegments == null || boundarySegments.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No boundary segments" });
                }

                // Get the room's bounding box
                var bbox = room.get_BoundingBox(activeView);
                if (bbox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Could not get room bounding box" });
                }

                // Calculate room center for dimension placement
                double centerX = (bbox.Min.X + bbox.Max.X) / 2.0;
                double centerY = (bbox.Min.Y + bbox.Max.Y) / 2.0;
                double centerZ = bbox.Min.Z; // Use room's Z elevation
                double roomWidth = bbox.Max.X - bbox.Min.X;
                double roomHeight = bbox.Max.Y - bbox.Min.Y;

                // Collect wall segments with their orientations, element IDs, and wall thickness
                var horizontalSegments = new List<(BoundarySegment segment, XYZ start, XYZ end, double y, double wallWidth)>();
                var verticalSegments = new List<(BoundarySegment segment, XYZ start, XYZ end, double x, double wallWidth)>();

                var firstLoop = boundarySegments[0];
                foreach (var segment in firstLoop)
                {
                    var curve = segment.GetCurve();
                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);
                    var direction = (end - start).Normalize();

                    // Only consider segments backed by walls
                    var wallElement = doc.GetElement(segment.ElementId) as Wall;
                    if (wallElement == null) continue;

                    // Get wall thickness
                    double wallWidth = wallElement.Width;

                    // Horizontal segments (running left-right)
                    if (Math.Abs(direction.X) > 0.7)
                    {
                        double avgY = (start.Y + end.Y) / 2.0;
                        horizontalSegments.Add((segment, start, end, avgY, wallWidth));
                    }
                    // Vertical segments (running up-down)
                    else if (Math.Abs(direction.Y) > 0.7)
                    {
                        double avgX = (start.X + end.X) / 2.0;
                        verticalSegments.Add((segment, start, end, avgX, wallWidth));
                    }
                }

                using (var trans = new Transaction(doc, "Add Room Dimensions"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var dimensionIds = new List<int>();
                    var errors = new List<string>();
                    var geoOptions = new Options() { ComputeReferences = true, View = activeView };

                    // === HORIZONTAL DIMENSION (measures room width) ===
                    // Place at BOTTOM of room, dimension to WALL CENTERLINE
                    if (verticalSegments.Count >= 2)
                    {
                        var leftWallSeg = verticalSegments.OrderBy(s => s.x).First();
                        var rightWallSeg = verticalSegments.OrderByDescending(s => s.x).First();

                        // Position at bottom of room (offset slightly inside)
                        double dimY = bbox.Min.Y + (roomHeight * 0.15); // 15% up from bottom
                        double lineLength = 1.0;

                        // Adjust positions to wall CENTERLINE
                        // Boundary is at interior face, centerline is half wall width OUTWARD from room
                        double leftX = leftWallSeg.x - (leftWallSeg.wallWidth / 2.0);  // Left wall centerline (left of interior)
                        double rightX = rightWallSeg.x + (rightWallSeg.wallWidth / 2.0);  // Right wall centerline (right of interior)

                        var leftLine = Line.CreateBound(
                            new XYZ(leftX, dimY - lineLength/2, centerZ),
                            new XYZ(leftX, dimY + lineLength/2, centerZ)
                        );
                        var rightLine = Line.CreateBound(
                            new XYZ(rightX, dimY - lineLength/2, centerZ),
                            new XYZ(rightX, dimY + lineLength/2, centerZ)
                        );

                        var leftDetailLine = doc.Create.NewDetailCurve(activeView, leftLine);
                        var rightDetailLine = doc.Create.NewDetailCurve(activeView, rightLine);

                        if (leftDetailLine != null && rightDetailLine != null)
                        {
                            var refArray = new ReferenceArray();
                            refArray.Append(leftDetailLine.GeometryCurve.Reference);
                            refArray.Append(rightDetailLine.GeometryCurve.Reference);

                            var dimLine = Line.CreateBound(
                                new XYZ(leftX, dimY, centerZ),
                                new XYZ(rightX, dimY, centerZ)
                            );

                            try
                            {
                                var dim = doc.Create.NewDimension(activeView, dimLine, refArray);
                                if (dim != null)
                                {
                                    dimensionIds.Add((int)dim.Id.Value);
                                    // KEEP detail lines - dimensions need their references to persist
                                }
                                else
                                {
                                    errors.Add("Horizontal dimension returned null");
                                    // Delete detail lines only if dimension failed
                                    doc.Delete(leftDetailLine.Id);
                                    doc.Delete(rightDetailLine.Id);
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Horizontal dim error: {ex.Message}");
                                doc.Delete(leftDetailLine.Id);
                                doc.Delete(rightDetailLine.Id);
                            }
                        }
                        else
                        {
                            errors.Add("Could not create detail lines for horizontal dimension");
                        }
                    }
                    else
                    {
                        errors.Add($"Not enough vertical segments: {verticalSegments.Count}");
                    }

                    // === VERTICAL DIMENSION (measures room height) ===
                    // Place on RIGHT side of room, dimension to WALL EXTERIOR FACE
                    if (horizontalSegments.Count >= 2)
                    {
                        var bottomWallSeg = horizontalSegments.OrderBy(s => s.y).First();
                        var topWallSeg = horizontalSegments.OrderByDescending(s => s.y).First();

                        // Position on right side of room (offset slightly inside)
                        double dimX = bbox.Max.X - (roomWidth * 0.08); // 8% in from right
                        double lineLength = 1.0;

                        // Adjust positions to wall EXTERIOR FACE
                        // Boundary is at interior face, exterior is full wall width OUTWARD from room
                        double bottomY = bottomWallSeg.y - bottomWallSeg.wallWidth;  // Bottom wall exterior (below interior)
                        double topY = topWallSeg.y + topWallSeg.wallWidth;  // Top wall exterior (above interior)

                        var bottomLine = Line.CreateBound(
                            new XYZ(dimX - lineLength/2, bottomY, centerZ),
                            new XYZ(dimX + lineLength/2, bottomY, centerZ)
                        );
                        var topLine = Line.CreateBound(
                            new XYZ(dimX - lineLength/2, topY, centerZ),
                            new XYZ(dimX + lineLength/2, topY, centerZ)
                        );

                        var bottomDetailLine = doc.Create.NewDetailCurve(activeView, bottomLine);
                        var topDetailLine = doc.Create.NewDetailCurve(activeView, topLine);

                        if (bottomDetailLine != null && topDetailLine != null)
                        {
                            var refArray = new ReferenceArray();
                            refArray.Append(bottomDetailLine.GeometryCurve.Reference);
                            refArray.Append(topDetailLine.GeometryCurve.Reference);

                            var dimLine = Line.CreateBound(
                                new XYZ(dimX, bottomY, centerZ),
                                new XYZ(dimX, topY, centerZ)
                            );

                            try
                            {
                                var dim = doc.Create.NewDimension(activeView, dimLine, refArray);
                                if (dim != null)
                                {
                                    dimensionIds.Add((int)dim.Id.Value);
                                    // KEEP detail lines - dimensions need their references to persist
                                }
                                else
                                {
                                    errors.Add("Vertical dimension returned null");
                                    // Delete detail lines only if dimension failed
                                    doc.Delete(bottomDetailLine.Id);
                                    doc.Delete(topDetailLine.Id);
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Vertical dim error: {ex.Message}");
                                doc.Delete(bottomDetailLine.Id);
                                doc.Delete(topDetailLine.Id);
                            }
                        }
                        else
                        {
                            errors.Add("Could not create detail lines for vertical dimension");
                        }
                    }
                    else
                    {
                        errors.Add($"Not enough horizontal segments: {horizontalSegments.Count}");
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = dimensionIds.Count > 0,
                        roomId = (int)room.Id.Value,
                        roomNumber = room.Number,
                        roomName = room.Name,
                        dimensionCount = dimensionIds.Count,
                        dimensionIds = dimensionIds,
                        horizontalSegmentCount = horizontalSegments.Count,
                        verticalSegmentCount = verticalSegments.Count,
                        roomWidth = Math.Round(roomWidth, 2),
                        roomHeight = Math.Round(roomHeight, 2),
                        errors = errors.Count > 0 ? errors : null
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Analyze dimensions in a view to extract the pattern (what they reference)
        /// Use this to capture how a user set up dimensions in a template room
        /// </summary>
        public static string GetDimensionPattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get view - use provided viewId or active view
                View view;
                if (parameters["viewId"] != null)
                {
                    var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                    view = doc.GetElement(viewId) as View;
                    if (view == null)
                        return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }
                else
                {
                    view = doc.ActiveView;
                }

                // Collect all dimensions in the view
                var collector = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Dimension))
                    .WhereElementIsNotElementType();

                var dimensions = collector.Cast<Dimension>().ToList();
                var dimensionPatterns = new List<object>();
                var geoOptions = new Options() { ComputeReferences = true, View = view };

                foreach (var dim in dimensions)
                {
                    try
                    {
                        // Get dimension line direction
                        var curve = dim.Curve;
                        if (curve == null) continue;

                        var line = curve as Line;
                        if (line == null) continue;

                        var direction = line.Direction.Normalize();
                        bool isHorizontal = Math.Abs(direction.X) > 0.7;
                        bool isVertical = Math.Abs(direction.Y) > 0.7;

                        // Get dimension value
                        double? dimValue = dim.Value;

                        // Analyze references
                        var refInfos = new List<object>();
                        var references = dim.References;

                        foreach (Reference refr in references)
                        {
                            var refElement = doc.GetElement(refr.ElementId);
                            string refType = "unknown";
                            string elementType = refElement?.GetType().Name ?? "null";
                            int elementId = refElement != null ? (int)refElement.Id.Value : -1;
                            double? refX = null;
                            double? refY = null;

                            if (refElement is Wall wall)
                            {
                                // Determine if this is centerline or face reference
                                var wallCurve = (wall.Location as LocationCurve)?.Curve;
                                if (wallCurve != null)
                                {
                                    var wallMidpoint = wallCurve.Evaluate(0.5, true);
                                    refX = Math.Round(wallMidpoint.X, 4);
                                    refY = Math.Round(wallMidpoint.Y, 4);
                                }

                                // Check reference type by examining the stable representation
                                string stableRef = refr.ConvertToStableRepresentation(doc);
                                if (stableRef.Contains("SURFACE"))
                                {
                                    // It's a face reference - determine which face
                                    // by checking the geometry
                                    refType = "wallFace";

                                    // Try to determine interior vs exterior
                                    var wallDir = ((wallCurve as Line)?.Direction ?? XYZ.BasisX).Normalize();
                                    var wallNormal = new XYZ(-wallDir.Y, wallDir.X, 0);

                                    // Get the face and check its normal
                                    try
                                    {
                                        var face = refElement.GetGeometryObjectFromReference(refr) as PlanarFace;
                                        if (face != null)
                                        {
                                            double dot = face.FaceNormal.DotProduct(wallNormal);
                                            refType = dot > 0 ? "wallExteriorFace" : "wallInteriorFace";
                                        }
                                    }
                                    catch { }
                                }
                                else
                                {
                                    refType = "wallCenterline";
                                }
                            }
                            else if (refElement is DetailCurve)
                            {
                                refType = "detailLine";
                                var detailCurve = (refElement as DetailCurve).GeometryCurve;
                                if (detailCurve != null)
                                {
                                    var midPt = detailCurve.Evaluate(0.5, true);
                                    refX = Math.Round(midPt.X, 4);
                                    refY = Math.Round(midPt.Y, 4);
                                }
                            }

                            refInfos.Add(new
                            {
                                referenceType = refType,
                                elementType = elementType,
                                elementId = elementId,
                                x = refX,
                                y = refY
                            });
                        }

                        // Get dimension position
                        var dimMidpoint = line.Evaluate(0.5, true);

                        dimensionPatterns.Add(new
                        {
                            dimensionId = (int)dim.Id.Value,
                            orientation = isHorizontal ? "horizontal" : (isVertical ? "vertical" : "angled"),
                            value = dimValue.HasValue ? Math.Round(dimValue.Value, 4) : (double?)null,
                            valueFeet = dimValue.HasValue ? Math.Round(dimValue.Value, 2) : (double?)null,
                            position = new { x = Math.Round(dimMidpoint.X, 4), y = Math.Round(dimMidpoint.Y, 4) },
                            references = refInfos
                        });
                    }
                    catch (Exception ex)
                    {
                        dimensionPatterns.Add(new
                        {
                            dimensionId = (int)dim.Id.Value,
                            error = ex.Message
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = (int)view.Id.Value,
                    viewName = view.Name,
                    dimensionCount = dimensions.Count,
                    dimensions = dimensionPatterns
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add dimensions to a room using wall references (centerline or face)
        /// This allows precise control over what the dimensions measure
        /// </summary>
        public static string AddRoomDimensionsWithPattern(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var activeView = doc.ActiveView;

                if (parameters["roomId"] == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "roomId required" });
                }

                var roomId = new ElementId(int.Parse(parameters["roomId"].ToString()));
                var room = doc.GetElement(roomId) as Room;

                if (room == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Room not found" });
                }

                // Get reference type parameters (default to wallCenterline)
                string horizontalLeftRef = parameters["horizontalLeftRef"]?.ToString() ?? "wallCenterline";
                string horizontalRightRef = parameters["horizontalRightRef"]?.ToString() ?? "wallCenterline";
                string verticalBottomRef = parameters["verticalBottomRef"]?.ToString() ?? "wallCenterline";
                string verticalTopRef = parameters["verticalTopRef"]?.ToString() ?? "wallCenterline";

                var boundaryOptions = new SpatialElementBoundaryOptions();
                var boundarySegments = room.GetBoundarySegments(boundaryOptions);

                if (boundarySegments == null || boundarySegments.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No boundary segments" });
                }

                var bbox = room.get_BoundingBox(activeView);
                if (bbox == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Could not get room bounding box" });
                }

                double centerZ = bbox.Min.Z;
                double roomWidth = bbox.Max.X - bbox.Min.X;
                double roomHeight = bbox.Max.Y - bbox.Min.Y;

                // Collect wall segments
                var horizontalWalls = new List<(Wall wall, BoundarySegment segment, double y)>();
                var verticalWalls = new List<(Wall wall, BoundarySegment segment, double x)>();

                var firstLoop = boundarySegments[0];
                foreach (var segment in firstLoop)
                {
                    var curve = segment.GetCurve();
                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);
                    var direction = (end - start).Normalize();

                    var wall = doc.GetElement(segment.ElementId) as Wall;
                    if (wall == null) continue;

                    if (Math.Abs(direction.X) > 0.7)
                    {
                        double avgY = (start.Y + end.Y) / 2.0;
                        horizontalWalls.Add((wall, segment, avgY));
                    }
                    else if (Math.Abs(direction.Y) > 0.7)
                    {
                        double avgX = (start.X + end.X) / 2.0;
                        verticalWalls.Add((wall, segment, avgX));
                    }
                }

                using (var trans = new Transaction(doc, "Add Room Dimensions with Pattern"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var dimensionIds = new List<int>();
                    var errors = new List<string>();
                    var geoOptions = new Options() { ComputeReferences = true, View = activeView };

                    // === HORIZONTAL DIMENSION ===
                    if (verticalWalls.Count >= 2)
                    {
                        var leftWall = verticalWalls.OrderBy(w => w.x).First();
                        var rightWall = verticalWalls.OrderByDescending(w => w.x).First();

                        Reference leftRef = GetWallReference(leftWall.wall, geoOptions, horizontalLeftRef, true);
                        Reference rightRef = GetWallReference(rightWall.wall, geoOptions, horizontalRightRef, false);

                        if (leftRef != null && rightRef != null)
                        {
                            var refArray = new ReferenceArray();
                            refArray.Append(leftRef);
                            refArray.Append(rightRef);

                            double dimY = bbox.Min.Y + (roomHeight * 0.15);
                            var dimLine = Line.CreateBound(
                                new XYZ(leftWall.x, dimY, centerZ),
                                new XYZ(rightWall.x, dimY, centerZ)
                            );

                            try
                            {
                                var dim = doc.Create.NewDimension(activeView, dimLine, refArray);
                                if (dim != null)
                                {
                                    dimensionIds.Add((int)dim.Id.Value);
                                }
                                else
                                {
                                    errors.Add("Horizontal dimension returned null");
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Horizontal dim error: {ex.Message}");
                            }
                        }
                        else
                        {
                            errors.Add($"Could not get wall references (left: {leftRef != null}, right: {rightRef != null})");
                        }
                    }

                    // === VERTICAL DIMENSION ===
                    if (horizontalWalls.Count >= 2)
                    {
                        var bottomWall = horizontalWalls.OrderBy(w => w.y).First();
                        var topWall = horizontalWalls.OrderByDescending(w => w.y).First();

                        Reference bottomRef = GetWallReference(bottomWall.wall, geoOptions, verticalBottomRef, true);
                        Reference topRef = GetWallReference(topWall.wall, geoOptions, verticalTopRef, false);

                        if (bottomRef != null && topRef != null)
                        {
                            var refArray = new ReferenceArray();
                            refArray.Append(bottomRef);
                            refArray.Append(topRef);

                            double dimX = bbox.Max.X - (roomWidth * 0.08);
                            var dimLine = Line.CreateBound(
                                new XYZ(dimX, bottomWall.y, centerZ),
                                new XYZ(dimX, topWall.y, centerZ)
                            );

                            try
                            {
                                var dim = doc.Create.NewDimension(activeView, dimLine, refArray);
                                if (dim != null)
                                {
                                    dimensionIds.Add((int)dim.Id.Value);
                                }
                                else
                                {
                                    errors.Add("Vertical dimension returned null");
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Vertical dim error: {ex.Message}");
                            }
                        }
                        else
                        {
                            errors.Add($"Could not get wall references (bottom: {bottomRef != null}, top: {topRef != null})");
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = dimensionIds.Count > 0,
                        roomId = (int)room.Id.Value,
                        roomNumber = room.Number,
                        roomName = room.Name,
                        dimensionCount = dimensionIds.Count,
                        dimensionIds = dimensionIds,
                        pattern = new
                        {
                            horizontalLeftRef,
                            horizontalRightRef,
                            verticalBottomRef,
                            verticalTopRef
                        },
                        errors = errors.Count > 0 ? errors : null
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get a wall reference based on the specified type
        /// </summary>
        private static Reference GetWallReference(Wall wall, Options geoOptions, string refType, bool positiveDirection)
        {
            try
            {
                if (refType == "wallCenterline")
                {
                    // Return wall centerline reference
                    return new Reference(wall);
                }
                else if (refType == "wallInteriorFace" || refType == "wallExteriorFace")
                {
                    // Get the appropriate face reference
                    bool wantExterior = refType == "wallExteriorFace";
                    return GetWallFaceReferenceByType(wall, geoOptions, wantExterior, positiveDirection);
                }
                else
                {
                    // Default to wall reference
                    return new Reference(wall);
                }
            }
            catch
            {
                return new Reference(wall);
            }
        }

        /// <summary>
        /// Get a wall face reference (interior or exterior)
        /// </summary>
        private static Reference GetWallFaceReferenceByType(Wall wall, Options geoOptions, bool exterior, bool positiveDirection)
        {
            try
            {
                var geometry = wall.get_Geometry(geoOptions);
                if (geometry == null) return new Reference(wall);

                var wallCurve = (wall.Location as LocationCurve)?.Curve;
                if (wallCurve == null) return new Reference(wall);

                var wallDir = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
                var wallNormal = new XYZ(-wallDir.Y, wallDir.X, 0);

                Reference bestRef = null;
                double bestDot = -2;

                foreach (var geoObj in geometry)
                {
                    Solid solid = geoObj as Solid;
                    if (solid == null || solid.Faces.Size == 0) continue;

                    foreach (Face face in solid.Faces)
                    {
                        PlanarFace planarFace = face as PlanarFace;
                        if (planarFace == null) continue;

                        if (Math.Abs(planarFace.FaceNormal.Z) > 0.1) continue;

                        double dotProduct = planarFace.FaceNormal.DotProduct(wallNormal);

                        // For exterior face, we want positive dot product
                        // For interior face, we want negative dot product
                        bool isExteriorFace = dotProduct > 0;

                        if (exterior == isExteriorFace)
                        {
                            double absDot = Math.Abs(dotProduct);
                            if (absDot > bestDot)
                            {
                                bestDot = absDot;
                                bestRef = planarFace.Reference;
                            }
                        }
                    }
                }

                return bestRef ?? new Reference(wall);
            }
            catch
            {
                return new Reference(wall);
            }
        }

        /// <summary>
        /// Get a reference to a wall face for dimensioning
        /// </summary>
        private static Reference GetWallFaceReference(Wall wall, Options geoOptions, bool positiveDirection)
        {
            try
            {
                var geometry = wall.get_Geometry(geoOptions);
                if (geometry == null) return null;

                // Get wall orientation to determine which face to use
                var wallCurve = (wall.Location as LocationCurve)?.Curve;
                if (wallCurve == null) return null;

                var wallDir = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
                var wallNormal = new XYZ(-wallDir.Y, wallDir.X, 0); // Perpendicular to wall direction

                Reference bestRef = null;
                double bestDot = -1;

                foreach (var geoObj in geometry)
                {
                    Solid solid = geoObj as Solid;
                    if (solid == null || solid.Faces.Size == 0) continue;

                    foreach (Face face in solid.Faces)
                    {
                        PlanarFace planarFace = face as PlanarFace;
                        if (planarFace == null) continue;

                        // Check if this is a vertical face (not top or bottom)
                        if (Math.Abs(planarFace.FaceNormal.Z) > 0.1) continue;

                        // Check if face normal is roughly perpendicular to wall direction
                        double dotProduct = planarFace.FaceNormal.DotProduct(wallNormal);

                        // We want the face that points in the desired direction
                        if (positiveDirection && dotProduct > bestDot)
                        {
                            bestDot = dotProduct;
                            bestRef = planarFace.Reference;
                        }
                        else if (!positiveDirection && -dotProduct > bestDot)
                        {
                            bestDot = -dotProduct;
                            bestRef = planarFace.Reference;
                        }
                    }
                }

                // Fallback to wall reference if no face found
                return bestRef ?? new Reference(wall);
            }
            catch
            {
                return new Reference(wall);
            }
        }
    }
}
