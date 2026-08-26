// Splits one terrain into zone pieces derived from a single shared-topology Core result.
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using MoleHill.Core.Grading;
using MoleHill.Grasshopper.Types;
using MoleHill.Grasshopper.Utilities;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public sealed class PartitionTerrainComponent : GH_Component
{
    private sealed class RegionInput
    {
        public required GH_Path Path { get; init; }

        public required string Name { get; init; }

        public required string Key { get; init; }

        public List<Curve> Curves { get; } = new();

        public HashSet<int> AreaIndexes { get; } = new();
    }

    public PartitionTerrainComponent()
        : base(
            "Partition Terrain",
            "Partition",
            "Insert closed zone boundaries into one terrain TIN and output matching terrain pieces with identical shared seam vertices.",
            "MoleHill",
            "Terrain")
    {
    }

    public override Guid ComponentGuid => new("EE4A022D-5125-4199-9C71-C6E6D924BF7D");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddGenericParameter("Terrain", "T", "MoleHill Terrain, or an ordinary TIN mesh.", GH_ParamAccess.item);
        pManager.AddCurveParameter(
            "Zones",
            "Z",
            "Closed partition outlines, one output terrain per tree branch. When omitted, embedded MoleHill zones are used.",
            GH_ParamAccess.tree);
        pManager[1].Optional = true;
        pManager.AddTextParameter("Names", "N", "Optional terrain-piece names in branch order.", GH_ParamAccess.list);
        pManager[2].Optional = true;
        pManager.AddBooleanParameter("Remainder", "R", "Output terrain outside all zones.", GH_ParamAccess.item, true);
        pManager[3].Optional = true;
        pManager.AddNumberParameter("Tolerance", "Tol", "Boundary insertion tolerance. 0 uses Rhino document tolerance.", GH_ParamAccess.item, 0.0);
        pManager[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGenericParameter("Terrains", "T", "Partitioned MoleHill Terrains in the same paths as the zone input.", GH_ParamAccess.tree);
        pManager.AddMeshParameter("Meshes", "M", "Ordinary partitioned meshes in the same paths as the zone input.", GH_ParamAccess.tree);
        pManager.AddGenericParameter("Remainder", "R", "Terrain outside all zones, when requested.", GH_ParamAccess.item);
        pManager.AddTextParameter("Report", "I", "Partition summary and warnings.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        object? source = null;
        if (!DA.GetData(0, ref source) || !TerrainDataAccess.TryGetTerrain(source, out MoleHillTerrainData terrain))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a MoleHill Terrain or mesh.");
            return;
        }

        GH_Structure<GH_Curve>? boundaryTree = null;
        DA.GetDataTree(1, out boundaryTree);
        var names = new List<string>();
        DA.GetDataList(2, names);
        bool includeRemainder = true;
        DA.GetData(3, ref includeRemainder);
        double tolerance = 0.0;
        DA.GetData(4, ref tolerance);
        if (tolerance <= 0.0)
            tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

        List<RegionInput> regions = CreateRegionInputs(boundaryTree, names, terrain);
        if (regions.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide closed zone curves or a MoleHill Terrain containing zones.");
            return;
        }

        if (!TerrainPartitionGeometry.TryExtractTriangleMesh(
                terrain.Mesh,
                out double[] vertices,
                out int[] faces,
                out string? meshWarning))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, meshWarning ?? "Could not read the terrain mesh.");
            return;
        }

        var report = new List<string>();
        if (!string.IsNullOrWhiteSpace(meshWarning))
            report.Add(meshWarning);

        var boundaries = new List<MeshAreaSplitter.AreaBoundary>();
        foreach (RegionInput region in regions)
        {
            foreach (Curve curve in region.Curves)
            {
                if (!TerrainPartitionGeometry.TryCreateBoundary(curve, tolerance, out var boundary, out _))
                {
                    report.Add($"{region.Name}: skipped an open or invalid boundary.");
                    continue;
                }

                region.AreaIndexes.Add(boundaries.Count);
                boundaries.Add(boundary);
            }
        }

        if (boundaries.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid closed zone boundaries were found.");
            return;
        }

        MeshAreaSplitter.SplitResult? result = MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            boundaries.ToArray(),
            tolerance,
            out string? splitWarning);
        if (result == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, splitWarning ?? "Terrain partition failed.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(splitWarning))
            report.Add(splitWarning);

        var terrainTree = new GH_Structure<MoleHillTerrainGoo>();
        var meshTree = new GH_Structure<GH_Mesh>();
        int outputCount = 0;
        foreach (RegionInput region in regions)
        {
            terrainTree.EnsurePath(region.Path);
            meshTree.EnsurePath(region.Path);
            if (region.AreaIndexes.Count == 0)
                continue;

            Mesh pieceMesh = TerrainPartitionGeometry.BuildSubMesh(result, region.AreaIndexes);
            if (pieceMesh.Faces.Count == 0)
            {
                report.Add($"{region.Name}: boundary does not cover any terrain faces.");
                continue;
            }

            string pieceKey = CreateChildKey(terrain.Key, region.Key, region.Name);
            var piece = new MoleHillTerrainData(
                pieceMesh,
                terrain.Breaklines,
                name: region.Name,
                key: pieceKey,
                revision: terrain.Revision,
                diagnostics: terrain.Diagnostics);
            terrainTree.Append(new MoleHillTerrainGoo(piece), region.Path);
            meshTree.Append(new GH_Mesh(pieceMesh.DuplicateMesh()), region.Path);
            outputCount++;
        }

        MoleHillTerrainGoo? remainderGoo = null;
        if (includeRemainder)
        {
            Mesh remainderMesh = TerrainPartitionGeometry.BuildRemainderMesh(result);
            if (remainderMesh.Faces.Count > 0)
            {
                var remainder = new MoleHillTerrainData(
                    remainderMesh,
                    terrain.Breaklines,
                    name: $"{terrain.Name} Remainder",
                    key: CreateChildKey(terrain.Key, "remainder", "Remainder"),
                    revision: terrain.Revision,
                    diagnostics: terrain.Diagnostics);
                remainderGoo = new MoleHillTerrainGoo(remainder);
            }
        }

        report.Insert(
            0,
            $"Inserted {boundaries.Count:N0} boundaries once; created {outputCount:N0} terrain pieces from {result.FaceCount:N0} shared-topology faces.");
        DA.SetDataTree(0, terrainTree);
        DA.SetDataTree(1, meshTree);
        if (remainderGoo != null)
            DA.SetData(2, remainderGoo);
        DA.SetDataList(3, report);
    }

    private static List<RegionInput> CreateRegionInputs(
        GH_Structure<GH_Curve>? tree,
        IReadOnlyList<string> names,
        MoleHillTerrainData terrain)
    {
        var result = new List<RegionInput>();
        if (tree != null && tree.DataCount > 0)
        {
            for (int branchIndex = 0; branchIndex < tree.PathCount; branchIndex++)
            {
                GH_Path path = tree.Paths[branchIndex];
                string name = branchIndex < names.Count && !string.IsNullOrWhiteSpace(names[branchIndex])
                    ? names[branchIndex]
                    : $"Zone {branchIndex + 1}";
                var region = new RegionInput
                {
                    Path = path,
                    Name = name,
                    Key = path.ToString()
                };
                region.Curves.AddRange(tree.get_Branch(path)
                    .OfType<GH_Curve>()
                    .Where(item => item.Value != null)
                    .Select(item => item.Value.DuplicateCurve()));
                result.Add(region);
            }

            return result;
        }

        for (int index = 0; index < terrain.Regions.Count; index++)
        {
            MoleHillTerrainRegion source = terrain.Regions[index];
            var region = new RegionInput
            {
                Path = new GH_Path(index),
                Name = source.Name,
                Key = source.Key
            };
            region.Curves.AddRange(source.Boundaries.Select(curve => curve.DuplicateCurve()));
            result.Add(region);
        }

        return result;
    }

    private static string CreateChildKey(string parentKey, string childKey, string fallbackName)
    {
        string resolvedChild = string.IsNullOrWhiteSpace(childKey) ? fallbackName : childKey;
        return string.IsNullOrWhiteSpace(parentKey)
            ? resolvedChild
            : $"{parentKey}/{resolvedChild}";
    }
}
