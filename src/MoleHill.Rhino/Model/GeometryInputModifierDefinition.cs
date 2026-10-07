using MoleHill.Core.Engine;
using System.Text.Json.Serialization;

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

    /// <summary>Pre-v32 combined boundary input, retained only so old documents can migrate.</summary>
    [JsonPropertyName("boundary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReferenceSet? LegacyBoundary { get; set; }

    [ModelLength]
    public double Tolerance { get; set; }

    public bool PeelBoundaryTriangles { get; set; } = true;

    [ModelLength]
    public double MaxBoundaryEdgeLength { get; set; } = 0.0;

    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double MaxBoundaryAngleDegrees { get; set; } = BoundaryTrianglePeelSettings.DefaultMaxInteriorAngleDegrees;

    [UnitFree("An angle in degrees; angles do not change with model units.")]
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
        if (LegacyBoundary != null)
            yield return LegacyBoundary;
    }
}
