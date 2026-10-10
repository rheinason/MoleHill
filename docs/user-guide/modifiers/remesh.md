# Remesh

Rebuilds the triangles so they are **even and well shaped**, while keeping every vertex on the surface and
keeping your breaklines and creases. Good meshes make Smooth, Sculpt and grading behave better, and make
contours cleaner.

## Mode

| Mode | Best for |
|---|---|
| **Isotropic (best quality)** | Regularises the whole terrain to even triangles. Best overall quality, but can be slow on very large terrains and may occasionally cross retaining walls at shallow wall angles |
| **Full Rebuild (classic, wall-safe)** | Constrained-Delaunay re-triangulation. Never crosses a wall or breakline; triangles are coarser in shape |
| **Local Refine (preserve topology)** | Only splits and flips triangles in place, preserving existing topology exactly. Fastest and safest on huge terrains, coarsest quality |

## Settings

| Setting | Meaning |
|---|---|
| **Breaklines** | Curves whose edges the remesh keeps. Creases detected from the mesh are kept as well |
| **Edge Length** | Target triangle edge length. Smaller is denser, larger is coarser. `0` preserves the mesh's approximate face density. Previews run at twice this length |
| **Crease Angle** | Keeps edges where the mesh folds by at least this many degrees (batter toes, slope breaks). Vertices slide along them and no edge flips across. About 20–35° catches batter toes; `0` turns it off |
| **Min Angle** | *Full Rebuild only.* Minimum triangle angle. About 20–30° gives well-shaped triangles; above about 34° refinement may not finish. `0` means no limit |
| **Max Area** | *Full Rebuild only.* Maximum triangle area, independent of Edge Length. `0` means no limit |

Creases are detected from the mesh on every build and are never saved as breaklines.

## Tips

- Start with Isotropic and a Crease Angle around 25°. If it crosses a retaining wall, switch to Full Rebuild.
- Remesh works on the mesh it receives. Place it below Smooth, Sculpt and grading cards to give them
  evenly sized triangles to work with, or above them to tidy the final result.
- For a quick regular mesh on a huge terrain, use Local Refine.

## Related

[Retopo](retopo.md) · [Simplify](simplify.md) · [Smooth](smooth.md)
