using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Contouring a per-vertex field rather than elevation — what draws a cut/fill delta, where a "level" is a
/// depth and level 0 is the balance line.
/// </summary>
public class ContourFieldGeneratorTests
{
    /// <summary>A unit grid quad, two triangles, all four corners at z = 100.</summary>
    private static (double[] Vertices, int[] Faces) LevelQuad()
    {
        var vertices = new[]
        {
            0.0, 0.0, 100.0,
            10.0, 0.0, 100.0,
            10.0, 10.0, 100.0,
            0.0, 10.0, 100.0
        };

        return (vertices, new[] { 0, 1, 2, 0, 2, 3 });
    }

    /// <summary>
    /// The point of the overload: the mesh is perfectly level, so elevation contouring produces nothing,
    /// while the field crosses its level and does.
    /// </summary>
    [Fact]
    public void Generate_FieldOnALevelMesh_ContoursTheFieldNotTheElevation()
    {
        var (vertices, faces) = LevelQuad();
        var field = new[] { -2.0, -2.0, 3.0, 3.0 };

        var byElevation = ContourGenerator.Generate(vertices, 4, faces, 2, new[] { 0.0 }, 0.001);
        var byField = ContourGenerator.Generate(vertices, 4, faces, 2, field, new[] { 0.0 }, 0.001);

        Assert.Empty(byElevation);
        ContourLevel level = Assert.Single(byField);
        Assert.Equal(0.0, level.Z);
        Assert.NotEmpty(level.Polylines);
    }

    /// <summary>The emitted crossing sits on the mesh, at the elevation the mesh has there — not at the level.</summary>
    [Fact]
    public void Generate_Field_EmitsPointsOnTheMeshNotAtTheLevelValue()
    {
        var (vertices, faces) = LevelQuad();
        var field = new[] { -2.0, -2.0, 3.0, 3.0 };

        ContourLevel level = Assert.Single(
            ContourGenerator.Generate(vertices, 4, faces, 2, field, new[] { 0.0 }, 0.001));

        foreach (var polyline in level.Polylines)
        {
            for (int i = 0; i < polyline.PointCount; i++)
                Assert.Equal(100.0, polyline.PointsXyz[(i * 3) + 2], 9);
        }
    }

    /// <summary>
    /// Unmapped ground — outside the comparison boundary, or over a hole in the reference — carries NaN and
    /// must draw nothing. Inventing a line there would report a depth that was never measured.
    /// </summary>
    [Fact]
    public void Generate_FieldWithNonFiniteVertices_SkipsEveryFaceTouchingThem()
    {
        var (vertices, faces) = LevelQuad();

        // The second triangle (0, 2, 3) touches vertex 3, which has no comparable value.
        var field = new[] { -2.0, -2.0, 3.0, double.NaN };

        ContourLevel level = Assert.Single(
            ContourGenerator.Generate(vertices, 4, faces, 2, field, new[] { 0.0 }, 0.001));

        // Only the first triangle contributes, so the result is a single unstitched segment.
        Assert.Single(level.Polylines);
        Assert.Equal(2, level.Polylines[0].PointCount);
    }

    /// <summary>A field that never changes sign has no balance line, and must not fake one.</summary>
    [Fact]
    public void Generate_FieldEntirelyAboveTheLevel_ProducesNothing()
    {
        var (vertices, faces) = LevelQuad();
        var field = new[] { 1.0, 1.5, 2.0, 2.5 };

        Assert.Empty(ContourGenerator.Generate(vertices, 4, faces, 2, field, new[] { 0.0 }, 0.001));
    }

    /// <summary>Several depths in one pass, the way a delta contour set is drawn.</summary>
    [Fact]
    public void Generate_FieldAtSeveralLevels_ContoursEachOne()
    {
        var (vertices, faces) = LevelQuad();
        var field = new[] { -3.0, -3.0, 3.0, 3.0 };

        var levels = ContourGenerator.Generate(
            vertices, 4, faces, 2, field, new[] { -2.0, 0.0, 2.0 }, 0.001);

        Assert.Equal(3, levels.Count);
        Assert.Equal(new[] { -2.0, 0.0, 2.0 }, levels.Select(level => level.Z));
    }

    /// <summary>
    /// Passing null for the field must be exactly the old behaviour, since that is how every existing
    /// elevation-contour caller now reaches the same marching pass.
    /// </summary>
    [Fact]
    public void Generate_NullField_MatchesTheElevationOverload()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 10.0,
            10.0, 10.0, 10.0,
            0.0, 10.0, 0.0
        };
        var faces = new[] { 0, 1, 2, 0, 2, 3 };
        var levels = new[] { 2.5, 5.0, 7.5 };

        var viaOverload = ContourGenerator.Generate(vertices, 4, faces, 2, levels, 0.001);
        var viaNullField = ContourGenerator.Generate(vertices, 4, faces, 2, null, levels, 0.001);

        Assert.Equal(viaOverload.Count, viaNullField.Count);
        for (int i = 0; i < viaOverload.Count; i++)
        {
            Assert.Equal(viaOverload[i].Z, viaNullField[i].Z);
            Assert.Equal(viaOverload[i].Polylines.Count, viaNullField[i].Polylines.Count);
        }
    }
}
