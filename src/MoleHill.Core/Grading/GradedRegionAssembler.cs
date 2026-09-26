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

        /// <summary>
        /// Optional per-vertex map from this sub-mesh's vertices to the SHARED terrain vertex index
        /// each one reproduces (or -1 for the sub-mesh's own interior vertices). When present,
        /// <see cref="WeldGradedRegion"/> stitches the sub-mesh onto the terrain by this identity
        /// (Triangle.NET's preserved <c>Vertex.ID</c>) instead of by spatial proximity â€” exact by
        /// construction, so the two independent triangulations cannot disagree on the shared boundary
        /// (the source of hairline cracks and overlapping slivers). Null falls back to the position weld.
        /// </summary>
        public int[]? BoundaryTerrainIndex { get; init; }

        /// <summary>
        /// Optional terrain-vertex merges this sub-mesh's (deduplicated) boundary implied: when two
        /// terrain boundary vertices are near-coincident, the fill triangulates them as ONE clean input
        /// point (Triangle.NET wants non-degenerate input), so the weld must collapse that terrain pair
        /// too or the dropped one would dangle. Each pair is (terrainIndexToReplace, keepTerrainIndex).
        /// </summary>
        public (int From, int To)[]? TerrainMerges { get; init; }
    }

    internal sealed class AssembledMesh
    {
        public required bool Success { get; init; }

        public string? Warning { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int VertexCount { get; init; }

        public int[] Faces { get; init; } = Array.Empty<int>();

        public int FaceCount { get; init; }

        /// <summary>
        /// The weld's own boundary analysis of <see cref="Faces"/>, when the weld did not have to repair
        /// the mesh; null when it did. Callers validating the assembly reuse it instead of sorting every
        /// edge of the terrain a second time. Face winding does not enter it (edges are keyed min/max),
        /// so the upward re-orientation after it leaves it exact.
        /// </summary>
        public MeshTopologyValidator.BoundaryGraphAnalysis? BoundaryAnalysis { get; init; }
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
    /// FULL split mesh â€” every face kept. The caller keeps the whole conformed mesh and assigns Z by
    /// section, so there is no carve/fill/weld seam (watertight by construction when the split succeeds).
    /// </summary>
    internal static MeshAreaSplitter.SplitResult? SplitConform(
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        IReadOnlyList<double[]> daylightLoopsXy,
        double tolerance,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline>? hardConstraints = null,
        MeshAreaTopologySplitter.PerformanceTimings? performanceTimings = null)
    {
        double[]? terrainOutline = TryBuildTerrainOutline(terrainFaces, terrainFaceCount, terrainVertices);
        var areas = new List<MeshAreaSplitter.AreaBoundary>(daylightLoopsXy.Count);
        var clippedLoops = new List<double[]>(daylightLoopsXy.Count);
        foreach (double[] xy in daylightLoopsXy)
        {
            double[] effective = ClipLoopToTerrain(xy, terrainOutline, tolerance);
            int count = effective.Length / 2;
            if (count >= 3)
            {
                areas.Add(new MeshAreaSplitter.AreaBoundary(effective, count));
                clippedLoops.Add(effective);
            }
        }

        if (areas.Count == 0)
            return null;

        MeshAreaSplitter.AreaBoundary[] areaArray = areas.ToArray();
        MeshAreaSplitter.SplitResult? handRolled = performanceTimings == null
            ? MeshAreaSplitter.SplitPreservingTopology(
                terrainVertices,
                terrainVertexCount,
                terrainFaces,
                terrainFaceCount,
                areaArray,
                tolerance,
                out _)
            : MeshAreaSplitter.SplitPreservingTopology(
                terrainVertices,
                terrainVertexCount,
                terrainFaces,
                terrainFaceCount,
                areaArray,
                tolerance,
                out _,
                performanceTimings);

        // The hand-rolled per-face conforming subdivision preserves terrain detail but can emit a
        // non-manifold sliver when a daylightÃ—terrain-edge cut point lands a hair off an existing
        // terrain vertex (distinct at model tolerance â†’ two degenerate faces on a shared edge). When
        // that happens, re-conform with a single Triangle.NET CDT, which is always a valid
        // (non-overlapping, manifold) triangulation. Keep the hand-rolled result whenever it is clean
        // so the terrain-detail-preserving path is unchanged for the scenes it already handles.
        // Non-manifold is not the only way the hand-rolled split can come back unusable. The same
        // "a hair off an existing vertex" situation also produces a plain NON-CONFORMING edge: the cut
        // point splits one face and its neighbour keeps the whole edge, so a vertex ends up inside that
        // edge. Nothing is non-manifold and no area is lost -- measured on a wall rail, the split
        // preserved projected area exactly -- but the sub-edge is used once, so it reads as a naked
        // edge and the terrain gains a boundary loop. Split-keep's own gate then rejects the result and
        // the corridor drops to a tier that does no ruled batter at all, which is how a 20 degree wall
        // came out at roughly 46 degrees.
        //
        // So accept the hand-rolled split only when it leaves the terrain's topology no worse than it
        // found it, and otherwise fall through to the CDT re-conform below, which is a valid
        // triangulation by construction. Preserving terrain detail is not worth an invalid mesh.
        if (handRolled is not null &&
            GradingTopologyDiagnostics.IsNotWorseThanInput(
                terrainFaces, terrainFaceCount, handRolled.Faces, handRolled.FaceCount, out _))
        {
            return handRolled;
        }

        MeshAreaSplitter.SplitResult? cdt = SplitConformViaCdt(
            terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount, clippedLoops, terrainOutline, tolerance, hardConstraints);
        return cdt ?? handRolled;
    }

    /// <summary>
    /// Adds each segment to <paramref name="output"/> split at every point of <paramref name="xy"/> that lies
    /// within <paramref name="tolerance"/> of its interior, in order along the segment. Points are bucketed
    /// on a coarse grid so a long segment only tests the points near it.
    /// </summary>
    internal static void AddSegmentsSplitAtOnSegmentPoints(
        IReadOnlyList<double> xy,
        IReadOnlyList<(int a, int b)> segmentsToSplit,
        double tolerance,
        List<(int a, int b)> output)
    {
        if (segmentsToSplit.Count == 0)
            return;

        int pointCount = xy.Count / 2;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i < pointCount; i++)
        {
            double x = xy[i * 2], y = xy[i * 2 + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        double extent = Math.Max(maxX - minX, maxY - minY);
        double cell = Math.Max(extent / Math.Max(1.0, Math.Sqrt(pointCount)), tolerance * 4.0);
        if (!(cell > 0.0) || double.IsInfinity(cell))
        {
            output.AddRange(segmentsToSplit);
            return;
        }

        int columns = Math.Max(1, (int)Math.Floor((maxX - minX) / cell) + 1);
        int rows = Math.Max(1, (int)Math.Floor((maxY - minY) / cell) + 1);
        var bucketStart = new int[(columns * rows) + 1];
        var pointCell = new int[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            int cx = Math.Clamp((int)((xy[i * 2] - minX) / cell), 0, columns - 1);
            int cy = Math.Clamp((int)((xy[i * 2 + 1] - minY) / cell), 0, rows - 1);
            pointCell[i] = (cy * columns) + cx;
            bucketStart[pointCell[i] + 1]++;
        }

        for (int c = 0; c < columns * rows; c++)
            bucketStart[c + 1] += bucketStart[c];

        var bucketPoints = new int[pointCount];
        var fill = (int[])bucketStart.Clone();
        for (int i = 0; i < pointCount; i++)
            bucketPoints[fill[pointCell[i]]++] = i;

        double toleranceSquared = tolerance * tolerance;
        var onSegment = new List<(double t, int point)>();
        foreach ((int a, int b) in segmentsToSplit)
        {
            double ax = xy[a * 2], ay = xy[a * 2 + 1];
            double dx = xy[b * 2] - ax, dy = xy[b * 2 + 1] - ay;
            double lengthSquared = (dx * dx) + (dy * dy);
            if (lengthSquared <= toleranceSquared)
            {
                output.Add((a, b));
                continue;
            }

            int c0 = Math.Clamp((int)((Math.Min(ax, ax + dx) - tolerance - minX) / cell), 0, columns - 1);
            int c1 = Math.Clamp((int)((Math.Max(ax, ax + dx) + tolerance - minX) / cell), 0, columns - 1);
            int r0 = Math.Clamp((int)((Math.Min(ay, ay + dy) - tolerance - minY) / cell), 0, rows - 1);
            int r1 = Math.Clamp((int)((Math.Max(ay, ay + dy) + tolerance - minY) / cell), 0, rows - 1);

            onSegment.Clear();
            double length = Math.Sqrt(lengthSquared);
            double endMargin = tolerance / length;
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int bucket = (r * columns) + c;
                    for (int k = bucketStart[bucket]; k < bucketStart[bucket + 1]; k++)
                    {
                        int p = bucketPoints[k];
                        if (p == a || p == b)
                            continue;

                        double px = xy[p * 2] - ax, py = xy[p * 2 + 1] - ay;
                        double t = ((px * dx) + (py * dy)) / lengthSquared;
                        if (t <= endMargin || t >= 1.0 - endMargin)
                            continue;

                        double ox = px - (t * dx), oy = py - (t * dy);
                        if ((ox * ox) + (oy * oy) <= toleranceSquared)
                            onSegment.Add((t, p));
                    }
                }
            }

            if (onSegment.Count == 0)
            {
                output.Add((a, b));
                continue;
            }

            onSegment.Sort((x, y) => x.t != y.t ? x.t.CompareTo(y.t) : x.point.CompareTo(y.point));
            int previous = a;
            double previousT = 0.0;
            foreach ((double t, int point) in onSegment)
            {
                // The caller's weld rounds to grid cells, so two points within tolerance can both
                // survive it when they straddle a cell edge. Routing through both would force a
                // sub-tolerance constraint segment — the very sliver this split exists to avoid.
                if (point == previous || t - previousT <= endMargin)
                    continue;

                output.Add((previous, point));
                previous = point;
                previousT = t;
            }

            if (previous != b)
                output.Add((previous, b));
        }
    }

    private static bool HasNonManifoldEdge(int[] faces, int faceCount)
    {
        return MeshTopologyValidator.HasNonManifoldEdge(faces, faceCount);
    }

    /// <summary>
    /// Triangle.NET-native terrain conform: re-triangulate the terrain points with the terrain outline
    /// and each clipped daylight loop inserted as exact constraint segments (one CDT â€” always a valid,
    /// non-overlapping triangulation), then keep every face whose centroid lies inside the terrain
    /// outline (dropping only the convex-hull skirt). The result is one manifold surface bounded by the
    /// terrain outline, conformed to the daylight loops, with all original terrain vertices preserved
    /// (so terrain elevations are exact) â€” watertight by construction for the split-keep path. Returns
    /// null when the terrain is non-simple (no single outline) or the CDT/extract did not produce a
    /// clean manifold mesh, so the caller can defer.
    /// </summary>
    internal static MeshAreaSplitter.SplitResult? SplitConformViaCdt(
        double[] terrainVertices,
        int terrainVertexCount,
        int[] terrainFaces,
        int terrainFaceCount,
        IReadOnlyList<double[]> clippedLoops,
        double[]? terrainOutline,
        double tolerance,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline>? hardConstraints = null)
    {
        if (terrainOutline is null || clippedLoops.Count == 0)
            return null;

        double weldTol = Math.Max(tolerance, 1e-6);
        double inverseCell = 1.0 / weldTol;
        var xy = new List<double>(terrainVertexCount * 2);
        var zin = new List<double>(terrainVertexCount);
        var pointIndex = new Dictionary<(long, long), int>(terrainVertexCount * 2);

        int AddPoint(double x, double y, double z)
        {
            var key = ((long)Math.Round(x * inverseCell), (long)Math.Round(y * inverseCell));
            if (pointIndex.TryGetValue(key, out int existing))
                return existing;

            int index = zin.Count;
            xy.Add(x);
            xy.Add(y);
            zin.Add(z);
            pointIndex[key] = index;
            return index;
        }

        var terrainMap = new int[terrainVertexCount];
        for (int i = 0; i < terrainVertexCount; i++)
            terrainMap[i] = AddPoint(terrainVertices[i * 3], terrainVertices[i * 3 + 1], terrainVertices[i * 3 + 2]);

        var segments = new List<(int a, int b)>();
        var terrainBoundary = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(terrainBoundary, IndexedMeshTools.CreateEdgeKeySet(), terrainFaces, terrainFaceCount);
        foreach (var (a, b) in terrainBoundary)
        {
            int ai = terrainMap[a], bi = terrainMap[b];
            if (ai != bi)
                segments.Add((ai, bi));
        }

        var grid = new TerrainFaceGrid(terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount);
        foreach (double[] loop in clippedLoops)
        {
            int n = loop.Length / 2;
            var li = new int[n];
            for (int j = 0; j < n; j++)
                li[j] = AddPoint(loop[j * 2], loop[j * 2 + 1], grid.InterpolateZ(loop[j * 2], loop[j * 2 + 1]));

            for (int j = 0; j < n; j++)
            {
                int u = li[j], v = li[(j + 1) % n];
                if (u != v)
                    segments.Add((u, v));
            }
        }

        // Re-insert the hard-constraint breaklines (retaining walls, etc.) as exact constraint edges.
        // Without them, a full-terrain CDT re-conform silently flips away the near-vertical wall-face
        // edges — orphaning wall-top vertices into tent-pole spikes. The wall vertices are already
        // terrain points, so AddPoint maps each constraint vertex onto its existing terrain point
        // (preserving the surveyed wall elevation); forcing the segment keeps the wall face intact.
        if (hardConstraints is not null)
        {
            var hardSegments = new List<(int a, int b)>();
            foreach (SurfaceRemesher.ConstraintPolyline constraint in hardConstraints)
            {
                int cn = constraint.PointCount;
                if (cn < 2)
                    continue;

                var ci = new int[cn];
                for (int j = 0; j < cn; j++)
                    ci[j] = AddPoint(constraint.Points[j * 3], constraint.Points[j * 3 + 1], constraint.Points[j * 3 + 2]);

                int lastSeg = constraint.IsClosed ? cn : cn - 1;
                for (int j = 0; j < lastSeg; j++)
                {
                    int u = ci[j], v = ci[(j + 1) % cn];
                    if (u != v)
                        hardSegments.Add((u, v));
                }
            }

            // Upstream stages have already embedded these constraints, so each segment is typically a
            // chain of terrain vertices sitting a hair (~1e-7) off the corner-to-corner line. Forced whole,
            // the segment passes those vertices by and Triangle.NET closes each one against it with a
            // zero-area sliver the full length of the side. The mesh stays watertight, but a remesh then
            // splits the long edge and the collinear short edges to coincident midpoints, and Rhino's
            // float vertex weld turns those into non-manifold edges and scrambled normals. Route the
            // segment through the vertices it already passes through instead.
            AddSegmentsSplitAtOnSegmentPoints(xy, hardSegments, weldTol, segments);
        }

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xy, xy.Count / 2, segments, maxArea: 0.0, minAngle: 0.0, convex: false, segmentSplitting: 0);
        if (outcome.Mesh is null)
            return null;

        TriangleNetExtractor.Result ex = TriangleNetExtractor.Extract(outcome.Mesh);
        if (ex.FaceCount == 0)
            return null;

        int vc = ex.VertexCount;
        var verts = new double[vc * 3];
        for (int i = 0; i < vc; i++)
        {
            double x = ex.Xy[i * 2], y = ex.Xy[i * 2 + 1];
            int sid = ex.SourceIds[i];
            verts[i * 3] = x;
            verts[i * 3 + 1] = y;
            verts[i * 3 + 2] = sid >= 0 && sid < zin.Count ? zin[sid] : grid.InterpolateZ(x, y);
        }

        // Keep every face inside the terrain outline (the convex-hull skirt is dropped by the centroid
        // test; because the outline is a constraint, no CDT face straddles it, so the test is exact).
        // Every face centroid is tested against the terrain outline and then against each clipped loop,
        // so preparing them once turns faces x edges into faces x (bounds test + one Y bucket).
        // Prepared containment answers exactly what the linear crossing test answered.
        PreparedPolygon? preparedOutline = PreparedPolygon.TryCreate(terrainOutline, terrainOutline.Length / 2);
        if (preparedOutline == null)
            return null;

        var preparedLoops = new PreparedPolygon?[clippedLoops.Count];
        for (int k = 0; k < clippedLoops.Count; k++)
            preparedLoops[k] = PreparedPolygon.TryCreate(clippedLoops[k], clippedLoops[k].Length / 2);

        var keptFaces = new List<int>(ex.FaceCount * 3);
        var keptAreaIndex = new List<int>(ex.FaceCount);
        for (int f = 0; f < ex.FaceCount; f++)
        {
            int a = ex.Faces[f * 3], b = ex.Faces[f * 3 + 1], c = ex.Faces[f * 3 + 2];
            double cx = (verts[a * 3] + verts[b * 3] + verts[c * 3]) / 3.0;
            double cy = (verts[a * 3 + 1] + verts[b * 3 + 1] + verts[c * 3 + 1]) / 3.0;
            if (!preparedOutline.Contains(cx, cy))
                continue;

            int areaIndex = -1;
            for (int k = 0; k < preparedLoops.Length; k++)
            {
                if (preparedLoops[k]?.Contains(cx, cy) == true)
                {
                    areaIndex = k;
                    break;
                }
            }

            keptFaces.Add(a);
            keptFaces.Add(b);
            keptFaces.Add(c);
            keptAreaIndex.Add(areaIndex);
        }

        if (keptFaces.Count == 0)
            return null;

        int keptFaceCount = keptFaces.Count / 3;
        int[] keptFaceArray = keptFaces.ToArray();
        if (HasNonManifoldEdge(keptFaceArray, keptFaceCount))
            return null;

        // Compact to the vertices the kept faces reference (drop the skirt-only terrain points).
        var remap = new int[vc];
        for (int i = 0; i < vc; i++)
            remap[i] = -1;

        var compactVertices = new List<double>(vc * 3);
        for (int i = 0; i < keptFaceArray.Length; i++)
        {
            int v = keptFaceArray[i];
            if (remap[v] < 0)
            {
                remap[v] = compactVertices.Count / 3;
                compactVertices.Add(verts[v * 3]);
                compactVertices.Add(verts[v * 3 + 1]);
                compactVertices.Add(verts[v * 3 + 2]);
            }

            keptFaceArray[i] = remap[v];
        }

        return new MeshAreaSplitter.SplitResult(
            compactVertices.ToArray(),
            compactVertices.Count / 3,
            keptFaceArray,
            keptFaceCount,
            keptAreaIndex.ToArray(),
            clippedLoops.Count);
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
        double tolerance,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline>? hardConstraints = null)
    {
        // A daylight loop that runs off the terrain edge is clipped to the terrain outline so the
        // carve region stays closed (following the boundary) instead of leaving an open chain.
        double[]? terrainOutline = TryBuildTerrainOutline(terrainFaces, terrainFaceCount, terrainVertices);

        var areas = new List<MeshAreaSplitter.AreaBoundary>(daylightLoopsXy.Count);
        var clippedLoops = new List<double[]>(daylightLoopsXy.Count);
        foreach (double[] xy in daylightLoopsXy)
        {
            double[] effective = ClipLoopToTerrain(xy, terrainOutline, tolerance);
            int count = effective.Length / 2;
            if (count >= 3)
            {
                areas.Add(new MeshAreaSplitter.AreaBoundary(effective, count));
                clippedLoops.Add(effective);
            }
        }

        if (areas.Count == 0)
            return new SplitOutsideResult { Success = false, Warning = "No usable daylight loops." };

        MeshAreaSplitter.SplitResult? split = MeshAreaSplitter.SplitPreservingTopology(
            terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount, areas.ToArray(), tolerance, out string? warning);

        string handRolledFailure = warning ?? "Terrain split failed.";
        if (split is not null)
        {
            SplitOutsideResult handRolled = ExtractOutsideRegion(split);
            if (handRolled.Success)
                return handRolled;

            handRolledFailure = handRolled.Warning ?? handRolledFailure;
        }

        // The hand-rolled per-face conforming split can emit overlapping slivers (non-manifold or
        // duplicated perimeter edges) where a daylight loop hugs the terrain outline, which makes its
        // hole boundary untraceable. Re-conform with a single Triangle.NET CDT — always a valid,
        // non-overlapping triangulation — and retry the extraction on that.
        MeshAreaSplitter.SplitResult? cdt = SplitConformViaCdt(
            terrainVertices, terrainVertexCount, terrainFaces, terrainFaceCount, clippedLoops, terrainOutline, tolerance, hardConstraints);
        if (cdt is not null)
        {
            SplitOutsideResult viaCdt = ExtractOutsideRegion(cdt);
            if (viaCdt.Success)
                return viaCdt;
        }

        return new SplitOutsideResult { Success = false, Warning = handRolledFailure };
    }

    /// <summary>
    /// Classifies a conformed split into carve (inside) and kept (outside) faces, traces the conformed
    /// hole boundary loops, and repairs pinched/branched boundaries by pulling the outside faces at
    /// irregular vertices into the carve region.
    /// </summary>
    private static SplitOutsideResult ExtractOutsideRegion(MeshAreaSplitter.SplitResult split)
    {
        var baseInsideFlags = new bool[split.FaceCount];
        int insideCount = 0;
        for (int f = 0; f < split.FaceCount; f++)
        {
            if (split.FaceAreaIndex[f] != -1)
            {
                baseInsideFlags[f] = true;
                insideCount++;
            }
        }

        if (insideCount == split.FaceCount)
            return new SplitOutsideResult { Success = false, Warning = "All terrain fell inside the daylight loops." };

        // The conformed daylight boundary = edges separating an inside face from an outside face, OR
        // naked edges of an inside face (where the carve region meets the terrain perimeter because
        // the daylight was clipped to the boundary). ChainBoundaryLoops can only walk degree-2
        // vertices, so a "pinched" boundary (the carve region touching itself or the terrain
        // perimeter at a branch vertex) leaves loops untraced. Repair by pulling the outside faces at
        // each branch vertex into the carve region — the parts merge and the vertex becomes regular —
        // and retry; the enlarged hole is filled and graded like the rest of the carve region.
        const int maxRepairPasses = 4;
        var flags = baseInsideFlags;
        List<int[]>? loops = null;
        List<int[]>? firstPassLoops = null;
        bool[]? firstPassFlags = null;
        string repairTrace = string.Empty;

        for (int pass = 0; pass <= maxRepairPasses; pass++)
        {
            Dictionary<int, List<int>> adjacency = BuildHoleBoundaryAdjacency(split.Faces, split.FaceCount, flags);
            List<int[]> traced = ChainBoundaryLoops(adjacency);

            int totalBoundaryEdges = 0;
            foreach (List<int> neighbours in adjacency.Values)
                totalBoundaryEdges += neighbours.Count;
            totalBoundaryEdges /= 2;

            int consumedEdges = 0;
            foreach (int[] loop in traced)
                consumedEdges += loop.Length;

            if (pass == 0)
            {
                firstPassLoops = traced;
                firstPassFlags = flags;
            }

            if (traced.Count > 0 && consumedEdges == totalBoundaryEdges)
            {
                loops = traced;
                break;
            }

            // Vertices where the boundary graph is not a simple cycle (branch or dead end).
            var irregularVertices = new HashSet<int>();
            foreach (KeyValuePair<int, List<int>> entry in adjacency)
            {
                if (entry.Value.Count != 2)
                    irregularVertices.Add(entry.Key);
            }

            repairTrace += $" pass {pass}: loops={traced.Count} edges={consumedEdges}/{totalBoundaryEdges} irregular={irregularVertices.Count};";
            if (irregularVertices.Count == 0)
                break;

            var repaired = (bool[])flags.Clone();
            bool changed = false;
            for (int f = 0; f < split.FaceCount; f++)
            {
                if (repaired[f])
                    continue;

                if (irregularVertices.Contains(split.Faces[f * 3]) ||
                    irregularVertices.Contains(split.Faces[f * 3 + 1]) ||
                    irregularVertices.Contains(split.Faces[f * 3 + 2]))
                {
                    repaired[f] = true;
                    changed = true;
                }
            }

            if (!changed)
                break;

            flags = repaired;
        }

        if (loops == null)
        {
            // Repair did not converge. Fall back to the pre-repair trace (partial traces were
            // historically accepted; downstream topology gates catch anything unsound).
            if (firstPassLoops == null || firstPassLoops.Count == 0)
            {
                return new SplitOutsideResult
                {
                    Success = false,
                    Warning = "Could not trace a closed conformed daylight boundary (daylight likely reaches the terrain edge)." + repairTrace
                };
            }

            loops = firstPassLoops;
            flags = firstPassFlags!;
        }

        var outsideFaces = new List<int>(split.FaceCount * 3);
        for (int f = 0; f < split.FaceCount; f++)
        {
            if (flags[f])
                continue;

            outsideFaces.Add(split.Faces[f * 3]);
            outsideFaces.Add(split.Faces[f * 3 + 1]);
            outsideFaces.Add(split.Faces[f * 3 + 2]);
        }

        if (outsideFaces.Count == 0)
            return new SplitOutsideResult { Success = false, Warning = "All terrain fell inside the daylight loops." };

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
    /// Boundary graph of the carve region for a given inside/outside face classification: edges
    /// shared by an inside and an outside face, plus naked inside edges (carve region on the terrain
    /// perimeter).
    /// </summary>
    private static Dictionary<int, List<int>> BuildHoleBoundaryAdjacency(int[] faces, int faceCount, bool[] insideFlags)
    {
        var edgeCounts = new Dictionary<long, (int inside, int outside)>(
            Math.Max(8, faceCount * 2),
            IndexedMeshTools.EdgeKeyComparer.Instance);

        void Bump(int a, int b, bool inside)
        {
            long key = IndexedMeshTools.GetEdgeKey(a, b);
            edgeCounts.TryGetValue(key, out (int inside, int outside) c);
            if (inside) c.inside++; else c.outside++;
            edgeCounts[key] = c;
        }

        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            bool inside = insideFlags[f];
            Bump(a, b, inside);
            Bump(b, c, inside);
            Bump(c, a, inside);
        }

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

        return adjacency;
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

        double[] weldedVertices;
        int[] faceArray;
        int weldedVertexCount;

        if (fills.Count > 0 && fills.All(fill => fill.BoundaryTerrainIndex != null))
        {
            // Identity weld (Triangle.NET's preserved Vertex.ID): each fill boundary vertex reproduces
            // a known terrain vertex, so stitch by that shared index â€” reuse the terrain vertex, append
            // only the fill's interior vertices. Exact by construction: no position-dedup tolerance, so
            // the two independent triangulations cannot disagree on the shared boundary.
            (weldedVertices, faceArray) = WeldByIdentity(outsideVertices, outsideFaces, outsideFaceCount, fills);
            weldedVertexCount = weldedVertices.Length / 3;
        }
        else
        {
            var welder = new VertexWelder(weldTolerance);
            var faces = new List<int>(outsideFaceCount * 3);
            var seenFaces = new HashSet<(int, int, int)>();

            AppendMesh(welder, faces, seenFaces, outsideVertices, outsideFaces, outsideFaceCount);
            foreach (SubMesh fill in fills)
                AppendMesh(welder, faces, seenFaces, fill.Vertices, fill.Faces, fill.FaceCount);

            weldedVertices = welder.ToVertexArray();
            faceArray = faces.ToArray();
            weldedVertexCount = welder.Count;
        }

        int faceCount = faceArray.Length / 3;

        // Enforce the watertight 2.5D invariant: zip hairline seam cracks (near-coincident boundary
        // vertices the exact weld missed) and close any remaining interior holes, so the assembly has a
        // single outer boundary. This is what lets the explicit engine succeed on grade-on-grade scenes
        // instead of deferring to the fallback.
        //
        // BUT only when the exact weld actually left a defect: when the assembly is ALREADY a single
        // closed manifold, the crack-repair is at best a no-op and at worst harmful â€” StitchBoundary
        // cracks would merge legitimately-distinct near-coincident vertices on the ORIGINAL terrain
        // outline (tight boundary notches far from any pad), turning a clean mesh non-manifold and
        // forcing the whole explicit path to defer. Skip the repair when nothing needs repairing.
        var weldAnalysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faceArray, faceCount);
        bool repaired = !weldAnalysis.HasSingleClosedBoundaryLoop;
        if (repaired)
        {
            (weldedVertices, faceArray) = MeshTopologyOperations.MakeWatertight(
                weldedVertices, weldedVertexCount, faceArray, faceCount, weldTolerance, out _, out _);
            faceCount = faceArray.Length / 3;
        }

        OrientFacesUpward(weldedVertices, faceArray, faceCount);

        return new AssembledMesh
        {
            Success = true,
            Vertices = weldedVertices,
            VertexCount = weldedVertices.Length / 3,
            Faces = faceArray,
            FaceCount = faceCount,
            BoundaryAnalysis = repaired ? null : weldAnalysis
        };
    }

    /// <summary>
    /// Stitches the fills onto the outside terrain by SHARED VERTEX IDENTITY. A fill boundary vertex
    /// (BoundaryTerrainIndex &gt;= 0) reuses that terrain vertex; interior fill vertices are appended.
    /// Any terrain-vertex merges the fills' deduplicated boundaries implied (near-coincident terrain
    /// vertices the fill collapsed to one) are applied to BOTH the outside faces and the boundary map
    /// via union-find, so both sides agree and nothing dangles. Unreferenced vertices (the carved-away
    /// terrain interior, and merged-away duplicates) are compacted out.
    /// </summary>
    private static (double[] vertices, int[] faces) WeldByIdentity(
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        IReadOnlyList<SubMesh> fills)
    {
        int terrainVertexCount = outsideVertices.Length / 3;

        // Union-find over terrain indices for the implied near-coincident merges.
        var parent = new int[terrainVertexCount];
        for (int i = 0; i < terrainVertexCount; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        foreach (SubMesh fill in fills)
        {
            if (fill.TerrainMerges is null)
                continue;

            foreach ((int from, int to) in fill.TerrainMerges)
            {
                if (from >= 0 && from < terrainVertexCount && to >= 0 && to < terrainVertexCount)
                {
                    int rf = Find(from), rt = Find(to);
                    if (rf != rt)
                        parent[rf] = rt;
                }
            }
        }

        var globalVertices = new List<double>(outsideVertices);
        var rawFaces = new List<int>(outsideFaceCount * 3);
        var seen = new HashSet<(int, int, int)>();

        void AddFace(int a, int b, int c)
        {
            if (a == b || b == c || a == c)
                return;
            if (!seen.Add(SortedTriple(a, b, c)))
                return;

            rawFaces.Add(a);
            rawFaces.Add(b);
            rawFaces.Add(c);
        }

        for (int f = 0; f < outsideFaceCount; f++)
            AddFace(Find(outsideFaces[f * 3]), Find(outsideFaces[f * 3 + 1]), Find(outsideFaces[f * 3 + 2]));

        foreach (SubMesh fill in fills)
        {
            int[] map = fill.BoundaryTerrainIndex!;
            var fillToGlobal = new int[fill.VertexCount];
            for (int v = 0; v < fill.VertexCount; v++)
            {
                if (map[v] >= 0)
                {
                    fillToGlobal[v] = Find(map[v]);
                }
                else
                {
                    fillToGlobal[v] = globalVertices.Count / 3;
                    globalVertices.Add(fill.Vertices[v * 3]);
                    globalVertices.Add(fill.Vertices[v * 3 + 1]);
                    globalVertices.Add(fill.Vertices[v * 3 + 2]);
                }
            }

            for (int f = 0; f < fill.FaceCount; f++)
                AddFace(fillToGlobal[fill.Faces[f * 3]], fillToGlobal[fill.Faces[f * 3 + 1]], fillToGlobal[fill.Faces[f * 3 + 2]]);
        }

        // Compact: keep only vertices a face references (drops carved-away terrain + merged duplicates).
        int total = globalVertices.Count / 3;
        var remap = new int[total];
        for (int i = 0; i < total; i++)
            remap[i] = -1;

        var compactVertices = new List<double>(globalVertices.Count);
        var compactFaces = new int[rawFaces.Count];
        for (int i = 0; i < rawFaces.Count; i++)
        {
            int old = rawFaces[i];
            if (remap[old] < 0)
            {
                remap[old] = compactVertices.Count / 3;
                compactVertices.Add(globalVertices[old * 3]);
                compactVertices.Add(globalVertices[old * 3 + 1]);
                compactVertices.Add(globalVertices[old * 3 + 2]);
            }

            compactFaces[i] = remap[old];
        }

        return (compactVertices.ToArray(), compactFaces);
    }

    /// <summary>Builds the terrain's single naked-edge outline loop as flat XY, or null if it has 0/many.</summary>
    internal static double[]? TryBuildTerrainOutline(int[] faces, int faceCount, double[] vertices)
    {
        var boundary = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundary, IndexedMeshTools.CreateEdgeKeySet(), faces, faceCount);
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
        var visited = IndexedMeshTools.CreateEdgeKeySet(); // visited undirected edges

        foreach (int start in adjacency.Keys)
        {
            foreach (int firstNext in adjacency[start])
            {
                if (visited.Contains(IndexedMeshTools.GetEdgeKey(start, firstNext)))
                    continue;

                var loop = new List<int> { start };
                int prev = start;
                int current = firstNext;
                visited.Add(IndexedMeshTools.GetEdgeKey(start, firstNext));

                while (current != start)
                {
                    loop.Add(current);
                    int next = -1;
                    foreach (int candidate in adjacency[current])
                    {
                        if (candidate == prev)
                            continue;
                        if (visited.Contains(IndexedMeshTools.GetEdgeKey(current, candidate)))
                            continue;
                        next = candidate;
                        break;
                    }

                    if (next < 0)
                        break; // open chain (shouldn't happen for a clean hole); abandon

                    visited.Add(IndexedMeshTools.GetEdgeKey(current, next));
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

    /// <summary>
    /// Merges coincident vertices (within tolerance) as they are appended, merging by actual Euclidean
    /// distance â€” NOT by exact cell match. A naive single-cell hash misses pairs that fall within
    /// tolerance but straddle a cell boundary (e.g. x*100 = 2849.4999 vs 2849.5001 round to different
    /// cells), leaving hairline seam cracks that show up as non-manifold/open edges at the
    /// fill-to-terrain weld. The cell size is 2x the tolerance and Add scans the 2x2x2 cell block
    /// covering [p - tol, p + tol] per axis (Math.Round is monotone, so that block provably contains
    /// every cell a within-tolerance point can hash to): 8 lookups instead of a 3x3x3 scan's 27. Cells
    /// hold a list of representatives because a single cell can contain points farther apart than the
    /// tolerance (cell diagonal = 2*tolerance*sqrt(3)).
    /// </summary>
    private sealed class VertexWelder
    {
        private readonly Dictionary<(long, long, long), List<int>> _cells = new();
        private readonly List<double> _vertices = new();
        private readonly double _inverseCell;
        private readonly double _tolerance;
        private readonly double _toleranceSq;

        public VertexWelder(double tolerance)
        {
            double t = Math.Max(tolerance, 1e-9);
            _tolerance = t;
            _inverseCell = 1.0 / (2.0 * t);
            _toleranceSq = t * t;
        }

        public int Count => _vertices.Count / 3;

        public int Add(double[] vertices, int index)
        {
            double x = vertices[index * 3];
            double y = vertices[index * 3 + 1];
            double z = vertices[index * 3 + 2];
            long cx0 = (long)Math.Round((x - _tolerance) * _inverseCell);
            long cy0 = (long)Math.Round((y - _tolerance) * _inverseCell);
            long cz0 = (long)Math.Round((z - _tolerance) * _inverseCell);

            for (long cx = cx0; cx <= cx0 + 1; cx++)
            {
                for (long cy = cy0; cy <= cy0 + 1; cy++)
                {
                    for (long cz = cz0; cz <= cz0 + 1; cz++)
                    {
                        if (!_cells.TryGetValue((cx, cy, cz), out List<int>? bucket))
                            continue;

                        foreach (int existing in bucket)
                        {
                            double ex = _vertices[existing * 3] - x;
                            double ey = _vertices[existing * 3 + 1] - y;
                            double ez = _vertices[existing * 3 + 2] - z;
                            if ((ex * ex) + (ey * ey) + (ez * ez) <= _toleranceSq)
                                return existing;
                        }
                    }
                }
            }

            int newIndex = _vertices.Count / 3;
            _vertices.Add(x);
            _vertices.Add(y);
            _vertices.Add(z);
            var key = (
                (long)Math.Round(x * _inverseCell),
                (long)Math.Round(y * _inverseCell),
                (long)Math.Round(z * _inverseCell));
            if (!_cells.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>(1);
                _cells[key] = list;
            }

            list.Add(newIndex);
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
        // Guard on BoundaryEdgeCount: the analysis uses HasOpenBoundaryChains=true as a sentinel when
        // there are zero boundary edges (a closed/non-manifold shell), so without this guard a purely
        // non-manifold defect would also emit a spurious "open boundary chains" clause naming a defect
        // that doesn't exist. The sentinel itself is left intact because gates elsewhere rely on it.
        if (assembled.HasOpenBoundaryChains && assembled.BoundaryEdgeCount > 0)
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
