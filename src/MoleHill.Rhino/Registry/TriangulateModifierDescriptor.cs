using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

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
    public override void RunBuildStage(ModifierBuildContext context) => TriangulateStage.Run(context);

    // Source rows first, then settings. The panel defers Contour Mode and the boundary-peel rows to its
    // own positions; the schema order remains the shared contract for non-panel consumers.
    public override IReadOnlyList<ModifierParam> Parameters { get; } = new ModifierParam[]
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
            RhinoObjectType.Point | RhinoObjectType.PointSet,
            "Points that become terrain vertices at their own height."),
        ModifierParam.Sources(
            "Breaklines", "Breaklines",
            m => ((TriangulateModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve,
            "Curves whose edges the mesh keeps. Each vertex sets the height along the edge."),
        ModifierParam.Sources(
            "Contours", "Contours",
            m => ((TriangulateModifierDefinition)m).Contours,
            RhinoObjectType.Curve,
            "Contour curves. Their vertices set terrain height. Contour Mode controls whether the edges between them are kept."),
        ModifierParam.Sources(
            "OuterBoundaries", "Outer",
            m => ((TriangulateModifierDefinition)m).OuterBoundaries,
            RhinoObjectType.Curve,
            "Closed curves that trim the finished terrain to the largest valid region they enclose in plan."),
        ModifierParam.Sources(
            "HideBoundaries", "Hide",
            m => ((TriangulateModifierDefinition)m).HideBoundaries,
            RhinoObjectType.Curve,
            "Closed curves whose interiors are removed from the finished terrain."),
        ModifierParam.Sources(
            "ShowBoundaries", "Show",
            m => ((TriangulateModifierDefinition)m).ShowBoundaries,
            RhinoObjectType.Curve,
            "Closed curves that restore regions removed by Hide, without extending beyond Outer."),
        ModifierParam.Sources(
            "DataClipBoundaries", "Data Clip",
            m => ((TriangulateModifierDefinition)m).DataClipBoundaries,
            RhinoObjectType.Curve,
            "Closed curves that clip the points, contours and breaklines before triangulation, so only data inside them is used."),
        ModifierParam.Choice(
            "ContourMode", "Contour Mode", ContourModeOptions,
            m => ((TriangulateModifierDefinition)m).ContourMode,
            (m, v) => ((TriangulateModifierDefinition)m).ContourMode = v ?? TriangulateModifierDefinition.AutoContourMode,
            $"Auto preserves contour edges below {TriangulateModifierDefinition.AutoUnconstrainedContourVertexThreshold:N0} source vertices, then treats dense contour stations as ordinary TIN samples for Grasshopper-like performance. Constrained preserves every contour segment. Vertices only always samples contour vertices without inserting their edges. Breaklines are kept in every mode."),
    }.Concat(GeometryInputParameterCatalog.BoundaryPeel).ToArray();
}
