using System.Runtime.InteropServices;
using Rhino.DocObjects.Custom;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Object-owned base, deliberately independent of any terrain/card identity.</summary>
[Guid("0F27D78C-5649-4801-961A-6D459BFD9B33")]
public sealed class ObjectPlacementReference : UserData
{
    public Point3d[] BoxCorners { get; internal set; } = Array.Empty<Point3d>();
    public Point3d[] BaseCorners { get; internal set; } = Array.Empty<Point3d>();
    public Vector3d Up { get; internal set; } = Vector3d.ZAxis;
    public bool IsCustomBase { get; internal set; }
    internal Transform LastPlacement { get; set; } = Transform.Identity;
    [ThreadStatic] internal static bool ApplyingPlacement = false;

    public override string Description => "MoleHill placement base";
    public override bool ShouldWrite => IsValid;
    internal bool IsValid => BoxCorners.Length == 8 && BaseCorners.Length == 4 &&
        BoxCorners.All(p => p.IsValid) && BaseCorners.All(p => p.IsValid) && TryGetPlane(out _);

    public ObjectPlacementReference() { }

    internal ObjectPlacementReference CopyReference() => new()
    {
        BoxCorners = (Point3d[])BoxCorners.Clone(), BaseCorners = (Point3d[])BaseCorners.Clone(),
        Up = Up, IsCustomBase = IsCustomBase, LastPlacement = LastPlacement
    };

    internal bool TryGetPlane(out Plane plane)
    {
        plane = Plane.Unset;
        if (BaseCorners.Length != 4) return false;
        Vector3d x = BaseCorners[1] - BaseCorners[0];
        Vector3d y = BaseCorners[3] - BaseCorners[0];
        if (!x.Unitize() || !y.Unitize()) return false;
        if (Vector3d.Multiply(Vector3d.CrossProduct(x, y), Up) < 0) y = -y;
        Point3d origin = Point3d.Origin;
        foreach (var p in BaseCorners) origin += (p - Point3d.Origin) * 0.25;
        plane = new Plane(origin, x, y);
        return plane.IsValid;
    }

    internal void TransformReference(Transform transform)
    {
        for (int i = 0; i < BoxCorners.Length; i++) BoxCorners[i].Transform(transform);
        for (int i = 0; i < BaseCorners.Length; i++) BaseCorners[i].Transform(transform);
        Vector3d up = Up;
        up.Transform(transform);
        Up = up;
    }

    protected override void OnTransform(Transform transform)
    {
        base.OnTransform(transform);
        TransformReference(transform);
        if (ApplyingPlacement)
            LastPlacement = transform * LastPlacement;
        else if (transform.TryGetInverse(out var inverse))
            LastPlacement = transform * LastPlacement * inverse;
        else
            LastPlacement = Transform.Unset;
    }

    protected override void OnDuplicate(UserData source)
    {
        if (source is not ObjectPlacementReference reference) return;
        BoxCorners = (Point3d[])reference.BoxCorners.Clone();
        BaseCorners = (Point3d[])reference.BaseCorners.Clone();
        Up = reference.Up;
        IsCustomBase = reference.IsCustomBase;
        LastPlacement = reference.LastPlacement;
    }

    protected override bool Write(BinaryArchiveWriter archive)
    {
        var data = new global::Rhino.Collections.ArchivableDictionary(1, "MoleHill placement base");
        for (int i = 0; i < 8; i++) data.Set("box" + i, BoxCorners[i]);
        for (int i = 0; i < 4; i++) data.Set("base" + i, BaseCorners[i]);
        data.Set("up", Up);
        data.Set("custom", IsCustomBase);
        data.Set("placement", LastPlacement);
        archive.WriteDictionary(data);
        return true;
    }

    protected override bool Read(BinaryArchiveReader archive)
    {
        var data = archive.ReadDictionary();
        if (data.Version != 1) return false;
        BoxCorners = Enumerable.Range(0, 8).Select(i => data.GetPoint3d("box" + i)).ToArray();
        BaseCorners = Enumerable.Range(0, 4).Select(i => data.GetPoint3d("base" + i)).ToArray();
        Up = data.GetVector3d("up");
        IsCustomBase = data.GetBool("custom");
        LastPlacement = data.ContainsKey("placement") ? (Transform)data["placement"] : Transform.Identity;
        return IsValid;
    }

    internal static ObjectPlacementReference? Find(GeometryBase geometry) =>
        geometry.UserData.Find(typeof(ObjectPlacementReference)) as ObjectPlacementReference;
}
