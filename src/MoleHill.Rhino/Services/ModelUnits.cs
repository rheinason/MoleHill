using System.Globalization;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class ModelUnits
{
    /// <summary>Short unit abbreviation for doc-unit display strings (e.g. "m", "ft", "mm").</summary>
    public static string Abbreviation(UnitSystem unitSystem) => unitSystem switch
    {
        UnitSystem.Microns => "µm",
        UnitSystem.Millimeters => "mm",
        UnitSystem.Centimeters => "cm",
        UnitSystem.Decimeters => "dm",
        UnitSystem.Meters => "m",
        UnitSystem.Dekameters => "dam",
        UnitSystem.Hectometers => "hm",
        UnitSystem.Kilometers => "km",
        UnitSystem.Microinches => "µin",
        UnitSystem.Mils => "mil",
        UnitSystem.Inches => "in",
        UnitSystem.Feet => "ft",
        UnitSystem.Yards => "yd",
        UnitSystem.Miles => "mi",
        _ => "units"
    };

    public static string FormatLength(double value, UnitSystem unitSystem, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation(unitSystem)}";

    public static string FormatArea(double value, UnitSystem unitSystem, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation(unitSystem)}²";

    public static string FormatVolume(double value, UnitSystem unitSystem, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation(unitSystem)}³";

    public static double FromMeters(double meters, UnitSystem unitSystem)
    {
        return unitSystem switch
        {
            UnitSystem.Microns => meters * 1_000_000.0,
            UnitSystem.Millimeters => meters * 1_000.0,
            UnitSystem.Centimeters => meters * 100.0,
            UnitSystem.Decimeters => meters * 10.0,
            UnitSystem.Meters => meters,
            UnitSystem.Dekameters => meters * 0.1,
            UnitSystem.Hectometers => meters * 0.01,
            UnitSystem.Kilometers => meters * 0.001,
            UnitSystem.Microinches => meters / 0.0000000254,
            UnitSystem.Mils => meters / 0.0000254,
            UnitSystem.Inches => meters / 0.0254,
            UnitSystem.Feet => meters / 0.3048,
            UnitSystem.Yards => meters / 0.9144,
            UnitSystem.Miles => meters / 1609.344,
            _ => meters
        };
    }

    public static double ModelDistanceOrDefault(double value, double defaultMeters, UnitSystem unitSystem)
    {
        return value > 0.0 ? value : FromMeters(defaultMeters, unitSystem);
    }
}
