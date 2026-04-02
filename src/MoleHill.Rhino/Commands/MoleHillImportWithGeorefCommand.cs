using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillImportWithGeorefCommand : Command
{
    public override string EnglishName => "MoleHillImportWithGeoref";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunImportWithGeoref(doc);
    }
}
