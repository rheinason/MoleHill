using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// The single declaration of every output destination MoleHill has: the layer path it resolves to
/// when nothing binds it, its appearance, and whether its colour is data or drafting.
///
/// This replaces three tables that used to be kept in agreement by hand — the suffix table in the
/// old <c>GeneratedLayerDefaults</c>, the kind-to-suffix switch in the old <c>SectionOutputLayers</c>,
/// and the hardcoded entry list in <c>LayerTemplateStore</c> — each of which spelled the same layer
/// paths independently, and which had tests whose only purpose was to catch them drifting apart. The
/// shipped layer template is now generated from this table, so there is nothing left to drift.
/// </summary>
internal static class LayerRoleRegistry
{
    // Colours whose meaning is fixed rather than a matter of taste. Cut and fill in particular must
    // never be seeded the same colour: a section that shades both alike cannot be read at all.
    private const int CutColorArgb = TerrainSectionAnnotationDefinitionBase.DefaultCutColorArgb;
    private const int FillColorArgb = TerrainSectionAnnotationDefinitionBase.DefaultFillColorArgb;
    private const int Black = unchecked((int)0xFF000000);
    private const int MidGrey = unchecked((int)0xFF8C8C8C);
    private const int LightGrey = unchecked((int)0xFFB4B4B4);
    private const int DarkGrey = unchecked((int)0xFF808080);

    /// <summary>Viewport thickness for a role whose print width is unset.</summary>
    public const int DefaultPreviewWidthPx = 2;

