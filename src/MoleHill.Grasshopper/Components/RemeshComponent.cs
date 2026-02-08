using Grasshopper.Kernel;
using Rhino.Geometry;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
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

        // Build vertex + segment lists
        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        for (int i = 0; i < vertexCount; i++)
        {
            xyList.Add(origVerts[i * 3]);
            xyList.Add(origVerts[i * 3 + 1]);
            zList.Add(origVerts[i * 3 + 2]);
        }

        // Add constraint curve vertices + segments
        foreach (var crv in constraints)
        {
            if (crv == null) continue;

            Polyline pl;
            if (!crv.TryGetPolyline(out pl))
            {
                var polyCrv = crv.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (polyCrv == null || !polyCrv.TryGetPolyline(out pl)) continue;
            }

            if (pl.Count < 2) continue;

            var crvIndices = new int[pl.Count];
            for (int i = 0; i < pl.Count; i++)
            {
                double px = pl[i].X, py = pl[i].Y;
                int near = PadGrader.FindNearVertex(xyList, px, py, 1e-6);
                if (near >= 0)
                {
                    crvIndices[i] = near;
                }
                else
                {
                    crvIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(PadGrader.InterpolateZ(origVerts, origFaces, faceCount, px, py));
                }
            }

            for (int i = 0; i < pl.Count - 1; i++)
            {
                if (crvIndices[i] != crvIndices[i + 1])
                    segList.Add((crvIndices[i], crvIndices[i + 1]));
            }

            if (crv.IsClosed && pl.Count >= 3)
            {
                int last = pl.Count - 1;
                if (pl[0].DistanceTo(pl[last]) >= tolerance && crvIndices[0] != crvIndices[last])
                {
                    segList.Add((crvIndices[last], crvIndices[0]));
                }
            }
        }

        // Triangulate with fallback
        int totalVerts = zList.Count;

        var triMesh = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            maxArea, minAngle,
            out string? triWarning);

        if (triMesh == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, triWarning ?? "Triangulation failed.");
            return;
        }

        if (triWarning != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, triWarning);

        if (triMesh.Triangles.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Triangulation produced 0 triangles.");
            return;
        }

        // Build output mesh
        var outVerts = triMesh.Vertices.ToList();
        var outTris = triMesh.Triangles.ToList();
        int outVertCount = outVerts.Count;
        int outFaceCount = outTris.Count;

        var outMesh = new RhinoMesh();
        outMesh.Vertices.Capacity = outVertCount;
        outMesh.Faces.Capacity = outFaceCount;

        var idToIdx = new Dictionary<int, int>(outVertCount);
        for (int i = 0; i < outVertCount; i++)
        {
            var mv = outVerts[i];
            idToIdx[mv.ID] = i;

            double z;
            if (mv.ID >= 0 && mv.ID < totalVerts)
            {
                z = zList[mv.ID];
            }
            else
            {
                z = PadGrader.InterpolateZ(origVerts, origFaces, faceCount, mv.X, mv.Y);
            }

            outMesh.Vertices.Add(mv.X, mv.Y, z);
        }

        for (int i = 0; i < outFaceCount; i++)
        {
            var tri = outTris[i];
            outMesh.Faces.AddFace(
                idToIdx.GetValueOrDefault(tri.GetVertex(0).ID, 0),
                idToIdx.GetValueOrDefault(tri.GetVertex(1).ID, 0),
                idToIdx.GetValueOrDefault(tri.GetVertex(2).ID, 0));
        }

        outMesh.Normals.ComputeNormals();
        outMesh.UnifyNormals();
        outMesh.Compact();

        DA.SetData(0, outMesh);
        DA.SetData(1, outFaceCount);
        DA.SetData(2, outVertCount);
    }
}
