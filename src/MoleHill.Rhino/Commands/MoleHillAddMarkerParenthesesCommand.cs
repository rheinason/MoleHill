using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillAddMarkerParenthesesCommand : Command
{
    public override string EnglishName => "MoleHillAddMarkerParentheses";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return BlockCommandService.RunMutateMarkerParentheses(doc, add: true);
    }
}
