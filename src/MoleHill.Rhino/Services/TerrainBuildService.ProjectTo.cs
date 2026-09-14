using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private static RhinoMesh ApplyProjectTo(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        ProjectToModifierDefinition modifier,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        if (!(modifier.Strength > 0.0))
            return mesh;

        if (!TryResolveProjectToTarget(snapshot, modifier, build, out RhinoMesh? target) || target == null)
            return mesh;

        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount,
                out string? inputError))
        {
            build.Diagnostics.Add(inputError ?? "Could not extract the Project To input mesh.");
            return mesh;
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(
                target, out double[] targetVertices, out int targetVertexCount, out int[] targetFaces,
                out int targetFaceCount, out string? targetError))
        {
            build.Diagnostics.Add(targetError ?? "Could not extract the Project To target mesh.");
            return mesh;
        }

        double tolerance = GetToleranceProfile(snapshot, terrain).CurveChordTolerance;
        List<double[]> loops = ResolveProjectToBoundaryLoops(snapshot, modifier, tolerance);
        if (modifier.Boundaries.HasReferences && loops.Count == 0)
        {
            build.Diagnostics.Add($"{modifier.Label}: no valid closed boundary curves were found; the modifier was not applied.");
            return mesh;
        }

        double[] conformed = SurfaceConformer.Conform(
            vertices,
            vertexCount,
            targetVertices,
            targetVertexCount,
            targetFaces,
            targetFaceCount,
            loops,
            modifier.Strength,
            modifier.FeatherDistance,
            tolerance,
            shouldCancel);

        if (conformed.Any(value => !double.IsFinite(value)))
        {
            build.Diagnostics.Add($"{modifier.Label}: projection produced invalid mesh data. Original mesh kept.");
            return mesh;
        }

        RhinoMesh result = RhinoGeometryConversions.BuildMesh(conformed, vertexCount, faces, faceCount);
        if (result.Vertices.Count == 0 || result.Faces.Count == 0 || !result.IsValid)
        {
            result.Dispose();
            build.Diagnostics.Add($"{modifier.Label}: projection produced an invalid mesh. Original mesh kept.");
            return mesh;
        }

        return result;
    }

    private static bool TryResolveProjectToTarget(
        TerrainBuildSnapshot snapshot,
        ProjectToModifierDefinition modifier,
        TerrainBuildResult build,
        out RhinoMesh? target)
    {
        target = null;
        if (modifier.TargetMesh.HasReferences)
        {
            IReadOnlyList<ResolvedSourceObject> sources =
                TerrainBuildSnapshotResolver.ResolveObjects(snapshot, modifier.TargetMesh);
            if (sources.Count != 1 || sources[0].Geometry is not RhinoMesh mesh)
            {
                build.Diagnostics.Add($"{modifier.Label}: Target Mesh must resolve to exactly one Rhino mesh.");
                return false;
            }

            target = mesh;
            return true;
        }

        if (modifier.TargetTerrainId is not { } terrainId)
        {
            build.Diagnostics.Add($"{modifier.Label}: assign one target mesh or terrain.");
            return false;
        }

        if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? reference))
        {
            build.Diagnostics.Add($"{modifier.Label}: the target terrain has no completed final mesh yet.");
            return false;
        }

        target = reference.Mesh;
        return true;
    }

    private static List<double[]> ResolveProjectToBoundaryLoops(
        TerrainBuildSnapshot snapshot,
        ProjectToModifierDefinition modifier,
        double tolerance)
    {
        var loops = new List<double[]>();
        foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out Polyline polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) <= tolerance)
                count--;
            if (count < 3)
                continue;

            var xy = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
            }
            loops.Add(xy);
        }

        return loops;
    }
}
