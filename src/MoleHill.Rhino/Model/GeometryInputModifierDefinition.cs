using MoleHill.Core.Engine;

namespace MoleHill.Rhino.Model;

public abstract class GeometryInputModifierDefinition : ModifierDefinition
{
    /// <summary>
    /// Optional exact source TIN. When present, Triangulate preserves this mesh topology instead of
    /// rebuilding from point/curve inputs.
    /// </summary>
    public SourceReferenceSet TinMesh { get; set; } = new();

    public SourceReferenceSet Points { get; set; } = new();

    public SourceReferenceSet Breaklines { get; set; } = new();

    public SourceReferenceSet Contours { get; set; } = new();

    public SourceReferenceSet Boundary { get; set; } = new();

    public double Tolerance { get; set; }

    public bool PeelBoundaryTriangles { get; set; } = true;

    public double MaxBoundaryEdgeLength { get; set; } = 0.0;

    public double MaxBoundaryAngleDegrees { get; set; } = BoundaryTrianglePeelSettings.DefaultMaxInteriorAngleDegrees;

    public double MaxBoundarySlopeDegrees { get; set; } = 0.0;

    public BoundaryTrianglePeelSettings CreateBoundaryPeelSettings()
    {
        return !PeelBoundaryTriangles || MaxBoundaryEdgeLength < 0.0
            ? BoundaryTrianglePeelSettings.Disabled
            : new BoundaryTrianglePeelSettings
            {
                Enabled = true,
                MaxBoundaryEdgeLength = MaxBoundaryEdgeLength,
                MaxInteriorAngleDegrees = MaxBoundaryAngleDegrees,
                MaxSlopeAngleDegrees = MaxBoundarySlopeDegrees
            };
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return TinMesh;
        yield return Points;
        yield return Breaklines;
        yield return Contours;
        yield return Boundary;
    }
}
