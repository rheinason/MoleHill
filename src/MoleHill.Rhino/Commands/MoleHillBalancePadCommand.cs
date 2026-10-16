// Registers the pad cut/fill balancing command.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillBalancePadCommand : Command
{
    public override string EnglishName => "mhBalancePad";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        BalancePadCommandService.Run(doc);
}
