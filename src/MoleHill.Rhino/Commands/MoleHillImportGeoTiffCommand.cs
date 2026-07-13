using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillImportGeoTiffCommand : Command
{
    public override string EnglishName => "mhImportGeoTiff";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return DocumentCommandService.RunImportGeoTiff(doc);
    }
}
