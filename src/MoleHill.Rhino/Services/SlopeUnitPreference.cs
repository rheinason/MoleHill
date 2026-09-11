using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The unit every slope <em>input</em> is shown in — panel cards and the slope-taking commands alike.
///
/// <para>It is a per-user display preference, not document state: the same terrain opened by a civil
/// engineer and a landscape architect should read 1:3 for one and 33% for the other without either
/// editing the file. Storage is unaffected — grading definitions keep persisting degrees — so changing
/// this never dirties a document or triggers a rebuild.</para>
///
/// <para>Persistence is injected rather than read directly from <c>PlugIn.Settings</c>, because the
/// Registry and Services sources are linked into the test project without a live Rhino plug-in. With no
/// provider wired the preference is an in-memory default, which is exactly what tests want.</para>
///
/// <para>Analyses and annotations keep their own per-item <c>Unit</c> property: that one is part of the
/// drawing (a contour label's unit belongs to the document, and two labels may legitimately differ),
/// whereas this one is about how you type. They deliberately do not share a value.</para>
/// </summary>
internal static class SlopeUnitPreference
{
    /// <summary>Percent is the default because it is what the analysis and annotation families already
    /// default to, so a fresh install reads consistently end to end.</summary>
    public const SlopeAnalyzer.SlopeUnit DefaultUnit = SlopeAnalyzer.SlopeUnit.Percent;

    private static SlopeAnalyzer.SlopeUnit _fallback = DefaultUnit;

    /// <summary>Set by the plug-in on load to read the persisted value; null in tests.</summary>
    public static Func<SlopeAnalyzer.SlopeUnit>? Provider { get; set; }

    /// <summary>Set by the plug-in on load to persist a change; null in tests.</summary>
    public static Action<SlopeAnalyzer.SlopeUnit>? Writer { get; set; }

    public static SlopeAnalyzer.SlopeUnit Current
    {
        get => Provider?.Invoke() ?? _fallback;
        set
        {
            _fallback = value;
            Writer?.Invoke(value);
        }
    }

    /// <summary>Dropdown/command-option entries, in the order they are offered.</summary>
    public static IReadOnlyList<SlopeAnalyzer.SlopeUnit> Choices { get; } = new[]
    {
        SlopeAnalyzer.SlopeUnit.Percent,
        SlopeAnalyzer.SlopeUnit.Promille,
        SlopeAnalyzer.SlopeUnit.Ratio,
        SlopeAnalyzer.SlopeUnit.Degrees,
    };

    /// <summary>Resets the in-memory value and unhooks persistence. Test seam.</summary>
    public static void ResetForTests()
    {
        Provider = null;
        Writer = null;
        _fallback = DefaultUnit;
    }
}
