using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>
/// Segments a 2.5D triangle mesh into drainage basins: which faces drain to which outlet, and whether
/// that outlet is the terrain edge or a closed depression.
/// </summary>
/// <remarks>
/// <para>
/// Routing follows each face's own plane gradient, the same quantity <see cref="WaterflowTracer"/>
/// follows, so a traced path and a catchment boundary cannot disagree about which way the ground falls.
/// </para>
/// <para>
/// The hard part is flat ground, and it is the reason this is usable on graded terrain at all. A graded
/// pad is one enormous flat area; routed naively, every face on it picks a different arbitrary outlet
/// out of rounding noise and the catchment map becomes confetti. Flat faces are therefore grouped into
/// regions and routed as a unit, breadth-first inward from the edges where the region actually spills
/// (the classic Garbrecht–Martz treatment, on a TIN rather than a grid). A region with nowhere to spill
/// is not a failure — it is a depression, and reporting it is half the point of this file.
/// </para>
/// </remarks>
public static class DrainageBasinAnalyzer
{
    public sealed class Options
    {
        /// <summary>
        /// Ground falling less steeply than this (as rise over run) is treated as flat. Never zero in
        /// practice: a survey-derived surface is never exactly level, and routing its noise face by face
        /// is exactly the confetti this class exists to avoid.
        /// </summary>
        public double FlatSlopeRatio { get; init; } = 0.005;

        /// <summary>
        /// Basins smaller than this share of the routed plan area are merged into the neighbour they
        /// spill into. Zero keeps every basin. A real survey yields hundreds of slivers, so a catchment
        /// map is close to unreadable without this.
        /// </summary>
        public double MinimumBasinAreaShare { get; init; }

        /// <summary>Safety cap on merge rounds; each round merges every basin still under the threshold.</summary>
        public int MaxMergeRounds { get; init; } = 16;

        public Func<bool>? CancellationRequested { get; init; }
    }

    public static BasinGraph Analyze(
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        int faceCount,
        Options? options = null)
    {
        if (vertices == null)
            throw new ArgumentNullException(nameof(vertices));
        if (faces == null)
            throw new ArgumentNullException(nameof(faces));
        if (vertexCount < 0 || vertexCount * 3 > vertices.Count)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (faceCount < 0 || faceCount * 3 > faces.Count)
            throw new ArgumentOutOfRangeException(nameof(faceCount));

        Options settings = options ?? new Options();
        var probe = CancellationProbe.For(settings.CancellationRequested);

        if (faceCount == 0)
        {
            return new BasinGraph
            {
                FaceBasin = Array.Empty<int>(),
                FlowsTo = Array.Empty<int>(),
                FaceCount = 0,
                Neighbors = Array.Empty<int>(),
                Basins = Array.Empty<BasinGraph.Basin>(),
                FlatFaceCount = 0,
                TotalPlanArea = 0.0
            };
        }

        int[] neighbors = FaceAdjacency.Build(faces, faceCount, vertexCount, probe);
        var geometry = FaceGeometry.Build(vertices, vertexCount, faces, faceCount, settings.FlatSlopeRatio, probe);

        var routing = new Routing(faceCount);
        RouteSlopedFaces(vertices, faces, faceCount, neighbors, geometry, routing, probe);
        RouteFlatRegions(faceCount, neighbors, geometry, routing, probe);
        int[] rootOfFace = ResolveRoots(faceCount, neighbors, geometry, routing, probe);

        int[] faceBasin = LabelBasins(faceCount, rootOfFace, out List<int> basinRoot);
        ConsolidateSinks(vertices, faces, faceCount, neighbors, faceBasin, basinRoot, routing, probe);
        if (settings.MinimumBasinAreaShare > 0.0 && basinRoot.Count > 1)
            MergeSmallBasins(vertices, faces, faceCount, neighbors, geometry, faceBasin, basinRoot, routing, settings, probe);

        IReadOnlyList<BasinGraph.Basin> basins = BuildBasins(
            vertices, faces, faceCount, geometry, routing, faceBasin, basinRoot, out double totalPlanArea);

        return new BasinGraph
        {
            FaceBasin = faceBasin,
            FlowsTo = routing.FlowsTo,
            FaceCount = faceCount,
            Neighbors = neighbors,
            Basins = basins,
            FlatFaceCount = geometry.FlatFaceCount,
            TotalPlanArea = totalPlanArea
        };
    }

