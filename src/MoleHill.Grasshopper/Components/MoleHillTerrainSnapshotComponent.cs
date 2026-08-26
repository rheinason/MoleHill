// Reads final Rhino-panel terrain snapshots through the optional host reflection bridge.
using System.Collections;
using System.Reflection;
using Grasshopper.Kernel;
using MoleHill.Grasshopper.Types;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

public sealed class MoleHillTerrainSnapshotComponent : GH_Component
{
    private const string BridgeTypeName = "MoleHill.Rhino.Services.TerrainGrasshopperBridge";
    private EventInfo? _snapshotChangedEvent;
    private EventHandler? _snapshotChangedHandler;

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
    }

    public override void AddedToDocument(GH_Document document)
    {
        base.AddedToDocument(document);
        TrySubscribeToSnapshotChanges();
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        UnsubscribeFromSnapshotChanges();
        base.RemovedFromDocument(document);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        TrySubscribeToSnapshotChanges();
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No active Rhino document.");
            return;
        }

        string terrainKey = string.Empty;
        DA.GetData(0, ref terrainKey);
        Type? bridgeType = FindBridgeType();
        if (bridgeType == null)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "The MoleHill Rhino plugin is not loaded. Install and load the combined MoleHill package before using Terrain Snapshot.");
            return;
        }

        MethodInfo? method = bridgeType.GetMethod(
            "GetSnapshot",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(RhinoDoc), typeof(string), typeof(string).MakeByRefType() },
            modifiers: null);
        if (method == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The loaded MoleHill Rhino plugin does not expose terrain snapshots.");
            return;
        }

        var arguments = new object?[] { doc, terrainKey, null };
        object? snapshot;
        try
        {
            snapshot = method.Invoke(null, arguments);
        }
        catch (Exception exception) when (exception is TargetInvocationException or
                                           ArgumentException or
                                           MethodAccessException or
                                           TargetParameterCountException)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                exception is TargetInvocationException invocation
                    ? invocation.InnerException?.Message ?? invocation.Message
                    : exception.Message);
            return;
        }

        if (snapshot == null)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Warning,
                arguments[2] as string ?? "MoleHill did not return a terrain snapshot.");
            return;
        }

        try
        {
            Type snapshotType = snapshot.GetType();
            if (snapshotType.GetProperty("Mesh")?.GetValue(snapshot) is not Mesh mesh)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "MoleHill snapshot did not contain a terrain mesh.");
                return;
            }

            string name = snapshotType.GetProperty("Name")?.GetValue(snapshot) as string ?? "Terrain";
            string key = snapshotType.GetProperty("Key")?.GetValue(snapshot) as string ?? string.Empty;
            long revision = Convert.ToInt64(snapshotType.GetProperty("Revision")?.GetValue(snapshot) ?? 0L);
            IReadOnlyList<Curve> breaklines = ReadCurves(snapshotType.GetProperty("Breaklines")?.GetValue(snapshot));
            IReadOnlyList<MoleHillTerrainRegion> regions = ReadRegions(snapshotType.GetProperty("Regions")?.GetValue(snapshot));
            IReadOnlyList<string> diagnostics = ReadStrings(snapshotType.GetProperty("Diagnostics")?.GetValue(snapshot));
            string unitSystem = snapshotType.GetProperty("UnitSystem")?.GetValue(snapshot) as string ?? "Unspecified";
            double metersPerModelUnit = Convert.ToDouble(snapshotType.GetProperty("MetersPerModelUnit")?.GetValue(snapshot) ?? 1.0);
            Transform localToWorld = snapshotType.GetProperty("LocalToWorld")?.GetValue(snapshot) is Transform transform
                ? transform
                : Transform.Identity;
            bool hasProjectBaseTransform = Convert.ToBoolean(
                snapshotType.GetProperty("HasProjectBaseTransform")?.GetValue(snapshot) ?? false);

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
                hasProjectBaseTransform);
            DA.SetData(0, new MoleHillTerrainGoo(terrain));
            DA.SetData(1, name);
            DA.SetData(2, ToGrasshopperInteger(revision));
            DA.SetDataList(3, diagnostics);
            DA.SetData(4, key);
            DA.SetData(5, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DA.SetData(6, unitSystem);
            DA.SetData(7, metersPerModelUnit);
            DA.SetData(8, localToWorld);
            DA.SetData(9, hasProjectBaseTransform);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or TargetException)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not read the MoleHill terrain snapshot: {exception.Message}");
        }
        finally
        {
            if (snapshot is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private void TrySubscribeToSnapshotChanges()
    {
        if (_snapshotChangedEvent != null)
            return;

        Type? bridgeType = FindBridgeType();
        EventInfo? eventInfo = bridgeType?.GetEvent("SnapshotChanged", BindingFlags.Public | BindingFlags.Static);
        if (eventInfo?.EventHandlerType != typeof(EventHandler))
            return;

        _snapshotChangedHandler = OnSnapshotChanged;
        eventInfo.AddEventHandler(null, _snapshotChangedHandler);
        _snapshotChangedEvent = eventInfo;
    }

    private void UnsubscribeFromSnapshotChanges()
    {
        if (_snapshotChangedEvent != null && _snapshotChangedHandler != null)
            _snapshotChangedEvent.RemoveEventHandler(null, _snapshotChangedHandler);
        _snapshotChangedEvent = null;
        _snapshotChangedHandler = null;
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        GH_Document? document = OnPingDocument();
        document?.ScheduleSolution(5, _ => ExpireSolution(false));
    }

    private static Type? FindBridgeType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(BridgeTypeName, throwOnError: false);
            if (type != null)
                return type;
        }

        return null;
    }

    private static IReadOnlyList<Curve> ReadCurves(object? source)
    {
        if (source is not IEnumerable enumerable)
            return Array.Empty<Curve>();

        return enumerable.Cast<object>()
            .OfType<Curve>()
            .ToArray();
    }

    private static IReadOnlyList<MoleHillTerrainRegion> ReadRegions(object? source)
    {
        if (source is not IEnumerable enumerable)
            return Array.Empty<MoleHillTerrainRegion>();

        var result = new List<MoleHillTerrainRegion>();
        foreach (object region in enumerable)
        {
            Type type = region.GetType();
            string name = type.GetProperty("Name")?.GetValue(region) as string ?? "Zone";
            string key = type.GetProperty("Key")?.GetValue(region) as string ?? string.Empty;
            IReadOnlyList<Curve> boundaries = ReadCurves(type.GetProperty("Boundaries")?.GetValue(region));
            if (boundaries.Count > 0)
                result.Add(new MoleHillTerrainRegion(name, key, boundaries));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStrings(object? source)
    {
        if (source is not IEnumerable enumerable)
            return Array.Empty<string>();

        return enumerable.Cast<object>()
            .OfType<string>()
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
    }

    private static int ToGrasshopperInteger(long value)
    {
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }
}
