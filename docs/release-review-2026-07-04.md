# Pre-release code review — 2026-07-04

Full sweep of architecture, stability, and performance ahead of release. Reviewed: Core engine
(TinEngine / caching / triangulation), Rhino build orchestration and threading, the sculpt subsystem
(including the then-uncommitted `SculptAnalysisColorizer` work), persistence and display, and
spot-checks of the grading core and GH components. The original review build was clean with 531 tests
passing (31 Rhino-runtime-gated skips). The 2026-07-12 completion audit passes **all 534 runnable
tests** with 39 expected runtime-gated skips.

**Overall assessment:** the architecture is in genuinely good shape — clean Core/host separation,
disciplined flat-array pipeline, registry-driven type system, fingerprint-cached staged builds, and a
carefully reasoned sculpt display-lock design. Exception discipline is excellent (two empty catches in
the whole tree). The findings below are ranked; the two HIGH items should block release, the MEDIUMs
are judgement calls, the LOWs are polish.

## Completion audit — 2026-07-12

- [x] H1 — incremental-edit elevation/source mapping.
- [x] H2 — unreadable document JSON protection and backup.
- [x] M1 — retired-worker mesh lifetime protection.
- [x] M2 — consistent Triangle.NET serialization across Core and Grasshopper.
- [x] M3 — sculpt working-mesh disposal.
- [x] M4 — allocation-free steady-state conduit drawing and cached marker labels.
- [x] P1 — persistent `TinEngine` reuse in Rhino worker caches.
- [x] L1–L6 — low-severity hardening and housekeeping.
- [x] `SculptAnalysisColorizer` selection parity verified in code: sculpt and baked preview use the
  same first-enabled, preview-capable analysis selector.
- [x] Full solution validation: 534 passed, 0 failed, 39 skipped (`dotnet test MoleHill.sln
  --no-restore`).
- [x] Release build: 0 warnings, 0 errors (`dotnet build MoleHill.sln -c Release --no-restore`).
- [ ] Manual Rhino smoke test: exercise a long sculpt session and orbit several hundred markers in
  multiple viewports while watching working set and frame time. This requires an interactive Rhino
  session and is the only remaining non-automated release check from this review.

Severity legend: **HIGH** = silent data corruption or data loss reachable by normal use.
**MEDIUM** = crash/leak/stale-state risk under realistic-but-narrower conditions.
**LOW** = polish, hardening, minor perf.

---

## H1 — TinEngine incremental edit silently corrupts elevations (HIGH)

**Status: Fixed 2026-07-04.** `Vertex.ID` is re-keyed to the current input index after every
incremental add/delete (`TinEngine.TryApplyIncrementalEdit`); `TinResult` now carries `SourceIds`
end-to-end (through boundary culling) and `WithUpdatedZ` maps through it instead of by output
position. Regression tests added in `TinEngineTests.cs` (remove/insert mid-list, Z-only change after
an incremental edit). P1 (share `TinEngine` into Rhino worker copies) implemented as a follow-on now
that this is safe — see `TerrainRuntimeCache.CreateWorkerCopy()`.

**Where:** `src/MoleHill.Core/Engine/TinEngine.cs` — `TryApplyIncrementalEdit` (:298-400),
`BuildResult` source-ID mapping (:686-696); `src/MoleHill.Core/Engine/TinResult.cs` —
`WithUpdatedZ` (:51).

**The defect.** `BuildResult` assigns Z by `zValues[srcId]` where `srcId` is Triangle.NET's
`Vertex.ID`. On a full rebuild IDs are assigned `0..n-1` in input order, so this is correct. But the
incremental path edits the *cached* mesh in place:

- **Remove:** deleting input point *k* (not the last) shifts every later input index down by one,
  while the surviving mesh vertices keep their old IDs. Every vertex with old ID > *k* now reads the
  wrong `zValues` slot — the whole tail of the point cloud gets its neighbor's elevation.
- **Add:** `Mesh.TryInsertPoint` assigns `id = hash_vtx++` (`src/TriangleNet/Mesh.cs:310`). That ID
  only matches the new point's input index if the point was appended at the *end* of the input list.
  Inserted mid-list, every later point reads a shifted Z and the new point reads someone else's.

