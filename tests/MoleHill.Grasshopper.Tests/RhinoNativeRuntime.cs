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

        string rhinoSystemDirectory = ResolveRhinoSystemDirectory();
        if (!Directory.Exists(rhinoSystemDirectory))
            return;

        SetDllDirectory(rhinoSystemDirectory);
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null || !path.Split(Path.PathSeparator).Contains(rhinoSystemDirectory, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", rhinoSystemDirectory + Path.PathSeparator + path);
    }

    /// <summary>
    /// Where the native Rhino assemblies live. Overridable so the native lane can run against a Rhino
    /// that is not at the default install path; matches the MSBuild <c>RhinoInstallDir</c> property.
    /// </summary>
    public static string ResolveRhinoSystemDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("MOLEHILL_RHINO_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? @"C:\Program Files\Rhino 8\System"
            : Path.Combine(configured, "System");
    }

    /// <summary>
    /// True when this run is a native lane: a missing runtime is a failure, not a skip. A green managed
    /// run must never be readable as native acceptance, so the lane says which it is rather than the
    /// report having to guess from a skip count.
    /// </summary>
    public static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_REQUIRE_NATIVE"), "1", StringComparison.Ordinal);

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
        // Under MOLEHILL_REQUIRE_NATIVE the attribute deliberately does not skip: the test runs, hits
        // the missing runtime, and fails. NativeRuntimePreflightTests names the cause once, up front.
        if (!RhinoNativeRuntime.IsAvailable && !RhinoNativeRuntime.IsRequired)
            Skip = RhinoNativeRuntime.UnavailableReason;
    }
}

/// <summary>
/// Preflight for the native lane. With <c>MOLEHILL_REQUIRE_NATIVE=1</c> a missing Rhino runtime fails
/// here with the reason, instead of the whole native suite quietly reporting as skipped.
/// </summary>
public class NativeRuntimePreflightTests
{
    [Fact]
    public void NativeRuntime_IsAvailable_WhenTheNativeLaneRequiresIt()
    {
        if (!RhinoNativeRuntime.IsRequired)
            return;

        Assert.True(
            RhinoNativeRuntime.IsAvailable,
            "MOLEHILL_REQUIRE_NATIVE=1 but " + RhinoNativeRuntime.UnavailableReason +
            " Searched " + RhinoNativeRuntime.ResolveRhinoSystemDirectory() +
            " (set MOLEHILL_RHINO_DIR to the Rhino install root to point elsewhere).");
    }
}
