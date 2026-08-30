# 2D drawing output — exploration

Goal: make MoleHill output sheet-ready. **Position: Rhino already owns styling and sheets. MoleHill's job
is to emit output that Rhino's existing machinery can act on, plus the few drawing objects Rhino has no
concept of.** No page generator, no MoleHill styling system.

## 1. What Rhino already provides (verified against RhinoCommon 8.9.24194)

| Need | Rhino API | Verdict |
|---|---|---|
| Per-view graphic overrides (the "view template" / VW class-override idea) | `Layer.SetPerViewportColor`, `SetPerViewportPlotColor`, **`SetPerViewportPlotWeight`**, `SetPerViewportVisible`, `PerViewportIsVisibleInNewDetails` | Complete. Two details of the same model can look completely different. |
| Annotative sizing (author once, correct at any scale) | `RhinoDoc.LayoutSpaceAnnotationScalingEnabled`, `ModelSpaceAnnotationScalingEnabled`, `ModelSpaceTextScale`, `ModelSpaceHatchScale`, `DimensionStyle.DimensionScale` | Complete. |
| Sheets, details, locked scale | `RhinoPageView.AddDetailView`, `DetailView.SetScale`, `PageToModelRatio`, `IsProjectionLocked` | Complete. |
| Section fills / cut appearance | Rhino 8 `SectionStyle`: `BackgroundFillMode/Color/PrintColor`, `BoundaryWidthScale`, `BoundaryColor/PrintColor`, `HatchIndex/HatchScale/HatchRotationRadians` | Complete for clipping-plane sections. |
| Paper-scaled linetypes | `Linetype.Width`, `WidthUnits`, `AlwaysModelDistances` | Complete. |
| Print width / print colour per layer | layer table; MoleHill's `LayerTemplateStore` already writes both | Complete. |

Conclusion: **every subsystem the earlier draft proposed building already ships in Rhino.** Building a
graphic-preset system, a paper-metrics unit, or a page generator would be re-implementing all of the above,
worse.

## 2. Why output still isn't sheet-ready

MoleHill currently bakes *appearance* into geometry, which is precisely what puts it out of reach of the
table above.

1. **Text is not style-bound.** `SectionLayoutHelper.BuildLabel` sets `TextHeight` explicitly, and
   `TerrainController.Output.cs:146` falls back to whatever the *current* dimension style happens to be.
   Annotation scaling has nothing to act on, and two documents produce different-looking output.
2. **Cut/fill section regions are transparent meshes** (`TerrainAnalysisAnnotationBuilder.cs:776`), coloured
   by `ApplyOpacity`. A shaded mesh is a rendering artefact, not a drawing element — it does not print
   sensibly and ignores `ModelSpaceHatchScale`.
3. ~~**Plot weights are hardcoded in code.**~~ *(Done.)* `SectionOutputLayers.GetPlotWeight` returned
   0.50 / 0.13 / 0.18 from a `switch` while `LayerTemplateStore` carried its own `PlotWeight` +
   `PrintColorArgb` per layer, and the section sub-layers were invented at runtime and never reached the
   template. Both tables are gone: `Registry/LayerRoleRegistry` is the single declaration and the shipped
   template is generated from it. See "Output layer roles" in `architecture.md`.
4. **Contours are one undifferentiated layer.** No major/minor split, so per-layer print width cannot express
   the single most important convention in a terrain drawing. No contour labels.

Fixing these four is most of the practical value, and none of them is a new subsystem.

## 3. What Rhino genuinely cannot do — MoleHill's actual territory

1. **Profile / section views.** Rhino has no concept of a station–elevation view: flattening, vertical
   exaggeration, station axis, elevation grid, data bands. A clipping plane plus `SectionStyle` gives a
   *cut appearance*, not a profile view. MoleHill already has ~80% of this.
