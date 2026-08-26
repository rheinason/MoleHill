namespace MoleHill.Core.Interop;

public sealed class TinSurfaceData
{
    public List<TinSurfacePoint> Points { get; } = new();
    public List<TinSurfaceTriangle> Triangles { get; } = new();
    public string Name { get; set; } = "Surface";

    /// <summary>The linear unit used by point coordinates in the LandXML document.</summary>
    public LandXmlLinearUnit LinearUnit { get; set; } = LandXmlLinearUnit.Meter;
}

public readonly record struct TinSurfacePoint(int Id, double X, double Y, double Z);

public readonly record struct TinSurfaceTriangle(int A, int B, int C);

public enum LandXmlLinearUnit
{
    Millimeter,
    Centimeter,
    Meter,
    Kilometer,
    Inch,
    Foot,
    UsSurveyFoot,
    Mile
}

public static class LandXmlLinearUnits
{
    public static double MetersPerUnit(LandXmlLinearUnit unit) => unit switch
    {
        LandXmlLinearUnit.Millimeter => 0.001,
        LandXmlLinearUnit.Centimeter => 0.01,
        LandXmlLinearUnit.Meter => 1.0,
        LandXmlLinearUnit.Kilometer => 1000.0,
        LandXmlLinearUnit.Inch => 0.0254,
        LandXmlLinearUnit.Foot => 0.3048,
        LandXmlLinearUnit.UsSurveyFoot => 1200.0 / 3937.0,
        LandXmlLinearUnit.Mile => 1609.344,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };
}
