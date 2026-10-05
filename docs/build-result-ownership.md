# Build-result ownership

Who owns each piece of geometry a terrain build touches, who may dispose it, and where the current
code has no obvious endpoint. This is R05's first deliverable from
[the 2026-09-19 review](codebase-review-and-implementation-plan-2026-09-19.md): the review asks for the
table *before* any ownership object is introduced, because the failure mode being guarded against is a
`Dispose()` on something that was only borrowed.

Status: **characterization only.** Nothing in this document has been changed in code. It is written
from the current sources (`TerrainRuntimeCache`, `TerrainController`, `TerrainController.Build`,
`TerrainBuildService.Cache`) so the gaps below are claims about code that can be checked, not
speculation.

## The two caches

A terrain has one **main** `TerrainRuntimeCache`, held by `TerrainController` per document and terrain.
Before a background build starts, `CreateWorkerCopy()` produces a **worker** cache for that build.

The copy is shallow where it matters:

- `TinEngine` is **shared**, deliberately — that is what keeps incremental edits reachable from a
  background build. `TinEngine.Build` serializes internally, so an old and a new worker queue rather
  than race.
- Each stage entry is re-created by `TerrainRuntimeCacheCloner.CloneStageCacheEntry`, but `MeshOutput`
  is copied **by reference**, and `AnalysisOutput`, `ZoneObjects`, `AuxiliaryObjects`, `MarkerObjects`,
  `ScatterObjects`, `ObjectPlacements`, the constraint lists and the diagnostics lists are copied as
  **the same list objects**. Only `TerrainRegions`, `ZoneAnalysisOutput` and `RuntimeOverlays` are
  deep-copied.

  (There is a second, same-named `CloneStageCacheEntry` in `TerrainBuildService.Cache.cs`. It re-stamps
  a cached entry's fingerprints rather than making a worker copy, and it deep-copies `AnalysisOutput`
  where this one does not. Read the qualified name before reasoning about either.)

So a worker cache is a mixture of borrowed entries (carried over from the main cache) and, once the
build runs, newly produced entries of its own. Nothing in the type distinguishes them.

## Ownership table

| Geometry | Held by | Owner | Disposed by | Notes |
|---|---|---|---|---|
| `StageEntries[].MeshOutput` on the **main** cache | main cache | main cache | `ReplaceBuildCachesFrom` (returns displaced meshes) → `DisposeDisplacedCacheMeshesWhenSafe`; `Clear()`/`DetachMeshOutputs()` on reset and terrain deletion | Disposal is deferred until retired workers finish, because a retired worker may still hold the same reference |
| `StageEntries[].MeshOutput` carried into a **worker** cache by the clone | worker cache | **main cache (borrowed)** | nobody — correctly | `PruneUnused` explicitly removes without disposing, and says why: the worker holds shallow refs into the main cache |
| `StageEntries[].MeshOutput` newly produced **by** a worker build | worker cache | worker cache until merge, then the main cache | `ReplaceBuildCachesFrom` on success — **nothing on any other path** | See gap 1 |
| `ZoneObjects` / `AuxiliaryObjects` / `MarkerObjects` / `ScatterObjects` (`GeneratedRhinoObject`) | stage entries, both caches | ambiguous — the same `List<T>` instance is in both caches | nobody | `GeneratedRhinoObject` is not `IDisposable`, yet holds `Geometry` plus cached preview `Mesh[]`, `Curve[]` and `TextEntity` instances. See gap 2 |
| `GradingTopologyEntries` / `SmoothEntries` | both caches | value types and arrays only | n/a (managed) | `GradingTopologyEntries` and `SmoothEntries` are copied by reference into the worker and replaced wholesale on merge |
| `TinEngine` | both caches | main cache | never disposed; `InvalidateCache()` on detach | Shared by design; a worker must never `Clear()` it |
| `DisplayState` | main cache only | main cache | replaced on publication | Not copied into the worker cache at all |
| Snapshot section terrain meshes | the build snapshot | the worker | `finally { snapshot.DisposeSectionTerrainMeshes(); }` in the worker body | The one path in this area with an unconditional endpoint on every exit |
| `CancellationTokenSource` per worker | `TerrainRebuildState` | the controller | `RetireRunningWorker`, deferred to the task's continuation when the worker is still running | Already correct; the model the geometry paths should follow |
| Baked document objects | the Rhino document | the document | Rhino | Out of scope here — object ids, not geometry instances |

## Gaps

These are the places where no obvious endpoint exists. None of them is demonstrated to be a permanent
leak: managed reachability and Rhino's own finalization may reclaim the memory. What they cost is the
ability to *prove* peak native memory and safe cleanup — which is the point of R05.

