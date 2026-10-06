namespace MoleHill.Core.Grading;

/// <summary>Everything one terrain face must be re-triangulated against.</summary>
internal sealed class FaceCutData
{
    public List<SegmentPiece> InternalSegments { get; } = new();
    public List<EdgePoint> EdgePoints { get; } = new();

    /// <summary>Isolated points strictly inside the face, inserted as free vertices.</summary>
    public List<Point2D> InteriorPoints { get; } = new();

    public bool HasData => InternalSegments.Count > 0 || EdgePoints.Count > 0 || InteriorPoints.Count > 0;
}
