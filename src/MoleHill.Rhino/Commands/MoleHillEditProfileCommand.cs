// Registers mhEditProfile: edit a curve's long section live, beside the curve.
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

public sealed class MoleHillEditProfileCommand : Command
{
    public override string EnglishName => "mhEditProfile";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        ProfileEditCommandService.Run(doc);
}
