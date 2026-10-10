# Triangulate

**Pinned base card.** Triangulate builds the terrain mesh from your survey data. It always sits at the
bottom of the modifier stack and can't be deleted or moved. Everything else works on its result.

It builds a *constrained Delaunay triangulation*: a triangle mesh whose edges honour your breaklines.

## What goes in

Fill in whichever sources you have. Heights come from the geometry.

| Setting | Use it for |
|---|---|
| **Exact TIN Mesh** | An existing triangle mesh. When set, its topology is preserved exactly and the point and curve inputs are ignored |
| **DEM Surface** | A planar surface carrying a numeric GeoTIFF image (create one with `mhImportGeoTiff`). Move the surface to control where the DEM sits in your project |
| **Points** | Points that become mesh vertices at their own height |
| **Breaklines** | Curves whose edges the mesh keeps. Each vertex sets the height along the edge |
| **Contours** | Contour curves. Their vertices set terrain height |

Source fields take Rhino objects (**Sel**) or layers (**Layers**), both replacing what's there. Layer
sources are watched.

## Trimming the mesh

Four closed-curve sources shape the outline. They work in plan (XY).

| Setting | Effect |
|---|---|
| **Outer** | Trims the finished terrain to the largest valid region these curves enclose |
| **Hide** | Removes the interior of these curves from the finished terrain |
| **Show** | Restores regions removed by Hide, without extending beyond Outer |
| **Data Clip** | Clips points, contours and breaklines **before** triangulation, so only data inside is used |

Use **Outer** to cut the mesh to a site boundary, **Hide** to cut holes (a building footprint, a pond),
**Show** to put back part of a hole, and **Data Clip** to ignore survey outside the area of interest. This
is faster than triangulating everything.

## Contour Mode

Controls how contour curves are used:

| Mode | Behaviour |
|---|---|
| **Auto (fast for large sets)** | Keeps contour edges when there are fewer than the automatic threshold of source vertices; beyond that, dense contour stations are treated as ordinary samples for speed |
| **Constrained (exact)** | Keeps every contour segment as an edge |
| **Vertices only (fastest)** | Samples contour vertices without keeping their edges |

Breaklines are kept as edges in every mode.

## Peel Border

Survey data often leaves long, thin triangles along the outer edge, spanning gaps in the data. **Peel
Border** removes unwanted triangles from the mesh **border only**. Interior faces are never candidates.

| Setting | Meaning |
|---|---|
| **Enabled** | Turn peeling on |
| **Max Edge** | Edge length above which a border triangle may be peeled. `0` chooses a threshold from the mesh's own edge lengths |
| **Max Angle** | Border triangles with an edge longer than Max Edge and an interior angle at or above this value are peeled (up to 180°) |
| **Slope Limit** | Border triangles at or above this slope are peeled. `0` turns slope-based peeling off |

## Tips

- Use breaklines for anything with a hard edge (kerbs, ridges, walls, stream banks). Mesh edges will follow
  them exactly.
- Breakline vertices are only merged when they are close in height as well as in plan, so parallel
  breaklines at different elevations (such as the rails of a retaining wall) stay separate.
- Duplicate points are merged. Clean noisy input with `mhValidateTerrainInputs` first.
- The collapsed summary shows how many sources of each kind are in use.

## Related

[Add Geometry](add-geometry.md) · [Remesh](remesh.md) · [Simplify](simplify.md) ·
[Commands: survey and terrain import](../04-commands.md#survey-and-terrain-import)
