using Grasshopper.Kernel;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Laplacian smoothing of mesh vertices, optionally within boundary curves. Spec-driven
/// (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class MeshSmoothComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public MeshSmoothComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Smoothing.png");

    public override Guid ComponentGuid => new("F6A7B8C9-D0E1-2345-6789-ABCDEF012345");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Mesh Smooth",
        Nick = "Smooth",
        Description = "Smooth mesh vertices using Laplacian smoothing. Optionally constrain to boundary regions and fix breakline vertices.",
        SubCategory = "Surface",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Triangle mesh to smooth. Optional when Terrain is supplied.", optional: true),
            GhPort.Curve("Boundaries", "B", "Closed curves defining regions to smooth. Leave empty to smooth the whole interior."),
            GhPort.Integer("Iterations", "I", "Number of smoothing passes.", @default: 3),
            GhPort.Number("Strength", "S", "Smoothing strength (0-1). When boundaries are provided, one value per boundary. When no boundaries, the first value sets global strength (default 0.5).", access: GH_ParamAccess.list),
            GhPort.Curve("Breaklines", "BL", "Curves along ridges or edges whose vertices should resist smoothing."),
            GhPort.Number("Fixity", "F", "How fixed breakline vertices are (0 = free, 1 = fully fixed).", @default: 1.0),
            GhPort.Generic("Terrain", "T", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", optional: true),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Smoothed mesh."),
            GhPort.Generic("Terrain", "T", "Terrain-aware smoothed output when a Terrain input was supplied."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        MoleHillTerrainData? sourceTerrain = ctx.TryGetTerrain(6, out var typedTerrain) ? typedTerrain : null;
        if (!ctx.TryGetMesh(0, out var mesh) && sourceTerrain == null)
            return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();

        var boundaryCurves = ctx.GetCurves(1);
        int iterations = ctx.GetInt(2, 3);
        var strengths = ctx.GetNumbers(3);
        var breaklineCurves = ctx.GetCurves(4);
        if (breaklineCurves.Count == 0 && sourceTerrain != null)
            breaklineCurves = sourceTerrain.Breaklines.Select(curve => curve.DuplicateCurve()).ToList();
        double breaklineFixity = ctx.GetNumber(5, 1.0);

        if (iterations <= 0)
        {
            ctx.SetData(0, mesh);
            EmitTerrain(ctx, sourceTerrain, mesh);
            return;
        }

        double tolerance = ctx.Tolerance;
        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            ctx.Warn("Input mesh has no faces.");
            return;
        }

        if (!mesh.IsValid)
            ctx.Warn("Input mesh is already invalid (likely degenerate faces from triangulation). Smoothing will not fix this.");

        if (!ctx.TryExtractMesh(mesh, out var extracted))
            return;
        double[] vertices = extracted.Vertices;
        vertexCount = extracted.VertexCount;
        int[] faces = extracted.Faces;
        faceCount = extracted.FaceCount;

        // Convert boundary curves with per-boundary strength
        var boundaries = new List<(double[] xyVerts, int vertCount, double strength)>();
        int bIdx = 0;
        foreach (var crv in boundaryCurves)
        {
            if (crv == null || !crv.IsClosed) { bIdx++; continue; }

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) { bIdx++; continue; }
            }

            if (pl.Count < 3) { bIdx++; continue; }

            int plCount = pl.Count;
            if (pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;

            var xyVerts = new double[plCount * 2];
            for (int i = 0; i < plCount; i++)
            {
                xyVerts[i * 2] = pl[i].X;
                xyVerts[i * 2 + 1] = pl[i].Y;
            }

            double s = strengths.Count > 0
                ? strengths[Math.Min(bIdx, strengths.Count - 1)]
                : 0.5;

            boundaries.Add((xyVerts, plCount, s));
            bIdx++;
        }

        // Warn only if curves were supplied but none were valid closed curves
        if (boundaryCurves.Count > 0 && boundaries.Count == 0)
        {
            ctx.Warn("No valid boundary curves. Curves must be closed.");
            ctx.SetData(0, mesh);
            EmitTerrain(ctx, sourceTerrain, mesh);
            return;
        }

        // Global strength used when no boundaries are provided
        double globalStrength = strengths.Count > 0
            ? Math.Max(0, Math.Min(1, strengths[0]))
            : 0.5;

        // Convert breakline curves (open or closed)
        var breaklineData = new List<MeshSmoother.BreaklinePolyline>();
        foreach (var crv in breaklineCurves)
        {
            if (crv == null) continue;

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) continue;
            }

            if (pl.Count < 2) continue;

            int plCount = pl.Count;
            bool isClosed = crv.IsClosed || (plCount > 2 && pl[0].DistanceTo(pl[plCount - 1]) <= tolerance);
            if (isClosed && pl[0].DistanceTo(pl[plCount - 1]) < tolerance)
                plCount--;

            var xyPts = new double[plCount * 2];
            for (int i = 0; i < plCount; i++)
            {
                xyPts[i * 2] = pl[i].X;
                xyPts[i * 2 + 1] = pl[i].Y;
            }

            breaklineData.Add(new MeshSmoother.BreaklinePolyline(xyPts, plCount, isClosed));
        }

        var smoothed = MeshSmoother.Smooth(
            vertices, vertexCount,
            faces, faceCount,
            boundaries.ToArray(),
            globalStrength,
            breaklineData.ToArray(),
            breaklineFixity,
            tolerance,
            iterations);

        Mesh output = GhSolveContext.BuildMesh(smoothed, faces);
        ctx.SetData(0, output);
        EmitTerrain(ctx, sourceTerrain, output);
    }

    private static void EmitTerrain(GhSolveContext ctx, MoleHillTerrainData? source, Mesh mesh)
    {
        if (source == null) return;
        var terrain = new MoleHillTerrainData(mesh, source.Breaklines, source.Regions,
            source.Name, source.Key, source.Revision, source.Diagnostics, source.UnitSystem,
            source.MetersPerModelUnit, source.LocalToWorld, source.HasProjectBaseTransform);
        ctx.SetData(1, new MoleHillTerrainGoo(terrain));
    }
}
