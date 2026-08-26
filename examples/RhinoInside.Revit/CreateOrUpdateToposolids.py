"""Optional Rhino.Inside.Revit Python 3 adapter for MoleHill Prepare Toposolid outputs.

Grasshopper inputs: Run, Profiles (tree), Points (tree), Keys (list), Fingerprints (list),
MetersPerUnit (list/item), ToposolidType, Level, PreserveParameters (list).
Outputs: Elements, Report.
"""

from System import Guid
from System.Collections.Generic import List
from Autodesk.Revit import DB
from Autodesk.Revit.DB.ExtensibleStorage import AccessLevel, Entity, Schema, SchemaBuilder
from RhinoInside.Revit import Revit


SCHEMA_GUID = Guid("b57636d2-a64d-4e6b-bb42-c770b99bb5c7")


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


def item_at(values, index, fallback=None):
    items = sequence(values)
    if not items:
        return fallback
    return items[min(index, len(items) - 1)]


def element_id(value):
    if value is None:
        return DB.ElementId.InvalidElementId
    if hasattr(value, "Value"):
        value = value.Value
    if isinstance(value, DB.ElementId):
        return value
    if hasattr(value, "Id"):
        return value.Id
    return DB.ElementId(int(value))


def identity_schema():
    schema = Schema.Lookup(SCHEMA_GUID)
    if schema is not None:
        return schema
    builder = SchemaBuilder(SCHEMA_GUID)
    builder.SetSchemaName("MoleHillToposolidIdentity")
    builder.SetDocumentation("Stable MoleHill terrain key and prepared-geometry fingerprint.")
    builder.SetReadAccessLevel(AccessLevel.Public)
    builder.SetWriteAccessLevel(AccessLevel.Public)
    builder.AddSimpleField("MoleHillKey", str)
    builder.AddSimpleField("GeometryFingerprint", str)
    return builder.Finish()


def read_identity(element, schema):
    entity = element.GetEntity(schema)
    if entity is None or not entity.IsValid():
        return None, None
    return (
        entity.Get[str](schema.GetField("MoleHillKey")),
        entity.Get[str](schema.GetField("GeometryFingerprint")),
    )


def write_identity(element, schema, key, fingerprint):
    entity = Entity(schema)
    entity.Set[str](schema.GetField("MoleHillKey"), key)
    entity.Set[str](schema.GetField("GeometryFingerprint"), fingerprint)
    element.SetEntity(entity)


def revit_curve_loops(rhino_profiles, scale, short_curve_tolerance):
    result = List[DB.CurveLoop]()
    for profile in rhino_profiles:
        success, polyline = profile.TryGetPolyline()
        if not success or not polyline.IsClosed or polyline.Count < 4:
            raise ValueError("Prepare Toposolid profile is not a closed polyline.")
        loop = DB.CurveLoop()
        for index in range(polyline.Count - 1):
            start = polyline[index]
            end = polyline[index + 1]
            a = DB.XYZ(start.X * scale, start.Y * scale, start.Z * scale)
            b = DB.XYZ(end.X * scale, end.Y * scale, end.Z * scale)
            if a.DistanceTo(b) <= short_curve_tolerance:
                raise ValueError("A prepared profile segment is shorter than Revit's ShortCurveTolerance.")
            loop.Append(DB.Line.CreateBound(a, b))
        result.Add(loop)
    return result


def revit_points(rhino_points, scale):
    result = List[DB.XYZ]()
    for point in rhino_points:
        result.Add(DB.XYZ(point.X * scale, point.Y * scale, point.Z * scale))
    return result


def capture_parameters(element, names):
    values = []
    for name in names:
        parameter = element.LookupParameter(name)
        if parameter is None:
            continue
        storage = parameter.StorageType
        if storage == DB.StorageType.Double:
            value = parameter.AsDouble()
        elif storage == DB.StorageType.Integer:
            value = parameter.AsInteger()
        elif storage == DB.StorageType.String:
            value = parameter.AsString()
        elif storage == DB.StorageType.ElementId:
            value = parameter.AsElementId()
        else:
            continue
        values.append((name, storage, value))
    return values


