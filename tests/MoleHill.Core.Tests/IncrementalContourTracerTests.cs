using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class IncrementalContourTracerTests
{
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) BuildGrid(int n, Func<double, double, double> height)
    {
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * n + i;
                vertices[v * 3] = i;
                vertices[v * 3 + 1] = j;
                vertices[v * 3 + 2] = height(i, j);
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = j * n + i, v10 = v00 + 1, v01 = v00 + n, v11 = v01 + 1;
                faces.AddRange(new[] { v00, v10, v11, v00, v11, v01 });
            }
        }

        return (vertices, n * n, faces.ToArray(), faces.Count / 3);
    }

    private static double TotalLength(IEnumerable<ContourSegment> segments, int level) =>
        segments.Where(s => s.Level == level)
            .Sum(s => Math.Sqrt((s.Bx - s.Ax) * (s.Bx - s.Ax) + (s.By - s.Ay) * (s.By - s.Ay) + (s.Bz - s.Az) * (s.Bz - s.Az)));

    private static double TotalLength(IEnumerable<ContourLevel> levels, double z) =>
        levels.Where(l => l.Z == z).SelectMany(l => l.Polylines).Sum(p =>
        {
            double sum = 0;
            for (int k = 0; k + 1 < p.PointCount; k++)
            {
                double dx = p.PointsXyz[k * 3 + 3] - p.PointsXyz[k * 3], dy = p.PointsXyz[k * 3 + 4] - p.PointsXyz[k * 3 + 1], dz = p.PointsXyz[k * 3 + 5] - p.PointsXyz[k * 3 + 2];
                sum += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            return sum;
        });

    [Fact]
    public void Constructor_SlopedGrid_MatchesContourGeneratorLength()
    {
        var (v, vc, f, fc) = BuildGrid(11, (x, y) => 0.37 * x + 0.21 * y);
        double[] levels = { 1.0, 2.0, 3.0 };

        var tracer = new IncrementalContourTracer(v, f, fc, levels);
        var reference = ContourGenerator.Generate(v, vc, f, fc, levels, 1e-9);

        for (int li = 0; li < levels.Length; li++)
            Assert.Equal(TotalLength(reference, levels[li]), TotalLength(tracer.Segments(), li), 6);
    }

    [Fact]
    public void Update_AfterLocalBump_MatchesFullRetrace()
    {
        var (v, vc, f, fc) = BuildGrid(21, (x, y) => 0.1 * x);
        double[] levels = { 0.5, 1.0, 1.5, 2.0, 2.5 };
        var tracer = new IncrementalContourTracer(v, f, fc, levels);
        int versionBefore = tracer.Version;

        // Raise vertices near (10, 10) and re-trace only the faces that touch them.
        var moved = new HashSet<int>();
        for (int i = 0; i < vc; i++)
        {
            double dx = v[i * 3] - 10, dy = v[i * 3 + 1] - 10;
            if (dx * dx + dy * dy < 9)
            {
                v[i * 3 + 2] += 1.3;
                moved.Add(i);
            }
        }

        var touched = Enumerable.Range(0, fc).Where(t => moved.Contains(f[t * 3]) || moved.Contains(f[t * 3 + 1]) || moved.Contains(f[t * 3 + 2]));
        tracer.Update(v, touched);

        var fresh = new IncrementalContourTracer(v, f, fc, levels);
        Assert.True(tracer.Version > versionBefore);
        for (int li = 0; li < levels.Length; li++)
            Assert.Equal(TotalLength(fresh.Segments(), li), TotalLength(tracer.Segments(), li), 9);
    }

    [Fact]
    public void Update_FaceLiftedAboveAllLevels_DropsItsSegments()
    {
        var (v, vc, f, fc) = BuildGrid(3, (x, _) => x);
        var tracer = new IncrementalContourTracer(v, f, fc, new[] { 0.5 });
        Assert.NotEmpty(tracer.Segments());

        for (int i = 0; i < vc; i++)
            v[i * 3 + 2] += 10.0;
        tracer.Update(v, Enumerable.Range(0, fc));

        Assert.Empty(tracer.Segments());
    }
}
