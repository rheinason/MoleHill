using System.Drawing;
using MoleHill.Core.Geometry;
using MoleHill.Core.Scan;
using MoleHill.Core.Processing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Writes paved/soft area boundaries and zone fills, and measured kerbs or colour-only road breaklines.</summary>
internal static partial class ScanToTerrainCommandService
{
    private static IEnumerable<Guid> AddSurfaceOutput(RhinoDoc doc, ScanTerrainPipeline.Result pipeline,
        double? sourceBottom, Func<string, Color, string, ObjectAttributes> onLayer)
    {
        if (pipeline.Areas != null && pipeline.IncludeAreaBoundaries)
        {
            foreach (ScanSurfaceAreas.Boundary boundary in pipeline.Areas.Boundaries.Where(b => !b.IsSharedDuplicate))
            {
                bool paved = boundary.Kind == ScanSurfaceKind.Paved;
                var style = paved ? TerrainCurveExtractionStyle.PavedAreas : TerrainCurveExtractionStyle.SoftAreas;
                using Curve curve = ScanCurveBuilder.Build(boundary.Curve);
                ObjectAttributes attributes = onLayer(style.Layer, style.Colour, style.Layer + " — draped boundary");
                // Paved outlines carry the paving in the rebuild; soft outlines are drawings only.
                attributes.SetUserString("MoleHill.Scan.GeometryRole", paved ? "Classification boundary and elevation breakline" : "Classification boundary");
                yield return doc.Objects.AddCurve(curve, attributes);
            }
            foreach (Guid id in AddZoneHatches(doc, pipeline.Areas, sourceBottom, onLayer))
                yield return id;
        }
        foreach (ScanRoadEdges.EdgeLine edge in pipeline.Roads)
        {
            if (!edge.IsKerb && pipeline.IncludeAreaBoundaries && pipeline.Areas != null) continue;
            var style = edge.IsKerb ? TerrainCurveExtractionStyle.Kerbs : TerrainCurveExtractionStyle.PavingEdges;
            ObjectAttributes Attributes(string name, ScanCurveGeometry geometry)
            {
                ObjectAttributes attributes = onLayer(style.Layer, style.Colour, name);
                attributes.SetUserString("MoleHill.Scan.MeasuredStep", Format(edge.MeanStep));
                attributes.SetUserString("MoleHill.Scan.StepEvidenceShare", Format(edge.MeasuredShare));
                ScanCurveBuilder.StampEvidence(attributes, geometry);
                return attributes;
            }
            using Curve bottom = ScanCurveBuilder.Build(edge.BottomCurve);
            yield return doc.Objects.AddCurve(bottom, Attributes(edge.IsKerb ? "Kerb bottom" : "Paving edge", edge.BottomCurve));
            if (edge.TopCurve != null)
            {
                using Curve top = ScanCurveBuilder.Build(edge.TopCurve);
                yield return doc.Objects.AddCurve(top, Attributes("Kerb top", edge.TopCurve));
            }
        }
    }

    /// <summary>
    /// Fills each paved and soft area with a flat solid hatch a metre below the source, as input for
    /// MoleHill zones. Areas nest (a patio in a lawn is a hole in the lawn's hatch and a hatch of its own), but a
    /// zone reads only a hatch's outer loop, so each hatch sits a centimetre above the one it lies in: under
    /// Priority by elevation, the default, the inner area wins where the outer area's loop covers it.
    /// </summary>
    private static IEnumerable<Guid> AddZoneHatches(RhinoDoc doc, ScanSurfaceAreas.Result areas, double? sourceBottom,
        Func<string, Color, string, ObjectAttributes> onLayer)
    {
        double lowest = Math.Min(areas.Ground.Values.Where(double.IsFinite).DefaultIfEmpty(0).Min(), sourceBottom ?? double.PositiveInfinity);
        double floor = Math.Floor(lowest) - 1, tolerance = doc.ModelAbsoluteTolerance;
        int pattern = Math.Max(0, HatchPatternService.EnsurePattern(doc, HatchPatternService.SolidPatternName));
        var regions = new List<(ScanSurfaceKind Kind, Hatch Hatch, double[] Outer, BoundingBox Box)>();
        foreach (ScanSurfaceKind kind in new[] { ScanSurfaceKind.Paved, ScanSurfaceKind.Soft })
        {
            // Every loop, shared duplicates included: a hole's loop is the drawn outline of the area inside it.
            var loops = new List<Curve>();
            foreach (ScanSurfaceAreas.Boundary boundary in areas.Boundaries.Where(b => b.Kind == kind))
            {
                var points = new List<Point3d>(boundary.Points.Length / 3 + 1);
                for (int i = 0; i < boundary.Points.Length; i += 3)
                    points.Add(new Point3d(boundary.Points[i], boundary.Points[i + 1], floor));
                if (points.Count < 3) continue;
                if (points[0].DistanceTo(points[^1]) > tolerance) points.Add(points[0]);
                loops.Add(new PolylineCurve(points));
            }
            if (loops.Count == 0) continue;
            // Hatch.Create nests the loops itself: a hole becomes an island, an area inside a hole its own hatch.
            Hatch[]? hatches = Hatch.Create(loops, pattern, 0, 1, tolerance);
            foreach (Curve loop in loops) loop.Dispose();
            foreach (Hatch hatch in hatches ?? Array.Empty<Hatch>())
            {
                if (hatch == null) continue;
                Curve? outer = hatch.Get3dCurves(true).FirstOrDefault();
                if (outer == null || !outer.TryGetPolyline(out Polyline polyline)) continue;
                var xy = new double[polyline.Count * 2];
                for (int i = 0; i < polyline.Count; i++) { xy[i * 2] = polyline[i].X; xy[i * 2 + 1] = polyline[i].Y; }
                regions.Add((kind, hatch, xy, polyline.BoundingBox));
            }
        }
        foreach (var region in regions)
        {
            int depth = regions.Count(other => !ReferenceEquals(other.Hatch, region.Hatch) && Inside(region, other));
            region.Hatch.Translate(0, 0, depth * .01);
            var style = region.Kind == ScanSurfaceKind.Paved ? TerrainCurveExtractionStyle.PavedZones : TerrainCurveExtractionStyle.SoftZones;
            ObjectAttributes attributes = onLayer(style.Layer, style.Colour, style.Layer + " — area fill");
            attributes.SetUserString("MoleHill.Scan.GeometryRole", "Classification zone");
            yield return doc.Objects.AddHatch(region.Hatch, attributes);
            region.Hatch.Dispose();
        }

        // Areas partition the ground, so one area's loop reaches strictly inside another's outer loop only when it
        // lies in a hole of that area. Shared boundary vertices decide nothing and are skipped.
        bool Inside((ScanSurfaceKind Kind, Hatch Hatch, double[] Outer, BoundingBox Box) inner,
            (ScanSurfaceKind Kind, Hatch Hatch, double[] Outer, BoundingBox Box) outer)
        {
            if (inner.Box.Min.X < outer.Box.Min.X || inner.Box.Min.Y < outer.Box.Min.Y ||
                inner.Box.Max.X > outer.Box.Max.X || inner.Box.Max.Y > outer.Box.Max.Y)
                return false;
            int count = outer.Outer.Length / 2;
            for (int i = 0; i < inner.Outer.Length; i += 2)
            {
                double x = inner.Outer[i], y = inner.Outer[i + 1];
                if (Geometry2D.DistanceToPolygon(x, y, outer.Outer, count) > tolerance * 10)
                    return Geometry2D.PointInPolygon(x, y, outer.Outer, count);
            }
            return false;
        }
    }
}