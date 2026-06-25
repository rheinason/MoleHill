using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

internal sealed class ElevationMarkerDescriptor : MarkerTypeDescriptor
{
    public override string Kind => "elevation";
    public override Type DefinitionType => typeof(ElevationMarkerDefinition);
    public override string AddButtonText => "+ Elevation";
    public override string AddButtonHelp => "Add elevation markers. By default these place an editable block symbol plus a value label.";
    public override int SortOrder => 0;
    public override MarkerDefinition Create() => new ElevationMarkerDefinition();
}
