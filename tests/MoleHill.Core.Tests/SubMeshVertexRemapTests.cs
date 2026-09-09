using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class SubMeshVertexRemapTests
{
    [Fact]
    public void TryGet_BeforeAnyClaim_ReturnsFalse()
    {
        var remap = new SubMeshVertexRemap(4);
        remap.Begin();

        Assert.False(remap.TryGet(2, out int mapped));
        Assert.Equal(-1, mapped);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsTheClaimedIndex()
    {
        var remap = new SubMeshVertexRemap(4);
        remap.Begin();
        remap.Set(2, 0);
        remap.Set(3, 1);

        Assert.True(remap.TryGet(2, out int first));
        Assert.True(remap.TryGet(3, out int second));
        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Begin_DiscardsThePreviousSubMeshClaims()
    {
        var remap = new SubMeshVertexRemap(4);
        remap.Begin();
        remap.Set(2, 0);

        remap.Begin();

        Assert.False(remap.TryGet(2, out _));
        remap.Set(2, 5);
        Assert.True(remap.TryGet(2, out int mapped));
        Assert.Equal(5, mapped);
    }

    [Fact]
    public void ExtractingManySubMeshes_MatchesAPerSubMeshDictionary()
    {
        var random = new Random(4242);
        int[] faces = new int[900];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = random.Next(0, 60);

        var remap = new SubMeshVertexRemap(60);
        for (int round = 0; round < 5; round++)
        {
            int start = round * 180;
            var expected = new Dictionary<int, int>();
            var actual = new List<int>();

            remap.Begin();
            int next = 0;
            for (int i = start; i < start + 180; i++)
            {
                int vertex = faces[i];
                if (!expected.TryGetValue(vertex, out int expectedIndex))
                {
                    expectedIndex = expected.Count;
                    expected[vertex] = expectedIndex;
                }

                if (!remap.TryGet(vertex, out int actualIndex))
                {
                    actualIndex = next++;
                    remap.Set(vertex, actualIndex);
                }

                actual.Add(actualIndex);
                Assert.Equal(expectedIndex, actualIndex);
            }

            Assert.Equal(expected.Count, next);
        }
    }

    [Fact]
    public void Constructor_NegativeVertexCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubMeshVertexRemap(-1));
    }
}
