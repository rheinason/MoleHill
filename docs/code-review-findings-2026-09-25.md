# Code review findings — 2026-09-25

This review covers everything committed from 2026-08-28 to 2026-09-25: `610fdec..f375ae6`, 140
first-parent commits and about 169k changed lines.

## How it was run

- **Unmerged branch (`main..review-plan-2026-09-19`, 56 commits).** One cloud ultrareview. Before it
  ran, the diff was trimmed from 102k to 7.0k lines by removing docs, `.rui`, icons, the copied-case
  fixture data, benchmarks, the latency-report tooling and the build setup. Every product-code change
  was kept.
- **Shipped history (`610fdec..main`).** Split into 14 contiguous slices of under 7k lines each. Each
  slice got a local `/code-review high`. The slice boundaries are listed at the end.
- **Checked against HEAD.** Every slice was reviewed as it stood at that point in history, so every
  finding below was re-checked against the current `review-plan-2026-09-19` HEAD (`f375ae6`). Status
  key:
  - **Confirmed**: the code at HEAD still has the defect.
  - **Plausible**: the code path exists as described, but the failure was not reproduced.
  - **Fixed / stale**: HEAD no longer has it. These are listed once at the end so nobody re-chases
    them.

Nothing here was built, run or tested live. "Confirmed" means the code was read, not that the
failure was reproduced.

---

## 1. Unmerged branch — ultrareview

The ultrareview found no blocking defects. Its four findings, all verified by the reviewer:

| # | Where | Finding | Severity |
|---|---|---|---|
| U1 | `Core/Grading/MeshAreaTopologySplitter.cs:947` | The new `catch (AggregateException) when (any OCE)` turns a genuine worker exception into a plain cancellation whenever another worker was cancelled at the same moment, so the real error is lost. Rethrow when any inner exception is not an OCE. | nit |
| U2 | `Core/Grading/MeshConstraintTopologyInserter.WallPatch.cs:100` | `Sample()` does an unindexed O(reference faces) scan per output vertex. The ring loop in `WallQuality.cs` rebuilds the whole candidate, up to 7 times, before it checks the 25k-face guard. Move the guard earlier and index the reference faces. | nit (perf) |
| U3 | `Rhino/UI/MoleHillPanel.cs:1324` | `RebuildVisibleTabLayout` has two stacked `<summary>` blocks. | nit |
| U4 | `Rhino/Services/TerrainBuildService.RetainingWalls.cs:735` | `TryInsertWallConstraintsIntoExistingMesh` still pairs `TryExtractMeshData` arrays with `mesh.Vertices.Count`/`Faces.Count`, the pattern CLAUDE.md forbids. The branch now reaches this fallback more often. | pre-existing, worth fixing |

---

## 2. Shipped history — confirmed at HEAD

### High: wrong output, lost user data, or a crash

