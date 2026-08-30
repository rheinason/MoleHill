namespace MoleHill.Rhino.Model;

public sealed class LayerTemplateDefinition
{
    /// <summary>
    /// Schema of this template. 0 is a pre-role file, which <c>LayerTemplateStore</c> upgrades on
    /// load by binding roles from the layer paths it already has. The containing JSON stays a plain
    /// array so an older build can still read a newer file and simply ignore what it does not know.
    /// </summary>
    public int Version { get; set; }

    public string Name { get; set; } = "Template";

    public List<LayerTemplateEntry> Entries { get; set; } = new();

    /// <summary>A deep copy, entries included.</summary>
    public LayerTemplateDefinition Copy() => new()
    {
        Version = Version,
        Name = Name,
        Entries = Entries.Select(entry => entry.Copy()).ToList()
    };
}
