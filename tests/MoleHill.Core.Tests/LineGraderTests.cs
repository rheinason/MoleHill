using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A "grade line" is the corridor grader at width zero: the drawn curve is the footprint and the
/// batters run away from it. These tests pin the properties that distinguish it from a corridor —
/// one design line rather than two road edges, and a slope that can differ on each side.
/// </summary>
public class LineGraderTests
{
    private static (double[] v, int vc, int[] f, int fc) FlatTerrain(double z, double planScale = 1.0)
    {
        double e = 60.0 * planScale;
        double[] v = { -e, -e, z, e, -e, z, e, e, z, -e, e, z };
        int[] f = { 0, 1, 2, 0, 2, 3 };
        return (v, 4, f, 2);
    }

    /// <summary>Terrain rising in +X, so a line along Y cuts on one side and fills on the other.</summary>
    private static (double[] v, int vc, int[] f, int fc) TerrainSlopedInX(double zAtMinX, double zAtMaxX)
    {
        double[] v =
        {
            -60, -60, zAtMinX,
             60, -60, zAtMaxX,
             60,  60, zAtMaxX,
            -60,  60, zAtMinX
        };
        int[] f = { 0, 1, 2, 0, 2, 3 };
        return (v, 4, f, 2);
    }

    private static PathGrader.PathDefinition Line(
        double[] xy,
        double[] z,
        double slopeAngleDeg = 45.0,
        double fillSlopeAngleDeg = 0.0,
        double maxDistance = 0.0,
        double leftCut = 0.0,
        double leftFill = 0.0,
        double rightCut = 0.0,
        double rightFill = 0.0) =>
        new(
            xyVertices: xy,
            zValues: z,
            vertexCount: z.Length,
            width: 0.0,
            slopeAngleDeg: slopeAngleDeg,
            maxDistance: maxDistance,
            fillSlopeAngleDeg: fillSlopeAngleDeg,
            leftCutSlopeAngleDeg: leftCut,
            leftFillSlopeAngleDeg: leftFill,
            rightCutSlopeAngleDeg: rightCut,
            rightFillSlopeAngleDeg: rightFill);

    /// <summary>Mean absolute slope of faces whose centroid sits on one side of the y=0 line.</summary>
    private static double MeanSlopeDegreesOnSide(GradingResult result, int sideSign, double minAbsY, double maxAbsY)
    {
        double total = 0.0;
        int count = 0;
        for (int fi = 0; fi < result.FaceCount; fi++)
        {
            int a = result.Faces[fi * 3], b = result.Faces[(fi * 3) + 1], c = result.Faces[(fi * 3) + 2];
            double cy = (result.Vertices[(a * 3) + 1] + result.Vertices[(b * 3) + 1] + result.Vertices[(c * 3) + 1]) / 3.0;
            double cx = (result.Vertices[a * 3] + result.Vertices[b * 3] + result.Vertices[c * 3]) / 3.0;
            if (Math.Abs(cx) > 12.0)
                continue;
            if (Math.Sign(cy) != sideSign)
                continue;
            if (Math.Abs(cy) < minAbsY || Math.Abs(cy) > maxAbsY)
                continue;

            double ux = result.Vertices[b * 3] - result.Vertices[a * 3];
            double uy = result.Vertices[(b * 3) + 1] - result.Vertices[(a * 3) + 1];
            double uz = result.Vertices[(b * 3) + 2] - result.Vertices[(a * 3) + 2];
            double vx = result.Vertices[c * 3] - result.Vertices[a * 3];
            double vy = result.Vertices[(c * 3) + 1] - result.Vertices[(a * 3) + 1];
            double vz = result.Vertices[(c * 3) + 2] - result.Vertices[(a * 3) + 2];
            double nx = (uy * vz) - (uz * vy);
            double ny = (uz * vx) - (ux * vz);
            double nz = (ux * vy) - (uy * vx);
            double horizontal = Math.Sqrt((nx * nx) + (ny * ny));
            if (horizontal <= 1e-12 && Math.Abs(nz) <= 1e-12)
                continue;

            total += Math.Atan2(horizontal, Math.Abs(nz)) * 180.0 / Math.PI;
            count++;
        }

        Assert.True(count > 0, $"No faces sampled on side {sideSign}.");
        return total / count;
    }

    [Fact]
    public void Grade_LineBelowFlatTerrain_CutsBothSidesAndStaysWatertight()
    {
        var terrain = FlatTerrain(10.0);

        // A line at z=0, ten below terrain, with 45 degree batters: reach is ~10 either side.
        var paths = new[] { Line(new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 0.0, 0.0 }) };

        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome.ErrorMessage;
        GradingResult? result = gradeOutcome.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        // The design line keeps its authored elevation.
        AssertVertexNear(result, -10.0, 0.0, 0.0);
        AssertVertexNear(result, 10.0, 0.0, 0.0);

