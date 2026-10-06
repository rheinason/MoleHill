# MoleHill.Rhino/UI

The dockable terrain panel (Eto.Forms) and its dialogs. Composes reusable card/editor primitives - add
fields by composing them, not by copying boilerplate.

Layer source popovers use a native `GridView` for available layers. Filtering replaces its data store;
keyboard navigation only selects and scrolls the existing rows. Enter commits the selected layer and
Escape closes the popup from either the search box or the grid.

- `CurveProfileControl.cs` — the read-only plan-station chart in `mhInspectCurve`. It paints the curve
  coloured by the selected metric, the terrain line, violation bands, events, a scrub cursor with its
  readout, and the legend ramp that says what the colours mean. Colour comes from the service-side
  `CurveReviewMetricSeries`, the same ramp the viewport conduit uses. Its one gesture is hover, which
  raises `HoverStationChanged` so the conduit can slide its viewport marker — the chart reports and
  scrubs, it never edits. It follows the existing Rhino-aware `UiTheme` rather than introducing a second
  palette.

- `MoleHillPanel.cs` + `MoleHillPanel.*.cs` partials - the panel, split by concern. The main file keeps
  only the controls and card state, construction, the controller subscription and `RefreshUi`; a new
  section or tab is a new partial, not an addition to it. The fixed sections: `.Toolbar.cs` (terrain
  selector, New/Copy/Delete, Rebuild/Live, Bake/visibility/lock), `.Settings.cs` (the Terrain Settings
  card), `.Tabs.cs` (tab strip, lazy rebuild of the visible tab, scroll restore, each tab's add
  toolbar); shared pieces: `.Controls.cs` (styling, button factories, help), `.Colors.cs` (optional
  colour editor, ARGB/opacity packing), `.DragDrop.cs` (stack reordering), `.Mutations.cs` (the single
  path from a card edit to the controller);
  `.Actions.cs` (terrain-level create/copy/delete/convert/bake/rebuild/reset actions),
  `.Cards.cs` (collapsible "stack card" framework), `.RuntimeDiagnostics.cs` (generic per-card issue
  counts and redraw-only **Show Issues** binding), `.Editors.cs` (form-control vocabulary -
  `CreateSourceEditor`/`CreateNumericEditor`/`CreateDropDownEditor`/...), `.Schema.cs` (schema to
  card-row builder for registry-driven modifier, analysis and annotation cards), `.Status.cs` (the detailed live/final build log,
  copy log, copy case, and structured diagnostic formatting), and per-tab card builders `.Modifiers.cs`, `.Zones.cs`,
  `.Analysis.cs`, `.Annotations.cs`, `.Objects.cs`, `.LayerPickers.cs`. The Analysis and
  Annotations tabs are separate top to bottom - separate stacks, toolbars, card maps, card builders and
  definition types - because analyses and annotations are separate content families
  (`docs/architecture.md` -> "Analysis vs annotation"). Only the Analysis tab has an eye button: it gates
  analysis output alone. Annotations are always drawn, so the Annotations tab has no such toggle and the
  per-card checkbox is the only control. Each tab's partial also owns that tab's type labels, colours
  and collapsed summaries. The toolbar's editable terrain selector combines active-terrain selection
  and rename in one field. Bake is grouped with the visibility/lock output actions. One
  "Output Layers" row names the layer template the terrain routes through, opens the editor, and creates
  its layers; there are no per-terrain or per-card layer pickers. Expanded Smooth/Sculpt cards show a sampled mesh-quality
  warning when an uneven incoming TIN has no earlier Remesh modifier. Triangulate keeps its source rows
  together, then shows a Boundaries group with standard Outer/Hide/Show/Data Clip object-and-layer rows,
  a Rectangle shortcut on Data Clip, Contour Mode, and a titled Peel Border settings group.
  Project To starts with a Target group: choosing a MoleHill terrain clears its Rhino mesh source and
  assigning a mesh clears the terrain choice; cycle-producing terrain choices are omitted. Boundaries,
  Strength, and Feather then use the ordinary schema rows.
  Simplify likewise uses ordinary schema rows: its Mode choice reveals Maximum Deviation, Target
  Vertices, or Retain Percentage, and its collapsed summary shows the active request. Measured
  reduction/error details stay in build diagnostics rather than document state.
