using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// The grading envelope: outside the road or pad, a graded point lies between the original ground and the
/// design height at its nearest edge, because a batter runs from one to the other. Checked independently of
/// whichever tier produced the result — this has its own closest-point search rather than the graders'
/// stationing — so a tier's own mistake cannot also blind the check.
///
/// <para>The design height comes from <em>every</em> path or pad, not just the nearest: where corridors
/// overlap the graders blend them, and a blended point lies between their heights. That makes the band
/// loose where designs differ in height, but it never rejects a correct blend, and the faults it exists for
/// — a batter climbing 5 m past a terrace it never met — clear any design and the ground by metres.</para>
/// </summary>
internal static class GradingEnvelope
{
    /// <summary>The structured diagnostic code a grader adds when it clamps envelope violations.</summary>
    internal const string ClampedCodeSuffix = ".envelope.clamped";

    /// <summary>
    /// How far outside the band a point may sit. Generous against seam blending and float hand-off, and
    /// still two orders of magnitude below the faults the check exists for.
    /// </summary>
    internal static double ZTolerance(double modelTolerance) =>
        GradingTolerances.ModelToleranceOrDefault(modelTolerance) * 20.0;

    internal static List<GradingEnvelopeViolation> FindForPaths(
        IndexedTriMesh terrain,
        IReadOnlyList<PathGrader.PathDefinition> paths,
        double[] vertices,
        int vertexCount,
        double modelTolerance)
    {
        var designs = new List<DesignLine>(paths.Count);
        foreach (PathGrader.PathDefinition path in paths)
        {
            if (path.VertexCount >= 1)
                designs.Add(new DesignLine(path.XyVertices, path.ZValues, path.VertexCount, path.IsClosed, path.MaximumHalfWidth(), null));
        }

        return Find(terrain, vertices, vertexCount, modelTolerance, designs, insideFootprintIsPolygon: false);
    }

    internal static List<GradingEnvelopeViolation> FindForPads(
        IndexedTriMesh terrain,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        double[] vertices,
        int vertexCount,
        double modelTolerance)
    {
        var designs = new List<DesignLine>(pads.Count);
        foreach (PadGrader.PadBoundary pad in pads)
        {
            if (pad.VertexCount >= 3)
                designs.Add(new DesignLine(pad.XyVertices, null, pad.VertexCount, true, 0.0, pad));
        }

        return Find(terrain, vertices, vertexCount, modelTolerance, designs, insideFootprintIsPolygon: true);
    }

    /// <summary>
    /// <paramref name="result"/> with each violating point pulled back into its band and a warning naming
    /// how many and how far — the fallback that keeps one grading fault from shipping a spike. Callers that
    /// must not tolerate faults (tests, copied cases) assert the warning is absent.
    /// </summary>
    internal static GradingResult Clamp(
        GradingResult result,
        IReadOnlyList<GradingEnvelopeViolation> violations,
        string operation,
        string subject)
    {
        if (violations.Count == 0)
            return result;

        double[] vertices = (double[])result.Vertices.Clone();
        double worst = 0.0;
        foreach (GradingEnvelopeViolation violation in violations)
        {
            vertices[(violation.VertexIndex * 3) + 2] = Math.Clamp(violation.Z, violation.LowZ, violation.HighZ);
            worst = Math.Max(worst, violation.Overshoot);
        }

        string message =
            $"{subject} held {violations.Count:N0} point(s) that rose above or fell below both the design and " +
            $"the existing ground (worst {worst:0.###}) back between the two. This is a grading fault: Copy Case " +
            "and report it.";
        var diagnostic = GradingDiagnostic.Warning(operation + ClampedCodeSuffix, message, operation: operation);

        return new GradingResult(
            vertices,
            result.VertexCount,
            result.Faces,
            result.FaceCount,
            result.CutVolume,
            result.FillVolume,
            result.DaylightVertices,
            result.DaylightVertexCount,
            result.OutputPolylines,
            result.Diagnostics.Concat(new[] { message }).ToArray(),
            result.PatchSummaries,
            result.StructuredDiagnostics.Concat(new[] { diagnostic }).ToArray());
    }

