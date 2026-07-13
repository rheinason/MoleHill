using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillRemoveSavedGeorefCommand : Command
{
    public override string EnglishName => "mhConvertToProjectCoordinates";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunApplySavedGeoref(doc, toProjectCoordinates: true);
    }
}
