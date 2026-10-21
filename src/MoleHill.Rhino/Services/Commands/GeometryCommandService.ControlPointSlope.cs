using MoleHill.Core.Analysis;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>Direct grip selection and cancellable slope preview for mhSlopeControlPoints.</summary>
internal static partial class GeometryCommandService
{
    public static Result RunSlopeControlPoints(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        // GetObject's ordinary preselection enumeration omits owners whose only selection
        // is a curve segment. Capture those explicitly alongside selected grips.
        var selectedObjects = new List<ObjRef>();
        var selectedSettings = new ObjectEnumeratorSettings
        {
            SelectedObjectsFilter = true,
            SubObjectSelected = true,
            IncludeGrips = true,
            LockedObjects = false,
            ObjectTypeFilter = ObjectType.Curve | ObjectType.Grip
        };
        foreach (RhinoObject obj in doc.Objects.GetObjectList(selectedSettings))
        {
            if (obj is GripObject) selectedObjects.Add(new ObjRef(obj));
            else if (obj is CurveObject)
                foreach (ComponentIndex component in obj.GetSelectedSubObjects() ?? Array.Empty<ComponentIndex>())
                    if (component.ComponentIndexType == ComponentIndexType.PolycurveSegment)
                        selectedObjects.Add(new ObjRef(doc, obj.Id, component));
        }
        if (selectedObjects.Count == 0 && !TryPickControlPoints(doc, selectedObjects, out Result picked))
            return picked;
        try
        {
            return RunSlopeControlPointSelection(doc, selectedObjects);
        }
        finally
        {
            foreach (ObjRef selected in selectedObjects) selected.Dispose();
        }
    }

    /// <summary>
    /// Picks control points, Ctrl+Shift curve segments, or whole curves after the command has started. A
    /// whole curve has its control points turned on and is asked for again, since hidden points cannot be
    /// picked. Grips must win over the curve under them: preferring the bottom object picked the curve
    /// instead, which the filter refused, so picking after the command started selected nothing (found live).
    /// </summary>
    private static bool TryPickControlPoints(RhinoDoc doc, List<ObjRef> selectedObjects, out Result result)
    {
        result = Result.Success;
        using (var selection = new GetObject())
        {
            selection.SetCommandPrompt("Select curve control points, Ctrl+Shift-select curve segments, or select curves to show their control points");
            selection.GeometryFilter = ObjectType.Grip | ObjectType.Curve;
            selection.SubObjectSelect = true;
            selection.EnablePreSelect(false, true);
            selection.SetCustomGeometryFilter((obj, geometry, component) => obj is GripObject ||
                (obj is CurveObject && component.ComponentIndexType is ComponentIndexType.PolycurveSegment or ComponentIndexType.InvalidType));
            if (selection.GetMultiple(1, 0) != GetResult.Object)
            {
                result = selection.CommandResult();
                return false;
            }

            var wholeCurves = new List<RhinoObject>();
            for (int index = 0; index < selection.ObjectCount; index++)
            {
                ObjRef picked = selection.Object(index);
                if (picked.Object() is CurveObject curve && picked.GeometryComponentIndex.ComponentIndexType == ComponentIndexType.InvalidType)
                    wholeCurves.Add(curve);
                else if (picked.Object() is GripObject grip)
                    selectedObjects.Add(new ObjRef(grip));
                else
                    selectedObjects.Add(new ObjRef(doc, picked.ObjectId, picked.GeometryComponentIndex));
            }

            if (wholeCurves.Count == 0)
                return true;

            doc.Objects.UnselectAll();
            foreach (RhinoObject curve in wholeCurves)
                curve.GripsOn = true;
            doc.Views.Redraw();
        }

        using var points = new GetObject();
        points.SetCommandPrompt("Select the control points to put on the slope");
        points.GeometryFilter = ObjectType.Grip;
        points.EnablePreSelect(false, true);
        if (points.GetMultiple(1, 0) != GetResult.Object)
        {
            result = points.CommandResult();
            return false;
        }

        for (int index = 0; index < points.ObjectCount; index++)
            selectedObjects.Add(new ObjRef(points.Object(index).Object()));
        return true;
    }

