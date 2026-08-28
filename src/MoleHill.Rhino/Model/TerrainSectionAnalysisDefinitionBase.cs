namespace MoleHill.Rhino.Model;

public abstract class TerrainSectionAnalysisDefinitionBase : AnalysisDefinition
{
    public const int DefaultCutColorArgb = unchecked((int)0xFFEB462D);

    public const int DefaultFillColorArgb = unchecked((int)0xFF4C849E);

    public SourceReferenceSet Sources { get; set; } = new();

    public List<Guid> ComparisonTerrainIds { get; set; } = new();

    public Guid? CutFillReferenceTerrainId { get; set; }

    public bool ShowCutFillRegions { get; set; } = true;

    public int CutColorArgb { get; set; } = DefaultCutColorArgb;

    public int FillColorArgb { get; set; } = DefaultFillColorArgb;

    public int CutFillOpacityPercent { get; set; } = 40;

    /// <summary>Hatch pattern for cut regions. Emitted as a real Rhino hatch so the fill prints; the
    /// pattern itself is edited in Rhino's hatch pattern table.</summary>
    public string? CutHatchPatternName { get; set; }

    /// <summary>Hatch pattern for fill regions.</summary>
    public string? FillHatchPatternName { get; set; }

    /// <summary>Pattern scale for the generated hatch. 0 (the default) derives a scale from the annotation
    /// text height, so the fill reads as a texture at whatever scale the drawing is set up for. A pattern's
    /// native spacing is arbitrary — Rhino's Hatch1 is 0.125 model units — so a fixed scale of 1 prints as
    /// solid black on a real section. Set a positive value to override.</summary>
    public double HatchScale { get; set; }

    /// <summary>Pattern rotation, in degrees, passed to the generated hatch.</summary>
    public double HatchRotationDegrees { get; set; }

    public string? OutputLayerPath { get; set; }

    public int? ColorArgb { get; set; }

    public double InsertionOriginX { get; set; }

    public double InsertionOriginY { get; set; }

    public double InsertionOriginZ { get; set; }

    public double InsertionXAxisX { get; set; } = 1.0;

    public double InsertionXAxisY { get; set; }

    public double InsertionXAxisZ { get; set; }

    public double InsertionYAxisX { get; set; }

    public double InsertionYAxisY { get; set; } = 1.0;

    public double InsertionYAxisZ { get; set; }

    public bool HasInsertionPlane { get; set; }

    public double TextHeight { get; set; } = 1.0;

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }
}
