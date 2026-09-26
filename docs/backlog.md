# Backlog

Feature work that is agreed in principle but not scheduled. Ordered by tier, not by date. An item
leaves this file when it ships (the architecture doc and the folder `README.md` absorb it) or when it
is explicitly dropped (say so in the entry rather than deleting it).

Derived from a 2026-09-12 comparison against Civil 3D, Bentley OpenSite, Vectorworks Landmark and
Trimble Business Center. The framing question was *what do those tools have that MoleHill does not* —
so every entry below names both the reference feature and the MoleHill family it would join.

## The test every entry has to pass

**This input obviously makes that output.** What this rules out is narrower than it first looks, and the
distinction is worth stating exactly, because two of the entries below turn on it.

**A modifier with no Rhino geometry input is itself the input.** Its parameters *are* the design — there
is nothing drawn in the document for the result to contradict, so it cannot drift from anything. Smooth,
Remesh, Retopo, Simplify (B6), crest rounding (B12) and angle-of-repose relaxation all sit here. That a
modifier reads the terrain and changes it everywhere is not a problem; that is what a modifier is. Judge
these on whether the result is good, not on whether the flow is intuitive.

**A modifier with geometry input takes that geometry as the design**, and parameters only modulate how
it is applied. A Grade Path's centreline at 42.30 puts the corridor top at 42.30, readable off the curve
before the build runs.

**The break is when the two mix** — when a modifier accepts drawn geometry as its design and then
derives or overrides part of that design from somewhere else, usually the terrain. Now the drawn thing
no longer says what will happen, and the user has to hold a second, invisible rule in their head to
predict the output. "Take this curve, but put the invert 0.3 m below existing ground" is the failure
case; so is "take this curve, but lift it where the fall is too flat".

The corollary is that for those mixed cases **help belongs upstream, in the curve.** That is already how
the product works: `mhDrapeCurve` puts a curve on the terrain, `mhSlopeCurve` / `mhSlopeCurveSection`
grade it, `mhOffsetFeature` offsets it preserving longitudinal grade, and `mhInspectCurve` reports
whether the result breaks a rule. A command that helps draw the curve, plus a rule that reports on it —
never a modifier that silently moves it.

---

## Accepted

### B1 + B2 — Watershed / catchment delineation and ponding — **shipped**

**Shipped 2026-09-13**, as **two** analyses over one shared Core routing: **Catchments** and **Ponding**,
sharing `DrainageAnalysisDefinition` and a pass-scoped `BasinGraphCache` — the arrangement Earthworks and
Cut / Fill already had. B2's entry wondered whether it should be one analysis with a depressions toggle;
two won, because the questions are asked separately. "Where does water go" draws forty polygons on a real
survey; "did I build a bathtub" usually draws nothing and is worth leaving on permanently, and forcing
the first to be on to get the second would make the check that catches errors the one people switch off.

Kept here only for the four places these entries were wrong, since each cost a rebuild to find:

- **"It wants the colour-ramp apparatus to tint basins"** (B1) — half right, and the wrong half is the
  interesting one. A ramp maps a position on a continuum, so near values read as near colours; a basin
  index is a *name*, and basin 4 is not more than basin 3. Catchments colour categorically from
  `CategoricalPalette` and the card declares no ramp row. Ponding *does* ramp, because ponded depth is a
  measurement — the distinction is the whole reason the two cards differ.
- **"The lowest saddle on its watershed rim"** (B2) — the spill is the lowest *lip*, which is not the
  lowest point of the catchment boundary. A depression's catchment runs up to the watershed divide and
  usually reaches the terrain edge far below the depression itself, so the naive reading is an order of
  magnitude low. It is a bottleneck path: the minimum over routes out of the maximum crossing along the
  route.
- **"Threshold on both impounded volume and depth"** (B2) — depth only, in `ModelLength`. `ParameterUnit`
  has no volume member, this product does not show unlabelled numbers, and depth is the figure a reader
  can actually judge: "50 mm standing water" means something where a cubic-metre threshold has to be
  re-derived per site. Catchment merging is a *share* of the terrain (`Percent`) for the same reason, and
  because a share is scale-free.