The failure is **silent** — XY topology stays correct, only Z is scrambled, appearing as
inexplicable spikes/steps after deleting or inserting one survey point.

**Compounding defect:** after any incremental edit, the *Z-only shortcut* (:166-187) is also wrong.
`TinResult.WithUpdatedZ` maps `zValues[i]` to output vertex *i* by **position**, and position comes
from `mesh.Vertices` dictionary-enumeration order — which no longer matches input order once a
deletion has punched a hole in the vertex dictionary.

**Exposure.** Grasshopper: fully exposed — `TinFromPointsAndBreaklines` persists `_engine` across
solves (`src/MoleHill.Grasshopper/Components/TinFromPointsAndBreaklines.cs:15`).
`PointCloudProcessor.Merge` orders breakline vertices first, spot points after, so editing a **spot
point** never changes segment indices and sails straight past the `AreSegmentsEqual` guard — the bug
triggers on ordinary spot-point edits even with breaklines present. Rhino: currently *unreachable*
(each background build gets a fresh `TinEngine` — see P1), but that also means P1 can't be fixed
before this is.

**There is no test coverage of the incremental path at all** (no test references
`TryApplyIncrementalEdit`, `TryInsertPoint`, or `TryDeletePoint`).

**Fix (recommended): re-normalize `Vertex.ID` after every successful incremental edit.**
In `TryApplyIncrementalEdit`, after the insert/delete succeeds and before `BuildResult`:

```csharp
// Re-key mesh vertex IDs to the CURRENT input indices so BuildResult's
// zValues[srcId] lookup stays valid after the arrays shifted.
foreach (var v in _cachedMesh.Vertices)
{
    v.ID = currentIndexByKey.TryGetValue(XyKey.FromValues(v.X, v.Y), out int inputIndex)
        ? inputIndex
        : -1; // Steiner / unknown -> BuildResult interpolates
}
```

(You already have `currentIndexByKey` in scope. `Vertex.ID` is settable — full rebuild already
assigns it. Also rebuild `_topologyState.VertexIdByKey` from the same pass instead of patching it
piecemeal.)

**Fix the Z-only shortcut structurally** so it can never desynchronize again: store the
source-index map on the result and index through it —

1. Add `int[] SourceIds` to `TinResult` (the `extracted.SourceIds` slice `BuildResult` already has,
   post-cull compacted with the same `NewToOld` remap).
2. `WithUpdatedZ(zValues)`: `newVerts[i*3+2] = SourceIds[i] >= 0 && SourceIds[i] < zValues.Length ?
   zValues[SourceIds[i]] : Vertices[i*3+2]` (keep interpolated Steiner Z).
3. Replace the current `zValues.Length == VertexCount` guard with
   `zValues.Length == inputVertexCount` (it currently conflates input count with output count; the
   guard passing at all is what makes the positional mapping load-bearing).

**Regression tests to add** (`tests/MoleHill.Core.Tests`, naming per convention):

- `Build_RemoveMiddlePoint_IncrementalEditKeepsCorrectZ` — build 10+ points with distinct Z, remove a
  middle one, assert every output vertex's Z matches its XY's input Z.
- `Build_InsertMiddlePoint_IncrementalEditKeepsCorrectZ` — same for a mid-list insert.
- `Build_ZOnlyChangeAfterIncrementalEdit_UpdatesCorrectVertices` — incremental edit, then change one
  Z and assert only that XY's vertex moved.
- Same three with breaklines present (spot edits must not disturb breakline Z).

---

## H2 — Unreadable terrain JSON in a .3dm: exception storm, and a silent-wipe path (HIGH)

**Status: Fixed 2026-07-04.** `TerrainDocumentStore.Load` now wraps `TerrainSerializer.Deserialize`
and returns `null` (plus a failure message) instead of throwing on truncated JSON or an unrecognized
`$type`. `TerrainController.DocumentState` carries a `LoadFailed` flag set from a `null` load; `Save`
early-returns while it's set, so the unreadable original is never overwritten. `Save` also stashes the
previous raw JSON under a `Terrains.backup` doc-string entry on every write (cheap insurance). The
panel surfaces a "Reset Terrain Data" toolbar button + status message while `LoadFailed` is set;
`TerrainController.ResetTerrainDataAfterFailedLoad` is the only path that clears the flag. Forward-compat
placeholder for unknown `$type` discriminators (item 4 below) was not implemented — the `LoadFailed`
guard is the safety net per the original recommendation. Regression tests in
`tests/MoleHill.Rhino.Tests/TerrainDocumentStoreTests.cs` (Rhino-runtime-gated, like the rest of the
suite).

