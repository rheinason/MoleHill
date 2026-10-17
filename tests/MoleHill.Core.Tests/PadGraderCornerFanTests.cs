using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A batter point in a pad's corner sector takes the section along its own direction from the corner,
/// interpolated across the corner fan, not whichever adjacent edge's section floating-point noise chose.
/// </summary>
public sealed class PadGraderCornerFanTests
{
    private const double Rise = 0.47;

    // A plane rising 0.47 to the north, on a 10 m grid.
    private static IndexedTriMesh NorthRisingTerrain()
    {
        const int cells = 22;
        const double origin = -100.0, spacing = 10.0;
        int side = cells + 1;
        var vertices = new double[side * side * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                int v = j * side + i;
                double y = origin + j * spacing;
                vertices[v * 3] = origin + i * spacing;
                vertices[v * 3 + 1] = y;
                vertices[v * 3 + 2] = Rise * y;
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                int a = j * side + i, b = a + 1, c = a + side + 1, d = a + side;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        return new IndexedTriMesh(vertices, side * side, faces.ToArray(), faces.Count / 3);
    }

    [Fact]
    public void Grade_CutCornerBetweenLongAndShortReaches_GradesTheWholeCornerSector()
    {
        // The pad's north edge cuts ~9.4 into ground rising at 0.47 under a 0.65 batter, so it daylights
        // ~52 out; the east edge meets ground level along +X, so it daylights ~14.5 out. A point between,
        // near north, lies on the batter well past 14.5. It used to be left at the original ground whenever
        // the east edge's shoulder won a distance tie.
        IndexedTriMesh terrain = NorthRisingTerrain();
        double ratio = Math.Tan(33.0 * Math.PI / 180.0);
        int checkedPoints = 0;

        for (int step = 0; step < 16; step++)
        {
            double z = step * 0.013;
            var pad = new PadGrader.PadBoundary(new[] { 0.0, 0.0, 20.0, 0.0, 20.0, 20.0, 0.0, 20.0 }, 4, z, slopeAngleDeg: 33.0);
            GradingResult result = PadGrader.Grade(new PadGradeRequest
            {
                Terrain = terrain,
                Pads = new[] { pad },
                ModelTolerance = 0.001,
            }).Result ?? throw new InvalidOperationException("Grade failed.");

            for (int v = 0; v < result.VertexCount; v++)
            {
                double x = result.Vertices[v * 3] - 20.0, y = result.Vertices[v * 3 + 1] - 20.0;
                if (x <= 0.5 || y <= 0.5)
                    continue;
                double distance = Math.Sqrt(x * x + y * y);
                double cosFromNorth = y / distance;
                // Along this direction the batter meets the ground at depth / (ratio - rise * cos).
                double reach = (Rise * 20.0 - z) / (ratio - Rise * cosFromNorth);
                if (distance < 16.0 || distance > 0.8 * reach)
                    continue;

                double ground = Rise * result.Vertices[v * 3 + 1];
                double batter = z + ratio * distance;
                checkedPoints++;
                Assert.True(Math.Abs(result.Vertices[v * 3 + 2] - batter) < 0.05,
                    $"pad at {z:F3}: corner point {distance:F1} out sits at {result.Vertices[v * 3 + 2]:F2}; batter {batter:F2}, ground {ground:F2}");
            }
        }

        Assert.True(checkedPoints > 0, "No batter point fell in the corner sector to check.");
    }
}
