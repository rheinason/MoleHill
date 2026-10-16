using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

/// <summary>Merge selected curve control point locations with a cancellable preview.</summary>
public sealed class MoleHillMergeControlPointsCommand : Command
{
    public override string EnglishName => "mhMergeControlPoints";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => MergeControlPointsCommandService.Run(doc);
}
