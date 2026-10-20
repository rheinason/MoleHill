using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>Shared landform-mode orchestration and result ranking for every extraction host.</summary>
public static partial class TerrainCurveRecovery
{
    public enum LandformMode { Auto, Yes, No }

    /// <summary>Auto tries both modes against the same reference, region, fixed breaklines and fitting
    /// options. Prefer meeting tolerance, then fewer curves, then fewer spots; ties retain landforms.</summary>
    public static Result RecoverBest(IndexedTriMesh terrain, Options options, LandformMode mode)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(typeof(LandformMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        // Interval contours take no landform lines; there is nothing to compare.
        if (mode != LandformMode.Auto || options.Policy is { ContourInterval: > 0 }) return Recover(terrain, options, mode == LandformMode.Yes);
        Result with = Recover(terrain, options, true);
        if (with.Termination == Termination.Cancelled || options.ShouldCancel?.Invoke() == true) return with;
        Result without = Recover(terrain, options, false);
        if (without.Termination == Termination.Cancelled) return without;
        bool withMet = with.Termination == Termination.ToleranceSatisfied;
        bool withoutMet = without.Termination == Termination.ToleranceSatisfied;
        if (withMet != withoutMet) return withMet ? with : without;
        if (options.Policy != null && !withMet && !withoutMet)
        {
            if (Math.Abs(with.MaximumDeviation - without.MaximumDeviation) > options.VerticalTolerance * .01)
                return with.MaximumDeviation < without.MaximumDeviation ? with : without;
            if (Math.Abs(with.ExcessErrorVolume - without.ExcessErrorVolume) > options.VerticalTolerance * 1e-6)
                return with.ExcessErrorVolume < without.ExcessErrorVolume ? with : without;
        }
        int withCount = with.Contours.Count + with.LandformLines.Count;
        int withoutCount = without.Contours.Count + without.LandformLines.Count;
        if (withCount != withoutCount) return withCount < withoutCount ? with : without;
        return with.SpotPointCount <= without.SpotPointCount ? with : without;
    }
}
