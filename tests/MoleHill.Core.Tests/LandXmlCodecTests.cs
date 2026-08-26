using MoleHill.Core.Interop;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class LandXmlCodecTests
{
    [Fact]
    public void WriteRead_RoundTripsTinSurface()
    {
        var source = new TinSurfaceData { Name = "Test" };
        source.Points.AddRange(new[]
        {
            new TinSurfacePoint(1, 0, 0, 10),
            new TinSurfacePoint(2, 10, 0, 11),
            new TinSurfacePoint(3, 0, 10, 12)
        });
        source.Triangles.Add(new TinSurfaceTriangle(1, 2, 3));

        TinSurfaceData result = LandXmlCodec.Read(LandXmlCodec.Write(source));
        Assert.Equal("Test", result.Name);
        Assert.Equal(source.Points, result.Points);
        Assert.Equal(source.Triangles, result.Triangles);
    }
}
