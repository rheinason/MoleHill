# Changelog

Released through Rhino's Package Manager (Yak) as `MoleHill`. Versions are prereleases while MoleHill
is in beta. Earlier versions are recorded only in the git history.

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
