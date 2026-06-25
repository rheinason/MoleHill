using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

internal sealed class SlopeMarkerDescriptor : MarkerTypeDescriptor
{
    public override string Kind => "slope";
    public override Type DefinitionType => typeof(SlopeMarkerDefinition);
    public override string AddButtonText => "+ Slope";
    public override string AddButtonHelp => "Add slope markers. By default these place an editable block symbol plus a slope value label.";
    public override int SortOrder => 1;
    public override MarkerDefinition Create() => new SlopeMarkerDefinition();
}
