using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MoleHill.Core.Interop;

public static class LandXmlCodec
{
    public static TinSurfaceData Read(string xml)
    {
        IReadOnlyList<TinSurfaceData> surfaces = ReadAll(xml);
        return surfaces.Count switch
        {
            1 => surfaces[0],
            _ => throw new FormatException($"LandXML contains {surfaces.Count} surfaces; use ReadAll to select or import them all.")
        };
    }

    public static IReadOnlyList<TinSurfaceData> ReadAll(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("LandXML content is empty.", nameof(xml));

        using var textReader = new StringReader(xml);
        return ReadAll(textReader);
    }

    public static IReadOnlyList<TinSurfaceData> ReadAll(TextReader textReader)
    {
        ArgumentNullException.ThrowIfNull(textReader);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        var surfaces = new List<TinSurfaceData>();
        LandXmlLinearUnit linearUnit = LandXmlLinearUnit.Meter;
        using XmlReader reader = XmlReader.Create(textReader, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName is "Metric" or "Imperial")
            {
                linearUnit = ParseLinearUnit(reader.LocalName, reader.GetAttribute("linearUnit"));
                continue;
            }

            if (reader.LocalName != "Surface")
                continue;

            using XmlReader subtree = reader.ReadSubtree();
            XElement surfaceElement = XElement.Load(subtree, LoadOptions.None);
            surfaces.Add(ParseSurface(surfaceElement, linearUnit));
        }

