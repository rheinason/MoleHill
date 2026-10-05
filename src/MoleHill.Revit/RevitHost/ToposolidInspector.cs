// Reads MoleHill identity and editable shape data back off Revit Toposolids, in Rhino units.
using Autodesk.Revit.DB;
using MoleHill.Revit.Planning;
using LineCurve = Rhino.Geometry.LineCurve;
using Point3d = Rhino.Geometry.Point3d;
using RhinoCurve = Rhino.Geometry.Curve;

namespace MoleHill.Revit.RevitHost;

internal sealed class InspectedToposolid
{
    public required object Element { get; init; }
    public string? Key { get; init; }
    public string? Fingerprint { get; init; }
    public bool IsSubdivision { get; init; }
    public long TypeId { get; init; }
    public long LevelId { get; init; }
    public long HostId { get; init; }
    public List<RhinoCurve> Profiles { get; } = new();
    public List<Point3d> ShapePoints { get; } = new();
}

internal static class ToposolidInspector
{
    /// <summary>Returns null when the input is not a Toposolid.</summary>
    public static InspectedToposolid? Inspect(object? input, double metersPerOutputUnit)
    {
        if (RevitInputs.AsElement(input, RevitInputs.ActiveDocument()) is not Toposolid toposolid)
            return null;

        double scale = ToposolidWritePlanner.MetersPerFoot / metersPerOutputUnit;
        (string? key, string? fingerprint) = ToposolidIdentity.Read(toposolid);
        var inspected = new InspectedToposolid
        {
            Element = toposolid,
            Key = key,
            Fingerprint = fingerprint,
            IsSubdivision = ToposolidIdentity.IsSubdivision(toposolid),
            TypeId = toposolid.GetTypeId().Value,
            LevelId = toposolid.LevelId.Value,
            HostId = toposolid.HostTopoId.Value
        };

        if (toposolid.Document.GetElement(toposolid.SketchId) is Sketch sketch)
        {
            foreach (CurveArray loop in sketch.Profile)
            {
                foreach (Curve curve in loop)
                    inspected.Profiles.Add(new LineCurve(ToPoint(curve.GetEndPoint(0), scale), ToPoint(curve.GetEndPoint(1), scale)));
            }
        }

        SlabShapeEditor? editor = toposolid.GetSlabShapeEditor();
        if (editor != null)
        {
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
                inspected.ShapePoints.Add(ToPoint(vertex.Position, scale));
        }

        return inspected;
    }

    private static Point3d ToPoint(XYZ point, double scale) => new(point.X * scale, point.Y * scale, point.Z * scale);
}
