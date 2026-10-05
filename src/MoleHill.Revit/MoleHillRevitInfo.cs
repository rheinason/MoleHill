// Grasshopper library metadata for MoleHill.Revit.gha, and the shared icon loader its components use.
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;

namespace MoleHill.Revit;

public class MoleHillRevitInfo : GH_AssemblyInfo
{
    public override string Name => "MoleHill for Rhino.Inside.Revit";

    public override Bitmap? Icon => LoadIcon("WriteToposolids.png");

    public override string Description =>
        "Writes MoleHill terrains to Revit Toposolids. Loads only inside Rhino.Inside.Revit.";
    public override Guid Id => new("25054E81-C5F1-4E58-BA94-DE303A5B1779");
    public override string AuthorName => "rheinason";
    public override string AuthorContact => "https://github.com/rheinason/MoleHill";
    public override string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;

    internal static Bitmap? LoadIcon(string fileName)
    {
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MoleHill.Revit.Resources.{fileName}");
        return stream == null ? null : new Bitmap(stream);
    }
}
