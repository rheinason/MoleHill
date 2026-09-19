using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Synthetic baseline for the cost the 2026-09-19 geometry-heavy trace put at 0.91 s: building a
/// <c>SpatialHashGrid2D</c> over every terrain edge so that a handful of grading constraints can be
/// snapped onto the mesh. Compares the whole-mesh index with the constraint-clipped one at the fixture's
/// scale.
/// </summary>
/// <remarks>
/// Opt in with <c>MOLEHILL_PERF=1</c>. Single observations on one machine, not warmed medians: this
/// measures the shape of the change, not a budget. It is also a managed micro-benchmark and therefore
/// says nothing about end-to-end edit-to-visible latency — only about the construction cost the trace
/// attributed to this constructor. Geometry equivalence is covered separately by
/// <see cref="ConstraintCoincidenceSnapperRegionTests"/>.
/// </remarks>
public class ConstraintCoincidenceSnapperScalingBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void ForConstraints_ClippedIndex_AgainstTheWholeMeshIndex()
    {
        if (!PerformanceLane.ShouldRun(output, "constraint coincidence snapper index scaling"))
            return;

        // side=250 gives 63,001 vertices, 125,000 faces and 188,000 unique edges — within a couple of
        // percent of the traced fixture's 62,500 vertices / 186,501 edges.
        foreach (int side in new[] { 100, 250 })
        {
            Run(side, padFraction: 0.08);
        }

        // A pad covering most of the terrain is the case clipping cannot help; it must not cost more.
        Run(250, padFraction: 0.90);
    }

    private void Run(int side, double padFraction)
    {
        BuildGrid(side, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        const double tolerance = 0.25;

        double extent = side;
        double padSize = extent * padFraction;
        double origin = (extent - padSize) * 0.5;
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>
        {
            ClosedLoop(origin, origin, padSize)
        };

        // One untimed pass each, so JIT and first-touch page faults are not charged to the comparison.
        _ = new ConstraintCoincidenceSnapper(vertices, vertexCount, faces, faceCount, tolerance);
        _ = ConstraintCoincidenceSnapper.ForConstraints(vertices, vertexCount, faces, faceCount, tolerance, constraints);

        var fullTimer = Stopwatch.StartNew();
        var full = new ConstraintCoincidenceSnapper(vertices, vertexCount, faces, faceCount, tolerance);
        fullTimer.Stop();

        var clippedTimer = Stopwatch.StartNew();
        var clipped = ConstraintCoincidenceSnapper.ForConstraints(
            vertices, vertexCount, faces, faceCount, tolerance, constraints);
        clippedTimer.Stop();

        var snapTimer = Stopwatch.StartNew();
        SurfaceRemesher.ConstraintPolyline snapped = clipped.SnapConstraintPolyline(constraints[0]);
        snapTimer.Stop();

        output.WriteLine(
            $"side={side} pad={padFraction:P0} verts={vertexCount:N0} faces={faceCount:N0} | " +
            $"full ctor {fullTimer.Elapsed.TotalMilliseconds:N1} ms ({full.IndexedCounts.Edges:N0} edges) | " +
            $"clipped ctor {clippedTimer.Elapsed.TotalMilliseconds:N1} ms ({clipped.IndexedCounts.Edges:N0} edges) | " +
            $"snap {snapTimer.Elapsed.TotalMilliseconds:N1} ms, {snapped.PointCount:N0} pts, " +
            $"escaped={clipped.RegionWasAbandoned}");
    }

    private static SurfaceRemesher.ConstraintPolyline ClosedLoop(double x, double y, double size)
    {
        // Deliberately off-grid, so the snap does real work rather than matching vertices exactly.
        double[] points =
        [
            x + 0.13, y + 0.07, 0.0,
            x + size + 0.13, y + 0.07, 0.0,
            x + size + 0.13, y + size + 0.07, 0.0,
            x + 0.13, y + size + 0.07, 0.0
        ];
        return new SurfaceRemesher.ConstraintPolyline(points, 4, IsClosed: true, PreserveInputElevation: false);
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
                vertices[i * 3 + 2] = 0.0;
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
