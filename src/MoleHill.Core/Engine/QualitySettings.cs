namespace MoleHill.Core.Engine;

/// <summary>
/// Mesh refinement parameters for Triangle.NET.
/// </summary>
public sealed class QualitySettings : IEquatable<QualitySettings>
{
    /// <summary>Maximum triangle area. Zero or negative means no constraint.</summary>
    public double MaxArea { get; init; }

    /// <summary>Minimum triangle angle in degrees. Zero or negative means no constraint.</summary>
    public double MinAngle { get; init; }

    public static QualitySettings None => new() { MaxArea = 0, MinAngle = 0 };

    public bool HasConstraints => MaxArea > 0 || MinAngle > 0;

    public bool Equals(QualitySettings? other)
    {
        if (other is null) return false;
        return MaxArea == other.MaxArea && MinAngle == other.MinAngle;
    }

    public override bool Equals(object? obj) => Equals(obj as QualitySettings);

    public override int GetHashCode() => HashCode.Combine(MaxArea, MinAngle);
}
