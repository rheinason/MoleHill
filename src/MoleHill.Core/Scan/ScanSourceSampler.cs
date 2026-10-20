using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>Indexed original observations for feature profiles; never interpolates cloud gaps.</summary>
internal sealed class ScanSourceSampler
{
    private readonly MeshHeightProjector? _mesh;
    private readonly ScanPointCloud? _cloud;
    private readonly Dictionary<(int X, int Y), List<int>> _bins = new();
    private readonly double _cell;
    public ScanSourceSampler(IndexedTriMesh mesh) => _mesh = new(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
    public ScanSourceSampler(ScanPointCloud cloud, double cell)
    {
        _cloud = cloud; _cell = cell;
        for (int i = 0; i < cloud.Count; i++)
        {
            var key = ((int)Math.Floor(cloud.Xyz[i * 3] / cell), (int)Math.Floor(cloud.Xyz[i * 3 + 1] / cell));
            if (!_bins.TryGetValue(key, out var list)) _bins[key] = list = new();
            list.Add(i);
        }
    }
    public double? Height(double x, double y)
    {
        if (_mesh != null) return _mesh.TryGetHeightExtremes(x, y, _mesh.BoundsDiagonal * 1e-10, out _, out double z) ? z : null;
        int cx = (int)Math.Floor(x / _cell), cy = (int)Math.Floor(y / _cell);
        double best = _cell * _cell * .25, height = double.NaN;
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            if (_bins.TryGetValue((cx + dx, cy + dy), out var list)) foreach (int i in list)
            {
                if (_cloud!.Classes?[i] is 3 or 4 or 5 or 6) continue;
                double distance = Math.Pow(_cloud.Xyz[i * 3] - x, 2) + Math.Pow(_cloud.Xyz[i * 3 + 1] - y, 2);
                if (distance < best) { best = distance; height = _cloud.Xyz[i * 3 + 2]; }
            }
        return double.IsFinite(height) ? height : null;
    }
}
