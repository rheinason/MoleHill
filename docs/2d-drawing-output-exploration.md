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
3. **Plot weights are hardcoded in code.** `SectionOutputLayers.GetPlotWeight` returns 0.50 / 0.13 / 0.18
   from a `switch`, while `LayerTemplateStore` already carries `PlotWeight` + `PrintColorArgb` per layer for
   the layers it knows about. The section sub-layers (`::Grid`, `::Ticks`, `::Labels`, `::CutFill::*`) are
   invented at runtime and never reach the template.
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
- **Move plot weights out of code into the layer template.** Extend `LayerTemplateStore` to cover the
  section sub-layers that `SectionOutputLayers` currently invents, and reduce `GetPlotWeight` to a fallback
  for layers the template doesn't define.
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
