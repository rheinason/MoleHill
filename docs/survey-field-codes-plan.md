# B11 — Survey field codes to breaklines

Implementation plan. Status: **phases 1-5 delivered**, live-verified 2026-09-17 except the two Eto
dialogs and the toolbar button, which need a run from an installed build. Written 2026-09-17.

Backlog entry: `docs/backlog.md` → B11.

## What this is, and what it is not

Rhino already imports point files and LAS/LAZ point clouds, and those points feed a terrain like any
other points. A MoleHill CSV reader that only produced `Point3d`s would duplicate the host for no gain.

What Rhino does not do is read the **code** carried on a surveyed point — `EP`, `TC`, `TOE`, `CL` — and
turn coded runs of points into **linework**. That is the step that otherwise means re-drawing the
surveyor's figures by hand, and it is the whole feature. Points are the by-product; figures are the
product.

So the reader exists only because the codes ride along with the coordinates in the same file. If a
format ever delivered codes separately, the reader would go and the code machinery would stay.

## Two decisions taken up front

**The output reaches a terrain through layers, not through a definition.** The backlog entry read
"assigned to the terrain as sources — and like them it does not mutate a terrain definition", which are
only compatible one way: each code rule names a **destination layer**, the command writes there, and the
user assigns that layer through the ordinary source editor. `SourceReferenceSet` already holds
`LayerPaths` alongside `ObjectIds`, so nothing new is needed on the terrain side, and re-importing a
revised survey refreshes every terrain that references the layer with no reassignment at all. The
command never touches a `TerrainDefinition`, exactly like `mhDrapeCurve` and `mhValidateTerrainInputs`.

**The code table is per-user, in AppData, and the document keeps no copy.** `LayerTemplateStore` has a
document-embedded twin (`LayerTemplateDocumentStore`) because a document must *draw* consistently for
whoever opens it next — routing and appearance are read on every build. A code table is consumed once,
at import, and what it leaves behind is ordinary curves on ordinary layers. The document has nothing to
remember, so embedding a copy would add a second resolution order to explain and nothing to show for it.
Sharing between machines and offices is served by import/export buttons in the editor, the way the layer
template editor already does it.

## Coordinates: the trap to get right first

**PNEZD is not XYZ.** Point / **Northing** / **Easting** / Z / Description puts N in the second column
and E in the third — so the first two coordinate columns are **swapped** relative to the obvious reading,
and N→Y, E→X. ENZ is the other order. Getting this wrong produces a terrain that is silently
transposed about the 45° line: plausible-looking, entirely wrong, and nothing downstream can detect it.

Two mitigations, both mandatory in the first implementation:

- The import dialog shows a **live preview grid of the first ~20 parsed rows**, with resolved X / Y / Z /
  code columns, updating as the mapping changes. A format dropdown alone is not enough; the user has to
  see easting in the X column before committing.
- Named presets (`PNEZD`, `PENZD`, `ENZ`, `NEZ`, `XYZ`) set the mapping, but the mapping stays editable
  per column index, because real files carry extra columns the presets do not name.

Unit handling copies `DocumentCommandService.RunImportGeoTiff`: `ModelUnitGuard.TryGet` for the document
unit, and a source-file linear unit chosen in the dialog (m / international ft / US survey ft) scaled
into model units, the same shape as `TryResolveRasterUnits`. **No CRS reprojection**, and the summary
line says so, exactly as the GeoTIFF import does.

Placement is a larger subject and has its own section.

## Project base versus real world

A survey is delivered in real-world coordinates; a MoleHill document usually works in local project
coordinates. This is the single place where a survey import differs most from every other input command,
and it is not one transform — it is three separate questions with three different answers.

### Horizontal: go through `ResolveProjectBase`, never straight to the transform

`ProjectBaseCPlaneService.TryGetTransform(toProjectCoordinates: true, …)` is the right transform, but it
must not be called directly. `DocumentCommandService.ResolveProjectBase` does the part that matters
first: it detects **legacy named CPlanes** — `FOTM` written world→local by the old Python tools, and
`Georef` written local→world by the legacy C# — and offers Migrate / IgnoreOnce / DisableLegacy.

Skipping that step is not a missing nicety. A document carrying a legacy CPlane would import as though
it had no project base at all, producing geometry offset by the whole site translation. It parses, it
draws, and it looks exactly like a correct import until somebody measures against existing linework. The
survey importer calls `ResolveProjectBase` and then `TryGetTransform`, in that order, like every other
georeferenced path in the plug-in.

