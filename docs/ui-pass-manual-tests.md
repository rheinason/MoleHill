# UI pass — manual test checklist

What still needs a person at the keyboard before the `ui-structure` branch is merged. Everything here was
either checked by script only, checked in one theme / width / scale only, or not driven at all (real mouse
drags, modal dialogs, the docked panel). The scripted checks and their results are recorded in
[ui-backlog.md](ui-backlog.md) → Implementation status and UI-R6.

Tick each box; write what you saw next to anything that fails.

## Follow-up — narrow dock, Spot migration and theme, 2026-11-23

This run used real computer-use clicks, keys, splitter/slider/grip drags and modal captures, with
Rhino scripts for disposable scene setup and native state assertions. **It is still not full merge
acceptance:** the remaining limitations are listed below; a checkbox is not a claim that every
width/theme/keyboard combination was repeated.

- Main run: commit `84e37550`, Release RHP MVID `f84c8bd8-681c-4712-8db8-bc132c577659`, owned
  PID **7392**, Rhino **8.36.26296.17001**. Native assembly path was the worktree RHP and
  `LoadPlugIn` returned true. Both required dock widths were reached by actual splitter drags and
  read back as **240 × 747** and **420 × 747** logical pixels. WPF DPI was **1 × 1 (100%)**.
- Final theme check: commit `9c31e2059177e22ea9258087472ee6edcc803ed2`, newly built with
  `dotnet build src/MoleHill.Rhino -c Release` (**0 warnings, 0 errors**), owned PID **24404**,
  worktree RHP MVID **`1b6ca227-4141-4242-bbea-55289874a7cc`**, loaded unblocked. A restart that
  loaded the normal-build RHP was excluded and closed before this verification.

**Sections 1–3:** Settings and toolbar layout passed at both widths in dark theme. Copy/delete/menu
columns aligned over Bake/eye/lock; no Settings strip, aligned edges, swatch-left/Reset-right,
separate opacity row, readable wrapping checkboxes, and side-by-side Edit/Apply (below the template
name at 240). Real slider drag made the terrain transparent; typing and arrow stepping synchronized
field/slider/alpha. A physical Bake click at 240 produced **467 tracked objects** and immediately
enabled both Untrack buttons, with the unavailable explanation removed.

Keyboard checks passed for Detail Size Up/Down/Shift+Up/Enter (**0.25 → 0.35 → 0.25 → 1.25**),
Tab skipping number arrows, Status Enter/Space, card Enter-collapse/Space-expand, segmented
Left/Right and the eye icon's Enter/Space. Card redraw loses focus, as expected; the next Tab restarts
at the terrain selector. Tab/Shift+Tab traversed settings, checkbox, icon and card disclosure stops
with visible focus/caret. The entire panel's tab order was not exhaustively traversed at both widths.
Card and section help rendered during focus/hover. Stepper Increase/Decrease hover text was not
successfully captured and remains open.

Actual eye click hid terrain, analysis colours, both annotation families and Scatter output together;
viewport-focused **Ctrl+Z** restored them in one `Hide MoleHill Terrain` undo, and **Ctrl+Y** hid them
again. The shown-state tooltip rendered. At 420, real hover showed the disabled Auto Range explanation;
turning Auto-fit range off enabled the bounds and hover said **Drag to scrub**. Cut/Fill's disabled
Fill bound showed **The range is symmetric about zero, so this follows Cut. Set it there.**
These range hovers were not repeated at 240.

**Section 4:** A 10,201-point relief terrain and fine Remesh produced a genuinely running background
build. Force Reset's real Yes/No modal appeared; **No** left the same build running. The first Yes
attempt happened after that worker completed, so it is not cancellation evidence. On a second active
Remesh, **Yes** cleared it and the first post-click capture showed the full reset sentence in the
expanded Status log. A subsequent physical Rebuild completed and restored **1,740,954 mesh faces**.
Subsecond cancellation latency was not measured. Windows and an Eto UI-timer callback both reported
**fo-FO, decimal separator comma**; the visible expanded running/completed logs used decimal points.
At 240 the collapsed running Status ellipsized before the elapsed time; expanding it exposed the log.
The real **Slow Build Warning** subsequently appeared with Continue then Cancel on the right:
Cancel skipped the request; a second request and Continue completed successfully.

