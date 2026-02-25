using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Automated Filled Region Solution for Room Boundaries
    /// Creates transparent filled regions to show effective room area with offsets
    /// </summary>
    public static class AutomatedFilledRegion
    {
        /// <summary>
        /// Create filled regions for ALL offices in the current view automatically
        /// </summary>
        public static string CreateFilledRegionsForAllOffices(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var activeView = doc.ActiveView;

                // Get parameters
                var fillPatternName = parameters["fillPatternName"]?.ToString() ?? "Solid fill";
                var transparency = parameters["transparency"]?.ToObject<int>() ?? 50;
                var roomNameFilter = parameters["roomNameFilter"]?.ToString() ?? "OFFICE";

                // Get all rooms in the active view
                var allRooms = new FilteredElementCollector(doc, activeView.Id)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0)
                    .Where(r => r.Name != null && r.Name.ToUpper().Contains(roomNameFilter.ToUpper()))
                    .ToList();

                if (allRooms.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"No rooms found with name containing '{roomNameFilter}'"
                    });
                }

                var results = new List<object>();
                var successCount = 0;
                var failCount = 0;

                using (var trans = new Transaction(doc, "Create Filled Regions for All Offices"))
                {
                    trans.Start();

                    foreach (var room in allRooms)
                    {
                        try
                        {
                            var result = CreateFilledRegionForRoom(doc, activeView, room, fillPatternName, transparency);
                            results.Add(new
                            {
                                success = true,
                                roomId = (int)room.Id.Value,
                                roomNumber = room.Number,
                                roomName = room.Name,
                                originalArea = result.originalArea,
                                effectiveArea = result.effectiveArea,
                                areaDifference = result.areaDifference,
                                filledRegionId = result.filledRegionId
                            });
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            results.Add(new
                            {
                                success = false,
                                roomId = (int)room.Id.Value,
                                roomNumber = room.Number,
                                roomName = room.Name,
                                error = ex.Message
                            });
                            failCount++;
                        }
                    }

                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalRooms = allRooms.Count,
                    successCount = successCount,
                    failCount = failCount,
                    results = results,
                    viewId = (int)activeView.Id.Value,
                    viewName = activeView.Name
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create filled region showing room with offset boundaries
        /// </summary>
        public static string CreateRoomBoundaryFilledRegion(UIApplication uiApp, JObject parameters)
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

                // Get offset parameters
                var hallwayOffset = parameters["hallwayOffset"]?.ToObject<double>() ?? 0.5;  // Default 0.5 ft toward office
                var fillPatternName = parameters["fillPatternName"]?.ToString() ?? "Solid fill";
                var transparency = parameters["transparency"]?.ToObject<int>() ?? 50;  // 50% transparent

                var options = new SpatialElementBoundaryOptions();
                var boundarySegments = room.GetBoundarySegments(options);

                if (boundarySegments == null || boundarySegments.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No boundary segments" });
                }

                using (var trans = new Transaction(doc, "Create Room Boundary Filled Region"))
                {
                    trans.Start();

                    // Get level elevation for planar consistency
                    var level = doc.GetElement(room.LevelId) as Level;
                    var levelZ = level.Elevation;
                    var roomCenter = GetRoomCenter(room);

                    // Process ONLY the first (outer) boundary loop
                    if (boundarySegments.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "No boundary segments" });
                    }

                    var firstLoop = boundarySegments[0];
                    var offsetInfo = new List<object>();

                    // Collect points for the offset boundary
                    var offsetPoints = new List<XYZ>();

                    for (int i = 0; i < firstLoop.Count; i++)
                    {
                        var segment = firstLoop[i];
                        var curve = segment.GetCurve();
                        var wallElement = doc.GetElement(segment.ElementId) as Wall;

                        // Determine wall classification and offset distance
                        string wallClassification = "Unknown";
                        double offsetDistance = 0.0;

                        if (wallElement != null)
                        {
                            var wallThickness = GetWallThickness(doc, wallElement);

                            if (IsExteriorWall(doc, wallElement))
                            {
                                wallClassification = "Exterior";
                                offsetDistance = wallThickness; // Full thickness - move to outside edge
                            }
                            else if (IsHallwayWall(doc, wallElement, room, options))
                            {
                                wallClassification = "Hallway";
                                offsetDistance = wallThickness; // Full thickness - move to hallway edge
                            }
                            else if (IsDemisingWall(doc, wallElement, room, options))
                            {
                                wallClassification = "Demising";
                                offsetDistance = 0.0; // Stay at centerline
                            }

                            offsetInfo.Add(new
                            {
                                index = i,
                                wallId = (int)wallElement.Id.Value,
                                wallType = doc.GetElement(wallElement.GetTypeId())?.Name,
                                classification = wallClassification,
                                wallThickness = wallThickness,
                                offsetDistance = offsetDistance
                            });
                        }

                        // Calculate offset curve
                        var start = curve.GetEndPoint(0);
                        var end = curve.GetEndPoint(1);

                        // Force Z to level elevation
                        start = new XYZ(start.X, start.Y, levelZ);
                        end = new XYZ(end.X, end.Y, levelZ);

                        // Calculate perpendicular direction (pointing toward room)
                        var direction = (end - start).Normalize();
                        var perpendicular = new XYZ(-direction.Y, direction.X, 0);

                        // Check if perpendicular points toward room center
                        var midpoint = (start + end) / 2;
                        var toRoom = (roomCenter - midpoint).Normalize();

                        // If perpendicular points away from room, flip it
                        if (perpendicular.DotProduct(toRoom) < 0)
                        {
                            perpendicular = perpendicular.Negate();
                        }

                        // Apply offset (positive = toward room, negative = away from room)
                        // For exterior/hallway, we want to move AWAY from room (toward exterior)
                        var offsetVector = perpendicular * -offsetDistance; // Negate to move away

                        var offsetStart = start + offsetVector;
                        var offsetEnd = end + offsetVector;

                        offsetPoints.Add(offsetStart);
                    }

                    // Build curve loop from offset points
                    var curveLoop = new CurveLoop();
                    for (int i = 0; i < offsetPoints.Count; i++)
                    {
                        var p1 = offsetPoints[i];
                        var p2 = offsetPoints[(i + 1) % offsetPoints.Count];

                        var line = Line.CreateBound(p1, p2);
                        curveLoop.Append(line);
                    }

                    // Get or create filled region type
                    var filledRegionType = GetOrCreateTransparentFilledRegionType(doc, fillPatternName, transparency);

                    // Create filled region
                    FilledRegion filledRegion = null;
                    try
                    {
                        filledRegion = FilledRegion.Create(doc, filledRegionType.Id, activeView.Id, new List<CurveLoop> { curveLoop });
                    }
                    catch (Exception ex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = ex.Message,
                            offsetInfo = offsetInfo,
                            stackTrace = ex.StackTrace
                        });
                    }

                    // Calculate area
                    var area = CalculateCurveLoopArea(curveLoop);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roomId = (int)room.Id.Value,
                        roomNumber = room.Number,
                        roomName = room.Name,
                        filledRegionId = (int)filledRegion.Id.Value,
                        originalArea = room.Area,
                        effectiveArea = area,
                        areaDifference = area - room.Area,
                        offsetCount = offsetInfo.Count,
                        offsets = offsetInfo,
                        viewId = (int)activeView.Id.Value,
                        viewName = activeView.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static (int filledRegionId, double originalArea, double effectiveArea, double areaDifference) CreateFilledRegionForRoom(
            Document doc, View activeView, Room room, string fillPatternName, int transparency)
        {
            var options = new SpatialElementBoundaryOptions();
            var boundarySegments = room.GetBoundarySegments(options);

            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                throw new Exception($"Room {room.Number} has no boundary segments");
            }

            var level = doc.GetElement(room.LevelId) as Level;
            var levelZ = level.Elevation;
            var roomCenter = GetRoomCenter(room);

            // Process ONLY the first (outer) boundary loop
            var firstLoop = boundarySegments[0];
            var offsetLines = new List<Line>();

            // First pass: Create offset lines for each segment
            for (int i = 0; i < firstLoop.Count; i++)
            {
                var segment = firstLoop[i];
                var curve = segment.GetCurve();
                var wallElement = doc.GetElement(segment.ElementId) as Wall;

                // Determine offset distance
                double offsetDistance = 0.0;

                if (wallElement != null)
                {
                    var wallThickness = GetWallThickness(doc, wallElement);

                    if (IsExteriorWall(doc, wallElement))
                    {
                        offsetDistance = wallThickness; // Full thickness - move to outside edge
                    }
                    else if (IsHallwayWall(doc, wallElement, room, options))
                    {
                        offsetDistance = wallThickness; // Full thickness - move to hallway edge
                    }
                    else if (IsDemisingWall(doc, wallElement, room, options))
                    {
                        offsetDistance = 0.0; // Stay at center
                    }
                }

                // Get endpoints
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);

                // Force Z to level elevation
                start = new XYZ(start.X, start.Y, levelZ);
                end = new XYZ(end.X, end.Y, levelZ);

                // Calculate perpendicular direction (pointing toward room)
                var direction = (end - start).Normalize();
                var perpendicular = new XYZ(-direction.Y, direction.X, 0);

                // Check if perpendicular points toward room center
                var midpoint = (start + end) / 2;
                var toRoom = (roomCenter - midpoint).Normalize();

                // If perpendicular points away from room, flip it
                if (perpendicular.DotProduct(toRoom) < 0)
                {
                    perpendicular = perpendicular.Negate();
                }

                // Apply offset (negative = away from room toward exterior/hallway)
                var offsetVector = perpendicular * -offsetDistance;

                var offsetStart = start + offsetVector;
                var offsetEnd = end + offsetVector;

                offsetLines.Add(Line.CreateBound(offsetStart, offsetEnd));
            }

            // Second pass: Find intersection points between consecutive offset lines
            var intersectionPoints = new List<XYZ>();

            for (int i = 0; i < offsetLines.Count; i++)
            {
                var currentLine = offsetLines[i];
                var nextLine = offsetLines[(i + 1) % offsetLines.Count];

                // Calculate intersection point
                var intersection = LineIntersection(currentLine, nextLine);
                if (intersection != null)
                {
                    intersectionPoints.Add(intersection);
                }
                else
                {
                    // If no intersection (parallel lines), use endpoint
                    intersectionPoints.Add(currentLine.GetEndPoint(1));
                }
            }

            // Build curve loop from intersection points
            var curveLoop = new CurveLoop();
            for (int i = 0; i < intersectionPoints.Count; i++)
            {
                var p1 = intersectionPoints[i];
                var p2 = intersectionPoints[(i + 1) % intersectionPoints.Count];

                var line = Line.CreateBound(p1, p2);
                curveLoop.Append(line);
            }

            // Get or create filled region type
            var filledRegionType = GetOrCreateTransparentFilledRegionType(doc, fillPatternName, transparency);

            // Create filled region
            var filledRegion = FilledRegion.Create(doc, filledRegionType.Id, activeView.Id, new List<CurveLoop> { curveLoop });

            // Calculate area
            var effectiveArea = CalculateCurveLoopArea(curveLoop);
            var originalArea = room.Area;
            var areaDifference = effectiveArea - originalArea;

            return ((int)filledRegion.Id.Value, originalArea, effectiveArea, areaDifference);
        }

        private static Curve CalculateOffsetCurve(Curve originalCurve, double offsetDistance, XYZ roomCenter)
        {
            var start = originalCurve.GetEndPoint(0);
            var end = originalCurve.GetEndPoint(1);

            // Calculate perpendicular direction
            var direction = (end - start).Normalize();
            var perpendicular = new XYZ(-direction.Y, direction.X, 0);

            // Determine which side points toward room
            var midpoint = (start + end) / 2;
            var toRoom = (roomCenter - midpoint).Normalize();

            // If perpendicular points away from room, flip it
            if (perpendicular.DotProduct(toRoom) < 0)
            {
                perpendicular = perpendicular.Negate();
            }

            // Create offset points
            var offsetStart = start + (perpendicular * offsetDistance);
            var offsetEnd = end + (perpendicular * offsetDistance);

            // Return offset line
            return Line.CreateBound(offsetStart, offsetEnd);
        }

        private static double CalculateCurveLoopArea(CurveLoop curveLoop)
        {
            // Use shoelace formula for polygon area
            var points = new List<XYZ>();
            foreach (var curve in curveLoop)
            {
                points.Add(curve.GetEndPoint(0));
            }

            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % points.Count];
                area += (p1.X * p2.Y) - (p2.X * p1.Y);
            }
            return Math.Abs(area / 2);
        }

        private static FilledRegionType GetOrCreateTransparentFilledRegionType(Document doc, string patternName, int transparency)
        {
            // Look for existing filled region type
            var existingType = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name.Contains("Room Boundary"));

            if (existingType != null)
            {
                return existingType;
            }

            // Create new filled region type
            var collector = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType));
            var defaultType = collector.FirstOrDefault() as FilledRegionType;

            if (defaultType != null)
            {
                var newType = defaultType.Duplicate("Room Boundary - Transparent") as FilledRegionType;
                return newType;
            }

            return defaultType;
        }

        private static bool IsHallwayWall(Document doc, Wall wall, Room room, SpatialElementBoundaryOptions options)
        {
            var adjacentRooms = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .Cast<Room>()
                .Where(r => r.Id != room.Id && r.Area > 0)
                .Where(r =>
                {
                    var boundaries = r.GetBoundarySegments(options);
                    if (boundaries == null) return false;
                    foreach (var segList in boundaries)
                    {
                        if (segList.Any(s => s.ElementId == wall.Id))
                            return true;
                    }
                    return false;
                })
                .ToList();

            return adjacentRooms.Any(r => r.Name != null &&
                   (r.Name.ToUpper().Contains("HALL") || r.Name.ToUpper().Contains("CORRIDOR")));
        }

        private static bool IsExteriorWall(Document doc, Wall wall)
        {
            // Check if wall has no adjacent rooms on one side (exterior)
            var wallType = doc.GetElement(wall.GetTypeId()) as WallType;
            if (wallType != null && wallType.Name.ToUpper().Contains("EXTERIOR"))
                return true;

            // Check if wall function is exterior
            var function = wallType?.Function;
            if (function == WallFunction.Exterior)
                return true;

            return false;
        }

        private static bool IsDemisingWall(Document doc, Wall wall, Room room, SpatialElementBoundaryOptions options)
        {
            // Demising wall is between two offices (not hallway, not exterior)
            if (IsExteriorWall(doc, wall))
                return false;

            if (IsHallwayWall(doc, wall, room, options))
                return false;

            // Check if adjacent room is also an office
            var adjacentRooms = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .Cast<Room>()
                .Where(r => r.Id != room.Id && r.Area > 0)
                .Where(r =>
                {
                    var boundaries = r.GetBoundarySegments(options);
                    if (boundaries == null) return false;
                    foreach (var segList in boundaries)
                    {
                        if (segList.Any(s => s.ElementId == wall.Id))
                            return true;
                    }
                    return false;
                })
                .ToList();

            return adjacentRooms.Any(r => r.Name != null && r.Name.ToUpper().Contains("OFFICE"));
        }

        private static double GetWallThickness(Document doc, Wall wall)
        {
            var wallType = doc.GetElement(wall.GetTypeId()) as WallType;
            if (wallType != null)
            {
                return wallType.Width;
            }
            return 0.5; // Default fallback
        }

        private static XYZ LineIntersection(Line line1, Line line2)
        {
            // Get line endpoints
            var p1 = line1.GetEndPoint(0);
            var p2 = line1.GetEndPoint(1);
            var p3 = line2.GetEndPoint(0);
            var p4 = line2.GetEndPoint(1);

            // Calculate line directions
            var d1 = p2 - p1;
            var d2 = p4 - p3;

            // Calculate denominator for intersection formula
            var denominator = d1.X * d2.Y - d1.Y * d2.X;

            // Check if lines are parallel (denominator is zero)
            if (Math.Abs(denominator) < 0.0001)
            {
                return null; // Lines are parallel
            }

            // Calculate intersection parameter for line1
            var t = ((p3.X - p1.X) * d2.Y - (p3.Y - p1.Y) * d2.X) / denominator;

            // Calculate intersection point
            var intersection = new XYZ(
                p1.X + t * d1.X,
                p1.Y + t * d1.Y,
                p1.Z // Use same Z as input lines
            );

            return intersection;
        }

        private static XYZ GetRoomCenter(Room room)
        {
            var locationPoint = room.Location as LocationPoint;
            if (locationPoint != null)
            {
                return locationPoint.Point;
            }

            var bbox = room.get_BoundingBox(null);
            if (bbox != null)
            {
                return (bbox.Min + bbox.Max) / 2;
            }

            return XYZ.Zero;
        }
    }
}
