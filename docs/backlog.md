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

### B1 — Watershed / catchment delineation

**Reference:** Civil 3D surface watersheds — TIN-based basin segmentation producing drain targets and
catchment polygons (boundary-drain, depression, flat-area and multi-drain basins), exportable as 2D/3D
polygons for a downstream hydrology tool.

**What MoleHill has:** the *other* half. `WaterflowTracer` walks a single deterministic downhill path
through a 2.5D mesh; the `waterflow` analysis draws those paths. There is no notion of which faces
drain to which outlet.

**Where it fits:** a new **analysis** (`AnalysisDefinition` + `AnalysisTypeRegistry` descriptor). It
evaluates the terrain and its result is a measurement per region, so it is an analysis, not an
annotation — and it wants the colour-ramp apparatus to tint basins, which only analyses carry.

**Shape:** Core algorithm over the existing flat face/edge arrays, adjacent to `WaterflowTracer`.
Per-face steepest-descent to an outlet, then group faces by outlet; a catchment boundary is the union
of the grouped faces' unshared edges. Outputs: basin polygons on a layer role, area per basin, and the
flow path from each basin to its outlet.

**Notes / open questions:**

- Flat and near-flat regions are the hard part — a graded pad is one enormous flat area, and a naive
  steepest-descent gives every face on it a different arbitrary outlet. Needs explicit flat-region
  handling (gradient propagation inward from the region's outflow edges) before this is usable on
  *graded* terrain, which is the whole point.
- Depressions interact with B2; build them together, or build B2's sink detection first.
- Whether basins should be merged below a size threshold (Civil 3D asks for a minimum average depth and
  a merge tolerance) — almost certainly yes, or a real survey yields hundreds of basins.

---

### B2 — Depression / ponding detection and pond volume

**Reference:** the "did I just build a bathtub" check. Present in most civil packages as low-point and
sink reporting; TBC surfaces it through stockpile-and-depression volume reports.

**What MoleHill has:** nothing. `Ponding` appears nowhere in the source (the grep hits are Triangle.NET
internals). The tool will happily let a Grade Pad create a closed depression and say nothing about it.

**Where it fits:** an **analysis**, and very likely the *same* analysis as B1 rather than a second one —
a sink is a basin with no outlet on the terrain boundary. Worth deciding early: one "Drainage" analysis
with a depressions toggle, or two analyses sharing one Core basin-graph result.

**Shape:** find sinks (local minima, or basins with no boundary outlet), find each sink's spill
elevation (the lowest saddle on its watershed rim), then compute impounded volume and the ponded
polygon at that elevation. Volume integration is the same prism-over-faces sum the earthworks analysis
already does.

**Why it is high value:** it is the one analysis that catches a *design error* rather than describing
the design. Everything else in the Analyses tab tells you what you drew; this one tells you it is
wrong.

**Notes:** must be tolerance-aware — a 2 mm numerical dimple on a 200 m pad is not a pond. Threshold on
both impounded volume and depth, both unit-aware, both user-set.

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

### B4 — Volume and quantity reporting

**Reference:** Civil 3D's Volumes Dashboard and its "insert cut/fill summary" table; TBC takeoff
reports. The deliverable the client actually pays for.

**What MoleHill has:** all the numbers, and no way out of the panel. `ZoneAnalysisSummary` holds plan
area, surface area, elevation range, slope and cut/fill per zone, computed after overlap and priority
rules so nothing double-counts — and it is runtime display state that evaporates on reload. There is no
CSV writer anywhere in the tree.

**Where it fits:** not a new family. Two outputs off existing state:

1. **CSV export** of the per-zone and whole-terrain summary, plus the B2 pond schedule and B1 catchment
   areas once those exist. A command (`mhExportTerrainReport`), sibling of the LandXML export.
2. **A drawn summary table** placed in the document as ordinary Rhino text — the equivalent of Civil
   3D's summary insert. It must be generated output routed through `LayerRole` like everything else,
   and regenerating it must replace rather than stack.

**Notes:**

- Low effort, high perceived value. Probably the best value-per-day item on this list.
- Decide whether the drawn table is an **annotation** (it describes the terrain, it is drawing, it
  would get `IsEnabled` and follow the annotation style) or a one-shot command output. Leaning
  annotation — that makes it update with the terrain, which is the entire argument for a live model.
- Units: every figure is unit-bearing and must go through `ModelUnitContext`. A report that says "1250"
  with no unit is worse than no report.

---

### B6 — Surface simplification with a stated error bound

**Reference:** Civil 3D's surface simplification (point and edge decimation, to a percentage or to a
maximum change in elevation). The prerequisite for using real survey or LiDAR data at all.

