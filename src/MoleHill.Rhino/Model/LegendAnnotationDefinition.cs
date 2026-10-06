namespace MoleHill.Rhino.Model;

/// <summary>
/// A key to the analysis colouring the terrain, drawn into the model so it can go on a sheet.
///
/// <para>It keys whatever the terrain is coloured by — the first enabled analysis that colours it — rather
/// than naming an analysis of its own. The terrain shows one colouring at a time, so a legend for any
/// other analysis would describe colours that are not on the drawing; following the coloured one is also
/// what keeps it right when a card is reordered or switched off.</para>
///
/// <para>Unlike every other annotation it is not produced by the build. Ramp edits recolour the terrain
/// without rebuilding it, so a legend drawn by the build would go stale on the first stop dragged. It is
/// regenerated with the preview colouring instead (<c>TerrainController.UpdateRuntimePreview</c>), from
/// the same resolved range and palette, so the key and the mesh cannot disagree.</para>
/// </summary>
public sealed class LegendAnnotationDefinition : AnnotationDefinition
{
    public LegendAnnotationDefinition()
    {
        Label = "Legend";
    }

    /// <summary>Heading drawn above the key. Empty uses the coloured analysis's own name, with its unit.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Lay the key out top to bottom (the default) or left to right.</summary>
    public bool Horizontal { get; set; }

    /// <summary>
    /// Size of one swatch, and the thickness of a gradient strip, as a multiple of the text height — so
    /// the key stays proportioned when the annotation style is rescaled.
    /// </summary>
    public double SwatchSize { get; set; } = 1.5;

    /// <summary>Length of a gradient strip as a multiple of the text height.</summary>
    public double GradientLength { get; set; } = 12.0;

    /// <summary>Override colour for the text and outlines. Unset draws them in the Legend layer's colour;
    /// the swatches always carry the analysis's own colours.</summary>
    public int? ColorArgb { get; set; }

    public double InsertionOriginX { get; set; }

    public double InsertionOriginY { get; set; }

    public double InsertionOriginZ { get; set; }

    /// <summary>False places the key beside the terrain's bounding box, so a freshly added card draws
    /// something without asking for a pick first.</summary>
    public bool HasInsertionPlane { get; set; }
}
