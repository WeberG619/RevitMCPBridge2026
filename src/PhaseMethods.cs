using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPBridge;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge2026
{
    /// <summary>
    /// MCP Server Methods for Revit Phases
    /// Handles phase creation, modification, phasing of elements, and phase filters
    /// </summary>
    public static class PhaseMethods
    {
        #region Phase Creation and Management

        /// <summary>
        /// Creates a new phase in the project
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters containing phaseName</param>
        /// <returns>JSON response with success status and phase ID</returns>
        [MCPMethod("createPhase", Category = "Phase", Description = "Creates a new phase in the project")]
        public static string CreatePhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["phaseName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "phaseName is required"
                    });
                }

                string phaseName = parameters["phaseName"].ToString();

                // **API LIMITATION**: In Revit 2026, phases cannot be created directly through the API
                // Phases must be created through the UI (Manage > Phases dialog)
                // This method documents the intended functionality but cannot execute it

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API LIMITATION: Phases cannot be created programmatically in Revit 2026. Use the UI (Manage > Phases) instead.",
                    apiLimitation = true,
                    workaround = "Create phases manually through Manage > Phases dialog, then use GetAllPhases to retrieve phase IDs"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all phases in the project
        /// </summary>
        [MCPMethod("getAllPhases", Category = "Phase", Description = "Gets all phases in the project")]
        public static string GetAllPhases(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                PhaseArray phases = doc.Phases;
                var phasesList = new List<object>();

                for (int i = 0; i < phases.Size; i++)
                {
                    Phase phase = phases.get_Item(i);
                    phasesList.Add(new
                    {
                        phaseId = (int)phase.Id.Value,
                        phaseName = phase.Name,
                        sequenceNumber = i + 1
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = phasesList.Count,
                    phases = phasesList
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets information about a specific phase
        /// </summary>
        [MCPMethod("getPhaseInfo", Category = "Phase", Description = "Gets information about a specific phase")]
        public static string GetPhaseInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["phaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "phaseId is required"
                    });
                }

                int phaseIdInt = parameters["phaseId"].ToObject<int>();
                ElementId phaseId = new ElementId(phaseIdInt);

                Phase phase = doc.GetElement(phaseId) as Phase;

                if (phase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase with ID {phaseIdInt} not found"
                    });
                }

                // Find sequence number
                PhaseArray phases = doc.Phases;
                int sequenceNumber = 0;
                for (int i = 0; i < phases.Size; i++)
                {
                    if (phases.get_Item(i).Id == phaseId)
                    {
                        sequenceNumber = i + 1;
                        break;
                    }
                }

                // Count elements created in this phase
                var elementsCreatedCount = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() == phaseId)
                    .Count();

                // Count elements demolished in this phase
                var elementsDemolishedCount = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId() == phaseId)
                    .Count();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    phaseId = phaseIdInt,
                    phaseName = phase.Name,
                    sequenceNumber = sequenceNumber,
                    elementsCreated = elementsCreatedCount,
                    elementsDemolished = elementsDemolishedCount
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Renames a phase
        /// </summary>
        [MCPMethod("renamePhase", Category = "Phase", Description = "Renames an existing phase")]
        public static string RenamePhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["phaseId"] == null || parameters["newName"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "phaseId and newName are required"
                    });
                }

                int phaseIdInt = parameters["phaseId"].ToObject<int>();
                ElementId phaseId = new ElementId(phaseIdInt);
                string newName = parameters["newName"].ToString();

                Phase phase = doc.GetElement(phaseId) as Phase;

                if (phase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase with ID {phaseIdInt} not found"
                    });
                }

                string oldName = phase.Name;

                using (var trans = new Transaction(doc, "Rename Phase"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    phase.Name = newName;
                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        phaseId = phaseIdInt,
                        oldName = oldName,
                        newName = newName,
                        message = $"Phase renamed from '{oldName}' to '{newName}'"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Deletes a phase
        /// </summary>
        [MCPMethod("deletePhase", Category = "Phase", Description = "Deletes a phase from the project")]
        public static string DeletePhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["phaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "phaseId is required"
                    });
                }

                int phaseIdInt = parameters["phaseId"].ToObject<int>();
                ElementId phaseId = new ElementId(phaseIdInt);

                Phase phase = doc.GetElement(phaseId) as Phase;

                if (phase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase with ID {phaseIdInt} not found"
                    });
                }

                // Check if this is the only phase
                PhaseArray phases = doc.Phases;
                if (phases.Size == 1)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot delete the only phase in the project"
                    });
                }

                // Count elements created or demolished in this phase
                var elementsCreated = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() == phaseId)
                    .Count();

                var elementsDemolished = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId() == phaseId)
                    .Count();

                if (elementsCreated > 0 || elementsDemolished > 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Cannot delete phase with elements. {elementsCreated} elements created, {elementsDemolished} elements demolished in this phase.",
                        elementsCreated = elementsCreated,
                        elementsDemolished = elementsDemolished
                    });
                }

                string phaseName = phase.Name;

                using (var trans = new Transaction(doc, "Delete Phase"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(phaseId);
                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        phaseId = phaseIdInt,
                        phaseName = phaseName,
                        message = $"Phase '{phaseName}' deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Reorders phases
        /// </summary>
        [MCPMethod("reorderPhases", Category = "Phase", Description = "Reorders phases in the project")]
        public static string ReorderPhases(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // **API LIMITATION**: Phases cannot be reordered programmatically in Revit 2026
                // Phase sequence is determined by their creation order and cannot be arbitrarily changed via API
                // Phases must be reordered through the UI (Manage > Phases dialog)

                // Get current phase order for reference
                PhaseArray phases = doc.Phases;
                var currentOrder = new List<object>();

                for (int i = 0; i < phases.Size; i++)
                {
                    Phase phase = phases.get_Item(i);
                    currentOrder.Add(new
                    {
                        phaseId = phase.Id.Value,
                        phaseName = phase.Name,
                        sequenceNumber = i + 1
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "API LIMITATION: Phases cannot be reordered programmatically in Revit 2026. Use the UI (Manage > Phases) instead.",
                    apiLimitation = true,
                    workaround = "Reorder phases manually through Manage > Phases dialog. Phase order is determined by creation sequence.",
                    currentPhaseOrder = currentOrder,
                    totalPhases = phases.Size
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Element Phasing

        /// <summary>
        /// Sets the phase created for an element
        /// </summary>
        [MCPMethod("setElementPhaseCreated", Category = "Phase", Description = "Sets the phase created property for an element")]
        public static string SetElementPhaseCreated(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["elementId"] == null || parameters["phaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId and phaseId are required"
                    });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                int phaseIdInt = parameters["phaseId"].ToObject<int>();

                ElementId elementId = new ElementId(elementIdInt);
                ElementId phaseId = new ElementId(phaseIdInt);

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                Phase phase = doc.GetElement(phaseId) as Phase;
                if (phase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase with ID {phaseIdInt} not found"
                    });
                }

                Parameter phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (phaseCreatedParam == null || phaseCreatedParam.IsReadOnly)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element does not support phase created parameter or it is read-only"
                    });
                }

                using (var trans = new Transaction(doc, "Set Element Phase Created"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    phaseCreatedParam.Set(phaseId);
                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = elementIdInt,
                        phaseId = phaseIdInt,
                        phaseName = phase.Name,
                        message = $"Element phase created set to '{phase.Name}'"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets the phase demolished for an element
        /// </summary>
        [MCPMethod("setElementPhaseDemolished", Category = "Phase", Description = "Sets the phase demolished property for an element")]
        public static string SetElementPhaseDemolished(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                // phaseId can be null to clear the demolished phase
                ElementId phaseId = null;
                string phaseName = "None";

                if (parameters["phaseId"] != null)
                {
                    int phaseIdInt = parameters["phaseId"].ToObject<int>();
                    phaseId = new ElementId(phaseIdInt);

                    Phase phase = doc.GetElement(phaseId) as Phase;
                    if (phase == null)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Phase with ID {phaseIdInt} not found"
                        });
                    }
                    phaseName = phase.Name;
                }

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                Parameter phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                if (phaseDemolishedParam == null || phaseDemolishedParam.IsReadOnly)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Element does not support phase demolished parameter or it is read-only"
                    });
                }

                using (var trans = new Transaction(doc, "Set Element Phase Demolished"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    if (phaseId == null)
                    {
                        // Clear the demolished phase (set to InvalidElementId)
                        phaseDemolishedParam.Set(ElementId.InvalidElementId);
                    }
                    else
                    {
                        phaseDemolishedParam.Set(phaseId);
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        elementId = elementIdInt,
                        phaseId = phaseId != null ? (int)phaseId.Value : -1,
                        phaseName = phaseName,
                        message = phaseId != null
                            ? $"Element phase demolished set to '{phaseName}'"
                            : "Element phase demolished cleared"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets phasing information for an element
        /// </summary>
        [MCPMethod("getElementPhasing", Category = "Phase", Description = "Gets phasing information for an element")]
        public static string GetElementPhasing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["elementId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementId is required"
                    });
                }

                int elementIdInt = parameters["elementId"].ToObject<int>();
                ElementId elementId = new ElementId(elementIdInt);

                Element element = doc.GetElement(elementId);
                if (element == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Element with ID {elementIdInt} not found"
                    });
                }

                // Get phase created parameter
                Parameter phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                ElementId phaseCreatedId = phaseCreatedParam?.AsElementId();
                Phase phaseCreated = phaseCreatedId != null ? doc.GetElement(phaseCreatedId) as Phase : null;

                // Get phase demolished parameter
                Parameter phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                ElementId phaseDemolishedId = phaseDemolishedParam?.AsElementId();
                Phase phaseDemolished = phaseDemolishedId != null && phaseDemolishedId != ElementId.InvalidElementId
                    ? doc.GetElement(phaseDemolishedId) as Phase
                    : null;

                // Determine phase status
                string phaseStatus;
                if (phaseDemolished != null)
                {
                    phaseStatus = "Demolished";
                }
                else if (phaseCreated != null)
                {
                    phaseStatus = "Existing";
                }
                else
                {
                    phaseStatus = "Unknown";
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    elementId = elementIdInt,
                    phaseCreated = phaseCreated != null ? new
                    {
                        phaseId = (int)phaseCreated.Id.Value,
                        phaseName = phaseCreated.Name
                    } : null,
                    phaseDemolished = phaseDemolished != null ? new
                    {
                        phaseId = (int)phaseDemolished.Id.Value,
                        phaseName = phaseDemolished.Name
                    } : null,
                    phaseStatus = phaseStatus,
                    hasPhasing = phaseCreatedParam != null
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets all elements in a specific phase
        /// </summary>
        [MCPMethod("getElementsInPhase", Category = "Phase", Description = "Gets all elements belonging to a specific phase")]
        public static string GetElementsInPhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["phaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "phaseId is required"
                    });
                }

                int phaseIdInt = parameters["phaseId"].ToObject<int>();
                ElementId phaseId = new ElementId(phaseIdInt);

                Phase phase = doc.GetElement(phaseId) as Phase;
                if (phase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase with ID {phaseIdInt} not found"
                    });
                }

                string status = parameters["status"]?.ToString()?.ToLower() ?? "all";

                var elementsInPhase = new List<object>();

                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType();

                foreach (Element element in collector)
                {
                    Parameter phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    Parameter phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);

                    if (phaseCreatedParam == null) continue;

                    ElementId phaseCreated = phaseCreatedParam.AsElementId();
                    ElementId phaseDemolished = phaseDemolishedParam?.AsElementId();

                    bool include = false;

                    if (status == "all" || status == "created" || status == "new")
                    {
                        if (phaseCreated == phaseId)
                        {
                            include = true;
                        }
                    }

                    if (status == "all" || status == "demolished")
                    {
                        if (phaseDemolished != null && phaseDemolished == phaseId)
                        {
                            include = true;
                        }
                    }

                    if (include)
                    {
                        elementsInPhase.Add(new
                        {
                            elementId = (int)element.Id.Value,
                            category = element.Category?.Name ?? "None",
                            elementType = element.GetType().Name,
                            phaseStatus = phaseDemolished != null && phaseDemolished == phaseId ? "Demolished" : "Created"
                        });
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    phaseId = phaseIdInt,
                    phaseName = phase.Name,
                    status = status,
                    count = elementsInPhase.Count,
                    elements = elementsInPhase
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets phasing for multiple elements
        /// </summary>
        [MCPMethod("setBulkElementPhasing", Category = "Phase", Description = "Sets phase created and demolished for multiple elements at once")]
        public static string SetBulkElementPhasing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["elementIds"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array is required"
                    });
                }

                var elementIdsList = parameters["elementIds"].ToObject<List<int>>();

                ElementId phaseCreatedId = null;
                ElementId phaseDemolishedId = null;

                if (parameters["phaseCreatedId"] != null)
                {
                    phaseCreatedId = new ElementId(parameters["phaseCreatedId"].ToObject<int>());
                }

                if (parameters["phaseDemolishedId"] != null)
                {
                    phaseDemolishedId = new ElementId(parameters["phaseDemolishedId"].ToObject<int>());
                }

                if (phaseCreatedId == null && phaseDemolishedId == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "At least one of phaseCreatedId or phaseDemolishedId must be provided"
                    });
                }

                int successCount = 0;
                int failedCount = 0;
                var failures = new List<string>();

                using (var trans = new Transaction(doc, "Set Bulk Element Phasing"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (int elemIdInt in elementIdsList)
                    {
                        try
                        {
                            Element element = doc.GetElement(new ElementId(elemIdInt));
                            if (element == null)
                            {
                                failures.Add($"Element {elemIdInt} not found");
                                failedCount++;
                                continue;
                            }

                            bool updated = false;

                            if (phaseCreatedId != null)
                            {
                                Parameter param = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                                if (param != null && !param.IsReadOnly)
                                {
                                    param.Set(phaseCreatedId);
                                    updated = true;
                                }
                            }

                            if (phaseDemolishedId != null)
                            {
                                Parameter param = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                                if (param != null && !param.IsReadOnly)
                                {
                                    param.Set(phaseDemolishedId);
                                    updated = true;
                                }
                            }

                            if (updated)
                            {
                                successCount++;
                            }
                            else
                            {
                                failures.Add($"Element {elemIdInt} does not support phasing or parameters are read-only");
                                failedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"Element {elemIdInt}: {ex.Message}");
                            failedCount++;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        successCount = successCount,
                        failedCount = failedCount,
                        totalElements = elementIdsList.Count,
                        failures = failures,
                        message = $"Updated {successCount} elements, {failedCount} failed"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Phase Filters

        /// <summary>
        /// Creates a new phase filter
        /// </summary>
        [MCPMethod("createPhaseFilter", Category = "Phase", Description = "Creates a new phase filter")]
        public static string CreatePhaseFilter(UIApplication uiApp, JObject parameters)
        {
            // **API LIMITATION**: Phase filters cannot be created programmatically in Revit 2026
            // Phase filters must be created through the UI (Manage > Phases > Phase Filters tab)

            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "API LIMITATION: Phase filters cannot be created programmatically in Revit 2026. Use the UI (Manage > Phases > Phase Filters) instead.",
                apiLimitation = true,
                workaround = "Create phase filters manually through Manage > Phases > Phase Filters tab, then use GetPhaseFilters to retrieve filter IDs"
            });
        }

        /// <summary>
        /// Gets all phase filters
        /// </summary>
        [MCPMethod("getPhaseFilters", Category = "Phase", Description = "Gets all phase filters in the project")]
        public static string GetPhaseFilters(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var phaseFilters = new List<object>();

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(PhaseFilter));

                foreach (PhaseFilter filter in collector)
                {
                    phaseFilters.Add(new
                    {
                        filterId = (int)filter.Id.Value,
                        filterName = filter.Name
                    });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = phaseFilters.Count,
                    phaseFilters = phaseFilters
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets phase filter settings
        /// </summary>
        [MCPMethod("getPhaseFilterInfo", Category = "Phase", Description = "Gets settings for a specific phase filter")]
        public static string GetPhaseFilterInfo(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "filterId is required" });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                PhaseFilter filter = doc.GetElement(new ElementId(filterIdInt)) as PhaseFilter;

                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Phase filter with ID {filterIdInt} not found" });
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    filterId = filterIdInt,
                    filterName = filter.Name
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        /// <summary>
        /// Modifies phase filter settings
        /// </summary>
        [MCPMethod("modifyPhaseFilter", Category = "Phase", Description = "Modifies settings for a phase filter")]
        public static string ModifyPhaseFilter(UIApplication uiApp, JObject parameters)
        {
            // **API LIMITATION**: Phase filter settings cannot be modified programmatically in Revit 2026
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                error = "API LIMITATION: Phase filter settings cannot be modified programmatically in Revit 2026. Use the UI instead.",
                apiLimitation = true
            });
        }

        /// <summary>
        /// Deletes a phase filter
        /// </summary>
        [MCPMethod("deletePhaseFilter", Category = "Phase", Description = "Deletes a phase filter")]
        public static string DeletePhaseFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["filterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = "filterId is required" });
                }

                int filterIdInt = parameters["filterId"].ToObject<int>();
                ElementId filterId = new ElementId(filterIdInt);

                PhaseFilter filter = doc.GetElement(filterId) as PhaseFilter;
                if (filter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = false, error = $"Phase filter with ID {filterIdInt} not found" });
                }

                string filterName = filter.Name;

                using (var trans = new Transaction(doc, "Delete Phase Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    doc.Delete(filterId);
                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        filterId = filterIdInt,
                        filterName = filterName,
                        message = $"Phase filter '{filterName}' deleted successfully"
                    });
                }
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

        #endregion

        #region View Phase Settings

        /// <summary>
        /// Sets the phase for a view
        /// </summary>
        [MCPMethod("setViewPhase", Category = "Phase", Description = "Sets the phase assigned to a view")]
        public static string SetViewPhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                if (parameters["phaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "phaseId is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                int phaseIdInt = parameters["phaseId"].ToObject<int>();

                ElementId viewId = new ElementId(viewIdInt);
                ElementId phaseId = new ElementId(phaseIdInt);

                View view = doc.GetElement(viewId) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                Phase phase = doc.GetElement(phaseId) as Phase;
                if (phase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase with ID {phaseIdInt} not found"
                    });
                }

                Parameter phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (phaseParam == null || phaseParam.IsReadOnly)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View does not support phase parameter or it is read-only"
                    });
                }

                using (var trans = new Transaction(doc, "Set View Phase"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    phaseParam.Set(phaseId);

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        viewName = view.Name,
                        phaseId = phaseIdInt,
                        phaseName = phase.Name,
                        message = $"View '{view.Name}' phase set to '{phase.Name}'"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Sets the phase filter for a view
        /// </summary>
        [MCPMethod("setViewPhaseFilter", Category = "Phase", Description = "Sets the phase filter for a view")]
        public static string SetViewPhaseFilter(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null || parameters["phaseFilterId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId and phaseFilterId are required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                int phaseFilterIdInt = parameters["phaseFilterId"].ToObject<int>();

                View view = doc.GetElement(new ElementId(viewIdInt)) as View;
                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                ElementId phaseFilterId = new ElementId(phaseFilterIdInt);
                PhaseFilter phaseFilter = doc.GetElement(phaseFilterId) as PhaseFilter;
                if (phaseFilter == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Phase filter with ID {phaseFilterIdInt} not found"
                    });
                }

                Parameter phaseFilterParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                if (phaseFilterParam == null || phaseFilterParam.IsReadOnly)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "View does not support phase filter parameter or it is read-only"
                    });
                }

                using (var trans = new Transaction(doc, "Set View Phase Filter"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    phaseFilterParam.Set(phaseFilterId);
                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        viewId = viewIdInt,
                        viewName = view.Name,
                        phaseFilterId = phaseFilterIdInt,
                        phaseFilterName = phaseFilter.Name,
                        message = $"View '{view.Name}' phase filter set to '{phaseFilter.Name}'"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets phase settings for a view
        /// </summary>
        [MCPMethod("getViewPhaseSettings", Category = "Phase", Description = "Gets phase and phase filter settings for a view")]
        public static string GetViewPhaseSettings(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                if (parameters["viewId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "viewId is required"
                    });
                }

                int viewIdInt = parameters["viewId"].ToObject<int>();
                View view = doc.GetElement(new ElementId(viewIdInt)) as View;

                if (view == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"View with ID {viewIdInt} not found"
                    });
                }

                Parameter phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                Parameter phaseFilterParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);

                object phaseInfo = null;
                object phaseFilterInfo = null;

                if (phaseParam != null && !phaseParam.IsReadOnly)
                {
                    ElementId phaseId = phaseParam.AsElementId();
                    if (phaseId != ElementId.InvalidElementId)
                    {
                        Phase phase = doc.GetElement(phaseId) as Phase;
                        if (phase != null)
                        {
                            phaseInfo = new
                            {
                                phaseId = phaseId.Value,
                                phaseName = phase.Name
                            };
                        }
                    }
                }

                if (phaseFilterParam != null && !phaseFilterParam.IsReadOnly)
                {
                    ElementId phaseFilterId = phaseFilterParam.AsElementId();
                    if (phaseFilterId != ElementId.InvalidElementId)
                    {
                        PhaseFilter phaseFilter = doc.GetElement(phaseFilterId) as PhaseFilter;
                        if (phaseFilter != null)
                        {
                            phaseFilterInfo = new
                            {
                                phaseFilterId = phaseFilterId.Value,
                                phaseFilterName = phaseFilter.Name
                            };
                        }
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    viewId = viewIdInt,
                    viewName = view.Name,
                    phase = phaseInfo,
                    phaseFilter = phaseFilterInfo
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Phase Analysis

        /// <summary>
        /// Analyzes phasing status across the project
        /// </summary>
        [MCPMethod("analyzePhasing", Category = "Phase", Description = "Analyzes phasing status across the project")]
        public static string AnalyzePhasing(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Get all phases
                PhaseArray phases = doc.Phases;
                var phaseAnalysis = new List<object>();

                for (int i = 0; i < phases.Size; i++)
                {
                    Phase phase = phases.get_Item(i);
                    ElementId phaseId = phase.Id;

                    // Count elements created in this phase
                    var elementsCreated = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .Where(e =>
                        {
                            Parameter param = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
                            return param != null && param.AsElementId() == phaseId;
                        })
                        .ToList();

                    // Count elements demolished in this phase
                    var elementsDemolished = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .Where(e =>
                        {
                            Parameter param = e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                            return param != null && param.AsElementId() == phaseId;
                        })
                        .ToList();

                    // Categorize created elements
                    var categoryBreakdown = elementsCreated
                        .GroupBy(e => e.Category?.Name ?? "None")
                        .Select(g => new { category = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(10)
                        .ToList();

                    phaseAnalysis.Add(new
                    {
                        phaseId = phaseId.Value,
                        phaseName = phase.Name,
                        sequenceNumber = i + 1,
                        elementsCreatedCount = elementsCreated.Count,
                        elementsDemolishedCount = elementsDemolished.Count,
                        topCategories = categoryBreakdown
                    });
                }

                // Overall statistics
                var totalElements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_CREATED) != null)
                    .Count();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    totalPhases = phases.Size,
                    totalPhasedElements = totalElements,
                    phaseAnalysis = phaseAnalysis,
                    message = $"Analyzed {phases.Size} phases with {totalElements} total phased elements"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Finds phasing conflicts or issues
        /// </summary>
        [MCPMethod("findPhasingConflicts", Category = "Phase", Description = "Finds phasing conflicts or inconsistencies in the project")]
        public static string FindPhasingConflicts(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                PhaseArray phases = doc.Phases;
                var conflicts = new List<object>();

                // Build phase sequence map (phaseId -> sequenceNumber)
                var phaseSequence = new Dictionary<ElementId, int>();
                for (int i = 0; i < phases.Size; i++)
                {
                    phaseSequence[phases.get_Item(i).Id] = i;
                }

                // Analyze all phased elements
                var phasedElements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_CREATED) != null)
                    .ToList();

                foreach (Element element in phasedElements)
                {
                    Parameter phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    Parameter phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);

                    if (phaseCreatedParam == null) continue;

                    ElementId phaseCreatedId = phaseCreatedParam.AsElementId();
                    ElementId phaseDemolishedId = phaseDemolishedParam?.AsElementId();

                    // Check for invalid phase references
                    if (phaseCreatedId == ElementId.InvalidElementId)
                    {
                        conflicts.Add(new
                        {
                            elementId = element.Id.Value,
                            category = element.Category?.Name ?? "None",
                            conflictType = "InvalidPhaseCreated",
                            description = "Element has invalid phase created reference"
                        });
                        continue;
                    }

                    // Check if phase created exists
                    if (!phaseSequence.ContainsKey(phaseCreatedId))
                    {
                        conflicts.Add(new
                        {
                            elementId = element.Id.Value,
                            category = element.Category?.Name ?? "None",
                            conflictType = "MissingPhaseCreated",
                            description = $"Phase created (ID: {phaseCreatedId.Value}) does not exist"
                        });
                        continue;
                    }

                    // Check demolished before created
                    if (phaseDemolishedId != null && phaseDemolishedId != ElementId.InvalidElementId)
                    {
                        if (!phaseSequence.ContainsKey(phaseDemolishedId))
                        {
                            conflicts.Add(new
                            {
                                elementId = element.Id.Value,
                                category = element.Category?.Name ?? "None",
                                conflictType = "MissingPhaseDemolished",
                                description = $"Phase demolished (ID: {phaseDemolishedId.Value}) does not exist"
                            });
                        }
                        else if (phaseSequence[phaseDemolishedId] < phaseSequence[phaseCreatedId])
                        {
                            Phase phaseCreated = doc.GetElement(phaseCreatedId) as Phase;
                            Phase phaseDemolished = doc.GetElement(phaseDemolishedId) as Phase;

                            conflicts.Add(new
                            {
                                elementId = element.Id.Value,
                                category = element.Category?.Name ?? "None",
                                conflictType = "DemolishedBeforeCreated",
                                description = $"Element demolished in '{phaseDemolished.Name}' before it was created in '{phaseCreated.Name}'",
                                phaseCreated = phaseCreated.Name,
                                phaseDemolished = phaseDemolished.Name
                            });
                        }
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    conflictCount = conflicts.Count,
                    conflicts = conflicts,
                    message = conflicts.Count > 0
                        ? $"Found {conflicts.Count} phasing conflicts"
                        : "No phasing conflicts found"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Gets phase transition report
        /// </summary>
        [MCPMethod("getPhaseTransitionReport", Category = "Phase", Description = "Gets a report of element transitions between phases")]
        public static string GetPhaseTransitionReport(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["fromPhaseId"] == null || parameters["toPhaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "fromPhaseId and toPhaseId are required"
                    });
                }

                int fromPhaseIdInt = parameters["fromPhaseId"].ToObject<int>();
                int toPhaseIdInt = parameters["toPhaseId"].ToObject<int>();

                ElementId fromPhaseId = new ElementId(fromPhaseIdInt);
                ElementId toPhaseId = new ElementId(toPhaseIdInt);

                Phase fromPhase = doc.GetElement(fromPhaseId) as Phase;
                Phase toPhase = doc.GetElement(toPhaseId) as Phase;

                if (fromPhase == null || toPhase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "One or both phase IDs are invalid"
                    });
                }

                // Get all phased elements
                var allElements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.get_Parameter(BuiltInParameter.PHASE_CREATED) != null)
                    .ToList();

                var newElements = new List<object>();
                var demolishedElements = new List<object>();
                var existingElements = new List<object>();

                foreach (Element element in allElements)
                {
                    Parameter phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    Parameter phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);

                    if (phaseCreatedParam == null) continue;

                    ElementId phaseCreated = phaseCreatedParam.AsElementId();
                    ElementId phaseDemolished = phaseDemolishedParam?.AsElementId() ?? ElementId.InvalidElementId;

                    // Element created in the "to" phase
                    if (phaseCreated == toPhaseId)
                    {
                        newElements.Add(new
                        {
                            elementId = element.Id.Value,
                            category = element.Category?.Name ?? "None"
                        });
                    }
                    // Element demolished in the "to" phase
                    else if (phaseDemolished == toPhaseId)
                    {
                        demolishedElements.Add(new
                        {
                            elementId = element.Id.Value,
                            category = element.Category?.Name ?? "None"
                        });
                    }
                    // Element exists in both phases
                    else if (phaseCreated == fromPhaseId || phaseCreated.Value < fromPhaseId.Value)
                    {
                        if (phaseDemolished == ElementId.InvalidElementId ||
                            phaseDemolished.Value > toPhaseId.Value)
                        {
                            existingElements.Add(new
                            {
                                elementId = element.Id.Value,
                                category = element.Category?.Name ?? "None"
                            });
                        }
                    }
                }

                // Category summaries
                var newByCategory = newElements
                    .GroupBy(e => ((dynamic)e).category)
                    .Select(g => new { category = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count)
                    .ToList();

                var demolishedByCategory = demolishedElements
                    .GroupBy(e => ((dynamic)e).category)
                    .Select(g => new { category = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count)
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    fromPhase = new { phaseId = fromPhaseIdInt, phaseName = fromPhase.Name },
                    toPhase = new { phaseId = toPhaseIdInt, phaseName = toPhase.Name },
                    newElementsCount = newElements.Count,
                    demolishedElementsCount = demolishedElements.Count,
                    existingElementsCount = existingElements.Count,
                    newElementsByCategory = newByCategory,
                    demolishedElementsByCategory = demolishedByCategory,
                    message = $"Transition from '{fromPhase.Name}' to '{toPhase.Name}': {newElements.Count} new, {demolishedElements.Count} demolished, {existingElements.Count} existing"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets the current phase for the project
        /// </summary>
        [MCPMethod("getCurrentPhase", Category = "Phase", Description = "Gets the current active phase for the project")]
        public static string GetCurrentPhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                PhaseArray phases = doc.Phases;

                if (phases.Size == 0)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "No phases found in the project"
                    });
                }

                // The last phase in the PhaseArray is typically the current/latest phase
                Phase currentPhase = phases.get_Item(phases.Size - 1);

                // Count elements in this phase
                var elementsInPhase = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e =>
                    {
                        Parameter param = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
                        return param != null && param.AsElementId() == currentPhase.Id;
                    })
                    .Count();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    phaseId = currentPhase.Id.Value,
                    phaseName = currentPhase.Name,
                    sequenceNumber = phases.Size,
                    elementsCreated = elementsInPhase,
                    message = $"Current phase is '{currentPhase.Name}' (sequence {phases.Size})"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Copies elements from one phase to another
        /// </summary>
        [MCPMethod("copyElementsToPhase", Category = "Phase", Description = "Copies elements from one phase to another")]
        public static string CopyElementsToPhase(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate parameters
                if (parameters["elementIds"] == null || parameters["targetPhaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "elementIds array and targetPhaseId are required"
                    });
                }

                var elementIdsList = parameters["elementIds"].ToObject<List<int>>();
                int targetPhaseIdInt = parameters["targetPhaseId"].ToObject<int>();
                ElementId targetPhaseId = new ElementId(targetPhaseIdInt);

                Phase targetPhase = doc.GetElement(targetPhaseId) as Phase;
                if (targetPhase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Target phase with ID {targetPhaseIdInt} not found"
                    });
                }

                int successCount = 0;
                int failedCount = 0;
                var failures = new List<string>();
                var copiedElements = new List<object>();

                using (var trans = new Transaction(doc, "Copy Elements to Phase"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    foreach (int elemIdInt in elementIdsList)
                    {
                        try
                        {
                            ElementId elemId = new ElementId(elemIdInt);
                            Element element = doc.GetElement(elemId);

                            if (element == null)
                            {
                                failures.Add($"Element {elemIdInt} not found");
                                failedCount++;
                                continue;
                            }

                            Parameter phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);

                            if (phaseCreatedParam == null || phaseCreatedParam.IsReadOnly)
                            {
                                failures.Add($"Element {elemIdInt} does not support phase created parameter or it is read-only");
                                failedCount++;
                                continue;
                            }

                            ElementId currentPhaseId = phaseCreatedParam.AsElementId();
                            Phase currentPhase = currentPhaseId != ElementId.InvalidElementId
                                ? doc.GetElement(currentPhaseId) as Phase
                                : null;

                            // Set the element to the target phase
                            phaseCreatedParam.Set(targetPhaseId);

                            copiedElements.Add(new
                            {
                                elementId = elemIdInt,
                                category = element.Category?.Name ?? "None",
                                fromPhase = currentPhase?.Name ?? "None",
                                toPhase = targetPhase.Name
                            });

                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"Element {elemIdInt}: {ex.Message}");
                            failedCount++;
                        }
                    }

                    trans.Commit();

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        targetPhaseId = targetPhaseIdInt,
                        targetPhaseName = targetPhase.Name,
                        successCount = successCount,
                        failedCount = failedCount,
                        totalElements = elementIdsList.Count,
                        copiedElements = copiedElements,
                        failures = failures,
                        message = $"Copied {successCount} elements to phase '{targetPhase.Name}', {failedCount} failed"
                    });
                }
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion

        #region Demolition Plan Automation

        /// <summary>
        /// Sets up a demolition plan by identifying elements to demolish and configuring phase settings.
        /// Automates: finding elements by scope, setting demolition phase, creating demo view.
        /// </summary>
        /// <param name="uiApp">The Revit UI Application</param>
        /// <param name="parameters">JSON parameters:
        /// - demolitionPhaseId (required): Phase ID for demolition
        /// - scope (optional): "selection", "category", "boundingBox", or "all" (default: "all")
        /// - elementIds (optional): Array of element IDs for "selection" scope
        /// - categories (optional): Array of category names for "category" scope
        /// - boundingBox (optional): {minX, minY, maxX, maxY} for "boundingBox" scope
        /// - createDemoView (optional): Create a demolition view (default: true)
        /// - demoViewName (optional): Name for the demolition view
        /// </param>
        /// <returns>JSON response with demolished elements and view info</returns>
        [MCPMethod("setupDemolitionPlan", Category = "Phase", Description = "Sets up a demolition plan by identifying elements to demolish and configuring phase settings")]
        public static string SetupDemolitionPlan(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate demolition phase
                if (parameters["demolitionPhaseId"] == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "demolitionPhaseId is required"
                    });
                }

                int demoPhaseIdInt = parameters["demolitionPhaseId"].ToObject<int>();
                ElementId demoPhaseId = new ElementId(demoPhaseIdInt);

                Phase demoPhase = doc.GetElement(demoPhaseId) as Phase;
                if (demoPhase == null)
                {
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Demolition phase with ID {demoPhaseIdInt} not found"
                    });
                }

                // Parse scope
                string scope = parameters["scope"]?.ToString()?.ToLower() ?? "all";
                bool createDemoView = parameters["createDemoView"]?.ToObject<bool>() ?? true;
                string demoViewName = parameters["demoViewName"]?.ToString() ?? $"Demolition Plan - {demoPhase.Name}";

                // Collect elements to demolish based on scope
                List<Element> elementsToDemo = new List<Element>();

                if (scope == "selection")
                {
                    // Use provided element IDs
                    var elementIds = parameters["elementIds"]?.ToObject<List<int>>();
                    if (elementIds == null || elementIds.Count == 0)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementIds array is required for 'selection' scope"
                        });
                    }

                    foreach (int id in elementIds)
                    {
                        Element elem = doc.GetElement(new ElementId(id));
                        if (elem != null)
                        {
                            elementsToDemo.Add(elem);
                        }
                    }
                }
                else if (scope == "category")
                {
                    // Filter by categories
                    var categoryNames = parameters["categories"]?.ToObject<List<string>>();
                    if (categoryNames == null || categoryNames.Count == 0)
                    {
                        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "categories array is required for 'category' scope"
                        });
                    }

                    foreach (string catName in categoryNames)
                    {
                        BuiltInCategory bic;
                        if (Enum.TryParse(catName, out bic))
                        {
                            var collector = new FilteredElementCollector(doc)
                                .OfCategory(bic)
                                .WhereElementIsNotElementType();
                            elementsToDemo.AddRange(collector);
                        }
                    }
                }
                else if (scope == "boundingbox")
                {
                    // Filter by bounding box
                    double minX = parameters["boundingBox"]?["minX"]?.ToObject<double>() ?? 0;
                    double minY = parameters["boundingBox"]?["minY"]?.ToObject<double>() ?? 0;
                    double maxX = parameters["boundingBox"]?["maxX"]?.ToObject<double>() ?? 100;
                    double maxY = parameters["boundingBox"]?["maxY"]?.ToObject<double>() ?? 100;

                    XYZ minPt = new XYZ(minX, minY, double.MinValue);
                    XYZ maxPt = new XYZ(maxX, maxY, double.MaxValue);
                    Outline outline = new Outline(minPt, maxPt);

                    var bbFilter = new BoundingBoxIntersectsFilter(outline);
                    var collector = new FilteredElementCollector(doc)
                        .WherePasses(bbFilter)
                        .WhereElementIsNotElementType();

                    elementsToDemo.AddRange(collector);
                }
                else // "all" - get all demolishable elements
                {
                    // Get common demolishable categories
                    var demoCategories = new List<BuiltInCategory>
                    {
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_Doors,
                        BuiltInCategory.OST_Windows,
                        BuiltInCategory.OST_Floors,
                        BuiltInCategory.OST_Ceilings,
                        BuiltInCategory.OST_Roofs,
                        BuiltInCategory.OST_Stairs,
                        BuiltInCategory.OST_Columns,
                        BuiltInCategory.OST_StructuralColumns,
                        BuiltInCategory.OST_StructuralFraming
                    };

                    foreach (var cat in demoCategories)
                    {
                        var collector = new FilteredElementCollector(doc)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType();
                        elementsToDemo.AddRange(collector);
                    }
                }

                // Filter to only elements that support phasing
                elementsToDemo = elementsToDemo.Where(e =>
                {
                    var param = e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                    return param != null && !param.IsReadOnly;
                }).ToList();

                int successCount = 0;
                int failedCount = 0;
                var failures = new List<string>();
                var demolishedElements = new List<object>();
                ElementId demoViewId = ElementId.InvalidElementId;

                using (var trans = new Transaction(doc, "Setup Demolition Plan"))
                {
                    trans.Start();
                    var failureOptions = trans.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(failureOptions);

                    // Set demolition phase for each element
                    foreach (Element elem in elementsToDemo)
                    {
                        try
                        {
                            var phaseDemoParam = elem.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                            if (phaseDemoParam != null && !phaseDemoParam.IsReadOnly)
                            {
                                phaseDemoParam.Set(demoPhaseId);
                                demolishedElements.Add(new
                                {
                                    elementId = (int)elem.Id.Value,
                                    category = elem.Category?.Name ?? "Unknown",
                                    name = elem.Name
                                });
                                successCount++;
                            }
                            else
                            {
                                failedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"Element {elem.Id.Value}: {ex.Message}");
                            failedCount++;
                        }
                    }

                    // Create demolition view if requested
                    if (createDemoView && successCount > 0)
                    {
                        try
                        {
                            // Find a floor plan view type
                            var viewFamilyTypes = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewFamilyType))
                                .Cast<ViewFamilyType>()
                                .Where(vft => vft.ViewFamily == ViewFamily.FloorPlan)
                                .ToList();

                            if (viewFamilyTypes.Count > 0)
                            {
                                // Get the first level
                                var levels = new FilteredElementCollector(doc)
                                    .OfClass(typeof(Level))
                                    .Cast<Level>()
                                    .OrderBy(l => l.Elevation)
                                    .ToList();

                                if (levels.Count > 0)
                                {
                                    ViewPlan demoView = ViewPlan.Create(doc, viewFamilyTypes[0].Id, levels[0].Id);
                                    demoView.Name = demoViewName;

                                    // Set the view's phase to the demolition phase
                                    var viewPhaseParam = demoView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                                    if (viewPhaseParam != null && !viewPhaseParam.IsReadOnly)
                                    {
                                        viewPhaseParam.Set(demoPhaseId);
                                    }

                                    // Find and set "Show Previous + Demo" phase filter if available
                                    var phaseFilters = new FilteredElementCollector(doc)
                                        .OfClass(typeof(PhaseFilter))
                                        .Cast<PhaseFilter>()
                                        .ToList();

                                    var demoFilter = phaseFilters.FirstOrDefault(pf =>
                                        pf.Name.ToLower().Contains("demo") ||
                                        pf.Name.ToLower().Contains("previous"));

                                    if (demoFilter != null)
                                    {
                                        var viewPhaseFilterParam = demoView.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                                        if (viewPhaseFilterParam != null && !viewPhaseFilterParam.IsReadOnly)
                                        {
                                            viewPhaseFilterParam.Set(demoFilter.Id);
                                        }
                                    }

                                    demoViewId = demoView.Id;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"Demo view creation: {ex.Message}");
                        }
                    }

                    trans.Commit();
                }

                // Categorize demolished elements
                var categoryBreakdown = demolishedElements
                    .GroupBy(e => ((dynamic)e).category.ToString())
                    .Select(g => new { category = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count)
                    .ToList();

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    demolitionPhase = new
                    {
                        phaseId = demoPhaseIdInt,
                        phaseName = demoPhase.Name
                    },
                    scope = scope,
                    successCount = successCount,
                    failedCount = failedCount,
                    categoryBreakdown = categoryBreakdown,
                    demoViewCreated = demoViewId != ElementId.InvalidElementId,
                    demoViewId = demoViewId != ElementId.InvalidElementId ? (int)demoViewId.Value : -1,
                    demoViewName = demoViewId != ElementId.InvalidElementId ? demoViewName : null,
                    failures = failures.Take(10).ToList(),
                    message = $"Set {successCount} elements for demolition in phase '{demoPhase.Name}'"
                });
            }
            catch (Exception ex)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        #endregion
    }
}
