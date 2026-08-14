using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptConstraintMaskTests
{
    [Fact]
    public void EvaluateInfluence_Polygon_PinsInteriorAndFeathersOutside()
    {
        var mask = new SculptConstraintMask(featherDistance: 2.0);
        mask.AddPolygon(new[] { 0.0, 0.0, 4.0, 0.0, 4.0, 4.0, 0.0, 4.0 }, 4);

        Assert.Equal(0.0, mask.EvaluateInfluence(2.0, 2.0), 12);
        Assert.Equal(0.5, mask.EvaluateInfluence(5.0, 2.0), 12);
        Assert.Equal(1.0, mask.EvaluateInfluence(6.0, 2.0), 12);
    }

    [Fact]
    public void EvaluateInfluence_BufferedClosedPolyline_DoesNotProtectLoopInterior()
    {
        var mask = new SculptConstraintMask(featherDistance: 1.0);
        mask.AddPolyline(
            new[] { 0.0, 0.0, 4.0, 0.0, 4.0, 4.0, 0.0, 4.0 },
            4,
            halfWidth: 0.5,
            isClosed: true);

        Assert.Equal(0.0, mask.EvaluateInfluence(0.25, 2.0), 12);
        Assert.Equal(1.0, mask.EvaluateInfluence(2.0, 2.0), 12);
    }

    [Fact]
    public void ApplyDab_ConstraintPinsInteriorVertex()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
        };
        int[] faces = { 0, 1, 2 };
        var mask = new SculptConstraintMask(featherDistance: 0.0);
        mask.AddPolygon(new[] { -0.25, -0.25, 0.25, -0.25, 0.25, 0.25, -0.25, 0.25 }, 4);
        var engine = new SculptBrushEngine(
            vertices, 3, faces, 1, new SculptDisplacementField(0.25), mask);
        var dab = new SculptDabParams(
            0.0, 0.0, 2.0, 1.0, SculptBrushKind.Draw, SculptFalloff.Constant, false, 7);

        engine.BeginStroke(dab);
        engine.ApplyDab(dab);

        Assert.Equal(0.0, engine.Vertices[2], 12);
        Assert.True(engine.Vertices[5] > 0.0);
    }
}