        if (surfaces.Count == 0)
            throw new FormatException("LandXML contains no Surface element.");
        return surfaces;
    }

    public static string Write(TinSurfaceData surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ValidateSurface(surface);

        XNamespace ns = "http://www.landxml.org/schema/LandXML-1.2";
        var root = new XElement(ns + "LandXML",
            new XAttribute("version", "1.2"),
            new XAttribute("date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CreateUnitsElement(ns, surface.LinearUnit),
            new XElement(ns + "Surfaces",
                new XElement(ns + "Surface",
                    new XAttribute("name", string.IsNullOrWhiteSpace(surface.Name) ? "Surface" : surface.Name),
                    new XElement(ns + "Definition", new XAttribute("surfType", "TIN"),
                        new XElement(ns + "Pnts", surface.Points.Select(p =>
                            new XElement(ns + "P", new XAttribute("id", p.Id.ToString(CultureInfo.InvariantCulture)),
                                // LandXML coordinate locations are Northing, Easting, Elevation (Y, X, Z).
                                Format(p.Y), " ", Format(p.X), " ", Format(p.Z)))),
                        new XElement(ns + "Faces", surface.Triangles.Select(f =>
                            new XElement(ns + "F", f.A.ToString(CultureInfo.InvariantCulture), " ",
                                f.B.ToString(CultureInfo.InvariantCulture), " ", f.C.ToString(CultureInfo.InvariantCulture))))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).ToString();
    }

    private static TinSurfaceData ParseSurface(XElement surface, LandXmlLinearUnit linearUnit)
    {
        var result = new TinSurfaceData
        {
            Name = (string?)surface.Attribute("name") ?? "Surface",
            LinearUnit = linearUnit
        };
        XElement? definition = surface.Elements().FirstOrDefault(e => e.Name.LocalName == "Definition");
        if (definition == null)
            throw new FormatException($"LandXML surface '{result.Name}' contains no TIN Definition.");

        XElement? pnts = definition.Elements().FirstOrDefault(e => e.Name.LocalName == "Pnts");
        if (pnts == null)
            throw new FormatException($"LandXML surface '{result.Name}' contains no points.");

        foreach (XElement point in pnts.Elements().Where(e => e.Name.LocalName == "P"))
        {
            string[] values = point.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 3 || !int.TryParse((string?)point.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                throw new FormatException("LandXML contains a point with an invalid id or coordinate list.");

            // LandXML is Northing, Easting, Elevation. The DTO is Cartesian X, Y, Z.
            double y = Parse(values[0]);
            double x = Parse(values[1]);
            double z = Parse(values[2]);
            result.Points.Add(new TinSurfacePoint(id, x, y, z));
        }

        XElement? faces = definition.Elements().FirstOrDefault(e => e.Name.LocalName == "Faces");
        if (faces != null)
        {
            foreach (XElement face in faces.Elements().Where(e => e.Name.LocalName == "F"))
            {
                string[] ids = face.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length != 3)
                    throw new FormatException("LandXML contains a face that is not triangular.");
                result.Triangles.Add(new TinSurfaceTriangle(ParseInt(ids[0]), ParseInt(ids[1]), ParseInt(ids[2])));
            }
        }

        ValidateSurface(result);
        return result;
    }

    private static void ValidateSurface(TinSurfaceData surface)
    {
        if (surface.Points.Count < 3)
            throw new FormatException("LandXML surface contains fewer than three points.");

        var pointIds = new HashSet<int>();
        foreach (TinSurfacePoint point in surface.Points)
        {
            if (!pointIds.Add(point.Id))
                throw new FormatException($"LandXML surface contains duplicate point id {point.Id}.");
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                throw new FormatException($"LandXML point {point.Id} contains a non-finite coordinate.");
        }

        foreach (TinSurfaceTriangle face in surface.Triangles)
        {
            if (face.A == face.B || face.B == face.C || face.C == face.A)
                throw new FormatException("LandXML surface contains a face with duplicate point references.");
            if (!pointIds.Contains(face.A) || !pointIds.Contains(face.B) || !pointIds.Contains(face.C))
                throw new FormatException("LandXML surface contains a face that references a missing point id.");
        }
    }

    private static XElement CreateUnitsElement(XNamespace ns, LandXmlLinearUnit unit)
    {
        bool metric = unit is LandXmlLinearUnit.Millimeter or LandXmlLinearUnit.Centimeter or
            LandXmlLinearUnit.Meter or LandXmlLinearUnit.Kilometer;
        string linearUnit = unit switch
        {
            LandXmlLinearUnit.Millimeter => "millimeter",
            LandXmlLinearUnit.Centimeter => "centimeter",
            LandXmlLinearUnit.Meter => "meter",
            LandXmlLinearUnit.Kilometer => "kilometer",
            LandXmlLinearUnit.Inch => "inch",
            LandXmlLinearUnit.Foot => "foot",
            LandXmlLinearUnit.UsSurveyFoot => "USSurveyFoot",
            LandXmlLinearUnit.Mile => "mile",
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };

        XElement unitSystem = metric
            ? new XElement(ns + "Metric",
                new XAttribute("areaUnit", "squareMeter"),
                new XAttribute("linearUnit", linearUnit),
                new XAttribute("volumeUnit", "cubicMeter"),
                new XAttribute("temperatureUnit", "celsius"),
                new XAttribute("pressureUnit", "milliBars"),
                new XAttribute("diameterUnit", "millimeter"),
                new XAttribute("angularUnit", "decimal degrees"),
                new XAttribute("directionUnit", "decimal degrees"))
            : new XElement(ns + "Imperial",
                new XAttribute("areaUnit", "squareFoot"),
                new XAttribute("linearUnit", linearUnit),
                new XAttribute("volumeUnit", "cubicYard"),
                new XAttribute("temperatureUnit", "fahrenheit"),
                new XAttribute("pressureUnit", "inchHG"),
                new XAttribute("diameterUnit", "inch"),
                new XAttribute("angularUnit", "decimal degrees"),
                new XAttribute("directionUnit", "decimal degrees"));
        return new XElement(ns + "Units", unitSystem);
    }

    private static LandXmlLinearUnit ParseLinearUnit(string system, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Older LandXML producers, including earlier MoleHill builds, sometimes emitted
            // only the unit-system element. Retain the conventional system default.
            return system == "Imperial" ? LandXmlLinearUnit.Foot : LandXmlLinearUnit.Meter;
        }

        string normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "millimeter" or "millimetre" => LandXmlLinearUnit.Millimeter,
            "centimeter" or "centimetre" => LandXmlLinearUnit.Centimeter,
            "meter" or "metre" => LandXmlLinearUnit.Meter,
            "kilometer" or "kilometre" => LandXmlLinearUnit.Kilometer,
            "inch" => LandXmlLinearUnit.Inch,
            "foot" or "feet" => LandXmlLinearUnit.Foot,
            "ussurveyfoot" or "ussurveyfeet" => LandXmlLinearUnit.UsSurveyFoot,
            "mile" => LandXmlLinearUnit.Mile,
            _ => throw new FormatException($"Unsupported LandXML linear unit '{value}'.")
        };
    }

    private static double Parse(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static XText Format(double value) => new(value.ToString("G17", CultureInfo.InvariantCulture));
}
