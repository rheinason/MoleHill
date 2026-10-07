using MoleHill.Core.Engine;
using Grasshopper.Kernel;
using Rhino.Geometry;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Grade terrain to a planar surface defined by 3D boundary curves, with controlled slope transitions.
/// Reports cut/fill volumes. Per-pad settings via matching-length lists. Spec-driven
/// (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class GradePadComponent : RegistryTerrainComponent
{
    private const double MinRepresentablePlaneNormalZ = 1e-3;

    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public GradePadComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.GradePad.png");

    public override Guid ComponentGuid => new("B3C4D5E6-F7A8-9012-CDEF-123456789012");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Grade Pad",
        Nick = "GradePad",
        Description = "Grade terrain within boundary curves to a planar surface with slope transitions. Flat curves make flat pads; 3D curves define angled pads.",
        SubCategory = "Grading",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Existing terrain mesh. Optional when Terrain is supplied.", optional: true),
            GhPort.Curve("Boundaries", "B", "Closed curves defining pad areas. Flat curves make flat pads; 3D curves define the finished pad plane.", optional: false),
            GhPort.Number("Slope Angle", "S", "Cut slope angle in degrees per boundary (terrain above the pad). Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Number("Max Distance", "D", "Max horizontal transition distance per boundary. 0 = auto. Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Curve("Lock Curves", "L", "Curves whose edges are preserved as constrained segments in the remesh."),
            GhPort.Integer("Corner Segments", "CS", "Arc vertices per convex corner. 0 = sharp ridge (hip), >=1 = rounded fan. Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Number("Fill Slope", "Sf", "Fill slope angle in degrees per boundary (terrain below the pad). 0 = same as cut slope. Shorter lists repeat last value.", access: GH_ParamAccess.list),
            GhPort.Generic("Terrain", "T", "Optional MoleHill Terrain input. When supplied, its constraints and metadata are carried to the appended Terrain output.", optional: true),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Modified terrain mesh."),
            GhPort.Number("Cut Volume", "Cv", "Total excavation volume."),
            GhPort.Number("Fill Volume", "Fv", "Total embankment volume."),
            GhPort.Number("Net Volume", "Nv", "Cut - Fill (positive = net cut)."),
            GhPort.Generic("Terrain", "T", "Terrain-aware graded output when a Terrain input was supplied."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(7, out var typedTerrain) ? typedTerrain : null;
        if (!ctx.TryGetMesh(0, out var mesh) && sourceTerrain == null)
            return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();

        var boundaryCurves = ctx.GetCurves(1);
        if (boundaryCurves.Count == 0)
            return;

        var slopeAngles = ctx.GetNumbers(2);
        var maxDists = ctx.GetNumbers(3);
        var lockCurves = ctx.GetCurves(4);
        if (lockCurves.Count == 0 && sourceTerrain != null)
            lockCurves = sourceTerrain.Breaklines.Select(curve => curve.DuplicateCurve()).ToList();
        var cornerSegmentsList = ctx.GetInts(5);
        var fillSlopeAngles = ctx.GetNumbers(6);

        double tolerance = ctx.Tolerance;
        int faceCount = mesh.Faces.Count;
        if (faceCount == 0)
        {
            ctx.Warn("Input mesh has no faces.");
            return;
        }

        var vertices = GhSolveContext.ToFlatVertices(mesh);
        int vertexCount = mesh.Vertices.Count;
        if (!ctx.TryToFlatFaces(mesh, out var faces))
            return;

        // Convert boundary curves with per-pad settings
        var pads = new List<PadGrader.PadBoundary>();
        int padIdx = 0;
        foreach (var crv in boundaryCurves)
        {
            if (crv == null) { padIdx++; continue; }

            if (!crv.IsClosed)
            {
                ctx.Warn("Boundary curve is not closed. Skipping.");
                padIdx++;
                continue;
            }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl))
                {
                    ctx.Warn("Could not tessellate boundary curve. Skipping.");
                    padIdx++;
                    continue;
                }
            }

            if (pl.Count < 3) { padIdx++; continue; }

            int plCount = pl.Count;
            if (pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;

            double slope = GhSolveContext.ListValue(slopeAngles, padIdx, 33.0);
            double dist = GhSolveContext.ListValue(maxDists, padIdx, 0.0);
            int cornerSegs = GhSolveContext.ListValue(cornerSegmentsList, padIdx, 0);
            double fillSlope = GhSolveContext.ListValue(fillSlopeAngles, padIdx, 0.0);
            double stitchApronDistance = ctx.FromMeters(0.5);
            if (!TryCreatePadBoundary(pl, plCount, slope, dist, cornerSegs, stitchApronDistance, fillSlope, out var pad, out string? warning))
            {
                ctx.Warn(warning ?? "Boundary curve did not define a stable pad plane. Skipping.");
                padIdx++;
                continue;
            }

            pads.Add(pad!);
            padIdx++;
        }

        if (pads.Count == 0)
        {
            ctx.Error("No valid boundary curves.");
            return;
        }

        // Convert lock curves
        PadGrader.LockCurve[]? locks = null;
        if (lockCurves.Count > 0)
        {
            var lockList = new List<PadGrader.LockCurve>();
            foreach (var crv in lockCurves)
            {
                if (crv == null) continue;

                Polyline pl;
                if (!crv.TryGetPolyline(out pl))
                {
                    var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                    if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) continue;
                }

                if (pl.Count < 2) continue;

                var xyVerts = new double[pl.Count * 2];
                for (int i = 0; i < pl.Count; i++)
                {
                    xyVerts[i * 2] = pl[i].X;
                    xyVerts[i * 2 + 1] = pl[i].Y;
                }
                lockList.Add(new PadGrader.LockCurve(xyVerts, pl.Count));
            }
            if (lockList.Count > 0) locks = lockList.ToArray();
        }

        GradeOutcome gradeOutcome = PadGrader.Grade(new PadGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            Pads = pads.ToArray(),
            LockCurves = locks ?? Array.Empty<PadGrader.LockCurve>(),
        });
        string? errorMessage = gradeOutcome.ErrorMessage;
        var result = gradeOutcome.Result;

        if (result == null)
        {
            ctx.Error(errorMessage ?? "Grading failed.");
            return;
        }

        if (errorMessage != null)
            ctx.Warn(errorMessage);

        Mesh outputMesh = GhSolveContext.BuildMesh(result.Vertices, result.Faces);
        ctx.SetData(0, outputMesh);
        ctx.SetData(1, result.CutVolume);
        ctx.SetData(2, result.FillVolume);
        ctx.SetData(3, result.NetVolume);
        if (sourceTerrain != null)
        {
            var gradedTerrain = new MoleHillTerrainData(outputMesh, sourceTerrain.Breaklines, sourceTerrain.Regions,
                sourceTerrain.Name, sourceTerrain.Key, sourceTerrain.Revision, sourceTerrain.Diagnostics,
                sourceTerrain.UnitSystem, sourceTerrain.MetersPerModelUnit, sourceTerrain.LocalToWorld,
                sourceTerrain.HasProjectBaseTransform);
            ctx.SetData(4, new MoleHillTerrainGoo(gradedTerrain));
        }
    }

    internal static bool TryCreatePadBoundary(
        Polyline polyline,
        int vertexCount,
        double slopeAngle,
        double maxDistance,
        int cornerFanSegments,
        double stitchApronDistance,
        double fillSlopeAngle,
        out PadGrader.PadBoundary? pad,
        out string? warning)
    {
        pad = null;
        warning = null;

        var boundaryVertices = new double[vertexCount * 3];
        var points = new Point3d[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            Point3d point = polyline[i];
            points[i] = point;
            boundaryVertices[i * 3] = point.X;
            boundaryVertices[i * 3 + 1] = point.Y;
            boundaryVertices[i * 3 + 2] = point.Z;
        }

        if (!TryGetPlaneCoefficients(points, out double planeXCoeff, out double planeYCoeff, out double planeConstant, out warning))
            return false;

        pad = PadGrader.PadBoundary.CreatePlanar(
            boundaryVertices,
            vertexCount,
            planeXCoeff,
            planeYCoeff,
            planeConstant,
            slopeAngle,
            maxDistance,
            cornerFanSegments,
            stitchApronDistance,
            fillSlopeAngle);
        return true;
    }

    private static bool TryGetPlaneCoefficients(
        IReadOnlyList<Point3d> points,
        out double planeXCoeff,
        out double planeYCoeff,
        out double planeConstant,
        out string? warning)
    {
        planeXCoeff = 0.0;
        planeYCoeff = 0.0;
        planeConstant = 0.0;
        warning = null;

        if (points.Count < 3)
        {
            warning = "Boundary curve must have at least 3 vertices to define a pad plane.";
            return false;
        }

        PlaneFitResult fit = Plane.FitPlaneToPoints(points, out Plane plane);
        if (fit == PlaneFitResult.Failure || Math.Abs(plane.Normal.Z) < MinRepresentablePlaneNormalZ)
        {
            warning = "Boundary curve does not define a stable planar pad.";
            return false;
        }

        if (plane.Normal.Z < 0)
            plane.Flip();

        planeXCoeff = -plane.Normal.X / plane.Normal.Z;
        planeYCoeff = -plane.Normal.Y / plane.Normal.Z;
        planeConstant = plane.Origin.Z + (plane.Normal.X * plane.Origin.X + plane.Normal.Y * plane.Origin.Y) / plane.Normal.Z;
        return true;
    }
}
