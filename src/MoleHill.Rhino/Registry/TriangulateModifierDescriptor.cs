using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class TriangulateModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "triangulate";
    public override Type DefinitionType => typeof(TriangulateModifierDefinition);
    public override string DisplayName => "Triangulate";
    public override string IconName => "ModTriangulate";
    public override bool CanCreateFromMenu => false; // pinned base modifier
    public override string Subtitle => "Terrain geometry";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new TriangulateModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunTriangulateStage(context);

    // Source rows only. The work-area picker + boundary-peel block are appended via the panel's
    // bespoke-rows hook (they can't be expressed by the parameter schema).
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Points", "Points",
            m => ((TriangulateModifierDefinition)m).Points,
            RhinoObjectType.Point | RhinoObjectType.PointSet),
        ParameterDescriptor.Sources(
            "Breaklines", "Breaklines",
            m => ((TriangulateModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve),
        ParameterDescriptor.Sources(
            "Contours", "Contours",
            m => ((TriangulateModifierDefinition)m).Contours,
            RhinoObjectType.Curve),
        ParameterDescriptor.Sources(
            "Boundary", "Boundary",
            m => ((TriangulateModifierDefinition)m).Boundary,
            RhinoObjectType.Curve),
    };
}
