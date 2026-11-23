# Rhino UI design backlog

Updated 2026-11-23. The decision record of the Penpot design review (Steps 01–80) and where its
implementation stands. Implementation is on branch `ui-structure`, not yet merged; each card was
checked live in Rhino 8.36 at 240/416 px unless noted. Rules that shipped now live as conventions in
[the UI README](../src/MoleHill.Rhino/UI/README.md); this file keeps the reasoning and what is open.

## Working agreement

- Prototype in the separate **MoleHill • UI design experiments** Penpot file.
  Keep **MoleHill • Rhino UI reference** and earlier comparison pages intact.
- Ask a couple of simple, related questions per turn (user preference updated
  2026-11-08). Keep each decision explicit; proceed independently only for very
  high confidence decisions.
- Share native Penpot screenshots in chat for remote review, including narrow
  layouts and both themes when affected.
- Obtain agreement on the design before implementing it in Rhino. Review actual
  Eto rendering and interaction before calling an item complete.
- A good reconstruction or technically centred text box does not establish that
  its visible text looks correctly aligned.

Tooling and local draft links: [Penpot reference](../tools/penpot/README.md) and
[self-hosted workflow](../tools/penpot/selfhost/README.md). Instance-specific URLs
and page IDs live in the ignored `tools/penpot/selfhost/design-draft.local.json`.

## Implementation status

Implemented structurally - on shared tokens and primitives, not card by card - in six steps:
tokens and guards; one card stack and one card header; units inside number fields and segmented
choices; schema sections; Results, source counts and tab fitting; then a per-card data pass.

| Steps | What | State |
| --- | --- | --- |
| 01–04 | Triangulate: sources visible; Additional Sources, Boundaries, Settings closed (UI-01–04) | Done |
| 05, 12–20 | Two-line header, order B, name over "Type · summary", bare type icon, icon actions (UI-08, UI-20–22) | Done |
| 09 | Softer borders | Declined; borders kept (UI-12) |
| 10 | Wording: Sources — n, contour labels (UI-13–19) | Done |
| 21–33 | Contours, Spot/Section/Profile annotations, comparison modes, sampling interval | Done |
| 34–36 | Spot Heights and Spot Slope as one card each, Points / Curves / Grid (B31, schema 34) | Done 2026-11-23; live-checked, incl. a document saved by 1.6.0 |
| 37 | Segmented controls for 2–3 modes; primary mode full row | Done |
| 38–40 | Report Table, Legend, Grade Callout | Done |
| 41–45 | Retaining Wall, Grade Line, Grade Path; units inside fields (UI-23) | Done |
| 47–54 | Remesh, Smooth, Displace (incl. Origin point row), Simplify | Done |
| 55–60, 62–64 | Scatter grouping and Block Mix, Plant/Orient Base | Done; Block Mix table details (57–59) not re-compared with the boards |
| 61 | Scatter Sequence: Random / Sequence inside Block Mix, ordered rows that drag to reorder (B32) | Done 2026-11-23; live-checked |
| 65–72 | Zones, Earthworks/Cut-Fill comparison modes, Catchments, Ponding, Waterflow, Gradient Compliance | Done; Earthworks results layout (67) not re-compared |
| 73–75 | Curvature/Landforms, ramp controls, Slope Units | Done |
| 76 | Status as a severity-coloured outcome line | Done |
| 77–78, 80 | Import/Export Terrain Definition and its preview | Done |
| 79 | Tab heading with Add on the same row | Done |
| — | Modal dialogs on one frame (`UiDialogs`); not a numbered step | Done 2026-11-23; live-checked |
| UI-P1 | Button captions "too high" | Closed 2026-11-23 by measurement: no native caption sits high (below) |
| UI-R6 | Runtime interaction checks | Exercised live 2026-11-23; four gaps found and fixed (below) |

## Open

