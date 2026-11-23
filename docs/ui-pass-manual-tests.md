# UI pass — manual test checklist

What still needs a person at the keyboard before the `ui-structure` branch is merged. Everything here was
either checked by script only, checked in one theme / width / scale only, or not driven at all (real mouse
drags, modal dialogs, the docked panel). The scripted checks and their results are recorded in
[ui-backlog.md](ui-backlog.md) → Implementation status and UI-R6.

Tick each box; write what you saw next to anything that fails.

## 0. Setup

- [ ] Close Rhino. In the worktree (`C:\Users\hbxma\mh-worktrees\ui-structure`) build the plug-in only:
      `dotnet build src/MoleHill.Rhino -c Release` (a full-solution build also overwrites the installed
      Grasshopper `.gha`).
- [ ] Start Rhino, open **Options → Plug-ins**, find MoleHill and point it at
      `src\MoleHill.Rhino\bin\Release\net7.0\MoleHill.Rhino.rhp` in the worktree (or use **Install…**).
      Restart Rhino and confirm in **Plug-ins** that the path is the worktree one.
- [ ] Open the MoleHill panel **docked** (not floating) — every live check so far hosted the panel in a
      plain window, never in Rhino's dock.
- [ ] Work in a throwaway document: model units metres, a terrain from a point grid with some relief.
- [ ] When done, point the plug-in back at your normal build.

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
- [ ] Untrack Selected / Untrack All: before any bake, both are greyed and the tooltip ends "Unavailable:
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

- [ ] A curve Scatter with three blocks: Block Mix starts with **Random / Sequence** across the full width.
      Random rows show weight and share.
- [ ] Switch to Sequence: rows show grip, checkbox, position number (1, 2, 3), thumbnail, name, remove and
      "n placed"; no weight.
- [ ] **Drag** the third row by its grip onto the first: it becomes position 1, the row highlights while
      hovering, and the placed blocks along the curve follow the new order.
- [ ] Drag a row onto the thin gap **below the last row**: it moves to the end.
- [ ] Untick a block: its position shows "–", the others renumber, and it disappears from the curve.
- [ ] **Ctrl+Z** undoes the reorder.
- [ ] Switch back to Random: the original weights are unchanged.
- [ ] Open a 1.6.0 document whose curve sequence used **weight 0** to leave a block out: that block now
      shows unticked and the placed pattern is the same as before.

## 8. Captions (UI-P1)

Measured centred at 100% display scale; confirm by eye at the scale you actually use.

- [ ] Look closely at Reset, Edit…, Apply, Add Modifier and a segmented control: the text should not look
      high. If it still looks high at your scale, note the scale (Windows Settings → Display) and a
      screenshot — the measurement only covered 100%.

## 9. Light theme and display scale

Every live capture so far was dark theme at 100%.

- [ ] Switch Rhino to the **light** theme and repeat sections 1, 3 and one dialog from 5: text readable,
      focus rings visible, no dark-only colours left behind.
- [ ] If you use 125% or 150% scaling, repeat section 1 at that scale: toolbar columns still line up,
      nothing in Terrain Settings clips.

## Not covered here

Not yet built, so nothing to test: the earthy tab icons (UI-09) and a fuller Status detail view.
Screen-reader names cannot be set through Eto and were not checked.
