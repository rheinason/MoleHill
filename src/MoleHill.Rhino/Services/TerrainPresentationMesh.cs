using System.Runtime.CompilerServices;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Turns the computational terrain mesh into the one Rhino should <em>shade</em>.
///
/// The terrain and whatever stands on it — a retaining wall above all — share vertices along their
/// common line. Rhino carries one normal per vertex and averages the adjacent face normals into it, so
/// at a wall base the average is pulled between a steep wall face and the ground. Measured on a live
/// trailer-ramp terrain: 155 shared rail vertices, ground genuinely flat at 1.4°, shaded with an
/// averaged normal tilted a median of 50° and up to 66°. Smooth shading then bleeds that across every
/// triangle touching the rail, which reads as a dark sawtooth hugging both sides of the wall. It is not
/// a winding fault, so <c>UnifyNormals</c> (and the UnifyMeshNormals command) do nothing for it.
///
/// Unwelding splits the vertices along that seam so the wall keeps its normal and the ground keeps its
/// own. It is done on a COPY, at the display and bake boundary only: unwelding raises the vertex count,
/// and the computational mesh is paired with arrays extracted from it all over the build service.
///
/// The artifact is newly visible rather than newly introduced. While the terrain beside a wall was still
/// a few huge slivers, those faces dominated the area-weighted average and the error stayed near 3°;
/// once the wall patch refined that band to the terrain's own density the two became comparable and the
/// welded seam finally showed its true cost.
/// </summary>
internal static class TerrainPresentationMesh
{
    // One shading copy per source mesh, so the conduit does not rebuild it on every redraw. The source
    // is held weakly: a terrain rebuild replaces the mesh, and the old pair must not pin it in memory.
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<Mesh, Mesh> ShadingCopies = new();

    // A mesh edited in place many times a second — the sculpt session's working mesh — is drawn as-is.
    // Its cached copy would go stale on the first dab (the cache is keyed by identity, not content) and
    // only refresh on mouse-up, while rebuilding it per dab costs a full duplicate and unweld per mouse
    // event. The session patches that mesh's normals itself; wall seams smear only until it ends.
    private static Mesh? _liveEditedMesh;

    internal static void Invalidate(Mesh? mesh)
    {
        if (mesh == null) return;
        lock (Gate) ShadingCopies.Remove(mesh);
    }

    /// <summary>Marks <paramref name="mesh"/> as edited in place, so it bypasses the shading copy;
    /// pass null to release it.</summary>
    internal static void SetLiveEditedMesh(Mesh? mesh)
    {
        lock (Gate)
        {
            _liveEditedMesh = mesh;
            if (mesh != null) ShadingCopies.Remove(mesh);
        }
    }

    /// <summary>
    /// A face leaning this far from horizontal is a wall. The same number, and the same meaning, as the
    /// remesher's RemeshWallFaceMinSlopeDeg.
    /// </summary>
    private const double WallFaceMinSlopeDegrees = 70.0;

    /// <summary>
    /// Fallback crease angle, used only between two faces that are BOTH walls — a mitre where two panels
    /// meet — which the wall-versus-terrain test cannot see because neither side is terrain.
    ///
    /// It stays high on purpose: real terrain folds against itself at 30–60° across any steep hillside,
    /// and unwelding those would facet a surface that should read as smooth, everywhere rather than only
    /// at walls. A fold sharper than this is a cliff, whose faces are walls by the test above anyway.
    /// </summary>
    private const double MitreCreaseAngleDegrees = 70.0;

    /// <summary>
    /// A shading copy of <paramref name="mesh"/> with its wall seams unwelded, or the input itself when
    /// there is nothing to do. Never mutates the input. Returns the input unchanged if anything fails,
    /// so a presentation concern can never cost the user their geometry.
    /// </summary>
    public static Mesh? CreateForDisplay(Mesh? mesh)
    {
        if (mesh == null || mesh.Faces.Count == 0)
            return mesh;

        lock (Gate)
        {
            if (ReferenceEquals(mesh, _liveEditedMesh)) return mesh;
            if (ShadingCopies.TryGetValue(mesh, out Mesh? shaded)) return shaded;
            Mesh? copy = null;
            try
            {
                copy = mesh.DuplicateMesh();
                if (copy == null) return mesh;
                if (!TryUnweldWallSeams(copy))
                {
                    copy.Dispose();
                    ShadingCopies.Add(mesh, mesh);
                    return mesh;
                }
                if (copy.Normals.Count != copy.Vertices.Count) copy.Normals.ComputeNormals();
                ShadingCopies.Add(mesh, copy);
                return copy;
            }
            catch (Exception)
            {
                copy?.Dispose();
                // Shading is cosmetic; retain the computational geometry on failure.
                return mesh;
            }
        }
    }

    /// <summary>
    /// Unwelds every seam where a wall face meets a non-wall face, plus any mitre between two wall faces.
    ///
    /// Classifying the FACES is what matters, rather than measuring the angle between them. A wall leaning
    /// 75° that meets ground sloping 20° the same way creases at only 55°, so an angle test set anywhere
    /// sane leaves that seam welded and the ground beside it still shades as if it were tilted 47°.
    /// Verified live in Rhino on exactly that case: an angle test left the rail reading 47.5°, while this
    /// test split it into 75.0° for the wall and 20.0° for the ground.
    /// </summary>
    private static bool TryUnweldWallSeams(Mesh mesh)
    {
        mesh.FaceNormals.ComputeFaceNormals();
        int faceCount = mesh.Faces.Count;
        if (mesh.FaceNormals.Count != faceCount)
            return false;

        // A unit face normal has |Z| = cos(slope from horizontal).
        // without a trig call per face.
        double wallLimit = Math.Cos(RhinoMath.ToRadians(WallFaceMinSlopeDegrees));
        var isWall = new bool[faceCount];
        for (int face = 0; face < faceCount; face++)
            isWall[face] = Math.Abs(mesh.FaceNormals[face].Z) <= wallLimit;

        double mitreLimit = Math.Cos(RhinoMath.ToRadians(MitreCreaseAngleDegrees));
        var seams = new List<int>();
        global::Rhino.Geometry.Collections.MeshTopologyEdgeList edges = mesh.TopologyEdges;
        for (int edge = 0; edge < edges.Count; edge++)
        {
            int[] connected = edges.GetConnectedFaces(edge);
            if (connected == null || connected.Length != 2)
                continue;

            int a = connected[0];
            int b = connected[1];
            if ((uint)a >= (uint)faceCount || (uint)b >= (uint)faceCount)
                continue;

            if (isWall[a] != isWall[b])
            {
                seams.Add(edge);
                continue;
            }

            if (!isWall[a])
                continue;

            Vector3f na = mesh.FaceNormals[a];
            Vector3f nb = mesh.FaceNormals[b];
            if ((na.X * nb.X) + (na.Y * nb.Y) + (na.Z * nb.Z) < mitreLimit)
                seams.Add(edge);
        }

        return seams.Count > 0 && mesh.UnweldEdge(seams, modifyNormals: true);
    }
}
