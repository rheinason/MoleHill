using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class ResolvedSourceObject
{
    public Guid ObjectId { get; init; }

    public string? LayerPath { get; init; }

    /// <summary>Block definition name when this object is a block instance; null otherwise.</summary>
    public string? InstanceDefinitionName { get; init; }

    public required GeometryBase Geometry { get; init; }

    public BoundingBox LocalBoundingBox { get; init; }

    public BoundingBox WorldBoundingBox { get; init; }

    public Transform SourceTransform { get; init; } = Transform.Identity;

    public bool HasSourceTransform { get; init; }

    public uint GeometryDataCrc { get; init; }
}
