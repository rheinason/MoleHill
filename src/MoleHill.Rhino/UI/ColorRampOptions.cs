using System;
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.UI;

/// <summary>What the card is editing, and where its edits should go.</summary>
internal sealed class ColorRampOptions
{
    /// <summary>Shown in the card header — the analysis's own name, so a stack of them reads.</summary>
    public required string Title { get; init; }

    public required ColorRamp Ramp { get; init; }

    public required AnalysisRange Range { get; init; }

    public required AnalysisColorMapper.Mode Mode { get; init; }

    public required double Interval { get; init; }

    public required bool AutoRange { get; init; }

    /// <summary>How the range is anchored — cut/fill stays symmetric about zero however Min/Max are dragged.</summary>
    public required RangeShape Shape { get; init; }

    /// <summary>Normalized histogram bars from the last colouring, or null for no distribution.</summary>
    public double[]? Histogram { get; init; }

    /// <summary>
    /// False when nothing has been coloured yet, so <see cref="Range"/> is a placeholder rather than a
    /// measurement. Cut/fill with no reference surface is the common case: showing a confident
    /// "-1.00 … +1.00" there invents a depth range the terrain has never been compared against.
    /// </summary>
    public bool HasData { get; init; } = true;

    /// <summary>Formats a value in the analysis's own unit — the panel already owns these.</summary>
    public required Func<double, string> FormatValue { get; init; }

    /// <summary>Whether the card starts expanded. The panel remembers this per analysis.</summary>
    public bool Expanded { get; init; }

    /// <summary>
    /// Which stop is selected. Remembered by the panel across card rebuilds: committing an edit saves the
    /// document, which rebuilds the card, and a selection that reset to the first stop every time would
    /// leave "−" pointed at a stop the user was not looking at.
    /// </summary>
    public int SelectedIndex { get; init; }

    /// <summary>
    /// The ramp changed. The flag is true while a drag is still in flight: those commits must not save the
    /// document or refresh the panel, because rebuilding the card mid-gesture destroys the control being
    /// dragged. A final call with false always follows.
    /// </summary>
    public Action<ColorRamp, bool>? OnRampChanged { get; init; }

    /// <summary>Applies a named preset, dropping any per-stop override.</summary>
    public Action<string>? OnPresetPicked { get; init; }

    public Action<AnalysisColorMapper.Mode>? OnModeChanged { get; init; }

    public Action<double, bool>? OnIntervalChanged { get; init; }

    public Action<double, double, bool>? OnRangeChanged { get; init; }

    public Action<bool>? OnAutoRangeChanged { get; init; }

    /// <summary>Told when the card is expanded or collapsed, so the state survives a card rebuild.</summary>
    public Action<bool>? OnExpandedChanged { get; init; }

    /// <summary>Told when the selected stop changes, for the same reason.</summary>
    public Action<int>? OnSelectionChanged { get; init; }
}
