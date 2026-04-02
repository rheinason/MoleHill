namespace MoleHill.Rhino.Model;

public sealed class LayerTemplateDefinition
{
    public string Name { get; set; } = "Template";

    public List<LayerTemplateEntry> Entries { get; set; } = new();
}