### Horizontal: no project base, and coordinates far from origin

`RunImportGeoTiff` drops real-world coordinates straight into the document when no base exists. That is
tolerable for a raster — it is one picture frame, and being far from origin costs it nothing. It is not
tolerable here: a survey in UTM sits at roughly 500,000 E / 6,000,000 N, and it arrives as thousands of
points that feed triangulation, `PointCloudProcessor`'s Z-aware dedup and every downstream tolerance
comparison, all of which are **absolute** distances.

So when no project base is found and the incoming coordinates exceed a distance threshold from the
origin, the import stops and offers three choices:

- **Set a project base at the survey's centroid** — routes into the existing
  `ProjectBaseCPlaneService.OrientDocument` path, so the document ends up with an ordinary project base
  that every other MoleHill command already understands. This is the recommended path and the default.
- **Continue in real-world coordinates**, with the magnitude stated in the warning.
- **Cancel.**

The point is that placement becomes a visible decision at the one moment the user can act on it, rather
than a silent default discovered later. Same principle as unmatched codes: report, offer, never assume.

### Vertical: an offset on the import, not on the project base

**The project base is XY-only by explicit design.** `TryValidateProjectBasePlane` rejects any plane whose
origin has a non-zero Z, with the message "vertical datum offsets are not supported", and the transform
is therefore a translation in XY plus a rotation about Z. Z passes through untouched.

That restriction stays, and B11 does not fight it. Instead the import dialog carries a **vertical offset**
applied to Z as the file is read, and stored nowhere.

This is not a workaround, it is the correct model. A vertical datum is a property of **the delivery**, not
of the site: levels arrive on AHD, ODN, NAP or on a site benchmark somebody set to 100.000, and two
surveys on two datums can perfectly well land in one document. A document-level Z datum could not express
that; a per-import offset can, and it is also what the user is actually holding — a number off the
surveyor's title block.

Lifting the `OriginZ == 0` restriction to make the project base a full 3D datum is a real idea, but it
touches validation, legacy migration, LandXML export, GeoTIFF import and every saved document. If it is
ever wanted it is its own backlog entry, and B11 must not absorb it.

### The round trip already works

Nothing new is needed to get survey-derived geometry back out to real-world coordinates. LandXML export
already applies `TryGetTransform(false, …)` before writing, and `mhApplySavedGeoref` converts a selection
in either direction. Importing into project coordinates therefore lands the data in the same frame as
everything else in the document, and the existing export paths take it back out unchanged. The vertical
offset is deliberately *not* part of that round trip — it is baked into the Z at read time, exactly as a
unit conversion is.

## Where the pieces live

Pure parsing and run-ordering are Rhino-free and belong in Core. `Core/Interop/` is already the foreign-
format folder (`LandXmlCodec`, `TinSurfaceData`, `ToposolidPointReducer`); a survey point file is that
and nothing else, so no new namespace is warranted.

### `src/MoleHill.Core/Interop/`

| File | Holds |
|---|---|
| `SurveyPoint.cs` | `int? Number`, `double X/Y/Z`, `string RawDescription`, source line number |
| `SurveyColumnMap.cs` | column index per field, plus the named presets |
| `SurveyPointFileReader.cs` | delimiter + column map → points, with per-line diagnostics |
| `FieldCodeRule.cs` | code, `FieldCodeRole`, destination layer, default-closed flag |
| `FieldCodeTable.cs` | the rule list, marker token spellings, lookup |
| `FieldCodeParser.cs` | raw description → code + figure number + markers |
| `SurveyFigureBuilder.cs` | the run-ordering state machine |
| `SurveyFigure.cs` | ordered point indices, role, layer, closed flag, arc spans |
| `SurveyImportResult.cs` | figures + loose spot points + diagnostics |

`FieldCodeRole` is `Breakline` / `Contour` / `Boundary` / `Spot` / `Ignore`. `Ignore` is a real rule, not
an absence — "we code trees and I do not want them" must be expressible as a decision rather than as a
gap that reads identically to a typo.

### `src/MoleHill.Rhino/`

