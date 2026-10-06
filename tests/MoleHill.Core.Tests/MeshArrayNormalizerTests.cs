using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The rules <see cref="MeshArrayNormalizer"/> reproduces were measured against RhinoCommon; these pin them.
/// The hosted <c>NormalizeEquivalenceProbe</c> checks them against Rhino itself.
/// </summary>
public class MeshArrayNormalizerTests
{
    [Fact]
    public void TryNormalize_NothingToMerge_KeepsVertexOrder()
    {
        double[] v = { 5, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        int[] f = { 1, 2, 3, 0, 3, 2 };

        Assert.True(MeshArrayNormalizer.TryNormalize(v, 4, f, 2, out double[] nv, out int nvc, out int[] nf, out _));

        Assert.Equal(4, nvc);
        Assert.Equal(v, nv);
        Assert.Equal(f, nf);
    }

    [Fact]
    public void TryNormalize_FloatEqualVertices_MergeAndSortDescendingKeepingTheLastAdded()
    {
        // Vertices 1 and 3 round to the same float at x = 4000 (1e-5 apart); 3 is added last, so it survives.
        double[] v = { 0, 0, 0, 4000.00001, 1, 1, 0, 5, 0, 4000.0, 1, 1 };
        int[] f = { 1, 0, 2, 0, 3, 2 };

        Assert.True(MeshArrayNormalizer.TryNormalize(v, 4, f, 2, out double[] nv, out int nvc, out int[] nf, out _));

        Assert.Equal(3, nvc);
        Assert.Equal(new double[] { 4000.0, 1, 1, 0, 5, 0, 0, 0, 0 }, nv);   // descending x, then y
        Assert.Equal(new[] { 0, 2, 1, 2, 0, 1 }, nf);
    }

    [Fact]
    public void TryNormalize_ExactlyCollinearFace_IsCulled_ButASliverStays()
    {
        double[] v = { 0, 0, 0, 1, 0, 0, 0.5, 0, 0, 0.5, 1e-12, 0, 0, 1, 0 };
        int[] f = { 0, 1, 2, 0, 1, 3, 4, 1, 0 };

        Assert.True(MeshArrayNormalizer.TryNormalize(v, 5, f, 3, out _, out int nvc, out int[] nf, out int nfc));

        Assert.Equal(2, nfc);                  // the collinear face goes, the 1e-12 sliver stays
        Assert.Equal(4, nvc);                  // its lone vertex is compacted away
        Assert.Equal(new[] { 0, 1, 2, 3, 1, 0 }, nf);
    }

    [Fact]
    public void TryNormalize_InconsistentWinding_ReturnsFalse()
    {
        double[] v = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
        int[] f = { 0, 1, 2, 0, 1, 3 };   // both traverse 0 -> 1

        Assert.False(MeshArrayNormalizer.TryNormalize(v, 4, f, 2, out _, out _, out _, out _));
    }

    [Fact]
    public void TryNormalize_CulledFaces_PreservesInputsAndEarlierOutputs()
    {
        double[] vertices = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 5, 5, 0 };
        int[] faces = { 0, 0, 1, 0, 1, 2 };
        double[] originalVertices = (double[])vertices.Clone();
        int[] originalFaces = (int[])faces.Clone();
        Assert.True(MeshArrayNormalizer.TryNormalize(vertices, 4, faces, 2,
            out double[] resultVertices, out int vertexCount, out int[] resultFaces, out int faceCount));

        // Vary subsequent calls, including winding rejection and empty input. Compaction must not
        // mutate source faces or the arrays already published to callers.
        for (int i = 0; i < 100; i++)
        {
            Assert.False(MeshArrayNormalizer.HasConsistentWinding(new[] { 0, 1, 2, 0, 1, 3 }, 2));
            Assert.True(MeshArrayNormalizer.TryNormalize(Array.Empty<double>(), 0, Array.Empty<int>(), 0,
                out _, out _, out _, out _));
            Assert.True(MeshArrayNormalizer.TryNormalize(vertices, 4, faces, 2, out _, out _, out _, out _));
        }

        Assert.Equal(originalVertices, vertices);
        Assert.Equal(originalFaces, faces);
        Assert.Equal(3, vertexCount);
        Assert.Equal(1, faceCount);
        Assert.Equal(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, resultVertices);
        Assert.Equal(new[] { 0, 1, 2 }, resultFaces);
    }

    [Fact]
    public void HasFloatDuplicates_ConcurrentVariedInputs_MatchesFloatSet()
    {
        Parallel.For(0, 128, run =>
        {
            var random = new Random(run + 4729);
            int count = run % 3 == 0 ? 0 : random.Next(2, 2000);
            var vertices = new double[count * 3];
            var expected = new HashSet<(float, float, float)>();
            bool duplicate = false;
            for (int i = 0; i < count; i++)
            {
                int offset = i * 3;
                vertices[offset] = random.Next(-100, 100) + random.NextDouble();
                vertices[offset + 1] = random.Next(-100, 100);
                vertices[offset + 2] = run % 2 == 0 ? 0 : random.NextDouble();
                if (i == count - 1 && run % 4 == 0)
                    Array.Copy(vertices, 0, vertices, offset, 3);
                duplicate |= !expected.Add(((float)vertices[offset], (float)vertices[offset + 1], (float)vertices[offset + 2]));
            }
            Assert.Equal(duplicate, MeshArrayNormalizer.HasFloatDuplicates(vertices, count));
        });
        Assert.True(MeshArrayNormalizer.HasFloatDuplicates(new double[] { -0.0, 1, 2, 0.0, 1, 2 }, 2));
        Assert.True(MeshArrayNormalizer.HasFloatDuplicates(new double[] { 4000, 1, 2, 4000.00001, 1, 2 }, 2));
    }

    [Fact]
    public void TryNormalize_PaddedFaceArray_UsesOnlyDeclaredFacesAndPreservesSource()
    {
        double[] vertices = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        int[] faces = { 0, 1, 2, -1, -1, -1 };
        Assert.True(MeshArrayNormalizer.TryNormalize(vertices, 3, faces, 1,
            out _, out int vertexCount, out int[] normalizedFaces, out int faceCount));
        Assert.Equal(3, vertexCount);
        Assert.Equal(1, faceCount);
        Assert.Equal(new[] { 0, 1, 2 }, normalizedFaces);
        Assert.Equal(new[] { 0, 1, 2, -1, -1, -1 }, faces);
    }
}
