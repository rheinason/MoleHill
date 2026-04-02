using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillTwoPointInterpolationCommand : Command
{
    public override string EnglishName => "MoleHillTwoPointInterpolation";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunTwoPointInterpolation(doc);
    }
}
