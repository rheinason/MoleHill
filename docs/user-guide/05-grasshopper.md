# Grasshopper components

The optional `MoleHill.gha` adds MoleHill components to Grasshopper for procedural workflows. They work
without the Rhino panel, and can also read the terrain you've built in it.

## Terrain exchange

| Component | What it does |
|---|---|
| **MoleHill Terrain Snapshot** | Reads the latest completed *final* terrain, its breaklines and zones from the Rhino panel. Previews and deferred builds are rejected, so downstream geometry never silently changes from approximate to final. Its `Status` output tells you if it is holding the last completed result |
| **Construct Terrain** | Packages an ordinary mesh, breaklines and zones as MoleHill Terrain data |
| **Deconstruct Terrain** | Exposes MoleHill Terrain as a plain mesh, curves, zone branches and metadata |
| **Partition Terrain** | Inserts zone boundaries once and outputs separate terrain meshes with exact shared seam vertices |
| **Prepare Toposolid** | Validates profiles and outputs a point-budgeted, error-measured terrain package for Revit |

MoleHill Terrain is deliberately *not* a closed editing system. Deconstruct it, edit the ordinary geometry
with any Grasshopper tools, then construct it again. A snapshot component stays bound to its document and
terrain even if you change the panel selection.

## Surface

| Component | What it does |
|---|---|
| **TIN Surface** | A TIN mesh from points and breaklines |
| **Remesh** | Refines a TIN with quality constraints and a maximum edge length. Mode and crease angle are exposed |
| **Mesh Smooth** | Laplacian smoothing within boundary curves |
| **Add Geometry** | Adds points and breaklines to an existing MoleHill mesh |
| **Mesh Simplify** | Reduces a terrain by deviation or target vertex count while keeping mandatory edges |
| **Retopo** | Generates a field-guided, quad-dominant mesh. It is a terminal output and does not produce typed Terrain |
| **Project To** | Blends a terrain vertically toward a target mesh, with optional boundaries and feathering |

## Grading

| Component | What it does |
|---|---|
| **Grade Pad** | Flatten terrain at pad elevations with slope transitions and cut/fill reporting |
| **Balance Grade Pad** | Searches a bounded pad elevation range for a target cut-minus-fill volume, and returns the chosen boundary, the graded mesh, all samples and a convergence status |
| **Grade Path** | Road or path grading from centerline elevations, with a constant fallback width or matched variable-width edges |
| **Grade Line** | Grades terrain away from design lines; curve Z is the finished elevation |
| **Retaining Wall** | Pair open rails, generate wall solids and grade between toe and top rails |
| **In Situ Stair** | Stair geometry and terrain transitions from stair references |

## Analysis and layout

| Component | What it does |
|---|---|
| **Slope Analysis** | Per-face slope colouring with a legend range |
| **Waterflow from Points** | Downhill paths from point sources |
| **Terrain Sections** | Overlay terrain profiles and shade proposed-versus-reference cut/fill |
| **Mesh Areas** | Split a mesh by closed boundary curves |
| **Mesh Collage** | Combine meshes in 2D planning mode or as coloured 3D terrain |

## Rhino.Inside.Revit

Inside Rhino.Inside.Revit (Revit 2025 or later) a **MoleHill > Revit** tab adds **Write Toposolids**
(create or update Toposolids and subdivisions from a Prepare Toposolid package) and **Inspect Toposolids**.
They stay hidden in plain Rhino. The Revit step has not yet been verified in a Revit host.
