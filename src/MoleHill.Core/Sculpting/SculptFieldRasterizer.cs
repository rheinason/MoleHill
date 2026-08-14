using MoleHill.Core.Grading;

namespace MoleHill.Core.Sculpting;

/// <summary>
/// Commits a stroke's per-vertex sculpt result into the displacement field: for every field sample
/// inside the stroke's dirty bounds (padded by one cell), the per-vertex raw delta surface
/// ((vertex Z - BaseZ) / constraint influence, piecewise linear over the working mesh) is interpolated
/// at the sample's world position and written into the field. Fully protected samples and samples
/// outside the mesh are left untouched. Because the working Z already contains any pre-existing
/// displacement (BaseZ excludes its masked contribution), overwriting samples merges old and new
/// sculpting automatically without applying the feather twice.
/// </summary>
public static class SculptFieldRasterizer
{
    public static void Rasterize(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] baseZ,
        SculptDisplacementField field,
        double dirtyMinX,
        double dirtyMaxX,
        double dirtyMinY,
        double dirtyMaxY,
        SculptConstraintMask? constraintMask = null)
    {
        if (vertexCount == 0 || faceCount == 0 || dirtyMinX > dirtyMaxX || dirtyMinY > dirtyMaxY)
            return;

        // Raw-delta surface: same XY, Z = unmasked displacement. The working mesh contains masked
        // displacement, so divide at vertices where influence is nonzero. At pinned vertices, carry
        // the existing raw field value through; protected field samples themselves are never written.
        var deltaVertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            double influence = constraintMask?.EvaluateInfluence(x, y) ?? 1.0;
            deltaVertices[i * 3] = x;
            deltaVertices[i * 3 + 1] = y;
            deltaVertices[i * 3 + 2] = influence <= 1e-9
                ? field.Sample(x, y)
                : (vertices[i * 3 + 2] - baseZ[i]) / influence;
        }

        var deltaGrid = new TerrainFaceGrid(deltaVertices, vertexCount, faces, faceCount);

        double cell = field.CellSize;
        int giMin = (int)Math.Floor(dirtyMinX / cell) - 1;
        int giMax = (int)Math.Ceiling(dirtyMaxX / cell) + 1;
        int gjMin = (int)Math.Floor(dirtyMinY / cell) - 1;
        int gjMax = (int)Math.Ceiling(dirtyMaxY / cell) + 1;

        var written = new HashSet<(int, int)>();
        var misses = new List<(int Gi, int Gj)>();
        for (int gj = gjMin; gj <= gjMax; gj++)
        {
            for (int gi = giMin; gi <= giMax; gi++)
            {
                double x = gi * cell;
                double y = gj * cell;
                double influence = constraintMask?.EvaluateInfluence(x, y) ?? 1.0;
                if (influence <= 1e-9)
                    continue; // preserve any displacement that existed before this region was protected

                if (deltaGrid.TryInterpolateZ(x, y, out double delta))
                {
                    field.SetSample(gi, gj, (float)delta);
                    written.Add((gi, gj));
                }
                else
                {
                    misses.Add((gi, gj));
                }
            }
        }

        FillPointLocationHoles(field, written, misses);
    }

    /// <summary>
    /// Point location can fail for samples that ARE on the mesh when the containing triangle is a
    /// degenerate sliver (near-zero barycentric denominator — common after DynTopo refinement of very
    /// anisotropic terrain). Left unwritten, those samples form a comb of stale/zero values inside the
    /// stroke that replays as needle spikes. Fill them from written orthogonal neighbors (>= 2, so the
    /// fill can't creep outward past the mesh boundary), relaxing until stable.
    /// </summary>
    private static void FillPointLocationHoles(
        SculptDisplacementField field,
        HashSet<(int, int)> written,
        List<(int Gi, int Gj)> misses)
    {
        const int maxPasses = 4;
        for (int pass = 0; pass < maxPasses && misses.Count > 0; pass++)
        {
            var remaining = new List<(int Gi, int Gj)>();
            int filled = 0;
            foreach (var (gi, gj) in misses)
            {
                double sum = 0.0;
                int count = 0;
                if (written.Contains((gi - 1, gj))) { sum += field.GetSample(gi - 1, gj); count++; }
                if (written.Contains((gi + 1, gj))) { sum += field.GetSample(gi + 1, gj); count++; }
                if (written.Contains((gi, gj - 1))) { sum += field.GetSample(gi, gj - 1); count++; }
                if (written.Contains((gi, gj + 1))) { sum += field.GetSample(gi, gj + 1); count++; }

                if (count >= 2)
                {
                    field.SetSample(gi, gj, (float)(sum / count));
                    written.Add((gi, gj));
                    filled++;
                }
                else
                {
                    remaining.Add((gi, gj));
                }
            }

            if (filled == 0)
                return;
            misses = remaining;
        }
    }
}
