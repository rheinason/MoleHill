using MoleHill.Core.Engine;

namespace MoleHill.Core.Retopo;

/// <summary>
/// Stage 1 of field-guided quad retopology: a 2-D <b>cross-field</b> (4-RoSy direction field) over the
/// terrain's XY plane. Each vertex carries one of four symmetric directions — the local quad-edge
/// orientation — stored as its period-π/2 representative <c>(cos 4θ, sin 4θ)</c> so the four directions are
/// a single value with no branch bookkeeping.
///
/// The field is <b>pinned</b> along features (boundary ∪ creases ∪ constraint breaklines) to the feature
/// tangent, and <b>smoothed</b> everywhere else by matrix-free diffusion (Gauss–Seidel on the vertex graph)
/// — no external linear solver. The result is the flow direction later stages trace quads along; for now it
/// is what the Retopo modifier draws so the flow/feature-alignment can be eyeballed before extraction is
/// built. Terrain is 2.5D, so this all happens in XY and Z is irrelevant to the field.
/// </summary>
public static class CrossFieldSolver
{
    public sealed class Options
    {
        /// <summary>Detect interior creases folding at least this many degrees and align the field to them. 0 = off.</summary>
        public double CreaseAngleDeg { get; init; }

        public double Tolerance { get; init; }

        /// <summary>Smoothing iterations; 0 auto-scales with mesh size so features propagate across it.
        /// This is the <b>maximum</b> — diffusion stops early once it has converged.</summary>
        public int Iterations { get; init; }

        /// <summary>
        /// Convergence threshold on the largest per-vertex change of the 4-RoSy representative in one
        /// sweep. The representative is a unit vector, so a change of e corresponds to about e radians
        /// in 4θ, i.e. e/4 in θ. The default settles θ far below anything downstream can resolve while
        /// letting a field that has stopped moving skip the remaining sweeps — the budget runs to 2,000
        /// on a large mesh, and most of those sweeps change nothing. Zero or less runs every sweep.
        /// </summary>
        public double ConvergenceTolerance { get; init; } = 1e-7;
    }

    public sealed class Result
    {
        public bool Success { get; init; }

        /// <summary>Per-vertex field angle in [0, π/2) — the local quad-edge direction (mod 90°).</summary>
        public double[] Theta { get; init; } = Array.Empty<double>();

        /// <summary>Per-vertex: true where the field is pinned to a feature/boundary tangent.</summary>
        public bool[] Pinned { get; init; } = Array.Empty<bool>();

        public string? Warning { get; init; }

        /// <summary>Diffusion sweeps actually run, which is at most <c>Options.Iterations</c>.</summary>
        public int Iterations { get; init; }

        /// <summary>Largest per-vertex representative change in the final sweep.</summary>
        public double FinalResidual { get; init; }

        /// <summary>True when diffusion stopped on the tolerance rather than exhausting its budget.</summary>
        public bool Converged { get; init; }
    }

    private const double QuarterPi = Math.PI / 2.0;

    public static Result Solve(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        Options options)
    {
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        if (vertexCount == 0 || faceCount == 0)
            return new Result { Success = false, Warning = "Input mesh has no usable triangles." };

        BuildAdjacency(vertices, faces, faceCount, vertexCount, out int[] offsets, out int[] neighbors);

        // Accumulate feature-tangent directions (in 4-RoSy space) at each vertex; a vertex touched by any
        // feature edge is pinned to the normalized accumulation.
        var accumX = new double[vertexCount];
        var accumY = new double[vertexCount];
        double tolerance = Math.Max(options.Tolerance, 1e-6);

        var boundarySegments = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundarySegments, IndexedMeshTools.CreateEdgeKeySet(), faces, faceCount);
        foreach ((int a, int b) in boundarySegments)
            AccumulateFeatureEdge(vertices, a, b, accumX, accumY);

        if (options.CreaseAngleDeg > 0)
        {
            double cosThreshold = Math.Cos(Math.Clamp(options.CreaseAngleDeg, 1.0, 179.0) * Math.PI / 180.0);
            foreach ((int a, int b) in SurfaceRemesher.DetectCreaseEdges(vertices, faces, faceCount, cosThreshold))
                AccumulateFeatureEdge(vertices, a, b, accumX, accumY);
        }

