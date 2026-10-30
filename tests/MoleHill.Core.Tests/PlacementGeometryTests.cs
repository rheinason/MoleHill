using MoleHill.Core.Scattering;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class PlacementGeometryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(70)]
    public void TryFit_TiltedCarBox_RecoversBroadBaseAndVolume(double angle)
    {
        double a = angle * Math.PI / 180;
        var points = new List<PlacementVector>();
        foreach (double x in new[] { -2.0, 2.0 })
            foreach (double y in new[] { -1.0, 1.0 })
                foreach (double z in new[] { 0.0, 0.8 })
                    points.Add(new(x * Math.Cos(a) + z * Math.Sin(a) + 500000,
                        y + 200000, -x * Math.Sin(a) + z * Math.Cos(a) + 10));
        Assert.True(PlacementBox.TryFit(points, out var box));
        Assert.NotNull(box);
        var corners = box.Corners;
        double length = (corners[1] - corners[0]).Length;
        double width = (corners[3] - corners[0]).Length;
        double height = (corners[4] - corners[0]).Length;
        Assert.Equal(4, length, 7);
        Assert.Equal(2, width, 7);
        Assert.Equal(0.8, height, 7);
        Assert.Equal(6.4, length * width * height, 7);
        Assert.True((corners[4] - corners[0]).Z >= 0);
        Assert.True(PlacementBox.TryFit(points, out var repeated));
        Assert.Equal(corners, repeated!.Corners);
    }

    [Fact]
    public void TryFitTerrain_CrossSlope_PreservesHeadingInPlan()
    {
        var points = new[] { new PlacementVector(-2, -1, -2.5), new PlacementVector(2, -1, 1.5),
            new PlacementVector(2, 1, 2.5), new PlacementVector(-2, 1, -1.5) };
        Assert.True(PlacementFrame.TryFit(points, new(1, 1, 0), out var frame));
        Assert.Equal(frame.XAxis.X, frame.XAxis.Y, 12);
        Assert.True(PlacementVector.Dot(frame.Normal, new PlacementVector(-1, -0.5, 1).Unit) > 1 - 1e-12);
        foreach (var point in points) Assert.InRange(Math.Abs(PlacementVector.Dot(point - frame.Origin, frame.Normal)), 0, 1e-10);
    }

    [Fact]
    public void TryFitTerrain_NonCoplanarHits_UsesBestFitWithoutClearance()
    {
        var points = new[] { new PlacementVector(-1, -1, 0), new PlacementVector(1, -1, 1),
            new PlacementVector(1, 1, 0), new PlacementVector(-1, 1, 1) };
        Assert.True(PlacementFrame.TryFit(points, new(1, 0, 0), out var frame));
        Assert.Equal(0.5, frame.Origin.Z, 12);
        Assert.Contains(points, point => PlacementVector.Dot(point - frame.Origin, frame.Normal) > 0);
        Assert.Contains(points, point => PlacementVector.Dot(point - frame.Origin, frame.Normal) < 0);
    }

    [Fact]
    public void TryFit_DegenerateOrNonFiniteInput_RefusesAnUnstableFrame()
    {
        var line = new[] { new PlacementVector(0, 0, 0), new PlacementVector(1, 0, 0), new PlacementVector(2, 0, 0) };
        Assert.False(PlacementBox.TryFit(line, out _));
        Assert.False(PlacementFrame.TryFit(line, new(1, 0, 0), out _));
        Assert.False(PlacementBox.TryFit(new[] { new PlacementVector(double.NaN, 0, 0), line[0], line[1] }, out _));
    }
}
