using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class GeneratedRhinoObject
{
    public GeometryBase? Geometry { get; init; }

    public required string Name { get; init; }

    public int? ColorArgb { get; init; }

    public string? LayerPath { get; init; }

    public string? SourceLayerPath { get; init; }

    public string? MaterialName { get; init; }

    public string? InstanceDefinitionName { get; init; }

    public MarkerBlockTemplate MarkerBlockTemplate { get; init; }

    public Transform InstanceTransform { get; init; } = Transform.Identity;
}
