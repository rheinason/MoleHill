using Grasshopper.Kernel;
using MoleHill.Core.Engine;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Grasshopper.Components;

/// <summary>
/// Refine a triangle mesh by re-triangulating with quality constraints.
/// Preserves constraint curves as mesh edges. Prepares meshes for smoothing.
/// </summary>
public class RemeshComponent : GH_Component
{
    public RemeshComponent()
        : base("Remesh", "Remesh",
               "Refine a triangle mesh with quality constraints. Adds vertices to improve triangle shape and density.",
               "MoleHill", "Surface")
    {
    }

    protected override System.Drawing.Bitmap? Icon =>
        MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.Remesh.png");

    public override Guid ComponentGuid => new("D5E6F7A8-B9C0-1234-EF01-345678901234");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Triangle mesh to refine.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Constraints", "C", "Curves to preserve as mesh edges (breaklines, boundaries).", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddNumberParameter("Edge Length", "E", "Maximum edge length. Controls point density. 0 = no constraint.", GH_ParamAccess.item, 0.0);
        pManager[2].Optional = true;
        pManager.AddNumberParameter("Max Area", "A", "Maximum triangle area. 0 = no constraint. Overrides Edge Length if both set.", GH_ParamAccess.item, 0.0);
        pManager[3].Optional = true;
        pManager.AddNumberParameter("Min Angle", "N", "Minimum triangle angle in degrees. 0 = no constraint.", GH_ParamAccess.item, 20.0);
        pManager[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Refined mesh.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Face Count", "F", "Number of faces.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Vertex Count", "V", "Number of vertices.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        RhinoMesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null) return;

        var constraints = new List<Curve>();
        DA.GetDataList(1, constraints);

        double edgeLength = 0.0, maxArea = 0.0, minAngle = 20.0;
        DA.GetData(2, ref edgeLength);
        DA.GetData(3, ref maxArea);
        DA.GetData(4, ref minAngle);

        // Convert edge length to max area (equilateral triangle: area = edge^2 * sqrt(3) / 4)
        if (edgeLength > 0 && maxArea <= 0)
            maxArea = edgeLength * edgeLength * Math.Sqrt(3.0) / 4.0;

        double tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        int vertexCount = mesh.Vertices.Count;
        int faceCount = mesh.Faces.Count;

        if (faceCount == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh has no faces.");
            return;
        }

        if (maxArea <= 0 && minAngle <= 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "No quality constraints set. Output equals input.");
            DA.SetData(0, mesh);
            DA.SetData(1, faceCount);
            DA.SetData(2, vertexCount);
            return;
        }

        // Extract mesh vertices
        var origVerts = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            var pt = mesh.Vertices[i];
            origVerts[i * 3] = pt.X;
            origVerts[i * 3 + 1] = pt.Y;
            origVerts[i * 3 + 2] = pt.Z;
        }

        var origFaces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            var face = mesh.Faces[i];
            if (face.IsQuad)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh contains quad faces. Only triangle meshes are supported.");
                return;
            }
            origFaces[i * 3] = face.A;
            origFaces[i * 3 + 1] = face.B;
            origFaces[i * 3 + 2] = face.C;
        }

        var remeshConstraints = new List<SurfaceRemesher.ConstraintPolyline>();
        foreach (var crv in constraints)
        {
            if (crv == null)
                continue;

            if (!TryGetPolyline(crv, tolerance, out var polyline))
                continue;

            remeshConstraints.Add(ToConstraintPolyline(polyline, crv.IsClosed));
        }

        var remeshResult = SurfaceRemesher.Remesh(
            origVerts,
            origFaces,
            remeshConstraints,
            new SurfaceRemesher.Options
            {
                Tolerance = tolerance,
                RequestedEdgeLength = edgeLength,
                MaxArea = maxArea,
                MinAngle = minAngle,
                ProtectSharpEdges = true
            });

        if (!remeshResult.Success)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, remeshResult.Warning ?? "Remesh could not preserve the mesh boundary or supplied constraints. Output equals input mesh.");
            DA.SetData(0, mesh);
            DA.SetData(1, faceCount);
            DA.SetData(2, vertexCount);
            return;
        }

        if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, remeshResult.Warning);

        var outMesh = BuildMesh(remeshResult.Vertices, remeshResult.Faces);
        int outVertCount = remeshResult.Vertices.Length / 3;
        int outFaceCount = remeshResult.Faces.Length / 3;

        DA.SetData(0, outMesh);
        DA.SetData(1, outFaceCount);
        DA.SetData(2, outVertCount);
    }

    private static bool TryGetPolyline(Curve curve, double tolerance, out Polyline polyline)
    {
        polyline = new Polyline();
        if (curve.TryGetPolyline(out polyline))
            return polyline.Count >= 2;

        var polyCurve = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
        return polyCurve != null && polyCurve.TryGetPolyline(out polyline) && polyline.Count >= 2;
    }

    private static SurfaceRemesher.ConstraintPolyline ToConstraintPolyline(Polyline polyline, bool isClosed)
    {
        var points = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            points[i * 3] = polyline[i].X;
            points[i * 3 + 1] = polyline[i].Y;
            points[i * 3 + 2] = polyline[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(points, polyline.Count, isClosed);
    }

    private static RhinoMesh BuildMesh(double[] vertices, int[] faces)
    {
        var mesh = new RhinoMesh();
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
