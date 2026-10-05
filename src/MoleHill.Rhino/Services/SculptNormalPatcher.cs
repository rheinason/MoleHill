using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Incremental normal updates for the sculpt working mesh: a vertex→face CSR index built once per
/// session; per dab, only faces incident to moved vertices get fresh face normals and only vertices
/// of those faces get re-averaged vertex normals — full-mesh ComputeNormals would dominate the
/// per-dab cost on large terrains.
/// </summary>
internal sealed class SculptNormalPatcher
{
    private readonly int[] _faces;
    private readonly int[] _vertexFaceOffsets;
    private readonly int[] _vertexFaceIndices;
    private readonly int[] _faceStamps;
    private readonly int[] _vertexStamps;
    // Managed mirror of the face normals: re-averaging through mesh.FaceNormals[i] is one native call
    // per incident face, ~80k per 20 m dab on a 540k-face terrain.
    private readonly float[] _faceNormals;
    private readonly List<int> _touchedFaces = new();
    private readonly List<int> _touchedVertices = new();
    private int _stamp;

    public SculptNormalPatcher(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        _faces = faces;
        _faceNormals = new float[faceCount * 3];
        for (int f = 0; f < faceCount; f++)
            StoreFaceNormal(vertices, f);
        _faceStamps = new int[faceCount];
        _vertexStamps = new int[vertexCount];

        var counts = new int[vertexCount];
        for (int f = 0; f < faceCount; f++)
        {
            counts[faces[f * 3]]++;
            counts[faces[f * 3 + 1]]++;
            counts[faces[f * 3 + 2]]++;
        }

        _vertexFaceOffsets = new int[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
            _vertexFaceOffsets[i + 1] = _vertexFaceOffsets[i] + counts[i];

        _vertexFaceIndices = new int[_vertexFaceOffsets[vertexCount]];
        var cursors = new int[vertexCount];
        Array.Copy(_vertexFaceOffsets, cursors, vertexCount);
        for (int f = 0; f < faceCount; f++)
        {
            _vertexFaceIndices[cursors[_faces[f * 3]]++] = f;
            _vertexFaceIndices[cursors[_faces[f * 3 + 1]]++] = f;
            _vertexFaceIndices[cursors[_faces[f * 3 + 2]]++] = f;
        }
    }

    private Vector3d StoreFaceNormal(double[] vertices, int f)
    {
        int a = _faces[f * 3] * 3, b = _faces[f * 3 + 1] * 3, c = _faces[f * 3 + 2] * 3;
        var normal = Vector3d.CrossProduct(
            new Vector3d(vertices[b] - vertices[a], vertices[b + 1] - vertices[a + 1], vertices[b + 2] - vertices[a + 2]),
            new Vector3d(vertices[c] - vertices[a], vertices[c + 1] - vertices[a + 1], vertices[c + 2] - vertices[a + 2]));
        normal.Unitize();
        _faceNormals[f * 3] = (float)normal.X;
        _faceNormals[f * 3 + 1] = (float)normal.Y;
        _faceNormals[f * 3 + 2] = (float)normal.Z;
        return normal;
    }

    /// <summary>Vertices re-averaged by the last <see cref="PatchNormals"/> call (the moved vertices
    /// plus their one-ring). Backed by a reused buffer — consume before the next patch.</summary>
    public IReadOnlyList<int> LastTouchedVertices => _touchedVertices;

    /// <summary>Faces whose normals the last <see cref="PatchNormals"/> call recomputed — every face
    /// incident to a moved vertex. Same reuse caveat as <see cref="LastTouchedVertices"/>.</summary>
    public IReadOnlyList<int> LastTouchedFaces => _touchedFaces;

    /// <summary>Recomputes normals around <paramref name="movedVertices"/> from the engine's
    /// <paramref name="vertices"/> (index-parity with <paramref name="mesh"/>) and writes them to the mesh.</summary>
    public void PatchNormals(RhinoMesh mesh, double[] vertices, IReadOnlyList<int> movedVertices)
    {
        _stamp++;
        _touchedFaces.Clear();
        _touchedVertices.Clear();

        foreach (int v in movedVertices)
        {
            for (int k = _vertexFaceOffsets[v]; k < _vertexFaceOffsets[v + 1]; k++)
            {
                int f = _vertexFaceIndices[k];
                if (_faceStamps[f] == _stamp)
                    continue;

                _faceStamps[f] = _stamp;
                _touchedFaces.Add(f);
            }
        }

        foreach (int f in _touchedFaces)
        {
            mesh.FaceNormals.SetFaceNormal(f, StoreFaceNormal(vertices, f));

            for (int corner = 0; corner < 3; corner++)
            {
                int v = _faces[f * 3 + corner];
                if (_vertexStamps[v] == _stamp)
                    continue;

                _vertexStamps[v] = _stamp;
                _touchedVertices.Add(v);
            }
        }

        foreach (int v in _touchedVertices)
        {
            var sum = Vector3d.Zero;
            for (int k = _vertexFaceOffsets[v]; k < _vertexFaceOffsets[v + 1]; k++)
            {
                int f = _vertexFaceIndices[k] * 3;
                sum.X += _faceNormals[f];
                sum.Y += _faceNormals[f + 1];
                sum.Z += _faceNormals[f + 2];
            }

            sum.Unitize();
            mesh.Normals.SetNormal(v, sum);
        }
    }
}
