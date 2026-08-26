// Produces validated, unit-aware profiles and bounded elevation points for downstream Revit Toposolid creation.
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using MoleHill.Grasshopper.Types;
using MoleHill.Grasshopper.Utilities;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public sealed class PrepareToposolidComponent : GH_Component
{
    public PrepareToposolidComponent()
        : base(
            "Prepare Toposolid",
            "Prep Toposolid",
            "Validate and prepare a MoleHill Terrain as horizontal profiles, bounded elevation points, and subdivision profiles for downstream Revit Toposolid creation.",
            "MoleHill",
            "Terrain")
    {
    }

    public override Guid ComponentGuid => new("C21D8D04-2F80-4F2D-AF14-C81A2F3923DB");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddGenericParameter("Terrain", "T", "MoleHill Terrain, or an ordinary mesh.", GH_ParamAccess.item);
        pManager.AddIntegerParameter(
            "Maximum Points",
            "P",
            "Maximum Toposolid elevation points (3-50,000). Defaults to Revit's 20,000 threshold.",
            GH_ParamAccess.item,
            20_000);
        pManager.AddNumberParameter(
            "Vertical Tolerance",
            "V",
            "Requested maximum measured vertical error in output units. Zero defaults to 0.05 m.",
            GH_ParamAccess.item,
            0.0);
        pManager[2].Optional = true;
        pManager.AddNumberParameter(
            "Profile Tolerance",
            "PT",
            "Profile and XY validation tolerance in source Rhino model units. Zero uses document tolerance.",
            GH_ParamAccess.item,
            0.0);
        pManager[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGenericParameter("Preparation", "T", "Validated MoleHill Toposolid preparation package.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Profiles", "P", "Horizontal outer and hole profiles for the base Toposolid.", GH_ParamAccess.list);
        pManager.AddPointParameter("Elevation Points", "E", "Bounded, constraint-aware elevation points for the Toposolid top face.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Subdivision Profiles", "S", "Horizontal subdivision profiles; one region per tree branch.", GH_ParamAccess.tree);
        pManager.AddTextParameter("Subdivision Names", "SN", "Subdivision names in branch order.", GH_ParamAccess.list);
        pManager.AddTextParameter("Subdivision Keys", "SK", "Stable subdivision keys in branch order.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Breaklines", "B", "Transformed source constraints. Exact Revit TIN edges are not guaranteed.", GH_ParamAccess.list);
        pManager.AddTextParameter("Terrain Key", "K", "Stable terrain key for idempotent downstream element mapping.", GH_ParamAccess.item);
        pManager.AddTextParameter("Geometry Fingerprint", "F", "Deterministic SHA-256 fingerprint of prepared profiles and elevation points.", GH_ParamAccess.item);
        pManager.AddTextParameter("Revision 64", "R64", "Lossless 64-bit terrain revision.", GH_ParamAccess.item);
        pManager.AddTextParameter("Unit System", "U", "Rhino model units retained by the prepared geometry.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Meters Per Unit", "MPU", "Metres represented by one prepared geometry unit.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Maximum Error", "ME", "Maximum measured source-vertex reconstruction error in Rhino model units.", GH_ParamAccess.item);
        pManager.AddTextParameter("Report", "I", "Preparation summary, warnings, and source diagnostics.", GH_ParamAccess.list);
        pManager.AddTextParameter("Subdivision Fingerprints", "SF", "Deterministic fingerprints in subdivision branch order.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        object? source = null;
        if (!DA.GetData(0, ref source) || !TerrainDataAccess.TryGetTerrain(source, out MoleHillTerrainData terrain))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a MoleHill Terrain or mesh.");
            return;
        }

        int maximumPoints = 20_000;
        double verticalTolerance = 0.0;
        double profileTolerance = 0.0;
        DA.GetData(1, ref maximumPoints);
        DA.GetData(2, ref verticalTolerance);
        DA.GetData(3, ref profileTolerance);

        if (profileTolerance <= 0.0)
            profileTolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        var options = new ToposolidPreparation.Options
        {
            MaximumPointCount = maximumPoints,
            VerticalTolerance = verticalTolerance,
            SourceTolerance = profileTolerance
        };
        if (!ToposolidPreparation.TryPrepare(terrain, options, out ToposolidPreparationData? preparation, out IReadOnlyList<string> report) ||
            preparation == null)
        {
            foreach (string message in report)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, message);
            return;
        }

        var subdivisionTree = new GH_Structure<GH_Curve>();
        for (int index = 0; index < preparation.Subdivisions.Count; index++)
        {
            var path = new GH_Path(index);
            subdivisionTree.EnsurePath(path);
            foreach (Curve profile in preparation.Subdivisions[index].Profiles)
                subdivisionTree.Append(new GH_Curve(profile.DuplicateCurve()), path);
        }

        foreach (string message in report.Where(message => message.Contains("budget was reached", StringComparison.OrdinalIgnoreCase)))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, message);

        DA.SetData(0, new ToposolidPreparationGoo(preparation));
        DA.SetDataList(1, preparation.Profiles.Select(profile => profile.DuplicateCurve()));
        DA.SetDataList(2, preparation.ElevationPoints);
        DA.SetDataTree(3, subdivisionTree);
        DA.SetDataList(4, preparation.Subdivisions.Select(subdivision => subdivision.Name));
        DA.SetDataList(5, preparation.Subdivisions.Select(subdivision => subdivision.Key));
        DA.SetDataList(6, preparation.Breaklines.Select(breakline => breakline.DuplicateCurve()));
        DA.SetData(7, preparation.Key);
        DA.SetData(8, preparation.GeometryFingerprint);
        DA.SetData(9, preparation.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        DA.SetData(10, preparation.UnitSystem);
        DA.SetData(11, preparation.MetersPerModelUnit);
        DA.SetData(12, preparation.MaximumMeasuredVerticalError);
        DA.SetDataList(13, report);
        DA.SetDataList(14, preparation.Subdivisions.Select(subdivision => subdivision.GeometryFingerprint));
    }
}
