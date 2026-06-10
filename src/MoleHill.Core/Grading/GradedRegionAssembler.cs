using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Welds explicitly-built graded sub-meshes (batter strips, pad tops, corridor surfaces) into
/// the existing terrain. The terrain is re-triangulated with each daylight loop embedded as a
/// hard constraint (plain constrained Delaunay, all original terrain points retained, no quality
/// refinement), the faces inside the daylight loops are dropped, and the graded sub-meshes are
/// concatenated and welded along their shared loops. The shared daylight/footprint rings carry
/// identical coordinates on both sides, so an exact-coincidence weld produces a watertight,
/// manifold result without trusting a fragile global remesh.
/// </summary>
internal static class GradedRegionAssembler
{
    internal sealed class SubMesh
    {
        public required double[] Vertices { get; init; }

        public required int VertexCount { get; init; }

        public required int[] Faces { get; init; }

        public required int FaceCount { get; init; }
    }

    internal sealed class RegionInsert
    {
        /// <summary>Closed daylight ring as flat XYZ, carrying the exact terrain elevation.</summary>
        public required double[] DaylightLoopXyz { get; init; }

        public required int DaylightLoopCount { get; init; }

        /// <summary>The graded sub-meshes that fill the daylight loop (batter strip, pad top, ...).</summary>
        public required IReadOnlyList<SubMesh> SubMeshes { get; init; }
    }

    internal sealed class AssembledMesh
    {
        public required bool Success { get; init; }

        public string? Warning { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int VertexCount { get; init; }

        public int[] Faces { get; init; } = Array.Empty<int>();

        public int FaceCount { get; init; }
    }

    internal static AssembledMesh Assemble(
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        IReadOnlyList<RegionInsert> inserts,
        double tolerance,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline>? hardConstraints = null)
    {
        if (terrainVertexCount <= 0 || terrainFaceCount <= 0)
            return new AssembledMesh { Success = false, Warning = "Terrain mesh is empty." };
        if (inserts.Count == 0)
            return new AssembledMesh { Success = false, Warning = "No graded inserts supplied." };

        double weldTolerance = Math.Max(tolerance, 1e-6);
        var terrainGrid = new TerrainFaceGrid(terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount);

        if (!TryBuildOutsideTerrain(
                terrainVertices,
                terrainVertexCount,
                terrainFaces,
                terrainFaceCount,
                inserts,
                hardConstraints ?? Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                terrainGrid,
                weldTolerance,
                out double[] outsideVertices,
                out int outsideVertexCount,
                out int[] outsideFaces,
                out int outsideFaceCount,
                out string? outsideWarning))
        {
            return new AssembledMesh { Success = false, Warning = outsideWarning };
        }

        // Concatenate the carved terrain with every graded sub-mesh, then weld coincident
        // vertices (daylight rings, footprint rings) so the seams close.
        var welder = new VertexWelder(weldTolerance);
        var faces = new List<int>(outsideFaceCount * 3);
        // Sub-meshes can tile the same sliver where a batter collapses to ~zero reach (e.g. a station
        // at grade). After welding those become duplicate triangles → non-manifold edges. Track the
        // welded triangles and drop duplicates.
        var seenFaces = new HashSet<(int, int, int)>();

        AppendMesh(welder, faces, seenFaces, outsideVertices, outsideFaces, outsideFaceCount);
        foreach (RegionInsert insert in inserts)
        {
            foreach (SubMesh subMesh in insert.SubMeshes)
                AppendMesh(welder, faces, seenFaces, subMesh.Vertices, subMesh.Faces, subMesh.FaceCount);
        }

        double[] weldedVertices = welder.ToVertexArray();
        int weldedVertexCount = welder.Count;
        int[] faceArray = faces.ToArray();
        int faceCount = faceArray.Length / 3;

        // Sub-meshes (terrain seam, batter, pad top) come from different sources with independent
        // winding. Grading surfaces never overhang, so orient every face to face upward (+Z) for a
        // consistent normal field without relying on downstream UnifyNormals.
        OrientFacesUpward(weldedVertices, faceArray, faceCount);

        return new AssembledMesh
        {
            Success = true,
            Vertices = weldedVertices,
            VertexCount = weldedVertexCount,
            Faces = faceArray,
            FaceCount = faceCount
        };
    }

