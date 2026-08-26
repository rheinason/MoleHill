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
        IEnumerable<string>? diagnostics = null)
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
    }

    public Mesh Mesh { get; }

    public IReadOnlyList<Curve> Breaklines { get; }

    public IReadOnlyList<MoleHillTerrainRegion> Regions { get; }

    public string Name { get; }

    public string Key { get; }

    public long Revision { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public bool IsValid => Mesh.IsValid && Mesh.Vertices.Count >= 3 && Mesh.Faces.Count > 0;

    public MoleHillTerrainData Duplicate()
    {
        return new MoleHillTerrainData(Mesh, Breaklines, Regions, Name, Key, Revision, Diagnostics);
    }
}
