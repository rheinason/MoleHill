using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Commands;

/// <summary>Starts the background 247k-point terrain pipeline diagnostic.</summary>
public sealed class MoleHillBenchmarkLargeTinCommand : Command
{
    private static int _isRunning;

    public override string EnglishName => "mhBenchmarkLargeTin";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            RhinoApp.WriteLine("MoleHill large-TIN diagnostic is already running.");
            return Result.Nothing;
        }

        RhinoApp.WriteLine(
            $"[MoleHill benchmark] Starting deterministic {LargeTinDiagnostic.DefaultPointCount:N0}-point, " +
            $"{LargeTinDiagnostic.DefaultWidth / 1000.0:0} x {LargeTinDiagnostic.DefaultHeight / 1000.0:0} km diagnostic in the background.");

        _ = Task.Run(() =>
        {
            try
            {
                LargeTinDiagnostic.Run(WriteReport);
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);
            }
        });

        return Result.Success;
    }

    private static void WriteReport(string message)
    {
        RhinoApp.InvokeOnUiThread((Action)(() => RhinoApp.WriteLine($"[MoleHill benchmark] {message}")));
    }
}
