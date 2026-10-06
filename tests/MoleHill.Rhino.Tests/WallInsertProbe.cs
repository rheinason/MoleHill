using System.Diagnostics;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Where a warm retaining-wall edit spends its time on the interactive plan-target fixture (~100k faces):
/// the stage timings and the rail-insertion phase diagnostics of repeated rail raises, written as text.
/// Run inside Rhino through <c>tools/rhino-hosted-perf.py --entry WallInsertProbe --print-script</c>; the
/// request path is the text file to write, optionally followed by <c>|</c> and the number of edits.
/// </summary>
public static class WallInsertProbe
{
    public static void Start(string request)
    {
        string[] parts = request.Split('|');
        string outputPath = parts[0];
        int edits = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 8;
        var lines = new List<string> { $"Power throttling disabled: {HostedPowerThrottling.OptOut()}" };
        try
        {
            CheckFillMesh(lines.Add);
            Run(lines.Add, side: 224, edits);
        }
        catch (Exception ex)
        {
            lines.Add("FAILED: " + ex);
        }

        File.WriteAllLines(outputPath, lines);
    }

    public static void Run(Action<string> write, int side, int edits)
    {
        InteractiveScaleBenchmark.Fixture fixture = InteractiveScaleBenchmark.CreateFixture(side);
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();
        var timer = Stopwatch.StartNew();
        TerrainBuildResult cold = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        write($"cold {timer.Elapsed.TotalMilliseconds:N1} ms, {cold.PrimaryMesh?.Faces.Count:N0} faces");

        for (int i = 0; i < edits; i++)
        {
            InteractiveScaleBenchmark.RaiseWall(fixture, 0.15);
            timer.Restart();
            TerrainBuildResult edited = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
            double ms = timer.Elapsed.TotalMilliseconds;
            write($"--- edit {i + 1}: {ms:N1} ms, {edited.PrimaryMesh?.Faces.Count:N0} faces");
            foreach (var timing in edited.Timings.Where(t => t.Elapsed.TotalMilliseconds >= 0.5))
                write($"  {timing.Stage}: {timing.Elapsed.TotalMilliseconds:N2} ms {timing.Detail}");
            foreach (string line in edited.Diagnostics.Where(line => line.Contains("Retaining Wall", StringComparison.Ordinal)))
                write("  # " + line);
        }
    }

    /// <summary>
    /// <c>RhinoGeometryConversions.BuildMesh</c> writes its arrays through a pointer; this checks the result
    /// against the same arrays added one call at a time.
    /// </summary>
    private static void CheckFillMesh(Action<string> write)
    {
        InteractiveScaleBenchmark.Fixture fixture = InteractiveScaleBenchmark.CreateFixture(224);
        TerrainBuildResult built = new TerrainBuildService().Build(fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final);
        ExtractedMeshData data = RhinoGeometryConversions.GetNormalizedMeshData(built.PrimaryMesh!);
        // Unrounded input, so the double-precision copy is actually exercised.
        double[] input = (double[])data.Vertices.Clone();
        for (int i = 0; i < input.Length; i++)
            input[i] += 1e-9 * ((i % 7) - 3);
        global::Rhino.Geometry.Mesh fast = new();
        var fastTimes = new List<double>();
        var slowTimes = new List<double>();
        global::Rhino.Geometry.Mesh slow = new();
        var timer = new Stopwatch();
        for (int round = 0; round < 9; round++)
        {
            timer.Restart();
            fast = new global::Rhino.Geometry.Mesh();
            FillForProbe(fast, input, data, perCall: false);
            fastTimes.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            slow = new global::Rhino.Geometry.Mesh();
            FillForProbe(slow, input, data, perCall: true);
            slowTimes.Add(timer.Elapsed.TotalMilliseconds);
        }

        slow.Normals.ComputeNormals();
        fast.Normals.ComputeNormals();
        fastTimes.Sort();
        slowTimes.Sort();
        double fastMs = fastTimes[4], slowMs = slowTimes[4];

        bool same = fast.Vertices.Count == slow.Vertices.Count && fast.Faces.Count == slow.Faces.Count;
        string firstDifference = same ? string.Empty : "counts";
        for (int i = 0; same && i < fast.Vertices.Count; i++)
        {
            same = fast.Vertices[i] == slow.Vertices[i] && fast.Vertices.Point3dAt(i) == slow.Vertices.Point3dAt(i) && fast.Normals[i] == slow.Normals[i];
            if (!same)
                firstDifference = $"vertex {i}: {fast.Vertices.Point3dAt(i)} n {fast.Normals[i]} vs {slow.Vertices.Point3dAt(i)} n {slow.Normals[i]}";
        }

        for (int i = 0; same && i < fast.Faces.Count; i++)
        {
            same = fast.Faces[i].Equals(slow.Faces[i]);
            if (!same)
                firstDifference = $"face {i}";
        }

        if (!same)
            write("FillMesh first difference: " + firstDifference);
        write($"FillMesh check: identical {same}; valid {fast.IsValid}/{slow.IsValid}; bbox {fast.GetBoundingBox(false)} vs {slow.GetBoundingBox(false)}; " +
              $"double precision {fast.Vertices.UseDoublePrecisionVertices}/{slow.Vertices.UseDoublePrecisionVertices}; fill median {fastMs:N2} ms vs per-call {slowMs:N2} ms");
    }

    private static void FillForProbe(global::Rhino.Geometry.Mesh mesh, double[] input, ExtractedMeshData data, bool perCall)
    {
        if (!perCall)
        {
            typeof(RhinoGeometryConversions)
                .GetMethod("FillMesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { mesh, input, data.VertexCount, data.Faces, data.FaceCount });
            return;
        }

        for (int i = 0; i < data.VertexCount; i++)
            mesh.Vertices.Add(input[i * 3], input[i * 3 + 1], input[i * 3 + 2]);
        for (int i = 0; i < data.FaceCount; i++)
            mesh.Faces.AddFace(data.Faces[i * 3], data.Faces[i * 3 + 1], data.Faces[i * 3 + 2]);
    }
}
