// Reads final Rhino-panel terrain snapshots through the optional typed host bridge.
using System.Reflection;
using Grasshopper.Kernel;
using GH_IO.Serialization;
using MoleHill.Grasshopper.Types;
using MoleHill.Interop;
using Rhino;
using Rhino.Geometry;
#if WINDOWS
using System.Windows.Forms;
#endif

namespace MoleHill.Grasshopper.Components;

public sealed class MoleHillTerrainSnapshotComponent : GH_Component
{
    private const int BridgeContractVersion = 4;
    private const string BridgeTypeName = "MoleHill.Rhino.Services.TerrainGrasshopperBridge";
    private ITerrainSnapshotBridge? _bridge;
    private EventHandler? _snapshotChangedHandler;
    private string _referenceMode = "Bound";
    private string _boundKey = string.Empty;
    private string _boundName = string.Empty;
    private string _boundDocumentId = string.Empty;
    private uint _boundDocumentSerial;
    private bool _legacyModeMissing;
    private MoleHillTerrainData? _lastCompleted;
    private MoleHillTerrainData? _frozenTerrain;
    private string _frozenSourceDocumentId = string.Empty;
    private uint _frozenSourceDocumentSerial;
    private string _frozenSourceKey = string.Empty;
    private bool _frozenSourceChanged;
    private bool _frozenSourceCheckPending = true;
    private string _observedStatus = string.Empty;
    private string _observedStatusMessage = string.Empty;
    private string _observedFingerprint = string.Empty;
    private string _observedName = string.Empty;
    private string _observedReferenceKey = string.Empty;
    private uint _observedDocumentSerial;
    private bool _observedInputOverride;
    private uint _lastDocumentSerial;
    private int _refreshPending;

    public MoleHillTerrainSnapshotComponent()
        : base(
            "MoleHill Terrain Snapshot",
            "Terrain Snapshot",
            "Read the latest completed final terrain from the MoleHill Rhino panel. Updates when MoleHill state changes.",
            "MoleHill",
            "Terrain")
    {
    }

