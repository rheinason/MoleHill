using System.Reflection;

namespace MoleHill.Rhino.UI;

internal static class PanelIcons
{
    internal static Eto.Drawing.Image? Load(string name)
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"MoleHill.Rhino.Resources.{name}.png");
        return stream != null ? new Eto.Drawing.Bitmap(stream) : null;
    }
}
