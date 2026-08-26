// Grasshopper goo wrapper for the open MoleHill terrain payload.
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;
using MoleHill.Shared;
using Rhino;
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
        return Value?.Duplicate();
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
                Value = CreateFromMesh(mesh);
                return true;
            case GH_Mesh meshGoo when meshGoo.Value != null:
                Value = CreateFromMesh(meshGoo.Value);
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

    public override bool Write(GH_IWriter writer)
    {
        if (Value == null)
            return false;

        writer.SetInt32("Schema", 1);
        writer.SetString("Mesh", GooSerialization.Encode(Value.Mesh));
        writer.SetString("Name", Value.Name);
        writer.SetString("Key", Value.Key);
        writer.SetInt64("Revision", Value.Revision);
        writer.SetString("UnitSystem", Value.UnitSystem);
        writer.SetDouble("MetersPerModelUnit", Value.MetersPerModelUnit);
        writer.SetDoubleArray("LocalToWorld", GooSerialization.EncodeTransform(Value.LocalToWorld));
        writer.SetBoolean("HasProjectBaseTransform", Value.HasProjectBaseTransform);
        writer.SetInt32("BreaklineCount", Value.Breaklines.Count);
        for (int index = 0; index < Value.Breaklines.Count; index++)
            writer.SetString("Breakline", index, GooSerialization.Encode(Value.Breaklines[index]));
        writer.SetInt32("RegionCount", Value.Regions.Count);
        for (int regionIndex = 0; regionIndex < Value.Regions.Count; regionIndex++)
        {
            MoleHillTerrainRegion region = Value.Regions[regionIndex];
            writer.SetString("RegionName", regionIndex, region.Name);
            writer.SetString("RegionKey", regionIndex, region.Key);
            writer.SetInt32("RegionBoundaryCount", regionIndex, region.Boundaries.Count);
            for (int boundaryIndex = 0; boundaryIndex < region.Boundaries.Count; boundaryIndex++)
            {
                writer.SetString(
                    $"RegionBoundary{regionIndex}",
                    boundaryIndex,
                    GooSerialization.Encode(region.Boundaries[boundaryIndex]));
            }
        }

        writer.SetInt32("DiagnosticCount", Value.Diagnostics.Count);
        for (int index = 0; index < Value.Diagnostics.Count; index++)
            writer.SetString("Diagnostic", index, Value.Diagnostics[index]);
        return true;
    }

    public override bool Read(GH_IReader reader)
    {
        if (!reader.ItemExists("Schema") || reader.GetInt32("Schema") != 1 || !reader.ItemExists("Mesh"))
            return false;

        Mesh? mesh = GooSerialization.DecodeMesh(reader.GetString("Mesh"));
        if (mesh == null)
            return false;

        var breaklines = new List<Curve>();
        int breaklineCount = reader.ItemExists("BreaklineCount") ? reader.GetInt32("BreaklineCount") : 0;
        for (int index = 0; index < breaklineCount; index++)
        {
            Curve? curve = GooSerialization.DecodeCurve(reader.GetString("Breakline", index));
            if (curve != null)
                breaklines.Add(curve);
        }

        var regions = new List<MoleHillTerrainRegion>();
        int regionCount = reader.ItemExists("RegionCount") ? reader.GetInt32("RegionCount") : 0;
        for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
        {
            var boundaries = new List<Curve>();
            int boundaryCount = reader.GetInt32("RegionBoundaryCount", regionIndex);
            for (int boundaryIndex = 0; boundaryIndex < boundaryCount; boundaryIndex++)
            {
                Curve? boundary = GooSerialization.DecodeCurve(reader.GetString($"RegionBoundary{regionIndex}", boundaryIndex));
                if (boundary != null)
                    boundaries.Add(boundary);
            }

            regions.Add(new MoleHillTerrainRegion(
                reader.GetString("RegionName", regionIndex),
                reader.GetString("RegionKey", regionIndex),
                boundaries));
            foreach (Curve boundary in boundaries)
                boundary.Dispose();
        }

        var diagnostics = new List<string>();
        int diagnosticCount = reader.ItemExists("DiagnosticCount") ? reader.GetInt32("DiagnosticCount") : 0;
        for (int index = 0; index < diagnosticCount; index++)
            diagnostics.Add(reader.GetString("Diagnostic", index));

        Value = new MoleHillTerrainData(
            mesh,
            breaklines,
            regions,
            reader.GetString("Name"),
            reader.GetString("Key"),
            reader.GetInt64("Revision"),
            diagnostics,
            reader.GetString("UnitSystem"),
            reader.GetDouble("MetersPerModelUnit"),
            GooSerialization.DecodeTransform(reader.GetDoubleArray("LocalToWorld")),
            reader.GetBoolean("HasProjectBaseTransform"));
        mesh.Dispose();
        foreach (Curve breakline in breaklines)
            breakline.Dispose();
        return Value.IsValid;
    }

    private static MoleHillTerrainData CreateFromMesh(Mesh mesh)
    {
        ModelUnitContext units = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        return new MoleHillTerrainData(
            mesh,
            unitSystem: units.IsSupported ? units.UnitSystem.ToString() : "Unspecified",
            metersPerModelUnit: units.IsSupported ? units.MetersPerModelUnit : 1.0);
    }
}