- **"Flat and near-flat regions are the hard part"** (B1) — right, but incomplete. Flat regions were
  tractable. The unforeseen difficulty was **cycles**: wherever water converges on a vertex, the two
  faces sharing that vertex's opposite edge each fall towards it and each leave through their shared
  edge, so face-to-face routing loops. That is the bottom of every valley and the low corner of every
  graded pad, not a rarity. Cycles are the same phenomenon as flat regions — a connected set of faces
  with no outlet among themselves — and go through the same spill routine.

See `docs/architecture.md` → "Analysis vs
annotation", and `src/MoleHill.Core/Analysis/README.md`.

Not done, and deliberately: nested or merged ponds (one spill level per depression), pond volume at a
*stated* level rather than at the spill, and catchment export for a downstream hydrology tool — the last
is the boundary this backlog draws, and wants B4's CSV writer (now shipped) rather than anything new here.

---

### B3 — Difference surface — **shipped**, on Cut / Fill rather than Earthworks

**Shipped 2026-09-12.** Kept here only to record the one place this entry was wrong, since the mistake is
easy to repeat: it was written as "an extension of the existing Earthworks analysis", listing a cut/fill
colour map as the first thing to add. That map already existed, as the separate **Cut / Fill** analysis —
which resolves the per-face delta and already pins zero through `RangeShape.SymmetricAboutZero`. Both
analyses derive from `ReferenceComparisonAnalysisDefinition`; Earthworks owns the *volumes*, Cut / Fill
owns the *delta*.

So the genuinely missing half was the drawn output, and it landed on Cut / Fill, which already holds the
field and the ramp:

- **Delta contours** at a stated depth, stepping out from zero both ways — "cut deeper than 1 m" as a
  line — on a `CutFillContours` layer role.
- **The balance line**, the same field at exactly zero, on its own `BalanceLine` role because it is a
  decision rather than a depth. Omitted where the delta never changes sign; a site that is all fill has
  no line where cut meets fill, and drawing one at the shallowest edge would invent a boundary.

The mechanism is a per-vertex field overload on `ContourGenerator` — elevation contouring is now that
same marching-triangles pass with the field left null. Unmapped vertices carry NaN and every face
touching one is skipped. See `docs/architecture.md` → "Analysis vs annotation".

**Not done, and deliberately:** major/minor layer separation for delta contours (elevation contours have
it; nobody has asked for it here), and delta contour *labels*.

---

### B4 — Volume and quantity reporting — **shipped**

**Shipped 2026-09-14**, as both outputs the entry asked for, over one assembled report:
`Core/Reporting` (`ReportDocument` / `ReportTable` / `ReportColumn` / `CsvWriter`) holds the form,
`Services/TerrainReportBuilder` fills it from the last build's summaries, and the two outputs only lay
it out — `mhExportTerrainReport` as CSV (right-click on the LandXML export button), and the new
**Report Table** annotation as text and rules on a `ReportTable` role. Neither computes a figure, so
they cannot disagree about a volume.

The entry's open question resolved as it leaned: the drawn table is an **annotation**. It describes the
terrain, its output is drawing, and being rebuilt with the terrain is the whole argument — a quantity on
a sheet must not be left over from a design two revisions ago.

Kept here only for the three things the entry did not foresee, each of which shaped the result:

- **It cannot run with the other annotations.** Its input is every *other* stage's output, and zones run
  *after* analyses and annotations — so a report built in the annotation stage draws the previous build's
  zone schedule, or nothing at all on a first build. It runs last, after scatter, and uncached: a correct
  cache key would have to fingerprint every stage's results, which costs more than the few hundred text
  entities it lays out.
- **"Every figure is unit-bearing" is a statement about the column, not the cell.** A cell reading
  `1250.00 m²` is a string no spreadsheet can sum, and a bare `1250` says nothing; the unit belongs in the
  heading, which fixes both and is also what the drawn table wants. And there are two slope units in play,
  not one: the drawn table carries its own, because a drawing's unit belongs to the document, while the
  CSV follows the per-user `SlopeUnitPreference`, because an export is something you read.
- **The hard part is what the report declines to say.** A quantity nothing measured is blank, never zero
  — in a quantity report a zero is a claim, and "no ponding analysis is switched on" must not read as
  "the terrain holds no water". Sections that measured nothing are dropped rather than printed as empty
  headings, and the zone totals row sums only the zones that were actually measured and says so.

See `docs/architecture.md` → "Rhino: quantity reporting" and `src/MoleHill.Core/Reporting/README.md`.

