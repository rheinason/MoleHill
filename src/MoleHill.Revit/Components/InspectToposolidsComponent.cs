// Reads MoleHill identity and editable shape data back from Revit Toposolids inside Rhino.Inside.Revit.
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using MoleHill.Revit.RevitHost;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Revit.Components;

public sealed class InspectToposolidsComponent : GH_Component
{
    public InspectToposolidsComponent()
        : base(
            "Inspect Toposolids",
            "Inspect Topo",
            "Read the MoleHill key and fingerprint, type, level, host, sketch profile and shape points of Revit Toposolids.",
            "MoleHill",
            "Revit")
    {
    }

    public override Guid ComponentGuid => new("79BC2A1E-C756-4612-B502-6BEB481A0FDE");

    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override System.Drawing.Bitmap? Icon => MoleHillRevitInfo.LoadIcon("InspectToposolids.png");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddGenericParameter("Toposolids", "T", "Revit Toposolids or subdivisions.", GH_ParamAccess.list);
        pManager.AddNumberParameter("Meters Per Unit", "MPU", "Metres per output unit. Empty uses the Rhino document's units.", GH_ParamAccess.item);
        pManager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("Keys", "K", "MoleHill key; empty for a Toposolid MoleHill did not write.", GH_ParamAccess.list);
        pManager.AddTextParameter("Fingerprints", "F", "Geometry fingerprint written with the key.", GH_ParamAccess.list);
        pManager.AddBooleanParameter("Is Subdivision", "S", "True for a subdivision of another Toposolid.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Type Ids", "TI", "Toposolid type element id.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Level Ids", "LI", "Level element id.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Host Ids", "HI", "Host Toposolid id of a subdivision; -1 otherwise.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Profiles", "P", "Sketch profile edges; one branch per Toposolid.", GH_ParamAccess.tree);
        pManager.AddPointParameter("Shape Points", "SP", "Shape-editing points; one branch per Toposolid.", GH_ParamAccess.tree);
        pManager.AddTextParameter("Report", "I", "One line per Toposolid.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var inputs = new List<object>();
        if (!DA.GetDataList(0, inputs))
            return;

        double metersPerUnit = 0.0;
        DA.GetData(1, ref metersPerUnit);
        if (!(metersPerUnit > 0.0))
        {
            UnitSystem units = RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Meters;
            metersPerUnit = RhinoMath.UnitScale(units, UnitSystem.Meters);
        }

        var keys = new List<string?>();
        var fingerprints = new List<string?>();
        var isSubdivision = new List<bool>();
        var typeIds = new List<int>();
        var levelIds = new List<int>();
        var hostIds = new List<int>();
        var profiles = new DataTree<Curve>();
        var shapePoints = new DataTree<Point3d>();
        var report = new List<string>();

        for (int index = 0; index < inputs.Count; index++)
        {
            InspectedToposolid? inspected;
            try
            {
                inspected = RevitSession.Inspect(inputs[index], metersPerUnit);
            }
            catch (FileNotFoundException)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Revit is not available. This component runs inside Rhino.Inside.Revit.");
                return;
            }

            if (inspected == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Item {index} is not a Revit Toposolid.");
                continue;
            }

            var path = new GH_Path(index);
            keys.Add(inspected.Key);
            fingerprints.Add(inspected.Fingerprint);
            isSubdivision.Add(inspected.IsSubdivision);
            typeIds.Add(ToInt(inspected.TypeId));
            levelIds.Add(ToInt(inspected.LevelId));
            hostIds.Add(ToInt(inspected.HostId));
            profiles.AddRange(inspected.Profiles, path);
            shapePoints.AddRange(inspected.ShapePoints, path);
            report.Add($"{index}: key {inspected.Key ?? "<none>"}, fingerprint {inspected.Fingerprint ?? "<none>"}" +
                       (inspected.IsSubdivision ? $", subdivision of {inspected.HostId}." : "."));
        }

        DA.SetDataList(0, keys);
        DA.SetDataList(1, fingerprints);
        DA.SetDataList(2, isSubdivision);
        DA.SetDataList(3, typeIds);
        DA.SetDataList(4, levelIds);
        DA.SetDataList(5, hostIds);
        DA.SetDataTree(6, profiles);
        DA.SetDataTree(7, shapePoints);
        DA.SetDataList(8, report);
    }

    /// <summary>Grasshopper integers are 32-bit; an id beyond that range reads as -1 rather than wrapping.</summary>
    private static int ToInt(long id) => id is >= int.MinValue and <= int.MaxValue ? (int)id : -1;
}
