using Grasshopper.Kernel;
using System.Drawing;
using System.Reflection;

namespace TopoTIN.Grasshopper;

public class TopoTINInfo : GH_AssemblyInfo
{
    public override string Name => "TopoTIN";

    public override Bitmap? Icon =>
        LoadIcon("TopoTIN.Grasshopper.Resources.TopoTIN.png");

    public override string Description =>
        "Terrain modeling toolkit: TIN surfaces, grading, slope analysis, mesh areas, and collage from points and breaklines.";
    public override Guid Id => new("F7A1B2C3-D4E5-6789-ABCD-EF0123456789");
    public override string AuthorName => "TopoTIN";
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