| # | Where | Finding |
|---|---|---|
| H1 | `Rhino/Services/TerrainController.cs:1633` | **Duplicating an annotation loses all its settings.** `CloneAnnotation` serializes with `TerrainSerializer.SharedOptions` (camelCase) but deserializes with the default options, so no property binds. The copy comes back as a default card. |
| H2 | `Rhino/UI/MoleHillPanel.Annotations.cs:832` / `MoleHillPanel.cs:2404` | **Annotation cards cannot be reordered.** The drag handle sends `"annotation-drag"`, but only analysis cards have drop handlers, and those accept `"analysis-drag"` only. |
| H3 | `Rhino/Services/TerrainBuildService.Analysis.cs:59, 326-328` (and the aspect summary) | **Mesh counts are paired with extracted arrays** (`currentMesh.Vertices.Count` / `Faces.Count` alongside the normalized copy), which CLAUDE.md forbids. On a mesh that normalization shrinks this is an `IndexOutOfRangeException` on the build worker; on a quad mesh, faces are silently skipped. Together with U4, these are the sites CLAUDE.md says remain. |
| H4 | `Core/Grading/MeshAreaTopologySplitter.cs:551 → 574` | **The zone-split "N faces kept their original topology" warning is always discarded.** It is written to `errorMessage`, and the final `Classify(..., out errorMessage)` then overwrites it with null. |
| H5 | `Core/Grading/MeshAreaTopologySplitter.cs:~526` | **A face that fails to re-triangulate is emitted whole** while its neighbours split the shared edges. This leaves T-junctions or naked edges in the zone submeshes, and the face's full area goes to one zone. (Plausible: the logic matches, not reproduced.) |
| H6 | `Rhino/Services/LayerRoleService.cs:192` + `LayerTemplateEditorDialog` | **Template editor edits never reach a document that already embeds a template.** `Build` prefers the document copy, the editor writes only the local store, and `PullFromLocal`/`PushToLocal` have no callers. The user sees "(modified)" and nothing changes. |
| H7 | `Rhino/Services/LayerRoutingMigration.cs:53` | **Every pre-schema-30 document with a default Retaining Wall is migrated as customised.** The old default `OutputLayerPath` was `MoleHill::Auxiliary`, which never equals `DefaultPath(Walls)`, so the document gets a "(migrated)" template embedded and the office's local template is ignored from then on. |
| H8 | `Rhino/Services/TerrainController.Output.cs:48, 290, 608`; `TerrainController.cs:902`; `TerrainDisplayConduit.cs:585, 623, 713` | **Per-terrain `LayerTemplateName` is ignored** on bake, in layer creation and in conduit appearance: they call `GetTable(doc)` without the terrain. With two templates in a document, preview and bake disagree. |
| H9 | `Rhino/Services/TerrainAnalysisAnnotationBuilder.cs:829` + `MoleHillPanel.Annotations.cs:438` | **Section Cut/Fill hatch pattern, scale and angle rows do nothing.** `SectionsCutFill` defaults `HatchPatternName: "Solid"`, the cut and fill roles inherit it, so the `?? analysis.*` fallback is never reached. |
| H10 | `Rhino/Services/TerrainAnalysisAnnotationBuilder.cs:~880-925` | **A reference terrain's section profile is drawn twice**: once as the grey existing-ground line, and again in the comparison loop, because the serializer forces the reference ID into `ComparisonTerrainIds`. |
| H11 | `Rhino/UI/ColorRampControl.cs:556, 725` | **A symmetric (cut/fill) range can be widened but never narrowed.** `SetRange(value, _range.High)` goes through `FromRequested(SymmetricAboutZero)`, which keeps max(\|low\|, \|high\|). |
| H12 | `Rhino/Registry/AnalysisPrerequisites.cs:54` | **`ChangesElevations` leaves out several modifiers that change elevations**: Retaining Wall, Grade Line, Project To, Add Geometry, In-Situ Stair and Simplify. Cut/Fill and Section cards then warn that "nothing has moved the ground" on terrains where something has. |
| H13 | `Rhino/Services/TerrainReportBuilder.cs:191` | **Report CSV and table omit Cut/Fill volumes.** The report only iterates `EarthworkAnalysisDefinition`, not the `CutFillAnalysisDefinition` that fills the same fields. |
| H14 | `Rhino/Services/TerrainReportTableBuilder.cs:226, 248` | **A Report Table colour override previews but does not bake.** The builder sets `ColorArgb` but also always sets `AppearanceSource = Layer`, and `AddTextEntity` then skips the object colour. |
| H15 | `Rhino/Services/TerrainUnitScaler.cs` | **Unit conversion misses the new length fields**: Project To `FeatherDistance`, and Report Table `InsertionOrigin*` and `TextHeight`. |
| H16 | `Core/Processing/SurfaceConformer.cs` | **Project To does not freeze steep or wall faces.** Wall top and toe vertices project to the same target Z, which breaks the "walls never buried" invariant. It also has no feather at the edge of the target footprint when no boundary is set, which leaves a hard step there. |
| H17 | `Rhino/Services/TerrainBuildService.Boundaries.cs:11` | **`GetBoundaryOwner` ignores `IsEnabled`.** A disabled Triangulate card still trims the terrain (Outer/Hide/Show) and still clips Add Geometry inputs. |
| H18 | `Rhino/Services/TerrainSerializer.cs` (v32 migration) | **Legacy per-modifier Boundaries all become one terrain-wide Outer trim**, and only the largest loop is kept. An Add Geometry patch boundary can crop the whole terrain. (Plausible.) |
| H19 | `Shared/RetainingWallGradePlanner.cs:114` | **The outward normal is paired by index** (`partner[Math.Min(i, …)]`). Rails with different vertex counts or different closed-curve start points get batter graded toward the wall. Use the closest point on the partner rail. |
| H20 | `Core/Grading/PathGrader.Types.cs:255` | **`HasAsymmetricSides` only compares left with right.** When both sides override to the same angle, the daylight loop is ray-marched at the shared angle while sections grade at the override, so the envelope and the batter disagree. The doc comment says otherwise. |
| H21 | `Core/Grading/GradingInputValidator.cs` | **`OutwardNormals` length is never validated** against `VertexCount`. A bad array throws `IndexOutOfRangeException` on a grading worker instead of producing a validation message. |
| H22 | `Rhino/UI/MoleHillPanel.cs:218-264` | **The opacity stepper begins its deferral once (GotFocus) but ends it twice (Enter and LostFocus).** The extra End can close a slider drag gesture mid-drag, so the undo grouping splits. |