**Not done, and deliberately:** a per-pond and per-catchment schedule (the drainage analyses summarize to
totals today, and a row per pond needs them to keep per-feature results), cost rates against quantities,
and page-fitting the drawn table — it is one column block, and splitting it across sheets is Rhino's
layout job, not the terrain's.

---

### B6 — Surface simplification with a stated error bound

**Delivered 2026-09-14.** The Rhino modifier now provides constraint-preserving maximum-deviation,
target-count and retain-percentage modes, backed by exact surface-overlay verification and honest
fallback diagnostics. Automated, scale and disposable-Rhino validation were completed; only larger release-characterization timings
remain open. The existing point reducer remains a separate Toposolid export implementation.

**Reference:** Civil 3D's surface simplification (point and edge decimation, to a percentage or to a
maximum change in elevation). The prerequisite for using real survey or LiDAR data at all.

**What MoleHill has:** the algorithm, in the wrong place. `Core/Interop/ToposolidPointReducer` already
does error-driven reduction — spatial extrema, then iterative insertion of the largest measured
reconstruction error until a tolerance or point cap is met — but it exists only to serve the Revit
Toposolid export path.

**Where it fits:** promote it to a **modifier**, early in the stack (it is input conditioning, like
Triangulate's boundary pre-filter). Parameters: mode (max vertical deviation / target point count /
percentage) and the active mode's value. Boundary and effective persistent constraints are always
protected; there is no exemption switch.

**Shape:** mostly a lift-and-generalise of the existing reducer, plus the modifier descriptor and
schema rows. The real work is the constraint interaction, below.

**Notes:**

- **Constraints must be exempt from reduction.** Breaklines, wall rails, grade-path road edges and the
  boundary are the terrain's meaning; decimating them is a bug, not a simplification. The persistent
  hard-constraint stack already knows which vertices those are.
- "Reduce a 2 M-point import to 50 k within a stated vertical tolerance" is the target use case, so the
  numbers matter — this wants a benchmark alongside `mhBenchmarkLargeTin`.
- Pairs with a future LAS/CSV import (see Candidates). That item is much less attractive without this
  one, and this one is useful on its own for GeoTIFF-derived terrain today.

---

### B7 — Grasshopper terrain workflow redesign

**In progress.** The live reference/contract foundation, terrain-aware Grade Pad and bracketed balance
helper are implemented. Disposable Rhino 8/Grasshopper validation covers independent bindings,
rename and panel-selection stability, `.gh` save/reopen, frozen lifecycle, failed-status holding,
and native `.3dm` source reopen. Full scope, remaining modifier/zone/Revit/usability phases,
legacy `.gh` component/wire upgrade by direct deserialization (the normal `GH_DocumentIO.Open` path
still stalls in automation), and other
acceptance checks are in
[the Grasshopper redesign plan](grasshopper-redesign-plan.md).
The plan's delivery-gate audit records which phase evidence is complete and which acceptance fixtures
remain.

Make the Grasshopper surface usable as a coherent terrain-design environment:

1. Robust, live terrain references in multi-terrain documents: select by readable name and retain
   stable identity through renames and panel selection changes.
2. Modifier-to-component parity with consistent terrain data, constraints, units, and diagnostics.
3. Dependable zones through GH editing, branching, partitioning, and export.
4. Verified, approachable Rhino.Inside.Revit workflows.
5. Icon and naming parity with native MoleHill.
6. Exploratory grading and cut/fill balancing, with outputs that let users reproduce a chosen design.

The main workflow is **minimal Rhino terrain → live/frozen snapshot → GH modifiers → chosen result**.
To return, users bake the successful inputs and manually recreate the native modifier, or bake the mesh
and use **Exact TIN Mesh** as a geometry checkpoint. The latter does not restore modifier history or
semantic constraint/zone data. Neither stack-to-graph nor graph-to-stack conversion is in scope.

Begin with a complete reference → Grade Pad → volume comparison
→ chosen-input workflow, then broaden coverage. B10's swale mode provides a subsequent exploration case.

B7 is an umbrella; each sub-item closes on its own evidence (see the plan's delivery sequence and
decisions D1–D14):

- **B7a — Reference, contracts, first workflow** (phases 1–2): `MoleHill.Interop` bridge, bound/follow
  reference with stale-while-rebuilding status and content fingerprint, in-place component upgrades
  (Terrain-or-Mesh inputs, Terrain output casting to Mesh), terrain-aware Grade Pad, volume comparison,
  bracketed-bisection balance helper.
