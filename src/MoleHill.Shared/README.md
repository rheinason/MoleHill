# MoleHill.Shared

RhinoCommon-dependent source that both hosts need. `MoleHill.Core` cannot hold it, because Core has no
Rhino dependency, and duplicating it per host is how the copies drifted apart. See
`docs/architecture.md` for the project map and `docs/file-index.md` for every file.

**It is a source folder, not an assembly.** `MoleHill.Rhino`, `MoleHill.Grasshopper` and both host test
projects import `MoleHill.Shared.props`, which compiles every `*.cs` here into each of them. A new file
therefore needs no project edits, and a file here must compile in all four.

Key files:
- `MeshNormalOrientation.cs` - the only place an output mesh's winding is unified and its normals
  computed. Its `...Welded` variant skips `UnifyNormals` when no directed edge repeats, which is only
  sound on a welded mesh. `MeshNormalOrientationGuardTests`
  fails on any other `UnifyNormals` call.
- `SplitResultMeshBuilder.cs` - a run of `MeshAreaSplitter.SplitResult` faces as a Rhino mesh, with one
  vertex remap reused across areas. Grasshopper takes the oriented result; the Rhino host normalizes the
  unfinished one.
- `RhinoGeometryConversions.cs` - the one conversion layer between Rhino meshes/polylines and Core's flat
  arrays, used by both hosts: extraction from a **normalized copy** with matching counts
  (`TryExtractMesh` -> `IndexedTriMesh`, `TryExtractMeshData`; quads become triangles, identical vertices
  combine, unused vertices and degenerate faces are culled), mesh construction (`BuildMesh`,
  `ToRhinoMesh`) and `ToConstraintPolyline`. Do not add a per-host extraction; it is what let a quad mesh
  work in the panel and fail in Grasshopper. It compiles with unsafe blocks, so every importing project
  sets `AllowUnsafeBlocks`.
- `ModelUnitContext.cs` - the single model-unit boundary for both hosts.
- `RetainingWallPlannerCore.cs`, `RetainingWallGradePlanner.cs`, `RetainingWallBrepBuilder.cs` -
  retaining-wall planning and solids. The solid builder calls Core's `WallRailStationing` to match
  plan corners before lofting, keeping joined rails usable even with different longitudinal grades.
- `InSituStairReferenceBuilder.cs`, `AdaptivePolylineBuilder.cs` - stair references and adaptive
  polyline sampling.
