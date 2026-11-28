# Local Penpot

For login, current comparison pages and review instructions, see the git-ignored UI design handoff,
`ui-design-handoff.local.md` in this folder (machine paths and instance IDs). Implementation status is
in the [Rhino UI design backlog](../../../docs/ui-backlog.md).

The official Penpot Compose stack, pinned to **2.18.6**, runs as Docker project
`molehill-penpot`. PostgreSQL and design assets use separate persistent named
volumes. Docker Desktop must be running with its Linux engine available.

```powershell
pwsh ./tools/penpot/selfhost/setup.ps1
pwsh ./tools/penpot/selfhost/start.ps1
```

The initial installation includes a **MoleHill** project with the design file imported.
Open [Penpot](http://localhost:9001) and sign in as `owner@molehill.local` using
the generated password in the ignored `credentials.local.json` file in this folder.
The project URL and native import statistics are in `project.local.json`.
Local development email is available
at [Mailpit](http://localhost:8025).

On a fresh installation, after generating the design reference, run:

```powershell
node ./tools/penpot/selfhost/init-project.mjs
```

This creates the local owner account, makes a MoleHill project, imports the native
file and verifies its page/component/colour counts through Penpot. Subsequent runs
reuse the saved account/project and verify the imported file without duplicating it.
It never resets an existing account password. Browser rendering and propagation of
component edits still need visual verification in Penpot.

For a read-only structural review and native PNG exports of representative boards:

```powershell
node ./tools/penpot/selfhost/review.mjs
```

Reports and exports go to `.artifacts/penpot-selfhost/review/`. The review does not
edit the design. Bounds warnings include text boxes that extend beyond their parent
even when their short visible text fits; inspect the PNGs before calling them clipping bugs.

After inspecting the native PNGs, `node ./tools/penpot/selfhost/promote-reference.mjs`
checks the structural/style audit and the twelve exported boards, restores the main
reference name, and marks earlier generated files with Archive names. It deletes no
files. It records the direct workspace URL in `project.local.json`.

## Design experiments

`node ./tools/penpot/selfhost/create-design-draft.mjs` creates a separate
**MoleHill • UI design experiments** file from the verified baseline archive.
It checks that the archive still matches the baseline revision and verifies that
the original file stays unchanged. The draft retains the 17 pages and 54 components.
Its independent file ID and workspace URL are saved in `design-draft.local.json`.
Subsequent runs retain that draft, including any manual design edits.

Make proposed changes in the experiment file and keep **MoleHill • Rhino UI
reference** as the comparison baseline. Agree on design decisions individually
before implementation. Reference refresh/promotion scripts do not target the draft.

Use the draft's **Approved / Numeric fields** assets for new numeric field
designs. The **Approved defaults — Numeric fields** page shows the components
and compact examples. Run `prototype-triangulate.mjs --numeric-defaults` to
create the page and assets in a draft or export the existing page.

`node ./tools/penpot/selfhost/prototype-triangulate.mjs` adds the approved Step 01
comparison page to that experiment file, without changing existing pages or masters.
Terrain Sources shows Points, Contours and Breaklines, in that order. Additional
Sources and Boundaries are collapsed in the default compositions. An expanded
composition documents their contents. These are editable static design states,
not clickable disclosure prototypes. Native comparison PNGs for 240/320/480 dark,
320 light and the expanded state are saved to
`.artifacts/penpot-selfhost/design-step-01/`. Re-running retains the saved comparison
page and exports it again, preserving manual changes.

Add `--settings` to create Step 02 on another comparison page. This compares the
approved source grouping with a collapsed Settings section containing Contour Mode
and every Peel Border control. Its expanded-state board shows those controls;
Additional Sources and Boundaries remain collapsed. Exports are saved separately
under `.artifacts/penpot-selfhost/design-step-02/`. Step 01 and the baseline remain intact.

`--core-layout` creates Step 03. Points, Contours and Breaklines stay permanently
visible under a plain Terrain Sources heading. Additional Sources, Boundaries and
Settings retain their collapsed defaults. The modifier stack runs **bottom to top
at every panel width**: Triangulate at the bottom, then Grade Pad, then Smooth.
This is the approved design convention for subsequent modifier proposals. Exports
are under `.artifacts/penpot-selfhost/design-step-03/`; earlier pages are retained.

`--button-layout` creates Step 04 from the approved Step 03 arrangement. It removes
the Terrain Sources heading and centres text-button captions on both axes using
native paragraph alignment and root vertical alignment. Input values, property
labels and section headings retain their original alignment. This button alignment
is the convention for subsequent design proposals. Exports are under
`.artifacts/penpot-selfhost/design-step-04/`; earlier comparison pages remain intact.

`--name-example` adds Step 05, a focused comparison of editable name fields and
plain names for three Retaining Wall cards: Terrain Walls, Building Retaining
Walls and Kerbs. Each has a separate Retaining Wall type subtitle. It uses the
actual badge and source header metrics, with editable static headers in dark and
light themes. Exports are under `.artifacts/penpot-selfhost/design-step-05/`.
The plain-name option is a comparison only; the user's preference is to retain
clearly editable name boxes. Existing pages are retained on subsequent runs.

`--tab-icons` adds Step 06, comparing current bitmap icons, simple category-tinted
vectors and those same vectors in neutral grey. Each theme shows 240-pixel compact
and 480-pixel labelled strips, with planned tooltip text below. All strips remain
horizontal and retain the two secondary visibility controls. Colours and glyphs
are candidates for review, not a new approved palette. Native exports are under
`.artifacts/penpot-selfhost/design-step-06/`. No live hover behaviour is claimed.

`--earth-tabs` creates Step 07, comparing the earlier proposal with an earthy
single-hue palette and a Zones glyph made of three regions, one solid and two
outlined. The revised category colours are ochre, clay, olive, sage and sand;
purple appears only in the labelled previous-proposal row. Dark/light native
exports are under `.artifacts/penpot-selfhost/design-step-07/`. Earlier pages remain intact.

`--annotation-icons` adds Step 08, comparing the previous Annotation note glyph
with a pencil (A) and a text label with leader line (B). Other glyphs and the earthy
palette stay the same. Native dark/light exports are under
`.artifacts/penpot-selfhost/design-step-08/`. The user selected **B: text label with
leader line**; carry B into subsequent prototypes. The other rows remain references.

`--soft-borders` creates Step 09 from the preserved Step 04 compositions. Both sides
carry the earthy tab icons and chosen Annotation B glyph; only routine button,
input and checkbox borders are softened on the right. Coloured card strips and
selection treatment stay the same. Native exports are under
`.artifacts/penpot-selfhost/design-step-09/`. The border proposal awaits visual approval.

The user subsequently deferred Step 09 and chose to retain existing borders.

`--wording` adds Step 10, a focused comparison of approved source and contour copy:
Sources with a numeric total and tooltip breakdown, Base Elevation, Major Every,
Separate Layers and Label Every. Interval and Label Interval remain. Dark/light
exports are under `.artifacts/penpot-selfhost/design-step-10/`. This isolates copy;
it does not replace the full card layout, unit or native-widget specifications.

`--full-review` adds Step 11, consolidating the agreed panel layout, earthy tab
icons (Annotation B), original borders/strips, name inputs, button-alignment
approach and approved copy. All five tabs have dark 240/320/480 and light 320
boards. Expanded type captions reuse their existing metadata line; collapsed
type/summary placement remains open rather than adding height.

The consolidated frames are detached static design states. Their labels, values,
paths and initial geometry are restored from the original `output/scene.json`
snapshot, avoiding generic captions returned by the linked source page. The source
archive hash is checked against the draft baseline. This does not repair or verify
live shared-component propagation. Native exports are under
`.artifacts/penpot-selfhost/design-step-11/`. `--export=Zones` or another substring
refreshes only matching PNGs and retains other export records.

`--source-summary` adds Step 12 from the preserved Step 11 frames. Source totals
move into the existing picker heading and the extra count row is removed.
Collapsed metadata shows type plus illustrative object/layer counts for source
cards, with singular/plural handling and no height increase. At 240 pixels, headers
that had no metadata line keep their existing height pending a separate decision.
All 20 native exports are under `.artifacts/penpot-selfhost/design-step-12/`.

`--fixed-headers` creates Step 13, a 52-pixel fixed two-line collapsed-header trial
at every width. Names stay on the top row; type/summary uses the lower row. Text
is conservatively ellipsized in the static prototype, with full text retained in
layer names and `tooltip-references.json`. Runtime must measure its actual font
and implement real tooltips. Expanded cards are unchanged. The page has 20 panel
boards and three narrow comparisons; `--export=Compare` exports those comparisons.
Exports and `header-audit.json` are under `.artifacts/penpot-selfhost/design-step-13/`.
The user preferred this layout. Ellipses appear only when required by available
width. A close-up can be exported using `--card="Grade Callout"` with
`--panel="Step 13 / Annotation / dark / 240"`; this uses Penpot's native 2× render
and preserves the other export records.

`--header-order` adds Step 14, comparing current control order with A (expand,
enable, type, name, actions, grip at the right) and B (grip, expand, enable, type,
name, actions). It shows collapsed Retaining Wall headers at 400 and 224 pixels,
in dark/light themes, with equal name space and 52-pixel height. Exports are under
`.artifacts/penpot-selfhost/design-step-14/`. The user selected **B** (left grip),
with vertical alignment to be improved.

`--align-headers` adds Step 15: original B, aligned collapsed B, and aligned
expanded name-field B, at 400/224-pixel card widths in both themes. Grip and arrow
use vectors; first-row controls and text share a centre. The 52-pixel header
height remains. Native exports and read-only pixel measurements are under
`.artifacts/penpot-selfhost/design-step-15/`. `png-ink.mjs` decodes RGB/RGBA PNGs
solely for QA measurements. Alignment review and native Eto verification remain pending.

`--audit-only` on `review.mjs` rechecks the imported graph, colour token/font links
and bounds without rendering images. The current reference has 17 pages, 54 main
components (39 cards + 15 fields), 12 colour roles with 24 light/dark token values,
one terrain gradient asset and 28 detailed dialog boards. Native rendered examples have been visually
reviewed; interactive browser gestures and live component synchronization remain outside
that check.

```powershell
pwsh ./tools/penpot/selfhost/stop.ps1
```

Stopping keeps the database and assets. Do not use `down --volumes` unless you
intend to destroy the local designs and accounts. Docker volumes are local to
this machine; placing these configuration files in Dropbox does not back them up.

`setup.ps1` creates random instance/database secrets in the ignored `.env` once
and preserves them on later runs. Keep that file with a database/assets backup.
It derives `compose.yaml` from the unmodified `compose.upstream.yaml` downloaded
from [Penpot's official repository](https://github.com/penpot/penpot/blob/main/docker/images/docker-compose.yaml).
The local changes bind both published ports to `127.0.0.1`, disable anonymous
telemetry, enable the 2.18 admin console, and replace the example secrets.
The upstream localhost email/session configuration is retained. This configuration
is intended for this PC; remote hosting needs a deliberate domain/TLS setup.

See [Penpot's installation and backup guide](https://help.penpot.app/technical-guide/getting-started/docker/).
