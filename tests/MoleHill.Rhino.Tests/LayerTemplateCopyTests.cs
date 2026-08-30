using System.Reflection;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Copying a template must carry every field.
///
/// The editor used to clone entries with a hand-written initializer that listed four of the eleven
/// properties. It silently dropped the role binding, so every layer showed as receiving nothing — and
/// saving would have written that back, erasing the routing for the whole template. These tests are
/// written against reflection rather than a fixed list so that adding a property without copying it
/// fails here instead of quietly losing data.
/// </summary>
public class LayerTemplateCopyTests
{
    [Fact]
    public void EntryCopy_CarriesEveryProperty()
    {
        var entry = new LayerTemplateEntry();
        var properties = WritableProperties(typeof(LayerTemplateEntry));
        Assert.NotEmpty(properties);

        foreach (PropertyInfo property in properties)
            property.SetValue(entry, DistinctValueFor(property));

        LayerTemplateEntry copy = entry.Copy();

        Assert.NotSame(entry, copy);
        foreach (PropertyInfo property in properties)
        {
            Assert.True(
                Equals(property.GetValue(entry), property.GetValue(copy)),
                $"LayerTemplateEntry.Copy() does not carry '{property.Name}'.");
        }
    }

    [Fact]
    public void DefinitionCopy_CarriesEveryPropertyAndDeepCopiesEntries()
    {
        var template = new LayerTemplateDefinition { Version = 7, Name = "Office" };
        template.Entries.Add(new LayerTemplateEntry { Path = "Drawing::Site", Role = "annotation" });

        LayerTemplateDefinition copy = template.Copy();

        Assert.Equal(7, copy.Version);
        Assert.Equal("Office", copy.Name);
        Assert.NotSame(template.Entries, copy.Entries);
        Assert.NotSame(template.Entries[0], copy.Entries[0]);
        Assert.Equal("annotation", copy.Entries[0].Role);

        // Editing the copy is what the dialog does; it must not reach back into the stored template.
        copy.Entries[0].Role = null;
        Assert.Equal("annotation", template.Entries[0].Role);
    }

    /// <summary>
    /// The property the editor actually lost. Pinned by name as well as by the reflection sweep,
    /// because this is the one whose loss is invisible until output lands on the wrong layer.
    /// </summary>
    [Fact]
    public void ACopiedShippedTemplate_KeepsItsRoleBindings()
    {
        LayerTemplateDefinition shipped = new LayerTemplateStore().GetDefaultTemplates().Single();
        LayerTemplateDefinition copy = shipped.Copy();

        Assert.Equal(
            shipped.Entries.Count(entry => entry.Role != null),
            copy.Entries.Count(entry => entry.Role != null));

        // The strongest form: the copy routes and styles identically.
        Assert.Equal(LayerRoleTable.Build(shipped).Fingerprint, LayerRoleTable.Build(copy).Fingerprint);
    }

    private static PropertyInfo[] WritableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .ToArray();

    /// <summary>A value that differs from the property's default, so a copy that skips it is caught.</summary>
    private static object DistinctValueFor(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string))
            return property.Name + "-value";
        if (type == typeof(int))
            return 4242;
        if (type == typeof(double))
            return 42.5;
        if (type == typeof(bool))
            return true;

        throw new NotSupportedException(
            $"LayerTemplateCopyTests has no sample value for {property.Name} ({property.PropertyType.Name}). "
                + "Add one so the copy of this property is covered.");
    }
}
