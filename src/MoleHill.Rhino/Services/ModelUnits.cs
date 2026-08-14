using System.Globalization;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class ModelUnits
{
    public static string Abbreviation(UnitSystem unitSystem) =>
        ModelUnitContext.GetAbbreviation(unitSystem);

    public static string FormatLength(double value, UnitSystem unitSystem, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation(unitSystem)}";

    public static string FormatArea(double value, UnitSystem unitSystem, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation(unitSystem)}\u00b2";

    public static string FormatVolume(double value, UnitSystem unitSystem, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation(unitSystem)}\u00b3";

    public static double FromMeters(double meters, UnitSystem unitSystem)
    {
        ModelUnitContext context = ModelUnitContext.FromUnitSystem(unitSystem);
        return context.IsSupported ? context.FromMeters(meters) : meters;
    }

    public static double FromMeters(double meters, ModelUnitContext context) =>
        context.IsSupported ? context.FromMeters(meters) : meters;

    public static double ModelDistanceOrDefault(double value, double defaultMeters, UnitSystem unitSystem)
    {
        if (double.IsFinite(value) && value > 0.0)
            return value;

        return FromMeters(defaultMeters, unitSystem);
    }
}
