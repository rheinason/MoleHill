namespace MoleHill.Core.Analysis;

/// <summary>One contour crossing of one triangle: the index of its level and its two end points.</summary>
public readonly record struct ContourSegment(int Level, double Ax, double Ay, double Az, double Bx, double By, double Bz);
