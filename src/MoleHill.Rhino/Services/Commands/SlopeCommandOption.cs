using MoleHill.Core.Analysis;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The slope value + unit option pair every slope-taking command shares, so mhSlopeCurve,
/// mhGradientInterpolation, mhOffsetFeature and mhSuggestRoute no longer each demand a different unit — one asked
/// for percent, one for promille and one for degrees, and none of them said which at the prompt.
///
/// <para>The command holds a slope as a ratio; the option shows and reads it in the user's slope
/// unit (<see cref="SlopeUnitPreference"/>), the same preference the panel's cards use. Switching
/// units here therefore switches them in the panel too — it is one setting, reachable from wherever
/// the user happens to be. Under the Ratio unit the value is the run <c>n</c> of 1:n, which is how
/// that unit has always been entered at the command line.</para>
/// </summary>
internal sealed class SlopeCommandOption
{
    /// <summary>Steepest ratio still expressible as an angle, so switching to Degrees cannot fail.</summary>
    private static readonly double MaxRatio = Math.Tan(RhinoMath.ToRadians(SlopeInput.MaxSlopeDegrees));

    private static readonly (string Label, SlopeAnalyzer.SlopeUnit Unit)[] UnitChoices =
    {
        ("Percent", SlopeAnalyzer.SlopeUnit.Percent),
        ("Promille", SlopeAnalyzer.SlopeUnit.Promille),
        ("Ratio1toN", SlopeAnalyzer.SlopeUnit.Ratio),
        ("Degrees", SlopeAnalyzer.SlopeUnit.Degrees),
    };

    private readonly string _cacheKey;
    private readonly string _optionName;
    private SlopeAnalyzer.SlopeUnit _unit;
    private OptionDouble _value = new(0.0);
    private int _unitOptionIndex = -1;
    private double _ratio;

    /// <param name="ignoreCachedValue">Opens at <paramref name="defaultRatio" /> rather than at the
    /// value last accepted. Set it when the default is measured from the geometry in hand — a slope
    /// read off the curve being edited describes that curve, and a value remembered from the last
    /// one does not.</param>
    /// <param name="optionName">The command-line label. "Slope" unless the value is a limit rather than
    /// a slope to apply, as with mhSuggestRoute's MaxGrade.</param>
    public SlopeCommandOption(string cacheKey, double defaultRatio, bool ignoreCachedValue = false, string optionName = "Slope")
    {
        _cacheKey = cacheKey;
        _optionName = optionName;
        _ratio = ignoreCachedValue ? defaultRatio : CommandOptionCache.GetValue(cacheKey, defaultRatio);
        _unit = SlopeUnitPreference.Current;
    }

    /// <summary>The slope as rise/run — what the geometry actually needs.</summary>
    public double Ratio => _ratio;

    /// <summary>Sets the slope from elsewhere (a panel field), remembered as a typed value would be.</summary>
    public void SetRatio(double ratio)
    {
        _ratio = Math.Clamp(ratio, -MaxRatio, MaxRatio);
        CommandOptionCache.SetValue(_cacheKey, _ratio);
    }

    /// <summary>Human-readable slope for a prompt or a report line.</summary>
    public string Description => SlopeInput.FormatWithUnit(_ratio, _unit);

    /// <summary>
    /// Adds the pair to a getter. Getters are rebuilt on every option loop, so this re-reads the
    /// preference each time and relabels itself when the unit changed on the previous pass.
    /// </summary>
    public void AddTo(GetBaseClass get)
    {
        _unit = SlopeUnitPreference.Current;

        // Deliberately the unbounded constructor. A slope option must accept negatives (a falling
        // batter) and has no sensible fixed ceiling once the unit can be percent, promille or the
        // run of 1:n — the ratio is clamped in Commit instead. Note the three-argument overload
        // OptionDouble(value, setLowerLimit, limit) makes `limit` an *upper* bound when
        // setLowerLimit is false, which silently capped this option at zero.
        _value = new OptionDouble(ToDisplayValue(_ratio, _unit));
        get.AddOptionDouble(_optionName, ref _value);
        _unitOptionIndex = get.AddOptionList("Units", UnitChoices.Select(choice => choice.Label), IndexOf(_unit));
    }

    /// <summary>
    /// Reads both options back after an interaction with the getter. The typed number is always
    /// interpreted in the unit that was on screen when it was typed, and only then is a newly picked
    /// unit applied — otherwise switching from Percent to Degrees would read "25" as 25 degrees.
    ///
    /// <para><paramref name="result"/> gates the unit list deliberately: <c>OptionIndex</c> reports
    /// the last option touched and is not cleared by a subsequent point or Enter, so reading it
    /// unconditionally would re-apply a unit chosen on an earlier pass.</para>
    /// </summary>
    public void Commit(GetBaseClass get, GetResult result)
    {
        // A number the unit cannot hold (an angle past MaxSlopeDegrees, which tan() would wrap into a
        // falling slope) is rejected rather than clamped, exactly as a panel field rejects it: the
        // previous slope stays and is shown again on the next pass.
        if (SlopeInput.TryConvertDisplayNumber(_value.CurrentValue, _unit, out double ratio))
            _ratio = Math.Clamp(ratio, -MaxRatio, MaxRatio);
        CommandOptionCache.SetValue(_cacheKey, _ratio);

        if (result != GetResult.Option || _unitOptionIndex < 0 || get.OptionIndex() != _unitOptionIndex)
            return;

        int picked = get.Option().CurrentListOptionIndex;
        if (picked >= 0 && picked < UnitChoices.Length)
            SlopeUnitPreference.Current = UnitChoices[picked].Unit;
    }

    private static int IndexOf(SlopeAnalyzer.SlopeUnit unit)
    {
        for (int index = 0; index < UnitChoices.Length; index++)
        {
            if (UnitChoices[index].Unit == unit)
                return index;
        }

        return 0;
    }

    /// <summary>Ratio to the number the option shows. Ratio unit shows the run n of 1:n.</summary>
    private static double ToDisplayValue(double ratio, SlopeAnalyzer.SlopeUnit unit) =>
        SlopeInput.ToDisplayNumber(ratio, unit);
}
