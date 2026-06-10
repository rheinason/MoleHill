using System.Reflection;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildServicePadLockTests
{
    [Fact]
    public void CombinePadLockCurves_FiltersFarPersistentHardConstraints()
    {
        var localLocks = Array.Empty<PadGrader.LockCurve>();
        var persistentHardConstraints = new[]
        {
            CreateConstraint(0.5, -2.0, 0.5, 2.0),
            CreateConstraint(50.0, 50.0, 55.0, 50.0)
        };
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[] { -1.0, -1.0, 1.0, -1.0, 1.0, 1.0, -1.0, 1.0 },
                4,
                targetZ: 1.0,
                slopeAngleDeg: 80.0)
        };
        double[] terrainVertices =
        {
            -5.0, -5.0, 0.0,
            5.0, -5.0, 0.0,
            5.0, 5.0, 0.0,
            -5.0, 5.0, 0.0
        };

        var method = typeof(TerrainBuildService).GetMethod(
            "CombinePadLockCurves",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] args =
        {
            localLocks,
            persistentHardConstraints,
            pads,
            terrainVertices,
            4,
            0.1,
            0
        };

        var combined = Assert.IsType<PadGrader.LockCurve[]>(method!.Invoke(null, args));

        Assert.Single(combined);
        Assert.Equal(1, Assert.IsType<int>(args[6]));
    }

    private static SurfaceRemesher.ConstraintPolyline CreateConstraint(double ax, double ay, double bx, double by)
    {
        return new SurfaceRemesher.ConstraintPolyline(
            new[] { ax, ay, 0.0, bx, by, 0.0 },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);
    }
}
