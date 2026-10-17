// Registers the sketch relaxation command: a drawn route relaxed live over the terrain, for Grade Path.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillRelaxRouteCommand : Command
{
    public override string EnglishName => "mhRelaxRoute";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        RouteRelaxCommandService.Run(doc);
}
