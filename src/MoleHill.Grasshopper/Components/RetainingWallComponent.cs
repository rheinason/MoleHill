using Grasshopper.Kernel;
using MoleHill.Core.Engine;
using MoleHill.Shared;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public class RetainingWallComponent : GH_Component
{
    public RetainingWallComponent()
        : base("Retaining Wall", "RetainWall",
               "Generate retaining wall solids from curve pairs and insert their rails as hard terrain breaklines.",
               "MoleHill", "Grading")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.RetainingWall.png");

    public override Guid ComponentGuid => new("D4E3F8A1-8B13-4CC1-8E56-5390F6D63C4D");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Terrain mesh (triangles only).", GH_ParamAccess.item);
        pManager.AddCurveParameter("Wall Curves", "C", "Unordered open or closed 3D curves defining retaining walls.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Max Wall Width", "W", "Maximum expected spacing between paired wall rails.", GH_ParamAccess.item, 1.0);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Terrain mesh with accepted wall rails inserted as hard breaklines.", GH_ParamAccess.item);
        pManager.AddBrepParameter("Wall Breps", "W", "Solid retaining wall Breps.", GH_ParamAccess.list);
        pManager.AddLineParameter("Pairs", "P", "Preview lines connecting matched pairs.", GH_ParamAccess.list);
        pManager.AddTextParameter("Report", "R", "Info / warning / error report entries.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null)
            return;

        var wallCurves = new List<Curve>();
        if (!DA.GetDataList(1, wallCurves) || wallCurves.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No wall curves provided.");
            DA.SetData(0, mesh);
            DA.SetDataList(1, Array.Empty<Brep>());
            DA.SetDataList(2, Array.Empty<Line>());
            DA.SetDataList(3, new[] { "[Warning] No wall curves provided." });
            return;
        }

        double maxWallWidth = 0.0;
        DA.GetData(2, ref maxWallWidth);
        if (maxWallWidth <= 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max wall width must be > 0.");
            return;
        }

        if (!TryExtractTriangleMesh(mesh, out double[] vertices, out int[] faces, out string? meshError))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, meshError ?? "Could not extract a triangle mesh.");
            return;
        }

        double modelTolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
        var plan = RetainingWallPlannerCore.Plan(
            wallCurves,
            Math.Max(modelTolerance, maxWallWidth),
            curveParsingTolerance: modelTolerance);
        var reportOut = new List<string>(plan.Report.Count + 2);
        foreach (var entry in plan.Report)
        {
            reportOut.Add(entry.ToString());
            switch (entry.Level)
            {
                case RetainingWallPlannerCore.ReportLevel.Error:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, entry.Message);
                    break;
                case RetainingWallPlannerCore.ReportLevel.Warning:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, entry.Message);
                    break;
                default:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, entry.Message);
                    break;
            }
        }

        if (plan.Walls.Count == 0)
        {
            DA.SetData(0, mesh);
            DA.SetDataList(1, Array.Empty<Brep>());
            DA.SetDataList(2, plan.PairLines);
            DA.SetDataList(3, reportOut);
            return;
        }

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(plan.Walls.Count * 2);
        var wallBreps = new List<Brep>(plan.Walls.Count);
        foreach (var wall in plan.Walls)
        {
            if (wall.Brep != null)
                wallBreps.Add(wall.Brep);

            AddWallConstraints(constraints, wall.Rails);
        }

        SurfaceRemesher.Result remeshResult = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = modelTolerance,
                RequestedEdgeLength = 0.0,
                MaxArea = 0.0,
                MinAngle = 0.0,
                ProtectSharpEdges = true,
                ConstraintInsertionOnly = true
            });

        Mesh outMesh = mesh;
        if (remeshResult.Success)
        {
            outMesh = BuildMesh(remeshResult.Vertices, remeshResult.Faces);
            if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, remeshResult.Warning);
                reportOut.Add($"[Warning] {remeshResult.Warning}");
            }
        }
        else
        {
            string warning = remeshResult.Warning ?? "Retaining wall breakline remesh failed. Output mesh equals input mesh.";
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
            reportOut.Add($"[Warning] {warning}");
        }

        DA.SetData(0, outMesh);
        DA.SetDataList(1, wallBreps);
        DA.SetDataList(2, plan.PairLines);
        DA.SetDataList(3, reportOut);
    }

    private static bool TryExtractTriangleMesh(Mesh mesh, out double[] vertices, out int[] faces, out string? error)
    {
        error = null;
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        if (mesh.Faces.Count == 0)
        {
            error = "Input mesh has no faces.";
            return false;
        }

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;
        vertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i * 3] = mesh.Vertices[i].X;
            vertices[i * 3 + 1] = mesh.Vertices[i].Y;
            vertices[i * 3 + 2] = mesh.Vertices[i].Z;
        }

        faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            MeshFace face = mesh.Faces[i];
            if (face.IsQuad)
            {
                error = "Mesh contains quad faces. Only triangle meshes are supported.";
                return false;
            }

            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        return true;
    }

    private static void AddWallConstraints(List<SurfaceRemesher.ConstraintPolyline> constraints, RetainingWallPlannerCore.WallRails rails)
    {
        int minimum = rails.IsClosed ? 3 : 2;
        if (rails.ToePoints.Length >= minimum)
            constraints.Add(BuildConstraint(rails.ToePoints, rails.IsClosed));
        if (rails.TopPoints.Length >= minimum)
            constraints.Add(BuildConstraint(rails.TopPoints, rails.IsClosed));
    }

    private static SurfaceRemesher.ConstraintPolyline BuildConstraint(Point3d[] railPoints, bool isClosed)
    {
        int count = railPoints.Length;
        var points = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            points[i * 3] = railPoints[i].X;
            points[i * 3 + 1] = railPoints[i].Y;
            points[i * 3 + 2] = railPoints[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(points, count, isClosed, PreserveInputElevation: true);
    }

    private static Mesh BuildMesh(double[] vertices, int[] faces)
    {
        var mesh = new Mesh();
        mesh.Vertices.Capacity = vertices.Length / 3;
        mesh.Faces.Capacity = faces.Length / 3;

        for (int i = 0; i < vertices.Length / 3; i++)
            mesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);

        for (int i = 0; i < faces.Length / 3; i++)
            mesh.Faces.AddFace(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);

        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        return mesh;
    }
}
