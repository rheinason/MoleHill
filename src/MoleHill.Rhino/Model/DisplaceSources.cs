using MoleHill.Core.Displacement;

namespace MoleHill.Rhino.Model;

/// <summary>The stored values of <see cref="DisplaceModifierDefinition.Source"/> and <see cref="DisplaceModifierDefinition.Wrap"/>.</summary>
public static class DisplaceSources
{
    public const string Noise = "noise";

    public const string Image = "image";

    public const string Repeat = "repeat";

    public const string Clamp = "clamp";

    public const string Once = "once";

    public static bool IsImage(DisplaceModifierDefinition displace) =>
        string.Equals(displace.Source, Image, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored wrap; anything unknown reads as Repeat.</summary>
    public static DisplacementWrap ParseWrap(string? wrap) =>
        string.Equals(wrap, Clamp, StringComparison.OrdinalIgnoreCase) ? DisplacementWrap.Clamp :
        string.Equals(wrap, Once, StringComparison.OrdinalIgnoreCase) ? DisplacementWrap.Once :
        DisplacementWrap.Repeat;
}