**Section 5 — real modals:** inspected Layer Templates, its New/Rename prompts, Output for layer,
Field Codes, Validate Terrain Inputs, Add Blocks to Scatter, Add Zones From Layers, Save Color Ramp,
Import Terrain Definition preview, and Slow Build Warning. Titles/actions/units and relevant layout
assertions passed where visible: annotation-root style row/picker appeared; a non-annotation row had
none; Print Width had mm; survey Vertical Offset had m and bold filename/Preview; validation Preview
filled its summary; block thumbnails loaded; the used zone layer was ticked/greyed; ramp prompt had
one field and Save/Cancel; import preview opened below the selector with Import/Cancel.
Survey and import-preview modals were invoked through their real dialog entry points with prepared
content, bypassing the file chooser. Other listed flows used their panel/command entry points.
Cancel clicks closed them; Output's OK and warning Continue were also exercised.
**Enter/Esc are still unverified for the modal family:** computer-use keyboard input activated the
owner, and a name-prompt typing attempt reached Rhino's command line instead. This is a harness
limitation, not evidence of a dialog keyboard defect. With the generated layer tree, the zone picker
also grew taller than the restored owner window; its separate modal capture exposed the buttons.

**Section 6:** Add offered Spot Heights/Spot Slope and none of the five old entries. Real Objects
pickers accepted two prepared points and a curve. Spot Heights gave **2 point labels**, then **9 curve
labels** at 10 m interval; switching back retained both points. Grid without a boundary gave **441**
labels; accepting the 20–60 m rectangle gave **81**, then changing spacing 5 → 10 m gave **25**.
All 25 anchors were within the rectangle and on the terrain (maximum mesh-distance about
**2.3e-7 m**). Label Settings started closed and contained exactly the requested formatting controls,
with only Results afterwards. A placement edit and real viewport-focused Ctrl+Z restored its previous
placement and sources. Spot Slope gave **2 point labels**, **8 curve-span labels**, and **441 grid
arrows**. Percent → Promille updated the values by ten. Flip Arrow reversed all **441/441** directions
without moving their anchors. Both point sources survived placement switching.

**FAIL → fixed in 6565d11, native test checks it.** Default arrows now point downhill; cards from 1.6.0
migrate with Flip Arrow inverted so their drawing is unchanged. Original finding:
**FAIL — default slope arrows point uphill.** On the planar legacy fixture, z = 0.05x + 0.02y, all
**25/25** grid arrow +X axes have positive dot product with the uphill gradient with Flip Arrow false.
The built-in arrow geometry points in +X. This is present in 1.6.0 and was reproduced in the final
1.7.0-beta worktree RHP; migration preserves it. The Flip Arrow tooltip describes the reverse of the
observed default. No source fix was made in this test task.

**Genuine 1.6.0 file round trip:** built the Rhino project from archived tag `v1.6.0` (0 warnings/errors),
loaded its exact RHP in owned PID **8464**, MVID **`17c9bd69-37e6-4ae9-b719-8cfe7daf28e0`**.
Created all five legacy card types plus a disabled sixth card, with distinct labels, source IDs,
prefix/suffix, F3 format, scales/colour and positions in the stack. Saved an actual native `.3dm`
(schema 33), then reopened it with **RhinoDoc.OpenHeadless**, exercising native plug-in document
reading rather than just JSON deserialization. All six mapped to the correct type/placement;
identity/order/enabled state, source sets and formatting assertions had **zero failures**. Rebuilt
**38 outputs** matched all transforms, block definitions, colours and user-text values, allowing only
the decimal comma/point difference between the locale-sensitive old output and invariant new output.
The latest worktree RHP also rebuilt the same 38 outputs with **zero differences** after that decimal
normalization. Saved schema 34 and reopened it natively/headlessly in the actual 1.6.0 RHP:
**LoadFailed true**, terrain state read-only, unrecognized `spot-heights`, and raw JSON preserved
byte-for-byte. Interactive File → Open was not used for this round trip.

