using System;
using System.Collections.Generic;
using System.Linq;
using MoleHill.Core.Scattering;
using Xunit;

namespace MoleHill.Core.Tests;

public class ScatterSamplerCurveTests
{
    // A straight 100-unit line along X.
    private static double[] StraightLine(double length) => new[] { 0.0, 0.0, length, 0.0 };

    [Fact]
    public void Curve_SpacingMode_PlacesAtCenterToCenterSpacing()
    {
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { StraightLine(100.0) },
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 10.0,
            Seed = 1
        };

        var points = ScatterSampler.Sample(request);

        // 0,10,20,...,100 → 11 points, exactly on the line, evenly spaced.
        Assert.Equal(11, points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal(i * 10.0, points[i].X, 6);
            Assert.Equal(0.0, points[i].Y, 6);
        }
    }

    [Fact]
    public void Curve_CountMode_DistributesTargetCountAlongLength()
    {
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { StraightLine(100.0) },
            DensityMode = ScatterDensityMode.Count,
            Count = 20,
            Seed = 1
        };

        var points = ScatterSampler.Sample(request);

        // step = 100/20 = 5 → 0,5,...,100 = 21 points (count maps to a spacing; endpoints included).
        Assert.InRange(points.Count, 20, 21);
        Assert.All(points, p => Assert.Equal(0.0, p.Y, 6));
    }

    [Fact]
    public void Curve_JitterXy_OffsetsPointsOffTheLine_WithinRadius()
    {
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { StraightLine(100.0) },
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 5.0,
            JitterXy = 2.0,
            Seed = 7
        };

        var points = ScatterSampler.Sample(request);

        Assert.NotEmpty(points);
        // Every point stays within the jitter disk of its nearest on-line position.
        Assert.All(points, p =>
        {
            double nearestX = Math.Round(p.X / 5.0) * 5.0;
            double dx = p.X - nearestX;
            double dist = Math.Sqrt(dx * dx + p.Y * p.Y);
            Assert.True(dist <= 2.0 + 1e-6, $"point {p} outside jitter radius (dist {dist})");
        });
        // At least some points actually moved off the line (jitter is doing something).
        Assert.Contains(points, p => Math.Abs(p.Y) > 1e-6);
    }

    [Fact]
    public void Curve_NoJitter_IsDeterministicForSeed()
    {
        ScatterRequest Make() => new()
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { StraightLine(50.0) },
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 5.0,
            JitterXy = 1.5,
            AlongJitter = 0.8,
            Seed = 42
        };

        var a = ScatterSampler.Sample(Make());
        var b = ScatterSampler.Sample(Make());

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 9);
            Assert.Equal(a[i].Y, b[i].Y, 9);
        }
    }

    [Fact]
    public void Curve_AlongJitter_KeepsPointsWithinHalfStepOfEvenPositions()
    {
        const double spacing = 10.0;
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { StraightLine(100.0) },
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = spacing,
            AlongJitter = 1.0, // full ±half-step wander
            Seed = 5
        };

        var points = ScatterSampler.Sample(request);

        Assert.NotEmpty(points);
        // Each point stays within half a step of some even multiple (no jitter pushes it further).
        Assert.All(points, p =>
        {
            double nearestEven = Math.Round(p.X / spacing) * spacing;
            Assert.True(Math.Abs(p.X - nearestEven) <= spacing / 2.0 + 1e-6,
                $"point at {p.X} wandered more than half a step");
        });
        // And the randomness actually moved some points off their even positions.
        Assert.Contains(points, p => Math.Abs(p.X - Math.Round(p.X / spacing) * spacing) > 1e-6);
    }

    [Fact]
    public void Curve_EdgeToEdge_SpacesByFootprintPlusGap()
    {
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { StraightLine(100.0) },
            DensityMode = ScatterDensityMode.EdgeToEdge,
            EdgeGap = 2.0,
            Seed = 1
        };

        // Constant footprint extent of 10 → centre-to-centre = 10/2 + 2 + 10/2 = 12; first centre at 5.
        var points = ScatterSampler.SampleCurve(request, itemExtent: _ => 10.0);

        Assert.True(points.Count >= 2);
        Assert.Equal(5.0, points[0].X, 6);
        for (int i = 1; i < points.Count; i++)
            Assert.Equal(12.0, points[i].X - points[i - 1].X, 6);
        // Last item's far edge must fit on the curve.
        Assert.True(points[^1].X + 5.0 <= 100.0 + 1e-6);
    }

    [Fact]
    public void Curve_Tangent_FollowsCurveDirection()
    {
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Curve,
            Paths = new[] { new[] { 0.0, 0.0, 10.0, 10.0 } }, // 45-degree line
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 2.0,
            Seed = 1
        };

        var points = ScatterSampler.SampleCurve(request, itemExtent: null);

        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(Math.PI / 4, p.TangentRadians, 6));
    }

    [Fact]
    public void Region_RunawayDensity_IsCappedNotHung()
    {
        // A 100x100 square with an absurd per-area density would generate ~10M points uncapped.
        var square = new[] { 0.0, 0.0, 100.0, 0.0, 100.0, 100.0, 0.0, 100.0 };
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Region,
            Boundaries = new[] { square },
            Pattern = ScatterPattern.Grid,
            DensityMode = ScatterDensityMode.PerArea,
            PerAreaDensity = 1000.0,
            Seed = 1,
            MaxSamples = 5000
        };

        var points = ScatterSampler.Sample(request);

        Assert.True(points.Count <= 5000, $"expected cap at 5000, got {points.Count}");
    }

    [Fact]
    public void Region_Cancellation_AbortsSampling()
    {
        var square = new[] { 0.0, 0.0, 100.0, 0.0, 100.0, 100.0, 0.0, 100.0 };
        var request = new ScatterRequest
        {
            Source = ScatterSourceMode.Region,
            Boundaries = new[] { square },
            Pattern = ScatterPattern.Grid,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 0.5,           // ~40k points uncancelled
            Seed = 1,
            ShouldCancel = () => true // cancel immediately
        };

        var points = ScatterSampler.Sample(request);

        // Cancelled before the first row completes → far fewer than the uncancelled ~40k.
        Assert.True(points.Count < 1000, $"cancellation did not abort early (got {points.Count})");
    }
}