**Where:** `src/MoleHill.Rhino/Services/TerrainDocumentStore.cs:13-17` (unguarded `Load`),
`src/MoleHill.Rhino/Services/TerrainController.cs:976-996` (`GetState` / `Save`),
`src/MoleHill.Rhino/Services/TerrainSerializer.cs:36-43`.

**Two distinct failure shapes:**

1. **Throwing JSON** (truncated payload, or a `$type` discriminator this build's registry doesn't
   know — e.g. a document saved by a *newer* plugin version): `TerrainSerializer.Deserialize` throws
   `JsonException`/`NotSupportedException`. `GetState` is called from **every** document event handler
   (`OnAddRhinoObject`, `OnIdle`, layer events, …), and nothing catches — so the user gets an
   exception on effectively every document interaction, with no way to reach the panel to fix
   anything. The failure never gets cached, so it re-throws forever.
2. **Quietly-empty JSON** (envelope shape mismatch → `envelope?.Terrains == null` → returns
   `new List<...>()`): `GetState` caches an *empty* state, and the next `Save` — triggered
   automatically by source-reference pruning, placement-transform sync, or any build — **overwrites
   the stored JSON with the empty list**. The user's terrain definitions are gone the moment they
   touch the document.

`LayerTemplateStore` already does "fall back to defaults if the settings file is invalid"; the
document store — where the stakes are much higher — has no equivalent.

**Fix instructions:**

1. Wrap the deserialize in `TerrainDocumentStore.Load` (or in `GetState`):
   ```csharp
   try { return TerrainSerializer.Deserialize(json, doc.ModelUnitSystem); }
   catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
   {
       RhinoApp.WriteLine($"[MoleHill] Could not read terrain data in this document: {ex.Message}. " +
           "Terrain state is read-only until resolved.");
       return null; // signal "unreadable", distinct from "no terrains"
   }
   ```
2. Give `DocumentState` a `LoadFailed` (or `IsReadOnly`) flag. When set — and also when
   `LoadJson` returned non-empty but `Deserialize` produced zero terrains — **refuse to `Save`**
   (early-return with a status message) so the original JSON is never clobbered. Only a deliberate
   user action (e.g. "Reset terrain data" in the picker `▾` menu) may clear the flag.
3. Before the *first* successful save over a document that had prior JSON, stash the original under a
   second doc-string entry (`Section = "MoleHill.Rhino"`, `Entry = "Terrains.backup"`). Cheap
   insurance; one string.
4. Forward-compat: in `TerrainJsonTypeResolver`, unknown `$type` discriminators currently throw. If
   `System.Text.Json`'s resolver hooks allow it, map unknowns to a `UnknownModifierDefinition`
   placeholder that round-trips its raw JSON (preserve-on-save); if that's more than you want before
   release, the `LoadFailed` guard in (2) is the safety net — ship at least that.
5. Test: `Deserialize_UnknownModifierType_DoesNotThrowOrDropSiblings` and a controller-level
   `Save_AfterFailedLoad_DoesNotOverwriteStoredJson`.

---

## M1 — Use-after-dispose race on stage meshes shared with retired build workers (MEDIUM)

**Status: Fixed. Audited 2026-07-12.** Retired worker tasks are retained in
`TerrainRebuildState.RetiredWorkers`; cache replacement returns displaced meshes instead of disposing
them inline; and `DisposeDisplacedCacheMeshesWhenSafe` waits for retired readers before disposal.
Completed workers are pruned from the idle pump. Runtime-cache removal uses the same deferred-disposal
path, and `TerrainRuntimeCacheTests.ReplaceBuildCachesFrom_ReturnsOnlyDisplacedMeshes` covers mesh
ownership transfer.

