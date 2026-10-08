using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>
/// A plan-view face index over a triangle mesh, answering the two questions a terrain section asks: where
/// does a vertical plane through a plan segment cut the mesh, and what is the top surface height at a plan
/// point. Built once per mesh and then queried for every section line, station and comparison profile.
/// </summary>
/// <remarks>
/// <para>
/// Sections used to cut with Rhino's <c>Intersection.MeshPlane</c>, which intersects the whole infinite
/// plane with every face of the mesh, and then threw away everything outside the short section line — once
/// per cut segment, per station, per terrain, and again for the cut/fill reference. On a large terrain with
/// a hundred cross-sections that was the entire build. Here a cut visits only the faces in the grid cells
/// the segment crosses.
/// </para>
/// <para>
/// A vertex lying exactly on the cut plane is the normal case, not a freak one: grading drops vertices on
/// round coordinates and section lines are drawn on round coordinates too. <c>MeshPlane</c> broke the
/// profile into disjoint runs there. This index classifies a vertex on the plane as being on the positive
/// side (simulation of simplicity), so every face is either uncut or crossed by exactly two of its edges,
/// and the crossings chain through shared edges without a gap. Vertices are welded by exact position
/// first (unless the caller says the mesh already is), so an unwelded seam chains through as well.
/// </para>
/// <para>Immutable after construction and safe to query from several threads.</para>
/// </remarks>
internal sealed class MeshSectionIndex
{
    private readonly double[] _vertices;
    private readonly int[] _faces;
    private readonly int _faceCount;
    private readonly double _minX;
    private readonly double _minY;
    private readonly double _maxX;
    private readonly double _maxY;
    private readonly double _invCell;
    private readonly int _columns;
    private readonly int _rows;

    // Dense CSR grid: the faces whose plan bounding box covers cell (cx, cy) are
    // _cellItems[_cellStart[c] .. _cellStart[c + 1]), c = cy * _columns + cx, ascending.
    private readonly int[] _cellStart;
    private readonly int[] _cellItems;

    /// <param name="weldCoincidentVertices">
    /// Weld vertices at exactly the same position before indexing, so a cut chains across an unwelded seam.
    /// A mesh from <c>RhinoGeometryConversions.TryExtractMesh</c> is welded already (it combines identical
    /// vertices), and skipping the weld there saves a hash of every vertex.
    /// </param>
    public MeshSectionIndex(IndexedTriMesh mesh, bool weldCoincidentVertices = true)
    {
        _vertices = mesh.Vertices;
        (_faces, _faceCount) = WeldFaces(mesh, weldCoincidentVertices);

        if (_faceCount == 0)
        {
            _invCell = 1.0;
            _columns = _rows = 1;
            _cellStart = new int[2];
            _cellItems = Array.Empty<int>();
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < _faceCount * 3; i++)
        {
            int v = _faces[i] * 3;
            minX = Math.Min(minX, _vertices[v]);
            maxX = Math.Max(maxX, _vertices[v]);
            minY = Math.Min(minY, _vertices[v + 1]);
            maxY = Math.Max(maxY, _vertices[v + 1]);
        }

        double spanX = maxX - minX;
        double spanY = maxY - minY;
        double span = Math.Max(spanX, spanY);

        // About four faces per cell, as the height projector sizes its grid, from the plan area so a long
        // thin terrain gets square cells rather than a few very long ones.
        double area = Math.Max(spanX, span * 1e-3) * Math.Max(spanY, span * 1e-3);
        double cellSize = ScaleAwareTolerance.ResolveLength(Math.Sqrt(area * 4.0 / _faceCount), span);
        _minX = minX;
        _minY = minY;
        _maxX = maxX;
        _maxY = maxY;
        _invCell = 1.0 / cellSize;
        _columns = Math.Clamp((int)Math.Floor(spanX * _invCell) + 1, 1, 1 << 14);
        _rows = Math.Clamp((int)Math.Floor(spanY * _invCell) + 1, 1, 1 << 14);
        (_cellStart, _cellItems) = BuildCells();
    }

    public int FaceCount => _faceCount;

