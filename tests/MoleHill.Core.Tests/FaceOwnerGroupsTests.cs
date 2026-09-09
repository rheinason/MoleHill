using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class FaceOwnerGroupsTests
{
    [Fact]
    public void Build_GroupsEachOwnerInAscendingFaceOrder()
    {
        int[] owners = { 1, 0, -1, 1, 0, 2, 1 };

        FaceOwnerGroups groups = FaceOwnerGroups.Build(owners, owners.Length, ownerCount: 3);

        Assert.Equal(new[] { 1, 4 }, groups.Faces(0).ToArray());
        Assert.Equal(new[] { 0, 3, 6 }, groups.Faces(1).ToArray());
        Assert.Equal(new[] { 5 }, groups.Faces(2).ToArray());
        Assert.Equal(new[] { 2 }, groups.Faces(FaceOwnerGroups.RemainderOwner).ToArray());
    }

    [Fact]
    public void Build_MatchesALinearScanForEveryOwner()
    {
        var random = new Random(20260910);
        var owners = new int[5000];
        const int ownerCount = 37;
        for (int i = 0; i < owners.Length; i++)
            owners[i] = random.Next(-1, ownerCount);

        FaceOwnerGroups groups = FaceOwnerGroups.Build(owners, owners.Length, ownerCount);

        for (int owner = -1; owner < ownerCount; owner++)
        {
            var expected = new List<int>();
            for (int face = 0; face < owners.Length; face++)
            {
                if (owners[face] == owner)
                    expected.Add(face);
            }

            Assert.Equal(expected, groups.Faces(owner).ToArray());
            Assert.Equal(expected.Count, groups.Count(owner));
        }
    }

    [Fact]
    public void Build_OwnersOutsideTheDeclaredRangeFallIntoTheRemainder()
    {
        int[] owners = { 0, 5, -3, 1 };

        FaceOwnerGroups groups = FaceOwnerGroups.Build(owners, owners.Length, ownerCount: 2);

        Assert.Equal(new[] { 0 }, groups.Faces(0).ToArray());
        Assert.Equal(new[] { 3 }, groups.Faces(1).ToArray());
        Assert.Equal(new[] { 1, 2 }, groups.Faces(FaceOwnerGroups.RemainderOwner).ToArray());
    }

    [Fact]
    public void Faces_OutOfRangeOwner_IsEmpty()
    {
        FaceOwnerGroups groups = FaceOwnerGroups.Build(new[] { 0, 1 }, 2, ownerCount: 2);

        Assert.True(groups.Faces(2).IsEmpty);
        Assert.True(groups.Faces(-2).IsEmpty);
        Assert.Equal(2, groups.OwnerCount);
    }

    [Fact]
    public void Build_HonoursAFaceCountShorterThanTheArray()
    {
        int[] owners = { 0, 0, 1, 1 };

        FaceOwnerGroups groups = FaceOwnerGroups.Build(owners, faceCount: 2, ownerCount: 2);

        Assert.Equal(new[] { 0, 1 }, groups.Faces(0).ToArray());
        Assert.True(groups.Faces(1).IsEmpty);
    }

    [Fact]
    public void GatherAscending_ReturnsTheSameFacesAsAMembershipScan()
    {
        int[] owners = { 2, 0, -1, 1, 2, 0, 3 };
        FaceOwnerGroups groups = FaceOwnerGroups.Build(owners, owners.Length, ownerCount: 4);
        var selected = new HashSet<int> { 0, 2 };

        var expected = new List<int>();
        for (int face = 0; face < owners.Length; face++)
        {
            if (selected.Contains(owners[face]))
                expected.Add(face);
        }

        Assert.Equal(expected, groups.GatherAscending(selected));
    }

    [Fact]
    public void GatherAscending_NoMatches_ReturnsEmpty()
    {
        FaceOwnerGroups groups = FaceOwnerGroups.Build(new[] { 0, 0 }, 2, ownerCount: 1);

        Assert.Empty(groups.GatherAscending(new HashSet<int> { 7 }));
    }

    [Fact]
    public void Build_EmptyInput_HasNoFaces()
    {
        FaceOwnerGroups groups = FaceOwnerGroups.Build(Array.Empty<int>(), 0, ownerCount: 0);

        Assert.True(groups.Faces(0).IsEmpty);
        Assert.True(groups.Faces(FaceOwnerGroups.RemainderOwner).IsEmpty);
    }
}
