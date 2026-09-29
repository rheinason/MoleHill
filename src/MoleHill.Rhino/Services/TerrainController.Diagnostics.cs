using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Diagnostics surfaced in the panel: runtime-overlay issue counts and visibility, the modifier mesh-
// quality hint, and the repro case bundle export.
internal sealed partial class TerrainController
{
    public bool TryExportTerrainCaseBundle(
        RhinoDoc doc,
        Guid terrainId,
        out string? archivePath,
        out string? coreTestCode,
        out string? errorMessage)
    {
        archivePath = null;
        coreTestCode = null;
        errorMessage = null;

        TerrainDefinition? terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
        {
            errorMessage = "Select a terrain before exporting a case bundle.";
            return false;
        }

        try
        {
            TerrainBuildSnapshot snapshot = CreateBuildSnapshot(doc, terrain);
            try
            {
                TerrainDisplayState? displayState = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).DisplayState?.Clone();
                TerrainCaseBundleExportResult export = TerrainCaseBundleExporter.Export(doc, terrain, snapshot, displayState);
                archivePath = export.ArchivePath;
                coreTestCode = export.CoreTestCode;
                return true;
            }
            finally
            {
                snapshot.DisposeSectionTerrainMeshes();
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not export the case bundle: {ex.Message}";
            return false;
        }
    }

    public RuntimeDiagnosticSummary GetRuntimeDiagnosticSummary(
        RhinoDoc doc,
        Guid terrainId,
        RuntimeOverlayOwner owner)
    {
        TerrainDisplayState? displayState = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).DisplayState;
        if (displayState == null)
            return default;

        int errors = 0;
        int warnings = 0;
        int information = 0;
        foreach (RuntimeOverlayItem item in displayState.RuntimeOverlays)
        {
            if (item.Channel != RuntimeOverlayChannel.Diagnostic || item.Owner != owner)
                continue;

            switch (item.Severity)
            {
                case RuntimeOverlaySeverity.Error:
                    errors++;
                    break;
                case RuntimeOverlaySeverity.Warning:
                    warnings++;
                    break;
                default:
                    information++;
                    break;
            }
        }

        return new RuntimeDiagnosticSummary(errors, warnings, information);
    }

    public bool IsRuntimeDiagnosticsVisible(RhinoDoc doc, Guid terrainId, RuntimeOverlayOwner owner)
    {
        return GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).VisibleDiagnosticOwners.Contains(owner);
    }

    public void SetRuntimeDiagnosticsVisible(
        RhinoDoc doc,
        Guid terrainId,
        RuntimeOverlayOwner owner,
        bool isVisible)
    {
        TerrainRuntimeCache runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        if (isVisible)
            runtimeCache.VisibleDiagnosticOwners.Add(owner);
        else
            runtimeCache.VisibleDiagnosticOwners.Remove(owner);

        if (runtimeCache.DisplayState != null)
        {
            runtimeCache.DisplayState.VisibleDiagnosticOwners.Clear();
            runtimeCache.DisplayState.VisibleDiagnosticOwners.UnionWith(runtimeCache.VisibleDiagnosticOwners);
            runtimeCache.DisplayState.InvalidateOverlayBounds();
        }

        doc.Views.Redraw();
    }

    public string? GetModifierMeshQualityWarning(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        int modifierIndex = terrain?.Modifiers.FindIndex(item => item.Id == modifierId) ?? -1;
        if (terrain == null || modifierIndex < 0 ||
            terrain.Modifiers[modifierIndex] is not (SmoothModifierDefinition or SculptModifierDefinition))
        {
            return null;
        }

        bool hasPriorRemesh = terrain.Modifiers
            .Take(modifierIndex)
            .Any(item => item.IsEnabled && item is RemeshModifierDefinition);
        if (hasPriorRemesh)
            return null;

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        Mesh? incomingMesh = FindIncomingModifierMesh(runtimeCache, terrain, modifierIndex);
        if (incomingMesh == null)
            return null;

        ExtractedMeshData meshData = RhinoGeometryConversions.GetNormalizedMeshData(incomingMesh);
        MeshRegularitySummary summary = MeshRegularityAnalyzer.Analyze(meshData.Vertices, meshData.Faces);
        bool isSparse = MeshRegularityAnalyzer.IsVerySparse(summary);
        bool hasSkinnyTriangles = MeshRegularityAnalyzer.HasVerySkinnyTriangles(summary);
        if (!isSparse && !hasSkinnyTriangles)
            return null;

        var reasons = new List<string>();
        if (isSparse)
            reasons.Add($"only {summary.FaceCount:N0} faces / coarse spacing");
        if (hasSkinnyTriangles)
            reasons.Add($"{summary.SkinnyFraction:P0} very skinny sampled triangles");

        return $"Incoming mesh has {string.Join(" and ", reasons)}. Add an enabled Remesh modifier below this card for a more even surface before applying {terrain.Modifiers[modifierIndex].Label}.";
    }

    private static Mesh? FindIncomingModifierMesh(
        TerrainRuntimeCache runtimeCache,
        TerrainDefinition terrain,
        int modifierIndex)
    {
        TerrainBuildMode preferredMode = runtimeCache.DisplayState?.IsPreview == true
            ? TerrainBuildMode.Preview
            : TerrainBuildMode.Final;
        foreach (TerrainBuildMode mode in new[] { preferredMode, preferredMode == TerrainBuildMode.Final ? TerrainBuildMode.Preview : TerrainBuildMode.Final })
        {
            for (int index = modifierIndex - 1; index >= 0; index--)
            {
                ModifierDefinition previous = terrain.Modifiers[index];
                if (!previous.IsEnabled)
                    continue;

                string stageKey = TerrainStageKey.ForMode(mode, TerrainStageKey.CreateModifier(previous));
                if (runtimeCache.StageEntries.TryGetValue(stageKey, out StageCacheEntry? entry) && entry.MeshOutput != null)
                    return entry.MeshOutput;
            }
        }

        return runtimeCache.DisplayState?.BaseTerrainMesh;
    }
}
