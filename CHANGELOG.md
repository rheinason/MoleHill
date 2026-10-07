# Changelog

Released through Rhino's Package Manager (Yak) as `MoleHill`. Versions before 1.0.0 were
beta prereleases. Earlier versions are recorded only in the git history.

## 1.3.3-beta — 2026-10-07

A beta that stops the viewport going blank after long editing sessions and stops round-cornered
retaining walls folding at their corners.

**Viewport**
- The perspective viewport no longer goes empty while a terrain is shown. Every rebuild used to grow
  the terrain's reported extents a little, so after a long editing session Rhino's clipping planes were
  fitted to an area thousands of times the site and nothing was drawn. Reset Build was the only cure.

**Retaining walls**
- Walls whose rails have round, chamfered or filleted corners (for example one rail offset from the
  other with round corners) no longer fold where they turn: each bend is matched as a whole, whatever
  it is made of. Points closer together than the tolerance no longer leave hairline slivers in the solid.

**Grasshopper**
- Remesh has Mode and Crease Angle inputs and can run the same isotropic remesh as the Remesh card.
  Existing definitions keep their output.
- Grasshopper terrain components accept quad meshes and combine duplicate vertices before grading, as
  the Rhino panel does.

**Units and speed**
- Ponding's Ignore Below and the pond and catchment results follow a change of model units.
- Hide and Show boundaries trim large contour sets about three times faster.

## 1.3.2-beta — 2026-10-06

A beta that stops terrains tearing where new data, zones or boundaries cross thin triangles, and makes
Hide boundaries work on large surveys.

**Terrain robustness**
- Add Geometry no longer crashes or folds the terrain when its lines cross bands of thin contour
  triangles. Each crossing is placed once on the edge both neighbouring triangles share, and a result
  that would overlap is retried in one piece or rebuilt instead.
- Zones no longer come out with overlapping seams or pinhole slits on surveys with thin triangles, and
  their area matches the terrain exactly. A damaged split is now reported in the build messages.
- Hide (and Show/Outer) boundaries no longer silently do nothing: triangles just outside the curve are
  kept, so the trimmed edge stays closed and the trim is accepted.
- Smooth no longer opens a hairline slit where Remesh left a flat sliver triangle.
- Triangulate repairs crossing and duplicate breaklines and contours before triangulating (without
  removing deliberate breakline stations), and reports places where two sources cross at different
  heights — usually two overlapping contour sets.
- Lines that run past the terrain edge are stored as inserted, so later rebuilds keep them crisp.
- The "far from the world origin" warning measures against the tolerance the terrain actually works at,
  so ordinary sites a few kilometres from the origin no longer warn.

**Grading and walls**
- Grade Path and Grade Pad batters no longer form thin near-vertical spikes where the daylight line
  turns tightly across a terrain crease.
- Retaining-wall solids from joined rails with different grades line up their corners instead of
  folding; stepped wall tops keep their steps.

**Annotations and comparisons**
- New **Legend** annotation keyed to whatever analysis colours the terrain; annotation styles are
  visible and editable on the Annotations tab.
- Cut/Fill, Earthworks and section cards say when the compared terrain is unbuilt or out of date, and
  can rebuild it in place.

**Performance**
- Grading allocates less and no longer stalls on garbage collection during large builds.

## 1.3.1-beta — 2026-10-06

A beta that makes sculpting responsive on large terrains and reduces rebuild and display work.

**Sculpting**
- The brush follows the cursor on large terrains without rebuilding the mesh's search tree after
  every dab. Surface normals update around the brush as the ground changes.
- Zone surfaces and contour annotations follow the brush during a stroke, instead of waiting for
  the rebuild after the stroke ends.

**Rebuilds and display**
- Triangulate cleans up survey points and breakline inputs with less repeated work; Remesh builds
  its projection grid more efficiently.
- Terrain linework draws once per frame, avoiding repeated curve draws in additional display passes.

## 1.3.0-beta — 2026-10-05

A beta that takes MoleHill terrains into Revit without scripting, and finishes the Grasshopper icons.

**Rhino.Inside.Revit**
- New **Write Toposolids** and **Inspect Toposolids** components on a MoleHill > Revit tab, replacing the
  three Python scripts that had to be pasted into Script components and wired input by input. Write
  takes Prepare Toposolid's Preparation output as a single wire and creates or updates one Toposolid
  per terrain, with its zones as subdivisions. Each element is matched by its terrain key: unchanged
  geometry is left alone, changed geometry is replaced in one undo step, and nothing is deleted because
  it is missing from a run.
