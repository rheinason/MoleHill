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

        double cell = field.CellSize;
        int giMin = (int)Math.Floor(dirtyMinX / cell) - 1;
        int giMax = (int)Math.Ceiling(dirtyMaxX / cell) + 1;
        int gjMin = (int)Math.Floor(dirtyMinY / cell) - 1;
        int gjMax = (int)Math.Ceiling(dirtyMaxY / cell) + 1;

        // Only the faces under the sampled rectangle can contain a sample, so only those need a delta
        // surface. Building the raw-delta copy and its face grid over the WHOLE terrain made a small
        // stroke cost time and memory proportional to the terrain, not to the stroke. Selecting the
        // subset is one arithmetic pass over the faces, with no per-face allocation.
        if (!TryBuildLocalDeltaSurface(
                vertices,
                faces,
                faceCount,
                baseZ,
                field,
                constraintMask,
                giMin * cell,
                giMax * cell,
                gjMin * cell,
                gjMax * cell,
                out double[] deltaVertices,
                out int deltaVertexCount,
                out int[] deltaFaces,
                out int deltaFaceCount))
        {
            return;
        }

        var deltaGrid = new TerrainFaceGrid(deltaVertices, deltaVertexCount, deltaFaces, deltaFaceCount);

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
    /// Builds the raw-delta surface restricted to the faces whose XY bounds meet the sampled rectangle.
    /// </summary>
    /// <remarks>
    /// Raw-delta surface: same XY, Z = unmasked displacement. The working mesh contains masked
    /// displacement, so divide at vertices where influence is nonzero. At pinned vertices, carry the
    /// existing raw field value through; protected field samples themselves are never written.
    ///
    /// A face whose XY bounding box misses the sampled rectangle cannot contain any sample in it, so
    /// leaving it out changes no interpolation result. Vertices are pulled in on first use, so the
    /// delta copy is sized by the stroke, not by the terrain.
    /// </remarks>
    private static bool TryBuildLocalDeltaSurface(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] baseZ,
        SculptDisplacementField field,
        SculptConstraintMask? constraintMask,
        double regionMinX,
        double regionMaxX,
        double regionMinY,
        double regionMaxY,
        out double[] deltaVertices,
        out int deltaVertexCount,
        out int[] deltaFaces,
        out int deltaFaceCount)
    {
        var localIndexBySource = new Dictionary<int, int>();
        var localVertices = new List<double>();
        var localFaces = new List<int>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[(f * 3) + 1];
            int i2 = faces[(f * 3) + 2];

            double x0 = vertices[i0 * 3], y0 = vertices[(i0 * 3) + 1];
            double x1 = vertices[i1 * 3], y1 = vertices[(i1 * 3) + 1];
            double x2 = vertices[i2 * 3], y2 = vertices[(i2 * 3) + 1];

            if (Math.Min(x0, Math.Min(x1, x2)) > regionMaxX ||
                Math.Max(x0, Math.Max(x1, x2)) < regionMinX ||
                Math.Min(y0, Math.Min(y1, y2)) > regionMaxY ||
                Math.Max(y0, Math.Max(y1, y2)) < regionMinY)
            {
                continue;
            }

            localFaces.Add(MapVertex(vertices, baseZ, field, constraintMask, localIndexBySource, localVertices, i0));
            localFaces.Add(MapVertex(vertices, baseZ, field, constraintMask, localIndexBySource, localVertices, i1));
            localFaces.Add(MapVertex(vertices, baseZ, field, constraintMask, localIndexBySource, localVertices, i2));
        }

        if (localFaces.Count == 0)
        {
            deltaVertices = Array.Empty<double>();
            deltaVertexCount = 0;
            deltaFaces = Array.Empty<int>();
            deltaFaceCount = 0;
            return false;
        }

        deltaVertices = localVertices.ToArray();
        deltaVertexCount = deltaVertices.Length / 3;
        deltaFaces = localFaces.ToArray();
        deltaFaceCount = deltaFaces.Length / 3;
        return true;
    }

    private static int MapVertex(
        double[] vertices,
        double[] baseZ,
        SculptDisplacementField field,
        SculptConstraintMask? constraintMask,
        Dictionary<int, int> localIndexBySource,
        List<double> localVertices,
        int sourceVertex)
    {
        if (localIndexBySource.TryGetValue(sourceVertex, out int existing))
            return existing;

        double x = vertices[sourceVertex * 3];
        double y = vertices[(sourceVertex * 3) + 1];
        double influence = constraintMask?.EvaluateInfluence(x, y) ?? 1.0;

        int localIndex = localVertices.Count / 3;
        localVertices.Add(x);
        localVertices.Add(y);
        localVertices.Add(influence <= 1e-9
            ? field.Sample(x, y)
            : (vertices[(sourceVertex * 3) + 2] - baseZ[sourceVertex]) / influence);
        localIndexBySource[sourceVertex] = localIndex;
        return localIndex;
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
