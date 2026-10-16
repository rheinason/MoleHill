// Registers the section variant of curve draping (the Drape Curve button's right-click).
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillDrapeCurveSectionCommand : Command
{
    public override string EnglishName => "mhDrapeCurveSection";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        DrapeCommandService.RunDrapeCurveSection(doc);
}
