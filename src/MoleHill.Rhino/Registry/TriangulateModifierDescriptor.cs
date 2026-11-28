using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
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
        (TriangulateModifierDefinition.AutoContourMode, "Auto"),
        (TriangulateModifierDefinition.ConstrainedContourMode, "Exact"),
        (TriangulateModifierDefinition.VerticesOnlyContourMode, "Vertices"),
    };

    // The former labels' own descriptions, kept on each segment.
    private static readonly IReadOnlyDictionary<string, string> ContourModeHelp = new Dictionary<string, string>
    {
        [TriangulateModifierDefinition.AutoContourMode] = "Keeps contour edges on smaller sets and samples only the vertices of dense ones. Fast for large sets.",
        [TriangulateModifierDefinition.ConstrainedContourMode] = "Keeps every contour segment as an edge. Exact, slower on large sets.",
        [TriangulateModifierDefinition.VerticesOnlyContourMode] = "Samples contour vertices without inserting their edges. Fastest.",
    };

    public override string Kind => "triangulate";
    public override Type DefinitionType => typeof(TriangulateModifierDefinition);
    public override string DisplayName => "Triangulate";
    public override string IconName => "ModTriangulate";

    public override int AccentArgb => unchecked((int)0xFF1976D2);

    public override bool CanCreateFromMenu => false; // pinned base modifier
    public override string Subtitle => "Terrain geometry";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new TriangulateModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TriangulateStage.Run(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var t = (TriangulateModifierDefinition)modifier;
        return $"{CountSources(t.DemSurface)} DEM · {Count(CountSources(t.Points), "point", "points")} · " +
            $"{Count(CountSources(t.Breaklines), "breakline", "breaklines")} · {Count(CountSources(t.Contours), "contour", "contours")}";
    }

    // The everyday sources first, then closed sections for everything else.
    public override IReadOnlyList<ModifierParam> Parameters { get; } = new ModifierParam[]
    {
        ModifierParam.Sources(
            "Points", "Points",
            m => ((TriangulateModifierDefinition)m).Points,
            RhinoObjectType.Point | RhinoObjectType.PointSet,
            "Points that become terrain vertices at their own height."),
        ModifierParam.Sources(
            "Contours", "Contours",
            m => ((TriangulateModifierDefinition)m).Contours,
            RhinoObjectType.Curve,
            "Contour curves. Their vertices set terrain height. Contour Mode controls whether the edges between them are kept."),
        ModifierParam.Sources(
            "Breaklines", "Breaklines",
            m => ((TriangulateModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve,
            "Curves whose edges the mesh keeps. Each vertex sets the height along the edge."),
        // Design review UI-01 to UI-04: the rarer sources, the boundary roles and the settings each sit in a
        // closed section whose summary names what is set.
        ModifierParam.Section("additionalSources", "Additional Sources", summary: m => Named(
            ("TIN", T(m).TinMesh.HasReferences), ("DEM", T(m).DemSurface.HasReferences))),
        ModifierParam.Sources(
            "TinMesh", "Exact TIN Mesh",
            m => ((TriangulateModifierDefinition)m).TinMesh,
            RhinoObjectType.Mesh,
            "Optional exact triangle mesh source. When set, its topology is preserved and the point/curve inputs are ignored."),
        ModifierParam.Sources(
            "DemSurface", "DEM Surface",
            m => ((TriangulateModifierDefinition)m).DemSurface,
            RhinoObjectType.Surface | RhinoObjectType.Brep,
            "Planar surface carrying a numeric GeoTIFF bitmap texture. Move the surface to control the DEM's project placement.",
            action: new RowAction(
                "importGeoTiff", "GeoTIFF...",
                "Create a georeferenced Rhino surface textured with a numeric single-band GeoTIFF and assign it as " +
                "the DEM source. Move the surface to align geographic and project coordinates.")),
        ModifierParam.Section("boundaries", "Boundaries", summary: m => Named(
            ("Outer", T(m).OuterBoundaries.HasReferences),
            ("Hide", T(m).HideBoundaries.HasReferences),
            ("Show", T(m).ShowBoundaries.HasReferences),
            ("Data Clip", T(m).DataClipBoundaries.HasReferences))),
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
            "Closed curves that clip the points, contours and breaklines before triangulation, so only data inside them is used.",
            action: new RowAction("dataClipRectangle", "Rectangle", "Draw a closed rectangle and use it as the Data Clip boundary.")),
        ModifierParam.Section("settings", "Settings"),
        ModifierParam.Choice(
            "ContourMode", "Contour Mode", ContourModeOptions,
            m => ((TriangulateModifierDefinition)m).ContourMode,
            (m, v) => ((TriangulateModifierDefinition)m).ContourMode = v ?? TriangulateModifierDefinition.AutoContourMode,
            $"Auto preserves contour edges below {TriangulateModifierDefinition.AutoUnconstrainedContourVertexThreshold:N0} source vertices, then treats dense contour stations as ordinary TIN samples for Grasshopper-like performance. Exact preserves every contour segment. Vertices always samples contour vertices without inserting their edges. Breaklines are kept in every mode.",
            optionHelp: ContourModeHelp),
    }.Concat(GeometryInputParameterCatalog.BoundaryPeel).ToArray();

    private static TriangulateModifierDefinition T(ModifierDefinition m) => (TriangulateModifierDefinition)m;

    private static string? Named(params (string Name, bool Set)[] roles)
    {
        string[] set = roles.Where(role => role.Set).Select(role => role.Name).ToArray();
        return set.Length == 0 ? null : string.Join(", ", set);
    }
}
