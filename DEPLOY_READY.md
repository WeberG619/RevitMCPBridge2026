# DEPLOY READY (HOLD for Weber's go) — createWoodTruss FACE-BUTT build (2026-06-14, 4th)

**Action for the deploy session:** run `deploy_truss.ps1` ONLY when Weber confirms (he is researching
native Revit truss families in parallel). Then reopen model + start MCP Bridge, confirm pong.

## Solid cut is DEAD for wood — proven live
3rd deploy (overlap-then-cope) ran and returned the definitive error:
`IsAllowedForSolidCut` == FALSE for BOTH the 2x4 web and 2x6 chord;
`AddCutBetweenSolids` threw `ArgumentException: The element does not meet the condition that it must
be solid and must be a GenericForm, GeomCombination, or a FamilyInstance. Parameter name: solidToBeCut`.
Wood loadable Structural Framing is INELIGIBLE for SolidSolidCutUtils (same class of restriction as
the steel-only AddCoping). No amount of overlap fixes eligibility. Cope path abandoned.

## This build = FACE-BUTT (deterministic, no solid cut)
Keeps `DisallowJoinAtEnd` (proven: kills auto-extension, Cut Length == System Length). Each web end
is slid along its axis to the chord's NEAR FACE plane so the square web end butts flush against the
chord. No cut API used. Caveat: square web end vs sloped chord = tiny triangular gap on steep
diagonals (standard for most real truss models). Compiles 0 errors. Model already clean.

## Post-deploy verification (Claude will run)
1. createWoodTruss one gable test (panels 8) at x=70. 2. Screenshot/eyeball: web ends butt the chord
faces, no overlap/extension. 3. Weber approves → frame whole hip system + group + setRoofLayers + save.

## What changed
`src/TrussBeamMethods.cs` → `createWoodTruss` now copes web ends to the chords the **proper** way:
- Chords keep auto-join → miter at ridge/heels (unchanged).
- Webs get `DisallowJoinAtEnd` (kills the weird auto-extension), sit at the work point penetrating
  the chord ~half-depth, then are coped flush via
  **`SolidSolidCutUtils.AddCutBetweenSolids(doc, web /*solidToBeCut*/, chord /*cuttingSolid*/)`**
  against the bottom chord and their top chord.
- Response now returns `copesMade` (count of successful cuts).

Replaces the failed `JoinGeometryUtils.JoinGeometry` approach (Join Status stayed 0, members uncut).

## Compile status
`dotnet build -c Debug -o bin\TrussCheck` → **Build succeeded, 0 errors** (Revit 2026 API). Not yet deployed to the live Addins DLL.

## Post-deploy verification (Claude will run)
1. Delete any leftover test truss from prior attempts.
2. `createWoodTruss` one test (gable, panels 8) → expect `copesMade > 0`.
3. `revit_get_element_properties` on a diagonal web → **Volume should DROP below the full 0.1678**
   and Cut Length ≈ chord-to-chord face distance (no extension).
4. Weber eyeballs the live 3D for clean coped ends.
5. If clean → frame the whole hip system per SpineA_truss_plan.md, group by role, setRoofLayers, save.
