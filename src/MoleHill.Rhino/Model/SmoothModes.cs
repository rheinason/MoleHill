namespace MoleHill.Rhino.Model;

/// <summary>The stored values of <see cref="SmoothModifierDefinition.Mode"/>.</summary>
public static class SmoothModes
{
    public const string Smooth = "smooth";

    public const string Round = "round";

    public static bool IsRound(SmoothModifierDefinition smooth) =>
        string.Equals(smooth.Mode, Round, StringComparison.OrdinalIgnoreCase);
}
