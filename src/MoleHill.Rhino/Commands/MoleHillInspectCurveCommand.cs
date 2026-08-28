using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillInspectCurveCommand : Command
{
    public override string EnglishName => "mhInspectCurve";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        CurveReviewService.Start(doc);
}
