// Registers the command that turns a drone scan's ground into contours and spot heights.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillScanToTerrainCommand : Command
{
    public override string EnglishName => "mhScanToTerrain";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        ScanToTerrainCommandService.Run(doc);
}
