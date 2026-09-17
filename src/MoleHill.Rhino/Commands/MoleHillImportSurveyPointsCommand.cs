// Registers the coded survey point file importer.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillImportSurveyPointsCommand : Command
{
    public override string EnglishName => "mhImportSurveyPoints";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        SurveyImportCommandService.RunImportSurveyPoints(doc);
}
