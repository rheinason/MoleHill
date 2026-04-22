using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Shared;

internal sealed class InSituStairReference
{
    public InSituStairReference(
        SurfaceStripGrader.SurfaceDefinition supportSurface,
        IReadOnlyList<Brep> stairBreps,
        double treadDepth,
        int stepCount,
        Point3d treadDepthLabelPoint)
    {
        SupportSurface = supportSurface;
        StairBreps = stairBreps;
        TreadDepth = treadDepth;
        StepCount = stepCount;
        TreadDepthLabelPoint = treadDepthLabelPoint;
    }

    public SurfaceStripGrader.SurfaceDefinition SupportSurface { get; }

    public IReadOnlyList<Brep> StairBreps { get; }

    public double TreadDepth { get; }

    public int StepCount { get; }

    public Point3d TreadDepthLabelPoint { get; }
}

internal sealed class InSituStairBuildResult
{
    public InSituStairBuildResult(IReadOnlyList<InSituStairReference> references, IReadOnlyList<string> warnings)
    {
        References = references;
        Warnings = warnings;
        SurfaceCount = references.Count;
        TreadDepthSummary = FormatTreadDepthSummary(references);
        StepCountSummary = FormatStepCountSummary(references);
        StatusSummary = FormatStatusSummary(SurfaceCount, TreadDepthSummary, StepCountSummary);
    }

    public IReadOnlyList<InSituStairReference> References { get; }

    public IReadOnlyList<string> Warnings { get; }

    public int SurfaceCount { get; }

    public string TreadDepthSummary { get; }

    public string StepCountSummary { get; }

    public string StatusSummary { get; }

    private static string FormatTreadDepthSummary(IReadOnlyList<InSituStairReference> references)
    {
        if (references.Count == 0)
            return string.Empty;

        double min = references.Min(reference => reference.TreadDepth);
        double max = references.Max(reference => reference.TreadDepth);
        if (Math.Abs(max - min) <= 1e-6)
            return $"{min:G4}";

        return $"{min:G4} - {max:G4}";
    }

    private static string FormatStepCountSummary(IReadOnlyList<InSituStairReference> references)
    {
        if (references.Count == 0)
            return string.Empty;

        if (references.Count == 1)
            return references[0].StepCount.ToString();

        int total = references.Sum(reference => reference.StepCount);
        return $"{total} total";
    }

    private static string FormatStatusSummary(int surfaceCount, string treadDepthSummary, string stepCountSummary)
    {
        string surfaceText = surfaceCount == 1 ? "1 surface" : $"{surfaceCount} surfaces";
        return $"In-Situ Stair: {surfaceText}, tread {treadDepthSummary}, {stepCountSummary} steps.";
    }
}

internal static class InSituStairReferenceBuilder
{
    private sealed class Section
    {
        public required Point3d LeftBase { get; init; }

        public required Point3d RightBase { get; init; }

        public required Point3d LeftTop { get; init; }

        public required Point3d RightTop { get; init; }
    }

    private sealed class RunDirectionInterpretation
    {
        public required Vector3d RunDir { get; init; }

        public required Vector3d WidthDir { get; init; }

        public required List<(double s, double t)> LocalBoundary { get; init; }

        public required double MinS { get; init; }

        public required double MaxS { get; init; }

        public required double StartMinT { get; init; }

        public required double StartMaxT { get; init; }

        public required double EndMinT { get; init; }

        public required double EndMaxT { get; init; }

        public required double StartZ { get; init; }

        public required double EndZ { get; init; }

        public required double TotalRise { get; init; }

        public required double TotalRun { get; init; }

        public required double TreadDepth { get; init; }
    }

