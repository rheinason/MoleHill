using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

/// <summary>Set selected curve control point elevations from a reference point and signed slope.</summary>
public sealed class MoleHillSlopeControlPointsCommand : Command
{
    public override string EnglishName => "mhSlopeControlPoints";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        GeometryCommandService.RunSlopeControlPoints(doc);
}