**Section 7 highlight:** at 240 the real third-to-first grip drag reordered **1/2/3 → 3/1/2** and counts
finished at **6/5/5 placed**. After removing the overly broad observer that interfered with drops,
a narrowly attached observer on the four existing sequence targets recorded **DragEnter: Move,
background #FF466EC3** and **Drop: transparent** during another successful pointer drag. Thus the
highlight state is applied during the gesture and cleared on release. **Its painted appearance while
held remains unverified**: the computer-use drag releases atomically, including when capture is
requested concurrently. This is not a failed reorder and is not a visual-highlight pass.

**Sections 8–9:** captions passed at the actual 100% scale. The original theme-switch failure was
reproduced on the main-run build. The final RHP independently passed a real dark → light → dark
switch without a forced RefreshUi: Settings/Status/cards, toolbar icons and step arrows updated.
Light Settings/toolbar were inspected at **420 and 240**, with readable captions, aligned columns,
wrapped checkboxes, side-by-side actions and a visible field focus ring. Layer Templates opened as a
real, readable light modal. Restored dark before closing. No 125/150% claim is made; actual scale is 100%.

**Fixed in 3455dba, re-measured live:** at 240 Zones/Analysis now get 57 px with a 28 px selectable area
and their icons; every tab icon is centred; the Zones heading wraps View/Add Zone below the title.
Original finding — **Additional narrow-width issue:** at 240 the Zones and Analysis selection panels measured only
**5 × 26 px** beside their view buttons. The main icons disappeared; clicking the apparent tab centre
toggled visibility instead. Selecting Zones required the remaining 5 px sliver. At 420 both main icons
were visible. This is separate from section 1's correctly aligned toolbar.

Evidence remains under `.artifacts/ui-remaining/`: genuine-v1.6.0.3dm, migrated-schema34.3dm,
remaining-ui-session.3dm, legacy-before/after JSON and output text, legacy-after-latest.txt,
legacy-refused-raw.json, keyboard.txt, held-drag-targets.txt, ui-culture.txt and recovery-build-log.txt.
The real UI captures are in the computer-use conversation. No production document was used.
Closed the legacy and main test Rhinos, and closed the final PID 24404 through its creating client
(`closed: true`). Restored/read back the original normal-build RHP registration; no Rhino process
remained. No source code or unrelated in-progress performance changes were edited by this test run.

## Follow-up — real Scatter mouse drags, 2026-11-23

**The capture blocker was resolved in this retry:** double-clicking Rhino's title bar to maximize the
window made the dock, toolbars and viewports render correctly in computer-use captures. The earlier
blank captures were not sufficient grounds to stop without trying this window transition.

Used the same verified Release RHP/MVID in a fresh owned slot `aardvark`, **PID 27304**, Rhino
8.36.26296.17001. Seeded a disposable metre document with a 121-point relief terrain, a straight curve
from (10,50) to (90,50), and three distinct box blocks named `Drag block 1/2/3`, with stored weights
1/2/3. Curve Scatter used Sequence, Count 15; the completed result contained 16 placements including
endpoints. These were setup scripts; **reorders below used actual computer-use pointer drags**.

- **PASS — third row to first:** grip drag (2504,1237) → (2515,1134), in the observed maximized
  window. Visible rows changed 1/2/3 → **3/1/2**, numbered 1/2/3. Native model query confirmed that order;
  completed output transforms sorted along the curve cycled **3,1,2,3,1,2**, with 16 placements.
