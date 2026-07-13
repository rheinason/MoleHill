using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TerrainConstraintPreprocessorTests
{
    [Fact]
    public void Process_LongTwoPointBreakline_ResamplesStraightRun()
    {
        double[] breakline =
        {
            0.0, 0.0, 10.0,
            100.0, 0.0, 10.0
        };

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            new[] { breakline },
            Array.Empty<double[]>(),
            tolerance: 0.5);

        Assert.Single(result);
        Assert.True(result[0].Length / 3 > 2, "Expected a long 2-point straight run to be resampled.");
        Assert.Equal(0.0, result[0][0], 6);
        Assert.Equal(100.0, result[0][^3], 6);
    }

    [Fact]
    public void Process_LongCollinearContourChain_PreservesRunButNormalizesSpacing()
    {
        double[] contour =
        {
            0.0, 0.0, 118.0,
            5.0, 0.0, 118.0,
            20.0, 0.0, 118.0,
            50.0, 0.0, 118.0,
            100.0, 0.0, 118.0
        };

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            Array.Empty<double[]>(),
            new[] { contour },
            tolerance: 0.25);

        Assert.Single(result);
        int pointCount = result[0].Length / 3;
        Assert.True(pointCount >= 4, $"Expected preserved straight run to remain sampled, got {pointCount} points.");

        double maxGap = 0.0;
        for (int i = 1; i < pointCount; i++)
        {
            double dx = result[0][i * 3] - result[0][(i - 1) * 3];
            double dy = result[0][i * 3 + 1] - result[0][(i - 1) * 3 + 1];
            maxGap = Math.Max(maxGap, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        Assert.True(maxGap < 60.0, $"Expected spacing normalization along straight run, got max gap {maxGap:F3}.");
    }

    [Fact]
    public void Process_ShortNoisyCollinearPolyline_DoesNotExplodeVertexCount()
    {
        double[] contour =
        {
            0.0, 0.0, 118.0,
            1.0, 0.0, 118.0,
            2.0, 0.0, 118.0
        };

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            Array.Empty<double[]>(),
            new[] { contour },
            tolerance: 0.5);

        Assert.Single(result);
        Assert.True(result[0].Length / 3 <= 3, "Expected short straight noise not to be unnecessarily densified.");
    }

    [Fact]
    public void Process_LongCollinearBreakline_ResamplesAlongOriginalZProfile()
    {
        double[] breakline =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            20.0, 0.0, 0.0,
            30.0, 0.0, 10.0
        };

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            new[] { breakline },
            Array.Empty<double[]>(),
            tolerance: 0.25);

        Assert.Single(result);
        double[] processed = result[0];
        Assert.Equal(4, processed.Length / 3);
        Assert.Equal(0.0, processed[2], 6);
        Assert.Equal(0.0, processed[5], 6);
        Assert.Equal(0.0, processed[8], 6);
        Assert.Equal(10.0, processed[11], 6);
    }

    [Fact]
    public void Process_BacktrackingCollinearRun_DoesNotCollapseRunOutOfOrder()
    {
        double[] breakline =
        {
            0.0, 0.0, 0.0,
            5.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            7.0, 0.0, 0.0,
            20.0, 0.0, 0.0
        };

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            new[] { breakline },
            Array.Empty<double[]>(),
            tolerance: 0.25);

        Assert.Single(result);
        double[] processed = result[0];

        bool foundBacktrackVertex = false;
        for (int i = 0; i < processed.Length / 3; i++)
        {
            if (Math.Abs(processed[i * 3] - 7.0) <= 1e-6)
            {
                foundBacktrackVertex = true;
                break;
            }
        }

        Assert.True(foundBacktrackVertex, "Expected backtracking station to survive instead of being collapsed into a monotonic straight run.");
    }

    [Fact]
    public void Process_LargeSiteContours_PreservesObservedSpacingInsteadOfToleranceScale()
    {
        const int contourCount = 100;
        const int pointsPerContour = 200;
        var contours = new List<double[]>(contourCount);
        for (int contourIndex = 0; contourIndex < contourCount; contourIndex++)
        {
            var contour = new double[pointsPerContour * 3];
            for (int pointIndex = 0; pointIndex < pointsPerContour; pointIndex++)
            {
                contour[pointIndex * 3] = pointIndex * 50.0;
                contour[pointIndex * 3 + 1] = contourIndex * 50.0;
                contour[pointIndex * 3 + 2] = contourIndex;
            }

            contours.Add(contour);
        }

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            Array.Empty<double[]>(),
            contours,
            tolerance: 0.0125);

        int inputCount = contourCount * pointsPerContour;
        int outputCount = result.Sum(static contour => contour.Length / 3);
        Assert.Equal(contourCount, result.Count);
        Assert.InRange(outputCount, inputCount, (int)(inputCount * 1.05));
    }
}