    public override Guid ComponentGuid => new("83749F9C-86ED-4517-B4AF-9B21E65B7EAE");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter(
            "Terrain",
            "T",
            "Optional MoleHill terrain name or GUID. Empty uses the terrain selected in the Rhino panel.",
            GH_ParamAccess.item,
            string.Empty);
        pManager[0].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGenericParameter("Terrain", "T", "Live MoleHill Terrain snapshot.", GH_ParamAccess.item);
        pManager.AddTextParameter("Name", "N", "MoleHill terrain name.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Revision", "R", "Applied final MoleHill build revision.", GH_ParamAccess.item);
        pManager.AddTextParameter("Diagnostics", "D", "Snapshot and build diagnostics.", GH_ParamAccess.list);
        pManager.AddTextParameter("Key", "K", "Stable MoleHill terrain key.", GH_ParamAccess.item);
        pManager.AddTextParameter("Revision 64", "R64", "Lossless 64-bit applied final revision.", GH_ParamAccess.item);
        pManager.AddTextParameter("Unit System", "U", "Source Rhino model unit system.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Meters Per Unit", "MPU", "Metres represented by one source model unit.", GH_ParamAccess.item);
        pManager.AddTransformParameter("Local To World", "X", "MoleHill project-local to real-world transform.", GH_ParamAccess.item);
        pManager.AddBooleanParameter("Has Project Base", "PB", "Whether Local To World represents a saved MoleHill Project Base.", GH_ParamAccess.item);
        pManager.AddTextParameter("Status", "St", "Current or unavailable source state.", GH_ParamAccess.item);
        pManager.AddTextParameter("Fingerprint", "Fp", "SHA-256 fingerprint of terrain content and source geometry.", GH_ParamAccess.item);
    }

    public override void AddedToDocument(GH_Document document)
    {
        base.AddedToDocument(document);
        TrySubscribeToSnapshotChanges();
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        UnsubscribeFromSnapshotChanges();
        System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
        base.RemovedFromDocument(document);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        TrySubscribeToSnapshotChanges();
        if (_frozenTerrain != null)
        {
            try { CheckFrozenSource(); }
            catch (Exception exception)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Could not compare the frozen terrain with its source: {exception.Message}");
            }
            EmitTerrain(DA, _frozenTerrain);
            DA.SetData(10, _frozenSourceChanged ? "FrozenSourceChanged" : "Frozen");
            Message = _frozenSourceChanged ? $"Frozen · source changed: {_frozenTerrain.Name}" :
                $"Frozen: {_frozenTerrain.Name}";
            return;
        }
        RhinoDoc? activeDoc = RhinoDoc.ActiveDoc;
        if (activeDoc == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No active Rhino document.");
            return;
        }

        string terrainKey = string.Empty;
        DA.GetData(0, ref terrainKey);
        if (_legacyModeMissing)
        {
            _referenceMode = string.IsNullOrWhiteSpace(terrainKey) ? "Follow" : "Bound";
            _legacyModeMissing = false;
        }

        bool inputOverride = !string.IsNullOrWhiteSpace(terrainKey);
        _observedInputOverride = inputOverride;
        if (!inputOverride && _referenceMode == "Bound")
            terrainKey = _boundKey;
        Message = inputOverride ? $"Input: {terrainKey}" :
            _referenceMode == "Follow" ? "Following panel selection" :
            string.IsNullOrWhiteSpace(_boundKey) ? "Live: choose terrain" : $"Live: {_boundName}";
        ITerrainSnapshotBridge? bridge = FindBridge();
        if (bridge == null)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "The MoleHill Rhino plugin is not loaded. Install and load the combined MoleHill package before using Terrain Snapshot.");
            return;
        }

        if (bridge.ContractVersion != BridgeContractVersion)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                $"MoleHill terrain bridge contract mismatch: Grasshopper expects version {BridgeContractVersion}, Rhino provides {bridge.ContractVersion}. Update both MoleHill plugins together.");
            return;
        }

        RhinoDoc doc = activeDoc;
        if (!inputOverride && _referenceMode == "Bound")
        {
            if (string.IsNullOrWhiteSpace(_boundKey))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Choose a MoleHill terrain from the component menu or supply the Terrain text input.");
                DA.SetData(10, "Unavailable");
                return;
            }
            RhinoDoc? sourceDoc = FindBoundDocument(bridge, out string? bindingError);
            if (sourceDoc == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, bindingError ?? "Bound Rhino document is unavailable.");
                DA.SetData(10, "Unavailable");
                return;
            }
            doc = sourceDoc;
        }

        TerrainInteropSnapshot? snapshot;
        string? snapshotError;
        try
        {
            snapshot = bridge.GetSnapshot(doc, terrainKey, out snapshotError);
        }
        catch (Exception exception) when (exception is TargetInvocationException or
                                           ArgumentException or
                                           MethodAccessException or
                                           TargetParameterCountException)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                exception.Message);
            return;
        }

        string status = bridge.GetStatus(doc, terrainKey, out string? statusMessage);
        _observedStatus = status;
        _observedStatusMessage = statusMessage ?? string.Empty;
        _observedReferenceKey = bridge.ResolveReferenceKey(doc, terrainKey) ?? string.Empty;
        _observedDocumentSerial = doc.RuntimeSerialNumber;
        if (snapshot == null)
        {
            string? resolvedKey = bridge.ResolveReferenceKey(doc, terrainKey);
            bool canHold = (status == "Rebuilding" || status == "Failed") &&
                _lastCompleted != null && _lastDocumentSerial == doc.RuntimeSerialNumber &&
                string.Equals(_lastCompleted.Key, resolvedKey, StringComparison.OrdinalIgnoreCase);
            if (canHold)
            {
                EmitTerrain(DA, _lastCompleted!);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    snapshotError ?? statusMessage ?? $"Terrain is {status.ToLowerInvariant()}.");
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    snapshotError ?? statusMessage ?? "MoleHill did not return a terrain snapshot.");
            }
            DA.SetData(10, status);
            _observedFingerprint = canHold ? _lastCompleted!.Fingerprint : string.Empty;
            _observedName = canHold ? _lastCompleted!.Name : string.Empty;
            return;
        }

        try
        {
            Mesh mesh = snapshot.Mesh;
            if (mesh == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "MoleHill snapshot did not contain a terrain mesh.");
                return;
            }

            string name = snapshot.Name;
            string key = snapshot.Key;
            long revision = snapshot.Revision;
            IReadOnlyList<Curve> breaklines = snapshot.Breaklines;
            IReadOnlyList<MoleHillTerrainRegion> regions = snapshot.Regions
                .Select(region => CreateTerrainRegion(region))
                .ToArray();
            IReadOnlyList<string> diagnostics = snapshot.Diagnostics;
            string unitSystem = snapshot.UnitSystem;
            double metersPerModelUnit = snapshot.MetersPerModelUnit;
            Transform localToWorld = snapshot.LocalToWorld;
            bool hasProjectBaseTransform = snapshot.HasProjectBaseTransform;

            var terrain = new MoleHillTerrainData(
                mesh,
                breaklines,
                regions,
                name,
                key,
                revision,
                diagnostics,
                unitSystem,
                metersPerModelUnit,
                localToWorld,
                hasProjectBaseTransform,
                snapshot.Fingerprint);
            _lastCompleted = terrain;
            _observedFingerprint = terrain.Fingerprint;
            _observedName = terrain.Name;
            _lastDocumentSerial = doc.RuntimeSerialNumber;
            EmitTerrain(DA, terrain);
            DA.SetData(10, status);
            if (status != "Current")
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    statusMessage ?? $"The source changed while this snapshot was read; status is {status}.");
            Message = inputOverride ? $"Input: {name}" :
                _referenceMode == "Follow" ? $"Following: {name}" : $"Live: {name}";
            if (!inputOverride && _referenceMode == "Bound")
                _boundName = name;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not read the MoleHill terrain snapshot: {exception.Message}");
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    private void TrySubscribeToSnapshotChanges()
    {
        if (_bridge != null)
            return;

        ITerrainSnapshotBridge? bridge = FindBridge();
        if (bridge == null || bridge.ContractVersion != BridgeContractVersion)
            return;

        _snapshotChangedHandler = OnSnapshotChanged;
        bridge.SnapshotChanged += _snapshotChangedHandler;
        _bridge = bridge;
    }

    private void UnsubscribeFromSnapshotChanges()
    {
        if (_bridge != null && _snapshotChangedHandler != null)
            _bridge.SnapshotChanged -= _snapshotChangedHandler;
        _bridge = null;
        _snapshotChangedHandler = null;
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        _frozenSourceCheckPending = true;
        GH_Document? document = OnPingDocument();
        if (document == null || System.Threading.Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
            return;
        document.ScheduleSolution(5, _ =>
        {
            System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
            bool shouldExpire;
            try { shouldExpire = ShouldExpireForSourceChange(); }
            catch (Exception) { shouldExpire = true; }
            if (shouldExpire)
                ExpireSolution(false);
        });
    }

    private bool ShouldExpireForSourceChange()
    {
        if (_frozenTerrain != null)
        {
            bool wasChanged = _frozenSourceChanged;
            CheckFrozenSource();
            return wasChanged != _frozenSourceChanged;
        }
        if (_observedInputOverride || _observedDocumentSerial == 0)
            return true;
        ITerrainSnapshotBridge? bridge = FindBridge();
        if (bridge?.ContractVersion != BridgeContractVersion)
            return true;
        RhinoDoc? sourceDoc = _referenceMode == "Bound" ? FindBoundDocument(bridge, out _) : RhinoDoc.ActiveDoc;
        if (sourceDoc == null)
            return _observedStatus != "Unavailable";
        string terrainKey = _referenceMode == "Bound" ? _boundKey : string.Empty;
        string status = bridge.GetStatus(sourceDoc, terrainKey, out string? message);
        string key = bridge.ResolveReferenceKey(sourceDoc, terrainKey) ?? string.Empty;
        if (sourceDoc.RuntimeSerialNumber != _observedDocumentSerial ||
            status != _observedStatus || key != _observedReferenceKey ||
            (status != "Current" && (message ?? string.Empty) != _observedStatusMessage))
            return true;
        if (status != "Current")
            return false;
        using TerrainInteropSnapshot? current = bridge.GetSnapshot(sourceDoc, terrainKey, out _);
        return current == null || !string.Equals(current.Fingerprint, _observedFingerprint,
            StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Name, _observedName, StringComparison.Ordinal);
    }

#if WINDOWS
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem picker = new("Choose MoleHill terrain");
        ITerrainSnapshotBridge? bridge = FindBridge();
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (bridge?.ContractVersion == BridgeContractVersion && doc != null)
        {
            TerrainInteropReference[] references = bridge.GetReferences(doc).ToArray();
            string? documentId = bridge.GetDocumentIdentity(doc);
            foreach (TerrainInteropReference reference in references)
            {
                bool duplicateName = references.Count(candidate =>
                    string.Equals(candidate.Name, reference.Name, StringComparison.OrdinalIgnoreCase)) > 1;
                string label = duplicateName ? $"{reference.Name} ({reference.Key[..4]})" : reference.Name;
                ToolStripMenuItem item = new(label) { Checked = _referenceMode == "Bound" &&
                    _boundKey == reference.Key && _boundDocumentId == documentId };
                item.Click += (_, _) =>
                {
                    _referenceMode = "Bound";
                    _boundKey = reference.Key;
                    _boundName = reference.Name;
                    _boundDocumentId = bridge.EnsureDocumentIdentity(doc);
                    _boundDocumentSerial = doc.RuntimeSerialNumber;
                    ExpireSolution(true);
                };
                picker.DropDownItems.Add(item);
            }
        }
        if (picker.DropDownItems.Count == 0)
            picker.DropDownItems.Add(new ToolStripMenuItem("No terrains available") { Enabled = false });
        menu.Items.Add(picker);
        ToolStripMenuItem follow = new("Follow panel selection") { Checked = _referenceMode == "Follow" };
        follow.Click += (_, _) => { _referenceMode = "Follow"; ExpireSolution(true); };
        menu.Items.Add(follow);
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem freeze = new("Freeze completed terrain") { Enabled = _lastCompleted != null && _frozenTerrain == null };
        freeze.Click += (_, _) =>
        {
            _frozenTerrain = _lastCompleted?.Duplicate();
            _frozenSourceKey = _frozenTerrain?.Key ?? string.Empty;
            RhinoDoc? sourceDoc = _lastDocumentSerial == 0 ? null :
                RhinoDoc.OpenDocuments().FirstOrDefault(candidate =>
                    candidate.RuntimeSerialNumber == _lastDocumentSerial);
            _frozenSourceDocumentId = bridge?.ContractVersion == BridgeContractVersion && sourceDoc != null
                ? bridge.EnsureDocumentIdentity(sourceDoc) : string.Empty;
            _frozenSourceDocumentSerial = _lastDocumentSerial;
            _frozenSourceChanged = false;
            _frozenSourceCheckPending = false;
            ExpireSolution(true);
        };
        menu.Items.Add(freeze);
        ToolStripMenuItem refresh = new("Refresh frozen terrain") { Enabled = _frozenTerrain != null && bridge?.ContractVersion == BridgeContractVersion && doc != null };
        refresh.Click += (_, _) => RefreshFrozenTerrain();
        menu.Items.Add(refresh);
        ToolStripMenuItem resume = new("Resume live terrain") { Enabled = _frozenTerrain != null };
        resume.Click += (_, _) => { _frozenTerrain = null; ExpireSolution(true); };
        menu.Items.Add(resume);
    }