**Gap 1 — an abandoned worker cache is dropped, not discarded.** `ExecuteBackgroundBuild` returns the
worker cache on every path, including `catch (OperationCanceledException)` and `catch (Exception)`. On
the success path `ApplySuccessfulBuild` merges it. On the cancelled, superseded and failed paths
`CompleteBackgroundBuild` returns *before* the merge, so `result.WorkerCache` simply goes out of scope. Any
mesh that build had already produced — a completed TIN stage before cancellation landed in a later one
— is released to the GC rather than disposed. The same applies when a result arrives for a stale
generation.

**Gap 2 — generated geometry has no owner at all.** `GeneratedRhinoObject` carries a `GeometryBase`
plus lazily built preview `Mesh[]`, `Curve[]` and `TextEntity` caches, and nothing disposes any of it.
`ReplaceBuildCachesFrom` tracks `MeshOutput` only. Because the clone shares the *list instances*, a
displaced generated object cannot be distinguished from a retained one by reference comparison of the
lists the way meshes are.

**Gap 3 — `PruneCompletedRetiredWorkers` forgets a worker without asking what it produced.** It removes
completed tasks from `RetiredWorkers` purely on `IsCompleted`. A retired worker completes by finishing
its build, so it completes *holding a result* — including a worker cache full of newly produced
geometry that will never be merged. The prune is what unblocks deferred disposal of displaced meshes,
which is correct; it just has no counterpart for the retired worker's own output.

**Gap 4 — document close and terrain deletion converge on `DetachMeshOutputs`, which only knows about
meshes.** The same blind spot as gap 2, at the other end of the lifetime.

## What the code already gets right

Worth stating, because the fix must not regress it:

- **Deferred disposal is real and necessary.** `DisposeDisplacedCacheMeshesWhenSafe` waits for retired
  workers before disposing displaced meshes, precisely because those workers borrowed them.
- **`PruneUnused` refuses to dispose**, with the reason in a comment: it runs on the worker cache, whose
  mesh refs belong to the main cache.
- **`Clear()` is dangerous on a worker copy** and is only called on the main cache. It disposes mesh
  outputs and invalidates the shared `TinEngine` — doing that from a worker would corrupt the main
  cache's live meshes and the engine both workers share.

## The shape of the fix (not yet implemented)

The review's proposal, restated against this table:

1. A build result owns a set of geometry, explicitly separated into **borrowed** (came from the main
   cache) and **owned** (produced by this build).
2. `Transfer` hands owned geometry to the main cache on a successful merge; ownership moves, so the
   result no longer discards it.
3. `Discard` disposes only owned geometry, never borrowed, and is called on **every** non-merge exit:
   cancelled, superseded, stale generation, failed, reset, terrain deleted, document closed.
4. Generated objects are tracked alongside primary meshes, which means `GeneratedRhinoObject` needs a
   disposal endpoint for its geometry and preview caches.
5. `Clear()` stays off worker copies.

Acceptance (from the review): successful merge, canceled build, superseded result, reset, terrain
deletion, document close, and an exception thrown before *and* after geometry creation. Repeated rapid
edits must settle to bounded retained memory with no use-after-dispose and no stale publication, and
the measurement must include native private bytes, not only managed allocation. That last part needs a
native soak run, so it cannot be closed from the managed test suite alone.

This work is coordinated with R03 (cancellation), which is what makes the abandoned-result paths common
enough to matter, and precedes R06 (scheduler extraction), which needs the ownership contract to be
explicit before the state machine can be moved.

## Source audit — 2026-10-04

The gaps above remain present at release `0fceebc` (1.2.1), but the original table is not a complete
inventory. This audit is a source trace, not a native memory measurement or proof of a permanent leak.

### Build outputs and cache outputs are different allocations

`StoreMeshStageCache` duplicates the computed mesh into `StageEntries[].MeshOutput` and returns the
computed mesh for subsequent stages. `RestoreCachedMeshStage` also duplicates the cache mesh. The final
`TerrainBuildResult.PrimaryMesh` is therefore not covered simply by disposing displaced cached meshes.
`UpdateDisplayState` hands that build mesh to `TerrainDisplayState.TerrainMesh`, with no disposal of the
previous display state's meshes. The baseline mesh is another duplicate made in `ApplyTriangulateStage`.
Intermediate stage meshes can also lose their last build reference when `CurrentMesh` is replaced.

Tracking only the worker cache's final contents misses those intermediate allocations, and misses
allocations made before a stage is cached or before a failed build returns a `TerrainBuildResult`.
Allocation registration must happen when geometry is acquired, not only when completion is processed.

### Superseded does not always mean unpublished

