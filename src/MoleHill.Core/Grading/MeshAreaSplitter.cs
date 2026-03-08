using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Splits a terrain mesh into separate meshes by closed boundary curves.
/// Re-triangulates with boundaries as constrained edges, then classifies
/// each face by which area its centroid falls inside.
/// </summary>
public static class MeshAreaSplitter
{
    private sealed class IndexedArea
    {
        public required AreaBoundary Boundary { get; init; }

        public required double MinX { get; init; }

        public required double MaxX { get; init; }

        public required double MinY { get; init; }

        public required double MaxY { get; init; }
    }

    private sealed class AreaSpatialIndex
    {
        private readonly double _minX;
        private readonly double _maxX;
        private readonly double _minY;
        private readonly double _maxY;
        private readonly double _invCell;
        private readonly Dictionary<long, List<int>> _grid = new();

        public AreaSpatialIndex(IndexedArea[] areas)
        {
            _minX = areas.Min(area => area.MinX);
            _maxX = areas.Max(area => area.MaxX);
            _minY = areas.Min(area => area.MinY);
            _maxY = areas.Max(area => area.MaxY);

            double span = Math.Max(_maxX - _minX, _maxY - _minY);
            int gridResolution = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(areas.Length * 2.0)));
            double cellSize = Math.Max(span / gridResolution, 1e-6);
            _invCell = 1.0 / cellSize;