**What MoleHill has:** the algorithm, in the wrong place. `Core/Interop/ToposolidPointReducer` already
does error-driven reduction — spatial extrema, then iterative insertion of the largest measured
reconstruction error until a tolerance or point cap is met — but it exists only to serve the Revit
Toposolid export path.

**Where it fits:** promote it to a **modifier**, early in the stack (it is input conditioning, like
Triangulate's boundary pre-filter). Parameters: mode (max vertical deviation / target point count /
percentage), the tolerance itself as a `ModelLength`, and whether constraints are exempt.

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

### B7 — Strengthen the Grasshopper surface components

**Replaces the "auto-balance cut/fill" item from the original analysis.** Auto-balance was proposed as
a solver that adjusts a Grade Pad's platform elevation to hit a net-zero volume target. In the Rhino
panel that is the wrong home: it implies MoleHill reaching out and rewriting scene objects, a flow only
the plant and project-object features use today and the least intuitive part of the product.
Grasshopper is already the iteration environment — a solver there is just a component that gets solved
repeatedly, with no hidden document mutation. **So the backlog item is the Grasshopper surface story,
and balance-to-target is one component inside it.**

**What MoleHill has:** 15 components. Eight are on `RegistryTerrainComponent` (Remesh, Grade Pad, Grade
Path, Mesh Smooth, Retaining Wall, In-Situ Stair, Slope Analysis, Mesh Areas), two are deliberate
bespoke escape hatches (TIN Surface for its per-instance cache, Mesh Collage for dual-mode and hatch
output), and the rest are the terrain-exchange and Toposolid boundary. The B2 parity migration in
`gh-parity-design.md` is done.

**The gap is coverage, not plumbing.** The Rhino panel has four content families; Grasshopper sees
essentially one. There is no contour, elevation, earthworks, waterflow, section, spot-height, sculpt or
retopo component. `ParameterDescriptor<TDefinition>` is now one generic descriptor shared by all four
families and rendered by one generic row builder — so a generator written against the generic
descriptor covers modifiers, analyses, annotations and objects at once, as `gh-parity-design.md`
anticipated.

**Candidate scope, roughly in order:**

1. **Analyses and annotations as components** — contours first (the most-asked-for output, and
   `ContourGenerator` is already a clean single-pass Core tracer), then elevation, earthworks,
   waterflow. These are pure Core calls on flat arrays; no `TerrainBuildService` dependency.
2. **Balance to target volume** — a component taking a Grade Pad's inputs and a target net volume,
   returning the platform elevation that hits it. Net volume is monotonic in platform elevation, so a
   bisection over the existing `PadGrader.Grade` plus the earthworks volume is enough; no optimiser.
   This is the honest, scoped-down version of Civil 3D's Auto-Balance and OpenSite's grading solver,
   and in Grasshopper it costs nothing architecturally — it is a loop around functions that exist.
3. **Make the bespoke two less bespoke only where it is free** — not a rewrite. TIN Surface's cache and
   Mesh Collage's dual mode are legitimate reasons to stay outside the spec framework.

**Notes:**

- Existing `ComponentGuid`s and port schemas are frozen. Any change is verified in real Grasshopper via
  `rhino-mcp`, per `docs/rhino-live-testing.md`.
- Watch the `PostConstructor` ordering trap recorded in `gh-parity-design.md`: the spec must be reached
  through the `protected abstract GhComponentSpec Spec` virtual property, never a field assigned after
  `base(...)`.
- This item is deliberately open-ended; split it into concrete entries once the first component lands.

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

### B9 — "Project To" modifier (conform to a target)

**Reference:** Blender's Shrinkwrap, and Civil 3D's Paste Surface as the strength-1 special case.

