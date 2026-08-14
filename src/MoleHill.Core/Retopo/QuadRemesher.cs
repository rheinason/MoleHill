using MoleHill.Core.Engine;

namespace MoleHill.Core.Retopo;

/// <summary>
/// Field-guided quad retopology orchestrator: cross-field (<see cref="CrossFieldSolver"/>) →
/// field-aligned isotropic remesh (<see cref="IsotropicRemesher"/> with <c>FieldTheta</c>) →
/// tri-to-quad pairing (<see cref="TriQuadPairer"/>). The remesh regularizes the triangles and slides
/// vertices along the field so edges straighten into the quad flow; pairing then merges triangle pairs
/// into quads without touching geometry. The output is ONE connected quad-dominant mesh, watertight by
/// construction — no parametrization, no lattice gaps, no strip welding. Steep retaining-wall faces are
/// frozen through the remesh (never moved or buried) and pair among themselves in their own plane.
/// Returns the field too so the Retopo modifier reuses it for the flow preview.
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
        /// Faces steeper than this (degrees from horizontal — retaining walls) are frozen: carried
        /// through unchanged and paired only with each other. 0 disables the mask.
        /// </summary>
        public double WallFaceMinSlopeDeg { get; init; }

        /// <summary>Isotropic remesh iterations (preview builds pass fewer).</summary>
        public int Iterations { get; init; } = 5;
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

        public int QuadCount => Quads.Length / 4;

        public int TriangleCount => Tris.Length / 3;
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

        IsotropicRemesher.Result remesh = IsotropicRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = h,
                CreaseAngleDeg = options.CreaseAngleDeg,
                Tolerance = options.Tolerance,
                WallFaceMinSlopeDeg = options.WallFaceMinSlopeDeg,
                Iterations = options.Iterations,
                FieldTheta = field.Theta
            });

        if (!remesh.Success)
        {
            return new Result
            {
                Success = false,
                Theta = field.Theta,
                Pinned = field.Pinned,
                Warning = remesh.Warning ?? "Field-aligned remesh failed."
            };
        }

        var featureEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int i = 0; i < remesh.FeatureEdges.Length; i += 2)
            featureEdges.Add(IndexedMeshTools.GetEdgeKey(remesh.FeatureEdges[i], remesh.FeatureEdges[i + 1]));

        // Field lookups for the pairer run against the ORIGINAL mesh (the field is per-input-vertex).
        var sampler = new IsotropicRemesher.FieldSampler(
            vertices, faces, field.Theta,
            new MoleHill.Core.Grading.TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, h * 0.5));

        TriQuadPairer.Result paired = TriQuadPairer.Pair(
            remesh.Vertices,
            remesh.Faces,
            featureEdges,
            remesh.FrozenFaces,
            new TriQuadPairer.Options
            {
                ThetaSampler = (x, y) => sampler.SampleTheta(x, y, double.NaN)
            });

        return new Result
        {
            Success = paired.QuadCount > 0,
            Theta = field.Theta,
            Pinned = field.Pinned,
            Vertices = remesh.Vertices,
            Quads = paired.Quads,
            Tris = paired.Tris,
            Warning = paired.QuadCount == 0 ? "Tri-to-quad pairing produced no quads." : field.Warning
        };
    }

    private static double DeriveSpacing(double[] vertices)
    {
        int vertexCount = vertices.Length / 3;
        if (vertexCount == 0)
            return double.Epsilon;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double coordinateScale = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            coordinateScale = Math.Max(coordinateScale, Math.Max(Math.Abs(x), Math.Abs(y)));
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        return Math.Max(spacing, ScaleAwareTolerance.LengthFloor(coordinateScale));
    }
}