            for (int areaIndex = 0; areaIndex < areas.Length; areaIndex++)
            {
                var area = areas[areaIndex];
                long minCellX = ToCell(area.MinX);
                long maxCellX = ToCell(area.MaxX);
                long minCellY = ToCell(area.MinY);
                long maxCellY = ToCell(area.MaxY);

                for (long cellY = minCellY; cellY <= maxCellY; cellY++)
                {
                    for (long cellX = minCellX; cellX <= maxCellX; cellX++)
                    {
                        long key = PackKey(cellX, cellY);
                        if (!_grid.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            _grid[key] = list;
                        }

                        list.Add(areaIndex);
                    }
                }
            }
        }

        public void GatherCandidates(double x, double y, List<int> candidates)
        {
            candidates.Clear();
            if (x < _minX || x > _maxX || y < _minY || y > _maxY)
                return;

            if (_grid.TryGetValue(PackKey(ToCell(x), ToCell(y)), out var list))
                candidates.AddRange(list);
        }

        private long ToCell(double value) => (long)Math.Floor(value * _invCell);

        private static long PackKey(long cellX, long cellY) =>
            (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
    }

    /// <summary>
    /// Closed polygon boundary defining an area.
    /// </summary>
    public sealed class AreaBoundary
    {
        /// <summary>Flat XY polygon vertices: [x0,y0, x1,y1, ...]</summary>
        public double[] XyVertices { get; }

        /// <summary>Number of polygon vertices.</summary>
        public int VertexCount { get; }

        public AreaBoundary(double[] xyVertices, int vertexCount)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
        }
    }

    /// <summary>
    /// Result of splitting a mesh by area boundaries.
    /// </summary>
    public sealed class SplitResult
    {
        /// <summary>Full mesh vertices (flat XYZ).</summary>
        public double[] Vertices { get; }

        /// <summary>Number of vertices.</summary>
        public int VertexCount { get; }

        /// <summary>Full mesh faces (triangle indices).</summary>
        public int[] Faces { get; }

        /// <summary>Number of faces.</summary>
        public int FaceCount { get; }

        /// <summary>Per-face area index: -1 = remainder, 0+ = area index.</summary>
        public int[] FaceAreaIndex { get; }

        /// <summary>Number of area boundaries.</summary>
        public int AreaCount { get; }

        public SplitResult(
            double[] vertices,
            int vertexCount,
            int[] faces,
            int faceCount,
            int[] faceAreaIndex,
            int areaCount)
        {
            Vertices = vertices;
            VertexCount = vertexCount;
            Faces = faces;
            FaceCount = faceCount;
            FaceAreaIndex = faceAreaIndex;
            AreaCount = areaCount;
        }
    }

    /// <summary>
    /// Split a mesh into areas defined by closed boundary curves.
    /// </summary>
    public static SplitResult? Split(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        AreaBoundary[] areas,
        double maxArea,
        double minAngle,
        out string? errorMessage)
    {
        errorMessage = null;
        const double dedupTol = 1e-6;

        if (areas.Length == 0)
        {
            errorMessage = "No area boundaries provided.";
            return null;
        }

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();
        var vertHash = new PadGrader.SpatialHash(dedupTol);
        var faceGrid = new PadGrader.FaceGrid(vertices, vertexCount, faces, faceCount);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];

            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        foreach (var area in areas)
        {
            var areaIndices = new int[area.VertexCount];
            for (int i = 0; i < area.VertexCount; i++)
            {
                double px = area.XyVertices[i * 2];
                double py = area.XyVertices[i * 2 + 1];

                int near = vertHash.FindNearest(xyList, px, py, dedupTol);
                if (near >= 0)
                {
                    areaIndices[i] = near;
                }
                else
                {
                    areaIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(faceGrid.InterpolateZ(px, py));
                    vertHash.Insert(areaIndices[i], px, py);
                }
            }

            for (int i = 0; i < area.VertexCount; i++)
            {
                int a = areaIndices[i];
                int b = areaIndices[(i + 1) % area.VertexCount];
                if (a != b)
                    segList.Add((a, b));
            }
        }

        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        var triMesh = TriangulationHelper.Triangulate(
            xyList,
            totalVerts,
            segList,
            maxArea,
            minAngle,
            out string? triWarning);

        if (triMesh == null)
        {
            errorMessage = triWarning ?? "Triangulation failed.";
            return null;
        }

        if (triWarning != null)
            errorMessage = triWarning;

        var outVerts = triMesh.Vertices.ToList();
        var outTris = triMesh.Triangles.ToList();
        int outVertCount = outVerts.Count;
        int outFaceCount = outTris.Count;

        var finalVerts = new double[outVertCount * 3];
        var idToIdx = new Dictionary<int, int>(outVertCount);

        for (int i = 0; i < outVertCount; i++)
        {
            var meshVertex = outVerts[i];
            idToIdx[meshVertex.ID] = i;
            finalVerts[i * 3] = meshVertex.X;
            finalVerts[i * 3 + 1] = meshVertex.Y;

            if (meshVertex.ID >= 0 && meshVertex.ID < totalVerts)
            {
                finalVerts[i * 3 + 2] = zList[meshVertex.ID];
            }
            else
            {
                finalVerts[i * 3 + 2] = faceGrid.InterpolateZ(meshVertex.X, meshVertex.Y);
            }
        }

        var finalFaces = new int[outFaceCount * 3];
        var faceAreaIndex = new int[outFaceCount];
        var indexedAreas = BuildIndexedAreas(areas);
        var areaIndex = new AreaSpatialIndex(indexedAreas);

        System.Threading.Tasks.Parallel.For(0, outFaceCount, () => new List<int>(8), (faceIndex, _, candidates) =>
        {
            var triangle = outTris[faceIndex];
            int i0 = idToIdx.GetValueOrDefault(triangle.GetVertex(0).ID, 0);
            int i1 = idToIdx.GetValueOrDefault(triangle.GetVertex(1).ID, 0);
            int i2 = idToIdx.GetValueOrDefault(triangle.GetVertex(2).ID, 0);

            finalFaces[faceIndex * 3] = i0;
            finalFaces[faceIndex * 3 + 1] = i1;
            finalFaces[faceIndex * 3 + 2] = i2;

            double cx = (finalVerts[i0 * 3] + finalVerts[i1 * 3] + finalVerts[i2 * 3]) / 3.0;
            double cy = (finalVerts[i0 * 3 + 1] + finalVerts[i1 * 3 + 1] + finalVerts[i2 * 3 + 1]) / 3.0;

            areaIndex.GatherCandidates(cx, cy, candidates);

            faceAreaIndex[faceIndex] = -1;
            for (int candidateIndex = candidates.Count - 1; candidateIndex >= 0; candidateIndex--)
            {
                int areaNumber = candidates[candidateIndex];
                var area = indexedAreas[areaNumber];
                if (cx < area.MinX || cx > area.MaxX || cy < area.MinY || cy > area.MaxY)
                    continue;

                if (PadGrader.PointInPolygon(cx, cy, area.Boundary.XyVertices, area.Boundary.VertexCount))
                {
                    faceAreaIndex[faceIndex] = areaNumber;
                    break;
                }
            }

            return candidates;
        }, _ => { });

        return new SplitResult(
            finalVerts,
            outVertCount,
            finalFaces,
            outFaceCount,
            faceAreaIndex,
            areas.Length);
    }

    private static IndexedArea[] BuildIndexedAreas(AreaBoundary[] areas)
    {
        var result = new IndexedArea[areas.Length];
        for (int areaIndex = 0; areaIndex < areas.Length; areaIndex++)
        {
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;

            var area = areas[areaIndex];
            for (int vertexIndex = 0; vertexIndex < area.VertexCount; vertexIndex++)
            {
                double x = area.XyVertices[vertexIndex * 2];
                double y = area.XyVertices[vertexIndex * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            result[areaIndex] = new IndexedArea
            {
                Boundary = area,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        return result;
    }
}
