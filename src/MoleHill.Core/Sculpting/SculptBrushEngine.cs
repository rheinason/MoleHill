using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Sculpting;

/// <summary>Parameters of one brush dab. Dab spacing along a stroke is the caller's job (canonically
/// 0.25 x radius of world-XY travel between dabs).</summary>
public readonly record struct SculptDabParams(
    double CenterX,
    double CenterY,
    double Radius,
    double Strength,
    SculptBrushKind Brush,
    SculptFalloff Falloff,
    bool Invert,
    int NoiseSeed);

/// <summary>
/// Owns the working mesh state of one sculpt session and applies brush dabs to it. Brushes mutate
/// vertex Z in place (the fast stroke path — no field writes, no re-triangulation); at stroke end the
/// caller rasterizes the accumulated per-vertex delta (Z - BaseZ) into the displacement field via
/// <see cref="SculptFieldRasterizer"/>. BaseZ is recovered from the constructor field as
/// z - field.Sample(x, y) — exact, because the sculpt build stage computed z = baseZ + Sample.
/// </summary>
public sealed class SculptBrushEngine
{
    private const double DrawStepScale = 0.05;    // Z gain per dab as a fraction of radius at full weight
    // Blend toward the neighbor average per dab. 2.0 reaches a full relaxation step at the brush
    // center at default 0.5 strength — a 0.5 scale felt far weaker than Draw in field testing.
    private const double SmoothStepScale = 2.0;
    private const double FlattenStepScale = 0.5;  // blend toward the stroke plane per dab
    private const double ClayPlaneOffset = 0.3;   // clay target plane offset as a fraction of radius

    private double[] _vertices;
    private int _vertexCount;
    private int[] _faces;
    private int _faceCount;
    private double[] _baseZ;
    private readonly SculptDisplacementField _field;
    private readonly SculptConstraintMask? _constraintMask;

    private SpatialHashGrid2D _vertexGrid;
    private readonly SpatialHashGrid2D.QueryScratch _scratch = new();
    private readonly List<int> _candidates = new();
    private readonly List<int> _affected = new();

    // Smooth-brush CSR adjacency, built lazily and kept until topology changes.
    private int[]? _neighborOffsets;
    private int[]? _neighborIndices;
    private bool[]? _isBoundary;

    // Per-stroke state.
    private SculptStrokeUndoRecord? _activeStroke;
    private readonly List<int> _touchedIndices = new();
    private readonly List<double> _touchedOldZ = new();
    private int[] _touchStamps;
    private int _strokeStamp;
    private double _strokePlaneZ;
    private int[] _grabIndices = Array.Empty<int>();
    private double[] _grabWeights = Array.Empty<double>();
    private double[] _grabZ0 = Array.Empty<double>();

    public SculptBrushEngine(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SculptDisplacementField field,
        SculptConstraintMask? constraintMask = null)
    {
        _vertices = vertices;
        _vertexCount = vertexCount;
        _faces = faces;
        _faceCount = faceCount;
        _field = field;
        _constraintMask = constraintMask;

        _baseZ = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            _baseZ[i] = vertices[i * 3 + 2] - (field.Sample(x, y) * ConstraintInfluence(x, y));
        }

