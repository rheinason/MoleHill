// Grasshopper wrapper for a validated, Revit-neutral Toposolid preparation package.
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Types;

public sealed class ToposolidPreparationGoo : GH_Goo<ToposolidPreparationData>
{
    public ToposolidPreparationGoo()
    {
    }

    public ToposolidPreparationGoo(ToposolidPreparationData value)
        : base(value)
    {
    }

    public override bool IsValid => Value?.IsValid == true;

    public override string IsValidWhyNot => Value == null
        ? "No Toposolid preparation data."
        : Value.IsValid
            ? string.Empty
            : "Toposolid preparation requires valid profiles and at least three elevation points.";

    public override string TypeName => "MoleHill Toposolid Preparation";

    public override string TypeDescription => "Validated profiles, points, subdivisions, coordinates and diagnostics for downstream Revit Toposolid creation.";

    public override IGH_Goo Duplicate() => Value == null
        ? new ToposolidPreparationGoo()
        : new ToposolidPreparationGoo(Value.Duplicate());

    public override object? ScriptVariable() => Value?.Duplicate();

    public override bool CastFrom(object source)
    {
        if (source is not ToposolidPreparationData preparation)
            return false;

        Value = preparation.Duplicate();
        return true;
    }

    public override bool CastTo<Q>(ref Q target)
    {
        if (Value == null || !typeof(Q).IsAssignableFrom(typeof(ToposolidPreparationData)))
            return false;

        object result = Value.Duplicate();
        target = (Q)result;
        return true;
    }

    public override string ToString() => Value == null
        ? "Null MoleHill Toposolid Preparation"
        : $"Toposolid Preparation ({Value.Name}, {Value.ElevationPoints.Count:N0} points, {Value.Subdivisions.Count:N0} subdivisions)";

    public override bool Write(GH_IWriter writer)
    {
        if (Value == null)
            return false;

        writer.SetInt32("Schema", 1);
        writer.SetString("Name", Value.Name);
        writer.SetString("Key", Value.Key);
        writer.SetInt64("Revision", Value.Revision);
        writer.SetString("Fingerprint", Value.GeometryFingerprint);
        writer.SetString("UnitSystem", Value.UnitSystem);
        writer.SetDouble("MetersPerModelUnit", Value.MetersPerModelUnit);
        writer.SetInt32("SourcePointCount", Value.SourcePointCount);
        writer.SetDouble("MaximumError", Value.MaximumMeasuredVerticalError);
        writer.SetInt32("ProfileCount", Value.Profiles.Count);
        for (int index = 0; index < Value.Profiles.Count; index++)
            writer.SetString("Profile", index, GooSerialization.Encode(Value.Profiles[index]));
        writer.SetDoubleArray("Points", FlattenPoints(Value.ElevationPoints));
        writer.SetInt32("BreaklineCount", Value.Breaklines.Count);
        for (int index = 0; index < Value.Breaklines.Count; index++)
            writer.SetString("Breakline", index, GooSerialization.Encode(Value.Breaklines[index]));
        writer.SetInt32("SubdivisionCount", Value.Subdivisions.Count);
        for (int subdivisionIndex = 0; subdivisionIndex < Value.Subdivisions.Count; subdivisionIndex++)
        {
            ToposolidSubdivisionData subdivision = Value.Subdivisions[subdivisionIndex];
            writer.SetString("SubdivisionName", subdivisionIndex, subdivision.Name);
            writer.SetString("SubdivisionKey", subdivisionIndex, subdivision.Key);
            writer.SetString("SubdivisionFingerprint", subdivisionIndex, subdivision.GeometryFingerprint);
            writer.SetInt32("SubdivisionProfileCount", subdivisionIndex, subdivision.Profiles.Count);
            for (int profileIndex = 0; profileIndex < subdivision.Profiles.Count; profileIndex++)
            {
                writer.SetString(
                    $"SubdivisionProfile{subdivisionIndex}",
                    profileIndex,
                    GooSerialization.Encode(subdivision.Profiles[profileIndex]));
            }
        }

        writer.SetInt32("DiagnosticCount", Value.Diagnostics.Count);
        for (int index = 0; index < Value.Diagnostics.Count; index++)
            writer.SetString("Diagnostic", index, Value.Diagnostics[index]);
        return true;
    }

    public override bool Read(GH_IReader reader)
    {
        if (!reader.ItemExists("Schema") || reader.GetInt32("Schema") != 1)
            return false;

        List<Curve> profiles = ReadCurves(reader, "Profile", reader.GetInt32("ProfileCount"));
        List<Curve> breaklines = ReadCurves(reader, "Breakline", reader.GetInt32("BreaklineCount"));
        var subdivisions = new List<ToposolidSubdivisionData>();
        int subdivisionCount = reader.GetInt32("SubdivisionCount");
        for (int subdivisionIndex = 0; subdivisionIndex < subdivisionCount; subdivisionIndex++)
        {
            List<Curve> subdivisionProfiles = ReadCurves(
                reader,
                $"SubdivisionProfile{subdivisionIndex}",
                reader.GetInt32("SubdivisionProfileCount", subdivisionIndex));
            subdivisions.Add(new ToposolidSubdivisionData(
                reader.GetString("SubdivisionName", subdivisionIndex),
                reader.GetString("SubdivisionKey", subdivisionIndex),
                subdivisionProfiles,
                reader.GetString("SubdivisionFingerprint", subdivisionIndex)));
            DisposeCurves(subdivisionProfiles);
        }

        var diagnostics = new List<string>();
        int diagnosticCount = reader.GetInt32("DiagnosticCount");
        for (int index = 0; index < diagnosticCount; index++)
            diagnostics.Add(reader.GetString("Diagnostic", index));

        Value = new ToposolidPreparationData(
            profiles,
            ExpandPoints(reader.GetDoubleArray("Points")),
            subdivisions,
            breaklines,
            reader.GetString("Name"),
            reader.GetString("Key"),
            reader.GetInt64("Revision"),
            reader.GetString("Fingerprint"),
            reader.GetString("UnitSystem"),
            reader.GetDouble("MetersPerModelUnit"),
            reader.GetInt32("SourcePointCount"),
            reader.GetDouble("MaximumError"),
            diagnostics);
        DisposeCurves(profiles);
        DisposeCurves(breaklines);
        return Value.IsValid;
    }

    private static double[] FlattenPoints(IReadOnlyList<Point3d> points)
    {
        var result = new double[points.Count * 3];
        for (int index = 0; index < points.Count; index++)
        {
            result[index * 3] = points[index].X;
            result[index * 3 + 1] = points[index].Y;
            result[index * 3 + 2] = points[index].Z;
        }

        return result;
    }

    private static IReadOnlyList<Point3d> ExpandPoints(double[] values)
    {
        var result = new Point3d[values.Length / 3];
        for (int index = 0; index < result.Length; index++)
            result[index] = new Point3d(values[index * 3], values[index * 3 + 1], values[index * 3 + 2]);
        return result;
    }

    private static List<Curve> ReadCurves(GH_IReader reader, string key, int count)
    {
        var curves = new List<Curve>(count);
        for (int index = 0; index < count; index++)
        {
            Curve? curve = GooSerialization.DecodeCurve(reader.GetString(key, index));
            if (curve != null)
                curves.Add(curve);
        }

        return curves;
    }

    private static void DisposeCurves(IEnumerable<Curve> curves)
    {
        foreach (Curve curve in curves)
            curve.Dispose();
    }
}
