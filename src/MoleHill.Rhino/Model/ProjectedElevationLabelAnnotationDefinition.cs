namespace MoleHill.Rhino.Model;

public sealed class ProjectedElevationLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public ProjectedElevationLabelAnnotationDefinition()
    {
        Label = "Spot Heights (Points)";
        ValueFormat = "F2";
    }
}
