using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>
/// Measures the depressions <see cref="DrainageBasinAnalyzer"/> found: how high the water rises before it
/// escapes, how deep and how much it holds, and where its shoreline runs.
/// </summary>
/// <remarks>
/// <para>
/// The question "how high does it fill" is a **bottleneck** problem, not a shortest-path one. Water
/// escaping a depression does not care about the total climb, only about the highest point it must get
/// over; so the spill elevation is the minimum, over all routes out, of the maximum crossing elevation
/// along that route — a minimax path, found by flooding outward from the floor and always taking the
/// lowest lip available. That is the classic priority flood, on a TIN's dual graph.
/// </para>
/// <para>
/// The naive alternative is tempting and wrong: taking the lowest vertex on the *basin's* boundary. A
/// depression's catchment runs all the way up to the watershed divide, so that boundary usually reaches
/// the terrain edge far below the depression's own lip, and the answer comes back tens of metres too
/// low. The flood is bounded by the basin, but the level is decided by the lip.
/// </para>
/// </remarks>
public static class PondingSolver
{
    public sealed class Options
    {
        /// <summary>
        /// Depressions shallower than this are not reported. A 2 mm numerical dimple on a 200 m pad is
        /// not a pond, and a check that reports one is a check people switch off.
        /// </summary>
        public double MinimumDepth { get; init; } = 0.05;

        /// <summary>Passed through to <see cref="ContourGenerator"/> when tracing the shoreline.</summary>
        public double Tolerance { get; init; } = 1e-6;

        public Func<bool>? CancellationRequested { get; init; }
    }

    public sealed class Pond
    {
        /// <summary>The sink basin this pond fills.</summary>
        public required int BasinIndex { get; init; }

        /// <summary>Water surface elevation once the depression is full and beginning to overflow.</summary>
        public required double SpillZ { get; init; }

        /// <summary>Lowest ground in the depression.</summary>
        public required double FloorZ { get; init; }

        public double MaxDepth => SpillZ - FloorZ;

        /// <summary>Impounded volume at <see cref="SpillZ"/>.</summary>
        public required double Volume { get; init; }

        /// <summary>Plan area of the water surface.</summary>
        public required double PlanArea { get; init; }

        /// <summary>Where it overflows — the low point of the lip it crosses first.</summary>
        public required double SpillX { get; init; }

        public required double SpillY { get; init; }

        /// <summary>True when it overflows off the edge of the terrain rather than onto more ground.</summary>
        public required bool SpillsOffTerrain { get; init; }

        /// <summary>Shoreline loops as flat XYZ, at <see cref="SpillZ"/>.</summary>
        public required IReadOnlyList<double[]> Outlines { get; init; }
    }

    public static IReadOnlyList<Pond> Solve(
        BasinGraph graph,
        double[] vertices,
        int vertexCount,
        int[] faces,
        Options? options = null)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (vertices == null)
            throw new ArgumentNullException(nameof(vertices));
        if (faces == null)
            throw new ArgumentNullException(nameof(faces));

        Options settings = options ?? new Options();
        var probe = CancellationProbe.For(settings.CancellationRequested);
        var ponds = new List<Pond>();

        foreach (BasinGraph.Basin basin in graph.Basins)
        {
            probe.ThrowIfCancelled();
            if (basin.Outlet != BasinGraph.OutletKind.Sink)
                continue;

            Pond? pond = SolveOne(graph, vertices, vertexCount, faces, basin, settings, probe);
            if (pond != null)
                ponds.Add(pond);
        }