    private static List<GradingEnvelopeViolation> Find(
        IndexedTriMesh terrain,
        double[] vertices,
        int vertexCount,
        double modelTolerance,
        IReadOnlyList<DesignLine> designs,
        bool insideFootprintIsPolygon)
    {
        var violations = new List<GradingEnvelopeViolation>();
        if (designs.Count == 0 || terrain.IsEmpty || vertexCount == 0)
            return violations;

        double zTolerance = ZTolerance(modelTolerance);
        double lengthTolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var ground = new GroundBand(terrain, lengthTolerance);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[(i * 3) + 1];
            double z = vertices[(i * 3) + 2];

            // Points off the original terrain have no ground to compare with, and points the grade left
            // alone are inside any band.
            if (!ground.TryGet(x, y, out double groundLow, out double groundHigh) ||
                (z >= groundLow - zTolerance && z <= groundHigh + zTolerance))
                continue;

            double low = groundLow;
            double high = groundHigh;
            bool insideFootprint = false;
            foreach (DesignLine design in designs)
            {
                if (insideFootprintIsPolygon && Geometry2D.PointInPolygon(x, y, design.Xy, design.Count))
                {
                    insideFootprint = true;
                    break;
                }

                ClosestOnLine(design, x, y, out double distance, out double designZ);
                if (!insideFootprintIsPolygon && distance <= design.HalfWidth + lengthTolerance)
                {
                    insideFootprint = true;
                    break;
                }

                low = Math.Min(low, designZ);
                high = Math.Max(high, designZ);
            }

            if (insideFootprint)
                continue;

            if (z > high + zTolerance || z < low - zTolerance)
                violations.Add(new GradingEnvelopeViolation(i, x, y, z, (groundLow + groundHigh) * 0.5, low, high));
        }

