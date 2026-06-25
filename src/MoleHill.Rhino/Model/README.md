# MoleHill.Rhino/Model

The serializable terrain definition — what's saved as JSON in the .3dm and drives every build. Plain
data (no Rhino API beyond geometry refs). `TerrainSerializer` (in `Services/`) round-trips these with
`System.Text.Json` polymorphism + `SchemaVersion` migrations.

- `TerrainDefinition.cs` — the root: name, flags, `GlobalTolerance`, and the ordered lists of
  `Modifiers`, `Analyses`, `Objects`, `Markers`, plus baked-output bookkeeping.
- `SourceReferenceSet.cs` — the universal input selector (object ids + layer paths); used by every
  definition that reads doc geometry.

## Definition hierarchies (JSON-polymorphic base → subtypes)
- **Modifiers** — polymorphism is registry-driven (`Services/TerrainJsonTypeResolver` reads each
  descriptor's `Kind`), **not** `[JsonDerivedType]` attributes. `ModifierDefinition` →
  `GeometryInputModifierDefinition`
  (`TriangulateModifierDefinition`, `AddGeometryModifierDefinition`; carry `Points/Breaklines/Contours/
  Boundary`), `GradePadModifierDefinition`, `GradePathModifierDefinition`, `RemeshModifierDefinition`,
  `SmoothModifierDefinition`, `MeshAreasModifierDefinition`, `MeshCollageModifierDefinition`,
  `RetainingWallModifierDefinition`, `InSituStairModifierDefinition`.
- **Analyses** — `AnalysisDefinition` → `Slope`, `Elevation`, `Contour`, `CutFill`, `Earthwork`,
  section/label analyses, etc.
- **Objects** — `TerrainObjectDefinition` → `LowestPointObjectDefinition`,
  `SurfaceOrientedObjectDefinition`, `ScatterObjectDefinition` (+ `ScatterBlockEntry`,
  `ScatterPreviewMode`; reuses Core's `ScatterPattern`/`ScatterDensityMode`).
- **Markers** — `MarkerDefinition` → `ElevationMarkerDefinition`, `SlopeMarkerDefinition`.

**To add a modifier/object/marker/analysis type:** create the subtype here (no `[JsonDerivedType]` — JSON
polymorphism for all four families is registry-driven via `Services/TerrainJsonTypeResolver`), then add the
matching descriptor in `Registry/` (see `Registry/README.md`). The descriptor supplies the JSON
discriminator, factory, menu entry, and card chrome. Modifiers also get their build step + schema card from
the descriptor; objects/markers/analyses still have bespoke card bodies (and analyses keep their
`TerrainBuildService.Analysis.cs` build stage).
