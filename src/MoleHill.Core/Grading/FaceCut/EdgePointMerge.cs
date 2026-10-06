namespace MoleHill.Core.Grading;

/// <summary>
/// When a face's new edge point counts as a duplicate of one it already records. The two callers of the
/// face-cutting kernel deliberately differ here; see <see cref="FaceCutGeometry.IsDuplicateEdgePoint"/>.
/// </summary>
internal enum EdgePointMerge
{
    /// <summary>
    /// Only a point on the <em>same</em> edge within tolerance is a duplicate. Line insertion needs this:
    /// in a sliver two edges pass within tolerance of one crossing, the neighbour across each edge splits
    /// it there, and dropping either record leaves that edge split on one side only.
    /// </summary>
    SameEdgeOnly,

    /// <summary>
    /// Also treats a point on a <em>different</em> edge as a duplicate when both lie within twice the
    /// tolerance of the same corner. The area splitter can afford it because its shared-edge registry is
    /// the union of both neighbours' points, so the neighbour still contributes the split.
    /// </summary>
    SameEdgeOrSharedCorner
}
