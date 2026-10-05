namespace MoleHill.Core.Analysis;

/// <summary>
/// Elevation contours kept per face, so a mesh whose Z changes locally can be re-contoured locally.
/// <see cref="ContourGenerator"/> traces the whole mesh and stitches polylines, which is right for a build
/// and wrong for a sculpt brush: on a 540k-face terrain the full pass is ~29 ms, while the faces a dab
/// touches re-trace in a millisecond or two. Segments are left unstitched — a viewport draws line
/// segments just as well — and each triangle is traced with the generator's own crossing rule, so the
/// live lines coincide with the built ones.
/// </summary>
public sealed class IncrementalContourTracer
{
    private readonly double[] _levels;
    private readonly int[] _faces;
    private readonly ContourSegment[]?[] _byFace;
    private readonly HashSet<int> _facesWithSegments = new();
    private readonly List<ContourSegment> _scratch = new();

    public IncrementalContourTracer(double[] vertices, int[] faces, int faceCount, IReadOnlyList<double> levels)
    {
        _levels = levels.Distinct().OrderBy(level => level).ToArray();
        _faces = faces;
        _byFace = new ContourSegment[]?[faceCount];
        for (int f = 0; f < faceCount; f++)
            Trace(vertices, f);
        Version = 1;
    }

    /// <summary>The sorted levels; <see cref="ContourSegment.Level"/> indexes this list.</summary>
    public IReadOnlyList<double> Levels => _levels;

    /// <summary>Bumped by every change, so a consumer can rebuild its draw buffers only when needed.</summary>
    public int Version { get; private set; }

    /// <summary>Re-traces the given faces against the current <paramref name="vertices"/>.</summary>
    public void Update(double[] vertices, IEnumerable<int> faces)
    {
        foreach (int f in faces)
            Trace(vertices, f);
        Version++;
    }

    /// <summary>Every current segment, in no particular order.</summary>
    public IEnumerable<ContourSegment> Segments()
    {
        foreach (int f in _facesWithSegments)
        {
            foreach (ContourSegment segment in _byFace[f]!)
                yield return segment;
        }
    }

    private void Trace(double[] v, int f)
    {
        _scratch.Clear();
        int ia = _faces[f * 3] * 3, ib = _faces[f * 3 + 1] * 3, ic = _faces[f * 3 + 2] * 3;
        double ax = v[ia], ay = v[ia + 1], az = v[ia + 2];
        double bx = v[ib], by = v[ib + 1], bz = v[ib + 2];
        double cx = v[ic], cy = v[ic + 1], cz = v[ic + 2];
        double vmin = Math.Min(az, Math.Min(bz, cz));
        double vmax = Math.Max(az, Math.Max(bz, cz));

        if (vmax - vmin > 0.0)
        {
            Span<double> pts = stackalloc double[6];
            for (int li = ContourGenerator.UpperBound(_levels, vmin); li < _levels.Length && _levels[li] <= vmax; li++)
            {
                double level = _levels[li];
                int found = 0;
                ContourGenerator.AddCrossing(ax, ay, az, az, bx, by, bz, bz, level, pts, ref found);
                ContourGenerator.AddCrossing(bx, by, bz, bz, cx, cy, cz, cz, level, pts, ref found);
                if (found < 2)
                    ContourGenerator.AddCrossing(cx, cy, cz, cz, ax, ay, az, az, level, pts, ref found);
                if (found == 2)
                    _scratch.Add(new ContourSegment(li, pts[0], pts[1], pts[2], pts[3], pts[4], pts[5]));
            }
        }

        if (_scratch.Count == 0)
        {
            _byFace[f] = null;
            _facesWithSegments.Remove(f);
            return;
        }

        _byFace[f] = _scratch.ToArray();
        _facesWithSegments.Add(f);
    }
}
