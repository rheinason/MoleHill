using MoleHill.Core.Engine;
using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Sculpt modifier build stage: replays the persisted displacement field onto the incoming mesh.
/// The field is a pure function of world XY, so the stage is fully stackable — upstream changes
/// (re-triangulation, grading edits) flow through and the sculpt re-applies on top verbatim.
/// </summary>
internal sealed partial class TerrainBuildService
{
    private static RhinoMesh ApplySculpt(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        SculptModifierDefinition modifier,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        if (modifier.Tiles.Count == 0)
            return mesh;

        SculptDisplacementField field = SculptFieldCodec.Decode(modifier);
        if (field.IsEmpty)
            return mesh;

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for sculpting.");
            return mesh;
        }

        // TryExtractMeshData caches its arrays on the input mesh — never mutate them in place.
        vertices = (double[])vertices.Clone();
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;

        if (shouldCancel?.Invoke() == true)
            return mesh;

        // DynTopo: refine the *base* mesh to the detail edge length wherever the field has influence
        // (re-derived from field occupancy every build, so it stays consistent after upstream changes),
        // THEN displace. Breaklines and other persistent constraints stay pinned.
        if (modifier.DynTopo && modifier.DetailSize > 0)
        {
            LocalMeshRefiner.Result refined = LocalMeshRefiner.Refine(
                vertices,
                faces,
                build.PersistentHardConstraints,
                new LocalMeshRefiner.Options
                {
                    TargetEdgeLength = modifier.DetailSize,
                    Tolerance = Math.Max(snapshot.ModelAbsoluteTolerance, 1e-6),
                    // Split-only, like Remesh's local refine: regularizing flips can scramble graded
                    // corridor topology, and the displacement replay doesn't need them.
                    DoFlips = false,
                    RegionFilter = (x, y) => field.HasInfluenceNear(x, y, modifier.DetailSize),
                });

            if (refined.Success)
            {
                vertices = refined.Vertices;
                faces = refined.Faces;
                vertexCount = vertices.Length / 3;
                faceCount = faces.Length / 3;
                if (refined.AddedVertices > 0)
                {
                    build.Diagnostics.Add(
                        $"{modifier.Label} DynTopo: +{refined.AddedVertices:N0} vertices under the sculpt, " +
                        $"preserved input flow ({faceCount:N0} faces).");
                }
            }
            else if (refined.Warning != null)
            {
                build.Diagnostics.Add($"{modifier.Label}: {refined.Warning}");
            }
        }

        if (shouldCancel?.Invoke() == true)
            return mesh;

        int displaced = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double dz = field.Sample(vertices[i * 3], vertices[i * 3 + 1]);
            if (dz == 0.0)
                continue;

            vertices[i * 3 + 2] += dz;
            displaced++;
        }

        if (displaced == 0)
        {
            build.Diagnostics.Add($"{modifier.Label}: sculpt field does not overlap the terrain (no vertices displaced).");
            return mesh;
        }

        return RhinoGeometryConversions.BuildMesh(vertices, vertexCount, faces, faceCount);
    }
}
