// Exposes MoleHill Terrain data as ordinary Grasshopper mesh, curves, trees, and metadata.
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using MoleHill.Grasshopper.Types;
using MoleHill.Grasshopper.Utilities;

namespace MoleHill.Grasshopper.Components;

public sealed class DeconstructTerrainComponent : GH_Component
{
    public DeconstructTerrainComponent()
        : base(
            "Deconstruct Terrain",
            "DeTerrain",
            "Expose a MoleHill Terrain as ordinary Grasshopper mesh, curve, tree, and text data.",
            "MoleHill",
            "Terrain")
    {
    }

    public override Guid ComponentGuid => new("0A1C05E0-8497-447A-A04A-C00C07BA055D");

    protected override System.Drawing.Bitmap? Icon => MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.DeconstructTerrain.png");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddGenericParameter("Terrain", "T", "MoleHill Terrain, or an ordinary mesh.", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Final terrain mesh.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Breaklines", "B", "Hard terrain constraints or creases.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Zones", "Z", "Zone outlines; one zone per tree branch.", GH_ParamAccess.tree);
        pManager.AddTextParameter("Zone Names", "ZN", "Zone names in branch order.", GH_ParamAccess.list);
        pManager.AddTextParameter("Name", "N", "Terrain display name.", GH_ParamAccess.item);
        pManager.AddTextParameter("Key", "K", "Stable source key when available.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Revision", "R", "Applied MoleHill build revision.", GH_ParamAccess.item);
        pManager.AddTextParameter("Diagnostics", "D", "Source and processing diagnostics.", GH_ParamAccess.list);
        pManager.AddTextParameter("Zone Keys", "ZK", "Stable zone keys in branch order.", GH_ParamAccess.list);
        pManager.AddTextParameter("Revision 64", "R64", "Lossless 64-bit source revision.", GH_ParamAccess.item);
        pManager.AddTextParameter("Unit System", "U", "Source Rhino model unit system.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Meters Per Unit", "MPU", "Metres represented by one source model unit.", GH_ParamAccess.item);
        pManager.AddTransformParameter("Local To World", "X", "MoleHill project-local to real-world transform.", GH_ParamAccess.item);
        pManager.AddBooleanParameter("Has Project Base", "PB", "Whether Local To World represents a saved MoleHill Project Base.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Zone Stack Indices", "ZI", "Original zone stack positions in branch order.", GH_ParamAccess.list);
        pManager.AddBooleanParameter("Zone Enabled", "ZE", "Enabled state in branch order.", GH_ParamAccess.list);
        pManager.AddBooleanParameter("Zone Elevation Priority", "ZEP", "Elevation-priority state in branch order.", GH_ParamAccess.list);
        pManager.AddIntegerParameter("Zone Colors ARGB", "ZC", "Signed ARGB colors in branch order.", GH_ParamAccess.list);
        pManager.AddBooleanParameter("Zone Color Override", "ZCO", "Color override state in branch order.", GH_ParamAccess.list);
        pManager.AddTextParameter("Zone Layers", "ZL", "Output layer hints in branch order.", GH_ParamAccess.list);
        pManager.AddTextParameter("Zone Materials", "ZM", "Output material hints in branch order.", GH_ParamAccess.list);
        pManager.AddBooleanParameter("Zone Split", "ZS", "Native split-to-separate-mesh hints in branch order.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        object? source = null;
        if (!DA.GetData(0, ref source) || !TerrainDataAccess.TryGetTerrain(source, out MoleHillTerrainData terrain))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a MoleHill Terrain or mesh.");
            return;
        }

        var zoneTree = new GH_Structure<GH_Curve>();
        var zoneNames = new List<string>();
        var zoneKeys = new List<string>();
        for (int index = 0; index < terrain.Regions.Count; index++)
        {
            MoleHillTerrainRegion region = terrain.Regions[index];
            var path = new GH_Path(index);
            zoneTree.EnsurePath(path);
            foreach (var boundary in region.Boundaries)
                zoneTree.Append(new GH_Curve(boundary.DuplicateCurve()), path);
            zoneNames.Add(region.Name);
            zoneKeys.Add(region.Key);
        }

        DA.SetData(0, terrain.Mesh.DuplicateMesh());
        DA.SetDataList(1, terrain.Breaklines.Select(curve => curve.DuplicateCurve()));
        DA.SetDataTree(2, zoneTree);
        DA.SetDataList(3, zoneNames);
        DA.SetData(4, terrain.Name);
        DA.SetData(5, terrain.Key);
        DA.SetData(6, ToGrasshopperInteger(terrain.Revision));
        DA.SetDataList(7, terrain.Diagnostics);
        DA.SetDataList(8, zoneKeys);
        DA.SetData(9, terrain.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        DA.SetData(10, terrain.UnitSystem);
        DA.SetData(11, terrain.MetersPerModelUnit);
        DA.SetData(12, terrain.LocalToWorld);
        DA.SetData(13, terrain.HasProjectBaseTransform);
        DA.SetDataList(14, terrain.Regions.Select(region => region.StackIndex));
        DA.SetDataList(15, terrain.Regions.Select(region => region.IsEnabled));
        DA.SetDataList(16, terrain.Regions.Select(region => region.UseInputElevationForPriority));
        DA.SetDataList(17, terrain.Regions.Select(region => region.ColorArgb));
        DA.SetDataList(18, terrain.Regions.Select(region => region.UseColorOverride));
        DA.SetDataList(19, terrain.Regions.Select(region => region.LayerName ?? string.Empty));
        DA.SetDataList(20, terrain.Regions.Select(region => region.MaterialName ?? string.Empty));
        DA.SetDataList(21, terrain.Regions.Select(region => region.SplitToSeparateMesh));
    }

    private static int ToGrasshopperInteger(long value)
    {
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }
}
