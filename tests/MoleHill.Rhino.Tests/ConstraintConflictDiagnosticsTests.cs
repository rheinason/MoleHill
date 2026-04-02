using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class ConstraintConflictDiagnosticsTests
{
    [Fact]
    public void Analyze_CrossingSegments_ReportsIntersection()
    {
        var pathConstraints = new[]
        {
            CreatePolyline(0.0, 0.0, 10.0, 10.0)
        };
        var hardConstraints = new[]
        {
            CreatePolyline(0.0, 10.0, 10.0, 0.0)
        };

        var summary = ConstraintConflictDiagnostics.Analyze(pathConstraints, hardConstraints, 1e-6);

        Assert.Equal(1, summary.IntersectionCount);
        Assert.Equal(0, summary.OverlapCount);
        Assert.True(summary.HasConflicts);
        Assert.Single(summary.Samples);
        Assert.Equal("intersection", summary.Samples[0].Kind);
    }

    [Fact]
    public void Analyze_CollinearOverlap_ReportsOverlap()
    {
        var pathConstraints = new[]
        {
            CreatePolyline(0.0, 0.0, 10.0, 0.0)
        };
        var hardConstraints = new[]
        {
            CreatePolyline(4.0, 0.0, 12.0, 0.0)
        };

        var summary = ConstraintConflictDiagnostics.Analyze(pathConstraints, hardConstraints, 1e-6);

        Assert.Equal(0, summary.IntersectionCount);
        Assert.Equal(1, summary.OverlapCount);
        Assert.True(summary.HasConflicts);
        Assert.Single(summary.Samples);
        Assert.Equal("overlap", summary.Samples[0].Kind);
    }

    [Fact]
    public void Analyze_SharedEndpointOnly_DoesNotReportConflict()
    {
        var pathConstraints = new[]
        {
            CreatePolyline(0.0, 0.0, 10.0, 0.0)
        };
        var hardConstraints = new[]
        {
            CreatePolyline(10.0, 0.0, 10.0, 10.0)
        };

        var summary = ConstraintConflictDiagnostics.Analyze(pathConstraints, hardConstraints, 1e-6);

        Assert.Equal(0, summary.IntersectionCount);
        Assert.Equal(0, summary.OverlapCount);
        Assert.False(summary.HasConflicts);
        Assert.Empty(summary.Samples);
    }

    private static ConstraintConflictDiagnostics.PolylineData CreatePolyline(params double[] xy)
    {
        var points = new double[(xy.Length / 2) * 3];
        for (int i = 0; i < xy.Length / 2; i++)
        {
            points[i * 3] = xy[i * 2];
            points[i * 3 + 1] = xy[i * 2 + 1];
            points[i * 3 + 2] = 0.0;
        }

        return new ConstraintConflictDiagnostics.PolylineData(points, xy.Length / 2, IsClosed: false);
    }
}