**Where:** `src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs` — `CreateWorkerCopy` (:29-47) with
`CloneStageCacheEntry` sharing `MeshOutput = entry.MeshOutput` shallowly (:611), and
`ReplaceBuildCachesFrom` disposing displaced meshes (:49-79);
`src/MoleHill.Rhino/Services/TerrainController.cs:1280-1305` (`RetireRunningWorker` cancels but does
not wait).

**Scenario.** Build A starts; its worker cache holds *shallow* references to the main cache's stage
meshes. The user edits again → `StartBackgroundBuild` retires A (cooperative cancel, no join) and
starts build B, whose worker cache shares the same meshes. B finishes and `ReplaceBuildCachesFrom`
disposes every main-cache mesh B didn't carry back. If A is still inside a non-cancellable section
(a Triangle.NET call can run for seconds; `RestoreCachedMeshStage`'s `DuplicateMesh` reads the shared
native mesh directly), A is now reading **disposed native memory** → access violation / Rhino crash.
The existing reference-equality guard only protects meshes the *completing* worker carries back — it
knows nothing about the *retired* worker.

**Fix (recommended — defer disposal until retired workers finish):**

1. In `TerrainRebuildState`, keep `List<Task> RetiredWorkers` — `RetireRunningWorker` adds the
   abandoned task instead of dropping it.
2. In `ApplySuccessfulBuild`, collect the displaced meshes (the set `ReplaceBuildCachesFrom`
   currently disposes) instead of disposing inline. If `RetiredWorkers` is empty (the common case),
   dispose immediately; otherwise
   `Task.WhenAll(retired).ContinueWith(_ => { foreach (m in displaced) m.Dispose(); })`.
   Prune completed entries from `RetiredWorkers` opportunistically (idle pump already exists).
3. Alternative if you prefer simplicity over peak memory: make `CloneStageCacheEntry` deep-copy
   `MeshOutput` (`DuplicateMesh`) — no shared native state at all. Measure first: the shallow copy
   exists precisely because worker-cache cloning is on the UI thread and timed (`workerCacheTimer`).

---

## M2 — Inconsistent Triangle.NET locking discipline (MEDIUM)

**Status: Fixed 2026-07-12 (Option A).** `TinEngine` already routed its constrained and fallback
triangulations through `TriangulationHelper.TriangulatePolygon`. The completion audit found and fixed
one remaining host bypass in `MeshCollageComponent`; all non-vendored production call sites in Core and
Grasshopper now use `TriangulationHelper` and therefore share its serialization lock.

**Where:** `src/MoleHill.Core/Engine/TriangulationHelper.cs:33-42,149-151` (global
`TriangulateLock` around every `GenericMesher.Triangulate`) vs
`src/MoleHill.Core/Engine/TinEngine.cs:539-551,614-618` (unlocked `Triangulate` calls).

Either Triangle.NET needs global serialization (then TinEngine races: a Rhino background build using
`TinEngine` can run concurrently with a GH solve or with `Parallel.For` grading paths that
triangulate) or it doesn't (then the global lock needlessly serializes all triangulation — it
currently defeats parallelism whenever grading tiers triangulate from parallel code).

Reading the vendored source: `RobustPredicates.Default` is safely double-check-locked and its statics
are read-only after the static ctor; the mutable static (`SweepLine.randomseed`) is only used by the
sweep-line algorithm, which `GenericMesher` doesn't use by default; `Configuration.RandomSource` is a
factory (each mesher gets its own `Random(0)`). So the lock is *probably* unnecessary — but the
codebase should pick one story:

- **Option A (safe, cheap):** route TinEngine's `Triangulate` calls through
  `TriangulationHelper.TriangulatePolygon` so everything shares the lock. One-line changes; keeps
  today's throughput.
- **Option B (faster):** delete the lock after a stress test (e.g. 8 threads × 1000 mixed CDT
  triangulations, assert identical outputs vs serial). Do this post-release if grading throughput
  matters.

Do not ship the current "half-locked" state — whichever assumption is wrong, it's wrong silently.

---

## M3 — Sculpt working meshes are never disposed (MEDIUM)

**Status: Fixed. Audited 2026-07-12.** The current sculpt workflow disposes its final working mesh in
the session `finally`, after `NotifySculptSessionEnded` releases the display lock. The DynTopo
`RefineUnderBrush` replacement path described below has since been removed (DynTopo is disabled), so
there are no intermediate replacement meshes to dispose.

