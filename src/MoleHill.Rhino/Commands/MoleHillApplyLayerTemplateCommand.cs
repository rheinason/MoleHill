using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillApplyLayerTemplateCommand : Command
{
    public override string EnglishName => "MoleHillApplyLayerTemplate";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return LayerTemplateCommandService.RunApplyLayerTemplate(doc);
    }
}
