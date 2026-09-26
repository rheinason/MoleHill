using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one terrain-object type (the Objects tab). Parallels
/// <see cref="ModifierTypeDescriptor"/>: drop a descriptor and the type appears in the add-menu, gets its
/// card chrome (label/icon/subtitle/accent), and (de)serializes — all from one place. Discovered by
/// reflection in <see cref="ObjectTypeRegistry"/>.
/// </summary>
internal abstract class ObjectTypeDescriptor
{
    /// <summary>Stable JSON discriminator (must match the historical `$type` value).</summary>
    public abstract string Kind { get; }

    public abstract Type DefinitionType { get; }

    /// <summary>Short type label used in the add-menu and card header (e.g. "Plant", "Orient", "Scatter").</summary>
    public abstract string DisplayName { get; }

    /// <summary>Icon-plate glyph text (e.g. "Z", "XY", "S"), used as a fallback when <see cref="IconName"/> is null.</summary>
    public abstract string IconLabel { get; }

    /// <summary>Optional panel icon resource name (loaded via <c>PanelIcons.Load</c>). Null falls back to <see cref="IconLabel"/>.</summary>
    public virtual string? IconName => null;

    /// <summary>One-line card subtitle.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Accent strip ARGB color for the card.</summary>
    public abstract int AccentArgb { get; }

    /// <summary>Ordering within the Add-Object menu.</summary>
    public virtual int SortOrder => 0;

    public virtual IReadOnlyList<ParameterDescriptor<TerrainObjectDefinition>> Parameters => ObjectParameterCatalog.Common;

    public abstract TerrainObjectDefinition Create();
}