- **PASS — end gap:** dragged the first row into the 8 px tail at y=1278. Rows changed **3/1/2 →
  1/2/3**, and the build completed. An initial drop at y=1284 missed the gap and made no change;
  native read-only bounds identified the tail as (2497,1274), size 529×8. Retried the real pointer
  gesture on the refreshed screenshot. The tail tooltip was visible afterwards.
- **PASS — actual Ctrl+Z:** undid the end-gap reorder, restoring **3/1/2** in the card and viewport.
  Command history showed `Undoing Edit MoleHill Terrain`; a rebuild completed.
- **PASS — inclusion:** unticked block 3 using its checkbox. Its position became **–**, the other
  rows renumbered 1/2, and the completed native output contained only alternating blocks **1/2**.
- **PASS — weights preserved:** clicked Random. Weight/share controls appeared, block 3 remained
  disabled, and native queries confirmed Random mode and unchanged weights **3,1,2** by row
  (1/2/3 by block identity). The two enabled shares showed 33.3% / 66.7%.
- **Still unverified:** highlight *during* the held drag. The computer-use drag is atomic and the
  captures show the released state. The reorder itself and resulting placements are verified.
- **Additional UI issue observed:** after reorder/inclusion edits, rows remained **“count pending”**
  after Status and command history reported a completed build. Native queries found 16 placements
  and one current Scatter summary. Undo reconstructed the rows and displayed 6/5/5 placed again.
  This is a stale quantity-display issue, separate from successful reordering; no source fix made.
  **Fixed afterwards (same day), live re-checked:** a finished build skipped the card refresh when its
  results matched the previous build's, but the cards had redrawn mid-build showing "count pending", and
  Scatter counts were not part of what was compared at all. The placed-count getter now records when a card
  drew the placeholder, which forces the next finished build to refresh, and Scatter counts are in the
  card-result signature. Re-check: a weight edit (no change to placements) and a reorder + untick both
  ended showing counts, never "count pending".

This follow-up covers section 7's current-document gestures, not the 1.6.0 weight-zero migration or
other unfinished sections. All captures were the actual docked panel at its existing approximately
576 px width; no claim is made for 240/420 px, light theme, or another display scale.

Closed the exact owned slot through its creating client (`closed: true`, graceful exit), restored and
read back the original normal-build registration, and confirmed no Rhino process remained. No test
3dm was saved and no source code was changed.

## Test run — 2026-11-23 (partial; not merge acceptance)

Tested commit `6fe771d26f83f1e0b02c809279fc6758016991ee`. One functional failure was reproduced.
The remaining visual/gesture checks are **unverified**, not passed.

### Environment and setup

- `dotnet build src/MoleHill.Rhino -c Release` passed: **0 warnings, 0 errors**. No full-solution build or Grasshopper installation copy was run.
- Rhino **8.36.26296.17001**, standalone router **0.4.0**, owned slot `aardvark`, **PID 5112**. The desktop router's initial spawn failed with Windows error 5; the supported `tools/rhino-live-client.py` host route succeeded.
- First disposable session (PID 7784) exposed the normal build at startup and was closed without testing it. With Rhino closed, temporarily changed MoleHill's registration to the worktree Release RHP and spawned a fresh session. Verified its assembly location, MVID **`a9e7bf59-5312-4340-a658-980857676e45`**, and `PlugIn.LoadPlugIn(id) == true`.
- Created a throwaway metre document with 441 points: x/y from 0 to 100 at 5 m spacing, z = `5 * sin(i * 0.2) * cos(j * 0.2)`. Created terrain `UI manual test` through `CreateTerrainFromPointIds`; build completed.
- Opened the registered panel (`mhPanel`, followed by `Panels.OpenPanel` and native queries). `Panels.IsPanelVisible` was true, `PanelDockBar` was nonempty, panel size **576 × 1132**, and `_isPanelLoaded` was true. This was the real docked panel, not a form-hosted reconstruction. Required 240/420 px widths were not reached.

