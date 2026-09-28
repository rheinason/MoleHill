using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

[CommandStyle(Style.ScriptRunner)]
public sealed class MoleHillUpdateAllLinkedBlocksCommand : Command
{
    public override string EnglishName => "mhUpdateAllLinkedBlocks";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return BlockCommandService.RunUpdateAllLinkedBlocks(doc);
    }
}
