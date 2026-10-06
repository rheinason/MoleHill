using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private static ulong ComputeAnalysisFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        ITerrainContentItem analysis,
        RhinoMesh baseMesh,
        RhinoMesh currentMesh,
        ulong baseMeshFingerprint,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("AnalysisById");
        builder.Add(analysis.Id);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(currentMesh));
        if (analysis is EarthworkAnalysisDefinition or CutFillAnalysisDefinition)
            builder.Add(baseMeshFingerprint != 0 ? baseMeshFingerprint : ComputeMeshFingerprint(baseMesh));

        AddSerializedFingerprint(ref builder, analysis, analysis.GetType());

        IEnumerable<SourceReferenceSet> sourceSets = analysis switch
        {
            AnalysisDefinition definition => definition.EnumerateSourceSets(),
            AnnotationDefinition definition => definition.EnumerateSourceSets(),
            _ => Array.Empty<SourceReferenceSet>()
        };
        foreach (var sourceSet in sourceSets)
            builder.Add(ComputeSourceSetFingerprint(snapshot, sourceSet));

        if (analysis is TerrainSectionAnnotationDefinitionBase section)
        {
            foreach (Guid terrainId in section.ComparisonTerrainIds)
            {
                builder.Add(terrainId);
                if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? reference))
                    continue;
                builder.Add(reference.MeshFingerprint);
                builder.Add(reference.Name);
                builder.Add(reference.ColorArgb);
            }
        }

        // North is the document's, not the definition's, so the serialized fingerprint above cannot see it:
        // without this, rotating north with mhSetSunNorth would leave every aspect card showing the old
        // bearings from cache.
        if (analysis is AspectAnalysisDefinition)
            builder.Add(snapshot.NorthAzimuthDegrees);

        if (analysis is ReferenceComparisonAnalysisDefinition { ReferenceTerrainId: { } referenceTerrainId })
        {
            builder.Add(referenceTerrainId);
            if (snapshot.SectionTerrains.TryGetValue(referenceTerrainId, out TerrainSectionReferenceSnapshot? referenceTerrain))
                builder.Add(referenceTerrain.MeshFingerprint);
        }

        // Routing and appearance both come from the layer template, so an edit to it has to
        // invalidate cached output the same way an edit to the analysis does.
        if (TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis))
            builder.Add(snapshot.LayerRoles.Fingerprint);

        return builder.ToUInt64();
    }

    private static ulong ComputeZonesFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RhinoMesh baseMesh,
        IReadOnlyList<ConstraintPolyline> persistentHardConstraints,
        ulong currentMeshFingerprint,
        ulong baseMeshFingerprint)
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
            builder.Add(ComputeZoneGradePathFingerprint(snapshot, terrain, zone));
        }

        // Zone quantities fold in the enabled Earthworks analysis (see BuildTerrainZones), so
        // enabling it or repointing its reference has to invalidate the cached zone summaries. No
        // reference set is the normal case too — it estimates against the base mesh, already folded in
        // below — so this only needs the analysis to be enabled, not to have an explicit reference.
        EarthworkAnalysisDefinition? earthwork = terrain.Analyses
            .OfType<EarthworkAnalysisDefinition>()
            .FirstOrDefault(item => item.IsEnabled);
        if (earthwork != null)
        {
            builder.Add("ZoneEarthworks");
            builder.Add(earthwork.Id);
            builder.Add(ComputeSourceSetFingerprint(snapshot, earthwork.Reference));
            builder.Add(baseMeshFingerprint != 0 ? baseMeshFingerprint : ComputeMeshFingerprint(baseMesh));
            if (earthwork.ReferenceTerrainId is { } referenceTerrainId)
            {
                builder.Add(referenceTerrainId);
                if (snapshot.SectionTerrains.TryGetValue(referenceTerrainId, out TerrainSectionReferenceSnapshot? referenceTerrain))
                    builder.Add(referenceTerrain.MeshFingerprint);
            }
        }

        return builder.ToUInt64();
    }

    /// <summary>A zone boundary curve that is also a Grade Path centerline gets its footprint from the
    /// path (see <c>TerrainBuildService.Zones.BuildGradePathSourceLookup</c>), so the zone's cached mesh
    /// must invalidate when the matched path's width settings change - not just when the shared curve
    /// object itself moves.</summary>
    private static ulong ComputeZoneGradePathFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        CollageZoneDefinition zone)
    {
        HashSet<Guid> selectedSourceIds = ResolveSourceObjectIds(snapshot, zone.Boundaries);
        if (selectedSourceIds.Count == 0)
            return 0UL;

        var builder = new FingerprintBuilder();
        builder.Add("ZoneGradePathV1");
        int matchedModifierCount = 0;
        foreach ((int _, GradePathModifierDefinition gradePath) in EnumeratePriorEnabledGradePathModifiersWithIndex(terrain, terrain.Modifiers.Count))
        {
            var matchingIds = TerrainBuildSnapshotResolver
                .ResolveObjects(snapshot, gradePath.Paths)
                .Select(static sourceObject => sourceObject.ObjectId)
                .Where(objectId => objectId != Guid.Empty && selectedSourceIds.Contains(objectId))
                .Distinct()
                .OrderBy(static objectId => objectId)
                .ToArray();
            if (matchingIds.Length == 0)
                continue;

            matchedModifierCount++;
            // The path's identity, not its position: a card inserted above it must not invalidate zones.
            builder.Add(gradePath.Id);
            builder.Add(gradePath.UseVariableWidth);
            builder.Add(gradePath.Width);
            builder.Add(gradePath.SlopeAngle);
            builder.Add(gradePath.CutSlopeAngle);
            builder.Add(gradePath.MaxDistance);
            builder.Add(gradePath.MaxEdgeDistance);
            builder.Add(ComputeSourceSetFingerprint(snapshot, gradePath.Paths));
            builder.Add(ComputeSourceSetFingerprint(snapshot, gradePath.WidthEdges));
            builder.Add(matchingIds.Length);
            foreach (Guid objectId in matchingIds)
                builder.Add(objectId);
        }

        return matchedModifierCount == 0 ? 0UL : builder.ToUInt64();
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

    private static ulong ComputeObjectsFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Objects");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));

        foreach (var definition in terrain.Objects.Where(item => item.IsEnabled && item is not ScatterObjectDefinition))
        {
            AddSerializedFingerprint(ref builder, definition, definition.GetType());
            foreach (var sourceSet in definition.EnumerateSourceSets())
                builder.Add(ComputeSourceSetFingerprint(snapshot, sourceSet));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeScatterFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Scatter");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));

        foreach (var definition in terrain.Objects.OfType<ScatterObjectDefinition>().Where(item => item.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, definition, definition.GetType());
            foreach (var sourceSet in definition.EnumerateSourceSets())
                builder.Add(ComputeSourceSetFingerprint(snapshot, sourceSet));
        }

        foreach (var entry in snapshot.BlockDefinitionBounds.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            builder.Add(entry.Key);
            builder.Add(entry.Value.Min.X);
            builder.Add(entry.Value.Min.Y);
            builder.Add(entry.Value.Min.Z);
            builder.Add(entry.Value.Max.X);
            builder.Add(entry.Value.Max.Y);
            builder.Add(entry.Value.Max.Z);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeSourceSetFingerprint(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        return TerrainBuildSnapshotResolver.GetSourceSetFingerprint(snapshot, sourceSet);
    }

    private static ulong ComputeMeshStageOutputFingerprint(
        RhinoMesh? mesh,
        IReadOnlyList<ConstraintPolyline> persistentHardConstraints)
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
        if (mesh == null)
            return 0UL;

        ExtractedMeshData data = RhinoGeometryConversions.GetNormalizedMeshData(mesh);
        var builder = new FingerprintBuilder();
        builder.Add("RhinoMeshV2");
        builder.Add(data.VertexCount);
        builder.AddBytes(MemoryMarshal.AsBytes(data.Vertices.AsSpan()));
        builder.Add(data.FaceCount);
        builder.AddBytes(MemoryMarshal.AsBytes(data.Faces.AsSpan()));

        return builder.ToUInt64();
    }

    internal static ulong ComputeMeshFingerprintForDiagnostics(RhinoMesh? mesh) => ComputeMeshFingerprint(mesh);

    private static ulong ComputeConstraintsFingerprint(IReadOnlyList<ConstraintPolyline> constraints)
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
        builder.AddBytes(JsonSerializer.SerializeToUtf8Bytes(value, type, FingerprintJsonOptions));
    }

    /// <summary>The serializer contract for fingerprints: the saved form minus <see cref="NotBuildInputAttribute"/> properties.</summary>
    internal static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { RemoveNotBuildInputProperties }
        }
    };

    private static void RemoveNotBuildInputProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].AttributeProvider?.IsDefined(typeof(NotBuildInputAttribute), inherit: true) == true)
                typeInfo.Properties.RemoveAt(i);
        }
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
