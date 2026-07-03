using MoleHill.Core.Engine;

namespace MoleHill.Core.Retopo;

/// <summary>
/// Stage 2 of quad retopology: a field-guided <b>parametrization</b> (u,v) of the terrain's XY plane whose
/// gradients follow the cross-field at a target spacing h. Integer isolines of u and v then form the quad
/// grid (see <see cref="QuadExtractor"/>).
///
/// The 4-RoSy field is first turned into a locally consistent direction field by BFS branch resolution
/// (per face, pick the 90° representative closest to a visited neighbour). Then u,v are the least-squares
/// scalar fields with ∇u ≈ e1/h and ∇v ≈ e2/h — solved as two Poisson problems <c>(GᵀM G) s = GᵀM g</c>
/// (G = per-face gradient, M = area; this is the cotangent Laplacian assembled implicitly) by a hand-rolled,
/// matrix-free Conjugate Gradient. No external solver. First cut is non-seamless: it does not enforce
/// integer/seamless transitions at singularities, so small defects there are expected and cleaned up later.
/// </summary>
public static class GuidedParametrizer
{
    /// <param name="active">
    /// Optional per-face mask; faces marked false (e.g. near-vertical retaining-wall faces, which are a thin
    /// sliver in plan with a stiff 1/area gradient) are dropped from the system so they cannot distort u,v.
    /// </param>
    public static (double[] U, double[] V) Solve(double[] vertices, int[] faces, double[] theta, double h, bool[]? active = null)
    {
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        double spacing = h > 1e-9 ? h : 1.0;

        double[] phi = ResolveFaceAngles(vertices, faces, theta);

        // Per-face gradient basis G_i = rot90(opposite edge)/(2A) and |area|. rot90(x,y) = (-y, x). Using the
        // signed 2A here with |A| as the mass weight makes (GᵀM G) the (positive) cotangent Laplacian
        // regardless of triangle winding.
        var gx = new double[faceCount * 3];
        var gy = new double[faceCount * 3];
        var mass = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            if (active != null && !active[f])
                continue; // masked-out face: mass stays 0 and basis stays 0 (contributes nothing)

            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1];
            double cx = vertices[c * 3], cy = vertices[c * 3 + 1];
            double twoArea = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            mass[f] = Math.Abs(twoArea) * 0.5;
            if (Math.Abs(twoArea) < 1e-15)
            {
                // Degenerate face contributes nothing.
                gx[f * 3] = gx[f * 3 + 1] = gx[f * 3 + 2] = 0;
                gy[f * 3] = gy[f * 3 + 1] = gy[f * 3 + 2] = 0;
                continue;
            }

