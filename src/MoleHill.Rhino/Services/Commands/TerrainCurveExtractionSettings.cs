using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>Shared extraction settings. Hosts choose defaults/cache namespace; fitting is always
/// measured against the supplied reference before the same curve builder previews and writes it.</summary>
internal record TerrainCurveExtractionSettings
{
    public double Tolerance { get; set; }
    public double PlanTolerance { get; set; }
    public int MaxContours { get; set; } = 200;
    public int RidgesValleys { get; set; }
    public bool Contours { get; set; } = true;

    public static readonly string[] LandformLabels = { "Auto", "Yes", "No" };

    public static TerrainCurveExtractionSettings Load(ModelUnitContext units, string prefix) => Read(new TerrainCurveExtractionSettings(), units, prefix, .1, .25, 200, 0);

    protected static T Read<T>(T settings, ModelUnitContext units, string prefix, double tolerance, double plan, int budget, int lines)
        where T : TerrainCurveExtractionSettings
    {
        settings.Tolerance = CommandOptionCache.GetLength(prefix + nameof(Tolerance), units, tolerance);
        settings.PlanTolerance = CommandOptionCache.GetLength(prefix + nameof(PlanTolerance), units, plan);
        settings.MaxContours = CommandOptionCache.GetValue(prefix + "MaxCurves", budget);
        settings.RidgesValleys = Math.Clamp(CommandOptionCache.GetValue(prefix + nameof(RidgesValleys), lines), 0, 2);
        settings.Contours = CommandOptionCache.GetValue(prefix + nameof(Contours), 1) == 1;
        return settings;
    }

    public void Save(ModelUnitContext units, string prefix)
    {
        CommandOptionCache.SetLength(prefix + nameof(Tolerance), units, Tolerance);
        CommandOptionCache.SetLength(prefix + nameof(PlanTolerance), units, PlanTolerance);
        CommandOptionCache.SetValue(prefix + "MaxCurves", MaxContours);
        CommandOptionCache.SetValue(prefix + nameof(RidgesValleys), RidgesValleys);
        CommandOptionCache.SetValue(prefix + nameof(Contours), Contours ? 1 : 0);
    }
}
