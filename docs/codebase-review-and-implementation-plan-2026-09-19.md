# Codebase review and implementation plan

Date: 2026-09-19  
Reviewed revision: `fc1a90b`, initially clean working tree  
Status: deliveries 1-5 implemented with acceptance gaps; delivery 6 is characterization only.
The edit-to-visible latency workstream has started: the trace is built and first measurements are
recorded in architecture.md. Branch `review-plan-2026-09-19`. Start at
**Handoff** for where to pick up, and **Implementation status** for implemented versus verified

## Handoff

Last worked: 2026-09-19. Branch **`review-plan-2026-09-19`**, cut from `retaining-wall-pinch` at
`fc1a90b`. Working tree clean; nothing is in flight or half-applied.

**The branch is not based on `main`.** Its merge base with `main` is `816aff3`, so it carries the five
retaining-wall-pinch commits (`d7627e4`..`fc1a90b`) underneath this work. Review or merge that branch
first, or rebase this one, otherwise a PR against `main` mixes two unrelated changes.

Implementation/history commits through `0f79354` (some deliveries span multiple commits):

```
e32a38a  Add 2026-09-19 codebase review and implementation plan     (this doc, as the baseline)
97b152e  Refuse future-schema terrain documents before normalization (R01)
920dcdf  Finish the packed-edge comparer migration and guard it      (R02)
5e3a3fd  Add declared build inputs and separated validation lanes    (R08/R09)
35abb9a  Keep the package lane working across host-path properties   (R08/R09 fix)
3812ae4  Observe cancellation inside the zone splitter's phases      (R03)
2c35c4b  Bound spatial-index membership growth and measure it        (R04)
f8fde7c  Characterize build-result geometry ownership                (R05, table only)
0f79354  Record implementation status and repair source navigation   (R13)
```

At progress audit revision `1eb436f`: 50 files, +2,865 / -175 against `fc1a90b`.

### Resume here

```powershell
git checkout review-plan-2026-09-19
./validate.ps1 managed     # expect 974 / 694 / 39 passing, 130 + 15 skipped (all native)
./validate.ps1 warnings    # expect 9 projects compiled, owned-code warnings 0
```

Then the two lanes this session could not run, on a machine with Rhino 8 installed and **Rhino closed**
(it locks the `.rhp` and `.gha`):

```powershell
./validate.ps1 native      # 130 Rhino + 15 Grasshopper tests currently skip; this makes them run
./validate.ps1 perf        # Release, serialized; establish measurements for these changes
```

These lanes remain unverified in the implementation handoff. Running them alone does not close all
acceptance gaps: representative before/after grading timings, cancellation latency/peak memory,
production index distributions and coarsened query cost still need targeted measurements. Their
outcomes should not be assumed. Earlier reviews contain historical timings, but those do not measure
this branch's changes.

### Next action

**The edit-to-visible trace is built and the first measurements are in** (2026-09-19). See
[architecture.md](architecture.md) - "Rhino: edit-to-visible latency" for the numbers and
`mhLatencyTrace` for the command. Headline: on the trailer-ramp fixture an unchanged rebuild spent
**3 ms computing and 1,084 ms scheduling**. The computation is not the wait.

Two follow-ups, in this order:

1. **Re-measure the completion pickup in an interactive Rhino.** The headless slot's own message loop
   is parked - a posted invoke took 264 ms to run while the post cost 0.0 ms - so the 100-630 ms
   pickup figures cannot be quoted as product numbers. Run `mhLatencyTrace` > On, edit, then
   `mhLatencyTrace` > Report in a real session. The 500 ms debounce needs no such confirmation.
2. **Decide the debounce policy.** It is the single largest and most certain component (500 ms of every
   edit, 42-75% of the measured wait). The five-edit burst discarded zero worker time, so coalescing is
   already doing its job; the question is whether the flat 500 ms should become gesture-end-driven or
   adaptive to the last build's duration. This is a product decision, not a measurement gap.

Only then rank stage optimization: geometry was 8-31% of the wait, and on a cached rebuild 0.2%.

**Delivered since.** Adaptive debounce (`TerrainDebouncePolicy`) and early publication of geometry ahead
of dependent outputs (`TerrainInterimPublishPolicy`, `TerrainController.PublishInterimGeometry`, with
`GeometryRevision`/`OutputsRevision` freshness on `TerrainDisplayState`). Both measured live; see
[architecture.md](architecture.md) - "Rhino: edit-to-visible latency".

**Re-measured.** The host wait is **1.1%**, not the dominant term claimed earlier. Those earlier figures
were largely a measurement error: `Thread.Sleep` in a `run_csharp` script runs on Rhino's UI thread and
blocks the loop being measured. The report now separates each marshal hop from the work that follows it,
and attributes the whole wait to debounce / MoleHill working / host wait / redraw.

**Delivered since.** Leading-edge debounce (a delay only while input is still arriving, plus a posted
dispatch so a zero delay means zero), and a plain preview for interim publications (the stale colouring
was computed from a different face set). Heavy fixture: edit to terrain visible **3,072 -> 1,159 ms**.
Small fixture: edit to visible **1,087 -> 385 ms**.

**Geometry-heavy terrains are a separate problem.** A stack of Triangulate -> Grade Pad -> Grade Path ->
Smooth -> Remesh with no analyses spends **98.5%** of edit-to-visible inside the modifier chain, so none
of the scheduling work above helps it and early publication correctly declines. The largest single item
is **`ResolveGradePadInputs` at 1.42 s for one rectangular boundary** - more than `PadGrader.Grade`
itself (1.36 s). Mesh marshalling was measured and excluded as the cause (67 ms extract, ~113 ms full
round trip). See [architecture.md](architecture.md), "The geometry-heavy case is a different problem".

**Still open.** (a) The marshal from a finished worker back to the UI thread is now the largest single
interval on the small fixture (275 ms of 385 ms). It measures 0.1 ms when Rhino's loop is busy, so only
a trace from a genuinely interactive session will say what an ordinary edit sees. (b) Ponding at 3.72 s
and Catchments at 1.40 s on a 244k-face mesh: the other four analyses total 0.13 s, so these two are the
whole dependent-output cost and neither has been profiled. (c) The final publication still costs ~500 ms
display publish + ~1,000 ms redraw on 244k faces, now paid after the interim one.