- **UI-09 — earthy single-hue tab icons, three-region Zones glyph** (Steps 06–07). Not implemented; the
  tab icons are unchanged. **UI-10** (Annotation's text-label-with-leader glyph, Step 08) needs checking
  against the existing `TabAnnotation` icon, which already draws a leader.
- **Step 77's tab view toggles** still use plain characters, not drawn glyphs.
- **Floating windows** (Sculpt toolbar, Relax Route, Curve Review, Terrain to Curves / Scan to Terrain)
  were outside the review.
- **Design tooling follow-up**, at the end of this file.

## Decision record

The sections below are the review as it happened, kept for the reasoning behind each choice. Statements
in them that something is "pending implementation" predate the status above.

### Review order

Current review order, confirmed 2026-11-08:
1. Compact navigation: recognisable categories, useful tooltips, clear active state.
2. Reduce competing emphasis: review routine borders and badges; preserve the
   useful coloured card strips and retain strong distinction for selection,
   warnings and actionable problems.
3. Tighten labels: plain user-facing vocabulary and clear counts/frequencies.

Keep duplicate/delete buttons directly visible; the user is comfortable with them.
Do not prioritise hiding actions or section-heading polish ahead of these three topics.

### UI-R1 — Compact tab navigation — direction agreed, details pending

The user prefers compressed, single-row navigation and explicitly rejects wrapping.
Review Modifiers, Objects, Zones, Analysis and Annotation together within that
constraint, including icon recognisability, active-tab feedback and tooltips.

Labels may yield to icons as space becomes tight. Do not revisit multi-row layouts,
add tabs, rename categories or change their order implicitly. Compare refinements
in Penpot before choosing their exact appearance.

Source check: `MoleHillPanel.Tabs.cs` already switches all five labels off below
its width threshold and keeps one row. Zones/Analysis secondary view toggles have
help text, but `MakeTabHeader` does not attach name/purpose help to the main tab
panels or icons. Plan clear tooltips for each tab and keep secondary-toggle help
distinct. A static Penpot board cannot verify hover behaviour.

Step 06 now compares the current icons, simplified single-hue category icons and
the same simplified glyphs in neutral grey, at 240 and 480 pixels in both themes.
The user requested trying one colour per icon/category; exact glyphs and colours
remain candidates until reviewed. All versions keep a separate active-tab treatment
and preserve the Zones/Analysis secondary view toggles. Planned tab tooltips are
shown as reference text; live hover behaviour is not implemented in Penpot.

Step 07 revises that candidate after the user disliked purple: ochre for Modifiers,
clay for Objects, olive for Zones, sage for Analysis and sand for Annotation, with
darker equivalents in the light theme. Zones now has three regions (one solid,
two outlined). It compares the previous proposal, the earthy revision and a neutral
reference. Retain the earthy/no-purple preference in future tab work; these exact
swatches and the new glyph are still pending visual approval.

The user requested replacing the Annotation note/callout icon. Step 08 compares
the previous glyph with A: a pencil/drawing icon, and B: a text label with a leader
line. All three rows retain the earthy palette and three-region Zones glyph, so
only Annotation changes. **The user selected B: text label with leader line.** Keep
that glyph in subsequent proposals. The note/callout and pencil versions remain
comparison references only.

Blender reference: its Properties editor uses a vertical icon rail, and many UI
icons are monochrome shapes tinted by theme/category. This does not imply that all
icons share one grey or that MoleHill should automatically adopt a vertical rail.
The Step 06 comparison keeps MoleHill horizontal; a vertical layout needs a separate
decision. Sources: [icon construction](https://developer.blender.org/docs/features/interface/icons/)
and [Properties editor](https://docs.blender.org/manual/en/5.2/editors/properties_editor.html).

### UI-R2 — Card headers and action hierarchy — current review

Review the collapse control, title, enable checkbox, badge, drag handle and
duplicate/delete actions. Reduce competition while keeping common actions easy to
find. Treat the pinned Triangulate header separately from movable modifier headers.
User preference: retain clearly editable name fields. Multiple cards can have the
same type but different purposes (Retaining Wall cards named Terrain Walls,
Building Retaining Walls and Kerbs). Do not replace them with plain titles without
further agreement. **Custom names already exist in the product:** expanded cards
use a TextBox bound to the modifier label; collapsed cards show the name as text
with a summary. Preserve that existing feature. The expanded subtitle currently
comes from the descriptor (Retaining Wall: “Wall breaklines, optional batter”),
not a literal type-name line. Step 05 compares both treatments, keeping a separate Retaining
Wall type subtitle beneath each custom name. The user approved that name/type
separation, with an explicit caution against increasing collapsed card height.
Keep the name editable when expanded and preserve compact collapsed headers;
decide how to retain useful summaries without adding unnecessary rows. The example
does not approve a complete new header layout or always-editable collapsed titles.

Retain visible duplicate/delete buttons. The user does not see a reason to hide
them. Do not use an assumed need to declutter as grounds to move them into a menu.

The user requested reconsidering the drag grip, disclosure arrow, type icon and
enable checkbox order before implementation. Step 14 compares collapsed headers
at 400-pixel and 224-pixel card widths in both themes:
- Current: grip, expand, type, enable, name, duplicate, delete.
- A: expand, enable, type, name, duplicate, delete, grip at the right.
- B: grip, expand, enable, type, name, duplicate, delete.

Both candidates put the type icon next to the custom name. All variants keep
52-pixel two-line height, equal name space, the same summary, colours and actions.
The user selected **B** and requested improved vertical alignment. Review expanded name
inputs with that order, and preserve the pinned Triangulate exceptions
(no enabled reorder affordance or extra duplicate/delete actions).

Step 15 compares original B with an aligned collapsed header and an expanded name
input, at 400/224-pixel card widths in both themes. Use a shared first-row centre:
22-pixel action controls, 20-pixel type plate, 12-pixel checkbox, and centred text.
The expanded 26-pixel name field shares that centre too. Grip dots and disclosure
arrow are vectors, removing the original font-glyph baseline dependency. Header
height remains 52 pixels; summary/type stays on the lower line.

Read-only pixel measurements of native dark PNGs found the original grip/arrow
5 pixels high, name 3.5 pixels high and checkbox 1 pixel high relative to the
control centre. In the revised render the grip, arrow and checkbox match it;
collapsed/expanded name ink is within half a pixel. Light-theme output was visually
checked too. This whole-ink check did not establish optical x-height alignment.
Real Eto font
metrics/hit targets still need native verification; this does not close UI-P1 for
the unrelated body-button captions.

Step 16 follows the user's request to centre text by its lowercase x-height and
reduce the type icon. Native Source Sans Pro `xxxx` probes establish separate
offsets: bold 12 px needs no shift, regular 12 px moves down 1 px, and the 11 px
summary moves down 0.5 px. Re-exported probes are within 0.5 px of the intended
centre. Compare a 16 px type plate / 12 px glyph against the previous 20 / 14 px
sizes, preserving control slots, B order and 52 px header height. Dark/light,
400/224 px comparisons remain on a separate page; visual acceptance is pending.
Measure the actual Eto font during implementation rather than copying prototype
offsets. Interaction targets have not been exercised by this static comparison.

Step 17 reduces the type badge to 14 px / glyph 10 px, and duplicate/delete
buttons from 22 to 18 px with proportionately smaller glyphs. It preserves Step
16 x-height calibration, positions, 52 px height and previous comparison pages.
Review collapsed and expanded headers at 400/224 px in both themes. These sizes
are provisional, not verified Rhino Layers toolbar dimensions. An inspection-only
Rhino was opened and closed; native WPF capture was blank and automatic approval
review rejected starting MCPStart for runtime inspection. Exact Layers toolbar
button, glyph and display-scale measurement remains pending permission to start
the listener. General Rhino toolbar image-size documentation does not establish
the Layers panel toolbar's button bounds.

Follow-up source inspection verified the installed Rhino.UI.dll (decompiled
read-only with ILSpy; evidence in `.artifacts/rhino-ui-source/`):
`LayersPanel.CreateButtonPanel` sets `ItemSize = new Size(22, 22)` and zero item
padding. `LayersPanelViewModel` requests 16 × 16 SVG images. `ImageButton` also
defaults to image size + 6 and centres the image inside its bounds; its border
pen is absent in the normal/disabled state. These are logical sizes, with physical
rendering affected by display scaling. Thus Step 17's 18 px buttons are smaller
than Rhino's Layers buttons, not a size match. Consider retaining a 22 px target
with smaller visible artwork and hover-only borders in the next review. Runtime
measurement is no longer necessary to establish the declared dimensions.

Live follow-up (Rhino 8.36.26296.17001, owned slot aardvark / PID 34408): opened
the actual docked `Rhino.UI.DialogPanels.LayersPanel` and read all 11 toolbar
controls after layout. Every button reports Eto bounds 22 × 22 and WPF actual
size 22 × 22; every image is 16 × 16. Parent logical-pixel scale is 1 (100%).
Native screenshot confirmed the compact borderless panel toolbar, distinct from
the larger main toolbar. SoftwareOnly rendering was enabled in this disposable
process to resolve blank WPF capture. Evidence: `.artifacts/rhino-ui-source/live-toolbar.txt`.

Step 18 follows the user's request to remove duplicate/delete resting outlines.
Compare outlined and borderless versions using the same Step 17 smaller artwork,
18 px button bounds, 14 px type badge / 10 px glyph, and measured text alignment.
Keep the previous pages. Provide hover and keyboard-focus feedback during Rhino
implementation; the static resting-state prototype does not verify interaction.
The name input and enable checkbox retain their outlines.

Step 19 removes the type icon's surrounding background plate, following the
user's request for the glyph alone. Keep the existing 10 px artwork, colour,
centre and reserved space, borderless actions, 52 px header and text alignment.
Compare collapsed/expanded headers in both themes at 400/224 px; prior pages
remain intact. This removes the decorative plate, not meaningful strokes within
the icon artwork. Visual approval and native implementation remain separate.

Step 20 compares header groupings side by side at the user's request, preserving
the current option A. A: grip, disclosure, enable, bare type icon, name, actions.
B: grip, disclosure, bare type icon, name, enable, actions (Blender-style placement,
adapted to our controls). Keep equal name space and identical glyph/control sizes,
text alignment and 52 px height. Show collapsed/expanded 400/224 px examples in
both themes. User prefers the current two-group organisation in principle; this
comparison does not approve moving enable or changing the chosen B order from
the earlier order review (the A/B labels here refer only to Step 20).

Step 20 reviewed: user approved retaining the current grouping (option A in this
comparison): grip, disclosure, enable, bare type icon, name, duplicate/delete.
Keep enable on the left; retain the borderless action and bare type-icon direction.
Native implementation and interaction checks remain pending.

### UI-R3 — Reduce competing visual emphasis — second priority

Review disclosure arrow size, heading contrast, spacing, routine borders
and badges. Preserve the coloured card strips. Make collapsed groups easy to discover without making every element
equally prominent. Check light and dark examples before approving changes.
Focus first on softer routine borders and possibly less prominent badges, while
keeping selection, warnings and actionable problems distinct. Slightly brighter or
bolder collapsed headings are acceptable in principle but are a lower priority.

Step 09 compares current routine borders with softer ones on buttons, inputs and
checkboxes. Both sides retain the same card strips, layout, earthy navigation and
chosen Annotation B icon. The user found the difference hard to see and agreed to
keep the existing borders and move on. Step 09 is retained as a deferred comparison,
not an approved implementation. Do not reduce other state contrasts from it.

### UI-R4 — Progressive disclosure in the other cards

User priority update: review **Annotation first**, before Retaining Wall or other
modifier bodies. The complaint is excessive control density and little hierarchy.
Start with Contours to establish a small, concrete pattern, then review the section
cards (especially Cross-Sections) and the spot-label families. Inspect descriptor
rows together with bespoke source/origin/text-size/result rows; descriptor counts
alone understate the full UI. Report Table also needs a content-versus-appearance
review. Preserve existing parameter meanings, source workflows and annotation
style ownership. Proposed group names and initial expansion states need user
agreement before treating them as approved or applying them across the category.

Contours grouping approved: keep Interval, Base Elevation and Major Every visible;
place colour and Separate Layers in initially collapsed Appearance. The user
specified that **Labels enable itself reveals its settings**: a single checkbox
heading enables labels and shows Label Interval, Label Every and Decimals beneath;
off hides dependent rows while retaining their values. No separate Labels
disclosure arrow or independent expansion state. Include the existing read-only
Text Size/style feedback with the revealed label settings rather than leaving it
unrelated at the bottom. Step 21 compares Labels off/on plus Appearance expanded,
at 304/224 px card widths in both themes. Initial enable value follows the saved
definition; these drawings are illustrative states, not a change to defaults.
Static Penpot states do not establish interactive behaviour in Rhino.

Step 22 corrects Step 21's left-of-label checkbox after the user identified the
inconsistency: modifier booleans use `CreateCheckEditor` with a left-hand label
and checkbox in the property control column. Labels follows that existing pattern
while still enabling/revealing its dependent rows. Do not introduce a separate
left-hand checkbox convention for annotation settings. Card-header enable and
special selection lists are different contexts. Earlier comparison is preserved.

Step 23 removes the extra indentation from dependent Labels and Appearance rows,
following user approval to try a single property grid. Keep labels on a shared
left edge and all checkbox/input controls in the same column, including Colour.
Preserve the small group gaps, enable-to-reveal behaviour and Appearance collapse;
add no shaded subpanel. Width freed by removing the inset goes to the editors.
Compare 304/224 px cards in both themes against the preserved Step 22.

Numeric alignment review: current Penpot fields are left aligned. A live Rhino
8.36 check (owned PID 34624) confirmed `TextAlignment=Left` in the built-in viewport
Properties numeric fields and a shown Rhino-styled Eto NumericStepper, the control
MoleHill uses without overriding alignment. Evidence:
`.artifacts/rhino-ui-source/live-numeric-alignment.txt`. Centred resting values
would be a Blender-inspired alternative, not a correction to Rhino consistency;
no numeric alignment change has been approved or applied yet.

User accepted retaining Rhino's left-aligned numeric fields and asked to proceed.
Keep the Step 23 single-grid Contours direction. Next review: Cross-Sections,
including the bespoke Sources, Compare/Profiles and insertion-origin rows as well
as the 13 schema rows. Initial proposal for discussion: always-visible Sources,
Station Interval, Cross-Section Width and Vertical Exaggeration; collapsed
Comparison and Layout sections. Do not couple comparison enablement to expansion
or add a master Labels flag where the model has independent label flags.

Cross-Sections simplification review: user finds the workflow confusing and wants
more than regrouping. Source audit: drawing-array Grid Columns/Cell Width/Height
and elevation grid lines share ambiguous "grid" terminology; Label Stations is a
distance band on the drawn section, Section Title is its alignment station caption,
and Plan Labels marks stations in plan. Keep those meanings distinct. With no
explicit comparison reference, cut/fill uses the initial triangulation; "none"
therefore does not mean comparison off. Automatic cell sizing already uses zero
overrides; propose showing Auto with custom dimensions revealed on demand.
Proposals pending user agreement: a simple primary alignment/interval/width flow,
an explicit optional comparison choice, automatic arrangement by default, and
labels grouped by plan versus section drawing. Changing new-card cut/fill defaults
would be a behaviour change; preserve saved settings and do not implement it from
this audit alone. Investigate stale source help claiming only the first alignment
is used: the current cross-section builder loops all resolved alignment curves.

User approved comparison as optional and layout as automatic as possible, asking
for an easy insertion-point input. Step 24 proposes an always-visible Insertion
row: Auto/Picked status, Pick, and Auto (reset to automatic placement). Avoid XYZ
fields in the normal flow; keep full picked coordinates available in a tooltip.
Picking must accept a new point or cancel without losing the old placement.
Existing section insertion picker supports pick/reset; no new picking workflow is
needed. Keep saved manual placements and layout overrides intact. Comparison Off
is the proposed initial state for new cards, not a migration of existing cards.
Automatic cell sizing exists; automatic column selection still needs an explicit
policy and implementation (current GridColumns default is four). Layout Overrides
retains access to manual controls. Step 24 uses draft labels Section Width and
Vertical Scale, preserving total-width and vertical-exaggeration meanings; labels
and grid contents still need review. It shows static 304/224 px Auto/Picked states
in both themes, with earlier pages intact. No Rhino implementation shipped.

Step 25 follows the user's request for neatly right-aligned inputs: align control
right edges (source-picker group, numeric fields, insertion buttons and comparison
dropdown), retaining the agreed left-aligned numeric text. The numeric prototype
had an unused trailing unit gutter; extend its input and move the stepper to the
shared edge. Preserve real unit information during implementation; do not hide
nonempty units to achieve alignment. Earlier pages remain available for comparison.

Step 26 supersedes the separate insertion status/Pick/Auto controls at the user's
request. Follow MoleHill's source-picker pattern (`CreateSourceEditor` and
`CreateProjectToMeshTargetEditor`): empty point shows a Pick Point pill; once set,
show a clickable Picked pill with adjoining × clear action. Clicking Picked
re-picks; × clears the explicit point and restores automatic placement. Keep the
group's right edge aligned with other editors. Full coordinates belong in the
picked tooltip; pick cancellation preserves the existing point. The point is a
stored coordinate, not an object reference. No separate Auto button. Existing
insertion picker behaviour is reused, with the proposed control presentation
shown statically in Penpot; native interaction remains unimplemented/unverified.

Step 27: user approved **Paths** for the Cross-Sections curve input, replacing
Alignment in the prototype. These are ordinary Rhino curves sampled to create
perpendicular cuts at Station Interval; Section Width is the total cut width.
Use this meaning in help text and source-selection prompts during implementation.
Keep the `Sources` storage property and existing Objects/Layers picker behaviour.
This approval is specific to Cross-Sections; review other annotation labels by
their own input role. Earlier pages remain intact.

Next Cross-Sections review: contents of the placeholder Labels and Grid group.
Propose splitting by output destination: On Terrain (cut lines and plan station
labels) versus Section Drawing (station title, distance labels, elevation labels
and elevation grid). Draft labels pending user agreement. Preserve independent
flags; ShowPlanLabels does not depend on ShowCutLinesOnTerrain in the current
builder. ShowSectionTitle prints the alignment station as a title; LabelStations
draws the horizontal distance labels beneath the section. ElevationGridInterval also
sets elevation-label steps, even with grid lines off; do not hide its only control
behind grid enablement without resolving that shared dependency.

User chose **Plan Annotation** and **Section Annotation**; both start collapsed.
Use full-width clickable group headers with keyboard/focus support during native
implementation, rather than limiting expansion to the arrow or text. The longer
name alone does not determine the hit target. Step 28 shows both annotation groups
expanded and a second state with Layout Overrides open too, at both widths/themes.
Plan Annotation: Cut Lines, Station Labels. Section Annotation: Station Title,
Distance Labels, Elevation Labels, Grid Lines, Elevation Interval (Auto), and the
existing Text Size/style readout. Draft row wording distinguishes location and
purpose while retaining each independent saved flag. Elevation Interval controls
the common elevation step for labels and grid, so stays visible while either
needs it. Opening a group does not enable its controls or change saved values.
Layout Overrides shows proposed Auto values, including column selection that is
not yet implemented. Comparison remains Off in these examples; comparison UI
details remain pending review. This is not evidence of native click behaviour.

Step 28 reviewed positively; keep Plan Annotation / Section Annotation and the
expanded contents as the current direction. Next: Comparison interaction. Proposed
choice set for review: Off, Original Terrain (before modifiers), Another Terrain,
Rhino Geometry. Reveal only the relevant terrain selector or geometry picker for
the chosen source. Treat cut/fill shading as a separate setting, not synonymous
with drawing a reference profile. Preserve existing references, manually chosen
extra profiles and settings when hiding controls or changing display state.
Default shading behaviour on enabling comparison remains a user decision.

Comparison options approved: Off, Original Terrain, Another Terrain, Rhino
Geometry. User approved **Shade Cut/Fill starts enabled when comparison is turned
on**. Keep the shading checkbox available to draw the reference without shading.
Step 29 shows the four modes in paired examples. Original Terrain needs no picker;
Another Terrain reveals a terrain selector; Rhino Geometry reveals Objects/Layers
source controls filtered to supported mesh/surface geometry. Off reveals neither
reference controls nor shading. The reference name in the mockup is illustrative.
Keep Plan Annotation, Section Annotation and Layout Overrides initially collapsed.
Switching visibility must preserve references and user settings; do not migrate
existing saved cards to the new defaults. An explicit comparison mode is still
proposed UI/behaviour requiring implementation and compatibility handling.

Step 29 approved; proceed to Section Cut (Step 30). It accepts direct cut curves,
with no cross-section station interval or width. Draft input name: Cut Lines.
Keep Vertical Scale, picked/clear insertion and optional comparison as the main
workflow. Plan Annotation holds Section Marks only; ask whether to keep this
single-control group or show the checkbox directly. Section Annotation holds
Section Title, Distance Labels, Tick Marks, Tick Interval, Elevation Labels,
Grid Lines, Elevation Interval and the style-derived Text Size readout. Preserve
independent tick/label flags and existing interval meanings: zero suppresses
ticks while distance labels use an automatic interval when the shared interval is
zero. Do not describe zero as auto ticks or hide the shared field unconditionally.
Section Cut currently arranges multiple cuts in one row automatically; it has no
GridColumns/CellWidth/CellHeight controls, so do not borrow Cross-Sections layout
overrides. Step 30 compares the preserved 400 px reference with the proposal and
shows collapsed/expanded 224 px examples, in both themes. Reference snapshot is
restored by matching source archive hash and IDs, not regenerated or overwritten.
Native implementation, revised wording and the single-control Plan group still
need review. No Rhino source changed.

Step 31: user chose **Section Line** as the Section Cut input label and approved
showing **Section Marks** directly, removing the single-control Plan Annotation
group. Keep Section Annotation collapsed initially. Retain the shared Objects/
Layers source picker and existing support for multiple input curves; singular
wording does not change source cardinality. Earlier Step 30 and original reference
remain available. Native implementation is still pending.

Step 31 approved; Step 32 adapts Section Along Curve. Draft input label Path;
keep Sample Interval (geometry sampling), Vertical Scale, picked/clear insertion,
optional comparison and directly visible Section Marks. Section Annotation holds
Section Title, Baseline, Distance Labels, Distance Interval, Elevation Labels,
Grid Lines, Elevation Interval and style-derived Text Size. Preserve native
defaults (baseline, distance labels and grid on) and keep sampling separate from
label interval. This type couples its station ticks to distance-label visibility;
do not invent an independent Tick Marks flag. Distance Interval zero is automatic.
Multiple profiles already stack vertically with automatic spacing; do not add
Cross-Sections' cell/column controls. Source label and sampling control prominence
remain review choices. Both themes and 400/224 px collapsed/expanded states are
on a separate page. No Rhino implementation shipped.

Step 33 records Path approval and explores the user's request to tie sampling to
tolerance. Today the profile samples at a fixed distance with a minimum of ten
times Rhino's document tolerance. Proposed Sampling choices are Auto, which
derives spacing from the terrain's Detail Size and respects that lower limit,
and Fixed Interval, which reveals Sample Interval. Preserve existing cards'
stored intervals when introducing Auto. Keep drawing label intervals separate.
The user approved Auto following terrain Detail Size. The exact spacing rule
and accuracy on terrain features still need validation during implementation.
Earlier pages remain available.

Step 34 compares separate cards with the approved direction of one Spot Heights
card. Placement offers At Points and Along Curves. Interval appears for Along
Curves, and both modes share a collapsed Label Settings group. At Points also
accepts curve edit points. Implementation must preserve existing source
selections, formatting, block actions and saved card identity during migration.

Step 35 adds the requested On a Grid placement to Spot Heights and Spot Slope
alongside At Points and Along Curves. Grid mode exposes Grid Spacing and optional
closed boundary curves through Objects and Layers. Empty boundaries cover the
whole terrain. The mockup uses Flow Arrows' existing 5 model-unit spacing as an
example for both cards and keeps Units visible on Spot Slope. Preserve Flip Arrow
and the existing Flow Arrows sampling, direction and output settings when moving
that workflow into Spot Slope. Grid-based elevation labels are new behavior and
still need a builder. Validate saved-card migration and terrain coverage before
implementing the merged menu entries.

Step 36 replaces the placement dropdown in both mockups with a full-width
Points / Curves / Grid mode selector. Keep all three choices visible with one
selected. The selector sits above the mode-specific fields. Use the same order
at narrow and wide widths. The layout follows Blender's guidance for controls
that determine which settings a panel shows. Native keyboard interaction and
preservation of per-mode settings are implementation work.

Step 37 applies the same single-selection buttons to mode choices with two or
three options in the current context. Primary modes use the full row. Secondary modes retain their
field label and use the right-hand control column where space permits. At narrow
widths, put the label above the buttons so every caption stays on one line.

- Modifiers — Displace Source and Wrap, Remesh, Retaining Wall, Simplify, Smooth
  and Triangulate Contour Mode inside Settings.
- Objects — Scatter Source, Block order and Density Mode. Density Mode has
  three choices per source. Regions offer Count, Per Area and Min Spacing.
  Curves offer Count, Centre Spacing and Edge to Edge.
- Analyses — Gradient Compliance Level Rule and Route Rule, plus Curvature
  Show and Measure. The Curvature atlas also reflects the live ramp's existing
  Linear, Stepped and Constant controls.
- Annotations — Legend Layout and Section Along Curve Sampling. Spot Heights
  and Spot Slope already use mode buttons from Step 36.
- Curve Inspector — Off, Report and Warn for each of the four rules. The
  compact board shows the selectors. Keep the existing threshold and units
  alongside each rule in the form.

Keep dropdowns for four or more visible options and for document-dependent
values such as terrains, styles and presets. Preserve each option's stored value, tooltip,
conditional fields and per-mode settings. The comparison shows shortened
captions where needed at 224 px. The all-fields atlas boards illustrate control
placement, while the Section Along Curve boards show the actual Auto and Fixed
Interval field visibility. The previous design pages remain available.

Step 38 separates Report Table into a visible Insertion Point and Contents
checklist, plus an initially collapsed Appearance group. Automatic placement
uses the current position beside the terrain. Picking uses the shared Picked
and clear interaction. Contents shows only sections that have reportable data
for the terrain, using the report builder's eligibility before applying the
card's inclusion switches. Keep a hidden section's saved choice so it can
return when its data becomes available.

Appearance holds Slope Units, Rules, Column Gap, Row Spacing, Color and the
style-derived Text Size. The approved default for Slope Units is the terrain's
setting, with an explicit override per report. The mockup labels this
“Terrain Default” and treats it as inheritance. Today the general slope
preference is per user, and the report stores its own unit. Implementation
needs a terrain-level source and a legacy fallback. Preserve explicit values
on existing reports. The two examples show terrain-only and terrain with a
zone schedule and Earthworks results.

Step 39 keeps Legend tied to the analysis currently colouring the terrain.
Show the analysis name as a read-only Colouring value and preserve the existing
unavailable-state messages. Keep Insertion Point, Title and the Vertical /
Horizontal layout buttons visible. An empty Title uses the analysis name and
its unit where applicable. The mockup shows that automatic title as placeholder
text alongside an example with a custom title. Put Swatch Size, Strip Length,
Color and style-derived Text Size in initially collapsed Appearance. Keep
automatic placement and the existing picked and clear interaction. The colour
override affects text and outlines, while the swatches use the analysis colours.

Step 40 keeps the Grade Callout Objects and Layers picker visible under
**Curves**. Explain in the tooltip that the grade uses the two endpoints
projected onto the terrain. Put the generated count on the existing type and
summary line, for example “Grade Callout · 3 callouts”. Keep Results collapsed
initially. It shows how many source curves resolved and the minimum, average
and maximum grade from the last build. Keep rebuild and unavailable feedback
visible when needed. Appearance is also collapsed initially and holds
Decimals, Prefix, Suffix, Color and the style-derived Text Size. The mockup
uses illustrative counts and values.

Step 41 keeps Wall Curves and Max Wall Width visible in both Retaining Wall
modes. Breaklines Only shows the common fields and mode buttons. Grade Terrain
reveals the shared Fill Slope, Cut Slope and Max Distance. The Separate Top
and Toe Slopes checkbox reveals the optional overrides grouped by rail, with
Cut Slope and Fill Slope together under each heading. Each override inherits
the corresponding shared slope until explicitly set. The mockup shows one
example Toe Fill override at 1:3 and keeps the 52 px header in both widths.
Retain the existing slope unit input and the meaning of zero Max Distance.

Step 42 removes the individual slope override checkboxes from the mockup to
match Rhino's existing optional slope editor. Blank fields display their
inherited slope as muted placeholder text. Enter a slope to override it and
clear the field to inherit again. This applies to the shared Cut Slope and
each optional Top and Toe slope. The Separate Top and Toe Slopes checkbox
still reveals the two groups.

Step 43 carries the shared slope layout over to Grade Line. Keep Design Lines,
Fill Slope, Cut Slope, Max Distance and Grade Through Breaklines visible.
Separate Left and Right Slopes reveals Cut and Fill under each side. Use the
existing blank-field inheritance and preserve the user’s slope unit. The
tooltips define left and right when looking from the curve start toward its end.
The compact comparison uses a single Right Fill override at 1:3.

Step 44 keeps Centerlines and Width visible in both Grade Path states.
Variable Width reveals Width Edges and Edge Match Distance. Width retains
its label and supplies the width wherever an edge does not match. Display
zero Edge Match Distance as **Auto**, with a tooltip explaining the automatic
distance of four times Width. Positive values remain explicit distances.
Keep Fill Slope, Cut Slope, Max Distance and Grade Through Breaklines visible,
with the shared blank-field inheritance for Cut Slope. The example uses a
2 m width, which gives an automatic match distance of 8 m.

Step 45 moves unit labels inside the Grade Path input boxes and aligns both
edges of the fields on a shared grid. Keep values left aligned. Put the muted
unit at the trailing edge, before the stepper arrows when present. This is
the approved default for numeric inputs. Units still follow the model
or the selected slope preference. Preserve parsing and reserve space for the
unit so it cannot overlap editable text at narrow widths.

Step 47 uses **Even Triangles**, **Full Rebuild** and **Local Refine** in the
Remesh selector. Keep Breaklines, Edge Length and Crease Angle visible for
all three. Full Rebuild uses Edge Length when preparing breaklines and also
exposes Min Angle and Max Area. Preserve mode values and each mode’s settings
when switching. Use inline units for the numeric fields.

Show the mode tradeoffs in the button tooltips.

- **Even Triangles** makes triangles more even across the terrain. It can
  take longer on large meshes and may cross shallow retaining walls.
- **Full Rebuild** retriangulates the terrain while keeping wall edges and
  breaklines. The triangles may be less even.
- **Local Refine** refines triangles in place and preserves the existing
  topology. It is faster on large meshes and can leave less even triangles.

Step 48 places the Remesh mode buttons first in the card body, above
Breaklines and the numeric inputs.

Step 49 keeps **Smooth** and **Round Creases** at the top of the Smooth
card. Put Strength first in Smooth, followed by Iterations. Round Creases
shows Rounding Length and Crease Angle in the same area, with inline units.
Both modes show Boundaries and Protect. Rename Protect Hold to **Height
Protection** and put it immediately below Protect in Smooth mode.

Height Protection controls how much protected curves keep their original
height. At 0 they smooth like the surrounding terrain. At 1 their heights
stay fixed. Keep the saved value and the existing 0–1 scale. The example
shows Smooth above a collapsed Remesh card so it is easy to go from
remeshing to adjusting Strength in the bottom-to-top stack.

Step 50 moves Boundaries directly below the mode buttons in both Smooth
layouts. Strength comes next in Smooth, and Rounding Length comes next
in Round Creases.

Step 51 puts Noise and Image first in Displace. Boundaries, Height, Protect,
Feather and Hold Border are visible in both modes. Noise shows Noise Size,
Octaves, Roughness, Seed, Rotation and Origin. Image shows its file, Mapping,
Neutral, Wrap and Show Mapping. When the image has no mapping rectangle,
keep Tile Width, Tile Height, Rotation, Origin X and Origin Y visible.
A selected rectangle supplies those values and hides the manual fields.
Keep the saved manual values for when the rectangle is cleared. Preserve
the existing sampling and missing-source feedback.

Step 52 separates **Image Placement** from **Outside Image**. The placement
section shows an optional Rectangle picker and the image size and position
fields when no rectangle is picked. Image Width and Image Height refer to
one copy on the terrain, in model units. A picked rectangle supplies the
same placement and shows a short status. Outside Image offers Repeat,
Extend and No Effect. These map to the saved Repeat, Clamp and Once options.
Repeat copies the image, Extend carries the edge pixels outward, and No
Effect leaves the surrounding terrain alone. Rename Show Mapping to Show
Image Guides for the viewport overlay.

Step 53 replaces separate Origin X and Origin Y fields with an Origin
point picker in Noise and in Image without a rectangle. Store the picked
point's world X and Y in the existing fields and ignore Z. Use the existing
Pick Point, Picked and clear interaction. The Rhino point prompt also accepts
typed coordinates. Show the coordinates in the picked tooltip. Clearing
returns to world 0,0. A rectangle supplies the image origin while selected,
with the saved point available again after the rectangle is cleared.

Step 54 puts **Deviation**, **Vertices** and **Keep %** first in Simplify.
Each mode shows one target field. Deviation shows Max Deviation with
an inline length unit. Vertices shows Target Vertices as an integer.
Keep % shows Retain with an inline percent sign. The header summarises
the active target on its existing line.

In tooltips, explain that Deviation limits the vertical difference from
the incoming surface. Vertices caps the output vertex count. Keep %
converts the incoming vertex count to a cap rounded down. The count
modes report achieved error without imposing a deviation limit. Preserve
the existing diagnostics when the target cannot be reached.

Step 55 keeps Scatter's Block Mix visible. Show the included blocks, editable
relative weights and calculated mix shares. In curve sequence mode, use the
existing sequence semantics instead of a weighted share. Keep the block
preview and placed count available for review. For Within Region, Exclusions
is an enable checkbox that reveals its source picker and clearance. Turning
it off should retain the saved sources and clearance. An existing card with
exclusion references should retain its current behaviour when migrated.
Setback, feather and the option to keep whole blocks inside belong under Edge
Settings. Placement Filters, Transforms and Preview are draft groupings.

The comparison uses three named Scatter cards for a planting composition.
Consider a compact system list or named groups when a terrain has enough
Scatter cards to need them. Different planting strata should have independent
rules, and block variants with the same rules can share a mix. Geo-Scatter's
group masks and ecosystem relationships are separate ideas for later review.
[Geo-Scatter system list and groups](https://www.geoscatter.com/docs-scattering.html#scatter-groups)

Step 56 orders Scatter by where, what and how. Region or curve sources
come first, along with applicable exclusions. Block Mix comes next.
Pattern, density and other distribution settings follow the mix.

Step 57 uses a 26 px height for Scatter's labelled buttons and input
fields. The Objects and Layers buttons fill the same editor column as
Pattern and the numeric fields. Equalise the density segments and keep
the labels on one line. Align Add with the block weight boxes, leaving
space beside each block for its remove action.

Step 58 puts Add and the block weight fields on the same editor grid as
Pattern and Total Count. Each block gets a name row with its inclusion
checkbox, calculated mix share and remove action. Its labelled Weight
field sits directly below. Weight is a unitless relative value.

Step 59 replaces the two-row block entry with a 28 px table row. Show
the inclusion checkbox, name, editable weight, calculated share and remove
action together. Label the columns once and keep Add on the shared field
grid. The two-block and ten-block examples use the same row height.

Step 60 makes Block Mix collapsible and initially expanded. The collapsed
heading shows the block count and keeps Add available. Collapsing hides the
table and brings Pattern and Density into view without changing the mix.

Step 61 compares Random and Sequence for Along Curve. Show Paths directly
and put the mode selector inside Block Mix. Random shows weight and share.
Sequence uses the same 28 px rows with an inclusion checkbox, position
number and drag handle. Drag to reorder the repeating sequence. Preserve
Random weights when switching modes. Before shipping, resolve how to
preserve existing sequences that use zero weight to omit a block, since
Sequence hides the weight field.

For Within Region, compare Boundaries and Zones as explicit source modes
with one picker for the active mode. The draft Zones picker shows a
selection count. In Count mode, show placed versus target on the existing
card summary line, for example **42 of 48 placed**. Keep the existing
result breakdown available and only compare counts from the same build.

For Plant and Orient, keep **Release Selected** and **Release All** together
at the top level of the expanded card. Both actions keep the current pose and
stop the card from controlling the released objects.

Step 62 places **Show Base** and **Change Base** together under **Base**,
collapsed by default. **Variation** also starts collapsed. Sources, Heading,
Scale and Offset remain visible, with placement feedback on the card summary.

Steps 63 and 64 use bold section labels at the same size and contrast as field
labels, with a clear disclosure arrow and no extra row height. The treatment
was agreed on the Orient card. Step 64 compares it across the full Objects panel
for review in context before applying it to other tabs.

Step 65 shows the Zone card collapsed, open with **Last Build** closed, and
fully expanded. Put the zone's source picker and priority setting above
Last Build. Use the existing summary line for type and source counts, for
example **Zone · 2 objects · 1 layer**. Resolved plan area stays in Last Build.

Step 66 compares **Original**, **Terrain** and **Geometry** for Earthworks.
Original compares against this terrain's initial surface. The other modes
show their own picker. Boundary, Cut, Fill and Net stay visible in all
three modes, with the basis of the completed result marked as Estimated
or Exact. Preserve the existing comparison when migrating a saved card.

Step 67 keeps the existing select-and-copy workflow. Show Cut, Fill, Net
and calculation mode in one read-only multiline text box under Last Build.
Keep the text selectable and verify partial selection and copying the full
summary in Rhino during implementation.

Step 68 gives Cut / Fill the same **Original / Terrain / Geometry** selector
and keeps Boundary visible. Preserve the colour ramp and its mapping controls.
**Balance Line** and **Depth Contours** stay visible. Turning them on reveals
their own colour settings and, for Depth Contours, the interval. Keep the
last build's drawing counts with these outputs. Volume quantities stay on
Earthworks, in its selectable summary box.

Step 69 keeps **Flat Below** visible on Catchments and Ponding and uses the
existing summary line for the key drainage finding. Ponding keeps deepest
point, volume and wet area under **Last Build**, closed by default. Show
each drawing option directly and reveal its colour control when enabled.
Keep the depth ramp on Ponding and categorical colouring on Catchments.

Step 70 keeps **Start Points**, **Max Length** and **Color** visible on
Waterflow from Points. An empty Max Length shows **No limit** and maps to
the existing zero value. Positive lengths use the inline model unit. Show
the path count in the header and group termination details under **Last
Build**. Rejected starts are separate from path endings. A separate count
for paths that hit the length cap would require recording that stop reason.

Step 71 uses **Results** as the shared heading for computed values. Waterflow,
Zones and Ponding use the same disclosure style and keep their existing
expansion defaults. Earthworks keeps its selectable result visible under
the heading. Reserve **Report** for a report the user produces, such as
Report Table. When results need updating, explain that in a status message
where the result would normally appear.

Step 72 puts Gradient Compliance's route slope and run settings in
**Route Limits**, collapsed by default. Standard, Measure Over and the shared
Level Limit stay above the Level Areas and Routes sections. The shared limit
also defines route landings, so it remains available with only Routes enabled.
Use **Off / Show / Warn** for both rules. Show replaces the visible Report label
and keeps the existing amber treatment. Warn keeps the red failure treatment.

Step 73 puts **Landforms / Curvature** first. Keep **Landform Size** and
**Flatness Tolerance** visible in both modes. An empty size shows the resolved
Auto value when available. Landforms uses a three-swatch key. Curvature uses
**Mean / Along / Across** and a ramp. Tooltips explain the direction of each
measure. Keep the landform totals in a selectable Results text box.

Before redesigning the colour ramp, compare both of its current states against
`ColorRampControl.cs`, `ColorRampBar.cs` and `MoleHillPanel.ColorRamp.cs`.
Step 73 uses a placeholder dropdown for Mapping and omits the stop editor and
preset actions. Use the actual controls as the baseline for the next comparison.

- In the compact state, keep the histogram, colour strip and width-aware ticks
  together. The histogram takes its colours from the ramp. With no data, show
  the ramp without measured ticks. Keep **Edit ramp** and **Presets** reachable
  and preserve click-to-edit on the strip and disclosure on the header.
- Mapping offers **Linear**, **Stepped** and **Constant**. Stepped alone shows
  the band interval, including the automatic width and its reset gesture.
  Keep auto-fit and manual range rules for each analysis: free bounds for
  Elevation, auto-fit from zero and nonnegative manual bounds for Slope and
  Ponding, mirrored bounds for Cut / Fill and Curvature, and a fixed compass
  range for Aspect.
- In the expanded editor, keep selectable and draggable stop handles, exact
  values, colour swatches, positions, add/remove, even spacing, reversal,
  built-in and saved presets, and saving a named preset. Keep the two-stop
  minimum. Preserve live recolouring, commit on release and remembered stop
  selection and expansion across panel refreshes.

Step 74 restores the audited ramp controls in the Penpot comparison. It shows
Linear with stops closed, Stepped with an automatic interval, and Constant
with the stop editor and manual range open. Check preset selection, exact
entry and drag behaviour in Rhino when implementing a new layout.
The four icon actions in the stop editor are borderless at rest. Keep
their button bounds and provide hover and keyboard focus feedback.

Step 75 keeps **Slope Units** visible above the ramp. The dropdown retains
Percent, Promille, Ratio and Degrees. Elevation shows a height range. Aspect
uses **N / E / S / W / N** on the full compass and shows the exact bearing in
Results. Keep **Flat Below** visible and the ramp's mapping and preset
controls available. The fixed compass needs no auto-fit or range fields.

For bar charts and histograms across cards, leave a small gap above the
tallest bar so it has room to breathe beneath the heading or plot edge.
Use the existing plot height where practical.

Review actual modifier workflows individually. Keep frequent controls visible and
consider collapsible groups for less-used options. Do not apply Triangulate's
grouping mechanically to every card or judge normal screens by the all-fields atlases.

### UI-R5 — Labels, summaries and source feedback — current review

Cross-tab summary review: Step 13 uses picked-object and watched-layer counts
for source-based cards. Step 54 shows the active setting for Simplify. Steps
61–64 use placement outcomes for Scatter, Plant and Orient. Existing analysis
and annotation cards also mix settings, sources and completed results. Review
these together before adding a build result to Zone headers.

Proposed rule: keep the card type and one short summary on the existing line.
Use source counts for cards primarily defined by their references and the
active setting for cards defined by a mode and target. Use a completed result
when it is the main reason to scan the card, as with Scatter's placed count or
Earthworks' net volume. Keep stale results out of the current summary and
retain detailed results in the card body. The Zone card follows the source
count pattern.

Review technical or awkward copy such as “Bindings”, “1 source refs” and “Major
Every Nth”. Review whether source buttons communicate what is selected, and whether
collapsed card summaries convey useful state. Wording and feedback changes need
individual agreement and must follow the repository's modifier vocabulary.
Source check confirms “Bindings” and “source refs” in the object UI/catalog and
“Major Every Nth” / “Label Every Nth” in the annotation descriptors. Review proposed
wording in context, including singular/plural counts and the meaning of each
frequency. Do not change stored keys or parameter semantics merely to improve labels.

Approved: replace “Bindings — 3 source refs” with **“Sources — 3”** and put the
breakdown in the tooltip, e.g. **“2 objects, 1 watched layer”**. The earlier
“Source Count — 2 objects · 1 layer” proposal was too long and is not approved.
The total is saved object IDs plus saved layer paths; a watched layer can contain
multiple objects. Preserve that meaning rather than claiming the aggregate counts
all resolved objects. Handle singular/plural tooltip wording explicitly.

Approved: “Major Every Nth” → **“Major Every”**, retaining the integer field and
tooltip explanation that 5 makes every fifth contour level major.

Approved: “Label Every Nth” → **“Label Every”**, preserving integer contour-level
frequency; 5 labels every fifth level and 1 labels every level.

Keep **“Label Interval”**. The user rejected “Label Spacing”. Its tooltip should
describe distance along each contour curve and 0 placing one label per curve,
distinguishing it from the vertical Interval between contour levels.

Approved: **“Start Z” → “Base Elevation”**, the reference elevation from which
contour levels are stepped, and **“Split Major / Minor” → “Separate Layers”**,
routing major and minor contours to separate layers. Keep those meanings explicit
in tooltips. The main wording decisions from the initial review are now agreed.

These are label/tooltip changes only. Do not change keys, units, defaults or the
underlying calculations.

Step 10 consolidates the approved source/contour wording in a focused native
Penpot comparison, in dark and light themes. It uses illustrative source counts
and keeps the displayed contour values identical across sides. Tooltip wording
is shown as reference text; native controls, steppers and units still need review
in their full card context during implementation. This is a copy comparison, not
a new layout specification.

### UI-R6 — Runtime interaction and feedback

Keep build feedback in the existing **Status** disclosure below the toolbar.
The collapsed row shows a brief summary. Use the existing text colours to
distinguish warnings and failures, and let the user expand that same row
to read the details. Carry this pattern into the full panel comparison.

Step 76 shows the existing Status placement in a full panel walkthrough at
240 and 416 px. The examples show a build running during a modifier edit,
an expanded warning log beside the analysis workflow, and a review before
Bake. The log retains Copy Log and Copy Case. The warning and result values
in the mockup are illustrative.

Step 77 sketches borderless toolbar icon actions beside the current toolbar.
Keep a visible hover and keyboard focus state. The terrain selector menu
offers **Import Terrain Definition** and **Export Terrain Definition**.
The draft import preview creates a new terrain and flags source links that
need attention.

Step 78 limits source transfer to assigned Rhino layer paths. Omit picked
object IDs and geometry payloads. The import preview shows matching layer
paths and flags missing layers for reassignment in the destination document.

Step 79 compares the full panel before and after aligning the toolbar,
disclosures, tabs, and cards to common edges at 240 and 416 px. Centre
icons within compact tabs. Put **Add Analysis** on the same row as the
section title and align the import preview to the card column.

During Rhino implementation, verify tooltips, reasons for unavailable actions,
keyboard navigation, visibility toggle meaning, rebuild status, cancellation and
undo. Static Penpot boards cannot establish these behaviours. Record what was
actually exercised, including any native checks that could not run.

**Exercised live 2026-11-23** (Rhino 8.36, a terrain carrying one card of every type, all five tabs):

- **Tooltips.** Every Button, CheckBox, DropDown, Slider and TextBox had help. Gaps, fixed: the 140
  number-field step arrows had none (now "Increase / Decrease", naming the Up/Down keys); the 84
  disclosure arrows had none (now say what they open). After the fix all 222 icon buttons, 84
  disclosure arrows and 21 segmented controls explain themselves (the ramp's Linear / Stepped / Constant
  carries help per segment, set on hover).
- **Reasons for unavailable actions.** Disabled controls said what they do, not why they were off. Fixed:
  Untrack Selected / Untrack All add "nothing from this terrain is baked and tracked yet"; the colour
  ramp's range fields say Auto Range owns them (or, for a symmetric range, which field sets it) instead of
  "Drag to scrub".
- **Keyboard navigation.** Cards and sections could not be opened from the keyboard: the disclosure arrow
  only painted. It now takes focus (with the focus ring) and Enter or Space toggles its card or section;
  exercised on a card (Enter collapsed, Space expanded) and on Terrain Settings. The number-field arrows
  left the tab order (Up/Down in the field already step), which had tripled the tab stops per number.
  Segmented controls take focus and step with Left/Right; icon buttons take Enter/Space.
- **Visibility toggle.** The eye hides the mesh, analysis colours and annotations - everything the
  terrain draws (checked in the viewport). Its state tooltip said only "Terrain visible", now "Terrain and
  all its outputs shown. Click to hide them."
- **Rebuild status.** During a slow Remesh the Status hint read "Building · Remesh (2 of 8) · 0 s". The
  build message wrote "0,003 s" and "259,2 MB" with the Windows decimal comma (it formats on the UI
  thread); now written with a point like every other panel number.
- **Cancellation.** Force Reset Build stopped the running build within 0.7 s and Status read "Build reset.
  Rebuild to resume terrain outputs." The confirmation dialog itself is modal and was not driven; its Yes
  path (`ForceResetTerrainBuild`) was.
- **Undo.** Hiding and showing the terrain is one Undo step ("Show MoleHill Terrain"); Undo and Redo both
  restored the state. Card edits, Spot placement and Scatter reorder share the same undo path and were
  exercised earlier the same day.

Not exercised: the mouse drag gesture itself (scripted input only), and screen-reader names (Eto exposes no
automation names to set).

## Polish deferred by the user

### UI-P1 — Button captions still look too high — **closed 2026-11-23 by measurement**

Step 04 sets horizontal and vertical alignment to centre, but the visible glyphs
still appeared too high in the Penpot boards. The user approved the approach and deferred
this detail on 2026-11-08, asking for the cause before any offset.

Measured in native Rhino 8.36 (Segoe UI 9 pt, 100% scale) by counting caption ink rows in
captured buttons. Every caption - native buttons (22 and 26 px) and the drawn segmented
control alike - centres its full line box (ascent + descent), so the capitals sit within
half a pixel of the box centre, **slightly low**, and lowercase about two pixels low:

| Control | Box | Capitals | Gap above / below capitals |
| --- | --- | --- | --- |
| Reset, Edit…, Untrack Selected | 22 px | rows 7-15 | 6 / 5 |
| Add Modifier | 26 px | rows 9-17 | 8 / 7 |
| Segmented "Points / Curves / Grid" | 26 px | rows 9-17 | 8 / 7 |

Descenders ("Objects", "Apply") change nothing: those captions sit on the same rows. The native
buttons report `VerticalContentAlignment = Stretch`, but the caption still centres. No native
caption sits high, so no offset was added: one would make the real panel wrong to match a
reconstruction. The "too high" reading belonged to the Penpot boards' text boxes.

## Completion

When a change ships, fold its lasting conventions into the relevant UI README or
architecture reference, and remove the completed planning entry here. Keep pending
design choices distinct from approved direction and from verified implementation.

## Design tooling follow-up

Before relying on the generated linked library for interactive design edits,
verify that nested field label/value overrides survive Penpot editor sync. During
Step 09 preparation, the experiment file's original linked Modifiers page returned
generic field labels (Sources / Value / Enabled) instead of the expected row text.
The detached Step 04 comparison still held the correct labels and was used as the
Step 09 source. Investigate this separately; do not confuse static native export
validation with proven live component propagation, or overwrite the preserved
reference/comparison files while investigating.
