using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SculptAnalysisColorizerTests
{
    [Fact]
    public void ResolveElevationRange_VerticalWallFaces_WeightsByPlanAreaLikeThePreview()
    {
        // A 10 x 10 flat pad at Z 0 (two faces) and a tall vertical wall rising to Z 1000 on its edge.
        // The wall covers no ground in plan, so the preview (TerrainAnalysisPreviewBuilder.MeasureFace)
        // gives it no weight; the sculpt auto-range must agree or colours jump when a session starts.
        double[] vertices =
        {
            0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0,
            0, 0, 1000, 10, 0, 1000
        };
        int[] faces = { 0, 1, 2, 0, 2, 3, 0, 1, 5, 0, 5, 4 };
        int faceCount = faces.Length / 3;
        var elevation = new ElevationAnalysisDefinition { AutoColorRange = true };

        AnalysisRange actual = SculptAnalysisColorizer.ResolveElevationRange(vertices, faces, faceCount, elevation);

        var values = new double[faceCount];
        var planAreas = new double[faceCount];
        var surfaceAreas = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[(f * 3) + 1], c = faces[(f * 3) + 2];
            values[f] = (vertices[(a * 3) + 2] + vertices[(b * 3) + 2] + vertices[(c * 3) + 2]) / 3.0;
            double e1x = vertices[b * 3] - vertices[a * 3], e1y = vertices[(b * 3) + 1] - vertices[(a * 3) + 1], e1z = vertices[(b * 3) + 2] - vertices[(a * 3) + 2];
            double e2x = vertices[c * 3] - vertices[a * 3], e2y = vertices[(c * 3) + 1] - vertices[(a * 3) + 1], e2z = vertices[(c * 3) + 2] - vertices[(a * 3) + 2];
            double nx = (e1y * e2z) - (e1z * e2y), ny = (e1z * e2x) - (e1x * e2z), nz = (e1x * e2y) - (e1y * e2x);
            planAreas[f] = Math.Abs(nz) * 0.5;
            surfaceAreas[f] = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz)) * 0.5;
        }

        AnalysisRange planWeighted = AnalysisRange.Resolve(values, planAreas, true, 0, 0, RangeShape.MinMax);
        AnalysisRange surfaceWeighted = AnalysisRange.Resolve(values, surfaceAreas, true, 0, 0, RangeShape.MinMax);
        Assert.NotEqual(surfaceWeighted, planWeighted); // the fixture discriminates the two weightings
        Assert.Equal(planWeighted, actual);
    }
}
