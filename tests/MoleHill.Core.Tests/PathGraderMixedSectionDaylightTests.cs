using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A section whose ground starts just below the road edge and then rises above the road — a low verge
/// before a raised terrace — with a kerb (two hard constraints) behind it. The section's dominant
/// difference says cut, but only a fill batter can meet the ground, since the ground starts below the
/// road. Reduced from a field case where the cut batter never daylighted, was capped at the kerb, and
/// stood up to 5 m above the terrace in a comb of near-vertical slivers.
/// </summary>
public class PathGraderMixedSectionDaylightTests
{
    private const double Spacing = 0.25;
    private const double MinX = -12.0;
    private const double MaxX = 12.0;
    private const double MinY = -6.0;
    private const double MaxY = 8.0;
    private const double KerbY = 4.5;

    [Fact]
    public void Grade_GroundBelowEdgeThenAboveRoadBeforeKerb_DoesNotRaiseTerraceToKerb()
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = BuildTerrain();
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { -8.0, 0.0, 8.0, 0.0 },
            zValues: new[] { 0.0, 0.0 },
            vertexCount: 2,
            width: 1.5,
            slopeAngleDeg: 45.0);
        var kerb = new[]
        {
            new ConstraintPolyline(new[] { MinX, KerbY, 0.5, MaxX, KerbY, 0.5 }, 2, false, true),
            new ConstraintPolyline(new[] { MinX, KerbY + 0.1, 0.4, MaxX, KerbY + 0.1, 0.4 }, 2, false, true)
        };

        GradeOutcome outcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            Paths = new[] { path },
            HardConstraints = kerb,
            ModelTolerance = 0.0015,
            PreferSplitKeep = true
        });

        GradingResult? result = outcome.Result;
        Assert.NotNull(result);

        // Beyond the short fill batter at the road edge nothing should move; the cut batter capped at
        // the kerb raised the terrace 3.75 m (45° over the 3.75 m from road edge to kerb).
        double worstRise = 0.0;
        for (int i = 0; i < result!.VertexCount; i++)
        {
            double x = result.Vertices[i * 3];
            double y = result.Vertices[i * 3 + 1];
            if (Math.Abs(x) > 6.0 || y < 1.2 || y > KerbY - 0.1)
                continue;

            worstRise = Math.Max(worstRise, result.Vertices[i * 3 + 2] - GroundZ(y));
        }

        Assert.True(worstRise < 0.05, $"Terrace raised {worstRise:F3} above the ground between road and kerb.");
    }

    /// <summary>0.13 below the road at its edge, rising to 0.5 above it at y = 2.5, flat to the kerb.</summary>
    private static double GroundZ(double y)
    {
        if (y <= 1.0)
            return y >= 0.0 ? -0.13 : -0.5;
        if (y <= 2.5)
            return -0.13 + ((y - 1.0) / 1.5 * 0.63);
        return y <= KerbY ? 0.5 : 0.4;
    }

    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) BuildTerrain()
    {
        int columns = (int)Math.Round((MaxX - MinX) / Spacing) + 1;
        int rows = (int)Math.Round((MaxY - MinY) / Spacing) + 1;
        var vertices = new double[columns * rows * 3];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = (row * columns) + column;
                double y = MinY + (row * Spacing);
                vertices[index * 3] = MinX + (column * Spacing);
                vertices[index * 3 + 1] = y;
                vertices[index * 3 + 2] = GroundZ(y);
            }
        }

        var faces = new int[(columns - 1) * (rows - 1) * 6];
        int face = 0;
        for (int row = 0; row < rows - 1; row++)
        {
            for (int column = 0; column < columns - 1; column++)
            {
                int a = (row * columns) + column;
                int b = a + 1;
                int c = a + columns + 1;
                int d = a + columns;
                faces[face++] = a; faces[face++] = b; faces[face++] = c;
                faces[face++] = a; faces[face++] = c; faces[face++] = d;
            }
        }

        return (vertices, columns * rows, faces, faces.Length / 3);
    }
}