| File | Holds |
|---|---|
| `Services/FieldCodeTableStore.cs` | `%AppData%/MoleHill/field-codes.json`, versioned, factory defaults on missing or corrupt, import/export — modelled on `LayerTemplateStore` |
| `Services/SurveyImportService.cs` | dialog orchestration, unit and project-base transform, layer creation, geometry creation, undo record, summary |
| `UI/FieldCodeTableEditorDialog.cs` | Eto grid editor for the table — modelled on `LayerTemplateEditorDialog` |
| `UI/SurveyImportDialog.cs` | file, delimiter, unit, column map, and the live preview grid |
| `Commands/MoleHillImportSurveyPointsCommand.cs` | `mhImportSurveyPoints` |
| `Commands/MoleHillEditFieldCodesCommand.cs` | `mhEditFieldCodes` |

Commands stay the usual twelve-line shell delegating to a service.

**`mhEditFieldCodes` is a variant of `mhImportSurveyPoints`**, so per the toolbar convention it belongs
on that button's `right_macro_id` rather than on a button of its own — the same relationship
`mhImportGeoTiffTerrain` has to `mhImportGeoTiff`. One new `bitmap_id`, one tile per strip.

**Destination layers are not `LayerRole`s.** `LayerRole` routes *generated terrain output*, whose
appearance the terrain owns so that preview and bake cannot drift. Survey linework is a user-owned
**input**: the user edits it, moves it, deletes it, and the terrain reads it back. Routing it through
the layer template would claim ownership the command does not have. Layers are created on demand through
`LayerCreationService` and then left alone.

## The run-ordering state machine

Points arrive in **file order, and file order is the ordering authority.** Point numbers are a label, not
a sequence — a surveyor renumbers, and a file merged from two days' work can carry numbers that decrease.
Sorting by number would reorder a figure into a zigzag.

The builder keeps one open run per key and closes it on an explicit marker, on a role change, or at
end of file. All four conventions ship in v1:

- **Figure-number suffix** (`EP1`, `EP2`) — the code plus a trailing integer is the run key, so two kerb
  lines on one site separate with no markers at all. The commonest convention and the cheapest to read.
- **Start / end markers** (`ST`, `END`) — explicit tokens open and close a run, for crews that do not
  number figures. The **spellings are editable table fields**, because every office differs; hard-coding
  `ST` would make the feature work for one office and fail silently for the next.
- **Trailing `-` continuation** — a code ending in `-` continues that code's open run.
- **Arc and close flags** — an arc token marks a point as an arc-through point, so three consecutive
  flagged points become a true arc; a close token loops the figure. Closed figures are what make a
  building footprint usable as a B8 **Hide** boundary directly, which is the reason this one is in v1
  rather than deferred.

Core records arc spans as index ranges on `SurveyFigure` and builds no geometry. Rhino turns a figure
into a `PolyCurve` of lines and `Arc` segments. That keeps Core Rhino-free and keeps the arc fitting in
one place.

## What the import declines to say

The code table is an invisible second rule sitting between the file and the drawing — the exact shape the
backlog's own test at the top of the file warns about. A command is not a modifier, so the test does not
bind, but the risk it names is real: the user cannot predict the output by looking at the input. The
answer is that **the import reports everything and drops nothing.**

- A summary line: points read, figures built, and a per-code count.
- **Unmatched codes are never discarded.** They become points on a `Survey::Unmatched` layer and are
  listed by name with their counts, so the missing rules are visible and a re-import fixes them. A code
  the table does not know is the normal first-run state, not an error.
- Runs still open at end of file are closed and reported.
- Malformed rows are reported with their line numbers, as a capped list rather than one message per line.

This is B4's lesson applied to an importer: a quantity nothing measured must never read as a measured
zero, and here a figure nothing matched must never read as a figure nobody surveyed.

## Delivery sequence

| Phase | Scope | Done when |
|---|---|---|
| 1 | Core reader, column map, presets | Tests cover delimiters, the PNEZD/ENZ swap, quoted descriptions, comment and blank lines, BOM, CRLF, and ragged rows |
| 2 | Core code table, parser, figure builder | Tests cover all four conventions, interleaved runs, an unclosed run at EOF, single-point runs, and decreasing point numbers |
| 3 | Rhino store and `mhEditFieldCodes` | Table round-trips through AppData; a corrupt file falls back to factory defaults; import/export work |
| 4 | Rhino import service, dialog, `mhImportSurveyPoints` | Preview grid reflects mapping changes live; geometry lands on the right layers under one undo record, with full rollback on failure |
| 4b | Placement | `ResolveProjectBase` runs before any transform; the far-from-origin prompt offers centroid / continue / cancel; the vertical offset applies at read time |
| 5 | Toolbar, docs, live validation | **Done.** `mhImportSurveyPoints` + `mhEditFieldCodes` share one Document-toolbar button (edit on `right_macro_id`), drawn from a `$designs` entry and checked at 16/24/32 px; the reader, figure builder, geometry builder and placement all verified in a disposable slot — see below |