### High: survey import (slice 03)

| # | Where | Finding |
|---|---|---|
| S1 | `Core/Interop/SurveyPointFileReader.cs:218` | **Comma-decimal coordinates are silently read 10^n times too large.** `AllowThousands` under the invariant culture turns `512345,67` into `51234567`. Drop `AllowThousands` so such rows fail loudly. |
| S2 | `Rhino/Services/FieldCodeTableStore.cs:46` | **Any read failure (a Dropbox lock, a newer-schema role) overwrites the user's field-code table with the defaults.** A failing `Save` also throws out of `Load()`, outside any try. |
| S3 | `Rhino/Services/SurveyPlacement.cs:88` | **When a project base exists, it is applied to every survey**, including one already on a small local grid. `IsFarFromOrigin` is not consulted on that path, so a local-grid survey is shifted by the full base offset. |
| S4 | `Rhino/Services/SurveyImportCommandService.cs:140, 187` | **All spot points go to the first Spot rule's layer.** The Layer column on every other Spot rule is ignored without warning. |
| S5 | `Rhino/Services/SurveyImportCommandService.cs:149-155, 203` | **The undo record is ended twice** on the failure path: `RollBack` ends it, then the finally block ends it again. |
| S6 | `Core/Interop/SurveyFigureBuilder.cs` | **`IsContinuation` is parsed and exposed in the editor but never read**, so `EP-` does not rejoin the previous run. |
| S7 | `Rhino/Services/SurveyPlacement.cs:142` | **A newly created project base is saved before the undo record starts** and is not reverted on failure or undo. (Plausible.) |
| S8 | `Core/Interop/FieldCodeParser.cs:16` | **`.` is a token separator**, so a `TOE 1.5` attribute becomes figure number 1 and splits runs. (Plausible.) |
| S9 | `Rhino/Services/FieldCodeTableStore.cs:135` | **`Normalize` silently drops duplicate-code rules on save**, usually the row the user just edited. (Plausible.) |
| S10 | `Rhino/Services/SurveyImportCommandService.cs:195` | **Typed layer paths are not validated.** An invalid path falls back to the current layer. (Plausible.) |

### Medium: inconsistent behaviour, edge cases

