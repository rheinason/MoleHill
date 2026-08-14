using Grasshopper.Kernel;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Registry;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Grade terrain to a sloping support surface and generate separate stair Breps from a target riser
/// height. Spec-driven (<see cref="RegistryTerrainComponent"/>).
/// </summary>
public sealed class InSituStairComponent : RegistryTerrainComponent
{
    private static readonly GhComponentSpec ComponentSpec = BuildSpec();

    public InSituStairComponent() : base(ComponentSpec)
    {
    }

    protected override GhComponentSpec Spec => ComponentSpec;

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.GradePath.png");

    public override Guid ComponentGuid => new("2D5B1419-4E82-4D77-93A8-6760D14F4302");

    private static GhComponentSpec BuildSpec() => new()
    {
        Name = "In-Situ Stair",
        Nick = "InSituStair",
        Description = "Grade terrain to a sloping support surface and generate separate stair Breps from a target riser height.",
        SubCategory = "Grading",
        Inputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Existing terrain mesh."),
            GhPort.Geometry("Reference Surface", "R", "Mesh, Brep, extrusion, or surface describing the stair run."),
            GhPort.Number("Riser Height", "H", "Vertical rise per step. Defaults to 0.15 m in document units."),
            GhPort.Number("Slope Angle", "S", "Daylight slope angle in degrees.", @default: 33.0),
            GhPort.Number("Max Distance", "D", "Maximum grading reach away from the stair. 0 = unlimited.", @default: 0.0),
        },
        Outputs = new[]
        {
            GhPort.Mesh("Mesh", "M", "Terrain mesh graded to the support surface beneath the stair."),
            GhPort.Brep("Stair Breps", "B", "Generated stair Breps that remain visible above the graded support surface."),
            GhPort.Number("Tread Depths", "Td", "Derived tread depth for each interpreted stair surface.", access: GH_ParamAccess.list),
            GhPort.Integer("Step Counts", "Sc", "Generated tread count for each interpreted stair surface.", access: GH_ParamAccess.list),
            GhPort.Number("Cut Volume", "Cv", "Total excavation volume."),
            GhPort.Number("Fill Volume", "Fv", "Total embankment volume."),
            GhPort.Number("Net Volume", "Nv", "Cut - Fill (positive = net cut)."),
            GhPort.Text("Warning", "W", "Surface interpretation or grading warning text.", access: GH_ParamAccess.item),
        },
        Solve = Solve,
    };

    private static void Solve(GhSolveContext ctx)
    {
        if (!ctx.TryGetMesh(0, out var mesh))
            return;

        var referenceGeometry = ctx.GetGeometry(1);
        if (referenceGeometry.Count == 0)
            return;

        double riserHeight = ctx.GetNumber(2, ctx.FromMeters(0.15));
        double slopeAngle = ctx.GetNumber(3, 33.0);
        double maxDistance = ctx.GetNumber(4, 0.0);

        if (riserHeight <= 0)
        {
            ctx.Error("Riser Height must be positive.");
            return;
        }

        var referenceMeshes = new List<Mesh>();
        foreach (var geometry in referenceGeometry)
            referenceMeshes.AddRange(ToReferenceMeshes(geometry));

        if (referenceMeshes.Count == 0)
        {
            ctx.Error("No usable reference surface geometry was supplied.");
            return;
        }

        if (!InSituStairReferenceBuilder.TryBuild(
                referenceMeshes,
                riserHeight,
                slopeAngle,
                maxDistance,
                out var stairBuild,
                out string? errorMessage))
        {
            ctx.Error(errorMessage ?? "Could not derive an in-situ stair from the reference surface.");
            return;
        }

        if (!TryExtractMesh(mesh, out var vertices, out var faces, out errorMessage))
        {
            ctx.Error(errorMessage ?? "Could not extract terrain mesh data.");
            return;
        }

        double[] currentVertices = vertices;
        int currentVertexCount = mesh.Vertices.Count;
        int[] currentFaces = faces;
        int currentFaceCount = mesh.Faces.Count;
        double cutVolume = 0.0;
        double fillVolume = 0.0;
        var warnings = new List<string>(stairBuild!.Warnings);

        foreach (var stairReference in stairBuild.References)
        {
            var result = SurfaceStripGrader.Grade(
                currentVertices,
                currentVertexCount,
                currentFaces,
                currentFaceCount,
                stairReference.SupportSurface,
                out string? gradingWarning);

            if (result == null)
            {
                ctx.Error(gradingWarning ?? "In-situ stair grading failed.");
                return;
            }

            currentVertices = result.Vertices;
            currentVertexCount = result.VertexCount;
            currentFaces = result.Faces;
            currentFaceCount = result.FaceCount;
            cutVolume += result.CutVolume;
            fillVolume += result.FillVolume;
            if (!string.IsNullOrWhiteSpace(gradingWarning))
                warnings.Add(gradingWarning);
        }

        string warning = string.Join(" | ", warnings.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (!string.IsNullOrWhiteSpace(warning))
            ctx.Warn(warning);

        ctx.SetData(0, GhSolveContext.BuildMesh(currentVertices, currentFaces));
        ctx.SetDataList(1, stairBuild.References.SelectMany(reference => reference.StairBreps));
        ctx.SetDataList(2, stairBuild.References.Select(reference => reference.TreadDepth));
        ctx.SetDataList(3, stairBuild.References.Select(reference => reference.StepCount));
        ctx.SetData(4, cutVolume);
        ctx.SetData(5, fillVolume);
        ctx.SetData(6, cutVolume - fillVolume);
        ctx.SetData(7, warning);
    }

    private static bool TryExtractMesh(Mesh mesh, out double[] vertices, out int[] faces, out string? errorMessage)
    {
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        errorMessage = null;

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;
        if (faceCount == 0)
        {
            errorMessage = "Input mesh has no faces.";
            return false;
        }

        vertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            var pt = mesh.Vertices[i];
            vertices[i * 3] = pt.X;
            vertices[i * 3 + 1] = pt.Y;
            vertices[i * 3 + 2] = pt.Z;
        }

        faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            var face = mesh.Faces[i];
            if (face.IsQuad)
            {
                errorMessage = "Mesh contains quad faces. Only triangle meshes are supported.";
                return false;
            }

            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        return true;
    }

    private static IEnumerable<Mesh> ToReferenceMeshes(GeometryBase? geometry)
    {
        switch (geometry)
        {
            case Mesh mesh:
                yield return mesh.DuplicateMesh();
                break;
            case Brep brep:
                foreach (var brepMesh in Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>())
                    yield return brepMesh;
                break;
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep == null)
                    yield break;

                foreach (var extrusionMesh in Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>())
                    yield return extrusionMesh;
                break;
            case Surface surface:
                var surfaceBrep = surface.ToBrep();
                if (surfaceBrep == null)
                    yield break;

                foreach (var surfaceMesh in Mesh.CreateFromBrep(surfaceBrep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>())
                    yield return surfaceMesh;
                break;
        }
    }
}
