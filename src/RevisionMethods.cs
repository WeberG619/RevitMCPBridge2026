using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    /// <summary>
    /// Revision and revision cloud methods for MCP Bridge
    /// </summary>
    public static class RevisionMethods
    {
        /// <summary>
        /// Get all revisions in the model
        /// </summary>
        [MCPMethod("getRevisions", Category = "Revision", Description = "Get all revisions in the model")]
        public static string GetRevisions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var revisions = new FilteredElementCollector(doc)
                    .OfClass(typeof(Revision))
                    .Cast<Revision>()
                    .Select(r => new
                    {
                        revisionId = r.Id.Value,
                        sequence = r.SequenceNumber,
                        date = r.RevisionDate,
                        description = r.Description,
                        issuedBy = r.IssuedBy,
                        issuedTo = r.IssuedTo,
                        visibility = r.Visibility.ToString(),
                        issued = r.Issued
                    })
                    .OrderBy(r => r.sequence)
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    revisionCount = revisions.Count,
                    revisions = revisions
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a new revision
        /// </summary>
        [MCPMethod("createNewRevision", Category = "Revision", Description = "Create a new revision in the model")]
        public static string CreateRevision(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var date = parameters["date"]?.ToString() ?? DateTime.Now.ToString("MM/dd/yyyy");
                var description = parameters["description"]?.ToString() ?? "";
                var issuedBy = parameters["issuedBy"]?.ToString() ?? "";
                var issuedTo = parameters["issuedTo"]?.ToString() ?? "";

                using (var trans = new Transaction(doc, "Create Revision"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var revision = Revision.Create(doc);
                    revision.RevisionDate = date;
                    revision.Description = description;
                    revision.IssuedBy = issuedBy;
                    revision.IssuedTo = issuedTo;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        revisionId = revision.Id.Value,
                        sequence = revision.SequenceNumber,
                        date = revision.RevisionDate,
                        description = revision.Description
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Update a revision
        /// </summary>
        [MCPMethod("updateRevision", Category = "Revision", Description = "Update an existing revision's properties")]
        public static string UpdateRevision(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var revisionId = parameters["revisionId"]?.Value<int>();

                if (!revisionId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId is required" });
                }

                var revision = doc.GetElement(new ElementId(revisionId.Value)) as Revision;
                if (revision == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision not found" });
                }

                using (var trans = new Transaction(doc, "Update Revision"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (parameters["date"] != null)
                        revision.RevisionDate = parameters["date"].ToString();

                    if (parameters["description"] != null)
                        revision.Description = parameters["description"].ToString();

                    if (parameters["issuedBy"] != null)
                        revision.IssuedBy = parameters["issuedBy"].ToString();

                    if (parameters["issuedTo"] != null)
                        revision.IssuedTo = parameters["issuedTo"].ToString();

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        revisionId = revision.Id.Value,
                        date = revision.RevisionDate,
                        description = revision.Description
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Issue a revision (mark as issued)
        /// </summary>
        [MCPMethod("issueRevision", Category = "Revision", Description = "Mark a revision as issued")]
        public static string IssueRevision(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var revisionId = parameters["revisionId"]?.Value<int>();

                if (!revisionId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId is required" });
                }

                var revision = doc.GetElement(new ElementId(revisionId.Value)) as Revision;
                if (revision == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision not found" });
                }

                using (var trans = new Transaction(doc, "Issue Revision"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    revision.Issued = true;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        revisionId = revision.Id.Value,
                        issued = true
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Delete a revision
        /// </summary>
        [MCPMethod("deleteRevision", Category = "Revision", Description = "Delete a revision from the model")]
        public static string DeleteRevision(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var revisionId = parameters["revisionId"]?.Value<int>();

                if (!revisionId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Revision"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(revisionId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedRevisionId = revisionId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get all revision clouds in the model
        /// </summary>
        [MCPMethod("getRevisionCloudsList", Category = "Revision", Description = "Get all revision clouds in the model or a specific view")]
        public static string GetRevisionClouds(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = parameters["viewId"]?.Value<int>();

                FilteredElementCollector collector;
                if (viewId.HasValue)
                {
                    collector = new FilteredElementCollector(doc, new ElementId(viewId.Value));
                }
                else
                {
                    collector = new FilteredElementCollector(doc);
                }

                var clouds = collector
                    .OfCategory(BuiltInCategory.OST_RevisionClouds)
                    .WhereElementIsNotElementType()
                    .Cast<RevisionCloud>()
                    .Select(c =>
                    {
                        var revision = doc.GetElement(c.RevisionId) as Revision;
                        return new
                        {
                            cloudId = c.Id.Value,
                            revisionId = (int)c.RevisionId.Value,
                            revisionSequence = revision?.SequenceNumber ?? -1,
                            revisionDescription = revision?.Description ?? "Unknown",
                            viewId = (int)c.OwnerViewId.Value
                        };
                    })
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    cloudCount = clouds.Count,
                    revisionClouds = clouds
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Create a revision cloud
        /// </summary>
        [MCPMethod("createNewRevisionCloud", Category = "Revision", Description = "Create a new revision cloud in a view")]
        public static string CreateRevisionCloud(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var viewId = parameters["viewId"]?.Value<int>();
                var revisionId = parameters["revisionId"]?.Value<int>();
                var points = parameters["points"]?.ToObject<double[][]>();

                if (!viewId.HasValue || !revisionId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "viewId and revisionId are required" });
                }

                if (points == null || points.Length < 3)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "At least 3 points are required for a cloud boundary" });
                }

                var view = doc.GetElement(new ElementId(viewId.Value)) as View;
                if (view == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "View not found" });
                }

                var revision = doc.GetElement(new ElementId(revisionId.Value)) as Revision;
                if (revision == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision not found" });
                }

                using (var trans = new Transaction(doc, "Create Revision Cloud"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Create list of curves from points
                    var curves = new List<Curve>();
                    for (int i = 0; i < points.Length; i++)
                    {
                        var start = new XYZ(points[i][0], points[i][1], 0);
                        var end = new XYZ(points[(i + 1) % points.Length][0], points[(i + 1) % points.Length][1], 0);
                        curves.Add(Line.CreateBound(start, end));
                    }

                    var cloud = RevisionCloud.Create(doc, view, revision.Id, curves);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        cloudId = cloud.Id.Value,
                        revisionId = revisionId.Value,
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
        /// Delete a revision cloud
        /// </summary>
        [MCPMethod("deleteRevisionCloudById", Category = "Revision", Description = "Delete a revision cloud by element ID")]
        public static string DeleteRevisionCloud(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var cloudId = parameters["cloudId"]?.Value<int>();

                if (!cloudId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "cloudId is required" });
                }

                using (var trans = new Transaction(doc, "Delete Revision Cloud"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(new ElementId(cloudId.Value));
                    trans.Commit();

                    return JsonConvert.SerializeObject(new { success = true, deletedCloudId = cloudId.Value });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Get revisions on a sheet
        /// </summary>
        [MCPMethod("getSheetRevisions", Category = "Revision", Description = "Get all revisions associated with a sheet")]
        public static string GetSheetRevisions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetId = parameters["sheetId"]?.Value<int>();

                if (!sheetId.HasValue)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId is required" });
                }

                var sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                var revisionIds = sheet.GetAllRevisionIds();
                var revisions = revisionIds.Select(id =>
                {
                    var revision = doc.GetElement(id) as Revision;
                    return new
                    {
                        revisionId = (int)id.Value,
                        sequence = revision?.SequenceNumber ?? -1,
                        date = revision?.RevisionDate ?? "",
                        description = revision?.Description ?? ""
                    };
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    sheetId = sheetId.Value,
                    sheetNumber = sheet.SheetNumber,
                    revisionCount = revisions.Count,
                    revisions = revisions
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Add revisions to a sheet
        /// </summary>
        [MCPMethod("addRevisionsToSheet", Category = "Revision", Description = "Add revisions to a sheet's revision block")]
        public static string AddRevisionsToSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetId = parameters["sheetId"]?.Value<int>();
                var revisionIds = parameters["revisionIds"]?.ToObject<int[]>();

                if (!sheetId.HasValue || revisionIds == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId and revisionIds are required" });
                }

                var sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                using (var trans = new Transaction(doc, "Add Revisions to Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var existingRevisions = sheet.GetAllRevisionIds().ToList();
                    var idsToAdd = revisionIds.Select(id => new ElementId(id));

                    foreach (var id in idsToAdd)
                    {
                        if (!existingRevisions.Contains(id))
                        {
                            existingRevisions.Add(id);
                        }
                    }

                    sheet.SetAdditionalRevisionIds(existingRevisions);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sheetId = sheetId.Value,
                        addedRevisionCount = revisionIds.Length
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Remove revisions from a sheet
        /// </summary>
        [MCPMethod("removeRevisionsFromSheet", Category = "Revision", Description = "Remove revisions from a sheet's revision block")]
        public static string RemoveRevisionsFromSheet(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheetId = parameters["sheetId"]?.Value<int>();
                var revisionIds = parameters["revisionIds"]?.ToObject<int[]>();

                if (!sheetId.HasValue || revisionIds == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "sheetId and revisionIds are required" });
                }

                var sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet;
                if (sheet == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Sheet not found" });
                }

                using (var trans = new Transaction(doc, "Remove Revisions from Sheet"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var existingRevisions = sheet.GetAdditionalRevisionIds().ToList();
                    var idsToRemove = revisionIds.Select(id => new ElementId(id)).ToHashSet();

                    existingRevisions.RemoveAll(id => idsToRemove.Contains(id));
                    sheet.SetAdditionalRevisionIds(existingRevisions);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        sheetId = sheetId.Value,
                        removedRevisionCount = revisionIds.Length
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Set revision visibility
        /// </summary>
        [MCPMethod("setRevisionVisibility", Category = "Revision", Description = "Set the visibility of a revision's clouds and tags")]
        public static string SetRevisionVisibility(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var revisionId = parameters["revisionId"]?.Value<int>();
                var visibility = parameters["visibility"]?.ToString();

                if (!revisionId.HasValue || string.IsNullOrEmpty(visibility))
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionId and visibility are required" });
                }

                var revision = doc.GetElement(new ElementId(revisionId.Value)) as Revision;
                if (revision == null)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "Revision not found" });
                }

                RevisionVisibility visibilityEnum;
                if (!Enum.TryParse(visibility, true, out visibilityEnum))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid visibility. Use: Hidden, CloudAndTagVisible, TagVisible, or CloudVisible"
                    });
                }

                using (var trans = new Transaction(doc, "Set Revision Visibility"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    revision.Visibility = visibilityEnum;

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        revisionId = revisionId.Value,
                        visibility = revision.Visibility.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Reorder revisions
        /// </summary>
        [MCPMethod("reorderRevisions", Category = "Revision", Description = "Reorder revisions to change their sequence numbers")]
        public static string ReorderRevisions(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var revisionIds = parameters["revisionIds"]?.ToObject<int[]>();

                if (revisionIds == null || revisionIds.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = "revisionIds array is required" });
                }

                using (var trans = new Transaction(doc, "Reorder Revisions"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    var ids = revisionIds.Select(id => new ElementId(id)).ToList();
                    Revision.ReorderRevisionSequence(doc, ids);

                    trans.Commit();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        reorderedCount = revisionIds.Length
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }
    }
}
