namespace MoleHill.Rhino.Services;

/// <summary>Defines the completed-final-state contract used by the Grasshopper snapshot bridge.</summary>
internal static class TerrainSnapshotEligibility
{
    public static string? GetRejectionReason(
        string terrainName,
        bool hasCompletedMesh,
        bool isPreview,
        bool hasDeferredOutputs)
    {
        if (!hasCompletedMesh)
            return $"Terrain '{terrainName}' has no completed build to snapshot.";
        if (isPreview || hasDeferredOutputs)
            return $"Terrain '{terrainName}' is showing a preview. Wait for its final MoleHill build before taking a snapshot.";
        return null;
    }
}
