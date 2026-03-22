namespace MoleHill.Rhino.Model;

public sealed class TerrainDefinition
{
    public const int CurrentSchemaVersion = 13;
    public const int DefaultTerrainColorArgb = unchecked((int)0xFFC7D2C2);

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid TerrainId { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Terrain";

    public bool LiveUpdateEnabled { get; set; } = true;

    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; } = false;

    public bool ProtectOutput { get; set; } = false;

    public int OutputTransparencyPercent { get; set; } = 0;

    public int TerrainColorArgb { get; set; } = DefaultTerrainColorArgb;

    public bool ShowTerrainMesh { get; set; } = true;

    public bool ShowZoneMeshes { get; set; } = true;

    public bool ShowMeshWires { get; set; } = true;

    public bool ShowSlopePreview { get; set; }

    public string? TerrainLayerPath { get; set; }

    public string? AuxiliaryLayerPath { get; set; }

    public string SlopePalettePreset { get; set; } = MoleHill.Rhino.Services.SlopePreviewPaletteCatalog.DefaultKey;

    public double SlopeColorLowPercent { get; set; }

    public double SlopeColorHighPercent { get; set; }

    public double GlobalTolerance { get; set; }

    public SourceReferenceSet EarthworkReference { get; set; } = new();

    public SourceReferenceSet EarthworkBoundary { get; set; } = new();

    public List<ModifierDefinition> Modifiers { get; set; } = new();

    public List<MarkerDefinition> Markers { get; set; } = new();

    public List<CollageZoneDefinition> Zones { get; set; } = new();

    public List<AnalysisDefinition> Analyses { get; set; } = new();

    public List<Guid> OutputObjectIds { get; set; } = new();

    public List<Guid> ZoneObjectIds { get; set; } = new();

    public List<Guid> AuxiliaryObjectIds { get; set; } = new();

    public List<Guid> MarkerObjectIds { get; set; } = new();

    public string? LastBuildMessage { get; set; }

    public DateTimeOffset? LastBuildUtc { get; set; }

    public TerrainAnalysisSummary? LastAnalysis { get; set; }

    public IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        foreach (var modifier in Modifiers)
        {
            foreach (var sourceSet in modifier.EnumerateSourceSets())
                yield return sourceSet;
        }

        foreach (var marker in Markers)
        {
            foreach (var sourceSet in marker.EnumerateSourceSets())
                yield return sourceSet;
        }

        foreach (var zone in Zones)
        {
            yield return zone.Boundaries;
        }

        yield return EarthworkReference;
        yield return EarthworkBoundary;
    }

    public void EnsureBaseModifier()
    {
        TriangulateModifierDefinition? baseModifier = null;
        for (int index = 0; index < Modifiers.Count; index++)
        {
            if (Modifiers[index] is not TriangulateModifierDefinition triangulate)
                continue;

            if (baseModifier == null)
            {
                baseModifier = triangulate;
                continue;
            }

            Modifiers[index] = AddGeometryModifierDefinition.FromTriangulate(triangulate);
        }

        if (baseModifier == null)
        {
            baseModifier = new TriangulateModifierDefinition();
            Modifiers.Insert(0, baseModifier);
        }
        else
        {
            int baseIndex = Modifiers.IndexOf(baseModifier);
            if (baseIndex > 0)
            {
                Modifiers.RemoveAt(baseIndex);
                Modifiers.Insert(0, baseModifier);
            }
        }

        EnsureGeometryInputStagesFollowBase();
    }

    private void EnsureGeometryInputStagesFollowBase()
    {
        if (Modifiers.Count <= 2)
            return;

        var ordered = new List<ModifierDefinition>(Modifiers.Count)
        {
            Modifiers[0]
        };

        for (int index = 1; index < Modifiers.Count; index++)
        {
            if (Modifiers[index] is GeometryInputModifierDefinition)
                ordered.Add(Modifiers[index]);
        }

        for (int index = 1; index < Modifiers.Count; index++)
        {
            if (Modifiers[index] is not GeometryInputModifierDefinition)
                ordered.Add(Modifiers[index]);
        }

        bool changed = ordered.Count != Modifiers.Count;
        if (!changed)
        {
            for (int index = 0; index < Modifiers.Count; index++)
            {
                if (ReferenceEquals(Modifiers[index], ordered[index]))
                    continue;

                changed = true;
                break;
            }
        }

        if (!changed)
            return;

        Modifiers.Clear();
        Modifiers.AddRange(ordered);
    }
}
