// One frame of mhEditProfile's preview: everything the conduit draws, computed by the session after each change.
using MoleHill.Core.Analysis;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// An immutable picture of the edit session for <see cref="ProfileEditConduit"/>. The session rebuilds it
/// after every change (a drag step, an option, an undo) and hands it over whole, so the conduit never reads
/// the editor while the user is changing it.
/// </summary>
internal sealed class ProfileEditView
{
    /// <summary>Where the section is drawn: station 0 at the datum, exaggerated elevation up the Y axis.</summary>
    public required LongSectionFrame Frame { get; init; }

    /// <summary>The height of the plane the section is drawn in.</summary>
    public required double PlaneZ { get; init; }

    public required double Length { get; init; }

    public required ProfileBend[] Bends { get; init; }

    /// <summary>The profile the session opened with, drawn faintly behind the edit.</summary>
    public required ProfileBend[] Initial { get; init; }

    /// <summary>Per segment: steeper than the maximum grade rule.</summary>
    public required bool[] SegmentTooSteep { get; init; }

    /// <summary>Per bend: a change of grade above the vertical break rule.</summary>
    public required bool[] BendBreak { get; init; }

    /// <summary>Per segment: the grade in the user's slope unit.</summary>
    public required string[] SegmentGrades { get; init; }

    /// <summary>Per bend: its elevation.</summary>
    public required string[] BendElevations { get; init; }

    public required IReadOnlyList<double[]> Ground { get; init; }

    /// <summary>Horizontal grid lines: every <see cref="GridStep"/> from <see cref="GridLow"/> to <see cref="GridHigh"/>.</summary>
    public required double GridStep { get; init; }

    public required double GridLow { get; init; }

    public required double GridHigh { get; init; }

    public required string[] GridLabels { get; init; }

    /// <summary>Station ticks every <see cref="StationStep"/>.</summary>
    public required double StationStep { get; init; }

    public required Func<double, string> FormatStation { get; init; }

    /// <summary>The curve as it will be written, in plan/3D, over the document curve.</summary>
    public Polyline? Preview { get; init; }

    /// <summary>The point on the plan curve at the station under the cursor.</summary>
    public Point3d? PlanMarker { get; init; }

    /// <summary>The station under the cursor or being dragged, marked in the section.</summary>
    public double? CursorStation { get; init; }

    public int HotBend { get; init; } = -1;

    public int SelectedBend { get; init; } = -1;

    public int HotSegment { get; init; } = -1;

    /// <summary>Lines of text shown beside the cursor while dragging.</summary>
    public string[]? Readout { get; init; }

    public Point3d ReadoutAt { get; init; }

    public Point3d ToWorld(double station, double elevation)
    {
        (double x, double y) = Frame.ToDrawing(station, elevation);
        return new Point3d(x, y, PlaneZ);
    }

    /// <summary>The section's extent in the drawing plane, for the conduit's bounding box and for zooming.</summary>
    public BoundingBox DrawingBounds()
    {
        var box = new BoundingBox(ToWorld(0.0, GridLow), ToWorld(Length, GridHigh));
        foreach (ProfileBend bend in Bends)
            box.Union(ToWorld(bend.Station, bend.Elevation));
        return box;
    }
}
