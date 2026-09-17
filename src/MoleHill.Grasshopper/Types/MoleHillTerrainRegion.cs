// Named region metadata carried by an open MoleHill Grasshopper terrain.
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Types;

public sealed class MoleHillTerrainRegion
{
    public MoleHillTerrainRegion(string name, string key, IEnumerable<Curve> boundaries)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Zone" : name;
        Key = key ?? string.Empty;
        Boundaries = boundaries
            .Where(boundary => boundary != null)
            .Select(boundary => boundary.DuplicateCurve())
            .ToArray();
    }

    public string Name { get; }

    public string Key { get; }

    public IReadOnlyList<Curve> Boundaries { get; }

    public int StackIndex { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool UseInputElevationForPriority { get; init; } = true;
    public int ColorArgb { get; init; } = unchecked((int)0xFF78B464);
    public bool UseColorOverride { get; init; }
    public string? LayerName { get; init; }
    public string? MaterialName { get; init; }
    public bool SplitToSeparateMesh { get; init; } = true;

    public MoleHillTerrainRegion Duplicate()
    {
        return new MoleHillTerrainRegion(Name, Key, Boundaries)
        {
            StackIndex = StackIndex,
            IsEnabled = IsEnabled,
            UseInputElevationForPriority = UseInputElevationForPriority,
            ColorArgb = ColorArgb,
            UseColorOverride = UseColorOverride,
            LayerName = LayerName,
            MaterialName = MaterialName,
            SplitToSeparateMesh = SplitToSeparateMesh
        };
    }
}