    public static bool TryBuild(
        IEnumerable<Mesh> sourceMeshes,
        double riserHeight,
        double slopeAngleDeg,
        double maxDistance,
        out InSituStairBuildResult? buildResult,
        out string? errorMessage)
    {
        buildResult = null;
        errorMessage = null;

        if (riserHeight <= 0)
        {
            errorMessage = "Riser height must be positive.";
            return false;
        }

        Mesh? merged = MergeMeshes(sourceMeshes);
        if (merged == null || merged.Faces.Count == 0)
        {
            errorMessage = "No valid reference surface meshes were found.";
            return false;
        }

        double interpretationTolerance = ComputeInterpretationTolerance(merged, riserHeight);

        if (!TryExtractWalkableSkins(merged, out var topSkins, out errorMessage))
            return false;

        var references = new List<InSituStairReference>(topSkins.Count);
        var warnings = new List<string>();
        for (int skinIndex = 0; skinIndex < topSkins.Count; skinIndex++)
        {
            if (!TryBuildSingle(
                    topSkins[skinIndex],
                    riserHeight,
                    slopeAngleDeg,
                    maxDistance,
                    interpretationTolerance,
                    out var reference,
                    out string? warning,
                    out string? surfaceError))
            {
                warnings.Add($"Surface {skinIndex + 1}: {surfaceError ?? "could not be interpreted as a stair."}");
                continue;
            }

            references.Add(reference!);
            if (!string.IsNullOrWhiteSpace(warning))
                warnings.Add($"Surface {skinIndex + 1}: {warning}");
        }

        if (references.Count == 0)
        {
            errorMessage = warnings.Count > 0
                ? string.Join(Environment.NewLine, warnings)
                : "No usable stair surfaces were found.";
            return false;
        }

        buildResult = new InSituStairBuildResult(references, warnings);
        return true;
    }

    private static double ComputeInterpretationTolerance(Mesh mesh, double riserHeight)
    {
        var bounds = mesh.GetBoundingBox(true);
        double scale = Math.Abs(riserHeight);
        if (bounds.IsValid)
            scale = Math.Max(scale, bounds.Diagonal.Length);

        if (scale <= 1e-9)
            scale = 1.0;

        return Math.Clamp(scale * 1e-6, 1e-6, 1e-3);
    }

