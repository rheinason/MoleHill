using System.Globalization;
using Rhino;

namespace MoleHill.Shared;

/// <summary>
/// Validated physical-unit information for a Rhino model document. MoleHill geometry remains in
/// model coordinates; this type is the single conversion boundary for physical defaults and display.
/// </summary>
public readonly record struct ModelUnitContext(
    UnitSystem UnitSystem,
    double MetersPerModelUnit,
    string Abbreviation,
    double AbsoluteTolerance)
{
    public bool IsSupported =>
        UnitSystem is not UnitSystem.None and not UnitSystem.Unset &&
        double.IsFinite(MetersPerModelUnit) &&
        MetersPerModelUnit > 0.0;

    public double FromMeters(double meters) => meters / MetersPerModelUnit;

    public double ToMeters(double modelLength) => modelLength * MetersPerModelUnit;

    public double FromSquareMeters(double squareMeters) =>
        squareMeters / (MetersPerModelUnit * MetersPerModelUnit);

    public double ToSquareMeters(double modelArea) =>
        modelArea * MetersPerModelUnit * MetersPerModelUnit;

    public double FromCubicMeters(double cubicMeters) =>
        cubicMeters / (MetersPerModelUnit * MetersPerModelUnit * MetersPerModelUnit);

    public double ToCubicMeters(double modelVolume) =>
        modelVolume * MetersPerModelUnit * MetersPerModelUnit * MetersPerModelUnit;

    public double FromPerSquareMeter(double perSquareMeter) =>
        perSquareMeter * MetersPerModelUnit * MetersPerModelUnit;

    public double ToPerSquareMeter(double perModelUnitSquared) =>
        perModelUnitSquared / (MetersPerModelUnit * MetersPerModelUnit);

    public string FormatLength(double value, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation}";

    public string FormatArea(double value, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation}\u00b2";

    public string FormatVolume(double value, string format = "F2") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} {Abbreviation}\u00b3";

    public string FormatPerArea(double value, string format = "F3") =>
        $"{value.ToString(format, CultureInfo.InvariantCulture)} /{Abbreviation}\u00b2";

    public static ModelUnitContext FromDocument(RhinoDoc? doc)
    {
        if (doc == null)
            return Unsupported(UnitSystem.Unset, 0.0);

        UnitSystem unitSystem = doc.ModelUnitSystem;
        if (unitSystem == UnitSystem.CustomUnits)
        {
            if (doc.GetCustomUnitSystem(true, out string customName, out double metersPerCustomUnit) &&
                double.IsFinite(metersPerCustomUnit) && metersPerCustomUnit > 0.0)
            {
                return new ModelUnitContext(
                    unitSystem,
                    metersPerCustomUnit,
                    string.IsNullOrWhiteSpace(customName) ? "custom units" : customName,
                    doc.ModelAbsoluteTolerance);
            }

            return Unsupported(unitSystem, doc.ModelAbsoluteTolerance);
        }

        return FromUnitSystem(unitSystem, doc.ModelAbsoluteTolerance);
    }

    public static ModelUnitContext FromUnitSystem(UnitSystem unitSystem, double absoluteTolerance = 0.0)
    {
        if (unitSystem is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
            return Unsupported(unitSystem, absoluteTolerance);

        double metersPerUnit = MetersPerKnownUnit(unitSystem);
        return double.IsFinite(metersPerUnit) && metersPerUnit > 0.0
            ? new ModelUnitContext(unitSystem, metersPerUnit, GetAbbreviation(unitSystem), absoluteTolerance)
            : Unsupported(unitSystem, absoluteTolerance);
    }

    public static string GetAbbreviation(UnitSystem unitSystem) => unitSystem switch
    {
        UnitSystem.Angstroms => "\u00c5",
        UnitSystem.Nanometers => "nm",
        UnitSystem.Microns => "\u00b5m",
        UnitSystem.Millimeters => "mm",
        UnitSystem.Centimeters => "cm",
        UnitSystem.Decimeters => "dm",
        UnitSystem.Meters => "m",
        UnitSystem.Dekameters => "dam",
        UnitSystem.Hectometers => "hm",
        UnitSystem.Kilometers => "km",
        UnitSystem.Megameters => "Mm",
        UnitSystem.Gigameters => "Gm",
        UnitSystem.Microinches => "\u00b5in",
        UnitSystem.Mils => "mil",
        UnitSystem.Inches => "in",
        UnitSystem.Feet => "ft",
        UnitSystem.Yards => "yd",
        UnitSystem.Miles => "mi",
        UnitSystem.PrinterPoints => "pt",
        UnitSystem.PrinterPicas => "pc",
        UnitSystem.NauticalMiles => "nmi",
        UnitSystem.AstronomicalUnits => "au",
        UnitSystem.LightYears => "ly",
        UnitSystem.Parsecs => "pc",
        UnitSystem.CustomUnits => "custom units",
        _ => "units"
    };

    private static ModelUnitContext Unsupported(UnitSystem unitSystem, double absoluteTolerance) =>
        new(unitSystem, double.NaN, "units", absoluteTolerance);

    private static double MetersPerKnownUnit(UnitSystem unitSystem) => unitSystem switch
    {
        UnitSystem.Angstroms => 1e-10,
        UnitSystem.Nanometers => 1e-9,
        UnitSystem.Microns => 1e-6,
        UnitSystem.Millimeters => 1e-3,
        UnitSystem.Centimeters => 1e-2,
        UnitSystem.Decimeters => 1e-1,
        UnitSystem.Meters => 1.0,
        UnitSystem.Dekameters => 10.0,
        UnitSystem.Hectometers => 100.0,
        UnitSystem.Kilometers => 1e3,
        UnitSystem.Megameters => 1e6,
        UnitSystem.Gigameters => 1e9,
        UnitSystem.Microinches => 2.54e-8,
        UnitSystem.Mils => 2.54e-5,
        UnitSystem.Inches => 0.0254,
        UnitSystem.Feet => 0.3048,
        UnitSystem.Yards => 0.9144,
        UnitSystem.Miles => 1609.344,
        UnitSystem.PrinterPoints => 0.0254 / 72.0,
        UnitSystem.PrinterPicas => 0.0254 / 6.0,
        UnitSystem.NauticalMiles => 1852.0,
        UnitSystem.AstronomicalUnits => 149_597_870_700.0,
        UnitSystem.LightYears => 9.4607304725808e15,
        UnitSystem.Parsecs => 3.0856775814913673e16,
        _ => double.NaN
    };
}
