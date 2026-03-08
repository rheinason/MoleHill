using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using TriangleNet.Meshing;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainBuildService
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

    public TerrainBuildResult Build(RhinoDoc doc, TerrainDefinition terrain)
    {
        var build = new TerrainBuildResult();
        RhinoMesh? currentMesh = null;
        RhinoMesh? baseMesh = null;

        foreach (var modifier in terrain.Modifiers.Where(mod => mod.IsEnabled))
        {
            switch (modifier)
            {
                case TriangulateModifierDefinition triangulate:
                    currentMesh = BuildTinMesh(doc, triangulate, build);
                    if (currentMesh != null && baseMesh == null)
                        baseMesh = currentMesh.DuplicateMesh();
                    break;
                case RemeshModifierDefinition remesh:
                    currentMesh = currentMesh == null ? WarnMissingMesh(build, remesh.Label) : ApplyRemesh(doc, currentMesh, remesh, build);
                    break;
                case SmoothModifierDefinition smooth:
                    currentMesh = currentMesh == null ? WarnMissingMesh(build, smooth.Label) : ApplySmooth(doc, currentMesh, smooth, build);
                    break;
                case RetainingWallModifierDefinition retainingWall:
                    currentMesh = currentMesh == null ? WarnMissingMesh(build, retainingWall.Label) : ApplyRetainingWalls(doc, currentMesh, retainingWall, build);
                    break;
                case GradePadModifierDefinition gradePad:
                    currentMesh = currentMesh == null ? WarnMissingMesh(build, gradePad.Label) : ApplyGradePad(doc, currentMesh, gradePad, build);
                    break;
                case GradePathModifierDefinition gradePath:
                    currentMesh = currentMesh == null ? WarnMissingMesh(build, gradePath.Label) : ApplyGradePath(doc, currentMesh, gradePath, build);
                    break;
            }
        }

        build.PrimaryMesh = currentMesh;
        if (currentMesh != null)
        {
            build.Analysis = BuildAnalysis(doc, terrain, baseMesh ?? currentMesh, currentMesh);
            BuildTerrainZones(doc, currentMesh, terrain, build);
            BuildMarkers(doc, terrain, currentMesh, build);
        }

        return build;
    }

    private static RhinoMesh? WarnMissingMesh(TerrainBuildResult build, string modifierLabel)
    {
        build.Diagnostics.Add($"{modifierLabel} requires a terrain mesh generated earlier in the stack.");
        return null;
    }

    private static RhinoMesh? BuildTinMesh(RhinoDoc doc, TriangulateModifierDefinition modifier, TerrainBuildResult build)
    {
        var points = RhinoSourceResolver.ResolvePoints(doc, modifier.Points);
        var curves = RhinoSourceResolver.ResolveCurves(doc, modifier.Breaklines);

        if (points.Count == 0 && curves.Count == 0)
        {
            build.Diagnostics.Add("Triangulate has no point or breakline sources.");
            return null;
        }

        double tolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : doc.ModelAbsoluteTolerance;

        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        var polylines = new List<double[]>();
        foreach (var curve in curves)
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            var flat = new double[polyline.Count * 3];
            for (int i = 0; i < polyline.Count; i++)
            {
                flat[i * 3] = polyline[i].X;
                flat[i * 3 + 1] = polyline[i].Y;
                flat[i * 3 + 2] = polyline[i].Z;
            }

            polylines.Add(flat);
        }

        var breaklineData = BreaklineDiscretizer.Process(polylines);
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, tolerance);
        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Triangulate needs at least three unique points after deduplication.");
            return null;
        }

        var result = new TinEngine().Build(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            QualitySettings.None,
            out var warning);

        if (result == null)
        {
            build.Diagnostics.Add(warning ?? "Triangulation failed.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during triangulation.");

        return RhinoGeometryConversions.ToRhinoMesh(result);
    }

    private static RhinoMesh ApplyRemesh(RhinoDoc doc, RhinoMesh mesh, RemeshModifierDefinition modifier, TerrainBuildResult build)
    {
        var constraintCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Constraints);

        double maxArea = modifier.MaxArea;
        if (modifier.EdgeLength > 0 && maxArea <= 0)
            maxArea = modifier.EdgeLength * modifier.EdgeLength * Math.Sqrt(3.0) / 4.0;

        if (maxArea <= 0 && modifier.MinAngle <= 0)
            return mesh.DuplicateMesh();

        return RemeshWithConstraintCurves(doc, mesh, constraintCurves, maxArea, modifier.MinAngle, "Remesh", build, preserveCurveElevation: false);
    }

    private static RhinoMesh ApplySmooth(RhinoDoc doc, RhinoMesh mesh, SmoothModifierDefinition modifier, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for smoothing.");
            return mesh;
        }

        double tolerance = doc.ModelAbsoluteTolerance;
        double effectiveStrength = Math.Clamp(modifier.Strength, 0.0, 1.0) / Math.Max(1, modifier.Iterations);
        var boundaries = new List<(double[] xyVerts, int vertCount, double strength)>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyVerts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            boundaries.Add((xyVerts, count, effectiveStrength));
        }

        var breaklines = new List<(double[] xyPts, int ptCount)>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Breaklines))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            int count = polyline.Count;
            if (curve.IsClosed && polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyPts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyPts[i * 2] = polyline[i].X;
                xyPts[i * 2 + 1] = polyline[i].Y;
            }

            breaklines.Add((xyPts, count));
        }

        var smoothed = MeshSmoother.Smooth(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            boundaries.ToArray(),
            effectiveStrength,
            breaklines.ToArray(),
            Math.Clamp(modifier.BreaklineFixity, 0.0, 1.0),
            tolerance,
            Math.Max(1, modifier.Iterations));

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            smoothed[i * 3] = vertices[i * 3];
            smoothed[i * 3 + 1] = vertices[i * 3 + 1];
        }

        if (smoothed.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            build.Diagnostics.Add("Smooth produced invalid mesh data. Original mesh kept.");
            return mesh;
        }

        var smoothedMesh = RhinoGeometryConversions.BuildMesh(smoothed, mesh.Vertices.Count, faces, mesh.Faces.Count);
        if (smoothedMesh.Faces.Count == 0 || smoothedMesh.Vertices.Count == 0 || !smoothedMesh.IsValid)
        {
            build.Diagnostics.Add("Smooth produced an invalid mesh. Original mesh kept.");
            return mesh;
        }

        return smoothedMesh;
    }

    private static void BuildTerrainZones(RhinoDoc doc, RhinoMesh mesh, TerrainDefinition terrain, TerrainBuildResult build)
    {
        if (terrain.Zones.Count == 0)
            return;

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for terrain zones.");
            return;
        }

        var entries = new List<ZoneBoundaryEntry>();
        for (int zoneIndex = 0; zoneIndex < terrain.Zones.Count; zoneIndex++)
        {
            var zone = terrain.Zones[zoneIndex];
            if (!zone.IsEnabled)
                continue;

            var zoneEntries = ResolveZoneBoundaries(doc, zone, zoneIndex);
            if (zone.Boundaries.HasReferences && zoneEntries.Count == 0)
            {
                build.Diagnostics.Add($"Zone '{zone.Name}' has no valid closed curves or horizontal planar surfaces.");
            }

            entries.AddRange(zoneEntries);
        }

        if (entries.Count == 0)
        {
            build.Diagnostics.Add("Zones has no valid closed boundaries.");
            return;
        }

        entries.Sort(CompareZoneEntries);
        var boundaries = entries.Select(entry => entry.Boundary).ToArray();

        var result = MeshAreaSplitter.Split(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            boundaries,
            0,
            0,
            out var splitWarning);

        if (result == null)
        {
            build.Diagnostics.Add(splitWarning ?? "Zones failed.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(splitWarning))
            build.Diagnostics.Add(splitWarning);

        var zoneOutputCounts = new Dictionary<Guid, int>();
        for (int i = 0; i < entries.Count; i++)
        {
            var subMesh = RhinoGeometryConversions.BuildSubMesh(result, i);
            if (subMesh.Faces.Count == 0)
                continue;

            var zone = entries[i].Zone;
            zoneOutputCounts.TryGetValue(zone.ZoneId, out int currentCount);
            currentCount++;
            zoneOutputCounts[zone.ZoneId] = currentCount;

            string outputName = currentCount == 1 ? zone.Name : $"{zone.Name} {currentCount}";
            build.ZoneObjects.Add(new GeneratedRhinoObject
            {
                Geometry = subMesh,
                Name = outputName,
                ColorArgb = null,
                LayerPath = GetBakedLayerPath(entries[i].InputLayerPath),
                SourceLayerPath = entries[i].InputLayerPath,
                MaterialName = null
            });
        }
    }

    private static RhinoMesh ApplyRetainingWalls(RhinoDoc doc, RhinoMesh mesh, RetainingWallModifierDefinition modifier, TerrainBuildResult build)
    {
        double wallTolerance = Math.Max(doc.ModelAbsoluteTolerance, modifier.Tolerance);
        var wallCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.WallCurves);
        if (wallCurves.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no curve inputs.");
            return mesh;
        }

        var plan = RhinoRetainingWallPlanner.Plan(wallCurves, wallTolerance);
        foreach (var entry in plan.Report)
            build.Diagnostics.Add(entry.ToString());

        var constraintCurves = new List<Curve>(plan.Walls.Count * 2);
        foreach (var wall in plan.Walls)
        {
            if (!IsWallStripUsable(wall.Strip, doc.ModelAbsoluteTolerance, out var stripMessage))
            {
                build.Diagnostics.Add($"Retaining wall pair ({wall.CurveA}, {wall.CurveB}) skipped: {stripMessage}");
                continue;
            }

            if (wall.Brep != null)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = wall.Brep,
                    Name = $"Wall {wall.CurveA}-{wall.CurveB}",
                    LayerPath = modifier.OutputLayerPath
                });
            }

            AddWallConstraintCurves(constraintCurves, wall.Strip);
        }

        if (constraintCurves.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no usable wall pairs to insert as breaklines.");
            return mesh;
        }

        return RemeshWithConstraintCurves(doc, mesh, constraintCurves, 0.0, 0.0, "Retaining Wall", build, preserveCurveElevation: true);
    }

    private static RhinoMesh ApplyGradePad(RhinoDoc doc, RhinoMesh mesh, GradePadModifierDefinition modifier, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade pad.");
            return mesh;
        }

        double tolerance = doc.ModelAbsoluteTolerance;
        var pads = new List<PadGrader.PadBoundary>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyVerts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            var bbox = curve.GetBoundingBox(false);
            double targetZ = (bbox.Min.Z + bbox.Max.Z) * 0.5;
            pads.Add(new PadGrader.PadBoundary(xyVerts, count, targetZ, modifier.SlopeAngle, modifier.MaxDistance));
        }

        if (pads.Count == 0)
        {
            build.Diagnostics.Add("Grade Pad has no valid closed boundaries.");
            return mesh;
        }

        PadGrader.LockCurve[]? locks = null;
        var lockCurves = new List<PadGrader.LockCurve>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.LockCurves))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            var xyVerts = new double[polyline.Count * 2];
            for (int i = 0; i < polyline.Count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            lockCurves.Add(new PadGrader.LockCurve(xyVerts, polyline.Count));
        }

        if (lockCurves.Count > 0)
            locks = lockCurves.ToArray();

        var result = PadGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            pads.ToArray(),
            locks,
            modifier.MaxArea,
            modifier.MinAngle,
            out var warning);

        if (result == null)
        {
            build.Diagnostics.Add(warning ?? "Grade Pad failed.");
            return mesh;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        return RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount);
    }

    private static RhinoMesh ApplyGradePath(RhinoDoc doc, RhinoMesh mesh, GradePathModifierDefinition modifier, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade path.");
            return mesh;
        }

        if (modifier.Width <= 0)
        {
            build.Diagnostics.Add("Grade Path width must be positive.");
            return mesh;
        }

        double tolerance = doc.ModelAbsoluteTolerance;
        var paths = new List<PathGrader.PathDefinition>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Paths))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            var pathXy = new double[polyline.Count * 2];
            var pathZ = new double[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                pathXy[i * 2] = polyline[i].X;
                pathXy[i * 2 + 1] = polyline[i].Y;
                pathZ[i] = polyline[i].Z;
            }

            paths.Add(new PathGrader.PathDefinition(pathXy, pathZ, polyline.Count, modifier.Width, modifier.SlopeAngle, modifier.MaxDistance));
        }

        if (paths.Count == 0)
        {
            build.Diagnostics.Add("Grade Path has no valid paths.");
            return mesh;
        }

        var result = PathGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            paths.ToArray(),
            out var warning);

        if (result == null)
        {
            build.Diagnostics.Add(warning ?? "Grade Path failed.");
            return mesh;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        return RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount);
    }

    private static List<ZoneBoundaryEntry> ResolveZoneBoundaries(RhinoDoc doc, CollageZoneDefinition zone, int zoneOrder)
    {
        double tolerance = doc.ModelAbsoluteTolerance;
        var result = new List<ZoneBoundaryEntry>();
        int sourceOrder = 0;

        string? inputLayerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        foreach (var obj in RhinoSourceResolver.ResolveObjects(doc, zone.Boundaries))
        {
            switch (obj.Geometry)
            {
                case Curve curve:
                    if (TryCreateAreaBoundary(curve, tolerance, out var curveBoundary))
                    {
                        result.Add(new ZoneBoundaryEntry
                        {
                            Zone = zone,
                            Boundary = curveBoundary,
                            ZoneOrder = zoneOrder,
                            SourceOrder = sourceOrder++,
                            PriorityZ = GetCurvePriorityZ(curve),
                            InputLayerPath = inputLayerPath ?? GetObjectLayerPath(doc, obj)
                        });
                    }

                    break;
                case Brep brep:
                    AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, brep, tolerance, inputLayerPath ?? GetObjectLayerPath(doc, obj));
                    break;
                case Extrusion extrusion:
                    var extrusionBrep = extrusion.ToBrep();
                    if (extrusionBrep != null)
                        AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, extrusionBrep, tolerance, inputLayerPath ?? GetObjectLayerPath(doc, obj));
                    break;
            }
        }

        return result;
    }

    private static int CompareZoneEntries(ZoneBoundaryEntry left, ZoneBoundaryEntry right)
    {
        bool leftUsesZ = left.Zone.UseInputElevationForPriority;
        bool rightUsesZ = right.Zone.UseInputElevationForPriority;

        if (leftUsesZ && rightUsesZ)
        {
            int zCompare = left.PriorityZ.CompareTo(right.PriorityZ);
            if (zCompare != 0)
                return zCompare;
        }
        else
        {
            int zoneCompare = left.ZoneOrder.CompareTo(right.ZoneOrder);
            if (zoneCompare != 0)
                return zoneCompare;
        }

        int sourceCompare = left.SourceOrder.CompareTo(right.SourceOrder);
        if (sourceCompare != 0)
            return sourceCompare;

        return left.ZoneOrder.CompareTo(right.ZoneOrder);
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

    private static string? GetObjectLayerPath(RhinoDoc doc, global::Rhino.DocObjects.RhinoObject obj)
    {
        int layerIndex = obj.Attributes.LayerIndex;
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        return doc.Layers[layerIndex].FullPath;
    }

    private static string? GetBakedLayerPath(string? inputLayerPath)
    {
        if (string.IsNullOrWhiteSpace(inputLayerPath))
            return null;

        string leaf = inputLayerPath.Contains("::", StringComparison.Ordinal)
            ? inputLayerPath[(inputLayerPath.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
            : inputLayerPath;

        return $"Baked{leaf}";
    }

    private static double GetCurvePriorityZ(Curve curve)
    {
        var bbox = curve.GetBoundingBox(true);
        return (bbox.Min.Z + bbox.Max.Z) * 0.5;
    }

    private static void BuildMarkers(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, TerrainBuildResult build)
    {
        mesh.Normals.ComputeNormals();

        foreach (var marker in terrain.Markers.Where(marker => marker.IsEnabled))
        {
            var samplePoints = RhinoSourceResolver.ResolveMarkerSamplePoints(doc, marker.Sources);
            foreach (var samplePoint in samplePoints)
            {
                var meshPoint = mesh.ClosestMeshPoint(samplePoint, 0.0);
                if (meshPoint == null)
                    continue;

                Point3d worldPoint = mesh.PointAt(meshPoint);
                string text;

                switch (marker)
                {
                    case ElevationMarkerDefinition elevation:
                        text = worldPoint.Z.ToString(elevation.Format);
                        break;
                    case SlopeMarkerDefinition slope:
                        var normal = mesh.NormalAt(meshPoint);
                        double slopeRadians = Math.Atan2(Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y), Math.Abs(normal.Z));
                        double value = slope.AsPercent
                            ? Math.Tan(slopeRadians) * 100.0
                            : slopeRadians * 180.0 / Math.PI;
                        text = value.ToString(slope.Format) + (slope.AsPercent ? "%" : "deg");
                        break;
                    default:
                        continue;
                }

                if (marker.UseBlockInstance)
                {
                    build.MarkerObjects.Add(new GeneratedRhinoObject
                    {
                        Name = marker.Name,
                        InstanceDefinitionName = GetMarkerBlockName(marker),
                        MarkerBlockTemplate = GetMarkerBlockTemplate(marker),
                        InstanceTransform = Transform.Translation(worldPoint - Point3d.Origin)
                            * Transform.Scale(Point3d.Origin, Math.Max(marker.BlockScale, 0.01)),
                        ColorArgb = marker.ColorArgb
                    });
                }

                if (!marker.ShowValueLabel)
                    continue;

                build.MarkerObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = new TextDot(text, worldPoint),
                    Name = marker.Name,
                    ColorArgb = marker.ColorArgb
                });
            }
        }
    }

    private static TerrainAnalysisSummary? BuildAnalysis(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh fallbackBaseMesh, RhinoMesh currentMesh)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return null;

        var slope = SlopeAnalyzer.Analyze(
            currentVertices,
            currentMesh.Vertices.Count,
            currentFaces,
            currentMesh.Faces.Count,
            SlopeAnalyzer.SlopeUnit.Percent);

        RhinoMesh baseMesh = ResolveEarthworkReferenceMesh(doc, terrain) ?? fallbackBaseMesh;
        var boundaries = RhinoSourceResolver.ResolveCurves(doc, terrain.EarthworkBoundary);
        EstimateEarthworks(baseMesh, currentMesh, boundaries, out double cutVolume, out double fillVolume);

        return new TerrainAnalysisSummary
        {
            SurfaceArea = AreaMassProperties.Compute(currentMesh)?.Area ?? 0.0,
            SlopeMinPercent = slope.Min,
            SlopeMaxPercent = slope.Max,
            SlopeAveragePercent = slope.Average,
            CutVolume = cutVolume,
            FillVolume = fillVolume,
            NetVolume = cutVolume - fillVolume,
            EarthworkIsEstimated = !terrain.EarthworkReference.HasReferences
        };
    }

    private static void EstimateEarthworks(RhinoMesh baseMesh, RhinoMesh currentMesh, IReadOnlyList<Curve> boundaries, out double cutVolume, out double fillVolume)
    {
        cutVolume = 0.0;
        fillVolume = 0.0;

        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return;

        for (int faceIndex = 0; faceIndex < currentMesh.Faces.Count; faceIndex++)
        {
            int a = currentFaces[faceIndex * 3];
            int b = currentFaces[faceIndex * 3 + 1];
            int c = currentFaces[faceIndex * 3 + 2];

            var pa = new Point3d(currentVertices[a * 3], currentVertices[a * 3 + 1], currentVertices[a * 3 + 2]);
            var pb = new Point3d(currentVertices[b * 3], currentVertices[b * 3 + 1], currentVertices[b * 3 + 2]);
            var pc = new Point3d(currentVertices[c * 3], currentVertices[c * 3 + 1], currentVertices[c * 3 + 2]);

            var centroid = new Point3d(
                (pa.X + pb.X + pc.X) / 3.0,
                (pa.Y + pb.Y + pc.Y) / 3.0,
                (pa.Z + pb.Z + pc.Z) / 3.0);

            if (!IsInsideBoundaries(centroid, boundaries))
                continue;

            var basePoint = baseMesh.ClosestMeshPoint(centroid, 0.0);
            if (basePoint == null)
                continue;

            double baseZ = baseMesh.PointAt(basePoint).Z;
            double deltaZ = centroid.Z - baseZ;
            double projectedArea = Math.Abs(
                (pb.X - pa.X) * (pc.Y - pa.Y) -
                (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;

            double volume = projectedArea * deltaZ;
            if (volume >= 0)
                fillVolume += volume;
            else
                cutVolume += -volume;
        }
    }

    private static RhinoMesh? ResolveEarthworkReferenceMesh(RhinoDoc doc, TerrainDefinition terrain)
    {
        var meshes = RhinoSourceResolver.ResolveMeshes(doc, terrain.EarthworkReference);
        if (meshes.Count == 0)
            return null;

        if (meshes.Count == 1)
            return meshes[0];

        var combined = new RhinoMesh();
        foreach (var mesh in meshes)
            combined.Append(mesh);

        combined.Normals.ComputeNormals();
        combined.UnifyNormals();
        combined.Compact();
        return combined;
    }

    private static bool IsInsideBoundaries(Point3d point, IReadOnlyList<Curve> boundaries)
    {
        if (boundaries.Count == 0)
            return true;

        foreach (var curve in boundaries)
        {
            var containment = curve.Contains(new Point3d(point.X, point.Y, curve.PointAtStart.Z), Plane.WorldXY, RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                return true;
        }

        return false;
    }

    private static bool IsWallStripUsable(RetainingWallMeshGrader.WallStripDefinition strip, double tolerance, out string message)
    {
        message = string.Empty;
        if (strip.StationCount < 2)
        {
            message = "too few stations.";
            return false;
        }

        double minWidth = double.MaxValue;
        for (int i = 0; i < strip.StationCount; i++)
        {
            double dx = strip.TopXy[i * 2] - strip.ToeXy[i * 2];
            double dy = strip.TopXy[i * 2 + 1] - strip.ToeXy[i * 2 + 1];
            minWidth = Math.Min(minWidth, Math.Sqrt(dx * dx + dy * dy));
        }

        if (minWidth < Math.Max(tolerance * 2.0, 0.05))
        {
            message = "strip width collapses too tightly.";
            return false;
        }

        return true;
    }

    private static void AddWallConstraintCurves(List<Curve> curves, RetainingWallMeshGrader.WallStripDefinition strip)
    {
        Curve? toeCurve = CreateWallRailCurve(strip.ToeXy, strip.ToeZ, strip.StationCount);
        if (toeCurve != null)
            curves.Add(toeCurve);

        Curve? topCurve = CreateWallRailCurve(strip.TopXy, strip.TopZ, strip.StationCount);
        if (topCurve != null)
            curves.Add(topCurve);
    }

    private static Curve? CreateWallRailCurve(double[] xy, double[] z, int count)
    {
        if (count < 2)
            return null;

        var points = new Point3d[count];
        for (int i = 0; i < count; i++)
            points[i] = new Point3d(xy[i * 2], xy[i * 2 + 1], z[i]);

        return new PolylineCurve(points);
    }

    private static RhinoMesh RemeshWithConstraintCurves(
        RhinoDoc doc,
        RhinoMesh mesh,
        IReadOnlyList<Curve> constraintCurves,
        double maxArea,
        double minAngle,
        string label,
        TerrainBuildResult build,
        bool preserveCurveElevation)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var origVerts, out var origFaces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? $"Could not extract mesh data for {label.ToLowerInvariant()}.");
            return mesh;
        }

        double tolerance = doc.ModelAbsoluteTolerance;
        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segments = new List<(int a, int b)>();

        for (int i = 0; i < vertexCount; i++)
        {
            xyList.Add(origVerts[i * 3]);
            xyList.Add(origVerts[i * 3 + 1]);
            zList.Add(origVerts[i * 3 + 2]);
        }

        foreach (var curve in constraintCurves)
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            var indices = new int[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                int near = PadGrader.FindNearVertex(xyList, polyline[i].X, polyline[i].Y, 1e-6);
                if (near >= 0)
                {
                    indices[i] = near;
                    continue;
                }

                indices[i] = zList.Count;
                xyList.Add(polyline[i].X);
                xyList.Add(polyline[i].Y);
                zList.Add(preserveCurveElevation
                    ? polyline[i].Z
                    : PadGrader.InterpolateZ(origVerts, origFaces, faceCount, polyline[i].X, polyline[i].Y));
            }

            for (int i = 0; i < polyline.Count - 1; i++)
            {
                if (indices[i] != indices[i + 1])
                    segments.Add((indices[i], indices[i + 1]));
            }

            if (curve.IsClosed && polyline.Count >= 3 && indices[0] != indices[^1])
                segments.Add((indices[^1], indices[0]));
        }

        var triMesh = TriangulationHelper.Triangulate(
            xyList,
            zList.Count,
            segments,
            maxArea,
            minAngle,
            out var triWarning);

        if (triMesh == null)
        {
            build.Diagnostics.Add(triWarning ?? $"{label} triangulation failed.");
            return mesh;
        }

        if (!string.IsNullOrWhiteSpace(triWarning))
            build.Diagnostics.Add(triWarning);

        var outVertices = triMesh.Vertices.ToList();
        var outFaces = triMesh.Triangles.ToList();
        var outMesh = new RhinoMesh();
        var idToIndex = new Dictionary<int, int>(outVertices.Count);

        for (int i = 0; i < outVertices.Count; i++)
        {
            var vertex = outVertices[i];
            idToIndex[vertex.ID] = i;
            double z = vertex.ID >= 0 && vertex.ID < zList.Count
                ? zList[vertex.ID]
                : PadGrader.InterpolateZ(origVerts, origFaces, faceCount, vertex.X, vertex.Y);

            outMesh.Vertices.Add(vertex.X, vertex.Y, z);
        }

        foreach (var tri in outFaces)
        {
            outMesh.Faces.AddFace(
                idToIndex.GetValueOrDefault(tri.GetVertex(0).ID, 0),
                idToIndex.GetValueOrDefault(tri.GetVertex(1).ID, 0),
                idToIndex.GetValueOrDefault(tri.GetVertex(2).ID, 0));
        }

        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();
        return CleanTinyFaces(outMesh, tolerance, label, build);
    }

    private static RhinoMesh CleanTinyFaces(RhinoMesh mesh, double tolerance, string sourceLabel, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return mesh;

        double minEdgeLength = Math.Max(tolerance * 2.0, 0.01);
        double minProjectedArea = Math.Max(tolerance * tolerance * 2.0, 1e-5);
        var keptFaces = new List<int>(faces.Length);
        int removed = 0;

        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            int a = faces[i * 3];
            int b = faces[i * 3 + 1];
            int c = faces[i * 3 + 2];

            var pa = new Point3d(vertices[a * 3], vertices[a * 3 + 1], vertices[a * 3 + 2]);
            var pb = new Point3d(vertices[b * 3], vertices[b * 3 + 1], vertices[b * 3 + 2]);
            var pc = new Point3d(vertices[c * 3], vertices[c * 3 + 1], vertices[c * 3 + 2]);

            double l0 = pa.DistanceTo(pb);
            double l1 = pb.DistanceTo(pc);
            double l2 = pc.DistanceTo(pa);
            double area = Math.Abs((pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;

            if (Math.Min(l0, Math.Min(l1, l2)) < minEdgeLength || area < minProjectedArea)
            {
                removed++;
                continue;
            }

            keptFaces.Add(a);
            keptFaces.Add(b);
            keptFaces.Add(c);
        }

        if (removed == 0)
            return mesh;

        build.Diagnostics.Add($"{sourceLabel} removed {removed} tiny faces.");
        return BuildRemappedMesh(vertices, keptFaces);
    }

    private static RhinoMesh BuildRemappedMesh(double[] vertices, List<int> faces)
    {
        var used = faces.Distinct().ToList();
        var remap = new Dictionary<int, int>(used.Count);
        var compactVertices = new double[used.Count * 3];

        for (int i = 0; i < used.Count; i++)
        {
            remap[used[i]] = i;
            compactVertices[i * 3] = vertices[used[i] * 3];
            compactVertices[i * 3 + 1] = vertices[used[i] * 3 + 1];
            compactVertices[i * 3 + 2] = vertices[used[i] * 3 + 2];
        }

        var compactFaces = new int[faces.Count];
        for (int i = 0; i < faces.Count; i++)
            compactFaces[i] = remap[faces[i]];

        return RhinoGeometryConversions.BuildMesh(compactVertices, used.Count, compactFaces, compactFaces.Length / 3);
    }

    private static MarkerBlockTemplate GetMarkerBlockTemplate(MarkerDefinition marker)
    {
        return marker switch
        {
            ElevationMarkerDefinition => MarkerBlockTemplate.Elevation,
            SlopeMarkerDefinition => MarkerBlockTemplate.Slope,
            _ => MarkerBlockTemplate.None
        };
    }

    private static string GetMarkerBlockName(MarkerDefinition marker)
    {
        if (!string.IsNullOrWhiteSpace(marker.BlockDefinitionName))
            return marker.BlockDefinitionName!;

        return marker switch
        {
            ElevationMarkerDefinition => "MoleHill_ElevationMarker",
            SlopeMarkerDefinition => "MoleHill_SlopeMarker",
            _ => "MoleHill_Marker"
        };
    }
}
