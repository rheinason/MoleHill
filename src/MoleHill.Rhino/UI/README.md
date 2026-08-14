# MoleHill.Rhino/UI

The dockable terrain panel (Eto.Forms) and its dialogs. Composes reusable card/editor primitives - add
fields by composing them, not by copying boilerplate.

- `MoleHillPanel.cs` + `MoleHillPanel.*.cs` partials - the panel, split by concern:
  `.Actions.cs` (terrain-level create/copy/delete/convert/bake/rebuild/reset actions),
  `.Cards.cs` (collapsible "stack card" framework), `.RuntimeDiagnostics.cs` (generic per-card issue
  counts and redraw-only **Show Issues** binding), `.Editors.cs` (form-control vocabulary -
  `CreateSourceEditor`/`CreateNumericEditor`/`CreateDropDownEditor`/...), `.Schema.cs` (schema to
  card-row builder for registry-driven modifier cards), `.Status.cs` (build status, copy log, copy case,
  and structured diagnostic formatting), and per-tab card builders `.Modifiers.cs`, `.Zones.cs`,
  `.Markers.cs`, `.Analysis.cs`, `.Objects.cs`, `.LayerPickers.cs`. The main file holds the toolbar,
  layout refresh, and shared helpers. Its editable terrain selector combines active-terrain selection
  and rename in one field. Bake is grouped with the visibility/lock output actions; Bake Layers creates
  the terrain's configured output layers. Expanded Smooth/Sculpt cards show a sampled mesh-quality
  warning when an uneven incoming TIN has no earlier Remesh modifier. Triangulate keeps its source rows
  together, then shows Work area, Contour Mode, and a titled Peel Border settings group.
- `UiMetrics.cs`, `PropertyRow.cs`, `AdaptiveControlGroup.cs`, and `AdaptivePrimaryActionRow.cs` -
  local responsive primitives for the dock panel. `UiMetrics` measures the active Eto font, and rows
  restack by their own width, so resizing the panel does not rebuild the whole content tree or drop
  editor focus. `UiTiming.cs` centralizes editor debounce intervals.
- `RefreshUi` treats missing model units as a document state: the status card explains how to set units,
  and creation/build controls stay disabled while non-destructive inspection remains available.
- `BlockSelectorDialog.cs` / `BlockThumbnailRenderer.cs` - the Insert-style block picker for Scatter,
  with Eto-drawn isometric thumbnails.
- `LayerTemplateEditorDialog.cs` - graphical layer-template editor: a `TreeGridView` of the layer
  hierarchy with display/print color swatches and plot-weight, a properties panel for the selected
  layer, multi-template management, and JSON import/export. `SlowBuildWarningDialog.cs` is the other
  dialog.
- `SculptToolbarForm.cs` - the floating sculpt mini-toolbar: borderless, non-activating, Topmost,
  pinned over the active viewport corner while a sculpt session runs (brush buttons, radius/strength
  sliders, falloff, Done). Only edits session prefs; returns focus to Rhino after every interaction so
  viewport shortcuts keep working. Owned by `Services/SculptSessionController`.
- `PanelIcons.cs` - 16x16 panel tab/badge icon loader. `PanelButtonIcons.cs` - theme-aware
  vector line icons for compact action buttons. Card headers expose duplicate/delete as direct icon
  actions; `UiTheme.cs` owns colors/spacing.

Tabs: Modifiers, Zones (Objects), Analysis, Markers. The panel talks to `Services/TerrainController`;
it holds no terrain logic. No automated UI tests (needs the Rhino runtime) - verify UI changes by compile
and a Rhino smoke load.
