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

## 2D drawing output

Rhino owns styling and sheets. These services exist only to make generated output something Rhino's
layer table, dimension styles, and layouts can act on. See `docs/architecture.md`.

- `AnnotationStyleService.cs` - the boundary to Rhino's dimension-style table. Resolves the terrain's
  named style on the document thread into an `AnnotationStyleSnapshot` on the build snapshot (the
  background build has no document access), creating it if absent so preview and bake match from the first
  build. Generated text binds to the style instead of carrying a hardcoded height; marker block instances
  scale from the style's effective text height (`TextHeight * DimensionScale`).
- `HatchPatternService.cs` - the same boundary for hatch patterns, whose indices are document-scoped.
  Creates Rhino's built-in patterns on demand, reuses a user-authored pattern of the same name untouched,
  and degrades an unresolvable pattern to Solid so a fill never silently disappears.
- `LayerRoleResolver.cs` - `LayerAppearance` and `LayerRoleTable`: one layer template flattened to
  role -> (path, appearance). Built on the document thread and carried on `TerrainBuildSnapshot`, since
  the background build has no document access. `Path` is non-null for every role by construction.
- `SlopeUnitPreference.cs` - which unit slope *inputs* are shown and typed in (percent / promille /
  ratio / degrees), shared by the panel cards and every slope-taking command. A per-user display
  preference: persisted through provider/writer delegates the plug-in points at its own settings, so the
  Registry and Services sources still compile in the test project with no live plug-in, and changing it
  never touches a document. Distinct from the per-item `Unit` on slope analyses and annotations, which is
  part of the drawing. Parsing and formatting themselves live in `MoleHill.Core.Analysis.SlopeInput`.
- `LayerRoleService.cs` - resolves and caches the table a terrain uses, seeds a document with its own
  embedded copy of the template, and reports (never merges) divergence from the machine-local one.
- `LayerCreationService.cs` - the only place layers are created. Seeds appearance at creation and leaves
  existing layers alone, so Layers-panel edits and per-detail overrides survive rebuilds. Styles the leaf
  only; an intermediate layer is seeded from its own entry, not a descendant's.
- `LayerTemplateDocumentStore.cs` - the document's copy of the templates it uses, so a .3dm renders the
  same wherever it is opened.
- `LayerRoutingMigration.cs` - carries a pre-schema-30 document's customised output layers into a
  template of its own rather than retargeting anything.
- `GeneratedRhinoObject.AppearanceSource` decides whether colour/plot weight are stamped on the object or
  left ByLayer, and comes from the role descriptor rather than being set per producer. Drawing output is
  `Layer`; output whose colour is data stays `Object`.

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
  commands, state/save/undo, sources/selection, and contour helpers. User-authored changes use Rhino's
  native Undo/Redo stack: the controller stores serialized before-state snapshots, joins an existing
  command record when one is active, and coalesces live panel gestures into one record. Undo restoration
  cancels stale work, clears caches, persists the restored JSON, and schedules fresh live builds.
- `TerrainUndoSnapshot.cs` - the private-state payload carried by Rhino custom undo events. Its state
  equality ignores the action label so no-op edits do not create history entries, while the label follows
  the snapshot through Undo and Redo.
- `TerrainJsonTypeResolver.cs` - registry-driven `ModifierDefinition` JSON polymorphism (replaces the
  hand-maintained `[JsonDerivedType]` list); wired into `TerrainSerializer.SharedOptions`.
- `TerrainDisplayConduit.cs` / `TerrainDisplayState.cs` - transient viewport preview of generated
  objects and runtime overlays (no doc objects until bake). Overlay drawing has per-terrain budgets and
  severity ordering. `TerrainDisplayState.RenderHash` is the change stamp the render mesh provider
  hands the RDK cache.
- `TerrainRenderMeshProvider.cs` - publishes terrain preview geometry (terrain mesh, zone/auxiliary
  meshes, renderable marker blocks, scatter instances) to render engines via the RDK custom render mesh system, without creating
  document objects. Advertises each `TerrainDefinition.TerrainId` as a non-object id. Must stay
  `public` with a public parameterless constructor so `RenderMeshProvider.RegisterProviders` (called
  from `MoleHillRhinoPlugin.OnLoad`) discovers it. **Support is opt-in per renderer:** verified against
  Rhino's `ChangeQueue` pipeline (Raytraced/Cycles); V-Ray historically yes; Enscape has no known
  support, so Bake remains the fallback there. Rhino render materials are retained from explicit
  assignments, layers, and block members where available. Ignores scatter `PreviewCap`/`PreviewMode`
  (viewport budgets, not render budgets) and skips text/dots/curves, which have no render mesh.
