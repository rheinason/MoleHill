# Scan to terrain: plan (2026-10-18)

**Status:** planned, not started. Backlog entry **B26**. Supersedes nothing; builds on B25 (`mhTerrainToCurves`).

## The problem

A drone photogrammetry mesh of a site (houses, roads, gardens, walls on a hillside) is the most common
starting point, and today it is a dead end. The user's manual routine is to draw the roads and kerbs from
Top view and drape them, guess the kerbs "a little by eye", then add contours where the green areas are.
`mhTerrainToCurves` refuses such a mesh outright: its border is not a set of simple loops, because the
houses are in it.

The goal is one command that takes the scan and gives back, on layers:

1. **Ground**: a clean 2.5D ground surface with the houses, cars and bushes removed.
2. **Buildings**: simplified closed volumes with the right roof shapes (flat, shed, gable, hip and
   combinations), standing on the ground.
3. **Walls and terrace edges**: the steps in the ground, as top and toe breaklines.
4. **Kerbs and road edges**: suggested breaklines along the paved areas, for the user to keep or delete.
5. **Paved and soft areas**: closed boundaries from the photo colours.
6. **Contours and spot heights**: the fewest that rebuild the soft areas within a tolerance (B25),
   with the breaklines above as constraints, so contours go where the green is.

All of it is editable Rhino geometry, ready to become the sources of a MoleHill terrain.

## What the reference scan shows (measured)

Reference: `Grønalid21 - simp.obj` (Tórshavn), with its diffuse atlas
`3D_MODEL_Gronalid_21_Torshavn_u0_v0_diffuse.jpg`. Not in the repository (size, and the data is not ours);
the measurements below come from `tools/scan-explore.py` (numpy and scipy, about 3 s for the whole
mesh), which prints them and writes the images described.

| Measure | Value |
|---|---|
| Mesh | 383,128 vertices, 766,572 triangles, one object, Z up |
| Extent | about 225 x 261 m plan, 66 m relief |
| Density | about 13 triangles per m² overall; about 7 vertices per m² on flat ground ("simp") |
| Faces | 64% face up, 33% are walls (steeper than 60°), 3% face down |
| Ground slope | median 8°, 90th percentile 20° |
| Texture | one 8192 x 8192 "lightbox" atlas; every triangle has UVs |

**The atlas is useful.** Sampling it at each triangle's UVs and laying the colours out in plan gives a
proper orthophoto: grey roads, green lawns, coloured roofs. The atlas layout does not matter; the UVs carry
the mapping. Colour is the strongest signal for paved against soft ground and helps reject trees.

**Buildings separate cleanly.** A first-cut progressive morphological ground filter (0.5 m cells, windows
of 1 to 24 m, slope allowance 0.6) finds 235 raised components, 99 of them at least 20 m² (11,070 m² in all,
median 6.8 m tall, middle sizes 27/108/192 m²), and they land on the houses in the orthophoto. Roof
facets cluster by aspect: the eight largest show 2 dominant facets (gables), 4 to 6 (hips, cross gables,
L-plans) and one 29%-flat roof. So parametric roof fitting is realistic for most of the stock.

**The ground model needs real work.** With removed houses filled by nearest ground and smoothed, the
first-cut ground is lumpy where houses stood. That needs a better filter and a proper fill (below).

**Walls and terrace edges are clear; kerbs mostly are not.** Two road cross-sections at full mesh
resolution: a 3 m step 4.5 m off one road edge and a 1.5 to 2 m wall beside another read unmistakably. A
0.15 to 0.2 m kerb shows on one side of one road; the other side of the same road rises 0.5 m over 4 m
with no step at all. At 7 vertices per m², 10 to 15 cm kerbs are at the resolution limit of this
mesh. Kerbs therefore come from the **colour boundary** between paved and other ground, confirmed by the
geometry where it shows, and are offered as suggestions, which is how the user places them today.

## Pipeline

All analysis in Core (a new `MoleHill.Core.Scan` namespace: pure, unit-free, testable on synthetic
scenes); reading the Rhino mesh, its material texture and writing layers in `MoleHill.Rhino`.

### S0. Read the scan

Host: take the picked mesh and, when its material has a diffuse bitmap, sample it at every face's texture
coordinates (Rhino keeps an OBJ's UVs and material). Hand Core flat arrays: XYZ vertices, faces, and an
optional RGB per face. Works without colour, with fewer results (no paved/soft split, kerbs from geometry
only).

### S1. Rasters

Rasterise the triangles (not the vertices; the vertex-only first cut left 40% of 0.5 m cells empty) into a
top-surface model (DSM) at 0.25 m, with per-cell colour of the top face, its normal, and the lowest
surface. Cell size from the mesh density, user-overridable.

### S2. Ground

Classify each cell ground or not, then rebuild the ground under what is not.

- **Filter.** Cloth simulation (an inverted cloth dropped onto the inverted DSM, a well-tested method
  robust on slopes) or the progressive morphological filter with a slope-adaptive threshold. Pick by
  measurement on the reference scan and synthetic slopes. Colour helps: roof colours and dark greens on
  tall objects are not ground.
- **Fill.** Harmonic (Laplace) interpolation across removed regions, from the ground around them, not
  nearest-neighbour, so a removed house leaves a smooth patch, not a lump.
- **Output.** A ground mesh (regular grid, or a TIN of ground points), on a `Ground` layer, usable directly
  as a terrain source. Report the share of the site that was filled.

### S3. Buildings

Per raised component that passes planarity and size checks (trees and bushes fail planarity and are
usually green):

1. **Footprint.** The component's outline, regularised: dominant orientation from the roof edges and
   walls, edges snapped to that orientation and its perpendicular where within an angle, short jogs
   removed. Most of this stock is rectangular or L-shaped.
2. **Roof planes.** Region growing on the mesh's up-facing faces inside the footprint (normals within a
   few degrees, plane distance within a tolerance), merging small regions.
