using System.Drawing;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.PlugIns;
using Rhino.Render;
using Rhino.Render.CustomRenderMeshes;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Publishes MoleHill terrain preview geometry to the RDK render pipeline.
///
/// MoleHill draws everything through <see cref="TerrainDisplayConduit"/>, so its geometry lives only
/// in the viewport and is invisible to render engines — historically you had to bake to render.
/// The RDK custom render mesh system exists for exactly this case: a provider may advertise
/// <see cref="NonObjectIds"/>, GUIDs that are not <c>RhinoObject</c>s in the document, and supply
/// render meshes for them. We advertise one id per terrain: <see cref="TerrainDefinition.TerrainId"/>,
/// which is already stable and persisted. Nothing is added to the document.
///
/// IMPORTANT — this is opt-in per renderer. A render engine only sees this geometry if it walks the
/// non-object id list. Rhino's own Raytraced/Cycles does; V-Ray historically does; there is no
/// evidence Enscape does (the same limitation that makes Grasshopper CustomPreview geometry
/// invisible in Enscape). For renderers that ignore the RDK subsystem there is no workaround short
/// of real document objects — i.e. Bake.
///
/// Must be public with a public parameterless constructor: RegisterProviders only discovers
/// publicly-exported derived types.
/// </summary>
public sealed class TerrainRenderMeshProvider : RenderMeshProvider
{
    private static readonly Guid ProviderIdentifier = new("6f1f2a4c-8d3b-4a17-9c52-1f5b7d0a3e88");

    public override string Name => "MoleHill Terrain";

    public override Guid ProviderId => ProviderIdentifier;

    /// <summary>
    /// Every terrain that currently has renderable preview state, across all open documents.
    ///
    /// The RDK does not pass a <see cref="RhinoDoc"/> here, so this list cannot be scoped per
    /// document. Terrain ids are persisted with the .3dm, so opening the same file twice advertises
    /// the same id twice — harmless, because <see cref="HasCustomRenderMeshes"/> and
    /// <see cref="RenderMeshes"/> both do receive the document and resolve against it.
    /// </summary>
    public override List<Guid> NonObjectIds
    {
        get
        {
            var ids = new List<Guid>();
            foreach (RhinoDoc doc in RhinoDoc.OpenDocuments())
            {
                foreach (TerrainPreviewView view in TerrainController.Instance.GetPreviewViews(doc))
                {
                    if (view.Terrain.IsVisible && !ids.Contains(view.Terrain.TerrainId))
                        ids.Add(view.Terrain.TerrainId);
                }
            }

            return ids;
        }
    }

    public override bool HasCustomRenderMeshes(
        MeshType mt,
        ViewportInfo vp,
        RhinoDoc doc,
        Guid objectId,
        ref Flags flags,
        PlugIn plugin,
        DisplayPipelineAttributes attrs)
    {
        if (mt != MeshType.Render && mt != MeshType.Any)
            return false;

        return TryResolve(doc, objectId, out TerrainDefinition? terrain, out TerrainDisplayState? displayState) &&
               displayState!.HasRenderableContent(terrain!);
    }

