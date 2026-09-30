using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain window by window: items (pads, paths) whose reach overlaps are grouped, each group is
/// graded on the faces under its window alone, and the results are stitched back into the untouched rest.
/// </summary>
/// <remarks>
/// Why (docs/incremental-rebuild-design-2026-09-29.md, "windowed graders"): a grader's changes stay within its
/// items' reach (measured: 13.7 m for a pad with a 15 m max distance), so the rest of the terrain passes
/// through. Grading a window alone makes that the definition rather than an observation, and it makes each
/// window's result a pure function of its key: the window's faces in canonical order plus whatever the caller
/// says its items depend on. A memo of window results is then exact, and an edit re-grades only the windows it
/// touches, whether the edit is to the terrain under them or to one item's settings.
///
/// A window's faces are those whose bounding box meets the window box. A face fully inside the box cannot
/// border a face outside the window, so the faces the window shares edges with the rest are the ones that
/// straddle the box edge. Those must come back from grading unchanged, or the patch would not weld: when one
/// does not, the caller grades the whole terrain instead (<see cref="Outcome.NeedsWholeMesh"/>).
/// </remarks>
public static class GradingWindows
{
    /// <summary>Grades one window: the window's local mesh, the indices of its items, and its box.</summary>
    public delegate GradingResult? WindowGrader(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        int[] items,
        (double MinX, double MinY, double MaxX, double MaxY) box,
        out string? errorMessage,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out IReadOnlyList<GradingDiagnostic> failureDiagnostics);

    /// <summary>Everything a window's result depends on besides its faces: its items' settings, the locks and
    /// constraints over it, tolerances. Folded into the window's memo key.</summary>
    public delegate void WindowContext(int[] items, double minX, double minY, double maxX, double maxY, XxHash64Builder low, XxHash64Builder high);

    public sealed class Memo
    {
        internal Dictionary<UInt128, WindowResult> Windows { get; } = new();

        public int ReusedWindows { get; internal set; }

        public int GradedWindows { get; internal set; }
    }

    internal sealed record WindowResult(
        GradingResult? Result,
        string? Error,
        IReadOnlyList<OutputPolyline> FailureOutputPolylines,
        IReadOnlyList<GradingDiagnostic> FailureDiagnostics,
        bool EdgesKept);

    public sealed class Outcome
    {
        /// <summary>The stitched result; null when <see cref="NeedsWholeMesh"/> or every window failed with nothing to return.</summary>
        public GradingResult? Result { get; init; }

        /// <summary>A window changed a face it shares with the rest of the terrain: grade the whole mesh instead.</summary>
        public bool NeedsWholeMesh { get; init; }

        /// <summary>Errors of windows whose grade failed (those windows keep their input faces).</summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        public IReadOnlyList<OutputPolyline> FailureOutputPolylines { get; init; } = Array.Empty<OutputPolyline>();

        public IReadOnlyList<GradingDiagnostic> FailureDiagnostics { get; init; } = Array.Empty<GradingDiagnostic>();

        public int WindowCount { get; init; }

        public int ReusedWindows { get; init; }
    }