**Next ownership action: R05, stage 2.** The ownership table ([build-result-ownership.md](build-result-ownership.md)) is
written and is the prerequisite the review asked for. The next change is the build-result ownership
object with `Transfer`/`Discard`, closing the four gaps that document names. Start at gap 1 - it is the
smallest: `ExecuteBackgroundBuild` returns its worker cache on canceled and failed paths, and
`CompleteBackgroundBuild` returns before the merge for canceled, superseded, failed or stale-generation
results. Newly produced geometry then lacks an explicit discard endpoint. This is a deterministic
cleanup gap, not proof of a permanent leak.

Do **not** start R06 (scheduler extraction) first. It needs the ownership contract to exist, and its
acceptance explicitly requires replaying the event sequences in a disposable Rhino slot before
interactive behaviour changes - see [rhino-live-testing.md](rhino-live-testing.md).

### Claims this branch does not make

Stated plainly so nobody quotes them later:

- **No timings were recorded.** R02 fixes a hash-collapse mechanism and proves the bucket spread, but
  no grading case was measured before or after. The historical "7 s of a 10 s remesh" figure describes
  the *original* fix in `IndexedMeshTools`, not this one.
- **No native acceptance.** Every `[RhinoNativeFact]` skipped. R01's "a rejected document is not
  overwritten" test is one of them.
- **No cancellation-latency measurement.** The 250 ms p95 target in R03 remains a proposal.
- **R04's budget constants come from the test fixtures**, not from a measured production distribution.

### Traps found the hard way

Each of these cost time in this session and will cost it again:

- **A `.ps1` with a non-ASCII character breaks under Windows PowerShell 5.1.** It decodes the file as
  ANSI, and a UTF-8 em dash lands on a byte the parser treats as a string delimiter - the error points
  at a line 200 further down. `validate.ps1` is deliberately ASCII-only with a BOM. Keep it that way.
- **`dotnet build` will not recompile just because you changed a warning-suppression property.** The
  up-to-date check is timestamp-based, so a warning sweep can return a clean log from a build that
  never ran. `validate.ps1 warnings` refuses to report success if it saw no compiler output, for
  exactly this reason.
- **`-p:NoWarn=a;b;c` needs `%3B` for the separators**, or MSBuild reads them as further switches.
- **`Directory.Build.props` must keep one `PropertyGroup`.** `build-yak-package.ps1` read the version
  with dotted XML access, which throws on an array. Hardened in `35abb9a`, but the packaging script is
  the only path that ships a `.rhp`, so treat it as fragile.
- **Piping the packaging script's output promotes yak's expected stderr warning to a fatal error.**
  The package lane runs it in a separate process for that reason.
- **There are two `CloneStageCacheEntry` methods** with different deep/shallow behaviour - one in
  `TerrainRuntimeCacheCloner`, one in `TerrainBuildService.Cache`. Read the qualified name.

### New files worth knowing about

| File | Why it exists |
|---|---|
| `validate.ps1` | The lane driver. Every lane writes evidence to `.artifacts/validate/<lane>-<timestamp>/` |
| [validation-lanes.md](validation-lanes.md) | What each lane proves and, more importantly, what it does not |
| [build-result-ownership.md](build-result-ownership.md) | The R05 ownership table; read before touching `TerrainRuntimeCache` |
| `global.json`, `.editorconfig` | The SDK floor, and the vendored-TriangleNet warning exemption that replaced the project-wide `NoWarn` |
| `tests/.../PackedEdgeKeyComparerGuardTests.cs` | Scans `src/` for default-comparer packed-key collections. A new spatial-cell key must be added to its allowlist with a reason, or the build fails |
| `tests/.../PerformanceLane.cs` | The shared benchmark gate. Every new benchmark routes through `ShouldRun`, or the perf lane cannot tell whether it executed |

## Assessment

MoleHill has a sound foundation: reusable geometry in Core, host adapters, a versioned Interop
contract, descriptor-driven features, extensive copied-case regressions, and documented topology
invariants. Preserve those investments. The best next work is to close concrete correctness and
performance gaps, make verification reproducible, and extract a few ownership boundaries from the
host. A broad rewrite or another round of file splitting would offer less value.

The highest-priority findings are:

1. Future-schema documents can be accepted and subsequently rewritten by an older serializer.
2. Several active grading paths still hash packed mesh-edge keys with the default comparer.
3. Expensive splitting phases cannot observe cancellation, extending superseded work and memory use.
4. Passing tests do not establish native-host or large-model acceptance; some performance tests
   report a pass without executing their benchmark body.
5. Worker/cache ownership is carefully managed but remains implicit and distributed across large
   host classes, making further performance changes harder to verify safely.

These are separate work items. Correctness fixes should not depend on completing an architectural
refactor, and refactors should not quietly alter geometry, persistence, or Grasshopper port contracts.

## Scope, evidence, and verification

This is a repository-wide structural survey with targeted source inspection of build configuration,
grading edge maps, area splitting, spatial indexing, cancellation, worker/cache lifecycle,
serialization, fingerprints, Grasshopper payloads, registries, benchmarks, and existing plans.
It is not a line-by-line audit of every algorithm, a measured hotspot ranking, a security audit,
or a native Rhino UI acceptance run. TriangleNet internals were excluded from refactoring proposals.

Evidence labels used below:

- **Confirmed:** visible in current source/configuration or in this review's test execution.
- **Risk:** the source exposes a failure mechanism, but its user-visible frequency or cost is unmeasured.
- **Investigation:** a plausible improvement requiring profiling or contract characterization first.

Source references use paths and symbol names rather than fragile historical line numbers.

### Current scale

Counts are physical C# lines from `rg --files src tests`, excluding `bin`, `obj`, and vendored
TriangleNet; they include comments and fixture literals and are not a complexity score.

| Area | C# files | Lines |
|---|---:|---:|
| Core | 163 | 48,394 |
| Rhino | 319 | 63,992 |
| Grasshopper | 37 | 7,072 |
| Shared linked sources | 6 | 3,792 |
| Interop | 4 | 77 |
| Core tests | 151 | 200,101 |
| Rhino tests | 76 | 11,504 |
| Grasshopper tests | 12 | 19,558 |

