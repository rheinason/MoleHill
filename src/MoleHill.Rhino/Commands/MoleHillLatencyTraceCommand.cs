using System.IO;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Commands;

/// <summary>
/// Controls the edit-to-visible latency trace: turn recording on, clear it, print the reconstructed
/// timelines, or save the raw events as CSV. The trace is off by default and costs one volatile read
/// per recording site when off.
/// </summary>
public sealed class MoleHillLatencyTraceCommand : Command
{
    public override string EnglishName => "mhLatencyTrace";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetOption();
        getter.SetCommandPrompt($"Latency trace is {(TerrainLatencyTrace.IsEnabled ? "on" : "off")}. Choose an action");
        int on = getter.AddOption("On");
        int off = getter.AddOption("Off");
        int clear = getter.AddOption("Clear");
        int report = getter.AddOption("Report");
        int save = getter.AddOption("Save");

        if (getter.Get() != global::Rhino.Input.GetResult.Option)
            return Result.Cancel;

        int selected = getter.Option()!.Index;
        if (selected == on)
        {
            TerrainLatencyTrace.IsEnabled = true;
            TerrainLatencyTrace.Clear();
            RhinoApp.WriteLine("[MoleHill] Latency trace on; buffer cleared. Edit a terrain, then run mhLatencyTrace > Report.");
        }
        else if (selected == off)
        {
            TerrainLatencyTrace.IsEnabled = false;
            RhinoApp.WriteLine("[MoleHill] Latency trace off. Recorded events are kept until Clear.");
        }
        else if (selected == clear)
        {
            TerrainLatencyTrace.Clear();
            RhinoApp.WriteLine("[MoleHill] Latency trace buffer cleared.");
        }
        else if (selected == report)
        {
            RhinoApp.WriteLine(TerrainLatencyReport.Format(TerrainLatencyTrace.Snapshot()));
        }
        else if (selected == save)
        {
            string directory = Path.Combine(Path.GetTempPath(), "MoleHillLatency");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var events = TerrainLatencyTrace.Snapshot();
            string csvPath = Path.Combine(directory, $"latency-{stamp}.csv");
            string textPath = Path.Combine(directory, $"latency-{stamp}.txt");
            File.WriteAllText(csvPath, TerrainLatencyReport.FormatCsv(events));
            File.WriteAllText(textPath, TerrainLatencyReport.Format(events));
            RhinoApp.WriteLine($"[MoleHill] Latency trace saved: {csvPath}");
            RhinoApp.WriteLine($"[MoleHill] Latency report saved: {textPath}");
        }

        return Result.Success;
    }
}
