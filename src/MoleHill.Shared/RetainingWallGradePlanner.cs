using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Shared;

/// <summary>
/// Turns accepted wall rails into the grade definitions that batter the terrain away from them. Shared
/// by the Rhino modifier and the Grasshopper component so both hosts produce the same wall.
/// </summary>
/// <remarks>
/// Each rail grades one way only — away from its partner — so the wall face is never buried and terrain
/// is never pushed through it. That outward direction is not the rail curve's own plan normal, which is
/// why it is handed to the grader explicitly.
///
/// There is no per-side "off" switch. A rail sits at an authored elevation, so its side always resolves
/// to some slope; where the terrain already meets the rail the batter measures no difference and emits
/// nothing, which is what "this side needs no grading" looks like.
/// </remarks>
internal static class RetainingWallGradePlanner
{
    /// <summary>Per-side batter angles in degrees. Zero on any member inherits the shared pair.</summary>
    public readonly record struct SideSlopes(double CutAngleDeg, double FillAngleDeg)
    {
        public static SideSlopes Inherit => new(0.0, 0.0);
    }

    public sealed class Options
    {
        /// <summary>Shared fill angle in degrees, used where terrain sits below a rail.</summary>
        public double FillAngleDeg { get; init; } = 33.0;

        /// <summary>Shared cut angle in degrees. Zero inherits <see cref="FillAngleDeg"/>.</summary>
        public double CutAngleDeg { get; init; }

        public SideSlopes Toe { get; init; } = SideSlopes.Inherit;

        public SideSlopes Top { get; init; } = SideSlopes.Inherit;

        /// <summary>Maximum grading reach away from a rail. Zero is unlimited.</summary>
        public double MaxDistance { get; init; }

        /// <summary>Plan distance below which two rails are treated as coincident.</summary>
        public double Tolerance { get; init; } = 1e-6;
    }

    public static List<PathGrader.PathDefinition> Build(
        IReadOnlyList<RetainingWallPlannerCore.PlannedWall> walls,
        Options options)
    {
        if (walls is null) throw new ArgumentNullException(nameof(walls));
        if (options is null) throw new ArgumentNullException(nameof(options));

        double sharedCut = options.CutAngleDeg > 0.0 ? options.CutAngleDeg : options.FillAngleDeg;
        var grades = new List<PathGrader.PathDefinition>(walls.Count * 2);

        foreach (RetainingWallPlannerCore.PlannedWall wall in walls)
        {
            AddRailGrade(wall, wall.Rails.ToePoints, wall.Rails.TopPoints, options.Toe);
            AddRailGrade(wall, wall.Rails.TopPoints, wall.Rails.ToePoints, options.Top);
        }

        return grades;

        void AddRailGrade(
            RetainingWallPlannerCore.PlannedWall wall,
            Point3d[] rail,
            Point3d[] partner,
            SideSlopes side)
        {
            if (rail.Length < 2 || partner.Length < 2)
                return;

            var xy = new double[rail.Length * 2];
            var z = new double[rail.Length];
            double[] normals = BuildOutwardNormals(rail, partner, options.Tolerance);
            for (int i = 0; i < rail.Length; i++)
            {
                xy[i * 2] = rail[i].X;
                xy[(i * 2) + 1] = rail[i].Y;
                z[i] = rail[i].Z;
            }

            grades.Add(new PathGrader.PathDefinition(
                xy,
                z,
                rail.Length,
                width: 0.0,
                slopeAngleDeg: sharedCut,
                maxDistance: options.MaxDistance,
                fillSlopeAngleDeg: options.FillAngleDeg,
                isClosed: wall.Rails.IsClosed,
                leftCutSlopeAngleDeg: side.CutAngleDeg,
                leftFillSlopeAngleDeg: side.FillAngleDeg,
                outwardNormals: normals));
        }
    }

    /// <summary>
    /// One unit direction per rail vertex, pointing away from the partner rail. Where the two rails are
    /// plan-coincident — a truly vertical wall face — there is no across direction to use, so the rail's
    /// own plan normal stands in, flipped to agree with the run's prevailing outward direction.
    /// </summary>
    internal static double[] BuildOutwardNormals(Point3d[] rail, Point3d[] partner, double tolerance)
    {
        var normals = new double[rail.Length * 2];
        double snapTolerance = Math.Max(tolerance, 1e-12);

        // First pass: the honest direction wherever the rails are actually apart in plan.
        var resolved = new bool[rail.Length];
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < rail.Length; i++)
        {
            Point3d across = partner[Math.Min(i, partner.Length - 1)];
            double dx = rail[i].X - across.X;
            double dy = rail[i].Y - across.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= snapTolerance)
                continue;

            normals[i * 2] = dx / length;
            normals[(i * 2) + 1] = dy / length;
            resolved[i] = true;
            sumX += normals[i * 2];
            sumY += normals[(i * 2) + 1];
        }

        // Second pass: fill the vertical-face stations from the rail's own normal, oriented to match.
        for (int i = 0; i < rail.Length; i++)
        {
            if (resolved[i])
                continue;

            int next = Math.Min(i + 1, rail.Length - 1);
            int prev = Math.Max(i - 1, 0);
            double tx = rail[next].X - rail[prev].X;
            double ty = rail[next].Y - rail[prev].Y;
            double tangentLength = Math.Sqrt((tx * tx) + (ty * ty));
            if (tangentLength <= snapTolerance)
            {
                normals[i * 2] = 1.0;
                normals[(i * 2) + 1] = 0.0;
                continue;
            }

            double nx = -ty / tangentLength;
            double ny = tx / tangentLength;
            if ((nx * sumX) + (ny * sumY) < 0.0)
            {
                nx = -nx;
                ny = -ny;
            }

            normals[i * 2] = nx;
            normals[(i * 2) + 1] = ny;
        }

        return normals;
    }
}
