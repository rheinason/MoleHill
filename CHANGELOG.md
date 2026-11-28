# Changelog

Released through Rhino's Package Manager (Yak) as `MoleHill`. Versions before 1.0.0 were
beta prereleases. Earlier versions are recorded only in the git history.

## Unreleased

- **Edit Profile** (`mhEditProfile`) replaces Extract Profile / Apply Profile. Pick a curve and MoleHill draws
  its long section as an ordinary curve, behind it a grid with elevation and station scales, the ground
  under the curve, and every segment's true grade in your slope unit (red over your Inspect Curve limit).
  - Edit the section with any Rhino tool: drag grips, use the gumball, Rebuild, Trim, Split, Join, or draw a
    new stretch and join it in.
  - `mhSlopeCurve`, `mhSlopeCurveSection` and `mhSlopeControlPoints` work on the section in **true** grades:
    2% typed is a true 2% (drawn 10% steep at 5x), and Slope Curve Section's BlendToTerrain blends to the
    section's ground line.
  - Inspect Curve opens beside it and reads the curve *as it will be*, with **Accept changes** and **Cancel**.
    Each is one undo step, and an Undo of Accept brings the section back.
  - Only what you change moves: stretches of the curve you did not edit keep their own points exactly, and
    where you trim the section away the curve keeps its heights. A smooth curve stays smooth unless you untick
    Keep smooth.
  - When the curve feeds a terrain card (a Grade Path, say), the ground shown is the terrain that card grades,
    and the command says which cards will update.
  - The section is saved with the file, so you can finish later. Running Edit Profile again with a section
    open offers Inspect, Accept or Cancel.
  - Removed: `mhExtractProfile` and `mhApplyProfile`. Moving their drawn section changed its elevations; the
    new grid stays put. For a drawing of a long section use the Section Along Curve annotation.
- Slope Curve Section and the other slope commands print lengths and elevations with a decimal point on
  every machine (a comma locale printed 24,000 for 24 m).
- **Kerbs with shaped ends and crossings.** A Retaining Wall whose two curves come together at the end, in
  a point or a round nose, now builds; before, the pair was skipped as too narrow. The wall solid follows
  the drawn end. The terrain's top breakline stops a few millimetres short of the tip, because a terrain
  cannot hold two heights at one point, and the terrain closes the nose with a steep face. The ground
  round the end now wraps it smoothly: a kerb drawn with its top above the ground used to leave a dark
  wedge reaching a metre or more past the nose, in both modes.
- **Kerb cuts.** A wall whose top curve drops to the toe's height for a crossing no longer loses its
  solid: it is built as one solid each side, tapering into the cut. A wall tapering to zero height at an
  end is now a closed solid too.
- **Wall Height** on the Retaining Wall card, for freestanding walls made from two curves sketched
  anywhere. With a height set, both curves are laid on the terrain as breaklines and the ground is left as
  it is; the wall solid stands that high above the higher of the two. Mode and grading do not apply and
  are hidden. 0 keeps the curves' own heights, as before.
- **Graded curved walls** left the terrain up to 9 cm off the wall's top and toe in places: the grade
  routed the rails through nearby terrain points instead of along the drawn curves. The rails are now
  moved onto the drawn curves, and the terrain follows the wall within a few millimetres.
- **Graded walls with rails closer than the grade's snap distance** (a narrow kerb, or the rails meeting at
  a pointed end) could lift stretches of the toe onto the wall top. The rails are now inserted instead
  when the graded terrain cannot hold them apart.
- **Remesh keeps curved breaklines.** A Remesh coarser than a curve's stations kept the curve in its first
  pass and smoothed across it in the next: a curvy Grade Path's road edges sagged up to 0.4 m at a 3 m edge
  length. They now stay within a few centimetres, as close as a single whole-terrain pass. A second Remesh
  card, or a Retopo after a Remesh, no longer loses a curve the first one coarsened either. And the corners where a
  graded road ends stay put: Remesh rounded them off by up to 10 cm.
