namespace MoleHill.Core.Grading;

/// <summary>
/// Where segment A meets segment B, as parameters along A (<see cref="T0"/>..<see cref="T1"/>) and the
/// matching points. A point intersection has <c>T0 == T1</c>; an overlap spans the shared stretch.
/// </summary>
internal readonly struct SegmentIntersection
{
    public required SegmentIntersectionKind Kind { get; init; }
    public required double T0 { get; init; }
    public required double T1 { get; init; }
    public required Point2D P0 { get; init; }
    public required Point2D P1 { get; init; }
}