**Naming:** **not** Shrinkwrap. Rhino 8 already has a `ShrinkWrap` command that builds a hull mesh
around geometry — a completely different operation, and reusing the word would be actively misleading.
`Project To` is the working name (the Rhino `Project` verb, which users already read as "along the
construction/world axis"); `Conform To` is the alternative if `Project` reads too close to Rhino's own
curve-projection command. Pick before implementation; the concept is settled either way.

**What it does:** pull the terrain onto a **target** — another terrain, a mesh, or a surface — with a
**strength** of 0–1, limited to a **boundary** region, feathered over a falloff distance. Strength 1
inside a boundary is Paste Surface. Strength 0.4 is a partial blend. A narrow feathered ring along the
site edge is a tie-in to existing conditions, which is the case that comes up constantly and currently
has no answer at all.

**Where it fits:** a **modifier**, mid-stack (after grading, before finishing).

**Why 2.5D makes it easy:** the projection direction is always world Z, so none of Blender's
nearest-surface-point / normal-direction ambiguity applies — it is a height lookup and a lerp.
`MeshHeightProjector` is already the fast XY→Z lookup, and the target/strength/boundary/falloff
vocabulary is the one `SculptConstraintMask` established.

**Notes:** the target is document geometry, so it needs the same source-tracking treatment other
source sets get. Behaviour where the target does not cover the terrain must be explicit — no target
above or below means strength 0 there, not a hole.

---

### B10 — Swale / ditch, as a Grade Path mode

**What it is:** structurally a swale *is* a Grade Path with a narrow or zero flat top — the corridor top
becomes the invert, and the existing Cut Slope / Fill Slope batters become the channel sides. Grade
Path already carries Width, separate cut and fill slopes, and variable width, so the corridor cascade
needs nothing new.

**Explicitly rejected: authoring the invert relative to existing ground.** The obvious-looking feature
here is "0.3 m below existing, falling at 1 %", and it is the wrong move — it breaks the rule at the top
of this file. The centreline curve's Z is the design; a Grade Path whose invert is inferred from the
terrain cannot be read off the curve before the build runs, and it silently re-authors its own input.
The user already has good tools for building that curve — `mhDrapeCurve` puts it on the terrain,
`mhSlopeCurve` / `mhSlopeCurveSection` grade it, `mhOffsetFeature` offsets it keeping longitudinal
grade — and *that* is where the help belongs.

**Same argument for minimum longitudinal fall: report it, do not enforce it.** A swale that ponds is
broken, but a modifier that quietly lifts the invert to fix the fall is exactly the drift being
avoided. Make it a rule instead — `mhInspectCurve` already has Off/Report/Warn rules with
user-persistent unit-aware thresholds and a max-grade check; a **minimum** grade check is the same
mechanism, and B2's ponding analysis catches it from the other end, on the built surface.

**So the actual scope is small:**

- a **flat invert width** distinct from the corridor width (trapezoidal rather than V), and
- optional **asymmetric left/right side slopes**,
- with Cut Slope / Fill Slope relabelled to channel-side language in this mode.

Ships as a mode on the Grade Path modifier and its Grasshopper component, not a sibling modifier.
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

**Scope:** a CSV/point reader that carries codes through (column mapping for PNEZD/ENZ, delimiter,
units), a user-editable code table mapping codes to breakline / contour / boundary / spot roles, and
run-ordering rules (sequence numbers, start/end markers, the `-` continuation convention). Output is
ordinary Rhino curves and points, assigned to the terrain as sources — exactly like every other input
preparation command, and like them it does not mutate a terrain definition.

**Notes:**

- It is an input-preparation **command** (`mhImportSurveyPoints`), a sibling of
  `mhValidateTerrainInputs` and `mhDrapeCurve`, not a modifier.
- Code conventions are office- and surveyor-specific, so the code table must be user-editable and
  persistable, in the same spirit as the layer templates.
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
Aspect (B5) shipped and its entry is gone; B3 shipped and is kept only to record where it was wrong.)*
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
  interval. Nearly free once B4's CSV writer exists, and LandXML export already covers the
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
  over `GradePadTest.3dm`, look at the output, and only then decide whether it is a modifier, a one-shot
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
