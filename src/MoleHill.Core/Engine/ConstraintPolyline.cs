namespace MoleHill.Core.Engine;

/// <summary>
/// Shared constraint polyline (flat XYZ points) used by remeshing, grading and the hosts.
/// </summary>
public readonly record struct ConstraintPolyline(double[] Points, int PointCount, bool IsClosed, bool PreserveInputElevation = false);
