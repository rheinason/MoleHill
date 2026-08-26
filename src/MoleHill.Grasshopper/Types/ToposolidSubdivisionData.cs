// One stable named subdivision profile set carried by a Toposolid preparation package.
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Types;

public sealed class ToposolidSubdivisionData
{
    public ToposolidSubdivisionData(
        string name,
        string key,
        IEnumerable<Curve> profiles,
        string? geometryFingerprint = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Subdivision" : name;
        Key = key ?? string.Empty;
        Profiles = profiles.Select(profile => profile.DuplicateCurve()).ToArray();
        GeometryFingerprint = geometryFingerprint ?? string.Empty;
    }

    public string Name { get; }

    public string Key { get; }

    public IReadOnlyList<Curve> Profiles { get; }

    public string GeometryFingerprint { get; }

    public ToposolidSubdivisionData Duplicate() => new(Name, Key, Profiles, GeometryFingerprint);
}
