// Registration and payload smoke tests for the open Grasshopper terrain workflow.
using MoleHill.Grasshopper.Components;
using MoleHill.Grasshopper.Types;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class TerrainComponentSmokeTests
{
    [RhinoNativeFact]
    public void LegacySnapshotArchive_DeserializesAndPreservesWire()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-snapshot-20260914.gh");
        var archive = new GH_Archive();
        Assert.True(archive.ReadFromFile(path));
        GH_IReader definition = archive.GetRootNode.FindChunk("Definition")!;
        var document = new GH_Document();

        Assert.True(document.Read(definition));
        var snapshot = Assert.IsType<MoleHillTerrainSnapshotComponent>(document.Objects[0]);
        var deconstruct = Assert.IsType<DeconstructTerrainComponent>(document.Objects[1]);
        Assert.Equal(12, snapshot.Params.Output.Count);
        Assert.Single(deconstruct.Params.Input[0].Sources);
        Assert.Same(snapshot.Params.Output[0], deconstruct.Params.Input[0].Sources[0]);
    }

    [Fact]
    public void TerrainComponents_RegisterOpenWorkflowPorts()
    {
        var snapshot = new MoleHillTerrainSnapshotComponent();
        var construct = new ConstructTerrainComponent();
        var deconstruct = new DeconstructTerrainComponent();
        var partition = new PartitionTerrainComponent();
        var prepareToposolid = new PrepareToposolidComponent();
        var balancePad = new BalanceGradePadComponent();
        var simplify = new MeshSimplifyComponent();
        var projectTo = new ProjectToComponent();
        var addGeometry = new AddGeometryComponent();
        var gradePad = new GradePadComponent();
        var smooth = new MeshSmoothComponent();
        var remesh = new RemeshComponent();
        var retainingWall = new RetainingWallComponent();
        var gradePath = new GradePathComponent();
        var inSituStair = new InSituStairComponent();
        var retopo = new RetopoComponent();

        Assert.Equal("Terrain", snapshot.Params.Output[0].Name);
        Assert.Equal(new Guid("83749F9C-86ED-4517-B4AF-9B21E65B7EAE"), snapshot.ComponentGuid);
        Assert.Equal("Has Project Base", snapshot.Params.Output[9].Name);
        Assert.Equal("Status", snapshot.Params.Output[10].Name);
        Assert.Equal("Fingerprint", snapshot.Params.Output[11].Name);
        Assert.Equal("Zones", construct.Params.Input[2].Name);
        Assert.Equal(global::Grasshopper.Kernel.GH_ParamAccess.tree, construct.Params.Input[2].Access);
        Assert.Equal("Zone Keys", construct.Params.Input[6].Name);
        Assert.Equal("Revision 64", construct.Params.Input[7].Name);
        Assert.Equal("Zone Enabled", construct.Params.Input[13].Name);
        Assert.Equal("Zone Stack Indices", construct.Params.Input[20].Name);
        Assert.Equal("Zones", deconstruct.Params.Output[2].Name);
        Assert.Equal(global::Grasshopper.Kernel.GH_ParamAccess.tree, deconstruct.Params.Output[2].Access);
        Assert.Equal("Zone Keys", deconstruct.Params.Output[8].Name);
        Assert.Equal("Revision 64", deconstruct.Params.Output[9].Name);
        Assert.Equal("Zone Stack Indices", deconstruct.Params.Output[14].Name);
        Assert.Equal("Zone Split", deconstruct.Params.Output[21].Name);
        Assert.Equal("Zones", partition.Params.Input[1].Name);
        Assert.Equal("Keys", partition.Params.Input[5].Name);
        Assert.Equal(global::Grasshopper.Kernel.GH_ParamAccess.tree, partition.Params.Output[0].Access);
        Assert.Equal("Remainder", partition.Params.Output[2].Name);
        Assert.Equal("Geometry Fingerprint", prepareToposolid.Params.Output[8].Name);
        Assert.Equal("Minimum Elevation", balancePad.Params.Input[2].Name);
        Assert.Equal("Chosen Boundary", balancePad.Params.Output[1].Name);
        Assert.Equal("Status", balancePad.Params.Output[6].Name);
        Assert.Equal("Sample Net", balancePad.Params.Output[11].Name);
        Assert.Equal("Terrain", balancePad.Params.Input[11].Name);
        Assert.Equal("Terrain", balancePad.Params.Output[12].Name);
        Assert.Equal("Simplify", simplify.NickName);
        Assert.Equal("Required Edges", simplify.Params.Input[4].Name);
        Assert.Equal("Retain Percentage", simplify.Params.Input[5].Name);
        Assert.Equal("Terrain", simplify.Params.Input[6].Name);
        Assert.Equal("Status", simplify.Params.Output[3].Name);
        Assert.Equal("Terrain", simplify.Params.Output[4].Name);
        Assert.Equal("Target", projectTo.Params.Input[1].Name);
        Assert.Equal("Terrain", projectTo.Params.Input[5].Name);
        Assert.Equal("Changed Vertices", projectTo.Params.Output[1].Name);
        Assert.Equal("Terrain", projectTo.Params.Output[2].Name);
        Assert.Equal("Breaklines", addGeometry.Params.Input[2].Name);
        Assert.Equal("Terrain", addGeometry.Params.Input[4].Name);
        Assert.Equal("Boundary", addGeometry.Params.Input[5].Name);
        Assert.Equal("Added Vertices", addGeometry.Params.Output[1].Name);
        Assert.Equal("Terrain", addGeometry.Params.Output[2].Name);
        Assert.Equal("Terrain", gradePad.Params.Input[7].Name);
        Assert.Equal("Terrain", gradePad.Params.Output[4].Name);
        Assert.Equal("Terrain", smooth.Params.Input[6].Name);
        Assert.Equal("Terrain", smooth.Params.Output[1].Name);
        Assert.Equal("Terrain", remesh.Params.Input[5].Name);
        Assert.Equal("Terrain", remesh.Params.Output[3].Name);
        Assert.Equal("Terrain", retainingWall.Params.Input[3].Name);
        Assert.Equal("Terrain", retainingWall.Params.Output[4].Name);
        Assert.Equal("Terrain", gradePath.Params.Input[8].Name);
        Assert.Equal("Terrain", gradePath.Params.Output[4].Name);
        Assert.Equal("Terrain", inSituStair.Params.Input[5].Name);
        Assert.Equal("Terrain", inSituStair.Params.Output[8].Name);
        Assert.Equal("Retopo", retopo.NickName);
        Assert.Equal("Quad Count", retopo.Params.Output[1].Name);
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