| # | Where | Finding |
|---|---|---|
| M1 | `Core/Analysis/AnalysisColorMapper.cs:76` | `FindBand` treats the first band as closed at the top and the others as half-open: a value of 2 goes to band 0, but 4 goes to band 2. |
| M2 | `Core/Analysis/AnalysisColorMapper.cs:50` | A user interval smaller than span/int.MaxValue makes the `(int)` cast overflow, so stepped mode paints one flat colour. |
| M3 | `Rhino/UI/ScrubField.cs:198` | Click-to-type pre-fills formatted text (the Interval field shows `2.00*`) and then parses it with a plain `double.TryParse`, so the edit is silently dropped. |
| M4 | `Rhino/Services/SculptAnalysisColorizer.cs:242` | The sculpt auto-range weights by 3D face area; the preview builder weights by plan area (`MeasureFace`). Colours shift at the start and end of a sculpt session. |
| M5 | `Rhino/Services/CurveReviewForm.cs:725` | `Parse` returns 0 for empty or unparseable text, and the rule threshold is then clamped to 0.01% and persisted. The slope thresholds also bypass `SlopeInput`/`SlopeUnitPreference`, which CLAUDE.md requires. |
| M6 | `Rhino/Services/CurveReviewForm.cs:690-692` | `ZoomTo` reads `.Point` from a `FirstOrDefault` on a struct, so when no event exists the view zooms to the world origin. |
| M7 | `Rhino/Services/CurveReviewLabeller.cs:107` | Station and cut/fill labels come from the nearest sample (up to half a sample spacing off) and are baked into text dots. |
| M8 | `Rhino/Services/GeometryCommandService.cs:971` | `FromDisplayValue` converts degrees with `tan()` without the `MaxSlopeDegrees` bound that `SlopeInput.TryParse` enforces, so 95° becomes a falling slope. |
| M9 | `Core/Analysis/SlopeInput.cs:182` vs `GeometryCommandService.cs:976` | A bare number in Ratio units means rise/run in panel fields but `1:n` at the command line. `4` is a 76° batter in one place and 1:4 in the other. |
| M10 | `Grasshopper/Components/SlopeAnalysisComponent.cs:56` | The help text offers `3=promille`, but the input is clamped to 0..2. |
| M11 | `Rhino/Services/BlockCommandService.cs` | `mhSetSunNorth` does not trigger a refresh, so aspect colours and bearings keep the old north until an unrelated rebuild. |
| M12 | `Grasshopper/Components/MeshSimplifyComponent.cs:28, 92` | `Mesh` is `optional: false`, so the documented Terrain-only wiring never solves. The retain percentage rounds up in Grasshopper (`Ceiling`, min 3) and down in Rhino (`Floor`), so the two hosts differ. |
| M13 | `Grasshopper/Components/RetopoComponent.cs:84` | `ComputeNormals()` without `UnifyNormals()`, which CLAUDE.md requires. |
| M14 | `Core/Analysis/BasinBoundaryExtractor.cs:67` | Every chain is closed by repeating its first point, including open chains from unbalanced edges. That draws a false straight divide. (Plausible.) |
| M15 | `Rhino/Services/TerrainBuildService.Tin.cs:420` | Add Geometry placed after walls or grading re-triangulates all current vertices as XY spots. Wall top and toe share XY and can be deduped. (Plausible; ordering is no longer enforced.) |
| M16 | `Rhino/Services/TerrainAnalysisPreviewBuilder.cs` | The histogram is binned over the data min..max but drawn across the mapped range, so bars sit under the wrong colours (slope and aspect). |

### Performance (the code path exists; cost not measured)

- `Core/Analysis/PondingSolver.cs`: the MinimumDepth filter runs after full-mesh `MeasureFill` and
  `TraceShoreline` for each sink. Move it right after `TryFlood`.
- `Core/Analysis/BasinBoundaryExtractor.cs`: O(basins × faces) scan-per-basin.
- `Core/Processing/RegionInputClipper.cs`: Data Clip tests every segment against every polygon edge
  with no bounding-box reject.
- `Core/Processing/SurfaceConformer.cs:115`: polygon distance computed twice per inside vertex.
- `Rhino/Services/TerrainAnalysisAnnotationBuilder.cs:1014`: the cut/fill reference is re-meshed and
  combined for every section cell.
