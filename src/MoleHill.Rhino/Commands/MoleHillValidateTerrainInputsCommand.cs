// Registers the selected terrain-input validation command.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillValidateTerrainInputsCommand : Command
{
    public override string EnglishName => "mhValidateTerrainInputs";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return TerrainInputCommandService.RunValidateTerrainInputs(doc);
    }
}