    private static bool TryBuildSingle(
        Mesh topSkin,
        double riserHeight,
        double slopeAngleDeg,
        double maxDistance,
        double tolerance,
        out InSituStairReference? reference,
        out string? warning,
        out string? errorMessage)
    {
        reference = null;
        warning = null;
        errorMessage = null;

        if (!TryExtractBoundary(topSkin, tolerance, out var boundary, out errorMessage))
            return false;

        var fit = Plane.FitPlaneToPoints(topSkin.Vertices.ToPoint3dArray(), out Plane plane);
        if (fit == PlaneFitResult.Failure || Math.Abs(plane.Normal.Z) < 1e-6)
        {
            errorMessage = "Could not fit a stable stair plane to the reference surface.";
            return false;
        }

        if (plane.Normal.Z < 0)
            plane.Flip();

        var anchor = new Point3d(boundary[0].X, boundary[0].Y, 0.0);
        Vector3d gradientRunDir = new(-plane.Normal.X, -plane.Normal.Y, 0.0);
        if (!gradientRunDir.Unitize())
        {
            errorMessage = "The reference surface is too flat to derive a stair run direction.";
            return false;
        }

        bool hasForward = TryInterpretRunDirection(
            boundary,
            anchor,
            plane,
            gradientRunDir,
            tolerance,
            riserHeight,
            out RunDirectionInterpretation? forwardInterpretation,
            out string? forwardError);
        bool hasReverse = TryInterpretRunDirection(
            boundary,
            anchor,
            plane,
            -gradientRunDir,
            tolerance,
            riserHeight,
            out RunDirectionInterpretation? reverseInterpretation,
            out string? reverseError);

        if (!hasForward && !hasReverse)
        {
            errorMessage = forwardError ?? reverseError ?? "The reference surface could not be interpreted as a stair strip.";
            return false;
        }

        var interpretation = hasForward && hasReverse
            ? ChoosePreferredInterpretation(forwardInterpretation!, reverseInterpretation!, tolerance)
            : (forwardInterpretation ?? reverseInterpretation)!;

        Vector3d runDir = interpretation.RunDir;
        Vector3d widthDir = interpretation.WidthDir;
        var localBoundary = interpretation.LocalBoundary;
        double minS = interpretation.MinS;
        double maxS = interpretation.MaxS;
        double startMinT = interpretation.StartMinT;
        double startMaxT = interpretation.StartMaxT;
        double endMinT = interpretation.EndMinT;
        double endMaxT = interpretation.EndMaxT;
        double startZ = interpretation.StartZ;
        double endZ = interpretation.EndZ;
        double totalRise = interpretation.TotalRise;
        double treadDepth = interpretation.TreadDepth;

        double rmsResidual = ComputePlaneResidual(topSkin, plane);
        if (rmsResidual > Math.Max(riserHeight * 0.5, tolerance * 20))
        {
            errorMessage = "The reference surface is too warped for the in-situ stair modifier. Use a planar or ruled surface.";
            return false;
        }

        int stepCount = Math.Max(1, (int)Math.Ceiling(totalRise / riserHeight - 1e-9));
        double topResidual = stepCount * riserHeight - totalRise;
        if (topResidual > tolerance * 5)
            warning = $"Top step rise was shortened by {topResidual:G4} units to finish at the supplied top edge.";

        var stairBreps = new List<Brep>(stepCount);
        Point3d labelPoint = Point3d.Unset;
        for (int stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            double s0 = minS + stepIndex * treadDepth;
            double s1 = Math.Min(minS + (stepIndex + 1) * treadDepth, maxS);
            if (s1 - s0 <= tolerance)
                continue;

            double topZ = stepIndex == stepCount - 1
                ? endZ
                : Math.Min(startZ + (stepIndex + 1) * riserHeight, endZ);

            if (!TryBuildSection(localBoundary, anchor, runDir, widthDir, plane, s0, topZ, tolerance, out Section startSection) ||
                !TryBuildSection(localBoundary, anchor, runDir, widthDir, plane, s1, topZ, tolerance, out Section endSection))
            {
                errorMessage = "The reference surface footprint could not be sliced into stair geometry.";
                return false;
            }

            if (!TryBuildStepBrep(startSection, endSection, tolerance, out Brep? stairStep, out Point3d stepLabelPoint))
            {
                errorMessage = "A stair step could not be converted into a Brep.";
                return false;
            }

            stairBreps.Add(stairStep!);
            if (!labelPoint.IsValid)
                labelPoint = stepLabelPoint;
        }

        if (stairBreps.Count == 0)
        {
            errorMessage = "No usable stair steps could be generated.";
            return false;
        }

        double[] footprintXy = new double[boundary.Count * 2];
        double[] boundaryVertices = new double[boundary.Count * 3];
        for (int i = 0; i < boundary.Count; i++)
        {
            footprintXy[i * 2] = boundary[i].X;
            footprintXy[i * 2 + 1] = boundary[i].Y;
            boundaryVertices[i * 3] = boundary[i].X;
            boundaryVertices[i * 3 + 1] = boundary[i].Y;
            boundaryVertices[i * 3 + 2] = EvaluatePlaneZ(plane, boundary[i]);
        }

        double d = -(plane.Normal.X * plane.OriginX + plane.Normal.Y * plane.OriginY + plane.Normal.Z * plane.OriginZ);
        double planeXCoeff = -plane.Normal.X / plane.Normal.Z;
        double planeYCoeff = -plane.Normal.Y / plane.Normal.Z;
        double planeConstant = -d / plane.Normal.Z;
        if (!labelPoint.IsValid)
            labelPoint = AveragePoint(boundary);

        reference = new InSituStairReference(
            new SurfaceStripGrader.SurfaceDefinition(
                footprintXy,
                boundary.Count,
                boundaryVertices,
                boundary.Count,
                planeXCoeff,
                planeYCoeff,
                planeConstant,
                slopeAngleDeg,
                maxDistance),
            stairBreps,
            treadDepth,
            stepCount,
            labelPoint);
        return true;
    }

