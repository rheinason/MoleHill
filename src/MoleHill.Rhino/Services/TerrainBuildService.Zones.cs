using System.Diagnostics;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Zone (collage) stage: building zone meshes and resolving/ordering zone boundary curves and breps.
internal sealed partial class TerrainBuildService
{
    private static void BuildTerrainZones(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        TerrainDefinition terrain,
        TerrainBuildResult build,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> projectionCache,
        Func<bool>? shouldCancel)
    {
        if (terrain.Zones.Count == 0)
            return;

        var totalTimer = Stopwatch.StartNew();
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for terrain zones.");
            return;
        }

        var entries = new List<ZoneBoundaryEntry>();
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double tolerance = toleranceProfile.InputMergeTolerance;
        Dictionary<Guid, PathGrader.PathDefinition> gradePathLookup = BuildGradePathSourceLookup(
            snapshot,
            terrain,
            terrain.Modifiers.Count,
            toleranceProfile.CurveChordTolerance,
            toleranceProfile.GradePathTolerance);
        var resolveTimer = Stopwatch.StartNew();
        for (int zoneIndex = 0; zoneIndex < terrain.Zones.Count; zoneIndex++)
        {
            var zone = terrain.Zones[zoneIndex];
            if (!zone.IsEnabled)
                continue;

            var zoneEntries = ResolveZoneBoundaries(snapshot, zone, zoneIndex, tolerance, gradePathLookup);
            if (zone.Boundaries.HasReferences && zoneEntries.Count == 0)
            {
                build.Diagnostics.Add($"Zone '{zone.Name}' has no valid closed curves or horizontal planar surfaces.");
            }

            entries.AddRange(zoneEntries);
        }
        resolveTimer.Stop();

        if (entries.Count == 0)
        {
            build.Diagnostics.Add("Zones has no valid closed boundaries.");
            return;
        }

        ZonePriorityResolver.SortInPlace(entries, entry => new ZonePriorityResolver.BoundaryPriority(
            entry.ZoneOrder, entry.SourceOrder, entry.PriorityZ,
            entry.Zone.UseInputElevationForPriority));
        foreach (IGrouping<Guid, ZoneBoundaryEntry> group in entries
                     .GroupBy(entry => entry.Zone.ZoneId)
                     .OrderBy(group => group.Min(entry => entry.ZoneOrder)))
        {
            ZoneBoundaryEntry first = group.First();
            build.TerrainRegions.Add(new TerrainRegionState
            {
                RegionId = first.Zone.ZoneId,
                Name = first.Zone.Name,
                Boundaries = group.Select(CreateRegionBoundaryCurve).ToList()
            });
        }
        var boundaries = entries.Select(entry => entry.Boundary).ToArray();

