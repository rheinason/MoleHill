// Station and elevation helpers shared by the long-section tools: plan stations, interpolation, datum.
namespace MoleHill.Core.Analysis;

/// <summary>
/// Where a long section is drawn: station 0 at the datum sits at (<see cref="OriginX"/>,
/// <see cref="OriginY"/>), station runs along +X at true scale and elevation along +Y, measured from
/// <see cref="Datum"/> and multiplied by <see cref="Exaggeration"/>.
/// </summary>
public readonly record struct LongSectionFrame(double OriginX, double OriginY, double Datum, double Exaggeration)
{
    public (double X, double Y) ToDrawing(double station, double elevation) =>
        (OriginX + station, OriginY + (elevation - Datum) * Exaggeration);

    public (double Station, double Elevation) FromDrawing(double x, double y) =>
        (x - OriginX, Datum + (y - OriginY) / Exaggeration);
}

/// <summary>
/// Station and elevation helpers for long sections: plan stations along a path, linear elevation at a
/// station, and a round datum. <see cref="SectionEdit"/> (mhEditProfile's profile mode) and the curve commands build on them.
/// </summary>
public static class LongSectionProfile
{
    /// <summary>Linear elevation at a station; NaN outside the profile's station range.</summary>
    public static double ElevationAt(double[] stations, double[] elevations, double station, double tolerance = 0.0)
    {
        if (stations.Length == 0)
            return double.NaN;
        if (station < stations[0] - tolerance || station > stations[^1] + tolerance)
            return double.NaN;
        if (station <= stations[0])
            return elevations[0];
        if (station >= stations[^1])
            return elevations[^1];

        int index = Array.BinarySearch(stations, station);
        if (index >= 0)
            return elevations[index];
        int upper = ~index;
        int lower = upper - 1;
        double t = (station - stations[lower]) / (stations[upper] - stations[lower]);
        return elevations[lower] + (elevations[upper] - elevations[lower]) * t;
    }

    /// <summary>Cumulative plan (XY) distance at each point of a flat XYZ path.</summary>
    public static double[] PlanStations(double[] pathXyz)
    {
        int count = pathXyz.Length / 3;
        var stations = new double[count];
        for (int i = 1; i < count; i++)
        {
            double dx = pathXyz[i * 3] - pathXyz[(i - 1) * 3];
            double dy = pathXyz[i * 3 + 1] - pathXyz[(i - 1) * 3 + 1];
            stations[i] = stations[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        return stations;
    }

    /// <summary>
    /// A datum a little below the lowest elevation, on a whole multiple of <paramref name="step"/>, so the
    /// section's base line reads as a round number.
    /// </summary>
    public static double SuggestDatum(double lowestElevation, double step)
    {
        if (!double.IsFinite(lowestElevation))
            return 0.0;
        if (!(step > 0.0))
            return lowestElevation;
        return Math.Floor(lowestElevation / step) * step - step;
    }
}
