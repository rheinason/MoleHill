// Open terrain payload shared by snapshot, construct/deconstruct, and partition components.
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Types;

public sealed class MoleHillTerrainData
{
    public MoleHillTerrainData(
        Mesh mesh,
        IEnumerable<Curve>? breaklines = null,
        IEnumerable<MoleHillTerrainRegion>? regions = null,
        string? name = null,
        string? key = null,
        long revision = 0,
        IEnumerable<string>? diagnostics = null,
        string? unitSystem = null,
        double metersPerModelUnit = 1.0,
        Transform? localToWorld = null,
        bool hasProjectBaseTransform = false)
    {
        Mesh = mesh?.DuplicateMesh() ?? throw new ArgumentNullException(nameof(mesh));
        Breaklines = (breaklines ?? Array.Empty<Curve>())
            .Where(curve => curve != null)
            .Select(curve => curve.DuplicateCurve())
            .ToArray();
        Regions = (regions ?? Array.Empty<MoleHillTerrainRegion>())
            .Where(region => region != null)
            .Select(region => region.Duplicate())
            .ToArray();
        Name = string.IsNullOrWhiteSpace(name) ? "Terrain" : name;
        Key = key ?? string.Empty;
        Revision = revision;
        Diagnostics = (diagnostics ?? Array.Empty<string>())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
        UnitSystem = string.IsNullOrWhiteSpace(unitSystem) ? "Unspecified" : unitSystem;
        MetersPerModelUnit = double.IsFinite(metersPerModelUnit) && metersPerModelUnit > 0.0
            ? metersPerModelUnit
            : 1.0;
        LocalToWorld = localToWorld is { IsValid: true } transform ? transform : Transform.Identity;
        HasProjectBaseTransform = hasProjectBaseTransform;
    }

    public Mesh Mesh { get; }

    public IReadOnlyList<Curve> Breaklines { get; }

    public IReadOnlyList<MoleHillTerrainRegion> Regions { get; }

    public string Name { get; }

    public string Key { get; }

    public long Revision { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public string UnitSystem { get; }

    public double MetersPerModelUnit { get; }

    /// <summary>Rigid MoleHill project-local to real-world transform, in Rhino model units.</summary>
    public Transform LocalToWorld { get; }

    public bool HasProjectBaseTransform { get; }

    public bool IsValid => Mesh.IsValid && Mesh.Vertices.Count >= 3 && Mesh.Faces.Count > 0;

    public MoleHillTerrainData Duplicate()
    {
        return new MoleHillTerrainData(
            Mesh,
            Breaklines,
            Regions,
            Name,
            Key,
            Revision,
            Diagnostics,
            UnitSystem,
            MetersPerModelUnit,
            LocalToWorld,
            HasProjectBaseTransform);
    }
}
