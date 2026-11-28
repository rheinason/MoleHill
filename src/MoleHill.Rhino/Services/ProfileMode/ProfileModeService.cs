// Profile mode (mhEditProfile): draw a curve's long section as a real curve, read it back on Accept.
using MoleHill.Core.Analysis;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>The open section of one plan curve: its link and every curve that currently is the section.</summary>
internal sealed record ProfileSession(ProfileSection Link, IReadOnlyList<RhinoObject> Pieces);

/// <summary>What Accept would write: the plan curve re-elevated from the section.</summary>
internal sealed record ProfileResult(Curve Curve, SectionEdit Edit, double Deviation, bool KeptSmooth);

/// <summary>
/// Profile mode. MoleHill draws the section and its grid; the user edits the section with any Rhino tool
/// (grips, gumball, Trim, Split, Join, Rebuild, MoleHill's slope commands) and accepts from Inspect Curve.
/// Nothing here edits geometry interactively: the mode only draws, reads and writes back, so every edit is an
/// ordinary Rhino edit with Rhino's own undo, and Accept is one more.
/// </summary>
internal static class ProfileModeService
{
    private const string ExaggerationKey = "MoleHill.ProfileMode.Exaggeration";
    private const string SectionName = "Long section";

    private static readonly Dictionary<uint, List<Guid>> Registry = new();
    private static readonly Dictionary<uint, PendingReplacement> Pending = new();
    private static readonly Dictionary<string, ProfileGroundLine> GroundCache = new();
    private static bool _initialized;

    /// <summary>Raised (on the UI thread) when a document's sections may have changed.</summary>
    public static event Action<RhinoDoc>? SectionsChanged;

