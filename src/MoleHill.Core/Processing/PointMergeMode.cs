namespace MoleHill.Core.Processing;

/// <summary>How selected point locations are collapsed without removing their identities.</summary>
public enum PointMergeMode
{
    ByDistance,
    AtCenter,
    First,
    Last
}
