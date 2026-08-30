namespace MoleHill.Rhino.Registry;

/// <summary>
/// What kind of geometry a role emits, and therefore which appearance fields mean anything for it.
///
/// The template editor shows a role's fields by facet, so a grid layer is never offered a hatch
/// pattern and a hatch layer is never offered an annotation style. Without this the properties panel
/// would present every field for every layer and most of them would do nothing.
/// </summary>
[Flags]
internal enum LayerRoleFacets
{
    None = 0,

    /// <summary>Curves: print width and preview thickness apply.</summary>
    Line = 1 << 0,

    /// <summary>Hatches: pattern, scale and rotation apply.</summary>
    Fill = 1 << 1,

    /// <summary>Text and text dots: the annotation style applies.</summary>
    Text = 1 << 2,

    /// <summary>Meshes and Breps.</summary>
    Mesh = 1 << 3,

    /// <summary>Block instances.</summary>
    Block = 1 << 4
}
