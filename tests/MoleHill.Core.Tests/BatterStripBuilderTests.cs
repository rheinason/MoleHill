using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class BatterStripBuilderTests
{
    // A flat square terrain patch at a constant elevation, spanning [-50, 50] in X and Y.
    private static TerrainFaceGrid FlatTerrain(double z)
    {
        double[] vertices =
        {
            -50.0, -50.0, z,
             50.0, -50.0, z,
             50.0,  50.0, z,
            -50.0,  50.0, z
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        return new TerrainFaceGrid(vertices, 4, faces, 2);
    }

    // A CCW unit square footprint of the given half-size, centred on the origin.
    private static double[] Square(double half) => new[]
    {
        -half, -half,
         half, -half,
         half,  half,
        -half,  half
    };

    [Fact]
    public void BuildDaylightLoop_FlatTerrainAbovePad_DaylightsAtSlopeReach()
    {
        // Pad at z=0, terrain flat at z=10, 45° slope → reach = dz / tan(45°) = 10 everywhere.
        TerrainFaceGrid terrain = FlatTerrain(10.0);
        double[] footprint = Square(5.0);
        double[] normals = BatterStripBuilder.ComputeClosedLoopOutwardNormals(footprint, 4);

        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            footprint,
            footprintCount: 4,
            isClosed: true,
            outwardNormals: normals,
            footprintZ: (_, _) => 0.0,
            slopeAngleDeg: 45.0,
            maxDistance: 0.0,
            terrain: terrain,
            barriers: PreparedBarriers.Empty,
            tolerance: 1e-3);

        Assert.True(loop.HasBatter);
        Assert.Equal(4, loop.Count);
        foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
        {
            Assert.Equal(BatterStripBuilder.DaylightStatus.Daylighted, station.Status);
            Assert.Equal(10.0, station.Reach, 2);
            Assert.Equal(10.0, station.DayZ, 3);
            // Daylight point lies outward along the station normal.
            Assert.True(Math.Abs(station.DayX) > 5.0 || Math.Abs(station.DayY) > 5.0);
        }
    }

    [Fact]
    public void BuildDaylightLoop_DensifiedFootprint_AllStationsDaylightConsistently()
    {
        TerrainFaceGrid terrain = FlatTerrain(8.0);

        // Densify each edge of a 5-half square into 4 segments → 16 stations.
        double[] corners = Square(5.0);
        var dense = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            for (int step = 0; step < 4; step++)
            {
                double t = step / 4.0;
                dense.Add(corners[i * 2] + ((corners[next * 2] - corners[i * 2]) * t));
                dense.Add(corners[i * 2 + 1] + ((corners[next * 2 + 1] - corners[i * 2 + 1]) * t));
            }
        }

        double[] footprint = dense.ToArray();
        int count = footprint.Length / 2;
        double[] normals = BatterStripBuilder.ComputeClosedLoopOutwardNormals(footprint, count);

        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            footprint,
            count,
            isClosed: true,
            outwardNormals: normals,
            footprintZ: (_, _) => 0.0,
            slopeAngleDeg: 45.0,
            maxDistance: 0.0,
            terrain: terrain,
            barriers: PreparedBarriers.Empty,
            tolerance: 1e-3);

        Assert.Equal(count, loop.Count);
        foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
        {
            Assert.Equal(BatterStripBuilder.DaylightStatus.Daylighted, station.Status);
            Assert.Equal(8.0, station.Reach, 2);
            Assert.Equal(8.0, station.DayZ, 3);
        }
    }

    [Fact]
    public void BuildDaylightLoop_TerrainAtPadElevation_ProducesNoBatter()
    {
        TerrainFaceGrid terrain = FlatTerrain(0.0);
        double[] footprint = Square(5.0);
        double[] normals = BatterStripBuilder.ComputeClosedLoopOutwardNormals(footprint, 4);

        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            footprint,
            4,
            isClosed: true,
            outwardNormals: normals,
            footprintZ: (_, _) => 0.0,
            slopeAngleDeg: 45.0,
            maxDistance: 0.0,
            terrain: terrain,
            barriers: PreparedBarriers.Empty,
            tolerance: 1e-3);

        Assert.False(loop.HasBatter);
        foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
        {
            Assert.Equal(BatterStripBuilder.DaylightStatus.Flat, station.Status);
            Assert.Equal(0.0, station.Reach, 6);
            Assert.Equal(station.FootX, station.DayX, 9);
            Assert.Equal(station.FootY, station.DayY, 9);
        }
    }

    [Fact]
    public void BuildDaylightLoop_NoDaylightWithinMaxDistance_ClampsToMaxDistance()
    {
        // Terrain is 100 above the pad but the pad only reaches out 5 units.
        TerrainFaceGrid terrain = FlatTerrain(100.0);
        double[] footprint = Square(5.0);
        double[] normals = BatterStripBuilder.ComputeClosedLoopOutwardNormals(footprint, 4);

        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            footprint,
            4,
            isClosed: true,
            outwardNormals: normals,
            footprintZ: (_, _) => 0.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0,
            terrain: terrain,
            barriers: PreparedBarriers.Empty,
            tolerance: 1e-3);

        foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
        {
            Assert.Equal(BatterStripBuilder.DaylightStatus.ClampedToMaxDistance, station.Status);
            Assert.Equal(5.0, station.Reach, 6);
            // Clamped stations stay on the slope plane: z = 0 + tan(45°) * 5 = 5.
            Assert.Equal(5.0, station.DayZ, 6);
        }
    }

    private static BatterStripBuilder.BatterStrip BuildStripOnFlatTerrain(
        double terrainZ,
        double padZ,
        double slopeAngleDeg,
        double edgeLength,
        int cornerFanSegments = 6)
    {
        TerrainFaceGrid terrain = FlatTerrain(terrainZ);
        var stations = BatterStripBuilder.BuildClosedFootprintStations(Square(5.0), 4, cornerFanSegments);
        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            stations.Xy,
            stations.Count,
            isClosed: true,
            outwardNormals: stations.Normals,
            footprintZ: (_, _) => padZ,
            slopeAngleDeg: slopeAngleDeg,
            maxDistance: 0.0,
            terrain: terrain,
            barriers: PreparedBarriers.Empty,
            tolerance: 1e-3);
        return BatterStripBuilder.BuildBatterStrip(loop, edgeLength);
    }

    private static double FaceSlopeDegrees(double[] v, int a, int b, int c)
    {
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        double nx = (uy * wz) - (uz * wy);
        double ny = (uz * wx) - (ux * wz);
        double nz = (ux * wy) - (uy * wx);
        double horizontal = Math.Sqrt((nx * nx) + (ny * ny));
        return Math.Atan2(horizontal, Math.Abs(nz)) * 180.0 / Math.PI;
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(26.565051)] // slope ratio 0.5
    [InlineData(63.434949)] // slope ratio 2.0
    public void BuildBatterStrip_FlatTerrain_AllFacesAtTargetSlope(double slopeAngleDeg)
    {
        BatterStripBuilder.BatterStrip strip = BuildStripOnFlatTerrain(
            terrainZ: 10.0,
            padZ: 0.0,
            slopeAngleDeg: slopeAngleDeg,
            edgeLength: 2.0);

        Assert.True(strip.FaceCount > 0);
        for (int f = 0; f < strip.FaceCount; f++)
        {
            double slope = FaceSlopeDegrees(
                strip.Vertices,
                strip.Faces[f * 3],
                strip.Faces[f * 3 + 1],
                strip.Faces[f * 3 + 2]);
            Assert.True(
                Math.Abs(slope - slopeAngleDeg) < 1.0,
                $"Face {f} slope {slope:F3}° deviates from target {slopeAngleDeg:F3}° by more than 1°.");
        }
    }

    [Fact]
    public void BuildBatterStrip_RowSubdivision_FollowsEdgeLength()
    {
        // reach = 10, edge length 2 → 5 rows.
        BatterStripBuilder.BatterStrip strip = BuildStripOnFlatTerrain(
            terrainZ: 10.0,
            padZ: 0.0,
            slopeAngleDeg: 45.0,
            edgeLength: 2.0);

        // reach ≈ 10 / edge 2 → ~5 rows (a sub-tolerance ray-march overshoot may round to 6).
        Assert.InRange(strip.RowCount, 5, 6);
        // Inner ring is at pad elevation, outer ring on terrain.
        foreach (int idx in strip.FootprintRingIndices)
            Assert.Equal(0.0, strip.Vertices[idx * 3 + 2], 6);
        foreach (int idx in strip.DaylightRingIndices)
            Assert.Equal(10.0, strip.Vertices[idx * 3 + 2], 3);
    }

    [Fact]
    public void BuildBatterStrip_FlatStations_ProducesNoFaces()
    {
        BatterStripBuilder.BatterStrip strip = BuildStripOnFlatTerrain(
            terrainZ: 0.0,
            padZ: 0.0,
            slopeAngleDeg: 45.0,
            edgeLength: 2.0);

        Assert.Equal(0, strip.FaceCount);
    }

    [Fact]
    public void ComputeClosedLoopOutwardNormals_Square_PointsOutward()
    {
        double[] normals = BatterStripBuilder.ComputeClosedLoopOutwardNormals(Square(5.0), 4);

        double invSqrt2 = 1.0 / Math.Sqrt(2.0);
        // Vertex 0 = (-5,-5) → outward is (-,-); vertex 2 = (5,5) → outward is (+,+).
        Assert.Equal(-invSqrt2, normals[0], 6);
        Assert.Equal(-invSqrt2, normals[1], 6);
        Assert.Equal(invSqrt2, normals[4], 6);
        Assert.Equal(invSqrt2, normals[5], 6);
    }
}