    public override RenderMeshes RenderMeshes(
        MeshType mt,
        ViewportInfo vp,
        RhinoDoc doc,
        Guid objectId,
        List<InstanceObject> ancestry,
        ref Flags flags,
        RenderMeshes previousPrimitives,
        PlugIn plugin,
        DisplayPipelineAttributes attrs)
    {
        if (!TryResolve(doc, objectId, out TerrainDefinition? terrain, out TerrainDisplayState? displayState))
            return previousPrimitives;

        uint hash = displayState!.RenderHash;

        // The display state is swapped atomically per build and carries a fresh stamp, so an equal
        // hash means the cached primitives are still exactly right.
        if (previousPrimitives != null && previousPrimitives.Hash == hash)
            return previousPrimitives;

        // Newer RhinoCommon (8.33+) marks this overload obsolete in favour of one taking a trailing
        // RDK flags word, but the plug-in builds against the 8.9 NuGet baseline where that overload
        // does not exist yet. Keep the 4-argument form until the baseline moves; no flag bit applies
        // to a plain non-document mesh collection anyway.
#pragma warning disable CS0612 // Type or member is obsolete
        var meshes = new RenderMeshes(doc, objectId, ProviderIdentifier, hash);
#pragma warning restore CS0612
        var definitionMeshCache = new Dictionary<string, DefinitionMeshPart[]>(StringComparer.OrdinalIgnoreCase);
        var materialCache = new Dictionary<RenderMaterialKey, RenderMaterial?>();

        AddTerrainMesh(meshes, doc, terrain!, displayState, materialCache);
        AddGeneratedObjects(meshes, doc, terrain!, displayState.ZoneObjects, definitionMeshCache, materialCache, filterAnalysis: false, requireZoneVisibility: true);
        AddGeneratedObjects(meshes, doc, terrain!, displayState.AuxiliaryObjects, definitionMeshCache, materialCache, filterAnalysis: true, requireZoneVisibility: false);
        AddGeneratedObjects(meshes, doc, terrain!, displayState.MarkerObjects, definitionMeshCache, materialCache, filterAnalysis: false, requireZoneVisibility: false);
        AddScatterObjects(meshes, doc, terrain!, displayState, definitionMeshCache, materialCache);

        return meshes;
    }

    private static void AddTerrainMesh(
        RenderMeshes meshes,
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainDisplayState displayState,
        Dictionary<RenderMaterialKey, RenderMaterial?> materialCache)
    {
        if (!terrain.ShowTerrainMesh)
            return;

        Mesh? mesh = displayState.PreviewTerrainMesh ?? displayState.TerrainMesh;
        if (mesh == null || mesh.Faces.Count == 0)
            return;

        // An analysis-coloured preview mesh carries per-vertex colours. Whether a given engine shades
        // from them is engine-dependent; we still hand over the coloured mesh so engines that do
        // (Cycles) match the viewport, and suppress the flat material so it cannot override them.
        bool hasVertexColors = mesh.VertexColors.Count > 0 && mesh.VertexColors.Count == mesh.Vertices.Count;

        AddInstance(
            meshes,
            mesh,
            Transform.Identity,
            hasVertexColors
                ? null
                : GetMaterial(
                    materialCache,
                    doc,
                    terrain,
                    TerrainDefinition.ResolveTerrainLayerPath(terrain.TerrainLayerPath),
                    sourceLayerPath: null,
                    terrain.TerrainColorArgb,
                    materialName: null));
    }

