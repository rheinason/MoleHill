// Lets the curve commands work on a profile-mode section in true grades: drawing space <-> true space.
using MoleHill.Core.Analysis;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// A long section is drawn with station along X and exaggerated elevation along Y, so the slope commands
/// would read its grades wrong by the exaggeration (2% typed would be drawn 2% steep, a true 0.4% at 5x).
/// This maps a section curve into <em>true space</em> (X = station, Z = true elevation, Y the drawing's
/// plane) where the commands' own plan-distance and Z logic is exactly right, and maps their preview and
/// result back. The map is affine, so a curve keeps its parameters: a point picked on the drawn curve is the
/// same parameter on the true one, and picks stay on the curve the user sees.
/// </summary>
internal sealed class SectionSpace
{
    private readonly Transform _toTrue;
    private readonly Transform _toDrawing;

    private SectionSpace(ProfileSection link)
    {
        Link = link;
        double e = link.Frame.Exaggeration;
        // X' = X, Y' = Z (the drawing plane), Z' = datum + (Y - originY) / e.
        _toTrue = Transform.ZeroTransformation;
        _toTrue.M00 = 1.0;
        _toTrue.M12 = 1.0;
        _toTrue.M21 = 1.0 / e;
        _toTrue.M23 = link.Frame.Datum - link.Frame.OriginY / e;
        _toTrue.M33 = 1.0;
        _toTrue.TryGetInverse(out _toDrawing);
    }

    public ProfileSection Link { get; }

    /// <summary>The section space of a curve object, or null when it is not a profile-mode section.</summary>
    public static SectionSpace? For(RhinoObject? rhinoObject) =>
        rhinoObject != null && ProfileSection.Read(rhinoObject.Attributes) is { } link ? new SectionSpace(link) : null;

    public Curve ToTrue(Curve drawn) => Map(drawn, _toTrue);

    public Curve ToDrawing(Curve trueCurve) => Map(trueCurve, _toDrawing);

    public Point3d ToTrue(Point3d drawn)
    {
        drawn.Transform(_toTrue);
        return drawn;
    }

    public Point3d ToDrawing(Point3d truePoint)
    {
        truePoint.Transform(_toDrawing);
        return truePoint;
    }

    /// <summary>
    /// The section's ground line as a thin strip in true space, standing in for "the terrain" in commands
    /// that blend a curve to the ground under it: under a section, that ground is the ground line.
    /// </summary>
    public Mesh? GroundStrip(ProfileGroundLine ground, double tolerance)
    {
        if (ground.IsEmpty)
            return null;
        double half = Math.Max(tolerance * 100.0, 0.05);
        var mesh = new Mesh();
        foreach (double[] run in ground.Runs)
        {
            int count = run.Length / 2;
            int first = mesh.Vertices.Count;
            for (int i = 0; i < count; i++)
            {
                mesh.Vertices.Add(run[i * 2], Link.PlaneZ - half, run[i * 2 + 1]);
                mesh.Vertices.Add(run[i * 2], Link.PlaneZ + half, run[i * 2 + 1]);
            }

            for (int i = 0; i + 1 < count; i++)
            {
                int a = first + i * 2;
                mesh.Faces.AddFace(a, a + 2, a + 3, a + 1);
            }
        }

        mesh.Normals.ComputeNormals();
        return mesh.Faces.Count > 0 ? mesh : null;
    }

    private static Curve Map(Curve curve, Transform transform)
    {
        // An arc or circle cannot take a non-uniform scale and stay one; its NURBS form can, exactly.
        Curve mapped = curve is ArcCurve ? curve.ToNurbsCurve() : curve.DuplicateCurve();
        if (!mapped.Transform(transform))
        {
            mapped.Dispose();
            mapped = curve.ToNurbsCurve();
            mapped.Transform(transform);
        }

        return mapped;
    }
}
