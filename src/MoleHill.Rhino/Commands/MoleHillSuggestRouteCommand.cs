// Registers the route suggestion command: alternative curves between picked points, for Grade Path.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillSuggestRouteCommand : Command
{
    public override string EnglishName => "mhSuggestRoute";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        RouteSuggestionCommandService.Run(doc);
}
