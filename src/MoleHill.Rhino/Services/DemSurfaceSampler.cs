using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Maps numeric GeoTIFF pixel centres through a textured surface into terrain sample points.</summary>
internal static class DemSurfaceSampler
{
    public static bool TrySample(
        GeometryBase geometry,
        GeoTiffElevationSamples raster,
        double elevationScale,
        out List<Point3d> points,
        out string? error)
    {
        points = new List<Point3d>(raster.Samples.Count);
        error = null;
        if (!double.IsFinite(elevationScale) || elevationScale <= 0.0)
        {
            error = "The DEM elevation scale is invalid.";
            return false;
        }

        Surface? surface = geometry switch
        {
            Brep brep when brep.Faces.Count == 1 => brep.Faces[0],
            Surface value => value,
            _ => null
        };
        if (surface == null)
        {
            error = "The DEM input must be a single Rhino surface.";
            return false;
        }
        if (!surface.TryGetPlane(out _))
        {
            error = "The DEM texture surface must be planar.";
            return false;
        }

        Interval uDomain = surface.Domain(0);
        Interval vDomain = surface.Domain(1);
        if (!uDomain.IsValid || !vDomain.IsValid || uDomain.Length <= 0.0 || vDomain.Length <= 0.0)
        {
            error = "The DEM texture surface has an invalid parameter domain.";
            return false;
        }

        foreach (GeoTiffElevationSample sample in raster.Samples)
        {
            double normalizedX = (sample.PixelX + 0.5) / raster.Width;
            double normalizedY = 1.0 - ((sample.PixelY + 0.5) / raster.Height);
            Point3d point = surface.PointAt(
                uDomain.ParameterAt(normalizedX),
                vDomain.ParameterAt(normalizedY));
            point.Z += sample.Elevation * elevationScale;
            if (point.IsValid)
                points.Add(point);
        }

        if (points.Count < 3)
        {
            error = "The DEM texture produced fewer than three valid surface samples.";
            return false;
        }
        return true;
    }
}
