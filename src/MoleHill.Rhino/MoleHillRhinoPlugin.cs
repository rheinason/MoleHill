using MoleHill.Rhino.Services;
using MoleHill.Rhino.UI;
using System.Drawing;
using System.Reflection;
using Rhino;
using Rhino.FileIO;
using Rhino.PlugIns;
using Rhino.UI;

namespace MoleHill.Rhino;

public sealed class MoleHillRhinoPlugin : PlugIn
{
    private readonly TerrainDocumentStore _documentStore = new();
    private static Icon? _panelIcon;

    public static MoleHillRhinoPlugin Instance { get; private set; } = null!;

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    public MoleHillRhinoPlugin()
    {
        Instance = this;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        TerrainController.Instance.Initialize();
        Panels.RegisterPanel(this, typeof(MoleHillPanel), "MoleHill", GetPanelIcon());
        return LoadReturnCode.Success;
    }

    protected override bool ShouldCallWriteDocument(FileWriteOptions options)
    {
        return true;
    }

    protected override void WriteDocument(RhinoDoc doc, BinaryArchiveWriter archive, FileWriteOptions options)
    {
        string json = TerrainSerializer.Serialize(TerrainController.Instance.GetTerrains(doc));
        _documentStore.SaveJson(doc, json);
        archive.WriteString(json);
    }

    protected override void ReadDocument(RhinoDoc doc, BinaryArchiveReader archive, FileReadOptions options)
    {
        string json = archive.ReadString();
        if (!string.IsNullOrWhiteSpace(json))
            _documentStore.SaveJson(doc, json);

        TerrainController.Instance.ReloadDocumentState(doc);
    }

    private static Icon? GetPanelIcon()
    {
        if (_panelIcon != null)
            return _panelIcon;

        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MoleHill.Rhino.EmbeddedResources.plugin-utility.ico");
        if (stream == null)
            return null;

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        _panelIcon = new Icon(copy);
        return _panelIcon;
    }
}
