using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class SurfaceConformerTests
{
    private static readonly double[] TargetVertices =
    [
        -20, -20, 10,
         20, -20, 10,
         20,  20, 10,
        -20,  20, 10
    ];

    private static readonly int[] TargetFaces = [0, 1, 2, 0, 2, 3];

    [Fact]
    public void Conform_NoBoundary_BlendsEveryCoveredVertex()
    {
        double[] result = Conform([0, 0, 0, 2, 2, 2], [], strength: 0.5, feather: 0.0);

        Assert.Equal(5.0, result[2], 10);
        Assert.Equal(6.0, result[5], 10);
    }

    [Fact]
    public void Conform_DonutBoundary_LeavesHoleAndExteriorUntouched()
    {
        double[] result = Conform(
            [0, 0, 0, 3, 0, 0, 8, 0, 0],
            [Square(0, 0, 10), Square(0, 0, 4)],
            strength: 1.0,
            feather: 0.0);

        Assert.Equal(0.0, result[2], 10);
        Assert.Equal(10.0, result[5], 10);
        Assert.Equal(0.0, result[8], 10);
    }

    [Fact]
    public void Conform_Feather_FadesInwardAtOuterAndHoleEdges()
    {
        double[] result = Conform(
            [4.5, 0, 0, 2.5, 0, 0],
            [Square(0, 0, 10), Square(0, 0, 4)],
            strength: 1.0,
            feather: 1.0);

        Assert.Equal(5.0, result[2], 10);
        Assert.Equal(5.0, result[5], 10);
    }

    [Fact]
    public void Conform_TargetWithoutCoverage_PreservesVertex()
    {
        double[] result = Conform([30, 30, 4], [], strength: 1.0, feather: 0.0);

        Assert.Equal(4.0, result[2], 10);
    }

    /// <summary>
    /// A wall's top and toe share XY. Projected, both would land on the one target Z and the wall would
    /// collapse; walls are never buried, so the wall vertices keep their elevation while the flat ground
    /// around them still projects.
    /// </summary>
    [Fact]
    public void Conform_WallFaces_KeepsWallVerticesAndProjectsTheRest()
    {
        // A 1 m-high vertical-ish wall (0.01 m run) between x=0 and x=0.01, with ground either side.
        double[] vertices =
        [
            -5, 0, 1,   0, 0, 1,   0.01, 0, 0,   5, 0, 0,
            -5, 5, 1,   0, 5, 1,   0.01, 5, 0,   5, 5, 0
        ];
        int[] faces =
        [
            0, 1, 5,  0, 5, 4,   // upper ground
            1, 2, 6,  1, 6, 5,   // wall
            2, 3, 7,  2, 7, 6    // lower ground
        ];

        double[] result = SurfaceConformer.Conform(
            vertices, 8, TargetVertices, 4, TargetFaces, 2, [], 1.0, 0.0, 1e-6,
            faces: faces, faceCount: 6, wallFaceMinSlopeDeg: 70.0);

        Assert.Equal(1.0, result[(1 * 3) + 2], 10);
        Assert.Equal(0.0, result[(2 * 3) + 2], 10);
        Assert.Equal(1.0, result[(5 * 3) + 2], 10);
        Assert.Equal(0.0, result[(6 * 3) + 2], 10);
        Assert.Equal(10.0, result[(0 * 3) + 2], 10);
        Assert.Equal(10.0, result[(3 * 3) + 2], 10);
    }

    [Fact]
    public void Conform_WallFacesWithoutThreshold_ProjectsEverything()
    {
        double[] vertices = [0, 0, 1, 0.01, 0, 0, 0, 5, 1];
        int[] faces = [0, 1, 2];

        double[] result = SurfaceConformer.Conform(
            vertices, 3, TargetVertices, 4, TargetFaces, 2, [], 1.0, 0.0, 1e-6,
            faces: faces, faceCount: 1, wallFaceMinSlopeDeg: 0.0);

        Assert.All([result[2], result[5], result[8]], z => Assert.Equal(10.0, z, 10));
    }

    /// <summary>
    /// No boundary: the region is the target's footprint (x, y in [-20, 20]). Terrain running past its
    /// edge must fade in over the feather rather than step from 0 to 10 at the edge.
    /// </summary>
    [Fact]
    public void Conform_NoBoundaryWithFeather_FadesInFromTheFootprintEdge()
    {
        double[] vertices =
        [
            21, 0, 0,     // outside the target: untouched
            19.5, 0, 0,   // 1.5 from the nearest uncovered vertex: partially projected
            18, 0, 0,     // 3 away, at the feather distance: fully projected
            0, 0, 0       // deep inside: fully projected
        ];

        double[] result = Conform(vertices, [], strength: 1.0, feather: 3.0);

        Assert.Equal(0.0, result[2], 10);
        Assert.Equal(10.0 * 0.5, result[5], 10); // smoothstep(0.5) = 0.5
        Assert.Equal(10.0, result[8], 10);
        Assert.Equal(10.0, result[11], 10);
    }

    [Fact]
    public void Conform_NoBoundaryWithFeatherAndFullCoverage_ProjectsEverything()
    {
        // Nothing lies outside the target, so there is no footprint edge to fade from.
        double[] result = Conform([19.9, 0, 0, 0, 0, 0], [], strength: 1.0, feather: 3.0);

        Assert.Equal(10.0, result[2], 10);
        Assert.Equal(10.0, result[5], 10);
    }

    private static double[] Conform(double[] vertices, IReadOnlyList<double[]> loops, double strength, double feather) =>
        SurfaceConformer.Conform(
            vertices,
            vertices.Length / 3,
            TargetVertices,
            TargetVertices.Length / 3,
            TargetFaces,
            TargetFaces.Length / 3,
            loops,
            strength,
            feather,
            1e-6);

    private static double[] Square(double cx, double cy, double size)
    {
        double h = size / 2.0;
        return [cx - h, cy - h, cx + h, cy - h, cx + h, cy + h, cx - h, cy + h];
    }
}
