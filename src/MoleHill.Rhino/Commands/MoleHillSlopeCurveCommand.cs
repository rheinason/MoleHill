using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillSlopeCurveCommand : Command
{
    public override string EnglishName => "MoleHillSlopeCurve";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunSlopeCurve(doc);
    }
}
