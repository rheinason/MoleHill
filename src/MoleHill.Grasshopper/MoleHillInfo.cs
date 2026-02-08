using Grasshopper.Kernel;
using System.Drawing;
using System.Reflection;

namespace MoleHill.Grasshopper;

public class MoleHillInfo : GH_AssemblyInfo
{
    public override string Name => "MoleHill";

    public override Bitmap? Icon =>
        LoadIcon("MoleHill.Grasshopper.Resources.MoleHill.png");

    public override string Description =>
        "Terrain modeling toolkit: TIN surfaces, grading, slope analysis, mesh areas, and collage from points and breaklines.";
    public override Guid Id => new("F7A1B2C3-D4E5-6789-ABCD-EF0123456789");
    public override string AuthorName => "MoleHill";
    public override string AuthorContact => "";
    public override string Version => "0.3.0";
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;

    internal static Bitmap? LoadIcon(string resourceName)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        return new Bitmap(stream);
    }
}
