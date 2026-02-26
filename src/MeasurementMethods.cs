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
    /// Measurement and distance calculation methods for MCP Bridge
    /// Enables autonomous spatial analysis and verification
    /// </summary>
    public static class MeasurementMethods
    {
        /// <summary>
        /// Parse a point from JSON - accepts both object {x,y,z} and array [x,y,z] formats
        /// </summary>
        private static XYZ ParsePoint(JToken pointToken)
        {
            if (pointToken == null)
                throw new ArgumentException("Point is required");

            if (pointToken.Type == JTokenType.Object)
            {
                var obj = pointToken as JObject;
                var x = obj["x"]?.ToObject<double>() ?? 0;
                var y = obj["y"]?.ToObject<double>() ?? 0;
                var z = obj["z"]?.ToObject<double>() ?? 0;
                return new XYZ(x, y, z);
            }

            if (pointToken.Type == JTokenType.Array)
            {
                var arr = pointToken.ToObject<double[]>();
                if (arr.Length >= 3)
                    return new XYZ(arr[0], arr[1], arr[2]);
                if (arr.Length == 2)
                    return new XYZ(arr[0], arr[1], 0);
                throw new ArgumentException("Point array must have at least 2 elements");
            }

            throw new ArgumentException($"Point must be object {{x,y,z}} or array [x,y,z], got {pointToken.Type}");
        }

        /// <summary>
        /// Measure distance between two points
        /// Parameters: point1 ([x,y,z] or {x,y,z}), point2 ([x,y,z] or {x,y,z})
        /// </summary>
        [MCPMethod("measureDistance", Category = "Measurement", Description = "Measure the distance between two XYZ points")]
        public static string MeasureDistance(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var point1 = ParsePoint(parameters["point1"]);
                var point2 = ParsePoint(parameters["point2"]);

                var distance = point1.DistanceTo(point2);
                var distanceXY = Math.Sqrt(Math.Pow(point2.X - point1.X, 2) + Math.Pow(point2.Y - point1.Y, 2));
                var distanceZ = Math.Abs(point2.Z - point1.Z);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    distance = Math.Round(distance, 4),
                    distanceFeetInches = FormatFeetInches(distance),
                    distanceXY = Math.Round(distanceXY, 4),
                    distanceXYFeetInches = FormatFeetInches(distanceXY),
                    distanceZ = Math.Round(distanceZ, 4),
                    deltaX = Math.Round(point2.X - point1.X, 4),
                    deltaY = Math.Round(point2.Y - point1.Y, 4),
                    deltaZ = Math.Round(point2.Z - point1.Z, 4),
                    point1 = new { x = point1.X, y = point1.Y, z = point1.Z },
                    point2 = new { x = point2.X, y = point2.Y, z = point2.Z }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Measure distance between two elements (center to center or closest points)
        /// Parameters: elementId1, elementId2, method ("center" | "closest" | "boundingBox")
        /// </summary>
        [MCPMethod("measureBetweenElements", Category = "Measurement", Description = "Measure the distance between two Revit elements")]
        public static string MeasureBetweenElements(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementId1 = new ElementId(parameters["elementId1"].Value<int>());
                var elementId2 = new ElementId(parameters["elementId2"].Value<int>());
                var method = parameters["method"]?.ToString() ?? "center";

                var element1 = doc.GetElement(elementId1);
                var element2 = doc.GetElement(elementId2);

                if (element1 == null || element2 == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "One or both elements not found"
                    });
                }

                XYZ point1, point2;
                string measurementType;

                if (method == "boundingBox")
                {
                    var bb1 = element1.get_BoundingBox(null);
                    var bb2 = element2.get_BoundingBox(null);

                    if (bb1 == null || bb2 == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Could not get bounding box for one or both elements"
                        });
                    }

                    point1 = (bb1.Min + bb1.Max) / 2;
                    point2 = (bb2.Min + bb2.Max) / 2;
                    measurementType = "boundingBoxCenter";
                }
                else // center (using location)
                {
                    point1 = GetElementCenter(element1);
                    point2 = GetElementCenter(element2);
                    measurementType = "locationCenter";
                }

                var distance = point1.DistanceTo(point2);
                var distanceXY = Math.Sqrt(Math.Pow(point2.X - point1.X, 2) + Math.Pow(point2.Y - point1.Y, 2));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    distance = Math.Round(distance, 4),
                    distanceFeetInches = FormatFeetInches(distance),
                    distanceXY = Math.Round(distanceXY, 4),
                    distanceXYFeetInches = FormatFeetInches(distanceXY),
                    measurementType = measurementType,
                    element1 = new
                    {
                        id = elementId1.Value,
                        category = element1.Category?.Name,
                        point = new { x = point1.X, y = point1.Y, z = point1.Z }
                    },
                    element2 = new
                    {
                        id = elementId2.Value,
                        category = element2.Category?.Name,
                        point = new { x = point2.X, y = point2.Y, z = point2.Z }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get room dimensions (width, length, area, perimeter)
        /// Parameters: roomId
        /// </summary>
        [MCPMethod("getRoomDimensions", Category = "Measurement", Description = "Get room dimensions including width, length, area, and perimeter")]
        public static string GetRoomDimensions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roomId = new ElementId(parameters["roomId"].Value<int>());
                var room = doc.GetElement(roomId) as Room;

                if (room == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Room not found"
                    });
                }

                // Get room boundary
                var options = new SpatialElementBoundaryOptions();
                var boundaries = room.GetBoundarySegments(options);

                if (boundaries == null || boundaries.Count == 0)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Room has no boundary"
                    });
                }

                // Get bounding box for width/length approximation
                var bb = room.get_BoundingBox(null);
                double width = 0, length = 0;
                XYZ minPoint = null, maxPoint = null;

                if (bb != null)
                {
                    minPoint = bb.Min;
                    maxPoint = bb.Max;
                    var deltaX = Math.Abs(bb.Max.X - bb.Min.X);
                    var deltaY = Math.Abs(bb.Max.Y - bb.Min.Y);
                    width = Math.Min(deltaX, deltaY);
                    length = Math.Max(deltaX, deltaY);
                }

                // Calculate perimeter from boundary segments
                double perimeter = 0;
                var segmentDetails = new List<object>();

                foreach (var boundaryList in boundaries)
                {
                    foreach (var segment in boundaryList)
                    {
                        var curve = segment.GetCurve();
                        var segLength = curve.Length;
                        perimeter += segLength;

                        var startPt = curve.GetEndPoint(0);
                        var endPt = curve.GetEndPoint(1);

                        segmentDetails.Add(new
                        {
                            length = Math.Round(segLength, 4),
                            lengthFeetInches = FormatFeetInches(segLength),
                            start = new { x = Math.Round(startPt.X, 2), y = Math.Round(startPt.Y, 2) },
                            end = new { x = Math.Round(endPt.X, 2), y = Math.Round(endPt.Y, 2) }
                        });
                    }
                }

                var area = room.Area;
                var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                var roomNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    roomId = roomId.Value,
                    roomName = roomName,
                    roomNumber = roomNumber,
                    area = Math.Round(area, 2),
                    areaSF = $"{Math.Round(area, 0)} SF",
                    perimeter = Math.Round(perimeter, 4),
                    perimeterFeetInches = FormatFeetInches(perimeter),
                    approximateWidth = Math.Round(width, 4),
                    approximateWidthFeetInches = FormatFeetInches(width),
                    approximateLength = Math.Round(length, 4),
                    approximateLengthFeetInches = FormatFeetInches(length),
                    boundingBox = bb != null ? new
                    {
                        min = new { x = minPoint.X, y = minPoint.Y, z = minPoint.Z },
                        max = new { x = maxPoint.X, y = maxPoint.Y, z = maxPoint.Z }
                    } : null,
                    boundarySegmentCount = segmentDetails.Count,
                    boundarySegments = segmentDetails
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Measure perpendicular distance from a point to a wall
        /// Parameters: wallId, point ([x,y,z] or {x,y,z})
        /// </summary>
        [MCPMethod("measurePerpendicularToWall", Category = "Measurement", Description = "Measure the perpendicular distance from a point to a wall")]
        public static string MeasurePerpendicularToWall(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var wallId = new ElementId(parameters["wallId"].Value<int>());
                var point = ParsePoint(parameters["point"]);

                var wall = doc.GetElement(wallId) as Wall;

                if (wall == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall not found"
                    });
                }

                var locationCurve = wall.Location as LocationCurve;
                if (locationCurve == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Wall has no location curve"
                    });
                }

                var curve = locationCurve.Curve;
                var result = curve.Project(point);

                if (result == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not project point to wall"
                    });
                }

                var closestPointOnWall = result.XYZPoint;
                var distance = result.Distance;

                // Calculate perpendicular distance (2D, ignoring Z)
                var distance2D = Math.Sqrt(
                    Math.Pow(closestPointOnWall.X - point.X, 2) +
                    Math.Pow(closestPointOnWall.Y - point.Y, 2));

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    distance = Math.Round(distance, 4),
                    distanceFeetInches = FormatFeetInches(distance),
                    distance2D = Math.Round(distance2D, 4),
                    distance2DFeetInches = FormatFeetInches(distance2D),
                    inputPoint = new { x = point.X, y = point.Y, z = point.Z },
                    closestPointOnWall = new { x = closestPointOnWall.X, y = closestPointOnWall.Y, z = closestPointOnWall.Z },
                    wallId = wallId.Value,
                    wallWidth = wall.Width
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Measure corridor width by finding parallel walls
        /// Parameters: point ([x,y,z] or {x,y,z}) - a point inside the corridor
        /// </summary>
        [MCPMethod("measureCorridorWidth", Category = "Measurement", Description = "Measure corridor width by finding parallel walls around a point")]
        public static string MeasureCorridorWidth(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var point = ParsePoint(parameters["point"]);
                var searchRadius = parameters["searchRadius"]?.Value<double>() ?? 20.0;

                // Get all walls near the point
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType();

                var nearbyWalls = new List<(Wall wall, double distance, XYZ closestPoint, XYZ direction)>();

                foreach (Wall wall in collector)
                {
                    var locationCurve = wall.Location as LocationCurve;
                    if (locationCurve == null) continue;

                    var curve = locationCurve.Curve;
                    var result = curve.Project(point);

                    if (result != null && result.Distance < searchRadius)
                    {
                        // Get wall direction
                        var startPt = curve.GetEndPoint(0);
                        var endPt = curve.GetEndPoint(1);
                        var direction = (endPt - startPt).Normalize();

                        nearbyWalls.Add((wall, result.Distance, result.XYZPoint, direction));
                    }
                }

                if (nearbyWalls.Count < 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not find two walls near the point",
                        wallsFound = nearbyWalls.Count
                    });
                }

                // Sort by distance and find parallel pairs
                nearbyWalls = nearbyWalls.OrderBy(w => w.distance).ToList();

                // Find the two closest walls that are roughly parallel
                Wall wall1 = null, wall2 = null;
                double corridorWidth = 0;
                XYZ wall1Point = null, wall2Point = null;

                for (int i = 0; i < nearbyWalls.Count && wall1 == null; i++)
                {
                    for (int j = i + 1; j < nearbyWalls.Count && wall2 == null; j++)
                    {
                        // Check if walls are parallel (dot product of directions close to 1 or -1)
                        var dot = Math.Abs(nearbyWalls[i].direction.DotProduct(nearbyWalls[j].direction));
                        if (dot > 0.9) // Nearly parallel
                        {
                            wall1 = nearbyWalls[i].wall;
                            wall2 = nearbyWalls[j].wall;
                            wall1Point = nearbyWalls[i].closestPoint;
                            wall2Point = nearbyWalls[j].closestPoint;

                            // Calculate corridor width as distance between the two closest points
                            corridorWidth = wall1Point.DistanceTo(wall2Point);
                            break;
                        }
                    }
                }

                if (wall1 == null || wall2 == null)
                {
                    // Fall back to two closest walls
                    wall1 = nearbyWalls[0].wall;
                    wall2 = nearbyWalls[1].wall;
                    wall1Point = nearbyWalls[0].closestPoint;
                    wall2Point = nearbyWalls[1].closestPoint;
                    corridorWidth = nearbyWalls[0].distance + nearbyWalls[1].distance;
                }

                // Adjust for wall thickness (corridor width is between wall faces, not centerlines)
                var wallThicknessAdjustment = (wall1.Width + wall2.Width) / 2;
                var clearWidth = corridorWidth - wallThicknessAdjustment;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    corridorWidth = Math.Round(corridorWidth, 4),
                    corridorWidthFeetInches = FormatFeetInches(corridorWidth),
                    clearWidth = Math.Round(clearWidth, 4),
                    clearWidthFeetInches = FormatFeetInches(clearWidth),
                    clearWidthInches = Math.Round(clearWidth * 12, 1),
                    meetsCodeMinimum44in = clearWidth * 12 >= 44,
                    inputPoint = new { x = point.X, y = point.Y, z = point.Z },
                    wall1 = new
                    {
                        id = wall1.Id.Value,
                        width = wall1.Width,
                        closestPoint = new { x = wall1Point.X, y = wall1Point.Y }
                    },
                    wall2 = new
                    {
                        id = wall2.Id.Value,
                        width = wall2.Width,
                        closestPoint = new { x = wall2Point.X, y = wall2Point.Y }
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get element center point
        /// </summary>
        private static XYZ GetElementCenter(Element element)
        {
            // Try location point first
            if (element.Location is LocationPoint locPoint)
            {
                return locPoint.Point;
            }

            // Try location curve (midpoint)
            if (element.Location is LocationCurve locCurve)
            {
                var curve = locCurve.Curve;
                return (curve.GetEndPoint(0) + curve.GetEndPoint(1)) / 2;
            }

            // Fall back to bounding box center
            var bb = element.get_BoundingBox(null);
            if (bb != null)
            {
                return (bb.Min + bb.Max) / 2;
            }

            throw new Exception("Could not determine element center");
        }

        /// <summary>
        /// Format feet as feet and inches string
        /// </summary>
        private static string FormatFeetInches(double feet)
        {
            var wholeFeet = (int)Math.Floor(feet);
            var inches = (feet - wholeFeet) * 12;
            var wholeInches = (int)Math.Round(inches);

            if (wholeInches == 12)
            {
                wholeFeet++;
                wholeInches = 0;
            }

            if (wholeInches == 0)
                return $"{wholeFeet}'-0\"";
            return $"{wholeFeet}'-{wholeInches}\"";
        }
    }
}
