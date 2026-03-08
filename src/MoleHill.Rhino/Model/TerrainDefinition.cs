namespace MoleHill.Rhino.Model;

public sealed class TerrainDefinition
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid TerrainId { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Terrain";

    public bool LiveUpdateEnabled { get; set; } = true;

    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; } = false;

    public bool ShowTerrainMesh { get; set; } = true;

    public bool ShowZoneMeshes { get; set; } = true;

    public string? TerrainLayerPath { get; set; }

    public string? AuxiliaryLayerPath { get; set; }

    public SourceReferenceSet EarthworkReference { get; set; } = new();

    public SourceReferenceSet EarthworkBoundary { get; set; } = new();

    public List<ModifierDefinition> Modifiers { get; set; } = new();

    public List<MarkerDefinition> Markers { get; set; } = new();

    public List<CollageZoneDefinition> Zones { get; set; } = new();

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
        if (Modifiers.OfType<TriangulateModifierDefinition>().Any())
            return;

        Modifiers.Insert(0, new TriangulateModifierDefinition());
    }
}
