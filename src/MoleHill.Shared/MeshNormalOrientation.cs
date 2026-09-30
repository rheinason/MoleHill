using Rhino.Geometry;

namespace MoleHill.Shared;

/// <summary>
/// The one place either host orients an output mesh: consistent face winding, then vertex normals.
/// </summary>
/// <remarks>
/// Only orientation lives here. The Rhino host's <c>RhinoGeometryConversions.NormalizeMeshInPlace</c>
/// also welds, culls and caches, which would renumber the vertices of a mesh that carries per-vertex
/// colours, so a coloured analysis mesh calls this rather than that.
/// </remarks>
internal static class MeshNormalOrientation
{
    /// <summary>
    /// Unifies face winding, then computes vertex normals, so the normals always describe the final
    /// winding. Safe for any mesh, welded or not.
    /// </summary>
    public static void UnifyAndComputeNormals(Mesh mesh)
    {
        mesh.UnifyNormals();
        mesh.Normals.ComputeNormals();
    }

    /// <summary>
    /// <see cref="UnifyAndComputeNormals"/> for a mesh whose coincident vertices are already shared -
    /// just welded, or a TIN Core built - skipping <c>UnifyNormals</c> when no directed edge repeats.
    /// </summary>
    /// <remarks>
    /// Only valid on a welded mesh. <c>UnifyNormals</c> works on mesh topology, which treats vertices at
    /// the same position as one, but <see cref="HasConsistentWinding"/> reads raw vertex indices. On an
    /// unwelded mesh no directed edge can repeat, so the check always passes and a face wound the wrong
    /// way across a seam of duplicate vertices would never be flipped.
    /// </remarks>
    public static void UnifyAndComputeNormalsWelded(Mesh mesh)
    {
        if (!HasConsistentWinding(mesh))
            mesh.UnifyNormals();
        mesh.Normals.ComputeNormals();
    }

    /// <summary>
    /// Computes vertex normals for a mesh whose winding the caller has already proven consistent on its welded
    /// arrays (<c>MeshArrayNormalizer.TryNormalize</c> returned true), so <c>UnifyNormals</c> has nothing to do.
    /// </summary>
    public static void ComputeNormalsConsistentlyWound(Mesh mesh) => mesh.Normals.ComputeNormals();

    /// <summary>
    /// True when no directed edge occurs twice, which on a welded mesh is exactly the condition under
    /// which <c>UnifyNormals</c> has nothing to flip: two faces that share an edge are consistently wound
    /// when they traverse it in opposite directions, and an inconsistent pair (or a non-manifold edge,
    /// which always has two uses in one direction) repeats a directed edge. A mesh holding any quad
    /// reports false, so <c>UnifyNormals</c> still decides for it.
    /// </summary>
    /// <remarks>
    /// <c>UnifyNormals</c> was ~40 ms of every normalization on a 111k-face terrain, and every mesh-producing
    /// stage normalizes its output, which the grading stages have already oriented upward. This check is a
    /// sort of three keys per face, a fraction of that.
    /// </remarks>
    public static bool HasConsistentWinding(Mesh mesh)
    {
        int faceCount = mesh.Faces.Count;
        if (faceCount == 0)
            return true;

        // Rented, not allocated: at 111k faces the key buffer is ~2.7 MB, a large-object-heap allocation
        // per normalization, and the full collections those provoked landed inside whatever stage ran
        // next (measured as +40 ms on the cold build's Ponding analysis, which had not changed).
        int keyCount = faceCount * 3;
        long[] rented = System.Buffers.ArrayPool<long>.Shared.Rent(keyCount);
        try
        {
            Span<long> directed = rented.AsSpan(0, keyCount);
            for (int face = 0; face < faceCount; face++)
            {
                MeshFace meshFace = mesh.Faces[face];
                if (meshFace.IsQuad)
                    return false;

                directed[face * 3] = ((long)meshFace.A << 32) | (uint)meshFace.B;
                directed[(face * 3) + 1] = ((long)meshFace.B << 32) | (uint)meshFace.C;
                directed[(face * 3) + 2] = ((long)meshFace.C << 32) | (uint)meshFace.A;
            }

            directed.Sort();
            for (int i = 1; i < directed.Length; i++)
            {
                if (directed[i] == directed[i - 1])
                    return false;
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<long>.Shared.Return(rented);
        }
    }
}
