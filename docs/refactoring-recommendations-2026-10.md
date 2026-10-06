# Refactoring recommendations — 2026-10

A sweep of `main` at `fff6ae5` (1.3.2-beta) for duplication, ad hoc code and modularity gaps that
built up over the ~520 commits since pre-1.0. This proposes work. It changes nothing. Open items
already tracked in [cleanup-plan.md](cleanup-plan.md) and the
[2026-09-19 review](codebase-review-and-implementation-plan-2026-09-19.md) (R06, R07, R11, R12) are
referenced rather than restated.

**The bar for every step is unchanged:** `dotnet build` clean, `./validate.ps1 managed` green, and
`./validate.ps1 hosted-perf` for anything on a build path. Do not touch `src/TriangleNet/**`.

## Scale

| Area | Lines | Files | Note |
|---|---:|---:|---|
| `MoleHill.Rhino/Services` | 48,400 | 170 | One flat folder |
| `MoleHill.Core/Grading` | 30,100 | 86 | |
| `MoleHill.Rhino/UI` | 15,200 | 47 | |
| `MoleHill.Core/Engine` | 13,500 | 32 | |
| `MoleHill.Shared` | 3,900 | 8 | `RetainingWallPlannerCore.cs` alone is 2,137 |
| Tests | 340,800 | 343 | ~250k of it is inline copied-case data |

Most of the duplication shows a single pattern. A new algorithm file arrives with its own private
copy of the 2D primitives, spatial grid and polyline normalizer it needs, because the shared version
either lives somewhere awkward (`SurfaceRemesher.ConstraintPolyline`, `GradingGeometry2D` under
`Grading/`) or doesn't exist. Most of the recommendations below give those primitives a home, so
the next file has nothing to copy.

---

## Priority 1: duplication that has already drifted, or that invites a known bug

### 1. Merge the forked face-cutting engine

`MeshAreaTopologySplitter.cs` (1,848 lines) and `MeshConstraintTopologyInserter*.cs` (1,590 + 3
partials) are two copies of one engine. They share **~30 private types and methods** under the
same names: `Point2D`, `SegmentPiece`, `EdgePoint`, `FaceData`, `FaceCutData`, `LocalPointBuilder`,
`GlobalPointLookup`, `SegmentIntersection`, `SnapPointToTriangle`, `SnapPointToEdge`,
`TriangulateTouchedFace`, `IntersectSegments`, `ParameterOnEdge`, `PointOnSegment`,
`ParameterOnSegment`, `AddSplitParameter`, `PackKey`, `ToCell`, `Lerp`, `Cross`, `DistanceSquared`,
and others.

**They have already drifted.** The splitter has `SnapToCorner`, `SnapToEdgeEnd` and
`HasTriangulableArea`, added to stop hair-apart corner points creating non-manifold slivers. The
inserter has none of them, and its `SnapPointToTriangle` takes `FaceData` by value where the
splitter's takes it `in`. Each fix that lands in one engine is a bug the other one keeps.

**Proposal:** extract an internal `FaceCutKernel` (in `Core/Grading/FaceCut/` or similar) that owns
the shared types, snapping, segment/face intersection and local triangulation. The two callers keep
only their policy: the splitter classifies areas, and the inserter handles wall patches and wall
quality.

**How:** do it in two separate commits. (a) Diff each shared pair, list every semantic
difference, and decide for each one whether the kernel takes the newer behaviour or a parameter.
Port the corner-snap fixes into the inserter *with a test* before merging. (b) Do the mechanical
extraction. Both engines sit on the grading and wall-insert hot paths, so both commits need
hosted-perf and the wall/grade copied-case suites (`GradePathRetainingWallRegressionTests`,
`Terrain_1_Retaining_Wall_Rails_CopiedCase`, the 1,152-case wall sweep).

### 2. A mesh value type, so vertex/face counts cannot be mis-paired

