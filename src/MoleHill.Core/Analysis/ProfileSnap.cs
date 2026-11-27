// Snaps a dragged profile bend's elevation to the ground, a neighbour's level, a round grade or a round height.
namespace MoleHill.Core.Analysis;

/// <summary>The elevation a dragged bend takes, what it snapped to, and from which neighbour for a grade or level.</summary>
public readonly record struct ProfileSnapResult(double Elevation, ProfileSnapKind Kind, double Grade = 0.0, bool FromNext = false);

/// <summary>
/// The snaps a bend drag offers, in the spirit of Rhino's object snaps: within <c>reach</c> (a few pixels,
/// converted to elevation by the caller, so it tightens as the user zooms in) of the cursor's elevation, the
/// nearest of: the ground under the bend, the level of either neighbour, a round grade from either neighbour,
/// or a round elevation. Holding the snap modifier off is the caller's business.
/// </summary>
public static class ProfileSnap
{
    public static ProfileSnapResult Snap(
        double station,
        double elevation,
        double reach,
        ProfileBend? previous,
        ProfileBend? next,
        double? ground,
        SlopeAnalyzer.SlopeUnit unit)
    {
        if (!(reach > 0.0) || !double.IsFinite(elevation))
            return new ProfileSnapResult(elevation, ProfileSnapKind.None);

        ProfileSnapResult best = new(elevation, ProfileSnapKind.None);
        double bestMiss = reach;

        void Offer(double z, ProfileSnapKind kind, double grade = 0.0, bool fromNext = false)
        {
            double miss = Math.Abs(z - elevation);
            // Strictly nearer wins, so on a tie the earlier (more meaningful) snap is kept.
            if (!double.IsFinite(z) || miss > reach || (best.Kind != ProfileSnapKind.None && miss >= bestMiss))
                return;
            best = new ProfileSnapResult(z, kind, grade, fromNext);
            bestMiss = miss;
        }

        if (ground is { } g)
            Offer(g, ProfileSnapKind.Ground);
        if (previous is { } p)
            Offer(p.Elevation, ProfileSnapKind.Level);
        if (next is { } n)
            Offer(n.Elevation, ProfileSnapKind.Level, fromNext: true);

        if (previous is { } before && station - before.Station > 0.0)
        {
            double run = station - before.Station;
            double grade = RoundGrade((elevation - before.Elevation) / run, unit);
            if (grade != 0.0)
                Offer(before.Elevation + grade * run, ProfileSnapKind.Grade, grade);
        }

        if (next is { } after && after.Station - station > 0.0)
        {
            double run = after.Station - station;
            // Grade as travelled, from this bend towards the next one.
            double grade = RoundGrade((after.Elevation - elevation) / run, unit);
            if (grade != 0.0)
                Offer(after.Elevation - grade * run, ProfileSnapKind.Grade, grade, fromNext: true);
        }

        // Coarser than the reach, or every cursor position would be "round" and the snap would always fire.
        double step = AnalysisRange.NiceStep(reach * 5.0);
        Offer(Math.Round(elevation / step) * step, ProfileSnapKind.Elevation);
        return best;
    }

    /// <summary>
    /// The nearest round grade in the unit the user reads: half a percent, five promille, half a degree, or
    /// a whole 1:n (steeper than 1:1 falls back to half percents).
    /// </summary>
    public static double RoundGrade(double grade, SlopeAnalyzer.SlopeUnit unit)
    {
        if (!double.IsFinite(grade))
            return 0.0;
        switch (unit)
        {
            case SlopeAnalyzer.SlopeUnit.Degrees:
            {
                double degrees = Math.Atan(grade) * 180.0 / Math.PI;
                return Math.Tan(Math.Round(degrees * 2.0) / 2.0 * Math.PI / 180.0);
            }
            case SlopeAnalyzer.SlopeUnit.Ratio when Math.Abs(grade) > 1e-9 && Math.Abs(grade) <= 1.0:
            {
                double run = Math.Round(1.0 / Math.Abs(grade));
                return run >= 1.0 ? Math.Sign(grade) / run : 0.0;
            }
            default: // percent and promille: 0.5% is 5 promille
                return Math.Round(grade * 200.0) / 200.0;
        }
    }
}
