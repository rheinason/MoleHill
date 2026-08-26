"""Audit MoleHill identity and editable shape data on Revit Toposolids.

Inputs: Toposolids (list), MetersPerUnit (optional item; defaults to 1 metre per output unit).
Outputs: Keys, Fingerprints, TypeIds, LevelIds, Profiles, ShapePoints, Report.
"""

from System import Guid
from Autodesk.Revit import DB
from Autodesk.Revit.DB.ExtensibleStorage import Schema
from Rhino.Geometry import LineCurve, Point3d
from Grasshopper import DataTree
from Grasshopper.Kernel.Data import GH_Path
from RhinoInside.Revit import Revit


TOPO_SCHEMA_GUID = Guid("b57636d2-a64d-4e6b-bb42-c770b99bb5c7")
SUBDIVISION_SCHEMA_GUID = Guid("36fcfa94-d039-4f31-a379-0c973c4490eb")


def sequence(value):
    if value is None:
        return []
    if isinstance(value, str):
        return [value]
    try:
        return list(value)
    except TypeError:
        return [value]


def read_identity(element):
    for guid in (TOPO_SCHEMA_GUID, SUBDIVISION_SCHEMA_GUID):
        schema = Schema.Lookup(guid)
        if schema is None:
            continue
        entity = element.GetEntity(schema)
        if entity is not None and entity.IsValid():
            return (
                entity.Get[str](schema.GetField("MoleHillKey")),
                entity.Get[str](schema.GetField("GeometryFingerprint")),
            )
    return None, None


document = Revit.ActiveDBDocument
if document is None:
    raise RuntimeError("No active Revit document is available.")
unit_values = sequence(MetersPerUnit)
meters_per_output_unit = float(unit_values[0]) if unit_values else 1.0
feet_to_output = 0.3048 / meters_per_output_unit

Keys = []
Fingerprints = []
TypeIds = []
LevelIds = []
Profiles = DataTree[object]()
ShapePoints = DataTree[Point3d]()
Report = []

for index, toposolid in enumerate(sequence(Toposolids)):
    if hasattr(toposolid, "Value"):
        toposolid = toposolid.Value
    key, fingerprint = read_identity(toposolid)
    Keys.append(key)
    Fingerprints.append(fingerprint)
    TypeIds.append(toposolid.GetTypeId().IntegerValue)
    LevelIds.append(toposolid.LevelId.IntegerValue)
    path = GH_Path(index)

    sketch_id = getattr(toposolid, "SketchId", DB.ElementId.InvalidElementId)
    sketch = document.GetElement(sketch_id) if sketch_id != DB.ElementId.InvalidElementId else None
    if sketch is not None:
        for curve_array in sketch.Profile:
            for curve in curve_array:
                start = curve.GetEndPoint(0)
                end = curve.GetEndPoint(1)
                Profiles.Add(
                    LineCurve(
                        Point3d(start.X * feet_to_output, start.Y * feet_to_output, start.Z * feet_to_output),
                        Point3d(end.X * feet_to_output, end.Y * feet_to_output, end.Z * feet_to_output)),
                    path,
                )

    editor = toposolid.GetSlabShapeEditor()
    if editor is not None:
        for vertex in editor.SlabShapeVertices:
            point = vertex.Position
            ShapePoints.Add(
                Point3d(point.X * feet_to_output, point.Y * feet_to_output, point.Z * feet_to_output),
                path,
            )
    Report.append("{}: key={}, fingerprint={}.".format(toposolid.Id, key or "<none>", fingerprint or "<none>"))