    private static bool TryInterpretRunDirection(
        IReadOnlyList<Point3d> boundary,
        Point3d anchor,
        Plane plane,
        Vector3d candidateRunDir,
        double tolerance,
        double riserHeight,
        out RunDirectionInterpretation? interpretation,
        out string? errorMessage)
    {
        interpretation = null;
        errorMessage = null;

        Vector3d runDir = candidateRunDir;
        if (!runDir.Unitize())
        {
            errorMessage = "The reference surface is too flat to derive a stair run direction.";
            return false;
        }

        Vector3d widthDir = new(-runDir.Y, runDir.X, 0.0);
        if (!widthDir.Unitize())
        {
            errorMessage = "The reference surface is too flat to derive a stair run width.";
            return false;
        }

        var localBoundary = ToLocalBoundary(boundary, anchor, runDir, widthDir);
        GetRange(localBoundary, out double minS, out double maxS);
        if (maxS - minS <= tolerance)
        {
            errorMessage = "The reference surface has no usable horizontal run.";
            return false;
        }

        if (!TryGetInterval(localBoundary, minS + tolerance, out double startMinT, out double startMaxT) ||
            !TryGetInterval(localBoundary, maxS - tolerance, out double endMinT, out double endMaxT))
        {
            errorMessage = "The reference surface footprint is not a single stair strip.";
            return false;
        }

        double startZ = EvaluatePlaneZ(plane, LocalToWorld(anchor, runDir, widthDir, minS, (startMinT + startMaxT) * 0.5));
        double endZ = EvaluatePlaneZ(plane, LocalToWorld(anchor, runDir, widthDir, maxS, (endMinT + endMaxT) * 0.5));
        double totalRise = endZ - startZ;
        if (totalRise <= tolerance)
        {
            errorMessage = "The reference surface is too flat to create stairs.";
            return false;
        }

        double totalRun = maxS - minS;
        if (totalRun <= tolerance)
        {
            errorMessage = "The reference surface has no usable horizontal run.";
            return false;
        }

        double grade = totalRise / totalRun;
        double treadDepth = riserHeight / grade;
        if (double.IsNaN(treadDepth) || double.IsInfinity(treadDepth) || treadDepth <= tolerance * 4)
        {
            errorMessage = "The implied tread depth is too small for stable stair geometry.";
            return false;
        }

        interpretation = new RunDirectionInterpretation
        {
            RunDir = runDir,
            WidthDir = widthDir,
            LocalBoundary = localBoundary,
            MinS = minS,
            MaxS = maxS,
            StartMinT = startMinT,
            StartMaxT = startMaxT,
            EndMinT = endMinT,
            EndMaxT = endMaxT,
            StartZ = startZ,
            EndZ = endZ,
            TotalRise = totalRise,
            TotalRun = totalRun,
            TreadDepth = treadDepth
        };
        return true;
    }

    private static RunDirectionInterpretation ChoosePreferredInterpretation(
        RunDirectionInterpretation forward,
        RunDirectionInterpretation reverse,
        double tolerance)
    {
        if (Math.Abs(forward.TotalRise - reverse.TotalRise) > tolerance)
            return forward.TotalRise > reverse.TotalRise ? forward : reverse;

        if (Math.Abs(forward.TotalRun - reverse.TotalRun) > tolerance)
            return forward.TotalRun > reverse.TotalRun ? forward : reverse;

        return forward;
    }

