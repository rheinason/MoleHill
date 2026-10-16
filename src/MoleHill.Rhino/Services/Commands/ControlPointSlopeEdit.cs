using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Builds an independent curve preview by changing only selected control point elevations.</summary>
internal static class ControlPointSlopeEdit
{
    public static int SegmentCount(Curve curve) => curve switch
    {
        PolylineCurve polyline => polyline.PointCount - 1,
        PolyCurve polycurve => polycurve.SegmentCount,
        _ => 0
    };

    public static Point3d[]? GetSegmentLocations(Curve curve, int segment)
    {
        if (segment < 0 || segment >= SegmentCount(curve)) return null;
        if (curve is PolylineCurve polyline)
            return new[] { polyline.Point(segment), polyline.Point(segment + 1) };
        using Curve section = ((PolyCurve)curve).SegmentCurve(segment).DuplicateCurve();
        if (GetLocations(section) is { } locations) return locations;
        using NurbsCurve nurbs = section.ToNurbsCurve();
        return GetLocations(nurbs);
    }

    public static bool TryCreateSections(Curve source, IReadOnlyCollection<int> segments,
        IReadOnlyCollection<int> pointIndices, Point3d reference, double ratio, double tolerance,
        out Curve? result, out string? error)
    {
        result = null;
        error = null;
        if (segments.Any(index => index < 0 || index >= SegmentCount(source)) || segments.Count == 0)
        {
            error = "Select a polyline or polycurve segment with Ctrl+Shift.";
            return false;
        }
        if (source is PolylineCurve)
        {
            var indices = new HashSet<int>(pointIndices);
            foreach (int segment in segments) { indices.Add(segment); indices.Add(segment + 1); }
            return TryCreate(source, indices, reference, ratio, tolerance, out result, out error,
                allowReferencePoint: true);
        }

        var polycurve = (PolyCurve)source;
        var pieces = new List<Curve>();
        try
        {
            for (int index = 0; index < polycurve.SegmentCount; index++)
            {
                Curve section = polycurve.SegmentCurve(index);
                if (!segments.Contains(index)) { pieces.Add(section.DuplicateCurve()); continue; }
                using Curve supported = GetLocations(section) == null ? section.ToNurbsCurve() : section.DuplicateCurve();
                if (!TryCreate(supported, Enumerable.Range(0, GetLocations(supported)!.Length).ToArray(),
                        reference, ratio, tolerance, out Curve? edited, out error, allowReferencePoint: true))
                    return false;
                pieces.Add(edited!);
            }
            // Shared vertices belong to the selected section too. Move the adjoining endpoint,
            // retaining the rest of that neighbouring section rather than breaking the join.
            for (int index = 0; index < pieces.Count; index++)
            {
                if (segments.Contains(index)) continue;
                int before = (index + pieces.Count - 1) % pieces.Count;
                int after = (index + 1) % pieces.Count;
                if ((index > 0 || source.IsClosed) && segments.Contains(before) &&
                    !pieces[index].SetStartPoint(pieces[before].PointAtEnd))
                {
                    error = "The adjoining section cannot move its shared endpoint. Convert it to NURBS first.";
                    return false;
                }
                if ((index + 1 < pieces.Count || source.IsClosed) && segments.Contains(after) &&
                    !pieces[index].SetEndPoint(pieces[after].PointAtStart))
                {
                    error = "The adjoining section cannot move its shared endpoint. Convert it to NURBS first.";
                    return false;
                }
            }
            var candidate = new PolyCurve();
            foreach (Curve piece in pieces)
            {
                if (candidate.Append(piece)) continue;
                candidate.Dispose();
                error = "The edited sections could not be connected.";
                return false;
            }
            candidate.Domain = source.Domain;
            if (!candidate.IsValid || (source.IsClosed && !candidate.IsClosed))
            {
                candidate.Dispose();
                error = "The edit would produce an invalid or disconnected curve.";
                return false;
            }
            result = candidate;
            return true;
        }
        finally { foreach (Curve piece in pieces) piece.Dispose(); }
    }

    public static Point3d[]? GetLocations(Curve curve) => curve switch
    {
        NurbsCurve nurbs => Enumerable.Range(0, nurbs.Points.Count)
            .Select(index => nurbs.Points[index].Location).ToArray(),
        PolylineCurve polyline => Enumerable.Range(0, polyline.PointCount)
            .Select(polyline.Point).ToArray(),
        LineCurve line => new[] { line.PointAtStart, line.PointAtEnd },
        _ => null
    };