CLAUDE.md spends a whole convention ("Mesh counts must come from the same extraction as the mesh
arrays") on a bug class that the type system could rule out. At least **74 signatures in 43 files**
take the loose quad `double[] vertices, int vertexCount, int[] faces, int faceCount`. Multi-line
signatures aren't counted, so the real number is higher.

**Proposal:** add `readonly struct IndexedTriMesh(double[] Vertices, int VertexCount, int[] Faces,
int FaceCount)` to Core, with validation in the constructor plus `Span`/`AsXy`/`Bounds` helpers.
Make `RhinoGeometryConversions.TryExtractMeshData` return one, so the counts can't be separated
from the arrays at the source. Then migrate inward one public entry point at a time: graders,
remeshers, splitter, analyzers. Keep the old overloads as one-line forwarders until no caller
remains.

This also shrinks the overload ladders in #7.

### 3. Move `ConstraintPolyline` out of `SurfaceRemesher`

`SurfaceRemesher.ConstraintPolyline` (`Engine/SurfaceRemesher.cs:14`) is the de facto constraint
type for the whole codebase. **~45 files** in Core, Grasshopper and Rhino spell it
`SurfaceRemesher.ConstraintPolyline`, which means that a grading file, a wall planner or a GH
component depends on a 2,300-line remesher in order to name a polyline.

Polyline normalization is also copied around it. `NormalizePointCount` exists four times
(`SurfaceRemesher.cs:2193`, `ConstraintNetworkNormalizer.cs:170`,
`MeshConstraintTopologyInserter.cs:768`, `ConstraintConflictDiagnostics.cs:163`). The
Polyline→`ConstraintPolyline` conversion exists three times (`RemeshComponent.cs:150`,
`MeshSimplifyComponent.cs:149`, `TerrainBuildService.MeshConstraints.cs:448`), plus a fourth for
`AreaBoundary`.

**Proposal:** add `Core/Engine/ConstraintPolyline.cs` as a top-level type with
`NormalizedPointCount`, closure and bounds members, and put the Rhino conversion in
`MoleHill.Shared` (see #8). This is a mechanical rename. A `global using` alias can bridge the
change if the diff gets too large.

Related: Core has six other polyline shapes (`OutputPolyline`, `PadBoundary`, `LockCurve`,
`AreaBoundary`, Rhino's `PolylineData`, and raw `(double[] polyXy, int count)` pairs). Merging them
is *not* recommended, because they carry different semantics. They should share the one flat-point
representation and its helpers, though.

### 4. One 2D geometry kernel

The same primitives are redefined privately all over the code:

| Primitive | Private copies |
|---|---|
| `Cross` | 11 — `DrainageBasinAnalyzer`, `WaterflowTracer`, `BoundaryClipper`, `GradingGeometry2D`, `GradingWindows`, `MeshAreaTopologySplitter`, `MeshConstraintTopologyInserter`, `SurfaceDeviationEvaluator`, `TinInputCleaner`, Rhino `ConstraintConflictDiagnostics`, `TerrainBuildService.Boundaries` |
| `Lerp` | 8 — `SurfaceRemesher`, `ConstraintNetworkNormalizer`, splitter, inserter, `RegionInputClipper`, `TerrainConstraintPreprocessor`, `TinInputCleaner`, `RetainingWallPlannerCore` (clamps!) |
| `DistanceSquared` | 8 |
| `Orient` | 4 — `TriangleBoundaryCuller`, `FillSlopeFlipper`, `LawsonFlipper`, Rhino `TerrainRuntimeCache` |
| `SegmentsIntersect` / `OnSegment` | 4 / 3 — incl. `PadGrader.Constraints`, `TerrainBuildService.Boundaries`, `TerrainRuntimeCache` |
| `PointOnSegment` / `ParameterOnSegment` | 4 / 4 |
| `PointInTriangle` | 3 |
| `SignedArea` | 3 — `ClipperGeometry`, `ScatterSampler`, `TerrainBuildService.Boundaries` |
| `PointInPolygon` | 3 — `GradingGeometry2D`, the `PadGrader` wrapper, GH `TerrainPartitionGeometry` |
| `ComputeBounds` | 3 |
| `InterpolateZ` | 3 |

Two of these are in the wrong project. A runtime *cache* (`TerrainRuntimeCache.cs:487-509`) carries
its own segment-intersection predicates, and so does a build-service partial
(`TerrainBuildService.Boundaries.cs:179-214`).

**Proposal:** promote `Grading/GradingGeometry2D` to `Core/Geometry/Geometry2D` (it's already the
documented home for these helpers) and add the missing primitives. Then delete the private copies,
replacing *only* the ones whose semantics really match. As review R12 says, write down any
difference before merging: inclusive/exclusive boundaries, tolerance vs exact, `Lerp` clamping, the
partition test's "no boundaries = outside". A copy that differs stays separate, with a comment
saying why. Add a guard test in the style of `PackedEdgeKeyComparerGuardTests` that fails on a new
`private static double Cross(` / `Orient(` / `Lerp(` outside the kernel.

Also, retire the `PadGrader.Spatial.cs` compatibility wrappers. `SpatialHash`, `FaceGrid`,
`DistToPolygon` and `FindNearVertex` now have **zero** callers outside the file. `PointInPolygon`,
`PolygonInteriorPoint` and `InterpolateZ` are left only in tests (21 sites) and inside the
`PadGrader` partials. CLAUDE.md still documents these as public API, so update it in the same
change.

---

## Priority 2: structural

### 5. Consolidate the spatial indexes

Core has six shared indexes (`SpatialHashGrid2D`, `SpatialVertexHash`, `TerrainFaceGrid`,
`TerrainSpatialIndex`, `SegmentProximityIndex`, `CellMembershipIndex`) and **13 private ones**:
`WaterflowTracer.FaceSpatialIndex`, `FeaturePolylineGraph.VertexXYGrid`,
`LocalMeshRefiner.VertexHashGrid`, `CrossFieldSolver.VertexHashGrid`,
`SurfaceRemesher.{SegmentSpatialIndex, NearVertexIndex, PreservedConstraintSegmentIndex}`,
`TriangleBoundaryCuller.ConstraintSpatialIndex`, `GradingWindows.ReachIndex`,
`TerrainDetailInserter.{PointIndex, SegmentIndex}`, `TerrainConstraintPreprocessor.NeighbourIndex`,
`SurfaceDeviationEvaluator.DomainCoverageIndex`, plus the grids inside the splitter and inserter.
`PackKey` alone is defined four times.

That is a correctness risk as well as clutter. The 2026-09-26 cell-key incident (default `long`
hash collapsing `cx`, 1.7 s instead of 0.1 s) happened because each index rolls its own key, and
the guard test only catches the collection type, not a poorly mixed key.

**Proposal:** settle on three primitives (a point grid, a segment grid and a face grid), each with
the comparer built in, and move the private indexes onto them *opportunistically*. Every index sits
on a measured hot path, so a migration needs a hosted-perf result, not just green tests. Don't do
this as one big pass.

### 6. Finish the type registry

`ModifierTypeDescriptor`'s own doc comment promises that "adding a modifier should mean dropping one
descriptor … rather than editing switches". That holds for build dispatch and the parameter schema,
but per-type switches still sit in seven places:

| Where | What it switches on | Belongs on |
|---|---|---|
| `MoleHillPanel.Modifiers.cs:571-627` `GetCollapsedSummary` | 13 modifier types | `ModifierTypeDescriptor.Summarize(def)` |
| `MoleHillPanel.Modifiers.cs:165` `AppendBespokeModifierRows` | 4 types, two of them empty cases | descriptor hook |
| `MoleHillPanel.Annotations.cs:56-380` | 17 annotation types. The result-row blocks are near-identical copy-paste ("Curves / Labels", "Min / Max", "Rebuild required") | descriptor `ResultRows(summary, def)` returning declarative rows |
| `MoleHillPanel.Analysis.cs` | 13 | same |
| `TerrainBuildService.Analysis.cs:245-290` | annotation build dispatch (switch expression) | `AnnotationTypeDescriptor.Build(...)`, mirroring `RunBuildStage` |
| `TerrainUnitScaler.cs:76-290` | 27 types; every new length field must be added by hand | drive it from the schema (`ParameterUnit.ModelLength`) or a `[ModelLength]` attribute, with a guard test that every `double` on a definition is classified |
| `TerrainSerializer.cs:438-656` | post-load normalization per type | `virtual Normalize()` on the definition |

The four descriptor bases have also drifted in vocabulary: modifiers use `DisplayName`/`IconName`,
analyses and annotations use `TypeLabel`/`MenuLabel`/`IconLabel`/`AccentArgb`, and markers use
`AddButtonText`. A generic `ContentTypeDescriptor<TDefinition>` carrying the chrome (kind, CLR type,
labels, icon, accent, sort, `Create`, `Parameters`, `Summarize`) fits the analysis/annotation rule
in CLAUDE.md. That rule already shares `ParameterDescriptor<T>` across families and relies on the
type parameter to keep them apart, so a shared chrome base keeps the families separate.

Lock it in with a guard test: no `case XxxDefinition` / `is XxxDefinition` outside `Registry/` and
`Model/`, with a short reasoned exemption list.

### 7. Request/result objects for the graders

`PadGrader` has **7** `Grade*` overloads and `PathGrader` has **10**. Each one adds an `out`
parameter or a defaulted trailing argument (`PadGrader.cs:19-79`: `out errorMessage`, then
`out failureOutputPolylines`, then `out failureStructuredDiagnostics`, then `hardConstraints`).
`PathGrader.ApplyZ.cs` repeats the pattern with 5 `ApplyGradingZ` overloads, 2
`ValidateApplyGradingZInputs` and 4 `TryFindClosestPathLocation`.

**Proposal:** `GradePadRequest` / `GradePathRequest` (mesh, from #2, plus features, constraints,
tolerances and options) and one `GradeOutcome` (result or failure, with polylines and structured
diagnostics). Keep one public `Grade(request)` per grader. The GH components and
`TerrainBuildService.Grading` are the only external callers.

### 8. One RhinoCommon conversion layer for both hosts

The Rhino and GH hosts each convert between Rhino meshes and flat arrays, and the two hosts
**behave differently**:

- Rhino: `RhinoGeometryConversions` (505 lines) normalizes a copy (quads→tris, combine, cull,
  degenerate removal) and returns consistent counts.
- GH: `GhSolveContext.ToFlatVertices`/`TryToFlatFaces` **reject** quad meshes with an error.
  `TerrainPartitionGeometry.TryExtractTriangleMesh` converts quads in place.
  `InSituStairComponent.TryExtractMesh` and `RetainingWallComponent.TryExtractTriangleMesh` are
  further private copies. `RhinoConverter.ToRhinoMesh` duplicates
  `RhinoGeometryConversions.ToRhinoMesh`.

So a quad mesh works in the panel and fails in Grasshopper. `RhinoGeometryConversions` only depends
on RhinoCommon.

**Proposal:** move it, the Polyline→`ConstraintPolyline` conversion (#3) and the GH
`RhinoConverter` into `MoleHill.Shared`, and delete the GH copies. The mesh-normal rules in
CLAUDE.md then hold in GH as well.

Smaller Rhino-side copies to fold in at the same time: `GetLayerPath` ×3
(`CommandScriptRunner`, `TerrainBuildSnapshotBuilder`, `TerrainController.Events`),
`DeleteObjects` ×3 (`LandXmlSurfaceService`, `TerrainInputCommandService`, and
`TerrainController.Output`, which also un-hides objects first, so keep that one distinct),
`DisposeCurves` ×3, and `NearestSampleIndex` ×3 (`CurveReviewAnalysis`, `CurveReviewConduit`,
`CurveProfileControl`). The panel also has four one-line forwarders to `AnalysisFormatting`
(`ParseSlopeUnit`, `GetSlopeUnitKey`, `GetLeafLayerName`, `ResolveLayerColorArgb`). Call the target
directly, and move the layer helpers out of a class named *Analysis*Formatting.

### 9. Name the remeshers by role, and pick one for GH Remesh

| Engine | Lines | Used by |
|---|---:|---|
| `SurfaceRemesher` | 2,307 | Rhino constraint insertion (`TerrainBuildService.MeshConstraints`), `MeshAreaSplitter`, **GH Remesh**, GH Retaining Wall |
| `IsotropicRemesher` / `TiledIsotropicRemesher` | 1,781 / 946 | **Rhino Remesh modifier**, Retopo |
| `LocalMeshRefiner` | — | Sculpt DynTopo only |
| `SurfaceSimplifier` | 733 | Simplify (both hosts) |

The Remesh card and the GH Remesh component run **different algorithms**, which the B7 parity
matrix doesn't show. `SurfaceRemesher` has effectively become the "constrained re-triangulation /
constraint insertion" engine, and three other engines borrow its `DetectCreaseEdges`
(`FeaturePolylineGraph`, `LocalMeshRefiner`, `CrossFieldSolver`).

**Proposal:** move `DetectCreaseEdges` to a small `MeshFeatureDetection` class. Give
`SurfaceRemesher` a name that matches its job, or at least say what that job is in its header and
the Engine README. Then decide on purpose whether GH Remesh should call the isotropic engine, and
record the decision in `gh-modifier-parity-matrix.md`.

---

## Priority 3: organization

### 10. Give `MoleHill.Rhino/Services` sub-folders

There are 170 files in one folder, covering the build pipeline, the controller, display conduits,
output/layers, persistence and migrations, five importers, the curve inspector, the sculpt session
and toolbar install. A suggested split (folders only; **keep the namespace** for now so the move is
a pure rename with an empty semantic diff):

```
Services/
  Build/        TerrainBuildService.*, TerrainBuildSnapshot*, TerrainStageKey, fingerprints, policies
  Controller/   TerrainController.*, debounce/supersede/interim-publish policies, undo
  Display/      TerrainDisplayConduit, preview/render mesh, RuntimeOverlay, ScatterBlockPreview
  Output/       LayerRole*, LayerTemplate*, AnnotationStyle, Hatch, GeneratedRhinoObject, blocks
  Annotation/   TerrainAnalysisAnnotationBuilder (split, see #12), legend, report table, section slicer
  Persistence/  TerrainSerializer, document store/identity, JSON resolver, migrations (#13)
  Import/       GeoTIFF*, ClassicTiffTagReader, RasterGeoreference, LandXml, Survey*, Georeference*
  CurveReview/  CurveReview* (9 files)
  Sculpt/       Sculpt* (8 files)
  Commands/     *CommandService, *CommandAlgorithms, CommandScriptRunner, CommandOptionCache
```

Regenerate `docs/file-index.md` in the same commit.

### 11. Break up `TerrainBuildService` by stage

This is one class split across **25 partials** (~11,000 lines). The partials named after a modifier
(`GradeLine`, `ProjectTo`, `Simplify`, `Sculpt`, `RetainingWalls`, `RetainingWallGrading`,
`Scatter`, `Waterflow`, `Drainage`, `GradientCompliance`, `Zones`) are stage executors that happen
to share `this`. Review R07 already asks for gradual separation. A concrete first step is to turn
the *smallest* stage partials (`GradientCompliance` 71, `Sculpt` 71, `Report` 74, `Waterflow` 97,
`ProjectTo` 124) into `static` classes that take `ModifierBuildContext`, and to call them from the
descriptor's `RunBuildStage`. That shows which shared state each stage really needs before the big
ones (`Tin` 1,706, `Grading` 1,567, `Analysis` 1,325) are tackled.

### 12. Split `TerrainAnalysisAnnotationBuilder` (1,725 lines)

It mixes three families: point/curve labels and callouts (lines 20-450), the section family
(452-1,290: terrain, cross-station and longitudinal sections, cut/fill reference slices, hatches)
and shared emit helpers (1,290-1,700). Splitting it into `AnnotationLabelBuilder`,
`SectionAnnotationBuilder` and `AnnotationEmit` falls out naturally once #6 gives annotations a
descriptor build hook.

### 13. Make schema migrations explicit

Legacy and version branches are spread inline: 40 sites in `TerrainSerializer`, 28 in
`LayerRoutingMigration`, 33 in `ProjectBaseCPlaneService`, and 7 each in `TerrainDefinition` and
`TerrainTolerancePolicy`. **Proposal:** an ordered list of `TerrainSchemaMigration(fromVersion,
Apply)` steps, run once after deserialization. Load-time normalization (#6) stays separate from
migration, and each migration gets one test fixture document. This is also where a retirement
policy goes, if one is ever wanted (for example, documents from before 1.0 load through a
one-time upgrade path).

### 14. Move pure logic out of the Rhino host

Several services touch the Rhino API barely or not at all, so they can only be tested through the
Rhino test project. Candidates (check each one; some use `RhinoDoc` for one lookup that can become
a parameter):

- the TIFF/GeoTIFF readers (`ClassicTiffTagReader`, `GeoTiffMetadataReader`,
  `GeoTiffLinearUnitReader`, `GeoTiffElevationReader`, `RasterGeoreference`) → `Core/IO/`
- `SectionProfileComparison`, `TerrainReportBuilder`, `CurveReviewRules`, `CurveReviewPalette`
- `GeometryCommandAlgorithms` (1,173) and `TerrainInputCommandAlgorithms` (741) use `Point3d`
  throughout. Keep them in the host, but move them under `Commands/` (#10) so "Algorithms" stops
  meaning two different things.

---

## Priority 4: tests and docs

### 15. Shared test fixtures

Grid/plane mesh builders are redefined about 30 times across test files (`BuildGrid` ×9,
`BuildGridVertices` ×4, `BuildGridFaces` ×4, `BuildJitteredGrid` ×2, and a long tail of
`CreateSloped*`/`CreateFlat*`). Add a `TestMeshes` helper per test project (or one linked file),
and switch to the `IndexedTriMesh` from #2 once it exists.

Copied-case tests are ~250k of the 340k test lines (`GradePathDistantHardConstraintCopiedCaseTests`
alone is 79,818 lines). Review R11 (move inline arrays to embedded data files) would cut compile
time and IDE load without weakening the tests. It's worth doing before the next large regression
gets copied in.

### 16. Trim `architecture.md` back to architecture

The "map to read first" is 2,537 lines. About 950 of them (§ "Rhino: edit-to-visible latency"
through "What is left on a small terrain", roughly lines 1,506-2,450) are dated performance
narratives ("2026-09-29", "40 → 27 ms"). The doc-lifecycle rule in `cleanup-plan.md` says to fold
in what a reader still needs and delete the rest. Keep the invariants (normalization once, publish
geometry before outputs, superseded-build handling) as short rules, and leave the measurements to
git history and `tests/perf-baselines/`.

### 17. Housekeeping

- Stray editor temp files are in the tree (git-ignored, but they show up in `ls` and `Glob`):
  `Core/Grading/PathGrader.Explicit.cs.tmp.*`,
  `Grasshopper/Registry/RegistryTerrainComponent.cs.tmp.*` ×2,
  `Rhino/Services/SurveyLayerNaming.cs.tmp.*`.
- `MoleHill.Shared/RetainingWallPlannerCore.cs` (2,137 lines) carries its own `CellKeyComparer`,
  `Lerp` and `Distance2D`. Because Shared is RhinoCommon-dependent source, it can't use Core's
  internals. Once #4 lands, check whether the RhinoCommon-free parts of the planner belong in Core.

---

## Suggested order

Each step is independently shippable. Mechanical moves go first, because they make every later diff
smaller.

| # | Step | Kind | Gate |
|---|---|---|---|
| 1 | #3 `ConstraintPolyline` top-level, #17 housekeeping | mechanical | managed |
| 2 | #10 Services sub-folders (namespaces unchanged) | mechanical | build + file-index |
| 3 | #4 geometry kernel + guard test, retire `PadGrader.Spatial` wrappers | semantic per copy | managed + hosted-perf |
| 4 | #8 shared conversion layer; quad-mesh parity in GH | behaviour change in GH | managed + GH live check |
| 5 | #1 face-cutting kernel (fix port first, then extraction) | semantic | managed + wall/grade copied cases + hosted-perf |
| 6 | #2 `IndexedTriMesh`, then #7 grader request objects | API | managed |
| 7 | #6 registry completion + guard test, then #12 | structural | managed + live panel check |
| 8 | #11 stage executors, smallest first | structural | managed + hosted-perf |
| 9 | #5 spatial indexes, one per change | perf-sensitive | hosted-perf each |
| 10 | #9, #13, #14, #15, #16 | as convenient | — |

**Out of scope:** `src/TriangleNet/**`, plus any behavioural change to grading tiers, wall insertion
or remesh quality that isn't required to remove a duplicate. Where a duplicate turns out to differ
on purpose, keep it and add a one-line comment saying why. That outcome is still progress.