    /// <summary>
    /// Grades every window. <paramref name="reach"/> is each item's plan extent grown by how far it can change
    /// the terrain; <paramref name="margin"/> is added beyond that so the faces along a window's edge are
    /// outside every item's reach.
    /// </summary>
    public static Outcome Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> reach,
        double margin,
        WindowGrader grade,
        WindowContext context,
        Memo? previous,
        Memo next)
    {
        List<(double MinX, double MinY, double MaxX, double MaxY, int[] Items)> windows = GroupWindows(reach, margin);
        if (windows.Count == 0)
            return new Outcome { Result = null, WindowCount = 0 };

        // Faces per window by bounding-box overlap; a face two windows share merges them. A window may not
        // have holes: a face it encloses is its own, or grading would fill the hole with a face the rest of the
        // terrain still has (a one-face pocket 8 mm past a window box did exactly that).
        int[] owner;
        while (true)
        {
            owner = AssignFaces(vertices, faces, faceCount, windows);
            (int mergeA, int mergeB) = FillHoles(vertices, faces, faceCount, owner, windows.Count);
            if (mergeA < 0)
                break;
            MergeWindows(windows, mergeA, mergeB);
        }
        var outputs = new (GradingResult? Result, int[] Faces, WindowResult Graded)[windows.Count];
        var memberFaces = new List<int>[windows.Count];
        for (int w = 0; w < windows.Count; w++)
            memberFaces[w] = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            if (owner[f] >= 0)
                memberFaces[owner[f]].Add(f);
        }

        HashSet<EdgeKey>[] interfaces = InterfaceEdges(vertices, faces, faceCount, owner, memberFaces);
        int reused = 0;
        var keys = new UInt128[windows.Count];
        var locals = new (double[] V, int[] F)[windows.Count];
        Parallel.For(0, windows.Count, w =>
        {
            if (memberFaces[w].Count == 0)
                return;
            locals[w] = ExtractCanonical(vertices, faces, memberFaces[w]);
            keys[w] = WindowKey(locals[w].V, locals[w].F, windows[w], context);
        });

        for (int w = 0; w < windows.Count; w++)
        {
            if (memberFaces[w].Count == 0)
                continue;

            WindowResult graded;
            if (previous != null && previous.Windows.TryGetValue(keys[w], out WindowResult? cached))
            {
                graded = cached;
                reused++;
            }
            else
            {
                (double[] v, int[] f) = locals[w];
                var box = (windows[w].MinX, windows[w].MinY, windows[w].MaxX, windows[w].MaxY);
                GradingResult? result = grade(v, v.Length / 3, f, f.Length / 3, windows[w].Items, box, out string? error, out var failurePolylines, out var failureDiagnostics);
                graded = new WindowResult(result, error, failurePolylines, failureDiagnostics,
                    result == null || Welds(BoundaryEdges(result.Vertices, result.Faces, result.FaceCount), interfaces[w]));
            }

            next.Windows[keys[w]] = graded;
            if (!graded.EdgesKept)
                return new Outcome { NeedsWholeMesh = true, WindowCount = windows.Count, ReusedWindows = reused };
            outputs[w] = (graded.Result, memberFaces[w].ToArray(), graded);
        }

        next.ReusedWindows = reused;
        next.GradedWindows = windows.Count - reused;
        return Stitch(vertices, vertexCount, faces, faceCount, owner, outputs, reused);
    }

    /// <summary>
    /// How far a batter from a pad at heights [<paramref name="lowZ"/>, <paramref name="highZ"/>] with
    /// slope <paramref name="slopeDeg"/> can reach before it meets the terrain, for a pad with no max distance.
    /// The batter has daylighted once it has covered the terrain's largest height difference from the pad, so
    /// the radius is grown until the height difference within it no longer asks for more. Reads only the
    /// terrain within the final radius.
    /// </summary>
    public static double DaylightReach(
        double[] vertices,
        int vertexCount,
        (double MinX, double MinY, double MaxX, double MaxY) padBox,
        double lowZ,
        double highZ,
        double slopeDeg)
    {
        double tangent = Math.Tan(Math.Clamp(slopeDeg, 0.5, 89.5) * Math.PI / 180.0);
        double radius = Math.Max(padBox.MaxX - padBox.MinX, padBox.MaxY - padBox.MinY) * 0.25;
        for (int round = 0; round < 32; round++)
        {
            double worst = 0.0;
            for (int i = 0; i < vertexCount; i++)
            {
                double x = vertices[i * 3], y = vertices[i * 3 + 1];
                if (x < padBox.MinX - radius || x > padBox.MaxX + radius || y < padBox.MinY - radius || y > padBox.MaxY + radius)
                    continue;
                double z = vertices[i * 3 + 2];
                worst = Math.Max(worst, Math.Max(z - lowZ, highZ - z));
            }

            double needed = worst / tangent;
            if (needed <= radius)
                return needed;
            radius = needed;
        }

        return radius;
    }

    /// <summary>The longest plan extent of any face whose box meets one of <paramref name="boxes"/>.</summary>
    public static double LongestPlanEdgeNear(double[] v, int[] faces, int faceCount, IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> boxes)
    {
        double longest = 0.0;
        for (int f = 0; f < faceCount; f++)
        {
            (double minX, double minY, double maxX, double maxY) = FaceBox(v, faces, f);
            bool near = false;
            foreach (var box in boxes)
            {
                if (maxX >= box.MinX && minX <= box.MaxX && maxY >= box.MinY && minY <= box.MaxY)
                {
                    near = true;
                    break;
                }
            }

            if (near)
                longest = Math.Max(longest, Math.Max(maxX - minX, maxY - minY));
        }

        return longest;
    }

    /// <summary>True when the XY polyline's bounding box meets <paramref name="box"/>.</summary>
    public static bool XyOverlaps(double[] xy, int count, (double MinX, double MinY, double MaxX, double MaxY) box)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            minX = Math.Min(minX, xy[i * 2]); maxX = Math.Max(maxX, xy[i * 2]);
            minY = Math.Min(minY, xy[i * 2 + 1]); maxY = Math.Max(maxY, xy[i * 2 + 1]);
        }

        return count > 0 && maxX >= box.MinX && minX <= box.MaxX && maxY >= box.MinY && minY <= box.MaxY;
    }

    /// <summary>True when the XYZ polyline's plan bounding box meets <paramref name="box"/>.</summary>
    public static bool PointsOverlap(double[] xyz, int count, (double MinX, double MinY, double MaxX, double MaxY) box)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            minX = Math.Min(minX, xyz[i * 3]); maxX = Math.Max(maxX, xyz[i * 3]);
            minY = Math.Min(minY, xyz[i * 3 + 1]); maxY = Math.Max(maxY, xyz[i * 3 + 1]);
        }

        return count > 0 && maxX >= box.MinX && minX <= box.MaxX && maxY >= box.MinY && minY <= box.MaxY;
    }

    private static List<(double MinX, double MinY, double MaxX, double MaxY, int[] Items)> GroupWindows(
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> reach, double margin)
    {
        int n = reach.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Overlap(Grow(reach[i], margin), Grow(reach[j], margin)))
                    parent[Find(i)] = Find(j);
            }
        }

        var groups = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out List<int>? list))
                groups[root] = list = new List<int>();
            list.Add(i);
        }

        var windows = new List<(double, double, double, double, int[])>();
        foreach (List<int> members in groups.Values)
        {
            var box = Grow(reach[members[0]], margin);
            foreach (int m in members)
            {
                var r = Grow(reach[m], margin);
                box = (Math.Min(box.MinX, r.MinX), Math.Min(box.MinY, r.MinY), Math.Max(box.MaxX, r.MaxX), Math.Max(box.MaxY, r.MaxY));
            }

            windows.Add((box.MinX, box.MinY, box.MaxX, box.MaxY, members.ToArray()));
        }

        // A fixed order: by the window's lowest item.
        windows.Sort(static (a, b) => a.Item5[0].CompareTo(b.Item5[0]));
        return windows;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Grow((double MinX, double MinY, double MaxX, double MaxY) box, double by) =>
        (box.MinX - by, box.MinY - by, box.MaxX + by, box.MaxY + by);

    private static bool Overlap((double MinX, double MinY, double MaxX, double MaxY) a, (double MinX, double MinY, double MaxX, double MaxY) b) =>
        a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    /// <summary>Window of each face, or -1; windows that would share a face are merged first.</summary>
    private static int[] AssignFaces(double[] vertices, int[] faces, int faceCount, List<(double MinX, double MinY, double MaxX, double MaxY, int[] Items)> windows)
    {
        var owner = new int[faceCount];
        while (true)
        {
            Array.Fill(owner, -1);
            int mergeA = -1, mergeB = -1;
            for (int f = 0; f < faceCount && mergeA < 0; f++)
            {
                (double minX, double minY, double maxX, double maxY) = FaceBox(vertices, faces, f);
                for (int w = 0; w < windows.Count; w++)
                {
                    var box = windows[w];
                    if (maxX < box.MinX || minX > box.MaxX || maxY < box.MinY || minY > box.MaxY)
                        continue;
                    if (owner[f] >= 0 && owner[f] != w)
                    {
                        mergeA = owner[f];
                        mergeB = w;
                        break;
                    }

                    owner[f] = w;
                }
            }

            if (mergeA < 0)
                return owner;

            MergeWindows(windows, mergeA, mergeB);
        }
    }

    private static void MergeWindows(List<(double MinX, double MinY, double MaxX, double MaxY, int[] Items)> windows, int mergeA, int mergeB)
    {
        var a = windows[mergeA];
        var b = windows[mergeB];
        int[] items = a.Items.Concat(b.Items).OrderBy(static i => i).ToArray();
        windows[mergeA] = (Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY), items);
        windows.RemoveAt(mergeB);
        windows.Sort(static (x, y) => x.Items[0].CompareTo(y.Items[0]));
    }

    /// <summary>
    /// Gives each window the faces inside its holes: any boundary loop of a window other than its outer one
    /// encloses faces it must own. Faces across a hole's edges are claimed, round after round, until the hole
    /// is gone; a hole of the terrain itself has no faces and stays. Returns two windows to merge when a hole
    /// holds another window's face, else (-1, -1).
    /// </summary>
    private static (int A, int B) FillHoles(double[] vertices, int[] faces, int faceCount, int[] owner, int windowCount)
    {
        for (int round = 0; round < 64; round++)
        {
            // Edges of every window's inner loops, with their window.
            var holeEdges = IndexedMeshTools.CreateEdgeKeyMap<int>();
            var windowFaces = new List<int>[windowCount];
            for (int w = 0; w < windowCount; w++)
                windowFaces[w] = new List<int>();
            for (int f = 0; f < faceCount; f++)
            {
                if (owner[f] >= 0)
                    windowFaces[owner[f]].Add(f);
            }

            var rimEdges = IndexedMeshTools.CreateEdgeKeyMap<int>();
            for (int w = 0; w < windowCount; w++)
            {
                List<long> inner = InnerLoopEdges(vertices, faces, windowFaces[w], out List<long> boundary);
                foreach (long edge in inner)
                    holeEdges[edge] = w;
                foreach (long edge in boundary)
                    rimEdges[edge] = w;
            }

            // A face all three of whose edges a window already bounds is enclosed by it even when one of its
            // corners touches the window's outer rim, so the pocket is no separate loop.
            bool claimed = false;
            for (int f = 0; f < faceCount; f++)
            {
                if (owner[f] >= 0)
                    continue;
                int enclosing = -1;
                bool enclosed = true;
                for (int k = 0; k < 3 && enclosed; k++)
                {
                    long edge = PackEdge(faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                    if (!rimEdges.TryGetValue(edge, out int w) || (enclosing >= 0 && enclosing != w))
                        enclosed = false;
                    else
                        enclosing = w;
                }

                if (enclosed)
                {
                    owner[f] = enclosing;
                    claimed = true;
                }
            }

            if (claimed)
                continue;
            if (holeEdges.Count == 0)
                return (-1, -1);
            for (int f = 0; f < faceCount; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    long edge = PackEdge(faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                    if (!holeEdges.TryGetValue(edge, out int w) || owner[f] == w)
                        continue;
                    if (owner[f] >= 0)
                        return (Math.Min(owner[f], w), Math.Max(owner[f], w));
                    owner[f] = w;
                    claimed = true;
                    break;
                }
            }

            if (!claimed)
                return (-1, -1);
        }

        return (-1, -1);
    }

    private static long PackEdge(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>Boundary edges of a set of faces that do not lie on its outer loop (the loop through its lowest-x vertex).</summary>
    private static List<long> InnerLoopEdges(double[] vertices, int[] faces, List<int> members, out List<long> boundary)
    {
        var uses = IndexedMeshTools.CreateEdgeKeyMap<int>(members.Count * 3);
        foreach (int f in members)
        {
            for (int k = 0; k < 3; k++)
            {
                long edge = PackEdge(faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                uses[edge] = uses.GetValueOrDefault(edge) + 1;
            }
        }

        boundary = uses.Where(static e => e.Value == 1).Select(static e => e.Key).ToList();
        if (boundary.Count == 0)
            return new List<long>();

        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        int lowest = -1;
        foreach (long edge in boundary)
        {
            int a = (int)(edge >> 32), b = (int)(edge & 0xFFFFFFFFL);
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            parent[Find(a)] = Find(b);
            foreach (int q in new[] { a, b })
            {
                if (lowest < 0 || vertices[q * 3] < vertices[lowest * 3] ||
                    (vertices[q * 3] == vertices[lowest * 3] && vertices[q * 3 + 1] < vertices[lowest * 3 + 1]))
                    lowest = q;
            }
        }

        int outer = Find(lowest);
        return boundary.Where(edge => Find((int)(edge >> 32)) != outer).ToList();
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) FaceBox(double[] v, int[] faces, int f)
    {
        int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
        return (Math.Min(v[a * 3], Math.Min(v[b * 3], v[c * 3])), Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1])),
                Math.Max(v[a * 3], Math.Max(v[b * 3], v[c * 3])), Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1])));
    }

    /// <summary>A face by its corners' coordinates, starting at the smallest corner (winding kept).</summary>
    internal readonly record struct FaceKey(double X0, double Y0, double Z0, double X1, double Y1, double Z1, double X2, double Y2, double Z2);

    private static FaceKey KeyOf(double[] v, int a, int b, int c)
    {
        var corners = new[] { a, b, c };
        int s = 0;
        for (int k = 1; k < 3; k++)
        {
            if (ComparePoints(v, corners[k], corners[s]) < 0)
                s = k;
        }

        int p = corners[s], q = corners[(s + 1) % 3], r = corners[(s + 2) % 3];
        return new FaceKey(v[p * 3], v[p * 3 + 1], v[p * 3 + 2], v[q * 3], v[q * 3 + 1], v[q * 3 + 2], v[r * 3], v[r * 3 + 1], v[r * 3 + 2]);
    }

    private static int ComparePoints(double[] v, int a, int b)
    {
        int c = v[a * 3].CompareTo(v[b * 3]);
        if (c != 0)
            return c;
        c = v[a * 3 + 1].CompareTo(v[b * 3 + 1]);
        return c != 0 ? c : v[a * 3 + 2].CompareTo(v[b * 3 + 2]);
    }

    /// <summary>
    /// The window's faces as a self-contained mesh in canonical order (each face from its smallest corner,
    /// faces sorted by corners, vertices by first use), and the faces straddling the window edge.
    /// </summary>
    private static (double[] V, int[] F) ExtractCanonical(double[] vertices, int[] faces, List<int> members)
    {
        var keyed = new (FaceKey Key, int Face)[members.Count];
        for (int m = 0; m < members.Count; m++)
        {
            int f = members[m];
            keyed[m] = (KeyOf(vertices, faces[f * 3], faces[f * 3 + 1], faces[f * 3 + 2]), f);
        }

        Array.Sort(keyed, static (p, q) => Compare(p.Key, q.Key));
        var localOf = new Dictionary<(double, double, double), int>(members.Count);
        var localV = new List<double>(members.Count * 2);
        var localF = new int[members.Count * 3];
        for (int m = 0; m < keyed.Length; m++)
        {
            FaceKey k = keyed[m].Key;
            localF[m * 3] = Local(k.X0, k.Y0, k.Z0);
            localF[m * 3 + 1] = Local(k.X1, k.Y1, k.Z1);
            localF[m * 3 + 2] = Local(k.X2, k.Y2, k.Z2);
        }

        return (localV.ToArray(), localF);

        int Local(double x, double y, double z)
        {
            if (!localOf.TryGetValue((x, y, z), out int index))
            {
                index = localV.Count / 3;
                localOf[(x, y, z)] = index;
                localV.Add(x);
                localV.Add(y);
                localV.Add(z);
            }

            return index;
        }
    }

    private static int Compare(FaceKey a, FaceKey b)
    {
        int c = a.X0.CompareTo(b.X0); if (c != 0) return c;
        c = a.Y0.CompareTo(b.Y0); if (c != 0) return c;
        c = a.Z0.CompareTo(b.Z0); if (c != 0) return c;
        c = a.X1.CompareTo(b.X1); if (c != 0) return c;
        c = a.Y1.CompareTo(b.Y1); if (c != 0) return c;
        c = a.Z1.CompareTo(b.Z1); if (c != 0) return c;
        c = a.X2.CompareTo(b.X2); if (c != 0) return c;
        c = a.Y2.CompareTo(b.Y2); if (c != 0) return c;
        return a.Z2.CompareTo(b.Z2);
    }

    private static UInt128 WindowKey(double[] v, int[] f, (double MinX, double MinY, double MaxX, double MaxY, int[] Items) window, WindowContext context)
    {
        var low = new XxHash64Builder(0x6a770001UL);
        var high = new XxHash64Builder(0x6a770002UL);
        ReadOnlySpan<byte> vb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan());
        ReadOnlySpan<byte> fb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(f.AsSpan());
        low.AddBytes(vb); high.AddBytes(vb);
        low.AddBytes(fb); high.AddBytes(fb);
        foreach (double d in new[] { window.MinX, window.MinY, window.MaxX, window.MaxY })
        {
            low.Add(d);
            high.Add(d);
        }

        context(window.Items, window.MinX, window.MinY, window.MaxX, window.MaxY, low, high);
        return new UInt128(high.ToUInt64(), low.ToUInt64());
    }

    /// <summary>
    /// Per window, the boundary edges it shares with faces it does not own (the untouched terrain or another
    /// window). A graded window welds back exactly when every one of them is still a boundary edge of its
    /// patch and no other patch boundary edge is one of them; edges on the terrain's own outer border are free
    /// to change, as they are when the whole terrain is graded.
    /// </summary>
    private static HashSet<EdgeKey>[] InterfaceEdges(double[] v, int[] faces, int faceCount, int[] owner, List<int>[] memberFaces)
    {
        var boundaryOwner = IndexedMeshTools.CreateEdgeKeyMap<int>();
        for (int w = 0; w < memberFaces.Length; w++)
        {
            var uses = IndexedMeshTools.CreateEdgeKeyMap<int>(memberFaces[w].Count * 3);
            foreach (int f in memberFaces[w])
            {
                for (int k = 0; k < 3; k++)
                {
                    long edge = PackEdge(faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                    uses[edge] = uses.GetValueOrDefault(edge) + 1;
                }
            }

            foreach ((long edge, int count) in uses)
            {
                if (count == 1)
                    boundaryOwner[edge] = w;
            }
        }

        var result = new HashSet<EdgeKey>[memberFaces.Length];
        for (int w = 0; w < result.Length; w++)
            result[w] = new HashSet<EdgeKey>();
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f * 3 + k], b = faces[f * 3 + ((k + 1) % 3)];
                if (boundaryOwner.TryGetValue(PackEdge(a, b), out int w) && owner[f] != w)
                    result[w].Add(EdgeByCoordinates(v, a, b));
            }
        }

        return result;
    }

    private static bool Welds(HashSet<EdgeKey> patchBoundary, HashSet<EdgeKey> interfaceEdges) => patchBoundary.IsSupersetOf(interfaceEdges);

    private static EdgeKey EdgeByCoordinates(double[] v, int a, int b)
    {
        if (ComparePoints(v, a, b) > 0)
            (a, b) = (b, a);
        return new EdgeKey(v[a * 3], v[a * 3 + 1], v[a * 3 + 2], v[b * 3], v[b * 3 + 1], v[b * 3 + 2]);
    }

    /// <summary>An edge by its ends' coordinates, smaller end first.</summary>
    internal readonly record struct EdgeKey(double X0, double Y0, double Z0, double X1, double Y1, double Z1);

    /// <summary>
    /// The edges used by one face only, by coordinates. A graded window welds back exactly when its boundary
    /// is the one it was given: then it neither lost an edge it shares with the rest of the terrain nor
    /// added a face outside itself.
    /// </summary>
    private static HashSet<EdgeKey> BoundaryEdges(double[] v, int[] f, int faceCount)
    {
        var uses = IndexedMeshTools.CreateEdgeKeyMap<int>(faceCount * 3);
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                long edge = PackEdge(f[t * 3 + k], f[t * 3 + ((k + 1) % 3)]);
                uses[edge] = uses.GetValueOrDefault(edge) + 1;
            }
        }

        var result = new HashSet<EdgeKey>();
        foreach ((long edge, int count) in uses)
        {
            if (count != 1)
                continue;
            result.Add(EdgeByCoordinates(v, (int)(edge >> 32), (int)(edge & 0xFFFFFFFFL)));
        }

        return result;
    }

    /// <summary>
    /// The terrain with every window's faces replaced by its graded patch: the untouched faces in their
    /// order, then each window's patch in window order, welded at identical coordinates.
    /// </summary>
    private static Outcome Stitch(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        int[] owner,
        (GradingResult? Result, int[] Faces, WindowResult Graded)[] outputs,
        int reused)
    {
        var outV = new List<double>(vertexCount * 3);
        var outF = new List<int>(faceCount * 3);
        var index = new Dictionary<(double, double, double), int>(vertexCount);
        int Vertex(double x, double y, double z)
        {
            if (!index.TryGetValue((x, y, z), out int i))
            {
                i = outV.Count / 3;
                index[(x, y, z)] = i;
                outV.Add(x);
                outV.Add(y);
                outV.Add(z);
            }

            return i;
        }

        for (int f = 0; f < faceCount; f++)
        {
            if (owner[f] >= 0)
                continue;
            for (int k = 0; k < 3; k++)
            {
                int g = faces[f * 3 + k];
                outF.Add(Vertex(vertices[g * 3], vertices[g * 3 + 1], vertices[g * 3 + 2]));
            }
        }

        double cut = 0, fill = 0;
        var daylight = new List<double>();
        var polylines = new List<OutputPolyline>();
        var diagnostics = new List<string>();
        var structured = new List<GradingDiagnostic>();
        var patches = new List<GradingPatch>();
        var errors = new List<string>();
        var failurePolylines = new List<OutputPolyline>();
        var failureDiagnostics = new List<GradingDiagnostic>();
        foreach ((GradingResult? result, int[] windowFaces, WindowResult graded) in outputs)
        {
            if (windowFaces == null)
                continue;
            if (result == null)
            {
                // A failed window keeps its input faces, as a failed whole-mesh grade kept the whole terrain.
                if (!string.IsNullOrWhiteSpace(graded.Error))
                    errors.Add(graded.Error!);
                failurePolylines.AddRange(graded.FailureOutputPolylines);
                failureDiagnostics.AddRange(graded.FailureDiagnostics);
                foreach (int f in windowFaces)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int g = faces[f * 3 + k];
                        outF.Add(Vertex(vertices[g * 3], vertices[g * 3 + 1], vertices[g * 3 + 2]));
                    }
                }

                continue;
            }

            for (int t = 0; t < result.FaceCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int g = result.Faces[t * 3 + k];
                    outF.Add(Vertex(result.Vertices[g * 3], result.Vertices[g * 3 + 1], result.Vertices[g * 3 + 2]));
                }
            }

            cut += result.CutVolume;
            fill += result.FillVolume;
            daylight.AddRange(result.DaylightVertices.Take(result.DaylightVertexCount * 3));
            polylines.AddRange(result.OutputPolylines);
            diagnostics.AddRange(result.Diagnostics);
            structured.AddRange(result.StructuredDiagnostics);
            patches.AddRange(result.PatchSummaries);
        }

        bool anyGraded = outputs.Any(static o => o.Result != null);
        GradingResult? stitched = anyGraded
            ? new GradingResult(outV.ToArray(), outV.Count / 3, outF.ToArray(), outF.Count / 3, cut, fill, daylight.ToArray(), daylight.Count / 3,
                polylines, diagnostics, patches, structured)
            : null;
        return new Outcome
        {
            Result = stitched,
            Errors = errors,
            FailureOutputPolylines = failurePolylines,
            FailureDiagnostics = failureDiagnostics,
            WindowCount = outputs.Length,
            ReusedWindows = reused
        };
    }
}