**Where:** `src/MoleHill.Rhino/Services/SculptSessionController.cs` — `TryBindWorkingMesh` (:139),
`RefineUnderBrush` (:414-423), session end (`BeginSession` finally block).

Every DynTopo refinement rebuilds the working mesh and abandons the previous one
(`_workingMesh = BuildIndexParityMesh(...)`); the session-end path abandons the last one. These are
native Rhino meshes — the same "GC can't account for native memory" rationale that
`ReplaceBuildCachesFrom` documents. A long sculpt session with DynTopo on a large terrain leaks a
full mesh copy per refine event.

**Fix:** in `RefineUnderBrush`, keep the old mesh in a local, and after
`_controller.UpdateSculptPreviewMesh(...)` re-points the display lock, `old.Dispose()` (all on the UI
thread — the conduit can't be mid-draw). At session end (the `finally` in `BeginSession`, after
`NotifySculptSessionEnded` has rebuilt canonically and the display state no longer references the
working mesh), dispose `_workingMesh` and null the fields.

---

## M4 — Display conduit allocates native objects every frame (MEDIUM, perf)

**Status: Fixed 2026-07-12.** `DisplayMaterial` instances are cached by color/transparency; marker
geometry is cached; curves and text draw under `PushModelTransform`/`PopModelTransform`; substituted
`TextEntity` objects are cached on `GeneratedRhinoObject`; and the last per-frame label composition is
now cached there as well. Focused `GeneratedRhinoObjectTests` cover label and text-entity reuse (native
text tests remain Rhino-runtime-gated).

**Where:** `src/MoleHill.Rhino/Services/TerrainDisplayConduit.cs` — `CreateDisplayMaterial`
(:505-513, a new `DisplayMaterial` per mesh per frame per viewport, never disposed) and
`DrawMarkerGeometry` (:600-634, `DuplicateCurve()` / `Duplicate()` per marker per frame, never
disposed).

With a few hundred markers and multiple viewports this is thousands of undisposed native allocations
per second of navigation — measurable frame cost plus unbounded native churn between GCs.

**Fix instructions:**

1. **Materials:** cache `DisplayMaterial` per `(argb, transparency)` in a small static/per-conduit
   `Dictionary<(int, double), DisplayMaterial>`. Colors change rarely (layer edits, terrain color
   edits); clearing the cache from `RaiseStateChanged`-adjacent code or simply letting it grow (a few
   dozen entries max) is fine.
2. **Markers:** replace duplicate-then-transform with a model transform:
   ```csharp
   e.Display.PushModelTransform(generated.InstanceTransform);
   e.Display.DrawCurve(curve, color, 2);           // original geometry, untransformed
   e.Display.PopModelTransform();
   ```
   For `TextEntity` with substituted preview text, cache the substituted duplicate on the
   `GeneratedRhinoObject` (it's display-state-scoped and immutable per build) instead of rebuilding
   per frame.

---

## P1 — Rhino builds never benefit from TinEngine's caching (perf note, blocked on H1)

**Status: Fixed 2026-07-04**, after H1. `CreateWorkerCopy()` now shares the persistent `TinEngine`
via the object initializer instead of `init = new()`.


**Where:** `src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs:13,29-47`.

`CreateWorkerCopy()` gives every background build a **fresh** `TinEngine` (`init = new()`), and
`ReplaceBuildCachesFrom` never carries one back. The stage-fingerprint cache correctly skips the TIN
stage when inputs are unchanged, but whenever inputs *do* change — every dragged spot point, every
nudged breakline — the build does a full CDT from scratch, exactly the case the Z-only /
incremental-edit shortcuts were built for.

**Fix:** share the persistent cache's `TinEngine` with worker copies (`copy.TinEngine = TinEngine` —
it is internally serialized by `_gate`, so concurrent old/new workers queue rather than race). Note
`init` semantics: change the property to `get; set;` or pass through the object initializer.
**Do this only after H1** — sharing the engine makes the incremental-edit path reachable from Rhino.
Expected win: spot-point drags on large terrains go from full re-triangulation to an O(local) edit.

---

## Low-severity / polish

