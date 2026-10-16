using MoleHill.Core.Displacement;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The mapping rectangle convention: the start point is the image's bottom-left, the bottom edge runs
/// counter-clockwise from it, so the drawing direction never mirrors or turns the image, and only a level,
/// square-cornered, four-cornered closed curve is a rectangle.
/// </summary>
public class MappingRectangleTests
{
    private const double Tolerance = 1e-6;

    private static double[] Xyz(params (double X, double Y)[] corners)
    {
        var xyz = new double[(corners.Length + 1) * 3];
        for (int i = 0; i <= corners.Length; i++)
        {
            (double x, double y) = corners[i % corners.Length];
            xyz[i * 3] = x;
            xyz[i * 3 + 1] = y;
            xyz[i * 3 + 2] = 5.0;
        }

        return xyz;
    }

    private static MappingRectangle Read(double[] xyz)
    {
        Assert.True(MappingRectangle.TryFromPolyline(xyz, xyz.Length / 3, Tolerance, out MappingRectangle rectangle, out string? error), error);
        return rectangle;
    }

    [Fact]
    public void CounterClockwise_StartCornerIsOrigin_FirstEdgeIsTheBottom()
    {
        MappingRectangle r = Read(Xyz((10, 20), (14, 20), (14, 22), (10, 22)));

        Assert.Equal(10.0, r.OriginX, 9);
        Assert.Equal(20.0, r.OriginY, 9);
        Assert.Equal(4.0, r.Width, 9);
        Assert.Equal(2.0, r.Height, 9);
        Assert.Equal(0.0, r.RotationDegrees, 9);
    }

    [Fact]
    public void ReversedDirection_FromTheSameCorner_PlacesTheImageIdentically()
    {
        MappingRectangle forward = Read(Xyz((10, 20), (14, 20), (14, 22), (10, 22)));
        MappingRectangle reversed = Read(Xyz((10, 20), (10, 22), (14, 22), (14, 20)));

        Assert.Equal(forward.OriginX, reversed.OriginX, 9);
        Assert.Equal(forward.OriginY, reversed.OriginY, 9);
        Assert.Equal(forward.Width, reversed.Width, 9);
        Assert.Equal(forward.Height, reversed.Height, 9);
        Assert.Equal(forward.RotationDegrees, reversed.RotationDegrees, 9);
    }

    [Fact]
    public void AnotherStartCorner_TurnsTheImageAQuarterTurn()
    {
        MappingRectangle r = Read(Xyz((14, 20), (14, 22), (10, 22), (10, 20)));

        Assert.Equal(14.0, r.OriginX, 9);
        Assert.Equal(20.0, r.OriginY, 9);
        Assert.Equal(2.0, r.Width, 9);
        Assert.Equal(4.0, r.Height, 9);
        Assert.Equal(90.0, r.RotationDegrees, 9);
    }

    [Fact]
    public void RotatedRectangle_GivesItsAngle_AndMapsCornersToTheTile()
    {
        double a = 30.0 * Math.PI / 180.0;
        (double X, double Y) P(double u, double v) => (100 + (u * Math.Cos(a)) - (v * Math.Sin(a)), 50 + (u * Math.Sin(a)) + (v * Math.Cos(a)));
        MappingRectangle r = Read(Xyz(P(0, 0), P(6, 0), P(6, 3), P(0, 3)));

        Assert.Equal(30.0, r.RotationDegrees, 9);
        (double u, double v) = r.Placement.ToLocal(P(6, 3).X, P(6, 3).Y);
        Assert.Equal(6.0, u, 9);
        Assert.Equal(3.0, v, 9);
    }

    [Fact]
    public void PointsAlongASide_AreIgnored()
    {
        MappingRectangle r = Read(Xyz((0, 0), (2, 0), (4, 0), (4, 2), (0, 2), (0, 1)));
        Assert.Equal(4.0, r.Width, 9);
        Assert.Equal(2.0, r.Height, 9);
    }

    [Fact]
    public void ScaledRectangle_ScalesTheTile_NotTheAngle()
    {
        MappingRectangle metres = Read(Xyz((1, 1), (5, 1), (5, 3), (1, 3)));
        MappingRectangle millimetres = Read(Xyz((1000, 1000), (5000, 1000), (5000, 3000), (1000, 3000)));

        Assert.Equal(metres.Width * 1000.0, millimetres.Width, 6);
        Assert.Equal(metres.Height * 1000.0, millimetres.Height, 6);
        Assert.Equal(metres.RotationDegrees, millimetres.RotationDegrees, 9);
    }

    [Theory]
    [InlineData("square")]
    [InlineData("corners")]
    [InlineData("start")]
    [InlineData("level")]
    public void NonRectangles_AreRefused_WithAReason(string kind)
    {
        double[] xyz = kind switch
        {
            "square" => Xyz((0, 0), (4, 0), (5, 2), (1, 2)),
            "corners" => Xyz((0, 0), (4, 0), (2, 3)),
            "start" => Xyz((2, 0), (4, 0), (4, 2), (0, 2), (0, 0)),
            _ => Xyz((0, 0), (4, 0), (4, 2), (0, 2)),
        };
        if (kind == "level")
            xyz[5] = 6.0;

        Assert.False(MappingRectangle.TryFromPolyline(xyz, xyz.Length / 3, Tolerance, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
