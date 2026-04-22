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
    private readonly LayerTemplateStore _layerTemplateStore = new();
    private static Icon? _panelIcon;

    public static MoleHillRhinoPlugin Instance { get; private set; } = null!;

    internal LayerTemplateStore LayerTemplateStore => _layerTemplateStore;

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    public MoleHillRhinoPlugin()
    {
        Instance = this;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        TerrainController.Instance.Initialize();
        Panels.RegisterPanel(this, typeof(MoleHillPanel), "MoleHill", GetPanelIcon());
        ToolbarInstaller.EnsureInstalled();
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        TerrainController.Instance.Shutdown();
        base.OnShutdown();
    }

    protected override bool ShouldCallWriteDocument(FileWriteOptions options)
    {
        return ShouldPersistTerrainDataOnWrite(options);
    }

    protected override void WriteDocument(RhinoDoc doc, BinaryArchiveWriter archive, FileWriteOptions options)
    {
        if (!ShouldPersistTerrainDataOnWrite(options))
            return;

        string json = TerrainSerializer.Serialize(TerrainController.Instance.GetTerrains(doc));
        _documentStore.SaveJson(doc, json);
        archive.WriteString(json);
    }

    protected override void ReadDocument(RhinoDoc doc, BinaryArchiveReader archive, FileReadOptions options)
    {
        if (!ShouldLoadTerrainDataOnRead(options))
            return;

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

    private static bool ShouldPersistTerrainDataOnWrite(FileWriteOptions options)
    {
        // Rhino can serialize plug-in document data for clipboard, export-selected, and other
        // partial-document writes. Persisting MoleHill state there leads to stale terrain data
        // being merged back into unrelated documents during paste/import/linked-block workflows.
        return !options.WriteSelectedObjectsOnly &&
               !options.WriteGeometryOnly;
    }

    private static bool ShouldLoadTerrainDataOnRead(FileReadOptions options)
    {
        // Only hydrate document-scoped terrain state when Rhino is opening a full document or
        // creating a new document from a template. Import/paste/insert/reference reads must not
        // overwrite the active document's existing MoleHill terrain store.
        return options.OpenMode || options.NewMode;
    }
}
