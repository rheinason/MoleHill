using System.Text.Json;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private static ulong ComputeAnalysisFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh baseMesh,
        RhinoMesh currentMesh,
        ulong baseMeshFingerprint,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Analysis");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(baseMeshFingerprint != 0 ? baseMeshFingerprint : ComputeMeshFingerprint(baseMesh));
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(currentMesh));
        foreach (var analysis in terrain.Analyses)
        {
            AddSerializedFingerprint(ref builder, analysis, analysis.GetType());
            foreach (var sourceSet in analysis.EnumerateSourceSets())
                builder.Add(ComputeSourceSetFingerprint(snapshot, sourceSet));
        }
        return builder.ToUInt64();
    }

    private static ulong ComputeZonesFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Zones");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(GetToleranceProfile(snapshot, terrain).InputMergeTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));

        foreach (var zone in terrain.Zones.Where(zone => zone.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, zone, zone.GetType());
            builder.Add(ComputeSourceSetFingerprint(snapshot, zone.Boundaries));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeMarkersFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Markers");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));

        foreach (var marker in terrain.Markers.Where(marker => marker.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, marker, marker.GetType());
            builder.Add(ComputeSourceSetFingerprint(snapshot, marker.Sources));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeSourceSetFingerprint(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        return TerrainBuildSnapshotResolver.GetSourceSetFingerprint(snapshot, sourceSet);
    }

    private static ulong ComputeMeshStageOutputFingerprint(
        RhinoMesh? mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints)
    {
        var builder = new FingerprintBuilder();
        builder.Add(ComputeMeshFingerprint(mesh));
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));
        return builder.ToUInt64();
    }

    private static ulong ComputeGradingTopologyOutputFingerprint(
        string graderKind,
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        int faceCount)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradingTopologyOutput");
        builder.Add(graderKind);
        builder.Add(vertexCount);
        AddDoubleArrayFingerprint(ref builder, vertices);
        builder.Add(faceCount);
        AddIntArrayFingerprint(ref builder, faces);
        return builder.ToUInt64();
    }

    private static ulong ComputeMeshFingerprint(RhinoMesh? mesh)
    {
        return mesh == null ? 0UL : mesh.DataCRC(0u);
    }

    private static ulong ComputeConstraintsFingerprint(IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        var builder = new FingerprintBuilder();
        builder.Add(constraints.Count);
        foreach (var constraint in constraints)
        {
            builder.Add(constraint.PointCount);
            builder.Add(constraint.IsClosed);
            builder.Add(constraint.PreserveInputElevation);
            AddDoubleArrayFingerprint(ref builder, constraint.Points);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeBoundaryPolylinesFingerprint(IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> boundaries)
    {
        var builder = new FingerprintBuilder();
        builder.Add(boundaries.Count);
        foreach (var boundary in boundaries)
        {
            builder.Add(boundary.PointCount);
            builder.Add(boundary.IsClosed);
            AddDoubleArrayFingerprint(ref builder, boundary.Points);
        }

        return builder.ToUInt64();
    }

    private static void AddSerializedFingerprint(ref FingerprintBuilder builder, object value, Type type)
    {
        builder.AddBytes(JsonSerializer.SerializeToUtf8Bytes(value, type));
    }

    private static void AddDoubleArrayFingerprint(ref FingerprintBuilder builder, IReadOnlyList<double> values)
    {
        builder.Add(values.Count);
        for (int i = 0; i < values.Count; i++)
            builder.Add(values[i]);
    }

    private static void AddIntArrayFingerprint(ref FingerprintBuilder builder, IReadOnlyList<int> values)
    {
        builder.Add(values.Count);
        for (int i = 0; i < values.Count; i++)
            builder.Add(values[i]);
    }
}