- `MoleHillPanel.CompareTerrain.cs` - the comparison rows shared by Cut/Fill, Earthworks and the section
  cards: the "Compare To Terrain" dropdown and a status row that says when the chosen terrain is
  building, unbuilt, empty, edited without a rebuild, or newer than this terrain's results, with a button
  that runs the rebuild without switching terrains. Sections add an "Also Draw" list for profiles drawn
  without comparing.
- `MoleHillPanel.AnnotationStyle.cs` - the Annotations tab's Text Style row (the style the terrain's text
  binds to, its height and font, a picker that edits the document's template copy, and Edit… into Rhino's
  Document Properties) and each annotation card's Text Size row. There is no control to set a fixed text
  height: bake stamps the style and resets the size, so it would preview and bake differently. Legacy
  fixed-height cards say so and offer Follow Style.
- `ColorRampControl.cs`, `ColorRampBar.cs`, `ScrubField.cs`, and `MoleHillPanel.ColorRamp.cs` - the
  colour-ramp card, declared by `ParameterDescriptor<AnalysisDefinition>.ColorRamp()` and rendered for Slope,
  Elevation and Cut/Fill. It replaced three separate things: the "Coloring & intervals" group, the
  read-only legend below it, and the "Mapped" summary row between them - a legend you can edit needs no
  separate editor. Collapsed it is a legend (histogram, ramp, ticks, mapping controls); clicking the ramp
  grows handles on the *same* bar and reveals a stop table, so there is never a second gradient on screen;
  double-clicking a stop's displayed value opens inline exact entry in the analysis's own units.
  `ColorRampBar` is the only painter and it paints through `AnalysisColorMapper`, so the strip and the
  terrain mesh cannot disagree about a colour. `ScrubField` is the drag-to-change numeric chip (alt-click
  resets, plain click opens a text box for an exact value). Edits commit through
  `MutateAndRefreshAnalysisLive` while the pointer is down and `MutateAndRefreshAnalysis` once on release.
  That split is load-bearing, not an optimisation: a plain analysis mutation saves the document, the save
  raises StateChanged, and StateChanged rebuilds the card - mid-drag that destroys the control being
  dragged, so the gesture died on its first mouse-move and edits looked like they did nothing. The bar
  also takes an explicit pointer capture, since neither Eto nor the native frameworks capture on their
  own and the drag would otherwise end the moment the cursor left the strip. A move received without the
  primary button closes a capture-lost gesture before its coordinates can snap a stop to an end. The same live/final split
  applies to the top toolbar's line-weight and opacity sliders (`MutateSelectedTerrainLive`), which were
  serializing the whole terrain to JSON and rebuilding every card on every slider tick. Expansion state
  is session-only, held in `_expandedColorRamps`, never written to the document.
- **Panel edits use Rhino Undo/Redo.** Ordinary commits register one serialized terrain-state record in
  `TerrainController`; sliders, live numeric fields, and ramp drags bracket their existing refresh
  deferral as one undo gesture. Mouse-up/blur commits the pending value and flushes the deferred document
  save before the gesture closes, so the panel never owns a second history stack and Rhino geometry plus
  terrain state remain in one record for compound actions.
