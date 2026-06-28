using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

internal sealed class ScatterObjectDescriptor : ObjectTypeDescriptor
{
    public override string Kind => "scatter";
    public override Type DefinitionType => typeof(ScatterObjectDefinition);
    public override string DisplayName => "Scatter";
    public override string IconLabel => "S";
    public override string? IconName => "ObjScatter";
    public override string Subtitle => "Scatter blocks across boundaries";
    public override int AccentArgb => unchecked((int)0xFF8E44AD); // 142,68,173
    public override int SortOrder => 2;
    public override TerrainObjectDefinition Create() => new ScatterObjectDefinition();
}
