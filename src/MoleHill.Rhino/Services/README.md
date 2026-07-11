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
- Reference-comparison analyses (Cut / Fill and Earthwork) share one centroid-delta pass per
  reference/boundary fingerprint. The common 2.5D reference lookup uses Core `MeshHeightProjector`;
  Rhino mesh-line projection is kept only for overlapping/near-vertical reference regions and records a
  diagnostic when used.
- Large final Grade Path stages with persistent hard constraints ask Core to try split-keep before the
  explicit carve/weld tier; smaller and unconstrained paths remain explicit-first.
- `TerrainBuildSnapshot.cs` / `TerrainBuildSnapshotBuilder.cs` / `TerrainBuildSnapshotResolver.cs` -
  resolve doc geometry (points/curves/blocks/layers) into the immutable build snapshot the service reads.
- `TerrainBuildResult.cs` - outputs (meshes, generated-object lists, diagnostics, timings).
- Smooth Breaklines can select curves already used by earlier Grade Path modifiers; the Smooth stage
  expands those selected centerlines into local road center/left/right breaklines without persisting
  them as global hard constraints.

## State, preview, bake
- `TerrainController.cs` + `TerrainController.*.cs` partials - owns document state (JSON in the .3dm)
  and the terrain command surface; split by concern: `.Build` (scheduling + background-build lifecycle),
  `.Output` (output sync + bake + attributes + owned-object lifecycle), `.Events` (Rhino doc events,
  idle, source sync), `.Display` (display state, placement sync, materials). The root file keeps CRUD
  commands, state/save/undo, sources/selection, and contour helpers.
- `TerrainJsonTypeResolver.cs` - registry-driven `ModifierDefinition` JSON polymorphism (replaces the
  hand-maintained `[JsonDerivedType]` list); wired into `TerrainSerializer.SharedOptions`.
- `TerrainDisplayConduit.cs` / `TerrainDisplayState.cs` - transient viewport preview of generated
  objects (no doc objects until bake).
- `TerrainRuntimeCache.cs` - per-terrain runtime cache (stage entries, TinEngine, display state, cloner).
  Worker caches shallow-copy stage mesh outputs; the controller defers disposal of displaced main-cache
  meshes until retired worker tasks have drained.
- `GeneratedRhinoObject.cs` - a previewable/bakeable output (geometry or block instance).
- `SculptSessionController.cs` - the interactive sculpt session: GetPoint loop (drag = stroke,
  Enter/Esc = done), brush dabs onto a working mesh via `Core/Sculpting/SculptBrushEngine`, incremental
  normal patching, per-stroke field commit + stroke undo stack, and F/Shift+F adjust modes. DynTopo is
  disabled and hidden for now. `TerrainController.Sculpt.cs` holds the display lock (working mesh keeps
  `PreviewTerrainMesh` authority across background build applies; builds defer while a stroke is being painted).
- `SculptAnalysisColorizer.cs` - live slope/elevation coloring of the sculpt working mesh: per-dab
  vertex recolor from the freshly patched normals / Z, range pinned at session start (cut/fill not
  colored live).
- `SculptFieldCodec.cs` - persisted `SculptTile` list (base64) ⇄ runtime `SculptDisplacementField`.

## Other
- `GeometryCommandService.cs`, `BlockCommandService.cs`, `LayerTemplateStore.cs`,
  `RhinoSourceResolver.cs`, `RhinoGeometryConversions.cs` - command/geometry helpers.
- `TerrainCoreCaseRecorder.cs` / `TerrainCoreCaseTestExporter.cs` / `TerrainCaseBundleExporter.cs` -
  the "Copy Case" repro-bundle exporters (generate the `*_CopiedCase.cs` Core tests).