2. **Contour major/minor treatment and contour labels** placed in a gap in the line.
3. **Legends and schedules** — slope legend, cut/fill legend, zone area table, earthwork volume table.
   Rhino has no schedule concept at all; MoleHill already computes every number
   (`ZoneAnalysisSummary`, earthworks summary).
4. **Terrain-specific annotation** — spot elevations, slope arrows, grade-between-points labels. Present,
   but currently as ad-hoc text rather than style-bound annotation.

## 4. Proposal

### Move 1 — print-correct output (near-term, no new concepts)

Make every generated object a well-behaved Rhino document object:

- **Bind text to named dimension styles** ("MoleHill Annotation", "MoleHill Section Label"), created on
  demand. Stop setting `TextHeight` per object; stop inheriting the current style. Annotation scaling then
  sizes everything per detail for free. Where a leader is appropriate (spot elevations, grade labels), emit
  a `Leader` rather than free text.
- **Cut/fill regions become `Hatch` objects** with a pattern index, not transparent meshes. Prints
  correctly, honours `ModelSpaceHatchScale`, and can share a hatch pattern with Rhino 8 `SectionStyle` so a
  clipped 3D view and a MoleHill section read the same.
- **Split contours onto major/minor sub-layers** so existing per-layer print width and linetype do the work.
- ~~**Move plot weights out of code into the layer template.**~~ *(Done — went further.)* Rather than two
  tables with one deferring to the other, output now names a **role** and the template binds roles to
  layers, so routing and appearance have one home. See "Output layer roles" in `architecture.md`.
- **Ship a MoleHill document template (.3dm)** carrying the layer tree, dimension styles, hatch patterns and
  linetypes. Assets, not code — and directly editable by the user with Rhino's own tools.

After this, the sheet workflow is entirely Rhino's: make a layout, drop a detail, set the scale, print. An
"existing vs proposed" pair of details is per-detail layer overrides — the Vectorworks trick, natively, with
no MoleHill involvement.

### Move 2 — keep and finish the section view

It stays, because Rhino has no equivalent. Two improvements:
- **Placement by picking a point**, not by typing `InsertionOriginX/Y/Z` and axis vectors into a card.
- **Data bands** under the profile (existing elevation, proposed elevation, cut/fill, station).
  `SectionProfileComparison` already produces the values; a band is text at station positions. This is the
  one Civil 3D feature landscape architects genuinely miss.

### Move 3 — legends and tables

Text plus hatch swatches on the annotation layer, generated from the analysis definitions that produced the
colours, regenerated by the normal pipeline like every other output. Cheap, and nothing else in the stack
does it well.

## 5. Explicitly not doing

- **No page/sheet generator.** Layouts are Rhino's. A clumsy one is worse than none.
- **No MoleHill styling system** — no graphic presets, no paper-metrics unit, no plot style tables.
  Layers, dimension styles, linetypes and hatch patterns are the styling system.
- **No sheet series** (one alignment → N numbered cross-section sheets). If it ever comes up, revisit then,
  and as a script over Rhino's layout API rather than a subsystem.

## 6. Sequencing

1. Named dimension styles + style-bound text/leaders. Self-contained; immediately fixes scale correctness.
2. Hatch-based cut/fill regions.
3. Layer template extension + contour major/minor; retire hardcoded plot weights.
4. MoleHill document template asset.
5. Section placement by pick; data bands.
6. Legends and tables.

Steps 1–4 are the printable-output fix and carry no API risk.

## 7. Marker scaling — tested findings (Rhino 8, live document)

Tested empirically rather than from documentation. Results:

- **`ArrowType.UserBlock` works.** A `DimensionStyle` with `LeaderArrowType = UserBlock` and
  `LeaderArrowBlockId` set to an instance-definition id round-trips through the dim style table and draws
  the block as the leader arrowhead at `LeaderArrowLength`. A spot-elevation marker can therefore be built
  entirely from native Rhino annotation, with the symbol artwork still authored as an ordinary block.
