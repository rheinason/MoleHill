using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

internal sealed class SurfaceOrientedObjectDescriptor : ObjectTypeDescriptor
{
    public override string Kind => "surface-oriented";
    public override Type DefinitionType => typeof(SurfaceOrientedObjectDefinition);
    public override string DisplayName => "Orient";
    public override string IconLabel => "XY";
    public override string Subtitle => "Orient to terrain slope";
    public override int AccentArgb => unchecked((int)0xFF43A047); // 67,160,71
    public override int SortOrder => 1;
    public override TerrainObjectDefinition Create() => new SurfaceOrientedObjectDefinition();
}