Large production files include `MoleHillPanel.cs` (2,878 lines), `SurfaceRemesher.cs` (2,311),
`RetainingWallPlannerCore.cs` (2,122), and `TerrainController.cs` (2,050). Their size identifies
inspection candidates, not automatic extraction targets. Several test files exceed 19,000 lines
because they embed real geometry; the largest is 39,059 lines.

### Validation actually run

SDK reported by `dotnet --version`: `10.0.400`.

```powershell
dotnet test MoleHill.sln --no-restore --verbosity quiet -p:SkipGrasshopperLibraryCopy=True --logger trx --results-directory .artifacts/review-2026-09-19
```

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 947 | 0 | 0 |
| Rhino | 684 | 0 | 129 |
| Grasshopper | 38 | 0 | 15 |
| Total | 1,669 | 0 | 144 |

The command exited successfully and built the solution's test dependencies and merged Grasshopper
outputs. TRX evidence is in the ignored `.artifacts/review-2026-09-19/` directory. This used existing
restore state; it does not demonstrate a fresh-machine restore. Native tests were skipped. No live
Rhino, viewport, Revit, full-scale benchmark, Release build, or Yak package acceptance was performed.
`MOLEHILL_PERF` was not enabled; the passing count includes opt-in benchmark methods that return early.

## Implementation status

### Progress audit — 2026-09-19, revision `1eb436f`

Checked commit history and current source against this plan. Deliveries 1-5 have implementation
commits and tests; delivery 6 has an ownership table only. R06/R07/R10/R11/R12 remain unstarted.
Validation and package successes in the table below are implementation-session reports unless
explicitly identified as rerun in this audit.

Audit rerun: `dotnet test MoleHill.sln --no-restore --verbosity quiet -p:SkipGrasshopperLibraryCopy=True
--logger trx --results-directory .artifacts/review-progress-2026-09-19` exited successfully:
Core **974 passed**, Rhino **694 passed / 130 skipped**, Grasshopper **39 passed / 15 skipped**.
Total **1,707 passed, 0 failed, 145 skipped**. No native, performance, warnings, or package lane was
rerun during this audit; this test run does not establish those acceptance results.

The main user-facing performance work is still outstanding. A diff against `fc1a90b` shows no changes
to `TerrainController.cs`, `TerrainController.Build.cs`, `TerrainController.Events.cs`, or
`TerrainBuildService.cs`. Thus the 500 ms debounce, waiting for worker completion, due-request
selection, publication after final-only outputs, and incomplete summed total timing remain as
described in the priority refinement. Core cancellation and edge hashing may help individual builds,
but no measured reduction in edit-to-visible latency is established.

| Latency work | Progress |
|---|---|
| End-to-end request trace, including canceled work and publication gaps | Not implemented |
| Eligible-request selection / multi-terrain head-of-queue blocking | Not implemented |
| Cancellation inside splitting/index phases | Implemented; latency budget unmeasured |
| Packed-edge hash corrections | Implemented; representative build speedup unmeasured |
| Publish exact geometry before dependent outputs, with freshness/ownership contracts | Not implemented |
| Rank and optimize the two largest measured final-build delays | Awaiting representative trace |

The headline and handoff have been corrected to separate implemented work from acceptance and to
put latency instrumentation before further broad refactoring.

Delivery order below matches the **Implementation sequence** table. "Implemented" and "verified" are
kept apart deliberately: a managed suite cannot close an acceptance item that names a native soak run
or a measured budget.

| Order | Item | Implemented | Verified by | Still open |
|---|---|---|---|---|
| 1 | R01 future-schema guard | Yes | Managed tests: future envelope/terrain/mixed list, empty future document, legacy migration + idempotent round-trip, unknown property at the current version, malformed input | The store-level no-overwrite test is `[RhinoNativeFact]` and **skipped** without a Rhino runtime; open/save in a live Rhino not run |
| 2 | R02 edge comparer fixes | Yes | Full managed suite unchanged; `PackedEdgeKeyComparerGuardTests` scans `src/` and fails on a default-comparer packed-key collection; `EdgeKeyComparerTests` asserts bucket spread on consecutive and shuffled numbering | No end-to-end timing on a representative grading case; the historical 7 s remesh figure is **not** re-measured and must not be quoted as this change's result |
| 3 | R08/R09 validation baseline | Yes | `validate.ps1 managed`, `warnings` and `package` run green end to end; the perf gate verified in both directions (fails on a skipped body, runs with `MOLEHILL_PERF=1`) | `validate.ps1 native` not run — no Rhino runtime was available in this session; `perf` not run as a full lane, so there are **no recorded timings** |
| 4 | R03 cancellation propagation | Yes | Mid-phase cancellation tests at measured fractions of a full run, on two mesh sizes; parallel cancellation asserted to surface as `OperationCanceledException`, never `AggregateException` | The p50/p95 cancellation-latency trace and peak-memory measurement are **not** done; the proposed 250 ms budget remains a proposal |
| 5 | R04 spatial capacity | Yes | Brute-force equivalence on uniform, clustered, long-diagonal, mixed tiny/huge, degenerate-extent and invalid-bounds fixtures; membership growth recorded across doubling sizes; ordinary distributions asserted **not** coarsened | Budget constants chosen from these fixtures, not from a measured production distribution; no query-cost comparison after coarsening |
| 6 | R05 ownership | Table only | [build-result-ownership.md](build-result-ownership.md) — characterization written from current sources | The ownership object, `Transfer`/`Discard`, generated-object disposal and the native repeated-edit soak are all **not** started |
| 7-11 | R06, R07, R10, R11, R08 cleanup, R12 | No | - | Not started. R06 needs the R05 contract first; its acceptance also requires replaying the cases in a live Rhino slot |
| Ongoing | R13 documentation | Partial | Architecture sections added for schema compatibility, cancellation, index capacity and ownership; `MoleHill.Core`/`MoleHill.Rhino` project-root READMEs added (they were referenced but missing); `docs/validation-lanes.md` added | The July cleanup plan still presents partial-file decomposition as current work; `docs/architecture.md` is still a long feature history rather than a concise map |

Two incidental fixes fell out of the above and are worth knowing about:

- The owned-code nullability warning count was **6**, not the 456 the project-wide `NoWarn` implied:
  450 were vendored TriangleNet. The suppression is now scoped to the vendor in `.editorconfig` and
  the 6 are fixed, so the solution builds clean under `-warnaserror`.
