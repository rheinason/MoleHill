using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillSoftEditCurvesCommand : Command
{
    public override string EnglishName => "MoleHillSoftEditCurves";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunSoftEditCurves(doc);
    }
}
