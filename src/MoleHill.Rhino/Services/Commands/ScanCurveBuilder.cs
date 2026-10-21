using MoleHill.Core.Scan;
using Rhino.Geometry;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>Builds preview and output from the same rational scan pieces.</summary>
internal static class ScanCurveBuilder
{
    public static void StampEvidence(ObjectAttributes attributes, ScanCurveGeometry curve)
    {
        attributes.SetUserString("MoleHill.Scan.ObservedShare", curve.ObservedShare.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
        attributes.SetUserString("MoleHill.Scan.InferredIntervals", System.Text.Json.JsonSerializer.Serialize(curve.InferredIntervals));
    }

    /// <summary>
    /// One polycurve of the pieces; consecutive straight pieces join into one polyline segment, so a run of
    /// held lines edits with PointsOn instead of as a chain of separate line segments.
    /// </summary>
    public static Curve Build(ScanCurveGeometry geometry)
    {
        var joined = new PolyCurve();
        var run = new Polyline();
        foreach (var piece in geometry.Pieces)
        {
            if (piece.Degree == 1 && piece.Weights.All(weight => weight == 1))
            {
                // A repeated vertex makes a zero-length segment, and Rhino calls the whole curve invalid
                // (found live: one paved outline of 54 repeated a corner). Pieces meet end to start, and a
                // degenerate piece can repeat its own point.
                for (int i = 0; i < piece.ControlPointCount; i++)
                {
                    var point = new Point3d(piece.ControlPoints[i * 3], piece.ControlPoints[i * 3 + 1], piece.ControlPoints[i * 3 + 2]);
                    if (run.Count == 0 || run[run.Count - 1].DistanceTo(point) > DuplicateDistance(point))
                        run.Add(point);
                }
                continue;
            }
            Flush();
            using var curve = new NurbsCurve(3, true, piece.Degree + 1, piece.ControlPointCount);
            for (int i = 0; i < piece.ControlPointCount; i++)
                curve.Points.SetPoint(i, new Point3d(piece.ControlPoints[i * 3], piece.ControlPoints[i * 3 + 1], piece.ControlPoints[i * 3 + 2]), piece.Weights[i]);
            for (int i = 0; i < curve.Knots.Count; i++) curve.Knots[i] = piece.Knots[i + 1];
            Append(curve);
        }
        Flush();
        if (joined.SegmentCount == 1)
        {
            Curve single = joined.SegmentCurve(0).DuplicateCurve(); joined.Dispose(); return single;
        }
        return joined;

        static double DuplicateDistance(Point3d point) =>
            1e-9 * Math.Max(1.0, Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))));

        void Flush()
        {
            if (run.Count < 2) { run.Clear(); return; }
            using var polyline = new PolylineCurve(run);
            Append(polyline);
            run = new Polyline();
        }
        void Append(Curve curve)
        {
            if (!joined.Append(curve)) { joined.Dispose(); throw new InvalidOperationException("Disconnected scan alignment."); }
        }
    }
}
