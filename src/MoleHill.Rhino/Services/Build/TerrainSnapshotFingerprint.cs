// SHA-256 provenance of completed terrain geometry, constraints, zones, and coordinates.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MoleHill.Interop;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class TerrainSnapshotFingerprint
{
    private static readonly SerializationOptions GeometryOptions = new()
    {
        WriteUserData = false,
        WriteRenderMeshes = false,
        WriteAnalysisMeshes = false
    };
    public static string Compute(
        RhinoDoc doc,
        TerrainInteropSnapshot snapshot,
        IReadOnlyList<Curve> hardConstraints,
        IReadOnlyList<Curve> elevationConstraints,
        IReadOnlyList<CollageZoneDefinition> zones)
    {
        using var writer = new HashWriter();
        writer.AddString("MoleHill terrain content fingerprint v1");
        Mesh mesh = snapshot.Mesh;
        writer.AddInt32(mesh.Vertices.Count);
        foreach (Point3f point in mesh.Vertices)
        {
            writer.AddDouble(point.X);
            writer.AddDouble(point.Y);
            writer.AddDouble(point.Z);
        }
        writer.AddInt32(mesh.Faces.Count);
        foreach (MeshFace face in mesh.Faces)
        {
            writer.AddInt32(face.A);
            writer.AddInt32(face.B);
            writer.AddInt32(face.C);
            writer.AddInt32(face.IsQuad ? face.D : -1);
        }

        AddCurves(writer, "hard", hardConstraints);
        AddCurves(writer, "elevation", elevationConstraints);
        writer.AddInt32(zones.Count);
        for (int index = 0; index < zones.Count; index++)
        {
            CollageZoneDefinition zone = zones[index];
            writer.AddString(zone.ZoneId.ToString("D"));
            writer.AddString(zone.Name);
            writer.AddInt32(index);
            writer.AddBoolean(zone.IsEnabled);
            writer.AddBoolean(zone.UseInputElevationForPriority);
            writer.AddInt32(zone.ColorArgb);
            writer.AddBoolean(zone.UseColorOverride);
            writer.AddString(zone.LayerName);
            writer.AddString(zone.MaterialName);
            writer.AddBoolean(zone.SplitToSeparateMesh);
            AddSourceSet(writer, doc, zone.Boundaries);
        }

        writer.AddInt32(snapshot.Regions.Count);
        foreach (TerrainInteropRegion region in snapshot.Regions)
        {
            writer.AddString(region.Key);
            writer.AddString(region.Name);
            AddCurves(writer, "resolved-zone", region.Boundaries);
        }
        writer.AddString(snapshot.UnitSystem);
        writer.AddDouble(snapshot.MetersPerModelUnit);
        writer.AddBoolean(snapshot.HasProjectBaseTransform);
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                writer.AddDouble(snapshot.LocalToWorld[row, column]);
        return writer.Finish();
    }

    private static void AddCurves(HashWriter writer, string kind, IReadOnlyList<Curve> curves)
    {
        writer.AddString(kind);
        writer.AddInt32(curves.Count);
        foreach (Curve curve in curves)
            writer.AddString(curve.ToJSON(GeometryOptions));
    }

    private static void AddSourceSet(HashWriter writer, RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        writer.AddInt32(sourceSet.ObjectIds.Count);
        var seen = new HashSet<Guid>();
        foreach (Guid id in sourceSet.ObjectIds)
        {
            writer.AddString(id.ToString("D"));
            if (seen.Add(id))
                AddSourceObject(writer, doc.Objects.FindId(id));
        }
        writer.AddInt32(sourceSet.LayerPaths.Count);
        foreach (string layerPath in sourceSet.LayerPaths)
        {
            writer.AddString(layerPath);
            RhinoObject[] layerObjects = doc.Objects.Where(obj =>
                obj.Attributes.LayerIndex >= 0 && obj.Attributes.LayerIndex < doc.Layers.Count &&
                string.Equals(doc.Layers[obj.Attributes.LayerIndex].FullPath, layerPath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(obj => obj.Id)
                .ToArray();
            writer.AddInt32(layerObjects.Length);
            foreach (RhinoObject obj in layerObjects)
            {
                writer.AddString(obj.Id.ToString("D"));
                if (seen.Add(obj.Id))
                    AddSourceObject(writer, obj);
            }
        }
    }

    private static void AddSourceObject(HashWriter writer, RhinoObject? obj)
    {
        if (obj?.Geometry == null)
        {
            writer.AddString("missing-source");
            return;
        }
        writer.AddString(obj.Geometry.ObjectType.ToString());
        writer.AddString(obj.Geometry.ToJSON(GeometryOptions));
    }

    private sealed class HashWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void AddBoolean(bool value) => AddInt32(value ? 1 : 0);

        public void AddInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void AddDouble(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            _hash.AppendData(bytes);
        }

        public void AddString(string? value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            AddInt32(bytes.Length);
            _hash.AppendData(bytes);
        }

        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        public void Dispose() => _hash.Dispose();
    }
}
