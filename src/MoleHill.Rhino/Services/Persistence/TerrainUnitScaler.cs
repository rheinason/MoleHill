using System.Collections.Concurrent;
using System.Reflection;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Scales every persisted value expressed in model length, area, volume, or inverse area when Rhino
/// scales a document while changing its model units. Angles, percentages, counts, ratios, normalized
/// vectors, block scale factors, and paper-space plot weights are intentionally unchanged.
///
/// <para>Which property scales, and by what power, is declared on the property itself
/// (<see cref="ModelLengthAttribute"/>, <see cref="ModelAreaAttribute"/>, <see cref="ModelVolumeAttribute"/>,
/// <see cref="InverseModelAreaAttribute"/>, or <see cref="UnitFreeAttribute"/> for the ones that do not
/// scale), and this class multiplies by reflection. <c>ModelUnitAttributeGuardTests</c> fails when a
/// <c>double</c> property is left undeclared. What stays as code below is only what is not a plain
/// multiply: placement transforms, the encoded sculpt field, the compliance rule set, and the choice of
/// which analysis owners produce length-valued summary fields.</para>
/// </summary>
internal static class TerrainUnitScaler
{
    private sealed record Member(PropertyInfo Property, ModelUnitKind Kind, bool OnlyWhenPositive, bool OwnerDependent);

    private static readonly ConcurrentDictionary<Type, Member[]> Plans = new();

    public static void Scale(ModifierDefinition modifier, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleModifier(modifier, lengthScale);
    }

    public static void Scale(AnalysisDefinition analysis, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleAnalysis(analysis, lengthScale);
    }

    public static void Scale(AnnotationDefinition annotation, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleMembers(annotation, lengthScale, ownerQualifies: false);
    }

    public static void Scale(TerrainObjectDefinition terrainObject, double lengthScale)
    {
        ValidateScale(lengthScale);
        ScaleObject(terrainObject, lengthScale);
    }

    public static void Scale(IEnumerable<TerrainDefinition> terrains, double lengthScale)
    {
        ValidateScale(lengthScale);

        foreach (TerrainDefinition terrain in terrains)
        {
            ScaleMembers(terrain, lengthScale, ownerQualifies: false);

            foreach (ModifierDefinition modifier in terrain.Modifiers)
                ScaleModifier(modifier, lengthScale);

            foreach (TerrainObjectDefinition terrainObject in terrain.Objects)
                ScaleObject(terrainObject, lengthScale);

            foreach (AnalysisDefinition analysis in terrain.Analyses)
                ScaleAnalysis(analysis, lengthScale);

            foreach (AnnotationDefinition annotation in terrain.Annotations)
                ScaleMembers(annotation, lengthScale, ownerQualifies: false);

            foreach (TerrainAnalysisSummary summary in terrain.LastAnalysisResults)
                ScaleSummary(terrain, summary, lengthScale);

            if (terrain.LegacyLastAnalysis != null)
                ScaleSummary(terrain, terrain.LegacyLastAnalysis, lengthScale);
        }
    }

    private static void ValidateScale(double lengthScale)
    {
        if (!double.IsFinite(lengthScale) || lengthScale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(lengthScale));
    }

    private static Member[] PlanFor(Type type) => Plans.GetOrAdd(type, BuildPlan);

    private static Member[] BuildPlan(Type type)
    {
        // A concrete type may promote inherited members the base declares unit-free (an elevation
        // analysis's colour range is a length; a slope analysis's is not).
        HashSet<string> promoted = new(
            type.GetCustomAttribute<ModelLengthMembersAttribute>(inherit: true)?.PropertyNames
            ?? Array.Empty<string>(),
            StringComparer.Ordinal);

        var members = new List<Member>();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(double) && property.PropertyType != typeof(double?))
                continue;
            if (!property.CanRead || property.GetSetMethod() == null || property.GetIndexParameters().Length > 0)
                continue;

            if (promoted.Contains(property.Name))
            {
                members.Add(new Member(property, ModelUnitKind.Length, OnlyWhenPositive: false, OwnerDependent: false));
                continue;
            }

