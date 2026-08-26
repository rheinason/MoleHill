# MoleHill.Rhino/Services

The Rhino-side engine: builds terrain from the saved definition, manages document state, previews, and
bakes. Rhino API lives here; reusable math is in `MoleHill.Core`. See `docs/architecture.md`.

## Build pipeline
- `TerrainBuildService.cs` + `TerrainBuildService.*.cs` partials - the staged build orchestrator. Each
  partial owns a stage: `.Tin`, `.MeshConstraints`, `.Grading`, `.Zones`, `.Analysis`, `.Objects`,
  `.Scatter`, `.Sculpt` (replays the sculpt displacement field as displacement-only); plus
  `.Cache`, `.Fingerprints`, `.Types`. Most stages are fingerprint-cached
  (`StageCacheEntry`); analysis, zones, markers, object placements, and scatter run only in
  `TerrainBuildMode.Final`. Analysis entries are per analysis id, so changing one card does not
  invalidate unrelated summaries/outputs; a fully cached analysis pass skips mesh extraction,
  elevation scanning, and area computation.
- Cache-hit timings carry a structured cache-hit flag. Cached cold timing reports are filtered on
  restore, and normalized cached meshes are duplicated without a redundant normalization pass.
- Long-running builds emit live phase, elapsed-time, and managed-memory updates through the controller's
  idle loop into the panel Status card. Rhino command history receives only concise started/finished,
  cancelled, or failed lines; detailed progress, diagnostics, and timings remain in Status.
- Triangulate flattens each source curve once and reuses the packed stations for constraints and TIN
  input. Its Contour Mode is `Auto` / `Constrained` / `Vertices only`; Auto treats contour sets at or
  above 250,000 source vertices as unconstrained samples while breaklines and the boundary stay exact.
  This matches the fast exploded-vertices Grasshopper path for multi-million-point contour datasets.
  The panel requests vertices/faces only from Core, skips unused edge-topology and large incremental-edit
  indexes, and uses a known-valid TIN finalizer rather than rescanning the Rhino mesh for duplicate,
  unused, or degenerate data. Mesh fingerprints stream the cached flat arrays in bulk.
- `LargeTinDiagnostic.cs` backs `mhBenchmarkLargeTin`, a background, deterministic 247k-point benchmark
  that times the shared TIN engine separately from Rhino conversion, normalization, fingerprints, and
  mesh-cache duplication without requiring a user model. Each phase reports managed heap, total
  allocation, process-private bytes, and working-set deltas so Rhino/native clone costs remain visible.
- Reference-comparison analyses (Cut / Fill and Earthwork) share one centroid-delta pass per
  reference/boundary fingerprint. The common 2.5D reference lookup uses Core `MeshHeightProjector`;
  Rhino mesh-line projection is kept only for overlapping/near-vertical reference regions and records a
  diagnostic when used.
- Large final Grade Path stages with persistent hard constraints ask Core to try split-keep before the
  explicit carve/weld tier; smaller and unconstrained paths remain explicit-first.
- Retaining Wall inserts accepted toe/top rails directly into the incoming mesh first, splitting only
  crossed faces and preserving untouched topology. A full constrained rebuild is reserved for cases
  where local topology insertion cannot produce an accepted mesh.
- Isotropic Remesh with Edge Length 0 derives its target from plan area per input face instead of the
  median edge. This preserves approximate global face density on terrains mixing dense feature sampling
  with sparse outer faces; disjoint split/collapse thresholds and split-before-collapse settle auto mode
  in three rounds without the former operator churn.
- Retaining-wall warnings carry local failure points/focus segments from the shared planner. The
  viewport shows action-oriented labels such as `Ends do not match`, `Rail doubles back`, and
  `Missing matching rail`; the whole input rail is retained only as subdued context.
- Open rails that fail crossing/station validation get one bounded cleanup candidate using the smaller
  of a tenth of Detail Size and a twentieth of wall width (never below wall tolerance). Successful
  cleanup is informational; authored geometry that already passes is unchanged. Centerline crossings
  are finite-segment tests and distinguish vertically separated plan crossings from likely physical
  overlap.
