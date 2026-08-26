"""Optional Rhino.Inside.Revit Python 3 adapter for MoleHill subdivision profiles.

Inputs: Run, Hosts (list), Profiles (tree), Keys (list), Fingerprints (list), MetersPerUnit (list/item),
SubdivisionType (optional item).
Outputs: Subdivisions, Report.
"""

from System import Guid, Int32
from System.Collections.Generic import List
from Autodesk.Revit import DB
from Autodesk.Revit.DB.ExtensibleStorage import AccessLevel, Entity, Schema, SchemaBuilder
from RhinoInside.Revit import Revit


SCHEMA_GUID = Guid("36fcfa94-d039-4f31-a379-0c973c4490eb")


def branches(tree):
    if tree is None:
        return []
    if hasattr(tree, "BranchCount"):
        return [list(tree.Branch(index)) for index in range(tree.BranchCount)]
    try:
        return [list(tree)]
    except TypeError:
        return [[tree]]


def sequence(value):
    if value is None:
        return []
    if isinstance(value, str):
        return [value]
    try:
        return list(value)
    except TypeError:
        return [value]


def item_at(value, index, fallback=None):
    items = sequence(value)
    return fallback if not items else items[min(index, len(items) - 1)]


def schema_for_identity():
    schema = Schema.Lookup(SCHEMA_GUID)
    if schema:
        return schema
    builder = SchemaBuilder(SCHEMA_GUID)
    builder.SetSchemaName("MoleHillToposolidSubdivisionIdentity")
    builder.SetReadAccessLevel(AccessLevel.Public)
    builder.SetWriteAccessLevel(AccessLevel.Public)
    builder.AddSimpleField("MoleHillKey", str)
    builder.AddSimpleField("GeometryFingerprint", str)
    builder.AddSimpleField("HostId", Int32)
    return builder.Finish()


def read_identity(element, schema):
    entity = element.GetEntity(schema)
    if entity is None or not entity.IsValid():
        return None, None, None
    return (
        entity.Get[str](schema.GetField("MoleHillKey")),
        entity.Get[str](schema.GetField("GeometryFingerprint")),
        entity.Get[Int32](schema.GetField("HostId")),
    )


def write_identity(element, schema, key, fingerprint, host_id):
    entity = Entity(schema)
    entity.Set[str](schema.GetField("MoleHillKey"), key)
    entity.Set[str](schema.GetField("GeometryFingerprint"), fingerprint)
    entity.Set[Int32](schema.GetField("HostId"), host_id.IntegerValue)
    element.SetEntity(entity)


def curve_loops(profiles, scale, short_curve_tolerance):
    loops = List[DB.CurveLoop]()
    for profile in profiles:
        success, polyline = profile.TryGetPolyline()
        if not success or not polyline.IsClosed:
            raise ValueError("Subdivision profile must be a closed polyline from Prepare Toposolid.")
        loop = DB.CurveLoop()
        for index in range(polyline.Count - 1):
            a = polyline[index]
            b = polyline[index + 1]
            start = DB.XYZ(a.X * scale, a.Y * scale, a.Z * scale)
            end = DB.XYZ(b.X * scale, b.Y * scale, b.Z * scale)
            if start.DistanceTo(end) <= short_curve_tolerance:
                raise ValueError("A subdivision profile segment is shorter than Revit's ShortCurveTolerance.")
            loop.Append(DB.Line.CreateBound(start, end))
        loops.Add(loop)
    return loops


Subdivisions = []
Report = []

if Run:
    document = Revit.ActiveDBDocument
    if document is None:
        raise RuntimeError("No active Revit document is available.")
    profile_branches = branches(Profiles)
    hosts = sequence(Hosts)
    keys = sequence(Keys)
    fingerprints = sequence(Fingerprints)
    if not (len(profile_branches) == len(hosts) == len(keys) == len(fingerprints)):
        raise ValueError("Hosts, profile branches, keys and fingerprints must have matching counts.")
    if any(not key for key in keys) or len(set(keys)) != len(keys):
        raise ValueError("Subdivision keys must be non-empty and unique.")

    schema = schema_for_identity()
    existing_by_key = {}
    for candidate in DB.FilteredElementCollector(document).OfClass(DB.Toposolid):
        key, fingerprint, host_id = read_identity(candidate, schema)
        if key:
            if key in existing_by_key:
                raise ValueError("More than one existing subdivision has MoleHill key '{}'.".format(key))
            existing_by_key[key] = (candidate, fingerprint, host_id)

    group = DB.TransactionGroup(document, "Create or update MoleHill Toposolid subdivisions")
    transaction = DB.Transaction(document, "Write MoleHill Toposolid subdivisions")
    group.Start()
    try:
        transaction.Start()
        for index, key in enumerate(keys):
            host = hosts[index]
            if hasattr(host, "Value"):
                host = host.Value
            host_id = host.Id if hasattr(host, "Id") else host
            fingerprint = fingerprints[index]
            existing, old_fingerprint, old_host_id = existing_by_key.get(key, (None, None, None))
            if existing is not None and old_fingerprint == fingerprint and old_host_id == host_id.IntegerValue:
                Subdivisions.append(existing)
                Report.append("{}: unchanged.".format(key))
                continue

            meters_per_unit = float(item_at(MetersPerUnit, index, 1.0))
            scale = meters_per_unit / 0.3048
            loops = curve_loops(profile_branches[index], scale, document.Application.ShortCurveTolerance)
            subdivision_type_id = None
            if SubdivisionType is not None:
                subdivision_type = SubdivisionType.Value if hasattr(SubdivisionType, "Value") else SubdivisionType
                subdivision_type_id = subdivision_type.Id if hasattr(subdivision_type, "Id") else subdivision_type
            replacement = (
                host.CreateSubDivision(document, loops)
                if subdivision_type_id is None
                else host.CreateSubDivision(document, subdivision_type_id, loops)
            )
            write_identity(replacement, schema, key, fingerprint, host_id)
            if existing is not None:
                document.Delete(existing.Id)
                Report.append("{}: replaced changed subdivision.".format(key))
            else:
                Report.append("{}: created subdivision.".format(key))
            Subdivisions.append(replacement)

        transaction.Commit()
        group.Assimilate()
    except Exception:
        if transaction.GetStatus() == DB.TransactionStatus.Started:
            transaction.RollBack()
        group.RollBack()
        raise
