// Writes Prepare Toposolid packages to Revit Toposolids (and their subdivisions) inside Rhino.Inside.Revit.
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using MoleHill.Revit.Planning;
using MoleHill.Revit.RevitHost;

namespace MoleHill.Revit.Components;

public sealed class WriteToposolidsComponent : GH_Component
{
    public WriteToposolidsComponent()
        : base(
            "Write Toposolids",
            "Write Topo",
            "Create or update one Revit Toposolid per Prepare Toposolid package, matched by terrain key. " +
            "An unchanged fingerprint leaves the element alone; changed geometry replaces it in one undo step. " +
            "Toposolids are never deleted because a package is missing.",
            "MoleHill",
            "Revit")
    {
    }

    public override Guid ComponentGuid => new("CBE97AE2-E08C-41DE-A8D2-F6845B5C98D3");

    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override System.Drawing.Bitmap? Icon => MoleHillRevitInfo.LoadIcon("WriteToposolids.png");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddBooleanParameter("Run", "R", "Write to Revit. While off, the packages are only validated.", GH_ParamAccess.item, false);
        pManager.AddGenericParameter("Preparation", "P", "Preparation packages from Prepare Toposolid; one Toposolid each.", GH_ParamAccess.list);
        pManager.AddGenericParameter("Toposolid Type", "T", "Toposolid type for new Toposolids. A replacement keeps its predecessor's type.", GH_ParamAccess.item);
        pManager[2].Optional = true;
        pManager.AddGenericParameter("Level", "L", "Level for new Toposolids. A replacement keeps its predecessor's level.", GH_ParamAccess.item);
        pManager[3].Optional = true;
        pManager.AddBooleanParameter("Subdivisions", "S", "Also write each package's zones as Toposolid subdivisions.", GH_ParamAccess.item, true);
        pManager.AddGenericParameter("Subdivision Type", "ST", "Optional Toposolid type for subdivisions; empty uses Revit's default.", GH_ParamAccess.item);
        pManager[5].Optional = true;
        pManager.AddTextParameter("Preserve Parameters", "PP", "Instance parameters copied onto a replacement Toposolid.", GH_ParamAccess.list);
        pManager[6].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGenericParameter("Toposolids", "T", "The Toposolid for each package, in input order.", GH_ParamAccess.list);
        pManager.AddGenericParameter("Subdivisions", "S", "Subdivisions written for each package; one branch per Toposolid.", GH_ParamAccess.tree);
        pManager.AddTextParameter("Report", "I", "What was created, replaced or kept.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        bool run = false;
        var packages = new List<object>();
        object? toposolidType = null;
        object? level = null;
        bool subdivisions = true;
        object? subdivisionType = null;
        var preserve = new List<string>();
        DA.GetData(0, ref run);
        if (!DA.GetDataList(1, packages))
            return;
        DA.GetData(2, ref toposolidType);
        DA.GetData(3, ref level);
        DA.GetData(4, ref subdivisions);
        DA.GetData(5, ref subdivisionType);
        DA.GetDataList(6, preserve);

        var inputs = new List<PreparationInput>(packages.Count);
        for (int index = 0; index < packages.Count; index++)
        {
            if (!PreparationReader.TryRead(packages[index], out PreparationInput? input) || input == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Item {index} is not a Prepare Toposolid package. Connect its Preparation output.");
                return;
            }

            inputs.Add(input);
        }

        WriteOutcome outcome;
        try
        {
            outcome = RevitSession.Write(run, inputs, toposolidType, level, subdivisions, subdivisionType, preserve);
        }
        catch (FileNotFoundException)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Revit is not available. This component runs inside Rhino.Inside.Revit.");
            return;
        }
        catch (Exception exception)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Nothing was written: {exception.Message}");
            return;
        }

        foreach (string error in outcome.Errors)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
        if (!run && outcome.Errors.Count == 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Validated only. Set Run to write to Revit.");

        var subdivisionTree = new DataTree<object>();
        for (int index = 0; index < outcome.Subdivisions.Count; index++)
            subdivisionTree.AddRange(outcome.Subdivisions[index], new GH_Path(index));

        DA.SetDataList(0, outcome.Toposolids);
        DA.SetDataTree(1, subdivisionTree);
        DA.SetDataList(2, outcome.Report);
    }
}
