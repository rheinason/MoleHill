using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Tells a layout detail's own draw pass from a shadow-map pass. A detail is drawn into its page's window,
/// so the pipeline's world-to-clip is the detail viewport's followed by a 2D scale and offset onto the
/// page's clip space, and that placement changes with every page zoom or pan. Comparing the matrices
/// exactly read every visible detail pass as a shadow pass after one wheel click, and the terrain drew in
/// the flat gray shadow-caster material (found live). See <c>TerrainDisplayConduit.IsShadowMapPass</c>.
/// </summary>
internal static class DetailProjectionMatch
{
    /// <summary>
    /// True when <paramref name="pipeline"/> (an OpenGL column-major world-to-clip) is
    /// <paramref name="viewport"/>'s world-to-clip followed by a positive scale and any offset in clip x
    /// and y. The w row must match outright, and the x and y rows must each be a positive multiple of the
    /// viewport's plus a multiple of its w row. A shadow map projects from the light, whose rotation is
    /// not the view's, so it fails one of them.
    /// </summary>
    internal static bool IsOwnProjection(float[] pipeline, Transform viewport)
    {
        if (pipeline.Length < 16)
            return false;

        for (int column = 0; column < 4; column++)
        {
            double expected = viewport[3, column];
            if (Math.Abs(expected - pipeline[(column * 4) + 3]) > 1e-4 * Math.Max(1.0, Math.Abs(expected)))
                return false;
        }

        // Clip x' = s·x + t·w (likewise y): each pipeline row is a least-squares fit of the viewport's row
        // and its w row, over all four columns. In a parallel view w is (0,0,0,1), so t is a plain offset.
        for (int row = 0; row < 2; row++)
        {
            double vv = 0.0, vw = 0.0, ww = 0.0, pv = 0.0, pw = 0.0, pp = 0.0;
            for (int column = 0; column < 4; column++)
            {
                double v = viewport[row, column];
                double w = viewport[3, column];
                double p = pipeline[(column * 4) + row];
                vv += v * v;
                vw += v * w;
                ww += w * w;
                pv += p * v;
                pw += p * w;
                pp += p * p;
            }

            double det = (vv * ww) - (vw * vw);
            if (det <= 1e-12 * vv * ww || pp <= 0.0)
                return false;

            double scale = ((pv * ww) - (pw * vw)) / det;
            double offset = ((vv * pw) - (vw * pv)) / det;
            if (scale <= 0.0)
                return false;

            double residual = 0.0;
            for (int column = 0; column < 4; column++)
            {
                double d = pipeline[(column * 4) + row] - (scale * viewport[row, column]) - (offset * viewport[3, column]);
                residual += d * d;
            }

            // Float precision on the pipeline side leaves ~1e-14 here; a different rotation leaves O(1).
            if (residual > 1e-6 * pp)
                return false;
        }

        return true;
    }
}
