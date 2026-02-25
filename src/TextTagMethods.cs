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
    /// Text notes and tag placement methods for MCP Bridge
    /// </summary>
    public static class TextTagMethods
    {
        /// <summary>
        /// Place a text note
        /// </summary>
        public static string PlaceTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var location = parameters["location"].ToObject<double[]>();
                var text = parameters["text"].ToString();

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Text Note"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(location[0], location[1], location[2]);

                    // Get text note type - with smart defaults based on context
                    ElementId textTypeId;
                    string selectedReason = "default";

                    if (parameters["textTypeId"] != null)
                    {
                        textTypeId = new ElementId(int.Parse(parameters["textTypeId"].ToString()));
                        selectedReason = "user specified";
                    }
                    else
                    {
                        // SMART DEFAULT: Find appropriate text size based on context
                        // textContext: "notes" = 3/32", "label" = 3/16", "title" = 1/4"
                        var textContext = parameters["textContext"]?.ToString()?.ToLower() ?? "notes";

                        // Target sizes in inches (internal Revit units are feet, so divide by 12)
                        double targetSizeInches = textContext switch
                        {
                            "notes" => 0.09375,    // 3/32" - standard for notes
                            "body" => 0.09375,     // 3/32" - alias for notes
                            "label" => 0.1875,     // 3/16" - for labels
                            "title" => 0.25,       // 1/4" - for titles
                            "heading" => 0.25,     // 1/4" - alias for title
                            "small" => 0.0625,     // 1/16" - smallest
                            _ => 0.09375           // Default to 3/32" for notes
                        };

                        // Get all text types and find one matching the target size
                        var textTypes = new FilteredElementCollector(doc)
                            .OfClass(typeof(TextNoteType))
                            .Cast<TextNoteType>()
                            .ToList();

                        TextNoteType matchingType = null;
                        double closestDiff = double.MaxValue;
                        TextNoteType closestType = null;

                        foreach (var tt in textTypes)
                        {
                            var sizeParam = tt.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            if (sizeParam != null)
                            {
                                double sizeInFeet = sizeParam.AsDouble();
                                double sizeInInches = sizeInFeet * 12.0;
                                double diff = Math.Abs(sizeInInches - targetSizeInches);

                                // Exact match (within small tolerance)
                                if (diff < 0.001)
                                {
                                    matchingType = tt;
                                    break;
                                }

                                // Track closest match
                                if (diff < closestDiff)
                                {
                                    closestDiff = diff;
                                    closestType = tt;
                                }
                            }
                        }

                        if (matchingType != null)
                        {
                            textTypeId = matchingType.Id;
                            selectedReason = $"matched {textContext} context ({targetSizeInches * 32}/32\")";
                        }
                        else if (closestType != null && closestDiff < 0.1)
                        {
                            textTypeId = closestType.Id;
                            var closestParam = closestType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            var closestSize = closestParam?.AsDouble() * 12 ?? 0;
                            selectedReason = $"closest to {textContext} context (found {closestSize:F4}\" instead of {targetSizeInches}\")";
                        }
                        else
                        {
                            // Fallback to system default
                            textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                            selectedReason = "system default (no matching size found)";
                        }
                    }

                    // Create text note options
                    var options = new TextNoteOptions
                    {
                        TypeId = textTypeId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    // Set alignment if specified
                    if (parameters["alignment"] != null)
                    {
                        var alignment = parameters["alignment"].ToString().ToLower();
                        options.HorizontalAlignment = alignment switch
                        {
                            "left" => HorizontalTextAlignment.Left,
                            "center" => HorizontalTextAlignment.Center,
                            "right" => HorizontalTextAlignment.Right,
                            _ => HorizontalTextAlignment.Left
                        };
                    }

                    // Set rotation if specified
                    if (parameters["rotation"] != null)
                    {
                        options.Rotation = double.Parse(parameters["rotation"].ToString());
                    }

                    var textNote = TextNote.Create(doc, viewId, point, text, options);

                    trans.Commit();

                    // Get text type info for response
                    var usedTextType = doc.GetElement(textTypeId) as TextNoteType;
                    var textSizeParam = usedTextType?.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    double textSizeInches = (textSizeParam?.AsDouble() ?? 0) * 12.0;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textNoteId = (int)textNote.Id.Value,
                        text = textNote.Text,
                        viewId = (int)viewId.Value,
                        textType = new
                        {
                            id = (int)textTypeId.Value,
                            name = usedTextType?.Name ?? "unknown",
                            sizeInches = Math.Round(textSizeInches, 4),
                            sizeFraction = GetFractionString(textSizeInches),
                            selectionReason = selectedReason
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify text note
        /// </summary>
        public static string ModifyTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var textNoteId = new ElementId(int.Parse(parameters["textNoteId"].ToString()));

                var textNote = doc.GetElement(textNoteId) as TextNote;
                if (textNote == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Text note not found"
                    });
                }

                using (var trans = new Transaction(doc, "Modify Text Note"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modified = new List<string>();

                    // Change text
                    if (parameters["text"] != null)
                    {
                        textNote.Text = parameters["text"].ToString();
                        modified.Add("text");
                    }

                    // Change type
                    if (parameters["textTypeId"] != null)
                    {
                        var newTypeId = new ElementId(int.Parse(parameters["textTypeId"].ToString()));
                        textNote.TextNoteType = doc.GetElement(newTypeId) as TextNoteType;
                        modified.Add("type");
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textNoteId = (int)textNoteId.Value,
                        modified = modified
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a wall tag
        /// </summary>
        public static string PlaceWallTag(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var wallId = new ElementId(int.Parse(parameters["wallId"].ToString()));
                var location = parameters["location"].ToObject<double[]>();

                var view = doc.GetElement(viewId) as View;
                var wall = doc.GetElement(wallId) as Wall;

                if (view == null || wall == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View or wall not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Wall Tag"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(location[0], location[1], location[2]);
                    var reference = new Reference(wall);

                    var tag = IndependentTag.Create(
                        doc,
                        viewId,
                        reference,
                        false,
                        TagMode.TM_ADDBY_CATEGORY,
                        TagOrientation.Horizontal,
                        point);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = (int)tag.Id.Value,
                        wallId = (int)wallId.Value,
                        viewId = (int)viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a door tag
        /// </summary>
        public static string PlaceDoorTag(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var doorId = new ElementId(int.Parse(parameters["doorId"].ToString()));
                var location = parameters["location"].ToObject<double[]>();

                var view = doc.GetElement(viewId) as View;
                var door = doc.GetElement(doorId) as FamilyInstance;

                if (view == null || door == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View or door not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Door Tag"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var point = new XYZ(location[0], location[1], location[2]);
                    var reference = new Reference(door);

                    var tag = IndependentTag.Create(
                        doc,
                        viewId,
                        reference,
                        false,
                        TagMode.TM_ADDBY_CATEGORY,
                        TagOrientation.Horizontal,
                        point);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = (int)tag.Id.Value,
                        doorId = (int)doorId.Value,
                        viewId = (int)viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a leader note
        /// </summary>
        public static string PlaceLeaderNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var text = parameters["text"].ToString();
                var leaderEnd = parameters["leaderEnd"].ToObject<double[]>();
                var textLocation = parameters["textLocation"].ToObject<double[]>();

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Leader Note"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var endPoint = new XYZ(leaderEnd[0], leaderEnd[1], leaderEnd[2]);
                    var textPoint = new XYZ(textLocation[0], textLocation[1], textLocation[2]);

                    // Get text note type
                    var textTypeId = parameters["textTypeId"] != null
                        ? new ElementId(int.Parse(parameters["textTypeId"].ToString()))
                        : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

                    // Create text note with leader
                    var options = new TextNoteOptions
                    {
                        TypeId = textTypeId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    var textNote = TextNote.Create(doc, viewId, textPoint, text, options);

                    // Note: In Revit 2026, leader functionality for text notes has changed
                    // Leaders are now managed differently through the Leaders class
                    // For now, create a simple text note without leader
                    // TODO: Implement new leader API if needed

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textNoteId = (int)textNote.Id.Value,
                        hasLeader = false,
                        viewId = (int)viewId.Value,
                        note = "Leader functionality not implemented in Revit 2026 API"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all text notes in a view
        /// </summary>
        public static string GetTextNotesInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var textNotes = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Select(tn => new
                    {
                        textNoteId = (int)tn.Id.Value,
                        text = tn.Text,
                        typeName = tn.TextNoteType.Name,
                        location = new[] { tn.Coord.X, tn.Coord.Y, tn.Coord.Z },
                        width = tn.Width,
                        height = tn.Height
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    textNoteCount = textNotes.Count,
                    textNotes = textNotes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all text notes on a sheet by checking OwnerViewId property
        /// This works for sheets where FilteredElementCollector(doc, viewId) fails
        /// </summary>
        public static string GetTextNotesOnSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["sheetId"] == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "sheetId is required"
                    });
                }

                var sheetId = new ElementId(int.Parse(parameters["sheetId"].ToString()));

                // Verify it's a sheet
                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element is not a sheet"
                    });
                }

                // Get ALL text notes in the document and filter by OwnerViewId
                var allTextNotes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Where(tn => tn.OwnerViewId.Value == sheetId.Value)
                    .Select(tn => new
                    {
                        textNoteId = (int)tn.Id.Value,
                        text = tn.Text,
                        typeName = tn.TextNoteType.Name,
                        location = new[] { tn.Coord.X, tn.Coord.Y, tn.Coord.Z },
                        width = tn.Width,
                        height = tn.Height,
                        ownerViewId = (int)tn.OwnerViewId.Value
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = (int)sheetId.Value,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    textNoteCount = allTextNotes.Count,
                    textNotes = allTextNotes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all tags in a view
        /// </summary>
        public static string GetTagsInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var tagList = new List<object>();
                var tagsInView = new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .ToList();

                foreach (var tag in tagsInView)
                {
                    var taggedId = -1;
                    try
                    {
                        // Try to get the tagged element reference
                        var refs = tag.GetTaggedReferences();
                        if (refs != null && refs.Count > 0)
                        {
                            var elemId = refs[0].ElementId;
                            if (elemId != ElementId.InvalidElementId)
                            {
                                taggedId = (int)elemId.Value;
                            }
                        }
                    }
                    catch
                    {
                        // If tagged element can't be retrieved, keep -1
                    }

                    var pos = tag.TagHeadPosition;
                    tagList.Add(new
                    {
                        tagId = (int)tag.Id.Value,
                        taggedElementId = taggedId,
                        tagText = tag.TagText,
                        hasLeader = tag.HasLeader,
                        location = new[] { pos.X, pos.Y, pos.Z }
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tagCount = tagList.Count,
                    tags = tagList
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all text note types
        /// </summary>
        public static string GetTextNoteTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var textTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .Select(tt => new
                    {
                        typeId = (int)tt.Id.Value,
                        name = tt.Name,
                        textSize = tt.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0,
                        fontName = tt.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString()
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    textTypeCount = textTypes.Count,
                    textTypes = textTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete text note
        /// </summary>
        public static string DeleteTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var textNoteId = new ElementId(int.Parse(parameters["textNoteId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Text Note"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(textNoteId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textNoteId = (int)textNoteId.Value,
                        message = "Text note deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete tag
        /// </summary>
        public static string DeleteTag(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var tagId = new ElementId(int.Parse(parameters["tagId"].ToString()));

                using (var trans = new Transaction(doc, "Delete Tag"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(tagId);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = (int)tagId.Value,
                        message = "Tag deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Tag all elements by category in a view
        /// </summary>
        public static string TagAllByCategory(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var categoryName = parameters["category"].ToString();

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Tag All By Category"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var taggedCount = 0;
                    var tagIds = new List<int>();

                    // Get category
                    BuiltInCategory builtInCat;
                    if (Enum.TryParse($"OST_{categoryName}", true, out builtInCat))
                    {
                        var elements = new FilteredElementCollector(doc, viewId)
                            .OfCategory(builtInCat)
                            .WhereElementIsNotElementType()
                            .ToList();

                        foreach (var element in elements)
                        {
                            try
                            {
                                var location = (element.Location as LocationPoint)?.Point
                                    ?? ((element.Location as LocationCurve)?.Curve.Evaluate(0.5, true));

                                if (location != null)
                                {
                                    var reference = new Reference(element);
                                    var tag = IndependentTag.Create(
                                        doc,
                                        viewId,
                                        reference,
                                        false,
                                        TagMode.TM_ADDBY_CATEGORY,
                                        TagOrientation.Horizontal,
                                        location);

                                    tagIds.Add((int)tag.Id.Value);
                                    taggedCount++;
                                }
                            }
                            catch
                            {
                                // Skip elements that can't be tagged
                                continue;
                            }
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        taggedCount = taggedCount,
                        tagIds = tagIds,
                        viewId = (int)viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modify tag properties
        /// </summary>
        public static string ModifyTag(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var tagId = new ElementId(int.Parse(parameters["tagId"].ToString()));

                var tag = doc.GetElement(tagId) as IndependentTag;
                if (tag == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Tag not found"
                    });
                }

                using (var trans = new Transaction(doc, "Modify Tag"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var modified = new List<string>();

                    // Change leader
                    if (parameters["hasLeader"] != null)
                    {
                        var hasLeader = bool.Parse(parameters["hasLeader"].ToString());
                        if (hasLeader && !tag.HasLeader)
                        {
                            tag.HasLeader = true;
                            modified.Add("addedLeader");
                        }
                        else if (!hasLeader && tag.HasLeader)
                        {
                            tag.HasLeader = false;
                            modified.Add("removedLeader");
                        }
                    }

                    // Move tag
                    if (parameters["newLocation"] != null)
                    {
                        var loc = parameters["newLocation"].ToObject<double[]>();
                        tag.TagHeadPosition = new XYZ(loc[0], loc[1], loc[2]);
                        modified.Add("location");
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = (int)tagId.Value,
                        modified = modified
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Gets detailed information about a specific tag including position
        /// </summary>
        public static string GetTagInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var tagId = new ElementId(int.Parse(parameters["tagId"].ToString()));

                var tag = doc.GetElement(tagId) as IndependentTag;
                if (tag == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Tag not found"
                    });
                }

                var position = tag.TagHeadPosition;

                // Get tagged element ID
                var refs = tag.GetTaggedReferences();
                var taggedId = refs.Count > 0 ? refs.First().ElementId.Value : -1;

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tag = new
                    {
                        tagId = (int)tag.Id.Value,
                        position = new double[] { position.X, position.Y, position.Z },
                        hasLeader = tag.HasLeader,
                        tagText = tag.TagText,
                        taggedElementId = taggedId
                    }
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Change the type of a tag (e.g., from "Room Tag With Area" to "Room Tag")
        /// </summary>
        public static string ChangeTagType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var tagId = new ElementId(int.Parse(parameters["tagId"].ToString()));
                var newTypeId = parameters["newTypeId"] != null
                    ? new ElementId(int.Parse(parameters["newTypeId"].ToString()))
                    : ElementId.InvalidElementId;
                var newTypeName = parameters["newTypeName"]?.ToString();

                var tag = doc.GetElement(tagId);
                if (tag == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Tag not found"
                    });
                }

                // Find the new type
                ElementId targetTypeId = newTypeId;
                if (targetTypeId == ElementId.InvalidElementId && !string.IsNullOrEmpty(newTypeName))
                {
                    // Search for type by name
                    var tagTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(fs => fs.Category?.Id == tag.Category?.Id)
                        .ToList();

                    var matchingType = tagTypes.FirstOrDefault(t =>
                        t.Name.Equals(newTypeName, StringComparison.OrdinalIgnoreCase) ||
                        t.FamilyName.Equals(newTypeName, StringComparison.OrdinalIgnoreCase) ||
                        $"{t.FamilyName}: {t.Name}".Equals(newTypeName, StringComparison.OrdinalIgnoreCase));

                    if (matchingType != null)
                    {
                        targetTypeId = matchingType.Id;
                    }
                }

                if (targetTypeId == ElementId.InvalidElementId)
                {
                    // Return available types
                    var availableTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(fs => fs.Category?.Id == tag.Category?.Id)
                        .Select(t => new { id = (int)t.Id.Value, name = t.Name, family = t.FamilyName })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Could not find target tag type",
                        availableTypes = availableTypes
                    });
                }

                using (var trans = new Transaction(doc, "Change Tag Type"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var oldTypeId = tag.GetTypeId();
                    tag.ChangeTypeId(targetTypeId);

                    trans.Commit();

                    var newType = doc.GetElement(targetTypeId);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagId = (int)tagId.Value,
                        oldTypeId = (int)oldTypeId.Value,
                        newTypeId = (int)targetTypeId.Value,
                        newTypeName = newType?.Name
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all tags that reference a specific element (to find duplicates)
        /// </summary>
        public static string GetTagsForElement(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var elementId = new ElementId(int.Parse(parameters["elementId"].ToString()));
                var viewId = parameters["viewId"] != null
                    ? new ElementId(int.Parse(parameters["viewId"].ToString()))
                    : ElementId.InvalidElementId;

                var element = doc.GetElement(elementId);
                if (element == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element not found"
                    });
                }

                // Get all tags in the document or specific view
                FilteredElementCollector collector;
                if (viewId != ElementId.InvalidElementId)
                {
                    collector = new FilteredElementCollector(doc, viewId);
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var tags = collector
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .Where(t => {
                        try
                        {
                            var refs = t.GetTaggedReferences();
                            return refs.Any(r => r.ElementId == elementId);
                        }
                        catch { return false; }
                    })
                    .ToList();

                // Also check for RoomTags specifically
                var roomTags = new List<RoomTag>();
                if (element is Room)
                {
                    var roomTagCollector = viewId != ElementId.InvalidElementId
                        ? new FilteredElementCollector(doc, viewId)
                        : new FilteredElementCollector(doc);

                    roomTags = roomTagCollector
                        .OfCategory(BuiltInCategory.OST_RoomTags)
                        .WhereElementIsNotElementType()
                        .Cast<RoomTag>()
                        .Where(t => t.Room?.Id == elementId)
                        .ToList();
                }

                var results = new List<object>();

                foreach (var tag in tags)
                {
                    var tagType = doc.GetElement(tag.GetTypeId());
                    results.Add(new
                    {
                        tagId = (int)tag.Id.Value,
                        tagType = tagType?.Name,
                        tagTypeId = (int)tag.GetTypeId().Value,
                        position = new double[] { tag.TagHeadPosition.X, tag.TagHeadPosition.Y, tag.TagHeadPosition.Z },
                        viewId = (int)tag.OwnerViewId.Value,
                        hasLeader = tag.HasLeader,
                        tagText = tag.TagText,
                        tagCategory = "IndependentTag"
                    });
                }

                foreach (var roomTag in roomTags)
                {
                    var tagType = doc.GetElement(roomTag.GetTypeId());
                    var headPos = roomTag.TagHeadPosition;
                    results.Add(new
                    {
                        tagId = (int)roomTag.Id.Value,
                        tagType = tagType?.Name,
                        tagTypeId = (int)roomTag.GetTypeId().Value,
                        position = new double[] { headPos.X, headPos.Y, headPos.Z },
                        viewId = (int)roomTag.View.Id.Value,
                        hasLeader = roomTag.HasLeader,
                        tagText = roomTag.Room?.Name ?? "",
                        tagCategory = "RoomTag"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = (int)elementId.Value,
                    elementName = element.Name,
                    elementCategory = element.Category?.Name,
                    tagCount = results.Count,
                    hasDuplicates = results.Count > 1,
                    tags = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get available room tag types in the project
        /// </summary>
        public static string GetRoomTagTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var roomTagTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Category?.BuiltInCategory == BuiltInCategory.OST_RoomTags)
                    .ToList();

                var results = roomTagTypes.Select(t => new
                {
                    typeId = (int)t.Id.Value,
                    typeName = t.Name,
                    familyName = t.FamilyName,
                    fullName = $"{t.FamilyName}: {t.Name}"
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = results.Count,
                    roomTagTypes = results
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Place a text note in a section/detail view using view-relative coordinates.
        /// This method properly transforms coordinates for section views where the
        /// standard XYZ coordinates don't work intuitively.
        /// </summary>
        /// <param name="parameters">
        /// Required: viewId, viewX (horizontal position in view), viewY (vertical position in view), text
        /// Optional: textTypeId, textContext, alignment
        /// </param>
        public static string PlaceTextNoteInView(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));
                var viewX = double.Parse(parameters["viewX"].ToString());
                var viewY = double.Parse(parameters["viewY"].ToString());
                var text = parameters["text"].ToString();

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                using (var trans = new Transaction(doc, "Place Text Note In View"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Get the view's coordinate system
                    XYZ point;

                    if (view is ViewSection viewSection)
                    {
                        // For section views, use the view's coordinate system
                        // Get the crop box which defines the view's coordinate system
                        var cropBox = viewSection.CropBox;
                        var transform = cropBox.Transform;

                        // The crop box transform defines the view's local coordinate system:
                        // - Origin is the center of the crop region
                        // - BasisX is the view's right direction
                        // - BasisY is the view's up direction
                        // - BasisZ is the view direction (out of the view)

                        // Transform view-relative coordinates to world coordinates
                        // viewX maps to BasisX (horizontal in view)
                        // viewY maps to BasisY (vertical in view)
                        point = transform.Origin +
                                transform.BasisX * viewX +
                                transform.BasisY * viewY;
                    }
                    else if (view is ViewDrafting)
                    {
                        // Drafting views use simple XY coordinates with Z=0
                        point = new XYZ(viewX, viewY, 0);
                    }
                    else
                    {
                        // For other views (plans, etc.), use XY with view's associated level
                        // or just use the coordinates directly
                        point = new XYZ(viewX, viewY, 0);
                    }

                    // Get text note type
                    ElementId textTypeId;
                    string selectedReason = "default";

                    if (parameters["textTypeId"] != null)
                    {
                        textTypeId = new ElementId(int.Parse(parameters["textTypeId"].ToString()));
                        selectedReason = "user specified";
                    }
                    else
                    {
                        var textContext = parameters["textContext"]?.ToString()?.ToLower() ?? "notes";
                        double targetSizeInches = textContext switch
                        {
                            "notes" => 0.09375,
                            "body" => 0.09375,
                            "label" => 0.1875,
                            "title" => 0.25,
                            "heading" => 0.25,
                            "small" => 0.0625,
                            _ => 0.09375
                        };

                        var textTypes = new FilteredElementCollector(doc)
                            .OfClass(typeof(TextNoteType))
                            .Cast<TextNoteType>()
                            .ToList();

                        TextNoteType matchingType = null;
                        double closestDiff = double.MaxValue;
                        TextNoteType closestType = null;

                        foreach (var tt in textTypes)
                        {
                            var sizeParam = tt.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            if (sizeParam != null)
                            {
                                double sizeInFeet = sizeParam.AsDouble();
                                double sizeInInches = sizeInFeet * 12.0;
                                double diff = Math.Abs(sizeInInches - targetSizeInches);

                                if (diff < 0.001)
                                {
                                    matchingType = tt;
                                    break;
                                }

                                if (diff < closestDiff)
                                {
                                    closestDiff = diff;
                                    closestType = tt;
                                }
                            }
                        }

                        if (matchingType != null)
                        {
                            textTypeId = matchingType.Id;
                            selectedReason = $"matched {textContext} context ({targetSizeInches * 32}/32\")";
                        }
                        else if (closestType != null && closestDiff < 0.1)
                        {
                            textTypeId = closestType.Id;
                            selectedReason = "closest size match";
                        }
                        else
                        {
                            textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                            selectedReason = "system default";
                        }
                    }

                    var options = new TextNoteOptions
                    {
                        TypeId = textTypeId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    if (parameters["alignment"] != null)
                    {
                        var alignment = parameters["alignment"].ToString().ToLower();
                        options.HorizontalAlignment = alignment switch
                        {
                            "left" => HorizontalTextAlignment.Left,
                            "center" => HorizontalTextAlignment.Center,
                            "right" => HorizontalTextAlignment.Right,
                            _ => HorizontalTextAlignment.Left
                        };
                    }

                    var textNote = TextNote.Create(doc, viewId, point, text, options);

                    trans.Commit();

                    var usedTextType = doc.GetElement(textTypeId) as TextNoteType;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textNoteId = (int)textNote.Id.Value,
                        text = textNote.Text,
                        viewId = (int)viewId.Value,
                        viewType = view.GetType().Name,
                        placedAt = new { x = point.X, y = point.Y, z = point.Z },
                        inputCoords = new { viewX = viewX, viewY = viewY },
                        textType = new
                        {
                            id = (int)textTypeId.Value,
                            name = usedTextType?.Name ?? "unknown",
                            selectionReason = selectedReason
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get the view coordinate system bounds for placing annotations.
        /// Returns the min/max coordinates in view-relative space.
        /// </summary>
        public static string GetViewAnnotationBounds(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var viewId = new ElementId(int.Parse(parameters["viewId"].ToString()));

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View not found"
                    });
                }

                if (view is ViewSection viewSection)
                {
                    var cropBox = viewSection.CropBox;
                    var transform = cropBox.Transform;

                    // The crop box min/max are in the view's local coordinate system
                    // relative to the transform origin
                    var min = cropBox.Min;
                    var max = cropBox.Max;

                    // Calculate the extents in view-relative coordinates
                    // These represent the visible area in the section view
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        viewType = "Section",
                        bounds = new
                        {
                            minX = min.X,
                            minY = min.Y,
                            maxX = max.X,
                            maxY = max.Y,
                            width = max.X - min.X,
                            height = max.Y - min.Y
                        },
                        transform = new
                        {
                            originX = transform.Origin.X,
                            originY = transform.Origin.Y,
                            originZ = transform.Origin.Z,
                            rightX = transform.BasisX.X,
                            rightY = transform.BasisX.Y,
                            rightZ = transform.BasisX.Z,
                            upX = transform.BasisY.X,
                            upY = transform.BasisY.Y,
                            upZ = transform.BasisY.Z
                        },
                        usage = "Use minX/maxX for horizontal position (viewX) and minY/maxY for vertical position (viewY) in placeTextNoteInView"
                    });
                }
                else
                {
                    // For non-section views, return simplified bounds
                    var outline = view.Outline;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = (int)viewId.Value,
                        viewName = view.Name,
                        viewType = view.GetType().Name,
                        bounds = new
                        {
                            minX = outline.Min.U,
                            minY = outline.Min.V,
                            maxX = outline.Max.U,
                            maxY = outline.Max.V,
                            width = outline.Max.U - outline.Min.U,
                            height = outline.Max.V - outline.Min.V
                        },
                        usage = "Use bounds for coordinate reference in view"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #region Helper Methods

        /// <summary>
        /// Convert inches to fractional string (e.g., 0.09375 -> "3/32\"")
        /// </summary>
        private static string GetFractionString(double inches)
        {
            // Common architectural text sizes
            if (Math.Abs(inches - 0.0625) < 0.001) return "1/16\"";
            if (Math.Abs(inches - 0.09375) < 0.001) return "3/32\"";
            if (Math.Abs(inches - 0.125) < 0.001) return "1/8\"";
            if (Math.Abs(inches - 0.1875) < 0.001) return "3/16\"";
            if (Math.Abs(inches - 0.25) < 0.001) return "1/4\"";
            if (Math.Abs(inches - 0.3125) < 0.001) return "5/16\"";
            if (Math.Abs(inches - 0.375) < 0.001) return "3/8\"";
            if (Math.Abs(inches - 0.5) < 0.001) return "1/2\"";

            // For other sizes, return decimal
            return $"{Math.Round(inches, 4)}\"";
        }

        #endregion
    }
}
