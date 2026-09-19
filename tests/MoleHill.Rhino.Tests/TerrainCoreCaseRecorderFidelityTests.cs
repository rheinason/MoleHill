using System.Reflection;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A recorded case is only evidence if it replays the call that was made. These tests pin that: every
/// member of a <see cref="PathGrader.PathDefinition"/> survives recording, and so do the two arguments
/// that decide which grading tier runs (tolerance and preferSplitKeep).
///
/// This exists because the recorder silently dropped the per-side slope angles and
/// <c>OutwardNormals</c>. Losing the normals turns a one-sided retaining-wall rail into an ordinary
/// two-sided path, so a captured wall failure replayed as a different — and passing — problem.
/// </summary>
public class TerrainCoreCaseRecorderFidelityTests
{
    private static PathGrader.PathDefinition CreateDistinctWallRail()
    {
        // Every value distinct, so a member copied from the wrong source still fails.
        return new PathGrader.PathDefinition(
            xyVertices: new[] { 0.0, 0.0, 10.0, 0.0, 20.0, 5.0 },
            zValues: new[] { 1.5, 2.5, 3.5 },
            vertexCount: 3,
            width: 0.0,
            slopeAngleDeg: 31.0,
            maxDistance: 7.0,
            fillSlopeAngleDeg: 29.0,
            leftEdgeXy: null,
            rightEdgeXy: null,
            isClosed: false,
            leftCutSlopeAngleDeg: 41.0,
            leftFillSlopeAngleDeg: 43.0,
            rightCutSlopeAngleDeg: 47.0,
            rightFillSlopeAngleDeg: 53.0,
            outwardNormals: new[] { 0.0, 1.0, 0.0, 1.0, 0.0, 1.0 });
    }

    private static TerrainCorePathCaseRecord RecordRail(
        PathGrader.PathDefinition path,
        double modelTolerance = 0.01,
        bool preferSplitKeep = true)
    {
        var recorder = new TerrainCoreCaseRecorder();
        recorder.RecordPath(
            "Retaining Wall Rails",
            vertices: new[] { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 },
            vertexCount: 3,
            faces: new[] { 0, 1, 2 },
            faceCount: 1,
            paths: new[] { path },
            hardConstraints: Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            modelTolerance: modelTolerance,
            preferSplitKeep: preferSplitKeep,
            succeeded: true,
            resultVertexCount: 3,
            resultFaceCount: 1,
            message: null);

        return Assert.IsType<TerrainCorePathCaseRecord>(Assert.Single(recorder.Records));
    }

    [Fact]
    public void RecordPath_WallRail_PreservesOutwardNormals()
    {
        PathGrader.PathDefinition original = CreateDistinctWallRail();

        PathGrader.PathDefinition recorded = Assert.Single(RecordRail(original).Paths);

        // The normals are what make the rail one-sided: PathDefinition derives its outward side sign
        // from them, and a null array means "grade both sides". Losing them is a silent downgrade.
        Assert.NotNull(recorded.OutwardNormals);
        Assert.Equal(original.OutwardNormals!, recorded.OutwardNormals!);
    }

    [Fact]
    public void RecordPath_WallRail_PreservesPerSideSlopeAngles()
    {
        PathGrader.PathDefinition original = CreateDistinctWallRail();

        PathGrader.PathDefinition recorded = Assert.Single(RecordRail(original).Paths);

        Assert.Equal(original.LeftCutSlopeAngleDeg, recorded.LeftCutSlopeAngleDeg);
        Assert.Equal(original.LeftFillSlopeAngleDeg, recorded.LeftFillSlopeAngleDeg);
        Assert.Equal(original.RightCutSlopeAngleDeg, recorded.RightCutSlopeAngleDeg);
        Assert.Equal(original.RightFillSlopeAngleDeg, recorded.RightFillSlopeAngleDeg);
    }

    [Fact]
    public void RecordPath_PreservesToleranceAndSplitKeepChoice()
    {
        TerrainCorePathCaseRecord record = RecordRail(
            CreateDistinctWallRail(),
            modelTolerance: 0.002,
            preferSplitKeep: true);

        // Both default on PathGrader.Grade, so an unrecorded value replays as a different tier.
        Assert.Equal(0.002, record.ModelTolerance);
        Assert.True(record.PreferSplitKeep);
        Assert.NotEqual(GradingTolerances.DefaultModelTolerance, record.ModelTolerance);
    }

    [Fact]
    public void RecordPath_ClonesArraysSoLaterMutationCannotRewriteHistory()
    {
        var normals = new[] { 0.0, 1.0, 0.0, 1.0, 0.0, 1.0 };
        var xy = new[] { 0.0, 0.0, 10.0, 0.0, 20.0, 5.0 };
        var path = new PathGrader.PathDefinition(
            xy, new[] { 1.5, 2.5, 3.5 }, 3, 0.0,
            outwardNormals: normals);

        TerrainCorePathCaseRecord record = RecordRail(path);
        normals[1] = -1.0;
        xy[0] = 999.0;

        PathGrader.PathDefinition recorded = Assert.Single(record.Paths);
        Assert.Equal(1.0, recorded.OutwardNormals![1]);
        Assert.Equal(0.0, recorded.XyVertices[0]);
    }

    /// <summary>
    /// The guard that catches the next added member. A new public property on PathDefinition that the
    /// recorder does not copy is exactly the failure this file exists for, and it is invisible to the
    /// tests above, which can only check members that existed when they were written.
    /// </summary>
    [Fact]
    public void RecordPath_CopiesEveryPublicPathDefinitionMember()
    {
        PathGrader.PathDefinition original = CreateDistinctWallRail();
        PathGrader.PathDefinition recorded = Assert.Single(RecordRail(original).Paths);

        PropertyInfo[] properties = typeof(PathGrader.PathDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        var mismatched = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            object? expected = property.GetValue(original);
            object? actual = property.GetValue(recorded);

            bool equal = (expected, actual) switch
            {
                (null, null) => true,
                (double[] a, double[] b) => a.SequenceEqual(b),
                (null, _) or (_, null) => false,
                var (a, b) => a.Equals(b)
            };

            if (!equal)
                mismatched.Add(property.Name);
        }

        Assert.True(
            mismatched.Count == 0,
            $"TerrainCoreCaseRecorder.ClonePaths dropped: {string.Join(", ", mismatched)}. " +
            "Add the member to ClonePaths AND to TerrainCoreCaseTestExporter.AppendPathArray, " +
            "or a recorded case will replay a different grade than the one captured.");
    }
}
