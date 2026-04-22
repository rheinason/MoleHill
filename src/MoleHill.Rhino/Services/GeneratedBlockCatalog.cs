using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime;

namespace MoleHill.Rhino.Services;

internal static class GeneratedBlockCatalog
{
    public const string DisplayToken = "Display";
    public const string ValueToken = "VALUE";
    public const string PrefixToken = "Prefix";
    public const string SuffixToken = "Suffix";
    public const string UnitToken = "Unit";
    public const string NameToken = "Name";
    public const string IndexToken = "Index";
    public const string DistanceToken = "Distance";

    public static string GetDefaultDefinitionName(MarkerBlockTemplate template) => template switch
    {
        MarkerBlockTemplate.Elevation => "MoleHill_ElevationMarker",
        MarkerBlockTemplate.Slope => "MoleHill_SlopeMarker",
        MarkerBlockTemplate.AnnotationElevation => "MoleHill_AnalysisElevation",
        MarkerBlockTemplate.AnnotationSlope => "MoleHill_AnalysisSlope",
        _ => "MoleHill_Block"
    };

    public static List<GeometryBase> CreateBlockGeometry(MarkerBlockTemplate template)
    {
        var geometry = new List<GeometryBase>();
        switch (template)
        {
            case MarkerBlockTemplate.Elevation:
                geometry.Add(new Circle(Plane.WorldXY, 0.8).ToNurbsCurve());
                geometry.Add(new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.8, 0.0, 0.0)));
                geometry.Add(new LineCurve(new Point3d(0.0, -0.8, 0.0), new Point3d(0.0, 0.8, 0.0)));
                break;
            case MarkerBlockTemplate.Slope:
                geometry.Add(new PolylineCurve(new[]
                {
                    new Point3d(-0.8, -0.2, 0.0),
                    new Point3d(0.4, -0.2, 0.0),
                    new Point3d(0.4, -0.6, 0.0),
                    new Point3d(0.9, 0.0, 0.0),
                    new Point3d(0.4, 0.6, 0.0),
                    new Point3d(0.4, 0.2, 0.0),
                    new Point3d(-0.8, 0.2, 0.0)
                }));
                geometry.Add(new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.9, 0.0, 0.0)));
                break;
            case MarkerBlockTemplate.AnnotationElevation:
                geometry.Add(new Circle(Plane.WorldXY, 0.45).ToNurbsCurve());
                geometry.Add(new LineCurve(new Point3d(-0.45, 0.0, 0.0), new Point3d(0.45, 0.0, 0.0)));
                geometry.Add(new LineCurve(new Point3d(0.0, -0.45, 0.0), new Point3d(0.0, 0.45, 0.0)));
                geometry.Add(CreateDisplayText(new Point3d(0.8, 0.0, 0.0), template));
                break;
            case MarkerBlockTemplate.AnnotationSlope:
                geometry.Add(new PolylineCurve(new[]
                {
                    new Point3d(-0.55, -0.15, 0.0),
                    new Point3d(0.05, -0.15, 0.0),
                    new Point3d(0.05, -0.4, 0.0),
                    new Point3d(0.45, 0.0, 0.0),
                    new Point3d(0.05, 0.4, 0.0),
                    new Point3d(0.05, 0.15, 0.0),
                    new Point3d(-0.55, 0.15, 0.0)
                }));
                geometry.Add(new LineCurve(new Point3d(-0.55, 0.0, 0.0), new Point3d(0.45, 0.0, 0.0)));
                geometry.Add(CreateDisplayText(new Point3d(0.8, 0.0, 0.0), template));
                break;
        }

        return geometry;
    }

    private static TextEntity CreateDisplayText(Point3d origin, MarkerBlockTemplate template)
    {
        return new TextEntity
        {
            Plane = new Plane(origin, Vector3d.XAxis, Vector3d.YAxis),
            RichText = CreateDisplayFormula(template),
            TextHeight = 1.0,
            Justification = TextJustification.MiddleLeft,
            MaskFrame = DimensionStyle.MaskFrame.NoFrame
        };
    }

    private static string CreateDisplayFormula(MarkerBlockTemplate template)
    {
        string valuePrompt = template == MarkerBlockTemplate.AnnotationElevation
            ? "Elevation value"
            : "value 1";

        return string.Concat(
            CreateBlockUserTextFormula(PrefixToken, "Prefix Value"),
            CreateBlockUserTextFormula(ValueToken, valuePrompt),
            CreateBlockUserTextFormula(SuffixToken, "Suffix Value"));
    }

    private static string CreateBlockUserTextFormula(string key, string prompt)
    {
        return $@"%<UserText(""block"",""{key}"",""{prompt}"","""")>%";
    }
}
