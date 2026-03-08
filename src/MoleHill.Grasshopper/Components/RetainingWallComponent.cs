using Grasshopper.Kernel;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Grading;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public class RetainingWallComponent : GH_Component
{
    public RetainingWallComponent()
        : base("Retaining Wall", "RetainWall",
               "Generate retaining wall solids from curve pairs and grade terrain mesh between wall rails.",
               "MoleHill", "Grading")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.GradePath.png");

    public override Guid ComponentGuid => new("D4E3F8A1-8B13-4CC1-8E56-5390F6D63C4D");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Terrain mesh (triangles only).", GH_ParamAccess.item);
        pManager.AddCurveParameter("Wall Curves", "C", "Unordered open 3D curves defining retaining walls.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Tolerance", "T", "Pairing threshold and sampling baseline.", GH_ParamAccess.item, 1.0);
        pManager.AddNumberParameter("Sharpness", "S", "Inside-strip profile sharpness (0-1).", GH_ParamAccess.item, 0.5);
        pManager.AddNumberParameter("Shoulder Width", "SW", "Outside transition width from wall rails.", GH_ParamAccess.item, 0.0);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Modified terrain mesh.", GH_ParamAccess.item);
        pManager.AddBrepParameter("Wall Breps", "W", "Wall solids.", GH_ParamAccess.list);
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

        double tolerance = 0.0;
        double sharpness = 0.5;
        double shoulderWidth = 0.0;
        DA.GetData(2, ref tolerance);
        DA.GetData(3, ref sharpness);
        DA.GetData(4, ref shoulderWidth);

        if (tolerance <= 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Tolerance must be > 0.");
            return;
        }

        sharpness = Math.Clamp(sharpness, 0.0, 1.0);
        shoulderWidth = Math.Max(0.0, shoulderWidth);

        if (mesh.Faces.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh has no faces.");
            return;
        }

        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            if (mesh.Faces[i].IsQuad)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh contains quad faces. Only triangle meshes are supported.");
                return;
            }
        }

        var plan = RetainingWallPlanner.Plan(wallCurves, tolerance);
        var reportOut = new List<string>(plan.Report.Count + plan.Walls.Count);
        foreach (var entry in plan.Report)
        {
            reportOut.Add(entry.ToString());
            switch (entry.Level)
            {
                case RetainingWallPlanner.ReportLevel.Error:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, entry.Message);
                    break;
                case RetainingWallPlanner.ReportLevel.Warning:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, entry.Message);
                    break;
                default:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, entry.Message);
                    break;
            }
        }

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;
        var vertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i * 3] = mesh.Vertices[i].X;
            vertices[i * 3 + 1] = mesh.Vertices[i].Y;
            vertices[i * 3 + 2] = mesh.Vertices[i].Z;
        }

        var faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            faces[i * 3] = mesh.Faces[i].A;
            faces[i * 3 + 1] = mesh.Faces[i].B;
            faces[i * 3 + 2] = mesh.Faces[i].C;
        }

        var wallBreps = new List<Brep>();
        foreach (var wall in plan.Walls)
        {
            var graded = RetainingWallMeshGrader.GradeSingleWall(
                vertices, vertexCount,
                faces, faceCount,
                wall.Strip,
                sharpness,
                shoulderWidth);

            if (!graded.GradeApplied || graded.MeshResult == null)
            {
                string msg = $"Pair ({wall.CurveA}, {wall.CurveB}): grading failed. {graded.WarningOrError ?? string.Empty}".Trim();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                reportOut.Add($"[Error] {msg}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(graded.WarningOrError))
            {
                string msg = $"Pair ({wall.CurveA}, {wall.CurveB}): {graded.WarningOrError}";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);
                reportOut.Add($"[Warning] {msg}");
            }

            vertices = graded.MeshResult.Vertices;
            vertexCount = graded.MeshResult.VertexCount;
            faces = graded.MeshResult.Faces;
            faceCount = graded.MeshResult.FaceCount;

            if (wall.Brep != null)
                wallBreps.Add(wall.Brep);
        }

        Mesh outMesh = new();
        outMesh.Vertices.Capacity = vertexCount;
        outMesh.Faces.Capacity = faceCount;
        for (int i = 0; i < vertexCount; i++)
            outMesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);
        for (int i = 0; i < faceCount; i++)
            outMesh.Faces.AddFace(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);
        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();

        DA.SetData(0, outMesh);
        DA.SetDataList(1, wallBreps);
        DA.SetDataList(2, plan.PairLines);
        DA.SetDataList(3, reportOut);
    }
}

