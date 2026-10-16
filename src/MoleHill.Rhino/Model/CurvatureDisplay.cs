namespace MoleHill.Rhino.Model;

/// <summary>What a curvature analysis colours the terrain with. Both read the same measured field.</summary>
public enum CurvatureDisplay
{
    /// <summary>One flat colour per landform class (ridge, valley, shoulder and the rest), with a swatch key.</summary>
    Landforms = 0,

    /// <summary>The chosen curvature measure through a diverging ramp, convex warm and concave cool.</summary>
    Curvature = 1
}
