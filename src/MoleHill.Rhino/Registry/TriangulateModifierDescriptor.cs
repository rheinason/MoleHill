using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class TriangulateModifierDescriptor : ModifierTypeDescriptor
{
    private static readonly IReadOnlyList<(string Key, string Label)> ContourModeOptions = new[]
    {
        (TriangulateModifierDefinition.AutoContourMode, "Auto (fast for large sets)"),
        (TriangulateModifierDefinition.ConstrainedContourMode, "Constrained (exact)"),
        (TriangulateModifierDefinition.VerticesOnlyContourMode, "Vertices only (fastest)"),
    };

    public override string Kind => "triangulate";
    public override Type DefinitionType => typeof(TriangulateModifierDefinition);
    public override string DisplayName => "Triangulate";
    public override string IconName => "ModTriangulate";
    public override bool CanCreateFromMenu => false; // pinned base modifier
    public override string Subtitle => "Terrain geometry";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new TriangulateModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunTriangulateStage(context);

    // Source rows first, then settings. The panel defers Contour Mode until immediately after its custom
    // work-area row; the schema order remains the shared contract for non-panel consumers.
    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "TinMesh", "Exact TIN Mesh",
            m => ((TriangulateModifierDefinition)m).TinMesh,
            RhinoObjectType.Mesh,
            "Optional exact triangle mesh source. When set, its topology is preserved and the point/curve inputs are ignored."),
        ModifierParam.Sources(
            "DemSurface", "DEM Surface",
            m => ((TriangulateModifierDefinition)m).DemSurface,
            RhinoObjectType.Surface | RhinoObjectType.Brep,
            "Planar surface carrying a numeric GeoTIFF bitmap texture. Move the surface to control the DEM's project placement."),
        ModifierParam.Sources(
            "Points", "Points",
            m => ((TriangulateModifierDefinition)m).Points,
            RhinoObjectType.Point | RhinoObjectType.PointSet),
        ModifierParam.Sources(
            "Breaklines", "Breaklines",
            m => ((TriangulateModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve),
        ModifierParam.Sources(
            "Contours", "Contours",
            m => ((TriangulateModifierDefinition)m).Contours,
            RhinoObjectType.Curve),
        ModifierParam.Sources(
            "Boundary", "Boundary",
            m => ((TriangulateModifierDefinition)m).Boundary,
            RhinoObjectType.Curve),
        ModifierParam.Choice(
            "ContourMode", "Contour Mode", ContourModeOptions,
            m => ((TriangulateModifierDefinition)m).ContourMode,
            (m, v) => ((TriangulateModifierDefinition)m).ContourMode = v ?? TriangulateModifierDefinition.AutoContourMode,
            $"Auto preserves contour edges below {TriangulateModifierDefinition.AutoUnconstrainedContourVertexThreshold:N0} source vertices, then treats dense contour stations as ordinary TIN samples for Grasshopper-like performance. Constrained preserves every contour segment. Vertices only always samples contour vertices without inserting their edges. Breaklines and Boundary remain constrained in every mode."),
    };
}