    /// <summary>
    /// The cut of the vertical plane through plan segment <c>a→b</c>, as polylines of world points
    /// (<c>[x0, y0, z0, x1, ...]</c>). Each polyline runs in the a→b direction. Only faces whose cut lies
    /// within <paramref name="margin"/> of the segment's station range are visited; a polyline may run up
    /// to one face beyond either end, so callers clip to the segment.
    /// </summary>
    public List<double[]> SliceSegment(double ax, double ay, double bx, double by, double margin)
    {
        var result = new List<double[]>();
        double dx = bx - ax;
        double dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (_faceCount == 0 || !(length > 0.0) || !double.IsFinite(length))
            return result;

        margin = Math.Max(margin, 0.0);
        double ux = dx / length;
        double uy = dy / length;

        // Signed distance is measured along the plan normal (-uy, ux); station along (ux, uy).
        List<int> candidates = CollectCandidateFaces(ax - (ux * margin), ay - (uy * margin), bx + (ux * margin), by + (uy * margin), margin);
        if (candidates.Count == 0)
            return result;

        var pieces = new List<Piece>(candidates.Count / 2);
        Span<double> distance = stackalloc double[3];
        Span<int> corner = stackalloc int[3];
        foreach (int face in candidates)
        {
            int positive = 0;
            for (int c = 0; c < 3; c++)
            {
                int v = _faces[(face * 3) + c];
                corner[c] = v;
                distance[c] = ((_vertices[(v * 3) + 1] - ay) * ux) - ((_vertices[v * 3] - ax) * uy);
                if (distance[c] >= 0.0)
                    positive++;
            }

            if (positive == 0 || positive == 3)
                continue;

            // Exactly two edges change side. The lone vertex is the one whose side differs from both others.
            int lone = 0;
            for (int c = 0; c < 3; c++)
            {
                bool side = distance[c] >= 0.0;
                if (side != (distance[(c + 1) % 3] >= 0.0) && side != (distance[(c + 2) % 3] >= 0.0))
                {
                    lone = c;
                    break;
                }
            }

            int other1 = (lone + 1) % 3;
            int other2 = (lone + 2) % 3;
            Crossing p = Cross(corner[lone], distance[lone], corner[other1], distance[other1]);
            Crossing q = Cross(corner[lone], distance[lone], corner[other2], distance[other2]);

            double sp = ((p.X - ax) * ux) + ((p.Y - ay) * uy);
            double sq = ((q.X - ax) * ux) + ((q.Y - ay) * uy);
            if ((sp < -margin && sq < -margin) || (sp > length + margin && sq > length + margin))
                continue;

            pieces.Add(new Piece(p, q));
        }

        ChainPieces(pieces, ax, ay, ux, uy, result);
        return result;
    }

    /// <summary>
    /// The highest point of the mesh above (<paramref name="x"/>, <paramref name="y"/>) — what a ray cast
    /// straight down from above the mesh hits first. False when the point is outside the mesh's plan.
    /// </summary>
    public bool TryGetTopZ(double x, double y, out double z)
    {
        z = double.NegativeInfinity;
        if (_faceCount == 0)
            return false;

        if (x < _minX || y < _minY || x > _maxX || y > _maxY)
            return false;

        // Clamped as faces were when they were indexed: past 1 << 14 columns the far cells share the last one.
        int cx = Math.Min((int)Math.Floor((x - _minX) * _invCell), _columns - 1);
        int cy = Math.Min((int)Math.Floor((y - _minY) * _invCell), _rows - 1);

        int cell = (cy * _columns) + cx;
        for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
        {
            int f = _cellItems[k] * 3;
            int i0 = _faces[f] * 3, i1 = _faces[f + 1] * 3, i2 = _faces[f + 2] * 3;
            double x0 = _vertices[i0], y0 = _vertices[i0 + 1];
            double x1 = _vertices[i1], y1 = _vertices[i1 + 1];
            double x2 = _vertices[i2], y2 = _vertices[i2 + 1];
            double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));

