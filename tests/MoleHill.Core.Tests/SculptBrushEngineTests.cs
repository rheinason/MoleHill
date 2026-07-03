using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptBrushEngineTests
{
    private const int GridN = 11;       // 11x11 vertices, spacing 1 => world [0,10]^2
    private const double Spacing = 1.0;

    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) BuildGridMesh(
        Func<double, double, double>? height = null)
    {
        int vertexCount = GridN * GridN;
        var vertices = new double[vertexCount * 3];
        for (int j = 0; j < GridN; j++)
        {
            for (int i = 0; i < GridN; i++)
            {
                int v = j * GridN + i;
                double x = i * Spacing;
                double y = j * Spacing;
                vertices[v * 3] = x;
                vertices[v * 3 + 1] = y;
                vertices[v * 3 + 2] = height?.Invoke(x, y) ?? 0.0;
            }
        }

        int faceCount = (GridN - 1) * (GridN - 1) * 2;
        var faces = new int[faceCount * 3];
        int f = 0;
        for (int j = 0; j < GridN - 1; j++)
        {
            for (int i = 0; i < GridN - 1; i++)
            {
                int v00 = j * GridN + i;
                int v10 = v00 + 1;
                int v01 = v00 + GridN;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, vertexCount, faces, faceCount);
    }

    private static SculptBrushEngine CreateEngine(
        Func<double, double, double>? height = null,
        SculptDisplacementField? field = null)
    {
        var (vertices, vertexCount, faces, faceCount) = BuildGridMesh(height);
        return new SculptBrushEngine(vertices, vertexCount, faces, faceCount, field ?? new SculptDisplacementField(0.25));
    }

    private static SculptDabParams Dab(
        SculptBrushKind brush,
        double cx = 5.0,
        double cy = 5.0,
        double radius = 2.5,
        double strength = 1.0,
        bool invert = false,
        SculptFalloff falloff = SculptFalloff.Smooth)
    {
        return new SculptDabParams(cx, cy, radius, strength, brush, falloff, invert, NoiseSeed: 7);
    }

    [Fact]
    public void ApplyDab_Draw_RaisesOnlyVerticesInsideRadius()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Draw);

        engine.BeginStroke(p);
        var affected = engine.ApplyDab(p);

        Assert.NotEmpty(affected);
        for (int i = 0; i < engine.VertexCount; i++)
        {
            double dx = engine.Vertices[i * 3] - 5.0;
            double dy = engine.Vertices[i * 3 + 1] - 5.0;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double z = engine.Vertices[i * 3 + 2];
            if (dist < 2.5 - 1e-9)
                Assert.True(z > 0.0, $"vertex {i} inside brush not raised");
            else
                Assert.Equal(0.0, z, 12);
        }
    }

    [Fact]
    public void ApplyDab_DrawInverted_Lowers()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Draw, invert: true);

        engine.BeginStroke(p);
        engine.ApplyDab(p);

        int center = 5 * GridN + 5;
        Assert.True(engine.Vertices[center * 3 + 2] < 0.0);
    }

    [Fact]
    public void ApplyDab_Weights_DecreaseWithDistance()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Draw, radius: 4.0);

        engine.BeginStroke(p);
        engine.ApplyDab(p);

        double zCenter = engine.Vertices[(5 * GridN + 5) * 3 + 2];
        double zNear = engine.Vertices[(5 * GridN + 6) * 3 + 2];   // 1 unit away
        double zFar = engine.Vertices[(5 * GridN + 8) * 3 + 2];    // 3 units away
        Assert.True(zCenter > zNear, "center should gain the most");
        Assert.True(zNear > zFar, "gain should fall off with distance");
        Assert.True(zFar > 0.0, "inside the radius still gains");
    }

    [Fact]
    public void ApplyDab_Smooth_ReducesSpikeAndPreservesOutside()
    {
        // A single spike at the center of an otherwise flat mesh.
        var engine = CreateEngine((x, y) => x == 5.0 && y == 5.0 ? 1.0 : 0.0);
        var p = Dab(SculptBrushKind.Smooth, radius: 2.0);

        engine.BeginStroke(p);
        engine.ApplyDab(p);

        int center = 5 * GridN + 5;
        Assert.True(engine.Vertices[center * 3 + 2] < 1.0, "spike should relax down");
        Assert.Equal(0.0, engine.Vertices[(0 * GridN + 0) * 3 + 2], 12);
    }

    [Fact]
    public void ApplyDab_Flatten_MovesTowardStrokePlane()
    {
        var engine = CreateEngine((x, y) => 0.2 * x);
        var p = Dab(SculptBrushKind.Flatten, radius: 3.0, falloff: SculptFalloff.Constant);

        engine.BeginStroke(p);
        engine.ApplyDab(p);

        // The stroke plane is the weighted mean under the brush at stroke start (= 0.2 * 5 = 1 by symmetry).
        double planeZ = 1.0;
        int west = 5 * GridN + 3;  // z was 0.6, below the plane
        int east = 5 * GridN + 7;  // z was 1.4, above the plane
        Assert.True(engine.Vertices[west * 3 + 2] > 0.6 && engine.Vertices[west * 3 + 2] <= planeZ + 1e-9);
        Assert.True(engine.Vertices[east * 3 + 2] < 1.4 && engine.Vertices[east * 3 + 2] >= planeZ - 1e-9);
    }

    [Fact]
    public void Grab_TranslatesCapturedVerticesByWeightedDelta()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Grab, radius: 2.0, falloff: SculptFalloff.Constant);

        engine.BeginStroke(p);
        engine.ApplyDab(p, grabDeltaZ: 1.5);
        engine.ApplyDab(p, grabDeltaZ: 0.75); // grab tracks the cursor: later delta overrides, not accumulates

        int center = 5 * GridN + 5;
        Assert.Equal(0.75, engine.Vertices[center * 3 + 2], 9);
    }

    [Fact]
    public void Constructor_WithExistingField_RecoversBaseZ()
    {
        // Working mesh Z = 0.5 everywhere; field says the displacement there is 0.5 => base is 0.
        var field = new SculptDisplacementField(0.25);
        for (int gi = -8; gi <= 48; gi++)
        {
            for (int gj = -8; gj <= 48; gj++)
                field.SetSample(gi, gj, 0.5f);
        }

        var engine = CreateEngine((_, _) => 0.5, field);

        for (int i = 0; i < engine.VertexCount; i++)
            Assert.Equal(0.0, engine.BaseZ[i], 6);
    }

    [Fact]
    public void EndStroke_RecordsOldAndNewZ_AndUndoRedoRoundTrips()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Draw);

        engine.BeginStroke(p);
        engine.ApplyDab(p);
        var record = engine.EndStroke();

        Assert.NotNull(record);
        Assert.NotEmpty(record!.TouchedZ);
        Assert.True(record.HasDirtyBounds);

        int center = 5 * GridN + 5;
        double sculpted = engine.Vertices[center * 3 + 2];
        Assert.True(sculpted > 0.0);

        engine.ApplyUndo(record);
        Assert.Equal(0.0, engine.Vertices[center * 3 + 2], 12);

        engine.ApplyRedo(record);
        Assert.Equal(sculpted, engine.Vertices[center * 3 + 2], 12);
    }

    [Fact]
    public void ApplyDab_MultipleDabsInOneStroke_CaptureFirstTouchOldZ()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Draw);

        engine.BeginStroke(p);
        engine.ApplyDab(p);
        engine.ApplyDab(p);
        var record = engine.EndStroke()!;

        engine.ApplyUndo(record);
        int center = 5 * GridN + 5;
        Assert.Equal(0.0, engine.Vertices[center * 3 + 2], 12);
    }

    [Fact]
    public void RefineRegion_CoarseMeshUnderBrush_SubdividesOnlyThere()
    {
        var engine = CreateEngine();
        int before = engine.VertexCount;

        bool changed = engine.RefineRegion(5.0, 5.0, 2.0, targetEdgeLength: 0.4);

        Assert.True(changed);
        Assert.True(engine.VertexCount > before);
        Assert.Equal(1, engine.TopologyVersion);
        for (int i = before; i < engine.VertexCount; i++)
        {
            double dx = engine.Vertices[i * 3] - 5.0;
            double dy = engine.Vertices[i * 3 + 1] - 5.0;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= 2.0 + 0.4 + 1.5,
                $"vertex {i} added far outside the brush disk");
        }
    }

    [Fact]
    public void RefineRegion_NewVertices_BaseZIsParentAverage()
    {
        // Base surface z = 0.3 * x, no displacement field: BaseZ of a midpoint must interpolate.
        var engine = CreateEngine((x, _) => 0.3 * x);

        Assert.True(engine.RefineRegion(5.0, 5.0, 2.0, targetEdgeLength: 0.4));

        for (int i = 0; i < engine.VertexCount; i++)
            Assert.Equal(0.3 * engine.Vertices[i * 3], engine.BaseZ[i], 9);
    }

    [Fact]
    public void ApplyUndo_AfterDynTopoStroke_RestoresSurfaceIncludingMidpoints()
    {
        var engine = CreateEngine();
        var p = Dab(SculptBrushKind.Draw, radius: 2.0);

        engine.BeginStroke(p);
        engine.ApplyDab(p);
        // Mid-stroke refinement: new midpoints inherit partially-displaced Z and get recorded.
        Assert.True(engine.RefineRegion(5.0, 5.0, 2.0, targetEdgeLength: 0.4));
        engine.ApplyDab(p);
        var record = engine.EndStroke()!;
        Assert.NotEmpty(record.CreatedMidpoints);

        engine.ApplyUndo(record);

        // The whole mesh must be back on the flat z = 0 surface, midpoints included.
        for (int i = 0; i < engine.VertexCount; i++)
            Assert.Equal(0.0, engine.Vertices[i * 3 + 2], 9);
    }

    [Fact]
    public void ApplyDab_Noise_IsDeterministicForSeed()
    {
        var engineA = CreateEngine();
        var engineB = CreateEngine();
        var p = Dab(SculptBrushKind.Noise, radius: 3.0);

        engineA.BeginStroke(p);
        engineA.ApplyDab(p);
        engineB.BeginStroke(p);
        engineB.ApplyDab(p);

        for (int i = 0; i < engineA.VertexCount; i++)
            Assert.Equal(engineA.Vertices[i * 3 + 2], engineB.Vertices[i * 3 + 2], 12);
    }
}
