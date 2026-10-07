using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MoleHill.Rhino.Model;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A new length field on a definition must say what it is. <c>TerrainUnitScaler</c> scales by reflection
/// over the declared units, so a <c>double</c> with no declaration would silently stay at its old value
/// when a document changes model units. Declare it with <see cref="ModelLengthAttribute"/> (or area,
/// volume, inverse area), or with <see cref="UnitFreeAttribute"/> and a reason when it is an angle,
/// percentage, ratio, count or similar.
/// </summary>
public sealed class ModelUnitAttributeGuardTests
{
    private static readonly Type[] Families =
    {
        typeof(ModifierDefinition), typeof(AnalysisDefinition), typeof(AnnotationDefinition),
        typeof(TerrainObjectDefinition), typeof(MarkerDefinition), typeof(TerrainDefinition),
        typeof(TerrainAnalysisSummary),
    };

    private static IEnumerable<Type> DefinitionTypes() =>
        typeof(ModifierDefinition).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && Families.Any(family => family.IsAssignableFrom(type)));

    private static IEnumerable<PropertyInfo> StoredDoubles(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?))
                && property.CanRead && property.GetSetMethod() != null && property.GetIndexParameters().Length == 0);

    [Fact]
    public void EveryDoubleOnADefinitionType_DeclaresItsUnit()
    {
        var undeclared = new List<string>();
        foreach (Type type in DefinitionTypes())
        {
            HashSet<string> promoted = type.GetCustomAttribute<ModelLengthMembersAttribute>(inherit: true)
                ?.PropertyNames.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();

            foreach (PropertyInfo property in StoredDoubles(type))
            {
                bool declared = promoted.Contains(property.Name)
                    || property.GetCustomAttribute<ModelUnitAttribute>(inherit: true) != null
                    || property.GetCustomAttribute<UnitFreeAttribute>(inherit: true) != null;
                if (!declared)
                    undeclared.Add($"{type.Name}.{property.Name}");
            }
        }

        Assert.True(undeclared.Count == 0,
            "Declare a unit ([ModelLength], [ModelArea], [ModelVolume], [InverseModelArea]) or [UnitFree(reason)] on: "
            + string.Join(", ", undeclared));
    }

    [Fact]
    public void NoPropertyIsBothScaledAndUnitFree()
    {
        var both = new List<string>();
        foreach (Type type in DefinitionTypes())
        {
            foreach (PropertyInfo property in StoredDoubles(type))
            {
                if (property.GetCustomAttribute<ModelUnitAttribute>(inherit: true) != null
                    && property.GetCustomAttribute<UnitFreeAttribute>(inherit: true) != null)
                    both.Add($"{type.Name}.{property.Name}");
            }
        }

        Assert.True(both.Count == 0, string.Join(", ", both));
    }

    [Fact]
    public void UnitFreeExemptions_CarryAReason()
    {
        var bare = new List<string>();
        foreach (Type type in DefinitionTypes())
        {
            foreach (PropertyInfo property in StoredDoubles(type))
            {
                var unitFree = property.GetCustomAttribute<UnitFreeAttribute>(inherit: true);
                if (unitFree != null && string.IsNullOrWhiteSpace(unitFree.Reason))
                    bare.Add($"{type.Name}.{property.Name}");
            }
        }

        Assert.True(bare.Count == 0, string.Join(", ", bare));
    }

    [Fact]
    public void ModelLengthMembers_NameRealStoredDoubles()
    {
        foreach (Type type in DefinitionTypes())
        {
            var attribute = type.GetCustomAttribute<ModelLengthMembersAttribute>(inherit: true);
            if (attribute == null)
                continue;

            HashSet<string> stored = StoredDoubles(type).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            foreach (string name in attribute.PropertyNames)
                Assert.True(stored.Contains(name), $"{type.Name} promotes '{name}', which is not a writable double property.");
        }
    }

    [Fact]
    public void ModelUnitAttributes_AreOnlyOnDoubleProperties()
    {
        var misplaced = new List<string>();
        foreach (Type type in typeof(ModifierDefinition).Assembly.GetTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                bool marked = property.GetCustomAttribute<ModelUnitAttribute>() != null;
                bool isDouble = property.PropertyType == typeof(double) || property.PropertyType == typeof(double?);
                if (marked && (!isDouble || property.GetSetMethod() == null))
                    misplaced.Add($"{type.Name}.{property.Name}");
            }
        }

        Assert.True(misplaced.Count == 0, "A unit attribute on a non-double or read-only property scales nothing: "
            + string.Join(", ", misplaced));
    }
}
