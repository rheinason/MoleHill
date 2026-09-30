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
}
