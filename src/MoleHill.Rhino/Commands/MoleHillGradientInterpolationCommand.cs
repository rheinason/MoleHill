using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillGradientInterpolationCommand : Command
{
    public override string EnglishName => "MoleHillGradientInterpolation";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunGradientInterpolation(doc);
    }
}
