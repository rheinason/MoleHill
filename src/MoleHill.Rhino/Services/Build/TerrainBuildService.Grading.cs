using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Grading stages: Grade Pad, Grade Path, and In-Situ Stair — input resolution, constraints, bounds, and patch summaries.
internal sealed partial class TerrainBuildService
{
    internal static PadGrader.LockCurve[] CombinePadLockCurves(
        IReadOnlyList<PadGrader.LockCurve> localLocks,
        IReadOnlyList<ConstraintPolyline> persistentHardConstraints,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        double[] terrainVertices,
        int terrainVertexCount,
        double terrainDetailSize,
        out int skippedPersistentLockCount)
    {
        skippedPersistentLockCount = 0;
        if (localLocks.Count == 0 && persistentHardConstraints.Count == 0)
            return Array.Empty<PadGrader.LockCurve>();

        var combined = new List<PadGrader.LockCurve>(localLocks.Count + persistentHardConstraints.Count);
        combined.AddRange(localLocks);
        Bounds2D[] padInfluenceBounds = BuildPadInfluenceBoundsForLocks(
            pads,
            terrainVertices,
            terrainVertexCount,
            terrainDetailSize);

        foreach (var constraint in persistentHardConstraints)
        {
            if (constraint.PointCount < 2 || constraint.IsClosed)
                continue;

            if (padInfluenceBounds.Length > 0 &&
                !ConstraintIntersectsAnyBounds(constraint, padInfluenceBounds))
            {
                skippedPersistentLockCount++;
                continue;
            }

            var xyVertices = new double[constraint.PointCount * 2];
            for (int i = 0; i < constraint.PointCount; i++)
            {
                xyVertices[i * 2] = constraint.Points[i * 3];
                xyVertices[i * 2 + 1] = constraint.Points[i * 3 + 1];
            }

            combined.Add(new PadGrader.LockCurve(xyVertices, constraint.PointCount));
        }

        return combined.ToArray();
    }

    private static Bounds2D[] BuildPadInfluenceBoundsForLocks(
        IReadOnlyList<PadGrader.PadBoundary> pads,
        double[] terrainVertices,
        int terrainVertexCount,
        double terrainDetailSize)
    {
        if (pads.Count == 0)
            return Array.Empty<Bounds2D>();

        ComputeTerrainBoundsAndZRange(
            terrainVertices,
            terrainVertexCount,
            out Bounds2D terrainBounds,
            out double terrainMinZ,
            out double terrainMaxZ);
        double terrainDiagonal = Math.Sqrt(
            ((terrainBounds.MaxX - terrainBounds.MinX) * (terrainBounds.MaxX - terrainBounds.MinX)) +
            ((terrainBounds.MaxY - terrainBounds.MinY) * (terrainBounds.MaxY - terrainBounds.MinY)));
        double basePadding = Math.Max(terrainDetailSize * 2.0, Math.Max(terrainDiagonal * 1e-12, double.Epsilon));

        var bounds = new Bounds2D[pads.Count];
        for (int i = 0; i < pads.Count; i++)
        {
            PadGrader.PadBoundary pad = pads[i];
            Bounds2D padBounds = GradingPatch.ComputeBounds(pad.XyVertices);
            double reach = EstimatePadReach(pad, padBounds, terrainMinZ, terrainMaxZ, terrainDiagonal);
            double expansion = Math.Max(basePadding, reach + pad.StitchApronDistance + terrainDetailSize);
            bounds[i] = ExpandBounds(padBounds, expansion);
        }

        return bounds;
    }

