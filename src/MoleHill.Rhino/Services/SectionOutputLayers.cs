namespace MoleHill.Rhino.Services;

internal enum SectionLayerKind
{
    Profile,
    Cuts,
    Grid,
    Ticks,
    Labels,
    CutFillCut,
    CutFillFill
}

internal static class SectionOutputLayers
{
    public static string? ResolveLayerPath(string? outputLayerPath, string? fallbackLayerPath, SectionLayerKind kind)
    {
        string? root = !string.IsNullOrWhiteSpace(outputLayerPath)
            ? outputLayerPath
            : fallbackLayerPath;

        if (string.IsNullOrWhiteSpace(root))
            return null;

        return kind switch
        {
            SectionLayerKind.Profile => root,
            SectionLayerKind.Cuts => $"{root}::Cuts",
            SectionLayerKind.Grid => $"{root}::Grid",
            SectionLayerKind.Ticks => $"{root}::Ticks",
            SectionLayerKind.Labels => $"{root}::Labels",
            SectionLayerKind.CutFillCut => $"{root}::CutFill::Cut",
            SectionLayerKind.CutFillFill => $"{root}::CutFill::Fill",
            _ => root
        };
    }
}