            ModelUnitAttribute? unit = property.GetCustomAttribute<ModelUnitAttribute>(inherit: true);
            if (unit != null)
                members.Add(new Member(property, unit.Kind, unit.OnlyWhenPositive, unit.OwnerDependent));
        }

        return members.ToArray();
    }

    private static double FactorFor(ModelUnitKind kind, double lengthScale)
    {
        double areaScale = lengthScale * lengthScale;
        return kind switch
        {
            ModelUnitKind.Length => lengthScale,
            ModelUnitKind.Area => areaScale,
            ModelUnitKind.Volume => areaScale * lengthScale,
            ModelUnitKind.InverseArea => 1.0 / areaScale,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <param name="target">The definition whose declared members are multiplied.</param>
    /// <param name="lengthScale">The model-length factor; area, volume and density factors derive from it.</param>
    /// <param name="ownerQualifies">
    /// Whether members marked <see cref="ModelUnitAttribute.OwnerDependent"/> scale too. Only a summary
    /// whose owning analysis measures lengths passes true.
    /// </param>
    private static void ScaleMembers(object target, double lengthScale, bool ownerQualifies)
    {
        foreach (Member member in PlanFor(target.GetType()))
        {
            if (member.OwnerDependent && !ownerQualifies)
                continue;

            // A null nullable means "not measured", and an unmeasured value stays unmeasured.
            if (member.Property.GetValue(target) is not double value)
                continue;
            if (member.OnlyWhenPositive && !(value > 0.0))
                continue;

            member.Property.SetValue(target, value * FactorFor(member.Kind, lengthScale));
        }
    }

    private static void ScaleModifier(ModifierDefinition modifier, double lengthScale)
    {
        ScaleMembers(modifier, lengthScale, ownerQualifies: false);

        if (modifier is SculptModifierDefinition sculpt)
            ScaleSculptField(sculpt, lengthScale);
    }

    private static void ScaleObject(TerrainObjectDefinition terrainObject, double lengthScale)
    {
        ScaleMembers(terrainObject, lengthScale, ownerQualifies: false);
        foreach (TerrainObjectPlacementState placement in terrainObject.PlacementStates)
            ScalePlacementTranslation(placement, lengthScale);
    }

    private static void ScaleAnalysis(AnalysisDefinition analysis, double lengthScale)
    {
        ScaleMembers(analysis, lengthScale, ownerQualifies: false);

        // The rule set is its own object, scaled by its own method. The limits on it are slopes, and a
        // slope has no length to scale.
        if (analysis is GradientComplianceAnalysisDefinition compliance)
            compliance.Rules.ScaleLengths(lengthScale);
    }

    private static void ScaleSummary(TerrainDefinition terrain, TerrainAnalysisSummary summary, double lengthScale)
    {
        // The owning definition may be in either family: summaries are build results, and both
        // analyses and annotations produce them.
        object? owner = terrain.Analyses.FirstOrDefault(item => item.Id == summary.AnalysisId)
            ?? (object?)terrain.Annotations.FirstOrDefault(item => item.Id == summary.AnalysisId);

        // The sample statistics and the mapped range are lengths for these owners; for slope they are
        // unitless and must not scale.
        bool ownerMeasuresLengths = owner is ElevationAnalysisDefinition or CutFillAnalysisDefinition or
            ProjectedElevationLabelAnnotationDefinition or CurveElevationLabelAnnotationDefinition;

        ScaleMembers(summary, lengthScale, ownerMeasuresLengths);
    }

    private static void ScalePlacementTranslation(TerrainObjectPlacementState placement, double lengthScale)
    {
        double[] values = placement.LastAppliedTransform;
        if (values == null || values.Length != 16)
            return;

        values[3] *= lengthScale;
        values[7] *= lengthScale;
        values[11] *= lengthScale;
    }

    /// <summary>
    /// The sculpt field is stored encoded, so its displacements (which are lengths) cannot be reached by
    /// the attribute pass. The cell size has already been scaled by then, as the codec expects.
    /// </summary>
    private static void ScaleSculptField(SculptModifierDefinition sculpt, double lengthScale)
    {
        if (sculpt.Tiles.Count == 0)
            return;

        try
        {
            var field = SculptFieldCodec.Decode(sculpt);
            foreach (float[] samples in field.Tiles.Values)
            {
                for (int index = 0; index < samples.Length; index++)
                    samples[index] = (float)(samples[index] * lengthScale);
            }

            sculpt.Tiles = SculptFieldCodec.Encode(field);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid legacy cell size: the build codec will apply its normal validation and recovery.
        }
    }
}
