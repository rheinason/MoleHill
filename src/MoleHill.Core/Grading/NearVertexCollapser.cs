using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Merges vertices a face-by-face re-triangulation created into a neighbour closer than tolerance, by edge
/// collapse on the finished, consistent mesh.
/// </summary>
/// <remarks>
/// Re-triangulating faces one at a time used to merge such points by position as each face was built. That
/// kept near-twins out of the mesh, but each face decided alone, so two faces could merge one split two ways
/// and fold over each other. Deciding per edge keeps the faces consistent but leaves the near-twins, which a
/// later stage (grading, smoothing) then pulls to different heights: a spike. Collapsing them afterwards, as
/// edges of one mesh, gets both: a collapse is taken only when it keeps the mesh manifold (the link condition),
/// flips no face in plan and pinches no border, and only vertices created after <c>firstNewVertex</c> move,
/// so every input vertex stays where it was.
/// </remarks>
internal static class NearVertexCollapser
{
    public static int Collapse(List<double> vertices, List<int> faces, int firstNewVertex, double tolerance)
    {
        int faceCount = faces.Count / 3;
        int vertexCount = vertices.Count / 3;
        if (firstNewVertex >= vertexCount || faceCount == 0)
            return 0;

        var vertexFaces = new List<int>?[vertexCount];
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
                (vertexFaces[faces[(f * 3) + k]] ??= new List<int>(6)).Add(f);
        }

        double toleranceSquared = tolerance * tolerance;
        var candidates = new List<(double LengthSquared, int From, int To)>();
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[(f * 3) + k], b = faces[(f * 3) + ((k + 1) % 3)];
                if (a < firstNewVertex && b < firstNewVertex)
                    continue;

                double dx = vertices[a * 3] - vertices[b * 3], dy = vertices[(a * 3) + 1] - vertices[(b * 3) + 1];
                double lengthSquared = (dx * dx) + (dy * dy);
                if (lengthSquared > toleranceSquared)
                    continue;

                // Move the newer vertex; an input vertex never moves.
                (int from, int to) = a > b ? (a, b) : (b, a);
                if (from < firstNewVertex)
                    continue;
                candidates.Add((lengthSquared, from, to));
            }
        }

        candidates.Sort((x, y) => x.LengthSquared.CompareTo(y.LengthSquared));
        var removedFace = new bool[faceCount];
        var gone = new bool[vertexCount];
        int collapsed = 0;
        foreach ((_, int from, int to) in candidates)
        {
            if (gone[from] || gone[to] || vertexFaces[from] is not { } fromFaces || vertexFaces[to] is not { } toFaces)
                continue;
            if (TryCollapse(vertices, faces, fromFaces, toFaces, removedFace, from, to))
            {
                gone[from] = true;
                collapsed++;
            }
        }

        if (collapsed == 0)
            return 0;

        int write = 0;
        for (int f = 0; f < faceCount; f++)
        {
            if (removedFace[f])
                continue;
            faces[write++] = faces[f * 3];
            faces[write++] = faces[(f * 3) + 1];
            faces[write++] = faces[(f * 3) + 2];
        }

        faces.RemoveRange(write, faces.Count - write);
        return collapsed;
    }

    private static bool TryCollapse(
        List<double> v, List<int> faces, List<int> fromFaces, List<int> toFaces, bool[] removedFace, int from, int to)
    {
        // Faces on the edge, and the vertices opposite it.
        var shared = new List<int>(2);
        var opposite = new HashSet<int>();
        foreach (int f in fromFaces)
        {
            if (removedFace[f] || !Contains(faces, f, to))
                continue;
            shared.Add(f);
            opposite.Add(Third(faces, f, from, to));
        }

        if (shared.Count == 0 || shared.Count > 2)
            return false;

        // Link condition: the two ends may share no neighbour but the vertices opposite the edge.
        var fromRing = Ring(faces, fromFaces, removedFace, from);
        var toRing = Ring(faces, toFaces, removedFace, to);
        foreach (int n in fromRing)
        {
            if (n != to && toRing.Contains(n) && !opposite.Contains(n))
                return false;
        }

        // A border vertex may only slide along the border: an interior edge between two border vertices
        // would pinch the mesh into two pieces.
        bool edgeOnBorder = shared.Count == 1;
        if (!edgeOnBorder && IsBorderVertex(faces, fromFaces, removedFace, from) && IsBorderVertex(faces, toFaces, removedFace, to))
            return false;

        // No face may flip or flatten in plan once 'from' sits at 'to'.
        foreach (int f in fromFaces)
        {
            if (removedFace[f] || shared.Contains(f))
                continue;
            double before = SignedPlanArea(v, faces[f * 3], faces[(f * 3) + 1], faces[(f * 3) + 2]);
            double after = SignedPlanArea(v,
                faces[f * 3] == from ? to : faces[f * 3],
                faces[(f * 3) + 1] == from ? to : faces[(f * 3) + 1],
                faces[(f * 3) + 2] == from ? to : faces[(f * 3) + 2]);
            if (before == 0.0 || Math.Sign(after) != Math.Sign(before) || Math.Abs(after) < Math.Abs(before) * 1e-6)
                return false;
        }

        foreach (int f in shared)
            removedFace[f] = true;
        foreach (int f in fromFaces)
        {
            if (removedFace[f])
                continue;
            for (int k = 0; k < 3; k++)
            {
                if (faces[(f * 3) + k] == from)
                    faces[(f * 3) + k] = to;
            }

            toFaces.Add(f);
        }

        fromFaces.Clear();
        return true;
    }

    private static bool Contains(List<int> faces, int f, int vertex) =>
        faces[f * 3] == vertex || faces[(f * 3) + 1] == vertex || faces[(f * 3) + 2] == vertex;

    private static int Third(List<int> faces, int f, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            int x = faces[(f * 3) + k];
            if (x != a && x != b)
                return x;
        }

        return -1;
    }

    private static HashSet<int> Ring(List<int> faces, List<int> vertexFaces, bool[] removedFace, int vertex)
    {
        var ring = new HashSet<int>();
        foreach (int f in vertexFaces)
        {
            if (removedFace[f])
                continue;
            for (int k = 0; k < 3; k++)
            {
                int x = faces[(f * 3) + k];
                if (x != vertex)
                    ring.Add(x);
            }
        }

        return ring;
    }

    private static bool IsBorderVertex(List<int> faces, List<int> vertexFaces, bool[] removedFace, int vertex)
    {
        var uses = new Dictionary<int, int>();
        foreach (int f in vertexFaces)
        {
            if (removedFace[f])
                continue;
            for (int k = 0; k < 3; k++)
            {
                int x = faces[(f * 3) + k];
                if (x != vertex)
                    uses[x] = uses.GetValueOrDefault(x) + 1;
            }
        }

        foreach (int count in uses.Values)
        {
            if (count == 1)
                return true;
        }

        return false;
    }

    private static double SignedPlanArea(List<double> v, int a, int b, int c) =>
        ((v[b * 3] - v[a * 3]) * (v[(c * 3) + 1] - v[(a * 3) + 1])) -
        ((v[(b * 3) + 1] - v[(a * 3) + 1]) * (v[c * 3] - v[a * 3]));
}
