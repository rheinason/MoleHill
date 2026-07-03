namespace MoleHill.Core.Sculpting;

/// <summary>The available sculpt brushes. Draw/Subtract push Z along +Z/-Z; Smooth relaxes toward the
/// neighbor average; Flatten pulls toward the plane sampled at stroke start; Grab rigidly translates the
/// captured region in Z; Clay fills toward an offset plane; Noise adds deterministic height variation.</summary>
public enum SculptBrushKind
{
    Draw,
    Subtract,
    Smooth,
    Flatten,
    Grab,
    Clay,
    Noise,
}

/// <summary>Brush falloff profiles (weight over normalized distance from the brush center).</summary>
public enum SculptFalloff
{
    Smooth,
    Linear,
    Sharp,
    Constant,
}

public static class SculptFalloffs
{
    /// <summary>
    /// Evaluates a falloff profile at normalized distance <paramref name="d"/> (0 = brush center,
    /// 1 = brush rim). Returns a weight in [0, 1]: 1 at the center, 0 at and beyond the rim
    /// (except Constant, which stays 1 inside the rim).
    /// </summary>
    public static double Evaluate(SculptFalloff falloff, double d)
    {
        if (d >= 1.0)
            return falloff == SculptFalloff.Constant && d <= 1.0 ? 1.0 : 0.0;
        if (d <= 0.0)
            return 1.0;

        double t = 1.0 - d;
        return falloff switch
        {
            SculptFalloff.Smooth => t * t * (3.0 - 2.0 * t),
            SculptFalloff.Linear => t,
            SculptFalloff.Sharp => t * t,
            SculptFalloff.Constant => 1.0,
            _ => t,
        };
    }
}
