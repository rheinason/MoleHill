using System.Collections.Concurrent;
using System.Reflection;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// The unit a card row shows. A row that declares one keeps it; a row that declares none takes it from
/// the definition property it edits, which already says what it is for unit scaling
/// (<see cref="ModelLengthAttribute"/>). So a length field always reads "2.5 m", and the two declarations
/// cannot drift apart: 24 length rows showed a bare number before this, Max Wall Width among them.
/// </summary>
internal static class ParameterUnits
{
    private static readonly ConcurrentDictionary<(Type Definition, string Key), ParameterUnit> Inferred = new();

    public static ParameterUnit Effective<TDefinition>(ParameterDescriptor<TDefinition> parameter, Type definitionType)
        where TDefinition : class =>
        parameter.Unit != ParameterUnit.None
            ? parameter.Unit
            : Inferred.GetOrAdd((definitionType, parameter.Key), static key => Infer(key.Definition, key.Key));

    private static ParameterUnit Infer(Type definitionType, string key)
    {
        PropertyInfo? property = definitionType.GetProperty(
            key,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return property != null && Attribute.IsDefined(property, typeof(ModelLengthAttribute))
            ? ParameterUnit.ModelLength
            : ParameterUnit.None;
    }
}
