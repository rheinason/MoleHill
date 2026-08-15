// Registers the interactive parallel wall-rail creation command.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillCreateWallCommand : Command
{
    public override string EnglishName => "mhCreateWall";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return TerrainInputCommandService.RunCreateWall(doc);
    }
}
