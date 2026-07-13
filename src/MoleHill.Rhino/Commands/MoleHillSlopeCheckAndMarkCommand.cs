using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillSlopeCheckAndMarkCommand : Command
{
    public override string EnglishName => "mhSlopeCheckAndMark";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunSlopeCheckAndMark(doc);
    }
}