    private static Mesh? MergeMeshes(IEnumerable<Mesh> sourceMeshes)
    {
        Mesh? merged = null;
        foreach (var mesh in sourceMeshes)
        {
            if (mesh == null || mesh.Faces.Count == 0)
                continue;

            var copy = mesh.DuplicateMesh();
            copy.Faces.ConvertQuadsToTriangles();
            if (merged == null)
                merged = copy;
            else
                merged.Append(copy);
        }

        if (merged == null)
            return null;

        merged.Vertices.CombineIdentical(true, true);
        merged.Vertices.CullUnused();
        merged.FaceNormals.ComputeFaceNormals();
        merged.Normals.ComputeNormals();
        merged.Compact();
        return merged;
    }

    private static bool TryExtractWalkableSkins(
        Mesh mesh,
        out List<Mesh> topSkins,
        out string? errorMessage)
    {
        topSkins = new List<Mesh>();
        errorMessage = null;

        mesh.FaceNormals.ComputeFaceNormals();

        // If the surface was supplied with downward-facing normals, flip it so the
        // upward-face filter below finds walkable geometry regardless of input orientation.
        double normalZSum = 0.0;
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
            normalZSum += mesh.FaceNormals[fi].Z;
        if (normalZSum < 0)
        {
            mesh.Flip(true, true, true);
            mesh.FaceNormals.ComputeFaceNormals();
        }

        var upwardMesh = new Mesh();
        var vertexMap = new Dictionary<int, int>();
        for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        {
            var normal = mesh.FaceNormals[faceIndex];
            if (normal.Z <= 0.2)
                continue;

            var face = mesh.Faces[faceIndex];
            int a = CopyVertex(mesh, upwardMesh, vertexMap, face.A);
            int b = CopyVertex(mesh, upwardMesh, vertexMap, face.B);
            int c = CopyVertex(mesh, upwardMesh, vertexMap, face.C);
            upwardMesh.Faces.AddFace(a, b, c);
        }

        upwardMesh.Vertices.CombineIdentical(true, true);
        upwardMesh.Vertices.CullUnused();
        upwardMesh.FaceNormals.ComputeFaceNormals();
        upwardMesh.Normals.ComputeNormals();
        upwardMesh.Compact();

        if (upwardMesh.Faces.Count == 0)
        {
            errorMessage = "No upward-facing walkable surface was found.";
            return false;
        }

        var pieces = upwardMesh.SplitDisjointPieces();
        if (pieces == null || pieces.Length == 0)
        {
            errorMessage = "The walkable surface could not be extracted.";
            return false;
        }

        topSkins = pieces
            .Where(piece => piece != null && piece.Faces.Count > 0)
            .OrderByDescending(piece => AreaMassProperties.Compute(piece)?.Area ?? 0.0)
            .ToList();
        if (topSkins.Count == 0)
        {
            errorMessage = "No upward-facing walkable surface was found.";
            return false;
        }

        return true;
    }

    private static bool TryExtractBoundary(
        Mesh topSkin,
        double tolerance,
        out List<Point3d> boundary,
        out string? errorMessage)
    {
        boundary = new List<Point3d>();
        errorMessage = null;

        var nakedEdges = topSkin.GetNakedEdges();
        if (nakedEdges == null || nakedEdges.Length == 0)
        {
            errorMessage = "The walkable surface does not expose a usable perimeter.";
            return false;
        }

        var selectedLoop = nakedEdges
            .Where(polyline => polyline.Count >= 4)
            .OrderByDescending(polyline => Math.Abs(AreaMassProperties.Compute(new PolylineCurve(polyline))?.Area ?? 0.0))
            .FirstOrDefault();

        if (selectedLoop == null || selectedLoop.Count < 4)
        {
            errorMessage = "The walkable surface perimeter could not be extracted.";
            return false;
        }

        boundary = selectedLoop.ToList();
        if (boundary[0].DistanceTo(boundary[^1]) <= tolerance)
            boundary.RemoveAt(boundary.Count - 1);

        if (boundary.Count < 3)
        {
            errorMessage = "The walkable surface perimeter is degenerate.";
            return false;
        }

        return true;
    }