    private static bool TryBuildOutsideTerrain(
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        IReadOnlyList<RegionInsert> inserts,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        TerrainFaceGrid terrainGrid,
        double weldTolerance,
        out double[] outsideVertices,
        out int outsideVertexCount,
        out int[] outsideFaces,
        out int outsideFaceCount,
        out string? warning)
    {
        outsideVertices = Array.Empty<double>();
        outsideVertexCount = 0;
        outsideFaces = Array.Empty<int>();
        outsideFaceCount = 0;
        warning = null;

        var xyList = new List<double>(terrainVertexCount * 2);
        var zInput = new List<double>(terrainVertexCount);
        var pointIndex = new Dictionary<(long, long), int>(terrainVertexCount * 2);
        double inverseCell = 1.0 / weldTolerance;

        int AddPoint(double x, double y, double z)
        {
            var key = ((long)Math.Round(x * inverseCell), (long)Math.Round(y * inverseCell));
            if (pointIndex.TryGetValue(key, out int existing))
                return existing;

            int index = zInput.Count;
            xyList.Add(x);
            xyList.Add(y);
            zInput.Add(z);
            pointIndex[key] = index;
            return index;
        }

        var terrainMap = new int[terrainVertexCount];
        for (int i = 0; i < terrainVertexCount; i++)
            terrainMap[i] = AddPoint(terrainVertices[i * 3], terrainVertices[i * 3 + 1], terrainVertices[i * 3 + 2]);

        var segments = new List<(int a, int b)>();

        // The original terrain boundary must be part of the PSLG. Without it Triangle.NET would
        // treat the daylight loop as the outermost contour and discard everything beyond it; with
        // it the mesh covers the whole (possibly non-convex) terrain region and the loop becomes an
        // internal constraint whose interior we drop by classification.
        var terrainBoundary = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(terrainBoundary, new HashSet<long>(), terrainFaces, terrainFaceCount);
        foreach (var (a, b) in terrainBoundary)
        {
            int ai = terrainMap[a];
            int bi = terrainMap[b];
            if (ai != bi)
                segments.Add((ai, bi));
        }

        int loopSegmentCount = 0;
        foreach (RegionInsert insert in inserts)
        {
            int count = insert.DaylightLoopCount;
            if (count < 3)
                continue;

            int first = AddPoint(insert.DaylightLoopXyz[0], insert.DaylightLoopXyz[1], insert.DaylightLoopXyz[2]);
            int previous = first;
            for (int i = 1; i < count; i++)
            {
                int current = AddPoint(
                    insert.DaylightLoopXyz[i * 3],
                    insert.DaylightLoopXyz[i * 3 + 1],
                    insert.DaylightLoopXyz[i * 3 + 2]);
                if (current != previous)
                {
                    segments.Add((previous, current));
                    loopSegmentCount++;
                }

                previous = current;
            }

            if (previous != first)
            {
                segments.Add((previous, first));
                loopSegmentCount++;
            }
        }

        if (loopSegmentCount == 0)
        {
            warning = "Daylight loops were degenerate; nothing to embed in the terrain.";
            return false;
        }

        // Embed hard constraints (lock curves / preserved breaklines) that lie outside the carved
        // regions, so they survive as creases in the welded terrain at their preserved elevation.
        foreach (SurfaceRemesher.ConstraintPolyline constraint in hardConstraints)
        {
            int pc = constraint.PointCount;
            if (pc < 2 || constraint.Points is null || constraint.Points.Length < pc * 3)
                continue;

            double ConstraintZ(int p, double x, double y) =>
                constraint.PreserveInputElevation ? constraint.Points[p * 3 + 2] : terrainGrid.InterpolateZ(x, y);

            int Segments = pc + (constraint.IsClosed ? 0 : -1);
            for (int s = 0; s < Segments; s++)
            {
                int p0 = s;
                int p1 = (s + 1) % pc;
                double x0 = constraint.Points[p0 * 3], y0 = constraint.Points[p0 * 3 + 1];
                double x1 = constraint.Points[p1 * 3], y1 = constraint.Points[p1 * 3 + 1];

                // Skip segments that enter a carved region; grading owns that area.
                if (IsInsideAnyDaylightLoop(x0, y0, inserts) || IsInsideAnyDaylightLoop(x1, y1, inserts))
                    continue;

                int a = AddPoint(x0, y0, ConstraintZ(p0, x0, y0));
                int b = AddPoint(x1, y1, ConstraintZ(p1, x1, y1));
                if (a != b)
                    segments.Add((a, b));
            }
        }

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xyList,
            zInput.Count,
            segments,
            maxArea: 0.0,
            minAngle: 0.0,
            convex: false,
            segmentSplitting: 0);

        if (outcome.Mesh == null)
        {
            warning = outcome.WarningMessage ?? "Failed to embed daylight loops in the terrain.";
            return false;
        }

        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        if (extracted.FaceCount == 0)
        {
            warning = "Embedding the daylight loops produced no terrain triangles.";
            return false;
        }