        return violations;
    }

    /// <summary>Distance to the line and the design height at its closest point.</summary>
    private static void ClosestOnLine(DesignLine design, double px, double py, out double distance, out double designZ)
    {
        int count = design.Count;
        if (count == 1)
        {
            double dx = px - design.Xy[0];
            double dy = py - design.Xy[1];
            distance = Math.Sqrt((dx * dx) + (dy * dy));
            designZ = design.ZAt(0, 0, 0.0, design.Xy[0], design.Xy[1]);
            return;
        }

        double bestSq = double.MaxValue;
        double bestZ = 0.0;

        int segmentCount = design.IsClosed && count > 2 ? count : count - 1;
        for (int s = 0; s < segmentCount; s++)
        {
            int a = s;
            int b = (s + 1) % count;
            double ax = design.Xy[a * 2];
            double ay = design.Xy[(a * 2) + 1];
            double bx = design.Xy[b * 2];
            double by = design.Xy[(b * 2) + 1];
            double ex = bx - ax;
            double ey = by - ay;
            double lengthSq = (ex * ex) + (ey * ey);
            double t = lengthSq > 1e-24 ? Math.Clamp((((px - ax) * ex) + ((py - ay) * ey)) / lengthSq, 0.0, 1.0) : 0.0;
            double cx = ax + (ex * t);
            double cy = ay + (ey * t);
            double dSq = ((px - cx) * (px - cx)) + ((py - cy) * (py - cy));
            if (dSq < bestSq)
            {
                bestSq = dSq;
                bestZ = design.ZAt(a, b, t, cx, cy);
            }
        }

        distance = Math.Sqrt(bestSq);
        designZ = bestZ;
    }

    /// <summary>
    /// The original ground at a point, as a band: every input face covering it within tolerance contributes.
    /// A retaining wall has two heights at one plan position — its foot and its top — and a single
    /// interpolated height picks one of them, which flagged sound grading along every wall as 2 m out.
    /// </summary>
    private sealed class GroundBand
    {
        private readonly IndexedTriMesh _terrain;
        private readonly double _tolerance;
        private readonly SpatialHashGrid2D _grid;
        private readonly List<int> _candidates = new(16);
        private readonly SpatialHashGrid2D.QueryScratch _scratch;

        public GroundBand(IndexedTriMesh terrain, double tolerance)
        {
            _terrain = terrain;
            _tolerance = tolerance;
            double[] v = terrain.Vertices;
            int[] f = terrain.Faces;
            var bounds = new Bounds2D[terrain.FaceCount];
            for (int face = 0; face < terrain.FaceCount; face++)
            {
                int a = f[face * 3] * 3, b = f[(face * 3) + 1] * 3, c = f[(face * 3) + 2] * 3;
                bounds[face] = new Bounds2D(
                    Math.Min(v[a], Math.Min(v[b], v[c])),
                    Math.Max(v[a], Math.Max(v[b], v[c])),
                    Math.Min(v[a + 1], Math.Min(v[b + 1], v[c + 1])),
                    Math.Max(v[a + 1], Math.Max(v[b + 1], v[c + 1])));
            }

            _grid = SpatialHashGrid2D.Build(bounds);
            _scratch = new SpatialHashGrid2D.QueryScratch(terrain.FaceCount);
        }

        public bool TryGet(double x, double y, out double low, out double high)
        {
            low = double.MaxValue;
            high = double.MinValue;
            _grid.GatherCandidates(Bounds2D.FromPoint(x, y, _tolerance), _candidates, _scratch);
            double[] v = _terrain.Vertices;
            int[] f = _terrain.Faces;
            foreach (int face in _candidates)
            {
                int a = f[face * 3] * 3, b = f[(face * 3) + 1] * 3, c = f[(face * 3) + 2] * 3;
                double det = ((v[b + 1] - v[c + 1]) * (v[a] - v[c])) + ((v[c] - v[b]) * (v[a + 1] - v[c + 1]));
                if (Math.Abs(det) > 1e-18)
                {
                    double w0 = (((v[b + 1] - v[c + 1]) * (x - v[c])) + ((v[c] - v[b]) * (y - v[c + 1]))) / det;
                    double w1 = (((v[c + 1] - v[a + 1]) * (x - v[c])) + ((v[a] - v[c]) * (y - v[c + 1]))) / det;
                    double w2 = 1.0 - w0 - w1;
                    if (w0 >= 0.0 && w1 >= 0.0 && w2 >= 0.0)
                    {
                        Widen((w0 * v[a + 2]) + (w1 * v[b + 2]) + (w2 * v[c + 2]), ref low, ref high);
                        continue;
                    }
                }

                // Near (or on) an edge, or a face with no plan area — a vertical wall face: take the heights
                // along each edge within tolerance.
                WidenFromEdge(v, a, b, x, y, ref low, ref high);
                WidenFromEdge(v, b, c, x, y, ref low, ref high);
                WidenFromEdge(v, c, a, x, y, ref low, ref high);
            }

            return low <= high;
        }

        private void WidenFromEdge(double[] v, int a, int b, double x, double y, ref double low, ref double high)
        {
            double ex = v[b] - v[a];
            double ey = v[b + 1] - v[a + 1];
            double lengthSq = (ex * ex) + (ey * ey);
            if (lengthSq <= 1e-24)
            {
                // Coincident in plan: a vertical edge. Both ends stand at this point.
                if (((x - v[a]) * (x - v[a])) + ((y - v[a + 1]) * (y - v[a + 1])) <= _tolerance * _tolerance)
                {
                    Widen(v[a + 2], ref low, ref high);
                    Widen(v[b + 2], ref low, ref high);
                }

                return;
            }

            double t = Math.Clamp((((x - v[a]) * ex) + ((y - v[a + 1]) * ey)) / lengthSq, 0.0, 1.0);
            double dx = x - (v[a] + (ex * t));
            double dy = y - (v[a + 1] + (ey * t));
            if ((dx * dx) + (dy * dy) <= _tolerance * _tolerance)
                Widen(v[a + 2] + ((v[b + 2] - v[a + 2]) * t), ref low, ref high);
        }

        private static void Widen(double z, ref double low, ref double high)
        {
            low = Math.Min(low, z);
            high = Math.Max(high, z);
        }
    }

    /// <summary>A path centreline (heights per vertex) or a pad boundary (heights from its plane).</summary>
    private sealed class DesignLine
    {
        public DesignLine(double[] xy, double[]? z, int count, bool isClosed, double halfWidth, PadGrader.PadBoundary? pad)
        {
            Xy = xy;
            Z = z;
            Count = count;
            IsClosed = isClosed;
            HalfWidth = halfWidth;
            Pad = pad;
        }

        public double[] Xy { get; }
        public double[]? Z { get; }
        public int Count { get; }
        public bool IsClosed { get; }
        public double HalfWidth { get; }
        public PadGrader.PadBoundary? Pad { get; }

        public double ZAt(int a, int b, double t, double x, double y) => Pad != null
            ? Pad.EvaluateZ(x, y)
            : Z![a] + ((Z[b] - Z[a]) * t);
    }
}
