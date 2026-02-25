using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Rich Text Note methods - multi-color text annotations
    /// Supported: Sheets, Legends, Drafting Views | Single-line, no leaders
    /// </summary>
    public static class RichTextMethods
    {
        // Schema GUID for storing rich text metadata
        private static readonly Guid RichTextSchemaGuid = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
        private const string SchemaName = "RichTextNoteData";
        private const string FieldName = "SpanData";

        #region Data Classes

        /// <summary>
        /// Represents a single colored text span
        /// </summary>
        public class TextSpan
        {
            public string Text { get; set; }
            public string Color { get; set; } // Hex format: "#RRGGBB"
        }

        /// <summary>
        /// Rich text note metadata stored in Extensible Storage
        /// </summary>
        public class RichTextData
        {
            public string Version { get; set; } = "1.0";
            public List<TextSpan> Spans { get; set; } = new List<TextSpan>();
            public int BaseTypeId { get; set; }
            public string Alignment { get; set; } = "left";
            public double[] AnchorPoint { get; set; }
            public int ViewId { get; set; }
        }

        #endregion

        #region Schema Management

        /// <summary>
        /// Get or create the Extensible Storage schema for rich text data
        /// </summary>
        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(RichTextSchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(RichTextSchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldName, typeof(string));

            return builder.Finish();
        }

        /// <summary>
        /// Store rich text data on an element
        /// </summary>
        private static void StoreRichTextData(Element element, RichTextData data)
        {
            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set<string>(FieldName, JsonConvert.SerializeObject(data));
            element.SetEntity(entity);
        }

        /// <summary>
        /// Retrieve rich text data from an element
        /// </summary>
        private static RichTextData GetRichTextData(Element element)
        {
            var schema = GetOrCreateSchema();
            var entity = element.GetEntity(schema);
            if (!entity.IsValid()) return null;

            var json = entity.Get<string>(FieldName);
            return JsonConvert.DeserializeObject<RichTextData>(json);
        }

        #endregion

        #region TextNoteType Color Management

        /// <summary>
        /// Get or create a TextNoteType with a specific color
        /// </summary>
        public static string GetOrCreateColoredTextType(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var hexColor = parameters["color"]?.ToString() ?? "#000000";
                var baseTypeId = parameters["baseTypeId"]?.Value<int>();

                // Parse hex color
                var color = ParseHexColor(hexColor);
                var typeName = $"RichText_{hexColor.TrimStart('#')}";

                // Check if type already exists
                var existingType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault(t => t.Name == typeName);

                if (existingType != null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textTypeId = existingType.Id.Value,
                        typeName = typeName,
                        created = false
                    });
                }

                // Get base type to duplicate
                TextNoteType baseType = null;
                if (baseTypeId.HasValue)
                {
                    baseType = doc.GetElement(new ElementId(baseTypeId.Value)) as TextNoteType;
                }

                if (baseType == null)
                {
                    // Use default text note type
                    var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    baseType = doc.GetElement(defaultTypeId) as TextNoteType;
                }

                if (baseType == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No base TextNoteType found"
                    });
                }

                using (var trans = new Transaction(doc, "Create Colored TextNoteType"))
                {
                    trans.Start();

                    // Duplicate the base type
                    var newType = baseType.Duplicate(typeName) as TextNoteType;

                    // Set the color
                    var colorParam = newType.get_Parameter(BuiltInParameter.LINE_COLOR);
                    if (colorParam != null && !colorParam.IsReadOnly)
                    {
                        // Revit stores color as RGB integer: R + G*256 + B*65536
                        int colorInt = color.Red + (color.Green << 8) + (color.Blue << 16);
                        colorParam.Set(colorInt);
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        textTypeId = newType.Id.Value,
                        typeName = typeName,
                        created = true,
                        color = hexColor
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Parse hex color string to Revit Color
        /// </summary>
        private static Color ParseHexColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) hex = "000000";

            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);

            return new Color(r, g, b);
        }

        /// <summary>
        /// Get all colored text types created for rich text
        /// </summary>
        public static string GetColoredTextTypes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var richTextTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .Where(t => t.Name.StartsWith("RichText_"))
                    .Select(t =>
                    {
                        var colorParam = t.get_Parameter(BuiltInParameter.LINE_COLOR);
                        var colorInt = colorParam?.AsInteger() ?? 0;
                        var r = colorInt & 0xFF;
                        var g = (colorInt >> 8) & 0xFF;
                        var b = (colorInt >> 16) & 0xFF;

                        return new
                        {
                            typeId = t.Id.Value,
                            name = t.Name,
                            color = $"#{r:X2}{g:X2}{b:X2}"
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = richTextTypes.Count,
                    types = richTextTypes
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region Rich Text Note CRUD

        /// <summary>
        /// Create a rich text note with multiple colored spans
        /// </summary>
        public static string CreateRichTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Parse parameters
                var viewId = new ElementId(parameters["viewId"].Value<int>());
                var location = parameters["location"].ToObject<double[]>();
                var spansJson = parameters["spans"].ToString();
                var spans = JsonConvert.DeserializeObject<List<TextSpan>>(spansJson);
                var baseTypeId = parameters["baseTypeId"]?.Value<int>();
                var alignment = parameters["alignment"]?.ToString() ?? "left";
                var gapFactor = parameters["gapFactor"]?.Value<double>() ?? 0.0; // Extra gap between spans

                var view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                // Verify it's a sheet or legend (supported view types)
                bool isSheet = view is ViewSheet;
                bool isLegend = view.ViewType == ViewType.Legend;
                bool isDraftingView = view.ViewType == ViewType.DraftingView;

                if (!isSheet && !isLegend && !isDraftingView)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View type '{view.ViewType}' not supported. Supported types: Sheet, Legend, DraftingView."
                    });
                }

                if (spans == null || spans.Count == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "No spans provided" });
                }

                using (var trans = new Transaction(doc, "Create Rich Text Note"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var anchorPoint = new XYZ(location[0], location[1], location[2]);
                    var currentX = anchorPoint.X;
                    var textNoteIds = new List<ElementId>();

                    // Get or use default base type
                    ElementId baseTextTypeId;
                    if (baseTypeId.HasValue)
                    {
                        baseTextTypeId = new ElementId(baseTypeId.Value);
                    }
                    else
                    {
                        baseTextTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    }

                    // Create each span
                    foreach (var span in spans)
                    {
                        if (string.IsNullOrEmpty(span.Text)) continue;

                        // Get or create colored type for this span
                        var coloredTypeId = GetOrCreateColoredTypeInternal(doc, span.Color, baseTextTypeId);

                        // Create text note options
                        var options = new TextNoteOptions
                        {
                            TypeId = coloredTypeId,
                            HorizontalAlignment = HorizontalTextAlignment.Left
                        };

                        // Place text note at current position
                        var point = new XYZ(currentX, anchorPoint.Y, anchorPoint.Z);
                        var textNote = TextNote.Create(doc, viewId, point, span.Text, options);
                        textNoteIds.Add(textNote.Id);

                        // Get bounding box to calculate width
                        var bbox = textNote.get_BoundingBox(view);
                        if (bbox != null)
                        {
                            var width = bbox.Max.X - bbox.Min.X;
                            currentX += width + gapFactor;
                        }
                        else
                        {
                            // Fallback: estimate width based on character count
                            currentX += span.Text.Length * 0.05; // Rough estimate
                        }
                    }

                    // Group the text notes
                    Group group = null;
                    if (textNoteIds.Count > 1)
                    {
                        group = doc.Create.NewGroup(textNoteIds);
                        group.GroupType.Name = $"RichText_{DateTime.Now:yyyyMMdd_HHmmss}";
                    }

                    // Store metadata
                    var richTextData = new RichTextData
                    {
                        Spans = spans,
                        BaseTypeId = (int)baseTextTypeId.Value,
                        Alignment = alignment,
                        AnchorPoint = location,
                        ViewId = (int)viewId.Value
                    };

                    // Store on group or first text note
                    var storageElement = group != null ? (Element)group : doc.GetElement(textNoteIds[0]);
                    StoreRichTextData(storageElement, richTextData);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        groupId = group?.Id.Value,
                        textNoteIds = textNoteIds.Select(id => id.Value).ToList(),
                        spanCount = spans.Count,
                        viewId = viewId.Value
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Internal helper to get or create colored text type
        /// </summary>
        private static ElementId GetOrCreateColoredTypeInternal(Document doc, string hexColor, ElementId baseTypeId)
        {
            hexColor = hexColor ?? "#000000";
            hexColor = hexColor.TrimStart('#');
            var typeName = $"RichText_{hexColor}";

            // Check if exists
            var existingType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name == typeName);

            if (existingType != null)
            {
                return existingType.Id;
            }

            // Create new type
            var baseType = doc.GetElement(baseTypeId) as TextNoteType;
            if (baseType == null)
            {
                return baseTypeId; // Fallback to base
            }

            var newType = baseType.Duplicate(typeName) as TextNoteType;

            // Set color
            var color = ParseHexColor(hexColor);
            var colorParam = newType.get_Parameter(BuiltInParameter.LINE_COLOR);
            if (colorParam != null && !colorParam.IsReadOnly)
            {
                int colorInt = color.Red + (color.Green << 8) + (color.Blue << 16);
                colorParam.Set(colorInt);
            }

            return newType.Id;
        }

        /// <summary>
        /// Get rich text note data from a group or text note
        /// </summary>
        public static string GetRichTextNoteData(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementId = new ElementId(parameters["elementId"].Value<int>());
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                var data = GetRichTextData(element);
                if (data == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No rich text data found on element"
                    });
                }

                // If it's a group, get member info
                List<object> members = null;
                if (element is Group group)
                {
                    members = group.GetMemberIds()
                        .Select(id =>
                        {
                            var textNote = doc.GetElement(id) as TextNote;
                            return textNote != null ? new
                            {
                                textNoteId = id.Value,
                                text = textNote.Text,
                                typeId = textNote.TextNoteType?.Id.Value
                            } : null;
                        })
                        .Where(m => m != null)
                        .Cast<object>()
                        .ToList();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementId.Value,
                    isGroup = element is Group,
                    data = data,
                    members = members
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Update a rich text note with new spans
        /// </summary>
        public static string UpdateRichTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementId = new ElementId(parameters["elementId"].Value<int>());
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                // Get existing data
                var existingData = GetRichTextData(element);
                if (existingData == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No rich text data found - not a rich text note"
                    });
                }

                // Parse new spans
                var spansJson = parameters["spans"]?.ToString();
                var newSpans = spansJson != null
                    ? JsonConvert.DeserializeObject<List<TextSpan>>(spansJson)
                    : existingData.Spans;

                using (var trans = new Transaction(doc, "Update Rich Text Note"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Delete old text notes
                    if (element is Group group)
                    {
                        var memberIds = group.GetMemberIds().ToList();
                        group.UngroupMembers();
                        foreach (var id in memberIds)
                        {
                            doc.Delete(id);
                        }
                        doc.Delete(elementId);
                    }
                    else if (element is TextNote)
                    {
                        doc.Delete(elementId);
                    }

                    trans.Commit();
                }

                // Create new rich text note with updated spans
                var createParams = new JObject
                {
                    ["viewId"] = existingData.ViewId,
                    ["location"] = JArray.FromObject(existingData.AnchorPoint),
                    ["spans"] = JArray.FromObject(newSpans),
                    ["baseTypeId"] = existingData.BaseTypeId,
                    ["alignment"] = existingData.Alignment
                };

                return CreateRichTextNote(uiApp, createParams);
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Explode rich text note into regular text notes (removes grouping and metadata)
        /// </summary>
        public static string ExplodeRichTextNote(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var elementId = new ElementId(parameters["elementId"].Value<int>());
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Element not found" });
                }

                if (!(element is Group group))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element is not a group - cannot explode"
                    });
                }

                using (var trans = new Transaction(doc, "Explode Rich Text Note"))
                {
                    trans.Start();

                    var memberIds = group.GetMemberIds().ToList();

                    // Ungroup
                    group.UngroupMembers();

                    // Clear extensible storage from text notes
                    var schema = GetOrCreateSchema();
                    foreach (var id in memberIds)
                    {
                        var textNote = doc.GetElement(id);
                        if (textNote != null)
                        {
                            textNote.DeleteEntity(schema);
                        }
                    }

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        explodedTextNoteIds = memberIds.Select(id => id.Value).ToList(),
                        message = "Rich text note exploded into individual text notes"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Find all rich text notes in the document
        /// </summary>
        public static string GetRichTextNotes(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = parameters["viewId"]?.Value<int>();
                var schema = GetOrCreateSchema();

                // Find groups with rich text data
                var richTextGroups = new FilteredElementCollector(doc)
                    .OfClass(typeof(Group))
                    .Cast<Group>()
                    .Where(g =>
                    {
                        var entity = g.GetEntity(schema);
                        return entity.IsValid();
                    })
                    .Select(g =>
                    {
                        var data = GetRichTextData(g);
                        return new
                        {
                            groupId = g.Id.Value,
                            groupName = g.Name,
                            viewId = data?.ViewId,
                            spanCount = data?.Spans?.Count ?? 0,
                            previewText = data?.Spans != null
                                ? string.Join("", data.Spans.Select(s => s.Text))
                                : ""
                        };
                    })
                    .Where(r => !viewId.HasValue || r.viewId == viewId)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = richTextGroups.Count,
                    richTextNotes = richTextGroups
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
