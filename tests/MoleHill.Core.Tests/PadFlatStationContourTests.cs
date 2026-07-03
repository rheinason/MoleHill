using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Regression for the bug where the explicit-batter engine deferred on
/// almost every real pad: when a pad edge crosses the terrain contour that sits at the pad's own
/// grade elevation, those stations are Flat (no batter), so their daylight point collapses ONTO the
/// footprint boundary. The shoulder-fold gate used a boundary-inclusive point-in-polygon test, so it
/// mistook that boundary-coincident point for a fold-back and bailed out of the explicit tier.
/// </summary>
public class PadFlatStationContourTests
{
    // A sloped strip terrain: z rises linearly from 0 at y=0 to 4 at y=width.
    private static (double[] v, int vc, int[] f, int fc) SlopedStrip(double width)
    {
        var xy = new List<double>();
        var z = new List<double>();
        for (int i = 0; i < 5; i++) { double x = i * 10.0; xy.Add(x); xy.Add(0.0); z.Add(0.0); }
        for (int i = 0; i < 5; i++) { double x = i * 10.0; xy.Add(x); xy.Add(width); z.Add(4.0); }

        var outcome = TriangulationHelper.Triangulate(xy, z.Count, new List<(int, int)>(), 0, 0, convex: false, 0);
        var ex = TriangleNetExtractor.Extract(outcome.Mesh!);
        var v = new double[ex.VertexCount * 3];
        for (int i = 0; i < ex.VertexCount; i++)
        {
            v[i * 3] = ex.Xy[i * 2];
            v[i * 3 + 1] = ex.Xy[i * 2 + 1];
            int src = ex.SourceIds[i];
            v[i * 3 + 2] = src >= 0 && src < z.Count ? z[src] : 0.0;
        }

        return (v, ex.VertexCount, ex.Faces, ex.FaceCount);
    }

    [Theory]
    [InlineData(40.0)] // wide: the pad daylights fully on terrain
    [InlineData(10.0)] // narrow: shoulders run off the strip and are clipped to the terrain edge
    public void Grade_PadEdgeCrossesGradeContour_UsesExplicitPath(double width)
    {
        var t = SlopedStrip(width);
        double cy = width * 0.5; // terrain elevation here equals the pad grade (2.0)
        double[] padXy = { 16, cy - 4, 24, cy - 4, 24, cy + 4, 16, cy + 4 };
        var pads = new[] { new PadGrader.PadBoundary(padXy, 4, targetZ: 2.0, slopeAngleDeg: 33.0) };

        GradingResult? result = PadGrader.Grade(t.v, t.vc, t.f, t.fc, pads, null, out string? err);

        PadInvariantAssert.AssertValidExplicitGrading(result, err, t.fc, pads, requireExplicit: true);
    }
}