- `TerrainDisplayColors.cs` - shared colour/transparency resolution for generated output. Used by both
  the conduit (`DisplayMaterial`) and the render mesh provider (`RenderMaterial`) so a render matches
  the viewport preview.
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
- Section Cut, Cross-Sections, and Section Along Curve snapshot selected terrains' completed final
  meshes and lay their profiles into the same section cell. Existing ground for cut/fill comes from the
  analysis's `CutFillReference` source set — any Rhino mesh or surface, cut by the same
  `SectionCutGeometry` that cut the terrain so both profiles share one station parametrization — or,
  failing that, from a selected comparison terrain. `SectionProfileComparison` splits proposed versus
  reference profiles at crossings and gaps, normalizing every edge to low-station-first because the
  slicer can return a run in descending station order. Generated cut/fill hatches preview and bake under
  `Sections::CutFill::Cut` and `Sections::CutFill::Fill` sublayers — paths that match the office layer
  template, so the fills inherit its colours and print widths — while dependent terrains rebuild on
  reference changes.
- The layer command surface creates the selected terrain's configured Terrain, Auxiliary, and Annotation
  output layers directly; it does not depend on highlighted source layers.
- `SculptSessionController.cs` - the interactive sculpt session: GetPoint loop (drag = stroke,
  Enter/Esc = done), brush dabs onto a working mesh via `Core/Sculpting/SculptBrushEngine`, incremental
  normal patching, shared constraint masking, per-stroke field commit + stroke undo stack, and
  F/Shift+F adjust modes. DynTopo is
  disabled and hidden for now. `TerrainController.Sculpt.cs` holds the display lock (working mesh keeps
  `PreviewTerrainMesh` authority across background build applies; builds defer while a stroke is being painted).
- `SculptAnalysisColorizer.cs` - live slope/elevation/cut-fill coloring of the sculpt working mesh: per-dab
  vertex recolor from the freshly patched normals / Z / reference projection, with the range pinned at
  session start. Aspect deliberately has none: its colours are per-face directions and a stroke changes
  the direction of every face it touches, so there is nothing to pin. An active aspect card means the
  sculpt stage draws in the terrain's own colour, exactly as it does with no analysis at all.
- `DocumentNorth.cs` - the document's north, in one place: `doc.Lights.Sun.North`, the angle
  `mhSetSunNorth` writes, as an azimuth CCW from +X. The aspect analysis reads it through here from both
  the preview path (which holds a `RhinoDoc`) and the build path (via `TerrainBuildSnapshot.
  NorthAzimuthDegrees`, captured on the document thread), so an aspect map cannot disagree with the sun.
  North is not on the definition, so `ComputeAnalysisFingerprint` adds it explicitly for an aspect
  analysis - otherwise rotating north leaves cached bearings on screen.
- Reference comparison caches separate current-mesh statistics from reference projection contexts.
  Multiple zone pieces therefore share the expensive reference index while retaining independent
  cut/fill volumes and per-call projection diagnostics.
- `SculptFieldCodec.cs` - persisted `SculptTile` list (base64) ⇄ runtime `SculptDisplacementField`.

## Layer templates
- `LayerTemplateStore.cs` - the machine-local templates (`%APPDATA%\MoleHill\layer-templates.json`), the
  office standard new documents are seeded from. Generates the shipped template from `LayerRoleRegistry`
  rather than restating it, and upgrades a pre-role file by recovering its bindings from the layer paths
  it already has.
- `LayerTemplateCommandService.cs` - `mhApplyLayerTemplate` (create-only) and `mhResetLayerStyles`, which
  re-stamps appearance onto existing layers and asks first, since that discards the user's edits.

