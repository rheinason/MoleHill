// The only entry points the components call into Revit through. Signatures use no Revit type, so a component
// class can be loaded (and fail politely) in a Grasshopper that has no RevitAPI.
using Autodesk.Revit.DB;
using MoleHill.Revit.Planning;

namespace MoleHill.Revit.RevitHost;

internal sealed class WriteOutcome
{
    public List<object> Toposolids { get; } = new();
    public List<List<object>> Subdivisions { get; } = new();
    public List<string> Report { get; } = new();
    public List<string> Errors { get; } = new();
}

internal static class RevitSession
{
    public static WriteOutcome Write(
        bool run,
        IReadOnlyList<PreparationInput> inputs,
        object? toposolidType,
        object? level,
        bool writeSubdivisions,
        object? subdivisionType,
        IReadOnlyList<string> preserveParameters)
    {
        var outcome = new WriteOutcome();
        Document? document = RevitInputs.ActiveDocument()
            ?? RevitInputs.AsElement(toposolidType, null)?.Document
            ?? RevitInputs.AsElement(level, null)?.Document;
        if (document == null)
        {
            outcome.Errors.Add("No active Revit document. This component runs inside Rhino.Inside.Revit.");
            return outcome;
        }

        if (!ToposolidWritePlanner.TryPlan(inputs, document.Application.ShortCurveTolerance, out var plans, out var errors))
        {
            outcome.Errors.AddRange(errors);
            return outcome;
        }

        ElementId toposolidTypeId = RevitInputs.ResolveTyped<ToposolidType>(toposolidType, document, "Toposolid Type", "type", outcome.Errors);
        ElementId levelId = RevitInputs.ResolveTyped<Level>(level, document, "Level", "level", outcome.Errors);
        ElementId subdivisionTypeId = RevitInputs.ResolveTyped<ToposolidType>(subdivisionType, document, "Subdivision Type", "type", outcome.Errors);
        if (outcome.Errors.Count > 0)
            return outcome;

        if (!run)
        {
            outcome.Report.Add($"{plans.Count} preparation(s) ready for '{document.Title}'. Set Run to write them.");
            return outcome;
        }

        ToposolidWriteResult result = ToposolidWriter.Write(new ToposolidWriteRequest
        {
            Document = document,
            Plans = plans,
            ToposolidTypeId = toposolidTypeId,
            LevelId = levelId,
            WriteSubdivisions = writeSubdivisions,
            SubdivisionTypeId = subdivisionTypeId,
            PreserveParameters = preserveParameters
        });

        outcome.Toposolids.AddRange(result.Toposolids);
        outcome.Subdivisions.AddRange(result.Subdivisions.Select(list => list.Cast<object>().ToList()));
        outcome.Report.AddRange(result.Report);
        return outcome;
    }

    public static InspectedToposolid? Inspect(object? input, double metersPerOutputUnit) =>
        ToposolidInspector.Inspect(input, metersPerOutputUnit);
}