- **L1 — 32-bit fingerprints.** **Status: Fixed 2026-07-04.** Added Core
  `XxHash64Builder`; `InputSnapshot` now stores deterministic `ulong` XY/Z hashes, and
  `ComputeMeshFingerprint` hashes Rhino mesh vertices/faces through the same 64-bit stage
  `FingerprintBuilder` instead of `DataCRC`.
- **L2 — `catch { }` in TinEngine's plain-Delaunay fallback.** **Status: Fixed 2026-07-04.**
  Cancellation still propagates; other fallback exceptions append `ex.Message` to `errorMessage`.
- **L3 — Dead branch** in `PathGrader.ApplyPathGrading.cs`. **Status: Fixed 2026-07-04.**
  Removed the unreachable `interpolateOriginalZ != null` arm in the null-only path.
- **L4 — `Mesh.TryGetDeleteHandle`.** **Status: Fixed 2026-07-04.** Added a validated
  `vertex.tri` fast path, cache the found incident handle, and fall back to an ID scan when
  Triangle.NET dictionary hashes no longer match re-keyed `Vertex.ID` values after incremental edits.
- **L5 — FingerprintBuilder.** **Status: Fixed 2026-07-04.** The Rhino stage fingerprint builder now
  streams spans into Core `XxHash64Builder` instead of byte-at-a-time FNV-1a.
- **L6 — Housekeeping.** **Status: Fixed 2026-07-04.** `.codex/` is now ignored. The reviewed
  `SculptAnalysisColorizer` work remains safe to include in the release commit; no commit was created
  as part of this checklist pass.

---

## Follow-up reviewed: `SculptAnalysisColorizer`

`SculptAnalysisColorizer.cs` (new) + hookups in `SculptSessionController.cs` +
`SamplePaletteColor` made internal. **Verdict: correct — safe to commit.**

- `ColorAll` reads vertex normals; `BuildIndexParityMesh` computes face + vertex normals before both
  call sites (bind and DynTopo rebuild), so no empty-normals read.
- `Recolor` consumes `SculptNormalPatcher.LastTouchedVertices` (moved verts + one-ring) immediately
  after `PatchNormals`, before the buffer is reused — matches the documented contract. The one-ring
  is exactly the set whose normals (slope mode) changed; for elevation mode it's a harmless superset.
- The pinned color range at session start deliberately avoids mid-stroke rescaling, and the canonical
  rebuild at session end restores the per-face baked preview.
- Selection parity was verified in code on 2026-07-12: `SculptAnalysisColorizer.TryCreate` and
  `TerrainAnalysisPreviewBuilder.UpdatePreviewMesh` both select the first analysis satisfying
  `IsEnabled && SupportsTerrainPreview`. An interactive Rhino visual smoke test is still listed in
  the completion audit above.

---

## What was checked and found healthy

- **Build orchestration:** versioned request/apply with generation counters; builds complete on the
  UI thread via the idle pump; cancellation is cooperative and checked inside triangulation loops;
  stale results are discarded by version *and* generation. Sound design.
- **Sculpt session:** display lock + stroke-deferred dispatch + re-assert on background apply is a
  careful, documented dance and holds up under review; undo (stroke-level and document-level) is
  correctly layered; `GetAsyncKeyState` polling rationale is documented.
- **Displacement field:** tile math (`FloorDiv`, `Locate`), codec (deflate + base64, corrupt tiles
  skipped without failing the build), resample-on-cellsize-change, and `CellSize` pinning are all
  correct.
- **Serializer migrations:** schema 22 migration chain is defensive and idempotent-looking;
  normalization clamps are thorough.
- **Grading core:** spatial hashing everywhere it matters, `Parallel.For` on the per-vertex pass,
  prepared/pre-bounded path structures, deterministic seeding. No O(n²) hot loops found in the paths
  sampled.
- **Exception hygiene:** one empty catch block remains in `src/` (a UI theme probe). No
  sync-over-async, no blocking waits outside the completed-task idle pump.

---

## Completion record

All code findings H1–H2, M1–M4, P1, and L1–L6 are complete. Automated validation is recorded in the
completion audit and status notes above. The remaining manual gate is the interactive Rhino sculpt and
multi-viewport marker performance smoke test.