        var splitTimer = Stopwatch.StartNew();
        var result = MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            // The extracted arrays describe a NORMALIZED copy of the mesh (quads split, identical
            // vertices combined, degenerate faces culled), so its counts are the only ones that match
            // them - mesh.Vertices.Count/mesh.Faces.Count belong to the un-normalized original.
            vertexCount,
            faces,
            faceCount,
            boundaries,
            tolerance,
            out var splitWarning,
            shouldCancel);
        splitTimer.Stop();

        if (result == null)
        {
            build.Diagnostics.Add(splitWarning ?? "Zones failed.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(splitWarning))
            build.Diagnostics.Add(splitWarning);

        var zoneOutputCounts = new Dictionary<Guid, int>();
        var zoneMeshes = new Dictionary<Guid, List<RhinoMesh>>();
        var outputTimer = Stopwatch.StartNew();

        // One grouping pass owns face selection for every entry. Asking each entry to find its own faces
        // rescans the whole result per boundary, which is the dominant cost once a terrain has millions
        // of faces and a scene has many zones.
        FaceOwnerGroups faceGroups = FaceOwnerGroups.Build(result.FaceAreaIndex, result.FaceCount, result.AreaCount);
        var zoneRemap = new SubMeshVertexRemap(result.VertexCount);
        for (int i = 0; i < entries.Count; i++)
        {
            ReadOnlySpan<int> entryFaces = faceGroups.Faces(i);
            if (entryFaces.Length == 0)
                continue;

            var subMesh = RhinoGeometryConversions.BuildSubMesh(result, entryFaces, zoneRemap);
            if (subMesh.Faces.Count == 0)
                continue;

            var zone = entries[i].Zone;
            zoneOutputCounts.TryGetValue(zone.ZoneId, out int currentCount);
            currentCount++;
            zoneOutputCounts[zone.ZoneId] = currentCount;

            string outputName = currentCount == 1 ? zone.Name : $"{zone.Name} {currentCount}";
            build.ZoneObjects.Add(new GeneratedRhinoObject
            {
                Role = LayerRole.Zones,
                Geometry = subMesh,
                Name = outputName,
                ColorArgb = zone.UseColorOverride ? zone.ColorArgb : null,
                LayerPath = snapshot.LayerRoles.Path(LayerRole.Zones, entries[i].InputLayerPath),
                SourceLayerPath = entries[i].InputLayerPath,
                MaterialName = null
            });
            if (!zoneMeshes.TryGetValue(zone.ZoneId, out List<RhinoMesh>? meshes))
            {
                meshes = new List<RhinoMesh>();
                zoneMeshes[zone.ZoneId] = meshes;
            }
            meshes.Add(subMesh);
        }

        // No reference set (nor a reference terrain) is the normal case, not a missing input: it estimates
        // against this terrain's own base triangulation, same as the terrain-level Earthworks analysis.
        EarthworkAnalysisDefinition? earthwork = terrain.Analyses
            .OfType<EarthworkAnalysisDefinition>()
            .FirstOrDefault(item => item.IsEnabled);
        // Zone statistics depend on each piece's own geometry, so they get a pass-local cache; the
        // reference projector is shared with the terrain-level analyses through the caller's cache.
        var comparisonCache = new Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats>();
        foreach (CollageZoneDefinition zone in terrain.Zones.Where(item => item.IsEnabled))
        {
            zoneMeshes.TryGetValue(zone.ZoneId, out List<RhinoMesh>? meshes);
            ZoneAnalysisSummary summary = ZoneAnalysisCalculator.Summarize(
                zone.ZoneId,
                meshes ?? new List<RhinoMesh>());

            if (earthwork != null && meshes is { Count: > 0 })
            {
                foreach (RhinoMesh zoneMesh in meshes)
                {
                    if (!RhinoGeometryConversions.TryExtractMeshData(zoneMesh, out double[] zoneVertices, out int[] zoneFaces, out _))
                        continue;

                    ReferenceComparisonStats stats = ComputeReferenceComparisonStats(
                        snapshot,
                        build.BaseMesh ?? mesh,
                        zoneVertices,
                        zoneFaces,
                        earthwork.Reference,
                        new SourceReferenceSet(),
                        build,
                        comparisonCache,
                        projectionCache,
                        shouldCancel,
                        earthwork.ReferenceTerrainId);
                    summary.HasEarthwork = true;
                    summary.EarthworkIsEstimated |= stats.IsEstimated;
                    summary.CutVolume += stats.CutVolume;
                    summary.FillVolume += stats.FillVolume;
                }
            }

            build.ZoneAnalysisResults.Add(summary);
        }
        outputTimer.Stop();
        totalTimer.Stop();

        build.RecordTiming(
            "Zones",
            totalTimer.Elapsed,
            $"{entries.Count} boundaries over {mesh.Faces.Count:N0} source faces; resolve {resolveTimer.Elapsed.TotalSeconds:0.##} s, classify {splitTimer.Elapsed.TotalSeconds:0.##} s, output {outputTimer.Elapsed.TotalSeconds:0.##} s",
            StageTimingDiagnosticThresholdMs);
    }

    private static List<ZoneBoundaryEntry> ResolveZoneBoundaries(
        TerrainBuildSnapshot snapshot,
        CollageZoneDefinition zone,
        int zoneOrder,
        double tolerance,
        Dictionary<Guid, PathGrader.PathDefinition> gradePathLookup)
    {
        var result = new List<ZoneBoundaryEntry>();
        int sourceOrder = 0;

        string? inputLayerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        foreach (var obj in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, zone.Boundaries))
        {
            switch (obj.Geometry)
            {
                case Curve curve:
                    MeshAreaSplitter.AreaBoundary boundary = null!;
                    bool haveBoundary = false;
                    if (obj.ObjectId != Guid.Empty &&
                        gradePathLookup.TryGetValue(obj.ObjectId, out PathGrader.PathDefinition? gradePathDefinition) &&
                        !gradePathDefinition.IsClosed)
                    {
                        haveBoundary = TryCreateGradePathZoneBoundary(gradePathDefinition, tolerance, out boundary);
                    }

                    if (!haveBoundary && !TryCreateAreaBoundary(curve, tolerance, out boundary))
                        break;

                    result.Add(new ZoneBoundaryEntry
                    {
                        Zone = zone,
                        Boundary = boundary,
                        ZoneOrder = zoneOrder,
                        SourceOrder = sourceOrder++,
                        PriorityZ = GetCurvePriorityZ(curve),
                        InputLayerPath = inputLayerPath ?? obj.LayerPath
                    });

                    break;
                case Brep brep:
                    AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, brep, tolerance, inputLayerPath ?? obj.LayerPath);
                    break;
                case Extrusion extrusion:
                    var extrusionBrep = extrusion.ToBrep();
                    if (extrusionBrep != null)
                        AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, extrusionBrep, tolerance, inputLayerPath ?? obj.LayerPath);
                    break;
            }
        }

        return result;
    }

    /// <summary>Indexes every enabled Grade Path modifier's resolved centerlines by source curve id, so a
    /// zone boundary drawn on the same curve as a path's centerline can be expanded to that path's
    /// footprint instead of requiring its own closed polygon. Zones always run after the full modifier
    /// stack, so every Grade Path modifier is eligible regardless of its position.</summary>
    private static Dictionary<Guid, PathGrader.PathDefinition> BuildGradePathSourceLookup(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        int modifierBound,
        double curveTolerance,
        double gradePathTolerance)
    {
        var lookup = new Dictionary<Guid, PathGrader.PathDefinition>();
        foreach (GradePathModifierDefinition gradePath in EnumeratePriorEnabledGradePathModifiers(terrain, modifierBound))
        {
            (ResolvedGradePathDefinition[] resolvedPaths, _) = ResolveGradePathDefinitions(
                snapshot, gradePath, curveTolerance, gradePathTolerance);
            foreach (ResolvedGradePathDefinition resolvedPath in resolvedPaths)
            {
                if (resolvedPath.SourceObjectId != Guid.Empty)
                    lookup[resolvedPath.SourceObjectId] = resolvedPath.Definition;
            }
        }

        return lookup;
    }

    /// <summary>Builds a zone boundary that tracks a Grade Path's resolved width: the variable-width
    /// left/right rails when width edges are in play, otherwise the centerline inflated by half the
    /// constant Width, squared off at both ends.</summary>
    private static bool TryCreateGradePathZoneBoundary(
        PathGrader.PathDefinition path,
        double tolerance,
        out MeshAreaSplitter.AreaBoundary boundary)
    {
        boundary = null!;

        if (path.HasVariableWidth)
        {
            int count = path.VertexCount;
            var footprint = new double[count * 4];
            for (int i = 0; i < count; i++)
            {
                footprint[i * 2] = path.LeftEdgeXy![i * 2];
                footprint[(i * 2) + 1] = path.LeftEdgeXy[(i * 2) + 1];
                int source = count - 1 - i;
                int destination = count + i;
                footprint[destination * 2] = path.RightEdgeXy![source * 2];
                footprint[(destination * 2) + 1] = path.RightEdgeXy[(source * 2) + 1];
            }

            boundary = new MeshAreaSplitter.AreaBoundary(footprint, footprint.Length / 2);
            return true;
        }

        if (!ClipperGeometry.TryInflateOpenPolylineToLoop(path.XyVertices, path.VertexCount, path.Width * 0.5, tolerance, out double[] loop))
            return false;

        boundary = new MeshAreaSplitter.AreaBoundary(loop, loop.Length / 2);
        return true;
    }

    private static bool TryCreateAreaBoundary(Curve curve, double tolerance, out MeshAreaSplitter.AreaBoundary boundary)
    {
        boundary = null!;

        if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
            return false;

        int count = polyline.Count;
        if (polyline[0].DistanceTo(polyline[^1]) < tolerance)
            count--;

        if (count < 3)
            return false;

        var xyVerts = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            xyVerts[i * 2] = polyline[i].X;
            xyVerts[i * 2 + 1] = polyline[i].Y;
        }

        boundary = new MeshAreaSplitter.AreaBoundary(xyVerts, count);
        return true;
    }

    private static void AppendPlanarBrepBoundaries(
        List<ZoneBoundaryEntry> entries,
        CollageZoneDefinition zone,
        int zoneOrder,
        ref int sourceOrder,
        Brep brep,
        double tolerance,
        string? inputLayerPath)
    {
        var candidates = new List<(Curve curve, double priorityZ)>();

        foreach (var face in brep.Faces)
        {
            if (!face.TryGetPlane(out var plane, tolerance))
                continue;

            if (Math.Abs(plane.Normal.Z) < 0.5)
                continue;

            foreach (var loop in face.Loops)
            {
                if (loop.LoopType != BrepLoopType.Outer)
                    continue;

                var loopCurve = loop.To3dCurve();
                if (loopCurve == null)
                    continue;

                candidates.Add((loopCurve, GetCurvePriorityZ(loopCurve)));
            }
        }

        if (candidates.Count == 0)
            return;

        double topZ = candidates.Max(candidate => candidate.priorityZ);
        foreach (var (curve, priorityZ) in candidates.Where(candidate => Math.Abs(candidate.priorityZ - topZ) <= tolerance))
        {
            if (!TryCreateAreaBoundary(curve, tolerance, out var boundary))
                continue;

            entries.Add(new ZoneBoundaryEntry
            {
                Zone = zone,
                Boundary = boundary,
                ZoneOrder = zoneOrder,
                SourceOrder = sourceOrder++,
                PriorityZ = priorityZ,
                InputLayerPath = inputLayerPath
            });
        }
    }

    private static double GetCurvePriorityZ(Curve curve)
    {
        var bbox = curve.GetBoundingBox(true);
        return (bbox.Min.Z + bbox.Max.Z) * 0.5;
    }

    private static Curve CreateRegionBoundaryCurve(ZoneBoundaryEntry entry)
    {
        int count = entry.Boundary.VertexCount;
        var points = new Point3d[count + 1];
        for (int index = 0; index < count; index++)
        {
            points[index] = new Point3d(
                entry.Boundary.XyVertices[index * 2],
                entry.Boundary.XyVertices[index * 2 + 1],
                entry.PriorityZ);
        }

        points[^1] = points[0];
        return new PolylineCurve(points);
    }
}
