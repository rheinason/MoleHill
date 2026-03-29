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
        "Optional Grasshopper components for the MoleHill Rhino terrain workflow.";
    public override Guid Id => new("F7A1B2C3-D4E5-6789-ABCD-EF0123456789");
    public override string AuthorName => "rheinason";
    public override string AuthorContact => "https://github.com/rheinason/MoleHill";
    public override string Version => GetVersion();
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;

    internal static Bitmap? LoadIcon(string resourceName)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        return new Bitmap(stream);
    }

    private static string GetVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparator = informationalVersion.IndexOf('+');
            return metadataSeparator >= 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    }
}
