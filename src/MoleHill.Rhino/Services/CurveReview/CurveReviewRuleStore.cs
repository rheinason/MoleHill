// Persists mhInspectCurve rule settings per user with unit-aware dimensional storage.
using MoleHill.Core.Analysis;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class CurveReviewRuleStore
{
    private const string Prefix = "CurveReview.";

    public static CurveReviewRuleSettings Load(RhinoDoc doc)
    {
        ModelUnitContext units = ModelUnitContext.FromDocument(doc);
        global::Rhino.PersistentSettings settings = MoleHillRhinoPlugin.Instance.Settings;
        double radiusMeters = settings.GetDouble(Prefix + "MinimumRadiusMeters", 25.0);
        return new CurveReviewRuleSettings
        {
            MaximumGradeMode = ReadMode(settings, "MaximumGradeMode", CurveReviewRuleMode.Warn),
            MaximumGradePercent = Positive(settings.GetDouble(Prefix + "MaximumGrade", 12.0), 12.0),
            MinimumRadiusMode = ReadMode(settings, "MinimumRadiusMode", CurveReviewRuleMode.Warn),
            MinimumRadius = units.IsSupported ? units.FromMeters(Positive(radiusMeters, 25.0)) : 25.0,
            VerticalBreakMode = ReadMode(settings, "VerticalBreakMode", CurveReviewRuleMode.Report),
            VerticalBreakThresholdPercent = Positive(settings.GetDouble(Prefix + "VerticalBreak", 10.0), 10.0),
            TerrainCoverageMode = ReadMode(settings, "TerrainCoverageMode", CurveReviewRuleMode.Warn),
            MinimumTerrainCoveragePercent = Math.Clamp(settings.GetDouble(Prefix + "TerrainCoverage", 90.0), 0.0, 100.0)
        };
    }

    public static void Save(RhinoDoc doc, CurveReviewRuleSettings value)
    {
        ModelUnitContext units = ModelUnitContext.FromDocument(doc);
        global::Rhino.PersistentSettings settings = MoleHillRhinoPlugin.Instance.Settings;
        settings.SetInteger(Prefix + "MaximumGradeMode", (int)value.MaximumGradeMode);
        settings.SetDouble(Prefix + "MaximumGrade", Positive(value.MaximumGradePercent, 12.0));
        settings.SetInteger(Prefix + "MinimumRadiusMode", (int)value.MinimumRadiusMode);
        settings.SetDouble(Prefix + "MinimumRadiusMeters", units.IsSupported ? units.ToMeters(value.MinimumRadius) : value.MinimumRadius);
        settings.SetInteger(Prefix + "VerticalBreakMode", (int)value.VerticalBreakMode);
        settings.SetDouble(Prefix + "VerticalBreak", Positive(value.VerticalBreakThresholdPercent, 10.0));
        settings.SetInteger(Prefix + "TerrainCoverageMode", (int)value.TerrainCoverageMode);
        settings.SetDouble(Prefix + "TerrainCoverage", Math.Clamp(value.MinimumTerrainCoveragePercent, 0.0, 100.0));
    }

    private static CurveReviewRuleMode ReadMode(global::Rhino.PersistentSettings settings, string key, CurveReviewRuleMode fallback)
    {
        int value = settings.GetInteger(Prefix + key, (int)fallback);
        return Enum.IsDefined(typeof(CurveReviewRuleMode), value) ? (CurveReviewRuleMode)value : fallback;
    }

    private static double Positive(double value, double fallback) => double.IsFinite(value) && value > 0.0 ? value : fallback;
}