- `Rhino/Services/TerrainSectionSlicer.cs:101`: `OffsetPlaneOffVertices` copies and scans all
  vertices per cut segment.
- `Rhino/Services/TerrainAnalysisPreviewBuilder.cs`: the catchment and ponding previews re-run the
  full solvers with no cancellation.
- `Grasshopper/Components/MoleHillTerrainSnapshotComponent.cs`: full mesh copy and hash on every
  Rhino `StateChanged` event.

### Low: docs, duplication, dead code

- `docs/file-index.md` / `docs/architecture.md` lagged several structural commits. Regenerate and
  re-check before merging.
- Two-family visibility rule duplicated (`TerrainController.Display.cs:562` and
  `TerrainAnalysisPreviewBuilder`). Preview and bake could drift.
- `BuildSubMesh`/`MapVertex` copied into three files; CSR cell build copied into three indexes.
- `CountFaces` and the old `BuildOwnedMesh` overload in `MeshAreasComponent` are dead.
- Longitudinal sections now get an auto elevation grid where interval 0 used to mean none. This was
  intended by `fc23aa6`, but existing documents change silently.

---

## 3. Fixed or stale at HEAD — do not re-chase

These were accurate when their slice was reviewed, but HEAD has fixed them:

- Build breaks in slices 14 and 15 (`SlopePreviewPaletteCatalog`, `PeekFinalTerrainMesh`,
  `ResolveGradePathDefinitions`, missing `UiLayouts` members, `SlopeAnalyzer` signature): all
  resolved. Variable-width Grade Path is wired into the Rhino build.
- Toolbar macros `mhSlopeCheckAndMark` and `mhOffset3dPolyline`: gone from the `.rui`.
- The curve label layer's `::Labels` suffix: now `LayerRoleService.EnsureRoleLayer(…, LayerRole.Labels)`.
- A terrain-gap marker drawn with no terrain: now guarded by `hasTerrainMesh`.
- `CurveReviewLabeller` in the test-project glob: now excluded.
- `DemSurface` unused: now sampled in `TerrainBuildSnapshotBuilder`.
- `DistributionBins` missing from `CloneAnalysis`: now copied.
- Retaining-wall rails baked as auxiliary curves: only the Brep is emitted now.
- Grade Pad losing its persistent constraints on a topology cache hit: the cache-hit path publishes
  them (`TerrainBuildService.Grading.cs:~205`).
- Zone earthworks sharing cached stats across sub-meshes: the cache key includes the mesh arrays,
  so it is never shared. (The opposite complaint, that it never hits, stands as a minor waste.)
- Scatter occupancy `Dictionary<long,int>` with the `lo ^ hi` hash: now keyed by a `(double, double)`
  tuple.

---

## Appendix: slice boundaries

| Slice | Range | Scope |
|---|---|---|
| ultrareview | `main..f375ae6` | Unmerged branch |
| 03 | `9dc694b..816aff3` | Survey field-code import |
| 04a / 04b | `8c039e3..9dc694b` | Grasshopper redesign, Simplify, Grade Line (Core / hosts) |
| 05 | `811e31c..ab3cae4` | Drainage, catchment, boundary roles, ponding |
| 06 | `ab3cae4..8c039e3` | Quantity reporting, Project To |
| 07 | `0574a05..811e31c` | Slope units, section ease, aspect, cut/fill delta |
| 08 | `7bb2a00..0574a05` | Scalability pass |
| 09 | `cfec337..7bb2a00` | Parameter schemas, zone split |
| 10 | `ff3c5de..cfec337` | Curve inspector rework |
| 11 | `a4595ec..ff3c5de` | Analysis vs annotation separation |
| 12 | `6b8bb19..a4595ec` | Layer role routing |
| 13 | `cfbdb46..6b8bb19` | Ramp panel UI, cut/fill reference |
| 14 | `d2aad32..cfbdb46` | Curve review overlay, DEM, colour ramps |
| 15 | `610fdec..d2aad32` | OffsetFeature, annotation styles, CSR adjacency, variable width |