`CompleteBackgroundBuild` can call `PublishSupersededGeometry`, which publishes the result's actual
`PrimaryMesh` and `BaseMesh` without merging its worker cache. They remain visible until the next
display-state replacement. A blanket discard of every superseded result would dispose visible geometry.
Ownership must transfer the published subset to the display state and discard only the unused subset.
Preview/interim states also carry generated objects forward by reference, so an outgoing display state's
objects cannot all be disposed merely because its meshes changed.

Rejected interim callbacks are a simpler subcase: `PublishInterimGeometry` can return because the
document, terrain or generation no longer matches, leaving its unpublished duplicated meshes to GC.
Accepted copies need safe display retirement; rejected copies never acquired a display reader.

### More native families need accounting

| Family | Allocation / alias | Missing endpoint |
|---|---|---|
| Region boundaries | `TerrainRegionState.Duplicate` duplicates curves during worker copying and display publication | Discarded worker regions and outgoing display/cache regions |
| Runtime overlay meshes | `RuntimeOverlayPrimitive.Mesh` and `Clone` duplicate `RegionMesh` | Worker, cached and displayed overlay copies |
| Retaining-wall plans | `RetainingWallPlanEntries` carries plans containing Breps by reference across workers | Replaced/pruned plans and cache teardown |
| Generated preview geometry | Brep meshing, hatch explosion and text duplication in `GeneratedRhinoObject` | Replaced preview caches and generated-object retirement |
| Analysis preview meshes | `TerrainAnalysisPreviewBuilder` assigns fresh `PreviewTerrainMesh` values | Previous preview mesh when replaced |

The wall-plan alias deserves explicit treatment: on a newly computed plan the generated wall output
uses `wall.Brep` directly; on a cache hit it uses `DuplicateBrep`. Thus a cold plan and its generated
output share native geometry despite the plan type's comment describing a duplicate. Disposing either
independently would invalidate the other. Choose one consistent ownership contract before adding cleanup.

`GetPreviewBrepMeshes` drops invalid/empty meshes from `Mesh.CreateFromBrep` without explicitly disposing
them. `GetPreviewHatchCurves` drops non-curves and all exploded geometry when the curve count exceeds
its cap. These rejected temporaries should be reclaimed immediately; retained previews need a lifecycle.

### Implementation order

1. Track build-owned geometry separately from borrowed cache geometry, including allocations before
   cache insertion and intermediate outputs. Discard must be idempotent and must not invalidate the
   shared TinEngine. Cover failed/canceled builds and already-completed workers retired before pickup.
2. Transfer ownership explicitly to the main cache and/or display state on successful or superseded
   publication. Account for aliases and generated outputs carried forward between states.
3. Retire displaced display/cache geometry only after worker and display/render readers release it.
   Extend accounting to plans, boundaries, overlays and lazy previews; reclaim rejected temporaries.
4. Verify merge/discard/retirement invariants, then measure native private bytes during repeated edits,
   cancellation, reset, terrain deletion and document close. Managed test success cannot establish the
   native memory bound.

No runtime cleanup was changed in this audit. Native acceptance still requires a Rhino host that can
launch the disposable test instance; the release session's host denied process breakaway.

## First fixes — 2026-10-05

The contained part of step 1, where the owner is unambiguous and nothing is drawing the geometry.
Display-state retirement, generated-object ownership and the other families above are **not** done.

- **Worker stage meshes are discarded on every non-merge exit (gaps 1 and 3).** `CreateWorkerCopy` now
  records which `MeshOutput`s it borrowed, by reference, and `DiscardOwnedMeshOutputs` disposes only
  the rest. It never touches `TinEngine`, is idempotent, and is a no-op after a merge because
  `ReplaceBuildCachesFrom` empties the worker's entries. It runs in a `finally` around
  `CompleteBackgroundBuild` and the synchronous build, when `TryCompleteFinishedBuild` finds the document
  or terrain gone, and in a continuation on every retired worker, whose result nobody reads. This is
  safe on the superseded-published path: stage-cache meshes are always clones (`StoreMeshStageCache`),
  never the `PrimaryMesh`/`BaseMesh` a display state holds. Cold-plan wall Breps
  (`RetainingWallPlanEntries`) a discarded worker made are still left to the GC.
- **The wall alias is gone.** The wall stage always publishes `wall.Brep.DuplicateBrep()`, so a cold
  plan and its output no longer share a native Brep — which is what `RetainingWallPlanCacheEntry`'s
  comment already claimed.
- **Rejected preview temporaries are disposed.** `GetPreviewBrepMeshes` disposes the invalid/empty
  meshes it filters out; `GetPreviewHatchCurves` disposes every exploded piece it does not keep.

Tests: `TerrainRuntimeCacheTests.DiscardOwnedMeshOutputs_*`. The two disposal tests are
`[RhinoNativeFact]`, so they skip under `dotnet test`; run inside a Rhino 8.35 slot they pass, with the
rest of the class (18/18).