3. **Roof model, in tiers.** LOD1: the footprint extruded from its ground height to the median roof
   height (always possible). LOD2 parametric: fit flat, shed, gable, hip, half-hip and cross-gable models
   to each rectangle of the footprint by least squares against the roof planes, choosing by error and
   facet count. LOD2 general (later): intersect the fitted planes over the footprint for roofs no template
   fits.
4. **Solid.** A closed Brep from ground (lowest ground along the footprint) to the roof, one per building,
   with eave, ridge and ground heights in user text.

Output on a `Buildings` layer. The buildings are context, not terrain inputs: they show the scheme in
place, and their footprints can later become pads or holes in a MoleHill terrain (a separate decision).

### S4. Walls and terrace edges

On the ground model: steps much larger than the local slope explains (the exploration's "unexplained
step" measure) chained along their length, kept when long and straight enough to be built (bushes give
short, blotchy responses), and each emitted as a pair of lines, top and toe, at the ground's heights,
simplified with the route curve fitter (few control points). Layer `Walls and edges`. These are hard
breaklines in MoleHill's sense.

### S5. Paved and soft areas

From the per-cell colour (excess green for vegetation; low saturation for asphalt and concrete) cleaned
with the geometry (paved areas are smooth and gently cross-fallen; lawns are rougher). Shadows are the
known trap (dark asphalt against dark green in shade): use saturation and the local geometry, not
brightness alone. Output closed boundaries on `Paved areas` and `Soft areas`, simplified to few control
points.

### S6. Kerbs and road edges

Candidates: the boundaries of paved areas, offset to where a step or crease in the ground lies within
about a metre (when the scan resolves one), straightened along the road's direction. Each suggestion
carries its evidence (colour edge only, or colour and step, with the step height) in user text, so the user
can trust or discard it. Layer `Kerbs (suggested)`. A review mode in the same panel can come later: click to
keep or drop.

### S7. Contours and spots

B25's recovery, restricted to the soft areas, with the walls, kerbs and paved boundaries as fixed
breaklines, so contours go where the green is and the rest is described by edges. Contours and spot
heights on their own layers. Contours fitted as light curves (the route fitter), not polylines.

## Command and panel

`mhScanToTerrain`, following the user's stated preference (2026-10-17): every setting on one floating
panel, the command line only takes Enter and Esc. The panel has one row per output (toggle, preview, its
main setting): ground cell size and filter strength; building minimum size and roof detail (LOD1 / LOD2);
wall minimum height; colour thresholds with a live preview of the paved/soft split; contour tolerance.
Results preview in the viewport as they are computed, stage by stage; Keep writes the layers under
`Scan {source}::...` (a fresh root per run, as Terrain to Curves now does).

## Phasing

| Phase | Delivers | Main risk |
|---|---|---|
| 1 | S0, S1, S2: clean ground mesh from a scan, with and without colour | the filter on steep, terraced ground |
| 2 | S3 LOD1 + footprints | trees passing as buildings; L-plans |
| 3 | S3 LOD2 parametric roofs | roof plane segmentation on a smoothed mesh |
| 4 | S4 walls and edges | chaining noisy steps into clean lines |
| 5 | S5 + S6 paved areas and suggested kerbs | shadows in the photo; kerbs below resolution |
| 6 | S7 contours in soft areas + the full panel | none new (B25 exists) |

Each phase is useful on its own: phase 1 alone replaces "trace the ground by hand", phase 4 replaces most
of the draped breaklines.

## Validation

- **Synthetic scenes in Core tests**: a hillside with gable and hip houses (known footprints, ridges,
  eaves), a road with 0.15 m kerbs and a 1.5 m terrace wall, generated as meshes with and without colour,
  at the reference scan's density. Assert footprint IoU, ridge height and orientation, wall line
  position, ground error under removed houses.
- **The reference scan**: measured figures recorded in this document as each phase lands (buildings
  found, ground filled, walls traced, time), and a live Rhino run.
- **Speed**: the reference scan should complete each stage in seconds; the exploration's numpy version
  took 3 s end to end at 0.5 m.

## Open questions for the user

1. **Buildings**: LOD1 boxes first, or straight to roof shapes? Closed Breps on a layer, or also blocks?
   Should footprints feed the terrain (as pads, or as holes the terrain is cut around)?
2. **Kerbs**: suggested lines to keep or delete afterwards, or a review step in the panel?
3. **Ground output**: a mesh, curves (contours and breaklines), or both by default?
4. **Other scans**: is this mesh typical (density, one textured object, Z up), or are some from other
   tools (Pix4D, Metashape, DJI Terra, RealityCapture) as several tiles or point clouds?
