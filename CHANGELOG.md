# Changelog

Released through Rhino's Package Manager (Yak) as `MoleHill`. Versions before 1.0.0 were
beta prereleases. Earlier versions are recorded only in the git history.

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
