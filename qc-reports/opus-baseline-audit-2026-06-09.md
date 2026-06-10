# RevitMCPBridge2026 — Bug Audit Baseline (Opus 4.8)

**Date:** 2026-06-09
**Model:** Claude Opus 4.8 (1M), 5 parallel sub-agents (Sonnet readers), ~198K LOC / 178 C# files
**Purpose:** Baseline to grade Fable 5 against. Run the SAME audit prompt on Fable 5, then compare:
1. Did Fable find these? (recall)
2. Did Fable find things Opus missed? (the real test)
3. Did Fable flag false positives? (precision — lower is better)

> ⚠️ These are model-generated and **UNVERIFIED**. Confidence is marked per item. Treat High-confidence as "very likely real," Med/Low as "check before fixing." Part of grading Fable 5 is whether it's more precise (fewer false alarms) or more thorough.

---

## TIER 1 — HIGH severity, crash / data-loss / silent-corruption

### H1. `callMCPMethod` infinite self-recursion → stack overflow crashes Revit
- **File:** `src/MCPServer.cs:3929` (`CallMCPMethodPassthrough`)
- **What:** `callMCPMethod` recursively calls `ProcessMessage()` with no depth/cycle guard. A crafted `{"method":"callMCPMethod","params":{"method":"callMCPMethod",...}}` blows the stack and kills the Revit process.
- **Fix:** Reject `targetMethod == "callMCPMethod"` at the top; optional `AsyncLocal<int>` depth counter.
- **Confidence:** High

### H2. `PlaceViewOnSheetForced` deletes the ORIGINAL view (irreversible data loss)
- **File:** `src/SheetMethods.cs:5618`
- **What:** Strategy C calls `doc.Delete(viewId)` on the original drafting view after placing a duplicate — silent, irreversible model-data loss inside a normal "place view" call.
- **Fix:** Remove the `doc.Delete(viewId)` block; keep the duplicate (different name) or rename without deleting.
- **Confidence:** High

### H3. Dimension-to-wall-face picks WRONG face on flipped walls (~50%) — violates the "dimension to core finish" standard
- **File:** `src/DimensioningMethods.cs:3273–3305` (`GetWallFaceReferenceForDimension`)
- **What:** Exterior-face detection assumes the CCW normal is always exterior; never reads `wall.Flipped`. For any wall drawn right-to-left or flipped, exterior/interior are swapped. This is the documented "+90°CCW face-side gotcha."
- **Fix:** `bool isExteriorFace = wall.Flipped ? (dot < 0) : (dot > 0);`
- **Confidence:** High

### H4. Older `createDimensionString` wall_face path dimensions to CENTERLINE, not the face
- **File:** `src/DimensioningMethods.cs:1222–1242`
- **What:** Geometry pulled with `new Options()` (default `ComputeReferences=false`) → `Face.Reference` is null → falls back to `new Reference(wall)` = centerline. Every wall_face dimension silently lands on centerline. (Also violates the core-finish standard.)
- **Fix:** `new Options { ComputeReferences = true, View = view }`.
- **Confidence:** High

### H5. Pipe `ReadLine()` blocks forever; `Stop()` can't interrupt → server freeze / DoS
- **File:** `src/MCPServer.cs:578` (+ `:525` `PipeOptions.None` entrenches it)
- **What:** Synchronous `reader.ReadLine()` with no timeout. A silent client hangs `HandleClient` permanently; `Stop()`'s `Wait(5000)` blocks too. Up to 254 concurrent connections = DoS surface.
- **Fix:** `PipeOptions.Asynchronous` + `ReadLineAsync().WithCancellation(ct)`, or set `ReadTimeout` and catch `IOException`.
- **Confidence:** High

### H6. Unsynchronized lazy init of `_methodRegistry` → dictionary corruption under concurrent clients
- **File:** `src/MCPServer.cs:3747` (also `:323`, `:362`)
- **What:** `if (_methodRegistry.Count == 0) Initialize...()` is not thread-safe; concurrent clients race writes into a plain `Dictionary`, → corruption / `KeyNotFoundException` / infinite probe loops.
- **Fix:** Initialize once at startup (already done in `RevitMCPBridgeApp.cs`) and delete the lazy guards, or `lock`/`Lazy<>`.
- **Confidence:** High

### H7. Batch drain caps at 10 with no re-raise → 11th+ queued requests starve
- **File:** `src/MCPRequestHandler.cs` (`Execute`, ~:65/:129)
- **What:** After `maxBatchSize=10` items the handler exits without re-raising the ExternalEvent. Extra queued requests stall until the next incoming request (or the 5-min timeout).
- **Fix:** If `_requestQueue.Count > 0` after the loop, `Raise()` again.
- **Confidence:** High

