using Grasshopper.Kernel;
using System.Drawing;

namespace TopoTIN.Grasshopper;

public class TopoTINInfo : GH_AssemblyInfo
{
    public override string Name => "TopoTIN";
    public override Bitmap? Icon => null;
    public override string Description => "TIN surface generation from points and breaklines using constrained Delaunay triangulation.";
    public override Guid Id => new("F7A1B2C3-D4E5-6789-ABCD-EF0123456789");
    public override string AuthorName => "TopoTIN";
    public override string AuthorContact => "";
    public override string Version => "0.1.0";
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;
}
