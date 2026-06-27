using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainBuildSnapshot
{
    public required TerrainDefinition Terrain { get; init; }

    public required double ModelAbsoluteTolerance { get; init; }

    public required UnitSystem ModelUnitSystem { get; init; }

    public Dictionary<SourceReferenceSet, List<ResolvedSourceObject>> SourceObjects { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    public Dictionary<SourceReferenceSet, ulong> SourceFingerprints { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    /// <summary>Local bounding box of each named block definition referenced by a scatter mix, captured on
    /// the main thread (the background build has no doc access). Used by edge-to-edge spacing to size each
    /// block. Keyed by instance-definition name.</summary>
    public Dictionary<string, BoundingBox> BlockDefinitionBounds { get; } = new(StringComparer.Ordinal);
}
