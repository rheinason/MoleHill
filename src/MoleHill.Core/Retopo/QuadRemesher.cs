using MoleHill.Core.Engine;

namespace MoleHill.Core.Retopo;

/// <summary>
/// Field-guided quad retopology orchestrator (Stage 2). Runs the cross-field (<see cref="CrossFieldSolver"/>),
/// a field-guided parametrization (<see cref="GuidedParametrizer"/>), and integer-lattice quad extraction
/// (<see cref="QuadExtractor"/>) to produce a quad-dominant mesh whose edges flow along the features, lifted
/// onto the 2.5D terrain surface. Returns the field too so the Retopo modifier can reuse it for the flow
/// preview. First cut is non-seamless — defects at singularities/boundary are expected (cleaned up in Stage 3).
/// </summary>
public static class QuadRemesher
{
    public sealed class Options
    {
        /// <summary>Target quad edge length. 0 auto-derives from mesh extent and vertex count.</summary>
        public double EdgeLength { get; init; }

        public double CreaseAngleDeg { get; init; }

        public double Tolerance { get; init; }

        /// <summary>
        /// Faces steeper than this (normal tilted more than this many degrees from vertical — e.g. retaining-
        /// wall faces) are excluded from the terrain quad field: they are a thin sliver in plan and can't be
        /// parametrized there. 0 disables the mask. Walls are rebuilt separately as quad strips.
        /// </summary>
        public double WallFaceMinSlopeDeg { get; init; }

        /// <summary>
        /// Close internal quad-lattice holes with triangle fans lifted to the source terrain. This keeps the
        /// current non-seamless parametrizer but removes the most visible Stage-2 gaps.
        /// </summary>
        public bool EnableCleanup { get; init; }

        /// <summary>
        /// Maximum XY area for cleanup's cheap fan fill. 0 derives a conservative cap from
        /// <see cref="EdgeLength"/> so large parametrization gaps are not turned into visual fans.
        /// </summary>
        public double CleanupMaxFanFillArea { get; init; }
    }

    public sealed class Result
    {
        public bool Success { get; init; }

        public double[] Theta { get; init; } = Array.Empty<double>();

        public bool[] Pinned { get; init; } = Array.Empty<bool>();

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Quads { get; init; } = Array.Empty<int>();

        public int[] Tris { get; init; } = Array.Empty<int>();

        public string? Warning { get; init; }

        public int CleanupTriangleCount { get; init; }

        public int ClosedGapLoopCount { get; init; }

        public int SkippedLargeGapLoopCount { get; init; }

        public int QuadCount => Quads.Length / 4;
    }

    public static Result Remesh(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        Options options)
    {
        CrossFieldSolver.Result field = CrossFieldSolver.Solve(
            vertices, faces, constraints,
            new CrossFieldSolver.Options { CreaseAngleDeg = options.CreaseAngleDeg, Tolerance = options.Tolerance });

        if (!field.Success)
        {
            return new Result
            {
                Success = false,
                Theta = field.Theta,
                Pinned = field.Pinned,
                Warning = field.Warning ?? "Cross-field could not be computed."
            };
        }

        double h = options.EdgeLength > 0 ? options.EdgeLength : DeriveSpacing(vertices);
        bool[]? active = options.WallFaceMinSlopeDeg > 0
            ? BuildActiveMask(vertices, faces, options.WallFaceMinSlopeDeg)
            : null;
        (double[] u, double[] v) = GuidedParametrizer.Solve(vertices, faces, field.Theta, h, active);
        QuadExtractor.QuadMesh quad = QuadExtractor.Extract(vertices, faces, u, v, Math.Max(options.Tolerance, 1e-9), active);

        string? warning = field.Warning;
        int cleanupTriangleCount = 0;
        int closedGapLoopCount = 0;
        if (options.EnableCleanup && quad.QuadCount > 0)
        {
            QuadRetopoCleanup.CleanupResult cleanup = QuadRetopoCleanup.CloseInternalBoundaryLoops(
                vertices,
                faces,
                quad.Vertices,
                quad.Quads,
                quad.Tris,
                new QuadRetopoCleanup.Options
                {
                    Tolerance = options.Tolerance,
                    MaxFanFillArea = options.CleanupMaxFanFillArea > 0
                        ? options.CleanupMaxFanFillArea
                        : h * h * 16.0
                });
            quad = new QuadExtractor.QuadMesh
            {
                Vertices = cleanup.Vertices,
                Quads = cleanup.Quads,
                Tris = cleanup.Tris
            };
            cleanupTriangleCount = cleanup.AddedTriangleCount;
            closedGapLoopCount = cleanup.ClosedLoopCount;
            int skippedLargeGapLoopCount = cleanup.SkippedLargeLoopCount;
            warning = CombineWarnings(warning, cleanup.Warning);
            return new Result
            {
                Success = quad.QuadCount > 0,
                Theta = field.Theta,
                Pinned = field.Pinned,
                Vertices = quad.Vertices,
                Quads = quad.Quads,
                Tris = quad.Tris,
                Warning = quad.QuadCount == 0 ? "Quad extraction produced no faces." : warning,
                CleanupTriangleCount = cleanupTriangleCount,
                ClosedGapLoopCount = closedGapLoopCount,
                SkippedLargeGapLoopCount = skippedLargeGapLoopCount
            };
        }

        return new Result
        {
            Success = quad.QuadCount > 0,
            Theta = field.Theta,
            Pinned = field.Pinned,
            Vertices = quad.Vertices,
            Quads = quad.Quads,
            Tris = quad.Tris,
            Warning = quad.QuadCount == 0 ? "Quad extraction produced no faces." : warning,
            CleanupTriangleCount = cleanupTriangleCount,
            ClosedGapLoopCount = closedGapLoopCount
        };
    }

    private static string? CombineWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second))
            return first;
        return $"{first} {second}";
    }

    /// <summary>Per-face mask: active where the face normal is within <paramref name="minSlopeDeg"/> of vertical.</summary>
    private static bool[] BuildActiveMask(double[] vertices, int[] faces, double minSlopeDeg)
    {
        int faceCount = faces.Length / 3;
        double cosThreshold = Math.Cos(Math.Clamp(minSlopeDeg, 1.0, 89.0) * Math.PI / 180.0);
        var active = new bool[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            double ux = vertices[b * 3] - vertices[a * 3], uy = vertices[b * 3 + 1] - vertices[a * 3 + 1], uz = vertices[b * 3 + 2] - vertices[a * 3 + 2];
            double wx = vertices[c * 3] - vertices[a * 3], wy = vertices[c * 3 + 1] - vertices[a * 3 + 1], wz = vertices[c * 3 + 2] - vertices[a * 3 + 2];
            double nx = (uy * wz) - (uz * wy);
            double ny = (uz * wx) - (ux * wz);
            double nz = (ux * wy) - (uy * wx);
            double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            active[f] = len > 1e-15 && Math.Abs(nz) / len >= cosThreshold;
        }

        return active;
    }

    private static double DeriveSpacing(double[] vertices)
    {
        int vertexCount = vertices.Length / 3;
        if (vertexCount == 0)
            return 1.0;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        return spacing > 1e-9 ? spacing : 1.0;
    }
}
