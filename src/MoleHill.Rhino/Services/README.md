# MoleHill.Rhino/Services

The Rhino-side engine: builds terrain from the saved definition, manages document state, previews, and
bakes. Rhino API lives here; reusable math is in `MoleHill.Core`. See `docs/architecture.md`.

## Build pipeline
- `TerrainBuildService.cs` + `TerrainBuildService.*.cs` partials — the staged build orchestrator. Each
  partial owns a stage: `.Tin`, `.MeshConstraints`, `.Grading`, `.Zones`, `.Analysis`, `.Objects`,
  `.Scatter`; plus `.Cache`, `.Fingerprints`, `.Types`. Most stages are fingerprint-cached
  (`StageCacheEntry`); the analysis/zones/objects/scatter block runs only in `TerrainBuildMode.Final`.
- `TerrainBuildSnapshot.cs` / `TerrainBuildSnapshotBuilder.cs` / `TerrainBuildSnapshotResolver.cs` —
  resolve doc geometry (points/curves/blocks/layers) into the immutable build snapshot the service reads.
- `TerrainBuildResult.cs` — outputs (meshes, generated-object lists, diagnostics, timings).

## State, preview, bake
- `TerrainController.cs` + `TerrainController.*.cs` partials — owns document state (JSON in the .3dm)
  and the terrain command surface; split by concern: `.Build` (scheduling + background-build lifecycle),
  `.Output` (output sync + bake + attributes + owned-object lifecycle), `.Events` (Rhino doc events,
  idle, source sync), `.Display` (display state, placement sync, materials). The root file keeps CRUD
  commands, state/save/undo, sources/selection, and contour helpers.
- `TerrainJsonTypeResolver.cs` — registry-driven `ModifierDefinition` JSON polymorphism (replaces the
  hand-maintained `[JsonDerivedType]` list); wired into `TerrainSerializer.SharedOptions`.
- `TerrainDisplayConduit.cs` / `TerrainDisplayState.cs` — transient viewport preview of generated
  objects (no doc objects until bake).
- `TerrainRuntimeCache.cs` — per-terrain runtime cache (stage entries, TinEngine, display state, cloner).
- `GeneratedRhinoObject.cs` — a previewable/bakeable output (geometry or block instance).

## Other
- `GeometryCommandService.cs`, `BlockCommandService.cs`, `LayerTemplateStore.cs`,
  `RhinoSourceResolver.cs`, `RhinoGeometryConversions.cs` — command/geometry helpers.
- `TerrainCoreCaseRecorder.cs` / `TerrainCoreCaseTestExporter.cs` / `TerrainCaseBundleExporter.cs` —
  the "Copy Case" repro-bundle exporters (generate the `*_CopiedCase.cs` Core tests).
