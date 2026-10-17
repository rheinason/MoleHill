# MoleHill.Core/Routing

Route suggestion between picked points, behind `mhSuggestRoute`. Pure and unit-free: lengths are model
units, grades are rise over run. The product rule it serves: **modifiers follow drawn geometry; help
with drawing belongs upstream, in the curve.** Nothing here touches a terrain definition — the output is
a polyline the user edits and then assigns to a Grade Path.

- `RouteHeightGrid.cs` — the terrain as the search sees it: ground heights on a regular plan grid, NaN
  where there is no ground. Built by rasterising triangles straight onto the nodes in their bounding box
  (one pass over faces), taking the **top** surface where faces overlap. `AutomaticCellSize` is a design
  scale: `SpanCells` (200) cells across the routed distance, coarser only where a million nodes would not
  cover the domain (it was a thousandth of the distance, and a 250 m route searched at 0.43 m came out as
  sub-metre staircases); `MaximumNodeCount` caps an explicit size. `TrySample` is bilinear over the on-ground corners,
  with a minimum-weight overload that decides "is this point on the terrain".
- `RouteSuggester.cs` — the search and the alternatives. 16-connected A* (eight neighbours plus knight
  moves); the heuristic is `wDistance * straight distance`, admissible because every move costs at
  least that. A move's grade is the steeper of its two halves, measured against the ground at its
  midpoint, so a diagonal or knight move cannot step over a ridge or ditch between its end nodes; a
  move touching an off-terrain node is impossible. `SearchDomain` is the points' box grown by half its
  diagonal, room to swing round a hill or zig-zag up a slope. The search state is (node, arrival
  heading), 17 per node, so a **turn is priced**: `2R·tan(θ/2)` at the distance weight, the line a corner
  through θ occupies once rounded, with R the minimum radius (the corridor width when there is none).
  Without it a climb steeper than the limit was lengthened by zig-zagging a cell or two at a time (a
  2.8 m median leg on an 18% slope, `RouteSwitchbackTests`); priced, it switches back in long legs and a
  near-reversal is dear. That is also why `MaximumNodeCount` is 500,000.
  A leg straightened too short for its climb is straightened again **held to the length the climb needs**
  (`StraightenPlan`'s `minimumLength`, 5%, 15% then 30% over it, and `MakeRoom` refuses edits below it),
  before the sharp line and the raw grid path are tried; corners left under the radius there are counted
  as grade-limited.
- `RouteCost.cs` — the one pricing function, shared by the search and the straightening:
  `wDistance + wSteepness * min(r,1)^2 + wEarthworks * E(r)` per unit length, `r` = ground grade over
  the limit, `E` = `2x + 4x^2` on the excess `x = r - 1`. Ground steeper than the limit is not
  forbidden — the profile will cut and fill there — so Earthworks decides between cutting through a
  short bump (cheap) and going round a hillside (when cutting it would be dear).
- `RouteLegBuilder.cs` — one leg's grid path to a 3D polyline. **Straightening** is cost-aware string
  pulling: from each kept vertex, jump to the farthest later vertex whose straight chord costs no more
  than the path it replaces (sampled every half cell, infinite over a hole). On even ground this is the
  straight line; Douglas-Peucker was rejected because the grid's equal-cost staircases are not within a
  cell of the straight line, so a tolerance cannot remove them. Then the leg is sampled every half cell,
  given its profile, and thinned per straight stretch (vertical Douglas-Peucker) keeping every corner.
  If straightening shortens a leg past what its height difference allows, the raw grid path is tried.
- `PlanRadiusSimplifier.cs` — consolidates the plan, then makes room for the minimum radius before
  rounding. **Consolidating** drops or merges corners, cheapest first, while the rounded leg stays within
  **5%** (`ConsolidateTolerance`) of the cost the search settled on, so a line drawn to follow every fold
  of the ground becomes a few bends. A straightened plan
  keeps short segments with small turns between them (grid staircases round a hill, oblique crossings
  of a step), and a corner deflecting by θ needs `R tan(θ/2)` of both its segments, so rounded as drawn
  most of them came out at a metre or two. Where a segment is too short for its two corners, the plan
  is edited around it: a run of up to four corners **merged** into one where the segments either side
  meet (one arc across several short segments), a run **dropped** (a chord replaces a zig-zag), or the
  segment's corners **slid** apart along the segments either side or along its own line (which keeps
  the bearing it crosses the ground at). Every edit is priced as the leg will be drawn — rounded — by the
  same `RouteCost`, and none that leaves the terrain at the straightening's ground weight is taken. The
  cheapest edit over all segments without room goes first, **whatever it costs**: the minimum radius is a
  requirement, like the maximum grade. It used to stop once the leg was 2.7% dearer, and on rolling ground
  that left corners of 0.1–2 m under a 10 m minimum, which read as the option not working (2026-10-16);
  the extra earthwork now shows in the readout's cut and fill instead. `RouteHillySiteTests` holds it.
  Pricing is local (a corner's fillet depends on the vertices two either
  side), segments are sampled once into running totals and arcs once per shape, so trying an edit is a
  handful of lookups. Waypoints are leg ends and never move.
