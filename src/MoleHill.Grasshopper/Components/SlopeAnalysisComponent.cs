using Grasshopper.Kernel;
using MoleHill.Core.Analysis;
using MoleHill.Grasshopper.Registry;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Color-code a mesh by per-face slope angle/percent. Spec-driven (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class SlopeAnalysisComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public SlopeAnalysisComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.SlopeAnalysis.png");

    public override Guid ComponentGuid => new("A2B3C4D5-E6F7-8901-BCDE-F12345678901");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "Slope Analysis",
        Nick = "Slope",
        Description = "Color-code a mesh by per-face slope. Green = flat, yellow = moderate, red = steep.",
        SubCategory = "Analysis",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Terrain mesh to analyze."),
            GhPort.Integer("Unit", "U", "Slope unit: 0=ratio, 1=percent, 2=degrees, 3=promille.", @default: 1),
            GhPort.Number("Low", "L", "Low end of color range (green). Default 0.", @default: 0.0),
            GhPort.Number("High", "H", "High end of color range (red). 0 = auto from data.", @default: 0.0),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Colored Mesh", "M", "Mesh colored by slope."),
            GhPort.Number("Slopes", "S", "Per-face slope values.", access: GH_ParamAccess.list),
            GhPort.Number("Min", "Mn", "Minimum slope."),
            GhPort.Number("Max", "Mx", "Maximum slope."),
            GhPort.Number("Average", "Av", "Area-weighted average slope."),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        if (!ctx.TryGetMesh(0, out var mesh))
            return;

        int unit = ctx.GetInt(1, 1);
        if (unit < (int)SlopeAnalyzer.SlopeUnit.Ratio || unit > (int)SlopeAnalyzer.SlopeUnit.Promille) unit = 1;

        double colorLow = ctx.GetNumber(2, 0.0);
        double colorHigh = ctx.GetNumber(3, 0.0);

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            ctx.Warn("Mesh has no faces.");
            return;
        }

        var vertices = GhSolveContext.ToFlatVertices(mesh);
        if (!ctx.TryToFlatFaces(mesh, out var faces))
            return;

        // A High of 0 (or below Low) still means "auto from data", which now fits the trimmed slope
        // distribution rather than the single steepest face.
        var result = SlopeAnalyzer.Analyze(vertices, vertexCount, faces, faceCount,
                                            (SlopeAnalyzer.SlopeUnit)unit,
                                            autoRange: colorHigh <= colorLow,
                                            requestedLow: colorLow,
                                            requestedHigh: colorHigh);

        // Build colored mesh with unshared vertices (flat shading)
        var coloredMesh = new Mesh();
        coloredMesh.Vertices.Capacity = faceCount * 3;
        coloredMesh.Faces.Capacity = faceCount;
        coloredMesh.VertexColors.Capacity = faceCount * 3;

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            int vi = f * 3;
            coloredMesh.Vertices.Add(vertices[i0 * 3], vertices[i0 * 3 + 1], vertices[i0 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i1 * 3], vertices[i1 * 3 + 1], vertices[i1 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i2 * 3], vertices[i2 * 3 + 1], vertices[i2 * 3 + 2]);

            coloredMesh.Faces.AddFace(vi, vi + 1, vi + 2);

            byte r = result.FaceColors[f * 3];
            byte g = result.FaceColors[f * 3 + 1];
            byte b = result.FaceColors[f * 3 + 2];

            var color = System.Drawing.Color.FromArgb(r, g, b);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
        }

        MeshNormalOrientation.UnifyAndComputeNormals(coloredMesh);
        coloredMesh.Compact();

        ctx.SetData(0, coloredMesh);
        ctx.SetDataList(1, result.Slopes);
        ctx.SetData(2, result.Min);
        ctx.SetData(3, result.Max);
        ctx.SetData(4, result.Average);
    }
}