## Other
- `GeometryCommandService.cs`, `TerrainInputCommandService.cs`, `TerrainInputCommandAlgorithms.cs`,
  `BlockCommandService.cs`, `RhinoSourceResolver.cs`,
  `RhinoGeometryConversions.cs` - command/geometry helpers. Terrain input commands are intentionally
  document-scoped and selected-only: validation edits selected points/curves, intersection splitting
  edits selected curves, draping samples a selected mesh/surface, and wall creation generates two
  ordinary open polylines. Validation replaces surviving objects in place so their ids and attributes
  remain intact; near-endpoint joins are restricted to curves on the same layer and retain the first
  curve's id/settings while deleting only the consumed curve objects.
  `GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline` is the single plan-offset + vertical-delta
  primitive, shared by `mhOffsetFeature` and `mhCreateWall`'s parallel rail;
  `TryResolveVerticalDelta` turns the command's `Vertical` mode (elevation / percent / degrees /
  1:n ratio) into that delta over the offset distance.
  `TryCreateCurveSectionEdit` is `mhSlopeCurveSection`. Re-grading part of a curve has to move one of
  the picked ends, and the moved end meets curve that did not move: with `Transition` at zero the two
  are bridged by a vertical line, which is the step users saw. A transition length instead eases that
  delta into the adjoining stretch (clamped to it, so the curve's own endpoint never moves), and
  `BlendToTerrain` spends the same length inside the section, ramping the pull up from each pick so
  both stay put. Straight neighbours carry control points only at their ends, so `InsertStationKnots`
  adds them across the band — including its boundaries, or the edit leaks a straight tail over the
  whole remainder instead of dying out where it was told to. `Anchor` chooses which pick keeps its
  elevation, pick order sets the direction the grade runs, and the command reports the *achieved*
  grade, since the anchor and the transition both move the ends. Every mode adds only the options it
  reads and previews through `DynamicDraw`, the same way `mhSlopeCurve` does.
  `MeasureElevationDeviation` + `CurveSlopeDeviation` answer the question both slope commands used to
  leave open. Both re-elevate a curve by moving its existing Greville points — deliberately, because
  that keeps the curve editable rather than replacing it with a dense rebuild — so the result only
  *interpolates* the asked-for grade, exactly at the Greville abscissae. On a degree-1 polyline that is
  exact and the deviation is zero. On a curved or high-degree curve with few control points it is not:
  a 20% grade on a degree-3 four-point S-curve strays 0.43 model units mid-span, and nothing on screen
  says so. Both commands therefore sample the result densely by plan station against the same rule the
  edit applied and report the worst stray and where — but only when it exceeds document tolerance, so
  the exact case stays silent instead of printing a zero users learn to skip. The measurement is opt-in
  per call (`measureDeviation`) and the live preview opts out: under blend-to-terrain every sample is a
  mesh ray, and the preview redraws on each mouse move.
- `CurveReviewService.cs` / `CurveReviewForm.cs` / `CurveReviewAnalysis.cs` / `CurveReviewConduit.cs` /
  `CurveReviewPalette.cs` - the `mhInspectCurve` plan-station inspector. **It reports; it never edits** -
  see `docs/architecture.md` for why the profile-editing stack was removed rather than restyled. The
  resizable document-owned form is a reading order of titled blocks: elevation profile, checks,
  measurements, events, display. Rule thresholds sit behind a "Rule limits" disclosure under Checks, since
  limits are set rarely and checks are read constantly; a failing check row and any event row are click
  targets that zoom the viewport to the occurrence. `CurveReviewRules` supplies Off/Report/Warn results;
  the Rhino-side store persists thresholds per user and stores radius in metres.
  `CurveReviewMetricSeries` (in `CurveReviewPalette.cs`) is the single source of colour: the panel chart
  and the viewport ribbon both build one for the selected metric - grade, elevation, cut/fill or plan
  radius - so they cannot drift. Plan radius is coloured by *tightness*, not raw radius, so a straight
  (infinite) and a true corner (NaN) land at opposite ends of the ramp instead of collapsing every real
  curve into one bucket. An over-limit stretch stays red whatever the metric is set to. The conduit draws
  its whole pass with depth testing and writing off **and** in the `DrawForeground` channel rather than
  `PostDrawObjects`: an inspected curve that grades the terrain lies inside the mesh it generated, and the
  terrain preview is itself a conduit drawing in `PostDrawObjects`, which painted straight over the
  overlay until the channel changed. One exact World-XY projection supplies all plan lengths and parameter
  lookups; endpoint discontinuities are excluded, real plan corners remain distinct from finite radii, and
  adjacent vertical-break samples merge into one PI event.
- `CurveReviewLabeller.cs` - the inspector's `Label` button. Picks points constrained to the inspected
  curve and drops text dots reading any combination of elevation, grade, station and cut/fill, taken from
  the analysis already on screen. Dots go to the `Labels` role's layer via
  `LayerRoleService.EnsureRoleLayer`, so they inherit its print width, and the whole run is one
  undo record. Replaces the removed `mhSlopeCheckAndMark` command. The terrain mesh is peeked (`TerrainController.PeekFinalTerrainMesh`,
  read-only, no copy) and the analysis is rebuilt only when the object serial, terrain mesh, or a limit
  changes, so the 4 Hz refresh timer stays cheap.
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
- `ClassicTiffTagReader.cs` / `GeoTiffMetadataReader.cs` / `GeoTiffElevationReader.cs` - direct classic-
  TIFF placement/GDAL tag parsing plus numeric single-band decoding through LibTiff.Net. The DEM path
  does not depend on GDI image conversion; it preserves integer/floating-point samples, applies
  scale/offset, skips NoData, and rejects RGB imagery. The Triangulate card references a planar Rhino
  surface carrying that GeoTIFF as its bitmap texture; snapshot capture maps pixel centres through the
  surface, so ordinary Rhino transforms control project placement without persistent sampled points.
- `LandXmlSurfaceService.cs` - transactionally imports every LandXML TIN surface, converts units and
  project-base coordinates, and preserves face topology with exact managed mesh sources; export converts
  completed Rhino meshes to metre-based LandXML through the Core codec.
- `CommandScriptRunner.cs` - replacement-aware batch transforms with locked-layer preflight and inverse
  rollback if an unexpected object transformation fails.
- `TerrainCoreCaseRecorder.cs` / `TerrainCoreCaseTestExporter.cs` / `TerrainCaseBundleExporter.cs` -
  the "Copy Case" repro-bundle exporters (generate the `*_CopiedCase.cs` Core tests).