- **Remesh's Crease Angle now defaults to 10 degrees** (was 30), on new Remesh cards and the Grasshopper
  Remesh component (was off). Most batter toes fold less than 30 degrees, so a 1:2 or 1:3 batter's toe and
  daylight line were smoothed away, up to 0.5 m off; at 10 they stay within about 0.2 m. Rough scanned ground
  is unaffected. Existing cards keep their value: lower it by hand to get the same.
- **Search in the Add menus.** Add Modifier, Analysis, Annotation and Object open a list with a search box:
  type to filter (names, initials such as `rw`, descriptions, and synonyms such as `kerb`), Up/Down and
  Enter to add, Esc to close.

## 1.7.0-beta — 2026-11-23

A redesign of the MoleHill panel and its dialogs, from a full design review of every card.

**Compatibility**
- Documents saved with this version use terrain schema 34, which 1.6.0 and earlier refuse to open. Older
  documents open and are upgraded on load.

**Fixes**
- The panel follows a switch between Rhino's light and dark themes, including after it has been hidden in
  the dock. Before, its surfaces could stay dark and its icons fade on a light Rhino.
- After a bake, Untrack Selected / Untrack All become available straight away.
- Scatter cards show the new placed counts after every rebuild, never a leftover "count pending".
- Slope arrows point downhill unless Flip Arrow is ticked, as the card says; they pointed uphill. Cards
  saved by earlier versions are converted with Flip Arrow switched, so existing drawings do not change.
- At narrow panel widths the Zones and Analysis tabs keep their icons beside their view buttons, and the
  Zones heading wraps instead of running under the scrollbar.

**Spot labels**
- **Spot Heights** and **Spot Slope** are one card each, placed at **Points**, along **Curves** or on a
  **Grid**. They replace Spot Heights (Points), Spot Heights (Curve), Spot Slope (Points), Spot Slope
  (Curve) and Flow Arrows, which is now Spot Slope on a grid. Spot heights on a grid are new.
- Each placement keeps its own sources, so switching placement and back finds them again.
- Existing cards are converted on load and keep their name, place in the stack, sources, block and
  formatting.

**Scatter**
- Along a curve, Block Mix chooses **Random** or **Sequence** at its top. In Sequence the blocks are an
  ordered list: drag a row to reorder the repeating pattern, and untick a block to leave it out. Weights
  are hidden there and kept for Random.
- A saved sequence that left a block out with weight 0 has that block unticked on load, so its pattern is
  unchanged.

**Panel settings**
- The Terrain Settings card fits a narrow panel: Opacity has its own row, the display and bake options span
  the card, and nothing is cut off. It no longer carries a coloured strip, which marks cards in the stacks.
- The toolbar's two rows of icons line up.
- Cards and sections open and close from the keyboard (Enter or Space on the arrow), and the number field
  arrows no longer take a Tab stop each.
- Every icon and arrow has a tooltip, and a greyed-out control says why it is unavailable.
- The build progress in Status is written with a decimal point on every machine.

**Cards**
- Every card header has two fixed lines: the card's name, then its type and a summary (for example
  "Contours · 2 objects · 1 layer"). Long text ends in an ellipsis, with the full text in the tooltip.
- Two- and three-way choices are buttons rather than drop-downs, and a card's main mode comes first.
- Units sit inside number fields. Numbers are written with a point; a comma typed as the decimal is read
  as one.
- Secondary settings are in sections that start closed and stay as you leave them. Measured values are
  under one **Results** section, and a source picker shows its count in its heading.
- Earthworks and Cut / Fill choose what they compare against: **Off**, **Original**, **Terrain** or
  **Geometry**.
- Report Table lists only the sections this terrain can fill. Legend shows which analysis it keys.
- Section Along Curve samples at the terrain's Detail Size unless you set an interval, which finds sharp
  crests a fixed 1 m spacing missed.
