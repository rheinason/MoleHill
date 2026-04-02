using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillLiftCurvesWithLineCommand : Command
{
    public override string EnglishName => "MoleHillLiftCurvesWithLine";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunLiftCurvesWithLine(doc);
    }
}