- `PlanFillet.cs` / `PlanPiece.cs` — horizontal curves. With a minimum radius, every corner of the
  (simplified) plan is rounded by a tangent arc (tangent length `R tan(θ/2)` on both segments). Where
  corners still want more of a segment than it has, their tangents on it shrink in proportion and each
  corner takes the smaller shrink of its two segments — the largest radius that fits, reported as a
  tight corner — so the plan is always G1; two tight corners that end up on one circle count once. A
  near-reversal stays a corner. Legs are rounded separately, so a waypoint is passed exactly and a turn
  there stays sharp. Rounding **shortens** a leg (an arc is shorter than its two tangents), so the
  profile is fitted again along the rounded plan and the result re-measured; a leg that no longer fits
  its height difference falls back to the rounded unsimplified plan, then to sharp corners (reported as
  limited by grade), then to the raw grid path.
- `RouteSpan.cs` — the exact output: straights, and arcs as rational quadratic spans (weight
  cos(sweep/2)) whose plan is exactly the circle, at most 15 degrees each. A span's elevation follows
  the angle almost but not exactly, so the leg builder measures the built spans every sixteenth of a
  degree (a whole degree read a hair under the curve's own steepest) and, if an arc span strays past
  the limit, refits with a fractionally tighter one. `RouteFilletSummary` carries the smallest radius
  used and the count of tight corners with their reasons: terrain-limited (a wider curve costs more
  than allowed or leaves the terrain), grade-limited (left sharp or rounded tighter because a wider curve would break the grade) and
  reversals.
- `GradeLimitedProfile.cs` — the profile: the L-infinity-optimal grade-limited fit to the ground with
  both ends held. Upper envelope `max(g_j - m|s - s_j|)` and lower `min(g_j + m|s - s_j|)` by two
  linear passes each; their mean is within the limit and splits departures evenly into cut and fill;
  clamping into the cone both held ends allow keeps it there. It equals the ground wherever the ground
  is already within the limit, so a route is **draped where it can be and graded only where it must**.
  Thinning keeps chords between fitted points, and a chord's grade is an average of the grades it
  spans — so the simplified output still obeys the limit, which the tests check segment by segment.
  Reusable for any "fit this curve to the ground within a grade" command.
- `RouteComplexity.cs` — how complicated a route is, as two counts: **plan** = total change of plan
  curvature x the reference radius (a bend at that radius counts 2, a sharp corner too, a long gentle
  curve little) and **profile** = total grade change / the limit (level to the limit counts 1). Asked for
  after a user compared a hand-drawn road (11 control points) with suggestions (~100): control points
  count how a curve is built, and counting the straights that trace a line scores a smooth curve as the
  most complex, so neither was used. On the reporting file the hand-drawn road scored 3.8, the
  suggestions 16–21 at Simplicity 0.
- **Simplicity** (`RouteRequest.Simplicity`, s >= 0): the turning radius the search prices turns at, the
  radius bends are rounded towards (never under the minimum, falling back to the minimum where the broader
  curve breaks the grade) and the profile thinning tolerance are all (1 + s) times their base. With a
  minimum radius the search also plans to 90% of the grade limit (`SearchGradeShare`): rounding shortens a
  route, and one planned to climb at exactly the limit kept its hairpins tight. On the reporting file:
  s = 0 gave complexity 15–17, s = 2 a spread of 3–18 (the plainest 7,500 m³ of earthwork against ~2,000
  for the closest to the ground), s = 4 4–11. `RouteComplexityTests` holds the direction.
- `RouteCurveFitter.cs` / `RouteSplineCurve.cs` / `FittedRouteCurve.cs` — the editable curve: the fewest
  cubic control points within a plan tolerance (`RouteRequest.CurveTolerance`, default 5% of the corridor
  width) and the profile tolerance, both widened by Simplicity. Legs are cut at waypoints and at corners
  over 20 degrees and fitted separately, ends held. Each piece is a **smoothing spline**: least squares
  plus a bending penalty, weighted as far as the tolerance allows by bisection, separately for plan and
  heights, with parameters corrected to each sample's nearest point. Plain least squares rang where
  curvature jumps (a 20 m arc read 4 m). Bending is measured against plan length (the plan weighted by the
  inverse cube of its speed, the heights by an arc-length Gram), because per-parameter bending let slow
  stretches kink (5 m in a 10 m hairpin) and steepened grades in bends. Knots are added where tolerance or
  the radius (10% slack, the overshoot where an arc meets a straight) is missed, then each is dropped if the
  curve still meets them. The **grade** is held by re-laying the profile along the curve's own plan with
  `GradeLimitedProfile` at a share of the limit shrunk by the measured overshoot (rounding grade breaks
  steepens what lies between), with up to three rounds of exactly inserted knots where that cannot reach.
  A line already over the limit (a route too short for its climb) is held to its own grade with 3% to
  spare instead: held to the limit it cannot be, and a relaxed route came out with 79 control points.
  Moving control-point heights directly was rejected (a metre of height error). At Simplicity 2 the test
  sites give 15-36 control points against 45-105 exact, within the grade limit. `RouteCurveFitterTests`.
- `RouteRelaxer.cs` (with `RouteRelaxSettings`, `RouteRelaxEvaluation`, `RouteRelaxIssue`,
  `RouteRelaxFailure`) — a sketched route relaxed over the ground, behind `mhRelaxRoute`. An elastic band
  of stations whose energy is the search's own cost (`RouteCost` through `ChordCost`, so it minimises
  exactly the score it is judged by), bending `R^2 theta^2 / l`, a penalty under the minimum radius,
  fidelity `Fidelity * l * (d/D)^2` to the guide line, and once Help me has seeded it a length shortfall
  per pin stretch (5% spare; applied to a plain sketch it snaked into 0.2 m bends). Moving a pin also moves
  the guide and the band near it with a soft-selection falloff `(1 - (s/r)^2)^2` along the line, ending at
  the neighbouring pins: moved alone, the pin was reached by a narrow spike from a band fidelity held on the
  sketch. Descent is a
  **Sobolev gradient**: finite-difference partials smoothed along the band at a scale cycling from a
  quarter of it to one station, step caps per scale and a line search, so the energy never rises.
  One-station-at-a-time descent crawled (a sketch over a hill moved a few metres in 2000 sweeps and came out
  wiggly). The band resamples between pins every eight sweeps. `Evaluate` gives the profile, metrics, the
  score against the sketch, and issues (cut, fill, climbs only more route can make, a route too short for
  its ends); `FitCurve` fits the editable curve from an evaluation without touching the band, so a host can
  fit while it steps. `HelpMe` runs the grid search through the pins with `RouteRequest.Guide` (a per-node
  penalty `((d - w)/w)^2` beyond the guide width; distances by a two-pass nearest-seed sweep), widening
  twice and then searching freely if the route comes out short, and reseeds the band. A sketch over a 12 m
  hill settles in about 200 sweeps (0.3 s), 76% cheaper with a tenth of the earthwork. Known limit, of the
  search rather than the band: 16 move directions give fixed grades on a uniform slope (the knight move on
  18% is 8.05%), so Help me can fail by a hair at a limit just under one of them. `RouteRelaxerTests`.
