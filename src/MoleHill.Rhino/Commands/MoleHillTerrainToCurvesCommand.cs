// Registers the command that recovers editable curves and spot points from a dense terrain.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillTerrainToCurvesCommand : Command
{
    public override string EnglishName => "mhTerrainToCurves";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        TerrainToCurvesCommandService.Run(doc);
}
