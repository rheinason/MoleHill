using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

/// <summary>Writes the selected terrain's measured quantities to a CSV file.</summary>
public sealed class MoleHillExportTerrainReportCommand : Command
{
    public override string EnglishName => "mhExportTerrainReport";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => DocumentCommandService.RunExportTerrainReport(doc);
}
