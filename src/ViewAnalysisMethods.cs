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
    /// Deep view analysis methods for AI understanding of Revit model state
    /// </summary>
    public static class ViewAnalysisMethods
    {
        /// <summary>
        /// Comprehensive view snapshot - captures everything visible in the current view
        /// Returns detailed information about all elements, their properties, and relationships
        /// </summary>
        public static string GetViewSnapshot(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var activeView = doc.ActiveView;

                var includeGeometry = parameters["includeGeometry"]?.ToObject<bool>() ?? true;
                var includeParameters = parameters["includeParameters"]?.ToObject<bool>() ?? true;
                var includeRelationships = parameters["includeRelationships"]?.ToObject<bool>() ?? true;
                var maxElements = parameters["maxElements"]?.ToObject<int>() ?? 1000;

                // Get all visible elements in the view
                var collector = new FilteredElementCollector(doc, activeView.Id)
                    .WhereElementIsNotElementType();

                var elements = new List<object>();
                var elementCount = 0;

                foreach (var element in collector)
                {
                    if (elementCount >= maxElements) break;

                    try
                    {
                        var elementData = new Dictionary<string, object>
                        {
                            ["elementId"] = (int)element.Id.Value,
                            ["category"] = element.Category?.Name ?? "Unknown",
                            ["name"] = element.Name,
                            ["type"] = doc.GetElement(element.GetTypeId())?.Name
                        };

                        // Add geometry information
                        if (includeGeometry)
                        {
                            var bbox = element.get_BoundingBox(activeView);
                            if (bbox != null)
                            {
                                elementData["boundingBox"] = new
                                {
                                    minX = bbox.Min.X,
                                    minY = bbox.Min.Y,
                                    minZ = bbox.Min.Z,
                                    maxX = bbox.Max.X,
                                    maxY = bbox.Max.Y,
                                    maxZ = bbox.Max.Z,
                                    centerX = (bbox.Min.X + bbox.Max.X) / 2,
                                    centerY = (bbox.Min.Y + bbox.Max.Y) / 2,
                                    centerZ = (bbox.Min.Z + bbox.Max.Z) / 2
                                };
                            }

                            // Get location
                            var location = element.Location;
                            if (location is LocationPoint locPoint)
                            {
                                elementData["location"] = new
                                {
                                    type = "Point",
                                    x = locPoint.Point.X,
                                    y = locPoint.Point.Y,
                                    z = locPoint.Point.Z
                                };
                            }
                            else if (location is LocationCurve locCurve)
                            {
                                var curve = locCurve.Curve;
                                elementData["location"] = new
                                {
                                    type = "Curve",
                                    startX = curve.GetEndPoint(0).X,
                                    startY = curve.GetEndPoint(0).Y,
                                    startZ = curve.GetEndPoint(0).Z,
                                    endX = curve.GetEndPoint(1).X,
                                    endY = curve.GetEndPoint(1).Y,
                                    endZ = curve.GetEndPoint(1).Z,
                                    length = curve.Length
                                };
                            }
                        }

                        // Add parameters
                        if (includeParameters)
                        {
                            var parameters_list = new List<object>();
                            foreach (Parameter param in element.Parameters)
                            {
                                if (param.Definition == null || !param.HasValue) continue;

                                parameters_list.Add(new
                                {
                                    name = param.Definition.Name,
                                    value = GetParameterValueAsString(param),
                                    type = param.StorageType.ToString(),
                                    isReadOnly = param.IsReadOnly
                                });
                            }
                            elementData["parameters"] = parameters_list;
                        }

                        // Add element-specific details
                        if (element is Wall wall)
                        {
                            elementData["elementType"] = "Wall";
                            elementData["wallDetails"] = new
                            {
                                width = (doc.GetElement(wall.GetTypeId()) as WallType)?.Width ?? 0,
                                height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                                function = (doc.GetElement(wall.GetTypeId()) as WallType)?.Function.ToString()
                            };
                        }
                        else if (element is Room room)
                        {
                            elementData["elementType"] = "Room";
                            elementData["roomDetails"] = new
                            {
                                number = room.Number,
                                area = room.Area,
                                perimeter = room.Perimeter,
                                volume = room.Volume,
                                level = doc.GetElement(room.LevelId)?.Name,
                                department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString()
                            };
                        }
                        else if (element is FamilyInstance famInst)
                        {
                            elementData["elementType"] = "FamilyInstance";
                            elementData["familyDetails"] = new
                            {
                                family = famInst.Symbol?.FamilyName,
                                familyType = famInst.Symbol?.Name,
                                host = famInst.Host?.Id.Value
                            };
                        }
                        else if (element is FilledRegion filledRegion)
                        {
                            elementData["elementType"] = "FilledRegion";
                            elementData["filledRegionDetails"] = new
                            {
                                typeId = (int)filledRegion.GetTypeId().Value
                            };
                        }

                        elements.Add(elementData);
                        elementCount++;
                    }
                    catch (Exception ex)
                    {
                        // Skip elements that cause errors
                        elements.Add(new
                        {
                            elementId = (int)element.Id.Value,
                            error = $"Failed to analyze: {ex.Message}"
                        });
                    }
                }

                // Group elements by category for easier analysis
                var elementsByCategory = elements
                    .GroupBy(e => ((Dictionary<string, object>)e).ContainsKey("category")
                        ? ((Dictionary<string, object>)e)["category"]
                        : "Unknown")
                    .ToDictionary(
                        g => g.Key,
                        g => g.Count()
                    );

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewInfo = new
                    {
                        viewId = (int)activeView.Id.Value,
                        viewName = activeView.Name,
                        viewType = activeView.ViewType.ToString(),
                        scale = activeView.Scale,
                        detailLevel = activeView.DetailLevel.ToString()
                    },
                    elementCount = elementCount,
                    elementsByCategory = elementsByCategory,
                    elements = elements
                }, Formatting.None);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        private static string GetParameterValueAsString(Parameter param)
        {
            if (!param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsDouble().ToString();
                case StorageType.ElementId:
                    return param.AsElementId().Value.ToString();
                default:
                    return param.AsValueString();
            }
        }
    }
}
