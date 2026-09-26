using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// One sample of a hosted-performance scenario: metric name to milliseconds. Metric names are
/// <c>{scenario}/{phase}/{stage}</c>, so a baseline can be compared scenario by scenario and a stage keeps
/// its identity across runs. A stage that reports twice in one build is summed, which is what its cost is.
/// </summary>
public sealed class PerfSampleRecorder
{
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The stage's own detail line (output counts, cache hits). Not compared - it is how a reader tells
    /// whether a changed time came with changed work, such as a fixture now producing more ponds.
    /// </summary>
    public Dictionary<string, string> Details { get; } = new(StringComparer.Ordinal);

    public void Record(string metric, double milliseconds)
    {
        Values[metric] = Values.TryGetValue(metric, out double existing)
            ? existing + milliseconds
            : milliseconds;
    }

    /// <summary>
    /// The wall time the caller measured around <c>TerrainBuildService.Build</c> plus every stage row
    /// the build reported, including per-analysis rows such as <c>Analysis Ponding</c>.
    /// </summary>
    internal void RecordBuild(string prefix, TerrainBuildResult result, TimeSpan wall)
    {
        Record($"{prefix}/wall", wall.TotalMilliseconds);
        foreach (TerrainBuildTiming timing in result.Timings)
        {
            Record($"{prefix}/{timing.Stage}", timing.Elapsed.TotalMilliseconds);
            if (!string.IsNullOrEmpty(timing.Detail))
                Details[$"{prefix}/{timing.Stage}"] = timing.Detail;
        }

        Details[$"{prefix}/{OutputDetail}"] = DescribeOutput(result.PrimaryMesh);
    }

    /// <summary>The detail key holding a hash of a build's finished mesh.</summary>
    public const string OutputDetail = "output mesh";

    /// <summary>
    /// Counts plus a hash of the mesh's own vertex and face lists, read from Rhino rather than from
    /// any cached extraction, so a speed change that alters the terrain shows up as a changed hash even
    /// when the change is in the caching itself.
    /// </summary>
    private static string DescribeOutput(global::Rhino.Geometry.Mesh? mesh)
    {
        if (mesh == null)
            return "none";

        float[] vertices = mesh.Vertices.ToFloatArray();
        int[] faces = mesh.Faces.ToIntArray(false);
        var hash = new FingerprintBuilder();
        hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices.AsSpan()));
        hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(faces.AsSpan()));
        return $"{mesh.Vertices.Count} verts, {mesh.Faces.Count} faces, {hash.ToUInt64():x16}";
    }
}