- The components ship in the same package and appear only inside Rhino.Inside.Revit (Revit 2025 or
  later); plain Rhino never shows them, and no Revit or Rhino.Inside files are added to the package.
  They have not yet been run in a Revit host, so treat the Revit step as unverified and report results.

**Grasshopper**
- Every component has its own icon. Terrain Snapshot, Construct/Deconstruct Terrain, Partition Terrain
  and Prepare Toposolid had none; Balance Grade Pad and In-Situ Stair borrowed a sibling's; Add
  Geometry, Project To, Simplify and Retopo were blurred 16 px images.

**Editing**
- Builds that are cancelled, superseded or fail now release the meshes they made instead of leaving
  them to the garbage collector.
- A debounced rebuild that falls due while Rhino is idle starts on time instead of waiting for the
  next mouse move.

## 1.2.1 — 2026-10-04

Stable release incorporating the large-terrain, grading, layer and retaining-wall changes from
1.1.0-beta and 1.2.0-beta, plus these fixes:

- Shared triangulation splits breaklines at existing T-junction vertices, including callers outside
  the initial TIN build, and explains the first failure when it falls back to plain Delaunay.
- Remesh preserves manifold output at float-precision mesh hand-off; Smooth avoids unnecessary
  Rhino normalization.
- Zone splitting preserves each source face's winding and builds zone meshes through the shared
  managed normalization path.
- Terrain JSON persistence runs after redraw rather than delaying it.
- Layer pickers use a native grid for large layer lists; keyboard navigation selects and scrolls
  without rebuilding the filtered list, and Enter/Escape work with the grid focused.

## 1.2.0-beta — 2026-10-02

A beta that makes terrains built from contours and snapped breaklines hold their lines, and makes retaining
walls insert into them reliably.

**Terrain**
- A breakline drawn to end on another no longer drops the whole terrain to plain Delaunay. One such junction
  used to make Triangulate ignore every breakline and contour and fill the site with long slivers; the
  junction is now split exactly. When Triangulate does have to fall back, its message says why.

**Retaining walls**
- Walls insert into terrains with long, thin triangles. A 24-wall case that inserted none of its walls, and
  so left them standing on unchanged ground, now inserts all of them.
- A closed ring wall follows its rail height on every side; the side closing the ring kept the ground's height.
- Wall edits on coarse contour terrains are about a quarter faster.

**Editing**
- The panel refreshes only when a finished build changed what the cards show, and only the status line
  updates while a rebuild is pending. A 100k-face wall edit evaluates in 27 ms instead of 40.
- The rebuild prompt for slow terrains appears only once a terrain has been measured slow.
- Long property labels wrap instead of being clipped.

## 1.1.0-beta — 2026-10-01

A beta for large terrains: edits now cost in proportion to what they change, not to the size of the site.
Graded surfaces can differ slightly from 1.0.0 where grading and Remesh now work in windows and tiles.

**Faster edits on large terrains**
- Editing no longer rebuilds everything below the change. Adding, moving or renaming a card, or a card with
  nothing selected yet, rebuilds nothing; such a card says why it has no effect.
- Remesh works in tiles and reuses every tile an edit did not touch. On a 3.2 million point park it is 2 to 3
  times faster cold, with better triangles, and a small survey edit re-meshes only its neighbourhood.
- Grade Pad, Grade Path and retaining-wall insertion work in windows around what they grade. An edit away
  from them reuses their earlier result.
- Cut / Fill is about ten times faster: the few points over a wall no longer scan the whole reference.
- Every stage hands its mesh on faster; the result is unchanged, verified bit for bit against Rhino.
- On the 1 m park, one survey-point edit went from 144 s to 31 s. On a 100k-face terrain a wall edit
  evaluates in 60 ms.

**Grading**
- New **Grade Through Breaklines** option on Grade Pad, Grade Path and Grade Line. Breaklines stay hard
  by default; with the option on, the card regrades across them and drops the parts it regraded, so later
  cards do not pull the old ground back.
- A path crossing a hard line (a pad edge, a wall) is stopped at it instead of the whole card refusing to
  grade.
