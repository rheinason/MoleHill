using System.Globalization;
using System.Xml.Linq;

namespace MoleHill.Core.Interop;

public static class LandXmlCodec
{
    public static TinSurfaceData Read(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("LandXML content is empty.", nameof(xml));

        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        XElement? surface = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Surface");
        if (surface == null)
            throw new FormatException("LandXML contains no Surface element.");

        var result = new TinSurfaceData
        {
            Name = (string?)surface.Attribute("name") ?? "Surface"
        };

        XElement? definition = surface.Elements().FirstOrDefault(e => e.Name.LocalName == "Definition");
        if (definition == null)
            throw new FormatException("LandXML surface contains no TIN Definition.");

        XElement? pnts = definition.Elements().FirstOrDefault(e => e.Name.LocalName == "Pnts");
        if (pnts == null)
            throw new FormatException("LandXML surface contains no points.");

        foreach (XElement point in pnts.Elements().Where(e => e.Name.LocalName == "P"))
        {
            string[] values = point.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 3 || !int.TryParse((string?)point.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                throw new FormatException("LandXML contains a point with an invalid id or coordinate list.");

            result.Points.Add(new TinSurfacePoint(id, Parse(values[0]), Parse(values[1]), Parse(values[2])));
        }

        XElement? faces = definition.Elements().FirstOrDefault(e => e.Name.LocalName == "Faces");

        if (faces != null)
        {
            foreach (XElement face in faces.Elements().Where(e => e.Name.LocalName == "F"))
            {
                string[] ids = face.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length < 3)
                    throw new FormatException("LandXML contains a face with fewer than three point references.");
                result.Triangles.Add(new TinSurfaceTriangle(ParseInt(ids[0]), ParseInt(ids[1]), ParseInt(ids[2])));
            }
        }

        if (result.Points.Count < 3)
            throw new FormatException("LandXML surface contains fewer than three points.");
        return result;
    }

    public static string Write(TinSurfaceData surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        XNamespace ns = "http://www.landxml.org/schema/LandXML-1.2";
        var root = new XElement(ns + "LandXML",
            new XAttribute("version", "1.2"),
            new XAttribute("date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(ns + "Units", new XElement(ns + "Metric")),
            new XElement(ns + "Surfaces",
                new XElement(ns + "Surface",
                    new XAttribute("name", string.IsNullOrWhiteSpace(surface.Name) ? "Surface" : surface.Name),
                    new XElement(ns + "Definition", new XAttribute("surfType", "TIN"),
                        new XElement(ns + "Pnts", surface.Points.Select(p =>
                            new XElement(ns + "P", new XAttribute("id", p.Id.ToString(CultureInfo.InvariantCulture)),
                                Format(p.X), " ", Format(p.Y), " ", Format(p.Z)))),
                        new XElement(ns + "Faces", surface.Triangles.Select(f =>
                            new XElement(ns + "F", f.A.ToString(CultureInfo.InvariantCulture), " ",
                                f.B.ToString(CultureInfo.InvariantCulture), " ", f.C.ToString(CultureInfo.InvariantCulture))))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).ToString();
    }

    private static double Parse(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static XText Format(double value) => new(value.ToString("G17", CultureInfo.InvariantCulture));
}
