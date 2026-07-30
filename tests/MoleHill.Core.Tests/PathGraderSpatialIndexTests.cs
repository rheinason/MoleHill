using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PathGraderSpatialIndexTests
{
    [Fact]
    public void ClosestPathSegment_GridCandidates_MatchLinearTraversal()
    {
        const int vertexCount = 1_024;
        const int queryCount = 5_000;
        const double maxDistance = 12.0;
        var pathXy = new double[vertexCount * 2];
        var pathZ = new double[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            double fraction = vertex / (double)(vertexCount - 1);
            pathXy[vertex * 2] = fraction * 1_000.0;
            pathXy[vertex * 2 + 1] =
                (Math.Sin(fraction * Math.PI * 8.0) * 3.0) +
                (Math.Sin(fraction * Math.PI * 31.0) * 0.2);
            pathZ[vertex] = 20.0 + (fraction * 8.0);
        }

        var queryXy = new double[queryCount * 2];
        var random = new Random(982451);
        for (int query = 0; query < queryCount; query++)
        {
            double fraction = (query + 0.5) / queryCount;
            double centerY =
                (Math.Sin(fraction * Math.PI * 8.0) * 3.0) +
                (Math.Sin(fraction * Math.PI * 31.0) * 0.2);
            queryXy[query * 2] = fraction * 1_000.0;
            queryXy[query * 2 + 1] =
                centerY + ((random.NextDouble() - 0.5) * maxDistance);
        }

        double expected = PathGrader.RunClosestPathQueriesForDiagnostics(
            pathXy,
            pathZ,
            vertexCount,
            queryXy,
            queryCount);
        double actual = PathGrader.RunIndexedClosestPathQueriesForDiagnostics(
            pathXy,
            pathZ,
            vertexCount,
            queryXy,
            queryCount,
            maxDistance);

        Assert.Equal(expected, actual);
    }
}