    private static readonly LayerRoleDescriptor[] Descriptors =
    {
        // ── Roots ────────────────────────────────────────────────────────────
        new(LayerRole.Terrain, "terrain", "Terrain", null, "", TerrainDefinition.DefaultTerrainLayerPath,
            new LayerAppearanceDefaults(
                ColorArgb: TerrainDefinition.DefaultTerrainColorArgb,
                PrintColorArgb: TerrainDefinition.DefaultTerrainColorArgb,
                PlotWeight: 0.18),
            LayerRoleFacets.Mesh,
            // The terrain carries its own colour and transparency, set per terrain by the user.
            ColorFromObject: true),

        new(LayerRole.Auxiliary, "auxiliary", "Auxiliary", null, "", TerrainDefinition.DefaultAuxiliaryLayerPath,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFFAAAAAA),
                PrintColorArgb: unchecked((int)0xFFAAAAAA),
                PlotWeight: 0.13),
            LayerRoleFacets.Mesh | LayerRoleFacets.Line),

        new(LayerRole.Annotation, "annotation", "Annotation", null, "", TerrainDefinition.DefaultAnnotationLayerPath,
            new LayerAppearanceDefaults(ColorArgb: Black, PrintColorArgb: Black, PlotWeight: 0.13),
            LayerRoleFacets.Line | LayerRoleFacets.Text),

        // Zone meshes hang under one branch with the zone's own input layer path appended, so a
        // document's zone layers mirror the layers the zones were read from.
        new(LayerRole.Zones, "zones", "Zones", null, "", "MoleHill::Zones",
            new LayerAppearanceDefaults(PlotWeight: 0.13),
            LayerRoleFacets.Mesh,
            // A zone's colour identifies the zone: it comes from the source layer or an override.
            ColorFromObject: true),

        new(LayerRole.Scatter, "scatter", "Scatter", null, "", "MoleHill::Scatter",
            new LayerAppearanceDefaults(PlotWeight: 0.13),
            LayerRoleFacets.Block | LayerRoleFacets.Mesh,
            ColorFromObject: true),

        // ── Auxiliary family ─────────────────────────────────────────────────
        // Walls and grading output each get their own sublayer rather than sharing the bare
        // Auxiliary layer, so either can be hidden, locked or restyled without the other. They used
        // to land directly on Auxiliary, which left no way to tell wall Breps from stair geometry in
        // the Layers pane.
        new(LayerRole.Walls, "walls", "Retaining Walls", LayerRole.Auxiliary, "::Walls",
            null,
            new LayerAppearanceDefaults(), LayerRoleFacets.Mesh),

        new(LayerRole.GradingAuxiliary, "grading-aux", "Grading Auxiliary", LayerRole.Auxiliary, "::Grading",
            null,
            new LayerAppearanceDefaults(), LayerRoleFacets.Line | LayerRoleFacets.Mesh),

        // ── Contours ─────────────────────────────────────────────────────────
        new(LayerRole.Contours, "contours", "Contours", LayerRole.Annotation, "::Contours",
            null,
            new LayerAppearanceDefaults(PlotWeight: 0.13), LayerRoleFacets.Line),

        // A major contour must read heavier than a minor one on paper and on screen alike.
        new(LayerRole.ContoursMajor, "contours-major", "Contours (Major)", LayerRole.Contours, "::Major",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFF6E4B1F), PrintColorArgb: Black, PlotWeight: 0.35),
            LayerRoleFacets.Line),

        new(LayerRole.ContoursMinor, "contours-minor", "Contours (Minor)", LayerRole.Contours, "::Minor",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFFA98A5C), PrintColorArgb: Black, PlotWeight: 0.13),
            LayerRoleFacets.Line),

        // Delta contours are depths, not elevations, so they must not read as terrain contours: their own
        // branch, in the cut hue the sections already use for cut.
        new(LayerRole.CutFillContours, "cut-fill-contours", "Cut / Fill Contours",
            LayerRole.Annotation, "::Cut Fill Contours",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: CutColorArgb, PrintColorArgb: CutColorArgb, PlotWeight: 0.13),
            LayerRoleFacets.Line),

        // The balance line is a decision, not a measurement, and it is the one line on a cut/fill drawing
        // a reader looks for first: heaviest of the three, and in neither the cut nor the fill colour.
        new(LayerRole.BalanceLine, "balance-line", "Balance Line", LayerRole.Annotation, "::Balance Line",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFF7B1FA2),
                PrintColorArgb: Black,
                PlotWeight: 0.50),
            LayerRoleFacets.Line),

        // ── Other drawing output under Annotation ────────────────────────────
        new(LayerRole.Waterflow, "waterflow", "Waterflow", LayerRole.Annotation, "::Waterflow",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFF1565C0),
                PrintColorArgb: unchecked((int)0xFF1565C0),
                PlotWeight: 0.30),
            LayerRoleFacets.Line),

        // A catchment boundary is a divide, not a flow: it is drawn in the same family as waterflow but
        // must not be mistaken for one, so it takes the heavier weight of the two and a distinct hue.
        new(LayerRole.Catchments, "catchments", "Catchments", LayerRole.Annotation, "::Catchments",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFF00897B),
                PrintColorArgb: Black,
                PlotWeight: 0.35),
            LayerRoleFacets.Line),

        // Flow paths share the waterflow colour on purpose — they are the same thing, traced from a
        // catchment's high point rather than from a point the user dropped.
        new(LayerRole.CatchmentFlowPaths, "catchment-flow-paths", "Catchment Flow Paths",
            LayerRole.Annotation, "::Catchment Flow Paths",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: unchecked((int)0xFF1565C0),
                PrintColorArgb: unchecked((int)0xFF1565C0),
                PlotWeight: 0.20),
            LayerRoleFacets.Line),

        // Labels print at the layer default width. The explicit pixel width keeps them at the
        // thickness they have always previewed at, rather than the 2 px the null weight would derive.
        new(LayerRole.Labels, "labels", "Labels", LayerRole.Annotation, "::Labels",
            null,
            new LayerAppearanceDefaults(PreviewWidthPx: 1, SuppressInheritedPlotWeight: true),
            LayerRoleFacets.Text),

        // ── Markers ──────────────────────────────────────────────────────────
        // Before roles existed these had no layer at all, and baked onto whatever layer the user
        // happened to be working on.
        new(LayerRole.Markers, "markers", "Markers", LayerRole.Annotation, "::Markers",
            null,
            new LayerAppearanceDefaults(), LayerRoleFacets.Block | LayerRoleFacets.Text,
            ColorFromObject: true),

        new(LayerRole.MarkerLabels, "marker-labels", "Marker Labels", LayerRole.Markers, "",
            null,
            new LayerAppearanceDefaults(PreviewWidthPx: 1, SuppressInheritedPlotWeight: true),
            LayerRoleFacets.Text, ColorFromObject: true),

        // ── Sections ─────────────────────────────────────────────────────────
        // The proposed terrain is the subject of the drawing: the heaviest line on it, and black.
        new(LayerRole.Sections, "sections", "Sections", LayerRole.Annotation, "::Sections",
            null,
            new LayerAppearanceDefaults(ColorArgb: Black, PrintColorArgb: Black, PlotWeight: 0.70),
            LayerRoleFacets.Line),

        // Existing ground is context: a light grey line the proposed profile is read against.
        new(LayerRole.SectionsExisting, "sections-existing", "Sections — Existing Ground",
            LayerRole.Sections, "::Existing",
            null,
            new LayerAppearanceDefaults(ColorArgb: MidGrey, PrintColorArgb: MidGrey, PlotWeight: 0.18),
            LayerRoleFacets.Line),

        new(LayerRole.SectionsCuts, "sections-cuts", "Sections — Cut Lines", LayerRole.Sections, "::Cuts",
            null,
            new LayerAppearanceDefaults(ColorArgb: Black, PrintColorArgb: Black, PlotWeight: 0.50),
            LayerRoleFacets.Line),

        new(LayerRole.SectionsGrid, "sections-grid", "Sections — Grid", LayerRole.Sections, "::Grid",
            null,
            new LayerAppearanceDefaults(ColorArgb: LightGrey, PrintColorArgb: DarkGrey, PlotWeight: 0.13),
            LayerRoleFacets.Line),

        new(LayerRole.SectionsTicks, "sections-ticks", "Sections — Ticks", LayerRole.Sections, "::Ticks",
            null,
            new LayerAppearanceDefaults(
                // Mid grey, not the dark grey the old shipped template carried: the two tables
                // disagreed, and this is the value the build path actually seeded onto the layer.
                ColorArgb: MidGrey, PrintColorArgb: Black, PlotWeight: 0.18, PreviewWidthPx: 1),
            LayerRoleFacets.Line),

        new(LayerRole.SectionsLabels, "sections-labels", "Sections — Labels", LayerRole.Sections, "::Labels",
            null,
            new LayerAppearanceDefaults(
                ColorArgb: Black, PrintColorArgb: Black, PreviewWidthPx: 1,
                SuppressInheritedPlotWeight: true),
            LayerRoleFacets.Text),

        // A grouping layer. It carries the fills' own hairline weight rather than inheriting the
        // profile's 0.70, so anything a user drops on it by hand does not print as a heavy line.
        new(LayerRole.SectionsCutFill, "sections-cutfill", "Sections — Cut / Fill",
            LayerRole.Sections, "::CutFill",
            null,
            new LayerAppearanceDefaults(PlotWeight: 0.13, HatchPatternName: "Solid"),
            LayerRoleFacets.Fill),

        new(LayerRole.SectionsCutFillCut, "sections-cutfill-cut", "Sections — Cut",
            LayerRole.SectionsCutFill, "::Cut",
            null,
            new LayerAppearanceDefaults(ColorArgb: CutColorArgb, PrintColorArgb: CutColorArgb, PlotWeight: 0.13),
            LayerRoleFacets.Fill),

        new(LayerRole.SectionsCutFillFill, "sections-cutfill-fill", "Sections — Fill",
            LayerRole.SectionsCutFill, "::Fill",
            null,
            new LayerAppearanceDefaults(ColorArgb: FillColorArgb, PrintColorArgb: FillColorArgb, PlotWeight: 0.13),
            LayerRoleFacets.Fill)
    };

    private static readonly Dictionary<LayerRole, LayerRoleDescriptor> ByRole =
        Descriptors.ToDictionary(descriptor => descriptor.Role);

    private static readonly Dictionary<string, LayerRoleDescriptor> ById =
        Descriptors.ToDictionary(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<LayerRoleDescriptor> All => Descriptors;

    public static LayerRoleDescriptor For(LayerRole role) => ByRole[role];

    /// <summary>Resolves a persisted id. Returns null for an id written by a newer build, so an
    /// unknown role degrades to an unbound plain layer instead of losing the entry.</summary>
    public static LayerRoleDescriptor? ForId(string? id) =>
        id != null && ById.TryGetValue(id, out var descriptor) ? descriptor : null;

    /// <summary>
    /// The layer path a role resolves to when neither it nor any ancestor is bound by a template:
    /// the parent's default path plus this role's suffix, terminating at a root's absolute path.
    /// Never null — that is what lets the routing API drop nullable returns entirely.
    /// </summary>
    public static string DefaultPath(LayerRole role)
    {
        var descriptor = For(role);
        if (descriptor.AbsoluteDefaultPath != null)
            return descriptor.AbsoluteDefaultPath;

        return DefaultPath(descriptor.Parent!.Value) + descriptor.RelativeSuffix;
    }

    /// <summary>
    /// Viewport thickness derived from print width, so the screen shows the same hierarchy the page
    /// does and there is only one number to maintain. Roles whose derived value would change how
    /// they look today carry an explicit <c>PreviewWidthPx</c> instead.
    /// </summary>
    public static int DerivePreviewWidthPx(double? plotWeight)
    {
        if (!plotWeight.HasValue || plotWeight.Value <= 0.0)
            return DefaultPreviewWidthPx;

        return plotWeight.Value switch
        {
            < 0.15 => 1,
            < 0.25 => 2,
            < 0.45 => 3,
            < 0.61 => 4,
            _ => 5
        };
    }
}
