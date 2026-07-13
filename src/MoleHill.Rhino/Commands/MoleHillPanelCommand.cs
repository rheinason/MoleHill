using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillPanelCommand : Command
{
    public override string EnglishName => "mhPanel";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(typeof(MoleHillPanel));
        return Result.Success;
    }
}