    private static void ComputeTerrainBoundsAndZRange(
        double[] vertices,
        int vertexCount,
        out Bounds2D bounds,
        out double minZ,
        out double maxZ)
    {
        if (vertexCount <= 0 || vertices.Length < 3)
        {
            bounds = new Bounds2D(0.0, 0.0, 0.0, 0.0);
            minZ = 0.0;
            maxZ = 0.0;
            return;
        }

        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        minZ = double.PositiveInfinity;
        maxZ = double.NegativeInfinity;
        int limit = Math.Min(vertexCount, vertices.Length / 3);
        for (int i = 0; i < limit; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[(i * 3) + 1];
            double z = vertices[(i * 3) + 2];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                continue;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        if (!double.IsFinite(minX))
        {
            bounds = new Bounds2D(0.0, 0.0, 0.0, 0.0);
            minZ = 0.0;
            maxZ = 0.0;
            return;
        }

        bounds = new Bounds2D(minX, maxX, minY, maxY);
    }

    private static double EstimatePadReach(
        PadGrader.PadBoundary pad,
        Bounds2D padBounds,
        double terrainMinZ,
        double terrainMaxZ,
        double terrainDiagonal)
    {
        if (pad.MaxDistance > 0.0)
            return pad.MaxDistance;

        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        if (!double.IsFinite(slopeRatio) || slopeRatio <= 1e-9)
            return terrainDiagonal;

        double padMinZ = double.PositiveInfinity;
        double padMaxZ = double.NegativeInfinity;
        AccumulatePadZRange(pad, padBounds.MinX, padBounds.MinY, ref padMinZ, ref padMaxZ);
        AccumulatePadZRange(pad, padBounds.MinX, padBounds.MaxY, ref padMinZ, ref padMaxZ);
        AccumulatePadZRange(pad, padBounds.MaxX, padBounds.MinY, ref padMinZ, ref padMaxZ);
        AccumulatePadZRange(pad, padBounds.MaxX, padBounds.MaxY, ref padMinZ, ref padMaxZ);

        double dz = Math.Max(
            Math.Abs(terrainMinZ - padMaxZ),
            Math.Abs(terrainMaxZ - padMinZ));
        double reach = dz / slopeRatio;
        if (!double.IsFinite(reach))
            return terrainDiagonal;

        return Math.Min(Math.Max(reach, 0.0), terrainDiagonal);
    }

    private static void AccumulatePadZRange(
        PadGrader.PadBoundary pad,
        double x,
        double y,
        ref double minZ,
        ref double maxZ)
    {
        double z = pad.EvaluateZ(x, y);
        if (!double.IsFinite(z))
            return;

        if (z < minZ) minZ = z;
        if (z > maxZ) maxZ = z;
    }

    private static Bounds2D ExpandBounds(Bounds2D bounds, double expansion)
    {
        return new Bounds2D(
            bounds.MinX - expansion,
            bounds.MaxX + expansion,
            bounds.MinY - expansion,
            bounds.MaxY + expansion);
    }

    private static bool ConstraintIntersectsAnyBounds(
        ConstraintPolyline constraint,
        IReadOnlyList<Bounds2D> bounds)
    {
        if (constraint.Points.Length < constraint.PointCount * 3)
            return false;

        Bounds2D constraintBounds = ComputeConstraintBounds(constraint);
        for (int i = 0; i < bounds.Count; i++)
        {
            if (constraintBounds.Intersects(bounds[i]))
                return true;
        }

        return false;
    }

    private static Bounds2D ComputeConstraintBounds(ConstraintPolyline constraint)
    {
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < constraint.PointCount; i++)
        {
            double x = constraint.Points[i * 3];
            double y = constraint.Points[(i * 3) + 1];
            if (!double.IsFinite(x) || !double.IsFinite(y))
                continue;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return double.IsFinite(minX)
            ? new Bounds2D(minX, maxX, minY, maxY)
            : new Bounds2D(0.0, 0.0, 0.0, 0.0);
    }

    internal static ConstraintConflictDiagnostics.ConflictSummary AnalyzeHardConstraintConflicts(
        IReadOnlyList<ConstraintPolyline> pathConstraints,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double tolerance)
    {
        return ConstraintConflictDiagnostics.Analyze(
            pathConstraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray(),
            hardConstraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray(),
            tolerance);
    }

    internal static void AddOutputPolylinesAsBreaklines(
        IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> polylines,
        TerrainBuildResult build)
    {
        build.PersistentHardConstraints.AddRange(CreateOutputPolylineConstraints(polylines));
    }

    internal static List<ConstraintPolyline> CreateOutputPolylineConstraints(
        IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> polylines)
    {
        var constraints = new List<ConstraintPolyline>(polylines.Count);
        foreach (var poly in polylines)
        {
            if (poly.VertexCount < 2)
                continue;
            constraints.Add(new ConstraintPolyline(
                poly.Vertices,
                poly.VertexCount,
                poly.IsClosed,
                PreserveInputElevation: true));
        }

        return constraints;
    }

    internal static List<ConstraintPolyline> CreatePreservedElevationConstraints(
        IReadOnlyList<ConstraintPolyline> constraints)
    {
        var preservedConstraints = new List<ConstraintPolyline>(constraints.Count);
        foreach (ConstraintPolyline constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            int pointValueCount = Math.Min(constraint.Points.Length, constraint.PointCount * 3);
            if (pointValueCount < constraint.PointCount * 3)
                continue;

            var points = new double[pointValueCount];
            Array.Copy(constraint.Points, points, pointValueCount);
            preservedConstraints.Add(new ConstraintPolyline(
                points,
                constraint.PointCount,
                constraint.IsClosed,
                PreserveInputElevation: true));
        }

        return preservedConstraints;
    }

    internal static (ResolvedGradePathDefinition[] Paths, IReadOnlyList<VariablePathWidthResolver.Diagnostic> Diagnostics)
        ResolveGradePathDefinitions(
            TerrainBuildSnapshot snapshot,
            GradePathModifierDefinition modifier,
            double curveTolerance,
            double gradePathTolerance)
    {
        var paths = new List<PathGrader.PathDefinition>();
        var sourceIds = new List<Guid>();
        double requestedEdgeLength = TerrainBuildHeuristics.GetGradePathCurveSamplingLength(modifier.Width);
        foreach (ResolvedSourceObject source in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, modifier.Paths))
        {
            if (source.Geometry is not Curve curve)
                continue;
            if (!RhinoSourceResolver.TryGetPolyline(
                    curve,
                    curveTolerance,
                    requireClosed: false,
                    requestedEdgeLength,
                    maxArea: 0.0,
                    out var polyline))
            {
                continue;
            }

            var pathXy = new double[polyline.Count * 2];
            var pathZ = new double[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                pathXy[i * 2] = polyline[i].X;
                pathXy[i * 2 + 1] = polyline[i].Y;
                pathZ[i] = polyline[i].Z;
            }

            double pathCutSlope = modifier.CutSlopeAngle > 0.0 ? modifier.CutSlopeAngle : modifier.SlopeAngle;
            paths.Add(new PathGrader.PathDefinition(
                pathXy,
                pathZ,
                polyline.Count,
                modifier.Width,
                pathCutSlope,
                modifier.MaxDistance,
                modifier.SlopeAngle,
                isClosed: curve.IsClosed));
            sourceIds.Add(source.ObjectId);
        }

        // Variable width is opt-in: with the toggle off the parked WidthEdges references stay on the
        // definition but never reach the resolver, so the corridor is a plain constant-Width path.
        if (!modifier.UseVariableWidth)
        {
            var constantWidth = new ResolvedGradePathDefinition[paths.Count];
            for (int i = 0; i < constantWidth.Length; i++)
                constantWidth[i] = new ResolvedGradePathDefinition(sourceIds[i], paths[i]);
            return (constantWidth, Array.Empty<VariablePathWidthResolver.Diagnostic>());
        }

        var widthEdges = new List<VariablePathWidthResolver.EdgeDefinition>();
        int edgeSourceIndex = 0;
        foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.WidthEdges))
        {
            if (RhinoSourceResolver.TryGetPolyline(
                    curve,
                    curveTolerance,
                    requireClosed: false,
                    requestedEdgeLength,
                    maxArea: 0.0,
                    out Polyline polyline) &&
                polyline.Count >= 2)
            {
                var xy = new double[polyline.Count * 2];
                for (int i = 0; i < polyline.Count; i++)
                {
                    xy[i * 2] = polyline[i].X;
                    xy[(i * 2) + 1] = polyline[i].Y;
                }
                widthEdges.Add(new VariablePathWidthResolver.EdgeDefinition(xy, polyline.Count, curve.IsClosed, edgeSourceIndex));
            }
            edgeSourceIndex++;
        }

        VariablePathWidthResolver.Result widthResult = VariablePathWidthResolver.Resolve(
            paths,
            widthEdges,
            new VariablePathWidthResolver.Options
            {
                MaxEdgeDistance = modifier.MaxEdgeDistance,
                Tolerance = gradePathTolerance
            });
        var resolved = new ResolvedGradePathDefinition[widthResult.Paths.Length];
        for (int i = 0; i < resolved.Length; i++)
            resolved[i] = new ResolvedGradePathDefinition(sourceIds[i], widthResult.Paths[i]);
        return (resolved, widthResult.Diagnostics);
    }

    internal static List<GradingPatch> BuildPadPatchSummaries(IReadOnlyList<PadGrader.PadBoundary> pads)
    {
        var patches = new List<GradingPatch>(pads.Count);
        for (int i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            double priority = ComputePadOwnershipPriority(pad);
            patches.Add(new GradingPatch
            {
                OwnerKey = $"pad:{i}",
                Kind = GradingPatchKind.Pad,
                Priority = priority,
                OwnedRegionLoopXy = (double[])pad.XyVertices.Clone(),
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(pad.XyVertices),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    private static double ComputePadOwnershipPriority(PadGrader.PadBoundary pad)
    {
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            sumX += pad.XyVertices[i * 2];
            sumY += pad.XyVertices[i * 2 + 1];
        }

        double cx = sumX / pad.VertexCount;
        double cy = sumY / pad.VertexCount;
        return pad.EvaluateZ(cx, cy);
    }

    internal static List<GradingPatch> BuildPathPatchSummaries(IReadOnlyList<PathGrader.PathDefinition> paths)
    {
        var patches = new List<GradingPatch>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            for (int v = 0; v < path.VertexCount; v++)
            {
                double x = path.XyVertices[v * 2];
                double y = path.XyVertices[v * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            double halfWidth = path.Width * 0.5;
            double shoulderAllowance = path.MaxDistance > 0.0
                ? path.MaxDistance
                : Math.Max(path.Width * 2.0, halfWidth);
            double expansion = halfWidth + shoulderAllowance;
            double[] ownedLoop =
            {
                minX - expansion, minY - expansion,
                maxX + expansion, minY - expansion,
                maxX + expansion, maxY + expansion,
                minX - expansion, maxY + expansion
            };

            patches.Add(new GradingPatch
            {
                OwnerKey = $"path:{i}",
                Kind = GradingPatchKind.Path,
                Priority = i,
                OwnedRegionLoopXy = ownedLoop,
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(ownedLoop),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    internal static GradingTopologyCacheEntry BuildPathTopologySummary(
        IReadOnlyList<double> inputVertices,
        int inputVertexCount,
        IReadOnlyList<int> inputFaces,
        int inputFaceCount,
        GradingResult? gradingResult,
        IReadOnlyList<GradingPatch> conservativePatchSummaries,
        IReadOnlyList<string> diagnostics)
    {
        IReadOnlyList<double> vertices = gradingResult?.Vertices ?? inputVertices;
        int vertexCount = gradingResult?.VertexCount ?? inputVertexCount;
        IReadOnlyList<int> faces = gradingResult?.Faces ?? inputFaces;
        int faceCount = gradingResult?.FaceCount ?? inputFaceCount;
        List<GradingPatch> patchSummaries = gradingResult?.PatchSummaries.Count > 0
            ? ClonePatchSummaries(gradingResult.PatchSummaries)
            : ClonePatchSummaries(conservativePatchSummaries);

        return new GradingTopologyCacheEntry
        {
            GraderKind = "Path",
            Fingerprint = 0,
            OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Path", vertices, vertexCount, faces, faceCount),
            Vertices = Array.Empty<double>(),
            VertexCount = vertexCount,
            Faces = Array.Empty<int>(),
            FaceCount = faceCount,
            PatchSummaries = patchSummaries,
            Diagnostics = diagnostics.ToList()
        };
    }

    internal static List<GradingPatch> ClonePatchSummaries(IReadOnlyList<GradingPatch> patchSummaries)
    {
        return patchSummaries
            .Select(static patch => new GradingPatch
            {
                OwnerKey = patch.OwnerKey,
                Kind = patch.Kind,
                Priority = patch.Priority,
                OwnedRegionLoopXy = (double[])patch.OwnedRegionLoopXy.Clone(),
                DaylightLoopXy = patch.DaylightLoopXy != null ? (double[])patch.DaylightLoopXy.Clone() : Array.Empty<double>(),
                StitchLoopXy = patch.StitchLoopXy != null ? (double[])patch.StitchLoopXy.Clone() : Array.Empty<double>(),
                DirtyBounds = patch.DirtyBounds,
                UsesFallbackBand = patch.UsesFallbackBand
            })
            .ToList();
    }
}
