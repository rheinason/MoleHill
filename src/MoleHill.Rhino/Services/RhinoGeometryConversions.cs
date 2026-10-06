using System.Runtime.CompilerServices;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class ExtractedMeshData
{
    public required double[] Vertices { get; init; }

    public required int VertexCount { get; init; }

    public required int[] Faces { get; init; }

    public required int FaceCount { get; init; }
}

internal static class RhinoGeometryConversions
{
    private static readonly ConditionalWeakTable<Mesh, ExtractedMeshData> MeshDataCache = new();
    private static readonly ConditionalWeakTable<Mesh, NormalizedMeshMarker> NormalizedMeshes = new();

    public static Mesh ToRhinoMesh(TinResult result)
    {
        var mesh = new Mesh();
        mesh.Vertices.Capacity = result.VertexCount;
        mesh.Faces.Capacity = result.FaceCount;

        for (int i = 0; i < result.VertexCount; i++)
        {
            mesh.Vertices.Add(
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
        }

        for (int i = 0; i < result.FaceCount; i++)
        {
            mesh.Faces.AddFace(
                result.Faces[i * 3],
                result.Faces[i * 3 + 1],
                result.Faces[i * 3 + 2]);
        }

        FinalizeKnownTriangleMesh(mesh);
        return mesh;
    }

    public static bool TryGetMeshData(Mesh mesh, out ExtractedMeshData data, out string? errorMessage)
    {
        errorMessage = null;
        if (MeshDataCache.TryGetValue(mesh, out data!))
            return true;

        var normalized = mesh.DuplicateMesh();
        NormalizeMeshInPlace(normalized);

        if (normalized.Faces.Count == 0)
        {
            data = null!;
            errorMessage = "Mesh has no faces.";
            return false;
        }

        if (!TryBuildMeshData(normalized, out data, out errorMessage))
        {
            data = null!;
            return false;
        }

        CacheMeshData(mesh, data);
        return true;
    }

    /// <summary>
    /// Extracts flat arrays <b>together with the counts that describe them</b>.
    ///
    /// Prefer this over the array-only overload whenever a count is needed. Extraction normalizes a
    /// COPY of the mesh - <see cref="NormalizeMeshInPlace"/> converts quads to triangles, combines
    /// identical vertices, culls unused vertices and culls degenerate faces - so the returned arrays
    /// routinely describe a different number of vertices and faces than the Rhino mesh that was passed
    /// in. Pairing these arrays with <c>mesh.Vertices.Count</c>/<c>mesh.Faces.Count</c> reads past the
    /// end of them; on a large terrain with degenerate slivers that is an IndexOutOfRangeException,
    /// not a rounding error.
    /// </summary>
    public static bool TryExtractMeshData(
        Mesh mesh,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount,
        out string? errorMessage)
    {
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        vertexCount = 0;
        faceCount = 0;
        if (!TryGetMeshData(mesh, out var data, out errorMessage))
            return false;

        vertices = data.Vertices;
        faces = data.Faces;
        vertexCount = data.VertexCount;
        faceCount = data.FaceCount;
        return true;
    }

    public static bool TryExtractMeshData(Mesh mesh, out double[] vertices, out int[] faces, out string? errorMessage)
    {
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        if (!TryGetMeshData(mesh, out var data, out errorMessage))
            return false;

        vertices = data.Vertices;
        faces = data.Faces;
        return true;
    }

    /// <summary>
    /// Extracts flat arrays from a mesh already normalized by the terrain stage-cache pipeline. Unlike
    /// <see cref="TryGetMeshData"/>, this avoids duplicating and normalizing a potentially large mesh on
    /// the Rhino UI thread. Callers must only pass cached terrain-stage meshes.
    /// </summary>
    public static ExtractedMeshData GetNormalizedMeshData(Mesh mesh)
    {
        if (MeshDataCache.TryGetValue(mesh, out ExtractedMeshData? data))
            return data;

        data = BuildMeshData(mesh);
        CacheMeshData(mesh, data);
        return data;
    }

    /// <summary>
    /// A normalized Rhino mesh from triangle arrays. The arrays are normalized in managed code
    /// (<see cref="MeshArrayNormalizer"/> reproduces <see cref="NormalizeMeshInPlace"/>), so Rhino only fills the
    /// mesh and computes normals; its combine, cull and read-back passes, about 300 ms of every 1.6 million
    /// faces a stage hands on, are skipped. When face windings need unifying, Rhino normalizes as before.
    /// </summary>
    public static Mesh BuildMesh(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        // Normalization culls an exactly collinear face, and a cap culled from the middle of a terrain is a
        // slit. Split it across its long edge first: the same surface, nothing left for the cull to open.
        faces = MeshArrayNormalizer.SplitCollinearCaps(vertices, faces, faceCount, out faceCount, out _);
        if (!MeshArrayNormalizer.TryNormalize(
                vertices, vertexCount, faces, faceCount,
                out double[] normalized, out int normalizedVertexCount, out int[] normalizedFaces, out int normalizedFaceCount) ||
            normalizedFaceCount == 0)
        {
            return BuildMeshThroughRhino(vertices, vertexCount, faces, faceCount);
        }

        var mesh = new Mesh();
        FillMesh(mesh, normalized, normalizedVertexCount, normalizedFaces, normalizedFaceCount);
        MeshNormalOrientation.ComputeNormalsConsistentlyWound(mesh);

        // What a read-back gives: the vertex list's indexer is single precision, so the arrays every stage has
        // always received are float-rounded. Kept that way so no stage sees different numbers.
        var rounded = new double[normalizedVertexCount * 3];
        for (int i = 0; i < rounded.Length; i++)
            rounded[i] = (float)normalized[i];
        var data = new ExtractedMeshData
        {
            Vertices = rounded,
            VertexCount = normalizedVertexCount,
            Faces = normalizedFaces,
            FaceCount = normalizedFaceCount
        };
        CacheMeshData(mesh, data);
        MarkNormalized(mesh);

        if (Environment.GetEnvironmentVariable("MOLEHILL_VERIFY_NORMALIZE") is { Length: > 0 } verifyLog)
            VerifyAgainstRhino(vertices, vertexCount, faces, faceCount, data, verifyLog);
        return mesh;
    }

    /// <summary>
    /// <see cref="BuildMesh"/> for a stage that moved <paramref name="source"/>'s vertices in height only, as
    /// Smooth does: the same mesh, without normalizing a topology that has not changed. On a 566k-face terrain
    /// normalizing took 460 ms of a Smooth whose smoothing took 8. <paramref name="vertices"/> must be the
    /// source's extracted arrays with only Z changed. When height alone could change the normal form
    /// (<see cref="MeshArrayNormalizer.HeightsKeepNormalForm"/>), or the source is not a normalized stage mesh,
    /// this is <see cref="BuildMesh"/> itself.
    /// </summary>
    public static Mesh BuildMeshWithNewHeights(Mesh source, double[] vertices, int vertexCount)
    {
        if (!TryGetMeshData(source, out ExtractedMeshData data, out _))
            return source;

        if (!IsNormalizedMesh(source) ||
            data.VertexCount != vertexCount ||
            !SamePlanPositions(data.Vertices, vertices, vertexCount) ||
            !MeshArrayNormalizer.HeightsKeepNormalForm(vertices, vertexCount, data.Faces, data.FaceCount))
        {
            return BuildMesh(vertices, vertexCount, data.Faces, data.FaceCount);
        }

        var mesh = new Mesh();
        FillMesh(mesh, vertices, vertexCount, data.Faces, data.FaceCount);
        MeshNormalOrientation.ComputeNormalsConsistentlyWound(mesh);
        var rounded = new double[vertexCount * 3];
        for (int i = 0; i < rounded.Length; i++)
            rounded[i] = (float)vertices[i];
        CacheMeshData(mesh, new ExtractedMeshData
        {
            Vertices = rounded,
            VertexCount = vertexCount,
            Faces = data.Faces,
            FaceCount = data.FaceCount
        });
        MarkNormalized(mesh);

        if (Environment.GetEnvironmentVariable("MOLEHILL_VERIFY_NORMALIZE") is { Length: > 0 } verifyLog)
            VerifyAgainstRhino(vertices, vertexCount, data.Faces, data.FaceCount, GetNormalizedMeshData(mesh), verifyLog);
        return mesh;

        static bool SamePlanPositions(double[] before, double[] after, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (before[i * 3] != after[i * 3] || before[i * 3 + 1] != after[i * 3 + 1])
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Writes vertices and triangles straight into an empty mesh's arrays: what
    /// <c>Vertices.Add(double, double, double)</c> and <c>Faces.AddFace</c> store, double-precision vertices
    /// included (adding a double turns them on), without a native call per element (~7 ms per 100k-face
    /// mesh, in every stage that builds one).
    /// </summary>
    private static unsafe void FillMesh(Mesh mesh, double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        mesh.Vertices.Count = vertexCount;
        // Only takes once the list has vertices; on an empty mesh it reads back false.
        mesh.Vertices.UseDoublePrecisionVertices = true;
        mesh.Faces.Count = faceCount;
        MeshUnsafeLock access = mesh.GetUnsafeLock(true);
        try
        {
            Point3f* points = access.VertexPoint3fArray(out int pointCount);
            Point3d* exact = access.VertexPoint3dArray(out int exactCount);
            MeshFace* triangles = access.FacesArray(out int triangleCount);
            if (pointCount != vertexCount || exactCount != vertexCount || triangleCount != faceCount)
                throw new InvalidOperationException(
                    $"Mesh arrays did not resize: {pointCount}/{exactCount}/{vertexCount} vertices, {triangleCount}/{faceCount} faces.");
            for (int i = 0; i < vertexCount; i++)
            {
                double x = vertices[i * 3], y = vertices[i * 3 + 1], z = vertices[i * 3 + 2];
                exact[i] = new Point3d(x, y, z);
                points[i] = new Point3f((float)x, (float)y, (float)z);
            }
            for (int i = 0; i < faceCount; i++)
                triangles[i] = new MeshFace(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);
        }
        finally
        {
            mesh.ReleaseUnsafeLock(access);
        }
    }

    /// <summary>
    /// Checks the managed normalization against Rhino's on the same input and appends the outcome to
    /// <paramref name="logPath"/>: the evidence that stages still receive exactly the arrays they did.
    /// </summary>
    private static void VerifyAgainstRhino(double[] vertices, int vertexCount, int[] faces, int faceCount, ExtractedMeshData managed, string logPath)
    {
        Mesh reference = BuildMeshThroughRhino(vertices, vertexCount, faces, faceCount);
        ExtractedMeshData expected = GetNormalizedMeshData(reference);
        string verdict;
        if (expected.VertexCount != managed.VertexCount || expected.FaceCount != managed.FaceCount)
            verdict = $"MISMATCH counts rhino {expected.VertexCount}/{expected.FaceCount} managed {managed.VertexCount}/{managed.FaceCount}";
        else if (!expected.Faces.AsSpan(0, expected.FaceCount * 3).SequenceEqual(managed.Faces.AsSpan(0, managed.FaceCount * 3)))
            verdict = "MISMATCH faces";
        else if (!expected.Vertices.AsSpan(0, expected.VertexCount * 3).SequenceEqual(managed.Vertices.AsSpan(0, managed.VertexCount * 3)))
            verdict = "MISMATCH vertices";
        else
            verdict = "equal";
        lock (MeshDataCache)
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {vertexCount} verts {faceCount} faces -> {managed.VertexCount}/{managed.FaceCount}: {verdict}{Environment.NewLine}");
    }

    private static Mesh BuildMeshThroughRhino(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        var mesh = new Mesh();
        mesh.Vertices.Capacity = vertexCount;
        mesh.Faces.Capacity = faceCount;

        for (int i = 0; i < vertexCount; i++)
        {
            mesh.Vertices.Add(
                vertices[i * 3],
                vertices[i * 3 + 1],
                vertices[i * 3 + 2]);
        }

        for (int i = 0; i < faceCount; i++)
        {
            mesh.Faces.AddFace(
                faces[i * 3],
                faces[i * 3 + 1],
                faces[i * 3 + 2]);
        }

        NormalizeMeshInPlace(mesh);
        return mesh;
    }

    /// <summary>
    /// Builds a quad-dominant mesh from flat arrays, <b>preserving quads</b> (unlike
    /// <see cref="BuildMesh"/>, which triangulates via <see cref="NormalizeMeshInPlace"/>). Used by the
    /// Retopo quad output. <paramref name="quads"/> is 4 indices/face, <paramref name="tris"/> 3/face.
    /// </summary>
    public static Mesh BuildQuadDominantMesh(double[] vertices, int[] quads, int[] tris)
    {
        var mesh = new Mesh();
        int vertexCount = vertices.Length / 3;
        mesh.Vertices.Capacity = vertexCount;
        mesh.Faces.Capacity = (quads.Length / 4) + (tris.Length / 3);

        for (int i = 0; i < vertexCount; i++)
            mesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);

        for (int i = 0; i < quads.Length / 4; i++)
            mesh.Faces.AddFace(quads[i * 4], quads[i * 4 + 1], quads[i * 4 + 2], quads[i * 4 + 3]);

        for (int i = 0; i < tris.Length / 3; i++)
            mesh.Faces.AddFace(tris[i * 3], tris[i * 3 + 1], tris[i * 3 + 2]);

        MeshNormalOrientation.UnifyAndComputeNormals(mesh);
        mesh.Compact();
        CacheMeshData(mesh, BuildMeshData(mesh));
        // Retopo deliberately produces quad faces. Mark this mesh as finalized so the generic stage
        // cache normalization does not flatten those faces back into triangles.
        MarkNormalized(mesh);
        return mesh;
    }

    /// <summary>
    /// Builds the normalized sub-mesh for one area; see <see cref="SplitResultMeshBuilder"/> for how to
    /// group faces and reuse the remap across areas.
    /// </summary>
    /// <remarks>
    /// The area's faces and their vertices in first-touch order, the arrays <c>CreateUnfinished</c> fills a
    /// Rhino mesh with, normalized by <see cref="BuildMesh"/> in managed code, which reproduces
    /// <see cref="NormalizeMeshInPlace"/> exactly and hands over to Rhino whenever winding needs unifying.
    /// Normalizing every zone through Rhino was 0.5 s of a 566k-face terrain's zones.
    /// </remarks>
    public static Mesh BuildSubMesh(
        MeshAreaSplitter.SplitResult result,
        ReadOnlySpan<int> faceIndices,
        SubMeshVertexRemap remap)
    {
        var faces = new int[faceIndices.Length * 3];
        var vertices = new List<double>(faceIndices.Length * 3);
        remap.Begin();
        for (int f = 0; f < faceIndices.Length; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int source = result.Faces[faceIndices[f] * 3 + k];
                if (!remap.TryGet(source, out int index))
                {
                    index = vertices.Count / 3;
                    vertices.Add(result.Vertices[source * 3]);
                    vertices.Add(result.Vertices[source * 3 + 1]);
                    vertices.Add(result.Vertices[source * 3 + 2]);
                    remap.Set(source, index);
                }

                faces[f * 3 + k] = index;
            }
        }

        return BuildMesh(vertices.ToArray(), vertices.Count / 3, faces, faceIndices.Length);
    }

    internal static void NormalizeMeshInPlace(Mesh mesh)
    {
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.Vertices.CombineIdentical(true, true);
        mesh.Vertices.CullUnused();
        mesh.Faces.CullDegenerateFaces();
        MeshNormalOrientation.UnifyAndComputeNormalsWelded(mesh);
        mesh.Compact();
        CacheMeshData(mesh, BuildMeshData(mesh));
        MarkNormalized(mesh);
    }

    /// <summary>
    /// Finalizes a triangle-only mesh whose vertices and faces have already been validated by Core.
    /// Avoids repeating duplicate, unused-vertex, and degenerate-face scans over very large TINs.
    /// </summary>
    private static void FinalizeKnownTriangleMesh(Mesh mesh)
    {
        MeshNormalOrientation.UnifyAndComputeNormalsWelded(mesh);
        mesh.Compact();
        CacheMeshData(mesh, BuildMeshData(mesh));
        MarkNormalized(mesh);
    }

    internal static bool IsNormalizedMesh(Mesh mesh) => NormalizedMeshes.TryGetValue(mesh, out _);

    /// <summary>
    /// <c>DuplicateMesh</c>, keeping what is known about the source: its extracted arrays and its
    /// normalized marker. A duplicate has the same vertex and face lists, so both stay exact.
    /// </summary>
    /// <remarks>
    /// Every stage-cache store and restore goes through a duplicate. A bare <c>DuplicateMesh</c> dropped
    /// both, so the next stage's <see cref="TryExtractMeshData(Mesh, out double[], out int, out int[], out int, out string?)"/>
    /// missed the cache and duplicated and fully re-normalized a mesh that was already normalized:
    /// ~60 ms on 124k faces, of which <c>UnifyNormals</c> alone is ~40 ms. The arrays are shared, not
    /// copied. Extracted arrays are read-only by convention already (the same instance's arrays are
    /// handed to every stage that reads it), and a consumer that needs to write clones them first, as
    /// Sculpt does.
    /// </remarks>
    internal static Mesh DuplicateWithCachedData(Mesh mesh)
    {
        Mesh duplicate = mesh.DuplicateMesh();
        if (MeshDataCache.TryGetValue(mesh, out ExtractedMeshData? data))
            CacheMeshData(duplicate, data);
        if (IsNormalizedMesh(mesh))
            MarkNormalized(duplicate);
        return duplicate;
    }

    private static bool TryBuildMeshData(Mesh mesh, out ExtractedMeshData data, out string? errorMessage)
    {
        errorMessage = null;
        data = BuildMeshData(mesh);
        if (data.FaceCount == 0)
        {
            errorMessage = "Mesh has no faces.";
            return false;
        }

        return true;
    }

    private static ExtractedMeshData BuildMeshData(Mesh mesh)
    {
        var vertices = new double[mesh.Vertices.Count * 3];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var pt = mesh.Vertices[i];
            vertices[i * 3] = pt.X;
            vertices[i * 3 + 1] = pt.Y;
            vertices[i * 3 + 2] = pt.Z;
        }

        // Quad-aware flattening: a quad face becomes two triangles (A,B,C)+(A,C,D). Triangle meshes are
        // unaffected; this keeps quad output (from Retopo) geometrically correct for any triangle consumer.
        int triangleCount = 0;
        for (int i = 0; i < mesh.Faces.Count; i++)
            triangleCount += mesh.Faces[i].IsQuad ? 2 : 1;

        var faces = new int[triangleCount * 3];
        int t = 0;
        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            var face = mesh.Faces[i];
            faces[t * 3] = face.A;
            faces[t * 3 + 1] = face.B;
            faces[t * 3 + 2] = face.C;
            t++;
            if (face.IsQuad)
            {
                faces[t * 3] = face.A;
                faces[t * 3 + 1] = face.C;
                faces[t * 3 + 2] = face.D;
                t++;
            }
        }

        return new ExtractedMeshData
        {
            Vertices = vertices,
            VertexCount = mesh.Vertices.Count,
            Faces = faces,
            FaceCount = triangleCount
        };
    }

    private static void CacheMeshData(Mesh mesh, ExtractedMeshData data)
    {
        MeshDataCache.Remove(mesh);
        MeshDataCache.Add(mesh, data);
    }

    private static void MarkNormalized(Mesh mesh)
    {
        NormalizedMeshes.Remove(mesh);
        NormalizedMeshes.Add(mesh, new NormalizedMeshMarker());
    }

    private sealed class NormalizedMeshMarker
    {
    }
}
