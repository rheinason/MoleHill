using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using MoleHill.Core.Geometry;
using MoleHill.Shared;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    // A disabled card must not keep trimming the terrain or clipping Add Geometry inputs — the card is
    // off, so its boundaries are too.
    private static TriangulateModifierDefinition? GetBoundaryOwner(TerrainDefinition terrain) =>
        terrain.Modifiers.OfType<TriangulateModifierDefinition>().FirstOrDefault(triangulate => triangulate.IsEnabled);

    private static List<MeshAreaSplitter.AreaBoundary> ResolveBoundaryAreas(
        TerrainBuildSnapshot snapshot,
        SourceReferenceSet sourceSet,
        double tolerance,
        string role,
        TerrainBuildResult build)
    {
        var result = new List<MeshAreaSplitter.AreaBoundary>();
        int invalid = 0;
        foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, sourceSet))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out Polyline polyline))
            {
                invalid++;
                continue;
            }

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceToSquared(polyline[count - 1]) <= tolerance * tolerance)
                count--;
            if (count < 3)
            {
                invalid++;
                continue;
            }

            var xy = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
            }

            if (Math.Abs(Geometry2D.SignedArea(xy, count)) <= tolerance * tolerance || HasSelfIntersection(xy, count))
            {
                invalid++;
                continue;
            }

            result.Add(new MeshAreaSplitter.AreaBoundary(xy, count));
        }

        if (invalid > 0)
            build.Diagnostics.Add($"{role} ignored {invalid:N0} invalid boundary curve(s); boundaries must be simple closed World-XY loops.");
        return result;
    }

    private static MeshAreaSplitter.AreaBoundary? SelectOuterBoundary(
        List<MeshAreaSplitter.AreaBoundary> boundaries,
        TerrainBuildResult build)
    {
        if (boundaries.Count == 0)
            return null;
        MeshAreaSplitter.AreaBoundary selected = boundaries
            .OrderByDescending(boundary => Math.Abs(Geometry2D.SignedArea(boundary.XyVertices, boundary.VertexCount)))
            .First();
        if (boundaries.Count > 1)
            build.Diagnostics.Add($"Outer resolved {boundaries.Count:N0} valid loops; only the largest loop is used.");
        return selected;
    }

    private static (List<Point3d> Points, List<Curve> Breaklines, List<Curve> Contours) FilterInputsToDataClip(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        List<Point3d> points,
        List<Curve> breaklines,
        List<Curve> contours,
        double tolerance,
        TerrainBuildResult build,
        Func<bool>? shouldCancel = null)
    {
        TriangulateModifierDefinition? owner = GetBoundaryOwner(terrain);
        if (owner == null || !owner.DataClipBoundaries.HasReferences)
            return (points, breaklines, contours);

        List<MeshAreaSplitter.AreaBoundary> clips = ResolveBoundaryAreas(
            snapshot, owner.DataClipBoundaries, tolerance, "Data Clip", build);
        if (clips.Count == 0)
            return (points, breaklines, contours);

        var xyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            xyz[i * 3] = points[i].X;
            xyz[(i * 3) + 1] = points[i].Y;
            xyz[(i * 3) + 2] = points[i].Z;
        }
        bool[] keep = RegionInputClipper.KeepPointsInside(xyz, points.Count, clips, tolerance);
        var keptPoints = new List<Point3d>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i])
                keptPoints.Add(points[i]);

        List<Curve> ClipCurves(List<Curve> curves)
        {
            List<TerrainTriangulationInputBuilder.FlattenedPolyline> flattened =
                TerrainTriangulationInputBuilder.CreateFlattenedPolylines(curves, tolerance);
            List<RegionInputClipper.InputPolyline> clipped = RegionInputClipper.ClipPolylines(
                flattened.Select(polyline => new RegionInputClipper.InputPolyline(
                    polyline.Points, polyline.Points.Length / 3, polyline.IsClosed)).ToList(),
                clips,
                tolerance,
                shouldCancel);
            var output = new List<Curve>(clipped.Count);
            foreach (RegionInputClipper.InputPolyline polyline in clipped)
            {
                var points3d = new Point3d[polyline.PointCount];
                for (int i = 0; i < polyline.PointCount; i++)
                    points3d[i] = new Point3d(polyline.Points[i * 3], polyline.Points[(i * 3) + 1], polyline.Points[(i * 3) + 2]);
                output.Add(new PolylineCurve(points3d));
            }
            return output;
        }

        List<Curve> keptBreaklines = ClipCurves(breaklines);
        List<Curve> keptContours = ClipCurves(contours);
        build.Diagnostics.Add(
            $"Data Clip kept {keptPoints.Count:N0}/{points.Count:N0} points and produced " +
            $"{keptBreaklines.Count:N0}/{breaklines.Count:N0} breakline and {keptContours.Count:N0}/{contours.Count:N0} contour parts.");
        return (keptPoints, keptBreaklines, keptContours);
    }

    private static RhinoMesh? ApplyTerrainBoundaryRoles(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel = null)
    {
        TriangulateModifierDefinition? owner = GetBoundaryOwner(terrain);
        if (owner == null || (!owner.OuterBoundaries.HasReferences && !owner.HideBoundaries.HasReferences && !owner.ShowBoundaries.HasReferences))
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out double[] vertices, out _, out int[] faces, out _, out string? extractionError))
        {
            build.Diagnostics.Add(extractionError ?? "Could not extract terrain mesh for boundary trimming.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        double tolerance = GetToleranceProfile(snapshot, terrain).CurveChordTolerance;
        MeshAreaSplitter.AreaBoundary? outer = SelectOuterBoundary(
            ResolveBoundaryAreas(snapshot, owner.OuterBoundaries, tolerance, "Outer", build), build);
        List<MeshAreaSplitter.AreaBoundary> hides = ResolveBoundaryAreas(snapshot, owner.HideBoundaries, tolerance, "Hide", build);
        List<MeshAreaSplitter.AreaBoundary> shows = ResolveBoundaryAreas(snapshot, owner.ShowBoundaries, tolerance, "Show", build);

        TerrainBoundaryTrimmer.Result? result = TerrainBoundaryTrimmer.Trim(
            vertices, vertices.Length / 3, faces, faces.Length / 3,
            outer, hides, shows, tolerance, out string? error, shouldCancel);
        if (result == null)
        {
            build.Diagnostics.Add(error ?? "Terrain boundary trim failed.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }
        if (shows.Count > 0 && !result.ShowRestoredAnyFace)
            build.Diagnostics.Add("Show boundaries did not restore any hidden terrain area.");
        if (result.FaceCount == 0)
        {
            build.Diagnostics.Add("Terrain boundary roles removed the entire surface.");
            return null;
        }

        return RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount);
    }

    private static bool HasSelfIntersection(double[] xy, int count)
    {
        for (int a = 0; a < count; a++)
        {
            int aNext = (a + 1) % count;
            for (int b = a + 1; b < count; b++)
            {
                int bNext = (b + 1) % count;
                if (a == b || aNext == b || bNext == a)
                    continue;
                if (Geometry2D.SegmentsTouch(
                    xy[a * 2], xy[(a * 2) + 1], xy[aNext * 2], xy[(aNext * 2) + 1],
                    xy[b * 2], xy[(b * 2) + 1], xy[bNext * 2], xy[(bNext * 2) + 1]))
                    return true;
            }
        }
        return false;
    }
}
