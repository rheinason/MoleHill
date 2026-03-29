using Eto.Forms;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillResetTerrainBuildCommand : global::Rhino.Commands.Command
{
    public override string EnglishName => "MoleHillResetTerrainBuild";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var controller = TerrainController.Instance;
        var terrain = controller.GetSelectedTerrain(doc);
        if (terrain == null)
            return Result.Nothing;

        var result = MessageBox.Show(
            RhinoEtoApp.MainWindowForDocument(doc),
            "Force reset clears queued rebuilds and cancels the running build for the selected terrain. In-flight preview state will be discarded.",
            "Force Reset Build",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
        if (result != DialogResult.Yes)
            return Result.Cancel;

        controller.ForceResetTerrainBuild(doc, terrain.TerrainId);
        return Result.Success;
    }
}
