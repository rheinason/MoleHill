# -*- coding: utf-8 -*-
"""Rhino Python prototype: set a curve grip's elevation from a reference point.

Run in Rhino's Python editor. No MoleHill installation is required.
Slope is signed percent: 2 means +2%, -2 means -2%, 0 means level.
Uses world XY plan distance, regardless of the current construction plane.
"""

import math

import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc


def elevation_from_percent(target, reference, percent, tolerance):
    """Return reference Z + signed rise/run * horizontal distance."""
    if not target.IsValid or not reference.IsValid:
        raise ValueError("Both points must have finite coordinates.")
    if math.isnan(percent) or math.isinf(percent):
        raise ValueError("Slope must be a finite percentage.")
    run = math.hypot(target.X - reference.X, target.Y - reference.Y)
    if run <= tolerance:
        raise ValueError("The points need different world XY positions to define a slope.")
    elevation = reference.Z + run * (percent / 100.0)
    if math.isnan(elevation) or math.isinf(elevation):
        raise ValueError("The calculated elevation is outside the supported range.")
    return elevation


def move_control_point_by_slope():
    curve_id = rs.GetObject("Select curve to edit", rs.filter.curve,
                            preselect=True, select=False)
    if curve_id is None:
        return

    grips_were_on = rs.ObjectGripsOn(curve_id)
    try:
        if not grips_were_on:
            rs.EnableObjectGrips(curve_id, True)
        # Some Rhino versions return None from EnableObjectGrips even on success.
        if not rs.ObjectGripsOn(curve_id):
            print("This curve does not support control point editing.")
            return
        sc.doc.Views.Redraw()

        while True:
            grip = rs.GetObjectGrip("Pick the control point whose elevation will change",
                                    preselect=False, select=False)
            if grip is None:
                return
            if grip[0] == curve_id:
                break
            print("Pick a control point on the curve you selected.")

        index = grip[1]
        target = rs.ObjectGripLocation(curve_id, index)
        reference = rs.GetPoint(
            "Reference point (use Point object snap for another control point)")
        if reference is None:
            return

        percent = rs.GetReal(
            "Slope in percent from reference to control point (+ above, - below)", 2.0)
        if percent is None:
            return
        try:
            elevation = elevation_from_percent(
                target, reference, percent, sc.doc.ModelAbsoluteTolerance)
        except ValueError as error:
            print(str(error))
            return

        destination = Rhino.Geometry.Point3d(target.X, target.Y, elevation)
        # Delay the only geometry edit until every input has been accepted.
        undo = sc.doc.BeginUndoRecord("Move control point by slope")
        try:
            previous = rs.ObjectGripLocation(curve_id, index, destination)
            if previous is None:
                print("Rhino could not update this control point.")
                return
        finally:
            if undo:
                sc.doc.EndUndoRecord(undo)
        print("Control point {0}: Z {1:.6g} -> {2:.6g} at {3:.6g}%".format(
            index, target.Z, elevation, percent))
    finally:
        # Restore the curve's original points-on state, including after Esc.
        if not grips_were_on and rs.IsObject(curve_id):
            rs.EnableObjectGrips(curve_id, False)
        sc.doc.Views.Redraw()


if __name__ == "__main__":
    move_control_point_by_slope()
