using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Retaining-wall stage: planning walls from curve pairs, wall-strip usability, and rail-constraint insertion.
internal static partial class RetainingWallStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var retainingWall = (RetainingWallModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Retaining Wall",
            TerrainBuildService.ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, retainingWall, c.CurrentMeshFingerprint),
            () => input == null ? TerrainBuildService.WarnMissingMesh(c.Build, retainingWall.Label) : ApplyRetainingWalls(c.Snapshot, c.Terrain, input, retainingWall, c.Build, c.Mode, c.RuntimeCache, c.StageKey, c.ShouldCancel),
            result => TerrainBuildService.DescribeModifierMeshResult(retainingWall.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    private static RhinoMesh ApplyRetainingWalls(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetainingWallModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        Func<bool>? shouldCancel)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
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
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        if (wallCurves.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no curve inputs.");
            return mesh;
        }

        var planTimer = Stopwatch.StartNew();
        // Only Final mode publishes the wall solids (see the AuxiliaryObjects add below). Preview
        // discarded them, so it no longer asks for them: the rails, pairing and constraints are
        // identical either way, and the Brep loft is the expensive half of BuildWalls.
        bool buildWallSolids = mode == TerrainBuildMode.Final;

        // Planning reads the wall curves and the wall tolerances and nothing else — not the terrain.
        // The wall stage's own fingerprint includes the upstream mesh, so an upstream Z edit misses
        // the stage; this key does not, and the plan survives it.
        ulong planFingerprint = ComputeRetainingWallPlanFingerprint(
            snapshot,
            modifier,
            maxWallWidth,
            wallTolerance,
            railCleanupTolerance);
        // A plan built without solids cannot serve a build that needs them, so BuiltSolids is part of
        // the match rather than a note on the entry.
        bool planFromCache =
            runtimeCache.RetainingWallPlanEntries.TryGetValue(stageKey, out RetainingWallPlanCacheEntry? cachedPlan) &&
            cachedPlan.Fingerprint == planFingerprint &&
            cachedPlan.BuiltSolids == buildWallSolids;

        RetainingWallPlannerCore.PlanResult plan;
        if (planFromCache)
        {
            plan = cachedPlan!.Plan;
        }
        else
        {
            plan = RetainingWallPlannerCore.Plan(
                wallCurves,
                maxWallWidth,
                curveParsingTolerance: wallTolerance,
                curveCleanupTolerance: railCleanupTolerance,
                buildSolids: buildWallSolids);

            runtimeCache.RetainingWallPlanEntries[stageKey] = new RetainingWallPlanCacheEntry
            {
                Fingerprint = planFingerprint,
                BuiltSolids = buildWallSolids,
                Plan = plan
            };
        }
        planTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Plan",
            planFromCache
                ? planTimer.Elapsed
                : plan.Timing.Total > TimeSpan.Zero ? plan.Timing.Total : planTimer.Elapsed,
            planFromCache
                ? TerrainBuildService.AppendCacheHitDetail($"{plan.Walls.Count:N0} planned walls")
                : DescribeRetainingWallPlanTiming(plan.Timing, plan.Walls.Count),
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        foreach (var entry in plan.Report)
        {
            build.Diagnostics.Add(entry.ToString());
            AddRetainingWallReportOverlay(build, modifier, wallCurves, entry, wallTolerance);
        }

        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        if (plan.Walls.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall produced no accepted wall pairs.");
            return mesh;
        }

        List<ConstraintPolyline> wallConstraints = new(plan.Walls.Count * 2);
        var wallOutputTimer = new Stopwatch();
        var constraintCurveTimer = new Stopwatch();
        int usableWallCount = 0;
        int wallBrepOutputCount = 0;
        foreach (var wall in plan.Walls)
        {
            TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
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
                        Role = LayerRole.Walls,
                        // The plan is the cache's — a cold plan is stored above before this runs — and
                        // outlives this build, so always publish a duplicate: the same contract
                        // CloneGeneratedObject keeps for the stage cache. Publishing a cold plan's own
                        // Brep left the output and the cached plan sharing one native object.
                        Geometry = wall.Brep.DuplicateBrep(),
                        Name = $"Wall {wall.CurveA}-{wall.CurveB}",
                        Kind = GeneratedObjectKind.RetainingWall,
                        LayerPath = snapshot.LayerRoles.Path(LayerRole.Walls)
                    });
                    wallBrepOutputCount++;
                }

                AddRetainingWallRailOutput(
                    build,
                    snapshot,
                    wall.Rails.ToePoints,
                    wall.Rails.IsClosed,
                    $"Wall {wall.CurveA}-{wall.CurveB} toe");
                AddRetainingWallRailOutput(
                    build,
                    snapshot,
                    wall.Rails.TopPoints,
                    wall.Rails.IsClosed,
                    $"Wall {wall.CurveA}-{wall.CurveB} top");
            }
            wallOutputTimer.Stop();

            constraintCurveTimer.Start();
            ConstraintPolyline[] wallSetConstraints = BuildWallConstraintCurves(wall.Rails, wallTolerance);
            constraintCurveTimer.Stop();
            if (wallSetConstraints.Length == 0)
                continue;

            wallConstraints.AddRange(wallSetConstraints);
        }
        build.RecordTiming(
            "Retaining Wall Outputs",
            wallOutputTimer.Elapsed,
            $"{wallBrepOutputCount:N0} Brep outputs from {usableWallCount:N0} usable walls",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        build.RecordTiming(
            "Retaining Wall Constraint Curves",
            constraintCurveTimer.Elapsed,
            $"{wallConstraints.Count:N0} raw rail constraints from {usableWallCount:N0} usable walls",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        // Grade before the rails go in. Insertion forces the terrain to the rail elevations, so a batter
        // measured after it starts with zero height difference at its own foot, reports Flat, and emits
        // nothing at all — the build looks clean and grades nothing. Found live on a 4 m wall.
        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        RhinoMesh ungraded = mesh;
        mesh = ApplyRetainingWallGrading(
            snapshot, terrain, mesh, modifier, plan.Walls, wallTolerance, build, runtimeCache, mode);
        bool railsGraded = !ReferenceEquals(mesh, ungraded);

        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        int rawConstraintCount = wallConstraints.Count;
        var prepareTimer = Stopwatch.StartNew();
        wallConstraints = PrepareWallConstraintsForRemesh(mesh, wallConstraints, wallTolerance);
        prepareTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Constraint Prep",
            prepareTimer.Elapsed,
            $"{rawConstraintCount:N0} raw -> {wallConstraints.Count:N0} prepared constraints",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        if (wallConstraints.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no usable wall pairs to insert as breaklines.");
            return mesh;
        }

        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        if (railsGraded)
        {
            var adoptTimer = Stopwatch.StartNew();
            bool adopted = TryAdoptGradedRails(mesh, wallConstraints, wallTolerance, build, out RhinoMesh adoptedMesh, out var tracedRails);
            adoptTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Adopt Graded Rails",
                adoptTimer.Elapsed,
                adopted ? $"{tracedRails.Count:N0} rails already in the graded terrain" : "not all rails traced; inserting",
                TerrainBuildService.StageTimingDiagnosticThresholdMs);
            if (adopted)
            {
                List<ConstraintPolyline> mergedConstraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, tracedRails);
                build.PersistentHardConstraints.Clear();
                build.PersistentHardConstraints.AddRange(mergedConstraints);
                return adoptedMesh;
            }
        }

        var topologyTimer = Stopwatch.StartNew();
        bool inserted = TerrainBuildService.TryInsertWallConstraintsWindowed(
                            mesh,
                            wallConstraints,
                            wallTolerance,
                            build,
                            useQualityPatch: modifier.GradesTerrain,
                            runtimeCache,
                            stageKey + ":insert",
                            out RhinoMesh insertedMesh) ||
                        TryInsertWallConstraintsIntoExistingMesh(
                            mesh,
                            wallConstraints,
                            wallTolerance,
                            build,
                            useQualityPatch: modifier.GradesTerrain,
                            reportFailures: true,
                            afterCombinedRemeshFailed: false,
                            out insertedMesh);
        topologyTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Topology Insert",
            topologyTimer.Elapsed,
            inserted ? $"{insertedMesh.Vertices.Count:N0} verts, {insertedMesh.Faces.Count:N0} faces" : "not inserted; trying constrained rebuild",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        if (inserted)
        {
            var persistTimer = Stopwatch.StartNew();
            List<ConstraintPolyline> mergedConstraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, wallConstraints);
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(mergedConstraints);
            persistTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Persist Constraints",
                persistTimer.Elapsed,
                $"{mergedConstraints.Count:N0} hard constraints",
                TerrainBuildService.StageTimingDiagnosticThresholdMs);
            return insertedMesh;
        }

        // Breakline mode keeps the rails and every existing breakline and contour exactly as drawn. Where a rail
        // crosses one of them at another height no terrain can honour both, and the constrained rebuild could
        // only fail or tear the terrain trying (11 s, then discarded, on every edit of the contour park). Say
        // where the conflict is instead; the user edits the lines or grades the wall.
        if (!modifier.GradesTerrain)
        {
            List<BreaklineHeightConflicts.Conflict> conflicts = BreaklineHeightConflicts.Find(
                wallConstraints,
                TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints),
                wallTolerance);
            if (conflicts.Count > 0)
            {
                string where = string.Join("; ", conflicts.Take(3).Select(c =>
                    FormattableString.Invariant($"({c.X:0.##}, {c.Y:0.##}) rail at {c.ZA:0.###}, existing line at {c.ZB:0.###}")));
                string message =
                    $"Wall rails cross {conflicts.Count:N0} existing breakline or contour segment(s) at a different height, e.g. {where}. " +
                    "Breakline mode keeps both as drawn, so the walls were not inserted: edit the conflicting lines, or switch the wall to Grade.";
                build.Diagnostics.Add("Retaining Wall: " + message);
                AddRetainingWallConstraintOverlay(
                    build,
                    modifier,
                    wallConstraints,
                    RuntimeOverlaySeverity.Error,
                    "retaining_wall.rails_cross_breaklines",
                    message,
                    "Breakline conflict");
                return mesh;
            }
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
        List<ConstraintPolyline> terrainElevationConstraints =
            TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints);
        List<ConstraintPolyline> remeshConstraints =
            TerrainBuildService.CombineConstraints(terrainElevationConstraints, wallConstraints);
        combineTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Constraint Merge",
            combineTimer.Elapsed,
            $"{build.PersistentHardConstraints.Count:N0} hard + {build.PersistentElevationConstraints.Count:N0} elevation + {wallConstraints.Count:N0} wall -> {remeshConstraints.Count:N0} remesh constraints",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        var remeshTimer = Stopwatch.StartNew();
        var remeshed = TerrainBuildService.RebuildMeshWithConstraints(
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
            // Never PREFER the reduced-interior seed. That seeds the CDT from the boundary and the
            // constraints alone, so every interior vertex is discarded — the upstream Remesh's detail
            // and any batter graded above. It is taken before the full-seed attempt is even evaluated,
            // so as a preference it is not a fallback at all. It remains reachable when the full-seed
            // attempt is rejected, which is what the detail guard below exists to contain.
            preferReducedInteriorSeed: false,
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
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        // Inserting breaklines ADDS vertices; it never removes the terrain. A rebuild that comes back
        // with markedly fewer is one that reseeded from the boundary and threw the interior away, and
        // shipping it silently destroys an upstream Remesh — measured on this stage at 2,828 faces down
        // to 249. Losing the wall breaklines is recoverable and visible; losing the terrain is neither.
        if (!ReferenceEquals(remeshed, mesh) && !keptInputMesh &&
            remeshed.Vertices.Count < mesh.Vertices.Count * TerrainBuildService.RetainingWallRebuildMinimumVertexRatio)
        {
            build.Diagnostics.Add(
                $"Retaining Wall constrained rebuild discarded terrain detail " +
                $"({mesh.Vertices.Count:N0} -> {remeshed.Vertices.Count:N0} verts); the incoming mesh was kept " +
                "and the wall breaklines were not inserted.");
            AddRetainingWallConstraintOverlay(
                build,
                modifier,
                wallConstraints,
                RuntimeOverlaySeverity.Error,
                "retaining_wall.rebuild_discarded_detail",
                "The constrained rebuild would have discarded terrain detail; the incoming mesh was retained.",
                "Detail loss");
            return mesh;
        }

        // Nor may it open the terrain. The vertex floor above only catches a rebuild that threw the interior
        // away; one that keeps its vertices but tears holes around the rails passed it — two extra boundary
        // loops on a ring wall beside a graded path, in both wall modes — and shipped as a success. The
        // bar is the input's own topology, never absolute health: a terrain may carry a hole of its own.
        if (!ReferenceEquals(remeshed, mesh) && !keptInputMesh &&
            RhinoGeometryConversions.TryExtractMeshData(mesh, out _, out _, out int[] inputFaces, out int inputFaceCount, out _) &&
            RhinoGeometryConversions.TryExtractMeshData(remeshed, out _, out _, out int[] outputFaces, out int outputFaceCount, out _) &&
            !GradingTopologyDiagnostics.IsNotWorseThanInput(inputFaces, inputFaceCount, outputFaces, outputFaceCount, out string? damage))
        {
            build.Diagnostics.Add(
                $"Retaining Wall constrained rebuild damaged the terrain ({damage}); the incoming mesh was kept " +
                "and the wall breaklines were not inserted.");
            AddRetainingWallConstraintOverlay(
                build,
                modifier,
                wallConstraints,
                RuntimeOverlaySeverity.Error,
                "retaining_wall.rebuild_damaged_topology",
                "The constrained rebuild would have opened the terrain; the incoming mesh was retained.",
                "Topology damage");
            return mesh;
        }

        if (!ReferenceEquals(remeshed, mesh))
        {
            var persistTimer = Stopwatch.StartNew();
            List<ConstraintPolyline> mergedConstraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, wallConstraints);
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(mergedConstraints);
            persistTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Persist Constraints",
                persistTimer.Elapsed,
                $"{mergedConstraints.Count:N0} hard constraints",
                TerrainBuildService.StageTimingDiagnosticThresholdMs);
            return remeshed;
        }

        AddRetainingWallConstraintOverlay(
            build,
            modifier,
            wallConstraints,
            RuntimeOverlaySeverity.Error,
            "retaining_wall.constraint_insertion_failed",
            "Wall breaklines could not be inserted without damaging terrain topology; the incoming mesh was retained.",
            "Breaklines failed");

        return remeshed;
    }

    /// <summary>
    /// Everything <see cref="RetainingWallPlannerCore.Plan"/> reads, and nothing else. Deliberately
    /// excludes the upstream mesh: that is the whole reason this key exists separately from
    /// <c>TerrainBuildService.ComputeModifierStageFingerprint</c>. Adding an input to the planner means adding it here.
    /// </summary>
    private static ulong ComputeRetainingWallPlanFingerprint(
        TerrainBuildSnapshot snapshot,
        RetainingWallModifierDefinition modifier,
        double maxWallWidth,
        double wallTolerance,
        double railCleanupTolerance)
    {
        var builder = new FingerprintBuilder();
        builder.Add(nameof(ComputeRetainingWallPlanFingerprint));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.WallCurves));
        builder.Add(maxWallWidth);
        builder.Add(wallTolerance);
        builder.Add(railCleanupTolerance);
        return builder.ToUInt64();
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
        IReadOnlyList<ConstraintPolyline> constraints,
        RuntimeOverlaySeverity severity,
        string code,
        string message,
        string shortLabel)
    {
        var primitives = new List<RuntimeOverlayPrimitive>();
        foreach (ConstraintPolyline constraint in constraints)
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

        if (rails.MinWidth < Math.Max(tolerance * 0.1, double.Epsilon))
        {
            message = "strip width collapses too tightly.";
            return false;
        }

        return true;
    }

    private static void AddRetainingWallRailOutput(
        TerrainBuildResult build,
        TerrainBuildSnapshot snapshot,
        Point3d[] points,
        bool isClosed,
        string name)
    {
        int minimum = isClosed ? 3 : 2;
        if (points.Length < minimum)
            return;

        Point3d[] displayPoints = points;
        if (isClosed && points[0] != points[^1])
        {
            displayPoints = new Point3d[points.Length + 1];
            Array.Copy(points, displayPoints, points.Length);
            displayPoints[^1] = points[0];
        }

        build.AuxiliaryObjects.Add(new GeneratedRhinoObject
        {
            Role = LayerRole.Walls,
            Geometry = new PolylineCurve(displayPoints),
            Name = name,
            Kind = GeneratedObjectKind.RetainingWall,
            AppearanceSource = GeneratedAppearanceSource.Layer,
            LayerPath = snapshot.LayerRoles.Path(LayerRole.Walls)
        });
    }

    private static ConstraintPolyline[] BuildWallConstraintCurves(RetainingWallPlannerCore.WallRails rails, double tolerance)
    {
        var curves = new List<ConstraintPolyline>(2);
        int minimum = rails.IsClosed ? 3 : 2;
        ConstraintPolyline toeCurve = CreateWallRailConstraint(rails.ToePoints, rails.IsClosed, tolerance);
        if (toeCurve.PointCount >= minimum)
            curves.Add(toeCurve);

        ConstraintPolyline topCurve = CreateWallRailConstraint(rails.TopPoints, rails.IsClosed, tolerance);
        if (topCurve.PointCount >= minimum)
            curves.Add(topCurve);

        return curves.ToArray();
    }

    private static ConstraintPolyline CreateWallRailConstraint(Point3d[] railPoints, bool isClosed, double tolerance)
    {
        int minimum = isClosed ? 3 : 2;
        if (railPoints.Length < minimum)
            return new ConstraintPolyline(Array.Empty<double>(), 0, isClosed, PreserveInputElevation: true);

        double tolSq = Math.Max(Math.Abs(tolerance), double.Epsilon);
        tolSq *= tolSq;
        var points = new List<Point3d>(railPoints.Length);
        for (int i = 0; i < railPoints.Length; i++)
        {
            Point3d point = railPoints[i];
            if (points.Count > 0 && TerrainBuildService.DistanceSquared2D(points[^1], point) <= tolSq)
            {
                points[^1] = point;
                continue;
            }

            points.Add(point);
        }

        if (isClosed && points.Count > 1 && TerrainBuildService.DistanceSquared2D(points[0], points[^1]) <= tolSq)
            points.RemoveAt(points.Count - 1);

        if (points.Count < minimum)
            return new ConstraintPolyline(Array.Empty<double>(), 0, isClosed, PreserveInputElevation: true);

        var values = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            values[i * 3] = points[i].X;
            values[i * 3 + 1] = points[i].Y;
            values[i * 3 + 2] = points[i].Z;
        }

        return new ConstraintPolyline(values, points.Count, isClosed, PreserveInputElevation: true);
    }

    private static List<ConstraintPolyline> PrepareWallConstraintsForRemesh(
        RhinoMesh mesh,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance)
    {
        var prepared = new List<ConstraintPolyline>(constraints.Count);
        if (constraints.Count == 0)
            return prepared;

        // Counts from the extraction, never from the Rhino mesh: the extraction normalizes a copy, so
        // mesh.Vertices.Count/mesh.Faces.Count can describe a different mesh than these arrays do.
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh,
                out var vertices,
                out int meshVertexCount,
                out var faces,
                out int meshFaceCount,
                out _))
        {
            foreach (ConstraintPolyline constraint in constraints)
            {
                ConstraintPolyline cleaned = CleanWallConstraintPolyline(constraint, tolerance);
                if (cleaned.PointCount >= 2)
                    prepared.Add(cleaned);
            }

            return prepared;
        }

        var snapper = ConstraintCoincidenceSnapper.ForConstraints(
            vertices,
            meshVertexCount,
            faces,
            meshFaceCount,
            Math.Max(Math.Abs(tolerance), double.Epsilon),
            constraints);

        foreach (ConstraintPolyline constraint in constraints)
        {
            ConstraintPolyline snapped = snapper.SnapConstraintPolyline(constraint);
            ConstraintPolyline cleaned = CleanWallConstraintPolyline(snapped, tolerance);
            if (cleaned.PointCount >= 2)
                prepared.Add(cleaned);
        }

        return TerrainBuildService.CombineConstraints(Array.Empty<ConstraintPolyline>(), prepared);
    }

    private static ConstraintPolyline CleanWallConstraintPolyline(
        ConstraintPolyline constraint,
        double tolerance)
    {
        if (constraint.PointCount < 2)
            return constraint;

        double tolSq = Math.Max(Math.Abs(tolerance), double.Epsilon);
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
            ? new ConstraintPolyline(points.ToArray(), pointCount, constraint.IsClosed, constraint.PreserveInputElevation)
            : new ConstraintPolyline(Array.Empty<double>(), 0, constraint.IsClosed, constraint.PreserveInputElevation);
    }

    /// <summary>
    /// Grade mode's rails are already in the terrain: the grade conformed each one as the edge of its own
    /// batter. Inserting them a second time is what broke graded walls. The conform snaps a line onto
    /// nearby terrain corners and edges (up to <see cref="MeshAreaTopologySplitter.ConformSnapToleranceFactor"/>
    /// times the tolerance), so the rail in the mesh wanders a few millimetres off the rail as drawn, and
    /// re-inserting the drawn rail at the 1 mm wall tolerance split each of those offsets into a needle
    /// sliver. On the wall grade probe that tore every bent and ring wall it met (up to 22 boundary loops and
    /// 46 non-manifold edges), sent the wall to the whole-terrain rebuild, and next to a graded path — whose
    /// elevation constraints that rebuild must also honour — either dropped the wall or kept the tear.
    ///
    /// So each rail is traced through the graded mesh at the conform's own snap radius. When every rail
    /// traces as one edge chain, the graded mesh already is the walled terrain: its rails are pinned to the
    /// drawn elevations and the traced chains are what later stages keep. When any rail does not trace, the
    /// caller falls back to inserting them.
    /// </summary>
    private static bool TryAdoptGradedRails(
        RhinoMesh mesh,
        IReadOnlyList<ConstraintPolyline> wallConstraints,
        double tolerance,
        TerrainBuildResult build,
        out RhinoMesh adoptedMesh,
        out List<ConstraintPolyline> tracedRails)
    {
        adoptedMesh = mesh;
        tracedRails = new List<ConstraintPolyline>();
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount, out _))
            return false;

        // A little past the conform's own reach, so a point snapped right at the limit still traces.
        double traceRadius = GradingTolerances.ModelToleranceOrDefault(tolerance) *
            MeshAreaTopologySplitter.ConformSnapToleranceFactor * 1.25;
        List<ConstraintPolyline> traced = InsertedConstraintTracer.TraceAll(
            wallConstraints, vertices, vertexCount, faces, faceCount, traceRadius, out int tracedCount);
        if (tracedCount != wallConstraints.Count)
            return false;

        // A traced point carries the graded mesh's elevation. The rail's is the drawn one, taken along the
        // drawn line at the point's plan position.
        for (int c = 0; c < traced.Count; c++)
            tracedRails.Add(WithElevationsAlong(traced[c], wallConstraints[c]));

        var before = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        TerrainBuildService.ApplyPreservedConstraintElevations(vertices, tracedRails, tolerance);
        adoptedMesh = TerrainBuildService.BuildMeshFromArrays(vertices, faces);
        build.Diagnostics.Add(
            $"Retaining Wall rails were conformed by the grade itself ({tracedRails.Count} traced within {traceRadius * 1000.0:0.#} mm); " +
            $"kept without re-insertion ({before.BoundaryComponentCount} boundary loop(s)).");
        return true;
    }

    /// <summary><paramref name="traced"/>'s plan points with elevations interpolated along <paramref name="drawn"/>.</summary>
    private static ConstraintPolyline WithElevationsAlong(
        ConstraintPolyline traced,
        ConstraintPolyline drawn)
    {
        double[] points = (double[])traced.Points.Clone();
        int segments = drawn.IsClosed ? drawn.PointCount : drawn.PointCount - 1;
        for (int i = 0; i < traced.PointCount; i++)
        {
            double x = points[i * 3], y = points[i * 3 + 1];
            double best = double.MaxValue;
            for (int s = 0; s < segments; s++)
            {
                int a = s, b = (s + 1) % drawn.PointCount;
                double ax = drawn.Points[a * 3], ay = drawn.Points[a * 3 + 1];
                double dx = drawn.Points[b * 3] - ax, dy = drawn.Points[b * 3 + 1] - ay;
                double lengthSquared = (dx * dx) + (dy * dy);
                double t = lengthSquared > 0 ? Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared, 0.0, 1.0) : 0.0;
                double ox = ax + (t * dx) - x, oy = ay + (t * dy) - y;
                double distanceSquared = (ox * ox) + (oy * oy);
                if (distanceSquared < best)
                {
                    best = distanceSquared;
                    points[i * 3 + 2] = drawn.Points[a * 3 + 2] + (t * (drawn.Points[b * 3 + 2] - drawn.Points[a * 3 + 2]));
                }
            }
        }

        return new ConstraintPolyline(points, traced.PointCount, traced.IsClosed, traced.PreserveInputElevation);
    }

    private static bool TryInsertWallConstraintsIntoExistingMesh(
        RhinoMesh mesh,
        IReadOnlyList<ConstraintPolyline> wallConstraints,
        double tolerance,
        TerrainBuildResult build,
        bool useQualityPatch,
        bool reportFailures,
        bool afterCombinedRemeshFailed,
        out RhinoMesh insertedMesh)
    {
        insertedMesh = mesh;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            if (reportFailures)
                build.Diagnostics.Add(errorMessage ?? "Retaining Wall topology insertion could not extract the incoming mesh.");
            return false;
        }

        IReadOnlyList<ConstraintPolyline>? qualityConstraints = useQualityPatch
            ? TerrainBuildService.CombineConstraints(TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints), wallConstraints)
            : null;
        var messages = new List<string>();
        bool inserted = InsertWallConstraintsCore(
            vertices, vertexCount, faces, faceCount, wallConstraints, qualityConstraints, tolerance, afterCombinedRemeshFailed,
            messages, out double[] outputVertices, out int[] outputFaces);
        if (inserted || reportFailures)
            build.Diagnostics.AddRange(messages);
        if (!inserted)
            return false;

        insertedMesh = TerrainBuildService.BuildMeshFromArrays(outputVertices, outputFaces);
        return true;
    }

    /// <summary>
    /// Inserts the wall breaklines into a mesh given as arrays: the quality patch (graded walls, when
    /// <paramref name="qualityConstraints"/> is set), else face-by-face insertion, else one re-triangulation of
    /// the rails' neighbourhood. Every step is local to the rails, so this runs as well on a window of the
    /// terrain as on the whole of it. What it did, or why it declined, goes to <paramref name="messages"/>.
    /// </summary>
    internal static bool InsertWallConstraintsCore(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> wallConstraints,
        IReadOnlyList<ConstraintPolyline>? qualityConstraints,
        double tolerance,
        bool afterCombinedRemeshFailed,
        List<string> messages,
        out double[] outputVertices,
        out int[] outputFaces)
    {
        outputVertices = vertices;
        outputFaces = faces;
        string? qualityMessage = null;
        bool qualityInserted = false;
        // Breakline-only mode is an authored terrain crease, not a graded wall band. Refining a lifted
        // quality patch here surrounds the two rails with terrain-elevation Steiner points and turns the
        // intended step into a bump. Graded mode still needs that protection for its batter/wall junction.
        if (qualityConstraints != null)
        {
            qualityInserted = MeshConstraintTopologyInserter.TryInsertQualityWallPatch(
                vertices, faces, qualityConstraints, tolerance,
                out outputVertices, out outputFaces, out qualityMessage);
        }

        int outputVertexCount = outputVertices.Length / 3;
        int outputFaceCount = outputFaces.Length / 3;
        var inputBoundary = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        string? rejection = null;
        string? topologyError = null;
        bool topologyInserted = qualityInserted;
        if (!qualityInserted)
        {
            topologyInserted = MeshConstraintTopologyInserter.TryInsert(
                new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
                wallConstraints,
                tolerance,
                out IndexedTriMesh inserted,
                out topologyError);
            (outputVertices, outputVertexCount, outputFaces, outputFaceCount) = inserted;
        }

        if (!topologyInserted)
        {
            rejection = topologyError ?? "Retaining Wall topology insertion could not insert wall breaklines into the existing mesh.";
        }
        else if (!TerrainBuildService.TopologyChanged(vertices, vertexCount, faces, faceCount, outputVertices, outputVertexCount, outputFaces, outputFaceCount, tolerance))
        {
            messages.Add("Retaining Wall topology insertion found no terrain faces crossed by wall breaklines.");
            return false;
        }
        else if (!TerrainBuildService.IsTopologyInsertionBoundarySafe(
                     inputBoundary, MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount), out string boundaryMessage))
        {
            rejection = $"Retaining Wall topology insertion rejected: {boundaryMessage}";
        }

        if (rejection != null)
        {
            // Face-by-face insertion must agree with every neighbour about where a shared edge was cut, and
            // an upstream conform that left a vertex a millimetre off an edge can make two faces disagree.
            // Re-triangulating the rails' neighbourhood as one piece has no shared edge to disagree about,
            // and keeps everything outside it exactly — which is why it goes before the whole-terrain rebuild
            // rather than after it: that rebuild has to honour every persisted constraint as well, and beside
            // a graded path it either failed or tore the terrain. Both wall modes use it.
            messages.Add(rejection);
            if (!MeshConstraintTopologyInserter.TryInsertByLocalTriangulation(
                    vertices, vertexCount, faces, faceCount, wallConstraints, tolerance,
                    out outputVertices, out outputVertexCount, out outputFaces, out outputFaceCount, out string? localError) ||
                !TerrainBuildService.IsTopologyInsertionBoundarySafe(
                    inputBoundary, MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount), out _))
            {
                messages.Add($"Retaining Wall local re-triangulation declined: {localError ?? "it would change the terrain boundary"}.");
                return false;
            }

            TerrainBuildService.ApplyPreservedConstraintElevations(outputVertices, wallConstraints, tolerance);
            messages.Add("Retaining Wall inserted its breaklines by re-triangulating their neighbourhood as one piece.");
            return true;
        }

        TerrainBuildService.ApplyPreservedConstraintElevations(outputVertices, wallConstraints, tolerance);
        if (qualityInserted)
        {
            messages.Add(qualityMessage!);
            return true;
        }

        if (qualityConstraints != null)
            messages.Add($"Retaining Wall uses local breakline insertion after the quality patch declined: {qualityMessage}");
        else
            messages.Add("Retaining Wall breakline-only mode uses ordinary local breakline insertion without a terrain-elevation quality patch.");
        messages.Add(afterCombinedRemeshFailed
            ? "Retaining Wall topology fallback inserted wall breaklines into the existing mesh after combined remesh failed."
            : "Retaining Wall topology insertion inserted wall breaklines into the existing mesh.");
        return true;
    }
}
