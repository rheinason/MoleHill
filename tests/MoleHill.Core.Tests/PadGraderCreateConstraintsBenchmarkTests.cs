using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The whole of <see cref="PadGrader.CreateConstraints"/> at the scale of the 2026-09-19 geometry-heavy
/// trace, which measured it at 1.28 s — of which 1.00 s was the
/// <see cref="ConstraintCoincidenceSnapper"/> constructor and 0.91 s the edge index inside it.
/// <see cref="ConstraintCoincidenceSnapperScalingBenchmarkTests"/> times the index alone; this times the
/// method the trace actually named, so the two can be compared against that trace directly.
/// </summary>
/// <remarks>
/// Opt in with <c>MOLEHILL_PERF=1</c>. Single observations on one machine, not warmed medians. This is
/// a Core benchmark and says nothing about edit-to-visible latency — the Rhino stage around this method
/// (input resolution, mesh marshalling, the grading tiers) is not exercised here.
/// </remarks>
public class PadGraderCreateConstraintsBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void CreateConstraints_AtTheTracedScale()
    {
        if (!PerformanceLane.ShouldRun(output, "PadGrader.CreateConstraints at the traced scale"))
            return;

        // 250x250 quads split into triangles: 63,001 vertices, 125,000 faces, 188,000 unique edges.
        // The trace's fixture was 62,500 vertices / 186,501 edges.
        BuildGrid(250, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);

        // One pad covering ~8% of the terrain's area, as the traced pad did.
        double extent = 250.0;
        double padSize = extent * 0.283;
        double origin = (extent - padSize) * 0.5;
        var pad = new PadGrader.PadBoundary(
            [origin, origin, origin + padSize, origin, origin + padSize, origin + padSize, origin, origin + padSize],
            vertexCount: 4,
            targetZ: 5.0,
            slopeAngleDeg: 33.0);

        // Untimed pass: JIT and first-touch page faults are not the measurement.
        _ = PadGrader.CreateConstraints(vertices, vertexCount, faces, faceCount, [pad], lockCurves: null);

        var timer = Stopwatch.StartNew();
        PadGrader.ConstraintSet result = PadGrader.CreateConstraints(
            vertices, vertexCount, faces, faceCount, [pad], lockCurves: null);
        timer.Stop();

        output.WriteLine(
            $"verts={vertexCount:N0} faces={faceCount:N0} edges=188,000 pad~8% | " +
            $"CreateConstraints {timer.Elapsed.TotalMilliseconds:N1} ms | " +
            $"{result.Constraints.Length:N0} constraints | " +
            $"trace 2026-09-19 measured 1,280 ms here, of which 910 ms was the edge index");

        Assert.NotEmpty(result.Constraints);
    }

    private static void BuildGrid(int divisions, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        int stride = divisions + 1;
        vertexCount = stride * stride;
        vertices = new double[vertexCount * 3];
        for (int y = 0; y <= divisions; y++)
        {
            for (int x = 0; x <= divisions; x++)
            {
                int i = (y * stride) + x;
                vertices[i * 3] = x;
                vertices[i * 3 + 1] = y;
                // A gentle slope, so the pad actually cuts and fills rather than sitting on a plane.
                vertices[i * 3 + 2] = (x * 0.04) + (y * 0.02);
            }
        }

        faceCount = divisions * divisions * 2;
        faces = new int[faceCount * 3];
        int f = 0;
        for (int y = 0; y < divisions; y++)
        {
            for (int x = 0; x < divisions; x++)
            {
                int a = (y * stride) + x;
                int b = a + 1;
                int c = a + stride;
                int d = c + 1;
                faces[f++] = a; faces[f++] = b; faces[f++] = d;
                faces[f++] = a; faces[f++] = d; faces[f++] = c;
            }
        }
    }
}