- Folding the new host-path properties into a second `PropertyGroup` broke `build-yak-package.ps1`,
  which read the version with dotted XML access. Caught by the new package lane on its first run,
  which is the lane earning its keep on day one.

## Findings and recommendations

### R01 — Reject unsupported future document schemas before normalization

**Priority: P1. Confirmed source behavior; data-loss risk, not a reproduced user incident.**

Evidence: [TerrainSerializer.cs](../src/MoleHill.Rhino/Services/TerrainSerializer.cs),
`Deserialize`, `Serialize`, and `TerrainDocumentEnvelope`; and
[TerrainDocumentStore.cs](../src/MoleHill.Rhino/Services/TerrainDocumentStore.cs), `Load`/`Save`.
The envelope schema is written but not checked on load. Per-terrain versions control selected
migrations, then are unconditionally set to `TerrainDefinition.CurrentSchemaVersion`. JSON options
do not preserve unknown properties. Unknown type discriminators fail safely, but a future document
using existing types and additional properties can pass through this path.

**Failure scenario:** a newer plugin adds a persisted property to an existing modifier. An older
plugin loads the document, drops that property during deserialization, stamps the older version,
and saves. The existing unreadable-document guard is never activated. The one previous-JSON backup
is useful but is not a durable forward-compatibility policy.

**Change:** validate envelope and per-terrain versions before legacy rewriting/normalization.
Unsupported future versions should use the existing load-failure/no-save path and preserve the
original JSON. Define the treatment of missing legacy versions explicitly. Do not simply accept
future schemas because all type discriminators happen to be known.

**Acceptance:** tests for future envelope only, future terrain only, mixed-version terrain lists,
known types with unknown properties, malformed input, and supported legacy fixtures. Rejected
documents must not be overwritten. Existing supported migrations must still round-trip and be
idempotent. Native verification covers open/save and the user-facing load-failure state.

### R02 — Finish the packed-edge comparer migration

**Priority: P1. Confirmed convention violation and collision mechanism; end-to-end cost unmeasured.**

Evidence:

- [PadGrader.Explicit.cs](../src/MoleHill.Core/Grading/PadGrader.Explicit.cs),
  `ExtractFillInterior`: `walls` and `edgeFaces` use default comparers with `FillEdgeKey`.
- [GradedRegionAssembler.cs](../src/MoleHill.Core/Grading/GradedRegionAssembler.cs),
  `BuildHoleBoundaryAdjacency`: default-comparer edge counts traverse every input face.
- [PadGrader.RegionRemesh.cs](../src/MoleHill.Core/Grading/PadGrader.RegionRemesh.cs):
  default-comparer `edgeCount`.
- [GradingResultBuilder.cs](../src/MoleHill.Core/Grading/GradingResultBuilder.cs):
  default-comparer `processedEdges` sets.
- [SurfaceRemesher.cs](../src/MoleHill.Core/Engine/SurfaceRemesher.cs): callers pass default
  `HashSet<long>` instances into `MeshConstraintTools.AddBoundarySegments`.

For packed `(min << 32) | max` keys, the default long hash XORs the two halves. Every edge `(2k, 2k+1)`
then hashes to 1. This defeats the intended near-constant-time lookup on common indexed meshes.
The repository already supplies `IndexedMeshTools.EdgeKeyComparer.Instance` for this reason.

**Change:** audit constructors, target-typed allocations, copied collections, and helper parameters;
apply the comparer to verified edge keys. Prefer a small canonical collection factory if it removes
repeated mistakes without hiding capacity choices. Add a focused guard/analyzer for this convention.
Do not mechanically alter all long-key dictionaries: spatial-cell keys use different encodings.

**Acceptance:** topology and output ordering remain unchanged across explicit, split-keep, and
region-remesh cases. Benchmark edge insertion/lookup on consecutive and shuffled vertex numbering,
then run representative grading cases. Report sizes and scaling; do not promise a speedup from the
historical remesh result alone. Address per-edge `List<int>` allocation separately after measurement.

### R03 — Make cancellation observable inside expensive phases

**Priority: P1 for responsiveness. Confirmed gap; latency needs measurement.**

Evidence: [MeshAreaTopologySplitter.cs](../src/MoleHill.Core/Grading/MeshAreaTopologySplitter.cs),
`MapBoundarySegmentsToFaces`, takes no cancellation callback and executes a face-parallel mapping
pass without a cancellation check. Surrounding checks cannot interrupt this phase.
[SpatialHashGrid2D.cs](../src/MoleHill.Core/Engine/SpatialHashGrid2D.cs), `Build`, performs membership
counting and filling without a cancellation parameter. This remains an acceptance gap in O09 of
the September scalability review.

**Change:** propagate cancellation through index construction, segment preparation, mapping,
classification, and large output setup loops. Use per-worker probes for parallel loop counters;
preserve deterministic candidate ordering. Define how parallel cancellation reaches the host as
`OperationCanceledException`, rather than becoming an ordinary build failure through aggregation.
Check cancellation before allocating large buffers where possible.

**Acceptance:** deterministic tests request cancellation after a phase has started, rather than only
before entry. No partial result is published. A controlled performance run records cancellation
request-to-worker-exit p50/p95, peak memory, and new-request latency during repeated edits. Identify
uninterruptible external triangulation calls explicitly. Initially target p95 below 250 ms in
MoleHill-owned phases on the agreed 100k/500k-face fixtures; treat this as a proposed budget, not a
current guarantee. Larger workloads need separately agreed bounds.

### R04 — Bound spatial-index membership growth, not just query extents

**Priority: P2. Confirmed allocation pattern; pathological-scale risk.**

Evidence: [SpatialHashGrid2D.cs](../src/MoleHill.Core/Engine/SpatialHashGrid2D.cs), `Build`, registers
an item's bounding box into every covered cell, counts memberships in `int`, sums them into an
`int running`, then allocates one flattened membership array. CSR avoids one list per cell, but
does not bound memberships when many large or diagonal bounds overlap a fine grid.

**Change:** instrument item count, cell count, total memberships, maximum occupancy, and build/query
cost. Add checked capacity accounting and a controlled failure or fallback before overflow or
unreasonable allocation. Evaluate coarser cells, an oversized-item side list, or a hierarchy only
against measured distributions. A side list is useful only if its repeated query cost stays bounded.

