namespace MoleHill.Core.Grading;

/// <summary>The part of a <see cref="CutSegment"/> that lies inside one terrain face.</summary>
internal readonly record struct SegmentPiece(Point2D Start, Point2D End);
