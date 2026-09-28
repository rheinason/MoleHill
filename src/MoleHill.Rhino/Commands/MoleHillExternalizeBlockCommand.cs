using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

[CommandStyle(Style.ScriptRunner)]
public sealed class MoleHillExternalizeBlockCommand : Command
{
    public override string EnglishName => "mhExternalizeBlock";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return BlockCommandService.RunExternalizeBlock(doc);
    }
}
