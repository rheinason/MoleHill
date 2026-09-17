using Rhino.Geometry;

namespace MoleHill.Interop;

public sealed class TerrainInteropRegion : IDisposable
{
    public string Name { get; init; } = "Zone";
    public string Key { get; init; } = string.Empty;
    public IReadOnlyList<Curve> Boundaries { get; init; } = Array.Empty<Curve>();
    public int StackIndex { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool UseInputElevationForPriority { get; init; } = true;
    public int ColorArgb { get; init; } = unchecked((int)0xFF78B464);
    public bool UseColorOverride { get; init; }
    public string? LayerName { get; init; }
    public string? MaterialName { get; init; }
    public bool SplitToSeparateMesh { get; init; } = true;

    public void Dispose()
    {
        foreach (Curve boundary in Boundaries)
            boundary.Dispose();
    }
}
