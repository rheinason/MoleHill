using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

/// <summary>
/// Re-applies a layer template's appearance to layers that already exist, for resetting a document
/// that has drifted from the office standard. Separate from mhApplyLayerTemplate because it throws
/// away the user's own Layers-panel edits, which applying a template must never do on its own.
/// </summary>
public sealed class MoleHillResetLayerStylesCommand : Command
{
    public override string EnglishName => "mhResetLayerStyles";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return LayerTemplateCommandService.RunResetLayerStyles(doc);
    }
}