### Failure: section 1, tracking buttons do not refresh after Bake

**Fixed afterwards (same day), live re-checked:** a bake now refreshes the panel like an untrack does.
After the panel's own Bake handler, 11 objects tracked, both Untrack buttons enabled and the unavailable
sentence gone, with no manual refresh.

Before Bake, Untrack Selected and Untrack All were disabled and had the expected unavailable explanation.
After Bake, both **remained disabled** with the same explanation despite **one tracked baked mesh**.
This persisted across separate script/AX observations, after idle time.

Reproduction used the real docked panel and controller. Called `TerrainController.BakeTerrain(doc, terrainId)`,
then separately invoked the panel's actual `OnBakeTerrain` handler through reflection to exercise the UI
handler path. Queried `BakedObjectIds.Count == 1`, **442 active document objects** (441 source points + one
mesh), and both buttons' `Enabled`/`ToolTip`. Both were still false/stale. Invoking `RefreshUi` then made
both true and removed the unavailable sentence. No source fix was made.

Source inspection explains the observation: `OnBakeTerrain` only calls `BakeTerrain`; the bake path saves
with `raiseStateChanged: false`, and neither path refreshes the panel at completion. This is native
control/document-state evidence; the physical Bake click was not exercised.

### Other partial evidence

- Section 1 opacity: actual UI click/type `65` + Enter changed field and slider to **65**, and persisted
  colour alpha to **166/255**. Actual Up + Enter changed **65 → 70**, alpha **178/255**, matching the 5% step.
  Slider dragging and the visible terrain appearance remain unverified.
- Section 2: the numeric Up/Enter path above worked. Down, Shift+Up, tab order, focus rings,
  disclosure/segmented/icon keyboard actions remain unverified.
- Section 3: AX/native properties confirmed the pre-bake unavailable explanations. Mouse-hover rendering,
  visibility/Undo/Redo, Auto Range and symmetric-range interactions remain unverified.
- Section 5 Layer Templates: clicked the real dock's Edit button. Native Eto queries confirmed
  `Layer Templates`, `Visible == true`, bounds **1029,193,1382,1005**. The modal was not exposed in the
  computer-use window inventory, and main-window AX became unavailable while it was open. An Escape
  sent to the owner did not establish cancellation; this is **not** evidence of a dialog keyboard bug.
  Closed that exact dialog programmatically for cleanup. Dialog layout, picker flows and Enter/Esc
  acceptance are unverified.

### Capture blocker and remaining work

Both Windows computer-use capture routes returned the main-window chrome/history but **blank WPF dock
and viewport content**. Native controls and populated AX data existed underneath. No paint defect is
inferred from these captures. Asked the user whether the panel was visible on the physical display;
no confirmation was available during the run.

Sections **1–3 at both required widths**, **4**, the rest of **5**, and **6–9** remain open. In particular,
no real Scatter drag, Spot placement/picking, 1.6.0 document migration/backward refusal, comma-decimal
build log, cancellation confirmation, light-theme or alternate-scale check was completed. The script
host reported invariant culture; it does not establish the comma-decimal scenario. No checklist box
covering those assertions has been ticked.

### Cleanup

Closed the exact owned slot through its creating client (`closed: true`, graceful exit). Restored and
read back the original registration:
`C:\Users\hbxma\Dropbox\TopoTest\src\MoleHill.Rhino\bin\Release\net7.0\MoleHill.Rhino.rhp`.
Process inspection found no remaining Rhino. No production document was used or saved; no source code
was changed. No unit-test suite was run: this run targeted the requested native/manual checklist.

## 0. Setup

- [x] Close Rhino. In the worktree (`C:\Users\hbxma\mh-worktrees\ui-structure`) build the plug-in only:
      `dotnet build src/MoleHill.Rhino -c Release` (a full-solution build also overwrites the installed
      Grasshopper `.gha`).