## Native soak and the remaining families — 2026-10-05

Step 4's measurement, run in a disposable Rhino 8.35 slot against the build with the fixes above. The
controller was driven as a user drives it: source points and a wall rail moved with
`doc.Objects.Transform`, live update on, no build called directly. Private bytes were sampled before and
after a forced full GC (`Collect` + `WaitForPendingFinalizers`, three times); what survives the GC is
retained, and the gap is garbage only waiting for collection.

| Scene | Edits | Post-GC private bytes over the run | Peak |
|---|---|---|---|
| TIN only, 150×150 points (44k faces) | ~500, half rapid (builds superseded/cancelled) | 959 MB after setup, then 1014–1076 MB, no trend | 1221 MB, flat after round 1 |
| Same survey + Grade Pad, graded Retaining Wall, zone, Slope + Elevation (slope preview active), 0.5 m contours (125 auxiliary outputs) | 320, 80 of them wall-rail edits (fresh wall plan each), ~160 cancelled builds | 1137 MB after setup, then 1155–1201 MB, no trend | 1341 MB, flat |

The pre-GC excess was 50–150 MB, and it was almost all **managed** (heap 115–285 MB falling to ~90 MB
after collection), not native geometry. Nothing retained grows.

That settles the families this document left open, and the answer for them is **GC-owned, by design**,
not an ownership object:

- **Display-state meshes** (`TerrainMesh`, `BaseTerrainMesh`, `InterimTerrainMesh`, `PreviewTerrainMesh`
  including analysis previews), **generated objects and their preview caches**, **region curves** and
  **runtime overlays** have readers MoleHill does not schedule: the conduit, the RDK's cached
  `RenderMeshes` (kept by the render engine across frames, keyed by `RenderHash`), bake, Sculpt
  (`BaseTerrainMesh`) and the Grasshopper bridge. Explicit disposal would need every one of them to
  report when it is done, for no measured gain. They become unreachable when their display state or
  stage entry is replaced, and the finalizer releases the native side.
- **Retaining-wall plan Breps** are cache-owned and now never aliased (see First fixes), so a replaced
  plan is ordinary garbage on the same terms.
- **Never `Dispose()` anything reachable from a `TerrainDisplayState`.** That is the rule that makes the
  above safe; break it and the failure is a use-after-dispose inside a render engine.

What stays explicit is what has a single owner and no outside reader: stage-cache meshes (displaced on
merge, deferred past retired workers), worker-owned stage meshes (`DiscardOwnedMeshOutputs`), the
snapshot's section meshes, and rejected preview temporaries.

**A scheduler bug the soak found.** A debounced Final request queued while a build ran could be stranded
after that build was cancelled: dispatch ran only from `RhinoApp.Idle`, which Windows raises once when
the queue empties and not again until a new message arrives, and the wake timer stopped as soon as no
build was running. Observed: a request 33 s overdue, nothing building, timer stopped, until any message
arrived. Interactively a mouse move hides it; headless it never clears. The wake timer now also runs
while a request is pending and dispatches due requests on its tick (`EnsureBuildWakeTimer`). With the
same message pumping, settling went from never (10 s observed) to ~2.0 s every time across 70 settles.

How to repeat the soak: the scripts were driven through the `rhino-mcp` `run_csharp` tool, which the
router cancels at 300 s, so keep each call to a few rounds and read memory with `Process.PrivateMemorySize64`.
A pending-build wait must pump with `RhinoApp.Wait()`; never `Thread.Sleep` on the UI thread.

## Interim published geometry (added 2026-09-19)

`TerrainController.PublishInterimGeometry` shows a finished terrain mesh before its dependent outputs
settle. The mesh it displays is a **copy**, duplicated on the worker thread, held as
`TerrainDisplayState.InterimTerrainMesh`.

| Piece | Produced by | Held by | Disposed by |
|---|---|---|---|
| `InterimTerrainMesh` | worker, via `DuplicateMesh` | the interim `TerrainDisplayState` | nobody - collected |

Two deliberate choices:

- **It is a copy.** The stages that run after `PrimaryMesh` is assigned keep reading the original mesh,
  so displaying that instance would let a later stage mutate geometry the conduit is already drawing.
- **It is not disposed.** A conduit may be mid-draw when a display state is replaced, and
  `DisposeDisplacedCacheMeshesWhenSafe` covers build-owned meshes, not this one. Disposing here would
  risk tearing a live draw to reclaim one transient mesh per slow build. Bounded, not a leak in the
  unbounded sense: at most one live per terrain, replaced on the next publication.

This is characterization, matching the rest of this document. If the ownership object with
`Transfer`/`Discard` (R05 stage 2) is built, this mesh is a fifth case for it.
