# MoleHill.Rhino/Model

The serializable terrain definition — what's saved as JSON in the .3dm and drives every build. Plain
data (no Rhino API beyond geometry refs). `TerrainSerializer` (in `Services/`) round-trips these with
`System.Text.Json` polymorphism + `SchemaVersion` migrations.

- `TerrainDefinition.cs` — the root: name, flags, `GlobalTolerance`, and the ordered lists of
  `Modifiers`, `Analyses`, `Objects`, `Markers`, plus baked-output bookkeeping. `PreviewLineWeight`
  multiplies every previewed line width for this terrain; it is a display preference only, never reaches
  baked geometry, and is the one place preview and bake deliberately differ. `LayerTemplateName` picks the
  layer template this terrain routes through, so two terrains can be drawn on separate layers. The
  `Legacy*LayerPath` and `LegacyAnnotationStyleName` fields exist only for the schema 30 migration.
- `SourceReferenceSet.cs` — the universal input selector (object ids + layer paths); used by every
  definition that reads doc geometry. These say what to *read* and are unrelated to output routing, which
  is `LayerRole`.
- `LayerRole.cs` — the closed set of output destinations. `LayerTemplateDefinition` / `LayerTemplateEntry`
  bind roles to real layers and carry their appearance; `EmbeddedLayerTemplateState` is the document's own
  copy of the templates it uses.
- `AspectAnalysisDefinition.cs` — which way the ground faces. Its range is *not* a user choice even though
  the base class carries one: aspect is always the full compass, so `RangeLow`/`RangeHigh` are a full turn
  and the ramp card hides the bounds. Defaults to the cyclic `aspect-wheel` preset in `Constant` mode,
  which reads as eight sectors. `FlatSlopeThresholdDegrees` is a slope like every other in the model —
  stored in degrees, typed in whatever unit the user works in.
- `ReportTableAnnotationDefinition.cs` — the quantity summary drawn into the model. Which sections to
  include, the rules, spacing as multiples of the text height, and an insertion origin (unset places it
  beside the terrain). It carries its own slope `Unit` because that unit is part of the drawing; the CSV
  export follows the per-user `SlopeUnitPreference` instead. It measures nothing — every figure comes from
  the other stages, which is why its build runs last.