    /// <summary>
    /// The routing state the three passes share. <see cref="OutletKind"/> is written where an outlet is
    /// *created*, never inferred afterwards: whether a basin has somewhere to go is known exactly at
    /// that moment, and reconstructing it later from the geometry is how a sink gets mistaken for an
    /// edge outlet.
    /// </summary>
    private sealed class Routing
    {
        public Routing(int faceCount)
        {
            FlowsTo = new int[faceCount];
            Array.Fill(FlowsTo, -1);
            Routed = new bool[faceCount];
            IsOutlet = new bool[faceCount];
            OutletKind = new BasinGraph.OutletKind[faceCount];
        }

        public int[] FlowsTo { get; }

        public bool[] Routed { get; }

        public bool[] IsOutlet { get; }

        public BasinGraph.OutletKind[] OutletKind { get; }

        public void MarkOutlet(int face, BasinGraph.OutletKind kind)
        {
            FlowsTo[face] = -1;
            Routed[face] = true;
            IsOutlet[face] = true;
            OutletKind[face] = kind;
        }

        /// <summary>Clears a face's routing so a re-route can decide it again from scratch.</summary>
        public void Reopen(int face)
        {
            FlowsTo[face] = -1;
            Routed[face] = false;
            IsOutlet[face] = false;
            OutletKind[face] = BasinGraph.OutletKind.Boundary;
        }

        public void MarkFlowsTo(int face, int downstream)
        {
            FlowsTo[face] = downstream;
            Routed[face] = true;
            IsOutlet[face] = false;
        }
    }

    /// <summary>
    /// Per-face plan geometry and gradient, computed once. <see cref="IsFlat"/> covers both genuinely
    /// level ground and faces with no usable plane at all — a vertical retaining-wall face has no
    /// gradient and no plan area, and routing it as flat lets it spill onto the ground below instead of
    /// standing as a phantom depression behind every wall.
    /// </summary>
    private sealed class FaceGeometry
    {
        public required double[] CentroidX { get; init; }

        public required double[] CentroidY { get; init; }

        public required double[] CentroidZ { get; init; }

        public required double[] PlanArea { get; init; }

        /// <summary>Steepest-descent direction in XY, zero-length for a flat or degenerate face.</summary>
        public required double[] DescentX { get; init; }

        public required double[] DescentY { get; init; }

        public required bool[] IsFlat { get; init; }

        /// <summary>Faces whose vertex indices are unusable: routed nowhere, owned by no basin.</summary>
        public required bool[] IsExcluded { get; init; }

        public required int FlatFaceCount { get; init; }

        public static FaceGeometry Build(
            IReadOnlyList<double> vertices,
            int vertexCount,
            IReadOnlyList<int> faces,
            int faceCount,
            double flatSlopeRatio,
            CancellationProbe probe)
        {
            double flatThreshold = Math.Max(0.0, flatSlopeRatio);
            var centroidX = new double[faceCount];
            var centroidY = new double[faceCount];
            var centroidZ = new double[faceCount];
            var planArea = new double[faceCount];
            var descentX = new double[faceCount];
            var descentY = new double[faceCount];
            var isFlat = new bool[faceCount];
            var isExcluded = new bool[faceCount];
            int flatCount = 0;

            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                probe.ThrowIfCancelledOften();
                int a = faces[faceIndex * 3];
                int b = faces[(faceIndex * 3) + 1];
                int c = faces[(faceIndex * 3) + 2];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount ||
                    a == b || b == c || a == c)
                {
                    isExcluded[faceIndex] = true;
                    continue;
                }

                double ax = vertices[a * 3];
                double ay = vertices[(a * 3) + 1];
                double az = vertices[(a * 3) + 2];
                double bx = vertices[b * 3];
                double by = vertices[(b * 3) + 1];
                double bz = vertices[(b * 3) + 2];
                double cx = vertices[c * 3];
                double cy = vertices[(c * 3) + 1];
                double cz = vertices[(c * 3) + 2];

                centroidX[faceIndex] = (ax + bx + cx) / 3.0;
                centroidY[faceIndex] = (ay + by + cy) / 3.0;
                centroidZ[faceIndex] = (az + bz + cz) / 3.0;

                double ux = bx - ax, uy = by - ay, uz = bz - az;
                double vx = cx - ax, vy = cy - ay, vz = cz - az;
                double nx = (uy * vz) - (uz * vy);
                double ny = (uz * vx) - (ux * vz);
                double nz = (ux * vy) - (uy * vx);
                planArea[faceIndex] = Math.Abs(nz) * 0.5;

                if (Math.Abs(nz) <= 1e-14)
                {
                    isFlat[faceIndex] = true;
                    flatCount++;
                    continue;
                }

                double gradientX = -nx / nz;
                double gradientY = -ny / nz;
                double slopeRatio = Math.Sqrt((gradientX * gradientX) + (gradientY * gradientY));
                if (!double.IsFinite(slopeRatio) || slopeRatio <= flatThreshold)
                {
                    isFlat[faceIndex] = true;
                    flatCount++;
                    continue;
                }