- Breakline-mode retaining walls whose rails cross a breakline or contour at another height now stop with an
  error naming where, instead of spending seconds on a rebuild that could not succeed.
- Retaining walls: graded walls keep their rails and insert by local triangulation; one-sided and ring rails
  batter to daylight; a ring wall too small for its inward batter keeps its rails. Every wall inserts across
  a 1,152-case sweep of shapes, slopes and neighbours.
- Remesh no longer freezes straight breaklines as walls where zero-area faces lay along them, and
  Triangulate keeps breaklines as triangulated, which removes thousands of slivers after a wall rebuild.

**Layers and import**
- Each terrain's generated output goes under its own `MoleHill {terrain}` layer root, so several terrains
  in one document no longer share layers. Renaming a terrain moves its layers; duplicating copies its own
  inputs only; deleting removes its empty layers.
- Survey import writes plain geometry to a layer tree named after the survey file, outside every terrain,
  and re-importing replaces or adds.

**Other**
- A warning when a terrain sits too far from the origin to build reliably, naming the tolerance that fits
  when the site is too wide to move.
- Modifier cards use one vocabulary for breaklines, boundaries, borders, creases and Protect.

## 1.0.0 — 2026-09-29

First stable release; it is no longer marked as a prerelease.

- New Gradient Compliance analysis: checks level areas and routes against an accessibility gradient
  standard and colours the terrain by verdict (pass, ramp, report, warn). Routes are split into running
  and cross slope, detected landings and runs are followed along the route, and each run's rise is
  measured between its landings. Ramp limits are edited in place as a going table on the card. The chosen
  standard is copied into the project, so a later plug-in update cannot change an existing verdict.
- Scatter's "Real (capped)" preview no longer hangs Rhino. It used to leak display memory on every
  redraw until the graphics driver closed Rhino; it now draws each block from a cached mesh, about ten
  times faster, with flat memory. Surfaces shade by their material, as Rhino's own display modes do.
- Shaded, Arctic and Rendered viewports no longer draw terrain, walls and scatter twice. Render-engine
  geometry meant for Raytraced and final renders was also being drawn in those modes, which showed
  every scatter instance as real geometry whatever its preview mode.
- Display modes with shadows no longer scatter stray points across the terrain. The shadow pass now draws
  only surfaces.
- Scatter blocks built from surfaces and polysurfaces now appear in Raytraced and final renders.
- Colour ramp stops accept an exact value (double-click the value), and dragging a stop past another no
  longer switches to its neighbour or jumps when the drag is interrupted.
- The scatter block picker opens immediately on large block libraries, loading thumbnails as rows appear.
- `mhExternalizeBlock` asks for a file name, and links the block through RhinoCommon instead of a
  scripted Block Manager call.
- `mhDrapeCurve` asks for the curves first, then the target, which can be the active MoleHill terrain.
  Its default spacing no longer mixes up model units and metres.
- Retaining walls in Breaklines mode once again insert their toe and top rails as direct terrain
  breaklines, instead of surrounding them with a refined terrain-elevation patch that softened the wall
  into a bump. Graded retaining walls keep the quality-patch protection.
- The In-Situ Stair card no longer shows a daylight slope control before that grading workflow is ready.

## 0.14.7-beta — 2026-09-26

- First release from the public repository.
- Every toolbar button icon is redrawn, and the dock panel's Add, Duplicate, Delete, Clear, Show/Hide,
  Rebuild, Reset Build and Bake glyphs are redrawn from scratch.
- Two unused macros in the toolbar file that called scripts outside MoleHill are removed. They were on no
  toolbar, so nothing visible changes.
- The licence notice now states Triangle's terms in full: MoleHill stays free, and it may not be sold or
  included in a commercial product without the Triangle author's licence.

## 0.14.6-beta — 2026-09-26

- Faster terrain builds: the spatial indexes no longer collapse under hash collisions, Remesh and
  grading constraint resolution are roughly halved on large terrains, and ponding is solved per basin.
- Grade Pad rounds are quicker on large terrains, and finished meshes are unchanged.
- Toggling a runtime overlay no longer re-solves the Catchments and Ponding previews.
- Section annotations resolve a cut/fill reference once per annotation instead of once per section.
- Mesh faces wound the wrong way across a seam are repaired again on coloured analysis meshes.
- Housekeeping: large internal files split by concern, unused code removed, documentation refreshed.
