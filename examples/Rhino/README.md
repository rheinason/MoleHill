# Rhino Python prototypes

The compiled plugin now provides `mhSlopeControlPoints`, which also supports preselected control
points, Ctrl+Shift-selected curve segments and a live slope preview. The Python example below
remains a standalone version of the original workflow.

## Merge curve control points

The compiled plugin provides `mhMergeControlPoints` with the same four modes, direct preselection
or ordered grip picks, and a live preview before Enter applies the merge. The standalone script
below remains available without the plugin.

Run [`merge_control_points.py`](merge_control_points.py) in Rhino 8's `ScriptEditor`
(Python), or `EditPythonScript`. No MoleHill installation is needed.

1. Turn on curve control points and preselect the points to merge, including
   points on separate curve objects.
2. Run the script and choose `ByDistance`, `AtCenter`, `First`, or `Last`.
   The script uses the preselected points immediately, without asking for curves
   or asking you to select the points again. By Distance also asks for a distance.
   Esc cancels before editing.

Without preselection, the script asks you to pick visible curve control points
directly, then choose the method. Pick individually and press Enter when finished.

- **By Distance:** enter a nonnegative distance in model units. Selected points
  within that 3D distance form clusters; each cluster moves to its average position.
  Clustering is transitive: A near B and B near C puts all three in one cluster,
  even if A and C are farther apart than the distance. Unselected points are excluded.
- **At Center:** move all selected points to their average XYZ position.
- **First / Last:** move them to the first / last point in Rhino's returned
  preselection order. Rhino does not expose historical click order here, so these
  are not guaranteed to match which point was clicked first/last, especially for
  window selections. Without preselection, the script records individual pick order.

This prototype merges **locations**, keeping each curve separate and retaining its
NURBS control-point count, weights, and knots. It does not join curves or remove
control points. Closed/periodic curves can have coupled seam grips that Rhino moves
together. Moving interior control points changes the curve without necessarily
making it pass through the merge location. Some collapses create invalid curves;
the script rejects these and restores the affected curves.

Edits retain object IDs and attributes and use one Rhino undo record. The original
points-on and grip selection states are restored. Distance checks are pairwise,
intended for manually selected sets rather than huge control-point arrays.

Verified in a disposable Rhino 8 on 2026-10-15: all four target calculations,
transitive distance clusters, a native cross-object NURBS merge with retained IDs,
attributes and point counts, workflow execution with supplied picks, points-on and
grip-selection restoration, cancellation, and rollback after a simulated failure on
the second curve. Interactive picking and Rhino Undo remain manual checks.
The preselection workflow was also verified with native selected grips on separate
curves: point picking was bypassed, each method reached the merge directly, selection
and points-on states survived, and cancelling the method left the curves unchanged.

## Move a curve control point by slope

Open [`move_control_point_by_slope.py`](move_control_point_by_slope.py) in Rhino 8's
`ScriptEditor`, choose Python, and run it. It also uses syntax compatible with
`EditPythonScript`. MoleHill does not need to be installed.

1. Select the curve. The script temporarily turns on its control points.
2. Pick the control point to move.
3. Pick a reference point anywhere in the model. Use Point object snap to pick
   another visible control point, or End to pick a curve endpoint. If the reference
   belongs to another curve, turn that curve's points on before running the script.
4. Enter a signed percentage: `2` means +2%, `-2` means -2%, and `0` means level.
   This standalone prototype deliberately asks for percent only; enter the number
   without a `%` suffix.

The selected point keeps its world X and Y. Its new elevation is
`reference.Z + horizontal_XY_distance * percent / 100`. For example, a reference
at Z=10 and a control point 20 model units away in plan gives Z=10.4 at +2%.
This matches the distance convention of `mhGradientInterpolation`, regardless of
the active construction plane. Points coincident in plan within model tolerance
are rejected because they do not define a finite slope.

The existing curve is edited through Rhino's grip API, retaining its object ID
and attributes. Esc before the edit leaves the geometry unchanged; the original
points-on state is restored on exit. Use Rhino Undo to undo the edit.

This sets the slope between the **control point locations**. An interior NURBS
control point generally lies off the curve, so it does not set the curve's local
tangent slope. Rhino may also adjust coupled grips at closed or periodic seams.
No new point objects are created. The prototype has no dynamic preview and is
not registered as a MoleHill command.

API reference: [McNeel's RhinoScriptSyntax grip methods](https://developer.rhino3d.com/en/api/RhinoScriptSyntax/#grips).

Verified in a disposable Rhino 8 on 2026-10-15 with programmatically supplied
prompt results: a native NURBS grip edit, unchanged X/Y and other control points,
retained object ID, signed/zero slopes, zero-run/nonfinite rejection, cancellation,
and points-on restoration. Interactive picking and Undo remain manual checks.
