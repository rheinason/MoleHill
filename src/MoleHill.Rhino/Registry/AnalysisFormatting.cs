using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
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
}
