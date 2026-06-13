using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using TriangleNet.Meshing;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Retaining-wall stage: planning walls from curve pairs, wall-strip usability, and rail-constraint insertion.
internal sealed partial class TerrainBuildService
{
    private static RhinoMesh ApplyRetainingWalls(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetainingWallModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double wallTolerance = toleranceProfile.RetainingWallTolerance(modifier.MaxWallWidth);
        double maxWallWidth = Math.Max(wallTolerance, modifier.MaxWallWidth);
        build.Diagnostics.Add($"Retaining Wall tolerance: {wallTolerance:G4}; max wall width: {maxWallWidth:G4}.");
        var resolveTimer = Stopwatch.StartNew();
        var wallCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.WallCurves);
        resolveTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Resolve",
            resolveTimer.Elapsed,
            $"{wallCurves.Count:N0} curve inputs",
            StageTimingDiagnosticThresholdMs);
        if (wallCurves.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no curve inputs.");
            return mesh;
        }

        var planTimer = Stopwatch.StartNew();
        var plan = RetainingWallPlannerCore.Plan(
            wallCurves,
            maxWallWidth,
            curveParsingTolerance: wallTolerance);
        planTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Plan",
            plan.Timing.Total > TimeSpan.Zero ? plan.Timing.Total : planTimer.Elapsed,
            DescribeRetainingWallPlanTiming(plan.Timing, plan.Walls.Count),
            StageTimingDiagnosticThresholdMs);
        foreach (var entry in plan.Report)
            build.Diagnostics.Add(entry.ToString());

        if (plan.Walls.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall produced no accepted wall pairs.");
            return mesh;
        }

        List<SurfaceRemesher.ConstraintPolyline> wallConstraints = new(plan.Walls.Count * 2);
        var wallOutputTimer = new Stopwatch();
        var constraintCurveTimer = new Stopwatch();
        int usableWallCount = 0;
        int wallBrepOutputCount = 0;
        foreach (var wall in plan.Walls)
        {
            constraintCurveTimer.Start();
            if (!IsWallStripUsable(wall.Rails, wallTolerance, out var stripMessage))
            {
                constraintCurveTimer.Stop();
                build.Diagnostics.Add($"Retaining wall pair ({wall.CurveA}, {wall.CurveB}) skipped: {stripMessage}");
                continue;
            }
            constraintCurveTimer.Stop();
            usableWallCount++;

            wallOutputTimer.Start();
            if (mode == TerrainBuildMode.Final)
            {
                if (wall.Brep != null)
                {
                    build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                    {
                        Geometry = wall.Brep,
                        Name = $"Wall {wall.CurveA}-{wall.CurveB}",
                        Kind = GeneratedObjectKind.RetainingWall,
                        LayerPath = TerrainDefinition.ResolveAuxiliaryLayerPath(modifier.OutputLayerPath ?? terrain.AuxiliaryLayerPath)
                    });
                    wallBrepOutputCount++;
                }
            }
            wallOutputTimer.Stop();

            constraintCurveTimer.Start();
            SurfaceRemesher.ConstraintPolyline[] wallSetConstraints = BuildWallConstraintCurves(wall.Rails, wallTolerance);
            constraintCurveTimer.Stop();
            if (wallSetConstraints.Length == 0)
                continue;

            wallConstraints.AddRange(wallSetConstraints);
        }
        build.RecordTiming(
            "Retaining Wall Outputs",
            wallOutputTimer.Elapsed,
            $"{wallBrepOutputCount:N0} Brep outputs from {usableWallCount:N0} usable walls",
            StageTimingDiagnosticThresholdMs);
        build.RecordTiming(
            "Retaining Wall Constraint Curves",
            constraintCurveTimer.Elapsed,
            $"{wallConstraints.Count:N0} raw rail constraints from {usableWallCount:N0} usable walls",
            StageTimingDiagnosticThresholdMs);

        int rawConstraintCount = wallConstraints.Count;
        var prepareTimer = Stopwatch.StartNew();
        wallConstraints = PrepareWallConstraintsForRemesh(mesh, wallConstraints, wallTolerance);
        prepareTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Constraint Prep",
            prepareTimer.Elapsed,
            $"{rawConstraintCount:N0} raw -> {wallConstraints.Count:N0} prepared constraints",
            StageTimingDiagnosticThresholdMs);

        if (wallConstraints.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no usable wall pairs to insert as breaklines.");
            return mesh;
        }

        var combineTimer = Stopwatch.StartNew();
        List<SurfaceRemesher.ConstraintPolyline> terrainElevationConstraints =
            CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints);
        List<SurfaceRemesher.ConstraintPolyline> remeshConstraints =
            CombineConstraints(terrainElevationConstraints, wallConstraints);
        combineTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Constraint Merge",
            combineTimer.Elapsed,
            $"{build.PersistentHardConstraints.Count:N0} hard + {build.PersistentElevationConstraints.Count:N0} elevation + {wallConstraints.Count:N0} wall -> {remeshConstraints.Count:N0} remesh constraints",
            StageTimingDiagnosticThresholdMs);

        var remeshTimer = Stopwatch.StartNew();
        var remeshed = RebuildMeshWithConstraints(
            snapshot,
            terrain,
            mesh,
            remeshConstraints,
            0.0,
            0.0,
            0.0,
            "Retaining Wall",
            build,
            out bool keptInputMesh,
            preferReducedInteriorSeed: true,
            addReducedInteriorGuideSeeds: false,
            addConstraintCorridorSeeds: false,
            toleranceOverride: wallTolerance,
            recordDetailedTimings: true);
        remeshTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Remesh",
            remeshTimer.Elapsed,
            keptInputMesh
                ? "kept upstream mesh"
                : ReferenceEquals(remeshed, mesh) ? "returned upstream mesh" : $"{remeshed.Vertices.Count:N0} verts, {remeshed.Faces.Count:N0} faces",
            StageTimingDiagnosticThresholdMs);

        if (!ReferenceEquals(remeshed, mesh))
        {
            var persistTimer = Stopwatch.StartNew();
            List<SurfaceRemesher.ConstraintPolyline> mergedConstraints = CombineConstraints(build.PersistentHardConstraints, wallConstraints);
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(mergedConstraints);
            persistTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Persist Constraints",
                persistTimer.Elapsed,
                $"{mergedConstraints.Count:N0} hard constraints",
                StageTimingDiagnosticThresholdMs);
            return remeshed;
        }

        if (keptInputMesh)
        {
            var fallbackTimer = Stopwatch.StartNew();
            bool inserted = TryInsertWallConstraintsIntoExistingMesh(
                mesh,
                wallConstraints,
                wallTolerance,
                build,
                reportFailures: true,
                afterCombinedRemeshFailed: true,
                out RhinoMesh insertedMesh);
            fallbackTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Topology Fallback",
                fallbackTimer.Elapsed,
                inserted ? $"{insertedMesh.Vertices.Count:N0} verts, {insertedMesh.Faces.Count:N0} faces" : "not inserted",
                StageTimingDiagnosticThresholdMs);

            if (!inserted)
                return remeshed;

            var persistTimer = Stopwatch.StartNew();
            List<SurfaceRemesher.ConstraintPolyline> mergedConstraints = CombineConstraints(build.PersistentHardConstraints, wallConstraints);
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(mergedConstraints);
            persistTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Persist Constraints",
                persistTimer.Elapsed,
                $"{mergedConstraints.Count:N0} hard constraints",
                StageTimingDiagnosticThresholdMs);
            return insertedMesh;
        }

        return remeshed;
    }

    private static string DescribeRetainingWallPlanTiming(RetainingWallPlannerCore.PlanTiming timing, int wallCount)
    {
        if (timing.Total <= TimeSpan.Zero)
            return $"{wallCount:N0} accepted walls";

        return $"{wallCount:N0} accepted walls; preprocess {FormatMilliseconds(timing.Preprocess)}, pairing {FormatMilliseconds(timing.Pairing)}, interactions {FormatMilliseconds(timing.Interactions)}, wall geometry {FormatMilliseconds(timing.Walls)}";
    }

    private static string FormatMilliseconds(TimeSpan elapsed)
    {
        return $"{elapsed.TotalMilliseconds:0.###} ms";
    }

    private static bool IsWallStripUsable(RetainingWallPlannerCore.WallRails rails, double tolerance, out string message)
    {
        message = string.Empty;
        int minimum = rails.IsClosed ? 3 : 2;
        if (rails.ToePoints.Length < minimum || rails.TopPoints.Length < minimum)
        {
            message = "too few rail points.";
            return false;
        }

        if (rails.MinWidth < Math.Max(tolerance * 0.1, 1e-6))
        {
            message = "strip width collapses too tightly.";
            return false;
        }

        return true;
    }

    private static SurfaceRemesher.ConstraintPolyline[] BuildWallConstraintCurves(RetainingWallPlannerCore.WallRails rails, double tolerance)
    {
        var curves = new List<SurfaceRemesher.ConstraintPolyline>(2);
        int minimum = rails.IsClosed ? 3 : 2;
        SurfaceRemesher.ConstraintPolyline toeCurve = CreateWallRailConstraint(rails.ToePoints, rails.IsClosed, tolerance);
        if (toeCurve.PointCount >= minimum)
            curves.Add(toeCurve);

        SurfaceRemesher.ConstraintPolyline topCurve = CreateWallRailConstraint(rails.TopPoints, rails.IsClosed, tolerance);
        if (topCurve.PointCount >= minimum)
            curves.Add(topCurve);

        return curves.ToArray();
    }

    private static SurfaceRemesher.ConstraintPolyline CreateWallRailConstraint(Point3d[] railPoints, bool isClosed, double tolerance)
    {
        int minimum = isClosed ? 3 : 2;
        if (railPoints.Length < minimum)
            return new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, isClosed, PreserveInputElevation: true);

        double tolSq = Math.Max(tolerance, 1e-6);
        tolSq *= tolSq;
        var points = new List<Point3d>(railPoints.Length);
        for (int i = 0; i < railPoints.Length; i++)
        {
            Point3d point = railPoints[i];
            if (points.Count > 0 && DistanceSquared2D(points[^1], point) <= tolSq)
            {
                points[^1] = point;
                continue;
            }

            points.Add(point);
        }

        if (isClosed && points.Count > 1 && DistanceSquared2D(points[0], points[^1]) <= tolSq)
            points.RemoveAt(points.Count - 1);

        if (points.Count < minimum)
            return new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, isClosed, PreserveInputElevation: true);

        var values = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            values[i * 3] = points[i].X;
            values[i * 3 + 1] = points[i].Y;
            values[i * 3 + 2] = points[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(values, points.Count, isClosed, PreserveInputElevation: true);
    }

    private static List<SurfaceRemesher.ConstraintPolyline> PrepareWallConstraintsForRemesh(
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        var prepared = new List<SurfaceRemesher.ConstraintPolyline>(constraints.Count);
        if (constraints.Count == 0)
            return prepared;

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
        {
            foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
            {
                SurfaceRemesher.ConstraintPolyline cleaned = CleanWallConstraintPolyline(constraint, tolerance);
                if (cleaned.PointCount >= 2)
                    prepared.Add(cleaned);
            }

            return prepared;
        }

        var snapper = new ConstraintCoincidenceSnapper(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            Math.Max(tolerance, 1e-6));

        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            SurfaceRemesher.ConstraintPolyline snapped = snapper.SnapConstraintPolyline(constraint);
            SurfaceRemesher.ConstraintPolyline cleaned = CleanWallConstraintPolyline(snapped, tolerance);
            if (cleaned.PointCount >= 2)
                prepared.Add(cleaned);
        }

        return CombineConstraints(Array.Empty<SurfaceRemesher.ConstraintPolyline>(), prepared);
    }

    private static SurfaceRemesher.ConstraintPolyline CleanWallConstraintPolyline(
        SurfaceRemesher.ConstraintPolyline constraint,
        double tolerance)
    {
        if (constraint.PointCount < 2)
            return constraint;

        double tolSq = Math.Max(tolerance, 1e-6);
        tolSq *= tolSq;
        var points = new List<double>(constraint.PointCount * 3);
        for (int i = 0; i < constraint.PointCount; i++)
        {
            double x = constraint.Points[i * 3];
            double y = constraint.Points[i * 3 + 1];
            double z = constraint.Points[i * 3 + 2];
            if (points.Count >= 3)
            {
                double dx = points[^3] - x;
                double dy = points[^2] - y;
                if ((dx * dx) + (dy * dy) <= tolSq)
                {
                    points[^3] = x;
                    points[^2] = y;
                    points[^1] = z;
                    continue;
                }
            }

            points.Add(x);
            points.Add(y);
            points.Add(z);
        }

        int pointCount = points.Count / 3;
        int minimum = constraint.IsClosed ? 3 : 2;
        return pointCount >= minimum
            ? new SurfaceRemesher.ConstraintPolyline(points.ToArray(), pointCount, constraint.IsClosed, constraint.PreserveInputElevation)
            : new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, constraint.IsClosed, constraint.PreserveInputElevation);
    }

    private static bool TryInsertWallConstraintsIntoExistingMesh(
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> wallConstraints,
        double tolerance,
        TerrainBuildResult build,
        bool reportFailures,
        bool afterCombinedRemeshFailed,
        out RhinoMesh insertedMesh)
    {
        insertedMesh = mesh;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            if (reportFailures)
                build.Diagnostics.Add(errorMessage ?? "Retaining Wall topology insertion could not extract the upstream mesh.");
            return false;
        }

        if (!MeshConstraintTopologyInserter.TryInsert(
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                wallConstraints,
                tolerance,
                out double[] outputVertices,
                out int outputVertexCount,
                out int[] outputFaces,
                out int outputFaceCount,
                out string? topologyError))
        {
            if (reportFailures)
                build.Diagnostics.Add(topologyError ?? "Retaining Wall topology insertion could not insert wall constraints into the existing mesh.");
            return false;
        }

        if (!TopologyChanged(vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, outputVertices, outputVertexCount, outputFaces, outputFaceCount, tolerance))
        {
            if (reportFailures)
                build.Diagnostics.Add("Retaining Wall topology insertion found no terrain faces crossed by wall constraints.");
            return false;
        }

        var inputBoundary = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, mesh.Faces.Count);
        var outputBoundary = MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount);
        if (!IsTopologyInsertionBoundarySafe(inputBoundary, outputBoundary, out string boundaryMessage))
        {
            if (reportFailures)
                build.Diagnostics.Add($"Retaining Wall topology insertion rejected: {boundaryMessage}");
            return false;
        }

        ApplyPreservedConstraintElevations(outputVertices, wallConstraints, tolerance);
        insertedMesh = BuildMeshFromArrays(outputVertices, outputFaces);
        build.Diagnostics.Add(afterCombinedRemeshFailed
            ? "Retaining Wall topology fallback inserted wall breaklines into the existing mesh after combined remesh failed."
            : "Retaining Wall topology insertion inserted wall breaklines into the existing mesh.");
        return true;
    }

}