- Displace's origin is picked as a point.
- The colour ramp's controls were reworked, and Landforms has a key.

**Panel**
- The tab strip fits narrow panels: labels show only when all five fit, and every tab names itself in a
  tooltip.
- The Status line shows the outcome of the last build, coloured by severity.
- **Import / Export Terrain Definition** in the terrain menu moves a whole terrain between files as a
  `.mhterrain` file. Assigned layers come with it, picked objects do not, lengths are converted to the
  document's units, and a preview lists layers to reassign.
- Dialogs share one layout: the main action then Cancel on the right, units inside fields, and no clipped
  labels or buttons.

## 1.6.0 — 2026-11-20

The first stable release since 1.4.0. It contains everything from the 1.4.1–1.5.5 betas below; their
entries give the detail. The code is 1.5.5-beta's, with the Enscape preview labelled experimental.

**Highlights since 1.4.0**
- **Scan to Terrain** turns a drone or photogrammetry mesh, or a point cloud, into MoleHill input: ground,
  contours, spot heights, buildings, walls and kerbs. **Terrain to Curves** turns a dense terrain into
  editable contours and crease lines.
- **Scatter** can keep whole blocks inside a boundary, plant into selected zones, avoid exclusion curves,
  set back and feather edges, bake one card at a time, and report planting quantities.
- **Plant and Orient** keep each object's base with the object through bake, moves, copies and saves, and
  Undo after moving a placed object no longer duplicates it.
- **Cut and fill volumes are exact** in Earthworks, Cut / Fill, zones and the graders (they were 9-16% off
  on coarse terrains), and **Balance Pad** balances several pads at once.
- New **Displace** modifier, **Round Creases** mode for Smooth and **Curvature** analysis.
- Route tools: **Relax Route**, a reworked **Suggest Route**, **Slope Control Points**, **Merge Control
  Points**, **Drape Curve Section**, and **Extract / Apply Profile** (prototype).
- **Export With Georef** to any format Rhino exports, and **Copy With Georef**.
- Grading follows one cut/fill rule, is checked against a grading envelope, and Grade Path stops where a
  breakline, wall or pad edge enters its road instead of moving it.

**Fixes since 1.4.0** include In-Situ Stair tearing walls and notching the border, Remesh Rebuild hanging,
stair stringer rails moving through Remesh, Smooth making new peaks, zone seams zig-zagging on dense
terrains, the terrain preview in layouts, nested blocks missing from renders, and text dots hidden under
the terrain.

**Known limitation**
- The scatter **Enscape / native blocks (experimental)** preview has not yet been checked inside Enscape.
  Bake is the reliable route to renderers.

## 1.5.5-beta — 2026-11-20

**Fixes**
- Undo after moving an oriented or planted object no longer duplicates it. One Undo now takes back both the
  move and the live re-orientation that followed, and Redo restores both, with the object still on its card.
  This was the known issue in 1.5.4-beta.
- Nested blocks (a block made of other blocks, such as a tree canopy) no longer vanish from renders of a
  scatter. Only a block's top-level members were sent to the renderer.

**Scatter**
- **Keep whole blocks inside** keeps the whole block within the boundary, not just its insertion point. It
  checks the block's bounds after rotation, scale, tilt and offset, and respects holes, exclusions and
  setbacks. Blocks that do not fit are dropped rather than replaced, so you can get fewer than you asked for.
- A scatter can be limited to selected terrain zones instead of picked boundaries. Shared edges between the
  selected zones are not treated as borders, and holes in a zone receive nothing.
- New planting controls: exclusion curves with a clearance, a boundary setback and a soft feathered edge.
  Density can be shown per m², per 100 m² or per hectare.
- The Block Mix shows each block's thumbnail, share and placed count, and each block can be switched off
  without removing it. Ratios can be typed: double-click the number, type, and press Enter (Esc cancels).
- Each scatter card shows how many blocks were placed and why the rest were rejected; **Show Issues** marks
  rejected spots in the viewport.
