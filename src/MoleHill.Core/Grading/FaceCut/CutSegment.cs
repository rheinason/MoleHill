namespace MoleHill.Core.Grading;

/// <summary>
/// One straight segment of the network being cut into the terrain (a constraint or an area boundary),
/// before it is clipped to individual faces.
/// </summary>
internal readonly record struct CutSegment(Point2D Start, Point2D End);
