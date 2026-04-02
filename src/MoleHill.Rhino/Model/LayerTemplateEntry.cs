namespace MoleHill.Rhino.Model;

public sealed class LayerTemplateEntry
{
    public string Path { get; set; } = string.Empty;

    public int ColorArgb { get; set; } = unchecked((int)0xFF000000);

    public int PrintColorArgb { get; set; } = unchecked((int)0xFF000000);

    public double PlotWeight { get; set; } = 0.0;
}
