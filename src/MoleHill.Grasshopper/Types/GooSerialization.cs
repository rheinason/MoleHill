// Versioned Rhino geometry helpers shared by persistent MoleHill Grasshopper goo types.
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Runtime;

namespace MoleHill.Grasshopper.Types;

internal static class GooSerialization
{
    private static readonly SerializationOptions Options = new()
    {
        WriteUserData = false,
        WriteRenderMeshes = false,
        WriteAnalysisMeshes = false
    };

    public static string Encode(GeometryBase geometry) => geometry.ToJSON(Options);

    public static Mesh? DecodeMesh(string json) => CommonObject.FromJSON(json) as Mesh;

    public static Curve? DecodeCurve(string json) => CommonObject.FromJSON(json) as Curve;

    public static double[] EncodeTransform(Transform transform) =>
    [
        transform.M00, transform.M01, transform.M02, transform.M03,
        transform.M10, transform.M11, transform.M12, transform.M13,
        transform.M20, transform.M21, transform.M22, transform.M23,
        transform.M30, transform.M31, transform.M32, transform.M33
    ];

    public static Transform DecodeTransform(double[] values)
    {
        if (values.Length != 16)
            return Transform.Identity;

        var transform = Transform.Identity;
        transform.M00 = values[0];
        transform.M01 = values[1];
        transform.M02 = values[2];
        transform.M03 = values[3];
        transform.M10 = values[4];
        transform.M11 = values[5];
        transform.M12 = values[6];
        transform.M13 = values[7];
        transform.M20 = values[8];
        transform.M21 = values[9];
        transform.M22 = values[10];
        transform.M23 = values[11];
        transform.M30 = values[12];
        transform.M31 = values[13];
        transform.M32 = values[14];
        transform.M33 = values[15];
        return transform.IsValid ? transform : Transform.Identity;
    }
}
