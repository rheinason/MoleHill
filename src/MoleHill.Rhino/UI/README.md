# MoleHill.Rhino/UI

The dockable terrain panel (Eto.Forms) and its dialogs. Composes reusable card/editor primitives — add
fields by composing them, not by copying boilerplate.

- `MoleHillPanel.cs` + `MoleHillPanel.*.cs` partials — the panel, split by concern:
  `.Cards.cs` (collapsible "stack card" framework), `.Editors.cs` (form-control vocabulary —
  `CreateSourceEditor`/`CreateNumericEditor`/`CreateDropDownEditor`/…), `.Schema.cs` (schema → card-row
  builder for registry-driven modifier cards), and per-tab card builders `.Modifiers.cs`, `.Analysis.cs`,
  `.Objects.cs`, `.LayerPickers.cs`. The main file holds the toolbar, terrain-level actions, layout
  refresh, zones/markers cards, and shared helpers.
- `BlockSelectorDialog.cs` / `BlockThumbnailRenderer.cs` — the Insert-style block picker for Scatter,
  with Eto-drawn isometric thumbnails.
- `LayerTemplateEditorDialog.cs` — graphical layer-template editor: a `TreeGridView` of the layer
  hierarchy with display/print color swatches and plot-weight, a properties panel for the selected
  layer, multi-template management, and JSON import/export. `SlowBuildWarningDialog.cs` — other dialog.
- `PanelIcons.cs` — 16×16 panel tab/badge icon loader. `UiTheme.cs` — colors/spacing.

Tabs: Modifiers, Zones (Objects), Analysis, Markers. The panel talks to `Services/TerrainController`;
it holds no terrain logic. No automated tests (needs the Rhino runtime) — verify UI changes by compile
+ a Rhino smoke load.