- **Bake this scatter** bakes and replaces one card's blocks without touching the rest of the terrain. Bake
  is also somewhat faster (about 14% at 30,000 blocks).
- New **Enscape / native blocks** preview places temporary copies of your real blocks in the document, up
  to the Preview Cap, so renderers that only see document objects can show them. Baked blocks keep their
  Enscape properties. This has not yet been checked inside Enscape itself.
- The Report Table annotation has a **Planting** section listing counts and percentages per block, and
  `mhExportTerrainReport` includes it in the CSV.

**Panel**
- Dragging a slider on cards that support live editing updates the terrain while you drag, not only when you
  let go.

## 1.5.4-beta — 2026-11-02

**Fixes**
- Layouts no longer show a stray copy of the terrain. The preview was also drawn in the page's own space, at
  its model coordinates, beside the detail that should hold it.
- A layout detail no longer turns the terrain flat gray when you zoom the page. Once the page was zoomed far
  enough that a detail ran past the window edge, every pass in it was taken for a shadow-map pass, and only
  the gray shadow-casting mesh was drawn, without the terrain's colours or analysis colouring.

**Plant and Orient**
- An object's base now belongs to the object, not the card: it is kept through bake, moves and other
  transforms, copies and saves, so a planted object is never re-fitted from its tilted geometry.
- Orient fits the object from its footprint, and Plant keeps the object's tilt.
- The card can show and correct an object's base, and has explicit actions to release objects from it.
- A failed solve or restore leaves the object and its history as they were.

**Real-world coordinates**
- Export With Georef offers every format Rhino can export (including formats added by other plug-ins), not
  only `.3dm`. A `.3dm` export carries the selected objects' layers and attributes but no other document
  content.
- New `mhCopyWithGeoref` (right-click on the Export With Georef button) copies the selection at real-world
  coordinates, ready for an ordinary Paste in a real-world file.

**Known issue**
- After Orient, Bake and a native Move, one native Undo can leave the oriented objects duplicated, and Redo can
  lose their card assignments. Delete the duplicates by hand until this is fixed.

## 1.5.3-beta — 2026-10-28

**Fixes**
- In-Situ Stair no longer tears the terrain. Its tiny-face cleanup read every steep face as tiny, so after a
  Retaining Wall it deleted the wall faces: both rails opened along the wall's whole length, and small holes
  opened across the site, far from the stair.
- In-Situ Stair no longer notches the terrain's border. It peeled the border again after grading, which ran up
  long needle triangles from slivers along a sparse border and cut V-shaped slits many metres into the site;
  Remesh then opened them into holes. The border now stays exactly as Triangulate left it.
- In-Situ Stair grades only the ground within its reach, as Grade Pad and Grade Path do; the rest of the terrain
  passes through untouched. It used to re-triangulate the whole site for a 3 m stair and drop hundreds of points
  along the border, which could stop a Retaining Wall stacked after it from inserting. The Grasshopper In-Situ
  Stair component grades the same way.
- Remesh (Rebuild mode) and a Retaining Wall's rebuild fallback can no longer delete wall faces. Their tiny-face
  cleanup now judges a face by its true size, not its size in plan (a vertical wall face has none), and only ever
  takes faces from the border. Its old guard engaged only on a terrain with one clean border, so on a terrain
  with a hole it could tear every wall open.
- Remesh (Rebuild mode) no longer hangs. A rail stationed along a line already in the terrain sat a float's
  rounding off it, and the triangulator read the two copies as crossing; on one site it spun for over 20 minutes,
  blocking every other triangulation meanwhile. Such overlaps are now joined before triangulating, and the
  triangulator gives up on an inverted mesh instead of looping. That site's rebuild now takes under a second.
- Walls beside stairs (stringers) keep their rails through Remesh. Remesh re-spaces a wall rail's vertices, and
  its next pass then failed to recognise rail edges that skipped the rail's own stations and flipped them: the
  terrain at a stringer's top rail could fall 0.3 m below it. Remeshing twice is now as safe as once.
