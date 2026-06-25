namespace MoleHill.Rhino.Model;

/// <summary>How a scatter definition is drawn in the display conduit during live editing. Bake always
/// materialises real block instances regardless of this mode.</summary>
public enum ScatterPreviewMode
{
    /// <summary>Draw each instance as a point (lightest; best for very large counts).</summary>
    Points = 0,

    /// <summary>Draw a small cached point proxy of each block's shape (cheap but more legible).</summary>
    ShapePoints = 3,

    /// <summary>Draw each instance's bounding-box edges (conveys footprint and scale).</summary>
    BoundingBox = 1,

    /// <summary>Draw real block geometry, capped at the preview limit.</summary>
    Instances = 2
}
