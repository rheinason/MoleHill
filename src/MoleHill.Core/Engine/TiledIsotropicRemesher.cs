using MoleHill.Core.Grading;

namespace MoleHill.Core.Engine;

/// <summary>
/// Isotropic remeshing as a function of local input: the terrain is cut into world-anchored square tiles,
/// each tile is remeshed on its own with the ring of faces along its cut held, and a second pass does the
/// same on a grid offset by half a tile. Every part of the terrain is interior to a tile in one of the two
/// passes, so it is remeshed freely there, and the only faces the second pass holds are ones the first pass
/// has already remeshed well away from its own cuts.
/// </summary>
/// <remarks>
/// Why: <see cref="IsotropicRemesher"/> orders every operator over the whole mesh, so a local edit can in
/// principle move vertices anywhere, and incremental rebuilds need an exact match with a cold build
/// (docs/incremental-rebuild-design-2026-09-29.md, D1/D2). Here a tile's output depends only on the input
/// faces in that tile, so re-running the tiles an edit touches reproduces a cold build exactly; and tiles
/// are independent, so a cold build runs them in parallel. The price is that the result is not the global
/// remesher's. Whether its topology is good enough is what the prototype measures.
/// </remarks>
public static class TiledIsotropicRemesher
{
    /// <summary>Tile side as a multiple of the target edge length.</summary>
    public const double DefaultTileEdgeMultiple = 64.0;

    /// <summary>
    /// Grid offsets of the passes, as fractions of the tile side. The first pass holds raw input vertices
    /// along its cuts, and the second pass's cuts run over them where the two grids cross; the third grid
    /// puts every one of those crossings inside a tile, and its own cuts hold only remeshed edges.
    /// </summary>
    private static readonly double[] PassOffsets = [0.0, 0.5, 0.25];

    public static IsotropicRemesher.Result Remesh(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        IsotropicRemesher.Options options,
        double tileSize = 0.0)
    {
        if (options.TargetEdgeLength <= 0)
            return new IsotropicRemesher.Result { Success = false, Vertices = vertices, Faces = faces, Warning = "Tiled remesh requires a positive target edge length." };

        double size = tileSize > 0 ? tileSize : options.TargetEdgeLength * DefaultTileEdgeMultiple;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int faceCount = faces.Length / 3;
        bool[] walls = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, faceCount, options.WallFaceMinSlopeDeg, degenerateAltitude: options.Tolerance);
        TerrainFaceGrid? surface = IsotropicRemesher.BuildProjectionGrid(vertices, faces, faceCount, walls, options.TargetEdgeLength * 0.5);
        if (surface is null)
            return new IsotropicRemesher.Result { Success = true, Vertices = vertices, Faces = faces, Warning = "All faces are steep (frozen); nothing to remesh." };
        TerrainFaceGrid? wallFaces = BuildWallGrid(vertices, faces, walls, options.TargetEdgeLength);
        double msSurface = timer.Elapsed.TotalMilliseconds;
        options = WithWalls(options, wallFaces);

        double[] outV = vertices;
        int[] outF = faces;
        bool[] outFrozen = walls;
        int failed = 0;
        var passTimes = new List<string>(PassOffsets.Length);
        double previous = msSurface;
        foreach (double offset in PassOffsets)
        {
            (outV, outF, outFrozen, int passFailed) = Pass(outV, outF, outFrozen, constraints, options, size, size * offset, surface, out string passTiming);
            failed += passFailed;
            double now = timer.Elapsed.TotalMilliseconds;
            passTimes.Add($"{now - previous:0} ({passTiming})");
            previous = now;
        }

