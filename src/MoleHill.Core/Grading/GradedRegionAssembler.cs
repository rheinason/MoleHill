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

    /// <summary>
    /// Result of splitting the terrain along daylight loops while preserving the terrain outside.
    /// Carries the kept outside faces and the conformed hole-boundary loops (terrain elevation) that
    /// the hole fill must triangulate against so the two sides share vertices exactly.
    /// </summary>
    internal sealed class SplitOutsideResult
    {
        public required bool Success { get; init; }

        public string? Warning { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int VertexCount { get; init; }

        public int[] OutsideFaces { get; init; } = Array.Empty<int>();

        public int OutsideFaceCount { get; init; }

        /// <summary>Each loop is a list of vertex indices into <see cref="Vertices"/> (closed).</summary>
        public IReadOnlyList<int[]> HoleBoundaryLoops { get; init; } = Array.Empty<int[]>();
    }

    /// <summary>
    /// Conforms the terrain to the daylight loops (clipped to the terrain outline) and returns the
    /// FULL split mesh — every face kept. The caller keeps the whole conformed mesh and assigns Z by
    /// section, so there is no carve/fill/weld seam (watertight by construction when the split succeeds).
    /// </summary>
    internal static MeshAreaSplitter.SplitResult? SplitConform(
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        IReadOnlyList<double[]> daylightLoopsXy,
        double tolerance)
    {
        double[]? terrainOutline = TryBuildTerrainOutline(terrainFaces, terrainFaceCount, terrainVertices);
        var areas = new List<MeshAreaSplitter.AreaBoundary>(daylightLoopsXy.Count);
        foreach (double[] xy in daylightLoopsXy)
        {
            double[] effective = ClipLoopToTerrain(xy, terrainOutline, tolerance);
            int count = effective.Length / 2;
            if (count >= 3)
                areas.Add(new MeshAreaSplitter.AreaBoundary(effective, count));
        }

        if (areas.Count == 0)
            return null;

        return MeshAreaSplitter.SplitPreservingTopology(
            terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount, areas.ToArray(), tolerance, out _);
    }

    /// <summary>
    /// Splits the terrain along the daylight loops (local insertion, terrain detail preserved
    /// everywhere the loops do not cross), keeps the faces outside the loops, and extracts the
    /// conformed hole-boundary loops (edges shared between an inside and an outside face).
    /// </summary>
    internal static SplitOutsideResult SplitOutside(
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        IReadOnlyList<double[]> daylightLoopsXy,
        double tolerance)
    {
        // A daylight loop that runs off the terrain edge is clipped to the terrain outline so the
        // carve region stays closed (following the boundary) instead of leaving an open chain.
        double[]? terrainOutline = TryBuildTerrainOutline(terrainFaces, terrainFaceCount, terrainVertices);

        var areas = new List<MeshAreaSplitter.AreaBoundary>(daylightLoopsXy.Count);
        foreach (double[] xy in daylightLoopsXy)
        {
            double[] effective = ClipLoopToTerrain(xy, terrainOutline, tolerance);
            int count = effective.Length / 2;
            if (count >= 3)
                areas.Add(new MeshAreaSplitter.AreaBoundary(effective, count));
        }

        if (areas.Count == 0)
            return new SplitOutsideResult { Success = false, Warning = "No usable daylight loops." };

        MeshAreaSplitter.SplitResult? split = MeshAreaSplitter.SplitPreservingTopology(
            terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount, areas.ToArray(), tolerance, out string? warning);
        if (split is null)
            return new SplitOutsideResult { Success = false, Warning = warning ?? "Terrain split failed." };

        var edgeCounts = new Dictionary<long, (int inside, int outside)>();
        var outsideFaces = new List<int>(split.FaceCount * 3);

        void Bump(int a, int b, bool inside)
        {
            long key = EdgeKey(a, b);
            edgeCounts.TryGetValue(key, out (int inside, int outside) c);
            if (inside) c.inside++; else c.outside++;
            edgeCounts[key] = c;
        }

        for (int f = 0; f < split.FaceCount; f++)
        {
            int a = split.Faces[f * 3];
            int b = split.Faces[f * 3 + 1];
            int c = split.Faces[f * 3 + 2];
            bool inside = split.FaceAreaIndex[f] != -1;
            Bump(a, b, inside);
            Bump(b, c, inside);
            Bump(c, a, inside);

            if (!inside)
            {
                outsideFaces.Add(a);
                outsideFaces.Add(b);
                outsideFaces.Add(c);
            }
        }

        if (outsideFaces.Count == 0)
            return new SplitOutsideResult { Success = false, Warning = "All terrain fell inside the daylight loops." };

        // The conformed daylight loop = edges that separate an inside face from an outside face, OR
        // naked edges of an inside face (where the carve region meets the terrain perimeter because
        // the daylight was clipped to the boundary).
        var adjacency = new Dictionary<int, List<int>>();
        foreach (KeyValuePair<long, (int inside, int outside)> entry in edgeCounts)
        {
            bool sharedInsideOutside = entry.Value.inside >= 1 && entry.Value.outside >= 1;
            bool nakedInsideEdge = entry.Value.inside == 1 && entry.Value.outside == 0;
            if (!sharedInsideOutside && !nakedInsideEdge)
                continue;

            int a = (int)(entry.Key >> 32);
            int b = (int)(entry.Key & 0xFFFFFFFFL);
            AddAdjacency(adjacency, a, b);
            AddAdjacency(adjacency, b, a);
        }

        List<int[]> loops = ChainBoundaryLoops(adjacency);
        if (loops.Count == 0)
            return new SplitOutsideResult { Success = false, Warning = "Could not trace a closed conformed daylight boundary (daylight likely reaches the terrain edge)." };

        return new SplitOutsideResult
        {
            Success = true,
            Vertices = split.Vertices,
            VertexCount = split.VertexCount,
            OutsideFaces = outsideFaces.ToArray(),
            OutsideFaceCount = outsideFaces.Count / 3,
            HoleBoundaryLoops = loops
        };
    }

    /// <summary>
    /// Welds the kept-outside terrain mesh with the graded hole-fill sub-meshes. The fills' outer
    /// rings are the conformed hole boundary, identical to the outside hole edges, so the weld is
    /// exact. Dedupes coincident faces and orients winding upward; validates manifold/watertight.
    /// </summary>
    internal static AssembledMesh WeldGradedRegion(
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        IReadOnlyList<SubMesh> fills,
        double tolerance)
    {
        double weldTolerance = Math.Max(tolerance, 1e-6);
        var welder = new VertexWelder(weldTolerance);
        var faces = new List<int>(outsideFaceCount * 3);
        var seenFaces = new HashSet<(int, int, int)>();

        AppendMesh(welder, faces, seenFaces, outsideVertices, outsideFaces, outsideFaceCount);
        foreach (SubMesh fill in fills)
            AppendMesh(welder, faces, seenFaces, fill.Vertices, fill.Faces, fill.FaceCount);

        double[] weldedVertices = welder.ToVertexArray();
        int[] faceArray = faces.ToArray();
        int faceCount = faceArray.Length / 3;

        // Enforce the watertight 2.5D invariant: zip hairline seam cracks (near-coincident boundary
        // vertices the exact weld missed) and close any remaining interior holes, so the assembly has a
        // single outer boundary. This is what lets the explicit engine succeed on grade-on-grade scenes
        // instead of deferring to the fallback.
        (weldedVertices, faceArray) = MeshTopologyOperations.MakeWatertight(
            weldedVertices, welder.Count, faceArray, faceCount, weldTolerance, out _, out _);
        faceCount = faceArray.Length / 3;

        OrientFacesUpward(weldedVertices, faceArray, faceCount);

        return new AssembledMesh
        {
            Success = true,
            Vertices = weldedVertices,
            VertexCount = weldedVertices.Length / 3,
            Faces = faceArray,
            FaceCount = faceCount
        };
    }

    /// <summary>Builds the terrain's single naked-edge outline loop as flat XY, or null if it has 0/many.</summary>
    private static double[]? TryBuildTerrainOutline(int[] faces, int faceCount, double[] vertices)
    {
        var boundary = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundary, new HashSet<long>(), faces, faceCount);
        if (boundary.Count == 0)
            return null;

        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (a, b) in boundary)
        {
            AddAdjacency(adjacency, a, b);
            AddAdjacency(adjacency, b, a);
        }

        List<int[]> loops = ChainBoundaryLoops(adjacency);
        if (loops.Count != 1)
            return null;

        int[] loop = loops[0];
        var xy = new double[loop.Length * 2];
        for (int i = 0; i < loop.Length; i++)
        {
            xy[i * 2] = vertices[loop[i] * 3];
            xy[i * 2 + 1] = vertices[loop[i] * 3 + 1];
        }

        return xy;
    }

    private static double[] ClipLoopToTerrain(double[] loopXy, double[]? terrainOutline, double tolerance)
    {
        if (terrainOutline is null)
            return loopXy;

        int count = loopXy.Length / 2;
        int outlineCount = terrainOutline.Length / 2;
        bool fullyInside = true;
        for (int i = 0; i < count; i++)
        {
            if (!GradingGeometry2D.PointInPolygon(loopXy[i * 2], loopXy[i * 2 + 1], terrainOutline, outlineCount))
            {
                fullyInside = false;
                break;
            }
        }

        if (fullyInside)
            return loopXy;

        if (ClipperGeometry.TryIntersectClosedLoops(new[] { loopXy }, terrainOutline, tolerance, out List<double[]> clipped) &&
            ClipperGeometry.TryPickLargestLoop(clipped, out double[] largest) &&
            largest.Length >= 6)
        {
            return largest;
        }

        return loopXy;
    }

    private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static void AddAdjacency(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out List<int>? list))
        {
            list = new List<int>(2);
            adjacency[from] = list;
        }

        if (!list.Contains(to))
            list.Add(to);
    }

    private static List<int[]> ChainBoundaryLoops(Dictionary<int, List<int>> adjacency)
    {
        var loops = new List<int[]>();
        var visited = new HashSet<long>(); // visited undirected edges

        foreach (int start in adjacency.Keys)
        {
            foreach (int firstNext in adjacency[start])
            {
                if (visited.Contains(EdgeKey(start, firstNext)))
                    continue;

                var loop = new List<int> { start };
                int prev = start;
                int current = firstNext;
                visited.Add(EdgeKey(start, firstNext));

                while (current != start)
                {
                    loop.Add(current);
                    int next = -1;
                    foreach (int candidate in adjacency[current])
                    {
                        if (candidate == prev)
                            continue;
                        if (visited.Contains(EdgeKey(current, candidate)))
                            continue;
                        next = candidate;
                        break;
                    }

                    if (next < 0)
                        break; // open chain (shouldn't happen for a clean hole); abandon

                    visited.Add(EdgeKey(current, next));
                    prev = current;
                    current = next;
                }

                if (current == start && loop.Count >= 3)
                    loops.Add(loop.ToArray());
            }
        }

        return loops;
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

    /// <summary>
    /// Builds an actionable message describing why a welded graded assembly failed the topology gate,
    /// naming the specific defect(s) so a deferral to the fallback is diagnosable rather than opaque.
    /// </summary>
    internal static string DescribeWeldTopologyFailure(
        string label,
        MeshTopologyValidator.BoundaryGraphAnalysis assembled,
        MeshTopologyValidator.BoundaryGraphAnalysis terrain)
    {
        var reasons = new List<string>(3);
        if (assembled.NonManifoldEdgeCount > 0)
            reasons.Add($"{assembled.NonManifoldEdgeCount} non-manifold edge(s)");
        if (assembled.HasOpenBoundaryChains)
            reasons.Add("open boundary chains (the graded region did not weld watertight to the terrain)");
        if (assembled.BoundaryComponentCount > terrain.BoundaryComponentCount)
            reasons.Add(
                $"{assembled.BoundaryComponentCount - terrain.BoundaryComponentCount} extra boundary loop(s) " +
                $"({terrain.BoundaryComponentCount}->{assembled.BoundaryComponentCount}); the graded region likely " +
                "overlaps an existing terrain hole or an adjacent graded feature");

        string detail = reasons.Count > 0 ? string.Join("; ", reasons) : "invalid topology";
        return $"{label} explicit assembly was not watertight: {detail}.";
    }
}
