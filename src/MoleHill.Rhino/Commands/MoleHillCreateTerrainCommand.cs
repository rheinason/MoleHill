using MoleHill.Rhino.Services;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillCreateTerrainCommand : Command
{
    public override string EnglishName => "MoleHillCreateTerrain";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        TerrainController.Instance.CreateTerrain(doc, seedFromSelection: true);
        Panels.OpenPanel(typeof(MoleHillPanel));
        return Result.Success;
    }
}
