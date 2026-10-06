using System.Diagnostics;
using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The tiled Remesh at park scale, cold and after a one-vertex edit, on a Remesh input exported by the park
/// probe (<c>ExportRemeshInputFolder</c>; validation-lanes.md, "park-stress"). Asserts that the incremental
/// result is the cold result of the edit, bit for bit, and reports what each costs. Skips unless
/// <c>MOLEHILL_REMESH_INPUT</c> names an exported file.
/// </summary>
public class TiledRemeshReplayBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Replay_OneVertexEdit_IncrementalEqualsColdAndReportsCost()
    {
        string? path = Environment.GetEnvironmentVariable("MOLEHILL_REMESH_INPUT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("Skipped: set MOLEHILL_REMESH_INPUT to a park-probe remesh-input file.");
            return;
        }

        Load(path, out double[] vertices, out int[] faces, out List<ConstraintPolyline> constraints);
        double target = TiledIsotropicRemesher.RoundedTarget(IsotropicRemesher.EstimateFaceCountPreservingTarget(vertices, faces));
        var options = new IsotropicRemesher.Options { TargetEdgeLength = target, CreaseAngleDeg = 30, Tolerance = 0.001, WallFaceMinSlopeDeg = 70, Iterations = 3 };

        var timer = Stopwatch.StartNew();
        TiledIsotropicRemesher.Remesh(vertices, faces, constraints, options, 0.0, previous: null, out TiledIsotropicRemesher.TiledRemeshMemo memo);
        double coldMs = timer.Elapsed.TotalMilliseconds;

        var edited = (double[])vertices.Clone();
        edited[(vertices.Length / 6 * 3) + 2] += 0.05;
        timer.Restart();
        IsotropicRemesher.Result incremental = TiledIsotropicRemesher.Remesh(edited, faces, constraints, options, 0.0, memo, out TiledIsotropicRemesher.TiledRemeshMemo after);
        double incrementalMs = timer.Elapsed.TotalMilliseconds;
        IsotropicRemesher.Result cold = TiledIsotropicRemesher.Remesh(edited, faces, constraints, options);

        output.WriteLine($"{faces.Length / 3:N0} faces, target {target}: cold {coldMs:N0} ms, one-vertex edit {incrementalMs:N0} ms ({after.ReusedTiles:N0} tiles reused, {after.RemeshedTiles:N0} remeshed)");
        output.WriteLine($"incremental: {incremental.Timing}");
        Assert.Equal(cold.Faces, incremental.Faces);
        Assert.Equal(cold.Vertices, incremental.Vertices);
    }

    private static void Load(string path, out double[] vertices, out int[] faces, out List<ConstraintPolyline> constraints)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        vertices = new double[reader.ReadInt32() * 3];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = reader.ReadDouble();
        faces = new int[reader.ReadInt32() * 3];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = reader.ReadInt32();
        int constraintCount = reader.ReadInt32();
        constraints = new List<ConstraintPolyline>(constraintCount);
        for (int c = 0; c < constraintCount; c++)
        {
            int n = reader.ReadInt32();
            bool closed = reader.ReadBoolean();
            bool preserve = reader.ReadBoolean();
            var points = new double[n * 3];
            for (int i = 0; i < points.Length; i++)
                points[i] = reader.ReadDouble();
            constraints.Add(new ConstraintPolyline(points, n, closed, preserve));
        }
    }
}
