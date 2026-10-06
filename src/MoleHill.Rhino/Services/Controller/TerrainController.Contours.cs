using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

// Contour annotation fast paths: re-contour, or recolour, one contour annotation from the cached final
// mesh without rebuilding the terrain.
internal sealed partial class TerrainController
{
    public void RebuildContourAnalysis(RhinoDoc doc, Guid terrainId, Guid analysisId)
    {
        if (!ModelUnitGuard.TryGet(doc, out MoleHill.Shared.ModelUnitContext unitContext))
            return;

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        var analysis = terrain.Annotations.OfType<ContourAnnotationDefinition>().FirstOrDefault(a => a.Id == analysisId);
        if (analysis == null)
            return;

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        var mesh = runtimeCache.DisplayState?.TerrainMesh;
        if (mesh == null)
        {
            ScheduleRebuild(doc, terrainId);
            return;
        }

        string terrainIdString = terrainId.ToString();
        string analysisIdString = analysisId.ToString();
        var toDelete = doc.Objects
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
                obj?.Attributes?.GetUserString(OutputAnalysisIdKey) == analysisIdString)
            .Select(obj => obj.Id)
            .ToList();

        using var _ = new EventSuppression(this);
        DeleteObjects(doc, toDelete);
        if (toDelete.Count > 0)
            terrain.AuxiliaryObjectIds.RemoveAll(toDelete.Contains);

        var (newObjects, summary) = TerrainBuildService.BuildContourObjects(mesh, analysis, doc.ModelAbsoluteTolerance);

        var existing = terrain.LastAnalysisResults.FirstOrDefault(r => r.AnalysisId == analysisId);
        if (existing != null)
            terrain.LastAnalysisResults.Remove(existing);
        terrain.LastAnalysisResults.Add(summary);

        var displayState = runtimeCache.DisplayState;
        if (displayState != null)
        {
            displayState.AuxiliaryObjects.RemoveAll(o => o.AnalysisId == analysisId);
            displayState.AuxiliaryObjects.AddRange(newObjects);
            displayState.AnalysisResults.RemoveAll(r => r.AnalysisId == analysisId);
            displayState.AnalysisResults.Add(summary);
        }

        Save(doc, state, raiseStateChanged: true);
        doc.Views.Redraw();
    }

    public void RefreshContourColor(RhinoDoc doc, Guid terrainId, Guid analysisId, int? colorArgb)
    {
        string terrainIdString = terrainId.ToString();
        string analysisIdString = analysisId.ToString();

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        if (runtimeCache.DisplayState != null)
        {
            for (int index = 0; index < runtimeCache.DisplayState.AuxiliaryObjects.Count; index++)
            {
                GeneratedRhinoObject generated = runtimeCache.DisplayState.AuxiliaryObjects[index];
                if (generated.AnalysisId != analysisId)
                    continue;

                runtimeCache.DisplayState.AuxiliaryObjects[index] = new GeneratedRhinoObject
                {
                    Role = generated.Role,
                    Geometry = generated.Geometry,
                    Name = generated.Name,
                    Kind = generated.Kind,
                    AnalysisId = generated.AnalysisId,
                    ColorArgb = colorArgb,
                    LayerPath = generated.LayerPath,
                    SourceLayerPath = generated.SourceLayerPath,
                    MaterialName = generated.MaterialName,
                    InstanceDefinitionName = generated.InstanceDefinitionName,
                    MarkerBlockTemplate = generated.MarkerBlockTemplate,
                    InstanceUserStrings = generated.InstanceUserStrings,
                    InstanceTransform = generated.InstanceTransform
                };
            }
        }

        using var _ = new EventSuppression(this);
        foreach (var obj in doc.Objects
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
                obj?.Attributes?.GetUserString(OutputAnalysisIdKey) == analysisIdString))
        {
            var attributes = obj.Attributes.Duplicate();
            if (colorArgb.HasValue)
            {
                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                attributes.ObjectColor = GetOpaqueColor(System.Drawing.Color.FromArgb(colorArgb.Value));
            }
            else
            {
                attributes.ColorSource = ObjectColorSource.ColorFromLayer;
            }
            doc.Objects.ModifyAttributes(obj, attributes, quiet: true);
        }

        doc.Views.Redraw();
    }
}
