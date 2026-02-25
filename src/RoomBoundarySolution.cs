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
    /// Automated Room Boundary Control using Room Separation Lines
    /// This is the PROPER way to control room boundaries in Revit
    /// </summary>
    public static class RoomBoundarySolution
    {
        /// <summary>
        /// Create offset room separation lines for room boundaries
        /// </summary>
        public static string CreateOffsetRoomBoundaries(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

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

                // Get offset distance (default 0.5 ft toward office for hallway walls)
                var hallwayOffset = parameters["hallwayOffset"]?.ToObject<double>() ?? 0.5;
                var createForDemising = parameters["createForDemising"]?.ToObject<bool>() ?? false;

                var createdLines = new List<object>();
                var options = new SpatialElementBoundaryOptions();
                var boundarySegments = room.GetBoundarySegments(options);

                if (boundarySegments == null || boundarySegments.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No boundary segments" });
                }

                using (var trans = new Transaction(doc, "Create Room Boundary Lines"))
                {
                    trans.Start();

                    // Get the room's level to create sketch plane
                    var level = doc.GetElement(room.LevelId) as Level;
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation));
                    var sketchPlane = SketchPlane.Create(doc, plane);

                    // Get room boundary line style
                    var roomBoundaryCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_RoomSeparationLines);
                    var graphicsStyle = roomBoundaryCat?.GetGraphicsStyle(GraphicsStyleType.Projection);

                    foreach (var segmentList in boundarySegments)
                    {
                        foreach (var segment in segmentList)
                        {
                            var wallElement = doc.GetElement(segment.ElementId) as Wall;
                            if (wallElement == null) continue;

                            // Determine if this is a hallway wall
                            var isHallwayWall = IsHallwayWall(doc, wallElement, room, options);
                            var isDemisingWall = IsDemisingWall(doc, wallElement, room, options);

                            // Only create separation lines for hallway walls (or demising if requested)
                            if (!isHallwayWall && !(createForDemising && isDemisingWall)) continue;

                            // Get the segment curve
                            var curve = segment.GetCurve();
                            var start = curve.GetEndPoint(0);
                            var end = curve.GetEndPoint(1);

                            // Calculate offset direction (perpendicular to wall, toward office)
                            var wallDirection = (end - start).Normalize();
                            var perpendicular = new XYZ(-wallDirection.Y, wallDirection.X, 0);

                            // Determine which side is the room side
                            var roomCenter = GetRoomCenter(room);
                            var wallMidpoint = (start + end) / 2;
                            var toRoom = (roomCenter - wallMidpoint).Normalize();

                            // If perpendicular points away from room, flip it
                            if (perpendicular.DotProduct(toRoom) < 0)
                            {
                                perpendicular = perpendicular.Negate();
                            }

                            // Create offset points
                            var offset = hallwayOffset;
                            var offsetStart = start + (perpendicular * offset);
                            var offsetEnd = end + (perpendicular * offset);

                            // Create the room separation line
                            var line = Line.CreateBound(offsetStart, offsetEnd);
                            var modelCurve = doc.Create.NewModelCurve(line, sketchPlane);

                            if (graphicsStyle != null)
                            {
                                modelCurve.LineStyle = graphicsStyle;
                            }

                            createdLines.Add(new
                            {
                                separationLineId = (int)modelCurve.Id.Value,
                                wallId = (int)wallElement.Id.Value,
                                wallType = doc.GetElement(wallElement.GetTypeId())?.Name,
                                isHallwayWall = isHallwayWall,
                                offsetDistance = offset,
                                length = line.Length
                            });
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        roomId = (int)room.Id.Value,
                        roomNumber = room.Number,
                        roomName = room.Name,
                        createdLineCount = createdLines.Count,
                        separationLines = createdLines
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
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

        private static bool IsDemisingWall(Document doc, Wall wall, Room room, SpatialElementBoundaryOptions options)
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

            return adjacentRooms.Any(r => r.Name != null && r.Name.ToUpper().Contains("OFFICE"));
        }

        private static XYZ GetRoomCenter(Room room)
        {
            var locationPoint = room.Location as LocationPoint;
            if (locationPoint != null)
            {
                return locationPoint.Point;
            }

            // Fallback: use bounding box center
            var bbox = room.get_BoundingBox(null);
            if (bbox != null)
            {
                return (bbox.Min + bbox.Max) / 2;
            }

            return XYZ.Zero;
        }
    }
}
