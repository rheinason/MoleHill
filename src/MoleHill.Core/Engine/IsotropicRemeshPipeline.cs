using MoleHill.Core.Grading;

namespace MoleHill.Core.Engine;

/// <summary>
/// The isotropic Remesh as both hosts run it: insert the card's own breaklines into the existing faces,
/// derive the target edge length, remesh (tiled by default), and collapse the float-coincident edges the
/// mesh hand-off would otherwise weld into folds. The Rhino Remesh card and the Grasshopper Remesh
/// component call this one sequence, so the two cannot drift apart.
/// </summary>
public static class IsotropicRemeshPipeline
{
    public sealed class Request
    {
        public required IndexedTriMesh Terrain { get; init; }

        /// <summary>
        /// Breaklines to insert into the existing faces first (draped: Z comes from the terrain). The isotropic
        /// remesher only pins constraints that already run along mesh edges; it never inserts one.
        /// </summary>
        public IReadOnlyList<ConstraintPolyline> InsertedConstraints { get; init; } = Array.Empty<ConstraintPolyline>();

        /// <summary>Every constraint the remesh must keep as edges (already in the mesh, or inserted above).</summary>
        public IReadOnlyList<ConstraintPolyline> PinnedConstraints { get; init; } = Array.Empty<ConstraintPolyline>();

        /// <summary>Target edge length; 0 preserves the mesh's approximate global plan density.</summary>
        public double EdgeLength { get; init; }

        /// <summary>Multiplies an automatic (EdgeLength 0) target; the Rhino preview build uses 2.</summary>
        public double AutoTargetScale { get; init; } = 1.0;

        public double CreaseAngleDeg { get; init; }

        public double Tolerance { get; init; }

        public double WallFaceMinSlopeDeg { get; init; } = 70.0;

        /// <summary>Null: 3 for an automatic target, 5 for an explicit one.</summary>
        public int? Iterations { get; init; }

        /// <summary>
        /// Remesh world-anchored tiles (<see cref="TiledIsotropicRemesher"/>), so the output depends only on
        /// local input; false runs the whole-mesh <see cref="IsotropicRemesher"/>.
        /// </summary>
        public bool Tiled { get; init; } = true;

        /// <summary>The previous run's tiles, reused where their input is unchanged (tiled only).</summary>
        public TiledIsotropicRemesher.TiledRemeshMemo? PreviousMemo { get; init; }

        public Func<bool>? ShouldCancel { get; init; }
    }

    public sealed class Outcome
    {
        /// <summary>True when the remesh ran and produced <see cref="Vertices"/> / <see cref="Faces"/>.</summary>
        public bool Success { get; init; }

        /// <summary>Set when inserting <see cref="Request.InsertedConstraints"/> failed; nothing else ran.</summary>
        public string? InsertError { get; init; }

        /// <summary>The target edge length used, or 0 when none could be derived (nothing ran).</summary>
        public double Target { get; init; }

        /// <summary>The remesher's own result, when it ran.</summary>
        public IsotropicRemesher.Result? Remesh { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Faces { get; init; } = Array.Empty<int>();

        public int CollapsedEdges { get; init; }

        public int SeparatedVertices { get; init; }

        /// <summary>This run's tiles, for the next (tiled only).</summary>
        public TiledIsotropicRemesher.TiledRemeshMemo? Memo { get; init; }
    }

    public static Outcome Run(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = request.Terrain;

        if (request.InsertedConstraints.Count > 0)
        {
            if (!MeshConstraintTopologyInserter.TryInsert(
                    request.Terrain, request.InsertedConstraints, request.Tolerance,
                    out IndexedTriMesh inserted, out string? insertError))
            {
                return new Outcome { InsertError = insertError ?? "Breakline insertion failed." };
            }

            (double[] insertedVertices, int insertedVertexCount, int[] insertedFaces, int insertedFaceCount) = inserted;
            vertices = insertedVertices.Length == insertedVertexCount * 3
                ? insertedVertices
                : insertedVertices[..(insertedVertexCount * 3)];
            faces = insertedFaces.Length == insertedFaceCount * 3
                ? insertedFaces
                : insertedFaces[..(insertedFaceCount * 3)];
        }
        else
        {
            vertices = vertices.Length == vertexCount * 3 ? vertices : vertices[..(vertexCount * 3)];
            faces = faces.Length == faceCount * 3 ? faces : faces[..(faceCount * 3)];
        }

        // EdgeLength 0 = preserve the mesh's approximate global plan density. A median edge badly
        // over-refines terrains that mix dense feature sampling with large sparse outer faces.
        double target = request.EdgeLength > 0
            ? request.EdgeLength
            : IsotropicRemesher.EstimateFaceCountPreservingTarget(vertices, faces) * request.AutoTargetScale;
        if (request.Tiled && request.EdgeLength <= 0)
            target = TiledIsotropicRemesher.RoundedTarget(target);
        if (target <= 0)
            return new Outcome();

        var options = new IsotropicRemesher.Options
        {
            TargetEdgeLength = target,
            CreaseAngleDeg = request.CreaseAngleDeg,
            Tolerance = request.Tolerance,
            WallFaceMinSlopeDeg = request.WallFaceMinSlopeDeg,
            Iterations = request.Iterations ?? (request.EdgeLength <= 0 ? 3 : 5),
            ShouldCancel = request.ShouldCancel
        };

        IsotropicRemesher.Result result;
        TiledIsotropicRemesher.TiledRemeshMemo? memo = null;
        if (request.Tiled)
            result = TiledIsotropicRemesher.Remesh(vertices, faces, request.PinnedConstraints, options, 0.0, request.PreviousMemo, out memo);
        else
            result = IsotropicRemesher.Remesh(vertices, faces, request.PinnedConstraints, options);

        if (!result.Success)
            return new Outcome { Target = target, Remesh = result, Memo = memo };

        // The hand-off welds vertices that share a float position, blindly. A remesh can leave pairs a micron
        // apart, and welding those dropped faces and made edges shared by four.
        (double[] outVertices, int[] outFaces) = FloatCoincidentEdgeCollapser.Collapse(
            result.Vertices, result.Faces, out int collapsed, out int separated);
        return new Outcome
        {
            Success = true,
            Target = target,
            Remesh = result,
            Vertices = outVertices,
            Faces = outFaces,
            CollapsedEdges = collapsed,
            SeparatedVertices = separated,
            Memo = memo
        };
    }
}
