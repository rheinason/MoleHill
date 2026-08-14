using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class ModelUnitGuard
{
    public const string RequiredMessage =
        "MoleHill requires model units. Set File > Properties > Units to a real length unit, then try again.";

    public static bool TryGet(RhinoDoc? doc, out ModelUnitContext context, bool report = true)
    {
        context = ModelUnitContext.FromDocument(doc);
        if (context.IsSupported)
            return true;

        if (report)
            RhinoApp.WriteLine($"[MoleHill] {RequiredMessage}");
        return false;
    }
}
