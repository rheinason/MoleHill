// Public reflection DTO for a completed final MoleHill terrain build.
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

public sealed class TerrainInteropSnapshot
{
    public required Mesh Mesh { get; init; }

    public string Name { get; init; } = "Terrain";

    public string Key { get; init; } = string.Empty;

    public long Revision { get; init; }

    public IReadOnlyList<Curve> Breaklines { get; init; } = Array.Empty<Curve>();

    public IReadOnlyList<TerrainInteropRegion> Regions { get; init; } = Array.Empty<TerrainInteropRegion>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
