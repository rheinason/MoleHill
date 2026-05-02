namespace MoleHill.Core.Engine;

/// <summary>
/// Controls iterative removal of unwanted TIN boundary triangles.
/// </summary>
public sealed class BoundaryTrianglePeelSettings : IEquatable<BoundaryTrianglePeelSettings>
{
    public const double DefaultMaxInteriorAngleDegrees = 170.0;

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum boundary edge length. Zero means auto, negative disables peeling for legacy callers.
    /// </summary>
    public double MaxBoundaryEdgeLength { get; init; } = 0.0;

    /// <summary>
    /// Maximum allowed interior triangle angle in degrees. Zero or negative disables the angle test.
    /// </summary>
    public double MaxInteriorAngleDegrees { get; init; } = DefaultMaxInteriorAngleDegrees;

    /// <summary>
    /// Maximum allowed face slope from horizontal in degrees. Zero or negative disables the slope test.
    /// </summary>
    public double MaxSlopeAngleDegrees { get; init; } = 0.0;

    public static BoundaryTrianglePeelSettings Default { get; } = new();

    public static BoundaryTrianglePeelSettings Disabled { get; } = new()
    {
        Enabled = false,
        MaxBoundaryEdgeLength = -1.0
    };

    public static BoundaryTrianglePeelSettings FromLegacyMaxBoundaryEdgeLength(double maxBoundaryEdgeLength)
    {
        return maxBoundaryEdgeLength < 0.0
            ? Disabled
            : new BoundaryTrianglePeelSettings { MaxBoundaryEdgeLength = maxBoundaryEdgeLength };
    }

    public bool UsesSlopeCriterion => Enabled && MaxSlopeAngleDegrees > 0.0;

    public bool Equals(BoundaryTrianglePeelSettings? other)
    {
        return other != null &&
               Enabled == other.Enabled &&
               MaxBoundaryEdgeLength == other.MaxBoundaryEdgeLength &&
               MaxInteriorAngleDegrees == other.MaxInteriorAngleDegrees &&
               MaxSlopeAngleDegrees == other.MaxSlopeAngleDegrees;
    }

    public override bool Equals(object? obj) => Equals(obj as BoundaryTrianglePeelSettings);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Enabled,
            MaxBoundaryEdgeLength,
            MaxInteriorAngleDegrees,
            MaxSlopeAngleDegrees);
    }
}
