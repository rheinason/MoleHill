using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Mutable per-stage state passed to a modifier descriptor's <see cref="ModifierTypeDescriptor.RunBuildStage"/>.
/// Carries the build loop's locals so a stage handler can read the incoming mesh and write the outgoing
/// mesh/fingerprint (and, for the base triangulate, capture the base mesh) without a central switch.
/// </summary>
internal sealed class ModifierBuildContext
{
    public required TerrainBuildSnapshot Snapshot { get; init; }
    public required TerrainDefinition Terrain { get; init; }
    public required ModifierDefinition Modifier { get; init; }
    public required int Index { get; init; }
    public required string StageKey { get; init; }
    public required TerrainBuildMode Mode { get; init; }
    public required TerrainBuildResult Build { get; init; }
    public required TerrainRuntimeCache RuntimeCache { get; init; }
    public required HashSet<string> UsedStageKeys { get; init; }
    public Func<bool>? ShouldCancel { get; init; }
    public Action<TerrainBuildProgress>? ReportProgress { get; init; }

    public RhinoMesh? CurrentMesh { get; set; }
    public ulong CurrentMeshFingerprint { get; set; }
    public RhinoMesh? BaseMesh { get; set; }
    public ulong BaseMeshFingerprint { get; set; }
}
