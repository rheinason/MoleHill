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

    public MoleHillTerrainRegion Duplicate()
    {
        return new MoleHillTerrainRegion(Name, Key, Boundaries);
    }
}