- `TerrainBuildSnapshot.cs` / `TerrainBuildSnapshotBuilder.cs` / `TerrainBuildSnapshotResolver.cs` -
  resolve doc geometry (points/curves/blocks/layers) into the immutable build snapshot the service reads.
- Layer-backed source sets use Rhino's native `FindByLayer` lookup, then re-resolve every result by ID through
  the active object table before accepting normal, locked, or hidden geometry. This rejects stale wrappers and
  transform predecessors that Rhino's layer lookup can retain. Terrain sources are restricted to ModelSpace;
  PageSpace objects on the same layer are excluded. Instance-definition, reference,
  grip, light, and phantom objects remain excluded.
- `TerrainBuildResult.cs` - outputs (meshes, generated-object lists, diagnostics, runtime overlays, timings).
- Zone stages also emit runtime-only `ZoneAnalysisSummary` values for resolved plan/surface area,
  elevation, slope, mesh counts, and reference-based cut/fill; these are carried through the display
  state and zone-stage cache but are not persisted as zone settings.
- Smooth Breaklines can select curves already used by earlier Grade Path modifiers; the Smooth stage
  expands those selected centerlines into local road center/left/right breaklines without persisting
  them as global hard constraints.
- `SculptConstraintMaskBuilder.cs` resolves Sculpt constraint sources into the Core mask. Ordinary
  closed/open curves become protected areas/breaklines; selected sources belonging to enabled earlier
  Grade Paths expand to the path's configured design width.

## State, preview, bake
- `ModelUnitGuard.cs`, shared `ModelUnitContext.cs`, and `TerrainUnitScaler.cs` define the host unit
  contract. Unitless documents remain readable but dimensional actions/builds are blocked. Rhino's
  scale-with-units event rescales every persisted physical value (including sculpt payloads and inverse-
  area scatter density), clears caches, saves, and schedules live rebuilds; dimensionless settings do not
  change. `TerrainBuildSnapshot` carries the resolved context to every stage.
- `TerrainController.cs` + `TerrainController.*.cs` partials - owns document state (JSON in the .3dm)
  and the terrain command surface; split by concern: `.Build` (scheduling + background-build lifecycle),
  `.Output` (output sync + bake + attributes + owned-object lifecycle), `.Events` (Rhino doc events,
  idle, source sync), `.Display` (display state, placement sync, materials). The root file keeps CRUD
  commands, state/save/undo, sources/selection, and contour helpers.
- `TerrainJsonTypeResolver.cs` - registry-driven `ModifierDefinition` JSON polymorphism (replaces the
  hand-maintained `[JsonDerivedType]` list); wired into `TerrainSerializer.SharedOptions`.
- `TerrainDisplayConduit.cs` / `TerrainDisplayState.cs` - transient viewport preview of generated
  objects and runtime overlays (no doc objects until bake). Overlay drawing has per-terrain budgets and
  severity ordering.
- `RuntimeOverlay.cs` - explicit non-bakeable marker/dot/text/polyline/mesh contracts for reusable
  Diagnostic and Guide channels. Owners are terrain/modifier/analysis/object/tool ids; palette, cloning,
  stable issue metadata, and bounds live here.
- `TerrainRuntimeCache.cs` - per-terrain runtime cache (stage entries, TinEngine, display state, cloner).
  Worker caches shallow-copy stage mesh outputs; the controller defers disposal of displaced main-cache
  meshes until retired worker tasks have drained. Grade Pad topology entries retain reusable flat
  geometry, while Grade Path entries retain summary metadata and patch bounds without duplicating full
  vertex/face arrays. The controller also inspects the cached incoming stage for Smooth/Sculpt
  mesh-regularity warnings when no earlier Remesh is enabled. Per-owner diagnostic visibility is
  session-only on the main cache, intentionally excluded from worker copies and build-cache merges.
