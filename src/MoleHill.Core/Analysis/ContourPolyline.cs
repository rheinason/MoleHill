namespace MoleHill.Core.Analysis;

/// <summary>A single traced contour polyline at one level: flat XYZ points and whether it closes.</summary>
public sealed class ContourPolyline
{
    public required double[] PointsXyz { get; init; }

    public required bool IsClosed { get; init; }

    public int PointCount => PointsXyz.Length / 3;
}