    private static Result RunSlopeControlPointSelection(RhinoDoc doc, IReadOnlyList<ObjRef> selectedObjects)
    {
        var groups = new Dictionary<Guid, List<int>>();
        var sections = new Dictionary<Guid, List<int>>();
        var anchors = new List<Point3d>();
        foreach (ObjRef selected in selectedObjects)
        {
            if (selected.Object() is CurveObject sectionOwner &&
                selected.GeometryComponentIndex.ComponentIndexType == ComponentIndexType.PolycurveSegment)
            {
                int segment = selected.GeometryComponentIndex.Index;
                Point3d[]? points = ControlPointSlopeEdit.GetSegmentLocations((Curve)sectionOwner.Geometry, segment);
                if (points == null) return Result.Failure;
                if (!groups.ContainsKey(sectionOwner.Id)) groups.Add(sectionOwner.Id, new List<int>());
                if (!sections.TryGetValue(sectionOwner.Id, out List<int>? segments))
                    sections.Add(sectionOwner.Id, segments = new List<int>());
                if (!segments.Contains(segment)) segments.Add(segment);
                anchors.AddRange(points);
                continue;
            }
            if (selected.Object() is not GripObject grip ||
                doc.Objects.FindId(grip.OwnerId) is not CurveObject owner)
            {
                RhinoApp.WriteLine("Select curve control points or Ctrl+Shift-selected polyline/polycurve segments.");
                return Result.Failure;
            }
            Point3d[]? locations = ControlPointSlopeEdit.GetLocations((Curve)owner.Geometry);
            if (locations == null || grip.Index < 0 || grip.Index >= locations.Length ||
                locations[grip.Index].DistanceTo(grip.CurrentLocation) > doc.ModelAbsoluteTolerance)
            {
                RhinoApp.WriteLine("Use NURBS curve, polyline or line control points; convert other curves to NURBS first.");
                return Result.Failure;
            }
            anchors.Add(grip.CurrentLocation);
            if (!groups.TryGetValue(owner.Id, out List<int>? indices))
                groups.Add(owner.Id, indices = new List<int>());
            if (!indices.Contains(grip.Index)) indices.Add(grip.Index);
        }

        using var referencePick = new GetPoint();
        referencePick.SetCommandPrompt("Reference point (Point object snap for another control point)");
        if (referencePick.Get() != GetResult.Point)
            return referencePick.CommandResult();
        Point3d reference = referencePick.Point();
        Point3d anchor = anchors.FirstOrDefault(point =>
            new Point2d(point.X, point.Y).DistanceTo(new Point2d(reference.X, reference.Y)) > doc.ModelAbsoluteTolerance,
            Point3d.Unset);
        if (!anchor.IsValid)
        {
            RhinoApp.WriteLine("The selected geometry and reference need different world XY positions.");
            return Result.Failure;
        }
        double run = new Point2d(anchor.X, anchor.Y).DistanceTo(new Point2d(reference.X, reference.Y));
        if (run <= doc.ModelAbsoluteTolerance)
        {
            RhinoApp.WriteLine("The first control point and reference need different world XY positions.");
            return Result.Failure;
        }

        var sources = groups.Select(group => (Id: group.Key,
            Curve: ((Curve)doc.Objects.FindId(group.Key).Geometry).DuplicateCurve(),
            Indices: group.Value,
            Segments: sections.TryGetValue(group.Key, out List<int>? segments) ? segments : new List<int>())).ToArray();
        using var preview = new ControlPointSlopePreview(doc.RuntimeSerialNumber, reference);
        var slope = new SlopeCommandOption("MoleHill.SlopeControlPoints.Slope", 0.02);
        double pickedRatio = slope.Ratio;
        // Like Offset: the mouse sets the slope until one is typed; then the mouse only chooses whether it
        // rises or falls from the reference, and Through hands the slope back to the mouse. One preview,
        // and a click and Enter apply the same thing (found live: a click took the mouse's slope and Enter
        // the typed one, with both previewed at once).
        bool typed = false;
        double RatioAt(double z) => typed ? (z < reference.Z ? -1 : 1) * Math.Abs(slope.Ratio) : (z - reference.Z) / run;
        try
        {
            bool Update(double ratio)
            {
                var curves = new List<Curve>();
                var points = new List<Point3d>();
                foreach (var source in sources)
                {
                    Curve? curve;
                    string? error;
                    bool success = source.Segments.Count > 0
                        ? ControlPointSlopeEdit.TryCreateSections(source.Curve, source.Segments, source.Indices,
                            reference, ratio, doc.ModelAbsoluteTolerance, out curve, out error)
                        : ControlPointSlopeEdit.TryCreate(source.Curve, source.Indices, reference,
                            ratio, doc.ModelAbsoluteTolerance, out curve, out error);
                    if (!success)
                    {
                        foreach (Curve created in curves) created.Dispose();
                        RhinoApp.WriteLine(error);
                        return false;
                    }
                    curves.Add(curve!);
                    if (ControlPointSlopeEdit.GetLocations(curve!) is { } locations)
                        points.AddRange(source.Indices.Select(index => locations[index]));
                    foreach (int segment in source.Segments)
                        points.AddRange(ControlPointSlopeEdit.GetSegmentLocations(curve!, segment)!);
                }
                preview.Update(curves, points, SlopeInput.FormatWithUnit(ratio, SlopeUnitPreference.Current));
                return true;
            }

            if (!Update(pickedRatio)) return Result.Failure;
            preview.Enabled = true;
            while (true)
            {
                doc.Views.Redraw();
                using var pick = new GetPoint();
                string current = SlopeInput.FormatWithUnit(pickedRatio, SlopeUnitPreference.Current);
                pick.SetCommandPrompt(typed
                    ? $"Move above the reference to rise or below it to fall at {SlopeInput.FormatWithUnit(Math.Abs(slope.Ratio), SlopeUnitPreference.Current)}; click or Enter applies {current}"
                    : $"Move vertically to set the slope, or type Slope; click or Enter applies {current}");
                pick.AcceptNothing(true);
                pick.Constrain(new Line(anchor, anchor + Vector3d.ZAxis));
                slope.AddTo(pick);
                int throughOption = typed ? pick.AddOption("Through") : -1;
                pick.DynamicDraw += (_, e) =>
                {
                    pickedRatio = RatioAt(e.CurrentPoint.Z);
                    Update(pickedRatio);
                };
                GetResult result = pick.Get();
                if (result == GetResult.Option)
                {
                    if (pick.OptionIndex() == throughOption)
                    {
                        typed = false;
                        continue;
                    }

                    bool valueTyped = pick.OptionIndex() == slope.ValueOptionIndex;
                    slope.Commit(pick, result);
                    if (valueTyped)
                    {
                        typed = true;
                        pickedRatio = slope.Ratio;
                    }

                    if (!Update(pickedRatio)) return Result.Failure;
                    continue;
                }
                if (result == GetResult.Point)
                    pickedRatio = RatioAt(pick.Point().Z);
                else if (result != GetResult.Nothing)
                    return pick.CommandResult();
                if (!Update(pickedRatio)) return Result.Failure;
                break;
            }

            preview.Enabled = false;
            for (int index = 0; index < sources.Length; index++)
            {
                if (doc.Objects.Replace(sources[index].Id, preview.Curves[index])) continue;
                // Restore any earlier replacements if a later owner could not be changed.
                for (int restore = 0; restore < index; restore++)
                    doc.Objects.Replace(sources[restore].Id, sources[restore].Curve);
                RhinoApp.WriteLine("Rhino could not replace the curve; the edit was cancelled.");
                return Result.Failure;
            }
            foreach (var source in sources)
            {
                RhinoObject owner = doc.Objects.FindId(source.Id);
                if (source.Indices.Count > 0)
                {
                    owner.GripsOn = true;
                    GripObject[] grips = owner.GetGrips();
                    foreach (int index in source.Indices) grips[index].Select(true);
                }
                foreach (int segment in source.Segments)
                    owner.SelectSubObject(new ComponentIndex(ComponentIndexType.PolycurveSegment, segment), true, true);
            }
            RhinoApp.WriteLine($"Control point slope: {SlopeInput.FormatWithUnit(pickedRatio, SlopeUnitPreference.Current)}");
            return Result.Success;
        }
        finally
        {
            preview.Enabled = false;
            foreach (var source in sources) source.Curve.Dispose();
            doc.Views.Redraw();
        }
    }

