using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

internal static class RhinoNativeRuntime
{
    private static readonly Lazy<(bool IsAvailable, string? Reason)> Availability = new(CheckAvailability);

    public static bool IsAvailable => Availability.Value.IsAvailable;

    public static string UnavailableReason => Availability.Value.Reason ?? "Rhino native runtime is unavailable in this test host.";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        const string rhinoSystemDirectory = @"C:\Program Files\Rhino 8\System";
        if (!Directory.Exists(rhinoSystemDirectory))
            return;

        SetDllDirectory(rhinoSystemDirectory);
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null || !path.Split(Path.PathSeparator).Contains(rhinoSystemDirectory, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", rhinoSystemDirectory + Path.PathSeparator + path);
    }

    private static (bool IsAvailable, string? Reason) CheckAvailability()
    {
        try
        {
            using var curve = new LineCurve(Point3d.Origin, new Point3d(1.0, 0.0, 0.0));
            return (true, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            return (false, "Rhino native runtime is unavailable in this test host: " + ex.Message);
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);
}

public sealed class RhinoNativeFactAttribute : FactAttribute
{
    public RhinoNativeFactAttribute()
    {
        if (!RhinoNativeRuntime.IsAvailable)
            Skip = RhinoNativeRuntime.UnavailableReason;
    }
}
