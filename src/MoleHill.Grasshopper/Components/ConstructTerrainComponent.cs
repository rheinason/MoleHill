// Packages ordinary Grasshopper terrain geometry and zone branches as MoleHill Terrain data.
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using MoleHill.Grasshopper.Types;
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

        var regions = new List<MoleHillTerrainRegion>();
        if (zoneTree != null)
        {
            for (int branchIndex = 0; branchIndex < zoneTree.PathCount; branchIndex++)
            {
                GH_Path path = zoneTree.Paths[branchIndex];
                Curve[] boundaries = zoneTree.get_Branch(path)
                    .OfType<GH_Curve>()
                    .Where(item => item.Value != null)
                    .Select(item => item.Value.DuplicateCurve())
                    .ToArray();
                if (boundaries.Length == 0)
                    continue;

                string zoneName = branchIndex < zoneNames.Count && !string.IsNullOrWhiteSpace(zoneNames[branchIndex])
                    ? zoneNames[branchIndex]
                    : $"Zone {branchIndex + 1}";
                regions.Add(new MoleHillTerrainRegion(zoneName, path.ToString(), boundaries));
            }
        }

        var terrain = new MoleHillTerrainData(mesh, breaklines, regions, name, key);
        if (!terrain.IsValid)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Terrain mesh is empty or invalid.");
            return;
        }

        DA.SetData(0, new MoleHillTerrainGoo(terrain));
    }
}