                // Descent is the negated gradient, matching WaterflowTracer's step direction exactly.
                descentX[faceIndex] = -gradientX;
                descentY[faceIndex] = -gradientY;
            }

            return new FaceGeometry
            {
                CentroidX = centroidX,
                CentroidY = centroidY,
                CentroidZ = centroidZ,
                PlanArea = planArea,
                DescentX = descentX,
                DescentY = descentY,
                IsFlat = isFlat,
                IsExcluded = isExcluded,
                FlatFaceCount = flatCount
            };
        }
    }

    /// <summary>
    /// Every sloped face drains through the edge its descent ray leaves by. A naked exit edge makes the
    /// face an outlet; anything else makes it point at its neighbour.
    /// </summary>
    private static void RouteSlopedFaces(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceCount,
        int[] neighbors,
        FaceGeometry geometry,
        Routing routing,
        CancellationProbe probe)
    {
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            probe.ThrowIfCancelledOften();
            if (geometry.IsExcluded[faceIndex] || geometry.IsFlat[faceIndex])
                continue;

            int exitEdge = FindExitEdge(vertices, faces, faceIndex, geometry);
            if (exitEdge < 0)
            {
                // No edge the descent ray leaves by — numerically possible on a sliver. Hand it to the
                // flat pass rather than inventing a direction for it.
                geometry.IsFlat[faceIndex] = true;
                continue;
            }

            int neighbor = neighbors[(faceIndex * 3) + exitEdge];
            if (neighbor < 0 || geometry.IsExcluded[neighbor])
                routing.MarkOutlet(faceIndex, BasinGraph.OutletKind.Boundary);
            else
                routing.MarkFlowsTo(faceIndex, neighbor);
        }
    }

    /// <summary>
    /// The edge the ray from the face centroid along the descent direction crosses first. A ray cast,
    /// not the largest dot product with an edge normal: near a corner those two disagree, and the ray is
    /// the one that says where water actually goes.
    /// </summary>
    private static int FindExitEdge(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceIndex,
        FaceGeometry geometry)
    {
        double cx = geometry.CentroidX[faceIndex];
        double cy = geometry.CentroidY[faceIndex];
        double dx = geometry.DescentX[faceIndex];
        double dy = geometry.DescentY[faceIndex];

        int bestEdge = -1;
        double bestT = double.PositiveInfinity;

        for (int edge = 0; edge < 3; edge++)
        {
            int a = faces[(faceIndex * 3) + edge];
            int b = faces[(faceIndex * 3) + ((edge + 1) % 3)];
            double ax = vertices[a * 3];
            double ay = vertices[(a * 3) + 1];
            double ex = vertices[b * 3] - ax;
            double ey = vertices[(b * 3) + 1] - ay;

            double denominator = Cross(ex, ey, dx, dy);
            if (Math.Abs(denominator) <= 1e-15)
                continue;

            double s = Cross(cx - ax, cy - ay, dx, dy) / denominator;
            if (s < -1e-9 || s > 1.0 + 1e-9)
                continue;

            double hitX = ax + (s * ex);
            double hitY = ay + (s * ey);
            double t = Math.Abs(dx) >= Math.Abs(dy) ? (hitX - cx) / dx : (hitY - cy) / dy;
            if (t <= 1e-12 || t >= bestT)
                continue;

            bestT = t;
            bestEdge = edge;
        }

        return bestEdge;
    }

    /// <summary>
    /// Groups flat faces into connected regions and routes each region as a unit: breadth-first inward
    /// from every edge the region spills across, so the whole region drains coherently to its real
    /// outlets instead of to rounding noise. A region with no such edge is a depression, and its lowest
    /// face becomes a sink outlet that everything else in the region points at.
    /// </summary>
    private static void RouteFlatRegions(
        int faceCount,
        int[] neighbors,
        FaceGeometry geometry,
        Routing routing,
        CancellationProbe probe)
    {
        var visited = new bool[faceCount];
        var inRegion = new bool[faceCount];
        var region = new List<int>();
        var stack = new Stack<int>();
        var queue = new Queue<int>();

        for (int seed = 0; seed < faceCount; seed++)
        {
            probe.ThrowIfCancelledOften();
            if (visited[seed] || geometry.IsExcluded[seed] || !geometry.IsFlat[seed])
                continue;

            region.Clear();
            stack.Clear();
            stack.Push(seed);
            visited[seed] = true;
            while (stack.Count > 0)
            {
                int face = stack.Pop();
                region.Add(face);
                inRegion[face] = true;
                for (int edge = 0; edge < 3; edge++)
                {
                    int neighbor = neighbors[(face * 3) + edge];
                    if (neighbor < 0 || visited[neighbor] || geometry.IsExcluded[neighbor] || !geometry.IsFlat[neighbor])
                        continue;
                    visited[neighbor] = true;
                    stack.Push(neighbor);
                }
            }

            RouteOneFlatRegion(region, inRegion, neighbors, geometry, routing, queue, probe);

            foreach (int face in region)
                inRegion[face] = false;
        }
    }

    private static void RouteOneFlatRegion(
        List<int> region,
        bool[] inRegion,
        int[] neighbors,
        FaceGeometry geometry,
        Routing routing,
        Queue<int> queue,
        CancellationProbe probe)
    {
        queue.Clear();

        // Seeds first: faces owning an edge the region spills across. A naked edge spills off the
        // terrain; an edge to a lower face outside the region spills onto it.
        foreach (int face in region)
        {
            probe.ThrowIfCancelledOften();
            int bestEdge = -1;
            double bestZ = double.PositiveInfinity;
            bool spillsOffTerrain = false;

            for (int edge = 0; edge < 3; edge++)
            {
                int neighbor = neighbors[(face * 3) + edge];
                if (neighbor < 0)
                {
                    spillsOffTerrain = true;
                    continue;
                }

                if (inRegion[neighbor] || geometry.IsExcluded[neighbor])
                    continue;
                if (geometry.CentroidZ[neighbor] >= geometry.CentroidZ[face])
                    continue;
                if (geometry.CentroidZ[neighbor] >= bestZ)
                    continue;

                bestZ = geometry.CentroidZ[neighbor];
                bestEdge = edge;
            }

            // A face that both touches the terrain edge and has a lower neighbour spills to the lower
            // neighbour: staying on the surface is the more informative answer, and that neighbour's own
            // basin carries the water off the edge if that is where it ends up.
            if (bestEdge >= 0)
            {
                routing.MarkFlowsTo(face, neighbors[(face * 3) + bestEdge]);
                queue.Enqueue(face);
            }
            else if (spillsOffTerrain)
            {
                routing.MarkOutlet(face, BasinGraph.OutletKind.Boundary);
                queue.Enqueue(face);
            }
        }

        if (queue.Count == 0)
        {
            // No outflow anywhere: this region is a depression. Its lowest face becomes the outlet and
            // the rest of the region drains inward to it.
            int lowest = region[0];
            foreach (int face in region)
            {
                if (geometry.CentroidZ[face] < geometry.CentroidZ[lowest])
                    lowest = face;
            }

            routing.MarkOutlet(lowest, BasinGraph.OutletKind.Sink);
            queue.Enqueue(lowest);
        }

        while (queue.Count > 0)
        {
            probe.ThrowIfCancelledOften();
            int face = queue.Dequeue();
            for (int edge = 0; edge < 3; edge++)
            {
                int neighbor = neighbors[(face * 3) + edge];
                if (neighbor < 0 || !inRegion[neighbor] || routing.Routed[neighbor])
                    continue;

                routing.MarkFlowsTo(neighbor, face);
                queue.Enqueue(neighbor);
            }
        }
    }

    /// <summary>
    /// Follows every face's pointers to the outlet it resolves to, with path compression.
    /// </summary>
    /// <remarks>
    /// Cycles are not an edge case here, they are what happens wherever water converges on a *vertex*:
    /// the two faces sharing that vertex's opposite edge each fall towards it, each leaves through their
    /// shared edge, and face-to-face routing goes round for ever. Calling that a depression would put a
    /// phantom pond at the bottom of every valley and on the low corner of every graded pad.
    ///
    /// So a cycle is treated as exactly what it is — a connected set of faces with no outlet among
    /// themselves, which is the same thing a flat region is — and handed to the same spill routine. It
    /// becomes a sink only when that routine finds nowhere for it to go.
    /// </remarks>
    private static int[] ResolveRoots(
        int faceCount,
        int[] neighbors,
        FaceGeometry geometry,
        Routing routing,
        CancellationProbe probe)
    {
        var rootOfFace = new int[faceCount];
        Array.Fill(rootOfFace, -1);
        var stamp = new int[faceCount];
        Array.Fill(stamp, -1);
        var path = new List<int>();
        var cycle = new List<int>();
        var inCycle = new bool[faceCount];
        var queue = new Queue<int>();

        // Every re-route strictly re-opens one cycle, and a cycle cannot re-form from the same faces
        // twice without the spill routine having found somewhere lower. The budget is a backstop against
        // a pathological mesh, not part of the algorithm: exhausting it falls back to calling the cycle
        // a sink, which is the conservative answer.
        int rerouteBudget = faceCount + 16;
        int walkId = 0;

        for (int start = 0; start < faceCount; start++)
        {
            probe.ThrowIfCancelledOften();
            if (geometry.IsExcluded[start] || !routing.Routed[start] || rootOfFace[start] >= 0)
                continue;

            int current = start;
            int root = -1;
            walkId++;
            path.Clear();

            while (root < 0)
            {
                if (rootOfFace[current] >= 0)
                {
                    root = rootOfFace[current];
                    break;
                }

                if (routing.IsOutlet[current])
                {
                    root = current;
                    break;
                }

                if (stamp[current] == walkId)
                {
                    int cycleStart = path.LastIndexOf(current);
                    cycle.Clear();
                    for (int index = cycleStart; index < path.Count; index++)
                    {
                        cycle.Add(path[index]);
                        inCycle[path[index]] = true;
                        routing.Reopen(path[index]);
                    }

                    if (rerouteBudget-- > 0)
                        RouteOneFlatRegion(cycle, inCycle, neighbors, geometry, routing, queue, probe);
                    else
                        routing.MarkOutlet(LowestByCentroid(cycle, geometry), BasinGraph.OutletKind.Sink);

                    foreach (int face in cycle)
                        inCycle[face] = false;

                    // Whether it spilled or became a sink, the pointers under this walk have changed.
                    // Start again rather than trusting anything recorded before the re-route.
                    current = start;
                    walkId++;
                    path.Clear();
                    continue;
                }

                stamp[current] = walkId;
                path.Add(current);

                int next = routing.FlowsTo[current];
                if (next < 0 || geometry.IsExcluded[next] || !routing.Routed[next])
                {
                    // Points at nothing usable — a hole in the mesh. Water leaves the surface there,
                    // which is an edge outlet, not a depression.
                    routing.MarkOutlet(current, BasinGraph.OutletKind.Boundary);
                    root = current;
                    break;
                }

                current = next;
            }

            foreach (int face in path)
                rootOfFace[face] = root;
            rootOfFace[root] = root;
        }

        return rootOfFace;
    }

    private static int LowestByCentroid(List<int> faces, FaceGeometry geometry)
    {
        int lowest = faces[0];
        foreach (int face in faces)
        {
            if (geometry.CentroidZ[face] < geometry.CentroidZ[lowest])
                lowest = face;
        }

        return lowest;
    }

    private static int[] LabelBasins(int faceCount, int[] rootOfFace, out List<int> basinRoot)
    {
        var faceBasin = new int[faceCount];
        Array.Fill(faceBasin, -1);
        basinRoot = new List<int>();
        var indexOfRoot = new Dictionary<int, int>();

        for (int face = 0; face < faceCount; face++)
        {
            int root = rootOfFace[face];
            if (root < 0)
                continue;

            if (!indexOfRoot.TryGetValue(root, out int basin))
            {
                basin = basinRoot.Count;
                indexOfRoot.Add(root, basin);
                basinRoot.Add(root);
            }

            faceBasin[face] = basin;
        }

        return faceBasin;
    }

    /// <summary>
    /// Merges sink basins that are really one depression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Water converging on a pit arrives from every side, and the routing splits it between however many
    /// cycles form around the low ground — a round bowl comes out as two half-bowls. They are one
    /// depression: a pond has one water surface, and reporting two would double-count its volume and draw
    /// two outlines over each other.
    /// </para>
    /// <para>
    /// Two rules, because the obvious one is not enough. Sinks that bottom out at the *same vertex* are
    /// plainly one pit. But the split does not always land that neatly: a bowl can divide into a piece
    /// holding the true low point and a piece whose own lowest vertex is a little higher, and those share
    /// no floor. So sinks are also merged when they are joined below the higher of their two floors —
    /// water standing at that floor already spans both, which is precisely what makes them one body of
    /// water. Joined *above* both floors is a bund between two ponds, and stays two.
    /// </para>
    /// <para>
    /// Left unmerged, such a pair is worse than cosmetic: each one's escape route runs straight into the
    /// other at its own floor level, so both measure a spill equal to their floor, both report zero
    /// depth, and a real depression is reported as no pond at all.
    /// </para>
    /// </remarks>
    private static void ConsolidateSinks(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceCount,
        int[] neighbors,
        int[] faceBasin,
        List<int> basinRoot,
        Routing routing,
        CancellationProbe probe)
    {
        int basinCount = basinRoot.Count;
        if (basinCount < 2)
            return;

        var floorVertex = new int[basinCount];
        var floorZ = new double[basinCount];
        Array.Fill(floorVertex, -1);
        Array.Fill(floorZ, double.PositiveInfinity);

        for (int face = 0; face < faceCount; face++)
        {
            probe.ThrowIfCancelledOften();
            int basin = faceBasin[face];
            if (basin < 0 || routing.OutletKind[basinRoot[basin]] != BasinGraph.OutletKind.Sink)
                continue;

            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = faces[(face * 3) + corner];
                double z = vertices[(vertex * 3) + 2];
                // Ties break on the lower vertex index so two basins meeting at a level floor agree on
                // which vertex names it, whatever order their faces were visited in.
                if (z > floorZ[basin] || (z == floorZ[basin] && vertex >= floorVertex[basin]))
                    continue;
                floorZ[basin] = z;
                floorVertex[basin] = vertex;
            }
        }

        var parent = new int[basinCount];
        for (int basin = 0; basin < basinCount; basin++)
            parent[basin] = basin;

        bool merged = false;

        // Rule one: a shared floor vertex is the same pit, whatever the routing made of it.
        var keeperOfFloor = new Dictionary<int, int>();
        for (int basin = 0; basin < basinCount; basin++)
        {
            if (floorVertex[basin] < 0)
                continue;

            if (!keeperOfFloor.TryGetValue(floorVertex[basin], out int keeper))
            {
                keeperOfFloor.Add(floorVertex[basin], basin);
                continue;
            }

            parent[Find(parent, basin)] = Find(parent, keeper);
            merged = true;
        }

        // Rule two: joined below the higher floor. Lowest joins first, so a chain of pits merges from the
        // bottom up and the answer cannot depend on the order faces happened to be visited in.
        var joins = new List<(double CrossingZ, int From, int To)>();
        for (int face = 0; face < faceCount; face++)
        {
            probe.ThrowIfCancelledOften();
            int from = faceBasin[face];
            if (from < 0 || floorVertex[from] < 0)
                continue;

            for (int edge = 0; edge < 3; edge++)
            {
                int neighbor = neighbors[(face * 3) + edge];
                if (neighbor < 0)
                    continue;
                int to = faceBasin[neighbor];
                if (to < 0 || to == from || floorVertex[to] < 0 || to < from)
                    continue;

                int a = faces[(face * 3) + edge];
                int b = faces[(face * 3) + ((edge + 1) % 3)];
                joins.Add((Math.Min(vertices[(a * 3) + 2], vertices[(b * 3) + 2]), from, to));
            }
        }

        joins.Sort((left, right) => left.CrossingZ.CompareTo(right.CrossingZ));
        foreach ((double crossingZ, int from, int to) in joins)
        {
            int rootFrom = Find(parent, from);
            int rootTo = Find(parent, to);
            if (rootFrom == rootTo)
                continue;
            if (crossingZ > Math.Max(floorZ[rootFrom], floorZ[rootTo]))
                continue;

            // The survivor keeps the lower floor, so it is the one that actually holds the water.
            int keeper = floorZ[rootFrom] <= floorZ[rootTo] ? rootFrom : rootTo;
            int absorbed = keeper == rootFrom ? rootTo : rootFrom;
            parent[absorbed] = keeper;
            floorZ[keeper] = Math.Min(floorZ[keeper], floorZ[absorbed]);
            merged = true;
        }

        if (!merged)
            return;

        var survivor = new int[basinCount];
        for (int basin = 0; basin < basinCount; basin++)
            survivor[basin] = Find(parent, basin);

        for (int face = 0; face < faceCount; face++)
        {
            int basin = faceBasin[face];
            if (basin >= 0)
                faceBasin[face] = survivor[basin];
        }

        // The absorbed basin's outlet face is no longer the basin's outlet, so point it on towards the
        // one that kept the floor — otherwise "follow the pointers to your basin's outlet" quietly stops
        // being true for half a bowl. A candidate is accepted only once it is *shown* to reach the keeper,
        // which is what stops two absorbed halves from being pointed at each other into a fresh cycle.
        for (int basin = 0; basin < basinCount; basin++)
        {
            if (survivor[basin] == basin)
                continue;

            int absorbedRoot = basinRoot[basin];
            int keeperRoot = basinRoot[survivor[basin]];
            for (int edge = 0; edge < 3; edge++)
            {
                int neighbor = neighbors[(absorbedRoot * 3) + edge];
                if (neighbor < 0 || neighbor == absorbedRoot || faceBasin[neighbor] != survivor[basin])
                    continue;
                if (!Reaches(routing.FlowsTo, neighbor, keeperRoot, faceCount))
                    continue;

                routing.MarkFlowsTo(absorbedRoot, neighbor);
                break;
            }
        }
    }

    /// <summary>Whether following <paramref name="flowsTo"/> from a face arrives at <paramref name="target"/>.</summary>
    private static bool Reaches(int[] flowsTo, int from, int target, int faceCount)
    {
        int current = from;
        for (int step = 0; step <= faceCount; step++)
        {
            if (current == target)
                return true;
            if (flowsTo[current] < 0)
                return false;
            current = flowsTo[current];
        }

        return false;
    }

    /// <summary>
    /// Absorbs slivers into the basin they spill into, lowest rim first. Rounds rather than one basin at
    /// a time: a real survey produces hundreds of slivers, and re-scanning every face once per merge
    /// would dominate the whole analysis. Chains formed within a round (A into B while B merges into C)
    /// resolve through the union-find, so the order faces are visited in cannot change the outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **Depressions are exempt, in both directions.** Merging exists to stop a survey's hundreds of
    /// sliver catchments making the map unreadable — it is a tidying-up of things that are merely
    /// numerous. A depression is not one of those: it is the finding the drainage analyses exist to
    /// report, and a small one is still a place water stands. Absorbed into a neighbour it disappears
    /// from the graph entirely, so the Catchments card would say "no closed depressions" while the
    /// Ponding card beside it, which routes unmerged, reports one — the two cards contradicting each
    /// other on screen.
    /// </para>
    /// <para>
    /// The other direction matters as much and is less obvious: a sliver absorbed *into* a depression
    /// extends it, and since a pond's spill is found by flooding until the water reaches a face of a
    /// different basin, a larger basin pushes that escape further out and the pond is measured too deep.
    /// </para>
    /// </remarks>
    private static void MergeSmallBasins(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceCount,
        int[] neighbors,
        FaceGeometry geometry,
        int[] faceBasin,
        List<int> basinRoot,
        Routing routing,
        Options settings,
        CancellationProbe probe)
    {
        int basinCount = basinRoot.Count;
        var isSink = new bool[basinCount];
        for (int basin = 0; basin < basinCount; basin++)
            isSink[basin] = routing.OutletKind[basinRoot[basin]] == BasinGraph.OutletKind.Sink;

        var parent = new int[basinCount];
        for (int basin = 0; basin < basinCount; basin++)
            parent[basin] = basin;

        var areas = new double[basinCount];
        double totalArea = 0.0;
        for (int face = 0; face < faceCount; face++)
        {
            int basin = faceBasin[face];
            if (basin < 0)
                continue;
            areas[basin] += geometry.PlanArea[face];
            totalArea += geometry.PlanArea[face];
        }

        double threshold = totalArea * settings.MinimumBasinAreaShare;
        if (threshold <= 0.0)
            return;

        var target = new int[basinCount];
        var spill = new double[basinCount];

        for (int round = 0; round < Math.Max(1, settings.MaxMergeRounds); round++)
        {
            probe.ThrowIfCancelled();
            Array.Fill(target, -1);
            Array.Fill(spill, double.PositiveInfinity);

            bool anySmall = false;
            for (int basin = 0; basin < basinCount; basin++)
            {
                if (!isSink[basin] && Find(parent, basin) == basin && areas[basin] > 0.0 && areas[basin] < threshold)
                    anySmall = true;
            }

            if (!anySmall)
                break;

            for (int face = 0; face < faceCount; face++)
            {
                probe.ThrowIfCancelledOften();
                int basinOfFace = faceBasin[face];
                if (basinOfFace < 0)
                    continue;
                int fromBasin = Find(parent, basinOfFace);

                for (int edge = 0; edge < 3; edge++)
                {
                    int neighbor = neighbors[(face * 3) + edge];
                    if (neighbor < 0 || faceBasin[neighbor] < 0)
                        continue;
                    int toBasin = Find(parent, faceBasin[neighbor]);
                    if (toBasin == fromBasin || isSink[toBasin])
                        continue;

                    // The elevation water must reach to cross this rim edge: the higher of its two ends.
                    int a = faces[(face * 3) + edge];
                    int b = faces[(face * 3) + ((edge + 1) % 3)];
                    double rimZ = Math.Max(vertices[(a * 3) + 2], vertices[(b * 3) + 2]);

                    if (!isSink[fromBasin] && areas[fromBasin] < threshold && rimZ < spill[fromBasin])
                    {
                        spill[fromBasin] = rimZ;
                        target[fromBasin] = toBasin;
                    }
                }
            }

            bool merged = false;
            for (int basin = 0; basin < basinCount; basin++)
            {
                if (target[basin] < 0)
                    continue;

                int from = Find(parent, basin);
                int to = Find(parent, target[basin]);
                if (from == to)
                    continue;

                // The survivor is the basin absorbing the sliver, so it keeps its outlet and its root.
                parent[from] = to;
                areas[to] += areas[from];
                areas[from] = 0.0;
                merged = true;
            }

            if (!merged)
                break;
        }

        // The union-find is the record of what merged; faceBasin is what every later pass reads. Applying
        // it here, once, is what keeps the two from disagreeing.
        for (int face = 0; face < faceCount; face++)
        {
            if (faceBasin[face] >= 0)
                faceBasin[face] = Find(parent, faceBasin[face]);
        }
    }

    private static int Find(int[] parent, int index)
    {
        int root = index;
        while (parent[root] != root)
            root = parent[root];
        while (parent[index] != root)
        {
            int next = parent[index];
            parent[index] = root;
            index = next;
        }

        return root;
    }

    /// <summary>
    /// Measures each surviving basin and renumbers by descending plan area, so the large basins keep
    /// their index — and a categorical colouring keeps its colours — when a small one appears, merges
    /// away, or shifts after an edit.
    /// </summary>
    private static IReadOnlyList<BasinGraph.Basin> BuildBasins(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceCount,
        FaceGeometry geometry,
        Routing routing,
        int[] faceBasin,
        List<int> basinRoot,
        out double totalPlanArea)
    {
        int rawCount = basinRoot.Count;
        var faceCounts = new int[rawCount];
        var areas = new double[rawCount];
        var lowestZ = new double[rawCount];
        var lowestX = new double[rawCount];
        var lowestY = new double[rawCount];
        // Two candidates per basin: the highest face that falls, and the highest face of any kind. The
        // first is the flow-path head; the second only matters for a basin that is flat throughout.
        var flowStartFace = new int[rawCount];
        var flowStartZ = new double[rawCount];
        var highestFace = new int[rawCount];
        var highestFaceZ = new double[rawCount];
        Array.Fill(lowestZ, double.PositiveInfinity);
        Array.Fill(flowStartFace, -1);
        Array.Fill(flowStartZ, double.NegativeInfinity);
        Array.Fill(highestFace, -1);
        Array.Fill(highestFaceZ, double.NegativeInfinity);
        totalPlanArea = 0.0;

        for (int face = 0; face < faceCount; face++)
        {
            int basin = faceBasin[face];
            if (basin < 0)
                continue;

            faceCounts[basin]++;
            areas[basin] += geometry.PlanArea[face];
            totalPlanArea += geometry.PlanArea[face];

            double centroidZ = geometry.CentroidZ[face];
            if (centroidZ > highestFaceZ[basin])
            {
                highestFaceZ[basin] = centroidZ;
                highestFace[basin] = face;
            }

            if (!geometry.IsFlat[face] && centroidZ > flowStartZ[basin])
            {
                flowStartZ[basin] = centroidZ;
                flowStartFace[basin] = face;
            }

            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = faces[(face * 3) + corner];
                double z = vertices[(vertex * 3) + 2];
                if (z < lowestZ[basin])
                {
                    lowestZ[basin] = z;
                    lowestX[basin] = vertices[vertex * 3];
                    lowestY[basin] = vertices[(vertex * 3) + 1];
                }

            }
        }

        var order = new List<int>(rawCount);
        for (int basin = 0; basin < rawCount; basin++)
        {
            if (faceCounts[basin] > 0)
                order.Add(basin);
        }

        order.Sort((left, right) =>
        {
            int byArea = areas[right].CompareTo(areas[left]);
            return byArea != 0 ? byArea : basinRoot[left].CompareTo(basinRoot[right]);
        });

        var remap = new int[rawCount];
        Array.Fill(remap, -1);
        var basins = new BasinGraph.Basin[order.Count];
        for (int index = 0; index < order.Count; index++)
        {
            int basin = order[index];
            remap[basin] = index;
            int root = basinRoot[basin];
            int startFace = flowStartFace[basin] >= 0 ? flowStartFace[basin] : highestFace[basin];
            basins[index] = new BasinGraph.Basin
            {
                Index = index,
                OutletFace = root,
                Outlet = routing.OutletKind[root],
                FaceCount = faceCounts[basin],
                PlanArea = areas[basin],
                LowestZ = double.IsPositiveInfinity(lowestZ[basin]) ? 0.0 : lowestZ[basin],
                LowestX = lowestX[basin],
                LowestY = lowestY[basin],
                FlowStartX = startFace >= 0 ? geometry.CentroidX[startFace] : 0.0,
                FlowStartY = startFace >= 0 ? geometry.CentroidY[startFace] : 0.0,
                FlowStartZ = startFace >= 0 ? geometry.CentroidZ[startFace] : 0.0
            };
        }

        for (int face = 0; face < faceCount; face++)
        {
            int basin = faceBasin[face];
            faceBasin[face] = basin < 0 ? -1 : remap[basin];
        }

        return basins;
    }

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);
}
