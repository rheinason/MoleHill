// Registers the mesh/surface curve draping command.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillDrapeCurveCommand : Command
{
    public override string EnglishName => "mhDrapeCurve";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return TerrainInputCommandService.RunDrapeCurve(doc);
    }
}