- `CutFillAnalysisDefinition.cs` — the signed delta, as colour *and* optionally as drawn lines
  (`ShowDeltaContours` + `DeltaContourInterval`, `ShowBalanceLine`, each with an optional explicit colour
  that falls back to its role's layer). `DrawsDeltaOutput` is the one question the build and the
  fingerprint ask. The sibling `EarthworkAnalysisDefinition` owns the *volumes* off the same
  `ReferenceComparisonAnalysisDefinition` base; nothing should compute the delta twice.

## Definition hierarchies (JSON-polymorphic base → subtypes)
- **Modifiers** — polymorphism is registry-driven (`Services/TerrainJsonTypeResolver` reads each
  descriptor's `Kind`), **not** `[JsonDerivedType]` attributes. `ModifierDefinition` →
  `GeometryInputModifierDefinition`
  (`TriangulateModifierDefinition`, `AddGeometryModifierDefinition`; carry `TinMesh/Points/Breaklines/
  Contours`; `TinMesh` preserves imported face topology and takes precedence over other sources),
  `GradePadModifierDefinition`, `GradePathModifierDefinition`, `RemeshModifierDefinition`,
  `SmoothModifierDefinition`, `MeshAreasModifierDefinition`, `MeshCollageModifierDefinition`,
  `RetainingWallModifierDefinition`, `InSituStairModifierDefinition`, `SculptModifierDefinition`,
  `ProjectToModifierDefinition`. Project To stores one mutually exclusive Rhino-mesh or MoleHill-terrain
  target plus nested boundary loops, Strength, and an inward Feather distance.
  `TriangulateModifierDefinition.ContourMode` persists the Auto / Constrained / Vertices-only choice;
  Auto switches dense contour sets to point samples while breaklines remain constrained. Triangulate alone
  owns the terrain-wide `OuterBoundaries`, `HideBoundaries`, `ShowBoundaries`, and `DataClipBoundaries`
  source sets; Add Geometry contributes no boundary state.
  Triangulate also stores a `DemSurface` source: a planar Rhino surface whose bitmap texture is a numeric
  GeoTIFF. Raster samples are mapped through the live surface at snapshot time, so moving the surface
  controls project placement without generating persistent point objects.
  Grade Path stores centerline sources, the constant width, a `UseVariableWidth` opt-in toggle, and —
  read only while that toggle is on — ordinary-Rhino width-edge sources plus an optional edge-matching
  distance; width-edge elevations are ignored. Turning the toggle off parks the edge references rather
  than clearing them, so the panel card of a plain path shows only Centerlines/Width, and re-enabling
  restores the previous edges. `EnumerateSourceSets` still yields the parked set so stale-object cleanup
  and layer renames keep tracking it.
  Sculpt persists both its raw displacement tiles and a constraint source set; selected closed curves
  protect their interiors, while earlier Grade Path sources resolve to their configured road width.
- **Analyses** — `AnalysisDefinition` → `Slope`, `Elevation`, `CutFill`, `Earthwork`,
  `WaterflowAnalysisDefinition`. Content that **evaluates** the terrain: the result is a measurement, a
  number or a colour on the mesh. Waterflow stores point sources and traces generated downhill curves on
  the final terrain — it emits geometry but is still an analysis, because a traced flow path is a computed
  finding, not a label. Slope/Elevation/CutFill carry `AutoColorRange`
  plus `RangeLow`/`RangeHigh`, `ColorMode` and `ColorInterval`; when auto-fit is on the stored bounds are
  ignored and Core's `AnalysisRange` fits the distribution instead. The colour-ramp members live here and
  nowhere else.
- **Annotations** — `AnnotationDefinition` → `Contour`, the `BlockAttributeAnnotationDefinition` label and
  callout types (spot heights, spot slopes, flow arrows, grade callouts), and the three
  `TerrainSectionAnnotationDefinitionBase` section types. Content that **describes** the terrain: the
  result is drawing. A peer family of `AnalysisDefinition`, not a subclass of it — see
  `docs/architecture.md` → "Analysis vs annotation". Only annotations carry `FollowsAnnotationStyle`.
  All section annotations persist optional
  comparison terrain ids, a `CutFillReference` source set (any mesh/surface treated as existing ground —
  preferred over the older reference terrain id), and hatch pattern/scale/rotation for the generated
  cut and fill fills; the owning terrain remains the implicit proposed profile. `CutColorArgb`,
  `FillColorArgb` and `CutFillOpacityPercent` are retained for document round-tripping only — cut/fill
  appearance comes from the `SectionsCutFillCut` / `SectionsCutFillFill` roles.
- **Objects** — `TerrainObjectDefinition` → `LowestPointObjectDefinition`,
  `SurfaceOrientedObjectDefinition`, `ScatterObjectDefinition` (+ `ScatterBlockEntry`,
  `ScatterPreviewMode`; reuses Core's `ScatterPattern`/`ScatterDensityMode`).
- **Markers** — `MarkerDefinition` → `ElevationMarkerDefinition`, `SlopeMarkerDefinition`.

`ITerrainContentItem` (Id, Label, IsEnabled) is implemented by `AnalysisDefinition` and
`AnnotationDefinition`. It is an interface over identity, not a shared base: it exists purely so
scaffolding that does not care which family it is handling (the build stage runner, fingerprinting) can be
written once. Do not reach for it when the meaning of the content matters.

**To add a modifier/object/marker/analysis/annotation type:** create the subtype here (no `[JsonDerivedType]`
— JSON polymorphism for all five families is registry-driven via `Services/TerrainJsonTypeResolver`), then
add the matching descriptor in `Registry/` (see `Registry/README.md`). The descriptor supplies the JSON
discriminator, factory, menu entry, and card chrome. Modifiers also get their build step + schema card from
the descriptor; objects/markers/analyses/annotations still have bespoke card bodies (and both analyses and
annotations keep their `TerrainBuildService.Analysis.cs` build stage).
