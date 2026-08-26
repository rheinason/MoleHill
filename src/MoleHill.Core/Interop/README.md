# Interop

Pure, host-independent exchange algorithms and formats.

- `TinSurfaceData.cs` and `LandXmlCodec.cs` provide unit-aware LandXML TIN import/export data and XML
  encoding. Coordinate-system reprojection remains outside Core.
- `ToposolidPointReducer.cs` performs deterministic, adaptive Toposolid elevation-point reduction. It
  preserves the caller's boundary/breakline-critical samples, seeds the approximation with spatial cell
  elevation extrema, triangulates in Core, measures source-vertex vertical error, and iteratively inserts
  the largest errors up to the requested point budget.

Revit API calls do not belong here. The Grasshopper host validates/converts Rhino curves and meshes around
this pure reducer; optional Python adapters under `examples/RhinoInside.Revit/` own the small Revit
transaction boundary.
