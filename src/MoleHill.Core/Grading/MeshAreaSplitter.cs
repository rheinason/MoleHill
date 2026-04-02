using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Splits a terrain mesh into separate meshes by closed boundary curves.
/// Re-triangulates with boundaries as constrained edges, then classifies
/// each face by which area its centroid falls inside.
/// </summary>
public static class MeshAreaSplitter
{
    private readonly record struct BoundarySegment(double Ax, double Ay, double Bx, double By);

    private sealed class IndexedArea
    {
        public required AreaBoundary Boundary { get; init; }

        public required double MinX { get; init; }

        public required double MaxX { get; init; }

        public required double MinY { get; init; }

        public required double MaxY { get; init; }

        public required BoundarySegment[] Segments { get; init; }

        public required SpatialHashGrid2D SegmentIndex { get; init; }
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
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentConstraints,
        double tolerance,
        double maxArea,
        double minAngle,
        out string? errorMessage)
    {
        errorMessage = null;

        if (areas.Length == 0)
        {
            errorMessage = "No area boundaries provided.";
            return null;
        }

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(persistentConstraints.Count + areas.Length);
        constraints.AddRange(persistentConstraints);
        for (int i = 0; i < areas.Length; i++)
        {
            constraints.Add(ToConstraintPolyline(areas[i]));
        }

        var remeshResult = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = tolerance,
                MaxArea = maxArea,
                MinAngle = minAngle,
                ProtectSharpEdges = true
            });

        if (!remeshResult.Success)
        {
            errorMessage = remeshResult.Warning ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            errorMessage = remeshResult.Warning;

        var finalVerts = remeshResult.Vertices;
        var finalFaces = remeshResult.Faces;
        return Classify(
            finalVerts,
            finalVerts.Length / 3,
            finalFaces,
            finalFaces.Length / 3,
            areas,
            tolerance,
            out _);
    }

    /// <summary>
    /// Split a mesh into exact area boundaries while preserving the original
    /// terrain triangles everywhere the boundaries do not touch.
    /// </summary>
    public static SplitResult? SplitPreservingTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage)
    {
        return MeshAreaTopologySplitter.Split(
            vertices,
            vertexCount,
            faces,
            faceCount,
            areas,
            boundaryTolerance,
            out errorMessage);
    }

    public static SplitResult? Classify(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage)
    {
        errorMessage = null;

        if (areas.Length == 0)
        {
            errorMessage = "No area boundaries provided.";
            return null;
        }

        var faceAreaIndex = BuildFaceAreaIndex(vertices, faces, areas, boundaryTolerance);
        return new SplitResult(
            vertices,
            vertexCount,
            faces,
            faceCount,
            faceAreaIndex,
            areas.Length);
    }

    private static SurfaceRemesher.ConstraintPolyline ToConstraintPolyline(AreaBoundary area)
    {
        var points = new double[area.VertexCount * 3];
        for (int i = 0; i < area.VertexCount; i++)
        {
            points[i * 3] = area.XyVertices[i * 2];
            points[i * 3 + 1] = area.XyVertices[i * 2 + 1];
            points[i * 3 + 2] = 0.0;
        }

        return new SurfaceRemesher.ConstraintPolyline(points, area.VertexCount, IsClosed: true, PreserveInputElevation: false);
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
            var segments = new BoundarySegment[area.VertexCount];
            var segmentBounds = new Bounds2D[area.VertexCount];
            for (int vertexIndex = 0; vertexIndex < area.VertexCount; vertexIndex++)
            {
                double x = area.XyVertices[vertexIndex * 2];
                double y = area.XyVertices[vertexIndex * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                int next = (vertexIndex + 1) % area.VertexCount;
                double nx = area.XyVertices[next * 2];
                double ny = area.XyVertices[next * 2 + 1];
                segments[vertexIndex] = new BoundarySegment(x, y, nx, ny);
                segmentBounds[vertexIndex] = new Bounds2D(
                    Math.Min(x, nx),
                    Math.Max(x, nx),
                    Math.Min(y, ny),
                    Math.Max(y, ny));
            }

            result[areaIndex] = new IndexedArea
            {
                Boundary = area,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY,
                Segments = segments,
                SegmentIndex = SpatialHashGrid2D.Build(segmentBounds)
            };
        }

        return result;
    }

    private static int[] BuildFaceAreaIndex(
        double[] vertices,
        int[] faces,
        AreaBoundary[] areas,
        double boundaryTolerance)
    {
        int faceCount = faces.Length / 3;
        var faceAreaIndex = new int[faceCount];
        var indexedAreas = BuildIndexedAreas(areas);
        var areaBounds = new Bounds2D[indexedAreas.Length];
        for (int i = 0; i < indexedAreas.Length; i++)
            areaBounds[i] = new Bounds2D(indexedAreas[i].MinX, indexedAreas[i].MaxX, indexedAreas[i].MinY, indexedAreas[i].MaxY);

        var areaIndex = SpatialHashGrid2D.Build(areaBounds);
        double tolerance = Math.Max(boundaryTolerance, 0.0);

        System.Threading.Tasks.Parallel.For(
            0,
            faceCount,
            () => (
                AreaScratch: new SpatialHashGrid2D.QueryScratch(indexedAreas.Length),
                AreaCandidates: new List<int>(8),
                SegmentScratch: new SpatialHashGrid2D.QueryScratch(),
                SegmentCandidates: new List<int>(8)),
            (faceIndex, _, state) =>
        {
            int i0 = faces[faceIndex * 3];
            int i1 = faces[faceIndex * 3 + 1];
            int i2 = faces[faceIndex * 3 + 2];

            double cx = (vertices[i0 * 3] + vertices[i1 * 3] + vertices[i2 * 3]) / 3.0;
            double cy = (vertices[i0 * 3 + 1] + vertices[i1 * 3 + 1] + vertices[i2 * 3 + 1]) / 3.0;

            areaIndex.GatherCandidates(Bounds2D.FromPoint(cx, cy, tolerance), state.AreaCandidates, state.AreaScratch);

            faceAreaIndex[faceIndex] = -1;
            for (int candidateIndex = state.AreaCandidates.Count - 1; candidateIndex >= 0; candidateIndex--)
            {
                int areaNumber = state.AreaCandidates[candidateIndex];
                var area = indexedAreas[areaNumber];
                if (cx < area.MinX - tolerance || cx > area.MaxX + tolerance || cy < area.MinY - tolerance || cy > area.MaxY + tolerance)
                    continue;

                if ((tolerance > 0 && IsNearBoundary(cx, cy, tolerance, area, state.SegmentCandidates, state.SegmentScratch)) ||
                    PadGrader.PointInPolygon(cx, cy, area.Boundary.XyVertices, area.Boundary.VertexCount))
                {
                    faceAreaIndex[faceIndex] = areaNumber;
                    break;
                }
            }

            return state;
        }, _ => { });

        return faceAreaIndex;
    }

    private static bool IsNearBoundary(
        double x,
        double y,
        double tolerance,
        IndexedArea area,
        List<int> segmentCandidates,
        SpatialHashGrid2D.QueryScratch segmentScratch)
    {
        area.SegmentIndex.GatherCandidates(Bounds2D.FromPoint(x, y, tolerance), segmentCandidates, segmentScratch);
        double toleranceSquared = tolerance * tolerance;

        for (int i = 0; i < segmentCandidates.Count; i++)
        {
            var segment = area.Segments[segmentCandidates[i]];
            double distance = DistanceToSegment(x, y, segment.Ax, segment.Ay, segment.Bx, segment.By);
            if (distance * distance <= toleranceSquared)
                return true;
        }

        return false;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared <= 1e-20
            ? 0.0
            : Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0.0, 1.0);
        double closestX = ax + (dx * t);
        double closestY = ay + (dy * t);
        double offsetX = px - closestX;
        double offsetY = py - closestY;
        return Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
    }
}
