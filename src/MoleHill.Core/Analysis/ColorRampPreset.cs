namespace MoleHill.Core.Analysis;

/// <summary>One named built-in ramp.</summary>
public sealed class ColorRampPreset
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required ColorRamp Ramp { get; init; }
}
