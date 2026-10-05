using Grasshopper.Kernel;

namespace MoleHill.Revit;

/// <summary>
/// Keeps this assembly out of every Grasshopper that is not running inside Revit. The Yak package ships it
/// beside <c>MoleHill.gha</c> for everyone, and Grasshopper loads every <c>.gha</c> it finds; aborting
/// here is what keeps the Revit components off the ribbon of a plain Rhino, where they could only fail.
/// Rhino.Inside.Revit loads Rhino, and therefore Grasshopper, into Revit's process, so <c>RevitAPI</c> is
/// already loaded by the time this runs.
/// </summary>
public sealed class MoleHillRevitPriority : GH_AssemblyPriority
{
    /// <summary>Set to <c>1</c> to load the components anyway — for checking them in a plain Rhino.</summary>
    public const string ForceLoadVariable = "MOLEHILL_LOAD_REVIT_COMPONENTS";

    public override GH_LoadingInstruction PriorityLoad() =>
        IsRevitHosted() || Environment.GetEnvironmentVariable(ForceLoadVariable) == "1"
            ? GH_LoadingInstruction.Proceed
            : GH_LoadingInstruction.Abort;

    internal static bool IsRevitHosted() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => string.Equals(assembly.GetName().Name, "RevitAPI", StringComparison.OrdinalIgnoreCase));
}