#endif

    public override bool Write(GH_IWriter writer)
    {
        writer.SetString("ReferenceMode", _referenceMode);
        writer.SetString("BoundTerrainKey", _boundKey);
        writer.SetString("BoundTerrainName", _boundName);
        writer.SetString("BoundDocumentIdentity", _boundDocumentId);
        writer.SetBoolean("TerrainFrozen", _frozenTerrain != null);
        writer.SetString("FrozenSourceDocumentIdentity", _frozenSourceDocumentId);
        writer.SetString("FrozenSourceKey", _frozenSourceKey);
        if (_frozenTerrain != null)
        {
            GH_IWriter chunk = writer.CreateChunk("FrozenTerrain");
            if (!new MoleHillTerrainGoo(_frozenTerrain).Write(chunk))
                return false;
        }
        return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
        bool result = base.Read(reader);
        _legacyModeMissing = !reader.ItemExists("ReferenceMode");
        _referenceMode = _legacyModeMissing ? "Bound" : reader.GetString("ReferenceMode");
        _boundKey = reader.ItemExists("BoundTerrainKey") ? reader.GetString("BoundTerrainKey") : string.Empty;
        _boundName = reader.ItemExists("BoundTerrainName") ? reader.GetString("BoundTerrainName") : string.Empty;
        _boundDocumentId = reader.ItemExists("BoundDocumentIdentity") ? reader.GetString("BoundDocumentIdentity") : string.Empty;
        _boundDocumentSerial = 0;
        _frozenTerrain = null;
        _frozenSourceDocumentId = reader.ItemExists("FrozenSourceDocumentIdentity")
            ? reader.GetString("FrozenSourceDocumentIdentity") : string.Empty;
        _frozenSourceKey = reader.ItemExists("FrozenSourceKey")
            ? reader.GetString("FrozenSourceKey") : string.Empty;
        _frozenSourceDocumentSerial = 0;
        _frozenSourceChanged = false;
        _frozenSourceCheckPending = true;
        if (reader.ItemExists("TerrainFrozen") && reader.GetBoolean("TerrainFrozen"))
        {
            GH_IReader? chunk = reader.FindChunk("FrozenTerrain");
            if (chunk == null)
                return false;
            var goo = new MoleHillTerrainGoo();
            if (!goo.Read(chunk) || goo.Value == null)
                return false;
            _frozenTerrain = goo.Value;
        }
        return result;
    }

