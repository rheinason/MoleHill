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
        double railCleanupTolerance = Math.Max(
            wallTolerance,
            Math.Min(toleranceProfile.DetailSize * 0.10, maxWallWidth * 0.05));
        build.Diagnostics.Add($"Retaining Wall tolerance: {wallTolerance:G4}; bounded rail cleanup: {railCleanupTolerance:G4}; max wall width: {maxWallWidth:G4}.");
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
            curveParsingTolerance: wallTolerance,
            curveCleanupTolerance: railCleanupTolerance);
        planTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Plan",
            plan.Timing.Total > TimeSpan.Zero ? plan.Timing.Total : planTimer.Elapsed,
            DescribeRetainingWallPlanTiming(plan.Timing, plan.Walls.Count),
            StageTimingDiagnosticThresholdMs);
        foreach (var entry in plan.Report)
        {
            build.Diagnostics.Add(entry.ToString());
            AddRetainingWallReportOverlay(build, modifier, wallCurves, entry, wallTolerance);
        }

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

        var topologyTimer = Stopwatch.StartNew();
        bool inserted = TryInsertWallConstraintsIntoExistingMesh(
            mesh,
            wallConstraints,
            wallTolerance,
            build,
            reportFailures: true,
            afterCombinedRemeshFailed: false,
            out RhinoMesh insertedMesh);
        topologyTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Topology Insert",
            topologyTimer.Elapsed,
            inserted ? $"{insertedMesh.Vertices.Count:N0} verts, {insertedMesh.Faces.Count:N0} faces" : "not inserted; trying constrained rebuild",
            StageTimingDiagnosticThresholdMs);

        if (inserted)
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
            return insertedMesh;
        }

        AddRetainingWallConstraintOverlay(
            build,
            modifier,
            wallConstraints,
            RuntimeOverlaySeverity.Warning,
            "retaining_wall.local_topology_fallback",
            "Local wall-breakline insertion was rejected; the constrained rebuild fallback was used.",
            "Topology fallback");

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

        AddRetainingWallConstraintOverlay(
            build,
            modifier,
            wallConstraints,
            RuntimeOverlaySeverity.Error,
            "retaining_wall.constraint_insertion_failed",
            "Wall breaklines could not be inserted without damaging terrain topology; the upstream mesh was retained.",
            "Breaklines failed");

        return remeshed;
    }

    private static void AddRetainingWallReportOverlay(
        TerrainBuildResult build,
        RetainingWallModifierDefinition modifier,
        IReadOnlyList<Curve> curves,
        RetainingWallPlannerCore.ReportEntry entry,
        double tolerance)
    {
        if (entry.Level == RetainingWallPlannerCore.ReportLevel.Info &&
            entry.Reason is not RetainingWallPlannerCore.ReportReason.RailDetailSimplified and
            not RetainingWallPlannerCore.ReportReason.CrossingWalls)
            return;

        var primitives = new List<RuntimeOverlayPrimitive>();
        bool informational = entry.Level == RetainingWallPlannerCore.ReportLevel.Info;
        int contextColor = informational
            ? System.Drawing.Color.FromArgb(112, 151, 170).ToArgb()
            : System.Drawing.Color.FromArgb(170, 135, 78).ToArgb();
        int focusColor = informational
            ? System.Drawing.Color.FromArgb(76, 132, 158).ToArgb()
            : System.Drawing.Color.FromArgb(235, 70, 45).ToArgb();
        foreach (int curveIndex in entry.RelatedCurves)
        {
            if (curveIndex < 0 || curveIndex >= curves.Count)
                continue;

            Point3d[] points = SampleDiagnosticCurve(curves[curveIndex], tolerance);
            if (points.Length >= 2)
                primitives.Add(RuntimeOverlayPrimitive.Polyline(points, curves[curveIndex].IsClosed, thickness: 2, colorArgb: contextColor));
        }

        if (primitives.Count == 0)
            return;

        foreach (Line segment in entry.FocusSegments)
        {
            if (segment.IsValid && segment.Length > tolerance)
                primitives.Add(RuntimeOverlayPrimitive.Polyline(new[] { segment.From, segment.To }, thickness: 5, colorArgb: focusColor));
        }

        Point3d anchor = entry.Location is Point3d location && location.IsValid
            ? location
            : primitives[0].Points[primitives[0].Points.Length / 2];
        string shortLabel = entry.Reason switch
        {
            RetainingWallPlannerCore.ReportReason.AmbiguousPair => "Ambiguous pair",
            RetainingWallPlannerCore.ReportReason.InvalidStationMapping when entry.Message.Contains("do not overlap", StringComparison.OrdinalIgnoreCase) => "Ends do not match",
            RetainingWallPlannerCore.ReportReason.InvalidStationMapping => "Rail doubles back",
            RetainingWallPlannerCore.ReportReason.RailDetailSimplified => "Tiny rail detail cleaned",
            RetainingWallPlannerCore.ReportReason.SelfIntersectingRail => "Rail crosses itself",
            RetainingWallPlannerCore.ReportReason.CrossingWalls when informational => "Walls cross in plan",
            RetainingWallPlannerCore.ReportReason.CrossingWalls => "Walls may overlap",
            RetainingWallPlannerCore.ReportReason.CornerRejected => "Corner rejected",
            RetainingWallPlannerCore.ReportReason.SolidFailed => "Could not build wall",
            RetainingWallPlannerCore.ReportReason.NoPair => "Missing matching rail",
            _ => entry.Reason.ToString()
        };
        primitives.Add(RuntimeOverlayPrimitive.Marker(anchor, size: 7, colorArgb: focusColor));
        primitives.Add(RuntimeOverlayPrimitive.Dot(anchor, shortLabel, colorArgb: focusColor));

        string relatedCurveKey = entry.RelatedCurves.Count > 0
            ? string.Join("-", entry.RelatedCurves)
            : "x";
        string stableSuffix = $"{entry.Reason}:{relatedCurveKey}:{entry.PairIndex?.ToString() ?? "x"}";
        build.RuntimeOverlays.Add(new RuntimeOverlayItem
        {
            StableId = $"retaining-wall:{modifier.Id:N}:{stableSuffix}",
            Owner = new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id),
            Severity = entry.Level switch
            {
                RetainingWallPlannerCore.ReportLevel.Error => RuntimeOverlaySeverity.Error,
                RetainingWallPlannerCore.ReportLevel.Warning => RuntimeOverlaySeverity.Warning,
                _ => RuntimeOverlaySeverity.Information
            },
            Code = $"retaining_wall.{ToDiagnosticCode(entry.Reason)}",
            Message = entry.Message,
            ShortLabel = shortLabel,
            Primitives = primitives
        });
    }

    private static Point3d[] SampleDiagnosticCurve(Curve curve, double tolerance)
    {
        if (curve.TryGetPolyline(out Polyline polyline))
        {
            Point3d[] source = polyline.ToArray();
            if (source.Length <= 256)
                return source;

            int step = (int)Math.Ceiling(source.Length / 255.0);
            var sampled = new List<Point3d>(256);
            for (int i = 0; i < source.Length; i += step)
                sampled.Add(source[i]);
            if (sampled[^1] != source[^1])
                sampled.Add(source[^1]);
            return sampled.ToArray();
        }

        double length = Math.Max(curve.GetLength(), tolerance);
        int segmentCount = Math.Clamp((int)Math.Ceiling(length / Math.Max(tolerance * 20.0, length / 255.0)), 8, 255);
        var points = new Point3d[segmentCount + 1];
        for (int i = 0; i <= segmentCount; i++)
            points[i] = curve.PointAtNormalizedLength((double)i / segmentCount);
        return points;
    }

    private static string ToDiagnosticCode(RetainingWallPlannerCore.ReportReason reason)
    {
        string value = reason.ToString();
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(value[i]));
        }
        return builder.ToString();
    }

    private static void AddRetainingWallConstraintOverlay(
        TerrainBuildResult build,
        RetainingWallModifierDefinition modifier,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        RuntimeOverlaySeverity severity,
        string code,
        string message,
        string shortLabel)
    {
        var primitives = new List<RuntimeOverlayPrimitive>();
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            var points = new Point3d[constraint.PointCount];
            for (int i = 0; i < constraint.PointCount; i++)
                points[i] = new Point3d(constraint.Points[i * 3], constraint.Points[i * 3 + 1], constraint.Points[i * 3 + 2]);
            if (points.Length >= 2)
                primitives.Add(RuntimeOverlayPrimitive.Polyline(points, constraint.IsClosed, thickness: 3));
        }

        if (primitives.Count == 0)
            return;

        Point3d anchor = primitives[0].Points[primitives[0].Points.Length / 2];
        primitives.Add(RuntimeOverlayPrimitive.Dot(anchor, shortLabel));
        build.RuntimeOverlays.Add(new RuntimeOverlayItem
        {
            StableId = $"retaining-wall:{modifier.Id:N}:{code}",
            Owner = new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id),
            Severity = severity,
            Code = code,
            Message = message,
            ShortLabel = shortLabel,
            Primitives = primitives
        });
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
