using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// R02 evidence. The reason the comparer exists, stated as an assertion rather than a timing run:
/// for a packed edge key the default hash is <c>lo ^ hi</c>, so the edges of a mesh numbered the way
/// a triangulator numbers one collapse into a handful of buckets. Bucket spread is deterministic;
/// elapsed time on one machine is not, so that is what is measured here.
/// </summary>
public class EdgeKeyComparerTests
{
    private static long[] ConsecutiveEdgeKeys(int count)
    {
        var keys = new long[count];
        for (int i = 0; i < count; i++)
            keys[i] = IndexedMeshTools.GetEdgeKey(2 * i, 2 * i + 1);

        return keys;
    }

    private static int DistinctBuckets(IEnumerable<long> keys, Func<long, int> hash, int bucketCount)
    {
        var buckets = new HashSet<int>();
        foreach (long key in keys)
            buckets.Add((hash(key) & int.MaxValue) % bucketCount);

        return buckets.Count;
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(16384)]
    public void EdgeKeyComparer_SpreadsConsecutiveEdgesAcrossBuckets(int edgeCount)
    {
        long[] keys = ConsecutiveEdgeKeys(edgeCount);

        int defaultBuckets = DistinctBuckets(keys, key => key.GetHashCode(), edgeCount);
        int comparerBuckets = DistinctBuckets(keys, IndexedMeshTools.EdgeKeyComparer.Instance.GetHashCode, edgeCount);

        // (2k, 2k+1) packs to (2k << 32) | (2k+1); lo ^ hi therefore lands almost everything together.
        Assert.True(defaultBuckets < edgeCount / 8, $"Default hash spread unexpectedly wide: {defaultBuckets}.");

        // A good hash fills ~63% of buckets for n keys in n buckets; allow generous slack.
        Assert.True(
            comparerBuckets > edgeCount / 2,
            $"EdgeKeyComparer spread only {comparerBuckets} of {edgeCount} buckets.");
    }

    [Fact]
    public void EdgeKeyComparer_SpreadsShuffledVertexNumberingToo()
    {
        // Shuffled numbering is the easy case for the default hash; the comparer must not be worse.
        var random = new Random(20260919);
        var keys = new long[16384];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = IndexedMeshTools.GetEdgeKey(random.Next(0, 1 << 20), random.Next(0, 1 << 20));

        int comparerBuckets = DistinctBuckets(keys, IndexedMeshTools.EdgeKeyComparer.Instance.GetHashCode, keys.Length);

        Assert.True(comparerBuckets > keys.Length / 2, $"Spread only {comparerBuckets} of {keys.Length} buckets.");
    }

    [Fact]
    public void PackedKeyComparer_SpreadsConsecutiveUnsignedEdgeKeys()
    {
        const int edgeCount = 16384;
        var keys = new ulong[edgeCount];
        for (int i = 0; i < edgeCount; i++)
            keys[i] = ((ulong)(uint)(2 * i) << 32) | (uint)(2 * i + 1);

        int defaultBuckets = DistinctBuckets(keys.Select(k => unchecked((long)k)), key => unchecked((ulong)key).GetHashCode(), edgeCount);
        int comparerBuckets = DistinctBuckets(
            keys.Select(k => unchecked((long)k)),
            key => IndexedMeshTools.PackedKeyComparer.Instance.GetHashCode(unchecked((ulong)key)),
            edgeCount);

        Assert.True(defaultBuckets < edgeCount / 8, $"Default hash spread unexpectedly wide: {defaultBuckets}.");
        Assert.True(comparerBuckets > edgeCount / 2, $"PackedKeyComparer spread only {comparerBuckets} buckets.");
    }

    [Fact]
    public void CreateEdgeKeySet_AndMap_CarryTheComparer()
    {
        Assert.Same(IndexedMeshTools.EdgeKeyComparer.Instance, IndexedMeshTools.CreateEdgeKeySet(16).Comparer);
        Assert.Same(IndexedMeshTools.EdgeKeyComparer.Instance, IndexedMeshTools.CreateEdgeKeyMap<int>(16).Comparer);
    }

    [Fact]
    public void CreateEdgeKeySet_ToleratesNegativeCapacity()
    {
        // Callers size these from face counts that can legitimately compute to zero or below.
        Assert.Empty(IndexedMeshTools.CreateEdgeKeySet(-5));
        Assert.Empty(IndexedMeshTools.CreateEdgeKeyMap<int>(-5));
    }
}
