using GH_IO.Serialization;
using MoleHill.Grasshopper.Components;
using MoleHill.Grasshopper.Types;
using MoleHill.Grasshopper.Utilities;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public sealed class ToposolidPreparationTests
{
    [Fact]
    public void PrepareToposolidComponent_RegistersNeutralContract()
    {
        var component = new PrepareToposolidComponent();

        Assert.Equal("Terrain", component.Params.Input[0].Name);
        Assert.Equal("Maximum Points", component.Params.Input[1].Name);
        Assert.Equal("Preparation", component.Params.Output[0].Name);
        Assert.Equal("Profiles", component.Params.Output[1].Name);
        Assert.Equal("Elevation Points", component.Params.Output[2].Name);
        Assert.Equal("Geometry Fingerprint", component.Params.Output[8].Name);
        Assert.Equal("Meters Per Unit", component.Params.Output[11].Name);
    }

    [RhinoNativeFact]
    public void TryPrepare_GridTerrain_RespectsBudgetAndIsDeterministic()
    {
        using Mesh mesh = CreateGridMesh(21, 21, (x, y) => Math.Sin(x * 0.2) + Math.Cos(y * 0.15));
        var terrain = new MoleHillTerrainData(
            mesh,
            name: "Site",
            key: "site-key",
            revision: 42,
            unitSystem: "Meters",
            metersPerModelUnit: 1.0);
        var options = new ToposolidPreparation.Options
        {
            MaximumPointCount = 100,
            VerticalTolerance = 0.01,
            SourceTolerance = 0.001
        };

        Assert.True(ToposolidPreparation.TryPrepare(terrain, options, out var first, out var firstReport),
            string.Join(Environment.NewLine, firstReport));
        Assert.True(ToposolidPreparation.TryPrepare(terrain, options, out var second, out var secondReport),
            string.Join(Environment.NewLine, secondReport));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange(first!.ElevationPoints.Count, 3, 100);
        Assert.NotEmpty(first.Profiles);
        Assert.Equal("site-key", first.Key);
        Assert.Equal(42, first.Revision);
        Assert.Equal(first.GeometryFingerprint, second!.GeometryFingerprint);
        Assert.Equal(first.ElevationPoints, second.ElevationPoints);
    }

    [RhinoNativeFact]
    public void TryPrepare_StackedXyVertices_RejectsNonHeightfield()
    {
        using var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(1.0, 0.0, 0.0);
        mesh.Vertices.Add(0.0, 1.0, 0.0);
        mesh.Vertices.Add(0.0, 0.0, 2.0);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Faces.AddFace(3, 2, 1);
        var terrain = new MoleHillTerrainData(mesh, unitSystem: "Meters", metersPerModelUnit: 1.0);

        bool success = ToposolidPreparation.TryPrepare(
            terrain,
            new ToposolidPreparation.Options { MaximumPointCount = 100, SourceTolerance = 0.001 },
            out _,
            out IReadOnlyList<string> report);

        Assert.False(success);
        Assert.Contains(report, message => message.Contains("single-valued heightfield", StringComparison.OrdinalIgnoreCase));
    }

    [RhinoNativeFact]
    public void MoleHillTerrainData_Duplicate_PreservesExchangeMetadata()
    {
        using Mesh mesh = CreateGridMesh(2, 2, (x, y) => x + y);
        Transform localToWorld = Transform.Translation(100.0, 200.0, 0.0);
        var terrain = new MoleHillTerrainData(
            mesh,
            name: "Site",
            key: "key",
            revision: long.MaxValue,
            diagnostics: new[] { "ok" },
            unitSystem: "Millimeters",
            metersPerModelUnit: 0.001,
            localToWorld: localToWorld,
            hasProjectBaseTransform: true);

        MoleHillTerrainData duplicate = terrain.Duplicate();

        Assert.Equal(long.MaxValue, duplicate.Revision);
        Assert.Equal("Millimeters", duplicate.UnitSystem);
        Assert.Equal(0.001, duplicate.MetersPerModelUnit);
        Assert.True(duplicate.HasProjectBaseTransform);
        Assert.Equal(100.0, duplicate.LocalToWorld.M03);
        Assert.Equal(200.0, duplicate.LocalToWorld.M13);
    }

    [RhinoNativeFact]
    public void MoleHillTerrainGoo_WriteRead_PreservesPersistentContract()
    {
        using Mesh mesh = CreateGridMesh(2, 2, (x, y) => x + y);
        var terrain = new MoleHillTerrainData(
            mesh,
            name: "Site",
            key: "site-key",
            revision: long.MaxValue,
            diagnostics: new[] { "diagnostic" },
            unitSystem: "Meters",
            metersPerModelUnit: 1.0,
            localToWorld: Transform.Translation(10.0, 20.0, 0.0),
            hasProjectBaseTransform: true);
        var source = new MoleHillTerrainGoo(terrain);
        var archive = new GH_Archive();

        Assert.True(source.Write(archive.CreateTopLevelNode("Terrain")));
        byte[] bytes = archive.Serialize_Binary();
        var restoredArchive = new GH_Archive();
        Assert.True(restoredArchive.Deserialize_Binary(bytes));
        var restored = new MoleHillTerrainGoo();
        Assert.True(restored.Read(restoredArchive.GetRootNode));

        Assert.NotNull(restored.Value);
        Assert.Equal("site-key", restored.Value!.Key);
        Assert.Equal(long.MaxValue, restored.Value.Revision);
        Assert.Equal("Meters", restored.Value.UnitSystem);
        Assert.True(restored.Value.HasProjectBaseTransform);
        Assert.Equal(10.0, restored.Value.LocalToWorld.M03);
        Assert.Equal("diagnostic", Assert.Single(restored.Value.Diagnostics));
    }

    private static Mesh CreateGridMesh(int xCount, int yCount, Func<double, double, double> elevation)
    {
        var mesh = new Mesh();
        for (int y = 0; y < yCount; y++)
        {
            for (int x = 0; x < xCount; x++)
                mesh.Vertices.Add(x, y, elevation(x, y));
        }

        for (int y = 0; y < yCount - 1; y++)
        {
            for (int x = 0; x < xCount - 1; x++)
            {
                int a = (y * xCount) + x;
                int b = a + 1;
                int d = ((y + 1) * xCount) + x;
                int c = d + 1;
                mesh.Faces.AddFace(a, b, c);
                mesh.Faces.AddFace(a, c, d);
            }
        }

        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }
}
