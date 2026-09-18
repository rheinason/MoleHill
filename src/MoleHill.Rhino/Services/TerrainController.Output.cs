using System.Security.Cryptography;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.Runtime;
using Rhino.UI;


namespace MoleHill.Rhino.Services;

// Output sync + bake: generated/baked object creation, attributes, block definitions, layers, owned-object lifecycle.
internal sealed partial class TerrainController
{
    private const string EmptyBlockAttributeValue = "\u200B";

    private OutputSyncMetrics SyncOutputs(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildResult build)
    {
        int deletedObjectCount = AllOwnedIds(terrain).Distinct().Count();
        DeleteOwnedObjects(doc, terrain);

        var outputIds = new List<Guid>();
        var zoneIds = new List<Guid>();
        var auxiliaryIds = new List<Guid>();
        var markerIds = new List<Guid>();
        var blockAttributeRefreshIds = new List<Guid>();

        using var _ = new EventSuppression(this);

        if (build.PrimaryMesh != null)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Role = LayerRole.Terrain,
                // Bake the unwelded shading copy for the same reason the conduit draws one: a welded
                // wall crease shades flat ground as if it were near-vertical. See TerrainPresentationMesh.
                Geometry = TerrainPresentationMesh.CreateForDisplay(build.PrimaryMesh) ?? build.PrimaryMesh,
                Name = terrain.Name,
                LayerPath = LayerRoleService.GetTable(doc).Path(LayerRole.Terrain)
            }, blockAttributeRefreshIds);

            if (id != Guid.Empty)
                outputIds.Add(id);
        }

        foreach (var zoneObject in build.ZoneObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, zoneObject, blockAttributeRefreshIds);
            if (id != Guid.Empty)
                zoneIds.Add(id);
        }

        foreach (var auxiliary in build.AuxiliaryObjects)
        {
            if (!TerrainOutputSyncPolicy.ShouldSyncAuxiliaryOutput(auxiliary))
                continue;

            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Role = auxiliary.Role,
                Geometry = auxiliary.Geometry,
                Name = auxiliary.Name,
                Kind = auxiliary.Kind,
                AnalysisId = auxiliary.AnalysisId,
                ColorArgb = auxiliary.ColorArgb,
                LayerPath = auxiliary.LayerPath,
                SourceLayerPath = auxiliary.SourceLayerPath,
                MaterialName = auxiliary.MaterialName,
                InstanceDefinitionName = auxiliary.InstanceDefinitionName,
                MarkerBlockTemplate = auxiliary.MarkerBlockTemplate,
                InstanceUserStrings = auxiliary.InstanceUserStrings,
                InstanceTransform = auxiliary.InstanceTransform
            }, blockAttributeRefreshIds);
            if (id != Guid.Empty)
                auxiliaryIds.Add(id);
        }

        foreach (var marker in build.MarkerObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, marker, blockAttributeRefreshIds);
            if (id != Guid.Empty)
                markerIds.Add(id);
        }

        QueuePendingBlockAttributeKeyRepair(doc, blockAttributeRefreshIds);

        terrain.OutputObjectIds = outputIds;
        terrain.ZoneObjectIds = zoneIds;
        terrain.AuxiliaryObjectIds = auxiliaryIds;
        terrain.MarkerObjectIds = markerIds;
        return new OutputSyncMetrics(
            deletedObjectCount,
            outputIds.Count,
            zoneIds.Count,
            auxiliaryIds.Count,
            markerIds.Count);
    }

    private Guid AddGeneratedObject(
        RhinoDoc doc,
        TerrainDefinition terrain,
        GeneratedRhinoObject generated,
        ICollection<Guid>? blockAttributeRefreshIds = null)
    {
        var attributes = CreateAttributes(doc, terrain, generated, trackOwnership: true);
        ApplyInstanceUserStrings(attributes, generated.InstanceUserStrings);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
            {
                var definition = doc.InstanceDefinitions[definitionIndex];
                if (definition != null)
                {
                    ApplyBlockAttributeValues(attributes, definition, generated.InstanceUserStrings);
                    Guid id = doc.Objects.AddInstanceObject(definitionIndex, generated.InstanceTransform, attributes);
                    if (id != Guid.Empty && !EnsureBlockInstanceAttributeKeys(doc, id, definition, generated.InstanceUserStrings))
                        blockAttributeRefreshIds?.Add(id);

                    return id;
                }
            }
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(brep, attributes),
            Curve curve => doc.Objects.AddCurve(curve, attributes),
            Hatch hatch => doc.Objects.AddHatch(hatch, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            TextEntity textEntity => AddTextEntity(doc, textEntity, attributes, terrain),
            _ => Guid.Empty
        };
    }

    /// <summary>
    /// Binds generated text to the terrain's annotation style so size, font, and mask come from a style the
    /// user edits in Rhino rather than from values baked into the geometry. Falls back to the document's
    /// current style only when no style was captured. The entity is duplicated before stamping because
    /// generated geometry is shared with the display conduit.
    ///
    /// <para>Assigning a dimension style <em>resets the entity's justification to the style's</em>: alignment
    /// is a style field, and an entity only keeps its own by overriding it, which assigning the style clears.
    /// So the alignment the producer chose is read off before the stamp and set back after — otherwise every
    /// generated label bakes top-left however it previewed, which silently misplaces anything whose position
    /// depends on its alignment (a right-aligned figure in a report column, a centred section label).</para>
    /// </summary>
    private static Guid AddTextEntity(
        RhinoDoc doc,
        TextEntity textEntity,
        ObjectAttributes attributes,
        TerrainDefinition terrain)
    {
        if (textEntity.Duplicate() is not TextEntity toBake)
            toBake = textEntity;

        TextHorizontalAlignment horizontalAlignment = toBake.TextHorizontalAlignment;
        TextVerticalAlignment verticalAlignment = toBake.TextVerticalAlignment;

        int styleIndex = AnnotationStyleService.EnsureStyle(
            doc,
            LayerRoleService.GetTable(doc, terrain).Appearance(LayerRole.Annotation).AnnotationStyleName
                ?? terrain.LegacyAnnotationStyleName);
        if (styleIndex >= 0 && styleIndex < doc.DimStyles.Count)
        {
            toBake.DimensionStyleId = doc.DimStyles[styleIndex].Id;
        }
        else if (toBake.DimensionStyleId == Guid.Empty)
        {
            int currentStyleIndex = doc.DimStyles.CurrentIndex;
            if (currentStyleIndex >= 0 && currentStyleIndex < doc.DimStyles.Count)
                toBake.DimensionStyleId = doc.DimStyles[currentStyleIndex].Id;
        }

        toBake.TextHorizontalAlignment = horizontalAlignment;
        toBake.TextVerticalAlignment = verticalAlignment;

        return doc.Objects.AddText(toBake, attributes);
    }

    private static int EnsureBlockDefinition(RhinoDoc doc, string definitionName, MarkerBlockTemplate template)
    {
        var existingDefinition = doc.InstanceDefinitions.Find(definitionName);
        if (existingDefinition != null)
        {
            if (ShouldRefreshManagedBlockDefinition(definitionName, template))
            {
                var refreshedGeometry = GeneratedBlockCatalog.CreateBlockGeometry(template);
                var refreshedAttributes = refreshedGeometry.Select(_ => CreateBlockDefinitionAttributes()).ToList();
                doc.InstanceDefinitions.ModifyGeometry(existingDefinition.Index, refreshedGeometry, refreshedAttributes);
            }

            return existingDefinition.Index;
        }

        var geometry = GeneratedBlockCatalog.CreateBlockGeometry(template);
        if (geometry.Count == 0)
            return -1;

        var attributes = geometry.Select(_ => CreateBlockDefinitionAttributes()).ToList();
        return doc.InstanceDefinitions.Add(definitionName, string.Empty, Point3d.Origin, geometry, attributes);
    }

    private static bool ShouldRefreshManagedBlockDefinition(string definitionName, MarkerBlockTemplate template)
    {
        return string.Equals(
            definitionName,
            GeneratedBlockCatalog.GetDefaultDefinitionName(template),
            StringComparison.Ordinal);
    }

    private static ObjectAttributes CreateBlockDefinitionAttributes()
    {
        return new ObjectAttributes
        {
            ColorSource = ObjectColorSource.ColorFromParent,
            PlotColorSource = ObjectPlotColorSource.PlotColorFromParent,
            MaterialSource = ObjectMaterialSource.MaterialFromParent,
            LinetypeSource = ObjectLinetypeSource.LinetypeFromParent
        };
    }

    private ObjectAttributes CreateAttributes(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        return CreateAttributes(doc, terrain, generated, trackOwnership: true);
    }

    private ObjectAttributes CreateAttributes(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated, bool trackOwnership)
    {
        var attributes = new ObjectAttributes
        {
            Name = $"{terrain.Name} :: {generated.Name}"
        };

        if (trackOwnership)
        {
            attributes.SetUserString(OutputOwnerKey, terrain.TerrainId.ToString());
            if (!string.IsNullOrWhiteSpace(generated.MaterialName))
                attributes.SetUserString(OutputBaseMaterialNameKey, generated.MaterialName!);
            else
                attributes.DeleteUserString(OutputBaseMaterialNameKey);

            if (generated.Kind != GeneratedObjectKind.Default)
                attributes.SetUserString(OutputKindKey, generated.Kind.ToString());
            else
                attributes.DeleteUserString(OutputKindKey);

            if (generated.AnalysisId.HasValue)
                attributes.SetUserString(OutputAnalysisIdKey, generated.AnalysisId.Value.ToString());
            else
                attributes.DeleteUserString(OutputAnalysisIdKey);
        }
        else
        {
            attributes.DeleteUserString(OutputOwnerKey);
            attributes.DeleteUserString(OutputBaseMaterialNameKey);
            attributes.DeleteUserString(OutputKindKey);
            attributes.DeleteUserString(OutputAnalysisIdKey);
        }

        // Layer-sourced output stays ByLayer so print colour and print width come from the layer table
        // rather than being frozen onto each generated object.
        bool useLayerAppearance = generated.AppearanceSource == GeneratedAppearanceSource.Layer;
        if (generated.ColorArgb.HasValue && !useLayerAppearance)
        {
            attributes.ColorSource = ObjectColorSource.ColorFromObject;
            attributes.ObjectColor = GetOpaqueColor(System.Drawing.Color.FromArgb(generated.ColorArgb.Value));
            if (!trackOwnership)
                ApplyBakedColorTransparency(doc, generated.ColorArgb.Value, attributes);
        }

        // Unconditional: a generated object must never inherit Rhino's current layer. Every producer
        // names a role, and every role resolves to a path, so a blank one here means something
        // constructed an object without going through the role table.
        string layerPath = !string.IsNullOrWhiteSpace(generated.LayerPath)
            ? generated.LayerPath!
            : LayerRoleService.GetTable(doc).Path(generated.Role);
        attributes.LayerIndex = EnsureLayer(doc, layerPath, generated.SourceLayerPath);

        if (generated.PlotWeight.HasValue && !useLayerAppearance)
        {
            attributes.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
            attributes.PlotWeight = generated.PlotWeight.Value;
        }

        if (generated.DisplayOrder != 0)
            attributes.DisplayOrder = generated.DisplayOrder;

        ApplyOutputWireAttributes(terrain, attributes);
        if (trackOwnership)
            ApplyOutputRenderAttributes(doc, terrain, attributes);
        return attributes;
    }

    private static void ApplyInstanceUserStrings(ObjectAttributes attributes, IReadOnlyDictionary<string, string>? userStrings)
    {
        if (userStrings == null)
            return;

        foreach (var pair in userStrings)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            SetUserStringPreservingEmpty(attributes, pair.Key, pair.Value ?? string.Empty);
        }
    }

    private static void ApplyBlockAttributeValues(
        ObjectAttributes attributes,
        InstanceDefinition definition,
        IReadOnlyDictionary<string, string>? userStrings)
    {
        foreach (var pair in BlockAttributePayload.BuildValues(userStrings, GetBlockAttributeFieldDefinitions(definition)))
            SetBlockAttributeUserString(attributes, pair.Key, pair.Value);
    }

    private bool EnsureBlockInstanceAttributeKeys(
        RhinoDoc doc,
        Guid objectId,
        InstanceDefinition definition,
        IReadOnlyDictionary<string, string>? userStrings)
    {
        var fields = GetBlockAttributeFieldDefinitions(definition);
        var values = BlockAttributePayload.BuildValues(userStrings, fields);
        if (values.Count == 0)
            return true;

        if (doc.Objects.FindId(objectId) is not InstanceObject instanceObject)
            return true;

        var currentStrings = instanceObject.Attributes.GetUserStrings();
        if (BlockAttributePayload.FindMissingFieldKeys(currentStrings, fields).Count == 0)
            return true;

        var attributes = instanceObject.Attributes.Duplicate();
        bool changed = false;
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || attributes.GetUserString(pair.Key) != null)
                continue;

            changed |= SetBlockAttributeUserString(attributes, pair.Key, pair.Value);
        }

        if (!changed)
            return BlockAttributePayload.FindMissingFieldKeys(instanceObject.Attributes.GetUserStrings(), fields).Count == 0;

        instanceObject.Attributes = attributes;
        instanceObject.CommitChanges();
        currentStrings = instanceObject.Attributes.GetUserStrings();
        if (BlockAttributePayload.FindMissingFieldKeys(currentStrings, fields).Count == 0)
            return true;

        bool objectChanged = false;
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || instanceObject.Attributes.GetUserString(pair.Key) != null)
                continue;

            objectChanged |= SetBlockAttributeUserString(instanceObject, pair.Key, pair.Value);
        }

        if (objectChanged)
            instanceObject.CommitChanges();

        return BlockAttributePayload.FindMissingFieldKeys(instanceObject.Attributes.GetUserStrings(), fields).Count == 0;
    }

    private static IReadOnlyList<BlockAttributeFieldDefinition> GetBlockAttributeFieldDefinitions(InstanceDefinition definition)
    {
        return TextFields.GetInstanceAttributeFields(definition)
            .Select(field => new BlockAttributeFieldDefinition(field.Key, field.Prompt, field.DefaultValue))
            .ToArray();
    }

    private void EmulateAddMissingBlockAttributeKeys(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var targets = objectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
            return;

        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber)
        {
            RhinoApp.WriteLine("MoleHill: skipped AddMissingBlockAttributeKeys emulation because the document is not active.");
            return;
        }

        var previousSelection = doc.Objects
            .GetSelectedObjects(false, false)
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        using var _ = new EventSuppression(this);
        try
        {
            doc.Objects.UnselectAll();
            foreach (Guid objectId in targets)
                doc.Objects.Select(objectId, true, true);

            bool ran = RhinoApp.RunScript(AddMissingBlockAttributeKeysCommand, false);
            if ((!ran || HasMissingBlockAttributeKeys(doc, targets)) &&
                RhinoApp.RunScript(AddMissingBlockAttributeKeysAllInstancesCommand, false))
            {
                // Rhino only exposes this repair via command execution.
            }
        }
        finally
        {
            doc.Objects.UnselectAll();
            foreach (Guid objectId in previousSelection)
                doc.Objects.Select(objectId, true, true);
        }
    }

    private static bool SetUserStringPreservingEmpty(CommonObject target, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (TryInvokeInternalSetUserString(target, key, value))
            return true;

        return target switch
        {
            ObjectAttributes attributes => attributes.SetUserString(key, value),
            GeometryBase geometry => geometry.SetUserString(key, value),
            InstanceDefinition definition => definition.SetUserString(key, value),
            _ => false
        };
    }

    private static bool SetBlockAttributeUserString(CommonObject target, string key, string value)
    {
        string storedValue = string.IsNullOrEmpty(value) ? EmptyBlockAttributeValue : value;
        return SetUserStringPreservingEmpty(target, key, storedValue);
    }

    private static bool TryInvokeInternalSetUserString(CommonObject target, string key, string value)
    {
        if (InternalSetUserStringMethod == null)
            return false;

        try
        {
            object? result = InternalSetUserStringMethod.Invoke(target, new object?[] { key, value });
            return result is not bool success || success;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMissingBlockAttributeKeys(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        foreach (Guid objectId in objectIds)
        {
            if (doc.Objects.FindId(objectId) is not InstanceObject instanceObject)
                continue;

            var definition = instanceObject.InstanceDefinition;
            if (definition == null)
                continue;

            if (BlockAttributePayload.FindMissingFieldKeys(
                    instanceObject.Attributes.GetUserStrings(),
                    GetBlockAttributeFieldDefinitions(definition)).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void QueuePendingBlockAttributeKeyRepair(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var targets = objectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
            return;

        if (!_pendingBlockAttributeKeyRepairs.TryGetValue(doc.RuntimeSerialNumber, out var pendingIds))
        {
            pendingIds = new HashSet<Guid>();
            _pendingBlockAttributeKeyRepairs[doc.RuntimeSerialNumber] = pendingIds;
        }

        foreach (Guid objectId in targets)
            pendingIds.Add(objectId);
    }

    private Guid AddBakedObject(
        RhinoDoc doc,
        TerrainDefinition terrain,
        GeneratedRhinoObject generated,
        ICollection<Guid>? blockAttributeRefreshIds = null)
    {
        var attributes = CreateAttributes(doc, terrain, generated, trackOwnership: false);
        ApplyInstanceUserStrings(attributes, generated.InstanceUserStrings);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
            {
                var definition = doc.InstanceDefinitions[definitionIndex];
                if (definition != null)
                {
                    ApplyBlockAttributeValues(attributes, definition, generated.InstanceUserStrings);
                    Guid id = doc.Objects.AddInstanceObject(definitionIndex, generated.InstanceTransform, attributes);
                    if (id != Guid.Empty && !EnsureBlockInstanceAttributeKeys(doc, id, definition, generated.InstanceUserStrings))
                        blockAttributeRefreshIds?.Add(id);

                    return id;
                }
            }
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(PrepareBrepForBake(doc, brep), attributes),
            Curve curve => doc.Objects.AddCurve(curve, attributes),
            Hatch hatch => doc.Objects.AddHatch(hatch, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            TextEntity textEntity => AddTextEntity(doc, textEntity, attributes, terrain),
            _ => Guid.Empty
        };
    }

    private static Brep PrepareBrepForBake(RhinoDoc doc, Brep brep)
    {
        Brep baked = brep.DuplicateBrep();
        baked.MergeCoplanarFaces(doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians);
        return baked;
    }

    private static void ApplyBakedColorTransparency(RhinoDoc doc, int colorArgb, ObjectAttributes attributes)
    {
        var color = System.Drawing.Color.FromArgb(colorArgb);
        double transparency = 1.0 - (color.A / 255.0);
        if (transparency <= 0.0)
            return;

        var material = new Material
        {
            DiffuseColor = GetOpaqueColor(color),
            Transparency = transparency,
            Name = $"__MoleHillBakedColor__{colorArgb:X8}"
        };

        int materialIndex = doc.Materials.Find(material.Name, true);
        if (materialIndex >= 0)
            doc.Materials.Modify(material, materialIndex, quiet: true);
        else
            materialIndex = doc.Materials.Add(material);

        if (materialIndex >= 0)
        {
            attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            attributes.MaterialIndex = materialIndex;
        }
    }

    private int EnsureLayer(RhinoDoc doc, string fullPath, string? sourceLayerPath = null)
    {
        global::Rhino.DocObjects.Layer? sourceLayer = TryGetSourceLayer(doc, sourceLayerPath);
        int existingIndex = doc.Layers.FindByFullPath(fullPath, -1);
        if (existingIndex >= 0)
        {
            SyncLayerAppearance(doc, existingIndex, sourceLayer);
            return existingIndex;
        }

        return EnsureLayerPath(doc, fullPath, sourceLayer);
    }

    /// <summary>
    /// Creates a <c>::</c>-delimited layer path. Delegates to <see cref="LayerCreationService"/> so
    /// the build pipeline, applying a layer template, and interactive commands like the curve
    /// inspector's labelling all create layers by the same rule: seed appearance once at creation,
    /// then leave the layer alone.
    /// </summary>
    internal static int EnsureLayerPath(RhinoDoc doc, string fullPath, global::Rhino.DocObjects.Layer? sourceLayer = null)
    {
        return LayerCreationService.EnsureLayerPath(doc, fullPath, LayerRoleService.GetTable(doc), sourceLayer);
    }

    private static global::Rhino.DocObjects.Layer? TryGetSourceLayer(RhinoDoc doc, string? sourceLayerPath)
    {
        if (string.IsNullOrWhiteSpace(sourceLayerPath))
            return null;

        int sourceIndex = doc.Layers.FindByFullPath(sourceLayerPath, -1);
        return sourceIndex >= 0 ? doc.Layers[sourceIndex] : null;
    }

    private static void SyncLayerAppearance(RhinoDoc doc, int layerIndex, global::Rhino.DocObjects.Layer? sourceLayer)
    {
        if (sourceLayer == null || layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return;

        var existing = doc.Layers[layerIndex];
        if (existing == null ||
            existing.Color == sourceLayer.Color &&
            existing.PlotColor == sourceLayer.PlotColor)
            return;

        var updated = existing;
        updated.Color = sourceLayer.Color;
        updated.PlotColor = sourceLayer.PlotColor;
        doc.Layers.Modify(updated, layerIndex, quiet: true);
    }

    private void DeleteOwnedObjects(RhinoDoc doc, TerrainDefinition terrain)
    {
        using var _ = new EventSuppression(this);
        DeleteObjects(doc, terrain.OutputObjectIds);
        DeleteObjects(doc, terrain.ZoneObjectIds);
        DeleteObjects(doc, terrain.AuxiliaryObjectIds);
        DeleteObjects(doc, terrain.MarkerObjectIds);
        terrain.OutputObjectIds.Clear();
        terrain.ZoneObjectIds.Clear();
        terrain.AuxiliaryObjectIds.Clear();
        terrain.MarkerObjectIds.Clear();
    }

    private void RestoreTerrainObjectPlacements(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var definition in terrain.Objects)
            RestorePlacementStates(doc, definition.PlacementStates);
    }

    private void RestorePlacementStates(RhinoDoc doc, IList<TerrainObjectPlacementState> placementStates)
    {
        if (placementStates.Count == 0)
            return;

        using var _ = new EventSuppression(this);
        foreach (var state in placementStates.ToList())
        {
            Transform appliedTransform = state.GetLastAppliedTransform();
            if (IsIdentityTransform(appliedTransform))
                continue;

            if (!TryGetInverse(appliedTransform, out Transform inverse))
                continue;

            if (!TryTransformSourceObject(doc, state.ObjectId, inverse, out Guid newObjectId))
                continue;

            if (newObjectId != state.ObjectId)
                ReplaceSourceObjectReferences(doc, state.ObjectId, newObjectId);

            state.SetLastAppliedTransform(Transform.Identity);
        }
    }

    private void PurgeOrphanedOwnedObjects(RhinoDoc doc, TerrainDefinition terrain)
    {
        var protectedIds = new HashSet<Guid>(AllOwnedIds(terrain).Concat(terrain.BakedObjectIds));
        string terrainIdString = terrain.TerrainId.ToString();

        var orphanedIds = doc.Objects
            .GetObjectList(new ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                DeletedObjects = false,
                HiddenObjects = true,
                LockedObjects = true,
                NormalObjects = true,
                ReferenceObjects = false
            })
            .Where(obj =>
                obj?.Attributes?.GetUserString(OutputOwnerKey) == terrainIdString &&
                !protectedIds.Contains(obj.Id))
            .Select(obj => obj.Id)
            .ToList();

        if (orphanedIds.Count > 0)
        {
            using var _ = new EventSuppression(this);
            DeleteObjects(doc, orphanedIds);
        }
    }

    private static void DeleteObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var ids = objectIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return;

        foreach (var id in ids)
        {
            doc.Objects.Show(id, ignoreLayerMode: true);
            doc.Objects.Unlock(id, ignoreLayerMode: true);
        }

        doc.Objects.Delete(ids, quiet: true);
    }

    private void ClearOwnership(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        using var _ = new EventSuppression(this);
        foreach (var objectId in objectIds)
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj?.Attributes == null)
                continue;

            var attributes = obj.Attributes.Duplicate();
            attributes.DeleteUserString(OutputOwnerKey);
            attributes.DeleteUserString(OutputBaseMaterialNameKey);
            doc.Objects.ModifyAttributes(objectId, attributes, quiet: true);
        }
    }

}
