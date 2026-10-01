using MoleHill.Core.Engine;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildServicePreservedElevationTests
{
    [Fact]
    public void ApplyPreservedConstraintElevations_ClosedRail_SetsVerticesOnItsClosingSegment()
    {
        // A closed square rail at 10 m, open in the point list (last point is not the first repeated), as
        // the wall planner emits ring walls. The closing segment runs from (0, 4) back to (0, 0).
        var rail = new SurfaceRemesher.ConstraintPolyline(
            new double[] { 0, 0, 10, 4, 0, 10, 4, 4, 10, 0, 4, 10 },
            4,
            IsClosed: true,
            PreserveInputElevation: true);
        double[] vertices =
        {
            2.0, 0.0, 0.0,   // first segment
            0.0, 2.0, 0.0,   // closing segment
            2.0, 2.0, 0.0    // inside, on no segment
        };

        TerrainBuildService.ApplyPreservedConstraintElevations(vertices, new[] { rail }, 0.005);

        Assert.Equal(10.0, vertices[2]);
        Assert.Equal(10.0, vertices[5]);
        Assert.Equal(0.0, vertices[8]);
    }

    [Fact]
    public void ApplyPreservedConstraintElevations_OpenRail_LeavesThePointsBetweenItsEndsAlone()
    {
        var rail = new SurfaceRemesher.ConstraintPolyline(
            new double[] { 0, 0, 10, 4, 0, 10, 4, 4, 10, 0, 4, 10 },
            4,
            IsClosed: false,
            PreserveInputElevation: true);
        double[] vertices = { 0.0, 2.0, 0.0 };

        TerrainBuildService.ApplyPreservedConstraintElevations(vertices, new[] { rail }, 0.005);

        Assert.Equal(0.0, vertices[2]);
    }
}