- In-Situ Stair no longer regrades a wall rail its batter reaches. A rail beside a stair already blocked the
  batter beyond it, but a vertex on the rail itself was regraded, lifting a stringer's toe rail by up to 0.1 m.

**Scan to Terrain**
- Walls and kerbs go on layers of their own, for Retaining Wall cards rather than as plain breaklines: wall
  top and toe rails on `Input::Walls`, kerbs on `Input::Walls::Kerbs`, so walls and kerbs can each have
  their own card.

## 1.5.2-beta — 2026-10-22

**Scan to Terrain**
- The panel no longer locks the views: it is a floating window and the command waits at the command line,
  so you can pan, zoom and rotate around the preview. Enter keeps a ready preview, Esc cancels, and Run and
  Keep are also command-line options.
- The panel opens at once. The scan and its texture are read in the background (a 1 M-triangle scan used to
  freeze Rhino for about 5 s first), and the scan runs only when you press **Run**.
- Output now follows MoleHill's layer structure, under `MoleHill <scan name>::Input`: Contours, Spots,
  Boundary, Zones (Paved and Soft) and one **Breaklines** layer holding every line the terrain must keep
  (wall tops and toes, paved outlines, kerbs, paving edges), each in its own colour. Buildings and soft
  outlines, which drive nothing, go under `Context`. A terrain of the same name reads them from the layers
  it already looks at.
- Walls at an angle to the scan's grid are traced. They broke into pieces under a metre and were dropped,
  so quay fronts and road edges were missing: a harbour test scan went from 8 to 14 walls, the two earlier
  reference scans from 9 to 48 and from 24 to 164, with both accuracy targets still met.
- More buildings are recognised: a roof level with the road behind it (a shed built into a slope), houses
  whose roofs are 65-70% planar, and turf roofs that are 85-90% planar. Rock and trees beside them stay
  ground.
- Terrain to Curves uses the same panel and writes to the same `MoleHill <name> curves::Input` layers.

## 1.5.1-beta — 2026-10-22

**Grade Path beside breaklines, walls and pads**
- A path now stops wherever a breakline, wall or pad edge enters its road, and the rest of it grades. It used
  to stop only where the centreline crossed one, so a bend, a pad corner or a stub reaching into the road, or
  a line through one of the path's own vertices, failed the whole grade.
- A grade no longer moves the breaklines, walls and pad edges it stops at. The batter could regrade the line
  itself, by up to the road's height above the ground, and on some terrains the corridor dropped the line
  altogether. A breakline lying inside the road was graded over; it now blocks the path there, as breaklines
  do (tick **Grade Through Breaklines** to grade across it).
- A path drawn to end on a pad's edge is left as drawn.

## 1.5.0-beta — 2026-10-21

**Terrain shaping**
- Smooth has a new **Round Creases** mode. It rounds batter crests, toes and slope breaks with a vertical
  curve of a stated **Rounding Length**, so graded terrain reads as landform instead of CAD. Crests drop,
  toes rise, and the plan never moves.
  - Crease Angle sets which folds count as creases.
  - Protect curves keep a kerb, road edge or pad edge sharp.
  - Walls are never touched.
  - Coarse triangles beside a crease are split first, so no Remesh is needed.
- New **Displace** modifier raises and lowers the ground by world-XY noise or a heightmap image.
  - Noise has a size, height, octaves, roughness, seed, rotation and origin; an image takes a tile size
    and a neutral grey, and a drawn **Mapping** rectangle places it.
  - Boundaries with a feather scope it. Breaklines, walls, graded edges, Protect curves and, with Hold
    Border, the border keep their height.
  - The card warns when the terrain is too coarse to carry the noise or the image, and says what Remesh
    edge length would.
- Modifier cards show what their last build did (a **stage report**): Displace its sampling, offsets and
  held vertices, with a warning when something needs attention.

