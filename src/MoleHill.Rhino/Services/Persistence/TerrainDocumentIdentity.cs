// Persistent MoleHill document lineage used by bound Grasshopper terrain references.
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class TerrainDocumentIdentity
{
    private const string Section = "MoleHill.Rhino";
    private const string Entry = "DocumentIdentity";

    public static string? Get(RhinoDoc doc)
    {
        string? stored = doc.Strings.GetValue(Section, Entry);
        return Guid.TryParse(stored, out Guid id) ? id.ToString("D") : null;
    }

    // Called on MoleHill document saves or an explicit GH picker action, never from a GH solve.
    public static string Ensure(RhinoDoc doc)
    {
        string? existing = Get(doc);
        if (existing != null)
            return existing;
        string created = Guid.NewGuid().ToString("D");
        doc.Strings.SetString(Section, Entry, created);
        return created;
    }
}
