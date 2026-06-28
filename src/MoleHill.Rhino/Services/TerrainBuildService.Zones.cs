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

// Zone (collage) stage: building zone meshes and resolving/ordering zone boundary curves and breps.
internal sealed partial class TerrainBuildService
{
    private static void BuildTerrainZones(TerrainBuildSnapshot snapshot, RhinoMesh mesh, TerrainDefinition terrain, TerrainBuildResult build)
    {
        if (terrain.Zones.Count == 0)
            return;

        var totalTimer = Stopwatch.StartNew();
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for terrain zones.");
            return;
        }

        var entries = new List<ZoneBoundaryEntry>();
        double tolerance = GetToleranceProfile(snapshot, terrain).InputMergeTolerance;
        var resolveTimer = Stopwatch.StartNew();
        for (int zoneIndex = 0; zoneIndex < terrain.Zones.Count; zoneIndex++)
        {
            var zone = terrain.Zones[zoneIndex];
            if (!zone.IsEnabled)
                continue;

            var zoneEntries = ResolveZoneBoundaries(snapshot, zone, zoneIndex, tolerance);
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

        entries.Sort(CompareZoneEntries);
        var boundaries = entries.Select(entry => entry.Boundary).ToArray();

        var splitTimer = Stopwatch.StartNew();
        var result = MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            boundaries,
            tolerance,
            out var splitWarning);
        splitTimer.Stop();

        if (result == null)
        {
            build.Diagnostics.Add(splitWarning ?? "Zones failed.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(splitWarning))
            build.Diagnostics.Add(splitWarning);

        var zoneOutputCounts = new Dictionary<Guid, int>();
        var outputTimer = Stopwatch.StartNew();
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
                ColorArgb = zone.UseColorOverride ? zone.ColorArgb : null,
                LayerPath = GetBakedLayerPath(entries[i].InputLayerPath),
                SourceLayerPath = entries[i].InputLayerPath,
                MaterialName = null
            });
        }
        outputTimer.Stop();
        totalTimer.Stop();

        build.RecordTiming(
            "Zones",
            totalTimer.Elapsed,
            $"{entries.Count} boundaries over {mesh.Faces.Count:N0} source faces; resolve {resolveTimer.Elapsed.TotalSeconds:0.##} s, classify {splitTimer.Elapsed.TotalSeconds:0.##} s, output {outputTimer.Elapsed.TotalSeconds:0.##} s",
            StageTimingDiagnosticThresholdMs);
    }

    private static List<ZoneBoundaryEntry> ResolveZoneBoundaries(TerrainBuildSnapshot snapshot, CollageZoneDefinition zone, int zoneOrder, double tolerance)
    {
        var result = new List<ZoneBoundaryEntry>();
        int sourceOrder = 0;

        string? inputLayerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        foreach (var obj in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, zone.Boundaries))
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
                            InputLayerPath = inputLayerPath ?? obj.LayerPath
                        });
                    }

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

    internal static string? GetBakedLayerPath(string? inputLayerPath)
    {
        if (string.IsNullOrWhiteSpace(inputLayerPath))
            return null;

        var segments = inputLayerPath
            .Split(new[] { "::" }, StringSplitOptions.None)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (segments.Length == 0)
            return "MoleHill::Zones";

        return $"MoleHill::Zones::{string.Join("::", segments)}";
    }

    private static double GetCurvePriorityZ(Curve curve)
    {
        var bbox = curve.GetBoundingBox(true);
        return (bbox.Min.Z + bbox.Max.Z) * 0.5;
    }

}