        var vertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];
            vertices[i * 3] = x;
            vertices[i * 3 + 1] = y;
            vertices[i * 3 + 2] = sourceId >= 0 && sourceId < zInput.Count
                ? zInput[sourceId]
                : terrainGrid.InterpolateZ(x, y);
        }

        // Keep only faces whose centroid is outside every daylight loop. The interior is replaced
        // by the explicit graded sub-meshes.
        var keptFaces = new List<int>(extracted.FaceCount * 3);
        for (int f = 0; f < extracted.FaceCount; f++)
        {
            int a = extracted.Faces[f * 3];
            int b = extracted.Faces[f * 3 + 1];
            int c = extracted.Faces[f * 3 + 2];
            double cx = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double cy = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;

            if (IsInsideAnyDaylightLoop(cx, cy, inserts))
                continue;

            keptFaces.Add(a);
            keptFaces.Add(b);
            keptFaces.Add(c);
        }

        if (keptFaces.Count == 0)
        {
            warning = "All terrain faces fell inside the daylight loops.";
            return false;
        }

        outsideVertices = vertices;
        outsideVertexCount = extracted.VertexCount;
        outsideFaces = keptFaces.ToArray();
        outsideFaceCount = keptFaces.Count / 3;
        return true;
    }

    private static bool IsInsideAnyDaylightLoop(double x, double y, IReadOnlyList<RegionInsert> inserts)
    {
        foreach (RegionInsert insert in inserts)
        {
            if (insert.DaylightLoopCount < 3)
                continue;
            if (GradingGeometry2D.PointInPolygon(x, y, ToXy(insert.DaylightLoopXyz, insert.DaylightLoopCount), insert.DaylightLoopCount))
                return true;
        }

        return false;
    }

    private static double[] ToXy(double[] xyz, int count)
    {
        var xy = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            xy[i * 2] = xyz[i * 3];
            xy[i * 2 + 1] = xyz[i * 3 + 1];
        }

        return xy;
    }

    private static void OrientFacesUpward(double[] vertices, int[] faces, int faceCount)
    {
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            double ux = vertices[b * 3] - vertices[a * 3];
            double uy = vertices[b * 3 + 1] - vertices[a * 3 + 1];
            double vx = vertices[c * 3] - vertices[a * 3];
            double vy = vertices[c * 3 + 1] - vertices[a * 3 + 1];
            double nz = (ux * vy) - (uy * vx); // z component of the face normal

            if (nz < 0.0)
            {
                faces[f * 3 + 1] = c;
                faces[f * 3 + 2] = b;
            }
        }
    }

    private static void AppendMesh(
        VertexWelder welder,
        List<int> faces,
        HashSet<(int, int, int)> seenFaces,
        double[] vertices,
        int[] meshFaces,
        int faceCount)
    {
        for (int f = 0; f < faceCount; f++)
        {
            int a = welder.Add(vertices, meshFaces[f * 3]);
            int b = welder.Add(vertices, meshFaces[f * 3 + 1]);
            int c = welder.Add(vertices, meshFaces[f * 3 + 2]);
            if (a == b || b == c || a == c)
                continue;

            if (!seenFaces.Add(SortedTriple(a, b, c)))
                continue;

            faces.Add(a);
            faces.Add(b);
            faces.Add(c);
        }
    }

    private static (int, int, int) SortedTriple(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>Merges coincident vertices (within tolerance) as they are appended.</summary>
    private sealed class VertexWelder
    {
        private readonly Dictionary<(long, long, long), int> _cells;
        private readonly List<double> _vertices = new();
        private readonly double _inverseCell;

        public VertexWelder(double tolerance)
        {
            _inverseCell = 1.0 / Math.Max(tolerance, 1e-9);
            _cells = new Dictionary<(long, long, long), int>();
        }

        public int Count => _vertices.Count / 3;

        public int Add(double[] vertices, int index)
        {
            double x = vertices[index * 3];
            double y = vertices[index * 3 + 1];
            double z = vertices[index * 3 + 2];
            var key = (
                (long)Math.Round(x * _inverseCell),
                (long)Math.Round(y * _inverseCell),
                (long)Math.Round(z * _inverseCell));

            if (_cells.TryGetValue(key, out int existing))
                return existing;

            int newIndex = _vertices.Count / 3;
            _vertices.Add(x);
            _vertices.Add(y);
            _vertices.Add(z);
            _cells[key] = newIndex;
            return newIndex;
        }

        public double[] ToVertexArray() => _vertices.ToArray();
    }
}