- **B7b — Modifier coverage** (phase 3): remaining parity-matrix entries. Retopo terminal; Sculpt stays
  Rhino-only.
- **B7c — Zones** (phase 4): full zone workflow through editing, branching and partitioning. The
  native boundary-priority comparator now lives in Core; GH still needs the full zone semantics and
  must use that resolver before matched multi-zone acceptance.
- **B7d — Rhino.Inside.Revit preparation** (phase 4): configured example `.gh` files with the Python
  adapters pre-wired, labelled unverified in Revit. Revit-host verification is descoped.
- **B7e — Usability and release** (phase 5): icons, discovery, previews, performance, examples. Native
  artwork is embedded for the new modifier routes and a validated two-branch Snapshot example ships;
  final release review and representative modifier examples remain.

---

### B8 — Boundary roles

**Implemented.** Triangulate now exposes multi-source Outer, Hide, Show, and Data Clip rows. Data Clip
clips raw inputs exactly; the other roles conform and trim both final and baseline meshes after the
modifier stack, before analyses, zones, preview, and bake. Legacy Boundary references migrate to Outer.

**Reference:** Civil 3D's boundary types — outer, hide, show, data clip — and Blender's Mask modifier.

**Previous state:** one kind of boundary did three jobs at once. Triangulate's Boundary was a
work-region pre-filter and an explicit constraint loop, with border peeling as a separate mechanism.
Those different intentions were fused into one control, which is why "hide this region" had no answer.

**Where it fits:** not a new family — a **role on the boundary input**, the same move `LayerRole` made
for output. Proposed roles:

- **Outer** — the terrain's extent. What the current boundary means when it constrains.
- **Hide** — the region is not drawn and not part of the surface, but the input data underneath is
  kept. The "a building sits here" and "grey out the neighbour's land" cases, and categorically
  different from deleting the input.
- **Show** — a region punched back through a Hide.
- **Data clip** — discard input *before* triangulation, for speed, with no effect on meaning. This is
  precisely what the work-region pre-filter already does, currently indistinguishable from Outer.

**Why it is worth doing properly:** holes, masks, the work region and peeling stop being four unrelated
features and become one concept with a role, which is the same simplification that made output routing
tractable. It also unblocks the masking need without adding a mask.

**Notes:** Hide has to decide whether hidden faces are absent from analyses and volumes or merely not
drawn. Almost certainly absent — a hidden region is not part of the surface — but that makes it a build
concern, not a display one, and the answer must be the same for earthworks, zones and bake.

---

### B9 — "Project To" modifier (conform to a target) — **shipped**

**Shipped 2026-09-14.** The card accepts exactly one Rhino mesh or one other MoleHill terrain, then
Strength, optional Boundaries, and an inward Feather distance. No boundary means the whole overlapping
XY footprint. Multiple closed loops use even-odd nesting, which gives donut holes without a separate
hole input; the same contained feather applies around outer and hole rims. Missing target coverage keeps
the incoming terrain unchanged. Terrain targets fingerprint their final mesh, update dependants, and
cannot be selected where that would introduce a projection cycle.

**Reference:** Blender's Shrinkwrap, and Civil 3D's Paste Surface as the strength-1 special case.

**Naming:** **Project To**, not Shrinkwrap. Rhino 8 already uses `ShrinkWrap` for hull-mesh creation;
Project To says that this operation is a World-Z projection toward another terrain surface.

**What it does:** pull the terrain onto a **target** — another terrain or a mesh — with a
**strength** of 0–1, limited to a **boundary** region, feathered over a falloff distance. Strength 1
inside a boundary is Paste Surface. Strength 0.4 is a partial blend. A narrow feathered ring along the
site edge is a tie-in to existing conditions, which is the case that comes up constantly and currently
has no answer at all.

**Where it fits:** a **modifier**, mid-stack (after grading, before finishing).

**Why 2.5D makes it easy:** the projection direction is always world Z, so none of Blender's
nearest-surface-point / normal-direction ambiguity applies — it is a height lookup and a lerp.
`MeshHeightProjector` is already the fast XY→Z lookup, and the target/strength/boundary/falloff
vocabulary is the one `SculptConstraintMask` established.

The Rhino-mesh target uses ordinary source tracking. A MoleHill-terrain target reads its latest final
mesh directly and participates in dependent rebuild scheduling.

---