    public static bool TryCreate(Curve source, IReadOnlyCollection<int> indices, Point3d reference,
        double ratio, double tolerance, out Curve? result, out string? error, bool allowReferencePoint = false)
    {
        result = null;
        error = null;
        Point3d[]? locations = GetLocations(source);
        if (locations == null)
        {
            error = "Use control points on a NURBS curve, polyline or line; convert other curves to NURBS first.";
            return false;
        }
        if (!reference.IsValid || !double.IsFinite(ratio) || indices.Count == 0)
        {
            error = "Select control points and supply a finite reference point and slope.";
            return false;
        }

        var moved = new HashSet<int>(indices);
        if (moved.Any(index => index < 0 || index >= locations.Length))
        {
            error = "The selected control point does not match the curve's control polygon.";
            return false;
        }
        // Rhino couples duplicate control points at a periodic or closed seam. Keep the same
        // relationship in both the preview and committed geometry, including rational weights.
        if (source is NurbsCurve periodic && periodic.IsPeriodic)
        {
            int period = locations.Length - periodic.Degree;
            foreach (int index in indices)
                for (int other = index % period; other < locations.Length; other += period)
                    moved.Add(other);
        }
        else if (source.IsClosed && (moved.Contains(0) || moved.Contains(locations.Length - 1)))
        {
            moved.Add(0);
            moved.Add(locations.Length - 1);
        }

        foreach (int index in moved)
        {
            Point3d point = locations[index];
            double run = Math.Sqrt(Math.Pow(point.X - reference.X, 2) + Math.Pow(point.Y - reference.Y, 2));
            if (run <= tolerance && !allowReferencePoint)
            {
                error = "A selected control point and the reference coincide in world XY; a slope needs horizontal distance.";
                return false;
            }
            double z = GeometryCommandAlgorithms.InterpolateGradientElevation(reference, point, ratio * 1000.0);
            locations[index] = new Point3d(point.X, point.Y, z);
            if (!locations[index].IsValid)
            {
                error = "The slope produces an elevation outside the supported range.";
                return false;
            }
        }

        return TryReplaceLocations(source, moved.ToDictionary(index => index, index => locations[index]),
            out result, out error);
    }

    /// <summary>Shared detached-curve edit for slope and merge commands; retains weights and seam coupling.</summary>
    public static bool TryReplaceLocations(Curve source, IReadOnlyDictionary<int, Point3d> replacements,
        out Curve? result, out string? error)
    {
        result = null;
        error = null;
        Point3d[]? locations = GetLocations(source);
        if (locations == null || replacements.Any(pair => pair.Key < 0 || pair.Key >= locations.Length || !pair.Value.IsValid))
        {
            error = "Use valid control points on a NURBS curve, polyline or line.";
            return false;
        }
        var moved = new Dictionary<int, Point3d>(replacements);
        foreach (var pair in replacements)
        {
            IEnumerable<int> coupled = new[] { pair.Key };
            if (source is NurbsCurve periodic && periodic.IsPeriodic)
            {
                int period = locations.Length - periodic.Degree;
                coupled = Enumerable.Range(0, locations.Length).Where(index => index % period == pair.Key % period);
            }
            else if (source.IsClosed && (pair.Key == 0 || pair.Key == locations.Length - 1))
                coupled = new[] { 0, locations.Length - 1 };
            foreach (int index in coupled)
            {
                if (moved.TryGetValue(index, out Point3d target) && target != pair.Value)
                {
                    error = "Coupled seam control points cannot move to different locations.";
                    return false;
                }
                moved[index] = pair.Value;
            }
        }
        foreach (var pair in moved) locations[pair.Key] = pair.Value;
        if (source is NurbsCurve nurbs)
        {
            var edited = (NurbsCurve)nurbs.DuplicateCurve();
            foreach (int index in moved.Keys)
                edited.Points.SetPoint(index, locations[index], nurbs.Points[index].Weight);
            result = edited;
        }
        else if (source is PolylineCurve)
            result = new PolylineCurve(locations) { Domain = source.Domain };
        else
            result = new LineCurve(locations[0], locations[1]) { Domain = source.Domain };

        if (result.IsValid)
            return true;
        result.Dispose();
        result = null;
        error = "The edit would produce an invalid curve.";
        return false;
    }
}