### H8. `TransactionGroup` static field never cleared on failure → permanently bricks the transaction-group subsystem
- **File:** `src/TransactionMethods.cs:40–54, 84–96, 125–141`
- **What:** `_activeTransactionGroup` checked for null but not `IsValidObject`; on doc switch/close the Assimilate/RollBack throws, the static is never reset, and every future `StartTransactionGroup` returns "already active" until Revit restarts.
- **Fix:** Guard `IsValidObject`, null out in `finally`, subscribe to `DocumentClosing` to clear.
- **Confidence:** High

### H9. `createCompoundWallType` flakiness — shell-layer inference + zero-width layer (ROOT CAUSE of known wall-type pain)
- **File:** `src/WallMethods.cs:1398–1403` (shell counts), `:1368–1370` (zero-width layer)
- **What:** Shell-layer counts mis-inferred when a membrane layer precedes the structure layer → Revit silently rejects the shell designation; and a layer with omitted thickness → `widthFt=0` → `CreateSimpleCompoundStructure` throws inside the transaction. Together these explain the intermittent breakage.
- **Fix:** After `SetNumberOfShellLayers`, verify with `GetNumberOfShellLayers` and fall back to 0/0; reject/҂default any non-membrane layer with width 0 before building the structure.
- **Confidence:** High (mechanism), Med (exact reproduction)

### H10. `CreateLinearDimension` deletes the temp detail lines it just referenced → broken/phantom dimension
- **File:** `src/ViewAnnotationMethods.cs:513–524` (same pattern `src/DetailMethods.cs:3526`)
- **What:** Creates throwaway detail lines, dimensions to their endpoints, then deletes them in the same transaction before commit → dangling references; Revit throws at commit or leaves a phantom dim.
- **Fix:** Don't delete the referenced lines; dimension to real model element references (walls/floors) or use invisible/hidden refs.
- **Confidence:** High

### H11. Roof `set_SlopeAngle` passed a rise/run ratio where Revit 2022+ expects radians
- **File:** `src/FloorCeilingRoofMethods.cs:508, 543` (and `modifyRoofSlope` `:1674`)
- **What:** `Math.Tan(deg)` (rise/run) passed straight to `set_SlopeAngle`, which expects radians → ~10% wrong slopes. `addSlopeArrow` (`:1620`) does it right (`Math.Atan`), confirming the inconsistency.
- **Fix:** `slopeRadians = slopeDegrees * Math.PI/180.0;` pass that.
- **Confidence:** Med

### H12. `Cast<Room>()` on a `SpatialElement` collector throws when MEP Spaces/Areas exist
- **File:** `src/RoomMethods.cs:1286–1301` (`getRoomBoundaryWalls`)
- **What:** `.OfClass(typeof(SpatialElement)).Cast<Room>()` throws `InvalidCastException` in any project containing Spaces or Areas.
- **Fix:** `.OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()`.
- **Confidence:** High

### H13. `addScheduleFilter` builds `ScheduleFieldId` from the field INDEX (wrong constructor) → filters the wrong field
- **File:** `src/ScheduleMethods.cs:1093`
- **What:** `new ScheduleFieldId(fieldIndex)` ≠ the opaque id from `definition.GetFieldId(fieldIndex)` (which grouping/sorting use correctly). Filter references a non-existent/incorrect field.
- **Fix:** `var fieldId = schedule.Definition.GetFieldId(fieldIndex);`
- **Confidence:** High

### H14. `createRoomSeparationLine` makes a plain `ModelCurve` (with a styled line-style) that does NOT bound rooms
- **File:** `src/RoomMethods.cs:916–938`
- **What:** Styling a ModelCurve doesn't make it room-bounding. Rooms won't close on it.
- **Fix:** Use `doc.Create.NewRoomBoundaryLines(sketchPlane, curveArray, view)`.
- **Confidence:** High

