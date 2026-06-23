# MoleHill.Rhino/Model

The serializable terrain definition — what's saved as JSON in the .3dm and drives every build. Plain
data (no Rhino API beyond geometry refs). `TerrainSerializer` (in `Services/`) round-trips these with
`System.Text.Json` polymorphism + `SchemaVersion` migrations.

- `TerrainDefinition.cs` — the root: name, flags, `GlobalTolerance`, and the ordered lists of
  `Modifiers`, `Analyses`, `Objects`, `Markers`, plus baked-output bookkeeping.
- `SourceReferenceSet.cs` — the universal input selector (object ids + layer paths); used by every
  definition that reads doc geometry.

## Definition hierarchies (JSON-polymorphic base → subtypes)
- **Modifiers** — `ModifierDefinition` → `GeometryInputModifierDefinition`
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

**To add a new modifier/analysis/object/marker type:** create the subtype here, register its
`[JsonDerivedType]` on the base, handle it in the matching `TerrainBuildService.*.cs` stage and the
`MoleHillPanel` card switch, and add its add-menu entry in the panel.
