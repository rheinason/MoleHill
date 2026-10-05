// Creates or updates Revit Toposolids (and their subdivisions) from planned MoleHill preparations, in one undo step.
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MoleHill.Revit.Planning;
using RhinoPoint = Rhino.Geometry.Point3d;

namespace MoleHill.Revit.RevitHost;

internal sealed class ToposolidWriteRequest
{
    public required Document Document { get; init; }
    public required IReadOnlyList<PlannedToposolid> Plans { get; init; }
    public ElementId ToposolidTypeId { get; init; } = ElementId.InvalidElementId;
    public ElementId LevelId { get; init; } = ElementId.InvalidElementId;
    public bool WriteSubdivisions { get; init; } = true;
    public ElementId SubdivisionTypeId { get; init; } = ElementId.InvalidElementId;
    public IReadOnlyList<string> PreserveParameters { get; init; } = Array.Empty<string>();
}

internal sealed class ToposolidWriteResult
{
    public List<Toposolid> Toposolids { get; } = new();
    public List<List<Toposolid>> Subdivisions { get; } = new();
    public List<string> Report { get; } = new();
}

internal static class ToposolidWriter
{
    /// <summary>
    /// Writes every plan in one transaction group, assimilated into a single undo step. An unchanged
    /// fingerprint keeps the element; a changed one creates the replacement first and only then deletes
    /// the old element, so a failure part-way leaves the document as it was. Elements that carry a
    /// MoleHill key absent from this run are reported, never deleted: absence is not an instruction.
    /// </summary>
    public static ToposolidWriteResult Write(ToposolidWriteRequest request)
    {
        Document document = request.Document;
        var result = new ToposolidWriteResult();
        Schema schema = ToposolidIdentity.GetOrCreateSchema();

        var hosts = new Dictionary<string, (Toposolid Element, string? Fingerprint)>(StringComparer.Ordinal);
        var subdivisions = new Dictionary<string, (Toposolid Element, string? Fingerprint)>(StringComparer.Ordinal);
        using (var collector = new FilteredElementCollector(document).OfClass(typeof(Toposolid)))
        {
            foreach (Toposolid candidate in collector.Cast<Toposolid>())
            {
                (string? key, string? fingerprint) = ToposolidIdentity.Read(candidate);
                if (string.IsNullOrEmpty(key))
                    continue;
                var index = ToposolidIdentity.IsSubdivision(candidate) ? subdivisions : hosts;
                if (!index.TryAdd(key, (candidate, fingerprint)))
                    throw new InvalidOperationException($"Revit holds more than one Toposolid with MoleHill key '{key}'. Delete the extra one, then run again.");
            }
        }

        using var group = new TransactionGroup(document, "Write MoleHill Toposolids");
        using var transaction = new Transaction(document, "Write MoleHill Toposolids");
        group.Start();
        try
        {
            transaction.Start();
            foreach (PlannedToposolid plan in request.Plans)
            {
                Toposolid host = WriteHost(request, plan, hosts, schema, out Toposolid? replaced, result.Report);
                result.Toposolids.Add(host);

                var written = new List<Toposolid>();
                if (request.WriteSubdivisions)
                {
                    foreach (PlannedSubdivision subdivision in plan.Subdivisions)
                        written.Add(WriteSubdivision(request, subdivision, host, subdivisions, schema, result.Report));
                    ReportOrphanedSubdivisions(plan, host, subdivisions, result.Report);
                }

                result.Subdivisions.Add(written);

                // Deleting the old host takes its subdivisions with it, so it goes last.
                if (replaced != null && replaced.IsValidObject)
                    document.Delete(replaced.Id);
            }

            transaction.Commit();
            group.Assimilate();
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
                transaction.RollBack();
            if (group.HasStarted())
                group.RollBack();
            throw;
        }

        return result;
    }

    private static Toposolid WriteHost(
        ToposolidWriteRequest request,
        PlannedToposolid plan,
        Dictionary<string, (Toposolid Element, string? Fingerprint)> hosts,
        Schema schema,
        out Toposolid? replaced,
        List<string> report)
    {
        replaced = null;
        bool exists = hosts.TryGetValue(plan.Key, out var existing);
        switch (ToposolidWritePlanner.Decide(exists, existing.Fingerprint, plan.Fingerprint))
        {
            case ToposolidWriteAction.Keep:
                report.Add($"{plan.Key}: unchanged; kept Toposolid {existing.Element.Id}.");
                return existing.Element;

            case ToposolidWriteAction.Replace:
            {
                Document document = request.Document;
                Toposolid created = Toposolid.Create(
                    document, ToCurveLoops(plan.Loops), ToXyz(plan.Points), existing.Element.GetTypeId(), existing.Element.LevelId);
                ToposolidIdentity.Write(created, schema, plan.Key, plan.Fingerprint);
                CopyParameters(existing.Element, created, request.PreserveParameters, plan.Key, report);
                report.Add($"{plan.Key}: geometry changed; replaced Toposolid {existing.Element.Id} with {created.Id}.");
                replaced = existing.Element;
                return created;
            }

            default:
            {
                if (request.ToposolidTypeId == ElementId.InvalidElementId || request.LevelId == ElementId.InvalidElementId)
                    throw new InvalidOperationException($"{plan.Key}: Toposolid Type and Level are required to create a new Toposolid.");
                Toposolid created = Toposolid.Create(
                    request.Document, ToCurveLoops(plan.Loops), ToXyz(plan.Points), request.ToposolidTypeId, request.LevelId);
                ToposolidIdentity.Write(created, schema, plan.Key, plan.Fingerprint);
                report.Add($"{plan.Key}: created Toposolid {created.Id}.");
                return created;
            }
        }
    }

