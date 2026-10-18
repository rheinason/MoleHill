using MoleHill.Core.Scan;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Copies Rhino observations once on the UI thread. Importers can retain LAS classes as a
/// byte array on PointCloud.UserDictionary["MoleHill.Scan.Classification"].</summary>
internal static class ScanPointCloudReader
{
    public static ScanPointCloud Read(PointCloud cloud)
    {
        var xyz = new double[checked(cloud.Count * 3)];
        int[]? colours = cloud.ContainsColors ? new int[cloud.Count] : null;
        byte[]? classes = cloud.UserDictionary.ContainsKey("MoleHill.Scan.Classification")
            ? cloud.UserDictionary["MoleHill.Scan.Classification"] as byte[] : null;
        if (cloud.UserDictionary.ContainsKey("MoleHill.Scan.Classification") && classes == null)
            throw new ArgumentException("Cloud classification must be a byte array in point order.");
        for (int i = 0; i < cloud.Count; i++)
        {
            Point3d point = cloud[i].Location;
            xyz[i * 3] = point.X; xyz[i * 3 + 1] = point.Y; xyz[i * 3 + 2] = point.Z;
            if (colours != null)
            {
                var colour = cloud[i].Color;
                colours[i] = colour.R << 16 | colour.G << 8 | colour.B;
            }
        }
        return new ScanPointCloud(xyz, colours, classes == null ? null : (byte[])classes.Clone());
    }
}
