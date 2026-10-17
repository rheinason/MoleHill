// The scored half of mhRelaxRoute's picture: evaluation, marked issues and the fitted curve.
using MoleHill.Core.Routing;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The slower half of the picture: the band scored on the search's terms, the stretches it does not fit, and
/// once fitted, the curve that Keep will add.
/// </summary>
internal sealed record RouteRelaxScore(
    RouteRelaxEvaluation Evaluation,
    IReadOnlyList<(RouteRelaxIssue Issue, Point3d Where, string Tag)> Issues,
    Polyline? Curve,
    int Version);
