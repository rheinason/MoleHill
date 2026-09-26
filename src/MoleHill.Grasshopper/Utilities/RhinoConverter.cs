using Rhino.Geometry;
using MoleHill.Core.Engine;
using MoleHill.Shared;

namespace MoleHill.Grasshopper.Utilities;

/// <summary>
/// Converts TinResult to Rhino.Geometry.Mesh and back.
/// </summary>
public static class RhinoConverter
{
    /// <summary>
    /// Convert a TinResult to a Rhino Mesh.
    /// </summary>
    public static Mesh ToRhinoMesh(TinResult result)
    {
        var mesh = new Mesh();

        // Preallocate capacity
        mesh.Vertices.Capacity = result.VertexCount;
        mesh.Faces.Capacity = result.FaceCount;

        // Add vertices
        for (int i = 0; i < result.VertexCount; i++)
        {
            mesh.Vertices.Add(
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
        }

        // Add faces
        for (int i = 0; i < result.FaceCount; i++)
        {
            mesh.Faces.AddFace(
                result.Faces[i * 3],
                result.Faces[i * 3 + 1],
                result.Faces[i * 3 + 2]);
        }

        MeshNormalOrientation.UnifyAndComputeNormals(mesh);
        mesh.Compact();

        return mesh;
    }

    /// <summary>
    /// Extract edge lines from a TinResult.
    /// </summary>
    public static Line[] ToEdgeLines(TinResult result)
    {
        var lines = new Line[result.EdgeCount];
        for (int i = 0; i < result.EdgeCount; i++)
        {
            int a = result.Edges[i * 2];
            int b = result.Edges[i * 2 + 1];
            lines[i] = new Line(
                new Point3d(result.Vertices[a * 3], result.Vertices[a * 3 + 1], result.Vertices[a * 3 + 2]),
                new Point3d(result.Vertices[b * 3], result.Vertices[b * 3 + 1], result.Vertices[b * 3 + 2]));
        }
        return lines;
    }

    /// <summary>
    /// Extract naked (boundary) edge lines from a TinResult.
    /// </summary>
    public static Line[] ToNakedEdgeLines(TinResult result)
    {
        var lines = new Line[result.NakedEdgeCount];
        for (int i = 0; i < result.NakedEdgeCount; i++)
        {
            int a = result.NakedEdges[i * 2];
            int b = result.NakedEdges[i * 2 + 1];
            lines[i] = new Line(
                new Point3d(result.Vertices[a * 3], result.Vertices[a * 3 + 1], result.Vertices[a * 3 + 2]),
                new Point3d(result.Vertices[b * 3], result.Vertices[b * 3 + 1], result.Vertices[b * 3 + 2]));
        }
        return lines;
    }

    /// <summary>
    /// Extract vertex points from a TinResult.
    /// </summary>
    public static Point3d[] ToPoints(TinResult result)
    {
        var pts = new Point3d[result.VertexCount];
        for (int i = 0; i < result.VertexCount; i++)
        {
            pts[i] = new Point3d(
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
        }
        return pts;
    }
}
