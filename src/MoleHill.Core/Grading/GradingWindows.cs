using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain window by window: each face within an item's reach (pads, paths) is that item's, items
/// sharing faces form one window, each window is graded alone, and the results are stitched back into the
/// untouched rest.
/// </summary>
/// <remarks>
/// Why (docs/incremental-rebuild-design-2026-09-29.md, "windowed graders"): a grader's changes stay within its
/// items' reach (measured: 13.7 m for a pad with a 15 m max distance), so the rest of the terrain passes
/// through. Grading a window alone makes that the definition rather than an observation, and it makes each
/// window's result a pure function of its key: the window's faces in canonical order plus whatever the caller
/// says its items depend on. A memo of window results is then exact, and an edit re-grades only the windows it
/// touches, whether the edit is to the terrain under them or to one item's settings.
///
/// A window's faces are those within its items' reach (<see cref="Reach"/>): near the item's plan shape, not
/// merely inside its bounding box, so a network of roads is a band and the land between them is left alone.
/// The edges a window shares with the rest must come back from grading as boundary edges, or the patch would
/// not weld: when one does not, the caller grades the whole terrain instead (<see cref="Outcome.NeedsWholeMesh"/>).
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

    /// <summary>
    /// An item's plan shape (flat XY) and how far past it the item can change the terrain. A face within
    /// <see cref="Radius"/> of the shape, or inside it when <see cref="Filled"/>, is the item's.
    /// </summary>
    public readonly record struct Reach(double[] Xy, int Count, bool Closed, bool Filled, double Radius)
    {
        public (double MinX, double MinY, double MaxX, double MaxY) Box(double grow)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                minX = Math.Min(minX, Xy[i * 2]); maxX = Math.Max(maxX, Xy[i * 2]);
                minY = Math.Min(minY, Xy[i * 2 + 1]); maxY = Math.Max(maxY, Xy[i * 2 + 1]);
            }

            return (minX - grow, minY - grow, maxX + grow, maxY + grow);
        }
    }

    public sealed class Memo
    {
        internal Dictionary<UInt128, WindowResult> Windows { get; } = new();

        /// <summary>Each window's two key halves by its items, to say why a window was re-graded.</summary>
        internal Dictionary<string, (UInt128 Faces, UInt128 Context)> Parts { get; } = new();

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

        /// <summary>Where the time went, phase by phase, for the stage's timing rows.</summary>
        public string Timings { get; init; } = string.Empty;
    }

    /// <summary>
    /// Grades every window. <paramref name="reach"/> is each item's shape and how far it can change the
    /// terrain; <paramref name="margin"/> is added beyond that so the faces along a window's edge are outside
    /// every item's reach.
    /// </summary>
    public static Outcome Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<Reach> reach,
        double margin,
        WindowGrader grade,
        WindowContext context,
        Memo? previous,
        Memo next)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phases = new System.Text.StringBuilder();
        void Phase(string name)
        {
            phases.Append(phases.Length > 0 ? ", " : string.Empty).Append(name).Append(' ').Append(clock.ElapsedMilliseconds).Append(" ms");
            clock.Restart();
        }

        int[] owner = AssignFaces(vertices, faces, faceCount, vertexCount, reach, margin, out var windows);
        Phase("assign");
        if (windows.Count == 0)
            return new Outcome { Result = null, WindowCount = 0, Timings = phases.ToString() };

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
        Phase("interfaces");
        int reused = 0;
        var keys = new UInt128[windows.Count];
        var parts = new (UInt128 Faces, UInt128 Context)[windows.Count];
        var locals = new (double[] V, int[] F)[windows.Count];
        Parallel.For(0, windows.Count, w =>
        {
            if (memberFaces[w].Count == 0)
                return;
            locals[w] = ExtractCanonical(vertices, faces, memberFaces[w]);
            parts[w] = WindowKey(locals[w].V, locals[w].F, windows[w], context);
            keys[w] = Combine(parts[w]);
        });
        Phase("extract+key");
        long gradeMs = 0, weldMs = 0;

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
                string items = string.Join(",", windows[w].Items);
                if (previous != null && previous.Parts.TryGetValue(items, out var before))
                {
                    string why = before.Faces != parts[w].Faces && before.Context != parts[w].Context ? "faces and context"
                        : before.Faces != parts[w].Faces ? "faces" : "context";
                    phases.Append($", window {w} re-graded: its {why} changed");
                }

                (double[] v, int[] f) = locals[w];
                var box = (windows[w].MinX, windows[w].MinY, windows[w].MaxX, windows[w].MaxY);
                long start = clock.ElapsedMilliseconds;
                GradingResult? result = grade(v, v.Length / 3, f, f.Length / 3, windows[w].Items, box, out string? error, out var failurePolylines, out var failureDiagnostics);
                long graderDone = clock.ElapsedMilliseconds;
                graded = new WindowResult(result, error, failurePolylines, failureDiagnostics,
                    result == null || Welds(BoundaryEdges(result.Vertices, result.Faces, result.FaceCount), interfaces[w]));
                gradeMs += graderDone - start;
                weldMs += clock.ElapsedMilliseconds - graderDone;
            }

            next.Windows[keys[w]] = graded;
            next.Parts[string.Join(",", windows[w].Items)] = parts[w];
            if (!graded.EdgesKept)
                return new Outcome { NeedsWholeMesh = true, WindowCount = windows.Count, ReusedWindows = reused, Timings = phases.ToString() };
            outputs[w] = (graded.Result, memberFaces[w].ToArray(), graded);
        }

        next.ReusedWindows = reused;
        next.GradedWindows = windows.Count - reused;
        clock.Restart();
        phases.Append($", grade {gradeMs} ms, weld check {weldMs} ms");
        Outcome stitched = Stitch(vertices, vertexCount, faces, faceCount, owner, outputs, reused);
        Phase("stitch");
        return new Outcome
        {
            Result = stitched.Result,
            Errors = stitched.Errors,
            FailureOutputPolylines = stitched.FailureOutputPolylines,
            FailureDiagnostics = stitched.FailureDiagnostics,
            WindowCount = stitched.WindowCount,
            ReusedWindows = stitched.ReusedWindows,
            Timings = phases.ToString()
        };
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

    /// <summary>
    /// Each item's reach grown by a margin of its own, so the faces along a window's edge lie clear of what the
    /// grader may touch: twice the longest plan extent of the faces within the item's reach, at least
    /// <paramref name="floor"/> and at most half the reach. It reads only faces within the reach, so an edit
    /// elsewhere cannot move a window. A margin from every face in the item's bounding box let one long
    /// sliver anywhere in a park-wide road network's box widen the band to the whole park.
    /// </summary>
    public static List<Reach> WithMargins(double[] v, int[] faces, int faceCount, IReadOnlyList<Reach> reach, double floor)
    {
        double[] longest = LongestFaceWithin(v, faces, faceCount, reach);
        var grown = new List<Reach>(reach.Count);
        for (int i = 0; i < reach.Count; i++)
        {
            double margin = Math.Max(floor, 2.0 * longest[i]);
            if (reach[i].Radius > 0)
                margin = Math.Min(margin, Math.Max(floor, 0.5 * reach[i].Radius));
            grown.Add(reach[i] with { Radius = reach[i].Radius + margin });
        }

        return grown;
    }

    /// <summary>Per item, the longest plan extent of the faces within its reach (0 when it reaches none).</summary>
    public static double[] LongestFaceWithin(double[] v, int[] faces, int faceCount, IReadOnlyList<Reach> reach)
    {
        ReachIndex index = ReachIndex.Build(reach, 0.0, faceCount);
        var longest = new double[reach.Count];
        if (!index.IsEmpty)
        {
            var gate = new object();
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, faceCount, 16384), range =>
            {
                var local = new double[reach.Count];
                var hits = new List<int>();
                for (int f = range.Item1; f < range.Item2; f++)
                {
                    int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
                    double cx = (v[a * 3] + v[b * 3] + v[c * 3]) / 3.0;
                    double cy = (v[a * 3 + 1] + v[b * 3 + 1] + v[c * 3 + 1]) / 3.0;
                    double rad = Math.Sqrt(Math.Max(Dist2(cx, cy, v[a * 3], v[a * 3 + 1]),
                        Math.Max(Dist2(cx, cy, v[b * 3], v[b * 3 + 1]), Dist2(cx, cy, v[c * 3], v[c * 3 + 1]))));
                    index.Hits(v[a * 3], v[a * 3 + 1], v[b * 3], v[b * 3 + 1], v[c * 3], v[c * 3 + 1], cx, cy, rad, hits);
                    if (hits.Count == 0)
                        continue;
                    (double minX, double minY, double maxX, double maxY) = FaceBox(v, faces, f);
                    double extent = Math.Max(maxX - minX, maxY - minY);
                    foreach (int item in hits)
                        local[item] = Math.Max(local[item], extent);
                }

                lock (gate)
                {
                    for (int i = 0; i < local.Length; i++)
                        longest[i] = Math.Max(longest[i], local[i]);
                }
            });
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

    /// <summary>
    /// The window of each face (or -1) and the windows, ordered by their lowest item. A face is an item's when
    /// it comes within the item's radius plus <paramref name="margin"/> of the item's plan shape (or lies
    /// inside a filled one); items sharing a face share a window. Pure in each face's coordinates and the
    /// items, so a face's window never depends on faces elsewhere.
    /// </summary>
    private static int[] AssignFaces(
        double[] v,
        int[] faces,
        int faceCount,
        int vertexCount,
        IReadOnlyList<Reach> reach,
        double margin,
        out List<(double MinX, double MinY, double MaxX, double MaxY, int[] Items)> windows)
    {
        int n = reach.Count;
        var itemOf = new int[faceCount];
        Array.Fill(itemOf, -1);
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        ReachIndex index = ReachIndex.Build(reach, margin, faceCount);
        if (!index.IsEmpty)
        {
            var pairs = new List<long>();
            var gate = new object();
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, faceCount, 16384), range =>
            {
                var local = IndexedMeshTools.CreateEdgeKeySet();
                var hits = new List<int>();
                for (int f = range.Item1; f < range.Item2; f++)
                {
                    int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
                    double cx = (v[a * 3] + v[b * 3] + v[c * 3]) / 3.0;
                    double cy = (v[a * 3 + 1] + v[b * 3 + 1] + v[c * 3 + 1]) / 3.0;
                    double rad = Math.Sqrt(Math.Max(Dist2(cx, cy, v[a * 3], v[a * 3 + 1]),
                        Math.Max(Dist2(cx, cy, v[b * 3], v[b * 3 + 1]), Dist2(cx, cy, v[c * 3], v[c * 3 + 1]))));
                    index.Hits(v[a * 3], v[a * 3 + 1], v[b * 3], v[b * 3 + 1], v[c * 3], v[c * 3 + 1], cx, cy, rad, hits);
                    if (hits.Count == 0)
                        continue;
                    int first = hits.Min();
                    foreach (int item in hits)
                    {
                        if (item != first)
                            local.Add(PackEdge(first, item));
                    }

                    itemOf[f] = first;
                }

                if (local.Count > 0)
                {
                    lock (gate)
                        pairs.AddRange(local);
                }
            });

            foreach (long pair in pairs)
                Union((int)(pair >> 32), (int)(pair & 0xFFFFFFFFL));
        }

        // A pocket of faces no item reached, closed off by windows, is the window's: grading would fill it
        // with faces the rest of the terrain still has.
        ClaimPockets(faces, faceCount, vertexCount, itemOf, Find, Union);

        var windowOf = new int[n];
        Array.Fill(windowOf, -1);
        windows = new List<(double, double, double, double, int[])>();
        var members = new List<List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (windowOf[root] < 0)
            {
                windowOf[root] = members.Count;
                members.Add(new List<int>());
            }

            members[windowOf[root]].Add(i);
        }

        foreach (List<int> items in members)
        {
            var box = reach[items[0]].Box(reach[items[0]].Radius + margin);
            foreach (int i in items)
            {
                var r = reach[i].Box(reach[i].Radius + margin);
                box = (Math.Min(box.MinX, r.MinX), Math.Min(box.MinY, r.MinY), Math.Max(box.MaxX, r.MaxX), Math.Max(box.MaxY, r.MaxY));
            }

            windows.Add((box.MinX, box.MinY, box.MaxX, box.MaxY, items.ToArray()));
        }

        var owner = new int[faceCount];
        for (int f = 0; f < faceCount; f++)
            owner[f] = itemOf[f] < 0 ? -1 : windowOf[Find(itemOf[f])];
        return owner;
    }

    private const int PocketFaceLimit = 64;

    /// <summary>
    /// Gives every small pocket of unreached faces beside windows to them: a flood across edges through
    /// unreached faces that stops within <see cref="PocketFaceLimit"/> faces is a pocket; the windows around it
    /// are merged and own it. That includes a sliver along the terrain border: it gives the window an extra
    /// boundary loop, and the explicit Grade Path batter failed to weld with three such slivers 14 m away.
    /// Larger unreached regions (the land between the roads of a network) stay out, and if grading fills one
    /// anyway, the weld check catches it.
    /// </summary>
    private static void ClaimPockets(int[] faces, int faceCount, int vertexCount, int[] itemOf, Func<int, int> find, Action<int, int> union)
    {
        bool any = false;
        for (int f = 0; f < faceCount && !any; f++)
            any = itemOf[f] >= 0;
        if (!any)
            return;

        // Vertex -> faces, CSR.
        var start = new int[vertexCount + 1];
        for (int i = 0; i < faceCount * 3; i++)
            start[faces[i] + 1]++;
        for (int i = 0; i < vertexCount; i++)
            start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var incident = new int[faceCount * 3];
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
                incident[fill[faces[f * 3 + k]]++] = f;
        }

        // The face across edge (a, b) from f, or -1 on the border.
        int Across(int f, int a, int b)
        {
            for (int s = start[a]; s < start[a + 1]; s++)
            {
                int g = incident[s];
                if (g == f)
                    continue;
                if (faces[g * 3] == b || faces[g * 3 + 1] == b || faces[g * 3 + 2] == b)
                    return g;
            }

            return -1;
        }

        // The flood that reached each face, 0 for none. An unowned face an earlier flood reached belongs to a
        // region that flood found too large or open, so meeting one ends this flood as well.
        var flood = new int[faceCount];
        int floodId = 0;
        var queue = new List<int>();
        var bounding = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            if (itemOf[f] < 0)
                continue;
            for (int k = 0; k < 3; k++)
            {
                int seed = Across(f, faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                if (seed < 0 || itemOf[seed] >= 0 || flood[seed] != 0)
                    continue;

                floodId++;
                queue.Clear();
                bounding.Clear();
                queue.Add(seed);
                flood[seed] = floodId;
                bool pocket = true;
                for (int q = 0; q < queue.Count && pocket; q++)
                {
                    int g = queue[q];
                    for (int e = 0; e < 3 && pocket; e++)
                    {
                        int h = Across(g, faces[g * 3 + e], faces[g * 3 + ((e + 1) % 3)]);
                        if (h < 0)
                            continue;
                        if (itemOf[h] >= 0)
                            bounding.Add(itemOf[h]);
                        else if (flood[h] == 0)
                        {
                            if (queue.Count >= PocketFaceLimit)
                                pocket = false;
                            flood[h] = floodId;
                            queue.Add(h);
                        }
                        else if (flood[h] != floodId)
                            pocket = false;
                    }
                }

                if (!pocket)
                    continue;
                int owner = bounding[0];
                foreach (int item in bounding)
                    union(owner, item);
                foreach (int g in queue)
                    itemOf[g] = owner;
            }
        }

        // A window's rim must be a set of simple loops. Faces chosen by distance can touch at a single rim vertex
        // (a pinch), and a grader welding its patch back sees open boundary chains there and defers to a softer
        // tier: the explicit Grade Path batter was lost that way next to a retaining wall. A pinched vertex's
        // whole fan joins the window, round after round, until no rim vertex is pinched.
        var candidates = new List<int>();
        var marked = new bool[vertexCount];
        void MarkRim(int f)
        {
            int group = find(itemOf[f]);
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f * 3 + k], b = faces[f * 3 + ((k + 1) % 3)];
                int h = Across(f, a, b);
                if (h >= 0 && itemOf[h] >= 0 && find(itemOf[h]) == group)
                    continue;
                foreach (int q in new[] { a, b })
                {
                    if (!marked[q])
                    {
                        marked[q] = true;
                        candidates.Add(q);
                    }
                }
            }
        }

        for (int f = 0; f < faceCount; f++)
        {
            if (itemOf[f] >= 0)
                MarkRim(f);
        }

        var groups = new List<int>();
        for (int round = 0; round < 32 && candidates.Count > 0; round++)
        {
            int[] vertices = candidates.ToArray();
            Array.Sort(vertices);
            candidates.Clear();
            foreach (int q in vertices)
                marked[q] = false;

            var claimed = new List<int>();
            foreach (int q in vertices)
            {
                groups.Clear();
                for (int s = start[q]; s < start[q + 1]; s++)
                {
                    int f = incident[s];
                    if (itemOf[f] >= 0 && !groups.Contains(find(itemOf[f])))
                        groups.Add(find(itemOf[f]));
                }

                foreach (int group in groups)
                {
                    int rimEdges = 0;
                    for (int s = start[q]; s < start[q + 1]; s++)
                    {
                        int f = incident[s];
                        if (itemOf[f] < 0 || find(itemOf[f]) != group)
                            continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int a = faces[f * 3 + k], b = faces[f * 3 + ((k + 1) % 3)];
                            if (a != q && b != q)
                                continue;
                            int h = Across(f, a, b);
                            if (h < 0 || itemOf[h] < 0 || find(itemOf[h]) != group)
                                rimEdges++;
                        }
                    }

                    if (rimEdges <= 2)
                        continue;
                    for (int s = start[q]; s < start[q + 1]; s++)
                    {
                        int f = incident[s];
                        if (itemOf[f] < 0)
                        {
                            itemOf[f] = group;
                            claimed.Add(f);
                        }
                        else if (find(itemOf[f]) != find(group))
                        {
                            union(group, itemOf[f]);
                            claimed.Add(f);
                        }
                    }

                    break;
                }
            }

            foreach (int f in claimed)
            {
                for (int k = 0; k < 3; k++)
                {
                    int q = faces[f * 3 + k];
                    for (int s = start[q]; s < start[q + 1]; s++)
                    {
                        if (itemOf[incident[s]] >= 0)
                            MarkRim(incident[s]);
                    }
                }
            }
        }
    }

    private static double Dist2(double ax, double ay, double bx, double by) => ((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by));

    /// <summary>
    /// Every item's shape as segments (each carrying its item's radius plus the margin) and filled polygons,
    /// bucketed on a uniform grid, so a face asks only the segments near it which items reach it.
    /// </summary>
    private sealed class ReachIndex
    {
        private const int MaxCells = 1 << 22;
        private const int MinCells = 1 << 12;

        private readonly double[] _segments;   // x0, y0, x1, y1, r per segment
        private readonly int[] _segmentItem;
        private readonly (int Item, PreparedPolygon Polygon, double MinX, double MinY, double MaxX, double MaxY)[] _filled;
        private readonly double _minX, _minY, _cell;
        private readonly int _nx, _ny;
        private readonly int[] _segStart = Array.Empty<int>(), _segItems = Array.Empty<int>();
        private readonly int[] _fillStart = Array.Empty<int>(), _fillItems = Array.Empty<int>();

        public bool IsEmpty => _segmentItem.Length == 0;

        private ReachIndex(double[] segments, int[] segmentItem,
            (int, PreparedPolygon, double, double, double, double)[] filled,
            double minX, double minY, double cell, int nx, int ny)
        {
            _segments = segments;
            _segmentItem = segmentItem;
            _filled = filled;
            _minX = minX;
            _minY = minY;
            _cell = cell;
            _nx = nx;
            _ny = ny;
            if (segmentItem.Length == 0)
                return;

            (_segStart, _segItems) = Bucket(segmentItem.Length, s =>
            {
                double r = segments[s * 5 + 4];
                return (Math.Min(segments[s * 5], segments[s * 5 + 2]) - r, Math.Min(segments[s * 5 + 1], segments[s * 5 + 3]) - r,
                        Math.Max(segments[s * 5], segments[s * 5 + 2]) + r, Math.Max(segments[s * 5 + 1], segments[s * 5 + 3]) + r);
            });
            (_fillStart, _fillItems) = Bucket(filled.Length, k => (filled[k].Item3, filled[k].Item4, filled[k].Item5, filled[k].Item6));
        }

        /// <summary>
        /// The grid has at most about one cell per face (and never more than <see cref="MaxCells"/>): its cells are
        /// allocated and swept whole, and a narrow reach over a small terrain otherwise asked for millions of
        /// empty ones, which cost a 2,700-face terrain 17 ms per rail insertion.
        /// </summary>
        public static ReachIndex Build(IReadOnlyList<Reach> reach, double margin, int faceCount)
        {
            var segments = new List<double>();
            var segmentItem = new List<int>();
            var filled = new List<(int, PreparedPolygon, double, double, double, double)>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, cell = 0.0;
            for (int i = 0; i < reach.Count; i++)
            {
                Reach item = reach[i];
                int n = item.Count;
                if (n == 0)
                    continue;
                double r = Math.Max(0.0, item.Radius) + margin;
                cell = Math.Max(cell, r);
                int count = n == 1 ? 1 : (item.Closed && n > 2 ? n : n - 1);
                for (int s = 0; s < count; s++)
                {
                    int a = s, b = n == 1 ? 0 : (s + 1) % n;
                    segments.Add(item.Xy[a * 2]); segments.Add(item.Xy[a * 2 + 1]);
                    segments.Add(item.Xy[b * 2]); segments.Add(item.Xy[b * 2 + 1]);
                    segments.Add(r);
                    segmentItem.Add(i);
                }

                var box = item.Box(r);
                minX = Math.Min(minX, box.MinX); minY = Math.Min(minY, box.MinY);
                maxX = Math.Max(maxX, box.MaxX); maxY = Math.Max(maxY, box.MaxY);
                if (item.Filled && n >= 3 && PreparedPolygon.TryCreate(item.Xy, n) is PreparedPolygon polygon)
                {
                    var inner = item.Box(0.0);
                    filled.Add((i, polygon, inner.MinX, inner.MinY, inner.MaxX, inner.MaxY));
                }
            }

            if (segmentItem.Count == 0)
                return new ReachIndex(Array.Empty<double>(), Array.Empty<int>(), Array.Empty<(int, PreparedPolygon, double, double, double, double)>(), 0, 0, 1, 0, 0);

            cell = Math.Max(cell, 1e-9);
            int nx, ny;
            while (true)
            {
                nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cell));
                ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cell));
                if ((long)nx * ny <= Math.Clamp(faceCount, MinCells, MaxCells))
                    break;
                cell *= 2.0;
            }

            return new ReachIndex(segments.ToArray(), segmentItem.ToArray(), filled.ToArray(), minX, minY, cell, nx, ny);
        }

        /// <summary>
        /// The items reaching the plan triangle (<paramref name="ax"/>, <paramref name="ay"/>) ...
        /// (<paramref name="qx"/>, <paramref name="qy"/>), whose centroid is (<paramref name="cx"/>,
        /// <paramref name="cy"/>) and circumradius about it <paramref name="rad"/>; distinct, in no particular
        /// order. The distance is the triangle's own, not its bounding circle's: a long sliver along the terrain
        /// border was taken in 23 m from a road with a 21.6 m reach, as a window piece joined to nothing.
        /// </summary>
        public void Hits(double ax, double ay, double bx, double by, double qx, double qy, double cx, double cy, double rad, List<int> hits)
        {
            hits.Clear();
            (int x0, int y0, int x1, int y1) = Cells(cx - rad, cy - rad, cx + rad, cy + rad);
            for (int gy = y0; gy <= y1; gy++)
            {
                for (int gx = x0; gx <= x1; gx++)
                {
                    int cellIndex = gy * _nx + gx;
                    for (int k = _segStart[cellIndex]; k < _segStart[cellIndex + 1]; k++)
                    {
                        int s = _segItems[k];
                        int item = _segmentItem[s];
                        if (hits.Contains(item))
                            continue;
                        double r = _segments[s * 5 + 4];
                        if (SegmentDist2(cx, cy, s) > (r + rad) * (r + rad))
                            continue;
                        if (TriangleDist2(ax, ay, bx, by, qx, qy, s) <= r * r)
                            hits.Add(item);
                    }

                    for (int k = _fillStart[cellIndex]; k < _fillStart[cellIndex + 1]; k++)
                    {
                        var entry = _filled[_fillItems[k]];
                        if (hits.Contains(entry.Item) || cx < entry.MinX || cx > entry.MaxX || cy < entry.MinY || cy > entry.MaxY)
                            continue;
                        if (entry.Polygon.Contains(cx, cy))
                            hits.Add(entry.Item);
                    }
                }
            }
        }

        /// <summary>Squared plan distance between triangle (a, b, q) and segment <paramref name="s"/>.</summary>
        private double TriangleDist2(double ax, double ay, double bx, double by, double qx, double qy, int s)
        {
            double sx = _segments[s * 5], sy = _segments[s * 5 + 1], ex = _segments[s * 5 + 2], ey = _segments[s * 5 + 3];
            if (InTriangle(sx, sy, ax, ay, bx, by, qx, qy) || InTriangle(ex, ey, ax, ay, bx, by, qx, qy))
                return 0.0;
            return Math.Min(SegmentSegmentDist2(ax, ay, bx, by, sx, sy, ex, ey),
                Math.Min(SegmentSegmentDist2(bx, by, qx, qy, sx, sy, ex, ey), SegmentSegmentDist2(qx, qy, ax, ay, sx, sy, ex, ey)));
        }

        private static bool InTriangle(double px, double py, double ax, double ay, double bx, double by, double qx, double qy)
        {
            double d1 = Cross(ax, ay, bx, by, px, py), d2 = Cross(bx, by, qx, qy, px, py), d3 = Cross(qx, qy, ax, ay, px, py);
            bool negative = d1 < 0 || d2 < 0 || d3 < 0;
            bool positive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(negative && positive);
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
            ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

        private static double SegmentSegmentDist2(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double d1 = Cross(cx, cy, dx, dy, ax, ay), d2 = Cross(cx, cy, dx, dy, bx, by);
            double d3 = Cross(ax, ay, bx, by, cx, cy), d4 = Cross(ax, ay, bx, by, dx, dy);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return 0.0;
            return Math.Min(Math.Min(PointSegmentDist2(ax, ay, cx, cy, dx, dy), PointSegmentDist2(bx, by, cx, cy, dx, dy)),
                Math.Min(PointSegmentDist2(cx, cy, ax, ay, bx, by), PointSegmentDist2(dx, dy, ax, ay, bx, by)));
        }

        private static double PointSegmentDist2(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 > 0.0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
            return Dist2(px, py, ax + (t * dx), ay + (t * dy));
        }

        private double SegmentDist2(double px, double py, int s)
        {
            double ax = _segments[s * 5], ay = _segments[s * 5 + 1], bx = _segments[s * 5 + 2], by = _segments[s * 5 + 3];
            double dx = bx - ax, dy = by - ay;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 > 0.0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
            return Dist2(px, py, ax + (t * dx), ay + (t * dy));
        }

        private (int X0, int Y0, int X1, int Y1) Cells(double minX, double minY, double maxX, double maxY) =>
            (Math.Clamp((int)Math.Floor((minX - _minX) / _cell), 0, _nx - 1),
             Math.Clamp((int)Math.Floor((minY - _minY) / _cell), 0, _ny - 1),
             Math.Clamp((int)Math.Floor((maxX - _minX) / _cell), 0, _nx - 1),
             Math.Clamp((int)Math.Floor((maxY - _minY) / _cell), 0, _ny - 1));

        /// <summary>Count-then-fill CSR of entries by the cells their boxes cover.</summary>
        private (int[] Start, int[] Items) Bucket(int count, Func<int, (double MinX, double MinY, double MaxX, double MaxY)> boxOf)
        {
            var start = new int[(_nx * _ny) + 1];
            for (int e = 0; e < count; e++)
            {
                var box = boxOf(e);
                (int x0, int y0, int x1, int y1) = Cells(box.MinX, box.MinY, box.MaxX, box.MaxY);
                for (int gy = y0; gy <= y1; gy++)
                {
                    for (int gx = x0; gx <= x1; gx++)
                        start[gy * _nx + gx + 1]++;
                }
            }

            for (int c = 0; c < _nx * _ny; c++)
                start[c + 1] += start[c];
            var fill = (int[])start.Clone();
            var items = new int[start[_nx * _ny]];
            for (int e = 0; e < count; e++)
            {
                var box = boxOf(e);
                (int x0, int y0, int x1, int y1) = Cells(box.MinX, box.MinY, box.MaxX, box.MaxY);
                for (int gy = y0; gy <= y1; gy++)
                {
                    for (int gx = x0; gx <= x1; gx++)
                        items[fill[gy * _nx + gx]++] = e;
                }
            }

            return (start, items);
        }
    }

    private static long PackEdge(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

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

    /// <summary>The window's key in two halves: its faces, and everything else its result depends on.</summary>
    private static (UInt128 Faces, UInt128 Context) WindowKey(double[] v, int[] f, (double MinX, double MinY, double MaxX, double MaxY, int[] Items) window, WindowContext context)
    {
        var low = new XxHash64Builder(0x6a770001UL);
        var high = new XxHash64Builder(0x6a770002UL);
        ReadOnlySpan<byte> vb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan());
        ReadOnlySpan<byte> fb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(f.AsSpan());
        low.AddBytes(vb); high.AddBytes(vb);
        low.AddBytes(fb); high.AddBytes(fb);
        var faces = new UInt128(high.ToUInt64(), low.ToUInt64());

        low = new XxHash64Builder(0x6a770003UL);
        high = new XxHash64Builder(0x6a770004UL);
        foreach (double d in new[] { window.MinX, window.MinY, window.MaxX, window.MaxY })
        {
            low.Add(d);
            high.Add(d);
        }

        context(window.Items, window.MinX, window.MinY, window.MaxX, window.MaxY, low, high);
        return (faces, new UInt128(high.ToUInt64(), low.ToUInt64()));
    }

    private static UInt128 Combine((UInt128 Faces, UInt128 Context) parts)
    {
        var low = new XxHash64Builder(0x6a770005UL);
        var high = new XxHash64Builder(0x6a770006UL);
        foreach (UInt128 part in new[] { parts.Faces, parts.Context })
        {
            low.Add((ulong)(part >> 64)); low.Add((ulong)part);
            high.Add((ulong)(part >> 64)); high.Add((ulong)part);
        }

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

        // Only a face with two corners on some window's boundary can hold one of its edges, so the scan
        // over the whole terrain is three array reads per face rather than three dictionary lookups (5 of a
        // 33 ms wall insertion on 100k faces).
        var onBoundary = new bool[v.Length / 3];
        foreach (long edge in boundaryOwner.Keys)
        {
            onBoundary[(int)(edge >> 32)] = true;
            onBoundary[(int)(edge & 0xFFFFFFFFL)] = true;
        }

        for (int f = 0; f < faceCount; f++)
        {
            int c0 = faces[f * 3], c1 = faces[f * 3 + 1], c2 = faces[f * 3 + 2];
            if ((onBoundary[c0] ? 1 : 0) + (onBoundary[c1] ? 1 : 0) + (onBoundary[c2] ? 1 : 0) < 2)
                continue;
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f * 3 + k], b = faces[f * 3 + ((k + 1) % 3)];
                if (onBoundary[a] && onBoundary[b] && boundaryOwner.TryGetValue(PackEdge(a, b), out int w) && owner[f] != w)
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
    /// <remarks>
    /// The input is a normalized mesh, so each coordinate has one index, and a patch can only weld onto a
    /// vertex of a window face (its boundary is the window's). So only those vertices, and the patches' own,
    /// are welded through the coordinate dictionary; every other vertex keeps its index in first-use order,
    /// which is the order the dictionary would have given it. Welding every corner of the terrain through the
    /// dictionary was 7 of a 33 ms wall insertion on 100k faces.
    /// </remarks>
    internal static Outcome Stitch(
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
        var index = new Dictionary<(double, double, double), int>();
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

        var nearWindow = new bool[vertexCount];
        foreach ((_, int[] windowFaces, _) in outputs)
        {
            if (windowFaces == null)
                continue;
            foreach (int f in windowFaces)
            {
                nearWindow[faces[f * 3]] = true;
                nearWindow[faces[f * 3 + 1]] = true;
                nearWindow[faces[f * 3 + 2]] = true;
            }
        }

        var remap = new int[vertexCount];
        Array.Fill(remap, -1);
        int Input(int g)
        {
            int i = remap[g];
            if (i >= 0)
                return i;
            if (nearWindow[g])
            {
                i = Vertex(vertices[g * 3], vertices[g * 3 + 1], vertices[g * 3 + 2]);
            }
            else
            {
                i = outV.Count / 3;
                outV.Add(vertices[g * 3]);
                outV.Add(vertices[g * 3 + 1]);
                outV.Add(vertices[g * 3 + 2]);
            }

            remap[g] = i;
            return i;
        }

        for (int f = 0; f < faceCount; f++)
        {
            if (owner[f] >= 0)
                continue;
            outF.Add(Input(faces[f * 3]));
            outF.Add(Input(faces[f * 3 + 1]));
            outF.Add(Input(faces[f * 3 + 2]));
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
                    outF.Add(Input(faces[f * 3]));
                    outF.Add(Input(faces[f * 3 + 1]));
                    outF.Add(Input(faces[f * 3 + 2]));
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
