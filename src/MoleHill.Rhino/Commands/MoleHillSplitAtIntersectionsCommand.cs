// Registers the selected-curve intersection splitting command.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillSplitAtIntersectionsCommand : Command
{
    public override string EnglishName => "mhSplitAtIntersections";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return TerrainInputCommandService.RunSplitAtIntersections(doc);
    }
}
