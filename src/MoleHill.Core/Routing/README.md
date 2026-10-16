# MoleHill.Core/Routing

Route suggestion between picked points, behind `mhSuggestRoute`. Pure and unit-free: lengths are model
units, grades are rise over run. The product rule it serves: **modifiers follow drawn geometry; help
with drawing belongs upstream, in the curve.** Nothing here touches a terrain definition — the output is
a polyline the user edits and then assigns to a Grade Path.

- `RouteHeightGrid.cs` — the terrain as the search sees it: ground heights on a regular plan grid, NaN
  where there is no ground. Built by rasterising triangles straight onto the nodes in their bounding box
  (one pass over faces), taking the **top** surface where faces overlap. `AutomaticCellSize` targets
  about a million nodes over the search domain but never goes finer than a thousandth of the routed
  distance; `MaximumNodeCount` caps an explicit size. `TrySample` is bilinear over the on-ground corners,
  with a minimum-weight overload that decides "is this point on the terrain".
- `RouteSuggester.cs` — the search and the alternatives. 16-connected A* (eight neighbours plus knight
  moves); the heuristic is `wDistance * straight distance`, admissible because every move costs at
  least that. A move's grade is the steeper of its two halves, measured against the ground at its
  midpoint, so a diagonal or knight move cannot step over a ridge or ditch between its end nodes; a
  move touching an off-terrain node is impossible. `SearchDomain` is the points' box grown by half its
  diagonal, room to swing round a hill or zig-zag up a slope.
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
- `GradeLimitedProfile.cs` — the profile: the L-infinity-optimal grade-limited fit to the ground with
  both ends held. Upper envelope `max(g_j - m|s - s_j|)` and lower `min(g_j + m|s - s_j|)` by two
  linear passes each; their mean is within the limit and splits departures evenly into cut and fill;
  clamping into the cone both held ends allow keeps it there. It equals the ground wherever the ground
  is already within the limit, so a route is **draped where it can be and graded only where it must**.
  Thinning keeps chords between fitted points, and a chord's grade is an average of the grades it
  spans — so the simplified output still obeys the limit, which the tests check segment by segment.
  Reusable for any "fit this curve to the ground within a grade" command.
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

**Determinism.** Single-threaded, no hashing, no randomness: the same grid and request give the same
arrays. `RouteSuggesterTests.Suggest_SameInput_IsDeterministic` holds it.

**Speed.** A 1.5 km square at 1.5 m (1M nodes), rough ground, two legs, three alternatives: about
3.6 s on the maintainer's machine (`Suggest_KilometreSite_IsInteractive`, performance lane). The search
checks its cancellation token every 4096 expansions; the Rhino command runs it on a worker and Esc
cancels.