    private static Toposolid WriteSubdivision(
        ToposolidWriteRequest request,
        PlannedSubdivision plan,
        Toposolid host,
        Dictionary<string, (Toposolid Element, string? Fingerprint)> subdivisions,
        Schema schema,
        List<string> report)
    {
        bool exists = subdivisions.TryGetValue(plan.IdentityKey, out var existing);
        bool onCurrentHost = exists && existing.Element.HostTopoId == host.Id;
        ToposolidWriteAction action = ToposolidWritePlanner.DecideSubdivision(exists, existing.Fingerprint, onCurrentHost, plan.Fingerprint);
        if (action == ToposolidWriteAction.Keep)
        {
            report.Add($"{plan.IdentityKey}: unchanged; kept subdivision {existing.Element.Id}.");
            return existing.Element;
        }

        // Revit 2025 has no typed overload; set the type afterwards, which every version supports.
        Toposolid created = host.CreateSubDivision(request.Document, ToCurveLoops(plan.Loops));
        if (request.SubdivisionTypeId != ElementId.InvalidElementId)
            created.ChangeTypeId(request.SubdivisionTypeId);
        ToposolidIdentity.Write(created, schema, plan.IdentityKey, plan.Fingerprint);

        // A subdivision on a replaced host dies with it; one still on the current host is ours to delete.
        if (exists && onCurrentHost)
            request.Document.Delete(existing.Element.Id);
        report.Add(action == ToposolidWriteAction.Replace
            ? $"{plan.IdentityKey}: replaced subdivision with {created.Id}."
            : $"{plan.IdentityKey}: created subdivision {created.Id}.");
        return created;
    }

    private static void ReportOrphanedSubdivisions(
        PlannedToposolid plan,
        Toposolid host,
        Dictionary<string, (Toposolid Element, string? Fingerprint)> subdivisions,
        List<string> report)
    {
        string prefix = ToposolidWritePlanner.SubdivisionIdentityKey(plan.Key, string.Empty);
        var planned = plan.Subdivisions.Select(subdivision => subdivision.IdentityKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, existing) in subdivisions)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                !planned.Contains(key) &&
                existing.Element.IsValidObject &&
                existing.Element.HostTopoId == host.Id)
            {
                report.Add($"{key}: no longer prepared; left subdivision {existing.Element.Id} in place. Delete it in Revit if the zone was removed.");
            }
        }
    }

    private static void CopyParameters(Element source, Element target, IReadOnlyList<string> names, string key, List<string> report)
    {
        foreach (string name in names)
        {
            Parameter? from = source.LookupParameter(name);
            if (from == null || !from.HasValue)
                continue;
            Parameter? to = target.LookupParameter(name);
            if (to == null || to.IsReadOnly || to.StorageType != from.StorageType)
            {
                report.Add($"{key}: could not preserve parameter '{name}' on the replacement.");
                continue;
            }

            bool set = from.StorageType switch
            {
                StorageType.Double => to.Set(from.AsDouble()),
                StorageType.Integer => to.Set(from.AsInteger()),
                StorageType.String => to.Set(from.AsString()),
                StorageType.ElementId => to.Set(from.AsElementId()),
                _ => false
            };
            if (!set)
                report.Add($"{key}: could not preserve parameter '{name}' on the replacement.");
        }
    }

    private static List<CurveLoop> ToCurveLoops(IReadOnlyList<PlannedLoop> loops)
    {
        var result = new List<CurveLoop>(loops.Count);
        foreach (PlannedLoop loop in loops)
        {
            var curveLoop = new CurveLoop();
            for (int index = 0; index < loop.Vertices.Count; index++)
            {
                XYZ start = ToXyz(loop.Vertices[index]);
                XYZ end = ToXyz(loop.Vertices[(index + 1) % loop.Vertices.Count]);
                curveLoop.Append(Line.CreateBound(start, end));
            }

            result.Add(curveLoop);
        }

        return result;
    }

    private static List<XYZ> ToXyz(IReadOnlyList<RhinoPoint> points) => points.Select(ToXyz).ToList();

    private static XYZ ToXyz(RhinoPoint point) => new(point.X, point.Y, point.Z);
}
