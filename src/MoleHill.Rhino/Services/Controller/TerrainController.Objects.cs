using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

// Object-card actions: live ownership, durable base correction and viewport inspection.
internal sealed partial class TerrainController
{
    public void ReleaseObjects(RhinoDoc doc, Guid terrainId, Guid definitionId, bool selectedOnly)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var definition = terrain.Objects.FirstOrDefault(item => item.Id == definitionId);
            if (definition == null || definition is ScatterObjectDefinition) return;
            var selected = selectedOnly ? doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).ToHashSet() : null;
            var ids = RhinoSourceResolver.ResolveObjects(doc, definition.Sources)
                .Where(obj => selected == null || selected.Contains(obj.Id)).Select(obj => obj.Id).ToHashSet();
            foreach (Guid id in ids)
                if (!definition.ReleasedObjectIds.Contains(id)) definition.ReleasedObjectIds.Add(id);
            definition.Sources.RemoveObjects(ids);
            definition.PlacementStates.RemoveAll(state => ids.Contains(state.ObjectId));
        }, undoDescription: "Release MoleHill Objects");
    }

    public void ShowObjectBases(RhinoDoc doc, Guid terrainId, Guid definitionId)
    {
        var definition = GetState(doc).Terrains.FirstOrDefault(t => t.TerrainId == terrainId)?.Objects.FirstOrDefault(d => d.Id == definitionId);
        if (definition == null) return;
        var references = RhinoSourceResolver.ResolveObjects(doc, definition.Sources)
            .Where(obj => !definition.ReleasedObjectIds.Contains(obj.Id))
            .Select(obj => GetCurrentObjectReference(doc, definition, obj.Id)).Where(reference => reference != null)
            .Cast<ObjectPlacementReference>().ToArray();
        using var preview = new ObjectPlacementBasePreview(doc.RuntimeSerialNumber, references);
        doc.Views.Redraw();
        using var get = new GetOption();
        get.SetCommandPrompt("Object bases (arrow shows up). Enter or Esc to finish");
        get.AcceptNothing(true);
        get.Get();
        preview.Enabled = false;
        doc.Views.Redraw();
    }

    public void ChangeObjectBase(RhinoDoc doc, Guid terrainId, Guid definitionId)
    {
        var definition = GetState(doc).Terrains.FirstOrDefault(t => t.TerrainId == terrainId)?.Objects.FirstOrDefault(d => d.Id == definitionId);
        if (definition == null) return;
        var ids = RhinoSourceResolver.ResolveObjects(doc, definition.Sources)
            .Where(obj => !definition.ReleasedObjectIds.Contains(obj.Id)).Select(obj => obj.Id).ToHashSet();
        using var pick = new GetObject();
        pick.SetCommandPrompt("Select an object from this card to change its base");
        pick.SetCustomGeometryFilter((obj, _, _) => ids.Contains(obj.Id));
        pick.SubObjectSelect = false;
        if (pick.Get() != GetResult.Object) return;
        Guid id = pick.Object(0).ObjectId;
        var reference = GetCurrentObjectReference(doc, definition, id);
        if (reference == null)
        {
            RhinoApp.WriteLine("This object does not have a usable placement box.");
            return;
        }
        using var preview = new ObjectPlacementBasePreview(doc.RuntimeSerialNumber, new[] { reference });
        string[] faces = { "Bottom", "Top", "Front", "Back", "Left", "Right" };
        int[,] corners = { { 0, 1, 3 }, { 4, 7, 5 }, { 0, 4, 1 }, { 3, 2, 7 }, { 0, 3, 4 }, { 1, 5, 2 } };
        using var options = new GetOption();
        options.SetCommandPrompt("Choose a base face; Enter accepts the preview");
        options.AcceptNothing(true);
        int faceOption = options.AddOptionList("Face", faces, 0);
        int customOption = options.AddOption("ThreePoints");
        bool accepted = false;
        while (true)
        {
            doc.Views.Redraw();
            var result = options.Get();
            if (result == GetResult.Nothing) { accepted = true; break; }
            if (result != GetResult.Option) break;
            if (options.OptionIndex() == faceOption)
            {
                int index = options.Option().CurrentListOptionIndex;
                var origin = reference.BoxCorners[corners[index, 0]];
                var x = reference.BoxCorners[corners[index, 1]] - origin;
                var y = reference.BoxCorners[corners[index, 2]] - origin;
                // Selected face's inward normal is the object's up direction.
                ObjectPlacementGeometry.SetBase(reference, new Plane(origin, x, y));
            }
            else if (options.OptionIndex() == customOption)
            {
                if (RhinoGet.GetPoint("Base plane origin", false, out var origin) != Result.Success ||
                    RhinoGet.GetPoint("Point along base X direction", false, out var xPoint) != Result.Success ||
                    RhinoGet.GetPoint("Point on base Y side", false, out var yPoint) != Result.Success) break;
                var plane = new Plane(origin, xPoint, yPoint);
                if (!plane.IsValid) { RhinoApp.WriteLine("The three points do not define a plane."); break; }
                ObjectPlacementGeometry.SetBase(reference, plane);
            }
        }
        preview.Enabled = false;
        doc.Views.Redraw();
        if (!accepted || !reference.IsValid) return;
        using var undo = BeginTerrainUndoTransaction(doc, "Change MoleHill Object Base");
        using var suppression = new EventSuppression(this);
        var obj = doc.Objects.FindId(id);
        if (obj == null) return;
        using var geometry = obj.Geometry.Duplicate();
        if (ObjectPlacementReference.Find(geometry) is { } old) geometry.UserData.Remove(old);
        geometry.UserData.Add(reference.CopyReference());
        if (!doc.Objects.Replace(id, geometry, ignoreModes: false)) return;
        Save(doc, GetState(doc));
        ScheduleRebuild(doc, terrainId);
    }

    private static ObjectPlacementReference? GetCurrentObjectReference(RhinoDoc doc, TerrainObjectDefinition definition, Guid id)
    {
        var obj = doc.Objects.FindId(id);
        if (obj == null) return null;
        var resolved = TerrainBuildSnapshotBuilder.CreateResolvedSourceObject(doc, obj);
        if (resolved == null) return null;
        using (resolved.Geometry)
        {
            var previous = definition.PlacementStates.FirstOrDefault(s => s.ObjectId == id)?.GetLastAppliedTransform() ?? Transform.Identity;
            if (!previous.TryGetInverse(out var inverse)) return null;
            using var baseline = resolved.Geometry.Duplicate();
            if (!baseline.Transform(inverse) || !ObjectPlacementGeometry.TryGetReference(resolved, baseline, inverse, out var reference)) return null;
            reference.TransformReference(previous);
            return reference;
        }
    }

    private static bool TransformsMatch(Transform a, Transform b)
    {
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
                if (!double.IsFinite(a[row, col]) || !double.IsFinite(b[row, col]) ||
                    Math.Abs(a[row, col] - b[row, col]) > 1e-10 * Math.Max(1, Math.Abs(b[row, col]))) return false;
        return true;
    }
}