        foreach ((int a, int b) in ConstraintFeatureEdges(vertices, vertexCount, faces, faceCount, constraints, tolerance))
            AccumulateFeatureEdge(vertices, a, b, accumX, accumY);

        var repX = new double[vertexCount];
        var repY = new double[vertexCount];
        var pinned = new bool[vertexCount];
        int pinnedCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double len = Math.Sqrt((accumX[i] * accumX[i]) + (accumY[i] * accumY[i]));
            if (len > 1e-9)
            {
                repX[i] = accumX[i] / len;
                repY[i] = accumY[i] / len;
                pinned[i] = true;
                pinnedCount++;
            }
            else
            {
                repX[i] = 1.0; // neutral θ = 0 seed for free vertices
                repY[i] = 0.0;
            }
        }

        int iterations = options.Iterations > 0
            ? options.Iterations
            : Math.Clamp((int)(4.0 * Math.Sqrt(vertexCount)), 50, 2000);

        // Gauss–Seidel diffusion: each free vertex becomes the normalized 4-RoSy average of its neighbours.
        // Pinned vertices are held fixed. Fixed iteration order ⇒ deterministic.
        //
        // The iteration budget is derived from vertex count and can reach 2,000 sweeps, but the field
        // stops moving long before that on most meshes. Each sweep tracks the largest change of any
        // vertex's representative and stops once it is below the tolerance; the residual is accumulated
        // in the same fixed order as the updates, so the stopping point is deterministic too.
        double convergenceTolerance = options.ConvergenceTolerance;
        int sweepsRun = 0;
        double residual = double.PositiveInfinity;
        bool converged = false;
        for (int iter = 0; iter < iterations; iter++)
        {
            sweepsRun++;
            residual = 0.0;
            for (int i = 0; i < vertexCount; i++)
            {
                if (pinned[i])
                    continue;

                double sumX = 0.0, sumY = 0.0;
                for (int k = offsets[i]; k < offsets[i + 1]; k++)
                {
                    int j = neighbors[k];
                    sumX += repX[j];
                    sumY += repY[j];
                }

                double len = Math.Sqrt((sumX * sumX) + (sumY * sumY));
                if (len > 1e-9)
                {
                    double newX = sumX / len;
                    double newY = sumY / len;
                    double deltaX = newX - repX[i];
                    double deltaY = newY - repY[i];
                    double delta = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                    if (delta > residual)
                        residual = delta;

                    repX[i] = newX;
                    repY[i] = newY;
                }
            }

            if (convergenceTolerance > 0.0 && residual <= convergenceTolerance)
            {
                converged = true;
                break;
            }
        }

        var theta = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            double angle = Math.Atan2(repY[i], repX[i]) / 4.0;
            angle %= QuarterPi;
            if (angle < 0.0)
                angle += QuarterPi;
            theta[i] = angle;
        }

        return new Result
        {
            Success = true,
            Theta = theta,
            Pinned = pinned,
            Iterations = sweepsRun,
            FinalResidual = double.IsPositiveInfinity(residual) ? 0.0 : residual,
            Converged = converged,
            Warning = pinnedCount == 0 ? "No feature or boundary edges to align to; field is unconstrained." : null
        };
    }

    private static void AccumulateFeatureEdge(double[] vertices, int a, int b, double[] accumX, double[] accumY)
    {
        double dx = vertices[b * 3] - vertices[a * 3];
        double dy = vertices[b * 3 + 1] - vertices[a * 3 + 1];
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-9)
            return;

        double angle = Math.Atan2(dy, dx);
        double c = Math.Cos(4.0 * angle);
        double s = Math.Sin(4.0 * angle);
        accumX[a] += c;
        accumY[a] += s;
        accumX[b] += c;
        accumY[b] += s;
    }

    private static void BuildAdjacency(
        double[] vertices, int[] faces, int faceCount, int vertexCount, out int[] offsets, out int[] neighbors)
    {
        IndexedMeshTools.EdgeTopology topology = IndexedMeshTools.BuildEdgeTopology(faces, faceCount);
        int edgeCount = topology.EdgeCount;

        offsets = new int[vertexCount + 1];
        for (int e = 0; e < edgeCount; e++)
        {
            offsets[topology.Edges[e * 2]]++;
            offsets[topology.Edges[e * 2 + 1]]++;
        }

        int running = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            int degree = offsets[i];
            offsets[i] = running;
            running += degree;
        }

        offsets[vertexCount] = running;
        neighbors = new int[running];
        var cursor = (int[])offsets.Clone();
        for (int e = 0; e < edgeCount; e++)
        {
            int a = topology.Edges[e * 2];
            int b = topology.Edges[e * 2 + 1];
            neighbors[cursor[a]++] = b;
            neighbors[cursor[b]++] = a;
        }
    }

    /// <summary>
    /// Mesh edges coinciding with a constraint-breakline segment, matched by nearest-vertex lookup (the
    /// constraint vertices are embedded in the mesh). Mirrors the marking used by the local-refine remesh.
    /// </summary>
    private static IEnumerable<(int a, int b)> ConstraintFeatureEdges(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        if (constraints.Count == 0)
            yield break;

        var meshEdges = IndexedMeshTools.CreateEdgeKeySet(faceCount * 3);
        for (int t = 0; t < faceCount; t++)
        {
            int a = faces[t * 3], b = faces[t * 3 + 1], c = faces[t * 3 + 2];
            meshEdges.Add(IndexedMeshTools.GetEdgeKey(a, b));
            meshEdges.Add(IndexedMeshTools.GetEdgeKey(b, c));
            meshEdges.Add(IndexedMeshTools.GetEdgeKey(c, a));
        }

        var index = new VertexHashGrid(vertices, vertexCount, Math.Max(tolerance * 4.0, 1e-6));

        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            int pointCount = constraint.PointCount;
            if (pointCount < 2)
                continue;

            int previous = index.FindNearest(constraint.Points[0], constraint.Points[1], tolerance);
            for (int i = 1; i < pointCount; i++)
            {
                int current = index.FindNearest(constraint.Points[i * 3], constraint.Points[i * 3 + 1], tolerance);
                if (previous >= 0 && current >= 0 && previous != current &&
                    meshEdges.Contains(IndexedMeshTools.GetEdgeKey(previous, current)))
                {
                    yield return (previous, current);
                }

                previous = current;
            }

            if (constraint.IsClosed)
            {
                int first = index.FindNearest(constraint.Points[0], constraint.Points[1], tolerance);
                if (previous >= 0 && first >= 0 && previous != first &&
                    meshEdges.Contains(IndexedMeshTools.GetEdgeKey(previous, first)))
                {
                    yield return (previous, first);
                }
            }
        }
    }

    private sealed class VertexHashGrid
    {
        private readonly double[] _vertices;
        private readonly Dictionary<long, List<int>> _cells = new(IndexedMeshTools.CellKeyComparer.Instance);
        private readonly double _inverseCellSize;

        public VertexHashGrid(double[] vertices, int vertexCount, double cellSize)
        {
            _vertices = vertices;
            _inverseCellSize = 1.0 / cellSize;
            for (int i = 0; i < vertexCount; i++)
            {
                long key = CellKey(vertices[i * 3], vertices[i * 3 + 1]);
                if (!_cells.TryGetValue(key, out List<int>? bucket))
                {
                    bucket = new List<int>(2);
                    _cells.Add(key, bucket);
                }

                bucket.Add(i);
            }
        }

        public int FindNearest(double x, double y, double tolerance)
        {
            double bestDistanceSquared = tolerance * tolerance;
            int best = -1;
            long centerX = (long)Math.Floor(x * _inverseCellSize);
            long centerY = (long)Math.Floor(y * _inverseCellSize);
            for (long cy = centerY - 1; cy <= centerY + 1; cy++)
            {
                for (long cx = centerX - 1; cx <= centerX + 1; cx++)
                {
                    if (!_cells.TryGetValue((cx << 32) | (uint)cy, out List<int>? bucket))
                        continue;

                    foreach (int index in bucket)
                    {
                        double dx = _vertices[index * 3] - x;
                        double dy = _vertices[index * 3 + 1] - y;
                        double distanceSquared = (dx * dx) + (dy * dy);
                        if (distanceSquared < bestDistanceSquared)
                        {
                            bestDistanceSquared = distanceSquared;
                            best = index;
                        }
                    }
                }
            }

            return best;
        }

        private long CellKey(double x, double y)
        {
            long cx = (long)Math.Floor(x * _inverseCellSize);
            long cy = (long)Math.Floor(y * _inverseCellSize);
            return (cx << 32) | (uint)cy;
        }
    }
}
