using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainBuildSnapshot
{
    public required TerrainDefinition Terrain { get; init; }

    public required double ModelAbsoluteTolerance { get; init; }

    public Dictionary<SourceReferenceSet, List<ResolvedSourceObject>> SourceObjects { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    public Dictionary<SourceReferenceSet, ulong> SourceFingerprints { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);
}