        var result = new IsotropicRemesher.Result
        {
            Success = true,
            Vertices = outV,
            Faces = outF,
            FrozenFaces = outFrozen,
            Warning = failed > 0 ? $"{failed:N0} tile(s) kept their input: the remesh of that tile was rejected." : null
        };
        result.Timing = $"tiled: surface {msSurface:0} ms, passes {string.Join(" / ", passTimes)} ms, tile {size:0.###}";
        return result;
    }

    /// <summary>One pass over a grid of <paramref name="size"/> offset by <paramref name="offset"/> in X and Y.</summary>
    internal static (double[] Vertices, int[] Faces, bool[] Frozen, int FailedTiles) Pass(
        double[] vertices,
        int[] faces,
        bool[] frozen,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        IsotropicRemesher.Options options,
        double size,
        double offset,
        TerrainFaceGrid surface)
        => Pass(vertices, faces, frozen, constraints, options, size, offset, surface, out _);

    internal static (double[] Vertices, int[] Faces, bool[] Frozen, int FailedTiles) Pass(
        double[] vertices,
        int[] faces,
        bool[] frozen,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        IsotropicRemesher.Options options,
        double size,
        double offset,
        TerrainFaceGrid surface,
        out string timing)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TileInput[] tiles = PrepareTiles(vertices, faces, frozen, options, size, offset, surface, out double msSplit, out double msCuts);
        var outputs = new (double[] V, int[] F, bool[] Frozen)[tiles.Length];
        var tileStats = new (int X, int Y, int FacesIn, int FacesOut, double Ms)[tiles.Length];
        int failed = 0;
        double msPrepared = clock.Elapsed.TotalMilliseconds;
        Parallel.For(0, tiles.Length, i =>
        {
            var tileClock = System.Diagnostics.Stopwatch.StartNew();
            TileInput tile = tiles[i];
            if (!RemeshTile(tile, constraints, options, surface, out outputs[i]))
                Interlocked.Increment(ref failed);
            tileStats[i] = (UnpackX(tile.Key), UnpackY(tile.Key), tile.Faces.Length / 3, outputs[i].F.Length / 3, tileClock.Elapsed.TotalMilliseconds);
        });

        double msTiles = clock.Elapsed.TotalMilliseconds - msPrepared;
        var stitched = Stitch(outputs, failed);
        double msStitch = clock.Elapsed.TotalMilliseconds - msPrepared - msTiles;
        timing = $"split {msSplit:0}, cuts {msCuts:0}, tiles {msTiles:0} [{tiles.Length} tiles, sum {tileStats.Sum(t => t.Ms):0}, max {tileStats.Select(t => t.Ms).DefaultIfEmpty().Max():0}], stitch {msStitch:0}";
        return stitched;
    }

    /// <summary>One tile of one pass: its faces, compacted, and its cut edges as local vertex pairs.</summary>
    internal sealed class TileInput
    {
        public required long Key { get; init; }
        public required double[] Vertices { get; init; }
        public required int[] Faces { get; init; }
        public required int[] HoldEdges { get; init; }
        public required bool[] Frozen { get; init; }
        public double MinX { get; init; }
        public double MinY { get; init; }
        public double MaxX { get; init; }
        public double MaxY { get; init; }
    }

    /// <summary>Refines along the grid lines, finds the cut edges and cuts the mesh into tiles, in a fixed order.</summary>
    internal static TileInput[] PrepareTiles(
        double[] vertices,
        int[] faces,
        bool[] frozen,
        IsotropicRemesher.Options options,
        double size,
        double offset,
        TerrainFaceGrid surface,
        out double msSplit,
        out double msCuts)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double inverse = 1.0 / size;
        var grown = new List<double>(vertices);
        (faces, frozen) = SplitLongCutEdges(grown, faces, frozen, options.TargetEdgeLength * SplitFactor, inverse, offset, surface);
        vertices = grown.ToArray();
        int faceCount = faces.Length / 3;
        msSplit = clock.Elapsed.TotalMilliseconds;
        long[] faceTile = AssignTiles(grown, faces, inverse, offset);

        // Cut edges: shared by faces in different tiles. Both tiles hold them, so the tiles weld exactly;
        // everything else on either side is remeshed freely.
        var tileCuts = new Dictionary<long, List<int>>(IndexedMeshTools.CellKeyComparer.Instance);
        foreach ((long key, int f0, int f1) in FindCutEdges(grown, faces, faceTile, inverse, offset))
        {
            int u = (int)(key >> 32), v = (int)(key & 0xFFFFFFFFL);
            AddCut(tileCuts, faceTile[f0], u, v);
            AddCut(tileCuts, faceTile[f1], u, v);
        }

        // Faces per tile in face order; tiles in a fixed order, so the output does not depend on scheduling.
        var tileFaces = new Dictionary<long, List<int>>(IndexedMeshTools.CellKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            if (!tileFaces.TryGetValue(faceTile[f], out List<int>? list))
                tileFaces[faceTile[f]] = list = new List<int>();
            list.Add(f);
        }

        long[] keys = tileFaces.Keys.ToArray();
        Array.Sort(keys, static (p, q) =>
        {
            int byY = UnpackY(p).CompareTo(UnpackY(q));
            return byY != 0 ? byY : UnpackX(p).CompareTo(UnpackX(q));
        });

        var tiles = new TileInput[keys.Length];
        Parallel.For(0, keys.Length, i =>
        {
            List<int> members = tileFaces[keys[i]];
            var localOf = new Dictionary<int, int>(members.Count);
            var localV = new List<double>(members.Count * 2);
            var localF = new int[members.Count * 3];
            var localFrozen = new bool[members.Count];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int m = 0; m < members.Count; m++)
            {
                int f = members[m];
                localFrozen[m] = frozen[f];
                for (int k = 0; k < 3; k++)
                {
                    int g = faces[f * 3 + k];
                    if (!localOf.TryGetValue(g, out int local))
                    {
                        local = localV.Count / 3;
                        localOf[g] = local;
                        double x = vertices[g * 3], y = vertices[g * 3 + 1];
                        localV.Add(x);
                        localV.Add(y);
                        localV.Add(vertices[g * 3 + 2]);
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }

                    localF[m * 3 + k] = local;
                }
            }

            int[] holdEdges = Array.Empty<int>();
            if (tileCuts.TryGetValue(keys[i], out List<int>? cuts))
            {
                holdEdges = new int[cuts.Count];
                for (int c = 0; c < cuts.Count; c++)
                    holdEdges[c] = localOf[cuts[c]];
            }

            tiles[i] = new TileInput
            {
                Key = keys[i],
                Vertices = localV.ToArray(),
                Faces = localF,
                HoldEdges = holdEdges,
                Frozen = localFrozen,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY
            };
        });

        msCuts = clock.Elapsed.TotalMilliseconds - msSplit;
        return tiles;
    }

    /// <summary>Remeshes one tile; on a rejected remesh the tile keeps its input and this returns false.</summary>
    internal static bool RemeshTile(
        TileInput tile,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        IsotropicRemesher.Options options,
        TerrainFaceGrid surface,
        out (double[] V, int[] F, bool[] Frozen) output)
    {
        var tileConstraints = constraints.Where(c => Overlaps(c, tile.MinX, tile.MinY, tile.MaxX, tile.MaxY)).ToList();
        var tileOptions = new IsotropicRemesher.Options
        {
            TargetEdgeLength = options.TargetEdgeLength,
            CreaseAngleDeg = options.CreaseAngleDeg,
            Tolerance = options.Tolerance,
            WallFaceMinSlopeDeg = options.WallFaceMinSlopeDeg,
            Iterations = options.Iterations,
            ShouldCancel = options.ShouldCancel,
            HoldEdges = tile.HoldEdges,
            Projection = surface,
            FrozenFaces = tile.Frozen,
            WallFaces = options.WallFaces
        };

        IsotropicRemesher.Result result = IsotropicRemesher.Remesh(tile.Vertices, tile.Faces, tileConstraints, tileOptions);
        output = result.Success && result.FrozenFaces.Length == result.Faces.Length / 3
            ? (result.Vertices, result.Faces, result.FrozenFaces)
            : (tile.Vertices, tile.Faces, tile.Frozen);
        return result.Success;
    }

    /// <summary>A grid over the original wall faces alone, or null when there are none.</summary>
    internal static TerrainFaceGrid? BuildWallGrid(double[] vertices, int[] faces, bool[] walls, double target)
    {
        int count = walls.Count(static w => w);
        if (count == 0)
            return null;

        var wallFaces = new int[count * 3];
        int next = 0;
        for (int f = 0; f < walls.Length; f++)
        {
            if (!walls[f])
                continue;
            wallFaces[next++] = faces[f * 3];
            wallFaces[next++] = faces[f * 3 + 1];
            wallFaces[next++] = faces[f * 3 + 2];
        }

        return new TerrainFaceGrid(vertices, vertices.Length / 3, wallFaces, count, target * 0.5);
    }

    internal static IsotropicRemesher.Options WithWalls(IsotropicRemesher.Options options, TerrainFaceGrid? wallFaces) => new()
    {
        TargetEdgeLength = options.TargetEdgeLength,
        CreaseAngleDeg = options.CreaseAngleDeg,
        Tolerance = options.Tolerance,
        WallFaceMinSlopeDeg = options.WallFaceMinSlopeDeg,
        Iterations = options.Iterations,
        ShouldCancel = options.ShouldCancel,
        WallFaces = wallFaces
    };

    // IsotropicRemesher's split threshold: a cut edge is never left longer than the remesher leaves an edge.
    private const double SplitFactor = 8.0 / 5.0;

    /// <summary>
    /// Refines the faces that straddle a grid line until none has an edge longer than
    /// <paramref name="threshold"/>, by the remesher's own rule: each such face bisects its longest edge,
    /// round after round. A tile cannot split a cut edge on its own (its neighbour would not match), so cut
    /// edges must be short before tiling, and every cut edge belongs to a straddling face. Splitting only the
    /// cut edges instead fanned a big flat pad triangle into slivers that three iterations could not coarsen
    /// back (654 faces became 40,692 in one tile of the 1 m park). Deterministic: a face's choice depends on
    /// its own geometry, and midpoints are added in edge-key order.
    /// </summary>
    private static (int[] Faces, bool[] Frozen) SplitLongCutEdges(List<double> verts, int[] faces, bool[] frozen, double threshold, double inverse, double offset, TerrainFaceGrid surface)
    {
        double thresholdSquared = threshold * threshold;
        int[] tris = faces;
        for (int round = 0; round < 24; round++)
        {
            int faceCount = tris.Length / 3;
            bool[] crossing = CrossingFaces(verts, tris, inverse, offset);
            var marked = IndexedMeshTools.CreateEdgeKeySet(0);
            for (int f = 0; f < faceCount; f++)
            {
                if (!crossing[f])
                    continue;
                long longest = LongestEdge(verts, tris[f * 3], tris[f * 3 + 1], tris[f * 3 + 2], out double lengthSquared);
                if (lengthSquared > thresholdSquared)
                    marked.Add(longest);
            }

            if (marked.Count == 0)
                return (tris, frozen);

            // Only faces on a split edge change; the rest are copied as they are.
            var touched = new bool[faceCount];
            ForBlocks(faceCount, (from, to) =>
            {
                for (int f = from; f < to; f++)
                {
                    int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                    touched[f] = marked.Contains(EdgeKey(v0, v1)) || marked.Contains(EdgeKey(v1, v2)) || marked.Contains(EdgeKey(v2, v0));
                }
            });

            // An edge of a wall face keeps its chord, which is exact on the planar wall; the terrain surface
            // excludes walls, so projecting there samples the wall's top or foot. Every other midpoint goes onto
            // the original surface: a remeshed edge's chord is not on it (0.86 m off across a fold on the park).
            var onWall = IndexedMeshTools.CreateEdgeKeySet(0);
            for (int f = 0; f < faceCount; f++)
            {
                if (!touched[f] || !frozen[f])
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    long key = EdgeKey(tris[f * 3 + k], tris[f * 3 + ((k + 1) % 3)]);
                    if (marked.Contains(key))
                        onWall.Add(key);
                }
            }

            long[] keys = marked.ToArray();
            Array.Sort(keys);
            var midpoints = IndexedMeshTools.CreateEdgeKeyMap<int>(keys.Length);
            foreach (long key in keys)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFFL);
                midpoints[key] = verts.Count / 3;
                double mx = (verts[a * 3] + verts[b * 3]) * 0.5;
                double my = (verts[a * 3 + 1] + verts[b * 3 + 1]) * 0.5;
                double mz = (verts[a * 3 + 2] + verts[b * 3 + 2]) * 0.5;
                if (!onWall.Contains(key) && surface.TryInterpolateZ(mx, my, out double projected))
                    mz = projected;
                verts.Add(mx);
                verts.Add(my);
                verts.Add(mz);
            }

            var next = new List<int>(tris.Length + (keys.Length * 6));
            var nextFrozen = new List<bool>(faceCount + (keys.Length * 2));
            for (int f = 0; f < faceCount; f++)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                if (!touched[f])
                {
                    next.Add(v0);
                    next.Add(v1);
                    next.Add(v2);
                    nextFrozen.Add(frozen[f]);
                    continue;
                }

                int m0 = midpoints.TryGetValue(EdgeKey(v0, v1), out int i0) ? i0 : -1;
                int m1 = midpoints.TryGetValue(EdgeKey(v1, v2), out int i1) ? i1 : -1;
                int m2 = midpoints.TryGetValue(EdgeKey(v2, v0), out int i2) ? i2 : -1;
                int before = next.Count / 3;
                IsotropicRemesher.EmitRefinedTriangle(verts, next, v0, v1, v2, m0, m1, m2);
                for (int child = next.Count / 3 - before; child > 0; child--)
                    nextFrozen.Add(frozen[f]);
            }

            tris = next.ToArray();
            frozen = nextFrozen.ToArray();
        }

        return (tris, frozen);
    }

    /// <summary>Faces whose plan bounding box straddles a grid line.</summary>
    private static bool[] CrossingFaces(List<double> verts, int[] tris, double inverse, double offset)
    {
        int faceCount = tris.Length / 3;
        var crossing = new bool[faceCount];
        ForBlocks(faceCount, (from, to) =>
        {
            ReadOnlySpan<double> v = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts);
            for (int f = from; f < to; f++)
            {
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                double minX = Math.Min(v[a * 3], Math.Min(v[b * 3], v[c * 3]));
                double maxX = Math.Max(v[a * 3], Math.Max(v[b * 3], v[c * 3]));
                double minY = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
                double maxY = Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1]));
                crossing[f] = Math.Floor((minX - offset) * inverse) != Math.Floor((maxX - offset) * inverse) ||
                              Math.Floor((minY - offset) * inverse) != Math.Floor((maxY - offset) * inverse);
            }
        });

        return crossing;
    }

    private static long LongestEdge(List<double> verts, int a, int b, int c, out double lengthSquared)
    {
        double ab = LengthSquared(verts, a, b), bc = LengthSquared(verts, b, c), ca = LengthSquared(verts, c, a);
        if (ab >= bc && ab >= ca)
        {
            lengthSquared = ab;
            return EdgeKey(a, b);
        }

        if (bc >= ca)
        {
            lengthSquared = bc;
            return EdgeKey(b, c);
        }

        lengthSquared = ca;
        return EdgeKey(c, a);
    }

    /// <summary>
    /// Edges between faces in different tiles, as (edge key, face, face), in edge-key order. Two faces whose
    /// centroids fall in different tiles span a grid line between them, so at least one of the two has a
    /// bounding box crossing a line: only those faces' edges are indexed, and one lookup pass over the rest
    /// finds their partners. Indexing every edge of a 2M-face terrain made this most of a pass.
    /// </summary>
    private static List<(long Key, int F0, int F1)> FindCutEdges(List<double> verts, int[] tris, long[] faceTile, double inverse, double offset)
    {
        int faceCount = tris.Length / 3;
        bool[] crossing = CrossingFaces(verts, tris, inverse, offset);

        int crossingCount = 0;
        foreach (bool c in crossing)
        {
            if (c)
                crossingCount++;
        }

        var faceOfEdge = IndexedMeshTools.CreateEdgeKeyMap<int>(crossingCount * 3);
        var cuts = new List<(long Key, int F0, int F1)>();
        for (int f = 0; f < faceCount; f++)
        {
            if (!crossing[f])
                continue;
            for (int k = 0; k < 3; k++)
            {
                long key = EdgeKey(tris[f * 3 + k], tris[f * 3 + ((k + 1) % 3)]);
                if (!faceOfEdge.TryAdd(key, f) && faceTile[faceOfEdge[key]] != faceTile[f])
                    cuts.Add((key, faceOfEdge[key], f));
            }
        }

        // Partners among the rest: read-only lookups, so in parallel; the sort below fixes the order.
        var gate = new object();
        ForBlocks(faceCount, (from, to) =>
        {
            List<(long Key, int F0, int F1)>? found = null;
            for (int f = from; f < to; f++)
            {
                if (crossing[f])
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    long key = EdgeKey(tris[f * 3 + k], tris[f * 3 + ((k + 1) % 3)]);
                    if (faceOfEdge.TryGetValue(key, out int other) && faceTile[other] != faceTile[f])
                        (found ??= new List<(long Key, int F0, int F1)>()).Add((key, other, f));
                }
            }

            if (found != null)
            {
                lock (gate)
                    cuts.AddRange(found);
            }
        });

        cuts.Sort(static (p, q) => p.Key.CompareTo(q.Key));
        return cuts;
    }

    private static long[] AssignTiles(List<double> verts, int[] tris, double inverse, double offset)
    {
        int faceCount = tris.Length / 3;
        var faceTile = new long[faceCount];
        ForBlocks(faceCount, (from, to) =>
        {
            ReadOnlySpan<double> v = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts);
            for (int f = from; f < to; f++)
            {
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                double cx = (v[a * 3] + v[b * 3] + v[c * 3]) / 3.0;
                double cy = (v[a * 3 + 1] + v[b * 3 + 1] + v[c * 3 + 1]) / 3.0;
                faceTile[f] = PackTile((long)Math.Floor((cx - offset) * inverse), (long)Math.Floor((cy - offset) * inverse));
            }
        });

        return faceTile;
    }

    /// <summary>Runs <paramref name="body"/> over [0, count) in blocks, in parallel when the count is large.</summary>
    private static void ForBlocks(int count, Action<int, int> body)
    {
        const int block = 32_768;
        if (count <= block)
        {
            body(0, count);
            return;
        }

        Parallel.For(0, (count + block - 1) / block, b => body(b * block, Math.Min(count, (b + 1) * block)));
    }

    private static void AddCut(Dictionary<long, List<int>> tileCuts, long tile, int u, int v)
    {
        if (!tileCuts.TryGetValue(tile, out List<int>? list))
            tileCuts[tile] = list = new List<int>();
        list.Add(u);
        list.Add(v);
    }

    private static long EdgeKey(int u, int v) => u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;

    private static double LengthSquared(List<double> verts, int a, int b)
    {
        double dx = verts[a * 3] - verts[b * 3], dy = verts[a * 3 + 1] - verts[b * 3 + 1], dz = verts[a * 3 + 2] - verts[b * 3 + 2];
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>
    /// Concatenates the tiles and welds vertices at identical coordinates. Held vertices keep their input
    /// coordinates bit for bit in every tile that holds them, so the cut welds exactly.
    /// </summary>
    private static (double[] Vertices, int[] Faces, bool[] Frozen, int FailedTiles) Stitch((double[] V, int[] F, bool[] Frozen)[] outputs, int failed)
    {
        int totalVertices = outputs.Sum(static o => o.V.Length / 3);
        int totalFaces = outputs.Sum(static o => o.F.Length / 3);
        var index = new Dictionary<(long, long, long), int>(totalVertices);
        var vertices = new List<double>(totalVertices * 3);
        var faces = new int[totalFaces * 3];
        var frozen = new bool[totalFaces];
        int write = 0;
        int writeFace = 0;
        foreach ((double[] v, int[] f, bool[] fz) in outputs)
        {
            int count = v.Length / 3;
            var remap = new int[count];
            for (int i = 0; i < count; i++)
            {
                var key = (BitConverter.DoubleToInt64Bits(v[i * 3]), BitConverter.DoubleToInt64Bits(v[i * 3 + 1]), BitConverter.DoubleToInt64Bits(v[i * 3 + 2]));
                if (!index.TryGetValue(key, out int global))
                {
                    global = vertices.Count / 3;
                    index[key] = global;
                    vertices.Add(v[i * 3]);
                    vertices.Add(v[i * 3 + 1]);
                    vertices.Add(v[i * 3 + 2]);
                }

                remap[i] = global;
            }

            for (int i = 0; i < f.Length; i++)
                faces[write++] = remap[f[i]];
            Array.Copy(fz, 0, frozen, writeFace, fz.Length);
            writeFace += fz.Length;
        }

        return (vertices.ToArray(), faces, frozen, failed);
    }

    private static bool Overlaps(SurfaceRemesher.ConstraintPolyline constraint, double minX, double minY, double maxX, double maxY)
    {
        for (int i = 0; i < constraint.PointCount; i++)
        {
            double x = constraint.Points[i * 3], y = constraint.Points[i * 3 + 1];
            if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                return true;
        }

        // A segment can cross a tile with no vertex in it; a bounding-box test keeps it conservative.
        double cMinX = double.MaxValue, cMinY = double.MaxValue, cMaxX = double.MinValue, cMaxY = double.MinValue;
        for (int i = 0; i < constraint.PointCount; i++)
        {
            cMinX = Math.Min(cMinX, constraint.Points[i * 3]);
            cMaxX = Math.Max(cMaxX, constraint.Points[i * 3]);
            cMinY = Math.Min(cMinY, constraint.Points[i * 3 + 1]);
            cMaxY = Math.Max(cMaxY, constraint.Points[i * 3 + 1]);
        }

        return cMinX <= maxX && cMaxX >= minX && cMinY <= maxY && cMaxY >= minY;
    }

    private static long PackTile(long x, long y) => (x << 32) | (uint)(int)y;

    private static int UnpackX(long key) => (int)(key >> 32);

    private static int UnpackY(long key) => (int)(key & 0xFFFFFFFFL);
}
