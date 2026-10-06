namespace MoleHill.Core.Grading;

/// <summary>A point where a cut meets edge <see cref="EdgeIndex"/> (0 = A-B, 1 = B-C, 2 = C-A) of a face.</summary>
internal readonly record struct EdgePoint(int EdgeIndex, Point2D Point);
