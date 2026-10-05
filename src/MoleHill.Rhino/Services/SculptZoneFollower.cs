using MoleHill.Core.Sculpting;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Keeps the zone meshes on the sculpted terrain while a sculpt session runs. Zones are build output,
/// cut from the terrain by the zone stage, so without this they only caught up in the rebuild after
/// mouse-up and the terrain moved through them for the whole stroke.
///
/// Sculpting changes Z only, so every zone vertex lies on one fixed terrain face for the session. Binding
/// locates each vertex once (face + barycentric weights, via the session's <see cref="SculptRayCaster"/>);
/// each dab then re-interpolates only the zone vertices inside the brush and patches their normals.
/// The zones are drawn from working copies: the meshes in the display state are also the zone stage's
/// cached output, and editing them in place would hand a later cache hit (an undo, say) sculpted zones.
///
/// Binding is done once per session (~140 ms of locating on a 370k-vertex zone set, so never per
/// stroke). Every post-stroke rebuild replaces the zone meshes on screen, but sculpting cannot change a
/// zone's plan topology and the copies already carry the live heights, so <see cref="Rebind"/> only
/// re-keys a copy to its replacement when the counts agree, and binds afresh only when they do not.
/// </summary>
internal sealed class SculptZoneFollower
{
    private const double TargetVerticesPerCell = 4.0;

    private sealed class ZoneCopy
    {
        public required RhinoMesh Source { get; set; }
        public required RhinoMesh Working { get; init; }
        public required double[] Vertices { get; init; }
        public required SculptNormalPatcher Normals { get; init; }
        public required int[] Face { get; init; }
        public required double[] Weights { get; init; }
        public List<int> Moved { get; } = new();
    }

    private readonly Dictionary<RhinoMesh, ZoneCopy> _bySource = new(ReferenceEqualityComparer.Instance);
    private ZoneCopy[] _copies = Array.Empty<ZoneCopy>();
    private RhinoMesh[] _boundSources = Array.Empty<RhinoMesh>();

    // Uniform XY grid over every zone vertex, CSR: entries of cell c are [_cellOffsets[c], _cellOffsets[c+1]).
    private double _minX, _minY, _cellSize = 1.0;
    private int _columns, _rows;
    private int[] _cellOffsets = new int[1];
    private int[] _entryZone = Array.Empty<int>();
    private int[] _entryVertex = Array.Empty<int>();

    /// <summary>The working copy drawn in place of <paramref name="source"/>, if it is a bound zone.</summary>
    public bool TryResolve(RhinoMesh source, out RhinoMesh working)
    {
        if (_bySource.TryGetValue(source, out ZoneCopy? copy))
        {
            working = copy.Working;
            return true;
        }

        working = source;
        return false;
    }

    public bool IsBound => _copies.Length > 0;

    /// <summary>
    /// Follows the zone meshes now on screen. A replacement with the same vertex and face counts as the
    /// copy it replaces is the same zone rebuilt, so the copy is simply re-keyed to it; anything else
    /// (a zone added, removed or reshaped) binds afresh.
    /// </summary>
    public void Rebind(IReadOnlyList<GeneratedRhinoObject> zones, SculptRayCaster caster, double[] terrainVertices)
    {
        RhinoMesh[] sources = EligibleSources(zones);
        bool sameShape = sources.Length == _copies.Length;
        for (int i = 0; sameShape && i < sources.Length; i++)
        {
            sameShape = ReferenceEquals(sources[i], _copies[i].Source) ||
                        (sources[i].Vertices.Count == _copies[i].Working.Vertices.Count &&
                         sources[i].Faces.Count == _copies[i].Working.Faces.Count);
        }

        if (!sameShape)
        {
            Bind(zones, caster, terrainVertices);
            return;
        }

        for (int i = 0; i < sources.Length; i++)
        {
            ZoneCopy copy = _copies[i];
            if (ReferenceEquals(sources[i], copy.Source))
                continue;

            _bySource.Remove(copy.Source);
            copy.Source = sources[i];
            _bySource[sources[i]] = copy;
        }

        _boundSources = sources;
    }

