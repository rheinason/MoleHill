// Breakline-mode wall ends: pins the terrain round a converged (pointed or round) end so it does not end in a cliff.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static partial class RetainingWallStage
{
    /// <summary>
    /// One breakline round each converged end whose top rail stands above the ground more than its toe does
    /// (a kerb drawn with its top raised over the ground): each ring point keeps the incoming ground plus a
    /// blend of the two rails' rises, the top rail's on the top side and the toe's on the toe side, so the ramp
    /// behind the top rail wraps round the end. Without it the ramp ends in one sliver face from the raised end
    /// and the tip to the nearest ground vertex (81 degrees, 1.3 m long beyond a round kerb nose, found live).
    /// A ring that would cross a rail, another ring or an existing line is left out: the end is then as before.
    /// When the wall grades (<paramref name="graded"/> is not null) its batter is the ramp, and it can come
    /// back down to the ground closer behind the top rail than the ring lies; the ring's top side then takes
    /// the batter's rise where the ring starts.
    /// </summary>
    private static List<ConstraintPolyline> BuildConvergedEndRings(
        RhinoMesh incoming,
        RhinoMesh? graded,
        IReadOnlyList<RetainingWallPlannerCore.ConvergedEnd> ends,
        IReadOnlyList<ConstraintPolyline> rails,
        IReadOnlyList<ConstraintPolyline> existing,
        double tolerance,
        out string summary)
    {
        var rings = new List<ConstraintPolyline>();
        summary = string.Empty;
        if (ends.Count == 0)
            return rings;

        int level = 0, offMesh = 0, crossing = 0;
        var incomingGround = new GroundSampler(incoming, tolerance);
        GroundSampler? gradedGround = graded != null ? new GroundSampler(graded, tolerance) : null;

        var others = new List<ConstraintPolyline>(rails.Count + existing.Count + ends.Count);
        others.AddRange(rails);
        others.AddRange(existing);
        foreach (RetainingWallPlannerCore.ConvergedEnd end in ends)
        {
            if (!incomingGround.TryGround(end.TopEnd, out double topGround) ||
                !incomingGround.TryGround(end.Tip, out double tipGround))
            {
                offMesh++;
                continue;
            }

            double topRise = end.TopEnd.Z - topGround;
            double toeRise = end.Tip.Z - tipGround;
            double step = Math.Abs(topRise - toeRise);
            if (step <= 10.0 * tolerance)
            {
                level++;
                continue;
            }

            // Wide enough that the ring's own fall, from the top rail's rise to the toe's over half a turn, stays
            // gentle (about 13 degrees), and never inside the wall.
            double radius = Math.Max(Math.Max(end.BodyGap, 1.5 * step), 20.0 * tolerance);
            (Point3d[] points, double[] topWeights) = end.Ring(radius);

            // Graded, the batter is the ramp, and it may already be back down to the ground behind the top rail
            // where the ring starts (0.19 m behind a 0.15 m kerb), so the top side takes the batter's rise there
            // when that is the lower. One rise for the whole side keeps the ring's fall smooth: picking per point
            // jumped from one to the other between neighbours, a new cliff.
            double ringTopRise = topRise;
            if (gradedGround != null &&
                incomingGround.TryGround(points[0], out double besideGround) &&
                gradedGround.TryGround(points[0], out double besideGraded) &&
                Math.Abs(besideGraded - besideGround) < Math.Abs(ringTopRise))
            {
                ringTopRise = besideGraded - besideGround;
            }

            var values = new double[points.Length * 3];
            bool draped = true;
            for (int i = 0; i < points.Length && draped; i++)
            {
                draped = incomingGround.TryGround(points[i], out double ground);
                values[i * 3] = points[i].X;
                values[i * 3 + 1] = points[i].Y;
                values[i * 3 + 2] = ground + (topWeights[i] * ringTopRise) + ((1.0 - topWeights[i]) * toeRise);
            }

            if (!draped)
            {
                offMesh++;
                continue;
            }

            var ring = new ConstraintPolyline(values, points.Length, IsClosed: false, PreserveInputElevation: true);
            // Any crossing at all, whatever its heights: the ring is a nicety and must never cost the wall.
            if (BreaklineHeightConflicts.Find(new[] { ring }, others, double.NegativeInfinity).Count > 0)
            {
                crossing++;
                continue;
            }

            rings.Add(ring);
            others.Add(ring);
        }

        summary = $"Retaining Wall pinned the ground round {rings.Count:N0} of {ends.Count:N0} pointed or round wall ends" +
            $" ({level:N0} with both rails at the same height above the ground, {crossing:N0} where the ring would cross a line, {offMesh:N0} off the terrain).";
        return rings;
    }

    /// <summary>
    /// Heights under plan points from an indexed copy of a mesh. Rhino's own line intersection tests every face,
    /// tens of milliseconds a point on a million-face terrain, and a ring takes a dozen points per end.
    /// </summary>
    private sealed class GroundSampler
    {
        private readonly RhinoMesh _mesh;
        private readonly double _tolerance;
        private readonly MeshHeightProjector? _projector;

        public GroundSampler(RhinoMesh mesh, double tolerance)
        {
            _mesh = mesh;
            _tolerance = tolerance;
            if (RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh extracted, out _) && !extracted.IsEmpty)
                _projector = new MeshHeightProjector(extracted.Vertices, extracted.VertexCount, extracted.Faces, extracted.FaceCount);
        }

        public bool TryGround(Point3d point, out double ground)
        {
            ground = 0.0;
            if (_projector != null &&
                _projector.TryProjectZ(point.X, point.Y, point.Z, _tolerance, out ground, out _))
                return true;

            // Off the indexed copy, or where it cannot choose (a steep face over the point): ask Rhino.
            if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(_mesh, point, _tolerance, out Point3d projected))
                return false;

            ground = projected.Z;
            return true;
        }
    }
}
