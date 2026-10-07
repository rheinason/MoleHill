using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed record ResolvedGradePathDefinition(Guid SourceObjectId, PathGrader.PathDefinition Definition);

internal sealed partial class TerrainBuildService
{
    private sealed class ZoneBoundaryEntry
    {
        public required CollageZoneDefinition Zone { get; init; }

        public required MeshAreaSplitter.AreaBoundary Boundary { get; init; }

        public required int ZoneOrder { get; init; }

        public required int SourceOrder { get; init; }

        public required double PriorityZ { get; init; }

        public string? InputLayerPath { get; init; }
    }

    internal readonly record struct ReferenceComparisonStats(
        double CutVolume,
        double FillVolume,
        double CutFillDisplayAbsMax,
        bool IsEstimated,
        int GridProjectionCount,
        int FallbackProjectionCount)
    {
        public double NetVolume => CutVolume - FillVolume;
    }

    internal readonly record struct ReferenceComparisonCacheKey(
        double[] CurrentVertices,
        int[] CurrentFaces,
        ulong ReferenceFingerprint,
        ulong BoundaryFingerprint,
        ulong ReferenceTerrainFingerprint,
        bool UsesFallbackBaseMesh);

    internal readonly record struct ReferenceProjectionCacheKey(
        ulong ReferenceFingerprint,
        ulong ReferenceTerrainFingerprint,
        bool UsesFallbackBaseMesh);

    internal sealed class ReferenceProjectionContext
    {
        public required global::Rhino.Geometry.Mesh Mesh { get; init; }

        public MeshHeightProjector? Projector { get; init; }

        public int GridProjectionCount { get; set; }

        public int FallbackProjectionCount { get; set; }

        private global::Rhino.Geometry.BoundingBox? _bounds;

        /// <summary>The reference mesh's bounds, read once: a fallback projection spans them.</summary>
        public global::Rhino.Geometry.BoundingBox Bounds => _bounds ??= Mesh.GetBoundingBox(true);

        /// <summary>
        /// Projects straight down or up onto the reference where the height grid cannot (over a wall): through
        /// the grid cell's own triangles when there is a grid, else through the whole reference mesh.
        /// </summary>
        public bool TryFallbackProject(global::Rhino.Geometry.Point3d point, double tolerance, out global::Rhino.Geometry.Point3d projected)
        {
            if (Projector != null)
            {
                double[] candidates = Projector.CandidateTrianglesAt(point.X, point.Y);
                if (candidates.Length > 0)
                    return TerrainMeshProjection.TryProjectPointAlongWorldZ(candidates, Bounds, point, tolerance, out projected);
            }

            return TerrainMeshProjection.TryProjectPointAlongWorldZ(Mesh, point, tolerance, out projected);
        }
    }

    private readonly record struct ConstraintSignature(
        ulong Fingerprint,
        int PointCount,
        bool IsClosed,
        bool PreserveInputElevation);

    internal readonly record struct TinyFaceCleanupResult(
        int[] Faces,
        int RemovedFaceCount,
        int BlockedFaceCount)
    {
        public bool HasChanges => RemovedFaceCount > 0;
    }
}
