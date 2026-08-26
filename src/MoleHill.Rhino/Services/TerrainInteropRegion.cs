// Public reflection DTO for one named MoleHill terrain region.
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

public sealed class TerrainInteropRegion : IDisposable
{
    public string Name { get; init; } = "Zone";

    public string Key { get; init; } = string.Empty;

    public IReadOnlyList<Curve> Boundaries { get; init; } = Array.Empty<Curve>();

    public void Dispose()
    {
        foreach (Curve boundary in Boundaries)
            boundary.Dispose();
    }
}
