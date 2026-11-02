using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class DetailProjectionMatchTests
{
    [Fact]
    public void IsOwnProjection_SameMatrix_ReturnsTrue()
    {
        Transform view = ParallelTopView();

        Assert.True(DetailProjectionMatch.IsOwnProjection(ToOpenGl(view), view));
    }

    [Fact]
    public void IsOwnProjection_PageZoomAndPanOfDetail_ReturnsTrue()
    {
        Transform view = ParallelTopView();
        Transform placed = ScaleOffsetClip(view, sx: 4.253, sy: 5.936, tx: 0.30, ty: -0.71);

        Assert.True(DetailProjectionMatch.IsOwnProjection(ToOpenGl(placed), view));
    }

    [Fact]
    public void IsOwnProjection_PerspectiveDetailAfterPageZoom_ReturnsTrue()
    {
        Transform view = PerspectiveView();
        Transform placed = ScaleOffsetClip(view, sx: 1.3, sy: 1.3, tx: -0.2, ty: 0.1);

        Assert.True(DetailProjectionMatch.IsOwnProjection(ToOpenGl(placed), view));
    }

    [Fact]
    public void IsOwnProjection_LightProjectionFromAnotherDirection_ReturnsFalse()
    {
        Transform view = ParallelTopView();
        Transform light = RotationAboutX(Math.PI / 5.0) * RotationAboutZ(0.4);
        Transform fromLight = view * light;

        Assert.False(DetailProjectionMatch.IsOwnProjection(ToOpenGl(fromLight), view));
    }

    [Fact]
    public void IsOwnProjection_MirroredScale_ReturnsFalse()
    {
        Transform view = ParallelTopView();
        Transform mirrored = ScaleOffsetClip(view, sx: -1.0, sy: 1.0, tx: 0.0, ty: 0.0);

        Assert.False(DetailProjectionMatch.IsOwnProjection(ToOpenGl(mirrored), view));
    }

    /// <summary>World to clip for a Top view framing 0..100 in plan (orthographic, w row 0,0,0,1).</summary>
    private static Transform ParallelTopView()
    {
        var t = Transform.Identity;
        t[0, 0] = 2.0 / 151.2; t[0, 3] = -0.661355;
        t[1, 1] = 2.0 / 106.0; t[1, 3] = -0.909091;
        t[2, 2] = -0.01; t[2, 3] = 0.2;
        return t;
    }

    /// <summary>A perspective world to clip: a rotated camera followed by a projection with a non-trivial w row.</summary>
    private static Transform PerspectiveView()
    {
        var shift = Transform.Identity;
        shift[0, 3] = -50; shift[1, 3] = -50; shift[2, 3] = -40;
        Transform camera = RotationAboutX(-Math.PI / 4.0) * shift;
        var projection = Transform.Identity;
        projection[0, 0] = 1.8;
        projection[1, 1] = 2.4;
        projection[2, 2] = -1.02; projection[2, 3] = -2.0;
        projection[3, 2] = -1.0; projection[3, 3] = 0.0;
        return projection * camera;
    }

    /// <summary>The page-to-detail placement: x' = sx·x + tx·w, y' = sy·y + ty·w in clip space.</summary>
    private static Transform ScaleOffsetClip(Transform view, double sx, double sy, double tx, double ty)
    {
        var place = Transform.Identity;
        place[0, 0] = sx; place[0, 3] = tx;
        place[1, 1] = sy; place[1, 3] = ty;
        return place * view;
    }

    // Built by hand: Transform.Rotation calls into Rhino's native library, absent outside Rhino.
    private static Transform RotationAboutX(double angle)
    {
        var t = Transform.Identity;
        t[1, 1] = Math.Cos(angle); t[1, 2] = -Math.Sin(angle);
        t[2, 1] = Math.Sin(angle); t[2, 2] = Math.Cos(angle);
        return t;
    }

    private static Transform RotationAboutZ(double angle)
    {
        var t = Transform.Identity;
        t[0, 0] = Math.Cos(angle); t[0, 1] = -Math.Sin(angle);
        t[1, 0] = Math.Sin(angle); t[1, 1] = Math.Cos(angle);
        return t;
    }

    private static float[] ToOpenGl(Transform t)
    {
        var m = new float[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
                m[(column * 4) + row] = (float)t[row, column];
        }

        return m;
    }
}
