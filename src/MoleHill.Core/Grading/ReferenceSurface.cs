// The original surface a volume is measured against: its triangles, their planes, and a plan grid over them.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// A reference (original) surface prepared for <see cref="SurfaceDifferenceVolume"/>: each triangle's plane,
/// and a dense plan grid of the triangles whose footprint has area, in CSR form (no hashing, so no
/// collapsing cell keys). Triangles standing on edge cover no plan area and are left out: the faces either
/// side of a vertical wall already cover the ground beneath it. Built once per reference and shared.
/// </summary>
public sealed class ReferenceSurface
{
    private readonly double[] _vertices;
    private readonly int[] _faces;
    private readonly double[] _planes;
    private readonly int[] _cellStart;
    private readonly int[] _cellFaces;
    private readonly int[] _faceMinCell;
    private readonly double _minX, _minY, _invCell;
    private readonly int _cellsX, _cellsY;
    private readonly Lazy<MeshHeightProjector> _projector;

    public ReferenceSurface(IndexedTriMesh mesh)
        : this(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount)
    {
    }

    public ReferenceSurface(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        _vertices = vertices;
        _faces = faces;
        FaceCount = faceCount;
        _planes = new double[faceCount * 3];
        _faceMinCell = new int[faceCount * 2];
        _projector = new Lazy<MeshHeightProjector>(
            () => new MeshHeightProjector(vertices, vertexCount, faces, faceCount),
            LazyThreadSafetyMode.ExecutionAndPublication);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int face = 0; face < faceCount; face++)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = faces[face * 3 + k];
                minX = Math.Min(minX, vertices[v * 3]);
                maxX = Math.Max(maxX, vertices[v * 3]);
                minY = Math.Min(minY, vertices[v * 3 + 1]);
                maxY = Math.Max(maxY, vertices[v * 3 + 1]);
            }
        }

        if (faceCount == 0 || !double.IsFinite(minX))
        {
            _minX = _minY = 0.0;
            _invCell = 1.0;
            _cellsX = _cellsY = 1;
            _cellStart = new int[2];
            _cellFaces = Array.Empty<int>();
            return;
        }

        double spanX = Math.Max(maxX - minX, 1e-9), spanY = Math.Max(maxY - minY, 1e-9);
        Scale = Math.Max(spanX, spanY);
        // About one triangle per cell, capped so a sliver-shaped extent cannot allocate a huge grid.
        double cell = Math.Sqrt(spanX * spanY / Math.Max(1, faceCount));
        cell = Math.Max(cell, Math.Max(spanX, spanY) / 4096.0);
        _minX = minX;
        _minY = minY;
        _invCell = 1.0 / cell;
        _cellsX = Math.Max(1, (int)Math.Ceiling(spanX * _invCell));
        _cellsY = Math.Max(1, (int)Math.Ceiling(spanY * _invCell));

        double degenerateArea = Scale * Scale * 1e-18;
        var counts = new int[_cellsX * _cellsY + 1];
        var range = new int[faceCount * 4];
        for (int face = 0; face < faceCount; face++)
        {
            int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
            double ax = vertices[a * 3], ay = vertices[a * 3 + 1], az = vertices[a * 3 + 2];
            double bx = vertices[b * 3] - ax, by = vertices[b * 3 + 1] - ay, bz = vertices[b * 3 + 2] - az;
            double cx = vertices[c * 3] - ax, cy = vertices[c * 3 + 1] - ay, cz = vertices[c * 3 + 2] - az;
            double det = bx * cy - cx * by;
            if (Math.Abs(det) * 0.5 <= degenerateArea || !double.IsFinite(det))
            {
                _planes[face * 3] = double.NaN;
                range[face * 4] = -1;
                continue;
            }

            // z = az + gx (x - ax) + gy (y - ay): kept relative to a corner so large coordinates do not cancel.
            _planes[face * 3] = (bz * cy - cz * by) / det;
            _planes[face * 3 + 1] = (bx * cz - cx * bz) / det;
            _planes[face * 3 + 2] = det;
            CellRange(Math.Min(ax, Math.Min(ax + bx, ax + cx)), Math.Min(ay, Math.Min(ay + by, ay + cy)),
                Math.Max(ax, Math.Max(ax + bx, ax + cx)), Math.Max(ay, Math.Max(ay + by, ay + cy)),
                out int x0, out int y0, out int x1, out int y1);
            range[face * 4] = x0; range[face * 4 + 1] = y0; range[face * 4 + 2] = x1; range[face * 4 + 3] = y1;
            _faceMinCell[face * 2] = x0;
            _faceMinCell[face * 2 + 1] = y0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    counts[y * _cellsX + x + 1]++;
        }

        for (int i = 1; i < counts.Length; i++)
            counts[i] += counts[i - 1];
        _cellStart = (int[])counts.Clone();
        _cellFaces = new int[counts[^1]];
        for (int face = 0; face < faceCount; face++)
        {
            if (range[face * 4] < 0)
                continue;
            for (int y = range[face * 4 + 1]; y <= range[face * 4 + 3]; y++)
                for (int x = range[face * 4]; x <= range[face * 4 + 2]; x++)
                    _cellFaces[counts[y * _cellsX + x]++] = face;
        }
    }

    public int FaceCount { get; }

    /// <summary>The larger plan extent of the reference; sets the scale of "no area".</summary>
    public double Scale { get; } = 1.0;

    internal MeshHeightProjector Projector => _projector.Value;

    internal double[] Vertices => _vertices;

    internal bool HasArea(int face) => !double.IsNaN(_planes[face * 3]);

    internal void Corner(int face, int k, out double x, out double y, out double z)
    {
        int v = _faces[face * 3 + k];
        x = _vertices[v * 3];
        y = _vertices[v * 3 + 1];
        z = _vertices[v * 3 + 2];
    }

    /// <summary>Signed twice-area of the face in plan (positive when its corners run anticlockwise).</summary>
    internal double SignedDoubleArea(int face) => _planes[face * 3 + 2];

    internal double HeightAt(int face, double x, double y)
    {
        int v = _faces[face * 3];
        return _vertices[v * 3 + 2] + _planes[face * 3] * (x - _vertices[v * 3]) + _planes[face * 3 + 1] * (y - _vertices[v * 3 + 1]);
    }

    /// <summary>
    /// Fills <paramref name="into"/> with every face with area whose grid cells meet the box, each once, in
    /// ascending cell order: a face spanning several cells is reported only from the first cell it shares
    /// with the box.
    /// </summary>
    internal void CollectCandidates(double minX, double minY, double maxX, double maxY, List<int> into)
    {
        into.Clear();
        if (_cellFaces.Length == 0)
            return;
        CellRange(minX, minY, maxX, maxY, out int x0, out int y0, out int x1, out int y1);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * _cellsX + x;
                for (int i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                {
                    int face = _cellFaces[i];
                    if (Math.Max(x0, _faceMinCell[face * 2]) == x && Math.Max(y0, _faceMinCell[face * 2 + 1]) == y)
                        into.Add(face);
                }
            }
        }
    }

    private void CellRange(double minX, double minY, double maxX, double maxY, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Clamp((int)Math.Floor((minX - _minX) * _invCell), _cellsX);
        y0 = Clamp((int)Math.Floor((minY - _minY) * _invCell), _cellsY);
        x1 = Clamp((int)Math.Floor((maxX - _minX) * _invCell), _cellsX);
        y1 = Clamp((int)Math.Floor((maxY - _minY) * _invCell), _cellsY);
    }

    private static int Clamp(int value, int count) => value < 0 ? 0 : value >= count ? count - 1 : value;
}
