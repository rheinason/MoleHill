using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillRemoveMarkerParenthesesCommand : Command
{
    public override string EnglishName => "MoleHillRemoveMarkerParentheses";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return BlockCommandService.RunMutateMarkerParentheses(doc, add: false);
    }
}
