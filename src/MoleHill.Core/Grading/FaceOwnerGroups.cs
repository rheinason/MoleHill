namespace MoleHill.Core.Grading;

/// <summary>
/// Groups faces by owner in one pass so a host can extract every sub-mesh without rescanning the whole
/// result for each one.
/// </summary>
/// <remarks>
/// Extracting B sub-meshes by testing all F faces per sub-mesh is O(B × F) even when every face has a
/// single owner. Counting then filling a flat CSR layout is O(F + B), and within each group the face
/// indices stay ascending — the same order a linear scan produced — so the extracted topology is
/// unchanged.
/// </remarks>
public sealed class FaceOwnerGroups
{
    /// <summary>Owner index used for faces that belong to no area.</summary>
    public const int RemainderOwner = -1;

    // Slot 0 is the remainder; owner i lives in slot i + 1.
    private readonly int[] _offsets;
    private readonly int[] _faceIndices;

    private FaceOwnerGroups(int[] offsets, int[] faceIndices, int ownerCount)
    {
        _offsets = offsets;
        _faceIndices = faceIndices;
        OwnerCount = ownerCount;
    }

    /// <summary>Number of non-remainder owners the groups were built for.</summary>
    public int OwnerCount { get; }

    /// <summary>Faces belonging to <paramref name="ownerIndex"/>, ascending. -1 is the remainder.</summary>
    public ReadOnlySpan<int> Faces(int ownerIndex)
    {
        int slot = ownerIndex + 1;
        if (slot < 0 || slot >= _offsets.Length - 1)
            return ReadOnlySpan<int>.Empty;

        int start = _offsets[slot];
        return _faceIndices.AsSpan(start, _offsets[slot + 1] - start);
    }

    /// <summary>Face count for <paramref name="ownerIndex"/> without materializing the span.</summary>
    public int Count(int ownerIndex) => Faces(ownerIndex).Length;

    /// <summary>
    /// Groups faces by <paramref name="ownerPerFace"/>. Owners outside
    /// [0, <paramref name="ownerCount"/>) — including <see cref="RemainderOwner"/> — land in the
    /// remainder group.
    /// </summary>
    public static FaceOwnerGroups Build(int[] ownerPerFace, int faceCount, int ownerCount)
    {
        ArgumentNullException.ThrowIfNull(ownerPerFace);
        if (faceCount < 0 || faceCount > ownerPerFace.Length)
            throw new ArgumentOutOfRangeException(nameof(faceCount));
        if (ownerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(ownerCount));

        var offsets = new int[ownerCount + 2];
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            offsets[SlotOf(ownerPerFace[faceIndex], ownerCount) + 1]++;

        // Counts land at slot + 1, so an inclusive running sum turns them into start offsets: slot k
        // occupies [offsets[k], offsets[k + 1]).
        for (int slot = 1; slot < offsets.Length; slot++)
            offsets[slot] += offsets[slot - 1];

        var faceIndices = new int[faceCount];
        var cursor = new int[ownerCount + 1];
        Array.Copy(offsets, cursor, ownerCount + 1);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int slot = SlotOf(ownerPerFace[faceIndex], ownerCount);
            faceIndices[cursor[slot]++] = faceIndex;
        }

        return new FaceOwnerGroups(offsets, faceIndices, ownerCount);
    }

    /// <summary>
    /// Ascending union of the faces owned by any of <paramref name="owners"/> — the same set and order a
    /// single linear scan testing membership would produce.
    /// </summary>
    public int[] GatherAscending(IReadOnlySet<int> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);

        int total = 0;
        foreach (int owner in owners)
            total += Count(owner);

        if (total == 0)
            return Array.Empty<int>();

        var gathered = new int[total];
        int written = 0;
        foreach (int owner in owners)
        {
            ReadOnlySpan<int> faces = Faces(owner);
            faces.CopyTo(gathered.AsSpan(written));
            written += faces.Length;
        }

        Array.Sort(gathered);
        return gathered;
    }

    private static int SlotOf(int owner, int ownerCount)
    {
        return owner >= 0 && owner < ownerCount ? owner + 1 : 0;
    }
}
