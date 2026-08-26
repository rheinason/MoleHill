# Commit review — 2026-08-26

Review of the four commits authored and committed on 2026-08-26 (Europe/London), covering
`064ee71..b8c8f06`:

| Commit | Summary |
| --- | --- |
| `10a9fa8` | Add core analysis and LandXML primitives |
| `c446c44` | Add Grasshopper terrain exchange components |
| `7573804` | Add Rhino terrain analysis and interop workflows |
| `b8c8f06` | Document terrain analysis and exchange workflows |

The series changes 66 files (+3,247/-116). It adds useful, well-separated Core primitives and thin
host integrations, but the DEM and LandXML paths currently have interoperability/correctness defects
that should block release. Waterflow and zone analysis also have cache/aggregation regressions.

> **Resolution status:** All ranked findings and the additional hardening items were addressed in the
> follow-up working tree on 2026-08-26. The detailed sections below are retained as the original review
> record; the resolution summary records the implemented behavior.

## Resolution summary

| ID | Status | Resolution |
| --- | --- | --- |
| H1 | Fixed | DEM creation now decodes numeric single-band TIFF samples, applies scale/offset, filters NoData, and rejects RGB/multi-band imagery. |
| H2 | Fixed | LandXML now maps Northing/Easting correctly, parses/emits linear units, converts through document units, and honors project-base transforms. |
| M1 | Fixed | LandXML faces are retained in a hidden exact-TIN mesh source; edited/non-Delaunay topology is no longer rebuilt from points. |
| M2 | Fixed | Runtime-cache cloning copies every waterflow terminal counter, with a regression test. |
| M3 | Fixed | Zone slope numerators and weights accumulate across every output mesh, with an order-independence regression test. |
| M4 | Fixed | LandXML and GeoTIFF terrain creation use one undo record and delete created terrain/object state on failure. |
| M5 | Fixed | Waterflow builds an XY face index once and checks cooperative cancellation while indexing, locating starts, and tracing paths. |
| M6 | Fixed | Analysis migration gates on the captured source schema version, with a combined legacy-zone/range regression. |
| L1 | Fixed | Each analysis color panel is auto-expanded once; a user's later collapsed state survives refreshes. |
| L2 | Fixed | Partition breaklines are projected/clipped to each output mesh. Branches also use odd/even containment so nested loops form holes. |
| L3 | Fixed | Coverage now includes independent LandXML semantics/validation, numeric DEM formats and NoData, cache parity, cancellation, partition holes/clipping, and snapshot final-state eligibility. Rhino-native projection tests remain runtime-gated by design. |

Additional hardening is complete: all LandXML surfaces are streamed and imported, duplicate ids and
invalid face references are rejected, negative infinity maps to the low palette stop, and legacy
unit-system-only LandXML remains readable using metre/foot defaults.

## Executive summary

Severity definitions used here:

- **HIGH** — normal use can silently produce materially wrong geometry or coordinates.
- **MEDIUM** — incorrect/stale output, incomplete rollback, or a serious performance problem in a
  realistic narrower case.
- **LOW** — hardening, UX, or coverage issue with limited direct impact.

| ID | Severity | Finding |
| --- | --- | --- |
| H1 | HIGH | The new DEM workflow reads an 8-bit display red channel, not numeric DEM samples. |
| H2 | HIGH | LandXML coordinates and units do not follow exchange semantics, so external round-trips can transpose and scale terrain. |
| M1 | MEDIUM | LandXML face topology is parsed and reported, then discarded during import. |
| M2 | MEDIUM | Cached waterflow summaries lose boundary/sink/rejected counters. |
| M3 | MEDIUM | Multi-output zone average slope is calculated from only the final piece's numerator. |
| M4 | MEDIUM | Failed LandXML/DEM creation leaves imported document objects behind. |
| M5 | MEDIUM | Waterflow start lookup is `O(start points * mesh faces)` and is not cancellable within a path. |
| M6 | MEDIUM | Pre-v25 analysis range migration can be skipped after a legacy-zone migration mutates the schema version. |
| L1 | LOW | Analysis color settings cannot remain collapsed across a panel refresh. |
| L2 | LOW | Partition outputs retain every original breakline, including curves outside each piece. |
| L3 | LOW | The tests that exercise the new Rhino-native geometry behavior are skipped in this environment. |

