// Registration and payload smoke tests for the open Grasshopper terrain workflow.
using MoleHill.Grasshopper.Components;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class TerrainComponentSmokeTests
{
    [Fact]
    public void TerrainComponents_RegisterOpenWorkflowPorts()
    {
        var snapshot = new MoleHillTerrainSnapshotComponent();
        var construct = new ConstructTerrainComponent();
        var deconstruct = new DeconstructTerrainComponent();
        var partition = new PartitionTerrainComponent();
        var prepareToposolid = new PrepareToposolidComponent();

        Assert.Equal("Terrain", snapshot.Params.Output[0].Name);
        Assert.Equal("Zones", construct.Params.Input[2].Name);
        Assert.Equal(global::Grasshopper.Kernel.GH_ParamAccess.tree, construct.Params.Input[2].Access);
        Assert.Equal("Zone Keys", construct.Params.Input[6].Name);
        Assert.Equal("Revision 64", construct.Params.Input[7].Name);
        Assert.Equal("Zones", deconstruct.Params.Output[2].Name);
        Assert.Equal(global::Grasshopper.Kernel.GH_ParamAccess.tree, deconstruct.Params.Output[2].Access);
        Assert.Equal("Zone Keys", deconstruct.Params.Output[8].Name);
        Assert.Equal("Revision 64", deconstruct.Params.Output[9].Name);
        Assert.Equal("Zones", partition.Params.Input[1].Name);
        Assert.Equal(global::Grasshopper.Kernel.GH_ParamAccess.tree, partition.Params.Output[0].Access);
        Assert.Equal("Remainder", partition.Params.Output[2].Name);
        Assert.Equal("Geometry Fingerprint", prepareToposolid.Params.Output[8].Name);
    }

    [RhinoNativeFact]
    public void MoleHillTerrainData_DuplicatesOpenGeometryInputs()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(1.0, 0.0, 0.0);
        mesh.Vertices.Add(0.0, 1.0, 1.0);
        mesh.Faces.AddFace(0, 1, 2);
        var breakline = new LineCurve(Point3d.Origin, new Point3d(1.0, 0.0, 0.0));
        var region = new MoleHillTerrainRegion(
            "Paving",
            "paving",
            new[] { new PolylineCurve(new[] { Point3d.Origin, new Point3d(1.0, 0.0, 0.0), new Point3d(0.0, 1.0, 0.0), Point3d.Origin }) });

        var terrain = new MoleHillTerrainData(mesh, new[] { breakline }, new[] { region }, "Site", "site", 12);
        mesh.Vertices.SetVertex(0, 99.0, 99.0, 99.0);

        Assert.True(terrain.IsValid);
        Assert.Equal("Site", terrain.Name);
        Assert.Equal(12, terrain.Revision);
        Assert.Equal(0.0f, terrain.Mesh.Vertices[0].X);
        Assert.Single(terrain.Breaklines);
        Assert.Single(terrain.Regions);
        Assert.Equal("Paving", terrain.Regions[0].Name);
    }
}
