using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillApplySavedGeorefCommand : Command
{
    public override string EnglishName => "MoleHillApplySavedGeoref";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunApplySavedGeoref(doc, removeGeoref: false);
    }
}
