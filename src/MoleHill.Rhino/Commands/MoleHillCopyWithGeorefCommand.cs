using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

/// <summary>Copies selected geometry at real-world coordinates for ordinary Rhino Paste.</summary>
[CommandStyle(Style.ScriptRunner)]
public sealed class MoleHillCopyWithGeorefCommand : Command
{
    public override string EnglishName => "mhCopyWithGeoref";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunCopyWithGeoref(doc);
    }
}
