using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillSetSunNorthCommand : Command
{
    public override string EnglishName => "mhSetSunNorth";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return BlockCommandService.RunSetSunNorth(doc);
    }
}