    // ── Lifetime ───────────────────────────────────────────────────────────

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        RhinoDoc.AddRhinoObject += OnAdd;
        RhinoDoc.DeleteRhinoObject += OnDelete;
        RhinoDoc.UndeleteRhinoObject += OnAdd;
        RhinoDoc.ReplaceRhinoObject += OnReplace;
        RhinoDoc.ModifyObjectAttributes += OnModifyAttributes;
        RhinoDoc.EndOpenDocument += OnOpen;
        RhinoDoc.CloseDocument += OnClose;
        Command.EndCommand += OnEndCommand;
        ProfileModeConduit.Instance.Enabled = true;
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;
        _initialized = false;
        RhinoDoc.AddRhinoObject -= OnAdd;
        RhinoDoc.DeleteRhinoObject -= OnDelete;
        RhinoDoc.UndeleteRhinoObject -= OnAdd;
        RhinoDoc.ReplaceRhinoObject -= OnReplace;
        RhinoDoc.ModifyObjectAttributes -= OnModifyAttributes;
        RhinoDoc.EndOpenDocument -= OnOpen;
        RhinoDoc.CloseDocument -= OnClose;
        Command.EndCommand -= OnEndCommand;
        ProfileModeConduit.Instance.Enabled = false;
    }

    // ── Reading the document ───────────────────────────────────────────────

    /// <summary>Every curve in the document that is a section, hidden and locked ones included.</summary>
    public static IReadOnlyList<RhinoObject> SectionObjects(RhinoDoc doc)
    {
        if (!Registry.TryGetValue(doc.RuntimeSerialNumber, out List<Guid>? ids))
        {
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                HiddenObjects = true,
                LockedObjects = true,
                ObjectTypeFilter = ObjectType.Curve,
                IncludeLights = false,
                IncludeGrips = false,
            };
            ids = doc.Objects.GetObjectList(settings).Where(o => ProfileSection.IsSection(o.Attributes)).Select(o => o.Id).ToList();
            Registry[doc.RuntimeSerialNumber] = ids;
        }

        return ids.Select(id => doc.Objects.FindId(id)).Where(o => o != null && !o.IsDeleted).Cast<RhinoObject>().ToList();
    }

    /// <summary>The document's open section (the first plan curve's, should there ever be two).</summary>
    public static ProfileSession? Session(RhinoDoc doc)
    {
        IReadOnlyList<RhinoObject> objects = SectionObjects(doc);
        foreach (RhinoObject o in objects)
        {
            if (ProfileSection.Read(o.Attributes) is not { } link)
                continue;
            var pieces = objects.Where(p => ProfileSection.Read(p.Attributes)?.PlanId == link.PlanId).ToList();
            return new ProfileSession(link, pieces);
        }

        return null;
    }

    public static bool IsSectionCurve(RhinoDoc doc, Guid id) =>
        doc.Objects.FindId(id) is { } o && ProfileSection.IsSection(o.Attributes);

    /// <summary>
    /// The plan curve as Accept would write it, or null with the reason it cannot be written yet (the
    /// section doubles back, the plan curve has been edited since the section was drawn, ...).
    /// </summary>
    public static ProfileResult? BuildResult(RhinoDoc doc, ProfileSession session, bool keepSmooth, out string? error)
    {
        error = null;
        double tolerance = doc.ModelAbsoluteTolerance;
        if (doc.Objects.FindId(session.Link.PlanId)?.Geometry is not Curve plan)
        {
            error = "The plan curve this section belongs to is gone. Cancel the section.";
            return null;
        }

        if (!TryGetPath(plan, tolerance, out double[] path))
        {
            error = "The plan curve could not be read as a path.";
            return null;
        }

        if (ProfileSection.Hash(path) != session.Link.PlanHash)
        {
            error = "The plan curve changed after its section was drawn. Cancel, then run mhEditProfile again.";
            return null;
        }

        double[] stations = LongSectionProfile.PlanStations(path);
        double[] heights = Enumerable.Range(0, path.Length / 3).Select(i => path[i * 3 + 2]).ToArray();
        var pieces = new List<double[]>();
        foreach (RhinoObject piece in session.Pieces)
        {
            if (piece.Geometry is Curve curve && session.Link.ToStations(curve, tolerance) is { } flat)
                pieces.Add(flat);
        }

        SectionEdit? edit = SectionEdit.Create(stations, heights, pieces, tolerance, out error);
        if (edit == null)
            return null;
        if (!edit.HasChanges)
            return new ProfileResult(plan.DuplicateCurve(), edit, 0.0, false);

        bool smooth = !plan.TryGetPolyline(out _);
        if (smooth && keepSmooth &&
            TerrainInputCommandAlgorithms.ApplyProfileToEditPoints(
                plan, station => edit.TakesProfile(station) ? edit.ElevationAt(station) : null, out double deviation) is { } fitted)
            return new ProfileResult(fitted, edit, deviation, true);

        double[] applied = edit.ApplyToPath(path);
        var points = new Point3d[applied.Length / 3];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Point3d(applied[i * 3], applied[i * 3 + 1], applied[i * 3 + 2]);
        return new ProfileResult(new PolylineCurve(points), edit, 0.0, false);
    }

    /// <summary>The ground under the plan curve, as the section's ground line (cached per curve and terrain).</summary>
    public static ProfileGroundLine Ground(RhinoDoc doc, ProfileSection link)
    {
        if (doc.Objects.FindId(link.PlanId)?.Geometry is not Curve plan || !TryGetPath(plan, doc.ModelAbsoluteTolerance, out double[] path))
            return ProfileGroundLine.Empty;
        (Mesh? mesh, bool owned, _) = GroundMesh(doc, link.PlanId);
        string key = $"{doc.RuntimeSerialNumber}|{link.PlanHash}|{(mesh == null ? 0 : mesh.GetHashCode())}";
        if (GroundCache.TryGetValue(key, out ProfileGroundLine? cached))
        {
            if (owned)
                mesh?.Dispose();
            return cached;
        }

        ProfileGroundLine ground = mesh == null ? ProfileGroundLine.Empty : SampleGround(mesh, path, doc.ModelAbsoluteTolerance);
        if (owned)
            mesh?.Dispose();
        if (GroundCache.Count > 32)
            GroundCache.Clear();
        GroundCache[key] = ground;
        return ground;
    }

    // ── The command ────────────────────────────────────────────────────────

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext units))
            return Result.Failure;

        if (Session(doc) is { } open)
            return Resume(doc, open);

        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select a curve to edit its profile");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.SetCustomGeometryFilter((rhinoObject, _, _) => rhinoObject == null || !ProfileSection.IsSection(rhinoObject.Attributes));
        getCurve.EnablePreSelect(true, true);
        if (getCurve.Get() != GetResult.Object)
            return getCurve.CommandResult();

        RhinoObject? planObject = getCurve.Object(0).Object();
        if (planObject?.Geometry is not Curve plan)
            return Result.Failure;
        doc.Objects.UnselectAll();

        double tolerance = doc.ModelAbsoluteTolerance;
        if (!TryGetPath(plan, tolerance, out double[] path))
        {
            RhinoApp.WriteLine("The curve could not be read as a path.");
            return Result.Failure;
        }

        double[] stations = LongSectionProfile.PlanStations(path);
        if (!(stations[^1] > tolerance))
        {
            RhinoApp.WriteLine("The curve has no length in plan; a profile needs one.");
            return Result.Failure;
        }

        double[] heights = Enumerable.Range(0, path.Length / 3).Select(i => path[i * 3 + 2]).ToArray();
        double low = heights.Min(), high = heights.Max();
        if (Ground(doc, new ProfileSection(planObject.Id, default, 0.0, ProfileSection.Hash(path))).Extent() is { } ground)
        {
            low = Math.Min(low, ground.Low);
            high = Math.Max(high, ground.High);
        }

        double step = AnalysisRange.NiceStep(Math.Max(high - low, units.FromMeters(0.1)) / 6.0);
        double datum = Math.Floor(low / step) * step;

        // Where to draw it: a picked point is station 0 at the datum; Enter puts it below the curve.
        double exaggeration = Math.Clamp(CommandOptionCache.GetValue(ExaggerationKey, 5.0), 0.1, 1000.0);
        BoundingBox box = plan.GetBoundingBox(true);
        Point3d? placed = null;
        while (true)
        {
            var getPlace = new GetPoint();
            getPlace.SetCommandPrompt("Where to draw the section (station 0 at its lowest grid line). Press Enter to draw it below the curve");
            getPlace.AcceptNothing(true);
            var option = new OptionDouble(exaggeration, 0.1, 1000.0);
            getPlace.AddOptionDouble("Exaggeration", ref option, "Vertical exaggeration of the section");
            GetResult result = getPlace.Get();
            if (result == GetResult.Option)
            {
                exaggeration = option.CurrentValue;
                CommandOptionCache.SetValue(ExaggerationKey, exaggeration);
                continue;
            }

            if (result == GetResult.Point)
                placed = getPlace.Point();
            else if (result != GetResult.Nothing)
                return getPlace.CommandResult();
            break;
        }

        double height = (Math.Ceiling(high / step) * step - datum) * exaggeration;
        double gap = Math.Max(Math.Max(stations[^1] * 0.05, height * 0.25), box.Diagonal.Length * 0.05);
        var frame = placed is { } p
            ? new LongSectionFrame(p.X, p.Y, datum, exaggeration)
            : new LongSectionFrame(box.Min.X, box.Min.Y - gap - height, datum, exaggeration);
        // Drawn above the curve and the terrain, so neither hides it in a plan view (found live: at the
        // curve's lowest height the terrain covered half the section).
        double planeZ = box.Max.Z;
        (Mesh? groundMesh, bool ownedMesh, _) = GroundMesh(doc, planObject.Id);
        if (groundMesh != null)
        {
            planeZ = Math.Max(planeZ, groundMesh.GetBoundingBox(false).Max.Z);
            if (ownedMesh)
                groundMesh.Dispose();
        }

        var link = new ProfileSection(planObject.Id, frame, placed?.Z ?? planeZ + tolerance * 10.0, ProfileSection.Hash(path));

        var points = new Point3d[stations.Length];
        for (int i = 0; i < stations.Length; i++)
            points[i] = link.ToWorld(stations[i], heights[i]);
        ObjectAttributes attributes = doc.CreateDefaultAttributes();
        attributes.Name = SectionName;
        link.Write(attributes);

        uint undo = doc.BeginUndoRecord("Edit Profile");
        Guid sectionId;
        try
        {
            sectionId = doc.Objects.AddPolyline(points, attributes);
        }
        finally
        {
            doc.EndUndoRecord(undo);
        }

        if (sectionId == Guid.Empty)
            return Result.Failure;

        string? layerPath = doc.Layers.FindIndex(planObject.Attributes.LayerIndex)?.FullPath;
        if (ProfileCurveUsers.Describe(ProfileCurveUsers.Find(TerrainController.Instance.GetTerrains(doc), planObject.Id, layerPath)) is { } usedBy)
            RhinoApp.WriteLine(usedBy);
        RhinoApp.WriteLine(
            "Long section drawn. Edit it with any Rhino tool (grips, Trim, Join, mhSlopeCurve ...), then Accept changes in Inspect Curve, or run mhEditProfile again.");
        ZoomToInclude(doc, sectionId, planObject.Id);
        CurveReviewService.StartProfileMode(doc);
        return Result.Success;
    }

    private static Result Resume(RhinoDoc doc, ProfileSession open)
    {
        var get = new GetOption();
        get.SetCommandPrompt("A long section is open. Inspect it, accept it, or cancel it");
        get.AcceptNothing(true);
        int inspect = get.AddOption("Inspect");
        int accept = get.AddOption("Accept");
        int cancel = get.AddOption("Cancel");
        GetResult result = get.Get();
        if (result == GetResult.Nothing || (result == GetResult.Option && get.OptionIndex() == inspect))
        {
            CurveReviewService.StartProfileMode(doc);
            return Result.Success;
        }

        if (result != GetResult.Option)
            return get.CommandResult();
        if (get.OptionIndex() == accept)
            return Accept(doc, keepSmooth: true, out _) ? Result.Success : Result.Failure;
        if (get.OptionIndex() == cancel)
            return Cancel(doc) ? Result.Success : Result.Failure;
        return Result.Cancel;
    }

    // ── Accept and Cancel ──────────────────────────────────────────────────

    /// <summary>Writes the section to its plan curve and removes the section: one undo step.</summary>
    public static bool Accept(RhinoDoc doc, bool keepSmooth, out Guid planId)
    {
        planId = Guid.Empty;
        if (Session(doc) is not { } session)
        {
            RhinoApp.WriteLine("No long section is open.");
            return false;
        }

        planId = session.Link.PlanId;
        ProfileResult? result = BuildResult(doc, session, keepSmooth, out string? error);
        if (result == null)
        {
            RhinoApp.WriteLine(error ?? "The section could not be read.");
            return false;
        }

        using (result.Curve)
        {
            uint undo = doc.BeginUndoRecord("Accept Profile");
            try
            {
                if (result.Edit.HasChanges && !doc.Objects.Replace(session.Link.PlanId, result.Curve))
                {
                    RhinoApp.WriteLine("Could not update the plan curve (is it locked?).");
                    return false;
                }

                foreach (RhinoObject piece in session.Pieces)
                    doc.Objects.Delete(piece, quiet: true, ignoreModes: true);
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
        }

        if (!result.Edit.HasChanges)
            RhinoApp.WriteLine("The section was not changed; the curve is as it was.");
        else
        {
            int stretches = result.Edit.ChangedRanges.Count;
            RhinoApp.WriteLine($"Profile accepted: {stretches} stretch{(stretches == 1 ? string.Empty : "es")} of the curve re-elevated; the rest keeps its own points.");
            if (result.KeptSmooth && result.Deviation > doc.ModelAbsoluteTolerance)
                RhinoApp.WriteLine(FormattableString.Invariant(
                    $"The curve stayed smooth, so between its edit points it strays up to {result.Deviation:0.###} from the section. Untick Keep smooth for an exact polyline."));
        }

        doc.Views.Redraw();
        return true;
    }

    /// <summary>Removes the section and leaves the plan curve as it is: one undo step.</summary>
    public static bool Cancel(RhinoDoc doc)
    {
        if (Session(doc) is not { } session)
            return false;
        uint undo = doc.BeginUndoRecord("Cancel Profile");
        try
        {
            foreach (RhinoObject piece in session.Pieces)
                doc.Objects.Delete(piece, quiet: true, ignoreModes: true);
        }
        finally
        {
            doc.EndUndoRecord(undo);
        }

        RhinoApp.WriteLine("Long section removed; the curve is as it was.");
        doc.Views.Redraw();
        return true;
    }

    // ── Keeping track of the section through edits ─────────────────────────

    /// <summary>
    /// A command that deleted a section piece and added curves (a Join that kept the new piece's attributes,
    /// say): the curves it added near the deleted piece are the section now, and get its link.
    /// </summary>
    private sealed class PendingReplacement
    {
        public ProfileSection? Link;
        public BoundingBox Area = BoundingBox.Empty;
        public readonly List<Guid> Added = new();
    }

    private static void Invalidate(RhinoDoc? doc)
    {
        if (doc == null)
            return;
        Registry.Remove(doc.RuntimeSerialNumber);
        SectionsChanged?.Invoke(doc);
    }

    private static bool Replaying(RhinoDoc doc) => doc.UndoActive || doc.RedoActive;

    private static void OnAdd(object? sender, RhinoObjectEventArgs e)
    {
        RhinoDoc? doc = e.TheObject.Document;
        if (doc == null || e.TheObject.ObjectType != ObjectType.Curve)
            return;
        if (ProfileSection.IsSection(e.TheObject.Attributes))
            Invalidate(doc);
        else if (!Replaying(doc) && Pending.TryGetValue(doc.RuntimeSerialNumber, out PendingReplacement? pending))
            pending.Added.Add(e.TheObject.Id);
    }

    private static void OnDelete(object? sender, RhinoObjectEventArgs e)
    {
        RhinoDoc? doc = e.TheObject.Document;
        if (doc == null || !ProfileSection.IsSection(e.TheObject.Attributes))
            return;
        Invalidate(doc);
        if (Replaying(doc) || ProfileSection.Read(e.TheObject.Attributes) is not { } link)
            return;
        if (!Pending.TryGetValue(doc.RuntimeSerialNumber, out PendingReplacement? pending))
            Pending[doc.RuntimeSerialNumber] = pending = new PendingReplacement();
        pending.Link ??= link;
        BoundingBox box = e.TheObject.Geometry.GetBoundingBox(true);
        box.Inflate(Math.Max(box.Diagonal.Length * 0.1, doc.ModelAbsoluteTolerance * 10.0));
        pending.Area.Union(box);
    }

    private static void OnReplace(object? sender, RhinoReplaceObjectEventArgs e)
    {
        if (ProfileSection.IsSection(e.OldRhinoObject.Attributes) || ProfileSection.IsSection(e.NewRhinoObject.Attributes))
            Invalidate(e.Document);
    }

    private static void OnModifyAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        if (ProfileSection.IsSection(e.OldAttributes) || ProfileSection.IsSection(e.NewAttributes))
            Invalidate(e.Document);
    }

    private static void OnEndCommand(object? sender, CommandEventArgs e)
    {
        RhinoDoc? doc = e.Document;
        if (doc == null || !Pending.Remove(doc.RuntimeSerialNumber, out PendingReplacement? pending))
            return;
        if (pending.Link == null || pending.Added.Count == 0 || e.CommandResult != Result.Success)
            return;

        // Re-tag after the command has finished with the document, never from inside its events.
        RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            foreach (Guid id in pending.Added)
            {
                if (doc.Objects.FindId(id) is not { IsDeleted: false } added || added.Geometry is not Curve curve ||
                    ProfileSection.IsSection(added.Attributes))
                    continue;
                BoundingBox box = curve.GetBoundingBox(true);
                if (!Overlaps(pending.Area, box))
                    continue;
                ObjectAttributes attributes = added.Attributes.Duplicate();
                pending.Link.Write(attributes);
                if (string.IsNullOrEmpty(attributes.Name))
                    attributes.Name = SectionName;
                doc.Objects.ModifyAttributes(added, attributes, quiet: true);
            }

            Invalidate(doc);
        }));
    }

    private static bool Overlaps(BoundingBox a, BoundingBox b) =>
        a.IsValid && b.IsValid && a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    private static void OnOpen(object? sender, DocumentOpenEventArgs e) => Invalidate(e.Document);

    private static void OnClose(object? sender, DocumentEventArgs e)
    {
        if (e.Document == null)
            return;
        Registry.Remove(e.Document.RuntimeSerialNumber);
        Pending.Remove(e.Document.RuntimeSerialNumber);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The curve as a flat XYZ path: a polyline's own points, otherwise a tolerance-fine tessellation.</summary>
    internal static bool TryGetPath(Curve curve, double tolerance, out double[] path)
    {
        path = Array.Empty<double>();
        if (!curve.TryGetPolyline(out Polyline polyline))
        {
            using PolylineCurve? tessellated = curve.ToPolyline(tolerance, RhinoMath.ToRadians(1.0), 0.0, 0.0);
            if (tessellated == null || !tessellated.TryGetPolyline(out polyline))
                return false;
        }

        if (polyline.Count < 2)
            return false;
        path = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            path[i * 3] = polyline[i].X;
            path[i * 3 + 1] = polyline[i].Y;
            path[i * 3 + 2] = polyline[i].Z;
        }

        return true;
    }

    /// <summary>
    /// The ground under the plan curve: the mesh the curve's own card grades when a card on a terrain reads it
    /// (the terrain before the road), else the active terrain as built.
    /// </summary>
    private static (Mesh? Mesh, bool Owned, string? From) GroundMesh(RhinoDoc doc, Guid planId)
    {
        string? layerPath = doc.Objects.FindId(planId) is { } plan ? doc.Layers.FindIndex(plan.Attributes.LayerIndex)?.FullPath : null;
        List<ProfileCurveUser> users = ProfileCurveUsers.Find(TerrainController.Instance.GetTerrains(doc), planId, layerPath);
        if (users.FirstOrDefault(user => user.Modifier != null) is { Modifier: { } card } user &&
            TerrainController.Instance.DuplicateIncomingModifierMesh(doc, user.Terrain, card.Id) is { } incoming)
            return (incoming, true, $"the ground '{card.Label}' grades");
        if (TerrainController.Instance.GetSelectedTerrain(doc) is { } terrain &&
            TerrainController.Instance.PeekFinalTerrainMesh(doc, terrain.TerrainId) is { } final)
            return (final, false, $"terrain '{terrain.Name}'");
        return (null, false, null);
    }

    private static ProfileGroundLine SampleGround(Mesh mesh, double[] path, double tolerance)
    {
        DrapeTargets targets = DrapeTargets.Create(new[] { mesh }, tolerance, out _);
        double[] stations = LongSectionProfile.PlanStations(path);
        double spacing = Math.Max(tolerance * 10.0, stations[^1] / 500.0);
        var samples = new SortedSet<double>(stations);
        for (double s = 0.0; s < stations[^1]; s += spacing)
            samples.Add(s);

        var runs = new List<double[]>();
        var current = new List<double>();
        foreach (double station in samples)
        {
            (double x, double y) = PlanPointAt(path, stations, station);
            if (targets.HeightAt(x, y, lowest: false) is { } z)
            {
                current.Add(station);
                current.Add(z);
                continue;
            }

            if (current.Count >= 4)
                runs.Add(current.ToArray());
            current.Clear();
        }

        if (current.Count >= 4)
            runs.Add(current.ToArray());
        return new ProfileGroundLine(runs);
    }

    private static (double X, double Y) PlanPointAt(double[] path, double[] stations, double station)
    {
        int index = Array.BinarySearch(stations, station);
        if (index >= 0)
            return (path[index * 3], path[index * 3 + 1]);
        int upper = Math.Min(~index, stations.Length - 1);
        int lower = Math.Max(upper - 1, 0);
        double span = stations[upper] - stations[lower];
        double t = span > 0.0 ? (station - stations[lower]) / span : 0.0;
        return (path[lower * 3] + (path[upper * 3] - path[lower * 3]) * t,
            path[lower * 3 + 1] + (path[upper * 3 + 1] - path[lower * 3 + 1]) * t);
    }

    private static void ZoomToInclude(RhinoDoc doc, Guid sectionId, Guid planId)
    {
        if (doc.Views.ActiveView is not { } view)
            return;
        BoundingBox box = BoundingBox.Empty;
        foreach (Guid id in new[] { sectionId, planId })
        {
            if (doc.Objects.FindId(id) is { } o)
                box.Union(o.Geometry.GetBoundingBox(true));
        }

        if (!box.IsValid || view.ActiveViewport.IsVisible(box))
            return;
        box.Inflate(box.Diagonal.Length * 0.08);
        view.ActiveViewport.ZoomBoundingBox(box);
        view.Redraw();
    }
}
