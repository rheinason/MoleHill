// Registers the field code table editor, the right-click variant of mhImportSurveyPoints.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillEditFieldCodesCommand : Command
{
    public override string EnglishName => "mhEditFieldCodes";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        SurveyImportCommandService.RunEditFieldCodes(doc);
}