### B10 — Swale / ditch — **shipped**, as the Grade Line modifier

**Shipped 2026-09-16, and not in the shape this entry first proposed.** It was scoped as a *mode on
Grade Path*; it ships instead as a sibling modifier, **Grade Line**, which is the corridor grader run at
width zero. The reasoning: a swale is not the only thing a width-less corridor is good for. A crest, a
toe, a bench edge, a wall rail and a ditch invert are all the same geometry — a drawn line at an
authored elevation with ground battering away from it — so the general form earns its own card, and the
swale is what you get when both sides cut.

**What ships:**

- **Grade Line** (`kind: "grade-line"`), a modifier and a matching Grasshopper component. Design lines
  in, the curve's Z is the finished elevation, batters run to daylight.
- **Per-side cut and fill slopes** — four optional overrides behind an *Asymmetric Sides* toggle. This
  is the entry's "optional asymmetric left/right side slopes", generalised: each side gets its own cut
  *and* fill, so a ditch with a steep backslope and a flat foreslope is four numbers on one card.
- **A grading mode on Retaining Wall**, using the same core: each rail batters away from its partner,
  so a wall can daylight into the bank above and the fill below without a second modifier.

**The V-swale is Grade Line with both sides cut.** The trapezoidal case — a flat invert width — stays
Grade Path with a small width, which it already did; no relabelling of Cut Slope / Fill Slope was
needed, because in the line form the two sides are named directly.

**There is no per-side enable, and that is deliberate.** A line at an authored elevation is a
discontinuity: if one side got no batter, its faces would run from that elevation straight to whatever
existing vertices were nearest — an uncontrolled slope, not untouched ground. So both sides always
resolve and the control is the slope each leaves at. "This side needs no grading" is a measurement, not
a switch: where the terrain already meets the line the batter builder reports `Flat` and emits nothing,
which is also what makes stacked Grade Lines on `mhOffsetFeature` offsets compose into a compound
cross-section.

**Both original rejections stand, and the shipped feature honours them:**

- **No invert authored relative to existing ground.** The curve's Z is the design, full stop. The help
  belongs upstream in the curve — `mhDrapeCurve`, `mhSlopeCurve` / `mhSlopeCurveSection`,
  `mhOffsetFeature`.
- **Minimum longitudinal fall is reported, not enforced.** Nothing lifts an invert to fix its fall.
  `mhInspectCurve`'s Off/Report/Warn rules are still where a minimum-grade check belongs, and B2's
  ponding analysis catches it on the built surface.

Pairs directly with B1/B2 — the point of a swale is where the water goes, and the drainage analyses are
what prove it works.

---

### B11 — Survey field codes to breaklines

**Reference:** Civil 3D description keys and figure-prefix databases.

**Not general point import.** Rhino already imports point files and LAS/LAZ point clouds, and those
points feed a terrain like any other points — a MoleHill CSV reader would duplicate the host for no
gain. What Rhino does not do is read the **code** on a surveyed point (`EP`, `TC`, `TOE`, `CL`…) and
turn coded runs into breaklines, which is the step that otherwise means re-drawing the surveyor's
linework by hand.

**Shipped 2026-09-17** — `mhImportSurveyPoints` reads a coded survey file into figures on named layers,
`mhEditFieldCodes` edits the per-user code table, and the two share one Document-toolbar button. Still open: the two Eto dialogs and the toolbar button are unverified in a
real installed build, because a worktree copy of the plugin shares its GUID with the installed one and
cannot be loaded alongside it.

**Scope:** a CSV/point reader that carries codes through (column mapping for PNEZD/ENZ, delimiter,
units), a user-editable code table mapping codes to breakline / contour / boundary / spot roles, and
run-ordering rules (figure-number suffixes, start/end markers, the `-` continuation convention, arc and
close flags — all four in v1). Output is ordinary Rhino curves and points on named layers.

**Notes:**

- It is an input-preparation **command** (`mhImportSurveyPoints`), a sibling of
  `mhValidateTerrainInputs` and `mhDrapeCurve`, not a modifier. `mhEditFieldCodes` is its right-click
  variant, so it shares the toolbar button rather than taking one of its own.
- Code conventions are office- and surveyor-specific, so the code table must be user-editable and
  persistable, in the same spirit as the layer templates — but **per-user in AppData only**, with no
  document-embedded copy. Layer templates need one because a document must *draw* consistently for the
  next person; a code table is consumed once at import and leaves ordinary curves behind.
