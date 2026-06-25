using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

internal sealed class LowestPointObjectDescriptor : ObjectTypeDescriptor
{
    public override string Kind => "lowest-point";
    public override Type DefinitionType => typeof(LowestPointObjectDefinition);
    public override string DisplayName => "Plant";
    public override string IconLabel => "Z";
    public override string Subtitle => "Place lowest point on terrain";
    public override int AccentArgb => unchecked((int)0xFF1E88E5); // 30,136,229
    public override int SortOrder => 0;
    public override TerrainObjectDefinition Create() => new LowestPointObjectDefinition();
}