**Acceptance:** uniform, clustered, long-diagonal, mixed tiny/huge bounds, and degenerate-extent cases;
indexed answers match brute force, including deterministic ordering where consumers depend on it.
Record memory across doubling sizes. Preserve clamped query extents and sparse query scratch.
This completes O04 acceptance rather than replacing the existing CSR work.

### R05 — Make worker result ownership and abandonment explicit

**Priority: P2. Confirmed distributed lifecycle; deterministic-cleanup risk requiring profiling.**

Evidence: [TerrainRuntimeCache.cs](../src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs),
`CreateWorkerCopy`, `ReplaceBuildCachesFrom`, `PruneUnused`; and
[TerrainController.cs](../src/MoleHill.Rhino/Services/TerrainController.cs),
`RetireRunningWorker`, `PruneCompletedRetiredWorkers`, `DisposeDisplacedCacheMeshesWhenSafe`.
Workers borrow cached mesh references. Successful merges transfer entries and defer disposal of
displaced meshes until retired workers finish. This is valuable existing behavior.

However, completed retired tasks are removed without an explicit cleanup of their newly produced
result geometry. Stale/canceled results and closed-document completion paths likewise lack one
obvious ownership endpoint. This does **not** establish a permanent leak: managed reachability and
Rhino finalization may reclaim resources. It makes peak native memory and safe cleanup hard to prove.

**Change:** first write an ownership table for cached, borrowed, newly created, published, and
abandoned geometry. Then introduce a focused build-result ownership object with explicit transfer
and discard operations. Discard must dispose only owned geometry, never borrowed cache/display
objects. Track generated geometry as well as primary meshes. Do not call `Clear()` indiscriminately
on worker copies; it can dispose shared meshes and invalidate their shared TinEngine.

**Acceptance:** tests and native soak runs cover successful merge, canceled build, superseded result,
reset, terrain deletion, document close, and exception before/after geometry creation. Multiple
rapid edits must settle to bounded retained memory with no use-after-dispose or stale publication.
Measure native private bytes as well as managed allocation. Coordinate this work with R03.

### R06 — Extract a testable build scheduler before expanding interactivity

**Priority: P2. Confirmed coupling; architectural improvement.**

Evidence: `TerrainController.cs`, [TerrainController.Build.cs](../src/MoleHill.Rhino/Services/TerrainController.Build.cs),
and [TerrainController.Events.cs](../src/MoleHill.Rhino/Services/TerrainController.Events.cs) jointly
own request versions, generations, debounce, retirement, result acceptance, document events,
publication, saves, and redraw. The Rhino test project explicitly excludes `TerrainController*.cs`.
Existing helper tests cannot establish the full orchestration behavior.

**Change:** extract the request/worker state machine behind explicit inputs and effects. Inputs are
request, clock advance, completion, reset, deletion, and close; effects request capture/start/cancel/
publish/discard. Inject a clock and executor so tests control ordering without sleeps. Keep Rhino
document capture, output mutation, and redraw on the host side. Keep scheduling concerns outside
the geometry Core; an internal host-independent unit is enough initially.

**Acceptance:** event-sequence tests prove that only the current generation/version publishes;
preview cannot become an authoritative final result; reset/close prevents later publication;
repeated requests coalesce; borrowed resources survive worker overlap. Replay these cases in a
disposable Rhino slot before changing interactive behavior.

### R07 — Separate stage execution from the broad build-service context gradually

**Priority: P2. Confirmed structure; refactor opportunity.**

Evidence: [TerrainBuildService.cs](../src/MoleHill.Rhino/Services/TerrainBuildService.cs) and its many
partials contain orchestration, fingerprints, cached execution, geometry conversion, stage bodies,
and output construction. [ModifierTypeDescriptor.cs](../src/MoleHill.Rhino/Registry/ModifierTypeDescriptor.cs)
already provides `RunBuildStage`; preserve that dispatch seam.

**Change:** pilot extraction of one bounded stage, such as Project To or Simplify, into a runner
with explicit resolved inputs, mesh/constraint input, cancellation, and result/diagnostics. Keep
document resolution in the snapshot boundary, reusable numerical work in Core, and Rhino geometry
conversion in the host. Measure whether the new API reduces dependencies before extracting more.
Use small records for stable bundles of related inputs, not a universal context object or service
locator. A new project is unnecessary until a real dependency boundary warrants it.

**Acceptance:** cached/uncached output parity, exact constraint behavior, fallback diagnostics, and
preview/final eligibility stay unchanged. A new stage should no longer require knowledge of unrelated
output families. Mechanical partial moves may aid navigation but do not satisfy this acceptance.

### R08 — Restore actionable compiler diagnostics and reproducible build inputs

**Priority: P1 for the baseline; P2 for incremental cleanup. Confirmed.**

Evidence: [MoleHill.Core.csproj](../src/MoleHill.Core/MoleHill.Core.csproj) enables nullable analysis
but suppresses numerous nullability warnings project-wide. It also compiles vendored TriangleNet
sources directly. Rhino/GH use `LangVersion=latest`; no tracked `global.json` or `.editorconfig`
was found. [MoleHill.Rhino.csproj](../src/MoleHill.Rhino/MoleHill.Rhino.csproj) hardcodes the
WindowsDesktop `7.0.20` reference-pack path. Host test projects use installed Rhino/GH DLL paths;
Rhino tests combine a newer RhinoCommon package with a direct installed-assembly reference.

**Change:** document and pin a known-working SDK/roll-forward policy; choose the language version
deliberately. Scope unavoidable vendor warning suppression to vendor compilation, retaining the
existing packaging approach until verified. Inventory owned-code warnings, remove broad suppression
in small batches, and ratchet new warnings. Establish a configurable Rhino installation path and an
explicit minimum-SDK versus installed-runtime compatibility matrix. Resolve duplicate reference
selection deliberately; a test should state which assembly it exercised.

Do not automatically retarget the plugin just because tests run on net8.0 or this machine has a
newer SDK. Host/runtime compatibility and package behavior need separate verification.

**Acceptance:** clean restore/build on a second environment or controlled runner, no hidden absolute
developer paths outside declared configuration, known runtime assembly provenance, and no new
owned-code warnings. Introduce formatting without mass reformatting historical geometry files.

### R09 — Separate managed, native, performance, and package acceptance

**Priority: P1. Confirmed verification gap.**