#if WINDOWS
    // Only the Windows context menu can ask for a refresh.
    private void RefreshFrozenTerrain()
    {
        ITerrainSnapshotBridge? bridge = FindBridge();
        RhinoDoc? activeDoc = RhinoDoc.ActiveDoc;
        if (bridge?.ContractVersion != BridgeContractVersion || activeDoc == null)
            return;
        RhinoDoc? sourceDoc = string.IsNullOrWhiteSpace(_frozenSourceDocumentId)
            ? activeDoc : FindFrozenSourceDocument(bridge);
        if (sourceDoc == null)
        {
            Rhino.RhinoApp.WriteLine("MoleHill frozen terrain refresh: bound source document is unavailable.");
            return;
        }
        TerrainInteropSnapshot? snapshot = bridge.GetSnapshot(sourceDoc, _frozenSourceKey,
            out string? error);
        if (snapshot == null)
        {
            Rhino.RhinoApp.WriteLine($"MoleHill frozen terrain refresh: {error ?? "source has no completed final build"}");
            return;
        }
        using (snapshot)
        {
            var regions = snapshot.Regions.Select(CreateTerrainRegion).ToArray();
            _frozenTerrain = new MoleHillTerrainData(snapshot.Mesh, snapshot.Breaklines, regions,
                snapshot.Name, snapshot.Key, snapshot.Revision, snapshot.Diagnostics, snapshot.UnitSystem,
                snapshot.MetersPerModelUnit, snapshot.LocalToWorld, snapshot.HasProjectBaseTransform,
                snapshot.Fingerprint);
        }
        _frozenSourceChanged = false;
        _frozenSourceCheckPending = false;
        _frozenSourceDocumentId = bridge.EnsureDocumentIdentity(sourceDoc);
        _frozenSourceDocumentSerial = sourceDoc.RuntimeSerialNumber;
        ExpireSolution(true);
    }