## Findings

### H1 — DEM creation does not read elevation samples (HIGH)

**Where:** `src/MoleHill.Rhino/Services/DocumentCommandService.cs:455-485`, especially :462 and
:473-475; exposed by `src/MoleHill.Rhino/UI/MoleHillPanel.cs:310` and documented as raster-elevation
sampling in `docs/architecture.md:49-51`.

`SampleRasterAsTerrainPoints` converts the image to `System.Drawing.Bitmap` and calculates Z as
`bitmap.GetPixel(x, y).R * elevationScale`. This is an 8-bit display color channel (0-255), not the
numeric value of a GeoTIFF elevation band. A 16-bit integer or 32-bit floating-point DEM is truncated
through image rendering; an ordinary RGB orthophoto is silently accepted and converted into terrain
whose elevations are its red intensities. NoData values are not detected either.

This produces plausible-looking but materially false terrain without an error.

**Recommended fix:** use a TIFF/GeoTIFF raster reader capable of reading the source sample format and
bit depth directly. Select a numeric elevation band, apply scale/offset metadata, reject unsupported
RGB-only imagery, and honor NoData. Add fixtures for unsigned/signed 16-bit and float32 DEMs, a NoData
cell, and an RGB GeoTIFF that must be rejected.

### H2 — LandXML coordinate order and units are not interoperable (HIGH)

**Where:** `src/MoleHill.Core/Interop/LandXmlCodec.cs:31-38` and :61-75;
`src/MoleHill.Rhino/Services/LandXmlSurfaceService.cs:26-35`;
`src/MoleHill.Rhino/Services/DocumentCommandService.cs:296-315`.

There are three coupled problems:

1. The codec reads and writes point text as `X Y Z`. LandXML coordinate locations are
   Northing/Easting/Elevation, i.e. Rhino `Y X Z`. Standard consumers therefore interpret exported
   X as northing and Y as easting, and standard imports are transposed on entry.
2. The writer always emits an empty `<Metric />` element. It does not declare `linearUnit` (or the
   other unit attributes expected by interoperable LandXML 1.2 documents).
3. Import ignores the file's `Units`; export neither represents the Rhino document unit system nor
   converts coordinates to the hard-coded metric system. A feet or millimetre document therefore
   exports raw model-unit numbers under a metric label, while an imperial LandXML file imports raw
   feet as model units.

The existing `WriteRead_RoundTripsTinSurface` test passes because the reader and writer share the
same coordinate-order mistake and it only tests the writer's own unitless-metric output.

**Recommended fix:** make the DTO or codec convention explicit and convert between LandXML
Northing/Easting/Elevation and Rhino X/Y/Z at one boundary. Parse and validate `Units`, return a
linear-unit descriptor with the surface, and convert through `ModelUnitContext` during import/export.
Emit a complete Metric or Imperial unit element. Add fixtures produced independently of MoleHill and
schema/interoperability tests, including asymmetric X/Y values and metre/foot/millimetre cases.

