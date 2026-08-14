using Grasshopper.Kernel;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Grasshopper.Registry;

/// <summary>
/// Per-solve helper passed to a <see cref="GhComponentSpec.Solve"/> body. Wraps
/// <see cref="IGH_DataAccess"/> with typed getters/setters and the mesh/curve conversion plumbing that the
/// hand-written components used to each copy (flat-array extraction, mesh rebuild with unified normals,
/// repeat-last list access).
/// </summary>
public sealed class GhSolveContext
{
    private readonly GH_Component _component;
    private readonly IGH_DataAccess _access;

    internal GhSolveContext(GH_Component component, IGH_DataAccess access)
    {
        _component = component;
        _access = access;
    }

    public ModelUnitContext UnitContext => ModelUnitContext.FromDocument(Rhino.RhinoDoc.ActiveDoc);

    public double Tolerance => UnitContext.AbsoluteTolerance;

    public Rhino.UnitSystem ModelUnitSystem => UnitContext.UnitSystem;

    public double FromMeters(double meters) => UnitContext.FromMeters(meters);

    public bool RequireModelUnits()
    {
        if (UnitContext.IsSupported)
            return true;

        Error("MoleHill requires model units. Set Rhino document units to a real length unit, then recompute.");
        return false;
    }

    // ── inputs ────────────────────────────────────────────────────────────
    public bool TryGetMesh(int index, out RhinoMesh mesh)
    {
        RhinoMesh? value = null;
        if (_access.GetData(index, ref value) && value != null)
        {
            mesh = value;
            return true;
        }

        mesh = null!;
        return false;
    }

    public List<Curve> GetCurves(int index)
    {
        var curves = new List<Curve>();
        _access.GetDataList(index, curves);
        return curves;
    }

    public double GetNumber(int index, double fallback = 0.0)
    {
        double value = fallback;
        _access.GetData(index, ref value);
        return value;
    }

    public List<double> GetNumbers(int index)
    {
        var values = new List<double>();
        _access.GetDataList(index, values);
        return values;
    }

    public int GetInt(int index, int fallback = 0)
    {
        int value = fallback;
        _access.GetData(index, ref value);
        return value;
    }

    public List<int> GetInts(int index)
    {
        var values = new List<int>();
        _access.GetDataList(index, values);
        return values;
    }

    public List<GeometryBase> GetGeometry(int index)
    {
        var values = new List<GeometryBase>();
        _access.GetDataList(index, values);
        return values;
    }

    public bool GetBool(int index, bool fallback = false)
    {
        bool value = fallback;
        _access.GetData(index, ref value);
        return value;
    }

    // ── outputs / messages ────────────────────────────────────────────────
    public void SetData(int index, object value) => _access.SetData(index, value);

    public void SetDataList(int index, System.Collections.IEnumerable values) => _access.SetDataList(index, values);

    public void Warn(string message) => _component.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, message);

    public void Error(string message) => _component.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, message);

    public void Remark(string message) => _component.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, message);

    // ── shared geometry plumbing ──────────────────────────────────────────
    public static double[] ToFlatVertices(RhinoMesh mesh)
    {
        int count = mesh.Vertices.Count;
        var vertices = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            var point = mesh.Vertices[i];
            vertices[i * 3] = point.X;
            vertices[i * 3 + 1] = point.Y;
            vertices[i * 3 + 2] = point.Z;
        }
        return vertices;
    }

    /// <summary>Flattens triangle faces; returns false (and reports an error) if the mesh has quads.</summary>
    public bool TryToFlatFaces(RhinoMesh mesh, out int[] faces)
    {
        int count = mesh.Faces.Count;
        faces = new int[count * 3];
        for (int i = 0; i < count; i++)
        {
            var face = mesh.Faces[i];
            if (face.IsQuad)
            {
                Error("Mesh contains quad faces. Only triangle meshes are supported.");
                faces = Array.Empty<int>();
                return false;
            }
            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }
        return true;
    }

    public static RhinoMesh BuildMesh(double[] vertices, int[] faces)
    {
        var mesh = new RhinoMesh();
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        mesh.Vertices.Capacity = vertexCount;
        mesh.Faces.Capacity = faceCount;

        for (int i = 0; i < vertexCount; i++)
            mesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);

        for (int i = 0; i < faceCount; i++)
            mesh.Faces.AddFace(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);

        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        return mesh;
    }

    /// <summary>Per-item list access with "shorter lists repeat the last value" semantics.</summary>
    public static T ListValue<T>(IReadOnlyList<T> list, int index, T fallback)
    {
        if (list.Count == 0)
            return fallback;
        return list[Math.Min(index, list.Count - 1)];
    }
}
