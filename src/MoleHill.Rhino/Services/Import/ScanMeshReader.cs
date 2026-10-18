using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MoleHill.Core.Engine;
using MoleHill.Core.Scan;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Captures scan triangles and diffuse UV colours without normalising away texture seams.</summary>
internal static class ScanMeshReader
{
    public static bool TryRead(RhinoDoc doc, RhinoObject? source, Mesh mesh, out IndexedTriMesh scan,
        out ScanColourSource? colours, out string? diagnostic)
    {
        scan = IndexedTriMesh.Empty;
        colours = null;
        diagnostic = null;
        Point3d[] points = mesh.Vertices.ToPoint3dArray();
        if (points.Length == 0 || mesh.Faces.Count == 0)
        {
            diagnostic = "The scan has no mesh faces.";
            return false;
        }
        var xyz = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            if (!points[i].IsValid)
            {
                diagnostic = "The scan contains a non-finite vertex.";
                return false;
            }
            xyz[i * 3] = points[i].X; xyz[i * 3 + 1] = points[i].Y; xyz[i * 3 + 2] = points[i].Z;
        }
        var faces = new List<int>(mesh.Faces.Count * 3);
        foreach (MeshFace face in mesh.Faces)
        {
            faces.AddRange(new[] { face.A, face.B, face.C });
            if (face.IsQuad)
                faces.AddRange(new[] { face.A, face.C, face.D });
        }
        if (faces.Any(index => index < 0 || index >= points.Length))
        {
            diagnostic = "A scan triangle refers to a missing vertex.";
            return false;
        }
        scan = IndexedTriMesh.FromArrays(xyz, faces.ToArray());
        try
        {
            using Material? material = source?.GetMaterial(true);
            using Texture? texture = material?.GetBitmapTexture();
            using Material? converted = texture == null ? source?.GetRenderMaterial(true)?.ToMaterial(global::Rhino.Render.RenderTexture.TextureGeneration.Allow) : null;
            using Texture? generated = converted?.GetBitmapTexture();
            Texture? diffuse = texture ?? generated;
            if (diffuse != null && mesh.TextureCoordinates.Count == points.Length && !string.IsNullOrWhiteSpace(diffuse.FileName))
            {
                string path = diffuse.FileName;
                if (!File.Exists(path))
                    path = global::Rhino.Render.Utilities.FindFile(doc, path, true);
                if (File.Exists(path))
                {
                    using var bitmap = new Bitmap(path);
                    if ((long)bitmap.Width * bitmap.Height > 128_000_000)
                        throw new InvalidOperationException("The diffuse bitmap exceeds 128 million pixels.");
                    ScanTexture image = Decode(bitmap, diffuse.WrapU == TextureUvwWrapping.Repeat, diffuse.WrapV == TextureUvwWrapping.Repeat);
                    var uv = new float[scan.FaceCount * 6];
                    Transform transform = diffuse.ApplyUvwTransform ? diffuse.UvwTransform : Transform.Identity;
                    for (int corner = 0; corner < scan.FaceCount * 3; corner++)
                    {
                        Point2f t = mesh.TextureCoordinates[scan.Faces[corner]];
                        var p = new Point3d(t.X, t.Y, 0);
                        p.Transform(transform);
                        uv[corner * 2] = (float)p.X;
                        uv[corner * 2 + 1] = (float)p.Y;
                    }
                    colours = new ScanColourSource(image, uv);
                    diagnostic = $"Diffuse colour: {bitmap.Width:N0} × {bitmap.Height:N0} bitmap sampled through triangle UVs.";
                    return true;
                }
                diagnostic = "The scan's diffuse bitmap could not be located; using available vertex colours or geometry only.";
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or ExternalException or InvalidOperationException)
        {
            diagnostic = $"The scan's diffuse bitmap could not be read ({error.Message}); using available vertex colours or geometry only.";
        }
        if (mesh.VertexColors.Count == points.Length)
        {
            var rgb = new int[scan.FaceCount];
            for (int face = 0; face < scan.FaceCount; face++)
            {
                Color a = mesh.VertexColors[scan.Faces[face * 3]], b = mesh.VertexColors[scan.Faces[face * 3 + 1]], c = mesh.VertexColors[scan.Faces[face * 3 + 2]];
                rgb[face] = ((a.R + b.R + c.R) / 3) << 16 | ((a.G + b.G + c.G) / 3) << 8 | (a.B + b.B + c.B) / 3;
            }
            colours = new ScanColourSource(rgb);
            diagnostic = "Scan colour: triangle averages of vertex colours.";
        }
        diagnostic ??= "No diffuse UV bitmap or vertex colours: ground, walls and geometry-supported kerbs only.";
        return true;
    }

    internal static ScanTexture Decode(Bitmap bitmap, bool repeatU = false, bool repeatV = false)
    {
        var rgb = new byte[checked(bitmap.Width * bitmap.Height * 3)];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int target = (y * bitmap.Width + x) * 3;
                    rgb[target] = row[x * 4 + 2]; rgb[target + 1] = row[x * 4 + 1]; rgb[target + 2] = row[x * 4];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return new ScanTexture(bitmap.Width, bitmap.Height, rgb, repeatU, repeatV);
    }
}