- [ ] Start Rhino, open **Options → Plug-ins**, find MoleHill and point it at
      `src\MoleHill.Rhino\bin\Release\net7.0\MoleHill.Rhino.rhp` in the worktree (or use **Install…**).
      Restart Rhino and confirm in **Plug-ins** that the path is the worktree one.
- [x] Open the MoleHill panel **docked** (not floating) — every live check so far hosted the panel in a
      plain window, never in Rhino's dock.
- [x] Work in a throwaway document: model units metres, a terrain from a point grid with some relief.
- [x] When done, point the plug-in back at your normal build.

Run sections 1–3 at **two dock widths** (as narrow as the dock allows, about 240 px, and about 420 px) and
in the **dark theme**; section 9 repeats the key screens in light theme and at another display scale.

## 1. Panel top and Terrain Settings

- [x] Toolbar: copy / delete / ⋯ on row 1 sit exactly above bake / eye / lock on row 2, at both widths.
- [x] Terrain Settings has no coloured strip on its left; its edges line up with the toolbar and Status.
- [x] Terrain Color row: swatch on the left, **Reset** on the right. Opacity has its own row: slider plus a
      `100%` field. Drag the slider and type a value; both move the terrain's opacity.
- [x] Show Wires, Warn Before Slow Builds and Replace Previously Baked span the card; no caption is cut
      off at the narrow width (they wrap onto separate lines instead).
- [x] Output Layers: **Edit…** and **Apply** side by side at the wide width; at the narrow width they may
      drop below the template name but are never stacked one above the other while there is room.
- [x] **PASS — physical Bake click rechecked with 467 tracked objects; see follow-up.** Untrack Selected / Untrack All: before any bake, both are greyed and the tooltip ends "Unavailable:
      nothing from this terrain is baked and tracked yet." Bake once: both enable and the extra sentence
      is gone.

## 2. Keyboard

In the docked panel, click once in the panel, then use only the keyboard.

- [ ] **Tab** moves through the toolbar and cards; every stop shows a visible focus ring or caret
      (icon buttons, disclosure arrows, segmented controls, fields, checkboxes).
- [x] Tab does **not** stop on the little ▴▾ arrows of a number field.
- [x] On a card's disclosure arrow: **Enter** collapses the card, **Space** expands it. Note where focus
      lands afterwards (expected: focus is lost because the stack redraws — record it if that is
      annoying in practice).