            // A face seen edge-on from above (a vertical wall) has no height to give; its neighbours do.
            double scale = Math.Max(Math.Abs(x1 - x0) + Math.Abs(y1 - y0), Math.Abs(x2 - x0) + Math.Abs(y2 - y0));
            if (!(Math.Abs(denom) > scale * scale * 1e-12))
                continue;

            double w0 = (((y1 - y2) * (x - x2)) + ((x2 - x1) * (y - y2))) / denom;
            double w1 = (((y2 - y0) * (x - x2)) + ((x0 - x2) * (y - y2))) / denom;
            double w2 = 1.0 - w0 - w1;
            const double inside = -1e-9;
            if (w0 < inside || w1 < inside || w2 < inside)
                continue;

            double faceZ = (w0 * _vertices[i0 + 2]) + (w1 * _vertices[i1 + 2]) + (w2 * _vertices[i2 + 2]);
            if (faceZ > z)
                z = faceZ;
        }

        return !double.IsNegativeInfinity(z);
    }

    private readonly record struct Crossing(long EdgeKey, double X, double Y, double Z);

    private readonly record struct Piece(Crossing P, Crossing Q);

    /// <summary>
    /// The crossing on edge (a, b), computed from the edge's lower-indexed end so the two faces sharing the
    /// edge produce bit-identical points.
    /// </summary>
    private Crossing Cross(int a, double da, int b, double db)
    {
        if (a > b)
        {
            (a, b) = (b, a);
            (da, db) = (db, da);
        }

        double t = da / (da - db);
        int ia = a * 3, ib = b * 3;
        return new Crossing(
            IndexedMeshTools.GetEdgeKey(a, b),
            _vertices[ia] + ((_vertices[ib] - _vertices[ia]) * t),
            _vertices[ia + 1] + ((_vertices[ib + 1] - _vertices[ia + 1]) * t),
            _vertices[ia + 2] + ((_vertices[ib + 2] - _vertices[ia + 2]) * t));
    }

    /// <summary>
    /// Joins face pieces through their shared crossing edges. An edge is shared by at most two faces on a
    /// manifold mesh, so each crossing links at most two pieces: open chains start at a crossing only one
    /// piece reaches (the mesh border, or the edge of the visited corridor), and what remains is closed.
    /// </summary>
    private static void ChainPieces(List<Piece> pieces, double ax, double ay, double ux, double uy, List<double[]> output)
    {
        if (pieces.Count == 0)
            return;

        // Edge key → up to two piece indices (-1 when absent).
        Dictionary<long, (int First, int Second)> byEdge = IndexedMeshTools.CreateEdgeKeyMap<(int, int)>(pieces.Count * 2);
        for (int i = 0; i < pieces.Count; i++)
        {
            Link(byEdge, pieces[i].P.EdgeKey, i);
            Link(byEdge, pieces[i].Q.EdgeKey, i);
        }

        var visited = new bool[pieces.Count];
        var points = new List<double>();

        // Open chains first, from a piece one of whose crossings no other piece shares.
        for (int i = 0; i < pieces.Count; i++)
        {
            if (visited[i])
                continue;

            Piece piece = pieces[i];
            if (IsChainEnd(byEdge, piece.P.EdgeKey))
                Walk(pieces, byEdge, visited, i, piece.P, points, ax, ay, ux, uy, output);
            else if (IsChainEnd(byEdge, piece.Q.EdgeKey))
                Walk(pieces, byEdge, visited, i, piece.Q, points, ax, ay, ux, uy, output);
        }

        for (int i = 0; i < pieces.Count; i++)
        {
            if (!visited[i])
                Walk(pieces, byEdge, visited, i, pieces[i].P, points, ax, ay, ux, uy, output);
        }
    }

    private static void Link(Dictionary<long, (int First, int Second)> byEdge, long key, int piece)
    {
        if (!byEdge.TryGetValue(key, out var linked))
            byEdge[key] = (piece, -1);
        else if (linked.Second < 0 && linked.First != piece)
            byEdge[key] = (linked.First, piece);
    }

    private static bool IsChainEnd(Dictionary<long, (int First, int Second)> byEdge, long key) =>
        byEdge[key].Second < 0;

    private static void Walk(
        List<Piece> pieces,
        Dictionary<long, (int First, int Second)> byEdge,
        bool[] visited,
        int start,
        Crossing entry,
        List<double> points,
        double ax,
        double ay,
        double ux,
        double uy,
        List<double[]> output)
    {
        points.Clear();
        Append(points, entry);
        int current = start;
        Crossing entered = entry;
        while (current >= 0 && !visited[current])
        {
            visited[current] = true;
            Piece piece = pieces[current];
            // A piece's two crossings lie on two different edges of its face, so the key tells them apart.
            Crossing exit = piece.P.EdgeKey == entered.EdgeKey ? piece.Q : piece.P;
            Append(points, exit);

            var (first, second) = byEdge[exit.EdgeKey];
            current = first == current ? second : first;
            entered = exit;
        }

        if (points.Count < 6)
            return;

        // Run with increasing station, the direction every downstream consumer reads a profile in.
        int n = points.Count / 3;
        double firstStation = ((points[0] - ax) * ux) + ((points[1] - ay) * uy);
        double lastStation = ((points[(n - 1) * 3] - ax) * ux) + ((points[((n - 1) * 3) + 1] - ay) * uy);
        var chain = new double[points.Count];
        if (lastStation >= firstStation)
        {
            points.CopyTo(chain);
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                int src = (n - 1 - i) * 3;
                chain[i * 3] = points[src];
                chain[(i * 3) + 1] = points[src + 1];
                chain[(i * 3) + 2] = points[src + 2];
            }
        }

        output.Add(chain);
    }

    /// <summary>Appends a point, skipping an exact repeat (a crossing at a vertex is reached from two edges).</summary>
    private static void Append(List<double> points, Crossing crossing)
    {
        int count = points.Count;
        if (count >= 3 &&
            points[count - 3] == crossing.X &&
            points[count - 2] == crossing.Y &&
            points[count - 1] == crossing.Z)
            return;

        points.Add(crossing.X);
        points.Add(crossing.Y);
        points.Add(crossing.Z);
    }

    /// <summary>
    /// Every face registered in a cell the segment's corridor (the segment widened by
    /// <paramref name="margin"/>) passes through, ascending and without repeats. Walked column by column: in
    /// each column the segment spans a known Y range, so no cell beside the segment is visited.
    /// </summary>
    private List<int> CollectCandidateFaces(double ax, double ay, double bx, double by, double margin)
    {
        var faces = new List<int>();
        double gx0 = (Math.Min(ax, bx) - margin - _minX) * _invCell;
        double gx1 = (Math.Max(ax, bx) + margin - _minX) * _invCell;
        int cx0 = Math.Max(0, (int)Math.Floor(gx0));
        int cx1 = Math.Min(_columns - 1, (int)Math.Floor(gx1));
        if (cx0 > cx1)
            return faces;

        double dx = bx - ax;
        double dy = by - ay;
        for (int cx = cx0; cx <= cx1; cx++)
        {
            // The part of the segment inside this column's X interval, widened by the margin.
            double colMin = _minX + (cx / _invCell) - margin;
            double colMax = _minX + ((cx + 1) / _invCell) + margin;
            double yLow, yHigh;
            if (Math.Abs(dx) <= double.Epsilon * Math.Max(Math.Abs(ax), 1.0))
            {
                yLow = Math.Min(ay, by);
                yHigh = Math.Max(ay, by);
            }
            else
            {
                double t0 = Math.Clamp((colMin - ax) / dx, 0.0, 1.0);
                double t1 = Math.Clamp((colMax - ax) / dx, 0.0, 1.0);
                double y0 = ay + (dy * t0);
                double y1 = ay + (dy * t1);
                yLow = Math.Min(y0, y1);
                yHigh = Math.Max(y0, y1);
            }

            int cy0 = Math.Max(0, (int)Math.Floor((yLow - margin - _minY) * _invCell));
            int cy1 = Math.Min(_rows - 1, (int)Math.Floor((yHigh + margin - _minY) * _invCell));
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int cell = (cy * _columns) + cx;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                    faces.Add(_cellItems[k]);
            }
        }

        if (faces.Count > 1)
        {
            faces.Sort();
            int write = 1;
            for (int read = 1; read < faces.Count; read++)
            {
                if (faces[read] != faces[write - 1])
                    faces[write++] = faces[read];
            }

            faces.RemoveRange(write, faces.Count - write);
        }

        return faces;
    }

    private (int[] Start, int[] Items) BuildCells()
    {
        int cellCount = _columns * _rows;
        var start = new int[cellCount + 1];
        for (int face = 0; face < _faceCount; face++)
        {
            FaceCellRange(face, out int cx0, out int cx1, out int cy0, out int cy1);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                    start[(cy * _columns) + cx + 1]++;
            }
        }

        for (int c = 0; c < cellCount; c++)
            start[c + 1] += start[c];

        var items = new int[start[cellCount]];
        var cursor = new int[cellCount];
        Array.Copy(start, cursor, cellCount);
        for (int face = 0; face < _faceCount; face++)
        {
            FaceCellRange(face, out int cx0, out int cx1, out int cy0, out int cy1);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                    items[cursor[(cy * _columns) + cx]++] = face;
            }
        }

        return (start, items);
    }

    private void FaceCellRange(int face, out int cx0, out int cx1, out int cy0, out int cy1)
    {
        int i0 = _faces[face * 3] * 3, i1 = _faces[(face * 3) + 1] * 3, i2 = _faces[(face * 3) + 2] * 3;
        double minX = Math.Min(_vertices[i0], Math.Min(_vertices[i1], _vertices[i2]));
        double maxX = Math.Max(_vertices[i0], Math.Max(_vertices[i1], _vertices[i2]));
        double minY = Math.Min(_vertices[i0 + 1], Math.Min(_vertices[i1 + 1], _vertices[i2 + 1]));
        double maxY = Math.Max(_vertices[i0 + 1], Math.Max(_vertices[i1 + 1], _vertices[i2 + 1]));
        cx0 = Math.Clamp((int)Math.Floor((minX - _minX) * _invCell), 0, _columns - 1);
        cx1 = Math.Clamp((int)Math.Floor((maxX - _minX) * _invCell), 0, _columns - 1);
        cy0 = Math.Clamp((int)Math.Floor((minY - _minY) * _invCell), 0, _rows - 1);
        cy1 = Math.Clamp((int)Math.Floor((maxY - _minY) * _invCell), 0, _rows - 1);
    }

    /// <summary>
    /// The faces with every vertex replaced by the first vertex at exactly the same position, dropping faces
    /// that collapse. A seam split for flat shading duplicates vertices; without this a cut would stop at it.
    /// </summary>
    private static (int[] Faces, int FaceCount) WeldFaces(IndexedTriMesh mesh, bool weld)
    {
        double[] v = mesh.Vertices;
        var canonical = new int[weld ? mesh.VertexCount : 0];
        var firstAt = new Dictionary<(double, double, double), int>(weld ? mesh.VertexCount : 0);
        bool anyWelded = false;
        for (int i = 0; weld && i < mesh.VertexCount; i++)
        {
            var key = (v[i * 3], v[(i * 3) + 1], v[(i * 3) + 2]);
            if (firstAt.TryGetValue(key, out int first))
            {
                canonical[i] = first;
                anyWelded = true;
            }
            else
            {
                firstAt.Add(key, i);
                canonical[i] = i;
            }
        }

        var faces = new int[mesh.FaceCount * 3];
        int count = 0;
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            int a = mesh.Faces[f * 3], b = mesh.Faces[(f * 3) + 1], c = mesh.Faces[(f * 3) + 2];
            if ((uint)a >= (uint)mesh.VertexCount || (uint)b >= (uint)mesh.VertexCount || (uint)c >= (uint)mesh.VertexCount)
                continue;

            if (anyWelded)
            {
                a = canonical[a];
                b = canonical[b];
                c = canonical[c];
            }

            if (a == b || b == c || a == c)
                continue;

            faces[count * 3] = a;
            faces[(count * 3) + 1] = b;
            faces[(count * 3) + 2] = c;
            count++;
        }

        return (faces, count);
    }
}
