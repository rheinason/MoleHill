using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillOffset3dPolylineCommand : Command
{
    public override string EnglishName => "MoleHillOffset3dPolyline";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return GeometryCommandService.RunOffset3dPolyline(doc);
    }
}
