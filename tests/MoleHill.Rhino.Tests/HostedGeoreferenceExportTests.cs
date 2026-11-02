using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>Behavioral export regressions to invoke inside a disposable Rhino (not a managed test host).</summary>
public static class HostedGeoreferenceExportTests
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void RunFileCases(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var source = RhinoDoc.CreateHeadless(null);
        source.ModelUnitSystem = UnitSystem.Meters;
        source.ModelAbsoluteTolerance = 0.0001;
        int layer = source.Layers.Add(new Layer { Name = "Selected curves", Color = System.Drawing.Color.Coral });
        var ids = new List<Guid>();
        for (int i = 0; i < 3; i++)
            ids.Add(source.Objects.AddLine(new Point3d(1, 2, 3 + i), new Point3d(4, 5, 6 + i),
                new ObjectAttributes { LayerIndex = layer, Name = $"Curve {i + 1}" }));

        // An unselected, instantiated block must not contribute either its definition or its mesh.
        using var mesh = new Mesh();
        for (int i = 0; i < 50_000; i++)
            mesh.Vertices.Add(i, i % 97, i % 13);
        for (int i = 2; i < 50_000; i += 3)
            mesh.Faces.AddFace(i - 2, i - 1, i);
        int unused = source.InstanceDefinitions.Add("Unrelated mesh", "", Point3d.Origin,
            new GeometryBase[] { mesh }, new[] { new ObjectAttributes() });
        source.Objects.AddInstanceObject(unused, Transform.Identity);

        // Embedded images and material libraries are document resources, not curve dependencies.
        string imagePath = Path.Combine(outputDirectory, "unrelated.png");
        using (var image = new System.Drawing.Bitmap(512, 512))
        {
            var random = new Random(17);
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                image.SetPixel(x, y, System.Drawing.Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            image.Save(imagePath);
        }
        source.Bitmaps.AddBitmap(imagePath, true);
        using var material = new Material { Name = "Unrelated image material" };
        material.SetBitmapTexture(imagePath);
        source.Materials.Add(material);

        source.Objects.Select(ids[1], true, true);
        source.Modified = false;
        int sourceCount = source.Objects.Count;
        var plane = new Plane(new Point3d(500000, 200000, 0), Vector3d.YAxis, -Vector3d.XAxis);
        source.NamedConstructionPlanes.Add("MoleHill_ProjectBase", plane);
        source.Strings.SetString("Source document data", "Must not transfer");
        source.Modified = false;
        Transform transform = Transform.PlaneToPlane(Plane.WorldXY, plane);
        var expected = new BoundingBox(new Point3d(499995, 200001, 3), new Point3d(499998, 200004, 8));

        foreach (string extension in new[] { "3dm", "dxf", "stp" })
        {
            string path = Path.Combine(outputDirectory, "three-curves." + extension);
            Require(GeoreferenceExportService.WriteSelected(source, ids, transform, path, RunMode.Scripted),
                $"{extension}: write failed");
            using var imported = RhinoDoc.CreateHeadless(null);
            imported.ModelUnitSystem = UnitSystem.Meters;
            Require(imported.Import(path), $"{extension}: import failed");
            RhinoObject[] objects = imported.Objects.Where(obj => !obj.IsInstanceDefinitionGeometry).ToArray();
            Require(objects.Length == 3, $"{extension}: selection leaked or objects were lost");
            var bounds = BoundingBox.Empty;
            foreach (RhinoObject obj in objects)
                bounds.Union(obj.Geometry.GetBoundingBox(true));
            Require(bounds.Min.DistanceTo(expected.Min) < 1e-6 && bounds.Max.DistanceTo(expected.Max) < 1e-6,
                $"{extension}: wrong georeferenced coordinates: {bounds}");
            if (extension == "3dm")
            {
                using File3dm model = File3dm.Read(path);
                Require(model.Settings.ModelUnitSystem == UnitSystem.Meters, "3dm: lost units");
                Require(Math.Abs(model.Settings.ModelAbsoluteTolerance - 0.0001) < 1e-12, "3dm: lost tolerance");
                Require(model.AllInstanceDefinitions.Count == 0, "3dm: unrelated block definition leaked");
                Require(model.AllNamedConstructionPlanes.Count == 0, "3dm: source CPlanes leaked");
                Require(model.Strings.Count == 0, "3dm: document user text leaked");
                Require(new FileInfo(path).Length < 100_000, "3dm: unrelated resource payload leaked");
                Require(objects.All(obj => obj.Attributes.Name?.StartsWith("Curve ") == true), "3dm: lost object names");
                Require(objects.All(obj => imported.Layers[obj.Attributes.LayerIndex].Name == "Selected curves"),
                    "3dm: lost source layer");
            }
            Require(source.Objects.Count == sourceCount && !source.Modified, "Source document changed");
            Require(source.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).SequenceEqual(new[] { ids[1] }),
                "Source selection changed");
            Require(source.Objects.FindId(ids[0]).Geometry.GetBoundingBox(true).Min.DistanceTo(new Point3d(1, 2, 3)) < 1e-12,
                "Source curve moved");
            Console.WriteLine($"PASS {extension}: three curves, correct coordinates, source unchanged; {new FileInfo(path).Length} bytes");
        }

        using var blockCurve = new LineCurve(new Point3d(1, 2, 3), new Point3d(4, 5, 6));
        int required = source.InstanceDefinitions.Add("Required line", "", Point3d.Origin,
            new GeometryBase[] { blockCurve }, new[] { new ObjectAttributes { LayerIndex = layer } });
        Guid block = source.Objects.AddInstanceObject(required, Transform.Identity,
            new ObjectAttributes { LayerIndex = layer, Name = "Selected block" });
        string blockPath = Path.Combine(outputDirectory, "selected-block.3dm");
        Require(GeoreferenceExportService.WriteSelected(source, new[] { block }, transform, blockPath, RunMode.Scripted),
            "Selected block write failed");
        using var blockDoc = RhinoDoc.OpenHeadless(blockPath);
        var instance = blockDoc.Objects.OfType<InstanceObject>().Single();
        Require(instance.Geometry.GetBoundingBox(true).Min.DistanceTo(expected.Min) < 1e-6,
            "Selected block has wrong coordinates");
        Require(instance.InstanceDefinition.Name == "Required line", "Selected block lost its definition");
        using File3dm blockModel = File3dm.Read(blockPath);
        Require(blockModel.AllInstanceDefinitions.Count == 1, "Selected block export leaked unrelated definitions");
        Console.WriteLine("PASS 3dm: selected block retained, unrelated definition excluded");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
