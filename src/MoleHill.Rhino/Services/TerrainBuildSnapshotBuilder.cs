using System.Text.Json;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render;

namespace MoleHill.Rhino.Services;

internal static class TerrainBuildSnapshotBuilder
{
    /// <summary>Every hatch pattern name the terrain's analyses reference, so the document-thread capture
    /// can resolve them all in one pass.</summary>
    private static IEnumerable<string?> EnumerateHatchPatternNames(TerrainDefinition terrain)
    {
        foreach (AnalysisDefinition analysis in terrain.Analyses)
        {
            if (analysis is not TerrainSectionAnalysisDefinitionBase section)
                continue;

            yield return HatchPatternService.ResolvePatternName(
                section.CutHatchPatternName, HatchPatternService.DefaultCutPatternName);
            yield return HatchPatternService.ResolvePatternName(
                section.FillHatchPatternName, HatchPatternService.DefaultFillPatternName);
        }
    }

    public static TerrainBuildSnapshot Create(
        RhinoDoc doc,
        TerrainDefinition terrain,
        IEnumerable<TerrainSectionReferenceSnapshot>? sectionTerrains = null)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            throw new InvalidOperationException(ModelUnitGuard.RequiredMessage);

        TerrainDefinition terrainClone = CloneTerrain(terrain);
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrainClone,
            ModelAbsoluteTolerance = doc.ModelAbsoluteTolerance,
            ModelUnitSystem = doc.ModelUnitSystem,
            UnitContext = unitContext,
            AnnotationStyle = AnnotationStyleService.Capture(doc, terrainClone.AnnotationStyleName),
            LayerRoles = LayerRoleService.GetTable(doc),
            HatchPatterns = HatchPatternService.Capture(doc, EnumerateHatchPatternNames(terrainClone))
        };

        foreach (var sourceSet in terrainClone.EnumerateSourceSets().Distinct(ReferenceEqualityComparer<SourceReferenceSet>.Instance))
        {
            List<ResolvedSourceObject> resolvedObjects = RhinoSourceResolver.ResolveObjects(doc, sourceSet, out SourceResolutionDiagnostics diagnostics)
                .Select(obj => CreateResolvedSourceObject(doc, obj))
                .Where(entry => entry != null)
                .Cast<ResolvedSourceObject>()
                .ToList();

            snapshot.SourceObjects[sourceSet] = resolvedObjects;
            snapshot.SourceFingerprints[sourceSet] = ComputeSourceSetFingerprint(sourceSet, resolvedObjects);
            snapshot.SourceDiagnostics[sourceSet] = diagnostics;
        }

        PopulateDemSamples(doc, terrainClone, snapshot);

        PopulateBlockDefinitionBounds(doc, terrainClone, snapshot);

        if (sectionTerrains != null)
        {
            foreach (TerrainSectionReferenceSnapshot reference in sectionTerrains)
                snapshot.SectionTerrains[reference.TerrainId] = reference;
        }

        return snapshot;
    }

    private static void PopulateDemSamples(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildSnapshot snapshot)
    {
        foreach (TriangulateModifierDefinition triangulate in terrain.Modifiers.OfType<TriangulateModifierDefinition>())
        {
            if (!snapshot.SourceObjects.TryGetValue(triangulate.DemSurface, out List<ResolvedSourceObject>? surfaces) ||
                surfaces.Count == 0)
                continue;

            var points = new List<Point3d>();
            var errors = new List<string>();
            foreach (ResolvedSourceObject source in surfaces)
            {
                RhinoObject? obj = doc.Objects.FindId(source.ObjectId);
                string? texturePath = obj == null ? null : GetBitmapTexturePath(doc, obj);
                if (string.IsNullOrWhiteSpace(texturePath))
                {
                    errors.Add("A DEM surface has no bitmap texture.");
                    continue;
                }
                if (!GeoTiffElevationReader.TryReadSamples(texturePath, 20_000, out GeoTiffElevationSamples? raster, out string? readError) ||
                    raster == null)
                {
                    errors.Add(readError ?? $"Could not read DEM texture '{Path.GetFileName(texturePath)}'.");
                    continue;
                }

                double elevationScale = ResolveDemElevationScale(texturePath, triangulate, snapshot.ResolvedUnitContext);
                if (!DemSurfaceSampler.TrySample(source.Geometry, raster, elevationScale, out List<Point3d> sampled, out string? sampleError))
                {
                    errors.Add(sampleError ?? "Could not map the DEM texture through its surface.");
                    continue;
                }
                points.AddRange(sampled);
            }

            if (points.Count > 0)
            {
                snapshot.DemPoints[triangulate.Id] = points;
                var cloud = new PointCloud(points);
                snapshot.DemFingerprints[triangulate.Id] = cloud.DataCRC(0u);
            }
            if (errors.Count > 0)
                snapshot.DemDiagnostics[triangulate.Id] = string.Join(" ", errors.Distinct(StringComparer.Ordinal));
        }
    }

    private static double ResolveDemElevationScale(
        string texturePath,
        TriangulateModifierDefinition triangulate,
        ModelUnitContext documentUnits)
    {
        if (triangulate.DemElevationScale > 0.0 && double.IsFinite(triangulate.DemElevationScale))
            return triangulate.DemElevationScale;

        return GeoTiffMetadataReader.TryRead(texturePath, out _, out _, out GeoTiffLinearUnit? sourceUnits) && sourceUnits.HasValue
            ? sourceUnits.Value.MetersPerUnit / documentUnits.MetersPerModelUnit
            : 1.0;
    }

    private static string? GetBitmapTexturePath(RhinoDoc doc, RhinoObject obj)
    {
        RenderMaterial? renderMaterial = obj.GetRenderMaterial(frontMaterial: true);
        Material? material = renderMaterial?.ToMaterial(RenderTexture.TextureGeneration.Allow);
        Texture? texture = material?.GetBitmapTexture();
        string? path = texture?.FileName;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Path.IsPathRooted(path))
            return path;

        string? documentDirectory = string.IsNullOrWhiteSpace(doc.Path) ? null : Path.GetDirectoryName(doc.Path);
        return string.IsNullOrWhiteSpace(documentDirectory) ? path : Path.Combine(documentDirectory, path);
    }

    // Capture local bounds of every named block referenced by a scatter mix. The background build picks
    // blocks by name and can't reach the doc, so edge-to-edge spacing reads sizes from here.
    private static void PopulateBlockDefinitionBounds(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildSnapshot snapshot)
    {
        foreach (var scatter in terrain.Objects.OfType<ScatterObjectDefinition>())
        {
            foreach (var entry in scatter.Blocks)
            {
                string? name = entry.BlockDefinitionName;
                if (string.IsNullOrWhiteSpace(name) || snapshot.BlockDefinitionBounds.ContainsKey(name!))
                    continue;

                InstanceDefinition? definition = doc.InstanceDefinitions.Find(name!);
                if (definition != null && TryGetInstanceDefinitionBoundingBox(definition, out BoundingBox bbox))
                    snapshot.BlockDefinitionBounds[name!] = bbox;
            }
        }
    }

    private static TerrainDefinition CloneTerrain(TerrainDefinition terrain)
    {
        string json = JsonSerializer.Serialize(terrain, TerrainSerializer.SharedOptions);
        TerrainDefinition? clone = JsonSerializer.Deserialize<TerrainDefinition>(json, TerrainSerializer.SharedOptions);
        if (clone == null)
            throw new InvalidOperationException("Could not clone terrain definition for background rebuild.");

        clone.EnsureBaseModifier();
        return clone;
    }

    private static ResolvedSourceObject? CreateResolvedSourceObject(RhinoDoc doc, RhinoObject obj)
    {
        var geometry = obj.Geometry?.Duplicate();
        if (geometry == null)
            return null;

        Transform sourceTransform = Transform.Identity;
        bool hasSourceTransform = false;
        string? instanceDefinitionName = null;
        BoundingBox localBoundingBox = geometry.GetBoundingBox(true);
        if (obj is global::Rhino.DocObjects.InstanceObject instanceObject)
        {
            sourceTransform = instanceObject.InstanceXform;
            hasSourceTransform = true;
            instanceDefinitionName = instanceObject.InstanceDefinition?.Name;
            if (TryGetInstanceDefinitionBoundingBox(instanceObject.InstanceDefinition, out BoundingBox instanceDefinitionBoundingBox))
                localBoundingBox = instanceDefinitionBoundingBox;
        }

        BoundingBox worldBoundingBox;
        if (!RhinoObject.GetTightBoundingBox(new[] { obj }, out worldBoundingBox))
        {
            worldBoundingBox = hasSourceTransform
                ? geometry.GetBoundingBox(sourceTransform)
                : geometry.GetBoundingBox(true);
        }

        return new ResolvedSourceObject
        {
            ObjectId = obj.Id,
            LayerPath = GetLayerPath(doc, obj.Attributes.LayerIndex),
            InstanceDefinitionName = instanceDefinitionName,
            Geometry = geometry,
            LocalBoundingBox = localBoundingBox,
            WorldBoundingBox = worldBoundingBox,
            SourceTransform = sourceTransform,
            HasSourceTransform = hasSourceTransform,
            GeometryDataCrc = geometry.DataCRC(0u)
        };
    }

    private static bool TryGetInstanceDefinitionBoundingBox(InstanceDefinition? definition, out BoundingBox bbox)
    {
        bbox = BoundingBox.Empty;
        if (definition == null)
            return false;

        bool found = false;
        foreach (RhinoObject obj in definition.GetObjects())
        {
            if (!TryGetDefinitionObjectBoundingBox(obj, out BoundingBox objectBoundingBox))
                continue;

            if (!found)
            {
                bbox = objectBoundingBox;
                found = true;
            }
            else
            {
                bbox.Union(objectBoundingBox);
            }
        }

        return found && bbox.IsValid;
    }

    private static bool TryGetDefinitionObjectBoundingBox(RhinoObject obj, out BoundingBox bbox)
    {
        bbox = BoundingBox.Empty;
        if (obj == null || obj.IsDeleted)
            return false;

        if (obj is InstanceObject instanceObject)
        {
            if (!TryGetInstanceDefinitionBoundingBox(instanceObject.InstanceDefinition, out BoundingBox nestedBoundingBox))
                return false;

            bbox = TransformBoundingBox(nestedBoundingBox, instanceObject.InstanceXform);
            return bbox.IsValid;
        }

        GeometryBase? geometry = obj.Geometry;
        if (geometry == null)
            return false;

        bbox = geometry.GetBoundingBox(true);
        return bbox.IsValid;
    }

    private static BoundingBox TransformBoundingBox(BoundingBox bbox, Transform transform)
    {
        if (!bbox.IsValid)
            return BoundingBox.Empty;

        Point3d[] corners = bbox.GetCorners();
        for (int index = 0; index < corners.Length; index++)
            corners[index].Transform(transform);

        return new BoundingBox(corners);
    }

    private static string? GetLayerPath(RhinoDoc doc, int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        return doc.Layers[layerIndex].FullPath;
    }

    private static ulong ComputeSourceSetFingerprint(SourceReferenceSet sourceSet, IReadOnlyList<ResolvedSourceObject> objects)
    {
        var builder = new FingerprintBuilder();

        foreach (Guid objectId in sourceSet.ObjectIds.OrderBy(id => id))
            builder.Add(objectId);

        foreach (string layerPath in sourceSet.LayerPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            builder.Add(layerPath);

        builder.Add(objects.Count);
        foreach (var obj in objects.OrderBy(entry => entry.ObjectId))
        {
            builder.Add(obj.ObjectId);
            builder.Add((int)obj.Geometry.ObjectType);
            builder.Add(obj.LayerPath);
            builder.Add(obj.GeometryDataCrc);
            AddBoundingBoxFingerprint(ref builder, obj.WorldBoundingBox);
        }

        return builder.ToUInt64();
    }

    private static void AddBoundingBoxFingerprint(ref FingerprintBuilder builder, BoundingBox bbox)
    {
        builder.Add(bbox.IsValid);
        if (!bbox.IsValid)
            return;

        builder.Add(bbox.Min.X);
        builder.Add(bbox.Min.Y);
        builder.Add(bbox.Min.Z);
        builder.Add(bbox.Max.X);
        builder.Add(bbox.Max.Y);
        builder.Add(bbox.Max.Z);
    }
}