Evidence: no tracked `.github` workflow was found; this says nothing about external CI. Host tests
link production sources rather than referencing the built host plugin. `RhinoNativeFact` skips
when its native probe fails. [LargeTerrainPerformanceBenchmarkTests.cs](../tests/MoleHill.Core.Tests/LargeTerrainPerformanceBenchmarkTests.cs)
and [MeshAreaTopologySplitterScalingBenchmarkTests.cs](../tests/MoleHill.Core.Tests/MeshAreaTopologySplitterScalingBenchmarkTests.cs)
return normally when `MOLEHILL_PERF` is absent.

**Change:** define four explicit lanes: fast managed regressions; native integration on a configured
Rhino runner; opt-in measured performance; actual package/install smoke. Native-required jobs fail
preflight if the runtime is missing instead of silently accepting skips. Performance runs record
whether bodies executed; eventually use a dedicated harness or explicit skip mechanism. Keep
source-linked tests where practical, but add artifact-level checks for the RHP, merged GHA, and
shared Interop assembly. Use the existing packaging script without `-Push` for package checks.

**Acceptance:** reports distinguish pass/fail/skip and benchmark-not-run; a green managed lane cannot
be read as native acceptance. Packaged contents and plugin/component discovery are checked against
the exact build. Historical `.gh`/`.3dm` fixtures remain readable; normal file opening is tested
separately from direct archive deserialization.

### R10 — Preserve defensive GH copying until ownership is defined and measured

**Priority: P2 investigation. Confirmed copying; benefit of reducing it is unproven.**

Evidence: [MoleHillTerrainData.cs](../src/MoleHill.Grasshopper/Types/MoleHillTerrainData.cs) duplicates
meshes, curves, and regions in its constructor; `Duplicate()` re-enters that path. Public getters
expose Rhino geometry objects, which are mutable. The architecture notes already record a
180k-face Snapshot/Deconstruct baseline and downstream replacement on some unchanged-content solves.

**Change:** profile Snapshot → Deconstruct, multiple consumers, typed modifier chains, and freeze/
refresh. Count mesh copies, fingerprint passes, downstream expirations, allocations, and elapsed
time. If worthwhile, introduce a clearly internal ownership-taking path for freshly produced geometry
or a revision-bound snapshot handle with defined lifetime. Keep public defensive semantics unless
all consumers can be proven safe. `IReadOnlyList` does not make its geometry elements immutable.

**Acceptance:** downstream edits cannot mutate upstream/frozen data; stale/current transitions and
names update correctly; persistent IDs and appended port ordering remain compatible. Compare cold
and warm solves. Coordinate with B7 rather than starting a competing terrain-wrapper design.

### R11 — Make copied-case fixtures easier to maintain without weakening them

**Priority: P2. Confirmed maintenance burden; build-time benefit needs measurement.**

Evidence: large regression files contain tens of thousands of source lines. These cases are useful
coverage of actual geometry failures and should not be removed to improve a line-count metric.

**Change:** pilot moving one large case to a versioned embedded fixture, retaining named assertions,
case provenance, units, tolerance, and a small readable scenario description in the test. Use exact
double round-tripping, deterministic ordering, and a checksum. Update the case exporter only after
the loader format is validated. Share test geometry helpers only when they preserve the assertions'
independence from the production algorithm.

**Acceptance:** old and new fixture arrays are bit-identical; assertions and exercised tiers match;
tests need no network/external file path. Measure build time, test load time, and artifact size before
expanding the migration. Keep small inline cases readable.

### R12 — Consolidate geometry helpers only after documenting semantic differences

**Priority: P3. Investigation inherited from the cleanup plan.**

Evidence: [GradingGeometry2D.cs](../src/MoleHill.Core/Grading/GradingGeometry2D.cs) is already a
canonical home for several predicates. Shared linked files include Rhino-dependent planning and
conversion work; Shared is a source folder, not a standalone project in the solution.

**Change:** inventory duplicated predicates by semantics: tolerance units, on-edge behavior,
even/odd holes, orientation, degenerate segments, and deterministic tie-breaking. Move only matching
reusable numerical behavior into Core. Keep Rhino/GH conversions in host/shared adapter code.
Consolidate the repeated Shared compile-item list before considering a new shipped assembly.

**Acceptance:** differential tests include boundary points, reversed winding, nested holes, repeated
vertices, large coordinate offsets, and unit-scaled cases. Do not merge area splitting and constraint
insertion simply because both intersect triangles; their output/elevation contracts differ.

### R13 — Repair the navigation and plan lifecycle

**Priority: P2. Confirmed documentation drift.**

The July [cleanup plan](cleanup-plan.md) still presents partial-file decomposition as a primary
task. The September [scalability review](terrain-scalability-review-2026-09-09.md) correctly records
that checked implementation items can have incomplete acceptance. Preserve that distinction.
`docs/architecture.md` calls itself a one-page map but now contains extensive feature history.
Core/Rhino project-root READMEs referenced during navigation do not exist; subfolder READMEs do.

**Change:** maintain one current roadmap with links to specialist plans and separate statuses for
implemented, verified, blocked, and superseded. Keep dated reviews as historical evidence. Split the
architecture landing page into a concise dependency/pipeline/ownership map with links to detailed
contracts. Fix misleading navigation and empty file-index descriptions. Correct the dependency map
to explain source-linked Shared and embedded TriangleNet compilation rather than implying ordinary
project references everywhere.

**Acceptance:** a new contributor can locate the relevant pipeline, owner, invariant, and validation
command without reading several historical plans. New/renamed source files still regenerate the
file index and update architecture and relevant READMEs in the same implementation change.

## Proposed architecture boundaries

Keep the existing project boundaries until an extraction demonstrates a practical benefit:

| Boundary | Responsibility | Must not own |
|---|---|---|
| Core | Numerical geometry, indexes, topology validation, deterministic algorithms | Rhino documents, UI, persisted host preferences |
| Snapshot builder | Capture document geometry/settings and resolve host references | Background document access |
| Build scheduler | Request ordering, cancellation, acceptance decisions | Geometry algorithms, direct document mutation |
| Stage runners | Adapt resolved inputs to Core and construct stage results | Global controller state or unrelated stage internals |
| Build-result ownership | Transfer/discard resources with explicit borrowed/owned distinctions | Arbitrary disposal of shared cache geometry |
| Publication/output services | Apply accepted state, preview/bake roles, document transactions | Accepting stale generations or approximate final output |
| Registry/schema UI | Feature metadata and unit-aware editors | Duplicated mathematical or persistence rules |
| Interop/GH boundary | Versioned snapshots, stable identity, explicit copying policy | Dependence on Rhino plugin implementation types |

