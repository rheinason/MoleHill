using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class SimplifyModifierTests
{
    [Fact]
    public void Descriptor_DeclaresModelLengthBoundAndPhysicalDefault()
    {
        ModifierTypeDescriptor descriptor = Assert.Single(
            TerrainTypeRegistry.Modifiers, item => item.DefinitionType == typeof(SimplifyModifierDefinition));
        var definition = Assert.IsType<SimplifyModifierDefinition>(descriptor.Create(UnitSystem.Millimeters));

        Assert.Equal("simplify", descriptor.Kind);
        Assert.Equal(50.0, definition.MaximumDeviation, 9);
        ParameterDescriptor<ModifierDefinition> parameter = Assert.Single(
            descriptor.Parameters, item => item.Key == "MaximumDeviation");
        Assert.Equal("MaximumDeviation", parameter.Key);
        Assert.Equal(ParameterUnit.ModelLength, parameter.Unit);
        Assert.Equal(ParameterUnit.None, Assert.Single(
            descriptor.Parameters, item => item.Key == "TargetVertexCount").Unit);
        Assert.Equal(ParameterUnit.Percent, Assert.Single(
            descriptor.Parameters, item => item.Key == "RetainPercentage").Unit);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesMaximumDeviation()
    {
        var modifier = new SimplifyModifierDefinition
        {
            Mode = SimplifyModifierDefinition.RetainPercentageMode,
            MaximumDeviation = 0.125,
            TargetVertexCount = 12_345,
            RetainPercentage = 37.5
        };

        string json = TerrainSerializer.Serialize([new TerrainDefinition { Modifiers = [modifier] }]);
        var restored = Assert.Single(
            Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers.OfType<SimplifyModifierDefinition>());

        Assert.Equal(0.125, restored.MaximumDeviation, 10);
        Assert.Equal(SimplifyModifierDefinition.RetainPercentageMode, restored.Mode);
        Assert.Equal(12_345, restored.TargetVertexCount);
        Assert.Equal(37.5, restored.RetainPercentage, 10);
    }

    [Fact]
    public void UnitScaler_ScalesMaximumDeviationAsLength()
    {
        var modifier = new SimplifyModifierDefinition { MaximumDeviation = 2.5 };

        TerrainUnitScaler.Scale(modifier, 1000.0);

        Assert.Equal(2500.0, modifier.MaximumDeviation, 9);
    }

    [Theory]
    [InlineData(101, 50.0, 50)]
    [InlineData(101, 100.0, 101)]
    [InlineData(101, 0.0, 0)]
    public void PercentageTarget_UsesDocumentedFloorRounding(int used, double percentage, int expected)
    {
        bool success = SimplifyStage.TryResolvePercentageTarget(
            used, percentage, out int target, out string? failure);

        Assert.True(success, failure);
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(double.NaN)]
    public void PercentageTarget_RejectsOutOfRangeValues(double percentage)
    {
        Assert.False(SimplifyStage.TryResolvePercentageTarget(
            100, percentage, out _, out string? failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void ConstraintResolver_MapsPolylineToEveryCoincidentMeshEdge()
    {
        Grid(out double[] vertices, out int[] faces);
        var constraint = new ConstraintPolyline(
            new[] { 0.0, 1.0, 1.0, 2.0, 1.0, 3.0 }, 2, IsClosed: false, PreserveInputElevation: true);

        bool success = SimplifyStage.TryResolveSimplifyConstraintEdges(
            vertices, faces, [constraint], 1e-8, out int[] segments, out string? failure);

        Assert.True(success, failure);
        var keys = Enumerable.Range(0, segments.Length / 2)
            .Select(i => IndexedMeshTools.GetEdgeKey(segments[i * 2], segments[i * 2 + 1]))
            .OrderBy(key => key)
            .ToArray();
        Assert.Equal(
            new[] { IndexedMeshTools.GetEdgeKey(3, 4), IndexedMeshTools.GetEdgeKey(4, 5) }.OrderBy(key => key),
            keys);
    }

    [Fact]
    public void ConstraintResolver_RejectsConflictingPersistentElevation()
    {
        Grid(out double[] vertices, out int[] faces);
        var constraint = new ConstraintPolyline(
            new[] { 0.0, 1.0, 8.0, 2.0, 1.0, 8.0 }, 2, IsClosed: false, PreserveInputElevation: true);

        bool success = SimplifyStage.TryResolveSimplifyConstraintEdges(
            vertices, faces, [constraint], 1e-8, out _, out string? failure);

        Assert.False(success);
        Assert.Contains("conflicts", failure);
    }

    private static void Grid(out double[] vertices, out int[] faces)
    {
        vertices =
        [
            0, 0, 0, 1, 0, 1, 2, 0, 2,
            0, 1, 1, 1, 1, 2, 2, 1, 3,
            0, 2, 2, 1, 2, 3, 2, 2, 4
        ];
        faces =
        [
            0, 1, 4, 0, 4, 3,
            1, 2, 5, 1, 5, 4,
            3, 4, 7, 3, 7, 6,
            4, 5, 8, 4, 8, 7
        ];
    }
}
