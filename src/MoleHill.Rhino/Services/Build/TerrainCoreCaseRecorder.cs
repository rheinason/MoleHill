using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainCoreCaseRecorder
{
    private readonly List<TerrainCoreCaseRecord> _records = new();

    public IReadOnlyList<TerrainCoreCaseRecord> Records => _records;

    public void RecordTin(
        string stageName,
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        bool useConvexHull,
        BoundaryTrianglePeelSettings boundaryPeelSettings,
        bool succeeded,
        int? resultVertexCount,
        int? resultFaceCount,
        string? message)
    {
        _records.Add(new TerrainCoreTinCaseRecord(
            stageName,
            succeeded,
            resultVertexCount,
            resultFaceCount,
            message,
            (double[])xyCoords.Clone(),
            (double[])zValues.Clone(),
            (int[])segments.Clone(),
            useConvexHull,
            CloneBoundaryPeelSettings(boundaryPeelSettings)));
    }

    public void RecordPath(
        string stageName,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathGrader.PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double modelTolerance,
        bool preferSplitKeep,
        bool succeeded,
        int? resultVertexCount,
        int? resultFaceCount,
        string? message)
    {
        _records.Add(new TerrainCorePathCaseRecord(
            stageName,
            succeeded,
            resultVertexCount,
            resultFaceCount,
            message,
            (double[])vertices.Clone(),
            vertexCount,
            (int[])faces.Clone(),
            faceCount,
            ClonePaths(paths),
            hardConstraints.Select(CloneConstraint).ToArray(),
            modelTolerance,
            preferSplitKeep));
    }

    public void RecordPad(
        string stageName,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadGrader.PadBoundary[] pads,
        PadGrader.LockCurve[]? lockCurves,
        bool succeeded,
        int? resultVertexCount,
        int? resultFaceCount,
        string? message)
    {
        _records.Add(new TerrainCorePadCaseRecord(
            stageName,
            succeeded,
            resultVertexCount,
            resultFaceCount,
            message,
            (double[])vertices.Clone(),
            vertexCount,
            (int[])faces.Clone(),
            faceCount,
            ClonePads(pads),
            CloneLocks(lockCurves)));
    }

    public TerrainCoreCaseRecord? SelectBestRecord()
    {
        return _records.FirstOrDefault(static record => !record.Succeeded)
            ?? _records.LastOrDefault(static record => record is TerrainCorePathCaseRecord or TerrainCorePadCaseRecord)
            ?? _records.LastOrDefault();
    }

    /// <summary>
    /// A recorded case is only worth replaying if it reproduces the call exactly. Every member that
    /// steers the grade is copied — in particular the per-side angles and <c>OutwardNormals</c>, which
    /// are what make a retaining-wall rail one-sided. Dropping the normals silently downgrades a wall
    /// rail to an ordinary two-sided path, so the replay would grade a different problem from the one
    /// that was captured. <c>TerrainCoreCaseRecorderFidelityTests</c> fails if a member is missed.
    /// </summary>
    private static PathGrader.PathDefinition[] ClonePaths(IEnumerable<PathGrader.PathDefinition> paths)
    {
        return paths
            .Select(static path => new PathGrader.PathDefinition(
                (double[])path.XyVertices.Clone(),
                (double[])path.ZValues.Clone(),
                path.VertexCount,
                path.Width,
                path.SlopeAngleDeg,
                path.MaxDistance,
                path.FillSlopeAngleDeg,
                path.LeftEdgeXy is null ? null : (double[])path.LeftEdgeXy.Clone(),
                path.RightEdgeXy is null ? null : (double[])path.RightEdgeXy.Clone(),
                path.IsClosed,
                path.LeftCutSlopeAngleDeg,
                path.LeftFillSlopeAngleDeg,
                path.RightCutSlopeAngleDeg,
                path.RightFillSlopeAngleDeg,
                path.OutwardNormals is null ? null : (double[])path.OutwardNormals.Clone()))
            .ToArray();
    }

    private static PadGrader.PadBoundary[] ClonePads(IEnumerable<PadGrader.PadBoundary> pads)
    {
        return pads
            .Select(static pad => PadGrader.PadBoundary.CreatePlanar(
                (double[])pad.BoundaryVertices.Clone(),
                pad.VertexCount,
                pad.PlaneXCoeff,
                pad.PlaneYCoeff,
                pad.PlaneConstant,
                pad.SlopeAngleDeg,
                pad.MaxDistance,
                stitchApronDistance: pad.StitchApronDistance,
                fillSlopeAngleDeg: pad.FillSlopeAngleDeg))
            .ToArray();
    }

    private static PadGrader.LockCurve[]? CloneLocks(PadGrader.LockCurve[]? lockCurves)
    {
        if (lockCurves == null || lockCurves.Length == 0)
            return null;

        return lockCurves
            .Select(static curve => new PadGrader.LockCurve((double[])curve.XyVertices.Clone(), curve.VertexCount))
            .ToArray();
    }

    private static ConstraintPolyline CloneConstraint(ConstraintPolyline constraint)
    {
        return new ConstraintPolyline(
            (double[])constraint.Points.Clone(),
            constraint.PointCount,
            constraint.IsClosed,
            constraint.PreserveInputElevation);
    }

    private static BoundaryTrianglePeelSettings CloneBoundaryPeelSettings(BoundaryTrianglePeelSettings settings)
    {
        return new BoundaryTrianglePeelSettings
        {
            Enabled = settings.Enabled,
            MaxBoundaryEdgeLength = settings.MaxBoundaryEdgeLength,
            MaxInteriorAngleDegrees = settings.MaxInteriorAngleDegrees,
            MaxSlopeAngleDegrees = settings.MaxSlopeAngleDegrees
        };
    }
}

internal abstract record TerrainCoreCaseRecord(
    string StageName,
    bool Succeeded,
    int? ResultVertexCount,
    int? ResultFaceCount,
    string? Message);

internal sealed record TerrainCoreTinCaseRecord(
    string StageName,
    bool Succeeded,
    int? ResultVertexCount,
    int? ResultFaceCount,
    string? Message,
    double[] XyCoords,
    double[] ZValues,
    int[] Segments,
    bool UseConvexHull,
    BoundaryTrianglePeelSettings BoundaryPeelSettings)
    : TerrainCoreCaseRecord(StageName, Succeeded, ResultVertexCount, ResultFaceCount, Message);

internal sealed record TerrainCorePathCaseRecord(
    string StageName,
    bool Succeeded,
    int? ResultVertexCount,
    int? ResultFaceCount,
    string? Message,
    double[] Vertices,
    int VertexCount,
    int[] Faces,
    int FaceCount,
    PathGrader.PathDefinition[] Paths,
    ConstraintPolyline[] HardConstraints,
    double ModelTolerance,
    bool PreferSplitKeep)
    : TerrainCoreCaseRecord(StageName, Succeeded, ResultVertexCount, ResultFaceCount, Message);

internal sealed record TerrainCorePadCaseRecord(
    string StageName,
    bool Succeeded,
    int? ResultVertexCount,
    int? ResultFaceCount,
    string? Message,
    double[] Vertices,
    int VertexCount,
    int[] Faces,
    int FaceCount,
    PadGrader.PadBoundary[] Pads,
    PadGrader.LockCurve[]? LockCurves)
    : TerrainCoreCaseRecord(StageName, Succeeded, ResultVertexCount, ResultFaceCount, Message);
