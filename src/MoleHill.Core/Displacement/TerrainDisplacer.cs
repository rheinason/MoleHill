using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Displacement;

/// <summary>
/// Moves terrain vertices up or down by an <see cref="IDisplacementSource"/>, keeping their plan position
/// and the mesh topology. What the edit leaves alone — the walls (faces at or above
/// <see cref="WallFaceMinSlopeDeg"/>, never buried), the hold lines (breaklines, wall rails, graded edges,
/// protect curves), the border if asked — is held exactly, and the offset fades back in over the feather
/// distance from each, the same way Round Creases fades (<see cref="HeldLines"/>). Boundaries scope the
/// edit: nothing outside them moves, and inside the offset rises from zero at the drawn line to full
/// strength at the feather distance, so the drawn line is where the edit stops.
/// </summary>
public static class TerrainDisplacer
{
    /// <summary>Faces at least this steep are walls: never moved.</summary>
    public const double WallFaceMinSlopeDeg = CreaseRounder.WallFaceMinSlopeDeg;

    public sealed class Request
    {
        public required IndexedTriMesh Mesh { get; init; }

        public required IDisplacementSource Source { get; init; }

        /// <summary>Closed plan polygons scoping the edit; empty means the whole terrain.</summary>
        public IReadOnlyList<(double[] xyVerts, int vertCount)> Boundaries { get; init; } = Array.Empty<(double[], int)>();

        /// <summary>Lines whose vertices keep their height exactly.</summary>
        public IReadOnlyList<MeshSmoother.BreaklinePolyline> HoldLines { get; init; } = Array.Empty<MeshSmoother.BreaklinePolyline>();

        /// <summary>Hold the mesh border (its naked edges) like a hold line.</summary>
        public bool HoldBorder { get; init; } = true;

        /// <summary>Plan distance over which the offset fades in from a boundary or a held line. Zero is a hard step.</summary>
        public double Feather { get; init; }

        /// <summary>Plan distance within which a vertex lies on a hold line.</summary>
        public double Tolerance { get; init; }
    }

    public readonly record struct Result(
        double[] Vertices,
        int MovedVertexCount,
        int HeldVertexCount,
        double MinOffset,
        double MaxOffset,
        double MedianEdgeLength,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY);

    /// <summary>
    /// Displaces <see cref="Request.Mesh"/>. Returns new vertices; only Z changes, so the faces stand as they
    /// are. <see cref="Result.MedianEdgeLength"/> is the median plan length of the edges touching a vertex the
    /// edit reaches (0 when it reaches none) — the resolution the pattern is sampled at. The bounds are the
    /// plan extent of those vertices.
    /// </summary>
    public static Result Apply(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = request.Mesh;
        double tol = Math.Max(request.Tolerance, 1e-9);
        double feather = Math.Max(0.0, request.Feather);
        var result = new double[vertexCount * 3];
        Array.Copy(vertices, result, vertexCount * 3);

        (double[] xyVerts, int vertCount)[] boundaries = request.Boundaries.ToArray();
        bool[] inside = HeldLines.InsideBoundaries(vertices, vertexCount, boundaries);
        var boundaryFlat = new List<double>();
        foreach ((double[] xy, int count) in boundaries)
            HeldLines.AddPolyline(boundaryFlat, xy, count, isClosed: true);
        HeldLines boundaryLines = HeldLines.FromSegments(boundaryFlat.ToArray());

        var held = new bool[vertexCount];
        var fade = new List<double>();
        bool[] wallFaces = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, faceCount, WallFaceMinSlopeDeg, degenerateAltitude: tol);
        HeldLines.HoldWallFaces(vertices, faces, faceCount, wallFaces, held, fade);

        IndexedMeshTools.EdgeTopology topology = IndexedMeshTools.BuildEdgeTopology(faces, faceCount);
        if (request.HoldBorder)
        {
            for (int e = 0; e < topology.NakedEdgeCount; e++)
            {
                int a = topology.NakedEdges[e * 2];
                int b = topology.NakedEdges[e * 2 + 1];
                held[a] = true;
                held[b] = true;
                HeldLines.AddMeshEdge(fade, vertices, a, b);
            }
        }

        HeldLines holdLines = HeldLines.FromPolylines(request.HoldLines);
        fade.AddRange(holdLines.Segments);
        HeldLines fadeLines = HeldLines.FromSegments(fade.ToArray());

        var weights = new double[vertexCount];
        var offsets = new double[vertexCount];
        System.Threading.Tasks.Parallel.For(
            0,
            vertexCount,
            () => (Candidates: new List<int>(16), Scratch: new SpatialHashGrid2D.QueryScratch()),
            (v, _, local) =>
            {
                if (!inside[v] || held[v])
                    return local;

                double px = vertices[v * 3];
                double py = vertices[v * 3 + 1];
                if (holdLines.IsOn(px, py, tol, local.Candidates, local.Scratch))
                    return local;

                double weight = 1.0;
                if (feather > 0.0)
                {
                    if (!boundaryLines.IsEmpty)
                        weight *= boundaryLines.Weight(px, py, feather, local.Candidates, local.Scratch);
                    weight *= fadeLines.Weight(px, py, feather, local.Candidates, local.Scratch);
                }

                if (weight <= 0.0)
                    return local;

                weights[v] = weight;
                offsets[v] = weight * request.Source.Offset(px, py);
                return local;
            },
            _ => { });

        int moved = 0;
        int heldCount = 0;
        double minOffset = 0.0, maxOffset = 0.0;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int v = 0; v < vertexCount; v++)
        {
            if (inside[v] && weights[v] <= 0.0)
                heldCount++;
            if (weights[v] <= 0.0)
                continue;

            double offset = offsets[v];
            if (!double.IsFinite(offset))
                continue;

            double x = vertices[v * 3], y = vertices[v * 3 + 1];
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            if (offset == 0.0)
                continue;

            result[v * 3 + 2] += offset;
            if (moved == 0)
            {
                minOffset = offset;
                maxOffset = offset;
            }
            else
            {
                minOffset = Math.Min(minOffset, offset);
                maxOffset = Math.Max(maxOffset, offset);
            }

            moved++;
        }

        double median = MedianReachedEdgeLength(vertices, topology, weights);
        return double.IsFinite(minX)
            ? new Result(result, moved, heldCount, minOffset, maxOffset, median, minX, minY, maxX, maxY)
            : new Result(result, moved, heldCount, minOffset, maxOffset, median, 0.0, 0.0, 0.0, 0.0);
    }

    private static double MedianReachedEdgeLength(double[] vertices, IndexedMeshTools.EdgeTopology topology, double[] weights)
    {
        var lengths = new List<double>();
        for (int e = 0; e < topology.EdgeCount; e++)
        {
            int a = topology.Edges[e * 2];
            int b = topology.Edges[e * 2 + 1];
            if (weights[a] <= 0.0 && weights[b] <= 0.0)
                continue;
            lengths.Add(Math.Sqrt(Geometry2D.DistanceSquared(vertices[a * 3], vertices[a * 3 + 1], vertices[b * 3], vertices[b * 3 + 1])));
        }

        if (lengths.Count == 0)
            return 0.0;

        lengths.Sort();
        int mid = lengths.Count / 2;
        return lengths.Count % 2 == 1 ? lengths[mid] : 0.5 * (lengths[mid - 1] + lengths[mid]);
    }
}
