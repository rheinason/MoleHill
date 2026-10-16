// Registers the long-section extraction command.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillExtractProfileCommand : Command
{
    public override string EnglishName => "mhExtractProfile";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        LongSectionCommandService.RunExtract(doc);
}
