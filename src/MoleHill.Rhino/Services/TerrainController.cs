using System.Text.Json;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainController
{
    private const string OutputOwnerKey = "MoleHillTerrainId";
    private readonly TerrainDocumentStore _documentStore = new();
    private readonly TerrainBuildService _buildService = new();
    private readonly Dictionary<uint, DocumentState> _states = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId), DateTime> _pendingRebuilds = new();
    private bool _initialized;
    private int _suppressDocEvents;

    public static TerrainController Instance { get; } = new();

    public event EventHandler? StateChanged;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        RhinoDoc.AddRhinoObject += OnAddRhinoObject;
        RhinoDoc.DeleteRhinoObject += OnDeleteRhinoObject;
        RhinoDoc.ReplaceRhinoObject += OnReplaceRhinoObject;
        RhinoDoc.UndeleteRhinoObject += OnUndeleteRhinoObject;
        RhinoDoc.ModifyObjectAttributes += OnModifyObjectAttributes;
        RhinoDoc.LayerTableEvent += OnLayerTableEvent;
        RhinoApp.Idle += OnIdle;
    }

    public IReadOnlyList<TerrainDefinition> GetTerrains(RhinoDoc doc) => GetState(doc).Terrains;

    public void ReloadDocumentState(RhinoDoc doc)
    {
        _states.Remove(doc.RuntimeSerialNumber);
        RaiseStateChanged();
    }

    public Guid? GetSelectedTerrainId(RhinoDoc doc) => GetState(doc).SelectedTerrainId;

    public void SetSelectedTerrain(RhinoDoc doc, Guid? terrainId)
    {
        var state = GetState(doc);
        state.SelectedTerrainId = terrainId;
        RaiseStateChanged();
    }

    public TerrainDefinition? GetSelectedTerrain(RhinoDoc doc)
    {
        var state = GetState(doc);
        Guid? selectedId = state.SelectedTerrainId ?? state.Terrains.FirstOrDefault()?.TerrainId;
        return selectedId == null ? null : state.Terrains.FirstOrDefault(terrain => terrain.TerrainId == selectedId.Value);
    }

    public TerrainDefinition CreateTerrain(RhinoDoc doc, bool seedFromSelection)
    {
        var state = GetState(doc);
        var terrain = new TerrainDefinition
        {
            Name = NextTerrainName(state.Terrains),
            TerrainLayerPath = doc.Layers.CurrentLayer?.FullPath,
            AuxiliaryLayerPath = "MoleHill::Auxiliary"
        };
        terrain.EnsureBaseModifier();

        if (seedFromSelection && terrain.Modifiers[0] is TriangulateModifierDefinition triangulate)
        {
            triangulate.Points.ReplaceObjects(GetSelectedPointObjectIds(doc));
            triangulate.Breaklines.ReplaceObjects(GetSelectedCurveObjectIds(doc));
        }

        state.Terrains.Add(terrain);
        state.SelectedTerrainId = terrain.TerrainId;
        Save(doc, state);

        if (terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();

        return terrain;
    }

    public void DeleteTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        DeleteOwnedObjects(doc, terrain);
        state.Terrains.Remove(terrain);
        if (state.SelectedTerrainId == terrainId)
            state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;

        Save(doc, state);
    }

    public void ConvertToRhino(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        uint undoRecord = doc.BeginUndoRecord("Detach MoleHill Terrain");
        doc.AddCustomUndoEvent("Detach MoleHill Terrain", OnRestoreStateUndo, CaptureUndoState(state));

        ClearOwnership(doc, terrain.OutputObjectIds);
        ClearOwnership(doc, terrain.ZoneObjectIds);
        ClearOwnership(doc, terrain.AuxiliaryObjectIds);
        ClearOwnership(doc, terrain.MarkerObjectIds);
        state.Terrains.Remove(terrain);
        if (state.SelectedTerrainId == terrainId)
            state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;

        Save(doc, state);
        if (undoRecord > 0)
            doc.EndUndoRecord(undoRecord);
    }

    public void AddModifier(RhinoDoc doc, Guid terrainId, string modifierKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = CreateModifier(modifierKind);
            if (modifier != null)
                terrain.Modifiers.Add(modifier);
        });
    }

    public void RemoveModifier(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = terrain.Modifiers.FirstOrDefault(item => item.Id == modifierId);
            if (modifier == null)
                return;

            terrain.Modifiers.Remove(modifier);
            terrain.EnsureBaseModifier();
        });
    }

    public void MoveModifier(RhinoDoc doc, Guid terrainId, Guid modifierId, int direction)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Modifiers.FindIndex(item => item.Id == modifierId);
            if (index < 0)
                return;

            int targetIndex = Math.Clamp(index + direction, 0, terrain.Modifiers.Count - 1);
            if (targetIndex == index)
                return;

            var modifier = terrain.Modifiers[index];
            terrain.Modifiers.RemoveAt(index);
            terrain.Modifiers.Insert(targetIndex, modifier);
        });
    }

    public void DuplicateModifier(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Modifiers.FindIndex(item => item.Id == modifierId);
            if (index < 0)
                return;

            var clone = CloneModifier(terrain.Modifiers[index]);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Label += " Copy";
            terrain.Modifiers.Insert(index + 1, clone);
        });
    }

    public void AddMarker(RhinoDoc doc, Guid terrainId, string markerKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            MarkerDefinition marker = markerKind switch
            {
                "elevation" => new ElevationMarkerDefinition(),
                "slope" => new SlopeMarkerDefinition(),
                _ => throw new InvalidOperationException($"Unknown marker kind '{markerKind}'.")
            };

            terrain.Markers.Add(marker);
        });
    }

    public void RemoveMarker(RhinoDoc doc, Guid terrainId, Guid markerId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Markers.RemoveAll(marker => marker.Id == markerId);
        });
    }

    public void MutateTerrain(RhinoDoc doc, Guid terrainId, Action<TerrainDefinition> mutator, bool scheduleRebuild = true)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        mutator(terrain);
        terrain.EnsureBaseModifier();
        Save(doc, state);

        if (scheduleRebuild && terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();
    }

    public void RebuildTerrain(RhinoDoc doc, Guid terrainId)
    {
        _pendingRebuilds.Remove((doc.RuntimeSerialNumber, terrainId));

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        RebuildTerrain(doc, state, terrain);
    }

    public TerrainDefinition DuplicateTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return null!;

        var json = System.Text.Json.JsonSerializer.Serialize(terrain);
        var clone = System.Text.Json.JsonSerializer.Deserialize<TerrainDefinition>(json)!;
        clone.TerrainId = Guid.NewGuid();
        clone.Name = terrain.Name + " Copy";
        clone.OutputObjectIds.Clear();
        clone.ZoneObjectIds.Clear();
        clone.AuxiliaryObjectIds.Clear();
        clone.MarkerObjectIds.Clear();
        clone.LastBuildMessage = null;
        clone.LastBuildUtc = null;

        foreach (var modifier in clone.Modifiers)
            modifier.Id = Guid.NewGuid();
        foreach (var marker in clone.Markers)
            marker.Id = Guid.NewGuid();
        foreach (var zone in clone.Zones)
            zone.ZoneId = Guid.NewGuid();

        state.Terrains.Add(clone);
        state.SelectedTerrainId = clone.TerrainId;
        Save(doc, state);

        if (clone.LiveUpdateEnabled)
            ScheduleRebuild(doc, clone.TerrainId);

        return clone;
    }

    public void BakeTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        using var _ = new EventSuppression(this);
        foreach (var objectId in terrain.OutputObjectIds.Concat(terrain.ZoneObjectIds).Concat(terrain.AuxiliaryObjectIds))
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj?.Geometry == null)
                continue;

            var attributes = obj.Attributes.Duplicate();
            attributes.DeleteUserString(OutputOwnerKey);

            switch (obj.Geometry)
            {
                case Mesh mesh:
                    doc.Objects.AddMesh(mesh, attributes);
                    break;
                case Brep brep:
                    doc.Objects.AddBrep(brep, attributes);
                    break;
            }
        }

        doc.Views.Redraw();
    }

    public void SetTerrainVisible(RhinoDoc doc, Guid terrainId, bool visible)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        terrain.IsVisible = visible;

        using var _ = new EventSuppression(this);
        foreach (var id in AllOwnedIds(terrain))
        {
            if (visible)
                doc.Objects.Show(id, ignoreLayerMode: true);
            else
                doc.Objects.Hide(id, ignoreLayerMode: true);
        }

        Save(doc, state);
        doc.Views.Redraw();
    }

    public void SetTerrainLocked(RhinoDoc doc, Guid terrainId, bool locked)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        terrain.IsLocked = locked;

        using var _ = new EventSuppression(this);
        foreach (var id in AllOwnedIds(terrain))
        {
            if (locked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
            else
                doc.Objects.Unlock(id, ignoreLayerMode: true);
        }

        Save(doc, state);
        doc.Views.Redraw();
    }

    public void RefreshTerrainDisplay(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        using var _ = new EventSuppression(this);
        foreach (var id in AllOwnedIds(terrain))
        {
            doc.Objects.Show(id, ignoreLayerMode: true);
            doc.Objects.Unlock(id, ignoreLayerMode: true);
        }

        ApplyVisibilityAndLock(doc, terrain);
        Save(doc, state);
        doc.Views.Redraw();
    }

    public IReadOnlyList<Guid> GetSelectedPointObjectIds(RhinoDoc doc)
    {
        return doc.Objects
            .GetSelectedObjects(false, false)
            .Where(obj => obj.Geometry is Point || obj.Geometry is PointCloud)
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<Guid> GetSelectedCurveObjectIds(RhinoDoc doc)
    {
        return doc.Objects
            .GetSelectedObjects(false, false)
            .Where(obj => obj.Geometry is Curve)
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<Guid> GetSelectedZoneObjectIds(RhinoDoc doc)
    {
        return doc.Objects
            .GetSelectedObjects(false, false)
            .Where(obj => obj.Geometry is Curve || obj.Geometry is Brep || obj.Geometry is Extrusion)
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<Guid> GetSelectedMeshObjectIds(RhinoDoc doc)
    {
        return doc.Objects
            .GetSelectedObjects(false, false)
            .Where(obj => obj.Geometry is Mesh || obj.Geometry is Brep || obj.Geometry is Extrusion)
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<string> GetSelectedLayerPaths(RhinoDoc doc)
    {
        if (doc.Layers.GetSelected(out var selectedLayerIndices) && selectedLayerIndices.Count > 0)
        {
            return selectedLayerIndices
                .Where(index => index >= 0 && index < doc.Layers.Count)
                .Select(index => doc.Layers[index].FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return doc.Objects
            .GetSelectedObjects(false, false)
            .Select(obj => GetLayerPath(doc, obj.Attributes.LayerIndex))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private DocumentState GetState(RhinoDoc doc)
    {
        if (_states.TryGetValue(doc.RuntimeSerialNumber, out var state))
            return state;

        state = new DocumentState
        {
            Terrains = _documentStore.Load(doc)
        };
        state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;
        _states[doc.RuntimeSerialNumber] = state;
        return state;
    }

    private void Save(RhinoDoc doc, DocumentState state)
    {
        _documentStore.Save(doc, state.Terrains);
        RaiseStateChanged();
    }

    private UndoState CaptureUndoState(DocumentState state)
    {
        return new UndoState
        {
            Json = TerrainSerializer.Serialize(state.Terrains),
            SelectedTerrainId = state.SelectedTerrainId
        };
    }

    private void RestoreUndoState(RhinoDoc doc, UndoState snapshot)
    {
        var restoredTerrains = TerrainSerializer.Deserialize(snapshot.Json);
        var restoredState = new DocumentState
        {
            Terrains = restoredTerrains,
            SelectedTerrainId = snapshot.SelectedTerrainId ?? restoredTerrains.FirstOrDefault()?.TerrainId
        };

        _states[doc.RuntimeSerialNumber] = restoredState;
        Save(doc, restoredState);
    }

    private void ScheduleRebuild(RhinoDoc doc, Guid terrainId)
    {
        _pendingRebuilds[(doc.RuntimeSerialNumber, terrainId)] = DateTime.UtcNow.AddMilliseconds(250);
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain != null)
            terrain.LastBuildMessage = "Scheduled rebuild...";
        RaiseStateChanged();
    }

    private void RebuildTerrain(RhinoDoc doc, DocumentState state, TerrainDefinition terrain)
    {
        terrain.LastBuildMessage = "Building terrain...";
        RaiseStateChanged();

        try
        {
            var build = _buildService.Build(doc, terrain);

            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastAnalysis = build.Analysis;
            terrain.LastBuildMessage = build.Diagnostics.Count == 0
                ? "Build succeeded."
                : string.Join(System.Environment.NewLine, build.Diagnostics.Take(8));

            SyncOutputs(doc, terrain, build);
            ApplyVisibilityAndLock(doc, terrain);
            Save(doc, state);
            doc.Views.Redraw();
        }
        catch (Exception ex)
        {
            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastBuildMessage = $"Build failed: {ex.Message}";
            RhinoApp.WriteLine($"[MoleHill] Rebuild failed for '{terrain.Name}': {ex}");
            Save(doc, state);
        }
    }

    private void SyncOutputs(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildResult build)
    {
        DeleteOwnedObjects(doc, terrain);

        var outputIds = new List<Guid>();
        var zoneIds = new List<Guid>();
        var auxiliaryIds = new List<Guid>();
        var markerIds = new List<Guid>();

        using var _ = new EventSuppression(this);

        if (build.PrimaryMesh != null)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Geometry = build.PrimaryMesh,
                Name = terrain.Name,
                LayerPath = terrain.TerrainLayerPath
            });

            if (id != Guid.Empty)
                outputIds.Add(id);
        }

        foreach (var zoneObject in build.ZoneObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, zoneObject);
            if (id != Guid.Empty)
                zoneIds.Add(id);
        }

        foreach (var auxiliary in build.AuxiliaryObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Geometry = auxiliary.Geometry,
                Name = auxiliary.Name,
                ColorArgb = auxiliary.ColorArgb,
                LayerPath = auxiliary.LayerPath ?? terrain.AuxiliaryLayerPath,
                SourceLayerPath = auxiliary.SourceLayerPath,
                MaterialName = auxiliary.MaterialName,
                InstanceDefinitionName = auxiliary.InstanceDefinitionName,
                MarkerBlockTemplate = auxiliary.MarkerBlockTemplate,
                InstanceTransform = auxiliary.InstanceTransform
            });
            if (id != Guid.Empty)
                auxiliaryIds.Add(id);
        }

        foreach (var marker in build.MarkerObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, marker);
            if (id != Guid.Empty)
                markerIds.Add(id);
        }

        terrain.OutputObjectIds = outputIds;
        terrain.ZoneObjectIds = zoneIds;
        terrain.AuxiliaryObjectIds = auxiliaryIds;
        terrain.MarkerObjectIds = markerIds;
    }

    private Guid AddGeneratedObject(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        var attributes = CreateAttributes(doc, terrain, generated);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
                return doc.Objects.AddInstanceObject(definitionIndex, generated.InstanceTransform, attributes);
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(brep, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            _ => Guid.Empty
        };
    }

    private static int EnsureBlockDefinition(RhinoDoc doc, string definitionName, MarkerBlockTemplate template)
    {
        var existingDefinition = doc.InstanceDefinitions.Find(definitionName);
        if (existingDefinition != null)
            return existingDefinition.Index;

        var geometry = CreateMarkerBlockGeometry(template);
        if (geometry.Count == 0)
            return -1;

        var attributes = geometry.Select(_ => new ObjectAttributes()).ToList();
        return doc.InstanceDefinitions.Add(definitionName, string.Empty, Point3d.Origin, geometry, attributes);
    }

    private static List<GeometryBase> CreateMarkerBlockGeometry(MarkerBlockTemplate template)
    {
        var geometry = new List<GeometryBase>();

        switch (template)
        {
            case MarkerBlockTemplate.Elevation:
                geometry.Add(new Circle(Plane.WorldXY, 0.8).ToNurbsCurve());
                geometry.Add(new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.8, 0.0, 0.0)));
                geometry.Add(new LineCurve(new Point3d(0.0, -0.8, 0.0), new Point3d(0.0, 0.8, 0.0)));
                break;
            case MarkerBlockTemplate.Slope:
                geometry.Add(new PolylineCurve(new[]
                {
                    new Point3d(-0.8, -0.2, 0.0),
                    new Point3d(0.4, -0.2, 0.0),
                    new Point3d(0.4, -0.6, 0.0),
                    new Point3d(0.9, 0.0, 0.0),
                    new Point3d(0.4, 0.6, 0.0),
                    new Point3d(0.4, 0.2, 0.0),
                    new Point3d(-0.8, 0.2, 0.0)
                }));
                geometry.Add(new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.9, 0.0, 0.0)));
                break;
        }

        return geometry;
    }

    private ObjectAttributes CreateAttributes(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        var attributes = new ObjectAttributes
        {
            Name = $"{terrain.Name} :: {generated.Name}"
        };

        attributes.SetUserString(OutputOwnerKey, terrain.TerrainId.ToString());

        if (generated.ColorArgb.HasValue)
        {
            attributes.ColorSource = ObjectColorSource.ColorFromObject;
            attributes.ObjectColor = System.Drawing.Color.FromArgb(generated.ColorArgb.Value);
        }

        if (!string.IsNullOrWhiteSpace(generated.LayerPath))
            attributes.LayerIndex = EnsureLayer(doc, generated.LayerPath!, generated.SourceLayerPath);

        if (!string.IsNullOrWhiteSpace(generated.MaterialName))
        {
            int materialIndex = doc.Materials.Find(generated.MaterialName!, true);
            if (materialIndex < 0)
            {
                var material = new Material { Name = generated.MaterialName! };
                materialIndex = doc.Materials.Add(material);
            }

            if (materialIndex >= 0)
            {
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                attributes.MaterialIndex = materialIndex;
            }
        }

        return attributes;
    }

    private int EnsureLayer(RhinoDoc doc, string fullPath, string? sourceLayerPath = null)
    {
        int existingIndex = doc.Layers.FindByFullPath(fullPath, -1);
        if (existingIndex >= 0)
            return existingIndex;

        int parentIndex = -1;
        string currentPath = string.Empty;
        global::Rhino.DocObjects.Layer? sourceLayer = null;
        if (!string.IsNullOrWhiteSpace(sourceLayerPath))
        {
            int sourceIndex = doc.Layers.FindByFullPath(sourceLayerPath, -1);
            if (sourceIndex >= 0)
                sourceLayer = doc.Layers[sourceIndex];
        }

        foreach (var segment in fullPath.Split(new[] { "::" }, StringSplitOptions.None))
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}::{segment}";
            int index = doc.Layers.FindByFullPath(currentPath, -1);
            if (index >= 0)
            {
                parentIndex = index;
                continue;
            }

            var layer = new Layer { Name = segment };
            if (parentIndex >= 0)
                layer.ParentLayerId = doc.Layers[parentIndex].Id;

            if (sourceLayer != null && string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                layer.Color = sourceLayer.Color;
                layer.PlotColor = sourceLayer.PlotColor;
            }

            parentIndex = doc.Layers.Add(layer);
        }

        return parentIndex >= 0 ? parentIndex : doc.Layers.CurrentLayerIndex;
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
            doc.Objects.ModifyAttributes(objectId, attributes, quiet: true);
        }
    }

    private void OnAddRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

        ScheduleRelevantTerrains(e.TheObject.Document, e.ObjectId, GetLayerPath(e.TheObject.Document, e.TheObject.Attributes.LayerIndex));
    }

    private void OnDeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

        ScheduleRelevantTerrains(e.TheObject.Document, e.ObjectId, GetLayerPath(e.TheObject.Document, e.TheObject.Attributes.LayerIndex));
    }

    private void OnReplaceRhinoObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.OldRhinoObject == null)
            return;

        ScheduleRelevantTerrains(e.Document, e.OldRhinoObject.Id, GetLayerPath(e.Document, e.OldRhinoObject.Attributes.LayerIndex));
    }

    private void OnUndeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

        ScheduleRelevantTerrains(e.TheObject.Document, e.ObjectId, GetLayerPath(e.TheObject.Document, e.TheObject.Attributes.LayerIndex));
    }

    private void OnModifyObjectAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.RhinoObject == null)
            return;

        if (TrySyncOwnedObjectLayer(e.Document, e.RhinoObject.Id, e.NewAttributes.LayerIndex))
            return;

        ScheduleRelevantTerrains(e.Document, e.RhinoObject.Id, GetLayerPath(e.Document, e.OldAttributes.LayerIndex), GetLayerPath(e.Document, e.NewAttributes.LayerIndex));
    }

    private void OnLayerTableEvent(object? sender, LayerTableEventArgs e)
    {
        if (_suppressDocEvents > 0)
            return;

        var state = GetState(e.Document);
        foreach (var terrain in state.Terrains.Where(terrain => terrain.LiveUpdateEnabled && terrain.EnumerateSourceSets().Any(source => source.LayerPaths.Count > 0)))
            ScheduleRebuild(e.Document, terrain.TerrainId);
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_pendingRebuilds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var nextItem = _pendingRebuilds
            .Where(item => item.Value <= now)
            .OrderBy(item => item.Value)
            .Select(item => ((uint docSerial, Guid terrainId)?)item.Key)
            .FirstOrDefault();

        if (!nextItem.HasValue)
            return;

        var key = nextItem.Value;
        _pendingRebuilds.Remove(key);
        var doc = RhinoDoc.FromRuntimeSerialNumber(key.docSerial);
        if (doc == null)
            return;

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == key.terrainId);
        if (terrain == null)
            return;

        RebuildTerrain(doc, state, terrain);
    }

    private void ScheduleRelevantTerrains(RhinoDoc doc, Guid objectId, params string?[] layerPaths)
    {
        var state = GetState(doc);
        foreach (var terrain in state.Terrains)
        {
            if (!terrain.LiveUpdateEnabled)
                continue;

            if (terrain.OutputObjectIds.Contains(objectId) ||
                terrain.ZoneObjectIds.Contains(objectId) ||
                terrain.AuxiliaryObjectIds.Contains(objectId) ||
                terrain.MarkerObjectIds.Contains(objectId))
            {
                continue;
            }

            bool objectMatch = terrain.EnumerateSourceSets().Any(source => source.ObjectIds.Contains(objectId));
            bool layerMatch = layerPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Any(path => terrain.EnumerateSourceSets().Any(source => source.LayerPaths.Contains(path!, StringComparer.OrdinalIgnoreCase)));

            if (objectMatch || layerMatch)
                ScheduleRebuild(doc, terrain.TerrainId);
        }
    }

    private static string? GetLayerPath(RhinoDoc doc, int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        return doc.Layers[layerIndex].FullPath;
    }

    private static ModifierDefinition? CreateModifier(string modifierKind) => modifierKind switch
    {
        "triangulate" => new TriangulateModifierDefinition(),
        "remesh" => new RemeshModifierDefinition(),
        "smooth" => new SmoothModifierDefinition(),
        "retaining-wall" => new RetainingWallModifierDefinition(),
        "grade-pad" => new GradePadModifierDefinition(),
        "grade-path" => new GradePathModifierDefinition(),
        _ => null
    };

    private static ModifierDefinition? CloneModifier(ModifierDefinition modifier)
    {
        string json = JsonSerializer.Serialize(modifier, modifier.GetType());
        return JsonSerializer.Deserialize(json, modifier.GetType()) as ModifierDefinition;
    }

    private static string NextTerrainName(IEnumerable<TerrainDefinition> terrains)
    {
        int index = 1;
        var names = terrains.Select(terrain => terrain.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains($"Terrain {index}"))
            index++;

        return $"Terrain {index}";
    }

    private void ApplyVisibilityAndLock(RhinoDoc doc, TerrainDefinition terrain)
    {
        if (terrain.IsVisible && !terrain.IsLocked && terrain.ShowTerrainMesh && terrain.ShowZoneMeshes)
            return;

        foreach (var id in terrain.OutputObjectIds)
        {
            if (!terrain.IsVisible || !terrain.ShowTerrainMesh)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.ZoneObjectIds)
        {
            if (!terrain.IsVisible || !terrain.ShowZoneMeshes)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.AuxiliaryObjectIds.Concat(terrain.MarkerObjectIds))
        {
            if (!terrain.IsVisible)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }
    }

    private static IEnumerable<Guid> AllOwnedIds(TerrainDefinition terrain) =>
        terrain.OutputObjectIds
            .Concat(terrain.ZoneObjectIds)
            .Concat(terrain.AuxiliaryObjectIds)
            .Concat(terrain.MarkerObjectIds);

    private bool TrySyncOwnedObjectLayer(RhinoDoc doc, Guid objectId, int newLayerIndex)
    {
        string? newLayerPath = GetLayerPath(doc, newLayerIndex);
        if (string.IsNullOrWhiteSpace(newLayerPath))
            return false;

        var state = GetState(doc);
        foreach (var terrain in state.Terrains)
        {
            if (terrain.OutputObjectIds.Contains(objectId))
            {
                if (string.Equals(terrain.TerrainLayerPath, newLayerPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                terrain.TerrainLayerPath = newLayerPath;
                Save(doc, state);
                return true;
            }

            if (terrain.AuxiliaryObjectIds.Contains(objectId))
            {
                if (string.Equals(terrain.AuxiliaryLayerPath, newLayerPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                terrain.AuxiliaryLayerPath = newLayerPath;
                Save(doc, state);
                return true;
            }
        }

        return false;
    }

    private void OnRestoreStateUndo(object? sender, global::Rhino.Commands.CustomUndoEventArgs e)
    {
        if (e.Tag is not UndoState snapshot)
            return;

        RestoreUndoState(e.Document, snapshot);
        e.Document.AddCustomUndoEvent("Detach MoleHill Terrain", OnRestoreStateUndo, CaptureUndoState(GetState(e.Document)));
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private sealed class DocumentState
    {
        public List<TerrainDefinition> Terrains { get; set; } = new();
        public Guid? SelectedTerrainId { get; set; }
    }

    private sealed class UndoState
    {
        public string Json { get; init; } = string.Empty;
        public Guid? SelectedTerrainId { get; init; }
    }

    private sealed class EventSuppression : IDisposable
    {
        private readonly TerrainController _controller;

        public EventSuppression(TerrainController controller)
        {
            _controller = controller;
            _controller._suppressDocEvents++;
        }

        public void Dispose()
        {
            _controller._suppressDocEvents--;
        }
    }
}
