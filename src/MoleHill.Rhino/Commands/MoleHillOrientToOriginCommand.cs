using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillOrientToOriginCommand : Command
{
    public override string EnglishName => "MoleHillOrientToOrigin";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunOrientToOrigin(doc);
    }
}