    private static int CopyVertex(Mesh source, Mesh target, Dictionary<int, int> vertexMap, int sourceIndex)
    {
        if (vertexMap.TryGetValue(sourceIndex, out int existing))
            return existing;

        var pt = source.Vertices[sourceIndex];
        int targetIndex = target.Vertices.Add(pt.X, pt.Y, pt.Z);
        vertexMap[sourceIndex] = targetIndex;
        return targetIndex;
    }

    private static List<(double s, double t)> ToLocalBoundary(
        IReadOnlyList<Point3d> boundary,
        Point3d anchor,
        Vector3d runDir,
        Vector3d widthDir)
    {
        var local = new List<(double s, double t)>(boundary.Count);
        for (int i = 0; i < boundary.Count; i++)
        {
            Vector3d offset = new(boundary[i].X - anchor.X, boundary[i].Y - anchor.Y, 0.0);
            local.Add((offset * runDir, offset * widthDir));
        }

        return local;
    }

    private static void GetRange(IReadOnlyList<(double s, double t)> localBoundary, out double minS, out double maxS)
    {
        minS = double.MaxValue;
        maxS = double.MinValue;
        for (int i = 0; i < localBoundary.Count; i++)
        {
            minS = Math.Min(minS, localBoundary[i].s);
            maxS = Math.Max(maxS, localBoundary[i].s);
        }
    }

    private static bool TryGetInterval(
        IReadOnlyList<(double s, double t)> localBoundary,
        double sampleS,
        out double minT,
        out double maxT)
    {
        minT = 0;
        maxT = 0;
        const double tolerance = 1e-6;
        var hits = new List<double>();

        for (int i = 0; i < localBoundary.Count; i++)
        {
            var a = localBoundary[i];
            var b = localBoundary[(i + 1) % localBoundary.Count];
            double minS = Math.Min(a.s, b.s);
            double maxS = Math.Max(a.s, b.s);

            if (sampleS < minS - tolerance || sampleS > maxS + tolerance)
                continue;

            if (Math.Abs(a.s - b.s) <= tolerance)
            {
                if (Math.Abs(sampleS - a.s) > tolerance)
                    continue;

                hits.Add(a.t);
                hits.Add(b.t);
                continue;
            }

            double ratio = (sampleS - a.s) / (b.s - a.s);
            if (ratio < -tolerance || ratio > 1.0 + tolerance)
                continue;

            hits.Add(a.t + (b.t - a.t) * ratio);
        }

        if (hits.Count < 2)
            return false;

        hits.Sort();
        minT = hits[0];
        maxT = hits[^1];
        return maxT - minT > tolerance;
    }

    private static bool TryBuildSection(
        IReadOnlyList<(double s, double t)> localBoundary,
        Point3d anchor,
        Vector3d runDir,
        Vector3d widthDir,
        Plane plane,
        double sampleS,
        double topZ,
        double tolerance,
        out Section section)
    {
        section = null!;
        if (!TryGetInterval(
                localBoundary,
                Math.Clamp(sampleS, GetMinS(localBoundary) + tolerance, GetMaxS(localBoundary) - tolerance),
                out double minT,
                out double maxT))
        {
            return false;
        }

        Point3d leftWorld = LocalToWorld(anchor, runDir, widthDir, sampleS, minT);
        Point3d rightWorld = LocalToWorld(anchor, runDir, widthDir, sampleS, maxT);
        double leftBaseZ = EvaluatePlaneZ(plane, leftWorld);
        double rightBaseZ = EvaluatePlaneZ(plane, rightWorld);

        section = new Section
        {
            LeftBase = new Point3d(leftWorld.X, leftWorld.Y, leftBaseZ),
            RightBase = new Point3d(rightWorld.X, rightWorld.Y, rightBaseZ),
            LeftTop = new Point3d(leftWorld.X, leftWorld.Y, topZ),
            RightTop = new Point3d(rightWorld.X, rightWorld.Y, topZ)
        };
        return true;
    }