Do not introduce an interface for every class. Introduce seams where lifecycle, independent testing,
or multiple real implementations require them. Preserve separate analysis/annotation families,
shared parameter-row vocabulary, `SlopeInput`, and `LayerRole` routing.

## Implementation sequence

### Priority refinement: reduce the wait for a visible final terrain

User feedback after the review identifies waiting for final builds as the main performance pain.
Treat edit-to-visible latency as the first performance workstream, ahead of broad stage extraction,
GH copying experiments, fixture migration, and general deduplication. The R01 persistence guard
remains an independent small correctness fix. The observations below are source-confirmed;
individual stage costs on the user's terrain have not yet been measured.

**Current ordinary-edit path:**

`edit → debounce / previous-worker completion → document snapshot → worker cache copy → modifier stack
→ boundary trimming → analyses → zones → markers → objects → scatter → reports → result acceptance
→ cache merge / document cleanup → display refresh → object synchronization → save → redraw`

- `TerrainController.cs::FinalDebounceMs` is 500 ms. `ScheduleRebuild` queues Final directly; the
  ordinary scheduling path does not queue a cheaper preview first. Preview support elsewhere in
  the code does not establish a preview-first ordinary-edit workflow.
- `RequestRebuild` cancels the current worker, but the idle dispatcher returns while that terrain's
  `IsBuilding` remains true. The replacement therefore waits for cancellation to be observed and
  completion processed. Debounce and this wait can overlap; do not add them as independent delays.
- `TerrainController.Events.cs` selects the oldest due request before checking whether it can run.
  If that request belongs to a building terrain or is deferred for sculpt, the dispatcher returns
  without considering another eligible terrain. This is a head-of-queue blocking mechanism for
  multiple terrains. Fix selection to find an eligible request while preserving per-terrain ordering
  and an explicit concurrency/memory limit; do not simply launch every pending build.
- `TerrainBuildService.Build` assigns `PrimaryMesh` before executing the final-only output stages,
  but returns the result only after all of them finish. `ApplySuccessfulBuild` publishes display
  state afterwards. A completed mesh therefore still waits behind its dependent outputs.
- `ApplySuccessfulBuild` performs legacy/orphan cleanup and `SyncTerrainObjects` outside its named
  phase timers. Its reported `Rebuild total` sums selected phases rather than measuring a complete
  request lifetime. Queue delay, completion-dispatch delay, and some host work are absent. The
  report can understate the wait a user experiences.
- Cached mesh stages still restore geometry, and boundary roles can trim both current and baseline
  meshes. Measure restoration/copying, fingerprints, and baseline work independently of the numerical
  modifier body. These are candidates, not established dominant costs.

**First delivery: an end-to-end latency trace.** Add a monotonic request timestamp and correlated
version/generation events for last edit, due time, snapshot start/end, worker start, geometry ready,
each final-output family, worker completion, accepted publication, document sync, and redraw return.
Record canceled/superseded work as well as successful builds. Distinguish redraw return from actual
visible-frame latency where the host permits measuring it. Provide both the latest-edit wait and
work consumed by abandoned requests. Account for every interval, with an explicit unclassified
remainder rather than hiding gaps in a summed total.

Trace at least an unchanged rebuild, one typical parameter edit, and several rapid consecutive edits
on the same representative terrain. Record modifier stack, face count, active analyses/annotations,
zones/scatter, cache hits, and cold/warm state. Existing Status logs can identify candidate stages,
but this trace is needed to rank total delay. Establish whether time is predominantly geometry,
dependent output, superseded work, or document-thread publication before selecting optimizations.

**Then implement in measured order:**

1. Fix omitted edge comparers and cancellation gaps on paths the trace actually reaches; fix the
   eligible-request selection issue with a two-terrain scheduling regression.
2. Remove redundant full-mesh passes/copies and unnecessarily invalidated stages where the trace
   demonstrates their cost. Preserve cache dependency correctness and ownership.
3. Evaluate publishing the completed exact terrain mesh before dependent outputs settle. Introduce
   explicit geometry/output revisions and freshness first: old quantities and drawings must not be
   presented as current, and partial publication must not pass existing final snapshot/bake checks.
   Immutable ownership is required so a worker cannot mutate geometry already displayed. This is
   a scoped continuation of the interactive-terrain plan, not a quick assignment to display state.
4. Tune debounce only after measuring cancellation cost and edit patterns. A shorter delay can
   increase discarded work; use gesture-end/immediate intent where the host can identify it.
5. Optimize the two largest remaining measured stages, with before/after traces and geometry checks.

**Success criteria:** report edit-to-visible terrain, edit-to-complete outputs, cancellation latency,
document-thread blocking, and peak memory separately. A faster worker with the same visible wait
does not finish this workstream. Preserve final geometry, no stale publication, preview/bake parity,
and clear freshness for dependent outputs. Use the named-fixture budgets in the interactive-terrain
plan rather than promising a universal sub-second final build.

Each row is a reviewable delivery slice. Sizes are relative scope (S = localized, M = cross-file,
L = lifecycle/host work), not promised duration. Split further if acceptance cannot fit one review.

| Order | Work item | Size | Dependencies | Completion evidence |
|---|---|---|---|---|
| 1 | R01 future-schema guard | S | None | Negative schema tests, legacy round-trip, no-save native check |
| 2 | R02 edge comparer fixes | S/M | None | Constructor audit, geometry regressions, adversarial hash/scaling evidence |
| 3 | R08/R09 reproducible validation baseline | M | None | SDK/configuration, explicit lane scripts, native preflight, recorded warning baseline |
| 4 | R03 cancellation propagation | M | Baseline | Mid-phase cancellation tests and latency/memory trace |
| 5 | R04 spatial capacity instrumentation and limits | M | Baseline | Pathological distribution fixtures, checked allocation, scaling report |
| 6 | R05 ownership characterization and cleanup | L | Cancellation tests | Ownership table, transition tests, native repeated-edit soak |
| 7 | R06 scheduler extraction | L | Ownership contracts | Deterministic event-sequence tests and equivalent native workflows |
| 8 | R07 one-stage extraction pilot | M | Baseline | Cache/fallback parity and smaller dependency surface |
| 9 | R10 GH copying experiment | M | Ownership measurements | Multi-branch allocation/solve comparison and mutation isolation |
| 10 | R11 one-fixture migration pilot | S/M | Baseline | Bitwise fixture equivalence and build/load measurements |
| 11 | R08 warning cleanup, R12 targeted deduplication | M per batch | Stable baseline | Ratcheted warning counts and semantic differential tests |
| Ongoing | R13 documentation/status maintenance | S per slice | Each change | Correct navigation and evidence-backed acceptance status |