#endif

    private RhinoDoc? FindFrozenSourceDocument(ITerrainSnapshotBridge bridge)
    {
        if (string.IsNullOrWhiteSpace(_frozenSourceDocumentId))
            return null;
        RhinoDoc[] matches = RhinoDoc.OpenDocuments().Where(doc =>
            string.Equals(bridge.GetDocumentIdentity(doc), _frozenSourceDocumentId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
            return matches[0];
        return matches.FirstOrDefault(doc => _frozenSourceDocumentSerial != 0 &&
            doc.RuntimeSerialNumber == _frozenSourceDocumentSerial);
    }

    private void CheckFrozenSource()
    {
        if (!_frozenSourceCheckPending || _frozenTerrain == null)
            return;
        ITerrainSnapshotBridge? bridge = FindBridge();
        if (bridge?.ContractVersion != BridgeContractVersion)
            return;
        RhinoDoc? sourceDoc = FindFrozenSourceDocument(bridge);
        if (sourceDoc == null)
            return;
        if (bridge.GetStatus(sourceDoc, _frozenSourceKey, out _) != "Current")
            return;
        using TerrainInteropSnapshot? current = bridge.GetSnapshot(sourceDoc, _frozenSourceKey, out _);
        if (current == null)
            return;
        _frozenSourceChanged = !string.Equals(_frozenTerrain.Fingerprint, current.Fingerprint,
            StringComparison.OrdinalIgnoreCase);
        _frozenSourceCheckPending = false;
    }

    private RhinoDoc? FindBoundDocument(ITerrainSnapshotBridge bridge, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_boundDocumentId))
        {
            error = "This saved terrain binding has no document identity. Choose the terrain again from the menu.";
            return null;
        }
        RhinoDoc[] matches = RhinoDoc.OpenDocuments().Where(doc =>
            string.Equals(bridge.GetDocumentIdentity(doc), _boundDocumentId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1 && _boundDocumentSerial != 0)
        {
            RhinoDoc? sameSession = matches.FirstOrDefault(doc => doc.RuntimeSerialNumber == _boundDocumentSerial);
            if (sameSession != null)
                return sameSession;
        }
        error = matches.Length == 0
            ? "The bound MoleHill Rhino document is not open. Open the source document to resume this reference."
            : "Several open Rhino documents share this MoleHill document identity. Close the copies or choose the intended terrain again.";
        return null;
    }

    private static ITerrainSnapshotBridge? FindBridge()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(BridgeTypeName, throwOnError: false);
            if (type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                is ITerrainSnapshotBridge bridge)
                return bridge;
        }

        return null;
    }

    private static MoleHillTerrainRegion CreateTerrainRegion(TerrainInteropRegion region)
    {
        return new MoleHillTerrainRegion(region.Name, region.Key, region.Boundaries)
        {
            StackIndex = region.StackIndex,
            IsEnabled = region.IsEnabled,
            UseInputElevationForPriority = region.UseInputElevationForPriority,
            ColorArgb = region.ColorArgb,
            UseColorOverride = region.UseColorOverride,
            LayerName = region.LayerName,
            MaterialName = region.MaterialName,
            SplitToSeparateMesh = region.SplitToSeparateMesh
        };
    }

    private static int ToGrasshopperInteger(long value)
    {
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }

    private static void EmitTerrain(IGH_DataAccess DA, MoleHillTerrainData terrain)
    {
        DA.SetData(0, new MoleHillTerrainGoo(terrain.Duplicate()));
        DA.SetData(1, terrain.Name);
        DA.SetData(2, ToGrasshopperInteger(terrain.Revision));
        DA.SetDataList(3, terrain.Diagnostics);
        DA.SetData(4, terrain.Key);
        DA.SetData(5, terrain.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        DA.SetData(6, terrain.UnitSystem);
        DA.SetData(7, terrain.MetersPerModelUnit);
        DA.SetData(8, terrain.LocalToWorld);
        DA.SetData(9, terrain.HasProjectBaseTransform);
        DA.SetData(11, terrain.Fingerprint);
    }
}

