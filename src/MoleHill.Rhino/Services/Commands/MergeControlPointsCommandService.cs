using System.Drawing;
using MoleHill.Core.Processing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>Direct curve grip picking, four merge methods and a detached curve preview before acceptance.</summary>
internal static class MergeControlPointsCommandService
{
    private readonly record struct GripKey(Guid Owner, int Index);

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out var units)) return Result.Failure;
        var keys = new List<GripKey>();
        foreach (RhinoObject obj in doc.Objects.GetSelectedObjects(false, true))
        {
            if (obj is not GripObject grip) continue;
            if (!TryAdd(doc, grip, keys)) return Result.Failure;
        }
        bool preselected = keys.Count > 0;
        if (!preselected)
        {
            while (true)
            {
                using var pick = new GetObject();
                pick.SetCommandPrompt("Pick curve control points in order; Enter when done");
                pick.GeometryFilter = ObjectType.Grip;
                pick.EnablePreSelect(false, true);
                pick.EnableUnselectObjectsOnExit(false);
                pick.EnableClearObjectsOnEntry(false);
                pick.AcceptNothing(true);
                GetResult result = pick.Get();
                if (result == GetResult.Nothing) break;
                if (result != GetResult.Object) return pick.CommandResult();
                if (pick.Object(0).Object() is not GripObject grip || !TryAdd(doc, grip, keys))
                    continue;
                grip.Select(true);
                doc.Views.Redraw();
            }
        }
        if (keys.Count < 2)
        {
            RhinoApp.WriteLine("Select at least two curve control points.");
            return Result.Nothing;
        }
        if (preselected)
            RhinoApp.WriteLine("First/Last use Rhino's returned grip order; preselection does not retain click order.");

        var sources = keys.Select(key => key.Owner).Distinct().ToDictionary(id => id,
            id => ((Curve)doc.Objects.FindId(id).Geometry).DuplicateCurve());
        var selection = sources.Keys.ToDictionary(id => id, id => doc.Objects.FindId(id).GetGrips()
            .Where(grip => grip.IsSelected(false) != 0).Select(grip => grip.Index).ToArray());
        Point3d[] points = keys.Select(key => ControlPointSlopeEdit.GetLocations(sources[key.Owner])![key.Index]).ToArray();
        double[] coordinates = points.SelectMany(point => new[] { point.X, point.Y, point.Z }).ToArray();
        PointMergeMode mode = CommandOptionCache.GetValue("MoleHill.MergeControlPoints.Mode", PointMergeMode.AtCenter);
        if (!Enum.IsDefined(typeof(PointMergeMode), mode)) mode = PointMergeMode.AtCenter;
        const string distanceKey = "MoleHill.MergeControlPoints.Distance";
        double distance = CommandOptionCache.GetLengthFromModelDefault(distanceKey, units, doc.ModelAbsoluteTolerance);
        using var preview = new MergePreview(doc.RuntimeSerialNumber, points);
        try
        {
            bool Build(out string? error)
            {
                error = null;
                double[] targets;
                try { targets = PointMerge.CreateTargets(coordinates, mode, distance); }
                catch (ArgumentException exception) { preview.Clear(); error = exception.Message; return false; }
                var locations = Enumerable.Range(0, points.Length)
                    .Select(index => new Point3d(targets[index * 3], targets[index * 3 + 1], targets[index * 3 + 2])).ToArray();
                var curves = new Dictionary<Guid, Curve>();
                foreach (var source in sources)
                {
                    var replacements = new Dictionary<int, Point3d>();
                    for (int index = 0; index < keys.Count; index++)
                        if (keys[index].Owner == source.Key) replacements.Add(keys[index].Index, locations[index]);
                    if (ControlPointSlopeEdit.TryReplaceLocations(source.Value, replacements, out Curve? curve, out error))
                        curves.Add(source.Key, curve!);
                    else
                    {
                        foreach (Curve created in curves.Values) created.Dispose();
                        preview.Clear();
                        return false;
                    }
                }
                preview.Update(curves, locations);
                return true;
            }

            while (true)
            {
                bool valid = Build(out string? error);
                preview.Enabled = true;
                doc.Views.Redraw();
                using var options = new GetOption();
                options.SetCommandPrompt(valid
                    ? $"Merge {keys.Count} control points: {mode}, {preview.MovedCount} moved; Enter to accept"
                    : $"{error} Choose another method or Esc to cancel");
                options.AcceptNothing(true);
                int modeOption = options.AddOptionList("Mode", Enum.GetNames(typeof(PointMergeMode)), (int)mode);
                var distanceOption = new OptionDouble(distance, true, 0);
                if (mode == PointMergeMode.ByDistance) options.AddOptionDouble("Distance", ref distanceOption);
                GetResult result = options.Get();
                if (result == GetResult.Option)
                {
                    distance = distanceOption.CurrentValue;
                    if (options.OptionIndex() == modeOption)
                        mode = (PointMergeMode)options.Option().CurrentListOptionIndex;
                    continue;
                }
                if (result != GetResult.Nothing) return options.CommandResult();
                if (!valid) { RhinoApp.WriteLine(error); continue; }
                break;
            }
            preview.Enabled = false;
            CommandOptionCache.SetValue("MoleHill.MergeControlPoints.Mode", mode);
            CommandOptionCache.SetLength(distanceKey, units, distance);
            if (preview.MovedCount == 0)
            {
                RhinoApp.WriteLine("No control points moved.");
                return Result.Success;
            }

            var changed = new List<Guid>();
            foreach (var pair in preview.Curves)
            {
                if (doc.Objects.Replace(pair.Key, pair.Value)) { changed.Add(pair.Key); continue; }
                bool restored = true;
                foreach (Guid id in changed) restored &= doc.Objects.Replace(id, sources[id]);
                RhinoApp.WriteLine(restored ? "Rhino could not update a curve; the merge was cancelled."
                    : "Rhino could not restore every curve. Use Undo to restore the merge.");
                return Result.Failure;
            }
            RhinoApp.WriteLine($"Merged control point locations: {preview.MovedCount} moved across {sources.Count} curves.");
            return Result.Success;
        }
        finally
        {
            preview.Enabled = false;
            foreach (var pair in selection)
            {
                RhinoObject? owner = doc.Objects.FindId(pair.Key);
                if (owner == null) continue;
                owner.GripsOn = true;
                GripObject[] grips = owner.GetGrips();
                foreach (GripObject grip in grips) grip.Select(false);
                foreach (int index in pair.Value) grips[index].Select(true);
            }
            foreach (Curve curve in sources.Values) curve.Dispose();
            doc.Views.Redraw();
        }
    }

    private static bool TryAdd(RhinoDoc doc, GripObject grip, List<GripKey> keys)
    {
        RhinoObject? owner = doc.Objects.FindId(grip.OwnerId);
        Point3d[]? locations = owner?.Geometry is Curve curve ? ControlPointSlopeEdit.GetLocations(curve) : null;
        if (owner == null || owner.IsLocked || owner.IsReference || locations == null ||
            grip.Index < 0 || grip.Index >= locations.Length ||
            locations[grip.Index].DistanceTo(grip.CurrentLocation) > doc.ModelAbsoluteTolerance)
        {
            RhinoApp.WriteLine("Select editable NURBS curve, polyline or line control points; convert other curves to NURBS first.");
            return false;
        }
        var key = new GripKey(grip.OwnerId, grip.Index);
        if (!keys.Contains(key)) keys.Add(key);
        return true;
    }

    private sealed class MergePreview(uint documentSerial, Point3d[] originals) : DisplayConduit, IDisposable
    {
        private static readonly Color PreviewColor = Color.FromArgb(20, 125, 75);
        private Point3d[] _targets = Array.Empty<Point3d>();
        public Dictionary<Guid, Curve> Curves { get; private set; } = new();
        public int MovedCount => Enumerable.Range(0, _targets.Length).Count(index => originals[index] != _targets[index]);

        public void Update(Dictionary<Guid, Curve> curves, Point3d[] targets)
        {
            Clear();
            Curves = curves;
            _targets = targets;
        }

        public void Clear()
        {
            foreach (Curve curve in Curves.Values) curve.Dispose();
            Curves.Clear();
            _targets = Array.Empty<Point3d>();
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
            e.IncludeBoundingBox(new BoundingBox(originals.Concat(_targets)));
            foreach (Curve curve in Curves.Values) e.IncludeBoundingBox(curve.GetBoundingBox(false));
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
            e.Display.PushDepthTesting(false);
            e.Display.PushDepthWriting(false);
            try
            {
                foreach (Curve curve in Curves.Values) e.Display.DrawCurve(curve, PreviewColor, 2);
                for (int index = 0; index < _targets.Length; index++)
                {
                    e.Display.DrawLine(originals[index], _targets[index], PreviewColor);
                    e.Display.DrawPoint(_targets[index], PreviewColor);
                }
            }
            finally { e.Display.PopDepthWriting(); e.Display.PopDepthTesting(); }
        }

        protected override void DrawOverlay(DrawEventArgs e)
        {
            if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
            foreach (Point3d target in _targets.Distinct())
                e.Display.DrawDot(target, "Merge", PreviewColor, Color.White);
        }

        public void Dispose() { Enabled = false; Clear(); }
    }
}