- `GeneratedRhinoObject.cs` - a previewable/bakeable output (geometry or block instance). Brep outputs
  cache explicit meshes built with the document's render settings so conduit tessellation stays stable
  across frames and closely matches the baked object.
- Contour curves and labels use their configured output layer, with blank values resolved to the selected
  terrain's Annotation layer before preview/sync/bake. Waterflow from Points follows the final mesh's
  per-face downhill gradients and emits one previewable/bakeable terrain-conforming curve per valid
  point source.
- The layer command surface creates the selected terrain's configured Terrain, Auxiliary, and Annotation
  output layers directly; it does not depend on highlighted source layers.
- `SculptSessionController.cs` - the interactive sculpt session: GetPoint loop (drag = stroke,
  Enter/Esc = done), brush dabs onto a working mesh via `Core/Sculpting/SculptBrushEngine`, incremental
  normal patching, shared constraint masking, per-stroke field commit + stroke undo stack, and
  F/Shift+F adjust modes. DynTopo is
  disabled and hidden for now. `TerrainController.Sculpt.cs` holds the display lock (working mesh keeps
  `PreviewTerrainMesh` authority across background build applies; builds defer while a stroke is being painted).
- `SculptAnalysisColorizer.cs` - live slope/elevation coloring of the sculpt working mesh: per-dab
  vertex recolor from the freshly patched normals / Z, range pinned at session start (cut/fill not
  colored live).
- `SculptFieldCodec.cs` - persisted `SculptTile` list (base64) ⇄ runtime `SculptDisplacementField`.

## Other
- `GeometryCommandService.cs`, `TerrainInputCommandService.cs`, `TerrainInputCommandAlgorithms.cs`,
  `BlockCommandService.cs`, `LayerTemplateStore.cs`, `RhinoSourceResolver.cs`,
  `RhinoGeometryConversions.cs` - command/geometry helpers. Terrain input commands are intentionally
  document-scoped and selected-only: validation edits selected points/curves, intersection splitting
  edits selected curves, draping samples a selected mesh/surface, and wall creation generates two
  ordinary open polylines. Validation replaces surviving objects in place so their ids and attributes
  remain intact; near-endpoint joins are restricted to curves on the same layer and retain the first
  curve's id/settings while deleting only the consumed curve objects.
- `ProjectBaseCPlaneService.cs` - reversible project-local/real-world transform storage and ModelSpace
  orientation; PageSpace layout content is left unchanged. Reorientation post-composes the new local inverse.
  The modern plane is validated as a horizontal XY-only frame; confirmed Python `FOTM` migrations invert
  their old world-to-local convention, while confirmed legacy C# `Georef` planes retain local-to-world.
  Legacy planes are never deleted by save/clear.
- `GeoreferenceImportPlanner.cs` - complete before/after object-table differencing so Paste/File import
  remaps all newly added ModelSpace objects rather than only Rhino's selected subset; failed remaps remove
  every newly added object and restore the prior selection.
- `RasterGeoreference.cs` / `GeoTiffMetadataReader.cs` / `GeoTiffLinearUnitReader.cs` - dependency-free
  affine raster placement from embedded GeoTIFF model tags or full six-value world files. Projected EPSG
  linear-unit keys are converted into document units; unlabelled rasters prompt for source units with
  document units as the default. CRS reprojection is intentionally out of scope.
- `LandXmlSurfaceService.cs` - imports LandXML TIN point surfaces into managed terrain sources and
  exports completed Rhino meshes through the Core LandXML codec.
- `CommandScriptRunner.cs` - replacement-aware batch transforms with locked-layer preflight and inverse
  rollback if an unexpected object transformation fails.
- `TerrainCoreCaseRecorder.cs` / `TerrainCoreCaseTestExporter.cs` / `TerrainCaseBundleExporter.cs` -
  the "Copy Case" repro-bundle exporters (generate the `*_CopiedCase.cs` Core tests).
