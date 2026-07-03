using System.Buffers.Binary;
using System.IO.Compression;

namespace MoleHill.Core.Sculpting;

/// <summary>
/// Sparse tiled 2.5D displacement field: a Z-offset function f(x, y) over world XY, stored as
/// 64x64-sample tiles on a grid anchored at the world origin (tile indices are stable regardless of
/// where sculpting happens). Missing tiles read as zero, so the field feathers to zero at its edges
/// and bilinear sampling is continuous across tile borders. Because the field is keyed to world XY
/// (never vertex indices), replaying it on a re-triangulated or otherwise changed base mesh
/// reproduces the sculpt — this is what makes sculpt modifiers stackable.
/// </summary>
public sealed class SculptDisplacementField
{
    /// <summary>Samples per tile side; one tile covers TileSize * CellSize world units.</summary>
    public const int TileSize = 64;
    private const int SamplesPerTile = TileSize * TileSize;

    private readonly Dictionary<(int I, int J), float[]> _tiles = new();
    private readonly double _invCellSize;

    /// <summary>World distance between adjacent samples.</summary>
    public double CellSize { get; }

    public SculptDisplacementField(double cellSize)
    {
        if (!(cellSize > 0.0) || double.IsInfinity(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive and finite.");

        CellSize = cellSize;
        _invCellSize = 1.0 / cellSize;
    }

    public IReadOnlyDictionary<(int I, int J), float[]> Tiles => _tiles;

    public bool IsEmpty => _tiles.Count == 0;

    /// <summary>Reads the sample at global sample index (gi, gj); world position (gi * CellSize, gj * CellSize).</summary>
    public float GetSample(int gi, int gj)
    {
        var (key, local) = Locate(gi, gj);
        return _tiles.TryGetValue(key, out float[]? tile) ? tile[local] : 0f;
    }

    /// <summary>Writes the sample at global sample index (gi, gj), allocating its tile on demand.</summary>
    public void SetSample(int gi, int gj, float value)
    {
        var (key, local) = Locate(gi, gj);
        if (!_tiles.TryGetValue(key, out float[]? tile))
        {
            if (value == 0f)
                return;
            tile = new float[SamplesPerTile];
            _tiles.Add(key, tile);
        }

        tile[local] = value;
    }

    /// <summary>Bilinearly interpolated displacement at world (x, y). Zero where no tiles exist.</summary>
    public double Sample(double x, double y)
    {
        if (_tiles.Count == 0)
            return 0.0;

        double gx = x * _invCellSize;
        double gy = y * _invCellSize;
        int gi = (int)Math.Floor(gx);
        int gj = (int)Math.Floor(gy);
        double fx = gx - gi;
        double fy = gy - gj;

        double v00 = GetSample(gi, gj);
        double v10 = GetSample(gi + 1, gj);
        double v01 = GetSample(gi, gj + 1);
        double v11 = GetSample(gi + 1, gj + 1);

        double bottom = v00 + (v10 - v00) * fx;
        double top = v01 + (v11 - v01) * fx;
        return bottom + (top - bottom) * fy;
    }

    /// <summary>
    /// True when any sample within <paramref name="padding"/> of world (x, y) is non-zero.
    /// Used as the DynTopo region gate: only terrain near actual displacement gets refined.
    /// </summary>
    public bool HasInfluenceNear(double x, double y, double padding)
    {
        if (_tiles.Count == 0)
            return false;

        int giMin = (int)Math.Floor((x - padding) * _invCellSize);
        int giMax = (int)Math.Ceiling((x + padding) * _invCellSize);
        int gjMin = (int)Math.Floor((y - padding) * _invCellSize);
        int gjMax = (int)Math.Ceiling((y + padding) * _invCellSize);

        for (int gj = gjMin; gj <= gjMax; gj++)
        {
            for (int gi = giMin; gi <= giMax; gi++)
            {
                if (GetSample(gi, gj) != 0f)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// World-space bounds of all non-zero samples (expanded by one cell so bilinear support is covered).
    /// False when the field has no non-zero samples.
    /// </summary>
    public bool TryGetNonZeroBounds(out double minX, out double maxX, out double minY, out double maxY)
    {
        long giMin = long.MaxValue, giMax = long.MinValue;
        long gjMin = long.MaxValue, gjMax = long.MinValue;

        foreach (var ((tileI, tileJ), tile) in _tiles)
        {
            for (int local = 0; local < SamplesPerTile; local++)
            {
                if (tile[local] == 0f)
                    continue;

                long gi = (long)tileI * TileSize + (local % TileSize);
                long gj = (long)tileJ * TileSize + (local / TileSize);
                if (gi < giMin) giMin = gi;
                if (gi > giMax) giMax = gi;
                if (gj < gjMin) gjMin = gj;
                if (gj > gjMax) gjMax = gj;
            }
        }

        if (giMin == long.MaxValue)
        {
            minX = maxX = minY = maxY = 0.0;
            return false;
        }

        minX = (giMin - 1) * CellSize;
        maxX = (giMax + 1) * CellSize;
        minY = (gjMin - 1) * CellSize;
        maxY = (gjMax + 1) * CellSize;
        return true;
    }

    /// <summary>Removes tiles whose samples are all within <paramref name="epsilon"/> of zero.</summary>
    public void PruneZeroTiles(float epsilon = 0f)
    {
        List<(int, int)>? dead = null;
        foreach (var (key, tile) in _tiles)
        {
            bool allZero = true;
            for (int i = 0; i < SamplesPerTile; i++)
            {
                if (Math.Abs(tile[i]) > epsilon)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero)
                (dead ??= new List<(int, int)>()).Add(key);
        }

        if (dead == null)
            return;

        foreach (var key in dead)
            _tiles.Remove(key);
    }

    /// <summary>
    /// Installs (or with null, removes) a tile's sample data directly. Takes ownership of
    /// <paramref name="data"/>. Used by stroke undo and deserialization.
    /// </summary>
    public void ReplaceTile(int i, int j, float[]? data)
    {
        if (data == null)
        {
            _tiles.Remove((i, j));
            return;
        }

        if (data.Length != SamplesPerTile)
            throw new ArgumentException($"Tile data must contain {SamplesPerTile} samples.", nameof(data));

        _tiles[(i, j)] = data;
    }

    /// <summary>Returns a copy of a tile's samples, or null when the tile is absent (for undo capture).</summary>
    public float[]? CopyTile(int i, int j)
    {
        return _tiles.TryGetValue((i, j), out float[]? tile) ? (float[])tile.Clone() : null;
    }

    /// <summary>Rebuilds the field at a different sample spacing by resampling this field bilinearly.</summary>
    public SculptDisplacementField Resample(double newCellSize)
    {
        var result = new SculptDisplacementField(newCellSize);
        if (!TryGetNonZeroBounds(out double minX, out double maxX, out double minY, out double maxY))
            return result;

        int giMin = (int)Math.Floor(minX / newCellSize);
        int giMax = (int)Math.Ceiling(maxX / newCellSize);
        int gjMin = (int)Math.Floor(minY / newCellSize);
        int gjMax = (int)Math.Ceiling(maxY / newCellSize);

        for (int gj = gjMin; gj <= gjMax; gj++)
        {
            for (int gi = giMin; gi <= giMax; gi++)
            {
                float value = (float)Sample(gi * newCellSize, gj * newCellSize);
                if (value != 0f)
                    result.SetSample(gi, gj, value);
            }
        }

        result.PruneZeroTiles();
        return result;
    }

    /// <summary>Serializes tile samples as Deflate-compressed little-endian float32 (FieldVersion 1 format).</summary>
    public static byte[] EncodeTile(float[] samples)
    {
        if (samples.Length != SamplesPerTile)
            throw new ArgumentException($"Tile data must contain {SamplesPerTile} samples.", nameof(samples));

        byte[] raw = new byte[SamplesPerTile * sizeof(float)];
        for (int i = 0; i < SamplesPerTile; i++)
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * sizeof(float)), samples[i]);

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    public static float[] DecodeTile(byte[] payload)
    {
        byte[] raw = new byte[SamplesPerTile * sizeof(float)];
        using (var input = new MemoryStream(payload))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int chunk = deflate.Read(raw, read, raw.Length - read);
                if (chunk <= 0)
                    throw new InvalidDataException("Sculpt tile payload is truncated.");
                read += chunk;
            }
        }

        float[] samples = new float[SamplesPerTile];
        for (int i = 0; i < SamplesPerTile; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * sizeof(float)));
        return samples;
    }

    private static ((int I, int J) Key, int Local) Locate(int gi, int gj)
    {
        int tileI = FloorDiv(gi, TileSize);
        int tileJ = FloorDiv(gj, TileSize);
        int localI = gi - tileI * TileSize;
        int localJ = gj - tileJ * TileSize;
        return ((tileI, tileJ), localJ * TileSize + localI);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return value < 0 && q * divisor != value ? q - 1 : q;
    }
}
