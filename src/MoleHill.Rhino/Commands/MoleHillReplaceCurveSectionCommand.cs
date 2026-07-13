using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillReplaceCurveSectionCommand : Command
{
    public override string EnglishName => "mhReplaceCurveSection";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunReplaceCurveSection(doc);
    }
}
