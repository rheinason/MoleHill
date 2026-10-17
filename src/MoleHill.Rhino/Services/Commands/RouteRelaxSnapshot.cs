// What the mhRelaxRoute viewport and panel show of the band at one moment, handed between threads.
using MoleHill.Core.Routing;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// An immutable picture of the relaxing band: its stations draped on the ground, its pins, and the score.
/// The relax worker publishes one after each batch of sweeps; the conduit and the panel read whichever is
/// newest without locking.
/// </summary>
internal sealed record RouteRelaxSnapshot(
    Polyline Band,
    IReadOnlyList<(int Id, Point3d Point)> Pins,
    double Cost,
    double SketchCost,
    bool Settled,
    int Sweeps)
{
    /// <summary>How much cheaper than the sketch the band is, as a share.</summary>
    public double Improvement => SketchCost > 0.0 ? 1.0 - (Cost / SketchCost) : 0.0;
}
