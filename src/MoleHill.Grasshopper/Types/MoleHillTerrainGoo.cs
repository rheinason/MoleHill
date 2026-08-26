// Grasshopper goo wrapper for the open MoleHill terrain payload.
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Types;

public sealed class MoleHillTerrainGoo : GH_Goo<MoleHillTerrainData>
{
    public MoleHillTerrainGoo()
    {
    }

    public MoleHillTerrainGoo(MoleHillTerrainData value)
        : base(value)
    {
    }

    public override bool IsValid => Value?.IsValid == true;

    public override string IsValidWhyNot => Value == null
        ? "No terrain data."
        : Value.IsValid
            ? string.Empty
            : "Terrain mesh is empty or invalid.";

    public override string TypeName => "MoleHill Terrain";

    public override string TypeDescription => "An open MoleHill terrain package containing a mesh, breaklines, regions, and source metadata.";

    public override IGH_Goo Duplicate()
    {
        return Value == null ? new MoleHillTerrainGoo() : new MoleHillTerrainGoo(Value.Duplicate());
    }

    public override object? ScriptVariable()
    {
        return Value;
    }

    public override bool CastFrom(object source)
    {
        switch (source)
        {
            case MoleHillTerrainData terrain:
                Value = terrain.Duplicate();
                return true;
            case MoleHillTerrainGoo goo when goo.Value != null:
                Value = goo.Value.Duplicate();
                return true;
            case Mesh mesh:
                Value = new MoleHillTerrainData(mesh);
                return true;
            case GH_Mesh meshGoo when meshGoo.Value != null:
                Value = new MoleHillTerrainData(meshGoo.Value);
                return true;
            default:
                return false;
        }
    }

    public override bool CastTo<Q>(ref Q target)
    {
        if (Value == null)
            return false;

        if (typeof(Q).IsAssignableFrom(typeof(MoleHillTerrainData)))
        {
            object terrain = Value.Duplicate();
            target = (Q)terrain;
            return true;
        }

        if (typeof(Q).IsAssignableFrom(typeof(Mesh)))
        {
            object mesh = Value.Mesh.DuplicateMesh();
            target = (Q)mesh;
            return true;
        }

        if (typeof(Q).IsAssignableFrom(typeof(GH_Mesh)))
        {
            object meshGoo = new GH_Mesh(Value.Mesh.DuplicateMesh());
            target = (Q)meshGoo;
            return true;
        }

        return false;
    }

    public override string ToString()
    {
        if (Value == null)
            return "Null MoleHill Terrain";

        return $"MoleHill Terrain ({Value.Name}, {Value.Mesh.Faces.Count:N0} faces, {Value.Regions.Count:N0} zones)";
    }
}
