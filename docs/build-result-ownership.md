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
