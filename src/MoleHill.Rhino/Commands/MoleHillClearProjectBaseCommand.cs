using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillClearProjectBaseCommand : Command
{
    public override string EnglishName => "mhClearProjectBase";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunClearProjectBase(doc);
    }
}
