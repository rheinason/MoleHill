using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one marker type (the Markers tab). Supplies the JSON discriminator,
/// the factory, and the add-button text/help. Discovered by reflection in <see cref="MarkerTypeRegistry"/>.
/// </summary>
internal abstract class MarkerTypeDescriptor
{
    /// <summary>Stable JSON discriminator (must match the historical `$type` value).</summary>
    public abstract string Kind { get; }

    public abstract Type DefinitionType { get; }

    /// <summary>Add-button caption (e.g. "+ Elevation").</summary>
    public abstract string AddButtonText { get; }

    /// <summary>Add-button tooltip.</summary>
    public abstract string AddButtonHelp { get; }

    /// <summary>Ordering within the marker add-button row.</summary>
    public virtual int SortOrder => 0;

    public abstract MarkerDefinition Create();
}
