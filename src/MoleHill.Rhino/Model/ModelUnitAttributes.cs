namespace MoleHill.Rhino.Model;

/// <summary>
/// What a stored number measures, so a change of model units can rescale it (<c>TerrainUnitScaler</c>).
/// Every <c>double</c> / <c>double?</c> property on a definition type carries exactly one of these, a
/// <see cref="UnitFreeAttribute"/>, or is named by a <see cref="ModelLengthMembersAttribute"/> on its
/// class; <c>ModelUnitAttributeGuardTests</c> fails the build otherwise, so a new length field cannot
/// silently skip scaling.
/// </summary>
public enum ModelUnitKind
{
    /// <summary>Scales by the length factor.</summary>
    Length,

    /// <summary>Scales by the length factor squared.</summary>
    Area,

    /// <summary>Scales by the length factor cubed.</summary>
    Volume,

    /// <summary>A per-area quantity (a density): scales by the reciprocal of the area factor.</summary>
    InverseArea
}

/// <summary>Base of the unit markers; use <see cref="ModelLengthAttribute"/> and friends.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public abstract class ModelUnitAttribute : Attribute
{
    protected ModelUnitAttribute(ModelUnitKind kind) => Kind = kind;

    public ModelUnitKind Kind { get; }

    /// <summary>
    /// Scale only while the value is above zero. Zero (and a negative sentinel) means "unset" for the
    /// field, and an unset value must stay unset.
    /// </summary>
    public bool OnlyWhenPositive { get; set; }

    /// <summary>
    /// Scale only when the owning definition is one whose values are lengths (a summary field shared by
    /// slope and elevation analyses). The generic pass skips these; the scaler decides per owner.
    /// </summary>
    public bool OwnerDependent { get; set; }
}

/// <summary>A distance, height, width or coordinate in model units.</summary>
public sealed class ModelLengthAttribute : ModelUnitAttribute
{
    public ModelLengthAttribute() : base(ModelUnitKind.Length) { }
}

/// <summary>An area in model units squared.</summary>
public sealed class ModelAreaAttribute : ModelUnitAttribute
{
    public ModelAreaAttribute() : base(ModelUnitKind.Area) { }
}

/// <summary>A volume in model units cubed.</summary>
public sealed class ModelVolumeAttribute : ModelUnitAttribute
{
    public ModelVolumeAttribute() : base(ModelUnitKind.Volume) { }
}

/// <summary>A quantity per unit area (a density).</summary>
public sealed class InverseModelAreaAttribute : ModelUnitAttribute
{
    public InverseModelAreaAttribute() : base(ModelUnitKind.InverseArea) { }
}

/// <summary>
/// A number that does not change with model units (an angle, percentage, count, ratio, normalized vector
/// component, or paper-space weight), or one the scaler deliberately leaves alone. The reason is required
/// so the exemption reads as a decision.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class UnitFreeAttribute : Attribute
{
    public UnitFreeAttribute(string reason) => Reason = reason;

    public string Reason { get; }
}

/// <summary>
/// Marks inherited properties that are model lengths on this concrete type only, overriding the
/// <see cref="UnitFreeAttribute"/> on the base declaration (an analysis base class's colour range is
/// unitless for slope but a length for elevation).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ModelLengthMembersAttribute : Attribute
{
    public ModelLengthMembersAttribute(params string[] propertyNames) => PropertyNames = propertyNames;

    public IReadOnlyList<string> PropertyNames { get; }
}
