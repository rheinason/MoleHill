# UI pass — manual test checklist

What still needs a person at the keyboard before the `ui-structure` branch is merged. Everything here was
either checked by script only, checked in one theme / width / scale only, or not driven at all (real mouse
drags, modal dialogs, the docked panel). The scripted checks and their results are recorded in
[ui-backlog.md](ui-backlog.md) → Implementation status and UI-R6.

Tick each box; write what you saw next to anything that fails.

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

- [ ] Toolbar: copy / delete / ⋯ on row 1 sit exactly above bake / eye / lock on row 2, at both widths.
- [ ] Terrain Settings has no coloured strip on its left; its edges line up with the toolbar and Status.
- [ ] Terrain Color row: swatch on the left, **Reset** on the right. Opacity has its own row: slider plus a
      `100%` field. Drag the slider and type a value; both move the terrain's opacity.
- [ ] Show Wires, Warn Before Slow Builds and Replace Previously Baked span the card; no caption is cut
      off at the narrow width (they wrap onto separate lines instead).
- [ ] Output Layers: **Edit…** and **Apply** side by side at the wide width; at the narrow width they may
      drop below the template name but are never stacked one above the other while there is room.
- [ ] **FAIL 2026-11-23 → fixed same day, re-check the physical Bake click (see run above).** Untrack Selected / Untrack All: before any bake, both are greyed and the tooltip ends "Unavailable:
      nothing from this terrain is baked and tracked yet." Bake once: both enable and the extra sentence
      is gone.

## 2. Keyboard

In the docked panel, click once in the panel, then use only the keyboard.

- [ ] **Tab** moves through the toolbar and cards; every stop shows a visible focus ring or caret
      (icon buttons, disclosure arrows, segmented controls, fields, checkboxes).
- [ ] Tab does **not** stop on the little ▴▾ arrows of a number field.
- [ ] On a card's disclosure arrow: **Enter** collapses the card, **Space** expands it. Note where focus
      lands afterwards (expected: focus is lost because the stack redraws — record it if that is
      annoying in practice).
- [ ] On a section arrow (e.g. a card's Results, or Terrain Settings): Enter / Space open and close it.
- [ ] On a segmented control: **Left / Right** change the choice.
- [ ] In a number field: **Up / Down** step it; **Shift+Up** steps ten times as far; **Enter** commits.
- [ ] On an icon button (duplicate, delete, eye): Enter or Space triggers it.

## 3. Tooltips and disabled states (mouse hover)

- [ ] Hover a number field's ▴ and ▾: "Increase / Decrease (Up arrow in the field; …)".
- [ ] Hover a card's disclosure arrow: "Expand this card …" / "Collapse this card …".
- [ ] Hover a section heading arrow: "Show or hide <section>."
- [ ] Hover the eye: "Terrain and all its outputs shown. Click to hide them." Click it: the mesh, the
      analysis colours and the annotations all disappear; the tooltip flips to "… hidden. Click to show
      them." **Ctrl+Z** brings everything back in one step; **Ctrl+Y** hides it again.
- [ ] Add a Slope analysis. With **Auto Range** on, the two range fields are greyed and their tooltip
      says Auto Range owns them. Turn Auto Range off: the fields enable and say "Drag to scrub".
- [ ] Add a Cut / Fill analysis (symmetric range): the upper field is greyed and says to set it with the
      lower one.

## 4. Build status and cancelling

- [ ] Make a slow build (a fine Remesh on a dense terrain). While it runs, the Status line shows
      "Building · <stage> (n of m) · s" and any numbers in the expanded log use a **point**, never a
      comma — check this on your comma-decimal Windows locale.
- [ ] Click **Force Reset Build** during the build. The confirmation dialog appears; **No** lets the
      build carry on.
- [ ] Again, **Yes**: the build stops within a second or so and Status reads "Build reset. Rebuild to
      resume terrain outputs." Rebuild brings the outputs back.

## 5. Dialogs (real modal flow)

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

- [ ] Annotations → Add: the menu offers **Spot Heights** and **Spot Slope**, and no longer the five old
      entries (Points / Curve variants, Flow Arrows).
- [ ] Spot Heights: Points / Curves / Grid across the full width. Points: pick two points → two labels at
      terrain height. Switch to Curves: the points row is replaced by Curves + Interval; pick a curve →
      labels along it. Switch back to Points: **your two points are still there**.
- [ ] Grid with no boundary covers the whole terrain; add a closed boundary curve → labels only inside.
      Grid Spacing changes the density.
- [ ] Label Settings is closed by default and contains Block, Decimals, Prefix, Suffix, Block Scale, Color
      and Text Size (nothing appears below it except Results).
- [ ] Spot Slope: same three placements plus Units and Flip Arrow; Grid draws downhill arrows (the old
      Flow Arrows).
- [ ] Change a placement, then **Ctrl+Z**: the previous placement returns.
- [ ] **Old documents:** open a `.3dm` saved by 1.6.0 that has the old spot or Flow Arrows cards. Each
      comes back as Spot Heights / Spot Slope with the right placement, same name, same sources, same
      formatting, same position, and a disabled card stays disabled. Rebuild and compare the labels with
      what 1.6.0 drew.
- [ ] Save that document, then try to open it with your normal 1.6.0 build: it should **refuse** (schema
      34) rather than open and lose data. (Expected; it is in the release notes.)

## 7. Scatter Sequence (mouse drag was never exercised)

- [x] A curve Scatter with three blocks: Block Mix starts with **Random / Sequence** across the full width.
      Random rows show weight and share.
- [ ] **PARTIAL — quantity refresh fixed in 84e3755; re-check by eye.** Switch to Sequence: rows show grip, checkbox, position number (1, 2, 3), thumbnail, name, remove and
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

Every live capture so far was dark theme at 100%.

- [ ] **FAIL 2026-11-23 — confirmed by the user; details to record here (what looked wrong, where).**
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