Placement is split out as 4b because it is the part with the most ways to be quietly wrong, and each
needs its own live check: a document with a saved base, one with a legacy `FOTM` or `Georef` CPlane, and
one with none at all.

Phases 1–2 are pure Core and fully testable without Rhino, which is most of the algorithmic risk.

## Tests

`tests/MoleHill.Core.Tests/` for phases 1–2 — the project keeps test classes flat, one file per class,
not mirrored into source subfolders — naming `MethodName_Scenario_ExpectedResult`.
`tests/MoleHill.Rhino.Tests/` for the store round-trip; geometry creation uses `[RhinoNativeFact]`.

Phase 5's live check is the one that matters: automated tests cannot tell a transposed survey from a
correct one, nor an unreferenced one from a georeferenced one, because all of them parse cleanly. The
three placement documents named under 4b are the fixtures for it.

## Live verification — 2026-09-17

Run in a disposable Rhino 8 slot against a 20-row PNEZD file in UTM-scale coordinates carrying every
convention (figure suffixes, `ST`/`END`, an `AR` flag, a `-` continuation, a `ClosedByDefault` code, an
`Ignore` code, a bad coordinate and a row with no description).

**The worktree plugin cannot be loaded as a plugin.** MoleHill auto-loads at startup from the main
checkout, and a second copy shares its plugin GUID, so `PlugIn.Find` returns the *other* build — exactly
the stale-binary trap `docs/rhino-live-testing.md` warns about. The worktree assemblies were therefore
loaded into a separate `AssemblyLoadContext` that resolves RhinoCommon back to the default context, so
geometry types stay compatible, and the loaded MVIDs were checked to differ from the running plugin's.
Anything that needs the *command* registered — the two Eto dialogs, the command names, the toolbar
button — is **not** covered by this and still wants a run from a real installed build.

| Checked | Result |
|---|---|
| PNEZD column mapping | X = 500010 (easting, column 3), Y = 6100010 (northing, column 2) — not transposed |
| Figures | `EP1` (4 pts, arc flag on the third), `EP2` (3), `BLD` (4, closed with no marker), `TOE` (3, joined by `-`) |
| Roles | 2 spots; `TREE` counted as ignored, *not* as unmatched; layers resolved to the role defaults |
| Diagnostics | bad coordinate reported at its real line number; unclosed runs reported and closed |
| Geometry | arc bulges past its chord; collinear arc falls back to straight; duplicate shots dropped; out-of-range index skipped; closure; Z preserved — the eleven assertions the skipped unit tests make |
| Placement | UTM flagged far-from-origin, local grid not; centre rounded to 500000, 6100000; **the generated base plane passes `TryValidateProjectBasePlane`**, which rejects a non-zero origin Z |

**One defect found, and only here.** A row carrying coordinates but no description column was *rejected*,
because `RequiredColumnCount` counted the description. Exporters routinely omit that trailing field on an
uncoded shot rather than writing an empty one, so a good level was being discarded with only a line number
to show for it — the one thing this pipeline is not supposed to do. The description is now optional; a
genuinely short row is still reported. Re-verified in a fresh slot: 19 points instead of 18.

## Relationship to other entries

- **B6 (simplification)** is the other half of the real-survey story and is already delivered; this entry
  supplies the linework B6 must protect from decimation, since breaklines from field codes become
  persistent hard constraints like any other.
- **B8 (boundary roles)** consumes closed figures directly — a coded building footprint is a Hide region
  with no extra step, which is why the close flag is in v1.
- `mhInspectCurve` is where imported figures get checked, not here. This command reads a file; it does
  not judge what it read.

## Not in scope

- General point import. Rhino does it, and LAS/LAZ is the host's job.
- CRS reprojection, matching the stated limit of the GeoTIFF import.
- Terrain creation. The command produces geometry on layers; making a terrain from it is the panel's job.
- Writing a coded file back out. Setting-out export is a separate candidate over `Core/Reporting`'s CSV
  writer and has nothing to do with the code table.
