namespace MoleHill.Rhino.Services;

internal enum SectionLayerKind
{
    Profile,
    Cuts,
    Grid,
    Ticks,
    Labels
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
            _ => root
        };
    }

    public static double? GetPlotWeight(SectionLayerKind kind) => kind switch
    {
        SectionLayerKind.Profile => 0.50,
        SectionLayerKind.Cuts => 0.50,
        SectionLayerKind.Grid => 0.13,
        SectionLayerKind.Ticks => 0.18,
        SectionLayerKind.Labels => null,
        _ => null
    };
}