    private static void AddGeneratedObjects(
        RenderMeshes meshes,
        RhinoDoc doc,
        TerrainDefinition terrain,
        IReadOnlyList<GeneratedRhinoObject> generatedObjects,
        Dictionary<string, DefinitionMeshPart[]> definitionMeshCache,
        Dictionary<RenderMaterialKey, RenderMaterial?> materialCache,
        bool filterAnalysis,
        bool requireZoneVisibility)
    {
        if (generatedObjects.Count == 0)
            return;

        if (requireZoneVisibility && !terrain.ShowZoneMeshes)
            return;

        foreach (GeneratedRhinoObject generated in generatedObjects)
        {
            if (filterAnalysis && !TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, generated))
                continue;

            RenderMaterial? material = GetMaterial(
                materialCache,
                doc,
                terrain,
                generated.LayerPath,
                generated.SourceLayerPath,
                generated.ColorArgb,
                generated.MaterialName);

            foreach (DefinitionMeshPart part in ExtractMeshes(generated, doc, definitionMeshCache))
            {
                RenderMaterial? effectiveMaterial = !string.IsNullOrWhiteSpace(generated.MaterialName)
                    ? material
                    : part.Material ?? material;
                AddInstance(meshes, part.Mesh, generated.InstanceTransform, effectiveMaterial);
            }
        }
    }

    private static void AddScatterObjects(
        RenderMeshes meshes,
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainDisplayState displayState,
        Dictionary<string, DefinitionMeshPart[]> definitionMeshCache,
        Dictionary<RenderMaterialKey, RenderMaterial?> materialCache)
    {
        // Deliberately ignores ScatterObjectDefinition.PreviewCap/PreviewMode: those exist to keep the
        // interactive viewport responsive, not to limit a render. A render gets every instance.
        foreach (GeneratedRhinoObject scatter in displayState.ScatterObjects)
        {
            RenderMaterial? material = GetMaterial(
                materialCache,
                doc,
                terrain,
                scatter.LayerPath,
                scatter.SourceLayerPath,
                scatter.ColorArgb,
                scatter.MaterialName);

            foreach (DefinitionMeshPart part in ExtractMeshes(scatter, doc, definitionMeshCache))
            {
                RenderMaterial? effectiveMaterial = !string.IsNullOrWhiteSpace(scatter.MaterialName)
                    ? material
                    : part.Material ?? material;
                AddInstance(meshes, part.Mesh, scatter.InstanceTransform, effectiveMaterial);
            }
        }
    }

    /// <summary>
    /// Render meshes for one generated object: direct meshes, tessellated Breps (reusing the
    /// conduit per-object cache), or the geometry of a referenced block definition. Text dots, text
    /// entities and curves have no render mesh and are skipped — they are annotation, and a renderer
    /// would not draw them anyway.
    /// </summary>
    private static IEnumerable<DefinitionMeshPart> ExtractMeshes(
        GeneratedRhinoObject generated,
        RhinoDoc doc,
        Dictionary<string, DefinitionMeshPart[]> definitionMeshCache)
    {
        switch (generated.Geometry)
        {
            case Mesh mesh when mesh.Faces.Count > 0:
                yield return new DefinitionMeshPart(mesh, null);
                break;
            case Brep brep:
                foreach (Mesh brepMesh in generated.GetPreviewBrepMeshes(brep))
                    yield return new DefinitionMeshPart(brepMesh, null);
                break;
            case Extrusion extrusion:
                Mesh? extrusionMesh = extrusion.GetMesh(MeshType.Render) ??
                                      Mesh.CreateFromSurface(extrusion, MeshingParameters.QualityRenderMesh);
                if (extrusionMesh is { Faces.Count: > 0 })
                    yield return new DefinitionMeshPart(extrusionMesh, null);
                break;
        }

        if (string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
            yield break;

        foreach (DefinitionMeshPart definitionMesh in GetDefinitionMeshes(doc, generated.InstanceDefinitionName!, definitionMeshCache))
            yield return definitionMesh;
    }

    /// <summary>
    /// Tessellates a block definition once per <see cref="RenderMeshes"/> call. Scatter routinely
    /// produces thousands of instances of a handful of definitions, so this cache is what keeps the
    /// call from re-meshing the same blocks over and over.
    /// </summary>
    private static DefinitionMeshPart[] GetDefinitionMeshes(
        RhinoDoc doc,
        string definitionName,
        Dictionary<string, DefinitionMeshPart[]> cache)
    {
        if (cache.TryGetValue(definitionName, out DefinitionMeshPart[]? cached))
            return cached;

        var collected = new List<DefinitionMeshPart>();
        InstanceDefinition? definition = doc.InstanceDefinitions.Find(definitionName);
        if (definition != null)
        {
            foreach (RhinoObject member in definition.GetObjects())
            {
                if (member?.Geometry == null)
                    continue;

                RenderMaterial? memberMaterial = member.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromParent
                    ? null
                    : member.GetRenderMaterial(frontMaterial: true);

                if (member.Geometry is Mesh mesh && mesh.Faces.Count > 0)
                {
                    collected.Add(new DefinitionMeshPart(mesh, memberMaterial));
                    continue;
                }

                foreach (Mesh renderMesh in member.GetMeshes(MeshType.Render))
                {
                    if (renderMesh is { Faces.Count: > 0 })
                        collected.Add(new DefinitionMeshPart(renderMesh, memberMaterial));
                }
            }
        }

        DefinitionMeshPart[] result = collected.ToArray();
        cache[definitionName] = result;
        return result;
    }

    private static void AddInstance(RenderMeshes meshes, Mesh mesh, Transform transform, RenderMaterial? material)
    {
        var instance = new Instance
        {
            Mesh = mesh,
            Transform = transform
        };

        if (material != null)
            instance.Material = material;

        meshes.AddInstance(instance);
    }

    /// <summary>
    /// Builds a transient <see cref="RenderMaterial"/> from the same colour/transparency the conduit
    /// uses, so a render matches the preview. Deliberately does NOT go through
    /// <c>TerrainController.EnsureDisplayMaterial</c>: that adds a material to the document, and the
    /// whole point of this provider is to render without touching the document.
    /// </summary>
    private static RenderMaterial? GetMaterial(
        Dictionary<RenderMaterialKey, RenderMaterial?> cache,
        RhinoDoc doc,
        TerrainDefinition terrain,
        string? layerPath,
        string? sourceLayerPath,
        int? colorArgb,
        string? materialName)
    {
        var key = new RenderMaterialKey(layerPath, sourceLayerPath, colorArgb, materialName);
        if (cache.TryGetValue(key, out RenderMaterial? cached))
            return cached;

        RenderMaterial? material = CreateMaterial(doc, terrain, layerPath, sourceLayerPath, colorArgb, materialName);
        cache[key] = material;
        return material;
    }

    private static RenderMaterial? CreateMaterial(
        RhinoDoc doc,
        TerrainDefinition terrain,
        string? layerPath,
        string? sourceLayerPath,
        int? colorArgb,
        string? materialName)
    {
        Color color = TerrainDisplayColors.GetOpaqueColor(
            TerrainDisplayColors.Resolve(doc, layerPath, sourceLayerPath, colorArgb));
        double transparency = TerrainDisplayColors.ResolveTransparency(terrain, colorArgb);

        RenderMaterial? sourceMaterial = FindNamedRenderMaterial(doc, materialName) ??
                                         FindLayerRenderMaterial(doc, sourceLayerPath) ??
                                         FindLayerRenderMaterial(doc, layerPath);
        if (sourceMaterial != null && transparency <= 0.0)
            return sourceMaterial;

        Material material = sourceMaterial != null
            ? new Material(sourceMaterial.ToMaterial(RenderTexture.TextureGeneration.Allow))
            : new Material { DiffuseColor = color };
        material.Transparency = Math.Max(material.Transparency, transparency);

        return RenderMaterial.CreateBasicMaterial(material, doc);
    }

    private static RenderMaterial? FindNamedRenderMaterial(RhinoDoc doc, string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return null;

        foreach (RenderMaterial renderMaterial in doc.RenderMaterials)
        {
            if (string.Equals(renderMaterial.Name, materialName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(renderMaterial.DisplayName, materialName, StringComparison.OrdinalIgnoreCase))
                return renderMaterial;
        }

        int materialIndex = doc.Materials.Find(materialName, ignoreDeletedMaterials: true);
        if (materialIndex < 0 || materialIndex >= doc.Materials.Count)
            return null;

        Material material = doc.Materials[materialIndex];
        return material.RenderMaterial ?? RenderMaterial.FromMaterial(material, doc);
    }

    private static RenderMaterial? FindLayerRenderMaterial(RhinoDoc doc, string? layerPath)
    {
        if (string.IsNullOrWhiteSpace(layerPath))
            return null;

        int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
        return layerIndex >= 0 && layerIndex < doc.Layers.Count
            ? doc.Layers[layerIndex].RenderMaterial
            : null;
    }

    private readonly record struct DefinitionMeshPart(Mesh Mesh, RenderMaterial? Material);

    private readonly record struct RenderMaterialKey(
        string? LayerPath,
        string? SourceLayerPath,
        int? ColorArgb,
        string? MaterialName);

    private static bool TryResolve(
        RhinoDoc doc,
        Guid terrainId,
        out TerrainDefinition? terrain,
        out TerrainDisplayState? displayState)
    {
        foreach (TerrainPreviewView view in TerrainController.Instance.GetPreviewViews(doc))
        {
            if (view.Terrain.TerrainId != terrainId || !view.Terrain.IsVisible)
                continue;

            terrain = view.Terrain;
            displayState = view.DisplayState;
            return true;
        }

        terrain = null;
        displayState = null;
        return false;
    }
}
