using Rhino.Geometry;

namespace MoleHill.Interop;

public sealed class TerrainInteropSnapshot : IDisposable
{
    public required Mesh Mesh { get; init; }
    public string Name { get; init; } = "Terrain";
    public string Key { get; init; } = string.Empty;
    public long Revision { get; init; }
    public string Fingerprint { get; set; } = string.Empty;
    public IReadOnlyList<Curve> Breaklines { get; init; } = Array.Empty<Curve>();
    public IReadOnlyList<TerrainInteropRegion> Regions { get; init; } = Array.Empty<TerrainInteropRegion>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public string UnitSystem { get; init; } = "Unspecified";
    public double MetersPerModelUnit { get; init; } = 1.0;
    public Transform LocalToWorld { get; init; } = Transform.Identity;
    public bool HasProjectBaseTransform { get; init; }

    public void Dispose()
    {
        Mesh.Dispose();
        foreach (Curve breakline in Breaklines)
            breakline.Dispose();
        foreach (TerrainInteropRegion region in Regions)
            region.Dispose();
    }
}
