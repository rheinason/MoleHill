using Grasshopper.Kernel;
using MoleHill.Core.Grading;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public class InSituStairComponent : GH_Component
{
    public InSituStairComponent()
        : base(
            "In-Situ Stair",
            "InSituStair",
            "Grade terrain to a sloping support surface and generate separate stair Breps from a target riser height.",
            "MoleHill",
            "Grading")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.GradePath.png");

    public override Guid ComponentGuid => new("2D5B1419-4E82-4D77-93A8-6760D14F4302");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Existing terrain mesh.", GH_ParamAccess.item);
        pManager.AddGeometryParameter("Reference Surface", "R", "Mesh, Brep, extrusion, or surface describing the stair run.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Riser Height", "H", "Vertical rise per step.", GH_ParamAccess.item, 0.15);
        pManager.AddNumberParameter("Slope Angle", "S", "Daylight slope angle in degrees.", GH_ParamAccess.item, 33.0);
        pManager[3].Optional = true;
        pManager.AddNumberParameter("Max Distance", "D", "Maximum grading reach away from the stair. 0 = unlimited.", GH_ParamAccess.item, 0.0);
        pManager[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Terrain mesh graded to the support surface beneath the stair.", GH_ParamAccess.item);
        pManager.AddBrepParameter("Stair Breps", "B", "Generated stair Breps that remain visible above the graded support surface.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Tread Depths", "Td", "Derived tread depth for each interpreted stair surface.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Step Counts", "Sc", "Generated tread count for each interpreted stair surface.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Cut Volume", "Cv", "Total excavation volume.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Fill Volume", "Fv", "Total embankment volume.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Net Volume", "Nv", "Cut - Fill (positive = net cut).", GH_ParamAccess.item);
        pManager.AddTextParameter("Warning", "W", "Surface interpretation or grading warning text.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null)
            return;

        var referenceGeometry = new List<GeometryBase>();
        if (!DA.GetDataList(1, referenceGeometry) || referenceGeometry.Count == 0)
            return;

        double riserHeight = 0.15;
        double slopeAngle = 33.0;
        double maxDistance = 0.0;
        DA.GetData(2, ref riserHeight);
        DA.GetData(3, ref slopeAngle);
        DA.GetData(4, ref maxDistance);

        if (riserHeight <= 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Riser Height must be positive.");
            return;
        }

        var referenceMeshes = new List<Mesh>();
        foreach (var geometry in referenceGeometry)
            referenceMeshes.AddRange(ToReferenceMeshes(geometry));

        if (referenceMeshes.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No usable reference surface geometry was supplied.");
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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Could not derive an in-situ stair from the reference surface.");
            return;
        }

        if (!TryExtractMesh(mesh, out var vertices, out var faces, out errorMessage))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, errorMessage ?? "Could not extract terrain mesh data.");
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
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, gradingWarning ?? "In-situ stair grading failed.");
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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);

        var outMesh = new Mesh();
        outMesh.Vertices.Capacity = currentVertexCount;
        outMesh.Faces.Capacity = currentFaceCount;
        for (int i = 0; i < currentVertexCount; i++)
            outMesh.Vertices.Add(currentVertices[i * 3], currentVertices[i * 3 + 1], currentVertices[i * 3 + 2]);
        for (int i = 0; i < currentFaceCount; i++)
            outMesh.Faces.AddFace(currentFaces[i * 3], currentFaces[i * 3 + 1], currentFaces[i * 3 + 2]);

        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();

        DA.SetData(0, outMesh);
        DA.SetDataList(1, stairBuild.References.SelectMany(reference => reference.StairBreps));
        DA.SetDataList(2, stairBuild.References.Select(reference => reference.TreadDepth));
        DA.SetDataList(3, stairBuild.References.Select(reference => reference.StepCount));
        DA.SetData(4, cutVolume);
        DA.SetData(5, fillVolume);
        DA.SetData(6, cutVolume - fillVolume);
        DA.SetData(7, warning);
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
