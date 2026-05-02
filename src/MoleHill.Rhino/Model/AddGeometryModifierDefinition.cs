namespace MoleHill.Rhino.Model;

public sealed class AddGeometryModifierDefinition : GeometryInputModifierDefinition
{
    public AddGeometryModifierDefinition()
    {
        Label = "Add Geometry";
    }

    public static AddGeometryModifierDefinition FromTriangulate(TriangulateModifierDefinition source)
    {
        string label = string.Equals(source.Label, "Triangulate", StringComparison.OrdinalIgnoreCase)
            ? "Add Geometry"
            : source.Label;

        return new AddGeometryModifierDefinition
        {
            Id = source.Id,
            Label = label,
            IsEnabled = source.IsEnabled,
            SchemaVersion = source.SchemaVersion,
            Tolerance = source.Tolerance,
            PeelBoundaryTriangles = source.PeelBoundaryTriangles,
            MaxBoundaryEdgeLength = source.MaxBoundaryEdgeLength,
            MaxBoundaryAngleDegrees = source.MaxBoundaryAngleDegrees,
            MaxBoundarySlopeDegrees = source.MaxBoundarySlopeDegrees,
            Points = CloneSourceSet(source.Points),
            Breaklines = CloneSourceSet(source.Breaklines),
            Contours = CloneSourceSet(source.Contours),
            Boundary = CloneSourceSet(source.Boundary)
        };
    }

    private static SourceReferenceSet CloneSourceSet(SourceReferenceSet source)
    {
        return new SourceReferenceSet
        {
            ObjectIds = source.ObjectIds.ToList(),
            LayerPaths = source.LayerPaths.ToList()
        };
    }
}
