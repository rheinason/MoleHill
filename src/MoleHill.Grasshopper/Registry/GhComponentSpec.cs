using Grasshopper.Kernel;

namespace MoleHill.Grasshopper.Registry;

/// <summary>Parameter data kinds a <see cref="GhPort"/> can register on a Grasshopper component.</summary>
public enum GhPortType
{
    Mesh,
    Curve,
    Number,
    Integer,
    Boolean,
    Brep,
    Line,
    Text,
    Geometry,
}

/// <summary>
/// Declarative description of one input or output port. Maps 1:1 onto the
/// <see cref="GH_InputParamManager"/>/<see cref="GH_OutputParamManager"/> Add* calls so a component's
/// parameter list is data rather than hand-written boilerplate.
/// </summary>
public sealed class GhPort
{
    public required string Name { get; init; }
    public required string Nick { get; init; }
    public required string Description { get; init; }
    public required GhPortType Type { get; init; }
    public GH_ParamAccess Access { get; init; } = GH_ParamAccess.item;
    public bool Optional { get; init; }
    public double? NumberDefault { get; init; }
    public int? IntegerDefault { get; init; }

    public static GhPort Mesh(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.item, bool optional = false) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Mesh, Access = access, Optional = optional };

    public static GhPort Curve(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.list, bool optional = true) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Curve, Access = access, Optional = optional };

    public static GhPort Number(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.item, bool optional = true, double? @default = null) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Number, Access = access, Optional = optional, NumberDefault = @default };

    public static GhPort Integer(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.item, bool optional = true, int? @default = null) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Integer, Access = access, Optional = optional, IntegerDefault = @default };

    public static GhPort Boolean(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.item, bool optional = true) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Boolean, Access = access, Optional = optional };

    public static GhPort Brep(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.list, bool optional = false) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Brep, Access = access, Optional = optional };

    public static GhPort Line(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.list, bool optional = false) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Line, Access = access, Optional = optional };

    public static GhPort Text(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.list, bool optional = false) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Text, Access = access, Optional = optional };

    public static GhPort Geometry(string name, string nick, string desc, GH_ParamAccess access = GH_ParamAccess.list, bool optional = false) =>
        new() { Name = name, Nick = nick, Description = desc, Type = GhPortType.Geometry, Access = access, Optional = optional };
}

/// <summary>
/// Declarative description of a MoleHill Grasshopper component: its catalog metadata, input/output ports,
/// and a <see cref="Solve"/> body that reads the inputs, calls <c>MoleHill.Core</c>, and sets the outputs.
/// A single <see cref="RegistryTerrainComponent"/> base turns this into a working component, so a type's
/// GH wiring lives in one small spec instead of a full hand-written <see cref="GH_Component"/>.
/// </summary>
public sealed class GhComponentSpec
{
    public required string Name { get; init; }
    public required string Nick { get; init; }
    public required string Description { get; init; }
    public required string SubCategory { get; init; }
    public required IReadOnlyList<GhPort> Inputs { get; init; }
    public required IReadOnlyList<GhPort> Outputs { get; init; }
    public required Action<GhSolveContext> Solve { get; init; }
}