- `RouteMetrics.cs` — the readout: plan length, max and mean (rise over length) grade from the output
  segments, and cut/fill as profile-minus-ground over the corridor width (a flat prism, no side
  slopes), integrated every half cell.
- `RouteRequest.cs` / `RouteAlternative.cs` / `RouteSuggestionResult.cs` / `RouteFailure.cs` — the
  contract. A failure names the point or leg and, for a too-steep leg, the grade it would need; the
  host words it in its own units.

**Alternatives.** After a route is kept, nodes within the corridor band of its drawn line cost 50% more
and the search runs again. A candidate whose *drawn* line (two grid paths a few cells apart can
straighten into one line) still lies in an earlier route's band for 70% or more of its length is a
near-duplicate: dropped, its band penalised too, and retried, at most four attempts per wanted route.
Routes come back ordered by their unpenalised cost.

**Determinism.** Single-threaded, no randomness, and no hash order that decides anything (the
simplifier's caches are keyed lookups, never iterated): the same grid and request give the same arrays.
`RouteSuggesterTests.Suggest_SameInput_IsDeterministic` and `RouteMinimumRadiusTests` hold it.

**Minimum radius.** `RouteMinimumRadiusTests` holds the site it was found failing on live (2026-10-16):
200 m surveyed at 2.5 m, start (20, 180) to end (180, 20), 8%, 10 m. Rounded as straightened, the
three routes had 8, 3 and 7 corners under 10 m, the tightest 1.0–1.7 m; simplified first, one corner
in all three is under, at 2.5 m, where the route hugs the foot of a 0.5–2 m step the site's formula
makes along y = 30. The test asserts that every tight corner lies at one of those steps, that the
readout counts exactly the corners the curve shows tight, the grade along the curve sampled finely,
and determinism.

**Speed.** (Measured before the automatic cell became span/200; at the default a 1.5 km route now
searches at 7.5 m and is much faster.) A 1.5 km square at 1.5 m (1M nodes), rough ground, two legs, three alternatives: 3.6–4.8 s
on the maintainer's machine, about 6.2 s with a 10 m minimum radius (`Suggest_KilometreSite_IsInteractive`,
performance lane, Debug build). Nearly all of it is the A* search. Building a leg takes a few
milliseconds; simplifying it for the radius 3–8 ms per leg on the 200 m live site and 30–65 ms per
2.5 km leg of about a hundred straightened vertices on the kilometre site. The rounded case is slower
mostly because the second and third searches work around the corridors of rounded lines. The search
checks its cancellation token every 4096 expansions; the Rhino command runs it on a worker and Esc
cancels.