    private sealed class ControlPointSlopePreview(uint documentSerial, Point3d reference) : DisplayConduit, IDisposable
    {
        // Rhino's tracking colour can match the viewport background. This preview needs
        // a visible colour on both light and dark viewports, including view captures.
        private static readonly System.Drawing.Color PreviewColor = System.Drawing.Color.FromArgb(20, 125, 75);
        public List<Curve> Curves { get; private set; } = new();
        private List<Point3d> _points = new();
        private string _label = string.Empty;

        public void Update(List<Curve> curves, List<Point3d> points, string label)
        {
            foreach (Curve curve in Curves) curve.Dispose();
            Curves = curves;
            _points = points;
            _label = label;
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
            e.IncludeBoundingBox(new BoundingBox(_points.Append(reference)));
            foreach (Curve curve in Curves) e.IncludeBoundingBox(curve.GetBoundingBox(false));
        }

        // Everything is drawn in the overlay. While a point is picked Rhino repaints only the overlay as
        // the mouse moves, so curves drawn in DrawForeground stayed put until the camera moved and only the
        // dots followed the mouse (found live).
        protected override void DrawOverlay(DrawEventArgs e)
        {
            if (e.RhinoDoc.RuntimeSerialNumber != documentSerial) return;
            foreach (Curve curve in Curves) e.Display.DrawCurve(curve, PreviewColor, 2);
            foreach (Point3d point in _points)
            {
                e.Display.DrawLine(reference, point, PreviewColor);
                e.Display.DrawPoint(point, PreviewColor);
            }
            e.Display.DrawPoint(reference, PreviewColor);
            foreach (Point3d point in _points)
                e.Display.DrawDot(point, $"{_label} / Z {point.Z:F3}", PreviewColor, System.Drawing.Color.White);
        }

        public void Dispose()
        {
            Enabled = false;
            foreach (Curve curve in Curves) curve.Dispose();
            Curves.Clear();
        }
    }
}
