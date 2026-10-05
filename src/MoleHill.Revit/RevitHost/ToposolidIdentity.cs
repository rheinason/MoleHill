// MoleHill identity (stable key + geometry fingerprint) stored on Revit Toposolids through Extensible Storage.
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace MoleHill.Revit.RevitHost;

internal static class ToposolidIdentity
{
    /// <summary>
    /// The schema the Python adapter wrote, kept so Toposolids it created are still recognised. A schema
    /// is immutable once a document holds it, so its GUID and fields can never change.
    /// </summary>
    public static readonly Guid SchemaGuid = new("b57636d2-a64d-4e6b-bb42-c770b99bb5c7");

    /// <summary>
    /// The Python subdivision adapter's schema, read only. Subdivisions are now stored under
    /// <see cref="SchemaGuid"/> and told apart by their <c>HostTopoId</c>.
    /// </summary>
    public static readonly Guid LegacySubdivisionSchemaGuid = new("36fcfa94-d039-4f31-a379-0c973c4490eb");

    private const string KeyField = "MoleHillKey";
    private const string FingerprintField = "GeometryFingerprint";

    public static Schema GetOrCreateSchema()
    {
        Schema? schema = Schema.Lookup(SchemaGuid);
        if (schema != null)
            return schema;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName("MoleHillToposolidIdentity");
        builder.SetDocumentation("Stable MoleHill terrain key and prepared-geometry fingerprint.");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(KeyField, typeof(string));
        builder.AddSimpleField(FingerprintField, typeof(string));
        return builder.Finish();
    }

    public static (string? Key, string? Fingerprint) Read(Element element)
    {
        foreach (Guid guid in new[] { SchemaGuid, LegacySubdivisionSchemaGuid })
        {
            Schema? schema = Schema.Lookup(guid);
            if (schema == null)
                continue;
            Entity entity = element.GetEntity(schema);
            if (entity != null && entity.IsValid())
                return (entity.Get<string>(schema.GetField(KeyField)), entity.Get<string>(schema.GetField(FingerprintField)));
        }

        return (null, null);
    }

    public static void Write(Element element, Schema schema, string key, string fingerprint)
    {
        var entity = new Entity(schema);
        entity.Set(schema.GetField(KeyField), key);
        entity.Set(schema.GetField(FingerprintField), fingerprint);
        element.SetEntity(entity);
    }

    public static bool IsSubdivision(Toposolid toposolid) => toposolid.HostTopoId != ElementId.InvalidElementId;
}