- **This entry was ambiguous where it mattered.** It said output is "assigned to the terrain as sources"
  and also that the command "does not mutate a terrain definition". Those reconcile exactly one way:
  each code rule names a destination **layer**, and the user assigns that layer through the ordinary
  source editor — `SourceReferenceSet` already carries `LayerPaths`. That also makes a revised survey a
  re-import rather than a reassignment.
- **PNEZD is not XYZ.** Northing is Y and Easting is X, so the first two coordinate columns are swapped
  against the obvious reading, and a mis-mapped file yields a silently transposed terrain that parses
  cleanly and that no test can catch. The import dialog must preview parsed rows, not just offer a
  format dropdown.
- **Project base versus real world is a third of this feature, not a line of it.** A survey arrives in
  real-world coordinates and the document usually is not. Three separate answers: horizontal placement
  goes through `DocumentCommandService.ResolveProjectBase` (**not** straight to `TryGetTransform`, or a
  document holding a legacy `FOTM`/`Georef` CPlane imports silently offset by the whole site
  translation); with no base saved and coordinates far from origin the import offers to set a base at
  the survey centroid rather than dropping a UTM point cloud into a tolerance-sensitive pipeline; and
  **vertical datum is an offset on the import dialog**, because the project base is XY-only on purpose
  (`TryValidateProjectBasePlane` rejects a non-zero origin Z) and a datum belongs to the *delivery*
  rather than the site — two surveys on two datums can land in one document. Making the project base a
  full 3D datum is a separate entry if it is ever wanted; it would touch validation, legacy migration,
  LandXML export, GeoTIFF import and every saved document.
- Still sits below B6 for the raw-point-volume case.

---

### B12 — Crest and toe rounding, as a Smooth mode

**Reference:** Blender's Bevel, applied to creases rather than to edges of a solid.

**What it is:** grading produces razor-sharp batter crests and toes; real ground does not. Round creases
sharper than a threshold dihedral angle by a stated radius, so graded terrain reads as landform instead
of CAD. None of the civil packages do this.

**Where it fits:** **a mode on the existing Smooth modifier**, not a new modifier. Smooth today is a
global Laplacian with no notion of where the creases are, which is precisely the thing this mode adds —
same modifier, same card, same place in the stack, with a mode switch between "smooth everything" and
"soften the creases". That keeps the stack from growing a near-duplicate entry and makes the
relationship legible: both are softening operations, differing in what they target.

**Shape:** `FeaturePolylineGraph` already extracts boundary ∪ creases at a crease angle for the
isotropic remesher, so crease detection exists. The new work is the rounding profile itself — displace
vertices within the radius toward a fitted arc across the crease — and it wants enough mesh resolution
to round into, so it inherits Smooth's existing "remesh first" regularity warning.

**Notes:** must **not** round constraint creases. A retaining wall top, a road edge and a kerb are sharp
on purpose, and the persistent hard-constraint stack already identifies them. A rounded wall top would
be an obvious bug, so this exclusion is part of the first implementation, not a refinement.

---

## Explicitly declined

- **A table-based curve / feature-line elevation editor** (Civil 3D's feature-line elevation editor: a
  grid of stations with elevation, grade in, grade out, insert PI). Declined as un-Rhino. Editing
  geometry through a spreadsheet is not how this product works, and it would reintroduce the second
  editing surface that `mhInspectCurve` was deliberately built to avoid. If stationed elevation editing
  ever returns, it belongs on the grading modifier — editing the definition and rebuilding through the
  pipeline — not in a grid.
- **Auto-balance as a Rhino-panel modifier.** See B7: reframed into Grasshopper rather than dropped.
- **Pipe networks and stormwater design**, **parcel and zoning layout**, **corridor assemblies and
  subassemblies**, **data shortcuts / multi-user referencing**. Out of scope — each is a product, not a
  feature. Exporting catchments (B1) so a real hydrology tool can consume them is the right boundary.

---

## Candidates — not accepted, not scheduled

Raised for consideration; none agreed. Kept here so they are not re-derived from scratch.

