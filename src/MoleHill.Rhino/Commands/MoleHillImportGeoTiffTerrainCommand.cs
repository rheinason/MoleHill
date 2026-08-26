using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillImportGeoTiffTerrainCommand : Command
{
    public override string EnglishName => "mhImportGeoTiffTerrain";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => DocumentCommandService.RunImportGeoTiffTerrain(doc);
}
