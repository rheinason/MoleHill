using MoleHill.Core.Interop;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class LandXmlCodecTests
{
    [Theory]
    [InlineData(LandXmlLinearUnit.Millimeter, 0.001)]
    [InlineData(LandXmlLinearUnit.Meter, 1.0)]
    [InlineData(LandXmlLinearUnit.Foot, 0.3048)]
    [InlineData(LandXmlLinearUnit.UsSurveyFoot, 1200.0 / 3937.0)]
    public void MetersPerUnit_KnownLandXmlUnit_ReturnsConversion(
        LandXmlLinearUnit unit,
        double expected)
    {
        Assert.Equal(expected, LandXmlLinearUnits.MetersPerUnit(unit), 12);
    }

    [Fact]
    public void Read_MetricWithoutLinearUnit_DefaultsToMetersForLegacyCompatibility()
    {
        const string xml = "<LandXML><Units><Metric /></Units><Surfaces><Surface><Definition><Pnts><P id=\"1\">0 0 0</P><P id=\"2\">0 1 0</P><P id=\"3\">1 0 0</P></Pnts><Faces><F>1 2 3</F></Faces></Definition></Surface></Surfaces></LandXML>";

        TinSurfaceData surface = LandXmlCodec.Read(xml);

        Assert.Equal(LandXmlLinearUnit.Meter, surface.LinearUnit);
    }

    [Fact]
    public void WriteRead_RoundTripsTinSurface()
    {
        var source = new TinSurfaceData { Name = "Test" };
        source.Points.AddRange(new[]
        {
            new TinSurfacePoint(1, 0, 0, 10),
            new TinSurfacePoint(2, 10, 0, 11),
            new TinSurfacePoint(3, 0, 10, 12)
        });
        source.Triangles.Add(new TinSurfaceTriangle(1, 2, 3));

        TinSurfaceData result = LandXmlCodec.Read(LandXmlCodec.Write(source));
        Assert.Equal("Test", result.Name);
        Assert.Equal(source.Points, result.Points);
        Assert.Equal(source.Triangles, result.Triangles);
        Assert.Equal(LandXmlLinearUnit.Meter, result.LinearUnit);
    }

    [Fact]
    public void Read_ExternalLandXml_MapsNorthingEastingAndUnitsToCartesianCoordinates()
    {
        const string xml = """
            <LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2" version="1.2">
              <Units><Imperial linearUnit="foot" /></Units>
              <Surfaces><Surface name="External"><Definition surfType="TIN">
                <Pnts>
                  <P id="10">200 100 7</P>
                  <P id="20">200 110 8</P>
                  <P id="30">210 100 9</P>
                </Pnts>
                <Faces><F>10 20 30</F></Faces>
              </Definition></Surface></Surfaces>
            </LandXML>
            """;

        TinSurfaceData result = LandXmlCodec.Read(xml);

        Assert.Equal(LandXmlLinearUnit.Foot, result.LinearUnit);
        Assert.Equal(new TinSurfacePoint(10, 100, 200, 7), result.Points[0]);
    }

    [Fact]
    public void Write_UsesNorthingEastingOrderAndCompleteUnitDeclaration()
    {
        var source = new TinSurfaceData { Name = "Units", LinearUnit = LandXmlLinearUnit.UsSurveyFoot };
        source.Points.AddRange([
            new TinSurfacePoint(1, 100, 200, 7),
            new TinSurfacePoint(2, 110, 200, 8),
            new TinSurfacePoint(3, 100, 210, 9)]);
        source.Triangles.Add(new TinSurfaceTriangle(1, 2, 3));

        string xml = LandXmlCodec.Write(source);

        Assert.Contains("linearUnit=\"USSurveyFoot\"", xml);
        Assert.Contains("areaUnit=\"squareFoot\"", xml);
        Assert.Contains(">200 100 7<", xml);
    }

    [Fact]
    public void ReadAll_MultipleSurfaces_ReturnsEverySurfaceWithoutWholeDocumentSelection()
    {
        const string xml = """
            <LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2" version="1.2">
              <Units><Metric linearUnit="meter" /></Units>
              <Surfaces>
                <Surface name="A"><Definition surfType="TIN"><Pnts><P id="1">0 0 0</P><P id="2">0 1 0</P><P id="3">1 0 0</P></Pnts><Faces><F>1 2 3</F></Faces></Definition></Surface>
                <Surface name="B"><Definition surfType="TIN"><Pnts><P id="4">0 0 1</P><P id="5">0 1 1</P><P id="6">1 0 1</P></Pnts><Faces><F>4 5 6</F></Faces></Definition></Surface>
              </Surfaces>
            </LandXML>
            """;

        IReadOnlyList<TinSurfaceData> result = LandXmlCodec.ReadAll(xml);

        Assert.Equal(2, result.Count);
        Assert.Equal(["A", "B"], result.Select(surface => surface.Name));
        Assert.Throws<FormatException>(() => LandXmlCodec.Read(xml));
    }

    [Theory]
    [InlineData("<P id=\"1\">0 0 0</P><P id=\"1\">0 1 0</P><P id=\"3\">1 0 0</P>", "<F>1 1 3</F>")]
    [InlineData("<P id=\"1\">0 0 0</P><P id=\"2\">0 1 0</P><P id=\"3\">1 0 0</P>", "<F>1 2 99</F>")]
    public void Read_InvalidIdsOrFaceReferences_Throws(string points, string faces)
    {
        string xml = $"<LandXML><Units><Metric linearUnit=\"meter\" /></Units><Surfaces><Surface><Definition><Pnts>{points}</Pnts><Faces>{faces}</Faces></Definition></Surface></Surfaces></LandXML>";

        Assert.Throws<FormatException>(() => LandXmlCodec.Read(xml));
    }
}
