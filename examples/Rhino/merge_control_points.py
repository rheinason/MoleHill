# -*- coding: utf-8 -*-
"""Collapse curve control point locations, within one curve or across curves.

Run in Rhino 8 ScriptEditor (Python) or EditPythonScript. No plugin required.
This moves grips; it does not delete NURBS points or join separate curves.
"""

import math

import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc


def merge_targets(points, mode, distance=None):
    """Return target positions. Distance clusters use transitive 3D proximity."""
    if len(points) < 2 or any(not point.IsValid for point in points):
        raise ValueError("Select at least two valid control points.")
    if mode not in ("ByDistance", "AtCenter", "First", "Last"):
        raise ValueError("Unknown merge mode.")
    if mode == "First":
        return [points[0]] * len(points)
    if mode == "Last":
        return [points[-1]] * len(points)

    groups = [list(range(len(points)))]
    if mode == "ByDistance":
        if distance is None or math.isnan(distance) or math.isinf(distance) or distance < 0:
            raise ValueError("Distance must be finite and nonnegative.")
        parents = list(range(len(points)))

        def root(index):
            while parents[index] != index:
                parents[index] = parents[parents[index]]
                index = parents[index]
            return index

        # Prototype: exact pairwise checks, suited to manually selected grips.
        for i in range(len(points)):
            for j in range(i):
                if points[i].DistanceTo(points[j]) <= distance:
                    parents[root(i)] = root(j)
        clusters = {}
        for i in range(len(points)):
            clusters.setdefault(root(i), []).append(i)
        groups = list(clusters.values())

    targets = list(points)
    for group in groups:
        count = float(len(group))
        center = Rhino.Geometry.Point3d(
            math.fsum(points[i].X / count for i in group),
            math.fsum(points[i].Y / count for i in group),
            math.fsum(points[i].Z / count for i in group))
        if not center.IsValid:
            raise ValueError("Merge position is outside the supported range.")
        for i in group:
            targets[i] = center
    return targets


def selected_curve_grips():
    """Read selected grips directly, without a getter that changes selection.

    Rhino exposes enumeration order, not the historical mouse click order.
    First/Last therefore use the order returned by Rhino for preselection.
    """
    keys = []
    for obj in sc.doc.Objects.GetSelectedObjects(False, True):
        if not isinstance(obj, Rhino.DocObjects.GripObject):
            continue
        owner = sc.doc.Objects.FindId(obj.OwnerId)
        if owner is None or not isinstance(owner.Geometry, Rhino.Geometry.Curve):
            raise ValueError("Select curve control points only.")
        key = (obj.OwnerId, obj.Index)
        if key not in keys:
            keys.append(key)
    return keys


def pick_grips():
    """Return (owner ID, grip index) keys; None means Esc, Enter finishes."""
    keys = []
    while True:
        getter = Rhino.Input.Custom.GetObject()
        try:
            getter.SetCommandPrompt(
                "Pick curve control points in order; Enter when done")
            getter.GeometryFilter = Rhino.DocObjects.ObjectType.Grip
            getter.EnablePreSelect(False, True)
            getter.EnableUnselectObjectsOnExit(False)
            getter.AcceptNothing(True)
            result = getter.Get()
            if result == Rhino.Input.GetResult.Cancel:
                return None
            if result == Rhino.Input.GetResult.Nothing:
                return keys
            if result != Rhino.Input.GetResult.Object:
                return None
            for i in range(getter.ObjectCount):
                grip = getter.Object(i).Object()
                key = (grip.OwnerId, grip.Index)
                owner = sc.doc.Objects.FindId(key[0])
                if owner is None or not isinstance(owner.Geometry, Rhino.Geometry.Curve):
                    print("Pick curve control points only.")
                    continue
                if key not in keys:
                    keys.append(key)
                    grip.Select(True)
            sc.doc.Views.Redraw()
        finally:
            getter.Dispose()


def apply_merge(keys, mode, distance=None):
    """Commit all affected curves in one undo record; restore on failure."""
    if len(set(keys)) != len(keys):
        raise ValueError("Each control point must be selected only once.")
    points = [rs.ObjectGripLocation(owner, index) for owner, index in keys]
    if any(point is None for point in points):
        raise ValueError("A selected control point is no longer available.")
    targets = merge_targets(points, mode, distance)
    if all(a == b for a, b in zip(points, targets)):
        return 0
    owners = list(dict.fromkeys(owner for owner, index in keys))
    originals = {}
    locations = {}
    for owner in owners:
        obj = sc.doc.Objects.FindId(owner)
        if obj is None or obj.IsLocked or obj.IsReference:
            raise ValueError("All selected curves must be editable.")
        originals[owner] = obj.Geometry.DuplicateCurve()
        locations[owner] = list(rs.ObjectGripLocations(owner))
    for key, target in zip(keys, targets):
        locations[key[0]][key[1]] = target

    undo = sc.doc.BeginUndoRecord("Merge curve control points")
    touched = []
    try:
        for owner in owners:
            touched.append(owner)
            if rs.ObjectGripLocations(owner, locations[owner]) is None:
                raise ValueError("Rhino could not update a curve.")
            if not sc.doc.Objects.FindId(owner).Geometry.IsValid:
                raise ValueError("This merge would make a curve invalid.")
        return sum(a != b for a, b in zip(points, targets))
    except Exception:
        failed = [owner for owner in touched
                  if not sc.doc.Objects.Replace(owner, originals[owner])]
        if failed:
            raise RuntimeError("Could not restore every curve. Use Rhino Undo.")
        raise
    finally:
        if undo:
            sc.doc.EndUndoRecord(undo)


def merge_control_points():
    states = {}
    try:
        keys = selected_curve_grips()
        preselected = bool(keys)
        # With no preselection, select visible grips directly. Never ask for curves.
        if not keys:
            keys = pick_grips()
        if keys is None:
            return
        if len(keys) < 2:
            print("Select at least two control points. No curves changed.")
            return
        for owner, index in keys:
            if owner not in states:
                states[owner] = (rs.ObjectGripsOn(owner), rs.SelectedObjectGrips(owner) or [])
        mode = rs.GetString("Merge control points", "AtCenter",
                            ["ByDistance", "AtCenter", "First", "Last"])
        if mode is None:
            return
        if preselected and mode in ("First", "Last"):
            print("First/Last use Rhino's returned grip order; preselection does not retain click order.")
        distance = None
        if mode == "ByDistance":
            distance = rs.GetReal("Merge distance in model units (3D)",
                                  sc.doc.ModelAbsoluteTolerance, 0.0)
            if distance is None:
                return
        changed = apply_merge(keys, mode, distance)
        print("Merged control point locations: {0} moved across {1} curve(s).".format(
            changed, len(set(owner for owner, index in keys))))
    except (ValueError, RuntimeError) as error:
        print(str(error))
    finally:
        for owner, state in states.items():
            if rs.IsObject(owner):
                if state[0]:
                    rs.EnableObjectGrips(owner, True)
                    rs.UnselectObjectGrips(owner)
                    for index in state[1]:
                        rs.SelectObjectGrip(owner, index)
                else:
                    rs.EnableObjectGrips(owner, False)
        sc.doc.Views.Redraw()


if __name__ == "__main__":
    merge_control_points()
