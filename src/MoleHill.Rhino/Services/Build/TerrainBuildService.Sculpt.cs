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
        SculptConstraintMask constraintMask = SculptConstraintMaskBuilder.Build(snapshot, terrain, modifier);

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

        // DynTopo refinement is intentionally disabled for now: the subdivision path can be unstable
        // on real graded terrain. Sculpt replay remains displacement-only against the incoming mesh.

        if (shouldCancel?.Invoke() == true)
            return mesh;

        int displaced = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            double dz = field.Sample(x, y) * constraintMask.EvaluateInfluence(x, y);
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