Start with the first three deliveries. They give immediate protection and make later work easier to
evaluate. Do not hold them behind the scheduler refactor. Index instrumentation can proceed before
selecting a new indexing strategy. Broader extraction should wait for the pilot's review.

### How this relates to existing plans

- This review is the current cross-cutting hygiene backlog; the July cleanup plan remains historical
  context. Its dead-code sweep should use call-graph/reflection/registry evidence, not text-reference
  counts alone.
- Scalability O01–O14 and O16 already have substantial implementation. Reopen only their documented
  acceptance gaps: especially O04 membership growth, O09 cancellation, O10 lifetime profiling, and
  O15 scatter viewport measurement. Do not reimplement completed CSR, grouping, or sparse-scratch work.
- [Interactive terrain](interactive-terrain-plan-2026-09-16.md) remains a product plan. R03/R05/R06
  support it, but this review does not approve approximate meshes for bake or analysis or promise
  universal realtime performance.
- [Grasshopper redesign](grasshopper-redesign-plan.md) remains the feature/compatibility backlog.
  R09/R10 support its host acceptance and snapshot cost work.
- Domain plans for drainage and simplification remain authoritative for their numerical contracts.
  Merge resulting work into one roadmap rather than maintaining duplicate checklists.

## Performance measurement and acceptance protocol

Use existing benchmark/case-replay infrastructure first. Add a dedicated benchmark project only
when isolation, repeatability, or automation requires it. A timing log is not a regression budget.

1. Record commit, configuration, SDK/runtime, Rhino version when applicable, CPU, RAM, worker count,
   fixture checksum, units/tolerance, and whether caches are cold or warm.
2. Separate preparation, algorithm, hashing, conversion, cache copy/merge, publication, output sync,
   save, and redraw. Add measured end-to-end request-to-visible latency; summing stage timers can
   omit scheduling and host work.
3. Use warmups and repeated samples. Report sample count, median and p95 rather than a single best
   time. Compare changes on the same machine and fixture. Disable competing test parallelism for
   process-wide memory/allocation measurements.
4. Record process-wide allocations for parallel work, retained managed memory, process-private/native
   memory, and sampled peak usage. Before/after deltas alone miss temporary peaks.
5. Test 100k, 500k, and 1m faces routinely in the performance lane; qualify 5m/15m only on a named
   machine with sufficient memory. Grow face count, boundary complexity, stage count, and worker
   concurrency independently. Stop before exhaustion and report the unsupported envelope honestly.
6. Include unchanged rebuild, local Z edit, wall edits, pad fallback, zone split, large scatter preview,
   multi-branch snapshot consumption, canceled build, and close/reset during work.
7. Keep geometry invariants alongside timing: finite coordinates, valid indices, no single-use
   interior edges, conserved domain area, constraint retention, slope behavior, deterministic ties,
   and consistent preview/bake output. A faster invalid mesh fails acceptance.

Agree budgets after the baseline. A reasonable initial review trigger is a repeatable >10% median
time or peak-memory regression on a stable representative fixture; it should trigger investigation,
not become a flaky universal CI failure threshold. Algorithmic scaling regressions warrant action
even when a small fixture still meets its absolute time budget.

## Validation commands and release gates

For implementation changes, use the relevant focused tests first, followed by the affected suites.
The full commands below are proposed gates; only the earlier recorded test command ran in this review.

```powershell
dotnet restore MoleHill.sln
dotnet build MoleHill.sln --no-restore -p:SkipGrasshopperLibraryCopy=True
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore
dotnet test tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj --no-restore
dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj --no-restore
dotnet build MoleHill.sln -c Release --no-restore -p:SkipGrasshopperLibraryCopy=True
```

Performance fixture example, in a dedicated PowerShell session:

```powershell
$env:MOLEHILL_PERF = '1'
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj -c Release --filter FullyQualifiedName~MeshAreaTopologySplitterScalingBenchmarkTests --logger 'console;verbosity=detailed'
Remove-Item Env:MOLEHILL_PERF
```

Review the benchmark's sizes before running it on a constrained machine. For native acceptance,
follow [rhino-live-testing.md](rhino-live-testing.md): owned disposable slot, exact plugin identity,
document-state assertions, real UI capture when relevant, exact-slot cleanup. Never count native
skips as execution. Package through `build-yak-package.ps1` only; publishing is a separate release
decision. Verify archive contents, installation, Interop identity, and actual plugin discovery.

## Ongoing hygiene rules

- Keep fixes, behavior-preserving moves, and algorithm changes in separate commits when practical.
- Preserve the grading tier cascade and diagnostics. Optimize a fallback only with cases that reach it.
- Require an owner, invalidation rule, lifetime, and memory expectation for each new cache.
- Require algorithm/version and all relevant geometry/settings in persistent cache keys; test changes
  to dependencies rather than adding blanket invalidation that defeats reuse.
- Prefer measured allocation/index changes over speculative pooling, SIMD, or more parallelism.
- Preserve linked-source test coverage while adding tests of the actual shipped assemblies.
- Keep persisted identifiers, JSON discriminators, GH component GUIDs/ports, and Interop versions stable
  unless an explicit migration is included.
- Preserve feature-family, slope-unit, annotation-style, and layer-role conventions with focused guards.
- Treat documentation and benchmark acceptance as part of the change, not a later cleanup sprint.

The desired result is a codebase where expensive work is measured and cancelable, geometry ownership
is explicit, host lifecycle behavior is testable, and a green validation report says precisely what
was exercised. That is a more useful hygiene standard than smaller files or fewer lines alone.
