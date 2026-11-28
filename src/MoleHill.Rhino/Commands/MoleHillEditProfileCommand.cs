// Registers mhEditProfile: draw a curve's long section to edit with Rhino's tools, then accept it.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillEditProfileCommand : Command
{
    public override string EnglishName => "mhEditProfile";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        ProfileModeService.Run(doc);
}
