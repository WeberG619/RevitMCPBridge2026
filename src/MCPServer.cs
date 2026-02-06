using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPBridge
{
    public partial class MCPServer
    {
        private CancellationTokenSource _cancellationTokenSource;
        private Task _serverTask;
        private bool _isRunning;
#if REVIT2025
        private readonly string _pipeName = "RevitMCPBridge2025";
#else
        private readonly string _pipeName = "RevitMCPBridge2026";
#endif

        public bool IsRunning => _isRunning;
        public string PipeName => _pipeName;

        public event EventHandler<string> MessageReceived;
        public event EventHandler<string> ErrorOccurred;

        #region Statistics Tracking

        private static long _totalRequests = 0;
        private static long _successfulRequests = 0;
        private static long _failedRequests = 0;
        private static long _totalResponseTimeMs = 0;
        private static long _peakResponseTimeMs = 0;
        private static int _currentConnections = 0;
        private static readonly object _statsLock = new object();

        public static long TotalRequestCount => _totalRequests;
        public static long SuccessfulRequestCount => _successfulRequests;
        public static long FailedRequestCount => _failedRequests;
        public static double AverageResponseTimeMs => _totalRequests > 0 ? (double)_totalResponseTimeMs / _totalRequests : 0;
        public static long PeakResponseTimeMs => _peakResponseTimeMs;
        public static int CurrentConnectionCount => _currentConnections;

        private static void RecordRequest(bool success, long responseTimeMs)
        {
            lock (_statsLock)
            {
                _totalRequests++;
                if (success) _successfulRequests++;
                else _failedRequests++;
                _totalResponseTimeMs += responseTimeMs;
                if (responseTimeMs > _peakResponseTimeMs)
                    _peakResponseTimeMs = responseTimeMs;
            }
        }

        /// <summary>
        /// Get list of all registered MCP methods
        /// </summary>
        public static List<string> GetRegisteredMethods()
        {
            return _methodRegistry.Keys.OrderBy(k => k).ToList();
        }

        #endregion

        /// <summary>
        /// Static method registry for BatchProcessor to execute methods by name.
        /// This allows autonomous batch execution without going through the async MCP pipeline.
        /// </summary>
        private static readonly Dictionary<string, Func<UIApplication, JObject, string>> _methodRegistry =
            new Dictionary<string, Func<UIApplication, JObject, string>>(StringComparer.OrdinalIgnoreCase);

        #region Level 3: Autonomous Intelligence Fields

        /// <summary>
        /// Methods that create or modify elements and should be auto-verified
        /// </summary>
        private static readonly HashSet<string> _verifiableMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Element creation
            "createWall", "createWallByPoints", "createWallsFromPolyline", "batchCreateWalls",
            "createRoom", "createSheet", "createFloorPlan", "createCeilingPlan", "createSection",
            "createElevation", "create3DView", "createLegendView", "createSchedule",
            // Element placement
            "placeFamilyInstance", "placeDoor", "placeWindow", "placeViewOnSheet",
            "placeViewOnSheetSmart", "placeMultipleViewsOnSheet", "placeTextNote",
            "createTag", "tagDoor", "tagRoom", "tagWall", "tagAllByCategory", "tagAllRooms",
            // Element modification
            "modifyWall", "modifyRoom", "modifyDoor", "modifyWindow", "modifyTextNote",
            "moveElements", "rotateElements", "copyElements", "setParameter", "setElementWorkset",
            // Element deletion
            "deleteElement", "deleteElements", "deleteWall", "deleteRoom", "deleteSheet",
            "deleteView", "deleteDoor", "deleteDoorWindow"
        };

        /// <summary>
        /// Methods that benefit from pre-execution correction checking
        /// </summary>
        private static readonly HashSet<string> _correctionCheckMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createWall", "createWallByPoints", "placeDoor", "placeWindow", "placeFamilyInstance",
            "createSheet", "placeViewOnSheet", "createRoom", "tagRoom", "tagDoor",
            "createSchedule", "setParameter"
        };

        /// <summary>
        /// Intelligence components (lazily initialized)
        /// </summary>
        private static CorrectionLearner _correctionLearner;
        private static ResultVerifier _resultVerifier;
        private static WorkflowPlanner _workflowPlanner;

        private static CorrectionLearner CorrectionLearnerInstance => _correctionLearner ?? (_correctionLearner = new CorrectionLearner());
        private static ResultVerifier ResultVerifierInstance => _resultVerifier ?? (_resultVerifier = new ResultVerifier());
        private static WorkflowPlanner WorkflowPlannerInstance => _workflowPlanner ?? (_workflowPlanner = new WorkflowPlanner());
        private static PreferenceLearner PreferenceLearnerInstance => PreferenceLearner.Instance;

        /// <summary>
        /// Tracks current workflow execution context
        /// </summary>
        private static WorkflowPlan _currentWorkflow;
        private static int _currentWorkflowStep;

        #endregion

        /// <summary>
        /// Initialize the method registry with all available methods.
        /// Called once during startup.
        /// </summary>
        public static void InitializeMethodRegistry()
        {
            // Wall Methods (use correct names from WallMethods.cs)
            _methodRegistry["createWallByPoints"] = WallMethods.CreateWallByPoints;
            _methodRegistry["createWall"] = WallMethods.CreateWallByPoints; // Alias for BMO and other internal callers
            _methodRegistry["createWallsFromPolyline"] = WallMethods.CreateWallsFromPolyline;
            _methodRegistry["batchCreateWalls"] = WallMethods.BatchCreateWalls;
            _methodRegistry["getWalls"] = WallMethods.GetWalls;
            _methodRegistry["getWallTypes"] = WallMethods.GetWallTypes;
            _methodRegistry["deleteWall"] = WallMethods.DeleteWall;
            // Wall Endpoint/Extend/Trim Methods (NEW)
            _methodRegistry["setWallEndpoint"] = WallMethods.SetWallEndpoint;
            _methodRegistry["extendWallToElement"] = WallMethods.ExtendWallToElement;
            _methodRegistry["trimWallToElement"] = WallMethods.TrimWallToElement;
            _methodRegistry["extendWallToPoint"] = WallMethods.ExtendWallToPoint;

            // Room Methods
            _methodRegistry["createRoom"] = RoomMethods.CreateRoom;
            _methodRegistry["getRooms"] = RoomMethods.GetRooms;

            // View Methods (use correct names from ViewMethods.cs)
            _methodRegistry["createFloorPlan"] = ViewMethods.CreateFloorPlan;
            _methodRegistry["createCeilingPlan"] = ViewMethods.CreateCeilingPlan;
            _methodRegistry["createSection"] = ViewMethods.CreateSection;
            _methodRegistry["createElevation"] = ViewMethods.CreateElevation;
            _methodRegistry["createDraftingView"] = ViewMethods.CreateDraftingView;
            _methodRegistry["duplicateView"] = ViewMethods.DuplicateView;
            _methodRegistry["renameView"] = ViewMethods.RenameView;
            _methodRegistry["setActiveView"] = ViewMethods.SetActiveView;
            _methodRegistry["getActiveView"] = ViewMethods.GetActiveView;
            _methodRegistry["zoomToFit"] = ViewMethods.ZoomToFit;
            _methodRegistry["zoomToElement"] = ViewMethods.ZoomToElement;
            _methodRegistry["zoomToRegion"] = ViewMethods.ZoomToRegion;
            _methodRegistry["zoomToGridIntersection"] = ViewMethods.ZoomToGridIntersection;

            // Measurement Methods
            _methodRegistry["measureDistance"] = MeasurementMethods.MeasureDistance;
            _methodRegistry["measureBetweenElements"] = MeasurementMethods.MeasureBetweenElements;
            _methodRegistry["getRoomDimensions"] = MeasurementMethods.GetRoomDimensions;
            _methodRegistry["measurePerpendicularToWall"] = MeasurementMethods.MeasurePerpendicularToWall;
            _methodRegistry["measureCorridorWidth"] = MeasurementMethods.MeasureCorridorWidth;

            // Sheet Methods
            _methodRegistry["createSheet"] = SheetMethods.CreateSheet;
            _methodRegistry["getAllSheets"] = SheetMethods.GetAllSheets;
            _methodRegistry["getSheets"] = SheetMethods.GetAllSheets;
            _methodRegistry["placeViewOnSheet"] = SheetMethods.PlaceViewOnSheet;
            _methodRegistry["placeViewOnSheetSmart"] = SheetMethods.PlaceViewOnSheetSmart;
            _methodRegistry["placeViewOnSheetForced"] = SheetMethods.PlaceViewOnSheetForced;
            _methodRegistry["placeMultipleViewsOnSheet"] = SheetMethods.PlaceMultipleViewsOnSheet;
            _methodRegistry["switchToView"] = SheetMethods.SwitchToViewMCP;
            _methodRegistry["switchToSheet"] = SheetMethods.SwitchToViewMCP;
            _methodRegistry["getSheetPrintableArea"] = SheetMethods.GetSheetPrintableAreaMCP;

            // Door/Window Methods
            _methodRegistry["placeDoor"] = DoorWindowMethods.PlaceDoor;
            _methodRegistry["placeWindow"] = DoorWindowMethods.PlaceWindow;
            _methodRegistry["getDoors"] = DoorWindowMethods.GetDoors;
            _methodRegistry["getWindows"] = DoorWindowMethods.GetWindows;

            // System Methods
            _methodRegistry["getVersion"] = SystemMethods.GetVersion;
            _methodRegistry["getConfiguration"] = SystemMethods.GetConfiguration;
            _methodRegistry["healthCheck"] = SystemMethods.HealthCheck;
            _methodRegistry["getStats"] = SystemMethods.GetStats;
            _methodRegistry["reloadConfiguration"] = SystemMethods.ReloadConfiguration;
            _methodRegistry["getMethods"] = SystemMethods.GetMethods;

            // Dialog Handling Methods
            _methodRegistry["setDialogAutoHandle"] = SystemMethods.SetDialogAutoHandle;
            _methodRegistry["getDialogSettings"] = SystemMethods.GetDialogSettings;
            _methodRegistry["getDialogHistory"] = SystemMethods.GetDialogHistory;
            _methodRegistry["clearDialogHistory"] = SystemMethods.ClearDialogHistory;

            // Selection Bridge Methods
            _methodRegistry["getSelection"] = SelectionMethods.GetSelection;
            _methodRegistry["waitForSelection"] = SelectionMethods.WaitForSelection;
            _methodRegistry["selectElements"] = SelectionMethods.SelectElements;
            _methodRegistry["highlightElements"] = SelectionMethods.HighlightElements;
            _methodRegistry["clearSelection"] = SelectionMethods.ClearSelection;
            _methodRegistry["pickPoint"] = SelectionMethods.PickPoint;
            _methodRegistry["pickEdge"] = SelectionMethods.PickEdge;
            _methodRegistry["pickFace"] = SelectionMethods.PickFace;
            _methodRegistry["zoomToElements"] = SelectionMethods.ZoomToElements;

            // UI Automation Methods
            _methodRegistry["executeRevitCommand"] = UIAutomationMethods.ExecuteRevitCommand;
            _methodRegistry["listRevitCommands"] = UIAutomationMethods.ListRevitCommands;
            _methodRegistry["showTaskDialog"] = UIAutomationMethods.ShowTaskDialog;
            _methodRegistry["showInputDialog"] = UIAutomationMethods.ShowInputDialog;
            _methodRegistry["getActiveViewInfo"] = UIAutomationMethods.GetActiveViewInfo;
            _methodRegistry["activateView"] = UIAutomationMethods.ActivateView;
            _methodRegistry["getOpenDocuments"] = UIAutomationMethods.GetOpenDocuments;
            _methodRegistry["refreshActiveView"] = UIAutomationMethods.RefreshActiveView;
            _methodRegistry["getUIState"] = UIAutomationMethods.GetUIState;
            _methodRegistry["closeView"] = UIAutomationMethods.CloseView;
            _methodRegistry["setViewZoom"] = UIAutomationMethods.SetViewZoom;
            _methodRegistry["getSelectionInfo"] = UIAutomationMethods.GetSelectionInfo;
            _methodRegistry["sendKeySequence"] = UIAutomationMethods.SendKeySequence;
            _methodRegistry["getRibbonState"] = UIAutomationMethods.GetRibbonState;
            _methodRegistry["canExecuteCommand"] = UIAutomationMethods.CanExecuteCommand;
            _methodRegistry["clearSelection"] = UIAutomationMethods.ClearSelection;
            _methodRegistry["setSelection"] = UIAutomationMethods.SetSelection;
            _methodRegistry["getPropertiesPaletteState"] = UIAutomationMethods.GetPropertiesPaletteState;
            _methodRegistry["getProjectBrowserState"] = UIAutomationMethods.GetProjectBrowserState;
            _methodRegistry["zoomToSelected"] = UIAutomationMethods.ZoomToSelected;

            // Family Editor Methods
            _methodRegistry["openFamilyForEditing"] = FamilyEditorMethods.OpenFamilyForEditing;
            _methodRegistry["getFamilyDocumentInfo"] = FamilyEditorMethods.GetFamilyDocumentInfo;
            _methodRegistry["addFamilyParameter"] = FamilyEditorMethods.AddFamilyParameter;
            _methodRegistry["addFamilyType"] = FamilyEditorMethods.AddFamilyType;
            _methodRegistry["setFamilyParameterValue"] = FamilyEditorMethods.SetFamilyParameterValue;
            _methodRegistry["addReferencePlane"] = FamilyEditorMethods.AddReferencePlane;
            _methodRegistry["createExtrusion"] = FamilyEditorMethods.CreateExtrusion;
            _methodRegistry["loadFamilyIntoProject"] = FamilyEditorMethods.LoadFamilyIntoProject;
            _methodRegistry["saveFamily"] = FamilyEditorMethods.SaveFamily;
            _methodRegistry["closeFamily"] = FamilyEditorMethods.CloseFamily;
            _methodRegistry["getFamilyCategories"] = FamilyEditorMethods.GetFamilyCategories;
            _methodRegistry["createNewFamily"] = FamilyEditorMethods.CreateNewFamily;
            _methodRegistry["createFamilyModelLine"] = FamilyEditorMethods.CreateModelLine;
            _methodRegistry["createFamilyModelLines"] = FamilyEditorMethods.CreateModelLines;
            _methodRegistry["createFamilySymbolicLine"] = FamilyEditorMethods.CreateSymbolicLine;
            _methodRegistry["createFamilyModelArc"] = FamilyEditorMethods.CreateModelArc;
            _methodRegistry["getFamilyGeometry"] = FamilyEditorMethods.GetFamilyGeometry;
            _methodRegistry["deleteFamilyElement"] = FamilyEditorMethods.DeleteFamilyElement;
            _methodRegistry["associateGeometryWithParameter"] = FamilyEditorMethods.AssociateGeometryWithParameter;
            _methodRegistry["getLineStyles"] = FamilyEditorMethods.GetLineStyles;
            _methodRegistry["setLineStyle"] = FamilyEditorMethods.SetLineStyle;
            _methodRegistry["getElementConstraints"] = FamilyEditorMethods.GetElementConstraints;
            _methodRegistry["addFamilyDimension"] = FamilyEditorMethods.AddDimension;
            _methodRegistry["constrainToReferencePlane"] = FamilyEditorMethods.ConstrainToReferencePlane;
            _methodRegistry["openNestedFamily"] = FamilyEditorMethods.OpenNestedFamily;
            _methodRegistry["getNestedFamilies"] = FamilyEditorMethods.GetNestedFamilies;
            _methodRegistry["moveFamilyElement"] = FamilyEditorMethods.MoveFamilyElement;

            // Document Manager Methods
            _methodRegistry["createNewDocument"] = DocumentMethods.CreateNewDocument;
            _methodRegistry["listProjectTemplates"] = DocumentMethods.ListProjectTemplates;
            _methodRegistry["openDocument"] = DocumentMethods.OpenDocument;
            _methodRegistry["saveDocument"] = DocumentMethods.SaveDocument;
            _methodRegistry["saveAsTemplate"] = DocumentMethods.SaveAsTemplate;
            _methodRegistry["closeDocument"] = DocumentMethods.CloseDocument;
            _methodRegistry["syncWithCentral"] = DocumentMethods.SyncWithCentral;
            _methodRegistry["getDocumentInfo"] = DocumentMethods.GetDocumentInfo;
            _methodRegistry["purgeUnused"] = DocumentMethods.PurgeUnused;
            _methodRegistry["reloadLinks"] = DocumentMethods.ReloadLinks;
            _methodRegistry["setProjectInfo"] = DocumentMethods.SetProjectInfo;
            _methodRegistry["enableWorksharing"] = DocumentMethods.EnableWorksharing;
            _methodRegistry["exportToIFC"] = DocumentMethods.ExportToIFC;
            _methodRegistry["switchDocument"] = DocumentMethods.SwitchDocument;

            // DWG Export Methods (Critical CD Methods)
            _methodRegistry["exportViewsToDWG"] = DocumentMethods.ExportViewsToDWG;
            _methodRegistry["exportSheetsToDWG"] = DocumentMethods.ExportSheetsToDWG;
            _methodRegistry["batchExportDWG"] = DocumentMethods.BatchExportDWG;

            // Room Finish Methods (Critical CD Methods)
            _methodRegistry["createRoomFinishSchedule"] = RoomMethods.CreateRoomFinishSchedule;
            _methodRegistry["getRoomFinishes"] = RoomMethods.GetRoomFinishes;
            _methodRegistry["updateRoomFinishes"] = RoomMethods.UpdateRoomFinishes;

            // Revision Methods (Critical CD Methods)
            _methodRegistry["createRevision"] = DocumentMethods.CreateRevision;
            _methodRegistry["getRevisions"] = DocumentMethods.GetRevisions;
            _methodRegistry["addRevisionToSheets"] = DocumentMethods.AddRevisionToSheets;
            _methodRegistry["removeRevisionFromSheets"] = DocumentMethods.RemoveRevisionFromSheets;
            _methodRegistry["placeRevisionCloud"] = DocumentMethods.PlaceRevisionCloud;
            _methodRegistry["getRevisionClouds"] = DocumentMethods.GetRevisionClouds;
            _methodRegistry["tagRevisionCloud"] = DocumentMethods.TagRevisionCloud;

            // PDF Export Methods (Critical CD Methods)
            _methodRegistry["exportSheetsToPDF"] = DocumentMethods.ExportSheetsToPDF;
            _methodRegistry["batchExportPDF"] = DocumentMethods.BatchExportPDF;
            _methodRegistry["getPrintSettings"] = DocumentMethods.GetPrintSettings;

            // View Template Methods (Tier 1)
            _methodRegistry["getViewTemplates"] = ViewMethods.GetViewTemplates;
            _methodRegistry["applyViewTemplate"] = ViewMethods.ApplyViewTemplate;
            _methodRegistry["createViewTemplate"] = ViewMethods.CreateViewTemplate;
            _methodRegistry["duplicateViewTemplate"] = ViewMethods.DuplicateViewTemplate;

            // Keynote Methods (Tier 1)
            _methodRegistry["placeKeynote"] = RevitMCPBridge2026.AnnotationMethods.PlaceKeynote;
            _methodRegistry["getKeynotesInView"] = RevitMCPBridge2026.AnnotationMethods.GetKeynotesInView;

            // Title Block Methods (Tier 1)
            _methodRegistry["getTitleBlockInfo"] = SheetMethods.GetTitleBlockInfo;
            _methodRegistry["updateTitleBlockParameters"] = SheetMethods.UpdateTitleBlockParameters;
            _methodRegistry["batchUpdateTitleBlocks"] = SheetMethods.BatchUpdateTitleBlocks;

            // Linked Models Methods (Tier 2)
            _methodRegistry["getLinkedModels"] = DocumentMethods.GetLinkedModels;
            _methodRegistry["reloadLinks"] = DocumentMethods.ReloadLinks;
            _methodRegistry["setLinkVisibility"] = DocumentMethods.SetLinkVisibility;
            _methodRegistry["getLinkWorksets"] = DocumentMethods.GetLinkWorksets;

            // Scope Box Methods (Tier 2)
            _methodRegistry["getScopeBoxes"] = ViewMethods.GetScopeBoxes;
            _methodRegistry["createScopeBox"] = ViewMethods.CreateScopeBox;
            _methodRegistry["applyScopeBoxToView"] = ViewMethods.ApplyScopeBoxToView;
            _methodRegistry["removeScopeBoxFromView"] = ViewMethods.RemoveScopeBoxFromView;

            // Design Options Methods removed - DesignOptionSet inaccessible in Revit 2026

            // Legend View Methods (Tier 3)
            _methodRegistry["getLegends"] = ViewMethods.GetLegends;
            _methodRegistry["createLegend"] = ViewMethods.CreateLegend;
            _methodRegistry["getLegendComponents"] = ViewMethods.GetLegendComponents;

            // Assembly Views Methods (Tier 3)
            _methodRegistry["createAssembly"] = ViewMethods.CreateAssembly;
            _methodRegistry["createAssemblyViews"] = ViewMethods.CreateAssemblyViews;
            _methodRegistry["getAssemblies"] = ViewMethods.GetAssemblies;

            // View Transfer Methods
            _methodRegistry["insertViewsFromFile"] = ViewMethods.InsertViewsFromFile;

            // Enhanced Vision Methods
            _methodRegistry["exportViewImage"] = VisionMethods.ExportViewImage;
            _methodRegistry["getViewExtents"] = VisionMethods.GetViewExtents;
            _methodRegistry["setElementVisibility"] = VisionMethods.SetElementVisibility;
            _methodRegistry["setCategoryVisibility"] = VisionMethods.SetCategoryVisibility;
            _methodRegistry["getVisibleElements"] = VisionMethods.GetVisibleElements;
            _methodRegistry["setGraphicsOverride"] = VisionMethods.SetGraphicsOverride;
            _methodRegistry["getViewRange"] = VisionMethods.GetViewRange;
            _methodRegistry["setViewRange"] = VisionMethods.SetViewRange;
            _methodRegistry["isolateElements"] = VisionMethods.IsolateElements;
            _methodRegistry["set3DSectionBox"] = VisionMethods.Set3DSectionBox;

            // Transaction Control Methods
            _methodRegistry["startTransactionGroup"] = TransactionMethods.StartTransactionGroup;
            _methodRegistry["commitTransactionGroup"] = TransactionMethods.CommitTransactionGroup;
            _methodRegistry["rollbackTransactionGroup"] = TransactionMethods.RollbackTransactionGroup;
            _methodRegistry["addCheckpoint"] = TransactionMethods.AddCheckpoint;
            _methodRegistry["getTransactionStatus"] = TransactionMethods.GetTransactionStatus;
            _methodRegistry["getUndoHistory"] = TransactionMethods.GetUndoHistory;
            _methodRegistry["executeWithUndo"] = TransactionMethods.ExecuteWithUndo;
            _methodRegistry["batchExecute"] = TransactionMethods.BatchExecute;
            _methodRegistry["safeExecute"] = TransactionMethods.SafeExecute;
            _methodRegistry["verifyAndRollback"] = TransactionMethods.VerifyAndRollback;

            // Stair/Railing/Ramp Methods (Complete registration)
            _methodRegistry["getStairs"] = StairRailingMethods.GetStairs;
            _methodRegistry["getStairTypes"] = StairRailingMethods.GetStairTypes;
            _methodRegistry["getStairDetails"] = StairRailingMethods.GetStairDetails;
            _methodRegistry["createStairBySketch"] = StairRailingMethods.CreateStairBySketch;
            _methodRegistry["createStairByComponent"] = StairRailingMethods.CreateStairByComponent;
            _methodRegistry["modifyStair"] = StairRailingMethods.ModifyStair;
            _methodRegistry["deleteStair"] = StairRailingMethods.DeleteStair;
            _methodRegistry["getStairRailings"] = StairRailingMethods.GetStairRailings;
            _methodRegistry["getRailings"] = StairRailingMethods.GetRailings;
            _methodRegistry["getRailingTypes"] = StairRailingMethods.GetRailingTypes;
            _methodRegistry["createRailing"] = StairRailingMethods.CreateRailing;
            _methodRegistry["deleteRailing"] = StairRailingMethods.DeleteRailing;
            _methodRegistry["getRamps"] = StairRailingMethods.GetRamps;
            _methodRegistry["getRampTypes"] = StairRailingMethods.GetRampTypes;
            _methodRegistry["createRamp"] = StairRailingMethods.CreateRamp;
            _methodRegistry["deleteRamp"] = StairRailingMethods.DeleteRamp;

            // Area Plan Methods (Critical CD Methods)
            _methodRegistry["createAreaPlan"] = RoomMethods.CreateAreaPlan;
            _methodRegistry["createAreaBoundary"] = RoomMethods.CreateAreaBoundary;
            _methodRegistry["getAreas"] = RoomMethods.GetAreas;
            _methodRegistry["placeArea"] = RoomMethods.PlaceArea;

            // Floor/Ceiling/Roof Creation Methods (for BMO and internal callers)
            _methodRegistry["createFloor"] = FloorCeilingRoofMethods.CreateFloor;
            _methodRegistry["createCeiling"] = FloorCeilingRoofMethods.CreateCeiling;
            _methodRegistry["createRoofByFootprint"] = FloorCeilingRoofMethods.CreateRoofByFootprint;
            _methodRegistry["createFootprintRoof"] = FloorCeilingRoofMethods.CreateRoofByFootprint; // Alias
            _methodRegistry["getFloorTypes"] = FloorCeilingRoofMethods.GetFloorTypes;
            _methodRegistry["getCeilingTypes"] = FloorCeilingRoofMethods.GetCeilingTypes;
            _methodRegistry["getRoofTypes"] = FloorCeilingRoofMethods.GetRoofTypes;

            // Ceiling Enhancements (Critical CD Methods)
            _methodRegistry["createCeilingGrid"] = FloorCeilingRoofMethods.CreateCeilingGrid;
            _methodRegistry["placeLightFixture"] = FloorCeilingRoofMethods.PlaceLightFixture;
            _methodRegistry["tagCeilingHeight"] = FloorCeilingRoofMethods.TagCeilingHeight;

            // Roof Enhancements (Critical CD Methods)
            _methodRegistry["createRoofByExtrusion"] = FloorCeilingRoofMethods.CreateRoofByExtrusion;
            _methodRegistry["addSlopeArrow"] = FloorCeilingRoofMethods.AddSlopeArrow;
            _methodRegistry["modifyRoofSlope"] = FloorCeilingRoofMethods.ModifyRoofSlope;

            // Boundary Editing Methods (Floor/Ceiling/Roof sketch modification)
            _methodRegistry["editFloorBoundary"] = FloorCeilingRoofMethods.EditFloorBoundary;
            _methodRegistry["editCeilingBoundary"] = FloorCeilingRoofMethods.EditCeilingBoundary;
            _methodRegistry["editRoofBoundary"] = FloorCeilingRoofMethods.EditRoofBoundary;
            _methodRegistry["getFloorBoundary"] = FloorCeilingRoofMethods.GetFloorBoundary;
            _methodRegistry["getCeilingBoundary"] = FloorCeilingRoofMethods.GetCeilingBoundary;
            _methodRegistry["getRoofBoundary"] = FloorCeilingRoofMethods.GetRoofBoundary;
            _methodRegistry["attachWallsToRoof"] = FloorCeilingRoofMethods.AttachWallsToRoof;

            // Dimension Enhancements (Critical CD Methods)
            _methodRegistry["batchDimensionWindows"] = DimensioningMethods.BatchDimensionWindows;
            _methodRegistry["autoAlignDimensions"] = DimensioningMethods.AutoAlignDimensions;
            _methodRegistry["createEqualityDimension"] = DimensioningMethods.CreateEqualityDimension;

            // New Dimension String Methods (8 total)
            _methodRegistry["getDimensionTypes"] = DimensioningMethods.GetDimensionTypes;
            _methodRegistry["getDimensionSegments"] = DimensioningMethods.GetDimensionSegments;
            _methodRegistry["createCustomDimensionString"] = DimensioningMethods.CreateCustomDimensionString;
            _methodRegistry["modifyDimensionText"] = DimensioningMethods.ModifyDimensionText;
            _methodRegistry["findDimensionsByElement"] = DimensioningMethods.FindDimensionsByElement;
            _methodRegistry["setDimensionType"] = DimensioningMethods.SetDimensionType;
            _methodRegistry["addSegmentToDimension"] = DimensioningMethods.AddSegmentToDimension;
            _methodRegistry["removeSegmentFromDimension"] = DimensioningMethods.RemoveSegmentFromDimension;

            // ============================================
            // SCHEDULE METHODS (35 methods)
            // ============================================
            _methodRegistry["createSchedule"] = RevitMCPBridge2026.ScheduleMethods.CreateSchedule;
            _methodRegistry["createKeySchedule"] = RevitMCPBridge2026.ScheduleMethods.CreateKeySchedule;
            _methodRegistry["createMaterialTakeoff"] = RevitMCPBridge2026.ScheduleMethods.CreateMaterialTakeoff;
            _methodRegistry["createSheetList"] = RevitMCPBridge2026.ScheduleMethods.CreateSheetList;
            _methodRegistry["addScheduleField"] = RevitMCPBridge2026.ScheduleMethods.AddScheduleField;
            _methodRegistry["removeScheduleField"] = RevitMCPBridge2026.ScheduleMethods.RemoveScheduleField;
            _methodRegistry["reorderScheduleFields"] = RevitMCPBridge2026.ScheduleMethods.ReorderScheduleFields;
            _methodRegistry["getScheduleFields"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleFields;
            _methodRegistry["modifyScheduleField"] = RevitMCPBridge2026.ScheduleMethods.ModifyScheduleField;
            _methodRegistry["getAvailableSchedulableFields"] = RevitMCPBridge2026.ScheduleMethods.GetAvailableSchedulableFields;
            _methodRegistry["addScheduleFilter"] = RevitMCPBridge2026.ScheduleMethods.AddScheduleFilter;
            _methodRegistry["removeScheduleFilter"] = RevitMCPBridge2026.ScheduleMethods.RemoveScheduleFilter;
            _methodRegistry["getScheduleFilters"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleFilters;
            _methodRegistry["filterScheduleByLevel"] = RevitMCPBridge2026.ScheduleMethods.FilterScheduleByLevel;
            _methodRegistry["modifyScheduleFilter"] = RevitMCPBridge2026.ScheduleMethods.ModifyScheduleFilter;
            _methodRegistry["addScheduleSorting"] = RevitMCPBridge2026.ScheduleMethods.AddScheduleSorting;
            _methodRegistry["addScheduleGrouping"] = RevitMCPBridge2026.ScheduleMethods.AddScheduleGrouping;
            _methodRegistry["getScheduleSortGrouping"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleSortGrouping;
            _methodRegistry["removeScheduleSorting"] = RevitMCPBridge2026.ScheduleMethods.RemoveScheduleSorting;
            _methodRegistry["formatScheduleAppearance"] = RevitMCPBridge2026.ScheduleMethods.FormatScheduleAppearance;
            _methodRegistry["setConditionalFormatting"] = RevitMCPBridge2026.ScheduleMethods.SetConditionalFormatting;
            _methodRegistry["setColumnWidth"] = RevitMCPBridge2026.ScheduleMethods.SetColumnWidth;
            _methodRegistry["setFieldAlignment"] = RevitMCPBridge2026.ScheduleMethods.SetFieldAlignment;
            _methodRegistry["getScheduleData"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleData;
            _methodRegistry["exportScheduleToCSV"] = RevitMCPBridge2026.ScheduleMethods.ExportScheduleToCSV;
            _methodRegistry["getScheduleCellValue"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleCellValue;
            _methodRegistry["getScheduleTotals"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleTotals;
            _methodRegistry["modifyScheduleProperties"] = RevitMCPBridge2026.ScheduleMethods.ModifyScheduleProperties;
            _methodRegistry["duplicateSchedule"] = RevitMCPBridge2026.ScheduleMethods.DuplicateSchedule;
            _methodRegistry["addCalculatedField"] = RevitMCPBridge2026.ScheduleMethods.AddCalculatedField;
            _methodRegistry["modifyCalculatedField"] = RevitMCPBridge2026.ScheduleMethods.ModifyCalculatedField;
            _methodRegistry["getAllSchedules"] = RevitMCPBridge2026.ScheduleMethods.GetAllSchedules;
            _methodRegistry["getScheduleInfo"] = RevitMCPBridge2026.ScheduleMethods.GetScheduleInfo;
            _methodRegistry["deleteSchedule"] = RevitMCPBridge2026.ScheduleMethods.DeleteSchedule;
            _methodRegistry["refreshSchedule"] = RevitMCPBridge2026.ScheduleMethods.RefreshSchedule;

            // ============================================
            // PARAMETER METHODS (30 methods)
            // ============================================
            _methodRegistry["createProjectParameter"] = RevitMCPBridge2026.ParameterMethods.CreateProjectParameter;
            _methodRegistry["getProjectParameters"] = RevitMCPBridge2026.ParameterMethods.GetProjectParameters;
            _methodRegistry["modifyProjectParameter"] = RevitMCPBridge2026.ParameterMethods.ModifyProjectParameter;
            _methodRegistry["deleteProjectParameter"] = RevitMCPBridge2026.ParameterMethods.DeleteProjectParameter;
            _methodRegistry["createSharedParameter"] = RevitMCPBridge2026.ParameterMethods.CreateSharedParameter;
            _methodRegistry["loadSharedParameterFile"] = RevitMCPBridge2026.ParameterMethods.LoadSharedParameterFile;
            _methodRegistry["getSharedParameters"] = RevitMCPBridge2026.ParameterMethods.GetSharedParameters;
            _methodRegistry["getSharedParameterDefinitions"] = RevitMCPBridge2026.ParameterMethods.GetSharedParameterDefinitions;
            _methodRegistry["bindSharedParameter"] = RevitMCPBridge2026.ParameterMethods.BindSharedParameter;
            _methodRegistry["getParameterValue"] = RevitMCPBridge2026.ParameterMethods.GetParameterValue;
            _methodRegistry["setParameterValue"] = RevitMCPBridge2026.ParameterMethods.SetParameterValue;
            _methodRegistry["getElementParameters"] = RevitMCPBridge2026.ParameterMethods.GetElementParameters;
            _methodRegistry["setMultipleParameterValues"] = RevitMCPBridge2026.ParameterMethods.SetMultipleParameterValues;
            _methodRegistry["getParameterGroups"] = RevitMCPBridge2026.ParameterMethods.GetParameterGroups;
            _methodRegistry["createParameterGroup"] = RevitMCPBridge2026.ParameterMethods.CreateParameterGroup;
            _methodRegistry["findElementsByParameterValue"] = RevitMCPBridge2026.ParameterMethods.FindElementsByParameterValue;
            _methodRegistry["searchParameters"] = RevitMCPBridge2026.ParameterMethods.SearchParameters;
            _methodRegistry["getParameterDefinition"] = RevitMCPBridge2026.ParameterMethods.GetParameterDefinition;
            _methodRegistry["getParameterTypes"] = RevitMCPBridge2026.ParameterMethods.GetParameterTypes;
            _methodRegistry["createGlobalParameter"] = RevitMCPBridge2026.ParameterMethods.CreateGlobalParameter;
            _methodRegistry["getGlobalParameters"] = RevitMCPBridge2026.ParameterMethods.GetGlobalParameters;
            _methodRegistry["modifyGlobalParameter"] = RevitMCPBridge2026.ParameterMethods.ModifyGlobalParameter;
            _methodRegistry["deleteGlobalParameter"] = RevitMCPBridge2026.ParameterMethods.DeleteGlobalParameter;
            _methodRegistry["setParameterFormula"] = RevitMCPBridge2026.ParameterMethods.SetParameterFormula;
            _methodRegistry["getParameterFormula"] = RevitMCPBridge2026.ParameterMethods.GetParameterFormula;
            _methodRegistry["copyParameterValues"] = RevitMCPBridge2026.ParameterMethods.CopyParameterValues;
            _methodRegistry["setParameterForMultipleElements"] = RevitMCPBridge2026.ParameterMethods.SetParameterForMultipleElements;
            _methodRegistry["parameterExists"] = RevitMCPBridge2026.ParameterMethods.ParameterExists;
            _methodRegistry["getParameterStorageType"] = RevitMCPBridge2026.ParameterMethods.GetParameterStorageType;
            _methodRegistry["bulkSetParameterConditional"] = RevitMCPBridge2026.ParameterMethods.BulkSetParameterConditional;

            // ============================================
            // STRUCTURAL METHODS (26 methods)
            // ============================================
            _methodRegistry["placeStructuralColumn"] = RevitMCPBridge2026.StructuralMethods.PlaceStructuralColumn;
            _methodRegistry["getStructuralColumnInfo"] = RevitMCPBridge2026.StructuralMethods.GetStructuralColumnInfo;
            _methodRegistry["modifyStructuralColumn"] = RevitMCPBridge2026.StructuralMethods.ModifyStructuralColumn;
            _methodRegistry["getStructuralColumnTypes"] = RevitMCPBridge2026.StructuralMethods.GetStructuralColumnTypes;
            _methodRegistry["createStructuralBeam"] = RevitMCPBridge2026.StructuralMethods.CreateStructuralBeam;
            _methodRegistry["getStructuralBeamInfo"] = RevitMCPBridge2026.StructuralMethods.GetStructuralBeamInfo;
            _methodRegistry["modifyStructuralBeam"] = RevitMCPBridge2026.StructuralMethods.ModifyStructuralBeam;
            _methodRegistry["getStructuralBeamTypes"] = RevitMCPBridge2026.StructuralMethods.GetStructuralBeamTypes;
            _methodRegistry["createFoundation"] = RevitMCPBridge2026.StructuralMethods.CreateFoundation;
            _methodRegistry["getFoundationInfo"] = RevitMCPBridge2026.StructuralMethods.GetFoundationInfo;
            _methodRegistry["getFoundationTypes"] = RevitMCPBridge2026.StructuralMethods.GetFoundationTypes;
            _methodRegistry["placeStructuralFraming"] = RevitMCPBridge2026.StructuralMethods.PlaceStructuralFraming;
            _methodRegistry["getStructuralFramingInView"] = RevitMCPBridge2026.StructuralMethods.GetStructuralFramingInView;
            _methodRegistry["createStructuralConnection"] = RevitMCPBridge2026.StructuralMethods.CreateStructuralConnection;
            _methodRegistry["getConnectionInfo"] = RevitMCPBridge2026.StructuralMethods.GetConnectionInfo;
            _methodRegistry["getAnalyticalModel"] = RevitMCPBridge2026.StructuralMethods.GetAnalyticalModel;
            _methodRegistry["setAnalyticalProperties"] = RevitMCPBridge2026.StructuralMethods.SetAnalyticalProperties;
            _methodRegistry["createRebar"] = RevitMCPBridge2026.StructuralMethods.CreateRebar;
            _methodRegistry["getRebarInfo"] = RevitMCPBridge2026.StructuralMethods.GetRebarInfo;
            _methodRegistry["createPointLoad"] = RevitMCPBridge2026.StructuralMethods.CreatePointLoad;
            _methodRegistry["createLineLoad"] = RevitMCPBridge2026.StructuralMethods.CreateLineLoad;
            _methodRegistry["createAreaLoad"] = RevitMCPBridge2026.StructuralMethods.CreateAreaLoad;
            _methodRegistry["getElementLoads"] = RevitMCPBridge2026.StructuralMethods.GetElementLoads;
            _methodRegistry["getAnalysisResults"] = RevitMCPBridge2026.StructuralMethods.GetAnalysisResults;
            _methodRegistry["getAllStructuralElements"] = RevitMCPBridge2026.StructuralMethods.GetAllStructuralElements;
            _methodRegistry["deleteStructuralElement"] = RevitMCPBridge2026.StructuralMethods.DeleteStructuralElement;

            // ============================================
            // MEP METHODS (35 methods)
            // ============================================
            _methodRegistry["createDuct"] = RevitMCPBridge2026.MEPMethods.CreateDuct;
            _methodRegistry["getDuctInfo"] = RevitMCPBridge2026.MEPMethods.GetDuctInfo;
            _methodRegistry["createDuctFitting"] = RevitMCPBridge2026.MEPMethods.CreateDuctFitting;
            _methodRegistry["createDuctAccessory"] = RevitMCPBridge2026.MEPMethods.CreateDuctAccessory;
            _methodRegistry["getDuctsInView"] = RevitMCPBridge2026.MEPMethods.GetDuctsInView;
            _methodRegistry["createPipe"] = RevitMCPBridge2026.MEPMethods.CreatePipe;
            _methodRegistry["getPipeInfo"] = RevitMCPBridge2026.MEPMethods.GetPipeInfo;
            _methodRegistry["createPipeFitting"] = RevitMCPBridge2026.MEPMethods.CreatePipeFitting;
            _methodRegistry["createPipeAccessory"] = RevitMCPBridge2026.MEPMethods.CreatePipeAccessory;
            _methodRegistry["getPipesInView"] = RevitMCPBridge2026.MEPMethods.GetPipesInView;
            _methodRegistry["createCableTray"] = RevitMCPBridge2026.MEPMethods.CreateCableTray;
            _methodRegistry["createConduit"] = RevitMCPBridge2026.MEPMethods.CreateConduit;
            _methodRegistry["getElectricalPathInfo"] = RevitMCPBridge2026.MEPMethods.GetElectricalPathInfo;
            _methodRegistry["placeElectricalFixture"] = RevitMCPBridge2026.MEPMethods.PlaceElectricalFixture;
            _methodRegistry["placeElectricalEquipment"] = RevitMCPBridge2026.MEPMethods.PlaceElectricalEquipment;
            _methodRegistry["getElectricalCircuits"] = RevitMCPBridge2026.MEPMethods.GetElectricalCircuits;
            _methodRegistry["createElectricalCircuit"] = RevitMCPBridge2026.MEPMethods.CreateElectricalCircuit;
            _methodRegistry["placeMechanicalEquipment"] = RevitMCPBridge2026.MEPMethods.PlaceMechanicalEquipment;
            _methodRegistry["placePlumbingFixture"] = RevitMCPBridge2026.MEPMethods.PlacePlumbingFixture;
            _methodRegistry["getEquipmentInfo"] = RevitMCPBridge2026.MEPMethods.GetEquipmentInfo;
            _methodRegistry["createMEPSystem"] = RevitMCPBridge2026.MEPMethods.CreateMEPSystem;
            _methodRegistry["getMEPSystems"] = RevitMCPBridge2026.MEPMethods.GetMEPSystems;
            _methodRegistry["getSystemInfo"] = RevitMCPBridge2026.MEPMethods.GetSystemInfo;
            _methodRegistry["modifySystemElements"] = RevitMCPBridge2026.MEPMethods.ModifySystemElements;
            _methodRegistry["createMEPSpace"] = RevitMCPBridge2026.MEPMethods.CreateMEPSpace;
            _methodRegistry["getSpaceInfo"] = RevitMCPBridge2026.MEPMethods.GetSpaceInfo;
            _methodRegistry["createZone"] = RevitMCPBridge2026.MEPMethods.CreateZone;
            _methodRegistry["tagSpace"] = RevitMCPBridge2026.MEPMethods.TagSpace;
            _methodRegistry["getConnectors"] = RevitMCPBridge2026.MEPMethods.GetConnectors;
            _methodRegistry["connectElements"] = RevitMCPBridge2026.MEPMethods.ConnectElements;
            _methodRegistry["calculateDuctSizing"] = RevitMCPBridge2026.MEPMethods.CalculateDuctSizing;
            _methodRegistry["calculatePipeSizing"] = RevitMCPBridge2026.MEPMethods.CalculatePipeSizing;
            _methodRegistry["calculateLoads"] = RevitMCPBridge2026.MEPMethods.CalculateLoads;
            _methodRegistry["getMEPTypes"] = RevitMCPBridge2026.MEPMethods.GetMEPTypes;
            _methodRegistry["deleteMEPElement"] = RevitMCPBridge2026.MEPMethods.DeleteMEPElement;

            // ============================================
            // FILTER METHODS (27 methods)
            // ============================================
            _methodRegistry["createViewFilter"] = RevitMCPBridge2026.FilterMethods.CreateViewFilter;
            _methodRegistry["getAllViewFilters"] = RevitMCPBridge2026.FilterMethods.GetAllViewFilters;
            _methodRegistry["getViewFilterInfo"] = RevitMCPBridge2026.FilterMethods.GetViewFilterInfo;
            _methodRegistry["modifyViewFilter"] = RevitMCPBridge2026.FilterMethods.ModifyViewFilter;
            _methodRegistry["deleteViewFilter"] = RevitMCPBridge2026.FilterMethods.DeleteViewFilter;
            _methodRegistry["createFilterRule"] = RevitMCPBridge2026.FilterMethods.CreateFilterRule;
            _methodRegistry["addRuleToFilter"] = RevitMCPBridge2026.FilterMethods.AddRuleToFilter;
            _methodRegistry["getFilterRules"] = RevitMCPBridge2026.FilterMethods.GetFilterRules;
            _methodRegistry["removeRuleFromFilter"] = RevitMCPBridge2026.FilterMethods.RemoveRuleFromFilter;
            _methodRegistry["applyFilterToView"] = RevitMCPBridge2026.FilterMethods.ApplyFilterToView;
            _methodRegistry["removeFilterFromView"] = RevitMCPBridge2026.FilterMethods.RemoveFilterFromView;
            _methodRegistry["getFiltersInView"] = RevitMCPBridge2026.FilterMethods.GetFiltersInView;
            _methodRegistry["setFilterOverrides"] = RevitMCPBridge2026.FilterMethods.SetFilterOverrides;
            _methodRegistry["getFilterOverrides"] = RevitMCPBridge2026.FilterMethods.GetFilterOverrides;
            _methodRegistry["selectElementsByFilter"] = RevitMCPBridge2026.FilterMethods.SelectElementsByFilter;
            _methodRegistry["countElementsByFilter"] = RevitMCPBridge2026.FilterMethods.CountElementsByFilter;
            _methodRegistry["createCategoryFilter"] = RevitMCPBridge2026.FilterMethods.CreateCategoryFilter;
            _methodRegistry["getFilterCategories"] = RevitMCPBridge2026.FilterMethods.GetFilterCategories;
            _methodRegistry["addCategoriesToFilter"] = RevitMCPBridge2026.FilterMethods.AddCategoriesToFilter;
            _methodRegistry["removeCategoriesFromFilter"] = RevitMCPBridge2026.FilterMethods.RemoveCategoriesFromFilter;
            _methodRegistry["createFilterFromTemplate"] = RevitMCPBridge2026.FilterMethods.CreateFilterFromTemplate;
            _methodRegistry["duplicateFilter"] = RevitMCPBridge2026.FilterMethods.DuplicateFilter;
            _methodRegistry["findViewsUsingFilter"] = RevitMCPBridge2026.FilterMethods.FindViewsUsingFilter;
            _methodRegistry["testFilter"] = RevitMCPBridge2026.FilterMethods.TestFilter;
            _methodRegistry["analyzeFilter"] = RevitMCPBridge2026.FilterMethods.AnalyzeFilter;
            _methodRegistry["getFilterableParameters"] = RevitMCPBridge2026.FilterMethods.GetFilterableParameters;
            _methodRegistry["validateFilterRules"] = RevitMCPBridge2026.FilterMethods.ValidateFilterRules;

            // ============================================
            // MATERIAL METHODS (27 methods)
            // ============================================
            _methodRegistry["createMaterial"] = RevitMCPBridge2026.MaterialMethods.CreateMaterial;
            _methodRegistry["getAllMaterials"] = RevitMCPBridge2026.MaterialMethods.GetAllMaterials;
            _methodRegistry["getMaterialInfo"] = RevitMCPBridge2026.MaterialMethods.GetMaterialInfo;
            _methodRegistry["modifyMaterial"] = RevitMCPBridge2026.MaterialMethods.ModifyMaterial;
            _methodRegistry["duplicateMaterial"] = RevitMCPBridge2026.MaterialMethods.DuplicateMaterial;
            _methodRegistry["deleteMaterial"] = RevitMCPBridge2026.MaterialMethods.DeleteMaterial;
            _methodRegistry["setMaterialAppearance"] = RevitMCPBridge2026.MaterialMethods.SetMaterialAppearance;
            _methodRegistry["getMaterialAppearance"] = RevitMCPBridge2026.MaterialMethods.GetMaterialAppearance;
            _methodRegistry["setMaterialTexture"] = RevitMCPBridge2026.MaterialMethods.SetMaterialTexture;
            _methodRegistry["setRenderAppearance"] = RevitMCPBridge2026.MaterialMethods.SetRenderAppearance;
            _methodRegistry["setMaterialSurfacePattern"] = RevitMCPBridge2026.MaterialMethods.SetMaterialSurfacePattern;
            _methodRegistry["getMaterialSurfacePattern"] = RevitMCPBridge2026.MaterialMethods.GetMaterialSurfacePattern;
            _methodRegistry["setMaterialPhysicalProperties"] = RevitMCPBridge2026.MaterialMethods.SetMaterialPhysicalProperties;
            _methodRegistry["getMaterialPhysicalProperties"] = RevitMCPBridge2026.MaterialMethods.GetMaterialPhysicalProperties;
            _methodRegistry["getMaterialClasses"] = RevitMCPBridge2026.MaterialMethods.GetMaterialClasses;
            _methodRegistry["setMaterialClass"] = RevitMCPBridge2026.MaterialMethods.SetMaterialClass;
            _methodRegistry["findElementsWithMaterial"] = RevitMCPBridge2026.MaterialMethods.FindElementsWithMaterial;
            _methodRegistry["replaceMaterial"] = RevitMCPBridge2026.MaterialMethods.ReplaceMaterial;
            _methodRegistry["getMaterialUsageStats"] = RevitMCPBridge2026.MaterialMethods.GetMaterialUsageStats;
            _methodRegistry["loadMaterialFromLibrary"] = RevitMCPBridge2026.MaterialMethods.LoadMaterialFromLibrary;
            _methodRegistry["exportMaterial"] = RevitMCPBridge2026.MaterialMethods.ExportMaterial;
            _methodRegistry["searchMaterials"] = RevitMCPBridge2026.MaterialMethods.SearchMaterials;
            _methodRegistry["getAppearanceAssets"] = RevitMCPBridge2026.MaterialMethods.GetAppearanceAssets;
            _methodRegistry["createAppearanceAsset"] = RevitMCPBridge2026.MaterialMethods.CreateAppearanceAsset;
            _methodRegistry["duplicateAppearanceAsset"] = RevitMCPBridge2026.MaterialMethods.DuplicateAppearanceAsset;
            _methodRegistry["modifyAppearanceAssetColor"] = RevitMCPBridge2026.MaterialMethods.ModifyAppearanceAssetColor;
            _methodRegistry["getAppearanceAssetDetails"] = RevitMCPBridge2026.MaterialMethods.GetAppearanceAssetDetails;
            _methodRegistry["createMaterialWithAppearance"] = RevitMCPBridge2026.MaterialMethods.CreateMaterialWithAppearance;
            _methodRegistry["getMaterialByName"] = RevitMCPBridge2026.MaterialMethods.GetMaterialByName;
            _methodRegistry["isMaterialInUse"] = RevitMCPBridge2026.MaterialMethods.IsMaterialInUse;

            // ============================================
            // PHASE METHODS (25 methods)
            // ============================================
            _methodRegistry["createPhase"] = RevitMCPBridge2026.PhaseMethods.CreatePhase;
            _methodRegistry["getAllPhases"] = RevitMCPBridge2026.PhaseMethods.GetAllPhases;
            _methodRegistry["getPhaseInfo"] = RevitMCPBridge2026.PhaseMethods.GetPhaseInfo;
            _methodRegistry["renamePhase"] = RevitMCPBridge2026.PhaseMethods.RenamePhase;
            _methodRegistry["deletePhase"] = RevitMCPBridge2026.PhaseMethods.DeletePhase;
            _methodRegistry["reorderPhases"] = RevitMCPBridge2026.PhaseMethods.ReorderPhases;
            _methodRegistry["setElementPhaseCreated"] = RevitMCPBridge2026.PhaseMethods.SetElementPhaseCreated;
            _methodRegistry["setElementPhaseDemolished"] = RevitMCPBridge2026.PhaseMethods.SetElementPhaseDemolished;
            _methodRegistry["getElementPhasing"] = RevitMCPBridge2026.PhaseMethods.GetElementPhasing;
            _methodRegistry["getElementsInPhase"] = RevitMCPBridge2026.PhaseMethods.GetElementsInPhase;
            _methodRegistry["setBulkElementPhasing"] = RevitMCPBridge2026.PhaseMethods.SetBulkElementPhasing;
            _methodRegistry["createPhaseFilter"] = RevitMCPBridge2026.PhaseMethods.CreatePhaseFilter;
            _methodRegistry["getPhaseFilters"] = RevitMCPBridge2026.PhaseMethods.GetPhaseFilters;
            _methodRegistry["getPhaseFilterInfo"] = RevitMCPBridge2026.PhaseMethods.GetPhaseFilterInfo;
            _methodRegistry["modifyPhaseFilter"] = RevitMCPBridge2026.PhaseMethods.ModifyPhaseFilter;
            _methodRegistry["deletePhaseFilter"] = RevitMCPBridge2026.PhaseMethods.DeletePhaseFilter;
            _methodRegistry["setViewPhase"] = RevitMCPBridge2026.PhaseMethods.SetViewPhase;
            _methodRegistry["setViewPhaseFilter"] = RevitMCPBridge2026.PhaseMethods.SetViewPhaseFilter;
            _methodRegistry["getViewPhaseSettings"] = RevitMCPBridge2026.PhaseMethods.GetViewPhaseSettings;
            _methodRegistry["analyzePhasing"] = RevitMCPBridge2026.PhaseMethods.AnalyzePhasing;
            _methodRegistry["findPhasingConflicts"] = RevitMCPBridge2026.PhaseMethods.FindPhasingConflicts;
            _methodRegistry["getPhaseTransitionReport"] = RevitMCPBridge2026.PhaseMethods.GetPhaseTransitionReport;
            _methodRegistry["getCurrentPhase"] = RevitMCPBridge2026.PhaseMethods.GetCurrentPhase;
            _methodRegistry["copyElementsToPhase"] = RevitMCPBridge2026.PhaseMethods.CopyElementsToPhase;
            _methodRegistry["setupDemolitionPlan"] = RevitMCPBridge2026.PhaseMethods.SetupDemolitionPlan;

            // ============================================
            // WORKSET METHODS (27 methods)
            // ============================================
            _methodRegistry["createWorkset"] = RevitMCPBridge2026.WorksetMethods.CreateWorkset;
            _methodRegistry["getAllWorksets"] = RevitMCPBridge2026.WorksetMethods.GetAllWorksets;
            _methodRegistry["getWorksetInfo"] = RevitMCPBridge2026.WorksetMethods.GetWorksetInfo;
            _methodRegistry["renameWorkset"] = RevitMCPBridge2026.WorksetMethods.RenameWorkset;
            _methodRegistry["deleteWorkset"] = RevitMCPBridge2026.WorksetMethods.DeleteWorkset;
            _methodRegistry["setElementWorkset"] = RevitMCPBridge2026.WorksetMethods.SetElementWorkset;
            _methodRegistry["getElementWorkset"] = RevitMCPBridge2026.WorksetMethods.GetElementWorkset;
            _methodRegistry["getElementsInWorkset"] = RevitMCPBridge2026.WorksetMethods.GetElementsInWorkset;
            _methodRegistry["moveElementsToWorkset"] = RevitMCPBridge2026.WorksetMethods.MoveElementsToWorkset;
            _methodRegistry["setWorksetVisibility"] = RevitMCPBridge2026.WorksetMethods.SetWorksetVisibility;
            _methodRegistry["getWorksetVisibilityInView"] = RevitMCPBridge2026.WorksetMethods.GetWorksetVisibilityInView;
            _methodRegistry["setGlobalWorksetVisibility"] = RevitMCPBridge2026.WorksetMethods.SetGlobalWorksetVisibility;
            _methodRegistry["isWorksetEditable"] = RevitMCPBridge2026.WorksetMethods.IsWorksetEditable;
            _methodRegistry["isElementBorrowed"] = RevitMCPBridge2026.WorksetMethods.IsElementBorrowed;
            _methodRegistry["relinquishOwnership"] = RevitMCPBridge2026.WorksetMethods.RelinquishOwnership;
            _methodRegistry["getCheckoutStatus"] = RevitMCPBridge2026.WorksetMethods.GetCheckoutStatus;
            _methodRegistry["isWorkshared"] = RevitMCPBridge2026.WorksetMethods.IsWorkshared;
            _methodRegistry["getWorksharingOptions"] = RevitMCPBridge2026.WorksetMethods.GetWorksharingOptions;
            _methodRegistry["synchronizeWithCentral"] = RevitMCPBridge2026.WorksetMethods.SynchronizeWithCentral;
            _methodRegistry["reloadLatest"] = RevitMCPBridge2026.WorksetMethods.ReloadLatest;
            _methodRegistry["getSyncHistory"] = RevitMCPBridge2026.WorksetMethods.GetSyncHistory;
            _methodRegistry["getWorksetsByCategory"] = RevitMCPBridge2026.WorksetMethods.GetWorksetsByCategory;
            _methodRegistry["createWorksetNamingScheme"] = RevitMCPBridge2026.WorksetMethods.CreateWorksetNamingScheme;
            _methodRegistry["getActiveWorkset"] = RevitMCPBridge2026.WorksetMethods.GetActiveWorkset;
            _methodRegistry["setActiveWorkset"] = RevitMCPBridge2026.WorksetMethods.SetActiveWorkset;
            _methodRegistry["bulkSetWorksetsByCategory"] = RevitMCPBridge2026.WorksetMethods.BulkSetWorksetsByCategory;

            // ============================================
            // DETAIL METHODS (35 methods)
            // ============================================
            _methodRegistry["createDetailLine"] = RevitMCPBridge2026.DetailMethods.CreateDetailLine;
            _methodRegistry["createDetailArc"] = RevitMCPBridge2026.DetailMethods.CreateDetailArc;
            _methodRegistry["createDetailPolyline"] = RevitMCPBridge2026.DetailMethods.CreateDetailPolyline;
            _methodRegistry["getDetailLineInfo"] = RevitMCPBridge2026.DetailMethods.GetDetailLineInfo;
            _methodRegistry["modifyDetailLine"] = RevitMCPBridge2026.DetailMethods.ModifyDetailLine;
            _methodRegistry["getDetailLinesInView"] = RevitMCPBridge2026.DetailMethods.GetDetailLinesInView;
            _methodRegistry["createFilledRegion"] = RevitMCPBridge2026.DetailMethods.CreateFilledRegion;
            _methodRegistry["getFilledRegionInfo"] = RevitMCPBridge2026.DetailMethods.GetFilledRegionInfo;
            _methodRegistry["modifyFilledRegionBoundary"] = RevitMCPBridge2026.DetailMethods.ModifyFilledRegionBoundary;
            _methodRegistry["getFilledRegionTypes"] = RevitMCPBridge2026.DetailMethods.GetFilledRegionTypes;
            _methodRegistry["getFilledRegionsInView"] = RevitMCPBridge2026.DetailMethods.GetFilledRegionsInView;
            _methodRegistry["placeDetailComponent"] = RevitMCPBridge2026.DetailMethods.PlaceDetailComponent;
            _methodRegistry["placeDetailComponentByName"] = RevitMCPBridge2026.DetailMethods.PlaceDetailComponentByName;
            _methodRegistry["placeRepeatingDetailComponent"] = RevitMCPBridge2026.DetailMethods.PlaceRepeatingDetailComponent;
            _methodRegistry["getDetailComponentInfo"] = RevitMCPBridge2026.DetailMethods.GetDetailComponentInfo;
            _methodRegistry["getDetailComponentTypes"] = RevitMCPBridge2026.DetailMethods.GetDetailComponentTypes;
            _methodRegistry["getDetailComponentsInView"] = RevitMCPBridge2026.DetailMethods.GetDetailComponentsInView;
            _methodRegistry["addInsulation"] = RevitMCPBridge2026.DetailMethods.AddInsulation;
            _methodRegistry["getInsulationInfo"] = RevitMCPBridge2026.DetailMethods.GetInsulationInfo;
            _methodRegistry["removeInsulation"] = RevitMCPBridge2026.DetailMethods.RemoveInsulation;
            _methodRegistry["createBreakLine"] = RevitMCPBridge2026.DetailMethods.CreateBreakLine;
            _methodRegistry["placeMarkerSymbol"] = RevitMCPBridge2026.DetailMethods.PlaceMarkerSymbol;
            _methodRegistry["getLineStyles"] = RevitMCPBridge2026.DetailMethods.GetLineStyles;
            _methodRegistry["createLineStyle"] = RevitMCPBridge2026.DetailMethods.CreateLineStyle;
            _methodRegistry["modifyLineStyle"] = RevitMCPBridge2026.DetailMethods.ModifyLineStyle;
            _methodRegistry["createDetailGroup"] = RevitMCPBridge2026.DetailMethods.CreateDetailGroup;
            _methodRegistry["placeDetailGroup"] = RevitMCPBridge2026.DetailMethods.PlaceDetailGroup;
            _methodRegistry["getDetailGroupTypes"] = RevitMCPBridge2026.DetailMethods.GetDetailGroupTypes;
            _methodRegistry["createMaskingRegion"] = RevitMCPBridge2026.DetailMethods.CreateMaskingRegion;
            _methodRegistry["overrideElementGraphics"] = RevitMCPBridge2026.DetailMethods.OverrideElementGraphics;
            _methodRegistry["getElementGraphicsOverrides"] = RevitMCPBridge2026.DetailMethods.GetElementGraphicsOverrides;
            _methodRegistry["clearElementGraphicsOverrides"] = RevitMCPBridge2026.DetailMethods.ClearElementGraphicsOverrides;
            _methodRegistry["deleteDetailElement"] = RevitMCPBridge2026.DetailMethods.DeleteDetailElement;
            _methodRegistry["copyDetailElements"] = RevitMCPBridge2026.DetailMethods.CopyDetailElements;
            _methodRegistry["createDetailComponentLibrary"] = RevitMCPBridge2026.DetailMethods.CreateDetailComponentLibrary;
            _methodRegistry["extractAndReplaceFilledRegions"] = RevitMCPBridge2026.DetailMethods.ExtractAndReplaceFilledRegions;
            // Drafting view enhancement methods
            _methodRegistry["getBreakLineTypes"] = RevitMCPBridge2026.DetailMethods.GetBreakLineTypes;
            _methodRegistry["placeBreakLineAuto"] = RevitMCPBridge2026.DetailMethods.PlaceBreakLineAuto;
            _methodRegistry["createDetailLineInDraftingView"] = RevitMCPBridge2026.DetailMethods.CreateDetailLineInDraftingView;
            _methodRegistry["dimensionDetailLines"] = RevitMCPBridge2026.DetailMethods.DimensionDetailLines;
            _methodRegistry["getDraftingViewBounds"] = RevitMCPBridge2026.DetailMethods.GetDraftingViewBounds;
            _methodRegistry["placeInsulationPattern"] = RevitMCPBridge2026.DetailMethods.PlaceInsulationPattern;
            _methodRegistry["convertDetailToDraftingView"] = RevitMCPBridge2026.DetailMethods.ConvertDetailToDraftingView;
            _methodRegistry["batchConvertDetailsToDraftingViews"] = RevitMCPBridge2026.DetailMethods.BatchConvertDetailsToDraftingViews;
            _methodRegistry["traceDetailToDraftingView"] = RevitMCPBridge2026.DetailMethods.TraceDetailToDraftingView;
            _methodRegistry["intelligentTraceDetailToDraftingView"] = RevitMCPBridge2026.DetailMethods.IntelligentTraceDetailToDraftingView;
            // Detail Component Family Management methods
            _methodRegistry["getDetailComponentFamilies"] = RevitMCPBridge2026.DetailMethods.GetDetailComponentFamilies;
            _methodRegistry["loadDetailComponentFamily"] = RevitMCPBridge2026.DetailMethods.LoadDetailComponentFamily;
            _methodRegistry["searchLocalDetailFamilies"] = RevitMCPBridge2026.DetailMethods.SearchLocalDetailFamilies;
            _methodRegistry["activateDetailComponentType"] = RevitMCPBridge2026.DetailMethods.ActivateDetailComponentType;
            _methodRegistry["placeDetailComponentAdvanced"] = RevitMCPBridge2026.DetailMethods.PlaceDetailComponentAdvanced;
            // Automated Autodesk Cloud Family Loading
            _methodRegistry["loadAutodeskFamilyAutomated"] = RevitMCPBridge2026.DetailMethods.LoadAutodeskFamilyAutomated;
            _methodRegistry["captureDialogState"] = RevitMCPBridge2026.DetailMethods.CaptureDialogState;
            // Detail Library Methods
            _methodRegistry["getDetailLibrary"] = RevitMCPBridge2026.DetailMethods.GetDetailLibrary;
            _methodRegistry["getDetailsInCategory"] = RevitMCPBridge2026.DetailMethods.GetDetailsInCategory;
            _methodRegistry["importDetailToDocument"] = RevitMCPBridge2026.DetailMethods.ImportDetailToDocument;
            _methodRegistry["searchDetailLibrary"] = RevitMCPBridge2026.DetailMethods.SearchDetailLibrary;

            // ============================================
            // ANNOTATION METHODS (33 methods)
            // ============================================
            _methodRegistry["loadKeynoteFile"] = RevitMCPBridge2026.AnnotationMethods.LoadKeynoteFile;
            _methodRegistry["getKeynoteEntries"] = RevitMCPBridge2026.AnnotationMethods.GetKeynoteEntries;
            _methodRegistry["placeAnnotationSymbol"] = RevitMCPBridge2026.AnnotationMethods.PlaceAnnotationSymbol;
            _methodRegistry["getAnnotationSymbolTypes"] = RevitMCPBridge2026.AnnotationMethods.GetAnnotationSymbolTypes;
            _methodRegistry["createRevisionCloud"] = RevitMCPBridge2026.AnnotationMethods.CreateRevisionCloud;
            _methodRegistry["getRevisionCloudsInView"] = RevitMCPBridge2026.AnnotationMethods.GetRevisionCloudsInView;
            _methodRegistry["modifyRevisionCloud"] = RevitMCPBridge2026.AnnotationMethods.ModifyRevisionCloud;
            _methodRegistry["deleteRevisionCloud"] = RevitMCPBridge2026.AnnotationMethods.DeleteRevisionCloud;
            _methodRegistry["modifyRevision"] = RevitMCPBridge2026.AnnotationMethods.ModifyRevision;
            _methodRegistry["setRevisionIssued"] = RevitMCPBridge2026.AnnotationMethods.SetRevisionIssued;
            _methodRegistry["placeAngularDimension"] = RevitMCPBridge2026.AnnotationMethods.PlaceAngularDimension;
            _methodRegistry["placeRadialDimension"] = RevitMCPBridge2026.AnnotationMethods.PlaceRadialDimension;
            _methodRegistry["placeDiameterDimension"] = RevitMCPBridge2026.AnnotationMethods.PlaceDiameterDimension;
            _methodRegistry["placeArcLengthDimension"] = RevitMCPBridge2026.AnnotationMethods.PlaceArcLengthDimension;
            _methodRegistry["createCallout"] = RevitMCPBridge2026.AnnotationMethods.CreateCallout;
            _methodRegistry["getCalloutsInView"] = RevitMCPBridge2026.AnnotationMethods.GetCalloutsInView;
            _methodRegistry["placeAreaTag"] = RevitMCPBridge2026.AnnotationMethods.PlaceAreaTag;
            _methodRegistry["getAreaTagsInView"] = RevitMCPBridge2026.AnnotationMethods.GetAreaTagsInView;
            _methodRegistry["placeSpotElevation"] = RevitMCPBridge2026.AnnotationMethods.PlaceSpotElevation;
            _methodRegistry["placeSpotCoordinate"] = RevitMCPBridge2026.AnnotationMethods.PlaceSpotCoordinate;
            _methodRegistry["placeSpotSlope"] = RevitMCPBridge2026.AnnotationMethods.PlaceSpotSlope;
            _methodRegistry["createReferencePlane"] = RevitMCPBridge2026.AnnotationMethods.CreateReferencePlane;
            _methodRegistry["getReferencePlanesInView"] = RevitMCPBridge2026.AnnotationMethods.GetReferencePlanesInView;
            _methodRegistry["createMatchline"] = RevitMCPBridge2026.AnnotationMethods.CreateMatchline;
            _methodRegistry["getMatchlinesInView"] = RevitMCPBridge2026.AnnotationMethods.GetMatchlinesInView;
            _methodRegistry["placeLegendComponent"] = RevitMCPBridge2026.AnnotationMethods.PlaceLegendComponent;
            _methodRegistry["getAllAnnotationsInView"] = RevitMCPBridge2026.AnnotationMethods.GetAllAnnotationsInView;
            _methodRegistry["deleteAnnotation"] = RevitMCPBridge2026.AnnotationMethods.DeleteAnnotation;
            _methodRegistry["addAnnotationLeader"] = RevitMCPBridge2026.AnnotationMethods.AddAnnotationLeader;
            _methodRegistry["setLeaderEndpoint"] = RevitMCPBridge2026.AnnotationMethods.SetLeaderEndpoint;
            _methodRegistry["getAnnotationLeaderInfo"] = RevitMCPBridge2026.AnnotationMethods.GetAnnotationLeaderInfo;
            _methodRegistry["placeAnnotationWithLeader"] = RevitMCPBridge2026.AnnotationMethods.PlaceAnnotationWithLeader;
            _methodRegistry["batchPlaceKeynotesWithLeaders"] = RevitMCPBridge2026.AnnotationMethods.BatchPlaceKeynotesWithLeaders;

            // ============================================
            // LEVEL METHODS (10 methods)
            // ============================================
            _methodRegistry["createLevel"] = LevelMethods.CreateLevel;
            _methodRegistry["getLevels"] = LevelMethods.GetLevels;
            _methodRegistry["getLevel"] = LevelMethods.GetLevel;
            _methodRegistry["renameLevel"] = LevelMethods.RenameLevel;
            _methodRegistry["setLevelElevation"] = LevelMethods.SetLevelElevation;
            _methodRegistry["deleteLevel"] = LevelMethods.DeleteLevel;
            _methodRegistry["createLevelArray"] = LevelMethods.CreateLevelArray;
            _methodRegistry["getLevelByElevation"] = LevelMethods.GetLevelByElevation;
            _methodRegistry["getElementsOnLevel"] = LevelMethods.GetElementsOnLevel;
            _methodRegistry["copyElementsToLevel"] = LevelMethods.CopyElementsToLevel;

            // ============================================
            // GRID METHODS (9 methods)
            // ============================================
            _methodRegistry["createGrid"] = GridMethods.CreateGrid;
            _methodRegistry["createArcGrid"] = GridMethods.CreateArcGrid;
            _methodRegistry["getGrids"] = GridMethods.GetGrids;
            _methodRegistry["getGrid"] = GridMethods.GetGrid;
            _methodRegistry["renameGrid"] = GridMethods.RenameGrid;
            _methodRegistry["deleteGrid"] = GridMethods.DeleteGrid;
            _methodRegistry["createGridArray"] = GridMethods.CreateGridArray;
            _methodRegistry["setGridExtents"] = GridMethods.SetGridExtents;
            _methodRegistry["getGridIntersection"] = GridMethods.GetGridIntersection;

            // ============================================
            // ELEMENT METHODS (17 methods)
            // ============================================
            _methodRegistry["getElementLocation"] = ElementMethods.GetElementLocation;
            _methodRegistry["getBoundingBox"] = ElementMethods.GetBoundingBox;
            _methodRegistry["moveElement"] = ElementMethods.MoveElement;
            _methodRegistry["moveElements"] = ElementMethods.MoveElements;
            _methodRegistry["placeFamilyInstance"] = ElementMethods.PlaceFamilyInstance;
            _methodRegistry["getFamilyInstanceTypes"] = ElementMethods.GetFamilyInstanceTypes;
            _methodRegistry["copyElements"] = ElementMethods.CopyElements;
            _methodRegistry["deleteElements"] = ElementMethods.DeleteElements;
            _methodRegistry["deleteElement"] = ElementMethods.DeleteElement;
            _methodRegistry["loadFamily"] = ElementMethods.LoadFamily;
            _methodRegistry["listFamilyFiles"] = ElementMethods.ListFamilyFiles;
            _methodRegistry["getLibraryPaths"] = ElementMethods.GetLibraryPaths;
            _methodRegistry["getLoadedFamilies"] = ElementMethods.GetLoadedFamilies;
            _methodRegistry["openLoadAutodeskFamilyDialog"] = ElementMethods.OpenLoadAutodeskFamilyDialog;
            _methodRegistry["getImportedInstances"] = ElementMethods.GetImportedInstances;
            _methodRegistry["getImportedGeometry"] = ElementMethods.GetImportedGeometry;
            _methodRegistry["getImportedLines"] = ElementMethods.GetImportedLines;
            // Element Transform Methods (NEW)
            _methodRegistry["mirrorElements"] = ElementMethods.MirrorElements;
            _methodRegistry["arrayElements"] = ElementMethods.ArrayElements;
            _methodRegistry["alignElements"] = ElementMethods.AlignElements;
            _methodRegistry["rotateElements"] = ElementMethods.RotateElements;

            // ============================================
            // GROUP METHODS (12 methods)
            // ============================================
            _methodRegistry["getGroups"] = GroupMethods.GetGroups;
            _methodRegistry["getGroupTypes"] = GroupMethods.GetGroupTypes;
            _methodRegistry["getGroupMembers"] = GroupMethods.GetGroupMembers;
            _methodRegistry["createGroup"] = GroupMethods.CreateGroup;
            _methodRegistry["placeGroup"] = GroupMethods.PlaceGroup;
            _methodRegistry["ungroupGroup"] = GroupMethods.UngroupGroup;
            _methodRegistry["deleteGroupType"] = GroupMethods.DeleteGroupType;
            _methodRegistry["renameGroupType"] = GroupMethods.RenameGroupType;
            _methodRegistry["duplicateGroupType"] = GroupMethods.DuplicateGroupType;
            _methodRegistry["getModelGroups"] = GroupMethods.GetModelGroups;
            _methodRegistry["getDetailGroups"] = GroupMethods.GetDetailGroups;
            _methodRegistry["createGroupAndReplicatePattern"] = GroupMethods.CreateGroupAndReplicatePattern;

            // ============================================
            // TAGGING METHODS (9 methods)
            // ============================================
            _methodRegistry["tagDoor"] = TaggingMethods.TagDoor;
            _methodRegistry["tagRoom"] = TaggingMethods.TagRoom;
            _methodRegistry["tagWall"] = TaggingMethods.TagWall;
            _methodRegistry["tagElement"] = TaggingMethods.TagElement;
            _methodRegistry["batchTagDoors"] = TaggingMethods.BatchTagDoors;
            _methodRegistry["batchTagRooms"] = TaggingMethods.BatchTagRooms;
            _methodRegistry["getTagsInView"] = TaggingMethods.GetTagsInView;
            _methodRegistry["deleteTag"] = TaggingMethods.DeleteTag;
            _methodRegistry["getTagInfo"] = TaggingMethods.GetTagInfo;

            // ============================================
            // LINK METHODS (15 methods)
            // ============================================
            _methodRegistry["getRevitLinks"] = LinkMethods.GetRevitLinks;
            _methodRegistry["getRevitLinkTypes"] = LinkMethods.GetRevitLinkTypes;
            _methodRegistry["loadRevitLink"] = LinkMethods.LoadRevitLink;
            _methodRegistry["reloadRevitLink"] = LinkMethods.ReloadRevitLink;
            _methodRegistry["unloadRevitLink"] = LinkMethods.UnloadRevitLink;
            _methodRegistry["deleteRevitLink"] = LinkMethods.DeleteRevitLink;
            _methodRegistry["getCADLinks"] = LinkMethods.GetCADLinks;
            _methodRegistry["importCAD"] = LinkMethods.ImportCAD;
            _methodRegistry["deleteCADLink"] = LinkMethods.DeleteCADLink;
            _methodRegistry["getLinkTransform"] = LinkMethods.GetLinkTransform;
            _methodRegistry["moveLinkInstance"] = LinkMethods.MoveLinkInstance;
            _methodRegistry["getCADGeometry"] = LinkMethods.GetCADGeometry;
            _methodRegistry["getCADWallCandidates"] = LinkMethods.GetCADWallCandidates;
            _methodRegistry["analyzeCADFloorPlan"] = LinkMethods.AnalyzeCADFloorPlan;
            _methodRegistry["queryLinkedElementCoordinates"] = LinkMethods.QueryLinkedElementCoordinates;

            // ============================================
            // REVISION METHODS (13 methods)
            // ============================================
            _methodRegistry["getRevisions"] = RevisionMethods.GetRevisions;
            _methodRegistry["createNewRevision"] = RevisionMethods.CreateRevision;
            _methodRegistry["updateRevision"] = RevisionMethods.UpdateRevision;
            _methodRegistry["issueRevision"] = RevisionMethods.IssueRevision;
            _methodRegistry["deleteRevision"] = RevisionMethods.DeleteRevision;
            _methodRegistry["getRevisionCloudsList"] = RevisionMethods.GetRevisionClouds;
            _methodRegistry["createNewRevisionCloud"] = RevisionMethods.CreateRevisionCloud;
            _methodRegistry["deleteRevisionCloudById"] = RevisionMethods.DeleteRevisionCloud;
            _methodRegistry["getSheetRevisions"] = RevisionMethods.GetSheetRevisions;
            _methodRegistry["addRevisionsToSheet"] = RevisionMethods.AddRevisionsToSheet;
            _methodRegistry["removeRevisionsFromSheet"] = RevisionMethods.RemoveRevisionsFromSheet;
            _methodRegistry["setRevisionVisibility"] = RevisionMethods.SetRevisionVisibility;
            _methodRegistry["reorderRevisions"] = RevisionMethods.ReorderRevisions;

            // ============================================
            // VALIDATION METHODS (16 methods)
            // ============================================
            _methodRegistry["verifyElement"] = ValidationMethods.VerifyElement;
            _methodRegistry["verifyTextContent"] = ValidationMethods.VerifyTextContent;
            _methodRegistry["getViewSnapshot"] = ValidationMethods.GetViewSnapshot;
            _methodRegistry["compareViewState"] = ValidationMethods.CompareViewState;
            _methodRegistry["verifyBatch"] = ValidationMethods.VerifyBatch;
            _methodRegistry["verifyOperation"] = ValidationMethods.VerifyOperation;
            _methodRegistry["verifyElementCount"] = ValidationMethods.VerifyElementCount;
            _methodRegistry["getModelState"] = ValidationMethods.GetModelState;
            _methodRegistry["preFlightCheck"] = ValidationMethods.PreFlightCheck;
            _methodRegistry["detectClashes"] = ValidationMethods.DetectClashes;
            _methodRegistry["runFullClashDetection"] = ValidationMethods.RunFullClashDetection;
            _methodRegistry["createClashView"] = ValidationMethods.CreateClashView;
            _methodRegistry["exportClashReport"] = ValidationMethods.ExportClashReport;
            _methodRegistry["getClashRiskAssessment"] = ValidationMethods.GetClashRiskAssessment;
            _methodRegistry["validateModelHealth"] = ValidationMethods.ValidateModelHealth;
            _methodRegistry["autoPlaceKeynotes"] = ValidationMethods.AutoPlaceKeynotes;
            _methodRegistry["generateLegendFromTypes"] = ValidationMethods.GenerateLegendFromTypes;
            _methodRegistry["exportConstructionData"] = ValidationMethods.ExportConstructionData;
            _methodRegistry["optimizeFurnitureLayout"] = ValidationMethods.OptimizeFurnitureLayout;
            _methodRegistry["validateElementSpacingAndAlignment"] = ValidationMethods.ValidateElementSpacingAndAlignment;

            // ============================================
            // SITE METHODS (11 methods)
            // ============================================
            _methodRegistry["getTopographySurfaces"] = SiteMethods.GetTopographySurfaces;
            _methodRegistry["createTopography"] = SiteMethods.CreateTopography;
            _methodRegistry["getTopographyPoints"] = SiteMethods.GetTopographyPoints;
            _methodRegistry["addTopographyPoints"] = SiteMethods.AddTopographyPoints;
            _methodRegistry["deleteTopography"] = SiteMethods.DeleteTopography;
            _methodRegistry["getBuildingPads"] = SiteMethods.GetBuildingPads;
            _methodRegistry["createBuildingPad"] = SiteMethods.CreateBuildingPad;
            _methodRegistry["getPropertyLines"] = SiteMethods.GetPropertyLines;
            _methodRegistry["createPropertyLine"] = SiteMethods.CreatePropertyLine;
            _methodRegistry["getSiteInfo"] = SiteMethods.GetSiteInfo;
            _methodRegistry["setSiteLocation"] = SiteMethods.SetSiteLocation;

            // ============================================
            // COMPLIANCE METHODS (10 methods)
            // ============================================
            _methodRegistry["runComplianceCheck"] = ComplianceMethods.RunComplianceCheck;
            _methodRegistry["checkCorridorWidths"] = ComplianceMethods.CheckCorridorWidths;
            _methodRegistry["checkDoorWidths"] = ComplianceMethods.CheckDoorWidths;
            _methodRegistry["checkRoomAreas"] = ComplianceMethods.CheckRoomAreas;
            _methodRegistry["checkToiletClearances"] = ComplianceMethods.CheckToiletClearances;
            _methodRegistry["checkDoorSwing"] = ComplianceMethods.CheckDoorSwing;
            _methodRegistry["checkStairDimensions"] = ComplianceMethods.CheckStairDimensions;
            _methodRegistry["checkCeilingHeights"] = ComplianceMethods.CheckCeilingHeights;
            _methodRegistry["checkWallFireRatings"] = ComplianceMethods.CheckWallFireRatings;
            _methodRegistry["generateComplianceReport"] = ComplianceMethods.GenerateComplianceReport;

            // ============================================
            // CD CHECKLIST METHODS (1 method)
            // ============================================
            _methodRegistry["runCDChecklist"] = CDChecklistMethods.RunCDChecklist;

            // ============================================
            // VIEW ANNOTATION METHODS (47 methods)
            // ============================================
            _methodRegistry["getViewCropRegion"] = ViewAnnotationMethods.GetViewCropRegion;
            _methodRegistry["expandViewCropRegion"] = ViewAnnotationMethods.ExpandViewCropRegion;
            _methodRegistry["getTextNoteLocation"] = ViewAnnotationMethods.GetTextNoteLocation;
            _methodRegistry["createTextNoteInCrop"] = ViewAnnotationMethods.CreateTextNoteInCrop;
            _methodRegistry["createLinearDimension"] = ViewAnnotationMethods.CreateLinearDimension;
            _methodRegistry["createTextNoteWithLeader"] = ViewAnnotationMethods.CreateTextNoteWithLeader;
            _methodRegistry["moveTextNote"] = ViewAnnotationMethods.MoveTextNote;
            _methodRegistry["getTextNoteTypes"] = ViewAnnotationMethods.GetTextNoteTypes;
            _methodRegistry["addLeaderToTextNote"] = ViewAnnotationMethods.AddLeaderToTextNote;
            _methodRegistry["setTextNoteLeaderEndpoint"] = ViewAnnotationMethods.SetTextNoteLeaderEndpoint;
            _methodRegistry["resetTextNoteLeaders"] = ViewAnnotationMethods.ResetTextNoteLeaders;
            _methodRegistry["getTextNotePositions"] = ViewAnnotationMethods.GetTextNotePositions;
            _methodRegistry["alignTextNotes"] = ViewAnnotationMethods.AlignTextNotes;
            _methodRegistry["createTextNoteAlignedWith"] = ViewAnnotationMethods.CreateTextNoteAlignedWith;
            _methodRegistry["getDetailComponentFamilies"] = ViewAnnotationMethods.GetDetailComponentFamilies;
            _methodRegistry["getDetailComponentTypesVA"] = ViewAnnotationMethods.GetDetailComponentTypes;
            _methodRegistry["placeDetailComponentVA"] = ViewAnnotationMethods.PlaceDetailComponent;
            _methodRegistry["getDetailComponentsInViewVA"] = ViewAnnotationMethods.GetDetailComponentsInView;
            _methodRegistry["moveDetailComponent"] = ViewAnnotationMethods.MoveDetailComponent;
            _methodRegistry["rotateDetailComponent"] = ViewAnnotationMethods.RotateDetailComponent;
            _methodRegistry["createDetailLineVA"] = ViewAnnotationMethods.CreateDetailLine;
            _methodRegistry["getLineStylesVA"] = ViewAnnotationMethods.GetLineStyles;
            _methodRegistry["createDetailArcVA"] = ViewAnnotationMethods.CreateDetailArc;
            _methodRegistry["getFilledRegionTypesVA"] = ViewAnnotationMethods.GetFilledRegionTypes;
            _methodRegistry["createFilledRegionVA"] = ViewAnnotationMethods.CreateFilledRegion;
            _methodRegistry["getWallTypeLayers"] = ViewAnnotationMethods.GetWallTypeLayers;
            _methodRegistry["getRoofTypeLayers"] = ViewAnnotationMethods.GetRoofTypeLayers;
            _methodRegistry["getFloorTypeLayers"] = ViewAnnotationMethods.GetFloorTypeLayers;
            _methodRegistry["getElementBoundingBoxInView"] = ViewAnnotationMethods.GetElementBoundingBoxInView;
            _methodRegistry["createFilledRegionFromPoints"] = ViewAnnotationMethods.CreateFilledRegionFromPoints;
            _methodRegistry["getFilledRegionsInViewVA"] = ViewAnnotationMethods.GetFilledRegionsInView;
            _methodRegistry["getDetailLinesInViewVA"] = ViewAnnotationMethods.GetDetailLinesInView;
            _methodRegistry["createDetailLinesFromPoints"] = ViewAnnotationMethods.CreateDetailLinesFromPoints;
            _methodRegistry["analyzeDetailView"] = ViewAnnotationMethods.AnalyzeDetailView;
            _methodRegistry["getSectionCutGeometry"] = ViewAnnotationMethods.GetSectionCutGeometry;
            _methodRegistry["scaleDetailViewElements"] = ViewAnnotationMethods.ScaleDetailViewElements;
            _methodRegistry["mirrorDetailViewElements"] = ViewAnnotationMethods.MirrorDetailViewElements;
            _methodRegistry["rotateDetailViewElements"] = ViewAnnotationMethods.RotateDetailViewElements;
            _methodRegistry["alignElements"] = ViewAnnotationMethods.AlignElements;
            _methodRegistry["distributeElements"] = ViewAnnotationMethods.DistributeElements;
            _methodRegistry["findDuplicateElements"] = ViewAnnotationMethods.FindDuplicateElements;
            _methodRegistry["autoTagUntagged"] = ViewAnnotationMethods.AutoTagUntagged;
            _methodRegistry["offsetDetailCurves"] = ViewAnnotationMethods.OffsetDetailCurves;
            _methodRegistry["conditionalParameterUpdate"] = ViewAnnotationMethods.ConditionalParameterUpdate;
            _methodRegistry["batchUpdateTitleblocks"] = ViewAnnotationMethods.BatchUpdateTitleblocks;
            _methodRegistry["batchSwapTypes"] = ViewAnnotationMethods.BatchSwapTypes;
            _methodRegistry["matchElementProperties"] = ViewAnnotationMethods.MatchElementProperties;
            _methodRegistry["detectElementClashes"] = ViewAnnotationMethods.DetectElementClashes;
            _methodRegistry["createViewsFromRooms"] = ViewAnnotationMethods.CreateViewsFromRooms;
            _methodRegistry["propagateParameterValues"] = ViewAnnotationMethods.PropagateParameterValues;

            // ============================================
            // INTELLIGENCE METHODS (46 methods)
            // ============================================
            _methodRegistry["getSuggestions"] = IntelligenceMethods.GetSuggestions;
            _methodRegistry["getAssistanceSummary"] = IntelligenceMethods.GetAssistanceSummary;
            _methodRegistry["getTaskAssistance"] = IntelligenceMethods.GetTaskAssistance;
            _methodRegistry["respondToSuggestion"] = IntelligenceMethods.RespondToSuggestion;
            _methodRegistry["getWorkflowStatistics"] = IntelligenceMethods.GetWorkflowStatistics;
            _methodRegistry["getWorkflowPatterns"] = IntelligenceMethods.GetWorkflowPatterns;
            _methodRegistry["getActionFrequencies"] = IntelligenceMethods.GetActionFrequencies;
            _methodRegistry["startTaskTracking"] = IntelligenceMethods.StartTaskTracking;
            _methodRegistry["endTaskTracking"] = IntelligenceMethods.EndTaskTracking;
            _methodRegistry["getLearnedPreferences"] = IntelligenceMethods.GetLearnedPreferences;
            _methodRegistry["getPreferredScale"] = IntelligenceMethods.GetPreferredScale;
            _methodRegistry["exportPreferencesForMemory"] = IntelligenceMethods.ExportPreferencesForMemory;
            _methodRegistry["getSheetLayoutRecommendation"] = IntelligenceMethods.GetSheetLayoutRecommendation;
            _methodRegistry["getRecommendedScale"] = IntelligenceMethods.GetRecommendedScale;
            _methodRegistry["suggestViewportPosition"] = IntelligenceMethods.SuggestViewportPosition;
            _methodRegistry["getLearningReport"] = IntelligenceMethods.GetLearningReport;
            _methodRegistry["exportWorkflowData"] = IntelligenceMethods.ExportWorkflowData;
            _methodRegistry["storeCorrection"] = IntelligenceMethods.StoreCorrection;
            _methodRegistry["getRelevantCorrections"] = IntelligenceMethods.GetRelevantCorrections;
            _methodRegistry["getMethodCorrections"] = IntelligenceMethods.GetMethodCorrections;
            _methodRegistry["getCorrectionStats"] = IntelligenceMethods.GetCorrectionStats;
            _methodRegistry["getCorrectionsAsKnowledge"] = IntelligenceMethods.GetCorrectionsAsKnowledge;
            _methodRegistry["markCorrectionApplied"] = IntelligenceMethods.MarkCorrectionApplied;
            _methodRegistry["deleteCorrection"] = IntelligenceMethods.DeleteCorrection;
            _methodRegistry["verifyOperationResult"] = IntelligenceMethods.VerifyOperationResult;
            _methodRegistry["analyzeWorkflowRequest"] = IntelligenceMethods.AnalyzeWorkflowRequest;
            _methodRegistry["getWorkflowTemplates"] = IntelligenceMethods.GetWorkflowTemplates;
            _methodRegistry["setProjectContext"] = IntelligenceMethods.SetProjectContext;
            _methodRegistry["getProjectContext"] = IntelligenceMethods.GetProjectContext;
            _methodRegistry["exportAllIntelligence"] = IntelligenceMethods.ExportAllIntelligence;
            _methodRegistry["getSessionSummaryForMemory"] = IntelligenceMethods.GetSessionSummaryForMemory;
            _methodRegistry["importPreferencesFromMemory"] = IntelligenceMethods.ImportPreferencesFromMemory;
            _methodRegistry["runProactiveAnalysis"] = IntelligenceMethods.RunProactiveAnalysis;
            _methodRegistry["getMonitorSuggestions"] = IntelligenceMethods.GetMonitorSuggestions;
            _methodRegistry["executeSuggestion"] = IntelligenceMethods.ExecuteSuggestion;
            _methodRegistry["takeModelSnapshot"] = IntelligenceMethods.TakeModelSnapshot;
            _methodRegistry["getUnplacedViews"] = IntelligenceMethods.GetUnplacedViews;
            _methodRegistry["getUntaggedRooms"] = IntelligenceMethods.GetUntaggedRooms;
            _methodRegistry["getAutoCorrections"] = IntelligenceMethods.GetAutoCorrections;
            _methodRegistry["dismissSuggestion"] = IntelligenceMethods.DismissSuggestion;
            _methodRegistry["cancelTask"] = IntelligenceMethods.CancelTask;
            _methodRegistry["getActiveTasks"] = IntelligenceMethods.GetActiveTasks;
            _methodRegistry["getTaskResult"] = IntelligenceMethods.GetTaskResult;
            _methodRegistry["configureAutonomy"] = IntelligenceMethods.ConfigureAutonomy;
            _methodRegistry["getAutonomyStats"] = IntelligenceMethods.GetAutonomyStats;
            _methodRegistry["getSupportedGoals"] = IntelligenceMethods.GetSupportedGoals;

            // ============================================
            // PROJECT ANALYSIS METHODS (12 methods)
            // ============================================
            _methodRegistry["analyzeProjectState"] = ProjectAnalysisMethods.AnalyzeProjectState;
            _methodRegistry["getSheetViewMatrix"] = ProjectAnalysisMethods.GetSheetViewMatrix;
            _methodRegistry["getViewPlacementStatus"] = ProjectAnalysisMethods.GetViewPlacementStatus;
            _methodRegistry["getLevelViewCoverage"] = ProjectAnalysisMethods.GetLevelViewCoverage;
            _methodRegistry["detectNamingPatterns"] = ProjectAnalysisMethods.DetectNamingPatterns;
            _methodRegistry["analyzeProjectGaps"] = ProjectAnalysisMethods.AnalyzeProjectGaps;
            _methodRegistry["extractProjectDataMatrix"] = ProjectAnalysisMethods.ExtractProjectDataMatrix;
            _methodRegistry["analyzeCirculationPatterns"] = ProjectAnalysisMethods.AnalyzeCirculationPatterns;
            _methodRegistry["validateSpaceEfficiency"] = ProjectAnalysisMethods.ValidateSpaceEfficiency;
            _methodRegistry["extractBuildingEnvelopeMetrics"] = ProjectAnalysisMethods.ExtractBuildingEnvelopeMetrics;
            _methodRegistry["extractAcousticProperties"] = ProjectAnalysisMethods.ExtractAcousticProperties;
            _methodRegistry["extractDaylightingAnalysis"] = ProjectAnalysisMethods.ExtractDaylightingAnalysis;

            // ============================================
            // RENDER METHODS (7 methods)
            // ============================================
            _methodRegistry["submitRender"] = RenderMethods.SubmitRender;
            _methodRegistry["getRenderStatus"] = RenderMethods.GetRenderStatus;
            _methodRegistry["getRenderResult"] = RenderMethods.GetRenderResult;
            _methodRegistry["listRenderJobs"] = RenderMethods.ListRenderJobs;
            _methodRegistry["cancelRender"] = RenderMethods.CancelRender;
            _methodRegistry["getRenderPresets"] = RenderMethods.GetRenderPresets;
            _methodRegistry["captureAndRender"] = RenderMethods.CaptureAndRender;

            // ============================================
            // VIEWPORT CAPTURE METHODS (7 methods - AnalyzeView has incompatible signature)
            // ============================================
            _methodRegistry["captureViewport"] = ViewportCaptureMethods.CaptureViewport;
            _methodRegistry["captureViewportToBase64"] = ViewportCaptureMethods.CaptureViewportToBase64;
            _methodRegistry["setCamera"] = ViewportCaptureMethods.SetCamera;
            _methodRegistry["getCamera"] = ViewportCaptureMethods.GetCamera;
            _methodRegistry["setViewStyle"] = ViewportCaptureMethods.SetViewStyle;
            _methodRegistry["listViews"] = ViewportCaptureMethods.ListViews;
            _methodRegistry["create3DView"] = ViewportCaptureMethods.Create3DView;

            // ============================================
            // SCENE ANALYSIS METHODS (4 methods)
            // ============================================
            _methodRegistry["getVisibleElements"] = SceneAnalysisMethods.GetVisibleElements;
            _methodRegistry["getViewMaterials"] = SceneAnalysisMethods.GetViewMaterials;
            _methodRegistry["getSceneDescription"] = SceneAnalysisMethods.GetSceneDescription;
            _methodRegistry["exportViewForRender"] = SceneAnalysisMethods.ExportViewForRender;

            // ============================================
            // CAPABILITY SYSTEM METHODS (11 methods)
            // ============================================
            _methodRegistry["classifyFailure"] = CapabilityMethods.ClassifyFailure;
            _methodRegistry["proposeToolSpec"] = CapabilityMethods.ProposeToolSpec;
            _methodRegistry["approveToolSpec"] = CapabilityMethods.ApproveToolSpec;
            _methodRegistry["listToolSpecs"] = CapabilityMethods.ListToolSpecs;
            _methodRegistry["getToolSpec"] = CapabilityMethods.GetToolSpec;
            _methodRegistry["getMethodRegistry"] = CapabilityMethods.GetMethodRegistry;
            _methodRegistry["rebuildMethodRegistry"] = CapabilityMethods.RebuildMethodRegistry;
            _methodRegistry["createTestArtifact"] = CapabilityMethods.CreateTestArtifact;
            _methodRegistry["runTest"] = CapabilityMethods.RunTest;
            _methodRegistry["listTests"] = CapabilityMethods.ListTests;
            _methodRegistry["getCapabilityStatus"] = CapabilityMethods.GetCapabilityStatus;

            // ============================================
            // FIRM PROFILE METHODS (11 methods)
            // Universal template with switchable firm standards
            // ============================================
            _methodRegistry["listProfiles"] = FirmProfileMethods.ListProfiles;
            _methodRegistry["getProfile"] = FirmProfileMethods.GetProfile;
            _methodRegistry["getCurrentProfile"] = FirmProfileMethods.GetCurrentProfile;
            _methodRegistry["setProfile"] = FirmProfileMethods.SetProfile;
            _methodRegistry["detectProfile"] = FirmProfileMethods.DetectProfile;
            _methodRegistry["getProfileSetting"] = FirmProfileMethods.GetProfileSetting;
            _methodRegistry["getTextTypeForProfile"] = FirmProfileMethods.GetTextTypeForProfile;
            _methodRegistry["getDimensionTypeForProfile"] = FirmProfileMethods.GetDimensionTypeForProfile;
            _methodRegistry["getSheetNumberFormat"] = FirmProfileMethods.GetSheetNumberFormat;
            _methodRegistry["getViewNamingConvention"] = FirmProfileMethods.GetViewNamingConvention;
            _methodRegistry["refreshProfiles"] = FirmProfileMethods.RefreshProfiles;

            // Batch Text Methods - for standardizing text across detail library
            try
            {
                Log.Information("[MCPServer] Registering BatchTextMethods...");
                _methodRegistry["getTextTypes"] = RevitMCPBridge2026.BatchTextMethods.GetTextTypes;
                _methodRegistry["createStandardTextType"] = RevitMCPBridge2026.BatchTextMethods.CreateStandardTextType;
                _methodRegistry["getTextNotes"] = RevitMCPBridge2026.BatchTextMethods.GetTextNotes;
                _methodRegistry["standardizeDocumentText"] = RevitMCPBridge2026.BatchTextMethods.StandardizeDocumentText;
                _methodRegistry["standardizeDimensionText"] = RevitMCPBridge2026.BatchTextMethods.StandardizeDimensionText;
                _methodRegistry["renameDimensionTypes"] = RevitMCPBridge2026.BatchTextMethods.RenameDimensionTypes;
                _methodRegistry["standardizeTextNoteTypes"] = RevitMCPBridge2026.BatchTextMethods.StandardizeTextNoteTypes;
                _methodRegistry["processDetailFile"] = RevitMCPBridge2026.BatchTextMethods.ProcessDetailFile;
                _methodRegistry["getDetailLibraryFiles"] = RevitMCPBridge2026.BatchTextMethods.GetDetailLibraryFiles;
                _methodRegistry["getNextFileToProcess"] = RevitMCPBridge2026.BatchTextMethods.GetNextFileToProcess;
                _methodRegistry["markFileProcessed"] = RevitMCPBridge2026.BatchTextMethods.MarkFileProcessed;
                Log.Information("[MCPServer] BatchTextMethods registered successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MCPServer] FAILED to register BatchTextMethods: {Message}", ex.Message);
                // Log inner exception if present
                if (ex.InnerException != null)
                {
                    Log.Error(ex.InnerException, "[MCPServer] Inner exception: {Message}", ex.InnerException.Message);
                }
            }

            Log.Information($"[MCPServer] Method registry initialized with {_methodRegistry.Count} methods for batch execution");

            // Register CIPS methods if enabled
            try
            {
                Log.Information("[MCPServer] About to register CIPS methods...");
                CIPS.CIPSMethods.RegisterIfEnabled(_methodRegistry);
                Log.Information("[MCPServer] CIPS registration complete");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MCPServer] CIPS registration skipped: {Message}", ex.Message);
            }

            // Add diagnostic method to check CIPS status
            _methodRegistry["cips_diagnostic"] = (uiApp, parameters) =>
            {
                try
                {
                    var assemblyDir = System.IO.Path.GetDirectoryName(typeof(CIPS.CIPSConfiguration).Assembly.Location);
                    var configPath = System.IO.Path.Combine(assemblyDir, "appsettings.json");
                    var configExists = System.IO.File.Exists(configPath);
                    var enabled = CIPS.CIPSConfiguration.Instance.Enabled;

                    return Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = true,
                        assemblyDir = assemblyDir,
                        configPath = configPath,
                        configExists = configExists,
                        cipsEnabled = enabled
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
            };

            // Add method to list all CIPS methods in registry
            _methodRegistry["debug_listCipsMethods"] = (uiApp, parameters) =>
            {
                var cipsMethods = _methodRegistry.Keys
                    .Where(k => k.StartsWith("cips_"))
                    .OrderBy(k => k)
                    .ToList();
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = cipsMethods.Count,
                    methods = cipsMethods
                });
            };

            // Register Building Model Orchestration methods
            try
            {
                Log.Information("[MCPServer] About to register BuildingModel methods...");
                BuildingModel.BuildingModelMethods.Register(_methodRegistry);
                Log.Information("[MCPServer] BuildingModel methods registered successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MCPServer] BuildingModel registration FAILED: {Message}", ex.Message);
                if (ex.InnerException != null)
                {
                    Log.Error("[MCPServer] Inner exception: {Inner}", ex.InnerException.Message);
                }
            }

            // ============================================
            // SMART TEMPLATE METHODS (12 methods)
            // ============================================
            try
            {
                _methodRegistry["getSelectedElementSmartInfo"] = SmartTemplateMethods.GetSelectedElementSmartInfo;
                _methodRegistry["getSmartElementInfo"] = SmartTemplateMethods.GetSmartElementInfo;
                _methodRegistry["getSmartTypeInfo"] = SmartTemplateMethods.GetSmartTypeInfo;
                _methodRegistry["getLinkedViews"] = SmartTemplateMethods.GetLinkedViews;
                _methodRegistry["navigateToLinkedView"] = SmartTemplateMethods.NavigateToLinkedView;
                _methodRegistry["getAllSmartTypes"] = SmartTemplateMethods.GetAllSmartTypes;
                _methodRegistry["searchSmartTypes"] = SmartTemplateMethods.SearchSmartTypes;
                _methodRegistry["updateSmartParameters"] = SmartTemplateMethods.UpdateSmartParameters;
                _methodRegistry["bulkUpdateSmartParameters"] = SmartTemplateMethods.BulkUpdateSmartParameters;
                _methodRegistry["openLinkedSchedule"] = SmartTemplateMethods.OpenLinkedSchedule;
                _methodRegistry["getTypeResources"] = SmartTemplateMethods.GetTypeResources;
                _methodRegistry["bindSmartParameters"] = SmartTemplateMethods.BindSmartParameters;
                _methodRegistry["bindSmartParametersFromFile"] = SmartTemplateMethods.BindSmartParametersFromFile;
                Log.Information("[MCPServer] SmartTemplate methods registered (13 methods)");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MCPServer] SmartTemplate registration FAILED: {Message}", ex.Message);
            }

            // Direct test method to verify registration works
            _methodRegistry["bmo_test"] = (uiApp, parameters) =>
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new { success = true, message = "Direct BMO test works!" });
            };
            Log.Information("[MCPServer] Direct bmo_test method registered");
        }

        // Static reference for verification callbacks (set during batch execution)
        private static UIApplication _currentUiApp;

        /// <summary>
        /// Execute a method by name without requiring UIApplication parameter.
        /// Uses the stored reference from the current batch execution context.
        /// </summary>
        public static string ExecuteMethodDirect(string methodName, JObject parameters)
        {
            if (_currentUiApp == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "No UIApplication context available. ExecuteMethodDirect can only be called during batch execution."
                });
            }
            return ExecuteMethod(_currentUiApp, methodName, parameters);
        }

        /// <summary>
        /// Set the current UIApplication for callbacks during batch execution
        /// </summary>
        public static void SetCurrentContext(UIApplication uiApp)
        {
            _currentUiApp = uiApp;
        }

        /// <summary>
        /// Check if a method exists in the registry.
        /// </summary>
        public static bool HasMethod(string methodName)
        {
            return _methodRegistry.ContainsKey(methodName);
        }

        /// <summary>
        /// Clear the current UIApplication context
        /// </summary>
        public static void ClearCurrentContext()
        {
            _currentUiApp = null;
        }

        /// <summary>
        /// Execute a method by name. Used by BatchProcessor for autonomous execution.
        /// </summary>
        public static string ExecuteMethod(UIApplication uiApp, string methodName, JObject parameters)
        {
            if (_methodRegistry.Count == 0)
            {
                InitializeMethodRegistry();
            }

            if (!_methodRegistry.TryGetValue(methodName, out var method))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = $"Method '{methodName}' not found in registry. Use getBatchMethods to list available methods."
                });
            }

            try
            {
                var result = method(uiApp, parameters);

                // Track operation for proactive monitoring (Level 4)
                try
                {
                    var resultObj = JObject.Parse(result);
                    var success = resultObj["success"]?.Value<bool>() ?? false;
                    var doc = uiApp?.ActiveUIDocument?.Document;
                    ProactiveMonitor.Instance.TrackOperation(methodName, success, doc);
                }
                catch
                {
                    // Don't let tracking errors affect the operation
                }

                return result;
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = ex.StackTrace,
                    method = methodName
                });
            }
        }

        /// <summary>
        /// Get list of methods available for batch execution
        /// </summary>
        public static string GetBatchMethods(UIApplication uiApp, JObject parameters)
        {
            if (_methodRegistry.Count == 0)
            {
                InitializeMethodRegistry();
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                methods = _methodRegistry.Keys.OrderBy(k => k).ToList(),
                count = _methodRegistry.Count
            });
        }

        /// <summary>
        /// Execute an action in Revit's main thread context using ExternalEvent
        /// </summary>
        private async Task<string> ExecuteInRevitContext(Func<UIApplication, string> action)
        {
            try
            {
                Log.Information("[ExecuteInRevitContext] Starting execution");
                var handler = RevitMCPBridgeApp.GetRequestHandler();
                var externalEvent = RevitMCPBridgeApp.GetExternalEvent();

                if (handler == null || externalEvent == null)
                {
                    Log.Error("Request handler or external event not initialized");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "MCP Bridge not properly initialized"
                    });
                }

                // Queue the request and get the task
                var resultTask = handler.QueueRequest(action);

                // Raise the external event to trigger execution
                var raiseResult = externalEvent.Raise();
                Log.Information($"External event raised with result: {raiseResult}");

                if (raiseResult != Autodesk.Revit.UI.ExternalEventRequest.Accepted)
                {
                    Log.Warning($"External event not accepted: {raiseResult}");
                    // If pending, the event is already raised and will process the queue
                    if (raiseResult == Autodesk.Revit.UI.ExternalEventRequest.Denied)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"External event denied - Revit may be busy or in modal state"
                        });
                    }
                    else if (raiseResult == Autodesk.Revit.UI.ExternalEventRequest.TimedOut)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"External event timed out during raise"
                        });
                    }
                }

                // Wait for the result with timeout (5 minutes for batch operations)
                var completedTask = await Task.WhenAny(resultTask, Task.Delay(300000));

                if (completedTask == resultTask)
                {
                    return await resultTask;
                }
                else
                {
                    Log.Error("Request timed out after 5 minutes");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Request timed out after 5 minutes"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing in Revit context");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
        
        public void Start()
        {
            if (_isRunning)
            {
                Log.Warning("MCP Server is already running");
                return;
            }

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _serverTask = RunServer(_cancellationTokenSource.Token);
                _isRunning = true;
                Log.Information($"MCP Server started on pipe: {_pipeName}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start MCP Server");
                throw;
            }
        }
        
        public void Stop()
        {
            if (!_isRunning)
            {
                Log.Warning("MCP Server is not running");
                return;
            }

            try
            {
                _cancellationTokenSource?.Cancel();
                _serverTask?.Wait(5000);
                _isRunning = false;
                Log.Information("MCP Server stopped");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping MCP Server");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
            }
        }
        
        private async Task RunServer(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipeServer = null;
                try
                {
                    // SYNCHRONOUS pipe to avoid async deadlock in Revit's threading model
                    pipeServer = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 254,
                        PipeTransmissionMode.Byte, PipeOptions.None);

                    Log.Debug("Waiting for client connection...");
                    // Use synchronous WaitForConnection wrapped in Task.Run
                    await Task.Run(() => pipeServer.WaitForConnection(), cancellationToken);
                    Log.Information("Client connected to MCP Server");

                    // Handle this client in a separate task so we can immediately accept new connections
                    var clientPipe = pipeServer;
                    pipeServer = null; // Don't dispose in finally block - client handler owns it now

                    _ = Task.Run(async () => await HandleClient(clientPipe, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Server operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Server error");
                    ErrorOccurred?.Invoke(this, ex.Message);
                    await Task.Delay(1000, cancellationToken);
                }
                finally
                {
                    pipeServer?.Dispose();
                }
            }
        }

        private async Task HandleClient(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
        {
            try
            {
                using (pipeServer)
                using (var reader = new StreamReader(pipeServer))
                using (var writer = new StreamWriter(pipeServer) { AutoFlush = true })
                {
                    while (pipeServer.IsConnected && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Use synchronous ReadLine to avoid async deadlock
                            var message = reader.ReadLine();
                            if (string.IsNullOrEmpty(message))
                                continue;

                            Log.Debug($"Received message: {message}");
                            MessageReceived?.Invoke(this, message);

                            var response = await ProcessMessage(message);
                            writer.WriteLine(response);
                            Log.Debug($"Sent response: {response}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error processing message");
                            var errorResponse = JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = ex.Message
                            });
                            await writer.WriteLineAsync(errorResponse);
                        }
                    }
                }

                Log.Information("Client disconnected");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling client connection");
            }
        }
        
        private async Task<string> ProcessMessage(string message)
        {
            // Enable auto-dialog handling during MCP command execution
            var previousDialogState = RevitMCPBridgeApp.AutoHandleDialogs;
            RevitMCPBridgeApp.AutoHandleDialogs = true;

            try
            {
                var request = JObject.Parse(message);
                var method = request["method"]?.ToString();
                var parameters = request["params"] as JObject;

                Log.Information($"Processing method: {method}");
                
                switch (method)
                {
                    case "ping":
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = "pong",
                            timestamp = DateTime.Now,
                            assemblyVersion = "2.0.0",
                            testMessage = "v2.0.0_PRODUCTION"
                        });

                    case "getVersion":
                        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                        var version = assembly.GetName().Version;
                        var infoVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                            .FirstOrDefault()?.InformationalVersion ?? version.ToString();
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            version = new
                            {
                                major = version.Major,
                                minor = version.Minor,
                                patch = version.Build,
                                build = version.Revision,
                                full = version.ToString(),
                                informational = infoVersion,
                                product = "RevitMCPBridge2026",
                                company = "BIM Ops Studio",
                                copyright = "Copyright © 2025-2026 BIM Ops Studio"
                            },
                            stats = new
                            {
                                totalMethods = GetRegisteredMethods().Count,
                                totalRequests = TotalRequestCount,
                                successRate = TotalRequestCount > 0 ? (double)SuccessfulRequestCount / TotalRequestCount * 100 : 100,
                                uptime = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime
                            }
                        });

                    case "getConfiguration":
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            configuration = AppSettings.Instance.GetStatus()
                        });

                    case "reloadConfiguration":
                        AppSettings.Instance.Reload();
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = "Configuration reloaded",
                            configuration = AppSettings.Instance.GetStatus()
                        });

                    case "listMethods":
                    case "listAllMethods":  // Alias for agent compatibility
                        return ListAvailableMethods(parameters);

                    case "getMethodInfo":
                        return GetMethodInfo(parameters);

                    // Universal method passthrough - allows calling ANY method by name
                    case "callMCPMethod":
                        return await CallMCPMethodPassthrough(request, parameters);

                    case "getProjectInfo":
                        return await GetProjectInfo();

                    case "getOpenDocuments":
                        return await GetOpenDocuments();

                    case "setActiveDocument":
                        return await SetActiveDocument(parameters);

                    case "openProject":
                        return await OpenProject(parameters);

                    case "closeProject":
                        return await CloseProject(parameters);

                    case "saveProject":
                        return await SaveProject(parameters);

                    case "saveProjectAs":
                        return await SaveProjectAs(parameters);

                    case "saveAsTemplate":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.SaveAsTemplate(uiApp, parameters));

                    case "listProjectTemplates":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ListProjectTemplates(uiApp, parameters));

                    case "setProjectInfo":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.SetProjectInfo(uiApp, parameters));

                    case "purgeUnused":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.PurgeUnused(uiApp, parameters));

                    case "getDocumentInfo":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.GetDocumentInfo(uiApp, parameters));

                    case "closeDocument":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.CloseDocument(uiApp, parameters));

                    case "openDocument":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.OpenDocument(uiApp, parameters));

                    case "saveDocument":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.SaveDocument(uiApp, parameters));

                    case "createNewDocument":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.CreateNewDocument(uiApp, parameters));

                    // Dialog Handling Methods
                    case "setDialogHandler":
                        return SetDialogHandler(parameters);

                    case "setDialogAutoHandle":
                        return await ExecuteInRevitContext(uiApp => SystemMethods.SetDialogAutoHandle(uiApp, parameters));

                    case "getDialogSettings":
                        return await ExecuteInRevitContext(uiApp => SystemMethods.GetDialogSettings(uiApp, parameters));

                    case "getDialogHistory":
                        return GetDialogHistory();

                    case "clearDialogHistory":
                        return ClearDialogHistory();

                    case "getElements":
                        return await GetElements(parameters);
                        
                    case "getElementProperties":
                        return await GetElementProperties(parameters);
                        
                    case "executeCommand":
                        return await ExecuteCommand(parameters);
                        
                    case "getViews":
                        return await GetViews(parameters);

                    case "exportViewImage":
                        return await ExportViewImage(parameters);

                    case "getCategories":
                        return await GetCategories();

                    case "getParameter":
                        return await GetParameter(parameters);

                    case "getParameters":
                        return await GetParameters(parameters);

                    case "setParameter":
                        return await SetParameter(parameters);

                    case "getTextElements":
                        return await GetTextElements(parameters);

                    case "createTextNote":
                        return await CreateTextNote(parameters);

                    case "modifyTextNote":
                        return await ModifyTextNote(parameters);

                    case "deleteTextNote":
                        return await DeleteTextNote(parameters);

                    // View Annotation Methods (crop-aware)
                    case "getViewCropRegion":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetViewCropRegion(uiApp, parameters));

                    case "expandViewCropRegion":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.ExpandViewCropRegion(uiApp, parameters));

                    case "getTextNoteLocation":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetTextNoteLocation(uiApp, parameters));

                    case "createTextNoteInCrop":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateTextNoteInCrop(uiApp, parameters));

                    case "createTextNoteWithLeader":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateTextNoteWithLeader(uiApp, parameters));

                    case "createLinearDimensionInCrop":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateLinearDimension(uiApp, parameters));

                    case "moveTextNote":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.MoveTextNote(uiApp, parameters));

                    case "getTextNoteTypesAll":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetTextNoteTypes(uiApp, parameters));

                    case "addLeaderToTextNote":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.AddLeaderToTextNote(uiApp, parameters));

                    case "setTextNoteLeaderEndpoint":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.SetTextNoteLeaderEndpoint(uiApp, parameters));

                    case "resetTextNoteLeaders":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.ResetTextNoteLeaders(uiApp, parameters));

                    // Text Alignment Methods
                    case "getTextNotePositions":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetTextNotePositions(uiApp, parameters));

                    case "alignTextNotes":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.AlignTextNotes(uiApp, parameters));

                    case "createTextNoteAlignedWith":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateTextNoteAlignedWith(uiApp, parameters));

                    // Detail Component Methods (View-Aware - supports useViewCoords)
                    case "getDetailComponentFamiliesVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetDetailComponentFamilies(uiApp, parameters));

                    case "getDetailComponentTypesVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetDetailComponentTypes(uiApp, parameters));

                    case "placeDetailComponentVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.PlaceDetailComponent(uiApp, parameters));

                    case "getDetailComponentsInViewVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetDetailComponentsInView(uiApp, parameters));

                    case "moveDetailComponentVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.MoveDetailComponent(uiApp, parameters));

                    case "rotateDetailComponentVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.RotateDetailComponent(uiApp, parameters));

                    // Detail Line Methods (View-Aware - supports useViewCoords)
                    case "createDetailLineVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateDetailLine(uiApp, parameters));

                    case "getLineStylesVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetLineStyles(uiApp, parameters));

                    case "createDetailArcVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateDetailArc(uiApp, parameters));

                    // Filled Region Methods (View-Aware - supports useViewCoords)
                    case "getFilledRegionTypesVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetFilledRegionTypes(uiApp, parameters));

                    case "createFilledRegionVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateFilledRegion(uiApp, parameters));

                    // ViewAnnotation - Compound Layer Analysis
                    case "getWallTypeLayers":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetWallTypeLayers(uiApp, parameters));

                    case "getRoofTypeLayers":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetRoofTypeLayers(uiApp, parameters));

                    case "getFloorTypeLayers":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetFloorTypeLayers(uiApp, parameters));

                    // ViewAnnotation - Element Bounding Box
                    case "getElementBoundingBoxInView":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetElementBoundingBoxInView(uiApp, parameters));

                    // ViewAnnotation - Advanced Filled Regions
                    case "createFilledRegionFromPoints":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateFilledRegionFromPoints(uiApp, parameters));

                    case "getFilledRegionsInView":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetFilledRegionsInView(uiApp, parameters));

                    // ViewAnnotation - Detail Lines (with VA suffix to avoid conflict)
                    case "getDetailLinesInViewVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetDetailLinesInView(uiApp, parameters));

                    case "createDetailLinesFromPointsVA":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateDetailLinesFromPoints(uiApp, parameters));

                    // ViewAnnotation - View Analysis
                    case "analyzeDetailView":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.AnalyzeDetailView(uiApp, parameters));

                    case "getSectionCutGeometry":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.GetSectionCutGeometry(uiApp, parameters));

                    // ViewAnnotation - Scale Detail Elements
                    case "scaleDetailViewElements":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.ScaleDetailViewElements(uiApp, parameters));

                    case "mirrorDetailViewElements":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.MirrorDetailViewElements(uiApp, parameters));

                    case "rotateDetailViewElements":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.RotateDetailViewElements(uiApp, parameters));

                    case "alignElements":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.AlignElements(uiApp, parameters));

                    case "distributeElements":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.DistributeElements(uiApp, parameters));

                    case "findDuplicateElements":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.FindDuplicateElements(uiApp, parameters));

                    case "autoTagUntagged":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.AutoTagUntagged(uiApp, parameters));

                    case "offsetDetailCurves":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.OffsetDetailCurves(uiApp, parameters));

                    case "conditionalParameterUpdate":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.ConditionalParameterUpdate(uiApp, parameters));

                    case "batchUpdateTitleblocks":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.BatchUpdateTitleblocks(uiApp, parameters));

                    case "batchSwapTypes":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.BatchSwapTypes(uiApp, parameters));

                    case "matchElementProperties":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.MatchElementProperties(uiApp, parameters));

                    case "detectElementClashes":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.DetectElementClashes(uiApp, parameters));

                    case "createViewsFromRooms":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.CreateViewsFromRooms(uiApp, parameters));

                    case "propagateParameterValues":
                        return await ExecuteInRevitContext(uiApp => ViewAnnotationMethods.PropagateParameterValues(uiApp, parameters));

                    case "testSimple":
                        return JsonConvert.SerializeObject(new { success = true, result = "SIMPLE_TEST_WORKS" });

                    // Acknowledgment method for hybrid context system - just returns the message
                    case "acknowledge":
                        return JsonConvert.SerializeObject(new {
                            success = true,
                            message = parameters["message"]?.ToString() ?? "Preferences stored.",
                            storedPreferences = parameters["storedPreferences"]
                        });

                    case "changeTextNoteType":
                        return await ChangeTextNoteType(parameters);

                    // Annotation Batch Manager
                    case "findTextNotesByContent":
                        return await FindTextNotesByContent(parameters);

                    case "batchUpdateTextNotes":
                        return await BatchUpdateTextNotes(parameters);

                    case "findAndReplaceText":
                        return await FindAndReplaceText(parameters);

                    case "getTextStatistics":
                        return await GetTextStatistics(parameters);

                    // Legend/Table Automation
                    case "getLegends":
                        return await GetLegends(parameters);

                    case "createLegend":
                        return await CreateLegend(parameters);

                    case "getSchedules":
                        return await GetSchedules(parameters);

                    case "getScheduleData":
                        return await GetScheduleData(parameters);

                    case "updateScheduleCell":
                        return await UpdateScheduleCell(parameters);

                    // Pre-Issue QC Dashboard
                    case "getSheets":  // Alias for getAllSheets
                    case "getAllSheets":
                        return await GetAllSheets(parameters);

                    case "getUnplacedViews":
                        return await GetUnplacedViews(parameters);

                    case "getEmptySheets":
                        return await GetEmptySheets(parameters);

                    case "validateTextSizes":
                        return await ValidateTextSizes(parameters);

                    case "getProjectWarnings":
                        return await GetProjectWarnings(parameters);

                    case "runQCChecks":
                        return await RunQCChecks(parameters);

                    // Tagging Methods (8 total)
                    case "tagDoor":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.TagDoor(uiApp, parameters));

                    case "tagRoom":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.TagRoom(uiApp, parameters));

                    case "tagWall":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.TagWall(uiApp, parameters));

                    case "tagElement":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.TagElement(uiApp, parameters));

                    case "batchTagDoors":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.BatchTagDoors(uiApp, parameters));

                    case "batchTagRooms":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.BatchTagRooms(uiApp, parameters));

                    case "getTagsInView":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.GetTagsInView(uiApp, parameters));

                    case "deleteTag":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.DeleteTag(uiApp, parameters));

                    case "getTagInfo":
                        return await ExecuteInRevitContext(uiApp => TaggingMethods.GetTagInfo(uiApp, parameters));

                    // Dimensioning Methods (6 total)
                    case "createLinearDimension":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.CreateLinearDimension(uiApp, parameters));

                    case "createAlignedDimension":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.CreateAlignedDimension(uiApp, parameters));

                    case "batchDimensionWalls":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.BatchDimensionWalls(uiApp, parameters));

                    case "batchDimensionDoors":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.BatchDimensionDoors(uiApp, parameters));

                    case "getDimensionsInView":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.GetDimensionsInView(uiApp, parameters));

                    case "deleteDimension":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.DeleteDimension(uiApp, parameters));

                    case "createDimensionString":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.CreateDimensionString(uiApp, parameters));

                    case "batchDimensionGrids":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.BatchDimensionGrids(uiApp, parameters));
                    case "batchDimensionWindows":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.BatchDimensionWindows(uiApp, parameters));
                    case "autoAlignDimensions":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.AutoAlignDimensions(uiApp, parameters));
                    case "createEqualityDimension":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.CreateEqualityDimension(uiApp, parameters));

                    // New Dimension String Methods (8 total)
                    case "getDimensionTypes":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.GetDimensionTypes(uiApp, parameters));
                    case "getDimensionSegments":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.GetDimensionSegments(uiApp, parameters));
                    case "createCustomDimensionString":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.CreateCustomDimensionString(uiApp, parameters));
                    case "modifyDimensionText":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.ModifyDimensionText(uiApp, parameters));
                    case "findDimensionsByElement":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.FindDimensionsByElement(uiApp, parameters));
                    case "setDimensionType":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.SetDimensionType(uiApp, parameters));
                    case "addSegmentToDimension":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.AddSegmentToDimension(uiApp, parameters));
                    case "removeSegmentFromDimension":
                        return await ExecuteInRevitContext(uiApp => DimensioningMethods.RemoveSegmentFromDimension(uiApp, parameters));

                    // Wall Methods
                    case "createWallByPoints":
                        return await ExecuteInRevitContext(uiApp => WallMethods.CreateWallByPoints(uiApp, parameters));

                    case "createWallsFromPolyline":
                        return await ExecuteInRevitContext(uiApp => WallMethods.CreateWallsFromPolyline(uiApp, parameters));

                    case "getWallInfo":
                        return await ExecuteInRevitContext(uiApp => WallMethods.GetWallInfo(uiApp, parameters));

                    case "modifyWallProperties":
                        return await ExecuteInRevitContext(uiApp => WallMethods.ModifyWallProperties(uiApp, parameters));

                    case "splitWall":
                        return await ExecuteInRevitContext(uiApp => WallMethods.SplitWall(uiApp, parameters));

                    case "joinWalls":
                        return await ExecuteInRevitContext(uiApp => WallMethods.JoinWalls(uiApp, parameters));

                    case "unjoinWalls":
                        return await ExecuteInRevitContext(uiApp => WallMethods.UnjoinWalls(uiApp, parameters));

                    case "getWallsInView":
                        return await ExecuteInRevitContext(uiApp => WallMethods.GetWallsInView(uiApp, parameters));

                    case "getWallTypes":
                        return await ExecuteInRevitContext(uiApp => WallMethods.GetWallTypes(uiApp, parameters));

                    case "duplicateWallType":
                        return await ExecuteInRevitContext(uiApp => WallMethods.DuplicateWallType(uiApp, parameters));

                    case "flipWall":
                        return await ExecuteInRevitContext(uiApp => WallMethods.FlipWall(uiApp, parameters));

                    case "deleteWall":
                        return await ExecuteInRevitContext(uiApp => WallMethods.DeleteWall(uiApp, parameters));

                    case "batchCreateWalls":
                        return await ExecuteInRevitContext(uiApp => WallMethods.BatchCreateWalls(uiApp, parameters));

                    case "modifyWallType":
                        return await ExecuteInRevitContext(uiApp => WallMethods.ModifyWallType(uiApp, parameters));

                    case "batchModifyWallTypes":
                        return await ExecuteInRevitContext(uiApp => WallMethods.BatchModifyWallTypes(uiApp, parameters));

                    // Floor, Ceiling, Roof Methods
                    case "createFloor":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateFloor(uiApp, parameters));

                    case "getFloorTypes":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetFloorTypes(uiApp, parameters));

                    case "createFloorOpening":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateFloorOpening(uiApp, parameters));

                    case "createCeiling":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateCeiling(uiApp, parameters));

                    case "getCeilingTypes":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetCeilingTypes(uiApp, parameters));

                    case "createRoofByFootprint":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateRoofByFootprint(uiApp, parameters));

                    case "getRoofTypes":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetRoofTypes(uiApp, parameters));

                    case "createRoofOpening":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateRoofOpening(uiApp, parameters));

                    case "getLevels":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetLevels(uiApp, parameters));

                    // Wall Methods
                    case "getWalls":
                        return await ExecuteInRevitContext(uiApp => WallMethods.GetWalls(uiApp, parameters));

                    // Door/Window Methods (model-wide)
                    case "getDoors":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetDoors(uiApp, parameters));

                    case "getWindows":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetWindows(uiApp, parameters));

                    // Floor/Ceiling Methods (model-wide)
                    case "getFloors":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetFloors(uiApp, parameters));

                    case "getCeilings":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetCeilings(uiApp, parameters));

                    // Stairs/Railings/Roofs
                    case "getStairs":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetStairs(uiApp, parameters));

                    case "getRailings":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetRailings(uiApp, parameters));

                    case "getRoofs":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetRoofs(uiApp, parameters));

                    case "getColumns":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetColumns(uiApp, parameters));

                    case "getCurtainWalls":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetCurtainWalls(uiApp, parameters));
                    case "createCeilingGrid":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateCeilingGrid(uiApp, parameters));
                    case "placeLightFixture":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.PlaceLightFixture(uiApp, parameters));
                    case "tagCeilingHeight":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.TagCeilingHeight(uiApp, parameters));
                    case "createRoofByExtrusion":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.CreateRoofByExtrusion(uiApp, parameters));
                    case "addSlopeArrow":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.AddSlopeArrow(uiApp, parameters));
                    case "modifyRoofSlope":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.ModifyRoofSlope(uiApp, parameters));

                    // Boundary Editing Methods
                    case "editFloorBoundary":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.EditFloorBoundary(uiApp, parameters));
                    case "editCeilingBoundary":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.EditCeilingBoundary(uiApp, parameters));
                    case "editRoofBoundary":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.EditRoofBoundary(uiApp, parameters));
                    case "getFloorBoundary":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetFloorBoundary(uiApp, parameters));
                    case "getCeilingBoundary":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetCeilingBoundary(uiApp, parameters));
                    case "getRoofBoundary":
                        return await ExecuteInRevitContext(uiApp => FloorCeilingRoofMethods.GetRoofBoundary(uiApp, parameters));

                    // Furniture/Fixtures
                    case "getFurniture":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetFurniture(uiApp, parameters));

                    case "getPlumbingFixtures":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetPlumbingFixtures(uiApp, parameters));

                    case "getLightingFixtures":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetLightingFixtures(uiApp, parameters));

                    case "getElectricalFixtures":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetElectricalFixtures(uiApp, parameters));

                    case "createWall":
                        Log.Information("[MCPServer] Routing createWall to WallMethods.CreateWallByPoints");
                        return await ExecuteInRevitContext(uiApp => WallMethods.CreateWallByPoints(uiApp, parameters));

                    // Base Point and Survey Point Methods
                    case "getProjectBasePoint":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetProjectBasePoint(uiApp, parameters));

                    case "setProjectBasePoint":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.SetProjectBasePoint(uiApp, parameters));

                    case "getSurveyPoint":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetSurveyPoint(uiApp, parameters));

                    case "setSurveyPoint":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.SetSurveyPoint(uiApp, parameters));

                    // Line Methods
                    case "createModelLine":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateModelLine(uiApp, parameters));

                    // Element Methods - Location, Placement, Bounding Box
                    case "getElementLocation":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetElementLocation(uiApp, parameters));

                    case "getBoundingBox":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetBoundingBox(uiApp, parameters));

                    case "moveElement":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.MoveElement(uiApp, parameters));

                    case "deleteElement":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.DeleteElement(uiApp, parameters));

                    case "placeFamilyInstance":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.PlaceFamilyInstance(uiApp, parameters));

                    case "getFamilyInstanceTypes":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetFamilyInstanceTypes(uiApp, parameters));

                    // Family Loading Methods
                    case "loadFamily":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.LoadFamily(uiApp, parameters));

                    case "listFamilyFiles":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.ListFamilyFiles(uiApp, parameters));

                    case "getLibraryPaths":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetLibraryPaths(uiApp, parameters));

                    case "getLoadedFamilies":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetLoadedFamilies(uiApp, parameters));

                    case "openLoadAutodeskFamilyDialog":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.OpenLoadAutodeskFamilyDialog(uiApp, parameters));

                    // Import/CAD Geometry Extraction Methods
                    case "getImportedInstances":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetImportedInstances(uiApp, parameters));

                    case "getImportedGeometry":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetImportedGeometry(uiApp, parameters));

                    case "getImportedLines":
                        return await ExecuteInRevitContext(uiApp => ElementMethods.GetImportedLines(uiApp, parameters));

                    // Project Setup Methods - Document Creation, Levels, Grids, Site, Utilities
                    case "createNewProject":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateNewProject(uiApp, parameters));

                    case "getProjectTemplates":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetProjectTemplates(uiApp, parameters));

                    case "createLevel":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateLevel(uiApp, parameters));

                    case "deleteLevel":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.DeleteLevel(uiApp, parameters));

                    case "createGrid":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateGrid(uiApp, parameters));

                    case "createArcGrid":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateArcGrid(uiApp, parameters));

                    case "getGrids":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetGrids(uiApp, parameters));

                    case "deleteGrid":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.DeleteGrid(uiApp, parameters));

                    case "createTopography":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateTopography(uiApp, parameters));

                    case "modifyTopography":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.ModifyTopography(uiApp, parameters));

                    case "createBuildingPad":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateBuildingPad(uiApp, parameters));

                    case "copyElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CopyElements(uiApp, parameters));

                    case "moveElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.MoveElements(uiApp, parameters));

                    case "rotateElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.RotateElements(uiApp, parameters));

                    case "mirrorElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.MirrorElements(uiApp, parameters));

                    case "arrayElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.ArrayElements(uiApp, parameters));

                    case "deleteElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.DeleteElements(uiApp, parameters));

                    case "copyElementsBetweenDocuments":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CopyElementsBetweenDocuments(uiApp, parameters));

                    case "transferFamilyBetweenDocuments":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.TransferFamilyBetweenDocuments(uiApp, parameters));

                    case "copyViewContentBetweenDocuments":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CopyViewContentBetweenDocuments(uiApp, parameters));

                    case "getSheetsFromDocument":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetSheetsFromDocument(uiApp, parameters));

                    case "getDetailLinesFromDocument":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetDetailLinesFromDocument(uiApp, parameters));

                    case "createGroup":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.CreateGroup(uiApp, parameters));

                    case "placeGroup":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.PlaceGroup(uiApp, parameters));

                    case "getGroupTypes":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.GetGroupTypes(uiApp, parameters));

                    case "ungroupElements":
                        return await ExecuteInRevitContext(uiApp => ProjectSetupMethods.UngroupElements(uiApp, parameters));

                    // Room Methods
                    case "createRoom":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.CreateRoom(uiApp, parameters));

                    case "getRoomInfo":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetRoomInfo(uiApp, parameters));

                    case "modifyRoomProperties":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.ModifyRoomProperties(uiApp, parameters));

                    case "placeRoomTag":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.PlaceRoomTag(uiApp, parameters));

                    case "getRooms":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetRooms(uiApp, parameters));

                    case "setRoomNumber":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.SetRoomNumber(uiApp, parameters));

                    case "setRoomName":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.SetRoomName(uiApp, parameters));

                    case "setRoomDepartment":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.SetRoomDepartment(uiApp, parameters));

                    case "setRoomComments":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.SetRoomComments(uiApp, parameters));

                    case "getRoomsByLevel":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetRoomsByLevel(uiApp, parameters));

                    case "batchUpdateRooms":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.BatchUpdateRooms(uiApp, parameters));

                    case "validateRoomData":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.ValidateRoomData(uiApp, parameters));

                    case "getRoomBoundaryWalls":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetRoomBoundaryWalls(uiApp, parameters));

                    case "updateRoomAreaFromFilledRegion":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.UpdateRoomAreaFromFilledRegion(uiApp, parameters));

                    case "createOffsetRoomBoundaries":
                        return await ExecuteInRevitContext(uiApp => RoomBoundarySolution.CreateOffsetRoomBoundaries(uiApp, parameters));

                    case "createRoomBoundaryFilledRegion":
                        return await ExecuteInRevitContext(uiApp => AutomatedFilledRegion.CreateRoomBoundaryFilledRegion(uiApp, parameters));

                    case "getViewSnapshot":
                        return await ExecuteInRevitContext(uiApp => ViewAnalysisMethods.GetViewSnapshot(uiApp, parameters));

                    case "createRoomSeparationLine":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.CreateRoomSeparationLine(uiApp, parameters));

                    case "deleteRoom":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.DeleteRoom(uiApp, parameters));

                    case "getRoomAtPoint":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetRoomAtPoint(uiApp, parameters));

                    case "renumberRooms":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.RenumberRooms(uiApp, parameters));

                    case "setAreaComputation":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.SetAreaComputation(uiApp, parameters));

                    case "getAreaComputation":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetAreaComputation(uiApp, parameters));
                    case "createAreaPlan":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.CreateAreaPlan(uiApp, parameters));
                    case "createAreaBoundary":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.CreateAreaBoundary(uiApp, parameters));
                    case "getAreas":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetAreas(uiApp, parameters));
                    case "placeArea":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.PlaceArea(uiApp, parameters));
                    case "createRoomFinishSchedule":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.CreateRoomFinishSchedule(uiApp, parameters));
                    case "getRoomFinishes":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.GetRoomFinishes(uiApp, parameters));
                    case "updateRoomFinishes":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.UpdateRoomFinishes(uiApp, parameters));

                    // DWG Export Methods (Critical CD Methods)
                    case "exportViewsToDWG":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportViewsToDWG(uiApp, parameters));
                    case "exportSheetsToDWG":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportSheetsToDWG(uiApp, parameters));
                    case "batchExportDWG":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.BatchExportDWG(uiApp, parameters));
                    case "exportDraftingViewsToFolder":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportDraftingViewsToFolder(uiApp, parameters));
                    case "exportDraftingViewsToRvt":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportDraftingViewsToRvt(uiApp, parameters));
                    case "exportDraftingViewsByCategoryToRvt":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportDraftingViewsByCategoryToRvt(uiApp, parameters));
                    case "exportLegendsToRvt":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportLegendsToRvt(uiApp, parameters));
                    case "exportSchedulesToRvt":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportSchedulesToRvt(uiApp, parameters));

                    // Library Methods (Search, Load, Insert from Detail Library)
                    case "searchLibrary":
                        return await ExecuteInRevitContext(uiApp => LibraryMethods.SearchLibrary(uiApp, parameters));
                    case "getLibraryStats":
                        return await ExecuteInRevitContext(uiApp => LibraryMethods.GetLibraryStats(uiApp, parameters));
                    case "loadLibraryFamily":
                        return await ExecuteInRevitContext(uiApp => LibraryMethods.LoadLibraryFamily(uiApp, parameters));
                    case "insertLibraryView":
                        return await ExecuteInRevitContext(uiApp => LibraryMethods.InsertLibraryView(uiApp, parameters));

                    // Library Sync Methods (Scan, Compare, Extract, Firm Management)
                    case "scanProjectContent":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.ScanProjectContent(uiApp, parameters));
                    case "compareProjectToLibrary":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.CompareProjectToLibrary(uiApp, parameters));
                    case "extractNewToLibrary":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.ExtractNewToLibrary(uiApp, parameters));
                    case "createFirmProfile":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.CreateFirmProfile(uiApp, parameters));
                    case "getFirmProfiles":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.GetFirmProfiles(uiApp, parameters));
                    case "detectFirmFromProject":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.DetectFirmFromProject(uiApp, parameters));
                    case "rebuildLibraryIndex":
                        return await ExecuteInRevitContext(uiApp => LibrarySyncMethods.RebuildLibraryIndex(uiApp, parameters));

                    // Capability System Methods (Self-expanding tool factory)
                    case "classifyFailure":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.ClassifyFailure(uiApp, parameters));
                    case "proposeToolSpec":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.ProposeToolSpec(uiApp, parameters));
                    case "approveToolSpec":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.ApproveToolSpec(uiApp, parameters));
                    case "listToolSpecs":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.ListToolSpecs(uiApp, parameters));
                    case "getToolSpec":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.GetToolSpec(uiApp, parameters));
                    case "getMethodRegistry":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.GetMethodRegistry(uiApp, parameters));
                    case "rebuildMethodRegistry":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.RebuildMethodRegistry(uiApp, parameters));
                    case "createTestArtifact":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.CreateTestArtifact(uiApp, parameters));
                    case "runTest":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.RunTest(uiApp, parameters));
                    case "listTests":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.ListTests(uiApp, parameters));
                    case "getCapabilityStatus":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.GetCapabilityStatus(uiApp, parameters));
                    case "generateImplementation":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.GenerateImplementation(uiApp, parameters));
                    case "runRegressionTests":
                        return await ExecuteInRevitContext(uiApp => CapabilityMethods.RunRegressionTests(uiApp, parameters));

                    // Batch Text Methods - for standardizing text across detail library
                    case "getTextTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.GetTextTypes(uiApp, parameters));
                    case "createStandardTextType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.CreateStandardTextType(uiApp, parameters));
                    case "getTextNotes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.GetTextNotes(uiApp, parameters));
                    case "standardizeDocumentText":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.StandardizeDocumentText(uiApp, parameters));
                    case "standardizeDimensionText":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.StandardizeDimensionText(uiApp, parameters));
                    case "renameDimensionTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.RenameDimensionTypes(uiApp, parameters));
                    case "standardizeTextNoteTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.StandardizeTextNoteTypes(uiApp, parameters));
                    case "processDetailFile":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.ProcessDetailFile(uiApp, parameters));
                    case "getDetailLibraryFiles":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.GetDetailLibraryFiles(uiApp, parameters));
                    case "getNextFileToProcess":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.GetNextFileToProcess(uiApp, parameters));
                    case "markFileProcessed":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.BatchTextMethods.MarkFileProcessed(uiApp, parameters));

                    // Revision Methods (Critical CD Methods)
                    case "createRevision":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.CreateRevision(uiApp, parameters));
                    case "getRevisions":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.GetRevisions(uiApp, parameters));
                    case "addRevisionToSheets":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.AddRevisionToSheets(uiApp, parameters));
                    case "removeRevisionFromSheets":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.RemoveRevisionFromSheets(uiApp, parameters));
                    case "placeRevisionCloud":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.PlaceRevisionCloud(uiApp, parameters));
                    case "getRevisionClouds":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.GetRevisionClouds(uiApp, parameters));
                    case "tagRevisionCloud":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.TagRevisionCloud(uiApp, parameters));

                    // PDF Export Methods (Critical CD Methods)
                    case "exportSheetsToPDF":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.ExportSheetsToPDF(uiApp, parameters));
                    case "batchExportPDF":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.BatchExportPDF(uiApp, parameters));
                    case "getPrintSettings":
                        return await ExecuteInRevitContext(uiApp => DocumentMethods.GetPrintSettings(uiApp, parameters));

                    // Schedule Methods
                    case "createSchedule":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.CreateSchedule(uiApp, parameters));

                    case "addScheduleField":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.AddScheduleField(uiApp, parameters));

                    case "addScheduleFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.AddScheduleFilter(uiApp, parameters));

                    case "filterScheduleByLevel":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.FilterScheduleByLevel(uiApp, parameters));

                    case "addScheduleSorting":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.AddScheduleSorting(uiApp, parameters));

                    case "formatScheduleAppearance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.FormatScheduleAppearance(uiApp, parameters));

                    case "exportScheduleToCSV":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.ExportScheduleToCSV(uiApp, parameters));

                    case "getAllSchedules":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetAllSchedules(uiApp, parameters));

                    case "getScheduleFields":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetScheduleFields(uiApp, parameters));

                    case "getAvailableSchedulableFields":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetAvailableSchedulableFields(uiApp, parameters));

                    case "deleteSchedule":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.DeleteSchedule(uiApp, parameters));

                    case "createKeySchedule":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.CreateKeySchedule(uiApp, parameters));

                    case "createMaterialTakeoff":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.CreateMaterialTakeoff(uiApp, parameters));

                    case "createSheetList":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.CreateSheetList(uiApp, parameters));

                    case "removeScheduleField":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.RemoveScheduleField(uiApp, parameters));

                    case "reorderScheduleFields":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.ReorderScheduleFields(uiApp, parameters));

                    case "modifyScheduleField":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.ModifyScheduleField(uiApp, parameters));

                    case "removeScheduleFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.RemoveScheduleFilter(uiApp, parameters));

                    case "getScheduleFilters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetScheduleFilters(uiApp, parameters));

                    case "modifyScheduleFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.ModifyScheduleFilter(uiApp, parameters));

                    case "addScheduleGrouping":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.AddScheduleGrouping(uiApp, parameters));

                    case "getScheduleSortGrouping":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetScheduleSortGrouping(uiApp, parameters));

                    case "removeScheduleSorting":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.RemoveScheduleSorting(uiApp, parameters));

                    case "setConditionalFormatting":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.SetConditionalFormatting(uiApp, parameters));

                    case "setColumnWidth":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.SetColumnWidth(uiApp, parameters));

                    case "setFieldAlignment":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.SetFieldAlignment(uiApp, parameters));

                    case "getScheduleCellValue":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetScheduleCellValue(uiApp, parameters));

                    case "getScheduleTotals":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetScheduleTotals(uiApp, parameters));

                    case "modifyScheduleProperties":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.ModifyScheduleProperties(uiApp, parameters));

                    case "duplicateSchedule":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.DuplicateSchedule(uiApp, parameters));

                    case "addCalculatedField":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.AddCalculatedField(uiApp, parameters));

                    case "modifyCalculatedField":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.ModifyCalculatedField(uiApp, parameters));

                    case "getScheduleInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.GetScheduleInfo(uiApp, parameters));

                    case "refreshSchedule":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ScheduleMethods.RefreshSchedule(uiApp, parameters));

                    // Workset Methods
                    case "createWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.CreateWorkset(uiApp, parameters));

                    case "getAllWorksets":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetAllWorksets(uiApp, parameters));

                    case "getWorksetInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetWorksetInfo(uiApp, parameters));

                    case "renameWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.RenameWorkset(uiApp, parameters));

                    case "deleteWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.DeleteWorkset(uiApp, parameters));

                    case "setElementWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.SetElementWorkset(uiApp, parameters));

                    case "getElementWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetElementWorkset(uiApp, parameters));

                    case "getElementsInWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetElementsInWorkset(uiApp, parameters));

                    case "moveElementsToWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.MoveElementsToWorkset(uiApp, parameters));

                    case "setWorksetVisibility":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.SetWorksetVisibility(uiApp, parameters));

                    case "getWorksetVisibilityInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetWorksetVisibilityInView(uiApp, parameters));

                    case "setGlobalWorksetVisibility":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.SetGlobalWorksetVisibility(uiApp, parameters));

                    case "isWorksetEditable":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.IsWorksetEditable(uiApp, parameters));

                    case "isElementBorrowed":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.IsElementBorrowed(uiApp, parameters));

                    case "relinquishOwnership":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.RelinquishOwnership(uiApp, parameters));

                    case "getCheckoutStatus":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetCheckoutStatus(uiApp, parameters));

                    case "enableWorksharing":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.EnableWorksharing(uiApp, parameters));

                    case "isWorkshared":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.IsWorkshared(uiApp, parameters));

                    case "getWorksharingOptions":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetWorksharingOptions(uiApp, parameters));

                    case "synchronizeWithCentral":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.SynchronizeWithCentral(uiApp, parameters));

                    case "reloadLatest":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.ReloadLatest(uiApp, parameters));

                    case "getSyncHistory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetSyncHistory(uiApp, parameters));

                    case "getWorksetsByCategory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetWorksetsByCategory(uiApp, parameters));

                    case "createWorksetNamingScheme":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.CreateWorksetNamingScheme(uiApp, parameters));

                    case "getActiveWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.GetActiveWorkset(uiApp, parameters));

                    case "setActiveWorkset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.SetActiveWorkset(uiApp, parameters));
                    case "bulkSetWorksetsByCategory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.WorksetMethods.BulkSetWorksetsByCategory(uiApp, parameters));

                    // Phase Methods
                    case "createPhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.CreatePhase(uiApp, parameters));

                    case "getAllPhases":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetAllPhases(uiApp, parameters));

                    case "getPhaseInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetPhaseInfo(uiApp, parameters));

                    case "renamePhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.RenamePhase(uiApp, parameters));

                    case "deletePhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.DeletePhase(uiApp, parameters));

                    case "reorderPhases":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.ReorderPhases(uiApp, parameters));

                    case "setElementPhaseCreated":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.SetElementPhaseCreated(uiApp, parameters));

                    case "setElementPhaseDemolished":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.SetElementPhaseDemolished(uiApp, parameters));

                    case "getElementPhasing":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetElementPhasing(uiApp, parameters));

                    case "getElementsInPhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetElementsInPhase(uiApp, parameters));

                    case "setBulkElementPhasing":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.SetBulkElementPhasing(uiApp, parameters));

                    case "createPhaseFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.CreatePhaseFilter(uiApp, parameters));

                    case "getPhaseFilters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetPhaseFilters(uiApp, parameters));

                    case "getPhaseFilterInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetPhaseFilterInfo(uiApp, parameters));

                    case "modifyPhaseFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.ModifyPhaseFilter(uiApp, parameters));

                    case "deletePhaseFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.DeletePhaseFilter(uiApp, parameters));

                    case "setViewPhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.SetViewPhase(uiApp, parameters));

                    case "setViewPhaseFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.SetViewPhaseFilter(uiApp, parameters));

                    case "getViewPhaseSettings":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetViewPhaseSettings(uiApp, parameters));

                    case "analyzePhasing":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.AnalyzePhasing(uiApp, parameters));

                    case "findPhasingConflicts":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.FindPhasingConflicts(uiApp, parameters));

                    case "getPhaseTransitionReport":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetPhaseTransitionReport(uiApp, parameters));

                    case "getCurrentPhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.GetCurrentPhase(uiApp, parameters));

                    case "copyElementsToPhase":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.CopyElementsToPhase(uiApp, parameters));
                    case "setupDemolitionPlan":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.PhaseMethods.SetupDemolitionPlan(uiApp, parameters));

                    // Door/Window Methods
                    case "placeDoor":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.PlaceDoor(uiApp, parameters));

                    case "placeWindow":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.PlaceWindow(uiApp, parameters));

                    case "getDoorWindowInfo":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetDoorWindowInfo(uiApp, parameters));

                    case "modifyDoorWindowProperties":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.ModifyDoorWindowProperties(uiApp, parameters));

                    case "flipDoorWindow":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.FlipDoorWindow(uiApp, parameters));

                    case "getDoorsInView":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetDoorsInView(uiApp, parameters));

                    case "getWindowsInView":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetWindowsInView(uiApp, parameters));

                    case "getDoorTypes":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetDoorTypes(uiApp, parameters));

                    case "getWindowTypes":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetWindowTypes(uiApp, parameters));

                    case "deleteDoorWindow":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.DeleteDoorWindow(uiApp, parameters));

                    case "getDoorSchedule":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetDoorSchedule(uiApp, parameters));

                    case "getWindowSchedule":
                        return await ExecuteInRevitContext(uiApp => DoorWindowMethods.GetWindowSchedule(uiApp, parameters));

                    // Text/Tag Methods
                    case "placeTextNote":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.PlaceTextNote(uiApp, parameters));

                    case "placeTextNoteInView":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.PlaceTextNoteInView(uiApp, parameters));

                    case "getViewAnnotationBounds":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetViewAnnotationBounds(uiApp, parameters));

                    case "modifyTextNote2":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.ModifyTextNote(uiApp, parameters));

                    case "placeWallTag":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.PlaceWallTag(uiApp, parameters));

                    case "placeDoorTag":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.PlaceDoorTag(uiApp, parameters));

                    case "placeLeaderNote":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.PlaceLeaderNote(uiApp, parameters));

                    case "getTextNotesInView":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetTextNotesInView(uiApp, parameters));

                    case "getTextNotesOnSheet":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetTextNotesOnSheet(uiApp, parameters));

                    case "getTagsInView2":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetTagsInView(uiApp, parameters));

                    case "getTextNoteTypes":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetTextNoteTypes(uiApp, parameters));

                    case "deleteTextNote2":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.DeleteTextNote(uiApp, parameters));

                    case "deleteTag2":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.DeleteTag(uiApp, parameters));

                    case "tagAllByCategory":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.TagAllByCategory(uiApp, parameters));

                    case "tagAllRooms":
                        // Convenience method - wraps tagAllByCategory with category=Rooms
                        if (parameters == null) parameters = new JObject();
                        parameters["category"] = "Rooms";
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.TagAllByCategory(uiApp, parameters));

                    case "modifyTag":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.ModifyTag(uiApp, parameters));

                    case "changeTagType":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.ChangeTagType(uiApp, parameters));

                    case "getTagsForElement":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetTagsForElement(uiApp, parameters));

                    case "getRoomTagTypes":
                        return await ExecuteInRevitContext(uiApp => TextTagMethods.GetRoomTagTypes(uiApp, parameters));

                    // Rich Text Methods (multi-color text annotations)
                    case "createRichTextNote":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.CreateRichTextNote(uiApp, parameters));

                    case "updateRichTextNote":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.UpdateRichTextNote(uiApp, parameters));

                    case "getRichTextNoteData":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.GetRichTextNoteData(uiApp, parameters));

                    case "getRichTextNotes":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.GetRichTextNotes(uiApp, parameters));

                    case "explodeRichTextNote":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.ExplodeRichTextNote(uiApp, parameters));

                    case "getOrCreateColoredTextType":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.GetOrCreateColoredTextType(uiApp, parameters));

                    case "getColoredTextTypes":
                        return await ExecuteInRevitContext(uiApp => RichTextMethods.GetColoredTextTypes(uiApp, parameters));

                    // View Methods
                    case "createFloorPlan":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CreateFloorPlan(uiApp, parameters));

                    case "createCeilingPlan":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CreateCeilingPlan(uiApp, parameters));

                    case "createSection":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CreateSection(uiApp, parameters));

                    case "createElevation":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CreateElevation(uiApp, parameters));

                    case "createDraftingView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CreateDraftingView(uiApp, parameters));

                    case "duplicateView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.DuplicateView(uiApp, parameters));

                    case "applyViewTemplate":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.ApplyViewTemplate(uiApp, parameters));

                    case "getAllViews":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetAllViews(uiApp, parameters));

                    case "getViewTemplates":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetViewTemplates(uiApp, parameters));

                    case "setViewCropBox":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.SetViewCropBox(uiApp, parameters));

                    case "setViewFarClip":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.SetViewFarClip(uiApp, parameters));

                    case "getViewCropBox":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetViewCropBox(uiApp, parameters));

                    case "renameView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.RenameView(uiApp, parameters));

                    case "deleteView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.DeleteView(uiApp, parameters));

                    case "setViewScale":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.SetViewScale(uiApp, parameters));

                    case "getActiveView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetActiveView(uiApp, parameters));

                    case "setActiveView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.SetActiveView(uiApp, parameters));

                    case "zoomToFit":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.ZoomToFit(uiApp, parameters));

                    case "zoomToElement":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.ZoomToElement(uiApp, parameters));

                    case "zoomToRegion":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.ZoomToRegion(uiApp, parameters));

                    case "zoomToGridIntersection":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.ZoomToGridIntersection(uiApp, parameters));

                    // Measurement Methods
                    case "measureDistance":
                        return await ExecuteInRevitContext(uiApp => MeasurementMethods.MeasureDistance(uiApp, parameters));

                    case "measureBetweenElements":
                        return await ExecuteInRevitContext(uiApp => MeasurementMethods.MeasureBetweenElements(uiApp, parameters));

                    case "getRoomDimensions":
                        return await ExecuteInRevitContext(uiApp => MeasurementMethods.GetRoomDimensions(uiApp, parameters));

                    case "measurePerpendicularToWall":
                        return await ExecuteInRevitContext(uiApp => MeasurementMethods.MeasurePerpendicularToWall(uiApp, parameters));

                    case "measureCorridorWidth":
                        return await ExecuteInRevitContext(uiApp => MeasurementMethods.MeasureCorridorWidth(uiApp, parameters));

                    case "showElement":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.ShowElement(uiApp, parameters));

                    case "regenerateView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.RegenerateView(uiApp, parameters));

                    case "verifyRoomValues":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.VerifyRoomValues(uiApp, parameters));

                    case "compareExpectedActual":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CompareExpectedActual(uiApp, parameters));

                    case "createLegendView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CreateLegendView(uiApp, parameters));

                    case "getLegendViews":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetLegendViews(uiApp, parameters));

                    case "getElementsInView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetElementsInView(uiApp, parameters));

                    case "setCategoryHidden":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.SetCategoryHidden(uiApp, parameters));

                    case "hideCategoriesInView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.HideCategoriesInView(uiApp, parameters));

                    case "hideElementsInView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.HideElementsInView(uiApp, parameters));

                    case "unhideElementsInView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.UnhideElementsInView(uiApp, parameters));

                    case "getRoomTagsInView":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetRoomTagsInView(uiApp, parameters));

                    case "insertViewsFromFile":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.InsertViewsFromFile(uiApp, parameters));

                    case "getColorFillScheme":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetColorFillScheme(uiApp, parameters));

                    case "setColorFillScheme":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.SetColorFillScheme(uiApp, parameters));

                    case "copyColorFillScheme":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.CopyColorFillScheme(uiApp, parameters));

                    case "getAllColorFillSchemes":
                        return await ExecuteInRevitContext(uiApp => ViewMethods.GetAllColorFillSchemes(uiApp, parameters));

                    // Compliance Methods
                    case "runComplianceCheck":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.RunComplianceCheck(uiApp, parameters));

                    case "checkCorridorWidths":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckCorridorWidths(uiApp, parameters));

                    case "checkDoorWidths":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckDoorWidths(uiApp, parameters));

                    case "checkRoomAreas":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckRoomAreas(uiApp, parameters));

                    case "checkToiletClearances":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckToiletClearances(uiApp, parameters));

                    case "checkDoorSwing":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckDoorSwing(uiApp, parameters));

                    case "checkStairDimensions":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckStairDimensions(uiApp, parameters));

                    case "checkCeilingHeights":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckCeilingHeights(uiApp, parameters));

                    case "checkWallFireRatings":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.CheckWallFireRatings(uiApp, parameters));

                    case "generateComplianceReport":
                        return await ExecuteInRevitContext(uiApp => ComplianceMethods.GenerateComplianceReport(uiApp, parameters));

                    // CD Checklist Methods
                    case "runCDChecklist":
                        return await ExecuteInRevitContext(uiApp => CDChecklistMethods.RunCDChecklist(uiApp, parameters));

                    case "getFilledRegions":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetFilledRegionsInView(uiApp, parameters));

                    // Sheet Methods
                    case "createSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.CreateSheet(uiApp, parameters));

                    case "placeViewOnSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.PlaceViewOnSheet(uiApp, parameters));

                    case "getAllSheets2":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetAllSheets(uiApp, parameters));

                    case "getViewportsOnSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetViewportsOnSheet(uiApp, parameters));

                    case "modifySheetProperties":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.ModifySheetProperties(uiApp, parameters));

                    case "moveViewport":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.MoveViewport(uiApp, parameters));

                    case "deleteSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.DeleteSheet(uiApp, parameters));

                    case "removeViewport":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.RemoveViewport(uiApp, parameters));

                    case "getTitleblockTypes":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetTitleblockTypes(uiApp, parameters));

                    case "getPreferredTitleblock":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetPreferredTitleblock(uiApp, parameters));

                    case "changeTitleBlock":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.ChangeTitleBlock(uiApp, parameters));

                    case "createSheetAuto":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.CreateSheetAuto(uiApp, parameters));

                    case "createSheetsIntelligent":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.CreateSheetsIntelligent(uiApp, parameters));

                    case "batchCreateSheetsWithDetails":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.BatchCreateSheetsWithDetails(uiApp, parameters));

                    case "analyzeProjectStandards":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.AnalyzeProjectStandards(uiApp, parameters));

                    case "duplicateSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.DuplicateSheet(uiApp, parameters));

                    case "setViewportType":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.SetViewportType(uiApp, parameters));

                    case "getViewportLabelOffset":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetViewportLabelOffset(uiApp, parameters));

                    case "setViewportLabelOffset":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.SetViewportLabelOffset(uiApp, parameters));

                    case "renumberSheets":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.RenumberSheets(uiApp, parameters));

                    case "getTitleblockDimensions":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetTitleblockDimensions(uiApp, parameters));

                    case "getViewportBounds":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetViewportBounds(uiApp, parameters));

                    case "analyzeSheetLayout":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.AnalyzeSheetLayout(uiApp, parameters));

                    case "calculateOptimalScale":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.CalculateOptimalScale(uiApp, parameters));

                    case "getSheetCoordinateSystem":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetSheetCoordinateSystem(uiApp, parameters));

                    case "getSheetPrintableArea":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GetSheetPrintableAreaMCP(uiApp, parameters));

                    case "switchToView":
                    case "switchToSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.SwitchToViewMCP(uiApp, parameters));

                    case "placeViewOnSheetSmart":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.PlaceViewOnSheetSmart(uiApp, parameters));

                    case "placeViewOnSheetForced":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.PlaceViewOnSheetForced(uiApp, parameters));

                    case "placeMultipleViewsOnSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.PlaceMultipleViewsOnSheet(uiApp, parameters));

                    case "importImage":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.ImportImage(uiApp, parameters));
                    case "moveImage":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.MoveImage(uiApp, parameters));
                    case "deleteImage":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.DeleteImage(uiApp, parameters));
                    case "copyElementsToSheet":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.CopyElementsToSheet(uiApp, parameters));
                    case "setTextNoteText":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.SetTextNoteText(uiApp, parameters));
                    // exportSheetsToPDF moved to DocumentMethods with enhanced features
                    // case "exportSheetsToPDF":
                    //     return await ExecuteInRevitContext(uiApp => SheetMethods.ExportSheetsToPDF(uiApp, parameters));
                    case "autoPopulateSheetFields":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.AutoPopulateSheetFields(uiApp, parameters));
                    case "batchPrintSheets":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.BatchPrintSheets(uiApp, parameters));
                    case "generateViewportLayout":
                        return await ExecuteInRevitContext(uiApp => SheetMethods.GenerateViewportLayout(uiApp, parameters));

                    case "createCallout":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.CreateCallout(uiApp, parameters));
                    case "createMatchline":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.CreateMatchline(uiApp, parameters));
                    case "createReferencePlane":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.CreateReferencePlane(uiApp, parameters));
                    // createRevision moved to DocumentMethods with enhanced features
                    // case "createRevision":
                    //     return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.CreateRevision(uiApp, parameters));
                    case "createRevisionCloud":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.CreateRevisionCloud(uiApp, parameters));
                    case "deleteAnnotation":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.DeleteAnnotation(uiApp, parameters));
                    case "deleteRevisionCloud":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.DeleteRevisionCloud(uiApp, parameters));
                    case "getAllAnnotationsInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetAllAnnotationsInView(uiApp, parameters));
                    case "getAllRevisions":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetAllRevisions(uiApp, parameters));
                    case "getAnnotationSymbolTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetAnnotationSymbolTypes(uiApp, parameters));
                    case "getAreaTagsInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetAreaTagsInView(uiApp, parameters));
                    case "getCalloutsInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetCalloutsInView(uiApp, parameters));
                    case "getKeynoteEntries":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetKeynoteEntries(uiApp, parameters));
                    case "getKeynotesInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetKeynotesInView(uiApp, parameters));
                    case "getLegendComponents":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetLegendComponents(uiApp, parameters));
                    case "getMatchlinesInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetMatchlinesInView(uiApp, parameters));
                    case "getReferencePlanesInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetReferencePlanesInView(uiApp, parameters));
                    case "getRevisionCloudsInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetRevisionCloudsInView(uiApp, parameters));
                    case "loadKeynoteFile":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.LoadKeynoteFile(uiApp, parameters));
                    case "modifyRevision":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.ModifyRevision(uiApp, parameters));
                    case "modifyRevisionCloud":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.ModifyRevisionCloud(uiApp, parameters));
                    case "placeAngularDimension":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceAngularDimension(uiApp, parameters));
                    case "placeAnnotationSymbol":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceAnnotationSymbol(uiApp, parameters));
                    case "placeArcLengthDimension":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceArcLengthDimension(uiApp, parameters));
                    case "placeAreaTag":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceAreaTag(uiApp, parameters));
                    case "placeDiameterDimension":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceDiameterDimension(uiApp, parameters));
                    case "placeKeynote":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceKeynote(uiApp, parameters));
                    case "placeLegendComponent":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceLegendComponent(uiApp, parameters));
                    case "placeRadialDimension":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceRadialDimension(uiApp, parameters));
                    case "placeSpotCoordinate":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceSpotCoordinate(uiApp, parameters));
                    case "placeSpotElevation":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceSpotElevation(uiApp, parameters));
                    case "placeSpotSlope":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceSpotSlope(uiApp, parameters));
                    case "setRevisionIssued":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.SetRevisionIssued(uiApp, parameters));

                    // Leader Management Methods
                    case "addAnnotationLeader":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.AddAnnotationLeader(uiApp, parameters));
                    case "setLeaderEndpoint":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.SetLeaderEndpoint(uiApp, parameters));
                    case "getAnnotationLeaderInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.GetAnnotationLeaderInfo(uiApp, parameters));
                    case "placeAnnotationWithLeader":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.PlaceAnnotationWithLeader(uiApp, parameters));
                    case "batchPlaceKeynotesWithLeaders":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.AnnotationMethods.BatchPlaceKeynotesWithLeaders(uiApp, parameters));

                    // Spatial Intelligence Methods - Phase 1: Spatial Awareness
                    case "getElementBoundingBox":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.GetElementBoundingBox(uiApp, parameters));
                    case "getViewportBoundingBoxes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.GetViewportBoundingBoxes(uiApp, parameters));
                    case "getAnnotationBoundingBoxes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.GetAnnotationBoundingBoxes(uiApp, parameters));
                    case "getSheetLayout":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.GetSheetLayout(uiApp, parameters));

                    // Spatial Intelligence Methods - Phase 2: Analysis
                    case "findEmptySpaceOnSheet":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.FindEmptySpaceOnSheet(uiApp, parameters));
                    case "checkForOverlaps":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.CheckForOverlaps(uiApp, parameters));
                    case "getAnnotationsInRegion":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.GetAnnotationsInRegion(uiApp, parameters));

                    // Spatial Intelligence Methods - Phase 3: Smart Placement
                    case "placeAnnotationInZone":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.PlaceAnnotationInZone(uiApp, parameters));
                    case "placeRelativeTo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.PlaceRelativeTo(uiApp, parameters));
                    case "autoArrangeAnnotations":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.AutoArrangeAnnotations(uiApp, parameters));
                    case "suggestPlacementLocation":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.SpatialIntelligenceMethods.SuggestPlacementLocation(uiApp, parameters));

                    case "addInsulation":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.AddInsulation(uiApp, parameters));
                    case "clearElementGraphicsOverrides":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ClearElementGraphicsOverrides(uiApp, parameters));
                    case "copyDetailElements":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CopyDetailElements(uiApp, parameters));
                    case "createBreakLine":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateBreakLine(uiApp, parameters));
                    case "createDetailArc":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateDetailArc(uiApp, parameters));
                    case "createDetailGroup":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateDetailGroup(uiApp, parameters));
                    case "createDetailLine":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateDetailLine(uiApp, parameters));
                    case "createDetailPolyline":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateDetailPolyline(uiApp, parameters));
                    case "createFilledRegion":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateFilledRegion(uiApp, parameters));
                    case "createLineStyle":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateLineStyle(uiApp, parameters));
                    case "createMaskingRegion":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateMaskingRegion(uiApp, parameters));
                    case "deleteDetailElement":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.DeleteDetailElement(uiApp, parameters));
                    case "getDetailComponentInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailComponentInfo(uiApp, parameters));
                    case "getDetailComponentTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailComponentTypes(uiApp, parameters));
                    case "getDetailComponentsInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailComponentsInView(uiApp, parameters));
                    case "getDetailGroupTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailGroupTypes(uiApp, parameters));
                    case "getDetailLineInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailLineInfo(uiApp, parameters));
                    case "getDetailLinesInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailLinesInView(uiApp, parameters));
                    case "getElementGraphicsOverrides":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetElementGraphicsOverrides(uiApp, parameters));
                    case "getFilledRegionInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetFilledRegionInfo(uiApp, parameters));
                    case "getFilledRegionTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetFilledRegionTypes(uiApp, parameters));
                    case "getInsulationInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetInsulationInfo(uiApp, parameters));
                    case "getLineStyles":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetLineStyles(uiApp, parameters));
                    case "modifyDetailLine":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ModifyDetailLine(uiApp, parameters));
                    case "modifyFilledRegionBoundary":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ModifyFilledRegionBoundary(uiApp, parameters));
                    case "modifyLineStyle":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ModifyLineStyle(uiApp, parameters));
                    case "overrideElementGraphics":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.OverrideElementGraphics(uiApp, parameters));
                    case "placeDetailComponent":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.PlaceDetailComponent(uiApp, parameters));
                    case "placeDetailGroup":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.PlaceDetailGroup(uiApp, parameters));
                    case "placeMarkerSymbol":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.PlaceMarkerSymbol(uiApp, parameters));
                    case "placeRepeatingDetailComponent":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.PlaceRepeatingDetailComponent(uiApp, parameters));
                    case "removeInsulation":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.RemoveInsulation(uiApp, parameters));
                    case "createDetailComponentLibrary":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateDetailComponentLibrary(uiApp, parameters));
                    case "extractAndReplaceFilledRegions":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ExtractAndReplaceFilledRegions(uiApp, parameters));
                    // New drafting view enhancement methods
                    case "getBreakLineTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetBreakLineTypes(uiApp, parameters));
                    case "placeBreakLineAuto":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.PlaceBreakLineAuto(uiApp, parameters));
                    case "createDetailLineInDraftingView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.CreateDetailLineInDraftingView(uiApp, parameters));
                    case "dimensionDetailLines":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.DimensionDetailLines(uiApp, parameters));
                    case "getDraftingViewBounds":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDraftingViewBounds(uiApp, parameters));
                    case "placeInsulationPattern":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.PlaceInsulationPattern(uiApp, parameters));
                    case "convertDetailToDraftingView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ConvertDetailToDraftingView(uiApp, parameters));
                    case "batchConvertDetailsToDraftingViews":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.BatchConvertDetailsToDraftingViews(uiApp, parameters));
                    case "traceDetailToDraftingView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.TraceDetailToDraftingView(uiApp, parameters));
                    case "intelligentTraceDetailToDraftingView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.IntelligentTraceDetailToDraftingView(uiApp, parameters));
                    case "getDetailLibrary":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailLibrary(uiApp, parameters));
                    case "getDetailsInCategory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.GetDetailsInCategory(uiApp, parameters));
                    case "importDetailToDocument":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.ImportDetailToDocument(uiApp, parameters));
                    case "searchDetailLibrary":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.DetailMethods.SearchDetailLibrary(uiApp, parameters));
                    case "addRoomDimensions":
                        return await ExecuteInRevitContext(uiApp => DimensionMethods.AddRoomDimensions(uiApp, parameters));
                    case "getDimensionPattern":
                        return await ExecuteInRevitContext(uiApp => DimensionMethods.GetDimensionPattern(uiApp, parameters));
                    case "addRoomDimensionsWithPattern":
                        return await ExecuteInRevitContext(uiApp => DimensionMethods.AddRoomDimensionsWithPattern(uiApp, parameters));
                    case "changeFamilyInstanceType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.ChangeFamilyInstanceType(uiApp, parameters));
                    case "closeFamilyDocument":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.CloseFamilyDocument(uiApp, parameters));
                    case "createFamilyType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.CreateFamilyType(uiApp, parameters));
                    case "createInPlaceFamily":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.CreateInPlaceFamily(uiApp, parameters));
                    case "deleteFamilyInstance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.DeleteFamilyInstance(uiApp, parameters));
                    case "deleteFamilyType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.DeleteFamilyType(uiApp, parameters));
                    case "exportFamiliesToFolder":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.ExportFamiliesToFolder(uiApp, parameters));
                    case "getAllFamilies":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetAllFamilies(uiApp, parameters));
                    case "getFamiliesByCategory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamiliesByCategory(uiApp, parameters));
                    case "getFamilyCategory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyCategory(uiApp, parameters));
                    case "getFamilyInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyInfo(uiApp, parameters));
                    case "getFamilyInstanceInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyInstanceInfo(uiApp, parameters));
                    case "getFamilyInstances":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyInstances(uiApp, parameters));
                    case "getFamilyParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyParameters(uiApp, parameters));
                    case "getFamilyTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyTypes(uiApp, parameters));
                    case "getInstanceCount":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetInstanceCount(uiApp, parameters));
                    case "isFamilyLoaded":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.IsFamilyLoaded(uiApp, parameters));
                    case "loadFamiliesFromDirectory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.LoadFamiliesFromDirectory(uiApp, parameters));
                    // loadFamily moved to ElementMethods with enhanced features
                    // case "loadFamily":
                    //     return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.LoadFamily(uiApp, parameters));
                    case "modifyFamilyInstance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.ModifyFamilyInstance(uiApp, parameters));
                    case "modifyFamilyType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.ModifyFamilyType(uiApp, parameters));
                    case "openFamilyDocument":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.OpenFamilyDocument(uiApp, parameters));
                    // placeFamilyInstance moved to ElementMethods with enhanced features
                    case "purgeUnusedFamilies":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.PurgeUnusedFamilies(uiApp, parameters));
                    case "purgeUnusedTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.PurgeUnusedTypes(uiApp, parameters));
                    case "reloadFamily":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.ReloadFamily(uiApp, parameters));
                    case "renameFamilyType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.RenameFamilyType(uiApp, parameters));
                    case "saveFamilyDocument":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.SaveFamilyDocument(uiApp, parameters));
                    case "searchFamilies":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.SearchFamilies(uiApp, parameters));
                    case "setFamilyParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.SetFamilyParameter(uiApp, parameters));
                    case "getMissingFamilies":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetMissingFamilies(uiApp, parameters));
                    case "transferFamilyToDocument":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.TransferFamilyToDocument(uiApp, parameters));
                    case "batchTransferFamilies":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.BatchTransferFamilies(uiApp, parameters));
                    case "getFamilyLabels":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.GetFamilyLabels(uiApp, parameters));
                    case "editFamilyLabel":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.EditFamilyLabel(uiApp, parameters));
                    case "addFamilyParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.AddFamilyParameter(uiApp, parameters));
                    case "loadFamilyToProject":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.LoadFamilyToProject(uiApp, parameters));
                    case "editFamilyFromInstance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.EditFamilyFromInstance(uiApp, parameters));
                    case "swapFamilyTypeByCategory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FamilyMethods.SwapFamilyTypeByCategory(uiApp, parameters));
                    case "addCategoriesToFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.AddCategoriesToFilter(uiApp, parameters));
                    case "addRuleToFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.AddRuleToFilter(uiApp, parameters));
                    case "analyzeFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.AnalyzeFilter(uiApp, parameters));
                    case "applyFilterToView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.ApplyFilterToView(uiApp, parameters));
                    case "countElementsByFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.CountElementsByFilter(uiApp, parameters));
                    case "createCategoryFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.CreateCategoryFilter(uiApp, parameters));
                    case "createFilterFromTemplate":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.CreateFilterFromTemplate(uiApp, parameters));
                    case "createFilterRule":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.CreateFilterRule(uiApp, parameters));
                    case "createViewFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.CreateViewFilter(uiApp, parameters));
                    case "deleteViewFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.DeleteViewFilter(uiApp, parameters));
                    case "duplicateFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.DuplicateFilter(uiApp, parameters));
                    case "findViewsUsingFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.FindViewsUsingFilter(uiApp, parameters));
                    case "getAllViewFilters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetAllViewFilters(uiApp, parameters));
                    case "getFilterCategories":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetFilterCategories(uiApp, parameters));
                    case "getFilterOverrides":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetFilterOverrides(uiApp, parameters));
                    case "getFilterRules":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetFilterRules(uiApp, parameters));
                    case "getFilterableParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetFilterableParameters(uiApp, parameters));
                    case "getFiltersInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetFiltersInView(uiApp, parameters));
                    case "getViewFilterInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.GetViewFilterInfo(uiApp, parameters));
                    case "modifyViewFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.ModifyViewFilter(uiApp, parameters));
                    case "removeCategoriesFromFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.RemoveCategoriesFromFilter(uiApp, parameters));
                    case "removeFilterFromView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.RemoveFilterFromView(uiApp, parameters));
                    case "removeRuleFromFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.RemoveRuleFromFilter(uiApp, parameters));
                    case "selectElementsByFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.SelectElementsByFilter(uiApp, parameters));
                    case "setFilterOverrides":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.SetFilterOverrides(uiApp, parameters));
                    case "testFilter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.TestFilter(uiApp, parameters));
                    case "validateFilterRules":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.FilterMethods.ValidateFilterRules(uiApp, parameters));
                    case "calculateDuctSizing":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CalculateDuctSizing(uiApp, parameters));
                    case "calculateLoads":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CalculateLoads(uiApp, parameters));
                    case "calculatePipeSizing":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CalculatePipeSizing(uiApp, parameters));
                    case "connectElements":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.ConnectElements(uiApp, parameters));
                    case "createCableTray":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateCableTray(uiApp, parameters));
                    case "createConduit":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateConduit(uiApp, parameters));
                    case "createDuct":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateDuct(uiApp, parameters));
                    case "createDuctAccessory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateDuctAccessory(uiApp, parameters));
                    case "createDuctFitting":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateDuctFitting(uiApp, parameters));
                    case "createElectricalCircuit":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateElectricalCircuit(uiApp, parameters));
                    case "createMEPSpace":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateMEPSpace(uiApp, parameters));
                    case "createMEPSystem":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateMEPSystem(uiApp, parameters));
                    case "createPipe":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreatePipe(uiApp, parameters));
                    case "createPipeAccessory":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreatePipeAccessory(uiApp, parameters));
                    case "createPipeFitting":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreatePipeFitting(uiApp, parameters));
                    case "createZone":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.CreateZone(uiApp, parameters));
                    case "deleteMEPElement":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.DeleteMEPElement(uiApp, parameters));
                    case "getConnectors":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetConnectors(uiApp, parameters));
                    case "getDuctInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetDuctInfo(uiApp, parameters));
                    case "getDuctsInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetDuctsInView(uiApp, parameters));
                    case "getElectricalCircuits":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetElectricalCircuits(uiApp, parameters));
                    case "getElectricalPathInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetElectricalPathInfo(uiApp, parameters));
                    case "getEquipmentInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetEquipmentInfo(uiApp, parameters));
                    case "getMEPSystems":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetMEPSystems(uiApp, parameters));
                    case "getMEPTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetMEPTypes(uiApp, parameters));
                    case "getPipeInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetPipeInfo(uiApp, parameters));
                    case "getPipesInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetPipesInView(uiApp, parameters));
                    case "getSpaceInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetSpaceInfo(uiApp, parameters));
                    case "getSystemInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.GetSystemInfo(uiApp, parameters));
                    case "modifySystemElements":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.ModifySystemElements(uiApp, parameters));
                    case "placeElectricalEquipment":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.PlaceElectricalEquipment(uiApp, parameters));
                    case "placeElectricalFixture":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.PlaceElectricalFixture(uiApp, parameters));
                    case "placeMechanicalEquipment":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.PlaceMechanicalEquipment(uiApp, parameters));
                    case "placePlumbingFixture":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.PlacePlumbingFixture(uiApp, parameters));
                    case "tagSpace":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MEPMethods.TagSpace(uiApp, parameters));
                    case "createAppearanceAsset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.CreateAppearanceAsset(uiApp, parameters));
                    case "createMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.CreateMaterial(uiApp, parameters));
                    case "deleteMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.DeleteMaterial(uiApp, parameters));
                    case "duplicateAppearanceAsset":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.DuplicateAppearanceAsset(uiApp, parameters));
                    case "modifyAppearanceAssetColor":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.ModifyAppearanceAssetColor(uiApp, parameters));
                    case "getAppearanceAssetDetails":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetAppearanceAssetDetails(uiApp, parameters));
                    case "createMaterialWithAppearance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.CreateMaterialWithAppearance(uiApp, parameters));
                    case "duplicateMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.DuplicateMaterial(uiApp, parameters));
                    case "exportMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.ExportMaterial(uiApp, parameters));
                    case "findElementsWithMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.FindElementsWithMaterial(uiApp, parameters));
                    case "getAllMaterials":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetAllMaterials(uiApp, parameters));
                    case "getAppearanceAssets":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetAppearanceAssets(uiApp, parameters));
                    case "getMaterialAppearance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialAppearance(uiApp, parameters));
                    case "getMaterialByName":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialByName(uiApp, parameters));
                    case "getMaterialClasses":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialClasses(uiApp, parameters));
                    case "getMaterialInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialInfo(uiApp, parameters));
                    case "getMaterialPhysicalProperties":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialPhysicalProperties(uiApp, parameters));
                    case "getMaterialSurfacePattern":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialSurfacePattern(uiApp, parameters));
                    case "getMaterialUsageStats":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.GetMaterialUsageStats(uiApp, parameters));
                    case "isMaterialInUse":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.IsMaterialInUse(uiApp, parameters));
                    case "loadMaterialFromLibrary":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.LoadMaterialFromLibrary(uiApp, parameters));
                    case "modifyMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.ModifyMaterial(uiApp, parameters));
                    case "replaceMaterial":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.ReplaceMaterial(uiApp, parameters));
                    case "searchMaterials":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SearchMaterials(uiApp, parameters));
                    case "setMaterialAppearance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SetMaterialAppearance(uiApp, parameters));
                    case "setMaterialClass":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SetMaterialClass(uiApp, parameters));
                    case "setMaterialPhysicalProperties":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SetMaterialPhysicalProperties(uiApp, parameters));
                    case "setMaterialSurfacePattern":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SetMaterialSurfacePattern(uiApp, parameters));
                    case "setMaterialTexture":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SetMaterialTexture(uiApp, parameters));

                    // Paint Methods
                    case "paintElementFace":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.PaintElementFace(uiApp, parameters));
                    case "paintWall":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.PaintWall(uiApp, parameters));
                    case "paintWalls":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.PaintWalls(uiApp, parameters));
                    case "removePaint":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.RemovePaint(uiApp, parameters));
                    case "isPainted":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.IsPainted(uiApp, parameters));

                    case "setRenderAppearance":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.MaterialMethods.SetRenderAppearance(uiApp, parameters));
                    case "bindSharedParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.BindSharedParameter(uiApp, parameters));
                    case "copyParameterValues":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.CopyParameterValues(uiApp, parameters));
                    case "createGlobalParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.CreateGlobalParameter(uiApp, parameters));
                    case "createParameterGroup":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.CreateParameterGroup(uiApp, parameters));
                    case "createProjectParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.CreateProjectParameter(uiApp, parameters));
                    case "createSharedParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.CreateSharedParameter(uiApp, parameters));
                    case "deleteGlobalParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.DeleteGlobalParameter(uiApp, parameters));
                    case "deleteProjectParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.DeleteProjectParameter(uiApp, parameters));
                    case "findElementsByParameterValue":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.FindElementsByParameterValue(uiApp, parameters));
                    case "getElementParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetElementParameters(uiApp, parameters));
                    case "getGlobalParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetGlobalParameters(uiApp, parameters));
                    case "getParameterDefinition":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetParameterDefinition(uiApp, parameters));
                    case "getParameterFormula":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetParameterFormula(uiApp, parameters));
                    case "getParameterGroups":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetParameterGroups(uiApp, parameters));
                    case "getParameterStorageType":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetParameterStorageType(uiApp, parameters));
                    case "getParameterTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetParameterTypes(uiApp, parameters));
                    case "getParameterValue":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetParameterValue(uiApp, parameters));
                    case "getProjectParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetProjectParameters(uiApp, parameters));
                    case "getSharedParameterDefinitions":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetSharedParameterDefinitions(uiApp, parameters));
                    case "getSharedParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.GetSharedParameters(uiApp, parameters));
                    case "loadSharedParameterFile":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.LoadSharedParameterFile(uiApp, parameters));
                    case "modifyGlobalParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.ModifyGlobalParameter(uiApp, parameters));
                    case "modifyProjectParameter":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.ModifyProjectParameter(uiApp, parameters));
                    case "parameterExists":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.ParameterExists(uiApp, parameters));
                    case "searchParameters":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.SearchParameters(uiApp, parameters));
                    case "setMultipleParameterValues":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.SetMultipleParameterValues(uiApp, parameters));
                    case "setParameterForMultipleElements":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.SetParameterForMultipleElements(uiApp, parameters));
                    case "setParameterFormula":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.SetParameterFormula(uiApp, parameters));
                    case "setParameterValue":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.SetParameterValue(uiApp, parameters));
                    case "bulkSetParameterConditional":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.ParameterMethods.BulkSetParameterConditional(uiApp, parameters));
                    case "createAreaLoad":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateAreaLoad(uiApp, parameters));
                    case "createFoundation":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateFoundation(uiApp, parameters));
                    case "createLineLoad":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateLineLoad(uiApp, parameters));
                    case "createPointLoad":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreatePointLoad(uiApp, parameters));
                    case "createRebar":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateRebar(uiApp, parameters));
                    case "createStructuralBeam":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateStructuralBeam(uiApp, parameters));
                    case "createStructuralConnection":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateStructuralConnection(uiApp, parameters));
                    case "deleteStructuralElement":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.DeleteStructuralElement(uiApp, parameters));
                    case "getAllStructuralElements":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetAllStructuralElements(uiApp, parameters));
                    case "createEscalator":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.CreateEscalator(uiApp, parameters));
                    case "getAnalysisResults":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetAnalysisResults(uiApp, parameters));
                    case "getAnalyticalModel":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetAnalyticalModel(uiApp, parameters));
                    case "getConnectionInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetConnectionInfo(uiApp, parameters));
                    case "getElementLoads":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetElementLoads(uiApp, parameters));
                    case "getFoundationInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetFoundationInfo(uiApp, parameters));
                    case "getFoundationTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetFoundationTypes(uiApp, parameters));
                    case "getRebarInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetRebarInfo(uiApp, parameters));
                    case "getStructuralBeamInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetStructuralBeamInfo(uiApp, parameters));
                    case "getStructuralBeamTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetStructuralBeamTypes(uiApp, parameters));
                    case "getStructuralColumnInfo":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetStructuralColumnInfo(uiApp, parameters));
                    case "getStructuralColumnTypes":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetStructuralColumnTypes(uiApp, parameters));
                    case "getStructuralFramingInView":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.GetStructuralFramingInView(uiApp, parameters));
                    case "modifyStructuralBeam":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.ModifyStructuralBeam(uiApp, parameters));
                    case "modifyStructuralColumn":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.ModifyStructuralColumn(uiApp, parameters));
                    case "placeStructuralColumn":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.PlaceStructuralColumn(uiApp, parameters));
                    case "placeStructuralFraming":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.PlaceStructuralFraming(uiApp, parameters));
                    case "setAnalyticalProperties":
                        return await ExecuteInRevitContext(uiApp => RevitMCPBridge2026.StructuralMethods.SetAnalyticalProperties(uiApp, parameters));

                    // AUTONOMOUS WORKFLOW METHODS - AI Agent Orchestration
                    case "executeWorkflow":
                        return await ExecuteInRevitContext(uiApp => WorkflowMethods.ExecuteWorkflow(uiApp, parameters));

                    case "getWorkflowStatus":
                        return await ExecuteInRevitContext(uiApp => WorkflowMethods.GetWorkflowStatus(uiApp, parameters));

                    case "listWorkflowTemplates":
                        return await ExecuteInRevitContext(uiApp => WorkflowMethods.ListWorkflowTemplates(uiApp, parameters));

                    case "pauseWorkflow":
                        return await ExecuteInRevitContext(uiApp => WorkflowMethods.PauseWorkflow(uiApp, parameters));

                    case "resumeWorkflow":
                        return await ExecuteInRevitContext(uiApp => WorkflowMethods.ResumeWorkflow(uiApp, parameters));

                    // Sheet Pattern Methods (11 methods)
                    case "detectSheetPattern":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.DetectSheetPattern(uiApp, parameters));

                    case "getPatternRules":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetPatternRules(uiApp, parameters));

                    case "generateFloorPlanSheets":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GenerateFloorPlanSheets(uiApp, parameters));

                    case "generateCompleteSheetSet":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GenerateCompleteSheetSet(uiApp, parameters));

                    case "createSheetsFromPattern":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.CreateSheetsFromPattern(uiApp, parameters));

                    case "createClientProfile":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.CreateClientProfile(uiApp, parameters));

                    case "getClientProfile":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetClientProfile(uiApp, parameters));

                    case "listKnownFirms":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.ListKnownFirms(uiApp, parameters));

                    case "detectExistingPattern":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.DetectExistingPattern(uiApp, parameters));

                    case "suggestNextSheetNumber":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.SuggestNextSheetNumber(uiApp, parameters));

                    case "convertBetweenPatterns":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.ConvertBetweenPatterns(uiApp, parameters));

                    // Guide Grid Methods
                    case "getGuideGrids":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetGuideGrids(uiApp, parameters));

                    case "getSheetGuideGrid":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetSheetGuideGrid(uiApp, parameters));

                    case "applyGuideGridToSheet":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.ApplyGuideGridToSheet(uiApp, parameters));

                    case "applyGuideGridToSheets":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.ApplyGuideGridToSheets(uiApp, parameters));

                    case "removeGuideGridFromSheet":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.RemoveGuideGridFromSheet(uiApp, parameters));

                    case "deleteGuideGrid":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.DeleteGuideGrid(uiApp, parameters));

                    case "getStandardDetailGridSpec":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetStandardDetailGridSpec(uiApp, parameters));

                    case "getDetailGridCells":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetDetailGridCells(uiApp, parameters));

                    case "calculateViewCellRequirements":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.CalculateViewCellRequirements(uiApp, parameters));

                    case "getNextAvailableGridCell":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.GetNextAvailableGridCell(uiApp, parameters));

                    case "placeViewOnGridCell":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.PlaceViewOnGridCell(uiApp, parameters));

                    case "placeLegendOnSheetRight":
                        return await ExecuteInRevitContext(uiApp => SheetPatternMethods.PlaceLegendOnSheetRight(uiApp, parameters));

                    // Viewport Capture & Camera Control Methods
                    case "captureViewport":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.CaptureViewport(uiApp, parameters));

                    case "captureViewportToBase64":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.CaptureViewportToBase64(uiApp, parameters));

                    case "analyzeView":
                        // Vision analysis - requires API key passed in parameters
                        var apiKey = parameters?["apiKey"]?.ToString();
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.AnalyzeView(uiApp, parameters, apiKey));

                    case "setCamera":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.SetCamera(uiApp, parameters));

                    case "getCamera":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.GetCamera(uiApp, parameters));

                    case "setViewStyle":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.SetViewStyle(uiApp, parameters));

                    case "listViews":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.ListViews(uiApp, parameters));

                    case "create3DView":
                        return await ExecuteInRevitContext(uiApp => ViewportCaptureMethods.Create3DView(uiApp, parameters));

                    // Scene Analysis Methods (AI Rendering)
                    case "getVisibleElements":
                        return await ExecuteInRevitContext(uiApp => SceneAnalysisMethods.GetVisibleElements(uiApp, parameters));

                    case "getViewMaterials":
                        return await ExecuteInRevitContext(uiApp => SceneAnalysisMethods.GetViewMaterials(uiApp, parameters));

                    case "getSceneDescription":
                        return await ExecuteInRevitContext(uiApp => SceneAnalysisMethods.GetSceneDescription(uiApp, parameters));

                    case "exportViewForRender":
                        return await ExecuteInRevitContext(uiApp => SceneAnalysisMethods.ExportViewForRender(uiApp, parameters));

                    // AI Rendering Methods
                    case "submitRender":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.SubmitRender(uiApp, parameters));

                    case "getRenderStatus":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.GetRenderStatus(uiApp, parameters));

                    case "getRenderResult":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.GetRenderResult(uiApp, parameters));

                    case "listRenderJobs":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.ListRenderJobs(uiApp, parameters));

                    case "cancelRender":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.CancelRender(uiApp, parameters));

                    case "getRenderPresets":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.GetRenderPresets(uiApp, parameters));

                    case "captureAndRender":
                        return await ExecuteInRevitContext(uiApp => RenderMethods.CaptureAndRender(uiApp, parameters));

                    // GridMethods - Extended Grid Operations
                    case "renameGrid":
                        return await ExecuteInRevitContext(uiApp => GridMethods.RenameGrid(uiApp, parameters));
                    case "createGridArray":
                        return await ExecuteInRevitContext(uiApp => GridMethods.CreateGridArray(uiApp, parameters));
                    case "setGridExtents":
                        return await ExecuteInRevitContext(uiApp => GridMethods.SetGridExtents(uiApp, parameters));
                    case "getGridIntersection":
                        return await ExecuteInRevitContext(uiApp => GridMethods.GetGridIntersection(uiApp, parameters));
                    case "getGrid":
                        return await ExecuteInRevitContext(uiApp => GridMethods.GetGrid(uiApp, parameters));

                    // LevelMethods - Extended Level Operations
                    case "renameLevel":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.RenameLevel(uiApp, parameters));
                    case "setLevelElevation":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.SetLevelElevation(uiApp, parameters));
                    case "createLevelArray":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.CreateLevelArray(uiApp, parameters));
                    case "getLevelByElevation":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.GetLevelByElevation(uiApp, parameters));
                    case "getElementsOnLevel":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.GetElementsOnLevel(uiApp, parameters));
                    case "copyElementsToLevel":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.CopyElementsToLevel(uiApp, parameters));
                    case "getLevel":
                        return await ExecuteInRevitContext(uiApp => LevelMethods.GetLevel(uiApp, parameters));

                    // StairRailingMethods - Stairs, Railings, and Ramps (extended methods - getStairs/getRailings already in FloorCeilingRoofMethods)
                    case "getStairTypes":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.GetStairTypes(uiApp, parameters));
                    case "getStairDetails":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.GetStairDetails(uiApp, parameters));
                    case "deleteStair":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.DeleteStair(uiApp, parameters));
                    case "getRailingTypes":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.GetRailingTypes(uiApp, parameters));
                    case "createRailing":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.CreateRailing(uiApp, parameters));
                    case "deleteRailing":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.DeleteRailing(uiApp, parameters));
                    case "getRamps":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.GetRamps(uiApp, parameters));
                    case "getRampTypes":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.GetRampTypes(uiApp, parameters));
                    case "deleteRamp":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.DeleteRamp(uiApp, parameters));
                    case "getStairRailings":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.GetStairRailings(uiApp, parameters));
                    case "createStairBySketch":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.CreateStairBySketch(uiApp, parameters));
                    case "createStairByComponent":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.CreateStairByComponent(uiApp, parameters));
                    case "modifyStair":
                        return await ExecuteInRevitContext(uiApp => StairRailingMethods.ModifyStair(uiApp, parameters));

                    // SiteMethods - Topography, Building Pads, Property Lines
                    case "getTopographySurfaces":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.GetTopographySurfaces(uiApp, parameters));
                    case "getTopographyPoints":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.GetTopographyPoints(uiApp, parameters));
                    case "addTopographyPoints":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.AddTopographyPoints(uiApp, parameters));
                    case "deleteTopography":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.DeleteTopography(uiApp, parameters));
                    case "getBuildingPads":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.GetBuildingPads(uiApp, parameters));
                    case "getPropertyLines":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.GetPropertyLines(uiApp, parameters));
                    case "createPropertyLine":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.CreatePropertyLine(uiApp, parameters));
                    case "getSiteInfo":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.GetSiteInfo(uiApp, parameters));
                    case "setSiteLocation":
                        return await ExecuteInRevitContext(uiApp => SiteMethods.SetSiteLocation(uiApp, parameters));

                    // LinkMethods - Revit Links and CAD Imports
                    case "getRevitLinks":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.GetRevitLinks(uiApp, parameters));
                    case "getRevitLinkTypes":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.GetRevitLinkTypes(uiApp, parameters));
                    case "loadRevitLink":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.LoadRevitLink(uiApp, parameters));
                    case "reloadRevitLink":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.ReloadRevitLink(uiApp, parameters));
                    case "unloadRevitLink":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.UnloadRevitLink(uiApp, parameters));
                    case "deleteRevitLink":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.DeleteRevitLink(uiApp, parameters));
                    case "getCADLinks":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.GetCADLinks(uiApp, parameters));
                    case "importCAD":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.ImportCAD(uiApp, parameters));
                    case "deleteCADLink":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.DeleteCADLink(uiApp, parameters));
                    case "getCADGeometry":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.GetCADGeometry(uiApp, parameters));
                    case "getCADWallCandidates":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.GetCADWallCandidates(uiApp, parameters));
                    case "analyzeCADFloorPlan":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.AnalyzeCADFloorPlan(uiApp, parameters));
                    case "getLinkTransform":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.GetLinkTransform(uiApp, parameters));
                    case "moveLinkInstance":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.MoveLinkInstance(uiApp, parameters));
                    case "queryLinkedElementCoordinates":
                        return await ExecuteInRevitContext(uiApp => LinkMethods.QueryLinkedElementCoordinates(uiApp, parameters));

                    // GroupMethods - Groups and Assemblies
                    case "getGroups":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.GetGroups(uiApp, parameters));
                    case "getGroupMembers":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.GetGroupMembers(uiApp, parameters));
                    case "ungroupGroup":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.UngroupGroup(uiApp, parameters));
                    case "deleteGroupType":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.DeleteGroupType(uiApp, parameters));
                    case "renameGroupType":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.RenameGroupType(uiApp, parameters));
                    case "duplicateGroupType":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.DuplicateGroupType(uiApp, parameters));
                    case "getModelGroups":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.GetModelGroups(uiApp, parameters));
                    case "getDetailGroups":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.GetDetailGroups(uiApp, parameters));
                    case "createGroupAndReplicatePattern":
                        return await ExecuteInRevitContext(uiApp => GroupMethods.CreateGroupAndReplicatePattern(uiApp, parameters));

                    // RevisionMethods - Extended Revision functionality (unique methods only)
                    // getRevisions and getRevisionClouds moved to DocumentMethods with enhanced features
                    // case "getRevisions":
                    //     return await ExecuteInRevitContext(uiApp => RevisionMethods.GetRevisions(uiApp, parameters));
                    case "updateRevision":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.UpdateRevision(uiApp, parameters));
                    case "issueRevision":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.IssueRevision(uiApp, parameters));
                    case "deleteRevision":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.DeleteRevision(uiApp, parameters));
                    // case "getRevisionClouds":
                    //     return await ExecuteInRevitContext(uiApp => RevisionMethods.GetRevisionClouds(uiApp, parameters));
                    case "getSheetRevisions":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.GetSheetRevisions(uiApp, parameters));
                    case "addRevisionsToSheet":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.AddRevisionsToSheet(uiApp, parameters));
                    case "removeRevisionsFromSheet":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.RemoveRevisionsFromSheet(uiApp, parameters));
                    case "setRevisionVisibility":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.SetRevisionVisibility(uiApp, parameters));
                    case "reorderRevisions":
                        return await ExecuteInRevitContext(uiApp => RevisionMethods.ReorderRevisions(uiApp, parameters));

                    // ========== BATCH METHODS ==========
                    // High-efficiency batch operations with single transaction
                    case "executeBatch":
                        Log.Information("[MCPServer] Routing executeBatch to BatchMethods");
                        return await ExecuteInRevitContext(uiApp => BatchMethods.ExecuteBatch(uiApp, parameters));

                    case "createWallBatch":
                        Log.Information("[MCPServer] Routing createWallBatch to BatchMethods");
                        return await ExecuteInRevitContext(uiApp => BatchMethods.CreateWallBatch(uiApp, parameters));

                    case "placeElementsBatch":
                        Log.Information("[MCPServer] Routing placeElementsBatch to BatchMethods");
                        return await ExecuteInRevitContext(uiApp => BatchMethods.PlaceElementsBatch(uiApp, parameters));

                    case "deleteElementsBatch":
                        Log.Information("[MCPServer] Routing deleteElementsBatch to BatchMethods");
                        return await ExecuteInRevitContext(uiApp => BatchMethods.DeleteElementsBatch(uiApp, parameters));

                    case "setParametersBatch":
                        Log.Information("[MCPServer] Routing setParametersBatch to BatchMethods");
                        return await ExecuteInRevitContext(uiApp => BatchMethods.SetParametersBatch(uiApp, parameters));

                    // ========== VALIDATION METHODS ==========
                    case "verifyElement":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.VerifyElement(uiApp, parameters));

                    case "verifyTextContent":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.VerifyTextContent(uiApp, parameters));

                    case "getValidationSnapshot":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.GetViewSnapshot(uiApp, parameters));

                    case "compareViewState":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.CompareViewState(uiApp, parameters));

                    case "verifyBatch":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.VerifyBatch(uiApp, parameters));

                    case "verifyOperation":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.VerifyOperation(uiApp, parameters));

                    case "verifyElementCount":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.VerifyElementCount(uiApp, parameters));

                    case "getModelState":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.GetModelState(uiApp, parameters));

                    case "preFlightCheck":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.PreFlightCheck(uiApp, parameters));

                    case "validateElementSpacingAndAlignment":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.ValidateElementSpacingAndAlignment(uiApp, parameters));

                    // ========== ORCHESTRATION METHODS ==========
                    case "createLifeSafetyLegend":
                        return await ExecuteInRevitContext(uiApp => OrchestrationMethods.CreateLifeSafetyLegend(uiApp, parameters));

                    case "createAreaCalculationLegend":
                        return await ExecuteInRevitContext(uiApp => OrchestrationMethods.CreateAreaCalculationLegend(uiApp, parameters));

                    case "executeOrchestrationWorkflow":
                        return await ExecuteInRevitContext(uiApp => OrchestrationMethods.ExecuteWorkflow(uiApp, parameters));

                    case "reviewViewForIssues":
                        return await ExecuteInRevitContext(uiApp => OrchestrationMethods.ReviewViewForIssues(uiApp, parameters));

                    case "batchModifyTextNotes":
                        return await ExecuteInRevitContext(uiApp => OrchestrationMethods.BatchModifyTextNotes(uiApp, parameters));

                    case "smartPlaceElement":
                        return await ExecuteInRevitContext(uiApp => OrchestrationMethods.SmartPlaceElement(uiApp, parameters));

                    // ========== SELF-HEALING METHODS ==========
                    case "recordOperation":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.RecordOperation(uiApp, parameters));

                    case "getOperationHistory":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.GetOperationHistory(uiApp, parameters));

                    case "clearOperationHistory":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.ClearOperationHistory(uiApp, parameters));

                    case "detectAnomalies":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.DetectAnomalies(uiApp, parameters));

                    case "attemptRecovery":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.AttemptRecovery(uiApp, parameters));

                    case "undoLastOperation":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.UndoLastOperation(uiApp, parameters));

                    case "safeDeleteElement":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.SafeDeleteElement(uiApp, parameters));

                    case "safeModifyElement":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.SafeModifyElement(uiApp, parameters));

                    case "healthCheck":
                        return await ExecuteInRevitContext(uiApp => SelfHealingMethods.HealthCheck(uiApp, parameters));

                    // ==================== PROJECT ANALYSIS METHODS (Predictive Intelligence) ====================
                    // Phase 1: Deep Project Analysis for gap detection and prediction

                    case "analyzeProjectState":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.AnalyzeProjectState(uiApp, parameters));

                    case "getSheetViewMatrix":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.GetSheetViewMatrix(uiApp, parameters));

                    case "getViewPlacementStatus":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.GetViewPlacementStatus(uiApp, parameters));

                    case "getLevelViewCoverage":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.GetLevelViewCoverage(uiApp, parameters));

                    case "detectNamingPatterns":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.DetectNamingPatterns(uiApp, parameters));

                    case "analyzeProjectGaps":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.AnalyzeProjectGaps(uiApp, parameters));

                    case "extractProjectDataMatrix":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.ExtractProjectDataMatrix(uiApp, parameters));

                    case "analyzeCirculationPatterns":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.AnalyzeCirculationPatterns(uiApp, parameters));

                    case "validateSpaceEfficiency":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.ValidateSpaceEfficiency(uiApp, parameters));

                    case "extractBuildingEnvelopeMetrics":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.ExtractBuildingEnvelopeMetrics(uiApp, parameters));

                    case "extractAcousticProperties":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.ExtractAcousticProperties(uiApp, parameters));

                    case "extractDaylightingAnalysis":
                        return await ExecuteInRevitContext(uiApp => ProjectAnalysisMethods.ExtractDaylightingAnalysis(uiApp, parameters));

                    // ==================== STANDARDS ENGINE METHODS (Phase 2) ====================
                    // Compare projects against standards and learn from existing projects

                    case "getAvailableStandards":
                        return await ExecuteInRevitContext(uiApp => StandardsEngineMethods.GetAvailableStandards(uiApp, parameters));

                    case "compareToStandard":
                        return await ExecuteInRevitContext(uiApp => StandardsEngineMethods.CompareToStandard(uiApp, parameters));

                    case "learnFromProject":
                        return await ExecuteInRevitContext(uiApp => StandardsEngineMethods.LearnFromProject(uiApp, parameters));

                    case "predictNextSteps":
                        return await ExecuteInRevitContext(uiApp => StandardsEngineMethods.PredictNextSteps(uiApp, parameters));

                    // ==================== EXECUTION ENGINE METHODS (Phase 5) ====================
                    // Execute predictions and automated actions

                    case "executePrediction":
                        return await ExecuteInRevitContext(uiApp => ExecutionEngineMethods.ExecutePrediction(uiApp, parameters));

                    case "executePredictions":
                        return await ExecuteInRevitContext(uiApp => ExecutionEngineMethods.ExecutePredictions(uiApp, parameters));

                    case "autoPlaceSchedule":
                        return await ExecuteInRevitContext(uiApp => ExecutionEngineMethods.AutoPlaceSchedule(uiApp, parameters));

                    case "autoPlaceView":
                        return await ExecuteInRevitContext(uiApp => ExecutionEngineMethods.AutoPlaceView(uiApp, parameters));

                    case "autoFixGaps":
                        return await ExecuteInRevitContext(uiApp => ExecutionEngineMethods.AutoFixGaps(uiApp, parameters));

