# Handoff: GC stall attribution (2026-10-06)

Branch `codex/grade-pad-mesh-performance` (unpushed):

- `b7dda04` (Codex): reduce grading allocation pressure. Unchanged faces are now shared, changed faces are
  compacted in place, and the boundary-edge scan uses endpoint buckets.
- `dace974`: hold normalizer scratch strongly so large allocations stop waiting on GC. Re-baselined.

## What we found

Cold Grade Pad Output Mesh is a 6 ms stage. Its baseline median swung between 7 and 37 ms with no code
change, because half the cold samples took 35 ms. A per-call probe showed each slow call had a background
gen2 collection inside it. The GC *pause* was only 0.5 ms. The rest was the stage's own large-object
allocations waiting for that collection to finish.

The weakly held `ArrayPool` from `b7dda04` was emptied by every full collection, including the
`GC.Collect` the lane runs between samples. Each call therefore reallocated about 3.4 MB of scratch,
right when that was most expensive. `dace974` replaces it with a strongly held `Scratch<T>`: four slots
per element type, smallest buffer that fits, an eighth of headroom on a miss, and nothing over 8M
elements kept. It also carves the 64 duplicate-check tables out of one buffer.

Results, hosted lane, 9 samples:

| Metric | Before | After |
|---|---|---|
| Allocation per Output Mesh call | 4.7 MB | 1.4 MB (its result only) |
| Cold Grade Pad Output Mesh | 33.9 ms | 6.1 ms (9 of 10 cold calls fast) |
| Cold Grade Pad | 100.2 ms | 74.6 ms |
| Cold wall | 626 ms | 620 ms |
| Pad-edit wall | 351 ms | 351 ms |
| Cold Grade Path Constraints | 32 ms | 44 ms median (already bimodal: min 15, p95 48) |

Finished meshes are identical to the baseline in all 12 phases. Core tests: 1,267 passed.

The stall partly *moved* to the next large allocation, so a broad allocation sweep would mostly chase it
from stage to stage.

## Next steps (agreed; step 1 not started, no files touched)

1. **Per-stage GC and allocation attribution in the hosted lane.**
   - Add an end timestamp (`DateTime.UtcNow`) to `TerrainBuildTiming`, set in
     `TerrainBuildResult.RecordTiming` (`src/MoleHill.Rhino/Services/TerrainBuildResult.cs`). The start
     time is that timestamp minus `Elapsed`.
   - In the lane only (`tests/MoleHill.Rhino.Tests/HostedPerformanceLane.cs`), add an `EventListener` on
     `Microsoft-Windows-DotNETRuntime` with the GC keyword. Record `GCStart`/`GCEnd` (generation, and
     whether it is background) and `GCAllocationTick` (large-object kind) with their timestamps.
   - Charge each collection and large-object allocation to the innermost stage window it falls in.
     Aggregate across samples into a **separate** result section, not `Metrics`, which is compared in ms.
     `PerfSampleRecorder.Details` keeps only the last sample, so it can't be used for this.
2. **If collections land inside the interactive warm edits,** cut large-object allocations on that path
   only. The 100k warm-edit wall is 27 ms median and 55 ms p95, which looks bimodal but is unverified.
   A 28 ms stall would roughly double those edits.
3. **Otherwise skip the sweep** and go after Remesh, the largest geometry-heavy stage at 210-245 ms.

## How to run the lane right now

`./validate.ps1 hosted-perf` fails here: the router reports `rhino_closed` during `run_csharp` and leaves
an orphaned Rhino holding the test DLLs. Stop only that PID. Workaround:

1. `dotnet build tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj -c Release --no-incremental -p:SkipGrasshopperLibraryCopy=True`
2. Write a request JSON. Use forward slashes in paths, and omit `BaselinePath` for a run you'll record.
3. MCP `spawn_slot`, then `run_csharp` with the body from
   `py -3 tools/rhino-hosted-perf.py --bin ... --request ... --print-script`, pointed at your request.
4. Wait for `ResultPath`, then `close_slot`.
5. `./validate.ps1 hosted-perf -HostedResult <file>`, adding `-UpdateBaseline` to record it. A result from
   a run without a baseline can only be recorded, not compared.

Don't run anything else while a hosted run is going: an overlapping test run contaminated one baseline.

Diagnostic trick: log `GC.CollectionCount(0/1/2)` deltas and `GC.GetTotalPauseDuration()` around each
sub-step to a file. If gen2 went up but the pause stayed under 1 ms, it was a background collection and
the time was an allocation waiting on it.
