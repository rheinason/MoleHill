# Changelog

Released through Rhino's Package Manager (Yak) as `MoleHill`. Versions before 1.0.0 were
beta prereleases. Earlier versions are recorded only in the git history.

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
