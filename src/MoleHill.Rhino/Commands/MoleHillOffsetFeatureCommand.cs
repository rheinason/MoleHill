using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillOffsetFeatureCommand : Command
{
    public override string EnglishName => "mhOffsetFeature";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunOffsetFeature(doc);
    }
}
