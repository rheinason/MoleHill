using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class SurfaceDeviationEvaluatorTests
{
    [Fact]
    public void Evaluate_OppositeQuadDiagonals_FindsCrossingEdgeMaximum()
    {
        double[] vertices = { 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 1, 0 };
        int[] firstDiagonal = { 0, 1, 2, 0, 2, 3 };
        int[] secondDiagonal = { 0, 1, 3, 1, 2, 3 };

        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(
            vertices, firstDiagonal, vertices, secondDiagonal);

        Assert.True(result.IsValid, result.FailureReason);
        Assert.True(result.HasEqualDomain, result.FailureReason);
        Assert.Equal(0.5, result.MaximumDeviation, 10);
        Assert.Equal(0.5, result.WorstX, 10);
        Assert.Equal(0.5, result.WorstY, 10);
    }

    [Fact]
    public void Evaluate_MissingCandidateCoverage_FailsDomainVerification()
    {
        double[] referenceVertices = { 0, 0, 0, 2, 0, 0, 2, 2, 0, 0, 2, 0 };
        int[] referenceFaces = { 0, 1, 2, 0, 2, 3 };
        double[] candidateVertices = { 0, 0, 0, 1, 0, 0, 1, 2, 0, 0, 2, 0 };
        int[] candidateFaces = { 0, 1, 2, 0, 2, 3 };

        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(
            referenceVertices, referenceFaces, candidateVertices, candidateFaces);

        Assert.True(result.IsValid, result.FailureReason);
        Assert.False(result.HasEqualDomain);
        Assert.Equal(4.0, result.ReferenceArea, 10);
        Assert.Equal(2.0, result.CandidateArea, 10);
        Assert.Equal(2.0, result.OverlapArea, 10);
    }

    [Fact]
    public void Evaluate_MatchingHoleDomains_PassButFilledHoleFails()
    {
        (double[] ringVertices, int[] ringFaces) = SquareRing();
        double[] filledVertices = { 0, 0, 0, 4, 0, 0, 4, 4, 0, 0, 4, 0 };
        int[] filledFaces = { 0, 1, 2, 0, 2, 3 };

        SurfaceDeviationEvaluator.Result same = SurfaceDeviationEvaluator.Evaluate(
            ringVertices, ringFaces, ringVertices, ringFaces);
        SurfaceDeviationEvaluator.Result filled = SurfaceDeviationEvaluator.Evaluate(
            ringVertices, ringFaces, filledVertices, filledFaces);

        Assert.True(same.HasEqualDomain, same.FailureReason);
        Assert.Equal(0.0, same.MaximumDeviation);
        Assert.False(filled.HasEqualDomain);
        Assert.Equal(12.0, filled.ReferenceArea, 10);
        Assert.Equal(16.0, filled.CandidateArea, 10);
    }

    [Fact]
    public void Evaluate_RollingRetriangulation_AgreesWithDenseBruteForceSamples()
    {
        double[] referenceVertices = { 0, 0, 0, 2, 0, 1, 2, 2, 0.2, 0, 2, -0.4 };
        int[] referenceFaces = { 0, 1, 2, 0, 2, 3 };
        double[] candidateVertices = { 0, 0, 0.1, 2, 0, 0.8, 2, 2, 0.5, 0, 2, -0.2 };
        int[] candidateFaces = { 0, 1, 3, 1, 2, 3 };

        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(
            referenceVertices, referenceFaces, candidateVertices, candidateFaces);
        double sampled = DenseSampleMaximum(referenceVertices, referenceFaces, candidateVertices, candidateFaces, 400);

        Assert.True(result.HasEqualDomain, result.FailureReason);
        Assert.InRange(Math.Abs(result.MaximumDeviation - sampled), 0.0, 1e-10);
    }

    [Fact]
    public void Evaluate_LargeTranslatedCoordinates_RemainsStable()
    {
        double[] first = { 1e9, -2e9, 0, 1e9 + 1, -2e9, 0, 1e9 + 1, -2e9 + 1, 1, 1e9, -2e9 + 1, 0 };
        int[] a = { 0, 1, 2, 0, 2, 3 };
        int[] b = { 0, 1, 3, 1, 2, 3 };

        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(first, a, first, b);

        Assert.True(result.HasEqualDomain, result.FailureReason);
        Assert.Equal(0.5, result.MaximumDeviation, 8);
    }

    [Fact]
    public void Evaluate_GradePadBatterSliverNarrowerThanModelTolerance_RemainsValid()
    {
        double[] vertices =
        {
            120.0000, 70.0000, 0.0000,
            120.1650, 64.8350, -0.0358,
            120.1667, 65.0000, -0.0264
        };
        int[] faces = { 0, 1, 2 };

        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(
            vertices,
            faces,
            vertices,
            faces,
            new SurfaceDeviationEvaluator.Options { NumericalTolerance = 0.01 });

        Assert.True(result.IsValid, result.FailureReason);
        Assert.True(result.HasEqualDomain, result.FailureReason);
        Assert.Equal(0.0, result.MaximumDeviation);
        Assert.InRange(result.ReferenceArea, 0.018, 0.019);
    }

    [Fact]
    public void Evaluate_Cancelled_ThrowsWithoutReturningPartialResult()
    {
        double[] vertices = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        int[] faces = { 0, 1, 2 };

        Assert.Throws<OperationCanceledException>(() => SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, vertices, faces,
            new SurfaceDeviationEvaluator.Options { ShouldCancel = () => true }));
    }

    [Fact]
    public void Evaluate_NonFiniteInput_ReturnsInvalidDiagnostic()
    {
        double[] vertices = { 0, 0, 0, 1, 0, double.NaN, 0, 1, 0 };

        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(vertices, new[] { 0, 1, 2 }, vertices, new[] { 0, 1, 2 });

        Assert.False(result.IsValid);
        Assert.Contains("not finite", result.FailureReason);
    }

    private static (double[] Vertices, int[] Faces) SquareRing()
    {
        double[] vertices =
        {
            0, 0, 0, 4, 0, 0, 4, 4, 0, 0, 4, 0,
            1, 1, 0, 3, 1, 0, 3, 3, 0, 1, 3, 0
        };
        int[] faces =
        {
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7
        };
        return (vertices, faces);
    }

    private static double DenseSampleMaximum(double[] aVertices, int[] aFaces, double[] bVertices, int[] bFaces, int divisions)
    {
        double maximum = 0.0;
        for (int y = 0; y <= divisions; y++)
        {
            for (int x = 0; x <= divisions; x++)
            {
                double px = 2.0 * x / divisions;
                double py = 2.0 * y / divisions;
                maximum = Math.Max(maximum, Math.Abs(FindZ(aVertices, aFaces, px, py) - FindZ(bVertices, bFaces, px, py)));
            }
        }
        return maximum;
    }

    private static double FindZ(double[] vertices, int[] faces, double x, double y)
    {
        for (int f = 0; f < faces.Length / 3; f++)
        {
            int i0 = faces[f * 3], i1 = faces[f * 3 + 1], i2 = faces[f * 3 + 2];
            double x0 = vertices[i0 * 3], y0 = vertices[i0 * 3 + 1];
            double x1 = vertices[i1 * 3], y1 = vertices[i1 * 3 + 1];
            double x2 = vertices[i2 * 3], y2 = vertices[i2 * 3 + 1];
            double denominator = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
            double w0 = (((y1 - y2) * (x - x2)) + ((x2 - x1) * (y - y2))) / denominator;
            double w1 = (((y2 - y0) * (x - x2)) + ((x0 - x2) * (y - y2))) / denominator;
            double w2 = 1.0 - w0 - w1;
            if (w0 >= -1e-12 && w1 >= -1e-12 && w2 >= -1e-12)
                return (w0 * vertices[i0 * 3 + 2]) + (w1 * vertices[i1 * 3 + 2]) + (w2 * vertices[i2 * 3 + 2]);
        }
        throw new InvalidOperationException("Sample fell outside the test domain.");
    }
}