            double inv = 1.0 / twoArea;
            // Opposite edge of vertex 0 is (c - b), of vertex 1 is (a - c), of vertex 2 is (b - a).
            SetBasis(gx, gy, f, 0, cx - bx, cy - by, inv);
            SetBasis(gx, gy, f, 1, ax - cx, ay - cy, inv);
            SetBasis(gx, gy, f, 2, bx - ax, by - ay, inv);
        }

        double[] u = SolvePoisson(faces, faceCount, vertexCount, gx, gy, mass, phi, spacing, useAcross: false);
        double[] v = SolvePoisson(faces, faceCount, vertexCount, gx, gy, mass, phi, spacing, useAcross: true);
        return (u, v);
    }

    private static void SetBasis(double[] gx, double[] gy, int f, int i, double ex, double ey, double invTwoArea)
    {
        // grad(λ_i) = rot90(opposite edge) / (2A); rot90(x,y) = (-y, x).
        gx[f * 3 + i] = -ey * invTwoArea;
        gy[f * 3 + i] = ex * invTwoArea;
    }

    private static double[] SolvePoisson(
        int[] faces, int faceCount, int vertexCount,
        double[] gx, double[] gy, double[] mass, double[] phi, double h, bool useAcross)
    {
        // Right-hand side b = GᵀM g_target, with g_target = e/h (e1 along φ for u, e2 = φ+90° for v).
        var b = new double[vertexCount];
        for (int f = 0; f < faceCount; f++)
        {
            double angle = useAcross ? phi[f] + (Math.PI / 2.0) : phi[f];
            double tx = Math.Cos(angle) / h;
            double ty = Math.Sin(angle) / h;
            double w = mass[f];
            for (int i = 0; i < 3; i++)
            {
                int vi = faces[f * 3 + i];
                b[vi] += ((gx[f * 3 + i] * tx) + (gy[f * 3 + i] * ty)) * w;
            }
        }

        var s = new double[vertexCount];
        ConjugateGradient(faces, faceCount, vertexCount, gx, gy, mass, b, s);
        return s;
    }

    /// <summary>Solves L·s = b (L = GᵀM G + tiny regularization) for the Poisson scalar field, matrix-free.</summary>
    private static void ConjugateGradient(
        int[] faces, int faceCount, int vertexCount,
        double[] gx, double[] gy, double[] mass, double[] b, double[] s)
    {
        var r = (double[])b.Clone(); // r = b - L·0 = b
        var p = (double[])r.Clone();
        var ap = new double[vertexCount];
        double rsOld = Dot(r, r);
        if (rsOld <= 0.0)
            return;

        double bNorm = Math.Sqrt(rsOld);
        int maxIter = Math.Clamp(vertexCount * 4, 200, 20000);
        for (int iter = 0; iter < maxIter; iter++)
        {
            ApplyLaplacian(faces, faceCount, vertexCount, gx, gy, mass, p, ap);
            double denom = Dot(p, ap);
            if (Math.Abs(denom) < 1e-30)
                break;

            double alpha = rsOld / denom;
            for (int i = 0; i < vertexCount; i++)
            {
                s[i] += alpha * p[i];
                r[i] -= alpha * ap[i];
            }

            double rsNew = Dot(r, r);
            if (Math.Sqrt(rsNew) <= 1e-8 * bNorm)
                break;

            double beta = rsNew / rsOld;
            for (int i = 0; i < vertexCount; i++)
                p[i] = r[i] + (beta * p[i]);
            rsOld = rsNew;
        }
    }

    private static void ApplyLaplacian(
        int[] faces, int faceCount, int vertexCount,
        double[] gx, double[] gy, double[] mass, double[] s, double[] outv)
    {
        Array.Clear(outv, 0, vertexCount);
        for (int f = 0; f < faceCount; f++)
        {
            // grad(s) on the face.
            double gsx = 0.0, gsy = 0.0;
            for (int i = 0; i < 3; i++)
            {
                int vi = faces[f * 3 + i];
                gsx += s[vi] * gx[f * 3 + i];
                gsy += s[vi] * gy[f * 3 + i];
            }

            double w = mass[f];
            for (int i = 0; i < 3; i++)
            {
                int vi = faces[f * 3 + i];
                outv[vi] += ((gx[f * 3 + i] * gsx) + (gy[f * 3 + i] * gsy)) * w;
            }
        }

        // Tiny Tikhonov term removes the constant nullspace so CG has a unique target (the shift is
        // irrelevant — it only relabels which integers the lattice falls on).
        for (int i = 0; i < vertexCount; i++)
            outv[i] += 1e-9 * s[i];
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// Turns the per-vertex 4-RoSy field into a locally consistent per-face direction angle by BFS: each
    /// face adopts the 90° representative of its own averaged angle that is closest to an already-resolved
    /// neighbour. Consistent everywhere except around singularities.
    /// </summary>
    private static double[] ResolveFaceAngles(double[] vertices, int[] faces, double[] theta)
    {
        int faceCount = faces.Length / 3;
        var baseAngle = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            double sx = 0.0, sy = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double t = theta[faces[f * 3 + i]];
                sx += Math.Cos(4.0 * t);
                sy += Math.Sin(4.0 * t);
            }

            baseAngle[f] = Math.Atan2(sy, sx) / 4.0; // representative in (-π/4, π/4]
        }

        // Edge -> the two incident faces, for neighbour walking.
        var edgeFaces = new Dictionary<long, (int f0, int f1)>(faceCount * 3);
        for (int f = 0; f < faceCount; f++)
        {
            AddEdgeFace(edgeFaces, faces[f * 3], faces[f * 3 + 1], f);
            AddEdgeFace(edgeFaces, faces[f * 3 + 1], faces[f * 3 + 2], f);
            AddEdgeFace(edgeFaces, faces[f * 3 + 2], faces[f * 3], f);
        }

        var phi = new double[faceCount];
        var done = new bool[faceCount];
        var queue = new Queue<int>();
        double quarter = Math.PI / 2.0;

        for (int seed = 0; seed < faceCount; seed++)
        {
            if (done[seed])
                continue;

            phi[seed] = baseAngle[seed];
            done[seed] = true;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                for (int e = 0; e < 3; e++)
                {
                    int a = faces[f * 3 + e], b = faces[f * 3 + ((e + 1) % 3)];
                    if (!edgeFaces.TryGetValue(EdgeKey(a, b), out (int f0, int f1) pair))
                        continue;

                    int n = pair.f0 == f ? pair.f1 : pair.f0;
                    if (n < 0 || done[n])
                        continue;

                    // Shift baseAngle[n] by the multiple of 90° that lands nearest phi[f].
                    double k = Math.Round((phi[f] - baseAngle[n]) / quarter);
                    phi[n] = baseAngle[n] + (k * quarter);
                    done[n] = true;
                    queue.Enqueue(n);
                }
            }
        }

        return phi;
    }

    private static void AddEdgeFace(Dictionary<long, (int f0, int f1)> edgeFaces, int a, int b, int face)
    {
        long key = EdgeKey(a, b);
        if (!edgeFaces.TryGetValue(key, out (int f0, int f1) pair))
            edgeFaces[key] = (face, -1);
        else if (pair.f1 < 0)
            edgeFaces[key] = (pair.f0, face);
    }

    private static long EdgeKey(int a, int b) => IndexedMeshTools.GetEdgeKey(a, b);
}