        _touchStamps = new int[vertexCount];
        _vertexGrid = BuildVertexGrid(vertices, vertexCount);
    }

    public double[] Vertices => _vertices;
    public int VertexCount => _vertexCount;
    public int[] Faces => _faces;
    public int FaceCount => _faceCount;
    public double[] BaseZ => _baseZ;
    public SculptDisplacementField Field => _field;
    public SculptConstraintMask? ConstraintMask => _constraintMask;
    public bool StrokeActive => _activeStroke != null;

    /// <summary>Bumped whenever DynTopo refinement swaps the working arrays — callers rebuild any
    /// mesh/index built from them when this changes.</summary>
    public int TopologyVersion { get; private set; }

    /// <summary>Starts a stroke: opens the undo record and captures stroke-constant state (the Flatten/Clay
    /// reference plane, the Grab anchor set) from the first dab's position.</summary>
    public void BeginStroke(in SculptDabParams p)
    {
        _activeStroke = new SculptStrokeUndoRecord();
        _touchedIndices.Clear();
        _touchedOldZ.Clear();
        _strokeStamp++;

        if (p.Brush is SculptBrushKind.Flatten or SculptBrushKind.Clay)
            _strokePlaneZ = ComputeWeightedPlaneZ(p);

        if (p.Brush == SculptBrushKind.Grab)
            CaptureGrabAnchor(p);
    }

    /// <summary>
    /// Applies one dab, mutating vertex Z in place. Returns the indices whose Z changed — the list is
    /// reused across calls, so consume it before the next dab. <paramref name="grabDeltaZ"/> is the
    /// total Z drag since stroke start (Grab brush only; other brushes ignore it).
    /// </summary>
    public IReadOnlyList<int> ApplyDab(in SculptDabParams p, double grabDeltaZ = 0.0)
    {
        if (_activeStroke == null)
            throw new InvalidOperationException("BeginStroke must be called before ApplyDab.");

        _affected.Clear();
        if (p.Radius <= 0.0 || p.Strength <= 0.0)
            return _affected;

        if (p.Brush == SculptBrushKind.Grab)
        {
            ApplyGrab(grabDeltaZ);
            return _affected;
        }

        GatherCandidates(p.CenterX, p.CenterY, p.Radius);
        if (_candidates.Count == 0)
            return _affected;

        if (p.Brush == SculptBrushKind.Smooth)
            EnsureAdjacency();

        double sign = p.Invert ? -1.0 : 1.0;
        double invRadius = 1.0 / p.Radius;
        double clayTarget = _strokePlaneZ + sign * ClayPlaneOffset * p.Radius;

        foreach (int i in _candidates)
        {
            double dx = _vertices[i * 3] - p.CenterX;
            double dy = _vertices[i * 3 + 1] - p.CenterY;
            double d = Math.Sqrt(dx * dx + dy * dy) * invRadius;
            if (d >= 1.0)
                continue;

            double w = p.Strength * SculptFalloffs.Evaluate(p.Falloff, d) *
                       ConstraintInfluence(_vertices[i * 3], _vertices[i * 3 + 1]);
            if (w <= 0.0)
                continue;

            double z = _vertices[i * 3 + 2];
            double newZ = z;
            switch (p.Brush)
            {
                case SculptBrushKind.Draw:
                    newZ = z + sign * w * p.Radius * DrawStepScale;
                    break;
                case SculptBrushKind.Subtract:
                    newZ = z - sign * w * p.Radius * DrawStepScale;
                    break;
                case SculptBrushKind.Erase:
                    newZ = z + w * (_baseZ[i] - z);
                    break;
                case SculptBrushKind.Smooth:
                    if (TryNeighborAverageZ(i, out double avg))
                        newZ = z + Math.Min(1.0, w * SmoothStepScale) * (avg - z);
                    break;
                case SculptBrushKind.Flatten:
                    newZ = z + Math.Min(1.0, w * FlattenStepScale) * (_strokePlaneZ - z);
                    break;
                case SculptBrushKind.Clay:
                    if (sign > 0.0 && z < clayTarget)
                        newZ = Math.Min(clayTarget, z + w * p.Radius * DrawStepScale);
                    else if (sign < 0.0 && z > clayTarget)
                        newZ = Math.Max(clayTarget, z - w * p.Radius * DrawStepScale);
                    break;
                case SculptBrushKind.Noise:
                    newZ = z + sign * w * p.Radius * DrawStepScale * ValueNoise(
                        _vertices[i * 3], _vertices[i * 3 + 1], p.Radius * 0.5, p.NoiseSeed);
                    break;
            }

            if (newZ == z)
                continue;

            TouchVertex(i, z);
            _vertices[i * 3 + 2] = newZ;
            _affected.Add(i);
        }

        if (_affected.Count > 0)
            _activeStroke.ExpandDirtyBounds(p.CenterX, p.CenterY, p.Radius);

        return _affected;
    }

    /// <summary>Closes the stroke and returns its undo record (per-vertex old/new Z and dirty bounds
    /// filled in; tile changes are added later by the commit path). Null when nothing was touched.</summary>
    public SculptStrokeUndoRecord? EndStroke()
    {
        var record = _activeStroke;
        _activeStroke = null;
        if (record == null || _touchedIndices.Count == 0)
            return null;

        for (int t = 0; t < _touchedIndices.Count; t++)
        {
            int i = _touchedIndices[t];
            record.TouchedZ.Add((i, _touchedOldZ[t], _vertices[i * 3 + 2]));
        }

        _touchedIndices.Clear();
        _touchedOldZ.Clear();
        return record;
    }

    /// <summary>
    /// Grows the working mesh under the brush disk to <paramref name="targetEdgeLength"/> (DynTopo).
    /// Topology is forward-only within a session: undo re-seats created midpoints on the restored
    /// surface instead of removing them (exact — midpoints lie on the PL surface by construction).
    /// Returns true when topology changed; the caller must then rebuild anything derived from the
    /// arrays (display mesh, normal index).
    /// </summary>
    public bool RefineRegion(double centerX, double centerY, double radius, double targetEdgeLength)
    {
        if (targetEdgeLength <= 0.0 || radius <= 0.0)
            return false;

        double reach = radius + targetEdgeLength;
        double reachSquared = reach * reach;
        LocalMeshRefiner.Result result = LocalMeshRefiner.Refine(
            _vertices,
            _faces,
            Array.Empty<ConstraintPolyline>(),
            new LocalMeshRefiner.Options
            {
                TargetEdgeLength = targetEdgeLength,
                // Split-only, everywhere: regularizing flips destroy intentional graded flow (as the
                // old Remesh showed on curved batters), so neither the session nor the
                // build stage runs them — subdivision alone keeps the surface and topology honest.
                DoFlips = false,
                RegionFilter = (x, y) =>
                {
                    double dx = x - centerX;
                    double dy = y - centerY;
                    return dx * dx + dy * dy <= reachSquared;
                },
            });

        if (!result.Success || result.AddedVertices == 0)
            return false;

        int oldVertexCount = _vertexCount;
        _vertices = result.Vertices;
        _faces = result.Faces;
        _vertexCount = _vertices.Length / 3;
        _faceCount = _faces.Length / 3;

        var baseZ = new double[_vertexCount];
        Array.Copy(_baseZ, baseZ, oldVertexCount);
        for (int k = 0; k < result.MidpointParents.Length / 2; k++)
        {
            int m = oldVertexCount + k;
            int a = result.MidpointParents[k * 2];
            int b = result.MidpointParents[k * 2 + 1];
            baseZ[m] = (baseZ[a] + baseZ[b]) * 0.5;
            _activeStroke?.CreatedMidpoints.Add((m, a, b));
        }

        _baseZ = baseZ;
        Array.Resize(ref _touchStamps, _vertexCount);
        _vertexGrid = BuildVertexGrid(_vertices, _vertexCount);
        _neighborOffsets = null;
        _neighborIndices = null;
        _isBoundary = null;
        TopologyVersion++;
        return true;
    }

    /// <summary>Reverts one stroke: restores pre-stroke vertex Z, re-seats midpoints the stroke
    /// created onto the restored surface, and restores pre-commit field tiles.</summary>
    public void ApplyUndo(SculptStrokeUndoRecord record)
    {
        foreach (var (index, oldZ, _) in record.TouchedZ)
        {
            if (index < _vertexCount)
                _vertices[index * 3 + 2] = oldZ;
        }

        // In creation order, so midpoints of midpoints resolve against already re-seated parents.
        foreach (var (m, a, b) in record.CreatedMidpoints)
        {
            if (m < _vertexCount)
                _vertices[m * 3 + 2] = (_vertices[a * 3 + 2] + _vertices[b * 3 + 2]) * 0.5;
        }

        foreach (var (i, j, oldTile, _) in record.TileChanges)
            _field.ReplaceTile(i, j, (float[]?)oldTile?.Clone());
    }

    /// <summary>Re-applies one undone stroke: restores post-stroke vertex Z and post-commit field tiles.</summary>
    public void ApplyRedo(SculptStrokeUndoRecord record)
    {
        foreach (var (index, _, newZ) in record.TouchedZ)
        {
            if (index < _vertexCount)
                _vertices[index * 3 + 2] = newZ;
        }

        foreach (var (i, j, _, newTile) in record.TileChanges)
            _field.ReplaceTile(i, j, (float[]?)newTile?.Clone());
    }

    private void GatherCandidates(double cx, double cy, double radius)
    {
        _candidates.Clear();
        _vertexGrid.GatherCandidates(Bounds2D.FromPoint(cx, cy, radius), _candidates, _scratch);
    }

    private void EnsureAdjacency()
    {
        if (_neighborOffsets != null)
            return;

        MeshSmoother.BuildNeighborGraph(_vertexCount, _faces, _faceCount, out var offsets, out var indices, out var boundary);
        _neighborOffsets = offsets;
        _neighborIndices = indices;
        _isBoundary = boundary;
    }

    private bool TryNeighborAverageZ(int vertex, out double average)
    {
        average = 0.0;
        // Boundary vertices are pinned (matching MeshSmoother) so smoothing can't curl the mesh rim.
        if (_isBoundary![vertex])
            return false;

        int start = _neighborOffsets![vertex];
        int end = _neighborOffsets[vertex + 1];
        if (end <= start)
            return false;

        double sum = 0.0;
        for (int n = start; n < end; n++)
            sum += _vertices[_neighborIndices![n] * 3 + 2];
        average = sum / (end - start);
        return true;
    }

    private void TouchVertex(int index, double oldZ)
    {
        if (_touchStamps[index] == _strokeStamp)
            return;

        _touchStamps[index] = _strokeStamp;
        _touchedIndices.Add(index);
        _touchedOldZ.Add(oldZ);
    }

    private double ComputeWeightedPlaneZ(in SculptDabParams p)
    {
        GatherCandidates(p.CenterX, p.CenterY, p.Radius);
        double invRadius = 1.0 / p.Radius;
        double weightSum = 0.0;
        double zSum = 0.0;
        foreach (int i in _candidates)
        {
            double dx = _vertices[i * 3] - p.CenterX;
            double dy = _vertices[i * 3 + 1] - p.CenterY;
            double d = Math.Sqrt(dx * dx + dy * dy) * invRadius;
            if (d >= 1.0)
                continue;

            double w = SculptFalloffs.Evaluate(p.Falloff, d) *
                       ConstraintInfluence(_vertices[i * 3], _vertices[i * 3 + 1]);
            weightSum += w;
            zSum += w * _vertices[i * 3 + 2];
        }

        return weightSum > 0.0 ? zSum / weightSum : 0.0;
    }

    private void CaptureGrabAnchor(in SculptDabParams p)
    {
        GatherCandidates(p.CenterX, p.CenterY, p.Radius);
        double invRadius = 1.0 / p.Radius;
        var indices = new List<int>();
        var weights = new List<double>();
        var z0 = new List<double>();
        foreach (int i in _candidates)
        {
            double dx = _vertices[i * 3] - p.CenterX;
            double dy = _vertices[i * 3 + 1] - p.CenterY;
            double d = Math.Sqrt(dx * dx + dy * dy) * invRadius;
            if (d >= 1.0)
                continue;

            double w = p.Strength * SculptFalloffs.Evaluate(p.Falloff, d) *
                       ConstraintInfluence(_vertices[i * 3], _vertices[i * 3 + 1]);
            if (w <= 0.0)
                continue;

            indices.Add(i);
            weights.Add(Math.Min(1.0, w));
            z0.Add(_vertices[i * 3 + 2]);
        }

        _grabIndices = indices.ToArray();
        _grabWeights = weights.ToArray();
        _grabZ0 = z0.ToArray();
        _activeStroke!.ExpandDirtyBounds(p.CenterX, p.CenterY, p.Radius);
    }

    private void ApplyGrab(double grabDeltaZ)
    {
        for (int k = 0; k < _grabIndices.Length; k++)
        {
            int i = _grabIndices[k];
            double newZ = _grabZ0[k] + _grabWeights[k] * grabDeltaZ;
            double z = _vertices[i * 3 + 2];
            if (newZ == z)
                continue;

            TouchVertex(i, _grabZ0[k]);
            _vertices[i * 3 + 2] = newZ;
            _affected.Add(i);
        }
    }

    private double ConstraintInfluence(double x, double y) =>
        _constraintMask?.EvaluateInfluence(x, y) ?? 1.0;

    private static SpatialHashGrid2D BuildVertexGrid(double[] vertices, int vertexCount)
    {
        var bounds = new Bounds2D[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            bounds[i] = Bounds2D.FromPoint(vertices[i * 3], vertices[i * 3 + 1]);
        return SpatialHashGrid2D.Build(bounds);
    }

    /// <summary>Deterministic value noise in [-1, 1]: hash lattice at <paramref name="cell"/> spacing,
    /// smoothstep-interpolated. World-anchored so overlapping dabs reinforce the same pattern.</summary>
    private static double ValueNoise(double x, double y, double cell, int seed)
    {
        double gx = x / cell;
        double gy = y / cell;
        int ix = (int)Math.Floor(gx);
        int iy = (int)Math.Floor(gy);
        double fx = gx - ix;
        double fy = gy - iy;
        double sx = fx * fx * (3.0 - 2.0 * fx);
        double sy = fy * fy * (3.0 - 2.0 * fy);

        double v00 = HashToUnit(ix, iy, seed);
        double v10 = HashToUnit(ix + 1, iy, seed);
        double v01 = HashToUnit(ix, iy + 1, seed);
        double v11 = HashToUnit(ix + 1, iy + 1, seed);

        double bottom = v00 + (v10 - v00) * sx;
        double top = v01 + (v11 - v01) * sx;
        return bottom + (top - bottom) * sy;
    }

    private static double HashToUnit(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B1u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0x85EBCA77u;
            h *= 0xC2B2AE3Du;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }
}
