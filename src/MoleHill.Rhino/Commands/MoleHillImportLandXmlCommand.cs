using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillImportLandXmlCommand : Command
{
    public override string EnglishName => "mhImportLandXml";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => DocumentCommandService.RunImportLandXml(doc);
}
