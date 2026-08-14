using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainBuildSnapshot
{
    public required TerrainDefinition Terrain { get; init; }

    public required double ModelAbsoluteTolerance { get; init; }

    public required UnitSystem ModelUnitSystem { get; init; }

    public ModelUnitContext UnitContext { get; init; }

    public ModelUnitContext ResolvedUnitContext => UnitContext.IsSupported
        ? UnitContext
        : ModelUnitContext.FromUnitSystem(ModelUnitSystem, ModelAbsoluteTolerance);

    public Dictionary<SourceReferenceSet, List<ResolvedSourceObject>> SourceObjects { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    public Dictionary<SourceReferenceSet, ulong> SourceFingerprints { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    public Dictionary<SourceReferenceSet, SourceResolutionDiagnostics> SourceDiagnostics { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    /// <summary>Local bounding box of each named block definition referenced by a scatter mix, captured on
    /// the main thread (the background build has no doc access). Used by edge-to-edge spacing to size each
    /// block. Keyed by instance-definition name.</summary>
    public Dictionary<string, BoundingBox> BlockDefinitionBounds { get; } = new(StringComparer.Ordinal);
}

internal sealed class SourceResolutionDiagnostics
{
    public int AcceptedObjects { get; set; }
    public int AcceptedNormalOrLayerHiddenObjects { get; set; }
    public int AcceptedObjectHiddenObjects { get; set; }
    public int AcceptedLockedObjects { get; set; }
    public int AcceptedSelectableObjects { get; set; }
    public int RejectedDeletedObjects { get; set; }
    public int RejectedStaleLayerObjects { get; set; }
    public int RejectedWrongLayerObjects { get; set; }
    public int RejectedPageSpaceObjects { get; set; }
    public int RejectedInstanceDefinitionObjects { get; set; }
    public int RejectedReferenceObjects { get; set; }
    public int MissingReferences { get; set; }
    public int ResolvedLayers { get; set; }
    public int MissingLayers { get; set; }
    public BoundingBox AcceptedBounds { get; set; } = BoundingBox.Empty;

    public string Describe()
    {
        string bounds = AcceptedBounds.IsValid
            ? $", XY bounds ({AcceptedBounds.Min.X:G8}, {AcceptedBounds.Min.Y:G8})-({AcceptedBounds.Max.X:G8}, {AcceptedBounds.Max.Y:G8})"
            : string.Empty;
        return $"{AcceptedObjects:N0} accepted ({AcceptedSelectableObjects:N0} selectable, " +
               $"{AcceptedLockedObjects:N0} object-locked, {AcceptedObjectHiddenObjects:N0} object-hidden), " +
               $"{RejectedPageSpaceObjects:N0} page-space rejected, " +
               $"{RejectedStaleLayerObjects:N0} stale layer records rejected, " +
               $"{RejectedWrongLayerObjects:N0} remapped-layer records rejected, {ResolvedLayers:N0} layers{bounds}";
    }
}
