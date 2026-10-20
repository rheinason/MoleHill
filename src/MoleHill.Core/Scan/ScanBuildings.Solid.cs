using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>Plane arrangements over footprint loops, with shared vertices, walls and a level base.</summary>
public static partial class ScanBuildings
{
    private readonly record struct Cut(double A, double B, double C);
    private sealed record RoofCell(List<P2> Polygon, int Plane);
    private readonly record struct CellEdge(int A, int B, int Cell);

    private static IndexedTriMesh BuildSolid(List<List<P2>> footprints, RoofModel model, double baseZ, Options options)
    {
        if (!double.IsFinite(baseZ))
            return EmptyShell(model, "No finite ground height");
        var inputXy = new List<double>();
        var containment = new List<double[]>();
        var segments = new List<int>();
        foreach (List<P2> loop in footprints)
        {
            containment.Add(loop.SelectMany(p => new[] { p.X, p.Y }).ToArray());
            int start = inputXy.Count / 2;
            foreach (P2 p in loop)
            {
                inputXy.Add(p.X); inputXy.Add(p.Y);
            }
            for (int i = 0; i < loop.Count; i++)
            {
                segments.Add(start + i); segments.Add(start + (i + 1) % loop.Count);
            }
        }
        TinResult? tin = new TinEngine().Build(inputXy.ToArray(), new double[inputXy.Count / 2], segments.ToArray(),
            QualitySettings.None, out _, useConvexHull: false, includeEdgeTopology: false);
        if (tin == null)
            return EmptyShell(model, "Footprint triangulation failed");
        double epsilon = Math.Max(options.FootprintTolerance, options.RoofTolerance) * 1e-7;
        var cuts = model.ExtraCuts.ToList();
        for (int i = 0; i < model.Planes.Count; i++)
            for (int j = i + 1; j < model.Planes.Count; j++)
            {
                if (model.Meets != null && !model.Meets(i, j))
                    continue;
                RoofPlane p = model.Planes[i], q = model.Planes[j];
                double a = p.X - q.X, b = p.Y - q.Y, c = p.Z - q.Z;
                double length = Math.Sqrt(a * a + b * b);
                if (length < 1e-10)
                    continue;
                a /= length; b /= length; c /= length;
                if (a < -1e-10 || Math.Abs(a) <= 1e-10 && b < 0)
                {
                    a = -a; b = -b; c = -c;
                }
                if (!cuts.Any(cut => Math.Abs(cut.A - a) < 1e-8 && Math.Abs(cut.B - b) < 1e-8 && Math.Abs(cut.C - c) < epsilon))
                    cuts.Add(new Cut(a, b, c));
            }
        var cells = new List<RoofCell>();
        for (int face = 0; face < tin.FaceCount; face++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var triangle = new List<P2>();
            for (int i = 0; i < 3; i++)
            {
                int vertex = tin.Faces[face * 3 + i] * 3;
                triangle.Add(new P2(tin.Vertices[vertex], tin.Vertices[vertex + 1]));
            }
            if (!InsideLoops(new P2(triangle.Average(p => p.X), triangle.Average(p => p.Y)), containment))
                continue;
            if (SignedArea(triangle) < 0)
                triangle.Reverse();
            var pieces = new List<List<P2>> { triangle };
            foreach (Cut cut in cuts)
            {
                var next = new List<List<P2>>();
                foreach (List<P2> piece in pieces)
                {
                    double min = piece.Min(p => cut.A * p.X + cut.B * p.Y + cut.C);
                    double max = piece.Max(p => cut.A * p.X + cut.B * p.Y + cut.C);
                    if (min >= -epsilon || max <= epsilon)
                    {
                        next.Add(piece);
                        continue;
                    }
                    List<P2> positive = ClipHalfPlane(piece, cut, true, epsilon);
                    List<P2> negative = ClipHalfPlane(piece, cut, false, epsilon);
                    if (positive.Count >= 3 && SignedArea(positive) > epsilon * epsilon)
                        next.Add(positive);
                    if (negative.Count >= 3 && SignedArea(negative) > epsilon * epsilon)
                        next.Add(negative);
                }
                pieces = next;
                if (pieces.Count + cells.Count > 20_000)
                    return EmptyShell(model, "Arrangement exceeded cell limit");
            }
            foreach (List<P2> piece in pieces)
            {
                double x = piece.Average(p => p.X), y = piece.Average(p => p.Y);
                cells.Add(new RoofCell(piece, model.Select(x, y)));
            }
        }
        var xyPoints = new List<P2>();
        var xyVertices = new List<List<int>>();
        var xyBuckets = new Dictionary<(long X, long Y), List<int>>();
        var vertices = new List<double>();
        var vertexBuckets = new Dictionary<(int Xy, long Z), List<int>>();
        var faces = new List<int>();
        var edgeUses = IndexedMeshTools.CreateEdgeKeyMap<List<CellEdge>>(cells.Count * 4);
        int[][] cellXy = cells.Select(cell => cell.Polygon.Select(AddXy).ToArray()).ToArray();
        for (int cell = 0; cell < cells.Count; cell++)
            for (int i = 0; i < cellXy[cell].Length; i++)
            {
                int a = cellXy[cell][i], b = cellXy[cell][(i + 1) % cellXy[cell].Length];
                if (a == b)
                    continue;
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (!edgeUses.TryGetValue(key, out List<CellEdge>? uses))
                    edgeUses.Add(key, uses = new List<CellEdge>(2));
                uses.Add(new CellEdge(a, b, cell));
            }
        if (model.ExtraCuts.Count > 0 || model.Kind == ScanRoofKind.PlaneArrangement && options.MinimumRoofDetail > 0)
        {
            // A composite's parts already respect the roof detail; a narrow gable half within one is no detail.
            SimplifySpatialPatches(cells, edgeUses, model, options.MinimumArea * .03,
                options.CancellationToken, model.Kind == ScanRoofKind.Composite ? 0 : options.MinimumRoofDetail);
            RegularizeRoofJunctions(cells, cellXy, xyPoints, edgeUses, model, baseZ, epsilon, options.CancellationToken);
        }
        for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            RoofCell cell = cells[cellIndex];
            options.CancellationToken.ThrowIfCancellationRequested();
            RoofPlane plane = model.Planes[cell.Plane];
            if (cell.Polygon.Any(p => plane.Height(p.X, p.Y) <= baseZ + options.MinimumHeight * 0.25))
                return EmptyShell(model, "An arrangement cell fell below the building base");
            int[] xy = cellXy[cellIndex];
            P2 center = new(cell.Polygon.Average(p => p.X), cell.Polygon.Average(p => p.Y));
            int centerId = AddXy(center), topCenter = AddVertex(centerId, plane.Height(center.X, center.Y)), bottomCenter = AddVertex(centerId, baseZ);
            for (int i = 0; i < xy.Length; i++)
            {
                int a = xy[i], b = xy[(i + 1) % xy.Length];
                if (a == b)
                    continue;
                int topA = AddVertex(a, plane.Height(xyPoints[a].X, xyPoints[a].Y));
                int topB = AddVertex(b, plane.Height(xyPoints[b].X, xyPoints[b].Y));
                AddFace(topCenter, topA, topB);
                AddFace(bottomCenter, AddVertex(b, baseZ), AddVertex(a, baseZ));
            }
        }
        foreach (List<CellEdge> uses in edgeUses.Values)
        {
            if (uses.Count > 2)
                return EmptyShell(model, "More than two cells use one plan edge");
            CellEdge edge = uses[0];
            RoofPlane upper = model.Planes[cells[edge.Cell].Plane];
            RoofPlane lower = new(0, 0, baseZ);
            if (uses.Count == 2)
            {
                lower = model.Planes[cells[uses[1].Cell].Plane];
                P2 a = xyPoints[edge.A], b = xyPoints[edge.B];
                double high = upper.Height((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                double low = lower.Height((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                if (Math.Abs(high - low) <= epsilon)
                    continue;
                if (high < low)
                {
                    edge = uses[1];
                    (upper, lower) = (lower, upper);
                }
            }
            int topA = AddVertex(edge.A, upper.Height(xyPoints[edge.A].X, xyPoints[edge.A].Y));
            int topB = AddVertex(edge.B, upper.Height(xyPoints[edge.B].X, xyPoints[edge.B].Y));
            int bottomA = AddVertex(edge.A, lower.Height(xyPoints[edge.A].X, xyPoints[edge.A].Y));
            int bottomB = AddVertex(edge.B, lower.Height(xyPoints[edge.B].X, xyPoints[edge.B].Y));
            // Several roof levels can meet at one plan vertex. Split every vertical edge at ALL
            // those levels; a long quad beside two shorter quads would leave a T-junction otherwise.
            var polygon = new List<int> { topB, topA };
            polygon.AddRange(xyVertices[edge.A].Where(v => vertices[v * 3 + 2] < vertices[topA * 3 + 2] - epsilon &&
                vertices[v * 3 + 2] > vertices[bottomA * 3 + 2] + epsilon).OrderByDescending(v => vertices[v * 3 + 2]));
            polygon.Add(bottomA); polygon.Add(bottomB);
            polygon.AddRange(xyVertices[edge.B].Where(v => vertices[v * 3 + 2] > vertices[bottomB * 3 + 2] + epsilon &&
                vertices[v * 3 + 2] < vertices[topB * 3 + 2] - epsilon).OrderBy(v => vertices[v * 3 + 2]));
            int centerXy = AddXy(new P2(polygon.Average(v => vertices[v * 3]), polygon.Average(v => vertices[v * 3 + 1])));
            int center = AddVertex(centerXy, polygon.Average(v => vertices[v * 3 + 2]));
            for (int i = 0; i < polygon.Count; i++)
                AddFace(center, polygon[i], polygon[(i + 1) % polygon.Count]);
        }
        var shellEdges = IndexedMeshTools.CreateEdgeKeyMap<(int Count, int Direction)>(faces.Count);
        for (int f = 0; f < faces.Count; f += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f + k], b = faces[f + (k + 1) % 3];
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                shellEdges.TryGetValue(key, out var entry);
                shellEdges[key] = (entry.Count + 1, entry.Direction + (a < b ? 1 : -1));
            }
        if (shellEdges.Values.Any(edge => edge.Count != 2 || edge.Direction != 0))
        {
            var bad = shellEdges.First(pair => pair.Value.Count != 2 || pair.Value.Direction != 0);
            int a = (int)(bad.Key >> 32), b = (int)(bad.Key & uint.MaxValue);
            return EmptyShell(model, FormattableString.Invariant($"Shell edge uses: {shellEdges.Values.Count(edge => edge.Count == 1)} open, {shellEdges.Values.Count(edge => edge.Count > 2)} multiple, {shellEdges.Values.Count(edge => edge.Count == 2 && edge.Direction != 0)} reversed; first {bad.Value.Count} uses, delta ({vertices[b * 3] - vertices[a * 3]:G5},{vertices[b * 3 + 1] - vertices[a * 3 + 1]:G5},{vertices[b * 3 + 2] - vertices[a * 3 + 2]:G5}) at ({vertices[a * 3]:G5},{vertices[a * 3 + 1]:G5},{vertices[a * 3 + 2]:G5})"));
        }
        return IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());

        int AddXy(P2 point)
        {
            long x = (long)Math.Floor(point.X / epsilon), y = (long)Math.Floor(point.Y / epsilon);
            int nearest = int.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (xyBuckets.TryGetValue((x + dx, y + dy), out List<int>? bucket))
                        foreach (int index in bucket)
                            if (Math.Abs(point.X - xyPoints[index].X) <= epsilon && Math.Abs(point.Y - xyPoints[index].Y) <= epsilon)
                                nearest = Math.Min(nearest, index);
            if (nearest != int.MaxValue)
                return nearest;
            int added = xyPoints.Count;
            xyPoints.Add(point);
            xyVertices.Add(new List<int>());
            if (!xyBuckets.TryGetValue((x, y), out List<int>? current))
                xyBuckets.Add((x, y), current = new List<int>());
            current.Add(added);
            return added;
        }
        int AddVertex(int xy, double z)
        {
            long key = (long)Math.Floor(z / epsilon);
            for (int dz = -1; dz <= 1; dz++)
                if (vertexBuckets.TryGetValue((xy, key + dz), out List<int>? bucket))
                    foreach (int index in bucket)
                        if (Math.Abs(vertices[index * 3 + 2] - z) <= epsilon)
                            return index;
            int added = vertices.Count / 3;
            vertices.Add(xyPoints[xy].X); vertices.Add(xyPoints[xy].Y); vertices.Add(z);
            xyVertices[xy].Add(added);
            if (!vertexBuckets.TryGetValue((xy, key), out List<int>? current))
                vertexBuckets.Add((xy, key), current = new List<int>());
            current.Add(added);
            return added;
        }
        void AddFace(int a, int b, int c)
        {
            if (a == b || b == c || c == a)
                return;
            faces.Add(a); faces.Add(b); faces.Add(c);
        }
    }

    private static List<P2> ClipHalfPlane(List<P2> polygon, Cut cut, bool positive, double epsilon)
    {
        var result = new List<P2>();
        for (int i = 0; i < polygon.Count; i++)
        {
            P2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            double da = cut.A * a.X + cut.B * a.Y + cut.C, db = cut.A * b.X + cut.B * b.Y + cut.C;
            bool insideA = positive ? da >= -epsilon : da <= epsilon;
            bool insideB = positive ? db >= -epsilon : db <= epsilon;
            if (insideA)
                Append(a);
            if (insideA != insideB)
            {
                double t = da / (da - db);
                Append(new P2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        if (result.Count > 1 && Math.Abs(result[0].X - result[^1].X) + Math.Abs(result[0].Y - result[^1].Y) <= epsilon)
            result.RemoveAt(result.Count - 1);
        return result;
        void Append(P2 p)
        {
            if (result.Count == 0 || Math.Abs(result[^1].X - p.X) + Math.Abs(result[^1].Y - p.Y) > epsilon)
                result.Add(p);
        }
    }

    private static IndexedTriMesh EmptyShell(RoofModel model, string reason)
    {
        model.Diagnostic += " " + reason + ".";
        return IndexedTriMesh.Empty;
    }
}
