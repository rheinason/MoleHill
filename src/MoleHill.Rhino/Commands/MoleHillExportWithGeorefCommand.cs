using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillExportWithGeorefCommand : Command
{
    public override string EnglishName => "mhExportWithGeoref";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunExportWithGeoref(doc);
    }
}
