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

## Definition hierarchies (JSON-polymorphic base → subtypes)
- **Modifiers** — polymorphism is registry-driven (`Services/TerrainJsonTypeResolver` reads each
  descriptor's `Kind`), **not** `[JsonDerivedType]` attributes. `ModifierDefinition` →
  `GeometryInputModifierDefinition`
  (`TriangulateModifierDefinition`, `AddGeometryModifierDefinition`; carry `TinMesh/Points/Breaklines/
  Contours/Boundary`; `TinMesh` preserves imported face topology and takes precedence over other sources),
  `GradePadModifierDefinition`, `GradePathModifierDefinition`, `RemeshModifierDefinition`,
  `SmoothModifierDefinition`, `MeshAreasModifierDefinition`, `MeshCollageModifierDefinition`,
  `RetainingWallModifierDefinition`, `InSituStairModifierDefinition`, `SculptModifierDefinition`.
  `TriangulateModifierDefinition.ContourMode` persists the Auto / Constrained / Vertices-only choice;
  Auto switches dense contour sets to point samples while breaklines and Boundary remain constrained.
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
- **Analyses** — `AnalysisDefinition` → `Slope`, `Elevation`, `Contour`, `CutFill`, `Earthwork`,
  `WaterflowAnalysisDefinition`, section/label analyses, etc. Waterflow stores point sources and
  traces generated downhill curves on the final terrain. Slope/Elevation/CutFill carry `AutoColorRange`
  plus `RangeLow`/`RangeHigh`, `ColorMode` and `ColorInterval`; when auto-fit is on the stored bounds are
  ignored and Core's `AnalysisRange` fits the distribution instead. All section analyses persist optional
  comparison terrain ids, a `CutFillReference` source set (any mesh/surface treated as existing ground —
  preferred over the older reference terrain id), and hatch pattern/scale/rotation for the generated
  cut and fill fills; the owning terrain remains the implicit proposed profile. `CutColorArgb`,
  `FillColorArgb` and `CutFillOpacityPercent` are retained for document round-tripping only — cut/fill
  appearance comes from the `SectionsCutFillCut` / `SectionsCutFillFill` roles.
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
