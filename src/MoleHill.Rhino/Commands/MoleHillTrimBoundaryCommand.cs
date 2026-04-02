using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillTrimBoundaryCommand : Command
{
    public override string EnglishName => "MoleHillTrimBoundary";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunTrimBoundary(doc);
    }
}
