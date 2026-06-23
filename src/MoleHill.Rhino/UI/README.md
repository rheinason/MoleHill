# MoleHill.Rhino/UI

The dockable terrain panel (Eto.Forms) and its dialogs. Composes reusable card/editor primitives — add
fields by composing them, not by copying boilerplate.

- `MoleHillPanel.cs` + `MoleHillPanel.*.cs` partials — the panel. `MoleHillPanel.Cards.cs` holds the
  collapsible "stack card" framework (shared by modifier/zone/object/analysis cards);
  `MoleHillPanel.Editors.cs` holds the form-control vocabulary (`CreateSourceEditor`,
  `CreateNumericEditor`, `CreateDropDownEditor`, `CreateCheckEditor`, …). The main file is large and a
  decomposition target (`docs/cleanup-plan.md`) — extract per-tab card builders into more partials.
- `BlockSelectorDialog.cs` / `BlockThumbnailRenderer.cs` — the Insert-style block picker for Scatter,
  with Eto-drawn isometric thumbnails.
- `LayerTemplateEditorDialog.cs`, `SlowBuildWarningDialog.cs` — other dialogs.
- `PanelIcons.cs` — 16×16 panel tab/badge icon loader. `UiTheme.cs` — colors/spacing.

Tabs: Modifiers, Zones (Objects), Analysis, Markers. The panel talks to `Services/TerrainController`;
it holds no terrain logic. No automated tests (needs the Rhino runtime) — verify UI changes by compile
+ a Rhino smoke load.
