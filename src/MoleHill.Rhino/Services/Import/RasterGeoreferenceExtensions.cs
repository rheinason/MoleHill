using MoleHill.Core.IO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>The RhinoCommon edge of the pure <see cref="RasterGeoreference"/> value.</summary>
internal static class RasterGeoreferenceExtensions
{
    public static Transform CreatePictureFrameToWorldTransform(this RasterGeoreference georeference, int imageHeight)
    {
        // Picture-frame local coordinates start at the lower-left and grow upward. Raster line
        // coordinates start at the upper-left and grow downward, hence line = height - localY.
        var transform = Transform.Identity;
        transform.M00 = georeference.XPixel;
        transform.M01 = -georeference.XLine;
        transform.M03 = georeference.XUpperLeft + (georeference.XLine * imageHeight);
        transform.M10 = georeference.YPixel;
        transform.M11 = -georeference.YLine;
        transform.M13 = georeference.YUpperLeft + (georeference.YLine * imageHeight);
        return transform;
    }
}