// Orchestration layer: +7 validation + 6 orchestration + 9 self-healing = 22 new methods
// Predictive Intelligence: +6 project analysis + 4 standards engine + 5 execution engine = 15 new methods

                    // ==================== CHANGE TRACKER METHODS ====================
                    // Real-time change detection and monitoring
                    case "getRecentChanges":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.GetRecentChanges(uiApp, parameters));

                    case "getChangesSince":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.GetChangesSince(uiApp, parameters));

                    case "getCurrentSelection":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.GetCurrentSelection(uiApp, parameters));

                    case "getActiveViewInfo":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.GetActiveViewInfo(uiApp, parameters));

                    case "getLastChangeTime":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.GetLastChangeTime(uiApp, parameters));

                    case "getChangeStatistics":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.GetChangeStatistics(uiApp, parameters));

                    case "clearChangeLog":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.ClearChangeLog(uiApp, parameters));

                    case "watchChanges":
                        return await ExecuteInRevitContext(uiApp => ChangeTrackerMethods.WatchChanges(uiApp, parameters));

                    // ==================== BATCH PROCESSING METHODS ====================
                    // Autonomous task queue execution for multi-hour workflows

                    case "loadBatch":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.LoadBatch(uiApp, parameters));

                    case "resumeBatch":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.ResumeBatch(uiApp, parameters));

                    case "createBatch":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.CreateBatch(uiApp, parameters));

                    case "executeNextTask":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.ExecuteNextTask(uiApp, parameters));

                    case "executeAllTasks":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.ExecuteAllTasks(uiApp, parameters));

                    case "pauseBatch":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.PauseBatch(uiApp, parameters));

                    case "getBatchStatus":
                        return await ExecuteInRevitContext(uiApp => BatchProcessor.GetBatchStatus(uiApp, parameters));

                    case "getBatchMethods":
                        return await ExecuteInRevitContext(uiApp => MCPServer.GetBatchMethods(uiApp, parameters));

                    // ========== TASK PARSING METHODS ==========
                    case "parseTasks":
                        return await ExecuteInRevitContext(uiApp => TaskParser.ParseTasks(uiApp, parameters));

                    case "getSupportedPatterns":
                        return await ExecuteInRevitContext(uiApp => TaskParser.GetSupportedPatterns(uiApp, parameters));

                    // ==================== INTELLIGENCE METHODS ====================
                    // Proactive assistance, workflow analysis, and preference learning

                    // Proactive Assistant
                    case "getSuggestions":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetSuggestions(uiApp, parameters));

                    case "getAssistanceSummary":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetAssistanceSummary(uiApp, parameters));

                    case "getTaskAssistance":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetTaskAssistance(uiApp, parameters));

                    case "respondToSuggestion":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.RespondToSuggestion(uiApp, parameters));

                    // Workflow Analysis
                    case "getWorkflowStatistics":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetWorkflowStatistics(uiApp, parameters));

                    case "getWorkflowPatterns":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetWorkflowPatterns(uiApp, parameters));

                    case "getActionFrequencies":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetActionFrequencies(uiApp, parameters));

                    case "startTaskTracking":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.StartTaskTracking(uiApp, parameters));

                    case "endTaskTracking":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.EndTaskTracking(uiApp, parameters));

                    // Preference Learning
                    case "getLearnedPreferences":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetLearnedPreferences(uiApp, parameters));

                    case "getPreferredScale":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetPreferredScale(uiApp, parameters));

                    case "exportPreferencesForMemory":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.ExportPreferencesForMemory(uiApp, parameters));

                    // Layout Intelligence
                    case "getSheetLayoutRecommendation":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetSheetLayoutRecommendation(uiApp, parameters));

                    case "getRecommendedScale":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetRecommendedScale(uiApp, parameters));

                    case "suggestViewportPosition":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.SuggestViewportPosition(uiApp, parameters));

                    // Learning Reports
                    case "getLearningReport":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetLearningReport(uiApp, parameters));

                    case "exportWorkflowData":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.ExportWorkflowData(uiApp, parameters));

                    // Correction Learning
                    case "storeCorrection":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.StoreCorrection(uiApp, parameters));

                    case "getRelevantCorrections":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetRelevantCorrections(uiApp, parameters));

                    case "getMethodCorrections":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetMethodCorrections(uiApp, parameters));

                    case "getCorrectionStats":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetCorrectionStats(uiApp, parameters));

                    case "getCorrectionsAsKnowledge":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetCorrectionsAsKnowledge(uiApp, parameters));

                    case "markCorrectionApplied":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.MarkCorrectionApplied(uiApp, parameters));

                    case "deleteCorrection":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.DeleteCorrection(uiApp, parameters));

                    // Result Verification
                    case "verifyOperationResult":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.VerifyOperationResult(uiApp, parameters));

                    // Workflow Planning
                    case "analyzeWorkflowRequest":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.AnalyzeWorkflowRequest(uiApp, parameters));

                    case "getWorkflowTemplates":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetWorkflowTemplates(uiApp, parameters));

                    case "setProjectContext":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.SetProjectContext(uiApp, parameters));

                    case "getProjectContext":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetProjectContext(uiApp, parameters));

                    // Memory Sync (for claude-memory MCP integration)
                    case "exportAllIntelligence":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.ExportAllIntelligence(uiApp, parameters));

                    case "getSessionSummaryForMemory":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetSessionSummaryForMemory(uiApp, parameters));

                    case "importPreferencesFromMemory":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.ImportPreferencesFromMemory(uiApp, parameters));

                    // ============================================================
                    // NATURAL LANGUAGE PROCESSING - BIM Ops Studio Core
                    // ============================================================

                    case "processNaturalLanguage":
                        return await ProcessNaturalLanguageInput(parameters);

                    case "confirmNaturalLanguageCommand":
                        return await ConfirmAndExecuteNaturalLanguageCommand(parameters);

                    case "getNLPStatus":
                        return GetNLPStatus();

                    // ============================================================
                    // LEVEL 3: AUTONOMOUS INTELLIGENCE
                    // ============================================================

                    case "smartExecute":
                        return await SmartExecute(parameters["targetMethod"]?.ToString() ?? "", parameters["targetParams"] as JObject ?? new JObject());

                    case "executeWorkflowStep":
                        return await ExecuteWorkflowStep(parameters);

                    case "getPreExecutionIntelligence":
                        return GetPreExecutionIntelligence(parameters["method"]?.ToString() ?? "", parameters);

                    // ============================================================
                    // LEVEL 4: PROACTIVE INTELLIGENCE
                    // ============================================================

                    case "runProactiveAnalysis":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.RunProactiveAnalysis(uiApp, parameters));

                    case "getMonitorSuggestions":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetMonitorSuggestions(uiApp, parameters));

                    case "executeSuggestion":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.ExecuteSuggestion(uiApp, parameters));

                    case "takeModelSnapshot":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.TakeModelSnapshot(uiApp, parameters));

                    case "analyzeUnplacedViews":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetUnplacedViews(uiApp, parameters));

                    case "analyzeUntaggedRooms":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetUntaggedRooms(uiApp, parameters));

                    case "getAutoCorrections":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.GetAutoCorrections(uiApp, parameters));

                    case "dismissSuggestion":
                        return await ExecuteInRevitContext(uiApp => IntelligenceMethods.DismissSuggestion(uiApp, parameters));

                    case "getMonitoringStats":
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            stats = ProactiveMonitor.Instance.GetMonitoringStats(),
                            shouldRunAnalysis = ProactiveMonitor.Instance.ShouldRunProactiveAnalysis()
                        });

                    // ============================================================
                    // LEVEL 5: FULL AUTONOMY
                    // ============================================================

                    case "executeGoal":
                        return await IntelligenceMethods.ExecuteGoal(_currentUiApp, parameters);

                    case "approveTask":
                        return await IntelligenceMethods.ApproveTask(_currentUiApp, parameters);

                    case "cancelTask":
                        return IntelligenceMethods.CancelTask(_currentUiApp, parameters);

                    case "getAutonomousTasks":
                        return IntelligenceMethods.GetActiveTasks(_currentUiApp, parameters);

                    case "getTaskResult":
                        return IntelligenceMethods.GetTaskResult(_currentUiApp, parameters);

                    case "configureAutonomy":
                        return IntelligenceMethods.ConfigureAutonomy(_currentUiApp, parameters);

                    case "getAutonomyStats":
                        return IntelligenceMethods.GetAutonomyStats(_currentUiApp, parameters);

                    case "getSupportedGoals":
                        return IntelligenceMethods.GetSupportedGoals(_currentUiApp, parameters);

                    // ============================================================
                    // UI AUTOMATION METHODS
                    // ============================================================

                    case "getSelectionInfo":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.GetSelectionInfo(uiApp, parameters));

                    case "sendKeySequence":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.SendKeySequence(uiApp, parameters));

                    case "getRibbonState":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.GetRibbonState(uiApp, parameters));

                    case "canExecuteCommand":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.CanExecuteCommand(uiApp, parameters));

                    case "clearSelection":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.ClearSelection(uiApp, parameters));

                    case "setSelection":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.SetSelection(uiApp, parameters));

                    case "getPropertiesPaletteState":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.GetPropertiesPaletteState(uiApp, parameters));

                    case "getProjectBrowserState":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.GetProjectBrowserState(uiApp, parameters));

                    case "zoomToSelected":
                        return await ExecuteInRevitContext(uiApp => UIAutomationMethods.ZoomToSelected(uiApp, parameters));

                    // ==================== VALIDATION & QC METHODS ====================

                    case "detectClashes":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.DetectClashes(uiApp, parameters));

                    case "validateModelHealth":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.ValidateModelHealth(uiApp, parameters));

                    case "autoPlaceKeynotes":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.AutoPlaceKeynotes(uiApp, parameters));

                    case "generateLegendFromTypes":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.GenerateLegendFromTypes(uiApp, parameters));

                    case "exportConstructionData":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.ExportConstructionData(uiApp, parameters));

                    case "optimizeFurnitureLayout":
                        return await ExecuteInRevitContext(uiApp => ValidationMethods.OptimizeFurnitureLayout(uiApp, parameters));case "setroomupperlimit":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.Setroomupperlimit(uiApp, parameters));case "getroomboundingbox":
                        return await ExecuteInRevitContext(uiApp => RoomMethods.Getroomboundingbox(uiApp, parameters));



                    

                    


                    

                    default:
                        // Check registry for dynamically registered methods (BMO, CIPS, etc.)
                        if (_methodRegistry.Count == 0)
                        {
                            InitializeMethodRegistry();
                        }

                        if (_methodRegistry.TryGetValue(method, out var registeredMethod))
                        {
                            Log.Information($"[MCPServer] Executing registered method: {method}");
                            return await ExecuteInRevitContext(uiApp => registeredMethod(uiApp, parameters));
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Unknown method: {method}"
                        });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing message");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
            finally
            {
                // Restore dialog handling state (off by default for manual Revit use)
                RevitMCPBridgeApp.AutoHandleDialogs = previousDialogState;
            }
        }

        /// <summary>
        /// List all available MCP methods for API discovery
        /// </summary>
        private string ListAvailableMethods(JObject parameters)
        {
            var categoryFilter = parameters?["category"]?.ToString();
            var searchFilter = parameters?["search"]?.ToString();
            var includeParams = parameters?["includeParams"]?.ToString()?.ToLower() == "true";

            var methods = new Dictionary<string, List<string>>
            {
                ["Core"] = new List<string> { "ping", "getVersion", "getConfiguration", "reloadConfiguration", "listMethods", "getMethodInfo", "getProjectInfo", "getOpenDocuments", "setActiveDocument", "openProject", "closeProject", "saveProject", "saveProjectAs" },
                ["Dialog"] = new List<string> { "setDialogHandler", "getDialogHistory", "clearDialogHistory" },
                ["Elements"] = new List<string> { "getElements", "getElementProperties", "getElementLocation", "getBoundingBox", "deleteElement", "deleteElements", "copyElements", "moveElements", "rotateElements", "mirrorElements", "arrayElements" },
                ["Families"] = new List<string> { "placeFamilyInstance", "getFamilyInstanceTypes", "loadFamily", "listFamilyFiles", "getLibraryPaths", "getLoadedFamilies", "openLoadAutodeskFamilyDialog", "getImportedInstances", "getImportedGeometry", "getImportedLines", "getFamilyTypes", "getFamilyInfo", "getFamilyInstances", "getFamilyParameters", "isFamilyLoaded", "loadFamiliesFromDirectory", "reloadFamily", "changeFamilyInstanceType", "modifyFamilyInstance", "deleteFamilyInstance", "openFamilyDocument", "closeFamilyDocument", "saveFamilyDocument", "getFamilyLabels", "editFamilyLabel", "addFamilyParameter", "loadFamilyToProject", "editFamilyFromInstance" },
                ["Views"] = new List<string> { "getViews", "createFloorPlan", "createCeilingPlan", "createSection", "createElevation", "create3DView", "duplicateView", "renameView", "deleteView", "setActiveView", "getActiveView", "zoomToFit", "zoomToElement", "zoomToRegion", "zoomToGridIntersection", "showElement", "createLegendView", "getLegendViews", "getElementsInView", "exportViewImage" },
                ["Measurement"] = new List<string> { "measureDistance", "measureBetweenElements", "getRoomDimensions", "measurePerpendicularToWall", "measureCorridorWidth" },
                ["Sheets"] = new List<string> { "getSheets", "getAllSheets", "createSheet", "deleteSheet", "getUnplacedViews", "getEmptySheets", "placeViewOnSheet", "moveViewport", "getViewportsOnSheet", "getTitleblockDimensions" },
                ["Walls"] = new List<string> { "getWalls", "getWallInfo", "getWallsInView", "getWallTypes", "createWall", "createWallByLine", "modifyWall", "deleteWall", "splitWall", "joinWalls", "flipWall" },
                ["Doors/Windows"] = new List<string> { "getDoors", "getWindows", "getDoorTypes", "getWindowTypes", "createDoor", "createWindow", "placeDoor", "placeWindow", "modifyDoor", "modifyWindow", "deleteDoorWindow", "flipDoorWindow", "tagDoorsInView", "tagWindowsInView", "getAllOpenings" },
                ["Rooms"] = new List<string> { "getRooms", "createRoom", "modifyRoom", "deleteRoom", "createRoomBoundary", "getRoomBoundaries", "tagRoomsInView", "getRoomFinishes", "setRoomFinishes", "createRoomSeparationLine" },
                ["Floors"] = new List<string> { "getFloors", "getFloorTypes", "createFloor", "modifyFloor", "deleteFloor" },
                ["Ceilings"] = new List<string> { "getCeilings", "getCeilingTypes", "createCeiling", "deleteCeiling" },
                ["Roofs"] = new List<string> { "getRoofs", "getRoofTypes", "createRoof", "deleteRoof" },
                ["Levels"] = new List<string> { "getLevels", "createLevel", "deleteLevel" },
                ["Grids"] = new List<string> { "getGrids", "createGrid", "createArcGrid", "deleteGrid", "batchDimensionGrids" },
                ["Text/Tags"] = new List<string> { "getTextElements", "placeTextNote", "modifyTextNote", "deleteTextNote", "getTextStatistics", "getTextNoteTypes", "createTag", "getTagsInView", "deleteTag", "getTagInfo", "tagDoor", "tagRoom", "tagWall", "tagElement", "tagAllByCategory", "tagAllRooms", "placeRoomTag", "placeWallTag", "placeDoorTag" },
                ["RichText"] = new List<string> { "createRichTextNote", "updateRichTextNote", "getRichTextNoteData", "getRichTextNotes", "explodeRichTextNote", "getOrCreateColoredTextType", "getColoredTextTypes" },
                ["Dimensions"] = new List<string> { "createLinearDimension", "createAlignedDimension", "createDimensionString", "getDimensionsInView", "deleteDimension", "batchDimensionWalls", "batchDimensionDoors", "addRoomDimensions", "placeAngularDimension", "placeArcLengthDimension", "placeDiameterDimension", "placeRadialDimension" },
                ["DetailLines"] = new List<string> { "createDetailLine", "createModelLine", "getDetailLinesInView", "placeDetailComponent", "placeDetailGroup", "placeRepeatingDetailComponent" },
                ["Schedules"] = new List<string> { "getSchedules", "getAllSchedules", "getScheduleData", "getScheduleInfo", "getScheduleFields", "createSchedule", "createKeySchedule", "duplicateSchedule", "deleteSchedule", "refreshSchedule", "addScheduleField", "removeScheduleField", "reorderScheduleFields", "modifyScheduleField", "addScheduleFilter", "removeScheduleFilter", "getScheduleFilters", "modifyScheduleFilter", "addScheduleSorting", "removeSorting", "getScheduleSortGrouping", "addScheduleGrouping", "formatScheduleAppearance", "modifyScheduleProperties", "getScheduleCellValue", "updateScheduleCell", "getScheduleTotals", "exportScheduleToCSV", "getDoorSchedule", "getWindowSchedule" },
                ["Legends"] = new List<string> { "getLegends", "createLegendView", "getLegendViews", "placeLegendComponent" },
                ["Worksets"] = new List<string> { "getWorksets", "createWorkset", "deleteWorkset", "renameWorkset", "getElementWorkset", "setElementWorkset", "getElementsInWorkset", "getActiveWorkset", "setActiveWorkset", "getWorksetVisibility", "setWorksetVisibility", "getCheckoutStatus" },
                ["Phases"] = new List<string> { "getPhases", "createPhase", "deletePhase", "renamePhase", "getElementPhasing", "setElementPhasing", "getElementsInPhase", "getPhaseFilters", "createPhaseFilter", "modifyPhaseFilter", "deletePhaseFilter" },
                ["Materials"] = new List<string> { "getMaterials", "getMaterialProperties", "getMaterialById", "createMaterial", "deleteMaterial", "setMaterialAppearance", "loadMaterialFromLibrary", "exportMaterial", "replaceMaterial" },
                ["Parameters"] = new List<string> { "getParameters", "setParameter", "getGlobalParameters", "setGlobalParameter", "createGlobalParameter", "deleteGlobalParameter", "createProjectParameter", "deleteProjectParameter", "findElementsByParameterValue", "loadSharedParameterFile", "getSharedParameters", "addSharedParameter" },
                ["Filters"] = new List<string> { "getViewFilters", "createViewFilter", "deleteViewFilter", "duplicateFilter", "applyFilterToView", "removeFilterFromView", "validateFilterRules" },
                ["MEP"] = new List<string> { "getDucts", "getPipes", "createDuct", "createPipe", "getDuctTypes", "getPipeTypes", "createZone", "deleteMEPElement", "getConnectors", "placeElectricalEquipment", "placeElectricalFixture", "placeMechanicalEquipment", "placePlumbingFixture", "calculateLoads" },
                ["Structural"] = new List<string> { "getColumns", "getBeams", "getFoundations", "createColumn", "createBeam", "createFoundation", "getStructuralFraming", "deleteStructuralElement", "placeStructuralColumn", "placeStructuralFraming", "createAreaLoad", "createLineLoad", "createPointLoad", "getElementLoads" },
                ["Annotations"] = new List<string> { "createSpotElevation", "createSpotCoordinate", "createRevisionCloud", "deleteAnnotation", "deleteRevisionCloud", "getAllAnnotationsInView", "placeSpotCoordinate", "placeSpotElevation", "placeSpotSlope", "placeAnnotationSymbol", "placeAreaTag", "placeKeynote", "placeMarkerSymbol", "loadKeynoteFile" },
                ["Detail"] = new List<string> { "createFilledRegion", "createDetailComponent", "createMaskingRegion", "deleteDetailElement", "getDetailComponentInfo", "getDetailComponentFamilies", "loadDetailComponentFamily", "searchLocalDetailFamilies", "activateDetailComponentType", "placeDetailComponentAdvanced", "loadAutodeskFamilyAutomated", "captureDialogState" },
                ["BasePoints"] = new List<string> { "getProjectBasePoint", "setProjectBasePoint", "getSurveyPoint", "setSurveyPoint" },
                ["Batch"] = new List<string> { "placeElementsBatch", "deleteElementsBatch", "setParametersBatch" },
                ["Capture"] = new List<string> { "captureViewport", "getCameraPosition", "setCameraPosition", "setViewStyle", "listViews" },
                ["Render"] = new List<string> { "submitRenderJob", "getRenderResult", "listRenderJobs", "cancelRenderJob" },
                ["Validation"] = new List<string> { "verifyElement", "verifyTextContent", "getValidationSnapshot", "compareViewState", "verifyBatch", "verifyOperation", "verifyElementCount", "getProjectWarnings", "runQCChecks", "validateTextSizes" },
                ["Orchestration"] = new List<string> { "createLifeSafetyLegend", "createAreaCalculationLegend", "executeOrchestrationWorkflow", "reviewViewForIssues", "batchModifyTextNotes", "smartPlaceElement" },
                ["SelfHealing"] = new List<string> { "recordOperation", "getOperationHistory", "clearOperationHistory", "detectAnomalies", "attemptRecovery", "undoLastOperation", "safeDeleteElement", "safeModifyElement", "healthCheck" },
                ["Links"] = new List<string> { "getRevitLinks", "loadRevitLink", "reloadRevitLink", "unloadRevitLink", "getLinkedElements" },
                ["Revisions"] = new List<string> { "getRevisions", "createRevision", "deleteRevision", "addRevisionToSheet" },
                ["Groups"] = new List<string> { "getGroups", "createGroup", "placeGroup", "ungroup", "editGroup" },
                ["Site"] = new List<string> { "getTopography", "createTopography", "modifyTopography", "createBuildingPad" },
                ["SheetPatterns"] = new List<string> { "createSheetsFromPattern", "getSheetPatterns", "createClientProfile", "applyClientProfile" },
                ["PredictiveIntelligence"] = new List<string> { "analyzeProjectState", "getSheetViewMatrix", "getViewPlacementStatus", "getLevelViewCoverage", "detectNamingPatterns", "analyzeProjectGaps", "getAvailableStandards", "compareToStandard", "learnFromProject", "predictNextSteps", "executePrediction", "executePredictions", "autoPlaceSchedule", "autoPlaceView", "autoFixGaps" },
                ["Level3Intelligence"] = new List<string> { "smartExecute", "executeWorkflow", "executeWorkflowStep", "getWorkflowStatus", "getPreExecutionIntelligence", "storeCorrection", "getRelevantCorrections", "getMethodCorrections", "getCorrectionStats", "getCorrectionsAsKnowledge", "markCorrectionApplied", "verifyOperationResult", "analyzeWorkflowRequest", "getWorkflowTemplates", "setProjectContext", "getProjectContext", "exportAllIntelligence", "getSessionSummaryForMemory", "importPreferencesFromMemory" },
                ["Level4Intelligence"] = new List<string> { "runProactiveAnalysis", "getMonitorSuggestions", "executeSuggestion", "takeModelSnapshot", "analyzeUnplacedViews", "analyzeUntaggedRooms", "getAutoCorrections", "dismissSuggestion", "getMonitoringStats" },
                ["Level5Autonomy"] = new List<string> { "executeGoal", "approveTask", "cancelTask", "getAutonomousTasks", "getTaskResult", "configureAutonomy", "getAutonomyStats", "getSupportedGoals" },
                ["Capability"] = new List<string> { "classifyFailure", "proposeToolSpec", "approveToolSpec", "listToolSpecs", "getToolSpec", "getMethodRegistry", "rebuildMethodRegistry", "createTestArtifact", "runTest", "listTests", "getCapabilityStatus" }
            };

            // Apply filters
            var result = new Dictionary<string, object>();
            var totalCount = 0;

            foreach (var category in methods)
            {
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !category.Key.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var filteredMethods = category.Value;
                if (!string.IsNullOrEmpty(searchFilter))
                {
                    filteredMethods = filteredMethods
                        .Where(m => m.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                if (filteredMethods.Count > 0)
                {
                    if (includeParams)
                    {
                        var methodsWithParams = filteredMethods.Select(m => new { name = m, parameters = GetMethodParameters(m) }).ToList();
                        result[category.Key] = new { count = filteredMethods.Count, methods = methodsWithParams };
                    }
                    else
                    {
                        result[category.Key] = new { count = filteredMethods.Count, methods = filteredMethods };
                    }
                    totalCount += filteredMethods.Count;
                }
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                totalMethods = totalCount,
                categories = result.Keys.ToList(),
                categoryFilter = categoryFilter,
                searchFilter = searchFilter,
                includeParams = includeParams,
                methods = result
            });
        }

        /// <summary>
        /// Universal method passthrough - allows calling ANY MCP method by name.
        /// This bridges the gap between the limited tool definitions in AgentFramework
        /// and the full 437+ methods available in the MCP server.
        /// </summary>
        private async Task<string> CallMCPMethodPassthrough(JObject originalRequest, JObject parameters)
        {
            try
            {
                var targetMethod = parameters?["method"]?.ToString();
                if (string.IsNullOrEmpty(targetMethod))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "method parameter is required. Use listAllMethods to see available methods."
                    });
                }

                // Get the nested parameters (or empty object if not provided)
                var targetParams = parameters["parameters"] as JObject ?? new JObject();

                // Create a new request with the target method
                var forwardedRequest = new JObject
                {
                    ["method"] = targetMethod,
                    ["params"] = targetParams
                };

                Log.Information($"[callMCPMethod] Forwarding call to '{targetMethod}' with params: {targetParams}");

                // Call ProcessMessage recursively with the forwarded request
                // We can't call ProcessMessage directly, so we'll construct the message and process it
                var forwardedMessage = forwardedRequest.ToString(Formatting.None);
                return await ProcessMessage(forwardedMessage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in callMCPMethod passthrough");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = $"callMCPMethod failed: {ex.Message}",
                    stackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Get parameter documentation for a specific method
        /// </summary>
        private static object GetMethodParameters(string methodName)
        {
            var paramDocs = new Dictionary<string, object>
            {
                // Core methods
                ["ping"] = new { params_required = new string[0], params_optional = new string[0], description = "Test MCP connection" },
                ["getVersion"] = new { params_required = new string[0], params_optional = new string[0], description = "Get version info, stats, and build metadata" },
                ["listMethods"] = new { params_required = new string[0], params_optional = new[] { "category", "search", "includeParams" }, description = "List all available methods" },
                ["getMethodInfo"] = new { params_required = new[] { "methodName" }, params_optional = new string[0], description = "Get detailed info about a specific method" },
                ["openProject"] = new { params_required = new[] { "filePath" }, params_optional = new string[0], description = "Open a Revit project file" },

                // Wall methods
                ["getWalls"] = new { params_required = new string[0], params_optional = new[] { "viewId", "levelId" }, description = "Get all walls in model or view" },
                ["createWall"] = new { params_required = new[] { "startPoint", "endPoint", "levelId" }, params_optional = new[] { "wallTypeId", "height", "offset" }, description = "Create a wall between two points. Points as {x,y,z} objects" },
                ["createWallByLine"] = new { params_required = new[] { "points", "levelId" }, params_optional = new[] { "wallTypeId", "height" }, description = "Create wall along polyline. Points as [[x,y,z], [x,y,z], ...]" },

                // Door/Window methods
                ["getDoors"] = new { params_required = new string[0], params_optional = new[] { "viewId" }, description = "Get all doors" },
                ["getWindows"] = new { params_required = new string[0], params_optional = new[] { "viewId" }, description = "Get all windows" },
                ["getDoorTypes"] = new { params_required = new string[0], params_optional = new string[0], description = "Get available door family types" },
                ["getWindowTypes"] = new { params_required = new string[0], params_optional = new string[0], description = "Get available window family types" },
                ["placeDoor"] = new { params_required = new[] { "wallId", "typeId", "location" }, params_optional = new[] { "levelId" }, description = "Place door in wall. Location as {x,y,z}" },
                ["placeWindow"] = new { params_required = new[] { "wallId", "typeId", "location" }, params_optional = new[] { "levelId", "sillHeight" }, description = "Place window in wall" },

                // Room methods
                ["getRooms"] = new { params_required = new string[0], params_optional = new[] { "levelId" }, description = "Get all rooms" },
                ["createRoom"] = new { params_required = new[] { "levelId", "location" }, params_optional = new[] { "name", "number" }, description = "Create room at location" },

                // View methods
                ["getViews"] = new { params_required = new string[0], params_optional = new[] { "viewType", "namePattern", "limit", "offset" }, description = "Get views with optional filtering. viewType: FloorPlan/Elevation/Section/ThreeD/etc. namePattern: substring match. limit/offset: pagination." },
                ["createFloorPlan"] = new { params_required = new[] { "levelId" }, params_optional = new[] { "name", "viewFamilyTypeId" }, description = "Create floor plan view" },
                ["createSection"] = new { params_required = new[] { "startPoint", "endPoint" }, params_optional = new[] { "name", "farClipOffset" }, description = "Create section view. Points as {x,y,z} or [x,y,z]" },
                ["createElevation"] = new { params_required = new[] { "location", "direction" }, params_optional = new[] { "name" }, description = "Create elevation view" },
                ["create3DView"] = new { params_required = new string[0], params_optional = new[] { "name" }, description = "Create 3D isometric view" },
                ["exportViewImage"] = new { params_required = new[] { "viewId" }, params_optional = new[] { "filePath", "width", "height", "format" }, description = "Export view as image" },

                // Sheet methods
                ["getSheets"] = new { params_required = new string[0], params_optional = new string[0], description = "Get all sheets" },
                ["getAllSheets"] = new { params_required = new string[0], params_optional = new string[0], description = "Get all sheets with details" },
                ["createSheet"] = new { params_required = new[] { "number", "name" }, params_optional = new string[0], description = "Create new sheet - titleblock is auto-detected from existing sheets (most commonly used one)" },
                ["getPreferredTitleblock"] = new { params_required = new string[0], params_optional = new string[0], description = "Get the titleblock most commonly used in the project - call this BEFORE creating sheets to know which titleblock will be used" },
                ["placeViewOnSheet"] = new { params_required = new[] { "sheetId", "viewId" }, params_optional = new[] { "x", "y" }, description = "Place view on sheet at position" },

                // Schedule methods
                ["getAllSchedules"] = new { params_required = new string[0], params_optional = new string[0], description = "Get all schedules in model" },
                ["getScheduleData"] = new { params_required = new[] { "scheduleId" }, params_optional = new string[0], description = "Get schedule contents" },
                ["createSchedule"] = new { params_required = new[] { "scheduleName", "category" }, params_optional = new string[0], description = "Create new schedule. Category: Rooms, Doors, Windows, Walls, etc." },
                ["addScheduleField"] = new { params_required = new[] { "scheduleId", "fieldName" }, params_optional = new[] { "heading" }, description = "Add field to schedule" },
                ["addScheduleFilter"] = new { params_required = new[] { "scheduleId", "fieldName", "filterType", "filterValue" }, params_optional = new string[0], description = "Add filter to schedule" },
                ["exportScheduleToCSV"] = new { params_required = new[] { "scheduleId", "filePath" }, params_optional = new string[0], description = "Export schedule to CSV file" },

                // Tag methods
                ["tagAllByCategory"] = new { params_required = new[] { "viewId", "category" }, params_optional = new string[0], description = "Tag all elements of category in view. Category: Rooms, Doors, Windows, Walls" },
                ["tagAllRooms"] = new { params_required = new[] { "viewId" }, params_optional = new string[0], description = "Tag all rooms in view" },
                ["tagDoor"] = new { params_required = new[] { "doorId", "viewId" }, params_optional = new[] { "tagTypeId", "location" }, description = "Tag a specific door" },
                ["tagRoom"] = new { params_required = new[] { "roomId", "viewId" }, params_optional = new[] { "tagTypeId", "location" }, description = "Tag a specific room" },

                // Dimension methods
                ["batchDimensionWalls"] = new { params_required = new[] { "viewId" }, params_optional = new[] { "wallType", "offset", "direction" }, description = "Add dimensions to all walls in view" },
                ["batchDimensionDoors"] = new { params_required = new[] { "viewId" }, params_optional = new string[0], description = "Add dimensions to all doors in view" },
                ["addRoomDimensions"] = new { params_required = new[] { "roomId", "viewId" }, params_optional = new string[0], description = "Add dimensions to specific room" },

                // Family methods
                ["getLoadedFamilies"] = new { params_required = new string[0], params_optional = new[] { "category" }, description = "Get all loaded families" },
                ["placeFamilyInstance"] = new { params_required = new[] { "familyTypeId", "location" }, params_optional = new[] { "levelId", "rotation" }, description = "Place family instance at location" },
                ["loadFamily"] = new { params_required = new[] { "filePath" }, params_optional = new string[0], description = "Load family from .rfa file" },

                // Element methods
                ["deleteElements"] = new { params_required = new[] { "elementIds" }, params_optional = new string[0], description = "Delete multiple elements by ID array" },
                ["getElementProperties"] = new { params_required = new[] { "elementId" }, params_optional = new string[0], description = "Get all properties of element" },

                // Validation methods
                ["getProjectWarnings"] = new { params_required = new string[0], params_optional = new[] { "limit" }, description = "Get model warnings" },
                ["healthCheck"] = new { params_required = new string[0], params_optional = new string[0], description = "Check MCP server health status" },

                // Level/Grid methods
                ["getLevels"] = new { params_required = new string[0], params_optional = new string[0], description = "Get all levels" },
                ["getGrids"] = new { params_required = new string[0], params_optional = new string[0], description = "Get all grids" }
            };

            return paramDocs.TryGetValue(methodName, out var doc)
                ? doc
                : new { params_required = new[] { "see source code" }, params_optional = new string[0], description = "Documentation pending" };
        }

        /// <summary>
        /// Get detailed information about a specific method
        /// </summary>
        private string GetMethodInfo(JObject parameters)
        {
            var methodName = parameters?["methodName"]?.ToString();
            if (string.IsNullOrEmpty(methodName))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "methodName is required" });
            }

            var paramInfo = GetMethodParameters(methodName);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                method = methodName,
                documentation = paramInfo
            });
        }

        private Task<string> GetProjectInfo()
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }

                    var doc = uiApp.ActiveUIDocument.Document;
                    var projectInfo = doc.ProjectInformation;

                    var info = new
                    {
                        success = true,
                        result = new
                        {
                            name = projectInfo.Name,
                            number = projectInfo.Number,
                            author = projectInfo.Author,
                            status = projectInfo.Status,
                            address = projectInfo.Address,
                            clientName = projectInfo.ClientName,
                            buildingName = projectInfo.BuildingName,
                            organizationName = projectInfo.OrganizationName,
                            organizationDescription = projectInfo.OrganizationDescription,
                            isPinned = projectInfo.Pinned,
                            isWorkshared = doc.IsWorkshared,
                            title = doc.Title,
                            pathname = doc.PathName
                        }
                    };

                    return JsonConvert.SerializeObject(info);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in GetProjectInfo");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        private Task<string> GetOpenDocuments()
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var app = uiApp.Application;
                    var documentList = new System.Collections.Generic.List<object>();
                    var activeDocTitle = uiApp.ActiveUIDocument?.Document?.Title ?? "";

                    foreach (Document doc in app.Documents)
                    {
                        documentList.Add(new
                        {
                            title = doc.Title,
                            pathName = doc.PathName,
                            isWorkshared = doc.IsWorkshared,
                            isActive = doc.Title == activeDocTitle
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            count = documentList.Count,
                            activeDocument = activeDocTitle,
                            documents = documentList
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in GetOpenDocuments");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        private Task<string> SetActiveDocument(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var documentName = parameters?["documentName"]?.ToString();
                    var documentPath = parameters?["documentPath"]?.ToString();

                    if (string.IsNullOrEmpty(documentName) && string.IsNullOrEmpty(documentPath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Either documentName or documentPath is required"
                        });
                    }

                    var app = uiApp.Application;
                    Document targetDoc = null;

                    // Find the document by name or path
                    foreach (Document doc in app.Documents)
                    {
                        if (!string.IsNullOrEmpty(documentPath) && doc.PathName == documentPath)
                        {
                            targetDoc = doc;
                            break;
                        }
                        if (!string.IsNullOrEmpty(documentName) && doc.Title == documentName)
                        {
                            targetDoc = doc;
                            break;
                        }
                        // Also check if title contains the name (for unsaved files like "Project1")
                        if (!string.IsNullOrEmpty(documentName) && doc.Title.Contains(documentName))
                        {
                            targetDoc = doc;
                            break;
                        }
                    }

                    if (targetDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Document not found: {documentName ?? documentPath}"
                        });
                    }

                    // Activate the document
                    // For already-open documents, we use OpenAndActivateDocument with the path
                    if (!string.IsNullOrEmpty(targetDoc.PathName))
                    {
                        uiApp.OpenAndActivateDocument(targetDoc.PathName);
                    }
                    else
                    {
                        // For unsaved documents, Revit API doesn't provide a way to activate them
                        // The user needs to save the file first, or we can try to use RequestViewChange
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Cannot switch to unsaved document '{targetDoc.Title}'. Please save the file first, then try again."
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            activatedDocument = targetDoc.Title,
                            path = targetDoc.PathName,
                            message = $"Switched to document: {targetDoc.Title}"
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in SetActiveDocument");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        private Task<string> OpenProject(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var filePath = parameters?["filePath"]?.ToString();

                    if (string.IsNullOrEmpty(filePath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "filePath is required"
                        });
                    }

                    if (!System.IO.File.Exists(filePath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"File not found: {filePath}"
                        });
                    }

                    // Check if already open
                    foreach (Document existingDoc in uiApp.Application.Documents)
                    {
                        if (existingDoc.PathName.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                        {
                            // Already open, just activate it
                            uiApp.OpenAndActivateDocument(filePath);
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                result = new
                                {
                                    documentTitle = existingDoc.Title,
                                    path = existingDoc.PathName,
                                    alreadyOpen = true,
                                    message = $"Document was already open and is now active: {existingDoc.Title}"
                                }
                            });
                        }
                    }

                    // Open AND ACTIVATE the document (critical for API to work with new doc)
                    var uiDoc = uiApp.OpenAndActivateDocument(filePath);

                    if (uiDoc == null || uiDoc.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to open document"
                        });
                    }

                    var doc = uiDoc.Document;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            documentTitle = doc.Title,
                            path = doc.PathName,
                            isFamilyDocument = doc.IsFamilyDocument,
                            isWorkshared = doc.IsWorkshared,
                            alreadyOpen = false,
                            message = $"Opened and activated document: {doc.Title}"
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in OpenProject");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        stackTrace = ex.StackTrace
                    });
                }
            });
        }

        private Task<string> CloseProject(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var save = parameters?["save"]?.Value<bool>() ?? false;
                    var documentPath = parameters?["documentPath"]?.ToString();
                    var documentName = parameters?["documentName"]?.ToString();
                    var closeAll = parameters?["closeAll"]?.Value<bool>() ?? false;

                    if (closeAll)
                    {
                        // Close all documents
                        var closedDocs = new System.Collections.Generic.List<string>();
                        var docs = uiApp.Application.Documents.Cast<Document>().ToList();

                        foreach (var doc in docs)
                        {
                            if (!doc.IsFamilyDocument) // Skip family documents
                            {
                                string title = doc.Title;
                                doc.Close(save);
                                closedDocs.Add(title);
                            }
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            result = new
                            {
                                closedCount = closedDocs.Count,
                                closedDocuments = closedDocs,
                                saved = save,
                                message = $"Closed {closedDocs.Count} documents"
                            }
                        });
                    }

                    // Close specific document or active document
                    Document targetDoc = null;

                    if (!string.IsNullOrEmpty(documentPath))
                    {
                        foreach (Document doc in uiApp.Application.Documents)
                        {
                            if (doc.PathName.Equals(documentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                targetDoc = doc;
                                break;
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(documentName))
                    {
                        foreach (Document doc in uiApp.Application.Documents)
                        {
                            if (doc.Title.Contains(documentName))
                            {
                                targetDoc = doc;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Close active document
                        targetDoc = uiApp.ActiveUIDocument?.Document;
                    }

                    if (targetDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No document found to close"
                        });
                    }

                    string docTitle = targetDoc.Title;
                    string docPath = targetDoc.PathName;

                    targetDoc.Close(save);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            closedDocument = docTitle,
                            path = docPath,
                            saved = save,
                            message = save ? $"Closed and saved: {docTitle}" : $"Closed without saving: {docTitle}"
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in CloseProject");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        stackTrace = ex.StackTrace
                    });
                }
            });
        }

        private Task<string> SaveProject(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var documentPath = parameters?["documentPath"]?.ToString();
                    var documentName = parameters?["documentName"]?.ToString();

                    // Find target document
                    Document targetDoc = null;

                    if (!string.IsNullOrEmpty(documentPath))
                    {
                        foreach (Document doc in uiApp.Application.Documents)
                        {
                            if (doc.PathName.Equals(documentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                targetDoc = doc;
                                break;
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(documentName))
                    {
                        foreach (Document doc in uiApp.Application.Documents)
                        {
                            if (doc.Title.Contains(documentName))
                            {
                                targetDoc = doc;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Save active document
                        targetDoc = uiApp.ActiveUIDocument?.Document;
                    }

                    if (targetDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No document found to save"
                        });
                    }

                    if (string.IsNullOrEmpty(targetDoc.PathName))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Document has never been saved. Use saveProjectAs instead."
                        });
                    }

                    string docTitle = targetDoc.Title;
                    string docPath = targetDoc.PathName;

                    targetDoc.Save();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            savedDocument = docTitle,
                            path = docPath,
                            message = $"Saved: {docTitle}"
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in SaveProject");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        stackTrace = ex.StackTrace
                    });
                }
            });
        }

        private Task<string> SaveProjectAs(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var newFilePath = parameters?["filePath"]?.ToString();
                    var documentName = parameters?["documentName"]?.ToString();
                    var overwrite = parameters?["overwrite"]?.Value<bool>() ?? false;

                    if (string.IsNullOrEmpty(newFilePath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "filePath is required for saveProjectAs"
                        });
                    }

                    // Find source document
                    Document sourceDoc = null;

                    if (!string.IsNullOrEmpty(documentName))
                    {
                        foreach (Document doc in uiApp.Application.Documents)
                        {
                            if (doc.Title.Contains(documentName))
                            {
                                sourceDoc = doc;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Use active document
                        sourceDoc = uiApp.ActiveUIDocument?.Document;
                    }

                    if (sourceDoc == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No document found to save"
                        });
                    }

                    string originalTitle = sourceDoc.Title;
                    string originalPath = sourceDoc.PathName;

                    // Configure SaveAs options
                    var saveAsOptions = new SaveAsOptions();
                    saveAsOptions.OverwriteExistingFile = overwrite;
                    saveAsOptions.MaximumBackups = 3;

                    // Perform SaveAs
                    sourceDoc.SaveAs(newFilePath, saveAsOptions);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            originalDocument = originalTitle,
                            originalPath = originalPath,
                            newPath = newFilePath,
                            overwritten = overwrite,
                            message = $"Saved '{originalTitle}' as '{newFilePath}'"
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in SaveProjectAs");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        stackTrace = ex.StackTrace
                    });
                }
            });
        }

        // Dialog handling methods
        private string SetDialogHandler(JObject parameters)
        {
            try
            {
                // Get parameters
                var autoHandle = parameters?["autoHandle"]?.Value<bool>() ?? true;
                var defaultResult = parameters?["defaultResult"]?.Value<int>() ?? 1;

                // Validate defaultResult (0 = Cancel, 1 = OK/Yes, 2 = No)
                if (defaultResult < 0 || defaultResult > 2)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "defaultResult must be 0 (Cancel), 1 (OK/Yes), or 2 (No)"
                    });
                }

                // Set the values
                RevitMCPBridgeApp.AutoHandleDialogs = autoHandle;
                RevitMCPBridgeApp.DefaultDialogResult = defaultResult;

                var resultName = defaultResult == 0 ? "Cancel" :
                                (defaultResult == 1 ? "OK/Yes" : "No");

                Log.Information($"Dialog handler set: autoHandle={autoHandle}, defaultResult={resultName}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        autoHandle = autoHandle,
                        defaultResult = defaultResult,
                        defaultResultName = resultName,
                        message = $"Dialog handler configured. Auto-handle: {autoHandle}, Default button: {resultName}"
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting dialog handler");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private string GetDialogHistory()
        {
            try
            {
                var history = RevitMCPBridgeApp.GetDialogHistory();

                var dialogs = history.Select(d => new
                {
                    timestamp = d.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    dialogId = d.DialogId,
                    message = d.Message,
                    resultClicked = d.ResultClicked,
                    resultName = d.ResultName
                }).ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        count = dialogs.Count,
                        autoHandleEnabled = RevitMCPBridgeApp.AutoHandleDialogs,
                        defaultResult = RevitMCPBridgeApp.DefaultDialogResult,
                        dialogs = dialogs
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting dialog history");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private string ClearDialogHistory()
        {
            try
            {
                RevitMCPBridgeApp.ClearDialogHistory();

                Log.Information("Dialog history cleared");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    result = new
                    {
                        message = "Dialog history cleared"
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error clearing dialog history");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private Task<string> GetElements(JObject parameters)
        {
            return Task.Run(() =>
            {
                try
                {
                    var uiApp = RevitMCPBridgeApp.GetUIApplication();
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }

                    var doc = uiApp.ActiveUIDocument.Document;
                    var categoryName = parameters?["category"]?.ToString();
                    var viewId = parameters?["viewId"]?.ToString();
                    
                    FilteredElementCollector collector;
                    
                    if (!string.IsNullOrEmpty(viewId) && int.TryParse(viewId, out int id))
                    {
                        var view = doc.GetElement(new ElementId(id)) as View;
                        if (view != null)
                            collector = new FilteredElementCollector(doc, view.Id);
                        else
                            collector = new FilteredElementCollector(doc);
                    }
                    else
                    {
                        collector = new FilteredElementCollector(doc);
                    }
                    
                    // Apply category filter if specified
                    if (!string.IsNullOrEmpty(categoryName))
                    {
                        Category category = null;

                        // First try direct category name lookup
                        try
                        {
                            category = doc.Settings.Categories.get_Item(categoryName);
                        }
                        catch { }

                        // If not found, try matching BuiltInCategory enum
                        if (category == null)
                        {
                            // Try with OST_ prefix
                            var enumName = categoryName.StartsWith("OST_") ? categoryName : "OST_" + categoryName;
                            if (Enum.TryParse<BuiltInCategory>(enumName, true, out var builtInCat))
                            {
                                category = doc.Settings.Categories.get_Item(builtInCat);
                            }
                            // Also try common variations
                            else if (Enum.TryParse<BuiltInCategory>("OST_" + categoryName.Replace(" ", ""), true, out builtInCat))
                            {
                                category = doc.Settings.Categories.get_Item(builtInCat);
                            }
                        }

                        if (category != null)
                        {
                            collector.OfCategoryId(category.Id);
                        }
                        else
                        {
                            // Category not found - return error instead of all elements
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Category '{categoryName}' not found. Use exact Revit category names (e.g., 'Walls', 'Doors', 'Rooms') or BuiltInCategory names (e.g., 'OST_Walls')."
                            });
                        }
                    }
                    
                    // Get elements
                    var elements = collector.WhereElementIsNotElementType().ToElements();
                    var elementList = new System.Collections.Generic.List<object>();
                    
                    foreach (var element in elements)
                    {
                        if (element.Category == null) continue;
                        
                        elementList.Add(new
                        {
                            id = element.Id.Value,
                            name = element.Name,
                            category = element.Category.Name,
                            typename = element.GetType().Name,
                            level = GetElementLevel(element)
                        });
                        
                        // Limit results for performance
                        if (elementList.Count >= 1000)
                            break;
                    }
                    
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            count = elementList.Count,
                            elements = elementList
                        }
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }
        
        private Task<string> GetElementProperties(JObject parameters)
        {
            return Task.Run(() =>
            {
                try
                {
                    var elementId = parameters?["elementId"]?.ToString();
                    if (string.IsNullOrEmpty(elementId))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId parameter is required"
                        });
                    }
                    
                    var uiApp = RevitMCPBridgeApp.GetUIApplication();
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }
                    
                    var doc = uiApp.ActiveUIDocument.Document;
                    var element = doc.GetElement(new ElementId(int.Parse(elementId)));
                    
                    if (element == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Element with ID {elementId} not found"
                        });
                    }
                    
                    var properties = new System.Collections.Generic.Dictionary<string, object>();
                    
                    // Basic properties
                    properties["Id"] = element.Id.Value;
                    properties["Name"] = element.Name;
                    properties["Category"] = element.Category?.Name;
                    properties["TypeName"] = element.GetType().Name;
                    
                    // Parameters
                    var parameters_dict = new System.Collections.Generic.Dictionary<string, object>();
                    foreach (Parameter param in element.Parameters)
                    {
                        if (param.Definition == null) continue;
                        
                        var paramName = param.Definition.Name;
                        var paramValue = GetParameterValue(param);
                        
                        parameters_dict[paramName] = new
                        {
                            value = paramValue,
                            storageType = param.StorageType.ToString(),
                            isReadOnly = param.IsReadOnly,
                            hasValue = param.HasValue
                        };
                    }
                    
                    properties["Parameters"] = parameters_dict;
                    
                    // Geometry info
                    var bbox = element.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        properties["BoundingBox"] = new
                        {
                            min = new { x = bbox.Min.X, y = bbox.Min.Y, z = bbox.Min.Z },
                            max = new { x = bbox.Max.X, y = bbox.Max.Y, z = bbox.Max.Z }
                        };
                    }
                    
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = properties
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }
        
        private object GetParameterValue(Parameter param)
        {
            if (!param.HasValue)
                return null;
                
            switch (param.StorageType)
            {
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.String:
                    return param.AsString();
                case StorageType.ElementId:
                    return param.AsElementId()?.Value;
                default:
                    return param.AsValueString();
            }
        }
        
        private Task<string> ExecuteCommand(JObject parameters)
        {
            return Task.Run(() =>
            {
                try
                {
                    var command = parameters?["command"]?.ToString();
                    var args = parameters?["args"] as JObject;
                    
                    if (string.IsNullOrEmpty(command))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "command parameter is required"
                        });
                    }
                    
                    // Here we would execute various Revit commands
                    // This is a placeholder for command execution logic
                    
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = $"Command '{command}' executed successfully"
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }
        
        private Task<string> GetViews(JObject parameters)
        {
            return Task.Run(() =>
            {
                try
                {
                    var uiApp = RevitMCPBridgeApp.GetUIApplication();
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }

                    var doc = uiApp.ActiveUIDocument.Document;

                    // Parse filter parameters
                    var viewTypeFilter = parameters?["viewType"]?.ToString();
                    var namePattern = parameters?["namePattern"]?.ToString();
                    var limit = parameters?["limit"]?.ToObject<int?>() ?? 0;
                    var offset = parameters?["offset"]?.ToObject<int?>() ?? 0;

                    // Start with all non-template views
                    IEnumerable<View> collector = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate);

                    // Apply viewType filter if specified
                    if (!string.IsNullOrEmpty(viewTypeFilter))
                    {
                        if (Enum.TryParse<ViewType>(viewTypeFilter, true, out var vt))
                        {
                            collector = collector.Where(v => v.ViewType == vt);
                        }
                    }

                    // Apply name pattern filter (case-insensitive contains)
                    if (!string.IsNullOrEmpty(namePattern))
                    {
                        collector = collector.Where(v =>
                            v.Name != null &&
                            v.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    // Get total count before pagination (for reporting)
                    var allMatching = collector.ToList();
                    int totalMatching = allMatching.Count;

                    // Apply pagination
                    IEnumerable<View> paginated = allMatching;
                    if (offset > 0)
                    {
                        paginated = paginated.Skip(offset);
                    }
                    if (limit > 0)
                    {
                        paginated = paginated.Take(limit);
                    }

                    var views = new System.Collections.Generic.List<object>();

                    foreach (var view in paginated)
                    {
                        int scale = 0;
                        try { scale = view.Scale; } catch { }

                        views.Add(new
                        {
                            id = view.Id.Value,
                            name = view.Name,
                            viewType = view.ViewType.ToString(),
                            level = (view as ViewPlan)?.GenLevel?.Name,
                            scale = scale,
                            isActive = view.Id == doc.ActiveView?.Id
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            count = views.Count,
                            totalMatching = totalMatching,
                            offset = offset,
                            limit = limit > 0 ? limit : totalMatching,
                            activeView = doc.ActiveView?.Name,
                            views = views
                        }
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        private Task<string> ExportViewImage(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }

                    var doc = uiApp.ActiveUIDocument.Document;
                    var viewIdStr = parameters?["viewId"]?.ToString();
                    var resolution = parameters?["resolution"]?.ToObject<int>() ?? 96;
                    var outputPath = parameters?["outputPath"]?.ToString();

                    // Get the view
                    View view;
                    if (!string.IsNullOrEmpty(viewIdStr) && int.TryParse(viewIdStr, out int viewId))
                    {
                        view = doc.GetElement(new ElementId(viewId)) as View;
                    }
                    else
                    {
                        view = doc.ActiveView;
                    }

                    if (view == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "View not found"
                        });
                    }

                    // Generate output path if not provided
                    if (string.IsNullOrEmpty(outputPath))
                    {
                        var tempPath = Path.GetTempPath();
                        var fileName = $"revit_view_{view.Id.Value}_{DateTime.Now:yyyyMMddHHmmss}.png";
                        outputPath = Path.Combine(tempPath, fileName);
                    }

                    // Create image export options
                    var imageOptions = new ImageExportOptions
                    {
                        ZoomType = ZoomFitType.FitToPage,
                        PixelSize = 1920,  // Width in pixels
                        FilePath = outputPath,
                        FitDirection = FitDirectionType.Horizontal,
                        HLRandWFViewsFileType = ImageFileType.PNG,
                        ShadowViewsFileType = ImageFileType.PNG,
                        ImageResolution = resolution >= 150 ? ImageResolution.DPI_150 : ImageResolution.DPI_72,
                        ExportRange = ExportRange.SetOfViews
                    };

                    // Set the view to export
                    imageOptions.SetViewsAndSheets(new List<ElementId> { view.Id });

                    // Export the image
                    doc.ExportImage(imageOptions);

                    // The actual file path may have a suffix added by Revit
                    var actualPath = outputPath;
                    var directory = Path.GetDirectoryName(outputPath);
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(outputPath);
                    var possibleFiles = Directory.GetFiles(directory, fileNameWithoutExt + "*.png");

                    if (possibleFiles.Length > 0)
                    {
                        actualPath = possibleFiles[0];
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            viewId = view.Id.Value,
                            viewName = view.Name,
                            outputPath = actualPath,
                            fileExists = File.Exists(actualPath),
                            resolution = resolution
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error exporting view image");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        private Task<string> GetCategories()
        {
            return Task.Run(() =>
            {
                try
                {
                    var uiApp = RevitMCPBridgeApp.GetUIApplication();
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }
                    
                    var doc = uiApp.ActiveUIDocument.Document;
                    var categories = new System.Collections.Generic.List<object>();
                    
                    foreach (Category category in doc.Settings.Categories)
                    {
                        if (category.CategoryType == CategoryType.Model)
                        {
                            categories.Add(new
                            {
                                id = category.Id.Value,
                                name = category.Name,
                                parent = category.Parent?.Name,
                                material = category.Material?.Name,
                                lineColor = category.LineColor?.ToString()
                            });
                        }
                    }
                    
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            count = categories.Count,
                            categories = categories
                        }
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }
        
        private Task<string> GetParameters(JObject parameters)
        {
            return Task.Run(() =>
            {
                try
                {
                    var elementId = parameters?["elementId"]?.ToString();
                    if (string.IsNullOrEmpty(elementId))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId parameter is required"
                        });
                    }
                    
                    var uiApp = RevitMCPBridgeApp.GetUIApplication();
                    if (uiApp?.ActiveUIDocument?.Document == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No active document"
                        });
                    }
                    
                    var doc = uiApp.ActiveUIDocument.Document;
                    var element = doc.GetElement(new ElementId(int.Parse(elementId)));
                    
                    if (element == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Element with ID {elementId} not found"
                        });
                    }
                    
                    var parameterList = new System.Collections.Generic.List<object>();
                    
                    foreach (Parameter param in element.Parameters)
                    {
                        if (param.Definition == null) continue;

                        parameterList.Add(new
                        {
                            name = param.Definition.Name,
                            value = GetParameterValue(param),
                            storageType = param.StorageType.ToString(),
                            isReadOnly = param.IsReadOnly,
                            hasValue = param.HasValue,
                            id = param.Id.Value,
                            isShared = param.IsShared,
                            guid = param.IsShared ? param.GUID.ToString() : null
                        });
                    }
                    
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            elementId = element.Id.Value,
                            elementName = element.Name,
                            parameterCount = parameterList.Count,
                            parameters = parameterList
                        }
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }
        
        private Task<string> SetParameter(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var elementId = parameters?["elementId"]?.ToString();
                    var parameterName = parameters?["parameterName"]?.ToString();
                    var value = parameters?["value"];

                    if (string.IsNullOrEmpty(elementId) || string.IsNullOrEmpty(parameterName))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId and parameterName parameters are required"
                        });
                    }

                    var doc = uiApp.ActiveUIDocument.Document;
                    var element = doc.GetElement(new ElementId(int.Parse(elementId)));

                    if (element == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Element with ID {elementId} not found"
                        });
                    }

                    var param = element.LookupParameter(parameterName);
                    if (param == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Parameter '{parameterName}' not found"
                        });
                    }

                    if (param.IsReadOnly)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Parameter '{parameterName}' is read-only"
                        });
                    }

                    // Set parameter value within a transaction
                    using (var trans = new Transaction(doc, "MCP Set Parameter"))
                    {
                        trans.Start();

                        try
                        {
                            switch (param.StorageType)
                            {
                                case StorageType.Double:
                                    param.Set(Convert.ToDouble(value));
                                    break;
                                case StorageType.Integer:
                                    param.Set(Convert.ToInt32(value));
                                    break;
                                case StorageType.String:
                                    param.Set(value.ToString());
                                    break;
                                case StorageType.ElementId:
                                    param.Set(new ElementId(Convert.ToInt32(value)));
                                    break;
                            }

                            trans.Commit();

                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                result = new
                                {
                                    elementId = element.Id.Value,
                                    parameterName = parameterName,
                                    newValue = GetParameterValue(param)
                                }
                            });
                        }
                        catch
                        {
                            trans.RollBack();
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        /// <summary>
        /// Gets a parameter value from an element
        /// </summary>
        private Task<string> GetParameter(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var elementId = parameters?["elementId"]?.ToString();
                    var parameterName = parameters?["parameterName"]?.ToString();

                    if (string.IsNullOrEmpty(elementId) || string.IsNullOrEmpty(parameterName))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId and parameterName parameters are required"
                        });
                    }

                    var doc = uiApp.ActiveUIDocument.Document;
                    var element = doc.GetElement(new ElementId(int.Parse(elementId)));

                    if (element == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Element with ID {elementId} not found"
                        });
                    }

                    var param = element.LookupParameter(parameterName);
                    if (param == null)
                    {
                        // Try built-in parameter by name
                        foreach (Parameter p in element.Parameters)
                        {
                            if (p.Definition.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                            {
                                param = p;
                                break;
                            }
                        }
                    }

                    if (param == null)
                    {
                        // Return list of available parameters
                        var availableParams = new List<string>();
                        foreach (Parameter p in element.Parameters)
                        {
                            availableParams.Add(p.Definition.Name);
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Parameter '{parameterName}' not found on element",
                            availableParameters = availableParams.OrderBy(p => p).Take(50).ToList()
                        });
                    }

                    var value = GetParameterValue(param);
                    var valueString = param.AsValueString();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            elementId = element.Id.Value,
                            elementCategory = element.Category?.Name,
                            parameterName = param.Definition.Name,
                            value = value,
                            valueAsString = valueString,
                            storageType = param.StorageType.ToString(),
                            isReadOnly = param.IsReadOnly,
                            hasValue = param.HasValue
                        }
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        private string GetElementLevel(Element element)
        {
            try
            {
                // Try to get level from element
                if (element is FamilyInstance fi)
                {
                    // Try to get level from Host
                    if (fi.Host != null && fi.Host is Level level)
                        return level.Name;

                    // Try to get level from parameter
                    var levelParam = fi.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                    {
                        var levelId = levelParam.AsElementId();
                        if (levelId != ElementId.InvalidElementId)
                        {
                            var levelElement = element.Document.GetElement(levelId) as Level;
                            if (levelElement != null)
                                return levelElement.Name;
                        }
                    }
                }

                // Try to get level for walls, floors, etc.
                if (element.LevelId != ElementId.InvalidElementId)
                {
                    var level = element.Document.GetElement(element.LevelId) as Level;
                    if (level != null)
                        return level.Name;
                }

                return "N/A";
            }
            catch
            {
                return "N/A";
            }
        }

        /// <summary>
        /// Get text elements from the document
        /// </summary>
        private Task<string> GetTextElements(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var viewIdStr = parameters?["viewId"]?.ToString();
                    var searchText = parameters?["searchText"]?.ToString();
                    var includeTextNotes = parameters?["includeTextNotes"]?.ToObject<bool?>() ?? true;

                    // Build filter based on parameters
                    FilteredElementCollector collector;

                    if (!string.IsNullOrEmpty(viewIdStr))
                    {
                        var viewId = new ElementId(int.Parse(viewIdStr));
                        collector = new FilteredElementCollector(doc, viewId);
                    }
                    else
                    {
                        collector = new FilteredElementCollector(doc);
                    }

                    var textElements = new List<object>();

                    if (includeTextNotes)
                    {
                        var textNotes = collector
                            .OfClass(typeof(TextNote))
                            .Cast<TextNote>();

                        foreach (var textNote in textNotes)
                        {
                            try
                            {
                                var text = textNote.Text;

                                // Apply search filter if specified
                                if (!string.IsNullOrEmpty(searchText) && !text.Contains(searchText))
                                    continue;

                                var ownerViewId = textNote.OwnerViewId;
                                var ownerView = doc.GetElement(ownerViewId) as View;
                                var position = textNote.Coord;

                                textElements.Add(new
                                {
                                    id = textNote.Id.Value,
                                    type = "TextNote",
                                    text = text ?? "",
                                    viewId = ownerViewId.Value,
                                    viewName = ownerView?.Name ?? "Unknown",
                                    position = new
                                    {
                                        x = position.X,
                                        y = position.Y,
                                        z = position.Z
                                    },
                                    width = textNote.Width
                                });
                            }
                            catch (Exception ex)
                            {
                                // Skip text notes that cause errors
                                Log.Warning(ex, $"Error processing text note {textNote.Id}");
                                continue;
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        count = textElements.Count,
                        texts = textElements  // Changed from result.textElements to texts
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting text elements");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        /// <summary>
        /// Create a new TextNote in a view
        /// </summary>
        private Task<string> CreateTextNote(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var viewIdStr = parameters?["viewId"]?.ToString();
                    var text = parameters?["text"]?.ToString();
                    var x = parameters?["x"]?.ToObject<double?>() ?? 0;
                    var y = parameters?["y"]?.ToObject<double?>() ?? 0;
                    var z = parameters?["z"]?.ToObject<double?>() ?? 0;

                    if (string.IsNullOrEmpty(viewIdStr) || string.IsNullOrEmpty(text))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "viewId and text parameters are required"
                        });
                    }

                    var viewId = new ElementId(int.Parse(viewIdStr));
                    var view = doc.GetElement(viewId) as View;

                    if (view == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"View with ID {viewIdStr} not found or is not a valid view"
                        });
                    }

                    using (var trans = new Transaction(doc, "MCP Create Text Note"))
                    {
                        trans.Start();

                        try
                        {
                            // Get TextNoteType - check for explicit parameter first
                            Element textNoteType = null;
                            var textTypeIdStr = parameters?["textTypeId"]?.ToString();

                            if (!string.IsNullOrEmpty(textTypeIdStr))
                            {
                                // User specified explicit type ID
                                var typeId = new ElementId(int.Parse(textTypeIdStr));
                                textNoteType = doc.GetElement(typeId);
                            }
                            else
                            {
                                // Find the most commonly used text type in the project
                                // This automatically picks up the project's standard (e.g., 3/32" Century Gothic)
                                var allTextTypes = new FilteredElementCollector(doc)
                                    .OfClass(typeof(TextNoteType))
                                    .Cast<TextNoteType>()
                                    .ToList();

                                var allTextNotes = new FilteredElementCollector(doc)
                                    .OfClass(typeof(TextNote))
                                    .Cast<TextNote>()
                                    .ToList();

                                if (allTextNotes.Count > 0)
                                {
                                    // Count usage of each type
                                    var typeUsage = allTextNotes
                                        .GroupBy(tn => tn.GetTypeId().Value)
                                        .OrderByDescending(g => g.Count())
                                        .Select(g => g.Key)
                                        .FirstOrDefault();

                                    if (typeUsage != 0)
                                    {
                                        textNoteType = doc.GetElement(new ElementId(typeUsage));
                                    }
                                }

                                // Fallback: look for types with "3/32" in the name (common standard size)
                                if (textNoteType == null)
                                {
                                    textNoteType = allTextTypes
                                        .FirstOrDefault(t => t.Name.Contains("3/32"));
                                }

                                // Final fallback: first available type
                                if (textNoteType == null)
                                {
                                    textNoteType = allTextTypes.FirstOrDefault();
                                }
                            }

                            if (textNoteType == null)
                            {
                                trans.RollBack();
                                return JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = "No TextNoteType found in document"
                                });
                            }

                            var position = new XYZ(x, y, z);
                            var textNote = TextNote.Create(doc, viewId, position, text, textNoteType.Id);

                            trans.Commit();

                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                result = new
                                {
                                    id = textNote.Id.Value,
                                    text = textNote.Text,
                                    viewId = viewId.Value,
                                    viewName = view.Name,
                                    position = new { x = position.X, y = position.Y, z = position.Z },
                                    textTypeId = textNoteType.Id.Value,
                                    textTypeName = textNoteType.Name
                                }
                            });
                        }
                        catch (Exception)
                        {
                            trans.RollBack();
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error creating text note");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        /// <summary>
        /// Modify an existing TextNote's content
        /// </summary>
        private Task<string> ModifyTextNote(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var elementIdStr = parameters?["elementId"]?.ToString();
                    var newText = parameters?["text"]?.ToString();

                    if (string.IsNullOrEmpty(elementIdStr) || string.IsNullOrEmpty(newText))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId and text parameters are required"
                        });
                    }

                    var elementId = new ElementId(int.Parse(elementIdStr));
                    var textNote = doc.GetElement(elementId) as TextNote;

                    if (textNote == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"TextNote with ID {elementIdStr} not found"
                        });
                    }

                    var oldText = textNote.Text;

                    using (var trans = new Transaction(doc, "MCP Modify Text Note"))
                    {
                        trans.Start();

                        try
                        {
                            textNote.Text = newText;
                            trans.Commit();

                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                result = new
                                {
                                    id = textNote.Id.Value,
                                    oldText = oldText,
                                    newText = textNote.Text,
                                    viewId = textNote.OwnerViewId.Value
                                }
                            });
                        }
                        catch (Exception)
                        {
                            trans.RollBack();
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error modifying text note");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        /// <summary>
        /// Delete a TextNote element
        /// </summary>
        private Task<string> DeleteTextNote(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var elementIdStr = parameters?["elementId"]?.ToString();

                    if (string.IsNullOrEmpty(elementIdStr))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId parameter is required"
                        });
                    }

                    var elementId = new ElementId(int.Parse(elementIdStr));
                    var textNote = doc.GetElement(elementId) as TextNote;

                    if (textNote == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"TextNote with ID {elementIdStr} not found"
                        });
                    }

                    var deletedText = textNote.Text;
                    var deletedViewId = textNote.OwnerViewId.Value;

                    using (var trans = new Transaction(doc, "MCP Delete Text Note"))
                    {
                        trans.Start();

                        try
                        {
                            doc.Delete(elementId);
                            trans.Commit();

                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                result = new
                                {
                                    deletedId = elementId.Value,
                                    deletedText = deletedText,
                                    viewId = deletedViewId
                                }
                            });
                        }
                        catch (Exception)
                        {
                            trans.RollBack();
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error deleting text note");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        /// <summary>
        /// Change the TextNoteType of an existing TextNote by searching for a type with specific size
        /// </summary>
        private Task<string> ChangeTextNoteType(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var elementIdStr = parameters?["elementId"]?.ToString();
                    var targetSizeInches = parameters?["textSizeInches"]?.ToObject<double?>() ?? 0.09375; // Default 3/32"

                    if (string.IsNullOrEmpty(elementIdStr))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "elementId parameter is required"
                        });
                    }

                    var elementId = new ElementId(int.Parse(elementIdStr));
                    var textNote = doc.GetElement(elementId) as TextNote;

                    if (textNote == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"TextNote with ID {elementIdStr} not found"
                        });
                    }

                    // Find TextNoteType with closest matching size
                    var targetSizeFeet = targetSizeInches / 12.0;
                    var allTextTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNoteType))
                        .Cast<TextNoteType>()
                        .ToList();

                    TextNoteType closestType = null;
                    double closestDiff = double.MaxValue;

                    foreach (var textType in allTextTypes)
                    {
                        var sizeParam = textType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                        if (sizeParam != null)
                        {
                            var size = sizeParam.AsDouble();
                            var diff = Math.Abs(size - targetSizeFeet);
                            if (diff < closestDiff)
                            {
                                closestDiff = diff;
                                closestType = textType;
                            }
                        }
                    }

                    if (closestType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "No suitable TextNoteType found"
                        });
                    }

                    var oldTypeName = doc.GetElement(textNote.GetTypeId())?.Name ?? "Unknown";

                    using (var trans = new Transaction(doc, "MCP Change Text Note Type"))
                    {
                        trans.Start();
                        textNote.ChangeTypeId(closestType.Id);
                        trans.Commit();
                    }

                    var newSize = closestType.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            id = elementId.Value,
                            oldType = oldTypeName,
                            newType = closestType.Name,
                            newSizeInches = newSize * 12,
                            newSizeFeet = newSize
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error changing text note type");
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }

        #region Annotation Batch Manager

        /// <summary>
        /// Find text notes containing specific text pattern
        /// </summary>
        private Task<string> FindTextNotesByContent(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var searchPattern = parameters?["searchPattern"]?.ToString();
                    var viewIdStr = parameters?["viewId"]?.ToString();
                    var caseSensitive = parameters?["caseSensitive"]?.ToObject<bool?>() ?? false;
                    var useRegex = parameters?["useRegex"]?.ToObject<bool?>() ?? false;

                    if (string.IsNullOrEmpty(searchPattern))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "searchPattern is required" });
                    }

                    // Collect text notes
                    FilteredElementCollector collector = string.IsNullOrEmpty(viewIdStr)
                        ? new FilteredElementCollector(doc)
                        : new FilteredElementCollector(doc, new ElementId(int.Parse(viewIdStr)));

                    var textNotes = collector
                        .OfClass(typeof(TextNote))
                        .Cast<TextNote>()
                        .ToList();

                    // Search through text notes
                    var matches = new List<object>();
                    foreach (var note in textNotes)
                    {
                        var text = note.Text;
                        bool isMatch = false;

                        if (useRegex)
                        {
                            var regex = new System.Text.RegularExpressions.Regex(
                                searchPattern,
                                caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            );
                            isMatch = regex.IsMatch(text);
                        }
                        else
                        {
                            isMatch = caseSensitive
                                ? text.Contains(searchPattern)
                                : text.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        if (isMatch)
                        {
                            var view = doc.GetElement(note.OwnerViewId) as View;
                            matches.Add(new
                            {
                                id = note.Id.Value,
                                text = text,
                                viewId = note.OwnerViewId.Value,
                                viewName = view?.Name ?? "Unknown",
                                typeId = note.GetTypeId().Value,
                                typeName = doc.GetElement(note.GetTypeId())?.Name ?? "Unknown"
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalFound = matches.Count,
                            searchPattern = searchPattern,
                            caseSensitive = caseSensitive,
                            useRegex = useRegex,
                            matches = matches
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error finding text notes by content");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Batch update multiple text notes at once
        /// </summary>
        private Task<string> BatchUpdateTextNotes(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var elementIds = parameters?["elementIds"]?.ToObject<List<string>>();
                    var newText = parameters?["text"]?.ToString();
                    var textTypeId = parameters?["textTypeId"]?.ToString();

                    if (elementIds == null || elementIds.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "elementIds array is required" });
                    }

                    var results = new List<object>();
                    using (var trans = new Transaction(doc, "MCP Batch Update Text Notes"))
                    {
                        trans.Start();

                        foreach (var idStr in elementIds)
                        {
                            try
                            {
                                var elementId = new ElementId(int.Parse(idStr));
                                var textNote = doc.GetElement(elementId) as TextNote;

                                if (textNote == null)
                                {
                                    results.Add(new { id = idStr, success = false, error = "Not a text note" });
                                    continue;
                                }

                                var oldText = textNote.Text;
                                var oldTypeId = textNote.GetTypeId().Value;

                                // Update text if provided
                                if (!string.IsNullOrEmpty(newText))
                                {
                                    textNote.Text = newText;
                                }

                                // Update type if provided
                                if (!string.IsNullOrEmpty(textTypeId))
                                {
                                    textNote.ChangeTypeId(new ElementId(int.Parse(textTypeId)));
                                }

                                results.Add(new
                                {
                                    id = idStr,
                                    success = true,
                                    oldText = oldText,
                                    newText = textNote.Text,
                                    oldTypeId = oldTypeId,
                                    newTypeId = textNote.GetTypeId().Value
                                });
                            }
                            catch (Exception ex)
                            {
                                results.Add(new { id = idStr, success = false, error = ex.Message });
                            }
                        }

                        trans.Commit();
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalProcessed = results.Count,
                            results = results
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in batch update text notes");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Find and replace text across multiple text notes
        /// </summary>
        private Task<string> FindAndReplaceText(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var findText = parameters?["findText"]?.ToString();
                    var replaceText = parameters?["replaceText"]?.ToString();
                    var viewIdStr = parameters?["viewId"]?.ToString();
                    var caseSensitive = parameters?["caseSensitive"]?.ToObject<bool?>() ?? false;
                    var useRegex = parameters?["useRegex"]?.ToObject<bool?>() ?? false;

                    if (string.IsNullOrEmpty(findText))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "findText is required" });
                    }

                    // Collect text notes
                    FilteredElementCollector collector = string.IsNullOrEmpty(viewIdStr)
                        ? new FilteredElementCollector(doc)
                        : new FilteredElementCollector(doc, new ElementId(int.Parse(viewIdStr)));

                    var textNotes = collector
                        .OfClass(typeof(TextNote))
                        .Cast<TextNote>()
                        .ToList();

                    var replacements = new List<object>();
                    using (var trans = new Transaction(doc, "MCP Find and Replace Text"))
                    {
                        trans.Start();

                        foreach (var note in textNotes)
                        {
                            var oldText = note.Text;
                            string newText;

                            if (useRegex)
                            {
                                var regex = new System.Text.RegularExpressions.Regex(
                                    findText,
                                    caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                );
                                newText = regex.Replace(oldText, replaceText ?? "");
                            }
                            else
                            {
                                newText = caseSensitive
                                    ? oldText.Replace(findText, replaceText ?? "")
                                    : System.Text.RegularExpressions.Regex.Replace(
                                        oldText,
                                        System.Text.RegularExpressions.Regex.Escape(findText),
                                        replaceText ?? "",
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                    );
                            }

                            if (oldText != newText)
                            {
                                note.Text = newText;
                                var view = doc.GetElement(note.OwnerViewId) as View;
                                replacements.Add(new
                                {
                                    id = note.Id.Value,
                                    viewName = view?.Name ?? "Unknown",
                                    oldText = oldText,
                                    newText = newText
                                });
                            }
                        }

                        trans.Commit();
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalReplaced = replacements.Count,
                            findText = findText,
                            replaceText = replaceText,
                            replacements = replacements
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in find and replace text");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Get statistics about text notes in the project
        /// </summary>
        private Task<string> GetTextStatistics(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var viewIdStr = parameters?["viewId"]?.ToString();

                    FilteredElementCollector collector = string.IsNullOrEmpty(viewIdStr)
                        ? new FilteredElementCollector(doc)
                        : new FilteredElementCollector(doc, new ElementId(int.Parse(viewIdStr)));

                    var textNotes = collector
                        .OfClass(typeof(TextNote))
                        .Cast<TextNote>()
                        .ToList();

                    // Group by type
                    var byType = textNotes
                        .GroupBy(n => n.GetTypeId())
                        .Select(g => new
                        {
                            typeId = g.Key.Value,
                            typeName = doc.GetElement(g.Key)?.Name ?? "Unknown",
                            count = g.Count()
                        })
                        .OrderByDescending(x => x.count)
                        .ToList();

                    // Group by view
                    var byView = textNotes
                        .GroupBy(n => n.OwnerViewId)
                        .Select(g =>
                        {
                            var view = doc.GetElement(g.Key) as View;
                            return new
                            {
                                viewId = g.Key.Value,
                                viewName = view?.Name ?? "Unknown",
                                count = g.Count()
                            };
                        })
                        .OrderByDescending(x => x.count)
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalTextNotes = textNotes.Count,
                            uniqueTypes = byType.Count,
                            uniqueViews = byView.Count,
                            byType = byType,
                            byView = byView
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting text statistics");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        #endregion

        #region Legend/Table Automation

        /// <summary>
        /// Get all legend views in the project
        /// </summary>
        private Task<string> GetLegends(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    var legends = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => v.ViewType == ViewType.Legend)
                        .Select(v => new
                        {
                            id = v.Id.Value,
                            name = v.Name,
                            isTemplate = v.IsTemplate,
                            scale = v.Scale
                        })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalLegends = legends.Count,
                            legends = legends
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting legends");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Create a new legend view
        /// </summary>
        private Task<string> CreateLegend(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var legendName = parameters?["name"]?.ToString();

                    if (string.IsNullOrEmpty(legendName))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "name is required" });
                    }

                    // Legend creation requires more complex setup - not yet implemented
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Legend creation not yet implemented - use existing legends or create manually"
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error creating legend");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Get all schedules in the project
        /// </summary>
        private Task<string> GetSchedules(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    var schedules = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSchedule))
                        .Cast<ViewSchedule>()
                        .Where(s => !s.IsTemplate)
                        .Select(s => new
                        {
                            id = s.Id.Value,
                            name = s.Name,
                            isAssemblyView = s.IsAssemblyView,
                            definition = new
                            {
                                categoryId = s.Definition.CategoryId.Value,
                                fieldCount = s.Definition.GetFieldCount(),
                                filterCount = s.Definition.GetFilterCount(),
                                sortGroupFieldCount = s.Definition.GetSortGroupFieldCount()
                            }
                        })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalSchedules = schedules.Count,
                            schedules = schedules
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting schedules");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Get schedule data (rows and columns)
        /// </summary>
        private Task<string> GetScheduleData(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var scheduleIdStr = parameters?["scheduleId"]?.ToString();

                    if (string.IsNullOrEmpty(scheduleIdStr))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "scheduleId is required" });
                    }

                    var scheduleId = new ElementId(int.Parse(scheduleIdStr));
                    var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                    if (schedule == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Schedule not found" });
                    }

                    var tableData = schedule.GetTableData();
                    var sectionData = tableData.GetSectionData(SectionType.Body);

                    var rows = new List<List<string>>();
                    for (int r = 0; r < sectionData.NumberOfRows; r++)
                    {
                        var row = new List<string>();
                        for (int c = 0; c < sectionData.NumberOfColumns; c++)
                        {
                            var cellText = schedule.GetCellText(SectionType.Body, r, c);
                            row.Add(cellText);
                        }
                        rows.Add(row);
                    }

                    // Get column headers
                    var headers = new List<string>();
                    var headerSection = tableData.GetSectionData(SectionType.Header);
                    if (headerSection != null && headerSection.NumberOfRows > 0)
                    {
                        for (int c = 0; c < headerSection.NumberOfColumns; c++)
                        {
                            var headerText = schedule.GetCellText(SectionType.Header, headerSection.NumberOfRows - 1, c);
                            headers.Add(headerText);
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            scheduleId = scheduleId.Value,
                            scheduleName = schedule.Name,
                            rowCount = sectionData.NumberOfRows,
                            columnCount = sectionData.NumberOfColumns,
                            headers = headers,
                            data = rows
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting schedule data");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Update schedule cell value
        /// </summary>
        private Task<string> UpdateScheduleCell(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var scheduleIdStr = parameters?["scheduleId"]?.ToString();
                    var row = parameters?["row"]?.ToObject<int?>() ?? -1;
                    var col = parameters?["column"]?.ToObject<int?>() ?? -1;
                    var value = parameters?["value"]?.ToString() ?? "";

                    if (string.IsNullOrEmpty(scheduleIdStr))
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "scheduleId is required" });
                    }

                    if (row < 0 || col < 0)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "row and column are required" });
                    }

                    var scheduleId = new ElementId(int.Parse(scheduleIdStr));
                    var schedule = doc.GetElement(scheduleId) as ViewSchedule;

                    if (schedule == null)
                    {
                        return JsonConvert.SerializeObject(new { success = false, error = "Schedule not found" });
                    }

                    // Schedule cells are typically read-only (calculated from model data)
                    // Only certain override cells can be edited
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Schedule cell editing not supported - schedule data is derived from model elements"
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error updating schedule cell");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        #endregion

        #region Pre-Issue QC Dashboard

        /// <summary>
        /// Get all sheets in the project with details
        /// </summary>
        private Task<string> GetAllSheets(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Select(sheet => new
                        {
                            id = sheet.Id.Value,
                            sheetNumber = sheet.SheetNumber,
                            sheetName = sheet.Name,
                            viewCount = sheet.GetAllPlacedViews().Count,
                            placedViews = sheet.GetAllPlacedViews().Select(vid => vid.Value).ToList()
                        })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalSheets = sheets.Count,
                            sheets = sheets
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting all sheets");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Find views that are not placed on any sheet
        /// </summary>
        private Task<string> GetUnplacedViews(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    // Get all views
                    var allViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted)
                        .ToList();

                    // Get all views that are placed on sheets
                    var placedViewIds = new HashSet<long>();
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>();

                    foreach (var sheet in sheets)
                    {
                        foreach (var viewId in sheet.GetAllPlacedViews())
                        {
                            placedViewIds.Add((long)viewId.Value);
                        }
                    }

                    // Find unplaced views
                    var unplacedViews = allViews
                        .Where(v => !placedViewIds.Contains((long)v.Id.Value))
                        .Select(v => new
                        {
                            id = v.Id.Value,
                            name = v.Name,
                            viewType = v.ViewType.ToString(),
                            scale = v.Scale
                        })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalViews = allViews.Count,
                            placedViews = placedViewIds.Count,
                            unplacedViews = unplacedViews.Count,
                            views = unplacedViews
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting unplaced views");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Check for sheets without views
        /// </summary>
        private Task<string> GetEmptySheets(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    var emptySheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(sheet => sheet.GetAllPlacedViews().Count == 0)
                        .Select(sheet => new
                        {
                            id = sheet.Id.Value,
                            sheetNumber = sheet.SheetNumber,
                            sheetName = sheet.Name
                        })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalEmptySheets = emptySheets.Count,
                            sheets = emptySheets
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting empty sheets");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Validate text note sizes against standards
        /// </summary>
        private Task<string> ValidateTextSizes(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var allowedSizesParam = parameters?["allowedSizes"]?.ToObject<List<double>>();

                    // Default allowed sizes in inches: 1/16", 3/32", 1/8", 3/16", 1/4"
                    var allowedSizesInches = allowedSizesParam ?? new List<double> { 0.0625, 0.09375, 0.125, 0.1875, 0.25 };
                    var allowedSizesFeet = allowedSizesInches.Select(s => s / 12.0).ToList();

                    var textNotes = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNote))
                        .Cast<TextNote>()
                        .ToList();

                    var nonStandardNotes = new List<object>();
                    foreach (var note in textNotes)
                    {
                        var typeId = note.GetTypeId();
                        var textType = doc.GetElement(typeId) as TextNoteType;

                        if (textType != null)
                        {
                            var sizeParam = textType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            if (sizeParam != null)
                            {
                                var sizeFeet = sizeParam.AsDouble();
                                var sizeInches = sizeFeet * 12.0;

                                // Check if size is in allowed list (with small tolerance)
                                bool isStandard = allowedSizesFeet.Any(allowed => Math.Abs(allowed - sizeFeet) < 0.0001);

                                if (!isStandard)
                                {
                                    var view = doc.GetElement(note.OwnerViewId) as View;
                                    nonStandardNotes.Add(new
                                    {
                                        id = note.Id.Value,
                                        text = note.Text.Length > 50 ? note.Text.Substring(0, 50) + "..." : note.Text,
                                        viewName = view?.Name ?? "Unknown",
                                        typeName = textType.Name,
                                        sizeInches = Math.Round(sizeInches, 5)
                                    });
                                }
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalTextNotes = textNotes.Count,
                            nonStandardCount = nonStandardNotes.Count,
                            allowedSizes = allowedSizesInches,
                            nonStandardNotes = nonStandardNotes
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error validating text sizes");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Get project warnings
        /// </summary>
        private Task<string> GetProjectWarnings(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var maxWarnings = parameters?["maxWarnings"]?.ToObject<int?>() ?? 100;

                    var warnings = doc.GetWarnings();

                    var warningList = warnings
                        .Take(maxWarnings)
                        .Select(w => new
                        {
                            severity = w.GetSeverity().ToString(),
                            description = w.GetDescriptionText(),
                            elementIds = w.GetFailingElements().Select(id => id.Value).ToList()
                        })
                        .ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            totalWarnings = warnings.Count,
                            returnedWarnings = warningList.Count,
                            warnings = warningList
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting project warnings");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Run comprehensive QC checks
        /// </summary>
        private Task<string> RunQCChecks(JObject parameters)
        {
            return ExecuteInRevitContext(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    // Get all data
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .ToList();

                    var allViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted)
                        .ToList();

                    var placedViewIds = new HashSet<long>();
                    foreach (var sheet in sheets)
                    {
                        foreach (var viewId in sheet.GetAllPlacedViews())
                        {
                            placedViewIds.Add((long)viewId.Value);
                        }
                    }

                    var emptySheets = sheets.Where(s => s.GetAllPlacedViews().Count == 0).Count();
                    var unplacedViews = allViews.Where(v => !placedViewIds.Contains((long)v.Id.Value)).Count();
                    var warnings = doc.GetWarnings().Count;

                    var textNotes = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNote))
                        .Count();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        result = new
                        {
                            summary = new
                            {
                                totalSheets = sheets.Count,
                                emptySheets = emptySheets,
                                totalViews = allViews.Count,
                                placedViews = placedViewIds.Count,
                                unplacedViews = unplacedViews,
                                totalWarnings = warnings,
                                totalTextNotes = textNotes
                            },
                            checks = new
                            {
                                hasEmptySheets = emptySheets > 0,
                                hasUnplacedViews = unplacedViews > 0,
                                hasWarnings = warnings > 0
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error running QC checks");
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        #endregion

        // NLP Methods moved to MCPServer.NLP.cs
        // Intelligence Methods moved to MCPServer.Intelligence.cs
    }
}