        return ponds;
    }

    private static Pond? SolveOne(
        BasinGraph graph,
        double[] vertices,
        int vertexCount,
        int[] faces,
        BasinGraph.Basin basin,
        Options settings,
        CancellationProbe probe)
    {
        int floorFace = FindFloorFace(graph, vertices, faces, basin);
        if (floorFace < 0)
            return null;

        if (!TryFlood(
                graph, vertices, faces, basin, floorFace, probe,
                out double spillZ, out double spillX, out double spillY, out bool spillsOffTerrain))
            return null;

        double floorZ = basin.LowestZ;
        if (spillZ <= floorZ)
            return null;

        // The depth test needs only the spill level, so apply it before measuring and tracing: both walk
        // the basin, and most sinks on a real terrain are numerical dimples this rejects. Same expression
        // as Pond.MaxDepth, so the result is unchanged.
        if (spillZ - floorZ < settings.MinimumDepth)
            return null;

        MeasureFill(graph, vertices, faces, basin, spillZ, out double volume, out double planArea);
        if (volume <= 0.0 || planArea <= 0.0)
            return null;

        return new Pond
        {
            BasinIndex = basin.Index,
            SpillZ = spillZ,
            FloorZ = floorZ,
            Volume = volume,
            PlanArea = planArea,
            SpillX = spillX,
            SpillY = spillY,
            SpillsOffTerrain = spillsOffTerrain,
            Outlines = TraceShoreline(graph, vertices, vertexCount, faces, basin, spillZ, settings)
        };
    }

    /// <summary>The face holding the basin's lowest vertex — where the flood starts.</summary>
    private static int FindFloorFace(BasinGraph graph, double[] vertices, int[] faces, BasinGraph.Basin basin)
    {
        int floorFace = -1;
        double lowest = double.PositiveInfinity;

        for (int face = 0; face < graph.FaceCount; face++)
        {
            if (graph.FaceBasin[face] != basin.Index)
                continue;

            for (int corner = 0; corner < 3; corner++)
            {
                double z = vertices[(faces[(face * 3) + corner] * 3) + 2];
                if (z >= lowest)
                    continue;
                lowest = z;
                floorFace = face;
            }
        }

        return floorFace;
    }

    /// <summary>
    /// Priority flood outward from the floor, always crossing the lowest lip available. The first face
    /// reached that belongs to another basin is the escape — it drains somewhere else by definition —
    /// and the level at which it was reached is the spill elevation. A naked edge is an escape too: the
    /// water leaves the terrain there.
    /// </summary>
    private static bool TryFlood(
        BasinGraph graph,
        double[] vertices,
        int[] faces,
        BasinGraph.Basin basin,
        int floorFace,
        CancellationProbe probe,
        out double spillZ,
        out double spillX,
        out double spillY,
        out bool spillsOffTerrain)
    {
        spillZ = 0.0;
        spillX = 0.0;
        spillY = 0.0;
        spillsOffTerrain = false;

        var visited = new HashSet<int> { floorFace };
        var queue = new PriorityQueue<Crossing, double>();
        EnqueueCrossings(graph, vertices, faces, floorFace, basin.LowestZ, visited, queue);

        while (queue.TryDequeue(out Crossing crossing, out double level))
        {
            probe.ThrowIfCancelledOften();

            // Off the terrain, or onto ground that drains elsewhere: either way the water is away.
            if (crossing.Face < 0 || graph.FaceBasin[crossing.Face] != basin.Index)
            {
                spillZ = level;
                spillX = crossing.X;
                spillY = crossing.Y;
                spillsOffTerrain = crossing.Face < 0;
                return true;
            }

            if (!visited.Add(crossing.Face))
                continue;

            EnqueueCrossings(graph, vertices, faces, crossing.Face, level, visited, queue);
        }

        // Flooded the whole basin without finding a way out. That means the basin's rim is the terrain's
        // own edge everywhere, which the router would have called a boundary outlet, so it should not
        // happen — but reporting no pond is the safe answer if it does.
        return false;
    }

    private static void EnqueueCrossings(
        BasinGraph graph,
        double[] vertices,
        int[] faces,
        int face,
        double level,
        HashSet<int> visited,
        PriorityQueue<Crossing, double> queue)
    {
        for (int edge = 0; edge < 3; edge++)
        {
            int neighbor = graph.Neighbors[(face * 3) + edge];
            if (neighbor >= 0 && visited.Contains(neighbor))
                continue;

            int a = faces[(face * 3) + edge];
            int b = faces[(face * 3) + ((edge + 1) % 3)];
            double za = vertices[(a * 3) + 2];
            double zb = vertices[(b * 3) + 2];

            // Water crosses an edge at the edge's *lowest* point, so that is the level this crossing
            // costs. The route's cost is the highest lip along it — a bottleneck, not a sum.
            double crossingZ = Math.Min(za, zb);
            double routeLevel = Math.Max(level, crossingZ);
            bool aIsLower = za <= zb;

            queue.Enqueue(
                new Crossing(
                    neighbor,
                    aIsLower ? vertices[a * 3] : vertices[b * 3],
                    aIsLower ? vertices[(a * 3) + 1] : vertices[(b * 3) + 1]),
                routeLevel);
        }
    }

    /// <summary>
    /// Impounded volume and water-surface area at the spill level, as the prism sum over the basin's
    /// faces that the earthworks analysis already uses for cut and fill. Faces above the water
    /// contribute nothing, so the basin's dry upper catchment falls out on its own.
    /// </summary>
    private static void MeasureFill(
        BasinGraph graph,
        double[] vertices,
        int[] faces,
        BasinGraph.Basin basin,
        double spillZ,
        out double volume,
        out double planArea)
    {
        volume = 0.0;
        planArea = 0.0;

        for (int face = 0; face < graph.FaceCount; face++)
        {
            if (graph.FaceBasin[face] != basin.Index)
                continue;

            int a = faces[face * 3];
            int b = faces[(face * 3) + 1];
            int c = faces[(face * 3) + 2];
            double ax = vertices[a * 3], ay = vertices[(a * 3) + 1];
            double bx = vertices[b * 3], by = vertices[(b * 3) + 1];
            double cx = vertices[c * 3], cy = vertices[(c * 3) + 1];
            double area = Math.Abs(((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax))) * 0.5;
            if (area <= 0.0)
                continue;

            double centroidZ =
                (vertices[(a * 3) + 2] + vertices[(b * 3) + 2] + vertices[(c * 3) + 2]) / 3.0;
            double depth = spillZ - centroidZ;
            if (depth <= 0.0)
                continue;

            volume += area * depth;
            planArea += area;
        }
    }

    /// <summary>
    /// The shoreline, as the zero level of the per-vertex field <c>z − spillZ</c>. No new tracing code:
    /// a shoreline is a contour, and this is the same marching-triangles pass that draws the cut/fill
    /// balance line. Vertices outside the depression carry NaN, so every face touching one is skipped and
    /// the line stops at the basin rather than wandering across the terrain.
    ///
    /// The sign matters, which is not obvious. <c>z − spillZ</c> is negative under water and zero on the
    /// rim, so the level sits at the field's *maximum*; <c>spillZ − z</c> would put it at the minimum,
    /// which <see cref="ContourGenerator"/> skips — a level equal to the minimum has no below-to-above
    /// transition to find. That is not a fringe case: a bunded pad, where the whole depression lies at or
    /// below its rim and nothing is above water at all, is the common one, and it would silently draw no
    /// shoreline. The inclusive upper bound this relies on is the same one added so a contour at a pad's
    /// exact design elevation still draws its outline.
    /// </summary>
    private static IReadOnlyList<double[]> TraceShoreline(
        BasinGraph graph,
        double[] vertices,
        int vertexCount,
        int[] faces,
        BasinGraph.Basin basin,
        double spillZ,
        Options settings)
    {
        var field = new double[vertexCount];
        Array.Fill(field, double.NaN);

        for (int face = 0; face < graph.FaceCount; face++)
        {
            if (graph.FaceBasin[face] != basin.Index)
                continue;

            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = faces[(face * 3) + corner];
                field[vertex] = vertices[(vertex * 3) + 2] - spillZ;
            }
        }

        List<ContourLevel> levels = ContourGenerator.Generate(
            vertices, vertexCount, faces, graph.FaceCount, field, new[] { 0.0 }, settings.Tolerance);

        var outlines = new List<double[]>();
        foreach (ContourLevel level in levels)
        {
            foreach (ContourPolyline polyline in level.Polylines)
            {
                if (polyline.PointCount >= 3)
                    outlines.Add(polyline.PointsXyz);
            }
        }

        return outlines;
    }

    /// <summary>A way out of the flooded region: the face beyond it (-1 off the terrain) and where it crosses.</summary>
    private readonly record struct Crossing(int Face, double X, double Y);
}
