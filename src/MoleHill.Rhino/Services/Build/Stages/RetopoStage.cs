using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Retopo;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Retopo modifier build stage: quad-dominant extraction on the incoming mesh and the flow-cross preview overlay.
/// </summary>
internal static class RetopoStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var retopo = (RetopoModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        if (input == null)
        {
            TerrainBuildService.WarnMissingMesh(c.Build, retopo.Label);
            return;
        }

        // Quad extraction replaces the mesh (Stage 2) — go through the cached mesh stage.
        if (retopo.Quads)
        {
            c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
                c.Build,
                c.RuntimeCache,
                c.StageKey,
                "Retopo",
                TerrainBuildService.ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, retopo, c.CurrentMeshFingerprint),
                () => ApplyRetopoQuads(c.Snapshot, c.Terrain, input, retopo, c.Build, c.Mode),
                result => TerrainBuildService.DescribeModifierMeshResult(retopo.Label, result),
                out ulong fingerprint,
                c.ShouldCancel);
            c.CurrentMeshFingerprint = fingerprint;
        }

        // The flow-cross overlay reads the field on the input triangle mesh; recompute each build (preview
        // only, independent of the mesh cache) so it survives cache hits.
        if (retopo.ShowField)
            BuildRetopoFieldOverlay(c.Snapshot, c.Terrain, input, retopo, c.Build);
    }

    /// <summary>
    /// Field-guided quad retopology: replace the terrain with a quad-dominant mesh whose edges flow
    /// along the features. Runs <see cref="QuadRemesher"/> (cross-field → field-aligned isotropic
    /// remesh → tri-to-quad pairing) on the input mesh + the whole constraint stack (incl. grade-path
    /// road edges via <c>PersistentHardConstraints</c>). One connected mesh, hole-free by construction;
    /// steep retaining-wall faces pass through frozen. Falls back to the input mesh on failure.
    /// </summary>
    private static RhinoMesh ApplyRetopoQuads(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetopoModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out var faces, out _, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for retopo.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        double edgeLength = modifier.TargetEdgeLength;
        if (mode == TerrainBuildMode.Preview && edgeLength > 0)
            edgeLength *= 2.0; // coarser quads for the fast preview

        var localConstraints = TerrainBuildService.CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            toleranceProfile.CurveChordTolerance,
            preserveInputElevation: false,
            requestedEdgeLength: 0,
            maxArea: 0);
        var constraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, localConstraints);

        double effectiveEdge = edgeLength > 0 ? edgeLength : EstimateQuadSpacing(vertices);

        QuadRemesher.Result result = QuadRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new QuadRemesher.Options
            {
                EdgeLength = effectiveEdge,
                CreaseAngleDeg = modifier.CreaseAngle,
                Tolerance = toleranceProfile.RemeshConstraintTolerance,
                // Steep retaining-wall faces are frozen through the retopo (never cut out or rebuilt),
                // so walls pass through exactly and the output cannot acquire holes at wall joins.
                WallFaceMinSlopeDeg = RetopoWallFaceMinSlopeDeg,
                Iterations = mode == TerrainBuildMode.Preview ? 3 : 5
            });

        if (!result.Success || result.QuadCount == 0)
        {
            build.Diagnostics.Add(result.Warning ?? "Retopo produced no quads; kept the input mesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        if (!ValidateQuadDominantTopology(faces, result.Quads, result.Tris, out string? topologyWarning))
        {
            build.Diagnostics.Add(topologyWarning ?? "Retopo produced invalid topology; kept the input mesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        build.Diagnostics.Add(
            $"Retopo quads: {result.QuadCount:N0} quads + {result.TriangleCount:N0} triangles over " +
            $"{result.Vertices.Length / 3:N0} vertices (field-aligned remesh + pairing; features and walls pinned)." +
            (string.IsNullOrWhiteSpace(result.Warning) ? "" : $" {result.Warning}"));

        return RhinoGeometryConversions.BuildQuadDominantMesh(result.Vertices, result.Quads, result.Tris);
    }

    // Near-vertical faces (retaining walls) are frozen through the retopo at this slope.
    private const double RetopoWallFaceMinSlopeDeg = 70.0;

    /// <summary>
    /// Accepts the quad-dominant output only when its topology is no worse than the input's — imperfect
    /// upstream grading may already be non-manifold, and the retopo carries those quarantined zones
    /// through unchanged rather than repairing or worsening them.
    /// </summary>
    private static bool ValidateQuadDominantTopology(int[] inputFaces, int[] quads, int[] tris, out string? warning)
    {
        int[] triangleFaces = BuildTriangleFacesForValidation(quads, tris);
        if (triangleFaces.Length == 0)
        {
            warning = "Retopo produced no valid faces; kept the input mesh.";
            return false;
        }

        MeshTopologyValidator.BoundaryGraphAnalysis input =
            MeshTopologyValidator.AnalyzeBoundaryGraph(inputFaces, inputFaces.Length / 3);
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(triangleFaces, triangleFaces.Length / 3);
        if (topology.NonManifoldEdgeCount > input.NonManifoldEdgeCount)
        {
            warning = $"Retopo produced {topology.NonManifoldEdgeCount:N0} non-manifold edge(s) " +
                $"(input had {input.NonManifoldEdgeCount:N0}); kept the input mesh.";
            return false;
        }

        if (topology.HasOpenBoundaryChains && !input.HasOpenBoundaryChains)
        {
            warning = "Retopo produced open naked-edge chains; kept the input mesh.";
            return false;
        }

        warning = null;
        return true;
    }

    private static int[] BuildTriangleFacesForValidation(int[] quads, int[] tris)
    {
        var faces = new int[(quads.Length / 4 * 6) + tris.Length];
        int t = 0;
        for (int i = 0; i < quads.Length / 4; i++)
        {
            int a = quads[i * 4];
            int b = quads[i * 4 + 1];
            int c = quads[i * 4 + 2];
            int d = quads[i * 4 + 3];
            faces[t++] = a;
            faces[t++] = b;
            faces[t++] = c;
            faces[t++] = a;
            faces[t++] = c;
            faces[t++] = d;
        }

        Array.Copy(tris, 0, faces, t, tris.Length);
        return faces;
    }

    private static double EstimateQuadSpacing(double[] vertices)
    {
        int vertexCount = vertices.Length / 3;
        if (vertexCount == 0)
            return double.Epsilon;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double coordinateScale = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            coordinateScale = Math.Max(coordinateScale, Math.Max(Math.Abs(x), Math.Abs(y)));
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        return Math.Max(spacing, Math.Max(coordinateScale * 1e-12, double.Epsilon));
    }

    /// <summary>
    /// Field-guided quad retopology, Stage 1 (cross-field preview). Computes a 2-D cross-field aligned to
    /// features via <see cref="CrossFieldSolver"/> and emits it as a decimated <b>flow-cross overlay</b> —
    /// short perpendicular segment pairs along the two quad directions, colored by θ — so the flow can be
    /// read directly (hue alone was unreadable) and validated before quad extraction (Stages 2–3) is built.
    /// Preview only; the mesh is unchanged. Features come from the whole stack: the boundary, detected
    /// creases, the modifier's own constraint curves, AND <c>PersistentHardConstraints</c> — grade-path road
    /// edges are generated from a centerline, so they only reach us through the stack, not as drawn curves.
    /// </summary>
    private static void BuildRetopoFieldOverlay(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetopoModifierDefinition modifier,
        TerrainBuildResult build)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out var faces, out _, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for retopo field preview.");
            return;
        }

        var localConstraints = TerrainBuildService.CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            toleranceProfile.CurveChordTolerance,
            preserveInputElevation: false,
            requestedEdgeLength: 0,
            maxArea: 0);
        var constraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, localConstraints);

        CrossFieldSolver.Result field = CrossFieldSolver.Solve(
            vertices,
            faces,
            constraints,
            new CrossFieldSolver.Options
            {
                CreaseAngleDeg = modifier.CreaseAngle,
                Tolerance = toleranceProfile.RemeshConstraintTolerance
            });

        if (!field.Success)
        {
            build.Diagnostics.Add(field.Warning ?? "Retopo cross-field could not be computed.");
            return;
        }

        int vertexCount = vertices.Length / 3;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        double crossHalf = 0.5 * (modifier.TargetEdgeLength > 0 ? modifier.TargetEdgeLength : spacing);
        if (!(crossHalf > 0.0) || !double.IsFinite(crossHalf))
            crossHalf = Math.Max(spacing, toleranceProfile.RemeshConstraintTolerance) * 0.5;

        // Decimate onto a grid ~3 crosses apart so the comb reads instead of matting into a solid patch.
        double cellSize = crossHalf * 3.0;
        double invCell = 1.0 / cellSize;
        var used = new HashSet<long>();
        var guidePrimitives = new List<RuntimeOverlayPrimitive>();

        int pinnedCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            if (field.Pinned[i])
                pinnedCount++;

            double x = vertices[i * 3], y = vertices[i * 3 + 1], z = vertices[i * 3 + 2];
            long cx = (long)Math.Floor(x * invCell);
            long cy = (long)Math.Floor(y * invCell);
            if (!used.Add((cx * 73856093L) ^ (cy * 19349663L)))
                continue;

            double theta = field.Theta[i];
            var point = new Point3d(x, y, z);
            var along = new Vector3d(Math.Cos(theta), Math.Sin(theta), 0.0) * crossHalf;
            var across = new Vector3d(-Math.Sin(theta), Math.Cos(theta), 0.0) * crossHalf;
            int argb = FieldColor(theta, field.Pinned[i]).ToArgb();
            guidePrimitives.Add(RuntimeOverlayPrimitive.Polyline(new[] { point - along, point + along }, colorArgb: argb));
            guidePrimitives.Add(RuntimeOverlayPrimitive.Polyline(new[] { point - across, point + across }, colorArgb: argb));
        }

        build.RuntimeOverlays.Add(new RuntimeOverlayItem
        {
            StableId = $"retopo-field:{modifier.Id:N}",
            Owner = new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id),
            Channel = RuntimeOverlayChannel.Guide,
            Code = "retopo.field",
            Message = "Retopo Stage 1 cross-field guide.",
            ShortLabel = "Field",
            Primitives = guidePrimitives
        });

        build.Diagnostics.Add(
            $"Retopo Stage 1 field preview: {vertexCount:N0} vertices, {pinnedCount:N0} feature-pinned, " +
            $"{guidePrimitives.Count:N0} overlay segments." +
            (string.IsNullOrWhiteSpace(field.Warning) ? "" : $" {field.Warning}"));
    }

    /// <summary>Maps a cross-field angle θ∈[0,π/2) to a hue so the quad-flow direction reads as color; pins pop brighter.</summary>
    private static System.Drawing.Color FieldColor(double theta, bool pinned)
    {
        double hue = Math.Clamp(theta / (Math.PI / 2.0), 0.0, 1.0) * 360.0;
        return HsvToColor(hue, pinned ? 1.0 : 0.7, pinned ? 1.0 : 0.9);
    }

    private static System.Drawing.Color HsvToColor(double hueDegrees, double saturation, double value)
    {
        double h = (hueDegrees % 360.0) / 60.0;
        int sector = (int)Math.Floor(h);
        double f = h - sector;
        double p = value * (1.0 - saturation);
        double q = value * (1.0 - (saturation * f));
        double t = value * (1.0 - (saturation * (1.0 - f)));
        double r, g, b;
        switch (sector)
        {
            case 0: r = value; g = t; b = p; break;
            case 1: r = q; g = value; b = p; break;
            case 2: r = p; g = value; b = t; break;
            case 3: r = p; g = q; b = value; break;
            case 4: r = t; g = p; b = value; break;
            default: r = value; g = p; b = q; break;
        }

        return System.Drawing.Color.FromArgb(
            (int)Math.Round(r * 255.0),
            (int)Math.Round(g * 255.0),
            (int)Math.Round(b * 255.0));
    }
}
