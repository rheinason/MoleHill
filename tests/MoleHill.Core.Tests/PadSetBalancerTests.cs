using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class PadSetBalancerTests
{
    // Terrain rising 1 in 10 along X over 200 x 100, on a 10 m grid.
    private static IndexedTriMesh SlopedTerrain()
    {
        const int nx = 21, ny = 11;
        var vertices = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = j * nx + i;
                vertices[v * 3] = i * 10.0;
                vertices[v * 3 + 1] = j * 10.0;
                vertices[v * 3 + 2] = i * 1.0;
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx + 1, d = a + nx;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        return new IndexedTriMesh(vertices, nx * ny, faces.ToArray(), faces.Count / 3);
    }

    private static PadGrader.PadBoundary Square(double x0, double y0, double size, double z) => new(
        new[] { x0, y0, x0 + size, y0, x0 + size, y0 + size, x0, y0 + size }, 4, z, slopeAngleDeg: 33.0);

    [Fact]
    public void Balance_EachSeparatePad_BalancesEveryPadOnItsOwn()
    {
        IndexedTriMesh terrain = SlopedTerrain();
        var pads = new[] { Square(30, 40, 20, 2.0), Square(140, 40, 20, 20.0) };

        PadSetBalanceResult result = PadSetBalancer.Balance(
            terrain, pads, null, null, PadBalanceMode.Each, targetNet: 0.0, new[] { 0.4, 0.4 }, modelTolerance: 0.001);

        Assert.Equal("Converged", result.Status);
        for (int k = 0; k < 2; k++)
            Assert.True(Math.Abs(result.PadCut[k] - result.PadFill[k]) <= 0.4, $"pad {k + 1} net {result.PadCut[k] - result.PadFill[k]}");
        // On a uniform slope a pad balances near the ground at its centre: x = 40 -> 4, x = 150 -> 15.
        Assert.Equal(4.0, 2.0 + result.Offsets[0], 0);
        Assert.Equal(15.0, 20.0 + result.Offsets[1], 0);
    }

    [Fact]
    public void Balance_Together_MovesEveryPadByOneAmountToBalanceTheTotal()
    {
        IndexedTriMesh terrain = SlopedTerrain();
        var pads = new[] { Square(30, 40, 20, 2.0), Square(140, 40, 20, 20.0) };

        PadSetBalanceResult result = PadSetBalancer.Balance(
            terrain, pads, null, null, PadBalanceMode.Together, targetNet: 0.0, new[] { 0.4, 0.4 }, modelTolerance: 0.001);

        Assert.Equal("Converged", result.Status);
        Assert.Equal(result.Offsets[0], result.Offsets[1]);
        Assert.True(Math.Abs(result.Net) <= 0.8, $"net {result.Net}");
        // The first pad starts 2 low and the second 5 high, so one shared move leaves them unbalanced apart.
        Assert.True(Math.Abs(result.PadCut[0] - result.PadFill[0]) > 10.0);
    }

    [Fact]
    public void Balance_FixedPad_IsGradedButNotCounted()
    {
        IndexedTriMesh terrain = SlopedTerrain();
        var moving = new[] { Square(30, 40, 20, 2.0) };
        var held = new[] { Square(140, 40, 20, 30.0) };

        PadSetBalanceResult result = PadSetBalancer.Balance(
            terrain, moving, held, null, PadBalanceMode.Together, targetNet: 0.0, new[] { 0.4 }, modelTolerance: 0.001);

        Assert.Equal("Converged", result.Status);
        Assert.True(Math.Abs(result.Net) <= 0.4);
    }
}
