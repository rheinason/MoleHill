using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class TerrainFaceGridTests
{
    [Fact]
    public void TryFindRayDaylightReach_GridCandidates_MatchLinearTraversal()
    {
        const int columns = 24;
        const int rows = 18;
        var vertices = new double[(columns + 1) * (rows + 1) * 3];
        for (int y = 0; y <= rows; y++)
        {
            for (int x = 0; x <= columns; x++)
            {
                int vertex = (y * (columns + 1)) + x;
                vertices[vertex * 3] = x;
                vertices[vertex * 3 + 1] = y;
                vertices[vertex * 3 + 2] =
                    (Math.Sin(x * 0.37) * 1.7) + (Math.Cos(y * 0.29) * 1.2);
            }
        }

        var faces = new int[columns * rows * 6];
        int nextFace = 0;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int a = (y * (columns + 1)) + x;
                int b = a + 1;
                int c = a + columns + 1;
                int d = c + 1;
                faces[nextFace++] = a;
                faces[nextFace++] = b;
                faces[nextFace++] = d;
                faces[nextFace++] = a;
                faces[nextFace++] = d;
                faces[nextFace++] = c;
            }
        }

        var terrain = new TerrainFaceGrid(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3);
        var random = new Random(731942);

        for (int query = 0; query < 1_000; query++)
        {
            double edgeX = -2.0 + (random.NextDouble() * (columns + 4.0));
            double edgeY = -2.0 + (random.NextDouble() * (rows + 4.0));
            double angle = random.NextDouble() * Math.PI * 2.0;
            double dirX = Math.Cos(angle);
            double dirY = Math.Sin(angle);
            double edgeZ = -4.0 + (random.NextDouble() * 12.0);
            double slopeRatio = 0.05 + (random.NextDouble() * 1.5);
            double branchSign = random.Next(2) == 0 ? -1.0 : 1.0;
            double maxReach = 0.1 + (random.NextDouble() * 32.0);

            bool expected = terrain.TryFindRayDaylightReachLinearForDiagnostics(
                edgeX,
                edgeY,
                edgeZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                maxReach,
                out double expectedReach,
                out double expectedBestApproach);
            bool actual = terrain.TryFindRayDaylightReach(
                edgeX,
                edgeY,
                edgeZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                maxReach,
                out double actualReach,
                out double actualBestApproach);

            Assert.Equal(expected, actual);
            Assert.Equal(expectedReach, actualReach, 10);
            Assert.Equal(expectedBestApproach, actualBestApproach, 10);
        }

        bool expectedPathological = terrain.TryFindRayDaylightReachLinearForDiagnostics(
            edgeX: -1_000_000.0,
            edgeY: rows * 0.5,
            edgeZ: 500.0,
            dirX: 1.0,
            dirY: 0.0,
            slopeRatio: 0.5,
            branchSign: 1.0,
            maxReach: 2_000_000.0,
            out double expectedPathologicalReach,
            out double expectedPathologicalBestApproach);
        bool actualPathological = terrain.TryFindRayDaylightReach(
            edgeX: -1_000_000.0,
            edgeY: rows * 0.5,
            edgeZ: 500.0,
            dirX: 1.0,
            dirY: 0.0,
            slopeRatio: 0.5,
            branchSign: 1.0,
            maxReach: 2_000_000.0,
            out double actualPathologicalReach,
            out double actualPathologicalBestApproach);

        Assert.Equal(expectedPathological, actualPathological);
        Assert.Equal(expectedPathologicalReach, actualPathologicalReach, 10);
        Assert.Equal(expectedPathologicalBestApproach, actualPathologicalBestApproach, 10);
    }
}
