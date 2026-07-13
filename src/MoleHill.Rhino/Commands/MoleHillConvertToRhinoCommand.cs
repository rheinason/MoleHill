using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillConvertToRhinoCommand : Command
{
    public override string EnglishName => "mhConvertToRhino";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var controller = TerrainController.Instance;
        var terrain = controller.GetSelectedTerrain(doc);
        if (terrain == null)
            return Result.Nothing;

        controller.ConvertToRhino(doc, terrain.TerrainId);
        return Result.Success;
    }
}
