using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MoleHill.Core.Engine;
using MoleHill.Core.Scan;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

// System.Drawing is Windows-only, and so is Rhino: the plug-in suppresses CA1416 project-wide, but the
// plain net8.0 test project compiles this file as linked source too.
#pragma warning disable CA1416

namespace MoleHill.Rhino.Services;

/// <summary>Captures scan triangles and diffuse UV colours without normalising away texture seams.</summary>
internal static class ScanMeshReader
{
    /// <summary>Where a scan's diffuse bitmap lives and how its UVs map onto it.</summary>
    internal sealed record DiffuseTexture(string Path, bool RepeatU, bool RepeatV, Transform UvTransform);

    /// <summary>
    /// Finds the scan's diffuse bitmap through its material. Touches the document, so it runs on the UI thread;
    /// everything slow (array copies, decoding the bitmap) is left to <see cref="TryRead"/>.
    /// </summary>
    public static DiffuseTexture? FindDiffuse(RhinoDoc doc, RhinoObject? source, Mesh mesh, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            using Material? material = source?.GetMaterial(true);
            using Texture? texture = material?.GetBitmapTexture();
            using Material? converted = texture == null ? source?.GetRenderMaterial(true)?.ToMaterial(global::Rhino.Render.RenderTexture.TextureGeneration.Allow) : null;
            using Texture? generated = converted?.GetBitmapTexture();
            Texture? diffuse = texture ?? generated;
            if (diffuse == null || mesh.TextureCoordinates.Count != mesh.Vertices.Count || string.IsNullOrWhiteSpace(diffuse.FileName))
                return null;
            string path = diffuse.FileName;
            if (!File.Exists(path))
                path = global::Rhino.Render.Utilities.FindFile(doc, path, true);
            if (File.Exists(path))
                return new DiffuseTexture(path, diffuse.WrapU == TextureUvwWrapping.Repeat, diffuse.WrapV == TextureUvwWrapping.Repeat,
                    diffuse.ApplyUvwTransform ? diffuse.UvwTransform : Transform.Identity);
            diagnostic = "The scan's diffuse bitmap could not be located; using available vertex colours or geometry only.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or ExternalException or InvalidOperationException)
        {
            diagnostic = $"The scan's diffuse bitmap could not be read ({error.Message}); using available vertex colours or geometry only.";
        }
        return null;
    }

    /// <summary>
    /// Copies the triangles and samples colour from <paramref name="texture"/> or vertex colours. Reads only the
    /// mesh it is given (a private copy) and the bitmap file, so it runs off the UI thread.
    /// </summary>
    public static bool TryRead(Mesh mesh, DiffuseTexture? texture, string? textureDiagnostic, out IndexedTriMesh scan,
        out ScanColourSource? colours, out string? diagnostic)
    {
        scan = IndexedTriMesh.Empty;
        colours = null;
        diagnostic = textureDiagnostic;
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
            if (face.A < 0 || face.B < 0 || face.C < 0 || face.D < 0 ||
                face.A >= points.Length || face.B >= points.Length || face.C >= points.Length || face.D >= points.Length)
            {
                diagnostic = "A scan triangle refers to a missing vertex.";
                return false;
            }
            faces.Add(face.A); faces.Add(face.B); faces.Add(face.C);
            if (face.IsQuad)
            {
                faces.Add(face.A); faces.Add(face.C); faces.Add(face.D);
            }
        }
        scan = IndexedTriMesh.FromArrays(xyz, faces.ToArray());
        if (texture != null)
        {
            try
            {
                using var bitmap = new Bitmap(texture.Path);
                if ((long)bitmap.Width * bitmap.Height > 128_000_000)
                    throw new InvalidOperationException("The diffuse bitmap exceeds 128 million pixels.");
                ScanTexture image = Decode(bitmap, texture.RepeatU, texture.RepeatV);
                var uv = new float[scan.FaceCount * 6];
                for (int corner = 0; corner < scan.FaceCount * 3; corner++)
                {
                    Point2f t = mesh.TextureCoordinates[scan.Faces[corner]];
                    var p = new Point3d(t.X, t.Y, 0);
                    p.Transform(texture.UvTransform);
                    uv[corner * 2] = (float)p.X;
                    uv[corner * 2 + 1] = (float)p.Y;
                }
                colours = new ScanColourSource(image, uv);
                diagnostic = FormattableString.Invariant($"Diffuse colour: {bitmap.Width} × {bitmap.Height} bitmap sampled through triangle UVs.");
                return true;
            }
            catch (Exception error) when (error is ArgumentException or IOException or ExternalException or InvalidOperationException)
            {
                diagnostic = $"The scan's diffuse bitmap could not be read ({error.Message}); using available vertex colours or geometry only.";
            }
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
