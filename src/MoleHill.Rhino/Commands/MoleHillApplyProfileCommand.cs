// Registers the command that maps an edited long section back onto its plan curve.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillApplyProfileCommand : Command
{
    public override string EnglishName => "mhApplyProfile";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        LongSectionCommandService.RunApply(doc);
}
