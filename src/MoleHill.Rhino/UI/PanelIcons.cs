using System.Reflection;

namespace MoleHill.Rhino.UI;

internal static class PanelIcons
{
    private static readonly Dictionary<string, Eto.Drawing.Image?> Cache = new(StringComparer.Ordinal);

    internal static Eto.Drawing.Image? Load(string name)
    {
        if (Cache.TryGetValue(name, out var cached))
            return cached;

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"MoleHill.Rhino.Resources.{name}.png");
        var image = stream != null ? new Eto.Drawing.Bitmap(stream) : null;
        Cache[name] = image;
        return image;
    }
}