    private static bool TryBuildStepBrep(
        Section startSection,
        Section endSection,
        double tolerance,
        out Brep? stepBrep,
        out Point3d labelPoint)
    {
        stepBrep = null;
        labelPoint = new Point3d(
            (startSection.LeftTop.X + startSection.RightTop.X + endSection.LeftTop.X + endSection.RightTop.X) * 0.25,
            (startSection.LeftTop.Y + startSection.RightTop.Y + endSection.LeftTop.Y + endSection.RightTop.Y) * 0.25,
            (startSection.LeftTop.Z + startSection.RightTop.Z + endSection.LeftTop.Z + endSection.RightTop.Z) * 0.25);

        double minBaseZ = Math.Min(
            Math.Min(startSection.LeftBase.Z, startSection.RightBase.Z),
            Math.Min(endSection.LeftBase.Z, endSection.RightBase.Z));
        double depth = startSection.LeftTop.Z - minBaseZ;
        if (depth <= tolerance)
            return false;

        var treadLoop = new Polyline(
            new[]
            {
                startSection.LeftTop,
                startSection.RightTop,
                endSection.RightTop,
                endSection.LeftTop,
                startSection.LeftTop
            });
        var treadCurve = new PolylineCurve(treadLoop);
        if (treadCurve.TryGetPlane(out Plane treadPlane) && treadPlane.Normal.Z < 0)
            treadCurve.Reverse();

        var extrusion = Extrusion.Create(treadCurve, -depth, true);
        stepBrep = extrusion?.ToBrep();
        return stepBrep != null;
    }

    private static double GetMinS(IReadOnlyList<(double s, double t)> localBoundary)
    {
        double min = double.MaxValue;
        for (int i = 0; i < localBoundary.Count; i++)
            min = Math.Min(min, localBoundary[i].s);
        return min;
    }

    private static double GetMaxS(IReadOnlyList<(double s, double t)> localBoundary)
    {
        double max = double.MinValue;
        for (int i = 0; i < localBoundary.Count; i++)
            max = Math.Max(max, localBoundary[i].s);
        return max;
    }

    private static Point3d LocalToWorld(Point3d anchor, Vector3d runDir, Vector3d widthDir, double s, double t)
    {
        return new Point3d(
            anchor.X + runDir.X * s + widthDir.X * t,
            anchor.Y + runDir.Y * s + widthDir.Y * t,
            0.0);
    }

    private static double EvaluatePlaneZ(Plane plane, Point3d point)
    {
        double d = -(plane.Normal.X * plane.OriginX + plane.Normal.Y * plane.OriginY + plane.Normal.Z * plane.OriginZ);
        return -(plane.Normal.X * point.X + plane.Normal.Y * point.Y + d) / plane.Normal.Z;
    }

    private static double ComputePlaneResidual(Mesh mesh, Plane plane)
    {
        if (mesh.Vertices.Count == 0)
            return 0.0;

        double sumSq = 0.0;
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var pt = mesh.Vertices.Point3dAt(i);
            double dz = pt.Z - EvaluatePlaneZ(plane, pt);
            sumSq += dz * dz;
        }

        return Math.Sqrt(sumSq / mesh.Vertices.Count);
    }

    private static Point3d AveragePoint(IReadOnlyList<Point3d> points)
    {
        if (points.Count == 0)
            return Point3d.Origin;

        double x = 0;
        double y = 0;
        double z = 0;
        for (int i = 0; i < points.Count; i++)
        {
            x += points[i].X;
            y += points[i].Y;
            z += points[i].Z;
        }

        double scale = 1.0 / points.Count;
        return new Point3d(x * scale, y * scale, z * scale);
    }
}
