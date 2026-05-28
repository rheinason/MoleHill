namespace MoleHill.Rhino.Services;

internal static class TerrainOutputSyncPolicy
{
    public static bool ShouldSyncAuxiliaryOutput(GeneratedRhinoObject generated)
    {
        return !generated.AnalysisId.HasValue;
    }
}
