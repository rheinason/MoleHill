using Rhino;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDocumentStore
{
    private const string Section = "MoleHill.Rhino";
    private const string Entry = "Terrains";

    public string? LoadJson(RhinoDoc doc) => doc.Strings.GetValue(Section, Entry);

    public List<TerrainDefinition> Load(RhinoDoc doc)
    {
        string? json = LoadJson(doc);
        return TerrainSerializer.Deserialize(json, doc.ModelUnitSystem);
    }

    public void SaveJson(RhinoDoc doc, string json)
    {
        doc.Strings.SetString(Section, Entry, json);
    }

    public void Save(RhinoDoc doc, IReadOnlyList<TerrainDefinition> terrains)
    {
        string json = TerrainSerializer.Serialize(terrains);
        SaveJson(doc, json);
    }
}
