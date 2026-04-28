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
        double maxBoundaryEdgeLength,
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
            maxBoundaryEdgeLength));
    }

    public void RecordPath(
        string stageName,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathGrader.PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
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
            hardConstraints.Select(CloneConstraint).ToArray()));
    }

    public void RecordPad(
        string stageName,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadGrader.PadBoundary[] pads,
        PadGrader.LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
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
            CloneLocks(lockCurves),
            maxArea,
            minAngle));
    }

    public TerrainCoreCaseRecord? SelectBestRecord()
    {
        return _records.FirstOrDefault(static record => !record.Succeeded)
            ?? _records.LastOrDefault(static record => record is TerrainCorePathCaseRecord or TerrainCorePadCaseRecord)
            ?? _records.LastOrDefault();
    }

    private static PathGrader.PathDefinition[] ClonePaths(IEnumerable<PathGrader.PathDefinition> paths)
    {
        return paths
            .Select(static path => new PathGrader.PathDefinition(
                (double[])path.XyVertices.Clone(),
                (double[])path.ZValues.Clone(),
                path.VertexCount,
                path.Width,
                path.SlopeAngleDeg,
                path.MaxDistance))
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
                pad.MaxDistance))
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

    private static SurfaceRemesher.ConstraintPolyline CloneConstraint(SurfaceRemesher.ConstraintPolyline constraint)
    {
        return new SurfaceRemesher.ConstraintPolyline(
            (double[])constraint.Points.Clone(),
            constraint.PointCount,
            constraint.IsClosed,
            constraint.PreserveInputElevation);
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
    double MaxBoundaryEdgeLength)
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
    SurfaceRemesher.ConstraintPolyline[] HardConstraints)
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
    PadGrader.LockCurve[]? LockCurves,
    double MaxArea,
    double MinAngle)
    : TerrainCoreCaseRecord(StageName, Succeeded, ResultVertexCount, ResultFaceCount, Message);