def restore_parameters(element, values, report):
    for name, storage, value in values:
        parameter = element.LookupParameter(name)
        if parameter is None or parameter.IsReadOnly or parameter.StorageType != storage:
            report.append("Could not preserve parameter '{}' on replacement.".format(name))
            continue
        try:
            parameter.Set(value)
        except Exception as error:
            report.append("Could not preserve parameter '{}': {}".format(name, error))


Elements = []
Report = []

if Run:
    document = Revit.ActiveDBDocument
    if document is None:
        raise RuntimeError("No active Revit document.")

    profile_branches = branches(Profiles)
    point_branches = branches(Points)
    keys = sequence(Keys)
    fingerprints = sequence(Fingerprints)
    if len(profile_branches) != len(point_branches):
        raise ValueError("Profiles and Points must have the same branch count.")
    if len(keys) != len(profile_branches) or len(fingerprints) != len(profile_branches):
        raise ValueError("Provide one stable key and fingerprint per terrain branch.")
    if any(not key for key in keys) or len(set(keys)) != len(keys):
        raise ValueError("Terrain keys must be non-empty and unique.")

    schema = identity_schema()
    existing_by_key = {}
    for candidate in DB.FilteredElementCollector(document).OfClass(DB.Toposolid):
        key, fingerprint = read_identity(candidate, schema)
        if key:
            if key in existing_by_key:
                raise ValueError("Revit contains more than one Toposolid with MoleHill key '{}'.".format(key))
            existing_by_key[key] = (candidate, fingerprint)

    type_id_input = element_id(ToposolidType)
    level_id_input = element_id(Level)
    parameter_names = sequence(PreserveParameters)
    group = DB.TransactionGroup(document, "Create or update MoleHill Toposolids")
    transaction = DB.Transaction(document, "Write MoleHill Toposolids")
    group.Start()
    try:
        transaction.Start()
        for index, key in enumerate(keys):
            fingerprint = fingerprints[index]
            existing, existing_fingerprint = existing_by_key.get(key, (None, None))
            if existing is not None and existing_fingerprint == fingerprint:
                Elements.append(existing)
                Report.append("{}: unchanged; kept Toposolid {}.".format(key, existing.Id))
                continue

            type_id = type_id_input
            level_id = level_id_input
            preserved = []
            if existing is not None:
                type_id = existing.GetTypeId()
                level_id = existing.LevelId
                preserved = capture_parameters(existing, parameter_names)
            if type_id == DB.ElementId.InvalidElementId or level_id == DB.ElementId.InvalidElementId:
                raise ValueError("ToposolidType and Level are required when creating a new MoleHill key.")

            meters_per_unit = float(item_at(MetersPerUnit, index, 1.0))
            if meters_per_unit <= 0.0:
                raise ValueError("MetersPerUnit must be positive.")
            scale_to_internal_feet = meters_per_unit / 0.3048
            loops = revit_curve_loops(
                profile_branches[index],
                scale_to_internal_feet,
                document.Application.ShortCurveTolerance,
            )
            points = revit_points(point_branches[index], scale_to_internal_feet)
            replacement = DB.Toposolid.Create(document, loops, points, type_id, level_id)
            write_identity(replacement, schema, key, fingerprint)
            restore_parameters(replacement, preserved, Report)
            if existing is not None:
                document.Delete(existing.Id)
                Report.append("{}: replaced changed Toposolid with {}.".format(key, replacement.Id))
            else:
                Report.append("{}: created Toposolid {}.".format(key, replacement.Id))
            Elements.append(replacement)

        transaction.Commit()
        group.Assimilate()
    except Exception:
        if transaction.GetStatus() == DB.TransactionStatus.Started:
            transaction.RollBack()
        group.RollBack()
        raise
