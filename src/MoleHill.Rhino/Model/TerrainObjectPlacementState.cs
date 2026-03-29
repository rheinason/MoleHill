using Rhino.Geometry;

namespace MoleHill.Rhino.Model;

public sealed class TerrainObjectPlacementState
{
    public Guid ObjectId { get; set; }

    public double[] LastAppliedTransform { get; set; } = CreateIdentityMatrix();

    public Transform GetLastAppliedTransform()
    {
        var values = LastAppliedTransform;
        if (values == null || values.Length != 16)
            return Transform.Identity;

        var transform = Transform.Identity;
        transform.M00 = values[0];
        transform.M01 = values[1];
        transform.M02 = values[2];
        transform.M03 = values[3];
        transform.M10 = values[4];
        transform.M11 = values[5];
        transform.M12 = values[6];
        transform.M13 = values[7];
        transform.M20 = values[8];
        transform.M21 = values[9];
        transform.M22 = values[10];
        transform.M23 = values[11];
        transform.M30 = values[12];
        transform.M31 = values[13];
        transform.M32 = values[14];
        transform.M33 = values[15];
        return transform;
    }

    public void SetLastAppliedTransform(Transform transform)
    {
        LastAppliedTransform =
        [
            transform.M00, transform.M01, transform.M02, transform.M03,
            transform.M10, transform.M11, transform.M12, transform.M13,
            transform.M20, transform.M21, transform.M22, transform.M23,
            transform.M30, transform.M31, transform.M32, transform.M33
        ];
    }

    public bool ReplaceObject(Guid oldObjectId, Guid newObjectId)
    {
        if (ObjectId != oldObjectId || newObjectId == Guid.Empty)
            return false;

        ObjectId = newObjectId;
        return true;
    }

    private static double[] CreateIdentityMatrix()
    {
        return
        [
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0
        ];
    }
}
