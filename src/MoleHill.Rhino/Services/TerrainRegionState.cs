// Resolved named region boundaries retained with a completed final terrain build.
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainRegionState
{
    public Guid RegionId { get; init; }

    public string Name { get; init; } = "Zone";

    public List<Curve> Boundaries { get; init; } = new();

    public TerrainRegionState Duplicate()
    {
        return new TerrainRegionState
        {
            RegionId = RegionId,
            Name = Name,
            Boundaries = Boundaries.Select(boundary => boundary.DuplicateCurve()).ToList()
        };
    }
}
