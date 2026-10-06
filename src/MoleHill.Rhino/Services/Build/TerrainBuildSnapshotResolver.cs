using MoleHill.Rhino.Model;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class TerrainBuildSnapshotResolver
{
    public static IReadOnlyList<ResolvedSourceObject> ResolveObjects(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        if (!snapshot.SourceObjects.TryGetValue(sourceSet, out var objects) || objects.Count == 0)
            return Array.Empty<ResolvedSourceObject>();

        return objects;
    }

    public static List<Point3d> ResolvePoints(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var points = new List<Point3d>();
        foreach (var obj in ResolveObjects(snapshot, sourceSet))
        {
            switch (obj.Geometry)
            {
                case Point point:
                    points.Add(point.Location);
                    break;
                case PointCloud pointCloud:
                    for (int i = 0; i < pointCloud.Count; i++)
                        points.Add(pointCloud[i].Location);
                    break;
            }
        }

        return points;
    }

    public static List<Curve> ResolveCurves(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var curves = new List<Curve>();
        foreach (var obj in ResolveObjects(snapshot, sourceSet))
        {
            if (obj.Geometry is Curve curve)
                curves.Add(curve);
        }

        return curves;
    }

    public static List<Mesh> ResolveMeshes(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var meshes = new List<Mesh>();
        foreach (var obj in ResolveObjects(snapshot, sourceSet))
        {
            switch (obj.Geometry)
            {
                case Mesh mesh:
                    meshes.Add(mesh);
                    break;
                case Brep brep:
                    var brepMeshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                    if (brepMeshes == null)
                        break;

                    foreach (var brepMesh in brepMeshes)
                        meshes.Add(brepMesh);
                    break;
                case Extrusion extrusion:
                    var extrusionBrep = extrusion.ToBrep();
                    if (extrusionBrep == null)
                        break;

                    var extrusionMeshes = Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh);
                    if (extrusionMeshes == null)
                        break;

                    foreach (var extrusionMesh in extrusionMeshes)
                        meshes.Add(extrusionMesh);
                    break;
            }
        }

        return meshes;
    }

    public static List<Point3d> ResolveMarkerSamplePoints(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var points = new List<Point3d>();
        foreach (var obj in ResolveObjects(snapshot, sourceSet))
        {
            switch (obj.Geometry)
            {
                case Point point:
                    points.Add(point.Location);
                    break;
                case PointCloud pointCloud:
                    for (int i = 0; i < pointCloud.Count; i++)
                        points.Add(pointCloud[i].Location);
                    break;
                case Curve curve:
                    points.Add(curve.PointAt(curve.Domain.ParameterAt(0.5)));
                    break;
            }
        }

        return points;
    }

    public static ulong GetSourceSetFingerprint(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        return snapshot.SourceFingerprints.TryGetValue(sourceSet, out ulong fingerprint)
            ? fingerprint
            : 0UL;
    }
}