    private static RhinoMesh[] EligibleSources(IReadOnlyList<GeneratedRhinoObject> zones) => zones
        .Select(zone => zone.Geometry as RhinoMesh)
        .Where(mesh => mesh != null && mesh.Faces.QuadCount == 0 && mesh.Faces.Count > 0)
        .Cast<RhinoMesh>()
        .ToArray();

    /// <summary>Binds to the zone meshes currently displayed: copies them and locates every vertex.</summary>
    public void Bind(IReadOnlyList<GeneratedRhinoObject> zones, SculptRayCaster caster, double[] terrainVertices)
    {
        RhinoMesh[] sources = EligibleSources(zones);
        Clear();
        _boundSources = sources;
        var copies = new List<ZoneCopy>(sources.Length);
        foreach (RhinoMesh source in sources)
        {
            RhinoMesh working = source.DuplicateMesh();
            double[] vertices = ToFlatArray(working);
            int vertexCount = working.Vertices.Count;
            int[] faces = working.Faces.ToIntArray(true);
            if (working.Normals.Count != vertexCount)
                working.Normals.ComputeNormals();

            var face = new int[vertexCount];
            var weights = new double[vertexCount * 3];
            // TryLocate only reads the caster, so the ~370 ns per vertex parallelises cleanly.
            Parallel.For(0, vertexCount, v =>
            {
                face[v] = caster.TryLocate(
                    terrainVertices, vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3 + 2],
                    out int located, out double w0, out double w1, out double w2)
                    ? located
                    : -1;
                weights[v * 3] = w0;
                weights[v * 3 + 1] = w1;
                weights[v * 3 + 2] = w2;
            });

            var copy = new ZoneCopy
            {
                Source = source,
                Working = working,
                Vertices = vertices,
                Normals = new SculptNormalPatcher(vertices, vertexCount, faces, faces.Length / 3),
                Face = face,
                Weights = weights
            };
            copies.Add(copy);
            _bySource[source] = copy;
        }

