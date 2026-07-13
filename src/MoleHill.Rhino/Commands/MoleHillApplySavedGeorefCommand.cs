using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillApplySavedGeorefCommand : Command
{
    public override string EnglishName => "mhConvertToRealWorldCoordinates";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunApplySavedGeoref(doc, toProjectCoordinates: false);
    }
}