**Analysis**
- New **Curvature** analysis colours the ground as **Landforms** (convex, planar, concave) or by its
  curvature value (mean, profile or plan) on the colour ramp, with the area of each landform.
  - **Landform Size** separates broad landforms from surface detail; Auto (a tenth of the site's shorter
    side) is shown greyed in the box. **Flatness Tolerance** sets what counts as planar.
- **Cut and fill volumes are exact.** Earthworks and the graders measured each face against the reference
  at one point, which read a balanced pad 9-16% off where the reference creases under it. They now overlay
  the two surfaces in plan and integrate the difference exactly.

**Input commands**
- New **Relax Route** (`mhRelaxRoute`, and the Suggest Route button's right-click): draw a rough route and
  watch it relax over the terrain towards a gentler, cheaper line, live.
  - Drag the route or its pins to steer it, and it follows like a rope; click a pin to release it. Every
    setting is on the panel.
  - A floating panel shows how much better than your sketch the route has become, climbing as it relaxes,
    and the compromise as one triangle to drag between **Short**, **Gentle** and **Little earthwork**,
    with sliders for how firmly to **keep to the sketch** and how plain the route should be.
  - Stretches the route does not fit are marked on the ground and listed: cut, fill, and climbs that need
    more route than the sketch has. **Help me** then searches near your sketch and through your pins, and
    finds the switchbacks.
  - **Keep** adds the route as a light curve, ready to edit and assign to a Grade Path.
  - Relaxing never ties the route into a loop. A loop a drag leaves around no pin is pulled out when you let
    go, as a rope pulls tight; one holding a pin is marked where it crosses.
- **Suggest Route** keeps each route as a light curve with only the control points it needs (typically
  10-40, where the exact arcs needed about a hundred), so it edits like a hand-drawn one. The grade limit
  still holds; the readout gives the control points and the tightest bend as drawn. `Curve=Exact` keeps
  the exact arcs.
- **Suggest Route** gives routes a designer would draw. On rolling ground the defaults used to return
  sub-metre segments, and corners far under the minimum radius:
  - The minimum radius is now a requirement, like the maximum grade. Every corner gets it unless a picked
    point or the terrain's edge leaves no room, and the extra earthwork shows in the cut and fill.
  - Bends that only followed small folds of the ground are merged, and the profile has grade breaks a
    few metres apart instead of a vertex every half metre.
  - The search grid is scaled to the route (about 1.3 m for a 250 m route, and faster).
  - Route numbers are drawn on top of every route, larger, and spread along them, so each one stays readable.
  - A new **Simplicity** option trades earthwork for plainer routes: fewer, broader bends and longer
    grades. At 0 a route follows the ground as closely as it can; the default, 2, offers routes from
    close to the ground to as plain as a hand-drawn road.
  - Every route reports a **complexity** score next to its earthwork, also stored on the kept curve: how
    much its curvature and its grade change. A bend at the minimum radius counts 2, a swing from level to
    the grade limit counts 1.
  - A climb steeper than the limit now switches back in long legs. The search used to gain the length it
    needed by zig-zagging every couple of metres, and rounding then left dozens of corners sharp.
- **Inspect Curve** shows grades in your slope unit everywhere: the summary, the labels on the curve, the
  hover readout and grade breaks. They were always percent, so a route built to 10° read 17.6%.
- **Drape Curve** drapes onto several meshes and surfaces at once and takes the top or the lowest
  surface (`Hit`). Stretches with nothing under them are split, bridged with a straight grade, or kept
  at the curve's own height (`Miss`); a split ends exactly on the terrain's edge. Each curve is reported
  on its own, and one that cannot be draped no longer stops the others. Polyline corners are kept.
  - `Loose=Yes` only moves the curve's own edit points onto the surface, so a smooth curve stays smooth
    with the same number of points.
- **Drape Curve Section** (right-click Drape Curve) drapes just a picked stretch and ties both ends back
  into the curve over a transition length.
- **Balance Pad** finds the height at which a pad's cut and fill balance and moves its boundary curve
  there. A curve already used by a Grade Pad card is balanced with that card's slopes, against the ground
  the card grades.
  - Select several pads to balance them `Together` (one move for the set) or `Each` on its own.
- New **Slope Control Points** sets the selected control points of a curve on one grade from a reference
  point. As with Offset, the mouse sets the slope until you type one; then the mouse only picks whether it
  rises or falls, and `Through` hands the slope back to the mouse. Pick control points before or after
  starting it, or pick a curve to show its points.
- New **Merge Control Points** pulls selected control points together, with a preview before it applies.
- **Extract Profile / Apply Profile** (prototype): draw a curve as a flat long section with the ground
  under it, edit the design line with any Rhino tool, and apply it back. The curve keeps its plan; a
  profile that doubles back is refused.
  - Run Extract Profile again and press Enter to redraw an existing section in place.
  - `Ground` takes the ground line from the active terrain, from picked meshes or surfaces, or leaves it out.
  - A smooth curve stays smooth: only its edit points take the profile, and the command says how far it
    strays between them. `Fit=Exact` gives a polyline that follows the profile exactly.
- **Terrain to Curves** turns a dense terrain (a DEM, a point-cloud mesh or the active MoleHill terrain)
  into a border, contours and spot heights that rebuild it within a tolerance, ready to use as editable
  input.
  - Contours are traced at a **Contour interval** and those the rebuild does not need are removed; spot
    heights go only where the ground still misses. Folds and wall edges become **crease** lines.
  - A floating panel previews the result and says how close it comes before **Keep**. A large terrain
    (100,000 vertices or more) waits for **Recompute**, and **Stop** ends a run.
  - The output goes on its own layers under "Terrain to Curves {source}", a fresh root for every run.
- New **Scan to Terrain** (`mhScanToTerrain`) turns a drone or photogrammetry mesh, or a point cloud,
  into the input a designer would draw.
  - It finds the ground under buildings, trees and cars, and draws walls and terrace edges, kerbs, paved
    and soft-ground outlines, contours and spot heights, plus closed building solids with fitted roofs.
  - Paving is carried by its outlines draped on the ground; soft ground is contoured. Each has its own
    accuracy, and the panel reports how close the result comes to the scan.
  - Ground the scan never measured (its gap fills) is followed but not counted as a miss.
- New toolbar buttons for Balance Pad, Extract/Apply Profile, Terrain to Curves, Scan to Terrain, Slope
  Control Points and Merge Control Points.

**Fixes**
- Polyline input that is straight in plan but carries a profile (a graded centreline, a draped straight
  line) kept its heights when simplified. A suggested route assigned to Grade Path came out up to 1.04 m
  off its own centreline.
- A grading window kept the breaklines on all its faces: a contour just past a pad's reach was dropped and
  the batter cut through it.
- Panel cards no longer run under the scrollbar when they grow past the fold.

## 1.4.1-beta — 2026-10-15

A beta that keeps grading between the design and the ground, makes zones follow the lines as drawn,
and fixes text dots and block labels around the terrain preview.

**Grading**
- Every batter now chooses cut or fill by the same rule: the side the ground starts on. A Grade Path
  beside a low verge with a terrace behind it no longer climbs into a 45° bank standing metres above the
  ground; it daylights on the side it can reach.
- Grade Pad and Grade Path check every result against the grading envelope: outside the road or pad, a
  graded point lies between the existing ground and the design height. Points outside it are pulled back
  and the card warns that a grading fault was clamped, so a spike can no longer ship silently. In a cut,
  dips that are already at grade are left alone rather than filled into a band.
- Smooth never makes a new peak or pit, and leaves held points (the border, points outside its regions,
  fully held breaklines) where they are.
- Copy Case for grading now records tolerance, detail size and walls, and the copied test checks the
  grading rules instead of pinning the faulty output.

**Zones**
- Zones and Partition Terrain split along the line as drawn. On dense terrains the seam used to zig-zag
  up to 7 cm off a straight zone edge. Seams along and across retaining walls no longer crack.

**Display and annotations**
- Rhino's own text dots stay visible over the terrain preview instead of being painted over by it.
- Annotation and marker blocks are sized by the label text they actually contain, so a block drawn under
  a scaled dimension style no longer bakes many times too large, and block text previews at the size it
  bakes. Editing a block's text rebuilds the terrains that place it.
- The collapsed Status header shows a running build: "Building · Remesh (3 of 9) · 12 s".

## 1.4.0 — 2026-10-09

Stable release incorporating the changes from 1.3.0-beta through 1.4.0-beta. No functional changes
from 1.4.0-beta.

- Sections cut large terrains faster, show elevation and plan labels, and support multiple terrain
  profiles with colours and a key. Sections along several curves stack without overlapping.
- Block-attribute annotation cards can pick, copy and edit blocks; previews include hatches, solids,
  nested blocks and member colours. Legend annotations describe terrain analysis colours.
- The shipped layer template separates Input and Output under each terrain. Saved templates keep
  their paths.
- Terrain editing is more robust around thin triangles, crossing survey lines, zone seams and grading
  batters. Hide and Show boundaries work on large surveys, and Smooth preserves closed terrain edges.
- Retaining walls match rounded and stepped rail corners and shade flat at bends. Terrain previews no
  longer disappear after long editing sessions.
- Sculpting responds better on large terrains; zones and contours update during strokes. Rebuilds,
  contour trimming and terrain display do less repeated work.
- Grasshopper components have distinct icons, accept quad meshes, and expose Remesh mode and crease
  angle. Ponding and catchment values follow changes of model units.
- Rhino.Inside.Revit gains Write Toposolids and Inspect Toposolids for Revit 2025 or later, with document
  validation and protection against deleting existing subdivisions during host replacement. These
  components remain unverified inside Revit.

## 1.4.0-beta — 2026-10-09

A beta that makes sections fast on large terrains, labels them properly, and lets one section draw
several terrains again.

**Sections**
- Sections build far faster on large terrains. 200 cross-sections on a 320,000-face terrain went from
  92 seconds to under half a second, and a cut that runs exactly through mesh vertices no longer breaks
  the profile into pieces.
- Real elevations up the left edge of every section, on the elevation grid's steps (thinned so the
  figures never overlap), a title beneath it (A-A', or the station for cross-sections), and the matching
  mark in plan at the ends of the cut line. Each has its own switch on the card. They are on by default,
  so existing sections gain them on their next rebuild, and automatically placed cross-sections move
  apart to make room.
- One section can draw several terrains again. The card's Profiles list shows every other terrain
  whenever one exists, with the compared terrain ticked and fixed; it used to disappear whenever no
  terrain was left after the Compare To Terrain choice. Each extra profile's colour can be chosen on the
  card, and a key under the title names every line.
- Cut/Fill, Earthworks and the section cards open their comparison with the same rows in the same order.
- Section Along Curve with several curves stacks them instead of drawing them on top of each other.
- Station labels and ticks keep their distance from the drawing under vertical exaggeration, existing
  ground under a fill no longer runs through the labels, and a level section still gets its elevation.

**Annotations**
- Every block-attribute annotation card has a Block row: pick any block in the document, copy the
  built-in block into your own, open it in Rhino's block editor, or go back to the built-in one. Block
  previews now show hatches, solids, nested blocks and member colours as they bake.

**Layers**
- The shipped layer template hangs everything off two branches under each terrain: Input and Output.
  Saved templates keep their own paths.

**Retaining walls**
- Wall sides are split at their bends, so the preview shades each panel flat instead of smearing it.

**Rhino.Inside.Revit**
- Write Toposolids checks every Revit step completed, validates Type, Level and Subdivision Type against
  the document being written, and refuses to replace a host when that would delete subdivisions it does
  not recreate. Not yet run inside Revit.

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