- `MoleHillPanel.Cards.cs` also owns the two non-card grouping primitives, so the panel has one visual
  language for "a group of rows". `SectionHeader` is the collapsible one (Terrain Settings, Status,
  Variable Width Matching): the card's own chevron glyph, the card's header fill and padding, and the
  whole strip is the hit target. `CreateSectionRule` is the static one (Peel Border, analysis summaries):
  a caption and a rule. Both replaced `GroupBox`, whose border boxed in content the surrounding card had
  already grouped, and a boxed icon-button chevron that was a different glyph at a different height from
  every card in the stack. There are now no `GroupBox` uses left in the panel.
- **Tab layouts are built lazily.** A controller state change marks all five tab layouts dirty and
  rebuilds only the one on screen; the rest build when they are next shown (`RebuildVisibleTabLayout`).
  Rebuilding all of them on every mutation was four fifths wasted work, and card rebuilds are the
  expensive part of a refresh. The rebuild also restores the scrollable's position, so editing something
  below the fold no longer throws you back to the top of the list.
- **Status changes do not rebuild cards.** The controller raises `StatusChanged` when only the build
  status moved (an object edit scheduling a rebuild, build start, progress, mid-edit frames, and a
  finished build whose card results are unchanged); `HandleControllerStatusChanged` updates the status
  line alone. A full relayout cost 45-66 ms on a 100k-face terrain's wall edit, on the UI thread.
- `UiMetrics.cs` and `UiControls.cs` - the panel design-system foundation. `UiMetrics` combines
  active-font measurement with semantic spacing, height, icon, card, and breakpoint tokens;
  `UiControls` owns the native-first button/label/input roles and standard card/form layouts. Choose a
  semantic role instead of assigning a new local size or colour. Every icon action - inside a card or in
  the top toolbar - uses the one `Icon` role, which pins width *and* height, because a minimum width of
  zero let each button take whatever its glyph asked for and a row of icon actions came out at several
  different sizes. Glyph and box are sized against Rhino's own panel toolbars (the Layers panel docks
  right beside ours), which is the comparison that shows when they are too big.
- **A widget in a property row fills the widget column.** Pass `expandWidget: true` and give editors no
  fixed `Width`; a control sized to itself lines up with nothing else on the card, and the row that looks
  hand-placed is the one that was. `UiMetrics.LabelColumn` is sized for the longest labels the cards
  actually use, so labels are not clipped mid-word; below `RowBreak` the row stacks and the label gets a
  full line regardless.
- `PropertyRow.cs`, `AdaptiveControlGroup.cs`, and `AdaptivePrimaryActionRow.cs` - responsive panel
  primitives. Simple properties remain side-by-side at narrow Blender-like widths, equal columns and
  action groups reflow independently, and resizing does not rebuild the full panel or drop editor focus.
  The supported compact target is 240 logical Eto pixels with no horizontal scrolling. Secondary card
  metadata and toolbar commands yield before essential titles, inputs, and primary actions. `UiTiming.cs`
  centralizes editor debounce intervals.

Horizontal scrolling is a design failure, not a responsive fallback. Panel and picker scrollables hard-
disable their native horizontal scrollbar; child layouts must reflow, hide secondary content, or move
low-priority actions into overflow while remaining pinned to the available client width.
- `RefreshUi` treats missing model units as a document state: the status card explains how to set units,
  and creation/build controls stay disabled while non-destructive inspection remains available.
- Expanded zone cards show last-final-build quantities for the resolved zone output: plan/surface area,
  elevation, slope, mesh counts, and Earthworks cut/fill when a reference is configured.
- `BlockSelectorDialog.cs` / `BlockThumbnailRenderer.cs` - the Insert-style block picker for Scatter.
  It opens from the lightweight definition-name list, then queues Eto-drawn isometric thumbnails only
  for rows the grid formats; one UI-timer tick renders one thumbnail so documents with hundreds of blocks
  do not pay the whole library's mesh/render cost before the dialog appears. Thumbnail cache keys include
  document, definition id, and size, and definition-table changes invalidate that document's entries.