Reference: Autodesk documents that LandXML coordinates are Northing, Easting, Elevation (`Y,X,Z`)
and that units participate in import/export:
[Supported LandXML data](https://help.autodesk.com/cloudhelp/2025/ENG/Civil3D-UserGuide/files/GUID-4D10ABA5-5EA0-41A8-BB61-C3F446CE7C6B.htm).

### M1 — LandXML import discards the source TIN faces (MEDIUM)

**Where:** `src/MoleHill.Core/Interop/LandXmlCodec.cs:40-50` parses `Triangles`, but
`src/MoleHill.Rhino/Services/LandXmlSurfaceService.cs:26-39` creates point objects only and calls
`CreateTerrainFromPointIds`.

Every imported face is ignored and the ordinary triangulate modifier builds a new TIN from points.
For non-unique Delaunay cases, edited/non-Delaunay surfaces, holes, or intentionally omitted faces,
the imported terrain can differ from the source even though the command reports the source face
count. This is effectively a "quick import," not a topology-preserving TIN import.

**Recommended fix:** either preserve faces in a managed mesh/TIN source and make that the default, or
explicitly label this command as a point-only/retriangulating import and warn whenever faces were
present. Add a fixture whose face diagonal differs from the Delaunay result. Autodesk's own LandXML
import documentation distinguishes point-only quick import from full point-and-face import:
[LandXML import settings](https://help.autodesk.com/cloudhelp/2019/ENG/Civil3D-UserGuide/files/GUID-FB6846E6-9E14-4C2D-B06A-E96FBD1399DB.htm).

### M2 — Waterflow cache hits erase end-state counts (MEDIUM)

**Where:** `src/MoleHill.Rhino/Model/TerrainAnalysisSummary.cs:45-49` adds
`WaterflowBoundaryCount`, `WaterflowSinkCount`, and `WaterflowRejectedCount`, but
`src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs:477-507` does not copy them in `CloneAnalysis`.
Cache restoration uses that clone at `src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:80-92`.

The first final build reports correct waterflow end counts. An unchanged cached rebuild restores the
same paths but presents all three counters as zero in the panel. The generated-output count survives,
making the summary internally inconsistent.

**Recommended fix:** copy all three fields in `CloneAnalysis`. Add a non-Rhino unit test for the
cloner and a cache-hit regression asserting the entire summary before/after the cached rebuild.

### M3 — Multi-piece zone average slope is wrong (MEDIUM)

**Where:** `src/MoleHill.Rhino/Services/ZoneAnalysisCalculator.cs:40-61`.

`slopeWeighted` is reset for each mesh, while `elevationWeight` accumulates the plan area of every
mesh. Line 61 then overwrites `SlopeAveragePercent` with:

```text
current mesh slope numerator / all meshes' accumulated area
```

For a zone that resolves to multiple outputs, earlier pieces contribute to the denominator but not
the numerator. The final result is order-dependent and generally too small.

**Recommended fix:** accumulate a `slopeWeightedSum` outside the mesh loop (or maintain a separate
total slope weight), then assign the average once after all meshes. Extend
`Summarize_MultipleOutputs_AggregatesWithoutChangingZoneIdentity` with two non-flat pieces of
different plan areas and reverse their input order.

### M4 — Import failures leave partial document state behind (MEDIUM)

**Where:** `src/MoleHill.Rhino/Services/LandXmlSurfaceService.cs:26-37` and
`src/MoleHill.Rhino/Services/DocumentCommandService.cs:406-448`, :468-485.

LandXML adds point objects one by one and returns failure without deleting earlier points if a later
add or terrain creation fails. The DEM workflow similarly leaves the placed picture frame and sampled
points if managed-terrain creation fails. Repeating the command accumulates orphaned geometry.

**Recommended fix:** treat each import as a transaction: track every created ID, delete all of them
on any failure/cancellation, and only retain them after the managed terrain is saved successfully.
Prefer one Rhino undo record for the complete operation.

### M5 — Waterflow tracing has an avoidable large-mesh stall path (MEDIUM)

**Where:** `src/MoleHill.Core/Analysis/WaterflowTracer.cs:76-97`, :247-283;
`src/MoleHill.Rhino/Services/TerrainBuildService.Waterflow.cs:37-54`.

For every start point, `FindContainingFace` linearly scans every face before tracing begins. This is
`O(P*F)` for P sources and F faces. `Trace` also has no cancellation callback; the Rhino build checks
cancellation only after an entire path returns. On the large TIN sizes this repository explicitly
targets, a collection of waterflow starts can make edits slow to cancel and expensive to rebuild.

**Recommended fix:** build a reusable XY face index once per trace call (uniform grid/BVH/R-tree),
query candidate faces for each start, and accept a cancellation callback checked during face lookup
and transitions. Add a large deterministic performance test with many starts.

### M6 — Analysis auto-range migration uses a schema version that another migration mutates (MEDIUM)

**Where:** `src/MoleHill.Rhino/Services/TerrainSerializer.cs:57-84`, :107-140, :256-268.

`Deserialize` correctly captures `sourceSchemaVersion`, but `MigrateAnalyses` tests
`terrain.SchemaVersion < 25` instead. `MigrateZones`, called immediately beforehand, sets
`terrain.SchemaVersion` to the current version whenever it migrates a legacy Mesh Collage or Mesh
Areas modifier. In such documents the analysis migration no longer sees the pre-v25 version, so an
existing explicit non-zero analysis range is silently changed to the new default `AutoColorRange =
true` behavior.

**Recommended fix:** pass `sourceSchemaVersion` into `MigrateAnalyses` and use it for the gate, as
`MigrateRemeshModifiers` already does. Add one serialization test combining a legacy zone modifier
with an explicitly ranged slope/elevation analysis.

### L1 — Color settings re-open on every panel refresh (LOW)

**Where:** `src/MoleHill.Rhino/UI/MoleHillPanel.cs:2535-2542`.

`CreateAnalysisColorSettings` unconditionally inserts the analysis ID into
`_expandedAnalysisColorSettings` before reading the expansion state. A click can hide the current
control, but the next UI rebuild adds the ID again and expands it. The set therefore does not persist
the user's collapsed choice.

**Recommended fix:** initialize expansion state only when the analysis/card is first introduced, or
keep a collapsed-ID set whose default is expanded.

### L2 — Partitioned terrains carry unpartitioned breakline metadata (LOW)

**Where:** `src/MoleHill.Grasshopper/Components/PartitionTerrainComponent.cs:163-189`.

Every zone piece and the remainder receives the complete original `terrain.Breaklines` collection.
After deconstruction, a piece can therefore claim constraints that lie wholly outside its mesh;
reconstructing or passing the package to a downstream consumer can reintroduce irrelevant geometry.

**Recommended fix:** clip/filter breaklines to each output mesh's XY domain, or document that
breaklines are source provenance rather than constraints applicable to the piece. Add a test with two
zones and one breakline wholly contained in only one zone.

### L3 — New host behavior is only partially exercised by today's automated run (LOW)

The new Core tests run, but the two `ZoneAnalysisCalculatorTests` and
`MoleHillTerrainData_DuplicatesOpenGeometryInputs` are `RhinoNativeFact`s and were skipped. The
Grasshopper port-registration smoke test passed, but it does not solve Construct/Deconstruct,
Partition, or the reflection snapshot component. There are also no tests for:

- external LandXML coordinate order, units, multiple surfaces, invalid face references, or import
  topology;
- numeric DEM sample formats and NoData;
- waterflow cache-hit summary parity or large-mesh cancellation;
- snapshot invalidation/subscription and final-vs-preview rejection;
- partitioned breakline/region semantics.

These gaps explain why the solution is green despite H1-H2 and M1-M3.

## Additional potential issues

These did not rise to ranked defects but are worth hardening:

- `LandXmlCodec.Read` selects only the first `<Surface>` and silently ignores additional surfaces.
  The command should offer selection or report that only one was imported.
- `LandXmlCodec.Read` accepts duplicate point IDs and faces that reference missing point IDs. Validate
  IDs and face references in Core before creating document state.
- The DOM-based `File.ReadAllText` + `XDocument.Parse` path duplicates large LandXML files in memory.
  A streaming reader would be safer for survey-scale TINs.
- `AnalysisColorMapper.Sample` maps both positive and negative infinity to the final palette stop.
  Negative infinity should map to the low stop if callers can supply signed unbounded values.
- A zone branch with multiple nested closed boundaries is treated as the union of all interiors by
  `PartitionTerrainComponent`; it does not encode holes. Clarify/validate this input convention.

## Validation performed

| Check | Result |
| --- | --- |
| `dotnet test MoleHill.sln --no-build --no-restore` | 673 passed, 0 failed, 71 runtime-gated skips |
| Core tests | 483 passed, 0 skipped |
| Rhino tests | 156 passed, 61 runtime-gated skips |
| Grasshopper tests | 34 passed, 10 runtime-gated skips |
| `dotnet build MoleHill.sln --no-restore -p:SkipGrasshopperLibraryCopy=True` | Succeeded; 0 warnings, 0 errors |
| `git diff --check` | Clean (line-ending notices only) |

## Recommended order of work

1. Replace red-channel DEM sampling and reject unsupported rasters (H1).
2. Correct LandXML coordinate/unit semantics and add independent fixtures (H2).
3. Decide whether LandXML import promises exact TIN topology; preserve faces or disclose quick import
   semantics (M1).
4. Fix waterflow cache cloning and zone slope accumulation (M2-M3); both are small, targeted changes.
5. Add transactional cleanup and waterflow spatial indexing/cancellation (M4-M5).
6. Correct the schema-version migration gate and close the host-level test gaps (M6/L3).