- [x] On a section arrow (e.g. a card's Results, or Terrain Settings): Enter / Space open and close it.
- [x] On a segmented control: **Left / Right** change the choice.
- [x] In a number field: **Up / Down** step it; **Shift+Up** steps ten times as far; **Enter** commits.
- [x] On an icon button (duplicate, delete, eye): Enter or Space triggers it.

## 3. Tooltips and disabled states (mouse hover)

- [ ] Hover a number field's ▴ and ▾: "Increase / Decrease (Up arrow in the field; …)".
- [ ] Hover a card's disclosure arrow: "Expand this card …" / "Collapse this card …".
- [x] Hover a section heading arrow: "Show or hide <section>."
- [ ] Hover the eye: "Terrain and all its outputs shown. Click to hide them." Click it: the mesh, the
      analysis colours and the annotations all disappear; the tooltip flips to "… hidden. Click to show
      them." **Ctrl+Z** brings everything back in one step; **Ctrl+Y** hides it again.
- [x] Add a Slope analysis. With **Auto Range** on, the two range fields are greyed and their tooltip
      says Auto Range owns them. Turn Auto Range off: the fields enable and say "Drag to scrub".
- [x] Add a Cut / Fill analysis (symmetric range): the upper field is greyed and says to set it with the
      lower one.

## 4. Build status and cancelling

- [ ] **PARTIAL — real slow build and comma-locale point formatting passed; at 240 the collapsed line clips elapsed time.** Make a slow build (a fine Remesh on a dense terrain). While it runs, the Status line shows
      "Building · <stage> (n of m) · s" and any numbers in the expanded log use a **point**, never a
      comma — check this on your comma-decimal Windows locale.
- [x] Click **Force Reset Build** during the build. The confirmation dialog appears; **No** lets the
      build carry on.
- [ ] **PARTIAL — cancellation/reset and recovery rebuild passed; subsecond latency unmeasured.** Again, **Yes**: the build stops within a second or so and Status reads "Build reset. Rebuild to
      resume terrain outputs." Rebuild brings the outputs back.

## 5. Dialogs (real modal flow)

All listed modals received visual/mouse checks in the follow-up above. Their boxes remain open for
the shared Enter/Esc requirement: the input tool targeted Rhino's owner window instead of the modal.

The dialogs were captured as plain windows; open each for real. For every one check: title, the main
action then **Cancel** on the right, **Enter** = main action, **Esc** = Cancel, headings bold, no clipped
caption or label, units inside number fields.

- [ ] **Layer Templates** (Terrain Settings → Output Layers → Edit…). Select a non-annotation layer: no
      "Annotation Style" row. Select an annotation layer: the row and its picker appear. **New** and
      **Rename** open the one-line name prompt (Create / Rename, then Cancel). Print Width shows "mm".
- [ ] **Output for <layer>** (Receives… button inside Layer Templates).
- [ ] **Import Survey Points** (`mhImportSurveyPoints` on any CSV): "Vertical Offset" shows the model unit
      inside the field; the file name and "Preview" headings are bold.
- [ ] **Field Codes** (`mhEditFieldCodes`): "Unmatched Codes" is not cut off; Restore Defaults / Import… /
      Export… sit left of Save / Cancel.
- [ ] **Validate Terrain Inputs** (`mhValidateTerrainInputs` with some curves selected): Tolerance shows
      the unit; **Preview** fills the summary; Apply / Cancel on the right.
- [ ] **Add Blocks to Scatter** (Scatter card → Add): the filter box reads "Filter blocks…"; block
      **thumbnails load** after the dialog opens (never checked live).
- [ ] **Add Zones From Layers** (Zones tab add-from-layers): "Filter layers…"; already-used layers are
      ticked and greyed.
- [ ] **Save Color Ramp** (an analysis ramp → presets → save): one-line prompt, Save / Cancel.
- [ ] **Import Terrain Definition** (terrain ⋯ menu): opens just below the terrain selector; Import /
      Cancel on the right.
- [ ] **Slow build warning** (a very large terrain with Warn Before Slow Builds on): Continue / Cancel,
      main action first.

## 6. Spot Heights and Spot Slope

- [x] Annotations → Add: the menu offers **Spot Heights** and **Spot Slope**, and no longer the five old
      entries (Points / Curve variants, Flow Arrows).
- [x] Spot Heights: Points / Curves / Grid across the full width. Points: pick two points → two labels at
      terrain height. Switch to Curves: the points row is replaced by Curves + Interval; pick a curve →
      labels along it. Switch back to Points: **your two points are still there**.
- [x] Grid with no boundary covers the whole terrain; add a closed boundary curve → labels only inside.
      Grid Spacing changes the density.
- [x] Label Settings is closed by default and contains Block, Decimals, Prefix, Suffix, Block Scale, Color
      and Text Size (nothing appears below it except Results).
- [ ] **FAIL — placement, units and flip passed, but default grid arrows point uphill; see follow-up.** Spot Slope: same three placements plus Units and Flip Arrow; Grid draws downhill arrows (the old
      Flow Arrows).
- [x] Change a placement, then **Ctrl+Z**: the previous placement returns.
- [x] **PASS — native headless file round trip; see follow-up. Old documents:** open a `.3dm` saved by 1.6.0 that has the old spot or Flow Arrows cards. Each
      comes back as Spot Heights / Spot Slope with the right placement, same name, same sources, same
      formatting, same position, and a disabled card stays disabled. Rebuild and compare the labels with
      what 1.6.0 drew.
- [x] **PASS — actual 1.6.0 RHP refused the native headless read and preserved its JSON.** Save that document, then try to open it with your normal 1.6.0 build: it should **refuse** (schema
      34) rather than open and lose data. (Expected; it is in the release notes.)

## 7. Scatter Sequence (mouse drag was never exercised)

- [x] A curve Scatter with three blocks: Block Mix starts with **Random / Sequence** across the full width.
      Random rows show weight and share.
- [x] **PASS — actual reorder at 240 finished showing 6/5/5 placed; see follow-up.** Switch to Sequence: rows show grip, checkbox, position number (1, 2, 3), thumbnail, name, remove and
      "n placed"; no weight.
- [ ] **PARTIAL — real drag and placement order passed; held-hover highlight unverified.** **Drag** the third row by its grip onto the first: it becomes position 1, the row highlights while
      hovering, and the placed blocks along the curve follow the new order.
      *Highlight attempt 2026-11-23:* a capture taken during the gesture still showed the released state,
      and an added event observer interfered with drop handling, so it was removed. Those attempts are not
      counted as application failures; the held-drag highlight stays unverified (needs a human eye).
- [x] Drag a row onto the thin gap **below the last row**: it moves to the end.
- [x] Untick a block: its position shows "–", the others renumber, and it disappears from the curve.
- [x] **Ctrl+Z** undoes the reorder.
- [x] Switch back to Random: the original weights are unchanged.
- [ ] Open a 1.6.0 document whose curve sequence used **weight 0** to leave a block out: that block now
      shows unticked and the placed pattern is the same as before.

## 8. Captions (UI-P1)

Measured centred at 100% display scale; confirm by eye at the scale you actually use.

- [x] **PASS 2026-11-23 at the actual 100% scale:** captions look centred. Look closely at Reset, Edit…, Apply, Add Modifier and a segmented control: the text should not look
      high. If it still looks high at your scale, note the scale (Windows Settings → Display) and a
      screenshot — the measurement only covered 100%.

## 9. Light theme and display scale

Dark and light were checked at 100%; see the narrow-dock follow-up above.

- [x] **FAIL 2026-11-23 → fixed (f819d6c, plus icons following the theme), live re-checked.** The user
      saw existing panel surfaces stay dark and toolbar icons turn nearly invisible after switching Rhino
      to light. Causes: the panel stopped listening for settings changes after its first unload (a hidden
      dock tab), and the theme was read from the command-prompt colour; icons on controls created once with
      the panel (Settings number arrows) kept the old theme's image. Re-check in the real docked panel:
      `SetToLightMode` → rebuilt light within ~0.65 s, step arrows dark and visible; `SetToDarkMode` →
      rebuilt dark within ~0.4 s, toolbar icons light. The user's appearance was restored and confirmed
      after a restart.
      Switch Rhino to the **light** theme and repeat sections 1, 3 and one dialog from 5: text readable,
      focus rings visible, no dark-only colours left behind.
      *Scripted follow-up:* a panel switched dark→light through its own theme-change handler matched one
      built fresh in light, so the switch itself leaves no stale colours. A test Rhino cannot show true
      light theme without changing the shared Rhino settings, so the visible failure needs the user's
      description or screenshot.
- [x] **Not applicable:** the display runs at 100%, where everything above was checked. If you use 125% or 150% scaling, repeat section 1 at that scale: toolbar columns still line up,
      nothing in Terrain Settings clips.

## Not covered here

Not yet built, so nothing to test: the earthy tab icons (UI-09) and a fuller Status detail view.
Screen-reader names cannot be set through Eto and were not checked.
