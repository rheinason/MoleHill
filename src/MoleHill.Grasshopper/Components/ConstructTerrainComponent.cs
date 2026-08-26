// Packages ordinary Grasshopper terrain geometry and zone branches as MoleHill Terrain data.
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using MoleHill.Grasshopper.Types;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public sealed class ConstructTerrainComponent : GH_Component
{
    public ConstructTerrainComponent()
        : base(
            "Construct Terrain",
            "Terrain",
            "Construct an open MoleHill Terrain from an ordinary mesh, breaklines, and optional zone branches.",
            "MoleHill",
            "Terrain")
    {
    }

    public override Guid ComponentGuid => new("6D7F0920-47D6-4B06-9C43-75A70B32815E");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Final 2.5D terrain mesh.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Breaklines", "B", "Optional hard terrain constraints or creases.", GH_ParamAccess.list);
        pManager[1].Optional = true;
        pManager.AddCurveParameter("Zones", "Z", "Optional closed zone outlines. Each tree branch defines one zone.", GH_ParamAccess.tree);
        pManager[2].Optional = true;
        pManager.AddTextParameter("Zone Names", "ZN", "Optional zone names in branch order.", GH_ParamAccess.list);
        pManager[3].Optional = true;
        pManager.AddTextParameter("Name", "N", "Terrain display name.", GH_ParamAccess.item, "Terrain");
        pManager[4].Optional = true;
        pManager.AddTextParameter("Key", "K", "Optional stable source key.", GH_ParamAccess.item, string.Empty);
        pManager[5].Optional = true;
        pManager.AddTextParameter("Zone Keys", "ZK", "Optional stable zone keys in branch order.", GH_ParamAccess.list);
        pManager[6].Optional = true;
        pManager.AddTextParameter("Revision 64", "R64", "Optional lossless 64-bit source revision.", GH_ParamAccess.item, "0");
        pManager[7].Optional = true;
        pManager.AddTextParameter("Diagnostics", "D", "Optional source and processing diagnostics.", GH_ParamAccess.list);
        pManager[8].Optional = true;
        pManager.AddTextParameter("Unit System", "U", "Source Rhino model unit system.", GH_ParamAccess.item, string.Empty);
        pManager[9].Optional = true;
        pManager.AddNumberParameter("Meters Per Unit", "MPU", "Metres represented by one source model unit.", GH_ParamAccess.item, 0.0);
        pManager[10].Optional = true;
        pManager.AddTransformParameter("Local To World", "X", "Optional MoleHill project-local to real-world transform.", GH_ParamAccess.item);
        pManager[11].Optional = true;
        pManager.AddBooleanParameter("Has Project Base", "PB", "Whether Local To World represents a saved MoleHill Project Base.", GH_ParamAccess.item, false);
        pManager[12].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGenericParameter("Terrain", "T", "MoleHill Terrain data.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Mesh? mesh = null;
        if (!DA.GetData(0, ref mesh) || mesh == null)
            return;

        var breaklines = new List<Curve>();
        DA.GetDataList(1, breaklines);

        GH_Structure<GH_Curve>? zoneTree = null;
        DA.GetDataTree(2, out zoneTree);
        var zoneNames = new List<string>();
        DA.GetDataList(3, zoneNames);
        string name = "Terrain";
        DA.GetData(4, ref name);
        string key = string.Empty;
        DA.GetData(5, ref key);
        var zoneKeys = new List<string>();
        DA.GetDataList(6, zoneKeys);
        string revisionText = "0";
        DA.GetData(7, ref revisionText);
        if (!long.TryParse(revisionText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long revision))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Revision 64 must be a signed 64-bit integer.");
            return;
        }

        var diagnostics = new List<string>();
        DA.GetDataList(8, diagnostics);
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        string unitSystem = unitContext.IsSupported ? unitContext.UnitSystem.ToString() : "Unspecified";
        DA.GetData(9, ref unitSystem);
        double metersPerModelUnit = unitContext.IsSupported ? unitContext.MetersPerModelUnit : 1.0;
        DA.GetData(10, ref metersPerModelUnit);
        Transform localToWorld = Transform.Identity;
        bool hasTransformInput = DA.GetData(11, ref localToWorld);
        bool hasProjectBaseTransform = false;
        DA.GetData(12, ref hasProjectBaseTransform);
        hasProjectBaseTransform &= hasTransformInput;

        var regions = new List<MoleHillTerrainRegion>();
        if (zoneTree != null)
        {
            for (int branchIndex = 0; branchIndex < zoneTree.PathCount; branchIndex++)
            {
                GH_Path path = zoneTree.Paths[branchIndex];
                Curve[] boundaries = zoneTree.get_Branch(path)
                    .OfType<GH_Curve>()
                    .Where(item => item.Value != null)
                    .Select(item => item.Value)
                    .ToArray();
                if (boundaries.Length == 0)
                    continue;

                string zoneName = branchIndex < zoneNames.Count && !string.IsNullOrWhiteSpace(zoneNames[branchIndex])
                    ? zoneNames[branchIndex]
                    : $"Zone {branchIndex + 1}";
                string zoneKey = branchIndex < zoneKeys.Count && !string.IsNullOrWhiteSpace(zoneKeys[branchIndex])
                    ? zoneKeys[branchIndex]
                    : path.ToString();
                regions.Add(new MoleHillTerrainRegion(zoneName, zoneKey, boundaries));
            }
        }

        var terrain = new MoleHillTerrainData(
            mesh,
            breaklines,
            regions,
            name,
            key,
            revision,
            diagnostics,
            unitSystem,
            metersPerModelUnit,
            localToWorld,
            hasProjectBaseTransform);
        if (!terrain.IsValid)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Terrain mesh is empty or invalid.");
            return;
        }

        DA.SetData(0, new MoleHillTerrainGoo(terrain));
    }
}