*(Swale, Project To, boundary roles and survey field codes were promoted to Accepted — see B8–B11.
Aspect (B5) shipped and its entry is gone; B1, B2 and B3 shipped and are kept only to record where they
were wrong.)*
- **Gradient compliance checking as an analysis.** Accessibility limits — running slope, cross slope,
  landing intervals, whatever local standard applies — evaluated over the graded surface and over path
  corridors, reported as pass/warn regions. MoleHill already has the pattern: `mhInspectCurve`'s
  Off/Report/Warn rules with user-persistent, unit-aware thresholds. This is that idea applied to a
  surface instead of a single curve. Landscape-specific, and a genuine differentiator — Civil 3D has no
  real equivalent.
- **Solar exposure / shade-hours analysis.** Per-face insolation over a date range, ramped.
  `mhSetSunNorth` means sun is already in the vocabulary, and the analysis family's ramp apparatus is
  ready. Something the civil packages do not offer at all.
- **Terrain statistics.** Min/max/mean elevation, plan and surface area, slope histogram, face count and
  triangle-quality distribution — read-only, in the panel. Cheap, and it makes B4's report richer
  without inventing new numbers.
- **Setting-out / stakeout point export.** A CSV of grid points or triangle vertices at a stated
  interval. Nearly free now that B4's `Core/Reporting` CSV writer exists, and LandXML export already covers the
  machine-control case.
- **Batch cross-sections at a stationed interval**, with cut/fill area per section and page-space
  layout. The section annotation already exists; what is missing is the *series* and the tabulation.

### Blender-modifier analogues

The modifier stack is already Blender-shaped, so Blender's list is a reasonable source of ideas — but
only where 2.5D terrain makes the idea *simpler* than it is in Blender, and where the host does not
already own the problem.

- **Crest and toe rounding** — promoted to **B12**, as a mode on the Smooth modifier.
- **Angle-of-repose relaxation** (the useful half of an erosion modifier — Blender's thermal-erosion
  addons). Enforce a maximum stable slope across the whole terrain: iteratively move material from
  anything steeper than the repose angle to its downhill neighbours until nothing exceeds it. Answers
  a question that is asked on every project — "nothing steeper than 1:3 anywhere" — and answers it by
  *fixing* the terrain rather than colouring it red, which is all the slope analysis can do today.
  Constraint-aware (walls and road edges exempt) and unit-aware through `SlopeInput`.
  **Flagged as worth exploring, with the outcome genuinely uncertain.** The uncertainty is entirely
  about the *result*, not the flow: this takes no geometry input, so its parameters are the design and
  there is nothing drawn for it to drift from — by the test at the top of this file it is in the clear,
  exactly like Smooth or Remesh. The open question is whether relaxation produces a surface a designer
  would keep. It moves material everywhere at once, so it may well satisfy the slope rule and still look
  soft and lumpy. Worth a throwaway prototype on a real graded scene *before* any modifier work: run it
  over a real graded scene, look at the output, and only then decide whether it is a modifier, a one-shot
  command, or a bad idea. If the global version disappoints, the fallback is a boundary-limited one —
  same algorithm, smaller claim.
- **Full hydraulic erosion** is the same machinery run for naturalism rather than compliance —
  worthwhile for restoration and mounding work, but strictly after the repose version, which is the
  part with a client asking for it.
- **Solidify / terrain base.** Give the terrain thickness: vertical skirt down to a flat base at a
  stated elevation or depth, producing a closed solid. Wanted for physical models and 3D print, for
  presentation blocks, and for renders where an infinitely thin sheet reads wrong. Probably a **bake**
  option rather than a modifier, since the result is terminal geometry and nothing downstream should
  grade it.
- **Global noise / detail field.** The Sculpt Noise brush is local and manual; there is no way to add
  fractal detail across a region. Useful for breaking up the over-smooth interpolation between sparse
  survey points and for visualisation. Cheap, because the durable data is the sparse world-XY
  displacement field that `Core/Sculpting` already owns — a noise modifier writes into the same kind of
  field instead of a new one, and inherits its stackability and constraint masking for free.
- **Displace — considered and skipped.** The texture, mapping and sizing apparatus is a large amount of
  work Rhino already does, and the two things a terrain would actually use are both present: a height
  image is the GeoTIFF DEM path, and a free-form vertical offset field is Sculpt. Revisit only if the
  noise item above turns out to want a general driver.
- **Mask, Decimate, Remesh, Smooth** map onto Boundary roles, B6, the Remesh modifier and the Smooth
  modifier respectively — recorded here so the mapping is not re-derived. **Lattice, Array, Mirror,
  Boolean** do not survive the move to 2.5D in a form worth having.
