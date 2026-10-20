using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>Shared extraction settings. Hosts choose defaults/cache namespace; fitting is always
/// measured against the supplied reference before the same curve builder previews and writes it.</summary>
internal record TerrainCurveExtractionSettings
{
    public double Tolerance { get; set; }
    public double PlanTolerance { get; set; }
    /// <summary>Height between contours; redundant ones are removed and spot heights added where the rebuild misses.</summary>
    public double ContourInterval { get; set; }
    /// <summary>Recovery's runaway guard: contours and spots allowed per hectare of measured ground.</summary>
    public int InputsPerHectare { get; set; } = 1000;
    public bool Contours { get; set; } = true;

    /// <summary>
    /// A terrain: 0.1 m, contours every 0.25 m, its folds held as crease lines. Its 2,000-contour error-driven
    /// predecessor missed tolerance on a 12 ha survey after an hour; interval recovery met it in 90 s.
    /// </summary>
    public static TerrainCurveExtractionSettings Load(ModelUnitContext units, string prefix) => Read(new TerrainCurveExtractionSettings(), units, prefix, .1, .25, .25, 1000);

    protected static T Read<T>(T settings, ModelUnitContext units, string prefix, double tolerance, double plan, double interval, int perHectare)
        where T : TerrainCurveExtractionSettings
    {
        settings.Tolerance = CommandOptionCache.GetLength(prefix + nameof(Tolerance), units, tolerance);
        settings.PlanTolerance = CommandOptionCache.GetLength(prefix + nameof(PlanTolerance), units, plan);
        settings.ContourInterval = CommandOptionCache.GetLength(prefix + nameof(ContourInterval), units, interval);
        settings.InputsPerHectare = CommandOptionCache.GetValue(prefix + "RecoveryPolicy.InputsPerHectare", perHectare);
        settings.Contours = CommandOptionCache.GetValue(prefix + nameof(Contours), 1) == 1;
        return settings;
    }

    public void Save(ModelUnitContext units, string prefix)
    {
        CommandOptionCache.SetLength(prefix + nameof(Tolerance), units, Tolerance);
        CommandOptionCache.SetLength(prefix + nameof(PlanTolerance), units, PlanTolerance);
        CommandOptionCache.SetLength(prefix + nameof(ContourInterval), units, ContourInterval);
        CommandOptionCache.SetValue(prefix + "RecoveryPolicy.InputsPerHectare", InputsPerHectare);
        CommandOptionCache.SetValue(prefix + nameof(Contours), Contours ? 1 : 0);
    }
}
