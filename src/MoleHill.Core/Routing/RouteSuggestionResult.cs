namespace MoleHill.Core.Routing;

/// <summary>The outcome of one <see cref="RouteSuggester.Suggest"/> call.</summary>
public sealed class RouteSuggestionResult
{
    internal RouteSuggestionResult(
        RouteFailure failure,
        IReadOnlyList<RouteAlternative> alternatives,
        int failedLeg,
        int failedPoint,
        double? requiredGrade,
        int droppedNearDuplicates)
    {
        Failure = failure;
        Alternatives = alternatives;
        FailedLeg = failedLeg;
        FailedPoint = failedPoint;
        RequiredGrade = requiredGrade;
        DroppedNearDuplicates = droppedNearDuplicates;
    }

    public RouteFailure Failure { get; }

    public bool Succeeded => Failure == RouteFailure.None;

    /// <summary>Distinct routes, best first. Empty on failure.</summary>
    public IReadOnlyList<RouteAlternative> Alternatives { get; }

    /// <summary>Zero-based leg (between point i and i + 1) a leg failure refers to; -1 otherwise.</summary>
    public int FailedLeg { get; }

    /// <summary>Zero-based point an off-terrain failure refers to; -1 otherwise.</summary>
    public int FailedPoint { get; }

    /// <summary>For <see cref="RouteFailure.ExceedsMaxGrade"/>: the least grade the failed leg would need.</summary>
    public double? RequiredGrade { get; }

    /// <summary>Candidate routes discarded because they ran too close to one already kept.</summary>
    public int DroppedNearDuplicates { get; }

    internal static RouteSuggestionResult Fail(RouteFailure failure, int leg = -1, int point = -1, double? requiredGrade = null) =>
        new(failure, Array.Empty<RouteAlternative>(), leg, point, requiredGrade, 0);
}
