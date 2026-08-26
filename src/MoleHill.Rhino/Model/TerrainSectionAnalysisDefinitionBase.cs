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
