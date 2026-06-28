namespace MoleHill.Rhino.Model;

public sealed class CollageZoneDefinition
{
    public Guid ZoneId { get; set; } = Guid.NewGuid();

    public bool IsEnabled { get; set; } = true;

    public string Name { get; set; } = "Zone";

    public SourceReferenceSet Boundaries { get; set; } = new();

    public int ColorArgb { get; set; } = unchecked((int)0xFF78B464);

    /// <summary>When true, <see cref="ColorArgb"/> overrides the source-layer color for this zone's
    /// output. When false (the default) the zone is coloured by its source layer.</summary>
    public bool UseColorOverride { get; set; }

    public string? LayerName { get; set; }

    public string? MaterialName { get; set; }

    public bool UseInputElevationForPriority { get; set; } = true;

    public bool SplitToSeparateMesh { get; set; } = true;
}