- `SurveyImportDialog.cs` - how to read a survey point file, with a live preview of the first rows.
  **The preview grid is the point of the dialog, not decoration:** PNEZD writes northing before
  easting, so a file read as XYZ parses perfectly and yields a terrain transposed about the 45 degree
  line that no later stage and no automated test can detect. Showing resolved X/Y/Z/code lets the
  user see easting in the X column before committing. Also carries the per-delivery vertical offset,
  which is not saved because a datum belongs to the file rather than the site.
- `FieldCodeTableEditorDialog.cs` - editor for the per-user survey field code table: a `GridView` of
  code rules (code, role, layer, closed, note) plus the marker spellings the office's crews type, and
  JSON import/export. The marker fields are the part that is easy to leave out and expensive to omit -
  hard-coding ST and END would work for one office and fail silently for the next, since the codes
  would still parse and the runs would simply never close. A blank Layer follows the role's own input
  layer, which is what lets one layer assignment pick up a whole survey. Edits are made against a
  clone and written only on Save.
- `LayerTemplateEditorDialog.cs` - graphical layer-template editor: a `TreeGridView` of the layer
  hierarchy with display/print color swatches, plot-weight and a read-only Receives column, a properties
  panel for the selected layer including which output role it receives, multi-template management, and
  JSON import/export. The role binding lives on the node, so it survives renaming or re-parenting a layer.
  Receives is a checklist, not a single choice, so several kinds of output can share one layer; ticking
  a role that is on another layer moves it, since one role landing on two layers would duplicate its
  output. Receives lists every role that *lands* on a layer, not the one bound to it — a role with no layer of
  its own resolves into an ancestor's, so binding alone would have shown nothing where retaining walls
  actually go. Roles whose layer the template does not list are offered as "Add N missing role layer(s)",
  which adds a row at the path each already resolves to, so nothing moves. Selecting the Annotation role
  exposes the document's Rhino dimension styles as the shared annotation-style picker; annotation cards
  no longer need independent text-height settings. `SlowBuildWarningDialog.cs` is the other dialog.
- `TerrainInputValidationDialog.cs` - short-lived, document-parented Eto dialog for selecting the
  cleanup operations and shared tolerance used by `mhValidateTerrainInputs`. Overkill removes both
  duplicate selected objects and later retraced segments inside a joined polycurve, retaining one
  unbroken traversal. The dialog
  previews operation counts and leaves document mutation to `TerrainInputCommandService`.
- `SculptToolbarForm.cs` - the floating sculpt mini-toolbar: borderless, non-activating, Topmost,
  pinned over the active viewport corner while a sculpt session runs (brush buttons, radius/strength
  sliders, falloff, Done). Only edits session prefs; returns focus to Rhino after every interaction so
  viewport shortcuts keep working. Owned by `Services/SculptSessionController`.
- `PanelIcons.cs` - 16x16 panel tab/badge icon loader. `PanelButtonIcons.cs` - theme-aware vector line
  icons on one optical canvas for universal compact actions. Named workflow actions stay as text, while
  icon-only actions always use a standard hit box and tooltip. Card headers expose duplicate/delete as
  direct icon actions; `UiTheme.cs` owns semantic colours for custom surfaces and state.

Use native Eto controls for ordinary buttons, inputs, lists, and selectors. Use `Drawable` only for
interaction or visualization Eto cannot express cleanly, such as ramps, scrub fields, swatches, and drag
handles. Dialogs and the floating sculpt toolbar have not yet been migrated to the panel design system.

Tabs: Modifiers, Objects, Zones, Analysis, Annotation. There is no Markers tab: markers are still built
from persisted definitions (`Services/TerrainBuildService.Objects.cs`) so older documents keep rendering
them, but spot elevation and slope labels are authored as annotations now, and the unreachable marker card
builder was deleted. The panel talks to `Services/TerrainController`;
it holds no terrain logic. No automated UI tests (needs the Rhino runtime) - verify UI changes by compile
and a Rhino smoke load.
