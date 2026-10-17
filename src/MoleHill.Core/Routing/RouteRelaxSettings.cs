namespace MoleHill.Core.Routing;

/// <summary>
/// What <see cref="RouteRelaxer"/> weighs a sketched route by: the route search's own pricing (distance,
/// steepness, earthworks against the grade limit), how smooth it must be, and how faithfully it keeps to
/// the line the user drew. Lengths are model units; the weights are relative.
/// </summary>
public sealed record RouteRelaxSettings
{
    /// <summary>The longitudinal grade the route aims to stay within, as rise over run.</summary>
    public required double MaxGrade { get; init; }

    public double DistanceWeight { get; init; } = 1.0;

    public double SteepnessWeight { get; init; } = 1.0;

    public double EarthworksWeight { get; init; } = 1.0;

    /// <summary>
    /// How strongly the route keeps to the sketch: 0 lets it go wherever the costs take it between the pins,
    /// 1 (the default) holds it within about a corridor of the line, 10 barely lets it move.
    /// </summary>
    public double Fidelity { get; init; } = 1.0;

    /// <summary>Width the cut and fill are measured over, and the scale the sketch is held to.</summary>
    public required double CorridorWidth { get; init; }

    /// <summary>The tightest plan radius the route should take; 0 for none.</summary>
    public double MinimumRadius { get; init; }

    /// <summary>As <see cref="RouteRequest.Simplicity"/>: broader bends and plainer grades for more earthwork.</summary>
    public double Simplicity { get; init; }

    /// <summary>Vertical tolerance for the editable curve's profile, model units; 0 picks one from the cell size.</summary>
    public double ProfileTolerance { get; init; }

    /// <summary>Plan tolerance for the editable curve, model units; 0 picks one from the corridor width.</summary>
    public double CurveTolerance { get; init; }

    /// <summary>Cut or fill deeper than this is reported as a stretch the route does not fit; 0 picks one from the corridor width.</summary>
    public double DeepCutFillDepth { get; init; }

    internal double SimplicityScale => 1.0 + Math.Max(0.0, Simplicity);

    /// <summary>The radius bends are priced at: the minimum radius, or the corridor width, widened by Simplicity.</summary>
    internal double BendRadius => (MinimumRadius > 0.0 ? MinimumRadius : CorridorWidth) * SimplicityScale;

    /// <summary>The distance from the sketch at which keeping to it costs as much as the route's own length.</summary>
    internal double FidelityScale => Math.Max(2.0 * CorridorWidth, MinimumRadius);

    internal double DeepDepth => DeepCutFillDepth > 0.0 ? DeepCutFillDepth : 0.25 * CorridorWidth;

    internal bool IsValid =>
        MaxGrade > 0.0 && double.IsFinite(MaxGrade) &&
        DistanceWeight >= 0.0 && SteepnessWeight >= 0.0 && EarthworksWeight >= 0.0 &&
        DistanceWeight + SteepnessWeight + EarthworksWeight > 0.0 &&
        Fidelity >= 0.0 && double.IsFinite(Fidelity) &&
        CorridorWidth > 0.0 && double.IsFinite(CorridorWidth) &&
        MinimumRadius >= 0.0 && Simplicity >= 0.0;

    /// <summary>
    /// The same weights as a route search request through <paramref name="points"/>, kept near
    /// <paramref name="guide"/> when one is given.
    /// </summary>
    public RouteRequest ToRequest(
        IReadOnlyList<(double X, double Y)> points,
        IReadOnlyList<(double X, double Y)>? guide = null,
        double guideWidth = 0.0) => new()
    {
        Guide = guide,
        GuideWidth = guideWidth,
        Points = points,
        MaxGrade = MaxGrade,
        DistanceWeight = DistanceWeight,
        SteepnessWeight = SteepnessWeight,
        EarthworksWeight = EarthworksWeight,
        CorridorWidth = CorridorWidth,
        MinimumRadius = MinimumRadius,
        Simplicity = Simplicity,
        ProfileTolerance = ProfileTolerance,
        CurveTolerance = CurveTolerance,
        AlternativeCount = 1,
    };
}
