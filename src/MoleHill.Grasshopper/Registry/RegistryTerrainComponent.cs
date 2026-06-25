using Grasshopper.Kernel;

namespace MoleHill.Grasshopper.Registry;

/// <summary>
/// Generic Grasshopper component driven by a <see cref="GhComponentSpec"/>. Param registration and the
/// solve dispatch live here once; each concrete MoleHill component is a small subclass that supplies its
/// spec, <see cref="GH_Component.ComponentGuid"/> (kept stable so existing .gh files resolve), and icon.
/// </summary>
public abstract class RegistryTerrainComponent : GH_Component
{
    protected RegistryTerrainComponent(GhComponentSpec spec)
        : base(spec.Name, spec.Nick, spec.Description, "MoleHill", spec.SubCategory)
    {
    }

    /// <summary>
    /// The component's spec. Exposed as a virtual property (not a stored field) because
    /// <see cref="GH_Component"/>'s base constructor calls <c>PostConstructor</c> →
    /// <see cref="RegisterInputParams"/> *before* the derived constructor body runs — a field set after
    /// <c>base(...)</c> would still be null. Concrete components back this with a static spec.
    /// </summary>
    protected abstract GhComponentSpec Spec { get; }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        foreach (var port in Spec.Inputs)
        {
            int index = port.Type switch
            {
                GhPortType.Mesh => pManager.AddMeshParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Curve => pManager.AddCurveParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Number => port.NumberDefault.HasValue
                    ? pManager.AddNumberParameter(port.Name, port.Nick, port.Description, port.Access, port.NumberDefault.Value)
                    : pManager.AddNumberParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Integer => port.IntegerDefault.HasValue
                    ? pManager.AddIntegerParameter(port.Name, port.Nick, port.Description, port.Access, port.IntegerDefault.Value)
                    : pManager.AddIntegerParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Boolean => pManager.AddBooleanParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Brep => pManager.AddBrepParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Line => pManager.AddLineParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Text => pManager.AddTextParameter(port.Name, port.Nick, port.Description, port.Access),
                GhPortType.Geometry => pManager.AddGeometryParameter(port.Name, port.Nick, port.Description, port.Access),
                _ => throw new ArgumentOutOfRangeException(nameof(port.Type), port.Type, "Unhandled port type."),
            };
            if (port.Optional)
                pManager[index].Optional = true;
        }
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        foreach (var port in Spec.Outputs)
        {
            switch (port.Type)
            {
                case GhPortType.Mesh: pManager.AddMeshParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Curve: pManager.AddCurveParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Number: pManager.AddNumberParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Integer: pManager.AddIntegerParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Boolean: pManager.AddBooleanParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Brep: pManager.AddBrepParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Line: pManager.AddLineParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Text: pManager.AddTextParameter(port.Name, port.Nick, port.Description, port.Access); break;
                case GhPortType.Geometry: pManager.AddGeometryParameter(port.Name, port.Nick, port.Description, port.Access); break;
                default: throw new ArgumentOutOfRangeException(nameof(port.Type), port.Type, "Unhandled port type.");
            }
        }
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Spec.Solve(new GhSolveContext(this, DA));
    }
}
