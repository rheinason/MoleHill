namespace MoleHill.Rhino.Services;

/// <summary>What a drainage preview's solve depends on. Colours, ranges and palettes are deliberately absent.</summary>
internal readonly record struct DrainagePreviewKey(string Kind, double FlatSlopeRatio, double MergeShare, double MinimumDepth);

/// <summary>What the curvature preview's surface fit depends on: only the landform size. Thresholds, the
/// measure shown and the colours are applied per refresh.</summary>
internal readonly record struct CurvaturePreviewKey(double LandformSize);

/// <summary>
/// The last drainage solve behind the terrain preview, reused while the mesh and the solve settings are
/// unchanged.
/// </summary>
/// <remarks>
/// The Catchments and Ponding previews used to route the terrain (and fill every depression) on every
/// preview refresh, even for a palette or range edit. That runs on the UI thread, and a colour change
/// does not move one drop of water. The cache keys on the mesh <em>instance</em>: a display state holds
/// one mesh for its lifetime and a rebuild replaces the state, so identity is exact. The one in-place
/// mutation, a sculpt stroke, clears it through <see cref="TerrainDisplayState.InvalidatePreviewBounds"/>.
/// One entry is enough, because only one analysis previews at a time.
/// <para>The Curvature preview shares the slot under its own key type (<see cref="CurvaturePreviewKey"/>):
/// its surface fit is the same kind of cost for the same kind of edit, and a key of another type never
/// matches, so the two cannot hand each other a stale solve.</para>
/// </remarks>
internal sealed class DrainagePreviewCache
{
    private object? _source;
    private object? _key;
    private object? _value;

    /// <summary>How many solves this cache has run. For tests; a cache hit leaves it unchanged.</summary>
    public int ComputeCount { get; private set; }

    public T GetOrCompute<TKey, T>(object source, TKey key, Func<T> compute)
        where TKey : struct, IEquatable<TKey>
        where T : class
    {
        if (ReferenceEquals(_source, source) && _key is TKey stored && stored.Equals(key) && _value is T cached)
            return cached;

        T value = compute();
        ComputeCount++;
        _source = source;
        _key = key;
        _value = value;
        return value;
    }

    public void Clear()
    {
        _source = null;
        _value = null;
        _key = null;
    }
}