        // Both sides were cut down from terrain level.
        Assert.True(MinZNearLine(result) < 0.5, "The line did not carve the terrain.");
    }

    [Fact]
    public void Grade_LineAboveFlatTerrain_FillsBothSides()
    {
        var terrain = FlatTerrain(0.0);
        var paths = new[] { Line(new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 6.0, 6.0 }) };

        GradeOutcome gradeOutcome2 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome2.ErrorMessage;
        GradingResult? result = gradeOutcome2.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);
        AssertVertexNear(result, -20.0, 0.0, 6.0);
        AssertVertexNear(result, 20.0, 0.0, 6.0);
        Assert.True(result.FillVolume > 0.0, "A line above ground must produce fill.");
    }

    [Fact]
    public void Grade_SingleLine_PublishesTheDesignLineOnce()
    {
        var terrain = FlatTerrain(10.0);
        var paths = new[] { Line(new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 0.0, 0.0 }) };

        GradeOutcome gradeOutcome3 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome3.ErrorMessage;
        GradingResult? result = gradeOutcome3.Result;

        Assert.True(result != null, errorMessage);

        // A corridor publishes two road edges; a line has one design line, and publishing it twice
        // would install the same breakline as two persistent constraints downstream.
        Assert.Single(result!.OutputPolylines);
    }

    [Fact]
    public void Grade_AsymmetricSides_ProducesDifferentBattersLeftAndRight()
    {
        var terrain = FlatTerrain(10.0);

        // Same cut on both sides of the branch, but a different angle per side.
        var paths = new[]
        {
            Line(
                new[] { -20.0, 0.0, 20.0, 0.0 },
                new[] { 0.0, 0.0 },
                slopeAngleDeg: 45.0,
                leftCut: 60.0,
                rightCut: 20.0)
        };

        GradeOutcome gradeOutcome4 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome4.ErrorMessage;
        GradingResult? result = gradeOutcome4.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        double steep = MeanSlopeDegreesOnSide(result, sideSign: 1, minAbsY: 1.0, maxAbsY: 4.0);
        double shallow = MeanSlopeDegreesOnSide(result, sideSign: -1, minAbsY: 1.0, maxAbsY: 4.0);

        Assert.True(
            steep > shallow + 15.0,
            $"Expected an asymmetric section; measured left {steep:F1} deg and right {shallow:F1} deg. " +
            string.Join(" | ", result.Diagnostics));
    }

    [Fact]
    public void HasAsymmetricSides_BothSidesOverriddenToTheSameAngle_IsTrue()
    {
        PathGrader.PathDefinition overridden = Line(
            new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 0.0, 0.0 }, slopeAngleDeg: 45.0, leftCut: 20.0, rightCut: 20.0);
        PathGrader.PathDefinition plain = Line(new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 0.0, 0.0 }, slopeAngleDeg: 45.0);

        Assert.True(overridden.HasAsymmetricSides);
        Assert.False(plain.HasAsymmetricSides);
    }

    /// <summary>
    /// Both sides overridden to one angle must grade exactly like that angle set as the shared pair.
    /// The daylight envelope used to be ray-marched at the shared angle while the sections graded at
    /// the override, so the carve and the batter disagreed.
    /// </summary>
    [Fact]
    public void Grade_BothSidesOverriddenToTheSameAngle_MatchesThatSharedAngle()
    {
        var terrain = FlatTerrain(10.0);
        double[] xy = { -20.0, 0.0, 20.0, 0.0 };
        double[] z = { 0.0, 0.0 };

        GradeOutcome gradeOutcome5 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = new[] { Line(xy, z, slopeAngleDeg: 45.0, leftCut: 20.0, rightCut: 20.0) },
        });
        string? overriddenError = gradeOutcome5.ErrorMessage;
        GradingResult? overridden = gradeOutcome5.Result;
        GradeOutcome gradeOutcome6 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = new[] { Line(xy, z, slopeAngleDeg: 20.0) },
        });
        string? sharedError = gradeOutcome6.ErrorMessage;
        GradingResult? shared = gradeOutcome6.Result;

        Assert.True(overridden != null, overriddenError);
        Assert.True(shared != null, sharedError);
        PadInvariantAssert.AssertWatertightManifold(overridden!);
        Assert.True(
            Math.Abs(overridden.CutVolume - shared!.CutVolume) <= 0.01 * shared.CutVolume,
            $"Overridden sides cut {overridden.CutVolume:F1}; the same shared angle cuts {shared.CutVolume:F1}.");
    }

    [Fact]
    public void Grade_LineAcrossSlopingTerrain_UsesCutOnOneSideAndFillOnTheOther()
    {
        // Terrain rises from z=0 at x=-60 to z=20 at x=+60, so a line along X at a constant z=10
        // fills the low end and cuts the high end. Each branch must use its own angle.
        var terrain = TerrainSlopedInX(0.0, 20.0);
        var paths = new[]
        {
            Line(
                new[] { -30.0, 0.0, 30.0, 0.0 },
                new[] { 10.0, 10.0 },
                slopeAngleDeg: 60.0,
                fillSlopeAngleDeg: 20.0)
        };

        GradeOutcome gradeOutcome7 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome7.ErrorMessage;
        GradingResult? result = gradeOutcome7.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);
        Assert.True(result!.CutVolume > 0.0, "The high end should cut.");
        Assert.True(result.FillVolume > 0.0, "The low end should fill.");
    }

    [Fact]
    public void Grade_LineAtTerrainElevation_ProducesNoBatter()
    {
        // "This side needs no grading" is a measurement, not a switch: a line already sitting on the
        // ground has nothing to batter to, and the terrain is left alone.
        var terrain = FlatTerrain(5.0);
        var paths = new[] { Line(new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 5.0, 5.0 }) };

        GradeOutcome gradeOutcome8 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome8.ErrorMessage;
        GradingResult? result = gradeOutcome8.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        for (int i = 0; i < result!.VertexCount; i++)
            Assert.True(Math.Abs(result.Vertices[(i * 3) + 2] - 5.0) < 1e-6, "A flat line moved the terrain.");
    }

    [Fact]
    public void Grade_MaxDistance_ClampsTheBatterReach()
    {
        var terrain = FlatTerrain(50.0);

        // A 45 degree batter would reach 50 either side; cap it at 5.
        var paths = new[] { Line(new[] { -20.0, 0.0, 20.0, 0.0 }, new[] { 0.0, 0.0 }, maxDistance: 5.0) };

        GradeOutcome gradeOutcome9 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome9.ErrorMessage;
        GradingResult? result = gradeOutcome9.Result;

        Assert.True(result != null, errorMessage);

        // Nothing beyond the cap (plus the end-arc sweep) may have moved off terrain level.
        for (int i = 0; i < result!.VertexCount; i++)
        {
            double y = result.Vertices[(i * 3) + 1];
            double z = result.Vertices[(i * 3) + 2];
            if (Math.Abs(y) > 8.0 && Math.Abs(result.Vertices[i * 3]) < 15.0)
                Assert.True(Math.Abs(z - 50.0) < 1e-6, $"Vertex at y={y:F2} moved despite the max-distance cap.");
        }
    }

    [Fact]
    public void Grade_SingleLineWithVariableWidthRails_IsRejected()
    {
        var terrain = FlatTerrain(10.0);
        var paths = new[]
        {
            new PathGrader.PathDefinition(
                xyVertices: new[] { -20.0, 0.0, 20.0, 0.0 },
                zValues: new[] { 0.0, 0.0 },
                vertexCount: 2,
                width: 0.0,
                leftEdgeXy: new[] { -20.0, 2.0, 20.0, 2.0 },
                rightEdgeXy: new[] { -20.0, -2.0, 20.0, -2.0 })
        };

        GradeOutcome gradeOutcome10 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome10.ErrorMessage;
        GradingResult? result = gradeOutcome10.Result;

        Assert.Null(result);
        Assert.Contains("single-line", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(25.4)]
    public void Grade_UniformlyScaledLine_PreservesThePhysicalResult(double scale)
    {
        var terrain = FlatTerrain(10.0 * scale, planScale: scale);
        var paths = new[]
        {
            Line(
                new[] { -20.0 * scale, 0.0, 20.0 * scale, 0.0 },
                new[] { 0.0, 0.0 },
                slopeAngleDeg: 45.0)
        };

        GradeOutcome gradeOutcome11 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
            ModelTolerance = 1e-3 * scale,
        });
        string? errorMessage = gradeOutcome11.ErrorMessage;
        GradingResult? result = gradeOutcome11.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        // Cut volume scales with the cube of a uniform scale.
        double normalized = result.CutVolume / (scale * scale * scale);
        Assert.True(normalized > 0.0, "Scaled line produced no cut. " + string.Join(" | ", result.Diagnostics));
    }

    /// <summary>
    /// A rail carrying explicit outward normals grades one way only — the retaining-wall case. The
    /// far side of the rail must be left for the wall itself, not battered through.
    /// </summary>
    [Fact]
    public void Grade_OneSidedRail_BattersOnlyTheSideItsNormalsPointTo()
    {
        var terrain = FlatTerrain(10.0);

        // Rail along X at z=0; normals point to +Y, so only +Y should be carved.
        const int n = 9;
        var xy = new double[n * 2];
        var z = new double[n];
        var normals = new double[n * 2];
        for (int i = 0; i < n; i++)
        {
            xy[i * 2] = -20.0 + (i * 5.0);
            xy[(i * 2) + 1] = 0.0;
            z[i] = 0.0;
            normals[i * 2] = 0.0;
            normals[(i * 2) + 1] = 1.0;
        }

        var paths = new[]
        {
            new PathGrader.PathDefinition(
                xy, z, n,
                width: 0.0,
                slopeAngleDeg: 45.0,
                outwardNormals: normals)
        };

        GradeOutcome gradeOutcome12 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome12.ErrorMessage;
        GradingResult? result = gradeOutcome12.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        // Nothing below the rail may have moved off terrain level.
        for (int i = 0; i < result.VertexCount; i++)
        {
            double y = result.Vertices[(i * 3) + 1];
            double zv = result.Vertices[(i * 3) + 2];
            if (y < -1.0)
                Assert.True(Math.Abs(zv - 10.0) < 1e-6, $"Vertex at y={y:F2} was graded on the wall side.");
        }

        // And the graded side actually moved. A planar 45-degree batter needs no vertices between the
        // rail and its daylight line, so the evidence is the volume and the rail holding its elevation
        // — not an intermediate vertex that a correct result is free not to have.
        Assert.True(result.CutVolume > 0.0, "The one-sided rail cut nothing. " + string.Join(" | ", result.Diagnostics));
        AssertVertexNear(result, 0.0, 0.0, 0.0);
    }

    /// <summary>
    /// The retaining-wall fill case, and a live-found regression. A one-sided rail *above* existing
    /// ground must keep its authored elevation: the explicit tier used to weld the rail row down to
    /// terrain level while building a correct batter above it, so the batter looked right and the rail
    /// silently flattened. Cut worked, fill did not — so this asserts the fill direction specifically.
    /// </summary>
    [Fact]
    public void Grade_OneSidedRailAboveGround_KeepsTheRailElevation()
    {
        var terrain = FlatTerrain(0.0);

        const int n = 3;
        var xy = new double[] { -30.0, 1.0, 0.0, 1.0, 30.0, 1.0 };
        var z = new double[] { 4.0, 4.0, 4.0 };
        var normals = new double[] { 0.0, 1.0, 0.0, 1.0, 0.0, 1.0 };

        var paths = new[]
        {
            new PathGrader.PathDefinition(
                xy, z, n,
                width: 0.0,
                slopeAngleDeg: 45.0,
                outwardNormals: normals)
        };

        GradeOutcome gradeOutcome13 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome13.ErrorMessage;
        GradingResult? result = gradeOutcome13.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        // The rail holds the elevation it was drawn at.
        AssertVertexNear(result, 0.0, 1.0, 4.0);
        AssertVertexNear(result, -30.0, 1.0, 4.0);

        // And the fill batter runs down from it, rather than a tent peaking mid-slope.
        Assert.True(result.FillVolume > 0.0, "A rail above ground must fill. " + string.Join(" | ", result.Diagnostics));
    }

    /// <summary>
    /// A whole retaining wall, as the planner emits it: two one-sided rails battering away from each
    /// other with their own slopes. Live-found regression — the carve honoured one-sidedness but the
    /// elevation pass did not, so the upper rail also graded the ground *below* the wall and the toe
    /// side rose instead of falling. The top side measured perfectly the whole time, which is what made
    /// it easy to miss.
    /// </summary>
    [Fact]
    public void Grade_WallRails_EachBatterStaysOnItsOwnSideOfTheWall()
    {
        // Ground flat at 0. Toe rail at y=0 z=2 batters toward -Y; top rail at y=1 z=6 toward +Y.
        var terrain = FlatTerrain(0.0);

        PathGrader.PathDefinition Rail(double y, double z, double outwardY, double fillDeg) =>
            new(
                new[] { -30.0, y, 0.0, y, 30.0, y },
                new[] { z, z, z },
                3,
                width: 0.0,
                slopeAngleDeg: 45.0,
                maxDistance: 0.0,
                fillSlopeAngleDeg: fillDeg,
                outwardNormals: new[] { 0.0, outwardY, 0.0, outwardY, 0.0, outwardY });

        // Toe at 45 degrees reaches ground 2 below in 2; top at ~26.57 (rise 0.5) reaches it in 12.
        var paths = new[]
        {
            Rail(0.0, 2.0, -1.0, 45.0),
            Rail(1.0, 6.0, 1.0, 26.565051177078),
        };

        GradeOutcome gradeOutcome14 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = paths,
        });
        string? errorMessage = gradeOutcome14.ErrorMessage;
        GradingResult? result = gradeOutcome14.Result;

        Assert.True(result != null, errorMessage);
        PadInvariantAssert.AssertWatertightManifold(result!);

        // Both rails hold the elevation they were drawn at.
        AssertVertexNear(result, 0.0, 0.0, 2.0);
        AssertVertexNear(result, 0.0, 1.0, 6.0);

        // The toe side falls away from the wall; nothing below the toe rail may rise above it.
        for (int i = 0; i < result.VertexCount; i++)
        {
            double y = result.Vertices[(i * 3) + 1];
            double z = result.Vertices[(i * 3) + 2];
            if (y < -0.01 && Math.Abs(result.Vertices[i * 3]) < 25.0)
            {
                Assert.True(
                    z <= 2.0 + 1e-6,
                    $"Terrain at y={y:F2} rose to z={z:F3}, above the toe rail — the upper rail's batter crossed the wall.");
            }
        }
    }

    /// <summary>
    /// A one-sided rail whose normals point to its right must batter at its right-side angles in the
    /// daylight loop as well as the elevation pass. The loop used to take the left-side angles for
    /// every one-sided rail, so a right-facing rail's envelope and its batter disagreed.
    /// </summary>
    [Fact]
    public void Grade_OneSidedRailFacingRight_UsesTheRightSideAngles()
    {
        var terrain = FlatTerrain(10.0);

        // Rail along +X, so its left is +Y; normals point to -Y, its right.
        PathGrader.PathDefinition Rail(double sharedDeg, double rightCutDeg) =>
            new(
                new[] { -20.0, 0.0, 0.0, 0.0, 20.0, 0.0 },
                new[] { 0.0, 0.0, 0.0 },
                3,
                width: 0.0,
                slopeAngleDeg: sharedDeg,
                rightCutSlopeAngleDeg: rightCutDeg,
                outwardNormals: new[] { 0.0, -1.0, 0.0, -1.0, 0.0, -1.0 });

        GradeOutcome gradeOutcome15 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = new[] { Rail(45.0, 20.0) },
        });
        string? overriddenError = gradeOutcome15.ErrorMessage;
        GradingResult? overridden = gradeOutcome15.Result;
        GradeOutcome gradeOutcome16 = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(terrain.v, terrain.vc, terrain.f, terrain.fc),
            Paths = new[] { Rail(20.0, 0.0) },
        });
        string? sharedError = gradeOutcome16.ErrorMessage;
        GradingResult? shared = gradeOutcome16.Result;

        Assert.True(overridden != null, overriddenError);
        Assert.True(shared != null, sharedError);
        PadInvariantAssert.AssertWatertightManifold(overridden!);
        Assert.True(
            Math.Abs(overridden.CutVolume - shared!.CutVolume) <= 0.01 * shared.CutVolume,
            $"Right-side override cut {overridden.CutVolume:F1}; the same shared angle cuts {shared.CutVolume:F1}.");
    }

    private static void AssertVertexNear(GradingResult result, double x, double y, double expectedZ)
    {
        double best = double.MaxValue;
        double bestZ = double.NaN;
        for (int i = 0; i < result.VertexCount; i++)
        {
            double dx = result.Vertices[i * 3] - x;
            double dy = result.Vertices[(i * 3) + 1] - y;
            double d = (dx * dx) + (dy * dy);
            if (d < best)
            {
                best = d;
                bestZ = result.Vertices[(i * 3) + 2];
            }
        }

        Assert.True(best < 1.0, $"No vertex near ({x}, {y}).");
        Assert.True(Math.Abs(bestZ - expectedZ) < 1e-3, $"Vertex near ({x}, {y}) is at z={bestZ:F4}, expected {expectedZ:F4}.");
    }

    private static double MinZNearLine(GradingResult result)
    {
        double min = double.MaxValue;
        for (int i = 0; i < result.VertexCount; i++)
        {
            if (Math.Abs(result.Vertices[(i * 3) + 1]) < 1.0)
                min = Math.Min(min, result.Vertices[(i * 3) + 2]);
        }

        return min;
    }
}