### H15. Systemic unit bug — wall height / door+window sill passed as feet, but the LLM naturally sends inches
- **File:** `src/WallMethods.cs:41,80,117,156`; `src/DoorWindowMethods.cs:387–392`
- **What:** No unit assertion/conversion. `{"sillHeight":36}` (meaning 36") becomes 36 FEET. Confirmed prior correction vector.
- **Fix:** Accept a `unit`/`isInches` flag and convert, or sanity-clamp (e.g. height>200 ⇒ inches), and document feet at the boundary.
- **Confidence:** High

---

## TIER 2 — HIGH/MED null-deref crashes (caught by `using` rollback, but crash the op + return opaque errors)

- **N1.** `CreateElevation` uses `doc.ActiveView.Id` — null when no view open; leaves a dangling marker. `src/ViewMethods.cs:282`. **High**
- **N2.** `PlaceSpotElevation`/`PlaceSpotCoordinate` read `.Id.Value` on a possibly-null result. `src/AnnotationMethods.cs:2016, 2120`. **High**
- **N3.** `DuplicateSheet` casts titleblock with no null check, derefs `.Symbol.Id`. `src/SheetMethods.cs:1293`. **High**
- **N4.** `DuplicateSheet` viewport loop derefs `Viewport.Create` result (null for empty views). `src/SheetMethods.cs:1318`. **High**
- **N5.** `TextNote.Create` fed `GetDefaultElementTypeId(...)` that can be `InvalidElementId` in stripped models. `src/DetailMethods.cs:7033, 7628, 8082, 8112`. **Med**
- **N6.** `setParameterValue` etc. take `ActiveUIDocument.Document` with no null guard (~36 sites). `src/ParameterMethods.cs`, `src/ElementMethods.cs`. **High** (volume), Med (impact — caught)
- **N7.** `CreateSheet` (primary) doesn't `Activate()` the titleblock symbol; sibling methods do. `src/SheetMethods.cs:329`. **Med**

---

## TIER 3 — MED commit/semantics + parse robustness

- **M1.** Several write methods `Commit()` even when all `param.Set()` failed, returning misleading success/failure. `src/ParameterMethods.cs:1145,1410`; `src/MCPServer.cs:6082,6183`; `src/FamilyMethods.cs:240`. **Med**
- **M2.** `CreateSharedParameter`/`BindSharedParameter` commit before checking the bind result (inverted order). `src/ParameterMethods.cs:547,916`. **Med**
- **M3.** Missing `"method"` field ⇒ `TryGetValue(null)` ⇒ `ArgumentNullException` ⇒ opaque "Value cannot be null." `src/MCPServer.cs:679`. **Med**
- **M4.** `Thread.Sleep(50)` on the Revit UI thread inside `Execute` (up to ~450ms UI freeze/batch). `src/MCPRequestHandler.cs:129`. **Med**
- **M5.** `RenumberSheets` `int.Parse(Regex digits)` throws `FormatException` on non-numeric input. `src/SheetMethods.cs:1515`. **Med**
- **M6.** `batchCreateSheets` duplicate-number check only consults pre-loop list ⇒ in-batch collisions. `src/SheetMethods.cs:4401`. **Med**
- **M7.** Double params set without unit conversion for Angle (radians) / temperature (Kelvin) specs. `src/ParameterMethods.cs:1136`. **Med**
- **M8.** Schedule `SheetColumnWidth` set in feet, undocumented (2 ⇒ 24"). `src/ScheduleMethods.cs:2246`. **Med**
- **M9.** `batchDimensionWindows` / `createEqualityDimension` fixed 10-ft dim line that misses references on longer walls/arrays. `src/DimensioningMethods.cs:1633, 1875`. **Med**
- **M10.** `createExtrusion` extrusion-depth + post-hoc `MoveElement` puts family geometry at the wrong position for non-zero start offset. `src/FamilyEditorMethods.cs:483–519`. **High** (per agent), needs verify
- **M11.** `AutoHandleDialogs` static bool not `volatile` (cross-thread stale read). `src/MCPServer.cs/RevitMCPBridgeApp.cs:24`. **Med** (low prob on x64)
- **M12.** `newDoc.Close()` in `finally` without `IsValidObject` check can mask the root exception. `src/DocumentMethods.cs:3717, 3946`. **Med**
- **M13.** Read-only `FilteredElementCollector` query wrapped in a write transaction on an in-memory doc. `src/DocumentMethods.cs:3318`. **Med**

---

## TIER 4 — LOW / cleanup (likely lower value; good false-positive bait for the comparison)

- **L1.** `FilteredElementCollector` not `using`-disposed in batch loops. `src/DocumentMethods.cs` (~30 sites). **Low**
- **L2.** IFC/Export wrapped in unnecessary transactions. `src/DocumentMethods.cs:1182`. **Low**
- **L3.** `deleteFamilyType` reads `.Id.Value` after `doc.Delete()`+commit. `src/FamilyMethods.cs:444`. **Low**
- **L4.** `PlaceViewOnSheetForced` doesn't restore the original active view. `src/SheetMethods.cs:5488`. **Low**
- **L5.** `sheetIds`/`createCompoundWallType sourceTypeId` `ToObject<>` with no null guard / int-vs-long inconsistency. `src/SheetMethods.cs:1501`, `src/WallMethods.cs:1333`. **Low** (long/int one is shaky — good FP test)

---

## Scorecard (fill in after Fable 5 run)

| Metric | Opus 4.8 | Fable 5 |
|---|---|---|
| High-severity real bugs | ~15 | |
| Found H1–H15? (recall of this list) | — | __/15 |
| NEW real bugs Opus missed | — | |
| False positives flagged | a few (M10, L5, H11 unverified) | |
| Best single catch | H2 (silent view deletion) / H3 (flipped-face) | |

**Verdict line:** _Is 2× Opus usage worth it for this codebase? _______________________________________
