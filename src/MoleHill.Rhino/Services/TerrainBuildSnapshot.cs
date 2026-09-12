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

    /// <summary>
    /// The document's north, as an angle CCW from +X, captured on the document thread. The aspect analysis
    /// reads it; the build has no document access, so it cannot ask <see cref="DocumentNorth"/> itself.
    /// </summary>
    public double NorthAzimuthDegrees { get; init; } = DocumentNorth.DefaultAzimuthDegrees;

    /// <summary>The terrain's annotation style, captured on the document thread. Generated text binds to
    /// its id; marker block instances derive their scale from its effective text height.</summary>
    public AnnotationStyleSnapshot AnnotationStyle { get; init; } =
        new() { StyleName = AnnotationStyleService.DefaultStyleName };

    /// <summary>Hatch pattern indices referenced by this terrain's analyses, resolved on the document
    /// thread. Pattern indices are document-scoped, so the background build cannot look them up.</summary>
    public HatchPatternSnapshot HatchPatterns { get; init; } = new();

    /// <summary>
    /// Where each kind of output lands and how it looks, resolved from the active layer template on
    /// the document thread. Every layer decision in the build reads this — the build has no document
    /// access, so it cannot consult the layer table itself.
    /// </summary>
    public LayerRoleTable LayerRoles { get; init; } = LayerRoleTable.Default;

    public ModelUnitContext ResolvedUnitContext => UnitContext.IsSupported
        ? UnitContext
        : ModelUnitContext.FromUnitSystem(ModelUnitSystem, ModelAbsoluteTolerance);

    public Dictionary<SourceReferenceSet, List<ResolvedSourceObject>> SourceObjects { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    public Dictionary<SourceReferenceSet, ulong> SourceFingerprints { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    public Dictionary<SourceReferenceSet, SourceResolutionDiagnostics> SourceDiagnostics { get; } =
        new(ReferenceEqualityComparer<SourceReferenceSet>.Instance);

    /// <summary>GeoTIFF samples mapped through each Triangulate card's live textured surface.</summary>
    public Dictionary<Guid, List<Point3d>> DemPoints { get; } = new();

    public Dictionary<Guid, ulong> DemFingerprints { get; } = new();

    public Dictionary<Guid, string> DemDiagnostics { get; } = new();

    /// <summary>Local bounding box of each named block definition referenced by a scatter mix, captured on
    /// the main thread (the background build has no doc access). Used by edge-to-edge spacing to size each
    /// block. Keyed by instance-definition name.</summary>
    public Dictionary<string, BoundingBox> BlockDefinitionBounds { get; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, TerrainSectionReferenceSnapshot> SectionTerrains { get; } = new();

    public void DisposeSectionTerrainMeshes()
    {
        foreach (TerrainSectionReferenceSnapshot reference in SectionTerrains.Values)
            reference.Mesh.Dispose();
        SectionTerrains.Clear();
    }
}

internal sealed class TerrainSectionReferenceSnapshot
{
    public required Guid TerrainId { get; init; }

    public required string Name { get; init; }

    public required int ColorArgb { get; init; }

    public required Mesh Mesh { get; init; }

    public required ulong MeshFingerprint { get; init; }
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
