using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Pure formatting/parsing helpers shared between the panel's hand-written analysis rows and
/// <see cref="ParameterDescriptor{TDefinition}"/> schema rows (dynamic labels, choice options, color
/// fallbacks). Lives in Registry rather than the UI project so the Rhino test project — which links
/// Registry sources directly without a UI reference — can still compile the descriptors that use them.
/// No Eto dependency.
/// </summary>
internal static class AnalysisFormatting
{
    public static string GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Percent => "percent",
        SlopeAnalyzer.SlopeUnit.Promille => "promille",
        SlopeAnalyzer.SlopeUnit.Ratio => "ratio",
        SlopeAnalyzer.SlopeUnit.Degrees => "degrees",
        _ => "percent"
    };

    public static SlopeAnalyzer.SlopeUnit ParseSlopeUnit(string key) => key switch
    {
        "promille" => SlopeAnalyzer.SlopeUnit.Promille,
        "ratio" => SlopeAnalyzer.SlopeUnit.Ratio,
        "degrees" => SlopeAnalyzer.SlopeUnit.Degrees,
        _ => SlopeAnalyzer.SlopeUnit.Percent
    };

    public static string GetSlopeUnitSuffixLabel(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Percent => "%",
        SlopeAnalyzer.SlopeUnit.Promille => "promille",
        SlopeAnalyzer.SlopeUnit.Ratio => "ratio",
        SlopeAnalyzer.SlopeUnit.Degrees => "deg",
        _ => "%"
    };

    /// <summary>Formats a sampled value with a card's format string, falling back to "G4" when it is blank or invalid.</summary>
    public static string FormatValue(double value, string? format)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "G4" : format;
        try
        {
            return value.ToString(effectiveFormat, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return value.ToString("G4", CultureInfo.CurrentCulture);
        }
    }

    public static string FormatSlopeSummaryValue(double percentValue, SlopeAnalyzer.SlopeUnit unit)
    {
        return FormatSlopeValue(ConvertPercentToSlopeUnit(percentValue, unit), unit);
    }

    public static string FormatSlopeValue(double value, SlopeAnalyzer.SlopeUnit unit)
    {
        if (double.IsNaN(value))
            return "n/a";

        if (double.IsPositiveInfinity(value))
        {
            return unit switch
            {
                SlopeAnalyzer.SlopeUnit.Degrees => "90.0 deg",
                SlopeAnalyzer.SlopeUnit.Percent => "inf %",
                SlopeAnalyzer.SlopeUnit.Promille => "inf promille",
                SlopeAnalyzer.SlopeUnit.Ratio => "inf",
                _ => "inf"
            };
        }

        return unit switch
        {
            SlopeAnalyzer.SlopeUnit.Percent => $"{value:F1}%",
            SlopeAnalyzer.SlopeUnit.Promille => $"{value:F1} promille",
            SlopeAnalyzer.SlopeUnit.Ratio => $"{value:F3}",
            SlopeAnalyzer.SlopeUnit.Degrees => $"{value:F1} deg",
            _ => value.ToString("F1")
        };
    }

    public static double ConvertPercentToSlopeUnit(double percentValue, SlopeAnalyzer.SlopeUnit unit)
    {
        return ConvertSlopeValue(percentValue, SlopeAnalyzer.SlopeUnit.Percent, unit);
    }

    public static double ConvertSlopeValue(double value, SlopeAnalyzer.SlopeUnit fromUnit, SlopeAnalyzer.SlopeUnit toUnit)
    {
        if (fromUnit == toUnit || Math.Abs(value) <= 1e-9)
            return value;

        double ratio = SlopeAnalyzer.ConvertUnitToRatio(value, fromUnit);
        return SlopeAnalyzer.ConvertRatioToUnit(ratio, toUnit);
    }

    public static List<(string Key, string Label)> GetValueFormatOptions(string selectedFormat)
    {
        var options = new List<(string Key, string Label)>
        {
            ("F0", "Whole number"),
            ("F1", "1 decimal place"),
            ("F2", "2 decimal places"),
            ("F3", "3 decimal places"),
            ("G4", "Compact")
        };

        if (!string.IsNullOrWhiteSpace(selectedFormat) &&
            !options.Any(option => string.Equals(option.Key, selectedFormat, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add((selectedFormat, $"Custom ({selectedFormat})"));
        }

        return options;
    }

    public static string GetLeafLayerName(string layerPath)
    {
        return layerPath.Contains("::", StringComparison.Ordinal)
            ? layerPath[(layerPath.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
            : layerPath;
    }

    public static int? ResolveLayerColorArgb(string? layerPath)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null || string.IsNullOrWhiteSpace(layerPath))
            return null;

        int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        var layer = doc.Layers[layerIndex];
        return layer.Color.ToArgb();
    }

    /// <summary>The layer a role's output lands on for this terrain, named as the user sees it.</summary>
    public static string GetRoleLayerPath(TerrainDefinition terrain, LayerRole role) =>
        Services.LayerRoleService.GetTable(RhinoDoc.ActiveDoc, terrain).Path(role);

    /// <summary>
    /// What an unset colour resolves to, for the "clear to use the layer colour" hint on a colour
    /// row. Names the layer the role actually routes to rather than a per-card override, since there
    /// is no longer one.
    /// </summary>
    public static string GetRoleColorText(TerrainDefinition terrain, LayerRole role) =>
        $"By Layer ({GetLeafLayerName(GetRoleLayerPath(terrain, role))})";

    /// <summary>
    /// How an analysis's values are written on its ramp. Slope carries a unit the user chose; cut/fill
    /// wants an explicit sign so a legend reads "-2.50 / +2.50" rather than making you infer which end is
    /// which. Shared by the card's tick row and the drawn Legend annotation, so the two print the same
    /// numbers.
    /// </summary>
    public static Func<double, string> GetValueFormatter(AnalysisDefinition analysis) => analysis switch
    {
        SlopeAnalysisDefinition slope => value => SlopeInput.FormatWithUnit(
            SlopeAnalyzer.ConvertUnitToRatio(value, slope.Unit), slope.Unit),
        CutFillAnalysisDefinition => value => value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture),

        // Curvatures are small numbers in 1/model-unit (0.02 per metre is a 50 m radius), so a fixed two
        // decimals would print every tick as 0.00. Three significant figures, signed so convex reads "+".
        CurvatureAnalysisDefinition => FormatCurvature,
        _ => value => value.ToString("F2", CultureInfo.CurrentCulture)
    };

    /// <summary>A curvature in 1/model-unit: three significant figures, with an explicit sign.</summary>
    public static string FormatCurvature(double value)
    {
        if (!double.IsFinite(value))
            return "n/a";
        if (value == 0.0)
            return "0";

        string magnitude = Math.Abs(value).ToString("G3", CultureInfo.CurrentCulture);
        return value > 0.0 ? "+" + magnitude : "-" + magnitude;
    }

    /// <summary>A slope stored as degrees, shown in the user's chosen slope unit.</summary>
    public static string FormatSlopeDegrees(double degrees) =>
        SlopeInput.FormatWithUnit(Math.Tan(degrees * Math.PI / 180.0), SlopeUnitPreference.Current);

    /// <summary>
    /// A slope for a collapsed card's one-line summary: at most one decimal, so a 33° batter reads
    /// "Fill 64.9%", not "Fill 64.941%". Fields keep the full precision.
    /// </summary>
    public static string FormatSlopeDegreesBrief(double degrees) =>
        SlopeInput.FormatWithUnit(Math.Tan(degrees * Math.PI / 180.0), SlopeUnitPreference.Current, "0.#");
}
