namespace MoleHill.Core.Analysis;

/// <summary>All contour polylines traced at one elevation.</summary>
public sealed class ContourLevel
{
    public required double Z { get; init; }

    public required IReadOnlyList<ContourPolyline> Polylines { get; init; }
}
