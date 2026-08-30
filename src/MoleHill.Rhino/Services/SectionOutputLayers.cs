namespace MoleHill.Rhino.Services;

internal enum SectionLayerKind
{
    Profile,

    /// <summary>The ground as it was before grading, drawn heavy so the section reads as a section.</summary>
    ExistingProfile,

    Cuts,
    Grid,
    Ticks,
    Labels,
    CutFillCut,
    CutFillFill
}

internal static class SectionOutputLayers
{
    /// <summary>Branch every section sublayer hangs from, matching <see cref="LayerTemplateStore"/>.</summary>
    private const string SectionsBranch = "::Sections";

    /// <summary>True when a layer path's last segment already names the sections branch, whether it is
    /// nested (<c>MoleHill::Annotation::Sections</c>) or a root layer of its own (<c>Sections</c>).</summary>
    private static bool IsSectionsBranch(string path) =>
        path.EndsWith(SectionsBranch, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, SectionsBranch[2..], StringComparison.OrdinalIgnoreCase);

    public static string? ResolveLayerPath(string? outputLayerPath, string? fallbackLayerPath, SectionLayerKind kind)
    {
        string? root = !string.IsNullOrWhiteSpace(outputLayerPath)
            ? outputLayerPath
            : fallbackLayerPath;

        if (string.IsNullOrWhiteSpace(root))
            return null;

        // Section output lives under a "Sections" branch so the paths match the ones the office layer
        // template styles (MoleHill::Annotation::Sections::...). Without it, generated sublayers were
        // siblings of the template's, and the hatches landed on freshly created, unstyled black layers.
        // A user who names an output layer that already ends in ::Sections is taken at their word.
        string sections = IsSectionsBranch(root!) ? root! : $"{root}{SectionsBranch}";

        return kind switch
        {
            SectionLayerKind.Profile => sections,
            SectionLayerKind.ExistingProfile => $"{sections}::Existing",
            SectionLayerKind.Cuts => $"{sections}::Cuts",
            SectionLayerKind.Grid => $"{sections}::Grid",
            SectionLayerKind.Ticks => $"{sections}::Ticks",
            SectionLayerKind.Labels => $"{sections}::Labels",
            SectionLayerKind.CutFillCut => $"{sections}::CutFill::Cut",
            SectionLayerKind.CutFillFill => $"{sections}::CutFill::Fill",
            _ => sections
        };
    }
}
