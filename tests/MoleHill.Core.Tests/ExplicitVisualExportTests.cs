using System.Globalization;
using System.Text;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

// Exports OBJ files of the explicit grading engine on realistic (undulating) terrain so the
// result can be eyeballed in Rhino. Not a CI assertion suite — a visual-proof harness.
public class ExplicitVisualExportTests
{
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "MoleHillExplicit");

    // Undulating terrain: a tilted plane plus a couple of sine bumps so daylight reach varies.
    private static (double[] v, int vc, int[] f, int fc) UndulatingTerrain(int n, double cell)
    {
        var v = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * cell;
                double y = j * cell;
                double z = (0.08 * x) - (0.05 * y)
                           + (2.0 * Math.Sin(x * 0.18) * Math.Cos(y * 0.15));
                int idx = (j * n) + i;
                v[idx * 3] = x;
                v[idx * 3 + 1] = y;
                v[idx * 3 + 2] = z;
            }
        }

        var f = new List<int>((n - 1) * (n - 1) * 6);
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int a = (j * n) + i, b = (j * n) + i + 1, c = ((j + 1) * n) + i + 1, d = ((j + 1) * n) + i;
                f.Add(a); f.Add(b); f.Add(c);
                f.Add(a); f.Add(c); f.Add(d);
            }
        }

        return (v, n * n, f.ToArray(), f.Count / 3);
    }

    private static void WriteObj(string path, double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        for (int i = 0; i < vertexCount; i++)
        {
            sb.Append("v ")
              .Append(vertices[i * 3].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(vertices[i * 3 + 1].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(vertices[i * 3 + 2].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        for (int i = 0; i < faceCount; i++)
        {
            sb.Append("f ")
              .Append(faces[i * 3] + 1).Append(' ')
              .Append(faces[i * 3 + 1] + 1).Append(' ')
              .Append(faces[i * 3 + 2] + 1).Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    [Fact]
    public void Export_SinglePad_Explicit()
    {
        var t = UndulatingTerrain(61, 1.0); // 60x60 m, ~1 m cells
        WriteObj(Path.Combine(OutDir, "input-terrain.obj"), t.v, t.vc, t.f, t.fc);

        // Pad centered so the batter daylights well within the terrain on every side.
        double[] padXy = { 24, 24, 36, 24, 36, 36, 24, 36 };
        var pads = new[] { new PadGrader.PadBoundary(padXy, 4, targetZ: -1.0, slopeAngleDeg: 30.0) };

        GradingResult? r = PadGrader.Grade(t.v, t.vc, t.f, t.fc, pads, null, out string? err);
        Assert.True(r != null, err);
        string diag = string.Join(" | ", r!.Diagnostics);
        Assert.Contains("explicit batter", diag, StringComparison.OrdinalIgnoreCase);
        WriteObj(Path.Combine(OutDir, "graded-pad.obj"), r.Vertices, r.VertexCount, r.Faces, r.FaceCount);
    }

    [Fact]
    public void Export_SinglePad_CoarseTerrain_Explicit()
    {
        // Coarse terrain (~6m cells, like the user's ~334-vert case) — the seam must NOT spoke.
        var t = UndulatingTerrain(11, 6.0); // 60x60 m, 11x11 grid
        WriteObj(Path.Combine(OutDir, "input-terrain-coarse.obj"), t.v, t.vc, t.f, t.fc);

        double[] padXy = { 24, 24, 36, 24, 36, 36, 24, 36 };
        var pads = new[] { new PadGrader.PadBoundary(padXy, 4, targetZ: -1.0, slopeAngleDeg: 30.0) };

        GradingResult? r = PadGrader.Grade(t.v, t.vc, t.f, t.fc, pads, null, out string? err);
        Assert.True(r != null, err);
        Assert.Contains("explicit batter", string.Join(" ", r!.Diagnostics), StringComparison.OrdinalIgnoreCase);
        WriteObj(Path.Combine(OutDir, "graded-pad-coarse.obj"), r.Vertices, r.VertexCount, r.Faces, r.FaceCount);
    }

    [Fact]
    public void Export_InteractingPads_Explicit()
    {
        var t = UndulatingTerrain(51, 1.0);
        // Two pads close enough that their batters overlap, at different elevations.
        var pads = new[]
        {
            new PadGrader.PadBoundary(new[] { 12.0, 18.0, 24.0, 18.0, 24.0, 30.0, 12.0, 30.0 }, 4, targetZ: -1.0, slopeAngleDeg: 30.0),
            new PadGrader.PadBoundary(new[] { 28.0, 20.0, 40.0, 20.0, 40.0, 32.0, 28.0, 32.0 }, 4, targetZ: 1.5, slopeAngleDeg: 30.0)
        };

        GradingResult? r = PadGrader.Grade(t.v, t.vc, t.f, t.fc, pads, null, out string? err);
        Assert.True(r != null, err);
        Assert.Contains("explicit batter", string.Join(" ", r!.Diagnostics), StringComparison.OrdinalIgnoreCase);
        WriteObj(Path.Combine(OutDir, "graded-interacting-pads.obj"), r.Vertices, r.VertexCount, r.Faces, r.FaceCount);
    }

    [Fact]
    public void Export_PathWithLockCurve_Explicit()
    {
        var t = UndulatingTerrain(61, 1.0);
        var paths = new[]
        {
            new PathGrader.PathDefinition(
                new[] { 18.0, 30.0, 42.0, 30.0 }, new[] { 3.0, 1.0 }, 2, width: 5.0, slopeAngleDeg: 30.0, maxDistance: 8.0)
        };
        // Lock curve (retaining wall) parallel to and just north of the road, clipping the +y batter.
        var lockCurve = new MoleHill.Core.Engine.ConstraintPolyline(
            new[] { 18.0, 36.0, 0.0, 42.0, 36.0, 0.0 }, PointCount: 2, IsClosed: false, PreserveInputElevation: false);

        GradingResult? r = PathGrader.Grade(t.v, t.vc, t.f, t.fc, paths, new[] { lockCurve }, out string? err);
        Assert.True(r != null, err);
        Assert.Contains("explicit corridor", string.Join(" ", r!.Diagnostics), StringComparison.OrdinalIgnoreCase);
        WriteObj(Path.Combine(OutDir, "graded-path-lockcurve.obj"), r.Vertices, r.VertexCount, r.Faces, r.FaceCount);
    }

    [Fact]
    public void Export_SinglePath_Explicit()
    {
        var t = UndulatingTerrain(61, 1.0);
        // Gently curving road kept well inside the terrain so the batters daylight before the edge.
        var xy = new List<double>();
        var z = new List<double>();
        for (int s = 0; s <= 12; s++)
        {
            double tt = s / 12.0;
            double x = 18 + (24 * tt);
            double y = 30 + (6 * Math.Sin(tt * Math.PI));
            xy.Add(x); xy.Add(y);
            z.Add(3.0 - (1.5 * tt));
        }

        var paths = new[]
        {
            new PathGrader.PathDefinition(xy.ToArray(), z.ToArray(), z.Count, width: 5.0, slopeAngleDeg: 30.0, maxDistance: 8.0)
        };

        GradingResult? r = PathGrader.Grade(t.v, t.vc, t.f, t.fc, paths, out string? err);
        Assert.True(r != null, err);
        string diag = string.Join(" | ", r!.Diagnostics);
        Assert.Contains("explicit corridor", diag, StringComparison.OrdinalIgnoreCase);
        WriteObj(Path.Combine(OutDir, "graded-path.obj"), r.Vertices, r.VertexCount, r.Faces, r.FaceCount);
    }
}