- **Leader text cannot read block attributes.** `%<UserText("block","VALUE",…)>%` on a leader renders as
  `####` (unresolved field) — a leader has no parent block instance to resolve against. Self-scope
  `%<UserText("VALUE")>%` reading a user string on the leader object itself also renders `####`.
  Block-attribute text *inside a block definition* resolves correctly, which is what MoleHill does today.
- **Duplicate block definitions per scale are unnecessary.** Block instances carry their own transform
  (`TerrainAnalysisAnnotationBuilder.cs:1190`, `TerrainBuildService.Objects.cs:65`), and the marker blocks
  are authored with internal text at `TextHeight = 1.0` (`GeneratedBlockCatalog.CreateDisplayText`), so
  instance scale *is* the desired model text height.
- **Not verified:** whether a UserBlock arrowhead rescales per detail under
  `LayoutSpaceAnnotationScalingEnabled`. Detail viewports rendered empty in the test instance (an
  environment artefact of the spawned Rhino, not a behaviour finding). The mechanism strongly implies it
  does — arrowhead size is `LeaderArrowLength` x effective `DimensionScale`, which is exactly what
  annotation scaling adjusts — but it should be confirmed in a normal Rhino session before being relied on.

### Consequence

The two coherent options are sharper than first framed:

| | Block instances (today, improved) | Leaders with UserBlock arrowheads |
|---|---|---|
| Symbol artwork | block definition | block definition (as arrowhead) |
| Value | block attribute, `UserText("block",…)` | **literal text written at build time** |
| Per-detail auto-scale | no | yes (pending confirmation) |
| User can override one label | yes (edit the attribute) | no (Detach) |
| Attribute extraction / scheduling | yes | no |

Because MoleHill regenerates all output every build, literal text is not a correctness loss — the value is
always rewritten. The real losses are attribute extraction and per-instance manual override.

### How the symbol artwork scales (mechanism, tested)

The arrowhead block is **normalized and drawn at `LeaderArrowLength`**, not at its authored size.
Verified: two identical leaders differing only in `LeaderArrowLength` (2.5 vs 10) drew the same symbol
block at 2.5x and 10x, with text height unchanged.

`LeaderArrowLength` is a dimension-style length, and every dimension-style length — `TextHeight`,
`LeaderArrowLength`, `LeaderLandingLength`, `TextGap` — is multiplied by the style's effective
`DimensionScale`. Verified: setting `DimensionScale = 3` on one style tripled **both its text and its
symbol block together**, while an untouched control style was unaffected.

So text and symbol scale in lockstep off a single number. That number is what layout annotation scaling
drives from the detail's page-to-model ratio. Consequences for authoring:

- Author symbol blocks at **unit size** (Rhino normalizes them anyway) and control size through
  `LeaderArrowLength`, expressed as a multiple of `TextHeight`.
- **Unverified link:** that `LayoutSpaceAnnotationScalingEnabled` actually sets the effective
  `DimensionScale` per detail. Detail viewports would not render in the spawned test instance, and the
  model-space analogue (`ModelSpaceTextScale = 3` with `ModelSpaceAnnotationScalingEnabled = true`) had
  **no effect** — implying a per-style "scale source" flag that selects document-vs-style scale.
  `DimensionStyle.Field.DimscaleSource` exists in the field enum but no corresponding property is exposed
  on `DimensionStyle`. Worth resolving in a normal Rhino session before this route is committed to.
- **The arrowhead rotates to follow the leader direction.** Correct and desirable for slope arrows. For a
  spot-elevation crosshair it means the symbol tilts with the leader, so either author that symbol
  rotationally symmetric (plain circle or dot) or keep elevation markers as block instances.

### Decision and implementation (defect 1)

**Markers stay block instances, sized from the annotation style.** Leaders were rejected because the
user's block would only be the arrowhead symbol — with block instances the user authors the *whole* marker
(symbol, value text, prefix/suffix, their arrangement), and `BlockDefinitionName` already lets them supply
their own. Custom Rhino objects were rejected because they only render correctly while MoleHill is loaded.