        _copies = copies.ToArray();
        BuildGrid();
    }

    /// <summary>
    /// Re-seats every bound zone vertex inside the XY bounds on the terrain's current
    /// <paramref name="terrainVertices"/>, and patches the normals around the ones that moved.
    /// </summary>
    public void Follow(double[] terrainVertices, int[] terrainFaces, double minX, double maxX, double minY, double maxY)
    {
        if (_copies.Length == 0 || _columns == 0)
            return;

        int c0 = Math.Clamp((int)Math.Floor((minX - _minX) / _cellSize), 0, _columns - 1);
        int c1 = Math.Clamp((int)Math.Floor((maxX - _minX) / _cellSize), 0, _columns - 1);
        int r0 = Math.Clamp((int)Math.Floor((minY - _minY) / _cellSize), 0, _rows - 1);
        int r1 = Math.Clamp((int)Math.Floor((maxY - _minY) / _cellSize), 0, _rows - 1);

        for (int row = r0; row <= r1; row++)
        {
            for (int col = c0; col <= c1; col++)
            {
                int cell = row * _columns + col;
                for (int k = _cellOffsets[cell]; k < _cellOffsets[cell + 1]; k++)
                {
                    ZoneCopy copy = _copies[_entryZone[k]];
                    int v = _entryVertex[k];
                    int f = copy.Face[v];
                    if (f < 0)
                        continue;

                    double x = copy.Vertices[v * 3], y = copy.Vertices[v * 3 + 1];
                    if (x < minX || x > maxX || y < minY || y > maxY)
                        continue;

                    double z =
                        copy.Weights[v * 3] * terrainVertices[terrainFaces[f * 3] * 3 + 2] +
                        copy.Weights[v * 3 + 1] * terrainVertices[terrainFaces[f * 3 + 1] * 3 + 2] +
                        copy.Weights[v * 3 + 2] * terrainVertices[terrainFaces[f * 3 + 2] * 3 + 2];
                    if (z == copy.Vertices[v * 3 + 2])
                        continue;

                    copy.Vertices[v * 3 + 2] = z;
                    copy.Working.Vertices.SetVertex(v, x, y, z);
                    copy.Moved.Add(v);
                }
            }
        }

        foreach (ZoneCopy copy in _copies)
        {
            if (copy.Moved.Count == 0)
                continue;

            copy.Normals.PatchNormals(copy.Working, copy.Vertices, copy.Moved);
            copy.Moved.Clear();
        }
    }

    public void Clear()
    {
        foreach (ZoneCopy copy in _copies)
            copy.Working.Dispose();

        _copies = Array.Empty<ZoneCopy>();
        _boundSources = Array.Empty<RhinoMesh>();
        _bySource.Clear();
        _columns = _rows = 0;
        _cellOffsets = new int[1];
        _entryZone = Array.Empty<int>();
        _entryVertex = Array.Empty<int>();
    }

    private static double[] ToFlatArray(RhinoMesh mesh)
    {
        int count = mesh.Vertices.Count;
        var flat = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            Point3d p = mesh.Vertices.Point3dAt(i);
            flat[i * 3] = p.X;
            flat[i * 3 + 1] = p.Y;
            flat[i * 3 + 2] = p.Z;
        }

        return flat;
    }

    private void BuildGrid()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int total = 0;
        foreach (ZoneCopy copy in _copies)
        {
            for (int i = 0; i < copy.Vertices.Length; i += 3)
            {
                minX = Math.Min(minX, copy.Vertices[i]);
                maxX = Math.Max(maxX, copy.Vertices[i]);
                minY = Math.Min(minY, copy.Vertices[i + 1]);
                maxY = Math.Max(maxY, copy.Vertices[i + 1]);
            }

            total += copy.Vertices.Length / 3;
        }

        if (total == 0)
            return;

        double width = Math.Max(maxX - minX, 1e-9), height = Math.Max(maxY - minY, 1e-9);
        _minX = minX;
        _minY = minY;
        _cellSize = Math.Max(Math.Sqrt(width * height * TargetVerticesPerCell / total), Math.Max(width, height) / 4096.0);
        _columns = Math.Max(1, (int)Math.Ceiling(width / _cellSize));
        _rows = Math.Max(1, (int)Math.Ceiling(height / _cellSize));

        var counts = new int[_columns * _rows + 1];
        var cells = new int[total];
        int n = 0;
        foreach (ZoneCopy copy in _copies)
        {
            for (int i = 0; i < copy.Vertices.Length; i += 3)
            {
                int cell = CellOf(copy.Vertices[i], copy.Vertices[i + 1]);
                cells[n++] = cell;
                counts[cell + 1]++;
            }
        }

        for (int c = 1; c < counts.Length; c++)
            counts[c] += counts[c - 1];

        _entryZone = new int[total];
        _entryVertex = new int[total];
        var cursors = (int[])counts.Clone();
        n = 0;
        for (int z = 0; z < _copies.Length; z++)
        {
            int vertexCount = _copies[z].Vertices.Length / 3;
            for (int v = 0; v < vertexCount; v++)
            {
                int slot = cursors[cells[n++]]++;
                _entryZone[slot] = z;
                _entryVertex[slot] = v;
            }
        }

        _cellOffsets = counts;
    }

    private int CellOf(double x, double y)
    {
        int col = Math.Clamp((int)Math.Floor((x - _minX) / _cellSize), 0, _columns - 1);
        int row = Math.Clamp((int)Math.Floor((y - _minY) / _cellSize), 0, _rows - 1);
        return row * _columns + col;
    }
}