Implemented:

- `TerrainDefinition.AnnotationStyleName` — the Rhino dimension style generated annotation binds to.
  Blank resolves to `"MoleHill Annotation"`, created on demand. Sizes/fonts/masks are edited in Rhino's own
  Annotation Styles editor; MoleHill has no styling UI of its own.
- `Services/AnnotationStyleService.cs` — the single boundary. `Capture` resolves the style on the document
  thread into an `AnnotationStyleSnapshot` carried on `TerrainBuildSnapshot` (the background build has no
  document access, same pattern as `BlockDefinitionBounds`). It ensures the style exists up front so the
  viewport preview and the baked objects are sized identically from the first build.
- Generated text no longer carries a hardcoded height: section labels, contour labels, and grade callouts
  resolve through the captured style. Baked `TextEntity` output is stamped with the style id, so it tracks
  later edits to the style.
- Marker block instance scale is derived from the style's effective text height; the stored `BlockScale`
  becomes a relative multiplier (1.0 = symbol text matches label text). No duplicate block definitions.
- `FollowsAnnotationStyle` on `AnalysisDefinition` and `MarkerDefinition`, default true. Schema bumped to
  27; documents saved earlier are migrated to false so existing drawings keep their exact sizes.

Not yet done: a panel row to pick a *different* style. The default path needs no UI — every terrain uses
"MoleHill Annotation" and the user edits that style in Rhino.

## 8. In-Rhino verification (Rhino 8, live document)

The implementation was exercised against a real document by loading the built `.rhp` as an assembly and
invoking the services directly. Findings:

**Confirmed working**
- `AnnotationStyleService.EnsureStyle` creates `"MoleHill Annotation"` once and is idempotent on repeat
  calls. `Capture` returns the style id and an effective height of `TextHeight * DimensionScale`.
- **Stamping `DimensionStyleId` overrides the authored `TextHeight` entirely.** Text authored at 7.0 with
  the style attached drew at the style's height, and editing the style afterwards moved every baked object
  with it. This is the behaviour the whole annotation-style change depends on.
- Hatch patterns are created on demand; **a fresh Rhino document contains none at all**, so the
  ensure-on-demand step was load-bearing rather than defensive. An unknown name resolves to Solid.
- Layer print widths seed correctly (`Major` 0.35, `Minor` 0.13) and the user's chosen root layer is left
  at Rhino's default, untouched.
- Contour majorness over z = 0..10 produced `M....M....M`, and the routed sublayer paths match the seeded
  widths.

**Two defects found and fixed**
1. **`HatchScale = 1.0` printed as solid black.** Rhino's Hatch1 spaces lines 0.125 model units apart, so
   on a 60 m section scale 1 draws ~8 lines per metre — an unreadable smear. A pattern's native spacing is
   arbitrary, so no fixed default can be right. `HatchPatternSnapshot` now captures each pattern's own line
   offset and `ResolveScale` derives a scale targeting a spacing of 0.8 x annotation text height
   (2 mm on paper at 1:100 for 0.25 m text). `HatchScale = 0` means derive; a positive value is honoured.
   Different patterns converge on the same drawn spacing.
2. **Only the first hatch of a region was kept.** `Hatch.Create` can return several hatches for one
   boundary; the code took `hatches[0]`, silently dropping the rest. It now emits all of them, with
   `emitted` counting objects while cut/fill counts stay per comparison region.

**Not verified**
- Whether `LayoutSpaceAnnotationScalingEnabled` drives `DimensionScale` per detail. Detail viewports would
  not render in the spawned test instance, and print-width differences are sub-pixel at viewport zoom, so
  layer weights were verified numerically instead. Both are Rhino's own pipeline rather than MoleHill code.

**Gotcha:** loading the `.rhp` for probing locks it, causing the documented MSB3021/MSB3027 copy failure on
the next build. Copy it to a temp path before `Assembly.LoadFrom`.
